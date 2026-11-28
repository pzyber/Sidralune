using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Sidralune.Core.Ephemeris;
using Sidralune.Imaging.Analysis;

namespace Sidralune.Imaging.Planetary
{
    public enum SharpenStepKind { Deconvolution, AlignChannels }

    /// <summary>The noise filters run after sharpening, alone or one after the other.</summary>
    public enum SharpenNoiseFilter { None, Bilateral, Sigma, BilateralAndSigma }

    /// <summary>A step that changes the image under the live settings: a deconvolution or a colour channel alignment.</summary>
    public class SharpenStep
    {
        public SharpenStepKind Kind;
        /// <summary>Richardson-Lucy with total-variation regularisation, or Wiener.</summary>
        public DeconvolutionMethod Method = DeconvolutionMethod.RichardsonLucyTV;
        /// <summary>The Gaussian PSF's FWHM in pixels, when no PSF file is given.</summary>
        public double Fwhm = 2.0;
        public int Iterations = 15;
        public double WienerK = 0.01;
        /// <summary>An image of a star (or a planet's moon) whose light is the PSF, or "" for the Gaussian.</summary>
        public string PsfFile = "";
        public SharpenStep Clone() => (SharpenStep)MemberwiseClone();
    }

    /// <summary>
    /// The Sharpen &amp; deconvolve settings for one object (Jupiter, the Moon…), or the default ones (Object ""):
    /// the steps applied to the image as opened, in order, then the live chain on top: wavelets, unsharp mask and a
    /// noise filter (on each channel, or on the luminance only), the limb and halo protection, then colour and tone.
    /// </summary>
    public class SharpenProfile
    {
        /// <summary>The object, by its English name (Body.ToString()), or "" for images of an unknown object.</summary>
        public string Object = "";
        /// <summary>The six wavelet layers' amounts (1 = slider at 100) and the denoise of the finest layers.</summary>
        public double[] Layers = { 1.2, 0.6, 0.2, 0, 0, 0 };
        public double Denoise = 0.6;
        /// <summary>The deconvolution settings shown, for the next deconvolution.</summary>
        public DeconvolutionMethod DeconvMethod = DeconvolutionMethod.RichardsonLucyTV;
        public double DeconvFwhm = 2.0;
        public int DeconvIterations = 15;
        public double WienerK = 0.01;
        public string PsfFile = "";
        public List<SharpenStep> Steps = new List<SharpenStep>();

        /// <summary>Sharpen and filter the brightness only, leaving the colour as it is, so no colour noise is made.</summary>
        public bool LuminanceOnly;
        /// <summary>Unsharp mask after the wavelets: the blur's sigma in pixels and the amount (0 = off).</summary>
        public double UnsharpRadius = 1.5, UnsharpAmount;
        /// <summary>A noise filter after sharpening, its strength in units of the measured noise.</summary>
        public SharpenNoiseFilter NoiseFilter = SharpenNoiseFilter.None;
        public double NoiseStrength = 1.0;
        /// <summary>0..1: how much of the sharpening is taken off in a band along the planet's limb.</summary>
        public double ProtectLimb;
        /// <summary>0..1: how far pixels are pulled back within the brightest and darkest of their neighbours as opened.</summary>
        public double SuppressHalos;
        /// <summary>Scale red and blue so the planet (not the sky) is grey on average, as a white balance.</summary>
        public bool NormalizeColour;
        public double Saturation = 1.0, Gamma = 1.0;
        /// <summary>-1..1: an S-curve that keeps black and white (positive adds contrast).</summary>
        public double Contrast;
        /// <summary>The tone curve's points, x0, y0, x1, y1…; null for none.</summary>
        public double[] Curve;

        public SharpenProfile Clone()
        {
            var c = (SharpenProfile)MemberwiseClone();
            c.Layers = (double[])Layers.Clone();
            c.Steps = Steps.Select(s => s.Clone()).ToList();
            c.Curve = Curve != null ? (double[])Curve.Clone() : null;
            return c;
        }

        public static void Deconvolve(AstroImage img, double fwhm, int iterations, CancellationToken ct = default(CancellationToken)) =>
            ApplyStep(img, new SharpenStep { Kind = SharpenStepKind.Deconvolution, Fwhm = fwhm, Iterations = iterations }, ct);

        /// <summary>The PSF of a deconvolution step: the recorded one from its file, or the Gaussian.</summary>
        public static Psf StepPsf(SharpenStep s)
        {
            if (string.IsNullOrEmpty(s.PsfFile)) return Psf.Gaussian(s.Fwhm);
            if (!File.Exists(s.PsfFile)) throw new FileNotFoundException(Core.Util.Lang.T("The PSF image {0} is not there", s.PsfFile), s.PsfFile);
            return Psf.FromImage(IO.ImageFile.Load(s.PsfFile));
        }

        /// <summary>Applies a step to the image in place; aligning the channels of a mono image leaves it as it is.</summary>
        public static void ApplyStep(AstroImage img, SharpenStep s, CancellationToken ct = default(CancellationToken))
        {
            if (s.Kind == SharpenStepKind.Deconvolution)
            {
                var o = new DeconvolutionOptions { Method = s.Method, Iterations = s.Iterations, TvLambda = 0.001, DeringingAmount = 0.3, WienerK = s.WienerK };
                Deconvolution.Run(img, StepPsf(s), o, null, ct);
            }
            else if (img.Channels == 3) ColorOps.AlignChannels(img, ct);
        }

        /// <summary>A copy of the image with the steps applied.</summary>
        public AstroImage ApplySteps(AstroImage original, CancellationToken ct = default(CancellationToken))
        {
            var img = original.Clone();
            foreach (var s in Steps) { ct.ThrowIfCancellationRequested(); ApplyStep(img, s, ct); }
            return img;
        }

        /// <summary>The image sharpened by the wavelet layers, with the finest two layers denoised first.</summary>
        public static AstroImage Wavelets(AstroImage src, double[] layers, double denoise, CancellationToken ct = default(CancellationToken)) =>
            new SharpenProfile { Layers = layers, Denoise = denoise }.Finish(src, ct);

        /// <summary>The image as this profile makes it: the steps, then the live chain.</summary>
        public AstroImage Apply(AstroImage original, CancellationToken ct = default(CancellationToken)) => Finish(ApplySteps(original, ct), ct);

        /// <summary>The live chain on an image with its steps applied; with every setting at its default, the wavelets alone.</summary>
        public AstroImage Finish(AstroImage src, CancellationToken ct = default(CancellationToken))
        {
            int w = src.Width, h = src.Height;
            var o = src.Clone();
            var layers = Layers ?? new double[6];
            Func<float[], float[]> sharpen = d =>
            {
                ct.ThrowIfCancellationRequested();
                var r = Imaging.Wavelets.Sharpen(d, w, h, layers, new[] { Denoise, Denoise * 0.5, 0, 0, 0, 0 });
                if (UnsharpAmount > 0) r = Filters.UnsharpMask(r, w, h, Math.Max(0.3, UnsharpRadius), UnsharpAmount);
                if (NoiseFilter != SharpenNoiseFilter.None && NoiseStrength > 0)
                {
                    double sigma = NoiseOnObject(r, w, h) * NoiseStrength;
                    if (sigma > 0 && (NoiseFilter == SharpenNoiseFilter.Bilateral || NoiseFilter == SharpenNoiseFilter.BilateralAndSigma)) r = Enhance.Bilateral(r, w, h, 2, 1.5, 2 * sigma);
                    if (sigma > 0 && (NoiseFilter == SharpenNoiseFilter.Sigma || NoiseFilter == SharpenNoiseFilter.BilateralAndSigma)) r = Filters.SigmaFilter(r, w, h, 2, 2 * sigma);
                }
                return r;
            };
            if (LuminanceOnly && src.Channels == 3)
            {
                // the change made to the brightness is added to each channel alike
                var lum = src.Luminance();
                var sharp = sharpen(lum);
                for (int c = 0; c < 3; c++)
                {
                    var a = src.Data[c]; var b = o.Data[c];
                    for (int i = 0; i < b.Length; i++) b[i] = a[i] + (sharp[i] - lum[i]);
                }
            }
            else for (int c = 0; c < o.Channels; c++) o.Data[c] = sharpen(src.Data[c]);
            ct.ThrowIfCancellationRequested();
            if (SuppressHalos > 0) LimitOvershoot(src, o, Math.Min(1, SuppressHalos));
            if (ProtectLimb > 0) ProtectTheLimb(src, o, Math.Min(1, ProtectLimb));
            if (NormalizeColour && o.Channels == 3) NormalizeTheColour(o);
            if (Saturation != 1 && o.Channels == 3) ColorOps.Saturation(o, Saturation);
            o.Clip(0, 1);
            var curve = new MonotoneCurve(Curve);
            if (Gamma != 1 || Contrast != 0 || !curve.IsIdentity)
            {
                // gamma, contrast and the curve as one table
                const int n = 4096;
                var lut = new float[n + 1];
                double g = Math.Max(0.05, Gamma), k = Math.Max(-1, Math.Min(1, Contrast));
                for (int i = 0; i <= n; i++)
                {
                    double v = Math.Pow((double)i / n, 1 / g);
                    v += k * (v * v * (3 - 2 * v) - v);
                    lut[i] = (float)curve[v];
                }
                for (int c = 0; c < o.Channels; c++)
                {
                    var d = o.Data[c];
                    for (int i = 0; i < d.Length; i++)
                    {
                        double x = d[i] * n;
                        int j = (int)x;
                        if (j >= n) { d[i] = lut[n]; continue; }
                        double f = x - j;
                        d[i] = (float)(lut[j] + (lut[j + 1] - lut[j]) * f);
                    }
                }
            }
            return o;
        }

        /// <summary>
        /// The grain on the planet, where the filters matter: what a 2-pixel blur takes off where the image is brighter
        /// than a quarter of the way from the sky to its highlights. The sky, quieter and usually most of the frame,
        /// would make the grain look smaller than it is on the disc; and after wavelet sharpening the finest scale alone
        /// is quiet (Denoise thresholds it) while the next one carries the grain. The whole frame when little of it is
        /// bright.
        /// </summary>
        public static double NoiseOnObject(float[] d, int w, int h)
        {
            if (w < 8 || h < 8) return 0;
            var smooth = Filters.GaussianBlur(d, w, h, 2.0);
            var sorted = (float[])smooth.Clone();
            Array.Sort(sorted);
            double sky = sorted[sorted.Length / 2], top = sorted[(int)(0.995 * (sorted.Length - 1))], thr = sky + 0.25 * (top - sky);
            var res = new List<double>();
            for (int i = 0; i < d.Length; i++) if (smooth[i] > thr) res.Add(Math.Abs(d[i] - smooth[i]));
            if (res.Count < 200) return ImageStatistics.MrsNoise(d, w, h);
            // the median absolute residual as a standard deviation
            return Core.Mathematics.Stats.Median(res) * 1.4826;
        }

        // pixels pulled back within the brightest and darkest of their neighbours in the image as opened: bright rims and
        // dark rings, which go past both, are taken off, and so is the clipping of highlights sharpened past white
        private static void LimitOvershoot(AstroImage src, AstroImage o, double amount)
        {
            int w = src.Width, h = src.Height;
            for (int c = 0; c < o.Channels; c++)
            {
                var lo = Filters.Morphology(src.Data[c], w, h, 2, false);
                var hi = Filters.Morphology(src.Data[c], w, h, 2, true);
                var d = o.Data[c];
                for (int i = 0; i < d.Length; i++)
                {
                    if (d[i] > hi[i]) d[i] -= (float)((d[i] - hi[i]) * amount);
                    else if (d[i] < lo[i]) d[i] += (float)((lo[i] - d[i]) * amount);
                }
            }
        }

        // the sharpening taken off in a band along the limb, where it leaves a bright rim inside and a dark ring outside
        private static void ProtectTheLimb(AstroImage src, AstroImage o, double amount)
        {
            int w = src.Width, h = src.Height;
            var lum = src.Channels == 1 ? src.Data[0] : src.Luminance();
            var ob = ObjectDetection.Detect(lum, w, h);
            if (ob == null || !ob.Found) return;
            var circ = ObjectDetection.FitLimb(lum, w, h, ob) ?? new[] { ob.Cx, ob.Cy, ob.Radius };
            double cx = circ[0], cy = circ[1], r = circ[2], band = Math.Max(2.0, 0.03 * r);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    double dist = Math.Abs(Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)) - r);
                    if (dist >= 2 * band) continue;
                    double k = amount * (dist <= band ? 1 : 0.5 * (1 + Math.Cos(Math.PI * (dist - band) / band)));
                    int i = y * w + x;
                    for (int c = 0; c < o.Channels; c++) o.Data[c][i] += (float)((src.Data[c][i] - o.Data[c][i]) * k);
                }
        }

        // red and blue scaled so the planet's mean is grey; the sky, which may be coloured at dawn, is left out
        private static void NormalizeTheColour(AstroImage o)
        {
            var lum = o.Luminance();
            var sorted = (float[])lum.Clone();
            Array.Sort(sorted);
            double top = sorted[(int)(0.995 * (sorted.Length - 1))], sky = sorted[sorted.Length / 2], thr = sky + 0.25 * (top - sky);
            var mean = new double[3];
            int n = 0;
            for (int i = 0; i < lum.Length; i++)
                if (lum[i] > thr) { for (int c = 0; c < 3; c++) mean[c] += o.Data[c][i]; n++; }
            if (n == 0 || mean[0] <= 0 || mean[2] <= 0) return;
            foreach (int c in new[] { 0, 2 })
            {
                float k = (float)(mean[1] / mean[c]);
                var d = o.Data[c];
                for (int i = 0; i < d.Length; i++) d[i] *= k;
            }
        }
    }

    /// <summary>Which Sun, Moon or planet an image shows, from its file and folder names.</summary>
    public static class PlanetaryObject
    {
        /// <summary>The objects the planetary tools have profiles for.</summary>
        public static readonly Body[] Objects = { Body.Sun, Body.Moon, Body.Mercury, Body.Venus, Body.Mars, Body.Jupiter, Body.Saturn, Body.Uranus, Body.Neptune };

        // short names in capture programs' file names (FireCapture "Jup", "Sat"), as whole words; not "Mar", which is
        // also March
        private static readonly Dictionary<string, Body> Short = new Dictionary<string, Body>
        {
            { "mer", Body.Mercury }, { "ven", Body.Venus }, { "jup", Body.Jupiter }, { "sat", Body.Saturn }, { "ura", Body.Uranus }, { "nep", Body.Neptune }
        };

        // the one-letter planet code after a WinJUPOS time and before the number, as the Pre-process tab and Save stacks
        // write it: 2026-11-28-2130_4-J_0000. Only there: FireCapture puts the observer's initial after the time
        // (2026-11-28-2130_4-U-RGB-Jup), which would read as Uranus
        private static readonly Regex WinJupos = new Regex(@"\d{4}-\d{2}-\d{2}-\d{4}_\d-([A-Za-z])(?=_\d|$)");
        private static readonly Dictionary<char, Body> Codes = new Dictionary<char, Body>
        {
            { 'J', Body.Jupiter }, { 'S', Body.Saturn }, { 'M', Body.Mars }, { 'V', Body.Venus }, { 'U', Body.Uranus }, { 'N', Body.Neptune }
        };

        /// <summary>
        /// The object named in a file name, or else in the names of the folders above it (SharpCap files the captures
        /// under a folder named after the target, AutoStakkert puts its stacks in a folder beside the video): an English
        /// name ("Jupiter", also at the start of a word such as "JupiterRGB"), a short name as a whole word ("Jup"), or
        /// Sidralune's one-letter code after a WinJUPOS time. Null when none is found.
        /// </summary>
        public static Body? FromPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            string name = Path.GetFileNameWithoutExtension(path);
            var found = FromName(name);
            var m = WinJupos.Match(name);
            Body b;
            if (found == null && m.Success && Codes.TryGetValue(char.ToUpperInvariant(m.Groups[1].Value[0]), out b)) found = b;
            string dir = Path.GetDirectoryName(path);
            for (int up = 0; found == null && up < 4 && !string.IsNullOrEmpty(dir); up++)
            {
                found = FromName(Path.GetFileName(dir));
                dir = Path.GetDirectoryName(dir);
            }
            return found;
        }

        private static Body? FromName(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            foreach (var word in Regex.Split(name.ToLowerInvariant(), @"[^\p{L}]+").Where(w => w.Length > 0))
            {
                foreach (var b in Objects) if (word.StartsWith(b.ToString().ToLowerInvariant(), StringComparison.Ordinal)) return b;
                Body s;
                if (Short.TryGetValue(word, out s)) return s;
            }
            return null;
        }

        /// <summary>The one-letter code the Pre-process tab writes after a WinJUPOS time ("J"), or "" for none.</summary>
        public static string Code(Body? b) => b.HasValue && Codes.ContainsValue(b.Value) ? Codes.First(kv => kv.Value == b.Value).Key.ToString() : "";
    }

    /// <summary>Sharpening many images with the profiles: where the results go and which profile each image gets.</summary>
    public static class SharpenBatch
    {
        public const string Suffix = "_sharpened";

        /// <summary>The result's path: beside the image, with _sharpened before the extension, as a 16-bit TIFF.</summary>
        public static string OutputPath(string input) => Path.Combine(Path.GetDirectoryName(input) ?? "", Path.GetFileNameWithoutExtension(input) + Suffix + ".tif");

        /// <summary>Whether a file is a result of sharpening, which is never sharpened again.</summary>
        public static bool IsOutput(string path) => Path.GetFileNameWithoutExtension(path ?? "").EndsWith(Suffix, StringComparison.OrdinalIgnoreCase);

        /// <summary>The saved profile of the image's object, or the settings in use when there is none or the object is not known.</summary>
        public static SharpenProfile ProfileFor(string path, IEnumerable<SharpenProfile> saved, SharpenProfile current)
        {
            var b = PlanetaryObject.FromPath(path);
            if (!b.HasValue) return current;
            string name = b.Value.ToString();
            return (saved ?? Enumerable.Empty<SharpenProfile>()).FirstOrDefault(p => p != null && p.Object == name) ?? current;
        }

        /// <summary>Sharpens one image with the profile and saves the result beside it; returns the result's path.</summary>
        public static string Run(string input, SharpenProfile profile, CancellationToken ct = default(CancellationToken))
        {
            var img = IO.ImageFile.Load(input);
            var o = profile.Apply(img, ct);
            string path = OutputPath(input);
            IO.TiffIO.Write(path, o);
            return path;
        }
    }
}
