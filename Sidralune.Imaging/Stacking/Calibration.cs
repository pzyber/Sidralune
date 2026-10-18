using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Sidralune.Core.Mathematics;
using Sidralune.Imaging.Analysis;
using Sidralune.Imaging.IO;

namespace Sidralune.Imaging.Stacking
{
    public enum FrameKind { Light, Dark, Flat, DarkFlat, Bias }

    /// <summary>Calibration master set and options (mirrors DeepSkyStacker's calibration settings).</summary>
    public class CalibrationSet
    {
        public AstroImage MasterBias, MasterDark, MasterFlat, MasterDarkFlat;
        /// <summary>Scale the dark by exposure time ratio (needs bias, or dark already bias-subtracted).</summary>
        public bool ScaleDarkByExposure = true;
        /// <summary>Find the dark multiplier that minimises noise in the calibrated light (Siril/PixInsight "dark optimisation").</summary>
        public bool OptimizeDark = false;
        public bool HotPixelRemoval = true;
        public double HotPixelSigma = 6.0;
        public bool ColdPixelRemoval = true;
        public double ColdPixelSigma = 6.0;
        /// <summary>Pedestal (0..1) added after dark subtraction to avoid clipping negative values.</summary>
        public double Pedestal = 0.0;
        /// <summary>Hot pixel map derived from the master dark (true = defective).</summary>
        public bool[] DefectMap;
        public bool MasterDarkIsBiasSubtracted;
        public double MasterDarkExposure;

        public bool HasAny => MasterBias != null || MasterDark != null || MasterFlat != null;

        private AstroImage _thermal;
        private readonly object _lock = new object();

        /// <summary>Dark current only (master dark minus bias) when possible; otherwise the master dark itself.</summary>
        public AstroImage ThermalDark()
        {
            if (MasterDark == null) return null;
            if (MasterDarkIsBiasSubtracted || MasterBias == null) return MasterDark;
            lock (_lock)
            {
                if (_thermal == null)
                {
                    _thermal = MasterDark.Clone();
                    Calibration.Subtract(_thermal, MasterBias, 0);
                }
                return _thermal;
            }
        }
    }

    public enum CombineMethod
    {
        Average, Median, KappaSigma, MedianKappaSigma, AutoAdaptiveWeightedAverage, EntropyWeightedAverage, Maximum, Minimum,
        WinsorizedSigma, LinearFitClipping, GeneralizedEsd, PercentileClipping
    }

    public class CombineOptions
    {
        public CombineMethod Method = CombineMethod.KappaSigma;
        public double KappaLow = 2.5, KappaHigh = 2.5;
        public int Iterations = 5;
        public double PercentileLow = 0.2, PercentileHigh = 0.1;
        /// <summary>Significance for Generalised ESD (Rosner 1983) and max outlier fraction.</summary>
        public double EsdAlpha = 0.05, EsdOutlierFraction = 0.3;
    }

    /// <summary>Calibration of frames and creation of master calibration frames.</summary>
    public static class Calibration
    {
        /// <summary>Combine calibration frames into a master (streamed row bands to bound memory).</summary>
        public static AstroImage CreateMaster(IList<string> files, FrameKind kind, CombineOptions opt, AstroImage masterBias = null, AstroImage masterDarkFlat = null,
            IProgress<double> progress = null, CancellationToken ct = default(CancellationToken))
        {
            if (files.Count == 0) return null;
            var first = ImageFile.Load(files[0]);
            int w = first.Width, h = first.Height, ch = first.Channels;
            var frames = new List<AstroImage> { first };
            for (int i = 1; i < files.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                frames.Add(ImageFile.Load(files[i]));
                if (progress != null) progress.Report(0.5 * i / files.Count);
            }
            // flats: subtract bias/dark-flat, normalise each frame (per CFA colour) before combination
            var sub = kind == FrameKind.Flat ? (masterDarkFlat ?? masterBias) : (kind == FrameKind.Dark || kind == FrameKind.DarkFlat ? masterBias : null);
            foreach (var f in frames)
            {
                if (sub != null) Subtract(f, sub, 0);
                if (kind == FrameKind.Flat) NormalizeFlat(f);
            }
            var master = new AstroImage(w, h, ch) { Header = first.Header.Clone(), Bayer = first.Bayer, OriginalBitDepth = first.OriginalBitDepth };
            var weights = Enumerable.Repeat(1.0, frames.Count).ToArray();
            for (int c = 0; c < ch; c++)
            {
                var planes = frames.Select(f => f.Data[c]).ToArray();
                var dst = master.Data[c];
                int done = 0;
                Parallel.For(0, h, () => Tuple.Create(new float[frames.Count], new PixelCombiner(frames.Count, opt, weights)), (y, st, tl) =>
                {
                    int o = y * w;
                    var buf = tl.Item1;
                    for (int x = 0; x < w; x++)
                    {
                        for (int k = 0; k < planes.Length; k++) buf[k] = planes[k][o + x];
                        dst[o + x] = tl.Item2.Combine(buf, planes.Length);
                    }
                    int d = Interlocked.Increment(ref done);
                    if (progress != null && (d & 63) == 0) progress.Report(0.5 + 0.5 * ((double)c / ch + (double)d / h / ch));
                    return tl;
                }, tl => { });
            }
            master.Header.Set("IMAGETYP", kind == FrameKind.Bias ? "Master Bias" : kind == FrameKind.Dark ? "Master Dark" : kind == FrameKind.Flat ? "Master Flat" : "Master Dark Flat");
            master.Header.Set("NCOMBINE", frames.Count, "Number of combined frames");
            master.Header.Set("EXPTIME", frames.Average(f => f.ExposureSeconds));
            master.Header.AddHistory("Combined with " + opt.Method + " by Sidralune");
            if (kind == FrameKind.Flat) NormalizeFlat(master);
            return master;
        }

        /// <summary>a -= b*scale (pixelwise, channels broadcast).</summary>
        public static void Subtract(AstroImage a, AstroImage b, double pedestal, double scale = 1.0)
        {
            if (a.Width != b.Width || a.Height != b.Height) throw new InvalidOperationException("Calibration frame size mismatch: " + a + " vs " + b);
            for (int c = 0; c < a.Channels; c++)
            {
                var d = a.Data[c]; var s = b.Data[Math.Min(c, b.Channels - 1)];
                float sc = (float)scale, ped = (float)pedestal;
                Parallel.For(0, a.Height, y => { int o = y * a.Width; for (int x = 0; x < a.Width; x++) d[o + x] = d[o + x] - sc * s[o + x] + ped; });
            }
        }

        /// <summary>Normalise a flat so each CFA colour (or channel) has mean 1.</summary>
        public static void NormalizeFlat(AstroImage f)
        {
            if (f.Channels == 1 && f.Bayer != BayerPattern.None)
            {
                var sums = new double[3]; var counts = new long[3];
                int w = f.Width, h = f.Height;
                var d = f.Data[0];
                // central 50% for robustness against vignetting extremes
                for (int y = h / 4; y < 3 * h / 4; y++)
                    for (int x = w / 4; x < 3 * w / 4; x++)
                    {
                        int c = Debayer.ColorAt(f.Bayer, x, y);
                        sums[c] += d[y * w + x]; counts[c]++;
                    }
                var mean = new float[3];
                for (int c = 0; c < 3; c++) mean[c] = counts[c] > 0 ? (float)(sums[c] / counts[c]) : 1;
                Parallel.For(0, h, y =>
                {
                    for (int x = 0; x < w; x++)
                    {
                        int c = Debayer.ColorAt(f.Bayer, x, y);
                        d[y * w + x] = mean[c] > 0 ? d[y * w + x] / mean[c] : 1;
                    }
                });
                return;
            }
            for (int c = 0; c < f.Channels; c++)
            {
                var d = f.Data[c];
                double s = 0; long n = 0;
                int w = f.Width, h = f.Height;
                for (int y = h / 4; y < 3 * h / 4; y++) for (int x = w / 4; x < 3 * w / 4; x++) { s += d[y * w + x]; n++; }
                float m = (float)(s / Math.Max(1, n));
                if (m <= 0) m = 1;
                for (int i = 0; i < d.Length; i++) d[i] /= m;
            }
        }

        /// <summary>Find hot pixels in a master dark: pixels more than sigma above a local 5x5 median.</summary>
        public static bool[] HotPixelMap(AstroImage dark, double sigma)
        {
            int w = dark.Width, h = dark.Height;
            var d = dark.Data[0];
            var st = ImageStatistics.Compute(d, w, 0);
            double thr = st.Median + sigma * Math.Max(st.Mad * Stats.MadToSigma, 1e-7);
            var map = new bool[d.Length];
            for (int i = 0; i < d.Length; i++) map[i] = d[i] > thr;
            return map;
        }

        /// <summary>Calibrate a light frame in place.</summary>
        public static void CalibrateLight(AstroImage light, CalibrationSet cal)
        {
            if (cal == null) return;
            double ped = cal.Pedestal;
            if (cal.MasterBias != null) Subtract(light, cal.MasterBias, 0);
            if (cal.MasterDark != null)
            {
                var thermal = cal.ThermalDark();
                bool scalable = thermal != cal.MasterDark || cal.MasterDarkIsBiasSubtracted;
                double k = 1.0;
                if (scalable)
                {
                    if (cal.ScaleDarkByExposure && cal.MasterDarkExposure > 0 && light.ExposureSeconds > 0)
                        k = light.ExposureSeconds / cal.MasterDarkExposure;
                    if (cal.OptimizeDark) k = OptimizeDarkScale(light, thermal, k);
                }
                // a dark that still contains the bias (and no bias frame was supplied) can only be subtracted 1:1
                Subtract(light, thermal, 0, k);
                light.Header.Set("DARKSCAL", k, "Dark scaling factor");
            }
            if (ped != 0) foreach (var ch in light.Data) for (int i = 0; i < ch.Length; i++) ch[i] += (float)ped;
            if (cal.MasterFlat != null)
            {
                var f = cal.MasterFlat;
                for (int c = 0; c < light.Channels; c++)
                {
                    var d = light.Data[c]; var fl = f.Data[Math.Min(c, f.Channels - 1)];
                    Parallel.For(0, light.Height, y =>
                    {
                        int o = y * light.Width;
                        for (int x = 0; x < light.Width; x++)
                        {
                            float fv = fl[o + x];
                            d[o + x] = fv > 0.05f ? d[o + x] / fv : d[o + x];
                        }
                    });
                }
            }
            if (cal.HotPixelRemoval || cal.ColdPixelRemoval)
                CosmeticCorrection(light, cal.DefectMap, cal.HotPixelRemoval ? cal.HotPixelSigma : 0, cal.ColdPixelRemoval ? cal.ColdPixelSigma : 0);
            light.Header.AddHistory("Calibrated by Sidralune");
        }

        /// <summary>Golden-section search for the dark multiplier minimising the calibrated frame's noise.</summary>
        public static double OptimizeDarkScale(AstroImage light, AstroImage dark, double initial)
        {
            int w = light.Width, h = light.Height;
            // use the central crop for speed
            int cw = Math.Min(w, 1024), chh = Math.Min(h, 1024), x0 = (w - cw) / 2, y0 = (h - chh) / 2;
            var l = new float[cw * chh]; var dk = new float[cw * chh];
            for (int y = 0; y < chh; y++)
            {
                Array.Copy(light.Data[0], (y0 + y) * w + x0, l, y * cw, cw);
                Array.Copy(dark.Data[0], (y0 + y) * w + x0, dk, y * cw, cw);
            }
            var tmp = new float[l.Length];
            Func<double, double> noise = k =>
            {
                for (int i = 0; i < l.Length; i++) tmp[i] = (float)(l[i] - k * dk[i]);
                return ImageStatistics.MrsNoise(tmp, cw, chh);
            };
            return Roots.GoldenMin(noise, Math.Max(0, initial * 0.2), initial * 2.0 + 0.5, 1e-3);
        }

        /// <summary>
        /// Replace defective pixels by the median of same-colour neighbours. Detection: map (from dark), plus any pixel
        /// deviating more than k sigma from its local median.
        /// </summary>
        public static int CosmeticCorrection(AstroImage img, bool[] defectMap, double hotSigma, double coldSigma)
        {
            int w = img.Width, h = img.Height, fixedCount = 0;
            bool cfa = img.Bayer != BayerPattern.None && img.Channels == 1;
            int step = cfa ? 2 : 1;
            for (int c = 0; c < img.Channels; c++)
            {
                var d = img.Data[c];
                var st = ImageStatistics.Compute(d, w, 0);
                double sigma = Math.Max(st.Noise, st.Mad * Stats.MadToSigma);
                var o = (float[])d.Clone();
                int count = 0;
                Parallel.For(0, h, () => new float[8], (y, s, buf) =>
                {
                    for (int x = 0; x < w; x++)
                    {
                        int i = y * w + x;
                        int k = 0;
                        for (int dy = -step; dy <= step; dy += step)
                            for (int dx = -step; dx <= step; dx += step)
                            {
                                if (dx == 0 && dy == 0) continue;
                                int xx = x + dx, yy = y + dy;
                                if (xx < 0 || yy < 0 || xx >= w || yy >= h) continue;
                                buf[k++] = d[yy * w + xx];
                            }
                        if (k < 3) continue;
                        float med = Stats.MedianInPlace(buf, k);
                        bool bad = defectMap != null && defectMap[i];
                        double dev = d[i] - med;
                        if (hotSigma > 0 && dev > hotSigma * sigma) bad = true;
                        if (coldSigma > 0 && -dev > coldSigma * sigma) bad = true;
                        // protect star cores: a real star has bright neighbours too
                        if (bad && !(defectMap != null && defectMap[i]))
                        {
                            float mx = float.MinValue;
                            for (int q = 0; q < k; q++) if (buf[q] > mx) mx = buf[q];
                            if (dev > 0 && mx - med > 0.5 * dev) bad = false;
                        }
                        if (bad) { o[i] = med; Interlocked.Increment(ref count); }
                    }
                    return buf;
                }, buf => { });
                Array.Copy(o, d, d.Length);
                fixedCount += count;
            }
            img.Header.Set("COSMETIC", fixedCount, "Pixels replaced by cosmetic correction");
            return fixedCount;
        }
    }
}
