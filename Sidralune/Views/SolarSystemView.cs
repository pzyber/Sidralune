using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Sidralune.Core;
using Sidralune.Core.Ephemeris;
using Sidralune.Core.Mathematics;
using Sidralune.Core.MinorBodies;
using Sidralune.Core.Satellites;
using Sidralune.Core.Time;
using Sidralune.Core.Util;
using Sidralune.Imaging;
using Sidralune.Imaging.Analysis;
using Sidralune.Imaging.IO;
using Sidralune.Science.Astrometry;
using Sidralune.Science.Databases;
using Sidralune.Science.SolarSystem;
using Sidralune.UI;
using AstroEvents = Sidralune.Core.Ephemeris.Events;

namespace Sidralune.Views
{
    /// <summary>
    /// Solar-system workbench: ephemerides for planets, asteroids and comets; a minor-body browser with orbit diagram;
    /// planetary physical data and Galilean moons; asteroid and lunar occultations; meteor showers; satellite passes;
    /// and MPC astrometry reporting (Obs80 and ADES).
    /// </summary>
    public class SolarSystemView : UserControl, ICommandSource
    {
        private readonly TabbedPanel _tabs = new TabbedPanel { Dock = DockStyle.Fill, Padding = new Padding(10) };
        // tables and lists keep the newest calculation; the online look-ups (Horizons, SBDB, CNEOS) run one at a time
        // calculations started with a button run one at a time, the button waiting for it
        private readonly JobSlot _ephemJob = new JobSlot(), _positionsJob = new JobSlot(), _galileanJob = new JobSlot(), _occJob = new JobSlot(), _passJob = new JobSlot(), _onlineJob = new JobSlot();

        public SolarSystemView()
        {
            Controls.Add(_tabs);
            _tabs.AddPage("Ephemeris", BuildEphemeris());
            _tabs.AddPage("Minor bodies", BuildMinor());
            _tabs.AddPage("Planets & moons", BuildPlanets());
            _tabs.AddPage("Occultations", BuildOccultations());
            _tabs.AddPage("Meteors", BuildMeteors());
            _tabs.AddPage("Satellites", BuildSatellites());
            _tabs.AddPage("MPC astrometry", BuildMpc());
        }

        private static string F(double v, string fmt) => double.IsNaN(v) ? "" : v.ToString(fmt, CultureInfo.InvariantCulture);
        private static string Local(AstroTime t) => AppState.Site.ToLocal(t.ToDateTimeUtc()).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

        private static object[] BodyChoices()
        {
            var list = new List<object>();
            foreach (Body b in new[] { Body.Sun, Body.Moon, Body.Mercury, Body.Venus, Body.Mars, Body.Jupiter, Body.Saturn, Body.Uranus, Body.Neptune, Body.Pluto }) list.Add(b);
            list.AddRange(MinorBodyStore.Instance.Comets.OrderBy(c => c.DisplayName).Take(2000));
            list.AddRange(MinorBodyStore.Instance.Asteroids.OrderBy(c => c.Number == 0 ? int.MaxValue : c.Number).Take(3000));
            return list.ToArray();
        }

        private static BodyPlace PlaceOf(object o, SkyContext ctx)
        {
            if (o is Body)
            {
                var b = (Body)o;
                var p = ctx.BodyPlace(b);
                if (b != Body.Moon && b != Body.Sun) PlanetPhysics.Fill(b, p, ctx.JdTT);
                return p;
            }
            return MinorBodyStore.Place(ctx, (MinorBody)o);
        }

        // ================================================================== ephemeris

        private ComboBox _ephBody;
        private DataGridView _ephGrid;

        private Control BuildEphemeris()
        {
            var panel = new Panel { Dock = DockStyle.Fill };
            _ephBody = Ui.Combo(BodyChoices());
            _ephBody.Width = 260;
            _ephBody.DropDownStyle = ComboBoxStyle.DropDown;
            _ephBody.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
            _ephBody.AutoCompleteSource = AutoCompleteSource.ListItems;
            var start = new TimeField { Width = 150, Value = AppState.Now.ToDateTimeUtc() };
            var step = Ui.Combo("10 min", "1 hour", "6 hours", "1 day", "7 days");
            step.SelectedIndex = 3; step.Width = 90;
            var count = Ui.Num(30, 1, 2000);
            var go = new FlatButton("Compute", Icons.Play, (s, e) => ComputeEphemeris(start.Value, step.SelectedIndex, (int)count.Value)) { Kind = ButtonKind.Primary };
            go.AutoSizeToText();
            _ephemJob.Guard(go);
            var csv = new FlatButton("CSV…", Icons.Save, (s, e) => GridToCsv(_ephGrid, "ephemeris.csv")) { Kind = ButtonKind.Subtle };
            csv.AutoSizeToText();
            var horizons = new FlatButton("Compare with JPL Horizons", Icons.Download, (s, e) => CompareHorizons(start.Value)) { Kind = ButtonKind.Subtle };
            horizons.AutoSizeToText();
            var chart = new FlatButton("Show in sky", Icons.Sky, (s, e) =>
            {
                var o = _ephBody.SelectedItem;
                if (o == null) return;
                AppState.Select(o is Body ? SkyView.PlanetObject((Body)o) : SkyView.MinorObject((MinorBody)o));
                MainForm.Instance.Navigate("sky");
            }) { Kind = ButtonKind.Subtle };
            chart.AutoSizeToText();
            var bar = Ui.Toolbar(_ephBody, new Label { Text = "from (UTC)", AutoSize = true, Margin = new Padding(4, 6, 2, 0) }, start, step, count, go, csv, horizons, chart);
            _ephGrid = Ui.Grid();
            foreach (var c in new[] { "Local time", "RA (J2000)", "Dec (J2000)", "Alt", "Az", "Δ (AU)", "r (AU)", "Elong.", "Phase", "Mag", "Size\"", "Const." }) _ephGrid.Columns.Add(c, c);
            panel.Controls.Add(_ephGrid);
            panel.Controls.Add(bar);
            return panel;
        }

        private object CurrentEphBody()
        {
            if (_ephBody.SelectedItem != null && _ephBody.Text == _ephBody.SelectedItem.ToString()) return _ephBody.SelectedItem;
            var mb = MinorBodyStore.Instance.Find(_ephBody.Text);
            if (mb != null) return mb;
            Body b;
            if (Enum.TryParse(_ephBody.Text, true, out b)) return b;
            return null;
        }

        private void ComputeEphemeris(DateTime startUtc, int stepIdx, int n)
        {
            var o = CurrentEphBody();
            if (o == null) { MainForm.Instance.Toast("Unknown object (load MPC data in Settings → Data, or add elements in Minor bodies)", Icons.Info); return; }
            double step = new[] { 10.0 / 1440, 1.0 / 24, 0.25, 1, 7 }[stepIdx];
            var rows = new List<object[]>();
            var site = AppState.Site;
            double t0 = AstroTime.FromDateTime(startUtc).JdUtc;
            _ephemJob.Run("Ephemeris", t =>
            {
                for (int i = 0; i < n; i++)
                {
                    var at = new AstroTime(t0 + i * step);
                    var ctx = new SkyContext(site, at);
                    var p = PlaceOf(o, ctx);
                    var cobj = Core.Catalogs.CatalogManager.Instance.ConstellationAt(p.Astrometric.RaHours * 15 * AstroMath.Deg2Rad, p.Astrometric.DecDeg * AstroMath.Deg2Rad);
                    string con = cobj != null ? cobj.Abbr : "";
                    rows.Add(new object[] { Local(at), AstroMath.FormatHms(p.Astrometric.RaHours, 1, true), AstroMath.FormatDms(p.Astrometric.DecDeg, 0, true, true),
                        F(p.Horizontal.AltDeg, "0.0"), F(p.Horizontal.AzDeg, "0.0"), F(p.Distance, "0.00000"), F(p.HelioDistance, "0.00000"),
                        F(p.Elongation * AstroMath.Rad2Deg, "0.0"), F(p.PhaseAngle * AstroMath.Rad2Deg, "0.0"), F(p.Magnitude, "0.0"), F(p.DiameterArcsec, "0.0"), con });
                }
            }, () =>
            {
                _ephGrid.Rows.Clear();
                foreach (var r in rows) _ephGrid.Rows.Add(r);
            });
        }

        private void CompareHorizons(DateTime startUtc)
        {
            var o = CurrentEphBody();
            if (o == null) return;
            string cmd = o is Body ? HorizonsId((Body)o) : ((MinorBody)o).IsComet ? ((MinorBody)o).Designation + ";" : (((MinorBody)o).Number > 0 ? ((MinorBody)o).Number + ";" : ((MinorBody)o).Designation + ";");
            string start = startUtc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), stop = startUtc.AddMinutes(1).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            List<string> lines = null;
            _onlineJob.Run("JPL Horizons", t => lines = OnlineServices.HorizonsEphemeris(cmd, start, stop, "1m", "500@399", "1"), () =>
            {
                if (lines == null || lines.Count == 0) { MainForm.Instance.Toast("Horizons returned no data", Icons.Warning); return; }
                var ctx = new SkyContext(new Observer { LatitudeDeg = 0, LongitudeDeg = 0, ElevationM = -6378137 }, AstroTime.FromDateTime(startUtc));
                var p = PlaceOf(o, new SkyContext(AppState.Site, AstroTime.FromDateTime(startUtc)));
                ProcessingView.ShowText("Horizons comparison", "JPL Horizons (geocentric, astrometric J2000):\r\n" + lines[0] + "\r\n\r\nSidralune (topocentric for the active site, astrometric J2000):\r\n"
                    + AstroMath.FormatHms(p.Astrometric.RaHours, 2, true) + "  " + AstroMath.FormatDms(p.Astrometric.DecDeg, 1, true, true)
                    + "\r\n\r\nDifferences up to the parallax (≈8.8″/Δ) are expected for nearby bodies.");
            }, ex => MainForm.Instance.Toast("Horizons: " + ex.Message, Icons.Warning));
        }

        private static string HorizonsId(Body b)
        {
            switch (b)
            {
                case Body.Sun: return "10";
                case Body.Moon: return "301";
                case Body.Pluto: return "999";
                default: return ((int)b * 100 + 99).ToString(CultureInfo.InvariantCulture);
            }
        }

        private static void GridToCsv(DataGridView g, string name)
        {
            if (g.Rows.Count == 0) return;
            using (var d = new SaveFileDialog { Filter = "CSV|*.csv", FileName = name })
            {
                if (d.ShowDialog() != DialogResult.OK) return;
                CsvWriter.Write(d.FileName, g.Columns.Cast<DataGridViewColumn>().Select(c => c.HeaderText).ToList(),
                    g.Rows.Cast<DataGridViewRow>().Select(r => (IList<object>)r.Cells.Cast<DataGridViewCell>().Select(c => c.Value).ToList()));
            }
        }

        // ================================================================== minor bodies

        private DataGridView _mbGrid;
        private OrbitDiagram _orbits;
        private List<MinorBody> _mbRows = new List<MinorBody>();

        private Control BuildMinor()
        {
            var panel = new Panel { Dock = DockStyle.Fill };
            var kind = Ui.Combo("Comets", "Asteroids", "Near-Earth objects", "All");
            var mag = Ui.Num(14, 3, 25, 1, 0.5m);
            var find = new TextBox { Width = 160 };
            var go = new FlatButton("List", Icons.Search, (s, e) => ListMinor(kind.SelectedIndex, (double)mag.Value, find.Text)) { Kind = ButtonKind.Primary };
            go.AutoSizeToText();
            _positionsJob.Guard(go);
            var add = new FlatButton("Add elements…", Icons.Add, (s, e) => AddElements());
            add.AutoSizeToText();
            var sbdb = new FlatButton("Fetch from JPL SBDB", Icons.Download, (s, e) =>
            {
                var des = PlannerView.Prompt("JPL Small-Body Database", "Designation or name (e.g. 433, 2024 YR4, 12P)", find.Text);
                if (string.IsNullOrWhiteSpace(des)) return;
                MinorBody mb = null;
                _onlineJob.Run("SBDB lookup", t => mb = OnlineServices.SbdbElements(des), () =>
                {
                    if (mb == null) { MainForm.Instance.Toast("Not found", Icons.Warning); return; }
                    AddBody(mb);
                    MainForm.Instance.Toast("Added " + mb.DisplayName + " (also shown in the sky chart)");
                    ListMinor(3, 99, mb.DisplayName);
                });
            });
            sbdb.AutoSizeToText();
            var nea = new FlatButton("Close approaches (60 d)", Icons.Warning, (s, e) => CloseApproaches()) { Kind = ButtonKind.Subtle };
            nea.AutoSizeToText();
            // bodies added with Add elements… or from JPL SBDB last for the session; this takes them away again
            var remove = new FlatButton("Remove added", Icons.Remove, (s, e) => RemoveAdded()) { Kind = ButtonKind.Subtle };
            remove.AutoSizeToText();
            var bar = Ui.Toolbar(kind, new Label { Text = "brighter than", AutoSize = true, Margin = new Padding(4, 6, 2, 0) }, mag, new Label { Text = "name", AutoSize = true, Margin = new Padding(4, 6, 2, 0) }, find, go, Ui.Separator(), add, sbdb, remove, nea);
            _mbGrid = Ui.Grid();
            foreach (var c in new[] { "Object", "Type", "Mag now", "RA", "Dec", "Δ (AU)", "r (AU)", "Elong.", "Alt now", "q / a (AU)", "e", "i (°)" }) _mbGrid.Columns.Add(c, c);
            _mbGrid.SelectionChanged += (s, e) =>
            {
                _orbits.Highlight = _mbGrid.SelectedRows.Cast<DataGridViewRow>().Select(r => r.Tag as MinorBody).Where(m => m != null).ToList();
                _orbits.Invalidate();
            };
            _mbGrid.CellDoubleClick += (s, e) =>
            {
                if (e.RowIndex < 0) return;
                var m = _mbGrid.Rows[e.RowIndex].Tag as MinorBody;
                if (m == null) return;
                AppState.Select(SkyView.MinorObject(m));
                MainForm.Instance.Navigate("sky");
            };
            _orbits = new OrbitDiagram { Dock = DockStyle.Right, Width = 460 };
            panel.Controls.Add(_mbGrid);
            panel.Controls.Add(_orbits);
            panel.Controls.Add(bar);
            panel.Controls.Add(new Label { Dock = DockStyle.Bottom, Height = 22, Tag = "dim", Text = "Double-click to show in the sky chart. Comets and asteroids from MPC files (Settings → Data) are drawn on charts and in the planetarium." });
            return panel;
        }

        private void ListMinor(int kind, double maxMag, string filter)
        {
            IEnumerable<MinorBody> src = kind == 0 ? MinorBodyStore.Instance.Comets : kind == 1 ? MinorBodyStore.Instance.Asteroids
                : kind == 2 ? MinorBodyStore.Instance.Asteroids.Where(a => a.Elements != null && a.Elements.PerihelionDistance < 1.3) : MinorBodyStore.Instance.All;
            if (!string.IsNullOrWhiteSpace(filter)) src = src.Where(m => m.DisplayName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 || (m.Designation ?? "").IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0);
            var list = src.ToList();
            var ctx = new SkyContext(AppState.Site, AppState.Now);
            var rows = new List<Tuple<MinorBody, BodyPlace>>();
            _positionsJob.Run("Minor body positions", t =>
            {
                int i = 0;
                foreach (var m in list)
                {
                    if (++i % 2000 == 0) Tasks.Report(t, (double)i / list.Count);
                    try
                    {
                        var p = MinorBodyStore.Place(ctx, m);
                        if (!double.IsNaN(p.Magnitude) && p.Magnitude > maxMag) continue;
                        rows.Add(Tuple.Create(m, p));
                    }
                    catch { }
                }
            }, () =>
            {
                _mbGrid.Rows.Clear();
                foreach (var r in rows.OrderBy(r => double.IsNaN(r.Item2.Magnitude) ? 99 : r.Item2.Magnitude).Take(2000))
                {
                    var m = r.Item1; var p = r.Item2; var el = m.Elements;
                    int k = _mbGrid.Rows.Add(m.DisplayName, m.IsComet ? "Comet" : (m.OrbitType ?? "Asteroid"), F(p.Magnitude, "0.0"), AstroMath.FormatHms(p.Astrometric.RaHours, 0, true), AstroMath.FormatDms(p.Astrometric.DecDeg, 0, true, true),
                        F(p.Distance, "0.000"), F(p.HelioDistance, "0.000"), F(p.Elongation * AstroMath.Rad2Deg, "0"), F(p.Horizontal.AltDeg, "0"),
                        el == null ? "" : F(m.IsComet ? el.PerihelionDistance : el.SemiMajorAxis, "0.000"), el == null ? "" : F(el.Eccentricity, "0.0000"), el == null ? "" : F(el.Inclination * AstroMath.Rad2Deg, "0.00"));
                    _mbGrid.Rows[k].Tag = m;
                }
                _orbits.Bodies = rows.Select(r => r.Item1).Take(300).ToList();
                _orbits.Invalidate();
                MainForm.Instance.Toast(rows.Count + " objects" + (rows.Count > 2000 ? " (first 2000 shown)" : ""));
            });
        }

        private void RemoveAdded()
        {
            var picked = _mbGrid.SelectedCells.Cast<DataGridViewCell>().Select(c => c.OwningRow).Distinct().Select(r => r.Tag as MinorBody).Where(m => m != null).ToList();
            if (picked.Count == 0) { MainForm.Instance.Toast("Select the objects to remove in the list", Icons.Info); return; }
            var added = picked.Where(MinorBodyStore.Instance.IsAdded).ToList();
            if (added.Count == 0) { MainForm.Instance.Toast("Only objects you added (Add elements…, JPL SBDB) can be removed; the others come from the downloaded catalogues", Icons.Info); return; }
            foreach (var m in added)
            {
                MinorBodyStore.Instance.Remove(m);
                _ephBody.Items.Remove(m);
                foreach (var r in _mbGrid.Rows.Cast<DataGridViewRow>().Where(r => r.Tag == m).ToList()) _mbGrid.Rows.Remove(r);
            }
            MainForm.Instance.Toast("Removed " + string.Join(", ", added.Select(m => m.DisplayName)) + (added.Count < picked.Count ? " (catalogue objects stay)" : ""));
        }

        private void AddElements()
        {
            var text = PlannerView.Prompt("Add orbital elements", "Paste one MPCORB line (asteroid) or CometEls line (comet)", "");
            if (string.IsNullOrWhiteSpace(text)) return;
            MinorBody mb = null;
            try { mb = MpcFormats.ParseMpcOrb(text); } catch { }
            if (mb == null) try { mb = MpcFormats.ParseCometEls(text); } catch { }
            if (mb == null) { MainForm.Instance.Toast("Could not parse the elements (MPC 1-line formats expected)", Icons.Warning); return; }
            AddBody(mb);
            MainForm.Instance.Toast("Added " + mb.DisplayName);
        }

        /// <summary>Adds a body by hand; one added again with the same designation replaces the earlier one in the list too.</summary>
        private void AddBody(MinorBody mb)
        {
            foreach (var old in _ephBody.Items.OfType<MinorBody>().Where(m => m.Designation == mb.Designation && m.IsComet == mb.IsComet && MinorBodyStore.Instance.IsAdded(m)).ToList()) _ephBody.Items.Remove(old);
            MinorBodyStore.Instance.Add(mb);
            _ephBody.Items.Add(mb);
        }

        private void CloseApproaches()
        {
            DataTable tab = null;
            _onlineJob.Run("JPL close approaches", t => tab = OnlineServices.CloseApproaches(0.05), () =>
            {
                if (tab == null) return;
                var sb = new System.Text.StringBuilder("Close approaches to Earth within 0.05 AU in the next 60 days (JPL CNEOS):\r\n\r\n");
                sb.AppendLine(string.Join("  ", tab.Columns));
                foreach (var r in tab.Rows) sb.AppendLine(string.Join("  ", r));
                ProcessingView.ShowText("Close approaches", sb.ToString());
            }, ex => MainForm.Instance.Toast("CNEOS: " + ex.Message, Icons.Warning));
        }

        /// <summary>Top-down (ecliptic) view of planetary orbits with selected minor-body orbits and current positions.</summary>
        private class OrbitDiagram : Control, IThemed
        {
            public List<MinorBody> Bodies = new List<MinorBody>(), Highlight = new List<MinorBody>();
            private double _scaleAu = 6;

            public OrbitDiagram()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
                MouseWheel += (s, e) => { _scaleAu = AstroMath.Clamp(_scaleAu * (e.Delta > 0 ? 0.8 : 1.25), 0.5, 120); Invalidate(); };
            }

            public void ApplyTheme(Palette p) => Invalidate();

            private static double OrbitPeriodDays(Body b)
            {
                switch (b)
                {
                    case Body.Mercury: return 87.969; case Body.Venus: return 224.701; case Body.Earth: return 365.256; case Body.Mars: return 686.98;
                    case Body.Jupiter: return 4332.59; case Body.Saturn: return 10759.22; case Body.Uranus: return 30688.5; default: return 60182;
                }
            }

            private static Vec3 Ecl(Vec3 eq) { double e = 23.4392911 * AstroMath.Deg2Rad; return new Vec3(eq.X, eq.Y * Math.Cos(e) + eq.Z * Math.Sin(e), -eq.Y * Math.Sin(e) + eq.Z * Math.Cos(e)); }

            protected override void OnPaint(PaintEventArgs e)
            {
                var p = Theme.Current;
                var g = e.Graphics;
                g.Clear(p.Surface);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                float cx = Width / 2f, cy = Height / 2f, sc = Math.Min(Width, Height) / 2f / (float)_scaleAu;
                double jd = AppState.Now.JdTT;
                Func<Vec3, PointF> map = v => { var q = Ecl(v); return new PointF(cx + (float)q.X * sc, cy - (float)q.Y * sc); };
                using (var pen = new Pen(Theme.WithAlpha(p.TextDim, 90)))
                using (var fill = new SolidBrush(p.Text))
                {
                    foreach (Body b in new[] { Body.Mercury, Body.Venus, Body.Earth, Body.Mars, Body.Jupiter, Body.Saturn, Body.Uranus, Body.Neptune })
                    {
                        double period = OrbitPeriodDays(b);
                        var pts = new List<PointF>();
                        for (int k = 0; k <= 120; k++) pts.Add(map(EphemerisManager.Position(b, jd + period * k / 120.0) - EphemerisManager.Position(Body.Sun, jd + period * k / 120.0)));
                        g.DrawLines(pen, pts.ToArray());
                        var now = map(EphemerisManager.Position(b, jd) - EphemerisManager.Position(Body.Sun, jd));
                        g.FillEllipse(fill, now.X - 3, now.Y - 3, 6, 6);
                        TextRenderer.DrawText(g, b.ToString(), Theme.SmallFont, new Point((int)now.X + 4, (int)now.Y - 6), p.TextDim);
                    }
                }
                using (var sun = new SolidBrush(p.Warning)) g.FillEllipse(sun, cx - 5, cy - 5, 10, 10);
                foreach (var m in Bodies.Concat(Highlight))
                {
                    bool hi = Highlight.Contains(m);
                    if (m.Elements == null) continue;
                    try
                    {
                        var col = m.IsComet ? p.Comet : p.Asteroid;
                        if (hi)
                        {
                            var el = m.Elements;
                            var pts = new List<PointF>();
                            if (el.Eccentricity < 0.99)
                            {
                                double per = Math.Pow(el.SemiMajorAxis, 1.5) * 365.25;
                                for (int k = 0; k <= 200; k++) pts.Add(map(m.HeliocentricEquatorial(jd + per * k / 200.0)));
                            }
                            else for (int k = -100; k <= 100; k++) pts.Add(map(m.HeliocentricEquatorial(el.PerihelionTime + k * 2.0)));
                            using (var pen = new Pen(col, 1.5f)) g.DrawLines(pen, pts.ToArray());
                        }
                        var now = map(m.HeliocentricEquatorial(jd));
                        using (var b = new SolidBrush(hi ? col : Theme.WithAlpha(col, 140))) g.FillEllipse(b, now.X - (hi ? 3.5f : 2), now.Y - (hi ? 3.5f : 2), hi ? 7 : 4, hi ? 7 : 4);
                        if (hi) TextRenderer.DrawText(g, m.DisplayName, Theme.UiFont, new Point((int)now.X + 5, (int)now.Y + 2), col);
                    }
                    catch { }
                }
                TextRenderer.DrawText(g, string.Format("Ecliptic view, {0:0.#} AU radius · wheel to zoom · {1}", _scaleAu, AppState.Now), Theme.SmallFont, new Point(6, Height - 18), p.TextDim);
            }
        }

        // ================================================================== planets & moons

        private Control BuildPlanets()
        {
            var panel = new Panel { Dock = DockStyle.Fill };
            var info = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, Font = Theme.MonoFont, ScrollBars = ScrollBars.Vertical };
            var moons = new MoonDiagram { Dock = DockStyle.Top, Height = 150 };
            var phen = new TextBox { Dock = DockStyle.Right, Width = 420, Multiline = true, ReadOnly = true, Font = Theme.MonoFont, ScrollBars = ScrollBars.Vertical };
            var refresh = new FlatButton("Refresh for current time", Icons.Refresh, null) { Kind = ButtonKind.Primary };
            refresh.AutoSizeToText();
            Action upd = () =>
            {
                var now = AppState.Now;
                var ctx = new SkyContext(AppState.Site, now);
                var sb = new System.Text.StringBuilder();
                sb.AppendLine(string.Format("{0} at {1}\r\n", now, AppState.Site.Name));
                sb.AppendLine("Body        Mag    Diam\"   Illum  Elong   Alt    Rise   Transit  Set");
                foreach (Body b in new[] { Body.Sun, Body.Moon, Body.Mercury, Body.Venus, Body.Mars, Body.Jupiter, Body.Saturn, Body.Uranus, Body.Neptune })
                {
                    var p = ctx.BodyPlace(b);
                    if (b != Body.Moon && b != Body.Sun) PlanetPhysics.Fill(b, p, ctx.JdTT);
                    var rs = AstroEvents.BodyRiseSet(b, AppState.Site, new AstroTime(now.JdUtc - 0.5));
                    Func<AstroTime?, string> hm = t => t.HasValue ? AppState.Site.ToLocal(t.Value.ToDateTimeUtc()).ToString("HH:mm", CultureInfo.InvariantCulture) : "  -  ";
                    sb.AppendLine(string.Format("{0,-10}{1,6}{2,8}{3,8}{4,7}{5,7}{6,8}{7,8}{8,8}", b, F(p.Magnitude, "0.0"), F(b == Body.Sun || b == Body.Moon ? PlanetPhysics.AngularDiameterArcsec(b, p.Distance) : p.DiameterArcsec, "0.0"),
                        b == Body.Sun ? "" : F(p.IlluminatedFraction * 100, "0") + "%", b == Body.Sun ? "" : F(p.Elongation * AstroMath.Rad2Deg, "0"), F(p.Horizontal.AltDeg, "0"), hm(rs.Rise), hm(rs.Transit), hm(rs.Set)));
                }
                double P, B0, L0; int rot;
                PlanetPhysics.SunOrientation(ctx.JdTT, out P, out B0, out L0, out rot);
                sb.AppendLine(string.Format("\r\nSun: P = {0:0.00}°, B0 = {1:0.00}°, L0 = {2:0.00}°, Carrington rotation {3}", P, B0, L0, rot));
                double ll, lb, lpa;
                PlanetPhysics.MoonLibration(ctx.JdTT, out ll, out lb, out lpa);
                sb.AppendLine(string.Format("Moon: libration l = {0:+0.00;-0.00}°, b = {1:+0.00;-0.00}°, axis PA {2:0.0}°, colongitude {3:0.0}°", ll, lb, lpa, PlanetPhysics.MoonColongitude(ctx.JdTT)));
                var jp = ctx.BodyPlace(Body.Jupiter);
                var toJ = EphemerisManager.Position(Body.Jupiter, ctx.JdTT - jp.LightTimeDays) - EphemerisManager.Position(Body.Earth, ctx.JdTT);
                double s1, s2, s3;
                PlanetPhysics.JupiterCentralMeridians(ctx.JdTT, toJ, jp.LightTimeDays, out s1, out s2, out s3);
                sb.AppendLine(string.Format("Jupiter: CM System I {0:0.0}°, II {1:0.0}°, III {2:0.0}°  (GRS transits when System II ≈ GRS longitude, see JUPOS)", s1, s2, s3));
                var sp = ctx.BodyPlace(Body.Saturn);
                double rb, rp, ru; PlanetPhysics.SaturnRings(ctx.JdTT, sp, out rb, out rp, out ru);
                sb.AppendLine(string.Format("Saturn: ring tilt B = {0:+0.00;-0.00}°, P = {1:0.00}°", rb, rp));
                foreach (Body b in new[] { Body.Mars, Body.Jupiter, Body.Saturn, Body.Venus })
                {
                    var p = ctx.BodyPlace(b);
                    var to = EphemerisManager.Position(b, ctx.JdTT - p.LightTimeDays) - EphemerisManager.Position(Body.Earth, ctx.JdTT);
                    double cm, slat, pa; PlanetPhysics.Orientation(b, ctx.JdTT, to, p.LightTimeDays, out cm, out slat, out pa);
                    sb.AppendLine(string.Format("{0}: central meridian {1:0.0}°, sub-Earth latitude {2:+0.0;-0.0}°, pole PA {3:0.0}°", b, cm, slat, pa));
                }
                info.Text = sb.ToString();
                moons.Moons = GalileanMoons.Positions(ctx.JdTT);
                moons.Invalidate();
            };
            refresh.Click += (s, e) => upd();
            var phenBtn = new FlatButton("Galilean moon events (7 days)", Icons.Calendar, (s, e) =>
            {
                List<AstroEvent> ev = null;
                double jd = AppState.Now.JdTT;
                _galileanJob.Run("Galilean moon phenomena", t => ev = GalileanMoons.Phenomena(jd, jd + 7), () =>
                {
                    var site = AppState.Site;
                    phen.Text = "Transits and occultations (local time); ✓ = Jupiter above 10° in a dark sky\r\n\r\n" + string.Join("\r\n", ev.Select(x =>
                    {
                        bool vis = AstroEvents.BodyAltitude(Body.Jupiter, site, x.Time) > 10 && AstroEvents.SunAltitudeFast(site, x.Time) < -6;
                        return (vis ? "✓ " : "  ") + Local(x.Time) + "  " + x.Description;
                    }));
                });
            });
            phenBtn.AutoSizeToText();
            _galileanJob.Guard(phenBtn);
            panel.Controls.Add(info);
            panel.Controls.Add(phen);
            panel.Controls.Add(moons);
            panel.Controls.Add(Ui.Toolbar(refresh, phenBtn));
            panel.VisibleChanged += (s, e) => { if (panel.Visible && info.Text.Length == 0) upd(); };
            return panel;
        }

        private class MoonDiagram : Control, IThemed
        {
            public GalileanMoon[] Moons;
            public MoonDiagram() { SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true); }
            public void ApplyTheme(Palette p) => Invalidate();
            protected override void OnPaint(PaintEventArgs e)
            {
                var p = Theme.Current; var g = e.Graphics;
                g.Clear(p.Surface); g.SmoothingMode = SmoothingMode.AntiAlias;
                float cx = Width / 2f, cy = Height / 2f, r = Math.Max(6, Width / 70f);
                using (var b = new SolidBrush(p.IsNight ? p.Planet : Color.FromArgb(220, 200, 170))) g.FillEllipse(b, cx - r, cy - r * 0.93f, 2 * r, 2 * r * 0.93f);
                TextRenderer.DrawText(g, "E", Theme.SmallFont, new Point(4, (int)cy - 8), p.TextDim);
                TextRenderer.DrawText(g, "W", Theme.SmallFont, new Point(Width - 16, (int)cy - 8), p.TextDim);
                TextRenderer.DrawText(g, "Jupiter's Galilean moons (as seen in binoculars: north up, west right)", Theme.SmallFont, new Point(4, 4), p.TextDim);
                if (Moons == null) return;
                var cols = p.IsNight ? new[] { p.Series[0], p.Series[2], p.Series[4], p.Series[1] } : new[] { Color.Gold, Color.LightSkyBlue, Color.Silver, Color.SandyBrown };
                for (int i = 0; i < Moons.Length; i++)
                {
                    var m = Moons[i];
                    float x = cx + (float)m.X * r, y = cy - (float)m.Y * r;
                    if (m.Occulted) continue;
                    using (var b = new SolidBrush(cols[i])) g.FillEllipse(b, x - 3, y - 3, 6, 6);
                    TextRenderer.DrawText(g, m.Name.Substring(0, 2), Theme.SmallFont, new Point((int)x - 7, (int)y + (i % 2 == 0 ? 6 : -20)), cols[i]);
                }
            }
        }

        // ================================================================== occultations

        private Control BuildOccultations()
        {
            var panel = new Panel { Dock = DockStyle.Fill };
            var obj = new TextBox { Width = 180, Text = "(1) Ceres" };
            var days = Ui.Num(30, 1, 365);
            var mag = Ui.Num(12, 5, 16, 1, 0.5m);
            var grid = Ui.Grid();
            foreach (var c in new[] { "Event", "Time (UTC)", "Star mag", "Δmag", "Duration (s)", "Shadow (km)", "Your offset (km)" }) grid.Columns.Add(c, c);
            var ast = new FlatButton("Asteroid occultations", Icons.Comet, (s, e) =>
            {
                var mb = MinorBodyStore.Instance.Find(obj.Text);
                if (mb == null) { MainForm.Instance.Toast("Unknown minor body", Icons.Info); return; }
                List<OccultationEvent> ev = null;
                var st = AppState.Now; var en = new AstroTime(st.JdUtc + (double)days.Value);
                double ml = (double)mag.Value;
                _occJob.Run("Occultation search", t => ev = OccultationPredictor.Search(mb, st, en, new VizierProvider(), ml), () =>
                {
                    grid.Rows.Clear();
                    foreach (var x in ev) grid.Rows.Add(x.Body.DisplayName + " → " + (x.Star.Id ?? "star"), x.Time.ToString(), F(x.StarMag, "0.0"), F(x.MagDrop, "0.0"), F(x.DurationSec, "0.0"), F(x.ShadowWidthKm, "0"), F(x.ObserverOffsetKm, "0"));
                    MainForm.Instance.Toast(ev.Count + " candidate events (Gaia DR3 stars)");
                }, ex => MainForm.Instance.Toast("Search failed: " + ex.Message, Icons.Warning));
            }) { Kind = ButtonKind.Primary };
            ast.AutoSizeToText();
            var lunar = new FlatButton("Lunar occultations", Icons.Moon, (s, e) =>
            {
                List<LunarOccultation> ev = null;
                var st = AppState.Now; double d = (double)days.Value, ml = Math.Min(9, (double)mag.Value);
                _occJob.Run("Lunar occultations", t => ev = LunarOccultations.Predict(AppState.Site, st, d, new LocalCatalogProvider(), ml), () =>
                {
                    grid.Rows.Clear();
                    foreach (var x in ev)
                        grid.Rows.Add("Moon → " + x.Star + (x.Graze ? " (graze)" : ""), x.Disappearance.ToString() + " D PA " + F(x.DisappearancePA, "0") + "°", F(x.StarMag, "0.0"), "",
                            F((x.Reappearance - x.Disappearance) * 86400, "0"), string.Format("Moon {0:0}% lit, alt {1:0}°, Sun {2:0}°", x.MoonIlluminated * 100, x.MoonAltitude, x.SunAltitude), "R " + x.Reappearance);
                    MainForm.Instance.Toast(ev.Count + " lunar occultations visible from the active site");
                });
            });
            lunar.AutoSizeToText();
            _occJob.Guard(ast, lunar);
            panel.Controls.Add(grid);
            panel.Controls.Add(new Label { Dock = DockStyle.Top, Height = 34, Tag = "dim", Text = "Asteroid predictions use the loaded orbit and Gaia DR3 (online), so timing uncertainty depends on the orbit (compare with IOTA/Occult or occult.it before travelling). Report timings to IOTA/EAON." });
            panel.Controls.Add(Ui.Toolbar(new Label { Text = "Body", AutoSize = true, Margin = new Padding(0, 6, 2, 0) }, obj, new Label { Text = "Days", AutoSize = true, Margin = new Padding(4, 6, 2, 0) }, days,
                new Label { Text = "Star mag ≤", AutoSize = true, Margin = new Padding(4, 6, 2, 0) }, mag, ast, lunar));
            return panel;
        }

        // ================================================================== meteors

        private Control BuildMeteors()
        {
            var grid = Ui.Grid();
            foreach (var c in new[] { "Shower", "Code", "Peak (local)", "ZHR", "Speed (km/s)", "Radiant", "Moon at peak", "Best hourly rate here" }) grid.Columns.Add(c, c);
            Action upd = () =>
            {
                grid.Rows.Clear();
                var site = AppState.Site;
                int year = AppState.Now.ToDateTimeUtc().Year;
                double sqm = site.SqmZenith;
                double nelm = 7.93 - 5 * Math.Log10(Math.Pow(10, 4.316 - sqm / 5) + 1); // naked-eye limiting magnitude from SQM
                foreach (var sh in MeteorShowers.Major)
                {
                    var peak = sh.PeakIn(year);
                    if (peak.JdUtc < AppState.Now.JdUtc - 5) peak = sh.PeakIn(year + 1);
                    // best rate during the dark hours around the peak
                    double best = 0;
                    for (double h = -12; h <= 12; h += 0.5)
                    {
                        var t = new AstroTime(peak.JdUtc + h / 24);
                        if (AstroEvents.SunAltitudeFast(site, t) > -12) continue;
                        double alt = AstroEvents.FixedAltitude(sh.RadiantRa * AstroMath.Deg2Rad, sh.RadiantDec * AstroMath.Deg2Rad, site, t);
                        best = Math.Max(best, sh.HourlyRate(alt, nelm));
                    }
                    var moon = new SkyContext(site, peak).BodyPlace(Body.Moon);
                    grid.Rows.Add(sh.Name, sh.Code, Local(peak), F(sh.Zhr, "0"), F(sh.VelocityKms, "0"), AstroMath.FormatHms(sh.RadiantRa / 15, 0, true) + " " + F(sh.RadiantDec, "+0;-0") + "°",
                        F(moon.IlluminatedFraction * 100, "0") + "%", F(best, "0") + string.Format(" (LM {0:0.0})", nelm));
                }
            };
            var panel = new Panel { Dock = DockStyle.Fill };
            panel.Controls.Add(grid);
            panel.Controls.Add(new Label { Dock = DockStyle.Top, Height = 24, Tag = "dim", Text = "Rates use the IMO formula with the radiant altitude and your site's limiting magnitude (from SQM in Settings). Peak times from solar longitude." });
            panel.VisibleChanged += (s, e) => { if (panel.Visible) upd(); };
            return panel;
        }

        // ================================================================== satellites

        private readonly List<Sgp4> _sats = new List<Sgp4>();

        private Control BuildSatellites()
        {
            var panel = new Panel { Dock = DockStyle.Fill };
            var grid = Ui.Grid();
            foreach (var c in new[] { "Satellite", "Rise (local)", "Rise az", "Max (local)", "Max alt", "Set (local)", "Set az", "Mag", "Visible" }) grid.Columns.Add(c, c);
            var days = Ui.Num(3, 1, 14);
            var minAlt = Ui.Num(20, 0, 80);
            var visOnly = new CheckBox { Text = "Visible passes only", Checked = true, AutoSize = true, Margin = new Padding(6, 8, 6, 0) };
            var status = new Label { AutoSize = true, Tag = "dim", Margin = new Padding(8, 8, 0, 0) };
            var load = new FlatButton("Load TLEs", Icons.Download, (s, e) => LoadTles(status)) { Kind = ButtonKind.Normal };
            load.AutoSizeToText();
            var go = new FlatButton("Predict passes", Icons.Play, (s, e) =>
            {
                if (_sats.Count == 0) LoadTles(status);
                if (_sats.Count == 0) return;
                var rows = new List<Tuple<string, SatellitePass>>();
                var site = AppState.Site; var now = AppState.Now; double d = (double)days.Value, ma = (double)minAlt.Value; bool vo = visOnly.Checked;
                var sats = _sats.ToList();
                _passJob.Run("Satellite passes", t =>
                {
                    int i = 0;
                    foreach (var sat in sats)
                    {
                        Tasks.Report(t, (double)i++ / sats.Count, sat.Tle.Name);
                        try { foreach (var p in SatelliteTools.Passes(sat, site, now, d, ma)) if (!vo || p.Visible) rows.Add(Tuple.Create(sat.Tle.Name, p)); } catch { }
                    }
                }, () =>
                {
                    grid.Rows.Clear();
                    foreach (var r in rows.OrderBy(r => r.Item2.Rise.JdUtc).Take(3000))
                    {
                        var p = r.Item2;
                        grid.Rows.Add(r.Item1, Local(p.Rise), F(p.RiseAzDeg, "0") + "°", Local(p.Culmination), F(p.MaxAltDeg, "0") + "°", Local(p.Set), F(p.SetAzDeg, "0") + "°", F(p.BrightestMag, "0.0"), p.Visible ? "yes" : "");
                    }
                    MainForm.Instance.Toast(rows.Count + " passes");
                });
            }) { Kind = ButtonKind.Primary };
            go.AutoSizeToText();
            _passJob.Guard(go);
            panel.Controls.Add(grid);
            panel.Controls.Add(Ui.Toolbar(load, new Label { Text = "Days", AutoSize = true, Margin = new Padding(4, 6, 2, 0) }, days, new Label { Text = "Min alt", AutoSize = true, Margin = new Padding(4, 6, 2, 0) }, minAlt, visOnly, go, status));
            return panel;
        }

        private void LoadTles(Label status)
        {
            _sats.Clear();
            foreach (var id in new[] { "tle-stations", "tle-visual" })
            {
                var ds = DataManager.Get(id);
                if (ds == null || !ds.Installed) continue;
                var lines = File.ReadAllLines(ds.LocalPath).Select(l => l.TrimEnd()).Where(l => l.Length > 0).ToList();
                for (int i = 0; i + 2 < lines.Count; i++)
                {
                    if (lines[i + 1].StartsWith("1 ") && lines[i + 2].StartsWith("2 "))
                    {
                        try
                        {
                            var tle = Tle.Parse(lines[i].Trim(), lines[i + 1], lines[i + 2]);
                            if (!_sats.Any(s => s.Tle.CatalogNumber == tle.CatalogNumber)) _sats.Add(new Sgp4(tle));
                        }
                        catch { }
                        i += 2;
                    }
                }
            }
            status.Text = _sats.Count == 0 ? "No TLE files installed: Settings → Data → CelesTrak sets" : _sats.Count + " satellites loaded";
        }

        // ================================================================== MPC astrometry

        private readonly ImageViewer _mpcViewer = new ImageViewer { Dock = DockStyle.Fill, EmptyText = "Open a plate-solved image (or solve it in Astrometry), then click on the moving object to measure it" };
        private readonly List<AstrometricObservation> _obs = new List<AstrometricObservation>();
        private readonly List<PointF> _obsPix = new List<PointF>();
        private AstroImage _mpcImg;
        private readonly JobSlot _mpcJob = new JobSlot(); // the image is loaded in the background

        private Control BuildMpc()
        {
            var panel = new Panel { Dock = DockStyle.Fill };
            var side = new Card("Astrometry report") { Dock = DockStyle.Right, Width = 380, AutoScroll = true };
            var g = new FormGrid(120);
            var des = g.Row("Designation", new TextBox { Text = "" });
            var station = g.Row("Station code", new TextBox { Text = AppState.Settings.MpcStationCode });
            var observer = g.Row("Observer", new TextBox { Text = Environment.UserName });
            var list = new TextBox { Multiline = true, ReadOnly = true, Height = 200, Font = Theme.MonoFont, ScrollBars = ScrollBars.Both, WordWrap = false };
            Action refresh = () => list.Text = string.Join("\r\n", _obs.Select(MpcReporting.Obs80));
            var open = new FlatButton("Open image…", Icons.Open, (s, e) =>
            {
                if (_mpcJob.Refuse()) return;
                using (var d = new OpenFileDialog { Filter = ImageFile.OpenFilter })
                {
                    if (d.ShowDialog() != DialogResult.OK) return;
                    string path = d.FileName;
                    AstroImage loaded = null;
                    _mpcJob.Run("Opening " + Path.GetFileName(path), t => loaded = ImageFile.Load(path), () =>
                    {
                        _mpcImg = loaded;
                        _mpcViewer.SetImage(_mpcImg);
                        if (_mpcViewer.Wcs == null) MainForm.Instance.Toast("No WCS in this image: plate solve it in Astrometry and save with WCS", Icons.Info);
                    });
                }
            }) { Width = 340 };
            _mpcJob.Guard(open);
            g.Full(open);
            var undo = new FlatButton("Remove last measurement", Icons.Undo, (s, e) => { if (_obs.Count > 0) { _obs.RemoveAt(_obs.Count - 1); _obsPix.RemoveAt(_obsPix.Count - 1); refresh(); _mpcViewer.Invalidate(); } }) { Width = 340, Kind = ButtonKind.Subtle };
            g.Full(undo);
            g.Full(list);
            var obs80 = new FlatButton("Copy MPC1992 (80-column)", Icons.List, (s, e) => { if (_obs.Count > 0) Clipboard.SetText(list.Text); }) { Width = 340 };
            var ades = new FlatButton("Save ADES PSV…", Icons.Save, (s, e) =>
            {
                if (_obs.Count == 0) return;
                var rig = AppState.ImagingRig;
                string text = MpcReporting.AdesPsv(_obs, station.Text, observer.Text, observer.Text, rig.Name, rig.ApertureMm / 1000.0, "Reflector", "CMO");
                using (var d = new SaveFileDialog { Filter = "ADES PSV|*.psv", FileName = "ades.psv" })
                    if (d.ShowDialog() == DialogResult.OK) File.WriteAllText(d.FileName, text);
            }) { Width = 340, Kind = ButtonKind.Primary };
            g.Full(obs80); g.Full(ades);
            g.Full(Ui.Hint("Clicks are refined to the nearest centroid (PSF fit). Times are mid-exposure from DATE-OBS + EXPTIME/2. Errors use the plate-solution RMS. Check the designation format (packed or provisional) before submitting to obs@minorplanetcenter.net or the MPC submission API."));
            side.Controls.Add(g);
            _mpcViewer.ImageClick += (x, y, e) =>
            {
                if (_mpcImg == null || e.Button != MouseButtons.Left) return;
                var w = _mpcViewer.Wcs;
                if (w == null) { MainForm.Instance.Toast("Image has no WCS", Icons.Info); return; }
                var start = _mpcImg.DateObs;
                if (start == null) { MainForm.Instance.Toast("Image has no DATE-OBS", Icons.Info); return; }
                // refine to the nearest detected source within 8 px
                var lum = _mpcImg.Luminance();
                var stars = StarDetector.Detect(lum, _mpcImg.Width, _mpcImg.Height, new StarDetectionOptions { MaxStars = 3000 });
                var best = stars.OrderBy(st => (st.X - x) * (st.X - x) + (st.Y - y) * (st.Y - y)).FirstOrDefault();
                double cx = x, cy = y;
                if (best != null && Math.Abs(best.X - x) < 8 && Math.Abs(best.Y - y) < 8)
                {
                    var fit = PsfFit.FitGaussian(lum, _mpcImg.Width, _mpcImg.Height, best);
                    cx = fit != null && !double.IsNaN(fit.X) ? fit.X : best.X; cy = fit != null && !double.IsNaN(fit.Y) ? fit.Y : best.Y;
                }
                var o = MpcReporting.Measure(w, cx, cy, start.Value.AddSeconds(_mpcImg.ExposureSeconds / 2), station.Text);
                string d = des.Text.Trim();
                if (d.Length <= 5 && d.All(char.IsDigit)) o.PackedNumber = MpcFormats.PackDesignationNumber(int.Parse(d)); else o.ProvisionalOrTemp = d;
                _obs.Add(o); _obsPix.Add(new PointF((float)cx, (float)cy));
                refresh();
                _mpcViewer.Invalidate();
            };
            _mpcViewer.PaintOverlay += (gr, map) =>
            {
                using (var pen = new Pen(Theme.Current.Asteroid, 1.5f))
                    for (int i = 0; i < _obsPix.Count; i++) { var p = map(_obsPix[i].X, _obsPix[i].Y); gr.DrawEllipse(pen, p.X - 8, p.Y - 8, 16, 16); TextRenderer.DrawText(gr, (i + 1).ToString(), Theme.SmallFont, new Point((int)p.X + 9, (int)p.Y - 9), Theme.Current.Asteroid); }
            };
            panel.Controls.Add(_mpcViewer);
            panel.Controls.Add(side);
            return panel;
        }

        public IEnumerable<AppCommand> GetCommands()
        {
            string[] names = { "ephemeris", "minor bodies & orbits", "planets & Galilean moons", "occultations", "meteor showers", "satellite passes", "MPC astrometry report" };
            for (int i = 0; i < names.Length; i++)
            {
                int k = i;
                yield return new AppCommand { Title = "Solar system: " + names[i], Icon = i == 0 ? Icons.Comet : null, Run = () => { MainForm.Instance.Navigate("solarsystem"); _tabs.Select(k); } };
            }
        }
    }
}
