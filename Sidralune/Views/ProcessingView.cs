using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Sidralune.Core.Mathematics;
using Sidralune.Imaging;
using Sidralune.Imaging.Analysis;
using Sidralune.Imaging.IO;
using Sidralune.UI;
using static Sidralune.Core.Util.Lang;

namespace Sidralune.Views
{
    /// <summary>Post-processing workspace with non-destructive history.</summary>
    public class ProcessingView : UserControl, ICommandSource
    {
        private readonly ImageViewer _viewer = new ImageViewer { Dock = DockStyle.Fill };
        private readonly Panel _tools = new Panel { Dock = DockStyle.Right, Width = 360, AutoScroll = true };
        private readonly Label _readout = new Label { Dock = DockStyle.Bottom, Height = 22, Tag = "dim" };
        private readonly ListBox _history = new ListBox { Height = 120 };
        private readonly List<Tuple<string, AstroImage>> _undo = new List<Tuple<string, AstroImage>>();
        private readonly List<Tuple<string, AstroImage>> _redo = new List<Tuple<string, AstroImage>>();
        private AstroImage _img;
        private string _name = "";
        private List<BackgroundSample> _samples;
        private readonly PlotControl _hist = new PlotControl { Height = 130, ShowLegend = false };
        // one edit at a time, so a slow step cannot land on an image opened or changed meanwhile; histograms keep the newest
        // every step works on a copy of the image, so stopping one leaves the image as it was
        private readonly JobSlot _job = new JobSlot(), _histJob = new JobSlot();

        public ProcessingView()
        {
            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 44, Padding = new Padding(4, 6, 4, 0), WrapContents = false };
            Func<string, string, string, Action, FlatButton> btn = (t, i, tip, a) => { var b = new FlatButton(t, i, (s, e) => a()) { ToolTipText = tip }; if (t == null) b.Width = 34; else b.AutoSizeToText(); bar.Controls.Add(b); return b; };
            var open = btn(T("Open"), Icons.Open, T("Open image (Ctrl+O)"), Open);
            var saveAs = btn(T("Save as"), Icons.Save, T("Save image"), SaveAs);
            bar.Controls.Add(Ui.Separator());
            var undo = btn(null, Icons.Undo, T("Undo (Ctrl+Z)"), Undo);
            var redo = btn(null, Icons.Redo, T("Redo (Ctrl+Y)"), Redo);
            bar.Controls.Add(Ui.Separator());
            var stretch = Ui.StretchCombo(DisplayStretch.AutoLinked, v => _viewer.Stretch = v);
            stretch.Width = 130;
            bar.Controls.Add(new Label { Text = T("Display"), AutoSize = true, Margin = new Padding(4, 8, 4, 0), Tag = "dim" });
            bar.Controls.Add(stretch);
            var inv = new FlatButton { Text = T("Invert"), Toggle = true }; inv.AutoSizeToText();
            inv.CheckedChanged += (s, e) => { _viewer.Invert = inv.Checked; _viewer.Rebuild(); };
            bar.Controls.Add(inv);
            var fc = new FlatButton { Text = T("False colour"), Toggle = true }; fc.AutoSizeToText();
            fc.CheckedChanged += (s, e) => { _viewer.FalseColor = fc.Checked; _viewer.Rebuild(); };
            bar.Controls.Add(fc);
            var proof = new FlatButton { Text = T("Proof colours"), Toggle = true, ToolTipText = T("Show the image as it will look in the proofing profile chosen in Settings → Colour (Ctrl+Shift+O)") }; proof.AutoSizeToText();
            proof.CheckedChanged += (s, e) => _viewer.Proof = proof.Checked;
            bar.Controls.Add(proof);
            var gamut = new FlatButton { Text = T("Gamut warning"), Toggle = true, ToolTipText = T("Mark the colours the monitor cannot show, or the proofing profile while proofing (Ctrl+Shift+Y)") }; gamut.AutoSizeToText();
            gamut.CheckedChanged += (s, e) => _viewer.GamutWarning = gamut.Checked;
            bar.Controls.Add(gamut);
            _viewer.ProofingChanged += () => { proof.Checked = _viewer.Proof; gamut.Checked = _viewer.GamutWarning; };
            var grid = new FlatButton { Text = T("WCS grid"), Toggle = true }; grid.AutoSizeToText();
            grid.CheckedChanged += (s, e) => { _viewer.ShowWcsGrid = grid.Checked; _viewer.Invalidate(); };
            bar.Controls.Add(grid);
            btn(null, Icons.Fit, T("Fit to window (0)"), () => _viewer.FitToWindow());
            btn("1:1", null, T("Actual pixels (1)"), () => _viewer.SetZoom(1));
            btn(T("Stats"), Icons.Info, T("Statistics and FITS header"), ShowInfo);
            var solve = btn(T("Solve"), Icons.Target, T("Plate solve the image, with the solver settings chosen in Astrometry"), Solve);
            bar.Controls.Add(_job.StopButton()); // ends whatever runs; the image stays as it was
            _viewer.ReadoutChanged += s => _readout.Text = s;
            _viewer.FilesDropped += f => { if (f.Length > 0) OpenFile(f[0]); };
            _viewer.PaintOverlay += (g, map) =>
            {
                if (_samples == null) return;
                using (var pen = new Pen(Theme.Current.Warning, 1.5f))
                    foreach (var s in _samples) { var p = map(s.X, s.Y); g.DrawRectangle(pen, p.X - 5, p.Y - 5, 10, 10); }
            };
            BuildTools();
            // while an edit runs the tool buttons and Open, Save as, Undo, Redo and Solve wait; the tool settings stay editable,
            // as each tool reads them when it is clicked
            _job.Guard(new Control[] { open, saveAs, undo, redo, solve }.Concat(Descendants(_tools).OfType<FlatButton>()).ToArray());
            Controls.Add(_viewer);
            Controls.Add(_readout);
            Controls.Add(_tools);
            Controls.Add(bar);
        }

        private static IEnumerable<Control> Descendants(Control root)
        {
            foreach (Control c in root.Controls)
            {
                yield return c;
                foreach (var d in Descendants(c)) yield return d;
            }
        }

        // ------------------------------------------------------------------ document

        /// <summary>Opens an image sent from elsewhere; an edit still running is dropped, as it was made for the old image.</summary>
        public void OpenImage(AstroImage img, string name)
        {
            if (img == null) return;
            _job.Cancel();
            if (img.Channels == 1 && img.Bayer != BayerPattern.None) img = Debayer.Apply(img, DebayerMethod.EdgeDirected);
            ColorUi.Opened(img);
            _img = img.Clone();
            _name = name;
            _undo.Clear(); _redo.Clear();
            _samples = null;
            Push(T("Open {0}", name), false);
            _viewer.SetImage(_img);
            UpdateHistogram();
        }

        private void Open()
        {
            if (_job.Refuse()) return;
            using (var d = new OpenFileDialog { Filter = ImageFile.OpenFilter })
                if (d.ShowDialog() == DialogResult.OK) OpenFile(d.FileName);
        }

        public void OpenFile(string path)
        {
            AstroImage img = null;
            _job.Run(T("Opening {0}", Path.GetFileName(path)), t => img = ImageFile.Load(path), () => OpenImage(img, Path.GetFileName(path)));
        }

        /// <summary>
        /// Plate solves the image here, as this workspace's job, with the solver settings chosen in Astrometry and the hints in
        /// the image's header; a solution is added to the image as a history step.
        /// </summary>
        private void Solve()
        {
            if (_img == null) { MainForm.Instance.Toast(T("Open an image first"), Icons.Info); return; }
            if (_job.Refuse()) return;
            var src = _img;
            var solve = MainForm.Instance.GetView<AstrometryView>("astrometry").SolveWithSettings(src);
            Science.Astrometry.SolveResult res = null;
            _job.Run(T("Plate solving"), t => res = solve(t), () => AddSolution(src, res));
        }

        /// <summary>Adds a plate solution to the image it was found for, or says what to try when there is none.</summary>
        private void AddSolution(AstroImage solved, Science.Astrometry.SolveResult res)
        {
            if (res == null || !res.Success)
            {
                MainForm.Instance.Toast(T("No plate solution ({0}). Try other hints or another solver in Astrometry → Plate solving (save the image and open it there).", res?.Message ?? T("no result")),
                    Icons.Warning, Theme.Current.Warning);
                return;
            }
            if (_img != solved) return; // another image was opened meanwhile
            var img = _img.Clone();
            res.Wcs.WriteToHeader(img.Header);
            _img = img;
            Push(T("Plate solve"));
            _viewer.SetImage(_img, false);
            MainForm.Instance.Toast(T("Plate solved: {0}", res.Wcs), Icons.Check, Theme.Current.Success);
        }

        private void SaveAs()
        {
            if (_img == null) return;
            using (var d = new SaveFileDialog { Filter = ImageFile.SaveFilter, FileName = Path.GetFileNameWithoutExtension(_name) + "_processed.fits" })
            {
                if (d.ShowDialog() != DialogResult.OK) return;
                var fmt = ImageFile.FormatFromFilterIndex(d.FilterIndex);
                Func<int, float, float> tf = null;
                if ((fmt == ImageSaveFormat.Png8 || fmt == ImageSaveFormat.Jpeg8) && _viewer.Stretch != DisplayStretch.None)
                {
                    var st = StretchOps.AutoStretch(_img, _viewer.Stretch != DisplayStretch.AutoUnlinked);
                    tf = (c, v) => (float)st[Math.Min(c, st.Length - 1)].Apply(v);
                }
                ImageFile.Save(d.FileName, _img, fmt, tf);
                MainForm.Instance.Toast(T("Saved {0}", Path.GetFileName(d.FileName)));
            }
        }

        private void Push(string label, bool addToUndo = true)
        {
            if (addToUndo) { }
            _undo.Add(Tuple.Create(label, _img.Clone()));
            // memory guard: keep at most 12 states
            while (_undo.Count > 12) _undo.RemoveAt(1);
            _redo.Clear();
            RefreshHistory();
        }

        private void RefreshHistory()
        {
            _history.Items.Clear();
            foreach (var u in _undo) _history.Items.Add(u.Item1);
            _history.SelectedIndex = _history.Items.Count - 1;
        }

        private void Undo()
        {
            if (_undo.Count < 2 || _job.Refuse()) return;
            _redo.Add(_undo[_undo.Count - 1]);
            _undo.RemoveAt(_undo.Count - 1);
            _img = _undo[_undo.Count - 1].Item2.Clone();
            _viewer.SetImage(_img, false); RefreshHistory(); UpdateHistogram();
        }

        private void Redo()
        {
            if (_redo.Count == 0 || _job.Refuse()) return;
            var r = _redo[_redo.Count - 1];
            _redo.RemoveAt(_redo.Count - 1);
            _undo.Add(r);
            _img = r.Item2.Clone();
            _viewer.SetImage(_img, false); RefreshHistory(); UpdateHistogram();
        }

        /// <summary>Run a processing step in the background and record it in the history.</summary>
        private void Apply(string label, Action<AstroImage> op) => ApplyStoppable(label, (img, ct) => op(img));

        /// <summary>As Apply, for a step that can be stopped part way: op gets the job's cancellation token.</summary>
        private void ApplyStoppable(string label, Action<AstroImage, System.Threading.CancellationToken> op)
        {
            if (_img == null) { MainForm.Instance.Toast(T("Open an image first"), Icons.Info); return; }
            var work = _img.Clone();
            _job.Run(label, t => op(work, t.Cts.Token), () =>
            {
                _img = work;
                Push(label);
                _viewer.SetImage(_img, false);
                UpdateHistogram();
            });
        }

        /// <summary>Assign a colour profile to the image (the values stay) or convert it to one (the colours stay).</summary>
        public void ProfileStep(bool convert)
        {
            if (_img == null) { MainForm.Instance.Toast(T("Open an image first"), Icons.Info); return; }
            Imaging.ColorManagement.IccProfile p;
            Imaging.ColorManagement.RenderingIntent intent;
            if (!ColorUi.ChooseProfile(FindForm(), _img, convert, out p, out intent)) return;
            ApplyProfileStep(convert, p, intent);
        }

        /// <summary>The assign/convert step itself (also for scripts and tests).</summary>
        public void ApplyProfileStep(bool convert, Imaging.ColorManagement.IccProfile p, Imaging.ColorManagement.RenderingIntent intent)
        {
            string name = ColorUi.Name(p);
            if (convert)
                Apply(T("Convert to {0}", name), img =>
                {
                    Imaging.ColorManagement.ColorTransform.Create(img.EffectiveProfile, p, intent).Apply(img);
                    img.Profile = p;
                });
            else Apply(T("Assign {0}", name), img => img.Profile = p);
        }

        private void Replace(string label, Func<AstroImage, AstroImage> op)
        {
            if (_img == null) { MainForm.Instance.Toast(T("Open an image first"), Icons.Info); return; }
            var src = _img;
            AstroImage res = null;
            _job.Run(label, t => res = op(src.Clone()), () =>
            {
                if (res == null) return;
                _img = res;
                Push(label);
                _viewer.SetImage(_img, true);
                UpdateHistogram();
            });
        }

        private void UpdateHistogram()
        {
            _hist.Clear();
            if (_img == null) return;
            var img = _img;
            var res = new List<int[]>();
            _histJob.Replace(T("Histogram"), t =>
            {
                for (int c = 0; c < img.Channels; c++) res.Add(Stats.Histogram(img.Data[c], 256, 0, 1));
            }, () =>
            {
                // resolved from the palette at draw time so the histogram follows day/night switches
                var rgb = new[] { Color.FromArgb(230, 90, 90), Color.FromArgb(90, 200, 120), Color.FromArgb(100, 140, 240) };
                var night = new[] { 0, 2, 3 };
                for (int c = 0; c < res.Count; c++)
                {
                    int ch = Math.Min(c, 2);
                    Func<Palette, Color> col = img.Channels == 1 ? (Func<Palette, Color>)(pal => pal.TextDim) : pal => pal.IsNight ? pal.Series[night[ch]] : rgb[ch];
                    var s = _hist.AddSeries(null, SeriesStyle.Line, col);
                    for (int i = 0; i < 256; i++) s.Add(i / 255.0, Math.Log10(1 + res[c][i]));
                }
                _hist.Invalidate();
            });
        }

        private void ShowInfo()
        {
            if (_img == null) return;
            var sb = new System.Text.StringBuilder();
            sb.AppendLine(_name + "  —  " + Tn("{0}×{1}, {2} channel", "{0}×{1}, {2} channels", _img.Channels, _img.Width, _img.Height, _img.Channels));
            for (int c = 0; c < _img.Channels; c++) sb.AppendLine(T("Channel {0}: {1}", c, ImageStatistics.Compute(_img.Data[c], _img.Width, 0)));
            var stars = StarDetector.Detect(_img);
            double f, h, e;
            StarDetector.Summary(stars, out f, out h, out e);
            sb.AppendLine(T("Stars: {0}  ·  FWHM {1:0.00} px  ·  HFR {2:0.00} px  ·  eccentricity {3:0.00}", stars.Count, f, h, e));
            sb.AppendLine();
            sb.AppendLine(T("FITS header:"));
            foreach (var card in _img.Header.Cards) sb.AppendLine(card.ToString());
            ShowText(T("Image information"), sb.ToString());
        }

        public static void ShowText(string title, string text)
        {
            using (var f = new Form { Text = title, Width = 820, Height = 640, StartPosition = FormStartPosition.CenterParent })
            {
                var tb = new TextBox { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Both, Font = Theme.MonoFont, Text = text, WordWrap = false };
                f.Controls.Add(tb);
                Theme.Apply(f);
                f.HandleCreated += (s, e) => Theme.DarkTitleBar(f);
                f.ShowDialog(MainForm.Instance);
            }
        }

        // ------------------------------------------------------------------ tools

        private void BuildTools()
        {
            var list = new List<Control>();
            // history + histogram
            var hist = new FormGrid(80);
            hist.Full(_hist);
            hist.Full(_history);
            list.Add(new Collapsible(T("Histogram & history"), hist));

            // stretch
            var st = new FormGrid(110);
            var applyStf = new FlatButton(T("Apply auto stretch (STF) permanently"), Icons.Check, (s, e) => Apply(T("Auto stretch"), img => StretchOps.ApplyHistogram(img, StretchOps.AutoStretch(img, true))));
            st.Full(applyStf);
            var ghsD = st.Row(T("GHS stretch D"), Ui.Num(3, 0, 15, 2, 0.1m));
            var ghsB = st.Row(T("GHS local b"), Ui.Num(2, -5, 15, 2, 0.1m));
            var ghsSP = st.Row(T("Symmetry point"), Ui.Num(0.05m, 0, 1, 4, 0.005m));
            var ghsLP = st.Row(T("Protect shadows"), Ui.Num(0, 0, 1, 3, 0.01m));
            var ghsHP = st.Row(T("Protect highlights"), Ui.Num(1, 0, 1, 3, 0.01m));
            st.Full(new FlatButton(T("Apply generalized hyperbolic stretch"), Icons.Check, (s, e) =>
            {
                var f = StretchOps.Ghs((double)ghsD.Value, (double)ghsB.Value, (double)ghsSP.Value, (double)ghsLP.Value, (double)Math.Max(ghsHP.Value, ghsSP.Value));
                Apply(T("GHS"), img => StretchOps.ApplyToLuminance(img, f));
            }));
            var asinh = st.Row(T("Arcsinh factor"), Ui.Num(50, 1, 1000));
            st.Full(new FlatButton(T("Apply arcsinh stretch (colour preserving)"), Icons.Check, (s, e) => { var bg = _img != null ? ImageStatistics.Compute(_img.Luminance(), _img.Width, 0).Background - 0.002 : 0; double k = (double)asinh.Value; Apply(T("Arcsinh"), img => StretchOps.ArcsinhStretch(img, k, Math.Max(0, bg))); }));
            var lb = st.Row(T("Levels black"), Ui.Num(0, 0, 1, 3, 0.005m));
            var lg = st.Row(T("Levels gamma"), Ui.Num(1, 0.1m, 5, 2, 0.05m));
            var lw = st.Row(T("Levels white"), Ui.Num(1, 0, 2, 3, 0.01m));
            st.Full(new FlatButton(T("Apply levels"), Icons.Check, (s, e) => { double b = (double)lb.Value, gm = (double)lg.Value, wp = (double)lw.Value; Apply(T("Levels"), img => StretchOps.Levels(img, b, gm, wp)); }));
            st.Full(new FlatButton(T("Local contrast (CLAHE)"), Icons.Check, (s, e) => Apply(T("CLAHE"), img => StretchOps.Clahe(img, 8, 2.0, 0.6))));
            list.Add(new Collapsible(T("Stretch"), st, false));

            // background
            var bgg = new FormGrid(110);
            var model = bgg.Row(T("Model"), Ui.EnumCombo(BackgroundModel.RadialBasis));
            var degree = bgg.Row(T("Polynomial degree"), Ui.Num(2, 1, 6));
            var gridN = bgg.Row(T("Samples per axis"), Ui.Num(16, 4, 50));
            var tol = bgg.Row(T("Tolerance"), Ui.Num(1, 0.2m, 5, 1, 0.1m));
            var smooth = bgg.Row(T("RBF smoothing"), Ui.Num(0.5m, 0, 5, 2, 0.1m));
            var corr = bgg.Row(T("Correction"), Ui.EnumCombo(BackgroundCorrection.Subtract));
            Func<BackgroundOptions> bopt = () => new BackgroundOptions { Model = (BackgroundModel)model.SelectedItem, PolynomialDegree = (int)degree.Value, GridSize = (int)gridN.Value, Tolerance = (double)tol.Value, Smoothing = (double)smooth.Value, Correction = (BackgroundCorrection)corr.SelectedItem };
            bgg.Full(new FlatButton(T("Show samples"), Icons.Grid, (s, e) => { if (_img != null) { _samples = BackgroundExtraction.AutoSamples(_img, bopt()); _viewer.Invalidate(); MainForm.Instance.Toast(Tn("{0} background sample", "{0} background samples", _samples.Count)); } }));
            bgg.Full(new FlatButton(T("Extract background (gradient removal)"), Icons.Check, (s, e) => { var o = bopt(); var smp = _samples; ApplyStoppable(T("Background extraction"), (img, ct) => BackgroundExtraction.Apply(img, o, smp, ct)); _samples = null; }) { Kind = ButtonKind.Primary });
            list.Add(new Collapsible(T("Background extraction"), bgg, false));

            // colour
            var col = new FormGrid(110);
            col.Full(new FlatButton(T("Background neutralization"), Icons.Check, (s, e) => Apply(T("Background neutralization"), img => ColorOps.BackgroundNeutralization(img))));
            col.Full(new FlatButton(T("Star colour calibration"), Icons.Check, (s, e) => Apply(T("Colour calibration"), img => ColorOps.StarColorCalibration(img))));
            col.Full(new FlatButton(T("Photometric colour calibration (needs WCS)"), Icons.Check, (s, e) => PhotometricColor()));
            var scnr = col.Row(T("SCNR amount"), Ui.Num(1, 0, 1, 2, 0.05m));
            col.Full(new FlatButton(T("Remove green cast (SCNR)"), Icons.Check, (s, e) => { double a = (double)scnr.Value; Apply(T("SCNR"), img => ColorOps.Scnr(img, ScnrMethod.AverageNeutral, a)); }));
            var sat = col.Row(T("Saturation"), Ui.Num(1.3m, 0, 4, 2, 0.05m));
            col.Full(new FlatButton(T("Apply saturation"), Icons.Check, (s, e) => { double a = (double)sat.Value; Apply(T("Saturation"), img => ColorOps.Saturation(img, a, 0.05)); }));
            var hue = col.Row(T("Hue shift (°)"), Ui.Num(0, -180, 180));
            col.Full(new FlatButton(T("Apply hue shift"), Icons.Check, (s, e) => { double a = (double)hue.Value; Apply(T("Hue"), img => ColorOps.HueSaturationLightness(img, a, 1, 1)); }));
            col.Full(new FlatButton(T("Align RGB channels"), Icons.Check, (s, e) => ApplyStoppable(T("Align channels"), (img, ct) => ColorOps.AlignChannels(img, ct))));
            col.Full(new FlatButton(T("Assign profile…"), Icons.Check, (s, e) => ProfileStep(false)));
            col.Full(new FlatButton(T("Convert to profile…"), Icons.Check, (s, e) => ProfileStep(true)));
            list.Add(new Collapsible(T("Colour"), col, false));

            // deconvolution
            var dc = new FormGrid(110);
            var dm = dc.Row(T("Method"), Ui.EnumCombo(DeconvolutionMethod.RichardsonLucyTV));
            var psfType = dc.Row(T("PSF"), Ui.Combo(T("From stars (empirical)"), T("Gaussian"), T("Moffat"), T("Airy")));
            var fwhm = dc.Row(T("PSF FWHM (px)"), Ui.Num(2.5m, 0.5m, 30, 2, 0.1m));
            var iters = dc.Row(T("Iterations"), Ui.Num(20, 1, 500));
            var tv = dc.Row(T("TV regularization"), Ui.Num(0.002m, 0, 0.1m, 4, 0.0005m));
            var der = dc.Row(T("Deringing"), Ui.Num(0.5m, 0, 1, 2, 0.05m));
            var wk = dc.Row(T("Wiener K"), Ui.Num(0.01m, 0.0001m, 1, 4, 0.001m));
            dc.Full(new FlatButton(T("Measure FWHM"), Icons.Target, (s, e) => { if (_img != null) { double f, h, ec; StarDetector.Summary(StarDetector.Detect(_img), out f, out h, out ec); if (!double.IsNaN(f)) fwhm.Value = (decimal)AstroMath.Clamp(f, 0.5, 30); } }));
            dc.Full(new FlatButton(T("Deconvolve"), Icons.Check, (s, e) =>
            {
                var o = new DeconvolutionOptions { Method = (DeconvolutionMethod)dm.SelectedItem, Iterations = (int)iters.Value, TvLambda = (double)tv.Value, DeringingAmount = (double)der.Value, WienerK = (double)wk.Value };
                int pt = psfType.SelectedIndex; double fw = (double)fwhm.Value;
                ApplyStoppable(T("Deconvolution"), (img, ct) =>
                {
                    Psf psf = pt == 1 ? Psf.Gaussian(fw) : pt == 2 ? Psf.Moffat(fw) : pt == 3 ? Psf.Airy(fw / 1.03 * 1.22) : Psf.FromStars(img.Luminance(), img.Width, img.Height, StarDetector.Detect(img));
                    Deconvolution.Run(img, psf, o, null, ct);
                });
            }) { Kind = ButtonKind.Primary });
            list.Add(new Collapsible(T("Deconvolution"), dc, false));

            // wavelets
            var wv = new FormGrid(110);
            var layers = new NumericUpDown[6]; var dn = new NumericUpDown[6];
            for (int i = 0; i < 6; i++) { layers[i] = wv.Row(T("Layer {0} ({1} px)", i + 1, 1 << i), Ui.Num(i < 3 ? 1.0m - i * 0.3m : 0, -1, 20, 2, 0.1m)); }
            var wdn = wv.Row(T("Denoise layer 1-2"), Ui.Num(0.5m, 0, 3, 2, 0.1m));
            wv.Full(new FlatButton(T("Apply wavelet sharpening"), Icons.Check, (s, e) =>
            {
                var amt = layers.Select(l => (double)l.Value).ToArray();
                double d = (double)wdn.Value;
                Apply(T("Wavelets"), img => { for (int c = 0; c < img.Channels; c++) img.Data[c] = Wavelets.Sharpen(img.Data[c], img.Width, img.Height, amt, new[] { d, d / 2, 0, 0, 0, 0 }); });
            }) { Kind = ButtonKind.Primary });
            list.Add(new Collapsible(T("Wavelets"), wv, false));

            // noise
            var nz = new FormGrid(110);
            var nStr = nz.Row(T("Strength"), Ui.Num(0.7m, 0, 1, 2, 0.05m));
            nz.Full(new FlatButton(T("Multiscale wavelet denoise"), Icons.Check, (s, e) => { double k = (double)nStr.Value; Apply(T("Wavelet denoise"), img => { for (int c = 0; c < img.Channels; c++) img.Data[c] = Wavelets.Denoise(img.Data[c], img.Width, img.Height, new[] { k, k * 0.8, k * 0.5, k * 0.3 }); }); }));
            nz.Full(new FlatButton(T("Non-local means (slow, best quality)"), Icons.Check, (s, e) => { double k = 1 + 2 * (double)nStr.Value; ApplyStoppable(T("NLM denoise"), (img, ct) => { for (int c = 0; c < img.Channels; c++) img.Data[c] = Enhance.NonLocalMeans(img.Data[c], img.Width, img.Height, k, 5, 2, ct); }); }));
            nz.Full(new FlatButton(T("Remove banding (rows)"), Icons.Check, (s, e) => Apply(T("Banding reduction"), img => Enhance.BandingReduction(img, true, false))));
            nz.Full(new FlatButton(T("Cosmetic correction (hot/cold pixels)"), Icons.Check, (s, e) => Apply(T("Cosmetic correction"), img => Imaging.Stacking.Calibration.CosmeticCorrection(img, null, 5, 5))));
            list.Add(new Collapsible(T("Noise reduction"), nz, false));

            // stars
            var stg = new FormGrid(110);
            var red = stg.Row(T("Reduction amount"), Ui.Num(0.6m, 0, 1, 2, 0.05m));
            stg.Full(new FlatButton(T("Reduce stars"), Icons.Check, (s, e) => { double a = (double)red.Value; Apply(T("Star reduction"), img => StarTools.ReduceStars(img, a)); }));
            stg.Full(new FlatButton(T("Remove stars (starless + save star layer)"), Icons.Check, (s, e) => RemoveStars()));
            stg.Full(new FlatButton(T("HDR multiscale (galaxy/nebula cores)"), Icons.Check, (s, e) => Apply(T("HDR multiscale"), img => Enhance.HdrMultiscale(img, 6, 0.5))));
            stg.Full(new FlatButton(T("Local contrast enhancement"), Icons.Check, (s, e) => Apply(T("Local contrast"), img => Enhance.LocalContrast(img, 60, 0.3))));
            list.Add(new Collapsible(T("Stars & structures"), stg, false));

            // geometry
            var geo = new FormGrid(110);
            var cropPct = geo.Row(T("Crop margins (%)"), Ui.Num(5, 0, 45, 1));
            geo.Full(new FlatButton(T("Crop edges"), Icons.Check, (s, e) => { double p = (double)cropPct.Value / 100; Replace(T("Crop"), img => Geometry.Crop(img, (int)(img.Width * p), (int)(img.Height * p), (int)(img.Width * (1 - 2 * p)), (int)(img.Height * (1 - 2 * p)))); }));
            var rot = geo.Row(T("Rotate (°)"), Ui.Num(90, -360, 360, 2));
            geo.Full(new FlatButton(T("Rotate"), Icons.Refresh, (s, e) => { double a = (double)rot.Value; Replace(T("Rotate {0}°", a), img => Math.Abs(a % 90) < 1e-9 ? Geometry.Rotate90(img, (int)Math.Round(a / 90)) : Geometry.Rotate(img, a, true)); }));
            geo.Full(new FlatButton(T("Flip horizontal"), Icons.Check, (s, e) => Replace(T("Flip horizontal"), Geometry.FlipHorizontal)));
            geo.Full(new FlatButton(T("Flip vertical"), Icons.Check, (s, e) => Replace(T("Flip vertical"), Geometry.FlipVertical)));
            var scale = geo.Row(T("Resample (%)"), Ui.Num(50, 5, 800));
            geo.Full(new FlatButton(T("Resample (Lanczos-3)"), Icons.Check, (s, e) => { double f = (double)scale.Value / 100; Replace(T("Resample"), img => Geometry.Resize(img, Math.Max(1, (int)(img.Width * f)), Math.Max(1, (int)(img.Height * f)))); }));
            geo.Full(new FlatButton(T("Bin 2×2"), Icons.Check, (s, e) => Replace(T("Bin 2×2"), img => Geometry.Bin(img, 2))));
            list.Add(new Collapsible(T("Geometry"), geo, false));

            // channels
            var ch = new FormGrid(110);
            ch.Full(new FlatButton(T("Extract luminance"), Icons.Check, (s, e) => Replace(T("Luminance"), img => img.ToMono())));
            ch.Full(new FlatButton(T("Split channels to files…"), Icons.Save, (s, e) => SplitChannels()));
            ch.Full(new FlatButton(T("LRGB: apply luminance file…"), Icons.Open, (s, e) => Lrgb()));
            var pal = ch.Row(T("Palette"), Ui.Combo("SHO", "HOO", "HSO", "FORAXX", "Natural"));
            ch.Full(new FlatButton(T("Narrowband combine (Ha, OIII, SII files)…"), Icons.Open, (s, e) => Narrowband((string)pal.SelectedItem)));
            ch.Full(new FlatButton(T("RGB combine (R, G, B files)…"), Icons.Open, (s, e) => RgbCombine()));
            list.Add(new Collapsible(T("Channels"), ch, false));

            // pixel math
            var pm = new FormGrid(60);
            var expr = pm.Row(T("f(x) ="), new TextBox { Text = "mtf(0.25, $T)", Font = Theme.MonoFont });
            pm.Full(Ui.Hint(T("$T is the current image; use min, max, abs, sqrt, exp, ln, pow, mtf(m,x), iif(c,a,b), clamp(x,lo,hi), $x, $y, $c. Other images: open them and refer by name (future: image manager).")));
            pm.Full(new FlatButton(T("Apply PixelMath"), Icons.Check, (s, e) =>
            {
                try
                {
                    var p = new PixelMath(expr.Text);
                    Replace(T("PixelMath: {0}", expr.Text), img => p.Evaluate(img, null));
                }
                catch (Exception ex) { MainForm.Instance.Toast(ex.Message, Icons.Warning, Theme.Current.Danger); }
            }));
            list.Add(new Collapsible(T("PixelMath"), pm, false));

            list.Reverse();
            foreach (var c in list) _tools.Controls.Add(c);
        }

        private void RemoveStars()
        {
            if (_img == null) return;
            var work = _img.Clone();
            AstroImage stars = null;
            _job.Run(T("Star removal"), t => stars = StarTools.RemoveStars(work, 1.8, 200, t.Cts.Token), () =>
            {
                _img = work; Push(T("Star removal")); _viewer.SetImage(_img, false);
                using (var d = new SaveFileDialog { Filter = "FITS|*.fits", FileName = "stars.fits", Title = T("Save the star layer (for screen blending later)") })
                    if (d.ShowDialog() == DialogResult.OK) FitsIO.Write(d.FileName, stars);
            });
        }

        private void PhotometricColor()
        {
            if (_img == null) return;
            var wcs = Science.Astrometry.Wcs.FromHeader(_img.Header, _img.Width, _img.Height);
            if (wcs == null) { MainForm.Instance.Toast(T("Plate solve the image first (Solve button)"), Icons.Warning); return; }
            Apply(T("Photometric colour calibration"), img =>
            {
                double ra, dec;
                wcs.Centre(out ra, out dec);
                var cat = new Science.Astrometry.VizierProvider { Source = Science.Astrometry.VizierProvider.Catalog.GaiaDR3 }.Cone(ra, dec, Math.Max(wcs.FieldWidthDeg, wcs.FieldHeightDeg) * 0.6, 15, 3000);
                var stars = StarDetector.Detect(img);
                var pairs = new List<Tuple<DetectedStar, double>>();
                foreach (var s in stars)
                {
                    double r, d;
                    wcs.PixelToWorld(s.X, s.Y, out r, out d);
                    var best = cat.Where(c => !double.IsNaN(c.BV)).OrderBy(c => AstroMath.Separation(r * AstroMath.Deg2Rad, d * AstroMath.Deg2Rad, c.Ra * AstroMath.Deg2Rad, c.Dec * AstroMath.Deg2Rad)).FirstOrDefault();
                    if (best != null && AstroMath.Separation(r * AstroMath.Deg2Rad, d * AstroMath.Deg2Rad, best.Ra * AstroMath.Deg2Rad, best.Dec * AstroMath.Deg2Rad) * AstroMath.Rad2ArcSec < 3 * wcs.PixelScale)
                        pairs.Add(Tuple.Create(s, best.BV));
                }
                var f = ColorOps.PhotometricColorCalibration(img, pairs);
                if (f == null) throw new InvalidOperationException(T("Not enough catalogue matches with colours ({0})", pairs.Count));
                Core.Util.Log.Info("PCC", string.Format("{0} stars, factors R {1:0.000} G 1 B {2:0.000}", pairs.Count, f[0], f[2]));
            });
        }

        /// <summary>The paths of n images chosen by the user, or null; they are loaded by the job that uses them.</summary>
        private string[] PickFiles(string title, int n)
        {
            if (_job.Refuse()) return null;
            using (var d = new OpenFileDialog { Filter = ImageFile.OpenFilter, Multiselect = n > 1, Title = title })
                return d.ShowDialog() != DialogResult.OK || d.FileNames.Length < n ? null : d.FileNames.Take(n).ToArray();
        }

        private void Lrgb()
        {
            if (_img == null || _img.Channels < 3) { MainForm.Instance.Toast(T("Open a colour image first"), Icons.Info); return; }
            var l = PickFiles(T("Luminance image (stretched, same size)"), 1);
            if (l == null) return;
            Replace(T("LRGB"), img => ColorOps.LrgbCombine(ImageFile.Load(l[0]), img, 1.0, 1.1));
        }

        /// <summary>Makes a new image from files in the background (loading included) and opens it.</summary>
        private void Compose(string label, Func<AstroImage> make)
        {
            AstroImage result = null;
            _job.Run(label, t => result = make(), () => OpenImage(result, label));
        }

        private void Narrowband(string palette)
        {
            if (_job.Refuse()) return;
            using (var d = new OpenFileDialog { Filter = ImageFile.OpenFilter, Multiselect = true, Title = T("Select Ha and OIII (and optionally SII) images — names containing Ha/OIII/SII are recognised") })
            {
                if (d.ShowDialog() != DialogResult.OK || d.FileNames.Length < 2) return;
                Func<string, string, bool> has = (f, k) => Path.GetFileName(f).ToUpperInvariant().Contains(k);
                var ha = d.FileNames.FirstOrDefault(f => has(f, "HA") || has(f, "H-ALPHA") || has(f, "HALPHA")) ?? d.FileNames[0];
                var o3 = d.FileNames.FirstOrDefault(f => has(f, "OIII") || has(f, "O3")) ?? d.FileNames.First(f => f != ha);
                var s2 = d.FileNames.FirstOrDefault(f => has(f, "SII") || has(f, "S2"));
                Compose(T("{0} composite", palette), () => ColorOps.NarrowbandPalette(ImageFile.Load(ha), ImageFile.Load(o3), s2 != null ? ImageFile.Load(s2) : null, palette));
            }
        }

        private void RgbCombine()
        {
            if (_job.Refuse()) return;
            using (var d = new OpenFileDialog { Filter = ImageFile.OpenFilter, Multiselect = true, Title = T("Select R, G and B images") })
            {
                if (d.ShowDialog() != DialogResult.OK || d.FileNames.Length < 3) return;
                var f = d.FileNames.OrderBy(n => n).ToArray();
                Func<string, int> order = n => { var l = Path.GetFileName(n).ToLowerInvariant(); return l.Contains("_r") || l.StartsWith("r") || l.Contains("red") ? 0 : l.Contains("_g") || l.StartsWith("g") || l.Contains("green") ? 1 : 2; };
                var sorted = f.OrderBy(order).ToArray();
                Compose(T("RGB composite"), () =>
                {
                    var imgs = sorted.Take(3).Select(ImageFile.Load).ToArray();
                    return AstroImage.Combine(imgs[0], imgs[1], imgs[2]);
                });
            }
        }

        private void SplitChannels()
        {
            if (_img == null || _img.Channels < 3) return;
            using (var d = new FolderBrowserDialog())
            {
                if (d.ShowDialog() != DialogResult.OK) return;
                string b = Path.GetFileNameWithoutExtension(_name);
                string[] n = { "R", "G", "B" };
                for (int c = 0; c < 3; c++) FitsIO.Write(Path.Combine(d.SelectedPath, b + "_" + n[c] + ".fits"), _img.ExtractChannel(c));
                MainForm.Instance.Toast(T("Channels saved"));
            }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.Z)) { Undo(); return true; }
            if (keyData == (Keys.Control | Keys.Y)) { Redo(); return true; }
            if (keyData == (Keys.Control | Keys.O)) { Open(); return true; }
            if (keyData == (Keys.Control | Keys.S)) { SaveAs(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        public IEnumerable<AppCommand> GetCommands()
        {
            yield return new AppCommand { Title = T("Process: open image"), Icon = Icons.Open, Shortcut = "Ctrl+O", Run = () => { MainForm.Instance.Navigate("processing"); Open(); } };
            yield return new AppCommand { Title = T("Process: auto stretch"), Run = () => Apply(T("Auto stretch"), img => StretchOps.ApplyHistogram(img, StretchOps.AutoStretch(img, true))) };
            yield return new AppCommand { Title = T("Process: background extraction"), Run = () => Apply(T("Background extraction"), img => BackgroundExtraction.Apply(img, new BackgroundOptions())) };
            yield return new AppCommand { Title = T("Process: undo"), Shortcut = "Ctrl+Z", Run = Undo };
        }
    }
}
