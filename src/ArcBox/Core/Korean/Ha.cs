// HA archive support — a C# port of HA 0.999 by Harri Hirvola (acoder.c, swdict.c, asc.c, hsc.c, cpy.c).
// HA is distributed under the GNU General Public License version 2 or later; this file is therefore GPL too
// (see THIRD-PARTY-NOTICES.txt). Copyright (C) 1995 Harri Hirvola; C# port (C) ArcBox contributors.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ArcBox.Core.Korean
{
    // ================================================================== byte I/O as HA sees it
    internal sealed class HaIn
    {
        private readonly Stream _s;
        private long _left;
        private readonly byte[] _buf = new byte[8192];
        private int _len, _pos;
        public HaIn(Stream s, long limit) { _s = s; _left = limit; }

        /// <summary>Next byte, or -1 at the end of the packed data (like HA's getbyte()).</summary>
        public int Get()
        {
            if (_pos < _len) return _buf[_pos++];
            if (_left <= 0) return -1;
            int n = _s.Read(_buf, 0, (int)Math.Min(_buf.Length, _left));
            if (n <= 0) { _left = 0; return -1; }
            _left -= n; _len = n; _pos = 1;
            return _buf[0];
        }
    }

    internal sealed class HaOut
    {
        private static readonly uint[] Table = MakeTable();
        private readonly Stream _s;
        private readonly byte[] _buf = new byte[8192];
        private int _n;
        private uint _crc = 0xFFFFFFFF;
        public long Count;
        public long Limit = long.MaxValue;
        public Action<int> Progress;
        public HaOut(Stream s) { _s = s; }

        public void Put(int c)
        {
            _buf[_n++] = (byte)c;
            if (_n == _buf.Length) Flush();
        }

        public void Flush()
        {
            if (_n == 0) return;
            uint c = _crc;
            for (int i = 0; i < _n; i++) c = Table[(c ^ _buf[i]) & 0xFF] ^ (c >> 8);
            _crc = c;
            _s.Write(_buf, 0, _n);
            Count += _n;
            Progress?.Invoke(_n);
            _n = 0;
            if (Count > Limit) throw new InvalidDataException("HA 데이터가 손상되었습니다 (크기 초과).");
        }

        public uint Crc => ~_crc;

        internal static uint[] MakeTable()
        {
            var t = new uint[256];
            for (uint i = 0; i < 256; i++) { uint v = i; for (int j = 0; j < 8; j++) v = (v & 1) != 0 ? (v >> 1) ^ 0xEDB88320 : v >> 1; t[i] = v; }
            return t;
        }
    }

    // ================================================================== arithmetic coder (acoder.c)
    internal sealed class HaArith
    {
        private ushort _h, _l, _v;
        private int _s;
        private int _gpat, _ppat;
        private readonly HaIn _in;
        private readonly List<byte> _out;

        public HaArith(HaIn input) { _in = input; }
        public HaArith(List<byte> output) { _out = output; }

        public void InitDecode()
        {
            _h = 0xFFFF; _l = 0; _gpat = 0;
            _v = (ushort)(_in.Get() << 8);
            _v |= (ushort)(0xFF & _in.Get());
        }

        private int GetBit()
        {
            _gpat <<= 1;
            if ((_gpat & 0xFF) == 0)
            {
                _gpat = _in.Get();
                if ((_gpat & 0x100) != 0) _gpat = 0x100;
                else { _gpat <<= 1; _gpat |= 1; }
            }
            return (_gpat & 0x100) >> 8;
        }

        public void In(int low, int high, int tot)
        {
            uint r = (uint)(_h - _l) + 1;
            _h = (ushort)((ushort)(r * (uint)high / (uint)tot - 1) + _l);
            _l = (ushort)(_l + (ushort)(r * (uint)low / (uint)tot));
            while (((_h ^ _l) & 0x8000) == 0)
            {
                _l <<= 1; _h = (ushort)((_h << 1) | 1);
                _v = (ushort)((_v << 1) | GetBit());
            }
            while ((_l & 0x4000) != 0 && (_h & 0x4000) == 0)
            {
                _l = (ushort)((_l << 1) & 0x7FFF);
                _h = (ushort)((_h << 1) | 0x8001);
                _v = (ushort)(((_v << 1) ^ 0x8000) | GetBit());
            }
        }

        public int Threshold(int tot)
        {
            uint r = (uint)(_h - _l) + 1;
            return (ushort)((((uint)(_v - _l) + 1) * (uint)tot - 1) / r);
        }

        // ---- encoder (used by the self test to produce archives)
        public void InitEncode() { _h = 0xFFFF; _l = 0; _s = 0; _ppat = 1; }

        private void PutBit(int b)
        {
            _ppat <<= 1;
            if (b != 0) _ppat |= 1;
            if ((_ppat & 0x100) != 0) { _out.Add((byte)(_ppat & 0xFF)); _ppat = 1; }
        }

        public void Out(int low, int high, int tot)
        {
            uint r = (uint)(_h - _l) + 1;
            _h = (ushort)((ushort)(r * (uint)high / (uint)tot - 1) + _l);
            _l = (ushort)(_l + (ushort)(r * (uint)low / (uint)tot));
            if (((_h ^ _l) & 0x8000) == 0)
            {
                PutBit(_l & 0x8000);
                while (_s != 0) { --_s; PutBit(~_l & 0x8000); }
                _l <<= 1; _h = (ushort)((_h << 1) | 1);
                while (((_h ^ _l) & 0x8000) == 0)
                {
                    PutBit(_l & 0x8000);
                    _l <<= 1; _h = (ushort)((_h << 1) | 1);
                }
            }
            while ((_l & 0x4000) != 0 && (_h & 0x4000) == 0)
            {
                ++_s;
                _l = (ushort)((_l << 1) & 0x7FFF);
                _h = (ushort)((_h << 1) | 0x8001);
            }
        }

        public void EndEncode()
        {
            ++_s;
            PutBit(_l & 0x4000);
            while (_s-- != 0) PutBit(~_l & 0x4000);
            if (_ppat == 1) return;
            while ((_ppat & 0x100) == 0) _ppat <<= 1;
            _out.Add((byte)(_ppat & 0xFF));
        }
    }

    // ================================================================== ASC: LZ77 + arithmetic coding (asc.c)
    internal sealed class HaAsc
    {
        private const int POSCODES = 31200, SLCODES = 16, LLCODES = 48, LLLEN = 16, LLBITS = 4, LLMASK = LLLEN - 1;
        private const int LENCODES = SLCODES + LLCODES * LLLEN, LTCODES = SLCODES + LLCODES, CTCODES = 256, PTCODES = 16;
        private const int LTSTEP = 8, MAXLT = 750 * LTSTEP, CTSTEP = 1, MAXCT = 1000 * CTSTEP, PTSTEP = 24, MAXPT = 250 * PTSTEP;
        private const int TTSTEP = 40, MAXTT = 150 * TTSTEP, TTORD = 4, TTOMASK = TTORD - 1;
        private const int LCUTOFF = 3 * LTSTEP, CCUTOFF = 3 * CTSTEP, CPLEN = 8, LPLEN = 4, MINLENLIM = 4096, MINLEN = 3;

        private readonly ushort[] ltab = new ushort[2 * LTCODES], eltab = new ushort[2 * LTCODES], ptab = new ushort[2 * PTCODES];
        private readonly ushort[] ctab = new ushort[2 * CTCODES], ectab = new ushort[2 * CTCODES];
        private readonly ushort[,] ttab = new ushort[TTORD, 2];
        private ushort ccnt, pmax, npt, ces, les, ttcon;
        private readonly HaArith ac;

        private HaAsc(HaArith a) { ac = a; }

        private static void TabInit(ushort[] t, int tl, int ival)
        {
            for (int i = tl; i < 2 * tl; ++i) t[i] = (ushort)ival;
            for (int i = tl - 1, j = (tl << 1) - 2; i != 0; --i, j -= 2) t[i] = (ushort)(t[j] + t[j + 1]);
        }

        private static void TScale(ushort[] t, int tl)
        {
            for (int i = (tl << 1) - 1; i >= tl; --i) if (t[i] > 1) t[i] >>= 1;
            for (int i = tl - 1, j = (tl << 1) - 2; i != 0; --i, j -= 2) t[i] = (ushort)(t[j] + t[j + 1]);
        }

        private static void TUpd(ushort[] t, int tl, int maxt, int step, int p)
        {
            for (int i = p + tl; i != 0; i >>= 1) t[i] = (ushort)(t[i] + step);
            if (t[1] >= maxt) TScale(t, tl);
        }

        private static void TZero(ushort[] t, int tl, int p)
        {
            int i = p + tl, step = t[i];
            for (; i != 0; i >>= 1) t[i] = (ushort)(t[i] - step);
        }

        private void ModelInit()
        {
            ces = CTSTEP; les = LTSTEP; ccnt = 0; ttcon = 0; npt = pmax = 1;
            for (int i = 0; i < TTORD; ++i) ttab[i, 0] = ttab[i, 1] = TTSTEP;
            TabInit(ltab, LTCODES, 0); TabInit(eltab, LTCODES, 1);
            TabInit(ctab, CTCODES, 0); TabInit(ectab, CTCODES, 1);
            TabInit(ptab, PTCODES, 0);
            TUpd(ptab, PTCODES, MAXPT, PTSTEP, 0);
        }

        private void TTScale(int con)
        {
            ttab[con, 0] >>= 1; if (ttab[con, 0] == 0) ttab[con, 0] = 1;
            ttab[con, 1] >>= 1; if (ttab[con, 1] == 0) ttab[con, 1] = 1;
        }

        private static int Walk(ushort[] t, int codes, int tv, out int lt)
        {
            int l = 2; lt = 0;
            for (;;)
            {
                if (lt + t[l] <= tv) { lt += t[l]; ++l; }
                if (l >= codes) return l - codes;
                l <<= 1;
            }
        }

        public static void Unpack(HaIn input, HaOut output)
        {
            var a = new HaArith(input);
            var m = new HaAsc(a);
            m.ModelInit();
            a.InitDecode();
            m.Decode(output);
            output.Flush();
        }

        private void Decode(HaOut o)
        {
            var dict = new byte[POSCODES];
            int bbf = 0;
            for (;;)
            {
                int tv = ac.Threshold(ttab[ttcon, 0] + ttab[ttcon, 1] + 1);
                int i = (ushort)(ttab[ttcon, 0] + ttab[ttcon, 1]);
                int l, p, lt;
                if (ttab[ttcon, 0] > tv)
                {
                    ac.In(0, ttab[ttcon, 0], i + 1);
                    ttab[ttcon, 0] += TTSTEP;
                    if (i >= MAXTT) TTScale(ttcon);
                    ttcon = (ushort)((ttcon << 1) & TTOMASK);
                    tv = ac.Threshold(ctab[1] + ces);
                    if (tv >= ctab[1])
                    {
                        ac.In(ctab[1], ctab[1] + ces, ctab[1] + ces);
                        tv = ac.Threshold(ectab[1]);
                        l = Walk(ectab, CTCODES, tv, out lt);
                        ac.In(lt, lt + ectab[CTCODES + l], ectab[1]);
                        TZero(ectab, CTCODES, l);
                        if (ectab[1] != 0) ces += CTSTEP; else ces = 0;
                        for (i = l < CPLEN ? 0 : l - CPLEN; i < (l + CPLEN >= CTCODES - 1 ? CTCODES - 1 : l + CPLEN); ++i)
                            if (ectab[CTCODES + i] != 0) TUpd(ectab, CTCODES, MAXCT, 1, i);
                    }
                    else
                    {
                        l = Walk(ctab, CTCODES, tv, out lt);
                        ac.In(lt, lt + ctab[CTCODES + l], ctab[1] + ces);
                    }
                    TUpd(ctab, CTCODES, MAXCT, CTSTEP, l);
                    if (ctab[CTCODES + l] == CCUTOFF) ces = (ushort)(ces - (CTSTEP < ces ? CTSTEP : ces - 1));
                    dict[bbf] = (byte)l; o.Put(l); if (++bbf == POSCODES) bbf = 0;
                    if (ccnt < POSCODES) ++ccnt;
                }
                else if (i > tv)
                {
                    ac.In(ttab[ttcon, 0], i, i + 1);
                    ttab[ttcon, 1] += TTSTEP;
                    if (i >= MAXTT) TTScale(ttcon);
                    ttcon = (ushort)(((ttcon << 1) | 1) & TTOMASK);
                    while (ccnt > pmax) { TUpd(ptab, PTCODES, MAXPT, PTSTEP, npt++); pmax <<= 1; }
                    tv = ac.Threshold(ptab[1]);
                    p = Walk(ptab, PTCODES, tv, out lt);
                    ac.In(lt, lt + ptab[PTCODES + p], ptab[1]);
                    TUpd(ptab, PTCODES, MAXPT, PTSTEP, p);
                    if (p > 1)
                    {
                        for (i = 1; p != 0; i <<= 1, --p) { }
                        i >>= 1;
                        if (i == (pmax >> 1)) l = (ushort)(ccnt - (pmax >> 1)); else l = i;
                        p = ac.Threshold(l);
                        ac.In(p, p + 1, l);
                        p += i;
                    }
                    tv = ac.Threshold(ltab[1] + les);
                    if (tv >= ltab[1])
                    {
                        ac.In(ltab[1], ltab[1] + les, ltab[1] + les);
                        tv = ac.Threshold(eltab[1]);
                        l = Walk(eltab, LTCODES, tv, out lt);
                        ac.In(lt, lt + eltab[LTCODES + l], eltab[1]);
                        TZero(eltab, LTCODES, l);
                        if (eltab[1] != 0) les += LTSTEP; else les = 0;
                        for (i = l < LPLEN ? 0 : l - LPLEN; i < (l + LPLEN >= LTCODES - 1 ? LTCODES - 1 : l + LPLEN); ++i)
                            if (eltab[LTCODES + i] != 0) TUpd(eltab, LTCODES, MAXLT, 1, i);
                    }
                    else
                    {
                        l = Walk(ltab, LTCODES, tv, out lt);
                        ac.In(lt, lt + ltab[LTCODES + l], ltab[1] + les);
                    }
                    TUpd(ltab, LTCODES, MAXLT, LTSTEP, l);
                    if (ltab[LTCODES + l] == LCUTOFF) les = (ushort)(les - (LTSTEP < les ? LTSTEP : les - 1));
                    if (l == SLCODES - 1) l = LENCODES - 1;
                    else if (l >= SLCODES)
                    {
                        i = ac.Threshold(LLLEN);
                        ac.In(i, i + 1, LLLEN);
                        l = ((l - SLCODES) << LLBITS) + i + SLCODES - 1;
                    }
                    l += 3;
                    if (ccnt < POSCODES) { ccnt = (ushort)(ccnt + l); if (ccnt > POSCODES) ccnt = POSCODES; }
                    // swd_dpair
                    p = bbf > p ? bbf - 1 - p : POSCODES - 1 - p + bbf;
                    if (p < 0 || p >= POSCODES) throw new InvalidDataException("HA 데이터가 손상되었습니다.");
                    while (l-- != 0)
                    {
                        byte c = dict[p];
                        dict[bbf] = c; o.Put(c);
                        if (++bbf == POSCODES) bbf = 0;
                        if (++p == POSCODES) p = 0;
                    }
                }
                else
                {
                    ac.In(i, i + 1, i + 1);
                    return;
                }
                if (o.Count > o.Limit) throw new InvalidDataException("HA 데이터가 손상되었습니다 (크기 초과).");
            }
        }

        // ---------------------------------------------------------------- encoder (self test only)
        public static byte[] Pack(byte[] data)
        {
            var outp = new List<byte>();
            var a = new HaArith(outp);
            var m = new HaAsc(a);
            m.ModelInit();
            a.InitEncode();
            var swd = new HaSwd(data, LENCODES + MINLEN - 1, POSCODES);
            for (swd.FindBest(); swd.Char >= 0;)
            {
                if (swd.Mlf > MINLEN || (swd.Mlf == MINLEN && swd.Bpos < MINLENLIM))
                {
                    int omlf = swd.Mlf, obpos = swd.Bpos, oc = swd.Char;
                    swd.FindBest();
                    if (swd.Mlf > omlf) m.CodeChar(oc);
                    else { swd.Accept(); m.CodePair(omlf, obpos); swd.FindBest(); }
                }
                else { swd.Mlf = MINLEN - 1; m.CodeChar(swd.Char); swd.FindBest(); }
            }
            int t = m.ttab[m.ttcon, 0] + m.ttab[m.ttcon, 1];
            a.Out(t, t + 1, t + 1);
            a.EndEncode();
            return outp.ToArray();
        }

        private void CodePair(int l, int p)
        {
            int i, j, lt, k, cf, tot;
            i = ttab[ttcon, 0] + ttab[ttcon, 1];
            ac.Out(ttab[ttcon, 0], i, i + 1);
            ttab[ttcon, 1] += TTSTEP;
            if (i >= MAXTT) TTScale(ttcon);
            ttcon = (ushort)(((ttcon << 1) | 1) & TTOMASK);
            while (ccnt > pmax) { TUpd(ptab, PTCODES, MAXPT, PTSTEP, npt++); pmax <<= 1; }
            for (i = p, j = 0; i != 0; ++j, i >>= 1) { }
            cf = ptab[PTCODES + j]; tot = ptab[1];
            for (lt = 0, i = PTCODES + j; i != 0; i >>= 1) { if ((i & 1) != 0) lt += ptab[i - 1]; ptab[i] += PTSTEP; }
            if (ptab[1] >= MAXPT) TScale(ptab, PTCODES);
            ac.Out(lt, lt + cf, tot);
            if (p > 1)
            {
                for (i = 0x8000; (p & i) == 0; i >>= 1) { }
                j = p & ~i;
                if (i != (pmax >> 1)) ac.Out(j, j + 1, i); else ac.Out(j, j + 1, ccnt - (pmax >> 1));
            }
            i = l - MINLEN;
            if (i == LENCODES - 1) { i = SLCODES - 1; j = 0xFFFF; }
            else if (i < SLCODES - 1) j = 0xFFFF;
            else { j = (i - SLCODES + 1) & LLMASK; i = ((i - SLCODES + 1) >> LLBITS) + SLCODES; }
            if ((cf = ltab[LTCODES + i]) == 0)
            {
                ac.Out(ltab[1], ltab[1] + les, ltab[1] + les);
                for (lt = 0, k = LTCODES + i; k != 0; k >>= 1) { if ((k & 1) != 0) lt += eltab[k - 1]; ltab[k] += LTSTEP; }
                if (ltab[1] >= MAXLT) TScale(ltab, LTCODES);
                ac.Out(lt, lt + eltab[LTCODES + i], eltab[1]);
                TZero(eltab, LTCODES, i);
                if (eltab[1] != 0) les += LTSTEP; else les = 0;
                for (k = i <= LPLEN ? 0 : i - LPLEN; k < (i + LPLEN >= LTCODES - 1 ? LTCODES - 1 : i + LPLEN); ++k)
                    if (eltab[LTCODES + k] != 0) TUpd(eltab, LTCODES, MAXLT, 1, k);
            }
            else
            {
                tot = ltab[1] + les;
                for (lt = 0, k = LTCODES + i; k != 0; k >>= 1) { if ((k & 1) != 0) lt += ltab[k - 1]; ltab[k] += LTSTEP; }
                if (ltab[1] >= MAXLT) TScale(ltab, LTCODES);
                ac.Out(lt, lt + cf, tot);
            }
            if (ltab[LTCODES + i] == LCUTOFF) les = (ushort)(les - (LTSTEP < les ? LTSTEP : les - 1));
            if (j != 0xFFFF) ac.Out(j, j + 1, LLLEN);
            if (ccnt < POSCODES) { ccnt = (ushort)(ccnt + l); if (ccnt > POSCODES) ccnt = POSCODES; }
        }

        private void CodeChar(int c)
        {
            int i, lt, tot, cf;
            i = ttab[ttcon, 0] + ttab[ttcon, 1];
            ac.Out(0, ttab[ttcon, 0], i + 1);
            ttab[ttcon, 0] += TTSTEP;
            if (i >= MAXTT) TTScale(ttcon);
            ttcon = (ushort)((ttcon << 1) & TTOMASK);
            if ((cf = ctab[CTCODES + c]) == 0)
            {
                ac.Out(ctab[1], ctab[1] + ces, ctab[1] + ces);
                for (lt = 0, i = CTCODES + c; i != 0; i >>= 1) { if ((i & 1) != 0) lt += ectab[i - 1]; ctab[i] += CTSTEP; }
                if (ctab[1] >= MAXCT) TScale(ctab, CTCODES);
                ac.Out(lt, lt + ectab[CTCODES + c], ectab[1]);
                TZero(ectab, CTCODES, c);
                if (ectab[1] != 0) ces += CTSTEP; else ces = 0;
                for (i = c <= CPLEN ? 0 : c - CPLEN; i < (c + CPLEN >= CTCODES - 1 ? CTCODES - 1 : c + CPLEN); ++i)
                    if (ectab[CTCODES + i] != 0) TUpd(ectab, CTCODES, MAXCT, 1, i);
            }
            else
            {
                tot = ctab[1] + ces;
                for (lt = 0, i = CTCODES + c; i != 0; i >>= 1) { if ((i & 1) != 0) lt += ctab[i - 1]; ctab[i] += CTSTEP; }
                if (ctab[1] >= MAXCT) TScale(ctab, CTCODES);
                ac.Out(lt, lt + cf, tot);
            }
            if (ctab[CTCODES + c] == CCUTOFF) ces = (ushort)(ces - (CTSTEP < ces ? CTSTEP : ces - 1));
            if (ccnt < POSCODES) ++ccnt;
        }
    }

    /// <summary>Sliding window dictionary of the ASC packer (swdict.c), used by the self test encoder.</summary>
    internal sealed class HaSwd
    {
        private const int HSIZE = 16384, HSHIFT = 3, MAXCNT = 1024, MINLEN = 3;
        public int Bpos, Mlf, Char;
        private readonly int cblen, iblen, blen;
        private int binb, bbf, bbl, inptr;
        private readonly ushort[] ccnt = new ushort[HSIZE], cr = new ushort[HSIZE], ll, best;
        private readonly byte[] b;
        private readonly byte[] _src;
        private int _srcPos;

        private int GetByte() => _srcPos < _src.Length ? _src[_srcPos++] : -1;
        private int Hash(int p) => (b[p] ^ ((b[p + 1] ^ (b[p + 2] << HSHIFT)) << HSHIFT)) & (HSIZE - 1);

        public HaSwd(byte[] src, int maxl, int bufl)
        {
            _src = src;
            iblen = maxl; cblen = bufl; blen = cblen + iblen;
            ll = new ushort[blen]; best = new ushort[blen];
            b = new byte[blen + iblen - 1];
            binb = bbf = bbl = inptr = 0;
            while (bbl < iblen)
            {
                int i = GetByte();
                if (i < 0) break;
                b[inptr++] = (byte)i;
                bbl++;
            }
            Mlf = MINLEN - 1;
        }

        private void Feed()
        {
            int c = GetByte();
            if (c < 0) { --bbl; if (++inptr == blen) inptr = 0; return; }
            if (inptr < iblen - 1) { b[inptr + blen] = b[inptr] = (byte)c; ++inptr; }
            else { b[inptr] = (byte)c; if (++inptr == blen) inptr = 0; }
        }

        public void Accept()
        {
            int j = Mlf - 2;
            do
            {
                if (binb == cblen) --ccnt[Hash(inptr)]; else ++binb;
                int i = Hash(bbf);
                ll[bbf] = cr[i];
                cr[i] = (ushort)bbf;
                best[bbf] = 30000;
                ccnt[i]++;
                if (++bbf == blen) bbf = 0;
                Feed();
            } while (--j != 0);
            Mlf = MINLEN - 1;
        }

        public void FindBest()
        {
            int i = Hash(bbf), cnt, ptr, startLen;
            if ((cnt = ccnt[i]++) > MAXCNT) cnt = MAXCNT;
            ptr = ll[bbf] = cr[i];
            cr[i] = (ushort)bbf;
            Char = b[bbf];
            if ((startLen = Mlf) >= bbl)
            {
                if (bbl == 0) Char = -1;
                best[bbf] = 30000;
            }
            else
            {
                for (int rf = b[bbf + Mlf - 1]; cnt-- != 0; ptr = ll[ptr])
                {
                    if (b[ptr + Mlf - 1] == rf && b[ptr] == b[bbf] && b[ptr + 1] == b[bbf + 1])
                    {
                        for (i = 3; i < bbl; ++i) if (b[ptr + i] != b[bbf + i]) break;
                        if (i <= Mlf) continue;
                        Bpos = ptr;
                        if ((Mlf = i) == bbl || best[ptr] < i) break;
                        rf = b[bbf + Mlf - 1];
                    }
                }
                best[bbf] = (ushort)Mlf;
                if (Mlf > startLen) Bpos = Bpos < bbf ? bbf - Bpos - 1 : blen - 1 - Bpos + bbf;
            }
            if (binb == cblen) --ccnt[Hash(inptr)]; else ++binb;
            if (++bbf == blen) bbf = 0;
            Feed();
        }
    }

    // ================================================================== HSC: order-4 context model (hsc.c)
    internal sealed class HaHsc
    {
        private const int IECLIM = 32, NECLIM = 5, NECTLIM = 4, NECMAX = 10, MAXCLEN = 4, NUMCON = 10000, NUMCFB = 32760;
        private const int ESCTH = 3, MAXTVAL = 8000, RFMINI = 4, HTLEN = 16384, NIL = 0xFFFF, ESC = 256;

        private readonly byte[] curcon = new byte[4];
        private readonly ushort[] ht = new ushort[HTLEN], hp = new ushort[NUMCON], elp = new ushort[NUMCON], eln = new ushort[NUMCON];
        private readonly byte[,] con = new byte[NUMCON, 4];
        private readonly byte[] cl = new byte[NUMCON], cc = new byte[NUMCON], fe = new byte[NUMCON], rfm = new byte[NUMCON];
        private readonly ushort[] ft = new ushort[NUMCON];
        private ushort elf, ell;
        private readonly ushort[] fa = new ushort[NUMCFB], nb = new ushort[NUMCFB];
        private readonly byte[] fc = new byte[NUMCFB];
        private ushort fcfbl, nrel;
        private readonly byte[] cmask = new byte[256], cmstack = new byte[257];
        private int cmsp;
        private byte nec;
        private readonly byte[] iec = new byte[MAXCLEN + 1];
        private int usp;
        private readonly ushort[] cps = new ushort[MAXCLEN + 2], as_ = new ushort[MAXCLEN + 2];
        private int dropcnt;
        private byte maxclen;
        private static readonly ushort[] hrt = MakeHrt();
        private readonly ushort[] hs = new ushort[MAXCLEN + 1];
        private int cslen;
        private readonly HaArith ac;

        private HaHsc(HaArith a) { ac = a; InitModel(); }

        private static ushort[] MakeHrt()
        {
            var t = new ushort[HTLEN];
            int z = 10;
            for (int i = 0; i < HTLEN; ++i)
            {
                int h = z / (2147483647 / 16807), l = z % (2147483647 / 16807);
                int v = 16807 * l - (2147483647 % 16807) * h;
                z = v > 0 ? v : v + 2147483647;
                t[i] = (ushort)(z & (HTLEN - 1));
            }
            return t;
        }

        private void InitModel()
        {
            maxclen = MAXCLEN;
            iec[0] = IECLIM >> 1;
            for (int i = 1; i <= MAXCLEN; ++i) iec[i] = (IECLIM >> 1) - 1;
            dropcnt = NUMCON / 4;
            nec = 0; nrel = 0; hs[0] = 0;
            for (int i = 0; i < HTLEN; ++i) ht[i] = NIL;
            for (int i = 0; i < NUMCON; ++i) { eln[i] = (ushort)(i + 1); elp[i] = (ushort)(i - 1); cl[i] = 0xFF; nb[i] = NIL; }
            elf = 0; ell = NUMCON - 1;
            int k;
            for (k = NUMCON; k < NUMCFB - 1; ++k) nb[k] = (ushort)(k + 1);
            nb[k] = NIL;
            fcfbl = NUMCON;
            curcon[3] = curcon[2] = curcon[1] = curcon[0] = 0;
            cmsp = 0;
            Array.Clear(cmask, 0, 256);
        }

        private int HashCon(int nc, int l)
        {
            int h = 0;
            if (l > 0) h = hrt[con[nc, 0]];
            if (l > 1) h = hrt[(con[nc, 1] + h) & (HTLEN - 1)];
            if (l > 2) h = hrt[(con[nc, 2] + h) & (HTLEN - 1)];
            if (l > 3) h = hrt[(con[nc, 3] + h) & (HTLEN - 1)];
            return h;
        }

        private int HashCur(int l)
        {
            int h = 0;
            if (l > 0) h = hrt[curcon[0]];
            if (l > 1) h = hrt[(curcon[1] + h) & (HTLEN - 1)];
            if (l > 2) h = hrt[(curcon[2] + h) & (HTLEN - 1)];
            if (l > 3) h = hrt[(curcon[3] + h) & (HTLEN - 1)];
            return h;
        }

        private void MoveContext(int c) { curcon[3] = curcon[2]; curcon[2] = curcon[1]; curcon[1] = curcon[0]; curcon[0] = (byte)c; }

        private void ReleaseCfBlocks()
        {
            int i, j, d;
            do
            {
                do { if (++nrel == NUMCON) nrel = 0; } while (nb[nrel] == NIL);
                for (i = 0; i <= usp; ++i) if ((cps[i] & 0x7FFF) == nrel) break;
            } while (i <= usp);
            for (i = nb[nrel], d = fa[nrel]; i != NIL; i = nb[i]) if (fa[i] < d) d = fa[i];
            ++d;
            if (fa[nrel] < d)
            {
                for (i = nb[nrel]; fa[i] < d && nb[i] != NIL; i = nb[i]) { }
                fa[nrel] = fa[i];
                fc[nrel] = fc[i];
                j = nb[i];
                nb[i] = fcfbl;
                fcfbl = nb[nrel];
                if ((nb[nrel] = (ushort)j) == NIL)
                {
                    cc[nrel] = 0;
                    fe[nrel] = (byte)((ft[nrel] = fa[nrel]) < ESCTH ? 1 : 0);
                    return;
                }
            }
            fa[nrel] = (ushort)(fa[nrel] / d);
            fe[nrel] = (byte)((ft[nrel] = fa[nrel]) < ESCTH ? 1 : 0);
            cc[nrel] = 0;
            for (j = nrel, i = nb[j]; i != NIL;)
            {
                if (fa[i] < d)
                {
                    nb[j] = nb[i];
                    nb[i] = fcfbl;
                    fcfbl = (ushort)i;
                    i = nb[j];
                }
                else
                {
                    ++cc[nrel];
                    fa[i] = (ushort)(fa[i] / d);
                    ft[nrel] += fa[i];
                    if (fa[i] < ESCTH) fe[nrel]++;
                    j = i;
                    i = nb[i];
                }
            }
        }

        private int MakeContext(int conlen, int c)
        {
            int i, nc, fp;
            nc = ell;
            ell = elp[nc];
            elp[elf] = (ushort)nc;
            eln[nc] = elf;
            elf = (ushort)nc;
            if (cl[nc] != 0xFF)
            {
                if (cl[nc] == MAXCLEN && --dropcnt == 0) maxclen = MAXCLEN - 1;
                i = HashCon(nc, cl[nc]);
                if (ht[i] == nc) ht[i] = hp[nc];
                else
                {
                    for (i = ht[i]; hp[i] != nc; i = hp[i]) { }
                    hp[i] = hp[nc];
                }
                if (nb[nc] != NIL)
                {
                    for (fp = nb[nc]; nb[fp] != NIL; fp = nb[fp]) { }
                    nb[fp] = fcfbl;
                    fcfbl = nb[nc];
                }
            }
            nb[nc] = NIL;
            fe[nc] = 1; ft[nc] = fa[nc] = 1;
            fc[nc] = (byte)c;
            rfm[nc] = RFMINI;
            cc[nc] = 0;
            cl[nc] = (byte)conlen;
            con[nc, 0] = curcon[0]; con[nc, 1] = curcon[1]; con[nc, 2] = curcon[2]; con[nc, 3] = curcon[3];
            i = HashCur(conlen);
            hp[nc] = ht[i];
            ht[i] = (ushort)nc;
            return nc;
        }

        private void ElMoveFront(int cp)
        {
            if (cp == elf) return;
            if (cp == ell) ell = elp[cp];
            else
            {
                elp[eln[cp]] = elp[cp];
                eln[elp[cp]] = eln[cp];
            }
            elp[elf] = (ushort)cp;
            eln[cp] = elf;
            elf = (ushort)cp;
        }

        private void AddModel(int c)
        {
            while (usp != 0)
            {
                int i = as_[--usp];
                int cp = cps[usp];
                if ((cp & 0x8000) != 0)
                {
                    cp &= 0x7FFF;
                    if (fcfbl == NIL) ReleaseCfBlocks();
                    nb[i] = fcfbl;
                    i = nb[i];
                    fcfbl = nb[fcfbl];
                    nb[i] = NIL;
                    fa[i] = 1;
                    fc[i] = (byte)c;
                    ++cc[cp];
                    ++fe[cp];
                }
                else if (++fa[i] == ESCTH) --fe[cp];
                if ((fa[i] << 1) < ++ft[cp] / (cc[cp] + 1)) --rfm[cp];
                else if (rfm[cp] < RFMINI) ++rfm[cp];
                if (rfm[cp] == 0 || ft[cp] >= MAXTVAL)
                {
                    ++rfm[cp];
                    fe[cp] = 0; ft[cp] = 0;
                    for (i = cp; i != NIL; i = nb[i])
                    {
                        if (fa[i] > 1)
                        {
                            fa[i] >>= 1;
                            ft[cp] += fa[i];
                            if (fa[i] < ESCTH) ++fe[cp];
                        }
                        else { ++ft[cp]; ++fe[cp]; }
                    }
                }
            }
        }

        private int FindNext()
        {
            for (int i = cslen - 1; i >= 0; --i)
            {
                int k = hs[i];
                for (int cp = ht[k]; cp != NIL; cp = hp[cp])
                {
                    if (cl[cp] != i) continue;
                    bool ok = true;
                    for (int q = i - 1; q >= 0; --q) if (curcon[q] != con[cp, q]) { ok = false; break; }
                    if (ok) { cslen = i; return cp; }
                }
            }
            return NIL;
        }

        private int FindLongest()
        {
            hs[1] = hrt[curcon[0]];
            hs[2] = hrt[(curcon[1] + hs[1]) & (HTLEN - 1)];
            hs[3] = hrt[(curcon[2] + hs[2]) & (HTLEN - 1)];
            hs[4] = hrt[(curcon[3] + hs[3]) & (HTLEN - 1)];
            usp = 0;
            while (cmsp != 0) cmask[cmstack[--cmsp]] = 0;
            cslen = MAXCLEN + 1;
            return FindNext();
        }

        private int AdjEscapeProb(int esc, int cp)
        {
            if (ft[cp] == 1) return iec[cl[cp]] >= (IECLIM >> 1) ? 2 : 1;
            if (cc[cp] == 255) return 1;
            if (cc[cp] != 0 && ((cc[cp] + 1) << 1) >= ft[cp])
            {
                esc = (short)(esc * ((cc[cp] + 1) << 1) / ft[cp]);
                if (cc[cp] + 1 == ft[cp]) esc += (cc[cp] + 1) >> 1;
            }
            return esc != 0 ? esc : 1;
        }

        private int DecodeFirst(int cp)
        {
            int c, tv, i, sum, tot, esc, cf = 0;
            esc = AdjEscapeProb(fe[cp], cp);
            tot = ft[cp];
            if (nec >= NECLIM)
            {
                int sv = tot <= NECTLIM && nec == NECMAX ? 2 : 1;
                tot <<= sv;
                tv = ac.Threshold(tot + esc) >> sv;
                for (c = cp, sum = 0; ; c = nb[c])
                {
                    if (c == NIL) break;
                    if (sum + fa[c] <= tv) sum += fa[c];
                    else { cf = fa[c] << sv; break; }
                }
                sum <<= sv;
            }
            else
            {
                tv = ac.Threshold(tot + esc);
                for (c = cp, sum = 0; ; c = nb[c])
                {
                    if (c == NIL) break;
                    if (sum + fa[c] <= tv) sum += fa[c];
                    else { cf = fa[c]; break; }
                }
            }
            usp = 1;
            if (c != NIL)
            {
                ac.In(sum, sum + cf, tot + esc);
                if (ft[cp] == 1 && iec[cl[cp]] != 0) --iec[cl[cp]];
                as_[0] = (ushort)c;
                cps[0] = (ushort)cp;
                c = fc[c];
                if (nec < NECMAX) ++nec;
            }
            else
            {
                ac.In(tot, tot + esc, tot + esc);
                if (ft[cp] == 1 && iec[cl[cp]] < IECLIM) ++iec[cl[cp]];
                for (i = cp; i != NIL; sum = i, i = nb[i]) { cmstack[cmsp++] = fc[i]; cmask[fc[i]] = 1; }
                cps[0] = (ushort)(0x8000 | cp);
                as_[0] = (ushort)sum;
                c = ESC;
                nec = 0;
            }
            return c;
        }

        private int DecodeRest(int cp)
        {
            int c, tv, i, sum, tot = 0, esc = 0, cf = 0;
            for (i = cp; i != NIL; i = nb[i])
                if (cmask[fc[i]] == 0) { tot += fa[i]; if (fa[i] < ESCTH) ++esc; }
            esc = AdjEscapeProb(esc, cp);
            tv = ac.Threshold(tot + esc);
            for (c = cp, sum = 0; ; c = nb[c])
            {
                if (c == NIL) break;
                if (cmask[fc[c]] == 0)
                {
                    if (sum + fa[c] <= tv) sum += fa[c];
                    else { cf = fa[c]; break; }
                }
            }
            if (c != NIL)
            {
                ac.In(sum, sum + cf, tot + esc);
                if (ft[cp] == 1 && iec[cl[cp]] != 0) --iec[cl[cp]];
                as_[usp] = (ushort)c;
                cps[usp++] = (ushort)cp;
                c = fc[c];
                ++nec;
            }
            else
            {
                ac.In(tot, tot + esc, tot + esc);
                if (ft[cp] == 1 && iec[cl[cp]] < IECLIM) ++iec[cl[cp]];
                for (i = cp; i != NIL; sum = i, i = nb[i])
                    if (cmask[fc[i]] == 0) { cmstack[cmsp++] = fc[i]; cmask[fc[i]] = 1; }
                cps[usp] = (ushort)(0x8000 | cp);
                as_[usp++] = (ushort)sum;
                c = ESC;
            }
            return c;
        }

        private int DecodeNew()
        {
            int c, sum, tot = 257 - cmsp;
            int tv = ac.Threshold(tot);
            for (c = sum = 0; c < 256; ++c)
            {
                if (cmask[c] != 0) continue;
                if (sum + 1 <= tv) ++sum; else break;
            }
            ac.In(sum, sum + 1, tot);
            return c;
        }

        public static void Unpack(HaIn input, HaOut o)
        {
            var a = new HaArith(input);
            var m = new HaHsc(a);
            a.InitDecode();
            for (;;)
            {
                int c;
                int cp = m.FindLongest();
                int ncmin = cp == NIL ? 0 : m.cl[cp] + 1;
                int ncmax = m.maxclen + 1;
                for (;;)
                {
                    if (cp == NIL) { c = m.DecodeNew(); break; }
                    if ((c = m.cmsp != 0 ? m.DecodeRest(cp) : m.DecodeFirst(cp)) != ESC) { m.ElMoveFront(cp); break; }
                    cp = m.FindNext();
                }
                if (c == ESC) break;
                m.AddModel(c);
                while (ncmax > ncmin) m.MakeContext(--ncmax, c);
                o.Put(c);
                m.MoveContext(c);
                if (o.Count > o.Limit) throw new InvalidDataException("HA 데이터가 손상되었습니다 (크기 초과).");
            }
            o.Flush();
        }

        // ---------------------------------------------------------------- encoder (self test only)
        private bool CodeFirst(int cp, int c)
        {
            int i, sum = 0, cf = 0, tot, esc;
            for (i = cp; i != NIL; i = nb[i])
            {
                if (fc[i] == c) { cf = fa[i]; as_[0] = (ushort)i; break; }
                sum += fa[i];
            }
            tot = ft[cp];
            esc = AdjEscapeProb(fe[cp], cp);
            if (nec >= NECLIM)
            {
                if (tot <= NECTLIM && nec == NECMAX) { tot <<= 2; sum <<= 2; cf <<= 2; }
                else { tot <<= 1; sum <<= 1; cf <<= 1; }
            }
            usp = 1;
            if (cf == 0)
            {
                ac.Out(tot, tot + esc, tot + esc);
                for (i = cp; i != NIL; sum = i, i = nb[i]) { cmstack[cmsp++] = fc[i]; cmask[fc[i]] = 1; }
                as_[0] = (ushort)sum;
                nec = 0;
                if (ft[cp] == 1 && iec[cl[cp]] < IECLIM) ++iec[cl[cp]];
                cps[0] = (ushort)(0x8000 | cp);
                return false;
            }
            ac.Out(sum, sum + cf, tot + esc);
            if (nec < NECMAX) ++nec;
            if (ft[cp] == 1 && iec[cl[cp]] != 0) --iec[cl[cp]];
            cps[0] = (ushort)cp;
            return true;
        }

        private bool CodeRest(int cp, int c)
        {
            int i, sum = 0, cf = 0, tot = 0, esc = 0;
            for (i = cp; i != NIL; i = nb[i])
            {
                if (cmask[fc[i]] == 0)
                {
                    if (fa[i] < ESCTH) ++esc;
                    if (cf == 0 && fc[i] == c) { sum = tot; cf = fa[i]; as_[usp] = (ushort)i; }
                    tot += fa[i];
                }
            }
            esc = AdjEscapeProb(esc, cp);
            if (cf == 0)
            {
                ac.Out(tot, tot + esc, tot + esc);
                for (i = cp; i != NIL; sum = i, i = nb[i])
                    if (cmask[fc[i]] == 0) { cmstack[cmsp++] = fc[i]; cmask[fc[i]] = 1; }
                as_[usp] = (ushort)sum;
                if (ft[cp] == 1 && iec[cl[cp]] < IECLIM) ++iec[cl[cp]];
                cps[usp++] = (ushort)(0x8000 | cp);
                return false;
            }
            ac.Out(sum, sum + cf, tot + esc);
            ++nec;
            if (ft[cp] == 1 && iec[cl[cp]] != 0) --iec[cl[cp]];
            cps[usp++] = (ushort)cp;
            return true;
        }

        private void CodeNew(int c)
        {
            int sum = 0, tot = 257 - cmsp;
            for (int i = 0; i < c; ++i) sum += 1 - cmask[i];
            ac.Out(sum, sum + 1, tot);
        }

        private bool CodeByte(int cp, int c) => cmsp != 0 ? CodeRest(cp, c) : CodeFirst(cp, c);

        public static byte[] Pack(byte[] data)
        {
            var outp = new List<byte>();
            var a = new HaArith(outp);
            var m = new HaHsc(a);
            a.InitEncode();
            int cp;
            foreach (byte c in data)
            {
                cp = m.FindLongest();
                int ncmin = cp == NIL ? 0 : m.cl[cp] + 1;
                int ncmax = m.maxclen + 1;
                for (;;)
                {
                    if (cp == NIL) { m.CodeNew(c); break; }
                    if (m.CodeByte(cp, c)) { m.ElMoveFront(cp); break; }
                    cp = m.FindNext();
                }
                m.AddModel(c);
                while (ncmax > ncmin) m.MakeContext(--ncmax, c);
                m.MoveContext(c);
            }
            cp = m.FindLongest();
            while (cp != NIL) { m.CodeByte(cp, ESC); cp = m.FindNext(); }
            m.CodeNew(ESC);
            a.EndEncode();
            return outp.ToArray();
        }
    }

    // ================================================================== the archive container
    internal sealed class HaArchive : ManagedArchive
    {
        private const int M_CPY = 0, M_ASC = 1, M_HSC = 2, M_DIR = 14, M_SPECIAL = 15;

        private sealed class Item { public int Index, Type; public long Packed, Size, DataPos; public uint Crc; }
        private readonly List<Item> _items = new List<Item>();
        private FileStream _f;

        public static bool Looks(byte[] h, int len, string path)
        {
            if (len < 9 || h[0] != 'H' || h[1] != 'A') return false;
            int vt = h[4];
            bool hdrOk = (vt >> 4) == 2 && ((vt & 15) <= 2 || (vt & 15) == 14 || (vt & 15) == 15) || vt == 0xFF;
            if (!hdrOk) return false;
            return Path.GetExtension(path).Equals(".ha", StringComparison.OrdinalIgnoreCase) || (h[2] | h[3] << 8) > 0;
        }

        public static HaArchive Open(string path)
        {
            var a = new HaArchive { FormatName = "HA" };
            a._f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
            try { a.Parse(); } catch { a.Dispose(); throw; }
            a.PhysicalSize = a._f.Length;
            return a;
        }

        public override void Dispose() => _f?.Dispose();

        private byte[] ReadN(int n) { var b = new byte[n]; AzoDecoder.ReadExact(_f, b, n); return b; }

        private byte[] ReadZ()
        {
            var ms = new MemoryStream();
            int c;
            while ((c = _f.ReadByte()) > 0) { ms.WriteByte((byte)(c == 0xFF ? '/' : c)); if (ms.Length > 4096) throw new ArchiveException("HA 파일이 손상되었습니다."); }
            if (c < 0) throw new ArchiveException("HA 파일이 잘렸습니다.");
            return ms.ToArray();
        }

        private void Parse()
        {
            var id = ReadN(4);
            if (id[0] != 'H' || id[1] != 'A') throw new ArchiveException("HA 파일이 아닙니다.");
            int count = id[2] | id[3] << 8;
            long pos = 4, size = _f.Length;
            for (int n = 0; n < count && pos < size;)
            {
                _f.Position = pos;
                int vt = _f.ReadByte();
                var h = ReadN(16);
                uint clen = U32(h, 0), olen = U32(h, 4), crc = U32(h, 8), time = U32(h, 12);
                var pathB = ReadZ();
                var nameB = ReadZ();
                int mdilen = _f.ReadByte();
                if (mdilen < 0) throw new ArchiveException("HA 파일이 잘렸습니다.");
                var mdi = ReadN(mdilen);
                long data = _f.Position;
                pos = data + clen;
                if (vt == 0xFF) continue; // deleted entry
                n++;
                int ver = vt >> 4, type = vt & 15;
                if (ver > 2) throw new ArchiveException("더 새로운 HA 버전으로 만든 파일입니다.");
                if (ver < 2) throw new ArchiveException("너무 오래된 HA 버전으로 만든 파일입니다.");
                if (type == M_SPECIAL) continue; // devices / links: nothing to extract on Windows
                if (type > M_HSC && type != M_DIR) throw new ArchiveException("알 수 없는 HA 압축 방식(" + type + ")입니다.");
                string dir = DecodeName(pathB, Cp949).Trim('/');
                string name = DecodeName(nameB, Cp949);
                string full = Normalize(dir.Length > 0 ? dir + "/" + name : name);
                if (full.Length == 0) continue;
                var it = new Item { Index = _items.Count, Type = type, Packed = clen, Size = olen, DataPos = data, Crc = crc };
                _items.Add(it);
                bool readOnly = mdilen >= 3 && mdi[0] == 2 && (mdi[1] & 0x80) == 0; // unix mode without owner write bit
                Entries.Add(new ArchiveEntry
                {
                    Index = it.Index, Path = full, Name = full, IsDir = type == M_DIR, Size = type == M_DIR ? 0 : olen,
                    PackedSize = clen, Crc = type == M_DIR ? (uint?)null : crc,
                    Method = type == M_DIR ? "" : type == M_CPY ? "CPY" : type == M_ASC ? "ASC" : "HSC",
                    MTime = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(time),
                    Attributes = (type == M_DIR ? 0x10u : 0) | (readOnly ? 1u : 0)
                });
            }
            if (_items.Count == 0 && count > 0) throw new ArchiveException("HA 파일이 손상되었습니다.");
        }

        public override void Extract(ISet<int> wanted, ManagedExtractContext ctx)
        {
            foreach (var it in _items)
            {
                if (!wanted.Contains(it.Index)) continue;
                if (it.Type == M_DIR) { ctx.Done(it.Index, null, false); continue; }
                var output = ctx.Open(it.Index) ?? Stream.Null;
                try
                {
                    var o = new HaOut(output) { Progress = ctx.Progress, Limit = it.Size };
                    _f.Position = it.DataPos;
                    var input = new HaIn(_f, it.Packed);
                    if (it.Size > 0)
                    {
                        switch (it.Type)
                        {
                            case M_CPY:
                                for (long left = it.Size; left > 0; left--)
                                {
                                    int c = input.Get();
                                    if (c < 0) break;
                                    o.Put(c);
                                }
                                o.Flush();
                                break;
                            case M_ASC: HaAsc.Unpack(input, o); break;
                            case M_HSC: HaHsc.Unpack(input, o); break;
                        }
                    }
                    string err = null;
                    if (o.Count != it.Size) err = "크기가 맞지 않습니다 (파일 손상)";
                    else if (o.Crc != it.Crc) err = "CRC 오류 (파일 손상)";
                    ctx.Done(it.Index, err, false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (ex is InvalidDataException || ex is EndOfStreamException || ex is IndexOutOfRangeException || ex is ArithmeticException)
                {
                    ctx.Done(it.Index, "HA 데이터가 손상되었습니다.", false);
                }
            }
        }

        // ---------------------------------------------------------------- test writer
        /// <summary>Builds an HA archive (method 0 = CPY, 1 = ASC, 2 = HSC). Self test only.</summary>
        internal static void WriteForTests(string path, IList<(string name, byte[] data, bool dir)> items, int method)
        {
            using (var f = File.Create(path))
            {
                f.Write(new byte[] { (byte)'H', (byte)'A', (byte)items.Count, (byte)(items.Count >> 8) }, 0, 4);
                foreach (var (name, data, dir) in items)
                {
                    int type = dir ? M_DIR : data.Length == 0 ? M_CPY : method;
                    byte[] packed = dir || data.Length == 0 ? new byte[0] : type == M_ASC ? HaAsc.Pack(data) : type == M_HSC ? HaHsc.Pack(data) : data;
                    uint crc = 0;
                    if (!dir)
                    {
                        var t = HaOut.MakeTable(); uint c = 0xFFFFFFFF;
                        foreach (var x in data) c = t[(c ^ x) & 0xFF] ^ (c >> 8);
                        crc = ~c;
                    }
                    string p = name.Replace('\\', '/');
                    int slash = p.LastIndexOf('/');
                    string dirPart = slash >= 0 ? p.Substring(0, slash + 1) : "", namePart = slash >= 0 ? p.Substring(slash + 1) : p;
                    var dp = Encoding.UTF8.GetBytes(dirPart);
                    for (int i = 0; i < dp.Length; i++) if (dp[i] == '/') dp[i] = 0xFF;
                    var np = Encoding.UTF8.GetBytes(namePart);
                    var hdr = new MemoryStream();
                    hdr.WriteByte((byte)(2 << 4 | type));
                    hdr.Write(BitConverter.GetBytes((uint)packed.Length), 0, 4);
                    hdr.Write(BitConverter.GetBytes((uint)(dir ? 0 : data.Length)), 0, 4);
                    hdr.Write(BitConverter.GetBytes(crc), 0, 4);
                    hdr.Write(BitConverter.GetBytes(1600000000u), 0, 4);
                    hdr.Write(dp, 0, dp.Length); hdr.WriteByte(0);
                    hdr.Write(np, 0, np.Length); hdr.WriteByte(0);
                    hdr.WriteByte(7);
                    hdr.Write(new byte[] { 2, 0xA4, 0x81, 0xE8, 0x03, 0xE8, 0x03 }, 0, 7);
                    var hb = hdr.ToArray();
                    f.Write(hb, 0, hb.Length);
                    f.Write(packed, 0, packed.Length);
                }
            }
        }
    }
}
