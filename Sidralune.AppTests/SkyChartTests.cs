using System;
using System.Collections;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sidralune.Core;
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
        private static Tuple<double, System.Collections.Generic.List<Tuple<MoonPlace, SelectedObject, double>>> DrawnMoons(AstroTime time, Body planet, double fovDeg, SelectedObject highlight = null)
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
                    CheckTrue(m.Moon.Name + ": a click on it selects it", d.Item2 != null && d.Item2.Kind == SelectedKind.PlanetMoon && d.Item2.Name == m.Moon.Name && d.Item2.Planet == f.Item1,
                        d.Item2 == null ? "nothing" : d.Item2.Kind + " " + d.Item2.Name);
                    Check(m.Moon.Name + ": drawn at its distance from " + f.Item1 + " (\")", d.Item3, m.SeparationArcsec, Math.Max(0.05, 0.01 * m.SeparationArcsec));
                }
            }
            // the moons the user named are all there
            var saturn = DrawnMoons(time, Body.Saturn, 0.4).Item2.Select(d => d.Item1.Moon.Name).ToList();
            var named = new[] { "Titan", "Rhea", "Tethys", "Dione", "Enceladus", "Iapetus", "Mimas" }.Where(n => { var m = PlanetMoons.Place(ctx, PlanetMoons.Find(n)); return !m.Occulted && m.PlanetRadii > 1.5 && m.SeparationArcsec < 450; }).ToList();
            CheckTrue("Saturn: " + string.Join(", ", named), named.All(saturn.Contains), string.Join(", ", saturn));
            // a faint moon only when it is bright enough or selected
            var jupiter = DrawnMoons(time, Body.Jupiter, 0.05).Item2;
            var adrastea = PlanetMoons.Place(ctx, PlanetMoons.Find("Adrastea"));
            CheckTrue("Adrastea (magnitude " + adrastea.Mag.ToString("0.0") + ") is not drawn at 3′", !jupiter.Any(d => d.Item1.Moon.Name == "Adrastea"));
            CheckTrue("…but is when selected", adrastea.Occulted || DrawnMoons(time, Body.Jupiter, 0.05, SelectedObject.From(adrastea)).Item2.Any(d => d.Item1.Moon.Name == "Adrastea"));
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
