using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Sidralune.Core.Mathematics;
using Sidralune.Imaging.Analysis;

namespace Sidralune.Imaging
{
    /// <summary>Star masks, star reduction and star removal.</summary>
    public static class StarTools
    {
        /// <summary>Soft star mask (0..1) built from detected stars with radius ∝ FWHM·log(flux).</summary>
        public static float[] StarMask(AstroImage img, double sizeFactor = 1.5, double softness = 1.0, List<DetectedStar> stars = null)
        {
            int w = img.Width, h = img.Height;
            stars = stars ?? StarDetector.Detect(img, new StarDetectionOptions { Sigma = 3, MaxStars = 20000, MaxEccentricity = 0.95 });
            var mask = new float[w * h];
            if (stars.Count == 0) return mask;
            double fmed = Stats.Median(stars.Select(s => s.Flux).ToList());
            foreach (var s in stars)
            {
                double r = Math.Max(1.5, s.Fwhm * sizeFactor * (1 + 0.5 * Math.Log10(Math.Max(1, s.Flux / Math.Max(fmed, 1e-9)))));
                if (s.Saturated) r *= 1.6;
                int rr = (int)Math.Ceiling(r * 2);
                for (int y = (int)s.Y - rr; y <= (int)s.Y + rr; y++)
                {
                    if (y < 0 || y >= h) continue;
                    for (int x = (int)s.X - rr; x <= (int)s.X + rr; x++)
                    {
                        if (x < 0 || x >= w) continue;
                        double d = Math.Sqrt((x - s.X) * (x - s.X) + (y - s.Y) * (y - s.Y));
                        double v = d <= r ? 1 : Math.Max(0, 1 - (d - r) / (r * softness));
                        int i = y * w + x;
                        if (v > mask[i]) mask[i] = (float)v;
                    }
                }
            }
            return mask;
        }

        /// <summary>Morphological star reduction: blend toward an erosion inside the star mask (PixInsight MorphologicalTransformation style).</summary>
        public static void ReduceStars(AstroImage img, double amount = 0.6, int radius = 2, float[] mask = null)
        {
            mask = mask ?? StarMask(img, 1.2);
            for (int c = 0; c < img.Channels; c++)
            {
                var d = img.Data[c];
                var er = Filters.Morphology(d, img.Width, img.Height, radius, false);
                var ero = Filters.GaussianBlur(er, img.Width, img.Height, 0.7);
                for (int i = 0; i < d.Length; i++) d[i] = (float)(d[i] + (Math.Min(ero[i], d[i]) - d[i]) * amount * mask[i]);
            }
            img.Header.AddHistory("Star reduction");
        }

        /// <summary>
        /// Remove stars: inpaint masked star regions by iterative harmonic diffusion from the surrounding background
        /// (boundary-value Laplace fill), followed by a light texture-preserving noise injection. Returns the star layer.
        /// </summary>
        public static AstroImage RemoveStars(AstroImage img, double sizeFactor = 1.8, int iterations = 200, System.Threading.CancellationToken ct = default(System.Threading.CancellationToken))
        {
            int w = img.Width, h = img.Height;
            var mask = StarMask(img, sizeFactor, 0.3);
            var hard = mask.Select(v => v > 0.05f).ToArray();
            var stars = img.CloneEmpty();
            for (int c = 0; c < img.Channels; c++)
            {
                var d = img.Data[c];
                var o = (float[])d.Clone();
                var st = ImageStatistics.Compute(d, w, 0);
                // initialise holes with the background estimate
                var bg = Filters.Median(Filters.Morphology(d, w, h, 3, false), w, h, 2);
                for (int i = 0; i < o.Length; i++) if (hard[i]) o[i] = bg[i];
                // multigrid-free Jacobi relaxation restricted to holes
                var tmp = new float[o.Length];
                for (int it = 0; it < iterations; it++)
                {
                    ct.ThrowIfCancellationRequested(); // a stop takes effect between iterations
                    Parallel.For(1, h - 1, y =>
                    {
                        for (int x = 1; x < w - 1; x++)
                        {
                            int i = y * w + x;
                            tmp[i] = hard[i] ? 0.25f * (o[i - 1] + o[i + 1] + o[i - w] + o[i + w]) : o[i];
                        }
                    });
                    for (int y = 1; y < h - 1; y++) Array.Copy(tmp, y * w + 1, o, y * w + 1, w - 2);
                }
                // re-inject noise so the filled areas match the surrounding grain
                var rng = new Random(17 + c);
                float sigma = (float)st.Noise;
                for (int i = 0; i < o.Length; i++)
                {
                    if (!hard[i]) continue;
                    double g = Math.Sqrt(-2 * Math.Log(1 - rng.NextDouble())) * Math.Cos(2 * Math.PI * rng.NextDouble());
                    o[i] += (float)(g * sigma * mask[i]);
                }
                for (int i = 0; i < o.Length; i++) stars.Data[c][i] = Math.Max(0, d[i] - o[i]);
                Array.Copy(o, d, d.Length);
            }
            img.Header.AddHistory("Star removal (inpainting)");
            return stars;
        }

        /// <summary>Screen-blend a star layer back onto a starless image: 1 - (1-a)(1-b).</summary>
        public static void ScreenBlend(AstroImage starless, AstroImage stars, double starStrength = 1.0)
        {
            for (int c = 0; c < starless.Channels; c++)
            {
                var a = starless.Data[c]; var b = stars.Data[Math.Min(c, stars.Channels - 1)];
                for (int i = 0; i < a.Length; i++)
                {
                    float sb = (float)(b[i] * starStrength);
                    a[i] = 1 - (1 - a[i]) * (1 - sb);
                }
            }
        }

        /// <summary>Range mask from luminance (lo..hi with fuzziness), for selective processing of nebulosity.</summary>
        public static float[] RangeMask(AstroImage img, double lo, double hi, double fuzz = 0.05, double smooth = 2)
        {
            var L = img.Luminance();
            var m = new float[L.Length];
            for (int i = 0; i < L.Length; i++)
            {
                double v = L[i];
                double a = fuzz > 0 ? AstroMath.Clamp((v - (lo - fuzz)) / fuzz, 0, 1) : (v >= lo ? 1 : 0);
                double b = fuzz > 0 ? AstroMath.Clamp(((hi + fuzz) - v) / fuzz, 0, 1) : (v <= hi ? 1 : 0);
                m[i] = (float)Math.Min(a, b);
            }
            return smooth > 0 ? Filters.GaussianBlur(m, img.Width, img.Height, smooth) : m;
        }
    }

    public static class Enhance
    {
        /// <summary>
        /// HDR multiscale transform: compress large-scale structure (wavelet residual beyond 'layers') by a gamma-like
        /// factor while keeping small-scale detail, revealing cores of bright galaxies/nebulae (like PixInsight HDRMT).
        /// </summary>
        public static void HdrMultiscale(AstroImage img, int layers = 6, double strength = 0.6)
        {
            for (int c = 0; c < img.Channels; c++)
            {
                var d = img.Data[c];
                var ls = Wavelets.Decompose(d, img.Width, img.Height, layers);
                var res = ls[layers];
                double mx = res.Max();
                for (int i = 0; i < res.Length; i++)
                {
                    double v = Math.Max(0, res[i]) / Math.Max(mx, 1e-9);
                    res[i] = (float)(Math.Pow(v, 1 - strength) * mx * (1 - strength * 0.5));
                }
                img.Data[c] = Wavelets.Reconstruct(ls);
            }
        }

        /// <summary>Local contrast enhancement by unsharp masking at a large radius.</summary>
        public static void LocalContrast(AstroImage img, double radius = 50, double amount = 0.3)
        {
            for (int c = 0; c < img.Channels; c++) img.Data[c] = Filters.UnsharpMask(img.Data[c], img.Width, img.Height, radius / 2.0, amount);
        }

        /// <summary>Edge-preserving bilateral filter (luminance-guided), radius in pixels, range sigma in data units.</summary>
        public static float[] Bilateral(float[] d, int w, int h, int radius, double sigmaSpatial, double sigmaRange)
        {
            var o = new float[d.Length];
            double ss2 = 2 * sigmaSpatial * sigmaSpatial, sr2 = 2 * sigmaRange * sigmaRange;
            Parallel.For(0, h, y =>
            {
                for (int x = 0; x < w; x++)
                {
                    float c = d[y * w + x];
                    double s = 0, sw = 0;
                    for (int dy = -radius; dy <= radius; dy++)
                    {
                        int yy = Filters.Mirror(y + dy, h);
                        for (int dx = -radius; dx <= radius; dx++)
                        {
                            float v = d[yy * w + Filters.Mirror(x + dx, w)];
                            double wt = Math.Exp(-(dx * dx + dy * dy) / ss2 - (v - c) * (v - c) / sr2);
                            s += wt * v; sw += wt;
                        }
                    }
                    o[y * w + x] = (float)(s / sw);
                }
            });
            return o;
        }

        /// <summary>Non-local means denoise (Buades et al. 2005) with 5x5 patches and a search window.</summary>
        public static float[] NonLocalMeans(float[] d, int w, int h, double strength, int search = 7, int patch = 2, System.Threading.CancellationToken ct = default(System.Threading.CancellationToken))
        {
            double sigma = ImageStatistics.MrsNoise(d, w, h);
            double hh = Math.Max(1e-12, strength * sigma);
            double h2 = hh * hh * (2 * patch + 1) * (2 * patch + 1);
            var o = new float[d.Length];
            Parallel.For(0, h, new ParallelOptions { CancellationToken = ct }, y =>
            {
                for (int x = 0; x < w; x++)
                {
                    double s = 0, sw = 0, wmax = 0;
                    for (int sy = -search; sy <= search; sy++)
                    {
                        int yy = y + sy;
                        if (yy < 0 || yy >= h) continue;
                        for (int sx = -search; sx <= search; sx++)
                        {
                            int xx = x + sx;
                            if (xx < 0 || xx >= w || (sx == 0 && sy == 0)) continue;
                            double dist = 0;
                            for (int py = -patch; py <= patch; py++)
                            {
                                int a = Filters.Mirror(y + py, h) * w, b = Filters.Mirror(yy + py, h) * w;
                                for (int px = -patch; px <= patch; px++)
                                {
                                    double dd = d[a + Filters.Mirror(x + px, w)] - d[b + Filters.Mirror(xx + px, w)];
                                    dist += dd * dd;
                                }
                            }
                            double wt = Math.Exp(-Math.Max(0, dist - 2 * sigma * sigma * (2 * patch + 1) * (2 * patch + 1)) / h2);
                            if (wt > wmax) wmax = wt;
                            s += wt * d[yy * w + xx]; sw += wt;
                        }
                    }
                    if (wmax == 0) wmax = 1;
                    s += wmax * d[y * w + x]; sw += wmax;
                    o[y * w + x] = (float)(s / sw);
                }
            });
            return o;
        }

        /// <summary>Remove horizontal/vertical banding (CMOS/DSLR) by subtracting robust row/column offsets.</summary>
        public static void BandingReduction(AstroImage img, bool rows = true, bool cols = false, double amount = 1.0)
        {
            int w = img.Width, h = img.Height;
            for (int c = 0; c < img.Channels; c++)
            {
                var d = img.Data[c];
                var st = ImageStatistics.Compute(d, w, 0);
                double sigma = Math.Max(st.BackgroundSigma, 1e-6);
                // each line's level: the median of its pixels, then again without the stars above it (clipping around
                // the line's own level, so lines offset by more than a few sigma are still corrected)
                Func<List<double>, double> level = buf =>
                {
                    double med = Stats.Median(buf);
                    var bg = buf.Where(v => v < med + 3 * sigma).ToList();
                    return bg.Count >= 10 ? Stats.Median(bg) : med;
                };
                if (rows)
                {
                    var buf = new List<double>(w);
                    for (int y = 0; y < h; y++)
                    {
                        buf.Clear();
                        for (int x = 0; x < w; x++) { float v = d[y * w + x]; if (!float.IsNaN(v)) buf.Add(v); }
                        if (buf.Count < 10) continue;
                        float off = (float)((level(buf) - st.Background) * amount);
                        for (int x = 0; x < w; x++) d[y * w + x] -= off;
                    }
                }
                if (cols)
                {
                    var buf = new List<double>(h);
                    for (int x = 0; x < w; x++)
                    {
                        buf.Clear();
                        for (int y = 0; y < h; y++) { float v = d[y * w + x]; if (!float.IsNaN(v)) buf.Add(v); }
                        if (buf.Count < 10) continue;
                        float off = (float)((level(buf) - st.Background) * amount);
                        for (int y = 0; y < h; y++) d[y * w + x] -= off;
                    }
                }
            }
        }
    }
}
