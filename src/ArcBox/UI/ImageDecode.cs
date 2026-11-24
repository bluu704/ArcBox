using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace ArcBox.UI
{
    /// <summary>Decodes images from memory: GDI+ for the classic formats, Windows Imaging Component (WIC) for
    /// WebP / HEIC / AVIF / JPEG XR and anything GDI+ cannot read.</summary>
    public static class ImageDecode
    {
        public static readonly string[] Extensions =
            { ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".gif", ".bmp", ".dib", ".tif", ".tiff", ".ico", ".webp", ".heic", ".heif", ".avif", ".jxr", ".wdp" };

        private static readonly string[] WicFirst = { ".webp", ".heic", ".heif", ".avif", ".jxr", ".wdp" };

        /// <summary>Diagnostics for /selftest-img: reports which decoders work on this machine.</summary>
        public static string Diagnose(string file)
        {
            var data = File.ReadAllBytes(file);
            var sb = new System.Text.StringBuilder();
            try
            {
                using (var img = Image.FromStream(new MemoryStream(data)))
                {
                    sb.Append("GDI+: " + img.Width + "x" + img.Height + " orientation=" + JpegOrientation(data));
                }
            }
            catch (Exception ex) { sb.Append("GDI+: fail " + ex.Message); }
            try { using (var b = DecodeWic(data)) sb.Append(" | WIC: " + b.Width + "x" + b.Height + " px(10,10)=" + b.GetPixel(10, 10)); }
            catch (Exception ex) { sb.Append(" | WIC: fail " + ex.GetType().Name + " " + ex.Message); }
            try { using (var d = Decode(data, file)) sb.Append(" | Decode: " + d.Width + "x" + d.Height); }
            catch (Exception ex) { sb.Append(" | Decode: fail " + ex.Message.Split('\n')[0]); }
            return sb.ToString();
        }

        public static bool IsImage(string name) => Extensions.Contains(Path.GetExtension(name).ToLowerInvariant());

        /// <summary>Returns the decoded image, or throws with a user-friendly message.</summary>
        public static Image Decode(byte[] data, string name)
        {
            string ext = Path.GetExtension(name).ToLowerInvariant();
            Exception gdiError = null;
            if (!WicFirst.Contains(ext))
            {
                try
                {
                    // GDI+ needs the stream to stay alive for the image's lifetime (GIF animation)
                    var img = Image.FromStream(new MemoryStream(data), true, true);
                    Orient(img, JpegOrientation(data));
                    return img;
                }
                catch (Exception ex) { gdiError = ex; }
            }
            try { var b = DecodeWic(data); Orient(b, JpegOrientation(data)); return b; }
            catch (Exception ex)
            {
                if (ext == ".webp" || ext == ".heic" || ext == ".heif" || ext == ".avif")
                    throw new InvalidOperationException("이 이미지 형식(" + ext.TrimStart('.').ToUpperInvariant() + ")을 표시할 코덱이 Windows에 없습니다.\n" +
                        "Microsoft Store에서 '" + (ext == ".webp" ? "WebP 이미지 확장" : ext == ".avif" ? "AV1 비디오 확장" : "HEIF 이미지 확장") + "'을 설치하면 볼 수 있습니다.", ex);
                throw new InvalidOperationException("이미지를 읽을 수 없습니다: " + (gdiError ?? ex).Message, ex);
            }
        }

        // ---------------------------------------------------------------- WIC via COM (no WPF dependency)
        [ComImport, Guid("ec5ec8a9-c395-4314-9c77-54d7a935ff70"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IWICImagingFactory
        {
            void CreateDecoderFromFilename();
            [PreserveSig] int CreateDecoderFromStream(IntPtr stream, IntPtr vendor, int options, out IWICBitmapDecoder decoder);
            void CreateDecoderFromFileHandle();
            void CreateComponentInfo();
            void CreateDecoder();
            void CreateEncoder();
            void CreatePalette();
            [PreserveSig] int CreateFormatConverter(out IWICFormatConverter converter);
        }

        [ComImport, Guid("9EDDE9E7-8DEE-47ea-99DF-E6FAF2ED44BF"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IWICBitmapDecoder
        {
            void QueryCapability(); void Initialize(); void GetContainerFormat(); void GetDecoderInfo(); void CopyPalette();
            void GetMetadataQueryReader(); void GetPreview(); void GetColorContexts(); void GetThumbnail();
            [PreserveSig] int GetFrameCount(out uint count);
            [PreserveSig] int GetFrame(uint index, out IntPtr frame);
        }

        [ComImport, Guid("00000301-a8f2-4877-ba0a-fd2b6645fb94"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IWICFormatConverter
        {
            [PreserveSig] int GetSize(out uint w, out uint h);
            void GetPixelFormat(); void GetResolution(); void CopyPalette();
            [PreserveSig] int CopyPixels(IntPtr rect, uint stride, uint bufferSize, IntPtr buffer);
            [PreserveSig] int Initialize(IntPtr source, ref Guid dstFormat, int dither, IntPtr palette, double alphaThreshold, int paletteTranslate);
        }

        [DllImport("shlwapi.dll")] private static extern IntPtr SHCreateMemStream(byte[] data, uint size);

        private static readonly Guid ClsidWicFactory = new Guid("cacaf262-9370-4615-a13b-9f5539da4c0a");
        private static Guid Bgra32 = new Guid("6fddc324-4e03-4bfe-b185-3d77768dc90f");

        internal static Bitmap DecodeWic(byte[] data)
        {
            var factory = (IWICImagingFactory)Activator.CreateInstance(Type.GetTypeFromCLSID(ClsidWicFactory));
            IntPtr stream = SHCreateMemStream(data, (uint)data.Length);
            if (stream == IntPtr.Zero) throw new InvalidOperationException("메모리가 부족합니다.");
            IWICBitmapDecoder decoder = null;
            IWICFormatConverter conv = null;
            IntPtr frame = IntPtr.Zero;
            try
            {
                int hr = factory.CreateDecoderFromStream(stream, IntPtr.Zero, 1, out decoder);
                if (hr != 0 || decoder == null) throw new InvalidOperationException("이미지 코덱을 찾을 수 없습니다 (0x" + hr.ToString("X8") + ")");
                hr = decoder.GetFrame(0, out frame);
                if (hr != 0) throw new InvalidOperationException("이미지를 읽을 수 없습니다 (0x" + hr.ToString("X8") + ")");
                hr = factory.CreateFormatConverter(out conv);
                if (hr != 0) throw new InvalidOperationException("이미지 변환기를 만들 수 없습니다");
                hr = conv.Initialize(frame, ref Bgra32, 0, IntPtr.Zero, 0, 0);
                if (hr != 0) throw new InvalidOperationException("이미지를 변환할 수 없습니다 (0x" + hr.ToString("X8") + ")");
                conv.GetSize(out uint w, out uint h);
                if (w == 0 || h == 0 || (long)w * h > 200_000_000) throw new InvalidOperationException("이미지가 너무 큽니다.");
                var bmp = new Bitmap((int)w, (int)h, PixelFormat.Format32bppArgb);
                var bd = bmp.LockBits(new Rectangle(0, 0, (int)w, (int)h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                try
                {
                    hr = conv.CopyPixels(IntPtr.Zero, (uint)bd.Stride, (uint)(bd.Stride * (int)h), bd.Scan0);
                    if (hr != 0) throw new InvalidOperationException("이미지를 읽을 수 없습니다 (0x" + hr.ToString("X8") + ")");
                }
                finally { bmp.UnlockBits(bd); }
                return bmp;
            }
            finally
            {
                if (conv != null) Marshal.ReleaseComObject(conv);
                if (frame != IntPtr.Zero) Marshal.Release(frame);
                if (decoder != null) Marshal.ReleaseComObject(decoder);
                Marshal.Release(stream);
                Marshal.ReleaseComObject(factory);
            }
        }

        /// <summary>Reads the EXIF orientation (tag 0x0112) straight from a JPEG's APP1 segment.</summary>
        internal static int JpegOrientation(byte[] d)
        {
            try
            {
                if (d.Length < 4 || d[0] != 0xFF || d[1] != 0xD8) return 1;
                int p = 2;
                while (p + 4 < d.Length && d[p] == 0xFF)
                {
                    int marker = d[p + 1], len = d[p + 2] << 8 | d[p + 3];
                    if (marker == 0xE1 && p + 10 < d.Length && d[p + 4] == 'E' && d[p + 5] == 'x' && d[p + 6] == 'i' && d[p + 7] == 'f')
                    {
                        int t = p + 10;
                        bool le = d[t] == 'I';
                        int U16(int o) => le ? d[o] | d[o + 1] << 8 : d[o] << 8 | d[o + 1];
                        int U32(int o) => le ? d[o] | d[o + 1] << 8 | d[o + 2] << 16 | d[o + 3] << 24 : d[o] << 24 | d[o + 1] << 16 | d[o + 2] << 8 | d[o + 3];
                        int ifd = t + U32(t + 4);
                        int n = U16(ifd);
                        for (int i = 0; i < n; i++)
                        {
                            int e = ifd + 2 + i * 12;
                            if (U16(e) == 0x0112) return U16(e + 8);
                        }
                        return 1;
                    }
                    if (marker == 0xDA) break;
                    p += 2 + len;
                }
            }
            catch { }
            return 1;
        }

        private static void Orient(Image img, int o)
        {
            RotateFlipType t;
            switch (o)
            {
                case 2: t = RotateFlipType.RotateNoneFlipX; break;
                case 3: t = RotateFlipType.Rotate180FlipNone; break;
                case 4: t = RotateFlipType.Rotate180FlipX; break;
                case 5: t = RotateFlipType.Rotate90FlipX; break;
                case 6: t = RotateFlipType.Rotate90FlipNone; break;
                case 7: t = RotateFlipType.Rotate270FlipX; break;
                case 8: t = RotateFlipType.Rotate270FlipNone; break;
                default: return;
            }
            img.RotateFlip(t);
        }
    }
}
