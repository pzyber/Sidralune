using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
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
        // the polar alignment methods, each giving its name while it runs: one runs at a time, and nothing else that moves
        // the mount or guides starts meanwhile
        private readonly List<Func<string>> _polarRunning = new List<Func<string>>();

        /// <summary>The polar alignment running now (other than one), or null.</summary>
        private string PolarBusyReason(string except = null) => _polarRunning.Select(f => f()).FirstOrDefault(n => n != null && n != except);

        /// <summary>What moves the mount now (a sequence, centring, a polar alignment), or null.</summary>
        private string MountBusyReason(string except = null)
        {
            if (SequenceActive) return T("Sequence");
            if (_cameraJob != null && !_cameraJob.Done && _cameraJobMovesMount && _cameraJobName != except) return _cameraJobName;
            return PolarBusyReason(except);
        }

        private static bool Refuse(string busy)
        {
            if (busy != null) MainForm.Instance.Toast(T("{0}. Stop it first.", T("{0} is running", busy)), Icons.Info);
            return busy != null;
        }

        /// <summary>Whether no polar alignment (other than one) runs, saying which does.</summary>
        private bool PolarFree(string except = null) => !Refuse(PolarBusyReason(except));

        /// <summary>Whether nothing (other than one) moves the mount, saying what does.</summary>
        private bool MountFree(string except = null) => !Refuse(MountBusyReason(except));

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
            _polarRunning.Add(() => running() ? T("Polar alignment") : null);
            stop.Click += (s, e) => { if (running()) _cameraJob.Cts.Cancel(); };
            Poll += () => stop.Enabled = running();
            go.Click += (s, e) =>
            {
                if (!Have(Dm.Telescope, NoTelescope) || !Have(Dm.Camera, NoCamera) || !CanUseCamera() || !MountFree(T("Polar alignment")) || !PointingFits(false)) return;
                var pa = new PolarAlignment { Capture = _capture, StepDeg = (double)step.Value, Exposure = (double)exp.Value };
                pa.Status += st => MainForm.Instance.SetStatus(st);
                RunCameraJob(T("Polar alignment"), t =>
                {
                    var r = pa.Run(t.Cts.Token).GetAwaiter().GetResult();
                    MainForm.Instance.Post(() => res.Text = AlignmentText(r));
                }, movesMount: true);
            };
            g.Full(Ui.Hint(T("Point the mount near the celestial equator/meridian with clear sky to the west or east. Sidralune rotates the RA axis three times, plate-solves each frame, finds the mount's rotation axis and reports how far it is from the refracted celestial pole. Works without a view of Polaris.")));
            g.Full(ButtonRow(go, stop));
            CameraButton(go);
            Availability.When(go, () => MountBusyReason(T("Polar alignment")) == null);
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
            /// <summary>The alignment runs: its frames are wanted, and guiding is held back.</summary>
            public bool Active;
            /// <summary>The camera the running alignment uses: chosen when it started, whatever is chosen meanwhile.</summary>
            public bool RunningImaging;
            /// <summary>The camera used now: the running alignment's, else the one chosen.</summary>
            public bool UsesImaging => Active ? RunningImaging : Imaging;
            public AlignmentFrames UsedFrames => UsesImaging ? (AlignmentFrames)Camera : Guider;
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
            if (src.UserChose || src.Active) return;
            int want = Dm.Camera != null || Dm.GuideCamera == null ? 0 : 1;
            if (src.Choice.SelectedIndex != want) src.Choice.SelectedIndex = want;
        }

        private bool PolarFramesRunning(PolarSource src) => _cameraJob != null && !_cameraJob.Done && _cameraJobName == src.JobName;

        /// <summary>Whether frames are coming from the chosen camera.</summary>
        private bool PolarFramesComing(PolarSource src) => src.UsesImaging ? PolarFramesRunning(src) : Dm.BuiltinGuider.IsRunning;

        /// <summary>Whether the chosen camera can be used now, saying why not.</summary>
        private bool PolarSourceFree(PolarSource src) => src.Imaging ? Have(Dm.Camera, NoCamera) && CanUseCamera() : GuideCameraFree();

        /// <summary>Guiding or calibrating now, with the Sidralune guider or PHD2.</summary>
        private bool GuidingNow()
        {
            var b = Dm.BuiltinGuider;
            bool guiding = b.InGuidingPhase || b.IsCalibrating;
            if (Dm.Phd2Connected) try { guiding |= Dm.Guider.IsGuiding; } catch { }
            return guiding;
        }

        /// <summary>Whether the mount only tracks: no guiding or calibrating, with the Sidralune guider or PHD2.</summary>
        private bool MountOnlyTracks()
        {
            bool guiding = GuidingNow();
            if (guiding) MainForm.Instance.Toast(T("Stop guiding first: the mount only tracks for this method."), Icons.Warning);
            return !guiding;
        }

        /// <summary>
        /// Whether a static or polar drift alignment could start now: nothing else moves the mount or guides, and the chosen
        /// camera is free. Its button is greyed out otherwise. A camera not connected is said when it is clicked.
        /// </summary>
        private bool PolarCanStart(PolarSource src)
        {
            if (src.Active) return true;
            if (MountBusyReason(src.JobName) != null || GuidingNow()) return false;
            return src.Imaging ? CameraBusyReason() == null : !Dm.Phd2Connected && !Dm.BuiltinGuider.CameraInUse;
        }

        /// <summary>
        /// Starts a static or polar drift alignment: its frames (the imaging camera's as a camera job, or the guider's
        /// loop), with guiding held back meanwhile, as the mount must only track. False, saying why, when it cannot start.
        /// </summary>
        private bool BeginPolarFrames(PolarSource src)
        {
            if (src.Active) return true;
            if (!MountFree(src.JobName) || !PolarSourceFree(src) || !MountOnlyTracks()) return false;
            try
            {
                Dm.BuiltinGuider.HoldGuiding(src.JobName);
                if (src.Imaging)
                {
                    var cf = src.Camera;
                    cf.Exposure = (double)src.Exposure.Value;
                    cf.Bin = (int)src.Bin.Value;
                    RunCameraJob(src.JobName, t => cf.Run(t.Cts.Token).GetAwaiter().GetResult());
                }
                else src.Guider.Ensure();
            }
            catch (Exception ex)
            {
                src.Guider.StopIfStarted();
                Dm.BuiltinGuider.ReleaseGuiding(src.JobName);
                MainForm.Instance.Toast(ex.Message, Icons.Warning);
                return false;
            }
            src.RunningImaging = src.Imaging;
            src.Active = true;
            Availability.Sync();
            return true;
        }

        /// <summary>Ends a static or polar drift alignment's frames (the guider's loop only when it was started for them), and guiding may start again.</summary>
        private void EndPolarFrames(PolarSource src)
        {
            if (!src.Active) return;
            src.Active = false;
            if (PolarFramesRunning(src)) _cameraJob.Cts.Cancel();
            src.Guider.StopIfStarted();
            Dm.BuiltinGuider.ReleaseGuiding(src.JobName);
            Availability.Sync();
            // another camera was chosen meanwhile: it is the one shown now, and what was measured no longer fits it
            if (src.Imaging != src.RunningImaging) src.RaiseChanged();
        }

        /// <summary>Calls ended when the page's frames stop coming while it runs (stopped on a camera tab or the Guiding tab, or failed).</summary>
        private void WhenPolarFramesStop(PolarSource src, Action ended)
        {
            // posted at once, so it comes before what the stop makes fail (the alignment waiting for a frame)
            src.Camera.Stopped += () => MainForm.Instance.Post(() => { if (src.Active && src.UsesImaging) ended(); });
            src.Guider.Stopped += () => MainForm.Instance.Post(() => { if (src.Active && !src.UsesImaging) ended(); });
            // and should a stop go unannounced
            Poll += () => { if (src.Active && !PolarFramesComing(src)) ended(); };
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
                viewer.EmptyText = src.UsesImaging ? T("Frames show here. Connect a camera in Observatory → Equipment.") : T("Guide frames show here. Connect a guide camera in Observatory → Equipment.");
                var f = src.UsesImaging ? lastCamera : Dm.Phd2Connected ? null : Dm.BuiltinGuider.LastFrame;
                if (!page.Visible) return;
                if (f == null) { if (viewer.Image != null) viewer.SetImage(null); else viewer.Invalidate(); return; }
                bool fit = viewer.Image == null || viewer.Image.Width != f.Width || viewer.Image.Height != f.Height;
                viewer.SetImage(f, fit);
            };
            Action post = () => { if (Interlocked.Exchange(ref posted, 1) == 0) MainForm.Instance.Post(show); };
            Dm.BuiltinGuider.FrameReady += f => { if (!src.UsesImaging) post(); };
            src.Camera.FrameReady += f => { lastCamera = f; if (src.UsesImaging) post(); };
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
                if (!MountFree(T("Drift alignment")) || !GuideCameraFree(true) || !Have(Dm.Telescope, NoTelescope) || !PointingFits(false)) return;
                var gd = Dm.BuiltinGuider;
                // guiding first, when it is not on yet
                startedGuiding = !gd.InGuidingPhase;
                job.Run(T("Drift alignment"), t =>
                {
                    if (!gd.InGuidingPhase) gd.StartGuiding(false, t.Cts.Token).GetAwaiter().GetResult();
                }, () => { Try(() => get().Start()); Availability.Sync(); });
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
                Availability.Sync();
            });
            over.Click += (s, e) => { get().Cancel(); get().Forget(); live.Text = ""; showResult(); Availability.Sync(); };
            _polarRunning.Add(() => job.Busy || get().Measuring ? T("Drift alignment") : null);
            job.Guard(start, over);
            Availability.When(start, () => !get().Measuring && !Dm.Phd2Connected && !Dm.BuiltinGuider.CameraInUse && MountBusyReason(T("Drift alignment")) == null);
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
                sa = new StaticPolarAlignment(src.UsedFrames, Dm);
                sa.Status += m => MainForm.Instance.Post(() => status.Text = m);
                return sa;
            };
            // another camera: what was measured with the other one no longer fits
            // (a change while it runs is for the next alignment: the running one keeps its camera)
            src.Changed += () => { if (src.Active) return; if (sa != null) { sa.StopTracking(); sa = null; } status.Text = res.Text = ""; };
            // the settings the running alignment started with
            bool byHand = false;
            double stepUsed = 15;
            var job = new JobSlot();
            // the alignment ends: stopped, failed, done, or its frames stopped elsewhere
            Action<string> end = why =>
            {
                job.Cancel();
                sa?.StopTracking();
                EndPolarFrames(src);
                if (why != null) status.Text = why;
            };
            _polarRunning.Add(() => src.Active ? T("Static polar alignment") : null);
            WhenPolarFramesStop(src, () => end(T("The frames stopped, so the alignment was stopped.")));
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
            Action finish = () =>
            {
                var r = get().Result;
                if (r == null) return;
                res.Text = AlignmentText(r);
                get().StartTracking();
                viewer.Invalidate();
                Availability.Sync();
            };
            go.Click += (s, e) =>
            {
                // the camera first, then the mount
                if (!PolarSourceFree(src)) return;
                if (!PointingFits(true)) return;
                if (auto.Checked && !Have(Dm.Telescope, NoTelescope)) return;
                if (!BeginPolarFrames(src)) return;
                // the settings are read now: changing them while it runs is for the next alignment
                sa?.StopTracking();
                sa = null;
                var a = get();
                byHand = !auto.Checked;
                a.StepDeg = stepUsed = (double)step.Value;
                res.Text = "";
                if (!byHand) job.Run(T("Static polar alignment"), t => a.RunAutomatic(t.Cts.Token).GetAwaiter().GetResult(), finish);
                else
                {
                    a.Reset();
                    job.Run(T("Static polar alignment"), t => a.AddFrame(t.Cts.Token).GetAwaiter().GetResult(),
                        () => status.Text = T("Turn RA about {0:0}° west, then click Next frame.", stepUsed));
                }
            };
            next.Click += (s, e) =>
            {
                if (!BeginPolarFrames(src)) return;
                var a = get();
                job.Run(T("Static polar alignment"), t =>
                {
                    a.AddFrame(t.Cts.Token).GetAwaiter().GetResult();
                    if (a.Frames >= 3) a.Compute();
                }, () =>
                {
                    if (a.Frames >= 3) finish();
                    else status.Text = T("Turn RA about {0:0}° west, then click Next frame.", stepUsed);
                });
            };
            stop.Click += (s, e) => { end(null); status.Text = ""; };
            job.Guard(go, next);
            // greyed out while they cannot be used: something else moves the mount, guides or uses the camera
            // while it runs (following the star too, and between frames turned by hand) Stop ends it first
            Availability.When(go, () => !src.Active && PolarCanStart(src));
            Availability.When(stop, () => job.Busy || src.Active);
            Availability.When(next, () => { var a = get(); return a.Frames > 0 && a.Frames < 3 && !a.Tracking && PolarCanStart(src); });
            Poll += () =>
            {
                var a = get();
                // the alignment ended by itself: it failed or was stopped, or the star is no longer followed (between the
                // frames turned by hand it goes on)
                if (src.Active && !job.Busy && !a.Tracking && (a.Frames == 0 || a.Frames >= 3)) end(null);
                if (!page.Visible) return;
                DefaultPolarSource(src);
                next.Visible = src.Active ? byHand : !auto.Checked;
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
            Func<PolarDriftAlignment> get = () => pd ?? (pd = new PolarDriftAlignment(src.UsedFrames, Dm));
            PolarDriftResult shown = null;
            // (a change while it runs is for the next alignment: the running one keeps its camera)
            src.Changed += () => { if (src.Active) return; if (pd != null) { pd.Cancel(); pd = null; } shown = null; live.Text = res.Text = ""; };
            // following ends: stopped, or its frames stopped elsewhere
            Action<string> end = why =>
            {
                pd?.Cancel();
                EndPolarFrames(src);
                if (why != null) live.Text = why;
            };
            _polarRunning.Add(() => src.Active ? T("Polar drift alignment") : null);
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
                if (!PointingFits(true) || !BeginPolarFrames(src)) return;
                // the settings are read now: changing them while it runs is for the next alignment
                pd?.Cancel();
                pd = null;
                var fromCal = src.Imaging ? null : PolarDriftAlignment.MirroredFromCalibration(Dm.BuiltinGuider);
                get().Mirrored = fromCal ?? mirrored.Checked;
                try { shown = null; res.Text = ""; get().Start(); }
                catch (Exception ex) { end(null); MainForm.Instance.Toast(ex.Message, Icons.Warning); }
                Availability.Sync();
            };
            // Stop follows what is going on: while measuring, it ends the measurement and shows the target (the star is
            // still followed, to move it into its circle); after that, it stops following and the frames
            stop.Click += (s, e) =>
            {
                var a = get();
                if (!a.Measuring) { end(""); viewer.Invalidate(); return; }
                var r = a.Stop();
                if (r == null || double.IsNaN(r.ErrorArcmin)) { end(T("Too short to tell: follow the star for a minute or two.")); return; }
                shown = r;
                live.Text = "";
                res.Text = T("Polar alignment error {0:0.0}′ ± {1:0.0}′", r.ErrorArcmin, r.UncertaintyArcmin) + "\r\n" + T("Move the star into its circle with the altitude and azimuth knobs.");
                viewer.Invalidate();
                Availability.Sync(); // Follow a star again
            };
            WhenPolarFramesStop(src, () => { end(T("The frames stopped, so the alignment was stopped.")); viewer.Invalidate(); });
            // greyed out while it cannot start: running already (following the star after the target is shown too; Stop
            // ends it first), or something else moves the mount, guides or uses the camera
            Availability.When(start, () => !src.Active && (src.Imaging || !Dm.Phd2Connected) && PolarCanStart(src));
            Availability.When(stop, () => src.Active);
            Poll += () =>
            {
                var a = get();
                // following ended by itself (its frames stopped): so does the alignment
                if (src.Active && !a.Following) end(null);
                if (!page.Visible) return;
                DefaultPolarSource(src);
                // for the next start: the running alignment keeps what it started with
                var fromCal = src.Imaging ? null : PolarDriftAlignment.MirroredFromCalibration(Dm.BuiltinGuider);
                if (fromCal != null) mirrored.Checked = fromCal.Value;
                mirrored.Enabled = fromCal == null;
                mirrorNote.Text = fromCal != null ? T("From the guider's calibration.")
                    : src.Imaging ? T("Say whether the camera sees a mirror image.") : T("Without a calibration, say whether the guide camera sees a mirror image.");
                if (a.Measuring)
                {
                    var c = a.Current;
                    live.Text = c.Frames < 5 || double.IsNaN(c.ErrorArcmin) ? T("Following the star… {0} frames so far.", c.Frames)
                        : T("{0:0} s: error {1:0.0}′ ± {2:0.0}′. Stop when it has settled.", c.Seconds, c.ErrorArcmin, c.UncertaintyArcmin);
                }
                if (a.Following) viewer.Invalidate();

            };
            root.Controls.Add(viewer);
            root.Controls.Add(card);
            return root;
        }
    }
}
