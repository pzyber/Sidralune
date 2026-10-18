using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Sidralune.Core.Mathematics;

namespace Sidralune.Imaging.Analysis
{
    public class DetectedStar
    {
        public double X, Y;          // centroid (pixels, 0-based)
        public double Flux;          // background-subtracted sum
        public double Peak;          // peak above background
        public double Background;
        public double Fwhm;          // pixels
        public double Hfr;           // half flux radius (pixels)
        public double Eccentricity;  // 0 = round
        public double Theta;         // major-axis angle (rad)
        public double Snr;
        public int Area;
        public bool Saturated;
        public double InstMag => Flux > 0 ? -2.5 * Math.Log10(Flux) : 99;
        public override string ToString() => string.Format("({0:0.00},{1:0.00}) flux {2:0.000} fwhm {3:0.00} hfr {4:0.00}", X, Y, Flux, Fwhm, Hfr);
    }

    public class StarDetectionOptions
    {
        /// <summary>Detection threshold in background sigmas.</summary>
        public double Sigma = 5.0;
        public int MinArea = 4;
        public int MaxArea = 4000;
        public int MaxStars = 2000;
        public int BackgroundCell = 64;
        public double SaturationLevel = 0.98;
        public double MaxEccentricity = 0.85;
        /// <summary>Gaussian pre-smoothing FWHM in pixels (matched filter); 0 disables.</summary>
        public double SmoothFwhm = 2.0;
        public bool RejectHotPixels = true;
        public bool FitPsf = false;
    }

    /// <summary>
    /// Star detection: mesh background, matched-filter smoothing, thresholded connected components,
    /// intensity-weighted centroids, second-moment shape, HFR, optional Gaussian/Moffat PSF fit.
    /// </summary>
    public static class StarDetector
    {
        public static List<DetectedStar> Detect(float[] d, int w, int h, StarDetectionOptions opt = null)
        {
            opt = opt ?? new StarDetectionOptions();
            float[] rms;
            var bg = BackgroundMesh.Estimate(d, w, h, Math.Max(16, Math.Min(opt.BackgroundCell, Math.Min(w, h) / 2)), out rms);
            var sub = new float[d.Length];
            Parallel.For(0, h, y => { int o = y * w; for (int x = 0; x < w; x++) sub[o + x] = d[o + x] - bg[o + x]; });
            var det = opt.SmoothFwhm > 0 ? Filters.GaussianBlur(sub, w, h, opt.SmoothFwhm / 2.3548) : sub;
            // smoothed noise is lower; scale threshold accordingly
            double noiseScale = opt.SmoothFwhm > 0 ? 1.0 / (2 * Math.Sqrt(Math.PI) * opt.SmoothFwhm / 2.3548) : 1.0;
            noiseScale = Math.Min(1, Math.Max(noiseScale, 0.15));
            var label = new int[d.Length];
            var stars = new List<DetectedStar>();
            var stack = new Stack<int>();
            int border = 3;
            for (int y = border; y < h - border; y++)
            {
                for (int x = border; x < w - border; x++)
                {
                    int i = y * w + x;
                    if (label[i] != 0) continue;
                    double thr = opt.Sigma * Math.Max(rms[i], 1e-7) * noiseScale;
                    if (det[i] <= thr) continue;
                    // flood fill (4-connected) the region above threshold
                    var pix = new List<int>();
                    stack.Push(i);
                    label[i] = -1;
                    bool touchesBorder = false;
                    while (stack.Count > 0 && pix.Count <= opt.MaxArea)
                    {
                        int p = stack.Pop();
                        pix.Add(p);
                        int px = p % w, py = p / w;
                        if (px <= border || py <= border || px >= w - border - 1 || py >= h - border - 1) touchesBorder = true;
                        for (int k = 0; k < 4; k++)
                        {
                            int q = k == 0 ? p - 1 : k == 1 ? p + 1 : k == 2 ? p - w : p + w;
                            if (q < 0 || q >= d.Length || label[q] != 0) continue;
                            if (det[q] > opt.Sigma * Math.Max(rms[q], 1e-7) * noiseScale * 0.6) { label[q] = -1; stack.Push(q); }
                        }
                    }
                    stack.Clear();
                    if (pix.Count < opt.MinArea || pix.Count > opt.MaxArea || touchesBorder) continue;
                    var s = Measure(d, sub, w, h, pix, bg, rms, opt);
                    if (s != null) stars.Add(s);
                }
            }
            // reject hot pixels / cosmic rays: very compact sources with small FWHM
            if (opt.RejectHotPixels)
                stars = stars.Where(s => s.Fwhm >= 1.0 || s.Area >= 9).ToList();
            stars = stars.Where(s => s.Eccentricity <= opt.MaxEccentricity || s.Saturated).ToList();
            stars.Sort((a, b) => b.Flux.CompareTo(a.Flux));
            if (stars.Count > opt.MaxStars) stars = stars.Take(opt.MaxStars).ToList();
            if (opt.FitPsf) foreach (var s in stars.Take(300)) PsfFit.FitGaussian(d, w, h, s);
            return stars;
        }

        public static List<DetectedStar> Detect(AstroImage img, StarDetectionOptions opt = null) => Detect(img.Luminance(), img.Width, img.Height, opt);

        private static DetectedStar Measure(float[] d, float[] sub, int w, int h, List<int> pix, float[] bg, float[] rms, StarDetectionOptions opt)
        {
            double sum = 0, sx = 0, sy = 0, peak = 0;
            bool sat = false;
            foreach (var p in pix)
            {
                double v = sub[p];
                if (v <= 0) continue;
                sum += v; sx += v * (p % w); sy += v * (p / w);
                if (v > peak) peak = v;
                if (d[p] >= opt.SaturationLevel) sat = true;
            }
            if (sum <= 0) return null;
            double cx = sx / sum, cy = sy / sum;
            // refine centroid and shape in a circular aperture around the peak
            double r = Math.Max(3, Math.Sqrt(pix.Count / Math.PI) * 2.0);
            int x0 = (int)Math.Max(0, cx - r), x1 = (int)Math.Min(w - 1, cx + r), y0 = (int)Math.Max(0, cy - r), y1 = (int)Math.Min(h - 1, cy + r);
            for (int it = 0; it < 3; it++)
            {
                double s0 = 0, s1 = 0, s2 = 0;
                for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++)
                    {
                        double dx = x - cx, dy = y - cy;
                        if (dx * dx + dy * dy > r * r) continue;
                        double v = sub[y * w + x];
                        if (v <= 0) continue;
                        s0 += v; s1 += v * x; s2 += v * y;
                    }
                if (s0 <= 0) break;
                cx = s1 / s0; cy = s2 / s0;
            }
            // total flux and half-flux radius within the aperture (signed values: unbiased by noise)
            double m0 = 0;
            var radial = new List<KeyValuePair<double, double>>();
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    double dx = x - cx, dy = y - cy, rr = dx * dx + dy * dy;
                    if (rr > r * r) continue;
                    double v = sub[y * w + x];
                    m0 += v;
                    radial.Add(new KeyValuePair<double, double>(Math.Sqrt(rr), v));
                }
            if (m0 <= 0) return null;
            radial.Sort((a, b) => a.Key.CompareTo(b.Key));
            double acc = 0, hfr = 0;
            foreach (var kv in radial)
            {
                acc += kv.Value;
                if (acc >= m0 / 2) { hfr = kv.Key; break; }
            }
            // adaptive Gaussian-weighted moments (Bernstein & Jarvis 2002): fixed point sigma_w^2 = 2 <r^2>/2
            double sw2 = Math.Max(0.5, hfr * hfr / 1.386); // HFR = 1.1774 sigma for a Gaussian
            double mxx = 0, myy = 0, mxy = 0;
            for (int it = 0; it < 12; it++)
            {
                double ws = 0, sx2 = 0, sy2 = 0, sxy = 0, scx = 0, scy = 0;
                for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++)
                    {
                        double dx = x - cx, dy = y - cy, rr = dx * dx + dy * dy;
                        if (rr > r * r) continue;
                        double v = sub[y * w + x];
                        double wt = v * Math.Exp(-0.5 * rr / sw2);
                        ws += wt; sx2 += wt * dx * dx; sy2 += wt * dy * dy; sxy += wt * dx * dy; scx += wt * dx; scy += wt * dy;
                    }
                if (ws <= 0) break;
                mxx = sx2 / ws; myy = sy2 / ws; mxy = sxy / ws;
                cx += scx / ws; cy += scy / ws;
                double nsw2 = Math.Max(0.25, mxx + myy);
                if (Math.Abs(nsw2 - sw2) < 1e-4 * sw2) { sw2 = nsw2; break; }
                sw2 = nsw2;
            }
            // at the fixed point the true moments are twice the weighted ones; remove pixel-integration variance (1/12)
            double txx = Math.Max(0.02, 2 * mxx - 1.0 / 12), tyy = Math.Max(0.02, 2 * myy - 1.0 / 12), txy = 2 * mxy;
            double tr = txx + tyy, det = txx * tyy - txy * txy;
            double disc = Math.Sqrt(Math.Max(0, tr * tr / 4 - det));
            double l1 = tr / 2 + disc, l2 = Math.Max(1e-9, tr / 2 - disc);
            double fwhm = 2.3548 * Math.Sqrt(Math.Sqrt(l1 * l2));
            mxx = txx; myy = tyy; mxy = txy;
            int ic = (int)Math.Round(cy) * w + (int)Math.Round(cx);
            double noise = ic >= 0 && ic < rms.Length ? rms[ic] : 1e-6;
            double snr = m0 / Math.Sqrt(m0 * 0 + radial.Count * noise * noise + 1e-30);
            return new DetectedStar
            {
                X = cx, Y = cy, Flux = m0, Peak = peak, Background = ic >= 0 && ic < bg.Length ? bg[ic] : 0,
                Fwhm = fwhm, Hfr = Math.Max(0.5, hfr), Eccentricity = Math.Sqrt(1 - l2 / l1), Theta = 0.5 * Math.Atan2(2 * mxy, mxx - myy),
                Snr = snr, Area = pix.Count, Saturated = sat
            };
        }

        /// <summary>Median FWHM, HFR and eccentricity of a star list (for frame quality / autofocus).</summary>
        public static void Summary(List<DetectedStar> stars, out double fwhm, out double hfr, out double ecc)
        {
            if (stars.Count == 0) { fwhm = hfr = ecc = double.NaN; return; }
            var use = stars.Where(s => !s.Saturated).ToList();
            if (use.Count < 3) use = stars;
            fwhm = Stats.Median(use.Select(s => s.Fwhm).ToList());
            hfr = Stats.Median(use.Select(s => s.Hfr).ToList());
            ecc = Stats.Median(use.Select(s => s.Eccentricity).ToList());
        }
    }

    /// <summary>2D elliptical Gaussian and Moffat PSF fitting (Levenberg-Marquardt).</summary>
    public static class PsfFit
    {
        public class Result
        {
            public double Amplitude, X, Y, SigmaX, SigmaY, Theta, Background, Beta = double.NaN;
            public double FwhmX => 2.3548 * SigmaX;
            public double FwhmY => 2.3548 * SigmaY;
            public double Fwhm => Math.Sqrt(FwhmX * FwhmY);
            public double ReducedChi2;
        }

        public static Result FitGaussian(float[] d, int w, int h, DetectedStar s, int radius = 0)
        {
            int r = radius > 0 ? radius : (int)Math.Max(4, Math.Ceiling(s.Fwhm * 2));
            int x0 = (int)Math.Max(0, s.X - r), x1 = (int)Math.Min(w - 1, s.X + r), y0 = (int)Math.Max(0, s.Y - r), y1 = (int)Math.Min(h - 1, s.Y + r);
            var xs = new List<int>(); var ys = new List<int>(); var vs = new List<double>();
            for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++) { xs.Add(x); ys.Add(y); vs.Add(d[y * w + x]); }
            if (vs.Count < 10) return null;
            double sig0 = Math.Max(0.6, s.Fwhm / 2.3548);
            var p0 = new[] { s.Peak, s.X, s.Y, sig0, sig0, 0.0, s.Background };
            var fit = LevenbergMarquardt.Fit((p, i) =>
            {
                double dx = xs[i] - p[1], dy = ys[i] - p[2];
                double c = Math.Cos(p[5]), sn = Math.Sin(p[5]);
                double u = (dx * c + dy * sn) / p[3], v = (-dx * sn + dy * c) / p[4];
                return p[0] * Math.Exp(-0.5 * (u * u + v * v)) + p[6];
            }, vs.Count, vs.ToArray(), null, p0,
            new[] { 0, s.X - 3, s.Y - 3, 0.3, 0.3, -Math.PI, double.NaN },
            new[] { double.NaN, s.X + 3, s.Y + 3, r, r, Math.PI, double.NaN }, 60);
            var q = fit.Parameters;
            var res = new Result { Amplitude = q[0], X = q[1], Y = q[2], SigmaX = Math.Abs(q[3]), SigmaY = Math.Abs(q[4]), Theta = q[5], Background = q[6], ReducedChi2 = fit.ReducedChiSquare };
            s.X = res.X; s.Y = res.Y; s.Fwhm = res.Fwhm;
            double a = Math.Max(res.SigmaX, res.SigmaY), b = Math.Min(res.SigmaX, res.SigmaY);
            s.Eccentricity = Math.Sqrt(1 - (b * b) / (a * a));
            return res;
        }

        public static Result FitMoffat(float[] d, int w, int h, DetectedStar s, int radius = 0)
        {
            int r = radius > 0 ? radius : (int)Math.Max(5, Math.Ceiling(s.Fwhm * 2.5));
            int x0 = (int)Math.Max(0, s.X - r), x1 = (int)Math.Min(w - 1, s.X + r), y0 = (int)Math.Max(0, s.Y - r), y1 = (int)Math.Min(h - 1, s.Y + r);
            var xs = new List<int>(); var ys = new List<int>(); var vs = new List<double>();
            for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++) { xs.Add(x); ys.Add(y); vs.Add(d[y * w + x]); }
            if (vs.Count < 10) return null;
            double alpha0 = Math.Max(0.6, s.Fwhm / 2.0);
            var p0 = new[] { s.Peak, s.X, s.Y, alpha0, 3.0, s.Background };
            var fit = LevenbergMarquardt.Fit((p, i) =>
            {
                double dx = xs[i] - p[1], dy = ys[i] - p[2];
                return p[0] * Math.Pow(1 + (dx * dx + dy * dy) / (p[3] * p[3]), -p[4]) + p[5];
            }, vs.Count, vs.ToArray(), null, p0, new[] { 0, s.X - 3, s.Y - 3, 0.3, 1.01, double.NaN }, new[] { double.NaN, s.X + 3, s.Y + 3, r, 20, double.NaN }, 60);
            var q = fit.Parameters;
            double fwhm = 2 * q[3] * Math.Sqrt(Math.Pow(2, 1 / q[4]) - 1);
            return new Result { Amplitude = q[0], X = q[1], Y = q[2], SigmaX = fwhm / 2.3548, SigmaY = fwhm / 2.3548, Beta = q[4], Background = q[5], ReducedChi2 = fit.ReducedChiSquare };
        }
    }
}
