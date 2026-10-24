using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sidralune.Core.Catalogs;
using Sidralune.Core.Time;
using Sidralune.Tests;
using Sidralune.UI;
using Sidralune.Views;
using static Sidralune.AppTests.AppHost;
using static Sidralune.Tests.Checks;

namespace Sidralune.AppTests
{
    /// <summary>Altitude charts across a daylight saving change: time runs forward, labels follow the local clock.</summary>
    [TestClass]
    public class TimeAxisTests : TestBase
    {
        private static readonly TimeZoneInfo Cet = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");

        private static double Utc(int y, int m, int d, int h) => new DateTime(y, m, d, h, 0, 0, DateTimeKind.Utc).ToOADate();

        [TestMethod]
        public void TestTicksAcrossClockChanges()
        {
            var plot = OnUi(() => new PlotControl { XFormat = AxisFormat.Time, TimeZone = Cet });
            // 25 October 2026: at 03:00 summer time the clock goes back to 02:00
            var autumn = plot.TimeTicks(Utc(2026, 10, 24, 12), Utc(2026, 10, 25, 12), 2.0 / 24);
            var labels = autumn.Select(x => plot.Clock(x).ToString("HH:mm")).ToList();
            CheckTrue("Autumn: ticks on even local hours, 02:00 twice", string.Join(" ", labels) == "14:00 16:00 18:00 20:00 22:00 00:00 02:00 02:00 04:00 06:00 08:00 10:00 12:00", string.Join(" ", labels));
            CheckTrue("Autumn: ticks keep moving forward", autumn.Zip(autumn.Skip(1), (a, b) => b > a).All(x => x));
            Check("The two 02:00 ticks are an hour apart (h)", (autumn[7] - autumn[6]) * 24, 1, 1e-6);
            // 29 March 2026: at 02:00 winter time the clock jumps to 03:00
            var spring = plot.TimeTicks(Utc(2026, 3, 28, 22), Utc(2026, 3, 29, 4), 1.0 / 24);
            labels = spring.Select(x => plot.Clock(x).ToString("HH:mm")).ToList();
            CheckTrue("Spring: no 02:00, no gap in the axis", string.Join(" ", labels) == "23:00 00:00 01:00 03:00 04:00 05:00 06:00", string.Join(" ", labels));
            var india = OnUi(() => new PlotControl { XFormat = AxisFormat.Time, TimeZone = TimeZoneInfo.FindSystemTimeZoneById("India Standard Time") });
            var ist = india.TimeTicks(Utc(2026, 10, 24, 0), Utc(2026, 10, 24, 6), 1.0 / 24).Select(x => india.Clock(x).ToString("HH:mm")).ToList();
            CheckTrue("Half-hour zones get whole-hour ticks", ist.First() == "06:00" && ist.All(t => t.EndsWith(":00")), string.Join(" ", ist));
            var plain = OnUi(() => new PlotControl { XFormat = AxisFormat.Time });
            CheckTrue("Without a time zone the values are the clock", plain.Clock(Utc(2026, 10, 24, 12)) == new DateTime(2026, 10, 24, 12, 0, 0));
        }

        [TestMethod]
        public void TestAltitudeChartsOnTheClockChangeNight()
        {
            var site = OnUi(() => AppState.Site);
            string zone = site.TimeZoneId;
            try
            {
                OnUi(() => { site.TimeZoneId = Cet.Id; AppState.SetTime(AstroTime.FromDateTime(new DateTime(2026, 10, 24, 18, 0, 0, DateTimeKind.Utc))); });
                var sky = View<SkyView>("sky");
                OnUi(() => Call(sky, "ShowInfo", SelectedObject.From(CatalogManager.Instance.Find("Capella"))));
                var alt = Field<PlotControl>(sky, "_alt");
                var xs = OnUi(() => alt.Series[0].X.ToList());
                CheckTrue("Sky: the altitude curve never runs backwards in time", xs.Count > 90 && xs.Zip(xs.Skip(1), (a, b) => b > a).All(x => x));
                CheckTrue("Sky: the axis is labelled in the site's time zone", OnUi(() => alt.TimeZone.Id) == Cet.Id);

                var planner = View<PlannerView>("planner");
                OnUi(() => Call(planner, "Fill"));
                var plot = Field<PlotControl>(planner, "_plot");
                CheckTrue("Planner: curves drawn", WaitFor(() => plot.Series.Count >= 2, 20000));
                var bad = OnUi(() => plot.Series.Where(s => !s.X.Zip(s.X.Skip(1), (a, b) => b > a).All(x => x)).Select(s => s.Name).ToList());
                CheckTrue("Planner: no curve runs backwards in time", bad.Count == 0, string.Join(", ", bad));
                var moon = OnUi(() => plot.Series.First(s => s.Name == "Moon"));
                Check("Planner: a full day of 15-minute steps spans 24 h", (moon.X.Last() - moon.X.First()) * 24, 24, 1e-6);
            }
            finally { OnUi(() => { site.TimeZoneId = zone; AppState.GoLive(); }); }
        }
    }
}
