using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using ArcBox.Core;

namespace ArcBox
{
    /// <summary>Saved archive passwords ("암호 관리자"). Stored in %APPDATA%\ArcBox\passwords.dat, encrypted with Windows DPAPI
    /// for the current user, so the file is useless on another account or PC.</summary>
    public static class PasswordVault
    {
        public sealed class Entry
        {
            public string Password;
            public string Memo = "";
            public DateTime Added = DateTime.Now;
            public DateTime LastUsed;
        }

        private static readonly object Sync = new object();
        private static List<Entry> _items;
        private static string FilePath => Path.Combine(AppSettings.Dir, "passwords.dat");
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ArcBox password vault v1");

        /// <summary>Try saved passwords automatically before asking.</summary>
        public static bool AutoTry
        {
            get { lock (Sync) { Load(); return _autoTry; } }
            set { lock (Sync) { Load(); _autoTry = value; Save(); } }
        }
        private static bool _autoTry = true;

        public static List<Entry> Items
        {
            get { lock (Sync) { Load(); return _items.Select(e => new Entry { Password = e.Password, Memo = e.Memo, Added = e.Added, LastUsed = e.LastUsed }).ToList(); } }
        }

        public static void Replace(IEnumerable<Entry> items)
        {
            lock (Sync)
            {
                Load();
                _items = items.Where(e => !string.IsNullOrEmpty(e.Password)).GroupBy(e => e.Password).Select(g => g.First()).ToList();
                Save();
            }
        }

        public static void Add(string password, string memo = "")
        {
            if (string.IsNullOrEmpty(password)) return;
            lock (Sync)
            {
                Load();
                if (_items.Any(e => e.Password == password)) return;
                _items.Add(new Entry { Password = password, Memo = memo ?? "" });
                Save();
            }
        }

        /// <summary>Passwords to try, most recently used first.</summary>
        public static List<string> Candidates()
        {
            lock (Sync)
            {
                Load();
                if (!_autoTry) return new List<string>();
                return _items.OrderByDescending(e => e.LastUsed).ThenByDescending(e => e.Added).Select(e => e.Password).ToList();
            }
        }

        public static void MarkUsed(string password)
        {
            if (password == null) return;
            lock (Sync)
            {
                Load();
                var e = _items.FirstOrDefault(x => x.Password == password);
                if (e == null) return;
                e.LastUsed = DateTime.Now;
                Save();
            }
        }

        // ------------------------------------------------------------ storage
        private static void Load()
        {
            if (_items != null) return;
            _items = new List<Entry>();
            try
            {
                if (!File.Exists(FilePath)) return;
                var plain = Unprotect(File.ReadAllBytes(FilePath));
                if (plain == null) return;
                foreach (var line in Encoding.UTF8.GetString(plain).Split('\n'))
                {
                    var f = line.Split('\t');
                    if (f.Length >= 2 && f[0] == "auto") { _autoTry = f[1] == "1"; continue; }
                    if (f.Length < 2 || f[0] != "pw") continue;
                    var e = new Entry { Password = Unesc(f[1]), Memo = f.Length > 2 ? Unesc(f[2]) : "" };
                    if (f.Length > 3 && long.TryParse(f[3], out long a)) e.Added = DateTime.FromBinary(a);
                    if (f.Length > 4 && long.TryParse(f[4], out long u)) e.LastUsed = DateTime.FromBinary(u);
                    if (e.Password.Length > 0) _items.Add(e);
                }
            }
            catch (Exception ex) { Log.Write("vault load: " + ex.Message); }
        }

        private static void Save()
        {
            try
            {
                Directory.CreateDirectory(AppSettings.Dir);
                var sb = new StringBuilder();
                sb.Append("auto\t").Append(_autoTry ? "1" : "0").Append('\n');
                foreach (var e in _items)
                    sb.Append("pw\t").Append(Esc(e.Password)).Append('\t').Append(Esc(e.Memo)).Append('\t')
                      .Append(e.Added.ToBinary()).Append('\t').Append(e.LastUsed.ToBinary()).Append('\n');
                var data = Protect(Encoding.UTF8.GetBytes(sb.ToString()));
                string tmp = FilePath + ".tmp";
                File.WriteAllBytes(tmp, data);
                if (File.Exists(FilePath)) File.Delete(FilePath);
                File.Move(tmp, FilePath);
            }
            catch (Exception ex) { Log.Write("vault save: " + ex.Message); }
        }

        private static string Esc(string s) => (s ?? "").Replace("\\", "\\\\").Replace("\t", "\\t").Replace("\n", "\\n").Replace("\r", "\\r");
        private static string Unesc(string s)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '\\' && i + 1 < s.Length)
                {
                    char c = s[++i];
                    sb.Append(c == 't' ? '\t' : c == 'n' ? '\n' : c == 'r' ? '\r' : c);
                }
                else sb.Append(s[i]);
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------ DPAPI (crypt32, works on every target framework)
        [StructLayout(LayoutKind.Sequential)] private struct BLOB { public int Size; public IntPtr Data; }
        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptProtectData(ref BLOB input, string desc, ref BLOB entropy, IntPtr reserved, IntPtr prompt, int flags, out BLOB output);
        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptUnprotectData(ref BLOB input, IntPtr desc, ref BLOB entropy, IntPtr reserved, IntPtr prompt, int flags, out BLOB output);
        [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr p);

        private static byte[] Protect(byte[] data) => Crypt(data, true);
        private static byte[] Unprotect(byte[] data) => Crypt(data, false);

        private static byte[] Crypt(byte[] data, bool protect)
        {
            var hIn = GCHandle.Alloc(data, GCHandleType.Pinned);
            var hEnt = GCHandle.Alloc(Entropy, GCHandleType.Pinned);
            try
            {
                var input = new BLOB { Size = data.Length, Data = hIn.AddrOfPinnedObject() };
                var ent = new BLOB { Size = Entropy.Length, Data = hEnt.AddrOfPinnedObject() };
                const int UI_FORBIDDEN = 1;
                bool ok = protect
                    ? CryptProtectData(ref input, "ArcBox", ref ent, IntPtr.Zero, IntPtr.Zero, UI_FORBIDDEN, out var output)
                    : CryptUnprotectData(ref input, IntPtr.Zero, ref ent, IntPtr.Zero, IntPtr.Zero, UI_FORBIDDEN, out output);
                if (!ok) throw new InvalidOperationException("DPAPI error " + Marshal.GetLastWin32Error());
                try
                {
                    var result = new byte[output.Size];
                    Marshal.Copy(output.Data, result, 0, output.Size);
                    return result;
                }
                finally { LocalFree(output.Data); }
            }
            finally { hIn.Free(); hEnt.Free(); }
        }
    }

    /// <summary>Wraps an operation UI so saved passwords are tried before the user is asked.</summary>
    public sealed class VaultUI : IOperationUI
    {
        private readonly IOperationUI _inner;
        private readonly Queue<string> _candidates;
        private bool _triedSaved;
        public string LastSaved { get; private set; }

        private VaultUI(IOperationUI inner)
        {
            _inner = inner;
            _candidates = new Queue<string>(PasswordVault.Candidates());
        }

        public static IOperationUI Wrap(IOperationUI ui) => ui == null || ui is VaultUI ? ui : new VaultUI(ui);

        public bool IsCancelled => _inner.IsCancelled;
        public void WaitIfPaused() => _inner.WaitIfPaused();
        public void SetStage(string text) => _inner.SetStage(text);
        public void SetTotal(ulong total) => _inner.SetTotal(total);
        public void SetCompleted(ulong done) => _inner.SetCompleted(done);
        public void SetCurrentFile(string name) => _inner.SetCurrentFile(name);
        public OverwriteAction AskOverwrite(string targetPath, ArchiveEntry entry) => _inner.AskOverwrite(targetPath, entry);

        public string AskPassword(string archiveName, bool retry)
        {
            if (_candidates.Count > 0)
            {
                _triedSaved = true;
                LastSaved = _candidates.Dequeue();
                return LastSaved;
            }
            LastSaved = null;
            // saved passwords failing silently is not "wrong password" for the user; only their own typing is
            bool wrong = retry && _userAsked;
            _userAsked = true;
            return _inner.AskPassword(archiveName, wrong);
        }

        private bool _userAsked;
        public bool TriedSaved => _triedSaved;
    }
}
