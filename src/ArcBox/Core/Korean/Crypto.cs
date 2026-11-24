using System;
using System.IO;
using System.Security.Cryptography;

namespace ArcBox.Core.Korean
{
    internal interface IDecryptor
    {
        void Decrypt(byte[] buf, int offset, int count);
    }

    /// <summary>PKZIP traditional encryption (ZIP 2.0).</summary>
    internal sealed class ZipCrypto : IDecryptor
    {
        private static readonly uint[] Crc = BuildCrc();
        private uint _k0 = 0x12345678, _k1 = 0x23456789, _k2 = 0x34567890;

        private static uint[] BuildCrc()
        {
            var t = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                t[i] = c;
            }
            return t;
        }

        public ZipCrypto(byte[] password) { foreach (var b in password) Update(b); }

        private void Update(byte c)
        {
            unchecked
            {
                _k0 = Crc[(_k0 ^ c) & 0xFF] ^ (_k0 >> 8);
                _k1 = (_k1 + (_k0 & 0xFF)) * 134775813 + 1;
                _k2 = Crc[(_k2 ^ (_k1 >> 24)) & 0xFF] ^ (_k2 >> 8);
            }
        }

        private byte Next()
        {
            uint t = _k2 | 2;
            return (byte)(unchecked(t * (t ^ 1)) >> 8);
        }

        /// <summary>Decrypts the 12-byte header and returns its last byte (for the password check).</summary>
        public byte CheckHeader(byte[] header12)
        {
            byte last = 0;
            for (int i = 0; i < 12; i++)
            {
                byte c = (byte)(header12[i] ^ Next());
                Update(c);
                last = c;
            }
            return last;
        }

        public void Decrypt(byte[] buf, int offset, int count)
        {
            for (int i = offset; i < offset + count; i++)
            {
                byte c = (byte)(buf[i] ^ Next());
                Update(c);
                buf[i] = c;
            }
        }
    }

    /// <summary>WinZip AE-2 style AES-CTR (little-endian counter starting at 1) and LEA-CTR (big-endian counter from 0).</summary>
    internal sealed class CtrDecryptor : IDecryptor, IDisposable
    {
        private readonly ICryptoTransform _aes;
        private readonly Lea _lea;
        private readonly bool _bigEndianCounter;
        private readonly byte[] _counter = new byte[16], _ks = new byte[16];
        private int _ksPos = 16;

        private CtrDecryptor(ICryptoTransform aes, Lea lea, bool bigEndian, bool startAtOne)
        {
            _aes = aes; _lea = lea; _bigEndianCounter = bigEndian;
            if (startAtOne) _counter[0] = 1;
        }

        /// <summary>Derives keys with PBKDF2-HMAC-SHA1 (1000 rounds). Returns null when the password is wrong.</summary>
        public static CtrDecryptor Create(bool lea, int keySize, byte[] password, byte[] salt, byte[] verifier)
        {
            byte[] dk;
            using (var kdf = new Rfc2898DeriveBytes(password, salt, 1000)) dk = kdf.GetBytes(keySize * 2 + 2);
            if (dk[keySize * 2] != verifier[0] || dk[keySize * 2 + 1] != verifier[1]) return null;
            var key = new byte[keySize];
            Buffer.BlockCopy(dk, 0, key, 0, keySize);
            if (lea) return new CtrDecryptor(null, new Lea(key), true, false);
            var aes = Aes.Create();
            aes.Mode = CipherMode.ECB;
            aes.Padding = PaddingMode.None;
            aes.Key = key;
            return new CtrDecryptor(aes.CreateEncryptor(), null, false, true);
        }

        public void Decrypt(byte[] buf, int offset, int count)
        {
            for (int i = offset; i < offset + count; i++)
            {
                if (_ksPos == 16)
                {
                    if (_aes != null) _aes.TransformBlock(_counter, 0, 16, _ks, 0);
                    else { Buffer.BlockCopy(_counter, 0, _ks, 0, 16); _lea.EncryptBlock(_ks); }
                    if (_bigEndianCounter) { for (int k = 15; k >= 0; k--) if (++_counter[k] != 0) break; }
                    else { for (int k = 0; k < 16; k++) if (++_counter[k] != 0) break; }
                    _ksPos = 0;
                }
                buf[i] ^= _ks[_ksPos++];
            }
        }

        public void Dispose() => _aes?.Dispose();
    }

    /// <summary>LEA block cipher (KS X 3246), encryption direction only (CTR mode).</summary>
    internal sealed class Lea
    {
        private static readonly uint[] Delta = { 0xc3efe9db, 0x44626b02, 0x79e27c8a, 0x78df30ec, 0x715ea49e, 0xc785da0a, 0xe04ef22a, 0xe5c40957 };
        private readonly uint[][] _rk;

        private static uint Rol(uint x, int n) { n &= 31; return n == 0 ? x : (x << n) | (x >> (32 - n)); }
        private static uint Ror(uint x, int n) { n &= 31; return n == 0 ? x : (x >> n) | (x << (32 - n)); }

        public Lea(byte[] key)
        {
            int nk = key.Length / 4;
            var t = new uint[nk];
            for (int i = 0; i < nk; i++) t[i] = BitConverter.ToUInt32(key, i * 4);
            if (nk == 4)
            {
                _rk = new uint[24][];
                for (int i = 0; i < 24; i++)
                {
                    uint d = Delta[i % 4];
                    unchecked
                    {
                        t[0] = Rol(t[0] + Rol(d, i), 1);
                        t[1] = Rol(t[1] + Rol(d, i + 1), 3);
                        t[2] = Rol(t[2] + Rol(d, i + 2), 6);
                        t[3] = Rol(t[3] + Rol(d, i + 3), 11);
                    }
                    _rk[i] = new[] { t[0], t[1], t[2], t[1], t[3], t[1] };
                }
            }
            else if (nk == 8)
            {
                _rk = new uint[32][];
                for (int i = 0; i < 32; i++)
                {
                    uint d = Delta[i % 8];
                    int b = 6 * i;
                    unchecked
                    {
                        t[b % 8] = Rol(t[b % 8] + Rol(d, i), 1);
                        t[(b + 1) % 8] = Rol(t[(b + 1) % 8] + Rol(d, i + 1), 3);
                        t[(b + 2) % 8] = Rol(t[(b + 2) % 8] + Rol(d, i + 2), 6);
                        t[(b + 3) % 8] = Rol(t[(b + 3) % 8] + Rol(d, i + 3), 11);
                        t[(b + 4) % 8] = Rol(t[(b + 4) % 8] + Rol(d, i + 4), 13);
                        t[(b + 5) % 8] = Rol(t[(b + 5) % 8] + Rol(d, i + 5), 17);
                    }
                    _rk[i] = new[] { t[b % 8], t[(b + 1) % 8], t[(b + 2) % 8], t[(b + 3) % 8], t[(b + 4) % 8], t[(b + 5) % 8] };
                }
            }
            else throw new ArgumentException("LEA key size");
        }

        public void EncryptBlock(byte[] block)
        {
            uint x0 = BitConverter.ToUInt32(block, 0), x1 = BitConverter.ToUInt32(block, 4), x2 = BitConverter.ToUInt32(block, 8), x3 = BitConverter.ToUInt32(block, 12);
            foreach (var rk in _rk)
            {
                unchecked
                {
                    uint t0 = Rol((x0 ^ rk[0]) + (x1 ^ rk[1]), 9);
                    uint t1 = Ror((x1 ^ rk[2]) + (x2 ^ rk[3]), 5);
                    uint t2 = Ror((x2 ^ rk[4]) + (x3 ^ rk[5]), 3);
                    x3 = x0; x0 = t0; x1 = t1; x2 = t2;
                }
            }
            Buffer.BlockCopy(BitConverter.GetBytes(x0), 0, block, 0, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(x1), 0, block, 4, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(x2), 0, block, 8, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(x3), 0, block, 12, 4);
        }

        /// <summary>KS X 3246 test vector check.</summary>
        public static bool SelfTest()
        {
            var key = new byte[] { 0x0f, 0x1e, 0x2d, 0x3c, 0x4b, 0x5a, 0x69, 0x78, 0x87, 0x96, 0xa5, 0xb4, 0xc3, 0xd2, 0xe1, 0xf0 };
            var pt = new byte[] { 0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18, 0x19, 0x1a, 0x1b, 0x1c, 0x1d, 0x1e, 0x1f };
            var ct = new byte[] { 0x9f, 0xc8, 0x4e, 0x35, 0x28, 0xc6, 0xc6, 0x18, 0x55, 0x32, 0xc7, 0xa7, 0x04, 0x64, 0x8b, 0xfd };
            new Lea(key).EncryptBlock(pt);
            for (int i = 0; i < 16; i++) if (pt[i] != ct[i]) return false;
            var key256 = new byte[] { 0x0f, 0x1e, 0x2d, 0x3c, 0x4b, 0x5a, 0x69, 0x78, 0x87, 0x96, 0xa5, 0xb4, 0xc3, 0xd2, 0xe1, 0xf0, 0xf0, 0xe1, 0xd2, 0xc3, 0xb4, 0xa5, 0x96, 0x87, 0x78, 0x69, 0x5a, 0x4b, 0x3c, 0x2d, 0x1e, 0x0f };
            var pt2 = new byte[] { 0x30, 0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3a, 0x3b, 0x3c, 0x3d, 0x3e, 0x3f };
            var ct2 = new byte[] { 0xd6, 0x51, 0xaf, 0xf6, 0x47, 0xb1, 0x89, 0xc1, 0x3a, 0x89, 0x00, 0xca, 0x27, 0xf9, 0xe1, 0x97 };
            new Lea(key256).EncryptBlock(pt2);
            for (int i = 0; i < 16; i++) if (pt2[i] != ct2[i]) return false;
            return true;
        }
    }

    /// <summary>Read-only view of [offset, offset+length) of a seekable stream, optionally decrypting.</summary>
    internal sealed class SegmentStream : Stream
    {
        private readonly Stream _s;
        private readonly long _start, _len;
        private long _pos;
        private readonly IDecryptor _dec;

        public SegmentStream(Stream s, long start, long len, IDecryptor dec)
        {
            _s = s; _start = start; _len = len; _dec = dec;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_pos >= _len) return 0;
            int want = (int)Math.Min(count, _len - _pos);
            _s.Position = _start + _pos;
            int n = _s.Read(buffer, offset, want);
            if (n <= 0) throw new InvalidDataException("압축 파일이 잘렸습니다 (분할 파일 누락?)");
            _dec?.Decrypt(buffer, offset, n);
            _pos += n;
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _len;
        public override long Position { get => _pos; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Concatenation of several streams (read-only, forward).</summary>
    internal sealed class ChainStream : Stream
    {
        private readonly System.Collections.Generic.IEnumerator<Stream> _parts;
        private Stream _cur;
        private bool _done;

        public ChainStream(System.Collections.Generic.IEnumerable<Stream> parts) { _parts = parts.GetEnumerator(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            while (!_done)
            {
                if (_cur == null)
                {
                    if (!_parts.MoveNext()) { _done = true; break; }
                    _cur = _parts.Current;
                }
                int n = _cur.Read(buffer, offset, count);
                if (n > 0) return n;
                _cur = null;
            }
            return 0;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Write-only stream that computes CRC-32 and forwards (or discards when target is null).</summary>
    internal sealed class CrcStream : Stream
    {
        private static readonly uint[] Table = BuildTable();
        private Stream _target;
        private uint _crc = 0xFFFFFFFF;
        public long Count;
        public long Limit = long.MaxValue;
        public Action<int> Progress;

        public CrcStream(Stream target) { _target = target; }

        private static uint[] BuildTable()
        {
            var t = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                t[i] = c;
            }
            return t;
        }

        public uint Crc => _crc ^ 0xFFFFFFFF;
        public void ResetCrc() { _crc = 0xFFFFFFFF; }
        public void Retarget(Stream s) { _target = s; }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (Count + count > Limit) throw new InvalidDataException("압축 해제 크기가 기록된 크기보다 큽니다 (손상된 파일)");
            uint c = _crc;
            for (int i = offset; i < offset + count; i++) c = Table[(c ^ buffer[i]) & 0xFF] ^ (c >> 8);
            _crc = c;
            Count += count;
            _target?.Write(buffer, offset, count);
            Progress?.Invoke(count);
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => Count;
        public override long Position { get => Count; set => throw new NotSupportedException(); }
        public override void Flush() => _target?.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
