using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ArcBox.Core
{
    /// <summary>
    /// An opened archive. All methods must be called from MTA (thread-pool) threads; the 7-Zip COM objects are
    /// not registered so they cannot be marshalled into the STA UI thread.
    /// </summary>
    public sealed class ArchiveReader : IDisposable
    {
        public string FilePath { get; private set; }
        public ArcFormat Format { get; private set; }
        public List<ArchiveEntry> Entries { get; private set; } = new List<ArchiveEntry>();
        public string Password { get; internal set; }
        public bool IsSplitVolume { get; private set; }
        public int VolumeCount { get; private set; } = 1;
        public int CodePage { get; private set; }
        public bool IsSolid { get; private set; }
        public bool HeadersEncrypted { get; private set; }
        public ulong PhysicalSize { get; private set; }
        public string Comment { get; private set; }
        public readonly List<KeyValuePair<string, string>> Info = new List<KeyValuePair<string, string>>();

        internal IInArchive Arc;
        internal Korean.ManagedArchive Managed;
        private Stream _mainStream;
        private InStreamWrapper _mainWrapper;
        private OpenCallback _openCallback;
        private readonly object _lock = new object();

        public string DisplayName => Path.GetFileName(FilePath);
        public bool HasEncrypted => Entries.Any(e => e.Encrypted);
        public bool CanModify => Format != null && Format.CanUpdate && !IsSplitVolume && VolumeCount <= 1
                                 && !Format.Is("gzip") && !Format.Is("bzip2") && !Format.Is("xz");

        private ArchiveReader() { }

        // ------------------------------------------------------------------ open
        public static bool IsSplitFirstVolume(string path, out List<string> volumes)
        {
            volumes = null;
            var m = Regex.Match(path, @"^(.*)\.(\d{3})$");
            if (!m.Success || m.Groups[2].Value != "001") return false;
            volumes = new List<string>();
            for (int i = 1; i < 10000; i++)
            {
                string v = m.Groups[1].Value + "." + i.ToString("000");
                if (!File.Exists(v)) break;
                volumes.Add(v);
            }
            return volumes.Count > 0;
        }

        public static ArchiveReader Open(string path, IOperationUI ui, int codePage = 0, string password = null)
        {
            var lib = SevenZipLib.Instance;
            string full = Path.GetFullPath(path);
            if (!File.Exists(full)) throw new ArchiveException("파일을 찾을 수 없습니다: " + full);

            var r = new ArchiveReader { FilePath = full, CodePage = codePage };
            string nameForDetection = full;
            if (IsSplitFirstVolume(full, out var vols) && vols.Count >= 1)
            {
                var cs = new ConcatReadStream(vols);
                r._mainStream = cs;
                r.IsSplitVolume = true;
                r.VolumeCount = cs.PartCount;
                nameForDetection = full.Substring(0, full.Length - 4);
            }
            else
            {
                r._mainStream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
            }

            try
            {
                var head = new byte[1 << 16];
                r._mainStream.Position = 0;
                int headLen = ReadFull(r._mainStream, head);
                if (!r.IsSplitVolume)
                {
                    // Korean formats (ALZ, EGG) are decoded in managed code
                    string managedPath = Korean.ManagedArchive.FirstVolume(full);
                    if (managedPath != full)
                    {
                        using (var fs = File.OpenRead(managedPath)) headLen = ReadFull(fs, head);
                    }
                    var m = Korean.ManagedArchive.TryOpen(managedPath, head, headLen);
                    if (m != null)
                    {
                        r._mainStream.Dispose();
                        r._mainStream = null;
                        r.Managed = m;
                        m.Password = password;
                        r.Password = password;
                        r.Format = new ArcFormat { Name = m.FormatName };
                        r.Entries = m.Entries;
                        r.IsSolid = m.Solid;
                        r.VolumeCount = m.VolumeCount;
                        r.PhysicalSize = (ulong)m.PhysicalSize;
                        r.LoadManagedInfo();
                        return r;
                    }
                }
                var candidates = lib.OrderCandidates(head, headLen, nameForDetection);
                bool isExe = full.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

                string pwd = password;
                IntPtr maxCheck = Marshal.AllocHGlobal(8);
                Marshal.WriteInt64(maxCheck, 1L << 23);
                try
                {
                    foreach (var fmt in candidates)
                    {
                        while (true)
                        {
                            if (ui != null && ui.IsCancelled) throw new OperationCanceledException();
                            r._mainStream.Position = 0;
                            IInArchive arc;
                            try { arc = lib.CreateInArchive(fmt); }
                            catch { break; }
                            if (codePage > 0 && fmt.Is("zip"))
                            {
                                try { SevenZipLib.SetProperties(arc, new List<KeyValuePair<string, object>> { new KeyValuePair<string, object>("cp", (uint)codePage) }); }
                                catch { }
                            }
                            var cb = new OpenCallback(full, ui) { Password = pwd };
                            var wrapper = new InStreamWrapper(r._mainStream, false);
                            int hr;
                            try { hr = arc.Open(wrapper, isExe ? maxCheck : IntPtr.Zero, cb); }
                            catch (Exception) { hr = HR.E_FAIL; }

                            if (hr == HR.S_OK)
                            {
                                r.Arc = arc;
                                r.Format = fmt;
                                r.Password = cb.Password;
                                r.HeadersEncrypted = cb.PasswordRequested;
                                r._mainWrapper = wrapper;
                                r._openCallback = cb;
                                if (!r.IsSplitVolume && cb.VolumeStreams.Count > 0) r.VolumeCount = 1 + cb.VolumeStreams.Count;
                                r.LoadEntries();
                                r.LoadInfo();
                                return r;
                            }

                            try { arc.Close(); } catch { }
                            try { Marshal.ReleaseComObject(arc); } catch { }
                            cb.DisposeVolumes();

                            if (cb.Cancelled || (ui != null && ui.IsCancelled)) throw new OperationCanceledException();
                            if (cb.PasswordRequested)
                            {
                                // encrypted headers: the password was wrong
                                if (ui == null) throw new ArchiveException("암호가 필요합니다.");
                                pwd = ui.AskPassword(Path.GetFileName(full), true);
                                if (pwd == null) throw new OperationCanceledException();
                                continue;
                            }
                            break;
                        }
                    }
                }
                finally { Marshal.FreeHGlobal(maxCheck); }
                throw new ArchiveException("지원하지 않는 형식이거나 손상된 압축 파일입니다.\n" + full);
            }
            catch
            {
                r._mainStream?.Dispose();
                throw;
            }
        }

        private static int ReadFull(Stream s, byte[] buf)
        {
            int total = 0;
            while (total < buf.Length)
            {
                int n = s.Read(buf, total, buf.Length - total);
                if (n <= 0) break;
                total += n;
            }
            return total;
        }

        private object GetProp(uint index, uint propId, IntPtr pv)
        {
            PropVar.Clear(pv);
            if (Arc.GetProperty(index, propId, pv) != 0) return null;
            return PropVar.Read(pv);
        }

        private object GetArcProp(uint propId, IntPtr pv)
        {
            PropVar.Clear(pv);
            if (Arc.GetArchiveProperty(propId, pv) != 0) return null;
            return PropVar.Read(pv);
        }

        public string DefaultItemName()
        {
            string n = Path.GetFileName(IsSplitVolume ? FilePath.Substring(0, FilePath.Length - 4) : FilePath);
            string ext = Path.GetExtension(n).ToLowerInvariant();
            string stem = Path.GetFileNameWithoutExtension(n);
            if (ext == ".tgz" || ext == ".tbz" || ext == ".tbz2" || ext == ".txz" || ext == ".tzst" || ext == ".taz" || ext == ".tlz")
                return stem + ".tar";
            return string.IsNullOrEmpty(stem) ? "data" : stem;
        }

        /// <summary>gzip stores names in a legacy code page; non-ASCII letters come back as '_' or '?'.</summary>
        private static bool LossyName(string stored, string derived)
        {
            if (stored.Length != derived.Length || stored == derived) return false;
            for (int i = 0; i < stored.Length; i++)
            {
                if (stored[i] == derived[i]) continue;
                if ((stored[i] == '_' || stored[i] == '?') && derived[i] > 127) continue;
                return false;
            }
            return true;
        }

        private void LoadEntries()
        {
            Arc.GetNumberOfItems(out uint count);
            var list = new List<ArchiveEntry>((int)Math.Min(count, 1000000));
            IntPtr pv = PropVar.Alloc();
            try
            {
                for (uint i = 0; i < count; i++)
                {
                    var e = new ArchiveEntry { Index = (int)i };
                    e.Path = PathUtil.NormalizeArchivePath(GetProp(i, Kpid.Path, pv) as string);
                    bool singleStream = count == 1 && (Format.Is("gzip") || Format.Is("bzip2") || Format.Is("xz") || Format.Is("lzma") || Format.Is("Z") || Format.Is("zstd"));
                    if (singleStream && (string.IsNullOrEmpty(e.Path) || e.Path.IndexOf('?') >= 0 || e.Path.IndexOf('\\') >= 0 || LossyName(e.Path, DefaultItemName()))) e.Path = DefaultItemName();
                    if (string.IsNullOrEmpty(e.Path)) e.Path = count == 1 ? DefaultItemName() : DefaultItemName() + "_" + i;
                    e.IsDir = GetProp(i, Kpid.IsDir, pv) is bool d && d;
                    e.Size = PropVar.ToULong(GetProp(i, Kpid.Size, pv)) ?? 0;
                    e.PackedSize = PropVar.ToULong(GetProp(i, Kpid.PackSize, pv));
                    e.MTime = GetProp(i, Kpid.MTime, pv) as DateTime?;
                    e.CTime = GetProp(i, Kpid.CTime, pv) as DateTime?;
                    e.Attributes = (uint)(PropVar.ToULong(GetProp(i, Kpid.Attrib, pv)) ?? 0);
                    if (!e.IsDir && (e.Attributes & 0x10) != 0 && (e.Attributes & 0x8000) == 0 && e.Size == 0) e.IsDir = true;
                    var crc = PropVar.ToULong(GetProp(i, Kpid.CRC, pv));
                    e.Crc = crc.HasValue ? (uint?)(uint)crc.Value : null;
                    e.Encrypted = GetProp(i, Kpid.Encrypted, pv) is bool en && en;
                    e.Method = GetProp(i, Kpid.Method, pv) as string;
                    int slash = e.Path.LastIndexOf('\\');
                    e.Name = slash >= 0 ? e.Path.Substring(slash + 1) : e.Path;
                    list.Add(e);
                }
            }
            finally { PropVar.Free(pv); }
            Entries = list;
        }

        private void LoadInfo()
        {
            IntPtr pv = PropVar.Alloc();
            try
            {
                IsSolid = GetArcProp(Kpid.Solid, pv) is bool s && s;
                PhysicalSize = PropVar.ToULong(GetArcProp(Kpid.PhySize, pv)) ?? (ulong)_mainStream.Length;
                Comment = GetArcProp(Kpid.Comment, pv) as string;
                string method = GetArcProp(Kpid.Method, pv) as string;
                var blocks = PropVar.ToULong(GetArcProp(Kpid.NumBlocks, pv));
                var headers = PropVar.ToULong(GetArcProp(Kpid.HeadersSize, pv));
                var errFlags = PropVar.ToULong(GetArcProp(Kpid.ErrorFlags, pv));
                string warning = GetArcProp(Kpid.Warning, pv) as string;

                ulong unpacked = 0; int files = 0, dirs = 0;
                foreach (var e in Entries) { if (e.IsDir) dirs++; else { files++; unpacked += e.Size; } }

                Info.Add(new KeyValuePair<string, string>("파일", FilePath));
                Info.Add(new KeyValuePair<string, string>("형식", Format.Name.ToUpperInvariant() + (IsSplitVolume || VolumeCount > 1 ? " (분할 " + VolumeCount + "개)" : "")));
                Info.Add(new KeyValuePair<string, string>("파일 / 폴더 수", files + "개 / " + dirs + "개"));
                Info.Add(new KeyValuePair<string, string>("원본 크기", PathUtil.FormatSize(unpacked) + " (" + unpacked.ToString("N0") + " 바이트)"));
                Info.Add(new KeyValuePair<string, string>("압축 크기", PathUtil.FormatSize(PhysicalSize) + " (" + PhysicalSize.ToString("N0") + " 바이트)"));
                if (unpacked > 0) Info.Add(new KeyValuePair<string, string>("압축률", (100.0 * PhysicalSize / unpacked).ToString("0.0") + "%"));
                if (!string.IsNullOrEmpty(method)) Info.Add(new KeyValuePair<string, string>("압축 방식", method));
                else
                {
                    var methods = Entries.Where(e => !string.IsNullOrEmpty(e.Method)).Select(e => e.Method).Distinct().Take(4).ToList();
                    if (methods.Count > 0) Info.Add(new KeyValuePair<string, string>("압축 방식", string.Join(", ", methods)));
                }
                Info.Add(new KeyValuePair<string, string>("솔리드", IsSolid ? "예" : "아니오"));
                if (blocks.HasValue) Info.Add(new KeyValuePair<string, string>("블록 수", blocks.Value.ToString()));
                if (headers.HasValue) Info.Add(new KeyValuePair<string, string>("헤더 크기", PathUtil.FormatSize(headers.Value)));
                Info.Add(new KeyValuePair<string, string>("암호", HasEncrypted ? "있음" : (Password != null ? "있음 (파일 이름 암호화)" : "없음")));
                Info.Add(new KeyValuePair<string, string>("수정 가능", CanModify ? "예" : "아니오"));
                if (!string.IsNullOrEmpty(Comment)) Info.Add(new KeyValuePair<string, string>("주석", Comment));
                if (errFlags.HasValue && errFlags.Value != 0) Info.Add(new KeyValuePair<string, string>("경고", "압축 파일 구조에 문제가 있습니다 (플래그 0x" + errFlags.Value.ToString("X") + ")"));
                if (!string.IsNullOrEmpty(warning)) Info.Add(new KeyValuePair<string, string>("경고", warning));
            }
            finally { PropVar.Free(pv); }
        }

        /// <summary>Names of the top-level items (used for "smart" extraction).</summary>
        public List<string> TopLevelNames()
        {
            return Entries.Select(e => PathUtil.SafeRelative(e.Path).Split('\\')[0])
                          .Where(s => s.Length > 0)
                          .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        // ------------------------------------------------------------------ extract / test
        /// <summary>Extracts entries. plan maps entry index -> absolute target path.</summary>
        public void Extract(IDictionary<int, string> plan, OverwriteMode mode, IOperationUI ui, OpResult res)
        {
            lock (_lock) RunExtract(plan, mode, ui, res, false);
        }

        public void Test(IOperationUI ui, OpResult res)
        {
            var plan = new Dictionary<int, string>();
            foreach (var e in Entries) plan[e.Index] = null;
            lock (_lock) RunExtract(plan, OverwriteMode.Overwrite, ui, res, true);
        }

        /// <summary>Reads one entry into memory (used by the image preview). Uses a private temp folder that is
        /// removed immediately, so nothing appears in the user's folders.</summary>
        public byte[] ReadEntryBytes(ArchiveEntry e, IOperationUI ui)
        {
            string dir = Path.Combine(Path.GetTempPath(), "ArcBox", "pv-" + Guid.NewGuid().ToString("N").Substring(0, 10));
            try
            {
                Directory.CreateDirectory(dir);
                string target = Path.Combine(dir, "data");
                var res = new OpResult();
                Extract(new Dictionary<int, string> { { e.Index, target } }, OverwriteMode.Overwrite, ui, res);
                if (res.Cancelled) return null;
                if (res.HasErrors) throw new ArchiveException(string.Join("\n", res.Errors.Take(3)));
                return File.ReadAllBytes(target);
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        /// <summary>Reads several entries in one extraction pass (thumbnails). Entries that fail are left out.</summary>
        public Dictionary<int, byte[]> ReadEntriesBytes(IList<ArchiveEntry> entries, IOperationUI ui)
        {
            var result = new Dictionary<int, byte[]>();
            string dir = Path.Combine(Path.GetTempPath(), "ArcBox", "th-" + Guid.NewGuid().ToString("N").Substring(0, 10));
            try
            {
                Directory.CreateDirectory(dir);
                var plan = new Dictionary<int, string>();
                foreach (var e in entries) plan[e.Index] = Path.Combine(dir, e.Index.ToString());
                Extract(plan, OverwriteMode.Overwrite, ui, new OpResult());
                foreach (var kv in plan)
                {
                    try { if (File.Exists(kv.Value)) result[kv.Key] = File.ReadAllBytes(kv.Value); } catch { }
                }
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
            return result;
        }

        /// <summary>Extracts one entry into a stream (used for previews / temp open).</summary>
        public Dictionary<int, string> PlanFor(IEnumerable<ArchiveEntry> entries, string destDir, string stripPrefix)
        {
            var plan = new Dictionary<int, string>();
            string root = Path.GetFullPath(destDir);
            stripPrefix = PathUtil.NormalizeArchivePath(stripPrefix);
            foreach (var e in entries)
            {
                string p = e.Path;
                if (stripPrefix.Length > 0)
                {
                    if (p.StartsWith(stripPrefix + "\\", StringComparison.OrdinalIgnoreCase)) p = p.Substring(stripPrefix.Length + 1);
                    else if (string.Equals(p, stripPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                }
                string rel = PathUtil.SafeRelative(p);
                if (rel.Length == 0) continue;
                string target = Path.Combine(root, rel);
                if (!PathUtil.IsUnder(target, root)) continue;
                plan[e.Index] = target;
            }
            return plan;
        }

        private void LoadManagedInfo()
        {
            ulong unpacked = 0; int files = 0, dirs = 0;
            foreach (var e in Entries) { if (e.IsDir) dirs++; else { files++; unpacked += e.Size; } }
            Info.Add(new KeyValuePair<string, string>("파일", FilePath));
            Info.Add(new KeyValuePair<string, string>("형식", Format.Name + (VolumeCount > 1 ? " (분할 " + VolumeCount + "개)" : "")));
            Info.Add(new KeyValuePair<string, string>("파일 / 폴더 수", files + "개 / " + dirs + "개"));
            Info.Add(new KeyValuePair<string, string>("원본 크기", PathUtil.FormatSize(unpacked) + " (" + unpacked.ToString("N0") + " 바이트)"));
            Info.Add(new KeyValuePair<string, string>("압축 크기", PathUtil.FormatSize(PhysicalSize) + " (" + PhysicalSize.ToString("N0") + " 바이트)"));
            if (unpacked > 0) Info.Add(new KeyValuePair<string, string>("압축률", (100.0 * PhysicalSize / unpacked).ToString("0.0") + "%"));
            var methods = Entries.Where(e => !string.IsNullOrEmpty(e.Method)).Select(e => e.Method).Distinct().Take(4).ToList();
            if (methods.Count > 0) Info.Add(new KeyValuePair<string, string>("압축 방식", string.Join(", ", methods)));
            Info.Add(new KeyValuePair<string, string>("솔리드", IsSolid ? "예" : "아니오"));
            Info.Add(new KeyValuePair<string, string>("암호", HasEncrypted ? "있음" : "없음"));
            Info.Add(new KeyValuePair<string, string>("수정 가능", "아니오 (읽기 전용 형식)"));
        }

        private void RunManagedExtract(IDictionary<int, string> plan, OverwriteMode mode, IOperationUI ui, OpResult res, bool test)
        {
            var created = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var dirTimes = new List<KeyValuePair<string, DateTime>>();
            var open = new Dictionary<int, KeyValuePair<FileStream, string>>();
            var ignore = new HashSet<int>();
            ulong totalSize = 0;
            foreach (var i in plan.Keys) if (i >= 0 && i < Entries.Count) totalSize += Entries[i].Size;
            ui?.SetTotal(totalSize);
            long done = 0;
            Managed.Password = Password;

            var ctx = new Korean.ManagedExtractContext
            {
                AskPassword = retry => ui?.AskPassword(DisplayName, retry),
                Progress = n =>
                {
                    if (ui == null) return;
                    if (ui.IsCancelled) throw new OperationCanceledException();
                    ui.WaitIfPaused();
                    done += n;
                    ui.SetCompleted((ulong)done);
                },
                Open = idx =>
                {
                    if (ui != null && ui.IsCancelled) throw new OperationCanceledException();
                    if (!plan.TryGetValue(idx, out string target)) { ignore.Add(idx); return null; }
                    var e = Entries[idx];
                    ui?.SetCurrentFile(e.Path);
                    if (test || target == null) return Stream.Null;
                    try
                    {
                        if (e.IsDir)
                        {
                            Directory.CreateDirectory(target);
                            if (e.MTime.HasValue) dirTimes.Add(new KeyValuePair<string, DateTime>(target, e.MTime.Value));
                            ignore.Add(idx);
                            return null;
                        }
                        string dir = Path.GetDirectoryName(target);
                        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                        if (Directory.Exists(target)) target = PathUtil.UniqueFile(target);
                        else if (File.Exists(target) && !created.Contains(target))
                        {
                            OverwriteAction act;
                            switch (mode)
                            {
                                case OverwriteMode.Overwrite: act = OverwriteAction.Overwrite; break;
                                case OverwriteMode.Skip: act = OverwriteAction.Skip; break;
                                case OverwriteMode.Rename: act = OverwriteAction.Rename; break;
                                default: act = ui != null ? ui.AskOverwrite(target, e) : OverwriteAction.Skip; break;
                            }
                            if (act == OverwriteAction.Cancel) throw new OperationCanceledException();
                            if (act == OverwriteAction.Skip) { res.FilesSkipped++; ignore.Add(idx); return null; }
                            if (act == OverwriteAction.Rename) target = PathUtil.UniqueFile(target);
                            else { try { File.SetAttributes(target, FileAttributes.Normal); } catch { } }
                        }
                        else if (File.Exists(target)) { try { File.SetAttributes(target, FileAttributes.Normal); } catch { } }
                        var fs = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 18);
                        created.Add(target);
                        open[idx] = new KeyValuePair<FileStream, string>(fs, target);
                        return fs;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        res.Errors.Add(e.Path + ": " + DescribeIo(ex));
                        ignore.Add(idx);
                        return null;
                    }
                },
                Done = (idx, err, pwd) =>
                {
                    if (ignore.Remove(idx)) return;
                    var e = Entries[idx];
                    string path = null;
                    if (open.TryGetValue(idx, out var kv))
                    {
                        open.Remove(idx);
                        path = kv.Value;
                        try { kv.Key.Dispose(); } catch (Exception ex) { if (err == null) err = DescribeIo(ex); }
                    }
                    if (err == null)
                    {
                        if (!e.IsDir) res.FilesProcessed++;
                        if (path != null)
                        {
                            res.OutputPaths.Add(path);
                            try
                            {
                                if (e.MTime.HasValue) File.SetLastWriteTimeUtc(path, e.MTime.Value);
                                uint a = e.Attributes & 0x07;
                                if (a != 0) File.SetAttributes(path, File.GetAttributes(path) | (FileAttributes)a);
                            }
                            catch { }
                        }
                    }
                    else
                    {
                        if (pwd) res.PasswordProblem = true;
                        res.Errors.Add(e.Path + ": " + err);
                        if (path != null) { try { File.Delete(path); } catch { } }
                    }
                }
            };

            try { Managed.Extract(new HashSet<int>(plan.Keys), ctx); }
            catch (OperationCanceledException) { res.Cancelled = true; }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException || ex is ArchiveException)
            {
                res.Errors.Add(DescribeIo(ex));
            }
            finally
            {
                foreach (var kv in open.Values)
                {
                    try { kv.Key.Dispose(); } catch { }
                    try { File.Delete(kv.Value); } catch { }
                }
                foreach (var kv in dirTimes.OrderByDescending(k => k.Key.Length))
                {
                    try { Directory.SetLastWriteTimeUtc(kv.Key, kv.Value); } catch { }
                }
                if (Managed.Password != null) Password = Managed.Password;
            }
        }

        private void RunExtract(IDictionary<int, string> plan, OverwriteMode mode, IOperationUI ui, OpResult res, bool test)
        {
            if (plan.Count == 0) return;
            if (Managed != null) { RunManagedExtract(plan, mode, ui, res, test); return; }
            var pending = new Dictionary<int, string>(plan);
            var created = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            ulong totalSize = 0;
            foreach (var i in pending.Keys) if (i >= 0 && i < Entries.Count) totalSize += Entries[i].Size;
            ui?.SetTotal(totalSize);

            for (int attempt = 0; attempt < 50; attempt++)
            {
                var cb = new ExtractCallback(this, pending, ui, mode, test, created);
                uint[] idx = pending.Keys.Select(i => (uint)i).OrderBy(x => x).ToArray();
                int hr;
                try { hr = Arc.Extract(idx, (uint)idx.Length, test ? 1 : 0, cb); }
                catch (Exception ex) { hr = HR.E_FAIL; res.Errors.Add("압축 해제 엔진 오류: " + ex.Message); }
                cb.Finish();
                res.FilesProcessed += cb.FilesDone;
                res.FilesSkipped += cb.FilesSkipped;
                res.OutputPaths.AddRange(cb.Outputs);

                if (cb.UserCancelled || (ui != null && ui.IsCancelled)) { res.Cancelled = true; res.Errors.AddRange(cb.Errors); return; }
                if (cb.WriteError != null)
                {
                    res.Errors.Add(DescribeIo(cb.WriteError));
                    res.Errors.AddRange(cb.Errors);
                    return;
                }
                if (hr != HR.S_OK && hr != HR.E_ABORT)
                    res.Errors.Add("압축 해제 중 오류가 발생했습니다 (0x" + hr.ToString("X8") + ")");

                if (cb.PasswordFailures.Count > 0 && ui != null)
                {
                    string p = ui.AskPassword(DisplayName, true);
                    if (p == null)
                    {
                        res.PasswordProblem = true;
                        foreach (var i in cb.PasswordFailures) res.Errors.Add(Entries[i].Path + ": 암호가 틀렸습니다");
                        res.Errors.AddRange(cb.Errors);
                        return;
                    }
                    Password = p;
                    var retry = new Dictionary<int, string>();
                    foreach (var i in cb.PasswordFailures) retry[i] = pending[i];
                    res.Errors.AddRange(cb.Errors);
                    pending = retry;
                    continue;
                }
                res.Errors.AddRange(cb.Errors);
                return;
            }
        }

        internal static string DescribeIo(Exception ex)
        {
            if (ex is IOException io)
            {
                int code = Marshal.GetHRForException(io) & 0xFFFF;
                if (code == 112 || code == 39) return "디스크 공간이 부족합니다.";
                return "파일 쓰기 오류: " + io.Message;
            }
            if (ex is UnauthorizedAccessException) return "접근 권한이 없습니다: " + ex.Message;
            return ex.Message;
        }

        // ------------------------------------------------------------------ lifetime
        public void Close()
        {
            if (Managed != null) { Managed.Dispose(); Managed = null; return; }
            if (Arc == null) return;
            if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
            {
                Task.Run(() => Close()).Wait();
                return;
            }
            lock (_lock)
            {
                try { Arc.Close(); } catch { }
                try { Marshal.ReleaseComObject(Arc); } catch { }
                Arc = null;
                _openCallback?.DisposeVolumes();
                _mainWrapper?.Dispose();
                _mainStream?.Dispose();
            }
        }

        public void Dispose() => Close();
    }

    // ====================================================================== callbacks
    [ComVisible(true)]
    internal sealed class OpenCallback : IArchiveOpenCallback, IArchiveOpenVolumeCallback, ICryptoGetTextPassword
    {
        private readonly string _dir;
        private string _currentName;
        private long _currentSize;
        private readonly IOperationUI _ui;
        public string Password;
        public bool PasswordRequested;
        public bool Cancelled;
        public readonly List<Stream> VolumeStreams = new List<Stream>();
        private readonly List<InStreamWrapper> _wrappers = new List<InStreamWrapper>();

        public OpenCallback(string path, IOperationUI ui)
        {
            _dir = Path.GetDirectoryName(path);
            _currentName = Path.GetFileName(path);
            try { _currentSize = new FileInfo(path).Length; } catch { }
            _ui = ui;
        }

        public int SetTotal(IntPtr files, IntPtr bytes) => HR.S_OK;

        public int SetCompleted(IntPtr files, IntPtr bytes)
        {
            if (_ui != null && _ui.IsCancelled) { Cancelled = true; return HR.E_ABORT; }
            return HR.S_OK;
        }

        public int GetProperty(uint propID, IntPtr value)
        {
            try
            {
                PropVar.WriteEmpty(value);
                if (propID == Kpid.Name) PropVar.WriteString(value, _currentName);
                else if (propID == Kpid.IsDir) PropVar.WriteBool(value, false);
                else if (propID == Kpid.Size) PropVar.WriteUInt64(value, (ulong)_currentSize);
                return HR.S_OK;
            }
            catch { return HR.E_FAIL; }
        }

        public int GetStream(string name, out IInStream inStream)
        {
            inStream = null;
            try
            {
                string full = Path.IsPathRooted(name) ? name : Path.Combine(_dir, name);
                if (!File.Exists(full)) return HR.S_FALSE;
                var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
                VolumeStreams.Add(fs);
                var w = new InStreamWrapper(fs, false);
                _wrappers.Add(w);
                _currentName = Path.GetFileName(full);
                _currentSize = fs.Length;
                inStream = w;
                return HR.S_OK;
            }
            catch { return HR.S_FALSE; }
        }

        public int CryptoGetTextPassword(out string password)
        {
            PasswordRequested = true;
            if (Password == null)
            {
                Password = _ui?.AskPassword(_currentName, false);
                if (Password == null) { Cancelled = true; password = ""; return HR.E_ABORT; }
            }
            password = Password;
            return HR.S_OK;
        }

        public void DisposeVolumes()
        {
            foreach (var w in _wrappers) w.Dispose();
            foreach (var s in VolumeStreams) s.Dispose();
            _wrappers.Clear();
            VolumeStreams.Clear();
        }
    }

    [ComVisible(true)]
    internal sealed class ExtractCallback : IArchiveExtractCallback, ICryptoGetTextPassword
    {
        private readonly ArchiveReader _r;
        private readonly IDictionary<int, string> _plan;
        private readonly IOperationUI _ui;
        private readonly bool _test;
        private readonly HashSet<string> _created;
        private OverwriteMode _mode;

        private int _cur = -1;
        private string _curPath;
        private FileStream _curFile;
        private OutStreamWrapper _curWrapper;
        private readonly List<KeyValuePair<string, DateTime>> _dirTimes = new List<KeyValuePair<string, DateTime>>();

        public readonly List<string> Errors = new List<string>();
        public readonly List<int> PasswordFailures = new List<int>();
        public readonly List<string> Outputs = new List<string>();
        public int FilesDone, FilesSkipped;
        public bool UserCancelled;
        public Exception WriteError;

        public ExtractCallback(ArchiveReader r, IDictionary<int, string> plan, IOperationUI ui, OverwriteMode mode, bool test, HashSet<string> created)
        {
            _r = r; _plan = plan; _ui = ui; _mode = mode; _test = test; _created = created;
        }

        public int SetTotal(ulong total) => HR.S_OK;

        public int SetCompleted(IntPtr completeValue)
        {
            if (_ui == null) return HR.S_OK;
            if (_ui.IsCancelled) { UserCancelled = true; return HR.E_ABORT; }
            _ui.WaitIfPaused();
            if (completeValue != IntPtr.Zero) _ui.SetCompleted((ulong)Marshal.ReadInt64(completeValue));
            return _ui.IsCancelled ? HR.E_ABORT : HR.S_OK;
        }

        public int GetStream(uint index, out ISequentialOutStream outStream, int askExtractMode)
        {
            outStream = null;
            _cur = (int)index;
            _curPath = null;
            if (_ui != null && _ui.IsCancelled) { UserCancelled = true; return HR.E_ABORT; }
            if (askExtractMode != AskMode.Extract || _test) return HR.S_OK;
            if (!_plan.TryGetValue((int)index, out string target) || target == null) return HR.S_OK;
            var e = _r.Entries[(int)index];
            _ui?.SetCurrentFile(e.Path);
            try
            {
                if (e.IsDir)
                {
                    Directory.CreateDirectory(target);
                    if (e.MTime.HasValue) _dirTimes.Add(new KeyValuePair<string, DateTime>(target, e.MTime.Value));
                    return HR.S_OK;
                }
                string dir = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                if (Directory.Exists(target))
                {
                    target = PathUtil.UniqueFile(target);
                }
                else if (File.Exists(target) && !_created.Contains(target))
                {
                    OverwriteAction act;
                    switch (_mode)
                    {
                        case OverwriteMode.Overwrite: act = OverwriteAction.Overwrite; break;
                        case OverwriteMode.Skip: act = OverwriteAction.Skip; break;
                        case OverwriteMode.Rename: act = OverwriteAction.Rename; break;
                        default: act = _ui != null ? _ui.AskOverwrite(target, e) : OverwriteAction.Skip; break;
                    }
                    if (act == OverwriteAction.Cancel) { UserCancelled = true; return HR.E_ABORT; }
                    if (act == OverwriteAction.Skip) { FilesSkipped++; return HR.S_OK; }
                    if (act == OverwriteAction.Rename) target = PathUtil.UniqueFile(target);
                    else
                    {
                        try { File.SetAttributes(target, FileAttributes.Normal); } catch { }
                    }
                }
                else if (File.Exists(target))
                {
                    try { File.SetAttributes(target, FileAttributes.Normal); } catch { }
                }

                _curFile = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 18);
                _curPath = target;
                _created.Add(target);
                _curWrapper = new OutStreamWrapper(_curFile);
                outStream = _curWrapper;
                return HR.S_OK;
            }
            catch (Exception ex)
            {
                Errors.Add(e.Path + ": " + ArchiveReader.DescribeIo(ex));
                return HR.S_OK; // skip this item, continue with the rest
            }
        }

        public int PrepareOperation(int askExtractMode) => HR.S_OK;

        public int SetOperationResult(int opRes)
        {
            var wrapper = _curWrapper;
            string path = _curPath;
            _curWrapper = null; _curFile = null; _curPath = null;
            if (wrapper?.Error != null && WriteError == null) WriteError = wrapper.Error;
            wrapper?.Dispose();

            if (_cur < 0 || _cur >= _r.Entries.Count) return HR.S_OK;
            var e = _r.Entries[_cur];
            if (opRes == OpRes.OK)
            {
                if (!e.IsDir) FilesDone++;
                if (path != null)
                {
                    Outputs.Add(path);
                    try
                    {
                        if (e.MTime.HasValue) File.SetLastWriteTimeUtc(path, e.MTime.Value);
                        if (e.CTime.HasValue) File.SetCreationTimeUtc(path, e.CTime.Value);
                        uint a = e.Attributes & 0x07; // readonly, hidden, system
                        if (a != 0) File.SetAttributes(path, File.GetAttributes(path) | (FileAttributes)a);
                    }
                    catch { }
                }
            }
            else
            {
                bool pwdErr = opRes == OpRes.WrongPassword ||
                              (e.Encrypted && (opRes == OpRes.DataError || opRes == OpRes.CRCError));
                if (pwdErr && !PasswordFailures.Contains(_cur)) PasswordFailures.Add(_cur);
                else Errors.Add(e.Path + ": " + OpRes.Describe(opRes, e.Encrypted));
                if (path != null && pwdErr)
                {
                    try { File.Delete(path); } catch { }
                }
            }
            return HR.S_OK;
        }

        public int CryptoGetTextPassword(out string password)
        {
            if (_r.Password == null)
            {
                string p = _ui?.AskPassword(_r.DisplayName, false);
                if (p == null) { UserCancelled = true; password = ""; return HR.E_ABORT; }
                _r.Password = p;
            }
            password = _r.Password;
            return HR.S_OK;
        }

        public void Finish()
        {
            if (_curWrapper != null) { _curWrapper.Dispose(); _curWrapper = null; }
            foreach (var kv in _dirTimes.OrderByDescending(k => k.Key.Length))
            {
                try { Directory.SetLastWriteTimeUtc(kv.Key, kv.Value); } catch { }
            }
        }
    }
}
