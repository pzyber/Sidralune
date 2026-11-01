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
