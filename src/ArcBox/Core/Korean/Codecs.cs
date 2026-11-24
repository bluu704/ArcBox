using System;
using System.IO;

namespace ArcBox.Core.Korean
{
    /// <summary>MSB-first bit reader over a stream (used by bzip2).</summary>
    internal sealed class MsbBitReader
    {
        private readonly Stream _s;
        private uint _buf;
        private int _bits;
        public bool Eof { get; private set; }

        public MsbBitReader(Stream s) { _s = s; }

        public int Bit()
        {
            if (_bits == 0)
            {
                int b = _s.ReadByte();
                if (b < 0) { Eof = true; throw new InvalidDataException("압축 데이터가 예상보다 일찍 끝났습니다"); }
                _buf = (uint)b; _bits = 8;
            }
            _bits--;
            return (int)((_buf >> _bits) & 1);
        }

        public uint Bits(int n)
        {
            uint v = 0;
            for (int i = 0; i < n; i++) v = (v << 1) | (uint)Bit();
            return v;
        }

        /// <summary>Discards the remaining bits of the current byte.</summary>
        public void Align() { _bits = 0; }

        public bool TryReadByteAligned(out int b)
        {
            _bits = 0;
            b = _s.ReadByte();
            return b >= 0;
        }
    }

    /// <summary>bzip2 decoder: standard streams (EGG) and ALZip's variant with "DLZ" block markers.</summary>
    internal static class Bzip2Decoder
    {
        private const int MaxGroups = 6, GroupSize = 50, MaxAlpha = 258, MaxCodeLen = 23, MaxSelectors = 18002;

        public static void DecodeStandard(Stream input, Stream output)
        {
            var br = new MsbBitReader(input);
            bool first = true;
            while (true)
            {
                int b0, b1, b2, b3;
                if (first) { b0 = input.ReadByte(); first = false; }
                else if (!br.TryReadByteAligned(out b0)) return; // end of (possibly concatenated) streams
                if (b0 < 0) throw new InvalidDataException("bzip2 헤더가 없습니다");
                b1 = input.ReadByte(); b2 = input.ReadByte(); b3 = input.ReadByte();
                if (b0 != 'B' || b1 != 'Z' || b2 != 'h' || b3 < '1' || b3 > '9')
                {
                    if (b0 == 0) return; // zero padding after the stream
                    throw new InvalidDataException("bzip2 헤더가 올바르지 않습니다");
                }
                int blockMax = (b3 - '0') * 100000;
                br = new MsbBitReader(input);
                while (true)
                {
                    ulong magic = ((ulong)br.Bits(24) << 24) | br.Bits(24);
                    br.Bits(32); // crc
                    if (magic == 0x314159265359UL) DecodeBlock(br, output, blockMax, true);
                    else if (magic == 0x177245385090UL) break;
                    else throw new InvalidDataException("bzip2 블록 표시가 올바르지 않습니다");
                }
            }
        }

        public static void DecodeAlz(Stream input, Stream output)
        {
            var br = new MsbBitReader(input);
            while (true)
            {
                uint marker = br.Bits(32);
                if (marker == 0x444C5A02) return;          // "DLZ\x02" end of stream
                if (marker != 0x444C5A01) throw new InvalidDataException("ALZ bzip2 블록 표시가 올바르지 않습니다");
                DecodeBlock(br, output, 900000, false);
            }
        }

        private static void DecodeBlock(MsbBitReader br, Stream output, int blockMax, bool hasRandomBit)
        {
            if (hasRandomBit && br.Bit() != 0) throw new InvalidDataException("지원하지 않는 bzip2 형식(randomised)입니다");
            int origPtr = (int)br.Bits(24);

            // symbol map
            var seqToUnseq = new byte[256];
            int nInUse = 0;
            uint used = br.Bits(16);
            for (int i = 0; i < 16; i++)
            {
                if ((used & (0x8000u >> i)) == 0) continue;
                uint b = br.Bits(16);
                for (int j = 0; j < 16; j++) if ((b & (0x8000u >> j)) != 0) seqToUnseq[nInUse++] = (byte)(i * 16 + j);
            }
            if (nInUse == 0) throw new InvalidDataException("bzip2 데이터 오류");
            int alphaSize = nInUse + 2;

            int nGroups = (int)br.Bits(3);
            if (nGroups < 2 || nGroups > MaxGroups) throw new InvalidDataException("bzip2 데이터 오류");
            int nSelectors = (int)br.Bits(15);
            if (nSelectors < 1) throw new InvalidDataException("bzip2 데이터 오류");
            var selectorMtf = new byte[Math.Min(nSelectors, MaxSelectors)];
            for (int i = 0; i < nSelectors; i++)
            {
                int j = 0;
                while (br.Bit() != 0) { j++; if (j >= nGroups) throw new InvalidDataException("bzip2 데이터 오류"); }
                if (i < MaxSelectors) selectorMtf[i] = (byte)j;
            }
            nSelectors = Math.Min(nSelectors, MaxSelectors);
            var pos = new byte[MaxGroups];
            for (int v = 0; v < nGroups; v++) pos[v] = (byte)v;
            var selector = new byte[nSelectors];
            for (int i = 0; i < nSelectors; i++)
            {
                int v = selectorMtf[i];
                byte tmp = pos[v];
                while (v > 0) { pos[v] = pos[v - 1]; v--; }
                pos[0] = tmp;
                selector[i] = tmp;
            }

            // code lengths and decode tables
            var limit = new int[nGroups, MaxCodeLen + 1];
            var bases = new int[nGroups, MaxCodeLen + 2];
            var perm = new int[nGroups, MaxAlpha];
            var minLens = new int[nGroups];
            var len = new byte[MaxAlpha];
            for (int t = 0; t < nGroups; t++)
            {
                int curr = (int)br.Bits(5);
                int minLen = 32, maxLen = 0;
                for (int i = 0; i < alphaSize; i++)
                {
                    while (true)
                    {
                        if (curr < 1 || curr > 20) throw new InvalidDataException("bzip2 데이터 오류");
                        if (br.Bit() == 0) break;
                        if (br.Bit() == 0) curr++; else curr--;
                    }
                    len[i] = (byte)curr;
                    if (curr > maxLen) maxLen = curr;
                    if (curr < minLen) minLen = curr;
                }
                // hbCreateDecodeTables
                int pp = 0;
                for (int i = minLen; i <= maxLen; i++)
                    for (int j = 0; j < alphaSize; j++) if (len[j] == i) perm[t, pp++] = j;
                var b = new int[MaxCodeLen + 2];
                for (int i = 0; i < alphaSize; i++) b[len[i] + 1]++;
                for (int i = 1; i < b.Length; i++) b[i] += b[i - 1];
                int vec = 0;
                for (int i = minLen; i <= maxLen; i++)
                {
                    vec += b[i + 1] - b[i];
                    limit[t, i] = vec - 1;
                    vec <<= 1;
                }
                for (int i = minLen + 1; i <= maxLen; i++) b[i] = ((limit[t, i - 1] + 1) << 1) - b[i];
                for (int i = 0; i < b.Length; i++) bases[t, i] = b[i];
                for (int i = maxLen + 1; i <= MaxCodeLen; i++) limit[t, i] = int.MaxValue;
                for (int i = 0; i < minLen; i++) limit[t, i] = -1;
                minLens[t] = minLen;
            }

            // MTF / RLE2 decode
            int eob = nInUse + 1;
            var tt = new int[blockMax];
            var unzftab = new int[256];
            var yy = new byte[256];
            for (int i = 0; i < 256; i++) yy[i] = (byte)i;
            int nblock = 0, groupNo = -1, groupPos = 0, gSel = 0;

            int NextSym()
            {
                if (groupPos == 0)
                {
                    groupNo++;
                    if (groupNo >= nSelectors) throw new InvalidDataException("bzip2 데이터 오류");
                    groupPos = GroupSize;
                    gSel = selector[groupNo];
                }
                groupPos--;
                int zn = minLens[gSel];
                int zvec = (int)br.Bits(zn);
                while (zvec > limit[gSel, zn])
                {
                    zn++;
                    if (zn > 20) throw new InvalidDataException("bzip2 데이터 오류");
                    zvec = (zvec << 1) | br.Bit();
                }
                int idx = zvec - bases[gSel, zn];
                if (idx < 0 || idx >= MaxAlpha) throw new InvalidDataException("bzip2 데이터 오류");
                return perm[gSel, idx];
            }

            int sym = NextSym();
            while (true)
            {
                if (sym == eob) break;
                if (sym == 0 || sym == 1)
                {
                    int es = 0, n = 1;
                    do
                    {
                        es += sym == 0 ? n : 2 * n;
                        n <<= 1;
                        if (n > (1 << 24)) throw new InvalidDataException("bzip2 데이터 오류");
                        sym = NextSym();
                    } while (sym == 0 || sym == 1);
                    byte uc = seqToUnseq[yy[0]];
                    unzftab[uc] += es;
                    if (nblock + es > blockMax) throw new InvalidDataException("bzip2 블록이 너무 큽니다");
                    for (int k = 0; k < es; k++) tt[nblock++] = uc;
                    continue;
                }
                else
                {
                    int nn = sym - 1;
                    if (nn >= nInUse) throw new InvalidDataException("bzip2 데이터 오류");
                    byte v = yy[nn];
                    Buffer.BlockCopy(yy, 0, yy, 1, nn);
                    yy[0] = v;
                    byte uc = seqToUnseq[v];
                    unzftab[uc]++;
                    if (nblock >= blockMax) throw new InvalidDataException("bzip2 블록이 너무 큽니다");
                    tt[nblock++] = uc;
                    sym = NextSym();
                }
            }
            if (origPtr < 0 || origPtr >= Math.Max(1, nblock)) throw new InvalidDataException("bzip2 데이터 오류");

            // inverse BWT
            var cftab = new int[257];
            for (int i = 0; i < 256; i++) cftab[i + 1] = cftab[i] + unzftab[i];
            for (int i = 0; i < nblock; i++)
            {
                int uc = tt[i] & 0xFF;
                tt[cftab[uc]++] |= i << 8;
            }

            // undo the initial run-length encoding while writing
            var outBuf = new byte[1 << 16];
            int op = 0;
            int tPos = tt[origPtr] >> 8;
            int last = -1, run = 0;
            for (int k = 0; k < nblock; k++)
            {
                tPos = tt[tPos];
                int ch = tPos & 0xFF;
                tPos >>= 8;
                if (run == 4)
                {
                    for (int r = 0; r < ch; r++)
                    {
                        outBuf[op++] = (byte)last;
                        if (op == outBuf.Length) { output.Write(outBuf, 0, op); op = 0; }
                    }
                    run = 0;
                    last = -1;
                    continue;
                }
                if (ch == last) run++; else { last = ch; run = 1; }
                outBuf[op++] = (byte)ch;
                if (op == outBuf.Length) { output.Write(outBuf, 0, op); op = 0; }
            }
            if (op > 0) output.Write(outBuf, 0, op);
        }
    }

    /// <summary>LZMA decoder (LZMA SDK algorithm). Decodes until outSize bytes or the end marker.</summary>
    internal sealed class LzmaDecoder
    {
        private const int kNumStates = 12, kNumPosBitsMax = 4, kNumLenToPosStates = 4, kNumAlignBits = 4,
            kStartPosModelIndex = 4, kEndPosModelIndex = 14, kNumFullDistances = 1 << (kEndPosModelIndex >> 1),
            kMatchMinLen = 2, kNumLowLenBits = 3, kNumMidLenBits = 3, kNumHighLenBits = 8;
        private const int kNumBitModelTotalBits = 11, kBitModelTotal = 1 << kNumBitModelTotalBits, kNumMoveBits = 5;
        private const uint kTopValue = 1 << 24;

        private Stream _in;
        private uint _range, _code;

        private int _lc, _lp, _pb;
        private uint _dictSize;
        private byte[] _win;
        private uint _winPos, _winSize;
        private bool _winFull;
        private ushort[] _literal;
        private readonly ushort[] _isMatch = new ushort[kNumStates << kNumPosBitsMax];
        private readonly ushort[] _isRep = new ushort[kNumStates], _isRepG0 = new ushort[kNumStates], _isRepG1 = new ushort[kNumStates], _isRepG2 = new ushort[kNumStates];
        private readonly ushort[] _isRep0Long = new ushort[kNumStates << kNumPosBitsMax];
        private readonly ushort[][] _posSlot = new ushort[kNumLenToPosStates][];
        private readonly ushort[] _posDecoders = new ushort[kNumFullDistances - kEndPosModelIndex];
        private readonly ushort[] _align = new ushort[1 << kNumAlignBits];
        private readonly LenDecoder _len = new LenDecoder(), _repLen = new LenDecoder();

        private sealed class LenDecoder
        {
            public ushort Choice, Choice2;
            public ushort[][] Low = new ushort[1 << kNumPosBitsMax][], Mid = new ushort[1 << kNumPosBitsMax][];
            public ushort[] High = new ushort[1 << kNumHighLenBits];
            public void Init(int numPosStates)
            {
                Choice = Choice2 = kBitModelTotal >> 1;
                for (int i = 0; i < numPosStates; i++)
                {
                    Low[i] = Probs(1 << kNumLowLenBits);
                    Mid[i] = Probs(1 << kNumMidLenBits);
                }
                High = Probs(1 << kNumHighLenBits);
            }
        }

        private static ushort[] Probs(int n) { var a = new ushort[n]; for (int i = 0; i < n; i++) a[i] = kBitModelTotal >> 1; return a; }

        public LzmaDecoder(byte[] props5)
        {
            int d = props5[0];
            if (d >= 9 * 5 * 5) throw new InvalidDataException("LZMA 속성이 올바르지 않습니다");
            _lc = d % 9; d /= 9; _lp = d % 5; _pb = d / 5;
            _dictSize = BitConverter.ToUInt32(props5, 1);
        }

        private void Normalize()
        {
            if (_range < kTopValue)
            {
                int b = _in.ReadByte();
                if (b < 0) throw new InvalidDataException("LZMA 데이터가 예상보다 일찍 끝났습니다");
                _code = (_code << 8) | (uint)b;
                _range <<= 8;
            }
        }

        private int DecodeBit(ushort[] probs, int i)
        {
            uint bound = (_range >> kNumBitModelTotalBits) * probs[i];
            int r;
            if (_code < bound) { _range = bound; probs[i] += (ushort)((kBitModelTotal - probs[i]) >> kNumMoveBits); r = 0; }
            else { _range -= bound; _code -= bound; probs[i] -= (ushort)(probs[i] >> kNumMoveBits); r = 1; }
            Normalize();
            return r;
        }

        private int DecodeBit(ref ushort prob)
        {
            uint bound = (_range >> kNumBitModelTotalBits) * prob;
            int r;
            if (_code < bound) { _range = bound; prob += (ushort)((kBitModelTotal - prob) >> kNumMoveBits); r = 0; }
            else { _range -= bound; _code -= bound; prob -= (ushort)(prob >> kNumMoveBits); r = 1; }
            Normalize();
            return r;
        }

        private uint DirectBits(int n)
        {
            uint res = 0;
            for (int i = 0; i < n; i++)
            {
                _range >>= 1;
                uint t = (_code - _range) >> 31; // 1 if code < range
                _code -= _range & (t - 1);
                res = (res << 1) | (1 - t);
                Normalize();
            }
            return res;
        }

        private int BitTree(ushort[] probs, int numBits)
        {
            int m = 1;
            for (int i = 0; i < numBits; i++) m = (m << 1) + DecodeBit(probs, m);
            return m - (1 << numBits);
        }

        private int BitTreeReverse(ushort[] probs, int offset, int numBits)
        {
            int m = 1, sym = 0;
            for (int i = 0; i < numBits; i++)
            {
                int bit = DecodeBit(probs, offset + m);
                m = (m << 1) + bit;
                sym |= bit << i;
            }
            return sym;
        }

        private int DecodeLen(LenDecoder d, int posState)
        {
            if (DecodeBit(ref d.Choice) == 0) return BitTree(d.Low[posState], kNumLowLenBits);
            if (DecodeBit(ref d.Choice2) == 0) return (1 << kNumLowLenBits) + BitTree(d.Mid[posState], kNumMidLenBits);
            return (1 << kNumLowLenBits) + (1 << kNumMidLenBits) + BitTree(d.High, kNumHighLenBits);
        }

        private byte GetByte(uint dist)
        {
            uint p = _winPos >= dist ? _winPos - dist : _winSize - dist + _winPos;
            return _win[p];
        }

        /// <summary>Decodes from input (positioned at the range coder start) into output.</summary>
        public void Decode(Stream input, Stream output, long outSize, Action<int> progress = null)
        {
            _in = input;
            _winSize = Math.Max(_dictSize, 1u << 12);
            if (outSize >= 0 && outSize < _winSize) _winSize = (uint)Math.Max(outSize, 1);
            _win = new byte[_winSize];
            _winPos = 0; _winFull = false;
            _literal = Probs(0x300 << (_lc + _lp));
            for (int i = 0; i < _isMatch.Length; i++) { _isMatch[i] = kBitModelTotal >> 1; _isRep0Long[i] = kBitModelTotal >> 1; }
            for (int i = 0; i < kNumStates; i++) _isRep[i] = _isRepG0[i] = _isRepG1[i] = _isRepG2[i] = kBitModelTotal >> 1;
            for (int i = 0; i < kNumLenToPosStates; i++) _posSlot[i] = Probs(1 << 6);
            for (int i = 0; i < _posDecoders.Length; i++) _posDecoders[i] = kBitModelTotal >> 1;
            for (int i = 0; i < _align.Length; i++) _align[i] = kBitModelTotal >> 1;
            _len.Init(1 << _pb); _repLen.Init(1 << _pb);

            _code = 0; _range = 0xFFFFFFFF;
            for (int i = 0; i < 5; i++)
            {
                int b = input.ReadByte();
                if (b < 0) throw new InvalidDataException("LZMA 데이터가 너무 짧습니다");
                _code = (_code << 8) | (uint)b;
            }

            uint state = 0, rep0 = 0, rep1 = 0, rep2 = 0, rep3 = 0;
            long total = 0;
            int pbMask = (1 << _pb) - 1, lpMask = (1 << _lp) - 1;
            byte prevByte = 0;
            var outBuf = new byte[1 << 16];
            int op = 0;

            void Put(byte b)
            {
                _win[_winPos++] = b;
                if (_winPos == _winSize) { _winPos = 0; _winFull = true; }
                outBuf[op++] = b;
                if (op == outBuf.Length) { output.Write(outBuf, 0, op); progress?.Invoke(op); op = 0; }
                total++;
            }

            while (outSize < 0 || total < outSize)
            {
                int posState = (int)total & pbMask;
                if (DecodeBit(_isMatch, (int)(state << kNumPosBitsMax) + posState) == 0)
                {
                    int litState = (((int)total & lpMask) << _lc) + (prevByte >> (8 - _lc));
                    int off = 0x300 * litState;
                    int sym = 1;
                    if (state >= 7)
                    {
                        int matchByte = GetByte(rep0 + 1);
                        do
                        {
                            int matchBit = (matchByte >> 7) & 1;
                            matchByte <<= 1;
                            int bit = DecodeBit(_literal, off + ((1 + matchBit) << 8) + sym);
                            sym = (sym << 1) | bit;
                            if (matchBit != bit) break;
                        } while (sym < 0x100);
                    }
                    while (sym < 0x100) sym = (sym << 1) | DecodeBit(_literal, off + sym);
                    prevByte = (byte)sym;
                    Put(prevByte);
                    state = state < 4 ? 0 : state < 10 ? state - 3 : state - 6;
                    continue;
                }

                int len;
                if (DecodeBit(_isRep, (int)state) != 0)
                {
                    if (total == 0) throw new InvalidDataException("LZMA 데이터 오류");
                    if (DecodeBit(_isRepG0, (int)state) == 0)
                    {
                        if (DecodeBit(_isRep0Long, (int)(state << kNumPosBitsMax) + posState) == 0)
                        {
                            state = state < 7 ? 9u : 11u;
                            prevByte = GetByte(rep0 + 1);
                            Put(prevByte);
                            continue;
                        }
                    }
                    else
                    {
                        uint dist;
                        if (DecodeBit(_isRepG1, (int)state) == 0) dist = rep1;
                        else
                        {
                            if (DecodeBit(_isRepG2, (int)state) == 0) dist = rep2;
                            else { dist = rep3; rep3 = rep2; }
                            rep2 = rep1;
                        }
                        rep1 = rep0; rep0 = dist;
                    }
                    len = DecodeLen(_repLen, posState);
                    state = state < 7 ? 8u : 11u;
                }
                else
                {
                    rep3 = rep2; rep2 = rep1; rep1 = rep0;
                    len = DecodeLen(_len, posState);
                    state = state < 7 ? 7u : 10u;
                    int lenState = Math.Min(len, kNumLenToPosStates - 1);
                    int posSlot = BitTree(_posSlot[lenState], 6);
                    if (posSlot >= kStartPosModelIndex)
                    {
                        int numDirect = (posSlot >> 1) - 1;
                        rep0 = (uint)((2 | (posSlot & 1)) << numDirect);
                        if (posSlot < kEndPosModelIndex)
                            rep0 += (uint)BitTreeReverse(_posDecoders, (int)rep0 - posSlot - 1, numDirect);
                        else
                        {
                            rep0 += DirectBits(numDirect - kNumAlignBits) << kNumAlignBits;
                            rep0 += (uint)BitTreeReverse(_align, 0, kNumAlignBits);
                            if (rep0 == 0xFFFFFFFF) break; // end marker
                        }
                    }
                    else rep0 = (uint)posSlot;
                    if (rep0 >= total && !_winFull) throw new InvalidDataException("LZMA 데이터 오류");
                    if (rep0 >= _winSize) throw new InvalidDataException("LZMA 데이터 오류");
                }
                len += kMatchMinLen;
                for (int i = 0; i < len && (outSize < 0 || total < outSize); i++)
                {
                    prevByte = GetByte(rep0 + 1);
                    Put(prevByte);
                }
            }
            if (op > 0) { output.Write(outBuf, 0, op); progress?.Invoke(op); }
        }
    }

    /// <summary>ESTsoft AZO decoder (port of the MIT-licensed libazo crate).</summary>
    internal static class AzoDecoder
    {
        public static void Decode(Stream input, Stream output, Action<int> progress = null)
        {
            int v = input.ReadByte(), flags = input.ReadByte();
            if (v != 0x31) throw new InvalidDataException("지원하지 않는 AZO 버전입니다");
            bool x86 = (flags & 1) != 0;
            var hdr = new byte[12];
            while (true)
            {
                ReadExact(input, hdr, 12);
                uint blockSize = BE(hdr, 0), compSize = BE(hdr, 4), check = BE(hdr, 8);
                if (blockSize == 0 && compSize == 0) break;
                if ((blockSize ^ compSize) != check || blockSize == 0 || compSize == 0 || blockSize > 256u * 1024 * 1024)
                    throw new InvalidDataException("AZO 블록 헤더가 올바르지 않습니다");
                var data = new byte[compSize];
                ReadExact(input, data, (int)compSize);
                byte[] outBlock = compSize == blockSize ? data : DecodeBlock(data, (int)blockSize);
                if (x86) X86Filter(outBlock);
                output.Write(outBlock, 0, outBlock.Length);
                progress?.Invoke(outBlock.Length);
            }
        }

        private static uint BE(byte[] b, int o) => (uint)(b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3]);

        internal static void ReadExact(Stream s, byte[] buf, int n)
        {
            int got = 0;
            while (got < n)
            {
                int r = s.Read(buf, got, n - got);
                if (r <= 0) throw new InvalidDataException("압축 데이터가 예상보다 일찍 끝났습니다");
                got += r;
            }
        }

        private static byte[] DecodeBlock(byte[] data, int blockSize)
        {
            var rc = new Range(data);
            rc.Init();
            var buf = new byte[blockSize];
            var alpha = new PredictProb(256, 256, 5);
            var matchFlag = new BoolState(8);
            var match = new MatchCode();
            buf[0] = (byte)alpha.Decode(rc, 0);
            int i = 1;
            while (i < blockSize)
            {
                if (matchFlag.Decode(rc) == 0)
                {
                    buf[i] = (byte)alpha.Decode(rc, buf[i - 1]);
                    i++;
                }
                else
                {
                    match.Decode(rc, (uint)i, out uint dist, out uint len);
                    if (dist == 0 || dist > i) throw new InvalidDataException("AZO 데이터 오류");
                    int src = i - (int)dist;
                    for (int j = 0; j < len && i + j < blockSize; j++) buf[i + j] = buf[src + j];
                    i += (int)len;
                }
            }
            if (!rc.FullyConsumed) throw new InvalidDataException("AZO 데이터 오류 (남는 데이터)");
            return buf;
        }

        private static void X86Filter(byte[] buf)
        {
            int size = buf.Length;
            if (size < 5) return;
            int i = 0;
            while (i < size - 4)
            {
                if (buf[i] == 0xE8 || buf[i] == 0xE9)
                {
                    if (buf[i + 4] == 0x00 || buf[i + 4] == 0xFF)
                    {
                        uint addr = BitConverter.ToUInt32(buf, i + 1);
                        addr = unchecked(addr - (uint)i);
                        if (((addr >> 24) & 1) != 0) addr |= 0xFF000000; else addr &= 0x00FFFFFF;
                        var b = BitConverter.GetBytes(addr);
                        Buffer.BlockCopy(b, 0, buf, i + 1, 4);
                    }
                    i += 5;
                }
                else i++;
            }
        }

        private sealed class Range
        {
            private const ulong MSB = 0x80000000, SMSB = 0x40000000, MASK = 0xFFFFFFFF;
            private readonly byte[] _d;
            private int _pos, _bitPos;
            private byte _cur;
            private ulong _low, _up = MASK, _tag;
            public Range(byte[] d) { _d = d; }

            private uint Bit()
            {
                if (_bitPos == 0)
                {
                    _cur = _pos < _d.Length ? _d[_pos] : (byte)0;
                    if (_pos < _d.Length) _pos++;
                    _bitPos = 8;
                }
                _bitPos--;
                return (uint)((_cur >> _bitPos) & 1);
            }

            public bool FullyConsumed => _pos == _d.Length;

            public void Init() { _tag = 0; for (int i = 0; i < 32; i++) _tag = ((_tag << 1) | Bit()) & MASK; }

            public uint Uniform(int totalBit)
            {
                ulong t = Math.Max((_up - _low + 1) >> totalBit, 1);
                ulong value = Math.Min((_tag - _low) / t, (1UL << totalBit) - 1);
                _up = _low + t * (value + 1) - 1;
                _low += t * value;
                Rescale();
                return (uint)value;
            }

            public uint Boolean(uint cum, int totalBit)
            {
                ulong t = Math.Max((_up - _low + 1) >> totalBit, 1);
                ulong v = (_tag - _low) / t;
                if (v >= cum) { _low += t * cum; Rescale(); return 1; }
                _up = _low + t * cum - 1;
                Rescale();
                return 0;
            }

            private void Rescale()
            {
                while ((_low & MSB) == (_up & MSB))
                {
                    ulong bit = Bit();
                    _tag = ((_tag << 1) & MASK) | bit;
                    _low = (_low << 1) & MASK;
                    _up = ((_up << 1) & MASK) | 1;
                }
                while ((_low & SMSB) != 0 && (_up & SMSB) == 0)
                {
                    ulong bit = Bit();
                    _tag = (((_tag << 1) | bit) ^ MSB) & MASK;
                    _low = (_low << 1) & (MASK >> 1);
                    _up = ((_up << 1) | 1 | MSB) & MASK;
                }
            }
        }

        private sealed class BoolState
        {
            private int _state;
            private readonly uint[] _prob;
            private readonly int _bits;
            public BoolState(int bits) { _bits = bits; _prob = new uint[1 << bits]; for (int i = 0; i < _prob.Length; i++) _prob[i] = 2048; }
            public uint Decode(Range rc)
            {
                uint bit = rc.Boolean(_prob[_state], 12);
                if (bit == 0) _prob[_state] += (4096 - _prob[_state]) >> 6; else _prob[_state] -= _prob[_state] >> 6;
                _state = ((_state << 1) | (int)bit) & ((1 << _bits) - 1);
                return bit;
            }
        }

        private sealed class BitProb
        {
            public readonly uint[] P;
            public readonly int BitN;
            public BitProb(int n)
            {
                int b = 0;
                while ((1 << b) < n) b++;
                BitN = n <= 1 ? 0 : b;
                P = new uint[1 << BitN];
                for (int i = 0; i < P.Length; i++) P[i] = 512;
            }
            public uint Decode(Range rc)
            {
                uint value = 0; int pre = 1;
                for (int i = BitN - 1; i >= 0; i--)
                {
                    uint bit = rc.Boolean(P[pre], 10);
                    Upd(pre, bit);
                    if (bit != 0) value |= 1u << i;
                    pre = (pre << 1) | (int)bit;
                }
                return value;
            }
            public void Update(uint value)
            {
                int pre = 1;
                for (int i = BitN - 1; i >= 0; i--)
                {
                    uint v = (value >> i) & 1;
                    Upd(pre, v);
                    pre = (pre << 1) | (int)v;
                }
            }
            private void Upd(int idx, uint bit)
            {
                if (bit == 0) P[idx] += (1024 - P[idx]) >> 4; else P[idx] -= P[idx] >> 4;
            }
            public static int Compare(BitProb m1, BitProb m2, uint value)
            {
                uint prod1 = 1, prod2 = 1; int pre = 1;
                uint overflow = 1023u << 22;
                for (int i = m1.BitN - 1; i >= 0; i--)
                {
                    uint p1 = m1.P[pre], p2 = m2.P[pre], v = (value >> i) & 1;
                    if (((prod1 | prod2) & overflow) != 0) { prod1 >>= 10; prod2 >>= 10; }
                    unchecked
                    {
                        if (v != 0) { prod1 *= 1024 - p1; prod2 *= 1024 - p2; }
                        else { prod1 *= p1; prod2 *= p2; }
                    }
                    pre = (pre << 1) | (int)v;
                }
                return prod1 > prod2 ? 1 : prod1 < prod2 ? -1 : 0;
            }
        }

        private sealed class PredictProb
        {
            private readonly BitProb[] _p1, _p2;
            private readonly int[] _lucky;
            private readonly int _shift;
            public PredictProb(int key, int n, int shift)
            {
                _shift = shift;
                _p1 = new BitProb[key];
                for (int i = 0; i < key; i++) _p1[i] = new BitProb(n);
                _p2 = new BitProb[key >> shift];
                for (int i = 0; i < _p2.Length; i++) _p2[i] = new BitProb(n);
                _lucky = new int[key];
            }
            public uint Decode(Range rc, int ctx)
            {
                int coarse = ctx >> _shift;
                uint v;
                if (_lucky[ctx] >= 0) { v = _p1[ctx].Decode(rc); _p2[coarse].Update(v); }
                else { v = _p2[coarse].Decode(rc); _p1[ctx].Update(v); }
                int r = BitProb.Compare(_p1[ctx], _p2[coarse], v);
                if (r > 0) _lucky[ctx]++;
                if (r < 0) _lucky[ctx]--;
                return v;
            }
        }

        private sealed class Table
        {
            public readonly uint[] Base = new uint[128], Extra = new uint[128];
            public Table(uint min, uint sgap, uint gap)
            {
                Base[0] = min;
                for (int i = 1; i < 128; i++)
                {
                    Extra[i] = (uint)i < sgap ? 0 : ((uint)i - sgap) / gap;
                    Base[i] = Base[i - 1] + (1u << (int)Extra[i - 1]);
                }
            }
            public int CodeFor(uint value)
            {
                // partition_point(b <= value) - 1, saturating
                int lo = 0, hi = 128;
                while (lo < hi) { int mid = (lo + hi) / 2; if (Base[mid] <= value) lo = mid + 1; else hi = mid; }
                return Math.Max(0, lo - 1);
            }
        }

        private static void Push(uint[] a, uint v) { for (int i = a.Length - 1; i >= 1; i--) a[i] = a[i - 1]; a[0] = v; }
        private static uint Promote(uint[] a, int idx) { uint v = a[idx]; for (int i = idx; i >= 1; i--) a[i] = a[i - 1]; a[0] = v; return v; }

        private sealed class MatchCode
        {
            private readonly BoolState _reuse = new BoolState(8), _distRecent = new BoolState(8), _matchRecent = new BoolState(8);
            private readonly BitProb _distRecentSel = new BitProb(2), _distCode = new BitProb(128), _matchRecentSel = new BitProb(2), _matchId = new BitProb(128);
            private readonly PredictProb _lenModel = new PredictProb(128, 128, 4);
            private readonly uint[] _recentDists = { 1, 2 }, _recentMatches = { 0, 1 };
            private readonly uint[] _matchLens = new uint[128], _matchStarts = new uint[128];
            private readonly Table _dist = new Table(1, 16, 4), _len = new Table(2, 32, 8);

            public MatchCode() { for (uint i = 0; i < 128; i++) _matchLens[i] = 2 + i; }

            public void Decode(Range rc, uint writePos, out uint dist, out uint len)
            {
                if (_reuse.Decode(rc) == 1)
                {
                    int id;
                    if (_matchRecent.Decode(rc) != 0) id = (int)Promote(_recentMatches, (int)_matchRecentSel.Decode(rc));
                    else { uint v = _matchId.Decode(rc); Push(_recentMatches, v); id = (int)v; }
                    len = Promote(_matchLens, id);
                    uint start = Promote(_matchStarts, id);
                    dist = unchecked(writePos - start);
                    return;
                }
                if (_distRecent.Decode(rc) != 0) dist = Promote(_recentDists, (int)_distRecentSel.Decode(rc));
                else
                {
                    int code = (int)_distCode.Decode(rc);
                    dist = _dist.Base[code];
                    if (_dist.Extra[code] > 0) dist += rc.Uniform((int)_dist.Extra[code]);
                    Push(_recentDists, dist);
                }
                int dc = _dist.CodeFor(dist);
                int lc = (int)_lenModel.Decode(rc, dc);
                len = _len.Base[lc];
                if (_len.Extra[lc] > 0) len += rc.Uniform((int)_len.Extra[lc]);
                Push(_matchLens, len);
                Push(_matchStarts, writePos);
            }
        }
    }
}
