using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ArcBox.Core.Korean
{
    /// <summary>PEA archives (PeaZip's own format, file format 1.x), written from the published specification and
    /// PeaZip's pea.pas (LGPL). Plain (non-encrypted) streams: stored or zlib-compressed, single or multi volume,
    /// with the object checks verified (CRC32, Adler32, MD5, SHA-1/256/512).</summary>
    internal sealed class PeaArchive : ManagedArchive
    {
        private const int WBufSize = 1048576;

        private sealed class Item
        {
            public int Index;
            public long DataPos;     // position of the first data byte (or first compsize field)
            public long Size;
            public bool IsDir;
            public byte[] HeaderBytes; // fns + name + age + attr (+ size), fed to the object check first
            public byte[] Tag;
        }

        private readonly List<Item> _items = new List<Item>();
        private PeaVolumes _vol;
        private bool _compressed;
        private string _objAlgo;
        private int _objAuth;

        public static bool Looks(byte[] h, int len) =>
            len >= 20 && h[0] == 0xEA && h[1] == 1 && h[10] == 0 && h[11] == 0 && h[12] == 'P' && h[13] == 'O' && h[14] == 'D' && h[15] == 0;

        public static PeaArchive Open(string path)
        {
            var a = new PeaArchive { FormatName = "PEA" };
            try { a.Parse(path); } catch { a.Dispose(); throw; }
            return a;
        }

        public override void Dispose() => _vol?.Dispose();

        private static string AlgoName(int b)
        {
            switch (b)
            {
                case 0: return "NOALGO"; case 1: return "ADLER32"; case 2: return "CRC32"; case 3: return "CRC64";
                case 16: return "MD5"; case 17: return "RIPEMD160"; case 18: return "SHA1"; case 19: return "SHA256";
                case 20: return "SHA512"; case 21: return "WHIRLPOOL"; case 22: return "SHA3_256"; case 23: return "SHA3_512";
                case 24: return "BLAKE2S"; case 25: return "BLAKE2B";
                default: return null;
            }
        }

        private static int AuthSize(string a)
        {
            switch (a)
            {
                case "NOALGO": return 0;
                case "ADLER32": case "CRC32": return 4;
                case "CRC64": return 8;
                case "MD5": return 16;
                case "RIPEMD160": case "SHA1": return 20;
                case "SHA256": case "SHA3_256": case "BLAKE2S": return 32;
                default: return 64;
            }
        }

        private void Parse(string path)
        {
            var head = new byte[20];
            using (var f = File.OpenRead(path)) AzoDecoder.ReadExact(f, head, 20);
            if (!Looks(head, 20)) throw new ArchiveException("PEA 파일이 아니거나 손상되었습니다.");
            if (head[2] > 6) throw new ArchiveException("이 PEA 파일은 더 새로운 형식 버전(1." + head[2] + ")으로 만들어졌습니다.");
            string volAlgo = AlgoName(head[3]) ?? throw new ArchiveException("알 수 없는 PEA 볼륨 검사 방식입니다.");
            int volAuth = AuthSize(volAlgo);
            byte timeEnc = head[6];
            int compr = head[16];
            if (compr > 3) throw new ArchiveException("알 수 없는 PEA 압축 방식입니다.");
            _compressed = compr != 0;
            int algo = head[18];
            if (algo >= 48)
                throw new ArchiveException("암호화된 PEA 파일은 아직 지원하지 않습니다.\n(PeaZip의 AES/Twofish/Serpent 암호화 방식)");
            string streamAlgo = AlgoName(algo) ?? throw new ArchiveException("알 수 없는 PEA 검사 방식입니다.");
            _objAlgo = AlgoName(head[19]) ?? throw new ArchiveException("알 수 없는 PEA 검사 방식입니다.");
            _objAuth = AuthSize(_objAlgo);

            _vol = new PeaVolumes(path, volAuth);
            VolumeCount = _vol.Count;
            PhysicalSize = _vol.PhysicalSize;
            var s = _vol;
            s.Position = 20;
            if (_compressed)
            {
                if (ReadU32(s) != WBufSize) throw new ArchiveException("PEA 압축 버퍼 크기가 잘못되었습니다 (손상된 파일).");
            }
            var buf = new byte[8];
            while (true)
            {
                int fns = ReadU16(s);
                if (fns == 0)
                {
                    AzoDecoder.ReadExact(s, buf, 4);
                    if (buf[0] != 'E' || buf[1] != 'O' || buf[2] != 'A' || buf[3] != 0) throw new ArchiveException("PEA 파일이 손상되었습니다 (알 수 없는 트리거).");
                    break;
                }
                var hdr = new byte[2 + fns + 8];
                hdr[0] = (byte)fns; hdr[1] = (byte)(fns >> 8);
                ReadInto(s, hdr, 2, fns + 8);
                uint age = U32(hdr, 2 + fns), attr = U32(hdr, 6 + fns);
                var it = new Item { Index = _items.Count, IsDir = (attr & 0x10) != 0 };
                string name = DecodeName(Sub(hdr, 2, fns), Cp949).Replace('/', '\\');
                if (name.Length >= 2 && name[1] == ':') name = name.Substring(2);
                name = Normalize(name.TrimStart('\\'));
                long packed = 0;
                if (!it.IsDir)
                {
                    var full = new byte[hdr.Length + 8];
                    Buffer.BlockCopy(hdr, 0, full, 0, hdr.Length);
                    ReadInto(s, full, hdr.Length, 8);
                    hdr = full;
                    it.Size = (long)BitConverter.ToUInt64(full, full.Length - 8);
                    if (it.Size < 0) throw new ArchiveException("PEA 파일이 손상되었습니다.");
                    it.DataPos = s.Position;
                    if (it.Size > 0)
                    {
                        if (_compressed)
                        {
                            long left = it.Size;
                            uint cs = ReadU32(s);
                            while (left > 0)
                            {
                                if (cs > WBufSize) throw new ArchiveException("PEA 파일이 손상되었습니다 (블록 크기).");
                                s.Position += cs;
                                left -= Math.Min(left, WBufSize);
                                cs = ReadU32(s);
                            }
                        }
                        else s.Position += it.Size;
                    }
                    packed = s.Position - it.DataPos;
                }
                it.HeaderBytes = hdr;
                it.Tag = new byte[_objAuth];
                ReadInto(s, it.Tag, 0, _objAuth);
                _items.Add(it);
                Entries.Add(new ArchiveEntry
                {
                    Index = it.Index, Path = name, Name = name, IsDir = it.IsDir, Size = (ulong)it.Size,
                    PackedSize = (ulong)packed, Attributes = attr & 0x3F, Method = it.IsDir ? "" : MethodName(compr),
                    MTime = timeEnc == 0x30 ? UnixToUtc(age) : AlzArchive.DosToUtc(age)
                });
            }
            _ = streamAlgo;
        }

        private static string MethodName(int c) => c == 0 ? "Store" : "Deflate:" + (c == 1 ? 3 : c == 2 ? 6 : 9);

        private static DateTime? UnixToUtc(uint t)
        {
            try { return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(t); } catch { return null; }
        }

        public override void Extract(ISet<int> wanted, ManagedExtractContext ctx)
        {
            foreach (var it in _items)
            {
                if (!wanted.Contains(it.Index)) continue;
                var check = new PeaCheck(_objAlgo);
                check.Update(it.HeaderBytes, 0, it.HeaderBytes.Length);
                if (it.IsDir)
                {
                    ctx.Done(it.Index, check.Matches(it.Tag) ? null : "검사값이 일치하지 않습니다 (헤더 손상)", false);
                    continue;
                }
                var output = ctx.Open(it.Index) ?? Stream.Null;
                try
                {
                    string err = Decode(it, output, check, ctx.Progress);
                    if (err == null && !check.Matches(it.Tag)) err = "검사값 오류 (파일 손상)";
                    ctx.Done(it.Index, err, false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (ex is InvalidDataException || ex is EndOfStreamException || ex is IOException && !(ex is FileNotFoundException))
                {
                    ctx.Done(it.Index, ex.Message, false);
                }
            }
        }

        private string Decode(Item it, Stream output, PeaCheck check, Action<int> progress)
        {
            var s = _vol;
            s.Position = it.DataPos;
            long left = it.Size;
            if (!_compressed)
            {
                var buf = new byte[1 << 16];
                while (left > 0)
                {
                    int n = (int)Math.Min(buf.Length, left);
                    AzoDecoder.ReadExact(s, buf, n);
                    check.Update(buf, 0, n);
                    output.Write(buf, 0, n);
                    progress?.Invoke(n);
                    left -= n;
                }
                return null;
            }
            var field = new byte[4];
            if (left == 0) return null;
            AzoDecoder.ReadExact(s, field, 4);
            check.Update(field, 0, 4);
            uint cs = U32(field, 0);
            var block = new byte[WBufSize];
            int last = 0;
            while (left > 0)
            {
                if (cs > WBufSize) return "PEA 블록 크기가 잘못되었습니다 (파일 손상)";
                int k = (int)Math.Min(left, WBufSize);
                AzoDecoder.ReadExact(s, block, (int)cs);
                byte[] plain;
                int plainLen;
                if (cs < k) { plain = Inflate(block, (int)cs, k); plainLen = plain.Length; }
                else { plain = block; plainLen = k; }
                if (plainLen != k) return "압축 해제 크기가 맞지 않습니다 (파일 손상)";
                check.Update(plain, 0, plainLen);
                output.Write(plain, 0, plainLen);
                progress?.Invoke(plainLen);
                left -= plainLen;
                last = plainLen;
                AzoDecoder.ReadExact(s, field, 4);
                check.Update(field, 0, 4);
                cs = U32(field, 0);
            }
            return cs == (uint)last ? null : "압축 해제 크기가 맞지 않습니다 (파일 손상)";
        }

        /// <summary>zlib (RFC 1950) data: 2-byte header + raw deflate + Adler-32.</summary>
        private static byte[] Inflate(byte[] src, int len, int expected)
        {
            if (len < 2 || (src[0] & 0x0F) != 8 || ((src[0] << 8) | src[1]) % 31 != 0) throw new InvalidDataException("PEA 압축 데이터가 손상되었습니다.");
            var outp = new byte[expected];
            using (var d = new DeflateStream(new MemoryStream(src, 2, len - 2), CompressionMode.Decompress))
            {
                int got = 0, n;
                while (got < expected && (n = d.Read(outp, got, expected - got)) > 0) got += n;
                if (got != expected) throw new InvalidDataException("PEA 압축 데이터가 손상되었습니다.");
            }
            return outp;
        }

        private static byte[] Sub(byte[] b, int o, int n) { var r = new byte[n]; Buffer.BlockCopy(b, o, r, 0, n); return r; }
        private static void ReadInto(Stream s, byte[] b, int o, int n)
        {
            while (n > 0) { int r = s.Read(b, o, n); if (r <= 0) throw new EndOfStreamException("PEA 파일이 잘렸습니다."); o += r; n -= r; }
        }
        private static int ReadU16(Stream s) { var b = new byte[2]; ReadInto(s, b, 0, 2); return b[0] | b[1] << 8; }
        private static uint ReadU32(Stream s) { var b = new byte[4]; ReadInto(s, b, 0, 4); return U32(b, 0); }

        // ------------------------------------------------------------------ test writer
        /// <summary>Writes a PEA 1.6 archive the way PeaZip does (non-encrypted). Used by the self test only.</summary>
        internal static void WriteForTests(string path, IList<(string name, byte[] data, bool dir)> items, int compr, int objAlgo, int volAlgo, long volumeSize)
        {
            var ms = new MemoryStream();
            void W(byte[] b) => ms.Write(b, 0, b.Length);
            W(new byte[] { 0xEA, 1, 6, (byte)volAlgo, 0x10, 0, 0x10, 0x82, 0, 0 });
            W(new byte[] { 0, 0, (byte)'P', (byte)'O', (byte)'D', 0, (byte)compr, 0, 2, (byte)objAlgo });
            if (compr != 0) W(BitConverter.GetBytes((uint)WBufSize));
            var stream = new PeaCheck("CRC32");
            string oa = AlgoName(objAlgo);
            uint dos = 0x5A6B_4C21;
            foreach (var (name, data, dir) in items)
            {
                var c = new PeaCheck(oa);
                var nb = Encoding.UTF8.GetBytes(name);
                var hdr = new MemoryStream();
                hdr.Write(BitConverter.GetBytes((ushort)nb.Length), 0, 2);
                hdr.Write(nb, 0, nb.Length);
                hdr.Write(BitConverter.GetBytes(dos), 0, 4);
                hdr.Write(BitConverter.GetBytes(dir ? 0x10u : 0x20u), 0, 4);
                if (!dir) hdr.Write(BitConverter.GetBytes((ulong)data.Length), 0, 8);
                var hb = hdr.ToArray();
                c.Update(hb, 0, hb.Length); W(hb);
                if (!dir && data.Length > 0)
                {
                    if (compr == 0) { c.Update(data, 0, data.Length); W(data); }
                    else
                    {
                        var blocks = new List<byte[]>();
                        for (int p = 0; p < data.Length; p += WBufSize)
                        {
                            int k = Math.Min(WBufSize, data.Length - p);
                            var z = ZlibForTests(data, p, k);
                            blocks.Add(z.Length < k ? z : Sub(data, p, k));
                        }
                        var f = BitConverter.GetBytes((uint)blocks[0].Length);
                        c.Update(f, 0, 4); W(f);
                        for (int i = 0; i < blocks.Count; i++)
                        {
                            int k = Math.Min(WBufSize, data.Length - i * WBufSize);
                            W(blocks[i]);
                            c.Update(data, i * WBufSize, k);
                            f = BitConverter.GetBytes((uint)(i + 1 < blocks.Count ? blocks[i + 1].Length : k));
                            c.Update(f, 0, 4); W(f);
                        }
                    }
                }
                W(c.Final());
            }
            W(new byte[] { 0, 0, (byte)'E', (byte)'O', (byte)'A', 0 });
            W(new byte[4]); // stream check (not verified by ArcBox)
            var all = ms.ToArray();
            int va = AuthSize(AlgoName(volAlgo));
            if (volumeSize <= 0)
            {
                using (var o = File.Create(path)) { o.Write(all, 0, all.Length); var vc = new PeaCheck(AlgoName(volAlgo)); vc.Update(all, 0, all.Length); o.Write(vc.Final(), 0, va); }
                return;
            }
            string bas = path.EndsWith(".pea", StringComparison.OrdinalIgnoreCase) ? path.Substring(0, path.Length - 4) : path;
            int vol = 1;
            for (long p = 0; p < all.Length; p += volumeSize, vol++)
            {
                int n = (int)Math.Min(volumeSize, all.Length - p);
                using (var o = File.Create(bas + "." + vol.ToString("000000") + ".pea"))
                {
                    o.Write(all, (int)p, n);
                    var vc = new PeaCheck(AlgoName(volAlgo)); vc.Update(all, (int)p, n); o.Write(vc.Final(), 0, va);
                }
            }
        }

        private static byte[] ZlibForTests(byte[] data, int off, int len)
        {
            var ms = new MemoryStream();
            ms.WriteByte(0x78); ms.WriteByte(0x9C);
            using (var d = new DeflateStream(ms, CompressionLevel.Optimal, true)) d.Write(data, off, len);
            var a = new PeaCheck("ADLER32"); a.Update(data, off, len);
            var t = a.Final(); Array.Reverse(t); // zlib stores Adler-32 big endian
            ms.Write(t, 0, 4);
            return ms.ToArray();
        }
    }

    /// <summary>Object / volume check value as PeaZip computes it (little endian for CRC32/Adler32).</summary>
    internal sealed class PeaCheck
    {
        private static readonly uint[] Table = BuildTable();
        private readonly string _algo;
        private uint _crc = 0xFFFFFFFF, _a = 1, _b = 0;
        private readonly HashAlgorithm _hash;

        public PeaCheck(string algo)
        {
            _algo = algo;
            switch (algo)
            {
                case "MD5": _hash = MD5.Create(); break;
                case "SHA1": _hash = SHA1.Create(); break;
                case "SHA256": _hash = SHA256.Create(); break;
                case "SHA512": _hash = SHA512.Create(); break;
            }
        }

        /// <summary>False for algorithms ArcBox cannot compute (their tags are then not verified).</summary>
        public bool Supported => _algo == "NOALGO" || _algo == "CRC32" || _algo == "ADLER32" || _hash != null;

        public void Update(byte[] b, int o, int n)
        {
            if (_algo == "CRC32") { uint c = _crc; for (int i = o; i < o + n; i++) c = Table[(c ^ b[i]) & 0xFF] ^ (c >> 8); _crc = c; }
            else if (_algo == "ADLER32") { for (int i = o; i < o + n; i++) { _a = (_a + b[i]) % 65521; _b = (_b + _a) % 65521; } }
            else _hash?.TransformBlock(b, o, n, null, 0);
        }

        public byte[] Final()
        {
            if (_algo == "CRC32") return BitConverter.GetBytes(~_crc);
            if (_algo == "ADLER32") return BitConverter.GetBytes(_b << 16 | _a);
            if (_hash != null) { _hash.TransformFinalBlock(new byte[0], 0, 0); return _hash.Hash; }
            return new byte[0];
        }

        public bool Matches(byte[] tag)
        {
            if (!Supported) return true;
            var f = Final();
            if (f.Length != tag.Length) return f.Length == 0;
            int diff = 0;
            for (int i = 0; i < f.Length; i++) diff |= f[i] ^ tag[i];
            return diff == 0;
        }

        private static uint[] BuildTable()
        {
            var t = new uint[256];
            for (uint i = 0; i < 256; i++) { uint c = i; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1; t[i] = c; }
            return t;
        }
    }

    /// <summary>Joins "name.000001.pea", "name.000002.pea", … dropping each volume's trailing check field.</summary>
    internal sealed class PeaVolumes : Stream
    {
        private readonly List<FileStream> _parts = new List<FileStream>();
        private readonly List<long> _starts = new List<long>(), _lens = new List<long>();
        private long _length, _pos;
        public long PhysicalSize;
        public int Count => _parts.Count;

        public PeaVolumes(string path, int volAuth)
        {
            var m = Regex.Match(path, @"^(.*)\.(\d{6})\.pea$", RegexOptions.IgnoreCase);
            var files = new List<string>();
            if (m.Success)
            {
                for (int i = 1; i < 1000000; i++)
                {
                    string f = m.Groups[1].Value + "." + i.ToString("000000") + ".pea";
                    if (!File.Exists(f)) break;
                    files.Add(f);
                }
                if (files.Count == 0) files.Add(path);
            }
            else files.Add(path);
            foreach (var f in files)
            {
                var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
                long len = Math.Max(0, fs.Length - volAuth);
                _parts.Add(fs); _starts.Add(_length); _lens.Add(len);
                _length += len; PhysicalSize += fs.Length;
            }
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position { get => _pos; set => _pos = value; }
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin)
        {
            _pos = origin == SeekOrigin.Begin ? offset : origin == SeekOrigin.Current ? _pos + offset : _length + offset;
            return _pos;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int total = 0;
            while (count > 0 && _pos < _length)
            {
                int i = _starts.BinarySearch(_pos);
                if (i < 0) i = ~i - 1;
                while (i < _parts.Count - 1 && _pos >= _starts[i] + _lens[i]) i++;
                long inPart = _pos - _starts[i];
                int n = (int)Math.Min(count, _lens[i] - inPart);
                if (n <= 0) break;
                _parts[i].Position = inPart;
                n = _parts[i].Read(buffer, offset, n);
                if (n <= 0) break;
                _pos += n; offset += n; count -= n; total += n;
            }
            return total;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) foreach (var p in _parts) p.Dispose();
            base.Dispose(disposing);
        }
    }
}
