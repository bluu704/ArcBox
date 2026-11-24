using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using ArcBox.Core;
using ArcBox.Shell;
using ArcBox.UI;

namespace ArcBox
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length >= 3 && args[0] == "/selftest")
            {
                int rc = 0;
                var t = new Thread(() => rc = SelfTest.Run(args[1], args[2]));
                t.SetApartmentState(ApartmentState.MTA); t.Start(); t.Join();
                return rc;
            }

            if (args.Length >= 3 && args[0] == "/selftest-img")
            {
                var lines = Directory.GetFiles(args[1]).OrderBy(x => x).Select(f => Path.GetFileName(f) + ": " + ImageDecode.Diagnose(f));
                File.WriteAllLines(args[2], lines);
                return 0;
            }
            if (args.Length >= 4 && args[0] == "/selftest-ko")
            {
                int rc = 0;
                var t = new Thread(() => rc = SelfTest.RunKorean(args[1], args[2], args[3]));
                t.SetApartmentState(ApartmentState.MTA); t.Start(); t.Join();
                return rc;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => Crash(e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Crash(e.ExceptionObject as Exception);

            AppSettings.Load();
            Theme.SetMode(AppSettings.Current.Theme);
            using (new Mutex(false, "ArcBoxAppRunning"))
            {
                try { return Run(args); }
                catch (Exception ex) { Crash(ex); return 1; }
            }
        }

        private static void Crash(Exception ex)
        {
            Log.Write("UNHANDLED: " + ex);
            try { MessageBox.Show("예기치 않은 오류가 발생했습니다.\n\n" + ex?.Message + "\n\n자세한 내용은 로그 파일에 기록했습니다.", "ArcBox", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            catch { }
        }

        private static void EnsureRegistered()
        {
            try { if (!Registration.IsRegisteredForThisExe()) Registration.Apply(AppSettings.Current); }
            catch (Exception ex) { Log.Write("register: " + ex.Message); }
        }

        private static int Run(string[] args)
        {
            string verb = args.Length > 0 && args[0].StartsWith("/") ? args[0].ToLowerInvariant() : null;
            var files = args.Skip(verb == null ? 0 : 1).Where(a => a.Length > 0).ToList();

            switch (verb)
            {
                case null:
                    MainForm.CleanupStaleTemp();
                    EnsureRegistered();
                    Application.Run(new MainForm(files.FirstOrDefault()));
                    return 0;
                case "/register":
                    Registration.Apply(AppSettings.Current);
                    return 0;
                case "/unregister":
                    Registration.RemoveAll();
                    return 0;
                case "/settings":
                    Application.Run(new SettingsForm());
                    return 0;
                case "/preview":
                {
                    // ArcBox.exe /preview <archive> : image viewer on the archive's pictures
                    ArchiveReader r = null;
                    var pf = ProgressForm.Run(null, "여는 중", Path.GetFileName(files[0]), null, false, ui => r = ArchiveReader.Open(files[0], ui));
                    if (r == null) { if (pf.Error != null) MessageBox.Show(pf.Error.Message, "ArcBox"); return 1; }
                    using (r)
                    {
                        var imgs = r.Entries.Where(e => !e.IsDir && ImageDecode.IsImage(e.Name)).OrderBy(e => e.Path).ToList();
                        if (imgs.Count == 0) { MessageBox.Show("이미지가 없습니다.", "ArcBox"); return 0; }
                        Application.Run(new ImageViewerForm(r, imgs, 0) { StartPosition = FormStartPosition.CenterScreen, ShowInTaskbar = true });
                    }
                    return 0;
                }
                case "/open":
                    Application.Run(new MainForm(files.FirstOrDefault()));
                    return 0;
            }

            var all = Collector.Collect(verb, files.Select(f => { try { return Path.GetFullPath(f); } catch { return f; } }));
            if (all == null || all.Count == 0) return 0; // handed over to the first instance
            switch (verb)
            {
                case "/x":
                    Ops.Extract(null, all, a => Path.GetDirectoryName(a), ExtractTarget.Smart, AppSettings.Current.Overwrite);
                    break;
                case "/xf":
                    Ops.Extract(null, all, a => Path.GetDirectoryName(a), ExtractTarget.Folder, AppSettings.Current.Overwrite);
                    break;
                case "/xd":
                    using (var f = new ExtractForm(all[0], false))
                    {
                        f.StartPosition = FormStartPosition.CenterScreen;
                        if (f.ShowDialog() == DialogResult.OK)
                        {
                            string dest = Path.GetFullPath(f.Destination);
                            Ops.Extract(null, all, a => dest, f.SmartFolder ? ExtractTarget.Smart : ExtractTarget.Exact, f.Overwrite);
                        }
                    }
                    break;
                case "/t":
                    Ops.Test(null, all);
                    break;
                case "/a":
                    using (var f = new CompressForm(all))
                    {
                        f.StartPosition = FormStartPosition.CenterScreen;
                        if (f.ShowDialog() == DialogResult.OK) Ops.Compress(null, all, f.ArchivePath, f.Options, f.Separately);
                    }
                    break;
                case "/az":
                    Ops.QuickCompress(null, all, "zip");
                    break;
                case "/a7":
                    Ops.QuickCompress(null, all, "7z");
                    break;
                case "/hash":
                    HashDialog.Show(null, all.Where(File.Exists).ToList());
                    break;
                default:
                    MessageBox.Show("알 수 없는 명령: " + verb, "ArcBox");
                    return 2;
            }
            return 0;
        }
    }
}
