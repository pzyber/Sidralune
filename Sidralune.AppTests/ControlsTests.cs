using System;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sidralune.Devices.Automation;
using Sidralune.Tests;
using Sidralune.UI;
using static Sidralune.AppTests.AppHost;
using static Sidralune.Tests.Checks;

namespace Sidralune.AppTests
{
    /// <summary>The app's own controls: buttons, tabs, collapsible sections, form grids, time fields, sliders, pills, notifications and the property-grid adapter.</summary>
    [TestClass]
    public class ControlsTests : TestBase
    {
        public enum Mode { Fast, Careful }

        public class Inner { public double Value = 1.5; }

        public class Options
        {
            public double ExposureTime = 10;
            public int FrameCount { get; set; } = 3;
            public Mode Mode = Mode.Fast;
            public Inner Advanced = new Inner();
            [TypeConverter(typeof(VideoRegionConverter))]
            public string Region { get; set; } = "640 × 480";
            public string ReadOnlyThing => "x";
        }

        [TestMethod]
        public void TestLabelStack()
        {
            var size = new Size(60, 15);
            Func<Point[], Size[], bool> clear = (pos, sz) =>
                !Enumerable.Range(0, pos.Length).Any(i => Enumerable.Range(i + 1, pos.Length - i - 1).Any(j => new Rectangle(pos[i], sz[i]).IntersectsWith(new Rectangle(pos[j], sz[j]))));
            // apart, each name keeps its side: under, or over with its foot 5 px above the dot
            var apart = LabelStack.Place(new[] { new PointF(100, 100), new PointF(300, 100) }, new[] { size, size }, new[] { false, true }, 5, 5);
            CheckTrue("Names apart keep their side", apart[0] == new Point(70, 105) && apart[1] == new Point(270, 80), apart[0] + " " + apart[1]);
            // two dots at one place, both preferring below: the second goes over
            var two = LabelStack.Place(new[] { new PointF(100, 100), new PointF(102, 101) }, new[] { size, size }, new[] { false, false }, 5, 5);
            CheckTrue("Two names at one place, one under and one over", clear(two, new[] { size, size }) && two[0].Y > 100 && two[1].Y + 15 < 100, two[0] + " " + two[1]);
            // four at one place: stacked further out, none overlapping
            var dots = Enumerable.Range(0, 4).Select(i => new PointF(100 + i, 100)).ToArray();
            var sizes = Enumerable.Repeat(size, 4).ToArray();
            CheckTrue("Four names at one place, none overlapping", clear(LabelStack.Place(dots, sizes, new[] { false, true, false, true }, 5, 5), sizes));

            // Io and Ganymede side by side on 1 November 2026 (as in Tonight and Solar system → Planets & moons)
            var time = Sidralune.Core.Time.AstroTime.FromDateTime(new DateTime(2026, 11, 1, 1, 43, 0, DateTimeKind.Utc));
            var moons = Sidralune.Science.SolarSystem.GalileanMoons.Positions(new Sidralune.Core.SkyContext(AppState.Site, time).JdTT);
            var shown = moons.Where(m => !m.Occulted).ToArray();
            CheckTrue("Io and Ganymede within 1.5 Jupiter radii of each other", Math.Abs(moons[0].X - moons[2].X) < 1.5 && Math.Abs(moons[0].Y - moons[2].Y) < 1.5,
                moons[0].X.ToString("0.00") + " " + moons[2].X.ToString("0.00"));
            foreach (var scale in new[] { 10f, 20f })
            {
                var names = OnUi(() => shown.Select(m => TextRenderer.MeasureText(m.Name, Theme.SmallFont)).ToArray());
                var at = LabelStack.Place(shown.Select(m => new PointF(300 + (float)m.X * scale, 60 - (float)m.Y * scale)).ToList(), names, shown.Select(m => Array.IndexOf(moons, m) % 2 == 1).ToList(), 5, 5);
                CheckTrue("Their names do not overlap at " + scale + " px per Jupiter radius", clear(at, names));
            }
        }

        [TestMethod]
        public void TestButtonsAndTabs()
        {
            OnUi(() =>
            {
                var b = new FlatButton("Go", Icons.Play);
                int w1 = b.Width;
                b.Text = "A much longer caption"; b.AutoSizeToText();
                CheckTrue("Buttons size to their text", b.Width > w1 + 50);
                int clicks = 0, toggles = 0;
                b.Click += (s, e) => clicks++;
                b.Toggle = true; b.CheckedChanged += (s, e) => toggles++;
                b.PerformClick(); b.PerformClick(); b.PerformClick();
                CheckTrue("Toggle buttons flip on click", clicks == 3 && toggles == 3 && b.Checked);

                var tabs = new SegmentedTabs();
                tabs.Tabs.AddRange(new[] { "One", "Two", "Three" });
                int changed = -1;
                tabs.SelectedChanged += i => changed = i;
                tabs.SelectedIndex = 2;
                CheckTrue("Segmented tabs report the selection", changed == 2 && tabs.SelectedIndex == 2);
                tabs.SelectedIndex = 7; tabs.SelectedIndex = -1;
                CheckTrue("Out-of-range selection ignored", tabs.SelectedIndex == 2);

                var panel = new TabbedPanel { Size = new Size(400, 300) };
                var p1 = panel.AddPage("First", new Label { Text = "1" });
                var p2 = panel.AddPage("Second", new Label { Text = "2" });
                panel.Select(1);
                CheckTrue("Tabbed panel shows the selected page", panel.Header.Tabs.Count == 2 && p2.Visible && !p1.Visible);
                panel.Dispose(); b.Dispose(); tabs.Dispose();
            });
        }

        [TestMethod]
        public void TestLayoutControls()
        {
            OnUi(() =>
            {
                var host = new Form { Size = new Size(500, 600), StartPosition = FormStartPosition.Manual, Location = new Point(-2000, 0), ShowInTaskbar = false };
                var grid = new FormGrid(100) { Dock = DockStyle.Top, AutoSize = true };
                var tb = grid.Row("Name", new TextBox());
                var num = grid.Row("Count", Ui.Num(5, 0, 10));
                grid.Full(Ui.Hint("A hint"));
                var col = new Collapsible("Section", grid) { Dock = DockStyle.Top };
                host.Controls.Add(col);
                host.Show();
                CheckTrue("Form grid rows: label then editor", grid.GetControlFromPosition(1, 0) == tb && grid.GetControlFromPosition(0, 0) is Label && ((Label)grid.GetControlFromPosition(0, 0)).Text == "Name" && grid.GetControlFromPosition(1, 1) == num);
                int open = col.Height;
                col.Expanded = false;
                int closed = col.Height;
                col.Expanded = true;
                CheckTrue("Collapsible sections fold and unfold", closed < open && col.Height == open && col.Body.Visible, open + " → " + closed);
                CheckTrue("Numeric box limits", num.Minimum == 0 && num.Maximum == 10 && num.Value == 5);
                var combo = Ui.Combo("a", "b", "c");
                CheckTrue("Combo selects the first item", combo.SelectedIndex == 0 && combo.Items.Count == 3);
                var ec = Ui.EnumCombo(Mode.Careful);
                CheckTrue("Enum combo selects the value", ec.SelectedItem.ToString() == "Careful");
                host.Close(); host.Dispose();
            });
        }

        [TestMethod]
        public void TestTimeFieldSliderAndPill()
        {
            OnUi(() =>
            {
                var tf = new TimeField();
                DateTime? committed = null;
                tf.ValueCommitted += d => committed = d;
                tf.Text = "2026-10-24 21:30";
                Call(tf, "OnKeyDown", new KeyEventArgs(Keys.Enter));
                CheckTrue("Time field commits on Enter", committed == new DateTime(2026, 10, 24, 21, 30, 0) && tf.Value == committed.Value);
                committed = null;
                tf.Text = "not a time";
                Call(tf, "OnLostFocus", EventArgs.Empty);
                CheckTrue("Invalid text reverts without committing", committed == null && tf.Text == "2026-10-24 21:30");
                tf.Text = "2026-10-25 01:00";
                Call(tf, "OnKeyDown", new KeyEventArgs(Keys.Escape));
                CheckTrue("Escape reverts", tf.Text == "2026-10-24 21:30");

                var sl = new Slider { Minimum = 0, Maximum = 100 };
                int changes = 0;
                sl.ValueChanged += (s, e) => changes++;
                sl.Value = 150;
                CheckTrue("Slider clamps to its range", sl.Value == 100 && changes >= 1);
                sl.Value = -5;
                CheckTrue("Slider lower limit", sl.Value == 0);

                var pill = new StatusPill();
                pill.Set("Camera · Simulator", Color.Green);
                CheckTrue("Status pill text and width", pill.Text == "Camera · Simulator" && pill.NaturalWidth > pill.TextWidth && pill.DotColor == Color.Green);
                pill.MaxTextWidth = 30;
                CheckTrue("Status pill shortens to a maximum width", pill.Width <= 30 + pill.NaturalWidth - pill.TextWidth);
            });
        }

        [TestMethod]
        public void TestNotifications()
        {
            App.Toast("Hello from a test");
            CheckTrue("Notification shown", WaitFor(() => Toasts().Contains("Hello from a test")));
            App.Toast("Analyse & stack");
            CheckTrue("An & in a notification is shown, not taken as a shortcut marker", WaitFor(() => OnUi(() => All(App).OfType<Label>().Any(l => l.Text == "Analyse & stack" && !l.UseMnemonic))));
            var rect = OnUi(() => App.Controls.OfType<Panel>().First(p => p.Controls.OfType<Label>().Any(l => l.Text == "Hello from a test")).Bounds);
            CheckTrue("Notification in the window's lower right corner", OnUi(() => rect.Right <= App.ClientSize.Width && rect.Bottom <= App.ClientSize.Height && rect.Left > App.ClientSize.Width / 2));
            CheckTrue("Notification disappears by itself", WaitFor(() => !Toasts().Contains("Hello from a test"), 8000));
        }

        [TestMethod]
        public void TestGridAdapter()
        {
            var o = new Options();
            var props = TypeDescriptor.GetProperties(new GridAdapter(o, "Test"));
            var names = props.Cast<PropertyDescriptor>().Select(p => p.DisplayName).ToList();
            CheckTrue("Fields and settable properties, names split into words", names.Contains("Exposure Time") && names.Contains("Frame Count") && names.Contains("Mode") && names.Contains("Advanced") && !names.Contains("Read Only Thing"), string.Join(", ", names));
            props["ExposureTime"].SetValue(null, 30.0);
            props["FrameCount"].SetValue(null, 7);
            CheckTrue("Values written through the grid", o.ExposureTime == 30 && o.FrameCount == 7);
            CheckTrue("Nested option objects expand", props["Advanced"].Converter is ExpandableObjectConverter && props["Advanced"].GetValue(null) is GridAdapter);
            CheckTrue("Enums get a drop-down", props["Mode"].Converter.GetStandardValuesSupported());
            CheckTrue("A member's own converter is used", props["Region"].Converter is VideoRegionConverter);
            CheckTrue("Category", props["Mode"].Category == "Test");
            CheckTrue("Words", GridAdapter.Words("MaxFrames") == "Max Frames" && GridAdapter.Words("FWHM") == "FWHM" && GridAdapter.Words("Bin") == "Bin");
        }
    }
}
