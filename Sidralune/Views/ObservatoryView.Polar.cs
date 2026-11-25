using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading;
using System.Windows.Forms;
using Sidralune.Devices;
using Sidralune.Devices.Automation;
using Sidralune.Devices.Guiding;
using Sidralune.Imaging;
using Sidralune.UI;
using static Sidralune.Core.Util.Lang;

namespace Sidralune.Views
{
    /// <summary>
    /// The Polar alignment tab: three-point alignment with the imaging camera and plate solving, drift alignment with the
    /// guide camera through the Sidralune guider, and static and polar drift alignment with either camera.
    /// </summary>
    public partial class ObservatoryView
    {
        private ComboBox _polarMethod;

        private Control BuildPolar()
        {
            var panel = new Panel { Dock = DockStyle.Fill };
            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(0, 0, 0, 6) };
            bar.Controls.Add(new Label { Text = T("Method"), AutoSize = true, Margin = new Padding(0, 7, 8, 0), Font = Theme.UiFontBold });
            var method = Ui.Combo(T("Three-point (plate solving)"), T("Drift alignment"), T("Static polar alignment"), T("Polar drift alignment"));
            method.Width = 280;
            _polarMethod = method;
            bar.Controls.Add(method);
            var body = new Panel { Dock = DockStyle.Fill };
            var pages = new[] { new Panel(), new Panel(), new Panel(), new Panel() };
            foreach (var p in pages) { p.Dock = DockStyle.Fill; p.Visible = false; body.Controls.Add(p); }
            pages[0].Controls.Add(ThreePointPage());
            pages[1].Controls.Add(DriftAlignmentPage(pages[1]));
            pages[2].Controls.Add(StaticAlignmentPage(pages[2]));
            pages[3].Controls.Add(PolarDriftPage(pages[3]));
            pages[0].Visible = true;
            method.SelectedIndex = 0;
            method.SelectedIndexChanged += (s, e) => { for (int i = 0; i < pages.Length; i++) pages[i].Visible = i == method.SelectedIndex; };
            panel.Controls.Add(body);
            panel.Controls.Add(bar);
            return panel;
        }

        private Control ThreePointPage()
        {
            var card = new Card(T("Three-point polar alignment")) { Dock = DockStyle.Fill };
            var g = new FormGrid(160);
            var step = g.Row(T("RA step (°)"), Ui.Num(25, 5, 60));
            var exp = g.Row(T("Exposure (s)"), Ui.Num(4, 0, 60, 1));
            var res = new Label { Height = 120, Font = Theme.HeaderFont, AutoSize = false };
            var go = new FlatButton(T("Start polar alignment"), Icons.Target) { Kind = ButtonKind.Primary }; go.AutoSizeToText();
            var stop = new FlatButton(T("Stop"), Icons.Stop) { Kind = ButtonKind.Danger, Enabled = false }; stop.AutoSizeToText();
            Func<bool> running = () => _cameraJob != null && !_cameraJob.Done && _cameraJobName == T("Polar alignment");
            stop.Click += (s, e) => { if (running()) _cameraJob.Cts.Cancel(); };
            Poll += () => stop.Enabled = running();
            go.Click += (s, e) =>
            {
                if (!Have(Dm.Telescope, NoTelescope) || !Have(Dm.Camera, NoCamera) || !CanUseCamera() || !PointingFits(false)) return;
                var pa = new PolarAlignment { Capture = _capture, StepDeg = (double)step.Value, Exposure = (double)exp.Value };
                pa.Status += st => MainForm.Instance.SetStatus(st);
                RunCameraJob(T("Polar alignment"), t =>
                {
                    var r = pa.Run(t.Cts.Token).GetAwaiter().GetResult();
                    MainForm.Instance.Post(() => res.Text = AlignmentText(r));
                });
            };
            g.Full(Ui.Hint(T("Point the mount near the celestial equator/meridian with clear sky to the west or east. Sidralune rotates the RA axis three times, plate-solves each frame, finds the mount's rotation axis and reports how far it is from the refracted celestial pole. Works without a view of Polaris.")));
            g.Full(ButtonRow(go, stop));
            _cameraButtons.Add(Tuple.Create((Control)go, false));
            g.Full(res);
            card.Controls.Add(g);
            return card;
        }

        private static string AlignmentText(PolarAlignmentResult r) =>
            T("Altitude error {0:+0.0;-0.0}'   Azimuth error {1:+0.0;-0.0}'", r.AltErrorArcmin, r.AzErrorArcmin) + "\r\n" + T("Total {0:0.0}'", r.TotalErrorArcmin) + "\r\n" + r.Advice;

        /// <summary>
        /// Whether the mount points where a method needs it (near the celestial pole, or away from it), saying where to point
        /// when not. Without a mount there is nothing to check.
        /// </summary>
        private bool PointingFits(bool nearPole)
        {
            var tel = Dm.Telescope;
            double dec;
            try { if (tel == null || !tel.Connected) return true; dec = Math.Abs(tel.Declination); } catch { return true; }
            if (nearPole && dec < 70) { MainForm.Instance.Toast(T("Point the mount within 20° of the celestial pole first."), Icons.Info); return false; }
            if (!nearPole && dec > 60) { MainForm.Instance.Toast(T("Point the mount nearer the celestial equator first (Dec within ±60°)."), Icons.Info); return false; }
            return true;
        }

        /// <summary>The guide camera can be used for alignment: connected, the Sidralune guider's (not PHD2's), and not guiding.</summary>
        private bool GuideCameraFree(bool guidingAllowed = false)
        {
            if (Dm.Phd2Connected) { MainForm.Instance.Toast(T("PHD2 is connected: these methods use Sidralune's own guider."), Icons.Warning); return false; }
            if (!Have(Dm.GuideCamera, T("No guide camera connected."))) return false;
            if (!guidingAllowed && (Dm.BuiltinGuider.InGuidingPhase || Dm.BuiltinGuider.IsCalibrating)) { MainForm.Instance.Toast(T("Stop guiding first: the mount only tracks for this method."), Icons.Warning); return false; }
            return true;
        }

        /// <summary>
        /// Where a static or polar drift alignment page takes its frames: the imaging camera (short exposures taken as a
        /// camera job, so nothing else uses the camera meanwhile and the camera tabs' Stop ends them) or the guide camera
        /// (the Sidralune guider's loop).
        /// </summary>
        private sealed class PolarSource
        {
            public ComboBox Choice;
            public NumericUpDown Exposure, Bin;
            public CameraFrames Camera;
            public GuiderFrames Guider;
            public string JobName;
            /// <summary>The camera was chosen by hand (else the imaging camera when one is connected, the guide camera otherwise).</summary>
            public bool UserChose;
            /// <summary>Raised when the choice changes: what the page made with the other source is dropped.</summary>
            public event Action Changed;
            public bool Imaging => Choice.SelectedIndex == 0;
            public AlignmentFrames Frames => Imaging ? (AlignmentFrames)Camera : Guider;
            public void RaiseChanged() => Changed?.Invoke();
        }

        /// <summary>The Camera choice, with exposure and binning for the imaging camera.</summary>
        private PolarSource PolarSourceRows(FormGrid g, string jobName)
        {
            var src = new PolarSource { Camera = new CameraFrames(_capture), Guider = new GuiderFrames(Dm.BuiltinGuider), JobName = jobName };
            src.Choice = g.Row(T("Camera"), Ui.Combo(T("Imaging camera"), T("Guide camera")));
            src.Exposure = g.Row(T("Exposure (s)"), Ui.Num(2, 0.1m, 30, 1, 0.5m));
            src.Bin = g.Row(T("Binning"), Ui.Num(2, 1, 4));
            src.Choice.SelectionChangeCommitted += (s, e) => src.UserChose = true;
            src.Choice.SelectedIndexChanged += (s, e) =>
            {
                ShowRow(g, src.Exposure, src.Imaging);
                ShowRow(g, src.Bin, src.Imaging);
                src.RaiseChanged();
            };
            ShowRow(g, src.Exposure, true);
            ShowRow(g, src.Bin, true);
            return src;
        }

        /// <summary>The imaging camera when one is connected, else the guide camera, until one is chosen by hand.</summary>
        private void DefaultPolarSource(PolarSource src)
        {
            if (src.UserChose || PolarFramesRunning(src)) return;
            int want = Dm.Camera != null || Dm.GuideCamera == null ? 0 : 1;
            if (src.Choice.SelectedIndex != want) src.Choice.SelectedIndex = want;
        }

        private bool PolarFramesRunning(PolarSource src) => _cameraJob != null && !_cameraJob.Done && _cameraJobName == src.JobName;

        /// <summary>Whether frames are coming from the chosen camera.</summary>
        private bool PolarFramesComing(PolarSource src) => src.Imaging ? PolarFramesRunning(src) : Dm.BuiltinGuider.IsRunning;

        /// <summary>Whether the chosen camera can be used now, saying why not.</summary>
        private bool PolarSourceFree(PolarSource src)
        {
            if (!src.Imaging) return GuideCameraFree();
            if (PolarFramesRunning(src)) return true;
            return Have(Dm.Camera, NoCamera) && CanUseCamera();
        }

        /// <summary>Starts the imaging camera's frames, when they are not coming already (the guider's loop starts itself).</summary>
        private void StartPolarFrames(PolarSource src)
        {
            if (!src.Imaging || PolarFramesRunning(src)) return;
            var cf = src.Camera;
            cf.Exposure = (double)src.Exposure.Value;
            cf.Bin = (int)src.Bin.Value;
            RunCameraJob(src.JobName, t => cf.Run(t.Cts.Token).GetAwaiter().GetResult());
        }

        /// <summary>Ends the frames the page started: the imaging camera's, or the guider's loop when it was started here.</summary>
        private void StopPolarFrames(PolarSource src)
        {
            if (PolarFramesRunning(src)) _cameraJob.Cts.Cancel();
            src.Guider.StopIfStarted();
        }

        /// <summary>The chosen camera's frames, with an overlay, shown while the page is.</summary>
        private ImageViewer PolarFrameViewer(Control page, PolarSource src, Action<Graphics, Func<double, double, PointF>> overlay)
        {
            var viewer = new ImageViewer { Dock = DockStyle.Fill };
            viewer.Stretch = DisplayStretch.AutoLinked;
            viewer.PaintOverlay += overlay;
            AstroImage lastCamera = null;
            int posted = 0;
            Action show = () =>
            {
                Interlocked.Exchange(ref posted, 0);
                viewer.EmptyText = src.Imaging ? T("Frames show here. Connect a camera in Observatory → Equipment.") : T("Guide frames show here. Connect a guide camera in Observatory → Equipment.");
                var f = src.Imaging ? lastCamera : Dm.Phd2Connected ? null : Dm.BuiltinGuider.LastFrame;
                if (!page.Visible) return;
                if (f == null) { if (viewer.Image != null) viewer.SetImage(null); else viewer.Invalidate(); return; }
                bool fit = viewer.Image == null || viewer.Image.Width != f.Width || viewer.Image.Height != f.Height;
                viewer.SetImage(f, fit);
            };
            Action post = () => { if (Interlocked.Exchange(ref posted, 1) == 0) MainForm.Instance.Post(show); };
            Dm.BuiltinGuider.FrameReady += f => { if (!src.Imaging) post(); };
            src.Camera.FrameReady += f => { lastCamera = f; if (src.Imaging) post(); };
            src.Changed += show;
            page.VisibleChanged += (s, e) => { if (page.Visible) show(); };
            show();
            return viewer;
        }

        /// <summary>A circle of a radius in screen pixels round an image point.</summary>
        private static void Circle(Graphics gr, Pen pen, PointF c, float r) => gr.DrawEllipse(pen, c.X - r, c.Y - r, 2 * r, 2 * r);

        private static void Cross(Graphics gr, Pen pen, PointF c, float r)
        {
            gr.DrawLine(pen, c.X - r, c.Y, c.X + r, c.Y);
            gr.DrawLine(pen, c.X, c.Y - r, c.X, c.Y + r);
        }

        private static void DrawTag(Graphics gr, string text, PointF at, Color color)
        {
            using (var b = new SolidBrush(color)) gr.DrawString(text, Theme.SmallFont, b, at.X + 8, at.Y + 6);
        }

        // ------------------------------------------------------------------ drift alignment

        private Control DriftAlignmentPage(Control page)
        {
            var card = new Card(T("Drift alignment")) { Dock = DockStyle.Fill };
            var g = new FormGrid(160);
            g.Full(Ui.Hint(T("Sidralune guides with the Dec corrections held back, and the Dec drift tells the polar axis's error: near the meridian and the celestial equator the azimuth error, 25–35° up in the east or west the altitude error. Adjust, measure again, until both are small.")));
            var where = Note(); where.MaximumSize = new Size(560, 0); g.Full(where);
            var start = MakeButton(T("Start measuring"), Icons.Play); start.Kind = ButtonKind.Primary;
            var stop = MakeButton(T("Stop"), Icons.Stop); stop.Kind = ButtonKind.Danger;
            var over = MakeButton(T("Start over"), Icons.Remove);
            g.Full(ButtonRow(start, stop, over));
            var live = Note(); live.MaximumSize = new Size(560, 0); g.Full(live);
            var res = new Label { AutoSize = true, MaximumSize = new Size(560, 0), Font = Theme.HeaderFont, UseMnemonic = false, Margin = new Padding(0, 8, 0, 4) };
            g.Full(res);
            card.Controls.Add(g);

            DriftAlignment da = null;
            Func<DriftAlignment> get = () => da ?? (da = new DriftAlignment(Dm.BuiltinGuider, Dm));
            var job = new JobSlot();
            Action showResult = () =>
            {
                var d = get();
                if (double.IsNaN(d.TowardMeridian) && double.IsNaN(d.TowardEast)) { res.Text = ""; return; }
                var r = PolarAxis.Error(PolarAxis.FromOffsets(double.IsNaN(d.TowardMeridian) ? 0 : d.TowardMeridian, double.IsNaN(d.TowardEast) ? 0 : d.TowardEast), Dm.Site, false);
                res.Text = (double.IsNaN(d.TowardMeridian) ? T("Altitude error: not measured yet") : T("Altitude error {0:+0.0;-0.0}′", r.AltErrorArcmin)) + "\r\n"
                         + (double.IsNaN(d.TowardEast) ? T("Azimuth error: not measured yet") : T("Azimuth error {0:+0.0;-0.0}′", r.AzErrorArcmin))
                         + (d.Last != null && d.Last.Advice != null ? "\r\n" + d.Last.Advice : "");
            };
            bool startedGuiding = false;
            start.Click += (s, e) =>
            {
                if (!GuideCameraFree(true) || !Have(Dm.Telescope, NoTelescope) || !PointingFits(false)) return;
                var gd = Dm.BuiltinGuider;
                // guiding first, when it is not on yet
                startedGuiding = !gd.InGuidingPhase;
                job.Run(T("Drift alignment"), t =>
                {
                    if (!gd.InGuidingPhase) gd.StartGuiding(false, t.Cts.Token).GetAwaiter().GetResult();
                }, () => Try(() => get().Start()));
            };
            // Stop follows what is going on: while guiding starts, it cancels that (and guiding, when this page started
            // it); while measuring, it ends the measurement and shows the error, and guiding goes on for the next one
            stop.Click += (s, e) => Try(() =>
            {
                if (job.Busy)
                {
                    job.Cancel();
                    if (startedGuiding) { startedGuiding = false; Dm.BuiltinGuider.StopGuiding(); }
                    live.Text = "";
                    return;
                }
                var m = get().Stop();
                live.Text = m == null || double.IsNaN(m.ErrorArcmin) ? T("Too short to tell: measure for a minute or two.") : "";
                showResult();
            });
            over.Click += (s, e) => { get().Cancel(); get().Forget(); live.Text = ""; showResult(); };
            job.Guard(start, over);
            Poll += () =>
            {
                if (!page.Visible) return;
                var d = get();
                var gd = Dm.BuiltinGuider;
                if (d.Measuring && !gd.InGuidingPhase) { d.Cancel(); live.Text = T("Guiding stopped, so the measurement was dropped."); }
                try { where.Text = Dm.Telescope != null && Dm.Telescope.Connected ? d.PointingAdvice() : T("Connect the mount: drift alignment needs to know where it points."); }
                catch (Exception ex) { where.Text = ex.Message; }
                if (d.Measuring)
                {
                    var c = d.Current;
                    string error = double.IsNaN(c.ErrorArcmin) ? ""
                        : " → " + (c.Altitude ? T("altitude error {0:+0.0;-0.0}′ ± {1:0.0}′", c.ErrorArcmin, c.UncertaintyArcmin) : T("azimuth error {0:+0.0;-0.0}′ ± {1:0.0}′", c.ErrorArcmin, c.UncertaintyArcmin));
                    live.Text = c.Frames < 5 ? T("Measuring… {0} frames so far.", c.Frames)
                        : T("{0:0} s: Dec drift {1:+0.00;-0.00}″/min ± {2:0.00}", c.Seconds, c.DriftArcsecPerMin, c.UncertaintyArcsecPerMin) + error;
                }
                start.Enabled = !d.Measuring && !job.Busy && !Dm.Phd2Connected;
                stop.Enabled = d.Measuring || job.Busy;
            };
            return card;
        }

        // ------------------------------------------------------------------ static polar alignment

        private Control StaticAlignmentPage(Control page)
        {
            var root = new Panel { Dock = DockStyle.Fill };
            var card = new Card(T("Static polar alignment")) { Dock = DockStyle.Left, Width = 420 };
            var g = new FormGrid(150);
            var src = PolarSourceRows(g, T("Static polar alignment"));
            var step = g.Row(T("RA step (°)"), Ui.Num(15, 5, 40));
            var auto = g.Row(T("Turning RA"), new CheckBox { Text = T("The mount turns itself"), Checked = true });
            g.Full(Ui.Hint(T("Point near the celestial pole. Sidralune takes a frame, turns RA about 15° west twice and plate-solves each frame: the mount's axis is the point the frames turn about, and the plate solution places the refracted pole. Then turn the altitude and azimuth knobs until the star reaches its circle.")));
            var go = MakeButton(T("Start alignment"), Icons.Target); go.Kind = ButtonKind.Primary;
            var next = MakeButton(T("Next frame"), Icons.Camera);
            var stop = MakeButton(T("Stop"), Icons.Stop); stop.Kind = ButtonKind.Danger;
            g.Full(ButtonRow(go, next, stop));
            var status = Note(); g.Full(status);
            var res = new Label { AutoSize = true, MaximumSize = new Size(380, 0), Font = Theme.HeaderFont, UseMnemonic = false, Margin = new Padding(0, 8, 0, 4) };
            g.Full(res);
            card.Controls.Add(g);

            StaticPolarAlignment sa = null;
            Func<StaticPolarAlignment> get = () =>
            {
                if (sa != null) return sa;
                sa = new StaticPolarAlignment(src.Frames, Dm);
                sa.Status += m => MainForm.Instance.Post(() => status.Text = m);
                return sa;
            };
            // another camera: what was measured with the other one no longer fits
            src.Changed += () => { if (sa != null) { sa.StopTracking(); sa = null; } status.Text = res.Text = ""; };
            var viewer = PolarFrameViewer(page, src, (gr, toScreen) =>
            {
                var r = sa?.Result;
                if (r == null) return;
                var p = Theme.Current;
                gr.SmoothingMode = SmoothingMode.AntiAlias;
                PointF axis = toScreen(r.AxisX, r.AxisY), pole = toScreen(r.PoleX, r.PoleY);
                using (var dash = new Pen(p.TextDim, 1f) { DashStyle = DashStyle.Dash }) gr.DrawLine(dash, axis, pole);
                using (var pen = new Pen(p.Accent, 1.5f)) { Cross(gr, pen, axis, 10); DrawTag(gr, T("Mount axis"), axis, p.Accent); }
                using (var pen = new Pen(p.Warning, 1.5f)) { Circle(gr, pen, pole, 6); DrawTag(gr, T("Pole"), pole, p.Warning); }
                if (double.IsNaN(r.TargetX)) return;
                PointF target = toScreen(r.TargetX, r.TargetY);
                using (var pen = new Pen(p.Success, 1.5f)) { Circle(gr, pen, target, 12); DrawTag(gr, T("Target"), target, p.Success); }
                if (!double.IsNaN(sa.StarX))
                {
                    PointF star = toScreen(sa.StarX, sa.StarY);
                    using (var pen = new Pen(p.Success, 1f) { DashStyle = DashStyle.Dot }) gr.DrawLine(pen, star, target);
                    using (var pen = new Pen(p.Success, 1.5f)) Circle(gr, pen, star, 7);
                }
            });
            var job = new JobSlot();
            Action finish = () =>
            {
                var r = get().Result;
                if (r == null) return;
                res.Text = AlignmentText(r);
                get().StartTracking();
                viewer.Invalidate();
            };
            go.Click += (s, e) =>
            {
                if (!PolarSourceFree(src) || !PointingFits(true)) return;
                if (auto.Checked && !Have(Dm.Telescope, NoTelescope)) return;
                var a = get();
                a.StopTracking();
                a.StepDeg = (double)step.Value;
                res.Text = "";
                StartPolarFrames(src);
                if (auto.Checked) job.Run(T("Static polar alignment"), t => a.RunAutomatic(t.Cts.Token).GetAwaiter().GetResult(), finish);
                else
                {
                    a.Reset();
                    job.Run(T("Static polar alignment"), t => a.AddFrame(t.Cts.Token).GetAwaiter().GetResult(),
                        () => status.Text = T("Turn RA about {0:0}° west, then click Next frame.", step.Value));
                }
            };
            next.Click += (s, e) =>
            {
                if (!PolarSourceFree(src)) return;
                var a = get();
                StartPolarFrames(src);
                job.Run(T("Static polar alignment"), t =>
                {
                    a.AddFrame(t.Cts.Token).GetAwaiter().GetResult();
                    if (a.Frames >= 3) a.Compute();
                }, () =>
                {
                    if (a.Frames >= 3) finish();
                    else status.Text = T("Turn RA about {0:0}° west, then click Next frame.", step.Value);
                });
            };
            stop.Click += (s, e) => { job.Cancel(); get().StopTracking(); StopPolarFrames(src); status.Text = ""; };
            job.Guard(go, next, step, auto, src.Choice, src.Exposure, src.Bin);
            Poll += () =>
            {
                var a = get();
                // the frames end with the alignment: when it failed or was stopped, and when the star is no longer followed
                if (!job.Busy && !a.Tracking && (a.Frames == 0 || a.Frames >= 3) && (PolarFramesRunning(src) || src.Guider.StartedLoop)) StopPolarFrames(src);
                if (!page.Visible) return;
                DefaultPolarSource(src);
                bool coming = PolarFramesRunning(src);
                src.Choice.Enabled = src.Exposure.Enabled = src.Bin.Enabled = !job.Busy && !a.Tracking && !coming;
                next.Visible = !auto.Checked;
                next.Enabled = !job.Busy && a.Frames > 0 && a.Frames < 3 && !a.Tracking;
                stop.Enabled = job.Busy || a.Tracking || coming;
                if (a.Tracking)
                {
                    double left = a.RemainingArcmin;
                    status.Text = double.IsNaN(left) ? T("The star is not found: turn the knobs more slowly, or click Start alignment again.")
                        : T("Turn the altitude and azimuth knobs until the star is in its circle: {0:0.0}′ to go.", left);
                }
            };
            root.Controls.Add(viewer);
            root.Controls.Add(card);
            return root;
        }

        // ------------------------------------------------------------------ polar drift alignment

        private Control PolarDriftPage(Control page)
        {
            var root = new Panel { Dock = DockStyle.Fill };
            var card = new Card(T("Polar drift alignment")) { Dock = DockStyle.Left, Width = 420 };
            var g = new FormGrid(150);
            var src = PolarSourceRows(g, T("Polar drift alignment"));
            var mirrored = new CheckBox { Text = T("Mirrored (an off-axis guider's prism, a diagonal)"), AutoSize = true, Margin = new Padding(0, 4, 0, 0) };
            g.Full(mirrored);
            var mirrorNote = Ui.Hint(""); g.Full(mirrorNote);
            g.Full(Ui.Hint(T("Point near the celestial pole. With the mount only tracking, a star is followed: its drift gives the polar axis's error, with no calibration and no plate solving. Stop when the error has settled, then move the star into its circle with the altitude and azimuth knobs.")));
            var start = MakeButton(T("Follow a star"), Icons.Play); start.Kind = ButtonKind.Primary;
            var stop = MakeButton(T("Stop"), Icons.Stop); stop.Kind = ButtonKind.Danger;
            g.Full(ButtonRow(start, stop));
            var live = Note(); g.Full(live);
            var res = new Label { AutoSize = true, MaximumSize = new Size(380, 0), Font = Theme.HeaderFont, UseMnemonic = false, Margin = new Padding(0, 8, 0, 4) };
            g.Full(res);
            card.Controls.Add(g);

            PolarDriftAlignment pd = null;
            Func<PolarDriftAlignment> get = () => pd ?? (pd = new PolarDriftAlignment(src.Frames, Dm));
            PolarDriftResult shown = null;
            src.Changed += () => { if (pd != null) { pd.Cancel(); pd = null; } shown = null; live.Text = res.Text = ""; };
            var viewer = PolarFrameViewer(page, src, (gr, toScreen) =>
            {
                var p = Theme.Current;
                gr.SmoothingMode = SmoothingMode.AntiAlias;
                // the star followed, from the first frame
                bool following = pd != null && pd.Following && !double.IsNaN(pd.StarX);
                PointF star = following ? toScreen(pd.StarX, pd.StarY) : PointF.Empty;
                if (following) using (var pen = new Pen(p.Success, 1.5f)) Circle(gr, pen, star, 7);
                var r = pd != null && pd.Measuring ? pd.Current : shown;
                if (r == null || double.IsNaN(r.TargetX)) return;
                PointF target = toScreen(r.TargetX, r.TargetY);
                using (var pen = new Pen(p.Success, 1.5f)) { Circle(gr, pen, target, 12); DrawTag(gr, T("Target"), target, p.Success); }
                if (following) using (var pen = new Pen(p.Success, 1f) { DashStyle = DashStyle.Dot }) gr.DrawLine(pen, star, target);
            });
            start.Click += (s, e) =>
            {
                if (!PolarSourceFree(src) || !PointingFits(true)) return;
                Try(() => { shown = null; res.Text = ""; StartPolarFrames(src); get().Start(); });
            };
            // Stop follows what is going on: while measuring, it ends the measurement and shows the target (the star is
            // still followed, to move it into its circle); after that, it stops following and the frames
            stop.Click += (s, e) =>
            {
                var a = get();
                if (!a.Measuring) { a.Cancel(); StopPolarFrames(src); live.Text = ""; viewer.Invalidate(); return; }
                var r = a.Stop();
                if (r == null || double.IsNaN(r.ErrorArcmin)) { a.Cancel(); StopPolarFrames(src); live.Text = T("Too short to tell: follow the star for a minute or two."); return; }
                shown = r;
                live.Text = "";
                res.Text = T("Polar alignment error {0:0.0}′ ± {1:0.0}′", r.ErrorArcmin, r.UncertaintyArcmin) + "\r\n" + T("Move the star into its circle with the altitude and azimuth knobs.");
                viewer.Invalidate();
            };
            bool loading = false;
            mirrored.CheckedChanged += (s, e) => { if (!loading) get().Mirrored = mirrored.Checked; };
            Poll += () =>
            {
                var a = get();
                // the frames ended elsewhere (the camera tabs' Stop, the guider stopped): nothing more to follow
                if (a.Following && !PolarFramesComing(src)) { a.Cancel(); src.Guider.StopIfStarted(); live.Text = ""; }
                if (!page.Visible) return;
                DefaultPolarSource(src);
                var fromCal = src.Imaging ? null : PolarDriftAlignment.MirroredFromCalibration(Dm.BuiltinGuider);
                loading = true;
                if (fromCal != null) { mirrored.Checked = fromCal.Value; a.Mirrored = null; }
                else a.Mirrored = mirrored.Checked;
                loading = false;
                mirrored.Enabled = fromCal == null && !a.Measuring;
                mirrorNote.Text = fromCal != null ? T("From the guider's calibration.")
                    : src.Imaging ? T("Say whether the camera sees a mirror image.") : T("Without a calibration, say whether the guide camera sees a mirror image.");
                if (a.Measuring)
                {
                    var c = a.Current;
                    live.Text = c.Frames < 5 || double.IsNaN(c.ErrorArcmin) ? T("Following the star… {0} frames so far.", c.Frames)
                        : T("{0:0} s: error {1:0.0}′ ± {2:0.0}′. Stop when it has settled.", c.Seconds, c.ErrorArcmin, c.UncertaintyArcmin);
                }
                if (a.Following) viewer.Invalidate();
                bool coming = PolarFramesRunning(src);
                src.Choice.Enabled = src.Exposure.Enabled = src.Bin.Enabled = !a.Following && !coming;
                start.Enabled = !a.Measuring && (src.Imaging || !Dm.Phd2Connected);
                stop.Enabled = a.Following || coming;
            };
            root.Controls.Add(viewer);
            root.Controls.Add(card);
            return root;
        }
    }
}
