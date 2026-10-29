using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Sidralune.Core.Mathematics;

namespace Sidralune.Science.Photometry
{
    public class Periodogram
    {
        public double[] Frequencies;  // cycles/day
        public double[] Power;
        public double BestPeriod;
        public double BestPower;
        public double FalseAlarmProbability = double.NaN;
        public string Method;
    }

    /// <summary>Period search algorithms for variable stars and exoplanets.</summary>
    public static class PeriodSearch
    {
        public static double[] FrequencyGrid(IList<double> t, double minPeriod, double maxPeriod, double oversample = 10)
        {
            double span = t.Max() - t.Min();
            double fmin = 1 / maxPeriod, fmax = 1 / minPeriod;
            double df = 1 / (span * oversample);
            int n = (int)Math.Min(2000000, Math.Max(10, (fmax - fmin) / df));
            var f = new double[n];
            for (int i = 0; i < n; i++) f[i] = fmin + i * (fmax - fmin) / (n - 1);
            return f;
        }

        /// <summary>Generalised (floating-mean, weighted) Lomb-Scargle periodogram (Zechmeister &amp; Kürster 2009).</summary>
        public static Periodogram LombScargle(IList<double> t, IList<double> y, IList<double> dy, double minPeriod, double maxPeriod, double oversample = 10, System.Threading.CancellationToken ct = default(System.Threading.CancellationToken))
        {
            var f = FrequencyGrid(t, minPeriod, maxPeriod, oversample);
            int n = t.Count;
            var w = new double[n];
            double W = 0;
            for (int i = 0; i < n; i++) { double e = dy != null && dy[i] > 0 ? dy[i] : 1; w[i] = 1 / (e * e); W += w[i]; }
            for (int i = 0; i < n; i++) w[i] /= W;
            double Y = 0; for (int i = 0; i < n; i++) Y += w[i] * y[i];
            double YY = 0; for (int i = 0; i < n; i++) YY += w[i] * (y[i] - Y) * (y[i] - Y);
            var p = new double[f.Length];
            Parallel.For(0, f.Length, new ParallelOptions { CancellationToken = ct }, k =>
            {
                double om = 2 * Math.PI * f[k];
                double C = 0, S = 0, YC = 0, YS = 0, CC = 0, SS = 0, CS = 0;
                for (int i = 0; i < n; i++)
                {
                    double c = Math.Cos(om * t[i]), s = Math.Sin(om * t[i]);
                    C += w[i] * c; S += w[i] * s;
                    YC += w[i] * (y[i] - Y) * c; YS += w[i] * (y[i] - Y) * s;
                    CC += w[i] * c * c; SS += w[i] * s * s; CS += w[i] * c * s;
                }
                double cc = CC - C * C, ss = SS - S * S, cs = CS - C * S;
                double D = cc * ss - cs * cs;
                p[k] = D > 0 ? (ss * YC * YC + cc * YS * YS - 2 * cs * YC * YS) / (YY * D) : 0;
            });
            var res = new Periodogram { Frequencies = f, Power = p, Method = "Generalised Lomb-Scargle" };
            int best = Array.IndexOf(p, p.Max());
            res.BestPeriod = 1 / f[best]; res.BestPower = p[best];
            // Baluev (2008)-style approximation of FAP via effective number of independent frequencies
            double M = (t.Max() - t.Min()) * (f[f.Length - 1] - f[0]);
            double prob = Math.Pow(1 - res.BestPower, (n - 3) / 2.0);
            res.FalseAlarmProbability = Math.Min(1, M * prob);
            return res;
        }

        /// <summary>Box Least Squares transit search (Kovács, Zucker &amp; Mazeh 2002). Durations in days.</summary>
        public static Periodogram Bls(IList<double> t, IList<double> y, double minPeriod, double maxPeriod, double[] durations = null, int bins = 300, double oversample = 3, System.Threading.CancellationToken ct = default(System.Threading.CancellationToken))
        {
            durations = durations ?? new[] { 0.04, 0.06, 0.08, 0.12, 0.16 };
            var f = FrequencyGrid(t, minPeriod, maxPeriod, oversample);
            int n = t.Count;
            double mean = y.Average();
            var yy = y.Select(v => v - mean).ToArray();
            double t0 = t.Min();
            var p = new double[f.Length];
            var bestT0 = new double[f.Length];
            var bestDur = new double[f.Length];
            Parallel.For(0, f.Length, new ParallelOptions { CancellationToken = ct }, () => new double[2][] { new double[bins], new double[bins] }, (k, st, buf) =>
            {
                double P = 1 / f[k];
                var bs = buf[0]; var bc = buf[1];
                Array.Clear(bs, 0, bins); Array.Clear(bc, 0, bins);
                for (int i = 0; i < n; i++)
                {
                    double ph = ((t[i] - t0) / P) % 1;
                    if (ph < 0) ph += 1;
                    int b = Math.Min(bins - 1, (int)(ph * bins));
                    bs[b] += yy[i]; bc[b] += 1;
                }
                double best = 0, bt = 0, bd = 0;
                foreach (var dur in durations)
                {
                    int q = Math.Max(1, (int)Math.Round(dur / P * bins));
                    if (q >= bins / 2) continue;
                    for (int start = 0; start < bins; start++)
                    {
                        double s = 0, r = 0;
                        for (int j = 0; j < q; j++) { int b = (start + j) % bins; s += bs[b]; r += bc[b]; }
                        if (r < 3 || r > n - 3) continue;
                        double sr = s * s / (r * (n - r)) * n; // signal residue (dip -> s negative)
                        if (s < 0 && sr > best) { best = sr; bt = (start + q / 2.0) / bins; bd = dur; }
                    }
                }
                p[k] = best; bestT0[k] = t0 + bt * P; bestDur[k] = bd;
                return buf;
            }, buf => { });
            var res = new Periodogram { Frequencies = f, Power = p, Method = "Box Least Squares" };
            int bi = Array.IndexOf(p, p.Max());
            res.BestPeriod = 1 / f[bi]; res.BestPower = p[bi];
            return res;
        }

        /// <summary>Phase dispersion minimisation (Stellingwerf 1978); returns Θ (lower is better) as 1-Θ in Power.</summary>
        public static Periodogram Pdm(IList<double> t, IList<double> y, double minPeriod, double maxPeriod, int bins = 10, double oversample = 5, System.Threading.CancellationToken ct = default(System.Threading.CancellationToken))
        {
            var f = FrequencyGrid(t, minPeriod, maxPeriod, oversample);
            int n = t.Count;
            double mean = y.Average(), var = y.Sum(v => (v - mean) * (v - mean)) / (n - 1);
            var p = new double[f.Length];
            Parallel.For(0, f.Length, new ParallelOptions { CancellationToken = ct }, k =>
            {
                double P = 1 / f[k];
                var s = new double[bins]; var s2 = new double[bins]; var c = new int[bins];
                for (int i = 0; i < n; i++)
                {
                    double ph = (t[i] / P) % 1; if (ph < 0) ph += 1;
                    int b = Math.Min(bins - 1, (int)(ph * bins));
                    s[b] += y[i]; s2[b] += y[i] * y[i]; c[b]++;
                }
                double num = 0; int dof = 0;
                for (int b = 0; b < bins; b++)
                {
                    if (c[b] < 2) continue;
                    double m = s[b] / c[b];
                    num += s2[b] - c[b] * m * m;
                    dof += c[b] - 1;
                }
                double theta = dof > 0 ? (num / dof) / var : 1;
                p[k] = 1 - theta;
            });
            var res = new Periodogram { Frequencies = f, Power = p, Method = "Phase dispersion minimisation" };
            int bi = Array.IndexOf(p, p.Max());
            res.BestPeriod = 1 / f[bi]; res.BestPower = p[bi];
            return res;
        }

        public static double[] Phase(IList<double> t, double period, double epoch)
        {
            var ph = new double[t.Count];
            for (int i = 0; i < t.Count; i++) { double x = ((t[i] - epoch) / period) % 1; ph[i] = x < 0 ? x + 1 : x; }
            return ph;
        }

        /// <summary>
        /// Time of minimum by the Kwee &amp; van Woerden (1956) method: symmetric fold around trial times, quadratic fit of
        /// S(T) and its minimum; returns time and formal error.
        /// </summary>
        public static double KweeVanWoerden(IList<double> t, IList<double> mag, out double error)
        {
            int n = t.Count;
            error = double.NaN;
            if (n < 7) return double.NaN;
            var idx = Enumerable.Range(0, n).OrderBy(i => t[i]).ToArray();
            var T = idx.Select(i => t[i]).ToArray(); var M = idx.Select(i => mag[i]).ToArray();
            // interpolate onto an even grid
            int N = Math.Max(21, n | 1);
            double dt = (T[n - 1] - T[0]) / (N - 1);
            var g = new double[N];
            var spline = new CubicSpline(T, M);
            for (int i = 0; i < N; i++) g[i] = spline.Evaluate(T[0] + i * dt);
            int imax = Array.IndexOf(g, g.Max()); // magnitudes: minimum light = maximum mag
            Func<double, double> S = centre =>
            {
                double s = 0; int cnt = 0;
                for (int k = 1; k < N; k++)
                {
                    double a = centre - k * dt, b = centre + k * dt;
                    if (a < T[0] || b > T[n - 1]) break;
                    double d = spline.Evaluate(a) - spline.Evaluate(b);
                    s += d * d; cnt++;
                }
                return cnt > 0 ? s / cnt : double.MaxValue;
            };
            double c0 = T[0] + imax * dt;
            double s1 = S(c0 - dt), s2 = S(c0), s3 = S(c0 + dt);
            double A = (s1 + s3 - 2 * s2) / (2 * dt * dt), B = (s3 - s1) / (2 * dt), C = s2;
            double tmin = A > 0 ? c0 - B / (2 * A) : c0;
            int Z = Math.Max(2, N / 4);
            if (A > 0) error = Math.Sqrt(Math.Max(0, (4 * A * C - B * B) / (4 * A * A * (Z - 1))));
            return tmin;
        }
    }

    /// <summary>
    /// Transit light curve with quadratic limb darkening, computed by radial integration of the occulted stellar disc
    /// (accurate to ~1e-6, equivalent to Mandel &amp; Agol 2002 for small planets and valid for any size ratio).
    /// </summary>
    public class TransitModel
    {
        public double T0, Period, Rp = 0.1, A = 10, Inclination = 89.0, U1 = 0.4, U2 = 0.25, Baseline = 1, Slope = 0;
        public double Eccentricity = 0, Omega = 90;
        public int Steps = 600;

        public double ImpactParameter => A * Math.Cos(Inclination * AstroMath.Deg2Rad);

        /// <summary>Total transit duration T14 (days, circular orbit).</summary>
        public double Duration
        {
            get
            {
                double b = ImpactParameter;
                double arg = Math.Sqrt(Math.Max(0, (1 + Rp) * (1 + Rp) - b * b)) / (A * Math.Sin(Inclination * AstroMath.Deg2Rad));
                return Period / Math.PI * Math.Asin(Math.Min(1, arg));
            }
        }

        /// <summary>Fractional transit depth at mid-transit.</summary>
        public double Depth => 1 - Flux(T0) / Baseline;

        /// <summary>Projected separation in stellar radii.</summary>
        public double Separation(double t)
        {
            double ph = 2 * Math.PI * (t - T0) / Period;
            double inc = Inclination * AstroMath.Deg2Rad;
            double z = A * Math.Sqrt(Math.Pow(Math.Sin(ph), 2) + Math.Pow(Math.Cos(inc) * Math.Cos(ph), 2));
            // planet behind the star: no transit
            if (Math.Cos(ph) < 0) return 1e9;
            return z;
        }

        public double Flux(double t) => (Baseline + Slope * (t - T0)) * (1 - Occulted(Separation(t), Rp, U1, U2, Steps));

        /// <summary>Fraction of stellar flux occulted at separation z for radius ratio p.</summary>
        public static double Occulted(double z, double p, double u1, double u2, int steps = 600)
        {
            if (z >= 1 + p || p <= 0) return 0;
            double total = Math.PI * (1 - u1 / 3 - u2 / 6);
            double rmin = Math.Max(0, z - p), rmax = Math.Min(1, z + p);
            double sum = 0;
            int n = steps;
            double dr = (rmax - rmin) / n;
            for (int i = 0; i < n; i++)
            {
                double r = rmin + (i + 0.5) * dr;
                double mu = Math.Sqrt(Math.Max(0, 1 - r * r));
                double I = 1 - u1 * (1 - mu) - u2 * (1 - mu) * (1 - mu);
                double frac;
                if (r <= p - z) frac = 1;
                else if (z < 1e-12) frac = r < p ? 1 : 0;
                else
                {
                    double c = (r * r + z * z - p * p) / (2 * r * z);
                    frac = c >= 1 ? 0 : c <= -1 ? 1 : Math.Acos(c) / Math.PI;
                }
                sum += I * 2 * Math.PI * r * frac * dr;
            }
            return sum / total;
        }

        public TransitModel Clone() => (TransitModel)MemberwiseClone();

        /// <summary>Fit parameters to data (T0, Rp, A, Inclination, Baseline, Slope; period and limb darkening fixed).</summary>
        public FitResult Fit(IList<double> t, IList<double> f, IList<double> df, bool fitLimbDarkening = false)
        {
            var self = this;
            int np = fitLimbDarkening ? 8 : 6;
            var p0 = new List<double> { T0, Rp, A, Inclination, Baseline, Slope };
            if (fitLimbDarkening) { p0.Add(U1); p0.Add(U2); }
            var lo = new List<double> { T0 - 0.1, 0.001, 1.1, 60, 0.5, double.NaN };
            var hi = new List<double> { T0 + 0.1, 0.5, 500, 90, 1.5, double.NaN };
            if (fitLimbDarkening) { lo.Add(0); lo.Add(-0.5); hi.Add(1); hi.Add(1); }
            var arrT = t.ToArray();
            ModelFunction model = (p, i) =>
            {
                var m = self.Clone();
                m.T0 = p[0]; m.Rp = p[1]; m.A = p[2]; m.Inclination = p[3]; m.Baseline = p[4]; m.Slope = p[5];
                if (p.Length > 6) { m.U1 = p[6]; m.U2 = p[7]; }
                m.Steps = 200;
                return m.Flux(arrT[i]);
            };
            var r = LevenbergMarquardt.Fit(model, t.Count, f.ToArray(), df?.ToArray(), p0.ToArray(), lo.ToArray(), hi.ToArray(), 100);
            var q = r.Parameters;
            T0 = q[0]; Rp = q[1]; A = q[2]; Inclination = q[3]; Baseline = q[4]; Slope = q[5];
            if (fitLimbDarkening) { U1 = q[6]; U2 = q[7]; }
            return r;
        }

        /// <summary>MCMC posterior (emcee-style) for T0, Rp, A, b; returns median and ±1σ.</summary>
        public void Mcmc(IList<double> t, IList<double> f, IList<double> df, int steps, out double[] median, out double[] lo, out double[] hi, System.Threading.CancellationToken ct = default(System.Threading.CancellationToken))
        {
            var self = this;
            var arrT = t.ToArray(); var arrF = f.ToArray();
            var arrE = df != null ? df.ToArray() : Enumerable.Repeat(0.001, t.Count).ToArray();
            Func<double[], double> logp = p =>
            {
                ct.ThrowIfCancellationRequested(); // checked at every step of every walker
                if (p[1] <= 0 || p[1] > 0.5 || p[2] < 1.1 || p[3] < 0 || p[3] > 1 + p[1]) return double.NegativeInfinity;
                var m = self.Clone();
                m.T0 = p[0]; m.Rp = p[1]; m.A = p[2]; m.Inclination = Math.Acos(p[3] / p[2]) * AstroMath.Rad2Deg; m.Steps = 150;
                double chi = 0;
                for (int i = 0; i < arrT.Length; i++) { double r = (arrF[i] - m.Flux(arrT[i])) / arrE[i]; chi += r * r; }
                return -0.5 * chi;
            };
            var start = new[] { T0, Rp, A, ImpactParameter };
            var scatter = new[] { 1e-4, Rp * 0.01, A * 0.01, 0.02 };
            var sampler = new EnsembleSampler(16, 4, logp);
            var samples = sampler.Run(start, scatter, steps, steps / 3);
            EnsembleSampler.Summarize(samples, 4, out median, out lo, out hi);
        }

        /// <summary>Planet radius (Jupiter radii) and semi-major axis (AU) from stellar radius (R☉) and the fit.</summary>
        public void Physical(double starRadiusSun, out double rpJup, out double aAu)
        {
            rpJup = Rp * starRadiusSun * 695700 / 71492;
            aAu = A * starRadiusSun * 695700 / 1.495978707e8;
        }
    }

    /// <summary>Simple light-curve utilities: binning, detrending, sigma clipping and O-C.</summary>
    public static class LightCurveTools
    {
        public static void Bin(IList<double> t, IList<double> y, double width, out List<double> bt, out List<double> by, out List<double> be)
        {
            bt = new List<double>(); by = new List<double>(); be = new List<double>();
            var idx = Enumerable.Range(0, t.Count).OrderBy(i => t[i]).ToList();
            int k = 0;
            while (k < idx.Count)
            {
                double start = t[idx[k]];
                var vals = new List<double>(); var ts = new List<double>();
                while (k < idx.Count && t[idx[k]] < start + width) { vals.Add(y[idx[k]]); ts.Add(t[idx[k]]); k++; }
                bt.Add(ts.Average()); by.Add(vals.Average());
                be.Add(vals.Count > 1 ? Stats.StdDev(vals) / Math.Sqrt(vals.Count) : double.NaN);
            }
        }

        /// <summary>Divide by a polynomial in time (and optionally airmass) fitted to out-of-transit data.</summary>
        public static double[] Detrend(IList<double> t, IList<double> y, int degree, Func<double, bool> useForFit = null, IList<double> airmass = null)
        {
            var xs = new List<double>(); var ys = new List<double>();
            double t0 = t.Average();
            for (int i = 0; i < t.Count; i++) if (useForFit == null || useForFit(t[i])) { xs.Add(t[i] - t0); ys.Add(y[i]); }
            var c = LinAlg.PolyFit(xs, ys, degree);
            var o = new double[t.Count];
            for (int i = 0; i < t.Count; i++) o[i] = c != null ? y[i] / LinAlg.PolyEval(c, t[i] - t0) : y[i];
            return o;
        }

        public static bool[] SigmaClip(IList<double> y, double kappa = 4)
        {
            double med = Stats.Median(y), mad = Stats.Mad(y, med) * Stats.MadToSigma;
            return y.Select(v => Math.Abs(v - med) > kappa * mad).ToArray();
        }

        /// <summary>O-C (observed minus calculated) for a list of minima times against a linear ephemeris.</summary>
        public static List<double[]> OMinusC(IList<double> minima, double epoch, double period)
        {
            return minima.Select(m => { double e = Math.Round((m - epoch) / period); return new[] { e, m - (epoch + e * period) }; }).ToList();
        }

        /// <summary>Photometric scatter in parts per thousand (robust).</summary>
        public static double ScatterPpt(IList<double> relFlux)
        {
            double med = Stats.Median(relFlux);
            return Stats.Mad(relFlux, med) * Stats.MadToSigma / med * 1000;
        }
    }
}
