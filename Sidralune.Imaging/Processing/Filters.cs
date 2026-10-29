using System;
using System.Threading.Tasks;
using Sidralune.Core.Mathematics;

namespace Sidralune.Imaging
{
    /// <summary>Spatial filters on single planes (row-major float arrays).</summary>
    public static class Filters
    {
        public static float[] GaussianKernel(double sigma)
        {
            int r = Math.Max(1, (int)Math.Ceiling(sigma * 3));
            var k = new float[2 * r + 1];
            double s = 0;
            for (int i = -r; i <= r; i++) { k[i + r] = (float)Math.Exp(-0.5 * i * i / (sigma * sigma)); s += k[i + r]; }
            for (int i = 0; i < k.Length; i++) k[i] /= (float)s;
            return k;
        }

        /// <summary>Separable convolution with mirrored edges.</summary>
        public static float[] ConvolveSeparable(float[] d, int w, int h, float[] kx, float[] ky)
        {
            var tmp = new float[d.Length];
            var o = new float[d.Length];
            int rx = kx.Length / 2, ry = ky.Length / 2;
            Parallel.For(0, h, Core.Util.StopRequest.Options, y =>
            {
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    float s = 0;
                    for (int k = -rx; k <= rx; k++)
                    {
                        int xx = Mirror(x + k, w);
                        s += d[row + xx] * kx[k + rx];
                    }
                    tmp[row + x] = s;
                }
            });
            Parallel.For(0, h, Core.Util.StopRequest.Options, y =>
            {
                for (int x = 0; x < w; x++)
                {
                    float s = 0;
                    for (int k = -ry; k <= ry; k++)
                    {
                        int yy = Mirror(y + k, h);
                        s += tmp[yy * w + x] * ky[k + ry];
                    }
                    o[y * w + x] = s;
                }
            });
            return o;
        }

        public static int Mirror(int i, int n)
        {
            if (n == 1) return 0;
            while (i < 0 || i >= n) { if (i < 0) i = -i; if (i >= n) i = 2 * n - i - 2; }
            return i;
        }

        public static float[] GaussianBlur(float[] d, int w, int h, double sigma)
        {
            if (sigma <= 0) return (float[])d.Clone();
            var k = GaussianKernel(sigma);
            return ConvolveSeparable(d, w, h, k, k);
        }

        public static float[] BoxBlur(float[] d, int w, int h, int radius)
        {
            var k = new float[2 * radius + 1];
            for (int i = 0; i < k.Length; i++) k[i] = 1f / k.Length;
            return ConvolveSeparable(d, w, h, k, k);
        }

        /// <summary>Median filter with square window (radius r). Uses a histogram-free sort per pixel; fine for r ≤ 3.</summary>
        public static float[] Median(float[] d, int w, int h, int r)
        {
            var o = new float[d.Length];
            int n = (2 * r + 1) * (2 * r + 1);
            Parallel.For(0, h, Core.Util.StopRequest.Options, () => new float[n], (y, st, buf) =>
            {
                for (int x = 0; x < w; x++)
                {
                    int k = 0;
                    for (int dy = -r; dy <= r; dy++)
                    {
                        int yy = Mirror(y + dy, h) * w;
                        for (int dx = -r; dx <= r; dx++) buf[k++] = d[yy + Mirror(x + dx, w)];
                    }
                    o[y * w + x] = Stats.MedianInPlace(buf, k);
                }
                return buf;
            }, buf => { });
            return o;
        }

        /// <summary>Grey-scale dilation/erosion with a square structuring element.</summary>
        public static float[] Morphology(float[] d, int w, int h, int r, bool dilate)
        {
            // separable max/min filter
            var tmp = new float[d.Length];
            var o = new float[d.Length];
            Parallel.For(0, h, Core.Util.StopRequest.Options, y =>
            {
                for (int x = 0; x < w; x++)
                {
                    float m = dilate ? float.MinValue : float.MaxValue;
                    for (int k = -r; k <= r; k++)
                    {
                        float v = d[y * w + Mirror(x + k, w)];
                        if (dilate ? v > m : v < m) m = v;
                    }
                    tmp[y * w + x] = m;
                }
            });
            Parallel.For(0, h, Core.Util.StopRequest.Options, y =>
            {
                for (int x = 0; x < w; x++)
                {
                    float m = dilate ? float.MinValue : float.MaxValue;
                    for (int k = -r; k <= r; k++)
                    {
                        float v = tmp[Mirror(y + k, h) * w + x];
                        if (dilate ? v > m : v < m) m = v;
                    }
                    o[y * w + x] = m;
                }
            });
            return o;
        }

        public static float[] Opening(float[] d, int w, int h, int r) => Morphology(Morphology(d, w, h, r, false), w, h, r, true);
        public static float[] Closing(float[] d, int w, int h, int r) => Morphology(Morphology(d, w, h, r, true), w, h, r, false);

        /// <summary>Unsharp mask: d + amount*(d - blur(d)), with optional threshold.</summary>
        public static float[] UnsharpMask(float[] d, int w, int h, double sigma, double amount, double threshold = 0)
        {
            var b = GaussianBlur(d, w, h, sigma);
            var o = new float[d.Length];
            Parallel.For(0, d.Length / Math.Max(1, w), Core.Util.StopRequest.Options, y =>
            {
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    float diff = d[i] - b[i];
                    o[i] = Math.Abs(diff) > threshold ? (float)(d[i] + amount * diff) : d[i];
                }
            });
            return o;
        }

        /// <summary>FFT convolution with a (small or large) kernel centred at kernel's middle. Output same size as input.</summary>
        public static float[] ConvolveFft(float[] d, int w, int h, float[] kernel, int kw, int kh)
        {
            int pw = Fft.NextPowerOfTwo(w + kw), ph = Fft.NextPowerOfTwo(h + kh);
            var ar = new double[pw * ph]; var ai = new double[pw * ph];
            var br = new double[pw * ph]; var bi = new double[pw * ph];
            int ox = kw / 2, oy = kh / 2;
            for (int y = 0; y < ph; y++)
                for (int x = 0; x < pw; x++)
                {
                    // mirror-pad image
                    int sx = Mirror(x - ox, w), sy = Mirror(y - oy, h);
                    if (x < w + 2 * ox && y < h + 2 * oy) ar[y * pw + x] = d[sy * w + sx];
                }
            for (int y = 0; y < kh; y++) for (int x = 0; x < kw; x++) br[y * pw + x] = kernel[y * kw + x];
            Fft.Transform2D(ar, ai, pw, ph, false);
            Fft.Transform2D(br, bi, pw, ph, false);
            for (int i = 0; i < ar.Length; i++)
            {
                double r = ar[i] * br[i] - ai[i] * bi[i], im = ar[i] * bi[i] + ai[i] * br[i];
                ar[i] = r; ai[i] = im;
            }
            Fft.Transform2D(ar, ai, pw, ph, true);
            var o = new float[w * h];
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) o[y * w + x] = (float)ar[(y + 2 * oy) * pw + x + 2 * ox];
            return o;
        }

        /// <summary>Laplacian-of-Gaussian sharpness/variance metric used for frame quality.</summary>
        public static double LaplacianVariance(float[] d, int w, int h, int x0 = 0, int y0 = 0, int rw = -1, int rh = -1)
        {
            if (rw < 0) rw = w; if (rh < 0) rh = h;
            double s = 0, s2 = 0; long n = 0;
            for (int y = Math.Max(1, y0); y < Math.Min(h - 1, y0 + rh); y++)
                for (int x = Math.Max(1, x0); x < Math.Min(w - 1, x0 + rw); x++)
                {
                    int i = y * w + x;
                    double l = 4 * d[i] - d[i - 1] - d[i + 1] - d[i - w] - d[i + w];
                    s += l; s2 += l * l; n++;
                }
            if (n == 0) return 0;
            double m = s / n;
            return s2 / n - m * m;
        }

        /// <summary>Gradient energy (Tenengrad / Sobel) focus metric.</summary>
        public static double GradientEnergy(float[] d, int w, int h, int x0, int y0, int rw, int rh)
        {
            double s = 0; long n = 0;
            for (int y = Math.Max(1, y0); y < Math.Min(h - 1, y0 + rh); y++)
                for (int x = Math.Max(1, x0); x < Math.Min(w - 1, x0 + rw); x++)
                {
                    int i = y * w + x;
                    double gx = d[i - w + 1] + 2 * d[i + 1] + d[i + w + 1] - d[i - w - 1] - 2 * d[i - 1] - d[i + w - 1];
                    double gy = d[i + w - 1] + 2 * d[i + w] + d[i + w + 1] - d[i - w - 1] - 2 * d[i - w] - d[i - w + 1];
                    s += gx * gx + gy * gy; n++;
                }
            return n > 0 ? s / n : 0;
        }
    }

    /// <summary>
    /// Starlet / à trous wavelet transform with the B3 spline kernel [1,4,6,4,1]/16 (Starck, Murtagh &amp; Fadili 2010).
    /// Used for RegiStax-style layer sharpening, multiscale denoising and the MRS noise estimator.
    /// </summary>
    public static class Wavelets
    {
        private static readonly float[] B3 = { 1f / 16, 4f / 16, 6f / 16, 4f / 16, 1f / 16 };

        /// <summary>Smooth with the B3 kernel dilated by 2^(scale-1) (scale ≥ 1).</summary>
        public static float[] AtrousSmooth(float[] d, int w, int h, int scale)
        {
            int step = 1 << (scale - 1);
            var tmp = new float[d.Length];
            var o = new float[d.Length];
            Parallel.For(0, h, Core.Util.StopRequest.Options, y =>
            {
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    float s = 0;
                    for (int k = -2; k <= 2; k++) s += B3[k + 2] * d[row + Filters.Mirror(x + k * step, w)];
                    tmp[row + x] = s;
                }
            });
            Parallel.For(0, h, Core.Util.StopRequest.Options, y =>
            {
                for (int x = 0; x < w; x++)
                {
                    float s = 0;
                    for (int k = -2; k <= 2; k++) s += B3[k + 2] * tmp[Filters.Mirror(y + k * step, h) * w + x];
                    o[y * w + x] = s;
                }
            });
            return o;
        }

        /// <summary>Decompose into 'levels' detail layers plus a residual (last element).</summary>
        public static float[][] Decompose(float[] d, int w, int h, int levels)
        {
            var layers = new float[levels + 1][];
            var cur = d;
            for (int j = 1; j <= levels; j++)
            {
                var sm = AtrousSmooth(cur, w, h, j);
                var detail = new float[d.Length];
                for (int i = 0; i < d.Length; i++) detail[i] = cur[i] - sm[i];
                layers[j - 1] = detail;
                cur = sm;
            }
            layers[levels] = cur;
            return layers;
        }

        public static float[] Reconstruct(float[][] layers)
        {
            int n = layers[0].Length;
            var o = new float[n];
            foreach (var l in layers) for (int i = 0; i < n; i++) o[i] += l[i];
            return o;
        }

        /// <summary>Noise sigma in each B3 starlet layer for unit white Gaussian noise (Starck &amp; Murtagh).</summary>
        public static readonly double[] LayerNoise = { 0.889, 0.200, 0.086, 0.041, 0.020, 0.010, 0.005, 0.0025 };

        /// <summary>
        /// RegiStax-like wavelet sharpening: each layer is multiplied by (1 + amount[j]) after soft-thresholding by
        /// denoise[j] × layer noise; optional "linked" layers mode is achieved by passing equal amounts.
        /// </summary>
        public static float[] Sharpen(float[] d, int w, int h, double[] amount, double[] denoise, double noiseSigma = -1)
        {
            int levels = amount.Length;
            var layers = Decompose(d, w, h, levels);
            if (noiseSigma < 0) noiseSigma = Analysis.ImageStatistics.MrsNoise(d, w, h);
            for (int j = 0; j < levels; j++)
            {
                double gain = 1 + amount[j];
                double thr = denoise != null && j < denoise.Length ? denoise[j] * noiseSigma * LayerNoise[Math.Min(j, LayerNoise.Length - 1)] * 3 : 0;
                var L = layers[j];
                for (int i = 0; i < L.Length; i++)
                {
                    double v = L[i];
                    if (thr > 0) { double a = Math.Abs(v) - thr; v = a > 0 ? Math.Sign(v) * a : 0; }
                    L[i] = (float)(v * gain);
                }
            }
            return Reconstruct(layers);
        }

        /// <summary>Multiscale denoise: k-sigma soft thresholding of the first layers (amounts 0..1 per layer).</summary>
        public static float[] Denoise(float[] d, int w, int h, double[] strength, double k = 3.0)
        {
            int levels = strength.Length;
            var layers = Decompose(d, w, h, levels);
            double sigma = Analysis.ImageStatistics.MrsNoise(d, w, h);
            for (int j = 0; j < levels; j++)
            {
                double thr = k * sigma * LayerNoise[Math.Min(j, LayerNoise.Length - 1)];
                double s = strength[j];
                var L = layers[j];
                for (int i = 0; i < L.Length; i++)
                {
                    double v = L[i];
                    double a = Math.Abs(v);
                    double shrunk = a > thr ? Math.Sign(v) * (a - thr) : 0;
                    L[i] = (float)(v * (1 - s) + shrunk * s);
                }
            }
            return Reconstruct(layers);
        }
    }
}
