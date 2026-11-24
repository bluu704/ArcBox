using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ArcBox.Core.Korean
{
    /// <summary>ACE 1.0/2.0 decompressor. C# port of the BSD-licensed acefile (Daniel Roethlisberger).</summary>
    internal sealed class AceDecoder
    {
        internal static InvalidDataException Corrupt(string m) => new InvalidDataException("ACE 데이터 오류: " + m);

        // ------------------------------------------------------------------ bit stream (32-bit LE words, MSB first)
        internal sealed class BitStream
        {
            private readonly Stream _s;
            private readonly List<uint> _words = new List<uint>();
            private int _wordBase;          // index of _words[0] in the stream
            private long _pos;              // bit position (absolute)
            private bool _eof;

            public BitStream(Stream s) { _s = s; }

            private uint Word(long idx)
            {
                while (idx - _wordBase >= _words.Count)
                {
                    if (_eof) { _words.Add(0); continue; }
                    var b = new byte[4];
                    int n = 0;
                    while (n < 4) { int r = _s.Read(b, n, 4 - n); if (r <= 0) break; n += r; }
                    if (n == 0) { _eof = true; _words.Add(0); continue; }
                    _words.Add(BitConverter.ToUInt32(b, 0));
                    if (n < 4) _eof = true;
                }
                // drop old words
                if (_words.Count > 4096 && idx - _wordBase > 2048)
                {
                    int drop = (int)(idx - _wordBase) - 16;
                    _words.RemoveRange(0, drop);
                    _wordBase += drop;
                }
                return _words[(int)(idx - _wordBase)];
            }

            public uint Peek(int bits)
            {
                if (bits == 0) return 0;
                long w = _pos >> 5;
                int off = (int)(_pos & 31);
                ulong v = ((ulong)Word(w) << 32) | Word(w + 1);
                return (uint)((v << off) >> (64 - bits));
            }

            public void Skip(int bits) { _pos += bits; }
            public uint Read(int bits) { if (bits == 0) return 0; uint v = Peek(bits); _pos += bits; return v; }

            public long ReadLong(int bits)
            {
                long v = 0;
                while (bits > 24) { v = (v << 24) | Read(24); bits -= 24; }
                return (v << bits) | Read(bits);
            }

            public int GolombRice(int rBits, bool signed)
            {
                long value = rBits == 0 ? 0 : Read(rBits);
                while (Read(1) == 1)
                {
                    value += 1L << rBits;
                    if (value > int.MaxValue) throw Corrupt("golomb");
                }
                if (!signed) return (int)value;
                return (value & 1) != 0 ? -(int)(value >> 1) - 1 : (int)(value >> 1);
            }

            public uint KnownWidthUInt(int bits)
            {
                if (bits < 2) return (uint)bits;
                bits -= 1;
                return Read(bits) + (1u << bits);
            }
        }

        // ------------------------------------------------------------------ mode
        public const int ModeLz77 = 0, ModeDelta = 1, ModeExe = 2, ModeSound8 = 3, ModeSound16 = 4, ModeSound32A = 5, ModeSound32B = 6, ModePic = 7;

        private sealed class AceMode
        {
            public int Mode, DeltaDist, DeltaLen, ExeMode;
            public static AceMode Read(BitStream bs)
            {
                var m = new AceMode { Mode = (int)bs.Read(8) };
                if (m.Mode == ModeDelta) { m.DeltaDist = (int)bs.Read(8); m.DeltaLen = (int)bs.Read(17); }
                else if (m.Mode == ModeExe) m.ExeMode = (int)bs.Read(8);
                return m;
            }
        }

        // ------------------------------------------------------------------ huffman
        private sealed class HuffTree
        {
            public int[] Codes; public int[] Widths; public int MaxWidth;
            public int ReadSymbol(BitStream bs)
            {
                uint c = bs.Peek(MaxWidth);
                if (c >= Codes.Length) throw Corrupt("huffman");
                int sym = Codes[c];
                bs.Skip(Widths[sym]);
                return sym;
            }
        }

        private static void QuickSort(int[] keys, int[] values, int left, int right)
        {
            int nl = left, nr = right, m = keys[right];
            while (true)
            {
                while (keys[nl] > m) nl++;
                while (keys[nr] < m) nr--;
                if (nl <= nr)
                {
                    int t = keys[nl]; keys[nl] = keys[nr]; keys[nr] = t;
                    t = values[nl]; values[nl] = values[nr]; values[nr] = t;
                    nl++; nr--;
                }
                if (nl >= nr) break;
            }
            if (left < nr)
            {
                if (left < nr - 1) QuickSort(keys, values, left, nr);
                else if (keys[left] < keys[nr]) { int t = keys[left]; keys[left] = keys[nr]; keys[nr] = t; t = values[left]; values[left] = values[nr]; values[nr] = t; }
            }
            if (right > nl)
            {
                if (nl < right - 1) QuickSort(keys, values, nl, right);
                else if (keys[nl] < keys[right]) { int t = keys[nl]; keys[nl] = keys[right]; keys[right] = t; t = values[nl]; values[nl] = values[right]; values[right] = t; }
            }
        }

        private static HuffTree MakeTree(int[] widths, int maxWidth)
        {
            int n = widths.Length;
            var syms = new int[n];
            var sw = (int[])widths.Clone();
            for (int i = 0; i < n; i++) syms[i] = i;
            if (n > 0) QuickSort(sw, syms, 0, n - 1);
            int used = 0;
            while (used < n && sw[used] != 0) used++;
            if (used < 2)
            {
                widths[syms[0]] = 1;
                if (used == 0) used++;
            }
            int maxCodes = 1 << maxWidth;
            var codes = new List<int>(maxCodes);
            for (int k = used - 1; k >= 0; k--)
            {
                int wdt = sw[k];
                if (wdt > maxWidth) throw Corrupt("width");
                int repeat = 1 << (maxWidth - wdt);
                for (int r = 0; r < repeat; r++) codes.Add(syms[k]);
                if (codes.Count > maxCodes) throw Corrupt("codes");
            }
            return new HuffTree { Codes = codes.ToArray(), Widths = widths, MaxWidth = maxWidth };
        }

        private static HuffTree ReadTree(BitStream bs, int maxWidth, int numCodes)
        {
            int numWidths = (int)bs.Read(9) + 1;
            if (numWidths > numCodes + 1) numWidths = numCodes + 1;
            int lower = (int)bs.Read(4), upper = (int)bs.Read(4);
            var ww = new int[upper + 1];
            for (int i = 0; i <= upper; i++) ww[i] = (int)bs.Read(3);
            var wtree = MakeTree(ww, 7);
            var widths = new List<int>(numWidths);
            while (widths.Count < numWidths)
            {
                int sym = wtree.ReadSymbol(bs);
                if (sym < upper) widths.Add(sym);
                else
                {
                    int len = (int)bs.Read(4) + 4;
                    len = Math.Min(len, numWidths - widths.Count);
                    for (int i = 0; i < len; i++) widths.Add(0);
                }
            }
            var w = widths.ToArray();
            if (upper > 0) for (int i = 1; i < w.Length; i++) w[i] = (w[i] + w[i - 1]) % upper;
            for (int i = 0; i < w.Length; i++) if (w[i] > 0) w[i] += lower;
            return MakeTree(w, maxWidth);
        }

        // ------------------------------------------------------------------ dictionary
        private const int DicSize = 1 << 22;
        private readonly byte[] _dic = new byte[DicSize];
        private long _dicPos;

        private void DicPut(byte b) { _dic[_dicPos & (DicSize - 1)] = b; _dicPos++; }
        private void DicRegister(byte[] buf, int n) { for (int i = 0; i < n; i++) DicPut(buf[i]); }

        // ------------------------------------------------------------------ LZ77
        private const int MaxCodeWidth = 11, NumMainCodes = 260 + 22 + 2, TypeCode = 260 + 22 + 1, NumLenCodes = 255;
        private HuffTree _mainTree, _lenTree;
        private int _symsToRead;
        private readonly int[] _distHist = new int[4];

        private void Lz77Reinit() { _symsToRead = 0; Array.Clear(_distHist, 0, 4); }

        private int ReadMain(BitStream bs)
        {
            if (_symsToRead == 0)
            {
                _mainTree = ReadTree(bs, MaxCodeWidth, NumMainCodes);
                _lenTree = ReadTree(bs, MaxCodeWidth, NumLenCodes);
                _symsToRead = (int)bs.Read(15);
            }
            _symsToRead--;
            return _mainTree.ReadSymbol(bs);
        }

        /// <summary>Decodes LZ77 symbols into out until at least softWant bytes (never more than hardMax) or a mode change.</summary>
        private AceMode Lz77Read(BitStream bs, List<byte> outBuf, long hardMax, int softWant)
        {
            int have = 0;
            while (have < softWant)
            {
                int sym = ReadMain(bs);
                if (sym <= 255) { DicPut((byte)sym); outBuf.Add((byte)sym); have++; }
                else if (sym < TypeCode)
                {
                    int len, dist;
                    if (sym <= 259)
                    {
                        len = _lenTree.ReadSymbol(bs);
                        int off = sym & 3;
                        // retrieve: pop element at SIZE - off - 1, append
                        int idx = 4 - off - 1;
                        dist = _distHist[idx];
                        for (int i = idx; i < 3; i++) _distHist[i] = _distHist[i + 1];
                        _distHist[3] = dist;
                        len += off > 1 ? 3 : 2;
                    }
                    else
                    {
                        dist = (int)bs.KnownWidthUInt(sym - 260);
                        len = _lenTree.ReadSymbol(bs);
                        for (int i = 0; i < 3; i++) _distHist[i] = _distHist[i + 1];
                        _distHist[3] = dist;
                        len += dist <= 255 ? 2 : dist <= 8191 ? 3 : 4;
                    }
                    dist++;
                    if (have + len > hardMax) throw Corrupt("copy exceeds size");
                    if (dist > _dicPos || dist > DicSize) throw Corrupt("copy out of bounds");
                    for (int i = 0; i < len; i++)
                    {
                        byte b = _dic[(_dicPos - dist) & (DicSize - 1)];
                        DicPut(b);
                        outBuf.Add(b);
                    }
                    have += len;
                }
                else if (sym == TypeCode) return AceMode.Read(bs);
                else throw Corrupt("symbol");
            }
            return null;
        }

        // ------------------------------------------------------------------ sound
        private static readonly int[] SoundQuant = BuildSoundQuant();
        private static int[] BuildSoundQuant()
        {
            var q = new int[256];
            for (int i = 1; i <= 128; i++) { int bl = BitLength(i); q[256 - i] = bl; q[i] = bl; }
            return q;
        }
        private static int BitLength(int v) { int n = 0; v = Math.Abs(v); while (v > 0) { n++; v >>= 1; } return n; }
        private static int SChar(int v) { v &= 0xFF; return v >= 128 ? v - 256 : v; }
        private static int UChar(int v) => v & 0xFF;

        private sealed class SoundSyms
        {
            public HuffTree[] Trees; public int ToRead;
            public int Read(BitStream bs, int model)
            {
                if (ToRead == 0)
                {
                    for (int i = 0; i < Trees.Length; i++) Trees[i] = ReadTree(bs, 10, 256 + 32 + 1);
                    ToRead = (int)bs.Read(15);
                }
                ToRead--;
                return Trees[model].ReadSymbol(bs);
            }
        }

        private sealed class Channel
        {
            private readonly SoundSyms _sr;
            private readonly int _base;
            private readonly int[] _predDifCnt = new int[2], _lastPredDifCnt = new int[2], _rarDifCnt = new int[4], _rarCoeff = new int[4], _rarDif = new int[9];
            private int _byteCount, _lastSample, _lastDelta, _adaptCnt, _adaptUse, _state, _code;
            public Channel(SoundSyms sr, int idx) { _sr = sr; _base = 3 * idx; }

            /// <summary>Returns a sample value, or null with mode set when a mode switch was read.</summary>
            public int? Get(BitStream bs, out AceMode mode)
            {
                mode = null;
                int value = 0;
                if (_state != 2)
                {
                    int model = _state << 1;
                    if (model == 0) model += _adaptUse;
                    _code = _sr.Read(bs, model + _base);
                    if (_code == 256 + 32) { mode = AceMode.Read(bs); return null; }
                }
                if (_state == 0)
                {
                    if (_code >= 32)
                    {
                        value = _code - 32;
                        _adaptCnt = (_adaptCnt * 7 >> 3) + value;
                        _adaptUse = _adaptCnt > 40 ? 1 : 0;
                    }
                    else _state = 2;
                }
                else if (_state == 1) { value = _code; _state = 0; }
                if (_state == 2)
                {
                    if (_code == 0) _state = 1; else _code--;
                    value = 0;
                }
                return (value & 1) != 0 ? 255 - (value >> 1) : value >> 1;
            }

            private int Predicted() => UChar((8 * _lastSample + _rarCoeff[0] * _rarDifCnt[0] + _rarCoeff[1] * _rarDifCnt[1] + _rarCoeff[2] * _rarDifCnt[2] + _rarCoeff[3] * _rarDifCnt[3]) >> 3);
            public int Predict() => _predDifCnt[0] > _predDifCnt[1] ? _lastSample : Predicted();

            public void Adjust(int sample)
            {
                _byteCount++;
                int predDif = SChar(Predicted() - sample) << 3;
                _rarDif[0] += Math.Abs(predDif - _rarDifCnt[0]);
                _rarDif[1] += Math.Abs(predDif + _rarDifCnt[0]);
                _rarDif[2] += Math.Abs(predDif - _rarDifCnt[1]);
                _rarDif[3] += Math.Abs(predDif + _rarDifCnt[1]);
                _rarDif[4] += Math.Abs(predDif - _rarDifCnt[2]);
                _rarDif[5] += Math.Abs(predDif + _rarDifCnt[2]);
                _rarDif[6] += Math.Abs(predDif - _rarDifCnt[3]);
                _rarDif[7] += Math.Abs(predDif + _rarDifCnt[3]);
                _rarDif[8] += Math.Abs(predDif);
                _lastDelta = SChar(sample - _lastSample);
                _predDifCnt[0] += SoundQuant[(predDif >> 3) & 0xFF];
                int d = _lastSample - sample;
                _predDifCnt[1] += SoundQuant[d < 0 ? 256 + d : d];
                _lastSample = sample;
                if ((_byteCount & 0x1F) == 0)
                {
                    int minDif = 0xFFFF, minPos = 0;
                    for (int i = 8; i >= 0; i--)
                    {
                        if (_rarDif[i] <= minDif) { minDif = _rarDif[i]; minPos = i; }
                        _rarDif[i] = 0;
                    }
                    if (minPos != 8)
                    {
                        int i = minPos >> 1;
                        if ((minPos & 1) == 0) { if (_rarCoeff[i] >= -16) _rarCoeff[i]--; }
                        else { if (_rarCoeff[i] <= 16) _rarCoeff[i]++; }
                    }
                    if ((_byteCount & 0xFF) == 0)
                        for (int i = 0; i < 2; i++) { _predDifCnt[i] -= _lastPredDifCnt[i]; _lastPredDifCnt[i] = _predDifCnt[i]; }
                }
                _rarDifCnt[3] = _rarDifCnt[2];
                _rarDifCnt[2] = _rarDifCnt[1];
                _rarDifCnt[1] = _lastDelta - _rarDifCnt[0];
                _rarDifCnt[0] = _lastDelta;
            }
        }

        private static readonly int[] NumChannels = { 1, 2, 3, 3 };
        private static readonly int[,] UseChannels = { { 0, 0, 0, 0 }, { 0, 1, 0, 1 }, { 0, 1, 0, 2 }, { 1, 0, 2, 0 } };
        private int _soundMode;
        private Channel[] _channels;

        private void SoundReinit(int mode)
        {
            _soundMode = mode - ModeSound8;
            int nc = NumChannels[_soundMode];
            var sr = new SoundSyms { Trees = new HuffTree[nc * 3] };
            _channels = new Channel[nc];
            for (int i = 0; i < nc; i++) _channels[i] = new Channel(sr, i);
        }

        private AceMode SoundRead(BitStream bs, List<byte> outBuf, long want)
        {
            long n = want & 0xFFFFFFFC;
            if (n > (1 << 20)) n = 1 << 20;
            for (long i = 0; i < n; i++)
            {
                var ch = _channels[UseChannels[_soundMode, i % 4]];
                int? v = ch.Get(bs, out AceMode m);
                if (m != null) return m;
                int sample = UChar(v.Value + ch.Predict());
                outBuf.Add((byte)sample);
                ch.Adjust(SChar(sample));
            }
            return null;
        }

        // ------------------------------------------------------------------ picture
        private static readonly int[] DifBitWidth = BuildDifBitWidth();
        private static int[] BuildDifBitWidth()
        {
            var a = new int[256];
            for (int i = 0; i < 128; i++) a[i] = BitLength(2 * i);
            for (int i = -128; i < 0; i++) a[256 + i] = BitLength(-2 * i - 1);
            return a;
        }

        private static readonly int[] Quant = new int[511], Quant9 = new int[511], Quant81 = new int[511];
        static AceDecoder()
        {
            for (int d = -255; d <= 255; d++)
            {
                int q = d <= -21 ? -4 : d <= -7 ? -3 : d <= -3 ? -2 : d <= -1 ? -1 : d == 0 ? 0 : d <= 2 ? 1 : d <= 6 ? 2 : d <= 20 ? 3 : 4;
                Quant[d + 255] = q; Quant9[d + 255] = 9 * q; Quant81[d + 255] = 81 * q;
            }
        }

        private sealed class ErrContext { public int Used, Predictor, Average = 4; public int[] Err = new int[4]; }

        private sealed class PixelDecoder
        {
            public int Kind, A, B, C, X, D;
            public PixelDecoder(int kind) { Kind = kind; if (kind != 0) A = B = C = X = 128; }
            public void Shift() { C = A; A = D; B = X; }
            public void UpdateD(int thisD, int refD)
            {
                if (Kind == 0) D = thisD;
                else if (Kind == 1) D = UChar(128 + thisD - refD);
                else D = UChar(128 + thisD - (refD * 11 >> 4));
            }
            public int Produce(int refX)
            {
                if (Kind == 0) return X;
                if (Kind == 1) return UChar(X + refX - 128);
                return UChar(X + (refX * 11 >> 4) - 128);
            }
            public int Context() => Math.Abs(Quant81[255 + D - A] + Quant9[255 + A - C] + Quant[255 + C - B]);
            private int Pred(int p)
            {
                switch (p)
                {
                    case 0: return A;
                    case 1: return B;
                    case 2: return (A + B) >> 1;
                    default: return UChar(A + B - C);
                }
            }
            public void UpdateX(BitStream bs, ErrContext ctx)
            {
                ctx.Used++;
                int r = ctx.Average / ctx.Used;
                int eps = bs.GolombRice(BitLength(r), true);
                X = UChar(Pred(ctx.Predictor) + eps);
                ctx.Average += Math.Abs(eps);
                if (ctx.Used == 128) { ctx.Used >>= 1; ctx.Average >>= 1; }
                int best = 0;
                for (int i = 0; i < 4; i++)
                {
                    ctx.Err[i] += DifBitWidth[(X - Pred(i)) & 0xFF];
                    if (i == 0 || ctx.Err[i] < ctx.Err[best]) best = i;
                }
                ctx.Predictor = best;
                if (ctx.Err[0] > 0x7F || ctx.Err[1] > 0x7F || ctx.Err[2] > 0x7F || ctx.Err[3] > 0x7F)
                    for (int i = 0; i < 4; i++) ctx.Err[i] >>= 1;
            }
        }

        private int _picWidth, _picPlanes;
        private ErrContext[] _err0, _err1;
        private int[] _prevRow;
        private readonly List<byte> _picLeftover = new List<byte>();

        private static ErrContext[] NewModel() { var a = new ErrContext[365]; for (int i = 0; i < a.Length; i++) a[i] = new ErrContext(); return a; }

        private void PicReinit(BitStream bs)
        {
            _picWidth = bs.GolombRice(12, false);
            _picPlanes = bs.GolombRice(2, false);
            if (_picWidth <= 0 || _picWidth > (1 << 24) || _picPlanes <= 0) throw Corrupt("picture");
            _err0 = NewModel(); _err1 = NewModel();
            _prevRow = new int[_picWidth + _picPlanes];
            _picLeftover.Clear();
        }

        private int[] PicRow(BitStream bs)
        {
            int len = _picWidth + _picPlanes;
            var row = new int[len];
            for (int plane = 0; plane < _picPlanes; plane++)
            {
                ErrContext[] model;
                PixelDecoder dec;
                if (plane == 0) { model = _err0; dec = new PixelDecoder(0); }
                else
                {
                    model = _err1;
                    int k = (int)bs.Read(2);
                    if (k > 2) throw Corrupt("pixel decoder");
                    dec = new PixelDecoder(k);
                }
                dec.UpdateD(_prevRow[plane], _prevRow[plane == 0 ? len - 1 : plane - 1]);
                for (int col = plane; col < _picWidth; col += _picPlanes)
                {
                    dec.Shift();
                    dec.UpdateD(_prevRow[col + _picPlanes], _prevRow[col + _picPlanes - 1]);
                    var ctx = model[dec.Context()];
                    dec.UpdateX(bs, ctx);
                    row[col] = dec.Produce(row[col == 0 ? len - 1 : col - 1]);
                }
            }
            _prevRow = row;
            return row;
        }

        private AceMode PicRead(BitStream bs, List<byte> outBuf, long want)
        {
            int start = outBuf.Count;
            if (_picLeftover.Count > 0) { outBuf.AddRange(_picLeftover); _picLeftover.Clear(); }
            while (outBuf.Count - start < want)
            {
                if (bs.Read(1) == 0) return AceMode.Read(bs);
                var row = PicRow(bs);
                long need = want - (outBuf.Count - start);
                int n = (int)Math.Min(need, _picWidth);
                for (int i = 0; i < n; i++) outBuf.Add((byte)row[i]);
                for (int i = n; i < _picWidth; i++) _picLeftover.Add((byte)row[i]);
                if (outBuf.Count - start > (1 << 20)) break;
            }
            return null;
        }

        // ------------------------------------------------------------------ public entry points
        public void DecompressStored(Stream input, Stream output, long size)
        {
            var buf = new byte[1 << 16];
            long done = 0;
            while (done < size)
            {
                int n = input.Read(buf, 0, (int)Math.Min(buf.Length, size - done));
                if (n <= 0) throw Corrupt("truncated stored file");
                DicRegister(buf, n);
                output.Write(buf, 0, n);
                done += n;
            }
        }

        public void DecompressLz77(Stream input, Stream output, long size)
        {
            Lz77Reinit();
            var bs = new BitStream(input);
            var chunk = new List<byte>(1 << 16);
            long done = 0;
            while (done < size)
            {
                chunk.Clear();
                var m = Lz77Read(bs, chunk, size - done, (int)Math.Min(1 << 16, size - done));
                if (m != null) throw Corrupt("mode switch in ACE 1.0 stream");
                if (chunk.Count == 0) throw Corrupt("no progress");
                output.Write(chunk.ToArray(), 0, chunk.Count);
                done += chunk.Count;
            }
        }

        public void DecompressBlocked(Stream input, Stream output, long size)
        {
            var bs = new BitStream(input);
            Lz77Reinit();
            var exeLeftover = new List<byte>();
            int lastDelta = 0;
            AceMode next = null, mode = new AceMode { Mode = ModeLz77 };
            long produced = 0;
            int stall = 0;
            while (produced < size)
            {
                if (next != null)
                {
                    if (mode.Mode != next.Mode)
                    {
                        if (next.Mode >= ModeSound8 && next.Mode <= ModeSound32B) SoundReinit(next.Mode);
                        else if (next.Mode == ModePic) PicReinit(bs);
                    }
                    mode = next;
                    next = null;
                }
                var outChunk = new List<byte>();
                if (mode.Mode == ModeDelta)
                {
                    if (mode.DeltaDist == 0 || mode.DeltaLen == 0) throw Corrupt("delta");
                    var delta = new List<byte>();
                    while (delta.Count < mode.DeltaLen)
                    {
                        var nm = Lz77Read(bs, delta, mode.DeltaLen - delta.Count, mode.DeltaLen - delta.Count);
                        if (nm != null)
                        {
                            if (next != null) throw Corrupt("delta clobbers mode");
                            next = nm;
                            if (delta.Count == 0) break;
                        }
                    }
                    if (delta.Count == 0 && next != null) continue;
                    var d = delta.ToArray();
                    for (int i = 0; i < d.Length; i++) { d[i] = (byte)(d[i] + lastDelta); lastDelta = d[i]; }
                    int planeSize = mode.DeltaLen / mode.DeltaDist;
                    for (int pos = 0; pos < planeSize; pos++)
                        for (int plane = 0; plane < mode.DeltaLen; plane += planeSize)
                            if (plane + pos < d.Length) outChunk.Add(d[plane + pos]);
                }
                else if (mode.Mode == ModeLz77 || mode.Mode == ModeExe)
                {
                    if (exeLeftover.Count > 0) { outChunk.AddRange(exeLeftover); exeLeftover.Clear(); }
                    long remaining = size - produced - outChunk.Count;
                    next = Lz77Read(bs, outChunk, remaining, (int)Math.Min(1 << 20, remaining));
                    if (mode.Mode == ModeExe)
                    {
                        int i = 0, brk = -1;
                        for (; i < outChunk.Count; i++)
                        {
                            if (i + 4 >= outChunk.Count) { brk = i; break; }
                            byte b = outChunk[i];
                            if (b == 0xE8)
                            {
                                long pos = produced + i;
                                if (mode.ExeMode == 0)
                                {
                                    int rel16 = (outChunk[i + 1] + (outChunk[i + 2] << 8) - (int)pos) & 0xFFFF;
                                    outChunk[i + 1] = (byte)rel16; outChunk[i + 2] = (byte)(rel16 >> 8);
                                    i += 2;
                                }
                                else
                                {
                                    uint rel32 = (uint)(outChunk[i + 1] | outChunk[i + 2] << 8 | outChunk[i + 3] << 16 | outChunk[i + 4] << 24);
                                    rel32 = unchecked(rel32 - (uint)pos);
                                    outChunk[i + 1] = (byte)rel32; outChunk[i + 2] = (byte)(rel32 >> 8); outChunk[i + 3] = (byte)(rel32 >> 16); outChunk[i + 4] = (byte)(rel32 >> 24);
                                    i += 4;
                                }
                            }
                            else if (b == 0xE9)
                            {
                                long pos = produced + i;
                                int rel16 = (outChunk[i + 1] + (outChunk[i + 2] << 8) - (int)pos) & 0xFFFF;
                                outChunk[i + 1] = (byte)rel16; outChunk[i + 2] = (byte)(rel16 >> 8);
                                i += 2;
                            }
                        }
                        if (brk >= 0 && produced + outChunk.Count < size)
                        {
                            for (int j = brk; j < outChunk.Count; j++)
                            {
                                if (outChunk[j] == 0xE8 || outChunk[j] == 0xE9)
                                {
                                    exeLeftover.AddRange(outChunk.GetRange(j, outChunk.Count - j));
                                    outChunk.RemoveRange(j, outChunk.Count - j);
                                    break;
                                }
                            }
                        }
                    }
                }
                else if (mode.Mode >= ModeSound8 && mode.Mode <= ModeSound32B)
                {
                    next = SoundRead(bs, outChunk, size - produced);
                    var arr = outChunk.ToArray(); DicRegister(arr, arr.Length);
                }
                else if (mode.Mode == ModePic)
                {
                    next = PicRead(bs, outChunk, size - produced);
                    var arr = outChunk.ToArray(); DicRegister(arr, arr.Length);
                }
                else throw Corrupt("unknown mode " + mode.Mode);

                if (outChunk.Count == 0 && next == null) { if (++stall > 4) throw Corrupt("no progress"); }
                else stall = 0;
                if (outChunk.Count > 0) output.Write(outChunk.ToArray(), 0, outChunk.Count);
                produced += outChunk.Count;
            }
        }

        /// <summary>Decompresses an ACE comment block (archive or file comment).</summary>
        public static string Comment(byte[] buf, Encoding enc)
        {
            try
            {
                var bs = new BitStream(new MemoryStream(buf));
                int want = (int)bs.Read(15);
                var tree = ReadTree(bs, MaxCodeWidth, NumMainCodes);
                var c = new List<byte>(want);
                var htab = new int[511];
                while (c.Count < want)
                {
                    int src = 0;
                    if (c.Count > 1) { int h = c[c.Count - 1] + c[c.Count - 2]; src = htab[h]; htab[h] = c.Count; }
                    int code = tree.ReadSymbol(bs);
                    if (code < 256) c.Add((byte)code);
                    else for (int i = 0; i < code - 256 + 2; i++) c.Add(c[src + i]);
                }
                return enc.GetString(c.ToArray());
            }
            catch { return null; }
        }
    }

    // ================================================================== ACE archive
    internal sealed class AceArchive : ManagedArchive
    {
        private sealed class Item
        {
            public int Index, CompType;
            public long Packed, Size, DataPos;
            public uint Crc;
            public bool Encrypted, Dir;
        }

        private readonly List<Item> _items = new List<Item>();
        private FileStream _f;

        public static AceArchive Open(string path)
        {
            var a = new AceArchive { FormatName = "ACE" };
            a._f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
            try { a.Parse(); }
            catch { a.Dispose(); throw; }
            a.PhysicalSize = a._f.Length;
            return a;
        }

        private static ushort Crc16(byte[] b)
        {
            uint c = 0xFFFFFFFF;
            foreach (var x in b) { c ^= x; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1; }
            return (ushort)c;
        }

        private void Parse()
        {
            var enc = Cp949Or437();
            // find main header (allow SFX stubs)
            long start = -1;
            var head = new byte[Math.Min(_f.Length, 1 << 19)];
            _f.Position = 0;
            AzoDecoder.ReadExact(_f, head, head.Length);
            for (int i = 7; i + 7 <= head.Length; i++)
            {
                if (head[i] == '*' && head[i + 1] == '*' && head[i + 2] == 'A' && head[i + 3] == 'C' && head[i + 4] == 'E' && head[i + 5] == '*' && head[i + 6] == '*')
                {
                    start = i - 7;
                    _f.Position = start;
                    try { ReadHeader(enc, true); break; }
                    catch (InvalidDataException) { start = -1; }
                }
            }
            if (start < 0) throw new ArchiveException("ACE 헤더를 찾을 수 없습니다.");
            while (_f.Position < _f.Length) ReadHeader(enc, false);
        }

        private static Encoding Cp949Or437() => Cp949;

        private void ReadHeader(Encoding enc, bool expectMain)
        {
            var h = new byte[4];
            if (_f.Read(h, 0, 4) < 4) throw new InvalidDataException("ACE 헤더가 잘렸습니다");
            ushort hcrc = BitConverter.ToUInt16(h, 0), hsize = BitConverter.ToUInt16(h, 2);
            var buf = new byte[hsize];
            AzoDecoder.ReadExact(_f, buf, hsize);
            if (Crc16(buf) != hcrc) throw new InvalidDataException("ACE 헤더 CRC 오류");
            int type = buf[0];
            int flags = buf[1] | buf[2] << 8;
            if (expectMain && type != 0) throw new InvalidDataException("ACE 메인 헤더가 아님");
            if (type == 0)
            {
                if ((flags & (1 << 11)) != 0) throw new ArchiveException("분할된 ACE 파일은 아직 지원하지 않습니다.");
                if ((flags & (1 << 15)) != 0) Solid = true;
                return;
            }
            if (type == 1 || type == 3)
            {
                int i = 3;
                var it = new Item { Index = _items.Count };
                if ((flags & 4) != 0) { it.Packed = (long)BitConverter.ToUInt64(buf, i); it.Size = (long)BitConverter.ToUInt64(buf, i + 8); i += 16; }
                else { it.Packed = BitConverter.ToUInt32(buf, i); it.Size = BitConverter.ToUInt32(buf, i + 4); i += 8; }
                uint dt = BitConverter.ToUInt32(buf, i), attr = BitConverter.ToUInt32(buf, i + 4);
                it.Crc = BitConverter.ToUInt32(buf, i + 8);
                it.CompType = buf[i + 12];
                int fnsz = BitConverter.ToUInt16(buf, i + 18);
                i += 20;
                string name = Normalize(DecodeName(Slice(buf, i, fnsz), enc));
                it.Encrypted = (flags & (1 << 14)) != 0;
                it.Dir = (attr & 0x10) != 0;
                it.DataPos = _f.Position;
                _f.Position += it.Packed;
                if ((flags & ((1 << 12) | (1 << 13))) != 0) throw new ArchiveException("분할된 ACE 파일은 아직 지원하지 않습니다.");
                _items.Add(it);
                var e = new ArchiveEntry
                {
                    Index = it.Index, Path = name.Length > 0 ? name : "file" + it.Index.ToString("0000"), IsDir = it.Dir,
                    Size = (ulong)it.Size, PackedSize = (ulong)it.Packed, Attributes = attr & 0x37, Crc = it.Crc ^ 0xFFFFFFFF,
                    Encrypted = it.Encrypted, Method = it.CompType == 0 ? "Stored" : it.CompType == 1 ? "LZ77" : "Blocked",
                    MTime = AlzArchiveTime((uint)dt)
                };
                int slash = e.Path.LastIndexOf('\\');
                e.Name = slash >= 0 ? e.Path.Substring(slash + 1) : e.Path;
                Entries.Add(e);
                return;
            }
            // recovery / unknown headers: skip addsize
            if ((flags & 1) != 0)
            {
                long add = (flags & 4) != 0 ? (long)BitConverter.ToUInt64(buf, 3) : BitConverter.ToUInt32(buf, 3);
                _f.Position += add;
            }
        }

        private static DateTime? AlzArchiveTime(uint dos) => AlzArchiveDos(dos);
        private static DateTime? AlzArchiveDos(uint t)
        {
            int sec = (int)(t & 0x1f) * 2, min = (int)(t >> 5) & 0x3f, hour = (int)(t >> 11) & 0x1f;
            int day = (int)(t >> 16) & 0x1f, month = (int)(t >> 21) & 0x0f, year = (int)((t >> 25) & 0x7f) + 1980;
            try { return new DateTime(year, month, day, hour, min, Math.Min(sec, 59), DateTimeKind.Local).ToUniversalTime(); } catch { return null; }
        }

        private static byte[] Slice(byte[] b, int o, int n) { var r = new byte[Math.Max(0, Math.Min(n, b.Length - o))]; Buffer.BlockCopy(b, o, r, 0, r.Length); return r; }

        public override void Extract(ISet<int> wanted, ManagedExtractContext ctx)
        {
            var dec = new AceDecoder();
            bool failedSolid = false;
            foreach (var it in _items)
            {
                bool want = wanted.Contains(it.Index);
                if (!want && !Solid) continue;
                Stream output = want ? ctx.Open(it.Index) : null;
                if (it.Dir || it.Size == 0) { if (want) ctx.Done(it.Index, null, false); continue; }
                if (it.Encrypted) { if (want) ctx.Done(it.Index, "암호화된 ACE 파일은 아직 지원하지 않습니다", false); failedSolid = true; continue; }
                if (failedSolid) { if (want) ctx.Done(it.Index, "앞선 파일 오류로 풀 수 없음 (솔리드 압축)", false); continue; }
                try
                {
                    var input = new BufferedStream(new SegmentStream(_f, it.DataPos, it.Packed, null), 1 << 16);
                    var crc = new CrcStream(output) { Limit = it.Size, Progress = want ? ctx.Progress : null };
                    switch (it.CompType)
                    {
                        case 0: dec.DecompressStored(input, crc, it.Size); break;
                        case 1: dec.DecompressLz77(input, crc, it.Size); break;
                        case 2: dec.DecompressBlocked(input, crc, it.Size); break;
                        default: throw new InvalidDataException("지원하지 않는 ACE 압축 방식 (" + it.CompType + ")");
                    }
                    // ACE stores the CRC-32 without the final inversion
                    bool ok = (crc.Crc ^ 0xFFFFFFFF) == it.Crc && crc.Count == it.Size;
                    if (!ok) failedSolid = Solid;
                    if (want) ctx.Done(it.Index, ok ? null : "CRC 오류 (파일 손상)", false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (ex is InvalidDataException || ex is EndOfStreamException || ex is IndexOutOfRangeException || ex is ArgumentOutOfRangeException)
                {
                    failedSolid = Solid;
                    if (want) ctx.Done(it.Index, ex.Message, false);
                }
            }
        }

        public override void Dispose() => _f?.Dispose();
    }
}
