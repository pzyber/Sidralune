using System;
using System.Collections.Generic;
using System.Linq;
using Sidralune.Imaging;
using Sidralune.Imaging.Analysis;
using Sidralune.Imaging.Planetary;
using Sidralune.Imaging.Registration;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Sidralune.Tests.Checks;

namespace Sidralune.Tests
{
    /// <summary>Geometry operations, transforms, star detection options, PSF fitting, star matching models and disc detection.</summary>
    [TestClass]
    public class ImagingGeometryTests : TestBase
    {
        private static AstroImage Numbered(int w, int h, int ch = 1)
        {
            var img = new AstroImage(w, h, ch);
            for (int c = 0; c < ch; c++) for (int i = 0; i < w * h; i++) img.Data[c][i] = i + 1000 * c;
            return img;
        }

        [TestMethod]
        public void TestGeometry()
        {
            var img = Numbered(7, 5, 2);
            var crop = Geometry.Crop(img, 2, 1, 3, 2);
            CheckTrue("Crop", crop.Width == 3 && crop.Height == 2 && crop.Data[0][0] == 1 * 7 + 2 && crop.Data[1][5] == 1000 + 2 * 7 + 4);
            var fh = Geometry.FlipHorizontal(img);
            CheckTrue("Horizontal flip", fh.Data[0][0] == 6 && fh.Data[0][7] == 13);
            var fv = Geometry.FlipVertical(img);
            CheckTrue("Vertical flip", fv.Data[0][0] == 28 && fv.Data[1][34] == 1006);
            var r1 = Geometry.Rotate90(img, 1);
            CheckTrue("Quarter turn swaps the size", r1.Width == 5 && r1.Height == 7);
            var r4 = Geometry.Rotate90(Geometry.Rotate90(Geometry.Rotate90(r1, 1), 1), 1);
            CheckTrue("Four quarter turns are the identity", r4.Width == 7 && r4.Data[0].SequenceEqual(img.Data[0]) && r4.Data[1].SequenceEqual(img.Data[1]));
            var r2 = Geometry.Rotate90(img, 2);
            CheckTrue("Half turn equals both flips", r2.Data[0].SequenceEqual(Geometry.FlipVertical(Geometry.FlipHorizontal(img)).Data[0]));
            var flat = new AstroImage(8, 6, new[] { Enumerable.Repeat(0.25f, 48).ToArray() });
            var binAvg = Geometry.Bin(flat, 2);
            var binSum = Geometry.Bin(flat, 2, true);
            CheckTrue("2×2 binning", binAvg.Width == 4 && binAvg.Height == 3 && Math.Abs(binAvg.Data[0][5] - 0.25) < 1e-6 && Math.Abs(binSum.Data[0][5] - 1.0) < 1e-6);
            foreach (var interp in new[] { Interpolation.Nearest, Interpolation.Bilinear, Interpolation.Bicubic, Interpolation.Lanczos3 })
            {
                var rs = Geometry.Resize(flat, 13, 9, interp);
                CheckTrue("Resize keeps a flat image flat (" + interp + ")", rs.Width == 13 && rs.Height == 9 && rs.Data[0].All(q => Math.Abs(q - 0.25) < 1e-4));
            }
            var ramp = new AstroImage(40, 30, 1);
            for (int y = 0; y < 30; y++) for (int x = 0; x < 40; x++) ramp.Data[0][y * 40 + x] = 0.01f * x + 0.02f * y;
            var shifted = Geometry.Warp(ramp, Transform2D.Translation(3, 2), 40, 30, Interpolation.Bilinear);
            Check("Warp by a translation", shifted.Data[0][10 * 40 + 20], 0.01 * 17 + 0.02 * 8, 1e-5);
            Check("Sub-pixel shift of a linear ramp", Geometry.Shift(ramp, 0.5, 0, Interpolation.Bilinear).Data[0][10 * 40 + 20], 0.01 * 19.5 + 0.02 * 10, 1e-5);
            var rot = Geometry.Rotate(flat, 30, true);
            CheckTrue("Rotation with expansion grows the canvas", rot.Width > 8 && rot.Height > 6);

            var t = Transform2D.Rotation(0.3, 10, 20, 1.5).Compose(Transform2D.Translation(4, -2));
            double u, v, x2, y2;
            t.Apply(7, 9, out u, out v);
            t.Inverse().Apply(u, v, out x2, out y2);
            CheckTrue("Transform inverse", Math.Abs(x2 - 7) < 1e-9 && Math.Abs(y2 - 9) < 1e-9);
            Check("Transform scale", t.Scale, 1.5, 1e-12);
            Check("Transform rotation", t.RotationDeg, 0.3 * 180 / Math.PI, 1e-9);
            var a = Transform2D.Translation(1, 2); var b = Transform2D.Rotation(0.5, 0, 0);
            double p1, q1, p2, q2;
            a.Compose(b).Apply(3, 4, out p1, out q1);
            b.Apply(3, 4, out p2, out q2); a.Apply(p2, q2, out p2, out q2);
            double p3, q3; a.Apply(3, 4, out p3, out q3); b.Apply(p3, q3, out p3, out q3);
            CheckTrue("Compose applies one transform after the other", (Math.Abs(p1 - p2) < 1e-12 && Math.Abs(q1 - q2) < 1e-12) || (Math.Abs(p1 - p3) < 1e-12 && Math.Abs(q1 - q3) < 1e-12));
        }

        [TestMethod]
        public void TestStarDetectionOptions()
        {
            int w = 200, h = 160;
            var stars = new List<Synthetic.SynthStar>();
            for (int i = 0; i < 12; i++) stars.Add(new Synthetic.SynthStar { X = 20 + (i % 4) * 50 + 0.3, Y = 25 + (i / 4) * 50 + 0.6, Flux = 0.2 + 0.05 * i });
            var img = Synthetic.Render(w, h, stars, Transform2D.Identity, 3.0, 0.05, 0.002, 3, 15);
            var found = StarDetector.Detect(img);
            CheckTrue("All stars found, hot pixels rejected", found.Count == 12, found.Count + " found");
            // the rendering includes shot noise, about 0.07 px of centroid scatter for these stars
            var posErr = stars.Select(s => found.Min(f => Math.Sqrt((f.X - s.X) * (f.X - s.X) + (f.Y - s.Y) * (f.Y - s.Y)))).ToList();
            CheckTrue("Centroids accurate (mean < 0.12 px, worst < 0.3 px)", posErr.Average() < 0.12 && posErr.Max() < 0.3, posErr.Average().ToString("0.000") + " / " + posErr.Max().ToString("0.000"));
            double fwhm, hfr, ecc;
            StarDetector.Summary(found, out fwhm, out hfr, out ecc);
            Check("Median FWHM", fwhm, 3.0, 0.3);
            CheckTrue("Round stars", ecc < 0.3, ecc.ToString("0.00"));
            var brightest = found.OrderByDescending(f => f.Flux).First();
            CheckTrue("Fluxes ordered like the input", Math.Abs(brightest.X - stars.Last().X) < 0.5 && Math.Abs(brightest.Y - stars.Last().Y) < 0.5);
            CheckTrue("Maximum star count", StarDetector.Detect(img, new StarDetectionOptions { MaxStars = 5 }).Count == 5);
            var withHot = StarDetector.Detect(img, new StarDetectionOptions { RejectHotPixels = false, MinArea = 1 });
            CheckTrue("Hot pixels detected when not rejected", withHot.Count > found.Count, withHot.Count + " with hot pixels");
            var sat = Synthetic.Render(w, h, new[] { new Synthetic.SynthStar { X = 100, Y = 80, Flux = 40 }, new Synthetic.SynthStar { X = 40, Y = 40, Flux = 0.3 } }, Transform2D.Identity, 3.0, 0.05, 0.002, 3);
            sat.Clip();
            var satStars = StarDetector.Detect(sat);
            CheckTrue("Saturated star flagged, normal star not", satStars.Count == 2 && satStars.Single(s => s.X > 90).Saturated && !satStars.Single(s => s.X < 50).Saturated, satStars.Count + " found");

            // PSF fitting on an elongated star
            var ell = new AstroImage(41, 41, 1);
            double sx = 2.0, sy = 1.2;
            for (int y = 0; y < 41; y++)
                for (int x = 0; x < 41; x++)
                {
                    double dx = x - 20.3, dy = y - 19.8;
                    ell.Data[0][y * 41 + x] = (float)(0.1 + 0.5 * Math.Exp(-0.5 * (dx * dx / (sx * sx) + dy * dy / (sy * sy))));
                }
            var guess = new DetectedStar { X = 20, Y = 20, Peak = 0.5, Background = 0.1, Fwhm = 4 };
            var g = PsfFit.FitGaussian(ell.Data[0], 41, 41, guess, 10);
            CheckTrue("Gaussian fit position", Math.Abs(g.X - 20.3) < 0.01 && Math.Abs(g.Y - 19.8) < 0.01, g.X.ToString("0.000") + ", " + g.Y.ToString("0.000"));
            CheckTrue("Gaussian fit widths", Math.Abs(Math.Max(g.SigmaX, g.SigmaY) - 2.0) < 0.02 && Math.Abs(Math.Min(g.SigmaX, g.SigmaY) - 1.2) < 0.02, g.SigmaX.ToString("0.000") + ", " + g.SigmaY.ToString("0.000"));
            Check("Gaussian fit amplitude", g.Amplitude, 0.5, 0.01);
            Check("Gaussian fit background", g.Background, 0.1, 0.005);
            var round = Synthetic.Render(41, 41, new[] { new Synthetic.SynthStar { X = 20, Y = 20, Flux = 1 } }, Transform2D.Identity, 4, 0.1, 0, 1);
            var m = PsfFit.FitMoffat(round.Data[0], 41, 41, guess, 12);
            Check("Moffat fit FWHM of a 4-px star", m.Fwhm, 4, 0.4);
            CheckTrue("Moffat β found", !double.IsNaN(m.Beta) && m.Beta > 1, m.Beta.ToString("0.0"));
        }

        [TestMethod]
        public void TestStarMatchingModels()
        {
            int w = 400, h = 300;
            var stars = Synthetic.RandomStars(200, w, h, 21);
            var refImg = Synthetic.Render(w, h, stars, Transform2D.Identity, 2.8, 0.05, 0.003, 1);
            var refStars = StarDetector.Detect(refImg);
            var cases = new[]
            {
                Tuple.Create(TransformModel.Translation, Transform2D.Translation(12.3, -7.6)),
                Tuple.Create(TransformModel.Similarity, Transform2D.Rotation(0.2, w / 2, h / 2, 1.02).Compose(Transform2D.Translation(5, 3))),
                Tuple.Create(TransformModel.Affine, Transform2D.Affine(1.01, 0.02, 4, -0.015, 0.99, -3)),
                Tuple.Create(TransformModel.Homography, new Transform2D { M = new[] { 1.0, 0.01, 3, -0.01, 1.0, 2, 2e-5, -1e-5, 1 } }),
            };
            foreach (var c in cases)
            {
                var img = Synthetic.Render(w, h, stars, c.Item2, 2.8, 0.05, 0.003, 2);
                var m = StarMatcher.Match(refStars, StarDetector.Detect(img), c.Item1);
                CheckTrue(c.Item1 + " match succeeds", m.Success && m.Matches >= 20, m.Matches + " matches, rms " + m.RmsError.ToString("0.000"));
                if (!m.Success) continue;
                // the result maps reference stars onto the target frame (or back): either way a reference star lands on its twin
                double worst = 0;
                foreach (var s in stars.Where(s => s.X > 60 && s.X < w - 60 && s.Y > 60 && s.Y < h - 60).Take(20))
                {
                    double tx, ty, mx, my;
                    c.Item2.Apply(s.X, s.Y, out tx, out ty);
                    m.Map(s.X, s.Y, out mx, out my);
                    double e1 = Math.Sqrt((mx - tx) * (mx - tx) + (my - ty) * (my - ty));
                    m.Map(tx, ty, out mx, out my);
                    double e2 = Math.Sqrt((mx - s.X) * (mx - s.X) + (my - s.Y) * (my - s.Y));
                    worst = Math.Max(worst, Math.Min(e1, e2));
                }
                // the rms includes the centroid noise of the faint stars; the fitted transform itself is much better
                CheckTrue(c.Item1 + " transform accurate to 0.3 px", worst < 0.3 && m.RmsError < 0.5, worst.ToString("0.000") + ", rms " + m.RmsError.ToString("0.000"));
            }
            var poly = StarMatcher.Match(refStars, StarDetector.Detect(Synthetic.Render(w, h, stars, Transform2D.Translation(2, 1), 2.8, 0.05, 0.003, 4)), TransformModel.Polynomial2);
            CheckTrue("Polynomial model", poly.Success && poly.PolyX != null && poly.RmsError < 0.3);
            var unrelated = StarDetector.Detect(Synthetic.Render(w, h, Synthetic.RandomStars(200, w, h, 99), Transform2D.Identity, 2.8, 0.05, 0.003, 5));
            var none = StarMatcher.Match(refStars, unrelated);
            CheckTrue("Unrelated fields do not match", !none.Success || none.Matches < 8, none.Matches + " matches");
        }

        [TestMethod]
        public void TestDiscDetection()
        {
            int w = 160, h = 120;
            var d = new float[w * h];
            var rnd = new Random(5);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    double r = Math.Sqrt((x - 70.4) * (x - 70.4) + (y - 55.7) * (y - 55.7));
                    d[y * w + x] = (float)((r < 30 ? 0.7 : 0.05) + 0.01 * rnd.NextDouble());
                }
            double thr = ObjectDetection.OtsuThreshold(d);
            CheckTrue("Otsu threshold separates disc and sky", d.All(v => (v > thr) == (v > 0.5)), thr.ToString("0.0000"));
            var o = ObjectDetection.Detect(d, w, h);
            CheckTrue("Disc found", o.Found && !o.TouchesEdge);
            Check("Disc centre x", o.Cx, 70.4, 0.3);
            Check("Disc centre y", o.Cy, 55.7, 0.3);
            Check("Disc radius", o.Radius, 30, 0.6);
            var limb = ObjectDetection.FitLimb(d, w, h, o);
            CheckTrue("Limb fit", limb != null && limb.Length >= 3 && Math.Abs(limb[0] - 70.4) < 0.5 && Math.Abs(limb[1] - 55.7) < 0.5 && Math.Abs(limb[2] - 30) < 0.7, limb == null ? "null" : string.Join(", ", limb.Select(v => v.ToString("0.00"))));
            var edge = new float[w * h];
            for (int y = 0; y < h; y++) for (int x = 0; x < 20; x++) edge[y * w + x] = 1;
            CheckTrue("Object touching the edge flagged", ObjectDetection.Detect(edge, w, h).TouchesEdge);
            CheckTrue("Nothing in an empty frame", !ObjectDetection.Detect(new float[w * h], w, h).Found);
        }
    }
}
