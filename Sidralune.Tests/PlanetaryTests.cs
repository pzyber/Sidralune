using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Sidralune.Imaging;
using Sidralune.Imaging.IO;
using Sidralune.Imaging.Planetary;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Sidralune.Tests.Checks;

namespace Sidralune.Tests
{
    [TestClass]
    public class PlanetaryTests : TestBase
    {
        private static double Rmse(float[] a, float[] b, int w, int h, int border)
        {
            double s = 0; int n = 0;
            for (int y = border; y < h - border; y++) for (int x = border; x < w - border; x++) { double d = a[y * w + x] - b[y * w + x]; s += d * d; n++; }
            return Math.Sqrt(s / n);
        }

        [TestMethod]
        public void TestLuckyStacking()
        {
            int w = 160, h = 160;
            var truth = Synthetic.Planet(w, h, 80, 80, 50, 0, 0, 0, 1);
            string p = Path.Combine(Path.GetTempPath(), "SidraluneTests", "lucky.ser");
            Directory.CreateDirectory(Path.GetDirectoryName(p));
            var rng = new Random(9);
            AstroImage single = null;
            using (var wr = new SerWriter(p, w, h, 16, 1))
                for (int i = 0; i < 60; i++)
                {
                    double blur = 0.8 + rng.NextDouble() * 2.5; // seeing varies
                    var f = Synthetic.Planet(w, h, 80 + rng.NextDouble() * 6 - 3, 80 + rng.NextDouble() * 6 - 3, 50, 0, blur, 0.02, 100 + i);
                    if (i == 0) single = f;
                    wr.Write(f);
                }
            var st = new LuckyStacker();
            st.Options.StackPercents = new[] { 25.0 };
            st.Options.ApSize = 32;
            using (var src = new SerReader(p))
            {
                var r = st.Run(src);
                Info(r.Report);
                var stack = r.Stacks[0];
                // compare sharpness by quality metric on the disc
                double qs = FrameQuality.Measure(single.Data[0], w, h, QualityMetric.Gradient, 30, 30, 100, 100);
                double qk = FrameQuality.Measure(stack.Data[0], w, h, QualityMetric.Gradient, 30, 30, 100, 100);
                double noiseSingle = Sidralune.Imaging.Analysis.ImageStatistics.MrsNoise(single.Data[0], w, h);
                double noiseStack = Sidralune.Imaging.Analysis.ImageStatistics.MrsNoise(stack.Data[0], w, h);
                Info(string.Format("noise single {0:0.0000} stack {1:0.0000}; APs {2}", noiseSingle, noiseStack, r.AlignmentPoints.Count));
                CheckTrue("Lucky stack reduces noise > 2x", noiseSingle / noiseStack > 2.0);
                var obj = ObjectDetection.Detect(stack.Data[0], w, h);
                Check("Stack centred on reference X", obj.Cx, ObjectDetection.Detect(st.Options.ApSize > 0 ? r.Reference.Data[0] : stack.Data[0], w, h).Cx, 0.5);
            }
        }

        [TestMethod]
        public void TestOptionCopies()
        {
            // a run works on a copy, so the settings shown can change while it works
            var pipp = new PippOptions { CropWidth = 300, Format = PippOutputFormat.Tiff, OutputFolder = "a" };
            var run = pipp.Clone();
            pipp.CropWidth = 500; pipp.Format = PippOutputFormat.Ser; pipp.OutputFolder = "b";
            CheckTrue("Pre-process: the run keeps the settings it started with", run.CropWidth == 300 && run.Format == PippOutputFormat.Tiff && run.OutputFolder == "a");
            var lucky = new LuckyOptions { ApSize = 48, StackPercents = new double[] { 10, 25 } };
            var luckyRun = lucky.Clone();
            lucky.ApSize = 64; lucky.StackPercents = new double[] { 50 };
            CheckTrue("Lucky stacking: the run keeps the settings it started with", luckyRun.ApSize == 48 && luckyRun.StackPercents.SequenceEqual(new double[] { 10, 25 }));
        }

        [TestMethod]
        public void TestLiveStats()
        {
            // a banded disc on a dark sky, as Jupiter looks in a live view
            Func<double, double, AstroImage> disc = (top, blur) =>
            {
                var img = new AstroImage(200, 150, 1);
                for (int y = 0; y < 150; y++)
                    for (int x = 0; x < 200; x++)
                    {
                        double r = Math.Sqrt((x - 100) * (x - 100) + (y - 75) * (y - 75));
                        img.Data[0][y * 200 + x] = (float)(r < 30 ? top * (0.75 + 0.25 * Math.Sin(y / 1.5)) : 0.02);
                    }
                if (blur > 0) img.Data[0] = Filters.GaussianBlur(img.Data[0], 200, 150, blur);
                img.Clip(0, 1);
                return img;
            };
            var sharp = LiveStats.Measure(disc(0.6, 0));
            var soft = LiveStats.Measure(disc(0.6, 2));
            var softer = LiveStats.Measure(disc(0.6, 4));
            CheckTrue("The planet is found", sharp.ObjectFound && soft.ObjectFound);
            CheckTrue("Focus rises as the image gets sharper", sharp.Focus > soft.Focus && soft.Focus > softer.Focus, sharp.Focus + " > " + soft.Focus + " > " + softer.Focus);
            Check("Peak brightness", sharp.Peak, 0.6, 0.01);
            CheckTrue("Nothing clipped at 60%", sharp.Saturated == 0);
            var clipped = LiveStats.Measure(disc(1.6, 0));
            CheckTrue("An overexposed planet shows most of its pixels clipped", clipped.Peak >= 0.999 && clipped.Saturated > 0.4, clipped.Saturated.ToString("0.00"));
            var empty = new AstroImage(200, 150, 1);
            for (int i = 0; i < empty.Data[0].Length; i++) empty.Data[0][i] = 0.02f;
            CheckTrue("An empty sky has no planet and no clipping", !LiveStats.Measure(empty).ObjectFound && LiveStats.Measure(empty).Saturated == 0);
        }

        [TestMethod]
        public void TestRotationEstimator()
        {
            int w = 200, h = 200;
            var a = Synthetic.Planet(w, h, 100, 100, 70, 0, 1, 0.005, 3);
            var b = FieldRotation.RotateAbout(a, 7.5, 100, 100);
            double est = RotationEstimator.Estimate(a.Data[0], b.Data[0], w, h, 100, 100, 10, 65, 20);
            Check("Estimated rotation (deg)", est, 7.5, 0.3);
            // correcting with the negated estimate must restore the original
            var back = FieldRotation.RotateAbout(b, -est, 100, 100);
            CheckTrue("Rotation correction restores image", Rmse(a.Data[0], back.Data[0], w, h, 40) < 0.02);
        }

        [TestMethod]
        public void TestSphereDerotationRoundtrip()
        {
            int w = 200, h = 200;
            var a = Synthetic.Planet(w, h, 100, 100, 80, 0, 0.5, 0, 1);
            var g = new DiscGeometry { Cx = 100, Cy = 100, Radius = 80, Flattening = 0.935, PoleAngleDeg = 15, SubObserverLatDeg = 3 };
            var f = SphereDerotation.Derotate(a, g, 600, 9.925);
            var back = SphereDerotation.Derotate(f, g, -600, 9.925);
            double err = Rmse(a.Data[0], back.Data[0], w, h, 45);
            Info("roundtrip rmse " + err.ToString("0.0000"));
            CheckTrue("Derotation +/- roundtrip", err < 0.02);
            CheckTrue("Derotation changes image", Rmse(a.Data[0], f.Data[0], w, h, 45) > 0.003);
        }

        [TestMethod]
        public void TestDeconvolution()
        {
            int w = 128, h = 128;
            var truth = Synthetic.Planet(w, h, 64, 64, 40, 0, 0, 0, 2);
            var psf = Psf.Gaussian(3.0);
            var conv = new FftConvolver(w, h, psf);
            var blurred = conv.Convolve(truth.Data[0]);
            var rng = new Random(1);
            for (int i = 0; i < blurred.Length; i++) blurred[i] += (float)((rng.NextDouble() - 0.5) * 0.004);
            double e0 = Rmse(truth.Data[0], blurred, w, h, 10);
            foreach (var m in new[] { DeconvolutionMethod.RichardsonLucy, DeconvolutionMethod.RichardsonLucyTV, DeconvolutionMethod.Wiener })
            {
                var opt = new DeconvolutionOptions { Method = m, Iterations = 25, WienerK = 0.002 };
                var r = Deconvolution.Run(blurred, w, h, psf, opt);
                double e1 = Rmse(truth.Data[0], r, w, h, 10);
                Info(string.Format("{0}: rmse blurred {1:0.0000} -> {2:0.0000}", m, e0, e1));
                CheckTrue(m + " improves fidelity", e1 < e0 * 0.85);
            }
        }

        [TestMethod]
        public void TestPreprocessor()
        {
            int w = 200, h = 160;
            string p = Path.Combine(Path.GetTempPath(), "SidraluneTests", "pipp_in.ser");
            using (var wr = new SerWriter(p, w, h, 16, 1))
                for (int i = 0; i < 20; i++) wr.Write(Synthetic.Planet(w, h, 60 + i * 3, 70 + (i % 5), 30, 0, 1 + (i % 4) * 0.5, 0.01, i), new DateTime(2025, 3, 1, 22, 0, i, DateTimeKind.Utc));
            var o = new PippOptions { OutputFolder = Path.Combine(Path.GetTempPath(), "SidraluneTests", "pipp_out"), Format = PippOutputFormat.Ser, KeepBestPercent = 50, Order = PippOutputOrder.Quality };
            using (var src = new SerReader(p))
            {
                var r = Preprocessor.Run(src, o);
                Info(r.Summary);
                Check("PIPP frames kept", r.Written, 10, 0);
                using (var outp = new SerReader(r.OutputFiles[0]))
                {
                    var f = outp.ReadFrame(0);
                    var ob = ObjectDetection.Detect(f.Data[0], f.Width, f.Height);
                    Check("PIPP centred X", ob.Cx, f.Width / 2.0, 1.0);
                    Check("PIPP centred Y", ob.Cy, f.Height / 2.0, 1.0);
                }
            }
        }

        /// <summary>A colour disc with the red and blue channels a little off the green one, as dispersion leaves them.</summary>
        private static AstroImage ColourDisc(int w, int h, int seed)
        {
            var r = Synthetic.Planet(w, h, w / 2.0 + 1.5, h / 2.0, w / 3.0, 0, 1.5, 0.01, seed);
            var g = Synthetic.Planet(w, h, w / 2.0, h / 2.0, w / 3.0, 0, 1.5, 0.01, seed + 1);
            var b = Synthetic.Planet(w, h, w / 2.0 - 1.5, h / 2.0, w / 3.0, 0, 1.5, 0.01, seed + 2);
            return new AstroImage(w, h, new[] { r.Data[0], g.Data[0], b.Data[0] });
        }

        [TestMethod]
        public void TestSharpenProfiles()
        {
            Func<string, Core.Ephemeris.Body?> obj = PlanetaryObject.FromPath;
            // Sidralune's own names, capture programs' names and their folders
            CheckTrue("Pre-process and Save stacks code: J is Jupiter, M is Mars", obj(@"C:\p\2026-11-28-2130_4-J_0000.tif") == Core.Ephemeris.Body.Jupiter && obj(@"C:\p\2026-11-28-2130_4-M_0001_p25.tif") == Core.Ephemeris.Body.Mars);
            CheckTrue("FireCapture: the observer's initial after the time is not read as a planet", obj(@"C:\p\2026-11-28-2130_4-U-RGB-Jup_lapl5_ap41.tif") == Core.Ephemeris.Body.Jupiter && obj(@"C:\p\2026-11-28-2130_4-U-RGB_lapl5.tif") == null);
            CheckTrue("English names, also at the start of a word, and short names as whole words", obj(@"C:\p\JupiterRGB_213045.tif") == Core.Ephemeris.Body.Jupiter && obj(@"C:\p\sat_0001.png") == Core.Ephemeris.Body.Saturn && obj(@"C:\p\moon-mosaic.tif") == Core.Ephemeris.Body.Moon);
            CheckTrue("SharpCap: the target's folder above the date and time folders", obj(@"C:\caps\Saturn\2026-11-28\21_30_45\21_30_45_lapl4_ap50.tif") == Core.Ephemeris.Body.Saturn);
            CheckTrue("AutoStakkert: the video's folder above AS_P25", obj(@"D:\Mars\AS_P25\2026-11-28-2130_4-U-RGB_lapl5.tif") == Core.Ephemeris.Body.Mars);
            CheckTrue("Nothing named: not known; March is not Mars", obj(@"C:\p\planet.tif") == null && obj(@"C:\p\2026-Mar-28.tif") == null && obj(null) == null);
            CheckTrue("Codes written after the time", PlanetaryObject.Code(Core.Ephemeris.Body.Jupiter) == "J" && PlanetaryObject.Code(Core.Ephemeris.Body.Mercury) == "" && PlanetaryObject.Code(null) == "");

            // each image gets its object's profile, and the settings in use when there is none
            var jup = new SharpenProfile { Object = "Jupiter", Layers = new[] { 2.0, 0.5, 0, 0, 0, 0 }, Denoise = 0.2, Steps = { new SharpenStep { Kind = SharpenStepKind.AlignChannels } } };
            var current = new SharpenProfile();
            var saved = new[] { jup };
            CheckTrue("Profiles by object", SharpenBatch.ProfileFor(@"C:\p\Jupiter_1.tif", saved, current) == jup && SharpenBatch.ProfileFor(@"C:\p\Saturn_1.tif", saved, current) == current && SharpenBatch.ProfileFor(@"C:\p\planet.tif", saved, current) == current);

            // a profile does what the Sharpen tab does by hand: its steps in order, then the wavelets
            int w = 96, h = 96;
            var img = ColourDisc(w, h, 4);
            var p = new SharpenProfile { Layers = new[] { 1.5, 0.8, 0.2, 0, 0, 0 }, Denoise = 0.4, Steps = { new SharpenStep { Kind = SharpenStepKind.Deconvolution, Fwhm = 1.8, Iterations = 6 }, new SharpenStep { Kind = SharpenStepKind.AlignChannels } } };
            var byHand = img.Clone();
            SharpenProfile.Deconvolve(byHand, 1.8, 6);
            ColorOps.AlignChannels(byHand);
            byHand = SharpenProfile.Wavelets(byHand, p.Layers, p.Denoise);
            var applied = p.Apply(img);
            bool same = Enumerable.Range(0, 3).All(c => applied.Data[c].SequenceEqual(byHand.Data[c]));
            CheckTrue("The profile gives the same image as the steps and wavelets by hand", same);
            CheckTrue("The image it was given is left as it was", img.Data[0].SequenceEqual(ColourDisc(w, h, 4).Data[0]));
            var mono = new AstroImage(w, h, new[] { img.Data[1] });
            CheckTrue("Aligning channels leaves a mono image as it is", new SharpenProfile { Layers = new double[6], Denoise = 0, Steps = { new SharpenStep { Kind = SharpenStepKind.AlignChannels } } }.Apply(mono).Data[0].SequenceEqual(SharpenProfile.Wavelets(mono, new double[6], 0).Data[0]));

            // results beside the image, never sharpened again
            string dir = Path.Combine(Path.GetTempPath(), "SidraluneTests", "sharpen");
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.CreateDirectory(dir);
            string input = Path.Combine(dir, "2026-11-28-2130_4-J_0000.fits");
            FitsIO.Write(input, img);
            CheckTrue("The result's name", SharpenBatch.OutputPath(input) == Path.Combine(dir, "2026-11-28-2130_4-J_0000_sharpened.tif"));
            CheckTrue("Results are known as results", SharpenBatch.IsOutput(SharpenBatch.OutputPath(input)) && !SharpenBatch.IsOutput(input));
            string result = SharpenBatch.Run(input, p);
            var back = ImageFile.Load(result);
            var want = p.Apply(ImageFile.Load(input));
            double worst = Enumerable.Range(0, 3).Max(c => Enumerable.Range(0, w * h).Max(i => Math.Abs(back.Data[c][i] - want.Data[c][i])));
            CheckTrue("The saved result is the profile's image, as a 16-bit TIFF", back.Channels == 3 && worst <= 1.0 / 65535 + 1e-6, worst.ToString("E2"));

            // kept in the settings file as JSON
            var json = Core.Util.Json.Serialize(new List<SharpenProfile> { p }, true);
            var read = (List<SharpenProfile>)Core.Util.Json.PopulateType(typeof(List<SharpenProfile>), Core.Util.Json.Parse(json));
            var q = read[0];
            CheckTrue("A profile survives the settings file", q.Layers.SequenceEqual(p.Layers) && q.Denoise == p.Denoise && q.Steps.Count == 2 && q.Steps[0].Kind == SharpenStepKind.Deconvolution && q.Steps[0].Fwhm == 1.8 && q.Steps[0].Iterations == 6 && q.Steps[1].Kind == SharpenStepKind.AlignChannels, json);
        }

        [TestMethod]
        public void TestMonotoneCurve()
        {
            var straight = new MonotoneCurve(null);
            CheckTrue("No points: the straight line", straight.IsIdentity && Math.Abs(straight[0.3] - 0.3) < 1e-12 && new MonotoneCurve(new[] { 0.5, 0.5 }).IsIdentity);
            var c = new MonotoneCurve(new[] { 0, 0, 0.25, 0.4, 0.5, 0.7, 1, 1 });
            CheckTrue("Through its points", Math.Abs(c[0.25] - 0.4) < 1e-12 && Math.Abs(c[0.5] - 0.7) < 1e-12 && c[0] == 0 && c[1] == 1);
            bool rising = true;
            for (int i = 1; i <= 1000; i++) if (c[i / 1000.0] < c[(i - 1) / 1000.0] - 1e-12) rising = false;
            CheckTrue("Rising points give a rising curve", rising);
            var flat = new MonotoneCurve(new[] { 0, 0, 0.3, 0.5, 0.6, 0.5, 1, 1 });
            bool level = Enumerable.Range(0, 101).Select(i => 0.3 + 0.3 * i / 100).All(x => Math.Abs(flat[x] - 0.5) < 1e-9);
            CheckTrue("No swing past the points where two are level", level);
            var ends = new MonotoneCurve(new[] { 0.2, 0.1, 0.8, 0.9 });
            CheckTrue("Ends added level with the first and last points, values kept within 0..1", Math.Abs(ends[0] - 0.1) < 1e-12 && Math.Abs(ends[1] - 0.9) < 1e-12 && ends[-1] == ends[0] && ends[2] == ends[1]);
        }

        [TestMethod]
        public void TestSharpenChain()
        {
            int w = 128, h = 128;
            // a banded colour disc kept away from black and white, with some noise
            Func<int, AstroImage> planet = seed =>
            {
                var ch = Enumerable.Range(0, 3).Select(c =>
                {
                    var d = Synthetic.Planet(w, h, 64, 64, 40, 0, 1.8, 0.012, seed + c).Data[0];
                    for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) d[y * w + x] = (float)(0.05 + d[y * w + x] * (0.45 + 0.1 * c) * (0.8 + 0.2 * Math.Cos((y - 64) / 40.0 * 8)));
                    return d;
                }).ToArray();
                return new AstroImage(w, h, ch);
            };
            var img = planet(3);
            var wav = new SharpenProfile { Layers = new[] { 1.0, 0.5, 0.2, 0, 0, 0 }, Denoise = 0.3 };

            // every live setting at its default: the wavelets alone, as profiles saved before them
            var old = (SharpenProfile)Core.Util.Json.PopulateType(typeof(SharpenProfile), Core.Util.Json.Parse("{\"Object\":\"Jupiter\",\"Layers\":[1.0,0.5,0.2,0,0,0],\"Denoise\":0.3,\"DeconvFwhm\":2.0,\"DeconvIterations\":15,\"Steps\":[]}"));
            var byHand = img.Clone();
            for (int c = 0; c < 3; c++) byHand.Data[c] = Wavelets.Sharpen(img.Data[c], w, h, wav.Layers, new[] { 0.3, 0.15, 0, 0, 0, 0 });
            byHand.Clip(0, 1);
            var plain = old.Finish(img);
            CheckTrue("A profile without the new settings sharpens with the wavelets alone", old.NoiseFilter == SharpenNoiseFilter.None && old.Saturation == 1 && old.Curve == null
                && Enumerable.Range(0, 3).All(c => plain.Data[c].SequenceEqual(byHand.Data[c])));

            // luminance only: each channel changes by the same amount, so the colour is left as it was
            var lum = wav.Clone(); lum.LuminanceOnly = true;
            var l = lum.Finish(img);
            double spread = 0;
            for (int i = 0; i < w * h; i++)
            {
                if (l.Data.Any(d => d[i] <= 0 || d[i] >= 1)) continue;
                double dr = l.Data[0][i] - img.Data[0][i], dg = l.Data[1][i] - img.Data[1][i], db = l.Data[2][i] - img.Data[2][i];
                spread = Math.Max(spread, Math.Max(Math.Abs(dr - dg), Math.Abs(db - dg)));
            }
            CheckTrue("Luminance only: the same change in each channel", spread < 1e-5, spread.ToString("E2"));
            // on a grey image the brightness is each channel, so it is sharpened as each channel would be
            var greyImg = new AstroImage(w, h, new[] { (float[])img.Data[1].Clone(), (float[])img.Data[1].Clone(), (float[])img.Data[1].Clone() });
            var lg = lum.Finish(greyImg); var pg = wav.Finish(greyImg);
            double same = Enumerable.Range(0, w * h).Max(i => Math.Abs(lg.Data[0][i] - pg.Data[0][i]));
            CheckTrue("And it sharpens the brightness as each channel would be", same < 1e-4, same.ToString("E2"));

            // unsharp mask on top of the wavelets
            var usm = wav.Clone(); usm.UnsharpAmount = 1.5; usm.UnsharpRadius = 1.2;
            CheckTrue("The unsharp mask sharpens more", Filters.LaplacianVariance(usm.Finish(img).Data[1], w, h) > 1.3 * Filters.LaplacianVariance(plain.Data[1], w, h));

            // the noise filters bring a noisy planet closer to the same planet without noise, sharpened alike
            var noisy = new AstroImage(w, h, new[] { Synthetic.Planet(w, h, 64, 64, 40, 0, 1.8, 0.012, 4).Data[0] });
            var clean = new AstroImage(w, h, new[] { Synthetic.Planet(w, h, 64, 64, 40, 0, 1.8, 0, 4).Data[0] });
            var reference = new AstroImage(w, h, new[] { Wavelets.Sharpen(clean.Data[0], w, h, wav.Layers, new[] { 0.3, 0.15, 0, 0, 0, 0 }, 0.012) });
            Func<AstroImage, double> off = im =>
            {
                double s = 0; int n = 0;
                for (int y = 34; y < 94; y++) for (int x = 34; x < 94; x++) { double e = im.Data[0][y * w + x] - reference.Data[0][y * w + x]; s += e * e; n++; }
                return Math.Sqrt(s / n);
            };
            double e0 = off(wav.Finish(noisy));
            foreach (SharpenNoiseFilter f in new[] { SharpenNoiseFilter.Bilateral, SharpenNoiseFilter.Sigma, SharpenNoiseFilter.BilateralAndSigma })
            {
                var nf = wav.Clone(); nf.NoiseFilter = f; nf.NoiseStrength = 1.5;
                double e1 = off(nf.Finish(noisy));
                CheckTrue(f + " takes noise off", e1 < 0.85 * e0, e0.ToString("E2") + " → " + e1.ToString("E2"));
            }

            // halos: sharpened pixels held within the brightest and darkest of their neighbours as they were
            var strong = new SharpenProfile { Layers = new[] { 3.0, 2.0, 1.0, 0, 0, 0 }, Denoise = 0 };
            var halo = strong.Clone(); halo.SuppressHalos = 1;
            var hres = halo.Finish(img);
            double past = 0;
            for (int c = 0; c < 3; c++)
            {
                var lo = Filters.Morphology(img.Data[c], w, h, 2, false); var hi = Filters.Morphology(img.Data[c], w, h, 2, true);
                for (int i = 0; i < w * h; i++) past = Math.Max(past, Math.Max(hres.Data[c][i] - hi[i], lo[i] - hres.Data[c][i]));
            }
            var sres = strong.Finish(img);
            double pastBefore = 0;
            for (int c = 0; c < 3; c++)
            {
                var lo = Filters.Morphology(img.Data[c], w, h, 2, false); var hi = Filters.Morphology(img.Data[c], w, h, 2, true);
                for (int i = 0; i < w * h; i++) pastBefore = Math.Max(pastBefore, Math.Max(sres.Data[c][i] - hi[i], lo[i] - sres.Data[c][i]));
            }
            CheckTrue("Suppress halos at full strength: nothing past its neighbours", past < 1e-6 && pastBefore > 0.02, pastBefore.ToString("0.000") + " → " + past.ToString("E2"));

            // the limb: no sharpening on it, the inside as before
            var limb = strong.Clone(); limb.ProtectLimb = 1;
            var lres = limb.Finish(img);
            var lumImg = img.Luminance();
            var ob = ObjectDetection.Detect(lumImg, w, h);
            var circ = ObjectDetection.FitLimb(lumImg, w, h, ob) ?? new[] { ob.Cx, ob.Cy, ob.Radius };
            double band = Math.Max(2.0, 0.03 * circ[2]), onLimb = 0, inside = 0;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    double dist = Math.Abs(Math.Sqrt((x - circ[0]) * (x - circ[0]) + (y - circ[1]) * (y - circ[1])) - circ[2]);
                    int i = y * w + x;
                    if (dist <= band) onLimb = Math.Max(onLimb, Math.Abs(lres.Data[1][i] - Math.Min(1, Math.Max(0, img.Data[1][i]))));
                    if (dist >= 2 * band) inside = Math.Max(inside, Math.Abs(lres.Data[1][i] - sres.Data[1][i]));
                }
            CheckTrue("Protect limb at full strength: the limb as it was, the rest sharpened as before", onLimb < 1e-6 && inside < 1e-6, onLimb.ToString("E2") + ", " + inside.ToString("E2"));

            // colour: normalised to grey on the planet, saturation, gamma, contrast and the curve
            var norm = new SharpenProfile { Layers = new double[6], Denoise = 0, NormalizeColour = true };
            var nres = norm.Finish(img);
            var disc = Enumerable.Range(0, w * h).Where(i => lumImg[i] > 0.25).ToList();
            var means = Enumerable.Range(0, 3).Select(c => disc.Average(i => (double)nres.Data[c][i])).ToArray();
            CheckTrue("Normalise the colour: the planet grey on average", Math.Abs(means[0] / means[1] - 1) < 0.02 && Math.Abs(means[2] / means[1] - 1) < 0.02, string.Join(", ", means.Select(m => m.ToString("0.0000"))));
            var grey = new SharpenProfile { Layers = new double[6], Denoise = 0, Saturation = 0 }.Finish(img);
            CheckTrue("Saturation 0: grey", Enumerable.Range(0, w * h).All(i => Math.Abs(grey.Data[0][i] - grey.Data[1][i]) < 1e-5 && Math.Abs(grey.Data[2][i] - grey.Data[1][i]) < 1e-5));
            Func<SharpenProfile, double, double> tone = (p, v) =>
            {
                var flatImage = new AstroImage(16, 16, 1);
                for (int i = 0; i < 256; i++) flatImage.Data[0][i] = (float)v;
                return p.Finish(flatImage).Data[0][8 * 16 + 8];
            };
            var flatLayers = new double[6];
            CheckTrue("Gamma 2 brightens the mid-tones", Math.Abs(tone(new SharpenProfile { Layers = flatLayers, Denoise = 0, Gamma = 2 }, 0.25) - 0.5) < 2e-3);
            CheckTrue("Contrast keeps black, white and the middle, and deepens the shadows", Math.Abs(tone(new SharpenProfile { Layers = flatLayers, Denoise = 0, Contrast = 1 }, 0.25) - 0.15625) < 2e-3
                && tone(new SharpenProfile { Layers = flatLayers, Denoise = 0, Contrast = 1 }, 0) == 0 && Math.Abs(tone(new SharpenProfile { Layers = flatLayers, Denoise = 0, Contrast = 1 }, 1) - 1) < 1e-6
                && Math.Abs(tone(new SharpenProfile { Layers = flatLayers, Denoise = 0, Contrast = 1 }, 0.5) - 0.5) < 2e-3);
            CheckTrue("The curve moves the tones through its points", Math.Abs(tone(new SharpenProfile { Layers = flatLayers, Denoise = 0, Curve = new[] { 0, 0, 0.5, 0.7, 1, 1 } }, 0.5) - 0.7) < 2e-3);

            // kept in the settings file
            var all = new SharpenProfile { LuminanceOnly = true, UnsharpRadius = 2.2, UnsharpAmount = 0.8, NoiseFilter = SharpenNoiseFilter.BilateralAndSigma, NoiseStrength = 1.4, ProtectLimb = 0.6, SuppressHalos = 0.3,
                NormalizeColour = true, Saturation = 1.3, Gamma = 1.1, Contrast = 0.2, Curve = new[] { 0, 0, 0.4, 0.5, 1, 1 }, DeconvMethod = DeconvolutionMethod.Wiener, WienerK = 0.02, PsfFile = @"C:\psf\star.fits" };
            var back = (SharpenProfile)Core.Util.Json.PopulateType(typeof(SharpenProfile), Core.Util.Json.Parse(Core.Util.Json.Serialize(all)));
            CheckTrue("The live settings survive the settings file", back.LuminanceOnly && back.UnsharpRadius == 2.2 && back.UnsharpAmount == 0.8 && back.NoiseFilter == SharpenNoiseFilter.BilateralAndSigma && back.NoiseStrength == 1.4
                && back.ProtectLimb == 0.6 && back.SuppressHalos == 0.3 && back.NormalizeColour && back.Saturation == 1.3 && back.Gamma == 1.1 && back.Contrast == 0.2 && back.Curve.SequenceEqual(all.Curve)
                && back.DeconvMethod == DeconvolutionMethod.Wiener && back.WienerK == 0.02 && back.PsfFile == all.PsfFile);
        }

        [TestMethod]
        public void TestRecordedPsfAndWiener()
        {
            // a recorded star, off centre on a noisy sky
            int w = 64, h = 64;
            var star = Psf.Gaussian(2.5);
            var frame = new AstroImage(w, h, 1);
            var rng = new Random(4);
            int r = star.Size / 2;
            for (int i = 0; i < w * h; i++) frame.Data[0][i] = 0.1f + (float)(rng.NextDouble() * 0.002);
            for (int y = -r; y <= r; y++) for (int x = -r; x <= r; x++) frame.Data[0][(20 + y) * w + 41 + x] += 8f * star.Data[(y + r) * star.Size + x + r];
            var psf = Psf.FromImage(frame);
            int pr = psf.Size / 2, peak = Array.IndexOf(psf.Data, psf.Data.Max());
            CheckTrue("The recorded PSF: odd, centred on the star, adding up to one", psf.Size % 2 == 1 && psf.Size >= 7 && psf.Size <= 63 && peak == pr * psf.Size + pr && Math.Abs(psf.Data.Sum() - 1) < 1e-4, psf.Size + ", " + peak);
            double fw = 0;
            for (int x = 0; x < psf.Size; x++) if (psf.Data[pr * psf.Size + x] >= psf.Data[peak] / 2) fw++;
            CheckTrue("As wide as the star", fw >= 2 && fw <= 4, fw.ToString());
            bool none = false;
            try { Psf.FromImage(new AstroImage(16, 16, 1)); } catch (ArgumentException) { none = true; }
            CheckTrue("An empty image has no PSF", none);

            // deconvolving a blurred disc with Wiener and that PSF brings it closer to the truth
            string dir = Path.Combine(Path.GetTempPath(), "SidraluneTests", "psf");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "star.fits");
            FitsIO.Write(file, frame);
            int dw = 128, dh = 128;
            var truth = Synthetic.Planet(dw, dh, 64, 64, 40, 0, 0, 0, 2);
            var conv = new FftConvolver(dw, dh, Psf.Gaussian(2.5));
            var blurred = new AstroImage(dw, dh, new[] { conv.Convolve(truth.Data[0]) });
            Func<AstroImage, double> err = im => Math.Sqrt(Enumerable.Range(0, dw * dh).Average(i => Math.Pow(im.Data[0][i] - truth.Data[0][i], 2)));
            foreach (var step in new[]
            {
                new SharpenStep { Kind = SharpenStepKind.Deconvolution, Method = DeconvolutionMethod.Wiener, WienerK = 0.002, Fwhm = 2.5 },
                new SharpenStep { Kind = SharpenStepKind.Deconvolution, Method = DeconvolutionMethod.Wiener, WienerK = 0.002, PsfFile = file },
                new SharpenStep { Kind = SharpenStepKind.Deconvolution, Method = DeconvolutionMethod.RichardsonLucyTV, Iterations = 20, PsfFile = file }
            })
            {
                var im = blurred.Clone();
                SharpenProfile.ApplyStep(im, step);
                CheckTrue(string.Format("{0} with {1} sharpens", step.Method, step.PsfFile.Length > 0 ? "the recorded PSF" : "a Gaussian"), err(im) < 0.85 * err(blurred), err(blurred).ToString("0.0000") + " → " + err(im).ToString("0.0000"));
            }
            bool missing = false;
            try { SharpenProfile.ApplyStep(blurred.Clone(), new SharpenStep { Kind = SharpenStepKind.Deconvolution, PsfFile = Path.Combine(dir, "gone.fits") }); }
            catch (FileNotFoundException ex) { missing = ex.Message.Contains("gone.fits"); }
            CheckTrue("A PSF file that is gone says which", missing);
        }

        [TestMethod]
        public void TestDerotationRingsWeightsAndShadows()
        {
            int w = 260, h = 200;
            double period = 10.6562;
            // Saturn: the rings stay as they are, beside the globe and in front of it; the globe turns
            var sat = new DiscGeometry { Cx = 130, Cy = 100, Radius = 50, Flattening = 0.902, PoleAngleDeg = 8, SubObserverLatDeg = -22, RingInner = SphereDerotation.SaturnRingInner, RingOuter = SphereDerotation.SaturnRingOuter };
            bool[] ring;
            var s0 = PlanetRenderer.Render(w, h, sat, 0, period, out ring);
            var s20 = PlanetRenderer.Render(w, h, sat, 1200, period);
            var moved = SphereDerotation.Derotate(s0, sat, 1200, period, false);
            var noRings = sat.Clone(); noRings.RingOuter = 0;
            var old = SphereDerotation.Derotate(s0, noRings, 1200, period, false);
            Func<int, bool> onGlobe = i => { double lon, lat; return SphereDerotation.SurfacePoint(sat, i % w, i / w, out lon, out lat); };
            var beside = Enumerable.Range(0, w * h).Where(i => ring[i] && !onGlobe(i)).ToList();
            var front = Enumerable.Range(0, w * h).Where(i => ring[i] && onGlobe(i)).ToList();
            Func<AstroImage, List<int>, double> err = (im, px) => px.Average(i => Math.Abs(im.Data[0][i] - s20.Data[0][i]));
            CheckTrue("Saturn's rings beside the globe are kept", beside.Count > 500 && err(moved, beside) < 1e-6 && err(old, beside) > 0.3, beside.Count + " px, " + err(moved, beside).ToString("E2") + " (without ring handling " + err(old, beside).ToString("0.00") + ")");
            CheckTrue("The ring in front of the globe stays the ring", front.Count > 50 && err(moved, front) < 1e-6 && err(old, front) > 0.02, front.Count + " px, " + err(moved, front).ToString("E2") + " (without " + err(old, front).ToString("0.000") + ")");
            // and the globe itself is derotated as before, away from its limb
            var inner = Enumerable.Range(0, w * h).Where(i => { double lon, lat; return !ring[i] && SphereDerotation.SurfacePoint(sat, i % w, i / w, out lon, out lat) && Math.Abs(lon) < 0.9 && Math.Abs(lat) < 0.9; }).ToList();
            double ge = inner.Average(i => Math.Abs(moved.Data[0][i] - s20.Data[0][i])), g0e = inner.Average(i => Math.Abs(s0.Data[0][i] - s20.Data[0][i]));
            CheckTrue("Saturn's globe turns", inner.Count > 300 && ge < 0.25 * g0e, ge.ToString("0.0000") + " vs " + g0e.ToString("0.0000"));

            // the disc of a ringed planet from its rings' length
            var ob = ObjectDetection.Detect(s0.Data[0], w, h);
            var disc = SphereDerotation.RingedDisc(s0.Data[0], w, h, ob, sat.PoleAngleDeg, false, SphereDerotation.SaturnRingOuter);
            CheckTrue("Saturn's centre and radius from its rings", disc != null && Math.Abs(disc[0] - sat.Cx) < 1 && Math.Abs(disc[1] - sat.Cy) < 1 && Math.Abs(disc[2] / sat.Radius - 1) < 0.03, disc == null ? "none" : string.Format("{0:0.0}, {1:0.0}, {2:0.0}", disc[0], disc[1], disc[2]));

            // Jupiter: weights by rotation from the target time, and images too far left out
            double jp = 9.92492;
            var jup = new DiscGeometry { Cx = 130, Cy = 100, Radius = 80, Flattening = 0.935, PoleAngleDeg = -12, SubObserverLatDeg = 3 };
            Func<double, AstroImage> at = minutes => PlanetRenderer.Render(w, h, jup, minutes * 60, jp);
            var t0 = new DateTime(2026, 11, 28, 21, 0, 0, DateTimeKind.Utc);
            // 0, about 10 and about 30 degrees of rotation from the target time
            double m10 = 10 / 360.0 * jp * 60, m30 = 30 / 360.0 * jp * 60;
            var imgs = new[] { at(0), at(m10), at(m30) };
            var times = new[] { t0, t0.AddMinutes(m10), t0.AddMinutes(m30) };
            var geos = new[] { jup, jup, jup };
            var res = SphereDerotation.Stack(imgs, geos, times, t0, jp, new DerotationOptions { MaxRotationDeg = 20, FarWeight = 0.5 });
            CheckTrue("Weights fall with the rotation, the farthest is left out", Math.Abs(res.Weight[0] - 1) < 1e-9 && Math.Abs(res.Weight[1] - 0.75) < 0.01 && res.Weight[2] == 0 && res.Used == 2,
                string.Join(", ", res.Weight.Select(x => x.ToString("0.00"))) + " at " + string.Join(", ", res.RotationDeg.Select(x => x.ToString("0.0"))));
            var two = SphereDerotation.Stack(imgs.Take(2).ToList(), geos.Take(2).ToList(), times.Take(2).ToList(), t0, jp, new DerotationOptions { MaxRotationDeg = 20, FarWeight = 0.5 });
            CheckTrue("An image left out changes nothing", res.Image.Data[0].SequenceEqual(two.Image.Data[0]));
            var none = SphereDerotation.Stack(new[] { imgs[2] }, new[] { jup }, new[] { times[2] }, t0, jp, new DerotationOptions { MaxRotationDeg = 20 });
            CheckTrue("The nearest is used even when all are too far", none.Used == 1);

            // orientation: found from two images, mirrored or not
            foreach (var trial in new[] { new { Pa = 37.0, Lat = 3.0, Mirror = false, F = 0.935 }, new { Pa = 205.0, Lat = -18.0, Mirror = true, F = 0.994 } })
            {
                var gt = new DiscGeometry { Cx = 130, Cy = 100, Radius = 80, Flattening = trial.F, PoleAngleDeg = trial.Pa, SubObserverLatDeg = trial.Lat, Mirrored = trial.Mirror };
                double per = trial.F > 0.99 ? 24.6229 : jp, mins = 12 / 360.0 * per * 60; // 12 degrees apart
                var a = PlanetRenderer.Render(w, h, gt, 0, per);
                var b = PlanetRenderer.Render(w, h, gt, mins * 60, per);
                var est = OrientationEstimator.Estimate(a, gt, t0, b, gt, t0.AddMinutes(mins), per);
                double off = est == null ? 999 : Math.Abs(Math.IEEERemainder(est.PoleAngleDeg - trial.Pa, 360));
                CheckTrue(string.Format("Orientation {0}° {1}: found", trial.Pa, trial.Mirror ? "mirrored" : "not mirrored"), est != null && off < 2 && est.Mirrored == trial.Mirror,
                    est == null ? "none" : string.Format("{0:0.0}° {1} (score {2:0.000}, other {3:0.000})", est.PoleAngleDeg, est.Mirrored ? "mirrored" : "not mirrored", est.Score, est.OtherMirrorScore));
            }
            CheckTrue("Too little rotation: no measurement", OrientationEstimator.Estimate(at(0), jup, t0, at(1), jup, t0.AddMinutes(1), jp) == null);

            // a moon's shadow follows its own orbit: left out where each image has it, put where it is at the target time
            double shadowR = 7;
            // moving east, against the turning surface, so derotation alone would put it in the wrong place
            Func<double, double[]> shadowAt = minutes => new[] { -0.1 - 0.012 * minutes, -0.25 };
            Func<double, AstroImage> withShadow = minutes => PlanetRenderer.Render(w, h, jup, minutes * 60, jp, shadowAt(minutes), shadowR);
            var mins3 = new[] { -12.0, 0, 12.0 };
            var shots = mins3.Select(withShadow).ToList();
            var shotTimes = mins3.Select(m => t0.AddMinutes(m)).ToList();
            var truth = withShadow(0);
            Func<double[], MovingSpot> spot = p => { double x, y; SphereDerotation.ImagePoint(jup, p[0], p[1], out x, out y); return new MovingSpot { Id = 1, X = x, Y = y, Radius = shadowR }; };
            var opt = new DerotationOptions { MaxRotationDeg = 0, Spots = mins3.Select(m => (IList<MovingSpot>)new List<MovingSpot> { spot(shadowAt(m)) }).ToList(), TargetSpots = new List<MovingSpot> { spot(shadowAt(0)) } };
            var plainStack = SphereDerotation.Stack(shots, new[] { jup, jup, jup }, shotTimes, t0, jp, new DerotationOptions { MaxRotationDeg = 0 }).Image;
            var followed = SphereDerotation.Stack(shots, new[] { jup, jup, jup }, shotTimes, t0, jp, opt).Image;
            var target = spot(shadowAt(0));
            Func<AstroImage, double, double, double, double> meanIn = (im, cx, cy, r) =>
                Enumerable.Range(0, w * h).Where(i => Math.Pow(i % w - cx, 2) + Math.Pow(i / w - cy, 2) < r * r).Average(i => (double)im.Data[0][i]);
            double truthDark = meanIn(truth, target.X, target.Y, shadowR * 0.7);
            double plainDark = meanIn(plainStack, target.X, target.Y, shadowR * 0.7), followDark = meanIn(followed, target.X, target.Y, shadowR * 0.7);
            CheckTrue("The shadow is as dark as it was at the target time", Math.Abs(followDark - truthDark) < 0.05 && plainDark > truthDark + 0.15, string.Format("truth {0:0.000}, followed {1:0.000}, without {2:0.000}", truthDark, followDark, plainDark));
            // where the other images' shadows went when derotated: no ghosts
            double ghosts = 0, ghostsBefore = 0;
            foreach (int k in new[] { 0, 2 })
            {
                var only = SphereDerotation.Derotate(withShadow(mins3[k]), jup, (t0 - shotTimes[k]).TotalSeconds, jp, false);
                // the derotated shadow: darkest pixels of the derotated image against its shadow-free twin
                var clean = SphereDerotation.Derotate(PlanetRenderer.Render(w, h, jup, mins3[k] * 60, jp), jup, (t0 - shotTimes[k]).TotalSeconds, jp, false);
                var ghostPx = Enumerable.Range(0, w * h).Where(i => clean.Data[0][i] - only.Data[0][i] > 0.2 && Math.Pow(i % w - target.X, 2) + Math.Pow(i / w - target.Y, 2) > 4 * shadowR * shadowR).ToList();
                if (ghostPx.Count == 0) continue;
                ghosts = Math.Max(ghosts, ghostPx.Average(i => truth.Data[0][i] - followed.Data[0][i]));
                ghostsBefore = Math.Max(ghostsBefore, ghostPx.Average(i => truth.Data[0][i] - plainStack.Data[0][i]));
            }
            CheckTrue("No ghost shadows where the others were", ghosts < 0.03 && ghostsBefore > 0.1, string.Format("{0:0.000} (without {1:0.000})", ghosts, ghostsBefore));
        }

        [TestMethod]
        public void TestPlanetaryFilters()
        {
            Func<string, string> f = name => PlanetaryFilters.FromImage(name, 1);
            CheckTrue("FireCapture names: the filter after the observer's initial", f("2026-11-28-2130_4-U-R-Jup_lapl5.tif") == "R" && f("2026-11-28-2130_4-B-R-Jup.tif") == "R" && f("2026-11-28-2131_2-U-B-Jup.tif") == "B");
            CheckTrue("Words and IR filters", f("jupiter_green_0001.tif") == "G" && f("Jup_IR685_213045.tif") == "IR" && f("sat-IR-pass.tif") == "IR" && f("jup_CH4.tif") == "CH4" && f("mars_red.png") == "R");
            CheckTrue("A colour image is colour, a mono one without a filter name is L", PlanetaryFilters.FromImage("2026-11-28-2130_4-U-R-Jup.tif", 3) == "RGB" && f("jupiter.tif") == "L");

            int w = 8, h = 8;
            Func<float, AstroImage> flat = v => { var im = new AstroImage(w, h, 1); for (int i = 0; i < w * h; i++) im.Data[0][i] = v; return im; };
            FilterCombination how; string lum;
            var one = PlanetaryFilters.Combine(new Dictionary<string, AstroImage> { { "R", flat(0.5f) } }, out how, out lum);
            CheckTrue("One filter: that stack", how == FilterCombination.Single && one.Data[0][0] == 0.5f);
            var rgb = PlanetaryFilters.Combine(new Dictionary<string, AstroImage> { { "R", flat(0.6f) }, { "G", flat(0.4f) }, { "B", flat(0.2f) } }, out how, out lum);
            CheckTrue("R, G and B: colour", how == FilterCombination.Rgb && rgb.Channels == 3 && rgb.Data[0][0] == 0.6f && rgb.Data[1][0] == 0.4f && rgb.Data[2][0] == 0.2f);
            var lrgb = PlanetaryFilters.Combine(new Dictionary<string, AstroImage> { { "R", flat(0.6f) }, { "G", flat(0.4f) }, { "B", flat(0.2f) }, { "L", flat(0.8f) }, { "IR", flat(0.1f) } }, out how, out lum);
            var wts = lrgb.EffectiveProfile.LuminanceWeights;
            double Y = wts[0] * lrgb.Data[0][0] + wts[1] * lrgb.Data[1][0] + wts[2] * lrgb.Data[2][0];
            CheckTrue("With L: its brightness, the colour kept", how == FilterCombination.LRgb && lum == "L" && Math.Abs(Y - 0.8) < 1e-4 && Math.Abs(lrgb.Data[0][0] / lrgb.Data[2][0] - 3) < 1e-3);
            PlanetaryFilters.Combine(new Dictionary<string, AstroImage> { { "R", flat(0.6f) }, { "G", flat(0.4f) }, { "B", flat(0.2f) }, { "IR", flat(0.3f) } }, out how, out lum);
            CheckTrue("IR is the luminance when there is no L", how == FilterCombination.LRgb && lum == "IR");
            var col = new AstroImage(w, h, 3);
            PlanetaryFilters.Combine(new Dictionary<string, AstroImage> { { "RGB", col }, { "IR", flat(0.3f) } }, out how, out lum);
            CheckTrue("Colour images with IR", how == FilterCombination.ColourWithLuminance && lum == "IR");
            CheckTrue("R and G alone make no colour", PlanetaryFilters.Combine(new Dictionary<string, AstroImage> { { "R", flat(0.6f) }, { "G", flat(0.4f) } }, out how, out lum) == null && how == FilterCombination.Separate);
        }

        [TestMethod]
        public void TestLuckyStackingDerotation()
        {
            // Jupiter turning over 35 minutes, in two SER videos: 21 degrees, more than the alignment points can follow
            int w = 170, h = 150;
            double period = 9.92492;
            var truthDisc = new DiscGeometry { Cx = 85, Cy = 75, Radius = 55, Flattening = 0.935, PoleAngleDeg = 20, SubObserverLatDeg = 3 };
            var start = new DateTime(2026, 11, 28, 21, 0, 0, DateTimeKind.Utc);
            string dir = Path.Combine(Path.GetTempPath(), "SidraluneTests", "derotstack");
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.CreateDirectory(dir);
            var rng = new Random(5);
            var files = new List<string>();
            DiscGeometry firstDisc = null; // the stack sits where the first frame's disc was
            int perVideo = 30;
            for (int v = 0; v < 2; v++)
            {
                string file = Path.Combine(dir, "jupiter_" + v + ".ser");
                files.Add(file);
                using (var ser = new SerWriter(file, w, h, 16, 1))
                    for (int k = 0; k < perVideo; k++)
                    {
                        // the second video starts 25 minutes after the first
                        var at = start.AddSeconds(v * 1500 + k * 20);
                        var g = truthDisc.Clone(); g.Cx += rng.NextDouble() * 4 - 2; g.Cy += rng.NextDouble() * 4 - 2;
                        if (v == 0 && k == 0) firstDisc = g;
                        var frame = PlanetRenderer.Render(w, h, g, (at - start).TotalSeconds, period);
                        var blurred = Filters.GaussianBlur(frame.Data[0], w, h, 0.8 + rng.NextDouble() * 0.6);
                        for (int i = 0; i < blurred.Length; i++) blurred[i] = (float)Math.Max(0, 0.03 + blurred[i] * 0.8 + (rng.NextDouble() - 0.5) * 0.01);
                        ser.Write(new AstroImage(w, h, new[] { blurred }), at);
                    }
            }
            using (var src = FrameSourceFactory.Open(files))
            {
                CheckTrue("Two videos read as one, with their own time stamps", src is ConcatFrameSource && src.FrameCount == 2 * perVideo
                    && src.Timestamp(perVideo).Value == start.AddSeconds(1500) && src.Timestamp(2 * perVideo - 1).Value == start.AddSeconds(1500 + (perVideo - 1) * 20));
                var mid = start.AddSeconds((1500 + (perVideo - 1) * 20) / 2.0);
                // how well the stack's fine detail matches the planet's at the middle time (1: exactly), on the disc away
                // from the limb; the stack sits where the first frame's disc was, so the truth is drawn there
                var truth = PlanetRenderer.Render(w, h, firstDisc, (mid - start).TotalSeconds, period).Data[0];
                Func<float[], float[]> detail = d => { var b = Filters.GaussianBlur(d, w, h, 6); var o = new float[d.Length]; for (int i = 0; i < d.Length; i++) o[i] = d[i] - b[i]; return o; };
                var truthDetail = detail(Filters.GaussianBlur(truth, w, h, 1.0));
                var inside = Enumerable.Range(0, w * h).Where(i => Math.Pow(i % w - firstDisc.Cx, 2) + Math.Pow((i / w - firstDisc.Cy) / 0.935, 2) < Math.Pow(0.8 * firstDisc.Radius, 2)).ToList();
                Func<AstroImage, double> match = stack =>
                {
                    var d = detail(stack.Data[0]);
                    double ab = 0, aa = 0, bb = 0;
                    foreach (int i in inside) { ab += d[i] * truthDetail[i]; aa += d[i] * d[i]; bb += truthDetail[i] * truthDetail[i]; }
                    return ab / Math.Sqrt(aa * bb);
                };
                var opt = new LuckyOptions { StackPercents = new[] { 60.0 }, ApSize = 32, ApSearchRadius = 4, LocalQuality = false };
                var plain = new LuckyStacker { Options = opt.Clone() }.Run(src);
                var rot = new PlanetRotation { PeriodHours = period, Flattening = 0.935, PoleAngleDeg = 20, SubObserverLatDeg = 3, MeasureOrientation = false };
                var turned = new LuckyStacker { Options = opt.Clone(), Derotation = rot };
                var derotated = turned.Run(src);
                double m0 = match(plain.Stacks[0]), m1 = match(derotated.Stacks[0]);
                // what is left unmatched (1 - match): a quarter or less of the plain stack's
                CheckTrue("Derotated frames stack to the planet at the middle time", m1 > 0.995 && 1 - m1 < 0.25 * (1 - m0), m0.ToString("0.0000") + " → " + m1.ToString("0.0000"));
                CheckTrue("The stack's time is that middle time", derotated.Stacks[0].DateObs.HasValue && Math.Abs((derotated.Stacks[0].DateObs.Value - mid).TotalSeconds) < 1 && derotated.Report.Contains("derotated to"), derotated.Report);
                // a wrong pole angle is put right from the frames themselves
                var guess = new PlanetRotation { PeriodHours = period, Flattening = 0.935, PoleAngleDeg = 60, SubObserverLatDeg = 3, MeasureOrientation = true };
                var measuring = new LuckyStacker { Options = opt.Clone(), Derotation = guess };
                var measured = measuring.Run(src);
                double off = Math.Abs(Math.IEEERemainder(measuring.DerotatedDisc.PoleAngleDeg - 20, 360));
                CheckTrue("The orientation is measured from the frames", off < 3 && !measuring.DerotatedDisc.Mirrored && 1 - match(measured.Stacks[0]) < 0.25 * (1 - m0), measuring.DerotatedDisc.PoleAngleDeg.ToString("0.0") + "°, " + match(measured.Stacks[0]).ToString("0.0000"));
            }
            // the videos must be alike
            var first = FrameSourceFactory.Open(files[0]);
            var other = new FileSequenceSource(new[] { WriteSmall(dir) });
            bool refused = false;
            try { new ConcatFrameSource(new List<IFrameSource> { first, other }); }
            catch (InvalidDataException) { refused = true; }
            finally { first.Dispose(); other.Dispose(); }
            CheckTrue("Videos of different sizes are refused", refused);
        }

        private static string WriteSmall(string dir)
        {
            string p = Path.Combine(dir, "small.tif");
            TiffIO.Write(p, new AstroImage(40, 30, 1));
            return p;
        }
    }
}
