using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Sidralune.Core.Mathematics;
using Sidralune.Imaging;
using Sidralune.Imaging.Analysis;
using Sidralune.Science.Astrometry;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Sidralune.Tests.Checks;

namespace Sidralune.Tests
{
    [TestClass]
    public class AstrometryTests : TestBase
    {
        /// <summary>Synthetic catalog of random stars around a centre.</summary>
        private class FakeCatalog : IStarCatalogProvider
        {
            public List<RefStar> Stars = new List<RefStar>();
            public string Name => "fake";
            public bool IsOnline => false;
            public FakeCatalog(double ra, double dec, double radius, int n, int seed)
            {
                var rng = new Random(seed);
                for (int i = 0; i < n; i++)
                {
                    double r = radius * Math.Sqrt(rng.NextDouble()), a = rng.NextDouble() * 2 * Math.PI;
                    double d = dec + r * Math.Cos(a);
                    Stars.Add(new RefStar { Ra = AstroMath.NormalizeDeg(ra + r * Math.Sin(a) / Math.Cos(d * AstroMath.Deg2Rad)), Dec = d, Mag = 6 + 6 * rng.NextDouble() });
                }
            }
            public List<RefStar> Cone(double ra, double dec, double radius, double mag, int max) =>
                Stars.Where(s => AstroMath.Separation(ra * AstroMath.Deg2Rad, dec * AstroMath.Deg2Rad, s.Ra * AstroMath.Deg2Rad, s.Dec * AstroMath.Deg2Rad) * AstroMath.Rad2Deg < radius && s.Mag < mag)
                     .OrderBy(s => s.Mag).Take(max).ToList();
        }

        [TestMethod]
        public void TestAstapCancel()
        {
            // a stand-in for ASTAP that takes half a minute: cancelling ends it at once
            string dir = Path.Combine(Path.GetTempPath(), "sidralune_astap_test");
            Directory.CreateDirectory(dir);
            string fake = Path.Combine(dir, "slow_astap.cmd");
            File.WriteAllText(fake, "@ping -n 30 127.0.0.1 >nul\r\n");
            using (var cts = new CancellationTokenSource(500))
            {
                var sw = Stopwatch.StartNew();
                bool cancelled = false;
                try { ExternalSolvers.SolveWithAstap(Path.Combine(dir, "none.fits"), 100, 100, double.NaN, double.NaN, double.NaN, 30, fake, cts.Token); }
                catch (OperationCanceledException) { cancelled = true; }
                CheckTrue("Cancelling an ASTAP solve ends it at once", cancelled && sw.ElapsedMilliseconds < 5000, sw.ElapsedMilliseconds + " ms");
            }
        }

        [TestMethod]
        public void TestImageDepth()
        {
            // magnitude = 10 - 2.5 log10(flux). The catalogue holds only stars of magnitude 6-7, all clipped at the top like
            // the bright stars of a deep exposure; the image shows 3000 stars down to about magnitude 13-14
            int w = 1600, h = 1200;
            var wcs = Wcs.Create(83.8, -5.4, 6.0, 20, w, h);
            var rnd = new Random(7);
            var stars = new List<Synthetic.SynthStar>();
            var mags = new List<double>();
            var refs = new List<RefStar>();
            Action<double, bool> add = (mag, inCatalogue) =>
            {
                var st = new Synthetic.SynthStar { X = 15 + rnd.NextDouble() * (w - 30), Y = 15 + rnd.NextDouble() * (h - 30), Flux = Math.Pow(10, -0.4 * (mag - 10)) };
                stars.Add(st); mags.Add(mag);
                if (!inCatalogue) return;
                double ra, dec;
                wcs.PixelToWorld(st.X, st.Y, out ra, out dec);
                refs.Add(new RefStar { Ra = ra, Dec = dec, Mag = mag });
            };
            for (int i = 0; i < 60; i++) add(6 + rnd.NextDouble(), true);
            for (int i = 0; i < 3000; i++) add(7 + 7 * rnd.NextDouble(), false);
            var img = Synthetic.Render(w, h, stars, Transform2D.Identity, 3, 0.05, 0.002, 3);
            var lum = img.Data[0];
            CheckTrue("The catalogue stars are clipped", refs.Count(r => { double x, y; wcs.WorldToPixel(r.Ra, r.Dec, out x, out y); return lum[(int)Math.Round(y) * w + (int)Math.Round(x)] >= 0.999f; }) > 50);

            var res = ImageDepth.Measure(lum, w, h, wcs, refs);
            CheckTrue("All stars are measured, not only the brightest 2000", res.Stars > 2000, res.Stars + " stars");
            CheckTrue("Clipped catalogue stars calibrate from their wings", res.Calibrators >= 40, res.Calibrators + " calibrators");
            Check("Zero point", res.ZeroPoint, 10, 0.1);
            // the true magnitude of the faintest 5 % of the stars that were detected
            var found = StarDetector.Detect(lum, w, h, new StarDetectionOptions { MaxStars = int.MaxValue });
            var trueMags = found.Select(f => { int k = 0; double best = 2.25; for (int i = 0; i < stars.Count; i++) { double d = (stars[i].X - f.X) * (stars[i].X - f.X) + (stars[i].Y - f.Y) * (stars[i].Y - f.Y); if (d < best) { best = d; k = i + 1; } } return k > 0 ? mags[k - 1] : double.NaN; })
                .Where(m => !double.IsNaN(m)).OrderBy(m => m).ToList();
            Check("Limiting magnitude: the faintest stars detected, beyond the catalogue's depth", res.Limit, trueMags[trueMags.Count * 19 / 20], 0.4);
            CheckTrue("Unknown without catalogue stars", double.IsNaN(ImageDepth.Measure(lum, w, h, wcs, refs.Take(3)).Limit));
            CheckTrue("Stars found beforehand give the same depth", ImageDepth.Measure(lum, w, h, wcs, refs, found).Limit == res.Limit);
            var pairs = ImageDepth.Match(wcs, found, refs);
            CheckTrue("Catalogue stars are matched where the solution puts them", pairs.Count >= 55 && pairs.All(m => { double x, y; wcs.WorldToPixel(m.Item2.Ra, m.Item2.Dec, out x, out y); return Math.Abs(m.Item1.X - x) < 2.5 && Math.Abs(m.Item1.Y - y) < 2.5; }), pairs.Count + " matches");
        }

        [TestMethod]
        public void TestWcsRoundtrip()
        {
            var w = Wcs.Create(83.8, -5.4, 2.5, 33, 1000, 800);
            double ra, dec, x, y;
            w.PixelToWorld(120, 700, out ra, out dec);
            w.WorldToPixel(ra, dec, out x, out y);
            Check("WCS roundtrip x", x, 120, 1e-6);
            Check("WCS roundtrip y", y, 700, 1e-6);
            Check("WCS scale", w.PixelScale, 2.5, 1e-9);
            Check("WCS rotation", w.RotationDeg, 33, 1e-9);
            var h = new FitsHeader();
            w.WriteToHeader(h);
            var w2 = Wcs.FromHeader(h, 1000, 800);
            w2.PixelToWorld(120, 700, out x, out y);
            Check("WCS header roundtrip", AstroMath.Separation(ra * AstroMath.Deg2Rad, dec * AstroMath.Deg2Rad, x * AstroMath.Deg2Rad, y * AstroMath.Deg2Rad) * AstroMath.Rad2ArcSec, 0, 1e-3);
        }

        [TestMethod]
        public void TestPlateSolveSynthetic()
        {
            foreach (var flip in new[] { false, true })
            {
                double ra0 = 150.3, dec0 = 42.7;
                var cat = new FakeCatalog(ra0, dec0, 3, 2500, 4);
                int W = 1200, H = 900;
                var truth = Wcs.Create(ra0 + 0.4, dec0 - 0.25, 3.2, 117, W, H, flip);
                // render catalog stars through the true WCS
                var synth = new List<Synthetic.SynthStar>();
                foreach (var s in cat.Stars)
                {
                    double x, y;
                    if (truth.WorldToPixel(s.Ra, s.Dec, out x, out y) && x > 0 && y > 0 && x < W && y < H)
                        synth.Add(new Synthetic.SynthStar { X = x, Y = y, Flux = 0.5 * Math.Pow(10, -0.4 * (s.Mag - 6)) });
                }
                var img = Synthetic.Render(W, H, synth, Transform2D.Identity, 3.0, 0.05, 0.004, 5);
                var res = PlateSolver.Solve(img, new SolveOptions { HintRa = ra0, HintDec = dec0, SearchRadius = 2, Catalog = cat, MagLimit = 13 });
                Info((flip ? "flipped: " : "normal: ") + res.Message + " in " + res.Elapsed.TotalSeconds.ToString("0.0") + "s");
                CheckTrue("Plate solve success" + (flip ? " (flipped)" : ""), res.Success);
                if (!res.Success) continue;
                double tr, td, sr, sd;
                truth.Centre(out tr, out td); res.Wcs.Centre(out sr, out sd);
                Check("Solved centre error (arcsec)", AstroMath.Separation(tr * AstroMath.Deg2Rad, td * AstroMath.Deg2Rad, sr * AstroMath.Deg2Rad, sd * AstroMath.Deg2Rad) * AstroMath.Rad2ArcSec, 0, 2);
                Check("Solved scale", res.Wcs.PixelScale, 3.2, 0.01);
                Check("Solved rotation", res.Wcs.RotationDeg, 117, 0.1);
                CheckTrue("Solved parity", res.Wcs.Flipped == flip);
                // stars found once beforehand (for the image depth as well) give the same solution
                var shared = PlateSolver.Solve(img, new SolveOptions { HintRa = ra0, HintDec = dec0, SearchRadius = 2, Catalog = cat, MagLimit = 13, Stars = ImageDepth.DetectAll(img.Data[0], W, H) });
                double pr, pd;
                if (shared.Success) shared.Wcs.Centre(out pr, out pd); else { pr = double.NaN; pd = double.NaN; }
                CheckTrue("Stars handed to the solver give the same solution", shared.Success && AstroMath.Separation(sr * AstroMath.Deg2Rad, sd * AstroMath.Deg2Rad, pr * AstroMath.Deg2Rad, pd * AstroMath.Deg2Rad) * AstroMath.Rad2ArcSec < 0.5);
            }
        }
    }
}
