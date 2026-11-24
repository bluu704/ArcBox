using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace ArcBox.Core
{
    public sealed class NewItem
    {
        public string ArchivePath;
        public string DiskPath;     // null for directories created only inside the archive
        public bool IsDir;
        public ulong Size;
        public DateTime MTime = DateTime.UtcNow, CTime = DateTime.UtcNow, ATime = DateTime.UtcNow;
        public uint Attributes;
    }

    public sealed class ModifyRequest
    {
        public readonly HashSet<int> Delete = new HashSet<int>();
        public readonly Dictionary<int, string> Rename = new Dictionary<int, string>();
        public readonly List<NewItem> Add = new List<NewItem>();
        public bool IsEmpty => Delete.Count == 0 && Rename.Count == 0 && Add.Count == 0;
    }

    public static class ArchiveWriter
    {
        // ---------------------------------------------------------------- scanning
        public static List<NewItem> Scan(IEnumerable<string> sources, string baseInArchive, OpResult res, IOperationUI ui, string excludePath = null)
        {
            var items = new List<NewItem>();
            string prefix = PathUtil.NormalizeArchivePath(baseInArchive);
            if (prefix.Length > 0) prefix += "\\";
            string exclude = excludePath == null ? null : Path.GetFullPath(excludePath);

            foreach (var raw in sources)
            {
                if (ui != null && ui.IsCancelled) break;
                string src = raw.TrimEnd('\\', '/');
                if (src.Length == 2 && src[1] == ':') src += "\\";
                string full = Path.GetFullPath(src);
                string name = Path.GetFileName(full.TrimEnd('\\'));
                if (string.IsNullOrEmpty(name)) name = full.Substring(0, 1);
                if (File.Exists(full))
                {
                    if (exclude != null && string.Equals(full, exclude, StringComparison.OrdinalIgnoreCase)) continue;
                    var it = FileItem(full, prefix + name, res);
                    if (it != null) items.Add(it);
                }
                else if (Directory.Exists(full)) AddDirectory(full, prefix + name, items, res, ui, exclude);
                else res.Errors.Add("찾을 수 없음: " + full);
            }
            return items;
        }

        private static NewItem FileItem(string full, string archivePath, OpResult res)
        {
            try
            {
                var fi = new FileInfo(full);
                // make sure we can read it (locked files are reported instead of breaking the archive)
                using (new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1)) { }
                return new NewItem
                {
                    ArchivePath = archivePath, DiskPath = full, Size = (ulong)fi.Length,
                    MTime = fi.LastWriteTimeUtc, CTime = fi.CreationTimeUtc, ATime = fi.LastAccessTimeUtc,
                    Attributes = (uint)fi.Attributes & 0x37
                };
            }
            catch (Exception ex)
            {
                res.Warnings.Add("건너뜀 (열 수 없음): " + full + " - " + ex.Message);
                return null;
            }
        }

        private static void AddDirectory(string root, string archiveRoot, List<NewItem> items, OpResult res, IOperationUI ui, string exclude)
        {
            var stack = new Stack<KeyValuePair<string, string>>();
            stack.Push(new KeyValuePair<string, string>(root, archiveRoot));
            while (stack.Count > 0)
            {
                if (ui != null && ui.IsCancelled) return;
                var cur = stack.Pop();
                DirectoryInfo di;
                try
                {
                    di = new DirectoryInfo(cur.Key);
                    items.Add(new NewItem
                    {
                        ArchivePath = cur.Value, IsDir = true, MTime = di.LastWriteTimeUtc, CTime = di.CreationTimeUtc,
                        ATime = di.LastAccessTimeUtc, Attributes = ((uint)di.Attributes & 0x37) | 0x10
                    });
                    if ((di.Attributes & FileAttributes.ReparsePoint) != 0) continue; // don't follow junctions/symlinks
                }
                catch (Exception ex) { res.Warnings.Add("건너뜀: " + cur.Key + " - " + ex.Message); continue; }

                try
                {
                    foreach (var f in di.GetFiles())
                    {
                        if (exclude != null && string.Equals(f.FullName, exclude, StringComparison.OrdinalIgnoreCase)) continue;
                        var it = FileItem(f.FullName, cur.Value + "\\" + f.Name, res);
                        if (it != null) items.Add(it);
                    }
                    var subs = di.GetDirectories();
                    for (int i = subs.Length - 1; i >= 0; i--)
                        stack.Push(new KeyValuePair<string, string>(subs[i].FullName, cur.Value + "\\" + subs[i].Name));
                }
                catch (Exception ex) { res.Warnings.Add("폴더를 읽을 수 없음: " + cur.Key + " - " + ex.Message); }
            }
        }

        // ---------------------------------------------------------------- properties
        internal static List<KeyValuePair<string, object>> BuildProps(CompressOptions o, string engine, bool headersEncrypted = false)
        {
            var p = new List<KeyValuePair<string, object>>();
            void Add(string k, object v) => p.Add(new KeyValuePair<string, object>(k, v));
            int level = Math.Max(0, Math.Min(9, o.Level));
            string mt = o.Threads > 0 ? o.Threads.ToString() : "on";
            bool hasPwd = !string.IsNullOrEmpty(o.Password);
            switch (engine)
            {
                case "7z":
                    Add("x", (uint)level);
                    if (level > 0 && !string.IsNullOrEmpty(o.Method)) Add("0", o.Method);
                    Add("s", o.Solid ? "on" : "off");
                    Add("mt", mt);
                    if (hasPwd && (o.EncryptFileNames || headersEncrypted)) Add("he", "on");
                    break;
                case "zip":
                    Add("x", (uint)level);
                    if (level > 0 && !string.IsNullOrEmpty(o.Method)) Add("m", o.Method);
                    Add("mt", mt);
                    if (hasPwd) Add("em", string.IsNullOrEmpty(o.ZipEncryption) ? "AES256" : o.ZipEncryption);
                    if (o.Utf8Names) Add("cp", 65001u);
                    break;
                case "gzip":
                    Add("x", (uint)Math.Max(1, level));
                    break;
                case "bzip2":
                case "xz":
                    Add("x", (uint)Math.Max(1, level));
                    Add("mt", mt);
                    break;
            }
            return p;
        }

        // ---------------------------------------------------------------- create
        public static void Create(string archivePath, List<NewItem> items, CompressOptions o, IOperationUI ui, OpResult res)
        {
            archivePath = Path.GetFullPath(archivePath);
            if (items.Count == 0) { res.Errors.Add("압축할 파일이 없습니다."); return; }
            string f = o.Format;

            if (CompressOptions.IsTarCombo(f))
            {
                string dir = Path.GetDirectoryName(archivePath);
                string tmpTar = Path.Combine(dir, "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".arcbox.tar");
                try
                {
                    ui?.SetStage("1/2 단계: TAR로 묶는 중");
                    var tarOpt = o.Clone(); tarOpt.VolumeSize = 0; tarOpt.Password = null;
                    CreateCore(tmpTar, items, tarOpt, "tar", ui, res, false);
                    if (res.Cancelled || res.HasErrors) return;
                    var fi = new FileInfo(tmpTar);
                    try { fi.Attributes |= FileAttributes.Hidden; } catch { }
                    var tarItem = new NewItem
                    {
                        ArchivePath = PathUtil.ArchiveBaseName(archivePath) + ".tar", DiskPath = tmpTar, Size = (ulong)fi.Length,
                        MTime = DateTime.UtcNow, CTime = DateTime.UtcNow, ATime = DateTime.UtcNow
                    };
                    ui?.SetStage("2/2 단계: 압축하는 중");
                    var r2 = new OpResult();
                    CreateCore(archivePath, new List<NewItem> { tarItem }, o, CompressOptions.EngineFormat(f), ui, r2, true);
                    res.Errors.AddRange(r2.Errors); res.Cancelled |= r2.Cancelled; res.OutputPaths.AddRange(r2.OutputPaths);
                }
                finally
                {
                    try { if (File.Exists(tmpTar)) { File.SetAttributes(tmpTar, FileAttributes.Normal); File.Delete(tmpTar); } } catch { }
                }
                return;
            }

            if (CompressOptions.IsSingleStream(f))
            {
                var files = items.Where(i => !i.IsDir).ToList();
                if (files.Count != 1 || items.Count != 1)
                {
                    res.Errors.Add(f.ToUpperInvariant() + " 형식은 파일 하나만 압축할 수 있습니다. TAR." + f.ToUpperInvariant() + " 형식을 사용하세요.");
                    return;
                }
            }
            CreateCore(archivePath, items, o, CompressOptions.EngineFormat(f), ui, res, true);
        }

        private static void CreateCore(string archivePath, List<NewItem> items, CompressOptions o, string engine, IOperationUI ui, OpResult res, bool countFiles)
        {
            var lib = SevenZipLib.Instance;
            var fmt = lib.FindFormat(engine) ?? throw new ArchiveException(engine + " 형식을 지원하지 않습니다.");
            var outArc = lib.CreateOutArchive(fmt);
            string tmp = null;
            VolumeWriteStream vol = null;
            Stream target = null;
            bool ok = false;
            try
            {
                SevenZipLib.SetProperties(outArc, BuildProps(o, engine));
                Directory.CreateDirectory(Path.GetDirectoryName(archivePath));
                if (o.VolumeSize > 0) { vol = new VolumeWriteStream(archivePath, o.VolumeSize); target = vol; }
                else
                {
                    tmp = Path.Combine(Path.GetDirectoryName(archivePath), "." + Path.GetFileName(archivePath) + "." + Guid.NewGuid().ToString("N").Substring(0, 6) + ".tmp");
                    target = new FileStream(tmp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 20);
                    try { File.SetAttributes(tmp, FileAttributes.Hidden); } catch { }
                }

                ulong total = 0;
                foreach (var i in items) total += i.Size;
                ui?.SetTotal(total);

                var slots = items.Select(i => new UpdateSlot { Item = i }).ToList();
                var cb = new UpdateCallback(slots, null, string.IsNullOrEmpty(o.Password) ? null : o.Password, null, ui);
                var ow = new OutStreamWrapper(target);
                int hr;
                try { hr = outArc.UpdateItems(ow, (uint)slots.Count, cb); }
                catch (Exception ex) { hr = HR.E_FAIL; res.Errors.Add("압축 엔진 오류: " + ex.Message); }
                finally { cb.CloseAll(); target.Flush(); }

                if (cb.UserCancelled || (ui != null && ui.IsCancelled)) { res.Cancelled = true; return; }
                if (ow.Error != null) { res.Errors.Add(ArchiveReader.DescribeIo(ow.Error)); return; }
                if (cb.ReadError != null) { res.Errors.Add(cb.ReadError); return; }
                if (hr != HR.S_OK) { res.Errors.Add("압축에 실패했습니다 (0x" + hr.ToString("X8") + ")"); return; }

                target.Dispose(); target = null;
                if (tmp != null)
                {
                    if (File.Exists(archivePath)) { File.SetAttributes(archivePath, FileAttributes.Normal); File.Delete(archivePath); }
                    File.Move(tmp, archivePath);
                    try { File.SetAttributes(archivePath, FileAttributes.Normal); } catch { }
                    tmp = null;
                    res.OutputPaths.Add(archivePath);
                }
                else
                {
                    res.OutputPaths.AddRange(vol.CreatedFiles);
                    // remove stale volumes from an earlier, larger archive with the same name
                    for (int i = vol.CreatedFiles.Count; ; i++)
                    {
                        string stale = VolumeWriteStream.VolumeName(archivePath, i);
                        if (!File.Exists(stale)) break;
                        try { File.Delete(stale); } catch { }
                    }
                }
                if (countFiles) res.FilesProcessed += items.Count(i => !i.IsDir);
                ok = true;
            }
            finally
            {
                try { target?.Dispose(); } catch { }
                if (!ok)
                {
                    if (tmp != null) try { File.Delete(tmp); } catch { }
                    if (vol != null) foreach (var v in vol.CreatedFiles) try { File.Delete(v); } catch { }
                }
                try { Marshal.ReleaseComObject(outArc); } catch { }
            }
        }

        // ---------------------------------------------------------------- modify
        /// <summary>Applies delete/rename/add to an opened archive. The archive is rewritten to a temporary file
        /// and swapped in only on success. The reader is closed afterwards; reopen it to see the result.</summary>
        public static void Modify(ArchiveReader r, ModifyRequest req, CompressOptions o, IOperationUI ui, OpResult res)
        {
            if (!r.CanModify) throw new ArchiveException("이 형식(" + r.Format.Name.ToUpperInvariant() + ")은 수정할 수 없습니다.");
            if (req.IsEmpty) return;
            o = o?.Clone() ?? new CompressOptions();
            string engine = r.Format.Name.ToLowerInvariant();
            if (string.IsNullOrEmpty(o.Password)) o.Password = r.Password;
            if (engine == "7z") o.Solid = r.IsSolid || r.Entries.Count == 0;

            var addPaths = new Dictionary<string, NewItem>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in req.Add) addPaths[a.ArchivePath] = a;
            var add = new List<NewItem>(req.Add);
            var slots = new List<UpdateSlot>();
            foreach (var e in r.Entries)
            {
                if (req.Delete.Contains(e.Index)) continue;
                if (addPaths.TryGetValue(e.Path, out var na))
                {
                    if (e.IsDir && na.IsDir) { add.Remove(na); }   // keep the existing folder entry
                    else continue;                                // replaced by the new file
                }
                req.Rename.TryGetValue(e.Index, out string newName);
                slots.Add(new UpdateSlot { ExistingIndex = e.Index, NewPath = newName });
            }
            foreach (var a in add) slots.Add(new UpdateSlot { Item = a });

            string original = r.FilePath;
            string tmp = Path.Combine(Path.GetDirectoryName(original), "." + Path.GetFileName(original) + "." + Guid.NewGuid().ToString("N").Substring(0, 6) + ".tmp");
            bool ok = false;
            var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 20);
            try
            {
                try { File.SetAttributes(tmp, FileAttributes.Hidden); } catch { }
                var outArc = (IOutArchive)r.Arc;
                SevenZipLib.SetProperties(r.Arc, BuildProps(o, engine, r.HeadersEncrypted));
                ulong total = 0;
                foreach (var a in add) total += a.Size;
                ui?.SetTotal(total);
                var cb = new UpdateCallback(slots, r, string.IsNullOrEmpty(o.Password) ? null : o.Password, r.Password, ui);
                var ow = new OutStreamWrapper(fs);
                int hr;
                try { hr = outArc.UpdateItems(ow, (uint)slots.Count, cb); }
                catch (Exception ex) { hr = HR.E_FAIL; res.Errors.Add("압축 엔진 오류: " + ex.Message); }
                finally { cb.CloseAll(); }

                if (cb.UserCancelled || (ui != null && ui.IsCancelled)) { res.Cancelled = true; return; }
                if (ow.Error != null) { res.Errors.Add(ArchiveReader.DescribeIo(ow.Error)); return; }
                if (cb.ReadError != null) { res.Errors.Add(cb.ReadError); return; }
                if (hr != HR.S_OK) { res.Errors.Add("압축 파일을 수정하지 못했습니다 (0x" + hr.ToString("X8") + "). 원본은 그대로 유지됩니다."); return; }
                fs.Dispose();
                r.Close();
                var attrs = File.GetAttributes(original);
                File.SetAttributes(original, FileAttributes.Normal);
                try { File.Replace(tmp, original, null, true); }
                catch
                {
                    File.Delete(original);
                    File.Move(tmp, original);
                }
                try { File.SetAttributes(original, attrs & ~FileAttributes.Hidden); } catch { }
                res.FilesProcessed += add.Count(a => !a.IsDir);
                ok = true;
            }
            finally
            {
                fs.Dispose();
                if (!ok) try { File.Delete(tmp); } catch { }
            }
        }
    }

    internal sealed class UpdateSlot
    {
        public int ExistingIndex = -1;
        public string NewPath;  // rename for existing items
        public NewItem Item;    // new data
    }

    [ComVisible(true)]
    internal sealed class UpdateCallback : IArchiveUpdateCallback, ICryptoGetTextPassword2, ICryptoGetTextPassword
    {
        private readonly List<UpdateSlot> _slots;
        private readonly ArchiveReader _source;
        private readonly string _password;
        private string _decodePassword;
        private readonly IOperationUI _ui;
        private readonly List<InStreamWrapper> _open = new List<InStreamWrapper>();
        public bool UserCancelled;
        public string ReadError;

        public UpdateCallback(List<UpdateSlot> slots, ArchiveReader source, string password, string decodePassword, IOperationUI ui)
        {
            _slots = slots; _source = source; _password = password; _decodePassword = decodePassword; _ui = ui;
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

        public int GetUpdateItemInfo(uint index, IntPtr newData, IntPtr newProperties, IntPtr indexInArchive)
        {
            var s = _slots[(int)index];
            bool isNew = s.Item != null;
            if (newData != IntPtr.Zero) Marshal.WriteInt32(newData, isNew ? 1 : 0);
            if (newProperties != IntPtr.Zero) Marshal.WriteInt32(newProperties, isNew || s.NewPath != null ? 1 : 0);
            if (indexInArchive != IntPtr.Zero) Marshal.WriteInt32(indexInArchive, isNew ? -1 : s.ExistingIndex);
            return HR.S_OK;
        }

        public int GetProperty(uint index, uint propID, IntPtr value)
        {
            try
            {
                var s = _slots[(int)index];
                if (s.Item == null)
                {
                    if (propID == Kpid.Path && s.NewPath != null) { PropVar.WriteString(value, s.NewPath); return HR.S_OK; }
                    if (propID == Kpid.IsAnti) { PropVar.WriteBool(value, false); return HR.S_OK; }
                    if (_source != null) return _source.Arc.GetProperty((uint)s.ExistingIndex, propID, value);
                    PropVar.WriteEmpty(value);
                    return HR.S_OK;
                }
                var it = s.Item;
                switch (propID)
                {
                    case Kpid.Path: PropVar.WriteString(value, it.ArchivePath); break;
                    case Kpid.IsDir: PropVar.WriteBool(value, it.IsDir); break;
                    case Kpid.Size: PropVar.WriteUInt64(value, it.IsDir ? 0 : it.Size); break;
                    case Kpid.Attrib: PropVar.WriteUInt32(value, it.Attributes | (it.IsDir ? 0x10u : 0u)); break;
                    case Kpid.MTime: PropVar.WriteFileTime(value, it.MTime); break;
                    case Kpid.CTime: PropVar.WriteFileTime(value, it.CTime); break;
                    case Kpid.ATime: PropVar.WriteFileTime(value, it.ATime); break;
                    case Kpid.IsAnti: PropVar.WriteBool(value, false); break;
                    default: PropVar.WriteEmpty(value); break;
                }
                return HR.S_OK;
            }
            catch { return HR.E_FAIL; }
        }

        public int GetStream(uint index, out ISequentialInStream inStream)
        {
            inStream = null;
            var it = _slots[(int)index].Item;
            if (it == null || it.IsDir || it.DiskPath == null) return HR.S_OK;
            if (_ui != null && _ui.IsCancelled) { UserCancelled = true; return HR.E_ABORT; }
            _ui?.SetCurrentFile(it.ArchivePath);
            try
            {
                string path = it.DiskPath;
                Func<Stream> open = () => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 18);
                var w = new InStreamWrapper(open(), true, open);
                lock (_open) _open.Add(w);
                inStream = w;
                return HR.S_OK;
            }
            catch (Exception ex)
            {
                ReadError = "파일을 열 수 없습니다: " + it.DiskPath + "\n" + ex.Message;
                return HR.E_FAIL;
            }
        }

        public int SetOperationResult(int operationResult) => HR.S_OK;

        public int CryptoGetTextPassword2(out int passwordIsDefined, out string password)
        {
            passwordIsDefined = _password != null ? 1 : 0;
            password = _password ?? "";
            return HR.S_OK;
        }

        public int CryptoGetTextPassword(out string password)
        {
            if (_decodePassword == null)
            {
                _decodePassword = _ui?.AskPassword(_source?.DisplayName ?? "", false);
                if (_decodePassword == null) { UserCancelled = true; password = ""; return HR.E_ABORT; }
            }
            password = _decodePassword;
            return HR.S_OK;
        }

        public void CloseAll()
        {
            lock (_open)
            {
                foreach (var w in _open)
                {
                    if (w.Error != null && ReadError == null) ReadError = "파일 읽기 오류: " + w.Error.Message;
                    w.Dispose();
                }
                _open.Clear();
            }
        }
    }
}
