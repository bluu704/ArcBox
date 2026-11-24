using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace ArcBox.Core
{
    /// <summary>Exposes a .NET stream to 7-Zip as IInStream. With a reopen factory the underlying file is
    /// closed as soon as it has been read to the end (keeps handle count low for big folders) and transparently
    /// reopened if 7-Zip seeks back later (e.g. ZIP falls back to "store" for incompressible data).</summary>
    [ComVisible(true)]
    internal sealed class InStreamWrapper : IInStream, ISequentialInStream, IStreamGetSize, IDisposable
    {
        private Stream _s;
        private byte[] _buf = new byte[0];
        private readonly bool _ownsStream;
        private readonly Func<Stream> _reopen;
        private long _pos;
        private bool _disposed;
        public Exception Error;

        public InStreamWrapper(Stream s, bool ownsStream, Func<Stream> reopen = null)
        {
            _s = s; _ownsStream = ownsStream; _reopen = reopen;
        }

        private Stream S
        {
            get
            {
                if (_s == null)
                {
                    if (_reopen == null || _disposed) throw new ObjectDisposedException("stream");
                    _s = _reopen();
                    _s.Position = _pos;
                }
                return _s;
            }
        }

        public int Read(IntPtr data, uint size, IntPtr processedSize)
        {
            try
            {
                int n = 0;
                if (size > 0)
                {
                    var s = S;
                    int want = (int)Math.Min(size, 1u << 20);
                    if (_buf.Length < want) _buf = new byte[want];
                    n = s.Read(_buf, 0, want);
                    if (n > 0) { Marshal.Copy(_buf, 0, data, n); _pos += n; }
                    else if (_reopen != null) { _s.Dispose(); _s = null; }
                }
                if (processedSize != IntPtr.Zero) Marshal.WriteInt32(processedSize, n);
                return HR.S_OK;
            }
            catch (Exception ex)
            {
                Error = ex;
                if (processedSize != IntPtr.Zero) Marshal.WriteInt32(processedSize, 0);
                return HR.E_FAIL;
            }
        }

        public int Seek(long offset, uint seekOrigin, IntPtr newPosition)
        {
            try
            {
                long p = S.Seek(offset, (SeekOrigin)seekOrigin);
                _pos = p;
                if (newPosition != IntPtr.Zero) Marshal.WriteInt64(newPosition, p);
                return HR.S_OK;
            }
            catch (Exception ex) { Error = ex; return HR.E_FAIL; }
        }

        public int GetSize(IntPtr size)
        {
            try
            {
                if (size != IntPtr.Zero) Marshal.WriteInt64(size, S.Length);
                return HR.S_OK;
            }
            catch { return HR.E_NOTIMPL; }
        }

        public void Dispose()
        {
            _disposed = true;
            var s = _s;
            _s = null;
            if (_ownsStream) s?.Dispose();
        }
    }

    /// <summary>Exposes a .NET stream to 7-Zip as IOutStream.</summary>
    [ComVisible(true)]
    internal sealed class OutStreamWrapper : IOutStream, ISequentialOutStream, IDisposable
    {
        private Stream _s;
        private byte[] _buf = new byte[0];
        public Exception Error;
        public long BytesWritten;

        public OutStreamWrapper(Stream s) { _s = s; }

        public int Write(IntPtr data, uint size, IntPtr processedSize)
        {
            try
            {
                int n = (int)size;
                if (n > 0)
                {
                    if (_buf.Length < n) _buf = new byte[Math.Max(n, 1 << 16)];
                    Marshal.Copy(data, _buf, 0, n);
                    _s.Write(_buf, 0, n);
                    BytesWritten += n;
                }
                if (processedSize != IntPtr.Zero) Marshal.WriteInt32(processedSize, n);
                return HR.S_OK;
            }
            catch (Exception ex)
            {
                Error = ex;
                if (processedSize != IntPtr.Zero) Marshal.WriteInt32(processedSize, 0);
                return HR.E_FAIL;
            }
        }

        public int Seek(long offset, uint seekOrigin, IntPtr newPosition)
        {
            try
            {
                long p = _s.Seek(offset, (SeekOrigin)seekOrigin);
                if (newPosition != IntPtr.Zero) Marshal.WriteInt64(newPosition, p);
                return HR.S_OK;
            }
            catch (Exception ex) { Error = ex; return HR.E_FAIL; }
        }

        public int SetSize(ulong newSize)
        {
            try { _s.SetLength((long)newSize); return HR.S_OK; }
            catch (Exception ex) { Error = ex; return HR.E_FAIL; }
        }

        public void Dispose() { _s?.Dispose(); _s = null; }
    }

    /// <summary>Read-only stream that concatenates split volumes (name.001, name.002, ...).</summary>
    internal sealed class ConcatReadStream : Stream
    {
        private readonly List<FileStream> _parts = new List<FileStream>();
        private readonly List<long> _starts = new List<long>();
        private long _length, _pos;

        public ConcatReadStream(IEnumerable<string> files)
        {
            foreach (var f in files)
            {
                var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
                _starts.Add(_length);
                _parts.Add(fs);
                _length += fs.Length;
            }
        }

        public int PartCount => _parts.Count;
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
            long p = origin == SeekOrigin.Begin ? offset : origin == SeekOrigin.Current ? _pos + offset : _length + offset;
            if (p < 0) throw new IOException("Seek before begin");
            _pos = p;
            return p;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_pos >= _length || count <= 0) return 0;
            int i = _starts.BinarySearch(_pos);
            if (i < 0) i = ~i - 1;
            while (i < _parts.Count - 1 && _starts[i + 1] <= _pos) i++;
            var part = _parts[i];
            long local = _pos - _starts[i];
            part.Position = local;
            int n = part.Read(buffer, offset, (int)Math.Min(count, part.Length - local));
            _pos += n;
            return n;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) foreach (var p in _parts) p.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>Seekable write stream that splits output into fixed-size volume files.</summary>
    internal sealed class VolumeWriteStream : Stream
    {
        private readonly string _baseName;
        private readonly long _volSize;
        private readonly List<FileStream> _vols = new List<FileStream>();
        private long _pos, _length;
        public readonly List<string> CreatedFiles = new List<string>();

        public VolumeWriteStream(string baseName, long volumeSize)
        {
            _baseName = baseName;
            _volSize = Math.Max(volumeSize, 64 * 1024);
        }

        public static string VolumeName(string baseName, int index) => baseName + "." + (index + 1).ToString("000");

        private FileStream Vol(int index)
        {
            while (_vols.Count <= index)
            {
                string name = VolumeName(_baseName, _vols.Count);
                var fs = new FileStream(name, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, 1 << 16);
                CreatedFiles.Add(name);
                _vols.Add(fs);
            }
            return _vols[index];
        }

        public override bool CanRead => false;
        public override bool CanSeek => true;
        public override bool CanWrite => true;
        public override long Length => _length;
        public override long Position { get => _pos; set => _pos = value; }
        public override void Flush() { foreach (var v in _vols) v.Flush(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin)
        {
            long p = origin == SeekOrigin.Begin ? offset : origin == SeekOrigin.Current ? _pos + offset : _length + offset;
            if (p < 0) throw new IOException("Seek before begin");
            _pos = p;
            return p;
        }

        public override void SetLength(long value)
        {
            _length = value;
            int last = value == 0 ? 0 : (int)((value - 1) / _volSize);
            for (int i = 0; i < _vols.Count; i++)
            {
                long start = i * _volSize;
                long len = Math.Max(0, Math.Min(_volSize, value - start));
                if (i <= last) _vols[i].SetLength(len);
            }
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            while (count > 0)
            {
                int vi = (int)(_pos / _volSize);
                long local = _pos % _volSize;
                var v = Vol(vi);
                if (v.Length < local) v.SetLength(local);
                v.Position = local;
                int n = (int)Math.Min(count, _volSize - local);
                v.Write(buffer, offset, n);
                offset += n; count -= n; _pos += n;
                if (_pos > _length) _length = _pos;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) foreach (var v in _vols) v.Dispose();
            base.Dispose(disposing);
        }
    }
}
