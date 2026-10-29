using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Sidralune.Core.Mathematics;
using Sidralune.Imaging.Analysis;

namespace Sidralune.Imaging
{
    public enum BackgroundModel { Polynomial, RadialBasis }
    public enum BackgroundCorrection { Subtract, Divide }

    public class BackgroundSample
    {
        public double X, Y;
        public double[] Value; // per channel
        public bool Manual;
    }

    public class BackgroundOptions
    {
        public BackgroundModel Model = BackgroundModel.RadialBasis;
        public int PolynomialDegree = 2;
        /// <summary>Thin-plate spline smoothing (0 = interpolate exactly). Typical 0.1..1.</summary>
        public double Smoothing = 0.5;
        public int GridSize = 16;           // samples per axis for automatic placement
        public int SampleRadius = 12;       // half-size of sample box (pixels)
        public double Tolerance = 1.0;      // sigma tolerance for rejecting bright samples
        public BackgroundCorrection Correction = BackgroundCorrection.Subtract;
        public List<BackgroundSample> ManualSamples;
        public List<int[]> ExcludeRects;    // x, y, w, h
    }

    /// <summary>
    /// Gradient / light-pollution removal: automatic sample selection on a grid with robust statistics
    /// (rejecting samples on stars and nebulosity), then a polynomial or thin-plate spline RBF surface model,
    /// in the spirit of GraXpert and PixInsight DBE/ABE.
    /// </summary>
    public static class BackgroundExtraction
    {
        public static List<BackgroundSample> AutoSamples(AstroImage img, BackgroundOptions opt)
        {
            int w = img.Width, h = img.Height, ch = img.Channels;
            var lum = img.Luminance();
            var global = ImageStatistics.Compute(lum, w, 0);
            var samples = new List<BackgroundSample>();
            int r = opt.SampleRadius;
            int n = Math.Max(4, opt.GridSize);
            var lumMed = new List<double>();
            for (int j = 0; j < n; j++)
                for (int i = 0; i < n; i++)
                {
                    int cx = (int)((i + 0.5) * w / n), cy = (int)((j + 0.5) * h / n);
                    if (opt.ExcludeRects != null && opt.ExcludeRects.Any(e => cx >= e[0] && cy >= e[1] && cx < e[0] + e[2] && cy < e[1] + e[3])) continue;
                    var s = new BackgroundSample { X = cx, Y = cy, Value = new double[ch] };
                    var buf = new List<double>();
                    bool ok = true;
                    for (int c = 0; c < ch && ok; c++)
                    {
                        buf.Clear();
                        for (int y = Math.Max(0, cy - r); y < Math.Min(h, cy + r); y++)
                            for (int x = Math.Max(0, cx - r); x < Math.Min(w, cx + r); x++)
                            {
                                float v = img.Data[c][y * w + x];
                                if (!float.IsNaN(v) && v > 0) buf.Add(v);
                            }
                        if (buf.Count < r * r) { ok = false; break; }
                        double mean, sig; int used;
                        Stats.SigmaClip(buf, 2.0, 4, out mean, out sig, out used);
                        s.Value[c] = Stats.Median(buf);
                    }
                    if (!ok) continue;
                    samples.Add(s);
                    lumMed.Add(ch >= 3 ? 0.2126 * s.Value[0] + 0.7152 * s.Value[1] + 0.0722 * s.Value[2] : s.Value[0]);
                }
            // reject samples sitting on objects: iteratively compare to a smooth low-order fit of the samples
            var keep = Enumerable.Repeat(true, samples.Count).ToArray();
            for (int it = 0; it < 4; it++)
            {
                var xs = new List<double>(); var ys = new List<double>(); var vs = new List<double>(); var idx = new List<int>();
                for (int k = 0; k < samples.Count; k++) if (keep[k]) { xs.Add(samples[k].X / w); ys.Add(samples[k].Y / h); vs.Add(lumMed[k]); idx.Add(k); }
                if (vs.Count < 8) break;
                var coef = FitPoly2D(xs, ys, vs, 2);
                if (coef == null) break;
                var res = new List<double>();
                for (int q = 0; q < xs.Count; q++) res.Add(vs[q] - EvalPoly2D(coef, xs[q], ys[q], 2));
                double med = Stats.Median(res), mad = Stats.Mad(res, med) * Stats.MadToSigma;
                bool changed = false;
                for (int q = 0; q < res.Count; q++)
                    if (res[q] - med > opt.Tolerance * 2.0 * Math.Max(mad, global.Noise)) { keep[idx[q]] = false; changed = true; }
                if (!changed) break;
            }
            var result = new List<BackgroundSample>();
            for (int k = 0; k < samples.Count; k++) if (keep[k]) result.Add(samples[k]);
            if (opt.ManualSamples != null) result.AddRange(opt.ManualSamples);
            return result;
        }

        /// <summary>Compute the background model image for the given samples.</summary>
        public static AstroImage Model(AstroImage img, List<BackgroundSample> samples, BackgroundOptions opt, System.Threading.CancellationToken ct = default(System.Threading.CancellationToken))
        {
            int w = img.Width, h = img.Height, ch = img.Channels;
            var model = new AstroImage(w, h, ch);
            var po = new ParallelOptions { CancellationToken = ct };
            for (int c = 0; c < ch; c++)
            {
                ct.ThrowIfCancellationRequested();
                var xs = samples.Select(s => s.X / w).ToList();
                var ys = samples.Select(s => s.Y / h).ToList();
                var vs = samples.Select(s => s.Value[c]).ToList();
                var d = model.Data[c];
                if (opt.Model == BackgroundModel.Polynomial || samples.Count < 6)
                {
                    int deg = Math.Max(1, Math.Min(opt.PolynomialDegree, 6));
                    var coef = FitPoly2D(xs, ys, vs, deg);
                    if (coef == null) continue;
                    Parallel.For(0, h, po, y => { double yy = (double)y / h; for (int x = 0; x < w; x++) d[y * w + x] = (float)EvalPoly2D(coef, (double)x / w, yy, deg); });
                }
                else
                {
                    var tps = new ThinPlateSpline(xs, ys, vs, opt.Smoothing);
                    // evaluate on a coarse grid then interpolate (the surface is smooth)
                    int gx = Math.Min(w, 128), gy = Math.Min(h, Math.Max(2, 128 * h / Math.Max(1, w)));
                    var grid = new float[gx * gy];
                    Parallel.For(0, gy, po, j => { for (int i = 0; i < gx; i++) grid[j * gx + i] = (float)tps.Evaluate((i + 0.5) / gx, (j + 0.5) / gy); });
                    var full = BackgroundMesh.Interpolate(grid, gx, gy, w, h);
                    Array.Copy(full, d, d.Length);
                }
            }
            return model;
        }

        /// <summary>Remove the background; returns the model that was removed.</summary>
        public static AstroImage Apply(AstroImage img, BackgroundOptions opt, List<BackgroundSample> samples = null, System.Threading.CancellationToken ct = default(System.Threading.CancellationToken))
        {
            samples = samples ?? AutoSamples(img, opt);
            var model = Model(img, samples, opt, ct);
            for (int c = 0; c < img.Channels; c++)
            {
                var d = img.Data[c]; var m = model.Data[c];
                double mean = m.Average(v => (double)v);
                if (opt.Correction == BackgroundCorrection.Subtract)
                {
                    // keep the mean background level (avoid negative pixels), like DBE "normalize"
                    float off = (float)mean;
                    for (int i = 0; i < d.Length; i++) d[i] = d[i] - m[i] + off * 0.1f;
                }
                else for (int i = 0; i < d.Length; i++) d[i] = m[i] > 1e-9 ? (float)(d[i] / m[i] * mean) : d[i];
            }
            img.Header.AddHistory("Background extraction (" + opt.Model + ", " + samples.Count + " samples)");
            return model;
        }

        private static int PolyTerms(int deg) => (deg + 1) * (deg + 2) / 2;

        public static double[] FitPoly2D(IList<double> x, IList<double> y, IList<double> v, int deg)
        {
            int nt = PolyTerms(deg), n = x.Count;
            if (n < nt) return null;
            var D = new double[n, nt];
            for (int i = 0; i < n; i++)
            {
                int k = 0;
                for (int a = 0; a <= deg; a++) for (int b = 0; b <= deg - a; b++) D[i, k++] = Math.Pow(x[i] - 0.5, a) * Math.Pow(y[i] - 0.5, b);
            }
            return LinAlg.LeastSquares(D, v.ToArray());
        }

        public static double EvalPoly2D(double[] c, double x, double y, int deg)
        {
            double s = 0; int k = 0;
            for (int a = 0; a <= deg; a++) for (int b = 0; b <= deg - a; b++) s += c[k++] * Math.Pow(x - 0.5, a) * Math.Pow(y - 0.5, b);
            return s;
        }
    }

    /// <summary>Smoothing thin-plate spline in 2D: f(x) = a0 + a1 x + a2 y + Σ w_i φ(|x - x_i|), φ(r) = r² log r.</summary>
    public class ThinPlateSpline
    {
        private readonly double[] _x, _y, _w;
        private readonly double _a0, _a1, _a2;

        private static double Phi(double r2) => r2 > 1e-20 ? 0.5 * r2 * Math.Log(r2) : 0;

        public ThinPlateSpline(IList<double> x, IList<double> y, IList<double> v, double smoothing)
        {
            int n = x.Count;
            _x = x.ToArray(); _y = y.ToArray();
            var A = new double[n + 3, n + 3];
            var b = new double[n + 3];
            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j < n; j++) A[i, j] = Phi(Sq(_x[i] - _x[j]) + Sq(_y[i] - _y[j]));
                A[i, i] += smoothing * 0.01 * n;
                A[i, n] = 1; A[i, n + 1] = _x[i]; A[i, n + 2] = _y[i];
                A[n, i] = 1; A[n + 1, i] = _x[i]; A[n + 2, i] = _y[i];
                b[i] = v[i];
            }
            var sol = LinAlg.Solve(A, b) ?? new double[n + 3];
            _w = new double[n];
            Array.Copy(sol, _w, n);
            _a0 = sol[n]; _a1 = sol[n + 1]; _a2 = sol[n + 2];
        }

        private static double Sq(double v) => v * v;

        public double Evaluate(double x, double y)
        {
            double s = _a0 + _a1 * x + _a2 * y;
            for (int i = 0; i < _w.Length; i++) s += _w[i] * Phi(Sq(x - _x[i]) + Sq(y - _y[i]));
            return s;
        }
    }
}
