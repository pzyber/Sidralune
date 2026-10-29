using System;
using System.Threading.Tasks;

namespace Sidralune.Imaging
{
    public enum Interpolation { Nearest, Bilinear, Bicubic, Lanczos3 }

    /// <summary>2D projective transform (3x3 row-major); affine when the last row is (0,0,1). Maps source -> destination.</summary>
    public class Transform2D
    {
        public double[] M = { 1, 0, 0, 0, 1, 0, 0, 0, 1 };

        public static Transform2D Identity => new Transform2D();

        public static Transform2D Affine(double a, double b, double c, double d, double e, double f) =>
            new Transform2D { M = new[] { a, b, c, d, e, f, 0, 0, 1 } };

        public static Transform2D Translation(double dx, double dy) => Affine(1, 0, dx, 0, 1, dy);

        /// <summary>Rotation by angle (radians, counter-clockwise in a y-down image appears clockwise) about (cx, cy), with scale.</summary>
        public static Transform2D Rotation(double angle, double cx, double cy, double scale = 1)
        {
            double c = Math.Cos(angle) * scale, s = Math.Sin(angle) * scale;
            return Affine(c, -s, cx - c * cx + s * cy, s, c, cy - s * cx - c * cy);
        }

        public void Apply(double x, double y, out double u, out double v)
        {
            double w = M[6] * x + M[7] * y + M[8];
            u = (M[0] * x + M[1] * y + M[2]) / w;
            v = (M[3] * x + M[4] * y + M[5]) / w;
        }

        public Transform2D Inverse()
        {
            var m = M;
            double det = m[0] * (m[4] * m[8] - m[5] * m[7]) - m[1] * (m[3] * m[8] - m[5] * m[6]) + m[2] * (m[3] * m[7] - m[4] * m[6]);
            if (Math.Abs(det) < 1e-300) return Identity;
            var r = new double[9];
            r[0] = (m[4] * m[8] - m[5] * m[7]) / det; r[1] = (m[2] * m[7] - m[1] * m[8]) / det; r[2] = (m[1] * m[5] - m[2] * m[4]) / det;
            r[3] = (m[5] * m[6] - m[3] * m[8]) / det; r[4] = (m[0] * m[8] - m[2] * m[6]) / det; r[5] = (m[2] * m[3] - m[0] * m[5]) / det;
            r[6] = (m[3] * m[7] - m[4] * m[6]) / det; r[7] = (m[1] * m[6] - m[0] * m[7]) / det; r[8] = (m[0] * m[4] - m[1] * m[3]) / det;
            return new Transform2D { M = r };
        }

        /// <summary>this ∘ other (apply other first).</summary>
        public Transform2D Compose(Transform2D other)
        {
            var a = M; var b = other.M;
            var r = new double[9];
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                    r[i * 3 + j] = a[i * 3] * b[j] + a[i * 3 + 1] * b[3 + j] + a[i * 3 + 2] * b[6 + j];
            return new Transform2D { M = r };
        }

        public double Scale => Math.Sqrt(Math.Abs(M[0] * M[4] - M[1] * M[3]));
        public double RotationDeg => Math.Atan2(M[3], M[0]) * 180 / Math.PI;
        public double Dx => M[2];
        public double Dy => M[5];

        public override string ToString() => string.Format("dx={0:0.00} dy={1:0.00} rot={2:0.000}° scale={3:0.0000}", Dx, Dy, RotationDeg, Scale);
    }

    public static class Geometry
    {
        public static AstroImage Crop(AstroImage src, int x0, int y0, int w, int h)
        {
            x0 = Math.Max(0, x0); y0 = Math.Max(0, y0);
            w = Math.Min(w, src.Width - x0); h = Math.Min(h, src.Height - y0);
            var dst = new AstroImage(w, h, src.Channels) { Header = src.Header.Clone(), Bayer = Debayer.Shift(src.Bayer, x0, y0), SourcePath = src.SourcePath, OriginalBitDepth = src.OriginalBitDepth };
            for (int c = 0; c < src.Channels; c++)
                for (int y = 0; y < h; y++)
                    Array.Copy(src.Data[c], (y0 + y) * src.Width + x0, dst.Data[c], y * w, w);
            return dst;
        }

        public static AstroImage FlipHorizontal(AstroImage src)
        {
            var dst = src.CloneEmpty();
            int w = src.Width;
            for (int c = 0; c < src.Channels; c++)
                for (int y = 0; y < src.Height; y++)
                    for (int x = 0; x < w; x++) dst.Data[c][y * w + x] = src.Data[c][y * w + (w - 1 - x)];
            dst.Bayer = (w & 1) == 0 ? Debayer.FlipHorizontal(src.Bayer) : src.Bayer;
            return dst;
        }

        public static AstroImage FlipVertical(AstroImage src)
        {
            var dst = src.CloneEmpty();
            int w = src.Width, h = src.Height;
            for (int c = 0; c < src.Channels; c++)
                for (int y = 0; y < h; y++) Array.Copy(src.Data[c], (h - 1 - y) * w, dst.Data[c], y * w, w);
            dst.Bayer = (h & 1) == 0 ? Debayer.FlipVertical(src.Bayer) : src.Bayer;
            return dst;
        }

        /// <summary>Rotate by multiples of 90° (clockwise).</summary>
        public static AstroImage Rotate90(AstroImage src, int quarterTurns)
        {
            quarterTurns = ((quarterTurns % 4) + 4) % 4;
            if (quarterTurns == 0) return src.Clone();
            if (quarterTurns == 2) return FlipVertical(FlipHorizontal(src));
            int w = src.Width, h = src.Height;
            var dst = new AstroImage(h, w, src.Channels) { Header = src.Header.Clone(), OriginalBitDepth = src.OriginalBitDepth };
            for (int c = 0; c < src.Channels; c++)
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int nx = quarterTurns == 1 ? h - 1 - y : y;
                        int ny = quarterTurns == 1 ? x : w - 1 - x;
                        dst.Data[c][ny * h + nx] = src.Data[c][y * w + x];
                    }
            return dst;
        }

        /// <summary>Software binning (average) by an integer factor.</summary>
        public static AstroImage Bin(AstroImage src, int f, bool sum = false)
        {
            int w = src.Width / f, h = src.Height / f;
            var dst = new AstroImage(w, h, src.Channels) { Header = src.Header.Clone(), OriginalBitDepth = src.OriginalBitDepth };
            float norm = sum ? 1f : 1f / (f * f);
            for (int c = 0; c < src.Channels; c++)
            {
                var s = src.Data[c]; var d = dst.Data[c]; int W = src.Width;
                Parallel.For(0, h, Core.Util.StopRequest.Options, y =>
                {
                    for (int x = 0; x < w; x++)
                    {
                        float acc = 0;
                        for (int dy = 0; dy < f; dy++) for (int dx = 0; dx < f; dx++) acc += s[(y * f + dy) * W + x * f + dx];
                        d[y * w + x] = acc * norm;
                    }
                });
            }
            dst.Header.Set("XBINNING", src.Header.GetInt("XBINNING", 1) * f);
            dst.Header.Set("YBINNING", src.Header.GetInt("YBINNING", 1) * f);
            return dst;
        }

        public static AstroImage Resize(AstroImage src, int newW, int newH, Interpolation interp = Interpolation.Lanczos3)
        {
            double sx = (double)src.Width / newW, sy = (double)src.Height / newH;
            var t = Transform2D.Affine(1 / sx, 0, 0.5 / sx - 0.5, 0, 1 / sy, 0.5 / sy - 0.5);
            return Warp(src, t, newW, newH, interp);
        }

        private static double Lanczos(double x)
        {
            if (x == 0) return 1;
            if (x <= -3 || x >= 3) return 0;
            double px = Math.PI * x;
            return 3 * Math.Sin(px) * Math.Sin(px / 3) / (px * px);
        }

        private static double Cubic(double x)
        {
            const double a = -0.5;
            x = Math.Abs(x);
            if (x <= 1) return (a + 2) * x * x * x - (a + 3) * x * x + 1;
            if (x < 2) return a * x * x * x - 5 * a * x * x + 8 * a * x - 4 * a;
            return 0;
        }

        /// <summary>Sample one channel at (x, y) with the given kernel. Returns NaN outside the image.</summary>
        public static float SampleAt(float[] d, int w, int h, double x, double y, Interpolation interp)
        {
            if (x < -0.5 || y < -0.5 || x > w - 0.5 || y > h - 0.5) return float.NaN;
            switch (interp)
            {
                case Interpolation.Nearest:
                    {
                        int ix = (int)Math.Round(x), iy = (int)Math.Round(y);
                        ix = Math.Max(0, Math.Min(w - 1, ix)); iy = Math.Max(0, Math.Min(h - 1, iy));
                        return d[iy * w + ix];
                    }
                case Interpolation.Bilinear:
                    {
                        int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
                        double fx = x - x0, fy = y - y0;
                        int xa = Clamp(x0, w), xb = Clamp(x0 + 1, w), ya = Clamp(y0, h), yb = Clamp(y0 + 1, h);
                        return (float)(d[ya * w + xa] * (1 - fx) * (1 - fy) + d[ya * w + xb] * fx * (1 - fy) + d[yb * w + xa] * (1 - fx) * fy + d[yb * w + xb] * fx * fy);
                    }
                default:
                    {
                        int r = interp == Interpolation.Lanczos3 ? 3 : 2;
                        int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
                        double sum = 0, wsum = 0, vmin = double.MaxValue, vmax = double.MinValue;
                        for (int j = y0 - r + 1; j <= y0 + r; j++)
                        {
                            double wy = interp == Interpolation.Lanczos3 ? Lanczos(y - j) : Cubic(y - j);
                            if (wy == 0) continue;
                            int jj = Clamp(j, h);
                            for (int i = x0 - r + 1; i <= x0 + r; i++)
                            {
                                double wx = interp == Interpolation.Lanczos3 ? Lanczos(x - i) : Cubic(x - i);
                                if (wx == 0) continue;
                                double v = d[jj * w + Clamp(i, w)];
                                double ww = wx * wy;
                                sum += v * ww; wsum += ww;
                                if (Math.Abs(i - x) <= 1 && Math.Abs(j - y) <= 1) { if (v < vmin) vmin = v; if (v > vmax) vmax = v; }
                            }
                        }
                        double res = wsum != 0 ? sum / wsum : 0;
                        // clamp to local range to suppress ringing around stars (as in PixInsight's clamping threshold)
                        if (vmin <= vmax) res = Math.Max(vmin - 0.3 * (vmax - vmin), Math.Min(vmax + 0.3 * (vmax - vmin), res));
                        return (float)res;
                    }
            }
        }

        private static int Clamp(int v, int n) => v < 0 ? 0 : (v >= n ? n - 1 : v);

        /// <summary>
        /// Warp src into a new image of size (w, h) where 'srcToDst' maps source pixel coordinates to destination.
        /// Pixels with no source data become NaN (or 0 if fillZero).
        /// </summary>
        public static AstroImage Warp(AstroImage src, Transform2D srcToDst, int w, int h, Interpolation interp = Interpolation.Lanczos3, bool fillZero = true)
        {
            var inv = srcToDst.Inverse();
            var dst = new AstroImage(w, h, src.Channels) { Header = src.Header.Clone(), OriginalBitDepth = src.OriginalBitDepth };
            int sw = src.Width, sh = src.Height;
            for (int c = 0; c < src.Channels; c++)
            {
                var s = src.Data[c]; var d = dst.Data[c];
                Parallel.For(0, h, Core.Util.StopRequest.Options, y =>
                {
                    for (int x = 0; x < w; x++)
                    {
                        double u, v;
                        inv.Apply(x, y, out u, out v);
                        float val = SampleAt(s, sw, sh, u, v, interp);
                        d[y * w + x] = float.IsNaN(val) && fillZero ? 0 : val;
                    }
                });
            }
            return dst;
        }

        /// <summary>Arbitrary rotation (degrees, clockwise on screen) about the centre, expanding the canvas if requested.</summary>
        public static AstroImage Rotate(AstroImage src, double degrees, bool expand = false, Interpolation interp = Interpolation.Lanczos3)
        {
            double a = degrees * Math.PI / 180;
            int w = src.Width, h = src.Height;
            int nw = w, nh = h;
            if (expand)
            {
                nw = (int)Math.Ceiling(Math.Abs(w * Math.Cos(a)) + Math.Abs(h * Math.Sin(a)));
                nh = (int)Math.Ceiling(Math.Abs(w * Math.Sin(a)) + Math.Abs(h * Math.Cos(a)));
            }
            double cx = (w - 1) / 2.0, cy = (h - 1) / 2.0, ncx = (nw - 1) / 2.0, ncy = (nh - 1) / 2.0;
            var t = Transform2D.Translation(ncx, ncy).Compose(Transform2D.Rotation(a, 0, 0)).Compose(Transform2D.Translation(-cx, -cy));
            return Warp(src, t, nw, nh, interp);
        }

        /// <summary>Shift by a sub-pixel offset (positive dx moves content right).</summary>
        public static AstroImage Shift(AstroImage src, double dx, double dy, Interpolation interp = Interpolation.Bicubic) =>
            Warp(src, Transform2D.Translation(dx, dy), src.Width, src.Height, interp);
    }
}
