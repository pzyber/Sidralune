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

namespace Sidralune.Views
{
    public class ObservatoryView : UserControl, ICommandSource
    {
        private readonly TabbedPanel _tabs = new TabbedPanel { Dock = DockStyle.Fill, Padding = new Padding(10) };
        private readonly CaptureService _capture = new CaptureService();
        private readonly Sequencer _sequencer = new Sequencer();
        private DeviceManager Dm => DeviceManager.Instance;

        private const string Phd2Hint = "Start PHD2, enable its server (Tools → Enable Server) and click Connect PHD2.";

        /// <summary>The device, or a clear error (shown as a toast by the buttons) when none of that kind is connected.</summary>
        private static T Need<T>(T device, string what, string hint = null) where T : class
        {
            if (device == null) throw new InvalidOperationException("No " + what + " connected. " + (hint ?? "Connect one in Observatory → Equipment."));
            return device;
        }

        /// <summary>For handlers that do not catch: show the same message as a toast and return false.</summary>
        private static bool Have(object device, string what)
        {
            if (device != null) return true;
            MainForm.Instance.Toast("No " + what + " connected. Connect one in Observatory → Equipment.", Icons.Warning);
            return false;
        }
        private readonly System.Windows.Forms.Timer _poll = new System.Windows.Forms.Timer { Interval = 1000 };
        private event Action Poll;

        public ObservatoryView()
        {
            Controls.Add(_tabs);
            _capture.ImageFolder = AppState.Settings.ImageFolder;
            _tabs.AddPage("Equipment", BuildEquipment());
            _tabs.AddPage("Imaging", BuildImaging());
            _tabs.AddPage("Guiding", BuildGuiding());
            _tabs.AddPage("Sequencer", BuildSequencer());
            _tabs.AddPage("Polar alignment", BuildPolar());
            _tabs.AddPage("Dome & weather", BuildDome());
            _poll.Tick += (s, e) => { if (Visible) Poll?.Invoke(); };
            _poll.Start();
        }

        // ================================================================== equipment

        private Control BuildEquipment()
        {
            var panel = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
            var table = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 6, Padding = new Padding(0, 4, 0, 0) };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 3; i++) table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 260));
            var kinds = new[] { DeviceKind.Telescope, DeviceKind.Camera, DeviceKind.Focuser, DeviceKind.FilterWheel, DeviceKind.Rotator, DeviceKind.Dome, DeviceKind.ObservingConditions, DeviceKind.SafetyMonitor, DeviceKind.Switch, DeviceKind.CoverCalibrator, DeviceKind.Guider };
            foreach (var k in kinds)
            {
                var kind = k;
                int r = table.RowCount++;
                table.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
                var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, Margin = new Padding(4, 6, 4, 4) };
                foreach (var d in Dm.Discover(kind, false)) combo.Items.Add(d);
                if (combo.Items.Count > 0) combo.SelectedIndex = 0;
                var connect = new FlatButton("Connect", Icons.Connect) { Margin = new Padding(4, 4, 4, 4) }; connect.AutoSizeToText();
                var setup = new FlatButton("Setup…", Icons.Settings) { Margin = new Padding(4, 4, 4, 4) }; setup.AutoSizeToText();
                var choose = new FlatButton("ASCOM…", Icons.More) { Margin = new Padding(4, 4, 4, 4), ToolTipText = "Open the ASCOM Chooser" }; choose.AutoSizeToText();
                var status = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Tag = "dim" };
                table.Controls.Add(new Label { Text = SplitWords(kind.ToString()), Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Font = Theme.UiFontBold }, 0, r);
                table.Controls.Add(combo, 1, r);
                table.Controls.Add(connect, 2, r);
                table.Controls.Add(setup, 3, r);
                table.Controls.Add(choose, 4, r);
                table.Controls.Add(status, 5, r);
                Action refresh = () =>
                {
                    var dev = Dm.Get(kind);
                    connect.Text = dev != null && dev.Connected ? "Disconnect" : "Connect";
                    connect.Kind = dev != null && dev.Connected ? ButtonKind.Danger : ButtonKind.Normal;
                    connect.AutoSizeToText();
                    status.Text = dev != null ? (dev.Connected ? "● Connected: " + dev.Info.Name : "○ Lost connection") : "Not connected";
                    status.ForeColor = dev != null && dev.Connected ? Theme.Current.Success : Theme.Current.TextDim;
                };
                connect.Click += (s, e) =>
                {
                    var dev = Dm.Get(kind);
                    if (dev != null && dev.Connected) { Dm.Disconnect(kind); refresh(); return; }
                    var info = combo.SelectedItem as DeviceInfo;
                    if (info == null) return;
                    Tasks.Run("Connecting " + info.Name, t => Dm.Connect(info), () => { refresh(); MainForm.Instance.Toast("Connected " + info.Name); });
                };
                setup.Click += (s, e) =>
                {
                    var info = combo.SelectedItem as DeviceInfo;
                    if (info != null && info.Protocol == DeviceProtocol.Ascom) try { new AscomTelescope(info).SetupDialog(); } catch (Exception ex) { MainForm.Instance.Toast(ex.Message, Icons.Warning); }
                    else if (info != null && info.Protocol == DeviceProtocol.Lx200)
                    {
                        var addr = PlannerView.Prompt("LX200 / OnStep address", "Serial port (COM3:9600) or TCP host:port:", info.Address);
                        if (!string.IsNullOrEmpty(addr)) info.Address = addr;
                    }
                    else MainForm.Instance.Toast("No setup dialog for this device");
                };
                choose.Click += (s, e) =>
                {
                    try
                    {
                        var id = AscomDiscovery.Choose(kind);
                        if (id == null) { if (!AscomDiscovery.PlatformInstalled) MainForm.Instance.Toast("The ASCOM Platform is not installed (ascom-standards.org)", Icons.Info); return; }
                        var info = new DeviceInfo { Kind = kind, Protocol = DeviceProtocol.Ascom, Name = id, Address = id };
                        combo.Items.Add(info); combo.SelectedItem = info;
                    }
                    catch (Exception ex) { MainForm.Instance.Toast(ex.Message, Icons.Warning); }
                };
                choose.Visible = kind != DeviceKind.Guider;
                Dm.DevicesChanged += (s, e) => MainForm.Instance.Post(refresh);
                refresh();
            }
            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 46 };
            var sim = new FlatButton("Connect simulated observatory", Icons.Observatory, (s, e) => Tasks.Run("Connecting simulators", t => Dm.ConnectSimulators(), () => MainForm.Instance.Toast("Simulated observatory ready"))) { Kind = ButtonKind.Primary }; sim.AutoSizeToText();
            var alpaca = new FlatButton("Discover Alpaca devices", Icons.Search, (s, e) => Tasks.Run("Alpaca discovery", t =>
            {
                var servers = Devices.Alpaca.AlpacaDiscovery.FindServers(2000);
                var found = servers.SelectMany(sv => { try { return Devices.Alpaca.AlpacaDiscovery.Devices(sv); } catch { return new List<DeviceInfo>(); } }).ToList();
                MainForm.Instance.Post(() =>
                {
                    foreach (var combo in table.Controls.OfType<ComboBox>())
                    {
                        int row = table.GetRow(combo);
                        var kind = kinds[row];
                        foreach (var d in found.Where(f => f.Kind == kind)) combo.Items.Add(d);
                    }
                    MainForm.Instance.Toast(found.Count + " Alpaca device(s) on " + servers.Count + " server(s)");
                });
            })); alpaca.AutoSizeToText();
            var indi = new FlatButton("Add INDI server…", Icons.Add, (s, e) => AddIndi(table, kinds)); indi.AutoSizeToText();
            var disc = new FlatButton("Disconnect all", Icons.Stop, (s, e) => Dm.DisconnectAll()) { Kind = ButtonKind.Danger }; disc.AutoSizeToText();
            bar.Controls.AddRange(new Control[] { sim, alpaca, indi, disc });
            var optics = new FormGrid(220);
            var fl = optics.Row("Imaging focal length (mm)", Ui.Num((decimal)Dm.FocalLengthMm, 10, 20000));
            var ap = optics.Row("Aperture (mm)", Ui.Num((decimal)Dm.ApertureMm, 10, 2000));
            fl.ValueChanged += (s, e) => Dm.FocalLengthMm = (double)fl.Value;
            ap.ValueChanged += (s, e) => Dm.ApertureMm = (double)ap.Value;
            optics.Full(Ui.Hint("ASCOM drivers are used through COM late binding, so no SDK is needed; install the ASCOM Platform plus your device drivers. Alpaca devices are found by UDP broadcast. INDI servers (for example on a Raspberry Pi) are reached over TCP 7624. LX200/OnStep mounts connect via serial or Wi-Fi."));
            var card = new Card("Optics") { Dock = DockStyle.Top, Height = 170 };
            card.Controls.Add(optics);
            panel.Controls.Add(card);
            panel.Controls.Add(table);
            panel.Controls.Add(bar);
            return panel;
        }

        private void AddIndi(TableLayoutPanel table, DeviceKind[] kinds)
        {
            var host = PlannerView.Prompt("INDI server", "Host:port", "localhost:7624");
            if (string.IsNullOrEmpty(host)) return;
            Tasks.Run("INDI discovery", t =>
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
                        foreach (var combo in table.Controls.OfType<ComboBox>())
                        {
                            var kind = kinds[table.GetRow(combo)];
                            if ((kind == DeviceKind.Telescope && isScope) || (kind == DeviceKind.Camera && isCam) || (kind == DeviceKind.Focuser && isFoc))
                                combo.Items.Add(new DeviceInfo { Kind = kind, Protocol = DeviceProtocol.Indi, Name = d + " (INDI)", Address = host + "/" + d });
                        }
                    }
                    MainForm.Instance.Toast(devs.Count + " INDI device(s) found");
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
        // buttons that start camera work; true marks Imaging's Start/Loop, which may replace a running manual capture
        private readonly List<Tuple<Control, bool>> _cameraButtons = new List<Tuple<Control, bool>>();
        private Label _busyNote;

        /// <summary>What is using the camera, or null when it is free.</summary>
        private string CameraBusyReason(bool ignoreManual = false)
        {
            if (SequenceActive) return "A sequence is running and using the camera";
            if (_cameraJob != null && !_cameraJob.Done) return _cameraJobName + " is running and using the camera";
            if (!ignoreManual && ManualCapturing) return "A capture or preview loop is running in Imaging";
            return null;
        }

        private bool CanUseCamera(bool ignoreManual = false)
        {
            var why = CameraBusyReason(ignoreManual);
            if (why != null) MainForm.Instance.Toast(why + ". Stop it first.", Icons.Info);
            return why == null;
        }

        /// <summary>Start camera work other than Imaging's own capture: autofocus, centring, polar alignment.</summary>
        private void RunCameraJob(string name, Action<BackgroundTask> work, Action onDone = null)
        {
            _cameraJobName = name;
            _cameraJob = Tasks.Run(name, work, onDone);
            SyncCameraButtons();
        }

        private void SyncCameraButtons()
        {
            foreach (var b in _cameraButtons) b.Item1.Enabled = CameraBusyReason(b.Item2) == null;
            if (_busyNote == null) return;
            var why = CameraBusyReason(true);
            _busyNote.Visible = why != null;
            if (why != null) _busyNote.Text = SequenceActive ? "A sequence is running and using the camera. Its frames are shown here as they arrive; Stop ends the sequence." : why + ". Stop ends it.";
        }

        private Control BuildImaging()
        {
            var panel = new Panel { Dock = DockStyle.Fill };
            _viewer = new ImageViewer { Dock = DockStyle.Fill, EmptyText = "Connect a camera and take an exposure" };
            var readout = new Label { Dock = DockStyle.Bottom, Height = 22, Tag = "dim" };
            _viewer.ReadoutChanged += s => readout.Text = s;
            var left = new Panel { Dock = DockStyle.Left, Width = 300, AutoScroll = true };
            var right = new Panel { Dock = DockStyle.Right, Width = 300, AutoScroll = true };

            // camera controls
            var cam = new FormGrid(110);
            var exp = cam.Row("Exposure (s)", Ui.Num(5, 0, 3600, 3, 1));
            var count = cam.Row("Count", Ui.Num(1, 1, 9999));
            var type = cam.Row("Frame type", Ui.Combo("LIGHT", "DARK", "FLAT", "BIAS", "SNAPSHOT"));
            var bin = cam.Row("Binning", Ui.Combo("1", "2", "3", "4"));
            var gain = cam.Row("Gain", Ui.Num(100, -1, 10000));
            var offset = cam.Row("Offset", Ui.Num(30, -1, 10000));
            var filter = cam.Row("Filter", new ComboBox { DropDownStyle = ComboBoxStyle.DropDown });
            var target = cam.Row("Target", new TextBox { Text = "Untitled" });
            var save = cam.Row("Save", new CheckBox { Text = "Save frames", Checked = true });
            var pattern = cam.Row("File pattern", new TextBox { Text = _capture.FilePattern });
            // button rows and status lines size to their content, so an empty status or a single row of buttons takes no extra space
            var btns = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true };
            var start = new FlatButton("Start", Icons.Play) { Kind = ButtonKind.Primary }; start.AutoSizeToText();
            var loop = new FlatButton("Loop", Icons.Refresh) { ToolTipText = "Continuous preview (not saved)" }; loop.AutoSizeToText();
            var stop = new FlatButton("Stop", Icons.Stop) { Kind = ButtonKind.Danger }; stop.AutoSizeToText();
            btns.Controls.AddRange(new Control[] { start, loop, stop });
            cam.Full(btns);
            var camStatus = new Label { Tag = "dim", AutoSize = true, MaximumSize = new Size(270, 0), Visible = false };
            camStatus.TextChanged += (s, e) => camStatus.Visible = camStatus.Text.Length > 0; // an empty line would still take a row
            cam.Full(camStatus);
            // cooling
            var cool = new FormGrid(110);
            var setT = cool.Row("Set point (°C)", Ui.Num(-10, -60, 30, 1));
            var coolOn = cool.Row("Cooler", new CheckBox { Text = "On" });
            var coolStatus = cool.Row("Status", new Label { Tag = "dim" });
            setT.ValueChanged += (s, e) => { if (Dm.Camera != null && Dm.Camera.CanSetTemperature) Dm.Camera.SetTemperature = (double)setT.Value; };
            coolOn.CheckedChanged += (s, e) => { if (Dm.Camera != null && Dm.Camera.CanSetTemperature) { Dm.Camera.SetTemperature = (double)setT.Value; Dm.Camera.CoolerOn = coolOn.Checked; } };
            var coolCard = new Collapsible("Cooling", cool);

            // planetary tab: video of short exposures back to back into a SER file, with its own camera settings
            // (planetary and deep-sky settings are nearly opposite, so each tab keeps its own)
            var vid = new FormGrid(110);
            var vExp = vid.Row("Exposure (ms)", Ui.Num(10, 0.1m, 10000, 1));
            var vRoi = vid.Row("Region", Ui.Combo("Full sensor", "1920 × 1080", "1280 × 960", "800 × 600", "640 × 480", "320 × 240"));
            var vBin = vid.Row("Binning", Ui.Combo("1", "2", "3", "4"));
            var vGain = vid.Row("Gain", Ui.Num(100, -1, 10000));
            var vOffset = vid.Row("Offset", Ui.Num(30, -1, 10000));
            var vFilter = vid.Row("Filter", new ComboBox { DropDownStyle = ComboBoxStyle.DropDown });
            var vTarget = vid.Row("Target", new TextBox { Text = "Untitled" });
            var vFrames = vid.Row("Max frames", Ui.Num(1000, 1, 100000));
            var vSecs = vid.Row("Max seconds", Ui.Num(60, 1, 3600));
            var vBtns = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true };
            var rec = new FlatButton("Record", Icons.Play) { Kind = ButtonKind.Primary }; rec.AutoSizeToText();
            var vStop = new FlatButton("Stop", Icons.Stop) { Kind = ButtonKind.Danger }; vStop.AutoSizeToText();
            var openPl = new FlatButton("Open in Planetary", Icons.Planet) { Enabled = false }; openPl.AutoSizeToText();
            vBtns.Controls.AddRange(new Control[] { rec, vStop, openPl });
            vStop.Click += (s, e) => { if (_cameraJob != null && _cameraJobName == "Recording video") _cameraJob.Cts.Cancel(); };
            vid.Full(vBtns);
            var vidStatus = new Label { Tag = "dim", AutoSize = true, MaximumSize = new Size(270, 0), Visible = false };
            vidStatus.TextChanged += (s, e) => vidStatus.Visible = vidStatus.Text.Length > 0;
            vid.Full(vidStatus);
            vid.Full(Ui.Hint("Records a SER video for the Planetary workspace. A small region gives the highest frame rate. Stop ends the recording and keeps the frames so far."));

            // Camera section: a Deep sky | Planetary switch over the two sets of settings, then the busy note for both
            var mode = new SegmentedTabs();
            mode.Tabs.AddRange(new[] { "Deep sky", "Planetary" });
            var busyNote = _busyNote = new Label { Tag = "dim", AutoSize = true, Visible = false, MaximumSize = new Size(270, 0), Text = "A sequence is running and using the camera. Its frames are shown here as they arrive; Stop ends the sequence." };
            var camBox = new FormGrid(110);
            camBox.Full(mode);
            camBox.Full(cam);
            camBox.Full(vid);
            camBox.Full(busyNote);
            vid.Visible = false;
            mode.SelectedChanged += i => { cam.Visible = i == 0; vid.Visible = i == 1; };
            var camCard = new Collapsible("Camera", camBox);
            string lastVideo = null;
            rec.Click += (s, e) =>
            {
                if (!Have(Dm.Camera, "camera") || !CanUseCamera()) return;
                int rw = 0, rh = 0;
                var m = System.Text.RegularExpressions.Regex.Match((string)vRoi.SelectedItem, @"(\d+) × (\d+)");
                if (m.Success) { rw = int.Parse(m.Groups[1].Value); rh = int.Parse(m.Groups[2].Value); }
                _capture.ImageFolder = AppState.Settings.ImageFolder;
                var req = new VideoRequest
                {
                    ExposureMs = (double)vExp.Value, Bin = int.Parse((string)vBin.SelectedItem), Gain = (int)vGain.Value, Offset = (int)vOffset.Value,
                    RoiWidth = rw, RoiHeight = rh, MaxFrames = (int)vFrames.Value, MaxSeconds = (double)vSecs.Value,
                    Filter = string.IsNullOrWhiteSpace(vFilter.Text) ? null : vFilter.Text, Target = vTarget.Text
                };
                vidStatus.Text = "Starting…";
                bool firstFrame = true;
                RunCameraJob("Recording video", t =>
                {
                    var r = _capture.RecordVideo(req, (img, p) =>
                    {
                        Tasks.Report(t, Math.Min(1, Math.Max(p.Frames / (double)req.MaxFrames, p.Elapsed.TotalSeconds / req.MaxSeconds)), string.Format("{0} frames · {1:0.0} fps", p.Frames, p.Fps));
                        MainForm.Instance.Post(() =>
                        {
                            _viewer.SetImage(img, firstFrame); firstFrame = false;
                            vidStatus.Text = string.Format("Recording {0}×{1} · {2} frames · {3:0.0} fps · {4:0} s", p.Width, p.Height, p.Frames, p.Fps, p.Elapsed.TotalSeconds);
                        });
                    }, t.Cts.Token).GetAwaiter().GetResult();
                    // posted from here rather than onDone, which does not run after Stop
                    MainForm.Instance.Post(() =>
                    {
                        if (r.Path == null) { vidStatus.Text = "Stopped before the first frame; nothing saved."; return; }
                        lastVideo = r.Path;
                        openPl.Enabled = true;
                        vidStatus.Text = string.Format("{0} frames {1}×{2} · {3:0.0} fps · {4:0.0} s{5}\r\nSaved {6}", r.Frames, r.Width, r.Height, r.Fps, r.Elapsed.TotalSeconds, r.Stopped ? " (stopped)" : "", System.IO.Path.GetFileName(r.Path));
                        MainForm.Instance.Toast(string.Format("Video saved: {0} frames at {1:0.0} fps", r.Frames, r.Fps));
                    });
                });
            };
            _cameraButtons.Add(Tuple.Create((Control)rec, false));
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
                if (!Have(Dm.Camera, "camera")) return;
                if (!CanUseCamera(true)) return;
                var previous = ManualCapturing ? _manualTask : null; // switching between Loop and Start: let its exposure end first
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
                _manualTask = Tasks.Run(previewLoop ? "Preview loop" : "Capturing " + n + " frame(s)", t =>
                {
                    while (previous != null && !previous.Done && !ct.IsCancellationRequested) Thread.Sleep(50);
                    for (int i = 0; i < n && !ct.IsCancellationRequested; i++)
                    {
                        Tasks.Report(t, previewLoop ? double.NaN : (double)i / n, (i + 1) + "/" + (previewLoop ? "∞" : n.ToString()));
                        _capture.Expose(req, ct).GetAwaiter().GetResult(); // shown through ImageCaptured below
                    }
                });
            };
            start.Click += (s, e) => run(false);
            loop.Click += (s, e) => run(true);
            _cameraButtons.Add(Tuple.Create((Control)start, true));
            _cameraButtons.Add(Tuple.Create((Control)loop, true));
            // every frame the camera delivers (manual, sequence, autofocus) is shown here
            _capture.ImageCaptured += res =>
            {
                bool fromSequence = SequenceActive; // decided now: the sequence may have finished by the time the UI updates
                MainForm.Instance.Post(() =>
                {
                    _viewer.SetImage(res.Image, _viewer.Image == null);
                    camStatus.Text = string.Format("{0}{1} stars · HFR {2:0.00} px · FWHM {3:0.00} px · median {4:0}{5}", fromSequence ? "Sequence · " : "", res.Stars, res.Hfr, res.Fwhm, res.Median * 65535, res.Path != null ? "\r\nSaved " + System.IO.Path.GetFileName(res.Path) : "");
                });
            };
            Tasks.Changed += SyncCameraButtons;
            _sequencer.Changed += () => MainForm.Instance.Post(SyncCameraButtons);
            stop.Click += (s, e) => { _captureCts?.Cancel(); _cameraJob?.Cts.Cancel(); _sequencer.Stop(); };

            // mount pad
            var mount = new FormGrid(90);
            var pos = mount.Row("Position", new Label { Tag = "dim", Height = 48 });
            var pad = new TableLayoutPanel { ColumnCount = 3, RowCount = 3, Width = 150, Height = 120 };
            for (int i = 0; i < 3; i++) { pad.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33)); pad.RowStyles.Add(new RowStyle(SizeType.Percent, 33)); }
            var rate = Ui.Combo("Guide 0.5×", "Centre 8×", "Find 64×", "Slew max");
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
            var stopBtn = new FlatButton { Icon = Icons.Stop, Dock = DockStyle.Fill, Kind = ButtonKind.Danger, Margin = new Padding(2) };
            stopBtn.Click += (s, e) => { try { Dm.Telescope?.AbortSlew(); } catch { } };
            pad.Controls.Add(stopBtn, 1, 1);
            pad.Controls.Add(padBtn(Icons.Right, 0, -1), 2, 1);
            pad.Controls.Add(padBtn(Icons.Down, 1, -1), 1, 2);
            mount.Row("Rate", rate);
            mount.Full(pad);
            var gotoBox = mount.Row("Go to", new TextBox());
            var mb = new FlowLayoutPanel { Height = 76 };
            Func<string, string, Action, FlatButton> mbtn = (t, i, a) => { var b = new FlatButton(t, i, (s, e) => { try { a(); } catch (Exception ex) { MainForm.Instance.Toast(ex.Message, Icons.Warning, Theme.Current.Danger); } }); b.AutoSizeToText(); mb.Controls.Add(b); return b; };
            mbtn("Slew", Icons.Target, () => GotoObject(gotoBox.Text, false));
            _cameraButtons.Add(Tuple.Create((Control)mbtn("Center", Icons.Solve, () => GotoObject(gotoBox.Text, true)), false)); // plate solves
            mbtn("Park", Icons.Lock, () => { var tel = Need(Dm.Telescope, "telescope"); Tasks.Run("Parking", t => tel.Park()); });
            mbtn("Unpark", Icons.Play, () => Need(Dm.Telescope, "telescope").Unpark());
            var track = new FlatButton("Tracking", Icons.Clock) { Toggle = true, Checked = true }; track.AutoSizeToText();
            track.CheckedChanged += (s, e) => { try { if (Dm.Telescope != null) Dm.Telescope.Tracking = track.Checked; } catch { } };
            mb.Controls.Add(track);
            mount.Full(mb);
            right.Controls.Add(new Collapsible("Focuser", BuildFocuser()));
            right.Controls.Add(new Collapsible("Mount", mount));

            Poll += () =>
            {
                var tel = Dm.Telescope;
                if (tel != null && tel.Connected)
                {
                    try
                    {
                        pos.Text = string.Format("α {0}  δ {1}\r\nAlt {2:0.0}°  Az {3:0.0}°  {4}{5}", AstroMath.FormatHms(tel.RightAscension, 0), AstroMath.FormatDms(tel.Declination, 0), tel.Altitude, tel.Azimuth, tel.SideOfPier, tel.Slewing ? " · slewing" : tel.AtPark ? " · parked" : "");
                    }
                    catch { }
                }
                else pos.Text = "No telescope connected";
                var c = Dm.Camera;
                if (c != null && c.Connected)
                {
                    try { coolStatus.Text = string.Format("{0:0.0} °C · power {1:0}%", c.CcdTemperature, c.CoolerPower); } catch { }
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
            Need(Dm.Telescope, "telescope");
            double raDeg, decDeg;
            var o = CatalogManager.Instance.Find(q) ?? CatalogManager.Instance.Search(q, 1).FirstOrDefault();
            Core.Ephemeris.Body b;
            if (o != null) { raDeg = o.RaHours * 15; decDeg = o.DecDeg; }
            else if (Enum.TryParse(q, true, out b)) { var p = SkyView.PlanetObject(b); raDeg = p.RaDeg; decDeg = p.DecDeg; }
            else
            {
                var parts = q.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2) { raDeg = AstroMath.ParseRaHours(string.Join(" ", parts.Take(parts.Length / 2))) * 15; decDeg = AstroMath.ParseSexagesimal(string.Join(" ", parts.Skip(parts.Length / 2))); }
                else throw new ArgumentException("Unknown object: " + q);
            }
            if (center)
            {
                Need(Dm.Camera, "camera"); // centring plate-solves its own frames
                if (!CanUseCamera()) return;
                var c = new Centering { Capture = _capture };
                c.Status += s => MainForm.Instance.SetStatus(s);
                RunCameraJob("Centering " + q, t => c.Center(raDeg, decDeg, t.Cts.Token).GetAwaiter().GetResult(), () => MainForm.Instance.Toast("Centered on " + q));
            }
            else
            {
                var app = new SkyContext(AppState.Site, AstroTime.Now).IcrsToApparent(raDeg * AstroMath.Deg2Rad, decDeg * AstroMath.Deg2Rad);
                Tasks.Run("Slewing to " + q, t => Centering.Slew(Dm.Telescope, app.RaHours, app.DecDeg, t.Cts.Token).GetAwaiter().GetResult());
            }
        }

        private Control BuildFocuser()
        {
            var g = new FormGrid(90);
            var pos = g.Row("Position", new Label { Tag = "dim" });
            var step = g.Row("Step", Ui.Num(100, 1, 10000));
            var mv = new FlowLayoutPanel { Height = 38 };
            var inn = new FlatButton("In", Icons.Left, (s, e) => { if (Dm.Focuser != null) Dm.Focuser.Move(Dm.Focuser.Position - (int)step.Value); }); inn.AutoSizeToText();
            var outb = new FlatButton("Out", Icons.Right, (s, e) => { if (Dm.Focuser != null) Dm.Focuser.Move(Dm.Focuser.Position + (int)step.Value); }); outb.AutoSizeToText();
            mv.Controls.Add(inn); mv.Controls.Add(outb);
            g.Full(mv);
            var afExp = g.Row("AF exposure", Ui.Num(3, 0, 120, 1));
            var afStep = g.Row("AF step", Ui.Num(100, 1, 5000));
            var plot = new PlotControl { Height = 170, Title = "V-curve", XLabel = "Position", YLabel = "HFR" };
            var af = new FlatButton("Run autofocus", Icons.Target) { Kind = ButtonKind.Primary }; af.AutoSizeToText();
            af.Click += (s, e) =>
            {
                if (Dm.Focuser == null || Dm.Camera == null) { MainForm.Instance.Toast("Focuser and camera required", Icons.Warning); return; }
                if (!CanUseCamera()) return;
                plot.Clear();
                var pts = plot.AddSeries("HFR", SeriesStyle.Scatter);
                var a = new Autofocus { Capture = _capture, Exposure = (double)afExp.Value, StepSize = (int)afStep.Value };
                a.PointMeasured += p => MainForm.Instance.Post(() => { pts.Add(p.Position, p.Hfr); plot.Invalidate(); });
                a.Status += st => MainForm.Instance.SetStatus(st);
                RunCameraJob("Autofocus", t =>
                {
                    var r = a.Run(t.Cts.Token).GetAwaiter().GetResult();
                    MainForm.Instance.Post(() =>
                    {
                        if (r.Success)
                        {
                            var fit = plot.AddSeries("Hyperbola fit");
                            var xs = r.Points.Select(p => (double)p.Position).ToList();
                            for (double x = xs.Min(); x <= xs.Max(); x += (xs.Max() - xs.Min()) / 100) fit.Add(x, r.A * Math.Sqrt(1 + Math.Pow((x - r.C) / r.B, 2)));
                            plot.Markers.Add(new PlotMarker { X = r.BestPosition, Label = "best " + r.BestPosition });
                        }
                        plot.Invalidate();
                        MainForm.Instance.Toast(r.Message);
                    });
                });
            };
            g.Full(af);
            _cameraButtons.Add(Tuple.Create((Control)af, false));
            g.Full(plot);
            Poll += () => { var f = Dm.Focuser; pos.Text = f != null && f.Connected ? string.Format("{0}{1}  {2}", f.Position, f.IsMoving ? " (moving)" : "", double.IsNaN(f.Temperature) ? "" : f.Temperature.ToString("0.0") + " °C") : "No focuser"; };
            return g;
        }

        // ================================================================== guiding

        private Control BuildGuiding()
        {
            var panel = new Panel { Dock = DockStyle.Fill };
            var plot = new PlotControl { Dock = DockStyle.Fill, Title = "Guiding error", YLabel = "arcsec", XLabel = "step" };
            var ra = plot.AddSeries("RA"); var dec = plot.AddSeries("Dec");
            var stats = new Label { Dock = DockStyle.Top, Height = 28, Font = Theme.UiFontBold };
            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 44 };
            Func<string, string, Action, FlatButton> btn = (t, i, a) => { var b = new FlatButton(t, i, (s, e) => { try { a(); } catch (Exception ex) { MainForm.Instance.Toast(ex.Message, Icons.Warning); } }); b.AutoSizeToText(); bar.Controls.Add(b); return b; };
            btn("Connect PHD2", Icons.Connect, () => Tasks.Run("Connect PHD2", t => Dm.Connect(new DeviceInfo { Kind = DeviceKind.Guider, Protocol = DeviceProtocol.Phd2, Name = "PHD2", Address = "localhost:4400" }),
                () => MainForm.Instance.Toast("Connected to PHD2"),
                ex => MainForm.Instance.Toast("Could not reach PHD2 on localhost:4400. " + Phd2Hint, Icons.Warning, Theme.Current.Danger)));
            Func<IGuider> guider = () => Need(Dm.Guider, "guider", Phd2Hint);
            btn("Start guiding", Icons.Play, () => { var g = guider(); Tasks.Run("Start guiding", t => g.StartGuiding(false, t.Cts.Token).GetAwaiter().GetResult()); }).Kind = ButtonKind.Primary;
            btn("Calibrate & guide", Icons.Target, () => { var g = guider(); Tasks.Run("Calibrate", t => g.StartGuiding(true, t.Cts.Token).GetAwaiter().GetResult()); });
            btn("Dither", Icons.Grid, () => { var g = guider(); Tasks.Run("Dither", t => g.Dither(5, false, 1.5, 10, 120, t.Cts.Token).GetAwaiter().GetResult()); });
            btn("Stop", Icons.Stop, () => guider().StopGuiding()).Kind = ButtonKind.Danger;
            btn("Clear graph", Icons.Remove, () => { ra.X.Clear(); ra.Y.Clear(); dec.X.Clear(); dec.Y.Clear(); plot.Invalidate(); });
            int n = 0;
            IGuider hooked = null;
            Poll += () =>
            {
                var g = Dm.Guider;
                if (g != hooked && g != null)
                {
                    hooked = g;
                    g.Step += s => MainForm.Instance.Post(() =>
                    {
                        ra.Add(n, s.RaArcsec); dec.Add(n, s.DecArcsec); n++;
                        if (ra.X.Count > 400) { ra.X.RemoveAt(0); ra.Y.RemoveAt(0); dec.X.RemoveAt(0); dec.Y.RemoveAt(0); }
                        plot.Invalidate();
                    });
                }
                stats.Text = g == null ? "No guider connected — start PHD2 and enable its server (Tools → Enable Server)" : string.Format("{0} · RMS RA {1:0.00}\"  Dec {2:0.00}\"  total {3:0.00}\"", g.State, g.RmsRaArcsec, g.RmsDecArcsec, g.RmsTotalArcsec);
            };
            panel.Controls.Add(plot);
            panel.Controls.Add(stats);
            panel.Controls.Add(bar);
            return panel;
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
            var add = new FlatButton("Add", Icons.Add); add.AutoSizeToText();
            var addMenu = new ContextMenuStrip { Renderer = new DarkToolStripRenderer() };
            foreach (var t in Sequencer.ItemTypes)
            {
                var type = t;
                addMenu.Items.Add(GridAdapter.Words(t.Name), null, (s, e) => AddItem((SequenceItem)Activator.CreateInstance(type)));
            }
            add.Click += (s, e) => addMenu.Show(add, new Point(0, add.Height));
            Func<string, string, Action, FlatButton> btn = (t, i, a) => { var b = new FlatButton(t, i, (s, e) => a()); b.AutoSizeToText(); bar.Controls.Add(b); return b; };
            bar.Controls.Add(add);
            btn("Remove", Icons.Remove, RemoveItem);
            btn("Up", Icons.Up, () => MoveItem(-1));
            btn("Down", Icons.Down, () => MoveItem(1));
            btn("Template…", Icons.Star, () =>
            {
                var sel = AppState.Selection;
                var name = sel != null ? sel.Name : "M 31";
                double ra = sel != null ? sel.RaDeg : 10.684, dec = sel != null ? sel.DecDeg : 41.269;
                _sequencer.Root = Sequencer.Template(name, ra, dec);
                RefreshTree();
            });
            btn("Open…", Icons.Open, () => { using (var d = new OpenFileDialog { Filter = "Sequence|*.json" }) if (d.ShowDialog() == DialogResult.OK) { _sequencer.Load(d.FileName); RefreshTree(); } });
            btn("Save…", Icons.Save, () => { using (var d = new SaveFileDialog { Filter = "Sequence|*.json", FileName = "sequence.json" }) if (d.ShowDialog() == DialogResult.OK) _sequencer.Save(d.FileName); });
            var run = new FlatButton("Run sequence", Icons.Play) { Kind = ButtonKind.Primary }; run.AutoSizeToText();
            run.Click += (s, e) =>
            {
                if (SequenceActive || !CanUseCamera()) return;
                var missing = _sequencer.MissingDevices();
                if (missing.Count > 0) { MainForm.Instance.Toast(Sequencer.DescribeMissing(missing) + ", or click Connect simulated observatory.", Icons.Warning); return; }
                _capture.ImageFolder = AppState.Settings.ImageFolder;
                _sequenceTask = Tasks.Run("Sequence", t => { using (t.Cts.Token.Register(_sequencer.Stop)) _sequencer.Start(_capture).GetAwaiter().GetResult(); });
            };
            bar.Controls.Add(run);
            _cameraButtons.Add(Tuple.Create((Control)run, false));
            btn("Stop", Icons.Stop, () => _sequencer.Stop()).Kind = ButtonKind.Danger;
            _sequencer.Log += s => MainForm.Instance.Post(() => log.AppendText(DateTime.Now.ToString("HH:mm:ss ") + s + "\r\n"));
            _sequencer.Changed += () => MainForm.Instance.Post(UpdateTreeStatus);
            _seqTree.AfterSelect += (s, e) => _seqProps.SelectedObject = e.Node?.Tag != null ? new GridAdapter(e.Node.Tag, "Instruction") : null;
            _seqProps.PropertyValueChanged += (s, e) => UpdateTreeStatus();
            var split = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 560 };
            split.Panel1.Controls.Add(_seqTree);
            var rightCard = new Card("Properties") { Dock = DockStyle.Fill };
            rightCard.Controls.Add(_seqProps);
            var trigCard = new Card("Triggers (checked between exposures)") { Dock = DockStyle.Bottom, Height = 160 };
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
            return st + it.Title + (string.IsNullOrEmpty(it.Summary) ? "" : "   " + it.Summary) + (it.Enabled ? "" : "  (disabled)");
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
            container.Items.Insert(Math.Max(0, Math.Min(idx, container.Items.Count)), item);
            RefreshTree();
        }

        private void RemoveItem()
        {
            var sel = _seqTree.SelectedNode;
            var parent = sel?.Parent?.Tag as SequenceContainer;
            if (parent == null) return;
            parent.Items.Remove((SequenceItem)sel.Tag);
            RefreshTree();
        }

        private void MoveItem(int dir)
        {
            var sel = _seqTree.SelectedNode;
            var parent = sel?.Parent?.Tag as SequenceContainer;
            if (parent == null) return;
            var it = (SequenceItem)sel.Tag;
            int i = parent.Items.IndexOf(it), j = i + dir;
            if (j < 0 || j >= parent.Items.Count) return;
            parent.Items.RemoveAt(i); parent.Items.Insert(j, it);
            RefreshTree();
        }

        // ================================================================== polar alignment

        private Control BuildPolar()
        {
            var card = new Card("Three-point polar alignment") { Dock = DockStyle.Fill };
            var g = new FormGrid(160);
            var step = g.Row("RA step (°)", Ui.Num(25, 5, 60));
            var exp = g.Row("Exposure (s)", Ui.Num(4, 0, 60, 1));
            var res = new Label { Height = 120, Font = Theme.HeaderFont, AutoSize = false };
            var go = new FlatButton("Start polar alignment", Icons.Target) { Kind = ButtonKind.Primary }; go.AutoSizeToText();
            go.Click += (s, e) =>
            {
                if (!Have(Dm.Telescope, "telescope") || !Have(Dm.Camera, "camera") || !CanUseCamera()) return;
                var pa = new PolarAlignment { Capture = _capture, StepDeg = (double)step.Value, Exposure = (double)exp.Value };
                pa.Status += st => MainForm.Instance.SetStatus(st);
                RunCameraJob("Polar alignment", t =>
                {
                    var r = pa.Run(t.Cts.Token).GetAwaiter().GetResult();
                    MainForm.Instance.Post(() => res.Text = string.Format("Altitude error {0:+0.0;-0.0}'   Azimuth error {1:+0.0;-0.0}'\r\nTotal {2:0.0}'\r\n{3}", r.AltErrorArcmin, r.AzErrorArcmin, r.TotalErrorArcmin, r.Advice));
                });
            };
            g.Full(Ui.Hint("Point the mount near the celestial equator/meridian with clear sky to the west or east. Sidralune rotates the RA axis three times, plate-solves each frame, finds the mount's rotation axis and reports how far it is from the refracted celestial pole. Works without a view of Polaris."));
            g.Full(go);
            _cameraButtons.Add(Tuple.Create((Control)go, false));
            g.Full(res);
            card.Controls.Add(g);
            return card;
        }

        // ================================================================== dome & weather

        private Control BuildDome()
        {
            var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            var dome = new Card("Dome / roof") { Dock = DockStyle.Fill };
            var dg = new FormGrid(120);
            var st = dg.Row("State", new Label { Tag = "dim" });
            var az = dg.Row("Azimuth", Ui.Num(0, 0, 360, 1));
            var bar = new FlowLayoutPanel { Height = 80 };
            Action<string, string, Action> btn = (t, i, a) => { var b = new FlatButton(t, i, (s, e) => { try { a(); } catch (Exception ex) { MainForm.Instance.Toast(ex.Message, Icons.Warning); } }); b.AutoSizeToText(); bar.Controls.Add(b); };
            btn("Open", Icons.Up, () => Need(Dm.Dome, "dome").OpenShutter());
            btn("Close", Icons.Down, () => Need(Dm.Dome, "dome").CloseShutter());
            btn("Slew", Icons.Target, () => Need(Dm.Dome, "dome").SlewToAzimuth((double)az.Value));
            btn("Park", Icons.Lock, () => Need(Dm.Dome, "dome").Park());
            var slave = new FlatButton("Follow telescope", Icons.Refresh) { Toggle = true }; slave.AutoSizeToText();
            bar.Controls.Add(slave);
            dg.Full(bar);
            dome.Controls.Add(dg);
            var weather = new Card("Weather & safety") { Dock = DockStyle.Fill };
            var wl = new Label { Dock = DockStyle.Fill, Font = Theme.UiFont };
            weather.Controls.Add(wl);
            Poll += () =>
            {
                var d = Dm.Dome;
                st.Text = d != null && d.Connected ? string.Format("Shutter {0} · azimuth {1:0.0}°{2}", d.Shutter, d.Azimuth, d.Slewing ? " · moving" : "") : "No dome connected";
                if (d != null && slave.Checked && Dm.Telescope != null)
                    try { double a = Dm.Telescope.Azimuth; if (Math.Abs(AstroMath.NormalizeDeg180(a - d.Azimuth)) > 3 && !d.Slewing) d.SlewToAzimuth(a); } catch { }
                var w = Dm.Weather; var s = Dm.Safety;
                wl.Text = (w != null ? string.Format("Temperature {0:0.0} °C\r\nHumidity {1:0}%  ·  dew point {2:0.0} °C\r\nCloud cover {3:0}%\r\nWind {4:0.0} m/s\r\nRain rate {5:0.0} mm/h\r\nSky quality {6:0.00} mag/arcsec²\r\nSky temperature {7:0} °C\r\n",
                    w.Temperature, w.Humidity, w.DewPoint, w.CloudCover, w.WindSpeed, w.RainRate, w.SkyQuality, w.SkyTemperature) : "No weather station connected\r\n")
                    + "\r\nSafety: " + (s == null ? "no monitor" : s.IsSafe ? "SAFE" : "UNSAFE");
                wl.ForeColor = s != null && !s.IsSafe ? Theme.Current.Danger : Theme.Current.Text;
            };
            panel.Controls.Add(dome, 0, 0);
            panel.Controls.Add(weather, 1, 0);
            return panel;
        }

        public IEnumerable<AppCommand> GetCommands()
        {
            yield return new AppCommand { Title = "Observatory: open equipment", Run = () => { MainForm.Instance.Navigate("observatory"); _tabs.Select(0); } };
            yield return new AppCommand { Title = "Observatory: sequencer", Run = () => { MainForm.Instance.Navigate("observatory"); _tabs.Select(3); } };
            yield return new AppCommand { Title = "Observatory: polar alignment", Run = () => { MainForm.Instance.Navigate("observatory"); _tabs.Select(4); } };
            yield return new AppCommand { Title = "Observatory: abort all", Icon = Icons.Stop, Run = () => { _captureCts?.Cancel(); _cameraJob?.Cts.Cancel(); _sequencer.Stop(); try { Dm.Telescope?.AbortSlew(); } catch { } } };
        }
    }
}
