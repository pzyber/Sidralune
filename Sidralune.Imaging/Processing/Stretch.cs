using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Sidralune.Core.Mathematics;
using Sidralune.Imaging.Analysis;

namespace Sidralune.Imaging
{
    /// <summary>Screen transfer / histogram transformation parameters (PixInsight-style).</summary>
    public class HistogramTransform
    {
        public double Shadows = 0, Midtones = 0.5, Highlights = 1;
        public double LowRange = 0, HighRange = 1;

        /// <summary>Midtones transfer function MTF(m, x).</summary>
        public static double Mtf(double m, double x)
        {
            if (x <= 0) return 0;
            if (x >= 1) return 1;
            if (m == 0.5) return x;
            return (m - 1) * x / ((2 * m - 1) * x - m);
        }

        public double Apply(double v)
        {
            double r = Highlights - Shadows;
            double x = r > 0 ? (v - Shadows) / r : 0;
            x = Mtf(Midtones, AstroMath.Clamp(x, 0, 1));
            return LowRange + x * (HighRange - LowRange);
        }

        /// <summary>
        /// Automatic screen stretch (PixInsight ScreenTransferFunction "auto" algorithm): shadows clipped at
        /// median - 2.8·MADN, midtones chosen so the median maps to targetBackground (0.25).
        /// </summary>
        public static HistogramTransform Auto(ChannelStats s, double targetBackground = 0.25, double shadowsClip = -2.8)
        {
            double madn = s.Mad * Stats.MadToSigma;
            var t = new HistogramTransform();
            bool inverted = s.Median > 0.5;
            if (!inverted)
            {
                t.Shadows = AstroMath.Clamp(s.Median + shadowsClip * madn, 0, 1);
                t.Highlights = 1;
                t.Midtones = Mtf(targetBackground, s.Median - t.Shadows);
            }
            else
            {
                t.Highlights = AstroMath.Clamp(s.Median - shadowsClip * madn, 0, 1);
                t.Shadows = 0;
                t.Midtones = 1 - Mtf(targetBackground, t.Highlights - s.Median);
            }
            return t;
        }

        public float[] Lut(int size = 65536)
        {
            var l = new float[size];
            for (int i = 0; i < size; i++) l[i] = (float)Apply(i / (double)(size - 1));
            return l;
        }
    }

    public static class StretchOps
    {
        public static void ApplyHistogram(AstroImage img, HistogramTransform[] perChannel)
        {
            for (int c = 0; c < img.Channels; c++)
            {
                var t = perChannel[Math.Min(c, perChannel.Length - 1)];
                var d = img.Data[c];
                Parallel.For(0, img.Height, y => { int o = y * img.Width; for (int x = 0; x < img.Width; x++) d[o + x] = (float)t.Apply(d[o + x]); });
            }
        }

        /// <summary>Auto stretch (linked or unlinked channels).</summary>
        public static HistogramTransform[] AutoStretch(AstroImage img, bool linked = true, double target = 0.25, double clip = -2.8)
        {
            var stats = new ChannelStats[img.Channels];
            for (int c = 0; c < img.Channels; c++) stats[c] = ImageStatistics.Compute(img.Data[c], img.Width, 0);
            if (linked && img.Channels > 1)
            {
                var avg = new ChannelStats { Median = 0, Mad = 0 };
                foreach (var s in stats) { avg.Median += s.Median / stats.Length; avg.Mad += s.Mad / stats.Length; }
                var t = HistogramTransform.Auto(avg, target, clip);
                var res = new HistogramTransform[img.Channels];
                for (int c = 0; c < img.Channels; c++) res[c] = t;
                return res;
            }
            var r = new HistogramTransform[img.Channels];
            for (int c = 0; c < img.Channels; c++) r[c] = HistogramTransform.Auto(stats[c], target, clip);
            return r;
        }

        /// <summary>
        /// Generalised Hyperbolic Stretch (Payne &amp; Cranfield 2021, as in Siril/PixInsight GHS). D = stretch factor (ln(D+1)),
        /// b = local intensity, SP = symmetry point, LP/HP = shadow/highlight protection.
        /// </summary>
        public static Func<double, double> Ghs(double D, double b, double SP, double LP = 0, double HP = 1)
        {
            if (D <= 0) return x => x;
            double Dd = Math.Exp(D) - 1; // the scripts use D in ln(D+1) form; interpret input as ln-stretch
            Func<double, double> T, Tp;
            if (b == -1)
            {
                T = x => Math.Log(1 + Dd * x);
                Tp = x => Dd / (1 + Dd * x);
            }
            else if (b < 0)
            {
                T = x => (1 - Math.Pow(1 - b * Dd * x, (b + 1) / b)) / (Dd * (b + 1));
                Tp = x => Math.Pow(1 - b * Dd * x, 1 / b);
            }
            else if (b == 0)
            {
                T = x => 1 - Math.Exp(-Dd * x);
                Tp = x => Dd * Math.Exp(-Dd * x);
            }
            else
            {
                T = x => 1 - Math.Pow(1 + b * Dd * x, -1 / b);
                Tp = x => Dd * Math.Pow(1 + b * Dd * x, -(1 + b) / b);
            }
            // piecewise: linear below LP, hyperbolic around SP (mirrored for x < SP), linear above HP
            Func<double, double> core = x =>
            {
                if (x < SP) return -T(SP - x);
                return T(x - SP);
            };
            Func<double, double> coreP = x => x < SP ? Tp(SP - x) : Tp(x - SP);
            double cLP = core(LP), cHP = core(HP), sLP = coreP(LP), sHP = coreP(HP);
            Func<double, double> piece = x =>
            {
                if (x < LP) return cLP + sLP * (x - LP);
                if (x > HP) return cHP + sHP * (x - HP);
                return core(x);
            };
            double y0 = piece(0), y1 = piece(1);
            return x => (piece(AstroMath.Clamp(x, 0, 1)) - y0) / (y1 - y0);
        }

        /// <summary>Arcsinh stretch preserving colour ratios (Lupton et al. 2004).</summary>
        public static void ArcsinhStretch(AstroImage img, double stretch, double blackPoint = 0, bool preserveColor = true)
        {
            double norm = Asinh(stretch);
            int n = img.PixelCount;
            if (img.Channels >= 3 && preserveColor)
            {
                var r = img.Data[0]; var g = img.Data[1]; var b = img.Data[2];
                Parallel.For(0, img.Height, y =>
                {
                    for (int x = 0; x < img.Width; x++)
                    {
                        int i = y * img.Width + x;
                        double R = Math.Max(0, r[i] - blackPoint), G = Math.Max(0, g[i] - blackPoint), B = Math.Max(0, b[i] - blackPoint);
                        double I = (R + G + B) / 3;
                        double k = I > 0 ? Asinh(stretch * I) / (norm * I) : 0;
                        r[i] = (float)(R * k); g[i] = (float)(G * k); b[i] = (float)(B * k);
                    }
                });
            }
            else img.Apply(v => (float)(Asinh(stretch * Math.Max(0, v - blackPoint)) / norm));
        }

        private static double Asinh(double x) => Math.Log(x + Math.Sqrt(x * x + 1));

        /// <summary>Levels: black, gamma, white (gamma &gt; 1 brightens).</summary>
        public static void Levels(AstroImage img, double black, double gamma, double white, int channel = -1)
        {
            for (int c = 0; c < img.Channels; c++)
            {
                if (channel >= 0 && c != channel) continue;
                var d = img.Data[c];
                double r = Math.Max(1e-9, white - black), ig = 1 / gamma;
                for (int i = 0; i < d.Length; i++) d[i] = (float)Math.Pow(AstroMath.Clamp((d[i] - black) / r, 0, 1), ig);
            }
        }

        /// <summary>Curves through control points (x,y in 0..1) via monotone cubic (Fritsch-Carlson) interpolation.</summary>
        public static Func<double, double> Curve(IList<double[]> points)
        {
            var p = new List<double[]>(points);
            p.Sort((a, b) => a[0].CompareTo(b[0]));
            if (p.Count == 0 || p[0][0] > 0) p.Insert(0, new[] { 0.0, 0.0 });
            if (p[p.Count - 1][0] < 1) p.Add(new[] { 1.0, 1.0 });
            int n = p.Count;
            var dx = new double[n - 1]; var m = new double[n - 1]; var t = new double[n];
            for (int i = 0; i < n - 1; i++) { dx[i] = Math.Max(1e-9, p[i + 1][0] - p[i][0]); m[i] = (p[i + 1][1] - p[i][1]) / dx[i]; }
            t[0] = m[0]; t[n - 1] = m[n - 2];
            for (int i = 1; i < n - 1; i++) t[i] = m[i - 1] * m[i] <= 0 ? 0 : (m[i - 1] + m[i]) / 2;
            for (int i = 0; i < n - 1; i++)
            {
                if (m[i] == 0) { t[i] = t[i + 1] = 0; continue; }
                double a = t[i] / m[i], b = t[i + 1] / m[i], s = a * a + b * b;
                if (s > 9) { double tau = 3 / Math.Sqrt(s); t[i] = tau * a * m[i]; t[i + 1] = tau * b * m[i]; }
            }
            return x =>
            {
                x = AstroMath.Clamp(x, 0, 1);
                int i = 0;
                while (i < n - 2 && x > p[i + 1][0]) i++;
                double h = dx[i], u = (x - p[i][0]) / h;
                double h00 = (1 + 2 * u) * (1 - u) * (1 - u), h10 = u * (1 - u) * (1 - u), h01 = u * u * (3 - 2 * u), h11 = u * u * (u - 1);
                return AstroMath.Clamp(h00 * p[i][1] + h10 * h * t[i] + h01 * p[i + 1][1] + h11 * h * t[i + 1], 0, 1);
            };
        }

        public static void ApplyFunction(AstroImage img, Func<double, double> f, int channel = -1)
        {
            var lut = new float[65536];
            for (int i = 0; i < lut.Length; i++) lut[i] = (float)f(i / 65535.0);
            for (int c = 0; c < img.Channels; c++)
            {
                if (channel >= 0 && c != channel) continue;
                var d = img.Data[c];
                Parallel.For(0, img.Height, y =>
                {
                    int o = y * img.Width;
                    for (int x = 0; x < img.Width; x++)
                    {
                        float v = d[o + x];
                        if (v <= 0) d[o + x] = lut[0];
                        else if (v >= 1) d[o + x] = lut[65535];
                        else
                        {
                            float fi = v * 65535; int i0 = (int)fi; float fr = fi - i0;
                            d[o + x] = lut[i0] + (i0 < 65535 ? (lut[i0 + 1] - lut[i0]) * fr : 0);
                        }
                    }
                });
            }
        }

        /// <summary>Apply a function to luminance only, scaling RGB to preserve hue/saturation.</summary>
        public static void ApplyToLuminance(AstroImage img, Func<double, double> f)
        {
            if (img.Channels < 3) { ApplyFunction(img, f); return; }
            var r = img.Data[0]; var g = img.Data[1]; var b = img.Data[2];
            Parallel.For(0, img.Height, y =>
            {
                for (int x = 0; x < img.Width; x++)
                {
                    int i = y * img.Width + x;
                    double L = 0.2126 * r[i] + 0.7152 * g[i] + 0.0722 * b[i];
                    if (L <= 1e-9) continue;
                    double k = f(L) / L;
                    r[i] = (float)(r[i] * k); g[i] = (float)(g[i] * k); b[i] = (float)(b[i] * k);
                }
            });
        }

        /// <summary>Contrast-limited adaptive histogram equalisation on luminance (Zuiderveld 1994).</summary>
        public static void Clahe(AstroImage img, int tiles = 8, double clipLimit = 2.0, double amount = 1.0)
        {
            var L = img.Luminance();
            int w = img.Width, h = img.Height, bins = 256;
            int tx = tiles, ty = Math.Max(1, (int)Math.Round(tiles * (double)h / w));
            var maps = new float[tx * ty][];
            Parallel.For(0, tx * ty, t =>
            {
                int i = t % tx, j = t / tx;
                int x0 = i * w / tx, x1 = (i + 1) * w / tx, y0 = j * h / ty, y1 = (j + 1) * h / ty;
                var hist = new double[bins];
                int n = 0;
                for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++) { int b = (int)(AstroMath.Clamp(L[y * w + x], 0, 0.99999) * bins); hist[b]++; n++; }
                double limit = clipLimit * n / bins, excess = 0;
                for (int b = 0; b < bins; b++) if (hist[b] > limit) { excess += hist[b] - limit; hist[b] = limit; }
                for (int b = 0; b < bins; b++) hist[b] += excess / bins;
                var map = new float[bins];
                double acc = 0;
                for (int b = 0; b < bins; b++) { acc += hist[b]; map[b] = (float)(acc / n); }
                maps[t] = map;
            });
            var outL = new float[L.Length];
            Parallel.For(0, h, y =>
            {
                double gy = (y + 0.5) * ty / h - 0.5;
                int j0 = (int)Math.Floor(gy); double fy = gy - j0;
                int ja = Math.Max(0, Math.Min(ty - 1, j0)), jb = Math.Max(0, Math.Min(ty - 1, j0 + 1));
                for (int x = 0; x < w; x++)
                {
                    double gx = (x + 0.5) * tx / w - 0.5;
                    int i0 = (int)Math.Floor(gx); double fx = gx - i0;
                    int ia = Math.Max(0, Math.Min(tx - 1, i0)), ib = Math.Max(0, Math.Min(tx - 1, i0 + 1));
                    int b = (int)(AstroMath.Clamp(L[y * w + x], 0, 0.99999) * bins);
                    double v = maps[ja * tx + ia][b] * (1 - fx) * (1 - fy) + maps[ja * tx + ib][b] * fx * (1 - fy) + maps[jb * tx + ia][b] * (1 - fx) * fy + maps[jb * tx + ib][b] * fx * fy;
                    outL[y * w + x] = (float)(L[y * w + x] * (1 - amount) + v * amount);
                }
            });
            for (int c = 0; c < img.Channels; c++)
            {
                var d = img.Data[c];
                for (int i = 0; i < d.Length; i++) d[i] = img.Channels == 1 ? outL[i] : (L[i] > 1e-6 ? d[i] * outL[i] / L[i] : outL[i]);
            }
        }
    }
}
