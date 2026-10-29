using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Sidralune.Core.Mathematics;
using Sidralune.Imaging.Analysis;

namespace Sidralune.Imaging
{
    /// <summary>Point spread function kernels (odd-sized, normalised to unit sum).</summary>
    public class Psf
    {
        public int Size;
        public float[] Data;

        public static Psf Gaussian(double fwhm, double ellipticity = 0, double angleDeg = 0)
        {
            double sx = fwhm / 2.3548, sy = sx * (1 - ellipticity);
            int r = (int)Math.Ceiling(Math.Max(sx, sy) * 4);
            var p = new Psf { Size = 2 * r + 1 };
            p.Data = new float[p.Size * p.Size];
            double a = angleDeg * Math.PI / 180, c = Math.Cos(a), s = Math.Sin(a);
            for (int y = -r; y <= r; y++)
                for (int x = -r; x <= r; x++)
                {
                    double u = (x * c + y * s) / sx, v = (-x * s + y * c) / sy;
                    p.Data[(y + r) * p.Size + x + r] = (float)Math.Exp(-0.5 * (u * u + v * v));
                }
            p.Normalize();
            return p;
        }

        public static Psf Moffat(double fwhm, double beta = 3.0)
        {
            double alpha = fwhm / (2 * Math.Sqrt(Math.Pow(2, 1 / beta) - 1));
            int r = (int)Math.Ceiling(fwhm * 3);
            var p = new Psf { Size = 2 * r + 1 };
            p.Data = new float[p.Size * p.Size];
            for (int y = -r; y <= r; y++)
                for (int x = -r; x <= r; x++)
                    p.Data[(y + r) * p.Size + x + r] = (float)Math.Pow(1 + (x * x + y * y) / (alpha * alpha), -beta);
            p.Normalize();
            return p;
        }

        /// <summary>
        /// Airy pattern for a (possibly obstructed) circular aperture, integrated over pixels by 3x3 supersampling.
        /// firstZeroPx = radius of the first dark ring in pixels (1.22 λ f/D / pixel).
        /// </summary>
        public static Psf Airy(double firstZeroPx, double obstruction = 0, int rings = 4)
        {
            int r = (int)Math.Ceiling(firstZeroPx * (rings + 0.5));
            var p = new Psf { Size = 2 * r + 1 };
            p.Data = new float[p.Size * p.Size];
            double k = 3.8317 / firstZeroPx; // first zero of J1
            double e = obstruction;
            for (int y = -r; y <= r; y++)
                for (int x = -r; x <= r; x++)
                {
                    double acc = 0;
                    for (int sy = -1; sy <= 1; sy++)
                        for (int sx = -1; sx <= 1; sx++)
                        {
                            double rr = Math.Sqrt(Math.Pow(x + sx / 3.0, 2) + Math.Pow(y + sy / 3.0, 2)) * k;
                            double v;
                            if (rr < 1e-8) v = 1;
                            else
                            {
                                double a = 2 * BesselJ1(rr) / rr;
                                double b = e > 0 ? e * e * 2 * BesselJ1(e * rr) / (e * rr) : 0;
                                v = (a - b) / (1 - e * e);
                                v *= v;
                            }
                            acc += v;
                        }
                    p.Data[(y + r) * p.Size + x + r] = (float)(acc / 9);
                }
            p.Normalize();
            return p;
        }

        /// <summary>Bessel J1 (Numerical Recipes rational approximation).</summary>
        public static double BesselJ1(double x)
        {
            double ax = Math.Abs(x);
            if (ax < 8.0)
            {
                double y = x * x;
                double a1 = x * (72362614232.0 + y * (-7895059235.0 + y * (242396853.1 + y * (-2972611.439 + y * (15704.48260 + y * (-30.16036606))))));
                double a2 = 144725228442.0 + y * (2300535178.0 + y * (18583304.74 + y * (99447.43394 + y * (376.9991397 + y * 1.0))));
                return a1 / a2;
            }
            double z = 8.0 / ax, yy = z * z, xx = ax - 2.356194491;
            double b1 = 1.0 + yy * (0.183105e-2 + yy * (-0.3516396496e-4 + yy * (0.2457520174e-5 + yy * (-0.240337019e-6))));
            double b2 = 0.04687499995 + yy * (-0.2002690873e-3 + yy * (0.8449199096e-5 + yy * (-0.88228987e-6 + yy * 0.105787412e-6)));
            double ans = Math.Sqrt(0.636619772 / ax) * (Math.Cos(xx) * b1 - z * Math.Sin(xx) * b2);
            return x < 0 ? -ans : ans;
        }

        /// <summary>Empirical PSF: median-combine normalised, sub-pixel-centred cutouts of unsaturated stars.</summary>
        public static Psf FromStars(float[] img, int w, int h, IList<DetectedStar> stars, int radius = 0, int maxStars = 60)
        {
            var use = stars.Where(s => !s.Saturated && s.Snr > 20).OrderByDescending(s => s.Flux).Take(maxStars).ToList();
            if (use.Count == 0) return Gaussian(stars.Count > 0 ? stars[0].Fwhm : 2.5);
            double fwhm = Stats.Median(use.Select(s => s.Fwhm).ToList());
            int r = radius > 0 ? radius : (int)Math.Ceiling(fwhm * 2.5);
            int size = 2 * r + 1;
            var cut = new List<float[]>();
            foreach (var s in use)
            {
                if (s.X - r - 2 < 0 || s.Y - r - 2 < 0 || s.X + r + 2 >= w || s.Y + r + 2 >= h) continue;
                var c = new float[size * size];
                double sum = 0;
                for (int y = -r; y <= r; y++)
                    for (int x = -r; x <= r; x++)
                    {
                        float v = Geometry.SampleAt(img, w, h, s.X + x, s.Y + y, Interpolation.Bicubic) - (float)s.Background;
                        c[(y + r) * size + x + r] = v; sum += v;
                    }
                if (sum <= 0) continue;
                for (int i = 0; i < c.Length; i++) c[i] = (float)(c[i] / sum);
                cut.Add(c);
            }
            if (cut.Count == 0) return Gaussian(fwhm);
            var p = new Psf { Size = size, Data = new float[size * size] };
            var buf = new float[cut.Count];
            for (int i = 0; i < p.Data.Length; i++)
            {
                for (int k = 0; k < cut.Count; k++) buf[k] = cut[k][i];
                p.Data[i] = Math.Max(0, Stats.MedianInPlace(buf, cut.Count));
            }
            p.Normalize();
            return p;
        }

        public void Normalize()
        {
            double s = 0;
            foreach (var v in Data) s += v;
            if (s > 0) for (int i = 0; i < Data.Length; i++) Data[i] = (float)(Data[i] / s);
        }
    }

    /// <summary>FFT convolution engine with a fixed PSF (precomputed optical transfer function).</summary>
    public class FftConvolver
    {
        private readonly int _w, _h, _pw, _ph, _pad;
        private readonly double[] _otfRe, _otfIm;

        public FftConvolver(int w, int h, Psf psf)
        {
            _w = w; _h = h;
            _pad = psf.Size;
            _pw = Fft.NextPowerOfTwo(w + 2 * _pad);
            _ph = Fft.NextPowerOfTwo(h + 2 * _pad);
            _otfRe = new double[_pw * _ph]; _otfIm = new double[_pw * _ph];
            int r = psf.Size / 2;
            for (int y = 0; y < psf.Size; y++)
                for (int x = 0; x < psf.Size; x++)
                {
                    int px = ((x - r) % _pw + _pw) % _pw, py = ((y - r) % _ph + _ph) % _ph;
                    _otfRe[py * _pw + px] = psf.Data[y * psf.Size + x];
                }
            Fft.Transform2D(_otfRe, _otfIm, _pw, _ph, false);
        }

        public float[] Convolve(float[] d, bool adjoint = false)
        {
            var re = new double[_pw * _ph]; var im = new double[_pw * _ph];
            // mirror padding to avoid wrap-around ringing
            for (int y = 0; y < _ph; y++)
            {
                int sy = Filters.Mirror(y - _pad, _h);
                for (int x = 0; x < _pw; x++)
                {
                    if (y >= _h + 2 * _pad || x >= _w + 2 * _pad) continue;
                    re[y * _pw + x] = d[sy * _w + Filters.Mirror(x - _pad, _w)];
                }
            }
            Fft.Transform2D(re, im, _pw, _ph, false);
            for (int i = 0; i < re.Length; i++)
            {
                double a = re[i], b = im[i], c = _otfRe[i], e = adjoint ? -_otfIm[i] : _otfIm[i];
                re[i] = a * c - b * e; im[i] = a * e + b * c;
            }
            Fft.Transform2D(re, im, _pw, _ph, true);
            var o = new float[_w * _h];
            for (int y = 0; y < _h; y++) for (int x = 0; x < _w; x++) o[y * _w + x] = (float)re[(y + _pad) * _pw + x + _pad];
            return o;
        }

        /// <summary>Wiener deconvolution with noise-to-signal ratio K.</summary>
        public float[] Wiener(float[] d, double k)
        {
            var re = new double[_pw * _ph]; var im = new double[_pw * _ph];
            for (int y = 0; y < _ph; y++)
            {
                int sy = Filters.Mirror(y - _pad, _h);
                for (int x = 0; x < _pw; x++)
                {
                    if (y >= _h + 2 * _pad || x >= _w + 2 * _pad) continue;
                    re[y * _pw + x] = d[sy * _w + Filters.Mirror(x - _pad, _w)];
                }
            }
            Fft.Transform2D(re, im, _pw, _ph, false);
            for (int i = 0; i < re.Length; i++)
            {
                double c = _otfRe[i], e = _otfIm[i];
                double h2 = c * c + e * e;
                double fr = c / (h2 + k), fi = -e / (h2 + k);
                double a = re[i], b = im[i];
                re[i] = a * fr - b * fi; im[i] = a * fi + b * fr;
            }
            Fft.Transform2D(re, im, _pw, _ph, true);
            var o = new float[_w * _h];
            for (int y = 0; y < _h; y++) for (int x = 0; x < _w; x++) o[y * _w + x] = (float)re[(y + _pad) * _pw + x + _pad];
            return o;
        }
    }

    public enum DeconvolutionMethod { RichardsonLucy, RichardsonLucyTV, Wiener, VanCittert, Landweber, BlindRichardsonLucy }

    public class DeconvolutionOptions
    {
        public DeconvolutionMethod Method = DeconvolutionMethod.RichardsonLucyTV;
        public int Iterations = 30;
        /// <summary>Total variation regularisation weight (Dey et al. 2006), typical 0.001..0.01.</summary>
        public double TvLambda = 0.002;
        public double WienerK = 0.01;
        public double Relaxation = 1.0;   // Van Cittert / Landweber step
        /// <summary>Deringing: limit how far a pixel may move from its original value relative to local dark structures.</summary>
        public double DeringingAmount = 0.0;
        /// <summary>Optional mask (0..1) restricting deconvolution to bright regions (e.g. planet disc / star mask).</summary>
        public float[] Mask;
        /// <summary>Noise-adaptive damping: stop changing pixels whose residual is below k·σ (Starck/White damping).</summary>
        public double DampingSigma = 0.0;
    }

    /// <summary>Iterative and direct image deconvolution.</summary>
    public static class Deconvolution
    {
        public static float[] Run(float[] d, int w, int h, Psf psf, DeconvolutionOptions opt, IProgress<double> progress = null, CancellationToken ct = default(CancellationToken))
        {
            var conv = new FftConvolver(w, h, psf);
            float[] result;
            // offset to keep data positive for RL
            float min = d.Where(v => !float.IsNaN(v)).DefaultIfEmpty(0).Min();
            float off = min < 1e-6f ? 1e-6f - min : 0;
            var data = off > 0 ? d.Select(v => v + off).ToArray() : (float[])d.Clone();
            double sigma = opt.DampingSigma > 0 ? ImageStatistics.MrsNoise(data, w, h) : 0;
            switch (opt.Method)
            {
                case DeconvolutionMethod.Wiener: result = conv.Wiener(data, opt.WienerK); break;
                case DeconvolutionMethod.VanCittert:
                case DeconvolutionMethod.Landweber:
                    {
                        var x = (float[])data.Clone();
                        for (int it = 0; it < opt.Iterations; it++)
                        {
                            ct.ThrowIfCancellationRequested();
                            var hx = conv.Convolve(x);
                            float[] upd;
                            if (opt.Method == DeconvolutionMethod.Landweber)
                            {
                                var r = new float[x.Length];
                                for (int i = 0; i < r.Length; i++) r[i] = data[i] - hx[i];
                                upd = conv.Convolve(r, true);
                            }
                            else { upd = new float[x.Length]; for (int i = 0; i < upd.Length; i++) upd[i] = data[i] - hx[i]; }
                            for (int i = 0; i < x.Length; i++) x[i] = Math.Max(0, x[i] + (float)opt.Relaxation * upd[i]);
                            progress?.Report((it + 1.0) / opt.Iterations);
                        }
                        result = x;
                        break;
                    }
                case DeconvolutionMethod.BlindRichardsonLucy:
                    result = BlindRl(data, w, h, psf, opt, progress, ct);
                    break;
                default:
                    {
                        var x = (float[])data.Clone();
                        bool tv = opt.Method == DeconvolutionMethod.RichardsonLucyTV && opt.TvLambda > 0;
                        for (int it = 0; it < opt.Iterations; it++)
                        {
                            ct.ThrowIfCancellationRequested();
                            var hx = conv.Convolve(x);
                            var ratio = new float[x.Length];
                            for (int i = 0; i < ratio.Length; i++)
                            {
                                float den = Math.Max(hx[i], 1e-12f);
                                float rr = data[i] / den;
                                if (sigma > 0)
                                {
                                    // Damped RL (White 1994): suppress updates where residual is noise-like
                                    double res = Math.Abs(data[i] - hx[i]) / (opt.DampingSigma * sigma);
                                    if (res < 1) rr = 1 + (rr - 1) * (float)(res * res);
                                }
                                ratio[i] = rr;
                            }
                            var corr = conv.Convolve(ratio, true);
                            float[] div = tv ? TvDivergence(x, w, h) : null;
                            Parallel.For(0, h, Core.Util.StopRequest.Options, y =>
                            {
                                for (int xx = 0; xx < w; xx++)
                                {
                                    int i = y * w + xx;
                                    double f = corr[i];
                                    if (tv) f /= Math.Max(0.1, 1 - opt.TvLambda * div[i]);
                                    x[i] = (float)Math.Max(1e-12, x[i] * f);
                                }
                            });
                            progress?.Report((it + 1.0) / opt.Iterations);
                        }
                        result = x;
                        break;
                    }
            }
            if (opt.DeringingAmount > 0) Dering(data, result, w, h, opt.DeringingAmount);
            if (opt.Mask != null)
                for (int i = 0; i < result.Length; i++) result[i] = data[i] + (result[i] - data[i]) * opt.Mask[i];
            if (off > 0) for (int i = 0; i < result.Length; i++) result[i] -= off;
            return result;
        }

        public static void Run(AstroImage img, Psf psf, DeconvolutionOptions opt, IProgress<double> progress = null, CancellationToken ct = default(CancellationToken))
        {
            for (int c = 0; c < img.Channels; c++)
            {
                int cc = c;
                var p = progress != null ? new Progress<double>(f => progress.Report((cc + f) / img.Channels)) : null;
                img.Data[c] = Run(img.Data[c], img.Width, img.Height, psf, opt, p, ct);
            }
            img.Header.AddHistory(string.Format("Deconvolution {0}, {1} iterations", opt.Method, opt.Iterations));
        }

        /// <summary>div(∇x/|∇x|) for TV regularisation.</summary>
        private static float[] TvDivergence(float[] x, int w, int h)
        {
            var div = new float[x.Length];
            const float eps = 1e-6f;
            Parallel.For(0, h, Core.Util.StopRequest.Options, y =>
            {
                for (int i = 0; i < w; i++)
                {
                    int p = y * w + i;
                    float c = x[p];
                    float xp = i < w - 1 ? x[p + 1] : c, xm = i > 0 ? x[p - 1] : c, yp = y < h - 1 ? x[p + w] : c, ym = y > 0 ? x[p - w] : c;
                    float gx = xp - c, gy = yp - c;
                    float n1 = (float)Math.Sqrt(gx * gx + gy * gy + eps);
                    float gxm = c - xm, gym1 = (i > 0 && y < h - 1 ? x[p - 1 + w] : xm) - xm;
                    float n2 = (float)Math.Sqrt(gxm * gxm + gym1 * gym1 + eps);
                    float gyy = c - ym, gxm2 = (y > 0 && i < w - 1 ? x[p - w + 1] : ym) - ym;
                    float n3 = (float)Math.Sqrt(gyy * gyy + gxm2 * gxm2 + eps);
                    div[p] = gx / n1 - gxm / n2 + gy / n1 - gyy / n3;
                }
            });
            return div;
        }

        /// <summary>
        /// Global deringing: wherever the restored image drops below the local minimum of the original around bright
        /// structures (dark rings), pull it back toward the original by 'amount'.
        /// </summary>
        public static void Dering(float[] orig, float[] res, int w, int h, double amount)
        {
            var mn = Filters.Morphology(orig, w, h, 2, false);
            for (int i = 0; i < res.Length; i++)
                if (res[i] < mn[i]) res[i] = (float)(res[i] + (mn[i] - res[i]) * amount);
        }

        /// <summary>Blind RL (Fish et al. 1995): alternate RL updates of image and PSF.</summary>
        private static float[] BlindRl(float[] data, int w, int h, Psf psf0, DeconvolutionOptions opt, IProgress<double> progress, CancellationToken ct)
        {
            var psf = new Psf { Size = psf0.Size, Data = (float[])psf0.Data.Clone() };
            var x = (float[])data.Clone();
            int outer = Math.Max(1, opt.Iterations / 5);
            for (int o = 0; o < outer; o++)
            {
                ct.ThrowIfCancellationRequested();
                var sub = new DeconvolutionOptions { Method = DeconvolutionMethod.RichardsonLucy, Iterations = 5 };
                x = Run(data, w, h, psf, sub);
                // PSF update: psf *= correlate(ratio, x) restricted to the kernel support
                var conv = new FftConvolver(w, h, psf);
                var hx = conv.Convolve(x);
                int r = psf.Size / 2;
                var np = new float[psf.Data.Length];
                double sumX = x.Sum(v => (double)v);
                for (int ky = -r; ky <= r; ky++)
                    for (int kx = -r; kx <= r; kx++)
                    {
                        double acc = 0;
                        for (int y = r; y < h - r; y += 2)
                            for (int xx = r; xx < w - r; xx += 2)
                            {
                                int i = y * w + xx;
                                acc += data[i] / Math.Max(1e-12, hx[i]) * x[(y - ky) * w + xx - kx];
                            }
                        int k = (ky + r) * psf.Size + kx + r;
                        np[k] = (float)(psf.Data[k] * acc / (sumX / 4));
                    }
                psf.Data = np;
                psf.Normalize();
                progress?.Report((o + 1.0) / outer);
            }
            return x;
        }
    }
}
