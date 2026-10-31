using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Sidralune.Core.Catalogs;
using Sidralune.Core.Ephemeris;
using Sidralune.Core.Mathematics;
using Sidralune.Core.Util;
using Sidralune.Devices;
using Sidralune.UI;
using Sidralune.Views;

namespace Sidralune
{
    /// <summary>A command available in the command palette.</summary>
    public class AppCommand
    {
        public string Title, Category, Shortcut, Icon;
        public Action Run;
        public override string ToString() => Title;
    }

    public class MainForm : Form, IThemed
    {
        public static MainForm Instance { get; private set; }
        private readonly NavRail _rail = new NavRail { Dock = DockStyle.Left };
        private readonly Panel _content = new Panel { Dock = DockStyle.Fill };
        private readonly Panel _top = new Panel { Dock = DockStyle.Top, Height = 48 };
        private readonly Panel _status = new Panel { Dock = DockStyle.Bottom, Height = 28 };
        private readonly Label _title = new Label { AutoSize = true, Font = Theme.TitleFont, Text = "Sidralune" };
        private readonly Label _viewTitle = new Label { AutoSize = true, Font = Theme.HeaderFont };
        private readonly FlatButton _search = new FlatButton { Text = "Search objects and commands…   Ctrl+K", Icon = Icons.Search, Width = 360 };
        private readonly Label _clock = new Label { AutoSize = true, Font = Theme.UiFont };
        private readonly FlatButton _night = new FlatButton { Icon = Icons.Night, Toggle = true, Width = 34, Kind = ButtonKind.Subtle, ToolTipText = "Night vision (red) mode  Ctrl+Shift+N" };
        private readonly FlatButton _live = new FlatButton { Text = "Live", Icon = Icons.Clock, Kind = ButtonKind.Subtle, ToolTipText = "Return to the current time" };
        private readonly FlowLayoutPanel _statusLeft = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = new Padding(4, 3, 0, 0) };
        private readonly StatusPill _statusMsg = new StatusPill { ShowDot = false, Dock = DockStyle.Right, Width = 0 };
        private FlatButton _logBtn;
        private readonly TextBox _log = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Bottom, Height = 160, Visible = false, Font = Theme.MonoFont };
        private readonly Timer _timer = new Timer { Interval = 1000 };
        private ToastHost _toasts;
        public readonly List<AppCommand> Commands = new List<AppCommand>();
        private readonly Dictionary<DeviceKind, StatusPill> _devicePills = new Dictionary<DeviceKind, StatusPill>();
        private readonly StatusPill _taskPill = new StatusPill();
        private readonly StatusPill _dataPill = new StatusPill();

        public MainForm()
        {
            Instance = this;
            Text = "Sidralune — Astronomy Suite";
            Font = Theme.UiFont;
            MinimumSize = new Size(1100, 700);
            Size = new Size(1500, 950);
            StartPosition = FormStartPosition.CenterScreen;
            KeyPreview = true;
            try { Icon = new Icon(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "Sidralune.ico")); } catch { try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { } }
            Tasks.UiContext = this;
            _toasts = new ToastHost(this);

            BuildTopBar();
            BuildStatusBar();
            Controls.Add(_content);
            Controls.Add(_log);
            Controls.Add(_rail);
            Controls.Add(_status);
            Controls.Add(_top);

            RegisterViews();
            _rail.SelectedChanged += ShowView;
            RegisterCommands();

            Theme.Changed += (s, e) => { _syncingNight = true; _night.Checked = Theme.Current.IsNight; _syncingNight = false; ApplyTheme(Theme.Current); };
            Log.Logged += e => Post(() => AppendLog(e));
            Tasks.Changed += UpdateTaskPill;
            // one update per burst of device changes (Disconnect all raises one per device)
            DeviceManager.Instance.DevicesChanged += (s, e) => { if (System.Threading.Interlocked.Exchange(ref _pillsPending, 1) == 0) Post(UpdateDevicePills); };
            CatalogManager.Instance.CatalogsChanged += (s, e) => Post(UpdateDataPill);
            EphemerisManager.SourceChanged += (s, e) => Post(UpdateDataPill);
            _timer.Tick += (s, e) => Tick();
            _timer.Start();
            Resize += (s, e) => _toasts.Relayout();
            FormClosing += (s, e) => { AppState.Settings.LastView = _rail.Selected?.Key ?? "home"; AppState.Save(); try { DeviceManager.Instance.DisconnectAll(); } catch { } };
            Theme.SetNightMode(AppState.Settings.NightMode);
            _night.Checked = AppState.Settings.NightMode;
            ApplyTheme(Theme.Current);
            UpdateDevicePills();
            UpdateDataPill();
            Tick();
        }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Theme.DarkTitleBar(this); }

        private bool _syncingNight;

        /// <summary>
        /// Switch night (red) mode and remember it. The top-bar button and the Settings check box both call this and
        /// follow Theme.Changed, so they stay in step whichever one (or Ctrl+Shift+N) was used.
        /// </summary>
        public void SetNightMode(bool on)
        {
            if (AppState.Settings.NightMode != on) { AppState.Settings.NightMode = on; AppState.Save(); }
            if (Theme.Current.IsNight != on) Theme.SetNightMode(on);
        }

        private void BuildTopBar()
        {
            var logo = new Label { Text = "", Font = Theme.IconFontLarge, AutoSize = true, Location = new Point(28, 13) };
            _title.Location = new Point(56, 11);
            _viewTitle.Location = new Point(170, 15);
            _top.Controls.Add(logo); _top.Controls.Add(_title); _top.Controls.Add(_viewTitle);
            _search.Height = 32;
            _search.Click += (s, e) => ShowPalette();
            _night.CheckedChanged += (s, e) => { if (!_syncingNight) SetNightMode(_night.Checked); };
            _live.Height = 32; _live.AutoSizeToText();
            _live.Click += (s, e) => AppState.GoLive();
            _night.Height = 32;
            _top.Controls.Add(_search); _top.Controls.Add(_clock); _top.Controls.Add(_night); _top.Controls.Add(_live);
            _top.Resize += (s, e) => LayoutTop();
            _top.Paint += (s, e) => { using (var pen = new Pen(Theme.Current.Border)) e.Graphics.DrawLine(pen, 0, _top.Height - 1, _top.Width, _top.Height - 1); };
        }

        private void LayoutTop()
        {
            _night.Location = new Point(_top.Width - _night.Width - 12, 8);
            _live.Location = new Point(_night.Left - _live.Width - 8, 8);
            _clock.Location = new Point(_live.Left - _clock.Width - 12, 15);
            // centre the search box between the view title and the clock; in a narrow window move it left and narrow it
            int left = Math.Max(360, _viewTitle.Right + 16), right = _clock.Left - 16;
            _search.Width = Math.Max(200, Math.Min(360, right - left));
            _search.Location = new Point(Math.Max(left, Math.Min((_top.Width - _search.Width) / 2, right - _search.Width)), 8);
        }

        private void BuildStatusBar()
        {
            foreach (var k in new[] { DeviceKind.Telescope, DeviceKind.Camera, DeviceKind.Focuser, DeviceKind.FilterWheel, DeviceKind.Guider, DeviceKind.Dome })
            {
                var pill = new StatusPill { Margin = new Padding(0, 0, 4, 0) };
                pill.Set(k.ToString(), Color.Gray);
                pill.Cursor = Cursors.Hand;
                pill.Click += (s, e) => Navigate("observatory");
                _devicePills[k] = pill;
                _statusLeft.Controls.Add(pill);
            }
            _dataPill.Margin = _taskPill.Margin = new Padding(0, 0, 4, 0); // same as the device pills so all sit on one line
            _statusLeft.Controls.Add(_dataPill);
            _dataPill.Cursor = Cursors.Hand;
            _dataPill.Click += (s, e) => Navigate("settings");
            _statusLeft.Controls.Add(_taskPill);
            var logBtn = _logBtn = new FlatButton { Text = "Log", Icon = Icons.List, Kind = ButtonKind.Subtle, Height = 22, Dock = DockStyle.Right };
            logBtn.AutoSizeToText();
            logBtn.Click += (s, e) => _log.Visible = !_log.Visible;
            _status.Resize += (s, e) => LayoutStatus();
            _status.Controls.Add(_statusLeft);
            _status.Controls.Add(_statusMsg);
            _status.Controls.Add(logBtn);
            _status.Paint += (s, e) => { using (var pen = new Pen(Theme.Current.Border)) e.Graphics.DrawLine(pen, 0, 0, _status.Width, 0); };
        }

        private void RegisterViews()
        {
            Action<string, string, string, string, Func<Control>> add = (key, title, icon, group, f) => _rail.Items.Add(new NavItem { Key = key, Title = title, Icon = icon, Group = group, Factory = f, Tooltip = title });
            add("home", "Tonight", Icons.Home, "plan", () => new TonightView());
            add("sky", "Sky", Icons.Sky, "plan", () => new SkyView());
            add("planner", "Planner", Icons.Planner, "plan", () => new PlannerView());
            add("observatory", "Observatory", Icons.Observatory, "acquire", () => new ObservatoryView());
            add("stacking", "Deep sky", Icons.Stack, "process", () => new StackingView());
            add("processing", "Process", Icons.Process, "process", () => new ProcessingView());
            add("planetary", "Planetary", Icons.Planet, "process", () => new PlanetaryView());
            add("astrometry", "Astrometry", Icons.Target, "science", () => new AstrometryView());
            add("photometry", "Photometry", Icons.Photometry, "science", () => new PhotometryView());
            add("spectro", "Spectra", Icons.Spectrum, "science", () => new SpectroscopyView());
            add("solarsystem", "Solar sys.", Icons.Comet, "science", () => new SolarSystemView());
            add("radio", "Radio", Icons.Radio, "science", () => new RadioView());
            add("databases", "Databases", Icons.Database, "tools", () => new DatabaseView());
            add("scripting", "Scripts", Icons.Code, "tools", () => new ScriptingView());
            add("settings", "Settings", Icons.Settings, "tools", () => new SettingsView());
        }

        private void RegisterCommands()
        {
            foreach (var it in _rail.Items)
            {
                var item = it;
                Commands.Add(new AppCommand { Title = "Go to " + item.Title, Category = "Navigate", Icon = item.Icon, Run = () => Navigate(item.Key) });
            }
            Commands.Add(new AppCommand { Title = "Toggle night vision mode", Category = "View", Shortcut = "Ctrl+Shift+N", Icon = Icons.Night, Run = () => SetNightMode(!Theme.Current.IsNight) });
            Commands.Add(new AppCommand { Title = "Return to live time", Category = "Time", Icon = Icons.Clock, Run = AppState.GoLive });
            Commands.Add(new AppCommand { Title = "Time: +1 hour", Category = "Time", Run = () => AppState.Step(1 / 24.0) });
            Commands.Add(new AppCommand { Title = "Time: -1 hour", Category = "Time", Run = () => AppState.Step(-1 / 24.0) });
            Commands.Add(new AppCommand { Title = "Time: +1 day", Category = "Time", Run = () => AppState.Step(1) });
            Commands.Add(new AppCommand { Title = "Connect simulated observatory", Category = "Devices", Icon = Icons.Connect, Run = () => Tasks.Run("Connect simulators", t => DeviceManager.Instance.ConnectSimulators(), () => Toast("Simulated observatory connected")) });
            Commands.Add(new AppCommand { Title = "Disconnect all devices", Category = "Devices", Run = () => DeviceManager.Instance.DisconnectAll() });
            Commands.Add(new AppCommand { Title = "Download recommended data (catalogs, DE440)", Category = "Data", Icon = Icons.Download, Run = () => Navigate("settings") });
            Commands.Add(new AppCommand { Title = "Show log", Category = "View", Run = () => _log.Visible = true });
        }

        /// <summary>Run on the UI thread (synchronously if no window handle exists yet).</summary>
        public void Post(Action a)
        {
            if (IsDisposed) return;
            if (!IsHandleCreated || !InvokeRequired) { if (!IsHandleCreated || !InvokeRequired) { try { a(); } catch (Exception ex) { Log.Debug("UI", ex.Message); } } return; }
            try { BeginInvoke(a); } catch (InvalidOperationException) { }
        }

        public void Navigate(string key) => _rail.Select(key);

        public T GetView<T>(string key) where T : Control
        {
            var it = _rail.Items.FirstOrDefault(i => i.Key == key);
            if (it == null) return null;
            if (it.Instance == null) { it.Instance = it.Factory(); it.Instance.Dock = DockStyle.Fill; it.Instance.Visible = false; _content.Controls.Add(it.Instance); Theme.Apply(it.Instance); }
            return it.Instance as T;
        }

        private void ShowView(NavItem item)
        {
            Cursor = Cursors.WaitCursor;
            try
            {
                if (item.Instance == null)
                {
                    item.Instance = item.Factory();
                    item.Instance.Dock = DockStyle.Fill;
                    _content.Controls.Add(item.Instance);
                    Theme.Apply(item.Instance);
                    var cmds = item.Instance as ICommandSource;
                    if (cmds != null) foreach (var c in cmds.GetCommands()) { c.Category = c.Category ?? item.Title; Commands.Add(c); }
                }
                foreach (Control c in _content.Controls) c.Visible = c == item.Instance;
                item.Instance.BringToFront();
                _viewTitle.Text = item.Title == "Tonight" ? "Tonight" : item.Title;
                (item.Instance as IActivatable)?.Activated();
            }
            catch (Exception ex) { Log.Error("UI", ex); Toast("Could not open " + item.Title + ": " + ex.Message, Icons.Warning, Theme.Current.Danger); }
            finally { Cursor = Cursors.Default; }
        }

        public void Toast(string text, string icon = Icons.Info, Color? accent = null) => _toasts.Show(text, icon, 4500, accent);
        public void SetStatus(string text) { if (InvokeRequired) BeginInvoke(new Action(() => SetStatus(text))); else { _statusMsg.Set(text, Color.Empty); LayoutStatus(); } }

        /// <summary>
        /// Fit the status bar to its width: everything at full length when there is room; otherwise the data and task
        /// items shorten with an ellipsis first and the message takes what is left (full texts are shown as tooltips).
        /// </summary>
        private void LayoutStatus()
        {
            if (_logBtn == null) return;
            _dataPill.MaxTextWidth = _taskPill.MaxTextWidth = _statusMsg.MaxTextWidth = int.MaxValue;
            int avail = _status.ClientSize.Width - _logBtn.Width - 8;
            int pills = _statusLeft.Padding.Horizontal;
            foreach (Control c in _statusLeft.Controls) pills += (c is StatusPill ? ((StatusPill)c).NaturalWidth : c.Width) + c.Margin.Horizontal;
            int msg = string.IsNullOrEmpty(_statusMsg.Text) ? 0 : _statusMsg.NaturalWidth + 8;
            int over = pills + msg - avail;
            foreach (var pill in new[] { _dataPill, _taskPill })
            {
                if (over <= 0) break;
                int text = pill.TextWidth, min = 90;
                int cut = Math.Min(over, Math.Max(0, text - min));
                if (cut > 0) { pill.MaxTextWidth = text - cut; over -= cut; }
            }
            int width = Math.Max(0, msg - Math.Max(0, over));
            if (width < msg) _statusMsg.MaxTextWidth = Math.Max(0, width - 20);
            _statusMsg.Width = width;
        }

        private void Tick()
        {
            var t = AppState.Now;
            var local = AppState.Site.ToLocal(t.ToDateTimeUtc());
            _clock.Text = string.Format("{0}   {1:ddd yyyy-MM-dd  HH:mm:ss}   LST {2}", AppState.Site.Name, local, AstroMath.FormatHms(t.LocalSiderealHours(AppState.Site.LongitudeDeg), 0, true));
            _clock.ForeColor = AppState.IsLive ? Theme.Current.TextDim : Theme.Current.Warning;
            _live.Visible = !AppState.IsLive;
            LayoutTop();
            if (DateTime.Now.Second % 3 == 0) UpdateDevicePills();
        }

        private int _pillsPending;

        private void UpdateDevicePills()
        {
            System.Threading.Interlocked.Exchange(ref _pillsPending, 0);
            _statusLeft.SuspendLayout();
            try { UpdateDevicePillsCore(); }
            finally { _statusLeft.ResumeLayout(true); }
            LayoutStatus();
        }

        private void UpdateDevicePillsCore()
        {
            var dm = DeviceManager.Instance;
            foreach (var kv in _devicePills)
            {
                var d = dm.Get(kv.Key);
                string state = d == null ? "" : (d.Connected ? "" : " (lost)");
                string extra = "";
                try
                {
                    if (kv.Key == DeviceKind.Telescope && dm.Telescope != null && dm.Telescope.Connected) extra = dm.Telescope.Slewing ? " · slewing" : dm.Telescope.AtPark ? " · parked" : dm.Telescope.Tracking ? " · tracking" : "";
                    if (kv.Key == DeviceKind.Camera && dm.Camera != null && dm.Camera.Connected && !double.IsNaN(dm.Camera.CcdTemperature)) extra = string.Format(" · {0:0.0}°C", dm.Camera.CcdTemperature);
                    if (kv.Key == DeviceKind.Guider && dm.Guider != null) extra = " · " + dm.Guider.State;
                }
                catch { }
                kv.Value.Set(kv.Key + state + extra, d == null ? Theme.Current.TextFaint : (d.Connected ? Theme.Current.Success : Theme.Current.Danger));
            }
        }

        private void UpdateTaskPill()
        {
            List<BackgroundTask> tasks;
            lock (Tasks.Running) tasks = Tasks.Running.ToList();
            if (tasks.Count == 0) { _taskPill.Set("Idle", Theme.Current.TextFaint); LayoutStatus(); return; }
            var t = tasks[0];
            string p = double.IsNaN(t.Progress) ? "" : string.Format(" {0:0}%", t.Progress * 100);
            _taskPill.Set(t.Name + p + (tasks.Count > 1 ? " (+" + (tasks.Count - 1) + ")" : "") + (string.IsNullOrEmpty(t.Status) ? "" : " — " + t.Status), Theme.Current.Accent);
            LayoutStatus();
        }

        private void UpdateDataPill()
        {
            bool hi = EphemerisManager.HasHighPrecision;
            var cm = CatalogManager.Instance;
            _dataPill.Set((hi ? "DE440" : "Analytic ephemeris") + " · " + cm.StarSource + " · " + cm.DeepSkySource, hi && !cm.StarSource.StartsWith("built") ? Theme.Current.Success : Theme.Current.Warning);
            LayoutStatus();
        }

        private void AppendLog(Log.Entry e)
        {
            if (_log.TextLength > 200000) _log.Text = _log.Text.Substring(100000);
            _log.AppendText(string.Format("{0:HH:mm:ss} [{1}] {2}: {3}\r\n", e.Time, e.Level, e.Source, e.Message));
            if (e.Level >= Log.Level.Warning) SetStatus(e.Source + ": " + e.Message);
        }

        public void ApplyTheme(Palette p)
        {
            BackColor = p.Background;
            ForeColor = p.Text;
            _top.BackColor = p.Background;
            _status.BackColor = p.Surface;
            _statusLeft.BackColor = p.Surface;
            _title.ForeColor = p.Text;
            _viewTitle.ForeColor = p.TextDim;
            foreach (Control c in _top.Controls) if (c is Label && c != _clock && c != _viewTitle) c.ForeColor = c == _title ? p.Text : p.Accent;
            _log.BackColor = p.Surface; _log.ForeColor = p.TextDim;
            Theme.Apply(_content);
            _rail.Invalidate(); _top.Invalidate(); _status.Invalidate();
            foreach (Control c in _top.Controls) c.Invalidate();
            foreach (var it in _rail.Items) if (it.Instance != null) Theme.Apply(it.Instance);
            if (IsHandleCreated) Theme.DarkTitleBar(this);
            // pills keep the colour they were given, so recolour them for the new palette
            UpdateDevicePills(); UpdateTaskPill(); UpdateDataPill();
            Theme.Apply(_status);
            Tick();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.K) || keyData == (Keys.Control | Keys.Shift | Keys.P) || keyData == (Keys.Control | Keys.F)) { ShowPalette(); return true; }
            if (keyData == (Keys.Control | Keys.Shift | Keys.N)) { _night.PerformClick(); return true; }
            for (int i = 0; i < Math.Min(9, _rail.Items.Count); i++)
                if (keyData == (Keys.Control | (Keys.D1 + i))) { Navigate(_rail.Items[i].Key); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        public void ShowPalette()
        {
            using (var p = new CommandPalette(Commands))
            {
                p.StartPosition = FormStartPosition.Manual;
                p.Location = PointToScreen(new Point((ClientSize.Width - p.Width) / 2, 60));
                p.ShowDialog(this);
            }
        }

        /// <summary>Show an object everywhere relevant: select it and open the sky chart centred on it.</summary>
        public void ShowObject(SelectedObject o)
        {
            AppState.Select(o);
            Navigate("sky");
            GetView<SkyView>("sky")?.Goto(o);
        }

        public void Start()
        {
            var last = AppState.Settings.LastView;
            Navigate(_rail.Items.Any(i => i.Key == last) ? last : "home");
        }
    }

    public interface ICommandSource { IEnumerable<AppCommand> GetCommands(); }
    public interface IActivatable { void Activated(); }

    /// <summary>Command palette: fuzzy search across commands and sky objects.</summary>
    public class CommandPalette : Form
    {
        private readonly TextBox _box = new TextBox { Dock = DockStyle.Top, Font = new Font("Segoe UI", 12f), BorderStyle = BorderStyle.None };
        private readonly ListBox _list = new ListBox { Dock = DockStyle.Fill, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 34, BorderStyle = BorderStyle.None, IntegralHeight = false };
        private readonly List<AppCommand> _commands;

        private class Entry { public string Title, Sub, Icon; public Action Run; public override string ToString() => Title; }

        public CommandPalette(List<AppCommand> commands)
        {
            _commands = commands;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            Size = new Size(640, 420);
            KeyPreview = true;
            var p = Theme.Current;
            BackColor = p.BorderStrong;
            Padding = new Padding(1);
            var inner = new Panel { Dock = DockStyle.Fill, BackColor = p.SurfaceRaised, Padding = new Padding(12) };
            var boxHost = new Panel { Dock = DockStyle.Top, Height = 40, BackColor = p.Surface, Padding = new Padding(10, 9, 10, 4) };
            _box.BackColor = p.Surface; _box.ForeColor = p.Text;
            boxHost.Controls.Add(_box);
            _list.BackColor = p.SurfaceRaised; _list.ForeColor = p.Text;
            var hint = new Label { Dock = DockStyle.Bottom, Height = 22, Text = "↑↓ to move · Enter to run · Esc to close · type an object name (M 31, Vega, Jupiter) or a command", ForeColor = p.TextFaint, Font = Theme.SmallFont };
            inner.Controls.Add(_list);
            inner.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 8, BackColor = p.SurfaceRaised });
            inner.Controls.Add(boxHost);
            inner.Controls.Add(hint);
            Controls.Add(inner);
            _box.TextChanged += (s, e) => Refill();
            _list.DrawItem += DrawItem;
            _list.DoubleClick += (s, e) => RunSelected();
            KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Escape) Close();
                else if (e.KeyCode == Keys.Down) { if (_list.Items.Count > 0) _list.SelectedIndex = Math.Min(_list.Items.Count - 1, _list.SelectedIndex + 1); e.Handled = true; }
                else if (e.KeyCode == Keys.Up) { if (_list.Items.Count > 0) _list.SelectedIndex = Math.Max(0, _list.SelectedIndex - 1); e.Handled = true; }
                else if (e.KeyCode == Keys.Enter) { RunSelected(); e.Handled = true; e.SuppressKeyPress = true; }
            };
            Deactivate += (s, e) => Close();
            Refill();
        }

        private static int Score(string text, string query)
        {
            if (string.IsNullOrEmpty(query)) return 1;
            text = text.ToLowerInvariant(); query = query.ToLowerInvariant();
            if (text.StartsWith(query)) return 100 - text.Length / 10;
            int i = text.IndexOf(query, StringComparison.Ordinal);
            if (i >= 0) return 60 - i;
            // subsequence match (fuzzy)
            int qi = 0, gaps = 0, last = -1;
            for (int k = 0; k < text.Length && qi < query.Length; k++)
                if (text[k] == query[qi]) { if (last >= 0) gaps += k - last - 1; last = k; qi++; }
            return qi == query.Length ? Math.Max(1, 30 - gaps) : 0;
        }

        private void Refill()
        {
            string q = _box.Text.Trim();
            var entries = new List<KeyValuePair<int, Entry>>();
            foreach (var c in _commands)
            {
                int s = Score(c.Title, q);
                if (s > 0) entries.Add(new KeyValuePair<int, Entry>(s, new Entry { Title = c.Title, Sub = c.Category + (c.Shortcut != null ? "  ·  " + c.Shortcut : ""), Icon = c.Icon ?? Icons.More, Run = c.Run }));
            }
            if (q.Length >= 1)
            {
                foreach (var o in CatalogManager.Instance.Search(q, 12))
                {
                    var obj = o;
                    entries.Add(new KeyValuePair<int, Entry>(Score(o.DisplayName, q) + 5, new Entry
                    {
                        Title = o.DisplayName, Icon = o is Star ? Icons.Star : Icons.Target,
                        Sub = (o is Star ? "Star" : DeepSkyObject.TypeLabel(o.Type)) + (double.IsNaN(o.Mag) ? "" : string.Format("  ·  mag {0:0.0}", o.Mag)) + (o.Constellation != null ? "  ·  " + o.Constellation : "") + "  ·  show in sky chart",
                        Run = () => MainForm.Instance.ShowObject(SelectedObject.From(obj))
                    }));
                }
                foreach (Body b in Enum.GetValues(typeof(Body)))
                {
                    if (b == Body.Earth || b == Body.EarthMoonBarycenter) continue;
                    int s = Score(b.ToString(), q);
                    if (s > 20)
                    {
                        var body = b;
                        entries.Add(new KeyValuePair<int, Entry>(s + 10, new Entry { Title = b.ToString(), Sub = "Solar system  ·  show in sky chart", Icon = Icons.Planet, Run = () => MainForm.Instance.ShowObject(Views.SkyView.PlanetObject(body)) }));
                    }
                }
                foreach (var name in Science.SolarSystem.GalileanMoons.Names)
                {
                    int s = Score(name, q);
                    if (s > 20)
                    {
                        var moonName = name;
                        entries.Add(new KeyValuePair<int, Entry>(s + 10, new Entry { Title = name, Sub = "Moon of Jupiter  ·  show in sky chart", Icon = Icons.Planet, Run = () => MainForm.Instance.ShowObject(Views.SkyView.MoonObject(moonName)) }));
                    }
                }
                foreach (var mb in Core.MinorBodies.MinorBodyStore.Instance.All.Where(m => m.DisplayName.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0).Take(8))
                {
                    var m = mb;
                    entries.Add(new KeyValuePair<int, Entry>(40, new Entry { Title = m.DisplayName, Sub = (m.IsComet ? "Comet" : "Asteroid") + "  ·  show in sky chart", Icon = Icons.Comet, Run = () => MainForm.Instance.ShowObject(Views.SkyView.MinorObject(m)) }));
                }
            }
            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (var e in entries.OrderByDescending(e => e.Key).Take(40)) _list.Items.Add(e.Value);
            if (_list.Items.Count > 0) _list.SelectedIndex = 0;
            _list.EndUpdate();
        }

        private void DrawItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            var p = Theme.Current;
            var it = (Entry)_list.Items[e.Index];
            bool sel = (e.State & DrawItemState.Selected) != 0;
            using (var b = new SolidBrush(sel ? p.AccentSoft : p.SurfaceRaised)) e.Graphics.FillRectangle(b, e.Bounds);
            TextRenderer.DrawText(e.Graphics, it.Icon, Theme.IconFont, new Point(e.Bounds.X + 8, e.Bounds.Y + 9), sel ? p.Accent : p.TextDim);
            TextRenderer.DrawText(e.Graphics, it.Title, Theme.UiFont, new Point(e.Bounds.X + 36, e.Bounds.Y + 2), p.Text);
            TextRenderer.DrawText(e.Graphics, it.Sub, Theme.SmallFont, new Point(e.Bounds.X + 36, e.Bounds.Y + 18), p.TextDim);
        }

        private void RunSelected()
        {
            var it = _list.SelectedItem as Entry;
            if (it == null) return;
            Close();
            try { it.Run(); } catch (Exception ex) { MainForm.Instance.Toast(ex.Message, Icons.Warning, Theme.Current.Danger); }
        }
    }
}
