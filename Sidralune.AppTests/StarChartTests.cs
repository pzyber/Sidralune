using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sidralune.Core;
using Sidralune.Core.Catalogs;
using Sidralune.Core.Mathematics;
using Sidralune.Core.Time;
using Sidralune.Tests;
using Sidralune.UI;
using Sidralune.Views;
using static Sidralune.AppTests.AppHost;
using static Sidralune.Tests.Checks;

namespace Sidralune.AppTests
{
    /// <summary>The all-sky star chart of the Sky view's Chart tab: projection, labels, page layout, paper and printing.</summary>
    [TestClass]
    public class StarChartTests : TestBase
    {
        private static readonly Observer Kallby = new Observer { Name = "Källby", LatitudeDeg = 58.5, LongitudeDeg = 13.03, TimeZoneId = "W. Europe Standard Time" };
        // 22:00 local time on 1 November 2026, Saturn in the south
        private static readonly AstroTime Evening = AstroTime.FromDateTime(new DateTime(2026, 11, 1, 21, 0, 0, DateTimeKind.Utc));

        // a page rendered on paper at a resolution, saved for a look when SIDRALUNE_RENDER_DIR is set
        private static StarChartPage Rendered(ChartPaper paper, bool landscape, int dpi, out Size pixels)
        {
            var page = new StarChartPage { Site = Kallby, Time = Evening, Layers = new ChartLayers() };
            Size px = Size.Empty;
            OnUi(() =>
            {
                using (var bmp = page.RenderImage(paper, landscape, dpi))
                {
                    px = bmp.Size;
                    var dir = Environment.GetEnvironmentVariable("SIDRALUNE_RENDER_DIR");
                    if (!string.IsNullOrEmpty(dir)) bmp.Save(Path.Combine(dir, string.Format("starchart-{0}-{1}.png", paper.Name, landscape ? "landscape" : "portrait")));
                }
            });
            pixels = px;
            return page;
        }

        private static double Dist(PointF a, PointF b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

        [TestMethod]
        public void TestStarChartProjection()
        {
            Size px;
            var page = Rendered(ChartPaper.All[0], false, 100, out px);
            PointF n, e, s, w, z, h45;
            page.ToPage(0, 0, out n); page.ToPage(AstroMath.HalfPi, 0, out e); page.ToPage(Math.PI, 0, out s); page.ToPage(1.5 * Math.PI, 0, out w);
            page.ToPage(0, AstroMath.HalfPi, out z); page.ToPage(0, Math.PI / 4, out h45);
            var c = page.Centre; float r = page.Radius;
            CheckTrue("The zenith in the middle", Dist(z, c) < 0.01);
            CheckTrue("North at the top, south at the bottom, on the circle", Dist(n, new PointF(c.X, c.Y - r)) < 0.01 && Dist(s, new PointF(c.X, c.Y + r)) < 0.01);
            CheckTrue("East on the left and west on the right, as seen looking up", Dist(e, new PointF(c.X - r, c.Y)) < 0.01 && Dist(w, new PointF(c.X + r, c.Y)) < 0.01);
            Check("Stereographic: 45° up at tan 22.5° of the radius", Dist(h45, c) / r, Math.Tan(Math.PI / 8), 1e-4);
            var polaris = CatalogManager.Instance.Find("Polaris");
            PointF pp;
            var ph = page.AltAz(polaris.Ra, polaris.Dec);
            page.ToPage(ph.Az, ph.Alt, out pp);
            CheckTrue("Polaris straight above the zenith, 31.5° from it at latitude 58.5°", Math.Abs(pp.X - c.X) < 0.02 * r && pp.Y < c.Y && Math.Abs(Dist(pp, c) / r - Math.Tan(31.5 * AstroMath.Deg2Rad / 2)) < 0.01, pp.ToString());
            // labels turn their tops towards the zenith: upright at the bottom, upside down at the top, a quarter turn at the sides
            Func<PointF, double> angle = p => AstroMath.NormalizeDeg180(page.LabelAngle(p));
            CheckTrue("Labels at the bottom read upright", Math.Abs(angle(new PointF(c.X, c.Y + 0.8f * r))) < 0.01);
            CheckTrue("Labels at the top are upside down on the page", Math.Abs(Math.Abs(angle(new PointF(c.X, c.Y - 0.8f * r))) - 180) < 0.01);
            CheckTrue("Labels on the east side turn a quarter clockwise, on the west side anticlockwise",
                Math.Abs(angle(new PointF(c.X - 0.8f * r, c.Y)) - 90) < 0.01 && Math.Abs(angle(new PointF(c.X + 0.8f * r, c.Y)) + 90) < 0.01);
            CheckTrue("Labels near the zenith stay upright", angle(new PointF(c.X + 0.05f * r, c.Y - 0.05f * r)) == 0);
        }

        [TestMethod]
        public void TestStarChartLabels()
        {
            Size px;
            var page = Rendered(ChartPaper.All[0], false, 100, out px);
            var boxes = page.LabelBoxes;
            var missing = new[] { "Saturn", "Capella", "CASSIOPEIA", "M31" }.Where(t => !page.LabelTexts.Contains(t)).ToList();
            CheckTrue("Stars, constellations, planets and deep-sky objects are named", boxes.Count > 40 && missing.Count == 0,
                boxes.Count + " labels, missing " + string.Join(", ", missing) + ": " + string.Join(", ", page.LabelTexts));
            int overlaps = 0;
            for (int i = 0; i < boxes.Count; i++)
                for (int j = i + 1; j < boxes.Count; j++)
                    if (StarChartPage.Overlap(boxes[i], boxes[j])) overlaps++;
            CheckTrue("No two labels overlap", overlaps == 0, overlaps + " overlapping pairs");
            CheckTrue("Every label within the horizon circle", boxes.All(b => b.All(q => Dist(q, page.Centre) <= page.Radius)));
            // each label's top faces the zenith (away from the middle, where labels stay upright)
            int wrong = 0;
            foreach (var b in boxes)
            {
                var mid = new PointF(b.Average(q => q.X), b.Average(q => q.Y));
                if (Dist(mid, page.Centre) < 0.3 * page.Radius) continue;
                double ux = b[0].X - b[3].X, uy = b[0].Y - b[3].Y, cx = page.Centre.X - mid.X, cy = page.Centre.Y - mid.Y;
                if ((ux * cx + uy * cy) / Math.Sqrt(ux * ux + uy * uy) / Math.Sqrt(cx * cx + cy * cy) < 0.8) wrong++;
            }
            CheckTrue("Labels have their tops towards the zenith", wrong == 0, wrong + " labels turned another way");
            CheckTrue("Overlap test", StarChartPage.Overlap(new[] { new PointF(0, 0), new PointF(2, 0), new PointF(2, 1), new PointF(0, 1) }, new[] { new PointF(1, 0.5f), new PointF(3, 0.5f), new PointF(3, 2), new PointF(1, 2) })
                && !StarChartPage.Overlap(new[] { new PointF(0, 0), new PointF(2, 0), new PointF(2, 1), new PointF(0, 1) }, new[] { new PointF(1.5f, 1.6f), new PointF(3, 0.1f), new PointF(3.5f, 0.6f), new PointF(2, 2.1f) }));
            CheckTrue("Bayer letters as Greek", StarChartPage.BayerLabel("Alp Ori") == "α" && StarChartPage.BayerLabel("Alp2 Cen") == "α²" && StarChartPage.BayerLabel("58 Ori") == "58" && StarChartPage.BayerLabel("Xyz Ori") == null);
        }

        [TestMethod]
        public void TestStarChartPages()
        {
            double lastLimit = 0;
            foreach (var paper in ChartPaper.All)
                foreach (var landscape in new[] { false, true })
                {
                    Size px;
                    var page = Rendered(paper, landscape, 60, out px);
                    string name = paper.Name + (landscape ? " landscape" : " portrait");
                    float w = (float)(px.Width / (60 / 25.4)), h = (float)(px.Height / (60 / 25.4));
                    float reach = page.Radius + 9 * page.FontScale;
                    var inner = new RectangleF(7.8f, 7.8f, w - 15.6f, h - 15.6f);
                    CheckTrue(name + ": the circle and its directions within the margins", page.Centre.X - reach >= inner.Left - 0.1 && page.Centre.X + reach <= inner.Right + 0.1 && page.Centre.Y - reach >= inner.Top - 0.1 && page.Centre.Y + reach <= inner.Bottom + 0.1);
                    var disc = new RectangleF(page.Centre.X - reach, page.Centre.Y - reach, 2 * reach, 2 * reach);
                    CheckTrue(name + ": the details beside the circle, within the margins", page.InfoBlocks.All(b => inner.Contains(b) && !b.IntersectsWith(disc)));
                    CheckTrue(name + ": sun, moon, planets and key shown", new[] { "The same sky", "Sun", "Moon", "Planets tonight", "Key" }.All(page.InfoHeadings.Contains), string.Join(", ", page.InfoHeadings));
                    CheckTrue(name + ": the circle fills most of the page's width or height", 2 * page.Radius > 0.6 * Math.Min(w, h), (2 * page.Radius).ToString("0") + " mm");
                    if (!landscape)
                    {
                        CheckTrue(paper.Name + ": deeper on larger paper", page.MagLimit > lastLimit, page.MagLimit.ToString("0.00"));
                        lastLimit = page.MagLimit;
                    }
                }
            CheckTrue("About magnitude 5.5 on A4 and 6.5 on A2", Math.Abs(StarChartPage.AutoMagLimit(172) - 5.5) < 0.1 && Math.Abs(StarChartPage.AutoMagLimit(370) - 6.5) < 0.1);
        }

        [TestMethod]
        public void TestStarChartNight()
        {
            Size px;
            var page = Rendered(ChartPaper.All[0], false, 60, out px);
            var lines = page.InfoLines;
            CheckTrue("Saturn among tonight's planets", lines.Any(l => l.StartsWith("Planets tonight: Saturn")), string.Join(" | ", lines));
            CheckTrue("Sunset, darkness and sunrise", lines.Any(l => l.StartsWith("Sun: Sets 16:")) && lines.Any(l => l.StartsWith("Sun: Dark from")) && lines.Any(l => l.StartsWith("Sun: Rises 07:")), string.Join(" | ", lines.Where(l => l.StartsWith("Sun"))));
            CheckTrue("The Moon's phase and how much of it is lit", lines.Any(l => l.StartsWith("Moon: ") && l.EndsWith("% lit")), string.Join(" | ", lines.Where(l => l.StartsWith("Moon"))));
            // the same stars on other dates: the same sidereal time, about 4 minutes earlier each day
            var times = StarChartPage.SameSkyTimes(Kallby, Evening);
            double lst = new SkyContext(Kallby, Evening).Last;
            CheckTrue("Five dates round the chart's, the 1st and 15th of the months", times.Count == 5 && times.Any(t => t.Item1 == new DateTime(2026, 10, 15)) && times.Any(t => t.Item1 == new DateTime(2026, 11, 15)), string.Join(", ", times.Select(t => t.Item1.ToString("d MMM"))));
            foreach (var t in times)
            {
                double other = new SkyContext(Kallby, AstroTime.FromDateTime(Kallby.ToUtc(t.Item2))).Last;
                CheckTrue("Same sky on " + t.Item1.ToString("d MMM") + " at " + t.Item2.ToString("HH:mm"), Math.Abs(AstroMath.NormalizeRadPi(other - lst)) * AstroMath.Rad2Deg * 4 < 1);
            }
            var later = times.First(t => t.Item1 == new DateTime(2026, 11, 15));
            CheckTrue("Two weeks later, about an hour earlier", Math.Abs((later.Item2 - later.Item1).TotalHours - 21.08) < 0.05, later.Item2.ToString("HH:mm"));
        }

        [TestMethod]
        public void TestStarChartPaper()
        {
            var a4 = ChartPaper.All.First(p => p.Name == "A4");
            var a3plus = ChartPaper.All.First(p => p.Name == "A3+");
            CheckTrue("A4 at 300 dpi", a4.Pixels(300, false) == new Size(2480, 3508) && a4.Pixels(300, true) == new Size(3508, 2480));
            CheckTrue("A3+ (329 × 483 mm) at 300 dpi", a3plus.Pixels(300, false) == new Size(3886, 5705));
            var page = new StarChartPage { Site = Kallby, Time = Evening, Layers = new ChartLayers() };
            var result = OnUi(() =>
            {
                using (var bmp = page.RenderImage(a4, false, 300))
                {
                    var dir = Environment.GetEnvironmentVariable("SIDRALUNE_RENDER_DIR");
                    if (!string.IsNullOrEmpty(dir)) bmp.Save(Path.Combine(dir, "starchart-A4-300dpi.png"));
                    // ink inside the circle: star dots and lines in black on the white paper
                    var c = page.Centre; float k = 300 / 25.4f;
                    int dark = 0;
                    for (int y = (int)((c.Y - 40) * k); y < (c.Y + 40) * k; y += 3)
                        for (int x = (int)((c.X - 40) * k); x < (c.X + 40) * k; x += 3)
                            if (bmp.GetPixel(x, y).GetBrightness() < 0.5) dark++;
                    return Tuple.Create(bmp.Size, bmp.HorizontalResolution, dark);
                }
            });
            CheckTrue("A 300 dpi image of the whole page", result.Item1 == new Size(2480, 3508) && Math.Abs(result.Item2 - 300) < 0.5);
            CheckTrue("Stars drawn in the circle", result.Item3 > 200, result.Item3.ToString());
            // a printer's own paper of a size, whatever it is called, or a custom size
            var ps = new System.Drawing.Printing.PrinterSettings();
            System.Drawing.Printing.PaperSize found = null;
            try { found = StarChartPage.FindPaperSize(ps, a3plus); } catch (System.Drawing.Printing.InvalidPrinterException) { }
            if (found != null)
                CheckTrue("A3+ paper for the printer, 329 × 483 mm", Math.Abs(Math.Min(found.Width, found.Height) * 0.254 - 329) < 3 && Math.Abs(Math.Max(found.Width, found.Height) * 0.254 - 483) < 3, found.PaperName);
        }

        [TestMethod]
        public void TestStarChartTab()
        {
            OnUi(() => App.Navigate("sky"));
            var sky = View<SkyView>("sky");
            var tabs = OnUi(() => Find<SegmentedTabs>(sky));
            var result = OnUi(() =>
            {
                tabs.SelectedIndex = 2;
                var paper = Field<ComboBox>(sky, "_paper");
                var fov = Field<ComboBox>(sky, "_fov");
                if (!sky.StarChart.Visible || sky.Chart.Visible || !paper.Visible || fov.Visible) return "the Chart tab does not show the star chart and its paper sizes";
                if (paper.Items.Cast<object>().Select(o => o.ToString()).Count(t => t.StartsWith("A4 ") || t.StartsWith("A3 ") || t.StartsWith("A3+ ") || t.StartsWith("A2 ")) != 8) return "paper sizes: " + string.Join(", ", paper.Items.Cast<object>());
                sky.StarChart.FixedTime = Evening;
                using (var bmp = new Bitmap(sky.StarChart.Width, sky.StarChart.Height))
                {
                    sky.StarChart.DrawToBitmap(bmp, new Rectangle(Point.Empty, bmp.Size));
                    var dir = Environment.GetEnvironmentVariable("SIDRALUNE_RENDER_DIR");
                    if (!string.IsNullOrEmpty(dir)) bmp.Save(Path.Combine(dir, "starchart-screen.png"));
                }
                // a click on a drawn object selects it
                var hit = sky.StarChart.Page.Hits.FirstOrDefault(h => h.O.Kind == SelectedKind.Planet) ?? sky.StarChart.Page.Hits.FirstOrDefault();
                if (hit == null) return "nothing drawn to click";
                float k = Field<float>(sky.StarChart, "_pxPerMm");
                Call(sky.StarChart, "OnMouseUp", new MouseEventArgs(MouseButtons.Left, 1, (int)Math.Round(hit.P.X * k), (int)Math.Round(hit.P.Y * k), 0));
                var title = Field<Label>(sky, "_objTitle").Text;
                if (title != hit.O.Name) return "clicked " + hit.O.Name + ", selected " + title;
                tabs.SelectedIndex = 0;
                if (sky.StarChart.Visible || !sky.Chart.Visible || paper.Visible || !fov.Visible) return "the Planetarium tab does not come back";
                sky.StarChart.FixedTime = null;
                return null;
            });
            CheckTrue("The Chart tab shows the star chart; a click selects an object; Planetarium comes back", result == null, result);
        }
    }
}
