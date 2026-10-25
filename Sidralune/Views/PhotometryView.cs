using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Sidralune.Core;
using Sidralune.Core.Coordinates;
using Sidralune.Core.Mathematics;
using Sidralune.Core.Time;
using Sidralune.Core.Util;
using Sidralune.Imaging;
using Sidralune.Imaging.IO;
using Sidralune.Science.Astrometry;
using Sidralune.Science.Databases;
using Sidralune.Science.Photometry;
using Sidralune.UI;
using AstroEvents = Sidralune.Core.Ephemeris.Events;

namespace Sidralune.Views
{
    /// <summary>
    /// Differential photometry of image sequences, light-curve analysis (periodograms, phase folding, minima timing,
    /// O-C) and exoplanet transit fitting with MCMC, plus transit predictions from the NASA Exoplanet Archive.
    /// </summary>
    public class PhotometryView : UserControl, ICommandSource
    {
        private readonly TabbedPanel _tabs = new TabbedPanel { Dock = DockStyle.Fill, Padding = new Padding(10) };
        private readonly DifferentialPhotometry _phot = new DifferentialPhotometry();
        private readonly List<string> _frames = new List<string>();
        // each tab runs one job at a time; measuring locks the frames, stars and aperture it is working with
        private readonly JobSlot _measureJob = new JobSlot(), _analysisJob = new JobSlot(), _transitJob = new JobSlot(), _predictJob = new JobSlot();

        // the light curve currently being analysed (shared by the analysis and transit tabs)
        private readonly List<double> _t = new List<double>(), _y = new List<double>(), _e = new List<double>();
        private bool _isFlux;
        private string _curveName = "";

        public PhotometryView()
        {
            Controls.Add(_tabs);
            _tabs.AddPage("Measure", BuildMeasure());
            _tabs.AddPage("Light curve analysis", BuildAnalysis());
            _tabs.AddPage("Exoplanet transits", BuildTransit());
            _tabs.AddPage("Transit predictions", BuildPredictions());
        }

        // ================================================================== measure

        private DataGridView _targets;
        private ListBox _frameList;
        private PlotControl _measPlot;
        private ComboBox _timeSystem;

        private Control BuildMeasure()
        {
            var panel = new Panel { Dock = DockStyle.Fill };
            var side = new Card("Frames & stars") { Dock = DockStyle.Left, Width = 470, AutoScroll = true };
            var g = new FormGrid(110);
            _frameList = new ListBox { Height = 120, IntegralHeight = false, HorizontalScrollbar = true, SelectionMode = SelectionMode.MultiExtended };
            var addFrames = new FlatButton("Add frames…", Icons.Add, (s, e) =>
            {
                using (var d = new OpenFileDialog { Filter = ImageFile.OpenFilter, Multiselect = true })
                    if (d.ShowDialog() == DialogResult.OK) { _frames.AddRange(d.FileNames); RefreshFrames(); }
            });
            addFrames.AutoSizeToText();
            // the list shows the frames in the order of _frames, so positions match
            Action removeFrames = () =>
            {
                var gone = _frameList.SelectedIndices.Cast<int>().OrderByDescending(i => i).ToList();
                if (gone.Count == 0) { MainForm.Instance.Toast("Select the frames to remove in the list", Icons.Info); return; }
                foreach (int i in gone) _frames.RemoveAt(i);
                RefreshFrames();
            };
            var removeFrame = new FlatButton("Remove selected", Icons.Remove, (s, e) => removeFrames()) { Kind = ButtonKind.Subtle };
            removeFrame.AutoSizeToText();
            _frameList.KeyDown += (s, e) => { if (e.KeyCode == Keys.Delete) { removeFrames(); e.Handled = true; } };
            var clear = new FlatButton("Clear", Icons.Close, (s, e) => { _frames.Clear(); RefreshFrames(); }) { Kind = ButtonKind.Subtle };
            clear.AutoSizeToText();
            g.Full(_frameList);
            g.Full(Bar(addFrames, removeFrame, clear));

            _targets = Ui.Grid();
            _targets.ReadOnly = false;
            _targets.AllowUserToAddRows = true;
            _targets.AllowUserToDeleteRows = true;
            _targets.Height = 210;
            _targets.Dock = DockStyle.None;
            _targets.Columns.Add("Name", "Name");
            _targets.Columns.Add("Ra", "RA");
            _targets.Columns.Add("Dec", "Dec");
            _targets.Columns.Add("Mag", "Cat. mag");
            _targets.Columns.Add("Bv", "B−V");
            var role = new DataGridViewComboBoxColumn { Name = "Role", HeaderText = "Role", FlatStyle = FlatStyle.Flat };
            role.Items.AddRange("Target", "Comparison", "Check");
            _targets.Columns.Add(role);
            _targets.DefaultValuesNeeded += (s, e) => e.Row.Cells["Role"].Value = "Comparison";
            g.Full(new Label { Text = "Stars (RA/Dec J2000; comparison stars need catalogue magnitudes for standard magnitudes)", AutoSize = true, Tag = "dim", MaximumSize = new Size(430, 0) });
            g.Full(_targets);
            var addSel = new FlatButton("Add selected object", Icons.Target, (s, e) =>
            {
                var o = AppState.Selection;
                if (o == null) { MainForm.Instance.Toast("Select an object in the sky chart or planner first", Icons.Info); return; }
                AddTargetRow(o.Name, o.RaDeg, o.DecDeg, double.NaN, double.NaN, "Target");
            });
            addSel.AutoSizeToText();
            var vsp = new FlatButton("AAVSO comps…", Icons.Download, (s, e) => LoadAavsoComps());
            vsp.AutoSizeToText();
            // the grid also deletes selected rows with the Delete key
            var removeStars = new FlatButton("Remove selected", Icons.Remove, (s, e) =>
            {
                var rows = _targets.SelectedCells.Cast<DataGridViewCell>().Select(c => c.OwningRow).Concat(_targets.SelectedRows.Cast<DataGridViewRow>()).Where(r => !r.IsNewRow).Distinct().ToList();
                if (rows.Count == 0) { MainForm.Instance.Toast("Select the stars to remove in the table", Icons.Info); return; }
                foreach (var r in rows) _targets.Rows.Remove(r);
            }) { Kind = ButtonKind.Subtle };
            removeStars.AutoSizeToText();
            g.Full(Bar(addSel, vsp, removeStars));

            var props = new PropertyGrid { Height = 210, HelpVisible = false, ToolbarVisible = false, PropertySort = PropertySort.NoSort, SelectedObject = new GridAdapter(_phot.Aperture, "Aperture") };
            g.Full(new Label { Text = "Aperture (radii in pixels; set FWHM multiple > 0 for adaptive apertures)", AutoSize = true, Tag = "dim" });
            g.Full(props);
            _timeSystem = g.Row("Time stamps", Ui.Combo("BJD (TDB)", "HJD (UTC)", "JD (UTC)"));
            var run = new FlatButton("Measure all frames", Icons.Play, (s, e) => RunPhotometry()) { Kind = ButtonKind.Primary };
            run.AutoSizeToText();
            var aavso = new FlatButton("AAVSO report…", Icons.Export, (s, e) => ExportAavso());
            aavso.AutoSizeToText();
            var csv = new FlatButton("CSV…", Icons.Save, (s, e) => ExportCsv());
            csv.AutoSizeToText();
            g.Full(Bar(run, aavso, csv));
            _measureJob.Guard(addFrames, removeFrame, clear, addSel, vsp, removeStars, _targets, props, run, aavso, csv);
            g.Full(Ui.Hint("Frames without a WCS are plate solved first. Ensemble differential photometry uses every comparison star, weighted by its error; the check star monitors the ensemble."));
            side.Controls.Add(g);

            _measPlot = new PlotControl { Dock = DockStyle.Fill, Title = "Light curves", XLabel = "JD", YLabel = "magnitude", InvertY = true, XFormat = AxisFormat.Jd };
            panel.Controls.Add(_measPlot);
            panel.Controls.Add(side);
            return panel;
        }

        private static FlowLayoutPanel Bar(params Control[] items)
        {
            var f = new FlowLayoutPanel { Height = 40, WrapContents = false };
            foreach (var i in items) { i.Margin = new Padding(0, 0, 6, 0); f.Controls.Add(i); }
            return f;
        }

        private void RefreshFrames()
        {
            _frameList.Items.Clear();
            foreach (var f in _frames) _frameList.Items.Add(Path.GetFileName(f));
        }

        private void AddTargetRow(string name, double ra, double dec, double mag, double bv, string role)
        {
            _targets.Rows.Add(name, AstroMath.FormatHms(ra / 15, 2, true), AstroMath.FormatDms(dec, 1, true, true),
                double.IsNaN(mag) ? "" : mag.ToString("0.000", CultureInfo.InvariantCulture), double.IsNaN(bv) ? "" : bv.ToString("0.000", CultureInfo.InvariantCulture), role);
        }

        private void LoadAavsoComps()
        {
            var name = PlannerView.Prompt("AAVSO comparison stars", "Variable star name (e.g. SS Cyg)", AppState.Selection != null ? AppState.Selection.Name : "");
            if (string.IsNullOrWhiteSpace(name)) return;
            DataTable tab = null;
            _measureJob.Run("AAVSO VSP query", t => tab = OnlineServices.AavsoComparisonStars(name, 60, 15), () =>
            {
                if (tab == null || tab.Rows.Count == 0) { MainForm.Instance.Toast("No comparison stars returned", Icons.Warning); return; }
                for (int i = 0; i < tab.Rows.Count; i++)
                {
                    string ra = tab.Get(i, "ra"), dec = tab.Get(i, "dec");
                    double r = ra.Contains(":") ? AstroMath.ParseRaHours(ra) * 15 : AstroMath.ParseInvariant(ra);
                    double d = dec.Contains(":") ? AstroMath.ParseSexagesimal(dec) : AstroMath.ParseInvariant(dec);
                    AddTargetRow("AAVSO " + tab.Get(i, "label") + " (" + tab.Get(i, "auid") + ")", r, d, tab.GetDouble(i, "V"), tab.GetDouble(i, "B-V"), "Comparison");
                }
                MainForm.Instance.Toast(tab.Rows.Count + " comparison stars added");
            });
        }

        private List<PhotometryTarget> ReadTargets()
        {
            var list = new List<PhotometryTarget>();
            foreach (DataGridViewRow r in _targets.Rows)
            {
                if (r.IsNewRow) continue;
                string name = Convert.ToString(r.Cells["Name"].Value), ra = Convert.ToString(r.Cells["Ra"].Value), dec = Convert.ToString(r.Cells["Dec"].Value);
                if (string.IsNullOrWhiteSpace(ra) || string.IsNullOrWhiteSpace(dec)) continue;
                string roleS = Convert.ToString(r.Cells["Role"].Value);
                var t = new PhotometryTarget
                {
                    Name = string.IsNullOrWhiteSpace(name) ? "Star " + (list.Count + 1) : name,
                    Ra = AstroMath.ParseRaHours(ra) * 15, Dec = AstroMath.ParseSexagesimal(dec),
                    CatalogMag = AstroMath.ParseInvariant(Convert.ToString(r.Cells["Mag"].Value)),
                    CatalogColor = AstroMath.ParseInvariant(Convert.ToString(r.Cells["Bv"].Value)),
                    IsComparison = roleS == "Comparison", IsCheck = roleS == "Check"
                };
                if (double.IsNaN(t.Ra) || double.IsNaN(t.Dec)) continue;
                list.Add(t);
            }
            return list;
        }

        private void RunPhotometry()
        {
            if (_measureJob.Refuse()) return; // the measurement below would be cleared under it
            var targets = ReadTargets();
            if (_frames.Count == 0 || targets.Count == 0) { MainForm.Instance.Toast("Add frames and at least one target and comparison star", Icons.Info); return; }
            if (!targets.Any(t => t.IsComparison)) { MainForm.Instance.Toast("At least one comparison star is required", Icons.Info); return; }
            _phot.Targets = targets;
            _phot.Curves.Clear();
            int timeSys = _timeSystem.SelectedIndex;
            var frames = _frames.ToList();
            var site = AppState.Site;
            int failed = 0;
            _measureJob.Run("Differential photometry", task =>
            {
                for (int i = 0; i < frames.Count; i++)
                {
                    task.Cts.Token.ThrowIfCancellationRequested();
                    Tasks.Report(task, (double)i / frames.Count, Path.GetFileName(frames[i]));
                    var img = ImageFile.Load(frames[i]);
                    if (img.Channels == 1 && img.Bayer != BayerPattern.None) img = Debayer.Apply(img, DebayerMethod.SuperPixel);
                    var wcs = Wcs.FromHeader(img.Header, img.Width, img.Height);
                    if (wcs == null)
                    {
                        var c = targets[0];
                        var r = PlateSolver.Solve(img, new SolveOptions { HintRa = c.Ra, HintDec = c.Dec, SearchRadius = 3, Catalog = new LocalCatalogProvider() }, null, task.Cts.Token);
                        if (!r.Success) { failed++; continue; }
                        wcs = r.Wcs;
                    }
                    var start = img.DateObs;
                    if (start == null) { failed++; continue; }
                    double jd = AstroTime.FromDateTime(start.Value).JdUtc + img.ExposureSeconds / 2 / 86400.0;
                    var tgt = targets.First(q => !q.IsComparison);
                    double tj = timeSys == 0 ? TimeCorrections.BjdTdb(jd, tgt.Ra, tgt.Dec) : timeSys == 1 ? TimeCorrections.HjdUtc(jd, tgt.Ra, tgt.Dec) : jd;
                    double alt = AstroEvents.FixedAltitude(tgt.Ra * AstroMath.Deg2Rad, tgt.Dec * AstroMath.Deg2Rad, site, new AstroTime(jd));
                    _phot.AddFrame(img, wcs, tj, Transforms.Airmass(alt * AstroMath.Deg2Rad), img.Filter ?? "V");
                }
            }, () =>
            {
                PlotMeasured();
                var first = _phot.Curves.FirstOrDefault();
                if (first.Value != null) UseCurve(first.Key, first.Value.Select(p => p.Jd), first.Value.Select(p => p.Mag), first.Value.Select(p => p.MagError), false);
                MainForm.Instance.Toast(string.Format("Measured {0} frames{1}", frames.Count - failed, failed > 0 ? string.Format(" ({0} skipped: no WCS/time)", failed) : ""), Icons.Check);
            });
        }

        private void PlotMeasured()
        {
            _measPlot.Clear();
            foreach (var kv in _phot.Curves)
            {
                var s = _measPlot.AddSeries(kv.Key, SeriesStyle.Scatter);
                var c = _measPlot.AddSeries(kv.Key + " check", SeriesStyle.Scatter, pal => pal.TextDim);
                foreach (var p in kv.Value.Where(p => !p.Rejected))
                {
                    s.Add(p.Jd, p.Mag, p.MagError);
                    if (!double.IsNaN(p.CheckMag)) c.Add(p.Jd, p.CheckMag);
                }
                if (c.X.Count == 0) _measPlot.Series.Remove(c);
            }
            _measPlot.Invalidate();
        }

        private void ExportAavso()
        {
            if (_phot.Curves.Count == 0) { MainForm.Instance.Toast("Measure frames first", Icons.Info); return; }
            var kv = _phot.Curves.First();
            var comps = _phot.Targets.Where(t => t.IsComparison).ToList();
            var check = _phot.Targets.FirstOrDefault(t => t.IsCheck);
            string code = AppState.Settings.AavsoObserverCode;
            if (string.IsNullOrEmpty(code)) code = PlannerView.Prompt("AAVSO", "Your AAVSO observer code", "") ?? "";
            if (code.Length > 0) { AppState.Settings.AavsoObserverCode = code; AppState.Save(); }
            string text = DifferentialPhotometry.AavsoExtended(kv.Key, kv.Value, code, comps.Count == 1 ? comps[0].Name : "ENSEMBLE", check != null ? check.Name : null, null);
            using (var d = new SaveFileDialog { Filter = "AAVSO extended format|*.txt", FileName = kv.Key.Replace(' ', '_') + "_aavso.txt" })
                if (d.ShowDialog() == DialogResult.OK) { File.WriteAllText(d.FileName, text); MainForm.Instance.Toast("AAVSO report written"); }
        }

        private void ExportCsv()
        {
            if (_phot.Curves.Count == 0) { MainForm.Instance.Toast("Measure frames first", Icons.Info); return; }
            using (var d = new FolderBrowserDialog { Description = "Folder for one CSV per target" })
                if (d.ShowDialog() == DialogResult.OK)
                    foreach (var kv in _phot.Curves) DifferentialPhotometry.ExportCsv(Path.Combine(d.SelectedPath, kv.Key.Replace(' ', '_') + ".csv"), kv.Value);
        }

        // ================================================================== analysis

        private PlotControl _curvePlot, _periodPlot, _phasePlot;
        private ComboBox _method;
        private NumericUpDown _pMin, _pMax, _detrend;
        private TextBox _anaOut;
        private Label _curveLabel;
        private double _period = double.NaN, _epoch = double.NaN;
        private double _selX0 = double.NaN, _selX1 = double.NaN;

        private Control BuildAnalysis()
        {
            var panel = new Panel { Dock = DockStyle.Fill };
            var side = new Card("Analysis") { Dock = DockStyle.Left, Width = 340, AutoScroll = true };
            var g = new FormGrid(130);
            _curveLabel = new Label { Tag = "dim", AutoSize = true, MaximumSize = new Size(300, 0), Text = "No light curve loaded" };
            g.Full(_curveLabel);
            var load = new FlatButton("Load CSV / text…", Icons.Open, (s, e) => LoadCurveFile()); load.AutoSizeToText();
            var demo = new FlatButton("Demo data", Icons.Star, (s, e) => DemoCurve()) { Kind = ButtonKind.Subtle }; demo.AutoSizeToText();
            g.Full(Bar(load, demo));
            _method = g.Row("Method", Ui.Combo("Lomb-Scargle (generalised)", "Box least squares (transits)", "Phase dispersion minimisation"));
            _pMin = g.Row("Min period (d)", Ui.Num(0.05m, 0.001m, 1000, 3, 0.01m));
            _pMax = g.Row("Max period (d)", Ui.Num(10, 0.01m, 10000, 2, 0.5m));
            _detrend = g.Row("Detrend order", Ui.Num(0, 0, 5));
            var go = new FlatButton("Find period", Icons.Search, (s, e) => RunPeriodSearch()) { Kind = ButtonKind.Primary }; go.AutoSizeToText();
            var clip = new FlatButton("Sigma clip", Icons.Filter, (s, e) => SigmaClipCurve()); clip.AutoSizeToText();
            g.Full(Bar(go, clip));
            var kvw = new FlatButton("Time minimum (Kwee-van Woerden)", Icons.Clock, (s, e) => TimeMinimum()); kvw.Width = 300;
            var oc = new FlatButton("O−C from minima list…", Icons.Chart, (s, e) => OcDiagram()); oc.Width = 300;
            g.Full(kvw); g.Full(oc);
            _analysisJob.Guard(load, demo, go, clip, kvw, oc);
            g.Full(Ui.Hint("Drag across the light curve to select an eclipse or transit for minimum timing. Click the periodogram to try another peak."));
            _anaOut = new TextBox { Multiline = true, ReadOnly = true, Height = 170, Font = Theme.MonoFont, ScrollBars = ScrollBars.Vertical };
            g.Full(_anaOut);
            side.Controls.Add(g);

            var plots = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1 };
            for (int i = 0; i < 3; i++) plots.RowStyles.Add(new RowStyle(SizeType.Percent, 33.3f));
            _curvePlot = new PlotControl { Dock = DockStyle.Fill, Title = "Light curve", XLabel = "time (d)", SelectRangeMode = true };
            _periodPlot = new PlotControl { Dock = DockStyle.Fill, Title = "Periodogram", XLabel = "period (d)", YLabel = "power" };
            _phasePlot = new PlotControl { Dock = DockStyle.Fill, Title = "Phase folded", XLabel = "phase" };
            _curvePlot.RangeSelected += (a, b) => { _selX0 = Math.Min(a, b); _selX1 = Math.Max(a, b); PlotCurve(); };
            _periodPlot.PlotClick += (x, y) => { if (x > 0) { _period = x; PlotPhase(); } };
            plots.Controls.Add(_curvePlot, 0, 0); plots.Controls.Add(_periodPlot, 0, 1); plots.Controls.Add(_phasePlot, 0, 2);
            panel.Controls.Add(plots);
            panel.Controls.Add(side);
            return panel;
        }

        private void UseCurve(string name, IEnumerable<double> t, IEnumerable<double> y, IEnumerable<double> e, bool isFlux)
        {
            _t.Clear(); _y.Clear(); _e.Clear();
            _t.AddRange(t); _y.AddRange(y); _e.AddRange(e ?? _t.Select(q => 0.0));
            while (_e.Count < _t.Count) _e.Add(0);
            _isFlux = isFlux; _curveName = name;
            _period = double.NaN; _epoch = double.NaN; _selX0 = _selX1 = double.NaN;
            _curveLabel.Text = string.Format("{0}: {1} points, {2:0.###} d span, rms {3:0.0000}", name, _t.Count, _t.Count > 1 ? _t.Max() - _t.Min() : 0, _t.Count > 1 ? Stats.StdDev(_y) : 0);
            _curvePlot.InvertY = !isFlux;
            _curvePlot.XFormat = _t.Count > 0 && _t[0] > 2000000 ? AxisFormat.Jd : AxisFormat.Number;
            _phasePlot.InvertY = !isFlux;
            _curvePlot.YLabel = _phasePlot.YLabel = isFlux ? "relative flux" : "magnitude";
            PlotCurve();
            _periodPlot.Clear(); _phasePlot.Clear();
            if (_tfT0 != null && _t.Count > 0) _tfT0.Text = _t[_y.IndexOf(isFlux ? _y.Min() : _y.Max())].ToString("0.00000", CultureInfo.InvariantCulture);
            PlotTransit(null);
        }

        private void PlotCurve()
        {
            _curvePlot.Clear();
            var s = _curvePlot.AddSeries(_curveName, SeriesStyle.Scatter);
            for (int i = 0; i < _t.Count; i++) s.Add(_t[i], _y[i], _e[i] > 0 ? _e[i] : double.NaN);
            if (!double.IsNaN(_selX0)) _curvePlot.Bands.Add(new PlotBand { X0 = _selX0, X1 = _selX1, ThemeColor = pal => Theme.WithAlpha(pal.Accent, 50), Label = "selection" });
            _curvePlot.Invalidate();
        }

        private void LoadCurveFile()
        {
            using (var d = new OpenFileDialog { Filter = "Light curves|*.csv;*.txt;*.dat;*.tbl|All files|*.*" })
            {
                if (d.ShowDialog() != DialogResult.OK) return;
                var t = new List<double>(); var y = new List<double>(); var e = new List<double>();
                bool flux = false;
                var lines = File.ReadAllLines(d.FileName);
                int tc = 0, yc = 1, ec = 2;
                // header detection: look for a time column and a magnitude or flux column
                var header = lines.FirstOrDefault(l => l.Trim().Length > 0 && !char.IsDigit(l.Trim().TrimStart('#', ' ')[0]) && l.Trim()[0] != '-');
                char[] seps = { ',', ';', '\t', ' ' };
                if (header != null)
                {
                    var cols = header.TrimStart('#').Split(seps, StringSplitOptions.RemoveEmptyEntries).Select(c => c.Trim().ToLowerInvariant()).ToList();
                    Func<string[], int> find = names => cols.FindIndex(c => names.Any(n => c == n || c.StartsWith(n)));
                    int a = find(new[] { "bjd", "hjd", "jd", "time", "date", "t" });
                    int b = find(new[] { "mag", "rel_flux", "flux", "relflux", "norm" });
                    if (a >= 0) tc = a;
                    if (b >= 0) { yc = b; flux = cols[b].Contains("flux") || cols[b].Contains("norm"); }
                    int c2 = cols.FindIndex(c => (c.Contains("err") || c.Contains("sig") || c.StartsWith("e_")) && cols.IndexOf(c) > yc);
                    ec = c2 >= 0 ? c2 : -1;
                }
                foreach (var l in lines)
                {
                    var p = l.Split(seps, StringSplitOptions.RemoveEmptyEntries);
                    if (p.Length <= Math.Max(tc, yc)) continue;
                    double tv = AstroMath.ParseInvariant(p[tc]), yv = AstroMath.ParseInvariant(p[yc]);
                    if (double.IsNaN(tv) || double.IsNaN(yv)) continue;
                    t.Add(tv); y.Add(yv); e.Add(ec >= 0 && ec < p.Length ? AstroMath.ParseInvariant(p[ec]) : 0);
                }
                if (t.Count < 5) { MainForm.Instance.Toast("Could not read a time series from that file", Icons.Warning); return; }
                if (!flux && y.Average() > 0.2 && y.Average() < 5 && y.Max() < 5) flux = true; // looks normalised
                for (int i = 0; i < e.Count; i++) if (double.IsNaN(e[i])) e[i] = 0;
                UseCurve(Path.GetFileNameWithoutExtension(d.FileName), t, y, e, flux);
            }
        }

        private void DemoCurve()
        {
            // synthetic eclipsing-binary-like variable with noise, for exploring the tools
            var rnd = new Random(3);
            var t = new List<double>(); var y = new List<double>(); var e = new List<double>();
            double P = 0.83725, T0 = 2460000.4312;
            for (int n = 0; n < 9; n++)
                for (int k = 0; k < 60; k++)
                {
                    double tt = 2460000.3 + n * 1.07 + k * 0.004 + rnd.NextDouble() * 0.001;
                    double ph = ((tt - T0) / P) % 1; if (ph < 0) ph++;
                    double m = 11.2 + 0.12 * Math.Cos(4 * Math.PI * ph) * 0.3;
                    double d1 = Math.Min(Math.Abs(ph), Math.Abs(1 - ph)), d2 = Math.Abs(ph - 0.5);
                    m += 0.65 * Math.Exp(-d1 * d1 / (2 * 0.025 * 0.025)) + 0.25 * Math.Exp(-d2 * d2 / (2 * 0.025 * 0.025));
                    t.Add(tt); y.Add(m + Gauss(rnd) * 0.008); e.Add(0.008);
                }
            UseCurve("Demo eclipsing binary", t, y, e, false);
            _pMin.Value = 0.1m; _pMax.Value = 3;
        }

        private static double Gauss(Random r) => Math.Sqrt(-2 * Math.Log(1 - r.NextDouble())) * Math.Cos(2 * Math.PI * r.NextDouble());

        private List<double> AnalysisY()
        {
            int deg = (int)_detrend.Value;
            if (deg == 0) return _y.ToList();
            return LightCurveTools.Detrend(_t, _y, deg).ToList();
        }

        private void RunPeriodSearch()
        {
            if (_t.Count < 10) { MainForm.Instance.Toast("Load a light curve first", Icons.Info); return; }
            int method = _method.SelectedIndex;
            double pmin = (double)_pMin.Value, pmax = (double)_pMax.Value;
            var t = _t.ToList(); var y = AnalysisY(); var e = _e.ToList();
            if (method == 1 && !_isFlux) y = y.Select(m => Math.Pow(10, -0.4 * (m - y.Average()))).ToList(); // BLS works on flux
            Periodogram pg = null;
            _analysisJob.Run("Period search", task =>
            {
                pg = method == 0 ? PeriodSearch.LombScargle(t, y, e.Any(v => v > 0) ? e : null, pmin, pmax)
                    : method == 1 ? PeriodSearch.Bls(t, y, pmin, pmax) : PeriodSearch.Pdm(t, y, pmin, pmax);
            }, () =>
            {
                _periodPlot.Clear();
                var s = _periodPlot.AddSeries(pg.Method);
                for (int i = 0; i < pg.Frequencies.Length; i++) s.Add(1 / pg.Frequencies[i], pg.Power[i]);
                _periodPlot.Markers.Add(new PlotMarker { X = pg.BestPeriod, Label = pg.BestPeriod.ToString("0.00000", CultureInfo.InvariantCulture) + " d" });
                _periodPlot.Invalidate();
                _period = pg.BestPeriod;
                PlotPhase();
                _anaOut.Text = string.Format("{0}\r\nBest period: {1:0.000000} d ({2:0.000} h)\r\nPower: {3:0.0000}{4}\r\nTip: eclipsing binaries often show the strongest peak at P/2 with Lomb-Scargle; check the phase plot.",
                    pg.Method, pg.BestPeriod, pg.BestPeriod * 24, pg.BestPower, double.IsNaN(pg.FalseAlarmProbability) ? "" : string.Format("\r\nFalse-alarm probability: {0:0.###E+0}", pg.FalseAlarmProbability));
                if (method == 1 && _tfPeriod != null) _tfPeriod.Text = pg.BestPeriod.ToString("0.000000", CultureInfo.InvariantCulture);
            });
        }

        private void PlotPhase()
        {
            if (double.IsNaN(_period) || _t.Count == 0) return;
            var y = AnalysisY();
            if (double.IsNaN(_epoch)) _epoch = _t[y.IndexOf(_isFlux ? y.Min() : y.Max())];
            var ph = PeriodSearch.Phase(_t, _period, _epoch);
            _phasePlot.Clear();
            _phasePlot.Title = string.Format("Phase folded at P = {0:0.000000} d", _period);
            var s = _phasePlot.AddSeries("data", SeriesStyle.Scatter);
            var s2 = _phasePlot.AddSeries("repeat", SeriesStyle.Scatter, pal => pal.TextDim);
            for (int i = 0; i < ph.Length; i++) { s.Add(ph[i], y[i]); s2.Add(ph[i] + 1, y[i]); }
            List<double> bt, by, be;
            var order = Enumerable.Range(0, ph.Length).OrderBy(i => ph[i]).ToList();
            LightCurveTools.Bin(order.Select(i => ph[i]).ToList(), order.Select(i => y[i]).ToList(), 0.02, out bt, out by, out be);
            var b = _phasePlot.AddSeries("binned", SeriesStyle.Line, pal => pal.Warning);
            for (int i = 0; i < bt.Count; i++) b.Add(bt[i], by[i]);
            _phasePlot.Invalidate();
        }

        private void SigmaClipCurve()
        {
            if (_t.Count < 10) return;
            var keep = LightCurveTools.SigmaClip(AnalysisY(), 4);
            int removed = keep.Count(k => !k);
            var t = new List<double>(); var y = new List<double>(); var e = new List<double>();
            for (int i = 0; i < keep.Length; i++) if (keep[i]) { t.Add(_t[i]); y.Add(_y[i]); e.Add(_e[i]); }
            UseCurve(_curveName, t, y, e, _isFlux);
            MainForm.Instance.Toast(removed + " outliers removed");
        }

        private void TimeMinimum()
        {
            if (double.IsNaN(_selX0)) { MainForm.Instance.Toast("Drag across a minimum in the light curve first", Icons.Info); return; }
            var idx = Enumerable.Range(0, _t.Count).Where(i => _t[i] >= _selX0 && _t[i] <= _selX1).ToList();
            if (idx.Count < 7) { MainForm.Instance.Toast("Select at least 7 points", Icons.Info); return; }
            var mags = idx.Select(i => _isFlux ? -2.5 * Math.Log10(Math.Max(1e-9, _y[i])) : _y[i]).ToList();
            double err;
            double tmin = PeriodSearch.KweeVanWoerden(idx.Select(i => _t[i]).ToList(), mags, out err);
            _anaOut.AppendText(string.Format("\r\nMinimum (KvW): {0:0.00000} ± {1:0.00000} d ({2} points)", tmin, err, idx.Count));
            Clipboard.SetText(tmin.ToString("0.00000", CultureInfo.InvariantCulture));
            _curvePlot.Markers.Add(new PlotMarker { X = tmin, Label = "min" });
            _curvePlot.Invalidate();
        }

        private void OcDiagram()
        {
            string input = PlannerView.Prompt("O−C diagram", "Minima times (JD), separated by spaces or commas; then epoch and period are taken from the phase fold", "");
            if (string.IsNullOrWhiteSpace(input)) return;
            var mins = input.Split(new[] { ' ', ',', ';', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries).Select(v => AstroMath.ParseInvariant(v)).Where(v => !double.IsNaN(v)).ToList();
            if (mins.Count < 2 || double.IsNaN(_period)) { MainForm.Instance.Toast("Need at least two minima and a period (run the period search)", Icons.Info); return; }
            var oc = LightCurveTools.OMinusC(mins, mins[0], _period);
            _phasePlot.Clear();
            _phasePlot.Title = "O−C diagram"; _phasePlot.XLabel = "cycle"; _phasePlot.YLabel = "O−C (min)"; _phasePlot.InvertY = false;
            var s = _phasePlot.AddSeries("O−C", SeriesStyle.LineScatter);
            foreach (var p in oc) s.Add(p[0], p[1] * 1440);
            double a, b, r2;
            if (Stats.LinearFit(oc.Select(p => p[0]).ToList(), oc.Select(p => p[1]).ToList(), out a, out b, out r2))
                _anaOut.AppendText(string.Format("\r\nRefined ephemeris: Min = {0:0.00000} + {1:0.0000000} × E", mins[0] + a, _period + b));
            _phasePlot.Invalidate();
        }

        // ================================================================== transits

        private TextBox _tfT0, _tfPeriod, _tfOut;
        private NumericUpDown _tfRstar, _tfSteps;
        private CheckBox _tfLd;
        private PlotControl _tfPlot;
        private TransitModel _model;

        private Control BuildTransit()
        {
            var panel = new Panel { Dock = DockStyle.Fill };
            var side = new Card("Transit model") { Dock = DockStyle.Left, Width = 340, AutoScroll = true };
            var g = new FormGrid(130);
            g.Full(Ui.Hint("Uses the light curve from the analysis tab (magnitudes are converted to relative flux). Quadratic limb darkening, Mandel & Agol geometry."));
            _tfT0 = g.Row("Mid-transit T0", new TextBox());
            _tfPeriod = g.Row("Period (d)", new TextBox { Text = "3.0" });
            _tfRstar = g.Row("Star radius (R☉)", Ui.Num(1, 0.1m, 100, 2, 0.05m));
            _tfLd = g.Row("Fit limb darkening", new CheckBox());
            _tfSteps = g.Row("MCMC steps", Ui.Num(1500, 200, 50000, 0, 100));
            var fit = new FlatButton("Fit (Levenberg-Marquardt)", Icons.Chart, (s, e) => FitTransit(false)) { Kind = ButtonKind.Primary }; fit.Width = 300;
            var mc = new FlatButton("Posterior (MCMC)", Icons.Layers, (s, e) => FitTransit(true)); mc.Width = 300;
            g.Full(fit); g.Full(mc);
            _transitJob.Guard(fit, mc);
            _tfOut = new TextBox { Multiline = true, ReadOnly = true, Height = 260, Font = Theme.MonoFont, ScrollBars = ScrollBars.Vertical };
            g.Full(_tfOut);
            side.Controls.Add(g);
            _tfPlot = new PlotControl { Dock = DockStyle.Fill, Title = "Transit", XLabel = "hours from mid-transit", YLabel = "relative flux" };
            panel.Controls.Add(_tfPlot);
            panel.Controls.Add(side);
            return panel;
        }

        private void FluxData(out List<double> f, out List<double> df)
        {
            f = new List<double>(); df = new List<double>();
            if (_isFlux)
            {
                double med = Stats.Median(_y);
                for (int i = 0; i < _y.Count; i++) { f.Add(_y[i] / med); df.Add(_e[i] > 0 ? _e[i] / med : double.NaN); }
            }
            else
            {
                double med = Stats.Median(_y);
                for (int i = 0; i < _y.Count; i++) { double fl = Math.Pow(10, -0.4 * (_y[i] - med)); f.Add(fl); df.Add(_e[i] > 0 ? fl * _e[i] / 1.0857 : double.NaN); }
            }
            if (df.Any(double.IsNaN))
            {
                double sc = Stats.StdDev(f) * 0.5 + 1e-5;
                for (int i = 0; i < df.Count; i++) if (double.IsNaN(df[i])) df[i] = sc;
            }
        }

        private void FitTransit(bool mcmc)
        {
            if (_t.Count < 10) { MainForm.Instance.Toast("Load a light curve in the analysis tab first", Icons.Info); return; }
            double t0 = AstroMath.ParseInvariant(_tfT0.Text), per = AstroMath.ParseInvariant(_tfPeriod.Text);
            if (double.IsNaN(t0) || double.IsNaN(per) || per <= 0) { MainForm.Instance.Toast("Enter T0 and the period", Icons.Info); return; }
            List<double> f, df; FluxData(out f, out df);
            double depth = Math.Max(0.001, 1 - f.Min());
            var m = _model != null && Math.Abs(_model.T0 - t0) < 0.2 ? _model.Clone() : new TransitModel { T0 = t0, Period = per, Rp = Math.Sqrt(depth), A = 10, Inclination = 88.5 };
            m.T0 = t0; m.Period = per;
            bool ld = _tfLd.Checked; int steps = (int)_tfSteps.Value; double rstar = (double)_tfRstar.Value;
            var t = _t.ToList();
            string report = "";
            FitResult fr = null;
            _transitJob.Run(mcmc ? "Transit MCMC" : "Transit fit", task =>
            {
                fr = m.Fit(t, f, df, ld);
                var sb = new System.Text.StringBuilder();
                string[] names = { "T0", "Rp/R*", "a/R*", "i (°)", "baseline", "slope" , "u1", "u2" };
                for (int i = 0; i < fr.Parameters.Length; i++) sb.AppendLine(string.Format("{0,-9}{1,14:0.000000} ± {2:0.000000}", names[i], fr.Parameters[i], fr.Errors != null && i < fr.Errors.Length ? fr.Errors[i] : double.NaN));
                sb.AppendLine(string.Format("χ²ᵣ = {0:0.000}", fr.ReducedChiSquare));
                if (mcmc)
                {
                    double[] med, lo, hi;
                    m.Mcmc(t, f, df, steps, out med, out lo, out hi);
                    sb.AppendLine("\r\nMCMC posterior (median, −1σ/+1σ):");
                    string[] mn = { "T0", "Rp/R*", "a/R*", "b" };
                    for (int i = 0; i < 4; i++) sb.AppendLine(string.Format("{0,-9}{1,14:0.000000}  −{2:0.000000} +{3:0.000000}", mn[i], med[i], med[i] - lo[i], hi[i] - med[i]));
                }
                double rpJ, aAu; m.Physical(rstar, out rpJ, out aAu);
                sb.AppendLine(string.Format("\r\nDepth {0:0.00} ppt · duration {1:0.00} h · b = {2:0.00}", m.Depth * 1000, m.Duration * 24, m.ImpactParameter));
                sb.AppendLine(string.Format("Planet radius {0:0.00} R♃ ({1:0.0} R⊕) · a = {2:0.0000} AU", rpJ, rpJ * 11.209, aAu));
                sb.AppendLine(string.Format("Residual scatter {0:0.00} ppt", LightCurveTools.ScatterPpt(f.Select((v, i) => v / m.Flux(t[i])).ToList())));
                report = sb.ToString();
            }, () =>
            {
                _model = m;
                _tfT0.Text = m.T0.ToString("0.000000", CultureInfo.InvariantCulture);
                _tfOut.Text = report;
                PlotTransit(m);
            });
        }

        private void PlotTransit(TransitModel m)
        {
            if (_tfPlot == null || _t.Count == 0) return;
            List<double> f, df; FluxData(out f, out df);
            double t0 = m != null ? m.T0 : AstroMath.ParseInvariant(_tfT0.Text);
            if (double.IsNaN(t0)) t0 = _t[0];
            double per = m != null ? m.Period : AstroMath.ParseInvariant(_tfPeriod.Text);
            _tfPlot.Clear();
            var s = _tfPlot.AddSeries("data", SeriesStyle.Scatter);
            Func<double, double> hrs = tt =>
            {
                double d = tt - t0;
                if (!double.IsNaN(per) && per > 0) d -= Math.Round(d / per) * per;
                return d * 24;
            };
            for (int i = 0; i < _t.Count; i++) s.Add(hrs(_t[i]), f[i]);
            if (m != null)
            {
                var ms = _tfPlot.AddSeries("model", SeriesStyle.Line, pal => pal.Warning);
                ms.Width = 2.2f;
                double span = Math.Max(m.Duration * 1.6, 0.1);
                for (double dt = -span; dt <= span; dt += span / 200) ms.Add(dt * 24, m.Flux(m.T0 + dt));
            }
            _tfPlot.Invalidate();
        }

        // ================================================================== predictions

        private DataGridView _predGrid;
        private DataTable _exo;

        private Control BuildPredictions()
        {
            var panel = new Panel { Dock = DockStyle.Fill };
            var vmax = Ui.Num(12, 6, 16, 1, 0.5m);
            var depth = Ui.Num(5, 0.5m, 50, 1, 0.5m);
            var days = Ui.Num(7, 1, 60);
            var fetch = new FlatButton("Predict transits", Icons.Download, (s, e) => Predict((double)vmax.Value, (double)depth.Value, (int)days.Value)) { Kind = ButtonKind.Primary };
            fetch.AutoSizeToText();
            _predictJob.Guard(fetch);
            var bar = Ui.Toolbar(new Label { Text = "Host V ≤", AutoSize = true, Margin = new Padding(0, 6, 4, 0) }, vmax,
                new Label { Text = "Depth ≥ (ppt)", AutoSize = true, Margin = new Padding(0, 6, 4, 0) }, depth,
                new Label { Text = "Nights", AutoSize = true, Margin = new Padding(0, 6, 4, 0) }, days, fetch);
            _predGrid = Ui.Grid();
            foreach (var c in new[] { "Planet", "Host V", "Depth (ppt)", "Duration (h)", "Ingress (local)", "Mid (local)", "Egress (local)", "Alt at mid", "Sun alt" }) _predGrid.Columns.Add(c, c);
            _predGrid.CellDoubleClick += (s, e) =>
            {
                if (e.RowIndex < 0) return;
                var row = _predGrid.Rows[e.RowIndex].Tag as double[];
                if (row == null) return;
                var o = new SelectedObject { Kind = SelectedKind.Coordinates, Name = Convert.ToString(_predGrid.Rows[e.RowIndex].Cells[0].Value), RaDeg = row[0], DecDeg = row[1] };
                AppState.Select(o);
                MainForm.Instance.Toast("Selected " + o.Name + " (use Planner or Sky to plan)");
            };
            panel.Controls.Add(_predGrid);
            panel.Controls.Add(new Label { Dock = DockStyle.Top, Height = 24, Tag = "dim", Text = "Transits fully observable at night (Sun below −12°, host above 25° at ingress, mid and egress) from the active site. Data: NASA Exoplanet Archive (pscomppars). Double-click to select." });
            panel.Controls.Add(bar);
            return panel;
        }

        private void Predict(double vmax, double minDepthPpt, int nights)
        {
            var site = AppState.Site;
            var start = AppState.Now;
            var rows = new List<object[]>();
            _predictJob.Run("Exoplanet transit predictions", task =>
            {
                if (_exo == null)
                {
                    Tasks.Report(task, double.NaN, "Querying NASA Exoplanet Archive…");
                    _exo = OnlineServices.Exoplanets(16, true);
                }
                for (int i = 0; i < _exo.Rows.Count; i++)
                {
                    double v = _exo.GetDouble(i, "sy_vmag"), P = _exo.GetDouble(i, "pl_orbper"), Tc = _exo.GetDouble(i, "pl_tranmid"), dur = _exo.GetDouble(i, "pl_trandur") / 24;
                    double dep = _exo.GetDouble(i, "pl_trandep"); // percent
                    double ratror = _exo.GetDouble(i, "pl_ratror");
                    double depthPpt = !double.IsNaN(dep) ? dep * 10 : !double.IsNaN(ratror) ? ratror * ratror * 1000 : double.NaN;
                    if (double.IsNaN(v) || v > vmax || double.IsNaN(P) || double.IsNaN(Tc) || double.IsNaN(dur) || double.IsNaN(depthPpt) || depthPpt < minDepthPpt) continue;
                    double ra = _exo.GetDouble(i, "ra"), dec = _exo.GetDouble(i, "dec");
                    double raR = ra * AstroMath.Deg2Rad, decR = dec * AstroMath.Deg2Rad;
                    // Tc is BJD_TDB; the ~minute-level difference to JD UTC is ignored for planning
                    double n0 = Math.Ceiling((start.JdUtc - Tc) / P);
                    for (double n = n0; Tc + n * P < start.JdUtc + nights; n++)
                    {
                        double mid = Tc + n * P, ing = mid - dur / 2, egr = mid + dur / 2;
                        bool ok = true; double altMid = 0, sunMid = 0;
                        foreach (var tt in new[] { ing, mid, egr })
                        {
                            var at = new AstroTime(tt);
                            double alt = AstroEvents.FixedAltitude(raR, decR, site, at), sun = AstroEvents.SunAltitudeFast(site, at);
                            if (alt < 25 || sun > -12) { ok = false; break; }
                            if (tt == mid) { altMid = alt; sunMid = sun; }
                        }
                        if (!ok) continue;
                        Func<double, string> loc = jd => site.ToLocal(new AstroTime(jd).ToDateTimeUtc()).ToString("ddd dd MMM HH:mm", CultureInfo.InvariantCulture);
                        rows.Add(new object[] { _exo.Get(i, "pl_name"), v.ToString("0.0", CultureInfo.InvariantCulture), depthPpt.ToString("0.0", CultureInfo.InvariantCulture), (dur * 24).ToString("0.0", CultureInfo.InvariantCulture),
                            loc(ing), loc(mid), loc(egr), altMid.ToString("0", CultureInfo.InvariantCulture) + "°", sunMid.ToString("0", CultureInfo.InvariantCulture) + "°", mid, ra, dec });
                    }
                }
            }, () =>
            {
                _predGrid.Rows.Clear();
                foreach (var r in rows.OrderBy(r => (double)r[9]))
                {
                    int k = _predGrid.Rows.Add(r.Take(9).ToArray());
                    _predGrid.Rows[k].Tag = new[] { (double)r[10], (double)r[11] };
                }
                MainForm.Instance.Toast(rows.Count + " observable transits");
            }, ex => MainForm.Instance.Toast("Exoplanet Archive query failed: " + ex.Message, Icons.Warning, Theme.Current.Warning));
        }

        public IEnumerable<AppCommand> GetCommands()
        {
            yield return new AppCommand { Title = "Photometry: measure image sequence", Icon = Icons.Photometry, Run = () => { MainForm.Instance.Navigate("photometry"); _tabs.Select(0); } };
            yield return new AppCommand { Title = "Photometry: period search / light curve", Run = () => { MainForm.Instance.Navigate("photometry"); _tabs.Select(1); } };
            yield return new AppCommand { Title = "Photometry: fit exoplanet transit", Run = () => { MainForm.Instance.Navigate("photometry"); _tabs.Select(2); } };
            yield return new AppCommand { Title = "Photometry: predict exoplanet transits tonight", Run = () => { MainForm.Instance.Navigate("photometry"); _tabs.Select(3); } };
        }
    }
}
