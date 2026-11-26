using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using Sidralune.Core.MinorBodies;
using Sidralune.Devices;
using Sidralune.Devices.Guiding;
using Sidralune.Imaging;
using Sidralune.UI;
using static Sidralune.Core.Util.Lang;

namespace Sidralune.Views
{
    /// <summary>The Guiding tab: PHD2 or the Sidralune guider, its frames and graph, and its settings, assistants, darks and tools.</summary>
    public partial class ObservatoryView
    {
        /// <summary>Hides or shows a FormGrid row: its editor and its label.</summary>
        private static void ShowRow(FormGrid grid, Control editor, bool on)
        {
            editor.Visible = on;
            var label = grid.GetControlFromPosition(0, grid.GetRow(editor));
            if (label != null && label != editor) label.Visible = on;
        }

        private static FlowLayoutPanel ButtonRow(params Control[] buttons)
        {
            var f = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, Margin = new Padding(0) };
            foreach (var b in buttons) { b.Margin = new Padding(0, 0, 6, 4); f.Controls.Add(b); }
            return f;
        }

        private static FlatButton MakeButton(string text, string icon) { var b = new FlatButton(text, icon); b.AutoSizeToText(); return b; }

        private static Label Note(bool dim = false) => new Label { AutoSize = true, MaximumSize = new Size(330, 0), UseMnemonic = false, Tag = dim ? "dim" : null, Margin = new Padding(0, 4, 0, 4) };

        /// <summary>Runs a UI action, telling the user what went wrong instead of throwing.</summary>
        private static void Try(Action a) { try { a(); } catch (Exception ex) { MainForm.Instance.Toast(ex.Message, Icons.Warning); } }

        private static string AoCalibrationText(AoCalibration c) =>
            c == null || c.XPxPerStep <= 0 ? T("The AO is not calibrated yet: it is calibrated after the mount when guiding starts.")
            : T("AO calibrated {0:yyyy-MM-dd HH:mm}: X {1:0.00} px a step towards {2:0}°, Y {3:0.00} px a step towards {4:0}°.", c.When.ToLocalTime(), c.XPxPerStep, c.XAngleDeg, c.YPxPerStep, c.YAngleDeg);

        private Control BuildGuiding()
        {
            var panel = new Panel { Dock = DockStyle.Fill };
            var plot = new PlotControl { Title = T("Guiding error"), YLabel = T("arcsec"), XLabel = T("step") };
            var ra = plot.AddSeries(T("RA")); var dec = plot.AddSeries(T("Dec"));
            // the error as a graph, as points round the lock position, and the guide star's profile
            var targetView = new GuideTargetView();
            var profile = new PlotControl { Title = T("Star profile"), YLabel = T("ADU"), XLabel = T("pixels from the star") };
            var profX = profile.AddSeries(T("X"), SeriesStyle.LineScatter); var profY = profile.AddSeries(T("Y"), SeriesStyle.LineScatter);
            // dashed, so X and Y can be told apart in the red night palette too
            profY.DashDot = true;
            var graphs = new TabbedPanel { Dock = DockStyle.Bottom, Height = 262 };
            graphs.AddPage(T("Graph"), plot);
            graphs.AddPage(T("Target"), targetView);
            graphs.AddPage(T("Star profile"), profile);
            var stats = new Label { Dock = DockStyle.Top, Height = 28, Font = Theme.UiFontBold, UseMnemonic = false }; // error messages may hold "&"
            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
            Func<string, string, Action, FlatButton> btn = (t, i, a) => { var b = new FlatButton(t, i, (s, e) => Try(a)); b.AutoSizeToText(); bar.Controls.Add(b); return b; };
            Func<SidraluneGuider> builtin = () => Dm.Guider as SidraluneGuider;

            // the Sidralune guider guides with the guide camera; PHD2, a program of its own, is connected here instead
            var phd2Job = new JobSlot();
            var phd2 = btn(T("Disconnect PHD2"), Icons.Connect, () =>
            {
                if (Dm.Phd2Connected) { phd2Job.Run(T("Disconnect PHD2"), t => Dm.Disconnect(DeviceKind.Guider)); return; }
                var info = new DeviceInfo { Kind = DeviceKind.Guider, Protocol = DeviceProtocol.Phd2, Name = "PHD2", Address = "localhost:4400" };
                phd2Job.Run(T("Connect PHD2"), t => Dm.Connect(info), () => MainForm.Instance.Toast(T("Connected to PHD2")),
                    ex => MainForm.Instance.Toast(T("Could not reach PHD2 on localhost:4400.") + " " + Phd2Hint, Icons.Warning, Theme.Current.Danger));
            });
            // sized for the longer text, so switching does not move the other buttons
            int phd2Width = phd2.Width;
            phd2.Text = T("Connect PHD2"); phd2.AutoSizeToText();
            phd2.Width = phd2Width = Math.Max(phd2Width, phd2.Width);
            phd2Job.Guard(phd2);
            Func<IGuider> guider = () => Need(Dm.Guider, NoGuider, GuiderHint);
            Func<SidraluneGuider> own = () =>
            {
                if (Dm.Phd2Connected) throw new InvalidOperationException(T("PHD2 takes its guide frames itself."));
                return Need(Dm.Guider as SidraluneGuider, T("No guide camera connected."));
            };
            var loop = btn(T("Loop"), Icons.Refresh, () => own().Loop());
            loop.ToolTipText = T("Take guide frames and follow the star, without guiding");
            var select = btn(T("Select star"), Icons.Star, () => { var g = own(); g.Loop(); g.SelectStar(); });
            select.ToolTipText = T("Choose the best guide star, or click a star in the frame");
            var guideJob = new JobSlot();
            // a polar alignment needs the mount to itself: guiding, calibrating and dithering wait until it ends
            var start = btn(T("Start guiding"), Icons.Play, () => { if (!PolarFree()) return; var g = guider(); guideJob.Run(T("Start guiding"), t => g.StartGuiding(false, t.Cts.Token).GetAwaiter().GetResult()); });
            start.Kind = ButtonKind.Primary;
            var calibrate = btn(T("Calibrate & guide"), Icons.Target, () => { if (!PolarFree()) return; var g = guider(); guideJob.Run(T("Calibrate"), t => g.StartGuiding(true, t.Cts.Token).GetAwaiter().GetResult()); });
            var dither = btn(T("Dither"), Icons.Grid, () => { if (!PolarFree()) return; var g = guider(); guideJob.Run(T("Dither"), t => g.Dither(5, false, 1.5, 10, 120, t.Cts.Token).GetAwaiter().GetResult()); });
            // one guiding command at a time: a second start would wait for the same settling
            guideJob.Guard(start, calibrate, dither);
            foreach (var b in new[] { start, calibrate, dither }) Availability.When(b, () => PolarBusyReason() == null);
            // while the guide camera takes dark frames (or a single frame) the Sidralune guider cannot loop or guide
            foreach (var b in new[] { start, calibrate, dither }) Availability.When(b, () => Dm.Phd2Connected || !Dm.BuiltinGuider.CameraInUse);
            foreach (var b in new[] { loop, select }) Availability.When(b, () => !Dm.Phd2Connected && !Dm.BuiltinGuider.CameraInUse);
            var guideStop = btn(T("Stop"), Icons.Stop, () => { guideJob.Cancel(); guider().StopGuiding(); });
            guideStop.Kind = ButtonKind.Danger;
            // Stop is available while the guider is doing something (PHD2 is idle in "Stopped" and "Selected"; the
            // Sidralune guider is busy whenever it takes frames) or a guiding command runs
            Action syncGuideStop = () =>
            {
                var gd = Dm.Guider;
                bool on = guideJob.Busy;
                try
                {
                    var sg = gd as SidraluneGuider;
                    if (sg != null) on |= sg.IsRunning;
                    else on |= gd != null && gd.Connected && !string.IsNullOrEmpty(gd.State) && gd.State != "Stopped" && gd.State != "Selected";
                }
                catch { }
                guideStop.Enabled = on;
                phd2.Text = Dm.Phd2Connected ? T("Disconnect PHD2") : T("Connect PHD2");
                phd2.Kind = Dm.Phd2Connected ? ButtonKind.Danger : ButtonKind.Normal;
                phd2.Width = phd2Width;
                // PHD2 opens the guide camera itself: with a guide camera connected here, Connect PHD2 waits
                if (!phd2Job.Busy) phd2.Enabled = Dm.Phd2Connected || Dm.GuideCamera == null;
                Availability.Sync(); // Loop and Select star follow PHD2 connecting
            };
            guideJob.Changed += syncGuideStop;
            phd2Job.Changed += syncGuideStop;
            Dm.DevicesChanged += (s, e) => MainForm.Instance.Post(syncGuideStop);
            syncGuideStop();
            btn(T("Clear graph"), Icons.Remove, () => { ra.X.Clear(); ra.Y.Clear(); dec.X.Clear(); dec.Y.Clear(); plot.Invalidate(); targetView.Clear(); });

            // the guide camera's frames, with the search boxes and the lock position
            var viewer = new ImageViewer { Dock = DockStyle.Fill };
            viewer.Stretch = DisplayStretch.AutoLinked;
            var starInfo = new Label { Dock = DockStyle.Bottom, Height = 22, Tag = "dim" };
            viewer.PaintOverlay += (gr, toScreen) =>
            {
                var g = builtin();
                if (g == null || viewer.Image == null || g.LastFrame == null) return;
                var s = g.Star;
                string state = g.State;
                int r = g.Settings.SearchRadius;
                // green while guiding, orange while the star is lost or a frame is left out for its brightness, yellow otherwise
                var color = state == SidraluneGuider.LostLock || g.FrameRejected ? Theme.Current.Danger : state == SidraluneGuider.Guiding ? Theme.Current.Success : Theme.Current.Warning;
                Action<Pen, double, double> box = (pen, x, y) =>
                {
                    var a = toScreen(x - r, y - r); var b = toScreen(x + r, y + r);
                    gr.DrawRectangle(pen, Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
                };
                using (var thin = new Pen(Theme.Current.TextDim, 1f))
                    foreach (var o in g.SecondaryStars) if (o[2] > 0) box(thin, o[0], o[1]);
                using (var pen = new Pen(color, 1.5f))
                {
                    if (s != null) box(pen, s.X, s.Y);
                    if (!double.IsNaN(g.LockX))
                    {
                        var c = toScreen(g.LockX, g.LockY);
                        gr.DrawLine(pen, c.X - 12, c.Y, c.X - 4, c.Y); gr.DrawLine(pen, c.X + 4, c.Y, c.X + 12, c.Y);
                        gr.DrawLine(pen, c.X, c.Y - 12, c.X, c.Y - 4); gr.DrawLine(pen, c.X, c.Y + 4, c.X, c.Y + 12);
                    }
                }
                if (g.UsingAo) DrawAoGauge(gr, viewer.ClientSize, Dm.Ao, g.Settings.AoBumpPercent, g.Bumping);
            };
            // a left click chooses the star; a right click offers that or the lock position there
            double menuX = double.NaN, menuY = double.NaN;
            var frameMenu = new ContextMenuStrip { Renderer = new DarkToolStripRenderer() };
            frameMenu.Items.Add(T("Select star here"), null, (s, e) => Try(() => { own().SelectStar(menuX, menuY); viewer.Invalidate(); }));
            frameMenu.Items.Add(T("Set lock position here"), null, (s, e) => Try(() => { own().SetLockPosition(menuX, menuY); viewer.Invalidate(); }));
            viewer.ImageClick += (x, y, e) =>
            {
                var g = builtin();
                if (g == null || Dm.Phd2Connected) return;
                if (e.Button == MouseButtons.Right) { menuX = x; menuY = y; frameMenu.Show(Cursor.Position); return; }
                if (e.Button != MouseButtons.Left) return;
                Try(() => { g.SelectStar(x, y); viewer.Invalidate(); });
            };
            var view = new Panel { Dock = DockStyle.Fill };
            view.Controls.Add(viewer);
            view.Controls.Add(starInfo);

            // the Sidralune guider's settings, assistants, dark frames and tools, on pages beside the frame
            var right = new TabbedPanel { Dock = DockStyle.Right, Width = 390 };
            Action refreshSettings, pollAssistants, pollDarks, pollTools;
            right.AddPage(T("Settings"), GuidingSettingsPage(out refreshSettings));
            right.AddPage(T("Assistants"), GuidingAssistantsPage(guideJob, refreshSettings, out pollAssistants));
            right.AddPage(T("Darks"), GuidingDarksPage(out pollDarks));
            right.AddPage(T("Tools"), GuidingToolsPage(out pollTools));
            right.Select(0);

            // the guider's steps and frames, shown one burst at a time (they arrive on the guider's thread)
            int n0 = 0;
            var steps = new System.Collections.Concurrent.ConcurrentQueue<GuideStep>();
            int stepsPosted = 0, framePosted = 0;
            Action drainSteps = () =>
            {
                Interlocked.Exchange(ref stepsPosted, 0);
                GuideStep s;
                while (steps.TryDequeue(out s)) { ra.Add(n0, s.RaArcsec); dec.Add(n0, s.DecArcsec); targetView.Add(s.RaArcsec, s.DecArcsec); n0++; }
                while (ra.X.Count > 400) { ra.X.RemoveAt(0); ra.Y.RemoveAt(0); dec.X.RemoveAt(0); dec.Y.RemoveAt(0); }
                plot.Invalidate();
            };
            Action<GuideStep> onStep = s => { steps.Enqueue(s); if (Interlocked.Exchange(ref stepsPosted, 1) == 0) MainForm.Instance.Post(drainSteps); };
            Action showFrame = () =>
            {
                Interlocked.Exchange(ref framePosted, 0);
                var g = Dm.BuiltinGuider;
                var f = g.LastFrame;
                if (f == null || Dm.Phd2Connected) return;
                bool fit = viewer.Image == null || viewer.Image.Width != f.Width || viewer.Image.Height != f.Height;
                viewer.SetImage(f, fit);
                var s = g.Star;
                if (profile.Visible) StarProfile(f, s, g.Settings.SearchRadius, profile, profX, profY);
                starInfo.Text = s == null ? T("No guide star: click a star or Select star") : g.Lost ? T("Star lost at {0:0.0}, {1:0.0}", s.X, s.Y)
                    : T("Star at {0:0.0}, {1:0.0} · SNR {2:0} · HFD {3:0.0} px", s.X, s.Y, s.Snr, s.Hfd) + (s.Saturated ? " · ⚠ " + T("saturated") : "")
                      + (g.FrameRejected ? " · ⚠ " + T("brightness jumped: frame not used") : "");
            };
            Action<AstroImage> onFrame = f => { if (Interlocked.Exchange(ref framePosted, 1) == 0) MainForm.Instance.Post(showFrame); };
            // the Sidralune guider is always there; PHD2's steps are followed while it is connected
            var sidralune = Dm.BuiltinGuider;
            sidralune.FrameReady += onFrame;
            sidralune.Failed += m => MainForm.Instance.Post(() => MainForm.Instance.Toast(m, Icons.Warning, Theme.Current.Danger));
            IGuider hooked = null;
            bool hookedOnce = false;
            Action hook = () =>
            {
                var g = Dm.Guider;
                // the first time too, with no guider yet, so the empty frame says what to do
                if (g == hooked && hookedOnce) return;
                hookedOnce = true;
                if (hooked != null) hooked.Step -= onStep;
                hooked = g;
                if (g != null) g.Step += onStep;
                if (!(g is SidraluneGuider)) viewer.SetImage(null);
                viewer.EmptyText = Dm.Phd2Connected ? T("PHD2 shows its guide frames itself") : T("Connect a guide camera in Observatory → Equipment, then click Loop");
                refreshSettings();
            };
            Dm.DevicesChanged += (s, e) => MainForm.Instance.Post(hook);
            // another equipment profile: its guider settings and calibration, and its focal length
            AppState.ProfileChanged += (s, e) => MainForm.Instance.Post(refreshSettings);
            hook();
            Poll += () =>
            {
                hook();
                syncGuideStop();
                pollAssistants(); pollDarks(); pollTools();
                var g = Dm.Guider;
                var sg = g as SidraluneGuider;
                if (g == null) stats.Text = T("No guider connected.") + " " + GuiderHint;
                else
                {
                    // the guider in use and its state, then why it stopped or the RMS once there are guide steps
                    string state = (sg != null ? DeviceManager.BuiltinGuiderName : "PHD2") + " · " + GuiderStateText(g.State);
                    // then how many stars, the exposure auto exposure chose, and comet tracking
                    string more = "";
                    if (sg != null && sg.IsRunning)
                    {
                        if (sg.StarsUsed > 1) more += " · " + T("{0} stars", sg.StarsUsed);
                        if (sg.Settings.AutoExposure) more += " · " + T("{0:0.0#} s", sg.CurrentExposure);
                        if (sg.CometFollowing) more += " · " + T("Comet");
                        if (sg.SubframeRead != null) more += " · " + T("Subframe");
                        if (sg.UsingAo)
                            try { var ao = Dm.Ao; more += " · " + T("AO {0:+0;-0;0}, {1:+0;-0;0}", ao.PositionX, ao.PositionY) + (sg.Bumping ? " · " + T("bumping the mount") : ""); } catch { }
                    }
                    if (sg != null && sg.State == SidraluneGuider.Stopped && sg.LastError != null) stats.Text = state + " · " + sg.LastError;
                    else if (double.IsNaN(g.RmsTotalArcsec)) stats.Text = state + more;
                    else stats.Text = T("{0} · RMS RA {1:0.00}\"  Dec {2:0.00}\"  total {3:0.00}\"", state, g.RmsRaArcsec, g.RmsDecArcsec, g.RmsTotalArcsec) + more;
                }
            };
            panel.Controls.Add(view);
            panel.Controls.Add(right);
            panel.Controls.Add(graphs);
            panel.Controls.Add(stats);
            panel.Controls.Add(bar);
            return panel;
        }

        /// <summary>
        /// The AO's position in its travel, in a corner of the frame: a square of the travel, the bump point inside it,
        /// and a dot where the AO is (red while the mount is being bumped).
        /// </summary>
        private static void DrawAoGauge(Graphics gr, Size view, IAdaptiveOptics ao, double bumpPercent, bool bumping)
        {
            int max, x, y;
            try { max = Math.Max(1, ao.MaxSteps); x = ao.PositionX; y = ao.PositionY; } catch { return; }
            var p = Theme.Current;
            const int size = 64, margin = 10;
            var box = new RectangleF(view.Width - size - margin, margin + 16, size, size);
            gr.SmoothingMode = SmoothingMode.AntiAlias;
            using (var bg = new SolidBrush(Color.FromArgb(200, p.Surface))) gr.FillRectangle(bg, box.X - 4, box.Y - 18, box.Width + 8, box.Height + 22);
            using (var text = new SolidBrush(p.TextDim)) gr.DrawString(T("AO"), Theme.SmallFont, text, box.X, box.Y - 16);
            using (var pen = new Pen(p.Border)) gr.DrawRectangle(pen, box.X, box.Y, box.Width, box.Height);
            float half = size / 2f, b = (float)(half * Math.Max(0, Math.Min(100, bumpPercent)) / 100);
            float cx = box.X + half, cy = box.Y + half;
            using (var pen = new Pen(p.Warning) { DashStyle = DashStyle.Dash }) gr.DrawRectangle(pen, cx - b, cy - b, 2 * b, 2 * b);
            using (var pen = new Pen(p.Border)) { gr.DrawLine(pen, cx, box.Y, cx, box.Bottom); gr.DrawLine(pen, box.X, cy, box.Right, cy); }
            // X positive West (to the right), Y positive North (up), as the AO's maker names them
            float dx = cx + half * x / max, dy = cy - half * y / max;
            using (var dot = new SolidBrush(bumping ? p.Danger : p.Accent)) gr.FillEllipse(dot, dx - 4, dy - 4, 8, 8);
        }

        /// <summary>The guide star's cross-sections through X and Y, with its peak, HFD and FWHM in the title.</summary>
        private static void StarProfile(AstroImage f, GuideStarInfo s, int searchRadius, PlotControl plot, PlotSeries px, PlotSeries py)
        {
            px.X.Clear(); px.Y.Clear(); py.X.Clear(); py.Y.Clear();
            if (s == null || f == null) { plot.Title = T("Star profile"); plot.Invalidate(); return; }
            var lum = f.Luminance();
            int w = f.Width, h = f.Height, r = Math.Max(8, searchRadius), cx = (int)Math.Round(s.X), cy = (int)Math.Round(s.Y);
            for (int d = -r; d <= r; d++)
            {
                int x = cx + d, y = cy + d;
                if (x >= 0 && x < w && cy >= 0 && cy < h) px.Add(d, lum[cy * w + x] * 65535.0);
                if (y >= 0 && y < h && cx >= 0 && cx < w) py.Add(d, lum[y * w + cx] * 65535.0);
            }
            double fx = Fwhm(px), fy = Fwhm(py), fwhm = double.IsNaN(fx) ? fy : double.IsNaN(fy) ? fx : (fx + fy) / 2;
            double peak = px.Y.Count > 0 ? px.Y.Max() : double.NaN;
            plot.Title = T("Star profile · peak {0:0} ADU · HFD {1:0.0} px · FWHM {2:0.0} px", peak, s.Hfd, fwhm);
            plot.Invalidate();
        }

        /// <summary>The width at half the height above the background (the ends' lowest), between the crossings either side of the peak.</summary>
        private static double Fwhm(PlotSeries s)
        {
            int n = s.Y.Count;
            if (n < 5) return double.NaN;
            double bg = Math.Min(s.Y.Take(3).Average(), s.Y.Skip(n - 3).Average());
            int top = s.Y.IndexOf(s.Y.Max());
            double half = bg + (s.Y[top] - bg) / 2;
            if (s.Y[top] - bg <= 0) return double.NaN;
            double left = double.NaN, right = double.NaN;
            for (int i = top; i > 0; i--) if (s.Y[i - 1] < half) { left = s.X[i - 1] + (half - s.Y[i - 1]) / (s.Y[i] - s.Y[i - 1]) * (s.X[i] - s.X[i - 1]); break; }
            for (int i = top; i < n - 1; i++) if (s.Y[i + 1] < half) { right = s.X[i] + (s.Y[i] - half) / (s.Y[i] - s.Y[i + 1]) * (s.X[i + 1] - s.X[i]); break; }
            return right - left;
        }

        /// <summary>The last hundred guide steps as points round the lock position (RA across, Dec up), with rings of whole arcseconds and the RMS circle.</summary>
        private sealed class GuideTargetView : Control, IThemed
        {
            private readonly List<PointF> _pts = new List<PointF>();

            public GuideTargetView()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            }

            public int Count => _pts.Count;

            public void Add(double ra, double dec)
            {
                if (double.IsNaN(ra) || double.IsNaN(dec)) return;
                _pts.Add(new PointF((float)ra, (float)dec));
                if (_pts.Count > 100) _pts.RemoveAt(0);
                Invalidate();
            }

            public void Clear() { _pts.Clear(); Invalidate(); }

            public void ApplyTheme(Palette p) => Invalidate();

            protected override void OnPaint(PaintEventArgs e)
            {
                var p = Theme.Current;
                var g = e.Graphics;
                g.Clear(p.ChartBackground);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                float cx = Width / 2f, cy = Height / 2f, radius = Math.Min(Width, Height) / 2f - 20;
                if (radius < 10) return;
                // scaled to the bulk of the points (nine in ten), so one large jump does not shrink the rest; points
                // beyond the outer ring sit on its edge
                double max = 3;
                if (_pts.Count > 0)
                {
                    var r = _pts.Select(q => Math.Max(Math.Abs(q.X), Math.Abs(q.Y))).OrderBy(x => x).ToList();
                    max = Math.Max(max, 1.15 * r[(int)Math.Floor(0.9 * (r.Count - 1))]);
                }
                max = Math.Ceiling(max);
                float k = (float)(radius / max);
                int step = max <= 6 ? 1 : (int)Math.Ceiling(max / 6);
                using (var grid = new Pen(p.ChartGrid))
                using (var text = new SolidBrush(p.ChartText))
                {
                    g.DrawLine(grid, cx - radius, cy, cx + radius, cy);
                    g.DrawLine(grid, cx, cy - radius, cx, cy + radius);
                    for (int r = step; r <= max; r += step)
                    {
                        g.DrawEllipse(grid, cx - r * k, cy - r * k, 2 * r * k, 2 * r * k);
                        g.DrawString(r + "″", Theme.SmallFont, text, cx + r * k * 0.71f + 1, cy - r * k * 0.71f - 13);
                    }
                    g.DrawString(T("RA"), Theme.SmallFont, text, cx + radius - 16, cy + 2);
                    g.DrawString(T("Dec"), Theme.SmallFont, text, cx + 3, cy - radius - 4);
                }
                // older points fainter
                for (int i = 0; i < _pts.Count; i++)
                {
                    int alpha = 60 + 195 * (i + 1) / _pts.Count;
                    float px = _pts[i].X * k, py = _pts[i].Y * k, len = (float)Math.Sqrt(px * px + py * py);
                    if (len > radius) { px *= radius / len; py *= radius / len; }
                    using (var b = new SolidBrush(Color.FromArgb(alpha, i == _pts.Count - 1 ? p.Warning : p.Accent)))
                        g.FillEllipse(b, cx + px - 2.5f, cy - py - 2.5f, 5, 5);
                }
                if (_pts.Count < 2) return;
                double rms = Math.Sqrt(_pts.Average(q => q.X * q.X + q.Y * q.Y));
                float rr = Math.Min(radius, (float)rms * k);
                using (var pen = new Pen(p.Success, 1.5f) { DashStyle = DashStyle.Dash }) g.DrawEllipse(pen, cx - rr, cy - rr, 2 * rr, 2 * rr);
                using (var b = new SolidBrush(p.Success)) g.DrawString(T("RMS {0:0.00}″", rms), Theme.UiFont, b, 6, Height - 22);
            }
        }

        // ------------------------------------------------------------------ settings

        private Control GuidingSettingsPage(out Action refresh)
        {
            var page = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
            var camGrid = new FormGrid(170);
            var exposure = camGrid.Row(T("Exposure (s)"), Ui.Num(2, 0.05m, 30, 2, 0.5m));
            var auto = camGrid.Row(T("Auto exposure"), new CheckBox { Text = T("Follow the star's SNR") });
            var autoMin = camGrid.Row(T("Shortest exposure (s)"), Ui.Num(1, 0.5m, 10, 1, 0.5m));
            var autoMax = camGrid.Row(T("Longest exposure (s)"), Ui.Num(5, 0.5m, 10, 1, 0.5m));
            var target = camGrid.Row(T("Target SNR"), Ui.Num(10, 3, 100));
            var binning = camGrid.Row(T("Binning"), Ui.Combo("1", "2", "3", "4"));
            var output = camGrid.Row(T("Guide through"), Ui.Combo(T("The mount"), T("The guide camera (ST-4)")));
            // from the equipment profile: the guide scope's, or the imaging telescope's through an off-axis guider
            var focal = camGrid.Row(T("Focal length"), new Label { AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, UseMnemonic = false });
            var solveOn = camGrid.Row(T("Plate solving"), new CheckBox { Text = T("Solve guide frames") });
            var solveText = camGrid.Row(T("Last solve"), new Label { AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, UseMnemonic = false });
            // as tall as its text wrapped to the column: the solve's numbers, or why it failed
            Action fitSolve = () =>
            {
                int h = TextRenderer.MeasureText(solveText.Text, solveText.Font, new Size(Math.Max(60, solveText.Width), 0), TextFormatFlags.WordBreak).Height + 6;
                if (solveText.Height != Math.Max(22, h)) solveText.Height = Math.Max(22, h);
            };
            solveText.Resize += (s, e) => fitSolve();
            camGrid.Full(Ui.Hint(T("When guiding starts, a guide frame is solved near the mount's position: the camera's scale and angle come from it, and guide stars are chosen from the catalogue's single stars that do not vary. A lost guide star is found again by solving, even far beyond its search box, and brought back.")));
            var radius = camGrid.Row(T("Search box radius (px)"), Ui.Num(15, 5, 50));
            var subframes = camGrid.Row(T("Subframes"), new CheckBox { Text = T("While guiding, download only a box round the stars") });
            var noise = camGrid.Row(T("Noise reduction"), Ui.Combo(T("None"), T("2×2 mean"), T("3×3 median")));

            var starGrid = new FormGrid(170);
            var multi = starGrid.Row(T("Multi-star"), new CheckBox { Text = T("Guide on several stars") });
            var maxStars = starGrid.Row(T("Most stars"), Ui.Num(9, 2, 20));
            var mass = starGrid.Row(T("Star brightness"), new CheckBox { Text = T("Skip frames where it jumps") });
            var massTol = starGrid.Row(T("Brightness tolerance (%)"), Ui.Num(50, 10, 200));
            starGrid.Full(Ui.Hint(T("A frame in which the guide star's brightness changes by more than the tolerance is not used for a correction; after three in a row, the new brightness counts.")));
            var ditherMode = starGrid.Row(T("Dither pattern"), Ui.Combo(T("Random"), T("Spiral")));
            starGrid.Full(Ui.Hint(T("Random dithers anywhere within the amount. Spiral steps out by the amount along a square spiral round where it began, 24 places before it begins again, so no two frames share a place.")));

            var calGrid = new FormGrid(170);
            var predict = calGrid.Row(T("Prediction"), new CheckBox { Text = T("Predict from the plate solve") });
            calGrid.Full(Ui.Hint(T("With a guide frame solved at this pointing, the directions come from the solve and the speeds from the mount's guide rates, and three steps each way only confirm them. When they disagree, it calibrates in full and says why.")));
            var step = calGrid.Row(T("Calibration step (ms)"), Ui.Num(0, 0, 5000, 0, 50));
            calGrid.Full(Ui.Hint(T("0 works the step out from the guide rate, the pixel scale and the declination.")));
            var distance = calGrid.Row(T("Calibration distance (px)"), Ui.Num(20, 5, 60));
            var reverses = calGrid.Row(T("After a meridian flip"), new CheckBox { Text = T("Mount reverses Dec") });
            calGrid.Full(Ui.Hint(T("Most mounts do not turn their Dec pulses round after a flip. If Dec runs away after a flip, change this.")));
            var onRotator = calGrid.Row(T("Rotator"), new CheckBox { Text = T("The guide camera turns with it") });
            var rotReversed = calGrid.Row(T("Rotator direction"), new CheckBox { Text = T("Reversed (a mirrored image)") });
            calGrid.Full(Ui.Hint(T("For an off-axis guider on a rotator: the calibration turns with the rotator, so turning it needs no new calibration.")));
            // "&" is text here ("Calibrate & guide"), not a shortcut marker
            var calText = new Label { AutoSize = true, MaximumSize = new Size(320, 0), Tag = "dim", UseMnemonic = false };
            calGrid.Full(calText);
            var clearCal = MakeButton(T("Clear calibration"), Icons.Remove);
            calGrid.Full(clearCal);

            var raGrid = new FormGrid(170);
            var raAlgo = raGrid.Row(T("RA algorithm"), Ui.Combo(T("Hysteresis"), T("Lowpass2"), T("Z-filter"), T("Predictive PEC")));
            var raHint = Ui.Hint(""); raGrid.Full(raHint);
            var raAggr = raGrid.Row(T("RA aggressiveness (%)"), Ui.Num(70, 0, 200));
            var raHyst = raGrid.Row(T("RA hysteresis (%)"), Ui.Num(10, 0, 90));
            var raLp = raGrid.Row(T("Lowpass2 aggressiveness (%)"), Ui.Num(80, 0, 100));
            var raZ = raGrid.Row(T("Z-filter exposure factor"), Ui.Num(4, 1, 8, 1, 0.5m));
            var pecPred = raGrid.Row(T("Predictive gain (%)"), Ui.Num(50, 0, 100));
            var pecReact = raGrid.Row(T("Reactive gain (%)"), Ui.Num(60, 0, 100));
            var pecPeriod = raGrid.Row(T("Worm period (s)"), Ui.Num(480, 10, 3600));
            var pecAuto = raGrid.Row(T("Period"), new CheckBox { Text = T("Adjust it from the guiding") });
            var pecRetain = raGrid.Row(T("Retain model (%)"), Ui.Num(40, 0, 100));
            var pecStatus = Ui.Hint(""); raGrid.Full(pecStatus);
            var raMin = raGrid.Row(T("RA minimum move (px)"), Ui.Num(0.2m, 0, 5, 2, 0.05m));
            var raMax = raGrid.Row(T("RA maximum pulse (ms)"), Ui.Num(2500, 50, 10000, 0, 100));

            var decGrid = new FormGrid(170);
            var decAlgo = decGrid.Row(T("Dec algorithm"), Ui.Combo(T("Resist Switch"), T("Lowpass2"), T("Z-filter")));
            var decHint = Ui.Hint(""); decGrid.Full(decHint);
            var decAggr = decGrid.Row(T("Dec aggressiveness (%)"), Ui.Num(100, 0, 100));
            var fast = decGrid.Row(T("Large Dec errors"), new CheckBox { Text = T("Correct at once") });
            var decLp = decGrid.Row(T("Lowpass2 aggressiveness (%)"), Ui.Num(80, 0, 100));
            var decZ = decGrid.Row(T("Z-filter exposure factor"), Ui.Num(4, 1, 8, 1, 0.5m));
            var decMin = decGrid.Row(T("Dec minimum move (px)"), Ui.Num(0.2m, 0, 5, 2, 0.05m));
            var decMax = decGrid.Row(T("Dec maximum pulse (ms)"), Ui.Num(2500, 50, 10000, 0, 100));
            var decMode = decGrid.Row(T("Guide Dec"), Ui.Combo(T("Both ways"), T("North only"), T("South only"), T("Off")));
            var backlash = decGrid.Row(T("Backlash compensation"), new CheckBox { Text = T("When Dec turns") });
            var blAmount = decGrid.Row(T("Backlash (ms)"), Ui.Num(0, 0, 5000, 0, 10));
            var blMin = decGrid.Row(T("Backlash at least (ms)"), Ui.Num(0, 0, 5000, 0, 10));
            var blMax = decGrid.Row(T("Backlash at most (ms)"), Ui.Num(0, 0, 10000, 0, 10));
            var blNow = Ui.Hint(""); decGrid.Full(blNow);

            var aoGrid = new FormGrid(170);
            var aoTravel = aoGrid.Row(T("Travel (steps each way)"), Ui.Num(45, 5, 500));
            var aoCalStep = aoGrid.Row(T("Calibration step (steps)"), Ui.Num(5, 1, 50));
            var aoCalSteps = aoGrid.Row(T("Calibration leg (steps)"), Ui.Num(25, 5, 200));
            var aoBump = aoGrid.Row(T("Bump the mount at (%)"), Ui.Num(80, 20, 100));
            var aoBumpStep = aoGrid.Row(T("Bump step (″)"), Ui.Num(1, 0.1m, 10, 1, 0.1m));
            var aoBumpDither = aoGrid.Row(T("After a dither"), new CheckBox { Text = T("Recentre the AO with the mount") });
            var aoAggr = aoGrid.Row(T("AO aggressiveness (%)"), Ui.Num(80, 0, 100));
            var aoMin = aoGrid.Row(T("AO minimum move (px)"), Ui.Num(0.1m, 0, 5, 2, 0.05m));
            aoGrid.Full(Ui.Hint(T("The AO corrects every frame; when it passes the bump point of its travel, the mount takes over in small moves. The AO is calibrated after the mount.")));
            var aoCalText = new Label { AutoSize = true, MaximumSize = new Size(320, 0), Tag = "dim", UseMnemonic = false };
            aoGrid.Full(aoCalText);
            var aoClear = MakeButton(T("Clear AO calibration"), Icons.Remove);
            aoGrid.Full(aoClear);
            var aoCard = new Collapsible(T("Adaptive optics"), aoGrid) { Visible = false };

            var srvGrid = new FormGrid(170);
            var srvOn = new CheckBox { Text = T("Let other programs guide with Sidralune"), AutoSize = true, Margin = new Padding(0, 4, 0, 0) };
            srvGrid.Full(srvOn);
            var srvPort = srvGrid.Row(T("Port"), Ui.Num(4400, 1024, 65535));
            var srvOthers = srvGrid.Row(T("Other computers"), new CheckBox { Text = T("Allow them to connect") });
            var srvStatus = new Label { AutoSize = true, MaximumSize = new Size(320, 0), Tag = "dim", UseMnemonic = false };
            srvGrid.Full(srvStatus);
            srvGrid.Full(Ui.Hint(T("Programs that can guide with PHD2 connect here instead, as if Sidralune's guider were PHD2. PHD2 itself must not use the same port.")));

            page.Controls.Add(new Collapsible(T("PHD2-compatible server"), srvGrid));
            page.Controls.Add(aoCard);
            page.Controls.Add(new Collapsible(T("Dec"), decGrid));
            page.Controls.Add(new Collapsible(T("RA"), raGrid));
            page.Controls.Add(new Collapsible(T("Calibration"), calGrid));
            page.Controls.Add(new Collapsible(T("Guide stars"), starGrid));
            page.Controls.Add(new Collapsible(T("Guide camera"), camGrid));

            var raKinds = new[] { GuideAlgorithmKind.Hysteresis, GuideAlgorithmKind.Lowpass2, GuideAlgorithmKind.ZFilter, GuideAlgorithmKind.PredictivePec };
            var decKinds = new[] { GuideAlgorithmKind.ResistSwitch, GuideAlgorithmKind.Lowpass2, GuideAlgorithmKind.ZFilter };
            var modes = new[] { DecGuideMode.Auto, DecGuideMode.North, DecGuideMode.South, DecGuideMode.Off };
            Func<GuideAlgorithmKind, string> algoHint = k =>
                k == GuideAlgorithmKind.Hysteresis ? T("Corrects a share of the error, smoothed with the one before.")
                : k == GuideAlgorithmKind.ResistSwitch ? T("Changes direction only when the error clearly asks for it, which suits Dec's backlash.")
                : k == GuideAlgorithmKind.Lowpass2 ? T("Fits a line through the last errors so the seeing averages out: for smooth mounts in good seeing.")
                : k == GuideAlgorithmKind.PredictivePec ? T("Learns the mount's periodic error while guiding and corrects it before it shows: for mounts with a regular worm error. It trains for two worm periods.")
                : T("Guides as if the exposures were longer: for slow errors in poor seeing.");
            // only the rows of the chosen algorithms, and of auto exposure and backlash compensation when on
            Action layout = () =>
            {
                var st = Dm.BuiltinGuider.Settings;
                ShowRow(camGrid, exposure, !st.AutoExposure);
                foreach (var c in new Control[] { autoMin, autoMax, target }) ShowRow(camGrid, c, st.AutoExposure);
                ShowRow(starGrid, maxStars, st.MultiStar);
                ShowRow(starGrid, massTol, st.StarMassCheck);
                ShowRow(raGrid, raAggr, st.RaAlgorithm == GuideAlgorithmKind.Hysteresis);
                ShowRow(raGrid, raHyst, st.RaAlgorithm == GuideAlgorithmKind.Hysteresis);
                ShowRow(raGrid, raLp, st.RaAlgorithm == GuideAlgorithmKind.Lowpass2);
                ShowRow(raGrid, raZ, st.RaAlgorithm == GuideAlgorithmKind.ZFilter);
                bool pec = st.RaAlgorithm == GuideAlgorithmKind.PredictivePec;
                foreach (var c in new Control[] { pecPred, pecReact, pecPeriod, pecAuto, pecRetain, pecStatus }) ShowRow(raGrid, c, pec);
                ShowRow(srvGrid, srvPort, st.ServerEnabled);
                ShowRow(srvGrid, srvOthers, st.ServerEnabled);
                ShowRow(decGrid, decAggr, st.DecAlgorithm == GuideAlgorithmKind.ResistSwitch);
                ShowRow(decGrid, fast, st.DecAlgorithm == GuideAlgorithmKind.ResistSwitch);
                ShowRow(decGrid, decLp, st.DecAlgorithm == GuideAlgorithmKind.Lowpass2);
                ShowRow(decGrid, decZ, st.DecAlgorithm == GuideAlgorithmKind.ZFilter);
                foreach (var c in new Control[] { blAmount, blMin, blMax, blNow }) ShowRow(decGrid, c, st.BacklashCompensation);
                raHint.Text = algoHint(st.RaAlgorithm);
                decHint.Text = algoHint(st.DecAlgorithm);
            };

            var controls = new Control[] { exposure, auto, autoMin, autoMax, target, binning, output, solveOn, radius, subframes, noise, multi, maxStars, mass, massTol, ditherMode, predict, step, distance, reverses, onRotator, rotReversed, clearCal,
                raAlgo, raAggr, raHyst, raLp, raZ, pecPred, pecReact, pecPeriod, pecAuto, pecRetain, raMin, raMax, decAlgo, decAggr, fast, decLp, decZ, decMin, decMax, decMode, backlash, blAmount, blMin, blMax,
                aoTravel, aoCalStep, aoCalSteps, aoBump, aoBumpStep, aoBumpDither, aoAggr, aoMin, aoClear };
            bool loading = false;
            refresh = () =>
            {
                // the Sidralune guider's settings can be changed at any time, except while PHD2 guides instead
                var g = Dm.BuiltinGuider;
                foreach (var c in controls) c.Enabled = !Dm.Phd2Connected;
                calText.Text = Dm.Phd2Connected ? T("PHD2 is connected: it has its own settings and calibration.") : CalibrationText(g.Calibration);
                aoCalText.Text = AoCalibrationText(g.AoCalibration);
                var st = g.Settings;
                loading = true;
                try
                {
                    Func<NumericUpDown, double, decimal> clamp = (n, v) => double.IsNaN(v) ? n.Minimum : Math.Max(n.Minimum, Math.Min(n.Maximum, (decimal)v));
                    exposure.Value = clamp(exposure, st.ExposureSeconds); auto.Checked = st.AutoExposure; autoMin.Value = clamp(autoMin, st.AutoExposureMin); autoMax.Value = clamp(autoMax, st.AutoExposureMax);
                    target.Value = clamp(target, st.TargetSnr); binning.SelectedIndex = Math.Max(0, Math.Min(3, st.Binning - 1));
                    output.SelectedIndex = st.Output == GuideOutputKind.Camera ? 1 : 0; focal.Text = T("{0:0} mm, from the equipment profile (Observatory → Equipment)", st.FocalLengthMm); radius.Value = clamp(radius, st.SearchRadius);
                    solveOn.Checked = st.SolveGuideFrames;
                    multi.Checked = st.MultiStar; maxStars.Value = clamp(maxStars, st.MaxStars); ditherMode.SelectedIndex = st.DitherMode == DitherPattern.Spiral ? 1 : 0; mass.Checked = st.StarMassCheck; massTol.Value = clamp(massTol, st.StarMassTolerance);
                    predict.Checked = st.PredictCalibration;
                    step.Value = clamp(step, st.CalibrationStepMs); distance.Value = clamp(distance, st.CalibrationDistancePx); reverses.Checked = st.MountReversesDecAfterFlip;
                    raAlgo.SelectedIndex = Math.Max(0, Array.IndexOf(raKinds, st.RaAlgorithm));
                    raAggr.Value = clamp(raAggr, st.RaAggressiveness); raHyst.Value = clamp(raHyst, st.RaHysteresis); raLp.Value = clamp(raLp, st.RaLowpassAggressiveness); raZ.Value = clamp(raZ, st.RaZFactor);
                    raMin.Value = clamp(raMin, st.RaMinMovePx); raMax.Value = clamp(raMax, st.MaxRaPulseMs);
                    decAlgo.SelectedIndex = Math.Max(0, Array.IndexOf(decKinds, st.DecAlgorithm));
                    decAggr.Value = clamp(decAggr, st.DecAggressiveness); fast.Checked = st.DecFastSwitch; decLp.Value = clamp(decLp, st.DecLowpassAggressiveness); decZ.Value = clamp(decZ, st.DecZFactor);
                    decMin.Value = clamp(decMin, st.DecMinMovePx); decMax.Value = clamp(decMax, st.MaxDecPulseMs); decMode.SelectedIndex = Math.Max(0, Array.IndexOf(modes, st.DecMode));
                    backlash.Checked = st.BacklashCompensation; blAmount.Value = clamp(blAmount, st.BacklashMs); blMin.Value = clamp(blMin, st.BacklashMinMs); blMax.Value = clamp(blMax, st.BacklashMaxMs);
                    subframes.Checked = st.UseSubframes; noise.SelectedIndex = Math.Max(0, Math.Min(2, (int)st.NoiseReduction));
                    onRotator.Checked = st.GuideCameraOnRotator; rotReversed.Checked = st.RotatorReversed;
                    pecPred.Value = clamp(pecPred, st.PecPredictiveGain); pecReact.Value = clamp(pecReact, st.PecReactiveGain); pecPeriod.Value = clamp(pecPeriod, st.PecPeriodSec);
                    pecAuto.Checked = st.PecAutoPeriod; pecRetain.Value = clamp(pecRetain, st.PecRetainPercent);
                    aoTravel.Value = clamp(aoTravel, st.AoTravel); aoCalStep.Value = clamp(aoCalStep, st.AoCalStep); aoCalSteps.Value = clamp(aoCalSteps, st.AoCalSteps);
                    aoBump.Value = clamp(aoBump, st.AoBumpPercent); aoBumpStep.Value = clamp(aoBumpStep, st.AoBumpStepArcsec); aoBumpDither.Checked = st.AoBumpOnDither;
                    aoAggr.Value = clamp(aoAggr, st.AoAggressiveness); aoMin.Value = clamp(aoMin, st.AoMinMovePx);
                    srvOn.Checked = st.ServerEnabled; srvPort.Value = clamp(srvPort, st.ServerPort); srvOthers.Checked = st.ServerAllowOthers;
                }
                finally { loading = false; }
                layout();
            };
            // each control writes only its own setting, so settings changed elsewhere (a script, an assistant) are not put back
            Action<Action<GuiderSettings>> save = set =>
            {
                if (loading) return;
                var g = Dm.BuiltinGuider;
                set(g.Settings);
                g.SaveSettings();
                calText.Text = CalibrationText(g.Calibration);
                layout();
            };
            exposure.ValueChanged += (s, e) => save(st => st.ExposureSeconds = (double)exposure.Value);
            auto.CheckedChanged += (s, e) => save(st => st.AutoExposure = auto.Checked);
            autoMin.ValueChanged += (s, e) => save(st => st.AutoExposureMin = (double)autoMin.Value);
            autoMax.ValueChanged += (s, e) => save(st => st.AutoExposureMax = (double)autoMax.Value);
            target.ValueChanged += (s, e) => save(st => st.TargetSnr = (double)target.Value);
            binning.SelectedIndexChanged += (s, e) => save(st => st.Binning = binning.SelectedIndex + 1);
            output.SelectedIndexChanged += (s, e) => save(st => st.Output = output.SelectedIndex == 1 ? GuideOutputKind.Camera : GuideOutputKind.Mount);
            radius.ValueChanged += (s, e) => save(st => st.SearchRadius = (int)radius.Value);
            solveOn.CheckedChanged += (s, e) => save(st => st.SolveGuideFrames = solveOn.Checked);
            multi.CheckedChanged += (s, e) => save(st => st.MultiStar = multi.Checked);
            maxStars.ValueChanged += (s, e) => save(st => st.MaxStars = (int)maxStars.Value);
            ditherMode.SelectedIndexChanged += (s, e) => save(st => st.DitherMode = ditherMode.SelectedIndex == 1 ? DitherPattern.Spiral : DitherPattern.Random);
            mass.CheckedChanged += (s, e) => save(st => st.StarMassCheck = mass.Checked);
            massTol.ValueChanged += (s, e) => save(st => st.StarMassTolerance = (double)massTol.Value);
            step.ValueChanged += (s, e) => save(st => st.CalibrationStepMs = (int)step.Value);
            predict.CheckedChanged += (s, e) => save(st => st.PredictCalibration = predict.Checked);
            distance.ValueChanged += (s, e) => save(st => st.CalibrationDistancePx = (double)distance.Value);
            reverses.CheckedChanged += (s, e) => save(st => st.MountReversesDecAfterFlip = reverses.Checked);
            raAlgo.SelectedIndexChanged += (s, e) => save(st => st.RaAlgorithm = raKinds[Math.Max(0, raAlgo.SelectedIndex)]);
            raAggr.ValueChanged += (s, e) => save(st => st.RaAggressiveness = (double)raAggr.Value);
            raHyst.ValueChanged += (s, e) => save(st => st.RaHysteresis = (double)raHyst.Value);
            raLp.ValueChanged += (s, e) => save(st => st.RaLowpassAggressiveness = (double)raLp.Value);
            raZ.ValueChanged += (s, e) => save(st => st.RaZFactor = (double)raZ.Value);
            raMin.ValueChanged += (s, e) => save(st => st.RaMinMovePx = (double)raMin.Value);
            raMax.ValueChanged += (s, e) => save(st => st.MaxRaPulseMs = (int)raMax.Value);
            decAlgo.SelectedIndexChanged += (s, e) => save(st => st.DecAlgorithm = decKinds[Math.Max(0, decAlgo.SelectedIndex)]);
            decAggr.ValueChanged += (s, e) => save(st => st.DecAggressiveness = (double)decAggr.Value);
            fast.CheckedChanged += (s, e) => save(st => st.DecFastSwitch = fast.Checked);
            decLp.ValueChanged += (s, e) => save(st => st.DecLowpassAggressiveness = (double)decLp.Value);
            decZ.ValueChanged += (s, e) => save(st => st.DecZFactor = (double)decZ.Value);
            decMin.ValueChanged += (s, e) => save(st => st.DecMinMovePx = (double)decMin.Value);
            decMax.ValueChanged += (s, e) => save(st => st.MaxDecPulseMs = (int)decMax.Value);
            decMode.SelectedIndexChanged += (s, e) => save(st => st.DecMode = modes[Math.Max(0, decMode.SelectedIndex)]);
            backlash.CheckedChanged += (s, e) => save(st => st.BacklashCompensation = backlash.Checked);
            blAmount.ValueChanged += (s, e) => save(st => st.BacklashMs = (double)blAmount.Value);
            blMin.ValueChanged += (s, e) => save(st => st.BacklashMinMs = (double)blMin.Value);
            blMax.ValueChanged += (s, e) => save(st => st.BacklashMaxMs = (double)blMax.Value);
            clearCal.Click += (s, e) => Dm.BuiltinGuider.ClearCalibration();
            subframes.CheckedChanged += (s, e) => save(st => st.UseSubframes = subframes.Checked);
            noise.SelectedIndexChanged += (s, e) => save(st => st.NoiseReduction = (NoiseReduction)Math.Max(0, noise.SelectedIndex));
            onRotator.CheckedChanged += (s, e) => save(st => st.GuideCameraOnRotator = onRotator.Checked);
            rotReversed.CheckedChanged += (s, e) => save(st => st.RotatorReversed = rotReversed.Checked);
            pecPred.ValueChanged += (s, e) => save(st => st.PecPredictiveGain = (double)pecPred.Value);
            pecReact.ValueChanged += (s, e) => save(st => st.PecReactiveGain = (double)pecReact.Value);
            pecPeriod.ValueChanged += (s, e) => save(st => st.PecPeriodSec = (double)pecPeriod.Value);
            pecAuto.CheckedChanged += (s, e) => save(st => st.PecAutoPeriod = pecAuto.Checked);
            pecRetain.ValueChanged += (s, e) => save(st => st.PecRetainPercent = (double)pecRetain.Value);
            aoTravel.ValueChanged += (s, e) => save(st => st.AoTravel = (int)aoTravel.Value);
            aoCalStep.ValueChanged += (s, e) => save(st => st.AoCalStep = (int)aoCalStep.Value);
            aoCalSteps.ValueChanged += (s, e) => save(st => st.AoCalSteps = (int)aoCalSteps.Value);
            aoBump.ValueChanged += (s, e) => save(st => st.AoBumpPercent = (double)aoBump.Value);
            aoBumpStep.ValueChanged += (s, e) => save(st => st.AoBumpStepArcsec = (double)aoBumpStep.Value);
            aoBumpDither.CheckedChanged += (s, e) => save(st => st.AoBumpOnDither = aoBumpDither.Checked);
            aoAggr.ValueChanged += (s, e) => save(st => st.AoAggressiveness = (double)aoAggr.Value);
            aoMin.ValueChanged += (s, e) => save(st => st.AoMinMovePx = (double)aoMin.Value);
            aoClear.Click += (s, e) => { Dm.BuiltinGuider.ClearAoCalibration(); aoCalText.Text = AoCalibrationText(Dm.BuiltinGuider.AoCalibration); };
            // the server listens as set: started, stopped or moved to another port at once
            Action applyServer = () =>
            {
                var st = Dm.BuiltinGuider.Settings;
                var srv = Dm.GuiderServer;
                srv.Stop();
                if (st.ServerEnabled) srv.Start(st.ServerPort, st.ServerAllowOthers);
            };
            srvOn.CheckedChanged += (s, e) => { if (loading) return; save(st => st.ServerEnabled = srvOn.Checked); applyServer(); };
            srvPort.ValueChanged += (s, e) => { if (loading) return; save(st => st.ServerPort = (int)srvPort.Value); applyServer(); };
            srvOthers.CheckedChanged += (s, e) => { if (loading) return; save(st => st.ServerAllowOthers = srvOthers.Checked); applyServer(); };
            var sidralune = Dm.BuiltinGuider;
            sidralune.CalibrationChanged += () => MainForm.Instance.Post(() =>
            {
                if (!Dm.Phd2Connected) calText.Text = CalibrationText(sidralune.Calibration);
                aoCalText.Text = AoCalibrationText(sidralune.AoCalibration);
            });
            Poll += () =>
            {
                var g = Dm.BuiltinGuider;
                var solved = g.CurrentSolve;
                string solveNow = solved != null ? T("{0:0.000}″/px, angle {1:0.0}°, focal length {2:0} mm", solved.ScaleArcsec, solved.AngleDeg, solved.SolvedFocalMm)
                    : g.SolveMessage ?? T("Not solved yet");
                if (solveText.Text != solveNow) { solveText.Text = solveNow; fitSolve(); }
                blNow.Text = g.Settings.BacklashCompensation && g.InGuidingPhase ? T("Now {0:0} ms, as it has adapted.", g.BacklashCompensationMs) : "";
                var pec = g.PredictivePec;
                pecStatus.Text = g.Settings.RaAlgorithm != GuideAlgorithmKind.PredictivePec ? ""
                    : pec == null || !g.InGuidingPhase ? T("Starts learning when guiding starts.")
                    : pec.Predicting ? T("Predicting, period {0:0} s", pec.PeriodSec) : T("Training: {0:0.0} of 2 periods", pec.Training * 2);
                bool showAo = Dm.Ao != null;
                if (aoCard.Visible != showAo) { aoCard.Visible = showAo; aoCalText.Text = AoCalibrationText(g.AoCalibration); }
                var srv = Dm.GuiderServer;
                srvStatus.Text = srv.Running ? T("Listening on port {0} · {1} connected", srv.Port, srv.ClientCount)
                    : g.Settings.ServerEnabled && srv.Problem != null ? srv.Problem : T("Off");
                srvStatus.ForeColor = srv.Running ? Theme.Current.Success : g.Settings.ServerEnabled ? Theme.Current.Danger : Theme.Current.TextDim;
            };
            refresh();
            return page;
        }

        // ------------------------------------------------------------------ assistants

        private Control GuidingAssistantsPage(JobSlot guideJob, Action refreshSettings, out Action poll)
        {
            var page = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
            var job = new JobSlot();
            GuidingAssistant ga = null;
            GuidingAssistantResult shown = null;
            var gaGrid = new FormGrid(170);
            gaGrid.Full(Ui.Hint(T("While guiding, measures how the stars move with corrections paused: the seeing, the drift, RA's swing and the polar alignment error. Give it two minutes, longer for polar alignment.")));
            var backlashTest = gaGrid.Row(T("Afterwards"), new CheckBox { Text = T("Measure Dec backlash"), Checked = true });
            var gaStart = MakeButton(T("Start measuring"), Icons.Play);
            var gaStop = MakeButton(T("Stop and analyse"), Icons.Stop);
            gaGrid.Full(ButtonRow(gaStart, gaStop));
            var gaText = Note();
            gaGrid.Full(gaText);
            var recs = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
            gaGrid.Full(recs);
            var apply = MakeButton(T("Apply the ticked settings"), Icons.Check);
            apply.Visible = false;
            gaGrid.Full(ButtonRow(apply));

            var caGrid = new FormGrid(170);
            caGrid.Full(Ui.Hint(T("Calibrates near the meridian and the celestial equator, where calibration is most reliable, on the side the mount points to now, then rates the calibration.")));
            var caHa = caGrid.Row(T("Hour angle (h)"), Ui.Num(0, -6, 6, 2, 0.1m));
            var caDec = caGrid.Row(T("Declination (°)"), Ui.Num(0, -85, 85, 0, 1));
            var caWhere = Note(true);
            caGrid.Full(caWhere);
            var caChecks = Note();
            caGrid.Full(caChecks);
            var caRun = MakeButton(T("Slew and calibrate"), Icons.Target);
            var caReturn = MakeButton(T("Return"), Icons.Undo);
            var caSuggest = MakeButton(T("Suggest again"), Icons.Refresh);
            caGrid.Full(ButtonRow(caRun, caReturn, caSuggest));
            var caResult = Note();
            caGrid.Full(caResult);
            page.Controls.Add(new Collapsible(T("Calibration Assistant"), caGrid));
            page.Controls.Add(new Collapsible(T("Guiding Assistant"), gaGrid));

            Func<GuidingAssistantResult, string> describe = r =>
            {
                if (r == null || r.Frames < 3) return r == null ? "" : T("Measuring… {0} frames so far.", r.Frames);
                var lines = new List<string>
                {
                    T("{0} frames over {1:0} s.", r.Frames, r.Seconds),
                    T("Seeing: RA {0:0.00}″, Dec {1:0.00}″ RMS.", r.SeeingRaArcsec, r.SeeingDecArcsec),
                    T("Drift: RA {0:0.00}″, Dec {1:0.00}″ a minute.", r.DriftRaArcsecPerMin, r.DriftDecArcsecPerMin),
                    T("RA swing {0:0.0}″ peak to peak, changing up to {1:0.00}″ a second.", r.RaPeakToPeakArcsec, r.RaMaxRateArcsecPerSec),
                    T("Polar alignment off by at least {0:0.0}′.", r.PolarErrorArcmin)
                };
                if (r.BacklashMeasured)
                    lines.Add(!string.IsNullOrEmpty(r.BacklashProblem) ? T("Dec backlash: {0}", r.BacklashProblem) : T("Dec backlash {0:0} ± {1:0} ms.", r.BacklashMs, r.BacklashUncertaintyMs));
                return string.Join(Environment.NewLine, lines);
            };
            var recList = new List<KeyValuePair<CheckBox, GuidingRecommendation>>();
            Action<GuidingAssistantResult> showResult = r =>
            {
                shown = r;
                gaText.Text = describe(r);
                recs.Controls.Clear();
                recList.Clear();
                if (r == null || r.Frames < 3) { apply.Visible = false; return; }
                foreach (var rec in GuidingAssistant.Recommend(r, Dm.BuiltinGuider.Settings))
                {
                    var cb = new CheckBox { Text = rec.Text, Checked = rec.Apply != null, Enabled = rec.Apply != null, AutoSize = true, MaximumSize = new Size(330, 0), UseMnemonic = false };
                    Theme.Apply(cb);
                    recs.Controls.Add(cb);
                    recList.Add(new KeyValuePair<CheckBox, GuidingRecommendation>(cb, rec));
                }
                apply.Visible = recList.Any(p => p.Value.Apply != null);
            };
            gaStart.Click += (s, e) => Try(() =>
            {
                var g = Dm.BuiltinGuider;
                if (Dm.Phd2Connected) throw new InvalidOperationException(T("PHD2 is connected: it has its own Guiding Assistant."));
                if (!PolarFree()) return;
                ga = new GuidingAssistant(g);
                ga.Start();
                showResult(null);
                gaText.Text = describe(ga.Current);
            });
            gaStop.Click += (s, e) =>
            {
                var a = ga;
                if (a == null || !a.Measuring) return;
                GuidingAssistantResult result = null;
                job.Run(T("Guiding Assistant"), t => result = a.Stop(backlashTest.Checked, t.Cts.Token).GetAwaiter().GetResult(),
                    () => { showResult(result); MainForm.Instance.Toast(T("Guiding Assistant finished")); });
            };
            apply.Click += (s, e) =>
            {
                var g = Dm.BuiltinGuider;
                int n = 0;
                foreach (var p in recList) if (p.Key.Checked && p.Value.Apply != null) { p.Value.Apply(g.Settings); n++; }
                if (n == 0) return;
                g.SaveSettings();
                refreshSettings();
                MainForm.Instance.Toast(T("Settings applied."));
            };

            CalibrationAssistant ca = null;
            bool edited = false, proposing = false;
            Func<CalibrationAssistant> assistant = () => ca ?? (ca = new CalibrationAssistant(Dm, Dm.BuiltinGuider));
            caHa.ValueChanged += (s, e) => { if (!proposing) edited = true; };
            caDec.ValueChanged += (s, e) => { if (!proposing) edited = true; };
            caSuggest.Click += (s, e) => { edited = false; };
            caRun.Click += (s, e) =>
            {
                if (!PolarFree()) return;
                var a = assistant();
                a.TargetHaHours = (double)caHa.Value; a.TargetDecDeg = (double)caDec.Value;
                caResult.Text = "";
                job.Run(T("Calibration Assistant"), t => a.Run(t.Cts.Token).GetAwaiter().GetResult(), () =>
                {
                    var c = Dm.BuiltinGuider.Calibration;
                    var rating = CalibrationAssistant.Rate(c);
                    string word = rating == CalibrationRating.Good ? T("Good") : rating == CalibrationRating.Acceptable ? T("Acceptable") : T("Poor");
                    caResult.Text = T("Calibration: {0}.", word) + Environment.NewLine + CalibrationText(c);
                });
            };
            caReturn.Click += (s, e) => { var a = assistant(); job.Run(T("Return"), t => a.Return(t.Cts.Token).GetAwaiter().GetResult()); };

            poll = () =>
            {
                var g = Dm.BuiltinGuider;
                bool phd2 = Dm.Phd2Connected, busy = job.Busy || guideJob.Busy;
                // the assistant goes when guiding stops under it
                if (ga != null && ga.Measuring && !g.InGuidingPhase) { ga.Cancel(); gaText.Text = T("Guiding stopped, so the Guiding Assistant did too."); }
                bool measuring = ga != null && ga.Measuring;
                gaStart.Enabled = !phd2 && !busy && !measuring && g.InGuidingPhase && g.Calibration != null && PolarBusyReason() == null;
                gaStop.Enabled = measuring && !job.Busy;
                // settings are read when the work starts: they stay editable while it runs
                backlashTest.Enabled = !phd2;
                apply.Enabled = !phd2 && !job.Busy;
                if (measuring) gaText.Text = describe(ga.Current);
                else if (shown == null && ga == null && g.Settings.AssistantResults.Count > 0) showResult(g.Settings.AssistantResults[0]);

                var tel = Dm.Telescope;
                bool mount = tel != null && tel.Connected;
                caRun.Enabled = !phd2 && !busy && mount && Dm.GuideCamera != null && !g.CameraInUse && PolarBusyReason() == null;
                caReturn.Enabled = !phd2 && !busy && mount && ca != null && !double.IsNaN(ca.StartRa);
                caSuggest.Enabled = caHa.Enabled = caDec.Enabled = !phd2;
                if (!mount) { caWhere.Text = T("Connect a telescope: the assistant slews it."); caChecks.Text = ""; return; }
                try
                {
                    var a = assistant();
                    if (!edited && !job.Busy)
                    {
                        a.Propose();
                        proposing = true;
                        try { caHa.Value = Math.Max(caHa.Minimum, Math.Min(caHa.Maximum, (decimal)a.TargetHaHours)); caDec.Value = Math.Max(caDec.Minimum, Math.Min(caDec.Maximum, (decimal)a.TargetDecDeg)); }
                        finally { proposing = false; }
                    }
                    double alt = CalibrationAssistant.Altitude((double)caHa.Value, (double)caDec.Value, Dm.Site.LatitudeDeg);
                    caWhere.Text = ((double)caHa.Value >= 0 ? T("West of the meridian, {0:0}° up.", alt) : T("East of the meridian, {0:0}° up.", alt));
                    var checks = a.Checks();
                    caChecks.Text = string.Join(Environment.NewLine, checks.Select(c => "⚠ " + c));
                }
                catch (Exception ex) { caChecks.Text = ex.Message; }
            };
            return page;
        }

        // ------------------------------------------------------------------ dark frames

        private Control GuidingDarksPage(out Action poll)
        {
            var page = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
            var job = new JobSlot();
            var grid = new FormGrid(170);
            var use = grid.Row(T("Use"), Ui.Combo(T("Nothing"), T("The dark library"), T("The bad-pixel map")));
            grid.Full(Ui.Hint(T("A dark library subtracts a master dark from each guide frame; a bad-pixel map mends the hot and cold pixels. Either removes hot pixels that could be taken for stars.")));
            var status = Note();
            grid.Full(status);

            var libGrid = new FormGrid(170);
            var frames = libGrid.Row(T("Dark frames each"), Ui.Num(5, 1, 20));
            var exposures = Note(true);
            libGrid.Full(exposures);
            var buildLib = MakeButton(T("Build dark library"), Icons.Layers);
            libGrid.Full(ButtonRow(buildLib));

            var mapGrid = new FormGrid(170);
            var mapExposure = mapGrid.Row(T("Exposure (s)"), Ui.Num(10, 1, 60));
            var mapFrames = mapGrid.Row(T("Dark frames"), Ui.Num(10, 3, 30));
            var sigma = mapGrid.Row(T("Threshold (σ)"), Ui.Num(5, 3, 20, 1, 0.5m));
            var buildMap = MakeButton(T("Build bad-pixel map"), Icons.Grid);
            mapGrid.Full(ButtonRow(buildMap));

            // the guide scope must be covered first: asked here, so the palette holds at night
            var cover = new FormGrid(170);
            var coverText = Note();
            coverText.Text = T("Cover the guide scope (or close the camera's shutter), then click Continue.");
            cover.Full(coverText);
            var go = MakeButton(T("Continue"), Icons.Check);
            var cancel = MakeButton(T("Cancel"), Icons.Close);
            cover.Full(ButtonRow(go, cancel));
            var progress = Note(true);
            var stop = job.StopButton();
            cover.Full(progress);
            cover.Full(stop);
            var coverCard = new Collapsible(T("Taking dark frames"), cover);

            page.Controls.Add(new Collapsible(T("Bad-pixel map"), mapGrid));
            page.Controls.Add(new Collapsible(T("Dark library"), libGrid));
            page.Controls.Add(coverCard);
            page.Controls.Add(new Collapsible(T("Dark frames"), grid));

            Func<double[]> libraryExposures = () =>
            {
                var st = Dm.BuiltinGuider.Settings;
                return st.AutoExposure ? SidraluneGuider.AutoExposureSteps(st.AutoExposureMin, st.AutoExposureMax) : new[] { Math.Max(0.05, st.ExposureSeconds) };
            };
            Action pending = null;
            Action<Action> ask = build => { pending = build; go.Visible = cancel.Visible = coverText.Visible = true; coverCard.Visible = true; };
            Action hideCover = () => { pending = null; go.Visible = cancel.Visible = coverText.Visible = false; coverCard.Visible = job.Busy; };
            go.Click += (s, e) => { var b = pending; hideCover(); coverCard.Visible = true; b?.Invoke(); };
            cancel.Click += (s, e) => hideCover();
            buildLib.Click += (s, e) => Try(() =>
            {
                var g = Dm.BuiltinGuider;
                if (Dm.GuideCamera == null) throw new InvalidOperationException(T("No guide camera connected."));
                if (g.IsRunning) throw new InvalidOperationException(T("Stop the guider before taking dark frames."));
                var list = libraryExposures();
                int each = (int)frames.Value;
                ask(() => job.Run(T("Dark library"), t =>
                {
                    var p = new Progress<double>(v => t.Progress = v);
                    g.BuildDarkLibrary(list, each, p, t.Cts.Token).GetAwaiter().GetResult();
                }, () => MainForm.Instance.Toast(T("Dark library built. Uncover the guide scope."))));
            });
            buildMap.Click += (s, e) => Try(() =>
            {
                var g = Dm.BuiltinGuider;
                if (Dm.GuideCamera == null) throw new InvalidOperationException(T("No guide camera connected."));
                if (g.IsRunning) throw new InvalidOperationException(T("Stop the guider before taking dark frames."));
                double exp = (double)mapExposure.Value, sg = (double)sigma.Value;
                int n = (int)mapFrames.Value;
                BadPixelMapInfo built = null;
                ask(() => job.Run(T("Bad-pixel map"), t =>
                {
                    var p = new Progress<double>(v => t.Progress = v);
                    built = g.BuildBadPixelMap(exp, n, sg, p, t.Cts.Token).GetAwaiter().GetResult();
                }, () => MainForm.Instance.Toast(T("Bad-pixel map built: {0} hot and {1} cold pixels. Uncover the guide scope.", built.Hot, built.Cold))));
            });
            bool loading = false;
            use.SelectedIndexChanged += (s, e) =>
            {
                if (loading) return;
                var g = Dm.BuiltinGuider;
                g.Settings.DarkFrames = use.SelectedIndex == 1 ? DarkFrameKind.DarkLibrary : use.SelectedIndex == 2 ? DarkFrameKind.BadPixelMap : DarkFrameKind.None;
                g.SaveSettings();
            };
            frames.ValueChanged += (s, e) => { if (!loading) { Dm.BuiltinGuider.Settings.DarkFrameCount = (int)frames.Value; Dm.BuiltinGuider.SaveSettings(); } };
            sigma.ValueChanged += (s, e) => { if (!loading) { Dm.BuiltinGuider.Settings.BadPixelSigma = (double)sigma.Value; Dm.BuiltinGuider.SaveSettings(); } };
            loading = true;
            try
            {
                var st0 = Dm.BuiltinGuider.Settings;
                use.SelectedIndex = st0.DarkFrames == DarkFrameKind.DarkLibrary ? 1 : st0.DarkFrames == DarkFrameKind.BadPixelMap ? 2 : 0;
                frames.Value = Math.Max(frames.Minimum, Math.Min(frames.Maximum, st0.DarkFrameCount));
                sigma.Value = Math.Max(sigma.Minimum, Math.Min(sigma.Maximum, (decimal)st0.BadPixelSigma));
            }
            finally { loading = false; }
            hideCover();
            job.Changed += () => { if (!job.Busy) hideCover(); };

            poll = () =>
            {
                var g = Dm.BuiltinGuider;
                bool phd2 = Dm.Phd2Connected, camera = Dm.GuideCamera != null;
                // read when a build starts: they stay editable while one runs
                use.Enabled = frames.Enabled = mapExposure.Enabled = mapFrames.Enabled = sigma.Enabled = !phd2;
                buildLib.Enabled = buildMap.Enabled = !phd2 && !job.Busy && camera && !g.IsRunning && pending == null;
                go.Enabled = !job.Busy;
                exposures.Text = T("Exposures: {0} s", string.Join(", ", libraryExposures().Select(x => x.ToString("0.0#"))));
                progress.Text = job.Busy && !double.IsNaN(job.Current.Progress) ? T("{0}: {1:0} %", job.Name, job.Current.Progress * 100) : "";
                stop.Visible = job.Busy;
                if (!camera) { status.Text = T("Connect a guide camera to build or use dark frames."); return; }
                var setup = g.SetupNow();
                if (setup == null) { status.Text = ""; return; }
                g.Darks.Load(setup.Camera);
                var lib = g.Darks.Library;
                var map = g.Darks.Map;
                string libText = lib == null || lib.Exposures.Count == 0 ? T("Dark library: none yet.")
                    : T("Dark library: {0} s, {1} frames each, {2:yyyy-MM-dd}.", string.Join(", ", lib.Exposures.Select(x => x.ToString("0.0#"))), lib.FramesEach, lib.When.ToLocalTime())
                      + " " + (g.Darks.LibraryFits(setup) ? T("It fits the guide camera.") : T("It doesn't fit the guide camera as set up now; build it again."));
                string mapText = map == null ? T("Bad-pixel map: none yet.")
                    : T("Bad-pixel map: {0} hot and {1} cold pixels, {2:yyyy-MM-dd}.", map.Hot, map.Cold, map.When.ToLocalTime())
                      + " " + (g.Darks.MapFits(setup) ? T("It fits the guide camera.") : T("It doesn't fit the guide camera as set up now; build it again."));
                status.Text = libText + Environment.NewLine + mapText;
            };
            return page;
        }

        // ------------------------------------------------------------------ tools

        private Control GuidingToolsPage(out Action poll)
        {
            var page = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
            var job = new JobSlot();

            var lockGrid = new FormGrid(170);
            var lockX = lockGrid.Row(T("Lock position X (px)"), Ui.Num(0, 0, 20000, 2, 0.5m));
            var lockY = lockGrid.Row(T("Lock position Y (px)"), Ui.Num(0, 0, 20000, 2, 0.5m));
            var setLock = MakeButton(T("Set"), Icons.Check);
            lockGrid.Full(ButtonRow(setLock));
            lockGrid.Full(Ui.Hint(T("Or right-click the frame: Set lock position here.")));
            var sticky = lockGrid.Row(T("Sticky lock position"), new CheckBox { Text = T("Keep it through stops") });
            var nudge = lockGrid.Row(T("Move by (″)"), Ui.Num(1, 0.1m, 60, 1, 0.5m));
            var north = MakeButton(T("North"), Icons.Up); var south = MakeButton(T("South"), Icons.Down);
            var east = MakeButton(T("East"), Icons.Left); var west = MakeButton(T("West"), Icons.Right);
            lockGrid.Full(ButtonRow(north, south, east, west));
            lockGrid.Full(Ui.Hint(T("Moves the telescope that far on the sky, through the calibration; without one, that many pixels in the frame.")));

            var cometGrid = new FormGrid(170);
            var source = cometGrid.Row(T("Rates from"), Ui.Combo(T("Entered rates"), T("A comet's orbit"), T("Training")));
            var cometRa = cometGrid.Row(T("RA (″/h, on the sky)"), Ui.Num(0, -100000, 100000, 1, 10));
            var cometDec = cometGrid.Row(T("Dec (″/h)"), Ui.Num(0, -100000, 100000, 1, 10));
            var comet = cometGrid.Row(T("Comet"), new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList });
            var follow = cometGrid.Row(T("Comet tracking"), new CheckBox { Text = T("Follow the comet") });
            var trainStart = MakeButton(T("Start training"), Icons.Play);
            var trainStop = MakeButton(T("Stop training"), Icons.Stop);
            cometGrid.Full(ButtonRow(trainStart, trainStop));
            var cometText = Note(true);
            cometGrid.Full(cometText);

            var manualGrid = new FormGrid(170);
            var pulse = manualGrid.Row(T("Pulse (ms)"), Ui.Num(1000, 10, 10000, 0, 100));
            var mNorth = MakeButton(T("North"), Icons.Up); var mSouth = MakeButton(T("South"), Icons.Down);
            var mEast = MakeButton(T("East"), Icons.Left); var mWest = MakeButton(T("West"), Icons.Right);
            manualGrid.Full(ButtonRow(mNorth, mSouth, mEast, mWest));
            var manualText = Note();
            manualGrid.Full(manualText);
            manualGrid.Full(Ui.Hint(T("While looping: one pulse, and how far the star moved, to check the guide cable and the guide rate.")));
            var aoSteps = manualGrid.Row(T("AO steps"), Ui.Num(5, 1, 100));
            var aNorth = MakeButton(T("North"), Icons.Up); var aSouth = MakeButton(T("South"), Icons.Down);
            var aEast = MakeButton(T("East"), Icons.Left); var aWest = MakeButton(T("West"), Icons.Right);
            var aoButtons = ButtonRow(aNorth, aSouth, aEast, aWest);
            manualGrid.Full(aoButtons);
            var aoHint = Ui.Hint(T("The AO moved by hand the same way, along its own axes, to check it."));
            manualGrid.Full(aoHint);

            page.Controls.Add(new Collapsible(T("Manual guide"), manualGrid));
            page.Controls.Add(new Collapsible(T("Comet tracking"), cometGrid));
            page.Controls.Add(new Collapsible(T("Lock position"), lockGrid));

            var sources = new[] { CometRateSource.Entered, CometRateSource.Comet, CometRateSource.Trained };
            bool loading = false;
            Action loadComet = null;
            Action<Action<GuiderSettings>> save = set => { if (loading) return; var g = Dm.BuiltinGuider; set(g.Settings); g.SaveSettings(); };
            setLock.Click += (s, e) => Try(() => Dm.BuiltinGuider.SetLockPosition((double)lockX.Value, (double)lockY.Value));
            sticky.CheckedChanged += (s, e) => save(st => st.StickyLock = sticky.Checked);
            nudge.ValueChanged += (s, e) => save(st => st.NudgeArcsec = (double)nudge.Value);
            foreach (var p in new[] { new KeyValuePair<FlatButton, GuideDirection>(north, GuideDirection.North), new KeyValuePair<FlatButton, GuideDirection>(south, GuideDirection.South),
                                      new KeyValuePair<FlatButton, GuideDirection>(east, GuideDirection.East), new KeyValuePair<FlatButton, GuideDirection>(west, GuideDirection.West) })
            {
                var dir = p.Value;
                p.Key.Click += (s, e) => Try(() => Dm.BuiltinGuider.NudgeLock(dir));
            }
            source.SelectedIndexChanged += (s, e) => save(st => st.CometSource = sources[Math.Max(0, source.SelectedIndex)]);
            cometRa.ValueChanged += (s, e) => save(st => st.CometRaArcsecPerHour = (double)cometRa.Value);
            cometDec.ValueChanged += (s, e) => save(st => st.CometDecArcsecPerHour = (double)cometDec.Value);
            comet.SelectedIndexChanged += (s, e) => { if (comet.SelectedItem != null) save(st => st.CometName = comet.SelectedItem.ToString()); };
            follow.CheckedChanged += (s, e) =>
            {
                if (loading) return;
                var g = Dm.BuiltinGuider;
                try { g.FollowComet(follow.Checked); }
                catch (Exception ex) { MainForm.Instance.Toast(ex.Message, Icons.Warning); loading = true; follow.Checked = false; loading = false; }
            };
            trainStart.Click += (s, e) => Try(() => Dm.BuiltinGuider.StartCometTraining());
            trainStop.Click += (s, e) => Try(() => { Dm.BuiltinGuider.StopCometTraining(); loadComet(); });
            foreach (var p in new[] { new KeyValuePair<FlatButton, GuideDirection>(mNorth, GuideDirection.North), new KeyValuePair<FlatButton, GuideDirection>(mSouth, GuideDirection.South),
                                      new KeyValuePair<FlatButton, GuideDirection>(mEast, GuideDirection.East), new KeyValuePair<FlatButton, GuideDirection>(mWest, GuideDirection.West) })
            {
                var dir = p.Value;
                p.Key.Click += (s, e) =>
                {
                    if (!PolarFree()) return;
                    var g = Dm.BuiltinGuider;
                    int ms = (int)pulse.Value;
                    ManualPulseResult r = null;
                    job.Run(T("Manual guide"), t => r = g.ManualPulse(dir, ms, t.Cts.Token).GetAwaiter().GetResult(), () =>
                    {
                        if (double.IsNaN(r.Distance)) { manualText.Text = T("Pulse sent. Choose a guide star to see how far it moves."); return; }
                        string moved = T("The star moved {0:0.0} px towards {1:0}°.", r.Distance, r.AngleDeg);
                        if (r.Matches == null) manualText.Text = moved;
                        else manualText.Text = moved + " " + (r.Matches == true ? T("As calibrated ({0:0.0} px towards {1:0}°).", r.ExpectedDistance, r.ExpectedAngleDeg)
                                                                              : T("⚠ The calibration expected {0:0.0} px towards {1:0}°. Check the guide cable, the guide rate and Dec backlash.", r.ExpectedDistance, r.ExpectedAngleDeg));
                    });
                };
            }

            foreach (var p in new[] { new KeyValuePair<FlatButton, AoDirection>(aNorth, AoDirection.North), new KeyValuePair<FlatButton, AoDirection>(aSouth, AoDirection.South),
                                      new KeyValuePair<FlatButton, AoDirection>(aEast, AoDirection.East), new KeyValuePair<FlatButton, AoDirection>(aWest, AoDirection.West) })
            {
                var dir = p.Value;
                p.Key.Click += (s, e) =>
                {
                    var g = Dm.BuiltinGuider;
                    int steps = (int)aoSteps.Value;
                    ManualPulseResult r = null;
                    job.Run(T("Manual guide"), t => r = g.ManualAoStep(dir, steps, t.Cts.Token).GetAwaiter().GetResult(), () =>
                    {
                        if (double.IsNaN(r.Distance)) { manualText.Text = T("AO moved. Choose a guide star to see how far it moves."); return; }
                        string moved = T("The star moved {0:0.0} px towards {1:0}°.", r.Distance, r.AngleDeg);
                        if (r.Matches == null) manualText.Text = moved;
                        else manualText.Text = moved + " " + (r.Matches == true ? T("As calibrated ({0:0.0} px towards {1:0}°).", r.ExpectedDistance, r.ExpectedAngleDeg)
                                                                              : T("⚠ The AO calibration expected {0:0.0} px towards {1:0}°. Calibrate the AO again.", r.ExpectedDistance, r.ExpectedAngleDeg));
                    });
                };
            }

            // the comet list, the rates and the settings as they are
            Action loadCometList = () =>
            {
                var names = MinorBodyStore.Instance.Comets.Select(c => c.Name).Where(n => !string.IsNullOrEmpty(n)).Distinct().OrderBy(n => n).ToList();
                if (names.Count == comet.Items.Count) return;
                comet.Items.Clear();
                foreach (var n in names) comet.Items.Add(n);
            };
            loadComet = () =>
            {
                var st = Dm.BuiltinGuider.Settings;
                loading = true;
                try
                {
                    sticky.Checked = st.StickyLock; nudge.Value = Math.Max(nudge.Minimum, Math.Min(nudge.Maximum, (decimal)st.NudgeArcsec));
                    source.SelectedIndex = Math.Max(0, Array.IndexOf(sources, st.CometSource));
                    cometRa.Value = Math.Max(cometRa.Minimum, Math.Min(cometRa.Maximum, (decimal)st.CometRaArcsecPerHour));
                    cometDec.Value = Math.Max(cometDec.Minimum, Math.Min(cometDec.Maximum, (decimal)st.CometDecArcsecPerHour));
                    loadCometList();
                    int i = comet.Items.IndexOf(st.CometName);
                    if (i >= 0) comet.SelectedIndex = i;
                    pulse.Value = Math.Max(pulse.Minimum, Math.Min(pulse.Maximum, st.ManualPulseMs));
                }
                finally { loading = false; }
            };
            pulse.ValueChanged += (s, e) => save(st => st.ManualPulseMs = (int)pulse.Value);
            loadComet();

            poll = () =>
            {
                var g = Dm.BuiltinGuider;
                bool phd2 = Dm.Phd2Connected, star = g.IsRunning && g.Star != null, guiding = g.InGuidingPhase;
                // the lock position as it moves, unless being typed in
                if (!double.IsNaN(g.LockX) && !lockX.Focused && !lockY.Focused)
                {
                    lockX.Value = Math.Max(lockX.Minimum, Math.Min(lockX.Maximum, (decimal)g.LockX));
                    lockY.Value = Math.Max(lockY.Minimum, Math.Min(lockY.Maximum, (decimal)g.LockY));
                }
                lockX.Enabled = lockY.Enabled = setLock.Enabled = !phd2 && star && !g.IsCalibrating;
                foreach (var b in new Control[] { north, south, east, west }) b.Enabled = !phd2 && star && !g.IsCalibrating;
                sticky.Enabled = nudge.Enabled = !phd2;
                var src = sources[Math.Max(0, source.SelectedIndex)];
                ShowRow(cometGrid, comet, src == CometRateSource.Comet);
                cometRa.Enabled = cometDec.Enabled = !phd2 && src != CometRateSource.Comet;
                source.Enabled = !phd2;
                comet.Enabled = !phd2 && comet.Items.Count > 0;
                if (src == CometRateSource.Comet) loadCometList();
                if (follow.Checked != g.CometFollowing) { loading = true; follow.Checked = g.CometFollowing; loading = false; }
                follow.Enabled = !phd2 && guiding;
                trainStart.Enabled = !phd2 && guiding && !g.CometTraining;
                trainStop.Enabled = !phd2 && g.CometTraining;
                cometText.Text = g.CometTraining ? T("Training: keep the comet centred in the imaging camera by moving the lock position.")
                    : g.CometFollowing ? T("Following at RA {0:0.0}″/h and Dec {1:0.0}″/h.", g.CometRaRate, g.CometDecRate)
                    : src == CometRateSource.Comet && comet.Items.Count == 0 ? T("No comets yet: download the comet data in Settings.")
                    : T("While guiding, the lock position follows the comet, so the comet stays put in the imaging camera.");
                bool looping = g.IsRunning && !g.IsCalibrating && !guiding;
                foreach (var b in new Control[] { mNorth, mSouth, mEast, mWest }) b.Enabled = !phd2 && looping && !job.Busy && PolarBusyReason() == null;
                bool ao = Dm.Ao != null;
                ShowRow(manualGrid, aoSteps, ao);
                aoButtons.Visible = aoHint.Visible = ao;
                foreach (var b in new Control[] { aNorth, aSouth, aEast, aWest }) b.Enabled = !phd2 && looping && !job.Busy;
                pulse.Enabled = !phd2;
            };
            return page;
        }

    }
}
