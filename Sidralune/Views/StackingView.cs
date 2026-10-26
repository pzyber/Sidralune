using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Sidralune.Imaging;
using Sidralune.Imaging.IO;
using Sidralune.Imaging.Stacking;
using Sidralune.UI;

namespace Sidralune.Views
{
    /// <summary>Deep-sky calibration, registration and stacking (DeepSkyStacker workflow).</summary>
    public class StackingView : UserControl, ICommandSource
    {
        private readonly StackingEngine _engine = new StackingEngine();
        // the settings the grid edits; a job gets a copy, so they stay editable while it works, and the engine uses them
        // again between jobs (saving a file list, marking a comet, choosing the reference)
        private StackingSettings _settings;
        private readonly DataGridView _grid = Ui.Grid();
        private readonly ImageViewer _viewer = new ImageViewer { Dock = DockStyle.Fill, EmptyText = "Add light frames (and optional darks, flats, dark flats and bias), then Register and Stack" };
        private readonly PropertyGrid _props = new PropertyGrid { Dock = DockStyle.Fill, HelpVisible = true, PropertySort = PropertySort.Categorized };
        private readonly Label _summary = new Label { Dock = DockStyle.Bottom, Height = 40, Tag = "dim" };
        private StackResult _result;
        private AstroImage _resultBase;
        private bool _markComet;
        private CalibrationSet _cal;
        // reading, registering and stacking work on the frame list and settings, so they run one at a time and lock both;
        // previews keep the newest
        private readonly JobSlot _job = new JobSlot(), _previewJob = new JobSlot();
        private readonly Slider _sat = new Slider { Minimum = 0, Maximum = 300, Value = 100, TickStyle = TickStyle.None, Dock = DockStyle.Fill };
        private readonly Slider _black = new Slider { Minimum = 0, Maximum = 200, Value = 0, TickStyle = TickStyle.None, Dock = DockStyle.Fill };
        private readonly Slider _mid = new Slider { Minimum = 1, Maximum = 99, Value = 50, TickStyle = TickStyle.None, Dock = DockStyle.Fill };

        public StackingView()
        {
            var left = new Panel { Dock = DockStyle.Left, Width = 240, Padding = new Padding(0, 0, 8, 0), AutoScroll = true };
            var actions = new FlowLayoutPanel { Dock = DockStyle.Top, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true };
            Action<string, string, Action, ButtonKind> act = (t, i, a, k) => { var b = new FlatButton(t, i, (s, e) => a()) { Width = 225, Kind = k, Margin = new Padding(0, 0, 0, 4) }; actions.Controls.Add(b); };
            actions.Controls.Add(Header("Frames"));
            act("Light frames…", Icons.Open, () => AddFiles(FrameKind.Light), ButtonKind.Normal);
            act("Dark frames…", Icons.Open, () => AddFiles(FrameKind.Dark), ButtonKind.Normal);
            act("Flat frames…", Icons.Open, () => AddFiles(FrameKind.Flat), ButtonKind.Normal);
            act("Dark flat frames…", Icons.Open, () => AddFiles(FrameKind.DarkFlat), ButtonKind.Normal);
            act("Bias / offset frames…", Icons.Open, () => AddFiles(FrameKind.Bias), ButtonKind.Normal);
            act("Master frames (FITS)…", Icons.Open, LoadMasters, ButtonKind.Subtle);
            actions.Controls.Add(Header("List"));
            act("Check all", Icons.Check, () => SetChecked(true), ButtonKind.Subtle);
            act("Uncheck all", Icons.Remove, () => SetChecked(false), ButtonKind.Subtle);
            act("Remove selected", Icons.Remove, RemoveSelected, ButtonKind.Subtle);
            act("Clear list", Icons.Close, () => { _engine.Frames.Clear(); RefreshGrid(); }, ButtonKind.Subtle);
            act("Save file list…", Icons.Save, SaveList, ButtonKind.Subtle);
            act("Open file list…", Icons.Open, LoadList, ButtonKind.Subtle);
            actions.Controls.Add(Header("Process"));
            act("Register checked frames", Icons.Target, Register, ButtonKind.Normal);
            act("Mark comet in selected frame", Icons.Comet, () => { _markComet = true; MainForm.Instance.Toast("Click the comet nucleus in the preview"); }, ButtonKind.Subtle);
            act("Use selected as reference", Icons.Star, UseAsReference, ButtonKind.Subtle);
            act("Stack checked frames", Icons.Stack, Stack, ButtonKind.Primary);
            act("Save stacked image…", Icons.Save, SaveResult, ButtonKind.Normal);
            act("Open in Process", Icons.Process, () => { if (_result != null) { MainForm.Instance.Navigate("processing"); MainForm.Instance.GetView<ProcessingView>("processing")?.OpenImage(CurrentResult(), "Stacked image"); } }, ButtonKind.Subtle);
            left.Controls.Add(actions);

            _grid.ReadOnly = false;
            _grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "On", HeaderText = "", FillWeight = 10 });
            foreach (var c in new[] { "Type", "File", "Exposure", "ISO/Gain", "Temp", "Filter", "Score", "Stars", "FWHM", "Ecc.", "Background", "Status" }) _grid.Columns.Add(c, c);
            for (int i = 1; i < _grid.Columns.Count; i++) _grid.Columns[i].ReadOnly = true;
            _grid.Columns["File"].FillWeight = 180; _grid.Columns["Status"].FillWeight = 140; _grid.Columns["Type"].FillWeight = 55;
            _grid.CurrentCellDirtyStateChanged += (s, e) => { if (_grid.IsCurrentCellDirty) _grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            _grid.CellValueChanged += (s, e) => { if (e.RowIndex >= 0 && e.ColumnIndex == 0) ((StackFrame)_grid.Rows[e.RowIndex].Tag).Enabled = (bool)(_grid.Rows[e.RowIndex].Cells[0].Value ?? false); };
            _grid.SelectionChanged += (s, e) => { if (!_filling) Preview(); };
            _grid.AllowDrop = true;
            _grid.DragEnter += (s, e) => { if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy; };
            _grid.DragDrop += (s, e) => { var f = e.Data.GetData(DataFormats.FileDrop) as string[]; if (f != null) AddPaths(f, null); };
            _viewer.FilesDropped += f => AddPaths(f, null);
            _viewer.ImageClick += (x, y, e) =>
            {
                if (!_markComet || _grid.SelectedRows.Count == 0) return;
                var fr = (StackFrame)_grid.SelectedRows[0].Tag;
                fr.CometXY = new[] { x, y };
                _markComet = false;
                _settings.Comet = CometMode.StarsAndComet;
                _props.Refresh();
                MainForm.Instance.Toast(string.Format("Comet marked at ({0:0.0}, {1:0.0}) in {2}. Mark it in at least two frames.", x, y, fr.FileName));
            };
            _viewer.PaintOverlay += (g, map) =>
            {
                if (_grid.SelectedRows.Count == 0) return;
                var fr = (StackFrame)_grid.SelectedRows[0].Tag;
                if (fr.Stars != null && _viewer.Image != null && _viewer.Image == _preview)
                    using (var pen = new Pen(Theme.WithAlpha(Theme.Current.Success, 180)))
                        foreach (var st in fr.Stars.Take(500)) { var p = map(st.X, st.Y); float r = (float)Math.Max(4, st.Hfr * 2 * _viewer.Zoom); g.DrawEllipse(pen, p.X - r, p.Y - r, 2 * r, 2 * r); }
                if (fr.CometXY != null) { var p = map(fr.CometXY[0], fr.CometXY[1]); using (var pen = new Pen(Theme.Current.Comet, 2)) { g.DrawEllipse(pen, p.X - 10, p.Y - 10, 20, 20); } }
            };

            var settingsCard = new Card("Stacking settings") { Dock = DockStyle.Fill };
            _settings = _engine.Settings;
            _props.SelectedObject = new GridAdapter(_settings, "Stacking");
            settingsCard.Controls.Add(_props);
            var post = new Card("Stacked image adjustments") { Dock = DockStyle.Bottom, Height = 170 };
            var pg = new FormGrid(90);
            pg.Row("Black point", _black); pg.Row("Midtones", _mid); pg.Row("Saturation", _sat);
            foreach (var tb in new[] { _black, _mid, _sat }) tb.ValueChanged += (s, e) => ApplyPost();
            post.Controls.Add(pg);
            _job.Guard(actions, post);
            _job.Changed += () =>
            {
                if (!_job.Busy) _engine.Settings = _settings;
                // the frame list stays visible for previews, but its check boxes wait for the job
                _grid.ReadOnly = _job.Busy;
                if (!_job.Busy) for (int i = 0; i < _grid.Columns.Count; i++) _grid.Columns[i].ReadOnly = i > 0;
            };
            var right = new Panel { Dock = DockStyle.Right, Width = 340, Padding = new Padding(8, 0, 0, 0) };
            right.Controls.Add(settingsCard);
            right.Controls.Add(post);

            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 300 };
            split.Panel1.Controls.Add(_grid);
            split.Panel2.Controls.Add(_viewer);
            split.Panel2.Controls.Add(_summary);
            Controls.Add(split);
            Controls.Add(right);
            Controls.Add(left);
            Padding = new Padding(10);
            _engine.Message += m => MainForm.Instance.SetStatus(m);
        }

        private static Label Header(string t) => new Label { Text = t.ToUpperInvariant(), Font = Theme.SmallFont, Tag = "dim", AutoSize = true, Margin = new Padding(2, 10, 0, 4) };

        private void AddFiles(FrameKind kind)
        {
            using (var d = new OpenFileDialog { Multiselect = true, Filter = ImageFile.OpenFilter, Title = "Add " + kind + " frames" })
                if (d.ShowDialog() == DialogResult.OK) AddPaths(d.FileNames, kind);
        }

        private void AddPaths(IEnumerable<string> paths, FrameKind? kind)
        {
            var list = paths.Where(ImageFile.IsSupported).ToList();
            _job.Run("Reading " + list.Count + " file headers", t => _engine.AddFiles(list, kind), RefreshGrid);
        }

        private void LoadMasters()
        {
            if (_job.Refuse()) return;
            using (var d = new OpenFileDialog { Multiselect = true, Filter = ImageFile.OpenFilter, Title = "Master bias / dark / flat (named accordingly)" })
            {
                if (d.ShowDialog() != DialogResult.OK) return;
                var files = d.FileNames;
                var loaded = new List<Tuple<string, FrameKind, AstroImage>>();
                // loaded in the background; they become the masters when all are in
                _job.Run("Loading " + files.Length + " master frame(s)", t =>
                {
                    foreach (var f in files)
                    {
                        var img = ImageFile.Load(f);
                        loaded.Add(Tuple.Create(f, StackingEngine.GuessKind(f, img), img));
                    }
                }, () =>
                {
                    foreach (var m in loaded)
                    {
                        if (m.Item2 == FrameKind.Bias) _engine.MasterBias = m.Item3;
                        else if (m.Item2 == FrameKind.Dark) _engine.MasterDark = m.Item3;
                        else if (m.Item2 == FrameKind.Flat) _engine.MasterFlat = m.Item3;
                        else if (m.Item2 == FrameKind.DarkFlat) _engine.MasterDarkFlat = m.Item3;
                        MainForm.Instance.Toast("Loaded master " + m.Item2 + ": " + Path.GetFileName(m.Item1));
                    }
                });
            }
        }

        private void SetChecked(bool on) { foreach (var f in _engine.Frames) f.Enabled = on; RefreshGrid(); }

        private void RemoveSelected()
        {
            foreach (DataGridViewRow r in _grid.SelectedRows) _engine.Frames.Remove((StackFrame)r.Tag);
            RefreshGrid();
        }

        private void UseAsReference()
        {
            if (_grid.SelectedRows.Count == 0) return;
            var fr = (StackFrame)_grid.SelectedRows[0].Tag;
            _settings.ReferencePath = fr.Path;
            foreach (var f in _engine.Frames) f.IsReference = f == fr;
            RefreshGrid();
        }

        private void RefreshGrid()
        {
            _grid.SuspendLayout();
            // adding the first row selects it and raises SelectionChanged; give every row its frame before it is added
            // and preview once the list is complete
            _filling = true;
            try
            {
                _grid.Rows.Clear();
                foreach (var f in _engine.Frames.OrderBy(f => f.Kind).ThenBy(f => f.Path))
                {
                    var row = new DataGridViewRow { Tag = f };
                    row.CreateCells(_grid, f.Enabled, f.Kind + (f.IsReference ? " ★" : ""), f.FileName, f.Exposure > 0 ? f.Exposure.ToString("0.###") + " s" : "",
                        !double.IsNaN(f.Iso) ? "ISO " + f.Iso : !double.IsNaN(f.Gain) ? f.Gain.ToString("0") : "", double.IsNaN(f.Temperature) ? "" : f.Temperature.ToString("0.0"),
                        f.Filter ?? "", f.Registered ? f.Score.ToString("0.0") : "", f.Registered ? f.StarCount.ToString() : "", double.IsNaN(f.Fwhm) ? "" : f.Fwhm.ToString("0.00"),
                        double.IsNaN(f.Eccentricity) ? "" : f.Eccentricity.ToString("0.00"), double.IsNaN(f.Background) ? "" : (f.Background * 65535).ToString("0"), f.Status);
                    _grid.Rows.Add(row);
                }
            }
            finally { _filling = false; _grid.ResumeLayout(); }
            Preview();
            var lights = _engine.Frames.Where(f => f.Kind == FrameKind.Light).ToList();
            _summary.Text = string.Format("{0} lights ({1} checked, {2:0.0} min) · {3} darks · {4} flats · {5} dark flats · {6} bias{7}",
                lights.Count, lights.Count(f => f.Enabled), lights.Where(f => f.Enabled).Sum(f => f.Exposure) / 60, _engine.Frames.Count(f => f.Kind == FrameKind.Dark),
                _engine.Frames.Count(f => f.Kind == FrameKind.Flat), _engine.Frames.Count(f => f.Kind == FrameKind.DarkFlat), _engine.Frames.Count(f => f.Kind == FrameKind.Bias),
                _result != null ? "\r\nResult: " + _result.Report : "");
        }

        private AstroImage _preview;
        private StackFrame _previewFrame;
        private bool _filling;

        private void Preview()
        {
            if (_grid.SelectedRows.Count == 0) return;
            var fr = _grid.SelectedRows[0].Tag as StackFrame;
            if (fr == null || fr == _previewFrame) return; // refreshing the list keeps the frame already shown
            _previewFrame = fr;
            AstroImage img = null;
            _previewJob.Replace("Loading preview", t =>
            {
                img = ImageFile.Load(fr.Path);
                if (img.Channels == 1 && img.Bayer != BayerPattern.None) img = Debayer.Apply(img, DebayerMethod.SuperPixel);
            }, () => { _preview = img; _viewer.SetImage(img); });
        }

        private void Register()
        {
            if (!_engine.Lights.Any()) { MainForm.Instance.Toast("No checked light frames", Icons.Warning); return; }
            if (_job.Refuse()) return; // the settings below must not change under a running job
            _engine.Settings = _settings.Clone(); // the run's own copy; the grid stays editable
            _job.Run("Registering", t =>
            {
                var prog = new Progress<StackProgress>(p => Tasks.Report(t, p.Fraction, p.Stage + (p.Detail != null ? " " + p.Detail : "")));
                _cal = _engine.BuildCalibration(prog, t.Cts.Token);
                _engine.Register(_cal, prog, t.Cts.Token);
            }, () => { RefreshGrid(); MainForm.Instance.Toast("Registration complete"); });
        }

        private void Stack()
        {
            if (!_engine.Lights.Any()) { MainForm.Instance.Toast("No checked light frames", Icons.Warning); return; }
            StackResult result = null;
            if (_job.Refuse()) return; // the settings below must not change under a running job
            _engine.Settings = _settings.Clone(); // the run's own copy; the grid stays editable
            _job.Run("Stacking", t =>
            {
                var prog = new Progress<StackProgress>(p => Tasks.Report(t, p.Fraction, p.Stage + (p.Detail != null ? " " + p.Detail : "")));
                var start = DateTime.Now;
                if (_cal == null || !_engine.Lights.All(l => l.Registered || !l.Enabled) || !_engine.Lights.Any(l => l.Registered))
                {
                    _cal = _engine.BuildCalibration(prog, t.Cts.Token);
                    _engine.Register(_cal, prog, t.Cts.Token);
                }
                result = _engine.Stack(_cal, prog, t.Cts.Token, start);
            }, () =>
            {
                _result = result;
                _resultBase = result.Image;
                _black.Value = 0; _mid.Value = 50; _sat.Value = 100;
                _viewer.SetImage(_result.Image);
                RefreshGrid();
                MainForm.Instance.Toast("Stacking complete: " + _result.Report, Icons.Check, Theme.Current.Success);
            });
        }

        private AstroImage CurrentResult()
        {
            if (_resultBase == null) return null;
            if (_black.Value == 0 && _mid.Value == 50 && _sat.Value == 100) return _resultBase;
            var img = _resultBase.Clone();
            var st = Imaging.Analysis.ImageStatistics.Compute(img.Luminance(), img.Width, 0);
            double black = st.Background - 3 * st.BackgroundSigma + _black.Value / 200.0 * 6 * st.BackgroundSigma;
            var ht = new HistogramTransform { Shadows = Math.Max(0, black), Midtones = 1 - _mid.Value / 100.0 };
            StretchOps.ApplyHistogram(img, new[] { ht });
            if (_sat.Value != 100) ColorOps.Saturation(img, _sat.Value / 100.0);
            return img;
        }

        private void ApplyPost()
        {
            if (_resultBase == null) return;
            var img = CurrentResult();
            _viewer.Stretch = _black.Value == 0 && _mid.Value == 50 ? DisplayStretch.AutoLinked : DisplayStretch.None;
            _viewer.SetImage(img, false);
        }

        private void SaveResult()
        {
            var img = CurrentResult();
            if (img == null) { MainForm.Instance.Toast("Nothing stacked yet", Icons.Warning); return; }
            using (var d = new SaveFileDialog { Filter = ImageFile.SaveFilter, FileName = "Autosave.fits" })
            {
                if (d.ShowDialog() != DialogResult.OK) return;
                var fmt = ImageFile.FormatFromFilterIndex(d.FilterIndex);
                Func<int, float, float> tf = null;
                if (fmt == ImageSaveFormat.Png8 || fmt == ImageSaveFormat.Jpeg8)
                {
                    var t = StretchOps.AutoStretch(img, true);
                    tf = (c, v) => (float)t[Math.Min(c, t.Length - 1)].Apply(v);
                }
                ImageFile.Save(d.FileName, img, fmt, tf);
                MainForm.Instance.Toast("Saved " + Path.GetFileName(d.FileName));
            }
        }

        private void SaveList()
        {
            using (var d = new SaveFileDialog { Filter = "Sidralune stacking list|*.sdstack", FileName = "filelist.sdstack" })
                if (d.ShowDialog() == DialogResult.OK) _engine.SaveProject(d.FileName);
        }

        private void LoadList()
        {
            using (var d = new OpenFileDialog { Filter = "Sidralune stacking list|*.sdstack" })
                if (d.ShowDialog() == DialogResult.OK) { _engine.LoadProject(d.FileName); _settings = _engine.Settings; _props.SelectedObject = new GridAdapter(_settings, "Stacking"); RefreshGrid(); }
        }

        public IEnumerable<AppCommand> GetCommands()
        {
            yield return new AppCommand { Title = "Deep sky: add light frames", Icon = Icons.Open, Run = () => { MainForm.Instance.Navigate("stacking"); AddFiles(FrameKind.Light); } };
            yield return new AppCommand { Title = "Deep sky: register", Run = Register };
            yield return new AppCommand { Title = "Deep sky: stack", Icon = Icons.Stack, Run = Stack };
        }
    }
}
