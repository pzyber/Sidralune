using System;
using System.Collections.Generic;

namespace Sidralune.Core.Mathematics
{
    /// <summary>Model function: given parameters p and independent index i returns predicted value.</summary>
    public delegate double ModelFunction(double[] p, int i);

    /// <summary>Result of a non-linear least-squares fit.</summary>
    public class FitResult
    {
        public double[] Parameters;
        public double[] Errors;
        public double ChiSquare;
        public double ReducedChiSquare;
        public int Iterations;
        public bool Converged;
    }

    /// <summary>Levenberg-Marquardt with numerical Jacobian, bounds by clamping, and covariance-based errors.</summary>
    public static class LevenbergMarquardt
    {
        public static FitResult Fit(ModelFunction model, int nObs, double[] y, double[] sigma, double[] p0,
            double[] lower = null, double[] upper = null, int maxIter = 200, double tol = 1e-10)
        {
            int np = p0.Length;
            var p = (double[])p0.Clone();
            double lambda = 1e-3;
            Func<double[], double> chi2 = q =>
            {
                double s = 0;
                for (int i = 0; i < nObs; i++)
                {
                    double r = (y[i] - model(q, i)) / (sigma != null ? sigma[i] : 1.0);
                    s += r * r;
                }
                return s;
            };
            Clamp(p, lower, upper);
            double c = chi2(p);
            var J = new double[nObs, np];
            var r0 = new double[nObs];
            int it;
            bool converged = false;
            for (it = 0; it < maxIter; it++)
            {
                for (int i = 0; i < nObs; i++)
                {
                    double s = sigma != null ? sigma[i] : 1.0;
                    r0[i] = (y[i] - model(p, i)) / s;
                }
                for (int j = 0; j < np; j++)
                {
                    double h = Math.Max(1e-8, Math.Abs(p[j]) * 1e-6);
                    var pp = (double[])p.Clone(); pp[j] += h;
                    var pm = (double[])p.Clone(); pm[j] -= h;
                    for (int i = 0; i < nObs; i++)
                    {
                        double s = sigma != null ? sigma[i] : 1.0;
                        J[i, j] = (model(pp, i) - model(pm, i)) / (2 * h * s);
                    }
                }
                var JtJ = new double[np, np];
                var Jtr = new double[np];
                for (int a = 0; a < np; a++)
                {
                    for (int b = a; b < np; b++)
                    {
                        double s = 0;
                        for (int i = 0; i < nObs; i++) s += J[i, a] * J[i, b];
                        JtJ[a, b] = JtJ[b, a] = s;
                    }
                    double t = 0;
                    for (int i = 0; i < nObs; i++) t += J[i, a] * r0[i];
                    Jtr[a] = t;
                }
                bool improved = false;
                for (int inner = 0; inner < 20; inner++)
                {
                    var A = (double[,])JtJ.Clone();
                    for (int a = 0; a < np; a++) A[a, a] *= (1 + lambda);
                    for (int a = 0; a < np; a++) if (A[a, a] == 0) A[a, a] = lambda;
                    var dp = LinAlg.Solve(A, Jtr);
                    if (dp == null) { lambda *= 10; continue; }
                    var pn = new double[np];
                    for (int a = 0; a < np; a++) pn[a] = p[a] + dp[a];
                    Clamp(pn, lower, upper);
                    double cn = chi2(pn);
                    if (cn < c)
                    {
                        double rel = (c - cn) / Math.Max(c, 1e-300);
                        p = pn; c = cn; lambda = Math.Max(lambda / 10, 1e-12); improved = true;
                        if (rel < tol) converged = true;
                        break;
                    }
                    lambda *= 10;
                    if (lambda > 1e12) break;
                }
                if (!improved || converged) { converged = true; break; }
            }
            // covariance
            var JtJf = new double[np, np];
            for (int a = 0; a < np; a++)
                for (int b = 0; b < np; b++)
                {
                    double s = 0;
                    for (int i = 0; i < nObs; i++) s += J[i, a] * J[i, b];
                    JtJf[a, b] = s;
                }
            var cov = LinAlg.Inverse(JtJf);
            int dof = Math.Max(1, nObs - np);
            double red = c / dof;
            var err = new double[np];
            for (int a = 0; a < np; a++)
            {
                double v = cov != null ? cov[a, a] : double.NaN;
                // if no sigma given, scale by reduced chi2
                if (sigma == null) v *= red;
                err[a] = v > 0 ? Math.Sqrt(v) : double.NaN;
            }
            return new FitResult { Parameters = p, Errors = err, ChiSquare = c, ReducedChiSquare = red, Iterations = it, Converged = converged };
        }

        private static void Clamp(double[] p, double[] lo, double[] hi)
        {
            for (int i = 0; i < p.Length; i++)
            {
                if (lo != null && !double.IsNaN(lo[i]) && p[i] < lo[i]) p[i] = lo[i];
                if (hi != null && !double.IsNaN(hi[i]) && p[i] > hi[i]) p[i] = hi[i];
            }
        }
    }

    /// <summary>Nelder-Mead downhill simplex minimiser for derivative-free problems.</summary>
    public static class NelderMead
    {
        public static double[] Minimize(Func<double[], double> f, double[] x0, double[] step, int maxIter = 2000, double tol = 1e-10)
        {
            int n = x0.Length;
            var simplex = new double[n + 1][];
            var fv = new double[n + 1];
            simplex[0] = (double[])x0.Clone();
            for (int i = 0; i < n; i++)
            {
                simplex[i + 1] = (double[])x0.Clone();
                simplex[i + 1][i] += step != null ? step[i] : (Math.Abs(x0[i]) > 0 ? 0.05 * x0[i] : 0.00025);
            }
            for (int i = 0; i <= n; i++) fv[i] = f(simplex[i]);
            for (int it = 0; it < maxIter; it++)
            {
                Array.Sort(fv, simplex);
                if (Math.Abs(fv[n] - fv[0]) <= tol * (Math.Abs(fv[0]) + Math.Abs(fv[n]) + 1e-30)) break;
                var cen = new double[n];
                for (int i = 0; i < n; i++) for (int j = 0; j < n; j++) cen[j] += simplex[i][j] / n;
                Func<double, double[]> along = t =>
                {
                    var r = new double[n];
                    for (int j = 0; j < n; j++) r[j] = cen[j] + t * (simplex[n][j] - cen[j]);
                    return r;
                };
                var xr = along(-1); double fr = f(xr);
                if (fr < fv[0])
                {
                    var xe = along(-2); double fe = f(xe);
                    if (fe < fr) { simplex[n] = xe; fv[n] = fe; } else { simplex[n] = xr; fv[n] = fr; }
                }
                else if (fr < fv[n - 1]) { simplex[n] = xr; fv[n] = fr; }
                else
                {
                    var xc = fr < fv[n] ? along(-0.5) : along(0.5);
                    double fc = f(xc);
                    if (fc < Math.Min(fr, fv[n])) { simplex[n] = xc; fv[n] = fc; }
                    else
                    {
                        for (int i = 1; i <= n; i++)
                        {
                            for (int j = 0; j < n; j++) simplex[i][j] = simplex[0][j] + 0.5 * (simplex[i][j] - simplex[0][j]);
                            fv[i] = f(simplex[i]);
                        }
                    }
                }
            }
            Array.Sort(fv, simplex);
            return simplex[0];
        }
    }

    /// <summary>
    /// Affine-invariant ensemble MCMC sampler (Goodman &amp; Weare 2010, as popularised by emcee) for posterior estimation.
    /// </summary>
    public class EnsembleSampler
    {
        private readonly Func<double[], double> _logProb;
        private readonly int _walkers, _dim;
        private readonly Random _rng;
        public double StretchA = 2.0;

        public EnsembleSampler(int walkers, int dim, Func<double[], double> logProb, int seed = 12345)
        {
            _walkers = Math.Max(walkers, 2 * dim + 2);
            _dim = dim;
            _logProb = logProb;
            _rng = new Random(seed);
        }

        public double AcceptanceFraction { get; private set; }

        /// <summary>Runs the chain; returns flattened samples after burn-in.</summary>
        public List<double[]> Run(double[] start, double[] scatter, int steps, int burn, Action<int> progress = null)
        {
            var pos = new double[_walkers][];
            var lp = new double[_walkers];
            for (int k = 0; k < _walkers; k++)
            {
                pos[k] = new double[_dim];
                for (int d = 0; d < _dim; d++) pos[k][d] = start[d] + scatter[d] * Gauss();
                lp[k] = _logProb(pos[k]);
            }
            var samples = new List<double[]>();
            long acc = 0, tot = 0;
            for (int s = 0; s < steps; s++)
            {
                for (int k = 0; k < _walkers; k++)
                {
                    int j;
                    do j = _rng.Next(_walkers); while (j == k);
                    double z = Math.Pow((StretchA - 1) * _rng.NextDouble() + 1, 2) / StretchA;
                    var y = new double[_dim];
                    for (int d = 0; d < _dim; d++) y[d] = pos[j][d] + z * (pos[k][d] - pos[j][d]);
                    double lpy = _logProb(y);
                    double q = (_dim - 1) * Math.Log(z) + lpy - lp[k];
                    tot++;
                    if (!double.IsNaN(lpy) && Math.Log(_rng.NextDouble()) < q) { pos[k] = y; lp[k] = lpy; acc++; }
                }
                if (s >= burn) for (int k = 0; k < _walkers; k++) samples.Add((double[])pos[k].Clone());
                if (progress != null && s % 50 == 0) progress(s);
            }
            AcceptanceFraction = tot > 0 ? (double)acc / tot : 0;
            return samples;
        }

        private double Gauss()
        {
            double u1 = 1.0 - _rng.NextDouble(), u2 = _rng.NextDouble();
            return Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
        }

        /// <summary>Median and 16/84 percentile half-width per parameter.</summary>
        public static void Summarize(List<double[]> samples, int dim, out double[] median, out double[] lo, out double[] hi)
        {
            median = new double[dim]; lo = new double[dim]; hi = new double[dim];
            var col = new List<double>(samples.Count);
            for (int d = 0; d < dim; d++)
            {
                col.Clear();
                foreach (var s in samples) col.Add(s[d]);
                median[d] = Stats.Percentile(col, 50);
                lo[d] = median[d] - Stats.Percentile(col, 15.865);
                hi[d] = Stats.Percentile(col, 84.135) - median[d];
            }
        }
    }

    /// <summary>Root finding and extremum search helpers used by event finders (rise/set, phases, conjunctions).</summary>
    public static class Roots
    {
        /// <summary>Brent's method root in [a,b]; f(a) and f(b) must bracket.</summary>
        public static double Brent(Func<double, double> f, double a, double b, double tol = 1e-9, int maxIter = 100)
        {
            double fa = f(a), fb = f(b);
            if (fa * fb > 0) return double.NaN;
            if (Math.Abs(fa) < Math.Abs(fb)) { double t = a; a = b; b = t; t = fa; fa = fb; fb = t; }
            double c = a, fc = fa, d = b - a, s = b;
            bool mflag = true;
            for (int i = 0; i < maxIter; i++)
            {
                if (fb == 0 || Math.Abs(b - a) < tol) return b;
                if (fa != fc && fb != fc)
                    s = a * fb * fc / ((fa - fb) * (fa - fc)) + b * fa * fc / ((fb - fa) * (fb - fc)) + c * fa * fb / ((fc - fa) * (fc - fb));
                else s = b - fb * (b - a) / (fb - fa);
                bool cond1 = !((s > (3 * a + b) / 4 && s < b) || (s < (3 * a + b) / 4 && s > b));
                bool cond2 = mflag && Math.Abs(s - b) >= Math.Abs(b - c) / 2;
                bool cond3 = !mflag && Math.Abs(s - b) >= Math.Abs(c - d) / 2;
                if (cond1 || cond2 || cond3) { s = (a + b) / 2; mflag = true; } else mflag = false;
                double fs = f(s);
                d = c; c = b; fc = fb;
                if (fa * fs < 0) { b = s; fb = fs; } else { a = s; fa = fs; }
                if (Math.Abs(fa) < Math.Abs(fb)) { double t = a; a = b; b = t; t = fa; fa = fb; fb = t; }
            }
            return b;
        }

        /// <summary>Scan [t0,t1] with step, returning all roots found by bracketing then Brent refinement.</summary>
        public static List<double> FindAll(Func<double, double> f, double t0, double t1, double step, double tol = 1e-7)
        {
            var res = new List<double>();
            double a = t0, fa = f(a);
            while (a < t1)
            {
                double b = Math.Min(a + step, t1), fb = f(b);
                if (!double.IsNaN(fa) && !double.IsNaN(fb) && fa * fb <= 0 && !(fa == 0 && fb == 0))
                {
                    double r = Brent(f, a, b, tol);
                    if (!double.IsNaN(r) && (res.Count == 0 || Math.Abs(r - res[res.Count - 1]) > tol * 10)) res.Add(r);
                }
                a = b; fa = fb;
            }
            return res;
        }

        /// <summary>Golden section search for a minimum in [a,b].</summary>
        public static double GoldenMin(Func<double, double> f, double a, double b, double tol = 1e-8)
        {
            double gr = (Math.Sqrt(5) - 1) / 2;
            double c = b - gr * (b - a), d = a + gr * (b - a);
            double fc = f(c), fd = f(d);
            while (Math.Abs(b - a) > tol)
            {
                if (fc < fd) { b = d; d = c; fd = fc; c = b - gr * (b - a); fc = f(c); }
                else { a = c; c = d; fc = fd; d = a + gr * (b - a); fd = f(d); }
            }
            return 0.5 * (a + b);
        }
    }

    /// <summary>Natural cubic spline interpolation.</summary>
    public class CubicSpline
    {
        private readonly double[] _x, _y, _m;

        public CubicSpline(IList<double> x, IList<double> y)
        {
            int n = x.Count;
            _x = new double[n]; _y = new double[n];
            for (int i = 0; i < n; i++) { _x[i] = x[i]; _y[i] = y[i]; }
            Array.Sort(_x, _y);
            _m = new double[n];
            if (n < 3) return;
            var a = new double[n]; var b = new double[n]; var c = new double[n]; var d = new double[n];
            for (int i = 1; i < n - 1; i++)
            {
                double h0 = _x[i] - _x[i - 1], h1 = _x[i + 1] - _x[i];
                a[i] = h0; b[i] = 2 * (h0 + h1); c[i] = h1;
                d[i] = 6 * ((_y[i + 1] - _y[i]) / h1 - (_y[i] - _y[i - 1]) / h0);
            }
            b[0] = b[n - 1] = 1;
            for (int i = 1; i < n; i++)
            {
                double w = a[i] / b[i - 1];
                b[i] -= w * c[i - 1];
                d[i] -= w * d[i - 1];
            }
            _m[n - 1] = d[n - 1] / b[n - 1];
            for (int i = n - 2; i >= 0; i--) _m[i] = (d[i] - c[i] * _m[i + 1]) / b[i];
        }

        public double Evaluate(double x)
        {
            int n = _x.Length;
            if (n == 0) return double.NaN;
            if (n == 1) return _y[0];
            if (n == 2) return _y[0] + (_y[1] - _y[0]) * (x - _x[0]) / (_x[1] - _x[0]);
            int lo = 0, hi = n - 1;
            if (x <= _x[0]) { lo = 0; hi = 1; }
            else if (x >= _x[n - 1]) { lo = n - 2; hi = n - 1; }
            else
                while (hi - lo > 1)
                {
                    int mid = (lo + hi) >> 1;
                    if (_x[mid] > x) hi = mid; else lo = mid;
                }
            double h = _x[hi] - _x[lo];
            double A = (_x[hi] - x) / h, B = (x - _x[lo]) / h;
            return A * _y[lo] + B * _y[hi] + ((A * A * A - A) * _m[lo] + (B * B * B - B) * _m[hi]) * h * h / 6.0;
        }
    }
}
