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
