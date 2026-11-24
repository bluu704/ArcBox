using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace ArcBox.Core
{
    /// <summary>Headless engine tests: ArcBox.exe /selftest &lt;workdir&gt; &lt;logfile&gt;</summary>
    public static class SelfTest
    {
        private sealed class HeadlessUI : IOperationUI
        {
            public Queue<string> Passwords = new Queue<string>();
            public OverwriteAction Overwrite = OverwriteAction.Overwrite;
            public int CancelAfter = -1;
            private int _calls;
            public int PasswordAsks;
            public bool IsCancelled => CancelAfter >= 0 && _calls > CancelAfter;
            public void WaitIfPaused() { }
            public void SetStage(string text) { }
            public void SetTotal(ulong total) { }
            public void SetCompleted(ulong done) { _calls++; }
            public void SetCurrentFile(string name) { _calls++; }
            public string AskPassword(string archiveName, bool retry) { PasswordAsks++; return Passwords.Count > 0 ? Passwords.Dequeue() : null; }
            public OverwriteAction AskOverwrite(string targetPath, ArchiveEntry entry) => Overwrite;
        }

        private static StringBuilder _log;
        private static int _pass, _fail;

        private static void Check(bool cond, string what)
        {
            if (cond) { _pass++; _log.AppendLine("  [OK]   " + what); }
            else { _fail++; _log.AppendLine("  [FAIL] " + what); }
        }

        /// <summary>Extracts every ALZ/EGG sample in a folder: ArcBox.exe /selftest-ko &lt;samples&gt; &lt;out&gt; &lt;log&gt;</summary>
        public static int RunKorean(string samples, string outDir, string logFile)
        {
            Korean.ManagedArchive.ForceLhaForTests = true;
            _log = new StringBuilder();
            _pass = _fail = 0;
            Check(Korean.Lea.SelfTest(), "LEA-128/256 test vectors");
            foreach (var f in Directory.GetFiles(samples).OrderBy(x => x))
            {
                string n = Path.GetFileName(f).ToLowerInvariant();
                if (n.EndsWith(".md") || System.Text.RegularExpressions.Regex.IsMatch(n, @"\.(a\d\d|c\d\d)$")) continue;
                if (System.Text.RegularExpressions.Regex.IsMatch(n, @"\.vol([2-9]|\d\d+)\.egg$")) continue;
                try
                {
                    var ui = new HeadlessUI();
                    if (n.StartsWith("patool_p.")) { ui.Passwords.Enqueue("wrong"); for (int i = 0; i < 4; i++) ui.Passwords.Enqueue("thereisnotry"); }
                    else for (int i = 0; i < 5; i++) ui.Passwords.Enqueue("test1234");
                    using (var r = ArchiveReader.Open(f, ui))
                    {
                        string dest = Fresh(Path.Combine(outDir, Path.GetFileName(f)));
                        var res = new OpResult();
                        r.Extract(r.PlanFor(r.Entries, dest, ""), OverwriteMode.Overwrite, ui, res);
                        var t = new OpResult();
                        r.Test(ui, t);
                        Check(!res.HasErrors && !t.HasErrors, Path.GetFileName(f) + ": " + r.Format.Name + (r.IsSolid ? " solid" : "") + " vols=" + r.VolumeCount +
                            " entries=" + r.Entries.Count + " files=" + res.FilesProcessed + " " + string.Join(" | ", res.Errors.Concat(t.Errors).Take(3)));
                    }
                }
                catch (Exception ex) { Check(false, Path.GetFileName(f) + ": " + ex.GetType().Name + " " + ex.Message); }
            }
            // wrong password must be detected, not produce garbage
            var enc = Directory.GetFiles(samples, "enc_aes256.egg").FirstOrDefault();
            if (enc != null)
            {
                var ui = new HeadlessUI();
                ui.Passwords.Enqueue("wrong"); ui.Passwords.Enqueue("test1234");
                using (var r = ArchiveReader.Open(enc, ui))
                {
                    var res = new OpResult();
                    r.Test(ui, res);
                    Check(!res.HasErrors && ui.PasswordAsks == 2, "wrong password re-asked (asks=" + ui.PasswordAsks + ")");
                }
            }
            _log.AppendLine("RESULT: " + _pass + " passed, " + _fail + " failed");
            File.WriteAllText(logFile, _log.ToString(), Encoding.UTF8);
            return _fail == 0 ? 0 : 1;
        }

        public static int Run(string work, string logFile)
        {
            _log = new StringBuilder();
            try { RunAll(work); }
            catch (Exception ex) { _fail++; _log.AppendLine("[CRASH] " + ex); }
            _log.AppendLine();
            _log.AppendLine("RESULT: " + _pass + " passed, " + _fail + " failed");
            File.WriteAllText(logFile, _log.ToString(), Encoding.UTF8);
            return _fail == 0 ? 0 : 1;
        }

        private static string MakeData(string root)
        {
            string src = Path.Combine(root, "원본 데이터");
            if (Directory.Exists(src)) Directory.Delete(src, true);
            Directory.CreateDirectory(Path.Combine(src, "하위 폴더", "깊은 폴더"));
            Directory.CreateDirectory(Path.Combine(src, "빈 폴더"));
            File.WriteAllText(Path.Combine(src, "안녕하세요.txt"), "한글 텍스트 파일입니다.\r\nHello!", Encoding.UTF8);
            File.WriteAllText(Path.Combine(src, "하위 폴더", "readme.md"), new string('x', 50000));
            File.WriteAllBytes(Path.Combine(src, "하위 폴더", "깊은 폴더", "empty.bin"), new byte[0]);
            var rnd = new Random(42);
            var big = new byte[1500 * 1024];
            rnd.NextBytes(big);
            File.WriteAllBytes(Path.Combine(src, "random.bin"), big);
            var t = new DateTime(2020, 5, 17, 10, 30, 0, DateTimeKind.Utc);
            foreach (var f in Directory.GetFiles(src, "*", SearchOption.AllDirectories)) File.SetLastWriteTimeUtc(f, t);
            return src;
        }

        private static bool SameTree(string a, string b, bool checkTimes, out string why)
        {
            why = null;
            var fa = Directory.GetFiles(a, "*", SearchOption.AllDirectories).Select(p => p.Substring(a.Length)).OrderBy(x => x).ToList();
            var fb = Directory.GetFiles(b, "*", SearchOption.AllDirectories).Select(p => p.Substring(b.Length)).OrderBy(x => x).ToList();
            if (!fa.SequenceEqual(fb)) { why = "file lists differ: [" + string.Join(",", fa) + "] vs [" + string.Join(",", fb) + "]"; return false; }
            foreach (var rel in fa)
            {
                if (!File.ReadAllBytes(a + rel).SequenceEqual(File.ReadAllBytes(b + rel))) { why = "content differs: " + rel; return false; }
                if (checkTimes && Math.Abs((File.GetLastWriteTimeUtc(a + rel) - File.GetLastWriteTimeUtc(b + rel)).TotalSeconds) > 2.1)
                { why = "mtime differs: " + rel + " " + File.GetLastWriteTimeUtc(b + rel); return false; }
            }
            var da = Directory.GetDirectories(a, "*", SearchOption.AllDirectories).Select(p => p.Substring(a.Length)).OrderBy(x => x).ToList();
            var db = Directory.GetDirectories(b, "*", SearchOption.AllDirectories).Select(p => p.Substring(b.Length)).OrderBy(x => x).ToList();
            if (!da.SequenceEqual(db)) { why = "dir lists differ: [" + string.Join(",", da) + "] vs [" + string.Join(",", db) + "]"; return false; }
            return true;
        }

        private static string Fresh(string p) { if (Directory.Exists(p)) Directory.Delete(p, true); Directory.CreateDirectory(p); return p; }

        private static void RunAll(string work)
        {
            Directory.CreateDirectory(work);
            var lib = SevenZipLib.Instance;
            _log.AppendLine("7z.dll: " + lib.LibraryPath + "  formats=" + lib.Formats.Count);
            _log.AppendLine("formats: " + string.Join(" ", lib.Formats.Select(f => f.Name + (f.CanUpdate ? "*" : ""))));
            string src = MakeData(work);
            string outDir = Fresh(Path.Combine(work, "out"));

            // 1. round trip for every writable format
            foreach (var fmt in new[] { "zip", "7z", "tar", "tgz", "tbz2", "txz" })
            {
                _log.AppendLine("== " + fmt);
                string arc = Path.Combine(outDir, "테스트" + CompressOptions.Extension(fmt));
                var res = new OpResult();
                var items = ArchiveWriter.Scan(new[] { src }, "", res, null);
                ArchiveWriter.Create(arc, items, new CompressOptions { Format = fmt, Level = 5 }, new HeadlessUI(), res);
                Check(!res.HasErrors && File.Exists(arc), "create " + string.Join("; ", res.Errors));
                using (var r = ArchiveReader.Open(arc, new HeadlessUI()))
                {
                    Check(r.Entries.Count > 0, "open: " + r.Format.Name + " entries=" + r.Entries.Count);
                    string dest = Fresh(Path.Combine(work, "x_" + fmt));
                    var r2 = new OpResult();
                    if (CompressOptions.IsTarCombo(fmt))
                    {
                        // outer stream holds a .tar; extract it and open again
                        r.Extract(r.PlanFor(r.Entries, dest, ""), OverwriteMode.Overwrite, new HeadlessUI(), r2);
                        string tar = Directory.GetFiles(dest).Single();
                        Check(tar.EndsWith(".tar"), "inner tar name " + Path.GetFileName(tar));
                        using (var t = ArchiveReader.Open(tar, new HeadlessUI()))
                        {
                            string d2 = Fresh(Path.Combine(work, "x_" + fmt + "_inner"));
                            t.Extract(t.PlanFor(t.Entries, d2, ""), OverwriteMode.Overwrite, new HeadlessUI(), r2);
                            Check(SameTree(src, Path.Combine(d2, "원본 데이터"), true, out string why), "inner roundtrip " + why);
                        }
                    }
                    else
                    {
                        r.Extract(r.PlanFor(r.Entries, dest, ""), OverwriteMode.Overwrite, new HeadlessUI(), r2);
                        Check(!r2.HasErrors, "extract " + string.Join("; ", r2.Errors));
                        Check(SameTree(src, Path.Combine(dest, "원본 데이터"), true, out string why), "roundtrip identical " + why);
                    }
                    var r3 = new OpResult();
                    r.Test(new HeadlessUI(), r3);
                    Check(!r3.HasErrors, "test " + string.Join("; ", r3.Errors));
                    Check(r.TopLevelNames().Count == 1, "smart top-level = " + string.Join(",", r.TopLevelNames()));
                }
            }

            // 2. single gz
            {
                _log.AppendLine("== gz");
                string arc = Path.Combine(outDir, "random.bin.gz");
                var res = new OpResult();
                var items = ArchiveWriter.Scan(new[] { Path.Combine(src, "random.bin") }, "", res, null);
                ArchiveWriter.Create(arc, items, new CompressOptions { Format = "gz" }, null, res);
                Check(!res.HasErrors, "create gz " + string.Join(";", res.Errors));
                using (var r = ArchiveReader.Open(arc, null))
                {
                    Check(r.Entries.Count == 1, "gz entry name=" + r.Entries[0].Path);
                    var multi = new OpResult();
                    ArchiveWriter.Create(Path.Combine(outDir, "bad.gz"), ArchiveWriter.Scan(new[] { src }, "", multi, null), new CompressOptions { Format = "gz" }, null, multi);
                    Check(multi.HasErrors && !File.Exists(Path.Combine(outDir, "bad.gz")), "gz rejects folders");
                }
            }

            // 3. encrypted 7z with encrypted names; wrong password first
            {
                _log.AppendLine("== 7z encrypted");
                string arc = Path.Combine(outDir, "secret.7z");
                var res = new OpResult();
                ArchiveWriter.Create(arc, ArchiveWriter.Scan(new[] { src }, "", res, null),
                    new CompressOptions { Format = "7z", Password = "비밀1234", EncryptFileNames = true }, null, res);
                Check(!res.HasErrors, "create encrypted 7z");
                var ui = new HeadlessUI();
                ui.Passwords.Enqueue("wrong");
                ui.Passwords.Enqueue("비밀1234");
                using (var r = ArchiveReader.Open(arc, ui))
                {
                    Check(ui.PasswordAsks == 2 && r.HeadersEncrypted, "asked twice for header password, headersEncrypted=" + r.HeadersEncrypted);
                    string dest = Fresh(Path.Combine(work, "x_enc7z"));
                    var r2 = new OpResult();
                    r.Extract(r.PlanFor(r.Entries, dest, ""), OverwriteMode.Overwrite, ui, r2);
                    Check(!r2.HasErrors && SameTree(src, Path.Combine(dest, "원본 데이터"), false, out _), "extract encrypted 7z " + string.Join(";", r2.Errors));
                }
                var none = new HeadlessUI();
                try { ArchiveReader.Open(arc, none).Dispose(); Check(false, "open without password should cancel"); }
                catch (OperationCanceledException) { Check(true, "cancelled password prompt → OperationCanceled"); }
            }

            // 4. zip AES: wrong then right password during extraction
            {
                _log.AppendLine("== zip AES");
                string arc = Path.Combine(outDir, "secret.zip");
                var res = new OpResult();
                ArchiveWriter.Create(arc, ArchiveWriter.Scan(new[] { src }, "", res, null),
                    new CompressOptions { Format = "zip", Password = "pw!", ZipEncryption = "AES256" }, null, res);
                Check(!res.HasErrors, "create aes zip");
                using (var r = ArchiveReader.Open(arc, null))
                {
                    Check(r.HasEncrypted, "entries are encrypted");
                    var ui = new HeadlessUI();
                    ui.Passwords.Enqueue("nope");
                    ui.Passwords.Enqueue("pw!");
                    string dest = Fresh(Path.Combine(work, "x_aes"));
                    var r2 = new OpResult();
                    r.Extract(r.PlanFor(r.Entries, dest, ""), OverwriteMode.Ask, ui, r2);
                    Check(!r2.HasErrors && ui.PasswordAsks == 2, "retry after wrong password (asks=" + ui.PasswordAsks + ") " + string.Join(";", r2.Errors));
                    Check(SameTree(src, Path.Combine(dest, "원본 데이터"), true, out string why), "aes content identical " + why);
                }
            }

            // 5. split volumes
            {
                _log.AppendLine("== split");
                string arc = Path.Combine(outDir, "split.7z");
                var res = new OpResult();
                ArchiveWriter.Create(arc, ArchiveWriter.Scan(new[] { src }, "", res, null),
                    new CompressOptions { Format = "7z", Level = 0, VolumeSize = 400 * 1024 }, null, res);
                Check(!res.HasErrors && res.OutputPaths.Count >= 3, "volumes created: " + res.OutputPaths.Count);
                using (var r = ArchiveReader.Open(arc + ".001", null))
                {
                    Check(r.IsSplitVolume && r.VolumeCount == res.OutputPaths.Count, "opened split, volumes=" + r.VolumeCount);
                    string dest = Fresh(Path.Combine(work, "x_split"));
                    var r2 = new OpResult();
                    r.Extract(r.PlanFor(r.Entries, dest, ""), OverwriteMode.Overwrite, null, r2);
                    Check(!r2.HasErrors && SameTree(src, Path.Combine(dest, "원본 데이터"), true, out _), "split roundtrip");
                }
                var zres = new OpResult();
                ArchiveWriter.Create(Path.Combine(outDir, "split.zip"), ArchiveWriter.Scan(new[] { src }, "", zres, null),
                    new CompressOptions { Format = "zip", Level = 0, VolumeSize = 500 * 1024 }, null, zres);
                using (var r = ArchiveReader.Open(Path.Combine(outDir, "split.zip.001"), null))
                {
                    string dest = Fresh(Path.Combine(work, "x_splitzip"));
                    var r2 = new OpResult();
                    r.Extract(r.PlanFor(r.Entries, dest, ""), OverwriteMode.Overwrite, null, r2);
                    Check(!r2.HasErrors && SameTree(src, Path.Combine(dest, "원본 데이터"), true, out _), "split zip roundtrip");
                }
            }

            // 5b. HA / PEA: archives produced by the ported packers, read back through ArchiveReader
            {
                var rnd = new Random(7);
                var bin = new byte[300000]; rnd.NextBytes(bin);
                var text = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(0, 20000).Select(i => "줄 " + i + ": 다람쥐 헌 쳇바퀴에 타고파 the quick brown fox " + (i % 97) + "\n")));
                var mixed = new byte[2600000];
                for (int i = 0; i < mixed.Length; i++) mixed[i] = (byte)(i % 5000 < 2500 ? text[i % text.Length] : bin[i % bin.Length]);
                var items = new List<(string, byte[], bool)>
                {
                    ("폴더", null, true), ("폴더/한글 문서.txt", text, false), ("폴더/random.bin", bin, false), ("empty.dat", new byte[0], false),
                    ("tiny.txt", Encoding.ASCII.GetBytes("a"), false), ("rep.txt", Enumerable.Repeat((byte)'x', 70000).ToArray(), false)
                };
                var bigItems = new List<(string, byte[], bool)>(items) { ("big/mixed.bin", mixed, false) };
                void Verify(string arc, List<(string, byte[], bool)> list, string label)
                {
                    try
                    {
                        using (var r = ArchiveReader.Open(arc, new HeadlessUI()))
                        {
                            string dest = Fresh(Path.Combine(work, "x_" + Path.GetFileName(arc)));
                            var r2 = new OpResult();
                            r.Extract(r.PlanFor(r.Entries, dest, ""), OverwriteMode.Overwrite, new HeadlessUI(), r2);
                            string why = null;
                            foreach (var (n, d, dir) in list)
                            {
                                string fp = Path.Combine(dest, n.Replace('/', '\\'));
                                if (dir) { if (!Directory.Exists(fp)) why = "missing dir " + n; continue; }
                                if (!File.Exists(fp)) { why = "missing " + n; break; }
                                if (!File.ReadAllBytes(fp).SequenceEqual(d)) { why = "differs " + n; break; }
                            }
                            Check(!r2.HasErrors && why == null, label + " (" + r.Format.Name + ", " + r.Entries.Count + " entries, " + new FileInfo(arc).Length + " bytes) " + why + " " + string.Join(";", r2.Errors));
                        }
                    }
                    catch (Exception ex) { Check(false, label + ": " + ex.GetType().Name + " " + ex.Message); }
                }
                void VerifyCorrupt(string arc, long offset, string label)
                {
                    var b = File.ReadAllBytes(arc); b[offset] ^= 0x55;
                    string bad = Path.Combine(outDir, "bad_" + Path.GetFileName(arc)); File.WriteAllBytes(bad, b);
                    try
                    {
                        using (var r = ArchiveReader.Open(bad, new HeadlessUI()))
                        {
                            var r2 = new OpResult();
                            r.Test(new HeadlessUI(), r2);
                            Check(r2.HasErrors, label + " detected");
                        }
                    }
                    catch (Exception ex) when (!(ex is OutOfMemoryException)) { Check(true, label + " detected at open: " + ex.Message.Split('\n')[0]); }
                }
                var sw = System.Diagnostics.Stopwatch.StartNew();
                foreach (int m in new[] { 0, 1, 2 })
                {
                    string arc = Path.Combine(outDir, "test_m" + m + ".ha");
                    Korean.HaArchive.WriteForTests(arc, items, m);
                    Verify(arc, items, "HA " + (m == 0 ? "CPY" : m == 1 ? "ASC" : "HSC") + " roundtrip");
                    if (m > 0) VerifyCorrupt(arc, new FileInfo(arc).Length / 2, "HA " + m + " corruption");
                }
                _log.AppendLine("   HA time " + sw.ElapsedMilliseconds + " ms");
                foreach (var (compr, obj, vol, volSize) in new[] { (0, 2, 0, 0L), (3, 2, 2, 0L), (1, 1, 0, 0L), (2, 19, 18, 0L), (3, 20, 2, 700000L), (0, 16, 0, 1000000L) })
                {
                    string arc = Path.Combine(outDir, "test_c" + compr + "o" + obj + "v" + volSize + ".pea");
                    Korean.PeaArchive.WriteForTests(arc, bigItems, compr, obj, vol, volSize);
                    string first = volSize > 0 ? arc.Substring(0, arc.Length - 4) + ".000001.pea" : arc;
                    Verify(first, bigItems, "PEA compr=" + compr + " obj=" + obj + " vol=" + vol + " split=" + volSize);
                    if (volSize == 0) VerifyCorrupt(arc, new FileInfo(arc).Length / 3, "PEA " + compr + " corruption");
                }
            }

            // 6. modify (add / rename / delete) for 7z and zip
            foreach (var fmt in new[] { "7z", "zip", "tar" })
            {
                _log.AppendLine("== modify " + fmt);
                string arc = Path.Combine(outDir, "modify." + fmt);
                var res = new OpResult();
                ArchiveWriter.Create(arc, ArchiveWriter.Scan(new[] { src }, "", res, null), new CompressOptions { Format = fmt }, null, res);
                var r = ArchiveReader.Open(arc, null);
                int before = r.Entries.Count;
                var req = new ModifyRequest();
                var del = r.Entries.First(e => e.Path.EndsWith("random.bin"));
                req.Delete.Add(del.Index);
                var ren = r.Entries.First(e => e.Path.EndsWith("안녕하세요.txt"));
                req.Rename[ren.Index] = ren.Path.Replace("안녕하세요.txt", "이름변경.txt");
                string extra = Path.Combine(work, "추가파일.txt");
                File.WriteAllText(extra, "added");
                req.Add.AddRange(ArchiveWriter.Scan(new[] { extra }, "원본 데이터\\하위 폴더", res, null));
                var mres = new OpResult();
                ArchiveWriter.Modify(r, req, null, null, mres);
                Check(!mres.HasErrors, "modify ok " + string.Join(";", mres.Errors));
                using (r = ArchiveReader.Open(arc, null))
                {
                    var paths = r.Entries.Select(e => e.Path).ToList();
                    Check(r.Entries.Count == before, "entry count " + before + " -> " + r.Entries.Count);
                    Check(!paths.Any(p => p.EndsWith("random.bin")), "deleted");
                    Check(paths.Contains("원본 데이터\\이름변경.txt"), "renamed");
                    Check(paths.Contains("원본 데이터\\하위 폴더\\추가파일.txt"), "added into subfolder");
                    var t = new OpResult(); r.Test(null, t);
                    Check(!t.HasErrors, "modified archive tests ok");
                    var renamed = r.Entries.First(e => e.Path.EndsWith("이름변경.txt"));
                    string d = Fresh(Path.Combine(work, "x_mod_" + fmt));
                    var x = new OpResult();
                    r.Extract(r.PlanFor(new[] { renamed }, d, "원본 데이터"), OverwriteMode.Overwrite, null, x);
                    Check(File.Exists(Path.Combine(d, "이름변경.txt")) && File.ReadAllText(Path.Combine(d, "이름변경.txt"), Encoding.UTF8).StartsWith("한글"), "renamed content intact, prefix stripped");
                }
                Check(!Directory.GetFiles(outDir, "*.tmp").Any(), "no temp files left");
            }

            // 7. zip-slip protection
            {
                _log.AppendLine("== zip-slip");
                string arc = Path.Combine(outDir, "evil.zip");
                var res = new OpResult();
                string evilSrc = Path.Combine(work, "evil.txt");
                File.WriteAllText(evilSrc, "evil");
                var it = ArchiveWriter.Scan(new[] { evilSrc }, "", res, null)[0];
                it.ArchivePath = "..\\..\\evil.txt";
                var it2 = ArchiveWriter.Scan(new[] { evilSrc }, "", res, null)[0];
                it2.ArchivePath = "CON\\aux.txt";
                ArchiveWriter.Create(arc, new List<NewItem> { it, it2 }, new CompressOptions { Format = "zip" }, null, res);
                using (var r = ArchiveReader.Open(arc, null))
                {
                    string dest = Fresh(Path.Combine(work, "slip", "a", "b"));
                    var x = new OpResult();
                    r.Extract(r.PlanFor(r.Entries, dest, ""), OverwriteMode.Overwrite, null, x);
                    Check(!File.Exists(Path.Combine(work, "slip", "evil.txt")) && !File.Exists(Path.Combine(work, "evil.txt.x")), "nothing written outside destination");
                    Check(Directory.GetFiles(dest, "*", SearchOption.AllDirectories).Length == 2, "files kept inside dest: " + string.Join(",", Directory.GetFiles(dest, "*", SearchOption.AllDirectories).Select(Path.GetFileName)));
                }
            }

            // 8. cancellation leaves nothing behind
            {
                _log.AppendLine("== cancel");
                string arc = Path.Combine(outDir, "cancel.7z");
                var res = new OpResult();
                var ui = new HeadlessUI { CancelAfter = 1 };
                ArchiveWriter.Create(arc, ArchiveWriter.Scan(new[] { src }, "", res, null), new CompressOptions { Format = "7z", Level = 9 }, ui, res);
                Check(res.Cancelled && !File.Exists(arc), "cancelled create: no output");
                Check(!Directory.GetFiles(outDir, "*.tmp", SearchOption.AllDirectories).Any() && !Directory.GetFiles(outDir, ".*").Any(), "no temp files after cancel");
            }

            // 9. overwrite policies
            {
                _log.AppendLine("== overwrite");
                string arc = Path.Combine(outDir, "테스트.zip");
                using (var r = ArchiveReader.Open(arc, null))
                {
                    string dest = Fresh(Path.Combine(work, "x_over"));
                    var x = new OpResult();
                    r.Extract(r.PlanFor(r.Entries, dest, ""), OverwriteMode.Overwrite, null, x);
                    var ui = new HeadlessUI { Overwrite = OverwriteAction.Rename };
                    var y = new OpResult();
                    r.Extract(r.PlanFor(r.Entries, dest, ""), OverwriteMode.Ask, ui, y);
                    Check(File.Exists(Path.Combine(dest, "원본 데이터", "안녕하세요 (2).txt")), "rename on conflict");
                    var z = new OpResult();
                    r.Extract(r.PlanFor(r.Entries, dest, ""), OverwriteMode.Skip, null, z);
                    Check(z.FilesSkipped > 0 && !File.Exists(Path.Combine(dest, "원본 데이터", "안녕하세요 (3).txt")), "skip on conflict: skipped=" + z.FilesSkipped);
                }
            }

            // 10. not an archive
            {
                _log.AppendLine("== garbage");
                string junk = Path.Combine(outDir, "junk.zip");
                File.WriteAllText(junk, "this is not a zip file at all");
                try { ArchiveReader.Open(junk, null).Dispose(); Check(false, "junk opened?!"); }
                catch (ArchiveException) { Check(true, "junk rejected with ArchiveException"); }
                // truncated archive
                string trunc = Path.Combine(outDir, "trunc.7z");
                var b = File.ReadAllBytes(Path.Combine(outDir, "테스트.7z"));
                File.WriteAllBytes(trunc, b.Take(b.Length / 2).ToArray());
                try
                {
                    using (var r = ArchiveReader.Open(trunc, null))
                    {
                        var t = new OpResult(); r.Test(null, t);
                        Check(t.HasErrors, "truncated detected: " + string.Join(";", t.Errors.Take(2)));
                    }
                }
                catch (ArchiveException) { Check(true, "truncated rejected on open"); }
            }
        }
    }
}
