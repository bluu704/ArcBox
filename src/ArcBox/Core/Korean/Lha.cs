using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ArcBox.Core.Korean
{
    /// <summary>LHA-family decoders (-lh5-/-lh6-/-lh7-, PMarc -pm1-/-pm2-). C# port of lhasa (ISC license, Simon Howard).</summary>
    internal static class LhaCodecs
    {
        // ------------------------------------------------------------------ bit reader (MSB first, 32-bit buffer)
        internal sealed class Bits
        {
            private readonly Stream _s;
            private readonly bool _zeroFillAtEof;
            private uint _buf;
            private int _bits;
            public Bits(Stream s, bool zeroFillAtEof = false) { _s = s; _zeroFillAtEof = zeroFillAtEof; }

            public int Peek(int n)
            {
                if (n == 0) return 0;
                while (_bits < n)
                {
                    int b = _s.ReadByte();
                    if (b < 0)
                    {
                        if (!_zeroFillAtEof) return -1;
                        b = 0;
                    }
                    _buf |= (uint)b << (24 - _bits);
                    _bits += 8;
                }
                return (int)(_buf >> (32 - n));
            }

            public int Read(int n)
            {
                int r = Peek(n);
                if (r >= 0 && n > 0) { _buf <<= n; _bits -= n; }
                return r;
            }

            public int Bit() => Read(1);
        }

        // ------------------------------------------------------------------ tree decode (lhasa tree_decode.c)
        private const int Leaf = 1 << 30;

        private static void InitTree(int[] tree) { for (int i = 0; i < tree.Length; i++) tree[i] = Leaf; }
        private static void SetSingle(int[] tree, int code) { tree[0] = code | Leaf; }

        private static void BuildTree(int[] tree, int treeLen, byte[] lengths, int num)
        {
            int nextEntry = 0, allocated = 1, codeLen = 0;
            bool remaining;
            do
            {
                // expand_queue
                int newNodes = (allocated - nextEntry) * 2;
                if (allocated + newNodes <= treeLen)
                {
                    int end = allocated;
                    while (nextEntry < end) { tree[nextEntry] = allocated; allocated += 2; nextEntry++; }
                }
                codeLen++;
                // add_codes_with_length
                remaining = false;
                for (int i = 0; i < num; i++)
                {
                    if (lengths[i] == codeLen)
                    {
                        if (nextEntry < allocated) tree[nextEntry++] = i | Leaf;
                    }
                    else if (lengths[i] > codeLen) remaining = true;
                }
            } while (remaining);
        }

        private static int ReadTree(Bits r, int[] tree)
        {
            int code = tree[0];
            while ((code & Leaf) == 0)
            {
                int bit = r.Bit();
                if (bit < 0) return -1;
                int idx = code + bit;
                if (idx >= tree.Length) return -1;
                code = tree[idx];
            }
            return code & ~Leaf;
        }

        // ------------------------------------------------------------------ -lh5- / -lh6- / -lh7- (lh_new_decoder.c)
        public static void DecodeLhNew(Stream input, Stream output, long size, int historyBits, int offsetBits)
        {
            const int NumCodes = 510, TempBits = 5, MaxTemp = (1 << TempBits) - 1;
            int maxOffset = (1 << offsetBits) - 1;
            int ringSize = 1 << historyBits;
            var r = new Bits(input);
            var ring = new byte[ringSize];
            for (int i = 0; i < ringSize; i++) ring[i] = (byte)' ';
            int ringPos = 0;
            var tempTree = new int[MaxTemp * 2]; var codeTree = new int[NumCodes * 2]; var offTree = new int[maxOffset * 2];
            InitTree(tempTree); InitTree(codeTree); InitTree(offTree);
            int blockRemaining = 0;
            long done = 0;
            var outBuf = new byte[1 << 16];
            int op = 0;

            int ReadLen()
            {
                int len = r.Read(3);
                if (len < 0) return -1;
                if (len == 7)
                {
                    while (true)
                    {
                        int b = r.Bit();
                        if (b < 0) return -1;
                        if (b == 0) break;
                        len++;
                    }
                }
                return len;
            }

            bool ReadTempTable()
            {
                int n = r.Read(TempBits);
                if (n < 0) return false;
                if (n == 0) { int c = r.Read(5); if (c < 0) return false; SetSingle(tempTree, c); return true; }
                if (n > MaxTemp) n = MaxTemp;
                var lens = new byte[MaxTemp];
                for (int i = 0; i < n; i++)
                {
                    int len = ReadLen();
                    if (len < 0) return false;
                    lens[i] = (byte)len;
                    if (i == 2)
                    {
                        int z = r.Read(2);
                        if (z < 0) return false;
                        for (int j = 0; j < z && i + 1 < MaxTemp; j++) { i++; lens[i] = 0; }
                    }
                }
                InitTree(tempTree);
                BuildTree(tempTree, tempTree.Length, lens, n);
                return true;
            }

            bool ReadCodeTable()
            {
                int n = r.Read(9);
                if (n < 0) return false;
                if (n == 0) { int c = r.Read(9); if (c < 0) return false; SetSingle(codeTree, c); return true; }
                if (n > NumCodes) n = NumCodes;
                var lens = new byte[NumCodes];
                int i = 0;
                while (i < n)
                {
                    int code = ReadTree(r, tempTree);
                    if (code < 0) return false;
                    if (code <= 2)
                    {
                        int skip;
                        if (code == 0) skip = 1;
                        else if (code == 1) { skip = r.Read(4); if (skip < 0) return false; skip += 3; }
                        else { skip = r.Read(9); if (skip < 0) return false; skip += 20; }
                        for (int j = 0; j < skip && i < n; j++) lens[i++] = 0;
                    }
                    else lens[i++] = (byte)(code - 2);
                }
                InitTree(codeTree);
                BuildTree(codeTree, codeTree.Length, lens, n);
                return true;
            }

            bool ReadOffsetTable()
            {
                int n = r.Read(offsetBits);
                if (n < 0) return false;
                if (n == 0) { int c = r.Read(offsetBits); if (c < 0) return false; SetSingle(offTree, c); return true; }
                if (n > maxOffset) n = maxOffset;
                var lens = new byte[maxOffset];
                for (int i = 0; i < n; i++) { int len = ReadLen(); if (len < 0) return false; lens[i] = (byte)len; }
                InitTree(offTree);
                BuildTree(offTree, offTree.Length, lens, n);
                return true;
            }

            void Put(byte b)
            {
                ring[ringPos] = b;
                ringPos = (ringPos + 1) & (ringSize - 1);
                if (done < size)
                {
                    outBuf[op++] = b;
                    done++;
                    if (op == outBuf.Length) { output.Write(outBuf, 0, op); op = 0; }
                }
            }

            while (done < size)
            {
                while (blockRemaining == 0)
                {
                    int len = r.Read(16);
                    if (len < 0 || !ReadTempTable() || !ReadCodeTable() || !ReadOffsetTable()) throw new InvalidDataException("LHA 데이터가 손상되었습니다");
                    blockRemaining = len;
                }
                blockRemaining--;
                int code = ReadTree(r, codeTree);
                if (code < 0) throw new InvalidDataException("LHA 데이터가 손상되었습니다");
                if (code < 256) Put((byte)code);
                else
                {
                    int count = code - 256 + 3;
                    int bits = ReadTree(r, offTree);
                    if (bits < 0) throw new InvalidDataException("LHA 데이터가 손상되었습니다");
                    int offset;
                    if (bits <= 1) offset = bits;
                    else { int v = r.Read(bits - 1); if (v < 0) throw new InvalidDataException("LHA 데이터가 손상되었습니다"); offset = v + (1 << (bits - 1)); }
                    int start = ringPos + ringSize - offset - 1;
                    for (int i = 0; i < count; i++) Put(ring[(start + i) & (ringSize - 1)]);
                }
            }
            if (op > 0) output.Write(outBuf, 0, op);
        }

        // ------------------------------------------------------------------ PMarc history list (pma_common.c)
        private sealed class History
        {
            private readonly byte[] _prev = new byte[256], _next = new byte[256];
            private byte _head;
            public History()
            {
                for (int i = 0; i < 256; i++) { _prev[i] = (byte)(i + 1); _next[i] = (byte)(i - 1); }
                _head = 0x20;
                _prev[0x7f] = 0x00; _next[0x00] = 0x7f;
                _prev[0x1f] = 0xa0; _next[0xa0] = 0x1f;
                _prev[0xdf] = 0x80; _next[0x80] = 0xdf;
                _prev[0x9f] = 0xe0; _next[0xe0] = 0x9f;
                _prev[0xff] = 0x20; _next[0x20] = 0xff;
            }
            public byte Find(int count)
            {
                byte code = _head;
                if (count < 128) for (int i = 0; i < count; i++) code = _prev[code];
                else for (int i = 0; i < 256 - count; i++) code = _next[code];
                return code;
            }
            public void Update(byte b)
            {
                if (_head == b) return;
                // unlink b
                _prev[_next[b]] = _prev[b];
                _next[_prev[b]] = _next[b];
                // insert after old head
                byte oldHead = _head;
                _prev[b] = oldHead;
                _next[b] = _next[oldHead];
                _prev[_next[oldHead]] = b;
                _next[oldHead] = b;
                _head = b;
            }
        }

        private static int VarLen(Bits r, int[,] table, int header)
        {
            int v = r.Read(table[header, 1]);
            if (v < 0) return -1;
            return table[header, 0] + v;
        }

        // ------------------------------------------------------------------ -pm2- (pm2_decoder.c)
        public static void DecodePm2(Stream input, Stream output, long size)
        {
            const int Ring = 8192;
            var hist = new int[,] { { 0, 3 }, { 8, 3 }, { 16, 4 }, { 32, 5 }, { 64, 5 }, { 96, 5 }, { 128, 6 }, { 192, 6 } };
            var copy = new int[,] { { 17, 3 }, { 25, 3 }, { 33, 5 }, { 65, 6 }, { 129, 7 }, { 256, 0 } };
            var r = new Bits(input);
            var ring = new byte[Ring];
            for (int i = 0; i < Ring; i++) ring[i] = (byte)' ';
            int ringPos = 0;
            var history = new History();
            var codeTree = new int[65]; var offTree = new int[17];
            InitTree(codeTree); InitTree(offTree);
            int state = 0; long rebuildRemaining = 0;
            bool needOffsetTree = false;
            long done = 0;
            var outBuf = new byte[1 << 16];
            int op = 0;

            bool ReadCodeTree()
            {
                int num = r.Read(5), minLen = r.Read(3);
                if (num < 0 || minLen < 0 || num > 29) return false;
                needOffsetTree = num >= 10 && !(num == 29 && minLen == 0);
                if (minLen == 0) { SetSingle(codeTree, num - 1); return true; }
                int lenBits = r.Read(3);
                if (lenBits < 0) return false;
                var lens = new byte[31];
                for (int i = 0; i < num; i++)
                {
                    int v = r.Read(lenBits);
                    if (v < 0) return false;
                    lens[i] = v == 0 ? (byte)0 : (byte)(minLen + v - 1);
                }
                InitTree(codeTree);
                BuildTree(codeTree, codeTree.Length, lens, num);
                return true;
            }

            bool ReadOffsetTree(int numOffsets)
            {
                if (!needOffsetTree) return true;
                var lens = new byte[8];
                int numCodes = 0, single = 0;
                for (int off = 0; off < numOffsets; off++)
                {
                    int len = r.Read(3);
                    if (len < 0) return false;
                    lens[off] = (byte)len;
                    if (len != 0) { single = off; numCodes++; }
                }
                if (numCodes == 1) { SetSingle(offTree, single); return true; }
                InitTree(offTree);
                BuildTree(offTree, offTree.Length, lens, numOffsets);
                return true;
            }

            void Rebuild()
            {
                switch (state)
                {
                    case 0: ReadCodeTree(); ReadOffsetTree(5); state = 1; rebuildRemaining = 1024; break;
                    case 1: ReadOffsetTree(6); state = 2; rebuildRemaining = 1024; break;
                    case 2: ReadOffsetTree(7); state = 3; rebuildRemaining = 2048; break;
                    case 3: if (r.Bit() == 1) ReadCodeTree(); ReadOffsetTree(8); state = 4; rebuildRemaining = 4096; break;
                    default: if (r.Bit() == 1) { ReadCodeTree(); ReadOffsetTree(8); } rebuildRemaining = 4096; break;
                }
            }

            void Put(byte b)
            {
                ring[ringPos] = b;
                ringPos = (ringPos + 1) % Ring;
                if (done < size)
                {
                    outBuf[op++] = b;
                    done++;
                    if (op == outBuf.Length) { output.Write(outBuf, 0, op); op = 0; }
                }
                history.Update(b);
                if (--rebuildRemaining == 0) Rebuild();
            }

            r.Bit();
            Rebuild();
            while (done < size)
            {
                int code = ReadTree(r, codeTree);
                if (code < 0) throw new InvalidDataException("PMA 데이터가 손상되었습니다");
                if (code < 8)
                {
                    int off = VarLen(r, hist, code);
                    if (off < 0) throw new InvalidDataException("PMA 데이터가 손상되었습니다");
                    Put(history.Find(off));
                }
                else
                {
                    code -= 8;
                    int count = code < 15 ? code + 2 : VarLen(r, copy, code - 15);
                    int offset;
                    if (code == 0) offset = r.Read(6);
                    else if (code < 20)
                    {
                        int v = ReadTree(r, offTree);
                        int bits, res = 0;
                        if (v < 0) offset = -1;
                        else
                        {
                            if (v == 0) bits = 6; else { bits = v + 5; res = 1 << bits; }
                            int x = r.Read(bits);
                            offset = x < 0 ? -1 : res + x;
                        }
                    }
                    else offset = 0;
                    if (count < 0 || offset < 0 || count > 256) throw new InvalidDataException("PMA 데이터가 손상되었습니다");
                    int start = ringPos + Ring - 1 - offset;
                    for (int i = 0; i < count; i++) Put(ring[(start + i) % Ring]);
                }
            }
            if (op > 0) output.Write(outBuf, 0, op);
        }

        // ------------------------------------------------------------------ -pm1- (pm1_decoder.c)
        private static readonly byte[][] Pm1Trees =
        {
            new byte[] { 0x12, 0x2d, 0xef, 0x1c, 0xab }, new byte[] { 0x12, 0x23, 0xde, 0xab, 0xcf }, new byte[] { 0x12, 0x2c, 0xd2, 0xab, 0xef },
            new byte[] { 0x12, 0xa2, 0xd2, 0xbc, 0xef }, new byte[] { 0x12, 0xa2, 0xc2, 0xbd, 0xef }, new byte[] { 0x12, 0xa2, 0xcd, 0xb1, 0xef },
            new byte[] { 0x12, 0xab, 0x12, 0xcd, 0xef }, new byte[] { 0x12, 0xab, 0x1d, 0xc1, 0xef }, new byte[] { 0x12, 0xab, 0xc1, 0xd1, 0xef },
            new byte[] { 0xa1, 0x12, 0x2c, 0xde, 0xbf }, new byte[] { 0xa1, 0x1d, 0x1c, 0xb1, 0xef }, new byte[] { 0xa1, 0x12, 0x2d, 0xef, 0xbc },
            new byte[] { 0xa1, 0x12, 0xb2, 0xde, 0xcf }, new byte[] { 0xa1, 0x12, 0xbc, 0xd1, 0xef }, new byte[] { 0xa1, 0x1c, 0xb1, 0xd1, 0xef },
            new byte[] { 0xa1, 0xb1, 0x12, 0xcd, 0xef }, new byte[] { 0xa1, 0xb1, 0xc1, 0xd1, 0xef }, new byte[] { 0x12, 0x1c, 0xde, 0xab },
            new byte[] { 0x12, 0xa2, 0xcd, 0xbe }, new byte[] { 0x12, 0xab, 0xc1, 0xde }, new byte[] { 0xa1, 0x1d, 0x1c, 0xbe },
            new byte[] { 0xa1, 0x12, 0xbc, 0xde }, new byte[] { 0xa1, 0x1c, 0xb1, 0xde }, new byte[] { 0xa1, 0xb1, 0xc1, 0xde },
            new byte[] { 0x1d, 0x1c, 0xab }, new byte[] { 0x1c, 0xa1, 0xbd }, new byte[] { 0x12, 0xab, 0xcd }, new byte[] { 0xa1, 0x1c, 0xbd },
            new byte[] { 0xa1, 0xb1, 0xcd }, new byte[] { 0xa1, 0xbc }, new byte[] { 0xab }, new byte[] { 0x00 },
        };

        public static void DecodePm1(Stream input, Stream output, long size)
        {
            const int Ring = 16384;
            var copyRanges = new int[,] { { 0, 6 }, { 64, 8 }, { 0, 6 }, { 64, 9 }, { 576, 11 }, { 2624, 13 }, { 64, 8 }, { 576, 8 }, { 576, 9 }, { 576, 10 }, { 2624, 8 }, { 2624, 9 }, { 2624, 10 }, { 2624, 11 }, { 2624, 12 } };
            var byteRanges = new int[,] { { 0, 4 }, { 16, 4 }, { 32, 5 }, { 64, 6 }, { 128, 6 }, { 192, 6 } };
            var r = new Bits(input, true);
            var ring = new byte[Ring];
            int ringPos = 0;
            long pos = 0;   // output_stream_pos
            var history = new History();
            byte[] tree = null;
            long done = 0;
            var outBuf = new byte[1 << 16];
            int op = 0;
            Exception Bad() => new InvalidDataException("PMA 데이터가 손상되었습니다");

            void Out(byte b)
            {
                ring[ringPos] = b;
                ringPos = (ringPos + 1) % Ring;
                history.Update(b);
                pos++;
                if (done < size)
                {
                    outBuf[op++] = b;
                    done++;
                    if (op == outBuf.Length) { output.Write(outBuf, 0, op); op = 0; }
                }
            }

            int AfterThreshold(long threshold, int def) => pos >= threshold ? r.Bit() : def;

            int CopyByteCount()
            {
                int x = r.Read(2);
                if (x < 3) return x + 3;
                x = r.Read(3);
                if (x < 5) return x + 6;
                if (x == 5) return r.Read(2) + 11;
                if (x == 6) return r.Read(3) + 15;
                x = r.Read(6);
                if (x < 62) return x + 23;
                if (x == 62) return r.Read(5) + 85;
                return r.Read(7) + 117;
            }

            int CopyTypeRange()
            {
                int x = r.Bit();
                if (x == 0)
                {
                    x = AfterThreshold(576, 0);
                    if (x != 0) return 4;
                    return AfterThreshold(64, 0);
                }
                x = AfterThreshold(64, 1);
                if (x == 0) return 3;
                x = AfterThreshold(2624, 1);
                return x != 0 ? 2 : 5;
            }

            bool CopyCommand()
            {
                int range = CopyTypeRange();
                int count = range < 2 ? 2 : CopyByteCount();
                if (range == 3) { if (pos < 320) range = 6; }
                else if (range == 4) { if (pos < 832) range = 7; else if (pos < 1088) range = 8; else if (pos < 1600) range = 9; }
                else if (range == 5)
                {
                    if (pos < 2880) range = 10; else if (pos < 3136) range = 11; else if (pos < 3648) range = 12;
                    else if (pos < 4672) range = 13; else if (pos < 6720) range = 14;
                }
                int dist = VarLen(r, copyRanges, range);
                if (dist < 0 || dist >= pos) return false;
                int idx = (ringPos + Ring - dist - 1) % Ring;
                for (int i = 0; i < count; i++) { Out(ring[idx]); idx = (idx + 1) % Ring; }
                return true;
            }

            int ByteIndex()
            {
                if (tree[0] == 0) return 0;
                int p = 0;
                while (true)
                {
                    int bit = r.Bit();
                    int child = bit == 0 ? (tree[p] >> 4) & 0x0f : tree[p] & 0x0f;
                    if (child >= 10) return child - 10;
                    p += child;
                    if (p >= tree.Length) throw Bad();
                }
            }

            int BlockCount()
            {
                int x = r.Read(2);
                if (x < 3) return x + 1;
                x = r.Read(3);
                if (x < 7) return x + 4;
                x = r.Read(4);
                if (x < 14) return x + 11;
                if (x == 14) return r.Read(6) + 25;
                return r.Read(7) + 89;
            }

            tree = Pm1Trees[r.Read(5)];
            while (done < size)
            {
                if (r.Bit() == 0) { if (!CopyCommand()) throw Bad(); }
                else
                {
                    int len = BlockCount();
                    for (int i = 0; i < len; i++)
                    {
                        int idx = ByteIndex();
                        int count = VarLen(r, byteRanges, idx);
                        Out(history.Find(count));
                    }
                    if (len != 216 && !CopyCommand()) throw Bad();
                }
            }
            if (op > 0) output.Write(outBuf, 0, op);
        }

        // ------------------------------------------------------------------ CRC-16 (ARC/LHA)
        public static ushort Crc16(ushort crc, byte[] b, int off, int n)
        {
            for (int i = off; i < off + n; i++)
            {
                crc ^= b[i];
                for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (ushort)((crc >> 1) ^ 0xA001) : (ushort)(crc >> 1);
            }
            return crc;
        }
    }

    /// <summary>Write-through stream computing CRC-16 (ARC/LHA polynomial).</summary>
    internal sealed class Crc16Stream : Stream
    {
        private readonly Stream _t;
        public ushort Crc;
        public long Count;
        public Action<int> Progress;
        public Crc16Stream(Stream t) { _t = t; }
        public override void Write(byte[] buffer, int offset, int count)
        {
            Crc = LhaCodecs.Crc16(Crc, buffer, offset, count);
            Count += count;
            _t?.Write(buffer, offset, count);
            Progress?.Invoke(count);
        }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => Count;
        public override long Position { get => Count; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    // ================================================================== LHA-header archive (used for PMarc .pma)
    internal sealed class LhaArchive : ManagedArchive
    {
        private sealed class Item { public int Index; public string Method; public long Packed, Size, DataPos; public ushort Crc; public bool HasCrc; }
        private readonly List<Item> _items = new List<Item>();
        private FileStream _f;

        public static bool LooksLikePma(byte[] head, int len) =>
            len > 22 && head[2] == '-' && head[3] == 'p' && head[4] == 'm' && head[6] == '-';

        public static LhaArchive Open(string path)
        {
            var a = new LhaArchive { FormatName = "PMA" };
            a._f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
            try { a.Parse(); } catch { a.Dispose(); throw; }
            a.PhysicalSize = a._f.Length;
            return a;
        }

        private void Parse()
        {
            var enc = Encoding.GetEncoding(932); // PMarc is a Japanese MSX/CP/M tool
            while (_f.Position < _f.Length)
            {
                long hdrStart = _f.Position;
                int first = _f.ReadByte();
                if (first <= 0) break;
                var h = new byte[Math.Max(first + 2, 26)];
                h[0] = (byte)first;
                int got = _f.Read(h, 1, h.Length - 1);
                if (got < 21) break;
                int level = h[20];
                var it = new Item { Index = _items.Count, Method = Encoding.ASCII.GetString(h, 2, 5) };
                string name; uint dosTime = 0; DateTime? mtime = null;
                if (level == 0 || level == 1)
                {
                    int hsize = first;
                    _f.Position = hdrStart;
                    h = new byte[hsize + 2];
                    AzoDecoder.ReadExact(_f, h, h.Length);
                    it.Packed = BitConverter.ToUInt32(h, 7);
                    it.Size = BitConverter.ToUInt32(h, 11);
                    dosTime = BitConverter.ToUInt32(h, 15);
                    int nl = h[21];
                    name = enc.GetString(h, 22, Math.Min(nl, h.Length - 22));
                    if (22 + nl + 2 <= h.Length) { it.Crc = BitConverter.ToUInt16(h, 22 + nl); it.HasCrc = true; }
                    if (level == 1)
                    {
                        int next = BitConverter.ToUInt16(h, h.Length - 2);
                        while (next > 0)
                        {
                            var ext = new byte[next];
                            AzoDecoder.ReadExact(_f, ext, next);
                            it.Packed -= next;
                            if (ext[0] == 2 && next > 3) // directory
                                name = enc.GetString(ext, 1, next - 3).Replace((char)0xFF, '\\') + "\\" + name;
                            next = BitConverter.ToUInt16(ext, next - 2);
                        }
                    }
                }
                else if (level == 2)
                {
                    int total = BitConverter.ToUInt16(h, 0);
                    _f.Position = hdrStart;
                    h = new byte[total];
                    AzoDecoder.ReadExact(_f, h, total);
                    it.Packed = BitConverter.ToUInt32(h, 7);
                    it.Size = BitConverter.ToUInt32(h, 11);
                    try { mtime = DateTimeOffset.FromUnixTimeSeconds(BitConverter.ToUInt32(h, 15)).UtcDateTime; } catch { }
                    it.Crc = BitConverter.ToUInt16(h, 21); it.HasCrc = true;
                    name = ""; string dir = "";
                    int p = 24;
                    while (p + 2 <= h.Length)
                    {
                        int sz = BitConverter.ToUInt16(h, p);
                        if (sz == 0 || p + sz > h.Length) break;
                        int type = h[p + 2];
                        if (type == 1) name = enc.GetString(h, p + 3, sz - 3);
                        else if (type == 2) dir = enc.GetString(h, p + 3, sz - 3).Replace((char)0xFF, '\\');
                        p += sz;
                    }
                    if (dir.Length > 0) name = dir.TrimEnd('\\') + "\\" + name;
                }
                else throw new ArchiveException("지원하지 않는 LHA 헤더 수준입니다 (" + level + ")");

                it.DataPos = _f.Position;
                _f.Position += it.Packed;
                _items.Add(it);
                bool isDir = it.Method == "-lhd-" || it.Method == "-pmd-";
                var e = new ArchiveEntry
                {
                    Index = it.Index, Path = Normalize(name.Replace('/', '\\')), IsDir = isDir, Size = (ulong)Math.Max(0, it.Size),
                    PackedSize = (ulong)Math.Max(0, it.Packed), Method = it.Method.Trim('-'),
                    MTime = mtime ?? (dosTime != 0 ? AlzArchive.DosToUtc(dosTime) : null)
                };
                if (string.IsNullOrEmpty(e.Path)) e.Path = "file" + it.Index;
                int slash = e.Path.LastIndexOf('\\');
                e.Name = slash >= 0 ? e.Path.Substring(slash + 1) : e.Path;
                Entries.Add(e);
            }
            if (_items.Count == 0) throw new ArchiveException("PMA 파일이 비어 있거나 손상되었습니다.");
        }

        public override void Extract(ISet<int> wanted, ManagedExtractContext ctx)
        {
            foreach (var it in _items)
            {
                if (!wanted.Contains(it.Index)) continue;
                var output = ctx.Open(it.Index);
                if (Entries[it.Index].IsDir) { ctx.Done(it.Index, null, false); continue; }
                try
                {
                    var input = new BufferedStream(new SegmentStream(_f, it.DataPos, it.Packed, null), 1 << 16);
                    var crc = new Crc16Stream(output) { Progress = ctx.Progress };
                    switch (it.Method)
                    {
                        case "-pm0-": case "-lh0-": case "-lz4-": CopyN(input, crc, it.Size); break;
                        case "-pm1-": LhaCodecs.DecodePm1(input, crc, it.Size); break;
                        case "-pm2-": LhaCodecs.DecodePm2(input, crc, it.Size); break;
                        case "-lh5-": LhaCodecs.DecodeLhNew(input, crc, it.Size, 14, 4); break;
                        case "-lh6-": LhaCodecs.DecodeLhNew(input, crc, it.Size, 16, 5); break;
                        case "-lh7-": LhaCodecs.DecodeLhNew(input, crc, it.Size, 17, 5); break;
                        default: ctx.Done(it.Index, "지원하지 않는 압축 방식 (" + it.Method + ")", false); continue;
                    }
                    bool ok = crc.Count == it.Size && (!it.HasCrc || crc.Crc == it.Crc);
                    ctx.Done(it.Index, ok ? null : "CRC 오류 (파일 손상)", false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (ex is InvalidDataException || ex is EndOfStreamException || ex is IndexOutOfRangeException)
                {
                    ctx.Done(it.Index, ex.Message, false);
                }
            }
        }

        internal static void CopyN(Stream input, Stream output, long n)
        {
            var buf = new byte[1 << 16];
            while (n > 0)
            {
                int r = input.Read(buf, 0, (int)Math.Min(buf.Length, n));
                if (r <= 0) throw new InvalidDataException("데이터가 예상보다 일찍 끝났습니다");
                output.Write(buf, 0, r);
                n -= r;
            }
        }

        public override void Dispose() => _f?.Dispose();
    }
}
