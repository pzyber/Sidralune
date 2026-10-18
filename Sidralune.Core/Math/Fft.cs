using System;
using System.Threading.Tasks;

namespace Sidralune.Core.Mathematics
{
    /// <summary>
    /// In-place complex FFT on split real/imag arrays. Radix-2 iterative for powers of two,
    /// Bluestein chirp-z for arbitrary lengths. 2D transforms are parallelised by rows/columns.
    /// </summary>
    public static class Fft
    {
        public static bool IsPowerOfTwo(int n) => n > 0 && (n & (n - 1)) == 0;

        public static int NextPowerOfTwo(int n)
        {
            int p = 1;
            while (p < n) p <<= 1;
            return p;
        }

        /// <summary>Forward (inverse=false) or inverse FFT. Inverse is scaled by 1/n.</summary>
        public static void Transform(double[] re, double[] im, bool inverse)
        {
            int n = re.Length;
            if (n <= 1) return;
            if (IsPowerOfTwo(n)) Radix2(re, im, inverse);
            else Bluestein(re, im, inverse);
            if (inverse)
            {
                double s = 1.0 / n;
                for (int i = 0; i < n; i++) { re[i] *= s; im[i] *= s; }
            }
        }

        private static void Radix2(double[] re, double[] im, bool inverse)
        {
            int n = re.Length;
            int levels = 0;
            for (int t = n; t > 1; t >>= 1) levels++;
            // bit reversal
            for (int i = 0; i < n; i++)
            {
                int j = ReverseBits(i, levels);
                if (j > i)
                {
                    double t = re[i]; re[i] = re[j]; re[j] = t;
                    t = im[i]; im[i] = im[j]; im[j] = t;
                }
            }
            double sign = inverse ? 1.0 : -1.0;
            for (int size = 2; size <= n; size <<= 1)
            {
                int half = size >> 1;
                double theta = sign * 2.0 * Math.PI / size;
                double wpr = Math.Cos(theta), wpi = Math.Sin(theta);
                for (int start = 0; start < n; start += size)
                {
                    double wr = 1.0, wi = 0.0;
                    for (int k = 0; k < half; k++)
                    {
                        int a = start + k, b = a + half;
                        double tr = wr * re[b] - wi * im[b];
                        double ti = wr * im[b] + wi * re[b];
                        re[b] = re[a] - tr; im[b] = im[a] - ti;
                        re[a] += tr; im[a] += ti;
                        double t = wr;
                        wr = t * wpr - wi * wpi;
                        wi = t * wpi + wi * wpr;
                    }
                }
            }
        }

        private static int ReverseBits(int x, int bits)
        {
            int y = 0;
            for (int i = 0; i < bits; i++) { y = (y << 1) | (x & 1); x >>= 1; }
            return y;
        }

        private static void Bluestein(double[] re, double[] im, bool inverse)
        {
            int n = re.Length;
            int m = NextPowerOfTwo(2 * n - 1);
            double sign = inverse ? 1.0 : -1.0;
            var cosT = new double[n];
            var sinT = new double[n];
            for (int i = 0; i < n; i++)
            {
                long j = (long)i * i % (2L * n);
                double ang = sign * Math.PI * j / n;
                cosT[i] = Math.Cos(ang);
                sinT[i] = Math.Sin(ang);
            }
            var ar = new double[m]; var ai = new double[m];
            var br = new double[m]; var bi = new double[m];
            for (int i = 0; i < n; i++)
            {
                ar[i] = re[i] * cosT[i] - im[i] * sinT[i];
                ai[i] = re[i] * sinT[i] + im[i] * cosT[i];
            }
            br[0] = cosT[0]; bi[0] = -sinT[0];
            for (int i = 1; i < n; i++)
            {
                br[i] = br[m - i] = cosT[i];
                bi[i] = bi[m - i] = -sinT[i];
            }
            Radix2(ar, ai, false);
            Radix2(br, bi, false);
            for (int i = 0; i < m; i++)
            {
                double r = ar[i] * br[i] - ai[i] * bi[i];
                double ii = ar[i] * bi[i] + ai[i] * br[i];
                ar[i] = r; ai[i] = ii;
            }
            Radix2(ar, ai, true);
            double sc = 1.0 / m;
            for (int i = 0; i < n; i++)
            {
                double r = ar[i] * sc, ii = ai[i] * sc;
                re[i] = r * cosT[i] - ii * sinT[i];
                im[i] = r * sinT[i] + ii * cosT[i];
            }
        }

        /// <summary>2D FFT of row-major w x h complex data.</summary>
        public static void Transform2D(double[] re, double[] im, int w, int h, bool inverse)
        {
            Parallel.For(0, h, () => new double[2][] { new double[w], new double[w] }, (y, st, buf) =>
            {
                var r = buf[0]; var i = buf[1];
                int o = y * w;
                Array.Copy(re, o, r, 0, w);
                Array.Copy(im, o, i, 0, w);
                Transform(r, i, inverse);
                Array.Copy(r, 0, re, o, w);
                Array.Copy(i, 0, im, o, w);
                return buf;
            }, buf => { });
            Parallel.For(0, w, () => new double[2][] { new double[h], new double[h] }, (x, st, buf) =>
            {
                var r = buf[0]; var i = buf[1];
                for (int y = 0; y < h; y++) { r[y] = re[y * w + x]; i[y] = im[y * w + x]; }
                Transform(r, i, inverse);
                for (int y = 0; y < h; y++) { re[y * w + x] = r[y]; im[y * w + x] = i[y]; }
                return buf;
            }, buf => { });
        }

        /// <summary>Swap quadrants so the zero frequency is in the centre.</summary>
        public static void Shift2D(double[] data, int w, int h)
        {
            var tmp = new double[data.Length];
            int hw = w / 2, hh = h / 2;
            for (int y = 0; y < h; y++)
            {
                int ny = (y + hh) % h;
                for (int x = 0; x < w; x++)
                    tmp[ny * w + (x + hw) % w] = data[y * w + x];
            }
            Array.Copy(tmp, data, data.Length);
        }

        /// <summary>Power spectrum (|X|^2) of a real signal; returns n/2+1 bins.</summary>
        public static double[] PowerSpectrum(double[] signal, double[] window = null)
        {
            int n = signal.Length;
            var re = new double[n]; var im = new double[n];
            for (int i = 0; i < n; i++) re[i] = signal[i] * (window != null ? window[i] : 1.0);
            Transform(re, im, false);
            var p = new double[n / 2 + 1];
            for (int i = 0; i < p.Length; i++) p[i] = re[i] * re[i] + im[i] * im[i];
            return p;
        }

        public static double[] HannWindow(int n)
        {
            var w = new double[n];
            for (int i = 0; i < n; i++) w[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (n - 1));
            return w;
        }

        public static double[] BlackmanHarrisWindow(int n)
        {
            var w = new double[n];
            for (int i = 0; i < n; i++)
            {
                double x = 2 * Math.PI * i / (n - 1);
                w[i] = 0.35875 - 0.48829 * Math.Cos(x) + 0.14128 * Math.Cos(2 * x) - 0.01168 * Math.Cos(3 * x);
            }
            return w;
        }

        /// <summary>
        /// Phase correlation between two equally sized real images. Returns the sub-pixel shift (dx, dy)
        /// that moves image b onto image a, plus the correlation peak strength.
        /// </summary>
        public static double PhaseCorrelate(float[] a, float[] b, int w, int h, out double dx, out double dy, double[] window = null)
        {
            int n = w * h;
            var ar = new double[n]; var ai = new double[n];
            var br = new double[n]; var bi = new double[n];
            double ma = 0, mb = 0;
            for (int i = 0; i < n; i++) { ma += a[i]; mb += b[i]; }
            ma /= n; mb /= n;
            for (int i = 0; i < n; i++)
            {
                double win = window != null ? window[i] : 1.0;
                ar[i] = (a[i] - ma) * win;
                br[i] = (b[i] - mb) * win;
            }
            Transform2D(ar, ai, w, h, false);
            Transform2D(br, bi, w, h, false);
            for (int i = 0; i < n; i++)
            {
                // cross power spectrum A * conj(B) / |A B|
                double r = ar[i] * br[i] + ai[i] * bi[i];
                double im = ai[i] * br[i] - ar[i] * bi[i];
                double mag = Math.Sqrt(r * r + im * im) + 1e-20;
                ar[i] = r / mag; ai[i] = im / mag;
            }
            Transform2D(ar, ai, w, h, true);
            int best = 0; double bv = double.MinValue;
            for (int i = 0; i < n; i++) if (ar[i] > bv) { bv = ar[i]; best = i; }
            int px = best % w, py = best / w;
            // parabolic sub-pixel refinement
            Func<int, int, double> at = (x, y) => ar[((y + h) % h) * w + ((x + w) % w)];
            double cx = at(px, py), lx = at(px - 1, py), rx = at(px + 1, py), uy = at(px, py - 1), dyv = at(px, py + 1);
            double sx = 0, sy = 0;
            double denx = lx - 2 * cx + rx, deny = uy - 2 * cx + dyv;
            if (Math.Abs(denx) > 1e-12) sx = 0.5 * (lx - rx) / denx;
            if (Math.Abs(deny) > 1e-12) sy = 0.5 * (uy - dyv) / deny;
            double fx = px + AstroMath.Clamp(sx, -0.5, 0.5);
            double fy = py + AstroMath.Clamp(sy, -0.5, 0.5);
            if (fx > w / 2) fx -= w;
            if (fy > h / 2) fy -= h;
            dx = fx; dy = fy;
            return bv;
        }

        /// <summary>Separable 2D Hann window for tapering correlation inputs.</summary>
        public static double[] Hann2D(int w, int h)
        {
            var wx = HannWindow(w); var wy = HannWindow(h);
            var r = new double[w * h];
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) r[y * w + x] = wx[x] * wy[y];
            return r;
        }
    }
}
