using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Sidralune.Core.Mathematics;
using Sidralune.Core.Util;
using Sidralune.Scripting;
using Sidralune.UI;

namespace Sidralune.Views
{
    /// <summary>
    /// C# scripting against the whole Sidralune API (compiled in-process), with output console, plot and table, plus a
    /// data-analysis workbench for CSV tables (column plots, polynomial and linear fits, statistics).
    /// </summary>
    public class ScriptingView : UserControl, ICommandSource
    {
        private readonly TabbedPanel _tabs = new TabbedPanel { Dock = DockStyle.Fill, Padding = new Padding(10) };
        private readonly RichTextBox _editor = new RichTextBox { Dock = DockStyle.Fill, Font = new Font("Consolas", 10.5f), AcceptsTab = true, WordWrap = false, BorderStyle = BorderStyle.None, DetectUrls = false };
        private readonly TextBox _console = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, Font = Theme.MonoFont, ScrollBars = ScrollBars.Both, WordWrap = false };
        private readonly PlotControl _plot = new PlotControl { Dock = DockStyle.Fill, Title = "Script plot" };
        private readonly DataGridView _grid = Ui.Grid();
        private ScriptHost _running;
        private FlatButton _runBtn;
        private string _path;

        private static string ScriptFolder => Path.Combine(AppPaths.Data, "Scripts");

        public ScriptingView()
        {
            Controls.Add(_tabs);
            _tabs.AddPage("C# scripts", BuildScripts());
            _tabs.AddPage("Data analysis", BuildData());
        }

        // ================================================================== scripts

        private Control BuildScripts()
        {
            var panel = new Panel { Dock = DockStyle.Fill };
            var examples = Ui.Combo(ScriptExamples.All.Select(e => (object)e[0]).ToArray());
            examples.Width = 280;
            examples.SelectedIndexChanged += (s, e) => { _editor.Text = ScriptExamples.All[examples.SelectedIndex][1]; _path = null; };
            _runBtn = new FlatButton("Run (F5)", Icons.Play, (s, e) => RunOrStop()) { Kind = ButtonKind.Primary };
            _runBtn.AutoSizeToText();
            var open = new FlatButton("Open…", Icons.Open, (s, e) =>
            {
                Directory.CreateDirectory(ScriptFolder);
                using (var d = new OpenFileDialog { Filter = "Sidralune scripts|*.cs;*.csx|All files|*.*", InitialDirectory = ScriptFolder })
                    if (d.ShowDialog() == DialogResult.OK) { _editor.Text = File.ReadAllText(d.FileName); _path = d.FileName; }
            }) { Kind = ButtonKind.Subtle };
            open.AutoSizeToText();
            var save = new FlatButton("Save…", Icons.Save, (s, e) => SaveScript(false)) { Kind = ButtonKind.Subtle };
            save.AutoSizeToText();
            var api = new FlatButton("API help", Icons.Info, (s, e) => ProcessingView.ShowText("Script API", ApiHelp())) { Kind = ButtonKind.Subtle };
            api.AutoSizeToText();
            var bar = Ui.Toolbar(examples, _runBtn, open, save, api);

            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterWidth = 6 };
            var editorHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(6) };
            editorHost.Controls.Add(_editor);
            split.Panel1.Controls.Add(editorHost);
            var right = new TabbedPanel { Dock = DockStyle.Fill };
            right.AddPage("Output", _console);
            right.AddPage("Plot", _plot);
            right.AddPage("Table", _grid);
            split.Panel2.Controls.Add(right);
            bool placed = false;
            split.SizeChanged += (s, e) => { if (!placed && split.Width > 600) { placed = true; split.SplitterDistance = split.Width * 55 / 100; } };
            _editor.Text = ScriptExamples.All[0][1];
            _editor.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.F5) { RunOrStop(); e.Handled = true; }
                else if (e.Control && e.KeyCode == Keys.S) { SaveScript(true); e.Handled = true; }
                else if (e.KeyCode == Keys.Tab && !e.Shift) { _editor.SelectedText = "    "; e.SuppressKeyPress = true; }
            };
            _rightTabs = right;
            panel.Controls.Add(split);
            panel.Controls.Add(bar);
            panel.Controls.Add(new Label { Dock = DockStyle.Bottom, Height = 22, Tag = "dim", Text = "The editor holds the body of Run(); add helper methods after a line //@members. Everything in Sidralune (catalogues, ephemerides, imaging, devices) is available. F5 runs, Ctrl+S saves." });
            return panel;
        }

        private TabbedPanel _rightTabs;

        private void SaveScript(bool quick)
        {
            Directory.CreateDirectory(ScriptFolder);
            if (!quick || _path == null)
            {
                using (var d = new SaveFileDialog { Filter = "Sidralune script|*.cs", InitialDirectory = ScriptFolder, FileName = _path != null ? Path.GetFileName(_path) : "script.cs" })
                {
                    if (d.ShowDialog() != DialogResult.OK) return;
                    _path = d.FileName;
                }
            }
            File.WriteAllText(_path, _editor.Text);
            MainForm.Instance.Toast("Saved " + Path.GetFileName(_path));
        }

        /// <summary>Load code into the editor and run it (used by the --script command-line option).</summary>
        public void RunScript(string code)
        {
            _tabs.Select(0);
            _editor.Text = code;
            if (_running == null) RunOrStop();
        }

        private void RunOrStop()
        {
            if (_running != null) { _running.Cts.Cancel(); return; }
            _console.Clear();
            List<CompileError> errors;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var type = ScriptHost.Compile(_editor.Text, out errors);
            foreach (var e in errors) _console.AppendText((e.Warning ? "warning " : "error ") + e + Environment.NewLine);
            if (type == null)
            {
                _rightTabs.Select(0);
                var first = errors.FirstOrDefault(e => !e.Warning);
                if (first != null && first.Line > 0 && first.Line <= _editor.Lines.Length)
                {
                    int idx = _editor.GetFirstCharIndexFromLine(first.Line - 1);
                    if (idx >= 0) { _editor.Select(idx, _editor.Lines[first.Line - 1].Length); _editor.Focus(); }
                }
                MainForm.Instance.Toast("Compilation failed", Icons.Warning, Theme.Current.Warning);
                return;
            }
            _console.AppendText(string.Format("Compiled in {0} ms, running…{1}", sw.ElapsedMilliseconds, Environment.NewLine));
            var host = new ScriptHost { Plot = _plot, Grid = _grid };
            host.Output = s => MainForm.Instance.Post(() => _console.AppendText(s.Replace("\n", Environment.NewLine).Replace("\r\r", "\r")));
            host.Ui = a => MainForm.Instance.Post(a);
            _running = host;
            _runBtn.Text = "Stop"; _runBtn.Icon = Icons.Stop; _runBtn.AutoSizeToText();
            var bt = Tasks.Run("Script", t =>
            {
                host.Cts = t.Cts;
                host.Progress = (p, s) => Tasks.Report(t, p, s);
                sw.Restart();
                host.Execute(type);
            }, () => Finished(string.Format("Finished in {0:0.00} s", sw.Elapsed.TotalSeconds)),
               ex => Finished(ex is OperationCanceledException ? "Cancelled" : "Exception: " + ex.GetType().Name + ": " + ex.Message + Environment.NewLine + ex.StackTrace));
        }

        private void Finished(string msg)
        {
            _console.AppendText(msg + Environment.NewLine);
            _running = null;
            _runBtn.Text = "Run (F5)"; _runBtn.Icon = Icons.Play; _runBtn.AutoSizeToText();
            if (_grid.Rows.Count > 0 && _console.Lines.Length < 4) _rightTabs.Select(2);
            else if (_plot.Series.Count > 0 && _console.Lines.Length < 4) _rightTabs.Select(1);
        }

        private static string ApiHelp()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Script members (call them directly):\r\n");
            foreach (var m in typeof(ScriptBase).GetMembers(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly)
                .Where(m => !(m is System.Reflection.MethodInfo && ((System.Reflection.MethodInfo)m).IsSpecialName) && m.Name != "Run").OrderBy(m => m.Name))
            {
                var mi = m as System.Reflection.MethodInfo;
                var pi = m as System.Reflection.PropertyInfo;
                if (mi != null) sb.AppendLine(string.Format("  {0} {1}({2})", Short(mi.ReturnType), mi.Name, string.Join(", ", mi.GetParameters().Select(p => Short(p.ParameterType) + " " + p.Name))));
                else if (pi != null) sb.AppendLine(string.Format("  {0} {1} {{ get; }}", Short(pi.PropertyType), pi.Name));
            }
            sb.AppendLine("\r\nNamespaces imported:\r\n  " + string.Join("\r\n  ", ScriptHost.Usings));
            sb.AppendLine("\r\nUseful entry points: Sky.BodyPlace(Body.Jupiter), Events.Twilight(Site, t), Catalogs.Search(\"M 31\"), StarDetector.Detect(img), PlateSolver.Solve(img, options), StackingEngine, LuckyStacker, PeriodSearch.LombScargle, OnlineServices.SimbadQuery(adql), Devices.Telescope.SlewToCoordinates(raHours, decDeg).");
            return sb.ToString();
        }

        private static string Short(Type t)
        {
            if (t == typeof(void)) return "void";
            if (t.IsGenericType) return t.Name.Substring(0, t.Name.IndexOf('`')) + "<" + string.Join(", ", t.GetGenericArguments().Select(Short)) + ">";
            return t.Name;
        }

        // ================================================================== data analysis

        private DataTableModel _data;
        private ComboBox _xCol, _yCol, _eCol, _fit;
        private PlotControl _dataPlot;
        private DataGridView _dataGrid;
        private TextBox _dataOut;
        private NumericUpDown _fitDeg;

        private class DataTableModel
        {
            public List<string> Columns = new List<string>();
            public List<string[]> Rows = new List<string[]>();
            public double Get(int r, int c) => c >= 0 && c < Rows[r].Length ? AstroMath.ParseInvariant(Rows[r][c]) : double.NaN;
        }

        private Control BuildData()
        {
            var panel = new Panel { Dock = DockStyle.Fill };
            var side = new Card("Columns & fits") { Dock = DockStyle.Left, Width = 320, AutoScroll = true };
            var g = new FormGrid(100);
            var open = new FlatButton("Open CSV / table…", Icons.Open, (s, e) => OpenData()) { Kind = ButtonKind.Primary, Width = 280 };
            g.Full(open);
            var paste = new FlatButton("Paste from clipboard", Icons.List, (s, e) => { if (Clipboard.ContainsText()) LoadData(Clipboard.GetText(), "clipboard"); }) { Width = 280 };
            g.Full(paste);
            _xCol = g.Row("X", Ui.Combo());
            _yCol = g.Row("Y", Ui.Combo());
            _eCol = g.Row("Y error", Ui.Combo());
            _fit = g.Row("Fit", Ui.Combo("None", "Polynomial", "Linear (weighted)", "Power law y = a·x^b", "Exponential y = a·e^(bx)"));
            _fitDeg = g.Row("Degree", Ui.Num(2, 1, 10));
            var invert = g.Row("Invert Y", new CheckBox());
            var logx = g.Row("log₁₀ X", new CheckBox());
            var logy = g.Row("log₁₀ Y", new CheckBox());
            var plotBtn = new FlatButton("Plot", Icons.Chart, (s, e) => PlotData(invert.Checked, logx.Checked, logy.Checked)) { Kind = ButtonKind.Primary, Width = 280 };
            g.Full(plotBtn);
            _dataOut = new TextBox { Multiline = true, ReadOnly = true, Height = 220, Font = Theme.MonoFont, ScrollBars = ScrollBars.Vertical };
            g.Full(_dataOut);
            side.Controls.Add(g);
            _dataPlot = new PlotControl { Dock = DockStyle.Fill, Title = "Data" };
            _dataGrid = Ui.Grid();
            _dataGrid.Dock = DockStyle.Bottom; _dataGrid.Height = 220;
            panel.Controls.Add(_dataPlot);
            panel.Controls.Add(_dataGrid);
            panel.Controls.Add(side);
            return panel;
        }

        private void OpenData()
        {
            using (var d = new OpenFileDialog { Filter = "Tables|*.csv;*.txt;*.dat;*.tsv;*.tbl|All files|*.*" })
                if (d.ShowDialog() == DialogResult.OK) LoadData(File.ReadAllText(d.FileName), Path.GetFileName(d.FileName));
        }

        private void LoadData(string text, string name)
        {
            var lines = text.Replace("\r", "").Split('\n').Where(l => l.Trim().Length > 0).ToList();
            if (lines.Count < 2) return;
            char sep = lines.Take(5).All(l => l.Contains(',')) ? ',' : lines.Take(5).All(l => l.Contains('\t')) ? '\t' : lines.Take(5).All(l => l.Contains(';')) ? ';' : ' ';
            Func<string, string[]> split = l => sep == ' ' ? l.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries) : l.Split(sep).Select(c => c.Trim().Trim('"')).ToArray();
            var m = new DataTableModel();
            var first = split(lines[0].TrimStart('#'));
            bool header = first.Any(c => c.Length > 0 && double.IsNaN(AstroMath.ParseInvariant(c)));
            m.Columns = header ? first.ToList() : Enumerable.Range(1, first.Length).Select(i => "col" + i).ToList();
            foreach (var l in lines.Skip(header ? 1 : 0)) if (!l.StartsWith("#")) m.Rows.Add(split(l));
            _data = m;
            foreach (var c in new[] { _xCol, _yCol, _eCol }) { c.Items.Clear(); if (c == _eCol) c.Items.Add("(none)"); c.Items.AddRange(m.Columns.Cast<object>().ToArray()); }
            _xCol.SelectedIndex = 0; _yCol.SelectedIndex = Math.Min(1, m.Columns.Count - 1); _eCol.SelectedIndex = 0;
            _dataGrid.Columns.Clear(); _dataGrid.Rows.Clear();
            foreach (var c in m.Columns) _dataGrid.Columns.Add(c, c);
            foreach (var r in m.Rows.Take(5000)) _dataGrid.Rows.Add(r.Cast<object>().ToArray());
            _dataOut.Text = string.Format("{0}: {1} rows × {2} columns", name, m.Rows.Count, m.Columns.Count);
            PlotData(false, false, false);
        }

        private void PlotData(bool invert, bool logx, bool logy)
        {
            if (_data == null) return;
            int xc = _xCol.SelectedIndex, yc = _yCol.SelectedIndex, ec = _eCol.SelectedIndex - 1;
            var x = new List<double>(); var y = new List<double>(); var e = new List<double>();
            for (int r = 0; r < _data.Rows.Count; r++)
            {
                double xv = _data.Get(r, xc), yv = _data.Get(r, yc), ev = ec >= 0 ? _data.Get(r, ec) : double.NaN;
                if (logx) xv = xv > 0 ? Math.Log10(xv) : double.NaN;
                if (logy) { if (yv > 0 && !double.IsNaN(ev)) ev = ev / (yv * Math.Log(10)); yv = yv > 0 ? Math.Log10(yv) : double.NaN; }
                if (double.IsNaN(xv) || double.IsNaN(yv)) continue;
                x.Add(xv); y.Add(yv); e.Add(ev);
            }
            _dataPlot.Clear();
            _dataPlot.InvertY = invert;
            _dataPlot.XLabel = (logx ? "log " : "") + _data.Columns[xc];
            _dataPlot.YLabel = (logy ? "log " : "") + _data.Columns[yc];
            _dataPlot.Title = _dataPlot.YLabel + " vs " + _dataPlot.XLabel;
            var s = _dataPlot.AddSeries("data", SeriesStyle.Scatter);
            for (int i = 0; i < x.Count; i++) s.Add(x[i], y[i], e[i]);
            var sb = new System.Text.StringBuilder();
            if (x.Count > 1)
            {
                sb.AppendLine(string.Format("N = {0}", x.Count));
                sb.AppendLine(string.Format("Y mean {0:G6}, σ {1:G4}, median {2:G6}", Stats.Mean(y), Stats.StdDev(y), Stats.Median(y)));
                sb.AppendLine(string.Format("Y range {0:G6} … {1:G6}", y.Min(), y.Max()));
                double a, b, r2;
                if (Stats.LinearFit(x, y, out a, out b, out r2)) sb.AppendLine(string.Format("Pearson r = {0:0.0000}", Math.Sign(b) * Math.Sqrt(Math.Max(0, r2))));
            }
            int fit = _fit.SelectedIndex;
            if (fit > 0 && x.Count > 2)
            {
                double[] c = null;
                Func<double, double> model = null;
                var w = e.Any(v => !double.IsNaN(v) && v > 0) ? e.Select(v => !double.IsNaN(v) && v > 0 ? 1 / (v * v) : 0).ToList() : null;
                if (fit == 1 || fit == 2)
                {
                    int deg = fit == 1 ? (int)_fitDeg.Value : 1;
                    c = LinAlg.PolyFit(x, y, deg, w);
                    if (c != null) { var cc = c; model = v => LinAlg.PolyEval(cc, v); sb.AppendLine("y = " + string.Join(" + ", c.Select((k, i) => k.ToString("G6", CultureInfo.InvariantCulture) + (i == 0 ? "" : i == 1 ? "·x" : "·x^" + i)))); }
                }
                else
                {
                    // fit in log space
                    var lx = new List<double>(); var ly = new List<double>();
                    for (int i = 0; i < x.Count; i++)
                    {
                        if (y[i] <= 0 || (fit == 3 && x[i] <= 0)) continue;
                        lx.Add(fit == 3 ? Math.Log(x[i]) : x[i]); ly.Add(Math.Log(y[i]));
                    }
                    double a, b, r2;
                    if (lx.Count > 2 && Stats.LinearFit(lx, ly, out a, out b, out r2))
                    {
                        double A = Math.Exp(a), B = b;
                        model = fit == 3 ? (Func<double, double>)(v => A * Math.Pow(v, B)) : (v => A * Math.Exp(B * v));
                        sb.AppendLine(string.Format(fit == 3 ? "y = {0:G6} · x^{1:G6}" : "y = {0:G6} · e^({1:G6}·x)", A, B));
                    }
                }
                if (model != null)
                {
                    var res = x.Select((v, i) => y[i] - model(v)).ToList();
                    sb.AppendLine(string.Format("Residual rms {0:G4}", Math.Sqrt(res.Sum(r => r * r) / res.Count)));
                    var fs = _dataPlot.AddSeries("fit", SeriesStyle.Line, pal => pal.Warning);
                    double x0 = x.Min(), x1 = x.Max();
                    for (int i = 0; i <= 300; i++) { double v = x0 + (x1 - x0) * i / 300; fs.Add(v, model(v)); }
                }
            }
            _dataOut.Text = sb.ToString();
            _dataPlot.Invalidate();
        }

        public IEnumerable<AppCommand> GetCommands()
        {
            yield return new AppCommand { Title = "Scripts: run C# script", Icon = Icons.Code, Run = () => { MainForm.Instance.Navigate("scripting"); _tabs.Select(0); } };
            yield return new AppCommand { Title = "Scripts: data analysis (CSV)", Run = () => { MainForm.Instance.Navigate("scripting"); _tabs.Select(1); } };
        }
    }
}
