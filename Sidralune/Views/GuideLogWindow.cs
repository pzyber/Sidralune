using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Sidralune.Core.Util;
using Sidralune.Devices;
using Sidralune.Devices.Guiding;
using Sidralune.UI;
using static Sidralune.Core.Util.Lang;

namespace Sidralune.Views
{
    /// <summary>
    /// The guide log viewer: a guide log of Sidralune's or PHD2's, or the session guiding now, as a graph of the error,
    /// its calibration, its statistics for the session and for each frame (the stretches between dithers), and the
    /// spectrum of the RA error, which shows the mount's periodic error and its period.
    /// </summary>
    public class GuideLogWindow : Form
    {
        private readonly DeviceManager _dm;
        private readonly FlatButton _current, _ownError;
        private readonly ComboBox _session;
        private readonly NumericUpDown _worm;
        private readonly Label _about, _calText, _spectrumText;
        private readonly DataGridView _grid;
        private readonly PlotControl _graph, _spectrum, _calPlot;
        private readonly EventHandler _themeChanged;
        private GuideLogFile _log;
        private bool _loading;

        public GuideLogWindow(DeviceManager dm)
        {
            _dm = dm;
            Text = T("Guide log");
            Icon = MainForm.AppIcon;
            Font = Theme.UiFont;
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(1280, 820);
            MinimumSize = new Size(900, 600);

            var open = new FlatButton(T("Open…"), Icons.Open, (s, e) => OpenFile());
            open.AutoSizeToText();
            _current = new FlatButton(T("This session"), Icons.Refresh, (s, e) => Load(CurrentLogPath()));
            _current.AutoSizeToText();
            _session = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 230 };
            _session.SelectedIndexChanged += (s, e) => { if (!_loading) ShowSession(); };
            // the mount's own error: the corrections added back, when the log gives the RA rate
            _ownError = new FlatButton(T("The mount's own error"), Icons.Chart) { Toggle = true, Checked = true };
            _ownError.AutoSizeToText();
            _ownError.CheckedChanged += (s, e) => ShowSpectrum();
            var wormLabel = new Label { Text = T("Worm period (s)"), AutoSize = true, Padding = new Padding(0, 7, 0, 0), Tag = "dim" };
            _worm = Ui.Num((decimal)Math.Max(10, Math.Min(10000, dm.BuiltinGuider.Settings.PecPeriodSec)), 10, 10000, 0, 1);
            _worm.Width = 80;
            _worm.ValueChanged += (s, e) => ShowSpectrum();
            var close = new FlatButton(T("Close"), Icons.Close, (s, e) => Close());
            close.AutoSizeToText();
            var bar = Ui.Toolbar(open, _current, Ui.Separator(), _session, _ownError, wormLabel, _worm, Ui.Separator(), close);

            _about = new Label { Dock = DockStyle.Top, Height = 44, Padding = new Padding(8, 6, 8, 0), Tag = "dim", UseMnemonic = false };
            _grid = Ui.Grid();
            _grid.MultiSelect = false;
            foreach (var h in new[] { T("Frame"), T("Start"), T("Length"), T("Steps"), T("RA"), T("Dec"), T("Total"), T("Peak") }) _grid.Columns.Add(h, h);
            _grid.SelectionChanged += (s, e) => ZoomToRow();
            _calText = new Label { Dock = DockStyle.Bottom, AutoSize = false, Height = 64, Padding = new Padding(8, 6, 8, 0), UseMnemonic = false };
            _calPlot = new PlotControl { Dock = DockStyle.Bottom, Height = 220, Title = T("Calibration"), XLabel = T("px"), YLabel = T("px") };
            var left = new Panel { Dock = DockStyle.Left, Width = 470, Padding = new Padding(0, 0, 8, 0) };
            left.Controls.Add(_grid);
            left.Controls.Add(_calText);
            left.Controls.Add(_calPlot);
            left.Controls.Add(_about);

            _graph = new PlotControl { Dock = DockStyle.Fill, Title = T("Guiding error"), XFormat = AxisFormat.Time, XLabel = T("time") };
            _spectrumText = new Label { Dock = DockStyle.Top, Height = 44, Padding = new Padding(8, 6, 8, 0), UseMnemonic = false };
            _spectrum = new PlotControl { Dock = DockStyle.Fill, Title = T("Spectrum of the RA error"), XLabel = T("period (min)") };
            var spectrumPanel = new Panel { Dock = DockStyle.Fill };
            spectrumPanel.Controls.Add(_spectrum);
            spectrumPanel.Controls.Add(_spectrumText);
            var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
            right.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            right.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            right.Controls.Add(_graph, 0, 0);
            right.Controls.Add(spectrumPanel, 0, 1);

            Controls.Add(right);
            Controls.Add(left);
            Controls.Add(bar);

            Theme.Apply(this);
            HandleCreated += (s, e) => Theme.DarkTitleBar(this);
            _themeChanged = (s, e) => { Theme.Apply(this); Theme.DarkTitleBar(this); };
            Theme.Changed += _themeChanged;
            Shown += (s, e) => Availability();
            Activated += (s, e) => Availability();
            Clear();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            Theme.Changed -= _themeChanged;
            base.OnFormClosed(e);
        }

        /// <summary>The Sidralune guider's log of the session guiding now (or last), if it has one.</summary>
        private string CurrentLogPath()
        {
            var p = _dm.BuiltinGuider.GuideLogPath;
            return !string.IsNullOrEmpty(p) && File.Exists(p) ? p : null;
        }

        /// <summary>This session is greyed out without a guide log of the Sidralune guider's to show.</summary>
        private void Availability() => _current.Enabled = CurrentLogPath() != null;

        private void OpenFile()
        {
            using (var d = new OpenFileDialog { Filter = T("Guide logs") + "|*.csv;*.txt|" + T("All files") + "|*.*", InitialDirectory = AppPaths.Logs })
                if (d.ShowDialog(this) == DialogResult.OK) Load(d.FileName);
        }

        /// <summary>Reads a guide log and shows its last session; says so when it is not one.</summary>
        public void Load(string path)
        {
            if (path == null) return;
            try { _log = GuideLogFile.Read(path); }
            catch (Exception ex)
            {
                _log = null;
                Clear();
                _about.Text = Path.GetFileName(path) + Environment.NewLine + ex.Message;
                return;
            }
            _loading = true;
            _session.Items.Clear();
            foreach (var s in _log.Sessions)
                _session.Items.Add(T("Guiding from {0:yyyy-MM-dd HH:mm}", s.Start));
            _loading = false;
            if (_log.Sessions.Count == 0)
            {
                Clear();
                _about.Text = Path.GetFileName(path) + Environment.NewLine + T("No guiding in this log.");
                return;
            }
            _session.SelectedIndex = _log.Sessions.Count - 1;
            Availability();
        }

        private GuideLogSession Session => _log != null && _session.SelectedIndex >= 0 && _session.SelectedIndex < _log.Sessions.Count ? _log.Sessions[_session.SelectedIndex] : null;

        private void Clear()
        {
            _graph.Clear(); _spectrum.Clear(); _calPlot.Clear();
            _grid.Rows.Clear();
            _about.Text = T("Open a guide log of Sidralune's or PHD2's, or show this session's.");
            _calText.Text = ""; _spectrumText.Text = "";
            _session.Enabled = false;
        }

        /// <summary>The numbers in arcseconds with the session's scale, else in pixels.</summary>
        private static string Fmt(double px, double scale) =>
            double.IsNaN(px) ? "–" : double.IsNaN(scale) ? T("{0:0.00} px", px) : T("{0:0.00}″", px * scale);

        private void ShowSession()
        {
            var s = Session;
            if (s == null) { Clear(); return; }
            _session.Enabled = _log.Sessions.Count > 1;
            double scale = s.ScaleArcsec;
            var total = s.Total;
            _about.Text = Path.GetFileName(_log.Path) + " · " + _log.Format + Environment.NewLine +
                (double.IsNaN(scale) ? T("The log does not give the pixel scale: the numbers are in pixels.") : T("Pixel scale {0:0.000}″/px", scale));

            // the statistics: the session, then each frame (the stretches between dithers), settling left out
            _grid.Rows.Clear();
            Action<string, GuideLogStats> row = (name, st) => _grid.Rows.Add(name, st.Steps > 0 ? st.Start.ToString("HH:mm:ss") : "", st.Steps > 0 ? FormatLength(st.End - st.Start) : "", st.Steps,
                Fmt(st.RmsRa, scale), Fmt(st.RmsDec, scale), Fmt(st.RmsTotal, scale), Fmt(st.Peak, scale));
            row(T("Session"), total);
            var frames = s.Frames();
            for (int i = 0; i < frames.Count; i++) row((i + 1).ToString(), frames[i]);
            _grid.Tag = frames;

            // the error over time: RA and Dec, settling shaded, lost stars marked
            _graph.Clear();
            _graph.YLabel = double.IsNaN(scale) ? T("px") : T("arcsec");
            double k = double.IsNaN(scale) ? 1 : scale;
            var ra = _graph.AddSeries(T("RA")); var dec = _graph.AddSeries(T("Dec"));
            // dashed, so RA and Dec can be told apart in the red night palette too
            dec.DashDot = true;
            foreach (var st in s.Steps) { double x = st.Time.ToOADate(); ra.Add(x, st.RaPx * k); dec.Add(x, st.DecPx * k); }
            DateTime? settleFrom = null;
            foreach (var st in s.Steps)
            {
                if (st.Settling && settleFrom == null) settleFrom = st.Time;
                if (!st.Settling && settleFrom != null) { _graph.Bands.Add(new PlotBand { X0 = settleFrom.Value.ToOADate(), X1 = st.Time.ToOADate(), ThemeColor = p => Color.FromArgb(40, p.Warning) }); settleFrom = null; }
            }
            foreach (var e in s.Events.Where(e => e.Name == "Star lost" || e.Name == "Star found by plate solve"))
                _graph.Markers.Add(new PlotMarker { X = e.Time.ToOADate(), Label = e.Name == "Star lost" ? T("Star lost") : T("Found by plate solve"), ThemeColor = p => p.Danger });
            ZoomToGuiding(s.Steps.Where(x => !x.Settling).ToList(), s);

            // the calibration it guided with
            _calText.Text = s.Calibration != null ? T("Calibration: {0}", s.Calibration) : T("The log holds no calibration: it guided with one made earlier.");
            _calPlot.Clear();
            foreach (var leg in s.CalibrationSteps.Select(c => c.Item1).Distinct())
            {
                var ser = _calPlot.AddSeries(LegName(leg), SeriesStyle.LineScatter);
                foreach (var c in s.CalibrationSteps.Where(c => c.Item1 == leg)) ser.Add(c.Item2, c.Item3);
            }
            _calPlot.Visible = s.CalibrationSteps.Count > 0;
            _calPlot.Invalidate();
            if (!double.IsNaN(s.WormPeriodSec) && s.WormPeriodSec >= 10) _worm.Value = (decimal)Math.Min(10000, s.WormPeriodSec);
            ShowSpectrum();
        }

        private static string LegName(string leg) =>
            leg == "West" ? T("West") : leg == "East" ? T("East") : leg == "North" ? T("North") : leg == "South" ? T("South") : leg;

        private static string FormatLength(TimeSpan t) => t.TotalHours >= 1 ? string.Format("{0}:{1:mm\\:ss}", (int)t.TotalHours, t) : t.ToString("mm\\:ss");

        /// <summary>The spectrum of the selected session's RA error, with the worm period and its harmonics marked.</summary>
        private void ShowSpectrum()
        {
            var s = Session;
            _spectrum.Clear();
            if (s == null) { _spectrumText.Text = ""; return; }
            double worm = (double)_worm.Value;
            var sp = GuideSpectrum.Of(s, _ownError.Checked, worm);
            string unit = sp.InArcsec ? "″" : " px";
            _spectrum.YLabel = sp.InArcsec ? T("amplitude (″)") : T("amplitude (px)");
            if (sp.PeriodSec.Length == 0)
            {
                _spectrumText.Text = T("Too short a log for a spectrum: it needs at least a few minutes of guiding.");
                _spectrum.Invalidate();
                return;
            }
            var ser = _spectrum.AddSeries(T("RA"));
            for (int i = 0; i < sp.PeriodSec.Length; i++) ser.Add(sp.PeriodSec[i] / 60, sp.Amplitude[i]);
            double maxP = sp.PeriodSec.Max();
            // the worm period and its first harmonic (an eccentric worm, a worn tooth)
            for (int h = 1; h <= 2; h++)
                if (worm / h <= maxP)
                    _spectrum.Markers.Add(new PlotMarker { X = worm / h / 60, Label = h == 1 ? T("worm") : "÷2", ThemeColor = p => p.Warning });
            string what = sp.CorrectionsAdded ? T("The mount's own RA error (the corrections added back at {0:0.00} px/s)", sp.RaRatePxPerSec)
                : _ownError.Checked ? T("The RA error left after guiding (the log does not give the RA rate, so the corrections cannot be added back)")
                : T("The RA error left after guiding");
            string method = sp.Method == "FFT" ? T("FFT") : T("Lomb–Scargle (the frames are unevenly spaced)");
            string peak = T("Largest: ±{0:0.00}{1} at {2:0.0} min.", sp.PeakAmplitude, unit, sp.PeakPeriodSec / 60);
            string wormText = !double.IsNaN(sp.WormAmplitude) ? T("Periodic error: ±{0:0.00}{1} ({2:0.00}{1} peak to peak) at {3:0.0} min.", sp.WormAmplitude, unit, 2 * sp.WormAmplitude, sp.WormPeriodSec / 60)
                : worm > sp.SpanSec / 1.5 ? T("Too short for the worm period: guide at least one and a half worm periods.") : "";
            _spectrumText.Text = what + " · " + method + Environment.NewLine + peak + " " + wormText;
            _spectrum.Invalidate();
        }

        /// <summary>Selecting the session or a frame shows its stretch of the graph.</summary>
        private void ZoomToRow()
        {
            var s = Session;
            var frames = _grid.Tag as List<GuideLogStats>;
            if (s == null || frames == null || _grid.SelectedRows.Count == 0) return;
            int r = _grid.SelectedRows[0].Index;
            if (r == 0) { ZoomToGuiding(s.Steps.Where(x => !x.Settling).ToList(), s); return; }
            if (r - 1 >= frames.Count) return;
            var f = frames[r - 1];
            if (f.Steps == 0) return;
            ZoomToGuiding(s.Steps.Where(x => !x.Settling && x.Time >= f.Start && x.Time <= f.End).ToList(), s);
        }

        /// <summary>
        /// The graph over these steps, its height set by them: the jumps of dithers and the settling after them run off
        /// it, so the guiding itself can be seen (Reset zoom shows everything).
        /// </summary>
        private void ZoomToGuiding(List<GuideLogStep> steps, GuideLogSession s)
        {
            if (steps.Count == 0) { _graph.ResetZoom(); return; }
            double k = double.IsNaN(s.ScaleArcsec) ? 1 : s.ScaleArcsec;
            double lim = Math.Max(0.5, steps.Max(x => Math.Max(Math.Abs(x.RaPx), Math.Abs(x.DecPx))) * k * 1.15);
            DateTime a = steps[0].Time, b = steps[steps.Count - 1].Time;
            double pad = Math.Max(5.0 / 86400, (b - a).TotalDays * 0.02);
            _graph.ZoomTo(a.ToOADate() - pad, b.ToOADate() + pad, -lim, lim);
        }
    }
}
