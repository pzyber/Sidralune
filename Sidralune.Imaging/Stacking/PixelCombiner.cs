using System;
using Sidralune.Core.Mathematics;

namespace Sidralune.Imaging.Stacking
{
    /// <summary>
    /// Per-pixel combination with outlier rejection. One instance per worker thread (holds scratch buffers).
    /// NaN inputs (no coverage) are ignored.
    /// </summary>
    public class PixelCombiner
    {
        private readonly CombineOptions _opt;
        private readonly double[] _frameWeights;
        private readonly float[] _v;
        private readonly double[] _w;
        private readonly float[] _tmp, _tmp2;
        private readonly int[] _idx;
        private readonly bool[] _keep;
        public long Rejected, Total;

        public PixelCombiner(int capacity, CombineOptions opt, double[] frameWeights)
        {
            _opt = opt;
            _frameWeights = frameWeights;
            _v = new float[capacity];
            _w = new double[capacity];
            _tmp = new float[capacity];
            _tmp2 = new float[capacity];
            _idx = new int[capacity];
            _keep = new bool[capacity];
        }

        /// <summary>Compatibility helper (allocates); prefer an instance per thread.</summary>
        public static float Combine(float[] values, int n, CombineOptions opt, double[] weights, float[] pixelWeights)
        {
            var c = new PixelCombiner(n, opt, weights);
            return c.Combine(values, n, pixelWeights);
        }

        public float Combine(float[] values, int n, float[] pixelWeights = null)
        {
            int m = 0;
            for (int i = 0; i < n; i++)
            {
                float v = values[i];
                if (float.IsNaN(v)) continue;
                _v[m] = v;
                double fw = _frameWeights != null && i < _frameWeights.Length ? _frameWeights[i] : 1.0;
                if (pixelWeights != null) fw *= pixelWeights[i];
                _w[m] = fw;
                _keep[m] = true;
                m++;
            }
            if (m == 0) return float.NaN;
            Total += m;
            if (m == 1) return _v[0];
            switch (_opt.Method)
            {
                case CombineMethod.Average: return WeightedMean(m);
                case CombineMethod.EntropyWeightedAverage: return WeightedMean(m);
                case CombineMethod.Median: return Median(m);
                case CombineMethod.Maximum: { float mx = _v[0]; for (int i = 1; i < m; i++) if (_v[i] > mx) mx = _v[i]; return mx; }
                case CombineMethod.Minimum: { float mn = _v[0]; for (int i = 1; i < m; i++) if (_v[i] < mn) mn = _v[i]; return mn; }
                case CombineMethod.KappaSigma: return KappaSigma(m, false);
                case CombineMethod.MedianKappaSigma: return KappaSigma(m, true);
                case CombineMethod.AutoAdaptiveWeightedAverage: return AutoAdaptive(m);
                case CombineMethod.WinsorizedSigma: return Winsorized(m);
                case CombineMethod.LinearFitClipping: return LinearFit(m);
                case CombineMethod.GeneralizedEsd: return Esd(m);
                case CombineMethod.PercentileClipping: return Percentile(m);
            }
            return WeightedMean(m);
        }

        private float WeightedMean(int m)
        {
            double s = 0, sw = 0;
            for (int i = 0; i < m; i++) if (_keep[i]) { s += _v[i] * _w[i]; sw += _w[i]; }
            return sw > 0 ? (float)(s / sw) : Median(m);
        }

        private float Median(int m)
        {
            int k = 0;
            for (int i = 0; i < m; i++) if (_keep[i]) _tmp[k++] = _v[i];
            return Stats.MedianInPlace(_tmp, k);
        }

        private void MeanStd(int m, out double mean, out double std, out int count)
        {
            double s = 0, s2 = 0; count = 0;
            for (int i = 0; i < m; i++) if (_keep[i]) { s += _v[i]; s2 += (double)_v[i] * _v[i]; count++; }
            mean = count > 0 ? s / count : 0;
            std = count > 1 ? Math.Sqrt(Math.Max(0, (s2 - s * s / count) / (count - 1))) : 0;
        }

        private float KappaSigma(int m, bool replaceWithMedian)
        {
            for (int it = 0; it < _opt.Iterations; it++)
            {
                double mean, std; int cnt;
                MeanStd(m, out mean, out std, out cnt);
                if (cnt < 3 || std <= 0) break;
                double centre = replaceWithMedian ? Median(m) : mean;
                double lo = centre - _opt.KappaLow * std, hi = centre + _opt.KappaHigh * std;
                bool changed = false;
                for (int i = 0; i < m; i++)
                {
                    if (!_keep[i]) continue;
                    if (_v[i] < lo || _v[i] > hi)
                    {
                        changed = true;
                        if (replaceWithMedian) _v[i] = (float)centre;
                        else { _keep[i] = false; Rejected++; }
                    }
                }
                if (!changed) break;
            }
            return WeightedMean(m);
        }

        /// <summary>Stetson-style iterative re-weighting: w = w0 / (1 + (|r|/(2σ))²).</summary>
        private float AutoAdaptive(int m)
        {
            var w0 = new double[m];
            Array.Copy(_w, w0, m);
            double mean = WeightedMean(m);
            for (int it = 0; it < Math.Max(5, _opt.Iterations); it++)
            {
                double s2 = 0, sw = 0;
                for (int i = 0; i < m; i++) { double r = _v[i] - mean; s2 += _w[i] * r * r; sw += _w[i]; }
                double sigma = sw > 0 ? Math.Sqrt(s2 / sw) : 0;
                if (sigma <= 1e-12) break;
                for (int i = 0; i < m; i++)
                {
                    double r = (_v[i] - mean) / (2 * sigma);
                    _w[i] = w0[i] / (1 + r * r);
                }
                double nm = WeightedMean(m);
                if (Math.Abs(nm - mean) < 1e-7) { mean = nm; break; }
                mean = nm;
            }
            return (float)mean;
        }

        /// <summary>Winsorized sigma clipping (Huber 1981), as in PixInsight ImageIntegration.</summary>
        private float Winsorized(int m)
        {
            for (int it = 0; it < _opt.Iterations; it++)
            {
                int k = 0;
                for (int i = 0; i < m; i++) if (_keep[i]) _tmp[k++] = _v[i];
                if (k < 3) break;
                Array.Copy(_tmp, _tmp2, k);
                float med = Stats.MedianInPlace(_tmp2, k);
                double sigma = 0;
                {
                    double mean, std; int c;
                    MeanStd(m, out mean, out std, out c);
                    sigma = std;
                }
                // winsorization loop to obtain a robust sigma
                for (int wi = 0; wi < 10 && sigma > 0; wi++)
                {
                    double lo = med - 1.5 * sigma, hi = med + 1.5 * sigma, s = 0, s2 = 0;
                    for (int i = 0; i < k; i++)
                    {
                        double v = Math.Max(lo, Math.Min(hi, _tmp[i]));
                        s += v; s2 += v * v;
                    }
                    double ns = 1.134 * Math.Sqrt(Math.Max(0, (s2 - s * s / k) / (k - 1)));
                    if (Math.Abs(ns - sigma) < 5e-4 * sigma) { sigma = ns; break; }
                    sigma = ns;
                }
                if (sigma <= 0) break;
                bool changed = false;
                for (int i = 0; i < m; i++)
                {
                    if (!_keep[i]) continue;
                    if (_v[i] < med - _opt.KappaLow * sigma || _v[i] > med + _opt.KappaHigh * sigma) { _keep[i] = false; Rejected++; changed = true; }
                }
                if (!changed) break;
            }
            return WeightedMean(m);
        }

        /// <summary>Linear fit clipping: fit sorted values vs rank; reject by mean absolute deviation from the line.</summary>
        private float LinearFit(int m)
        {
            for (int it = 0; it < _opt.Iterations; it++)
            {
                int k = 0;
                var idx = _idx;
                for (int i = 0; i < m; i++) if (_keep[i]) idx[k++] = i;
                if (k < 4) break;
                Array.Sort(idx, 0, k, new ValueComparer(_v));
                double sx = 0, sy = 0, sxx = 0, sxy = 0;
                for (int r = 0; r < k; r++) { double y = _v[idx[r]]; sx += r; sy += y; sxx += r * r; sxy += r * y; }
                double den = k * sxx - sx * sx;
                if (den == 0) break;
                double b = (k * sxy - sx * sy) / den, a = (sy - b * sx) / k;
                double mad = 0;
                for (int r = 0; r < k; r++) mad += Math.Abs(_v[idx[r]] - (a + b * r));
                mad /= k;
                if (mad <= 0) break;
                bool changed = false;
                for (int r = 0; r < k; r++)
                {
                    double dev = _v[idx[r]] - (a + b * r);
                    if (dev < -_opt.KappaLow * mad || dev > _opt.KappaHigh * mad) { _keep[idx[r]] = false; Rejected++; changed = true; }
                }
                if (!changed) break;
            }
            return WeightedMean(m);
        }

        private class ValueComparer : System.Collections.Generic.IComparer<int>
        {
            private readonly float[] _v;
            public ValueComparer(float[] v) { _v = v; }
            public int Compare(int a, int b) => _v[a].CompareTo(_v[b]);
        }

        /// <summary>Generalised extreme Studentized deviate test (Rosner 1983).</summary>
        private float Esd(int m)
        {
            int maxOut = Math.Max(1, (int)(m * _opt.EsdOutlierFraction));
            var removed = new int[maxOut];
            for (int i = 0; i < maxOut; i++) removed[i] = -1;
            int nOutliers = 0;
            for (int i = 1; i <= maxOut; i++)
            {
                double mean, std; int cnt;
                MeanStd(m, out mean, out std, out cnt);
                if (cnt < 3 || std <= 0) break;
                int worst = -1; double R = 0;
                for (int k = 0; k < m; k++)
                {
                    if (!_keep[k]) continue;
                    double r = Math.Abs(_v[k] - mean) / std;
                    if (r > R) { R = r; worst = k; }
                }
                int n = m;
                double p = 1 - _opt.EsdAlpha / (2.0 * (n - i + 1));
                double t = Stats.StudentTQuantile(p, n - i - 1);
                double lambda = (n - i) * t / Math.Sqrt((n - i - 1 + t * t) * (n - i + 1));
                _keep[worst] = false;
                removed[i - 1] = worst;
                if (R > lambda) nOutliers = i;
            }
            // restore those beyond the last significant outlier
            for (int i = nOutliers; i < maxOut; i++) if (removed[i] >= 0 && i < removed.Length) _keep[removed[i]] = true;
            for (int i = 0; i < nOutliers; i++) Rejected++;
            return WeightedMean(m);
        }

        private float Percentile(int m)
        {
            float med = Median(m);
            if (med == 0) return WeightedMean(m);
            for (int i = 0; i < m; i++)
            {
                double rel = (_v[i] - med) / Math.Abs(med);
                if (rel < -_opt.PercentileLow || rel > _opt.PercentileHigh) { _keep[i] = false; Rejected++; }
            }
            return WeightedMean(m);
        }
    }
}
