using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Sidralune.Core;
using Sidralune.Core.Catalogs;
using Sidralune.Core.Mathematics;
using Sidralune.Core.Observing;
using Sidralune.Core.Time;
using Sidralune.Devices;
using Sidralune.Imaging;
using Sidralune.Imaging.IO;
using Sidralune.Science.Astrometry;
using Sidralune.UI;

namespace Sidralune.Views
{
    public class AstrometryView : UserControl, ICommandSource
    {
        private readonly TabbedPanel _tabs = new TabbedPanel { Dock = DockStyle.Fill, Padding = new Padding(10) };
        private readonly ImageViewer _viewer = new ImageViewer { Dock = DockStyle.Fill, EmptyText = "Open an image to plate solve (FITS headers with RA/DEC, FOCALLEN and XPIXSZ speed things up)" };
        private AstroImage _img;
        private SolveResult _solve;
        // colours are palette roles, resolved when painting so the annotations follow day/night switches
        private List<Tuple<string, double, double, Func<Palette, Color>>> _annotations = new List<Tuple<string, double, double, Func<Palette, Color>>>();
        private readonly TextBox _result = new TextBox { Multiline = true, ReadOnly = true, Dock = DockStyle.Bottom, Height = 150, Font = Theme.MonoFont, ScrollBars = ScrollBars.Vertical };
        private readonly SkyChart _frameChart = new SkyChart { Dock = DockStyle.Fill, Mode = ChartMode.Atlas, FovDeg = 6 };
        private SelectedObject _frameTarget;
        private NumericUpDown _rows, _cols, _overlap, _rot;

        public AstrometryView()
        {
            Controls.Add(_tabs);
            _tabs.AddPage("Plate solving", BuildSolve());
            _tabs.AddPage("Framing & mosaics", BuildFraming());
            _tabs.AddPage("Exposure planner", BuildExposure());
        }

        // ================================================================== solving

        private Control BuildSolve()
        {
            var panel = new Panel { Dock = DockStyle.Fill };
            var side = new Card("Solver") { Dock = DockStyle.Right, Width = 360, AutoScroll = true };
            var g = new FormGrid(130);
            var solver = g.Row("Solver", Ui.Combo("Internal + Gaia DR3 (online)", "Internal (local catalog)", "ASTAP (installed)", "astrometry.net (online)"));
            var ra = g.Row("Hint RA", new TextBox());
            var dec = g.Row("Hint Dec", new TextBox());
            var radius = g.Row("Search radius (°)", Ui.Num(5, 0.5m, 90, 1));
            var scale = g.Row("Scale (\"/px, 0=auto)", Ui.Num(0, 0, 200, 3, 0.1m));
            var sip = g.Row("SIP order", Ui.Num(2, 0, 4));
            var buttons = new FlowLayoutPanel { Height = 200, FlowDirection = FlowDirection.TopDown };
            Action<string, string, Action, ButtonKind> btn = (t, i, a, k) => { var b = new FlatButton(t, i, (s, e) => { try { a(); } catch (Exception ex) { MainForm.Instance.Toast(ex.Message, Icons.Warning); } }) { Kind = k, Width = 320 }; buttons.Controls.Add(b); };
            btn("Open image…", Icons.Open, () => { using (var d = new OpenFileDialog { Filter = ImageFile.OpenFilter }) if (d.ShowDialog() == DialogResult.OK) { var img = ImageFile.Load(d.FileName); SetImage(img, ra, dec, scale); } }, ButtonKind.Normal);
            btn("Solve", Icons.Target, () => Solve(solver.SelectedIndex, ra.Text, dec.Text, (double)radius.Value, (double)scale.Value, (int)sip.Value), ButtonKind.Primary);
            btn("Write WCS to FITS…", Icons.Save, () =>
            {
                if (_solve == null || !_solve.Success) return;
                using (var d = new SaveFileDialog { Filter = "FITS|*.fits", FileName = Path.GetFileNameWithoutExtension(_img.SourcePath ?? "solved") + "_wcs.fits" })
                    if (d.ShowDialog() == DialogResult.OK) { _solve.Wcs.WriteToHeader(_img.Header); FitsIO.Write(d.FileName, _img); }
            }, ButtonKind.Normal);
            btn("Sync telescope to solution", Icons.Observatory, () =>
            {
                var tel = DeviceManager.Instance.Telescope;
                if (tel == null || _solve == null || !_solve.Success) { MainForm.Instance.Toast("Needs a solution and a connected telescope", Icons.Info); return; }
                double cra, cdec; _solve.Wcs.Centre(out cra, out cdec);
                var app = new SkyContext(AppState.Site, AstroTime.Now).IcrsToApparent(cra * AstroMath.Deg2Rad, cdec * AstroMath.Deg2Rad);
                tel.SyncToCoordinates(app.RaHours, app.DecDeg);
                MainForm.Instance.Toast("Telescope synced");
            }, ButtonKind.Normal);
            btn("Show field in sky chart", Icons.Sky, () =>
            {
                if (_solve == null || !_solve.Success) return;
                var w = _solve.Wcs;
                double cra, cdec; w.Centre(out cra, out cdec);
                // trace the image border through the solution so the outline stays on the right patch of sky
                var outline = new List<double[]>();
                int n = 8;
                double x1 = w.ImageWidth - 1, y1 = w.ImageHeight - 1;
                Action<double, double> add = (x, y) => { double pra, pdec; w.PixelToWorld(x, y, out pra, out pdec); outline.Add(new[] { pra, pdec }); };
                for (int i = 0; i < n; i++) add(x1 * i / n, 0);
                for (int i = 0; i < n; i++) add(x1, y1 * i / n);
                for (int i = 0; i < n; i++) add(x1 * (n - i) / n, y1);
                for (int i = 0; i < n; i++) add(0, y1 * (n - i) / n);
                var sky = MainForm.Instance.GetView<SkyView>("sky");
                MainForm.Instance.Navigate("sky");
                sky.ShowField("Solved field", outline, cra * AstroMath.Deg2Rad, cdec * AstroMath.Deg2Rad, Math.Max(w.FieldWidthDeg, w.FieldHeightDeg) * 2.5);
            }, ButtonKind.Subtle);
            g.Full(buttons);
            g.Full(Ui.Hint("The internal solver matches triangles of stars (scale and rotation invariant) against catalogue stars, then fits a TAN-SIP world coordinate system. Without hints it tries field sizes from 0.25° to 60°. Gaia DR3 queries are cached for offline re-use."));
            side.Controls.Add(g);
            _viewer.ShowWcsGrid = true;
            _viewer.PaintOverlay += PaintSolveOverlay;
            _viewer.FilesDropped += f => { if (f.Length > 0) SetImage(ImageFile.Load(f[0]), ra, dec, scale); };
            var readout = new Label { Dock = DockStyle.Bottom, Height = 22, Tag = "dim" };
            _viewer.ReadoutChanged += s => readout.Text = s;
            panel.Controls.Add(_viewer);
            panel.Controls.Add(readout);
            panel.Controls.Add(_result);
            panel.Controls.Add(side);
            _hintRa = ra; _hintDec = dec; _hintScale = scale; _solverCombo = solver; _radius = radius; _sip = sip;
            return panel;
        }

        private TextBox _hintRa, _hintDec;
        private NumericUpDown _hintScale, _radius, _sip;
        private ComboBox _solverCombo;

        public void SolveImage(AstroImage img)
        {
            _tabs.Select(0);
            SetImage(img.Clone(), _hintRa, _hintDec, _hintScale);
            Solve(_solverCombo.SelectedIndex, _hintRa.Text, _hintDec.Text, (double)_radius.Value, (double)_hintScale.Value, (int)_sip.Value);
        }

        private void SetImage(AstroImage img, TextBox ra, TextBox dec, NumericUpDown scale)
        {
            if (img.Channels == 1 && img.Bayer != BayerPattern.None) img = Debayer.Apply(img, DebayerMethod.SuperPixel);
            _img = img; _solve = null; _annotations.Clear();
            _viewer.SetImage(img);
            var h = img.Header;
            double r = h.GetDouble("RA"), d = h.GetDouble("DEC");
            if (double.IsNaN(r) && h.Has("OBJCTRA")) { r = AstroMath.ParseSexagesimal(h.GetString("OBJCTRA")) * 15; d = AstroMath.ParseSexagesimal(h.GetString("OBJCTDEC")); }
            if (double.IsNaN(r) && h.Has("CRVAL1")) { r = h.GetDouble("CRVAL1"); d = h.GetDouble("CRVAL2"); }
            ra.Text = double.IsNaN(r) ? "" : AstroMath.FormatHms(r / 15, 1, true);
            dec.Text = double.IsNaN(d) ? "" : AstroMath.FormatDms(d, 0, true, true);
            double fl = h.GetDouble("FOCALLEN"), px = h.GetDouble(new[] { "XPIXSZ", "PIXSIZE1" });
            scale.Value = !double.IsNaN(fl) && !double.IsNaN(px) && fl > 0 ? (decimal)AstroMath.Clamp(206.265 * px / fl, 0, 200) : 0;
            _result.Text = string.Format("{0}: {1}×{2}{3}", Path.GetFileName(img.SourcePath ?? "image"), img.Width, img.Height, _viewer.Wcs != null ? "  ·  existing WCS: " + _viewer.Wcs : "");
        }

        private void Solve(int solver, string raText, string decText, double radius, double scale, int sip)
        {
            if (_img == null) { MainForm.Instance.Toast("Open an image first", Icons.Info); return; }
            var img = _img;
            double ra = AstroMath.ParseRaHours(raText) * 15, dec = AstroMath.ParseSexagesimal(decText);
            Tasks.Run("Plate solving", t =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                if (solver == 2 || solver == 3)
                {
                    string tmp = Path.Combine(Path.GetTempPath(), "sidralune_solve.fits");
                    FitsIO.Write(tmp, img, 16);
                    Wcs w = solver == 2 ? ExternalSolvers.SolveWithAstap(tmp, img.Width, img.Height, ra, dec, scale > 0 ? img.Height * scale / 3600 : double.NaN, radius)
                        : ExternalSolvers.SolveWithNova(tmp, AppState.Settings.NovaApiKey, img.Width, img.Height, new Progress<string>(s => t.Status = s), t.Cts.Token);
                    _solve = new SolveResult { Success = w != null, Wcs = w, Message = w != null ? "Solved: " + w : "No solution", Elapsed = sw.Elapsed };
                }
                else
                {
                    var opt = new SolveOptions { HintRa = ra, HintDec = dec, SearchRadius = radius, ScaleHint = scale > 0 ? scale : double.NaN, SipOrder = sip };
                    opt.Catalog = solver == 0 ? (IStarCatalogProvider)new VizierProvider() : new LocalCatalogProvider();
                    if (double.IsNaN(ra) && solver == 0) opt.Catalog = new LocalCatalogProvider(); // blind solving uses the local catalog
                    _solve = PlateSolver.Solve(img, opt, new Progress<string>(s => t.Status = s), t.Cts.Token);
                }
                if (_solve.Success)
                {
                    _solve.Wcs.WriteToHeader(img.Header);
                    BuildAnnotations(_solve.Wcs);
                }
            }, () =>
            {
                _viewer.Wcs = _solve.Success ? _solve.Wcs : null;
                _viewer.Invalidate();
                _result.Text = _solve.Message + string.Format("\r\nElapsed {0:0.0} s, {1} stars detected, {2} matched, rms {3:0.00}\"", _solve.Elapsed.TotalSeconds, _solve.ImageStars, _solve.Wcs != null ? _solve.Wcs.MatchedStars : 0, _solve.Wcs != null ? _solve.Wcs.RmsArcsec : double.NaN)
                    + (_solve.Success ? "\r\nObjects in field: " + string.Join(", ", _annotations.Select(a => a.Item1).Take(40)) : "");
                MainForm.Instance.Toast(_solve.Success ? "Plate solved" : "No solution: " + _solve.Message, _solve.Success ? Icons.Check : Icons.Warning, _solve.Success ? Theme.Current.Success : Theme.Current.Warning);
            });
        }

        private void BuildAnnotations(Wcs w)
        {
            var list = new List<Tuple<string, double, double, Func<Palette, Color>>>();
            double cra, cdec; w.Centre(out cra, out cdec);
            double r = Math.Sqrt(w.FieldWidthDeg * w.FieldWidthDeg + w.FieldHeightDeg * w.FieldHeightDeg) / 2 * AstroMath.Deg2Rad;
            var dsos = new List<DeepSkyObject>();
            CatalogManager.Instance.DeepSky.Cone(cra * AstroMath.Deg2Rad, cdec * AstroMath.Deg2Rad, r, 99, dsos);
            foreach (var d in dsos)
            {
                double x, y;
                if (w.WorldToPixel(d.RaHours * 15, d.DecDeg, out x, out y) && x >= 0 && y >= 0 && x < w.ImageWidth && y < w.ImageHeight) list.Add(Tuple.Create(d.DisplayName, x, y, (Func<Palette, Color>)(pal => pal.Nebula)));
            }
            var stars = new List<Star>();
            CatalogManager.Instance.Stars.Cone(cra * AstroMath.Deg2Rad, cdec * AstroMath.Deg2Rad, r, 12, stars);
            foreach (var s in stars.Where(s => s.Name != null || s.Bayer != null).Take(60))
            {
                double x, y;
                if (w.WorldToPixel(s.RaHours * 15, s.DecDeg, out x, out y) && x >= 0 && y >= 0 && x < w.ImageWidth && y < w.ImageHeight) list.Add(Tuple.Create(s.DisplayName, x, y, (Func<Palette, Color>)(pal => pal.Warning)));
            }
            var ctx = new SkyContext(AppState.Site, AstroTime.FromDateTime(_img.DateObs ?? DateTime.UtcNow));
            foreach (var mb in Core.MinorBodies.MinorBodyStore.Instance.All)
            {
                try
                {
                    var p = Core.MinorBodies.MinorBodyStore.Place(ctx, mb);
                    double x, y;
                    if (w.WorldToPixel(p.Astrometric.RaHours * 15, p.Astrometric.DecDeg, out x, out y) && x >= 0 && y >= 0 && x < w.ImageWidth && y < w.ImageHeight)
                        list.Add(Tuple.Create(mb.DisplayName + string.Format(" ({0:0.0})", p.Magnitude), x, y, mb.IsComet ? (Func<Palette, Color>)(pal => pal.Comet) : pal => pal.Asteroid));
                }
                catch { }
            }
            _annotations = list;
        }

        private void PaintSolveOverlay(Graphics g, Func<double, double, PointF> map)
        {
            if (_solve != null && _solve.Success)
            {
                using (var pen = new Pen(Theme.WithAlpha(Theme.Current.Success, 160)))
                    foreach (var m in _solve.Matches.Take(400)) { var p = map(m.Item1.X, m.Item1.Y); g.DrawEllipse(pen, p.X - 6, p.Y - 6, 12, 12); }
            }
            foreach (var a in _annotations)
            {
                var p = map(a.Item2, a.Item3);
                var col = a.Item4(Theme.Current);
                using (var pen = new Pen(col, 1.5f)) { g.DrawLine(pen, p.X - 14, p.Y, p.X - 5, p.Y); g.DrawLine(pen, p.X, p.Y - 14, p.X, p.Y - 5); }
                TextRenderer.DrawText(g, a.Item1, Theme.UiFont, new Point((int)p.X + 4, (int)p.Y - 20), col);
            }
        }

        // ================================================================== framing

        private Control BuildFraming()
        {
            var panel = new Panel { Dock = DockStyle.Fill };
            var side = new Card("Framing") { Dock = DockStyle.Right, Width = 360, AutoScroll = true };
            var g = new FormGrid(130);
            var target = g.Row("Target", new TextBox());
            var cam = g.Row("Camera / scope", new Label { Tag = "dim", Height = 34 });
            _rot = g.Row("Rotation (°)", Ui.Num(0, -180, 180, 1));
            _cols = g.Row("Mosaic columns", Ui.Num(1, 1, 10));
            _rows = g.Row("Mosaic rows", Ui.Num(1, 1, 10));
            _overlap = g.Row("Overlap (%)", Ui.Num(15, 0, 50));
            var tiles = new TextBox { Multiline = true, ReadOnly = true, Height = 200, Font = Theme.MonoFont, ScrollBars = ScrollBars.Vertical };
            var go = new FlatButton("Update", Icons.Refresh, (s, e) => { var o = CatalogManager.Instance.Find(target.Text) ?? CatalogManager.Instance.Search(target.Text, 1).FirstOrDefault(); if (o != null) _frameTarget = SelectedObject.From(o); UpdateFraming(tiles, cam); }) { Kind = ButtonKind.Primary };
            go.AutoSizeToText();
            var copy = new FlatButton("Copy tile list", Icons.List, (s, e) => { if (tiles.Text.Length > 0) Clipboard.SetText(tiles.Text); }); copy.AutoSizeToText();
            var bar = new FlowLayoutPanel { Height = 40 }; bar.Controls.Add(go); bar.Controls.Add(copy);
            g.Full(bar);
            g.Full(tiles);
            g.Full(Ui.Hint("The rectangle shows your sensor (Settings → Equipment) on the sky. Drag the chart to reposition, mouse wheel to zoom. Tile centres can be pasted into a sequence."));
            foreach (var n in new[] { _rot, _cols, _rows, _overlap }) n.ValueChanged += (s, e) => UpdateFraming(tiles, cam);
            _frameChart.ViewChanged += () => UpdateFraming(tiles, cam, true);
            side.Controls.Add(g);
            panel.Controls.Add(_frameChart);
            panel.Controls.Add(side);
            _frameTiles = tiles; _frameCam = cam; _frameTargetBox = target;
            return panel;
        }

        private TextBox _frameTiles, _frameTargetBox;
        private Label _frameCam;

        public void FrameTarget(SelectedObject o)
        {
            _tabs.Select(1);
            _frameTarget = o;
            _frameTargetBox.Text = o.Name;
            _frameChart.CenterOn(o.RaDeg * AstroMath.Deg2Rad, o.DecDeg * AstroMath.Deg2Rad);
            UpdateFraming(_frameTiles, _frameCam);
        }

        private void UpdateFraming(TextBox tiles, Label cam, bool fromDrag = false)
        {
            var rig = AppState.ImagingRig;
            cam.Text = string.Format("{0}\r\n{1:0.00}° × {2:0.00}° at {3:0.00}\"/px", rig.Name, rig.FieldWidthDeg, rig.FieldHeightDeg, rig.PixelScaleArcsec);
            double cra, cdec;
            if (fromDrag || _frameTarget == null) { _frameChart.ScreenToIcrs(new Point(_frameChart.Width / 2, _frameChart.Height / 2), out cra, out cdec); cra *= AstroMath.Rad2Deg; cdec *= AstroMath.Rad2Deg; }
            else { cra = _frameTarget.RaDeg; cdec = _frameTarget.DecDeg; _frameChart.CenterOn(cra * AstroMath.Deg2Rad, cdec * AstroMath.Deg2Rad); }
            int nc = (int)_cols.Value, nr = (int)_rows.Value;
            double ov = (double)_overlap.Value / 100, rot = (double)_rot.Value * AstroMath.Deg2Rad;
            double stepX = rig.FieldWidthDeg * (1 - ov), stepY = rig.FieldHeightDeg * (1 - ov);
            var list = new List<double[]>();
            var sb = new System.Text.StringBuilder();
            for (int r = 0; r < nr; r++)
                for (int c = 0; c < nc; c++)
                {
                    double dx = (c - (nc - 1) / 2.0) * stepX, dy = ((nr - 1) / 2.0 - r) * stepY;
                    // rotate the offset and convert from the tangent plane (east = -x on sky)
                    double ex = dx * Math.Cos(rot) - dy * Math.Sin(rot), ny = dx * Math.Sin(rot) + dy * Math.Cos(rot);
                    double tra, tdec;
                    Wcs.StandardToSpherical(-ex * AstroMath.Deg2Rad, ny * AstroMath.Deg2Rad, cra * AstroMath.Deg2Rad, cdec * AstroMath.Deg2Rad, out tra, out tdec);
                    list.Add(new[] { tra, tdec, rig.FieldWidthDeg, rig.FieldHeightDeg, (double)_rot.Value });
                    sb.AppendLine(string.Format("Tile {0},{1}: {2}  {3}", r + 1, c + 1, AstroMath.FormatHms(tra / 15, 1, true), AstroMath.FormatDms(tdec, 0, true, true)));
                }
            _frameChart.MosaicTiles = list;
            if (!fromDrag) _frameChart.FovDeg = Math.Max(rig.FieldWidthDeg * nc, rig.FieldHeightDeg * nr) * 2.2;
            tiles.Text = sb.ToString();
            _frameChart.Invalidate();
        }

        // ================================================================== exposure planner

        private Control BuildExposure()
        {
            var card = new Card("Exposure & signal-to-noise planner") { Dock = DockStyle.Fill, AutoScroll = true };
            var g = new FormGrid(240);
            var rigLabel = g.Row("Imaging system", new Label { Tag = "dim" });
            var sb = g.Row("Target surface brightness (mag/″²)", Ui.Num(22, 14, 28, 1, 0.1m));
            var sky = g.Row("Sky brightness (mag/″²)", Ui.Num((decimal)AstroMath.Clamp(AppState.Site.SqmZenith, 15, 22.5), 15, 22.5m, 2, 0.05m));
            var bw = g.Row("Filter bandwidth (nm)", Ui.Num(88, 1, 400, 1));
            var sub = g.Row("Sub-exposure (s)", Ui.Num(180, 1, 3600));
            var snr = g.Row("Target SNR", Ui.Num(30, 1, 1000));
            var outp = new Label { Height = 160, AutoSize = false, Font = Theme.UiFont };
            g.Full(outp);
            var plot = new PlotControl { Height = 260, Title = "SNR vs total integration", XLabel = "hours", YLabel = "SNR" };
            g.Full(plot);
            Action upd = () =>
            {
                var rig = AppState.ImagingRig;
                rig.FilterBandwidthNm = (double)bw.Value;
                rigLabel.Text = rig.Name + string.Format("  ({0:0.00}\"/px, f/{1:0.0})", rig.PixelScaleArcsec, rig.FocalRatio);
                double sbv = (double)sb.Value, skyv = (double)sky.Value, s = (double)sub.Value;
                double one = ExposureCalculator.ExtendedSnr(rig, sbv, skyv, s, 1);
                double total = ExposureCalculator.TimeForSnr(rig, sbv, skyv, s, (double)snr.Value);
                double opt = ExposureCalculator.OptimalSubExposure(rig, skyv);
                outp.Text = string.Format("Signal per pixel per sub: {0:0.0} e⁻   ·   sky {1:0.0} e⁻\r\nSNR of one sub: {2:0.00}\r\nTotal integration for SNR {3}: {4:0.0} h ({5:0} subs)\r\nOptimal sub-exposure (read noise < 5% of sky noise): {6:0} s\r\nCritical focus zone: {7:0} µm   ·   Nyquist focal length for {8:0.0}\" seeing: {9:0} mm",
                    ExposureCalculator.SurfaceRate(rig, sbv) * s, ExposureCalculator.SurfaceRate(rig, skyv) * s, one, snr.Value, total / 3600, total / s, opt,
                    ExposureCalculator.CriticalFocusZoneUm(rig.FocalRatio), AppState.Site.SeeingArcsec, ExposureCalculator.OptimalFocalLength(rig.PixelSizeUm, AppState.Site.SeeingArcsec));
                plot.Clear();
                var ser = plot.AddSeries("SNR");
                for (double h = 0.1; h <= 20; h += 0.1) ser.Add(h, ExposureCalculator.ExtendedSnr(rig, sbv, skyv, s, Math.Max(1, (int)(h * 3600 / s))));
                plot.Markers.Add(new PlotMarker { X = (double)snr.Value, Horizontal = true, Label = "target" });
                plot.Invalidate();
            };
            foreach (var n in new[] { sb, sky, bw, sub, snr }) n.ValueChanged += (s, e) => upd();
            card.VisibleChanged += (s, e) => upd();
            card.Controls.Add(g);
            upd();
            return card;
        }

        public IEnumerable<AppCommand> GetCommands()
        {
            yield return new AppCommand { Title = "Astrometry: plate solve an image", Icon = Icons.Target, Run = () => { MainForm.Instance.Navigate("astrometry"); _tabs.Select(0); } };
            yield return new AppCommand { Title = "Astrometry: framing & mosaic planner", Run = () => { MainForm.Instance.Navigate("astrometry"); _tabs.Select(1); } };
            yield return new AppCommand { Title = "Astrometry: exposure calculator", Run = () => { MainForm.Instance.Navigate("astrometry"); _tabs.Select(2); } };
        }
    }
}
