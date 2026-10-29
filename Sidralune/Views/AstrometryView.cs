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
        // one solve at a time: opening another image or solving again waits for it
        private readonly JobSlot _solveJob = new JobSlot();
        private List<Annotation> _annotations = new List<Annotation>();
        private ImageDepth.Result _depthInfo;
        private TimeSpan[] _steps = new TimeSpan[4]; // finding the stars, solving, placing the labels, measuring the depth
        private double DepthLimit => _depthInfo != null ? _depthInfo.Limit : double.NaN; // the faintest magnitude the solved image shows, NaN when unknown
        private static readonly string[] LabelModes = { "All", "Visible", "None" };

        /// <summary>A label on the solved image; colours are palette roles, resolved when painting so they follow day/night switches.</summary>
        private class Annotation
        {
            public string Name;
            public double X, Y, Mag = double.NaN;
            public bool MinorBody;
            public Func<Palette, Color> Color;
        }

        /// <summary>The labels to show: asteroids and comets as the Minor body labels option says.</summary>
        private IEnumerable<Annotation> ShownAnnotations
        {
            get
            {
                string mode = AppState.Settings.MinorBodyLabels;
                return _annotations.Where(a => !a.MinorBody || mode == "All" || mode == "Visible" && (double.IsNaN(DepthLimit) || a.Mag <= DepthLimit));
            }
        }
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
            // linear (unstretched) data is mostly black: the automatic stretch shows the faint stars the solution is checked against;
            // None suits images that are already stretched
            g.Row("Display", Ui.StretchCombo(_viewer.Stretch, v => _viewer.Stretch = v));
            var labels = g.Row("Minor body labels", Ui.Combo("All in the field", "Bright enough to show", "None"));
            labels.SelectedIndex = Math.Max(0, Array.IndexOf(LabelModes, AppState.Settings.MinorBodyLabels));
            labels.SelectedIndexChanged += (s, e) =>
            {
                AppState.Settings.MinorBodyLabels = LabelModes[labels.SelectedIndex];
                AppState.Save();
                ShowSolveText();
                _viewer.Invalidate();
            };
            var buttons = new FlowLayoutPanel { Height = 240, FlowDirection = FlowDirection.TopDown };
            Func<string, string, Action, ButtonKind, FlatButton> btn = (t, i, a, k) => { var b = new FlatButton(t, i, (s, e) => { try { a(); } catch (Exception ex) { MainForm.Instance.Toast(ex.Message, Icons.Warning); } }) { Kind = k, Width = 320 }; buttons.Controls.Add(b); return b; };
            var open = btn("Open image…", Icons.Open, () => { using (var d = new OpenFileDialog { Filter = ImageFile.OpenFilter }) if (d.ShowDialog() == DialogResult.OK) OpenFile(d.FileName, ra, dec, scale); }, ButtonKind.Normal);
            var solve = btn("Solve", Icons.Target, () => Solve(solver.SelectedIndex, ra.Text, dec.Text, (double)radius.Value, (double)scale.Value, (int)sip.Value), ButtonKind.Primary);
            // Stop ends a solve, or the loading of an image, at once; it is available only while one runs
            var stop = btn("Stop", Icons.Stop, () =>
            {
                if (!_solveJob.Busy) return;
                MainForm.Instance.Toast(_solveJob.Name + " stopped", Icons.Info);
                _solveJob.Cancel();
            }, ButtonKind.Danger);
            stop.Enabled = false;
            _solveJob.Changed += () => stop.Enabled = _solveJob.Busy;
            var write = btn("Write WCS to FITS…", Icons.Save, () =>
            {
                if (_solve == null || !_solve.Success) return;
                using (var d = new SaveFileDialog { Filter = "FITS|*.fits", FileName = Path.GetFileNameWithoutExtension(_img.SourcePath ?? "solved") + "_wcs.fits" })
                    if (d.ShowDialog() == DialogResult.OK) { _solve.Wcs.WriteToHeader(_img.Header); FitsIO.Write(d.FileName, _img); }
            }, ButtonKind.Normal);
            var sync = btn("Sync telescope to solution", Icons.Observatory, () =>
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
            _solveJob.Guard(open, solve, write, sync);
            g.Full(buttons);
            g.Full(Ui.Hint("The internal solver matches triangles of stars (scale and rotation invariant) against catalogue stars, then fits a TAN-SIP world coordinate system. Without hints it tries field sizes from 0.25° to 60°. Gaia DR3 queries are cached for offline re-use."));
            side.Controls.Add(g);
            _viewer.ShowWcsGrid = true;
            _viewer.PaintOverlay += PaintSolveOverlay;
            _viewer.FilesDropped += f => { if (f.Length > 0) OpenFile(f[0], ra, dec, scale); };
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
            if (_solveJob.Refuse()) return;
            SetImage(img.Clone(), _hintRa, _hintDec, _hintScale);
            Solve(_solverCombo.SelectedIndex, _hintRa.Text, _hintDec.Text, (double)_radius.Value, (double)_hintScale.Value, (int)_sip.Value);
        }

        /// <summary>
        /// A plate solve of img with the solver settings chosen here (solver, search radius, SIP order) and the hints in its
        /// header, for another workspace to run as its own job; this workspace and its image are left as they are.
        /// </summary>
        public Func<BackgroundTask, SolveResult> SolveWithSettings(AstroImage img)
        {
            int solver = _solverCombo.SelectedIndex, sip = (int)_sip.Value;
            double radius = (double)_radius.Value, ra, dec, scale;
            ReadHints(img, out ra, out dec, out scale);
            return t => RunSolver(img, solver, ra, dec, radius, scale, sip, null, t);
        }

        /// <summary>The pointing (RA/DEC, OBJCTRA/OBJCTDEC or an earlier solution) and the scale in "/px (0 when unknown) from an image's header.</summary>
        private static void ReadHints(AstroImage img, out double ra, out double dec, out double scale)
        {
            var h = img.Header;
            ra = h.GetDouble("RA"); dec = h.GetDouble("DEC");
            if (double.IsNaN(ra) && h.Has("OBJCTRA")) { ra = AstroMath.ParseSexagesimal(h.GetString("OBJCTRA")) * 15; dec = AstroMath.ParseSexagesimal(h.GetString("OBJCTDEC")); }
            if (double.IsNaN(ra) && h.Has("CRVAL1")) { ra = h.GetDouble("CRVAL1"); dec = h.GetDouble("CRVAL2"); }
            double fl = h.GetDouble("FOCALLEN"), px = h.GetDouble(new[] { "XPIXSZ", "PIXSIZE1" });
            scale = !double.IsNaN(fl) && !double.IsNaN(px) && fl > 0 ? AstroMath.Clamp(206.265 * px / fl, 0, 200) : 0;
        }

        /// <summary>
        /// Solves img with a solver from the Solver list (0 internal + Gaia, 1 internal + local catalogue, 2 ASTAP, 3 astrometry.net);
        /// stars are the image's stars when already found. Runs in the background.
        /// </summary>
        private static SolveResult RunSolver(AstroImage img, int solver, double ra, double dec, double radius, double scale, int sip, List<Imaging.Analysis.DetectedStar> stars, BackgroundTask t)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            if (solver == 2 || solver == 3)
            {
                // a file of its own, so solves started in two workspaces do not share one
                string tmp = Path.Combine(Path.GetTempPath(), "sidralune_solve_" + Guid.NewGuid().ToString("N") + ".fits");
                try
                {
                    FitsIO.Write(tmp, img, 16);
                    Wcs w = solver == 2 ? ExternalSolvers.SolveWithAstap(tmp, img.Width, img.Height, ra, dec, scale > 0 ? img.Height * scale / 3600 : double.NaN, radius, null, t.Cts.Token)
                        : ExternalSolvers.SolveWithNova(tmp, AppState.Settings.NovaApiKey, img.Width, img.Height, new Progress<string>(s => t.Status = s), t.Cts.Token);
                    return new SolveResult { Success = w != null, Wcs = w, Message = w != null ? "Solved: " + w : "No solution", Elapsed = sw.Elapsed };
                }
                finally { try { File.Delete(tmp); } catch { } }
            }
            var opt = new SolveOptions { HintRa = ra, HintDec = dec, SearchRadius = radius, ScaleHint = scale > 0 ? scale : double.NaN, SipOrder = sip, Stars = stars };
            opt.Catalog = solver == 0 ? (IStarCatalogProvider)new VizierProvider() : new LocalCatalogProvider();
            if (double.IsNaN(ra) && solver == 0) opt.Catalog = new LocalCatalogProvider(); // blind solving uses the local catalog
            return PlateSolver.Solve(img, opt, new Progress<string>(s => t.Status = s), t.Cts.Token);
        }

        /// <summary>Loads (and debayers) an image in the background as the solve job, so a solve waits for it and it for a solve.</summary>
        private void OpenFile(string path, TextBox ra, TextBox dec, NumericUpDown scale)
        {
            AstroImage img = null;
            _solveJob.Run("Opening " + Path.GetFileName(path), t => img = Debayered(ImageFile.Load(path)), () => SetImage(img, ra, dec, scale));
        }

        private static AstroImage Debayered(AstroImage img) => img.Channels == 1 && img.Bayer != BayerPattern.None ? Debayer.Apply(img, DebayerMethod.SuperPixel) : img;

        private void SetImage(AstroImage img, TextBox ra, TextBox dec, NumericUpDown scale)
        {
            img = Debayered(img);
            _img = img; _solve = null; _annotations.Clear(); _depthInfo = null; _steps = new TimeSpan[4];
            _viewer.SetImage(img);
            double r, d, sc;
            ReadHints(img, out r, out d, out sc);
            ra.Text = double.IsNaN(r) ? "" : AstroMath.FormatHms(r / 15, 1, true);
            dec.Text = double.IsNaN(d) ? "" : AstroMath.FormatDms(d, 0, true, true);
            scale.Value = (decimal)sc;
            _result.Text = string.Format("{0}: {1}×{2}{3}", Path.GetFileName(img.SourcePath ?? "image"), img.Width, img.Height, _viewer.Wcs != null ? "  ·  existing WCS: " + _viewer.Wcs : "");
        }

        private void Solve(int solver, string raText, string decText, double radius, double scale, int sip)
        {
            if (_img == null) { MainForm.Instance.Toast("Open an image first", Icons.Info); return; }
            var img = _img;
            double ra = AstroMath.ParseRaHours(raText) * 15, dec = AstroMath.ParseSexagesimal(decText);
            SolveResult solved = null;
            List<Annotation> annotations = null;
            ImageDepth.Result depth = null;
            var steps = new TimeSpan[4];
            List<Imaging.Analysis.DetectedStar> stars = null;
            _solveJob.Run("Plate solving", t =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                if (solver < 2)
                {
                    // every star is found once: the solver takes the brightest, the depth measurement all of them
                    stars = ImageDepth.DetectAll(img.Luminance(), img.Width, img.Height);
                    steps[0] = sw.Elapsed;
                }
                solved = RunSolver(img, solver, ra, dec, radius, scale, sip, stars, t);
                if (solved.Success)
                {
                    solved.Wcs.WriteToHeader(img.Header);
                    steps[1] = sw.Elapsed - steps[0];
                    annotations = BuildAnnotations(solved.Wcs, img);
                    steps[2] = sw.Elapsed - steps[0] - steps[1];
                    depth = Depth(solved, img, stars);
                    steps[3] = sw.Elapsed - steps[0] - steps[1] - steps[2];
                }
                else steps[1] = sw.Elapsed - steps[0];
                solved.Elapsed = sw.Elapsed; // all of it: the steps are shown separately
            }, () =>
            {
                // results are applied here, on the UI thread, to the image they were found for
                _solve = solved;
                _annotations = annotations ?? new List<Annotation>();
                _depthInfo = depth;
                _steps = steps;
                _viewer.Wcs = _solve.Success ? _solve.Wcs : null;
                _viewer.Invalidate();
                ShowSolveText();
                MainForm.Instance.Toast(_solve.Success ? "Plate solved" : "No solution: " + _solve.Message, _solve.Success ? Icons.Check : Icons.Warning, _solve.Success ? Theme.Current.Success : Theme.Current.Warning);
            });
        }

        private void ShowSolveText()
        {
            if (_solve == null) return;
            _result.Text = _solve.Message + string.Format("\r\nElapsed {0:0.0} s{1}, {2} stars used for solving, {3} matched, rms {4:0.00}\"", _solve.Elapsed.TotalSeconds, StepsText(),
                _solve.ImageStars, _solve.Wcs != null ? _solve.Wcs.MatchedStars : 0, _solve.Wcs != null ? _solve.Wcs.RmsArcsec : double.NaN)
                + (_solve.Success ? (double.IsNaN(DepthLimit) ? "\r\nImage depth: unknown (too few catalogue stars could be measured), so all asteroids and comets are labelled"
                    : string.Format("\r\nImage depth: about magnitude {0:0.0} ({1} catalogue stars, {2} stars detected)", DepthLimit, _depthInfo.Calibrators, _depthInfo.Stars))
                    + "\r\nObjects in field: " + string.Join(", ", ShownAnnotations.Select(a => a.Name).Take(40)) : "");
        }

        /// <summary>Where the time went, e.g. " (finding stars 7.1 s, solving 13.9 s, labels 0.4 s, image depth 1.6 s)"; empty when not timed in parts.</summary>
        private string StepsText()
        {
            if (_steps.All(x => x == TimeSpan.Zero)) return "";
            var parts = new[] { "finding stars", "solving", "labels", "image depth" }.Zip(_steps, (n, x) => new { n, x }).Where(p => p.x > TimeSpan.Zero)
                .Select(p => string.Format("{0} {1:0.0} s", p.n, p.x.TotalSeconds));
            return " (" + string.Join(", ", parts) + ")";
        }

        /// <summary>How faint the solved image reaches, calibrated on catalogue stars where the solution puts them.</summary>
        private static ImageDepth.Result Depth(SolveResult solved, AstroImage img, List<Imaging.Analysis.DetectedStar> stars)
        {
            var w = solved.Wcs;
            double cra, cdec; w.Centre(out cra, out cdec);
            var refs = new LocalCatalogProvider().Cone(cra, cdec, Math.Sqrt(w.FieldWidthDeg * w.FieldWidthDeg + w.FieldHeightDeg * w.FieldHeightDeg) / 2, 20, 3000);
            // an online solve brings deeper catalogue stars of its own: use them when there are more of them
            var solverRefs = solved.Matches.Select(m => m.Item2).ToList();
            return ImageDepth.Measure(img.Luminance(), img.Width, img.Height, w, solverRefs.Count > refs.Count ? solverRefs : refs, stars);
        }

        private static List<Annotation> BuildAnnotations(Wcs w, AstroImage img)
        {
            var list = new List<Annotation>();
            double cra, cdec; w.Centre(out cra, out cdec);
            double r = Math.Sqrt(w.FieldWidthDeg * w.FieldWidthDeg + w.FieldHeightDeg * w.FieldHeightDeg) / 2 * AstroMath.Deg2Rad;
            var dsos = new List<DeepSkyObject>();
            CatalogManager.Instance.DeepSky.Cone(cra * AstroMath.Deg2Rad, cdec * AstroMath.Deg2Rad, r, 99, dsos);
            foreach (var d in dsos)
            {
                double x, y;
                if (w.WorldToPixel(d.RaHours * 15, d.DecDeg, out x, out y) && x >= 0 && y >= 0 && x < w.ImageWidth && y < w.ImageHeight) list.Add(new Annotation { Name = d.DisplayName, X = x, Y = y, Color = pal => pal.Nebula });
            }
            var stars = new List<Star>();
            CatalogManager.Instance.Stars.Cone(cra * AstroMath.Deg2Rad, cdec * AstroMath.Deg2Rad, r, 12, stars);
            foreach (var s in stars.Where(s => s.Name != null || s.Bayer != null).Take(60))
            {
                double x, y;
                if (w.WorldToPixel(s.RaHours * 15, s.DecDeg, out x, out y) && x >= 0 && y >= 0 && x < w.ImageWidth && y < w.ImageHeight) list.Add(new Annotation { Name = s.DisplayName, X = x, Y = y, Color = pal => pal.Warning });
            }
            var ctx = new SkyContext(AppState.Site, AstroTime.FromDateTime(img.DateObs ?? DateTime.UtcNow));
            foreach (var mb in Core.MinorBodies.MinorBodyStore.Instance.All)
            {
                try
                {
                    var p = Core.MinorBodies.MinorBodyStore.Place(ctx, mb);
                    double x, y;
                    if (w.WorldToPixel(p.Astrometric.RaHours * 15, p.Astrometric.DecDeg, out x, out y) && x >= 0 && y >= 0 && x < w.ImageWidth && y < w.ImageHeight)
                        list.Add(new Annotation { Name = mb.DisplayName + string.Format(" ({0:0.0})", p.Magnitude), X = x, Y = y, Mag = p.Magnitude, MinorBody = true, Color = mb.IsComet ? (Func<Palette, Color>)(pal => pal.Comet) : pal => pal.Asteroid });
                }
                catch { }
            }
            return list;
        }

        private void PaintSolveOverlay(Graphics g, Func<double, double, PointF> map)
        {
            if (_solve != null && _solve.Success)
            {
                using (var pen = new Pen(Theme.WithAlpha(Theme.Current.Success, 160)))
                    foreach (var m in _solve.Matches.Take(400)) { var p = map(m.Item1.X, m.Item1.Y); g.DrawEllipse(pen, p.X - 6, p.Y - 6, 12, 12); }
            }
            foreach (var a in ShownAnnotations)
            {
                var p = map(a.X, a.Y);
                var col = a.Color(Theme.Current);
                using (var pen = new Pen(col, 1.5f)) { g.DrawLine(pen, p.X - 14, p.Y, p.X - 5, p.Y); g.DrawLine(pen, p.X, p.Y - 14, p.X, p.Y - 5); }
                TextRenderer.DrawText(g, a.Name, Theme.UiFont, new Point((int)p.X + 4, (int)p.Y - 20), col);
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
