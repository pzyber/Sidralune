using System;
using System.Linq;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sidralune.Devices;
using Sidralune.Devices.Automation;
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
            try
            {
                OnUi(() => Button(view, "Connect simulated observatory").PerformClick());
                CheckTrue("Simulated observatory connected", WaitFor(() => Dm.Telescope != null && Dm.Camera != null && Dm.Focuser != null && Dm.Dome != null, 15000));
                CheckTrue("Rows show Disconnect for connected devices", WaitFor(() => All(view).OfType<FlatButton>().Count(b => b.Text == "Disconnect") >= 6));
                var widths = OnUi(() => All(view).OfType<FlatButton>().Where(b => b.Text == "Disconnect" || b.Text == "Connect").Select(b => b.Width).Distinct().ToList());
                CheckTrue("Connect and Disconnect buttons keep one width", widths.Count == 1, string.Join(", ", widths));
                CheckTrue("Equipment buttons wait while the simulators connect, then come back", WaitFor(() => OnUi(() => Button(view, "Disconnect all").Enabled), 15000));
                OnUi(() => Button(view, "Disconnect all").PerformClick());
                CheckTrue("Disconnect all disconnects every device", WaitFor(() => !Dm.All.Any(), 15000));
                CheckTrue("Rows back to Connect and Not connected", WaitFor(() => All(view).OfType<FlatButton>().All(b => b.Text != "Disconnect") && All(view).OfType<Label>().Count(l => l.Text == "Not connected") >= 6));
            }
            finally { Dm.DisconnectAll(); }
        }

        [TestMethod]
        public void TestMissingDeviceMessages()
        {
            Dm.DisconnectAll();
            var view = View<ObservatoryView>("observatory");
            OnUi(() => Button(view, "Start guiding").PerformClick());
            CheckTrue("Start guiding without a guider says what to do", WaitFor(() => Toasts().Any(t => t.StartsWith("No guider connected. Start PHD2"))));
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
                FlatButton loop = OnUi(() => Button(view, "Loop")), record = OnUi(() => Button(view, "Record")), af = OnUi(() => Button(view, "Run autofocus")),
                    polar = OnUi(() => Button(view, "Start polar alignment")), run = OnUi(() => Button(view, "Run sequence"));
                var stop = OnUi(() => All(loop.Parent).OfType<FlatButton>().First(b => b.Text == "Stop"));
                CheckTrue("All camera jobs available when idle", WaitFor(() => record.Enabled && af.Enabled && polar.Enabled && run.Enabled));
                OnUi(() => loop.PerformClick());
                CheckTrue("A preview loop blocks the other camera jobs", WaitFor(() => !record.Enabled && !af.Enabled && !polar.Enabled && !run.Enabled && loop.Enabled));
                CheckTrue("Frames arrive in the viewer", WaitFor(() => Field<ImageViewer>(view, "_viewer").Image != null, 15000));
                OnUi(() => stop.PerformClick());
                CheckTrue("Stop frees the camera", WaitFor(() => record.Enabled && af.Enabled && polar.Enabled && run.Enabled, 15000));
                CheckTrue("Mount controls stay usable", OnUi(() => Button(view, "Tracking") == null || Button(view, "Tracking").Enabled));
            }
            finally { OnUi(() => { foreach (var b in All(App).OfType<FlatButton>().Where(x => x.Text == "Stop")) { } }); Dm.DisconnectAll(); }
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
