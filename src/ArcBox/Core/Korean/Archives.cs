using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ArcBox.Core.Korean
{
    internal sealed class ManagedExtractContext
    {
        public Func<int, Stream> Open;              // output for an entry; null = discard
        public Action<int, string, bool> Done;      // entry index, error (null = ok), password related
        public Func<bool, string> AskPassword;      // retry? -> password, null = cancel
        public Action<int> Progress;                // decoded bytes
    }

    /// <summary>Archive formats decoded in managed code (formats the 7-Zip engine does not know).</summary>
    internal abstract class ManagedArchive : IDisposable
    {
        public string FormatName;
        public readonly List<ArchiveEntry> Entries = new List<ArchiveEntry>();
        public bool Solid;
        public int VolumeCount = 1;
        public long PhysicalSize;
        public string Password;
        public bool HeadersEncrypted => false;

        public abstract void Extract(ISet<int> wanted, ManagedExtractContext ctx);
        public abstract void Dispose();

        static ManagedArchive()
        {
#if NET
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
#endif
        }

        internal static bool ForceLhaForTests;

        public static ManagedArchive TryOpen(string path, byte[] head, int headLen)
        {
            if (ForceLhaForTests && path.EndsWith(".lzh", StringComparison.OrdinalIgnoreCase)) return LhaArchive.Open(path);
            if (headLen >= 4 && head[0] == 'A' && head[1] == 'L' && head[2] == 'Z' && head[3] == 1) return AlzArchive.Open(path);
            if (headLen >= 4 && head[0] == 'E' && head[1] == 'G' && head[2] == 'G' && head[3] == 'A') return EggArchive.Open(path);
            if (headLen >= 14 && Encoding.ASCII.GetString(head, 7, 7) == "**ACE**") return AceArchive.Open(path);
            if (ZooArchive.Looks(head, headLen)) return ZooArchive.Open(path);
            if (LhaArchive.LooksLikePma(head, headLen)) return LhaArchive.Open(path);
            if (ArcArchive.Looks(head, headLen, path)) return ArcArchive.Open(path);
            if (ZpaqArchive.Looks(head, headLen)) return ZpaqArchive.Open(path);
            if (PeaArchive.Looks(head, headLen)) return PeaArchive.Open(path);
            if (HaArchive.Looks(head, headLen, path)) return HaArchive.Open(path);
            return StreamArchive.TryOpen(path, head, headLen);
        }

        /// <summary>For ".a00"/".vol2.egg" style names, finds the file that starts the set.</summary>
        public static string FirstVolume(string path)
        {
            var m = Regex.Match(path, @"^(.*)\.[a-z]\d\d$", RegexOptions.IgnoreCase);
            if (m.Success && File.Exists(m.Groups[1].Value + ".alz")) return m.Groups[1].Value + ".alz";
            return path;
        }

        protected static Encoding Cp949
        {
            get { try { return Encoding.GetEncoding(949); } catch { return Encoding.Default; } }
        }

        protected static string DecodeName(byte[] b, Encoding legacy)
        {
            try { return new UTF8Encoding(false, true).GetString(b); }
            catch { return legacy.GetString(b); }
        }

        protected static string Normalize(string name)
        {
            var sb = new StringBuilder(name.Length);
            foreach (char c in name) if (!char.IsControl(c)) sb.Append(c == '/' ? '\\' : c);
            return PathUtil.NormalizeArchivePath(sb.ToString());
        }

        /// <summary>Asks for the password until <paramref name="check"/> accepts it. Returns null when cancelled.</summary>
        protected T WithPassword<T>(ManagedExtractContext ctx, Func<string, T> check) where T : class
        {
            bool retry = false;
            while (true)
            {
                if (Password == null)
                {
                    Password = ctx.AskPassword(retry);
                    if (Password == null) return null;
                }
                var r = check(Password);
                if (r != null) return r;
                Password = null;
                retry = true;
            }
        }

        protected static void Copy(Stream input, Stream output)
        {
            var buf = new byte[1 << 16];
            int n;
            while ((n = input.Read(buf, 0, buf.Length)) > 0) output.Write(buf, 0, n);
        }

        protected static uint U32(byte[] b, int o) => BitConverter.ToUInt32(b, o);
    }

    // ================================================================== ALZ
    internal sealed class AlzArchive : ManagedArchive
    {
        private sealed class Item
        {
            public int Index;
            public byte Attr, Desc, Method;
            public uint DosTime, Crc;
            public long Packed, Size, DataPos;
            public byte[] EncCheck;
            public bool Encrypted => (Desc & 1) != 0;
            public bool DataDescriptor => (Desc & 8) != 0;
        }

        private readonly List<Item> _items = new List<Item>();
        private AlzVolumes _vol;

        public static AlzArchive Open(string path)
        {
            var a = new AlzArchive { FormatName = "ALZ" };
            a._vol = new AlzVolumes(FirstVolume(path));
            try { a.Parse(); }
            catch { a.Dispose(); throw; }
            a.VolumeCount = a._vol.Count;
            a.PhysicalSize = a._vol.PhysicalSize;
            return a;
        }

        private byte[] ReadN(int n)
        {
            var b = new byte[n];
            AzoDecoder.ReadExact(_vol, b, n);
            return b;
        }

        private void Parse()
        {
            uint commentSize = BitConverter.ToUInt32(_vol.Tail, 4);
            bool seenCentral = false;
            var enc = Cp949;
            while (true)
            {
                var sigb = new byte[4];
                int got = _vol.Read(sigb, 0, 4);
                if (got < 4) break;
                uint sig = U32(sigb, 0);
                switch (sig)
                {
                    case 0x015a4c41: ReadN(4); break;                       // ALZ header
                    case 0x015a4c42:                                       // local file header
                    {
                        byte[] h;
                        try { h = ReadN(9); } catch (InvalidDataException) { return; }
                        var it = new Item
                        {
                            Index = _items.Count, Attr = h[2], DosTime = U32(h, 3), Desc = h[7]
                        };
                        int nameLen = h[0] | (h[1] << 8);
                        int byteLen;
                        switch (it.Desc & 0xF0)
                        {
                            case 0x00: byteLen = 0; break;
                            case 0x10: byteLen = 1; break;
                            case 0x20: byteLen = 2; break;
                            case 0x40: byteLen = 4; break;
                            case 0x80: byteLen = 8; break;
                            default: throw new ArchiveException("ALZ 파일 헤더가 손상되었습니다.");
                        }
                        if (byteLen > 0)
                        {
                            var m = ReadN(2 + 4 + byteLen * 2);
                            it.Method = m[0];
                            it.Crc = U32(m, 2);
                            it.Packed = VarInt(m, 6, byteLen);
                            it.Size = VarInt(m, 6 + byteLen, byteLen);
                        }
                        if (nameLen == 0 || nameLen > 4096) throw new ArchiveException("ALZ 파일 이름이 손상되었습니다.");
                        string name = Normalize(DecodeName(ReadN(nameLen), enc));
                        if (it.Encrypted) it.EncCheck = ReadN(12);
                        it.DataPos = _vol.Position;
                        if (it.Packed < 0) throw new ArchiveException("ALZ 파일이 손상되었습니다.");
                        _vol.Position += it.Packed;
                        _items.Add(it);
                        var e = new ArchiveEntry
                        {
                            Index = it.Index, Path = name, IsDir = (it.Attr & 0x10) != 0, Size = (ulong)it.Size,
                            PackedSize = (ulong)it.Packed, Attributes = (uint)(it.Attr & 0x27) | ((it.Attr & 0x10) != 0 ? 0x10u : 0),
                            Crc = byteLen > 0 ? (uint?)it.Crc : null, Encrypted = it.Encrypted,
                            Method = it.Method == 0 ? "Store" : it.Method == 1 ? "BZip2" : it.Method == 2 ? "Deflate" : "?" + it.Method,
                            MTime = DosToUtc(it.DosTime)
                        };
                        int slash = e.Path.LastIndexOf('\\');
                        e.Name = slash >= 0 ? e.Path.Substring(slash + 1) : e.Path;
                        Entries.Add(e);
                        break;
                    }
                    case 0x015a4c43: ReadN(12); seenCentral = true; break;  // central directory
                    case 0x025a4c43: return;                                // end of central directory
                    case 0x015a4c45: if (commentSize > 4) _vol.Position += commentSize - 4; break;
                    case 0x035a4c43: break;                                 // split marker
                    default:
                        if (seenCentral || _items.Count > 0) return;
                        throw new ArchiveException("ALZ 파일이 아니거나 손상되었습니다.");
                }
            }
        }

        private static long VarInt(byte[] b, int o, int len)
        {
            ulong v = 0;
            for (int i = 0; i < len; i++) v |= (ulong)b[o + i] << (8 * i);
            return (long)Math.Min(v, long.MaxValue);
        }

        internal static DateTime? DosToUtc(uint t)
        {
            int sec = (int)(t & 0x1f) * 2, min = (int)(t >> 5) & 0x3f, hour = (int)(t >> 11) & 0x1f;
            int day = (int)(t >> 16) & 0x1f, month = (int)(t >> 21) & 0x0f, year = (int)((t >> 25) & 0x7f) + 1980;
            try { return new DateTime(year, month, day, hour, min, Math.Min(sec, 59), DateTimeKind.Local).ToUniversalTime(); }
            catch { return null; }
        }

        public override void Extract(ISet<int> wanted, ManagedExtractContext ctx)
        {
            foreach (var it in _items)
            {
                if (!wanted.Contains(it.Index)) continue;
                var e = Entries[it.Index];
                Stream output = null;
                try
                {
                    output = ctx.Open(it.Index);
                    if (e.IsDir) { ctx.Done(it.Index, null, false); continue; }
                    IDecryptor dec = null;
                    if (it.Encrypted)
                    {
                        byte expected = it.DataDescriptor ? (byte)(it.DosTime >> 8) : (byte)(it.Crc >> 24);
                        var zc = WithPassword(ctx, p =>
                        {
                            var z = new ZipCrypto(Cp949.GetBytes(p));
                            return z.CheckHeader(it.EncCheck) == expected ? z : null;
                        });
                        if (zc == null) { ctx.Done(it.Index, "암호를 입력하지 않았습니다", true); continue; }
                        dec = zc;
                    }
                    var input = new BufferedStream(new SegmentStream(_vol, it.DataPos, it.Packed, dec), 1 << 16);
                    var crc = new CrcStream(output) { Limit = it.Size, Progress = ctx.Progress };
                    switch (it.Method)
                    {
                        case 0: Copy(input, crc); break;
                        case 1: Bzip2Decoder.DecodeAlz(input, crc); break;
                        case 2: using (var d = new DeflateStream(input, CompressionMode.Decompress, true)) Copy(d, crc); break;
                        default: ctx.Done(it.Index, "지원하지 않는 압축 방식 (" + it.Method + ")", false); continue;
                    }
                    if (crc.Crc != it.Crc || crc.Count != it.Size)
                        ctx.Done(it.Index, it.Encrypted ? "CRC 오류 (암호가 틀렸거나 파일 손상)" : "CRC 오류 (파일 손상)", it.Encrypted);
                    else ctx.Done(it.Index, null, false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (ex is InvalidDataException || ex is EndOfStreamException)
                {
                    ctx.Done(it.Index, ex.Message, it.Encrypted);
                }
            }
        }

        public override void Dispose() => _vol?.Dispose();

        /// <summary>ALZ split set: name.alz, name.a00, name.a01 … (8-byte header on later volumes, 16-byte tail on all but the last).</summary>
        private sealed class AlzVolumes : Stream
        {
            private readonly List<FileStream> _files = new List<FileStream>();
            private readonly List<long> _hdr = new List<long>(), _size = new List<long>(), _start = new List<long>();
            private long _pos, _total;
            public readonly byte[] Tail = new byte[16];
            public int Count => _files.Count;
            public long PhysicalSize;

            public AlzVolumes(string first)
            {
                string prefix = first.Length >= 4 ? first.Substring(0, first.Length - 3) : null;
                for (int i = 0; i < 1000; i++)
                {
                    string p = i == 0 ? first : prefix + (char)('a' + (i - 1) / 100) + ((i - 1) % 100).ToString("00");
                    if (i > 0 && (prefix == null || !File.Exists(p))) break;
                    _files.Add(new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16));
                }
                for (int i = 0; i < _files.Count; i++)
                {
                    long len = _files[i].Length;
                    PhysicalSize += len;
                    long h = i == 0 ? 0 : 8, t = i == _files.Count - 1 ? 0 : 16;
                    if (len < h + t) throw new ArchiveException("ALZ 분할 파일이 손상되었습니다.");
                    _hdr.Add(h); _start.Add(_total); _size.Add(len - h - t); _total += len - h - t;
                }
                var last = _files[_files.Count - 1];
                if (last.Length >= 16) { last.Position = last.Length - 16; AzoDecoder.ReadExact(last, Tail, 16); }
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                int total = 0;
                while (count > 0 && _pos < _total)
                {
                    int i = _start.Count - 1;
                    while (i > 0 && _start[i] > _pos) i--;
                    long local = _pos - _start[i];
                    int want = (int)Math.Min(count, _size[i] - local);
                    if (want <= 0) { _pos = _start[i] + _size[i]; continue; }
                    _files[i].Position = _hdr[i] + local;
                    int n = _files[i].Read(buffer, offset, want);
                    if (n <= 0) break;
                    total += n; offset += n; count -= n; _pos += n;
                }
                return total;
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                _pos = origin == SeekOrigin.Begin ? offset : origin == SeekOrigin.Current ? _pos + offset : _total + offset;
                return _pos;
            }

            public override bool CanRead => true;
            public override bool CanSeek => true;
            public override bool CanWrite => false;
            public override long Length => _total;
            public override long Position { get => _pos; set => _pos = value; }
            public override void Flush() { }
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            protected override void Dispose(bool disposing) { if (disposing) foreach (var f in _files) f.Dispose(); base.Dispose(disposing); }
        }
    }

    // ================================================================== EGG
    internal sealed class EggArchive : ManagedArchive
    {
        private const uint SigEgg = 0x41474745, SigSplit = 0x24F5A262, SigSolid = 0x24E5A060, SigFile = 0x0A8590E3,
            SigName = 0x0A8591AC, SigComment = 0x04C63672, SigWinInfo = 0x2C86950B, SigEncrypt = 0x08D1470F,
            SigBlock = 0x02B50C13, SigDummy = 0x07463307, SigEnd = 0x08E28222, SigSkip = 0xFFFF0000,
            SigGlobalEnc = 0x08D144A8, SigPosix = 0x1EE922E5;

        private sealed class Block { public byte Method; public uint Size, Packed, Crc; public long DataPos; }

        private sealed class Item
        {
            public int Index;
            public uint FileId;
            public long Size;
            public string Name;
            public uint? ParentId;
            public byte Attr;
            public long? FileTime;
            public byte EncMethod = 0xFF;
            public byte[] EncData;
            public readonly List<Block> Blocks = new List<Block>();
            public bool IsDir => (Attr & 0x80) != 0;
        }

        private readonly List<Item> _items = new List<Item>();
        private Stream _s;

        private static readonly string[] MethodNames = { "Store", "Deflate", "BZip2", "AZO", "LZMA" };

        public static EggArchive Open(string path)
        {
            var a = new EggArchive { FormatName = "EGG" };
            try
            {
                var vols = EggVolumes.Open(path);
                a._s = vols;
                a.VolumeCount = vols.Count;
                a.PhysicalSize = vols.PhysicalSize;
                a.Parse();
            }
            catch { a.Dispose(); throw; }
            return a;
        }

        private byte[] ReadN(int n) { var b = new byte[n]; AzoDecoder.ReadExact(_s, b, n); return b; }
        private uint ReadU32() => U32(ReadN(4), 0);
        private byte ReadU8() => ReadN(1)[0];

        private uint ExtraField(out byte flags)
        {
            flags = ReadU8();
            return (flags & 1) != 0 ? ReadU32() : BitConverter.ToUInt16(ReadN(2), 0);
        }

        private void Skip(long n) { _s.Position += n; }

        private void Parse()
        {
            _s.Position = 0;
            if (ReadU32() != SigEgg) throw new ArchiveException("EGG 파일이 아닙니다.");
            ReadN(10);
            while (true)
            {
                uint sig = ReadU32();
                if (sig == SigEnd) break;
                if (sig == SigSplit || sig == SigSolid || sig == SigSkip || sig == SigGlobalEnc)
                {
                    uint size = ExtraField(out _);
                    Skip(size);
                    if (sig == SigSolid) Solid = true;
                }
                else throw new ArchiveException("EGG 파일이 손상되었습니다.");
            }

            while (_s.Position < _s.Length)
            {
                uint sig;
                try { sig = ReadU32(); } catch (InvalidDataException) { break; }
                if (sig == SigFile) _items.Add(ParseFile());
                else if (sig == SigComment || sig == SigDummy) Skip(ExtraField(out _));
                else if (sig == SigEnd)
                {
                    if (_s.Position + 4 > _s.Length) break;
                    uint next = ReadU32();
                    _s.Position -= 4;
                    if (next != SigFile && next != SigComment && next != SigDummy) break;
                }
                else throw new ArchiveException("EGG 파일이 손상되었습니다.");
            }

            // relative paths
            var byId = new Dictionary<uint, Item>();
            foreach (var it in _items) byId[it.FileId] = it;
            var resolved = new Dictionary<Item, string>();
            foreach (var it in _items)
            {
                if (it.ParentId.HasValue && byId.TryGetValue(it.ParentId.Value, out var p) && p != it && p.Name.Trim('\\').Length > 0)
                    resolved[it] = p.Name.TrimEnd('\\') + "\\" + it.Name;
            }
            foreach (var kv in resolved) kv.Key.Name = kv.Value;

            foreach (var it in _items)
            {
                ulong packed = 0;
                foreach (var b in it.Blocks) packed += b.Packed;
                var e = new ArchiveEntry
                {
                    Index = it.Index, Path = it.Name, IsDir = it.IsDir, Size = (ulong)Math.Max(0, it.Size), PackedSize = packed,
                    Attributes = (uint)(it.Attr & 0x07) | (it.IsDir ? 0x10u : 0), Encrypted = it.EncData != null,
                    Method = string.Join(" ", it.Blocks.Select(b => b.Method < MethodNames.Length ? MethodNames[b.Method] : "?").Distinct()),
                    Crc = it.Blocks.Count == 1 ? (uint?)it.Blocks[0].Crc : null
                };
                if (it.FileTime.HasValue && it.FileTime.Value > 0)
                {
                    try { e.MTime = DateTime.FromFileTimeUtc(it.FileTime.Value); } catch { }
                }
                int slash = e.Path.LastIndexOf('\\');
                e.Name = slash >= 0 ? e.Path.Substring(slash + 1) : e.Path;
                Entries.Add(e);
            }
        }

        private Item ParseFile()
        {
            var it = new Item { Index = _items.Count, FileId = ReadU32(), Size = (long)Math.Min(BitConverter.ToUInt64(ReadN(8), 0), long.MaxValue), Name = "" };
            while (true)
            {
                uint sig = ReadU32();
                if (sig == SigEnd) break;
                uint size = ExtraField(out byte flags);
                switch (sig)
                {
                    case SigName:
                    {
                        long remaining = size;
                        Encoding enc = Encoding.UTF8;
                        if ((flags & 0x08) != 0)
                        {
                            ushort locale = BitConverter.ToUInt16(ReadN(2), 0);
                            remaining -= 2;
                            enc = locale == 932 ? SafeEncoding(932) : Cp949;
                        }
                        if ((flags & 0x10) != 0) { it.ParentId = ReadU32(); remaining -= 4; }
                        if (remaining < 0 || remaining > 65536) throw new ArchiveException("EGG 파일 이름이 손상되었습니다.");
                        var nb = ReadN((int)remaining);
                        it.Name = Normalize((flags & 0x08) != 0 ? enc.GetString(nb) : DecodeName(nb, Cp949));
                        break;
                    }
                    case SigWinInfo:
                        if (size < 9) throw new ArchiveException("EGG 파일이 손상되었습니다.");
                        it.FileTime = (long)BitConverter.ToUInt64(ReadN(8), 0);
                        it.Attr |= ReadU8();
                        Skip(size - 9);
                        break;
                    case SigPosix:
                    {
                        if (size < 20) throw new ArchiveException("EGG 파일이 손상되었습니다.");
                        var p = ReadN(20);
                        uint mode = U32(p, 0);
                        ulong mtime = BitConverter.ToUInt64(p, 12);
                        if ((mode & 0xF000) == 0x4000) it.Attr |= 0x80;
                        if (!it.FileTime.HasValue) it.FileTime = (long)((mtime + 11644473600UL) * 10000000UL);
                        Skip(size - 20);
                        break;
                    }
                    case SigEncrypt:
                    {
                        if (size == 0) throw new ArchiveException("EGG 암호 정보가 손상되었습니다.");
                        it.EncMethod = ReadU8();
                        int[] lens;
                        switch (it.EncMethod)
                        {
                            case 0: lens = new[] { 16 }; break;
                            case 1: case 5: lens = new[] { 20, 10 }; break;
                            case 2: case 6: lens = new[] { 28, 18 }; break;
                            default: throw new ArchiveException("지원하지 않는 EGG 암호 방식입니다 (" + it.EncMethod + ").");
                        }
                        int declared = (int)size - 1;
                        long start = _s.Position;
                        bool LandsOnEnd(int l)
                        {
                            _s.Position = start + l;
                            bool ok = _s.Position + 4 <= _s.Length && ReadU32() == SigEnd;
                            _s.Position = start;
                            return ok;
                        }
                        int use = declared;
                        if (!(lens.Contains(declared) && LandsOnEnd(declared)))
                            foreach (var l in lens) if (LandsOnEnd(l)) { use = l; break; }
                        it.EncData = ReadN(use);
                        break;
                    }
                    default:
                        Skip(size); // comment and unknown sub-headers
                        break;
                }
            }

            while (true)
            {
                if (_s.Position + 4 > _s.Length) break;
                uint sig = ReadU32();
                if (sig == SigBlock)
                {
                    var h = ReadN(14);
                    if (ReadU32() != SigEnd) throw new ArchiveException("EGG 블록이 손상되었습니다.");
                    var b = new Block { Method = h[0], Size = U32(h, 2), Packed = U32(h, 6), Crc = U32(h, 10), DataPos = _s.Position };
                    Skip(b.Packed);
                    it.Blocks.Add(b);
                }
                else if (sig == SigComment || sig == SigFile || sig == SigEnd || sig == SigDummy) { _s.Position -= 4; break; }
                else throw new ArchiveException("EGG 파일이 손상되었습니다.");
            }
            return it;
        }

        private static Encoding SafeEncoding(int cp) { try { return Encoding.GetEncoding(cp); } catch { return Encoding.Default; } }

        /// <summary>Creates the decryptor for an item (asking for the password as needed). Throws when cancelled.</summary>
        private IDecryptor Decryptor(Item it, ManagedExtractContext ctx)
        {
            if (it.EncData == null) return null;
            var d = it.EncData;
            IDecryptor r = WithPassword<IDecryptor>(ctx, p =>
            {
                byte[] pw = Encoding.UTF8.GetBytes(p);
                switch (it.EncMethod)
                {
                    case 0:
                    {
                        if (d.Length < 16) return null;
                        var z = new ZipCrypto(pw);
                        var hdr = new byte[12];
                        Buffer.BlockCopy(d, 0, hdr, 0, 12);
                        return z.CheckHeader(hdr) == (byte)(U32(d, 12) >> 24) ? z : null;
                    }
                    case 1: case 2: case 5: case 6:
                    {
                        int saltSize = it.EncMethod == 1 || it.EncMethod == 5 ? 8 : 16;
                        int keySize = saltSize == 8 ? 16 : 32;
                        if (d.Length < saltSize + 2) return null;
                        var salt = new byte[saltSize];
                        Buffer.BlockCopy(d, 0, salt, 0, saltSize);
                        var ver = new[] { d[saltSize], d[saltSize + 1] };
                        return CtrDecryptor.Create(it.EncMethod >= 5, keySize, pw, salt, ver);
                    }
                    default: return null;
                }
            });
            if (r == null) throw new PasswordCancelled();
            return r;
        }

        private sealed class PasswordCancelled : Exception { }

        private static void DecodeBlock(byte method, Stream input, Stream output, long outSize, Action<int> progress)
        {
            switch (method)
            {
                case 0: Copy(input, output); break;
                case 1: using (var d = new DeflateStream(input, CompressionMode.Decompress, true)) Copy(d, output); break;
                case 2: Bzip2Decoder.DecodeStandard(input, output); break;
                case 3: AzoDecoder.Decode(input, output); break;
                case 4:
                {
                    var hdr = new byte[9];
                    AzoDecoder.ReadExact(input, hdr, 9);
                    var props = new byte[5];
                    Buffer.BlockCopy(hdr, 4, props, 0, 5);
                    new LzmaDecoder(props).Decode(input, output, outSize);
                    break;
                }
                default: throw new InvalidDataException("지원하지 않는 압축 방식 (" + method + ")");
            }
        }

        public override void Extract(ISet<int> wanted, ManagedExtractContext ctx)
        {
            if (Solid) { ExtractSolid(wanted, ctx); return; }
            foreach (var it in _items)
            {
                if (!wanted.Contains(it.Index)) continue;
                try
                {
                    var output = ctx.Open(it.Index);
                    if (it.IsDir || it.Blocks.Count == 0) { ctx.Done(it.Index, it.Size != 0 && !it.IsDir ? "데이터 블록이 없습니다 (손상된 파일)" : null, false); continue; }
                    IDecryptor dec;
                    try { dec = Decryptor(it, ctx); }
                    catch (PasswordCancelled) { ctx.Done(it.Index, "암호를 입력하지 않았습니다", true); continue; }
                    var crc = new CrcStream(output) { Progress = ctx.Progress };
                    string err = null;
                    foreach (var b in it.Blocks)
                    {
                        crc.ResetCrc();
                        long before = crc.Count;
                        crc.Limit = before + b.Size;
                        DecodeBlock(b.Method, new BufferedStream(new SegmentStream(_s, b.DataPos, b.Packed, dec), 1 << 16), crc, b.Size, ctx.Progress);
                        if (crc.Crc != b.Crc || crc.Count - before != b.Size)
                        {
                            err = it.EncData != null ? "CRC 오류 (암호가 틀렸거나 파일 손상)" : "CRC 오류 (파일 손상)";
                            break;
                        }
                    }
                    (dec as IDisposable)?.Dispose();
                    ctx.Done(it.Index, err, err != null && it.EncData != null);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (ex is InvalidDataException || ex is EndOfStreamException || ex is IOException && !(ex is FileNotFoundException))
                {
                    ctx.Done(it.Index, ex.Message, it.EncData != null);
                }
            }
        }

        private void ExtractSolid(ISet<int> wanted, ManagedExtractContext ctx)
        {
            foreach (var it in _items.Where(i => i.IsDir && wanted.Contains(i.Index)))
            {
                ctx.Open(it.Index);
                ctx.Done(it.Index, null, false);
            }
            var files = _items.Where(i => !i.IsDir).ToList();
            var blocks = files.SelectMany(f => f.Blocks).ToList();
            if (blocks.Count == 0)
            {
                foreach (var f in files.Where(f => wanted.Contains(f.Index))) { ctx.Open(f.Index); ctx.Done(f.Index, null, false); }
                return;
            }
            var sink = new SolidSink(files, wanted, ctx);
            IEnumerable<Stream> Parts()
            {
                foreach (var f in files)
                {
                    if (f.Blocks.Count == 0) continue;
                    var dec = Decryptor(f, ctx);
                    foreach (var b in f.Blocks) yield return new SegmentStream(_s, b.DataPos, b.Packed, dec);
                }
            }
            long total = blocks.Sum(b => (long)b.Size);
            try
            {
                DecodeBlock(blocks[0].Method, new BufferedStream(new ChainStream(Parts()), 1 << 16), sink, total, ctx.Progress);
                sink.Finish();
            }
            catch (PasswordCancelled) { sink.Fail("암호를 입력하지 않았습니다", true); }
            catch (OperationCanceledException) { sink.Fail(null, false); throw; }
            catch (Exception ex) when (ex is InvalidDataException || ex is EndOfStreamException || ex is IOException && !(ex is FileNotFoundException))
            {
                sink.Fail(ex.Message, files.Any(f => f.EncData != null));
            }
        }

        /// <summary>Splits the single solid output stream back into files and checks each block CRC.</summary>
        private sealed class SolidSink : Stream
        {
            private readonly List<Item> _files;
            private readonly ISet<int> _wanted;
            private readonly ManagedExtractContext _ctx;
            private readonly List<KeyValuePair<long, uint>> _crcSpans = new List<KeyValuePair<long, uint>>();
            private int _fi, _ci;
            private long _fileWritten, _crcWritten;
            private bool _open;
            private readonly CrcStream _crc = new CrcStream(null);
            private string _blockError;

            public SolidSink(List<Item> files, ISet<int> wanted, ManagedExtractContext ctx)
            {
                _files = files; _wanted = wanted; _ctx = ctx;
                foreach (var f in files) foreach (var b in f.Blocks) _crcSpans.Add(new KeyValuePair<long, uint>(b.Size, b.Crc));
            }

            private void EnsureOpen()
            {
                if (_open) return;
                _open = true;
                var f = _files[_fi];
                _crc.Retarget(_wanted.Contains(f.Index) ? _ctx.Open(f.Index) : null);
            }

            private void CloseCurrent(string err)
            {
                var f = _files[_fi];
                if (_wanted.Contains(f.Index)) _ctx.Done(f.Index, err, err != null && f.EncData != null);
                _crc.Retarget(null);
                _open = false;
                _fi++;
                _fileWritten = 0;
            }

            private void AdvanceFull()
            {
                while (_fi < _files.Count && _fileWritten >= _files[_fi].Size)
                {
                    EnsureOpen();
                    CloseCurrent(_blockError);
                    _blockError = null;
                }
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                while (count > 0)
                {
                    AdvanceFull();
                    if (_fi >= _files.Count || _ci >= _crcSpans.Count) throw new InvalidDataException("압축 해제 크기가 기록과 다릅니다 (손상된 파일)");
                    EnsureOpen();
                    long fileNeed = _files[_fi].Size - _fileWritten, crcNeed = _crcSpans[_ci].Key - _crcWritten;
                    int take = (int)Math.Min(count, Math.Min(fileNeed, crcNeed));
                    _crc.Write(buffer, offset, take);
                    _ctx.Progress?.Invoke(take);
                    _fileWritten += take; _crcWritten += take;
                    offset += take; count -= take;
                    if (_crcWritten == _crcSpans[_ci].Key)
                    {
                        if (_crc.Crc != _crcSpans[_ci].Value) throw new InvalidDataException(_files[_fi].EncData != null ? "CRC 오류 (암호가 틀렸거나 파일 손상)" : "CRC 오류 (파일 손상)");
                        _crc.ResetCrc();
                        _ci++;
                        _crcWritten = 0;
                    }
                }
            }

            public void Finish()
            {
                AdvanceFull();
                if (_fi != _files.Count) throw new InvalidDataException("압축 해제 크기가 기록과 다릅니다 (손상된 파일)");
            }

            public void Fail(string err, bool pwd)
            {
                while (_fi < _files.Count)
                {
                    var f = _files[_fi];
                    if (_wanted.Contains(f.Index))
                    {
                        if (!_open) { _open = true; _crc.Retarget(null); }
                        if (err != null) _ctx.Done(f.Index, err, pwd);
                    }
                    _open = false;
                    _fi++;
                    err = err == null ? null : "앞선 파일 오류로 풀 수 없음 (솔리드 압축)";
                }
            }

            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }

        public override void Dispose() => _s?.Dispose();

        /// <summary>EGG split set: volumes are linked by header IDs; continuation volumes skip their own header.</summary>
        private sealed class EggVolumes : Stream
        {
            private readonly List<FileStream> _files = new List<FileStream>();
            private readonly List<long> _phys = new List<long>(), _start = new List<long>(), _size = new List<long>();
            private long _pos, _total;
            public int Count => _files.Count;
            public long PhysicalSize;

            public static EggVolumes Open(string path)
            {
                var v = new EggVolumes();
                try
                {
                    string dir = Path.GetDirectoryName(Path.GetFullPath(path));
                    var info = ReadInfo(path);
                    // walk back to the first volume when a later one was opened
                    var seen = new HashSet<uint>();
                    string first = path;
                    while (info.prev != 0 && seen.Add(info.id))
                    {
                        string p = FindById(dir, info.prev);
                        if (p == null) throw new ArchiveException("앞쪽 분할 파일을 찾을 수 없습니다.");
                        first = p; info = ReadInfo(p);
                    }
                    seen.Clear();
                    string cur = first;
                    while (cur != null)
                    {
                        var ri = ReadInfo(cur);
                        if (!seen.Add(ri.id)) throw new ArchiveException("EGG 분할 파일 연결이 잘못되었습니다.");
                        var fs = new FileStream(cur, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
                        long dataStart = v._files.Count == 0 ? 0 : ri.dataStart;
                        v._files.Add(fs);
                        v._phys.Add(dataStart);
                        v._start.Add(v._total);
                        v._size.Add(fs.Length - dataStart);
                        v._total += fs.Length - dataStart;
                        v.PhysicalSize += fs.Length;
                        if (ri.next == 0) break;
                        cur = FindById(dir, ri.next);
                        if (cur == null) throw new ArchiveException("다음 분할 파일을 찾을 수 없습니다 (" + (v._files.Count + 1) + "번째).");
                    }
                }
                catch { v.Dispose(); throw; }
                return v;
            }

            private struct VolInfo { public uint id, prev, next; public long dataStart; }

            private static VolInfo ReadInfo(string path)
            {
                using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    var r = new BinaryReader(f);
                    if (r.ReadUInt32() != SigEgg) throw new ArchiveException("EGG 파일이 아닙니다: " + Path.GetFileName(path));
                    r.ReadUInt16();
                    var info = new VolInfo { id = r.ReadUInt32() };
                    r.ReadUInt32();
                    while (true)
                    {
                        uint sig = r.ReadUInt32();
                        if (sig == SigEnd) break;
                        byte flags = r.ReadByte();
                        uint size = (flags & 1) != 0 ? r.ReadUInt32() : r.ReadUInt16();
                        long next = f.Position + size;
                        if (sig == SigSplit && size >= 8) { info.prev = r.ReadUInt32(); info.next = r.ReadUInt32(); }
                        f.Position = next;
                    }
                    info.dataStart = f.Position;
                    return info;
                }
            }

            private static string FindById(string dir, uint id)
            {
                foreach (var f in Directory.GetFiles(dir))
                {
                    try
                    {
                        using (var s = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                        {
                            var b = new byte[10];
                            if (s.Read(b, 0, 10) == 10 && BitConverter.ToUInt32(b, 0) == SigEgg && BitConverter.ToUInt32(b, 6) == id) return f;
                        }
                    }
                    catch { }
                }
                return null;
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                int total = 0;
                while (count > 0 && _pos < _total)
                {
                    int i = _start.Count - 1;
                    while (i > 0 && _start[i] > _pos) i--;
                    long local = _pos - _start[i];
                    int want = (int)Math.Min(count, _size[i] - local);
                    if (want <= 0) { _pos = _start[i] + _size[i]; continue; }
                    _files[i].Position = _phys[i] + local;
                    int n = _files[i].Read(buffer, offset, want);
                    if (n <= 0) break;
                    total += n; offset += n; count -= n; _pos += n;
                }
                return total;
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                _pos = origin == SeekOrigin.Begin ? offset : origin == SeekOrigin.Current ? _pos + offset : _total + offset;
                return _pos;
            }

            public override bool CanRead => true;
            public override bool CanSeek => true;
            public override bool CanWrite => false;
            public override long Length => _total;
            public override long Position { get => _pos; set => _pos = value; }
            public override void Flush() { }
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            protected override void Dispose(bool disposing) { if (disposing) foreach (var f in _files) f.Dispose(); base.Dispose(disposing); }
        }
    }
}
