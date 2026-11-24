using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ArcBox.Shell
{
    public sealed class MenuItemDef
    {
        public string Id, Text, Args;
        public bool ForArchives;   // shown on archive files ("ArcBox로 풀기" submenu)
    }

    public static class ShellMenu
    {
        public static readonly List<MenuItemDef> AllItems = new List<MenuItemDef>
        {
            new MenuItemDef { Id = "xsmart", Text = "여기에 풀기 (스마트)", Args = "/x", ForArchives = true },
            new MenuItemDef { Id = "xfolder", Text = "폴더로 풀기", Args = "/xf", ForArchives = true },
            new MenuItemDef { Id = "xdialog", Text = "압축 풀기…", Args = "/xd", ForArchives = true },
            new MenuItemDef { Id = "open", Text = "ArcBox로 열기", Args = "/open", ForArchives = true },
            new MenuItemDef { Id = "test", Text = "압축 파일 테스트", Args = "/t", ForArchives = true },
            new MenuItemDef { Id = "adialog", Text = "압축하기…", Args = "/a" },
            new MenuItemDef { Id = "azip", Text = "ZIP으로 바로 압축", Args = "/az" },
            new MenuItemDef { Id = "a7z", Text = "7Z로 바로 압축", Args = "/a7" },
            new MenuItemDef { Id = "hash", Text = "해시 값 확인", Args = "/hash" },
        };
    }

    /// <summary>Per-user (HKCU) file associations, icons and Explorer context menus. Needs no admin rights.</summary>
    public static class Registration
    {
        private const string AppKey = @"Software\ArcBox";
        private const string Classes = @"Software\Classes";
        private const string ProgPrefix = "ArcBox.";

        [DllImport("shell32.dll")] private static extern void SHChangeNotify(int eventId, int flags, IntPtr item1, IntPtr item2);

        public static string ExePath => Application.ExecutablePath;
        public static string IconDir => Path.Combine(Path.GetDirectoryName(ExePath), "icons");

        private static string IconFor(string ext)
        {
            string p = Path.Combine(IconDir, ext + ".ico");
            if (File.Exists(p)) return p;
            p = Path.Combine(IconDir, "archive.ico");
            return File.Exists(p) ? p : ExePath + ",0";
        }

        private static string Cmd(string args) => "\"" + ExePath + "\" " + args + " \"%1\"";

        public static bool IsRegisteredForThisExe()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(AppKey))
                    return string.Equals(k?.GetValue("RegisteredExe") as string, ExePath, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        public static void Apply(AppSettings s)
        {
            var cu = Registry.CurrentUser;
            using (var app = cu.CreateSubKey(AppKey))
            {
                app.SetValue("RegisteredExe", ExePath);
                using (var caps = app.CreateSubKey("Capabilities"))
                {
                    caps.SetValue("ApplicationName", "ArcBox");
                    caps.SetValue("ApplicationDescription", "ArcBox 압축 프로그램 — ZIP, 7Z, RAR 등 압축/해제");
                    using (var fa = caps.CreateSubKey("FileAssociations"))
                        foreach (var f in Formats.All) fa.SetValue("." + f.Ext, ProgPrefix + f.Ext);
                }
            }
            using (var ra = cu.CreateSubKey(@"Software\RegisteredApplications")) ra.SetValue("ArcBox", AppKey + @"\Capabilities");

            using (var a = cu.CreateSubKey(Classes + @"\Applications\ArcBox.exe"))
            {
                a.SetValue("FriendlyAppName", "ArcBox");
                using (var c = a.CreateSubKey(@"shell\open\command")) c.SetValue("", "\"" + ExePath + "\" \"%1\"");
                using (var st = a.CreateSubKey("SupportedTypes")) foreach (var f in Formats.All) st.SetValue("." + f.Ext, "");
            }

            foreach (var f in Formats.All)
            {
                string prog = ProgPrefix + f.Ext;
                using (var pk = cu.CreateSubKey(Classes + "\\" + prog))
                {
                    pk.SetValue("", f.Label + " 압축 파일");
                    pk.SetValue("FriendlyTypeName", f.Label + " 압축 파일");
                    using (var di = pk.CreateSubKey("DefaultIcon")) di.SetValue("", IconFor(f.Ext));
                    using (var sh = pk.CreateSubKey("shell")) sh.SetValue("", "open");
                    using (var op = pk.CreateSubKey(@"shell\open"))
                    {
                        op.SetValue("", "ArcBox로 열기");
                        op.SetValue("Icon", ExePath + ",0");
                    }
                    using (var c = pk.CreateSubKey(@"shell\open\command")) c.SetValue("", "\"" + ExePath + "\" \"%1\"");
                }
                using (var ek = cu.CreateSubKey(Classes + "\\." + f.Ext))
                {
                    using (var ow = ek.CreateSubKey("OpenWithProgids")) ow.SetValue(prog, new byte[0], RegistryValueKind.None);
                    string cur = ek.GetValue("") as string;
                    if (s.Associated.Contains(f.Ext))
                    {
                        if (!string.IsNullOrEmpty(cur) && cur != prog)
                            using (var bk = cu.CreateSubKey(AppKey + @"\Backup")) bk.SetValue("." + f.Ext, cur);
                        ek.SetValue("", prog);
                    }
                    else if (cur == prog) RestoreDefault(ek, f.Ext);
                }
            }

            RemoveMenus();
            if (s.ShellMenu) AddMenus(s);
            Notify();
        }

        private static void RestoreDefault(RegistryKey ek, string ext)
        {
            string backup = null;
            using (var bk = Registry.CurrentUser.OpenSubKey(AppKey + @"\Backup")) backup = bk?.GetValue("." + ext) as string;
            if (!string.IsNullOrEmpty(backup)) ek.SetValue("", backup);
            else ek.DeleteValue("", false);
        }

        private static void AddMenus(AppSettings s)
        {
            var cu = Registry.CurrentUser;
            var compress = ShellMenu.AllItems.Where(i => !i.ForArchives && s.MenuItems.Contains(i.Id)).ToList();
            var extract = ShellMenu.AllItems.Where(i => i.ForArchives && s.MenuItems.Contains(i.Id)).ToList();

            if (compress.Count > 0)
            {
                foreach (var root in new[] { @"*\shell\ArcBox", @"Directory\shell\ArcBox" })
                {
                    bool isDir = root.StartsWith("Directory");
                    using (var k = cu.CreateSubKey(Classes + "\\" + root))
                    {
                        k.SetValue("MUIVerb", "ArcBox로 압축");
                        k.SetValue("Icon", ExePath + ",0");
                        k.SetValue("SubCommands", "");
                        k.SetValue("MultiSelectModel", "Player");
                        int n = 1;
                        foreach (var it in compress)
                        {
                            if (isDir && it.Id == "hash") continue;
                            using (var ik = k.CreateSubKey(@"shell\" + (n++).ToString("00") + it.Id))
                            {
                                ik.SetValue("MUIVerb", it.Text);
                                ik.SetValue("MultiSelectModel", "Player");
                                using (var c = ik.CreateSubKey("command")) c.SetValue("", Cmd(it.Args));
                            }
                        }
                    }
                }
            }

            if (extract.Count > 0)
            {
                foreach (var f in Formats.All)
                {
                    using (var k = cu.CreateSubKey(Classes + @"\SystemFileAssociations\." + f.Ext + @"\shell\ArcBoxExtract"))
                    {
                        k.SetValue("MUIVerb", "ArcBox로 풀기");
                        k.SetValue("Icon", ExePath + ",0");
                        k.SetValue("SubCommands", "");
                        k.SetValue("MultiSelectModel", "Player");
                        int n = 1;
                        foreach (var it in extract)
                        {
                            using (var ik = k.CreateSubKey(@"shell\" + (n++).ToString("00") + it.Id))
                            {
                                ik.SetValue("MUIVerb", it.Text);
                                ik.SetValue("MultiSelectModel", "Player");
                                using (var c = ik.CreateSubKey("command")) c.SetValue("", Cmd(it.Args));
                            }
                        }
                    }
                }
            }
        }

        private static void RemoveMenus()
        {
            var cu = Registry.CurrentUser;
            foreach (var root in new[] { @"*\shell\ArcBox", @"Directory\shell\ArcBox" })
                try { cu.DeleteSubKeyTree(Classes + "\\" + root, false); } catch { }
            foreach (var f in Formats.All)
                try { cu.DeleteSubKeyTree(Classes + @"\SystemFileAssociations\." + f.Ext + @"\shell\ArcBoxExtract", false); } catch { }
        }

        public static void RemoveAll()
        {
            var cu = Registry.CurrentUser;
            RemoveMenus();
            foreach (var f in Formats.All)
            {
                string prog = ProgPrefix + f.Ext;
                try
                {
                    using (var ek = cu.OpenSubKey(Classes + "\\." + f.Ext, true))
                    {
                        if (ek != null)
                        {
                            if ((ek.GetValue("") as string) == prog) RestoreDefault(ek, f.Ext);
                            using (var ow = ek.OpenSubKey("OpenWithProgids", true)) ow?.DeleteValue(prog, false);
                        }
                    }
                }
                catch { }
                try { cu.DeleteSubKeyTree(Classes + "\\" + prog, false); } catch { }
            }
            try { cu.DeleteSubKeyTree(Classes + @"\Applications\ArcBox.exe", false); } catch { }
            try { using (var ra = cu.OpenSubKey(@"Software\RegisteredApplications", true)) ra?.DeleteValue("ArcBox", false); } catch { }
            try { cu.DeleteSubKeyTree(AppKey, false); } catch { }
            Notify();
        }

        /// <summary>Extensions where Windows' saved user choice points to another program (we cannot override it).</summary>
        public static List<string> OverriddenByUserChoice(AppSettings s)
        {
            var list = new List<string>();
            foreach (var ext in s.Associated)
            {
                try
                {
                    using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\." + ext + @"\UserChoice"))
                    {
                        string prog = k?.GetValue("ProgId") as string;
                        if (!string.IsNullOrEmpty(prog) && prog != ProgPrefix + ext) list.Add(ext);
                    }
                }
                catch { }
            }
            return list;
        }

        public static void Notify()
        {
            try { SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero); } catch { }
        }
    }
}
