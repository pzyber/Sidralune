using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Sidralune.Core;
using Sidralune.Core.Catalogs;
using Sidralune.Core.Mathematics;
using Sidralune.Core.Time;
using Sidralune.Devices;
using Sidralune.Devices.Ascom;
using Sidralune.Devices.Automation;
using Sidralune.Devices.Guiding;
using Sidralune.Imaging;
using Sidralune.UI;
using static Sidralune.Core.Util.Lang;

namespace Sidralune.Views
{
    public partial class ObservatoryView : UserControl, ICommandSource
    {
        private readonly TabbedPanel _tabs = new TabbedPanel { Dock = DockStyle.Fill, Padding = new Padding(10) };
        private readonly CaptureService _capture = new CaptureService();
        private readonly Sequencer _sequencer = new Sequencer();
        private DeviceManager Dm => DeviceManager.Instance;

        private static string Phd2Hint => T("Start PHD2 and enable its server (Tools → Enable Server).");
        private static string GuiderHint => T("Connect a guide camera in Observatory → Equipment, or click Connect PHD2.");

        // what is missing, as whole sentences (the words for the devices differ in form from language to language)
        private static string NoTelescope => T("No telescope connected.");
        private static string NoCamera => T("No camera connected.");
        private static string NoFocuser => T("No focuser connected.");
        private static string NoGuider => T("No guider connected.");
        private static string NoDome => T("No dome connected.");

        /// <summary>The device, or a clear error (shown as a toast by the buttons) when none of that kind is connected.</summary>
        private static TDevice Need<TDevice>(TDevice device, string missing, string hint = null) where TDevice : class
        {
            if (device == null) throw new InvalidOperationException(missing + " " + (hint ?? T("Connect one in Observatory → Equipment.")));
            return device;
        }

        /// <summary>For handlers that do not catch: show the same message as a toast and return false.</summary>
        private static bool Have(object device, string missing)
        {
            if (device != null) return true;
            MainForm.Instance.Toast(missing + " " + T("Connect one in Observatory → Equipment."), Icons.Warning);
            return false;
        }

        // a kind of device, as named in the equipment list
        private static string KindTitle(DeviceKind k)
        {
            switch (k)
            {
                case DeviceKind.Telescope: return T("Telescope");
                case DeviceKind.Camera: return T("Camera");
                case DeviceKind.GuideCamera: return T("Guide camera");
                case DeviceKind.Focuser: return T("Focuser");
                case DeviceKind.FilterWheel: return T("Filter wheel");
                case DeviceKind.Rotator: return T("Rotator");
                case DeviceKind.Dome: return T("Dome");
                case DeviceKind.ObservingConditions: return T("Observing conditions");
                case DeviceKind.SafetyMonitor: return T("Safety monitor");
                case DeviceKind.Switch: return T("Switch");
                case DeviceKind.CoverCalibrator: return T("Flat panel"); // ASCOM calls it a cover calibrator
                case DeviceKind.Guider: return T("Guider");
                case DeviceKind.AdaptiveOptics: return T("AO");
                default: return SplitWords(k.ToString());
            }
        }
        private readonly System.Windows.Forms.Timer _poll = new System.Windows.Forms.Timer { Interval = 1000 };
        private event Action Poll;

        public ObservatoryView()
        {
            Controls.Add(_tabs);
            _capture.ImageFolder = AppState.Settings.ImageFolder;
            _tabs.AddPage(T("Equipment"), BuildEquipment());
            _tabs.AddPage(T("Imaging"), BuildImaging());
            _tabs.AddPage(T("Guiding"), BuildGuiding());
            _tabs.AddPage(T("Sequencer"), BuildSequencer());
            _tabs.AddPage(T("Polar alignment"), BuildPolar());
            _tabs.AddPage(T("Dome & weather"), BuildDome());
            _poll.Tick += (s, e) => { if (Visible) Poll?.Invoke(); };
            // what is busy elsewhere can change without a job starting or ending (guiding started by another program)
            Poll += Availability.Sync;
            _poll.Start();
        }

        // ================================================================== equipment

        private Control BuildEquipment()
        {
            var panel = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
            var table = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 6, Padding = new Padding(0, 4, 0, 0) };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); // the device names, as long as their language needs
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 3; i++) table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 260));
            var rowRefreshers = new List<Action>();
            var kinds = new[] { DeviceKind.Telescope, DeviceKind.Camera, DeviceKind.GuideCamera, DeviceKind.AdaptiveOptics, DeviceKind.Focuser, DeviceKind.FilterWheel, DeviceKind.Rotator, DeviceKind.Dome, DeviceKind.ObservingConditions, DeviceKind.SafetyMonitor, DeviceKind.Switch, DeviceKind.CoverCalibrator };
            foreach (var k in kinds)
            {
                var kind = k;
                int r = table.RowCount++;
                table.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
                var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, Margin = new Padding(4, 6, 4, 4) };
                foreach (var d in Dm.Discover(kind, false)) combo.Items.Add(d);
                if (combo.Items.Count > 0) combo.SelectedIndex = 0;
                // sized for the longer label so switching between Connect and Disconnect never re-lays out the table
                var connect = new FlatButton(T("Disconnect"), Icons.Connect) { Margin = new Padding(4, 4, 4, 4) }; connect.AutoSizeToText();
                int connectWidth = connect.Width;
                connect.Text = T("Connect"); connect.AutoSizeToText();
                connectWidth = Math.Max(connectWidth, connect.Width);
                var setup = new FlatButton(T("Setup…"), Icons.Settings) { Margin = new Padding(4, 4, 4, 4) }; setup.AutoSizeToText();
                var choose = new FlatButton("ASCOM…", Icons.More) { Margin = new Padding(4, 4, 4, 4), ToolTipText = T("Open the ASCOM Chooser") }; choose.AutoSizeToText();
                // ASCOM has no standard for AO units: SX AO over a serial port, INDI or the simulator
                if (kind == DeviceKind.AdaptiveOptics) choose.Visible = false;
                var status = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Tag = "dim" };
                table.Controls.Add(new Label { Text = KindTitle(kind), AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 8, 0), Font = Theme.UiFontBold }, 0, r);
                table.Controls.Add(combo, 1, r);
                table.Controls.Add(connect, 2, r);
                table.Controls.Add(setup, 3, r);
                table.Controls.Add(choose, 4, r);
                table.Controls.Add(status, 5, r);
                Action refresh = () =>
                {
                    var dev = Dm.Get(kind);
                    connect.Text = dev != null && dev.Connected ? T("Disconnect") : T("Connect");
                    connect.Kind = dev != null && dev.Connected ? ButtonKind.Danger : ButtonKind.Normal;
                    connect.Width = connectWidth;
                    status.Text = dev != null ? (dev.Connected ? "● " + T("Connected: {0}", dev.Info.Name) : "○ " + T("Lost connection")) : T("Not connected");
                    // PHD2 opens the guide camera itself while it is connected
                    if (kind == DeviceKind.GuideCamera && Dm.Phd2Connected) status.Text = T("In use by PHD2");
                    if (kind == DeviceKind.GuideCamera) connect.Enabled = combo.Enabled = !Dm.Phd2Connected;
                    status.ForeColor = dev != null && dev.Connected ? Theme.Current.Success : Theme.Current.TextDim;
                };
                // one connect or disconnect at a time per device: a second click while a driver is connecting would connect twice
                var job = new JobSlot().Guard(connect, combo);
                connect.Click += (s, e) =>
                {
                    var dev = Dm.Get(kind);
                    // in the background: a driver can take seconds to disconnect, and the window must keep painting
                    if (dev != null && dev.Connected) { job.Run(T("Disconnecting {0}", dev.Info.Name), t => Dm.Disconnect(kind)); return; }
                    var info = combo.SelectedItem as DeviceInfo;
                    if (info == null) return;
                    job.Run(T("Connecting {0}", info.Name), t => Dm.Connect(info), () => { refresh(); MainForm.Instance.Toast(T("Connected {0}", info.Name)); });
                };
                setup.Click += (s, e) =>
                {
                    var info = combo.SelectedItem as DeviceInfo;
                    if (info != null && info.Protocol == DeviceProtocol.Ascom) try { new AscomTelescope(info).SetupDialog(); } catch (Exception ex) { MainForm.Instance.Toast(ex.Message, Icons.Warning); }
                    else if (info != null && info.Protocol == DeviceProtocol.Lx200)
                    {
                        var addr = PlannerView.Prompt(T("LX200 / OnStep address"), T("Serial port (COM3:9600) or TCP host:port:"), info.Address);
                        if (!string.IsNullOrEmpty(addr)) info.Address = addr;
                    }
                    else MainForm.Instance.Toast(T("No setup dialog for this device"));
                };
                choose.Click += (s, e) =>
                {
                    try
                    {
                        var id = AscomDiscovery.Choose(kind);
                        if (id == null) { if (!AscomDiscovery.PlatformInstalled) MainForm.Instance.Toast(T("The ASCOM Platform is not installed (ascom-standards.org)"), Icons.Info); return; }
                        var info = new DeviceInfo { Kind = kind, Protocol = DeviceProtocol.Ascom, Name = id, Address = id };
                        combo.Items.Add(info); combo.SelectedItem = info;
                    }
                    catch (Exception ex) { MainForm.Instance.Toast(ex.Message, Icons.Warning); }
                };
                rowRefreshers.Add(refresh);
                refresh();
            }
            // one refresh of all rows per burst of device changes (Disconnect all raises one per device), laid out once
            int refreshPending = 0;
            Action syncDisconnect = null;
            Action refreshAll = () =>
            {
                System.Threading.Interlocked.Exchange(ref refreshPending, 0);
                table.SuspendLayout();
                try { foreach (var r in rowRefreshers) r(); }
                finally { table.ResumeLayout(true); }
                syncDisconnect?.Invoke();
            };
            Dm.DevicesChanged += (s, e) => { if (System.Threading.Interlocked.Exchange(ref refreshPending, 1) == 0) MainForm.Instance.Post(refreshAll); };
            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 46 };
            var equipment = new JobSlot(); // connecting the simulators, discovery and disconnecting all: one at a time
            var sim = new FlatButton(T("Connect simulated observatory"), Icons.Observatory, (s, e) => equipment.Run(T("Connecting simulators"), t => Dm.ConnectSimulators(), () => MainForm.Instance.Toast(T("Simulated observatory ready")))) { Kind = ButtonKind.Primary }; sim.AutoSizeToText();
            var alpaca = new FlatButton(T("Discover Alpaca devices"), Icons.Search, (s, e) => equipment.Run(T("Alpaca discovery"), t =>
            {
                var servers = Devices.Alpaca.AlpacaDiscovery.FindServers(2000);
                var found = servers.SelectMany(sv => { try { return Devices.Alpaca.AlpacaDiscovery.Devices(sv); } catch { return new List<DeviceInfo>(); } }).ToList();
                MainForm.Instance.Post(() =>
                {
                    foreach (var combo in table.Controls.OfType<ComboBox>())
                    {
                        int row = table.GetRow(combo);
                        var kind = kinds[row];
                        // a guide camera is one of the servers' cameras, listed in both rows
                        foreach (var d in found.Where(f => f.Kind == kind || (kind == DeviceKind.GuideCamera && f.Kind == DeviceKind.Camera)))
                            combo.Items.Add(new DeviceInfo { Kind = kind, Protocol = d.Protocol, Name = d.Name, Address = d.Address, Description = d.Description });
                    }
                    MainForm.Instance.Toast(Tn("{0} Alpaca device found", "{0} Alpaca devices found", found.Count) + " " + Tn("on {0} server", "on {0} servers", servers.Count));
                });
            })); alpaca.AutoSizeToText();
            var indi = new FlatButton(T("Add INDI server…"), Icons.Add, (s, e) => AddIndi(table, kinds)); indi.AutoSizeToText();
            var disc = new FlatButton(T("Disconnect all"), Icons.Stop, (s, e) => equipment.Run(T("Disconnecting all devices"), t => Dm.DisconnectAll())) { Kind = ButtonKind.Danger }; disc.AutoSizeToText();
            bar.Controls.AddRange(new Control[] { sim, alpaca, indi, disc });
            equipment.Guard(sim, alpaca);
            // Disconnect all needs something to disconnect, and waits for connecting, discovery or a disconnect running
            syncDisconnect = () => disc.Enabled = !equipment.Busy && Dm.All.Any();
            equipment.Changed += syncDisconnect;
            syncDisconnect();
            var optics = new FormGrid(220);
            var fl = optics.Row(T("Imaging focal length (mm)"), Ui.Num((decimal)Dm.FocalLengthMm, 10, 20000));
            var ap = optics.Row(T("Aperture (mm)"), Ui.Num((decimal)Dm.ApertureMm, 10, 2000));
            fl.ValueChanged += (s, e) => Dm.FocalLengthMm = (double)fl.Value;
            ap.ValueChanged += (s, e) => Dm.ApertureMm = (double)ap.Value;
            optics.Full(Ui.Hint(T("ASCOM drivers are used through COM late binding, so no SDK is needed; install the ASCOM Platform plus your device drivers. Alpaca devices are found by UDP broadcast. INDI servers (for example on a Raspberry Pi) are reached over TCP 7624. LX200/OnStep mounts connect via serial or Wi-Fi.")));
            var card = new Card(T("Optics")) { Dock = DockStyle.Top, Height = 170 };
            card.Controls.Add(optics);
            panel.Controls.Add(card);
            panel.Controls.Add(table);
            panel.Controls.Add(bar);
            return panel;
        }

        private void AddIndi(TableLayoutPanel table, DeviceKind[] kinds)
        {
            var host = PlannerView.Prompt(T("INDI server"), T("Host:port"), "localhost:7624");
            if (string.IsNullOrEmpty(host)) return;
            Tasks.Run(T("INDI discovery"), t =>
            {
                var c = new Devices.Indi.IndiClient();
                var hp = host.Split(':');
                c.Connect(hp[0], hp.Length > 1 ? int.Parse(hp[1]) : 7624);
                Thread.Sleep(2000);
                var devs = c.Devices.ToList();
                var props = c.Properties.Values.ToList();
                c.Dispose();
                MainForm.Instance.Post(() =>
                {
                    foreach (var d in devs)
                    {
                        bool isScope = props.Any(p => p.Device == d && p.Name == "EQUATORIAL_EOD_COORD");
                        bool isCam = props.Any(p => p.Device == d && p.Name == "CCD_EXPOSURE");
                        bool isFoc = props.Any(p => p.Device == d && p.Name == "ABS_FOCUS_POSITION");
                        bool isAo = props.Any(p => p.Device == d && p.Name == "AO_NS");
                        foreach (var combo in table.Controls.OfType<ComboBox>())
                        {
                            var kind = kinds[table.GetRow(combo)];
                            if ((kind == DeviceKind.Telescope && isScope) || ((kind == DeviceKind.Camera || kind == DeviceKind.GuideCamera) && isCam) || (kind == DeviceKind.Focuser && isFoc)
                                || (kind == DeviceKind.AdaptiveOptics && isAo))
                                combo.Items.Add(new DeviceInfo { Kind = kind, Protocol = DeviceProtocol.Indi, Name = d + " (INDI)", Address = host + "/" + d });
                        }
                    }
                    MainForm.Instance.Toast(Tn("{0} INDI device found", "{0} INDI devices found", devs.Count));
                });
            });
        }

        private static string SplitWords(string s) => GridAdapter.Words(s);

        // ================================================================== imaging

        private ImageViewer _viewer;
        private CancellationTokenSource _captureCts;
        // the camera takes one exposure at a time: manual capture (Imaging) and the sequencer must not run together
        private BackgroundTask _manualTask, _sequenceTask;
        private bool ManualCapturing => _manualTask != null && !_manualTask.Done;
        private bool SequenceActive => (_sequenceTask != null && !_sequenceTask.Done) || _sequencer.Running;
        private BackgroundTask _cameraJob;
        private string _cameraJobName;
        // the camera job also moves the mount (centring, three-point polar alignment)
        private bool _cameraJobMovesMount;
        // the sequencer's Template, Open and Save, which wait while a sequence runs
        private readonly List<Control> _sequenceFileButtons = new List<Control>();
        // the camera tabs' Stop buttons (enabled while something uses the camera) and the sequencer's (while a sequence runs)
        private readonly List<Control> _cameraStops = new List<Control>();
        private Control _sequenceStop;
        private static string LiveViewJob => T("Live view");
        private bool LiveViewRunning => _cameraJob != null && !_cameraJob.Done && _cameraJobName == LiveViewJob;
        private Label _busyNote;

        /// <summary>
        /// What is using the camera, or null when it is free. The note under the camera controls leaves out Imaging's own
        /// capture (ignoreManual), and Record takes over from a live view (ignoreLiveView).
        /// </summary>
        private string CameraBusyReason(bool ignoreManual = false, bool ignoreLiveView = false)
        {
            if (SequenceActive) return T("A sequence is running and using the camera");
            if (_cameraJob != null && !_cameraJob.Done && !(ignoreLiveView && LiveViewRunning)) return T("{0} is running and using the camera", _cameraJobName);
            if (!ignoreManual && ManualCapturing) return T("A capture or preview loop is running in Imaging");
            return null;
        }

        /// <summary>
        /// What Stop does on both camera tabs: end whatever uses the camera, as the note under the camera controls says
        /// (a capture or preview loop, a recording or live view, autofocus, centring, polar alignment or a sequence).
        /// </summary>
        private void StopCameraWork()
        {
            _captureCts?.Cancel();
            _cameraJob?.Cts.Cancel();
            _sequencer.Stop();
        }

        /// <summary>
        /// A button that starts camera work: greyed out while the camera is busy with something else. Record stays
        /// available during a live view, which it takes over.
        /// </summary>
        private void CameraButton(Control c, bool takesOverLiveView = false) => Availability.When(c, () => CameraBusyReason(false, takesOverLiveView) == null);

        private bool CanUseCamera(bool ignoreManual = false, bool ignoreLiveView = false)
        {
            var why = CameraBusyReason(ignoreManual, ignoreLiveView);
            if (why != null) MainForm.Instance.Toast(T("{0}. Stop it first.", why), Icons.Info);
            return why == null;
        }

        /// <summary>Start camera work other than Imaging's own capture: autofocus, centring, polar alignment.</summary>
        private void RunCameraJob(string name, Action<BackgroundTask> work, Action onDone = null, bool movesMount = false)
        {
            _cameraJobName = name;
            _cameraJobMovesMount = movesMount;
            _cameraJob = Tasks.Run(name, work, onDone);
            SyncCameraButtons();
        }

        private void SyncCameraButtons()
        {
            Availability.Sync();
            foreach (var b in _sequenceFileButtons) b.Enabled = !SequenceActive;
            foreach (var b in _cameraStops) b.Enabled = CameraBusyReason() != null;
            if (_sequenceStop != null) _sequenceStop.Enabled = SequenceActive;
            if (_busyNote == null) return;
            var why = CameraBusyReason(true);
            _busyNote.Visible = why != null;
            if (why != null) _busyNote.Text = SequenceActive ? SequenceNote : T("{0}. Stop ends it.", why);
        }

        /// <summary>The live view's status line: size, frame rate, brightness and clipping, and focus against the best so far.</summary>
        internal static string LiveViewText(int width, int height, double fps, Imaging.Planetary.LiveStats st, double best)
        {
            string clip = st.Saturated > 0.001 ? " · ⚠ " + T("{0:0.#}% clipped", st.Saturated * 100) : "";
            return T("Live view {0}×{1} · {2:0.0} fps · peak {3:0}%", width, height, fps, st.Peak * 100) + clip + "\r\n" +
                T("Focus {0:0.000} · best {1:0.000}", st.Focus * 1000, best * 1000) + (st.ObjectFound ? "" : " " + T("(no planet found)"));
        }

        private static string SequenceNote => T("A sequence is running and using the camera. Its frames are shown here as they arrive; Stop ends the sequence.");

        private Control BuildImaging()
        {
            var panel = new Panel { Dock = DockStyle.Fill };
            _viewer = new ImageViewer { Dock = DockStyle.Fill, EmptyText = T("Connect a camera and take an exposure") };
            var readout = new Label { Dock = DockStyle.Bottom, Height = 22, Tag = "dim" };
            _viewer.ReadoutChanged += s => readout.Text = s;
            var left = new Panel { Dock = DockStyle.Left, Width = 300, AutoScroll = true };
            var right = new Panel { Dock = DockStyle.Right, Width = 300, AutoScroll = true };

            // camera controls
            var cam = new FormGrid(110);
            // frame types are written to the FITS header as they are (LIGHT, DARK…), so they are not translated
            var exp = cam.Row(T("Exposure (s)"), Ui.Num(5, 0, 3600, 3, 1));
            var count = cam.Row(T("Count"), Ui.Num(1, 1, 9999));
            var type = cam.Row(T("Frame type"), Ui.Combo(ExposureRequest.FrameTypes.Concat(new[] { "SNAPSHOT" }).ToArray()));
            var bin = cam.Row(T("Binning"), Ui.Combo("1", "2", "3", "4"));
            var gain = cam.Row(T("Gain"), Ui.Num(100, -1, 10000));
            var offset = cam.Row(T("Offset"), Ui.Num(30, -1, 10000));
            var filter = cam.Row(T("Filter"), new ComboBox { DropDownStyle = ComboBoxStyle.DropDown });
            var target = cam.Row(T("Target"), new TextBox { Text = T("Untitled") });
            var save = cam.Row(T("Save"), new CheckBox { Text = T("Save frames"), Checked = true });
            var pattern = cam.Row(T("File pattern"), new TextBox { Text = _capture.FilePattern });
            // button rows and status lines size to their content, so an empty status or a single row of buttons takes no extra space
            var btns = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true };
            var start = new FlatButton(T("Start"), Icons.Play) { Kind = ButtonKind.Primary }; start.AutoSizeToText();
            var loop = new FlatButton(T("Loop"), Icons.Refresh) { ToolTipText = T("Continuous preview (not saved)") }; loop.AutoSizeToText();
            var stop = new FlatButton(T("Stop"), Icons.Stop) { Kind = ButtonKind.Danger, Enabled = false }; stop.AutoSizeToText();
            _cameraStops.Add(stop);
            btns.Controls.AddRange(new Control[] { start, loop, stop });
            cam.Full(btns);
            var camStatus = new Label { Tag = "dim", AutoSize = true, MaximumSize = new Size(270, 0), Visible = false };
            camStatus.TextChanged += (s, e) => camStatus.Visible = camStatus.Text.Length > 0; // an empty line would still take a row
            cam.Full(camStatus);
            // cooling
            var cool = new FormGrid(110);
            var setT = cool.Row(T("Set point (°C)"), Ui.Num(-10, -60, 30, 1));
            var coolOn = cool.Row(T("Cooler"), new CheckBox { Text = T("On") });
            var coolStatus = cool.Row(T("Status"), new Label { Tag = "dim" });
            setT.ValueChanged += (s, e) => { if (Dm.Camera != null && Dm.Camera.CanSetTemperature) Dm.Camera.SetTemperature = (double)setT.Value; };
            coolOn.CheckedChanged += (s, e) => { if (Dm.Camera != null && Dm.Camera.CanSetTemperature) { Dm.Camera.SetTemperature = (double)setT.Value; Dm.Camera.CoolerOn = coolOn.Checked; } };
            var coolCard = new Collapsible(T("Cooling"), cool);

            // planetary tab: video of short exposures back to back into a SER file, with its own camera settings
            // (planetary and deep-sky settings are nearly opposite, so each tab keeps its own)
            var vid = new FormGrid(110);
            var vExp = vid.Row(T("Exposure (ms)"), Ui.Num(10, 0.1m, 10000, 1));
            var vRoi = vid.Row(T("Region"), Ui.Combo(VideoRequest.Regions));
            var vBin = vid.Row(T("Binning"), Ui.Combo("1", "2", "3", "4"));
            var vGain = vid.Row(T("Gain"), Ui.Num(100, -1, 10000));
            var vOffset = vid.Row(T("Offset"), Ui.Num(30, -1, 10000));
            var vFilter = vid.Row(T("Filter"), new ComboBox { DropDownStyle = ComboBoxStyle.DropDown });
            var vTarget = vid.Row(T("Target"), new TextBox { Text = T("Untitled") });
            var vFrames = vid.Row(T("Max frames"), Ui.Num(1000, 1, 100000));
            var vSecs = vid.Row(T("Max seconds"), Ui.Num(60, 1, 3600));
            var vBtns = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true };
            var rec = new FlatButton(T("Record"), Icons.Play) { Kind = ButtonKind.Primary }; rec.AutoSizeToText();
            var live = new FlatButton(T("Live view"), Icons.Eye) { ToolTipText = T("Continuous frames with these settings, for framing, focus and exposure (not saved)") }; live.AutoSizeToText();
            var vStop = new FlatButton(T("Stop"), Icons.Stop) { Kind = ButtonKind.Danger, Enabled = false }; vStop.AutoSizeToText();
            _cameraStops.Add(vStop);
            var openPl = new FlatButton(T("Open in Planetary"), Icons.Planet) { Enabled = false }; openPl.AutoSizeToText();
            vBtns.Controls.AddRange(new Control[] { rec, live, vStop, openPl });
            vStop.Click += (s, e) => StopCameraWork();
            vid.Full(vBtns);
            var vidStatus = new Label { Tag = "dim", AutoSize = true, MaximumSize = new Size(270, 0), Visible = false };
            vidStatus.TextChanged += (s, e) => vidStatus.Visible = vidStatus.Text.Length > 0;
            vid.Full(vidStatus);
            vid.Full(Ui.Hint(T("Live view shows frames with these settings without saving them, with the frame rate, clipping and a focus number that rises as focus improves. Record saves a SER video for the Planetary workspace (from live view it takes over straight away). A small region gives the highest frame rate. Stop ends either and keeps the frames recorded so far.")));

            // Camera section: a Deep sky | Planetary switch over the two sets of settings, then the busy note for both
            var mode = new SegmentedTabs();
            mode.Tabs.AddRange(new[] { T("Deep sky"), T("Planetary") });
            var busyNote = _busyNote = new Label { Tag = "dim", AutoSize = true, Visible = false, MaximumSize = new Size(270, 0), Text = SequenceNote };
            var camBox = new FormGrid(110);
            camBox.Full(mode);
            camBox.Full(cam);
            camBox.Full(vid);
            camBox.Full(busyNote);
            vid.Visible = false;
            // planets are shown as recorded: an automatic stretch would blow the black sky up into noise
            mode.SelectedChanged += i => { cam.Visible = i == 0; vid.Visible = i == 1; _viewer.Stretch = i == 1 ? DisplayStretch.None : DisplayStretch.AutoLinked; };
            var camCard = new Collapsible(T("Camera"), camBox);
            string lastVideo = null;
            // the settings of the Planetary tab, for live view and recording alike
            Func<VideoRequest> videoRequest = () =>
            {
                int rw, rh;
                VideoRequest.ParseRegion((string)vRoi.SelectedItem, out rw, out rh);
                return new VideoRequest
                {
                    ExposureMs = (double)vExp.Value, Bin = int.Parse((string)vBin.SelectedItem), Gain = (int)vGain.Value, Offset = (int)vOffset.Value,
                    RoiWidth = rw, RoiHeight = rh, MaxFrames = (int)vFrames.Value, MaxSeconds = (double)vSecs.Value,
                    Filter = string.IsNullOrWhiteSpace(vFilter.Text) ? null : vFilter.Text, Target = vTarget.Text
                };
            };
            live.Click += (s, e) =>
            {
                if (!Have(Dm.Camera, NoCamera) || !CanUseCamera()) return;
                var req = videoRequest();
                vidStatus.Text = T("Starting live view…");
                double best = 0;
                int measuring = 0;
                RunCameraJob(LiveViewJob, t =>
                {
                    _capture.LiveView(req, (img, p) =>
                    {
                        // measured beside the capture, so the frame rate does not suffer; a frame is skipped while one is measured
                        if (System.Threading.Interlocked.Exchange(ref measuring, 1) == 1) return;
                        double fps = p.Fps; int frames = p.Frames, fw = p.Width, fh = p.Height;
                        Task.Run(() =>
                        {
                            try
                            {
                                var st = Imaging.Planetary.LiveStats.Measure(img);
                                MainForm.Instance.Post(() =>
                                {
                                    if (t.Done || t.Cts.IsCancellationRequested) return;
                                    _viewer.SetImage(img, frames == 1 || _viewer.Image == null || _viewer.Image.Width != fw);
                                    best = Math.Max(best, st.Focus);
                                    vidStatus.Text = LiveViewText(fw, fh, fps, st, best);
                                });
                            }
                            finally { System.Threading.Interlocked.Exchange(ref measuring, 0); }
                        });
                    }, t.Cts.Token).GetAwaiter().GetResult();
                    MainForm.Instance.Post(() => { if (_cameraJob == t) vidStatus.Text = T("Live view stopped"); }); // unless Record took over
                });
            };
            CameraButton(live);
            rec.Click += (s, e) =>
            {
                if (!Have(Dm.Camera, NoCamera) || !CanUseCamera(false, true)) return;
                // a live view gives way: it stops, and the recording starts as soon as its last frame is in
                var liveJob = LiveViewRunning ? _cameraJob : null;
                liveJob?.Cts.Cancel();
                _capture.ImageFolder = AppState.Settings.ImageFolder;
                var req = videoRequest();
                vidStatus.Text = T("Starting…");
                RunCameraJob(T("Recording video"), t =>
                {
                    while (liveJob != null && !liveJob.Done && !t.Cts.IsCancellationRequested) Thread.Sleep(20);
                    // frames and the result are shown through VideoFrame and VideoRecorded below
                    var r = _capture.RecordVideo(req, (img, p) =>
                        Tasks.Report(t, Math.Min(1, Math.Max(p.Frames / (double)req.MaxFrames, p.Elapsed.TotalSeconds / req.MaxSeconds)), T("{0} frames · {1:0.0} fps", p.Frames, p.Fps)),
                        t.Cts.Token).GetAwaiter().GetResult();
                    if (r.Path != null) MainForm.Instance.Post(() => MainForm.Instance.Toast(T("Video saved: {0} frames at {1:0.0} fps", r.Frames, r.Fps)));
                });
            };
            // every video recording (manual or sequence) is shown here
            _capture.VideoFrame += (img, p) =>
            {
                string from = SequenceActive ? T("Sequence") + " · " : "";
                MainForm.Instance.Post(() =>
                {
                    _viewer.SetImage(img, p.Frames == 1);
                    vidStatus.Text = from + T("Recording {0}×{1} · {2} frames · {3:0.0} fps · {4:0} s", p.Width, p.Height, p.Frames, p.Fps, p.Elapsed.TotalSeconds);
                });
            };
            _capture.VideoRecorded += r =>
            {
                string from = SequenceActive ? T("Sequence") + " · " : "";
                MainForm.Instance.Post(() =>
                {
                    if (r.Path == null) { vidStatus.Text = from + T("Stopped before the first frame; nothing saved."); return; }
                    lastVideo = r.Path;
                    openPl.Enabled = true;
                    vidStatus.Text = from + T("{0} frames {1}×{2} · {3:0.0} fps · {4:0.0} s", r.Frames, r.Width, r.Height, r.Fps, r.Elapsed.TotalSeconds) + (r.Stopped ? " " + T("(stopped)") : "") + "\r\n" +
                        T("Saved {0}", System.IO.Path.GetFileName(r.Path));
                });
            };
            CameraButton(rec, true);
            openPl.Click += (s, e) =>
            {
                if (lastVideo == null) return;
                MainForm.Instance.Navigate("planetary");
                var pv = MainForm.Instance.GetView<PlanetaryView>("planetary");
                if (pv != null) pv.OpenVideo(lastVideo);
            };

            left.Controls.Add(coolCard);
            left.Controls.Add(camCard);

            Action<bool> run = previewLoop =>
            {
                if (!Have(Dm.Camera, NoCamera)) return;
                if (!CanUseCamera()) return;
                _captureCts?.Cancel();
                _captureCts = new CancellationTokenSource();
                var ct = _captureCts.Token;
                _capture.FilePattern = pattern.Text;
                _capture.ImageFolder = AppState.Settings.ImageFolder;
                var req = new ExposureRequest
                {
                    Seconds = (double)exp.Value, Bin = int.Parse((string)bin.SelectedItem), Gain = (int)gain.Value, Offset = (int)offset.Value,
                    Filter = string.IsNullOrWhiteSpace(filter.Text) ? null : filter.Text, Target = target.Text, FrameType = (string)type.SelectedItem,
                    Light = (string)type.SelectedItem != "DARK" && (string)type.SelectedItem != "BIAS", Save = save.Checked && !previewLoop
                };
                int n = previewLoop ? int.MaxValue : (int)count.Value;
                _manualTask = Tasks.Run(previewLoop ? T("Preview loop") : Tn("Capturing {0} frame", "Capturing {0} frames", n), t =>
                {
                    for (int i = 0; i < n && !ct.IsCancellationRequested; i++)
                    {
                        Tasks.Report(t, previewLoop ? double.NaN : (double)i / n, (i + 1) + "/" + (previewLoop ? "∞" : n.ToString()));
                        _capture.Expose(req, ct).GetAwaiter().GetResult(); // shown through ImageCaptured below
                    }
                });
            };
            start.Click += (s, e) => run(false);
            loop.Click += (s, e) => run(true);
            // greyed out while the camera is busy, a capture or preview loop of their own included: Stop first
            CameraButton(start);
            CameraButton(loop);
            // every frame the camera delivers (manual, sequence, autofocus) is shown here
            _capture.ImageCaptured += res =>
            {
                bool fromSequence = SequenceActive; // decided now: the sequence may have finished by the time the UI updates
                MainForm.Instance.Post(() =>
                {
                    _viewer.SetImage(res.Image, _viewer.Image == null);
                    camStatus.Text = (fromSequence ? T("Sequence") + " · " : "") + T("{0} stars · HFR {1:0.00} px · FWHM {2:0.00} px · median {3:0}", res.Stars, res.Hfr, res.Fwhm, res.Median * 65535) +
                        (res.Path != null ? "\r\n" + T("Saved {0}", System.IO.Path.GetFileName(res.Path)) : "");
                });
            };
            Tasks.Changed += SyncCameraButtons;
            _sequencer.Changed += () => MainForm.Instance.Post(SyncCameraButtons);
            stop.Click += (s, e) => StopCameraWork();
            SyncCameraButtons();

            // mount pad
            var mount = new FormGrid(90);
            var pos = mount.Row(T("Position"), new Label { Tag = "dim", Height = 48 });
            var pad = new TableLayoutPanel { ColumnCount = 3, RowCount = 3, Width = 150, Height = 120 };
            for (int i = 0; i < 3; i++) { pad.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33)); pad.RowStyles.Add(new RowStyle(SizeType.Percent, 33)); }
            var rate = Ui.Combo(T("Guide 0.5×"), T("Centre 8×"), T("Find 64×"), T("Slew max"));
            Func<double> rateVal = () => { var rr = new[] { 0.0021, 0.033, 0.27, 3.0 }; return rr[Math.Max(0, rate.SelectedIndex)]; };
            Func<string, int, double, FlatButton> padBtn = (icon, axis, sign) =>
            {
                var b = new FlatButton { Icon = icon, Dock = DockStyle.Fill, Margin = new Padding(2) };
                b.MouseDown += (s, e) => { try { Dm.Telescope?.MoveAxis(axis, sign * rateVal()); } catch (Exception ex) { MainForm.Instance.Toast(ex.Message, Icons.Warning); } };
                b.MouseUp += (s, e) => { try { Dm.Telescope?.MoveAxis(axis, 0); } catch { } };
                return b;
            };
            pad.Controls.Add(padBtn(Icons.Up, 1, 1), 1, 0);
            pad.Controls.Add(padBtn(Icons.Left, 0, 1), 0, 1);
            // the mount's Stop is available whenever a telescope is connected, not only while it reports a slew: a slew may be
            // started by another program, drivers do not always report one, and a stop must never wait for the next poll
            var stopBtn = new FlatButton { Icon = Icons.Stop, Dock = DockStyle.Fill, Kind = ButtonKind.Danger, Margin = new Padding(2), Enabled = false, ToolTipText = T("Stop the mount") };
            stopBtn.Click += (s, e) => { try { Dm.Telescope?.AbortSlew(); } catch { } };
            Action syncMountStop = () => { var tel = Dm.Telescope; bool on = false; try { on = tel != null && tel.Connected; } catch { } stopBtn.Enabled = on; };
            Dm.DevicesChanged += (s, e) => MainForm.Instance.Post(syncMountStop);
            syncMountStop();
            pad.Controls.Add(stopBtn, 1, 1);
            pad.Controls.Add(padBtn(Icons.Right, 0, -1), 2, 1);
            pad.Controls.Add(padBtn(Icons.Down, 1, -1), 1, 2);
            mount.Row(T("Rate"), rate);
            mount.Full(pad);
            var gotoBox = mount.Row(T("Go to"), new TextBox());
            var mb = new FlowLayoutPanel { Height = 76 };
            Func<string, string, Action, FlatButton> mbtn = (t, i, a) => { var b = new FlatButton(t, i, (s, e) => { try { a(); } catch (Exception ex) { MainForm.Instance.Toast(ex.Message, Icons.Warning, Theme.Current.Danger); } }); b.AutoSizeToText(); mb.Controls.Add(b); return b; };
            mbtn(T("Slew"), Icons.Target, () => GotoObject(gotoBox.Text, false));
            var centre = mbtn(T("Center"), Icons.Solve, () => GotoObject(gotoBox.Text, true));
            CameraButton(centre); // plate solves
            Availability.When(centre, () => PolarBusyReason() == null);
            mbtn(T("Park"), Icons.Lock, () => { var tel = Need(Dm.Telescope, NoTelescope); Tasks.Run(T("Parking"), t => tel.Park()); });
            mbtn(T("Unpark"), Icons.Play, () => Need(Dm.Telescope, NoTelescope).Unpark());
            var track = new FlatButton(T("Tracking"), Icons.Clock) { Toggle = true, Checked = true }; track.AutoSizeToText();
            track.CheckedChanged += (s, e) => { try { if (Dm.Telescope != null) Dm.Telescope.Tracking = track.Checked; } catch { } };
            mb.Controls.Add(track);
            mount.Full(mb);
            right.Controls.Add(new Collapsible(T("Focuser"), BuildFocuser()));
            right.Controls.Add(new Collapsible(T("Mount"), mount));

            Poll += () =>
            {
                syncMountStop(); // a lost connection shows here
                var tel = Dm.Telescope;
                if (tel != null && tel.Connected)
                {
                    try
                    {
                        pos.Text = string.Format("α {0}  δ {1}\r\n", AstroMath.FormatHms(tel.RightAscension, 0), AstroMath.FormatDms(tel.Declination, 0)) +
                            T("Alt {0:0.0}°  Az {1:0.0}°  {2}", tel.Altitude, tel.Azimuth, tel.SideOfPier) + (tel.Slewing ? " · " + T("slewing") : tel.AtPark ? " · " + T("parked") : "");
                    }
                    catch { }
                }
                else pos.Text = T("No telescope connected");
                var c = Dm.Camera;
                if (c != null && c.Connected)
                {
                    try { coolStatus.Text = T("{0:0.0} °C · power {1:0}%", c.CcdTemperature, c.CoolerPower); } catch { }
                }
                var fw = Dm.FilterWheel;
                if (fw != null && fw.Connected && filter.Items.Count == 0) { try { filter.Items.AddRange(fw.Names); vFilter.Items.AddRange(fw.Names); } catch { } }
            };

            panel.Controls.Add(_viewer);
            panel.Controls.Add(readout);
            panel.Controls.Add(right);
            panel.Controls.Add(left);
            return panel;
        }

        private void GotoObject(string q, bool center)
        {
            Need(Dm.Telescope, NoTelescope);
            double raDeg, decDeg;
            var o = CatalogManager.Instance.Find(q) ?? CatalogManager.Instance.Search(q, 1).FirstOrDefault();
            // the Sun, Moon or a planet by its English name or the one shown
            var body = Enum.GetValues(typeof(Core.Ephemeris.Body)).Cast<Core.Ephemeris.Body>()
                .Where(x => string.Equals(x.ToString(), q.Trim(), StringComparison.OrdinalIgnoreCase) || string.Equals(Core.Ephemeris.BodyNames.Name(x), q.Trim(), StringComparison.CurrentCultureIgnoreCase)).ToList();
            if (o != null) { raDeg = o.RaHours * 15; decDeg = o.DecDeg; }
            else if (body.Count > 0) { var p = SkyView.PlanetObject(body[0]); raDeg = p.RaDeg; decDeg = p.DecDeg; }
            else
            {
                var parts = q.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2) { raDeg = AstroMath.ParseRaHours(string.Join(" ", parts.Take(parts.Length / 2))) * 15; decDeg = AstroMath.ParseSexagesimal(string.Join(" ", parts.Skip(parts.Length / 2))); }
                else throw new ArgumentException(T("Unknown object: {0}", q));
            }
            if (center)
            {
                Need(Dm.Camera, NoCamera); // centring plate-solves its own frames
                if (!CanUseCamera() || !PolarFree()) return;
                var c = new Centering { Capture = _capture };
                c.Status += s => MainForm.Instance.SetStatus(s);
                RunCameraJob(T("Centering {0}", q), t => c.Center(raDeg, decDeg, t.Cts.Token).GetAwaiter().GetResult(), () => MainForm.Instance.Toast(T("Centered on {0}", q)), movesMount: true);
            }
            else
            {
                var app = new SkyContext(AppState.Site, AstroTime.Now).IcrsToApparent(raDeg * AstroMath.Deg2Rad, decDeg * AstroMath.Deg2Rad);
                Tasks.Run(T("Slewing to {0}", q), t => Centering.Slew(Dm.Telescope, app.RaHours, app.DecDeg, t.Cts.Token).GetAwaiter().GetResult());
            }
        }

        private Control BuildFocuser()
        {
            var g = new FormGrid(90);
            var pos = g.Row(T("Position"), new Label { Tag = "dim" });
            var step = g.Row(T("Step"), Ui.Num(100, 1, 10000));
            var mv = new FlowLayoutPanel { Height = 38 };
            var inn = new FlatButton(Tc("focuser", "In"), Icons.Left, (s, e) => { if (Dm.Focuser != null) Dm.Focuser.Move(Dm.Focuser.Position - (int)step.Value); }); inn.AutoSizeToText();
            var outb = new FlatButton(Tc("focuser", "Out"), Icons.Right, (s, e) => { if (Dm.Focuser != null) Dm.Focuser.Move(Dm.Focuser.Position + (int)step.Value); }); outb.AutoSizeToText();
            mv.Controls.Add(inn); mv.Controls.Add(outb);
            g.Full(mv);
            var afExp = g.Row(T("AF exposure"), Ui.Num(3, 0, 120, 1));
            var afStep = g.Row(T("AF step"), Ui.Num(100, 1, 5000));
            var plot = new PlotControl { Height = 170, Title = T("V-curve"), XLabel = T("Position"), YLabel = "HFR" };
            var af = new FlatButton(T("Run autofocus"), Icons.Target) { Kind = ButtonKind.Primary }; af.AutoSizeToText();
            af.Click += (s, e) =>
            {
                if (!Have(Dm.Camera, NoCamera) || !Have(Dm.Focuser, NoFocuser)) return;
                if (!CanUseCamera()) return;
                plot.Clear();
                var pts = plot.AddSeries("HFR", SeriesStyle.Scatter);
                var a = new Autofocus { Capture = _capture, Exposure = (double)afExp.Value, StepSize = (int)afStep.Value };
                a.PointMeasured += p => MainForm.Instance.Post(() => { pts.Add(p.Position, p.Hfr); plot.Invalidate(); });
                a.Status += st => MainForm.Instance.SetStatus(st);
                RunCameraJob(T("Autofocus"), t =>
                {
                    var r = a.Run(t.Cts.Token).GetAwaiter().GetResult();
                    MainForm.Instance.Post(() =>
                    {
                        if (r.Success)
                        {
                            var fit = plot.AddSeries(T("Hyperbola fit"));
                            var xs = r.Points.Select(p => (double)p.Position).ToList();
                            for (double x = xs.Min(); x <= xs.Max(); x += (xs.Max() - xs.Min()) / 100) fit.Add(x, r.A * Math.Sqrt(1 + Math.Pow((x - r.C) / r.B, 2)));
                            plot.Markers.Add(new PlotMarker { X = r.BestPosition, Label = T("best {0}", r.BestPosition) });
                        }
                        plot.Invalidate();
                        MainForm.Instance.Toast(r.Message);
                    });
                });
            };
            g.Full(af);
            CameraButton(af);
            g.Full(plot);
            Poll += () => { var f = Dm.Focuser; pos.Text = f != null && f.Connected ? f.Position + (f.IsMoving ? " " + T("(moving)") : "") + "  " + (double.IsNaN(f.Temperature) ? "" : f.Temperature.ToString("0.0") + " °C") : T("No focuser"); };
            return g;
        }

        // ================================================================== guiding

        /// <summary>A guider's state in words; PHD2's and the Sidralune guider's states use the same names.</summary>
        internal static string GuiderStateText(string state)
        {
            switch (state)
            {
                case "Stopped": return T("Stopped");
                case "Looping": return T("Looping");
                case "Selected": return T("Star selected");
                case "Calibrating": return T("Calibrating");
                case "Guiding": return T("Guiding");
                case "Paused": return T("Paused");
                case "LostLock": return T("Star lost");
                default: return state ?? "";
            }
        }

        /// <summary>What a calibration found, in words: its directions and rates, when it was made, and what looked wrong.</summary>
        internal static string CalibrationText(GuideCalibration c)
        {
            if (c == null) return T("Not calibrated. Calibrate & guide calibrates first; Start guiding calibrates when there is no calibration for this equipment.");
            var lines = new List<string>
            {
                T("Calibrated {0:yyyy-MM-dd HH:mm}: RA {1:0}° at {2:0.00} px/s, Dec {3:0}° at {4:0.00} px/s, {5:0.0}° from square.", c.When.ToLocalTime(), c.RaAngleDeg, c.RaRatePxPerSec, c.DecAngleDeg, c.DecRatePxPerSec, c.OrthogonalityErrorDeg)
            };
            foreach (var issue in c.Issues)
            {
                if (issue == GuideCalibration.IssueOrthogonality) lines.Add("⚠ " + T("RA and Dec are far from square: backlash or poor seeing may have upset the calibration. Calibrate again."));
                if (issue == GuideCalibration.IssueRateRatio) lines.Add("⚠ " + T("RA and Dec moved at rates that do not fit the declination. Check the guide rates, then calibrate again."));
                if (issue == GuideCalibration.IssueFewSteps) lines.Add("⚠ " + T("Few calibration steps. A shorter calibration step makes the calibration more accurate."));
                if (issue == GuideCalibration.IssuePrediction) lines.Add("⚠ " + T("The star moved at another speed than the guide rate and focal length predict. Check the guide scope's focal length and the mount's guide rate."));
                if (issue == GuideCalibration.IssueNearPole) lines.Add("⚠ " + T("Calibrated near the pole, where RA moves the star little. Calibrate nearer the celestial equator when you can."));
            }
            return string.Join(Environment.NewLine, lines);
        }

        // ================================================================== sequencer

        private TreeView _seqTree;
        private PropertyGrid _seqProps;

        private Control BuildSequencer()
        {
            var panel = new Panel { Dock = DockStyle.Fill };
            _seqTree = new TreeView { Dock = DockStyle.Fill, HideSelection = false, FullRowSelect = true, ItemHeight = 26 };
            _seqProps = new PropertyGrid { Dock = DockStyle.Fill, HelpVisible = false, PropertySort = PropertySort.NoSort };
            var log = new TextBox { Multiline = true, ReadOnly = true, Dock = DockStyle.Bottom, Height = 140, ScrollBars = ScrollBars.Vertical, Font = Theme.MonoFont };
            // owner-drawn check list (the native CheckedListBox draws its boxes in system colours)
            var triggers = new ListBox { Dock = DockStyle.Bottom, Height = 110, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 24 };
            foreach (var t in _sequencer.Triggers) triggers.Items.Add(t);
            triggers.DrawItem += (s, e) =>
            {
                if (e.Index < 0) return;
                var p = Theme.Current;
                var t = _sequencer.Triggers[e.Index];
                using (var b = new SolidBrush((e.State & DrawItemState.Selected) != 0 ? p.AccentSoft : triggers.BackColor)) e.Graphics.FillRectangle(b, e.Bounds);
                int size = (int)Math.Round(13 * triggers.DeviceDpi / 96.0);
                Theme.DrawCheckGlyph(e.Graphics, new Rectangle(e.Bounds.X + 4, e.Bounds.Y + (e.Bounds.Height - size) / 2, size - 1, size - 1), t.Enabled, true);
                TextRenderer.DrawText(e.Graphics, t.Title, triggers.Font, new Rectangle(e.Bounds.X + size + 10, e.Bounds.Y, e.Bounds.Width - size - 10, e.Bounds.Height), p.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            };
            Action<int> toggle = i => { if (i >= 0 && i < _sequencer.Triggers.Count) { _sequencer.Triggers[i].Enabled = !_sequencer.Triggers[i].Enabled; triggers.Invalidate(); } };
            triggers.MouseClick += (s, e) => toggle(triggers.IndexFromPoint(e.Location));
            triggers.KeyDown += (s, e) => { if (e.KeyCode == Keys.Space) { toggle(triggers.SelectedIndex); e.Handled = true; } };
            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 44 };
            var add = new FlatButton(T("Add"), Icons.Add); add.AutoSizeToText();
            var addMenu = new ContextMenuStrip { Renderer = new DarkToolStripRenderer() };
            foreach (var t in Sequencer.ItemTypes)
            {
                var type = t;
                addMenu.Items.Add(GridAdapter.Text(t.Name), null, (s, e) => AddItem((SequenceItem)Activator.CreateInstance(type)));
            }
            add.Click += (s, e) => addMenu.Show(add, new Point(0, add.Height));
            Func<string, string, Action, FlatButton> btn = (t, i, a) => { var b = new FlatButton(t, i, (s, e) => a()); b.AutoSizeToText(); bar.Controls.Add(b); return b; };
            bar.Controls.Add(add);
            btn(T("Remove"), Icons.Remove, RemoveItem);
            btn(T("Up"), Icons.Up, () => MoveItem(-1));
            btn(T("Down"), Icons.Down, () => MoveItem(1));
            var template = btn(T("Template…"), Icons.Star, () =>
            {
                var sel = AppState.Selection;
                var name = sel != null ? sel.Name : "M 31";
                double ra = sel != null ? sel.RaDeg : 10.684, dec = sel != null ? sel.DecDeg : 41.269;
                _sequencer.Root = Sequencer.Template(name, ra, dec);
                RefreshTree();
            });
            var openSeq = btn(T("Open…"), Icons.Open, () => { using (var d = new OpenFileDialog { Filter = T("Sequence") + "|*.json" }) if (d.ShowDialog() == DialogResult.OK) { _sequencer.Load(d.FileName); RefreshTree(); } });
            var saveSeq = btn(T("Save…"), Icons.Save, () => { using (var d = new SaveFileDialog { Filter = T("Sequence") + "|*.json", FileName = "sequence.json" }) if (d.ShowDialog() == DialogResult.OK) _sequencer.Save(d.FileName); });
            var run = new FlatButton(T("Run sequence"), Icons.Play) { Kind = ButtonKind.Primary }; run.AutoSizeToText();
            run.Click += (s, e) =>
            {
                if (SequenceActive || !CanUseCamera() || !PolarFree()) return;
                var missing = _sequencer.MissingDevices();
                if (missing.Count > 0) { MainForm.Instance.Toast(Sequencer.DescribeMissing(missing) + T(", or click Connect simulated observatory."), Icons.Warning); return; }
                _capture.ImageFolder = AppState.Settings.ImageFolder;
                _sequenceTask = Tasks.Run(T("Sequence"), t => { using (t.Cts.Token.Register(_sequencer.Stop)) _sequencer.Start(_capture).GetAwaiter().GetResult(); });
            };
            bar.Controls.Add(run);
            CameraButton(run);
            Availability.When(run, () => PolarBusyReason() == null);
            // the running sequence can still be edited, but not replaced or saved half done
            _sequenceFileButtons.AddRange(new Control[] { template, openSeq, saveSeq });
            var seqStop = btn(T("Stop"), Icons.Stop, () => _sequencer.Stop());
            seqStop.Kind = ButtonKind.Danger;
            _sequenceStop = seqStop;
            SyncCameraButtons();
            _sequencer.Log += s => MainForm.Instance.Post(() => log.AppendText(DateTime.Now.ToString("HH:mm:ss ") + s + "\r\n"));
            _sequencer.Changed += () => MainForm.Instance.Post(UpdateTreeStatus);
            _seqTree.AfterSelect += (s, e) => _seqProps.SelectedObject = e.Node?.Tag != null ? new GridAdapter(e.Node.Tag, T("Instruction")) : null;
            _seqProps.PropertyValueChanged += (s, e) => UpdateTreeStatus();
            var split = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 560 };
            split.Panel1.Controls.Add(_seqTree);
            var rightCard = new Card(T("Properties")) { Dock = DockStyle.Fill };
            rightCard.Controls.Add(_seqProps);
            var trigCard = new Card(T("Triggers (checked between exposures)")) { Dock = DockStyle.Bottom, Height = 160 };
            trigCard.Controls.Add(triggers);
            split.Panel2.Controls.Add(rightCard);
            split.Panel2.Controls.Add(trigCard);
            panel.Controls.Add(split);
            panel.Controls.Add(log);
            panel.Controls.Add(bar);
            _sequencer.Root = Sequencer.Template("M 31", 10.684, 41.269);
            RefreshTree();
            return panel;
        }

        private void RefreshTree()
        {
            _seqTree.BeginUpdate();
            _seqTree.Nodes.Clear();
            _seqTree.Nodes.Add(Node(_sequencer.Root));
            _seqTree.ExpandAll();
            _seqTree.EndUpdate();
        }

        private TreeNode Node(SequenceItem it)
        {
            var n = new TreeNode(Label(it)) { Tag = it };
            if (it.Children != null) foreach (var c in it.Children) n.Nodes.Add(Node(c));
            return n;
        }

        private static string Label(SequenceItem it)
        {
            string st = it.Status == ItemStatus.Running ? "▶ " : it.Status == ItemStatus.Done ? "✓ " : it.Status == ItemStatus.Failed ? "✗ " : it.Status == ItemStatus.Skipped ? "– " : "";
            return st + it.Title + (string.IsNullOrEmpty(it.Summary) ? "" : "   " + it.Summary) + (it.Enabled ? "" : "  " + T("(disabled)"));
        }

        private void UpdateTreeStatus()
        {
            Action<TreeNodeCollection> walk = null;
            walk = nodes => { foreach (TreeNode n in nodes) { var it = (SequenceItem)n.Tag; n.Text = Label(it); n.ForeColor = it.Status == ItemStatus.Failed ? Theme.Current.Danger : it.Status == ItemStatus.Running ? Theme.Current.Accent : it.Status == ItemStatus.Done ? Theme.Current.Success : Theme.Current.Text; walk(n.Nodes); } };
            walk(_seqTree.Nodes);
        }

        private void AddItem(SequenceItem item)
        {
            var sel = _seqTree.SelectedNode;
            var container = sel?.Tag as SequenceContainer ?? (sel?.Parent?.Tag as SequenceContainer) ?? _sequencer.Root;
            int idx = sel != null && sel.Tag is SequenceContainer ? container.Items.Count : (sel != null ? container.Items.IndexOf((SequenceItem)sel.Tag) + 1 : container.Items.Count);
            if (item is TargetContainer && AppState.Selection != null)
                ((TargetContainer)item).Target = new SequenceTarget { Name = AppState.Selection.Name, RaDeg = AppState.Selection.RaDeg, DecDeg = AppState.Selection.DecDeg };
            container.Insert(idx, item);
            RefreshTree();
        }

        private void RemoveItem()
        {
            var sel = _seqTree.SelectedNode;
            var parent = sel?.Parent?.Tag as SequenceContainer;
            if (parent == null) return;
            if (!parent.Remove((SequenceItem)sel.Tag)) { MainForm.Instance.Toast(T("That instruction is running. Stop the sequence to remove it."), Icons.Info); return; }
            RefreshTree();
        }

        private void MoveItem(int dir)
        {
            var sel = _seqTree.SelectedNode;
            var parent = sel?.Parent?.Tag as SequenceContainer;
            if (parent == null) return;
            var it = (SequenceItem)sel.Tag;
            if (it.Status == ItemStatus.Running) { MainForm.Instance.Toast(T("That instruction is running. Stop the sequence to move it."), Icons.Info); return; }
            if (parent.Move(it, dir)) RefreshTree();
        }

        // ================================================================== dome & weather

        private Control BuildDome()
        {
            var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            var dome = new Card(T("Dome / roof")) { Dock = DockStyle.Fill };
            var dg = new FormGrid(120);
            var st = dg.Row(T("State"), new Label { Tag = "dim" });
            var az = dg.Row(T("Azimuth"), Ui.Num(0, 0, 360, 1));
            var bar = new FlowLayoutPanel { Height = 80 };
            Action<string, string, Action> btn = (t, i, a) => { var b = new FlatButton(t, i, (s, e) => { try { a(); } catch (Exception ex) { MainForm.Instance.Toast(ex.Message, Icons.Warning); } }); b.AutoSizeToText(); bar.Controls.Add(b); };
            var slave = new FlatButton(T("Follow telescope"), Icons.Refresh) { Toggle = true }; slave.AutoSizeToText();
            btn(T("Open"), Icons.Up, () => Need(Dm.Dome, NoDome).OpenShutter());
            btn(T("Close"), Icons.Down, () => Need(Dm.Dome, NoDome).CloseShutter());
            // moving the dome by hand ends following the telescope, which would turn it straight back
            btn(T("Slew"), Icons.Target, () => { var d = Need(Dm.Dome, NoDome); slave.Checked = false; d.SlewToAzimuth((double)az.Value); });
            btn(T("Park"), Icons.Lock, () => { var d = Need(Dm.Dome, NoDome); slave.Checked = false; d.Park(); });
            bar.Controls.Add(slave);
            dg.Full(bar);
            dome.Controls.Add(dg);
            var weather = new Card(T("Weather & safety")) { Dock = DockStyle.Fill };
            var wl = new Label { Dock = DockStyle.Fill, Font = Theme.UiFont };
            weather.Controls.Add(wl);
            Poll += () =>
            {
                var d = Dm.Dome;
                st.Text = d != null && d.Connected ? T("Shutter {0} · azimuth {1:0.0}°", d.Shutter, d.Azimuth) + (d.Slewing ? " · " + T("moving") : "") : T("No dome connected");
                if (d != null && slave.Checked && Dm.Telescope != null)
                    try { double a = Dm.Telescope.Azimuth; if (Math.Abs(AstroMath.NormalizeDeg180(a - d.Azimuth)) > 3 && !d.Slewing) d.SlewToAzimuth(a); } catch { }
                var w = Dm.Weather; var s = Dm.Safety;
                wl.Text = (w != null ? string.Join("\r\n", T("Temperature {0:0.0} °C", w.Temperature), T("Humidity {0:0}%  ·  dew point {1:0.0} °C", w.Humidity, w.DewPoint), T("Cloud cover {0:0}%", w.CloudCover),
                    T("Wind {0:0.0} m/s", w.WindSpeed), T("Rain rate {0:0.0} mm/h", w.RainRate), T("Sky quality {0:0.00} mag/arcsec²", w.SkyQuality), T("Sky temperature {0:0} °C", w.SkyTemperature)) + "\r\n" : T("No weather station connected") + "\r\n")
                    + "\r\n" + T("Safety: {0}", s == null ? T("no monitor") : s.IsSafe ? T("SAFE") : T("UNSAFE"));
                wl.ForeColor = s != null && !s.IsSafe ? Theme.Current.Danger : Theme.Current.Text;
            };
            panel.RowStyles.Add(new RowStyle(SizeType.Percent, 50)); panel.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            panel.Controls.Add(dome, 0, 0);
            panel.Controls.Add(weather, 1, 0);
            panel.Controls.Add(BuildSwitches(), 0, 1);
            panel.Controls.Add(BuildFlatPanel(), 1, 1);
            return panel;
        }

        /// <summary>The switch box's switches: on/off ones as tick boxes, ones with levels as numbers; built again when it changes.</summary>
        private Control BuildSwitches()
        {
            var card = new Card(T("Switches")) { Dock = DockStyle.Fill };
            var g = new FormGrid(160) { AutoScroll = true };
            var none = new Label { Text = T("No switch box connected."), AutoSize = true, Tag = "dim" };
            card.Controls.Add(g);
            ISwitchDevice shown = null;
            var editors = new List<Tuple<int, Control, double>>();
            bool loading = false;
            Action build = () =>
            {
                var sw = Dm.Switch;
                if (sw == shown && g.Controls.Count > 0) return;
                shown = sw;
                editors.Clear();
                g.SuspendLayout();
                g.Controls.Clear(); g.RowStyles.Clear(); g.RowCount = 0;
                if (sw == null) { g.Full(none); g.ResumeLayout(); return; }
                int n = 0;
                try { n = sw.Count; } catch { }
                for (int k = 0; k < n; k++)
                {
                    int i = k;
                    string name; double max;
                    try { name = sw.GetName(i); max = sw.GetMaxValue(i); } catch { continue; }
                    if (max <= 1)
                    {
                        var cb = g.Row(name, new CheckBox { Text = T("On") });
                        cb.CheckedChanged += (s, e) => { if (!loading) Try(() => sw.SetSwitch(i, cb.Checked)); };
                        editors.Add(Tuple.Create(i, (Control)cb, max));
                    }
                    else
                    {
                        var num = g.Row(name, Ui.Num(0, 0, (decimal)max));
                        num.ValueChanged += (s, e) => { if (!loading) Try(() => sw.SetValue(i, (double)num.Value)); };
                        editors.Add(Tuple.Create(i, (Control)num, max));
                    }
                }
                g.ResumeLayout(true);
                Theme.Apply(g);
            };
            build();
            Dm.DevicesChanged += (s, e) => MainForm.Instance.Post(build);
            Poll += () =>
            {
                build();
                var sw = Dm.Switch;
                if (sw == null) return;
                loading = true;
                try
                {
                    foreach (var ed in editors)
                    {
                        if (ed.Item2.Focused) continue;
                        var cb = ed.Item2 as CheckBox;
                        if (cb != null) cb.Checked = sw.GetSwitch(ed.Item1);
                        var num = ed.Item2 as NumericUpDown;
                        if (num != null) num.Value = Math.Max(num.Minimum, Math.Min(num.Maximum, (decimal)sw.GetValue(ed.Item1)));
                    }
                }
                catch { }
                finally { loading = false; }
            };
            return card;
        }

        /// <summary>The flat panel: its cover opened and closed, and its light on at a brightness or off.</summary>
        private Control BuildFlatPanel()
        {
            var card = new Card(T("Flat panel")) { Dock = DockStyle.Fill };
            var g = new FormGrid(160);
            var state = g.Row(T("State"), new Label { Tag = "dim", AutoSize = true, UseMnemonic = false });
            var brightness = g.Row(T("Brightness"), Ui.Num(128, 0, 255));
            var open = MakeButton(T("Open cover"), Icons.Up);
            var close = MakeButton(T("Close cover"), Icons.Down);
            var on = MakeButton(T("Light on"), Icons.Star);
            var off = MakeButton(T("Light off"), Icons.Close);
            g.Full(ButtonRow(open, close, on, off));
            g.Full(Ui.Hint(T("For flat frames: close the cover over the telescope, light the panel, and take FLAT exposures. The sequencer has a Flat panel instruction for the same.")));
            card.Controls.Add(g);
            Func<ICoverCalibrator> panel = () => Need(Dm.Cover, T("No flat panel connected."));
            open.Click += (s, e) => Try(() => panel().OpenCover());
            close.Click += (s, e) => Try(() => panel().CloseCover());
            on.Click += (s, e) => Try(() => { var c = panel(); c.CalibratorOn((int)Math.Min(c.MaxBrightness, brightness.Value)); });
            off.Click += (s, e) => Try(() => panel().CalibratorOff());
            Poll += () =>
            {
                var c = Dm.Cover;
                foreach (var b in new Control[] { open, close, on, off, brightness }) b.Enabled = c != null;
                if (c == null) { state.Text = T("No flat panel connected."); return; }
                try
                {
                    string cover = c.CoverState;
                    string coverText = cover == "Open" ? T("open") : cover == "Closed" ? T("closed") : cover == "Moving" ? T("moving") : cover;
                    if (brightness.Maximum != Math.Max(1, c.MaxBrightness)) brightness.Maximum = Math.Max(1, c.MaxBrightness);
                    state.Text = T("Cover {0} · light {1} of {2}", coverText, c.Brightness, c.MaxBrightness);
                }
                catch (Exception ex) { state.Text = ex.Message; }
            };
            return card;
        }

        public IEnumerable<AppCommand> GetCommands()
        {
            yield return new AppCommand { Title = T("Observatory: open equipment"), Run = () => { MainForm.Instance.Navigate("observatory"); _tabs.Select(0); } };
            yield return new AppCommand { Title = T("Observatory: sequencer"), Run = () => { MainForm.Instance.Navigate("observatory"); _tabs.Select(3); } };
            yield return new AppCommand { Title = T("Observatory: polar alignment"), Run = () => { MainForm.Instance.Navigate("observatory"); _tabs.Select(4); } };
            yield return new AppCommand { Title = T("Observatory: abort all"), Icon = Icons.Stop, Run = () => { _captureCts?.Cancel(); _cameraJob?.Cts.Cancel(); _sequencer.Stop(); try { Dm.Telescope?.AbortSlew(); } catch { } } };
        }
    }
}
