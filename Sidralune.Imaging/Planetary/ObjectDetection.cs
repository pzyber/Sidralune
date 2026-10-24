using System;
using System.Collections.Generic;
using System.Linq;
using Sidralune.Core.Mathematics;
using Sidralune.Imaging.Analysis;

namespace Sidralune.Imaging.Planetary
{
    public class ObjectInfo
    {
        public bool Found;
        public double Cx, Cy;          // centroid
        public int X0, Y0, X1, Y1;     // bounding box
        public double Radius;          // equivalent radius (pixels)
        public double Brightness;      // mean level inside object
        public double Threshold;
        public bool TouchesEdge;
        public int Pixels;
        public int Width => X1 - X0 + 1;
        public int Height => Y1 - Y0 + 1;
    }

    /// <summary>Planet/Moon/Sun detection for centring, cropping and rejection.</summary>
    public static class ObjectDetection
    {
        /// <summary>Otsu threshold on a subsampled histogram.</summary>
        public static double OtsuThreshold(float[] d)
        {
            const int bins = 256;
            var hist = new double[bins];
            float mn = float.MaxValue, mx = float.MinValue;
            for (int i = 0; i < d.Length; i += 3) { if (d[i] < mn) mn = d[i]; if (d[i] > mx) mx = d[i]; }
            if (mx <= mn) return mn;
            int n = 0;
            for (int i = 0; i < d.Length; i += 3) { int b = (int)((d[i] - mn) / (mx - mn) * (bins - 1)); hist[b]++; n++; }
            double sum = 0;
            for (int i = 0; i < bins; i++) sum += i * hist[i];
            double sumB = 0, wB = 0, best = 0; int bt = 0;
            for (int t = 0; t < bins; t++)
            {
                wB += hist[t];
                if (wB == 0) continue;
                double wF = n - wB;
                if (wF == 0) break;
                sumB += t * hist[t];
                double mB = sumB / wB, mF = (sum - sumB) / wF;
                double between = wB * wF * (mB - mF) * (mB - mF);
                if (between > best) { best = between; bt = t; }
            }
            // the upper edge of the last background bin: its own pixels belong to the background
            return mn + (mx - mn) * (bt + 1) / (bins - 1.0);
        }

        /// <summary>
        /// Detect the dominant bright object: threshold (Otsu or explicit fraction of max), largest connected component,
        /// intensity-weighted centroid.
        /// </summary>
        public static ObjectInfo Detect(float[] d, int w, int h, double thresholdOverride = -1, int minPixels = 20)
        {
            double thr = thresholdOverride >= 0 ? thresholdOverride : OtsuThreshold(d);
            var info = new ObjectInfo { Threshold = thr };
            // downsample for speed on big frames, label components on the coarse grid
            int step = Math.Max(1, Math.Min(w, h) / 400);
            int gw = w / step, gh = h / step;
            var lbl = new int[gw * gh];
            int bestLbl = 0, bestCount = 0, cur = 0;
            var stack = new Stack<int>();
            for (int gy = 0; gy < gh; gy++)
                for (int gx = 0; gx < gw; gx++)
                {
                    int gi = gy * gw + gx;
                    if (lbl[gi] != 0 || d[gy * step * w + gx * step] <= thr) continue;
                    cur++;
                    int count = 0;
                    stack.Push(gi); lbl[gi] = cur;
                    while (stack.Count > 0)
                    {
                        int p = stack.Pop(); count++;
                        int px = p % gw, py = p / gw;
                        for (int k = 0; k < 4; k++)
                        {
                            int nx = px + (k == 0 ? -1 : k == 1 ? 1 : 0), ny = py + (k == 2 ? -1 : k == 3 ? 1 : 0);
                            if (nx < 0 || ny < 0 || nx >= gw || ny >= gh) continue;
                            int q = ny * gw + nx;
                            if (lbl[q] != 0 || d[ny * step * w + nx * step] <= thr) continue;
                            lbl[q] = cur; stack.Push(q);
                        }
                    }
                    if (count > bestCount) { bestCount = count; bestLbl = cur; }
                }
            if (bestLbl == 0 || bestCount * step * step < minPixels) return info;
            int x0 = int.MaxValue, y0 = int.MaxValue, x1 = 0, y1 = 0;
            for (int gy = 0; gy < gh; gy++)
                for (int gx = 0; gx < gw; gx++)
                    if (lbl[gy * gw + gx] == bestLbl) { x0 = Math.Min(x0, gx); x1 = Math.Max(x1, gx); y0 = Math.Min(y0, gy); y1 = Math.Max(y1, gy); }
            x0 = Math.Max(0, x0 * step - step); y0 = Math.Max(0, y0 * step - step);
            x1 = Math.Min(w - 1, x1 * step + 2 * step); y1 = Math.Min(h - 1, y1 * step + 2 * step);
            double s = 0, sx = 0, sy = 0, sb = 0; int n = 0;
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    double v = d[y * w + x] - thr;
                    if (v <= 0) continue;
                    s += v; sx += v * x; sy += v * y; sb += d[y * w + x]; n++;
                }
            if (s <= 0) return info;
            info.Found = true;
            info.Cx = sx / s; info.Cy = sy / s;
            info.X0 = x0; info.Y0 = y0; info.X1 = x1; info.Y1 = y1;
            info.Pixels = n;
            info.Radius = Math.Sqrt(n / Math.PI);
            info.Brightness = sb / n;
            info.TouchesEdge = x0 <= 1 || y0 <= 1 || x1 >= w - 2 || y1 >= h - 2;
            return info;
        }

        /// <summary>
        /// Fit a circle to the limb of a bright disc (Sun/Moon/planet): radial edge points by maximum gradient along rays,
        /// then RANSAC + algebraic (Kåsa) least squares. Returns cx, cy, r.
        /// </summary>
        public static double[] FitLimb(float[] d, int w, int h, ObjectInfo guess, int rays = 180)
        {
            if (guess == null || !guess.Found) return null;
            var pts = new List<double[]>();
            double rmax = Math.Max(guess.Width, guess.Height) * 0.75;
            for (int k = 0; k < rays; k++)
            {
                double a = 2 * Math.PI * k / rays, ca = Math.Cos(a), sa = Math.Sin(a);
                double best = 0, br = -1;
                double prev = Geometry.SampleAt(d, w, h, guess.Cx, guess.Cy, Interpolation.Bilinear);
                for (double r = guess.Radius * 0.5; r < rmax; r += 0.5)
                {
                    double x = guess.Cx + r * ca, y = guess.Cy + r * sa;
                    if (x < 1 || y < 1 || x >= w - 2 || y >= h - 2) break;
                    double v = Geometry.SampleAt(d, w, h, x, y, Interpolation.Bilinear);
                    double g = prev - v;
                    if (g > best) { best = g; br = r; }
                    prev = v;
                }
                if (br > 0) pts.Add(new[] { guess.Cx + br * ca, guess.Cy + br * sa });
            }
            if (pts.Count < 10) return new[] { guess.Cx, guess.Cy, guess.Radius };
            var rng = new Random(3);
            double[] best3 = null; int bestIn = 0;
            for (int it = 0; it < 300; it++)
            {
                var a = pts[rng.Next(pts.Count)]; var b = pts[rng.Next(pts.Count)]; var c = pts[rng.Next(pts.Count)];
                var circ = Circle3(a, b, c);
                if (circ == null) continue;
                int inl = pts.Count(p => Math.Abs(Math.Sqrt(Sq(p[0] - circ[0]) + Sq(p[1] - circ[1])) - circ[2]) < 1.5);
                if (inl > bestIn) { bestIn = inl; best3 = circ; }
            }
            if (best3 == null) return new[] { guess.Cx, guess.Cy, guess.Radius };
            var inliers = pts.Where(p => Math.Abs(Math.Sqrt(Sq(p[0] - best3[0]) + Sq(p[1] - best3[1])) - best3[2]) < 1.5).ToList();
            return KasaFit(inliers) ?? best3;
        }

        private static double Sq(double v) => v * v;

        private static double[] Circle3(double[] a, double[] b, double[] c)
        {
            double d = 2 * (a[0] * (b[1] - c[1]) + b[0] * (c[1] - a[1]) + c[0] * (a[1] - b[1]));
            if (Math.Abs(d) < 1e-9) return null;
            double ux = ((a[0] * a[0] + a[1] * a[1]) * (b[1] - c[1]) + (b[0] * b[0] + b[1] * b[1]) * (c[1] - a[1]) + (c[0] * c[0] + c[1] * c[1]) * (a[1] - b[1])) / d;
            double uy = ((a[0] * a[0] + a[1] * a[1]) * (c[0] - b[0]) + (b[0] * b[0] + b[1] * b[1]) * (a[0] - c[0]) + (c[0] * c[0] + c[1] * c[1]) * (b[0] - a[0])) / d;
            return new[] { ux, uy, Math.Sqrt(Sq(a[0] - ux) + Sq(a[1] - uy)) };
        }

        private static double[] KasaFit(List<double[]> p)
        {
            int n = p.Count;
            if (n < 3) return null;
            var D = new double[n, 3]; var y = new double[n];
            for (int i = 0; i < n; i++) { D[i, 0] = p[i][0]; D[i, 1] = p[i][1]; D[i, 2] = 1; y[i] = p[i][0] * p[i][0] + p[i][1] * p[i][1]; }
            var s = LinAlg.LeastSquares(D, y);
            if (s == null) return null;
            double cx = s[0] / 2, cy = s[1] / 2;
            return new[] { cx, cy, Math.Sqrt(s[2] + cx * cx + cy * cy) };
        }
    }

    public enum QualityMetric { Laplacian, Gradient, LocalContrast }

    /// <summary>Frame sharpness estimation (brightness-normalised so exposure fluctuations do not bias ranking).</summary>
    public static class FrameQuality
    {
        public static double Measure(float[] d, int w, int h, QualityMetric m = QualityMetric.Gradient, int x0 = 0, int y0 = 0, int rw = -1, int rh = -1, double noiseBlur = 1.0)
        {
            if (rw < 0) rw = w; if (rh < 0) rh = h;
            // light pre-blur makes the metric robust against noise (AutoStakkert "noise robust" setting)
            float[] src = d;
            int sw = w, sx0 = x0, sy0 = y0;
            if (noiseBlur > 0)
            {
                var crop = new float[rw * rh];
                for (int y = 0; y < rh; y++) Array.Copy(d, (y0 + y) * w + x0, crop, y * rw, rw);
                src = Filters.GaussianBlur(crop, rw, rh, noiseBlur);
                sw = rw; sx0 = 0; sy0 = 0;
            }
            double mean = 0; long n = 0;
            for (int y = sy0; y < sy0 + rh; y++) for (int x = sx0; x < sx0 + rw; x++) { mean += src[y * sw + x]; n++; }
            mean /= Math.Max(1, n);
            if (mean <= 1e-9) return 0;
            double q;
            switch (m)
            {
                case QualityMetric.Laplacian: q = Filters.LaplacianVariance(src, sw, noiseBlur > 0 ? rh : h, sx0, sy0, rw, rh); break;
                case QualityMetric.LocalContrast:
                    {
                        var b = Filters.GaussianBlur(src, sw, noiseBlur > 0 ? rh : h, 3);
                        double s2 = 0;
                        for (int y = sy0; y < sy0 + rh; y++) for (int x = sx0; x < sx0 + rw; x++) { double dd = src[y * sw + x] - b[y * sw + x]; s2 += dd * dd; }
                        q = s2 / Math.Max(1, n);
                        break;
                    }
                default: q = Filters.GradientEnergy(src, sw, noiseBlur > 0 ? rh : h, sx0, sy0, rw, rh); break;
            }
            return q / (mean * mean);
        }
    }
}
