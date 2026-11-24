using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace ArcBox.Core
{
    public sealed class ArcFormat
    {
        public int Index;
        public string Name;
        public Guid ClassId;
        public string[] Extensions = new string[0];
        public string[] AddExtensions = new string[0];
        public bool CanUpdate;
        public List<byte[]> Signatures = new List<byte[]>();
        public int SignatureOffset;
        public override string ToString() => Name;
        public bool Is(string name) => string.Equals(Name, name, StringComparison.OrdinalIgnoreCase);
    }

    public class ArchiveException : Exception
    {
        public ArchiveException(string message) : base(message) { }
        public ArchiveException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>Loads 7z.dll and creates handler objects.</summary>
    internal sealed class SevenZipLib
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryW(string path);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int CreateObjectFn(ref Guid clsid, ref Guid iid, out IntPtr obj);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetNumberOfFormatsFn(out uint count);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetHandlerProperty2Fn(uint index, uint propId, IntPtr value);

        private static SevenZipLib _instance;
        private static readonly object Sync = new object();

        private readonly CreateObjectFn _createObject;
        public readonly List<ArcFormat> Formats = new List<ArcFormat>();
        public readonly string LibraryPath;

        public static SevenZipLib Instance
        {
            get
            {
                lock (Sync)
                {
                    if (_instance == null) _instance = new SevenZipLib(FindLibrary());
                    return _instance;
                }
            }
        }

        private static string FindLibrary()
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            string sub = IntPtr.Size == 8 ? "x64" : "x86";
            var candidates = new List<string>
            {
                Path.Combine(dir, "7z.dll"),
                Path.Combine(dir, sub, "7z.dll"),
                Path.Combine(dir, "engine", "7z.dll"),
            };
            foreach (var c in candidates) if (File.Exists(c)) return c;
            throw new ArchiveException("압축 엔진(7z.dll)을 찾을 수 없습니다. 프로그램을 다시 설치해 주세요.\n" + dir);
        }

        private SevenZipLib(string path)
        {
            LibraryPath = path;
            IntPtr h = LoadLibraryW(path);
            if (h == IntPtr.Zero)
                throw new ArchiveException("압축 엔진을 불러오지 못했습니다 (오류 " + Marshal.GetLastWin32Error() + "): " + path);

            _createObject = GetFn<CreateObjectFn>(h, "CreateObject");
            var getCount = GetFn<GetNumberOfFormatsFn>(h, "GetNumberOfFormats");
            var getProp = GetFn<GetHandlerProperty2Fn>(h, "GetHandlerProperty2");

            getCount(out uint count);
            IntPtr pv = PropVar.Alloc();
            try
            {
                for (uint i = 0; i < count; i++)
                {
                    var f = new ArcFormat { Index = (int)i };
                    PropVar.Clear(pv); getProp(i, HandlerProp.Name, pv); f.Name = PropVar.Read(pv) as string ?? ("fmt" + i);
                    PropVar.Clear(pv); getProp(i, HandlerProp.ClassID, pv);
                    var g = PropVar.ReadBstrBytes(pv);
                    if (g == null || g.Length < 16) continue;
                    f.ClassId = new Guid(g.Take(16).ToArray());
                    PropVar.Clear(pv); getProp(i, HandlerProp.Extension, pv);
                    f.Extensions = Split(PropVar.Read(pv) as string);
                    PropVar.Clear(pv); getProp(i, HandlerProp.AddExtension, pv);
                    f.AddExtensions = Split(PropVar.Read(pv) as string);
                    PropVar.Clear(pv); getProp(i, HandlerProp.Update, pv);
                    f.CanUpdate = PropVar.Read(pv) is bool b && b;
                    PropVar.Clear(pv); getProp(i, HandlerProp.Signature, pv);
                    var sig = PropVar.ReadBstrBytes(pv);
                    if (sig != null && sig.Length > 0) f.Signatures.Add(sig);
                    PropVar.Clear(pv); getProp(i, HandlerProp.MultiSignature, pv);
                    var ms = PropVar.ReadBstrBytes(pv);
                    if (ms != null)
                    {
                        int pos = 0;
                        while (pos < ms.Length)
                        {
                            int len = ms[pos++];
                            if (len == 0 || pos + len > ms.Length) break;
                            f.Signatures.Add(ms.Skip(pos).Take(len).ToArray());
                            pos += len;
                        }
                    }
                    PropVar.Clear(pv); getProp(i, HandlerProp.SigOffset, pv);
                    f.SignatureOffset = (int)(PropVar.ToULong(PropVar.Read(pv)) ?? 0);
                    Formats.Add(f);
                }
            }
            finally { PropVar.Free(pv); }
        }

        private static string[] Split(string s) =>
            string.IsNullOrEmpty(s) ? new string[0] : s.ToLowerInvariant().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

        private static T GetFn<T>(IntPtr module, string name) where T : class
        {
            IntPtr p = GetProcAddress(module, name);
            if (p == IntPtr.Zero) throw new ArchiveException("7z.dll에 " + name + " 함수가 없습니다.");
            return (T)(object)Marshal.GetDelegateForFunctionPointer(p, typeof(T));
        }

        public ArcFormat FindFormat(string name) => Formats.FirstOrDefault(f => f.Is(name));

        private object Create(Guid clsid, Guid iid)
        {
            int hr = _createObject(ref clsid, ref iid, out IntPtr ptr);
            if (hr != 0 || ptr == IntPtr.Zero) throw new ArchiveException("압축 핸들러를 만들 수 없습니다 (0x" + hr.ToString("X8") + ")");
            try { return Marshal.GetObjectForIUnknown(ptr); }
            finally { Marshal.Release(ptr); }
        }

        public IInArchive CreateInArchive(ArcFormat f) => (IInArchive)Create(f.ClassId, typeof(IInArchive).GUID);
        public IOutArchive CreateOutArchive(ArcFormat f) => (IOutArchive)Create(f.ClassId, typeof(IOutArchive).GUID);

        private static readonly HashSet<string> LowPriority = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "PE", "TE", "ELF", "MachO", "MBR", "GPT", "APM", "FAT", "NTFS", "Ext", "VHD", "VHDX", "VMDK", "QCOW", "VDI", "Base64", "COFF", "MUB", "Hxs", "Sparse", "LP", "SquashFS", "CramFS", "IHex", "AVB", "UEFIc", "UEFIf", "FLV", "SWF", "SWFc", "Mslz", "Ppmd", "Lzma", "Lzma86", "MsLZ" };

        private static readonly HashSet<string> ContentDetectable = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "tar", "Cpio", "Ar", "Iso", "Udf", "Rpm", "Lzh", "Arj", "Cab", "Rar", "Rar5", "7z", "zip", "wim", "xz", "bzip2", "gzip", "zstd", "Z", "Xar", "Dmg" };

        /// <summary>Orders formats: signature match first, then extension match, then everything else.</summary>
        public List<ArcFormat> OrderCandidates(byte[] head, int headLen, string fileName)
        {
            string ext = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
            var sig = new List<ArcFormat>();
            var byExt = new List<ArcFormat>();
            var rest = new List<ArcFormat>();
            var low = new List<ArcFormat>();
            foreach (var f in Formats)
            {
                if (f.Is("Split") || f.Is("Hash")) continue;
                bool sm = f.Signatures.Any(s => Matches(head, headLen, s, f.SignatureOffset));
                bool em = f.Extensions.Contains(ext);
                if (sm && !LowPriority.Contains(f.Name)) sig.Add(f);
                else if (em && !LowPriority.Contains(f.Name)) byExt.Add(f);
                else if (LowPriority.Contains(f.Name)) low.Add(f);
                else rest.Add(f);
            }
            // when extension and signature both match, put it first
            sig.Sort((a, b) => (b.Extensions.Contains(ext) ? 1 : 0) - (a.Extensions.Contains(ext) ? 1 : 0));
            var all = new List<ArcFormat>();
            all.AddRange(sig); all.AddRange(byExt);
            // last resort: only formats whose content can be recognised reliably even without a known extension
            all.AddRange(rest.Where(f => ContentDetectable.Contains(f.Name)));
            return all;
        }

        private static bool Matches(byte[] head, int len, byte[] sig, int offset)
        {
            if (offset + sig.Length > len) return false;
            for (int i = 0; i < sig.Length; i++) if (head[offset + i] != sig[i]) return false;
            return true;
        }

        public static void SetProperties(object handler, IList<KeyValuePair<string, object>> props)
        {
            if (props == null || props.Count == 0) return;
            if (!(handler is ISetProperties sp)) return;
            IntPtr values = Marshal.AllocHGlobal(PropVar.Size * props.Count);
            try
            {
                for (int i = 0; i < props.Count; i++)
                {
                    IntPtr p = values + i * PropVar.Size;
                    for (int k = 0; k < PropVar.Size; k += 4) Marshal.WriteInt32(p, k, 0);
                    PropVar.WriteObject(p, props[i].Value);
                }
                int hr = sp.SetProperties(props.Select(x => x.Key).ToArray(), values, (uint)props.Count);
                if (hr != 0) throw new ArchiveException("압축 옵션이 올바르지 않습니다 (0x" + hr.ToString("X8") + ")");
            }
            finally
            {
                for (int i = 0; i < props.Count; i++) PropVar.Clear(values + i * PropVar.Size);
                Marshal.FreeHGlobal(values);
            }
        }
    }
}
