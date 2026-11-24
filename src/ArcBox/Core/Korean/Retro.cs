using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace ArcBox.Core.Korean
{
    /// <summary>Classic DOS-era codecs: RLE90, Huffman "squeeze", Unix-compress style LZW (ARC), ZOO LZW.</summary>
    internal static class RetroCodecs
    {
        /// <summary>ARC "packed" run-length encoding: 0x90 n repeats the previous byte (n-1) more times; 0x90 0 is a literal 0x90.</summary>
        public sealed class Rle90Stream : Stream
        {
            private readonly Stream _t;
            private bool _esc;
            private int _last = -1;
            public Rle90Stream(Stream t) { _t = t; }
            public override void Write(byte[] buffer, int offset, int count)
            {
                var o = new MemoryStream(count + 16);
                for (int i = offset; i < offset + count; i++)
                {
                    byte b = buffer[i];
                    if (_esc)
                    {
                        _esc = false;
                        if (b == 0) { o.WriteByte(0x90); _last = 0x90; }
                        else if (_last >= 0) for (int k = 1; k < b; k++) o.WriteByte((byte)_last);
                    }
                    else if (b == 0x90) _esc = true;
                    else { o.WriteByte(b); _last = b; }
                }
                _t.Write(o.GetBuffer(), 0, (int)o.Length);
            }
            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() => _t.Flush();
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }

        /// <summary>ARC "squeezed": Huffman tree (LSB-first bits) followed by RLE90 data.</summary>
        public static void Unsqueeze(Stream input, Stream output)
        {
            var br = new BinaryReader(input);
            int numNodes = br.ReadUInt16();
            if (numNodes > 256) throw new InvalidDataException("squeeze 트리가 손상되었습니다");
            var nodes = new short[Math.Max(1, numNodes), 2];
            for (int i = 0; i < numNodes; i++) { nodes[i, 0] = br.ReadInt16(); nodes[i, 1] = br.ReadInt16(); }
            var rle = new Rle90Stream(output);
            if (numNodes == 0) return;
            var buf = new byte[1 << 14];
            int op = 0, node = 0, cur = 0, bits = 0;
            while (true)
            {
                if (bits == 0) { cur = input.ReadByte(); if (cur < 0) break; bits = 8; }
                int bit = cur & 1; cur >>= 1; bits--;
                int child = nodes[node, bit];
                if (child < 0)
                {
                    int sym = -(child + 1);
                    if (sym == 256) break;
                    buf[op++] = (byte)sym;
                    if (op == buf.Length) { rle.Write(buf, 0, op); op = 0; }
                    node = 0;
                }
                else
                {
                    if (child >= numNodes) throw new InvalidDataException("squeeze 데이터가 손상되었습니다");
                    node = child;
                }
            }
            if (op > 0) rle.Write(buf, 0, op);
        }

        /// <summary>LZW as in Unix compress 4.0 (used by ARC "crunched" method 8 and "squashed" method 9).</summary>
        public static void UnixLzw(Stream input, Stream output, int maxBits)
        {
            const int Clear = 256, First = 257, InitBits = 9;
            if (maxBits < 9 || maxBits > 16) throw new InvalidDataException("LZW 비트 수가 올바르지 않습니다");
            int maxMaxCode = 1 << maxBits;
            var prefix = new int[maxMaxCode];
            var suffix = new byte[maxMaxCode];
            for (int i = 0; i < 256; i++) suffix[i] = (byte)i;
            var stack = new byte[maxMaxCode + 1];

            int nBits = InitBits, maxCode = (1 << nBits) - 1, freeEnt = First;
            bool clearFlag = false;
            var gbuf = new byte[16];
            int offset = 0, size = 0;

            int GetCode()
            {
                if (clearFlag || offset >= size || freeEnt > maxCode)
                {
                    if (freeEnt > maxCode)
                    {
                        nBits++;
                        maxCode = nBits == maxBits ? maxMaxCode : (1 << nBits) - 1;
                    }
                    if (clearFlag) { nBits = InitBits; maxCode = (1 << nBits) - 1; clearFlag = false; }
                    int got = 0;
                    while (got < nBits) { int r = input.Read(gbuf, got, nBits - got); if (r <= 0) break; got += r; }
                    if (got <= 0) return -1;
                    offset = 0;
                    size = (got << 3) - (nBits - 1);
                }
                int bitOff = offset;
                int code = 0;
                for (int i = 0; i < nBits; i++)
                {
                    int bp = bitOff + i;
                    if ((gbuf[bp >> 3] >> (bp & 7) & 1) != 0) code |= 1 << i;
                }
                offset += nBits;
                return code;
            }

            var outBuf = new byte[1 << 16];
            int op = 0;
            void Emit(byte b) { outBuf[op++] = b; if (op == outBuf.Length) { output.Write(outBuf, 0, op); op = 0; } }

            int oldCode = GetCode();
            if (oldCode == -1) return;
            int finChar = oldCode;
            Emit((byte)finChar);
            int code;
            while ((code = GetCode()) > -1)
            {
                if (code == Clear)
                {
                    clearFlag = true;
                    freeEnt = First - 1;
                    if ((code = GetCode()) == -1) break;
                }
                int inCode = code, sp = 0;
                if (code >= freeEnt)
                {
                    if (code > freeEnt) throw new InvalidDataException("LZW 데이터가 손상되었습니다");
                    stack[sp++] = (byte)finChar;
                    code = oldCode;
                }
                while (code >= 256)
                {
                    if (sp >= stack.Length) throw new InvalidDataException("LZW 데이터가 손상되었습니다");
                    stack[sp++] = suffix[code];
                    code = prefix[code];
                }
                finChar = suffix[code];
                stack[sp++] = (byte)finChar;
                while (sp > 0) Emit(stack[--sp]);
                if ((code = freeEnt) < maxMaxCode)
                {
                    prefix[code] = oldCode;
                    suffix[code] = (byte)finChar;
                    freeEnt = code + 1;
                }
                oldCode = inCode;
            }
            if (op > 0) output.Write(outBuf, 0, op);
        }

        /// <summary>ZOO method 1 (LZW, 9..13 bits, LSB-first, CLEAR=256, EOF=257).</summary>
        public static void ZooLzw(Stream input, Stream output)
        {
            const int MaxBits = 13, ClearCode = 256, EofCode = 257, FirstFree = 258;
            var next = new int[1 << MaxBits];
            var ch = new byte[1 << MaxBits];
            var stack = new byte[1 << MaxBits];
            int nbits = 9, maxCode = 512, freeCode = FirstFree;
            long bitBuf = 0; int bitCount = 0;

            int Rd()
            {
                while (bitCount < nbits)
                {
                    int b = input.ReadByte();
                    if (b < 0) return EofCode;
                    bitBuf |= (long)b << bitCount;
                    bitCount += 8;
                }
                int c = (int)(bitBuf & ((1 << nbits) - 1));
                bitBuf >>= nbits;
                bitCount -= nbits;
                return c;
            }

            var outBuf = new byte[1 << 16];
            int op = 0;
            void Emit(byte b) { outBuf[op++] = b; if (op == outBuf.Length) { output.Write(outBuf, 0, op); op = 0; } }

            int oldCode = 0, finChar = 0;
            while (true)
            {
                int cur = Rd();
                if (cur == EofCode) break;
                if (cur == ClearCode)
                {
                    nbits = 9; maxCode = 512; freeCode = FirstFree;
                    oldCode = cur = Rd();
                    if (cur == EofCode) break;
                    finChar = cur;
                    Emit((byte)cur);
                    continue;
                }
                int inCode = cur, sp = 0;
                if (cur >= freeCode)
                {
                    if (cur > freeCode) throw new InvalidDataException("ZOO 데이터가 손상되었습니다");
                    cur = oldCode;
                    stack[sp++] = (byte)finChar;
                }
                while (cur > 255)
                {
                    if (sp >= stack.Length) throw new InvalidDataException("ZOO 데이터가 손상되었습니다");
                    stack[sp++] = ch[cur];
                    cur = next[cur];
                }
                finChar = cur;
                stack[sp++] = (byte)cur;
                while (sp > 0) Emit(stack[--sp]);
                if (freeCode < (1 << MaxBits))
                {
                    ch[freeCode] = (byte)finChar;
                    next[freeCode] = oldCode;
                    freeCode++;
                    if (freeCode >= maxCode && nbits < MaxBits) { nbits++; maxCode <<= 1; }
                }
                oldCode = inCode;
            }
            if (op > 0) output.Write(outBuf, 0, op);
        }
    }

    // ================================================================== SEA ARC / PKPAK
    internal sealed class ArcArchive : ManagedArchive
    {
        private sealed class Item { public int Index, Method; public long Packed, Size, DataPos; public ushort Crc; }
        private readonly List<Item> _items = new List<Item>();
        private FileStream _f;

        public static bool Looks(byte[] h, int len, string path)
        {
            if (len < 29 || h[0] != 0x1A || h[1] == 0 || h[1] > 11) return false;
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".arc" || ext == ".ark" || ext == ".pak") return true;
            // filename field must be printable and NUL terminated
            for (int i = 2; i < 15; i++) { if (h[i] == 0) return i > 2; if (h[i] < 32) return false; }
            return false;
        }

        public static ArcArchive Open(string path)
        {
            var a = new ArcArchive { FormatName = "ARC" };
            a._f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
            try { a.Parse(); } catch { a.Dispose(); throw; }
            a.PhysicalSize = a._f.Length;
            return a;
        }

        private void Parse()
        {
            var enc = Encoding.GetEncoding(437);
            var h = new byte[27];
            while (_f.Position < _f.Length)
            {
                int mark = _f.ReadByte();
                if (mark != 0x1A) { if (_items.Count > 0) break; throw new ArchiveException("ARC 파일이 아니거나 손상되었습니다."); }
                int method = _f.ReadByte();
                if (method <= 0) break;
                int hlen = method == 1 ? 23 : 27;
                AzoDecoder.ReadExact(_f, h, hlen); // name(13)+size(4)+date(2)+time(2)+crc(2)[+orig(4)]
                int nameLen = Array.IndexOf(h, (byte)0, 0, 13); if (nameLen < 0) nameLen = 13;
                var it = new Item { Index = _items.Count, Method = method & 0x7F, Packed = BitConverter.ToUInt32(h, 13), Crc = BitConverter.ToUInt16(h, 21) };
                it.Size = method == 1 ? it.Packed : BitConverter.ToUInt32(h, 23);
                ushort date = BitConverter.ToUInt16(h, 17), time = BitConverter.ToUInt16(h, 19);
                it.DataPos = _f.Position;
                _f.Position += it.Packed;
                _items.Add(it);
                string name = enc.GetString(h, 0, nameLen);
                var e = new ArchiveEntry
                {
                    Index = it.Index, Path = Normalize(name), Size = (ulong)it.Size, PackedSize = (ulong)it.Packed, Crc = it.Crc,
                    Method = MethodName(it.Method), MTime = AlzArchive.DosToUtc((uint)(date << 16 | time))
                };
                e.Name = e.Path;
                Entries.Add(e);
            }
            if (_items.Count == 0) throw new ArchiveException("ARC 파일이 비어 있습니다.");
        }

        private static string MethodName(int m)
        {
            switch (m)
            {
                case 1: case 2: return "Stored";
                case 3: return "Packed";
                case 4: return "Squeezed";
                case 5: case 6: case 7: case 8: return "Crunched";
                case 9: return "Squashed";
                case 10: return "Crushed";
                case 11: return "Distilled";
                default: return "?" + m;
            }
        }

        public override void Extract(ISet<int> wanted, ManagedExtractContext ctx)
        {
            foreach (var it in _items)
            {
                if (!wanted.Contains(it.Index)) continue;
                var output = ctx.Open(it.Index);
                try
                {
                    // ARC has no encryption flag: a CRC mismatch may mean the entry is "garbled" with a password
                    string err = Decode(it, Password, Stream.Null, null);
                    if (err == "CRC")
                    {
                        string pw = WithPassword(ctx, p => Decode(it, p, Stream.Null, null) == null ? p : null);
                        if (pw == null) { ctx.Done(it.Index, "CRC 오류 (파일이 손상되었거나 암호가 걸려 있음)", true); continue; }
                    }
                    else if (err != null) { ctx.Done(it.Index, err, false); continue; }
                    err = Decode(it, Password, output, ctx.Progress);
                    ctx.Done(it.Index, err == "CRC" ? "CRC 오류 (파일 손상)" : err, false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (ex is InvalidDataException || ex is EndOfStreamException || ex is IndexOutOfRangeException)
                {
                    ctx.Done(it.Index, ex.Message, false);
                }
            }
        }

        /// <summary>Decodes one entry. Returns null when OK, "CRC" on checksum mismatch, or an error message.</summary>
        private string Decode(Item it, string password, Stream output, Action<int> progress)
        {
            IDecryptor dec = string.IsNullOrEmpty(password) ? null : new XorDecryptor(Encoding.GetEncoding(437).GetBytes(password.ToUpperInvariant()));
            try
            {
                {
                    var input = new BufferedStream(new SegmentStream(_f, it.DataPos, it.Packed, dec), 1 << 16);
                    var crc = new Crc16Stream(output) { Progress = progress };
                    switch (it.Method)
                    {
                        case 1: case 2: LhaArchive.CopyN(input, crc, it.Packed); break;
                        case 3: { var r = new RetroCodecs.Rle90Stream(crc); input.CopyTo(r); break; }
                        case 4: RetroCodecs.Unsqueeze(input, crc); break;
                        case 8:
                        {
                            int bits = input.ReadByte();
                            RetroCodecs.UnixLzw(input, new RetroCodecs.Rle90Stream(crc), bits);
                            break;
                        }
                        case 9: RetroCodecs.UnixLzw(input, crc, 13); break;
                        default: return "지원하지 않는 ARC 압축 방식 (" + MethodName(it.Method) + ")";
                    }
                    return crc.Count == it.Size && crc.Crc == it.Crc ? null : "CRC";
                }
            }
            catch (InvalidDataException) when (output == Stream.Null) { return "CRC"; }
        }

        private sealed class XorDecryptor : IDecryptor
        {
            private readonly byte[] _k; private int _i;
            public XorDecryptor(byte[] k) { _k = k; }
            public void Decrypt(byte[] buf, int offset, int count)
            {
                for (int i = offset; i < offset + count; i++) { buf[i] ^= _k[_i]; _i = (_i + 1) % _k.Length; }
            }
        }

        public override void Dispose() => _f?.Dispose();
    }

    // ================================================================== ZOO
    internal sealed class ZooArchive : ManagedArchive
    {
        private sealed class Item { public int Index, Method; public long Packed, Size, DataPos; public ushort Crc; }
        private readonly List<Item> _items = new List<Item>();
        private FileStream _f;
        private const uint Magic = 0xFDC4A7DC;

        public static bool Looks(byte[] h, int len) => len >= 34 && BitConverter.ToUInt32(h, 20) == Magic && h[0] == 'Z' && h[1] == 'O' && h[2] == 'O';

        public static ZooArchive Open(string path)
        {
            var a = new ZooArchive { FormatName = "ZOO" };
            a._f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
            try { a.Parse(); } catch { a.Dispose(); throw; }
            a.PhysicalSize = a._f.Length;
            return a;
        }

        private void Parse()
        {
            var br = new BinaryReader(_f);
            _f.Position = 24;
            long pos = br.ReadUInt32();
            var enc = Encoding.GetEncoding(437);
            var seen = new HashSet<long>();
            while (pos > 0 && pos < _f.Length && seen.Add(pos))
            {
                _f.Position = pos;
                if (br.ReadUInt32() != Magic) throw new ArchiveException("ZOO 파일이 손상되었습니다.");
                int type = br.ReadByte(), method = br.ReadByte();
                long next = br.ReadUInt32(), offset = br.ReadUInt32();
                ushort date = br.ReadUInt16(), time = br.ReadUInt16(), crc = br.ReadUInt16();
                long size = br.ReadUInt32(), packed = br.ReadUInt32();
                br.ReadByte(); br.ReadByte();
                int deleted = br.ReadByte();
                br.ReadByte(); br.ReadUInt32(); br.ReadUInt16();
                var fname = br.ReadBytes(13);
                if (next == 0) break; // terminating entry
                string name = enc.GetString(fname, 0, Math.Max(0, Array.IndexOf(fname, (byte)0) < 0 ? 13 : Array.IndexOf(fname, (byte)0)));
                if (type == 2)
                {
                    int varLen = br.ReadUInt16();
                    long varStart = _f.Position + 3; // tz(1) + dir_crc(2)
                    if (varLen > 0)
                    {
                        _f.Position = varStart;
                        int namLen = br.ReadByte(), dirLen = varLen > 1 ? br.ReadByte() : 0;
                        string lname = namLen > 0 ? enc.GetString(br.ReadBytes(namLen)).TrimEnd('\0') : "";
                        string dir = dirLen > 0 ? enc.GetString(br.ReadBytes(dirLen)).TrimEnd('\0') : "";
                        if (lname.Length > 0) name = lname;
                        if (dir.Length > 0) name = dir.Replace('/', '\\').TrimEnd('\\') + "\\" + name;
                    }
                }
                if (deleted == 0)
                {
                    var it = new Item { Index = _items.Count, Method = method, Packed = packed, Size = size, DataPos = offset, Crc = crc };
                    _items.Add(it);
                    var e = new ArchiveEntry
                    {
                        Index = it.Index, Path = Normalize(name), Size = (ulong)size, PackedSize = (ulong)packed, Crc = crc,
                        Method = method == 0 ? "Stored" : method == 1 ? "LZW" : method == 2 ? "LH5" : "?" + method,
                        MTime = AlzArchive.DosToUtc((uint)(date << 16 | time))
                    };
                    int slash = e.Path.LastIndexOf('\\');
                    e.Name = slash >= 0 ? e.Path.Substring(slash + 1) : e.Path;
                    Entries.Add(e);
                }
                pos = next;
            }
            if (_items.Count == 0) throw new ArchiveException("ZOO 파일이 비어 있습니다.");
        }

        public override void Extract(ISet<int> wanted, ManagedExtractContext ctx)
        {
            foreach (var it in _items)
            {
                if (!wanted.Contains(it.Index)) continue;
                var output = ctx.Open(it.Index);
                try
                {
                    var input = new BufferedStream(new SegmentStream(_f, it.DataPos, it.Packed, null), 1 << 16);
                    var crc = new Crc16Stream(output) { Progress = ctx.Progress };
                    switch (it.Method)
                    {
                        case 0: LhaArchive.CopyN(input, crc, it.Packed); break;
                        case 1: RetroCodecs.ZooLzw(input, crc); break;
                        case 2: LhaCodecs.DecodeLhNew(input, crc, it.Size, 13, 4); break;
                        default: ctx.Done(it.Index, "지원하지 않는 ZOO 압축 방식 (" + it.Method + ")", false); continue;
                    }
                    bool ok = crc.Count == it.Size && crc.Crc == it.Crc;
                    ctx.Done(it.Index, ok ? null : "CRC 오류 (파일 손상)", false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (ex is InvalidDataException || ex is EndOfStreamException || ex is IndexOutOfRangeException)
                {
                    ctx.Done(it.Index, ex.Message, false);
                }
            }
        }

        public override void Dispose() => _f?.Dispose();
    }

    // ================================================================== single-stream formats
    /// <summary>Files that hold one (or a few) streams: .lz (lzip), .lz4, .br, .uu/.uue/.xxe, .aes (AES Crypt).</summary>
    internal sealed class StreamArchive : ManagedArchive
    {
        private string _path;
        private string _kind;
        private readonly List<Action<Stream, ManagedExtractContext>> _decoders = new List<Action<Stream, ManagedExtractContext>>();
        private readonly List<long> _expected = new List<long>();

        public static StreamArchive TryOpen(string path, byte[] h, int len)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            string stem = Path.GetFileNameWithoutExtension(path);
            var a = new StreamArchive { _path = path };
            long fileLen = new FileInfo(path).Length;
            a.PhysicalSize = fileLen;
            if (len >= 6 && h[0] == 'L' && h[1] == 'Z' && h[2] == 'I' && h[3] == 'P') { a._kind = "LZ"; a.FormatName = "LZIP"; a.Single(stem, Lzip.SizeFromTrailers(path)); }
            else if (len >= 4 && h[0] == 0x04 && h[1] == 0x22 && h[2] == 0x4D && h[3] == 0x18) { a._kind = "LZ4"; a.FormatName = "LZ4"; a.Single(stem, 0); }
            else if (len >= 4 && h[0] == 0x02 && h[1] == 0x21 && h[2] == 0x4C && h[3] == 0x18) { a._kind = "LZ4L"; a.FormatName = "LZ4 (legacy)"; a.Single(stem, 0); }
            else if (len >= 5 && h[0] == 'A' && h[1] == 'E' && h[2] == 'S' && h[3] <= 2) { a._kind = "AES"; a.FormatName = "AES Crypt"; a.Single(stem, 0); a.Entries[0].Encrypted = true; }
            else if (ext == ".br") { a._kind = "BR"; a.FormatName = "Brotli"; a.Single(stem, 0); }
            else if (ext == ".uu" || ext == ".uue" || ext == ".xxe" || UuText(h, len))
            {
                a._kind = "UU"; a.FormatName = ext == ".xxe" ? "XXE" : "UUE";
                a.ScanUu();
                if (a.Entries.Count == 0) return null;
            }
            else return null;
            if (a.Entries.Count > 0 && a._kind != "UU")
            {
                string k = a._kind;
                a._decoders.Add((output, ctx) => a.DecodeSingle(k, output, ctx));
            }
            return a;
        }

        private void Single(string name, long size)
        {
            if (_kind == "LZ" || _kind == "BR" || _kind == "AES" || _kind.StartsWith("LZ4"))
            {
                if (string.IsNullOrEmpty(Path.GetExtension(name)) && (name.EndsWith(".tar") == false)) { }
            }
            Entries.Add(new ArchiveEntry { Index = 0, Path = string.IsNullOrEmpty(name) ? "data" : name, Name = string.IsNullOrEmpty(name) ? "data" : name, Size = (ulong)Math.Max(0, size), PackedSize = (ulong)PhysicalSize, Method = FormatName });
            _expected.Add(size);
        }

        private static bool UuText(byte[] h, int len)
        {
            string s = Encoding.ASCII.GetString(h, 0, Math.Min(len, 4096));
            return s.StartsWith("begin ") || s.Contains("\nbegin 6") || s.Contains("\nbegin-base64 ") || s.StartsWith("begin-base64 ");
        }

        // ---- uuencode / xxencode / base64 (one archive may hold several "begin" blocks)
        private sealed class UuBlock { public long Start; public bool Base64; public string Name; }
        private readonly List<UuBlock> _uu = new List<UuBlock>();

        private void ScanUu()
        {
            using (var f = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var r = new StreamReader(f, Encoding.GetEncoding(28591));
                long pos = 0;
                string line;
                while ((line = r.ReadLine()) != null)
                {
                    long lineStart = pos;
                    pos += Encoding.GetEncoding(28591).GetByteCount(line) + 1;
                    if (line.StartsWith("begin ") || line.StartsWith("begin-base64 "))
                    {
                        var parts = line.Split(new[] { ' ' }, 3);
                        if (parts.Length < 3) continue;
                        var b = new UuBlock { Start = lineStart, Base64 = line.StartsWith("begin-base64"), Name = parts[2].Trim() };
                        _uu.Add(b);
                        int idx = Entries.Count;
                        Entries.Add(new ArchiveEntry { Index = idx, Path = Normalize(b.Name), Name = Path.GetFileName(b.Name), Method = b.Base64 ? "Base64" : FormatName });
                        _expected.Add(-1);
                        _decoders.Add((output, ctx) => DecodeUu(b, output));
                    }
                }
            }
        }

        private const string XxTable = "+-0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

        private void DecodeUu(UuBlock b, Stream output)
        {
            using (var f = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                f.Position = b.Start;
                var r = new StreamReader(f, Encoding.GetEncoding(28591));
                r.ReadLine(); // begin line
                if (b.Base64)
                {
                    var sb = new StringBuilder();
                    string l;
                    while ((l = r.ReadLine()) != null && l.Trim() != "====") sb.Append(l.Trim());
                    var data = Convert.FromBase64String(sb.ToString());
                    output.Write(data, 0, data.Length);
                    return;
                }
                bool? xx = null;
                string line;
                var buf = new byte[64];
                while ((line = r.ReadLine()) != null)
                {
                    if (line.StartsWith("end")) break;
                    if (line.Length == 0) continue;
                    if (xx == null) xx = FormatName == "XXE" || (line[0] == 'h' && line.Length >= 61);
                    int Val(char c)
                    {
                        if (xx == true) { int i = XxTable.IndexOf(c); if (i < 0) throw new InvalidDataException("XXE 데이터가 손상되었습니다"); return i; }
                        return (c - 32) & 63;
                    }
                    int n = Val(line[0]);
                    if (n == 0) { if (xx != true || line[0] == '+') continue; }
                    int o = 0;
                    for (int i = 1; o < n && i < line.Length; i += 4)
                    {
                        int a = Val(line[i]), bb = i + 1 < line.Length ? Val(line[i + 1]) : 0, c = i + 2 < line.Length ? Val(line[i + 2]) : 0, d = i + 3 < line.Length ? Val(line[i + 3]) : 0;
                        int v = a << 18 | bb << 12 | c << 6 | d;
                        if (o < n) buf[o++] = (byte)(v >> 16);
                        if (o < n) buf[o++] = (byte)(v >> 8);
                        if (o < n) buf[o++] = (byte)v;
                    }
                    output.Write(buf, 0, o);
                }
            }
        }

        private void DecodeSingle(string kind, Stream output, ManagedExtractContext ctx)
        {
            using (var f = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16))
            {
                var input = new BufferedStream(f, 1 << 16);
                switch (kind)
                {
                    case "LZ": Lzip.Decode(input, output); break;
                    case "LZ4": using (var d = K4os.Compression.LZ4.Streams.LZ4Stream.Decode(input, 0, true)) d.CopyTo(output); break;
                    case "LZ4L": Lz4Legacy(input, output); break;
                    case "BR":
#if NET
                        using (var d = new BrotliStream(input, CompressionMode.Decompress, true)) d.CopyTo(output);
#else
                        using (var d = new BrotliSharpLib.BrotliStream(input, CompressionMode.Decompress, true)) d.CopyTo(output);
#endif
                        break;
                    case "AES": AesCrypt.Decrypt(input, f.Length, output, ctx, this); break;
                }
            }
        }

        /// <summary>LZ4 legacy frame: magic, then [u32 size][raw LZ4 block] (each block up to 8 MB).</summary>
        private static void Lz4Legacy(Stream input, Stream output)
        {
            var hdr = new byte[4];
            AzoDecoder.ReadExact(input, hdr, 4);
            var dst = new byte[8 << 20];
            while (true)
            {
                int got = 0;
                while (got < 4) { int r = input.Read(hdr, got, 4 - got); if (r <= 0) break; got += r; }
                if (got < 4) return;
                uint size = BitConverter.ToUInt32(hdr, 0);
                if (size == 0x184C2102) continue; // concatenated legacy stream
                if (size > LZ4Bound(8 << 20)) throw new InvalidDataException("LZ4 블록 크기가 올바르지 않습니다");
                var src = new byte[size];
                AzoDecoder.ReadExact(input, src, (int)size);
                int n = K4os.Compression.LZ4.LZ4Codec.Decode(src, 0, src.Length, dst, 0, dst.Length);
                if (n < 0) throw new InvalidDataException("LZ4 데이터가 손상되었습니다");
                output.Write(dst, 0, n);
            }
        }

        private static long LZ4Bound(int n) => n + n / 255 + 16;

        public override void Extract(ISet<int> wanted, ManagedExtractContext ctx)
        {
            for (int i = 0; i < Entries.Count; i++)
            {
                if (!wanted.Contains(i)) continue;
                var output = ctx.Open(i);
                var counter = new CrcStream(output) { Progress = ctx.Progress };
                try
                {
                    _decoders[i](counter, ctx);
                    if (_expected[i] > 0 && counter.Count != _expected[i]) ctx.Done(i, "크기가 기록과 다릅니다 (파일 손상)", false);
                    else ctx.Done(i, null, false);
                }
                catch (OperationCanceledException) { throw; }
                catch (AesCrypt.WrongPassword) { ctx.Done(i, "암호를 입력하지 않았습니다", true); }
                catch (Exception ex) when (ex is InvalidDataException || ex is EndOfStreamException || ex is FormatException || ex is IOException && !(ex is FileNotFoundException) || ex is CryptographicException || ex.GetType().Name.Contains("LZ4") || ex is InvalidOperationException)
                {
                    ctx.Done(i, ex.Message, Entries[i].Encrypted);
                }
            }
        }

        public override void Dispose() { }

        internal string AskPasswordFor(ManagedExtractContext ctx, Func<string, bool> check) => WithPassword(ctx, p => check(p) ? p : null);
    }

    // ================================================================== lzip
    internal static class Lzip
    {
        public static long SizeFromTrailers(string path)
        {
            // walk members backwards using the trailer's member size
            try
            {
                using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    long end = f.Length, total = 0;
                    var t = new byte[20];
                    while (end >= 26)
                    {
                        f.Position = end - 20;
                        AzoDecoder.ReadExact(f, t, 20);
                        long data = (long)BitConverter.ToUInt64(t, 4), member = (long)BitConverter.ToUInt64(t, 12);
                        if (member < 26 || member > end) break;
                        total += data;
                        end -= member;
                    }
                    return end == 0 ? total : 0;
                }
            }
            catch { return 0; }
        }

        public static void Decode(Stream input, Stream output)
        {
            var hdr = new byte[6];
            bool first = true;
            while (true)
            {
                int got = 0;
                while (got < 6) { int r = input.Read(hdr, got, 6 - got); if (r <= 0) break; got += r; }
                if (got == 0 && !first) return;
                if (got < 6 || hdr[0] != 'L' || hdr[1] != 'Z' || hdr[2] != 'I' || hdr[3] != 'P')
                {
                    if (!first) return; // trailing data
                    throw new InvalidDataException("lzip 헤더가 올바르지 않습니다");
                }
                first = false;
                int code = hdr[5];
                uint dict = 1u << (code & 31);
                dict -= (dict / 16) * (uint)((code >> 5) & 7);
                var props = new byte[5];
                props[0] = 93; // lc=3 lp=0 pb=2
                BitConverter.GetBytes(dict).CopyTo(props, 1);
                var crc = new CrcStream(output);
                new LzmaDecoder(props).Decode(input, crc, -1);
                var trailer = new byte[20];
                AzoDecoder.ReadExact(input, trailer, 20);
                if (BitConverter.ToUInt32(trailer, 0) != crc.Crc || (long)BitConverter.ToUInt64(trailer, 4) != crc.Count)
                    throw new InvalidDataException("lzip CRC 오류 (파일 손상)");
            }
        }
    }

    // ================================================================== AES Crypt (aescrypt.com file format v1/v2)
    internal static class AesCrypt
    {
        public sealed class WrongPassword : Exception { }

        public static void Decrypt(Stream input, long totalLen, Stream output, ManagedExtractContext ctx, StreamArchive owner)
        {
            var br = new BinaryReader(input);
            var magic = br.ReadBytes(5);
            int version = magic[3];
            if (version == 0) throw new InvalidDataException("AES Crypt 0 버전은 지원하지 않습니다");
            long pos = 5;
            if (version == 2)
            {
                while (true)
                {
                    int l = br.ReadByte() << 8 | br.ReadByte();
                    pos += 2;
                    if (l == 0) break;
                    br.ReadBytes(l); pos += l;
                }
            }
            var iv1 = br.ReadBytes(16);
            var encKeys = br.ReadBytes(48);
            var hmac1 = br.ReadBytes(32);
            pos += 96;

            byte[] key2 = null, iv2 = null;
            string pw = owner.AskPasswordFor(ctx, p =>
            {
                var key = Derive(iv1, p);
                using (var h = new HMACSHA256(key))
                {
                    var mac = h.ComputeHash(encKeys);
                    if (!mac.SequenceEqual(hmac1)) return false;
                }
                using (var aes = Aes.Create())
                {
                    aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.None; aes.Key = key; aes.IV = iv1;
                    var plain = aes.CreateDecryptor().TransformFinalBlock(encKeys, 0, 48);
                    iv2 = plain.Take(16).ToArray();
                    key2 = plain.Skip(16).Take(32).ToArray();
                }
                return true;
            });
            if (pw == null) throw new WrongPassword();

            long cipherLen = totalLen - pos - 1 - 32;
            if (cipherLen < 0 || cipherLen % 16 != 0) throw new InvalidDataException("AES Crypt 파일이 손상되었습니다");
            using (var aes = Aes.Create())
            using (var hmac = new HMACSHA256(key2))
            {
                aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.None; aes.Key = key2; aes.IV = iv2;
                var dec = aes.CreateDecryptor();
                var buf = new byte[1 << 16];
                var outBuf = new byte[1 << 16];
                long left = cipherLen;
                byte[] lastBlock = null;
                while (left > 0)
                {
                    int n = (int)Math.Min(buf.Length, left);
                    AzoDecoder.ReadExact(input, buf, n);
                    hmac.TransformBlock(buf, 0, n, null, 0);
                    int m = dec.TransformBlock(buf, 0, n, outBuf, 0);
                    left -= n;
                    if (left == 0)
                    {
                        // keep the last block back until we know the final length
                        output.Write(outBuf, 0, m - 16);
                        lastBlock = new byte[16];
                        Buffer.BlockCopy(outBuf, m - 16, lastBlock, 0, 16);
                    }
                    else output.Write(outBuf, 0, m);
                }
                hmac.TransformFinalBlock(new byte[0], 0, 0);
                int mod = input.ReadByte() & 0x0F;
                var hmac2 = new byte[32];
                AzoDecoder.ReadExact(input, hmac2, 32);
                if (!hmac.Hash.SequenceEqual(hmac2)) throw new InvalidDataException("AES Crypt 무결성 검사 실패 (파일 손상)");
                if (lastBlock != null) output.Write(lastBlock, 0, mod == 0 ? 16 : mod);
            }
        }

        private static byte[] Derive(byte[] iv, string password)
        {
            var pw = Encoding.Unicode.GetBytes(password);
            var digest = new byte[32];
            Buffer.BlockCopy(iv, 0, digest, 0, 16);
            using (var sha = SHA256.Create())
            {
                var buf = new byte[32 + pw.Length];
                for (int i = 0; i < 8192; i++)
                {
                    Buffer.BlockCopy(digest, 0, buf, 0, 32);
                    Buffer.BlockCopy(pw, 0, buf, 32, pw.Length);
                    digest = sha.ComputeHash(buf);
                }
            }
            return digest;
        }
    }
}
