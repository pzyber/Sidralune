using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Sidralune.Core;
using Sidralune.Core.Mathematics;
using Sidralune.Core.Time;
using Sidralune.Imaging;
using Sidralune.Imaging.IO;
using Sidralune.Science.Spectroscopy;
using Sidralune.UI;

namespace Sidralune.Views
{
    /// <summary>
    /// Spectroscopy: 2D spectrum reduction (trace, optimal extraction, lamp wavelength calibration) and 1D analysis
    /// (normalisation, line measurement, radial velocity, instrument response, temperature, spectral class, BeSS export).
    /// </summary>
    public class SpectroscopyView : UserControl, ICommandSource
    {
        private readonly TabbedPanel _tabs = new TabbedPanel { Dock = DockStyle.Fill, Padding = new Padding(10) };

        public SpectroscopyView()
        {
            Controls.Add(_tabs);
            _tabs.AddPage("Reduce 2D spectrum", BuildReduce());
            _tabs.AddPage("Analyse spectrum", BuildAnalyse());
        }

        // ================================================================== reduction

        private readonly ImageViewer _viewer = new ImageViewer { Dock = DockStyle.Fill, EmptyText = "Open a 2D spectrum image (dispersion along X). Dark/flat calibrate in Stacking first if possible." };
        private AstroImage _sci, _lamp;
        private double[] _trace;
        private Spectrum _extracted, _lampSpec;
        private double[] _dispersion;
        private TextBox _redOut;
        private PlotControl _redPlot;

        private Control BuildReduce()
        {
            var panel = new Panel { Dock = DockStyle.Fill };
            var side = new Card("Reduction") { Dock = DockStyle.Right, Width = 360, AutoScroll = true };
            var g = new FormGrid(140);
            var half = g.Row("Aperture half-width", Ui.Num(5, 1, 50, 1, 0.5m));
            var gap = g.Row("Sky gap / width", Ui.Num(4, 0, 50));
            var skyW = g.Row("", Ui.Num(8, 1, 80));
            var optimal = g.Row("Optimal extraction", new CheckBox { Checked = true });
            var lampList = g.Row("Lamp", Ui.Combo(LineLists.Lamps.Keys.Cast<object>().ToArray()));
            var start = g.Row("Approx. start (Å)", Ui.Num(3900, 1000, 20000, 1, 10));
            var disp = g.Row("Approx. Å / pixel", Ui.Num(2.5m, 0.001m, 100, 4, 0.05m));
            var degree = g.Row("Polynomial degree", Ui.Num(3, 1, 6));
            Action<string, string, Action, ButtonKind> btn = (t, i, a, k) => { var b = new FlatButton(t, i, (s, e) => { try { a(); } catch (Exception ex) { MainForm.Instance.Toast(ex.Message, Icons.Warning); } }) { Kind = k, Width = 320 }; g.Full(b); };
            btn("1 · Open science frame…", Icons.Open, () => { var img = OpenImage(); if (img != null) { _sci = img; _trace = null; _viewer.SetImage(img); } }, ButtonKind.Normal);
            btn("2 · Trace & extract", Icons.Spectrum, () =>
            {
                if (_sci == null) throw new InvalidOperationException("Open a science frame first");
                _trace = SpectroTools.FindTrace(_sci);
                _extracted = SpectroTools.Extract(_sci, _trace, (double)half.Value, (double)gap.Value, (double)skyW.Value, optimal.Checked, double.IsNaN(_sci.Gain) ? 1 : _sci.Gain);
                _extracted.Name = _sci.ObjectName ?? Path.GetFileNameWithoutExtension(_sci.SourcePath ?? "spectrum");
                _extracted.Header = _sci.Header.Clone();
                if (_dispersion != null) SpectroTools.ApplyDispersion(_extracted, _dispersion);
                _viewer.Invalidate();
                PlotReduced();
            }, ButtonKind.Normal);
            btn("3 · Open lamp frame…", Icons.Open, () => { var img = OpenImage(); if (img != null) { _lamp = img; MainForm.Instance.Toast("Lamp frame loaded"); } }, ButtonKind.Normal);
            btn("4 · Auto wavelength calibration", Icons.Target, () =>
            {
                if (_lamp == null || _trace == null) throw new InvalidOperationException("Extract the science spectrum and open a lamp frame first");
                _lampSpec = SpectroTools.Extract(_lamp, _trace, (double)half.Value, (double)gap.Value, (double)skyW.Value, false);
                double rms; List<double[]> ids;
                _dispersion = SpectroTools.AutoCalibrate(_lampSpec.Flux, LineLists.Lamps[(string)lampList.SelectedItem], (double)start.Value, (double)disp.Value, (int)degree.Value, out rms, out ids);
                if (_dispersion == null) throw new InvalidOperationException("Could not identify enough lamp lines; check the approximate start and dispersion");
                SpectroTools.ApplyDispersion(_extracted, _dispersion);
                SpectroTools.ApplyDispersion(_lampSpec, _dispersion);
                _redOut.Text = string.Format("Dispersion solution (degree {0}), {1} lines, rms {2:0.000} Å\r\n", _dispersion.Length - 1, ids.Count, rms)
                    + string.Join("\r\n", ids.Select(p => string.Format("  px {0,8:0.00} → {1,9:0.00} Å", p[0], p[1])));
                PlotReduced();
            }, ButtonKind.Primary);
            btn("Linear calibration from start/dispersion", Icons.Chart, () =>
            {
                if (_extracted == null) throw new InvalidOperationException("Extract a spectrum first");
                _dispersion = new[] { (double)start.Value, (double)disp.Value };
                SpectroTools.ApplyDispersion(_extracted, _dispersion);
                PlotReduced();
            }, ButtonKind.Subtle);
            btn("Send to analysis →", Icons.Right, () => { if (_extracted != null) { LoadSpectrum(_extracted.Clone()); _tabs.Select(1); } }, ButtonKind.Normal);
            _redOut = new TextBox { Multiline = true, ReadOnly = true, Height = 160, Font = Theme.MonoFont, ScrollBars = ScrollBars.Vertical };
            g.Full(_redOut);
            side.Controls.Add(g);
            _viewer.PaintOverlay += (gr, map) =>
            {
                if (_trace == null || _sci == null) return;
                var pts = new List<PointF>();
                for (int x = 0; x < _sci.Width; x += 4) pts.Add(map(x, Poly(_trace, x)));
                if (pts.Count > 1) using (var pen = new Pen(Theme.Current.Success, 1.5f)) gr.DrawLines(pen, pts.ToArray());
            };
            _redPlot = new PlotControl { Dock = DockStyle.Bottom, Height = 240, Title = "Extracted spectrum", XLabel = "pixel", YLabel = "flux" };
            panel.Controls.Add(_viewer);
            panel.Controls.Add(_redPlot);
            panel.Controls.Add(side);
            return panel;
        }

        private static double Poly(double[] c, double x) { double v = 0; for (int i = c.Length - 1; i >= 0; i--) v = v * x + c[i]; return v; }

        private static AstroImage OpenImage()
        {
            using (var d = new OpenFileDialog { Filter = ImageFile.OpenFilter })
            {
                if (d.ShowDialog() != DialogResult.OK) return null;
                var img = ImageFile.Load(d.FileName);
                if (img.Channels > 1) img = img.ToMono();
                return img;
            }
        }

        private void PlotReduced()
        {
            _redPlot.Clear();
            bool cal = _dispersion != null;
            _redPlot.XLabel = cal ? "wavelength (Å)" : "pixel";
            if (_extracted != null) { var s = _redPlot.AddSeries(_extracted.Name ?? "science"); for (int i = 0; i < _extracted.Length; i++) s.Add(_extracted.Wavelength[i], _extracted.Flux[i]); }
            if (_lampSpec != null && cal)
            {
                double scale = _extracted != null ? _extracted.Flux.Max() / Math.Max(1e-9, _lampSpec.Flux.Max()) : 1;
                var l = _redPlot.AddSeries("lamp", SeriesStyle.Line, Theme.Current.TextDim);
                for (int i = 0; i < _lampSpec.Length; i++) l.Add(_lampSpec.Wavelength[i], _lampSpec.Flux[i] * scale);
            }
            _redPlot.Invalidate();
        }

        // ================================================================== analysis

        private Spectrum _spec, _shown;
        private readonly PlotControl _plot = new PlotControl { Dock = DockStyle.Fill, Title = "Spectrum", XLabel = "wavelength (Å)", YLabel = "flux", SelectRangeMode = true };
        private TextBox _out;
        private ComboBox _lines;
        private double _selA = double.NaN, _selB = double.NaN;
        private readonly List<LineMeasurement> _measured = new List<LineMeasurement>();

        private Control BuildAnalyse()
        {
            var panel = new Panel { Dock = DockStyle.Fill };
            var side = new Card("Analysis") { Dock = DockStyle.Right, Width = 360, AutoScroll = true };
            var g = new FormGrid(120);
            _lines = g.Row("Line list", Ui.Combo("Stellar", "Nebular", "Telluric", "None"));
            _lines.SelectedIndexChanged += (s, e) => PlotSpectrum();
            var rest = g.Row("Rest λ for RV (Å)", new TextBox { Text = "6562.81" });
            var teff = g.Row("Reference Teff (K)", Ui.Num(9500, 2000, 50000, 0, 100));
            Action<string, string, Action, ButtonKind> btn = (t, i, a, k) => { var b = new FlatButton(t, i, (s, e) => { try { a(); } catch (Exception ex) { MainForm.Instance.Toast(ex.Message, Icons.Warning); } }) { Kind = k, Width = 320 }; g.Full(b); };
            btn("Open spectrum (FITS / text)…", Icons.Open, () =>
            {
                using (var d = new OpenFileDialog { Filter = "Spectra|*.fit;*.fits;*.fts;*.txt;*.dat;*.csv|All files|*.*" })
                    if (d.ShowDialog() == DialogResult.OK) LoadSpectrum(Spectrum.Load(d.FileName));
            }, ButtonKind.Normal);
            btn("Normalise to continuum", Icons.Chart, () => { Need(); _shown = SpectroTools.Normalize(_shown); PlotSpectrum(); }, ButtonKind.Normal);
            btn("Measure selected line", Icons.Target, () =>
            {
                Need();
                if (double.IsNaN(_selA)) throw new InvalidOperationException("Drag across a line in the plot first");
                double rw = AstroMath.ParseInvariant(rest.Text);
                var near = AllLines().OrderBy(l => Math.Abs(l.Wavelength - (_selA + _selB) / 2)).FirstOrDefault();
                if (double.IsNaN(rw) && near != null) rw = near.Wavelength;
                var m = SpectroTools.MeasureLine(_shown, _selA, _selB, rw, near != null && Math.Abs(near.Wavelength - rw) < 1 ? near.Name : null);
                _measured.Add(m);
                _out.AppendText(string.Format("{0}: λ {1:0.00} Å  FWHM {2:0.00} Å  EW {3:0.000} Å  depth {4:0.000}  RV {5:0.0} km/s{6}\r\n",
                    m.Name ?? "line", m.Centre, m.Fwhm, m.EquivalentWidth, m.Depth, m.RadialVelocity, m.Emission ? " (emission)" : ""));
                PlotSpectrum();
            }, ButtonKind.Primary);
            btn("Barycentric correction", Icons.Clock, () =>
            {
                Need();
                var h = _spec.Header;
                double ra = h.GetDouble("RA"), dec = h.GetDouble("DEC");
                if (double.IsNaN(ra) && AppState.Selection != null) { ra = AppState.Selection.RaDeg; dec = AppState.Selection.DecDeg; }
                var ds = h.GetString("DATE-OBS");
                DateTime dt;
                if (double.IsNaN(ra) || ds == null || !DateTime.TryParse(ds, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out dt))
                    throw new InvalidOperationException("Needs DATE-OBS and RA/DEC in the header (or a selected object)");
                double v = SpectroTools.BarycentricCorrection(AstroTime.FromDateTime(dt).JdUtc, ra, dec);
                _out.AppendText(string.Format("Barycentric correction: {0:+0.00;-0.00} km/s (add to measured RVs)\r\n", v));
            }, ButtonKind.Normal);
            btn("Instrument response from reference star", Icons.Layers, () =>
            {
                Need();
                // reference: blackbody at the given temperature, or a loaded library spectrum
                Spectrum reference;
                if (MessageBox.Show("Use a reference spectrum file? (No = blackbody at the reference Teff)", "Instrument response", MessageBoxButtons.YesNo) == DialogResult.Yes)
                {
                    using (var d = new OpenFileDialog { Filter = "Spectra|*.fit;*.fits;*.txt;*.dat|All files|*.*" })
                    {
                        if (d.ShowDialog() != DialogResult.OK) return;
                        reference = Spectrum.Load(d.FileName);
                    }
                }
                else
                {
                    reference = new Spectrum(_spec.Wavelength.ToArray(), _spec.Wavelength.Select(w => SpectroTools.Planck(w, (double)teff.Value)).ToArray());
                }
                _response = SpectroTools.InstrumentResponse(_spec, reference);
                _out.AppendText("Instrument response computed; it is applied to spectra opened next (and to the current one now).\r\n");
                _shown = SpectroTools.Divide(_spec, _response);
                PlotSpectrum();
            }, ButtonKind.Normal);
            btn("Temperature & spectral class", Icons.Star, () =>
            {
                Need();
                double scale;
                double T = SpectroTools.BlackbodyTemperature(_shown, out scale);
                string cls = SpectroTools.EstimateSpectralClass(SpectroTools.Normalize(_shown));
                int peak = Array.IndexOf(_shown.Flux, _shown.Flux.Max());
                _out.AppendText(string.Format("Blackbody fit T ≈ {0:0} K (needs a response-corrected spectrum) · Wien peak {1:0} Å → {2:0} K · class ≈ {3}\r\n", T, _shown.Wavelength[peak], SpectroTools.WienTemperature(_shown.Wavelength[peak]), cls));
            }, ButtonKind.Normal);
            btn("Cross-correlation RV vs template…", Icons.Refresh, () =>
            {
                Need();
                using (var d = new OpenFileDialog { Filter = "Spectra|*.fit;*.fits;*.txt;*.dat|All files|*.*" })
                {
                    if (d.ShowDialog() != DialogResult.OK) return;
                    double rv = SpectroTools.CrossCorrelationRv(SpectroTools.Normalize(_shown), SpectroTools.Normalize(Spectrum.Load(d.FileName)));
                    _out.AppendText(string.Format("Cross-correlation RV: {0:0.0} km/s\r\n", rv));
                }
            }, ButtonKind.Normal);
            btn("Reset to original", Icons.Undo, () => { Need(); _shown = _response != null ? SpectroTools.Divide(_spec, _response) : _spec.Clone(); _measured.Clear(); PlotSpectrum(); }, ButtonKind.Subtle);
            btn("Save (BeSS FITS / text)…", Icons.Save, () =>
            {
                Need();
                using (var d = new SaveFileDialog { Filter = "BeSS FITS|*.fits|Text (λ, flux)|*.txt", FileName = (_shown.Name ?? "spectrum") + ".fits" })
                {
                    if (d.ShowDialog() != DialogResult.OK) return;
                    if (d.FilterIndex == 2) _shown.SaveText(d.FileName);
                    else _shown.SaveBessFits(d.FileName, _shown.Name ?? "unknown", Environment.UserName, AppState.ImagingRig.Name, AppState.Site.Name);
                }
            }, ButtonKind.Normal);
            g.Full(Ui.Hint("Drag across the plot to select a wavelength range. Line markers show the chosen list; RV is measured relative to the rest wavelength (empty = nearest listed line)."));
            _out = new TextBox { Multiline = true, ReadOnly = true, Height = 180, Font = Theme.MonoFont, ScrollBars = ScrollBars.Vertical };
            g.Full(_out);
            side.Controls.Add(g);
            _plot.RangeSelected += (a, b) => { _selA = Math.Min(a, b); _selB = Math.Max(a, b); PlotSpectrum(); };
            panel.Controls.Add(_plot);
            panel.Controls.Add(side);
            return panel;
        }

        private Spectrum _response;

        private void Need() { if (_shown == null) throw new InvalidOperationException("Open a spectrum first"); }

        private void LoadSpectrum(Spectrum s)
        {
            _spec = s;
            _shown = _response != null ? SpectroTools.Divide(s, _response) : s.Clone();
            _measured.Clear(); _selA = _selB = double.NaN;
            _out.Text = string.Format("{0}: {1} points, {2:0.0}–{3:0.0} Å, {4:0.000} Å/px\r\n", s.Name, s.Length, s.Wavelength[0], s.Wavelength[s.Length - 1], s.Dispersion);
            PlotSpectrum();
        }

        private IEnumerable<SpectralLine> AllLines()
        {
            switch (_lines.SelectedIndex)
            {
                case 0: return LineLists.Stellar;
                case 1: return LineLists.Nebular;
                case 2: return LineLists.Telluric;
                default: return Enumerable.Empty<SpectralLine>();
            }
        }

        private void PlotSpectrum()
        {
            _plot.Clear();
            if (_shown == null) return;
            _plot.Title = _shown.Name ?? "Spectrum";
            var s = _plot.AddSeries(_shown.Name ?? "spectrum");
            for (int i = 0; i < _shown.Length; i++) s.Add(_shown.Wavelength[i], _shown.Flux[i]);
            double w0 = _shown.Wavelength[0], w1 = _shown.Wavelength[_shown.Length - 1];
            foreach (var l in AllLines().Where(l => l.Wavelength > w0 && l.Wavelength < w1)) _plot.Markers.Add(new PlotMarker { X = l.Wavelength, Label = l.Name, Color = Theme.Current.TextDim });
            foreach (var m in _measured) _plot.Markers.Add(new PlotMarker { X = m.Centre, Label = string.Format("{0:0.0}", m.Centre), Color = Theme.Current.Warning });
            if (!double.IsNaN(_selA)) _plot.Bands.Add(new PlotBand { X0 = _selA, X1 = _selB, Color = Theme.WithAlpha(Theme.Current.Accent, 50) });
            _plot.Invalidate();
        }

        public IEnumerable<AppCommand> GetCommands()
        {
            yield return new AppCommand { Title = "Spectroscopy: reduce 2D spectrum", Icon = Icons.Spectrum, Run = () => { MainForm.Instance.Navigate("spectro"); _tabs.Select(0); } };
            yield return new AppCommand { Title = "Spectroscopy: analyse 1D spectrum", Run = () => { MainForm.Instance.Navigate("spectro"); _tabs.Select(1); } };
        }
    }
}
