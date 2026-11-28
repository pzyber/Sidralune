using System;
using System.Collections.Generic;
using System.Linq;

namespace Sidralune.Imaging
{
    /// <summary>
    /// A tone curve through points (x, y) in 0..1, drawn with monotone cubic interpolation (Fritsch &amp; Carlson 1980),
    /// so it never swings past the points between them. The ends are at x = 0 and x = 1.
    /// </summary>
    public class MonotoneCurve
    {
        private readonly double[] _x, _y, _m;

        /// <summary>The curve through the points, given as x0, y0, x1, y1…; null or fewer than two points is the straight line.</summary>
        public MonotoneCurve(double[] flat)
        {
            var pts = new List<KeyValuePair<double, double>>();
            if (flat != null)
                for (int i = 0; i + 1 < flat.Length; i += 2)
                    if (!double.IsNaN(flat[i]) && !double.IsNaN(flat[i + 1]))
                        pts.Add(new KeyValuePair<double, double>(Clamp(flat[i]), Clamp(flat[i + 1])));
            pts = pts.GroupBy(p => p.Key).Select(g => g.Last()).OrderBy(p => p.Key).ToList();
            if (pts.Count == 0 || pts[0].Key > 0) pts.Insert(0, new KeyValuePair<double, double>(0, pts.Count == 0 ? 0 : pts[0].Value));
            if (pts[pts.Count - 1].Key < 1) pts.Add(new KeyValuePair<double, double>(1, pts.Count == 1 ? 1 : pts[pts.Count - 1].Value));
            if (flat == null || flat.Length < 4) pts = new List<KeyValuePair<double, double>> { new KeyValuePair<double, double>(0, 0), new KeyValuePair<double, double>(1, 1) };
            int n = pts.Count;
            _x = pts.Select(p => p.Key).ToArray();
            _y = pts.Select(p => p.Value).ToArray();
            // Fritsch-Carlson slopes
            var d = new double[n - 1];
            for (int i = 0; i < n - 1; i++) d[i] = (_y[i + 1] - _y[i]) / (_x[i + 1] - _x[i]);
            _m = new double[n];
            _m[0] = d[0]; _m[n - 1] = d[n - 2];
            for (int i = 1; i < n - 1; i++) _m[i] = d[i - 1] * d[i] <= 0 ? 0 : (d[i - 1] + d[i]) / 2;
            for (int i = 0; i < n - 1; i++)
            {
                if (d[i] == 0) { _m[i] = 0; _m[i + 1] = 0; continue; }
                double a = _m[i] / d[i], b = _m[i + 1] / d[i], s = a * a + b * b;
                if (s > 9) { double t = 3 / Math.Sqrt(s); _m[i] = t * a * d[i]; _m[i + 1] = t * b * d[i]; }
            }
        }

        /// <summary>Whether the curve is the straight line from (0, 0) to (1, 1).</summary>
        public bool IsIdentity => _x.Length == 2 && _y[0] == 0 && _y[1] == 1;

        public double this[double x]
        {
            get
            {
                x = Clamp(x);
                int i = Array.BinarySearch(_x, x);
                if (i >= 0) return _y[i];
                i = Math.Max(0, Math.Min(_x.Length - 2, ~i - 1));
                double h = _x[i + 1] - _x[i], t = (x - _x[i]) / h, t2 = t * t, t3 = t2 * t;
                return Clamp((2 * t3 - 3 * t2 + 1) * _y[i] + (t3 - 2 * t2 + t) * h * _m[i] + (-2 * t3 + 3 * t2) * _y[i + 1] + (t3 - t2) * h * _m[i + 1]);
            }
        }

        private static double Clamp(double v) => v < 0 ? 0 : v > 1 ? 1 : v;
    }
}
