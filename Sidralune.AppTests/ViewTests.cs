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
            }
            finally { OnUi(() => { form.Close(); AppState.GoLive(); }); }
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

        private static bool Cloaked(Form f) { int v; return DwmGetWindowAttribute(f.Handle, 14, out v, 4) == 0 && v != 0; } // DWMWA_CLOAKED

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
