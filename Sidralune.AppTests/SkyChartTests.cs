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

        [TestMethod]
        public void TestJupiterMoonsClickable()
        {
            var time = AstroTime.FromDateTime(new DateTime(2026, 10, 31, 1, 0, 0, DateTimeKind.Utc));
            var ctx = new SkyContext(AppState.Site, time);
            var expected = GalileanMoons.Positions(ctx);
            var jup = ctx.BodyPlace(Body.Jupiter);
            PlanetPhysics.Fill(Body.Jupiter, jup, ctx.JdTT);
            var found = OnUi(() =>
            {
                using (var chart = new SkyChart { Mode = ChartMode.Atlas, Size = new Size(1600, 1000), FixedTime = time })
                using (var bmp = new Bitmap(1600, 1000))
                using (var g = Graphics.FromImage(bmp))
                {
                    chart.CenterOn(jup.Astrometric.Ra, jup.Astrometric.Dec, 0.25);
                    chart.Render(g, 1600, 1000, Theme.Current, false);
                    var j = Drawn(chart, "Jupiter");
                    double scale = Field<double>(chart, "_scale"); // pixels per radian
                    var res = expected.Where(m => !m.Occulted).Select(m =>
                    {
                        var d = Drawn(chart, m.Name);
                        var hit = d == null ? null : chart.HitTest(Point.Round(d.Item1));
                        double px = d == null ? double.NaN : Math.Sqrt(Math.Pow(d.Item1.X - j.Item1.X, 2) + Math.Pow(d.Item1.Y - j.Item1.Y, 2));
                        return Tuple.Create(m, hit, px / scale * AstroMath.Rad2Deg * 3600);
                    }).ToList();
                    return Tuple.Create(res, (double)j.Item2 / scale * AstroMath.Rad2Deg * 3600);
                }
            });
            Check("Jupiter is drawn at its apparent radius (\")", found.Item2, jup.DiameterArcsec / 2, 0.6);
            CheckTrue("Some moons are visible", found.Item1.Count >= 2, found.Item1.Count + " moons");
            foreach (var f in found.Item1)
            {
                CheckTrue(f.Item1.Name + ": a click on it selects it", f.Item2 != null && f.Item2.Kind == SelectedKind.PlanetMoon && f.Item2.Name == f.Item1.Name && f.Item2.Planet == Body.Jupiter,
                    f.Item2 == null ? "nothing" : f.Item2.Kind + " " + f.Item2.Name);
                Check(f.Item1.Name + ": drawn at its distance from Jupiter (\")", f.Item3, f.Item1.SeparationArcsec, 1.5);
            }

            // the info panel describes a clicked moon
            var sky = View<SkyView>("sky");
            var info = OnUi(() =>
            {
                Call(sky, "ShowInfo", SkyView.MoonObject("ganymede"));
                return Tuple.Create(Field<Control>(sky, "_objTitle").Text, Field<Control>(sky, "_objSub").Text, Field<Control>(sky, "_objInfo").Text);
            });
            CheckTrue("Info: the moon's name and what it orbits", info.Item1 == "Ganymede" && info.Item2.StartsWith("Moon of Jupiter"), info.Item1 + " / " + info.Item2);
            CheckTrue("Info: magnitude, place, position around Jupiter, size and orbit", new[] { "Magnitude", "RA/Dec J2000", "Alt/Az", "Rise", "Position", "of Jupiter", "Diameter", "km", "Orbital period", "days" }
                .All(s => info.Item3.Contains(s)) && !info.Item3.Contains("Magnitude       —"), info.Item3);
            // in an observing list, a moon stays a moon (not Jupiter, which the entry also names)
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
            var entry = typeof(PlannerView).GetMethod("FromSky", flags).Invoke(null, new object[] { SkyView.MoonObject("Europa") });
            var back = (SelectedObject)typeof(PlannerView).GetMethod("ToSelected", flags).Invoke(null, new[] { entry });
            CheckTrue("Planner: a listed moon comes back as that moon", back.Kind == SelectedKind.PlanetMoon && back.Name == "Europa", back.Kind + " " + back.Name);
            CheckTrue("Only Jupiter's four big moons are found by name", SkyView.MoonObject("Io") != null && SkyView.MoonObject("Titan") == null && SkyView.MoonObject("Jupiter") == null);
        }
    }
}
