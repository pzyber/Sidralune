using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Sidralune.Core;
using Sidralune.Core.Ephemeris;
using Sidralune.Core.Mathematics;
using Sidralune.Core.Time;
using Sidralune.Imaging;
using Sidralune.Imaging.IO;
using Sidralune.Imaging.Planetary;
using Sidralune.UI;

namespace Sidralune.Views
{
    /// <summary>Planetary, lunar and solar imaging: PIPP, AutoStakkert, RegiStax and WinJUPOS workflows.</summary>
    public class PlanetaryView : UserControl, ICommandSource
    {
        private readonly TabbedPanel _tabs = new TabbedPanel { Dock = DockStyle.Fill, Padding = new Padding(10) };
        // Pre-process and Lucky stacking each read their own video, so either can run while the other does; opening another
        // video in a tab waits for that tab's job. Each other tool runs one job at a time, and the wavelet preview keeps the newest
        private IFrameSource _ppSource, _luckySource;
        private AstroImage _stacked;
        private readonly JobSlot _ppJob = new JobSlot(), _luckyJob = new JobSlot(), _sharpJob = new JobSlot(), _waveletJob = new JobSlot(), _deroJob = new JobSlot(), _solarJob = new JobSlot();
        // the Pre-process slider reads one frame at a time, the newest wins
        private readonly JobSlot _scrubJob = new JobSlot();

        public PlanetaryView()
        {
            Controls.Add(_tabs);
            _tabs.AddPage("Pre-process", BuildPipp());
            _tabs.AddPage("Lucky stacking", BuildLucky());
            _tabs.AddPage("Sharpen & deconvolve", BuildSharpen());
            _tabs.AddPage("Derotation", BuildDerotation());
            _tabs.AddPage("Solar & lunar", BuildSolar());
        }

        private static Card Side(string title, int width) => new Card(title) { Dock = DockStyle.Right, Width = width, AutoScroll = true };

        /// <summary>The files of a video or image sequence chosen by the user, or null; asked only when the tab's own job is idle.</summary>
        private static List<string> PickSource(JobSlot job)
        {
            if (job.Refuse()) return null;
            using (var d = new OpenFileDialog { Filter = "Video & images|*.ser;*.avi;*.fit;*.fits;*.tif;*.tiff;*.png;*.bmp;*.jpg|All|*.*", Multiselect = true })
                return d.ShowDialog() == DialogResult.OK ? d.FileNames.OrderBy(f => f).ToList() : null;
        }

        /// <summary>
        /// Opens a source in the background as the tab's job (an image sequence loads its first image, and the first frame is
        /// prepared for display), then makes it the tab's source and shows that frame.
        /// </summary>
        private void OpenSourceInBackground(JobSlot job, Func<IFrameSource> open, Func<IFrameSource, AstroImage> firstFrame, Action<IFrameSource, AstroImage> opened)
        {
            IFrameSource src = null;
            AstroImage first = null;
            job.Run("Opening sequence", t =>
            {
                src = open();
                try { first = firstFrame(src); }
                catch { src.Dispose(); throw; }
            }, () => opened(src, first));
        }

        /// <summary>Closes a tab's old source and announces the new one.</summary>
        private static IFrameSource Replace(IFrameSource old, IFrameSource source)
        {
            old?.Dispose();
            MainForm.Instance.Toast(string.Format("{0}: {1} frames, {2}×{3}, {4}-bit{5}", Path.GetFileName(source.Path), source.FrameCount, source.Width, source.Height, source.BitDepth, source.Bayer != BayerPattern.None ? ", Bayer " + source.Bayer : ""));
            return source;
        }

        private Action<Func<IFrameSource>> _openPreprocess;
        private Action<Func<IFrameSource>, Action> _openLucky;

        /// <summary>Open a recorded video in the Pre-process tab (Observatory → Imaging → Open in Planetary).</summary>
        public void OpenVideo(string path)
        {
            if (_ppJob.Refuse()) return;
            _tabs.Select(0);
            _openPreprocess(() => FrameSourceFactory.Open(path));
        }

        // ================================================================== PIPP

        private Control BuildPipp()
        {
            var panel = new Panel { Dock = DockStyle.Fill };
            // frames as recorded, like every planetary tab: an automatic stretch would blow the black sky up into noise
            var viewer = new ImageViewer { Dock = DockStyle.Fill, Stretch = DisplayStretch.None, EmptyText = "Open a SER/AVI video or an image sequence" };
            var scrub = new Slider { Dock = DockStyle.Bottom, Height = 30, TickStyle = TickStyle.None };
            var plot = new PlotControl { Dock = DockStyle.Bottom, Height = 150, Title = "Frame quality", XLabel = "frame", ShowLegend = true };
            var opt = new PippOptions { OutputFolder = Path.Combine(AppState.Settings.ImageFolder, "Planetary") };
            var props = new PropertyGrid { Dock = DockStyle.Fill, HelpVisible = false, SelectedObject = new GridAdapter(opt, "Pre-process"), PropertySort = PropertySort.NoSort };
            var side = Side("Options", 380);
            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 44 };
            var planet = Ui.Combo("(no derotation target)", "Jupiter", "Saturn", "Mars", "Venus", "Moon", "Sun", "Mercury", "Uranus", "Neptune");
            planet.Width = 200;
            Func<string, string, Action, ButtonKind, FlatButton> btn = (t, i, a, k) => { var b = new FlatButton(t, i, (s, e) => a()) { Kind = k }; b.AutoSizeToText(); bar.Controls.Add(b); return b; };
            List<string> output = null; // the files of the last pre-processing run
            bool settingScrub = false;
            _openPreprocess = open => OpenSourceInBackground(_ppJob, open, src => Preprocessor.PrepareFrame(src, 0, opt), (src, first) =>
            {
                _ppSource = Replace(_ppSource, src);
                settingScrub = true;
                scrub.Maximum = Math.Max(0, src.FrameCount - 1); scrub.Value = 0;
                settingScrub = false;
                viewer.SetImage(first);
            });
            btn("Open source…", Icons.Open, () => { var files = PickSource(_ppJob); if (files != null) _openPreprocess(() => FrameSourceFactory.Open(files)); }, ButtonKind.Normal);
            btn("Output folder…", Icons.Open, () => { using (var d = new FolderBrowserDialog { SelectedPath = opt.OutputFolder }) if (d.ShowDialog() == DialogResult.OK) { opt.OutputFolder = d.SelectedPath; props.Refresh(); } }, ButtonKind.Subtle);
            bar.Controls.Add(new Label { Text = "Field derotation target:", AutoSize = true, Margin = new Padding(0, 8, 4, 0), Tag = "dim" });
            bar.Controls.Add(planet);
            btn("Process", Icons.Play, () =>
            {
                if (_ppSource == null) { MainForm.Instance.Toast("Open a source first", Icons.Info); return; }
                if (planet.SelectedIndex > 0)
                {
                    Body b; Enum.TryParse((string)planet.SelectedItem, out b);
                    var t0 = _ppSource.Timestamp(0) ?? DateTime.UtcNow;
                    var p = new SkyContext(AppState.Site, AstroTime.FromDateTime(t0)).BodyPlace(b);
                    opt.Site = AppState.Site; opt.TargetRaRad = p.Astrometric.Ra; opt.TargetDecRad = p.Astrometric.Dec;
                    opt.PlanetCode = ((string)planet.SelectedItem).Substring(0, 1);
                }
                var src = _ppSource;
                PippResult res = null;
                // the run works on a copy of the options, which stay editable for the next run
                var runOpt = opt.Clone();
                _ppJob.Run("Pre-processing", t => res = Preprocessor.Run(src, runOpt, new Progress<double>(f => Tasks.Report(t, f)), t.Cts.Token), () =>
                {
                    plot.Clear();
                    var all = plot.AddSeries("quality", SeriesStyle.Scatter);
                    var kept = plot.AddSeries("kept", SeriesStyle.Scatter, pal => pal.Success);
                    foreach (var f in res.Frames) { if (f.Rejected) all.Add(f.Index, f.Quality); else kept.Add(f.Index, f.Quality); }
                    plot.Invalidate();
                    MainForm.Instance.Toast("Pre-process: " + res.Summary, Icons.Check, Theme.Current.Success);
                    output = res.OutputFiles.ToList();
                });
            }, ButtonKind.Primary);
            // the result goes to Lucky stacking when you choose; the button waits while a stack is running there
            _luckyJob.Guard(btn("Stacking →", Icons.Right, () =>
            {
                if (output == null || output.Count == 0) { MainForm.Instance.Toast("Process the sequence first", Icons.Info); return; }
                // a video split by Max frames per file opens at its first part; frames written as images open together
                bool video = output[0].EndsWith(".ser", StringComparison.OrdinalIgnoreCase) || output[0].EndsWith(".avi", StringComparison.OrdinalIgnoreCase);
                var files = output;
                _tabs.Select(1);
                _openLucky(() => video ? FrameSourceFactory.Open(files[0]) : FrameSourceFactory.Open(files), () =>
                {
                    if (video && files.Count > 1) MainForm.Instance.Toast("Opened the first of " + files.Count + " video files", Icons.Info);
                });
            }, ButtonKind.Subtle));
            // a frame is read in the background, and only the newest one asked for is shown
            scrub.ValueChanged += (s, e) =>
            {
                if (settingScrub || _ppSource == null) return;
                var src = _ppSource; int i = scrub.Value;
                AstroImage frame = null;
                _scrubJob.Replace("Reading frame", t => { try { frame = Preprocessor.PrepareFrame(src, i, opt); } catch { } }, () => { if (frame != null && src == _ppSource) viewer.SetImage(frame, false); });
            };
            viewer.PaintOverlay += (g, map) =>
            {
                if (viewer.Image == null) return;
                var ob = ObjectDetection.Detect(viewer.Image.Luminance(), viewer.Image.Width, viewer.Image.Height, opt.DetectionThreshold);
                if (!ob.Found) return;
                var a = map(ob.X0, ob.Y0); var b2 = map(ob.X1, ob.Y1); var c = map(ob.Cx, ob.Cy);
                using (var pen = new Pen(Theme.Current.Success, 1.5f)) { g.DrawRectangle(pen, a.X, a.Y, b2.X - a.X, b2.Y - a.Y); g.DrawLine(pen, c.X - 8, c.Y, c.X + 8, c.Y); g.DrawLine(pen, c.X, c.Y - 8, c.X, c.Y + 8); }
            };
            side.Controls.Add(props);
            // Stop, after Process, ends pre-processing; the rest of the toolbar waits for the job
            var ppStop = _ppJob.StopButton(n => n == "Pre-processing");
            _ppJob.Guard(bar.Controls.Cast<Control>().Concat(new Control[] { scrub }).ToArray());
            Ui.InsertAfter(bar, ppStop, "Process");
            panel.Controls.Add(viewer);
            panel.Controls.Add(scrub);
            panel.Controls.Add(plot);
            panel.Controls.Add(side);
            panel.Controls.Add(bar);
            return panel;
        }

        // ================================================================== lucky stacking

        private Control BuildLucky()
        {
            var panel = new Panel { Dock = DockStyle.Fill };
            var viewer = new ImageViewer { Dock = DockStyle.Fill, Stretch = DisplayStretch.None, EmptyText = "Open a SER/AVI (ideally centred in Pre-process) and press Stack" };
            var stacker = new LuckyStacker();
            var props = new PropertyGrid { Dock = DockStyle.Fill, HelpVisible = false, SelectedObject = new GridAdapter(stacker.Options, "Lucky stacking"), PropertySort = PropertySort.NoSort };
            var plot = new PlotControl { Dock = DockStyle.Bottom, Height = 160, Title = "Frame quality (sorted)", XLabel = "frames (%)", YLabel = "quality" };
            var percents = new TextBox { Text = "10, 25, 50", Width = 120 };
            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 44 };
            LuckyResult result = null;
            LuckyOptions resultOptions = null; // the options the shown result was made with
            int shown = 0;
            Func<string, string, Action, ButtonKind, FlatButton> btn = (t, i, a, k) => { var b = new FlatButton(t, i, (s, e) => a()) { Kind = k }; b.AutoSizeToText(); bar.Controls.Add(b); return b; };
            _openLucky = (open, done) => OpenSourceInBackground(_luckyJob, open, src => src.ReadFrame(0), (src, first) =>
            {
                _luckySource = Replace(_luckySource, src);
                viewer.SetImage(first);
                done?.Invoke();
            });
            btn("Open source…", Icons.Open, () => { var files = PickSource(_luckyJob); if (files != null) _openLucky(() => FrameSourceFactory.Open(files), null); }, ButtonKind.Normal);
            bar.Controls.Add(new Label { Text = "Stack %:", AutoSize = true, Margin = new Padding(6, 8, 4, 0), Tag = "dim" });
            bar.Controls.Add(percents);
            btn("Analyse & stack", Icons.Stack, () =>
            {
                if (_luckySource == null) { MainForm.Instance.Toast("Open a source first", Icons.Info); return; }
                stacker.Options.StackPercents = percents.Text.Split(',').Select(p => AstroMath.ParseInvariant(p)).Where(p => !double.IsNaN(p) && p > 0 && p <= 100).DefaultIfEmpty(25).ToArray();
                var src = _luckySource;
                // the run works on a copy of the options, which stay editable for the next run
                var run = new LuckyStacker { Options = stacker.Options.Clone() };
                LuckyResult res = null;
                _luckyJob.Run("Lucky stacking", t => res = run.Run(src, new Progress<string>(s => t.Status = s), new Progress<double>(f => Tasks.Report(t, f)), t.Cts.Token), () =>
                {
                    result = res;
                    resultOptions = run.Options;
                    shown = 0;
                    _stacked = result.Stacks[0];
                    viewer.SetImage(_stacked);
                    plot.Clear();
                    var q = result.Frames.Select(f => f.Quality).OrderByDescending(x => x).ToList();
                    var s = plot.AddSeries("quality");
                    for (int i = 0; i < q.Count; i++) s.Add(100.0 * i / q.Count, q[i] / q[0]);
                    foreach (var pct in resultOptions.StackPercents) plot.Markers.Add(new PlotMarker { X = pct, Label = pct + "%" });
                    plot.Invalidate();
                    MainForm.Instance.Toast(result.Report, Icons.Check, Theme.Current.Success);
                });
            }, ButtonKind.Primary);
            Func<bool> stacked = () => { if (result != null && result.Stacks.Count > 0) return true; MainForm.Instance.Toast("Nothing stacked yet: open a sequence and click Analyse & stack", Icons.Info); return false; };
            btn("Next stack", Icons.Right, () => { if (stacked()) { shown = (shown + 1) % result.Stacks.Count; _stacked = result.Stacks[shown]; viewer.SetImage(_stacked, false); MainForm.Instance.SetStatus("Showing best " + resultOptions.StackPercents[shown] + "%"); } }, ButtonKind.Subtle);
            btn("Save stacks…", Icons.Save, () =>
            {
                if (!stacked()) return;
                using (var d = new FolderBrowserDialog())
                    if (d.ShowDialog() == DialogResult.OK)
                        for (int i = 0; i < result.Stacks.Count; i++)
                        {
                            var img = result.Stacks[i];
                            string name = img.DateObs.HasValue ? Preprocessor.WinJuposName(img.DateObs.Value, "", i) : "stack_" + i;
                            TiffIO.Write(Path.Combine(d.SelectedPath, name + "_p" + resultOptions.StackPercents[i] + ".tif"), img);
                        }
            }, ButtonKind.Normal);
            // the toolbar waits for the stack, this button for a deconvolution running in Sharpen & deconvolve as well
            _sharpJob.Guard(btn("Sharpen →", Icons.Right, () => { if (stacked()) { _tabs.Select(2); SetSharpenImage(result.Stacks[shown]); } }, ButtonKind.Subtle));
            viewer.PaintOverlay += (g, map) =>
            {
                if (result == null || viewer.Image != result.Reference && viewer.Image != _stacked) return;
                using (var pen = new Pen(Theme.WithAlpha(Theme.Current.Accent, 120)))
                    foreach (var ap in result.AlignmentPoints.Where(a => a[2] == resultOptions.ApSize))
                    {
                        var p1 = map(ap[0] - ap[2] / 2.0, ap[1] - ap[2] / 2.0); var p2 = map(ap[0] + ap[2] / 2.0, ap[1] + ap[2] / 2.0);
                        g.DrawRectangle(pen, p1.X, p1.Y, p2.X - p1.X, p2.Y - p1.Y);
                    }
            };
            var side = Side("Stacking options", 360);
            side.Controls.Add(props);
            // Stop, after Analyse & stack, ends lucky stacking; the rest of the toolbar waits for the job
            var luckyStop = _luckyJob.StopButton(n => n == "Lucky stacking");
            _luckyJob.Guard(bar.Controls.Cast<Control>().ToArray());
            Ui.InsertAfter(bar, luckyStop, "Analyse & stack");
            panel.Controls.Add(viewer);
            panel.Controls.Add(plot);
            panel.Controls.Add(side);
            panel.Controls.Add(bar);
            return panel;
        }

        // ================================================================== sharpening

        private ImageViewer _sharpViewer;
        private AstroImage _sharpSource;
        private readonly Slider[] _layers = new Slider[6];
        private Slider _denoise;
        private System.Windows.Forms.Timer _sharpDebounce;

        private void SetSharpenImage(AstroImage img)
        {
            if (_sharpJob.Refuse()) return;
            _sharpSource = img; _sharpViewer.SetImage(img); UpdateSharpen();
        }

        private Control BuildSharpen()
        {
            var panel = new Panel { Dock = DockStyle.Fill };
            _sharpViewer = new ImageViewer { Dock = DockStyle.Fill, EmptyText = "Open a stacked planetary image (TIFF/FITS/PNG) or send one from Stacking", Stretch = DisplayStretch.None };
            var side = Side("Wavelets", 360);
            var g = new FormGrid(100);
            for (int i = 0; i < 6; i++)
            {
                _layers[i] = new Slider { Minimum = -50, Maximum = 400, Value = i == 0 ? 120 : i == 1 ? 60 : i == 2 ? 20 : 0, TickStyle = TickStyle.None };
                _layers[i].ValueChanged += (s, e) => { _sharpDebounce.Stop(); _sharpDebounce.Start(); };
                g.Row("Layer " + (i + 1), _layers[i]);
            }
            _denoise = new Slider { Minimum = 0, Maximum = 300, Value = 60, TickStyle = TickStyle.None };
            _denoise.ValueChanged += (s, e) => { _sharpDebounce.Stop(); _sharpDebounce.Start(); };
            g.Row("Denoise", _denoise);
            _sharpDebounce = new System.Windows.Forms.Timer { Interval = 250 };
            _sharpDebounce.Tick += (s, e) => { _sharpDebounce.Stop(); UpdateSharpen(); };
            var decFwhm = g.Row("Deconv. FWHM", Ui.Num(2.0m, 0.5m, 15, 1, 0.1m));
            var decIt = g.Row("Iterations", Ui.Num(15, 1, 200));
            var buttons = new FlowLayoutPanel { Height = 160, FlowDirection = FlowDirection.TopDown };
            Action<string, string, Action, ButtonKind> btn = (t, i, a, k) => { var b = new FlatButton(t, i, (s, e) => a()) { Kind = k, Width = 300 }; buttons.Controls.Add(b); };
            btn("Open image…", Icons.Open, () =>
            {
                if (_sharpJob.Refuse()) return;
                using (var d = new OpenFileDialog { Filter = ImageFile.OpenFilter })
                {
                    if (d.ShowDialog() != DialogResult.OK) return;
                    string path = d.FileName;
                    AstroImage loaded = null;
                    _sharpJob.Run("Opening " + Path.GetFileName(path), t => loaded = ImageFile.Load(path), () => SetSharpenImage(loaded));
                }
            }, ButtonKind.Normal);
            btn("Richardson-Lucy deconvolution (TV)", Icons.Check, () =>
            {
                if (_sharpSource == null) return;
                var src = _sharpSource.Clone(); double fw = (double)decFwhm.Value; int it = (int)decIt.Value;
                _sharpJob.Run("Deconvolution", t => Deconvolution.Run(src, Psf.Gaussian(fw), new DeconvolutionOptions { Method = DeconvolutionMethod.RichardsonLucyTV, Iterations = it, TvLambda = 0.001, DeringingAmount = 0.3 }, null, t.Cts.Token), () => { _sharpSource = src; UpdateSharpen(); });
            }, ButtonKind.Normal);
            btn("Align RGB channels (ADC)", Icons.Check, () =>
            {
                if (_sharpSource == null) { MainForm.Instance.Toast("Open an image first", Icons.Info); return; }
                if (_sharpSource.Channels != 3) { MainForm.Instance.Toast("Aligning channels needs a colour image", Icons.Info); return; }
                var s = _sharpSource.Clone();
                _sharpJob.Run("Aligning colour channels", t => ColorOps.AlignChannels(s, t.Cts.Token), () => { _sharpSource = s; UpdateSharpen(); });
            }, ButtonKind.Normal);
            btn("Save result…", Icons.Save, () =>
            {
                var img = _sharpViewer.Image; if (img == null) return;
                using (var d = new SaveFileDialog { Filter = ImageFile.SaveFilter, FileName = "planet.tif", FilterIndex = 4 })
                    if (d.ShowDialog() == DialogResult.OK) ImageFile.Save(d.FileName, img, ImageFile.FormatFromFilterIndex(d.FilterIndex));
            }, ButtonKind.Primary);
            // Stop, under the two buttons that change the image, ends a deconvolution or a channel alignment; the other buttons wait
            var sharpStop = _sharpJob.StopButton(n => n == "Deconvolution" || n == "Aligning colour channels");
            sharpStop.Width = 300;
            _sharpJob.Guard(buttons.Controls.Cast<Control>().ToArray());
            Ui.InsertAfter(buttons, sharpStop, "Align RGB channels (ADC)");
            buttons.Height = 200;
            g.Full(buttons);
            g.Full(Ui.Hint("Layer sliders amplify the à trous (B3 spline) detail scales 1, 2, 4, 8, 16, 32 px. Denoise soft-thresholds the finest layers against the measured noise. Apply deconvolution before wavelets."));
            side.Controls.Add(g);
            panel.Controls.Add(_sharpViewer);
            panel.Controls.Add(side);
            return panel;
        }

        private void UpdateSharpen()
        {
            if (_sharpSource == null) return;
            var src = _sharpSource;
            var amt = _layers.Select(l => l.Value / 100.0).ToArray();
            double dn = _denoise.Value / 100.0;
            var o = src.Clone();
            _waveletJob.Replace("Wavelets", t =>
            {
                for (int c = 0; c < o.Channels; c++) o.Data[c] = Wavelets.Sharpen(src.Data[c], src.Width, src.Height, amt, new[] { dn, dn * 0.5, 0, 0, 0, 0 });
                o.Clip(0, 1);
            }, () => _sharpViewer.SetImage(o, false));
        }

        // ================================================================== derotation

        private class DeroItem { public string Path; public AstroImage Image; public DateTime Time; public DiscGeometry Geo; public override string ToString() => System.IO.Path.GetFileName(Path) + "  " + Time.ToString("HH:mm:ss"); }

        // the images of the Derotation tab, and the action that shows them after a change
        private readonly List<DeroItem> _deroItems = new List<DeroItem>();
        private Action _deroRefresh;

        private Control BuildDerotation()
        {
            var panel = new Panel { Dock = DockStyle.Fill };
            var viewer = new ImageViewer { Dock = DockStyle.Fill, Stretch = DisplayStretch.None, EmptyText = "Add stacked images of the same planet (times from FITS DATE-OBS or WinJUPOS file names)" };
            // the image list, always shown like Photometry's frame list; its heading counts the images
            var list = new ListBox { Height = 120, IntegralHeight = false, SelectionMode = SelectionMode.MultiExtended };
            var heading = new Label { Text = "No images yet", Tag = "dim", AutoSize = true };
            var listBar = new FlowLayoutPanel { Height = 38, WrapContents = false }; // Remove selected and Clear list
            var items = _deroItems;
            var g = new FormGrid(140);
            var planet = g.Row("Planet", Ui.Combo("Jupiter", "Saturn", "Mars", "Venus"));
            var north = g.Row("Image north angle (°)", Ui.Num(0, -180, 180, 1));
            var mirror = g.Row("Mirrored", new CheckBox { Text = "Image is mirror-reversed" });
            var target = g.Row("Target time (UTC)", new TextBox());
            var ld = g.Row("Limb darkening comp.", new CheckBox { Text = "Compensate", Checked = true });
            var info = new Label { Tag = "dim", AutoSize = true, MaximumSize = new Size(340, 0), Visible = false }; // the last result, once there is one
            Func<string, DateTime?> timeFromName = n =>
            {
                // WinJUPOS: YYYY-MM-DD-HHMM_T
                var m = System.Text.RegularExpressions.Regex.Match(Path.GetFileName(n), @"(\d{4})-(\d{2})-(\d{2})-(\d{2})(\d{2})_(\d)");
                if (!m.Success) return null;
                return new DateTime(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value), int.Parse(m.Groups[4].Value), int.Parse(m.Groups[5].Value), int.Parse(m.Groups[6].Value) * 6, DateTimeKind.Utc);
            };
            Func<DeroItem, Body, double, bool, DiscGeometry> geometry = (it, b, northDeg, mirrored) =>
            {
                var lum = it.Image.Luminance();
                var ob = ObjectDetection.Detect(lum, it.Image.Width, it.Image.Height);
                var circ = ObjectDetection.FitLimb(lum, it.Image.Width, it.Image.Height, ob) ?? new[] { ob.Cx, ob.Cy, ob.Radius };
                var ctx = new SkyContext(AppState.Site, AstroTime.FromDateTime(it.Time));
                var pl = ctx.BodyPlace(b);
                double cm, lat, pa;
                PlanetPhysics.Orientation(b, ctx.JdTDB, pl.Astrometric.ToVector(), pl.LightTimeDays, out cm, out lat, out pa);
                double flat = PlanetPhysics.PolarRadiusKm(b) / PlanetPhysics.EquatorialRadiusKm(b);
                return new DiscGeometry { Cx = circ[0], Cy = circ[1], Radius = circ[2], Flattening = flat, PoleAngleDeg = pa + northDeg, SubObserverLatDeg = lat, Mirrored = mirrored };
            };
            Action refresh = () =>
            {
                items.Sort((a, b2) => a.Time.CompareTo(b2.Time));
                list.Items.Clear(); foreach (var it in items) list.Items.Add(it);
                heading.Text = items.Count == 0 ? "No images yet" : items.Count == 1 ? "1 image" : items.Count + " images, in time order";
                target.Text = items.Count > 0 ? new DateTime((long)items.Average(i => (double)i.Time.Ticks), DateTimeKind.Utc).ToString("yyyy-MM-dd HH:mm:ss") : "";
            };
            _deroRefresh = refresh;
            var bar = new FlowLayoutPanel { Height = 80, FlowDirection = FlowDirection.TopDown };
            Action<string, string, Action, ButtonKind> btn = (t, i, a, k) => { var b = new FlatButton(t, i, (s, e) => a()) { Kind = k, Width = 300 }; bar.Controls.Add(b); };
            Action addImages = () =>
            {
                if (_deroJob.Refuse()) return;
                using (var d = new OpenFileDialog { Filter = ImageFile.OpenFilter, Multiselect = true })
                {
                    if (d.ShowDialog() != DialogResult.OK) return;
                    var files = d.FileNames;
                    var added = new List<DeroItem>();
                    var untimed = new List<string>();
                    // loaded in the background; the images join the list when all are in
                    _deroJob.Run("Loading " + files.Length + " image(s)", t =>
                    {
                        for (int i = 0; i < files.Length; i++)
                        {
                            t.Cts.Token.ThrowIfCancellationRequested();
                            Tasks.Report(t, (double)i / files.Length, Path.GetFileName(files[i]));
                            var img = ImageFile.Load(files[i]);
                            var time = img.DateObs ?? timeFromName(files[i]);
                            if (time.HasValue) added.Add(new DeroItem { Path = files[i], Image = img, Time = time.Value });
                            else untimed.Add(Path.GetFileName(files[i]));
                        }
                    }, () =>
                    {
                        items.AddRange(added);
                        refresh();
                        if (untimed.Count > 0) MainForm.Instance.Toast("No time found for " + string.Join(", ", untimed) + " (use FITS DATE-OBS or WinJUPOS naming)", Icons.Warning);
                    });
                }
            };
            btn("Derotate & stack to target time", Icons.Stack, () =>
            {
                if (items.Count == 0) { MainForm.Instance.Toast("Add images first", Icons.Info); return; }
                DateTime tt;
                if (!DateTime.TryParse(target.Text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out tt)) { MainForm.Instance.Toast("Invalid target time", Icons.Warning); return; }
                Body b; Enum.TryParse((string)planet.SelectedItem, out b);
                AstroImage res = null;
                double nd = (double)north.Value; bool mr = mirror.Checked;
                _deroJob.Run("Derotating", t =>
                {
                    foreach (var it in items) { t.Cts.Token.ThrowIfCancellationRequested(); it.Geo = geometry(it, b, nd, mr); }
                    var imgs = items.Select(i => i.Image).ToList(); var geos = items.Select(i => i.Geo).ToList(); var times = items.Select(i => i.Time).ToList();
                    res = SphereDerotation.DerotateAndStack(imgs, geos, times, tt, SphereDerotation.PeriodHours(b), t.Cts.Token);
                }, () =>
                {
                    viewer.SetImage(res);
                    var g0 = items[0].Geo;
                    info.Visible = true;
                    info.Text = string.Format("Disc radius {0:0.0} px · pole PA {1:0.0}° · sub-Earth lat {2:+0.0;-0.0}° · span {3:0.0} min", g0.Radius, g0.PoleAngleDeg, g0.SubObserverLatDeg, (items.Last().Time - items.First().Time).TotalMinutes);
                    _stacked = res;
                });
            }, ButtonKind.Primary);
            btn("Save result…", Icons.Save, () => { if (viewer.Image == null) return; using (var d = new SaveFileDialog { Filter = "TIFF 16-bit|*.tif", FileName = "derotated.tif" }) if (d.ShowDialog() == DialogResult.OK) TiffIO.Write(d.FileName, viewer.Image); }, ButtonKind.Normal);
            g.Full(heading);
            // removing images, by button or with Delete; the target time follows the images that are left
            Action remove = () =>
            {
                var gone = list.SelectedItems.Cast<DeroItem>().ToList();
                if (gone.Count == 0) { MainForm.Instance.Toast("Select the images to remove in the list", Icons.Info); return; }
                if (gone.Any(it => it.Image == viewer.Image)) viewer.SetImage(null);
                items.RemoveAll(gone.Contains);
                refresh();
            };
            // Add images…, Remove selected and Clear list under the list, as for Photometry's frames
            Action<string, string, Action, ButtonKind> small = (t, i, a, k) => { var b = new FlatButton(t, i, (s, e) => a()) { Kind = k }; b.AutoSizeToText(); listBar.Controls.Add(b); };
            small("Add images…", Icons.Add, addImages, ButtonKind.Normal);
            small("Remove selected", Icons.Remove, remove, ButtonKind.Subtle);
            small("Clear list", Icons.Close, () => { if (items.Any(it => it.Image == viewer.Image)) viewer.SetImage(null); items.Clear(); refresh(); }, ButtonKind.Subtle);
            list.KeyDown += (s, e) => { if (e.KeyCode == Keys.Delete) { remove(); e.Handled = true; } };
            g.Full(list);
            g.Full(listBar);
            g.Full(bar);
            g.Full(info);
            g.Full(Ui.Hint("Each image is re-projected on an oblate spheroid as if taken at the target time, using the planet's rotation period, the sub-Earth latitude and pole position angle from the ephemeris. Set 'image north angle' if your images are not north-up."));
            list.SelectedIndexChanged += (s, e) => { var it = list.SelectedItem as DeroItem; if (it != null) viewer.SetImage(it.Image); };
            // Stop, under Derotate & stack, ends derotating or loading images; everything else waits for the job
            var deroStop = _deroJob.StopButton(n => n == "Derotating" || n.StartsWith("Loading "));
            deroStop.Width = 300;
            _deroJob.Guard(g.Controls.Cast<Control>().Where(c => c != bar).Concat(bar.Controls.Cast<Control>()).ToArray());
            Ui.InsertAfter(bar, deroStop, "Derotate & stack to target time");
            bar.Height = 120;
            var side = Side("Derotation", 380);
            side.Controls.Add(g);
            panel.Controls.Add(viewer);
            panel.Controls.Add(side);
            return panel;
        }

        // ================================================================== solar & lunar

        private Control BuildSolar()
        {
            var panel = new Panel { Dock = DockStyle.Fill };
            var viewer = new ImageViewer { Dock = DockStyle.Fill, Stretch = DisplayStretch.None, EmptyText = "Open a full-disc solar or lunar image" };
            AstroImage img = null;
            double[] limb = null;
            var g = new FormGrid(130);
            var gain = g.Row("Prominence gain", Ui.Num(6, 1, 50, 1));
            var gamma = g.Row("Prominence gamma", Ui.Num(0.6m, 0.1m, 2, 2, 0.05m));
            var flat = g.Row("Limb flattening", Ui.Num(1, 0, 1, 2, 0.05m));
            var bar = new FlowLayoutPanel { Height = 330, FlowDirection = FlowDirection.TopDown };
            Action<string, string, Action, ButtonKind> btn = (t, i, a, k) => { var b = new FlatButton(t, i, (s, e) => { try { a(); } catch (Exception ex) { MainForm.Instance.Toast(ex.Message, Icons.Warning); } }) { Kind = k, Width = 300 }; bar.Controls.Add(b); };
            Func<AstroImage, double[]> findLimb = im =>
            {
                var lum = im.Luminance();
                var ob = ObjectDetection.Detect(lum, im.Width, im.Height);
                return ObjectDetection.FitLimb(lum, im.Width, im.Height, ob);
            };
            Func<double[], string> describe = l => l != null ? string.Format("Limb: centre ({0:0.0}, {1:0.0}), radius {2:0.0} px", l[0], l[1], l[2]) : "No disc found";
            // every tool works on the image in the background (finding the limb first when it needs it), then shows the result;
            // the buttons wait meanwhile
            Action<string, bool, Func<AstroImage, double[], AstroImage>> tool = (name, needsLimb, op) =>
            {
                if (img == null) { MainForm.Instance.Toast("Open an image first", Icons.Info); return; }
                var src = img; var l = limb; AstroImage result = null;
                _solarJob.Run(name, t =>
                {
                    if (needsLimb && l == null) l = findLimb(src);
                    if (needsLimb && l == null) throw new InvalidOperationException("No disc found");
                    result = op(src, l);
                }, () =>
                {
                    limb = l;
                    if (result != null) { img = result; viewer.SetImage(img, false); }
                    else { viewer.Invalidate(); MainForm.Instance.Toast(describe(limb)); }
                });
            };
            btn("Open image…", Icons.Open, () =>
            {
                if (_solarJob.Refuse()) return;
                using (var d = new OpenFileDialog { Filter = ImageFile.OpenFilter })
                {
                    if (d.ShowDialog() != DialogResult.OK) return;
                    string path = d.FileName;
                    AstroImage loaded = null;
                    _solarJob.Run("Opening " + Path.GetFileName(path), t => loaded = ImageFile.Load(path), () => { img = loaded; limb = null; viewer.SetImage(img); });
                }
            }, ButtonKind.Normal);
            btn("Detect limb", Icons.Target, () => { limb = null; tool("Detecting the limb", true, (s, l) => null); }, ButtonKind.Normal);
            // the settings are read here, on the UI thread, before the work starts
            btn("Flatten limb darkening", Icons.Check, () => { double fl = (double)flat.Value; tool("Flattening limb darkening", true, (s, l) => { var o = s.Clone(); SolarTools.FlattenLimbDarkening(o, l[0], l[1], l[2], fl); return o; }); }, ButtonKind.Normal);
            btn("Enhance prominences", Icons.Check, () => { double ga = (double)gain.Value, gm = (double)gamma.Value; tool("Enhancing prominences", true, (s, l) => { var o = s.Clone(); SolarTools.EnhanceProminences(o, l[0], l[1], l[2] * 1.005, ga, gm); return o; }); }, ButtonKind.Normal);
            btn("Colourise H-alpha", Icons.Check, () => tool("Colourising", false, (s, l) => ColorOps.Colorize(s.Channels > 1 ? s.ToMono() : s, 1.0, 0.45, 0.15)), ButtonKind.Normal);
            btn("Invert (negative)", Icons.Check, () => tool("Inverting", false, (s, l) => { var o = s.Clone(); ColorOps.Invert(o); return o; }), ButtonKind.Normal);
            btn("Wavelet sharpen", Icons.Check, () => tool("Wavelet sharpening", false, (s, l) => { var o = s.Clone(); for (int c = 0; c < o.Channels; c++) o.Data[c] = Wavelets.Sharpen(o.Data[c], o.Width, o.Height, new[] { 1.0, 0.6, 0.2, 0, 0 }, new[] { 0.5, 0.2, 0, 0, 0 }); o.Clip(0, 1); return o; }), ButtonKind.Normal);
            btn("Stitch mosaic tiles…", Icons.Grid, () =>
            {
                using (var d = new OpenFileDialog { Filter = ImageFile.OpenFilter, Multiselect = true, Title = "Select overlapping tiles in order (left→right, top→bottom)" })
                {
                    if (d.ShowDialog() != DialogResult.OK) return;
                    var cols = PlannerView.Prompt("Mosaic", "Tiles per row:", "2");
                    int n; if (!int.TryParse(cols, out n) || n < 1) n = 2;
                    var tiles = new List<MosaicStitcher.Tile>();
                    var files = d.FileNames.OrderBy(f => f).ToList();
                    AstroImage stitched = null;
                    _solarJob.Run("Stitching mosaic", t =>
                    {
                        for (int i = 0; i < files.Count; i++)
                        {
                            t.Cts.Token.ThrowIfCancellationRequested();
                            var ti = ImageFile.Load(files[i]);
                            tiles.Add(new MosaicStitcher.Tile { Image = ti, X = (i % n) * ti.Width * 0.8, Y = (i / n) * ti.Height * 0.8 });
                        }
                        stitched = MosaicStitcher.Stitch(tiles, true, t.Cts.Token);
                    }, () => { img = stitched; limb = null; viewer.SetImage(img); });
                }
            }, ButtonKind.Normal);
            btn("Save…", Icons.Save, () => { if (img == null) return; using (var d = new SaveFileDialog { Filter = ImageFile.SaveFilter, FileName = "solar.tif", FilterIndex = 4 }) if (d.ShowDialog() == DialogResult.OK) ImageFile.Save(d.FileName, img, ImageFile.FormatFromFilterIndex(d.FilterIndex)); }, ButtonKind.Primary);
            g.Full(bar);
            viewer.PaintOverlay += (gr, map) =>
            {
                if (limb == null) return;
                var c = map(limb[0], limb[1]); var e = map(limb[0] + limb[2], limb[1]);
                float r = e.X - c.X;
                using (var pen = new Pen(Theme.Current.Warning, 1.2f) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash }) gr.DrawEllipse(pen, c.X - r, c.Y - r, 2 * r, 2 * r);
            };
            // Stop, under Stitch mosaic tiles, ends stitching; the other tools wait for the job
            var solarStop = _solarJob.StopButton(n => n == "Stitching mosaic");
            solarStop.Width = 300;
            _solarJob.Guard(bar.Controls.Cast<Control>().ToArray());
            Ui.InsertAfter(bar, solarStop, "Stitch mosaic tiles…");
            bar.Height += 40;
            var side = Side("Solar & lunar tools", 360);
            side.Controls.Add(g);
            panel.Controls.Add(viewer);
            panel.Controls.Add(side);
            return panel;
        }

        public IEnumerable<AppCommand> GetCommands()
        {
            yield return new AppCommand { Title = "Planetary: open video (SER/AVI)", Icon = Icons.Open, Run = () => { MainForm.Instance.Navigate("planetary"); _tabs.Select(0); } };
            yield return new AppCommand { Title = "Planetary: lucky stacking", Run = () => { MainForm.Instance.Navigate("planetary"); _tabs.Select(1); } };
            yield return new AppCommand { Title = "Planetary: wavelet sharpening", Run = () => { MainForm.Instance.Navigate("planetary"); _tabs.Select(2); } };
            yield return new AppCommand { Title = "Planetary: derotation", Run = () => { MainForm.Instance.Navigate("planetary"); _tabs.Select(3); } };
        }
    }
}
