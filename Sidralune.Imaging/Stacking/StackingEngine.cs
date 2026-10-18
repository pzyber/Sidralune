using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Sidralune.Core.Mathematics;
using Sidralune.Imaging.Analysis;
using Sidralune.Imaging.IO;
using Sidralune.Imaging.Registration;

namespace Sidralune.Imaging.Stacking
{
    public class StackFrame
    {
        public string Path;
        public FrameKind Kind = FrameKind.Light;
        public bool Enabled = true;
        public string Group = "Main";
        // results of registration
        public bool Registered;
        public double Score;
        public int StarCount;
        public double Fwhm = double.NaN, Hfr = double.NaN, Eccentricity = double.NaN;
        public double Background = double.NaN, Noise = double.NaN, Snr = double.NaN;
        public double Exposure;
        public DateTime? DateObs;
        public string Filter;
        public double Iso = double.NaN, Gain = double.NaN, Temperature = double.NaN;
        public double[] TransformMatrix; // target -> reference
        public double RmsError = double.NaN;
        public int Matches;
        public double[] CometXY;         // comet position in this frame (pixels), if known
        public string Status = "";
        public bool IsReference;
        [NonSerialized] public List<DetectedStar> Stars;
        public string FileName => System.IO.Path.GetFileName(Path);
    }

    public enum StackingMode { Standard, Mosaic, Intersection, Custom }
    public enum NormalizationMode { None, Additive, AdditiveScaling, Multiplicative }
    public enum WeightingMode { None, Noise, Score, Snr }
    public enum CometMode { None, CometAligned, StarsAndComet }

    public class StackingSettings
    {
        public CombineOptions LightCombine = new CombineOptions { Method = CombineMethod.KappaSigma, KappaLow = 2.5, KappaHigh = 2.5, Iterations = 5 };
        public CombineOptions DarkCombine = new CombineOptions { Method = CombineMethod.MedianKappaSigma, KappaLow = 3, KappaHigh = 3, Iterations = 5 };
        public CombineOptions FlatCombine = new CombineOptions { Method = CombineMethod.Median };
        public CombineOptions BiasCombine = new CombineOptions { Method = CombineMethod.Median };
        public DebayerMethod Debayer = DebayerMethod.EdgeDirected;
        public BayerPattern? BayerOverride;
        public double DetectionSigma = 5;
        public int MaxStars = 1500;
        public TransformModel Alignment = TransformModel.Affine;
        public double KeepPercent = 100;
        public double MinScoreRatio = 0;   // reject frames scoring below ratio*reference
        public StackingMode Mode = StackingMode.Standard;
        public int[] CustomRect;            // x, y, w, h in reference coordinates
        public NormalizationMode Normalization = NormalizationMode.AdditiveScaling;
        public WeightingMode Weighting = WeightingMode.Noise;
        public int DrizzleScale = 1;
        public double DrizzlePixFrac = 0.8;
        public CometMode Comet = CometMode.None;
        public Interpolation Interp = Interpolation.Lanczos3;
        public bool PerChannelBackgroundCalibration = true;
        public bool HotPixelRemoval = true, ColdPixelRemoval = true;
        public bool OptimizeDark = false;
        public double Pedestal = 0;
        public string TempFolder;
        public string ReferencePath;
        public bool KeepTempFiles = false;
        public bool SaveCalibrated = false;
        public string CalibratedFolder;
    }

    public class StackProgress
    {
        public string Stage;
        public double Fraction;
        public string Detail;
        public StackProgress(string s, double f, string d = null) { Stage = s; Fraction = f; Detail = d; }
    }

    public class StackResult
    {
        public AstroImage Image;
        public AstroImage CometImage;
        public AstroImage Rejection; // rejected-pixel fraction map
        public int FramesStacked;
        public double TotalExposure;
        public StackFrame Reference;
        public TimeSpan Duration;
        public string Report;
    }

    /// <summary>Deep-sky calibration, registration and stacking pipeline.</summary>
    public class StackingEngine
    {
        public StackingSettings Settings = new StackingSettings();
        public List<StackFrame> Frames = new List<StackFrame>();
        public AstroImage MasterBias, MasterDark, MasterFlat, MasterDarkFlat;
        public event Action<string> Message;

        private void Msg(string s) { Message?.Invoke(s); Core.Util.Log.Info("Stacking", s); }

        public IEnumerable<StackFrame> Lights => Frames.Where(f => f.Kind == FrameKind.Light && f.Enabled);

        public static FrameKind GuessKind(string path, AstroImage hdr = null)
        {
            string n = System.IO.Path.GetFileName(path).ToLowerInvariant();
            string t = hdr != null ? (hdr.ImageType ?? "").ToLowerInvariant() : "";
            Func<string, bool> has = k => n.Contains(k) || t.Contains(k);
            if (has("darkflat") || has("dark flat") || has("flatdark") || has("flat dark")) return FrameKind.DarkFlat;
            if (has("bias") || has("offset") || has("zero")) return FrameKind.Bias;
            if (has("dark")) return FrameKind.Dark;
            if (has("flat")) return FrameKind.Flat;
            return FrameKind.Light;
        }

        public void AddFiles(IEnumerable<string> paths, FrameKind? kind = null)
        {
            foreach (var p in paths)
            {
                if (Frames.Any(f => string.Equals(f.Path, p, StringComparison.OrdinalIgnoreCase))) continue;
                var fr = new StackFrame { Path = p, Kind = kind ?? GuessKind(p) };
                try
                {
                    var img = ImageFile.Load(p);
                    fr.Exposure = img.ExposureSeconds; fr.DateObs = img.DateObs; fr.Filter = img.Filter;
                    fr.Iso = img.Header.GetDouble("ISOSPEED"); fr.Gain = img.Gain; fr.Temperature = img.Temperature;
                    if (kind == null) fr.Kind = GuessKind(p, img);
                }
                catch (Exception ex) { fr.Status = "Unreadable: " + ex.Message; fr.Enabled = false; }
                Frames.Add(fr);
            }
        }

        private List<string> FilesOf(FrameKind k) => Frames.Where(f => f.Kind == k && f.Enabled).Select(f => f.Path).ToList();

        // ---------------------------------------------------------------- masters

        public CalibrationSet BuildCalibration(IProgress<StackProgress> progress, CancellationToken ct)
        {
            var s = Settings;
            var cal = new CalibrationSet { HotPixelRemoval = s.HotPixelRemoval, ColdPixelRemoval = s.ColdPixelRemoval, OptimizeDark = s.OptimizeDark, Pedestal = s.Pedestal };
            Func<string, IProgress<double>> pr = stage => new Progress<double>(f => progress?.Report(new StackProgress(stage, f)));
            if (MasterBias == null && FilesOf(FrameKind.Bias).Count > 0)
            {
                Msg("Creating master bias from " + FilesOf(FrameKind.Bias).Count + " frames");
                MasterBias = Calibration.CreateMaster(FilesOf(FrameKind.Bias), FrameKind.Bias, s.BiasCombine, null, null, pr("Master bias"), ct);
            }
            if (MasterDark == null && FilesOf(FrameKind.Dark).Count > 0)
            {
                Msg("Creating master dark from " + FilesOf(FrameKind.Dark).Count + " frames");
                MasterDark = Calibration.CreateMaster(FilesOf(FrameKind.Dark), FrameKind.Dark, s.DarkCombine, MasterBias, null, pr("Master dark"), ct);
                if (MasterBias != null) MasterDark.Header.Set("BIASSUB", true);
            }
            if (MasterDarkFlat == null && FilesOf(FrameKind.DarkFlat).Count > 0)
            {
                Msg("Creating master dark flat");
                MasterDarkFlat = Calibration.CreateMaster(FilesOf(FrameKind.DarkFlat), FrameKind.DarkFlat, s.DarkCombine, null, null, pr("Master dark flat"), ct);
            }
            if (MasterFlat == null && FilesOf(FrameKind.Flat).Count > 0)
            {
                Msg("Creating master flat from " + FilesOf(FrameKind.Flat).Count + " frames");
                MasterFlat = Calibration.CreateMaster(FilesOf(FrameKind.Flat), FrameKind.Flat, s.FlatCombine, MasterBias, MasterDarkFlat, pr("Master flat"), ct);
            }
            cal.MasterBias = MasterBias;
            cal.MasterDark = MasterDark;
            cal.MasterFlat = MasterFlat;
            cal.MasterDarkFlat = MasterDarkFlat;
            if (MasterDark != null)
            {
                cal.MasterDarkExposure = MasterDark.ExposureSeconds;
                cal.MasterDarkIsBiasSubtracted = MasterDark.Header.Get("BIASSUB") == "T";
                if (s.HotPixelRemoval) cal.DefectMap = Calibration.HotPixelMap(cal.ThermalDark(), 8);
            }
            return cal;
        }

        // ---------------------------------------------------------------- per-frame preparation

        private AstroImage Prepare(StackFrame f, CalibrationSet cal)
        {
            var img = ImageFile.Load(f.Path);
            Calibration.CalibrateLight(img, cal);
            if (img.Channels == 1 && (img.Bayer != BayerPattern.None || Settings.BayerOverride.HasValue))
                img = Debayer.Apply(img, Settings.Debayer, Settings.BayerOverride);
            return img;
        }

        private static double Score(List<DetectedStar> stars, double fwhm, double ecc)
        {
            if (stars.Count == 0 || double.IsNaN(fwhm)) return 0;
            // favour many sharp, round stars: sum of sqrt(SNR) over stars / (FWHM * (1+ecc))
            double s = 0;
            foreach (var st in stars) s += Math.Sqrt(Math.Max(0, st.Snr));
            return s / (Math.Max(0.5, fwhm) * (1 + ecc));
        }

        /// <summary>Register (and score) all enabled light frames.</summary>
        public void Register(CalibrationSet cal, IProgress<StackProgress> progress, CancellationToken ct)
        {
            var lights = Lights.ToList();
            int done = 0;
            var opt = new StarDetectionOptions { Sigma = Settings.DetectionSigma, MaxStars = Settings.MaxStars };
            Parallel.ForEach(lights, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2), CancellationToken = ct }, f =>
            {
                try
                {
                    var img = Prepare(f, cal);
                    var lum = img.Luminance();
                    var stars = StarDetector.Detect(lum, img.Width, img.Height, opt);
                    double fwhm, hfr, ecc;
                    StarDetector.Summary(stars, out fwhm, out hfr, out ecc);
                    var st = ImageStatistics.Compute(lum, img.Width, 0);
                    f.Stars = stars; f.StarCount = stars.Count; f.Fwhm = fwhm; f.Hfr = hfr; f.Eccentricity = ecc;
                    f.Background = st.Background; f.Noise = st.Noise; f.Snr = ImageStatistics.SnrEstimate(st);
                    f.Score = Score(stars, fwhm, ecc);
                    f.Registered = stars.Count >= 3;
                    f.Status = f.Registered ? "Registered" : "Too few stars";
                }
                catch (Exception ex) { f.Registered = false; f.Status = "Error: " + ex.Message; }
                int d = Interlocked.Increment(ref done);
                progress?.Report(new StackProgress("Registering", (double)d / lights.Count, f.FileName));
            });
            // reference
            StackFrame reference = null;
            if (!string.IsNullOrEmpty(Settings.ReferencePath)) reference = lights.FirstOrDefault(l => string.Equals(l.Path, Settings.ReferencePath, StringComparison.OrdinalIgnoreCase) && l.Registered);
            if (reference == null) reference = lights.Where(l => l.Registered).OrderByDescending(l => l.Score).FirstOrDefault();
            if (reference == null) throw new InvalidOperationException("No light frame could be registered (no stars detected).");
            foreach (var l in lights) l.IsReference = l == reference;
            reference.TransformMatrix = Transform2D.Identity.M;
            reference.Matches = reference.StarCount; reference.RmsError = 0;
            Msg(string.Format("Reference frame: {0} (score {1:0.0}, {2} stars, FWHM {3:0.00}px)", reference.FileName, reference.Score, reference.StarCount, reference.Fwhm));
            foreach (var l in lights)
            {
                if (l == reference || !l.Registered) continue;
                var m = StarMatcher.Match(reference.Stars, l.Stars, Settings.Alignment);
                if (m.Success && m.Matches >= 6 && m.RmsError < 3)
                {
                    l.TransformMatrix = m.Transform.M; l.Matches = m.Matches; l.RmsError = m.RmsError;
                    l.Status = string.Format("Aligned ({0} stars, rms {1:0.00}px)", m.Matches, m.RmsError);
                }
                else { l.Registered = false; l.Status = "Alignment failed"; }
            }
        }

        // ---------------------------------------------------------------- stacking

        public StackResult Run(IProgress<StackProgress> progress = null, CancellationToken ct = default(CancellationToken))
        {
            var t0 = DateTime.Now;
            var cal = BuildCalibration(progress, ct);
            Register(cal, progress, ct);
            return Stack(cal, progress, ct, t0);
        }

        public StackResult Stack(CalibrationSet cal, IProgress<StackProgress> progress, CancellationToken ct, DateTime? started = null)
        {
            var t0 = started ?? DateTime.Now;
            var s = Settings;
            var reg = Lights.Where(l => l.Registered && l.TransformMatrix != null).OrderByDescending(l => l.Score).ToList();
            if (reg.Count == 0) throw new InvalidOperationException("No registered frames to stack");
            var reference = reg.FirstOrDefault(r => r.IsReference) ?? reg[0];
            int keep = Math.Max(1, (int)Math.Round(reg.Count * s.KeepPercent / 100.0));
            var use = reg.Take(keep).Where(r => r.Score >= s.MinScoreRatio * reference.Score).ToList();
            use.Remove(reference);
            use.Insert(0, reference); // normalisation uses index 0 as the reference
            Msg(string.Format("Stacking {0} of {1} registered frames", use.Count, reg.Count));

            // geometry
            var refImg = Prepare(reference, cal);
            int rw = refImg.Width, rh = refImg.Height, ch = refImg.Channels;
            double minX = 0, minY = 0, maxX = rw, maxY = rh;
            if (s.Mode == StackingMode.Mosaic || s.Mode == StackingMode.Intersection)
            {
                bool union = s.Mode == StackingMode.Mosaic;
                if (!union) { minX = double.MinValue; minY = double.MinValue; maxX = double.MaxValue; maxY = double.MaxValue; }
                foreach (var f in use)
                {
                    var t = new Transform2D { M = f.TransformMatrix };
                    double[] xs = new double[4], ys = new double[4];
                    t.Apply(0, 0, out xs[0], out ys[0]); t.Apply(rw, 0, out xs[1], out ys[1]);
                    t.Apply(0, rh, out xs[2], out ys[2]); t.Apply(rw, rh, out xs[3], out ys[3]);
                    if (union) { minX = Math.Min(minX, xs.Min()); minY = Math.Min(minY, ys.Min()); maxX = Math.Max(maxX, xs.Max()); maxY = Math.Max(maxY, ys.Max()); }
                    else
                    {
                        // inscribed axis-aligned bounds (conservative for small rotations)
                        minX = Math.Max(minX, Math.Max(xs[0], xs[2])); maxX = Math.Min(maxX, Math.Min(xs[1], xs[3]));
                        minY = Math.Max(minY, Math.Max(ys[0], ys[1])); maxY = Math.Min(maxY, Math.Min(ys[2], ys[3]));
                    }
                }
                minX = Math.Floor(minX); minY = Math.Floor(minY); maxX = Math.Ceiling(maxX); maxY = Math.Ceiling(maxY);
            }
            else if (s.Mode == StackingMode.Custom && s.CustomRect != null)
            {
                minX = s.CustomRect[0]; minY = s.CustomRect[1]; maxX = minX + s.CustomRect[2]; maxY = minY + s.CustomRect[3];
            }
            int scale = Math.Max(1, s.DrizzleScale);
            int ow = (int)((maxX - minX) * scale), oh = (int)((maxY - minY) * scale);
            var canvas = Transform2D.Affine(scale, 0, -minX * scale, 0, scale, -minY * scale);

            // comet transforms
            Dictionary<StackFrame, Transform2D> cometT = null;
            if (s.Comet != CometMode.None) cometT = CometTransforms(use, reference);

            // normalisation statistics (per frame, per channel) from the reference-calibrated frames
            string temp = s.TempFolder ?? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Sidralune_stack_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(temp);
            var loc = new double[use.Count][]; var scl = new double[use.Count][];
            var weights = new double[use.Count];
            var warpedFiles = new string[use.Count];
            var cometFiles = s.Comet != CometMode.None ? new string[use.Count] : null;
            var entropyFiles = s.LightCombine.Method == CombineMethod.EntropyWeightedAverage ? new string[use.Count] : null;
            int prepared = 0;
            var drizzle = scale > 1 ? new DrizzleAccumulator(ow, oh, ch, s.DrizzlePixFrac) : null;
            for (int i = 0; i < use.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var f = use[i];
                var img = f == reference ? refImg : Prepare(f, cal);
                if (s.SaveCalibrated && !string.IsNullOrEmpty(s.CalibratedFolder))
                {
                    Directory.CreateDirectory(s.CalibratedFolder);
                    FitsIO.Write(System.IO.Path.Combine(s.CalibratedFolder, System.IO.Path.GetFileNameWithoutExtension(f.Path) + "_cal.fits"), img, -32);
                }
                loc[i] = new double[ch]; scl[i] = new double[ch];
                for (int c = 0; c < ch; c++)
                {
                    var st = ImageStatistics.Compute(img.Data[c], img.Width, 0);
                    loc[i][c] = st.Background;
                    scl[i][c] = Math.Max(1e-9, st.BackgroundSigma);
                }
                double noise = ImageStatistics.MrsNoise(img.Luminance(), img.Width, img.Height);
                switch (s.Weighting)
                {
                    case WeightingMode.Noise: weights[i] = 1.0 / Math.Max(1e-12, noise * noise); break;
                    case WeightingMode.Score: weights[i] = f.Score; break;
                    case WeightingMode.Snr: weights[i] = Math.Max(1e-6, f.Snr * f.Snr); break;
                    default: weights[i] = 1; break;
                }
                var t = canvas.Compose(new Transform2D { M = f.TransformMatrix });
                if (drizzle != null)
                {
                    Normalize(img, loc[i], scl[i], loc[0], scl[0], s.Normalization, i == 0);
                    drizzle.Add(img, t, weights[i]);
                }
                else
                {
                    var warped = Geometry.Warp(img, t, ow, oh, s.Interp, false);
                    Normalize(warped, loc[i], scl[i], loc[0], scl[0], s.Normalization, i == 0);
                    warpedFiles[i] = System.IO.Path.Combine(temp, "w" + i + ".raw");
                    WriteRaw(warpedFiles[i], warped);
                    if (entropyFiles != null)
                    {
                        entropyFiles[i] = System.IO.Path.Combine(temp, "e" + i + ".raw");
                        WriteRaw(entropyFiles[i], EntropyMap(warped));
                    }
                    if (cometFiles != null)
                    {
                        var tc = canvas.Compose(cometT[f]);
                        var cw = Geometry.Warp(img, tc, ow, oh, s.Interp, false);
                        Normalize(cw, loc[i], scl[i], loc[0], scl[0], s.Normalization, i == 0);
                        cometFiles[i] = System.IO.Path.Combine(temp, "c" + i + ".raw");
                        WriteRaw(cometFiles[i], cw);
                    }
                }
                prepared++;
                progress?.Report(new StackProgress("Calibrating & aligning", (double)prepared / use.Count, f.FileName));
            }
            // normalise weights
            double wmax = weights.Max();
            for (int i = 0; i < weights.Length; i++) weights[i] /= wmax > 0 ? wmax : 1;

            var result = new StackResult { Reference = reference, FramesStacked = use.Count, TotalExposure = use.Sum(u => u.Exposure) };
            if (drizzle != null)
            {
                result.Image = drizzle.Result();
            }
            else
            {
                AstroImage rej;
                result.Image = CombineFiles(warpedFiles, entropyFiles, ow, oh, ch, s.LightCombine, weights, progress, ct, "Stacking", out rej);
                result.Rejection = rej;
                if (cometFiles != null)
                {
                    AstroImage rej2;
                    var cometStack = CombineFiles(cometFiles, null, ow, oh, ch, s.LightCombine, weights, progress, ct, "Stacking comet", out rej2);
                    if (s.Comet == CometMode.CometAligned) result.Image = cometStack;
                    else
                    {
                        // stars + comet: star-aligned stack with comet rejected (strong rejection) + comet-aligned stack with stars removed
                        result.CometImage = cometStack;
                        result.Image = MergeStarsAndComet(result.Image, cometStack);
                    }
                }
            }
            if (!s.KeepTempFiles) try { Directory.Delete(temp, true); } catch { }
            if (s.PerChannelBackgroundCalibration && result.Image.Channels == 3) BackgroundCalibrate(result.Image);
            var h = result.Image.Header = refImg.Header.Clone();
            h.Set("NCOMBINE", use.Count, "Number of stacked frames");
            h.Set("EXPTIME", result.TotalExposure, "Total integration [s]");
            h.Set("STACKMTH", s.LightCombine.Method.ToString());
            h.Remove("BAYERPAT");
            if (scale > 1) h.Set("DRIZZLE", scale);
            h.AddHistory("Stacked by Sidralune");
            result.Duration = DateTime.Now - t0;
            result.Report = string.Format("{0} frames, total exposure {1:0.0} min, {2:0.0} s elapsed, method {3}{4}",
                use.Count, result.TotalExposure / 60, result.Duration.TotalSeconds, s.LightCombine.Method, scale > 1 ? ", drizzle ×" + scale : "");
            Msg(result.Report);
            return result;
        }

        private static void Normalize(AstroImage img, double[] loc, double[] scl, double[] refLoc, double[] refScl, NormalizationMode mode, bool isRef)
        {
            if (mode == NormalizationMode.None || isRef) return;
            for (int c = 0; c < img.Channels; c++)
            {
                var d = img.Data[c];
                double k = refScl[c] / scl[c];
                double l0 = loc[c], lr = refLoc[c];
                float off = (float)(lr - l0), fk = (float)k, fl0 = (float)l0, flr = (float)lr;
                float mul = l0 > 1e-9 ? (float)(lr / l0) : 1f;
                for (int i = 0; i < d.Length; i++)
                {
                    float v = d[i];
                    if (float.IsNaN(v)) continue;
                    switch (mode)
                    {
                        case NormalizationMode.Additive: d[i] = v + off; break;
                        case NormalizationMode.AdditiveScaling: d[i] = (v - fl0) * fk + flr; break;
                        case NormalizationMode.Multiplicative: d[i] = v * mul; break;
                    }
                }
            }
        }

        private static void WriteRaw(string path, AstroImage img)
        {
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            {
                var buf = new byte[img.Width * 4 * 64];
                foreach (var plane in img.Data)
                {
                    for (int i = 0; i < plane.Length; i += buf.Length / 4)
                    {
                        int n = Math.Min(buf.Length / 4, plane.Length - i);
                        Buffer.BlockCopy(plane, i * 4, buf, 0, n * 4);
                        fs.Write(buf, 0, n * 4);
                    }
                }
            }
        }

        /// <summary>Strip-wise combination of warped temporary frames.</summary>
        private static AstroImage CombineFiles(string[] files, string[] entropy, int w, int h, int ch, CombineOptions opt, double[] weights,
            IProgress<StackProgress> progress, CancellationToken ct, string stage, out AstroImage rejectionMap)
        {
            var outImg = new AstroImage(w, h, ch);
            var rej = new AstroImage(w, h, 1);
            int n = files.Length;
            int band = Math.Max(8, Math.Min(256, (int)(256L * 1024 * 1024 / Math.Max(1L, (long)w * 4 * n * (entropy != null ? 2 : 1)))));
            var streams = files.Select(f => new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16)).ToArray();
            var estreams = entropy != null ? entropy.Select(f => new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16)).ToArray() : null;
            try
            {
                long planeBytes = (long)w * h * 4;
                for (int c = 0; c < ch; c++)
                {
                    for (int y0 = 0; y0 < h; y0 += band)
                    {
                        ct.ThrowIfCancellationRequested();
                        int rows = Math.Min(band, h - y0);
                        var bands = new float[n][];
                        var ebands = entropy != null ? new float[n][] : null;
                        var bytes = new byte[(long)rows * w * 4];
                        for (int k = 0; k < n; k++)
                        {
                            bands[k] = ReadBand(streams[k], c * planeBytes + (long)y0 * w * 4, bytes, rows * w);
                            if (ebands != null) ebands[k] = ReadBand(estreams[k], (long)y0 * w * 4, bytes, rows * w);
                        }
                        var dst = outImg.Data[c];
                        var rdst = rej.Data[0];
                        Parallel.For(0, rows, () => Tuple.Create(new float[n], new float[n], new PixelCombiner(n, opt, weights)), (r, st, tl) =>
                        {
                            var vals = tl.Item1; var pw = tl.Item2; var comb = tl.Item3;
                            for (int x = 0; x < w; x++)
                            {
                                int i = r * w + x;
                                for (int k = 0; k < n; k++) { vals[k] = bands[k][i]; if (ebands != null) pw[k] = ebands[k][i]; }
                                long before = comb.Rejected, tot = comb.Total;
                                float v = comb.Combine(vals, n, ebands != null ? pw : null);
                                dst[(y0 + r) * w + x] = float.IsNaN(v) ? 0 : v;
                                long used = comb.Total - tot;
                                if (c == 0) rdst[(y0 + r) * w + x] = used > 0 ? (float)(comb.Rejected - before) / used : 0;
                            }
                            return tl;
                        }, tl => { });
                        progress?.Report(new StackProgress(stage, ((double)c + (double)(y0 + rows) / h) / ch));
                    }
                }
            }
            finally
            {
                foreach (var s in streams) s.Dispose();
                if (estreams != null) foreach (var s in estreams) s.Dispose();
            }
            rejectionMap = rej;
            return outImg;
        }

        private static float[] ReadBand(FileStream fs, long offset, byte[] buf, int count)
        {
            fs.Position = offset;
            int need = count * 4, got = 0;
            while (got < need) { int r = fs.Read(buf, got, need - got); if (r <= 0) break; got += r; }
            var f = new float[count];
            Buffer.BlockCopy(buf, 0, f, 0, got);
            return f;
        }

        /// <summary>
        /// Local entropy weight map (DeepSkyStacker's Entropy Weighted Average): Shannon entropy of the luminance
        /// histogram in 15×15 windows, giving more weight to frames with more information (e.g. unsaturated cores).
        /// </summary>
        public static AstroImage EntropyMap(AstroImage img)
        {
            int w = img.Width, h = img.Height;
            var lum = img.Luminance();
            var ent = new float[lum.Length];
            const int win = 7, bins = 32;
            int cw = Math.Max(1, w / 8), chh = Math.Max(1, h / 8);
            // computed on a coarse grid then interpolated (exact per-pixel entropy is prohibitively slow)
            int gx = (w + 7) / 8, gy = (h + 7) / 8;
            var grid = new float[gx * gy];
            Parallel.For(0, gy, j =>
            {
                var hist = new int[bins];
                for (int i = 0; i < gx; i++)
                {
                    Array.Clear(hist, 0, bins);
                    int cx = i * 8 + 4, cy = j * 8 + 4, n = 0;
                    for (int y = cy - win; y <= cy + win; y++)
                        for (int x = cx - win; x <= cx + win; x++)
                        {
                            if (x < 0 || y < 0 || x >= w || y >= h) continue;
                            float v = lum[y * w + x];
                            if (float.IsNaN(v)) continue;
                            int b = (int)(Math.Max(0, Math.Min(0.9999, v)) * bins);
                            hist[b]++; n++;
                        }
                    double e = 0;
                    for (int b = 0; b < bins; b++) if (hist[b] > 0) { double p = (double)hist[b] / n; e -= p * Math.Log(p); }
                    grid[j * gx + i] = (float)Math.Max(1e-3, e);
                }
            });
            var map = BackgroundMesh.Interpolate(grid, gx, gy, w, h);
            return new AstroImage(w, h, new[] { map });
        }

        /// <summary>Comet-aligned transforms: star transform followed by a shift that pins the comet to its reference position.</summary>
        private Dictionary<StackFrame, Transform2D> CometTransforms(List<StackFrame> frames, StackFrame reference)
        {
            var dict = new Dictionary<StackFrame, Transform2D>();
            // interpolate comet position (in reference coordinates) linearly in time from frames that have it marked
            var marked = frames.Where(f => f.CometXY != null && f.DateObs.HasValue).ToList();
            Func<StackFrame, double[]> refPos = f =>
            {
                var t = new Transform2D { M = f.TransformMatrix };
                double u, v;
                t.Apply(f.CometXY[0], f.CometXY[1], out u, out v);
                return new[] { u, v };
            };
            double[] refComet = reference.CometXY != null ? reference.CometXY : (marked.Count > 0 ? refPos(marked[0]) : new[] { 0.0, 0.0 });
            foreach (var f in frames)
            {
                var star = new Transform2D { M = f.TransformMatrix };
                double[] pos = null;
                if (f.CometXY != null) pos = refPos(f);
                else if (marked.Count >= 2 && f.DateObs.HasValue)
                {
                    var a = marked.First(); var b = marked.Last();
                    double ta = a.DateObs.Value.Ticks, tb = b.DateObs.Value.Ticks, tf = f.DateObs.Value.Ticks;
                    var pa = refPos(a); var pb = refPos(b);
                    double k = tb != ta ? (tf - ta) / (tb - ta) : 0;
                    pos = new[] { pa[0] + k * (pb[0] - pa[0]), pa[1] + k * (pb[1] - pa[1]) };
                }
                if (pos == null) { dict[f] = star; continue; }
                dict[f] = Transform2D.Translation(refComet[0] - pos[0], refComet[1] - pos[1]).Compose(star);
            }
            return dict;
        }

        /// <summary>Combine a star-aligned stack and a comet-aligned stack: per pixel keep the brighter of the two above background.</summary>
        private static AstroImage MergeStarsAndComet(AstroImage stars, AstroImage comet)
        {
            var o = stars.CloneEmpty();
            for (int c = 0; c < stars.Channels; c++)
            {
                var a = stars.Data[c]; var b = comet.Data[c]; var d = o.Data[c];
                var sa = ImageStatistics.Compute(a, stars.Width, 0); var sb = ImageStatistics.Compute(b, comet.Width, 0);
                float ba = (float)sa.Background, bb = (float)sb.Background;
                for (int i = 0; i < d.Length; i++) d[i] = ba + Math.Max(a[i] - ba, b[i] - bb);
            }
            return o;
        }

        /// <summary>Neutralise the background: equalise per-channel background levels to the mean background.</summary>
        public static void BackgroundCalibrate(AstroImage img)
        {
            var bg = new double[img.Channels];
            for (int c = 0; c < img.Channels; c++) bg[c] = ImageStatistics.Compute(img.Data[c], img.Width, 0).Background;
            double target = bg.Average();
            for (int c = 0; c < img.Channels; c++)
            {
                float off = (float)(target - bg[c]);
                var d = img.Data[c];
                for (int i = 0; i < d.Length; i++) d[i] += off;
            }
        }

        // ---------------------------------------------------------------- project files

        public void SaveProject(string path)
        {
            var obj = new Dictionary<string, object>
            {
                { "Settings", Settings },
                { "Frames", Frames.Select(f => new Dictionary<string, object> { { "Path", f.Path }, { "Kind", f.Kind.ToString() }, { "Enabled", f.Enabled }, { "Group", f.Group }, { "CometXY", f.CometXY } }).ToList() }
            };
            File.WriteAllText(path, Core.Util.Json.Serialize(obj, true));
        }

        public void LoadProject(string path)
        {
            var d = Core.Util.Json.ParseObject(File.ReadAllText(path));
            object so;
            if (d.TryGetValue("Settings", out so)) Settings = (StackingSettings)Core.Util.Json.PopulateType(typeof(StackingSettings), so) ?? new StackingSettings();
            Frames.Clear();
            object fo;
            if (d.TryGetValue("Frames", out fo))
                foreach (var item in (List<object>)fo)
                {
                    var fd = (Dictionary<string, object>)item;
                    var f = new StackFrame { Path = (string)fd["Path"], Kind = (FrameKind)Enum.Parse(typeof(FrameKind), (string)fd["Kind"]), Enabled = (bool)fd["Enabled"] };
                    object g; if (fd.TryGetValue("Group", out g) && g != null) f.Group = (string)g;
                    object cx; if (fd.TryGetValue("CometXY", out cx) && cx is List<object>) f.CometXY = ((List<object>)cx).Select(Convert.ToDouble).ToArray();
                    Frames.Add(f);
                }
        }
    }

    /// <summary>
    /// Variable-pixel linear reconstruction ("drizzle", Fruchter &amp; Hook 2002) with a square drop kernel of size
    /// pixfrac, approximating the transformed drop by an axis-aligned square (exact for pure translation).
    /// </summary>
    public class DrizzleAccumulator
    {
        private readonly int _w, _h, _ch;
        private readonly double _pixfrac;
        private readonly double[][] _sum;
        private readonly double[] _wt;

        public DrizzleAccumulator(int w, int h, int ch, double pixfrac)
        {
            _w = w; _h = h; _ch = ch; _pixfrac = pixfrac;
            _sum = new double[ch][];
            for (int c = 0; c < ch; c++) _sum[c] = new double[w * h];
            _wt = new double[w * h];
        }

        public void Add(AstroImage img, Transform2D t, double weight)
        {
            double scale = t.Scale;
            double half = 0.5 * _pixfrac * scale;
            int iw = img.Width, ih = img.Height;
            for (int y = 0; y < ih; y++)
                for (int x = 0; x < iw; x++)
                {
                    double u, v;
                    t.Apply(x, y, out u, out v);
                    double x0 = u - half, x1 = u + half, y0 = v - half, y1 = v + half;
                    int ix0 = (int)Math.Floor(x0 + 0.5), ix1 = (int)Math.Floor(x1 + 0.5), iy0 = (int)Math.Floor(y0 + 0.5), iy1 = (int)Math.Floor(y1 + 0.5);
                    for (int oy = Math.Max(0, iy0); oy <= Math.Min(_h - 1, iy1); oy++)
                    {
                        double oy0 = oy - 0.5, oy1 = oy + 0.5;
                        double ovy = Math.Min(y1, oy1) - Math.Max(y0, oy0);
                        if (ovy <= 0) continue;
                        for (int ox = Math.Max(0, ix0); ox <= Math.Min(_w - 1, ix1); ox++)
                        {
                            double ovx = Math.Min(x1, ox + 0.5) - Math.Max(x0, ox - 0.5);
                            if (ovx <= 0) continue;
                            double a = ovx * ovy * weight;
                            int o = oy * _w + ox;
                            bool any = false;
                            for (int c = 0; c < _ch; c++)
                            {
                                float val = img.Data[Math.Min(c, img.Channels - 1)][y * iw + x];
                                if (float.IsNaN(val)) continue;
                                _sum[c][o] += val * a; any = true;
                            }
                            if (any) _wt[o] += a;
                        }
                    }
                }
        }

        public AstroImage Result()
        {
            var img = new AstroImage(_w, _h, _ch);
            img.Weight = new float[_w * _h];
            for (int i = 0; i < _wt.Length; i++)
            {
                img.Weight[i] = (float)_wt[i];
                for (int c = 0; c < _ch; c++) img.Data[c][i] = _wt[i] > 0 ? (float)(_sum[c][i] / _wt[i]) : 0;
            }
            return img;
        }
    }
}
