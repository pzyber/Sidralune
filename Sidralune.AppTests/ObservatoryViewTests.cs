using System;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
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
                OnUi(() => decAlgo.SelectedIndex = 1);
                CheckTrue("Choosing Lowpass2 shows its rows instead", rowShown("Lowpass2 aggressiveness (%)") && !rowShown("Dec aggressiveness (%)"));
                CheckTrue("…and is saved", g.Settings.DecAlgorithm == GuideAlgorithmKind.Lowpass2 && GuiderSettings.Load().DecAlgorithm == GuideAlgorithmKind.Lowpass2);
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
                CheckTrue("It describes what it measured", WaitFor(() => OnUi(() => All(assist).OfType<Label>().Any(l => l.Text.Contains("Seeing: RA "))), 15000));
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
            }
            finally
            {
                Dm.DisconnectAll();
                Dm.BuiltinGuider.SettlePixels = 1.5; Dm.BuiltinGuider.SettleSeconds = 10;
                OnUi(() => All(view).OfType<TabbedPanel>().First(t => t.Header.Tabs.Contains("Guiding")).Select(0));
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
                CheckTrue("Start and Loop stay available, to replace each other's capture", WaitFor(() => loop.Enabled && start.Enabled));
                CheckTrue("Frames arrive in the viewer", WaitFor(() => Field<ImageViewer>(view, "_viewer").Image != null, 15000));
                OnUi(() => stop.PerformClick());
                CheckTrue("Stop frees the camera", WaitFor(() => record.Enabled && af.Enabled && polar.Enabled && run.Enabled && loop.Enabled && start.Enabled, 15000));
                CheckTrue("The Stop buttons are greyed out again", WaitFor(() => OnUi(() => !stop.Enabled && !videoStop.Enabled)));
                OnUi(() => { var e = Find<Label>(view, l => l.Text == "Exposure (s)"); var row = (TableLayoutPanel)e.Parent; ((NumericUpDown)row.GetControlFromPosition(1, row.GetRow(e))).Value = 0.5m; });
                OnUi(() => loop.PerformClick());
                CheckTrue("Loop again", WaitFor(() => OnUi(() => Field<BackgroundTask>(view, "_manualTask").Name == "Preview loop")));
                OnUi(() => start.PerformClick());
                CheckTrue("Start replaces the loop, and both stay available", WaitFor(() => OnUi(() => Field<BackgroundTask>(view, "_manualTask").Name.StartsWith("Capturing")) && start.Enabled && loop.Enabled, 15000));
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
