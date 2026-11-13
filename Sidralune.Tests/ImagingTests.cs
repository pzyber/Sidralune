using System;
using System.IO;
using System.Linq;
using Sidralune.Imaging;
using Sidralune.Imaging.Analysis;
using Sidralune.Imaging.IO;
using Sidralune.Imaging.Registration;
using Sidralune.Imaging.Stacking;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Sidralune.Tests.Checks;

namespace Sidralune.Tests
{
    [TestClass]
    public class ImagingTests : TestBase
    {
        private static string TempDir()
        {
            var d = Path.Combine(Path.GetTempPath(), "SidraluneTests");
            Directory.CreateDirectory(d);
            return d;
        }

        [TestMethod]
        public void TestFitsRoundtrip()
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

        [TestMethod]
        public void TestTiffLzw()
        {
            // an LZW-compressed TIFF as Windows writes it: gradients, noise and long runs, large enough for the code
            // table to fill and be cleared many times and for a code to refer to the entry being made (KwKwK)
            int w = 1500, h = 900;
            var rnd = new Random(7);
            string p = Path.Combine(TempDir(), "lzw.tif");
            using (var bmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format24bppRgb))
            {
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int r = x * 255 / w, g = y < h / 3 ? 128 : y < 2 * h / 3 ? rnd.Next(256) : (x / 7) % 2 * 255, b = (x + y) % 256;
                        bmp.SetPixel(x, y, System.Drawing.Color.FromArgb(r, g, b));
                    }
                var enc = System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders().First(e => e.FormatID == System.Drawing.Imaging.ImageFormat.Tiff.Guid);
                var prm = new System.Drawing.Imaging.EncoderParameters(1);
                prm.Param[0] = new System.Drawing.Imaging.EncoderParameter(System.Drawing.Imaging.Encoder.Compression, (long)System.Drawing.Imaging.EncoderValue.CompressionLZW);
                bmp.Save(p, enc, prm);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var img = TiffIO.Read(p);
                Info("1500 × 900 LZW TIFF read in " + sw.ElapsedMilliseconds + " ms");
                int bad = 0;
                for (int y = 0; y < h && bad < 5; y++)
                    for (int x = 0; x < w; x++)
                    {
                        var c = bmp.GetPixel(x, y);
                        int i = y * w + x;
                        if (Math.Abs(img.Data[0][i] * 255 - c.R) > 0.01 || Math.Abs(img.Data[1][i] * 255 - c.G) > 0.01 || Math.Abs(img.Data[2][i] * 255 - c.B) > 0.01) { if (++bad >= 5) break; }
                    }
                CheckTrue("An LZW TIFF decodes to exactly the pixels written", img.Width == w && img.Height == h && img.Channels == 3 && bad == 0, bad + " pixels differ");
            }
            // 32-bit float, read without allocating per sample
            var f = new AstroImage(3, 2, 1);
            f.Data[0][0] = -0.25f; f.Data[0][1] = 1.5f; f.Data[0][5] = 0.123456f;
            string fp = Path.Combine(TempDir(), "float.tif");
            TiffIO.Write(fp, f, true);
            var fb = TiffIO.Read(fp);
            CheckTrue("32-bit float TIFF values, also outside 0–1", fb.Data[0][0] == -0.25f && fb.Data[0][1] == 1.5f && fb.Data[0][5] == 0.123456f);
        }

        [TestMethod]
        public void TestQuickStatistics()
        {
            // the quick statistics (for the display stretch and background levels) agree with the full ones
            var img = Synthetic.Render(800, 600, Synthetic.RandomStars(300, 800, 600, 3), Transform2D.Identity, 3, 0.1, 0.01, 2);
            var full = ImageStatistics.Compute(img.Data[0], img.Width, 0);
            var quick = ImageStatistics.Robust(img.Data[0]);
            CheckTrue("Quick median, MAD and background equal the full statistics", quick.Median == full.Median && quick.Mad == full.Mad && quick.Background == full.Background && quick.BackgroundSigma == full.BackgroundSigma,
                quick.Median + " " + full.Median + " / " + quick.Background + " " + full.Background);
            // and a large plane takes a fraction of the time
            var big = new float[7488 * 5688];
            var rnd = new Random(3);
            for (int i = 0; i < big.Length; i += 13) big[i] = (float)rnd.NextDouble();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            ImageStatistics.Robust(big);
            long q = sw.ElapsedMilliseconds;
            sw.Restart();
            ImageStatistics.Compute(big, 7488, 0);
            long f = sw.ElapsedMilliseconds;
            Info(string.Format("42.6-megapixel plane: quick statistics {0} ms, full {1} ms", q, f));
            CheckTrue("The quick statistics are much faster on a large image", q * 3 < f, q + " vs " + f + " ms");
        }

        [TestMethod]
        public void TestSerRoundtrip()
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

        [TestMethod]
        public void TestStarDetectionAndMatching()
        {
            int w = 800, h = 600;
            var stars = Synthetic.RandomStars(300, w, h, 7);
            var a = Synthetic.Render(w, h, stars, Transform2D.Identity, 3.0, 0.05, 0.003, 11, 30);
            var t = Transform2D.Rotation(1.5 * Math.PI / 180, 400, 300).Compose(Transform2D.Translation(12.3, -7.8));
            var b = Synthetic.Render(w, h, stars, t, 3.4, 0.06, 0.003, 12, 30);
            var sa = StarDetector.Detect(a);
            var sb = StarDetector.Detect(b);
            Info(string.Format("detected {0} / {1} stars", sa.Count, sb.Count));
            // the frames also hold 30 hot pixels each, which must not count as stars
            CheckTrue("Detected enough stars", sa.Count > 70 && sb.Count > 70);
            CheckTrue("No hot pixels among the stars", sa.All(s => s.Fwhm >= 0.8) && sb.All(s => s.Fwhm >= 0.8));
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

        [TestMethod]
        public void TestStackingPipeline()
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

        [TestMethod]
        public void TestPhaseCorrelation()
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
