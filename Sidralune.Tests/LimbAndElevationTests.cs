using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sidralune.Core;
using Sidralune.Core.Ephemeris;
using Sidralune.Core.Mathematics;
using Sidralune.Core.Time;
using Sidralune.Core.Util;
using static Sidralune.Tests.Checks;

namespace Sidralune.Tests
{
    /// <summary>
    /// Eclipse contacts with the Moon's real edge (a lunar limb profile from LRO LOLA), the Moon's orientation it needs,
    /// and the heights of places on the Earth (ETOPO1) for the eclipse map.
    /// </summary>
    [TestClass]
    public class LimbAndElevationTests : TestBase
    {
        private static readonly string Dir = Path.Combine(Path.GetTempPath(), "SidraluneTests", "LimbAndElevation");

        private static BesselianElements Eclipse2028()
        {
            var list = Events.SolarEclipses(AstroTime.CalendarToJd(2028, 7, 20), AstroTime.CalendarToJd(2028, 7, 24));
            return BesselianElements.Compute(list[0].Time);
        }

        /// <summary>A profile all round at one height above 1737.4 km.</summary>
        private static LimbProfile Uniform(double km) => LimbProfile.Uniform(km);

        /// <summary>The height that makes the outline the mean umbral limb NASA uses (k = 0.272281).</summary>
        private static readonly double MeanLimbKm = BesselianElements.K2 * AstroMath.EarthRadius_km - LunarLimb.RadiusKm;

        [TestMethod]
        public void TestLimbContacts()
        {
            var be = Eclipse2028();
            double lat = -16.65373, lon = 128.66009; // in the Kimberley, near the central line
            var mean = be.Local(lat, lon);
            CheckTrue("Total there with a smooth Moon", mean.Kind == 1 && mean.CentralSeconds > 200, mean.CentralSeconds.ToString("0.0"));

            // an outline as round as the mean limb gives the mean contacts
            var same = be.Local(lat, lon, 0, Uniform(MeanLimbKm));
            double d2 = (same.C2Limb.Time.JdTT - mean.C2.Time.JdTT) * 86400, d3 = (same.C3Limb.Time.JdTT - mean.C3.Time.JdTT) * 86400;
            CheckTrue("A round outline the size of the mean limb: the mean contacts", same.LimbCorrected && same.KindLimb == 1 && Math.Abs(d2) < 0.003 && Math.Abs(d3) < 0.003, d2.ToString("0.0000") + " / " + d3.ToString("0.0000") + " s");

            // a kilometre higher all round: a bigger Moon, a longer totality (about 2 s near the central line)
            var big = be.Local(lat, lon, 0, Uniform(MeanLimbKm + 1));
            double longer = big.LimbSeconds - mean.CentralSeconds;
            CheckTrue("A kilometre higher all round: totality longer by about 2 s, both ends", longer > 1 && longer < 5 && big.C2Limb.Time.JdTT < mean.C2.Time.JdTT && big.C3Limb.Time.JdTT > mean.C3.Time.JdTT, longer.ToString("0.00") + " s");

            // a mountain on one half of the limb moves one contact only: the second and third contacts are on opposite sides
            var moved = new double[2, 2];
            for (int half = 0; half < 2; half++)
            {
                var p = Uniform(MeanLimbKm);
                for (int i = 0; i < LimbProfile.Samples; i++) if ((i < LimbProfile.Samples / 2) == (half == 0)) p.Heights[i] += 2;
                var l = be.Local(lat, lon, 0, p);
                moved[half, 0] = (l.C2Limb.Time.JdTT - mean.C2.Time.JdTT) * 86400;
                moved[half, 1] = (l.C3Limb.Time.JdTT - mean.C3.Time.JdTT) * 86400;
            }
            bool one = Math.Abs(moved[0, 0]) > 0.5 != Math.Abs(moved[0, 1]) > 0.5 && Math.Abs(moved[1, 0]) > 0.5 != Math.Abs(moved[1, 1]) > 0.5
                && Math.Min(Math.Abs(moved[0, 0]), Math.Abs(moved[0, 1])) < 0.003 && Math.Min(Math.Abs(moved[1, 0]), Math.Abs(moved[1, 1])) < 0.003;
            CheckTrue("Mountains on one side move one contact only", one, string.Format("east half: C2 {0:0.000} s, C3 {1:0.000} s; west half: C2 {2:0.000} s, C3 {3:0.000} s", moved[0, 0], moved[0, 1], moved[1, 0], moved[1, 1]));

            // valleys deep enough let the Sun through at maximum, so no totality; a place just outside the path gets a short
            // one from mountains
            var deep = be.Local(lat, lon, 0, Uniform(MeanLimbKm - 150));
            CheckTrue("Valleys 150 km deep all round (a Moon smaller than the Sun): no totality", deep.LimbCorrected && deep.KindLimb == 0 && deep.C2Limb == null);
            GeoPoint outside = new GeoPoint();
            bool found = false;
            for (double dl = 0; dl < 3 && !found; dl += 0.002)
            {
                var o = be.Local(lat + dl, lon);
                if (o.Kind == 0 && o.Covers) { outside = new GeoPoint(lat + dl, lon); found = true; }
            }
            var graze = found ? be.Local(outside.Lat, outside.Lon, 0, Uniform(MeanLimbKm + 3)) : null;
            CheckTrue("Just outside the path, 3 km higher mountains make it total", found && graze.KindLimb == 1 && graze.LimbSeconds > 0, found ? outside + ": " + graze.LimbSeconds.ToString("0.0") + " s" : "no edge found");

            // an annular eclipse works the other way: mountains shorten annularity (26 January 2028)
            var ann = BesselianElements.Compute(Events.SolarEclipses(AstroTime.CalendarToJd(2028, 1, 24), AstroTime.CalendarToJd(2028, 1, 28))[0].Time);
            GeoPoint centre = new GeoPoint();
            double cl, clon;
            CheckTrue("The 2028 annular eclipse's axis on the Earth", ann.AxisOnEarth(0, out cl, out clon));
            centre = new GeoPoint(cl, clon);
            var annMean = ann.Local(centre.Lat, centre.Lon);
            var annHigh = ann.Local(centre.Lat, centre.Lon, 0, Uniform(BesselianElements.K2 * AstroMath.EarthRadius_km - LunarLimb.RadiusKm + 1));
            CheckTrue("Annular: a kilometre higher all round shortens annularity", annMean.Kind == 2 && annHigh.KindLimb == 2 && annHigh.LimbSeconds < annMean.CentralSeconds - 0.5, annMean.CentralSeconds.ToString("0.0") + " → " + annHigh.LimbSeconds.ToString("0.0") + " s");
        }

        /// <summary>A stand-in for LOLA at 1 pixel per degree, one height all round (km above 1737.4 km).</summary>
        private static LunarLimb UniformMoon(double km, string name)
        {
            Directory.CreateDirectory(Dir);
            string path = Path.Combine(Dir, name + ".sltg");
            var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, System.Text.Encoding.ASCII, true)) for (int i = 0; i < 360 * 180; i++) w.Write((short)Math.Round(km * 2000));
            ms.Position = 0;
            LunarLimb.Write(path, 1, ms);
            return LunarLimb.Open(path);
        }

        [TestMethod]
        public void TestSitePhasesWithLimb()
        {
            // the site's phase list: the topocentric total phase moved by the limb profile's correction
            var greatest = Events.SolarEclipses(AstroTime.CalendarToJd(2028, 7, 20), AstroTime.CalendarToJd(2028, 7, 24))[0].Time;
            var site = new Observer { Name = "Kimberley", LatitudeDeg = -16.65373, LongitudeDeg = 128.66009, ElevationM = 0 };
            Func<System.Collections.Generic.List<EclipsePhase>, string, double> at = (ph, name) => { var x = ph.FirstOrDefault(p => p.Name == name); return x == null ? double.NaN : x.Time.JdTT; };
            var plain = Events.SolarEclipsePhases(site, greatest, false);
            double c2 = at(plain, "Total eclipse begins"), c3 = at(plain, "Total eclipse ends");
            CheckTrue("The total phase without a profile", !double.IsNaN(c2) && !double.IsNaN(c3));
            try
            {
                LunarLimb.Override = UniformMoon(MeanLimbKm, "mean");
                var same = Events.SolarEclipsePhases(site, greatest);
                double s2 = (at(same, "Total eclipse begins") - c2) * 86400, s3 = (at(same, "Total eclipse ends") - c3) * 86400;
                CheckTrue("A profile as round as the mean limb: the same total phase", Math.Abs(s2) < 0.01 && Math.Abs(s3) < 0.01, s2.ToString("0.000") + " / " + s3.ToString("0.000") + " s");
                LunarLimb.Override = UniformMoon(MeanLimbKm + 1, "higher");
                var big = Events.SolarEclipsePhases(site, greatest);
                double b2 = (at(big, "Total eclipse begins") - c2) * 86400, b3 = (at(big, "Total eclipse ends") - c3) * 86400;
                CheckTrue("A kilometre higher: begins earlier, ends later", b2 < -0.5 && b3 > 0.5, b2.ToString("0.00") + " / " + b3.ToString("0.00") + " s");
                LunarLimb.Override = UniformMoon(MeanLimbKm - 15, "lower"); // too deep for a whole round: the outline is stored in half metres
                var none = Events.SolarEclipsePhases(site, greatest);
                CheckTrue("Valleys 15 km deep all round: shorter or no total phase", double.IsNaN(at(none, "Total eclipse begins")) || at(none, "Total eclipse ends") - at(none, "Total eclipse begins") < c3 - c2 - 5 / 86400.0);
            }
            finally { LunarLimb.Override = null; }
        }

        [TestMethod]
        public void TestElevationMovesContacts()
        {
            var be = Eclipse2028();
            var low = be.Local(-16.65373, 128.66009, 0);
            var high = be.Local(-16.65373, 128.66009, 2000);
            double d2 = (high.C2.Time.JdTT - low.C2.Time.JdTT) * 86400, d3 = (high.C3.Time.JdTT - low.C3.Time.JdTT) * 86400;
            CheckTrue("At 2000 m the contacts move by tenths of a second", Math.Abs(d2) > 0.02 && Math.Abs(d2) < 3 && Math.Abs(d3) > 0.02 && Math.Abs(d3) < 3, d2.ToString("0.000") + " / " + d3.ToString("0.000") + " s");
        }

        [TestMethod]
        public void TestMoonOrientation()
        {
            // the IAU model (the mean Earth frame of lunar maps) against Meeus's librations: the sub-Earth point and the
            // position angle of the axis
            foreach (var date in new[] { new DateTime(2026, 8, 12, 17, 0, 0), new DateTime(2027, 8, 2, 10, 0, 0), new DateTime(2028, 7, 22, 3, 0, 0), new DateTime(2030, 3, 15, 0, 0, 0) })
            {
                var t = AstroTime.FromDateTime(DateTime.SpecifyKind(date, DateTimeKind.Utc));
                double jd = t.JdTDB;
                var toMoon = EphemerisManager.Position(Body.Moon, jd) - EphemerisManager.Position(Body.Earth, jd);
                double cm, subLat, pa;
                PlanetPhysics.Orientation(Body.Moon, jd, toMoon.Normalized(), toMoon.Length * AstroMath.LightTimeAU_days, out cm, out subLat, out pa);
                // Meeus's axis angle is from the north of date, Orientation's from J2000's: the pole and the Moon of date
                double ra0, dec0, w;
                PlanetPhysics.RotationElements(Body.Moon, jd - toMoon.Length * AstroMath.LightTimeAU_days, out ra0, out dec0, out w);
                var npb = Sidralune.Core.Coordinates.Precession.NpbMatrix(t.T);
                double ra, dec, pra, pdec;
                (npb * toMoon.Normalized()).ToSpherical(out ra, out dec);
                (npb * Vec3.FromSpherical(ra0 * AstroMath.Deg2Rad, dec0 * AstroMath.Deg2Rad)).ToSpherical(out pra, out pdec);
                pa = AstroMath.NormalizeDeg(AstroMath.PositionAngle(ra, dec, pra, pdec) * AstroMath.Rad2Deg);
                double l, b, axis;
                PlanetPhysics.MoonLibration(t.JdTT, out l, out b, out axis);
                double dl = ((-cm - l) % 360 + 540) % 360 - 180, dpa = ((pa - axis) % 360 + 540) % 360 - 180;
                CheckTrue(date.ToString("yyyy-MM-dd") + ": libration as Meeus's within 0.15°", Math.Abs(dl) < 0.15 && Math.Abs(subLat - b) < 0.15 && Math.Abs(dpa) < 0.15,
                    string.Format("longitude {0:0.000}° (Meeus {1:0.000}°), latitude {2:0.000}° ({3:0.000}°), axis {4:0.000}° ({5:0.000}°)", -cm, l, subLat, b, pa, axis));
            }
        }

        [TestMethod]
        public void TestLunarTopography()
        {
            Directory.CreateDirectory(Dir);
            // a made-up LOLA grid at 1 pixel per degree: 2000 half metres (1 km) everywhere, but 4000 (2 km) at 80°–100° E
            // within 30° of the equator (the middle of the Moon's east limb)
            string img = Path.Combine(Dir, "LDEM_1.IMG"), sltg = Path.Combine(Dir, "lola1.sltg");
            int cols = 360, rows = 180;
            using (var f = new FileStream(img, FileMode.Create))
            using (var w = new BinaryWriter(f))
                for (int r = 0; r < rows; r++)
                    for (int c = 0; c < cols; c++) w.Write((short)(c + 0.5 > 80 && c + 0.5 < 100 && Math.Abs(90 - (r + 0.5)) < 30 ? 4000 : 2000));
            LunarLimb.Prepare(img, sltg, 1);
            var limb = LunarLimb.Open(sltg);
            var grid = TileGrid.Open(sltg);
            CheckTrue("Only the tiles in the limb band kept", grid.StoredTiles > 0 && grid.StoredTiles < grid.AllTiles, grid.StoredTiles + " of " + grid.AllTiles);
            CheckTrue("Heights in the band read back: 1 km, 2 km east of 80°", Math.Abs(limb.HeightKm(10.5, 120.5) - 1) < 1e-9 && Math.Abs(limb.HeightKm(0.5, 90.5) - 2) < 1e-9, limb.HeightKm(10.5, 120.5) + " / " + limb.HeightKm(0.5, 90.5));
            CheckTrue("Between points: halfway", Math.Abs(limb.HeightKm(0.5, 80) - 1.5) < 1e-9, limb.HeightKm(0.5, 80).ToString());
            CheckTrue("The middle of the near side is not kept (reads 0)", limb.HeightKm(0.5, 0.5) == 0);
            CheckTrue("Longitudes wrap round", Math.Abs(limb.HeightKm(10.5, 120.5 - 360) - 1) < 1e-9);
            CheckTrue("The poles read", Math.Abs(limb.HeightKm(90, 45) - 1) < 1e-9 && Math.Abs(limb.HeightKm(-90, 200) - 1) < 1e-9);

            // its profile seen from the Earth during the 2028 eclipse: 1 km all round, 2 km where the eastern limb is
            var be = Eclipse2028();
            Vec3 view, north, east;
            be.AxisFrame(0, out view, out north, out east);
            var p = limb.Profile(view, north, east, AstroTime.FromTT(be.JdTT(0)).JdTDB);
            double lo = p.Heights.Min(), hi = p.Heights.Max();
            CheckTrue("The profile: 1 km all round, up to 2 km on one side", Math.Abs(lo - 1) < 0.01 && Math.Abs(hi - 2) < 0.01, lo.ToString("0.000") + " – " + hi.ToString("0.000") + " km");
            // the high side is the Moon's east (selenographic) limb: in the sky, roughly west of its centre (position angle near 270°)
            double sx = 0, sy = 0;
            for (int i = 0; i < LimbProfile.Samples; i++)
                if (p.Heights[i] > 1.9) { sx += Math.Sin(i * p.StepDeg * AstroMath.Deg2Rad); sy += Math.Cos(i * p.StepDeg * AstroMath.Deg2Rad); }
            double paHigh = AstroMath.NormalizeDeg(Math.Atan2(sx, sy) * AstroMath.Rad2Deg); // the middle of the high part
            CheckTrue("The high side where the Moon's 90° E limb is (position angle about 270°)", Math.Abs(((paHigh - 270) % 360 + 540) % 360 - 180) < 30, paHigh.ToString("0.0") + "°");

            // a grid of the wrong size is refused
            File.WriteAllBytes(img, new byte[1000]);
            bool refused = false;
            try { LunarLimb.Prepare(img, sltg + "2", 1); } catch (InvalidDataException) { refused = true; }
            CheckTrue("A file of the wrong size is refused", refused);
        }

        [TestMethod]
        public void TestElevationData()
        {
            Directory.CreateDirectory(Dir);
            // a made-up ETOPO-like grid, 1° apart, rows from 90° N: land of 1000 m at 45°–55° N 20°–30° E, a hollow of −100 m
            // in it at 50° N 25° E, the sea floor at −4000 m elsewhere (some of it in the land's tile)
            int cols = 361, rows = 181;
            Func<int, int, short> value = (lat, lon) =>
            {
                if (lat == 50 && lon == 25) return -100;
                if (lat >= 45 && lat <= 55 && lon >= 20 && lon <= 30) return 1000;
                return -4000;
            };
            var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, System.Text.Encoding.ASCII, true))
                for (int r = 0; r < rows; r++)
                    for (int c = 0; c < cols; c++) w.Write(value(90 - r, -180 + c));
            string zip = Path.Combine(Dir, "etopo_test.zip"), sltg = Path.Combine(Dir, "etopo.sltg");
            if (File.Exists(zip)) File.Delete(zip);
            using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                using (var s = new StreamWriter(z.CreateEntry("etopo_test.hdr").Open()))
                    s.Write("NCOLS 361\r\nNROWS 181\r\nXLLCENTER -180.0\r\nYLLCENTER -90.0\r\nCELLSIZE 1.0\r\nNODATA_VALUE -32768\r\nBYTEORDER LSBFIRST\r\nNUMBERTYPE 2_BYTE_INTEGER\r\n");
                using (var s = z.CreateEntry("etopo_test.bin").Open()) { ms.Position = 0; ms.CopyTo(s); }
            }
            Elevation.Prepare(zip, sltg);
            var e = Elevation.Open(sltg);
            var grid = TileGrid.Open(sltg);
            CheckTrue("Only the tiles with land kept", grid.StoredTiles == 1, grid.StoredTiles + " of " + grid.AllTiles);
            Func<double, double, double> at = (la, lo) => e.MetresAt(la, lo) ?? double.NaN;
            CheckTrue("On the land: 1000 m", at(47, 22) == 1000 && at(52.5, 27.5) == 1000);
            CheckTrue("Between the land's edge and the sea: halfway to sea level, not pulled down by the sea floor", Math.Abs(at(55.5, 25) - 500) < 1e-9, at(55.5, 25).ToString());
            CheckTrue("Below sea level reads as sea level", at(50, 25) == 0, at(50, 25).ToString());
            CheckTrue("The sea is 0, in the land's tile and far out", at(43, 34) == 0 && at(0, -30) == 0, at(43, 34) + " / " + at(0, -30));
            CheckTrue("The poles and the date line read", e.MetresAt(90, 0).HasValue && e.MetresAt(-90, 0).HasValue && at(10, 180) == at(10, -180));

            // a grid of the wrong size is refused
            if (File.Exists(zip)) File.Delete(zip);
            using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                using (var s = new StreamWriter(z.CreateEntry("x.hdr").Open())) s.Write("NCOLS 361\r\nNROWS 181\r\n");
                using (var s = z.CreateEntry("x.bin").Open()) s.Write(new byte[100], 0, 100);
            }
            bool refused = false;
            try { Elevation.Prepare(zip, sltg + "2"); } catch (InvalidDataException) { refused = true; }
            CheckTrue("A grid of the wrong size is refused", refused);
        }

        [TestMethod]
        public void TestNewDatasets()
        {
            foreach (var id in new[] { Elevation.DatasetId, LunarLimb.Dataset16, LunarLimb.Dataset64 })
            {
                var d = DataManager.Get(id);
                CheckTrue(id + ": listed, from https, prepared after download, not recommended", d != null && d.Url.StartsWith("https://") && d.Prepare != null && !d.Recommended && d.ApproxBytes > 1000000 && !string.IsNullOrEmpty(d.License));
            }
            CheckTrue("The LOLA grids have a mirror", DataManager.Get(LunarLimb.Dataset16).Mirrors.Length > 0 && DataManager.Get(LunarLimb.Dataset64).Mirrors.Length > 0);
        }

        [TestMethod]
        public void TestInstalledData()
        {
            // with the real data installed for Sidralune (skipped otherwise): heights of known places, and the 2028
            // eclipse with the Moon's real edge
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sidralune", "Data");
            string etopo = Path.Combine(root, "Maps", "etopo1.sltg");
            if (File.Exists(etopo))
            {
                var e = Elevation.Open(etopo);
                double stockholm = e.MetresAt(59.33, 18.07) ?? double.NaN, blanc = e.MetresAt(45.8326, 6.8652) ?? double.NaN, sea = e.MetresAt(56, 3) ?? double.NaN;
                CheckTrue("ETOPO1: Stockholm low, Mont Blanc high, the North Sea 0", stockholm < 100 && blanc > 3500 && sea == 0, stockholm + " / " + blanc + " / " + sea);
            }
            // the limb corrections need DE440s (the analytic Moon puts the eclipse 53 s late, the Moon turned another way)
            bool loadedHere = EclipseGeometryTests.UseInstalledDe440();
            try { InstalledLola(root); }
            finally { if (loadedHere) EphemerisManager.UnloadSpk(); }
        }

        private void InstalledLola(string root)
        {
            if (!EphemerisManager.HasHighPrecision) { Info("DE440s is not installed: the lunar limb corrections are not checked"); return; }
            foreach (var name in new[] { "lola64.sltg", "lola16.sltg" })
            {
                string path = Path.Combine(root, "Ephemerides", name);
                if (!File.Exists(path)) continue;
                var limb = LunarLimb.Open(path);
                var be = Eclipse2028();
                // the limb corrections of C2 and C3 (real edge − mean limb) in Xavier Jubier's Solar Eclipse Maps for 22 July
                // 2028 ("LC", lunar limb corrected); the difference of two times from the same elements, so free of the
                // ephemeris's own error
                var points = new[]
                {
                    new { P = new GeoPoint(-16.81566, 129.63405), C2 = -3.8, C3 = -3.6 },
                    new { P = new GeoPoint(-15.62364, 126.76662), C2 = 3.4, C3 = -2.1 },
                    new { P = new GeoPoint(-16.65373, 128.66009), C2 = 2.8, C3 = -1.6 },
                };
                foreach (var x in points)
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var l = be.Local(x.P.Lat, x.P.Lon, 0, limb);
                    double c2 = l.C2Limb == null ? double.NaN : (l.C2Limb.Time.JdTT - l.C2.Time.JdTT) * 86400, c3 = l.C3Limb == null ? double.NaN : (l.C3Limb.Time.JdTT - l.C3.Time.JdTT) * 86400;
                    CheckTrue(name + " at " + x.P + ": the limb corrections as Xavier Jubier's within 0.5 s", l.KindLimb == 1 && Math.Abs(c2 - x.C2) < 0.5 && Math.Abs(c3 - x.C3) < 0.5,
                        string.Format("C2 {0:+0.00;-0.00} s (his {1:+0.0;-0.0}), C3 {2:+0.00;-0.00} s (his {3:+0.0;-0.0}); {4:0.0} → {5:0.0} s, {6} ms", c2, x.C2, c3, x.C3, l.CentralSeconds, l.LimbSeconds, sw.ElapsedMilliseconds));
                    Info(name + " at " + x.P + string.Format(": C2 {0:+0.00;-0.00} s (his {1:+0.0;-0.0}), C3 {2:+0.00;-0.00} s (his {3:+0.0;-0.0})", c2, x.C2, c3, x.C3));
                }
            }
        }
    }
}
