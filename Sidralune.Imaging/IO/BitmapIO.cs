using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace Sidralune.Imaging.IO
{
    /// <summary>8-bit bitmap import/export through System.Drawing (PNG, JPEG, BMP, GIF, 8-bit TIFF).</summary>
    public static class BitmapIO
    {
        public static AstroImage Read(string path)
        {
            using (var bmp = new Bitmap(path))
            {
                var img = FromBitmap(bmp);
                img.SourcePath = path;
                return img;
            }
        }

        public static unsafe AstroImage FromBitmap(Bitmap bmp)
        {
            int w = bmp.Width, h = bmp.Height;
            bool gray = bmp.PixelFormat == PixelFormat.Format8bppIndexed && IsGrayPalette(bmp.Palette);
            var img = new AstroImage(w, h, gray ? 1 : 3) { OriginalBitDepth = 8 };
            var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                for (int y = 0; y < h; y++)
                {
                    byte* row = (byte*)data.Scan0 + (long)y * data.Stride;
                    for (int x = 0; x < w; x++)
                    {
                        int i = y * w + x;
                        float b = row[x * 4] / 255f, g = row[x * 4 + 1] / 255f, r = row[x * 4 + 2] / 255f;
                        if (gray) img.Data[0][i] = g;
                        else { img.Data[0][i] = r; img.Data[1][i] = g; img.Data[2][i] = b; }
                    }
                }
            }
            finally { bmp.UnlockBits(data); }
            return img;
        }

        private static bool IsGrayPalette(ColorPalette p)
        {
            if (p.Entries.Length < 256) return false;
            for (int i = 0; i < 256; i += 17) { var c = p.Entries[i]; if (c.R != c.G || c.G != c.B) return false; }
            return true;
        }

        /// <summary>Render an image to a 32bpp bitmap applying a display transfer function per channel.</summary>
        public static unsafe Bitmap ToBitmap(AstroImage img, Func<int, float, float> transfer = null)
        {
            int w = img.Width, h = img.Height;
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                var lut = new byte[65536];
                System.Threading.Tasks.Parallel.For(0, h, Core.Util.StopRequest.Options, y =>
                {
                    byte* row = (byte*)data.Scan0 + (long)y * data.Stride;
                    for (int x = 0; x < w; x++)
                    {
                        int i = y * w + x;
                        float r = img.Data[0][i];
                        float g = img.Channels > 1 ? img.Data[1][i] : r;
                        float b = img.Channels > 2 ? img.Data[2][i] : r;
                        if (transfer != null) { r = transfer(0, r); g = transfer(img.Channels > 1 ? 1 : 0, g); b = transfer(img.Channels > 2 ? 2 : 0, b); }
                        row[x * 4] = B(b); row[x * 4 + 1] = B(g); row[x * 4 + 2] = B(r); row[x * 4 + 3] = 255;
                    }
                });
            }
            finally { bmp.UnlockBits(data); }
            return bmp;
        }

        private static byte B(float v)
        {
            if (float.IsNaN(v) || v <= 0) return 0;
            if (v >= 1) return 255;
            return (byte)(v * 255 + 0.5f);
        }

        public static void Write(string path, AstroImage img, Func<int, float, float> transfer = null, int jpegQuality = 92)
        {
            using (var bmp = ToBitmap(img, transfer))
            {
                string ext = Path.GetExtension(path).ToLowerInvariant();
                if (ext == ".jpg" || ext == ".jpeg")
                {
                    var enc = Array.Find(ImageCodecInfo.GetImageEncoders(), c => c.FormatID == ImageFormat.Jpeg.Guid);
                    var p = new EncoderParameters(1);
                    p.Param[0] = new EncoderParameter(Encoder.Quality, (long)jpegQuality);
                    bmp.Save(path, enc, p);
                }
                else if (ext == ".bmp") bmp.Save(path, ImageFormat.Bmp);
                else if (ext == ".gif") bmp.Save(path, ImageFormat.Gif);
                else bmp.Save(path, ImageFormat.Png);
            }
        }
    }

    public enum ImageSaveFormat { Fits16, Fits32Float, Xisf32Float, Xisf16, Tiff16, Tiff32Float, Png8, Jpeg8 }

    /// <summary>Format-agnostic image loading/saving by file extension.</summary>
    public static class ImageFile
    {
        public static readonly string[] FitsExt = { ".fit", ".fits", ".fts", ".fz" };
        public static readonly string[] RawExt = { ".dng", ".crw", ".cr2", ".cr3" };
        public const string OpenFilter = "All supported|*.fit;*.fits;*.fts;*.xisf;*.tif;*.tiff;*.png;*.jpg;*.jpeg;*.bmp;*.dng;*.crw;*.cr2;*.cr3;*.ser;*.avi|FITS|*.fit;*.fits;*.fts|XISF|*.xisf|TIFF|*.tif;*.tiff|Camera RAW (DNG, CRW, CR2, CR3)|*.dng;*.crw;*.cr2;*.cr3|Bitmap|*.png;*.jpg;*.jpeg;*.bmp|Video (SER, AVI)|*.ser;*.avi|All files|*.*";
        public const string SaveFilter = "FITS 32-bit float|*.fits|FITS 16-bit|*.fits|XISF 32-bit float|*.xisf|TIFF 16-bit|*.tif|TIFF 32-bit float|*.tif|PNG 8-bit (stretched)|*.png|JPEG 8-bit (stretched)|*.jpg";

        public static bool IsSupported(string path)
        {
            string e = Path.GetExtension(path).ToLowerInvariant();
            return Array.IndexOf(FitsExt, e) >= 0 || e == ".xisf" || e == ".tif" || e == ".tiff" || e == ".png" || e == ".jpg" || e == ".jpeg" || e == ".bmp" || Array.IndexOf(RawExt, e) >= 0 || e == ".ser" || e == ".avi";
        }

        public static AstroImage Load(string path)
        {
            string e = Path.GetExtension(path).ToLowerInvariant();
            AstroImage img;
            if (Array.IndexOf(FitsExt, e) >= 0) img = FitsIO.Read(path);
            else if (e == ".xisf") img = XisfIO.Read(path);
            else if (e == ".tif" || e == ".tiff" || e == ".dng" || e == ".cr2") img = TiffIO.Read(path);
            else if (e == ".cr3") img = Cr3IO.Read(path);
            else if (e == ".crw") img = CrwIO.Read(path);
            else if (e == ".ser" || e == ".avi")
            {
                using (var src = FrameSourceFactory.Open(path)) img = src.ReadFrame(0);
            }
            else img = BitmapIO.Read(path);
            img.SourcePath = path;
            return img;
        }

        public static void Save(string path, AstroImage img, ImageSaveFormat fmt, Func<int, float, float> transfer = null)
        {
            switch (fmt)
            {
                case ImageSaveFormat.Fits16: FitsIO.Write(path, img, 16); break;
                case ImageSaveFormat.Fits32Float: FitsIO.Write(path, img, -32); break;
                case ImageSaveFormat.Xisf32Float: XisfIO.Write(path, img, true); break;
                case ImageSaveFormat.Xisf16: XisfIO.Write(path, img, false); break;
                case ImageSaveFormat.Tiff16: TiffIO.Write(path, img, false); break;
                case ImageSaveFormat.Tiff32Float: TiffIO.Write(path, img, true); break;
                default: BitmapIO.Write(path, img, transfer); break;
            }
        }

        public static ImageSaveFormat FormatFromFilterIndex(int idx)
        {
            switch (idx)
            {
                case 1: return ImageSaveFormat.Fits32Float;
                case 2: return ImageSaveFormat.Fits16;
                case 3: return ImageSaveFormat.Xisf32Float;
                case 4: return ImageSaveFormat.Tiff16;
                case 5: return ImageSaveFormat.Tiff32Float;
                case 6: return ImageSaveFormat.Png8;
                default: return ImageSaveFormat.Jpeg8;
            }
        }

        public static ImageSaveFormat FormatFromExtension(string path)
        {
            string e = Path.GetExtension(path).ToLowerInvariant();
            if (Array.IndexOf(FitsExt, e) >= 0) return ImageSaveFormat.Fits32Float;
            if (e == ".xisf") return ImageSaveFormat.Xisf32Float;
            if (e == ".tif" || e == ".tiff") return ImageSaveFormat.Tiff16;
            if (e == ".jpg" || e == ".jpeg") return ImageSaveFormat.Jpeg8;
            return ImageSaveFormat.Png8;
        }
    }
}
