using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace ArcBox
{
    /// <summary>Simple key=value settings stored in %APPDATA%\ArcBox\settings.ini</summary>
    public sealed class AppSettings
    {
        public static AppSettings Current { get; private set; } = new AppSettings();

        public string Theme = "system";            // system | light | dark
        public bool OpenFolderAfterExtract = true;
        public bool SmartFolder = true;            // extract dialog: create folder named after archive
        public string OverwriteMode = "ask";       // ask | overwrite | skip | rename
        public string LastFormat = "zip";
        public int LastLevel = 5;
        public bool Utf8Names = true;
        public bool ShellMenu = true;
        public string DoubleClick = "open";       // open | smart | folder | menu
        public HashSet<string> MenuItems = new HashSet<string>(Shell.ShellMenu.AllItems.Select(i => i.Id));
        public HashSet<string> Associated = new HashSet<string>(Shell.Formats.All.Where(f => f.DefaultOn).Select(f => f.Ext));
        public List<string> Recent = new List<string>();
        public List<string> RecentDest = new List<string>();
        public int WindowW = 960, WindowH = 620;
        public bool WindowMax;
        public int TreeWidth = 186;
        public string ViewMode = "details";       // details | medium | large | xlarge

        public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ArcBox");
        private static string FilePath => Path.Combine(Dir, "settings.ini");

        public static void Load()
        {
            var s = new AppSettings();
            try
            {
                if (File.Exists(FilePath))
                {
                    foreach (var line in File.ReadAllLines(FilePath, Encoding.UTF8))
                    {
                        int eq = line.IndexOf('=');
                        if (eq <= 0) continue;
                        string k = line.Substring(0, eq).Trim(), v = line.Substring(eq + 1).Trim();
                        switch (k)
                        {
                            case "theme": s.Theme = v; break;
                            case "openFolderAfterExtract": s.OpenFolderAfterExtract = v == "1"; break;
                            case "smartFolder": s.SmartFolder = v == "1"; break;
                            case "overwrite": s.OverwriteMode = v; break;
                            case "lastFormat": s.LastFormat = v; break;
                            case "lastLevel": int.TryParse(v, out s.LastLevel); break;
                            case "utf8": s.Utf8Names = v == "1"; break;
                            case "shellMenu": s.ShellMenu = v == "1"; break;
                            case "doubleClick": s.DoubleClick = v; break;
                            case "menuItems": s.MenuItems = Set(v); break;
                            case "associated": s.Associated = Set(v); break;
                            case "recent": s.Recent = List(v); break;
                            case "recentDest": s.RecentDest = List(v); break;
                            case "windowW": int.TryParse(v, out s.WindowW); break;
                            case "windowH": int.TryParse(v, out s.WindowH); break;
                            case "windowMax": s.WindowMax = v == "1"; break;
                            case "treeWidth": int.TryParse(v, out s.TreeWidth); break;
                            case "viewMode": s.ViewMode = v; break;
                        }
                    }
                }
            }
            catch (Exception ex) { Log.Write("settings load: " + ex.Message); }
            if (s.WindowW < 500) s.WindowW = 960;
            if (s.WindowH < 360) s.WindowH = 620;
            if (Array.IndexOf(Core.CompressOptions.AllFormats, s.LastFormat) < 0) s.LastFormat = "zip";
            Current = s;
        }

        private static HashSet<string> Set(string v) =>
            new HashSet<string>(v.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim().ToLowerInvariant()));

        private static List<string> List(string v) =>
            v.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0).ToList();

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var sb = new StringBuilder();
                void W(string k, object v) => sb.Append(k).Append('=').Append(v).Append("\r\n");
                W("theme", Theme);
                W("openFolderAfterExtract", OpenFolderAfterExtract ? 1 : 0);
                W("smartFolder", SmartFolder ? 1 : 0);
                W("overwrite", OverwriteMode);
                W("lastFormat", LastFormat);
                W("lastLevel", LastLevel);
                W("utf8", Utf8Names ? 1 : 0);
                W("shellMenu", ShellMenu ? 1 : 0);
                W("doubleClick", DoubleClick);
                W("menuItems", string.Join(",", MenuItems));
                W("associated", string.Join(",", Associated.OrderBy(x => x)));
                W("recent", string.Join("|", Recent.Take(10)));
                W("recentDest", string.Join("|", RecentDest.Take(10)));
                W("windowW", WindowW); W("windowH", WindowH); W("windowMax", WindowMax ? 1 : 0);
                W("treeWidth", TreeWidth);
                W("viewMode", ViewMode);
                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, sb.ToString(), Encoding.UTF8);
                if (File.Exists(FilePath)) File.Delete(FilePath);
                File.Move(tmp, FilePath);
            }
            catch (Exception ex) { Log.Write("settings save: " + ex.Message); }
        }

        public void AddRecent(string path)
        {
            Recent.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            Recent.Insert(0, path);
            if (Recent.Count > 10) Recent.RemoveRange(10, Recent.Count - 10);
            Save();
        }

        public void AddRecentDest(string path)
        {
            RecentDest.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            RecentDest.Insert(0, path);
            if (RecentDest.Count > 10) RecentDest.RemoveRange(10, RecentDest.Count - 10);
            Save();
        }

        public Core.OverwriteMode Overwrite =>
            OverwriteMode == "overwrite" ? Core.OverwriteMode.Overwrite :
            OverwriteMode == "skip" ? Core.OverwriteMode.Skip :
            OverwriteMode == "rename" ? Core.OverwriteMode.Rename : Core.OverwriteMode.Ask;
    }

    public static class Log
    {
        private static readonly object Sync = new object();

        public static void Write(string msg)
        {
            try
            {
                lock (Sync)
                {
                    string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ArcBox");
                    Directory.CreateDirectory(dir);
                    string f = Path.Combine(dir, "log.txt");
                    if (File.Exists(f) && new FileInfo(f).Length > 1024 * 1024) File.Delete(f);
                    File.AppendAllText(f, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + "\r\n", Encoding.UTF8);
                }
            }
            catch { }
        }
    }
}
