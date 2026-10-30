using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Sidralune.Core;
using Sidralune.Core.Catalogs;
using Sidralune.Core.Coordinates;
using Sidralune.Core.Ephemeris;
using Sidralune.Core.Mathematics;
using Sidralune.Core.Observing;
using Sidralune.Core.Time;
using Sidralune.Core.Util;
using Sidralune.UI;
using AstroEvents = Sidralune.Core.Ephemeris.Events;

namespace Sidralune.Views
{
    public class ListEntry
    {
        public string Name;
        public double RaDeg, DecDeg;
        public string Type = "", Constellation = "";
        public double Mag = double.NaN, SizeArcmin = double.NaN;
        public bool Observed;
        public string Notes = "";
        public string Planet;
    }

    public class ObservingList { public string Name = "My list"; public List<ListEntry> Entries = new List<ListEntry>(); }

    public class LogEntry
    {
        public DateTime TimeUtc;
        public string Object, Site, Instrument, Eyepiece, Notes;
        public int Seeing = 3, Transparency = 3, Rating = 3;
        public double Sqm = double.NaN;
    }

    public class PlannerData { public List<ObservingList> Lists = new List<ObservingList>(); public List<LogEntry> Log = new List<LogEntry>(); public int Active; }

    /// <summary>Observing lists, nightly planner and logbook (SkyTools-style).</summary>
    public class PlannerView : UserControl, ICommandSource
    {
        private PlannerData _data = new PlannerData();
        private static string DataPath => Path.Combine(AppPaths.Settings, "planner.json");
        // the table follows the time and the list: the newest calculation wins; a generated list is made one at a time
        private readonly JobSlot _fillJob = new JobSlot(), _generateJob = new JobSlot();
        private readonly ListBox _lists = new ListBox { Dock = DockStyle.Fill };
        private readonly DataGridView _grid = Ui.Grid();
        private readonly PlotControl _plot = new PlotControl { Dock = DockStyle.Fill, XFormat = AxisFormat.Time, Title = "Nightly planner — altitude", YLabel = "Altitude (°)" };
        private readonly TextBox _add = new TextBox { Width = 220 };
        private readonly CheckBox _onlyVisible = new CheckBox { Text = "Up tonight only", AutoSize = true };
        private readonly DataGridView _log = Ui.Grid();
        private readonly Label _logTitle = new Label { Dock = DockStyle.Top, Height = 24, Font = Theme.UiFontBold };

        public PlannerView()
        {
            Load();
            var left = new Card("Observing lists") { Dock = DockStyle.Left, Width = 240 };
            var lb = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 112 };
            Action<string, string, Action> btn = (t, i, a) => { var b = new FlatButton(t, i, (s, e) => a()); b.AutoSizeToText(); lb.Controls.Add(b); };
            btn("New", Icons.Add, NewList);
            btn("Rename", Icons.More, RenameList);
            btn("Delete", Icons.Remove, DeleteList);
            btn("Import", Icons.Open, ImportCsv);
            btn("Export", Icons.Save, ExportCsv);
            left.Controls.Add(_lists);
            left.Controls.Add(lb);
            _lists.SelectedIndexChanged += (s, e) => { if (_lists.SelectedIndex >= 0) { _data.Active = _lists.SelectedIndex; Fill(); } };

            var centre = new Panel { Dock = DockStyle.Fill };
            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 42, Padding = new Padding(0, 6, 0, 0), WrapContents = false };
            bar.Controls.Add(new Label { Text = Icons.Search, Font = Theme.IconFont, AutoSize = true, Margin = new Padding(0, 7, 2, 0) });
            bar.Controls.Add(_add);
            _add.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { AddByName(_add.Text); e.SuppressKeyPress = true; } };
            var addb = new FlatButton("Add", Icons.Add, (s, e) => AddByName(_add.Text)); addb.AutoSizeToText(); bar.Controls.Add(addb);
            var gen = new FlatButton("Generate best for tonight…", Icons.Star, (s, e) => Generate()) { Kind = ButtonKind.Primary }; gen.AutoSizeToText(); bar.Controls.Add(gen);
            // generating waits for the one running, which Stop ends
            _generateJob.Guard(gen);
            bar.Controls.Add(_generateJob.StopButton());
            var rem = new FlatButton("Remove", Icons.Remove, (s, e) => RemoveSelected()); rem.AutoSizeToText(); bar.Controls.Add(rem);
            var refresh = new FlatButton("Recalculate", Icons.Refresh, (s, e) => Fill()); refresh.AutoSizeToText(); bar.Controls.Add(refresh);
            bar.Controls.Add(_onlyVisible);
            _onlyVisible.CheckedChanged += (s, e) => Fill();
            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 330 };
            split.Panel1.Controls.Add(_grid);
            split.Panel2.Controls.Add(_plot);
            centre.Controls.Add(split);
            centre.Controls.Add(bar);
            _grid.ReadOnly = false;
            _grid.CellDoubleClick += (s, e) => { if (e.RowIndex >= 0) MainForm.Instance.ShowObject(ToSelected((ListEntry)_grid.Rows[e.RowIndex].Tag)); };
            _grid.SelectionChanged += (s, e) => { if (_filling) return; PlotSelected(); ShowLog(); };
            _grid.CellValueChanged += (s, e) =>
            {
                if (e.RowIndex < 0) return;
                var en = (ListEntry)_grid.Rows[e.RowIndex].Tag;
                if (e.ColumnIndex == 0) en.Observed = (bool)(_grid.Rows[e.RowIndex].Cells[0].Value ?? false);
                if (_grid.Columns[e.ColumnIndex].Name == "Notes") en.Notes = Convert.ToString(_grid.Rows[e.RowIndex].Cells[e.ColumnIndex].Value);
                Save();
            };

            var right = new Card("Logbook") { Dock = DockStyle.Right, Width = 330 };
            var logBar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40 };
            var addLog = new FlatButton("Log observation…", Icons.Add, (s, e) => AddLog()) { Kind = ButtonKind.Primary }; addLog.AutoSizeToText();
            var expLog = new FlatButton("Export", Icons.Save, (s, e) => ExportLog()); expLog.AutoSizeToText();
            logBar.Controls.Add(addLog); logBar.Controls.Add(expLog);
            right.Controls.Add(_log);
            right.Controls.Add(_logTitle);
            right.Controls.Add(logBar);

            Controls.Add(centre);
            Controls.Add(right);
            Controls.Add(new Panel { Dock = DockStyle.Left, Width = 8 });
            Controls.Add(left);
            Padding = new Padding(8);
            ReloadLists();
            AppState.SiteChanged += (s, e) => Fill();
        }

        private new void Load()
        {
            try { if (File.Exists(DataPath)) _data = (PlannerData)Json.PopulateType(typeof(PlannerData), Json.Parse(File.ReadAllText(DataPath))) ?? new PlannerData(); }
            catch (Exception ex) { Log.Warn("Planner", ex.Message); }
            if (_data.Lists.Count == 0)
            {
                var m = new ObservingList { Name = "Messier catalogue" };
                foreach (var d in CatalogManager.Instance.DeepSkyList.Where(d => d.Messier != null).OrderBy(d => int.Parse(d.Messier.Substring(1))))
                    m.Entries.Add(FromSky(SelectedObject.From(d)));
                _data.Lists.Add(m);
                _data.Lists.Add(new ObservingList { Name = "Tonight" });
            }
        }

        private void Save() { try { File.WriteAllText(DataPath, Json.Serialize(_data, true)); } catch (Exception ex) { Log.Warn("Planner", ex.Message); } }

        private ObservingList Active => _data.Lists[Math.Max(0, Math.Min(_data.Active, _data.Lists.Count - 1))];

        private void ReloadLists()
        {
            _lists.Items.Clear();
            foreach (var l in _data.Lists) _lists.Items.Add(l.Name + "  (" + l.Entries.Count + ")");
            _lists.SelectedIndex = Math.Max(0, Math.Min(_data.Active, _lists.Items.Count - 1));
        }

        private static ListEntry FromSky(SelectedObject o)
        {
            var e = new ListEntry { Name = o.Name, RaDeg = o.RaDeg, DecDeg = o.DecDeg, Mag = o.Mag, SizeArcmin = o.SizeArcmin, Planet = o.Planet.HasValue ? o.Planet.Value.ToString() : null };
            if (o.Catalog != null) { e.Type = o.Catalog is Star ? "Star" : DeepSkyObject.TypeLabel(o.Catalog.Type); e.Constellation = o.Catalog.Constellation ?? ""; }
            else e.Type = o.Kind.ToString();
            return e;
        }

        private static SelectedObject ToSelected(ListEntry e)
        {
            Body b;
            if (e.Planet != null && Enum.TryParse(e.Planet, out b)) return SkyView.PlanetObject(b);
            var cat = CatalogManager.Instance.Find(e.Name);
            if (cat != null) return SelectedObject.From(cat);
            return new SelectedObject { Kind = SelectedKind.Coordinates, Name = e.Name, RaDeg = e.RaDeg, DecDeg = e.DecDeg, Mag = e.Mag, SizeArcmin = e.SizeArcmin };
        }

        public void AddObject(SelectedObject o)
        {
            if (o == null) return;
            if (Active.Entries.Any(e => e.Name == o.Name)) return;
            Active.Entries.Add(FromSky(o));
            Save(); ReloadLists(); Fill();
        }

        private void AddByName(string q)
        {
            if (string.IsNullOrWhiteSpace(q)) return;
            Body b;
            if (Enum.TryParse(q.Trim(), true, out b)) { AddObject(SkyView.PlanetObject(b)); _add.Clear(); return; }
            var o = CatalogManager.Instance.Find(q) ?? CatalogManager.Instance.Search(q, 1).FirstOrDefault();
            if (o == null) { MainForm.Instance.Toast("Not found: " + q, Icons.Warning); return; }
            AddObject(SelectedObject.From(o));
            _add.Clear();
        }

        private void RemoveSelected()
        {
            foreach (DataGridViewRow r in _grid.SelectedRows) Active.Entries.Remove((ListEntry)r.Tag);
            Save(); ReloadLists(); Fill();
        }

        private void NewList()
        {
            var name = Prompt("New observing list", "Name:", "List " + (_data.Lists.Count + 1));
            if (string.IsNullOrWhiteSpace(name)) return;
            _data.Lists.Add(new ObservingList { Name = name });
            _data.Active = _data.Lists.Count - 1;
            Save(); ReloadLists();
        }

        private void RenameList()
        {
            var name = Prompt("Rename list", "Name:", Active.Name);
            if (string.IsNullOrWhiteSpace(name)) return;
            Active.Name = name; Save(); ReloadLists();
        }

        private void DeleteList()
        {
            if (_data.Lists.Count <= 1) return;
            if (MessageBox.Show(this, "Delete list '" + Active.Name + "'?", "Delete", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
            _data.Lists.Remove(Active); _data.Active = 0; Save(); ReloadLists();
        }

        public static string Prompt(string title, string label, string value)
        {
            using (var f = new Form { Text = title, Width = 380, Height = 150, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent, MaximizeBox = false, MinimizeBox = false })
            {
                var tb = new TextBox { Text = value, Left = 12, Top = 32, Width = 340 };
                var l = new Label { Text = label, Left = 12, Top = 10, AutoSize = true };
                var ok = new FlatButton("OK", null) { Left = 196, Top = 66, Width = 75, Kind = ButtonKind.Primary, DialogResult = DialogResult.OK };
                var cancel = new FlatButton("Cancel", null) { Left = 277, Top = 66, Width = 75, DialogResult = DialogResult.Cancel };
                ok.Click += (s, e) => { f.DialogResult = DialogResult.OK; f.Close(); };
                cancel.Click += (s, e) => { f.DialogResult = DialogResult.Cancel; f.Close(); };
                f.Controls.AddRange(new Control[] { l, tb, ok, cancel });
                f.AcceptButton = ok;
                Theme.Apply(f);
                f.HandleCreated += (s, e) => Theme.DarkTitleBar(f);
                return f.ShowDialog() == DialogResult.OK ? tb.Text : null;
            }
        }

        private void Fill()
        {
            var site = AppState.Site;
            var now = AppState.Now;
            var local = site.ToLocal(now.ToDateTimeUtc());
            var noon = AstroTime.FromDateTime(site.ToUtc(local.Date.AddHours(local.Hour < 12 ? -12 : 12)));
            var tw = AstroEvents.Twilight(site, noon);
            var entries = Active.Entries.ToList();
            var ins = AppState.Instrument; var eye = AppState.Eye;
            _fillJob.Replace("Planning " + Active.Name, t =>
            {
                var rows = new List<object[]>();
                var ctxNow = new SkyContext(site, now);
                var moon = ctxNow.BodyPlace(Body.Moon);
                var start = tw.AstronomicalDusk ?? tw.NauticalDusk ?? tw.SunSet ?? noon.AddHours(8);
                var end = tw.AstronomicalDawn ?? tw.NauticalDawn ?? tw.SunRise ?? noon.AddHours(18);
                var samples = new List<AstroTime>();
                for (var x = start; x <= end; x = x.AddMinutes(30)) samples.Add(x);
                var ctxs = samples.Select(x => new SkyContext(site, x)).ToList();
                var moons = ctxs.Select(c => c.BodyPlace(Body.Moon)).ToList();
                Func<AstroTime?, string> hm = x => x.HasValue ? site.ToLocal(x.Value.ToDateTimeUtc()).ToString("HH:mm") : "—";
                int k = 0;
                foreach (var e in entries)
                {
                    t.Cts.Token.ThrowIfCancellationRequested(); // replaced by a newer calculation
                    k++;
                    if (k % 10 == 0) Tasks.Report(t, (double)k / entries.Count);
                    var so = ToSelected(e);
                    double ra = so.RaDeg * AstroMath.Deg2Rad, dec = so.DecDeg * AstroMath.Deg2Rad;
                    RiseSetResult rs;
                    try { rs = so.Planet.HasValue ? AstroEvents.BodyRiseSet(so.Planet.Value, site, noon) : AstroEvents.FixedRiseSet(ra, dec, site, noon); } catch { rs = new RiseSetResult(); }
                    var hz = ctxNow.IcrsToHorizontal(ra, dec);
                    double best = double.MinValue, bestAlt = -90; AstroTime? bestT = null; Detectability rating = Detectability.NotVisible; double bestMag = double.NaN;
                    double maxAlt = -90;
                    for (int i = 0; i < ctxs.Count; i++)
                    {
                        var h = ctxs[i].IcrsToHorizontal(ra, dec);
                        if (h.AltDeg > maxAlt) maxAlt = h.AltDeg;
                        if (h.AltDeg < 10) continue;
                        if (double.IsNaN(e.Mag) || so.Planet.HasValue) { if (h.AltDeg > bestAlt) { bestAlt = h.AltDeg; bestT = samples[i]; } continue; }
                        double sep = AstroMath.Separation(ra, dec, moons[i].Astrometric.Ra, moons[i].Astrometric.Dec);
                        double sky = VisibilityModel.SkyBrightness(site, h.Alt, AstroEvents.SunAltitudeFast(site, samples[i]), moons[i].Horizontal.Alt, moons[i].PhaseAngle * AstroMath.Rad2Deg, sep);
                        var v = VisibilityModel.Predict(e.Mag, double.IsNaN(e.SizeArcmin) ? 0 : e.SizeArcmin, h.Alt, sky, site, ins, eye);
                        if (v.BestMargin > best) { best = v.BestMargin; bestT = samples[i]; bestAlt = h.AltDeg; rating = v.Rating; bestMag = v.BestMagnification; }
                    }
                    if (_onlyVisible.Checked && maxAlt < 10) continue;
                    double moonSep = AstroMath.Separation(ra, dec, moon.Astrometric.Ra, moon.Astrometric.Dec) * AstroMath.Rad2Deg;
                    rows.Add(new object[] { e, e.Observed, e.Name, e.Type, e.Constellation, double.IsNaN(e.Mag) ? "" : e.Mag.ToString("0.0"),
                        double.IsNaN(e.SizeArcmin) ? "" : AstroMath.FormatAngleAuto(e.SizeArcmin / 60),
                        rs.AlwaysUp ? "circ." : hm(rs.Rise), hm(rs.Transit), rs.AlwaysUp ? "circ." : hm(rs.Set), bestT.HasValue ? hm(bestT) : "—",
                        hz.AltDeg.ToString("0") + "°", best > double.MinValue ? rating.ToString() : (so.Planet.HasValue ? "planet" : "—"),
                        double.IsNaN(bestMag) ? "" : "×" + bestMag.ToString("0"), moonSep.ToString("0") + "°", e.Notes });
                }
                MainForm.Instance.Post(() => { if (!t.Cts.IsCancellationRequested) ShowRows(rows, tw, noon); });
            });
        }

        private void ShowRows(List<object[]> rows, TwilightTimes tw, AstroTime noon)
        {
            _grid.SuspendLayout();
            // adding the first row makes it current and raises SelectionChanged; plot once when the rows are complete
            _filling = true;
            _grid.Rows.Clear();
            if (_grid.Columns.Count == 0)
            {
                _grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Obs", HeaderText = "✓", FillWeight = 12 });
                foreach (var c in new[] { "Object", "Type", "Con", "Mag", "Size", "Rise", "Transit", "Set", "Best", "Alt now", "Visibility", "Best ×", "Moon", "Notes" }) _grid.Columns.Add(c, c);
                for (int i = 1; i < _grid.Columns.Count - 1; i++) _grid.Columns[i].ReadOnly = true;
                _grid.Columns["Object"].FillWeight = 140; _grid.Columns["Type"].FillWeight = 90; _grid.Columns["Notes"].FillWeight = 120;
            }
            try
            {
                foreach (var r in rows)
                {
                    var row = new DataGridViewRow { Tag = r[0] };
                    row.CreateCells(_grid, r.Skip(1).ToArray());
                    _grid.Rows.Add(row);
                }
            }
            finally { _filling = false; _grid.ResumeLayout(); }
            _twilight = tw; _noon = noon;
            PlotSelected();
            ShowLog();
        }

        private bool _filling;
        private TwilightTimes _twilight;
        private AstroTime _noon;

        private void PlotSelected()
        {
            _plot.Clear();
            if (_twilight == null) return;
            var site = AppState.Site;
            // UTC on the axis, labelled with the site's clock: a daylight saving change must not fold the curves
            _plot.TimeZone = site.TimeZone;
            Action<AstroTime?, AstroTime?, int> band = (a, b, alpha) => { if (a.HasValue && b.HasValue) _plot.Bands.Add(new PlotBand { X0 = a.Value.ToDateTimeUtc().ToOADate(), X1 = b.Value.ToDateTimeUtc().ToOADate(), ThemeColor = pal => Theme.WithAlpha(pal.Accent, alpha) }); };
            band(_twilight.SunSet, _twilight.SunRise, 16); band(_twilight.NauticalDusk, _twilight.NauticalDawn, 16); band(_twilight.AstronomicalDusk, _twilight.AstronomicalDawn, 22);
            var sel = _grid.SelectedRows.Cast<DataGridViewRow>().Select(r => r.Tag as ListEntry).Where(e => e != null).Take(8).ToList();
            if (sel.Count == 0) sel = _grid.Rows.Cast<DataGridViewRow>().Select(r => r.Tag as ListEntry).Where(e => e != null).Take(5).ToList();
            var moonS = _plot.AddSeries("Moon", SeriesStyle.Line, pal => pal.Moon);
            moonS.DashDot = true;
            for (int i = 0; i <= 96; i++)
            {
                var t = _noon.AddMinutes(i * 15);
                moonS.Add(t.ToDateTimeUtc().ToOADate(), AstroEvents.BodyAltitude(Body.Moon, site, t));
            }
            foreach (var e in sel)
            {
                var so = ToSelected(e);
                var s = _plot.AddSeries(e.Name);
                for (int i = 0; i <= 96; i++)
                {
                    var t = _noon.AddMinutes(i * 15);
                    double alt = so.Planet.HasValue ? AstroEvents.BodyAltitude(so.Planet.Value, site, t) : AstroEvents.FixedAltitude(so.RaDeg * AstroMath.Deg2Rad, so.DecDeg * AstroMath.Deg2Rad, site, t);
                    s.Add(t.ToDateTimeUtc().ToOADate(), alt);
                }
            }
            _plot.FixedYMin = 0; _plot.FixedYMax = 90;
            _plot.Markers.Add(new PlotMarker { X = AppState.Now.ToDateTimeUtc().ToOADate(), Label = "now" });
            _plot.Markers.Add(new PlotMarker { X = 30, Horizontal = true, Label = "30°", ThemeColor = pal => pal.TextFaint });
            _plot.Invalidate();
        }

        private void ShowLog()
        {
            var e = _grid.SelectedRows.Count > 0 ? _grid.SelectedRows[0].Tag as ListEntry : null;
            _logTitle.Text = e != null ? e.Name : "Select an object";
            _log.Rows.Clear();
            if (_log.Columns.Count == 0) foreach (var c in new[] { "Date", "Rating", "Notes" }) _log.Columns.Add(c, c);
            if (e == null) return;
            foreach (var l in _data.Log.Where(l => l.Object == e.Name).OrderByDescending(l => l.TimeUtc))
                _log.Rows.Add(l.TimeUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"), new string('★', l.Rating), l.Instrument + ": " + l.Notes);
        }

        private void AddLog()
        {
            var e = _grid.SelectedRows.Count > 0 ? _grid.SelectedRows[0].Tag as ListEntry : null;
            if (e == null) return;
            var entry = new LogEntry { TimeUtc = DateTime.UtcNow, Object = e.Name, Site = AppState.Site.Name, Instrument = AppState.Instrument.Name, Eyepiece = AppState.Instrument.EyepieceFocalMm + " mm", Sqm = AppState.Site.SqmZenith, Notes = "" };
            using (var f = new Form { Text = "Log observation — " + e.Name, Width = 460, Height = 420, StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog })
            {
                var pg = new PropertyGrid { Dock = DockStyle.Fill, SelectedObject = new GridAdapter(entry, "Observation"), HelpVisible = false };
                var ok = new FlatButton("Save", Icons.Save) { Dock = DockStyle.Bottom, Kind = ButtonKind.Primary, Height = 36 };
                ok.Click += (s, a) => { f.DialogResult = DialogResult.OK; f.Close(); };
                f.Controls.Add(pg); f.Controls.Add(ok);
                Theme.Apply(f);
                f.HandleCreated += (s, a) => Theme.DarkTitleBar(f);
                if (f.ShowDialog(this) != DialogResult.OK) return;
            }
            _data.Log.Add(entry);
            e.Observed = true;
            Save(); ShowLog(); Fill();
        }

        private void ExportLog()
        {
            using (var d = new SaveFileDialog { Filter = "CSV|*.csv", FileName = "logbook.csv" })
                if (d.ShowDialog() == DialogResult.OK)
                    CsvWriter.Write(d.FileName, new[] { "UTC", "Object", "Site", "Instrument", "Eyepiece", "Seeing", "Transparency", "SQM", "Rating", "Notes" },
                        _data.Log.Select(l => (IList<object>)new object[] { l.TimeUtc, l.Object, l.Site, l.Instrument, l.Eyepiece, l.Seeing, l.Transparency, l.Sqm, l.Rating, l.Notes }));
        }

        private void ImportCsv()
        {
            using (var d = new OpenFileDialog { Filter = "CSV / text|*.csv;*.txt|All|*.*" })
            {
                if (d.ShowDialog() != DialogResult.OK) return;
                var list = new ObservingList { Name = Path.GetFileNameWithoutExtension(d.FileName) };
                foreach (var line in File.ReadAllLines(d.FileName))
                {
                    var name = line.Split(',', ';', '\t')[0].Trim().Trim('"');
                    if (name.Length == 0 || name.Equals("name", StringComparison.OrdinalIgnoreCase) || name.Equals("object", StringComparison.OrdinalIgnoreCase)) continue;
                    var o = CatalogManager.Instance.Find(name);
                    if (o != null) list.Entries.Add(FromSky(SelectedObject.From(o)));
                }
                _data.Lists.Add(list); _data.Active = _data.Lists.Count - 1;
                Save(); ReloadLists();
                MainForm.Instance.Toast(string.Format("Imported {0} objects", list.Entries.Count));
            }
        }

        private void ExportCsv()
        {
            using (var d = new SaveFileDialog { Filter = "CSV|*.csv", FileName = Active.Name + ".csv" })
                if (d.ShowDialog() == DialogResult.OK)
                    CsvWriter.Write(d.FileName, new[] { "Name", "Type", "Constellation", "RA", "Dec", "Mag", "Size_arcmin", "Observed", "Notes" },
                        Active.Entries.Select(e => (IList<object>)new object[] { e.Name, e.Type, e.Constellation, AstroMath.FormatHms(e.RaDeg / 15, 1, true), AstroMath.FormatDms(e.DecDeg, 0, true, true), e.Mag, e.SizeArcmin, e.Observed, e.Notes }));
        }

        private void Generate()
        {
            if (_generateJob.Refuse()) return;
            var types = new[] { "All deep-sky", "Galaxies", "Nebulae", "Clusters", "Planetary nebulae", "Double stars" };
            var choice = Prompt("Generate observing list", "Type (All deep-sky, Galaxies, Nebulae, Clusters, Planetary nebulae):", "All deep-sky");
            if (choice == null) return;
            var site = AppState.Site;
            var now = AppState.Now;
            var local = site.ToLocal(now.ToDateTimeUtc());
            var noon = AstroTime.FromDateTime(site.ToUtc(local.Date.AddHours(local.Hour < 12 ? -12 : 12)));
            var ins = AppState.Instrument; var eye = AppState.Eye;
            ObservingList list = null;
            _generateJob.Run("Generating list", t =>
            {
                var tw = AstroEvents.Twilight(site, noon);
                var start = tw.AstronomicalDusk ?? tw.NauticalDusk ?? noon.AddHours(8);
                var end = tw.AstronomicalDawn ?? tw.NauticalDawn ?? noon.AddHours(18);
                var samples = new List<AstroTime>();
                for (var x = start; x <= end; x = x.AddHours(1)) samples.Add(x);
                var ctxs = samples.Select(x => new SkyContext(site, x)).ToList();
                var moons = ctxs.Select(c => c.BodyPlace(Body.Moon)).ToList();
                Func<DeepSkyObject, bool> filter = d =>
                {
                    var c = choice.ToLowerInvariant();
                    if (c.StartsWith("gal")) return d.Type == ObjectType.Galaxy || d.Type == ObjectType.GalaxyPair || d.Type == ObjectType.GalaxyGroup;
                    if (c.StartsWith("neb")) return d.Type == ObjectType.EmissionNebula || d.Type == ObjectType.ReflectionNebula || d.Type == ObjectType.SupernovaRemnant || d.Type == ObjectType.ClusterNebula || d.Type == ObjectType.HIIRegion;
                    if (c.StartsWith("clu")) return d.Type == ObjectType.OpenCluster || d.Type == ObjectType.GlobularCluster;
                    if (c.StartsWith("plan")) return d.Type == ObjectType.PlanetaryNebula;
                    return true;
                };
                var scored = new List<Tuple<double, DeepSkyObject>>();
                foreach (var o in CatalogManager.Instance.DeepSkyList.Where(o => !double.IsNaN(o.Mag) && o.Mag < 13 && filter(o)))
                {
                    t.Cts.Token.ThrowIfCancellationRequested();
                    double size = double.IsNaN(o.MajorAxis) ? 1 : o.MajorAxis;
                    double best = double.MinValue;
                    for (int i = 0; i < ctxs.Count; i++)
                    {
                        var h = ctxs[i].IcrsToHorizontal(o.Ra, o.Dec);
                        if (h.AltDeg < 25) continue;
                        double sep = AstroMath.Separation(o.Ra, o.Dec, moons[i].Astrometric.Ra, moons[i].Astrometric.Dec);
                        double sky = VisibilityModel.SkyBrightness(site, h.Alt, AstroEvents.SunAltitudeFast(site, samples[i]), moons[i].Horizontal.Alt, moons[i].PhaseAngle * AstroMath.Rad2Deg, sep);
                        best = Math.Max(best, VisibilityModel.Predict(o.Mag, size, h.Alt, sky, site, ins, eye).BestMargin);
                    }
                    if (best > 0) scored.Add(Tuple.Create(best, o));
                }
                list = new ObservingList { Name = choice + " " + local.ToString("yyyy-MM-dd") };
                foreach (var s in scored.OrderByDescending(x => x.Item1).Take(60)) list.Entries.Add(FromSky(SelectedObject.From(s.Item2)));
            }, () =>
            {
                // added once done, so a stopped run adds nothing
                _data.Lists.Add(list); _data.Active = _data.Lists.Count - 1; Save(); ReloadLists();
                MainForm.Instance.Toast(list.Entries.Count + " objects selected for tonight");
            });
        }

        public IEnumerable<AppCommand> GetCommands()
        {
            yield return new AppCommand { Title = "Planner: generate tonight's best list", Run = () => { MainForm.Instance.Navigate("planner"); Generate(); } };
            yield return new AppCommand { Title = "Planner: new observing list", Run = NewList };
        }
    }
}
