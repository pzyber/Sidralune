using System;
using System.IO;
using System.Linq;
using Sidralune.Imaging;
using Sidralune.Imaging.Analysis;
using Sidralune.Imaging.IO;
using Sidralune.Imaging.Registration;
using Sidralune.Imaging.Stacking;
using static Sidralune.Tests.Program;

namespace Sidralune.Tests
{
    public static class ImagingTests
    {
        private static string TempDir()
        {
            var d = Path.Combine(Path.GetTempPath(), "SidraluneTests");
            Directory.CreateDirectory(d);
            return d;
        }

        public static void TestFitsRoundtrip()
        {
            var img = Synthetic.Render(200, 150, Synthetic.RandomStars(50, 200, 150, 1), Transform2D.Identity, 3, 0.1, 0.01, 2);
            img.Header.Set("OBJECT", "Test field");
            img.ExposureSeconds = 120;
            img.Bayer = BayerPattern.RGGB;
            string p = Path.Combine(TempDir(), "rt.fits");
            FitsIO.Write(p, img, -32);
            var back = FitsIO.Read(p);
            Check("FITS width", back.Width, 200, 0);
            Check("FITS pixel", back.Data[0][150 * 10 + 33], img.Data[0][150 * 10 + 33], 1e-7);
            CheckTrue("FITS keyword", back.ObjectName == "Test field", back.ObjectName);
            Check("FITS exptime", back.ExposureSeconds, 120, 1e-9);
            CheckTrue("FITS bayer roundtrip", back.Bayer == BayerPattern.RGGB, back.Bayer.ToString());
            FitsIO.Write(p, img, 16);
            back = FitsIO.Read(p);
            Check("FITS16 pixel", back.Data[0][1234], img.Data[0][1234], 1.0 / 65535);
            string x = Path.Combine(TempDir(), "rt.xisf");
            XisfIO.Write(x, img, true, true);
            var bx = XisfIO.Read(x);
            Check("XISF pixel", bx.Data[0][4321], img.Data[0][4321], 1e-7);
            string t = Path.Combine(TempDir(), "rt.tif");
            TiffIO.Write(t, img, false);
            var bt = TiffIO.Read(t);
            Check("TIFF16 pixel", bt.Data[0][999], img.Data[0][999], 1.0 / 65535);
        }

        public static void TestSerRoundtrip()
        {
            string p = Path.Combine(TempDir(), "rt.ser");
            var a = Synthetic.Planet(120, 100, 60, 50, 35, 0, 1, 0.01, 1);
            using (var w = new SerWriter(p, 120, 100, 16, 1)) { w.Write(a, new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc)); w.Write(a); }
            using (var r = new SerReader(p))
            {
                Check("SER frames", r.FrameCount, 2, 0);
                Check("SER pixel", r.ReadFrame(0).Data[0][50 * 120 + 60], a.Data[0][50 * 120 + 60], 1.0 / 65535);
                CheckTrue("SER timestamp", r.Timestamp(0) == new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc));
            }
            string av = Path.Combine(TempDir(), "rt.avi");
            using (var w = new AviWriter(av, 120, 100, 1, 25)) { w.Write(a); w.Write(a); w.Write(a); }
            using (var r = new AviReader(av))
            {
                Check("AVI frames", r.FrameCount, 3, 0);
                Check("AVI pixel", r.ReadFrame(1).Data[0][50 * 120 + 60], a.Data[0][50 * 120 + 60], 1.0 / 255);
            }
        }

        public static void TestStarDetectionAndMatching()
        {
            int w = 800, h = 600;
            var stars = Synthetic.RandomStars(300, w, h, 7);
            var a = Synthetic.Render(w, h, stars, Transform2D.Identity, 3.0, 0.05, 0.003, 11, 30);
            var t = Transform2D.Rotation(1.5 * Math.PI / 180, 400, 300).Compose(Transform2D.Translation(12.3, -7.8));
            var b = Synthetic.Render(w, h, stars, t, 3.4, 0.06, 0.003, 12, 30);
            var sa = StarDetector.Detect(a);
            var sb = StarDetector.Detect(b);
            Info(string.Format("detected {0} / {1} stars", sa.Count, sb.Count));
            CheckTrue("Detected enough stars", sa.Count > 80 && sb.Count > 80);
            double fwhm, hfr, ecc;
            StarDetector.Summary(sa, out fwhm, out hfr, out ecc);
            Check("Measured FWHM", fwhm, 3.0, 0.35);
            var m = StarMatcher.Match(sa, sb, TransformModel.Affine);
            CheckTrue("Match success", m.Success, m.Matches + " matches, rms " + m.RmsError.ToString("0.000"));
            // m maps b -> a; compare with inverse of t
            var inv = t.Inverse();
            double u1, v1, u2, v2;
            m.Transform.Apply(100, 500, out u1, out v1);
            inv.Apply(100, 500, out u2, out v2);
            Check("Recovered transform x", u1, u2, 0.1);
            Check("Recovered transform y", v1, v2, 0.1);
        }

        public static void TestStackingPipeline()
        {
            int w = 400, h = 300, n = 8;
            var dir = Path.Combine(TempDir(), "stack");
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.CreateDirectory(dir);
            var stars = Synthetic.RandomStars(150, w, h, 3);
            var rng = new Random(5);
            var files = new System.Collections.Generic.List<string>();
            for (int i = 0; i < n; i++)
            {
                var t = Transform2D.Translation(rng.NextDouble() * 20 - 10, rng.NextDouble() * 20 - 10).Compose(Transform2D.Rotation((rng.NextDouble() - 0.5) * 0.02, w / 2, h / 2));
                var img = Synthetic.Render(w, h, stars, t, 2.8, 0.05, 0.01, 100 + i, 20);
                img.ExposureSeconds = 60;
                string p = Path.Combine(dir, "light_" + i + ".fits");
                FitsIO.Write(p, img, -32);
                files.Add(p);
            }
            var single = FitsIO.Read(files[0]);
            double noise1 = ImageStatistics.MrsNoise(single.Data[0], w, h);
            var eng = new StackingEngine();
            eng.Settings.LightCombine.Method = CombineMethod.KappaSigma;
            eng.Settings.Mode = StackingMode.Standard;
            eng.AddFiles(files, FrameKind.Light);
            var res = eng.Run();
            Info(res.Report);
            double noiseN = ImageStatistics.MrsNoise(res.Image.Data[0], res.Image.Width, res.Image.Height);
            Info(string.Format("noise single {0:0.00000} stacked {1:0.00000} ratio {2:0.00} (ideal {3:0.00})", noise1, noiseN, noise1 / noiseN, Math.Sqrt(n)));
            Check("Frames stacked", res.FramesStacked, n, 0);
            CheckTrue("Stacking reduces noise by > 2x", noise1 / noiseN > 2.0);
            // hot pixels must be rejected: maximum near 1.0 should be gone except star cores
            var st = StarDetector.Detect(res.Image);
            double f, hf, e;
            StarDetector.Summary(st, out f, out hf, out e);
            Check("Stacked FWHM stays sharp", f, 2.8, 0.6);
            foreach (var m in new[] { CombineMethod.Median, CombineMethod.WinsorizedSigma, CombineMethod.LinearFitClipping, CombineMethod.GeneralizedEsd, CombineMethod.AutoAdaptiveWeightedAverage })
            {
                eng.Settings.LightCombine.Method = m;
                var r2 = eng.Stack(eng.BuildCalibration(null, default(System.Threading.CancellationToken)), null, default(System.Threading.CancellationToken));
                double nn = ImageStatistics.MrsNoise(r2.Image.Data[0], w, h);
                CheckTrue("Method " + m + " reduces noise", noise1 / nn > 1.7, (noise1 / nn).ToString("0.00"));
            }
        }

        public static void TestPhaseCorrelation()
        {
            var a = Synthetic.Planet(128, 128, 64, 64, 40, 0, 1.0, 0.005, 1);
            var b = Geometry.Shift(a, 3.4, -2.2);
            double dx, dy;
            Sidralune.Core.Mathematics.Fft.PhaseCorrelate(a.Data[0], b.Data[0], 128, 128, out dx, out dy, Sidralune.Core.Mathematics.Fft.Hann2D(128, 128));
            Check("Phase corr dx", dx, -3.4, 0.25);
            Check("Phase corr dy", dy, 2.2, 0.25);
        }
    }
}
