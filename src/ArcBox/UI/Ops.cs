using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using ArcBox.Core;

namespace ArcBox.UI
{
    public enum ExtractTarget { Smart, Folder, Exact }

    /// <summary>High level operations shared by the main window and the Explorer context menu.</summary>
    public static class Ops
    {
        public static string ResolveDest(ArchiveReader r, string baseDir, ExtractTarget mode)
        {
            string name = PathUtil.ArchiveBaseName(r.FilePath);
            if (mode == ExtractTarget.Exact) return baseDir;
            var tops = r.TopLevelNames();
            if (mode == ExtractTarget.Smart && tops.Count == 1) return baseDir;
            if (mode == ExtractTarget.Folder && tops.Count == 1 && string.Equals(tops[0], name, StringComparison.OrdinalIgnoreCase)
                && r.Entries.Any(e => e.Path.Contains("\\"))) return baseDir; // avoid "name\name"
            return PathUtil.UniqueDir(Path.Combine(baseDir, PathUtil.SanitizeComponent(name)));
        }

        public static void Extract(IWin32Window owner, IList<string> archives, Func<string, string> baseDirFor, ExtractTarget mode, OverwriteMode ow)
        {
            if (archives.Count == 0) return;
            var total = new OpResult();
            string lastDest = null, lastArchiveDir = null;
            string subject = archives.Count == 1 ? Path.GetFileName(archives[0]) : archives.Count + "개 압축 파일";
            var pf = ProgressForm.Run(owner, "압축 푸는 중", subject, archives.Count == 1 ? baseDirFor(archives[0]) : null, true, ui =>
            {
                for (int i = 0; i < archives.Count; i++)
                {
                    if (ui.IsCancelled) { total.Cancelled = true; break; }
                    string a = archives[i];
                    ui.SetStage(archives.Count > 1 ? (i + 1) + "/" + archives.Count + " " + Path.GetFileName(a) : "");
                    try
                    {
                        using (var r = ArchiveReader.Open(a, ui))
                        {
                            string dest = ResolveDest(r, baseDirFor(a), mode);
                            var res = new OpResult();
                            r.Extract(r.PlanFor(r.Entries, dest, ""), ow, ui, res);
                            if (res.HasErrors) for (int k = 0; k < res.Errors.Count; k++) res.Errors[k] = Path.GetFileName(a) + " › " + res.Errors[k];
                            total.Merge(res);
                            lastDest = dest; lastArchiveDir = Path.GetDirectoryName(Path.GetFullPath(a));
                            if (res.Cancelled) break;
                        }
                    }
                    catch (OperationCanceledException) { total.Cancelled = true; break; }
                    catch (ArchiveException ex) { total.Errors.Add(Path.GetFileName(a) + ": " + ex.Message); }
                }
            });
            if (pf.Error != null) total.Errors.Add(pf.Error.Message);
            ListDialog.Result(owner, "압축 풀기", total, null);
            if (!total.Cancelled && !total.HasErrors && pf.OpenFolderChecked && lastDest != null &&
                !string.Equals(lastDest.TrimEnd('\\'), lastArchiveDir?.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                Kit.OpenFolder(lastDest);
        }

        public static void Test(IWin32Window owner, IList<string> archives)
        {
            var total = new OpResult();
            var pf = ProgressForm.Run(owner, "테스트 중", archives.Count == 1 ? Path.GetFileName(archives[0]) : archives.Count + "개 압축 파일", null, false, ui =>
            {
                foreach (var a in archives)
                {
                    if (ui.IsCancelled) break;
                    try
                    {
                        using (var r = ArchiveReader.Open(a, ui))
                        {
                            var res = new OpResult();
                            r.Test(ui, res);
                            if (res.HasErrors) for (int k = 0; k < res.Errors.Count; k++) res.Errors[k] = Path.GetFileName(a) + " › " + res.Errors[k];
                            total.Merge(res);
                        }
                    }
                    catch (OperationCanceledException) { total.Cancelled = true; }
                    catch (ArchiveException ex) { total.Errors.Add(Path.GetFileName(a) + ": " + ex.Message); }
                }
            });
            if (pf.Error != null) total.Errors.Add(pf.Error.Message);
            if (pf.IsCancelled || total.Cancelled) return;
            ListDialog.Result(owner, "압축 파일 테스트", total, "검사를 마쳤습니다. 오류가 없습니다.\n(파일 " + total.FilesProcessed + "개 확인)");
        }

        /// <summary>Default archive name for a set of sources: one item → its name, several → parent folder name.</summary>
        public static string DefaultArchivePath(IList<string> sources, string format)
        {
            string first = Path.GetFullPath(sources[0].TrimEnd('\\'));
            string dir = Path.GetDirectoryName(first) ?? first;
            string name;
            if (sources.Count == 1)
                name = File.Exists(first) && !CompressOptions.IsSingleStream(format) ? Path.GetFileNameWithoutExtension(first) : Path.GetFileName(first);
            else name = Path.GetFileName(dir.TrimEnd('\\'));
            if (string.IsNullOrEmpty(name)) name = "압축";
            return Path.Combine(dir, name + CompressOptions.Extension(format));
        }

        public static bool Compress(IWin32Window owner, IList<string> sources, string archivePath, CompressOptions o, bool separately)
        {
            var total = new OpResult();
            string subject = separately ? sources.Count + "개 항목" : Path.GetFileName(archivePath);
            var pf = ProgressForm.Run(owner, "압축하는 중", subject, archivePath, false, ui =>
            {
                if (separately)
                {
                    string outDir = Path.GetDirectoryName(archivePath);
                    for (int i = 0; i < sources.Count; i++)
                    {
                        if (ui.IsCancelled) { total.Cancelled = true; break; }
                        string src = sources[i];
                        ui.SetStage((i + 1) + "/" + sources.Count);
                        string target = PathUtil.UniqueFile(Path.Combine(outDir, Path.GetFileName(DefaultArchivePath(new[] { src }, o.Format))));
                        var res = new OpResult();
                        var items = ArchiveWriter.Scan(new[] { src }, "", res, ui, target);
                        ArchiveWriter.Create(target, items, o, ui, res);
                        total.Merge(res);
                        if (res.Cancelled) break;
                    }
                }
                else
                {
                    ui.SetStage("파일 목록 만드는 중");
                    var items = ArchiveWriter.Scan(sources, "", total, ui, archivePath);
                    ui.SetStage("");
                    if (ui.IsCancelled) { total.Cancelled = true; return; }
                    ArchiveWriter.Create(archivePath, items, o, ui, total);
                }
            });
            if (pf.Error != null) total.Errors.Add(pf.Error.Message);
            if (pf.IsCancelled) total.Cancelled = true;
            ListDialog.Result(owner, "압축하기", total, null);
            return !total.Cancelled && !total.HasErrors;
        }

        public static void QuickCompress(IWin32Window owner, IList<string> sources, string format)
        {
            var o = new CompressOptions { Format = format, Level = 5, Utf8Names = AppSettings.Current.Utf8Names };
            string path = PathUtil.UniqueFile(DefaultArchivePath(sources, format));
            Compress(owner, sources, path, o, false);
        }
    }
}
