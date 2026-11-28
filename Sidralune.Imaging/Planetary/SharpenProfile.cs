using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Sidralune.Core.Ephemeris;

namespace Sidralune.Imaging.Planetary
{
    public enum SharpenStepKind { Deconvolution, AlignChannels }

    /// <summary>A step that changes the image under the wavelets: a deconvolution or a colour channel alignment.</summary>
    public class SharpenStep
    {
        public SharpenStepKind Kind;
        public double Fwhm = 2.0;
        public int Iterations = 15;
        public SharpenStep Clone() => (SharpenStep)MemberwiseClone();
    }

    /// <summary>
    /// The Sharpen &amp; deconvolve settings for one object (Jupiter, the Moon…), or the default ones (Object ""):
    /// the steps applied to the image as opened, in order, then the wavelet layers and their denoise on top.
    /// </summary>
    public class SharpenProfile
    {
        /// <summary>The object, by its English name (Body.ToString()), or "" for images of an unknown object.</summary>
        public string Object = "";
        /// <summary>The six wavelet layers' amounts (1 = slider at 100) and the denoise of the finest layers.</summary>
        public double[] Layers = { 1.2, 0.6, 0.2, 0, 0, 0 };
        public double Denoise = 0.6;
        /// <summary>The deconvolution settings shown, for the next deconvolution.</summary>
        public double DeconvFwhm = 2.0;
        public int DeconvIterations = 15;
        public List<SharpenStep> Steps = new List<SharpenStep>();

        public SharpenProfile Clone()
        {
            var c = (SharpenProfile)MemberwiseClone();
            c.Layers = (double[])Layers.Clone();
            c.Steps = Steps.Select(s => s.Clone()).ToList();
            return c;
        }

        public static void Deconvolve(AstroImage img, double fwhm, int iterations, CancellationToken ct = default(CancellationToken)) =>
            Deconvolution.Run(img, Psf.Gaussian(fwhm), new DeconvolutionOptions { Method = DeconvolutionMethod.RichardsonLucyTV, Iterations = iterations, TvLambda = 0.001, DeringingAmount = 0.3 }, null, ct);

        /// <summary>Applies a step to the image in place; aligning the channels of a mono image leaves it as it is.</summary>
        public static void ApplyStep(AstroImage img, SharpenStep s, CancellationToken ct = default(CancellationToken))
        {
            if (s.Kind == SharpenStepKind.Deconvolution) Deconvolve(img, s.Fwhm, s.Iterations, ct);
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
        public static AstroImage Wavelets(AstroImage src, double[] layers, double denoise, CancellationToken ct = default(CancellationToken))
        {
            var o = src.Clone();
            for (int c = 0; c < o.Channels; c++)
            {
                ct.ThrowIfCancellationRequested();
                o.Data[c] = Imaging.Wavelets.Sharpen(src.Data[c], src.Width, src.Height, layers, new[] { denoise, denoise * 0.5, 0, 0, 0, 0 });
            }
            o.Clip(0, 1);
            return o;
        }

        /// <summary>The image as this profile makes it: the steps, then the wavelets.</summary>
        public AstroImage Apply(AstroImage original, CancellationToken ct = default(CancellationToken)) => Wavelets(ApplySteps(original, ct), Layers, Denoise, ct);
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
