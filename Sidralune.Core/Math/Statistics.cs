using System;
using System.Collections.Generic;

namespace Sidralune.Core.Mathematics
{
    /// <summary>Robust statistics: medians, MAD, sigma clipping, percentiles, noise estimators.</summary>
    public static class Stats
    {
        public static double Mean(IList<double> v)
        {
            if (v.Count == 0) return double.NaN;
            double s = 0;
            for (int i = 0; i < v.Count; i++) s += v[i];
            return s / v.Count;
        }

        public static double Mean(float[] v)
        {
            if (v.Length == 0) return double.NaN;
            double s = 0;
            for (int i = 0; i < v.Length; i++) s += v[i];
            return s / v.Length;
        }

        public static double StdDev(IList<double> v)
        {
            int n = v.Count;
            if (n < 2) return 0;
            double m = Mean(v), s = 0;
            for (int i = 0; i < n; i++) { double d = v[i] - m; s += d * d; }
            return Math.Sqrt(s / (n - 1));
        }

        public static double StdDev(float[] v, int n, double mean)
        {
            if (n < 2) return 0;
            double s = 0;
            for (int i = 0; i < n; i++) { double d = v[i] - mean; s += d * d; }
            return Math.Sqrt(s / (n - 1));
        }

        /// <summary>k-th smallest element (0-based) via Hoare quickselect; reorders the array.</summary>
        public static float QuickSelect(float[] a, int n, int k)
        {
            int lo = 0, hi = n - 1;
            while (lo < hi)
            {
                float pivot = a[(lo + hi) >> 1];
                int i = lo, j = hi;
                while (i <= j)
                {
                    while (a[i] < pivot) i++;
                    while (a[j] > pivot) j--;
                    if (i <= j) { float t = a[i]; a[i] = a[j]; a[j] = t; i++; j--; }
                }
                if (k <= j) hi = j;
                else if (k >= i) lo = i;
                else return a[k];
            }
            return a[k];
        }

        public static double QuickSelect(double[] a, int n, int k)
        {
            int lo = 0, hi = n - 1;
            while (lo < hi)
            {
                double pivot = a[(lo + hi) >> 1];
                int i = lo, j = hi;
                while (i <= j)
                {
                    while (a[i] < pivot) i++;
                    while (a[j] > pivot) j--;
                    if (i <= j) { double t = a[i]; a[i] = a[j]; a[j] = t; i++; j--; }
                }
                if (k <= j) hi = j;
                else if (k >= i) lo = i;
                else return a[k];
            }
            return a[k];
        }

        /// <summary>Median of first n entries; the buffer is reordered.</summary>
        public static float MedianInPlace(float[] a, int n)
        {
            if (n <= 0) return float.NaN;
            if (n == 1) return a[0];
            int h = n >> 1;
            float m1 = QuickSelect(a, n, h);
            if ((n & 1) == 1) return m1;
            // the lower median is the maximum of the left partition
            float m0 = float.MinValue;
            for (int i = 0; i < h; i++) if (a[i] > m0) m0 = a[i];
            return 0.5f * (m0 + m1);
        }

        public static double MedianInPlace(double[] a, int n)
        {
            if (n <= 0) return double.NaN;
            if (n == 1) return a[0];
            int h = n >> 1;
            double m1 = QuickSelect(a, n, h);
            if ((n & 1) == 1) return m1;
            double m0 = double.MinValue;
            for (int i = 0; i < h; i++) if (a[i] > m0) m0 = a[i];
            return 0.5 * (m0 + m1);
        }

        public static double Median(IList<double> v)
        {
            var a = new double[v.Count];
            v.CopyTo(a, 0);
            return MedianInPlace(a, a.Length);
        }

        public static float Median(float[] v)
        {
            var a = (float[])v.Clone();
            return MedianInPlace(a, a.Length);
        }

        /// <summary>Median absolute deviation (unscaled).</summary>
        public static double Mad(IList<double> v, double median)
        {
            var d = new double[v.Count];
            for (int i = 0; i < d.Length; i++) d[i] = Math.Abs(v[i] - median);
            return MedianInPlace(d, d.Length);
        }

        public static float Mad(float[] v, int n, float median, float[] scratch = null)
        {
            var d = scratch ?? new float[n];
            for (int i = 0; i < n; i++) d[i] = Math.Abs(v[i] - median);
            return MedianInPlace(d, n);
        }

        /// <summary>Gaussian-consistent scale from MAD.</summary>
        public const double MadToSigma = 1.4826;

        public static double Percentile(IList<double> v, double p)
        {
            if (v.Count == 0) return double.NaN;
            var a = new double[v.Count];
            v.CopyTo(a, 0);
            Array.Sort(a);
            double idx = AstroMath.Clamp(p, 0, 100) / 100.0 * (a.Length - 1);
            int i0 = (int)Math.Floor(idx);
            int i1 = Math.Min(i0 + 1, a.Length - 1);
            return a[i0] + (a[i1] - a[i0]) * (idx - i0);
        }

        /// <summary>Iterative kappa-sigma clipped mean/sigma around the median.</summary>
        public static void SigmaClip(IList<double> v, double kappa, int iterations, out double mean, out double sigma, out int used)
        {
            var data = new List<double>(v);
            mean = Mean(data); sigma = StdDev(data); used = data.Count;
            for (int it = 0; it < iterations && data.Count > 2; it++)
            {
                double med = Median(data);
                double lo = med - kappa * sigma, hi = med + kappa * sigma;
                var keep = new List<double>(data.Count);
                foreach (var x in data) if (x >= lo && x <= hi) keep.Add(x);
                if (keep.Count == data.Count || keep.Count < 2) break;
                data = keep;
                mean = Mean(data); sigma = StdDev(data); used = data.Count;
            }
        }

        /// <summary>
        /// Biweight midvariance location/scale estimator (Beers, Flynn &amp; Gebhardt 1990), highly robust.
        /// </summary>
        public static void Biweight(IList<double> v, out double location, out double scale, double c = 6.0)
        {
            double M = Median(v);
            double mad = Mad(v, M);
            if (mad <= 0) { location = M; scale = 0; return; }
            double num = 0, den = 0;
            for (int i = 0; i < v.Count; i++)
            {
                double u = (v[i] - M) / (c * mad);
                if (Math.Abs(u) < 1) { double w = (1 - u * u); w *= w; num += (v[i] - M) * w; den += w; }
            }
            location = den > 0 ? M + num / den : M;
            double n = v.Count, sn = 0, sd = 0;
            for (int i = 0; i < v.Count; i++)
            {
                double u = (v[i] - M) / (9.0 * mad);
                if (Math.Abs(u) < 1)
                {
                    double u2 = u * u;
                    sn += (v[i] - M) * (v[i] - M) * Math.Pow(1 - u2, 4);
                    sd += (1 - u2) * (1 - 5 * u2);
                }
            }
            scale = sd != 0 ? Math.Sqrt(n * sn) / Math.Abs(sd) : 0;
        }

        /// <summary>Simple linear regression y = a + b x. Returns false if degenerate.</summary>
        public static bool LinearFit(IList<double> x, IList<double> y, out double a, out double b, out double r2)
        {
            int n = x.Count;
            a = b = r2 = 0;
            if (n < 2) return false;
            double sx = 0, sy = 0, sxx = 0, sxy = 0, syy = 0;
            for (int i = 0; i < n; i++) { sx += x[i]; sy += y[i]; sxx += x[i] * x[i]; sxy += x[i] * y[i]; syy += y[i] * y[i]; }
            double d = n * sxx - sx * sx;
            if (Math.Abs(d) < 1e-300) return false;
            b = (n * sxy - sx * sy) / d;
            a = (sy - b * sx) / n;
            double vy = n * syy - sy * sy;
            r2 = vy > 0 ? (n * sxy - sx * sy) * (n * sxy - sx * sy) / (d * vy) : 1;
            return true;
        }

        /// <summary>
        /// Noise estimate using the robust first-difference method (σ ≈ MAD(diff)·1.4826/√2), useful for images and spectra.
        /// </summary>
        public static double DifferenceNoise(float[] data, int n, int stride = 1)
        {
            int m = (n - stride);
            if (m < 2) return 0;
            int step = Math.Max(1, m / 200000);
            var d = new List<double>(m / step + 1);
            for (int i = 0; i < m; i += step) d.Add(data[i + stride] - data[i]);
            double med = Median(d);
            return Mad(d, med) * MadToSigma / Math.Sqrt(2.0);
        }

        /// <summary>Weighted mean with standard error.</summary>
        public static double WeightedMean(IList<double> v, IList<double> sigma, out double err)
        {
            double sw = 0, s = 0;
            for (int i = 0; i < v.Count; i++)
            {
                double w = sigma[i] > 0 ? 1.0 / (sigma[i] * sigma[i]) : 0;
                sw += w; s += w * v[i];
            }
            err = sw > 0 ? Math.Sqrt(1.0 / sw) : double.NaN;
            return sw > 0 ? s / sw : double.NaN;
        }

        /// <summary>Histogram of values into 'bins' between min and max.</summary>
        public static int[] Histogram(float[] data, int bins, double min, double max)
        {
            var h = new int[bins];
            double scale = bins / Math.Max(1e-30, max - min);
            for (int i = 0; i < data.Length; i++)
            {
                double v = data[i];
                if (double.IsNaN(v)) continue;
                int b = (int)((v - min) * scale);
                if (b < 0) b = 0; else if (b >= bins) b = bins - 1;
                h[b]++;
            }
            return h;
        }

        /// <summary>Inverse of the standard normal CDF (Acklam's algorithm, |rel err| &lt; 1.15e-9).</summary>
        public static double NormalQuantile(double p)
        {
            if (p <= 0) return double.NegativeInfinity;
            if (p >= 1) return double.PositiveInfinity;
            double[] a = { -3.969683028665376e+01, 2.209460984245205e+02, -2.759285104469687e+02, 1.383577518672690e+02, -3.066479806614716e+01, 2.506628277459239e+00 };
            double[] b = { -5.447609879822406e+01, 1.615858368580409e+02, -1.556989798598866e+02, 6.680131188771972e+01, -1.328068155288572e+01 };
            double[] c = { -7.784894002430293e-03, -3.223964580411365e-01, -2.400758277161838e+00, -2.549732539343734e+00, 4.374664141464968e+00, 2.938163982698783e+00 };
            double[] d = { 7.784695709041462e-03, 3.224671290700398e-01, 2.445134137142996e+00, 3.754408661907416e+00 };
            double plow = 0.02425, phigh = 1 - plow, q, r;
            if (p < plow)
            {
                q = Math.Sqrt(-2 * Math.Log(p));
                return (((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) / ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1);
            }
            if (p <= phigh)
            {
                q = p - 0.5; r = q * q;
                return (((((a[0] * r + a[1]) * r + a[2]) * r + a[3]) * r + a[4]) * r + a[5]) * q / (((((b[0] * r + b[1]) * r + b[2]) * r + b[3]) * r + b[4]) * r + 1);
            }
            q = Math.Sqrt(-2 * Math.Log(1 - p));
            return -(((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) / ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1);
        }

        public static double NormalCdf(double x) => 0.5 * Erfc(-x / Math.Sqrt(2));

        /// <summary>Complementary error function (Numerical Recipes Chebyshev, rel. err &lt; 1.2e-7).</summary>
        public static double Erfc(double x)
        {
            double z = Math.Abs(x);
            double t = 1.0 / (1.0 + 0.5 * z);
            double ans = t * Math.Exp(-z * z - 1.26551223 + t * (1.00002368 + t * (0.37409196 + t * (0.09678418 +
                t * (-0.18628806 + t * (0.27886807 + t * (-1.13520398 + t * (1.48851587 + t * (-0.82215223 + t * 0.17087277)))))))));
            return x >= 0 ? ans : 2.0 - ans;
        }

        public static double Erf(double x) => 1.0 - Erfc(x);

        /// <summary>Student-t critical value approximation (Hill 1970) for two-sided p.</summary>
        public static double StudentTQuantile(double p, double dof)
        {
            double z = NormalQuantile(p);
            double g1 = (z * z * z + z) / 4.0;
            double g2 = (5 * Math.Pow(z, 5) + 16 * Math.Pow(z, 3) + 3 * z) / 96.0;
            double g3 = (3 * Math.Pow(z, 7) + 19 * Math.Pow(z, 5) + 17 * Math.Pow(z, 3) - 15 * z) / 384.0;
            return z + g1 / dof + g2 / (dof * dof) + g3 / (dof * dof * dof);
        }
    }
}
