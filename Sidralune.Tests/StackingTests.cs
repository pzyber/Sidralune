using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Sidralune.Core.Mathematics;
using Sidralune.Imaging;
using Sidralune.Imaging.Analysis;
using Sidralune.Imaging.IO;
using Sidralune.Imaging.Planetary;
using Sidralune.Imaging.Registration;
using Sidralune.Imaging.Stacking;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Sidralune.Tests.Checks;

namespace Sidralune.Tests
{
    /// <summary>Pixel rejection, calibration masters, the stacking engine's options, drizzle, video and raw decoding, solar tools and mosaics.</summary>
    [TestClass]
    public class StackingTests : TestBase
    {
        private static string Folder(string name)
        {
            string dir = Path.Combine(Path.GetTempPath(), "SidraluneTests", name);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static float Gauss(Random r) => (float)Stats.NormalQuantile(Math.Max(1e-9, Math.Min(1 - 1e-9, r.NextDouble())));

        [TestMethod]
        public void TestPixelRejection()
        {
            var values = new float[] { 1.0f, 1.1f, 0.9f, 1.05f, 0.95f, 1.02f, 0.98f, 10f, -5f };
            int n = values.Length;
            var robust = new[] { CombineMethod.KappaSigma, CombineMethod.MedianKappaSigma, CombineMethod.WinsorizedSigma, CombineMethod.LinearFitClipping,
                CombineMethod.GeneralizedEsd, CombineMethod.PercentileClipping, CombineMethod.Median, CombineMethod.AutoAdaptiveWeightedAverage };
            foreach (var m in robust)
            {
                var opt = new CombineOptions { Method = m, KappaLow = 2, KappaHigh = 2 };
                float r = PixelCombiner.Combine((float[])values.Clone(), n, opt, null, null);
                CheckTrue(m + " rejects the outliers", Math.Abs(r - 1.0) < 0.06, r.ToString("0.0000"));
            }
            Check("Average keeps everything", PixelCombiner.Combine((float[])values.Clone(), n, new CombineOptions { Method = CombineMethod.Average }, null, null), values.Average(), 1e-5);
            Check("Maximum", PixelCombiner.Combine((float[])values.Clone(), n, new CombineOptions { Method = CombineMethod.Maximum }, null, null), 10, 0);
            Check("Minimum", PixelCombiner.Combine((float[])values.Clone(), n, new CombineOptions { Method = CombineMethod.Minimum }, null, null), -5, 0);
            Check("Weighted average", PixelCombiner.Combine(new[] { 1f, 3f }, 2, new CombineOptions { Method = CombineMethod.Average }, new[] { 3.0, 1.0 }, null), 1.5, 1e-6);
            var nanVals = new[] { 1f, float.NaN, 1.2f, 0.8f };
            Check("NaN pixels are ignored", PixelCombiner.Combine(nanVals, 4, new CombineOptions { Method = CombineMethod.Average }, null, null), 1, 1e-6);
            var pc = new PixelCombiner(9, new CombineOptions { Method = CombineMethod.KappaSigma, KappaLow = 2, KappaHigh = 2 }, null);
            pc.Combine((float[])values.Clone(), n);
            CheckTrue("Rejection is counted", pc.Rejected >= 2 && pc.Total == n, pc.Rejected + " of " + pc.Total);

            // with a large Gaussian sample every rejection method lands on the mean and none rejects much
            var rnd = new Random(2);
            var big = Enumerable.Range(0, 40).Select(i => 0.5f + 0.01f * Gauss(rnd)).ToArray();
            foreach (var m in robust)
            {
                var c = new PixelCombiner(40, new CombineOptions { Method = m }, null);
                float r = c.Combine((float[])big.Clone(), big.Length);
                CheckTrue(m + " on clean data", Math.Abs(r - big.Average()) < 0.006 && c.Rejected <= 8, r.ToString("0.0000") + ", rejected " + c.Rejected);
            }
        }

        [TestMethod]
        public void TestCalibration()
        {
            int w = 64, h = 48;
            string dir = Folder("calibration");
            var rnd = new Random(4);
            // sensor model: bias 0.05, dark current 0.0005/s with three hot pixels, vignetted flat
            var hot = new[] { 10 * w + 10, 20 * w + 40, 30 * w + 5 };
            Func<int, int, double> vignette = (x, y) => 1 - 0.3 * (Math.Pow((x - w / 2.0) / w, 2) + Math.Pow((y - h / 2.0) / h, 2)) * 4;
            Func<double, double, AstroImage> frame = (exp, sky) =>
            {
                var img = new AstroImage(w, h, 1);
                img.ExposureSeconds = exp;
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int i = y * w + x;
                        double thermal = 0.0005 * exp * (hot.Contains(i) ? 400 : 1);
                        img.Data[0][i] = (float)(0.05 + thermal + sky * vignette(x, y) + 0.001 * Gauss(rnd));
                    }
                return img;
            };
            var biasFiles = new List<string>(); var darkFiles = new List<string>(); var flatFiles = new List<string>();
            for (int i = 0; i < 9; i++)
            {
                string b = Path.Combine(dir, "bias_" + i + ".fits"), d = Path.Combine(dir, "dark_" + i + ".fits"), f = Path.Combine(dir, "flat_" + i + ".fits");
                FitsIO.Write(b, frame(0, 0)); FitsIO.Write(d, frame(120, 0)); FitsIO.Write(f, frame(0.001, 0.4 + 0.02 * i));
                biasFiles.Add(b); darkFiles.Add(d); flatFiles.Add(f);
            }
            var mBias = Calibration.CreateMaster(biasFiles, FrameKind.Bias, new CombineOptions { Method = CombineMethod.Median });
            Check("Master bias level", Stats.Median(mBias.Data[0].Select(v => (double)v).ToList()), 0.05, 0.001);
            CheckTrue("Master bias header", mBias.Header.GetString("IMAGETYP") == "Master Bias" && mBias.Header.GetInt("NCOMBINE") == 9);
            var mDark = Calibration.CreateMaster(darkFiles, FrameKind.Dark, new CombineOptions { Method = CombineMethod.MedianKappaSigma }, mBias);
            Check("Master dark (bias removed) is the dark current", Stats.Median(mDark.Data[0].Select(v => (double)v).ToList()), 0.06, 0.001);
            var mFlat = Calibration.CreateMaster(flatFiles, FrameKind.Flat, new CombineOptions { Method = CombineMethod.Median }, mBias);
            // normalised on the central half of the frame, away from the vignetted corners
            double centralMean = 0; int cn = 0;
            for (int y = h / 4; y < 3 * h / 4; y++) for (int x = w / 4; x < 3 * w / 4; x++) { centralMean += mFlat.Data[0][y * w + x]; cn++; }
            Check("Master flat normalised to 1 in the centre", centralMean / cn, 1, 1e-4);
            Check("Master flat follows the vignetting", mFlat.Data[0][0] / mFlat.Data[0][(h / 2) * w + w / 2], vignette(0, 0), 0.02);
            var hotMap = Calibration.HotPixelMap(mDark, 6);
            CheckTrue("Hot pixel map finds the hot pixels", hot.All(i => hotMap[i]) && hotMap.Count(b => b) <= 10, hotMap.Count(b => b) + " flagged");

            // a 60 s light with a flat sky: calibration leaves a flat image with no hot pixels
            var light = frame(60, 0.2);
            var cal = new CalibrationSet { MasterBias = mBias, MasterDark = mDark, MasterFlat = mFlat, MasterDarkIsBiasSubtracted = true, MasterDarkExposure = 120, DefectMap = hotMap };
            Calibration.CalibrateLight(light, cal);
            var ld = light.Data[0];
            double centre = ld[(h / 2) * w + w / 2], corner = ld[2 * w + 2];
            CheckTrue("Calibrated sky is flat (vignetting removed)", Math.Abs(centre - corner) < 0.01, string.Format("{0:0.0000} vs {1:0.0000}", centre, corner));
            CheckTrue("Hot pixels gone", hot.All(i => Math.Abs(ld[i] - centre) < 0.02), string.Join(", ", hot.Select(i => ld[i].ToString("0.000"))));
            Check("Dark scaled by exposure (60 s / 120 s)", light.Header.GetDouble("DARKSCAL"), 0.5, 1e-9);

            var lightB = frame(60, 0.2);
            var thermalScaled = mDark.Clone();
            lightB.Header.Set("EXPTIME", 60.0);
            Calibration.Subtract(lightB, mBias, 0);
            Check("Optimised dark scale finds 0.5", Calibration.OptimizeDarkScale(lightB, thermalScaled, 1.0), 0.5, 0.1);

            var defects = new AstroImage(w, h, 1);
            for (int i = 0; i < w * h; i++) defects.Data[0][i] = 0.3f + 0.002f * Gauss(rnd);
            defects.Data[0][5 * w + 5] = 1; defects.Data[0][25 * w + 25] = 0;
            int fixedCount = Calibration.CosmeticCorrection(defects, null, 5, 5);
            CheckTrue("Cosmetic correction fixes a hot and a cold pixel", fixedCount >= 2 && Math.Abs(defects.Data[0][5 * w + 5] - 0.3) < 0.02 && Math.Abs(defects.Data[0][25 * w + 25] - 0.3) < 0.02, fixedCount + " fixed");

            CheckTrue("Frame kind from file names and headers", StackingEngine.GuessKind("M31_dark_120s.fits") == FrameKind.Dark && StackingEngine.GuessKind("FlatDark_001.fit") == FrameKind.DarkFlat
                && StackingEngine.GuessKind("bias.fit") == FrameKind.Bias && StackingEngine.GuessKind("flat-Ha.fit") == FrameKind.Flat && StackingEngine.GuessKind("M31_0001.fit") == FrameKind.Light
                && StackingEngine.GuessKind("x.fit", new AstroImage(1, 1, 1) { Header = HeaderWith("IMAGETYP", "Dark Frame") }) == FrameKind.Dark);
        }

        private static FitsHeader HeaderWith(string k, string v) { var h = new FitsHeader(); h.Set(k, v); return h; }

        [TestMethod]
        public void TestSettingsCopy()
        {
            // a run works on a copy, so the settings shown can change while it works, nested options included
            var s = new StackingSettings { KeepPercent = 80, CustomRect = new[] { 1, 2, 3, 4 } };
            s.LightCombine.KappaHigh = 3;
            var run = s.Clone();
            s.KeepPercent = 50; s.LightCombine.KappaHigh = 1.5; s.DarkCombine.Method = CombineMethod.Average; s.CustomRect[0] = 99;
            CheckTrue("The run keeps the settings it started with, combine options and rectangle too",
                run.KeepPercent == 80 && run.LightCombine.KappaHigh == 3 && run.DarkCombine.Method != CombineMethod.Average && run.CustomRect[0] == 1
                && run.LightCombine != s.LightCombine && run.FlatCombine != s.FlatCombine && run.BiasCombine != s.BiasCombine);
            CheckTrue("A copy without a rectangle stays without one", new StackingSettings().Clone().CustomRect == null);
        }

        [TestMethod]
        public void TestStackingOptions()
        {
            int w = 240, h = 180, n = 6;
            string dir = Folder("stack-options");
            var stars = Synthetic.RandomStars(120, w, h, 8);
            var rng = new Random(3);
            var files = new List<string>();
            for (int i = 0; i < n; i++)
            {
                var t = Transform2D.Translation(rng.NextDouble() * 10 - 5, rng.NextDouble() * 10 - 5);
                var img = Synthetic.Render(w, h, stars, t, i == n - 1 ? 4.0 : 2.8, 0.05, 0.01, 200 + i, 10);
                img.ExposureSeconds = 30;
                string p = Path.Combine(dir, "light_" + i + ".fits");
                FitsIO.Write(p, img, -32);
                files.Add(p);
            }
            var eng = new StackingEngine();
            eng.AddFiles(files);
            CheckTrue("Frames added once, as lights, with exposure", eng.Frames.Count == n && eng.Frames.All(f => f.Kind == FrameKind.Light && f.Exposure == 30));
            eng.AddFiles(files);
            CheckTrue("Adding the same files again is ignored", eng.Frames.Count == n);
            var res = eng.Run();
            CheckTrue("Standard stack", res.FramesStacked == n && res.Image.Width == w && Math.Abs(res.TotalExposure - n * 30) < 1e-9);
            var bad = eng.Frames.Single(f => f.Path == files[n - 1]);
            CheckTrue("The blurred frame scores lowest", eng.Frames.Where(f => f != bad).All(f => f.Score > bad.Score));

            eng.Settings.KeepPercent = 50;
            var half = eng.Run();
            CheckTrue("Keep the best 50%", half.FramesStacked == n / 2, half.FramesStacked + " stacked");
            eng.Settings.KeepPercent = 100;

            eng.Settings.Mode = StackingMode.Intersection;
            var inter = eng.Run();
            CheckTrue("Intersection mode crops to the common area", inter.Image.Width <= w && inter.Image.Height <= h && inter.Image.Width >= w - 12, inter.Image.Width + "×" + inter.Image.Height);
            eng.Settings.Mode = StackingMode.Mosaic;
            var mosaic = eng.Run();
            CheckTrue("Mosaic mode covers every frame", mosaic.Image.Width >= w && mosaic.Image.Height >= h, mosaic.Image.Width + "×" + mosaic.Image.Height);
            eng.Settings.Mode = StackingMode.Custom;
            eng.Settings.CustomRect = new[] { 20, 30, 100, 80 };
            var custom = eng.Run();
            CheckTrue("Custom rectangle", custom.Image.Width == 100 && custom.Image.Height == 80, custom.Image.Width + "×" + custom.Image.Height);
            eng.Settings.Mode = StackingMode.Standard;

            eng.Settings.DrizzleScale = 2;
            var drizzled = eng.Run();
            CheckTrue("Drizzle ×2 doubles the size", drizzled.Image.Width == 2 * w && drizzled.Image.Height == 2 * h, drizzled.Image.Width + "×" + drizzled.Image.Height);
            eng.Settings.DrizzleScale = 1;

            foreach (var norm in new[] { NormalizationMode.None, NormalizationMode.Additive, NormalizationMode.Multiplicative })
                foreach (var wt in new[] { WeightingMode.None, WeightingMode.Score, WeightingMode.Snr })
                {
                    eng.Settings.Normalization = norm; eng.Settings.Weighting = wt;
                    var r = eng.Run();
                    CheckTrue(string.Format("Normalisation {0}, weighting {1}", norm, wt), r.FramesStacked == n && r.Image.Data[0].All(v => !float.IsNaN(v)));
                }

            string project = Path.Combine(dir, "project.sdstack");
            eng.Frames[1].Enabled = false; eng.Frames[2].Group = "Night 2"; eng.Frames[3].CometXY = new[] { 12.5, 40.25 };
            eng.Settings.KeepPercent = 80; eng.Settings.Alignment = TransformModel.Homography;
            eng.SaveProject(project);
            var back = new StackingEngine();
            back.LoadProject(project);
            CheckTrue("Project round trip", back.Frames.Count == n && !back.Frames[1].Enabled && back.Frames[2].Group == "Night 2" && back.Frames[3].CometXY[1] == 40.25
                && back.Settings.KeepPercent == 80 && back.Settings.Alignment == TransformModel.Homography);
        }

        [TestMethod]
        public void TestDrizzle()
        {
            int w = 40, h = 30;
            var img = new AstroImage(w, h, 1);
            var rnd = new Random(1);
            for (int i = 0; i < w * h; i++) img.Data[0][i] = (float)rnd.NextDouble();
            var acc = new DrizzleAccumulator(w, h, 1, 1.0);
            acc.Add(img, Transform2D.Identity, 1);
            var r = acc.Result();
            double worst = 0;
            for (int i = 0; i < w * h; i++) worst = Math.Max(worst, Math.Abs(r.Data[0][i] - img.Data[0][i]));
            Check("Drizzle with the identity and pixfrac 1 reproduces the image", worst, 0, 1e-6);
            var flat = new AstroImage(w, h, new[] { Enumerable.Repeat(0.4f, w * h).ToArray() });
            var up = new DrizzleAccumulator(2 * w, 2 * h, 1, 0.7);
            for (int k = 0; k < 4; k++) up.Add(flat, Transform2D.Affine(2, 0, 0.5 + (k & 1), 0, 2, 0.5 + (k >> 1)), 1);
            var ur = up.Result();
            var inner = new List<double>();
            for (int y = 4; y < 2 * h - 4; y++) for (int x = 4; x < 2 * w - 4; x++) inner.Add(ur.Data[0][y * 2 * w + x]);
            CheckTrue("Drizzle ×2 of a flat field stays flat and keeps its level", inner.All(v => Math.Abs(v - 0.4) < 1e-5) && ur.Weight.Skip(2 * w * 4).Take(2 * w).Any(v => v > 0));
        }

        [TestMethod]
        public void TestVideoAndSequences()
        {
            string dir = Folder("video-io");
            string avi = Path.Combine(dir, "test.avi");
            int w = 48, h = 32;
            var frames = new List<AstroImage>();
            for (int i = 0; i < 5; i++)
            {
                var f = new AstroImage(w, h, 3);
                for (int c = 0; c < 3; c++) for (int p = 0; p < w * h; p++) f.Data[c][p] = ((p + 7 * i + 50 * c) % 256) / 255f;
                frames.Add(f);
            }
            using (var aw = new AviWriter(avi, w, h, 3, 25)) foreach (var f in frames) aw.Write(f);
            using (var src = FrameSourceFactory.Open(avi))
            {
                CheckTrue("AVI opened as AVI", src is AviReader && src.FrameCount == 5 && src.Width == w && src.Height == h && src.Channels == 3);
                Check("AVI frame rate", ((AviReader)src).FramesPerSecond, 25, 1e-6);
                double worst = 0;
                for (int i = 0; i < 5; i++)
                {
                    var r = src.ReadFrame(i);
                    for (int c = 0; c < 3; c++) for (int p = 0; p < w * h; p++) worst = Math.Max(worst, Math.Abs(r.Data[c][p] - frames[i].Data[c][p]));
                }
                Check("AVI frames round trip (8-bit)", worst, 0, 0.5 / 255 + 1e-6);
            }
            string monoAvi = Path.Combine(dir, "mono.avi");
            using (var aw = new AviWriter(monoAvi, w, h, 1)) aw.Write(frames[0].ExtractChannel(1));
            using (var src = FrameSourceFactory.Open(monoAvi))
                CheckTrue("Mono AVI", src.Channels == 1 && Math.Abs(src.ReadFrame(0).Data[0][100] - frames[0].Data[1][100]) < 0.5 / 255 + 1e-6);

            var files = new List<string>();
            for (int i = 0; i < 3; i++)
            {
                var f = frames[i].ExtractChannel(0);
                f.Header.Set("DATE-OBS", new DateTime(2026, 10, 24, 22, 0, i).ToString("yyyy-MM-ddTHH:mm:ss"));
                string p = Path.Combine(dir, "seq_" + i + ".fits");
                FitsIO.Write(p, f);
                files.Add(p);
            }
            using (var seq = FrameSourceFactory.Open(files))
            {
                CheckTrue("Image sequence source", seq is FileSequenceSource && seq.FrameCount == 3 && seq.Width == w);
                CheckTrue("Sequence time stamps from DATE-OBS", seq.Timestamp(2) == new DateTime(2026, 10, 24, 22, 0, 2) && seq.Timestamp(0) == new DateTime(2026, 10, 24, 22, 0, 0));
                CheckTrue("Sequence frame contents", Math.Abs(seq.ReadFrame(1).Data[0][9] - frames[1].Data[0][9]) < 1e-6);
            }
            using (var single = FrameSourceFactory.Open(new[] { files[0] }))
                CheckTrue("A single image is a one-frame source", single.FrameCount == 1);
            bool threw = false;
            try { new FileSequenceSource(new string[0]); } catch (ArgumentException) { threw = true; }
            CheckTrue("Empty sequence refused", threw);
        }

        // ------------------------------------------------------------------ lossless JPEG (DNG / CR2 raw data)

        private class BitWriter
        {
            public readonly List<byte> Bytes = new List<byte>();
            private int _acc, _n;
            public void Put(int value, int bits)
            {
                for (int i = bits - 1; i >= 0; i--)
                {
                    _acc = (_acc << 1) | ((value >> i) & 1);
                    if (++_n == 8) { Emit((byte)_acc); _acc = 0; _n = 0; }
                }
            }
            private void Emit(byte b) { Bytes.Add(b); if (b == 0xFF) Bytes.Add(0); }
            public void Flush() { while (_n != 0) Put(1, 1); }
            public void Marker(int m) { Flush(); Bytes.Add(0xFF); Bytes.Add((byte)m); }
        }

        /// <summary>A minimal ITU T.81 lossless JPEG encoder (one Huffman table, 5-bit codes for the 17 categories).</summary>
        private static byte[] EncodeLosslessJpeg(ushort[] s, int w, int h, int nc, int precision, int predictor, int restart)
        {
            var o = new List<byte> { 0xFF, 0xD8 };
            Action<int, byte[]> seg = (marker, body) => { o.Add(0xFF); o.Add((byte)marker); o.Add((byte)((body.Length + 2) >> 8)); o.Add((byte)((body.Length + 2) & 0xFF)); o.AddRange(body); };
            var sof = new List<byte> { (byte)precision, (byte)(h >> 8), (byte)h, (byte)(w >> 8), (byte)w, (byte)nc };
            for (int c = 0; c < nc; c++) sof.AddRange(new byte[] { (byte)(c + 1), 0x11, 0 });
            seg(0xC3, sof.ToArray());
            var dht = new List<byte> { 0x00 };
            for (int l = 1; l <= 16; l++) dht.Add((byte)(l == 5 ? 17 : 0));
            for (int v = 0; v <= 16; v++) dht.Add((byte)v);
            seg(0xC4, dht.ToArray());
            if (restart > 0) seg(0xDD, new[] { (byte)(restart >> 8), (byte)restart });
            var sos = new List<byte> { (byte)nc };
            for (int c = 0; c < nc; c++) sos.AddRange(new byte[] { (byte)(c + 1), 0x00 });
            sos.AddRange(new byte[] { (byte)predictor, 0, 0 });
            seg(0xDA, sos.ToArray());
            var bw = new BitWriter();
            int initial = 1 << (precision - 1), mcu = 0, rst = 0;
            bool lineStart = true; // the first line of the scan or of a restart interval uses the 1-D predictor
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    bool newInterval = restart > 0 && mcu > 0 && mcu % restart == 0;
                    if (newInterval) { bw.Marker(0xD0 + (rst++ & 7)); }
                    bool intervalStart = mcu == 0 || newInterval;
                    if (intervalStart) lineStart = true;
                    if (x == 0 && !intervalStart) lineStart = lineStart && false;
                    for (int c = 0; c < nc; c++)
                    {
                        int idx = (y * w + x) * nc + c;
                        int pred;
                        if (intervalStart) pred = initial;
                        else if (lineStart && x > 0) pred = s[idx - nc];
                        else if (x == 0) pred = s[idx - w * nc];
                        else
                        {
                            int ra = s[idx - nc], rb = s[idx - w * nc], rc = s[idx - w * nc - nc];
                            switch (predictor)
                            {
                                case 2: pred = rb; break;
                                case 3: pred = rc; break;
                                case 4: pred = ra + rb - rc; break;
                                case 5: pred = ra + ((rb - rc) >> 1); break;
                                case 6: pred = rb + ((ra - rc) >> 1); break;
                                case 7: pred = (ra + rb) >> 1; break;
                                default: pred = ra; break;
                            }
                        }
                        int diff = (s[idx] - pred) & 0xFFFF;
                        if (diff >= 32768) diff -= 65536;
                        int cat = diff == 0 ? 0 : diff == -32768 ? 16 : (int)Math.Floor(Math.Log(Math.Abs(diff), 2)) + 1;
                        bw.Put(cat, 5);
                        if (cat > 0 && cat < 16) bw.Put(diff > 0 ? diff : diff + (1 << cat) - 1, cat);
                    }
                    mcu++;
                }
            }
            bw.Flush();
            o.AddRange(bw.Bytes);
            o.Add(0xFF); o.Add(0xD9);
            return o.ToArray();
        }

        [TestMethod]
        public void TestLosslessJpeg()
        {
            int w = 23, h = 11;
            var rnd = new Random(9);
            foreach (var cfg in new[] { new[] { 1, 12, 1 }, new[] { 2, 14, 1 }, new[] { 1, 16, 6 }, new[] { 3, 12, 7 }, new[] { 1, 15, 4 } })
            {
                int nc = cfg[0], precision = cfg[1], predictor = cfg[2];
                var s = new ushort[w * h * nc];
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                        for (int c = 0; c < nc; c++)
                            s[(y * w + x) * nc + c] = (ushort)Math.Min((1 << precision) - 1, 1000 + 30 * x + 17 * y + 400 * c + rnd.Next(60) + (x == 5 && y == 5 ? (1 << precision) - 2000 : 0));
                var data = EncodeLosslessJpeg(s, w, h, nc, precision, predictor, 0);
                var dec = LosslessJpeg.Decode(data);
                CheckTrue(string.Format("Lossless JPEG {0} component(s), {1}-bit, predictor {2}", nc, precision, predictor),
                    dec.Width == w && dec.Height == h && dec.Components == nc && dec.Precision == precision && dec.Samples.SequenceEqual(s));
            }
            // restart markers every row, as in tiled DNG files
            var r = new ushort[w * h];
            for (int i = 0; i < r.Length; i++) r[i] = (ushort)(2000 + 13 * (i % w) + 7 * (i / w) + rnd.Next(30));
            var withRestart = LosslessJpeg.Decode(EncodeLosslessJpeg(r, w, h, 1, 14, 1, w));
            int wrong = Enumerable.Range(0, r.Length).Count(i => withRestart.Samples[i] != r[i]);
            CheckTrue("Lossless JPEG with a restart interval per row", wrong == 0, wrong + " wrong samples");
            bool threw = false;
            try { LosslessJpeg.Decode(new byte[] { 1, 2, 3, 4 }); } catch (InvalidDataException) { threw = true; }
            CheckTrue("Not a JPEG stream", threw);
        }

        [TestMethod]
        public void TestSolarAndMosaic()
        {
            int w = 200, h = 200;
            double cx = 100, cy = 100, rad = 70;
            var sun = new AstroImage(w, h, 1);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    double r = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)) / rad;
                    // limb darkening I(μ) = 1 - 0.6 (1 - μ); faint prominences just outside the limb
                    sun.Data[0][y * w + x] = r < 1 ? (float)(0.8 * (1 - 0.6 * (1 - Math.Sqrt(1 - r * r)))) : (r < 1.1 ? 0.01f : 0f);
                }
            var flat = sun.Clone();
            SolarTools.FlattenLimbDarkening(flat, cx, cy, rad);
            Func<AstroImage, double, double> at = (im, rr) => im.Data[0][(int)cy * w + (int)(cx + rr * rad)];
            double before = at(sun, 0.9) / at(sun, 0), after = at(flat, 0.9) / at(flat, 0);
            CheckTrue("Limb darkening flattened", after > 0.9 && after > before, string.Format("edge/centre {0:0.00} → {1:0.00}", before, after));
            var prom = sun.Clone();
            SolarTools.EnhanceProminences(prom, cx, cy, rad);
            CheckTrue("Prominences brightened outside the disc", at(prom, 1.05) > 2 * at(sun, 1.05), at(prom, 1.05).ToString("0.000"));
            var occ = sun.Clone();
            SolarTools.OccultDisc(occ, cx, cy, rad, 0);
            CheckTrue("Disc occulted", at(occ, 0) == 0 && at(occ, 0.5) == 0 && at(occ, 1.05) == at(sun, 1.05));

            // a mosaic from two overlapping tiles with a rough layout
            var full = Synthetic.Render(300, 160, Synthetic.RandomStars(150, 300, 160, 12), Transform2D.Identity, 3, 0.1, 0.002, 4);
            Func<int, AstroImage> crop = x0 =>
            {
                var t = new AstroImage(180, 160, 1);
                for (int y = 0; y < 160; y++) for (int x = 0; x < 180; x++) t.Data[0][y * 180 + x] = full.Data[0][y * 300 + x0 + x];
                return t;
            };
            var tiles = new List<MosaicStitcher.Tile> { new MosaicStitcher.Tile { Image = crop(0), X = 0, Y = 0 }, new MosaicStitcher.Tile { Image = crop(120), X = 116, Y = 3 } };
            var stitched = MosaicStitcher.Stitch(tiles);
            CheckTrue("Mosaic refined to the true offset", Math.Abs(tiles[1].X - 120) < 0.6 && Math.Abs(tiles[1].Y) < 0.6, string.Format("{0:0.00}, {1:0.00}", tiles[1].X, tiles[1].Y));
            CheckTrue("Mosaic canvas size", Math.Abs(stitched.Width - 300) <= 1 && Math.Abs(stitched.Height - 160) <= 1, stitched.Width + "×" + stitched.Height);
            double err = 0;
            for (int y = 20; y < 140; y++) for (int x = 20; x < 280; x++) err = Math.Max(err, Math.Abs(stitched.Data[0][y * stitched.Width + x] - full.Data[0][y * 300 + x]));
            CheckTrue("Mosaic matches the original", err < 0.05, err.ToString("0.000"));
        }
    }
}
