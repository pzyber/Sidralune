using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sidralune.Core;
using Sidralune.Core.Ephemeris;
using Sidralune.Core.Time;
using static Sidralune.Tests.Checks;

namespace Sidralune.Tests
{
    /// <summary>Besselian elements, an eclipse anywhere on the Earth, and the paths drawn on the eclipse map.</summary>
    [TestClass]
    public class EclipseGeometryTests : TestBase
    {
        private static AstroTime? Greatest(int y, int m, int d)
        {
            var list = Events.SolarEclipses(AstroTime.CalendarToJd(y, m, d) - 2, AstroTime.CalendarToJd(y, m, d) + 2);
            return list.Count == 1 ? list[0].Time : (AstroTime?)null;
        }

        private static double Sec(SolarContact c, int y, int mo, int d, int h, int mi, double s) =>
            c == null ? double.NaN : (c.Time.ToDateTimeUtc() - new DateTime(y, mo, d, h, mi, 0, DateTimeKind.Utc).AddSeconds(s)).TotalSeconds;

        [TestMethod]
        public void TestLocalCircumstances()
        {
            // 22 July 2028 in the Kimberley, from Xavier Jubier's Solar Eclipse Maps (mean lunar limb): C1, C2, maximum, C3, C4 and totality
            var gn = Greatest(2028, 7, 22);
            CheckTrue("Solar eclipse on 22 July 2028", gn.HasValue);
            if (!gn.HasValue) return;
            // with DE440s (a copy of the one installed for Sidralune, when there is one) within 2 s; the built-in analytic
            // Moon puts this eclipse about 53 s late, so without it only roughly
            bool loadedHere = UseInstalledDe440();
            try { Kimberley2028(gn.Value); }
            finally { if (loadedHere) EphemerisManager.UnloadSpk(); }

            // the Besselian elements agree with the topocentric calculation used elsewhere (12 August 2026)
            var aug12n = Greatest(2026, 8, 12);
            if (!aug12n.HasValue) return;
            var aug12 = aug12n.Value;
            var be26 = BesselianElements.Compute(aug12);
            foreach (var o in new[]
            {
                new Observer { Name = "Stockholm", LatitudeDeg = 59.3293, LongitudeDeg = 18.0686, ElevationM = 30 },
                new Observer { Name = "Reykjavík", LatitudeDeg = 64.1466, LongitudeDeg = -21.9426, ElevationM = 20 },
                new Observer { Name = "Madrid", LatitudeDeg = 40.4168, LongitudeDeg = -3.7038, ElevationM = 650 },
                new Observer { Name = "Sydney", LatitudeDeg = -33.8688, LongitudeDeg = 151.2093, ElevationM = 20 },
            })
            {
                var b = be26.Local(o.LatitudeDeg, o.LongitudeDeg, o.ElevationM);
                var t = Events.SolarEclipseAt(o, aug12);
                CheckTrue(o.Name + ": both say whether the Moon covers the Sun", b.Covers == t.Covers, b.Covers + " / " + t.Covers);
                if (!b.Covers || !t.Covers) continue;
                Check(o.Name + ": maximum as topocentric (s)", (b.Maximum.Time.JdTT - t.Maximum.JdTT) * 86400, 0, 3);
                Check(o.Name + ": first contact as topocentric (s)", (b.C1.Time.JdTT - t.Start.Value.JdTT) * 86400, 0, 3);
                Check(o.Name + ": last contact as topocentric (s)", (b.C4.Time.JdTT - t.End.Value.JdTT) * 86400, 0, 3);
            }
        }

        /// <summary>
        /// Loads a copy of the DE440s installed for Sidralune (in the user's own data folder) when no kernel is loaded;
        /// true if it did, and the caller unloads it again.
        /// </summary>
        public static bool UseInstalledDe440()
        {
            if (EphemerisManager.HasHighPrecision) return false;
            string installed = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sidralune", "Data", "Ephemerides", "de440s.bsp");
            if (!System.IO.File.Exists(installed)) return false;
            string copy = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SidraluneTests-de440s.bsp");
            try
            {
                if (!System.IO.File.Exists(copy) || new System.IO.FileInfo(copy).Length != new System.IO.FileInfo(installed).Length) System.IO.File.Copy(installed, copy, true);
                EphemerisManager.LoadSpk(copy);
                return true;
            }
            catch (Exception) { return false; }
        }

        /// <summary>22 July 2028 in the Kimberley against Xavier Jubier's Solar Eclipse Maps.</summary>
        private static void Kimberley2028(AstroTime greatest)
        {
            var be = BesselianElements.Compute(greatest);
            bool precise = EphemerisManager.HasHighPrecision;
            double tol = precise ? 2 : 90, durTol = precise ? 2 : 30;
            var points = new[]
            {
                new { Lat = -16.81566, Lon = 129.63405, T = new[] { 1, 31, 31.0, 3, 2, 37.2, 3, 4, 54.8, 3, 7, 12.5, 4, 36, 13.0 }, Dur = 275.3 },
                new { Lat = -15.62364, Lon = 126.76662, T = new[] { 1, 22, 55.3, 2, 53, 11.0, 2, 55, 45.7, 2, 58, 20.6, 4, 28, 43.0 }, Dur = 309.6 },
                new { Lat = -16.65373, Lon = 128.66009, T = new[] { 1, 28, 54.1, 2, 59, 35.5, 3, 2, 9.1, 3, 4, 42.8, 4, 33, 59.7 }, Dur = 307.3 },
            };
            foreach (var p in points)
            {
                var l = be.Local(p.Lat, p.Lon);
                string tag = p.Lat.ToString("0.00") + ", " + p.Lon.ToString("0.00") + ": ";
                CheckTrue(tag + "total", l.Covers && l.Kind == 1 && l.Visible);
                var contacts = new[] { l.C1, l.C2, l.Maximum, l.C3, l.C4 };
                var names = new[] { "C1", "C2", "maximum", "C3", "C4" };
                for (int i = 0; i < 5; i++)
                    Check(tag + names[i] + (precise ? " with DE440" : "") + " (s)", Sec(contacts[i], 2028, 7, 22, (int)p.T[3 * i], (int)p.T[3 * i + 1], p.T[3 * i + 2]), 0, tol);
                Check(tag + "totality" + (precise ? " with DE440" : "") + " (s)", l.CentralSeconds, p.Dur, durTol);
                CheckTrue(tag + "the Sun about 52° up at maximum", Math.Abs(l.Maximum.Altitude - 52.5) < 1.5, l.Maximum.Altitude.ToString("0.0"));
                Check(tag + "magnitude above 1", l.Magnitude, 1.025, 0.02);
                Check(tag + "all of the Sun covered", l.Obscuration, 1, 1e-9);
            }
        }

        [TestMethod]
        public void TestMapLines()
        {
            var gn = Greatest(2028, 7, 22);
            if (!gn.HasValue) { CheckTrue("Solar eclipse on 22 July 2028", false); return; }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var map = EclipseGeometry.Solar(gn.Value);
            CheckTrue("A solar eclipse map is worked out within 15 s", sw.Elapsed.TotalSeconds < 15, sw.Elapsed.TotalSeconds.ToString("0.0") + " s");
            var central = map.Lines.Where(l => l.Kind == EclipseLineKind.CentralLine).SelectMany(l => l.Parts).ToList();
            var limits = map.Lines.Where(l => l.Kind == EclipseLineKind.CentralLimit).SelectMany(l => l.Parts).ToList();
            CheckTrue("Central line and both limits", central.Count > 0 && limits.Count >= 2, central.Count + " / " + limits.Count);
            // every point of the limits is on the edge of the total phase
            var edge = limits.SelectMany(p => p).ToList();
            double worst = edge.Max(p => Math.Abs(map.Elements.Local(p.Lat, p.Lon).Centrality));
            Check("The limits are on the edge of the path (Earth radii)", worst, 0, 1e-4);
            // the path's width across the Kimberley (Xavier Jubier: 224.8 to 230.0 km near 128° E)
            var c = central.SelectMany(p => p).OrderBy(p => Math.Abs(p.Lon - 128.66)).First();
            var sides = edge.OrderBy(p => EclipseGeometry.DistanceKm(p, c)).ToList();
            var n = sides.First(); var s = sides.First(p => EclipseGeometry.DistanceKm(p, n) > 100);
            Check("Path width near 128.66° E (km)", EclipseGeometry.DistanceKm(n, s), 228, 8);
            foreach (var kind in new[] { EclipseLineKind.PenumbralLimit, EclipseLineKind.ContactAtHorizon, EclipseLineKind.MaximumAtHorizon })
                CheckTrue(kind + " drawn", map.Lines.Any(l => l.Kind == kind && l.Parts.Count > 0));
            CheckTrue("The map is centred on the eclipse (Australia in view)", map.West < 130 && map.East > 130 && map.South < -16 && map.North > -16, map.West + " " + map.East + " " + map.South + " " + map.North);
            CheckTrue("Greatest eclipse on the central line", map.GreatestPoint.HasValue && Math.Abs(map.Elements.Local(map.GreatestPoint.Value.Lat, map.GreatestPoint.Value.Lon).AxisDistance) < 0.002);

            // 28 August 2026: the Moon sets during the eclipse in Stockholm and is below the horizon in Tokyo the whole time
            var lunar = Events.LunarEclipses(AstroTime.CalendarToJd(2026, 8, 20), AstroTime.CalendarToJd(2026, 9, 5));
            if (lunar.Count != 1) { CheckTrue("Lunar eclipse in August 2026", false); return; }
            var lm = EclipseGeometry.Lunar(lunar[0].Time);
            Func<double, double, byte> cls = (lat, lon) => lm.Visibility[(int)Math.Floor(90 - lat), (int)Math.Floor(lon + 180)];
            CheckTrue("Contacts P1, U1, MAX, U4, P4", lm.SubLunar.Select(x => x.Item1).SequenceEqual(new[] { "P1", "U1", "MAX", "U4", "P4" }), string.Join(" ", lm.SubLunar.Select(x => x.Item1)));
            CheckTrue("Stockholm sees part of it", cls(59.33, 18.07) == 1, cls(59.33, 18.07).ToString());
            CheckTrue("Tokyo sees none of it", cls(35.68, 139.65) == 0, cls(35.68, 139.65).ToString());
            var max = lm.SubLunar.First(x => x.Item1 == "MAX").Item2;
            CheckTrue("Under the Moon at greatest eclipse it is all visible", cls(max.Lat, max.Lon) == 2);
            CheckTrue("A horizon line for each contact", lm.Lines.Count(l => l.Kind == EclipseLineKind.MoonHorizon) == 4);
        }

        [TestMethod]
        public void TestWorldMap()
        {
            // the built-in Natural Earth 1:110m map
            var map = Core.Util.WorldMap.BuiltIn();
            CheckTrue("The built-in map has land, lakes and borders", map.Land.Count > 100 && map.Lakes.Count > 10 && map.Borders.Count > 300, map.Land.Count + " / " + map.Lakes.Count + " / " + map.Borders.Count);
            CheckTrue("Stockholm and Alice Springs are on land", map.IsLand(59.33, 18.07) && map.IsLand(-23.70, 133.88));
            CheckTrue("The North Sea, the Pacific and Lake Victoria are not", !map.IsLand(56, 3) && !map.IsLand(0, -150) && !map.IsLand(-1, 33));
            CheckTrue("Made from Natural Earth 1:110m", map.Source == "Natural Earth 1:110m", map.Source);

            // GeoJSON as Natural Earth writes it, and the compact form it is kept in
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SidraluneWorldMapTest");
            System.IO.Directory.CreateDirectory(dir);
            string land = System.IO.Path.Combine(dir, "land.geojson"), places = System.IO.Path.Combine(dir, "places.geojson");
            System.IO.File.WriteAllText(land, "{\"type\":\"FeatureCollection\",\"features\":[{\"type\":\"Feature\",\"properties\":{},\"geometry\":{\"type\":\"MultiPolygon\",\"coordinates\":[[[[0,0],[10,0],[10,10],[0,10],[0,0]],[[4,4],[6,4],[6,6],[4,6],[4,4]]]]}}]}");
            System.IO.File.WriteAllText(places, "{\"type\":\"FeatureCollection\",\"features\":[{\"type\":\"Feature\",\"properties\":{\"scalerank\":3,\"NAME\":\"Smalltown\",\"pop_max\":1200},\"geometry\":{\"type\":\"Point\",\"coordinates\":[2.5,3.5]}},{\"type\":\"Feature\",\"properties\":{\"scalerank\":0,\"name\":\"Capital\",\"pop_max\":5000000.0},\"geometry\":{\"type\":\"Point\",\"coordinates\":[8,8]}}]}");
            var gj = Core.Util.WorldMap.FromGeoJson(land, null, null, places);
            CheckTrue("A polygon with a hole: land around it, not in it", gj.IsLand(2, 2) && !gj.IsLand(5, 5) && !gj.IsLand(20, 20));
            CheckTrue("Places by rank, with names and populations", gj.Places.Count == 2 && gj.Places[0].Name == "Capital" && gj.Places[0].Population == 5000000 && gj.Places[1].Name == "Smalltown" && gj.Places[1].Lat == 3.5f);
            var ms = new System.IO.MemoryStream();
            gj.Write(ms); ms.Position = 0;
            var back = Core.Util.WorldMap.Read(ms);
            CheckTrue("Kept and read back unchanged", back.Land.Count == 2 && back.Land[1].SequenceEqual(gj.Land[1]) && back.Places.Count == 2 && back.Places[1].Name == "Smalltown" && back.Source == gj.Source);

            // the Natural Earth datasets in Settings → Data
            foreach (var id in Core.Util.WorldMap.DatasetIds)
            {
                var d = Core.Util.DataManager.Get(id);
                CheckTrue(id + " listed, from a fixed Natural Earth release", d != null && d.Url.Contains("/natural-earth-vector/v5.1.2/geojson/") && d.Url.EndsWith(d.FileName) && !d.Recommended && d.License.Contains("Public domain"), d?.Url);
            }
        }

        [TestMethod]
        public void TestCentralLine()
        {
            // the shadow axis on the Earth, and a place on it is right under the axis at its maximum
            var gn = Greatest(2028, 7, 22);
            if (!gn.HasValue) { CheckTrue("Solar eclipse on 22 July 2028", false); return; }
            var g = gn.Value;
            bool loadedHere = UseInstalledDe440();
            try { CentralLine2028(g); }
            finally { if (loadedHere) EphemerisManager.UnloadSpk(); }
        }

        private static void CentralLine2028(AstroTime g)
        {
            var be = BesselianElements.Compute(g);
            double lat, lon;
            CheckTrue("The axis meets the Earth at greatest eclipse", be.AxisOnEarth(be.Hours(g.JdTT), out lat, out lon));
            var l = be.Local(lat, lon);
            Check("A point of the central line is on the axis at its maximum (Earth radii)", l.AxisDistance, 0, 0.002);
            CheckTrue("Total there", l.Kind == 1, l.Kind.ToString());
            // the central line crosses the Kimberley between the points Xavier Jubier's map shows inside the path
            bool found = false;
            for (double t = -3; t <= 3; t += 1 / 120.0)
                if (be.AxisOnEarth(t, out lat, out lon) && Math.Abs(lon - 128.66) < 0.05) { found = true; Check("The central line at 128.66° E (latitude)", lat, -16.65, EphemerisManager.HasHighPrecision ? 0.15 : 0.5); break; }
            CheckTrue("The central line reaches 128.66° E", found);
        }
    }
}
