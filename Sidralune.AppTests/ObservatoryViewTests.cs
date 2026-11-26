using System;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sidralune.Core.Mathematics;
using Sidralune.Devices;
using Sidralune.Devices.Automation;
using Sidralune.Devices.Guiding;
using Sidralune.Devices.Simulators;
using Sidralune.Tests;
using Sidralune.UI;
using Sidralune.Views;
using static Sidralune.AppTests.AppHost;
using static Sidralune.Tests.Checks;

namespace Sidralune.AppTests
{
    /// <summary>The Observatory workspace: equipment, the one-camera-job rule, messages for missing devices and the sequencer.</summary>
    [TestClass]
    public class ObservatoryViewTests : TestBase
    {
        private static DeviceManager Dm => DeviceManager.Instance;

        [TestMethod]
        public void TestEquipment()
        {
            var view = View<ObservatoryView>("observatory");
            Func<FlatButton> mountStop = () => Find<FlatButton>(view, b => b.ToolTipText == "Stop the mount");
            try
            {
                OnUi(() => Dm.DisconnectAll());
                CheckTrue("With nothing connected, Disconnect all and the mount's Stop are greyed out", WaitFor(() => OnUi(() => !Button(view, "Disconnect all").Enabled && !mountStop().Enabled)));
                OnUi(() => Button(view, "Connect simulated observatory").PerformClick());
                CheckTrue("Simulated observatory connected", WaitFor(() => Dm.Telescope != null && Dm.Camera != null && Dm.Focuser != null && Dm.Dome != null, 15000));
                CheckTrue("Rows show Disconnect for connected devices", WaitFor(() => All(view).OfType<FlatButton>().Count(b => b.Text == "Disconnect") >= 6));
                // the rows of the equipment table (the Guiding tab has a Connect of its own)
                var widths = OnUi(() => All(view).OfType<FlatButton>().Where(b => (b.Text == "Disconnect" || b.Text == "Connect") && b.Parent is TableLayoutPanel).Select(b => b.Width).Distinct().ToList());
                CheckTrue("Connect and Disconnect buttons keep one width", widths.Count == 1, string.Join(", ", widths));
                CheckTrue("Equipment buttons wait while the simulators connect, then come back", WaitFor(() => OnUi(() => Button(view, "Disconnect all").Enabled), 15000));
                CheckTrue("The mount's Stop is available with a telescope connected", WaitFor(() => OnUi(() => mountStop().Enabled)));
                OnUi(() => Button(view, "Disconnect all").PerformClick());
                CheckTrue("Disconnect all disconnects every device", WaitFor(() => !Dm.All.Any(), 15000));
                CheckTrue("Rows back to Connect and Not connected", WaitFor(() => All(view).OfType<FlatButton>().All(b => b.Text != "Disconnect") && All(view).OfType<Label>().Count(l => l.Text == "Not connected") >= 6));
                CheckTrue("Disconnect all and the mount's Stop are greyed out again", WaitFor(() => OnUi(() => !Button(view, "Disconnect all").Enabled && !mountStop().Enabled)));
            }
            finally { Dm.DisconnectAll(); }
        }

        [TestMethod]
        public void TestGuidingTab()
        {
            var view = View<ObservatoryView>("observatory");
            try
            {
                OnUi(() => Dm.DisconnectAll());
                // a tab built with nothing connected says what to connect, in the frame and the status line
                var fresh = OnUi(() => new ObservatoryView { Size = new System.Drawing.Size(1500, 900) });
                CheckTrue("With no guider, the empty guide frame says what to connect", OnUi(() => Find<ImageViewer>(fresh, v => v.EmptyText == "Connect a guide camera in Observatory → Equipment, then click Loop") != null));
                CheckTrue("…and the status line too, without sending you to the tab it is on", WaitFor(() => OnUi(() => All(view).OfType<Label>().Any(l => l.Text == "No guider connected. Connect a guide camera in Observatory → Equipment, or click Connect PHD2."))));
                CheckTrue("Equipment has a guide camera row, and no guider row (the Sidralune guider is built in)", OnUi(() => All(view).OfType<Label>().Any(l => l.Text == "Guide camera") && !All(view).OfType<Label>().Any(l => l.Text == "Guider")));
                CheckTrue("PHD2 is connected on the Guiding tab", OnUi(() => Button(Button(view, "Start guiding").Parent, "Connect PHD2") != null));
                OnUi(() => Button(view, "Connect simulated observatory").PerformClick());
                CheckTrue("With the simulated guide camera the Sidralune guider guides", WaitFor(() => Dm.GuideCamera != null && Dm.Guider == Dm.BuiltinGuider, 15000));
                CheckTrue("With a guide camera connected, Connect PHD2 is greyed out", WaitFor(() => OnUi(() =>
                {
                    var b = Button(Button(view, "Start guiding").Parent, "Connect PHD2");
                    return b != null && !b.Enabled;
                })));
                var g = (SidraluneGuider)Dm.Guider;
                var tel = (TelescopeSimulator)Dm.Telescope;
                // quick to calibrate: a fast guide rate and short exposures
                tel.SlewRateDegPerSec = 90;
                tel.GuideRateRa = tel.GuideRateDec = 2 * TelescopeSimulator.SiderealDegPerSec;
                Centering.Slew(tel, tel.SiderealTime - 0.5, 30, CancellationToken.None).Wait();
                g.Settings.ExposureSeconds = 0.2; g.Settings.FocalLengthMm = 500;
                // the simulated seeing at these short exposures moves the star more than 1.5 px now and then: settle looser and sooner
                g.SettlePixels = 3; g.SettleSeconds = 2;
                g.ClearCalibration();

                var bar = OnUi(() => Button(view, "Start guiding").Parent);
                var viewer = OnUi(() => Find<ImageViewer>(view, v => v.EmptyText.StartsWith("Connect a guide camera in Observatory")));
                CheckTrue("A guide camera view", viewer != null);
                OnUi(() => Button(bar, "Loop").PerformClick());
                CheckTrue("Loop shows guide frames", WaitFor(() => viewer.Image != null && g.State == SidraluneGuider.Looping, 10000), g.State);
                CheckTrue("…and Stop becomes available", WaitFor(() => Button(bar, "Stop").Enabled));
                CheckTrue("Before any guide steps the status line shows the state, without an RMS", WaitFor(() => All(view).OfType<Label>().Any(l => l.Text == "Sidralune guider · Looping")) && OnUi(() => !All(view).OfType<Label>().Any(l => l.Text.Contains("NaN"))));
                var notCalibrated = OnUi(() => All(view).OfType<Label>().FirstOrDefault(l => l.Text.StartsWith("Not calibrated.")));
                CheckTrue("The calibration text shows \"Calibrate & guide\" with its &", notCalibrated != null && notCalibrated.Text.Contains("Calibrate & guide") && !notCalibrated.UseMnemonic);
                OnUi(() => Button(bar, "Select star").PerformClick());
                CheckTrue("Select star chooses one", WaitFor(() => g.Star != null && g.State == SidraluneGuider.Selected, 5000), g.State);
                CheckTrue("The star is described under the frame", WaitFor(() => All(view).OfType<Label>().Any(l => l.Text.StartsWith("Star at ")), 5000));

                var camCard = OnUi(() => All(view).OfType<Collapsible>().First(c => c.Title == "Guide camera"));
                var exposure = OnUi(() => All(camCard).OfType<NumericUpDown>().First());
                CheckTrue("The settings show the guider's", OnUi(() => exposure.Value == 0.2m || exposure.Enabled));
                OnUi(() => exposure.Value = 0.25m);
                CheckTrue("A changed setting reaches the guider and is saved", g.Settings.ExposureSeconds == 0.25 && GuiderSettings.Load().ExposureSeconds == 0.25);
                CheckTrue("…and leaves the other settings alone (the focal length set in code stays)", g.Settings.FocalLengthMm == 500, g.Settings.FocalLengthMm.ToString());

                // the guider's pages, with the Guiding tab on screen so Visible says what is shown
                OnUi(() => { var tabs = All(view).OfType<TabbedPanel>().First(t => t.Header.Tabs.Contains("Guiding")); tabs.Select(tabs.Header.Tabs.IndexOf("Guiding")); });
                var side = OnUi(() => All(view).OfType<TabbedPanel>().FirstOrDefault(t => t.Header.Tabs.Contains("Assistants")));
                CheckTrue("The guider has Settings, Assistants, Darks and Tools pages", side != null && OnUi(() => side.Header.Tabs.SequenceEqual(new[] { "Settings", "Assistants", "Darks", "Tools" })));
                var decCard = OnUi(() => All(view).OfType<Collapsible>().First(c => c.Title == "Dec"));
                var decAlgo = OnUi(() => All(decCard).OfType<ComboBox>().First());
                Func<string, bool> rowShown = label => OnUi(() => All(decCard).OfType<Label>().Any(l => l.Text == label && l.Visible));
                CheckTrue("Resist Switch shows its own rows", rowShown("Dec aggressiveness (%)") && !rowShown("Lowpass2 aggressiveness (%)"));
                OnUi(() => decAlgo.SelectedIndex = decAlgo.Items.IndexOf("Lowpass2"));
                CheckTrue("Choosing Lowpass2 shows its rows instead", rowShown("Lowpass2 aggressiveness (%)") && !rowShown("Dec aggressiveness (%)") && !rowShown("Lowpass slope weight"));
                CheckTrue("…and is saved", g.Settings.DecAlgorithm == GuideAlgorithmKind.Lowpass2 && GuiderSettings.Load().DecAlgorithm == GuideAlgorithmKind.Lowpass2);
                OnUi(() => decAlgo.SelectedIndex = decAlgo.Items.IndexOf("Lowpass"));
                CheckTrue("Choosing Lowpass shows its slope weight", rowShown("Lowpass slope weight") && !rowShown("Lowpass2 aggressiveness (%)"));
                CheckTrue("…and is saved", g.Settings.DecAlgorithm == GuideAlgorithmKind.Lowpass && GuiderSettings.Load().DecAlgorithm == GuideAlgorithmKind.Lowpass);
                OnUi(() => decAlgo.SelectedIndex = 0);

                OnUi(() => Button(bar, "Start guiding").PerformClick());
                CheckTrue("While a start runs, the other guiding commands wait", OnUi(() => !Button(bar, "Calibrate & guide").Enabled && !Button(bar, "Dither").Enabled));
                CheckTrue("Start guiding calibrates, then guides", WaitFor(() => g.IsGuiding, 60000), g.State);
                var plot = OnUi(() => Find<PlotControl>(view, p => p.Title == "Guiding error"));
                CheckTrue("Guide steps reach the graph", WaitFor(() => plot.Series.Count == 2 && plot.Series[0].X.Count >= 5, 15000), string.Format("state {0}, history {1}, points {2}, guider {3}", g.State, g.History.Count, plot.Series[0].X.Count, Dm.Guider == g));
                CheckTrue("The calibration is described", OnUi(() => All(view).OfType<Label>().Any(l => l.Text.StartsWith("Calibrated "))));
                CheckTrue("The status line names the state and the RMS", WaitFor(() => All(view).OfType<Label>().Any(l => l.Text.StartsWith("Sidralune guider · Guiding · RMS RA "))), OnUi(() => string.Join(" | ", All(view).OfType<Label>().Where(l => l.Text.Contains("Sidralune guider")).Select(l => l.Text))));
                CheckTrue("…and how many stars it guides on", WaitFor(() => All(view).OfType<Label>().Any(l => l.Text.StartsWith("Sidralune guider · Guiding · RMS RA ") && l.Text.Contains(" stars"))));

                // manual pulses wait while guiding
                var manual = OnUi(() => All(view).OfType<Collapsible>().First(c => c.Title == "Manual guide"));
                CheckTrue("Manual guide waits while guiding", WaitFor(() => OnUi(() => All(manual).OfType<FlatButton>().All(b => !b.Enabled))));
                // the lock position, typed in on the Tools page
                var lockCard = OnUi(() => All(view).OfType<Collapsible>().First(c => c.Title == "Lock position"));
                var nums = OnUi(() => All(lockCard).OfType<NumericUpDown>().Take(2).ToList());
                double nx = Math.Round(g.LockX + 2, 2), ny = Math.Round(g.LockY + 1, 2);
                OnUi(() => { side.Select(3); nums[0].Value = (decimal)nx; nums[1].Value = (decimal)ny; Button(lockCard, "Set").PerformClick(); });
                CheckTrue("Set moves the lock position", Math.Abs(g.LockX - nx) < 1e-9 && Math.Abs(g.LockY - ny) < 1e-9, g.LockX + ", " + g.LockY);
                CheckTrue("…and the stars are guided there", WaitFor(() => Math.Abs(g.Star.X - nx) < 0.7 && Math.Abs(g.Star.Y - ny) < 0.7, 15000), g.Star.ToString());

                // the Guiding Assistant, briefly, without the backlash test
                var assist = OnUi(() => All(view).OfType<Collapsible>().First(c => c.Title == "Guiding Assistant"));
                OnUi(() => { side.Select(1); All(assist).OfType<CheckBox>().First().Checked = false; });
                // once Start guiding has settled (it waits for the stars to stay put, which a paused guider would not do)
                CheckTrue("The Guiding Assistant can start while guiding", WaitFor(() => OnUi(() => Button(assist, "Start measuring").Enabled), 40000));
                OnUi(() => Button(assist, "Start measuring").PerformClick());
                CheckTrue("It pauses corrections", WaitFor(() => g.State == SidraluneGuider.Paused, 3000), g.State);
                Thread.Sleep(4000);
                OnUi(() => Button(assist, "Stop and analyse").PerformClick());
                // the live text shows the seeing too: the result is in once its suggestions (and Apply) are there
                CheckTrue("It describes what it measured", WaitFor(() => OnUi(() => All(assist).OfType<Label>().Any(l => l.Text.Contains("Seeing: RA ")) && Button(assist, "Apply the ticked settings").Visible), 15000));
                CheckTrue("…and guiding resumes", WaitFor(() => g.IsGuiding, 5000), g.State);
                OnUi(() => Button(assist, "Apply the ticked settings").PerformClick());
                double suggested = Math.Max(0.05, Math.Round(0.65 * g.Settings.AssistantResults[0].SeeingRaArcsec / g.PixelScale, 2));
                CheckTrue("Apply sets the suggested minimum move", Math.Abs(g.Settings.RaMinMovePx - suggested) < 1e-9, g.Settings.RaMinMovePx + " vs " + suggested);

                OnUi(() => Button(bar, "Stop").PerformClick());
                CheckTrue("Stop stops the guider and greys out", WaitFor(() => g.State == SidraluneGuider.Stopped && !Button(bar, "Stop").Enabled, 10000), g.State);

                // dark frames: the guide scope must be covered first; Cancel takes none
                var darkCard = OnUi(() => All(view).OfType<Collapsible>().First(c => c.Title == "Dark library"));
                var cover = OnUi(() => All(view).OfType<Collapsible>().First(c => c.Title == "Taking dark frames"));
                OnUi(() => side.Select(2));
                CheckTrue("Build dark library is available with the guider stopped", WaitFor(() => OnUi(() => Button(darkCard, "Build dark library").Enabled), 5000));
                OnUi(() => Button(darkCard, "Build dark library").PerformClick());
                CheckTrue("It asks to cover the guide scope first", OnUi(() => cover.Visible && All(cover).OfType<Label>().Any(l => l.Visible && l.Text.StartsWith("Cover the guide scope"))));
                OnUi(() => Button(cover, "Cancel").PerformClick());
                CheckTrue("Cancel takes no dark frames", OnUi(() => !cover.Visible) && !g.IsRunning && WaitFor(() => OnUi(() => Button(darkCard, "Build dark library").Enabled), 3000));
                // while they are taken: the guider cannot loop or guide (greyed out), and the dark settings stay editable
                OnUi(() => Button(darkCard, "Build dark library").PerformClick());
                OnUi(() => Button(cover, "Continue").PerformClick());
                CheckTrue("While dark frames are taken, Loop, Select star and Start guiding are greyed out",
                    WaitFor(() => g.CameraInUse && OnUi(() => !Button(bar, "Loop").Enabled && !Button(bar, "Select star").Enabled && !Button(bar, "Start guiding").Enabled), 10000));
                CheckTrue("…the dark settings stay editable", OnUi(() => All(darkCard).OfType<NumericUpDown>().All(n => n.Enabled)));
                OnUi(() => Button(cover, "Stop").PerformClick());
                CheckTrue("Stopped, the guider can loop again", WaitFor(() => !g.CameraInUse && OnUi(() => Button(bar, "Loop").Enabled && Button(bar, "Start guiding").Enabled), 15000));
            }
            finally
            {
                Dm.DisconnectAll();
                Dm.BuiltinGuider.SettlePixels = 1.5; Dm.BuiltinGuider.SettleSeconds = 10;
                OnUi(() => All(view).OfType<TabbedPanel>().First(t => t.Header.Tabs.Contains("Guiding")).Select(0));
            }
        }

        [TestMethod]
        public void TestGuiderAdvancedUi()
        {
            var view = View<ObservatoryView>("observatory");
            var g = Dm.BuiltinGuider;
            Func<string, Collapsible> card = title => OnUi(() => All(view).OfType<Collapsible>().First(c => c.Title == title));
            Func<Control, string, bool> shown = (parent, label) => OnUi(() => All(parent).OfType<Label>().Any(l => l.Text == label && l.Visible));
            Func<Control, string, bool> has = (parent, label) => OnUi(() => All(parent).OfType<Label>().Any(l => l.Text == label));
            try
            {
                OnUi(() => Dm.DisconnectAll());
                CheckTrue("Equipment has an AO row", has(view, "AO"));
                OnUi(() => { var tabs = All(view).OfType<TabbedPanel>().First(t => t.Header.Tabs.Contains("Guiding")); tabs.Select(tabs.Header.Tabs.IndexOf("Guiding")); });
                var graphs = OnUi(() => All(view).OfType<TabbedPanel>().FirstOrDefault(t => t.Header.Tabs.Contains("Target")));
                CheckTrue("The graph has Graph, Target and Star profile pages", graphs != null && OnUi(() => graphs.Header.Tabs.SequenceEqual(new[] { "Graph", "Target", "Star profile" })));
                var side = OnUi(() => All(view).OfType<TabbedPanel>().First(t => t.Header.Tabs.Contains("Assistants")));
                OnUi(() => side.Select(0));

                // Predictive PEC: its rows when chosen, and its state
                var raCard = card("RA");
                var raAlgo = OnUi(() => All(raCard).OfType<ComboBox>().First());
                int pecIndex = OnUi(() => raAlgo.Items.Cast<object>().Select(o => o.ToString()).ToList().IndexOf("Predictive PEC"));
                CheckTrue("Predictive PEC is one of the RA algorithms", pecIndex > 0);
                CheckTrue("Its rows are hidden for Hysteresis", OnUi(() => raAlgo.SelectedIndex == 0) && !shown(raCard, "Worm period (s)") && shown(raCard, "RA hysteresis (%)"));
                OnUi(() => raAlgo.SelectedIndex = pecIndex);
                CheckTrue("Choosing Predictive PEC shows its rows", shown(raCard, "Predictive gain (%)") && shown(raCard, "Reactive gain (%)") && shown(raCard, "Worm period (s)") && shown(raCard, "Retain model (%)") && !shown(raCard, "RA hysteresis (%)"));
                CheckTrue("…and is saved", g.Settings.RaAlgorithm == GuideAlgorithmKind.PredictivePec && GuiderSettings.Load().RaAlgorithm == GuideAlgorithmKind.PredictivePec);
                CheckTrue("…with its state", WaitFor(() => shown(raCard, "Starts learning when guiding starts.")));
                OnUi(() => raAlgo.SelectedIndex = 0);

                CheckTrue("The guide camera card has subframes and noise reduction", shown(card("Guide camera"), "Subframes") && shown(card("Guide camera"), "Noise reduction"));
                CheckTrue("The calibration card has the rotator", shown(card("Calibration"), "Rotator") && shown(card("Calibration"), "Rotator direction"));
                var noise = OnUi(() => { var l = All(card("Guide camera")).OfType<Label>().First(x => x.Text == "Noise reduction"); var t = (TableLayoutPanel)l.Parent; return (ComboBox)t.GetControlFromPosition(1, t.GetRow(l)); });
                OnUi(() => noise.SelectedIndex = 2);
                CheckTrue("Noise reduction is saved", GuiderSettings.Load().NoiseReduction == NoiseReduction.Median3x3);
                OnUi(() => noise.SelectedIndex = 0);

                // an AO: its card, and its steps on the Tools page
                var aoCard = card("Adaptive optics");
                CheckTrue("No AO card without an AO", WaitFor(() => OnUi(() => !aoCard.Visible)));
                OnUi(() => Dm.Connect(new DeviceInfo { Kind = DeviceKind.AdaptiveOptics, Protocol = DeviceProtocol.Simulator, Name = "AO Simulator", Address = "sim" }));
                CheckTrue("Connecting an AO shows its card", WaitFor(() => OnUi(() => aoCard.Visible), 5000));
                CheckTrue("…saying it is not calibrated yet", WaitFor(() => OnUi(() => All(aoCard).OfType<Label>().Any(l => l.Text.StartsWith("The AO is not calibrated yet")))));
                CheckTrue("…with the bump settings", shown(aoCard, "Bump the mount at (%)") && shown(aoCard, "Bump step (″)"));
                OnUi(() => side.Select(3));
                var manual = card("Manual guide");
                CheckTrue("Manual guide gains AO steps", WaitFor(() => shown(manual, "AO steps"), 5000));
                OnUi(() => { side.Select(0); Dm.Disconnect(DeviceKind.AdaptiveOptics); });
                CheckTrue("Without the AO its card goes", WaitFor(() => OnUi(() => !aoCard.Visible), 5000));

                // the PHD2-compatible server
                var srvCard = card("PHD2-compatible server");
                var srvOn = OnUi(() => All(srvCard).OfType<CheckBox>().First());
                var port = OnUi(() => All(srvCard).OfType<NumericUpDown>().First());
                CheckTrue("The server is off, its port hidden", OnUi(() => !srvOn.Checked) && !shown(srvCard, "Port") && WaitFor(() => shown(srvCard, "Off")));
                OnUi(() => { port.Value = 4479; srvOn.Checked = true; });
                CheckTrue("Turning it on listens on the port", WaitFor(() => Dm.GuiderServer.Running && Dm.GuiderServer.Port == 4479, 5000));
                CheckTrue("…and says so", WaitFor(() => shown(srvCard, "Listening on port 4479 · 0 connected"), 5000));
                CheckTrue("…and is saved", GuiderSettings.Load().ServerEnabled && GuiderSettings.Load().ServerPort == 4479);
                OnUi(() => srvOn.Checked = false);
                CheckTrue("Off stops it", WaitFor(() => !Dm.GuiderServer.Running, 5000) && !GuiderSettings.Load().ServerEnabled);

                // the polar alignment methods
                var method = Field<ComboBox>(view, "_polarMethod");
                CheckTrue("Polar alignment offers four methods", OnUi(() => method.Items.Cast<object>().Select(o => o.ToString()).SequenceEqual(new[] { "Three-point (plate solving)", "Drift alignment", "Static polar alignment", "Polar drift alignment" })));
                OnUi(() => { var tabs = All(view).OfType<TabbedPanel>().First(t => t.Header.Tabs.Contains("Polar alignment")); tabs.Select(tabs.Header.Tabs.IndexOf("Polar alignment")); });
                Func<string, bool> cardShown = title => OnUi(() => All(view).OfType<Card>().Any(c => c.Title == title && c.Visible));
                CheckTrue("Three-point is the default", OnUi(() => method.SelectedIndex == 0) && cardShown("Three-point polar alignment") && !cardShown("Drift alignment"));
                CheckTrue("…with a Stop, greyed out while it is not running", OnUi(() => { var b = Button(Button(view, "Start polar alignment").Parent, "Stop"); return b != null && !b.Enabled; }));
                OnUi(() => method.SelectedIndex = 1);
                CheckTrue("Drift alignment has its page", cardShown("Drift alignment") && !cardShown("Three-point polar alignment"));
                CheckTrue("…which asks for the mount", WaitFor(() => OnUi(() => All(view).OfType<Label>().Any(l => l.Visible && l.Text == "Connect the mount: drift alignment needs to know where it points."))));
                CheckTrue("…and has a Stop, greyed out while nothing runs", OnUi(() => { var b = Button(All(view).OfType<Card>().First(c => c.Title == "Drift alignment"), "Stop"); return b != null && !b.Enabled; }));
                // the simulated mount starts at the pole: drift alignment asks to point nearer the equator first
                OnUi(() =>
                {
                    Dm.Connect(new DeviceInfo { Kind = DeviceKind.Telescope, Protocol = DeviceProtocol.Simulator, Name = "Telescope", Address = "sim" });
                    Dm.Connect(new DeviceInfo { Kind = DeviceKind.GuideCamera, Protocol = DeviceProtocol.Simulator, Name = "Guide camera", Address = "sim" });
                });
                OnUi(() => Button(All(view).OfType<Card>().First(c => c.Title == "Drift alignment"), "Start measuring").PerformClick());
                CheckTrue("At the pole, drift alignment asks to point nearer the equator", WaitFor(() => Toasts().Any(t => t.StartsWith("Point the mount nearer the celestial equator first"))) && !Dm.BuiltinGuider.IsRunning);
                OnUi(() => Dm.DisconnectAll());
                OnUi(() => method.SelectedIndex = 2);
                CheckTrue("Static polar alignment has its page", cardShown("Static polar alignment"));
                var staticCard = OnUi(() => All(view).OfType<Card>().First(c => c.Title == "Static polar alignment"));
                var staticCamera = OnUi(() => All(staticCard).OfType<ComboBox>().First());
                CheckTrue("…with a choice of camera, the imaging camera's exposure and binning shown",
                    WaitFor(() => OnUi(() => staticCamera.Text == "Imaging camera" && shown(staticCard, "Exposure (s)") && shown(staticCard, "Binning"))));
                OnUi(() => Button(staticCard, "Start alignment").PerformClick());
                CheckTrue("…which needs the camera", WaitFor(() => Toasts().Any(t => t.StartsWith("No camera connected."))));
                OnUi(() => Dm.Connect(new DeviceInfo { Kind = DeviceKind.GuideCamera, Protocol = DeviceProtocol.Simulator, Name = "Guide camera", Address = "sim" }));
                CheckTrue("With only a guide camera, it is the one used, without the exposure and binning",
                    WaitFor(() => OnUi(() => staticCamera.Text == "Guide camera" && !shown(staticCard, "Exposure (s)") && !shown(staticCard, "Binning"))));
                // with the imaging camera, the frames are a camera job: other camera work waits, and the page's Stop ends them
                OnUi(() =>
                {
                    Dm.DisconnectAll();
                    Dm.Connect(new DeviceInfo { Kind = DeviceKind.Telescope, Protocol = DeviceProtocol.Simulator, Name = "Telescope", Address = "sim" });
                    Dm.Connect(new DeviceInfo { Kind = DeviceKind.Camera, Protocol = DeviceProtocol.Simulator, Name = "Camera", Address = "sim" });
                });
                CheckTrue("With an imaging camera, it is the one used", WaitFor(() => OnUi(() => staticCamera.Text == "Imaging camera")));
                OnUi(() => Button(staticCard, "Start alignment").PerformClick());
                CheckTrue("Static polar alignment takes the imaging camera's frames as a camera job",
                    WaitFor(() => OnUi(() => Field<string>(view, "_cameraJobName") == "Static polar alignment" && Button(staticCard, "Stop").Enabled), 5000));
                CheckTrue("…holding guiding back meanwhile", g.GuidingHeldBy == "Static polar alignment");
                CheckTrue("Start alignment is greyed out while it runs: Stop ends it first", OnUi(() => !Button(staticCard, "Start alignment").Enabled));
                // its settings were read when it started: they stay editable, and a change is for the next alignment
                Func<string, Control> row = label => OnUi(() => { var l = Find<Label>(staticCard, x => x.Text == label); var t = (TableLayoutPanel)l.Parent; return t.GetControlFromPosition(1, t.GetRow(l)); });
                CheckTrue("Its settings stay editable while it runs", OnUi(() => staticCamera.Enabled && row("Exposure (s)").Enabled && row("Binning").Enabled && row("RA step (°)").Enabled && row("Turning RA").Enabled));
                OnUi(() => staticCamera.SelectedIndex = 1);
                Thread.Sleep(1500);
                CheckTrue("Choosing another camera while it runs leaves the running alignment on its camera",
                    OnUi(() => Button(staticCard, "Stop").Enabled && Field<string>(view, "_cameraJobName") == "Static polar alignment" && !Field<BackgroundTask>(view, "_cameraJob").Done) && !g.IsRunning);
                OnUi(() => staticCamera.SelectedIndex = 0);
                // nothing else that guides or moves the mount can start meanwhile: greyed out
                // the Guiding Assistant has a Start measuring too: drift alignment's is on its card
                var driftAlignCard = OnUi(() => All(view).OfType<Card>().First(c => c.Title == "Drift alignment"));
                Func<string, bool> greyed = text => OnUi(() => !Button(text == "Start measuring" ? (Control)driftAlignCard : view, text).Enabled);
                CheckTrue("Starting guiding, calibrating and dithering on the Guiding tab are greyed out",
                    WaitFor(() => greyed("Start guiding") && greyed("Calibrate & guide") && greyed("Dither")));
                CheckTrue("…and the other polar alignment methods", greyed("Start measuring") && greyed("Follow a star") && greyed("Start polar alignment"));
                CheckTrue("…and sequences and centring", greyed("Run sequence") && greyed("Center"));
                OnUi(() => Button(view, "Start guiding").PerformClick());
                CheckTrue("A greyed-out button does nothing", !g.IsRunning && !g.InGuidingPhase);
                // a camera tab's Stop ends the frames: the alignment ends with them, and says so
                OnUi(() => typeof(ObservatoryView).GetMethod("StopCameraWork", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Invoke(view, null));
                CheckTrue("A camera tab's Stop ends the alignment, not only its frames",
                    WaitFor(() => OnUi(() => shown(staticCard, "The frames stopped, so the alignment was stopped.") && !Button(staticCard, "Stop").Enabled), 15000));
                CheckTrue("…and guiding may start again", g.GuidingHeldBy == null && OnUi(() => Field<BackgroundTask>(view, "_cameraJob").Done));
                var freed = new[] { "Start guiding", "Calibrate & guide", "Start measuring", "Follow a star", "Run sequence" };
                CheckTrue("…its buttons available again", WaitFor(() => freed.All(t => !greyed(t))), string.Join(", ", freed.Where(t => greyed(t))));
                CheckTrue("Start alignment is available again", WaitFor(() => OnUi(() => Button(staticCard, "Start alignment").Enabled), 15000));
                OnUi(() => Button(staticCard, "Start alignment").PerformClick());
                CheckTrue("Started again", WaitFor(() => OnUi(() => Button(staticCard, "Stop").Enabled && Field<string>(view, "_cameraJobName") == "Static polar alignment" && !Field<BackgroundTask>(view, "_cameraJob").Done), 15000));
                OnUi(() => Button(staticCard, "Stop").PerformClick());
                CheckTrue("…and Stop ends them", WaitFor(() => OnUi(() => Field<BackgroundTask>(view, "_cameraJob").Done && !Button(staticCard, "Stop").Enabled), 15000));
                OnUi(() => Dm.DisconnectAll());
                // without a calibration (an earlier test may have left one), whether the image is mirrored is asked
                OnUi(() => { g.ClearCalibration(); method.SelectedIndex = 3; });
                CheckTrue("Polar drift alignment has its page", cardShown("Polar drift alignment"));
                var driftCard = OnUi(() => All(view).OfType<Card>().First(c => c.Title == "Polar drift alignment"));
                CheckTrue("…asking whether the imaging camera's image is mirrored", WaitFor(() => OnUi(() => All(driftCard).OfType<Label>().Any(l => l.Visible && l.Text == "Say whether the camera sees a mirror image."))));
                OnUi(() => Dm.Connect(new DeviceInfo { Kind = DeviceKind.GuideCamera, Protocol = DeviceProtocol.Simulator, Name = "Guide camera", Address = "sim" }));
                CheckTrue("…and the guide camera's, without a calibration", WaitFor(() => OnUi(() => All(driftCard).OfType<Label>().Any(l => l.Visible && l.Text.StartsWith("Without a calibration, say whether")))));
            }
            finally
            {
                Dm.DisconnectAll();
                Dm.GuiderServer.Stop();
                g.Settings.RaAlgorithm = GuideAlgorithmKind.Hysteresis; g.Settings.ServerEnabled = false; g.Settings.NoiseReduction = NoiseReduction.None; g.SaveSettings();
                OnUi(() =>
                {
                    Field<ComboBox>(view, "_polarMethod").SelectedIndex = 0;
                    All(view).OfType<TabbedPanel>().First(t => t.Header.Tabs.Contains("Guiding")).Select(0);
                });
            }
        }

        [TestMethod]
        public void TestEquipmentProfiles()
        {
            var view = View<ObservatoryView>("observatory");
            var s = AppState.Settings;
            var g = Dm.BuiltinGuider;
            var first = AppState.Profile;
            string firstTelescope = first.Telescope, firstCamera = first.Camera;
            double firstFactor = first.FocalFactor, firstGuideFocal = first.GuideFocalLengthMm;
            var firstGuiding = first.Guiding;
            var firstDevices = first.Devices.ToList();
            try
            {
                OnUi(() =>
                {
                    Dm.DisconnectAll();
                    All(view).OfType<TabbedPanel>().First(t => t.Header.Tabs.Contains("Equipment")).Select(0);
                });
                CheckTrue("There is a first profile, from the setup so far", s.Profiles.Count >= 1 && AppState.NameOf(first) == (string.IsNullOrEmpty(first.Name) ? "My equipment" : first.Name)
                    && s.Telescopes.Any(t => t.Name == first.Telescope));
                var row = OnUi(() => All(view).OfType<FlowLayoutPanel>().First(f => f.Controls.OfType<Label>().Any(l => l.Text == "Equipment profile")));
                var pick = OnUi(() => row.Controls.OfType<ComboBox>().First());
                var optics = OnUi(() => All(view).OfType<Card>().First(c => c.Title == "Optics"));
                var combos = OnUi(() => All(optics).OfType<ComboBox>().ToList());
                var nums = OnUi(() => All(optics).OfType<NumericUpDown>().ToList());
                ComboBox scope = combos[0], camera = combos[1], through = combos[2];
                NumericUpDown factor = nums[0], guideFocal = nums[1];
                CheckTrue("The tab shows the profile in use", OnUi(() => pick.Text == AppState.NameOf(first) && scope.Text == first.Telescope));

                // the imaging focal length: the telescope's, through the reducer; the guider's: the guide scope's or, through an
                // off-axis guider, the imaging one
                var apo = s.Telescopes.FirstOrDefault(t => t.Name != first.Telescope) ?? s.Telescopes[0];
                OnUi(() => { scope.SelectedIndex = s.Telescopes.IndexOf(apo); factor.Value = 0.8m; through.SelectedIndex = 1; guideFocal.Value = 180; });
                CheckTrue("The imaging optics follow the telescope and the reducer", WaitFor(() => Math.Abs(Dm.FocalLengthMm - apo.FocalMm * 0.8) < 1e-6 && Dm.ApertureMm == apo.ApertureMm),
                    Dm.FocalLengthMm + " mm");
                CheckTrue("…the guider takes the guide scope's focal length", g.Settings.FocalLengthMm == 180 && first.Telescope == apo.Name && first.FocalFactor == 0.8);
                CheckTrue("…and the planner the reducer", Math.Abs(AppState.ImagingRig.FocalLengthMm - apo.FocalMm * 0.8) < 1e-6 || s.Telescopes[s.ActiveTelescope] != apo);
                OnUi(() => through.SelectedIndex = 0);
                CheckTrue("Through an off-axis guider the guider takes the imaging focal length", WaitFor(() => Math.Abs(g.Settings.FocalLengthMm - Dm.FocalLengthMm) < 1e-6));
                CheckTrue("The Guiding tab shows where its focal length comes from", WaitFor(() => OnUi(() => All(view).OfType<Label>().Any(l => l.Text == string.Format("{0:0} mm, from the equipment profile (Observatory → Equipment)", g.Settings.FocalLengthMm)))));

                // a renamed telescope keeps its profiles
                string apoName = apo.Name;
                apo.Name = apoName + " (renamed)";
                AppState.RenamedInPlanner(apo, apoName, apo.Name);
                CheckTrue("Renaming the telescope in Settings keeps the profile on it", first.Telescope == apo.Name);
                apo.Name = apoName;
                AppState.RenamedInPlanner(apo, apo.Name + " (renamed)", apoName);

                // a device connected in its row is the rig's; Connect all connects it again
                var mountRow = OnUi(() => All(view).OfType<ComboBox>().First(c => c.Items.OfType<DeviceInfo>().Any(d => d.Kind == DeviceKind.Telescope)));
                OnUi(() => mountRow.SelectedItem = mountRow.Items.OfType<DeviceInfo>().First(d => d.Protocol == DeviceProtocol.Simulator));
                var table = (TableLayoutPanel)OnUi(() => mountRow.Parent);
                var mountConnect = OnUi(() => (FlatButton)table.GetControlFromPosition(2, table.GetRow(mountRow)));
                OnUi(() => mountConnect.PerformClick());
                CheckTrue("A device connected in its row is remembered by the profile", WaitFor(() => Dm.Telescope != null && first.DeviceFor(DeviceKind.Telescope) != null
                    && first.DeviceFor(DeviceKind.Telescope).Protocol == DeviceProtocol.Simulator, 10000));
                CheckTrue("Another profile waits while devices are connected (greyed out)", WaitFor(() => OnUi(() => !pick.Enabled && !Button(row, "New…").Enabled && !Button(row, "Delete").Enabled)));
                CheckTrue("…renaming does not", OnUi(() => Button(row, "Rename…").Enabled));
                OnUi(() => Dm.DisconnectAll());
                CheckTrue("Disconnected, Connect all is available", WaitFor(() => OnUi(() => Button(view, "Connect all").Enabled && pick.Enabled)));
                OnUi(() => Button(view, "Connect all").PerformClick());
                CheckTrue("Connect all connects the profile's devices", WaitFor(() => Dm.Telescope != null && Dm.Telescope.Connected, 10000));
                CheckTrue("…and is greyed out when they all are", WaitFor(() => OnUi(() => !Button(view, "Connect all").Enabled)));
                OnUi(() => Dm.DisconnectAll());

                // a second rig: its own optics and guider settings; switching brings each back
                g.Settings.ExposureSeconds = 1.5; g.SaveSettings();
                var second = first.Copy("Second rig", EquipmentProfile.NewGuiderFile(s.Profiles));
                second.FocalFactor = 1; second.Guiding = GuideOptics.GuideScope; second.GuideFocalLengthMm = 400; second.Devices.Clear();
                OnUi(() => { s.Profiles.Add(second); AppState.ApplyProfile(); });
                CheckTrue("A new profile is listed", WaitFor(() => OnUi(() => pick.Items.Count == s.Profiles.Count && pick.Items.Contains("Second rig"))));
                OnUi(() => pick.SelectedIndex = s.Profiles.IndexOf(second));
                CheckTrue("Choosing it uses its optics", WaitFor(() => AppState.Profile == second && Math.Abs(Dm.FocalLengthMm - apo.FocalMm) < 1e-6 && g.Settings.FocalLengthMm == 400));
                CheckTrue("…and its own guider settings file", g.SettingsPath.EndsWith(second.GuiderFile));
                g.Settings.ExposureSeconds = 3; g.SaveSettings();
                CheckTrue("…shown on the Optics card", WaitFor(() => OnUi(() => through.SelectedIndex == 1 && guideFocal.Value == 400 && guideFocal.Visible)));
                OnUi(() => pick.SelectedIndex = s.Profiles.IndexOf(first));
                CheckTrue("Back to the first rig, its guider settings come back", WaitFor(() => AppState.Profile == first && g.Settings.ExposureSeconds == 1.5));
                CheckTrue("…its rows show its devices", WaitFor(() => OnUi(() => ((DeviceInfo)mountRow.SelectedItem).Protocol == DeviceProtocol.Simulator)));
            }
            finally
            {
                OnUi(() =>
                {
                    Dm.DisconnectAll();
                    s.Profiles.RemoveAll(p => p != first);
                    s.ActiveProfile = 0;
                    first.Telescope = firstTelescope; first.Camera = firstCamera; first.FocalFactor = firstFactor; first.Guiding = firstGuiding; first.GuideFocalLengthMm = firstGuideFocal;
                    first.Devices.Clear(); foreach (var d in firstDevices) first.Remember(d);
                    AppState.Save();
                    AppState.ApplyProfile();
                });
            }
        }

        [TestMethod]
        public void TestSwitchesAndFlatPanel()
        {
            var view = View<ObservatoryView>("observatory");
            Func<string, Card> card = title => OnUi(() => All(view).OfType<Card>().First(c => c.Title == title));
            try
            {
                OnUi(() =>
                {
                    Dm.DisconnectAll();
                    var tabs = All(view).OfType<TabbedPanel>().First(t => t.Header.Tabs.Contains("Dome & weather"));
                    tabs.Select(tabs.Header.Tabs.IndexOf("Dome & weather"));
                });
                var switches = card("Switches");
                var flat = card("Flat panel");
                CheckTrue("Without a switch box the Switches card says so", WaitFor(() => OnUi(() => All(switches).OfType<Label>().Any(l => l.Visible && l.Text == "No switch box connected."))));
                CheckTrue("Without a flat panel its buttons are greyed out", WaitFor(() => OnUi(() => !Button(flat, "Open cover").Enabled && !Button(flat, "Light on").Enabled)));

                OnUi(() =>
                {
                    Dm.Connect(new DeviceInfo { Kind = DeviceKind.Switch, Protocol = DeviceProtocol.Simulator, Name = "Switch", Address = "sim" });
                    Dm.Connect(new DeviceInfo { Kind = DeviceKind.CoverCalibrator, Protocol = DeviceProtocol.Simulator, Name = "Cover", Address = "sim" });
                });
                var sw = (SwitchSimulator)Dm.Switch;
                var cover = (CoverCalibratorSimulator)Dm.Cover;
                cover.MoveSeconds = 0.5;
                CheckTrue("With the switch box, a row for each switch", WaitFor(() => OnUi(() => new[] { "Dew heater 1", "Dew heater 2", "Fan", "Mount power" }.All(n => All(switches).OfType<Label>().Any(l => l.Visible && l.Text == n))), 5000));
                Func<string, Control> editor = name => OnUi(() => { var l = All(switches).OfType<Label>().First(x => x.Text == name); var t = (TableLayoutPanel)l.Parent; return t.GetControlFromPosition(1, t.GetRow(l)); });
                CheckTrue("On/off switches are tick boxes, dew heaters have a level", editor("Fan") is CheckBox && editor("Dew heater 1") is NumericUpDown);
                CheckTrue("…and show the switch box's state (the mount's power on)", WaitFor(() => OnUi(() => ((CheckBox)editor("Mount power")).Checked)));
                OnUi(() => ((CheckBox)editor("Fan")).Checked = true);
                CheckTrue("Ticking a switch turns it on", sw.GetSwitch(2));
                OnUi(() => ((NumericUpDown)editor("Dew heater 1")).Value = 30);
                CheckTrue("A level sets the dew heater", sw.GetValue(0) == 30);

                CheckTrue("With a flat panel its buttons are available", WaitFor(() => OnUi(() => Button(flat, "Open cover").Enabled && Button(flat, "Light on").Enabled)));
                OnUi(() => Button(flat, "Open cover").PerformClick());
                CheckTrue("Open cover opens it, and the card says so", WaitFor(() => cover.CoverState == "Open" && OnUi(() => All(flat).OfType<Label>().Any(l => l.Text == "Cover open · light 0 of 255")), 5000));
                OnUi(() => { All(flat).OfType<NumericUpDown>().First().Value = 200; Button(flat, "Light on").PerformClick(); });
                CheckTrue("Light on lights it at the brightness set", cover.Brightness == 200);
                OnUi(() => Button(flat, "Light off").PerformClick());
                CheckTrue("Light off puts it out", cover.Brightness == 0);
            }
            finally
            {
                Dm.DisconnectAll();
                OnUi(() => All(view).OfType<TabbedPanel>().First(t => t.Header.Tabs.Contains("Dome & weather")).Select(0));
            }
        }

        [TestMethod]
        public void TestDomeParkEndsFollowing()
        {
            var view = View<ObservatoryView>("observatory");
            try
            {
                OnUi(() =>
                {
                    Dm.DisconnectAll();
                    Dm.Connect(new DeviceInfo { Kind = DeviceKind.Telescope, Protocol = DeviceProtocol.Simulator, Name = "Telescope", Address = "sim" });
                    Dm.Connect(new DeviceInfo { Kind = DeviceKind.Dome, Protocol = DeviceProtocol.Simulator, Name = "Dome", Address = "sim" });
                    var tabs = All(view).OfType<TabbedPanel>().First(t => t.Header.Tabs.Contains("Dome & weather"));
                    tabs.Select(tabs.Header.Tabs.IndexOf("Dome & weather"));
                });
                var tel = (TelescopeSimulator)Dm.Telescope;
                tel.SlewRateDegPerSec = 90;
                // the telescope low in the east: the dome follows it there
                Centering.Slew(tel, tel.SiderealTime - 4, 0, CancellationToken.None).Wait();
                var card = OnUi(() => All(view).OfType<Card>().First(c => c.Title == "Dome / roof"));
                var follow = OnUi(() => Button(card, "Follow telescope"));
                OnUi(() => follow.PerformClick());
                CheckTrue("Follow telescope turns the dome to the telescope", WaitFor(() => Math.Abs(AstroMath.NormalizeDeg180(Dm.Dome.Azimuth - tel.Azimuth)) < 3.5 && !Dm.Dome.Slewing, 15000), Dm.Dome.Azimuth.ToString("0.0"));
                OnUi(() => Button(card, "Park").PerformClick());
                CheckTrue("Park turns Follow telescope off", OnUi(() => !follow.Checked));
                CheckTrue("…and the dome parks and stays parked", WaitFor(() => Dm.Dome.AtPark, 15000) && !WaitFor(() => !Dm.Dome.AtPark, 3000), Dm.Dome.Azimuth.ToString("0.0"));
                OnUi(() => follow.PerformClick());
                CheckTrue("Following again moves it back to the telescope", WaitFor(() => Dm.Dome.Slewing || Math.Abs(AstroMath.NormalizeDeg180(Dm.Dome.Azimuth - tel.Azimuth)) < 3.5, 5000));
                OnUi(() => Button(card, "Slew").PerformClick());
                CheckTrue("Slew by hand turns it off too", OnUi(() => !follow.Checked));
            }
            finally
            {
                Dm.DisconnectAll();
                OnUi(() => All(view).OfType<TabbedPanel>().First(t => t.Header.Tabs.Contains("Dome & weather")).Select(0));
            }
        }

        [TestMethod]
        public void TestMissingDeviceMessages()
        {
            Dm.DisconnectAll();
            var view = View<ObservatoryView>("observatory");
            CheckTrue("Without a guider its Stop is greyed out", WaitFor(() => OnUi(() => !Button(Button(view, "Start guiding").Parent, "Stop").Enabled)));
            OnUi(() => Button(view, "Start guiding").PerformClick());
            CheckTrue("Start guiding without a guider says what to do", WaitFor(() => Toasts().Any(t => t.StartsWith("No guider connected. Connect a guide camera in Observatory → Equipment, or click Connect PHD2"))));
            OnUi(() => Button(view, "Run autofocus").PerformClick());
            CheckTrue("Autofocus without a camera says so", WaitFor(() => Toasts().Any(t => t == "No camera connected. Connect one in Observatory → Equipment.")));
            OnUi(() => Button(view, "Run sequence").PerformClick());
            CheckTrue("A sequence without its devices says which to connect", WaitFor(() => Toasts().Any(t => t.StartsWith("This sequence needs"))));
            OnUi(() => Button(view, "Record").PerformClick());
            CheckTrue("Video without a camera", WaitFor(() => Toasts().Any(t => t.StartsWith("No camera connected"))));
        }

        [TestMethod]
        public void TestOneCameraJobAtATime()
        {
            var view = View<ObservatoryView>("observatory");
            try
            {
                OnUi(() => Dm.ConnectSimulators());
                FlatButton loop = OnUi(() => Button(view, "Loop")), start = OnUi(() => Button(view, "Start")), record = OnUi(() => Button(view, "Record")), af = OnUi(() => Button(view, "Run autofocus")),
                    polar = OnUi(() => Button(view, "Start polar alignment")), run = OnUi(() => Button(view, "Run sequence"));
                var stop = OnUi(() => All(loop.Parent).OfType<FlatButton>().First(b => b.Text == "Stop"));
                var videoStop = OnUi(() => All(record.Parent).OfType<FlatButton>().First(b => b.Text == "Stop"));
                CheckTrue("All camera jobs available when idle", WaitFor(() => record.Enabled && af.Enabled && polar.Enabled && run.Enabled));
                CheckTrue("With nothing using the camera there is nothing to stop", OnUi(() => !stop.Enabled && !videoStop.Enabled));
                OnUi(() => loop.PerformClick());
                CheckTrue("A preview loop blocks the other camera jobs", WaitFor(() => !record.Enabled && !af.Enabled && !polar.Enabled && !run.Enabled));
                CheckTrue("Both camera tabs' Stop buttons are available while it runs", OnUi(() => stop.Enabled && videoStop.Enabled));
                CheckTrue("Start and Loop are greyed out while it runs", WaitFor(() => !loop.Enabled && !start.Enabled));
                CheckTrue("Frames arrive in the viewer", WaitFor(() => Field<ImageViewer>(view, "_viewer").Image != null, 15000));
                OnUi(() => stop.PerformClick());
                CheckTrue("Stop frees the camera", WaitFor(() => record.Enabled && af.Enabled && polar.Enabled && run.Enabled && loop.Enabled && start.Enabled, 15000));
                CheckTrue("The Stop buttons are greyed out again", WaitFor(() => OnUi(() => !stop.Enabled && !videoStop.Enabled)));
                OnUi(() => { var e = Find<Label>(view, l => l.Text == "Exposure (s)"); var row = (TableLayoutPanel)e.Parent; ((NumericUpDown)row.GetControlFromPosition(1, row.GetRow(e))).Value = 0.5m; });
                OnUi(() => loop.PerformClick());
                CheckTrue("Loop again", WaitFor(() => OnUi(() => Field<BackgroundTask>(view, "_manualTask").Name == "Preview loop")));
                CheckTrue("…greying out Start", WaitFor(() => !start.Enabled && !loop.Enabled));
                OnUi(() => start.PerformClick());
                CheckTrue("…which then does nothing", OnUi(() => Field<BackgroundTask>(view, "_manualTask").Name == "Preview loop"));
                OnUi(() => stop.PerformClick());
                CheckTrue("Stop makes Start available", WaitFor(() => start.Enabled && loop.Enabled, 15000));
                OnUi(() => start.PerformClick());
                CheckTrue("Start captures", WaitFor(() => OnUi(() => Field<BackgroundTask>(view, "_manualTask").Name.StartsWith("Capturing")), 15000));
                CheckTrue("When the capture is done the camera is free", WaitFor(() => start.Enabled && loop.Enabled && record.Enabled, 20000));
                var planetaryStop = OnUi(() => All(record.Parent).OfType<FlatButton>().First(b => b.Text == "Stop"));
                OnUi(() => loop.PerformClick());
                CheckTrue("Loop once more", WaitFor(() => !record.Enabled));
                OnUi(() => planetaryStop.PerformClick());
                CheckTrue("The Planetary tab's Stop ends a Deep sky loop too", WaitFor(() => record.Enabled && af.Enabled, 15000));
                CheckTrue("Mount controls stay usable", OnUi(() => Button(view, "Tracking") == null || Button(view, "Tracking").Enabled));
            }
            finally { OnUi(() => { foreach (var b in All(App).OfType<FlatButton>().Where(x => x.Text == "Stop")) { } }); Dm.DisconnectAll(); }
        }

        [TestMethod]
        public void TestPlanetaryLiveView()
        {
            var view = View<ObservatoryView>("observatory");
            try
            {
                OnUi(() => Dm.ConnectSimulators());
                FlatButton live = OnUi(() => Button(view, "Live view")), record = OnUi(() => Button(view, "Record")), loop = OnUi(() => Button(view, "Loop"));
                var stop = OnUi(() => All(record.Parent).OfType<FlatButton>().First(b => b.Text == "Stop"));
                Func<string> status = () => OnUi(() => All(record.Parent.Parent).OfType<Label>().Select(l => l.Text).FirstOrDefault(t => t.StartsWith("Live view") || t.StartsWith("Recording") || t.Contains("Saved")) ?? "");
                Func<string> job = () => OnUi(() => { var j = Field<BackgroundTask>(view, "_cameraJob"); return j != null && !j.Done ? Field<string>(view, "_cameraJobName") : null; });
                OnUi(() => { var region = Find<Label>(view, l => l.Text == "Region"); var row = (TableLayoutPanel)region.Parent; ((ComboBox)row.GetControlFromPosition(1, row.GetRow(region))).SelectedItem = "320 × 240"; });
                CheckTrue("Camera available", WaitFor(() => OnUi(() => live.Enabled && record.Enabled), 15000));
                var tabs = OnUi(() => All(view).OfType<SegmentedTabs>().First(t => t.Tabs.Contains("Planetary")));
                var viewer = Field<ImageViewer>(view, "_viewer");
                OnUi(() => tabs.SelectedIndex = 1);
                CheckTrue("The Planetary tab shows frames as recorded", OnUi(() => viewer.Stretch == DisplayStretch.None));
                OnUi(() => tabs.SelectedIndex = 0);
                CheckTrue("The Deep sky tab stretches them", OnUi(() => viewer.Stretch == DisplayStretch.AutoLinked));
                OnUi(() => tabs.SelectedIndex = 1);
                OnUi(() => live.PerformClick());
                CheckTrue("Live view shows frames with their frame rate, brightness and focus", WaitFor(() => status().StartsWith("Live view 320×240") && status().Contains("fps") && status().Contains("Focus"), 15000), status());
                CheckTrue("Frames arrive in the viewer", OnUi(() => Field<ImageViewer>(view, "_viewer").Image != null));
                CheckTrue("While it runs, Record stays available and other camera work waits", OnUi(() => record.Enabled && !live.Enabled && !loop.Enabled));
                OnUi(() => record.PerformClick());
                CheckTrue("Record takes over from live view", WaitFor(() => job() == "Recording video" && status().StartsWith("Recording"), 15000), job() + " / " + status());
                OnUi(() => stop.PerformClick());
                CheckTrue("Stop ends the recording and keeps it", WaitFor(() => job() == null && status().Contains("Saved"), 15000), status());
                Func<int> videos = () => System.IO.Directory.GetFiles(System.IO.Path.Combine(AppState.Settings.ImageFolder, "Planetary"), "*", System.IO.SearchOption.AllDirectories).Length;
                int saved = videos();
                OnUi(() => live.PerformClick());
                CheckTrue("Live view again", WaitFor(() => job() == "Live view", 15000));
                OnUi(() => All(loop.Parent).OfType<FlatButton>().First(b => b.Text == "Stop").PerformClick());
                CheckTrue("The Deep sky tab's Stop ends live view too", WaitFor(() => job() == null && OnUi(() => live.Enabled && loop.Enabled), 15000));
                CheckTrue("Live view saves nothing", videos() == saved, saved + " → " + videos());
            }
            finally { Dm.DisconnectAll(); }
        }

        [TestMethod]
        public void TestSequencerEditing()
        {
            var view = View<ObservatoryView>("observatory");
            var seq = Field<Sequencer>(view, "_sequencer");
            var tree = Field<TreeView>(view, "_seqTree");
            int nodes = OnUi(() => Count(tree.Nodes));
            OnUi(() => { tree.SelectedNode = tree.Nodes[0]; Call(view, "AddItem", new RecordVideo()); });
            CheckTrue("Add an instruction", OnUi(() => Count(tree.Nodes)) == nodes + 1 && seq.Root.Items.Last() is RecordVideo);
            var added = OnUi(() => tree.Nodes[0].Nodes.Cast<TreeNode>().Last());
            OnUi(() => { tree.SelectedNode = added; Call(view, "MoveItem", -1); });
            CheckTrue("Move it up", seq.Root.Items[seq.Root.Items.Count - 2] is RecordVideo);
            OnUi(() => { tree.SelectedNode = tree.Nodes[0].Nodes.Cast<TreeNode>().First(n => n.Tag is RecordVideo); Call(view, "RemoveItem"); });
            CheckTrue("Remove it", OnUi(() => Count(tree.Nodes)) == nodes && !seq.Root.Items.OfType<RecordVideo>().Any());
            var grid = Field<PropertyGrid>(view, "_seqProps");
            OnUi(() =>
            {
                // the Sequencer tab must be on screen for the tree to raise its selection events
                var tabs = All(view).OfType<TabbedPanel>().First(t => t.Header.Tabs.Contains("Sequencer"));
                tabs.Select(tabs.Header.Tabs.IndexOf("Sequencer"));
                tree.SelectedNode = null;
                tree.SelectedNode = tree.Nodes[0].Nodes[0];
            });
            CheckTrue("The selected instruction's properties are shown", OnUi(() => grid.SelectedObject is GridAdapter));
            var take = new TakeExposures();
            var frameType = System.ComponentModel.TypeDescriptor.GetProperties(new GridAdapter(take, "Instruction"))["FrameType"];
            CheckTrue("Frame type is a drop-down in the properties", frameType.Converter is FrameTypeConverter && frameType.Converter.GetStandardValuesExclusive());
            try
            {
                OnUi(() => Devices.DeviceManager.Instance.Connect(new Devices.DeviceInfo { Kind = Devices.DeviceKind.FilterWheel, Protocol = Devices.DeviceProtocol.Simulator }));
                var filter = System.ComponentModel.TypeDescriptor.GetProperties(new GridAdapter(new SwitchFilter(), "Instruction"))["Filter"];
                var names = filter.Converter.GetStandardValues().Cast<string>().ToList();
                CheckTrue("Filter drop-down lists the connected wheel's filters", names.SequenceEqual(Devices.DeviceManager.Instance.FilterWheel.Names) && !filter.Converter.GetStandardValuesExclusive(), string.Join(", ", names));
            }
            finally { Devices.DeviceManager.Instance.Disconnect(Devices.DeviceKind.FilterWheel); }
            // while a sequence runs it can be edited, but Template, Open and Save wait
            var saved = seq.Root;
            var wait = new Wait { Seconds = 30 };
            try
            {
                OnUi(() => { seq.Root = new SequenceContainer { Items = { wait, new Annotation { Text = "after" } } }; Call(view, "RefreshTree"); Button(view, "Run sequence").PerformClick(); });
                CheckTrue("The sequencer's Stop is available while the sequence runs", WaitFor(() => OnUi(() => Button(Button(view, "Template…").Parent, "Stop").Enabled), 10000));
                CheckTrue("Template, Open and Save wait while the sequence runs", WaitFor(() => OnUi(() => wait.Status == ItemStatus.Running && !Button(view, "Template…").Enabled && !Button(view, "Open…").Enabled && !Button(view, "Save…").Enabled), 10000));
                CheckTrue("Add, Remove, Up and Down stay available", OnUi(() => { var bar = Button(view, "Template…").Parent; return Button(bar, "Add").Enabled && Button(bar, "Remove").Enabled && Button(bar, "Up").Enabled && Button(bar, "Down").Enabled; }));
                OnUi(() => { tree.SelectedNode = tree.Nodes[0].Nodes[0]; Call(view, "AddItem", new Annotation { Text = "added" }); });
                CheckTrue("An instruction can be added while the sequence runs", seq.Root.Items.Count == 3 && seq.Root.Items[1] is Annotation);
                OnUi(() => { tree.SelectedNode = tree.Nodes[0].Nodes.Cast<TreeNode>().First(n => n.Tag == wait); Call(view, "RemoveItem"); });
                CheckTrue("The running instruction is not removed", seq.Root.Items.Contains(wait) && Toasts().Any(t => t.StartsWith("That instruction is running")));
            }
            finally { seq.Stop(); }
            CheckTrue("Template, Open and Save are back when it stops", WaitFor(() => OnUi(() => !seq.Running && Button(view, "Template…").Enabled && Button(view, "Save…").Enabled), 10000));
            CheckTrue("The sequencer's Stop is greyed out when no sequence runs", WaitFor(() => OnUi(() => !Button(Button(view, "Template…").Parent, "Stop").Enabled)));
            OnUi(() => { seq.Root = saved; Call(view, "RefreshTree"); });

            var command = System.ComponentModel.TypeDescriptor.GetProperties(new GridAdapter(new RunScript(), "Instruction"))["Command"];
            CheckTrue("Program path opens a file picker", command.GetEditor(typeof(System.Drawing.Design.UITypeEditor)) != null && command.GetEditor(typeof(System.Drawing.Design.UITypeEditor)).GetType().Name == "FileNameEditor");
            var imagingTypes = OnUi(() =>
            {
                var label = Find<Label>(view, l => l.Text == "Frame type");
                var row = (TableLayoutPanel)label.Parent;
                return ((ComboBox)row.GetControlFromPosition(1, row.GetRow(label))).Items.Cast<object>().Select(o => o.ToString()).ToList();
            });
            CheckTrue("Imaging offers the same frame types plus SNAPSHOT", imagingTypes.SequenceEqual(ExposureRequest.FrameTypes.Concat(new[] { "SNAPSHOT" })), string.Join(", ", imagingTypes));
        }

        private static int Count(TreeNodeCollection n) => n.Cast<TreeNode>().Sum(x => 1 + Count(x.Nodes));
    }
}
