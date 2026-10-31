using System;
using System.Collections;
using System.Drawing;
using System.Linq;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sidralune.Core;
using Sidralune.Core.Ephemeris;
using Sidralune.Core.Time;
using Sidralune.Science.SolarSystem;
using Sidralune.Tests;
using Sidralune.UI;
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
                    chart.CenterOn(jup.Ra, jup.Dec, 0.2);
                    // move the view so Jupiter's middle is 40 pixels left of the chart, its disc still reaching in
                    double ra, dec;
                    chart.ScreenToIcrs(new Point(1600 + 40, 500), out ra, out dec);
                    chart.CenterOn(ra, dec);
                    chart.Render(g, 1600, 1000, Theme.Current, false);
                    var j = Drawn(chart, "Jupiter");
                    if (j == null) return "Jupiter was not drawn";
                    if (j.Item1.X > -20 || j.Item1.X + j.Item2 < 10) return "Jupiter at x = " + j.Item1.X + " with radius " + j.Item2 + " does not test the edge";
                    // the moons in view are drawn (a 4-pixel dot in the text colour)
                    var moons = GalileanMoons.Positions(time.JdTT).Where(m => !m.Occulted).Select(m => new PointF(j.Item1.X + (float)(m.X * j.Item2 * (chart.FlipX ? -1 : 1)), j.Item1.Y - (float)(m.Y * j.Item2 * (chart.FlipY ? -1 : 1))))
                        .Where(p => p.X > 5 && p.X < 1595 && p.Y > 5 && p.Y < 995).ToList();
                    if (moons.Count == 0) return "no moon in view to test";
                    var bg = bmp.GetPixel(1599, 999).ToArgb();
                    var missing = moons.Where(p => bmp.GetPixel((int)Math.Round(p.X), (int)Math.Round(p.Y)).ToArgb() == bg).ToList();
                    return missing.Count == 0 ? null : missing.Count + " of " + moons.Count + " moons in view not drawn, e.g. at " + missing[0];
                }
            });
            CheckTrue("Jupiter and its moons are drawn while its middle is just outside the chart", result == null, result);
        }
    }
}
