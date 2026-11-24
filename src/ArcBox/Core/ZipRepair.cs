using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace ArcBox.Core
{
    /// <summary>
    /// Rebuilds a damaged ZIP from its local file headers ("손상된 ZIP 복구").
    /// The central directory at the end of a ZIP is what usually gets lost (cut-off download, broken copy). This walks the file
    /// from the start, finds every local header, checks stored/deflated data against its CRC, and writes the entries that are
    /// intact into a new ZIP with a fresh central directory. Data is copied as-is (no recompression), so encrypted and
    /// LZMA/BZip2/Zstd entries are kept too; those can only be checked for completeness, not for content.
    /// </summary>
    public static class ZipRepair
    {
        public sealed class Result
        {
            public int Recovered, Unverified;
            public readonly List<string> Damaged = new List<string>();
            public long Scanned;
            public string Output;
        }

        private sealed class Item
        {
            public ushort Version, Flags, Method, Time, Date;
            public uint Crc;
            public long Comp, Uncomp;
            public byte[] Name, Extra;
            public long DataStart;
            public long OutOffset;
            public bool IsDir;
        }

        private const uint LocalSig = 0x04034b50, CentralSig = 0x02014b50, DescSig = 0x08074b50, EndSig = 0x06054b50;

        public static Result Repair(string input, string output, IOperationUI ui)
        {
            var res = new Result { Output = output };
            var items = new List<Item>();
            using (var src = new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
            using (var dst = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            {
                long len = src.Length;
                ui?.SetTotal((ulong)len);
                long pos = FindSig(src, 0, len, LocalSig);
                while (pos >= 0 && pos + 30 <= len)
                {
                    if (ui != null) { if (ui.IsCancelled) throw new OperationCanceledException(); ui.WaitIfPaused(); ui.SetCompleted((ulong)pos); }
                    var it = ReadLocal(src, pos, len);
                    if (it == null) { pos = FindSig(src, pos + 1, len, LocalSig); continue; }
                    string name = DecodeName(it);
                    ui?.SetCurrentFile(name);

                    long next;
                    bool ok = Resolve(src, it, len, out next, out bool verified);
                    if (!ok)
                    {
                        res.Damaged.Add(name);
                        // the header may be garbage that happened to match: keep scanning right after it
                        pos = FindSig(src, pos + 4, len, LocalSig);
                        continue;
                    }
                    if (!verified && !it.IsDir) res.Unverified++;
                    it.OutOffset = dst.Position;
                    WriteLocal(dst, it);
                    Copy(src, it.DataStart, it.Comp, dst);
                    if ((it.Flags & 8) != 0) WriteDescriptor(dst, it);
                    items.Add(it);
                    res.Recovered++;
                    pos = FindSig(src, next, len, LocalSig);
                }
                res.Scanned = len;
                if (items.Count == 0) throw new ArchiveException("복구할 수 있는 파일을 찾지 못했습니다. ZIP 파일이 아니거나 너무 많이 손상되었습니다.");
                WriteCentral(dst, items);
            }
            return res;
        }

        // ------------------------------------------------------------ reading
        private static Item ReadLocal(Stream s, long pos, long len)
        {
            var h = ReadAt(s, pos, 30);
            if (h == null || U32(h, 0) != LocalSig) return null;
            var it = new Item
            {
                Version = U16(h, 4), Flags = U16(h, 6), Method = U16(h, 8), Time = U16(h, 10), Date = U16(h, 12),
                Crc = U32(h, 14), Comp = U32(h, 18), Uncomp = U32(h, 22),
            };
            int nameLen = U16(h, 26), extraLen = U16(h, 28);
            if (nameLen == 0 || nameLen > 4096) return null;
            if (!(it.Method == 0 || it.Method == 8 || it.Method == 9 || it.Method == 12 || it.Method == 14 || it.Method == 93 || it.Method == 95 || it.Method == 98 || it.Method == 99)) return null;
            if (pos + 30 + nameLen + extraLen > len) return null;
            it.Name = ReadAt(s, pos + 30, nameLen);
            var extra = ReadAt(s, pos + 30 + nameLen, extraLen) ?? new byte[0];
            it.DataStart = pos + 30 + nameLen + extraLen;
            // zip64 sizes, then drop the zip64 field (it is rebuilt on write)
            var kept = new MemoryStream();
            int i = 0;
            while (i + 4 <= extra.Length)
            {
                int id = U16(extra, i), sz = U16(extra, i + 2);
                if (i + 4 + sz > extra.Length) break;
                if (id == 1)
                {
                    int o = i + 4;
                    if (it.Uncomp == 0xFFFFFFFF && o + 8 <= i + 4 + sz) { it.Uncomp = (long)U64(extra, o); o += 8; }
                    if (it.Comp == 0xFFFFFFFF && o + 8 <= i + 4 + sz) { it.Comp = (long)U64(extra, o); o += 8; }
                }
                else kept.Write(extra, i, 4 + sz);
                i += 4 + sz;
            }
            it.Extra = kept.ToArray();
            it.IsDir = it.Name[it.Name.Length - 1] == '/' || it.Name[it.Name.Length - 1] == '\\';
            return it;
        }

        /// <summary>Works out where the entry's data ends and checks it. next = where to look for the following header.</summary>
        private static bool Resolve(Stream s, Item it, long len, out long next, out bool verified)
        {
            verified = false;
            next = it.DataStart;
            bool encrypted = (it.Flags & 1) != 0;
            bool hasDesc = (it.Flags & 8) != 0;
            if (!hasDesc || it.Comp > 0)
            {
                if (it.DataStart + it.Comp <= len && TryVerify(s, it, encrypted, out verified))
                {
                    long end = it.DataStart + it.Comp;
                    next = end + (hasDesc ? DescriptorLength(s, end, it.Comp) : 0);
                    return true;
                }
                if (!hasDesc) return false; // damaged data, or the entry was cut off at the end of the file
            }

            // sizes are only in the data descriptor after the data: try each candidate until the data checks out
            long from = it.DataStart;
            for (int tries = 0; tries < 64; tries++)
            {
                long d = FindSig(s, from, len, DescSig);
                if (d < 0) return false;
                var b = ReadAt(s, d, 24) ?? ReadAt(s, d, 16);
                if (b != null)
                {
                    long comp = d - it.DataStart;
                    if (U32(b, 8) == comp)
                    {
                        var c = Copy(it); c.Crc = U32(b, 4); c.Comp = comp; c.Uncomp = U32(b, 12);
                        if (TryVerify(s, c, encrypted, out verified)) { it.Crc = c.Crc; it.Comp = c.Comp; it.Uncomp = c.Uncomp; next = d + 16; return true; }
                    }
                    if (b.Length >= 24 && (long)U64(b, 8) == comp)
                    {
                        var c = Copy(it); c.Crc = U32(b, 4); c.Comp = comp; c.Uncomp = (long)U64(b, 16);
                        if (TryVerify(s, c, encrypted, out verified)) { it.Crc = c.Crc; it.Comp = c.Comp; it.Uncomp = c.Uncomp; next = d + 24; return true; }
                    }
                }
                from = d + 1;
            }
            return false;
        }

        /// <summary>Length of the data descriptor at 'at' (with or without signature, 32- or 64-bit sizes).</summary>
        private static int DescriptorLength(Stream s, long at, long comp)
        {
            var b = ReadAt(s, at, 24) ?? ReadAt(s, at, 16) ?? ReadAt(s, at, 12);
            if (b == null) return 0;
            if (U32(b, 0) == DescSig)
                return b.Length >= 24 && U32(b, 8) != comp && (long)U64(b, 8) == comp ? 24 : 16;
            return b.Length >= 20 && U32(b, 4) != comp && (long)U64(b, 4) == comp ? 20 : 12;
        }

        private static bool TryVerify(Stream s, Item it, bool encrypted, out bool verified)
        {
            verified = false;
            if (it.DataStart + it.Comp > s.Length) return false;
            if (encrypted || !(it.Method == 0 || it.Method == 8)) return true; // can't check the content; size fits
            try
            {
                uint crc = 0xFFFFFFFF;
                long total = 0;
                s.Position = it.DataStart;
                var limited = new SubStream(s, it.DataStart, it.Comp);
                using (var data = it.Method == 8 ? (Stream)new DeflateStream(limited, CompressionMode.Decompress, true) : limited)
                {
                    var buf = new byte[1 << 16];
                    int n;
                    while ((n = data.Read(buf, 0, buf.Length)) > 0)
                    {
                        crc = Crc32.Update(crc, buf, 0, n);
                        total += n;
                        if (total > it.Uncomp + (1 << 20) && it.Uncomp > 0) return false;
                    }
                }
                verified = (crc ^ 0xFFFFFFFF) == it.Crc && (it.Uncomp == 0 || total == it.Uncomp);
                if (verified && it.Uncomp == 0) it.Uncomp = total;
                return verified;
            }
            catch { return false; }
        }

        // ------------------------------------------------------------ writing
        private static bool NeedZip64(Item it) => it.Comp >= 0xFFFFFFFF || it.Uncomp >= 0xFFFFFFFF;

        private static void WriteLocal(Stream d, Item it)
        {
            bool z64 = NeedZip64(it);
            bool desc = (it.Flags & 8) != 0;
            var extra = new MemoryStream();
            if (z64)
            {
                W16(extra, 1); W16(extra, 16); W64(extra, (ulong)it.Uncomp); W64(extra, (ulong)it.Comp);
            }
            extra.Write(it.Extra, 0, it.Extra.Length);
            var h = new MemoryStream();
            W32(h, LocalSig); W16(h, z64 ? (ushort)Math.Max((int)it.Version, 45) : it.Version); W16(h, it.Flags); W16(h, it.Method);
            W16(h, it.Time); W16(h, it.Date);
            // with a data descriptor (needed for ZipCrypto's check byte) the header keeps zeros, like the original
            W32(h, desc ? 0 : it.Crc);
            W32(h, desc ? 0 : z64 ? 0xFFFFFFFF : (uint)it.Comp);
            W32(h, desc ? 0 : z64 ? 0xFFFFFFFF : (uint)it.Uncomp);
            W16(h, (ushort)it.Name.Length); W16(h, (ushort)extra.Length);
            h.Write(it.Name, 0, it.Name.Length);
            extra.Position = 0; extra.CopyTo(h);
            h.Position = 0; h.CopyTo(d);
        }

        private static void WriteDescriptor(Stream d, Item it)
        {
            var h = new MemoryStream();
            W32(h, DescSig); W32(h, it.Crc);
            if (NeedZip64(it)) { W64(h, (ulong)it.Comp); W64(h, (ulong)it.Uncomp); }
            else { W32(h, (uint)it.Comp); W32(h, (uint)it.Uncomp); }
            h.Position = 0; h.CopyTo(d);
        }

        private static void WriteCentral(Stream d, List<Item> items)
        {
            long cdStart = d.Position;
            foreach (var it in items)
            {
                bool big = NeedZip64(it) || it.OutOffset >= 0xFFFFFFFF;
                var extra = new MemoryStream();
                if (big)
                {
                    var z = new MemoryStream();
                    if (it.Uncomp >= 0xFFFFFFFF || NeedZip64(it)) W64(z, (ulong)it.Uncomp);
                    if (it.Comp >= 0xFFFFFFFF || NeedZip64(it)) W64(z, (ulong)it.Comp);
                    if (it.OutOffset >= 0xFFFFFFFF) W64(z, (ulong)it.OutOffset);
                    W16(extra, 1); W16(extra, (ushort)z.Length); z.Position = 0; z.CopyTo(extra);
                }
                extra.Write(it.Extra, 0, it.Extra.Length);
                var h = new MemoryStream();
                W32(h, CentralSig);
                W16(h, (ushort)(0x0300 | Math.Max((int)it.Version & 0xFF, big ? 45 : 20))); // made by: version, host unix-neutral
                W16(h, big ? (ushort)Math.Max((int)it.Version, 45) : it.Version);
                W16(h, it.Flags); W16(h, it.Method); W16(h, it.Time); W16(h, it.Date); W32(h, it.Crc);
                bool z64 = NeedZip64(it);
                W32(h, z64 ? 0xFFFFFFFF : (uint)it.Comp);
                W32(h, z64 ? 0xFFFFFFFF : (uint)it.Uncomp);
                W16(h, (ushort)it.Name.Length); W16(h, (ushort)extra.Length); W16(h, 0); // comment
                W16(h, 0); W16(h, 0);                                                     // disk, internal attrs
                // external attributes: unix mode + DOS directory/archive bit (the original central record is gone)
                W32(h, it.IsDir ? (0x41ED0000u | 0x10) : (0x81A40000u | 0x20));
                W32(h, it.OutOffset >= 0xFFFFFFFF ? 0xFFFFFFFF : (uint)it.OutOffset);
                h.Write(it.Name, 0, it.Name.Length);
                extra.Position = 0; extra.CopyTo(h);
                h.Position = 0; h.CopyTo(d);
            }
            long cdEnd = d.Position, cdSize = cdEnd - cdStart;
            bool z64end = items.Count >= 0xFFFF || cdStart >= 0xFFFFFFFF || cdSize >= 0xFFFFFFFF;
            var e = new MemoryStream();
            if (z64end)
            {
                W32(e, 0x06064b50); W64(e, 44); W16(e, 45); W16(e, 45); W32(e, 0); W32(e, 0);
                W64(e, (ulong)items.Count); W64(e, (ulong)items.Count); W64(e, (ulong)cdSize); W64(e, (ulong)cdStart);
                W32(e, 0x07064b50); W32(e, 0); W64(e, (ulong)cdEnd); W32(e, 1);
            }
            W32(e, EndSig); W16(e, 0); W16(e, 0);
            W16(e, (ushort)Math.Min(items.Count, 0xFFFF)); W16(e, (ushort)Math.Min(items.Count, 0xFFFF));
            W32(e, cdSize >= 0xFFFFFFFF ? 0xFFFFFFFF : (uint)cdSize);
            W32(e, cdStart >= 0xFFFFFFFF ? 0xFFFFFFFF : (uint)cdStart);
            W16(e, 0);
            e.Position = 0; e.CopyTo(d);
        }

        // ------------------------------------------------------------ helpers
        private static string DecodeName(Item it)
        {
            try
            {
                var enc = (it.Flags & 0x800) != 0 ? Encoding.UTF8 : Encoding.GetEncoding(949);
                return enc.GetString(it.Name);
            }
            catch { return Encoding.UTF8.GetString(it.Name); }
        }

        private static Item Copy(Item a) => new Item
        {
            Version = a.Version, Flags = a.Flags, Method = a.Method, Time = a.Time, Date = a.Date, Crc = a.Crc, Comp = a.Comp, Uncomp = a.Uncomp,
            Name = a.Name, Extra = a.Extra, DataStart = a.DataStart, IsDir = a.IsDir,
        };

        /// <summary>Position of the next 4-byte signature at or after start, or -1.</summary>
        private static long FindSig(Stream s, long start, long len, uint sig)
        {
            if (start < 0) start = 0;
            var buf = new byte[1 << 20];
            byte b0 = (byte)sig, b1 = (byte)(sig >> 8), b2 = (byte)(sig >> 16), b3 = (byte)(sig >> 24);
            long p = start;
            while (p + 4 <= len)
            {
                s.Position = p;
                int n = ReadFull(s, buf, (int)Math.Min(buf.Length, len - p));
                if (n < 4) return -1;
                for (int i = 0; i + 3 < n; i++)
                    if (buf[i] == b0 && buf[i + 1] == b1 && buf[i + 2] == b2 && buf[i + 3] == b3) return p + i;
                p += n - 3;
            }
            return -1;
        }

        private static void Copy(Stream src, long from, long count, Stream dst)
        {
            src.Position = from;
            var buf = new byte[1 << 16];
            while (count > 0)
            {
                int n = src.Read(buf, 0, (int)Math.Min(buf.Length, count));
                if (n <= 0) throw new EndOfStreamException();
                dst.Write(buf, 0, n);
                count -= n;
            }
        }

        private static byte[] ReadAt(Stream s, long pos, int count)
        {
            if (pos < 0 || pos + count > s.Length) return null;
            s.Position = pos;
            var b = new byte[count];
            return ReadFull(s, b, count) == count ? b : null;
        }

        private static int ReadFull(Stream s, byte[] b, int count)
        {
            int total = 0;
            while (total < count) { int n = s.Read(b, total, count - total); if (n <= 0) break; total += n; }
            return total;
        }

        private static ushort U16(byte[] b, int o) => (ushort)(b[o] | (b[o + 1] << 8));
        private static uint U32(byte[] b, int o) => (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));
        private static ulong U64(byte[] b, int o) => U32(b, o) | ((ulong)U32(b, o + 4) << 32);
        private static void W16(Stream s, ushort v) { s.WriteByte((byte)v); s.WriteByte((byte)(v >> 8)); }
        private static void W16(Stream s, int v) => W16(s, (ushort)v);
        private static void W32(Stream s, uint v) { W16(s, (ushort)v); W16(s, (ushort)(v >> 16)); }
        private static void W64(Stream s, ulong v) { W32(s, (uint)v); W32(s, (uint)(v >> 32)); }

        private static class Crc32
        {
            private static readonly uint[] T = Make();
            private static uint[] Make()
            {
                var t = new uint[256];
                for (uint i = 0; i < 256; i++) { uint c = i; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1; t[i] = c; }
                return t;
            }
            public static uint Update(uint crc, byte[] b, int o, int n) { for (int i = 0; i < n; i++) crc = T[(crc ^ b[o + i]) & 0xFF] ^ (crc >> 8); return crc; }
        }

        /// <summary>Read-only window onto part of another stream.</summary>
        private sealed class SubStream : Stream
        {
            private readonly Stream _s; private readonly long _start, _len; private long _pos;
            public SubStream(Stream s, long start, long len) { _s = s; _start = start; _len = len; }
            public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
            public override long Length => _len;
            public override long Position { get => _pos; set => throw new NotSupportedException(); }
            public override int Read(byte[] buffer, int offset, int count)
            {
                long left = _len - _pos;
                if (left <= 0) return 0;
                _s.Position = _start + _pos;
                int n = _s.Read(buffer, offset, (int)Math.Min(count, left));
                _pos += n;
                return n;
            }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
