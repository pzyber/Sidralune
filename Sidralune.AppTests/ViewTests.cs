using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sidralune.Tests;
using Sidralune.UI;
using static Sidralune.AppTests.AppHost;
using static Sidralune.Tests.Checks;

namespace Sidralune.AppTests
{
    /// <summary>Every workspace opens without errors, in day and night mode and at the smallest window size, and the command palette.</summary>
    [TestClass]
    public class ViewTests : TestBase
    {
        [TestMethod]
        public void TestEveryViewOpens()
        {
            var views = AllViews;
            CheckTrue("Fifteen workspaces", views.Count == 15, views.Count.ToString());
            foreach (var v in views)
            {
                int mark = LogMark();
                OnUi(() => App.Navigate(v.Key));
                Settle(800);
                var errors = ErrorsSince(mark);
                bool shown = OnUi(() => v.Instance != null && v.Instance.Visible && v.Instance.Width > 300 && v.Instance.Controls.Count > 0);
                CheckTrue(v.Title + " opens", shown && errors.Count == 0, string.Join(" | ", errors));
            }
        }

        [TestMethod]
        public void TestTonightLayout()
        {
            // a fresh Tonight tab opened as the main window opens a workspace, at a large, a medium and the smallest window size
            foreach (var size in new[] { new Size(2000, 1100), new Size(1486, 943), new Size(1100, 700) })
            {
                var state = OnUi(() =>
                {
                    var f = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-3000, 0), ShowInTaskbar = false, Size = size };
                    f.Show();
                    var v = new Sidralune.Views.TonightView { Dock = DockStyle.Fill };
                    f.Controls.Add(v);
                    Theme.Apply(v);
                    return Tuple.Create(f, (Panel)typeof(Sidralune.Views.TonightView).GetField("_cards", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(v));
                });
                Settle(500);
                var r = OnUi(() =>
                {
                    var panel = state.Item2;
                    var cards = panel.Controls.OfType<Card>().ToDictionary(c => c.Title, c => c.Bounds);
                    bool bar = panel.VerticalScroll.Visible;
                    // scrolled to the end, where the lowest card is
                    panel.AutoScrollPosition = new Point(0, 100000);
                    int bottomScrolled = panel.Controls.OfType<Card>().Max(c => c.Bottom);
                    var res = Tuple.Create(cards, panel.ClientSize, bar, bottomScrolled);
                    state.Item1.Close();
                    return res;
                });
                string tag = size.Width + "×" + size.Height + ": ";
                var b = r.Item1.Values.ToList();
                int bottom = b.Max(x => x.Bottom), right = b.Max(x => x.Right);
                Func<string, Rectangle> card = t => r.Item1.First(kv => kv.Key.StartsWith(t)).Value;
                // the scroll bar is there at once when the cards go below the window, without resizing or switching tabs first
                CheckTrue(tag + "a scroll bar exactly when the cards go below the window", r.Item3 == (bottom > r.Item2.Height), bottom + " vs " + r.Item2.Height);
                CheckTrue(tag + "scrolled to the end, the lowest card is in view", r.Item4 <= r.Item2.Height, r.Item4 + " ≤ " + r.Item2.Height);
                CheckTrue(tag + "nothing to scroll sideways", right <= r.Item2.Width, right + " ≤ " + r.Item2.Width);
                // packed: no card overlaps another, and each is at the top or right under the lowest card above it
                CheckTrue(tag + "no card overlaps another", !b.Any(x => b.Any(y => y != x && y.IntersectsWith(x))));
                int top0 = b.Min(y => y.Top);
                CheckTrue(tag + "no gap above any card", b.All(x => x.Top == b.Where(y => y.Bottom <= x.Top && y.Left < x.Right && x.Left < y.Right).Select(y => y.Bottom + 12).DefaultIfEmpty(top0).Max()),
                    string.Join(", ", r.Item1.Select(kv => kv.Key + " " + kv.Value)));
                if (size.Width == 1100)
                {
                    // two columns: Coming up beside Planets, under the Moon, before the deep-sky targets across both
                    CheckTrue(tag + "Coming up under the Moon", card("Coming up").Top == card("Moon").Bottom + 12 && card("Coming up").Left == card("Moon").Left);
                    CheckTrue(tag + "the deep-sky targets under Planets and Coming up", card("Best deep-sky").Top == Math.Max(card("Planets").Bottom, card("Coming up").Bottom) + 12);
                }
                if (size.Width == 1486)
                {
                    // three columns: Coming up right under Planets, the targets under Tonight and Moon, Getting started under Jupiter
                    CheckTrue(tag + "Coming up under Planets", card("Coming up").Top == card("Planets").Bottom + 12 && card("Coming up").Left == card("Planets").Left);
                    CheckTrue(tag + "the deep-sky targets under Tonight and the Moon", card("Best deep-sky").Top == card("Tonight").Bottom + 12 && card("Best deep-sky").Right == card("Moon").Right);
                    CheckTrue(tag + "Comets & asteroids under Coming up", card("Comets").Top == card("Coming up").Bottom + 12);
                    CheckTrue(tag + "Getting started under Jupiter's moons", card("Getting started").Top == card("Jupiter").Bottom + 12);
                }
            }
        }

        [TestMethod]
        public void TestTonightComingUpFollowsTime()
        {
            // Coming up lists the events after the time the user sets, like the other cards, not those after the time the tab opened
            var bf = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            var start = new DateTime(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);
            var form = OnUi(() => new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-3000, 0), ShowInTaskbar = false, Size = new Size(1500, 900) });
            try
            {
                var view = OnUi(() =>
                {
                    AppState.SetTime(Core.Time.AstroTime.FromDateTime(start));
                    var v = new Sidralune.Views.TonightView { Dock = DockStyle.Fill };
                    form.Controls.Add(v);
                    form.Show();
                    typeof(Sidralune.Views.TonightView).GetMethod("Refresh", bf, null, new[] { typeof(bool) }, null).Invoke(v, new object[] { true });
                    return v;
                });
                var grid = Field<DataGridView>(view, "_eventGrid");
                Func<string> first = () => OnUi(() => grid.Rows.Count > 0 ? (string)grid.Rows[0].Cells[0].Value : null);
                CheckTrue("Coming up lists events", WaitFor(() => first() != null, 60000));
                string before = first();

                var later = start.AddDays(60);
                OnUi(() => AppState.SetTime(Core.Time.AstroTime.FromDateTime(later)));
                CheckTrue("Coming up changes with the time", WaitFor(() => first() != before, 60000), before);
                // the first event is now after the new time ("Tue 19 Jan 04:12", in the site's time zone and the language's month names)
                var local = OnUi(() => AppState.Site.ToLocal(later));
                string when = first();
                var parsed = DateTime.ParseExact(when.Substring(when.IndexOf(' ') + 1) + " " + local.Year, "dd MMM HH:mm yyyy", Core.Util.Lang.DateNames);
                if (parsed < local.AddDays(-1)) parsed = parsed.AddYears(1);
                CheckTrue("Coming up starts after the new time", parsed >= local.AddMinutes(-1) && parsed < local.AddDays(400), when + " after " + local);
                // the eclipses within a year are as seen from the site (2027: annular in February, total in August), not just "somewhere on Earth"
                var eclipses = OnUi(() => grid.Rows.Cast<DataGridViewRow>().Select(r => (string)r.Cells[1].Value).Where(t => t.Contains("solar eclipse")).ToList());
                CheckTrue("Coming up shows solar eclipses from the site", eclipses.Count > 0 && eclipses.All(t => !t.Contains("somewhere on Earth")
                    && (t.Contains("% of the Sun covered") || t.Contains("(not visible from ") || t.StartsWith("Total solar eclipse"))), string.Join(" / ", eclipses));
            }
            finally { OnUi(() => { form.Close(); AppState.GoLive(); }); }
        }

        [TestMethod]
        public void TestSolarSystemEclipses()
        {
            // Solar sys. → Eclipses lists the eclipses from the time shown, each with its type and how it looks from the site,
            // and the selected one's phases in time order; Show in sky goes to the Sun at the eclipse
            var bf = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            var start = new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);
            try
            {
                var view = View<Sidralune.Views.SolarSystemView>("solarsystem");
                var grid = Field<DataGridView>(view, "_eclGrid");
                var phases = Field<DataGridView>(view, "_eclPhases");
                OnUi(() =>
                {
                    AppState.SetTime(Core.Time.AstroTime.FromDateTime(start));
                    typeof(Sidralune.Views.SolarSystemView).GetMethod("ComputeEclipses", bf).Invoke(view, new object[] { 2 });
                    App.Navigate("solarsystem");
                    Field<TabbedPanel>(view, "_tabs").Select(3);
                });
                CheckTrue("Eclipses listed", WaitFor(() => OnUi(() => grid.Rows.Count >= 4), 60000), OnUi(() => grid.Rows.Count.ToString()));
                var rows = OnUi(() => grid.Rows.Cast<DataGridViewRow>().Select(r => Enumerable.Range(0, 5).Select(i => (string)r.Cells[i].Value).ToArray()).ToList());
                CheckTrue("The total solar eclipse of 12 August 2026 first, then the partial lunar eclipse of 28 August",
                    rows[0][0].StartsWith("2026-08-12") && rows[0][1] == "Total solar eclipse" && rows[1][0].StartsWith("2026-08-28") && rows[1][1] == "Partial lunar eclipse", string.Join(" / ", rows.Take(2).Select(r => string.Join(", ", r))));
                CheckTrue("Each says how it looks from the site", rows.All(r => !string.IsNullOrEmpty(r[2])) && rows.Where(r => r[1].Contains("lunar")).All(r => r[3].Length > 0));
                CheckTrue("In time order", rows.Select(r => r[0]).SequenceEqual(rows.Select(r => r[0]).OrderBy(x => x)));

                // the solar eclipse's phases: greatest eclipse on Earth at the global time, in time order
                OnUi(() => grid.CurrentCell = grid.Rows[0].Cells[0]);
                var global = Core.Ephemeris.Events.SolarEclipses(Core.Time.AstroTime.FromDateTime(start).JdTT, Core.Time.AstroTime.FromDateTime(start).JdTT + 30)[0].Time;
                string greatest = OnUi(() => AppState.Site.ToLocal(global.ToDateTimeUtc()).ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture));
                var solar = OnUi(() => phases.Rows.Cast<DataGridViewRow>().Select(r => (string)r.Cells[0].Value + " " + (string)r.Cells[1].Value).ToList());
                CheckTrue("The solar eclipse's phases include greatest eclipse on Earth", solar.Contains("Greatest eclipse on Earth " + greatest), string.Join(" / ", solar));
                // the lunar eclipse: penumbral and partial phases and greatest eclipse, in time order
                OnUi(() => grid.CurrentCell = grid.Rows[1].Cells[0]);
                var lunar = OnUi(() => phases.Rows.Cast<DataGridViewRow>().Select(r => new[] { (string)r.Cells[0].Value, (string)r.Cells[1].Value }).ToList());
                var names = lunar.Select(p => p[0]).ToList();
                CheckTrue("The lunar eclipse's phases", new[] { "Penumbral eclipse begins", "Partial eclipse begins", "Greatest eclipse", "Partial eclipse ends", "Penumbral eclipse ends" }.All(names.Contains)
                    && names.IndexOf("Penumbral eclipse begins") < names.IndexOf("Partial eclipse begins") && names.IndexOf("Partial eclipse begins") < names.IndexOf("Greatest eclipse")
                    && lunar.Select(p => p[1]).SequenceEqual(lunar.Select(p => p[1]).OrderBy(x => x)), string.Join(" / ", lunar.Select(p => p[0] + " " + p[1])));

                // Show in sky: the Moon at greatest eclipse
                OnUi(() => typeof(Sidralune.Views.SolarSystemView).GetMethod("ShowEclipse", bf).Invoke(view, null));
                Settle(500);
                CheckTrue("Show in sky sets the time to the eclipse and selects the Moon", OnUi(() => AppState.Selection != null && AppState.Selection.Planet == Core.Ephemeris.Body.Moon
                    && Math.Abs((AppState.Now.ToDateTimeUtc() - new DateTime(2026, 8, 28, 4, 13, 0)).TotalMinutes) < 5), OnUi(() => AppState.Now + " " + AppState.Selection?.Name));
            }
            finally { OnUi(() => AppState.GoLive()); }
        }

        [TestMethod]
        public void TestEclipseMap()
        {
            // the map beside the eclipse list: the selected eclipse's lines, zoom and move, a click shows the eclipse there,
            // Add as site, and drawing by day and at night
            var bf = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            var start = new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);
            int sites = OnUi(() => AppState.Settings.Sites.Count);
            try
            {
                var view = View<Sidralune.Views.SolarSystemView>("solarsystem");
                var grid = Field<DataGridView>(view, "_eclGrid");
                var map = Field<EclipseMap>(view, "_eclMap");
                OnUi(() =>
                {
                    AppState.SetTime(Core.Time.AstroTime.FromDateTime(start));
                    typeof(Sidralune.Views.SolarSystemView).GetMethod("ComputeEclipses", bf).Invoke(view, new object[] { 1 });
                    App.Navigate("solarsystem");
                    Field<TabbedPanel>(view, "_tabs").Select(3);
                });
                CheckTrue("Eclipses listed", WaitFor(() => OnUi(() => grid.Rows.Count >= 2), 60000));
                OnUi(() => grid.CurrentCell = grid.Rows[0].Cells[0]);
                CheckTrue("The total solar eclipse of 12 August 2026 drawn", WaitFor(() => OnUi(() => map.Data != null && map.Data.Solar), 60000));
                var kinds = OnUi(() => map.Data.Lines.Where(l => l.Parts.Count > 0).Select(l => l.Kind).Distinct().ToList());
                CheckTrue("Its central line, limits of totality and the edge of the partial eclipse", kinds.Contains(Core.Ephemeris.EclipseLineKind.CentralLine) && kinds.Contains(Core.Ephemeris.EclipseLineKind.CentralLimit) && kinds.Contains(Core.Ephemeris.EclipseLineKind.PenumbralLimit), string.Join(", ", kinds));
                // seen at every longitude around the North Pole, so the whole world; greatest eclipse (north of Greenland) in view
                CheckTrue("Greatest eclipse in view", OnUi(() => { var s = map.ToScreen(map.Data.GreatestPoint.Value.Lat, map.Data.GreatestPoint.Value.Lon); return s.X >= 0 && s.Y >= 0 && s.X <= map.Width && s.Y <= map.Height; }));
                OnUi(() => map.ShowRegion(-40, 40, 10, 75));

                // screen and map places back and forth, and zooming keeps the place under the cursor
                var round = OnUi(() => { var s = map.ToScreen(64.15, -21.94); var g = map.ToGeo(Point.Round(s)); return Math.Abs(g.Lat - 64.15) + Math.Abs(g.Lon + 21.94); });
                CheckTrue("A place drawn and read back", round < 2 / OnUi(() => map.Scale) + 1e-6, round.ToString("0.0000"));
                var kept = OnUi(() => { var at = new Point(map.Width / 3, map.Height / 3); var before = map.ToGeo(at); map.ZoomAt(2, at); var after = map.ToGeo(at); return Math.Abs(after.Lat - before.Lat) + Math.Abs(after.Lon - before.Lon); });
                CheckTrue("Zooming keeps the place under the cursor", kept < 0.05, kept.ToString("0.0000"));

                // a click on the path near Reykjavík: total there, with the duration and the contacts
                OnUi(() => typeof(Sidralune.Views.SolarSystemView).GetMethod("ShowPlace", bf).Invoke(view, new object[] { new Core.Ephemeris.GeoPoint(64.15, -21.94) }));
                string info = OnUi(() => map.InfoText) ?? "";
                CheckTrue("The popup: total, its duration, the path's width and the contacts in UT", info.Contains("Total solar eclipse") && info.Contains("Duration of totality:") && info.Contains("Path width") && info.Contains("Partial eclipse begins") && info.Contains("Time (UTC)"), info);
                var local = OnUi(() => map.Data.Elements.Local(64.15, -21.94));
                CheckTrue("The duration shown is the calculated one", info.Contains(Core.Util.Lang.T("{0} min {1:0.0} s", (int)(local.CentralSeconds / 60), local.CentralSeconds - 60 * (int)(local.CentralSeconds / 60))), info);
                // the close button in its own space: clear of the text and wholly inside the box, as is Add as site
                var box = OnUi(() => new Rectangle(Point.Empty, map.InfoSize));
                var text = OnUi(() => map.InfoTextBounds); var close = OnUi(() => map.InfoCloseBounds); var add = OnUi(() => map.InfoAddSiteBounds);
                CheckTrue("The popup's close button clear of its text and inside it", !close.IntersectsWith(text) && box.Contains(close) && box.Contains(add) && box.Contains(text) && !add.IntersectsWith(text), "box " + box + ", text " + text + ", close " + close + ", add " + add);
                // Open in Google Maps and Copy in a row with Add as site, inside the box and clear of each other
                var maps = OnUi(() => map.InfoOpenMapsBounds); var copy = OnUi(() => map.InfoCopyBounds);
                CheckTrue("Google Maps and Copy beside Add as site", box.Contains(maps) && box.Contains(copy) && !maps.IntersectsWith(add) && !copy.IntersectsWith(maps) && !maps.IntersectsWith(text) && maps.Top == add.Top && copy.Top == add.Top, "add " + add + ", maps " + maps + ", copy " + copy + ", box " + box);
                string opened = null;
                OnUi(() => { var was = map.OpenUrl; map.OpenUrl = u => opened = u; Field<FlatButton>(map, "_openMaps").PerformClick(); map.OpenUrl = was; });
                CheckTrue("Google Maps opened at the place", opened == "https://www.google.com/maps/search/?api=1&query=64.15%2C-21.94", opened);
                // Copy: the place in decimal degrees on the clipboard (what was there put back)
                string copied = OnUi(() =>
                {
                    string before = Clipboard.ContainsText() ? Clipboard.GetText() : null;
                    map.CopyPlace();
                    string got = Clipboard.GetText();
                    if (before != null) Clipboard.SetText(before); else Clipboard.Clear();
                    return got;
                });
                CheckTrue("The place copied", copied == "64.15, -21.94", copied);

                // Go to coordinates: a field beside its button under the zoom buttons; what it can't read is said, a
                // place is gone to with the eclipse there shown
                OnUi(() => map.ShowGoTo());
                var goButton = OnUi(() => map.GoToButtonBounds); var goPanel = OnUi(() => map.GoToPanelBounds);
                CheckTrue("The Go to field opens beside its button", OnUi(() => map.GoToOpen) && goPanel.Left > goButton.Right && goPanel.Top == goButton.Top && goPanel.Right <= OnUi(() => map.Width), "button " + goButton + ", field " + goPanel);
                string hint = OnUi(() => map.GoToHint);
                bool wentBad = OnUi(() => { map.GoToText = "Reykjavik"; return map.TryGoTo(); });
                CheckTrue("Text that is no place: said so, the field stays open", !wentBad && OnUi(() => map.GoToOpen) && OnUi(() => map.GoToHint) != hint, OnUi(() => map.GoToHint));
                bool went = OnUi(() => { map.GoToText = "64°09′N 21°56′W"; return map.TryGoTo(); });
                CheckTrue("Gone to 64°09′N 21°56′W: centred, the field closed, the eclipse there shown", went && !OnUi(() => map.GoToOpen) && Math.Abs(OnUi(() => map.CenterLat) - 64.15) < 1 && Math.Abs(OnUi(() => map.CenterLon) + 21.93) < 0.01 && (OnUi(() => map.InfoText) ?? "").Contains("Total solar eclipse"), OnUi(() => map.InfoText));
                // the popup beside, below or above the place, never over its marker (also in a narrow map)
                foreach (int width in new[] { 0, 520 })
                {
                    var covers = OnUi(() =>
                    {
                        int h = map.Height;
                        if (width > 0) { map.Dock = DockStyle.None; map.Size = new Size(width, h); }
                        map.GoTo(new Core.Ephemeris.GeoPoint(64.15, -21.94));
                        var at = Point.Round(map.ToScreen(64.15, -21.94));
                        var r = map.InfoBounds; r.Inflate(6, 6);
                        string res = r.Contains(at) ? "over the place at " + at + ": " + map.InfoBounds + " in " + map.Size : null;
                        if (width > 0) map.Dock = DockStyle.Fill;
                        return res;
                    });
                    CheckTrue("The popup clear of the place" + (width > 0 ? " in a narrow map" : ""), covers == null, covers);
                }
                OnUi(() => typeof(Sidralune.Views.SolarSystemView).GetMethod("ShowPlace", bf).Invoke(view, new object[] { new Core.Ephemeris.GeoPoint(-33.87, 151.21) }));
                CheckTrue("No eclipse in Sydney", (OnUi(() => map.InfoText) ?? "").Contains("No eclipse here"), OnUi(() => map.InfoText));

                // drawn without errors by day and at night
                int mark = LogMark();
                foreach (bool night in new[] { false, true })
                    OnUi(() =>
                    {
                        Theme.SetNightMode(night); Theme.Apply(App);
                        using (var bmp = new Bitmap(Math.Max(200, map.Width), Math.Max(200, map.Height))) map.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
                    });
                OnUi(() => { Theme.SetNightMode(AppState.Settings.NightMode); Theme.Apply(App); });
                CheckTrue("Drawn by day and at night without errors", ErrorsSince(mark).Count == 0, string.Join(" | ", ErrorsSince(mark)));

                // the lunar eclipse: where the Moon is up
                OnUi(() => grid.CurrentCell = grid.Rows[1].Cells[0]);
                CheckTrue("The lunar eclipse of 28 August 2026 drawn with where it is visible", WaitFor(() => OnUi(() => map.Data != null && !map.Data.Solar && map.Data.Visibility != null), 60000));
                OnUi(() => typeof(Sidralune.Views.SolarSystemView).GetMethod("ShowPlace", bf).Invoke(view, new object[] { new Core.Ephemeris.GeoPoint(59.33, 18.07) }));
                CheckTrue("A click on Stockholm: the Moon sets during it", (OnUi(() => map.InfoText) ?? "").Contains("Moonset"), OnUi(() => map.InfoText));
                // the shading worked out for the view, here zoomed in on the moonset line through Scandinavia: shaded on
                // the side where the Moon is still up, clear beyond it
                mark = LogMark();
                var shade = OnUi(() =>
                {
                    map.ShowRegion(0, 50, 40, 70);
                    using (var bmp = new Bitmap(map.Width, map.Height))
                    {
                        map.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
                        // the colour in the open sea west of Norway (up for part of it) against far east in Russia (not at all)
                        var west = map.ToScreen(64, 2); var east = map.ToScreen(64, 38);
                        return Tuple.Create(bmp.GetPixel((int)west.X, (int)west.Y), bmp.GetPixel((int)east.X, (int)east.Y));
                    }
                });
                CheckTrue("The lunar map drawn zoomed in without errors", ErrorsSince(mark).Count == 0, string.Join(" | ", ErrorsSince(mark)));
                CheckTrue("Shaded where the Moon is up for part of it, not beyond", shade.Item1 != shade.Item2, shade.Item1 + " / " + shade.Item2);

                // Add as site: the place joins the sites and opens in Settings → Location
                OnUi(() => typeof(Sidralune.Views.SolarSystemView).GetMethod("AddEclipseSite", bf).Invoke(view, new object[] { new Core.Ephemeris.GeoPoint(-16.65373, 128.66009) }));
                CheckTrue("Add as site adds the place", OnUi(() => AppState.Settings.Sites.Count == sites + 1 && Math.Abs(AppState.Settings.Sites.Last().LatitudeDeg + 16.65373) < 1e-6 && AppState.Settings.Sites.Last().ElevationM == 0));
                CheckTrue("and opens Settings", OnUi(() => App.GetView<Sidralune.Views.SettingsView>("settings")?.Visible == true));
            }
            finally
            {
                OnUi(() => { while (AppState.Settings.Sites.Count > sites) AppState.Settings.Sites.RemoveAt(AppState.Settings.Sites.Count - 1); AppState.Save(); AppState.GoLive(); });
            }
        }

        [TestMethod]
        public void TestNightModeAndSmallWindow()
        {
            var views = AllViews;
            var size = OnUi(() => App.Size);
            try
            {
                OnUi(() => { App.SetNightMode(true); App.Size = App.MinimumSize; });
                foreach (var v in views)
                {
                    int mark = LogMark();
                    OnUi(() => App.Navigate(v.Key));
                    Settle(500);
                    var errors = ErrorsSince(mark);
                    // the visible parts of the workspace stay inside the window (no control pushed out of reach)
                    var outside = OnUi(() =>
                    {
                        var view = v.Instance;
                        return All(view).Where(c => c.Visible && c is FlatButton && c.Width > 0 && IsShown(c))
                            .Where(c => { var r = view.RectangleToClient(c.RectangleToScreen(c.ClientRectangle)); return r.Right > view.ClientSize.Width + 2 || r.Bottom > view.ClientSize.Height + 2; })
                            .Where(c => !InScrollable(c, view)).Select(c => c.Text).ToList();
                    });
                    CheckTrue(v.Title + " at 1100 × 700 in night mode", errors.Count == 0 && outside.Count == 0, string.Join(" | ", errors.Concat(outside.Select(o => "outside the window: " + o))));
                }
            }
            finally { OnUi(() => { App.SetNightMode(false); App.Size = size; }); }
        }

        private static bool IsShown(Control c)
        {
            for (var p = c; p != null; p = p.Parent) if (!p.Visible) return false;
            return true;
        }

        /// <summary>Inside a container that scrolls (or a strip that wraps), so it can be reached.</summary>
        private static bool InScrollable(Control c, Control stop)
        {
            for (var p = c.Parent; p != null && p != stop; p = p.Parent)
            {
                var sc = p as ScrollableControl;
                if (sc != null && sc.AutoScroll) return true;
                var flow = p as FlowLayoutPanel;
                if (flow != null && flow.WrapContents && flow.AutoSize) return true;
            }
            return false;
        }

        [TestMethod]
        public void TestCommands()
        {
            foreach (var v in AllViews) OnUi(() => App.Navigate(v.Key));
            Settle(500);
            var cmds = OnUi(() => App.Commands.ToList());
            CheckTrue("A Go to command for every workspace", AllViews.All(v => cmds.Any(c => c.Title == "Go to " + v.Title)));
            var dupes = cmds.GroupBy(c => c.Category + "/" + c.Title).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            CheckTrue("Command titles are unique", dupes.Count == 0, string.Join(", ", dupes));
            CheckTrue("Every command has a title, a category and an action", cmds.All(c => !string.IsNullOrWhiteSpace(c.Title) && !string.IsNullOrWhiteSpace(c.Category) && c.Run != null));
            OnUi(() => cmds.First(c => c.Title == "Go to Sky").Run());
            CheckTrue("Running a Go to command opens the workspace", OnUi(() => AllViews.First(v => v.Key == "sky").Instance.Visible));
            OnUi(() => cmds.First(c => c.Title == "Time: +1 hour").Run());
            CheckTrue("Time commands", !AppState.IsLive);
            OnUi(() => cmds.First(c => c.Title == "Return to live time").Run());
            CheckTrue("Back to live", AppState.IsLive);
            OnUi(() =>
            {
                using (var palette = new CommandPalette(cmds))
                {
                    var box = Find<TextBox>(palette);
                    CheckTrue("Command palette builds with a search box", box != null);
                }
            });
        }

        [TestMethod]
        public void TestWindowsHaveTheAppIcon()
        {
            // windows Sidralune opens (API help, Image information) show its icon, not Windows' generic one
            OnUi(() =>
            {
                CheckTrue("Sidralune's icon loads", MainForm.AppIcon != null);
                CheckTrue("The main window has Sidralune's icon", ReferenceEquals(App.Icon, MainForm.AppIcon));
                using (var f = Views.ProcessingView.TextWindow("Script API", "text"))
                    CheckTrue("A text window has Sidralune's icon", ReferenceEquals(f.Icon, MainForm.AppIcon));
            });
        }

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);

        // DWMWA_CLOAKED, only the app's own cloak (DWM_CLOAKED_APP): Windows also cloaks windows itself (DWM_CLOAKED_SHELL),
        // for example on another virtual desktop or while the session is locked, which made the test fail now and then
        private static bool Cloaked(Form f) { int v; return DwmGetWindowAttribute(f.Handle, 14, out v, 4) == 0 && (v & 1) != 0; }

        [TestMethod]
        public void TestWindowsOpenWithoutFlash()
        {
            // a new window is kept off the screen until it has painted once (its text box showed white for a moment, blinding
            // in night mode), and is then shown: the main window and a text window (API help)
            CheckTrue("The main window is shown", OnUi(() => !Cloaked(App)));
            Form f = null;
            try
            {
                OnUi(() =>
                {
                    f = Views.ProcessingView.TextWindow("Script API", "text");
                    f.StartPosition = FormStartPosition.Manual; f.Location = new Point(-3000, 0); f.ShowInTaskbar = false;
                    var h = f.Handle; // the window exists but has not been shown or painted
                });
                CheckTrue("A new text window is hidden until it is painted", OnUi(() => Cloaked(f)));
                OnUi(() => f.Show(App));
                CheckTrue("The text window is shown once it has painted", WaitFor(() => f.Visible && !Cloaked(f)));
            }
            finally { OnUi(() => f?.Dispose()); }
        }

        [TestMethod]
        public void TestPrintPreview()
        {
            // Sidralune's print preview: the app's icon and colours, every page, the paper as printed by day and red on dark paper at night
            var doc = new System.Drawing.Printing.PrintDocument();
            int printed = 0;
            doc.BeginPrint += (s, e) => printed = 0;
            doc.PrintPage += (s, e) =>
            {
                var b = e.MarginBounds;
                e.Graphics.FillRectangle(Brushes.Black, b.X + b.Width / 4, b.Y + b.Height / 4, b.Width / 2, b.Height / 2); // ink in the middle, paper at the edges
                e.HasMorePages = ++printed < 2;
            };
            OnUi(() =>
            {
                if (!doc.PrinterSettings.IsValid) { Assert.Inconclusive("No printer is installed"); return; }
                using (var w = new PrintPreviewWindow(doc, "Preview test", false))
                {
                    // it opens at once and draws the pages after it is shown
                    CheckTrue("The preview opens before the pages are drawn", w.Pages.Length == 0 && printed == 0);
                    w.Generate();
                    CheckTrue("The preview has Sidralune's icon", ReferenceEquals(w.Icon, MainForm.AppIcon));
                    CheckTrue("The preview has the app's background", w.BackColor == Theme.Current.Background);
                    CheckTrue("The preview has every page", w.Pages.Length == 2);
                    var size = new Size(200, 280);
                    using (var day = PrintPreviewWindow.RenderPage(w.Pages[0], size, false))
                    using (var night = PrintPreviewWindow.RenderPage(w.Pages[0], size, true))
                    {
                        Color paper = day.GetPixel(4, 4), ink = day.GetPixel(100, 140);
                        CheckTrue("By day the paper is white and the ink black", paper.R > 240 && paper.G > 240 && paper.B > 240 && ink.R < 20 && ink.G < 20 && ink.B < 20);
                        paper = night.GetPixel(4, 4); ink = night.GetPixel(100, 140);
                        CheckTrue("At night the paper is dark and the ink red", paper.R < 60 && ink.R > 150 && ink.G < 30 && ink.B < 30 && paper.G < 10 && paper.B < 10);
                    }
                }
            });
        }
    }
}
