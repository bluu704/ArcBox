using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace ArcBox.Core
{
    public sealed class ArchiveEntry
    {
        public int Index;
        public string Path;        // normalized with '\', no leading separator
        public string Name;
        public bool IsDir;
        public ulong Size;
        public ulong? PackedSize;
        public DateTime? MTime;    // UTC
        public DateTime? CTime;    // UTC
        public uint Attributes;
        public uint? Crc;
        public bool Encrypted;
        public string Method;
        public override string ToString() => Path;
    }

    public enum OverwriteMode { Ask, Overwrite, Skip, Rename }
    public enum OverwriteAction { Overwrite, Skip, Rename, Cancel }

    /// <summary>Callbacks from a running operation to whoever shows progress / asks questions.</summary>
    public interface IOperationUI
    {
        bool IsCancelled { get; }
        void WaitIfPaused();
        void SetStage(string text);
        void SetTotal(ulong total);
        void SetCompleted(ulong done);
        void SetCurrentFile(string name);
        /// <returns>null when the user cancels</returns>
        string AskPassword(string archiveName, bool retry);
        OverwriteAction AskOverwrite(string targetPath, ArchiveEntry entry);
    }

    public sealed class OpResult
    {
        public readonly List<string> Errors = new List<string>();
        public readonly List<string> Warnings = new List<string>();
        public int FilesProcessed;
        public int FilesSkipped;
        public bool Cancelled;
        public bool PasswordProblem;
        public readonly List<string> OutputPaths = new List<string>();
        public bool HasErrors => Errors.Count > 0;

        public void Merge(OpResult o)
        {
            Errors.AddRange(o.Errors); Warnings.AddRange(o.Warnings);
            FilesProcessed += o.FilesProcessed; FilesSkipped += o.FilesSkipped;
            Cancelled |= o.Cancelled; PasswordProblem |= o.PasswordProblem;
            OutputPaths.AddRange(o.OutputPaths);
        }
    }

    public sealed class CompressOptions
    {
        public string Format = "zip";       // zip, 7z, tar, tgz, tbz2, txz, gz, bz2, xz
        public int Level = 5;               // 0..9
        public string Method;               // null = default for format
        public string Password;             // null/empty = none
        public bool EncryptFileNames;       // 7z only
        public string ZipEncryption = "AES256";
        public bool Solid = true;           // 7z only
        public long VolumeSize;             // 0 = no split
        public bool Utf8Names = true;       // zip only
        public int Threads;                 // 0 = auto

        public CompressOptions Clone() => (CompressOptions)MemberwiseClone();

        public static readonly string[] AllFormats = { "zip", "7z", "tar", "tgz", "tbz2", "txz", "gz", "bz2", "xz" };
        public static bool IsSingleStream(string f) => f == "gz" || f == "bz2" || f == "xz";
        public static bool IsTarCombo(string f) => f == "tgz" || f == "tbz2" || f == "txz";

        public static string Extension(string f)
        {
            switch (f)
            {
                case "tgz": return ".tar.gz";
                case "tbz2": return ".tar.bz2";
                case "txz": return ".tar.xz";
                default: return "." + f;
            }
        }

        public static string EngineFormat(string f)
        {
            switch (f)
            {
                case "zip": return "zip";
                case "7z": return "7z";
                case "tar": return "tar";
                case "gz": case "tgz": return "gzip";
                case "bz2": case "tbz2": return "bzip2";
                case "xz": case "txz": return "xz";
                default: throw new ArgumentException(f);
            }
        }

        public static string FromFileName(string path)
        {
            string n = System.IO.Path.GetFileName(path).ToLowerInvariant();
            if (n.EndsWith(".tar.gz") || n.EndsWith(".tgz")) return "tgz";
            if (n.EndsWith(".tar.bz2") || n.EndsWith(".tbz2") || n.EndsWith(".tbz")) return "tbz2";
            if (n.EndsWith(".tar.xz") || n.EndsWith(".txz")) return "txz";
            string e = System.IO.Path.GetExtension(n).TrimStart('.');
            return AllFormats.Contains(e) ? e : null;
        }

        /// <summary>Parses "100M", "4.7G", "700 MB", "1024" (bytes) etc. Returns 0 when empty/invalid.</summary>
        public static long ParseSize(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return 0;
            s = s.Trim().ToUpperInvariant().Replace(" ", "").Replace("BYTES", "").Replace("B", "");
            if (s.Length == 0) return 0;
            double mul = 1;
            char last = s[s.Length - 1];
            if (last == 'K') mul = 1024; else if (last == 'M') mul = 1024 * 1024; else if (last == 'G') mul = 1024.0 * 1024 * 1024; else if (last == 'T') mul = 1024.0 * 1024 * 1024 * 1024;
            if (mul > 1) s = s.Substring(0, s.Length - 1);
            if (!double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v) || v <= 0) return 0;
            return (long)(v * mul);
        }
    }

    public static class PathUtil
    {
        private static readonly HashSet<string> Reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
          "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };

        public static string NormalizeArchivePath(string p)
        {
            if (string.IsNullOrEmpty(p)) return "";
            p = p.Replace('/', '\\');
            while (p.StartsWith("\\")) p = p.Substring(1);
            while (p.EndsWith("\\")) p = p.Substring(0, p.Length - 1);
            return p;
        }

        public static string SanitizeComponent(string c)
        {
            if (string.IsNullOrEmpty(c)) return "_";
            var sb = new StringBuilder(c.Length);
            foreach (char ch in c)
            {
                if (ch < 32 || ch == '<' || ch == '>' || ch == ':' || ch == '"' || ch == '|' || ch == '?' || ch == '*' || ch == '/' || ch == '\\')
                    sb.Append('_');
                else sb.Append(ch);
            }
            string s = sb.ToString().TrimEnd(' ', '.');
            if (s.Length == 0) s = "_";
            string stem = s.Split('.')[0];
            if (Reserved.Contains(stem)) s = "_" + s;
            return s;
        }

        /// <summary>Turns an archive-internal path into a safe relative path. Blocks "..", drive letters and
        /// absolute paths (zip-slip protection).</summary>
        public static string SafeRelative(string archivePath)
        {
            var parts = new List<string>();
            foreach (var raw in archivePath.Replace('/', '\\').Split('\\'))
            {
                if (raw.Length == 0 || raw == ".") continue;
                if (raw == "..") continue; // drop traversal components
                string c = raw;
                if (parts.Count == 0 && c.Length == 2 && c[1] == ':') continue; // drive letter
                parts.Add(SanitizeComponent(c));
            }
            return string.Join("\\", parts);
        }

        public static bool IsUnder(string full, string root)
        {
            string r = Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            string f = Path.GetFullPath(full);
            return f.StartsWith(r, StringComparison.OrdinalIgnoreCase) || string.Equals(f.TrimEnd('\\', '/'), r.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
        }

        public static string UniqueFile(string path)
        {
            if (!File.Exists(path) && !Directory.Exists(path)) return path;
            string dir = Path.GetDirectoryName(path);
            string name = Path.GetFileNameWithoutExtension(path);
            string ext = Path.GetExtension(path);
            for (int i = 2; ; i++)
            {
                string p = Path.Combine(dir, name + " (" + i + ")" + ext);
                if (!File.Exists(p) && !Directory.Exists(p)) return p;
            }
        }

        public static string UniqueDir(string path)
        {
            if (!File.Exists(path) && !Directory.Exists(path)) return path;
            for (int i = 2; ; i++)
            {
                string p = path + " (" + i + ")";
                if (!File.Exists(p) && !Directory.Exists(p)) return p;
            }
        }

        /// <summary>"foo.tar.gz" -> "foo", "bar.7z.001" -> "bar", "x.part1.rar" -> "x".</summary>
        public static string ArchiveBaseName(string archivePath)
        {
            string n = Path.GetFileName(archivePath);
            if (System.Text.RegularExpressions.Regex.IsMatch(n, @"\.\d{3}$")) n = n.Substring(0, n.Length - 4);
            n = System.Text.RegularExpressions.Regex.Replace(n, @"\.part0*1\.rar$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            string lower = n.ToLowerInvariant();
            foreach (var e in new[] { ".tar.gz", ".tar.bz2", ".tar.xz", ".tar.zst", ".tar.lzma", ".tar.z" })
                if (lower.EndsWith(e)) return n.Substring(0, n.Length - e.Length);
            string x = Path.GetFileNameWithoutExtension(n);
            return string.IsNullOrEmpty(x) ? n : x;
        }

        public static string FormatSize(ulong bytes)
        {
            if (bytes < 1024) return bytes + " B";
            double v = bytes;
            string[] u = { "B", "KB", "MB", "GB", "TB" };
            int i = 0;
            while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
            return v.ToString(v >= 100 ? "0" : v >= 10 ? "0.0" : "0.00") + " " + u[i];
        }
    }
}
