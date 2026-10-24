using System;
using System.Collections.Generic;
using System.Linq;
using Sidralune.Imaging.Analysis;

namespace Sidralune.Science.Astrometry
{
    /// <summary>
    /// How faint a solved image reaches. Catalogue stars found where the solution puts them give a photometric zero point,
    /// which is applied to the faintest stars detected, so the limit can reach beyond the catalogue's own depth.
    /// Every star is measured by fitting the image's own star profile (built from unsaturated stars) to its pixels below the
    /// clip level: a saturated star is measured from its wings, so a bright catalogue such as HYG, whose stars are all
    /// saturated in a deep exposure, still calibrates the image.
    /// </summary>
    public static class ImageDepth
    {
        public class Result
        {
            /// <summary>Magnitude of the faintest stars detected (the faintest 5 %), NaN when it cannot be told.</summary>
            public double Limit = double.NaN;
            public double ZeroPoint = double.NaN;
            /// <summary>Catalogue stars that calibrated the zero point, and stars detected.</summary>
            public int Calibrators, Stars;
        }

        /// <summary>All the stars of an image, brightest first, as Measure needs them (and the plate solver can use them).</summary>
        public static List<DetectedStar> DetectAll(float[] lum, int w, int h) => StarDetector.Detect(lum, w, h, new StarDetectionOptions { MaxStars = int.MaxValue });

        /// <summary>
        /// Measures the depth of an image (luminance, 0-1) solved by wcs against catalogue stars refs. stars are all the
        /// stars detected in it (DetectAll); null to detect them here.
        /// </summary>
        public static Result Measure(float[] lum, int w, int h, Wcs wcs, IEnumerable<RefStar> refs, List<DetectedStar> stars = null)
        {
            var res = new Result();
            stars = stars ?? DetectAll(lum, w, h);
            res.Stars = stars.Count;
            if (stars.Count < 10) return res;
            // pixels at or above the clip level are cut off in the brightest stars
            double clip = 0.9 * stars.Max(s => s.Peak + s.Background);
            int r;
            var psf = BuildPsf(lum, w, h, stars, clip, out r);
            if (psf == null) return res;
            Func<DetectedStar, double> flux = s => FitFlux(lum, w, h, s, psf, r, clip);
            var zp = Match(wcs, stars, refs).Where(m => !double.IsNaN(m.Item2.Mag)).Select(m => m.Item2.Mag + 2.5 * Math.Log10(flux(m.Item1)))
                .Where(v => !double.IsNaN(v) && !double.IsInfinity(v)).OrderBy(v => v).ToList();
            res.Calibrators = zp.Count;
            var faint = stars.Select(flux).Where(f => f > 0).OrderBy(f => f).ToList();
            if (zp.Count < 5 || faint.Count < 10) return res;
            res.ZeroPoint = zp[zp.Count / 2];
            res.Limit = res.ZeroPoint - 2.5 * Math.Log10(faint[faint.Count / 20]);
            return res;
        }

        /// <summary>Pairs each catalogue star with the nearest detected star the solution puts within tolPx of it, one to one.</summary>
        public static List<Tuple<DetectedStar, RefStar>> Match(Wcs wcs, IEnumerable<DetectedStar> detected, IEnumerable<RefStar> refs, double tolPx = 2.5)
        {
            // stars in cells of the tolerance's size: a catalogue star only looks at the 3 x 3 cells around it
            var cells = new Dictionary<long, List<DetectedStar>>();
            Func<double, long> cell = v => (long)Math.Floor(v / tolPx);
            foreach (var s in detected)
            {
                long key = cell(s.X) * 1000003 + cell(s.Y);
                List<DetectedStar> list;
                if (!cells.TryGetValue(key, out list)) cells[key] = list = new List<DetectedStar>();
                list.Add(s);
            }
            var used = new HashSet<DetectedStar>();
            var res = new List<Tuple<DetectedStar, RefStar>>();
            foreach (var rs in refs)
            {
                double x, y;
                if (!wcs.WorldToPixel(rs.Ra, rs.Dec, out x, out y)) continue;
                DetectedStar best = null;
                double bestD = tolPx * tolPx;
                for (long cx = cell(x) - 1; cx <= cell(x) + 1; cx++)
                    for (long cy = cell(y) - 1; cy <= cell(y) + 1; cy++)
                    {
                        List<DetectedStar> list;
                        if (!cells.TryGetValue(cx * 1000003 + cy, out list)) continue;
                        foreach (var s in list)
                        {
                            double d = (s.X - x) * (s.X - x) + (s.Y - y) * (s.Y - y);
                            if (d <= bestD && !used.Contains(s)) { best = s; bestD = d; }
                        }
                    }
                if (best != null) { used.Add(best); res.Add(Tuple.Create(best, rs)); }
            }
            return res;
        }

        /// <summary>
        /// The image's star profile, normalised to a sum of 1, on a (2r + 1)² grid centred on the star: the per-pixel median
        /// of up to 50 bright, isolated stars well below the clip level, preferring stars centred near a pixel centre so the
        /// profile is not smeared. Null when there are fewer than 5 such stars.
        /// </summary>
        public static float[] BuildPsf(float[] lum, int w, int h, IList<DetectedStar> stars, double clip, out int r)
        {
            var fw = stars.Where(s => !s.Saturated && s.Fwhm > 0).Select(s => s.Fwhm).OrderBy(f => f).ToList();
            double fwhm = fw.Count > 0 ? fw[fw.Count / 2] : 3;
            r = Math.Max(3, Math.Min(20, (int)Math.Ceiling(2 * fwhm)));
            int rr = r, n = 2 * r + 1;
            var usable = stars.Where(s => !s.Saturated && s.Peak + s.Background < 0.5 * clip && s.X >= rr && s.Y >= rr && s.X < w - rr - 1 && s.Y < h - rr - 1)
                .OrderByDescending(s => s.Flux).Take(400)
                .Where(s => !stars.Any(o => o != s && Math.Abs(o.X - s.X) < 2 * rr && Math.Abs(o.Y - s.Y) < 2 * rr)).ToList();
            Func<DetectedStar, bool> centred = s => Math.Abs(s.X - Math.Round(s.X)) < 0.2 && Math.Abs(s.Y - Math.Round(s.Y)) < 0.2;
            var picks = (usable.Count(centred) >= 5 ? usable.Where(centred) : usable).Take(50).ToList();
            if (picks.Count < 5) return null;
            var cuts = new List<float[]>();
            foreach (var s in picks)
            {
                int cx = (int)Math.Round(s.X), cy = (int)Math.Round(s.Y);
                var cut = new float[n * n];
                double sum = 0;
                for (int y = -r; y <= r; y++)
                    for (int x = -r; x <= r; x++) { double v = lum[(cy + y) * w + cx + x] - s.Background; cut[(y + r) * n + x + r] = (float)v; sum += v; }
                if (sum <= 0) continue;
                for (int i = 0; i < cut.Length; i++) cut[i] /= (float)sum;
                cuts.Add(cut);
            }
            if (cuts.Count < 5) return null;
            var psf = new float[n * n];
            var col = new float[cuts.Count];
            for (int i = 0; i < psf.Length; i++)
            {
                for (int k = 0; k < cuts.Count; k++) col[k] = cuts[k][i];
                Array.Sort(col);
                psf[i] = Math.Max(0, col[col.Length / 2]);
            }
            float total = psf.Sum();
            for (int i = 0; i < psf.Length; i++) psf[i] /= total;
            return psf;
        }

        /// <summary>
        /// A star's total flux: the amplitude of the profile, shifted to the star's sub-pixel position, fitted to its pixels
        /// below the clip level. NaN when too little of the profile is left below it (a star clipped far into its wings).
        /// </summary>
        public static double FitFlux(float[] lum, int w, int h, DetectedStar s, float[] psf, int r, double clip)
        {
            int n = 2 * r + 1, cx = (int)Math.Round(s.X), cy = (int)Math.Round(s.Y);
            double num = 0, den = 0, all = 0;
            for (int y = -r; y <= r; y++)
                for (int x = -r; x <= r; x++)
                {
                    int px = cx + x, py = cy + y;
                    double p = Bilinear(psf, n, px - s.X + r, py - s.Y + r);
                    all += p * p;
                    if (px < 0 || py < 0 || px >= w || py >= h) continue;
                    double v = lum[py * w + px];
                    if (v >= clip) continue;
                    num += p * (v - s.Background);
                    den += p * p;
                }
            return den >= 0.02 * all ? num / den : double.NaN;
        }

        private static double Bilinear(float[] g, int n, double x, double y)
        {
            int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
            double fx = x - x0, fy = y - y0;
            Func<int, int, double> at = (i, j) => i < 0 || j < 0 || i >= n || j >= n ? 0 : g[j * n + i];
            return (1 - fy) * ((1 - fx) * at(x0, y0) + fx * at(x0 + 1, y0)) + fy * ((1 - fx) * at(x0, y0 + 1) + fx * at(x0 + 1, y0 + 1));
        }
    }
}
