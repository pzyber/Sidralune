using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Sidralune.Imaging.ColorManagement
{
    /// <summary>
    /// Converts image values from one profile to another. Between matrix/TRC profiles (every working space) it works
    /// in floating point and keeps values outside 0..1; otherwise (profiles with lookup tables) Windows does it at
    /// 16 bits per channel.
    /// </summary>
    public sealed class ColorTransform
    {
        public IccProfile From { get; }
        public IccProfile To { get; }
        public RenderingIntent Intent { get; }
        /// <summary>Done exactly by Sidralune in floating point (false: by Windows, clipped to 0..1).</summary>
        public bool IsExact { get; }

        private readonly double[,] _matrix; // linear source RGB → linear destination RGB

        private static readonly ConcurrentDictionary<string, ColorTransform> Cache = new ConcurrentDictionary<string, ColorTransform>();

        private ColorTransform(IccProfile from, IccProfile to, RenderingIntent intent)
        {
            From = from; To = to; Intent = intent;
            IsExact = from.IsSimple && to.IsSimple && from.IsGray == to.IsGray;
            if (IsExact && from.IsRgb)
            {
                // matrix/TRC profiles have no perceptual or saturation tables: those intents are colorimetric
                var m = to.Matrix != null ? Mat.Inverse(to.Matrix) : null;
                var scale = new double[3, 3];
                for (int i = 0; i < 3; i++)
                    scale[i, i] = intent == RenderingIntent.AbsoluteColorimetric ? from.WhitePoint[i] / to.WhitePoint[i] : 1;
                _matrix = Mat.Mul(m, Mat.Mul(scale, from.Matrix));
            }
        }

        /// <summary>The transform between two profiles (cached).</summary>
        public static ColorTransform Create(IccProfile from, IccProfile to, RenderingIntent intent)
        {
            if (from == null || to == null) throw new ArgumentNullException(from == null ? "from" : "to");
            if (from.IsGray != to.IsGray) throw new ArgumentException("Both profiles must be RGB, or both grey");
            return Cache.GetOrAdd(from.Fingerprint + ">" + to.Fingerprint + ":" + (int)intent, k => new ColorTransform(from, to, intent));
        }

        /// <summary>Converts an image's values in place (its channel count must match the profiles).</summary>
        public void Apply(AstroImage img)
        {
            if (From.SameAs(To) && Intent != RenderingIntent.AbsoluteColorimetric) return;
            bool rgb = From.IsRgb;
            if (rgb ? img.Channels < 3 : img.Channels != 1) throw new ArgumentException("The image does not have the profile's channels");
            int w = img.Width, h = img.Height;
            if (!IsExact)
            {
                ApplyWithWindows(img);
                return;
            }
            var d = img.Data;
            Parallel.For(0, h, Core.Util.StopRequest.Options, y =>
            {
                int o = y * w;
                if (!rgb)
                {
                    var c = d[0]; var fc = From.Curves[0]; var tc = To.Curves[0];
                    for (int x = 0; x < w; x++) c[o + x] = (float)tc.FromLinear(fc.ToLinear(c[o + x]));
                    return;
                }
                var p = new double[3];
                for (int x = 0; x < w; x++)
                {
                    int i = o + x;
                    p[0] = d[0][i]; p[1] = d[1][i]; p[2] = d[2][i];
                    Pixel(p);
                    d[0][i] = (float)p[0]; d[1][i] = (float)p[1]; d[2][i] = (float)p[2];
                }
            });
        }

        /// <summary>Converts one RGB value (exact transforms only).</summary>
        public void Pixel(double[] p)
        {
            var fc = From.Curves; var tc = To.Curves; var m = _matrix;
            double r = fc[0].ToLinear(p[0]), g = fc[1].ToLinear(p[1]), b = fc[2].ToLinear(p[2]);
            p[0] = tc[0].FromLinear(m[0, 0] * r + m[0, 1] * g + m[0, 2] * b);
            p[1] = tc[1].FromLinear(m[1, 0] * r + m[1, 1] * g + m[1, 2] * b);
            p[2] = tc[2].FromLinear(m[2, 0] * r + m[2, 1] * g + m[2, 2] * b);
        }

        private void ApplyWithWindows(AstroImage img)
        {
            using (var t = new WcsTransform(new[] { From, To }, new[] { Intent, Intent }, false))
            {
                int w = img.Width, h = img.Height;
                bool rgb = From.IsRgb;
                const int strip = 64;
                for (int y0 = 0; y0 < h; y0 += strip)
                {
                    int rows = Math.Min(strip, h - y0), n = rows * w;
                    var buf = new ushort[n * (rgb ? 3 : 1)];
                    for (int i = 0; i < n; i++)
                    {
                        int k = y0 * w + i;
                        // Windows' 16-bit RGB layout is blue, green, red
                        if (rgb) { buf[i * 3] = U16(img.Data[2][k]); buf[i * 3 + 1] = U16(img.Data[1][k]); buf[i * 3 + 2] = U16(img.Data[0][k]); }
                        else buf[i] = U16(img.Data[0][k]);
                    }
                    var outBuf = t.Translate16(buf, w, rows, rgb);
                    for (int i = 0; i < n; i++)
                    {
                        int k = y0 * w + i;
                        if (rgb) { img.Data[2][k] = outBuf[i * 3] / 65535f; img.Data[1][k] = outBuf[i * 3 + 1] / 65535f; img.Data[0][k] = outBuf[i * 3 + 2] / 65535f; }
                        else img.Data[0][k] = outBuf[i] / 65535f;
                    }
                }
            }
        }

        /// <summary>A 0..1 value as 16 bits (clipped).</summary>
        public static ushort U16(float v) => float.IsNaN(v) || v <= 0 ? (ushort)0 : v >= 1 ? (ushort)65535 : (ushort)(v * 65535 + 0.5f);
    }

    /// <summary>
    /// A colour transform through the Windows Color System (mscms): any ICC profiles, including lookup tables, the
    /// monitor's, proofing chains and gamut checks. Pixels are 16-bit blue, green, red in and out, or 8-bit
    /// blue, green, red, unused out (the layout of 32-bit Windows bitmaps).
    /// </summary>
    public sealed class WcsTransform : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)] private struct PROFILE { public int dwType; public IntPtr pProfileData; public int cbDataSize; }
        [DllImport("mscms.dll", SetLastError = true)] private static extern IntPtr OpenColorProfileW(ref PROFILE p, int access, int share, int creation);
        [DllImport("mscms.dll", SetLastError = true)] private static extern bool CloseColorProfile(IntPtr h);
        [DllImport("mscms.dll", SetLastError = true)] private static extern IntPtr CreateMultiProfileTransform(IntPtr[] profiles, int n, int[] intents, int nIntents, int flags, int cmm);
        [DllImport("mscms.dll", SetLastError = true)] private static extern bool DeleteColorTransform(IntPtr t);
        [DllImport("mscms.dll", SetLastError = true)] private static extern bool TranslateBitmapBits(IntPtr t, IntPtr src, int inFmt, int w, int h, int inStride, IntPtr dst, int outFmt, int outStride, IntPtr cb, IntPtr cbData);
        [DllImport("mscms.dll", SetLastError = true)] private static extern bool CheckBitmapBits(IntPtr t, IntPtr src, int inFmt, int w, int h, int stride, byte[] result, IntPtr cb, IntPtr cbData);

        private const int BM_16b_RGB = 0x000A, BM_16b_GRAY = 0x0505, BM_xRGBQUADS = 0x0008;
        private const int BEST_MODE = 3, ENABLE_GAMUT_CHECKING = 0x00010000;

        private IntPtr _handle;
        private readonly object _lock = new object();

        /// <param name="profiles">The chain: source, (proofing,) destination.</param>
        /// <param name="gamutCheck">Made for CheckGamut (against the last profile).</param>
        public WcsTransform(IList<IccProfile> profiles, IList<RenderingIntent> intents, bool gamutCheck)
        {
            var handles = new IntPtr[profiles.Count];
            var pins = new List<GCHandle>();
            try
            {
                for (int i = 0; i < profiles.Count; i++)
                {
                    var pin = GCHandle.Alloc(profiles[i].Bytes, GCHandleType.Pinned);
                    pins.Add(pin);
                    var p = new PROFILE { dwType = 2 /* PROFILE_MEMBUFFER */, pProfileData = pin.AddrOfPinnedObject(), cbDataSize = profiles[i].Bytes.Length };
                    handles[i] = OpenColorProfileW(ref p, 1 /* PROFILE_READ */, 1 /* FILE_SHARE_READ */, 3 /* OPEN_EXISTING */);
                    if (handles[i] == IntPtr.Zero) throw new InvalidOperationException("Windows could not open the colour profile \"" + profiles[i].Description + "\" (" + Marshal.GetLastWin32Error() + ")");
                }
                var ints = new int[intents.Count];
                for (int i = 0; i < ints.Length; i++) ints[i] = (int)intents[i];
                _handle = CreateMultiProfileTransform(handles, handles.Length, ints, ints.Length, BEST_MODE | (gamutCheck ? ENABLE_GAMUT_CHECKING : 0), 0);
                if (_handle == IntPtr.Zero) throw new InvalidOperationException("Windows could not create the colour transform (" + Marshal.GetLastWin32Error() + ")");
            }
            finally
            {
                // the transform does not need the profiles once made
                foreach (var h in handles) if (h != IntPtr.Zero) CloseColorProfile(h);
                foreach (var p in pins) p.Free();
            }
        }

        /// <summary>16-bit pixels (blue, green, red, or grey) to 16-bit pixels of the destination.</summary>
        public ushort[] Translate16(ushort[] src, int w, int h, bool rgb)
        {
            int ch = rgb ? 3 : 1;
            var dst = new ushort[src.Length];
            var a = GCHandle.Alloc(src, GCHandleType.Pinned);
            var b = GCHandle.Alloc(dst, GCHandleType.Pinned);
            try
            {
                lock (_lock)
                    if (!TranslateBitmapBits(_handle, a.AddrOfPinnedObject(), rgb ? BM_16b_RGB : BM_16b_GRAY, w, h, w * ch * 2, b.AddrOfPinnedObject(), rgb ? BM_16b_RGB : BM_16b_GRAY, w * ch * 2, IntPtr.Zero, IntPtr.Zero))
                        throw new InvalidOperationException("Windows could not convert the colours (" + Marshal.GetLastWin32Error() + ")");
            }
            finally { a.Free(); b.Free(); }
            return dst;
        }

        /// <summary>Pixel layouts Windows converts between (16-bit RGB is blue, green, red).</summary>
        public enum Format { Rgb16 = BM_16b_RGB, Gray16 = BM_16b_GRAY, Cmyk8 = 0x0020 /* BM_CMYKQUADS */ }

        /// <summary>The layout of a profile's own values (for the middle step of a proof).</summary>
        public static Format FormatFor(IccProfile p) => p.IsGray ? Format.Gray16 : p.ColorSpace == "CMYK" ? Format.Cmyk8 : Format.Rgb16;

        private static int Bytes(Format f, int w) => f == Format.Rgb16 ? w * 6 : f == Format.Gray16 ? w * 2 : w * 4;

        /// <summary>Converts a buffer of pixels from one layout to another (16-bit buffers as ushort[], 8-bit as byte[]).</summary>
        public Array Translate(Array src, Format inFmt, int w, int h, Format outFmt)
        {
            Array dst = outFmt == Format.Cmyk8 ? (Array)new byte[w * h * 4] : new ushort[Bytes(outFmt, w) / 2 * h];
            var a = GCHandle.Alloc(src, GCHandleType.Pinned);
            var b = GCHandle.Alloc(dst, GCHandleType.Pinned);
            try
            {
                lock (_lock)
                    if (!TranslateBitmapBits(_handle, a.AddrOfPinnedObject(), (int)inFmt, w, h, Bytes(inFmt, w), b.AddrOfPinnedObject(), (int)outFmt, Bytes(outFmt, w), IntPtr.Zero, IntPtr.Zero))
                        throw new InvalidOperationException("Windows could not convert the colours (" + Marshal.GetLastWin32Error() + ")");
            }
            finally { a.Free(); b.Free(); }
            return dst;
        }

        /// <summary>16-bit blue, green, red pixels to 32-bit bitmap rows (blue, green, red, alpha set to 255).</summary>
        public void TranslateToBitmap(ushort[] src, int w, int h, IntPtr dst, int dstStride) => TranslateToBitmap(src, Format.Rgb16, w, h, dst, dstStride);

        /// <summary>Pixels in any layout to 32-bit bitmap rows (blue, green, red, alpha set to 255).</summary>
        public unsafe void TranslateToBitmap(Array src, Format inFmt, int w, int h, IntPtr dst, int dstStride)
        {
            var a = GCHandle.Alloc(src, GCHandleType.Pinned);
            try
            {
                lock (_lock)
                    if (!TranslateBitmapBits(_handle, a.AddrOfPinnedObject(), (int)inFmt, w, h, Bytes(inFmt, w), dst, BM_xRGBQUADS, dstStride, IntPtr.Zero, IntPtr.Zero))
                        throw new InvalidOperationException("Windows could not convert the colours (" + Marshal.GetLastWin32Error() + ")");
            }
            finally { a.Free(); }
            for (int y = 0; y < h; y++)
            {
                byte* row = (byte*)dst + (long)y * dstStride;
                for (int x = 0; x < w; x++) row[x * 4 + 3] = 255;
            }
        }

        /// <summary>For each 16-bit blue, green, red pixel: true when it is outside the last profile's gamut.</summary>
        public bool[] CheckGamut(ushort[] src, int w, int h)
        {
            var result = new byte[w * h];
            var a = GCHandle.Alloc(src, GCHandleType.Pinned);
            try
            {
                lock (_lock)
                    if (!CheckBitmapBits(_handle, a.AddrOfPinnedObject(), BM_16b_RGB, w, h, w * 6, result, IntPtr.Zero, IntPtr.Zero))
                        throw new InvalidOperationException("Windows could not check the gamut (" + Marshal.GetLastWin32Error() + ")");
            }
            finally { a.Free(); }
            var o = new bool[result.Length];
            for (int i = 0; i < o.Length; i++) o[i] = result[i] != 0;
            return o;
        }

        public void Dispose()
        {
            lock (_lock)
                if (_handle != IntPtr.Zero) { DeleteColorTransform(_handle); _handle = IntPtr.Zero; }
        }
    }
}
