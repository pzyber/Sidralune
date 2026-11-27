using System;
using System.Collections;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sidralune.Core;
using Sidralune.Core.Catalogs;
using Sidralune.Core.Coordinates;
using Sidralune.Core.Ephemeris;
using Sidralune.Core.Mathematics;
using Sidralune.Core.Time;
using Sidralune.Science.SolarSystem;
using Sidralune.Tests;
using Sidralune.UI;
using Sidralune.Views;
using static Sidralune.AppTests.AppHost;
using static Sidralune.Tests.Checks;

namespace Sidralune.AppTests
{
    /// <summary>What the sky chart draws near its edges.</summary>
    [TestClass]
    public class SkyChartTests : TestBase
    {
        // where a drawn object of this name is on screen, and its radius; null when it was not drawn
        private static Tuple<PointF, float> Drawn(SkyChart chart, string name)
        {
            foreach (var h in Field<IList>(chart, "_hits"))
            {
                var t = h.GetType();
                var o = (SelectedObject)t.GetField("O").GetValue(h);
                if (o != null && o.Name == name) return Tuple.Create((PointF)t.GetField("P").GetValue(h), (float)t.GetField("R").GetValue(h) - 2);
            }
            return null;
        }

        [TestMethod]
        public void TestSelectedMinorBodyDrawn()
        {
            // a faint asteroid, selected and centred as the Sky tab shows it, at a wide, a medium and a narrow field and at
            // a low and a deep magnitude limit: it is drawn at each, where the selection marker is, also in the first
            // frame, before the other minor bodies' positions are worked out in the background; a faint one that is not
            // selected keeps to the limit
            var time = AstroTime.FromDateTime(new DateTime(2026, 11, 18, 19, 43, 0, DateTimeKind.Utc));
            var store = Core.MinorBodies.MinorBodyStore.Instance;
            var ctx = new SkyContext(AppState.Site, time);
            var body = store.Asteroids.Select(m => new { m, p = Core.MinorBodies.MinorBodyStore.Place(ctx, m) }).Where(x => x.p.Magnitude > 9.5 && x.p.Magnitude < 13)
                .OrderBy(x => x.p.Magnitude).Select(x => x.m).FirstOrDefault();
            CheckTrue("A faint asteroid to show", body != null);
            if (body == null) return;
            var place = Core.MinorBodies.MinorBodyStore.Place(ctx, body);
            var sel = new SelectedObject { Kind = SelectedKind.MinorBody, Name = body.DisplayName, Minor = body, RaDeg = place.Astrometric.RaHours * 15, DecDeg = place.Astrometric.DecDeg, Mag = place.Magnitude };
            // another faint one, not selected, to see the limit still applies
            var other = store.Asteroids.FirstOrDefault(m => m != body && Core.MinorBodies.MinorBodyStore.Place(ctx, m).Magnitude > 8.6);
            foreach (var mode in new[] { ChartMode.Atlas, ChartMode.Planetarium })
            foreach (var lim in new[] { 6.5, 14.0 })
            foreach (var fov in new[] { 100.0, 2.25, 14.5 / 60 })
            {
                var r = OnUi(() =>
                {
                    using (var chart = new SkyChart { Mode = mode, Size = new Size(1600, 1000), FixedTime = time, Highlight = sel })
                    using (var bmp = new Bitmap(1600, 1000))
                    using (var g = Graphics.FromImage(bmp))
                    {
                        chart.Layers.AutoMagLimit = false; chart.Layers.MagLimit = lim;
                        chart.CenterOn(place.Astrometric.Ra, place.Astrometric.Dec, fov);
                        Func<Tuple<PointF, float>, double?> off = d => d == null ? (double?)null : Math.Sqrt(Math.Pow(d.Item1.X - 800, 2) + Math.Pow(d.Item1.Y - 500, 2));
                        // the first frame, while the positions are still being worked out in the background (they are handed
                        // back through the window)
                        var handle = chart.Handle;
                        chart.Render(g, 1600, 1000, Theme.Current, false);
                        var first = off(Drawn(chart, body.DisplayName));
                        var until = DateTime.Now.AddSeconds(20);
                        while (Field<bool>(chart, "_minorComputing") && DateTime.Now < until) { Application.DoEvents(); System.Threading.Thread.Sleep(20); }
                        chart.Render(g, 1600, 1000, Theme.Current, false);
                        var otherDrawn = other != null && Drawn(chart, other.DisplayName) != null;
                        return Tuple.Create(first, off(Drawn(chart, body.DisplayName)), otherDrawn);
                    }
                });
                string tag = body.DisplayName + " (mag " + place.Magnitude.ToString("0.0") + ") at " + AstroMath.FormatAngleAuto(fov) + ", limit " + lim + ", " + mode + ": ";
                CheckTrue(tag + "drawn in the first frame", r.Item1 != null);
                CheckTrue(tag + "drawn", r.Item2 != null);
                if (r.Item2 != null) CheckTrue(tag + "where it is selected, in the middle", r.Item2.Value < 3, r.Item2.Value.ToString("0.0") + " px");
                if (other != null && lim == 6.5) CheckTrue(tag + other.DisplayName + ", fainter than the limit and not selected, is left out", !r.Item3);
            }
        }

        [TestMethod]
        public void TestMinorBodiesLoadedLaterAreDrawn()
        {
            // a chart drawn before a comet is loaded (the catalogues load in the background at start-up, and comets can be
            // added by hand) shows it once it is there, without being selected and without the time moving on
            var time = AstroTime.FromDateTime(new DateTime(2026, 11, 18, 20, 30, 0, DateTimeKind.Utc));
            var store = Core.MinorBodies.MinorBodyStore.Instance;
            var ast = store.Asteroids.First();
            var comet = new Core.MinorBodies.MinorBody { Designation = "C/2026 Z8", Name = "C/2026 Z8 (test)", IsComet = true, Elements = ast.Elements, H = 2 };
            var place = Core.MinorBodies.MinorBodyStore.Place(new SkyContext(AppState.Site, time), comet);
            CheckTrue("The test comet is bright", place.Magnitude < 8, place.Magnitude.ToString("0.0"));
            var chart = OnUi(() => new SkyChart { Mode = ChartMode.Atlas, Size = new Size(1600, 1000), FixedTime = time });
            Func<bool> render = () => OnUi(() =>
            {
                using (var bmp = new Bitmap(1600, 1000))
                using (var g = Graphics.FromImage(bmp))
                {
                    chart.Render(g, 1600, 1000, Theme.Current, false);
                    var until = DateTime.Now.AddSeconds(20);
                    while (Field<bool>(chart, "_minorComputing") && DateTime.Now < until) { Application.DoEvents(); System.Threading.Thread.Sleep(20); }
                    chart.Render(g, 1600, 1000, Theme.Current, false);
                    return Drawn(chart, comet.DisplayName) != null;
                }
            });
            try
            {
                OnUi(() => { var h = chart.Handle; chart.Layers.AutoMagLimit = false; chart.Layers.MagLimit = 10; chart.CenterOn(place.Astrometric.Ra, place.Astrometric.Dec, 10); });
                CheckTrue("Not drawn before it is loaded", !render());
                store.Add(comet);
                CheckTrue("Drawn once it is loaded", render());
                store.Remove(comet);
                CheckTrue("Gone again once it is removed", !render());
            }
            finally { store.Remove(comet); OnUi(() => chart.Dispose()); }
        }

        [TestMethod]
        public void TestCometTailAwayFromSun()
        {
            // a comet (an asteroid's orbit with a comet's brightness), selected and centred: its tail keeps its direction as
            // the chart zooms, and points away from the Sun along the sky, in both chart modes
            var time = AstroTime.FromDateTime(new DateTime(2026, 11, 18, 20, 15, 0, DateTimeKind.Utc));
            var ast = Core.MinorBodies.MinorBodyStore.Instance.Asteroids.First();
            var comet = new Core.MinorBodies.MinorBody { Designation = "C/2026 Z9", Name = "C/2026 Z9 (test)", IsComet = true, Elements = ast.Elements, H = 8 };
            var ctx = new SkyContext(AppState.Site, time);
            var place = Core.MinorBodies.MinorBodyStore.Place(ctx, comet);
            var sun = ctx.BodyPlace(Body.Sun);
            var sel = new SelectedObject { Kind = SelectedKind.MinorBody, Name = comet.DisplayName, Minor = comet, RaDeg = place.Astrometric.RaHours * 15, DecDeg = place.Astrometric.DecDeg };
            foreach (var mode in new[] { ChartMode.Atlas, ChartMode.Planetarium })
            {
                var angles = new System.Collections.Generic.List<double>();
                foreach (var fov in new[] { 64.0, 51.2, 2.0, 14.5 / 60 })
                {
                    var r = OnUi(() =>
                    {
                        using (var chart = new SkyChart { Mode = mode, Size = new Size(1600, 1000), FixedTime = time, Highlight = sel })
                        using (var bmp = new Bitmap(1600, 1000))
                        using (var g = Graphics.FromImage(bmp))
                        {
                            chart.CenterOn(place.Astrometric.Ra, place.Astrometric.Dec, fov);
                            chart.Render(g, 1600, 1000, Theme.Current, false);
                            var d = Drawn(chart, comet.DisplayName);
                            if (d == null) return null;
                            double ang = (double)Call(chart, "TailAngle", place.Astrometric.ToVector().Normalized(), d.Item1);
                            // 20 px along the tail, back on the sky: farther from the Sun than the comet
                            double ra, dec;
                            chart.ScreenToIcrs(Point.Round(new PointF(d.Item1.X + 20 * (float)Math.Cos(ang), d.Item1.Y + 20 * (float)Math.Sin(ang))), out ra, out dec);
                            double from = AstroMath.Separation(sun.Astrometric.Ra, sun.Astrometric.Dec, place.Astrometric.Ra, place.Astrometric.Dec);
                            double tail = AstroMath.Separation(sun.Astrometric.Ra, sun.Astrometric.Dec, ra, dec);
                            return Tuple.Create(ang, tail - from, 20 * fov / 1600 * AstroMath.Deg2Rad);
                        }
                    });
                    string tag = mode + " at " + AstroMath.FormatAngleAuto(fov) + ": ";
                    CheckTrue(tag + "the comet is drawn", r != null);
                    if (r == null) continue;
                    angles.Add(r.Item1);
                    // nearly the whole 20 px away from the Sun (a pixel's rounding allowed)
                    CheckTrue(tag + "the tail points away from the Sun", r.Item2 > 0.8 * r.Item3, (r.Item2 * AstroMath.Rad2Deg * 3600).ToString("0.0") + "\" of " + (r.Item3 * AstroMath.Rad2Deg * 3600).ToString("0.0") + "\"");
                }
                // the same direction at every zoom (within 1°)
                double spread = angles.Count < 2 ? 0 : angles.Max(a => angles.Max(b => Math.Abs(Math.IEEERemainder(a - b, 2 * Math.PI)))) * AstroMath.Rad2Deg;
                CheckTrue(mode + ": the tail keeps its direction when zooming", spread < 1, spread.ToString("0.00") + "°");
            }
        }

        [TestMethod]
        public void TestJupiterPartlyInView()
        {
            var time = AstroTime.FromDateTime(new DateTime(2026, 10, 31, 1, 0, 0, DateTimeKind.Utc));
            var result = OnUi(() =>
            {
                using (var chart = new SkyChart { Mode = ChartMode.Atlas, Size = new Size(1600, 1000), FixedTime = time })
                using (var bmp = new Bitmap(1600, 1000))
                using (var g = Graphics.FromImage(bmp))
                {
                    var jup = new SkyContext(AppState.Site, time).BodyPlace(Body.Jupiter).Astrometric;
                    chart.CenterOn(jup.Ra, jup.Dec, 0.1);
                    // move the view so Jupiter's middle is 40 pixels left of the chart, its disc still reaching in
                    double ra, dec;
                    chart.ScreenToIcrs(new Point(1600 + 40, 500), out ra, out dec);
                    chart.CenterOn(ra, dec);
                    chart.Render(g, 1600, 1000, Theme.Current, false);
                    var j = Drawn(chart, "Jupiter");
                    if (j == null) return "Jupiter was not drawn";
                    if (j.Item1.X > -20 || j.Item1.X + j.Item2 < 10) return "Jupiter at x = " + j.Item1.X + " with radius " + j.Item2 + " does not test the edge";
                    // the moons in view are drawn (a 4-pixel dot in the text colour)
                    var moons = GalileanMoons.Names.Select(n => Drawn(chart, n)).Where(m => m != null).Select(m => m.Item1)
                        .Where(p => p.X > 5 && p.X < 1595 && p.Y > 5 && p.Y < 995).ToList();
                    if (moons.Count == 0) return "no moon in view to test";
                    var bg = bmp.GetPixel(1599, 999).ToArgb();
                    var missing = moons.Where(p => bmp.GetPixel((int)Math.Round(p.X), (int)Math.Round(p.Y)).ToArgb() == bg).ToList();
                    return missing.Count == 0 ? null : missing.Count + " of " + moons.Count + " moons in view not drawn, e.g. at " + missing[0];
                }
            });
            CheckTrue("Jupiter and its moons are drawn while its middle is just outside the chart", result == null, result);
        }

        // the planet centred in a 1600 x 1000 atlas chart of this width (deg): the planet's drawn radius and, for each
        // moon of it drawn, the moon, what a click there selects and its drawn distance from the planet (arcsec)
        private static Tuple<double, System.Collections.Generic.List<Tuple<MoonPlace, SelectedObject, double>>> DrawnMoons(AstroTime time, Body planet, double fovDeg, SelectedObject highlight = null, bool deeper = true, double magLimit = 6.5)
        {
            var ctx = new SkyContext(AppState.Site, time);
            var pl = ctx.BodyPlace(planet);
            var places = PlanetMoons.Places(ctx, planet);
            return OnUi(() =>
            {
                using (var chart = new SkyChart { Mode = ChartMode.Atlas, Size = new Size(1600, 1000), FixedTime = time, Highlight = highlight })
                using (var bmp = new Bitmap(1600, 1000))
                using (var g = Graphics.FromImage(bmp))
                {
                    chart.Layers.AutoMagLimit = deeper; chart.Layers.MagLimit = magLimit;
                    chart.CenterOn(pl.Astrometric.Ra, pl.Astrometric.Dec, fovDeg);
                    chart.Render(g, 1600, 1000, Theme.Current, false);
                    var p = Drawn(chart, planet.ToString());
                    double scale = Field<double>(chart, "_scale"); // pixels per radian
                    var res = places.Select(m => new { m, d = Drawn(chart, m.Moon.Name) }).Where(x => x.d != null).Select(x =>
                    {
                        double px = Math.Sqrt(Math.Pow(x.d.Item1.X - p.Item1.X, 2) + Math.Pow(x.d.Item1.Y - p.Item1.Y, 2));
                        return Tuple.Create(x.m, chart.HitTest(Point.Round(x.d.Item1)), px / scale * AstroMath.Rad2ArcSec);
                    }).ToList();
                    return Tuple.Create(p.Item2 / scale * AstroMath.Rad2ArcSec, res);
                }
            });
        }

        [TestMethod]
        public void TestPlanetMoonsClickable()
        {
            var time = AstroTime.FromDateTime(new DateTime(2026, 10, 31, 1, 0, 0, DateTimeKind.Utc));
            var ctx = new SkyContext(AppState.Site, time);
            var jup = ctx.BodyPlace(Body.Jupiter);
            PlanetPhysics.Fill(Body.Jupiter, jup, ctx.JdTT);
            Check("Jupiter is drawn at its apparent radius (\")", DrawnMoons(time, Body.Jupiter, 0.25).Item1, jup.DiameterArcsec / 2, 0.6);
            // fields that show each planet's classical moons apart from it
            var fields = new[] { Tuple.Create(Body.Mars, 0.02), Tuple.Create(Body.Jupiter, 0.5), Tuple.Create(Body.Saturn, 0.4), Tuple.Create(Body.Uranus, 0.05), Tuple.Create(Body.Neptune, 0.05), Tuple.Create(Body.Pluto, 0.01) };
            foreach (var f in fields)
            {
                var drawn = DrawnMoons(time, f.Item1, f.Item2);
                double half = f.Item2 / 2 * 3600 * 1000 / 1600; // half the chart's height, arcsec
                var shown = PlanetMoons.Places(ctx, f.Item1).Where(m => m.Moon.Major && !m.Occulted && m.SeparationArcsec < half && m.PlanetRadii > 1.5).ToList();
                var missing = shown.Where(m => !drawn.Item2.Any(d => d.Item1.Moon == m.Moon)).Select(m => m.Moon.Name).ToList();
                CheckTrue(f.Item1 + ": the classical moons in view are drawn", missing.Count == 0 && shown.Count > 0, shown.Count + " in view, missing " + string.Join(", ", missing));
                foreach (var d in drawn.Item2)
                {
                    var m = d.Item1;
                    // a click selects the moon, or one drawn on top of it (Naiad and Thalassa are seen nearly in line)
                    var hit = d.Item2 == null ? null : drawn.Item2.FirstOrDefault(o => o.Item1.Moon.Name == d.Item2.Name);
                    bool onTop = hit != null && Math.Abs(hit.Item1.EastArcsec - m.EastArcsec) * 1600 / (f.Item2 * 3600) < 1.5 && Math.Abs(hit.Item1.NorthArcsec - m.NorthArcsec) * 1600 / (f.Item2 * 3600) < 1.5;
                    CheckTrue(m.Moon.Name + ": a click on it selects it", d.Item2 != null && d.Item2.Kind == SelectedKind.PlanetMoon && (d.Item2.Name == m.Moon.Name || onTop) && d.Item2.Planet == f.Item1,
                        d.Item2 == null ? "nothing" : d.Item2.Kind + " " + d.Item2.Name);
                    Check(m.Moon.Name + ": drawn at its distance from " + f.Item1 + " (\")", d.Item3, m.SeparationArcsec, Math.Max(0.05, 0.01 * m.SeparationArcsec));
                }
            }
            // the moons the user named are all there
            var saturn = DrawnMoons(time, Body.Saturn, 0.4).Item2.Select(d => d.Item1.Moon.Name).ToList();
            var named = new[] { "Titan", "Rhea", "Tethys", "Dione", "Enceladus", "Iapetus", "Mimas" }.Where(n => { var m = PlanetMoons.Place(ctx, PlanetMoons.Find(n)); return !m.Occulted && m.PlanetRadii > 1.5 && m.SeparationArcsec < 450; }).ToList();
            CheckTrue("Saturn: " + string.Join(", ", named), named.All(saturn.Contains), string.Join(", ", saturn));
            // without Deeper when zoomed in, a faint moon only when it is bright enough or selected
            var jupiter = DrawnMoons(time, Body.Jupiter, 0.05, deeper: false).Item2;
            var adrastea = PlanetMoons.Place(ctx, PlanetMoons.Find("Adrastea"));
            CheckTrue("Adrastea (magnitude " + adrastea.Mag.ToString("0.0") + ") is not drawn at 3′ with a fixed limit", !jupiter.Any(d => d.Item1.Moon.Name == "Adrastea"));
            CheckTrue("…but is when selected", adrastea.Occulted || DrawnMoons(time, Body.Jupiter, 0.05, SelectedObject.From(adrastea), deeper: false).Item2.Any(d => d.Item1.Moon.Name == "Adrastea"));
            CheckTrue("…and with Deeper when zoomed in", adrastea.Occulted || DrawnMoons(time, Body.Jupiter, 0.05).Item2.Any(d => d.Item1.Moon.Name == "Adrastea"));
        }

        [TestMethod]
        public void TestDeeperWhenZoomedIn()
        {
            // whatever limit is chosen, zoomed in all the way every moon and every catalogue star is drawn
            var time = AstroTime.FromDateTime(new DateTime(2026, 11, 1, 1, 0, 0, DateTimeKind.Utc));
            var ctx = new SkyContext(AppState.Site, time);
            double cap = CatalogManager.Instance.StarLimitingMagnitude + 0.5;
            foreach (double lim in new[] { 4.0, 6.5, 12 })
            {
                var limits = OnUi(() =>
                {
                    using (var chart = new SkyChart { Size = new Size(1600, 1000) })
                    {
                        chart.Layers.MagLimit = lim;
                        Func<double, double> at = fov => { chart.FovDeg = fov; return chart.EffectiveMagLimit; };
                        var r = new[] { at(90), at(2), at(SkyChart.MinFovDeg), 0 };
                        chart.Layers.AutoMagLimit = false;
                        r[3] = at(SkyChart.MinFovDeg);
                        return r;
                    }
                });
                Check("limit " + lim + ": at 90°", limits[0], Math.Min(lim, cap), 1e-9);
                Check("limit " + lim + ": at 2°, as before", limits[1], Math.Min(lim + 2.2 * Math.Log10(45), cap), 1e-9);
                Check("limit " + lim + ": at 36″, the catalogue's faintest star", limits[2], cap, 1e-9);
                Check("limit " + lim + ": at 36″ without Deeper when zoomed in", limits[3], lim, 1e-9);
            }
            // Neptune's moons at 36″ with the limit at 4: all of them clear of its disc and in view
            var drawn = DrawnMoons(time, Body.Neptune, SkyChart.MinFovDeg, magLimit: 4).Item2;
            double half = SkyChart.MinFovDeg / 2 * 3600 * 1000 / 1600 - 0.5;
            var shown = PlanetMoons.Places(ctx, Body.Neptune).Where(m => !m.Occulted && Math.Abs(m.NorthArcsec) < half && Math.Abs(m.EastArcsec) < half * 1.6 && m.PlanetRadii > 1.1).ToList();
            var missing = shown.Where(m => !drawn.Any(d => d.Item1.Moon == m.Moon)).Select(m => m.Moon.Name + " (" + m.Mag.ToString("0.0") + ")").ToList();
            CheckTrue("Neptune: every moon in view is drawn at 36″ with the limit at 4", shown.Count >= 3 && missing.Count == 0, shown.Count + " in view, missing " + string.Join(", ", missing));
            // the faintest catalogue star, at 36″ with the limit at 4
            var faintest = CatalogManager.Instance.Stars.All().Where(s => !double.IsNaN(s.Mag)).OrderByDescending(s => s.Mag).First();
            var hit = OnUi(() =>
            {
                using (var chart = new SkyChart { Mode = ChartMode.Atlas, Size = new Size(1600, 1000), FixedTime = time })
                using (var bmp = new Bitmap(1600, 1000))
                using (var g = Graphics.FromImage(bmp))
                {
                    chart.Layers.MagLimit = 4;
                    chart.CenterOn(faintest.Ra, faintest.Dec, SkyChart.MinFovDeg);
                    chart.Render(g, 1600, 1000, Theme.Current, false);
                    return chart.HitTest(new Point(800, 500));
                }
            });
            CheckTrue("the faintest catalogue star (magnitude " + faintest.Mag.ToString("0.0") + ") is drawn at 36″ with the limit at 4",
                hit != null && hit.Kind == SelectedKind.Star && Math.Abs(hit.Mag - faintest.Mag) < 0.01, hit == null ? "nothing" : hit.Kind + " " + hit.Name + " " + hit.Mag);
        }

        [TestMethod]
        public void TestFarMoonZoomedIn()
        {
            // Iapetus 6.8′ from Saturn: zoomed in on the moon, Saturn is thousands of pixels outside the chart
            var time = AstroTime.FromDateTime(new DateTime(2026, 11, 1, 1, 8, 0, DateTimeKind.Utc));
            var iapetus = PlanetMoons.Place(new SkyContext(AppState.Site, time), PlanetMoons.Find("Iapetus"));
            CheckTrue("Iapetus is far from Saturn", iapetus.SeparationArcsec > 300, iapetus.SeparationArcsec.ToString("0") + "\"");
            foreach (double fov in new[] { 0.075, 0.06, 0.02, SkyChart.MinFovDeg })
            {
                var result = OnUi(() =>
                {
                    using (var chart = new SkyChart { Mode = ChartMode.Atlas, Size = new Size(1600, 1000), FixedTime = time, Highlight = SelectedObject.From(iapetus) })
                    using (var bmp = new Bitmap(1600, 1000))
                    using (var g = Graphics.FromImage(bmp))
                    {
                        chart.CenterOn(iapetus.Place.Astrometric.Ra, iapetus.Place.Astrometric.Dec, fov);
                        chart.Render(g, 1600, 1000, Theme.Current, false);
                        var m = Drawn(chart, "Iapetus");
                        if (m == null) return "Iapetus was not drawn";
                        if (Math.Abs(m.Item1.X - 800) > 3 || Math.Abs(m.Item1.Y - 500) > 3) return "Iapetus drawn at " + m.Item1 + ", not in the middle";
                        var hit = chart.HitTest(new Point(800, 500));
                        if (hit == null || hit.Name != "Iapetus") return "a click on it selects " + (hit == null ? "nothing" : hit.Name);
                        return Drawn(chart, "Saturn") != null ? "Saturn was drawn although it is outside the chart" : null;
                    }
                });
                CheckTrue("Iapetus is drawn and clickable at " + SkyView.FormatOffset(fov * 3600), result == null, result);
            }
        }

        [TestMethod]
        public void TestStarDotSizes()
        {
            // the default chart keeps the sizes it had
            for (double m = -1.5; m <= 6.5; m += 0.5)
                Check("limit 6.5: mag " + m + " as before (px)", SkyChart.StarDotRadius(m, 6.5), Math.Max(0.7, 0.55 * Math.Pow(7.7 - m, 0.95)), 1e-5);
            var limits = new[] { 4.0, 6.5, 9, 12, 16, 21.5 };
            foreach (double lim in limits)
            {
                // as in an atlas: the faintest star shown the smallest dot, Sirius the largest, whatever the limit
                Check("limit " + lim + ": the faintest star the smallest dot (px)", SkyChart.StarDotRadius(lim, lim), 0.7, 1e-6);
                Check("limit " + lim + ": Sirius the largest (px)", SkyChart.StarDotRadius(-1.5, lim), SkyChart.StarDotRadius(-1.5, 6.5), 1e-5);
                bool graded = true;
                for (double m = -1.5; m < lim; m += 0.25) graded &= SkyChart.StarDotRadius(m, lim) >= SkyChart.StarDotRadius(m + 0.25, lim);
                CheckTrue("limit " + lim + ": brighter stars never smaller", graded);
            }
            // a deep limit squeezes the sizes instead of growing every star (as they used to, which made a mass of large dots)
            foreach (double lim in limits.Where(l => l > 6.5))
            {
                bool smaller = true;
                for (double m = -1; m <= lim; m += 0.25) smaller &= SkyChart.StarDotRadius(m, lim) <= Math.Max(0.7, 0.55 * Math.Pow(lim + 1.2 - m, 0.95)) + 1e-6;
                CheckTrue("limit " + lim + ": no star bigger than it used to be", smaller);
            }
            Check("limit 12: a mag 6 star (px)", SkyChart.StarDotRadius(6, 12), 1.7, 0.2);
            Check("limit 12: a mag 9 star (px)", SkyChart.StarDotRadius(9, 12), 1.0, 0.2);
            // zoomed in all the way every limit ends at the same one, so the chart looks the same
            var time = AstroTime.FromDateTime(new DateTime(2026, 11, 1, 1, 43, 0, DateTimeKind.Utc));
            var betelgeuse = CatalogManager.Instance.Find("Betelgeuse");
            var shots = new[] { 4.0, 6.5, 12 }.Select(lim => OnUi(() =>
            {
                using (var chart = new SkyChart { Mode = ChartMode.Atlas, Size = new Size(1600, 1000), FixedTime = time })
                using (var bmp = new Bitmap(1600, 1000))
                using (var g = Graphics.FromImage(bmp))
                {
                    chart.Layers.MagLimit = lim;
                    chart.CenterOn(betelgeuse.Ra, betelgeuse.Dec, SkyChart.MinFovDeg);
                    chart.Render(g, 1600, 1000, Theme.Current, false);
                    var px = new int[200 * 200];
                    for (int x = 0; x < 200; x++) for (int y = 0; y < 200; y++) px[x * 200 + y] = bmp.GetPixel(700 + x, 400 + y).ToArgb();
                    return px;
                }
            })).ToList();
            CheckTrue("at 36″, limits 4, 6.5 and 12 draw Betelgeuse alike", shots[0].SequenceEqual(shots[1]) && shots[1].SequenceEqual(shots[2]));
            CheckTrue("…and it is drawn", shots[0].Distinct().Count() > 2);
            // stars grow as you zoom in, smoothly, to 2.5 times their size at the narrowest field
            Check("zoom factor at 90° and wider", SkyChart.StarZoomFactor(90), 1, 1e-6);
            Check("…at 160°", SkyChart.StarZoomFactor(160), 1, 1e-6);
            Check("…at 9°", SkyChart.StarZoomFactor(9), 1 + 1.5 / Math.Log10(9000), 1e-6);
            Check("…at 36″", SkyChart.StarZoomFactor(SkyChart.MinFovDeg), 2.5, 1e-6);
            var fovs = new[] { 60, 10, 2, 0.3, 0.05, SkyChart.MinFovDeg };
            var sizes = fovs.Select(fov => OnUi(() =>
            {
                using (var chart = new SkyChart { Mode = ChartMode.Atlas, Size = new Size(1600, 1000), FixedTime = time })
                using (var bmp = new Bitmap(1600, 1000))
                using (var g = Graphics.FromImage(bmp))
                {
                    chart.CenterOn(betelgeuse.Ra, betelgeuse.Dec, fov);
                    chart.Render(g, 1600, 1000, Theme.Current, false);
                    var d = Drawn(chart, "Betelgeuse");
                    return d == null ? 0 : d.Item2;
                }
            })).ToList();
            bool grows = sizes[0] > 0;
            for (int k = 1; k < sizes.Count; k++) grows &= sizes[k] > sizes[k - 1];
            CheckTrue("Betelgeuse grows as you zoom in from 60° to 36″", grows, string.Join(", ", sizes.Select(s => s.ToString("0.0"))));
        }

        [TestMethod]
        public void TestMilkyWayZoomedIn()
        {
            // the share of the chart the Milky Way shades (a grid of pixels that differ with it turned off), and the shade
            // in the middle, centred on galactic latitude b (deg) in Monoceros
            Func<double, double, Tuple<double, int>> shaded = (b, fov) => OnUi(() =>
            {
                double ra, dec;
                Transforms.GalacticToEquatorial(203 * AstroMath.Deg2Rad, b * AstroMath.Deg2Rad, out ra, out dec);
                var shots = new Bitmap[2];
                for (int k = 0; k < 2; k++)
                    using (var chart = new SkyChart { Mode = ChartMode.Atlas, Size = new Size(1600, 1000) })
                    using (var g = Graphics.FromImage(shots[k] = new Bitmap(1600, 1000)))
                    {
                        chart.Layers.Stars = false; chart.Layers.DeepSky = false; chart.Layers.MilkyWay = k == 1;
                        chart.CenterOn(ra, dec, fov);
                        chart.Render(g, 1600, 1000, Theme.Current, false);
                    }
                int differ = 0, all = 0;
                for (int x = 10; x < 1600; x += 20)
                    for (int y = 10; y < 1000; y += 20, all++)
                        if (shots[0].GetPixel(x, y).ToArgb() != shots[1].GetPixel(x, y).ToArgb()) differ++;
                var mid = shots[1].GetPixel(803, 497);
                foreach (var s in shots) s.Dispose();
                return Tuple.Create((double)differ / all, mid.R + mid.G + mid.B);
            });
            // in the plane the whole chart is shaded at every zoom; it used to break up below 2° and vanish by 1°
            var inPlane = new[] { 30, 2.25, 1.8, 1.15, 0.1, SkyChart.MinFovDeg }.Select(fov => Tuple.Create(fov, shaded(0, fov))).ToList();
            foreach (var s in inPlane.Skip(1))
                CheckTrue("in the plane, all of the chart shaded at " + SkyView.FormatOffset(s.Item1 * 3600), s.Item2.Item1 > 0.98, (s.Item2.Item1 * 100).ToString("0") + "%");
            CheckTrue("…as brightly at every zoom", inPlane.Skip(1).All(s => s.Item2.Item2 == inPlane[1].Item2.Item2), string.Join(", ", inPlane.Select(s => s.Item2.Item2)));
            CheckTrue("…and as a band, not all of it, at 30°", inPlane[0].Item2.Item1 > 0.2 && inPlane[0].Item2.Item1 < 0.98, (inPlane[0].Item2.Item1 * 100).ToString("0") + "%");
            // 6° from the plane only the outer strip, fainter; 20° away none
            var outer = shaded(6, 0.5);
            CheckTrue("6° from the plane, shaded by the outer strip only", outer.Item1 > 0.98 && outer.Item2 < inPlane[1].Item2.Item2, (outer.Item1 * 100).ToString("0") + "%, shade " + outer.Item2);
            CheckTrue("20° from the plane, not shaded", shaded(20, 0.5).Item1 < 0.01);
        }

        [TestMethod]
        public void TestSelectedBodyFollowsTime()
        {
            // the Moon, the Sun or a planet selected and then the time moved on: the selection marker and Center go to where
            // it is at the new time, not where it was when it was selected (the Moon moves about its own width in an hour)
            var sky = View<SkyView>("sky");
            var t0 = AstroTime.FromDateTime(new DateTime(2026, 11, 27, 21, 12, 0, DateTimeKind.Utc));
            var t1 = t0.AddHours(2);
            try
            {
                foreach (var b in new[] { Body.Moon, Body.Sun, Body.Mars })
                {
                    var r = OnUi(() =>
                    {
                        AppState.SetTime(t0);
                        var o = SkyView.PlanetObject(b);
                        AppState.Select(o);
                        AppState.SetTime(t1);
                        var marked = sky.Chart.Highlight;
                        // Center
                        sky.Goto(Field<SelectedObject>(sky, "_sel"));
                        // the chart's middle on the sky, as the next frame draws it
                        Call(sky.Chart, "Setup", sky.Chart.Width, sky.Chart.Height);
                        var c = (Vec3)Call(sky.Chart, "CenterIcrs");
                        double ra, dec;
                        c.ToSpherical(out ra, out dec);
                        return Tuple.Create(marked, ra, dec);
                    });
                    var now = new SkyContext(AppState.Site, t1).BodyPlace(b).Astrometric;
                    double off = AstroMath.Separation(r.Item1.RaDeg * AstroMath.Deg2Rad, r.Item1.DecDeg * AstroMath.Deg2Rad, now.Ra, now.Dec) * AstroMath.Rad2Deg * 3600;
                    CheckTrue(b + ": marked where it is two hours later", off < 1, off.ToString("0.0") + "\"");
                    double centre = AstroMath.Separation(r.Item2, r.Item3, now.Ra, now.Dec) * AstroMath.Rad2Deg * 3600;
                    // (the planetarium's middle goes through the horizon and refraction and back: some arcseconds; the Moon
                    // was a degree off before)
                    CheckTrue(b + ": Center goes there", centre < 60, centre.ToString("0.0") + "\"");
                }
            }
            finally { OnUi(() => AppState.GoLive()); }
        }

        [TestMethod]
        public void TestPlanetMoonInfo()
        {
            // the info panel describes a clicked moon
            var sky = View<SkyView>("sky");
            Func<string, Tuple<string, string, string>> info = name => OnUi(() =>
            {
                Call(sky, "ShowInfo", SkyView.MoonObject(name));
                return Tuple.Create(Field<Control>(sky, "_objTitle").Text, Field<Control>(sky, "_objSub").Text, Field<Control>(sky, "_objInfo").Text);
            });
            foreach (var t in new[] { Tuple.Create("ganymede", "Ganymede", "Jupiter"), Tuple.Create("Titan", "Titan", "Saturn"), Tuple.Create("titania", "Titania", "Uranus"), Tuple.Create("Triton", "Triton", "Neptune"), Tuple.Create("Charon", "Charon", "Pluto"), Tuple.Create("Phobos", "Phobos", "Mars") })
            {
                var i = info(t.Item1);
                CheckTrue(t.Item2 + ": the moon's name and what it orbits", i.Item1 == t.Item2 && i.Item2.StartsWith("Moon of " + t.Item3), i.Item1 + " / " + i.Item2);
                CheckTrue(t.Item2 + ": magnitude, place, position around " + t.Item3 + ", size and orbit",
                    new[] { "Magnitude", "RA/Dec J2000", "Alt/Az", "Rise", "Position", t.Item3, "Diameter", "km", "Orbital period", "days" }.All(s => i.Item3.Contains(s)) && !i.Item3.Contains("Magnitude       —"), i.Item3);
                CheckTrue(t.Item2 + ": retrograde only for Triton", i.Item3.Contains("(retrograde)") == (t.Item2 == "Triton"));
            }
            // in an observing list, a moon stays a moon (not its planet, which the entry also names)
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
            foreach (var name in new[] { "Europa", "Titania" })
            {
                var entry = typeof(PlannerView).GetMethod("FromSky", flags).Invoke(null, new object[] { SkyView.MoonObject(name) });
                var back = (SelectedObject)typeof(PlannerView).GetMethod("ToSelected", flags).Invoke(null, new[] { entry });
                CheckTrue("Planner: a listed " + name + " comes back as that moon", back.Kind == SelectedKind.PlanetMoon && back.Name == name, back.Kind + " " + back.Name);
            }
            CheckTrue("Moons of all planets are found by name, planets and the Moon are not", new[] { "Io", "Titan", "Miranda", "Triton", "Nix", "Deimos" }.All(n => SkyView.MoonObject(n) != null)
                && SkyView.MoonObject("Jupiter") == null && SkyView.MoonObject("Moon") == null);
            CheckTrue("Distances from the planet in arcseconds, arcminutes or degrees", SkyView.FormatOffset(12.34) == 12.3.ToString("0.0") + "\"" && SkyView.FormatOffset(600) == 10.0.ToString("0.0") + "′" && SkyView.FormatOffset(7200) == 2.0.ToString("0.00") + "°");
        }
    }
}
