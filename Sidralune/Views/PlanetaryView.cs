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
        private IFrameSource _source;
        private AstroImage _stacked;

        public PlanetaryView()
        {
            Controls.Add(_tabs);
            _tabs.AddPage("Pre-process", BuildPipp());
            _tabs.AddPage("Stacking", BuildLucky());
            _tabs.AddPage("Sharpen & deconvolve", BuildSharpen());
            _tabs.AddPage("Derotation", BuildDerotation());
            _tabs.AddPage("Solar & lunar", BuildSolar());
        }

        private static Card Side(string title, int width) => new Card(title) { Dock = DockStyle.Right, Width = width, AutoScroll = true };

        private bool OpenSource()
        {
            using (var d = new OpenFileDialog { Filter = "Video & images|*.ser;*.avi;*.fit;*.fits;*.tif;*.tiff;*.png;*.bmp;*.jpg|All|*.*", Multiselect = true })
            {
                if (d.ShowDialog() != DialogResult.OK) return false;
                SetSource(FrameSourceFactory.Open(d.FileNames.OrderBy(f => f).ToList()));
                return true;
            }
        }

        private void SetSource(IFrameSource source)
        {
            _source?.Dispose();
            _source = source;
            MainForm.Instance.Toast(string.Format("{0}: {1} frames, {2}×{3}, {4}-bit{5}", Path.GetFileName(_source.Path), _source.FrameCount, _source.Width, _source.Height, _source.BitDepth, _source.Bayer != BayerPattern.None ? ", Bayer " + _source.Bayer : ""));
        }

        private Action _showPreprocessSource;

        /// <summary>Open a recorded video in the Pre-process tab (Observatory → Imaging → Open in Planetary).</summary>
        public void OpenVideo(string path)
        {
            SetSource(FrameSourceFactory.Open(path));
            _tabs.Select(0);
            _showPreprocessSource?.Invoke();
        }

        // ================================================================== PIPP

        private Control BuildPipp()
        {
            var panel = new Panel { Dock = DockStyle.Fill };
            var viewer = new ImageViewer { Dock = DockStyle.Fill, EmptyText = "Open a SER/AVI video or an image sequence" };
            var scrub = new Slider { Dock = DockStyle.Bottom, Height = 30, TickStyle = TickStyle.None };
            var plot = new PlotControl { Dock = DockStyle.Bottom, Height = 150, Title = "Frame quality", XLabel = "frame", ShowLegend = true };
            var opt = new PippOptions { OutputFolder = Path.Combine(AppState.Settings.ImageFolder, "Planetary") };
            var props = new PropertyGrid { Dock = DockStyle.Fill, HelpVisible = false, SelectedObject = new GridAdapter(opt, "Pre-process"), PropertySort = PropertySort.NoSort };
            var side = Side("Options", 380);
            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 44 };
            var planet = Ui.Combo("(no derotation target)", "Jupiter", "Saturn", "Mars", "Venus", "Moon", "Sun", "Mercury", "Uranus", "Neptune");
            planet.Width = 200;
            Action<string, string, Action, ButtonKind> btn = (t, i, a, k) => { var b = new FlatButton(t, i, (s, e) => a()) { Kind = k }; b.AutoSizeToText(); bar.Controls.Add(b); };
            _showPreprocessSource = () => { scrub.Maximum = Math.Max(0, _source.FrameCount - 1); scrub.Value = 0; viewer.SetImage(Preprocessor.PrepareFrame(_source, 0, opt)); };
            btn("Open source…", Icons.Open, () => { if (OpenSource()) _showPreprocessSource(); }, ButtonKind.Normal);
            btn("Output folder…", Icons.Open, () => { using (var d = new FolderBrowserDialog { SelectedPath = opt.OutputFolder }) if (d.ShowDialog() == DialogResult.OK) { opt.OutputFolder = d.SelectedPath; props.Refresh(); } }, ButtonKind.Subtle);
            bar.Controls.Add(new Label { Text = "Field derotation target:", AutoSize = true, Margin = new Padding(0, 8, 4, 0), Tag = "dim" });
            bar.Controls.Add(planet);
            btn("Process", Icons.Play, () =>
            {
                if (_source == null) { MainForm.Instance.Toast("Open a source first", Icons.Info); return; }
                if (planet.SelectedIndex > 0)
                {
                    Body b; Enum.TryParse((string)planet.SelectedItem, out b);
                    var t0 = _source.Timestamp(0) ?? DateTime.UtcNow;
                    var p = new SkyContext(AppState.Site, AstroTime.FromDateTime(t0)).BodyPlace(b);
                    opt.Site = AppState.Site; opt.TargetRaRad = p.Astrometric.Ra; opt.TargetDecRad = p.Astrometric.Dec;
                    opt.PlanetCode = ((string)planet.SelectedItem).Substring(0, 1);
                }
                var src = _source;
                PippResult res = null;
                Tasks.Run("Pre-processing", t => res = Preprocessor.Run(src, opt, new Progress<double>(f => Tasks.Report(t, f)), t.Cts.Token), () =>
                {
                    plot.Clear();
                    var all = plot.AddSeries("quality", SeriesStyle.Scatter);
                    var kept = plot.AddSeries("kept", SeriesStyle.Scatter, pal => pal.Success);
                    foreach (var f in res.Frames) { if (f.Rejected) all.Add(f.Index, f.Quality); else kept.Add(f.Index, f.Quality); }
                    plot.Invalidate();
                    MainForm.Instance.Toast("Pre-process: " + res.Summary, Icons.Check, Theme.Current.Success);
                    if (res.OutputFiles.Count > 0 && res.OutputFiles[0].EndsWith(".ser")) { _source?.Dispose(); _source = new SerReader(res.OutputFiles[0]); }
                });
            }, ButtonKind.Primary);
            scrub.ValueChanged += (s, e) => { if (_source != null) { try { viewer.SetImage(Preprocessor.PrepareFrame(_source, scrub.Value, opt), false); } catch { } } };
            viewer.PaintOverlay += (g, map) =>
            {
                if (viewer.Image == null) return;
                var ob = ObjectDetection.Detect(viewer.Image.Luminance(), viewer.Image.Width, viewer.Image.Height, opt.DetectionThreshold);
                if (!ob.Found) return;
                var a = map(ob.X0, ob.Y0); var b2 = map(ob.X1, ob.Y1); var c = map(ob.Cx, ob.Cy);
                using (var pen = new Pen(Theme.Current.Success, 1.5f)) { g.DrawRectangle(pen, a.X, a.Y, b2.X - a.X, b2.Y - a.Y); g.DrawLine(pen, c.X - 8, c.Y, c.X + 8, c.Y); g.DrawLine(pen, c.X, c.Y - 8, c.X, c.Y + 8); }
            };
            side.Controls.Add(props);
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
            var viewer = new ImageViewer { Dock = DockStyle.Fill, EmptyText = "Open a SER/AVI (ideally centred in Pre-process) and press Stack" };
            var stacker = new LuckyStacker();
            var props = new PropertyGrid { Dock = DockStyle.Fill, HelpVisible = false, SelectedObject = new GridAdapter(stacker.Options, "Stacking"), PropertySort = PropertySort.NoSort };
            var plot = new PlotControl { Dock = DockStyle.Bottom, Height = 160, Title = "Frame quality (sorted)", XLabel = "frames (%)", YLabel = "quality" };
            var percents = new TextBox { Text = "10, 25, 50", Width = 120 };
            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 44 };
            LuckyResult result = null;
            int shown = 0;
            Action<string, string, Action, ButtonKind> btn = (t, i, a, k) => { var b = new FlatButton(t, i, (s, e) => a()) { Kind = k }; b.AutoSizeToText(); bar.Controls.Add(b); };
            btn("Open source…", Icons.Open, () => { if (OpenSource()) viewer.SetImage(_source.ReadFrame(0)); }, ButtonKind.Normal);
            bar.Controls.Add(new Label { Text = "Stack %:", AutoSize = true, Margin = new Padding(6, 8, 4, 0), Tag = "dim" });
            bar.Controls.Add(percents);
            btn("Analyse & stack", Icons.Stack, () =>
            {
                if (_source == null) { MainForm.Instance.Toast("Open a source first", Icons.Info); return; }
                stacker.Options.StackPercents = percents.Text.Split(',').Select(p => AstroMath.ParseInvariant(p)).Where(p => !double.IsNaN(p) && p > 0 && p <= 100).DefaultIfEmpty(25).ToArray();
                var src = _source;
                Tasks.Run("Lucky stacking", t => result = stacker.Run(src, new Progress<string>(s => t.Status = s), new Progress<double>(f => Tasks.Report(t, f)), t.Cts.Token), () =>
                {
                    shown = 0;
                    _stacked = result.Stacks[0];
                    viewer.SetImage(_stacked);
                    plot.Clear();
                    var q = result.Frames.Select(f => f.Quality).OrderByDescending(x => x).ToList();
                    var s = plot.AddSeries("quality");
                    for (int i = 0; i < q.Count; i++) s.Add(100.0 * i / q.Count, q[i] / q[0]);
                    foreach (var pct in stacker.Options.StackPercents) plot.Markers.Add(new PlotMarker { X = pct, Label = pct + "%" });
                    plot.Invalidate();
                    MainForm.Instance.Toast(result.Report, Icons.Check, Theme.Current.Success);
                });
            }, ButtonKind.Primary);
            btn("Next stack", Icons.Right, () => { if (result != null && result.Stacks.Count > 0) { shown = (shown + 1) % result.Stacks.Count; _stacked = result.Stacks[shown]; viewer.SetImage(_stacked, false); MainForm.Instance.SetStatus("Showing best " + stacker.Options.StackPercents[shown] + "%"); } }, ButtonKind.Subtle);
            btn("Save stacks…", Icons.Save, () =>
            {
                if (result == null) return;
                using (var d = new FolderBrowserDialog())
                    if (d.ShowDialog() == DialogResult.OK)
                        for (int i = 0; i < result.Stacks.Count; i++)
                        {
                            var img = result.Stacks[i];
                            string name = img.DateObs.HasValue ? Preprocessor.WinJuposName(img.DateObs.Value, "", i) : "stack_" + i;
                            TiffIO.Write(Path.Combine(d.SelectedPath, name + "_p" + stacker.Options.StackPercents[i] + ".tif"), img);
                        }
            }, ButtonKind.Normal);
            btn("Sharpen →", Icons.Right, () => { if (_stacked != null) { _tabs.Select(2); SetSharpenImage(_stacked); } }, ButtonKind.Subtle);
            viewer.PaintOverlay += (g, map) =>
            {
                if (result == null || viewer.Image != result.Reference && viewer.Image != _stacked) return;
                using (var pen = new Pen(Theme.WithAlpha(Theme.Current.Accent, 120)))
                    foreach (var ap in result.AlignmentPoints.Where(a => a[2] == stacker.Options.ApSize))
                    {
                        var p1 = map(ap[0] - ap[2] / 2.0, ap[1] - ap[2] / 2.0); var p2 = map(ap[0] + ap[2] / 2.0, ap[1] + ap[2] / 2.0);
                        g.DrawRectangle(pen, p1.X, p1.Y, p2.X - p1.X, p2.Y - p1.Y);
                    }
            };
            var side = Side("Stacking options", 360);
            side.Controls.Add(props);
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

        private void SetSharpenImage(AstroImage img) { _sharpSource = img; _sharpViewer.SetImage(img); UpdateSharpen(); }

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
            btn("Open image…", Icons.Open, () => { using (var d = new OpenFileDialog { Filter = ImageFile.OpenFilter }) if (d.ShowDialog() == DialogResult.OK) SetSharpenImage(ImageFile.Load(d.FileName)); }, ButtonKind.Normal);
            btn("Richardson-Lucy deconvolution (TV)", Icons.Check, () =>
            {
                if (_sharpSource == null) return;
                var src = _sharpSource.Clone(); double fw = (double)decFwhm.Value; int it = (int)decIt.Value;
                Tasks.Run("Deconvolution", t => Deconvolution.Run(src, Psf.Gaussian(fw), new DeconvolutionOptions { Method = DeconvolutionMethod.RichardsonLucyTV, Iterations = it, TvLambda = 0.001, DeringingAmount = 0.3 }), () => { _sharpSource = src; UpdateSharpen(); });
            }, ButtonKind.Normal);
            btn("Align RGB channels (ADC)", Icons.Check, () => { if (_sharpSource != null && _sharpSource.Channels == 3) { var s = _sharpSource.Clone(); ColorOps.AlignChannels(s); _sharpSource = s; UpdateSharpen(); } }, ButtonKind.Normal);
            btn("Save result…", Icons.Save, () =>
            {
                var img = _sharpViewer.Image; if (img == null) return;
                using (var d = new SaveFileDialog { Filter = ImageFile.SaveFilter, FileName = "planet.tif", FilterIndex = 4 })
                    if (d.ShowDialog() == DialogResult.OK) ImageFile.Save(d.FileName, img, ImageFile.FormatFromFilterIndex(d.FilterIndex));
            }, ButtonKind.Primary);
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
            Tasks.Run("Wavelets", t =>
            {
                var o = src.Clone();
                for (int c = 0; c < o.Channels; c++) o.Data[c] = Wavelets.Sharpen(src.Data[c], src.Width, src.Height, amt, new[] { dn, dn * 0.5, 0, 0, 0, 0 });
                o.Clip(0, 1);
                MainForm.Instance.Post(() => _sharpViewer.SetImage(o, false));
            });
        }

        // ================================================================== derotation

        private class DeroItem { public string Path; public AstroImage Image; public DateTime Time; public DiscGeometry Geo; public override string ToString() => System.IO.Path.GetFileName(Path) + "  " + Time.ToString("HH:mm:ss"); }

        private Control BuildDerotation()
        {
            var panel = new Panel { Dock = DockStyle.Fill };
            var viewer = new ImageViewer { Dock = DockStyle.Fill, Stretch = DisplayStretch.None, EmptyText = "Add stacked images of the same planet (times from FITS DATE-OBS or WinJUPOS file names)" };
            var list = new ListBox { Dock = DockStyle.Top, Height = 150 };
            var items = new List<DeroItem>();
            var g = new FormGrid(140);
            var planet = g.Row("Planet", Ui.Combo("Jupiter", "Saturn", "Mars", "Venus"));
            var north = g.Row("Image north angle (°)", Ui.Num(0, -180, 180, 1));
            var mirror = g.Row("Mirrored", new CheckBox { Text = "Image is mirror-reversed" });
            var target = g.Row("Target time (UTC)", new TextBox());
            var ld = g.Row("Limb darkening comp.", new CheckBox { Text = "Compensate", Checked = true });
            var info = new Label { Tag = "dim", Height = 70, AutoSize = false };
            g.Full(info);
            Func<string, DateTime?> timeFromName = n =>
            {
                // WinJUPOS: YYYY-MM-DD-HHMM_T
                var m = System.Text.RegularExpressions.Regex.Match(Path.GetFileName(n), @"(\d{4})-(\d{2})-(\d{2})-(\d{2})(\d{2})_(\d)");
                if (!m.Success) return null;
                return new DateTime(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value), int.Parse(m.Groups[4].Value), int.Parse(m.Groups[5].Value), int.Parse(m.Groups[6].Value) * 6, DateTimeKind.Utc);
            };
            Func<DeroItem, DiscGeometry> geometry = it =>
            {
                Body b; Enum.TryParse((string)planet.SelectedItem, out b);
                var lum = it.Image.Luminance();
                var ob = ObjectDetection.Detect(lum, it.Image.Width, it.Image.Height);
                var circ = ObjectDetection.FitLimb(lum, it.Image.Width, it.Image.Height, ob) ?? new[] { ob.Cx, ob.Cy, ob.Radius };
                var ctx = new SkyContext(AppState.Site, AstroTime.FromDateTime(it.Time));
                var pl = ctx.BodyPlace(b);
                double cm, lat, pa;
                PlanetPhysics.Orientation(b, ctx.JdTDB, pl.Astrometric.ToVector(), pl.LightTimeDays, out cm, out lat, out pa);
                double flat = PlanetPhysics.PolarRadiusKm(b) / PlanetPhysics.EquatorialRadiusKm(b);
                return new DiscGeometry { Cx = circ[0], Cy = circ[1], Radius = circ[2], Flattening = flat, PoleAngleDeg = pa + (double)north.Value, SubObserverLatDeg = lat, Mirrored = mirror.Checked };
            };
            var bar = new FlowLayoutPanel { Height = 120, FlowDirection = FlowDirection.TopDown };
            Action<string, string, Action, ButtonKind> btn = (t, i, a, k) => { var b = new FlatButton(t, i, (s, e) => a()) { Kind = k, Width = 300 }; bar.Controls.Add(b); };
            btn("Add images…", Icons.Add, () =>
            {
                using (var d = new OpenFileDialog { Filter = ImageFile.OpenFilter, Multiselect = true })
                {
                    if (d.ShowDialog() != DialogResult.OK) return;
                    foreach (var f in d.FileNames)
                    {
                        var img = ImageFile.Load(f);
                        var t = img.DateObs ?? timeFromName(f);
                        if (!t.HasValue) { MainForm.Instance.Toast("No time found for " + Path.GetFileName(f) + " (use FITS DATE-OBS or WinJUPOS naming)", Icons.Warning); continue; }
                        items.Add(new DeroItem { Path = f, Image = img, Time = t.Value });
                    }
                    items.Sort((a, b2) => a.Time.CompareTo(b2.Time));
                    list.Items.Clear(); foreach (var it in items) list.Items.Add(it);
                    if (items.Count > 0) target.Text = new DateTime((long)items.Average(i => (double)i.Time.Ticks), DateTimeKind.Utc).ToString("yyyy-MM-dd HH:mm:ss");
                }
            }, ButtonKind.Normal);
            btn("Derotate & stack to target time", Icons.Stack, () =>
            {
                if (items.Count == 0) return;
                DateTime tt;
                if (!DateTime.TryParse(target.Text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out tt)) { MainForm.Instance.Toast("Invalid target time", Icons.Warning); return; }
                Body b; Enum.TryParse((string)planet.SelectedItem, out b);
                AstroImage res = null;
                Tasks.Run("Derotating", t =>
                {
                    foreach (var it in items) it.Geo = geometry(it);
                    var imgs = items.Select(i => i.Image).ToList(); var geos = items.Select(i => i.Geo).ToList(); var times = items.Select(i => i.Time).ToList();
                    res = SphereDerotation.DerotateAndStack(imgs, geos, times, tt, SphereDerotation.PeriodHours(b));
                }, () =>
                {
                    viewer.SetImage(res);
                    var g0 = items[0].Geo;
                    info.Text = string.Format("Disc radius {0:0.0} px · pole PA {1:0.0}° · sub-Earth lat {2:+0.0;-0.0}° · span {3:0.0} min", g0.Radius, g0.PoleAngleDeg, g0.SubObserverLatDeg, (items.Last().Time - items.First().Time).TotalMinutes);
                    _stacked = res;
                });
            }, ButtonKind.Primary);
            btn("Save result…", Icons.Save, () => { if (viewer.Image == null) return; using (var d = new SaveFileDialog { Filter = "TIFF 16-bit|*.tif", FileName = "derotated.tif" }) if (d.ShowDialog() == DialogResult.OK) TiffIO.Write(d.FileName, viewer.Image); }, ButtonKind.Normal);
            g.Full(bar);
            g.Full(Ui.Hint("Each image is re-projected on an oblate spheroid as if taken at the target time, using the planet's rotation period, the sub-Earth latitude and pole position angle from the ephemeris. Set 'image north angle' if your images are not north-up."));
            list.SelectedIndexChanged += (s, e) => { var it = list.SelectedItem as DeroItem; if (it != null) viewer.SetImage(it.Image); };
            var side = Side("Derotation", 380);
            side.Controls.Add(g);
            side.Controls.Add(list);
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
            Action show = () => viewer.SetImage(img, false);
            Action fit = () =>
            {
                var lum = img.Luminance();
                var ob = ObjectDetection.Detect(lum, img.Width, img.Height);
                limb = ObjectDetection.FitLimb(lum, img.Width, img.Height, ob);
                MainForm.Instance.Toast(limb != null ? string.Format("Limb: centre ({0:0.0}, {1:0.0}), radius {2:0.0} px", limb[0], limb[1], limb[2]) : "No disc found");
            };
            btn("Open image…", Icons.Open, () => { using (var d = new OpenFileDialog { Filter = ImageFile.OpenFilter }) if (d.ShowDialog() == DialogResult.OK) { img = ImageFile.Load(d.FileName); limb = null; viewer.SetImage(img); } }, ButtonKind.Normal);
            btn("Detect limb", Icons.Target, () => { if (img != null) { fit(); viewer.Invalidate(); } }, ButtonKind.Normal);
            btn("Flatten limb darkening", Icons.Check, () => { if (img == null) return; if (limb == null) fit(); img = img.Clone(); SolarTools.FlattenLimbDarkening(img, limb[0], limb[1], limb[2], (double)flat.Value); show(); }, ButtonKind.Normal);
            btn("Enhance prominences", Icons.Check, () => { if (img == null) return; if (limb == null) fit(); img = img.Clone(); SolarTools.EnhanceProminences(img, limb[0], limb[1], limb[2] * 1.005, (double)gain.Value, (double)gamma.Value); show(); }, ButtonKind.Normal);
            btn("Colourise H-alpha", Icons.Check, () => { if (img == null) return; img = ColorOps.Colorize(img.Channels > 1 ? img.ToMono() : img, 1.0, 0.45, 0.15); show(); }, ButtonKind.Normal);
            btn("Invert (negative)", Icons.Check, () => { if (img == null) return; img = img.Clone(); ColorOps.Invert(img); show(); }, ButtonKind.Normal);
            btn("Wavelet sharpen", Icons.Check, () => { if (img == null) return; var o = img.Clone(); for (int c = 0; c < o.Channels; c++) o.Data[c] = Wavelets.Sharpen(o.Data[c], o.Width, o.Height, new[] { 1.0, 0.6, 0.2, 0, 0 }, new[] { 0.5, 0.2, 0, 0, 0 }); o.Clip(0, 1); img = o; show(); }, ButtonKind.Normal);
            btn("Stitch mosaic tiles…", Icons.Grid, () =>
            {
                using (var d = new OpenFileDialog { Filter = ImageFile.OpenFilter, Multiselect = true, Title = "Select overlapping tiles in order (left→right, top→bottom)" })
                {
                    if (d.ShowDialog() != DialogResult.OK) return;
                    var cols = PlannerView.Prompt("Mosaic", "Tiles per row:", "2");
                    int n; if (!int.TryParse(cols, out n) || n < 1) n = 2;
                    var tiles = new List<MosaicStitcher.Tile>();
                    var files = d.FileNames.OrderBy(f => f).ToList();
                    Tasks.Run("Stitching mosaic", t =>
                    {
                        for (int i = 0; i < files.Count; i++)
                        {
                            var ti = ImageFile.Load(files[i]);
                            tiles.Add(new MosaicStitcher.Tile { Image = ti, X = (i % n) * ti.Width * 0.8, Y = (i / n) * ti.Height * 0.8 });
                        }
                        img = MosaicStitcher.Stitch(tiles);
                    }, () => viewer.SetImage(img));
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
            var side = Side("Solar & lunar tools", 360);
            side.Controls.Add(g);
            panel.Controls.Add(viewer);
            panel.Controls.Add(side);
            return panel;
        }

        public IEnumerable<AppCommand> GetCommands()
        {
            yield return new AppCommand { Title = "Planetary: open video (SER/AVI)", Icon = Icons.Open, Run = () => { MainForm.Instance.Navigate("planetary"); _tabs.Select(0); } };
            yield return new AppCommand { Title = "Planetary: stacking", Run = () => { MainForm.Instance.Navigate("planetary"); _tabs.Select(1); } };
            yield return new AppCommand { Title = "Planetary: wavelet sharpening", Run = () => { MainForm.Instance.Navigate("planetary"); _tabs.Select(2); } };
            yield return new AppCommand { Title = "Planetary: derotation", Run = () => { MainForm.Instance.Navigate("planetary"); _tabs.Select(3); } };
        }
    }
}
