using System;
using System.Collections.Generic;
using System.Linq;
using Sidralune.Imaging.Analysis;

namespace Sidralune.Devices.Guiding
{
    /// <summary>One measurement of the guide star (image pixels; flux and peak in the frame's 0..1 units).</summary>
    public class GuideStarInfo
    {
        public double X, Y, Flux, Peak, Snr, Hfd;
        public bool Saturated;
        public override string ToString() => string.Format("({0:0.00}, {1:0.00}) SNR {2:0} HFD {3:0.0}", X, Y, Snr, Hfd);
    }

    /// <summary>Choosing a guide star in a frame and measuring it again in the frames after, with Sidralune's star detector.</summary>
    public static class GuideStars
    {
        // a hot pixel measures about half a pixel across; small guide-scope stars about one and a half
        public const double MinSnr = 8, MinHfd = 1.0, MaxHfd = 12;

        /// <summary>
        /// The best guide star in a frame, or null: a single, unsaturated, well-measured star (SNR, HFD within limits) at
        /// least edgeMargin pixels from the edges (room to calibrate and dither; the search radius and 8 pixels if not
        /// given), preferring the strongest signal that is not near saturation. With near, the usable star closest to that
        /// point (within 40 pixels), or failing that the closest star of any kind, for a star the user clicked.
        /// </summary>
        public static GuideStarInfo Select(float[] d, int w, int h, int searchRadius, double nearX = double.NaN, double nearY = double.NaN, int edgeMargin = 0)
        {
            List<DetectedStar> stars;
            var good = Usable(d, w, h, searchRadius, edgeMargin, MinSnr, out stars);
            DetectedStar pick;
            if (!double.IsNaN(nearX))
            {
                Func<DetectedStar, double> dist = s => Math.Sqrt((s.X - nearX) * (s.X - nearX) + (s.Y - nearY) * (s.Y - nearY));
                pick = good.Where(s => dist(s) <= 40).OrderBy(dist).FirstOrDefault() ?? stars.Where(s => dist(s) <= 40).OrderBy(dist).FirstOrDefault();
            }
            else pick = good.FirstOrDefault();
            if (pick == null) return null;
            // measured the way it will be measured while guiding, so the lock position and later frames agree
            return Measure(d, w, h, pick.X, pick.Y, searchRadius)
                ?? new GuideStarInfo { X = pick.X, Y = pick.Y, Flux = pick.Flux, Peak = pick.Peak, Snr = pick.Snr, Hfd = 2 * pick.Hfr, Saturated = pick.Saturated };
        }

        /// <summary>
        /// The stars fit to guide on, strongest first: single, unsaturated, well measured (at least minSnr, HFD within
        /// limits) and at least edgeMargin pixels from the edges (the search radius and 8 pixels if less). All the stars
        /// found come back in all.
        /// </summary>
        private static List<DetectedStar> Usable(float[] d, int w, int h, int searchRadius, int edgeMargin, double minSnr, out List<DetectedStar> all)
        {
            var stars = StarDetector.Detect(d, w, h, new StarDetectionOptions { Sigma = 5, MaxStars = 400, MaxEccentricity = 0.8 });
            all = stars;
            int margin = Math.Max(searchRadius + 8, Math.Min(edgeMargin, Math.Min(w, h) / 3));
            Func<DetectedStar, bool> roomy = s => s.X >= margin && s.Y >= margin && s.X < w - margin && s.Y < h - margin;
            // a neighbour inside the search box with a fifth of the flux or more would pull the measurement
            Func<DetectedStar, bool> alone = s => !stars.Any(o => o != s && Math.Abs(o.X - s.X) <= searchRadius + 3 && Math.Abs(o.Y - s.Y) <= searchRadius + 3 && o.Flux > s.Flux / 5);
            return stars.Where(s => !s.Saturated && s.Snr >= minSnr && 2 * s.Hfr >= MinHfd && 2 * s.Hfr <= MaxHfd && roomy(s) && alone(s))
                        .OrderBy(s => s.Peak > 0.85 ? 1 : 0).ThenByDescending(s => s.Snr).ToList();
        }

        /// <summary>
        /// More stars to guide on besides the ones given (positions to keep clear of): fit stars at least a search box
        /// apart from those and from each other, strongest first, measured the way guiding measures them.
        /// </summary>
        public static List<GuideStarInfo> SelectMore(float[] d, int w, int h, int searchRadius, int edgeMargin, IList<double[]> taken, int count, double minSnr = 6)
        {
            var found = new List<GuideStarInfo>();
            if (count <= 0) return found;
            List<DetectedStar> all;
            var good = Usable(d, w, h, searchRadius, edgeMargin, minSnr, out all);
            var clear = new List<double[]>(taken);
            int box = 2 * searchRadius + 1;
            foreach (var s in good)
            {
                if (found.Count >= count) break;
                if (clear.Any(t => Math.Abs(t[0] - s.X) < box && Math.Abs(t[1] - s.Y) < box)) continue;
                var m = Measure(d, w, h, s.X, s.Y, searchRadius);
                if (m == null || m.Saturated) continue;
                found.Add(m);
                clear.Add(new[] { m.X, m.Y });
            }
            return found;
        }

        /// <summary>
        /// The star in the square of the search radius around (x, y), or null when there is none. The background and noise
        /// come from the square's outer ring; the star is the brightest spot on a 3×3 median (so a single hot pixel does
        /// not count), and its position a Gaussian-weighted centroid, iterated until it settles.
        /// </summary>
        public static GuideStarInfo Measure(float[] d, int w, int h, double x, double y, int searchRadius)
        {
            int r = Math.Max(5, searchRadius);
            int cxi = (int)Math.Round(x), cyi = (int)Math.Round(y);
            int x0 = Math.Max(0, cxi - r), y0 = Math.Max(0, cyi - r), x1 = Math.Min(w, cxi + r + 1), y1 = Math.Min(h, cyi + r + 1);
            if (x1 - x0 < 9 || y1 - y0 < 9) return null;

            var ring = new List<double>();
            for (int j = y0; j < y1; j++)
                for (int i = x0; i < x1; i++)
                    if (i < x0 + 2 || i >= x1 - 2 || j < y0 + 2 || j >= y1 - 2) ring.Add(d[j * w + i]);
            double bg = Median(ring);
            for (int k = 0; k < ring.Count; k++) ring[k] = Math.Abs(ring[k] - bg);
            double sigma = Math.Max(1e-6, 1.4826 * Median(ring));

            double best = double.MinValue;
            int px = -1, py = -1;
            var nine = new double[9];
            for (int j = y0 + 1; j < y1 - 1; j++)
                for (int i = x0 + 1; i < x1 - 1; i++)
                {
                    int n = 0;
                    for (int dj = -1; dj <= 1; dj++) for (int di = -1; di <= 1; di++) nine[n++] = d[(j + dj) * w + i + di];
                    Array.Sort(nine);
                    if (nine[4] > best) { best = nine[4]; px = i; py = j; }
                }
            if (px < 0 || best - bg < 3 * sigma) return null;

            // Gaussian-weighted centroid: weights centred on the estimate, which moves until it sits on the star's centre
            const int R = 6;
            const double s2 = 2 * 1.5 * 1.5;
            double cx = px, cy = py;
            for (int it = 0; it < 15; it++)
            {
                double sw = 0, sx = 0, sy = 0;
                for (int j = Math.Max(0, (int)cy - R); j <= Math.Min(h - 1, (int)cy + R + 1); j++)
                    for (int i = Math.Max(0, (int)cx - R); i <= Math.Min(w - 1, (int)cx + R + 1); i++)
                    {
                        double v = d[j * w + i] - bg;
                        if (v <= 0) continue;
                        double r2 = (i - cx) * (i - cx) + (j - cy) * (j - cy);
                        if (r2 > R * R) continue;
                        double g = v * Math.Exp(-r2 / s2);
                        sw += g; sx += g * i; sy += g * j;
                    }
                if (sw <= 0) return null;
                double nx = sx / sw, ny = sy / sw, shift = Math.Abs(nx - cx) + Math.Abs(ny - cy);
                cx = nx; cy = ny;
                if (shift < 0.002) break;
            }
            if (Math.Abs(cx - x) > r || Math.Abs(cy - y) > r) return null;

            // flux, peak and half-flux diameter in a circle around the centre
            double flux = 0, peak = 0;
            int count = 0;
            var radial = new List<KeyValuePair<double, double>>();
            for (int j = Math.Max(0, (int)cy - R); j <= Math.Min(h - 1, (int)cy + R + 1); j++)
                for (int i = Math.Max(0, (int)cx - R); i <= Math.Min(w - 1, (int)cx + R + 1); i++)
                {
                    double r2 = (i - cx) * (i - cx) + (j - cy) * (j - cy);
                    if (r2 > R * R) continue;
                    double raw = d[j * w + i], v = raw - bg;
                    peak = Math.Max(peak, raw);
                    flux += v;
                    count++;
                    if (v > 0) radial.Add(new KeyValuePair<double, double>(Math.Sqrt(r2), v));
                }
            if (flux <= 0) return null;
            double snr = flux / (sigma * Math.Sqrt(count));
            if (snr < 3) return null;
            radial.Sort((a, b) => a.Key.CompareTo(b.Key));
            double half = radial.Sum(p => p.Value) / 2, acc = 0, hfr = 0.5;
            foreach (var p in radial) { acc += p.Value; if (acc >= half) { hfr = Math.Max(0.5, p.Key); break; } }
            return new GuideStarInfo { X = cx, Y = cy, Flux = flux, Peak = peak - bg, Snr = snr, Hfd = 2 * hfr, Saturated = peak >= 0.98 };
        }

        private static double Median(List<double> v)
        {
            var a = v.ToArray();
            Array.Sort(a);
            int n = a.Length;
            return n == 0 ? 0 : n % 2 == 1 ? a[n / 2] : 0.5 * (a[n / 2 - 1] + a[n / 2]);
        }
    }
}
