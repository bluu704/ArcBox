using System;
using System.Runtime.InteropServices;

namespace ArcBox.Core
{
    internal static class HR
    {
        public const int S_OK = 0;
        public const int S_FALSE = 1;
        public const int E_NOTIMPL = unchecked((int)0x80004001);
        public const int E_ABORT = unchecked((int)0x80004004);
        public const int E_FAIL = unchecked((int)0x80004005);
        public const int E_OUTOFMEMORY = unchecked((int)0x8007000E);
        public const int E_INVALIDARG = unchecked((int)0x80070057);
    }

    /// <summary>7-Zip item / archive property ids (PropID.h).</summary>
    internal static class Kpid
    {
        public const uint Path = 3, Name = 4, Extension = 5, IsDir = 6, Size = 7, PackSize = 8, Attrib = 9,
            CTime = 10, ATime = 11, MTime = 12, Solid = 13, Commented = 14, Encrypted = 15, SplitBefore = 16,
            SplitAfter = 17, DictionarySize = 18, CRC = 19, Type = 20, IsAnti = 21, Method = 22, HostOS = 23,
            Comment = 28, NumBlocks = 38, NumVolumes = 39, PhySize = 44, HeadersSize = 45, ErrorFlags = 71,
            WarningFlags = 72, Warning = 73;
    }

    /// <summary>NArchive::NHandlerPropID</summary>
    internal static class HandlerProp
    {
        public const uint Name = 0, ClassID = 1, Extension = 2, AddExtension = 3, Update = 4, KeepName = 5,
            Signature = 6, MultiSignature = 7, SigOffset = 8, Flags = 11;
    }

    internal static class AskMode { public const int Extract = 0, Test = 1, Skip = 2; }

    internal static class OpRes
    {
        public const int OK = 0, UnsupportedMethod = 1, DataError = 2, CRCError = 3, Unavailable = 4,
            UnexpectedEnd = 5, DataAfterEnd = 6, IsNotArc = 7, HeadersError = 8, WrongPassword = 9;

        public static string Describe(int r, bool encrypted)
        {
            switch (r)
            {
                case UnsupportedMethod: return "지원하지 않는 압축 방식";
                case DataError: return encrypted ? "데이터 오류 (암호가 틀렸을 수 있음)" : "데이터 오류 (파일 손상)";
                case CRCError: return encrypted ? "CRC 오류 (암호가 틀렸을 수 있음)" : "CRC 오류 (파일 손상)";
                case Unavailable: return "데이터를 사용할 수 없음 (분할 파일 누락?)";
                case UnexpectedEnd: return "예상치 못한 파일 끝 (파일이 잘렸음)";
                case DataAfterEnd: return "데이터 끝 뒤에 추가 데이터가 있음";
                case IsNotArc: return "압축 파일이 아님";
                case HeadersError: return "헤더 오류";
                case WrongPassword: return "암호가 틀렸습니다";
                default: return "알 수 없는 오류 (" + r + ")";
            }
        }
    }

    // ---------------------------------------------------------------- streams
    [ComImport, Guid("23170F69-40C1-278A-0000-000300010000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ISequentialInStream
    {
        [PreserveSig] int Read(IntPtr data, uint size, IntPtr processedSize);
    }

    [ComImport, Guid("23170F69-40C1-278A-0000-000300020000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ISequentialOutStream
    {
        [PreserveSig] int Write(IntPtr data, uint size, IntPtr processedSize);
    }

    [ComImport, Guid("23170F69-40C1-278A-0000-000300030000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IInStream
    {
        [PreserveSig] int Read(IntPtr data, uint size, IntPtr processedSize);
        [PreserveSig] int Seek(long offset, uint seekOrigin, IntPtr newPosition);
    }

    [ComImport, Guid("23170F69-40C1-278A-0000-000300040000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IOutStream
    {
        [PreserveSig] int Write(IntPtr data, uint size, IntPtr processedSize);
        [PreserveSig] int Seek(long offset, uint seekOrigin, IntPtr newPosition);
        [PreserveSig] int SetSize(ulong newSize);
    }

    [ComImport, Guid("23170F69-40C1-278A-0000-000300060000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IStreamGetSize
    {
        [PreserveSig] int GetSize(IntPtr size);
    }

    // ---------------------------------------------------------------- password
    [ComImport, Guid("23170F69-40C1-278A-0000-000500100000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ICryptoGetTextPassword
    {
        [PreserveSig] int CryptoGetTextPassword([MarshalAs(UnmanagedType.BStr)] out string password);
    }

    [ComImport, Guid("23170F69-40C1-278A-0000-000500110000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ICryptoGetTextPassword2
    {
        [PreserveSig] int CryptoGetTextPassword2(out int passwordIsDefined, [MarshalAs(UnmanagedType.BStr)] out string password);
    }

    // ---------------------------------------------------------------- archive
    [ComImport, Guid("23170F69-40C1-278A-0000-000600100000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IArchiveOpenCallback
    {
        [PreserveSig] int SetTotal(IntPtr files, IntPtr bytes);
        [PreserveSig] int SetCompleted(IntPtr files, IntPtr bytes);
    }

    [ComImport, Guid("23170F69-40C1-278A-0000-000600300000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IArchiveOpenVolumeCallback
    {
        [PreserveSig] int GetProperty(uint propID, IntPtr value);
        [PreserveSig] int GetStream([MarshalAs(UnmanagedType.LPWStr)] string name, out IInStream inStream);
    }

    [ComImport, Guid("23170F69-40C1-278A-0000-000600200000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IArchiveExtractCallback
    {
        [PreserveSig] int SetTotal(ulong total);
        [PreserveSig] int SetCompleted(IntPtr completeValue);
        [PreserveSig] int GetStream(uint index, out ISequentialOutStream outStream, int askExtractMode);
        [PreserveSig] int PrepareOperation(int askExtractMode);
        [PreserveSig] int SetOperationResult(int opRes);
    }

    [ComImport, Guid("23170F69-40C1-278A-0000-000600600000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IInArchive
    {
        [PreserveSig] int Open(IInStream stream, IntPtr maxCheckStartPosition, IArchiveOpenCallback openCallback);
        [PreserveSig] int Close();
        [PreserveSig] int GetNumberOfItems(out uint numItems);
        [PreserveSig] int GetProperty(uint index, uint propID, IntPtr value);
        [PreserveSig] int Extract([MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] uint[] indices, uint numItems, int testMode, IArchiveExtractCallback extractCallback);
        [PreserveSig] int GetArchiveProperty(uint propID, IntPtr value);
        [PreserveSig] int GetNumberOfProperties(out uint numProperties);
        [PreserveSig] int GetPropertyInfo(uint index, IntPtr name, IntPtr propID, IntPtr varType);
        [PreserveSig] int GetNumberOfArchiveProperties(out uint numProperties);
        [PreserveSig] int GetArchivePropertyInfo(uint index, IntPtr name, IntPtr propID, IntPtr varType);
    }

    [ComImport, Guid("23170F69-40C1-278A-0000-000600800000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IArchiveUpdateCallback
    {
        [PreserveSig] int SetTotal(ulong total);
        [PreserveSig] int SetCompleted(IntPtr completeValue);
        [PreserveSig] int GetUpdateItemInfo(uint index, IntPtr newData, IntPtr newProperties, IntPtr indexInArchive);
        [PreserveSig] int GetProperty(uint index, uint propID, IntPtr value);
        [PreserveSig] int GetStream(uint index, out ISequentialInStream inStream);
        [PreserveSig] int SetOperationResult(int operationResult);
    }

    [ComImport, Guid("23170F69-40C1-278A-0000-000600A00000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IOutArchive
    {
        [PreserveSig] int UpdateItems(ISequentialOutStream outStream, uint numItems, IArchiveUpdateCallback updateCallback);
        [PreserveSig] int GetFileTimeType(out uint type);
    }

    [ComImport, Guid("23170F69-40C1-278A-0000-000600030000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ISetProperties
    {
        [PreserveSig] int SetProperties([MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr, SizeParamIndex = 2)] string[] names, IntPtr values, uint numProps);
    }

    /// <summary>
    /// Manual PROPVARIANT access. PROPVARIANT is 16 bytes on x86 and 24 bytes on x64, and 7-Zip copies the
    /// whole struct, so we never marshal it as a managed struct; we always go through raw memory.
    /// </summary>
    internal static class PropVar
    {
        public const ushort VT_EMPTY = 0, VT_I2 = 2, VT_I4 = 3, VT_BSTR = 8, VT_BOOL = 11, VT_UI1 = 17, VT_UI2 = 18,
            VT_UI4 = 19, VT_I8 = 20, VT_UI8 = 21, VT_INT = 22, VT_UINT = 23, VT_FILETIME = 64;

        public static readonly int Size = IntPtr.Size == 8 ? 24 : 16;

        [DllImport("ole32.dll")] private static extern int PropVariantClear(IntPtr pvar);
        [DllImport("oleaut32.dll")] private static extern uint SysStringByteLen(IntPtr bstr);

        public static IntPtr Alloc()
        {
            IntPtr p = Marshal.AllocHGlobal(Size);
            for (int i = 0; i < Size; i += 4) Marshal.WriteInt32(p, i, 0);
            return p;
        }

        public static void Free(IntPtr p)
        {
            if (p == IntPtr.Zero) return;
            try { PropVariantClear(p); } catch { }
            Marshal.FreeHGlobal(p);
        }

        public static void Clear(IntPtr p)
        {
            try { PropVariantClear(p); } catch { }
            for (int i = 0; i < Size; i += 4) Marshal.WriteInt32(p, i, 0);
        }

        public static ushort Vt(IntPtr p) => (ushort)Marshal.ReadInt16(p);

        public static object Read(IntPtr p)
        {
            switch (Vt(p))
            {
                case VT_EMPTY: return null;
                case VT_BSTR:
                    IntPtr b = Marshal.ReadIntPtr(p, 8);
                    return b == IntPtr.Zero ? null : Marshal.PtrToStringBSTR(b);
                case VT_BOOL: return Marshal.ReadInt16(p, 8) != 0;
                case VT_UI1: return (uint)Marshal.ReadByte(p, 8);
                case VT_UI2: return (uint)(ushort)Marshal.ReadInt16(p, 8);
                case VT_I2: return (long)Marshal.ReadInt16(p, 8);
                case VT_UI4: case VT_UINT: return (uint)Marshal.ReadInt32(p, 8);
                case VT_I4: case VT_INT: return (long)Marshal.ReadInt32(p, 8);
                case VT_UI8: return (ulong)Marshal.ReadInt64(p, 8);
                case VT_I8: return Marshal.ReadInt64(p, 8);
                case VT_FILETIME:
                    long ft = Marshal.ReadInt64(p, 8);
                    if (ft <= 0) return null;
                    try { return DateTime.FromFileTimeUtc(ft); } catch { return null; }
                default: return null;
            }
        }

        public static byte[] ReadBstrBytes(IntPtr p)
        {
            if (Vt(p) != VT_BSTR) return null;
            IntPtr b = Marshal.ReadIntPtr(p, 8);
            if (b == IntPtr.Zero) return null;
            int len = (int)SysStringByteLen(b);
            var data = new byte[len];
            Marshal.Copy(b, data, 0, len);
            return data;
        }

        public static ulong? ToULong(object o)
        {
            switch (o)
            {
                case ulong u: return u;
                case uint u4: return u4;
                case long l when l >= 0: return (ulong)l;
                default: return null;
            }
        }

        // writers (used when 7-Zip asks us for a property): only touch the first 16 bytes
        public static void WriteEmpty(IntPtr p) { Marshal.WriteInt64(p, 0, 0); Marshal.WriteInt64(p, 8, 0); }
        private static void SetVt(IntPtr p, ushort vt) { Marshal.WriteInt64(p, 0, 0); Marshal.WriteInt16(p, 0, (short)vt); }
        public static void WriteBool(IntPtr p, bool v) { SetVt(p, VT_BOOL); Marshal.WriteInt64(p, 8, 0); Marshal.WriteInt16(p, 8, (short)(v ? -1 : 0)); }
        public static void WriteUInt32(IntPtr p, uint v) { SetVt(p, VT_UI4); Marshal.WriteInt64(p, 8, 0); Marshal.WriteInt32(p, 8, (int)v); }
        public static void WriteUInt64(IntPtr p, ulong v) { SetVt(p, VT_UI8); Marshal.WriteInt64(p, 8, (long)v); }
        public static void WriteString(IntPtr p, string s) { SetVt(p, VT_BSTR); Marshal.WriteInt64(p, 8, 0); Marshal.WriteIntPtr(p, 8, Marshal.StringToBSTR(s ?? "")); }
        public static void WriteFileTime(IntPtr p, DateTime utc)
        {
            long ft;
            try { ft = utc.ToFileTimeUtc(); } catch { ft = 0; }
            if (ft <= 0) { WriteEmpty(p); return; }
            SetVt(p, VT_FILETIME); Marshal.WriteInt64(p, 8, ft);
        }

        public static void WriteObject(IntPtr p, object v)
        {
            switch (v)
            {
                case null: WriteEmpty(p); break;
                case bool b: WriteBool(p, b); break;
                case uint u: WriteUInt32(p, u); break;
                case int i: WriteUInt32(p, (uint)i); break;
                case ulong ul: WriteUInt64(p, ul); break;
                case long l: WriteUInt64(p, (ulong)l); break;
                case DateTime d: WriteFileTime(p, d); break;
                default: WriteString(p, v.ToString()); break;
            }
        }
    }
}
