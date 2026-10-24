using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using Sidralune.Core;
using Sidralune.Core.Catalogs;
using Sidralune.Core.Mathematics;
using Sidralune.Core.Util;
using Sidralune.Science.Databases;
using Sidralune.UI;

namespace Sidralune.Views
{
    /// <summary>
    /// Catalogue browser (local catalogues with filters), online astronomical databases (Sesame, SIMBAD, VizieR TAP,
    /// AAVSO VSX, NASA Exoplanet Archive, JPL) with a free-form ADQL console, and a directory of citizen-science projects.
    /// </summary>
    public class DatabaseView : UserControl, ICommandSource
    {
        private readonly TabbedPanel _tabs = new TabbedPanel { Dock = DockStyle.Fill, Padding = new Padding(10) };
        private DataTable _last;

        public DatabaseView()
        {
            Controls.Add(_tabs);
            _tabs.AddPage("Local catalogues", BuildLocal());
            _tabs.AddPage("Online databases", BuildOnline());
            _tabs.AddPage("Amateur science", BuildProjects());
        }

        // ================================================================== local

        private Control BuildLocal()
        {
            var panel = new Panel { Dock = DockStyle.Fill };
            var kind = Ui.Combo("Deep-sky objects", "Stars", "Named stars", "Constellations");
            var type = Ui.Combo(new object[] { "Any type" }.Concat(Enum.GetValues(typeof(ObjectType)).Cast<object>()).ToArray());
            var con = new TextBox { Width = 60 };
            var mag = Ui.Num(12, -2, 20, 1, 0.5m);
            var minSize = Ui.Num(0, 0, 600, 1, 1);
            var text = new TextBox { Width = 160 };
            var grid = Ui.Grid();
            var count = new Label { AutoSize = true, Tag = "dim", Margin = new Padding(8, 8, 0, 0) };
            var go = new FlatButton("Search", Icons.Search, null) { Kind = ButtonKind.Primary };
            go.AutoSizeToText();
            go.Click += (s, e) =>
            {
                grid.Columns.Clear(); grid.Rows.Clear();
                var ctx = new SkyContext(AppState.Site, AppState.Now);
                string q = text.Text.Trim(), cn = con.Text.Trim();
                double mlim = (double)mag.Value, smin = (double)minSize.Value;
                ObjectType? ot = type.SelectedIndex > 0 ? (ObjectType?)type.SelectedItem : null;
                Func<SkyObject, bool> common = o => (q.Length == 0 || o.DisplayName.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 || (o.Name ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                    && (cn.Length == 0 || string.Equals(o.Constellation, cn, StringComparison.OrdinalIgnoreCase))
                    && (double.IsNaN(o.Mag) || o.Mag <= mlim) && (ot == null || o.Type == ot.Value);
                if (kind.SelectedIndex == 0)
                {
                    foreach (var c in new[] { "Object", "Type", "Const.", "RA", "Dec", "Mag", "Size (′)", "SB", "Alt now", "Notes" }) grid.Columns.Add(c, c);
                    var res = CatalogManager.Instance.DeepSky.All().Where(d => common(d) && (smin == 0 || d.MajorAxis >= smin)).OrderBy(d => double.IsNaN(d.Mag) ? 99 : d.Mag).Take(5000).ToList();
                    foreach (var d in res)
                    {
                        int k = grid.Rows.Add(d.DisplayName, d.Type, d.Constellation, AstroMath.FormatHms(d.RaHours, 0, true), AstroMath.FormatDms(d.DecDeg, 0, true, true), F(d.Mag, "0.0"),
                            double.IsNaN(d.MajorAxis) ? "" : F(d.MajorAxis, "0.0") + (double.IsNaN(d.MinorAxis) ? "" : "×" + F(d.MinorAxis, "0.0")), F(d.SurfaceBrightness, "0.0"),
                            F(ctx.IcrsToHorizontal(d.Ra, d.Dec).AltDeg, "0"), string.Join(", ", d.AltNames.Take(3)));
                        grid.Rows[k].Tag = d;
                    }
                    count.Text = res.Count + " objects";
                }
                else if (kind.SelectedIndex == 1 || kind.SelectedIndex == 2)
                {
                    foreach (var c in new[] { "Star", "Const.", "RA", "Dec", "V", "B−V", "Spectral type", "Parallax (mas)", "Variable", "Alt now" }) grid.Columns.Add(c, c);
                    IEnumerable<Star> src = kind.SelectedIndex == 2 ? (IEnumerable<Star>)CatalogManager.Instance.NamedStars : CatalogManager.Instance.Stars.All();
                    var res = src.Where(st => common(st)).OrderBy(st => st.Mag).Take(5000).ToList();
                    foreach (var st in res)
                    {
                        int k = grid.Rows.Add(st.DisplayName, st.Constellation, AstroMath.FormatHms(st.RaHours, 1, true), AstroMath.FormatDms(st.DecDeg, 0, true, true), F(st.Mag, "0.00"), F(st.BV, "0.00"),
                            st.SpectralType, F(st.Parallax, "0.00"), st.VariableType, F(ctx.IcrsToHorizontal(st.Ra, st.Dec).AltDeg, "0"));
                        grid.Rows[k].Tag = st;
                    }
                    count.Text = res.Count + " stars" + (res.Count == 5000 ? " (first 5000)" : "");
                }
                else
                {
                    foreach (var c in new[] { "Abbr.", "Name", "Genitive", "Label RA", "Label Dec", "Alt now" }) grid.Columns.Add(c, c);
                    foreach (var c in CatalogManager.Instance.Constellations.Where(c => q.Length == 0 || c.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0))
                        grid.Rows.Add(c.Abbr, c.Name, c.Genitive, AstroMath.FormatHms(c.LabelRa * AstroMath.Rad2Hour, 0, true), AstroMath.FormatDms(c.LabelDec * AstroMath.Rad2Deg, 0, true, true), F(ctx.IcrsToHorizontal(c.LabelRa, c.LabelDec).AltDeg, "0"));
                    count.Text = grid.Rows.Count + " constellations";
                }
            };
            grid.CellDoubleClick += (s, e) =>
            {
                if (e.RowIndex < 0) return;
                var o = grid.Rows[e.RowIndex].Tag as SkyObject;
                if (o == null) return;
                AppState.Select(SelectedObject.From(o));
                MainForm.Instance.Navigate("sky");
            };
            var toPlan = new FlatButton("Add to plan", Icons.Planner, (s, e) =>
            {
                int n = 0;
                foreach (DataGridViewRow r in grid.SelectedRows) { var o = r.Tag as SkyObject; if (o != null) { MainForm.Instance.GetView<PlannerView>("planner").AddObject(SelectedObject.From(o)); n++; } }
                MainForm.Instance.Toast(n + " objects added to the observing plan");
            });
            toPlan.AutoSizeToText();
            var csv = new FlatButton("CSV…", Icons.Save, (s, e) => GridCsv(grid)) { Kind = ButtonKind.Subtle };
            csv.AutoSizeToText();
            panel.Controls.Add(grid);
            panel.Controls.Add(Ui.Toolbar(kind, type, new Label { Text = "Const.", AutoSize = true, Margin = new Padding(4, 6, 2, 0) }, con,
                new Label { Text = "Mag ≤", AutoSize = true, Margin = new Padding(4, 6, 2, 0) }, mag, new Label { Text = "Size ≥ (′)", AutoSize = true, Margin = new Padding(4, 6, 2, 0) }, minSize,
                new Label { Text = "Name", AutoSize = true, Margin = new Padding(4, 6, 2, 0) }, text, go, toPlan, csv, count));
            return panel;
        }

        private static string F(double v, string fmt) => double.IsNaN(v) ? "" : v.ToString(fmt, CultureInfo.InvariantCulture);

        private static void GridCsv(DataGridView g)
        {
            if (g.Rows.Count == 0) return;
            using (var d = new SaveFileDialog { Filter = "CSV|*.csv", FileName = "query.csv" })
                if (d.ShowDialog() == DialogResult.OK)
                    CsvWriter.Write(d.FileName, g.Columns.Cast<DataGridViewColumn>().Select(c => c.HeaderText).ToList(),
                        g.Rows.Cast<DataGridViewRow>().Select(r => (IList<object>)r.Cells.Cast<DataGridViewCell>().Select(c => c.Value).ToList()));
        }

        // ================================================================== online

        private DataGridView _onGrid;
        private TextBox _adql, _onInfo;
        private ComboBox _service;

        private static readonly string[][] Examples =
        {
            new[] { "SIMBAD: objects within 0.5° of M 42", "simbad", "SELECT TOP 200 main_id, ra, dec, otype_txt, sp_type FROM basic WHERE CONTAINS(POINT('ICRS', ra, dec), CIRCLE('ICRS', 83.82, -5.39, 0.5)) = 1" },
            new[] { "SIMBAD: Mira variables brighter than V=8", "simbad", "SELECT TOP 200 main_id, ra, dec, flux AS V FROM basic JOIN flux ON oid = oidref WHERE filter = 'V' AND flux < 8 AND otype = 'Mi*' ORDER BY flux" },
            new[] { "VizieR: Gaia DR3 stars G<12 near Vega", "vizier", "SELECT TOP 500 Source, RA_ICRS, DE_ICRS, Gmag, BP-RP AS bp_rp, Plx FROM \"I/355/gaiadr3\" WHERE Gmag < 12 AND 1 = CONTAINS(POINT('ICRS', RA_ICRS, DE_ICRS), CIRCLE('ICRS', 279.2347, 38.7837, 1))" },
            new[] { "VizieR: Washington Double Star catalogue near Albireo", "vizier", "SELECT TOP 100 * FROM \"B/wds/wds\" WHERE 1 = CONTAINS(POINT('ICRS', RAJ2000, DEJ2000), CIRCLE('ICRS', 292.68, 27.96, 0.5))" },
            new[] { "Exoplanet Archive: transiting planets with host V < 10", "exo", "select pl_name, hostname, sy_vmag, pl_orbper, pl_trandep, pl_trandur from pscomppars where tran_flag = 1 and sy_vmag < 10 order by sy_vmag" },
        };

        private Control BuildOnline()
        {
            var panel = new Panel { Dock = DockStyle.Fill };
            var top = new Card("Query") { Dock = DockStyle.Top, Height = 250 };
            var g = new FormGrid(110);
            var name = new TextBox();
            var resolve = new FlatButton("Resolve name (Sesame)", Icons.Search, (s, e) => Resolve(name.Text)) { Kind = ButtonKind.Primary };
            resolve.AutoSizeToText();
            var vsx = new FlatButton("AAVSO VSX", Icons.Photometry, (s, e) => Vsx(name.Text));
            vsx.AutoSizeToText();
            var cone = new FlatButton("SIMBAD cone (selection)", Icons.Target, (s, e) =>
            {
                var o = AppState.Selection;
                if (o == null) { MainForm.Instance.Toast("Select an object first", Icons.Info); return; }
                RunTable("SIMBAD cone", () => OnlineServices.SimbadCone(o.RaDeg, o.DecDeg, 0.3));
            });
            cone.AutoSizeToText();
            var nameRow = new TableLayoutPanel { ColumnCount = 4, Height = 36, Dock = DockStyle.Top };
            nameRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 3; i++) nameRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            nameRow.Controls.Add(name, 0, 0); nameRow.Controls.Add(resolve, 1, 0); nameRow.Controls.Add(vsx, 2, 0); nameRow.Controls.Add(cone, 3, 0);
            name.Dock = DockStyle.Fill;
            g.Row("Object", nameRow);
            _service = g.Row("ADQL service", Ui.Combo("SIMBAD TAP", "VizieR TAP", "NASA Exoplanet Archive TAP"));
            var ex = g.Row("Examples", Ui.Combo(Examples.Select(x => (object)x[0]).ToArray()));
            _adql = new TextBox { Multiline = true, Height = 70, Font = Theme.MonoFont, ScrollBars = ScrollBars.Vertical, Text = Examples[0][2] };
            g.Row("ADQL", _adql);
            ex.SelectedIndexChanged += (s, e) => { var x = Examples[ex.SelectedIndex]; _adql.Text = x[2]; _service.SelectedIndex = x[1] == "simbad" ? 0 : x[1] == "vizier" ? 1 : 2; };
            var run = new FlatButton("Run query", Icons.Play, (s, e) => RunAdql()) { Kind = ButtonKind.Primary };
            run.AutoSizeToText();
            var csv = new FlatButton("CSV…", Icons.Save, (s, e) => GridCsv(_onGrid)) { Kind = ButtonKind.Subtle };
            csv.AutoSizeToText();
            var plot = new FlatButton("Plot columns…", Icons.Chart, (s, e) => PlotColumns()) { Kind = ButtonKind.Subtle };
            plot.AutoSizeToText();
            var bar = new FlowLayoutPanel { Height = 38 }; bar.Controls.Add(run); bar.Controls.Add(csv); bar.Controls.Add(plot);
            g.Full(bar);
            top.Controls.Add(g);
            _onGrid = Ui.Grid();
            _onInfo = new TextBox { Dock = DockStyle.Right, Width = 360, Multiline = true, ReadOnly = true, Font = Theme.MonoFont, ScrollBars = ScrollBars.Vertical };
            _onGrid.CellDoubleClick += (s, e) =>
            {
                if (e.RowIndex < 0 || _last == null) return;
                int rc = _last.Columns.FindIndex(c => c.Equals("ra", StringComparison.OrdinalIgnoreCase) || c.StartsWith("RA", StringComparison.OrdinalIgnoreCase));
                int dc = _last.Columns.FindIndex(c => c.Equals("dec", StringComparison.OrdinalIgnoreCase) || c.StartsWith("DE", StringComparison.OrdinalIgnoreCase));
                if (rc < 0 || dc < 0) return;
                double ra = AstroMath.ParseInvariant(_last.Rows[e.RowIndex][rc]), dec = AstroMath.ParseInvariant(_last.Rows[e.RowIndex][dc]);
                if (double.IsNaN(ra) || double.IsNaN(dec)) return;
                AppState.Select(new SelectedObject { Kind = SelectedKind.Coordinates, Name = _last.Rows[e.RowIndex][0], RaDeg = ra, DecDeg = dec });
                MainForm.Instance.Navigate("sky");
            };
            panel.Controls.Add(_onGrid);
            panel.Controls.Add(_onInfo);
            panel.Controls.Add(top);
            return panel;
        }

        // the online lookups share one result area: the newest request wins
        private readonly JobSlot _query = new JobSlot();

        private void Resolve(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            ResolvedName r = null;
            _query.Replace("Sesame", t => r = OnlineServices.Sesame(name), () =>
            {
                if (r == null || double.IsNaN(r.RaDeg)) { _onInfo.Text = "Not resolved: " + name; return; }
                _onInfo.Text = string.Format("{0}\r\nType: {1}\r\nRA  {2}\r\nDec {3}\r\nV = {4}\r\nSp. {5}\r\n\r\nAliases:\r\n{6}", r.Name, r.Type, AstroMath.FormatHms(r.RaDeg / 15, 2, true), AstroMath.FormatDms(r.RaDeg == r.RaDeg ? r.DecDeg : 0, 1, true, true),
                    F(r.Vmag, "0.00"), r.SpectralType, string.Join("\r\n", r.Aliases.Take(60)));
                AppState.Select(new SelectedObject { Kind = SelectedKind.Coordinates, Name = r.Name, RaDeg = r.RaDeg, DecDeg = r.DecDeg, Mag = r.Vmag });
            }, ex => _onInfo.Text = "Sesame error: " + ex.Message);
        }

        private void Vsx(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            Dictionary<string, object> d = null;
            _query.Replace("AAVSO VSX", t => d = OnlineServices.VsxObject(name), () =>
                _onInfo.Text = d == null ? "Not in VSX: " + name : "AAVSO VSX\r\n\r\n" + string.Join("\r\n", d.Select(kv => kv.Key + ": " + Convert.ToString(kv.Value, CultureInfo.InvariantCulture))),
                ex => _onInfo.Text = "VSX error: " + ex.Message);
        }

        private void RunAdql()
        {
            string q = _adql.Text;
            int svc = _service.SelectedIndex;
            RunTable("ADQL query", () => svc == 0 ? OnlineServices.SimbadQuery(q) : svc == 1 ? OnlineServices.VizierQuery(q) : OnlineServices.Tap(OnlineServices.ExoplanetTap, q));
        }

        private void RunTable(string title, Func<DataTable> query)
        {
            DataTable tab = null;
            _query.Replace(title, t => tab = query(), () =>
            {
                _last = tab;
                _onGrid.Columns.Clear(); _onGrid.Rows.Clear();
                foreach (var c in tab.Columns) _onGrid.Columns.Add(c, c);
                foreach (var r in tab.Rows.Take(20000)) _onGrid.Rows.Add(r.Cast<object>().ToArray());
                _onInfo.Text = string.Format("{0}: {1} rows × {2} columns\r\nDouble-click a row with RA/Dec to show it in the sky chart.", title, tab.Rows.Count, tab.Columns.Count);
            }, ex => _onInfo.Text = title + " failed:\r\n" + ex.Message);
        }

        private void PlotColumns()
        {
            if (_last == null || _last.Columns.Count < 2) return;
            var spec = PlannerView.Prompt("Plot", "Column names for X and Y (comma separated). Columns: " + string.Join(", ", _last.Columns.Take(20)), _last.Columns[1] + "," + _last.Columns[Math.Min(2, _last.Columns.Count - 1)]);
            if (spec == null) return;
            var p = spec.Split(',');
            if (p.Length < 2) return;
            int xc = _last.Col(p[0].Trim()), yc = _last.Col(p[1].Trim());
            if (xc < 0 || yc < 0) { MainForm.Instance.Toast("Unknown column", Icons.Warning); return; }
            var plot = new PlotControl { Dock = DockStyle.Fill, Title = p[1].Trim() + " vs " + p[0].Trim(), XLabel = p[0].Trim(), YLabel = p[1].Trim() };
            var s = plot.AddSeries("data", SeriesStyle.Scatter);
            for (int i = 0; i < _last.Rows.Count; i++) s.Add(_last.GetDouble(i, _last.Columns[xc]), _last.GetDouble(i, _last.Columns[yc]));
            if (p[1].Trim().ToLowerInvariant().Contains("mag")) plot.InvertY = true;
            var f = new Form { Text = plot.Title, Size = new Size(900, 600), StartPosition = FormStartPosition.CenterParent };
            f.Controls.Add(plot);
            Theme.Apply(f);
            f.Show(this);
        }

        // ================================================================== projects

        private static readonly string[][] Projects =
        {
            new[] { "Variable stars", "AAVSO", "Submit variable-star photometry (AAVSO extended format from the Photometry view).", "https://www.aavso.org/" },
            new[] { "Variable stars", "BAA Variable Star Section", "Long-term light curves, visual and CCD.", "https://britastro.org/section/variable-star" },
            new[] { "Exoplanets", "Exoplanet Watch (NASA)", "Transit timing to keep ephemerides fresh for Ariel/JWST.", "https://exoplanets.nasa.gov/exoplanet-watch/" },
            new[] { "Exoplanets", "ExoClock (Ariel)", "Transit observations of Ariel targets.", "https://www.exoclock.space/" },
            new[] { "Exoplanets", "ETD – Exoplanet Transit Database", "Transit light curves and O−C diagrams.", "http://var2.astro.cz/ETD/" },
            new[] { "Minor planets", "Minor Planet Center", "Astrometry of asteroids and comets (ADES / 80-column from the Solar system view).", "https://minorplanetcenter.net/" },
            new[] { "Minor planets", "NEO Confirmation Page", "Follow up newly discovered near-Earth object candidates.", "https://minorplanetcenter.net/iau/NEO/toconfirm_tabular.html" },
            new[] { "Occultations", "IOTA", "Asteroid and lunar occultation timings.", "https://occultations.org/" },
            new[] { "Occultations", "IOTA/ES – European Section", "Occultation predictions and reporting in Europe.", "https://www.iota-es.de/" },
            new[] { "Occultations", "Occult Watcher Cloud", "Coordinate occultation stations.", "https://cloud.occultwatcher.net/" },
            new[] { "Planets", "ALPO", "Planetary, lunar and solar observing sections.", "https://alpo-astronomy.org/" },
            new[] { "Planets", "PVOL – Planetary Virtual Observatory", "Upload Jupiter, Saturn and other planetary images for professional use.", "http://pvol2.ehu.eus/" },
            new[] { "Planets", "JUPOS", "Jupiter feature longitude measurements.", "http://jupos.org/" },
            new[] { "Sun", "SIDC / SILSO sunspot number", "Report sunspot counts (Wolf number).", "https://www.sidc.be/SILSO/" },
            new[] { "Meteors", "IMO – International Meteor Organization", "Visual meteor counts and video networks.", "https://www.imo.net/" },
            new[] { "Meteors", "Global Meteor Network", "Low-cost video cameras for meteor orbits.", "https://globalmeteornetwork.org/" },
            new[] { "Spectroscopy", "BeSS database", "Spectra of Be stars (BeSS FITS export in the Spectra view).", "http://basebe.obspm.fr/" },
            new[] { "Spectroscopy", "ARAS database", "Amateur spectroscopy campaigns (novae, symbiotics, Be stars).", "https://aras-database.github.io/database/" },
            new[] { "Supernovae", "Transient Name Server", "Report supernova and transient discoveries.", "https://www.wis-tns.org/" },
            new[] { "Radio", "Radio JOVE", "Jupiter and solar decametric radio (20 MHz).", "https://radiojove.gsfc.nasa.gov/" },
            new[] { "Radio", "Society of Amateur Radio Astronomers", "Hydrogen line, SID monitoring, meteor scatter.", "https://www.radio-astronomy.org/" },
            new[] { "Online", "Zooniverse – space projects", "Classify galaxies, find planets and asteroids in survey data.", "https://www.zooniverse.org/projects?discipline=space" },
            new[] { "Online", "Kilonova Seekers / GOTO", "Spot transients in survey images.", "https://www.zooniverse.org/projects/dry-lab-ops/kilonova-seekers" },
            new[] { "Dark skies", "Globe at Night", "Measure sky brightness (SQM readings).", "https://www.globeatnight.org/" },
        };

        private Control BuildProjects()
        {
            var grid = Ui.Grid();
            foreach (var c in new[] { "Field", "Project", "What you contribute" }) grid.Columns.Add(c, c);
            grid.Columns[0].FillWeight = 20; grid.Columns[1].FillWeight = 35; grid.Columns[2].FillWeight = 90;
            foreach (var p in Projects) { int k = grid.Rows.Add(p[0], p[1], p[2]); grid.Rows[k].Tag = p[3]; }
            grid.CellDoubleClick += (s, e) =>
            {
                if (e.RowIndex < 0) return;
                var url = grid.Rows[e.RowIndex].Tag as string;
                if (url != null) try { Process.Start(url); } catch { }
            };
            var panel = new Panel { Dock = DockStyle.Fill };
            panel.Controls.Add(grid);
            panel.Controls.Add(new Label { Dock = DockStyle.Top, Height = 24, Tag = "dim", Text = "Double-click to open the project's website in your browser. Sidralune exports the formats these projects accept." });
            return panel;
        }

        public IEnumerable<AppCommand> GetCommands()
        {
            yield return new AppCommand { Title = "Databases: search local catalogues", Icon = Icons.Database, Run = () => { MainForm.Instance.Navigate("databases"); _tabs.Select(0); } };
            yield return new AppCommand { Title = "Databases: SIMBAD / VizieR / ADQL query", Run = () => { MainForm.Instance.Navigate("databases"); _tabs.Select(1); } };
            yield return new AppCommand { Title = "Databases: citizen science projects", Run = () => { MainForm.Instance.Navigate("databases"); _tabs.Select(2); } };
        }
    }
}
