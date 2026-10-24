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
using Sidralune.Imaging;
using Sidralune.Science.Radio;
using Sidralune.UI;

namespace Sidralune.Views
{
    /// <summary>
    /// Amateur radio astronomy: system calculator, hydrogen-line spectroscopy from SDR IQ recordings or a live rtl_tcp
    /// server (waterfall, LSR velocities, on/off calibration, baseline, kinematic distances), Jupiter decametric storm
    /// predictions, and time-domain tools (drift scans, pulsar folding, SID detection).
    /// </summary>
    public class RadioView : UserControl, ICommandSource
    {
        private readonly TabbedPanel _tabs = new TabbedPanel { Dock = DockStyle.Fill, Padding = new Padding(10) };

        public RadioView()
        {
            Controls.Add(_tabs);
            _tabs.AddPage("Hydrogen line / spectra", BuildSpectrum());
            _tabs.AddPage("Radio telescope calculator", BuildCalculator());
            _tabs.AddPage("Jupiter & Sun", BuildJupiter());
            _tabs.AddPage("Time domain", BuildTimeDomain());
        }

        // ================================================================== spectra

        private readonly PlotControl _specPlot = new PlotControl { Dock = DockStyle.Fill, Title = "Spectrum", XLabel = "frequency (MHz)", YLabel = "power (dB)", SelectRangeMode = true };
        private readonly ImageViewer _waterfall = new ImageViewer { Dock = DockStyle.Bottom, Height = 220, EmptyText = "Waterfall" };
        private RadioSpectrum _on, _off;
        private ComboBox _fmt, _axis;
        private NumericUpDown _centre, _rate, _tsys, _baseDeg, _maxBlocks;
        private ComboBox _fft;
        private TextBox _pointing, _specOut;
        private double _exLo = double.NaN, _exHi = double.NaN;
        private System.Threading.CancellationTokenSource _liveCts;
        private readonly JobSlot _iqJob = new JobSlot(); // one recording is processed at a time

        private Control BuildSpectrum()
        {
            var panel = new Panel { Dock = DockStyle.Fill };
            var side = new Card("Receiver & processing") { Dock = DockStyle.Right, Width = 360, AutoScroll = true };
            var g = new FormGrid(140);
            _fmt = g.Row("IQ format", Ui.EnumCombo(IqFormat.RtlSdrUint8));
            _centre = g.Row("Centre (MHz)", Ui.Num((decimal)RadioMath.HydrogenLineMHz, 1, 6000, 4, 0.1m));
            _rate = g.Row("Sample rate (MS/s)", Ui.Num(2.4m, 0.01m, 60, 3, 0.1m));
            _fft = g.Row("FFT size", Ui.Combo(256, 512, 1024, 2048, 4096, 8192));
            _fft.SelectedItem = 2048;
            _maxBlocks = g.Row("Max blocks", Ui.Num(20000, 10, 10000000, 0, 1000));
            _tsys = g.Row("Tsys (K)", Ui.Num(120, 10, 10000));
            _pointing = g.Row("Pointing (RA Dec)", new TextBox { Text = "" });
            _axis = g.Row("X axis", Ui.Combo("Frequency (MHz)", "Velocity LSR (km/s)"));
            _axis.SelectedIndexChanged += (s, e) => PlotSpectrum();
            _baseDeg = g.Row("Baseline degree", Ui.Num(3, 0, 8));
            Func<string, string, Action, ButtonKind, FlatButton> btn = (t, i, a, k) => { var b = new FlatButton(t, i, (s, e) => { try { a(); } catch (Exception ex) { MainForm.Instance.Toast(ex.Message, Icons.Warning); } }) { Kind = k, Width = 320 }; g.Full(b); return b; };
            _iqJob.Guard(btn("Open ON recording…", Icons.Open, () => OpenIq(true), ButtonKind.Primary),
                btn("Open OFF / reference recording…", Icons.Open, () => OpenIq(false), ButtonKind.Normal));
            btn("Live capture (rtl_tcp, 127.0.0.1:1234)", Icons.Connect, () => ToggleLive(), ButtonKind.Normal);
            btn("Use selected object as pointing", Icons.Target, () =>
            {
                var o = AppState.Selection;
                if (o != null) _pointing.Text = AstroMath.FormatHms(o.RaDeg / 15, 0, true) + " " + AstroMath.FormatDms(o.DecDeg, 0, true, true);
            }, ButtonKind.Subtle);
            btn("Analyse line (selected range = line window)", Icons.Chart, () => AnalyseLine(), ButtonKind.Normal);
            btn("Export CSV…", Icons.Save, () => ExportSpectrum(), ButtonKind.Subtle);
            g.Full(Ui.Hint("Record raw IQ data with your SDR software. With an OFF (frequency-switched or cold-sky) reference the spectrum is calibrated to antenna temperature: (ON−OFF)/OFF × Tsys. Drag across the line to exclude it from the baseline fit."));
            _specOut = new TextBox { Multiline = true, ReadOnly = true, Height = 160, Font = Theme.MonoFont, ScrollBars = ScrollBars.Vertical };
            g.Full(_specOut);
            side.Controls.Add(g);
            _specPlot.RangeSelected += (a, b) => { _exLo = Math.Min(a, b); _exHi = Math.Max(a, b); PlotSpectrum(); };
            _waterfall.FalseColor = true;
            panel.Controls.Add(_specPlot);
            panel.Controls.Add(_waterfall);
            panel.Controls.Add(side);
            return panel;
        }

        private int FftSize => Convert.ToInt32(_fft.SelectedItem);

        private void OpenIq(bool on)
        {
            if (_iqJob.Refuse()) return;
            using (var d = new OpenFileDialog { Filter = "IQ recordings|*.bin;*.iq;*.raw;*.cu8;*.cs8;*.cs16;*.cf32;*.wav|All files|*.*" })
            {
                if (d.ShowDialog() != DialogResult.OK) return;
                string path = d.FileName;
                var fmt = (IqFormat)_fmt.SelectedItem;
                string ext = Path.GetExtension(path).ToLowerInvariant();
                if (ext == ".cs8") fmt = IqFormat.HackRfInt8; else if (ext == ".cu8") fmt = IqFormat.RtlSdrUint8; else if (ext == ".cs16") fmt = IqFormat.Int16; else if (ext == ".cf32") fmt = IqFormat.Float32; else if (ext == ".wav") fmt = IqFormat.WavStereo16;
                int n = FftSize; double fc = (double)_centre.Value * 1e6, sr = (double)_rate.Value * 1e6; int maxB = (int)_maxBlocks.Value;
                var rows = new List<double[]>();
                RadioSpectrum spec = null;
                _iqJob.Run("Processing IQ recording", t =>
                {
                    int every = 1, count = 0;
                    long bytes = new FileInfo(path).Length;
                    int bps = fmt == IqFormat.RtlSdrUint8 || fmt == IqFormat.HackRfInt8 ? 2 : fmt == IqFormat.Float32 ? 8 : 4;
                    long totalBlocks = Math.Min(maxB, bytes / bps / n);
                    every = (int)Math.Max(1, totalBlocks / 400);
                    var acc = new double[n]; int accN = 0;
                    spec = IqProcessing.Spectrum(IqProcessing.ReadIq(path, fmt, n), n, fc, sr, maxB, row =>
                    {
                        for (int i = 0; i < n; i++) acc[i] += row[i];
                        if (++accN >= every) { rows.Add(acc.Select(v => v / accN).ToArray()); acc = new double[n]; accN = 0; }
                        if (++count % 500 == 0) { Tasks.Report(t, (double)count / totalBlocks); t.Cts.Token.ThrowIfCancellationRequested(); }
                    });
                }, () =>
                {
                    if (on) _on = spec; else _off = spec;
                    if (on) ShowWaterfall(rows, n);
                    _specOut.Text = string.Format("{0}: {1} averaged spectra, {2:0.000} kHz/channel\r\n", on ? "ON" : "OFF", spec.Averages, sr / n / 1000);
                    PlotSpectrum();
                });
            }
        }

        private void ShowWaterfall(List<double[]> rows, int n)
        {
            if (rows.Count == 0) return;
            var img = new AstroImage(n, rows.Count, 1);
            var d = img.Data[0];
            for (int r = 0; r < rows.Count; r++)
                for (int i = 0; i < n; i++) d[r * n + i] = (float)(10 * Math.Log10(Math.Max(1e-20, rows[r][i])));
            float min = d.Min(), max = d.Max();
            for (int i = 0; i < d.Length; i++) d[i] = (d[i] - min) / Math.Max(1e-6f, max - min);
            _waterfall.SetImage(img);
        }

        private void ToggleLive()
        {
            if (_liveCts != null) { _liveCts.Cancel(); _liveCts = null; return; }
            var cts = _liveCts = new System.Threading.CancellationTokenSource();
            int n = FftSize; double fc = (double)_centre.Value * 1e6, sr = (double)_rate.Value * 1e6;
            var rows = new List<double[]>();
            Tasks.Run("Live SDR capture", t =>
            {
                using (var rtl = new RtlTcpClient())
                {
                    rtl.Connect();
                    rtl.SetSampleRate(sr); rtl.SetFrequency(fc); rtl.SetAgc(false); rtl.SetManualGain(true); rtl.SetGain(400);
                    var acc = new double[n]; int count = 0;
                    while (!cts.IsCancellationRequested && !t.Cts.IsCancellationRequested)
                    {
                        var block = rtl.Read(n * 64, cts.Token);
                        var spec = IqProcessing.Spectrum(new[] { block }, n, fc, sr, int.MaxValue, row => { lock (rows) { rows.Add(row); if (rows.Count > 300) rows.RemoveAt(0); } });
                        count++;
                        for (int i = 0; i < n; i++) acc[i] += (spec.Power[i] - acc[i]) / Math.Min(count, 200); // running average
                        var snapshot = new RadioSpectrum { Power = acc.ToArray(), FrequencyHz = spec.FrequencyHz, CentreHz = fc, SampleRate = sr, Averages = count };
                        if (count % 10 == 0)
                            MainForm.Instance.Post(() => { _on = snapshot; PlotSpectrum(); lock (rows) ShowWaterfall(rows.ToList(), n); });
                    }
                }
            }, () => { if (_liveCts == cts) _liveCts = null; MainForm.Instance.Toast("Live capture stopped"); }, ex => { if (_liveCts == cts) _liveCts = null; MainForm.Instance.Toast("rtl_tcp: " + ex.Message + " (start rtl_tcp first)", Icons.Warning); });
        }

        private bool TryPointing(out double ra, out double dec)
        {
            ra = dec = double.NaN;
            var p = _pointing.Text.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (p.Length < 2) return false;
            // accept "hh:mm:ss ±dd:mm:ss" or decimal degrees
            string a = string.Join(" ", p.Take(p.Length / 2)), b = string.Join(" ", p.Skip(p.Length / 2));
            ra = a.Contains(":") || a.Contains("h") ? AstroMath.ParseRaHours(a) * 15 : AstroMath.ParseInvariant(a);
            dec = AstroMath.ParseSexagesimal(b);
            return !double.IsNaN(ra) && !double.IsNaN(dec);
        }

        private void GetCurve(out double[] x, out double[] y, out bool calibrated)
        {
            x = y = null; calibrated = false;
            if (_on == null) return;
            double vcorr = 0;
            double ra, dec;
            bool vel = _axis.SelectedIndex == 1;
            if (vel && TryPointing(out ra, out dec)) vcorr = VelocityFrames.LsrCorrection(AppState.Site, AppState.Now, ra, dec);
            x = vel ? _on.VelocityKms(RadioMath.HydrogenLineMHz * 1e6, vcorr) : _on.FrequencyHz.Select(f => f / 1e6).ToArray();
            if (_off != null && _off.Power.Length == _on.Power.Length) { y = IqProcessing.OnOff(_on, _off, (double)_tsys.Value); calibrated = true; }
            else y = _on.PowerDb;
            if (!double.IsNaN(_exLo) && _baseDeg.Value > 0) y = IqProcessing.Baseline(x, y, (int)_baseDeg.Value, _exLo, _exHi);
        }

        private void PlotSpectrum()
        {
            double[] x, y; bool cal;
            GetCurve(out x, out y, out cal);
            _specPlot.Clear();
            if (x == null) return;
            bool vel = _axis.SelectedIndex == 1;
            _specPlot.XLabel = vel ? "V(LSR) km/s" : "frequency (MHz)";
            _specPlot.YLabel = cal ? "T_A (K)" : "power (dB)";
            var s = _specPlot.AddSeries(cal ? "ON−OFF" : "ON");
            for (int i = 0; i < x.Length; i++) s.Add(x[i], y[i]);
            if (!vel && x[0] < RadioMath.HydrogenLineMHz && x[x.Length - 1] > RadioMath.HydrogenLineMHz) _specPlot.Markers.Add(new PlotMarker { X = RadioMath.HydrogenLineMHz, Label = "HI 1420.406" });
            if (vel) _specPlot.Markers.Add(new PlotMarker { X = 0, Label = "0 km/s" });
            if (!double.IsNaN(_exLo)) _specPlot.Bands.Add(new PlotBand { X0 = _exLo, X1 = _exHi, ThemeColor = pal => Theme.WithAlpha(pal.Accent, 40), Label = "line window" });
            _specPlot.Invalidate();
        }

        private void AnalyseLine()
        {
            double[] x, y; bool cal;
            GetCurve(out x, out y, out cal);
            if (x == null || double.IsNaN(_exLo)) { MainForm.Instance.Toast("Load a spectrum and drag across the line first", Icons.Info); return; }
            bool vel = _axis.SelectedIndex == 1;
            var idx = Enumerable.Range(0, x.Length).Where(i => x[i] >= _exLo && x[i] <= _exHi).ToList();
            if (idx.Count < 3) return;
            int peak = idx.OrderByDescending(i => y[i]).First();
            double area = 0;
            for (int k = 1; k < idx.Count; k++) area += 0.5 * (y[idx[k]] + y[idx[k - 1]]) * Math.Abs(x[idx[k]] - x[idx[k - 1]]);
            var sb = new System.Text.StringBuilder();
            sb.AppendLine(string.Format("Peak {0:0.000} at {1:0.000} {2}", y[peak], x[peak], vel ? "km/s" : "MHz"));
            sb.AppendLine(string.Format("Integrated {0:0.00} {1}", area, cal ? (vel ? "K km/s" : "K MHz") : "dB·unit"));
            double ra, dec;
            if (vel && TryPointing(out ra, out dec))
            {
                double l, b;
                Transforms.EquatorialToGalactic(ra * AstroMath.Deg2Rad, dec * AstroMath.Deg2Rad, out l, out b);
                l *= AstroMath.Rad2Deg; b *= AstroMath.Rad2Deg; if (l < 0) l += 360;
                sb.AppendLine(string.Format("Galactic l = {0:0.00}°, b = {1:0.00}°", l, b));
                if (cal) sb.AppendLine(string.Format("N(HI) ≈ {0:0.00E+0} cm⁻² (optically thin)", 1.823e18 * area));
                var dists = VelocityFrames.KinematicDistances(l, x[peak]);
                if (dists != null && dists.Length > 0) sb.AppendLine("Kinematic distance(s): " + string.Join(", ", dists.Select(v => v.ToString("0.00", CultureInfo.InvariantCulture) + " kpc")));
                if ((l < 90 || l > 270))
                {
                    double vmax = idx.Where(i => y[i] > y[peak] * 0.1).Select(i => l < 90 ? x[i] : -x[i]).Max();
                    double rr, vr; VelocityFrames.TangentPoint(l, l < 90 ? vmax : -vmax, out rr, out vr);
                    sb.AppendLine(string.Format("Tangent point: R = {0:0.00} kpc, rotation speed {1:0} km/s (rotation-curve point)", rr, vr));
                }
            }
            _specOut.AppendText(sb.ToString());
        }

        private void ExportSpectrum()
        {
            double[] x, y; bool cal;
            GetCurve(out x, out y, out cal);
            if (x == null) return;
            using (var d = new SaveFileDialog { Filter = "CSV|*.csv", FileName = "spectrum.csv" })
                if (d.ShowDialog() == DialogResult.OK)
                    Core.Util.CsvWriter.Write(d.FileName, new[] { _axis.SelectedIndex == 1 ? "vlsr_kms" : "freq_mhz", cal ? "ta_k" : "power_db" }, x.Select((v, i) => (IList<object>)new object[] { v, y[i] }));
        }

        // ================================================================== calculator

        private Control BuildCalculator()
        {
            var card = new Card("Radio telescope calculator") { Dock = DockStyle.Fill, AutoScroll = true };
            var g = new FormGrid(220);
            var f = g.Row("Frequency (MHz)", Ui.Num((decimal)RadioMath.HydrogenLineMHz, 1, 100000, 3, 1));
            var dia = g.Row("Dish diameter (m)", Ui.Num(1.5m, 0.1m, 500, 2, 0.1m));
            var eff = g.Row("Aperture efficiency", Ui.Num(0.55m, 0.05m, 1, 2, 0.05m));
            var tsys = g.Row("System temperature (K)", Ui.Num(120, 5, 20000));
            var bw = g.Row("Bandwidth (MHz)", Ui.Num(2.4m, 0.001m, 1000, 3, 0.1m));
            var tint = g.Row("Integration (s)", Ui.Num(60, 0.1m, 1000000, 1, 10));
            var yf = g.Row("Y-factor (hot/cold, dB)", Ui.Num(0, 0, 30, 2, 0.1m));
            var outp = new Label { AutoSize = true, Font = Theme.UiFont, MaximumSize = new Size(900, 0) };
            g.Full(outp);
            var grid = Ui.Grid(); grid.Dock = DockStyle.None; grid.Height = 300;
            foreach (var c in new[] { "Source", "Type", "Flux (Jy)", "T_A (K)", "SNR", "Alt now" }) grid.Columns.Add(c, c);
            g.Full(grid);
            Action upd = () =>
            {
                double hz = (double)f.Value * 1e6, D = (double)dia.Value, e = (double)eff.Value, T = (double)tsys.Value;
                if (yf.Value > 0) T = RadioMath.TsysFromYFactor(RadioMath.DbToRatio((double)yf.Value));
                double aeff = RadioMath.EffectiveArea(D, e), sens = RadioMath.RadiometerSensitivity(T, (double)bw.Value * 1e6, (double)tint.Value);
                outp.Text = string.Format("Wavelength {0:0.000} m · beam (HPBW) {1:0.00}° · gain {2:0.0} dBi · effective area {3:0.00} m²\r\nTsys {4:0} K · SEFD {5:0} Jy · radiometer ΔT = {6:0.0000} K ({7:0.0} Jy rms)",
                    RadioMath.WavelengthM(hz), RadioMath.BeamWidthDeg(hz, D), RadioMath.DishGainDbi(hz, D, e), aeff, T, RadioMath.Sefd(T, aeff), sens, sens * RadioMath.Sefd(T, aeff) / T);
                grid.Rows.Clear();
                var site = AppState.Site; var now = AppState.Now;
                foreach (var s in RadioSources.Bright)
                {
                    double S = s.FluxAt(hz), ta = RadioMath.AntennaTemperature(S, aeff);
                    double alt = Core.Ephemeris.Events.FixedAltitude(s.RaDeg * AstroMath.Deg2Rad, s.DecDeg * AstroMath.Deg2Rad, site, now);
                    grid.Rows.Add(s.Name, s.Type, S.ToString("0.0", CultureInfo.InvariantCulture), ta.ToString("0.000", CultureInfo.InvariantCulture), (ta / sens).ToString("0.0", CultureInfo.InvariantCulture), alt.ToString("0", CultureInfo.InvariantCulture) + "°");
                }
            };
            foreach (var n in new[] { f, dia, eff, tsys, bw, tint, yf }) n.ValueChanged += (s, e) => upd();
            card.Controls.Add(g);
            upd();
            return card;
        }

        // ================================================================== Jupiter

        private Control BuildJupiter()
        {
            var panel = new Panel { Dock = DockStyle.Fill };
            var days = Ui.Num(7, 1, 60);
            var list = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, Font = Theme.MonoFont, ScrollBars = ScrollBars.Vertical };
            var now = new Label { Dock = DockStyle.Top, Height = 46, Font = Theme.HeaderFont };
            var go = new FlatButton("Predict storms", Icons.Calendar, (s, e) =>
            {
                List<string> res = null;
                int d = (int)days.Value;
                Tasks.Run("Jupiter radio prediction", t => res = JupiterRadio.Predict(AppState.Site, AppState.Now, d), () =>
                    list.Text = res.Count == 0 ? "No Io-related source windows with Jupiter up at night." : "Probable Io-A/B/C/D storm windows (Jupiter > 5°, Sun below horizon), UTC:\r\n\r\n" + string.Join("\r\n", res));
            }) { Kind = ButtonKind.Primary };
            go.AutoSizeToText();
            var bar = Ui.Toolbar(new Label { Text = "Days", AutoSize = true, Margin = new Padding(0, 6, 4, 0) }, days, go);
            var timer = new Timer { Interval = 5000 };
            Action upd = () =>
            {
                double ph, cml; JupiterRadio.IoPhaseAndCml(AppState.Now.JdTT, out ph, out cml);
                now.Text = string.Format("Now: CML(III) {0:0.0}°  ·  Io phase {1:0.0}°  ·  active: {2}", cml, ph, JupiterRadio.ActiveSources(cml, ph));
            };
            timer.Tick += (s, e) => { if (Visible) upd(); };
            timer.Start();
            upd();
            panel.Controls.Add(list);
            panel.Controls.Add(new Label { Dock = DockStyle.Top, Height = 40, Tag = "dim", Text = "Decametric emission (≈18–40 MHz) is most likely when the System III central meridian longitude and Io's orbital phase fall in the classic Io-A, B, C and D regions (Carr et al. source map). A dipole array tuned to 20.1 MHz (Radio JOVE) is enough." });
            panel.Controls.Add(now);
            panel.Controls.Add(bar);
            return panel;
        }

        // ================================================================== time domain

        private Control BuildTimeDomain()
        {
            var panel = new Panel { Dock = DockStyle.Fill };
            var plot = new PlotControl { Dock = DockStyle.Fill, Title = "Time series", XLabel = "time (s)", YLabel = "power" };
            var outp = new TextBox { Dock = DockStyle.Bottom, Height = 110, Multiline = true, ReadOnly = true, Font = Theme.MonoFont };
            var t = new List<double>(); var y = new List<double>();
            var period = Ui.Num(0.714519m, 0.001m, 100000, 6, 0.001m);
            var dec = Ui.Num(0, -90, 90, 2, 1);
            Action plotRaw = () => { plot.Clear(); plot.XLabel = "time (s)"; var s = plot.AddSeries("data"); for (int i = 0; i < t.Count; i++) s.Add(t[i], y[i]); plot.Invalidate(); };
            var open = new FlatButton("Open time series…", Icons.Open, (s, e) =>
            {
                using (var d = new OpenFileDialog { Filter = "CSV / text (time, value)|*.csv;*.txt;*.dat|All files|*.*" })
                {
                    if (d.ShowDialog() != DialogResult.OK) return;
                    t.Clear(); y.Clear();
                    foreach (var line in File.ReadLines(d.FileName))
                    {
                        var p = line.Split(new[] { ',', ';', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        if (p.Length < 2) continue;
                        double a = AstroMath.ParseInvariant(p[0]), b = AstroMath.ParseInvariant(p[1]);
                        if (!double.IsNaN(a) && !double.IsNaN(b)) { t.Add(a); y.Add(b); }
                    }
                    outp.Text = t.Count + " samples loaded";
                    plotRaw();
                }
            });
            open.AutoSizeToText();
            var drift = new FlatButton("Fit drift scan", Icons.Chart, (s, e) =>
            {
                if (t.Count < 10) return;
                var r = TimeDomainRadio.FitDriftScan(t, y);
                plotRaw();
                plot.Markers.Add(new PlotMarker { X = r[0], Label = "transit" });
                outp.Text = string.Format("Transit at t = {0:0.0} s · FWHM {1:0.0} s → beam {2:0.00}° at δ {3:0}° · peak {4:0.000}", r[0], r[1], TimeDomainRadio.BeamFromDrift(r[1], (double)dec.Value), dec.Value, r[2]);
            });
            drift.AutoSizeToText();
            var fold = new FlatButton("Fold at period", Icons.Refresh, (s, e) =>
            {
                if (t.Count < 10) return;
                var prof = TimeDomainRadio.Fold(t, y, (double)period.Value, 64);
                plot.Clear(); plot.XLabel = "phase";
                var ser = plot.AddSeries("profile", SeriesStyle.Step);
                for (int i = 0; i < prof.Length; i++) ser.Add((i + 0.5) / prof.Length, prof[i]);
                double mx = prof.Max(), md = Stats.Median(prof.ToList()), sd = Stats.Mad(prof.ToList(), md) * 1.4826;
                outp.Text = string.Format("Folded at {0} s: peak S/N ≈ {1:0.0}", period.Value, sd > 0 ? (mx - md) / sd : double.NaN);
                plot.Invalidate();
            });
            fold.AutoSizeToText();
            var sid = new FlatButton("Detect SIDs", Icons.Warning, (s, e) =>
            {
                if (t.Count < 100) return;
                var ev = TimeDomainRadio.DetectSid(y);
                plotRaw();
                foreach (var i in ev) plot.Markers.Add(new PlotMarker { X = t[i], ThemeColor = pal => pal.Warning });
                outp.Text = ev.Count + " sudden ionospheric disturbance candidates" + (ev.Count > 0 ? ": " + string.Join(", ", ev.Take(20).Select(i => t[i].ToString("0", CultureInfo.InvariantCulture))) : "");
                plot.Invalidate();
            });
            sid.AutoSizeToText();
            var dm = Ui.Num(26.76m, 0, 3000, 2, 1);
            var dmBtn = new FlatButton("DM delay", Icons.Clock, (s, e) =>
                outp.Text = string.Format("Dispersion delay for DM {0}: {1:0.000} s between {2} and {3} MHz", dm.Value, TimeDomainRadio.DispersionDelay((double)dm.Value, 408, 1420), 408, 1420));
            dmBtn.AutoSizeToText();
            var bar = Ui.Toolbar(open, Ui.Separator(), new Label { Text = "δ", AutoSize = true, Margin = new Padding(0, 6, 2, 0) }, dec, drift, Ui.Separator(),
                new Label { Text = "P (s)", AutoSize = true, Margin = new Padding(0, 6, 2, 0) }, period, fold, Ui.Separator(), sid, Ui.Separator(),
                new Label { Text = "DM", AutoSize = true, Margin = new Padding(0, 6, 2, 0) }, dm, dmBtn);
            panel.Controls.Add(plot);
            panel.Controls.Add(outp);
            panel.Controls.Add(bar);
            return panel;
        }

        public IEnumerable<AppCommand> GetCommands()
        {
            yield return new AppCommand { Title = "Radio: hydrogen line spectrum", Icon = Icons.Radio, Run = () => { MainForm.Instance.Navigate("radio"); _tabs.Select(0); } };
            yield return new AppCommand { Title = "Radio: telescope calculator", Run = () => { MainForm.Instance.Navigate("radio"); _tabs.Select(1); } };
            yield return new AppCommand { Title = "Radio: Jupiter storm predictions", Run = () => { MainForm.Instance.Navigate("radio"); _tabs.Select(2); } };
        }
    }
}
