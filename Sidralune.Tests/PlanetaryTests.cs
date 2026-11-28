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
    }
}
