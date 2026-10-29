using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Sidralune.Core.Mathematics;
using Sidralune.Imaging;
using Sidralune.Imaging.Analysis;
using Sidralune.Imaging.Planetary;
using Sidralune.Imaging.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Sidralune.Tests.Checks;

namespace Sidralune.Tests
{
    /// <summary>Debayering, image files, background extraction, colour, filters, wavelets, PixelMath, star tools and stretches.</summary>
    [TestClass]
    public class ImagingProcessingTests : TestBase
    {
        private static string Temp(string name)
        {
            string dir = Path.Combine(Path.GetTempPath(), "SidraluneTests", "imaging");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, name);
        }

        /// <summary>A smooth colour image: gentle gradients so interpolation can recover it.</summary>
        private static AstroImage Gradient(int w, int h)
        {
            var img = new AstroImage(w, h, 3);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    img.Data[0][i] = (float)(0.2 + 0.5 * x / w);
                    img.Data[1][i] = (float)(0.3 + 0.4 * y / h);
                    img.Data[2][i] = (float)(0.6 - 0.3 * (x + y) / (w + h));
                }
            return img;
        }

        private static AstroImage Mosaic(AstroImage rgb, BayerPattern p)
        {
            var cfa = new AstroImage(rgb.Width, rgb.Height, 1) { Bayer = p };
            for (int y = 0; y < rgb.Height; y++)
                for (int x = 0; x < rgb.Width; x++)
                    cfa.Data[0][y * rgb.Width + x] = rgb.Data[Debayer.ColorAt(p, x, y)][y * rgb.Width + x];
            return cfa;
        }

        private static double MaxDiff(float[] a, float[] b, int w, int h, int margin)
        {
            double m = 0;
            for (int y = margin; y < h - margin; y++)
                for (int x = margin; x < w - margin; x++) m = Math.Max(m, Math.Abs(a[y * w + x] - b[y * w + x]));
            return m;
        }

        private static double Std(float[] d)
        {
            double mean = d.Average(v => (double)v);
            return Math.Sqrt(d.Sum(v => (v - mean) * (v - mean)) / d.Length);
        }

        private static float[] Noise(int n, double sigma, int seed, double level = 0.5)
        {
            var r = new Random(seed);
            var d = new float[n];
            for (int i = 0; i < n; i++) d[i] = (float)(level + sigma * Stats.NormalQuantile(Math.Max(1e-9, Math.Min(1 - 1e-9, r.NextDouble()))));
            return d;
        }

        [TestMethod]
        public void TestStoppedProcessing()
        {
            // each long step checks for a stop; with one already made it ends at once
            int w = 128, h = 96;
            var img = Synthetic.Render(w, h, Synthetic.RandomStars(30, w, h, 6), Transform2D.Identity, 3, 0.1, 0.002, 6);
            using (var cts = new CancellationTokenSource())
            {
                cts.Cancel();
                var ct = cts.Token;
                Func<Action, bool> stops = a => { try { a(); return false; } catch (OperationCanceledException) { return true; } };
                CheckTrue("Non-local means stops", stops(() => Enhance.NonLocalMeans(img.Data[0], w, h, 1.5, 5, 2, ct)));
                CheckTrue("Star removal stops", stops(() => StarTools.RemoveStars(img.Clone(), 1.8, 200, ct)));
                var geo = new DiscGeometry { Cx = 64, Cy = 48, Radius = 30, Flattening = 1 };
                var now = DateTime.UtcNow;
                CheckTrue("Derotation stops", stops(() => SphereDerotation.DerotateAndStack(new[] { img, img }, new[] { geo, geo }, new[] { now, now }, now, 9.9, ct)));
                var tiles = new List<MosaicStitcher.Tile> { new MosaicStitcher.Tile { Image = img, X = 0, Y = 0 }, new MosaicStitcher.Tile { Image = img, X = 100, Y = 0 } };
                CheckTrue("Mosaic stitching stops", stops(() => MosaicStitcher.Stitch(tiles, true, ct)));
            }
            CheckTrue("Without a stop they still run", Enhance.NonLocalMeans(img.Data[0], w, h, 1.5, 3, 1).Length == w * h);
        }

        [TestMethod]
        public void TestAlignChannels()
        {
            // red and blue shifted against green, as atmospheric dispersion does
            int w = 256, h = 256;
            var green = Synthetic.Render(w, h, Synthetic.RandomStars(60, w, h, 5), Transform2D.Identity, 3, 0.1, 0.001, 5);
            var img = new AstroImage(w, h, new[] { Geometry.Shift(green, 2, 1).Data[0], green.Data[0], Geometry.Shift(green, -1.5, -0.5).Data[0] });
            var shifts = ColorOps.AlignChannels(img.Clone());
            CheckTrue("The red and blue shifts are found", Math.Abs(Math.Abs(shifts[0][0]) - 2) < 0.3 && Math.Abs(Math.Abs(shifts[0][1]) - 1) < 0.3
                && Math.Abs(Math.Abs(shifts[2][0]) - 1.5) < 0.3 && Math.Abs(Math.Abs(shifts[2][1]) - 0.5) < 0.3, string.Join(" / ", shifts.Select(s => s[0].ToString("0.00") + "," + s[1].ToString("0.00"))));
            using (var cts = new System.Threading.CancellationTokenSource())
            {
                cts.Cancel();
                var copy = img.Clone();
                bool stopped = false;
                try { ColorOps.AlignChannels(copy, cts.Token); } catch (OperationCanceledException) { stopped = true; }
                CheckTrue("A stopped alignment ends without shifting anything", stopped && copy.Data[0].SequenceEqual(img.Data[0]) && copy.Data[2].SequenceEqual(img.Data[2]));
            }
        }

        [TestMethod]
        public void TestDebayer()
        {
            CheckTrue("RGGB layout", Debayer.ColorAt(BayerPattern.RGGB, 0, 0) == 0 && Debayer.ColorAt(BayerPattern.RGGB, 1, 0) == 1 && Debayer.ColorAt(BayerPattern.RGGB, 1, 1) == 2);
            CheckTrue("BGGR layout", Debayer.ColorAt(BayerPattern.BGGR, 0, 0) == 2 && Debayer.ColorAt(BayerPattern.BGGR, 1, 1) == 0);
            var all = new[] { BayerPattern.RGGB, BayerPattern.BGGR, BayerPattern.GRBG, BayerPattern.GBRG };
            CheckTrue("Flips are their own inverse", all.All(p => Debayer.FlipHorizontal(Debayer.FlipHorizontal(p)) == p && Debayer.FlipVertical(Debayer.FlipVertical(p)) == p));
            bool shiftOk = all.All(p => Enumerable.Range(0, 4).All(k =>
            {
                int dx = k & 1, dy = k >> 1;
                var s = Debayer.Shift(p, dx, dy);
                return Debayer.ColorAt(s, 0, 0) == Debayer.ColorAt(p, dx, dy) && Debayer.ColorAt(s, 1, 1) == Debayer.ColorAt(p, dx + 1, dy + 1);
            }));
            CheckTrue("Shifted pattern matches the cropped sensor", shiftOk);

            var truth = Gradient(64, 48);
            foreach (var p in all)
            {
                var cfa = Mosaic(truth, p);
                foreach (var m in new[] { DebayerMethod.Bilinear, DebayerMethod.MalvarHeCutler, DebayerMethod.EdgeDirected })
                {
                    var rgb = Debayer.Apply(cfa, m);
                    double worst = Enumerable.Range(0, 3).Max(c => MaxDiff(rgb.Data[c], truth.Data[c], 64, 48, 3));
                    CheckTrue(string.Format("{0} {1} recovers smooth colour", m, p), rgb.Channels == 3 && rgb.Width == 64 && worst < 0.02, worst.ToString("0.0000"));
                }
                var sp = Debayer.Apply(cfa, DebayerMethod.SuperPixel);
                CheckTrue("Super pixel " + p + " halves the size", sp.Width == 32 && sp.Height == 24 && sp.Channels == 3);
                Check("Super pixel " + p + " red", sp.Data[0][10 * 32 + 10], (truth.Data[0][20 * 64 + 20] + truth.Data[0][21 * 64 + 21]) / 2, 0.02);
            }
            var mono = new AstroImage(8, 8, 1);
            CheckTrue("No pattern: unchanged", Debayer.Apply(mono, DebayerMethod.Bilinear) == mono);
            var planes = Debayer.SplitCfa(Mosaic(truth, BayerPattern.RGGB), BayerPattern.RGGB);
            CheckTrue("CFA split into four quarter planes", planes.Length == 4 && planes.All(q => q.Length == 32 * 24) && planes[0][0] == truth.Data[0][0] && planes[3][0] == truth.Data[2][65]);
        }

        [TestMethod]
        public void TestImageFiles()
        {
            var mono = new AstroImage(37, 21, 1);
            for (int i = 0; i < mono.PixelCount; i++) mono.Data[0][i] = (i % 97) / 96f;
            mono.Header.Set("OBJECT", "M 42");
            mono.Header.Set("EXPTIME", 120.0);
            var color = Gradient(33, 17);
            var cases = new[]
            {
                Tuple.Create(ImageSaveFormat.Fits32Float, ".fits", 1e-7), Tuple.Create(ImageSaveFormat.Fits16, ".fits", 1.0 / 65535 * 0.6),
                Tuple.Create(ImageSaveFormat.Xisf32Float, ".xisf", 1e-7), Tuple.Create(ImageSaveFormat.Xisf16, ".xisf", 1.0 / 65535 * 0.6),
                Tuple.Create(ImageSaveFormat.Tiff32Float, ".tif", 1e-7), Tuple.Create(ImageSaveFormat.Tiff16, ".tif", 1.0 / 65535 * 0.6),
                Tuple.Create(ImageSaveFormat.Png8, ".png", 1.0 / 255 * 0.6), Tuple.Create(ImageSaveFormat.Jpeg8, ".jpg", 0.06)
            };
            foreach (var c in cases)
                foreach (var img in new[] { mono, color })
                {
                    string path = Temp("roundtrip-" + c.Item1 + "-" + img.Channels + c.Item2);
                    ImageFile.Save(path, img, c.Item1);
                    var back = ImageFile.Load(path);
                    bool sameShape = back != null && back.Width == img.Width && back.Height == img.Height && (back.Channels == img.Channels || (c.Item1 == ImageSaveFormat.Jpeg8 || c.Item1 == ImageSaveFormat.Png8) && img.Channels == 1);
                    double worst = 0;
                    if (sameShape)
                        for (int ch = 0; ch < img.Channels; ch++)
                            for (int i = 0; i < img.PixelCount; i++) worst = Math.Max(worst, Math.Abs(back.Data[Math.Min(ch, back.Channels - 1)][i] - img.Data[ch][i]));
                    CheckTrue(string.Format("{0} {1} round trip", c.Item1, img.Channels == 1 ? "mono" : "colour"), sameShape && worst <= c.Item3, sameShape ? "max error " + worst.ToString("0.0000000") : "shape changed");
                }
            var fits = ImageFile.Load(Temp("roundtrip-Fits32Float-1.fits"));
            CheckTrue("FITS keeps header cards", fits.ObjectName == "M 42" && Math.Abs(fits.ExposureSeconds - 120) < 1e-9);
            CheckTrue("Format from extension", ImageFile.FormatFromExtension("a.xisf") == ImageSaveFormat.Xisf32Float && ImageFile.FormatFromExtension("a.PNG") == ImageSaveFormat.Png8 && ImageFile.FormatFromExtension("b.jpeg") == ImageSaveFormat.Jpeg8);
            CheckTrue("Supported files", ImageFile.IsSupported("x.FIT") && ImageFile.IsSupported("y.tiff") && ImageFile.IsSupported("z.cr2") && !ImageFile.IsSupported("notes.txt"));
            var bmp = BitmapIO.ToBitmap(color);
            var fromBmp = BitmapIO.FromBitmap(bmp);
            CheckTrue("Bitmap round trip", fromBmp.Width == 33 && fromBmp.Channels == 3 && Math.Abs(fromBmp.Data[2][100] - color.Data[2][100]) < 1.0 / 255 + 1e-6);
            bmp.Dispose();
        }

        [TestMethod]
        public void TestImageModel()
        {
            var img = Gradient(10, 8);
            var c = img.Clone();
            c.Data[0][0] = 9;
            CheckTrue("Clone is a deep copy", img.Data[0][0] != 9);
            var lum = img.Luminance();
            CheckTrue("Luminance between the channels", lum.Length == 80 && lum[5] >= Math.Min(img.Data[0][5], Math.Min(img.Data[1][5], img.Data[2][5])) && lum[5] <= Math.Max(img.Data[0][5], Math.Max(img.Data[1][5], img.Data[2][5])));
            var rgb = AstroImage.Combine(img.ExtractChannel(0), img.ExtractChannel(1), img.ExtractChannel(2));
            CheckTrue("Split and combine channels", rgb.Channels == 3 && rgb.Data[1][33] == img.Data[1][33]);
            Check("Bilinear sample between pixels", img.Sample(0, 2.5, 3), (img.Data[0][3 * 10 + 2] + img.Data[0][3 * 10 + 3]) / 2, 1e-6);
            var clipped = new AstroImage(2, 1, new[] { new[] { -0.5f, float.NaN } });
            clipped.Clip();
            CheckTrue("Clip removes negatives and NaN", clipped.Data[0][0] == 0 && clipped.Data[0][1] == 0);
            var h = new FitsHeader();
            h.Set("DATE-OBS", "2026-10-24T21:15:30.5");
            h.Set("CCD-TEMP", -10.0);
            h.Set("FILTER", "Ha");
            h.AddHistory("step one");
            var withHeader = new AstroImage(1, 1, 1) { Header = h };
            CheckTrue("Header helpers", withHeader.DateObs == new DateTime(2026, 10, 24, 21, 15, 30, 500) && withHeader.Temperature == -10 && withHeader.Filter == "Ha" && h.Cards.Any(k => k.Key == "HISTORY"));
            h.Set("filter", "OIII");
            CheckTrue("Keys are case-insensitive and replaced", h.GetString("FILTER") == "OIII" && h.Cards.Count(k => string.Equals(k.Key, "FILTER", StringComparison.OrdinalIgnoreCase)) == 1);
            h.Remove("Filter");
            CheckTrue("Remove a key", !h.Has("FILTER"));
        }

        [TestMethod]
        public void TestImageStatistics()
        {
            int w = 200, h = 150;
            var d = Noise(w * h, 0.01, 5, 0.2);
            var s = ImageStatistics.Compute(d, w);
            Check("Mean", s.Mean, 0.2, 0.001);
            Check("Median", s.Median, 0.2, 0.001);
            Check("Standard deviation", s.StdDev, 0.01, 0.0005);
            Check("MAD × 1.4826", s.Mad * Stats.MadToSigma, 0.01, 0.0005);
            Check("Noise estimate", s.Noise, 0.01, 0.002);
            Check("Multiresolution noise", ImageStatistics.MrsNoise(d, w, h), 0.01, 0.002);
            CheckTrue("Histogram covers every pixel", s.Histogram != null && s.Histogram.Sum() == w * h);
            var sat = (float[])d.Clone();
            for (int i = 0; i < 50; i++) sat[i] = 1;
            CheckTrue("Saturated pixels counted", ImageStatistics.Compute(sat, w).Saturated == 50);

            // background mesh follows a gradient under stars
            var grad = new float[w * h];
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) grad[y * w + x] = (float)(0.1 + 0.2 * x / w) + d[y * w + x] - 0.2f;
            for (int k = 0; k < 40; k++) grad[(k * 37 % h) * w + k * 53 % w] = 1;
            float[] rms;
            var bg = BackgroundMesh.Estimate(grad, w, h, 32, out rms);
            Check("Background mesh left edge", bg[75 * w + 10], 0.1 + 0.2 * 10 / w, 0.01);
            Check("Background mesh right edge", bg[75 * w + 190], 0.1 + 0.2 * 190.0 / w, 0.01);
            // the rms of a cell includes the gradient across it (0.033 over a 33-pixel cell): √(0.01² + 0.033²/12)
            Check("Background rms", rms[75 * w + 100], Math.Sqrt(1e-4 + 0.033 * 0.033 / 12), 0.002);
        }

        [TestMethod]
        public void TestBackgroundExtraction()
        {
            var xs = new List<double>(); var ys = new List<double>(); var vs = new List<double>();
            for (int i = 0; i < 40; i++)
            {
                double x = (i * 37) % 100, y = (i * 61) % 80;
                xs.Add(x); ys.Add(y); vs.Add(0.1 + 0.002 * x - 0.001 * y + 1e-5 * x * y + 2e-5 * x * x);
            }
            var c = BackgroundExtraction.FitPoly2D(xs, ys, vs, 2);
            Check("Degree-2 polynomial fitted exactly", BackgroundExtraction.EvalPoly2D(c, 55, 33, 2), 0.1 + 0.11 - 0.033 + 1e-5 * 55 * 33 + 2e-5 * 55 * 55, 1e-9);
            var tps = new ThinPlateSpline(xs, ys, vs, 0);
            Check("Thin-plate spline interpolates its samples", tps.Evaluate(xs[7], ys[7]), vs[7], 1e-6);

            // a gradient with stars: after extraction the background is flat
            int w = 240, h = 160;
            var img = Synthetic.Render(w, h, Synthetic.RandomStars(80, w, h, 4), Transform2D.Identity, 3, 0.1, 0.002, 9);
            var d = img.Data[0];
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) d[y * w + x] += (float)(0.15 * x / w + 0.08 * y / h);
            foreach (var model in new[] { BackgroundModel.Polynomial, BackgroundModel.RadialBasis })
            {
                var work = img.Clone();
                var opt = new BackgroundOptions { Model = model, GridSize = 10, SampleRadius = 6 };
                BackgroundExtraction.Apply(work, opt);
                var wd = work.Data[0];
                Func<int, int, double> patch = (cx, cy) => { var l = new List<double>(); for (int y = cy - 8; y < cy + 8; y++) for (int x = cx - 8; x < cx + 8; x++) l.Add(wd[y * w + x]); return Stats.Median(l); };
                double spread = new[] { patch(20, 20), patch(220, 20), patch(20, 140), patch(220, 140), patch(120, 80) }.Max() - new[] { patch(20, 20), patch(220, 20), patch(20, 140), patch(220, 140), patch(120, 80) }.Min();
                CheckTrue(model + " background flattened", spread < 0.01, "corner spread " + spread.ToString("0.0000") + " (was 0.23)");
            }
            var div = img.Clone();
            BackgroundExtraction.Apply(div, new BackgroundOptions { Correction = BackgroundCorrection.Divide, GridSize = 10, SampleRadius = 6 });
            CheckTrue("Division keeps the image positive", div.Data[0].All(v => v > 0));
        }

        [TestMethod]
        public void TestColour()
        {
            double hh, ss, ll, r, g, b;
            foreach (var rgb in new[] { new[] { 0.8, 0.3, 0.1 }, new[] { 0.2, 0.7, 0.9 }, new[] { 0.5, 0.5, 0.5 }, new[] { 1.0, 0.0, 1.0 } })
            {
                ColorOps.RgbToHsl(rgb[0], rgb[1], rgb[2], out hh, out ss, out ll);
                ColorOps.HslToRgb(hh, ss, ll, out r, out g, out b);
                CheckTrue(string.Format("HSL round trip ({0}, {1}, {2})", rgb[0], rgb[1], rgb[2]), Math.Abs(r - rgb[0]) < 1e-9 && Math.Abs(g - rgb[1]) < 1e-9 && Math.Abs(b - rgb[2]) < 1e-9);
            }
            ColorOps.RgbToHsl(1, 0, 0, out hh, out ss, out ll);
            CheckTrue("Pure red: hue 0, full saturation, lightness 0.5", Math.Abs(hh) < 1e-9 && Math.Abs(ss - 1) < 1e-9 && Math.Abs(ll - 0.5) < 1e-9);

            // a colour cast in the background is removed
            int w = 100, h = 80;
            var img = new AstroImage(w, h, 3);
            var nr = Noise(w * h, 0.003, 1, 0.12); var ng = Noise(w * h, 0.003, 2, 0.08); var nb = Noise(w * h, 0.003, 3, 0.05);
            img.Data[0] = nr; img.Data[1] = ng; img.Data[2] = nb;
            ColorOps.BackgroundNeutralization(img);
            double mr = Stats.Median(img.Data[0].Select(v => (double)v).ToList()), mg = Stats.Median(img.Data[1].Select(v => (double)v).ToList()), mb = Stats.Median(img.Data[2].Select(v => (double)v).ToList());
            CheckTrue("Background neutralised", Math.Abs(mr - mg) < 0.002 && Math.Abs(mb - mg) < 0.002, string.Format("{0:0.0000} {1:0.0000} {2:0.0000}", mr, mg, mb));

            var green = new AstroImage(1, 1, new[] { new[] { 0.2f }, new[] { 0.9f }, new[] { 0.4f } });
            ColorOps.Scnr(green);
            Check("SCNR average neutral caps green at (R+B)/2", green.Data[1][0], 0.3, 1e-6);
            var green2 = new AstroImage(1, 1, new[] { new[] { 0.2f }, new[] { 0.9f }, new[] { 0.4f } });
            ColorOps.Scnr(green2, ScnrMethod.MaximumNeutral, 0.5);
            Check("SCNR maximum neutral at half strength", green2.Data[1][0], 0.65, 1e-6);
            var notGreen = new AstroImage(1, 1, new[] { new[] { 0.5f }, new[] { 0.2f }, new[] { 0.5f } });
            ColorOps.Scnr(notGreen);
            Check("SCNR leaves a non-green pixel alone", notGreen.Data[1][0], 0.2, 1e-7);

            var grey = new AstroImage(1, 1, new[] { new[] { 0.3f }, new[] { 0.6f }, new[] { 0.9f } });
            ColorOps.Saturation(grey, 0);
            CheckTrue("Zero saturation makes grey", Math.Abs(grey.Data[0][0] - grey.Data[2][0]) < 1e-5 && Math.Abs(grey.Data[1][0] - grey.Data[0][0]) < 1e-5, string.Format("{0:0.000} {1:0.000} {2:0.000}", grey.Data[0][0], grey.Data[1][0], grey.Data[2][0]));

            var ha = new AstroImage(1, 1, new[] { new[] { 0.7f } }); var o3 = new AstroImage(1, 1, new[] { new[] { 0.2f } }); var s2 = new AstroImage(1, 1, new[] { new[] { 0.4f } });
            var sho = ColorOps.NarrowbandPalette(ha, o3, s2, "SHO");
            CheckTrue("SHO palette: S→R, H→G, O→B", sho.Data[0][0] == 0.4f && sho.Data[1][0] == 0.7f && sho.Data[2][0] == 0.2f);
            var hoo = ColorOps.NarrowbandPalette(ha, o3, null, "hoo");
            CheckTrue("HOO palette without SII", hoo.Data[0][0] == 0.7f && hoo.Data[1][0] == 0.2f && hoo.Data[2][0] == 0.2f);
            var inv = new AstroImage(1, 1, new[] { new[] { 0.25f } });
            ColorOps.Invert(inv);
            Check("Invert", inv.Data[0][0], 0.75, 1e-7);
            var colourised = ColorOps.Colorize(new AstroImage(1, 1, new[] { new[] { 1f } }), 1, 0.5, 0.25);
            CheckTrue("Colourise a white pixel with a tint", colourised.Channels == 3 && colourised.Data[0][0] > colourised.Data[2][0]);

            var lum = new AstroImage(2, 1, new[] { new[] { 0.2f, 0.8f } });
            var col = new AstroImage(2, 1, new[] { new[] { 0.5f, 0.5f }, new[] { 0.25f, 0.25f }, new[] { 0.1f, 0.1f } });
            var lrgb = ColorOps.LrgbCombine(lum, col);
            CheckTrue("LRGB: brighter luminance gives a brighter pixel, colour kept", lrgb.Data[0][1] > lrgb.Data[0][0] && lrgb.Data[0][1] > lrgb.Data[1][1] && lrgb.Data[1][1] > lrgb.Data[2][1]);
            var wb = new AstroImage(1, 1, new[] { new[] { 0.4f }, new[] { 0.4f }, new[] { 0.4f } });
            ColorOps.WhiteBalance(wb, 2, 1, 0.5, false);
            CheckTrue("White balance multiplies channels", Math.Abs(wb.Data[0][0] - 0.8) < 1e-6 && Math.Abs(wb.Data[2][0] - 0.2) < 1e-6);
        }

        [TestMethod]
        public void TestFilters()
        {
            var k = Filters.GaussianKernel(2);
            Check("Gaussian kernel sums to 1", k.Sum(), 1, 1e-6);
            CheckTrue("Gaussian kernel symmetric and peaked", k.Length % 2 == 1 && k[0] == k[k.Length - 1] && k[k.Length / 2] == k.Max());
            CheckTrue("Mirror indices", Filters.Mirror(-1, 10) == 1 && Filters.Mirror(10, 10) == 8 && Filters.Mirror(4, 10) == 4);
            int w = 64, h = 48;
            var flat = Enumerable.Repeat(0.3f, w * h).ToArray();
            CheckTrue("Blurs keep a flat image flat", Filters.GaussianBlur(flat, w, h, 3).All(v => Math.Abs(v - 0.3f) < 1e-5) && Filters.BoxBlur(flat, w, h, 2).All(v => Math.Abs(v - 0.3f) < 1e-5));
            CheckTrue("Unsharp mask keeps a flat image flat", Filters.UnsharpMask(flat, w, h, 2, 1).All(v => Math.Abs(v - 0.3f) < 1e-5));
            var impulse = new float[w * h];
            impulse[20 * w + 30] = 1;
            Check("Gaussian blur preserves flux", Filters.GaussianBlur(impulse, w, h, 2).Sum(), 1, 1e-4);
            CheckTrue("Median removes an isolated hot pixel", Filters.Median(impulse, w, h, 1).All(v => v == 0));
            var dil = Filters.Morphology(impulse, w, h, 2, true);
            CheckTrue("Dilation grows a point to a 5×5 square", dil.Count(v => v == 1) == 25 && dil[18 * w + 28] == 1 && dil[17 * w + 30] == 0);
            CheckTrue("Opening removes a small bright spot", Filters.Opening(impulse, w, h, 1).All(v => v == 0));
            var hole = Enumerable.Repeat(1f, w * h).ToArray(); hole[10 * w + 10] = 0;
            CheckTrue("Closing fills a small dark hole", Filters.Closing(hole, w, h, 1)[10 * w + 10] == 1);

            // FFT convolution agrees with direct separable convolution
            var img = Noise(w * h, 0.1, 3);
            var kk = Filters.GaussianKernel(1.5);
            var direct = Filters.ConvolveSeparable(img, w, h, kk, kk);
            var k2 = new float[kk.Length * kk.Length];
            for (int y = 0; y < kk.Length; y++) for (int x = 0; x < kk.Length; x++) k2[y * kk.Length + x] = kk[x] * kk[y];
            var viaFft = Filters.ConvolveFft(img, w, h, k2, kk.Length, kk.Length);
            Check("FFT and direct convolution agree away from the edges", MaxDiff(direct, viaFft, w, h, kk.Length), 0, 1e-4);

            var sharp = Synthetic.Render(w, h, Synthetic.RandomStars(20, w, h, 2), Transform2D.Identity, 2, 0.1, 0.001, 3).Data[0];
            CheckTrue("Laplacian variance: sharp beats blurred", Filters.LaplacianVariance(sharp, w, h) > 2 * Filters.LaplacianVariance(Filters.GaussianBlur(sharp, w, h, 2), w, h));
            CheckTrue("Gradient energy: sharp beats blurred", Filters.GradientEnergy(sharp, w, h, 0, 0, w, h) > Filters.GradientEnergy(Filters.GaussianBlur(sharp, w, h, 2), w, h, 0, 0, w, h));

            // wavelets: decomposition is exact, sharpening without gain is the identity, denoise lowers noise
            var layers = Wavelets.Decompose(sharp, w, h, 4);
            CheckTrue("Wavelet layers", layers.Length == 5);
            Check("Wavelet reconstruction is exact", MaxDiff(Wavelets.Reconstruct(layers), sharp, w, h, 0), 0, 1e-5);
            Check("Wavelet sharpen with no gain is the identity", MaxDiff(Wavelets.Sharpen(sharp, w, h, new double[4], null, 0), sharp, w, h, 0), 0, 1e-5);
            CheckTrue("Wavelet sharpen raises small-scale contrast", Filters.LaplacianVariance(Wavelets.Sharpen(sharp, w, h, new[] { 1.0, 0.5, 0, 0 }, null, 0), w, h) > Filters.LaplacianVariance(sharp, w, h));
            var noisy = Noise(w * h, 0.05, 8);
            CheckTrue("Wavelet denoise lowers noise", Std(Wavelets.Denoise(noisy, w, h, new[] { 1.0, 0.8 })) < 0.7 * Std(noisy));
        }

        [TestMethod]
        public void TestPixelMath()
        {
            var t = new AstroImage(4, 3, new[] { Enumerable.Range(0, 12).Select(i => i / 12f).ToArray() });
            Func<string, AstroImage> eval = e => new PixelMath(e).Evaluate(t, new Dictionary<string, AstroImage>());
            Check("Precedence and power", eval("1 + 2 * 3 ^ 2").Data[0][0], 19, 1e-6);
            Check("Parentheses and unary minus", eval("-(2 - 5) / 2").Data[0][0], 1.5, 1e-6);
            Check("Target image", eval("$T * 2").Data[0][5], 10 / 12.0, 1e-6);
            var pos = eval("$x + 10 * $y");
            CheckTrue("Pixel coordinates", pos.Data[0][0] == 0 && pos.Data[0][6] == 12);
            Check("iif and comparison", eval("iif($x >= 2 && $y == 1, 1, 0)").Data[0][6], 1, 0);
            Check("iif false branch", eval("iif($x >= 2 && $y == 1, 1, 0)").Data[0][5], 0, 0);
            Check("Functions", eval("max(1, 4, 2) + min(3, -1) + abs(-2) + sqrt(16) + clamp(5, 0, 1) + avg(2, 4)").Data[0][0], 4 - 1 + 2 + 4 + 1 + 3, 1e-6);
            Check("ln, log, exp, pow", eval("ln(exp(2)) + log(1000) + pow(2, 5)").Data[0][0], 2 + 3 + 32, 1e-5);
            Check("mtf in PixelMath", eval("mtf(0.25, 0.25)").Data[0][0], 0.5, 1e-6);
            Check("Constants", eval("pi + e").Data[0][0], Math.PI + Math.E, 1e-5);

            var ha = new AstroImage(4, 3, new[] { Enumerable.Repeat(0.6f, 12).ToArray() });
            var o3 = new AstroImage(4, 3, new[] { Enumerable.Repeat(0.2f, 12).ToArray() });
            var pm = new PixelMath("0.7 * Ha + 0.3 * OIII");
            CheckTrue("Referenced images", pm.ReferencedImages.Contains("Ha") && pm.ReferencedImages.Contains("OIII"));
            Check("Named images", pm.Evaluate(t, new Dictionary<string, AstroImage> { { "Ha", ha }, { "OIII", o3 } }).Data[0][3], 0.48, 1e-6);
            var rgb = new AstroImage(2, 1, 3);
            var chan = new PixelMath("$c").Evaluate(rgb, new Dictionary<string, AstroImage>());
            CheckTrue("Channel variable", chan.Channels == 3 && chan.Data[2][1] == 2);

            foreach (var bad in new[] { "1 +", "(1 + 2", "2 * * 3", "1 2" })
            {
                bool threw = false;
                try { new PixelMath(bad); } catch (FormatException) { threw = true; }
                CheckTrue("Syntax error reported: " + bad, threw);
            }
            bool missing = false;
            try { new PixelMath("Ha + 1").Evaluate(t, new Dictionary<string, AstroImage>()); } catch (Exception ex) { missing = ex is KeyNotFoundException || ex is ArgumentException || ex is InvalidOperationException; }
            CheckTrue("Missing image reported", missing);
        }

        [TestMethod]
        public void TestStarTools()
        {
            int w = 160, h = 120;
            var stars = new List<Synthetic.SynthStar>
            {
                new Synthetic.SynthStar { X = 40.3, Y = 40.6, Flux = 0.3 }, new Synthetic.SynthStar { X = 70.5, Y = 90.2, Flux = 0.1 },
                new Synthetic.SynthStar { X = 30.1, Y = 95.7, Flux = 0.05 }
            };
            var img = Synthetic.Render(w, h, stars, Transform2D.Identity, 3, 0.1, 0.002, 7);
            var mask = StarTools.StarMask(img);
            var bright = stars[0];
            int bi = (int)Math.Round(bright.Y) * w + (int)Math.Round(bright.X);
            CheckTrue("Star mask covers each star", stars.All(s => mask[(int)Math.Round(s.Y) * w + (int)Math.Round(s.X)] > 0.9), string.Join(", ", stars.Select(s => mask[(int)Math.Round(s.Y) * w + (int)Math.Round(s.X)].ToString("0.00"))));
            Check("Star mask is empty far from the stars", mask[15 * w + 140], 0, 0);
            double peak = img.Data[0][bi];
            var starless = StarTools.RemoveStars(img.Clone());
            CheckTrue("Star removal flattens a star to the background", starless.Data[0][bi] < 0.1 + 0.3 * (peak - 0.1), string.Format("{0:0.000} → {1:0.000}", peak, starless.Data[0][bi]));
            var reduced = img.Clone();
            StarTools.ReduceStars(reduced, 0.6, 2, mask);
            CheckTrue("Star reduction dims a star", reduced.Data[0][bi] < peak);
            var a = new AstroImage(1, 1, new[] { new[] { 0.4f } }); var s2 = new AstroImage(1, 1, new[] { new[] { 0.5f } });
            StarTools.ScreenBlend(a, s2);
            Check("Screen blend 1-(1-a)(1-b)", a.Data[0][0], 0.7, 1e-6);
            var ramp = new AstroImage(10, 1, new[] { Enumerable.Range(0, 10).Select(i => i / 9f).ToArray() });
            var range = StarTools.RangeMask(ramp, 0.3, 0.7, 0, 0);
            CheckTrue("Range mask selects the middle", range[0] == 0 && range[5] == 1 && range[9] == 0);

            var noisy = new AstroImage(w, h, new[] { Noise(w * h, 0.05, 2) });
            CheckTrue("Bilateral filter keeps a flat image flat", Enhance.Bilateral(Enumerable.Repeat(0.4f, w * h).ToArray(), w, h, 3, 2, 0.1).All(v => Math.Abs(v - 0.4) < 1e-5));
            CheckTrue("Non-local means lowers noise", Std(Enhance.NonLocalMeans(noisy.Data[0], w, h, 1.0)) < 0.7 * Std(noisy.Data[0]));
            var banded = new AstroImage(w, h, new[] { Noise(w * h, 0.002, 4, 0.2) });
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) banded.Data[0][y * w + x] += (y % 4 == 0) ? 0.02f : 0;
            Enhance.BandingReduction(banded);
            var rowMeans = Enumerable.Range(0, h).Select(y => Enumerable.Range(0, w).Average(x => banded.Data[0][y * w + x])).ToList();
            CheckTrue("Banding reduction evens out rows", rowMeans.Max() - rowMeans.Min() < 0.004, (rowMeans.Max() - rowMeans.Min()).ToString("0.0000") + " (was 0.02)");
            var hdr = img.Clone();
            Enhance.HdrMultiscale(hdr);
            CheckTrue("HDR multiscale keeps values finite", hdr.Data[0].All(v => !float.IsNaN(v) && !float.IsInfinity(v)));
        }

        [TestMethod]
        public void TestStretches()
        {
            Check("MTF(m, m) = 0.5", HistogramTransform.Mtf(0.2, 0.2), 0.5, 1e-12);
            CheckTrue("MTF end points", HistogramTransform.Mtf(0.2, 0) == 0 && HistogramTransform.Mtf(0.2, 1) == 1 && HistogramTransform.Mtf(0.5, 0.3) == 0.3);
            var ht = new HistogramTransform { Shadows = 0.1, Midtones = 0.5, Highlights = 0.9 };
            Check("Shadows and highlights rescale", ht.Apply(0.5), 0.5, 1e-12);
            CheckTrue("Clipped below and above", ht.Apply(0.05) == 0 && ht.Apply(0.95) == 1);
            var lut = ht.Lut(256);
            CheckTrue("Lookup table monotonic", lut.Length == 256 && Enumerable.Range(1, 255).All(i => lut[i] >= lut[i - 1]));

            // auto stretch puts the background at 0.25
            int w = 120, h = 90;
            var img = new AstroImage(w, h, new[] { Noise(w * h, 0.002, 9, 0.05) });
            var t = StretchOps.AutoStretch(img);
            StretchOps.ApplyHistogram(img, t);
            Check("Auto stretch background", Stats.Median(img.Data[0].Select(v => (double)v).ToList()), 0.25, 0.02);

            var lv = new AstroImage(3, 1, new[] { new[] { 0.1f, 0.3f, 0.6f } });
            StretchOps.Levels(lv, 0.1, 1, 0.5);
            CheckTrue("Levels black and white point", lv.Data[0][0] < 1e-6 && Math.Abs(lv.Data[0][1] - 0.5) < 1e-6 && lv.Data[0][2] == 1);
            var gam = new AstroImage(1, 1, new[] { new[] { 0.25f } });
            StretchOps.Levels(gam, 0, 2, 1);
            Check("Levels gamma 2 brightens (x^(1/γ))", gam.Data[0][0], 0.5, 1e-6);
            var curve = StretchOps.Curve(new List<double[]> { new[] { 0.25, 0.5 } });
            CheckTrue("Curve passes through its points", Math.Abs(curve(0.25) - 0.5) < 1e-9 && curve(0) == 0 && curve(1) == 1);
            CheckTrue("Curve is monotonic", Enumerable.Range(1, 100).All(i => curve(i / 100.0) >= curve((i - 1) / 100.0)));
            var ghs = StretchOps.Ghs(5, 0, 0.1);
            CheckTrue("Generalised hyperbolic stretch: fixed ends, monotonic", Math.Abs(ghs(0)) < 1e-9 && Math.Abs(ghs(1) - 1) < 1e-9 && Enumerable.Range(1, 100).All(i => ghs(i / 100.0) >= ghs((i - 1) / 100.0)));
            CheckTrue("GHS raises contrast around the symmetry point", ghs(0.12) - ghs(0.08) > 0.04 * 3, (ghs(0.12) - ghs(0.08)).ToString("0.000"));
            CheckTrue("GHS with no stretch is the identity", Math.Abs(StretchOps.Ghs(0, 0, 0.1)(0.37) - 0.37) < 1e-12);
            foreach (double bb in new[] { -1.0, -0.5, 1, 3 })
            {
                var f = StretchOps.Ghs(4, bb, 0.2);
                CheckTrue("GHS b = " + bb + " monotonic with fixed ends", Math.Abs(f(0)) < 1e-6 && Math.Abs(f(1) - 1) < 1e-6 && Enumerable.Range(1, 200).All(i => f(i / 200.0) >= f((i - 1) / 200.0) - 1e-9));
            }
            var asinh = new AstroImage(1, 1, new[] { new[] { 0.01f }, new[] { 0.02f }, new[] { 0.03f } });
            StretchOps.ArcsinhStretch(asinh, 100);
            CheckTrue("Arcsinh brightens faint pixels and keeps colour ratios", asinh.Data[0][0] > 0.05 && Math.Abs(asinh.Data[2][0] / asinh.Data[0][0] - 3) < 1e-4);
            var cl = new AstroImage(w, h, new[] { Noise(w * h, 0.05, 1, 0.3) });
            StretchOps.Clahe(cl);
            CheckTrue("CLAHE stays within 0..1", cl.Data[0].All(v => v >= 0 && v <= 1));
            var fn = new AstroImage(2, 1, new[] { new[] { 0.25f, 0.5f } });
            StretchOps.ApplyFunction(fn, x => x * x);
            Check("Apply a function via lookup table", fn.Data[0][1], 0.25, 1e-4);
        }
    }
}
