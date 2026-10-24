using System;
using System.Collections.Generic;
using System.Linq;
using Sidralune.Core.Mathematics;
using Sidralune.Imaging;
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
            }
        }
    }
}
