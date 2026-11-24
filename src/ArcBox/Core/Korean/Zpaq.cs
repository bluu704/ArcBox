using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace ArcBox.Core.Korean
{
    /// <summary>ZPAQ decompressor: C# port of libzpaq (public domain, Matt Mahoney) and the zpaq journaling reader.</summary>
    internal sealed class ZReader
    {
        private readonly Stream _s;
        public ZReader(Stream s) { _s = s; }
        public int Get() => _s.ReadByte();
        public long Position { get => _s.Position; set => _s.Position = value; }
    }

    internal static class ZpaqError { public static InvalidDataException E(string m) => new InvalidDataException("ZPAQ 데이터 오류: " + m); }

    // ------------------------------------------------------------------ ZPAQL virtual machine
    internal sealed class Zpaql
    {
        public byte[] Header = new byte[0];
        public int Cend, Hbegin, Hend;
        private byte[] _m = new byte[1];
        private uint[] _h = new uint[1];
        private readonly uint[] _r = new uint[256];
        private uint _a, _b, _c, _d;
        private int _f, _pc;
        public Stream Output;
        public HashAlgorithm Sha1;
        private readonly byte[] _out = new byte[1 << 14];
        private int _bufPtr;

        public static readonly int[] CompSize = { 0, 2, 3, 2, 3, 4, 6, 6, 3, 5 };

        public void Clear() { Cend = Hbegin = Hend = 0; _a = _b = _c = _d = 0; _f = _pc = 0; Header = new byte[0]; _h = new uint[1]; _m = new byte[1]; }

        public int Read(ZReader r)
        {
            int G() { int c = r.Get(); if (c < 0) throw ZpaqError.E("unexpected end of file"); return c; }
            int hsize = G(); hsize += G() * 256;
            Header = new byte[hsize + 300];
            Cend = Hbegin = Hend = 0;
            Header[Cend++] = (byte)(hsize & 255);
            Header[Cend++] = (byte)(hsize >> 8);
            while (Cend < 7) Header[Cend++] = (byte)G();
            int n = Header[Cend - 1];
            for (int i = 0; i < n; i++)
            {
                int type = G();
                Header[Cend++] = (byte)type;
                int size = type < CompSize.Length ? CompSize[type] : 0;
                if (size < 1) throw ZpaqError.E("invalid component type");
                if (Cend + size > hsize) throw ZpaqError.E("COMP overflows header");
                for (int j = 1; j < size; j++) Header[Cend++] = (byte)G();
            }
            if ((Header[Cend++] = (byte)G()) != 0) throw ZpaqError.E("missing COMP END");
            Hbegin = Hend = Cend + 128;
            if (Hend > hsize + 129) throw ZpaqError.E("missing HCOMP");
            while (Hend < hsize + 129) Header[Hend++] = (byte)G();
            if ((Header[Hend++] = (byte)G()) != 0) throw ZpaqError.E("missing HCOMP END");
            return Cend + Hend - Hbegin;
        }

        public void InitH() => Init(Header[2], Header[3]);
        public void InitP() => Init(Header[4], Header[5]);

        private void Init(int hbits, int mbits)
        {
            if (hbits > 30 || mbits > 30) throw ZpaqError.E("model needs too much memory");
            _h = new uint[1 << hbits];
            _m = new byte[1 << mbits];
            Array.Clear(_r, 0, 256);
            _a = _b = _c = _d = 0; _pc = 0; _f = 0;
        }

        public uint H(int i) => _h[i & (_h.Length - 1)];

        public void Flush()
        {
            if (_bufPtr == 0) return;
            Output?.Write(_out, 0, _bufPtr);
            Sha1?.TransformBlock(_out, 0, _bufPtr, null, 0);
            _bufPtr = 0;
        }

        public void OutC(int ch)
        {
            if (ch < 0) { Flush(); return; }
            _out[_bufPtr++] = (byte)ch;
            if (_bufPtr == _out.Length) Flush();
        }

        public void Run(uint input)
        {
            _pc = Hbegin;
            _a = input;
            while (Execute()) { }
        }

        private void Err() => throw ZpaqError.E("ZPAQL execution error");

        private int Jmp() => ((Header[_pc] + 128) & 255) - 127;

        private bool Execute()
        {
            int mm = _m.Length - 1, hm = _h.Length - 1;
            byte op = Header[_pc++];
            unchecked
            {
                switch (op)
                {
                    case 0: Err(); break;
                    case 1: ++_a; break;
                    case 2: --_a; break;
                    case 3: _a = ~_a; break;
                    case 4: _a = 0; break;
                    case 7: _a = _r[Header[_pc++]]; break;
                    case 8: { uint t = _a; _a = _b; _b = t; } break;
                    case 9: ++_b; break;
                    case 10: --_b; break;
                    case 11: _b = ~_b; break;
                    case 12: _b = 0; break;
                    case 15: _b = _r[Header[_pc++]]; break;
                    case 16: { uint t = _a; _a = _c; _c = t; } break;
                    case 17: ++_c; break;
                    case 18: --_c; break;
                    case 19: _c = ~_c; break;
                    case 20: _c = 0; break;
                    case 23: _c = _r[Header[_pc++]]; break;
                    case 24: { uint t = _a; _a = _d; _d = t; } break;
                    case 25: ++_d; break;
                    case 26: --_d; break;
                    case 27: _d = ~_d; break;
                    case 28: _d = 0; break;
                    case 31: _d = _r[Header[_pc++]]; break;
                    case 32: SwapM((int)(_b & mm)); break;
                    case 33: _m[_b & mm]++; break;
                    case 34: _m[_b & mm]--; break;
                    case 35: _m[_b & mm] = (byte)~_m[_b & mm]; break;
                    case 36: _m[_b & mm] = 0; break;
                    case 39: if (_f != 0) _pc += Jmp(); else ++_pc; break;
                    case 40: SwapM((int)(_c & mm)); break;
                    case 41: _m[_c & mm]++; break;
                    case 42: _m[_c & mm]--; break;
                    case 43: _m[_c & mm] = (byte)~_m[_c & mm]; break;
                    case 44: _m[_c & mm] = 0; break;
                    case 47: if (_f == 0) _pc += Jmp(); else ++_pc; break;
                    case 48: { uint t = _a; _a = _h[_d & hm]; _h[_d & hm] = t; } break;
                    case 49: ++_h[_d & hm]; break;
                    case 50: --_h[_d & hm]; break;
                    case 51: _h[_d & hm] = ~_h[_d & hm]; break;
                    case 52: _h[_d & hm] = 0; break;
                    case 55: _r[Header[_pc++]] = _a; break;
                    case 56: return false;
                    case 57: OutC((int)(_a & 255)); break;
                    case 59: _a = (_a + _m[_b & mm] + 512) * 773; break;
                    case 60: _h[_d & hm] = (_h[_d & hm] + _a + 512) * 773; break;
                    case 63: _pc += Jmp(); break;
                    case 64: break;
                    case 65: _a = _b; break;
                    case 66: _a = _c; break;
                    case 67: _a = _d; break;
                    case 68: _a = _m[_b & mm]; break;
                    case 69: _a = _m[_c & mm]; break;
                    case 70: _a = _h[_d & hm]; break;
                    case 71: _a = Header[_pc++]; break;
                    case 72: _b = _a; break;
                    case 73: break;
                    case 74: _b = _c; break;
                    case 75: _b = _d; break;
                    case 76: _b = _m[_b & mm]; break;
                    case 77: _b = _m[_c & mm]; break;
                    case 78: _b = _h[_d & hm]; break;
                    case 79: _b = Header[_pc++]; break;
                    case 80: _c = _a; break;
                    case 81: _c = _b; break;
                    case 82: break;
                    case 83: _c = _d; break;
                    case 84: _c = _m[_b & mm]; break;
                    case 85: _c = _m[_c & mm]; break;
                    case 86: _c = _h[_d & hm]; break;
                    case 87: _c = Header[_pc++]; break;
                    case 88: _d = _a; break;
                    case 89: _d = _b; break;
                    case 90: _d = _c; break;
                    case 91: break;
                    case 92: _d = _m[_b & mm]; break;
                    case 93: _d = _m[_c & mm]; break;
                    case 94: _d = _h[_d & hm]; break;
                    case 95: _d = Header[_pc++]; break;
                    case 96: _m[_b & mm] = (byte)_a; break;
                    case 97: _m[_b & mm] = (byte)_b; break;
                    case 98: _m[_b & mm] = (byte)_c; break;
                    case 99: _m[_b & mm] = (byte)_d; break;
                    case 100: break;
                    case 101: _m[_b & mm] = _m[_c & mm]; break;
                    case 102: _m[_b & mm] = (byte)_h[_d & hm]; break;
                    case 103: _m[_b & mm] = Header[_pc++]; break;
                    case 104: _m[_c & mm] = (byte)_a; break;
                    case 105: _m[_c & mm] = (byte)_b; break;
                    case 106: _m[_c & mm] = (byte)_c; break;
                    case 107: _m[_c & mm] = (byte)_d; break;
                    case 108: _m[_c & mm] = _m[_b & mm]; break;
                    case 109: break;
                    case 110: _m[_c & mm] = (byte)_h[_d & hm]; break;
                    case 111: _m[_c & mm] = Header[_pc++]; break;
                    case 112: _h[_d & hm] = _a; break;
                    case 113: _h[_d & hm] = _b; break;
                    case 114: _h[_d & hm] = _c; break;
                    case 115: _h[_d & hm] = _d; break;
                    case 116: _h[_d & hm] = _m[_b & mm]; break;
                    case 117: _h[_d & hm] = _m[_c & mm]; break;
                    case 118: break;
                    case 119: _h[_d & hm] = Header[_pc++]; break;
                    case 128: _a += _a; break;
                    case 129: _a += _b; break;
                    case 130: _a += _c; break;
                    case 131: _a += _d; break;
                    case 132: _a += _m[_b & mm]; break;
                    case 133: _a += _m[_c & mm]; break;
                    case 134: _a += _h[_d & hm]; break;
                    case 135: _a += Header[_pc++]; break;
                    case 136: _a -= _a; break;
                    case 137: _a -= _b; break;
                    case 138: _a -= _c; break;
                    case 139: _a -= _d; break;
                    case 140: _a -= _m[_b & mm]; break;
                    case 141: _a -= _m[_c & mm]; break;
                    case 142: _a -= _h[_d & hm]; break;
                    case 143: _a -= Header[_pc++]; break;
                    case 144: _a *= _a; break;
                    case 145: _a *= _b; break;
                    case 146: _a *= _c; break;
                    case 147: _a *= _d; break;
                    case 148: _a *= _m[_b & mm]; break;
                    case 149: _a *= _m[_c & mm]; break;
                    case 150: _a *= _h[_d & hm]; break;
                    case 151: _a *= Header[_pc++]; break;
                    case 152: Div(_a); break;
                    case 153: Div(_b); break;
                    case 154: Div(_c); break;
                    case 155: Div(_d); break;
                    case 156: Div(_m[_b & mm]); break;
                    case 157: Div(_m[_c & mm]); break;
                    case 158: Div(_h[_d & hm]); break;
                    case 159: Div(Header[_pc++]); break;
                    case 160: Mod(_a); break;
                    case 161: Mod(_b); break;
                    case 162: Mod(_c); break;
                    case 163: Mod(_d); break;
                    case 164: Mod(_m[_b & mm]); break;
                    case 165: Mod(_m[_c & mm]); break;
                    case 166: Mod(_h[_d & hm]); break;
                    case 167: Mod(Header[_pc++]); break;
                    case 168: _a &= _a; break;
                    case 169: _a &= _b; break;
                    case 170: _a &= _c; break;
                    case 171: _a &= _d; break;
                    case 172: _a &= _m[_b & mm]; break;
                    case 173: _a &= _m[_c & mm]; break;
                    case 174: _a &= _h[_d & hm]; break;
                    case 175: _a &= Header[_pc++]; break;
                    case 176: _a &= ~_a; break;
                    case 177: _a &= ~_b; break;
                    case 178: _a &= ~_c; break;
                    case 179: _a &= ~_d; break;
                    case 180: _a &= ~(uint)_m[_b & mm]; break;
                    case 181: _a &= ~(uint)_m[_c & mm]; break;
                    case 182: _a &= ~_h[_d & hm]; break;
                    case 183: _a &= ~(uint)Header[_pc++]; break;
                    case 184: _a |= _a; break;
                    case 185: _a |= _b; break;
                    case 186: _a |= _c; break;
                    case 187: _a |= _d; break;
                    case 188: _a |= _m[_b & mm]; break;
                    case 189: _a |= _m[_c & mm]; break;
                    case 190: _a |= _h[_d & hm]; break;
                    case 191: _a |= Header[_pc++]; break;
                    case 192: _a ^= _a; break;
                    case 193: _a ^= _b; break;
                    case 194: _a ^= _c; break;
                    case 195: _a ^= _d; break;
                    case 196: _a ^= _m[_b & mm]; break;
                    case 197: _a ^= _m[_c & mm]; break;
                    case 198: _a ^= _h[_d & hm]; break;
                    case 199: _a ^= Header[_pc++]; break;
                    case 200: _a <<= (int)(_a & 31); break;
                    case 201: _a <<= (int)(_b & 31); break;
                    case 202: _a <<= (int)(_c & 31); break;
                    case 203: _a <<= (int)(_d & 31); break;
                    case 204: _a <<= _m[_b & mm] & 31; break;
                    case 205: _a <<= _m[_c & mm] & 31; break;
                    case 206: _a <<= (int)(_h[_d & hm] & 31); break;
                    case 207: _a <<= Header[_pc++] & 31; break;
                    case 208: _a >>= (int)(_a & 31); break;
                    case 209: _a >>= (int)(_b & 31); break;
                    case 210: _a >>= (int)(_c & 31); break;
                    case 211: _a >>= (int)(_d & 31); break;
                    case 212: _a >>= _m[_b & mm] & 31; break;
                    case 213: _a >>= _m[_c & mm] & 31; break;
                    case 214: _a >>= (int)(_h[_d & hm] & 31); break;
                    case 215: _a >>= Header[_pc++] & 31; break;
                    case 216: _f = 1; break;
                    case 217: _f = _a == _b ? 1 : 0; break;
                    case 218: _f = _a == _c ? 1 : 0; break;
                    case 219: _f = _a == _d ? 1 : 0; break;
                    case 220: _f = _a == _m[_b & mm] ? 1 : 0; break;
                    case 221: _f = _a == _m[_c & mm] ? 1 : 0; break;
                    case 222: _f = _a == _h[_d & hm] ? 1 : 0; break;
                    case 223: _f = _a == Header[_pc++] ? 1 : 0; break;
                    case 224: _f = 0; break;
                    case 225: _f = _a < _b ? 1 : 0; break;
                    case 226: _f = _a < _c ? 1 : 0; break;
                    case 227: _f = _a < _d ? 1 : 0; break;
                    case 228: _f = _a < _m[_b & mm] ? 1 : 0; break;
                    case 229: _f = _a < _m[_c & mm] ? 1 : 0; break;
                    case 230: _f = _a < _h[_d & hm] ? 1 : 0; break;
                    case 231: _f = _a < Header[_pc++] ? 1 : 0; break;
                    case 232: _f = 0; break;
                    case 233: _f = _a > _b ? 1 : 0; break;
                    case 234: _f = _a > _c ? 1 : 0; break;
                    case 235: _f = _a > _d ? 1 : 0; break;
                    case 236: _f = _a > _m[_b & mm] ? 1 : 0; break;
                    case 237: _f = _a > _m[_c & mm] ? 1 : 0; break;
                    case 238: _f = _a > _h[_d & hm] ? 1 : 0; break;
                    case 239: _f = _a > Header[_pc++] ? 1 : 0; break;
                    case 255:
                        _pc = Hbegin + Header[_pc] + 256 * Header[_pc + 1];
                        if (_pc >= Hend) Err();
                        break;
                    default: Err(); break;
                }
            }
            if (_pc < 0 || _pc >= Header.Length) Err();
            return true;
        }

        private void SwapM(int i)
        {
            unchecked
            {
                _a ^= _m[i];
                _m[i] ^= (byte)_a;
                _a ^= _m[i];
            }
        }
        private void Div(uint x) { _a = x != 0 ? _a / x : 0; }
        private void Mod(uint x) { _a = x != 0 ? _a % x : 0; }
    }

    // ------------------------------------------------------------------ context model predictor
    internal sealed class ZPredictor
    {
        private sealed class Comp
        {
            public uint Limit, Cxt, A, B, C;
            public uint[] Cm = new uint[0];
            public byte[] Ht = new byte[0];
            public ushort[] A16 = new ushort[0];
        }

        private static readonly ushort[] Squasht = BuildSquash();
        private static readonly short[] Stretcht = BuildStretch();
        private static readonly byte[] Ns = ZpaqTables.Sns;

        private static ushort[] BuildSquash()
        {
            var s = new ushort[4096];
            Array.Copy(ZpaqTables.Ssquasht, 0, s, 1376, 1344);
            for (int i = 2720; i < 4096; i++) s[i] = 32767;
            return s;
        }

        private static short[] BuildStretch()
        {
            var s = new short[32768];
            int k = 16384;
            for (int i = 0; i < 712; i++) for (int j = ZpaqTables.Stdt[i]; j > 0; j--) s[k++] = (short)i;
            for (int i = 0; i < 16384; i++) s[i] = (short)-s[32767 - i];
            return s;
        }

        private static int Squash(int x) => Squasht[x + 2048];
        private static int Stretch(int x) => Stretcht[x];
        private static int Clamp2k(int x) => x < -2048 ? -2048 : x > 2047 ? 2047 : x;
        private static int Clamp512k(int x) => x < -(1 << 19) ? -(1 << 19) : x >= (1 << 19) ? (1 << 19) - 1 : x;
        private static int Next(int state, int y) => Ns[state * 4 + y];
        private static int CmInit(int state) => ((Ns[state * 4 + 3] * 2 + 1) << 22) / (Ns[state * 4 + 2] + Ns[state * 4 + 3] + 1);

        private readonly Zpaql _z;
        private int _c8 = 1, _hmap4 = 1;
        private readonly int[] _p = new int[256];
        private readonly uint[] _h = new uint[256];
        private readonly Comp[] _comp = new Comp[256];

        public ZPredictor(Zpaql z) { _z = z; for (int i = 0; i < 256; i++) _comp[i] = new Comp(); }

        public bool IsModeled => _z.Header[6] != 0;

        public void Init()
        {
            _z.InitH();
            Array.Clear(_p, 0, 256); Array.Clear(_h, 0, 256);
            _c8 = 1; _hmap4 = 1;
            for (int i = 0; i < 256; i++) _comp[i] = new Comp();
            var hd = _z.Header;
            int n = hd[6], cp = 7;
            for (int i = 0; i < n; i++)
            {
                var cr = _comp[i];
                switch (hd[cp])
                {
                    case 1: _p[i] = (hd[cp + 1] - 128) * 4; break;
                    case 2:
                        if (hd[cp + 1] > 28) throw ZpaqError.E("CM too big");
                        cr.Cm = new uint[1 << hd[cp + 1]];
                        cr.Limit = (uint)(hd[cp + 2] * 4);
                        for (int j = 0; j < cr.Cm.Length; j++) cr.Cm[j] = 0x80000000;
                        break;
                    case 3:
                        if (hd[cp + 1] > 24) throw ZpaqError.E("ICM too big");
                        cr.Limit = 1023;
                        cr.Cm = new uint[256];
                        cr.Ht = new byte[64 << hd[cp + 1]];
                        for (int j = 0; j < 256; j++) cr.Cm[j] = (uint)CmInit(j);
                        break;
                    case 4:
                        if (hd[cp + 1] > 28 || hd[cp + 2] > 30) throw ZpaqError.E("MATCH too big");
                        cr.Cm = new uint[1 << hd[cp + 1]];
                        cr.Ht = new byte[1 << hd[cp + 2]];
                        cr.Ht[0] = 1;
                        break;
                    case 5:
                        if (hd[cp + 1] >= i || hd[cp + 2] >= i) throw ZpaqError.E("AVG");
                        break;
                    case 6:
                        if (hd[cp + 1] > 28 || hd[cp + 2] >= i || hd[cp + 3] >= i) throw ZpaqError.E("MIX2");
                        cr.C = 1u << hd[cp + 1];
                        cr.A16 = new ushort[1 << hd[cp + 1]];
                        for (int j = 0; j < cr.A16.Length; j++) cr.A16[j] = 32768;
                        break;
                    case 7:
                    {
                        int m = hd[cp + 3];
                        if (hd[cp + 1] > 24 || hd[cp + 2] >= i || m < 1 || m > i - hd[cp + 2]) throw ZpaqError.E("MIX");
                        cr.C = 1u << hd[cp + 1];
                        cr.Cm = new uint[m << hd[cp + 1]];
                        for (int j = 0; j < cr.Cm.Length; j++) cr.Cm[j] = (uint)(65536 / m);
                        break;
                    }
                    case 8:
                        if (hd[cp + 1] > 24 || hd[cp + 2] >= i) throw ZpaqError.E("ISSE");
                        cr.Ht = new byte[64 << hd[cp + 1]];
                        cr.Cm = new uint[512];
                        for (int j = 0; j < 256; j++)
                        {
                            cr.Cm[j * 2] = 1 << 15;
                            cr.Cm[j * 2 + 1] = (uint)Clamp512k(Stretch(CmInit(j) >> 8) * 1024);
                        }
                        break;
                    case 9:
                        if (hd[cp + 1] > 24 || hd[cp + 2] >= i || hd[cp + 3] > hd[cp + 4] * 4) throw ZpaqError.E("SSE");
                        cr.Cm = new uint[32 << hd[cp + 1]];
                        cr.Limit = (uint)(hd[cp + 4] * 4);
                        for (int j = 0; j < cr.Cm.Length; j++) cr.Cm[j] = (uint)(Squash((j & 31) * 64 - 992) << 17 | hd[cp + 3]);
                        break;
                    default: throw ZpaqError.E("unknown component");
                }
                cp += Zpaql.CompSize[hd[cp]];
            }
        }

        private static int Find(byte[] ht, int sizebits, uint cxt)
        {
            int chk = (int)(cxt >> sizebits & 255);
            int h0 = (int)((cxt * 16) & (uint)(ht.Length - 16));
            if (ht[h0] == chk) return h0;
            int h1 = h0 ^ 16;
            if (ht[h1] == chk) return h1;
            int h2 = h0 ^ 32;
            if (ht[h2] == chk) return h2;
            int r;
            if (ht[h0 + 1] <= ht[h1 + 1] && ht[h0 + 1] <= ht[h2 + 1]) r = h0;
            else if (ht[h1 + 1] < ht[h2 + 1]) r = h1;
            else r = h2;
            Array.Clear(ht, r, 16);
            ht[r] = (byte)chk;
            return r;
        }

        private void Train(Comp cr, int y)
        {
            unchecked
            {
                int idx = (int)(cr.Cxt & (uint)(cr.Cm.Length - 1));
                uint pn = cr.Cm[idx];
                uint count = pn & 0x3ff;
                int error = y * 32767 - (int)(pn >> 17);
                pn += (uint)((error * ZpaqTables.Sdt[count]) & -1024) + (count < cr.Limit ? 1u : 0u);
                cr.Cm[idx] = pn;
            }
        }

        public int Predict()
        {
            var hd = _z.Header;
            int n = hd[6], cp = 7;
            unchecked
            {
                for (int i = 0; i < n; i++)
                {
                    var cr = _comp[i];
                    switch (hd[cp])
                    {
                        case 1: break;
                        case 2:
                            cr.Cxt = _h[i] ^ (uint)_hmap4;
                            _p[i] = Stretch((int)(cr.Cm[cr.Cxt & (uint)(cr.Cm.Length - 1)] >> 17));
                            break;
                        case 3:
                            if (_c8 == 1 || (_c8 & 0xf0) == 16) cr.C = (uint)Find(cr.Ht, hd[cp + 1] + 2, _h[i] + 16 * (uint)_c8);
                            cr.Cxt = cr.Ht[cr.C + (_hmap4 & 15)];
                            _p[i] = Stretch((int)(cr.Cm[cr.Cxt & (uint)(cr.Cm.Length - 1)] >> 8));
                            break;
                        case 4:
                            if (cr.A == 0) _p[i] = 0;
                            else
                            {
                                cr.C = (uint)(cr.Ht[(cr.Limit - cr.B) & (uint)(cr.Ht.Length - 1)] >> (int)(7 - cr.Cxt)) & 1;
                                int v = ZpaqTables.Sdt2k[cr.A] * (cr.C == 0 ? 1 : -1);
                                _p[i] = Stretch(v & 32767);
                            }
                            break;
                        case 5:
                            _p[i] = (_p[hd[cp + 1]] * hd[cp + 3] + _p[hd[cp + 2]] * (256 - hd[cp + 3])) >> 8;
                            break;
                        case 6:
                        {
                            cr.Cxt = (_h[i] + (uint)(_c8 & hd[cp + 5])) & (cr.C - 1);
                            int w = cr.A16[cr.Cxt];
                            _p[i] = (w * _p[hd[cp + 2]] + (65536 - w) * _p[hd[cp + 3]]) >> 16;
                            break;
                        }
                        case 7:
                        {
                            int m = hd[cp + 3];
                            cr.Cxt = _h[i] + (uint)(_c8 & hd[cp + 5]);
                            cr.Cxt = (cr.Cxt & (cr.C - 1)) * (uint)m;
                            int s = 0, j0 = hd[cp + 2];
                            for (int j = 0; j < m; j++) s += ((int)cr.Cm[cr.Cxt + j] >> 8) * _p[j0 + j];
                            _p[i] = Clamp2k(s >> 8);
                            break;
                        }
                        case 8:
                        {
                            if (_c8 == 1 || (_c8 & 0xf0) == 16) cr.C = (uint)Find(cr.Ht, hd[cp + 1] + 2, _h[i] + 16 * (uint)_c8);
                            cr.Cxt = cr.Ht[cr.C + (_hmap4 & 15)];
                            int w0 = (int)cr.Cm[cr.Cxt * 2], w1 = (int)cr.Cm[cr.Cxt * 2 + 1];
                            _p[i] = Clamp2k((w0 * _p[hd[cp + 2]] + w1 * 64) >> 16);
                            break;
                        }
                        case 9:
                        {
                            cr.Cxt = (_h[i] + (uint)_c8) * 32;
                            int pq = _p[hd[cp + 2]] + 992;
                            if (pq < 0) pq = 0;
                            if (pq > 1983) pq = 1983;
                            int wt = pq & 63;
                            pq >>= 6;
                            cr.Cxt += (uint)pq;
                            uint mask = (uint)(cr.Cm.Length - 1);
                            _p[i] = Stretch((int)(((cr.Cm[cr.Cxt & mask] >> 10) * (uint)(64 - wt) + (cr.Cm[(cr.Cxt + 1) & mask] >> 10) * (uint)wt) >> 13));
                            cr.Cxt += (uint)(wt >> 5);
                            break;
                        }
                    }
                    cp += Zpaql.CompSize[hd[cp]];
                }
            }
            return Squash(_p[n - 1]);
        }

        public void Update(int y)
        {
            var hd = _z.Header;
            int n = hd[6], cp = 7;
            unchecked
            {
                for (int i = 0; i < n; i++)
                {
                    var cr = _comp[i];
                    switch (hd[cp])
                    {
                        case 1: break;
                        case 2: Train(cr, y); break;
                        case 3:
                        {
                            int hi = (int)cr.C + (_hmap4 & 15);
                            cr.Ht[hi] = (byte)Next(cr.Ht[hi], y);
                            int idx = (int)(cr.Cxt & (uint)(cr.Cm.Length - 1));
                            uint pn = cr.Cm[idx];
                            pn += (uint)((y * 32767 - (int)(pn >> 8)) >> 2);
                            cr.Cm[idx] = pn;
                            break;
                        }
                        case 4:
                        {
                            uint hmask = (uint)(cr.Ht.Length - 1), cmask = (uint)(cr.Cm.Length - 1);
                            if ((int)cr.C != y) cr.A = 0;
                            cr.Ht[cr.Limit & hmask] = (byte)(cr.Ht[cr.Limit & hmask] * 2 + y);
                            if (++cr.Cxt == 8)
                            {
                                cr.Cxt = 0;
                                ++cr.Limit;
                                cr.Limit &= (1u << hd[cp + 2]) - 1;
                                if (cr.A == 0)
                                {
                                    cr.B = cr.Limit - cr.Cm[_h[i] & cmask];
                                    if ((cr.B & hmask) != 0)
                                        while (cr.A < 255 && cr.Ht[(cr.Limit - cr.A - 1) & hmask] == cr.Ht[(cr.Limit - cr.A - cr.B - 1) & hmask]) ++cr.A;
                                }
                                else cr.A += cr.A < 255 ? 1u : 0u;
                                cr.Cm[_h[i] & cmask] = cr.Limit;
                            }
                            break;
                        }
                        case 5: break;
                        case 6:
                        {
                            int err = (y * 32767 - Squash(_p[i])) * hd[cp + 4] >> 5;
                            int w = cr.A16[cr.Cxt];
                            w += (err * (_p[hd[cp + 2]] - _p[hd[cp + 3]]) + (1 << 12)) >> 13;
                            if (w < 0) w = 0;
                            if (w > 65535) w = 65535;
                            cr.A16[cr.Cxt] = (ushort)w;
                            break;
                        }
                        case 7:
                        {
                            int m = hd[cp + 3];
                            int err = (y * 32767 - Squash(_p[i])) * hd[cp + 4] >> 4;
                            int j0 = hd[cp + 2];
                            for (int j = 0; j < m; j++)
                            {
                                int w = (int)cr.Cm[cr.Cxt + j];
                                cr.Cm[cr.Cxt + j] = (uint)Clamp512k(w + ((err * _p[j0 + j] + (1 << 12)) >> 13));
                            }
                            break;
                        }
                        case 8:
                        {
                            int err = y * 32767 - Squash(_p[i]);
                            int w0 = (int)cr.Cm[cr.Cxt * 2], w1 = (int)cr.Cm[cr.Cxt * 2 + 1];
                            cr.Cm[cr.Cxt * 2] = (uint)Clamp512k(w0 + ((err * _p[hd[cp + 2]] + (1 << 12)) >> 13));
                            cr.Cm[cr.Cxt * 2 + 1] = (uint)Clamp512k(w1 + ((err + 16) >> 5));
                            cr.Ht[cr.C + (_hmap4 & 15)] = (byte)Next((int)cr.Cxt, y);
                            break;
                        }
                        case 9: Train(cr, y); break;
                    }
                    cp += Zpaql.CompSize[hd[cp]];
                }
            }
            _c8 += _c8 + y;
            if (_c8 >= 256)
            {
                _z.Run((uint)(_c8 - 256));
                _hmap4 = 1;
                _c8 = 1;
                for (int i = 0; i < n; i++) _h[i] = _z.H(i);
            }
            else if (_c8 >= 16 && _c8 < 32) _hmap4 = (_hmap4 & 0xf) << 5 | y << 4 | 1;
            else _hmap4 = (_hmap4 & 0x1f0) | (((_hmap4 & 0xf) * 2 + y) & 0xf);
        }
    }

    // ------------------------------------------------------------------ arithmetic decoder
    internal sealed class ZDecoder
    {
        private readonly ZReader _in;
        private uint _low = 1, _high = 0xFFFFFFFF, _curr;
        public readonly ZPredictor Pr;

        public ZDecoder(ZReader r, Zpaql z) { _in = r; Pr = new ZPredictor(z); }

        public int Get() => _in.Get();

        public void Init()
        {
            Pr.Init();
            if (Pr.IsModeled) { _low = 1; _high = 0xFFFFFFFF; _curr = 0; }
            else _low = _high = _curr = 0;
        }

        private int Decode(int p)
        {
            if (_curr < _low || _curr > _high) throw ZpaqError.E("archive corrupted");
            uint mid = _low + (uint)(((ulong)(_high - _low) * (uint)p) >> 16);
            int y;
            if (_curr <= mid) { y = 1; _high = mid; } else { y = 0; _low = mid + 1; }
            while ((_high ^ _low) < 0x1000000)
            {
                _high = _high << 8 | 255;
                _low <<= 8;
                if (_low == 0) _low = 1;
                int c = Get();
                if (c < 0) throw ZpaqError.E("unexpected end of file");
                _curr = _curr << 8 | (uint)c;
            }
            return y;
        }

        public int Decompress()
        {
            if (Pr.IsModeled)
            {
                if (_curr == 0) for (int i = 0; i < 4; i++) _curr = _curr << 8 | (uint)(Get() & 255);
                if (Decode(0) != 0)
                {
                    if (_curr != 0) throw ZpaqError.E("decoding end of stream");
                    return -1;
                }
                int c = 1;
                while (c < 256)
                {
                    int p = Pr.Predict() * 2 + 1;
                    c += c + Decode(p);
                    Pr.Update(c & 1);
                }
                return c - 256;
            }
            if (_curr == 0)
            {
                for (int i = 0; i < 4; i++) _curr = _curr << 8 | (uint)(Get() & 255);
                if (_curr == 0) return -1;
            }
            --_curr;
            int b = Get();
            if (b < 0) throw ZpaqError.E("unexpected end of file");
            return b;
        }

        public int Skip()
        {
            int c = -1;
            if (Pr.IsModeled)
            {
                while (_curr == 0) { int g = Get(); if (g < 0) return -1; _curr = (uint)g; }
                while (_curr != 0 && (c = Get()) >= 0) _curr = _curr << 8 | (uint)c;
                while ((c = Get()) == 0) { }
                return c;
            }
            if (_curr == 0) for (int i = 0; i < 4 && (c = Get()) >= 0; i++) _curr = _curr << 8 | (uint)c;
            while (_curr > 0)
            {
                while (_curr > 0) { --_curr; if (Get() < 0) throw ZpaqError.E("skipped to EOF"); }
                for (int i = 0; i < 4 && (c = Get()) >= 0; i++) _curr = _curr << 8 | (uint)c;
            }
            if (c >= 0) c = Get();
            return c;
        }
    }

    // ------------------------------------------------------------------ block / segment reader
    internal sealed class ZDecompresser
    {
        private readonly ZReader _in;
        private readonly Zpaql _z = new Zpaql();
        private readonly Zpaql _pz = new Zpaql();
        private ZDecoder _dec;
        private bool _firstSeg;
        private int _ppState, _ppHsize, _ph, _pm;

        public ZDecompresser(ZReader r) { _in = r; }

        public bool FindBlock()
        {
            uint h1 = 0x3D49B113, h2 = 0x29EB7F93, h3 = 0x2614BE13, h4 = 0x3828EB13;
            int c;
            unchecked
            {
                while ((c = _in.Get()) != -1)
                {
                    h1 = h1 * 12 + (uint)c; h2 = h2 * 20 + (uint)c; h3 = h3 * 28 + (uint)c; h4 = h4 * 44 + (uint)c;
                    if (h1 == 0xB16B88F1 && h2 == 0xFF5376F1 && h3 == 0x72AC5BF1 && h4 == 0x2F909AF1) break;
                }
            }
            if (c == -1) return false;
            c = _in.Get();
            if (c != 1 && c != 2) throw ZpaqError.E("unsupported ZPAQ level");
            if (_in.Get() != 1) throw ZpaqError.E("unsupported ZPAQL type");
            _z.Read(_in);
            if (c == 1 && _z.Header.Length > 6 && _z.Header[6] == 0) throw ZpaqError.E("level 1 requires a component");
            _dec = new ZDecoder(_in, _z);
            _firstSeg = true;
            return true;
        }

        /// <summary>Returns the segment file name, or null at end of block.</summary>
        public string FindFilename()
        {
            int c = _in.Get();
            if (c == 255) return null;
            if (c != 1) throw ZpaqError.E("missing segment or end of block");
            var b = new List<byte>();
            while (true)
            {
                c = _in.Get();
                if (c == -1) throw ZpaqError.E("unexpected EOF");
                if (c == 0) break;
                b.Add((byte)c);
            }
            return Encoding.UTF8.GetString(b.ToArray());
        }

        public string ReadComment()
        {
            var b = new List<byte>();
            while (true)
            {
                int c = _in.Get();
                if (c == -1) throw ZpaqError.E("unexpected EOF");
                if (c == 0) break;
                b.Add((byte)c);
            }
            if (_in.Get() != 0) throw ZpaqError.E("missing reserved byte");
            return Encoding.UTF8.GetString(b.ToArray());
        }

        private void PpWrite(int c)
        {
            switch (_ppState)
            {
                case 0:
                    if (c < 0) throw ZpaqError.E("unexpected EOS");
                    _ppState = c + 1;
                    if (_ppState > 2) throw ZpaqError.E("unknown post processing type");
                    if (_ppState == 1) _pz.Clear();
                    break;
                case 1: _pz.OutC(c); break;
                case 2:
                    if (c < 0) throw ZpaqError.E("unexpected EOS");
                    _ppHsize = c; _ppState = 3;
                    break;
                case 3:
                    if (c < 0) throw ZpaqError.E("unexpected EOS");
                    _ppHsize += c * 256;
                    if (_ppHsize < 1) throw ZpaqError.E("empty PCOMP");
                    _pz.Header = new byte[_ppHsize + 300];
                    _pz.Cend = 8;
                    _pz.Hbegin = _pz.Hend = _pz.Cend + 128;
                    _pz.Header[4] = (byte)_ph;
                    _pz.Header[5] = (byte)_pm;
                    _ppState = 4;
                    break;
                case 4:
                    if (c < 0) throw ZpaqError.E("unexpected EOS");
                    _pz.Header[_pz.Hend++] = (byte)c;
                    if (_pz.Hend - _pz.Hbegin == _ppHsize)
                    {
                        int hs = _pz.Cend - 2 + _pz.Hend - _pz.Hbegin;
                        _pz.Header[0] = (byte)(hs & 255);
                        _pz.Header[1] = (byte)(hs >> 8);
                        _pz.InitP();
                        _ppState = 5;
                    }
                    break;
                case 5:
                    _pz.Run(unchecked((uint)c));
                    if (c < 0) _pz.Flush();
                    break;
            }
        }

        /// <summary>Decompresses the current segment into output. Returns the SHA-1 of the output.</summary>
        public byte[] DecompressSegment(Stream output, Func<bool> cancelled = null)
        {
            if (_firstSeg)
            {
                _dec.Init();
                _ppState = 0; _ppHsize = 0; _ph = _z.Header[4]; _pm = _z.Header[5];
                _pz.Clear();
                _firstSeg = false;
            }
            using (var sha = SHA1.Create())
            {
                _pz.Output = output;
                _pz.Sha1 = sha;
                while ((_ppState & 3) != 1) PpWrite(_dec.Decompress());
                long n = 0;
                while (true)
                {
                    int c = _dec.Decompress();
                    PpWrite(c);
                    if (c == -1) break;
                    if ((++n & 0xFFFF) == 0 && cancelled != null && cancelled()) throw new OperationCanceledException();
                }
                _pz.Flush();
                sha.TransformFinalBlock(new byte[0], 0, 0);
                _pz.Output = null; _pz.Sha1 = null;
                return sha.Hash;
            }
        }

        /// <summary>Reads the segment trailer after a decompressed segment. Returns the stored SHA-1 or null.</summary>
        public byte[] ReadSegmentEnd(bool skipData)
        {
            int c = skipData ? _dec.Skip() : _in.Get();
            if (c == 254) return null;
            if (c == 253)
            {
                var h = new byte[20];
                for (int i = 0; i < 20; i++) { int b = _in.Get(); if (b < 0) throw ZpaqError.E("unexpected EOF"); h[i] = (byte)b; }
                return h;
            }
            throw ZpaqError.E("missing end of segment marker");
        }

        public void SkipSegment() => ReadSegmentEnd(true);
    }

    // ================================================================== archive
    internal sealed class ZpaqArchive : ManagedArchive
    {
        private sealed class FileRec { public string Name; public long Date; public long Attr; public List<uint> Ptr = new List<uint>(); }
        private sealed class Frag { public byte[] Sha1; public uint Size; public uint BlockStart; }
        private sealed class Seg { public long BlockPos; public int SegIndex; public int File; public bool LastOfFile; public byte[] Sha1; }

        private FileStream _f;
        private bool _journal;
        private readonly Dictionary<uint, Frag> _frags = new Dictionary<uint, Frag>();
        private readonly Dictionary<uint, long> _dblocks = new Dictionary<uint, long>();
        private readonly List<FileRec> _files = new List<FileRec>();
        private readonly List<Seg> _segs = new List<Seg>();
        public int Versions;

        public static bool Looks(byte[] h, int len)
        {
            if (len >= 4 && h[0] == '7' && h[1] == 'k' && h[2] == 'S' && h[3] == 't') return true;
            if (len >= 4 && h[0] == 'z' && h[1] == 'P' && h[2] == 'Q' && h[3] >= 1) return true;
            return false;
        }

        public static ZpaqArchive Open(string path)
        {
            var a = new ZpaqArchive { FormatName = "ZPAQ" };
            a._f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
            try { a.Parse(); } catch { a.Dispose(); throw; }
            a.PhysicalSize = a._f.Length;
            return a;
        }

        private static uint U32(byte[] b, int o) => BitConverter.ToUInt32(b, o);

        private void Parse()
        {
            var bs = new BufferedStream(_f, 1 << 16);
            var r = new ZReader(bs);
            var files = new Dictionary<string, FileRec>(StringComparer.Ordinal);
            var order = new List<string>();
            string lastFile = Path.GetFileNameWithoutExtension(_f.Name);
            bool stop = false;
            while (!stop)
            {
                long blockPos = r.Position;
                var d = new ZDecompresser(r);
                if (!d.FindBlock()) break;
                int segIndex = 0;
                string fname;
                while ((fname = d.FindFilename()) != null)
                {
                    string comment = d.ReadComment();
                    if (comment.EndsWith("jDC\u0001") && fname.Length == 28 && fname.StartsWith("jDC"))
                    {
                        _journal = true;
                        char type = fname[17];
                        uint num = uint.Parse(fname.Substring(18, 10));
                        if (type == 'd')
                        {
                            d.SkipSegment();
                            _dblocks[num] = blockPos;
                        }
                        else
                        {
                            var ms = new MemoryStream();
                            var hash = d.DecompressSegment(ms);
                            var stored = d.ReadSegmentEnd(false);
                            if (stored != null && !stored.SequenceEqual(hash)) throw new ArchiveException("ZPAQ 색인 블록이 손상되었습니다.");
                            var data = ms.ToArray();
                            if (type == 'c')
                            {
                                if (data.Length < 8) throw new ArchiveException("ZPAQ 파일이 손상되었습니다.");
                                long jmp = BitConverter.ToInt64(data, 0);
                                if (jmp < 0) { stop = true; break; } // incomplete transaction
                                Versions++;
                            }
                            else if (type == 'h')
                            {
                                if (data.Length % 24 != 4) throw new ArchiveException("ZPAQ 파일이 손상되었습니다.");
                                int n = (data.Length - 4) / 24;
                                for (int i = 0; i < n; i++)
                                {
                                    var sha = new byte[20];
                                    Buffer.BlockCopy(data, 4 + i * 24, sha, 0, 20);
                                    _frags[num + (uint)i] = new Frag { Sha1 = sha, Size = U32(data, 4 + i * 24 + 20), BlockStart = num };
                                }
                            }
                            else if (type == 'i')
                            {
                                int p = 0;
                                while (p + 9 <= data.Length)
                                {
                                    long date = BitConverter.ToInt64(data, p); p += 8;
                                    int z = Array.IndexOf(data, (byte)0, p);
                                    if (z < 0) throw new ArchiveException("ZPAQ 색인이 손상되었습니다.");
                                    string name = Encoding.UTF8.GetString(data, p, z - p);
                                    p = z + 1;
                                    if (date != 0)
                                    {
                                        var fr = new FileRec { Name = name, Date = date };
                                        uint na = U32(data, p); p += 4;
                                        for (int i = 0; i < na; i++) if (i < 8) fr.Attr |= (long)data[p + i] << (i * 8);
                                        p += (int)na;
                                        uint ni = U32(data, p); p += 4;
                                        for (int i = 0; i < ni; i++) { fr.Ptr.Add(U32(data, p)); p += 4; }
                                        if (!files.ContainsKey(name)) order.Add(name);
                                        files[name] = fr;
                                    }
                                    else if (files.Remove(name)) order.Remove(name);
                                }
                            }
                        }
                    }
                    else
                    {
                        // streaming format: segments with optional file names
                        if (fname.Length > 0) lastFile = fname.Replace('\\', '/');
                        bool newFile = fname.Length > 0 || _segs.Count == 0;
                        if (newFile)
                        {
                            var fr = new FileRec { Name = lastFile };
                            _files.Add(fr);
                        }
                        d.SkipSegment();
                        _segs.Add(new Seg { BlockPos = blockPos, SegIndex = segIndex, File = _files.Count - 1 });
                    }
                    segIndex++;
                }
            }
            if (_journal)
            {
                foreach (var n in order) _files.Add(files[n]);
                if (Versions == 0) Versions = 1;
            }
            for (int i = 0; i < _segs.Count; i++) _segs[i].LastOfFile = i == _segs.Count - 1 || _segs[i + 1].File != _segs[i].File;

            for (int i = 0; i < _files.Count; i++)
            {
                var fr = _files[i];
                bool dir = fr.Name.EndsWith("/");
                ulong size = 0;
                foreach (var p in fr.Ptr) if (_frags.TryGetValue(p, out var fg)) size += fg.Size;
                var e = new ArchiveEntry
                {
                    Index = i, Path = Normalize(fr.Name.TrimEnd('/')), IsDir = dir, Size = size, Method = "ZPAQ",
                    MTime = DateFromDecimal(fr.Date)
                };
                if ((fr.Attr & 255) == 'w') e.Attributes = (uint)(fr.Attr >> 8) & 0x37;
                if (string.IsNullOrEmpty(e.Path)) e.Path = "file" + i;
                int slash = e.Path.LastIndexOf('\\');
                e.Name = slash >= 0 ? e.Path.Substring(slash + 1) : e.Path;
                Entries.Add(e);
            }
            if (Entries.Count == 0 && !_journal && _segs.Count == 0) throw new ArchiveException("ZPAQ 파일에 데이터가 없습니다 (암호화된 ZPAQ는 지원하지 않습니다).");
        }

        private static DateTime? DateFromDecimal(long d)
        {
            if (d < 19000101000000) return null;
            try
            {
                return new DateTime((int)(d / 10000000000), (int)(d / 100000000 % 100), (int)(d / 1000000 % 100),
                    (int)(d / 10000 % 100), (int)(d / 100 % 100), (int)(d % 100), DateTimeKind.Utc);
            }
            catch { return null; }
        }

        // decoded d-block cache: start fragment -> fragment payloads
        private readonly Dictionary<uint, Dictionary<uint, byte[]>> _cache = new Dictionary<uint, Dictionary<uint, byte[]>>();
        private readonly LinkedList<uint> _lru = new LinkedList<uint>();

        private Dictionary<uint, byte[]> LoadBlock(uint start, ManagedExtractContext ctx)
        {
            if (_cache.TryGetValue(start, out var hit)) { _lru.Remove(start); _lru.AddFirst(start); return hit; }
            if (!_dblocks.TryGetValue(start, out long pos)) throw new InvalidDataException("ZPAQ 데이터 블록이 없습니다 (" + start + ")");
            _f.Position = pos;
            var r = new ZReader(new BufferedStream(_f, 1 << 16));
            var d = new ZDecompresser(r);
            if (!d.FindBlock() || d.FindFilename() == null) throw new InvalidDataException("ZPAQ 데이터 블록을 읽을 수 없습니다");
            d.ReadComment();
            var ms = new MemoryStream();
            var hash = d.DecompressSegment(ms);
            var stored = d.ReadSegmentEnd(false);
            if (stored != null && !stored.SequenceEqual(hash)) throw new InvalidDataException("ZPAQ 데이터 블록 체크섬 오류 (파일 손상)");
            var data = ms.GetBuffer();
            int len = (int)ms.Length;
            if (len < 8) throw new InvalidDataException("ZPAQ 데이터 블록이 손상되었습니다");
            uint n = U32(data, len - 4);
            if (len < 8 + 4L * n) throw new InvalidDataException("ZPAQ 데이터 블록이 손상되었습니다");
            var map = new Dictionary<uint, byte[]>();
            int sizeTable = len - 8 - (int)(4 * n), off = 0;
            using (var sha = SHA1.Create())
            {
                for (uint i = 0; i < n; i++)
                {
                    int sz = (int)U32(data, sizeTable + (int)(4 * i));
                    if (off + sz > sizeTable) throw new InvalidDataException("ZPAQ 데이터 블록이 손상되었습니다");
                    var frag = new byte[sz];
                    Buffer.BlockCopy(data, off, frag, 0, sz);
                    off += sz;
                    if (_frags.TryGetValue(start + i, out var meta) && !sha.ComputeHash(frag).SequenceEqual(meta.Sha1))
                        throw new InvalidDataException("ZPAQ 조각 체크섬 오류 (파일 손상)");
                    map[start + i] = frag;
                }
            }
            _cache[start] = map;
            _lru.AddFirst(start);
            while (_lru.Count > 6) { _cache.Remove(_lru.Last.Value); _lru.RemoveLast(); }
            return map;
        }

        public override void Extract(ISet<int> wanted, ManagedExtractContext ctx)
        {
            if (_journal) { ExtractJournal(wanted, ctx); return; }
            // streaming: decode blocks that contain wanted segments, sequentially
            Stream current = null;
            int currentFile = -1;
            string pendingError = null;
            foreach (var group in _segs.GroupBy(s => s.BlockPos))
            {
                bool any = group.Any(s => wanted.Contains(s.File));
                if (!any) continue;
                _f.Position = group.Key;
                var r = new ZReader(new BufferedStream(_f, 1 << 16));
                var d = new ZDecompresser(r);
                if (!d.FindBlock()) break;
                foreach (var seg in group)
                {
                    if (d.FindFilename() == null) break;
                    d.ReadComment();
                    bool want = wanted.Contains(seg.File);
                    if (want && seg.File != currentFile)
                    {
                        currentFile = seg.File;
                        pendingError = null;
                        current = new CrcStream(ctx.Open(seg.File)) { Progress = ctx.Progress };
                    }
                    try
                    {
                        var hash = d.DecompressSegment(want ? current : Stream.Null);
                        var stored = d.ReadSegmentEnd(false);
                        if (want && stored != null && !stored.SequenceEqual(hash)) pendingError = "체크섬 오류 (파일 손상)";
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (InvalidDataException ex) { if (want) { ctx.Done(seg.File, ex.Message, false); currentFile = -1; } break; }
                    if (want && seg.LastOfFile) { ctx.Done(seg.File, pendingError, false); currentFile = -1; }
                }
            }
        }

        private void ExtractJournal(ISet<int> wanted, ManagedExtractContext ctx)
        {
            for (int i = 0; i < _files.Count; i++)
            {
                if (!wanted.Contains(i)) continue;
                var output = ctx.Open(i);
                if (Entries[i].IsDir) { ctx.Done(i, null, false); continue; }
                try
                {
                    foreach (var p in _files[i].Ptr)
                    {
                        if (!_frags.TryGetValue(p, out var meta)) throw new InvalidDataException("ZPAQ 조각 정보가 없습니다");
                        var block = LoadBlock(meta.BlockStart, ctx);
                        var data = block[p];
                        output?.Write(data, 0, data.Length);
                        ctx.Progress?.Invoke(data.Length);
                    }
                    ctx.Done(i, null, false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (ex is InvalidDataException || ex is KeyNotFoundException || ex is IndexOutOfRangeException)
                {
                    ctx.Done(i, ex.Message, false);
                }
            }
        }

        public override void Dispose() => _f?.Dispose();
    }
}
