using System;
using System.Collections.Generic;
using System.Linq;

namespace ArcBox.Shell
{
    public sealed class FormatInfo
    {
        public string Ext;        // lower case, no dot
        public string Group;      // 압축 파일 | UNIX 계열 압축 파일 | 디스크 이미지 · 기타
        public bool DefaultOn;
        public string Label => Ext.ToUpperInvariant();
    }

    public static class Formats
    {
        public const string GroupArchive = "압축 파일";
        public const string GroupUnix = "UNIX 계열 압축 파일";
        public const string GroupOther = "디스크 이미지 · 기타";

        public static readonly List<FormatInfo> All = Build();

        private static List<FormatInfo> Build()
        {
            var list = new List<FormatInfo>();
            void Add(string group, string exts, bool on)
            {
                foreach (var e in exts.Split(' ')) list.Add(new FormatInfo { Ext = e.ToLowerInvariant(), Group = group, DefaultOn = on });
            }
            Add(GroupArchive, "ZIP ZIPX RAR 7Z ALZ EGG CAB BH 001 ARJ LHA LZH PMA ACE ARC AES ZPAQ ZST BR PEA WIM SQX ZOO HA", true);
            Add(GroupUnix, "TAR GZ TGZ BZ BZ2 TBZ TBZ2 XZ TXZ LZMA TLZ LZ UU UUE XXE Z TAZ LZ4 TZST CPIO RPM DEB AR", true);
            Add(GroupOther, "ISO IMG VHD VHDX DMG CHM MSI APK JAR XPI", false);
            return list;
        }

        private static HashSet<string> _engine;

        /// <summary>Extensions the 7-Zip engine can open (computed from 7z.dll at runtime).</summary>
        public static bool EngineSupports(string ext)
        {
            if (_engine == null)
            {
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    foreach (var f in Core.SevenZipLib.Instance.Formats)
                    {
                        foreach (var e in f.Extensions) set.Add(e);
                        foreach (var e in f.AddExtensions) set.Add(e.Trim('*', '.'));
                    }
                    set.Add("001");
                    set.Add("zst"); set.Add("tzst"); set.Add("bz"); set.Add("tlz"); set.Add("alz"); set.Add("egg");
                    foreach (var x in new[] { "ace", "arc", "zoo", "pma", "zpaq", "lz", "lz4", "br", "uu", "uue", "xxe", "aes", "ha", "pea" }) set.Add(x);
                }
                catch { }
                _engine = set;
            }
            return _engine.Count == 0 || _engine.Contains(ext);
        }

        public static bool IsArchiveExtension(string path)
        {
            string ext = System.IO.Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
            if (System.Text.RegularExpressions.Regex.IsMatch(ext, @"^\d{3}$")) return true;
            return All.Any(f => f.Ext == ext) || (ext.Length > 0 && EngineSupports(ext) && ext != "exe");
        }
    }
}
