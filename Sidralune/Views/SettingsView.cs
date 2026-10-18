using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Sidralune.Core;
using Sidralune.Core.Ephemeris;
using Sidralune.Core.Mathematics;
using Sidralune.Core.Util;
using Sidralune.UI;

namespace Sidralune.Views
{
    public class SettingsView : UserControl
    {
        private readonly TabbedPanel _tabs = new TabbedPanel { Dock = DockStyle.Fill, Padding = new Padding(12) };

        public SettingsView()
        {
            Controls.Add(_tabs);
            _tabs.AddPage("Location", BuildLocation());
            _tabs.AddPage("Equipment", BuildEquipment());
            _tabs.AddPage("Data", BuildData());
            _tabs.AddPage("Observer & accounts", BuildObserver());
            _tabs.AddPage("Appearance", BuildAppearance());
            _tabs.AddPage("About", BuildAbout());
        }

        // ------------------------------------------------------------------ location

        private Control BuildLocation()
        {
            var panel = new Panel { Dock = DockStyle.Fill };
            var list = new ListBox { Dock = DockStyle.Left, Width = 220 };
            var editor = new Card("Site") { Dock = DockStyle.Fill };
            var grid = new FormGrid(170);
            var name = grid.Row("Name", new TextBox());
            var lat = grid.Row("Latitude (°, N positive)", new TextBox());
            var lon = grid.Row("Longitude (°, E positive)", new TextBox());
            var elev = grid.Row("Elevation (m)", Ui.Num(0, -500, 9000));
            var tz = grid.Row("Time zone", new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList });
            foreach (var z in TimeZoneInfo.GetSystemTimeZones()) tz.Items.Add(z.Id);
            var sqm = grid.Row("Sky brightness (SQM, mag/arcsec²)", Ui.Num(21, 15, 22.5m, 2, 0.05m));
            var bortle = grid.Row("Bortle class", new Label { Tag = "dim" });
            var ext = grid.Row("Extinction k(V) (mag/airmass)", Ui.Num(0.2m, 0.05m, 1, 2, 0.01m));
            var seeing = grid.Row("Typical seeing (arcsec)", Ui.Num(2.5m, 0.3m, 10, 1, 0.1m));
            var temp = grid.Row("Temperature (°C) for refraction", Ui.Num(10, -50, 50, 1));
            var press = grid.Row("Pressure (hPa)", Ui.Num(1010, 500, 1100));
            var mpc = grid.Row("MPC observatory code", new TextBox());
            var horizon = grid.Row("Horizon profile (az:alt, …)", new TextBox { Multiline = true, Height = 60 });
            grid.Full(Ui.Hint("Coordinates accept decimal degrees or sexagesimal (e.g. 51 28 38 N, -0 00 05). The horizon profile masks trees/buildings in charts and planners. SQM drives the visibility model; Bortle is derived from it."));
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40 };
            var save = new FlatButton("Save site", Icons.Save) { Kind = ButtonKind.Primary };
            var add = new FlatButton("New site", Icons.Add);
            var del = new FlatButton("Delete", Icons.Remove) { Kind = ButtonKind.Danger };
            var use = new FlatButton("Use this site", Icons.Check);
            foreach (var b in new[] { save, use, add, del }) { b.AutoSizeToText(); buttons.Controls.Add(b); }
            grid.Full(buttons);
            editor.Controls.Add(grid);
            editor.AutoScroll = true;
            panel.Controls.Add(editor);
            panel.Controls.Add(new Panel { Dock = DockStyle.Left, Width = 12 });
            panel.Controls.Add(list);
            Action reload = () =>
            {
                list.Items.Clear();
                for (int i = 0; i < AppState.Settings.Sites.Count; i++) list.Items.Add((i == AppState.Settings.ActiveSite ? "● " : "   ") + AppState.Settings.Sites[i].Name);
                if (list.Items.Count > 0 && list.SelectedIndex < 0) list.SelectedIndex = AppState.Settings.ActiveSite;
            };
            Action<Observer> show = o =>
            {
                name.Text = o.Name; lat.Text = o.LatitudeDeg.ToString("0.00000", CultureInfo.InvariantCulture); lon.Text = o.LongitudeDeg.ToString("0.00000", CultureInfo.InvariantCulture);
                elev.Value = (decimal)AstroMath.Clamp(o.ElevationM, -500, 9000); tz.SelectedItem = o.TimeZoneId ?? TimeZoneInfo.Local.Id;
                sqm.Value = (decimal)AstroMath.Clamp(o.SqmZenith, 15, 22.5); ext.Value = (decimal)AstroMath.Clamp(o.ExtinctionV, 0.05, 1); seeing.Value = (decimal)AstroMath.Clamp(o.SeeingArcsec, 0.3, 10);
                temp.Value = (decimal)AstroMath.Clamp(o.TemperatureC, -50, 50); press.Value = (decimal)AstroMath.Clamp(o.PressureHpa, 500, 1100); mpc.Text = o.MpcCode;
                horizon.Text = string.Join(", ", (o.HorizonProfile ?? new List<double[]>()).Select(h => h[0].ToString("0", CultureInfo.InvariantCulture) + ":" + h[1].ToString("0.#", CultureInfo.InvariantCulture)));
                bortle.Text = "Bortle " + Observer.SqmToBortle(o.SqmZenith) + string.Format("  ·  naked-eye limit ≈ {0:0.0}", Core.Observing.VisibilityModel.NakedEyeLimit(o.SqmZenith));
            };
            list.SelectedIndexChanged += (s, e) => { if (list.SelectedIndex >= 0) show(AppState.Settings.Sites[list.SelectedIndex]); };
            sqm.ValueChanged += (s, e) => bortle.Text = "Bortle " + Observer.SqmToBortle((double)sqm.Value) + string.Format("  ·  naked-eye limit ≈ {0:0.0}", Core.Observing.VisibilityModel.NakedEyeLimit((double)sqm.Value));
            save.Click += (s, e) =>
            {
                if (list.SelectedIndex < 0) return;
                var o = AppState.Settings.Sites[list.SelectedIndex];
                double la = AstroMath.ParseSexagesimal(lat.Text), lo = AstroMath.ParseSexagesimal(lon.Text);
                if (double.IsNaN(la) || la < -90 || la > 90 || double.IsNaN(lo)) { MainForm.Instance.Toast("Invalid coordinates", Icons.Warning, Theme.Current.Danger); return; }
                o.Name = name.Text; o.LatitudeDeg = la; o.LongitudeDeg = AstroMath.NormalizeDeg180(lo); o.ElevationM = (double)elev.Value; o.TimeZoneId = tz.SelectedItem as string;
                o.SqmZenith = (double)sqm.Value; o.Bortle = Observer.SqmToBortle(o.SqmZenith); o.ExtinctionV = (double)ext.Value; o.SeeingArcsec = (double)seeing.Value;
                o.TemperatureC = (double)temp.Value; o.PressureHpa = (double)press.Value; o.MpcCode = mpc.Text.Trim();
                o.HorizonProfile = horizon.Text.Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split(':')).Where(p => p.Length == 2)
                    .Select(p => new[] { AstroMath.ParseInvariant(p[0]), AstroMath.ParseInvariant(p[1]) }).Where(p => !double.IsNaN(p[0]) && !double.IsNaN(p[1])).OrderBy(p => p[0]).ToList();
                int sel = list.SelectedIndex;
                reload(); list.SelectedIndex = sel;
                AppState.NotifySiteChanged();
                MainForm.Instance.Toast("Site saved");
            };
            add.Click += (s, e) => { AppState.Settings.Sites.Add(new Observer { Name = "New site", LatitudeDeg = AppState.Site.LatitudeDeg, LongitudeDeg = AppState.Site.LongitudeDeg, TimeZoneId = AppState.Site.TimeZoneId }); reload(); list.SelectedIndex = list.Items.Count - 1; };
            del.Click += (s, e) => { if (AppState.Settings.Sites.Count > 1 && list.SelectedIndex >= 0) { AppState.Settings.Sites.RemoveAt(list.SelectedIndex); AppState.Settings.ActiveSite = 0; reload(); list.SelectedIndex = 0; AppState.NotifySiteChanged(); } };
            use.Click += (s, e) => { if (list.SelectedIndex >= 0) { AppState.Settings.ActiveSite = list.SelectedIndex; int sel = list.SelectedIndex; reload(); list.SelectedIndex = sel; AppState.NotifySiteChanged(); MainForm.Instance.Toast("Active site: " + AppState.Site.Name); } };
            reload();
            return panel;
        }

        // ------------------------------------------------------------------ equipment

        private Control BuildEquipment()
        {
            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 2 };
            for (int i = 0; i < 3; i++) t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.3f));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, 120));
            t.Controls.Add(ProfileEditor("Telescopes", AppState.Settings.Telescopes, () => new TelescopeProfile { Name = "New telescope" }, () => AppState.Settings.ActiveTelescope, v => AppState.Settings.ActiveTelescope = v), 0, 0);
            t.Controls.Add(ProfileEditor("Eyepieces", AppState.Settings.Eyepieces, () => new EyepieceProfile { Name = "New eyepiece" }, () => AppState.Settings.ActiveEyepiece, v => AppState.Settings.ActiveEyepiece = v), 1, 0);
            t.Controls.Add(ProfileEditor("Cameras", AppState.Settings.Cameras, () => new CameraProfile { Name = "New camera" }, () => AppState.Settings.ActiveCamera, v => AppState.Settings.ActiveCamera = v), 2, 0);
            var info = new Label { Dock = DockStyle.Fill, Padding = new Padding(8) };
            Action upd = () =>
            {
                var ins = AppState.Instrument; var rig = AppState.ImagingRig;
                info.Text = string.Format("Visual: {0} with the selected eyepiece gives ×{1:0}, true field {2}, exit pupil {3:0.0} mm, Dawes limit {4:0.00}\".\r\n" +
                    "Imaging: {5}: {6:0.00}\"/px, field {7:0.00}° × {8:0.00}°, f/{9:0.0}. Optimal sub-exposure at SQM {10:0.0}: {11:0} s.  Planetary f-ratio for this pixel size: f/{12:0.0} (Nyquist).",
                    ins.Name, ins.Magnification, AstroMath.FormatAngleAuto(ins.TrueFieldDeg), ins.ExitPupilMm, ins.DawesLimit,
                    rig.Name, rig.PixelScaleArcsec, rig.FieldWidthDeg, rig.FieldHeightDeg, rig.FocalRatio, AppState.Site.SqmZenith,
                    Core.Observing.ExposureCalculator.OptimalSubExposure(rig, AppState.Site.SqmZenith), Core.Observing.ExposureCalculator.OptimalPlanetaryFRatio(rig.PixelSizeUm));
            };
            upd();
            var card = new Card("Combination summary") { Dock = DockStyle.Fill };
            card.Controls.Add(info);
            t.Controls.Add(card, 0, 1);
            t.SetColumnSpan(card, 3);
            t.VisibleChanged += (s, e) => upd();
            return t;
        }

        private Control ProfileEditor<T>(string title, List<T> items, Func<T> create, Func<int> getActive, Action<int> setActive) where T : class
        {
            var card = new Card(title) { Dock = DockStyle.Fill };
            var list = new ListBox { Dock = DockStyle.Top, Height = 120 };
            var pg = new PropertyGrid { Dock = DockStyle.Fill, HelpVisible = false, PropertySort = PropertySort.NoSort };
            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 38 };
            var add = new FlatButton("Add", Icons.Add); add.AutoSizeToText();
            var del = new FlatButton("Remove", Icons.Remove) { Kind = ButtonKind.Danger }; del.AutoSizeToText();
            var act = new FlatButton("Use", Icons.Check); act.AutoSizeToText();
            bar.Controls.Add(act); bar.Controls.Add(add); bar.Controls.Add(del);
            Action reload = () =>
            {
                int sel = list.SelectedIndex;
                list.Items.Clear();
                for (int i = 0; i < items.Count; i++) list.Items.Add((i == getActive() ? "● " : "   ") + (string)typeof(T).GetField("Name").GetValue(items[i]));
                list.SelectedIndex = Math.Min(Math.Max(0, sel), list.Items.Count - 1);
            };
            list.SelectedIndexChanged += (s, e) => { if (list.SelectedIndex >= 0) pg.SelectedObject = new GridAdapter(items[list.SelectedIndex], title); };
            pg.PropertyValueChanged += (s, e) => { AppState.Save(); reload(); };
            add.Click += (s, e) => { items.Add(create()); reload(); list.SelectedIndex = items.Count - 1; AppState.Save(); };
            del.Click += (s, e) => { if (items.Count > 1 && list.SelectedIndex >= 0) { items.RemoveAt(list.SelectedIndex); setActive(0); reload(); AppState.Save(); } };
            act.Click += (s, e) => { if (list.SelectedIndex >= 0) { setActive(list.SelectedIndex); reload(); AppState.Save(); MainForm.Instance.Toast(title + ": active profile changed"); } };
            card.Controls.Add(pg);
            card.Controls.Add(bar);
            card.Controls.Add(list);
            reload();
            return card;
        }

        // ------------------------------------------------------------------ data manager

        private Control BuildData()
        {
            var panel = new Panel { Dock = DockStyle.Fill };
            var grid = Ui.Grid();
            grid.ReadOnly = false;
            grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "", Width = 30, FillWeight = 8 });
            grid.Columns.Add("name", "Dataset"); grid.Columns.Add("desc", "Description"); grid.Columns.Add("size", "Size"); grid.Columns.Add("status", "Status"); grid.Columns.Add("lic", "License");
            grid.Columns[1].FillWeight = 60; grid.Columns[2].FillWeight = 130; grid.Columns[3].FillWeight = 25; grid.Columns[4].FillWeight = 40; grid.Columns[5].FillWeight = 30;
            for (int i = 1; i < grid.Columns.Count; i++) grid.Columns[i].ReadOnly = true;
            Action reload = () =>
            {
                grid.Rows.Clear();
                foreach (var d in DataManager.Datasets)
                {
                    int r = grid.Rows.Add(!d.Installed && d.Recommended, d.Name, d.Description, d.SizeText, d.Installed ? "Installed " + d.InstalledDate.Value.ToString("yyyy-MM-dd") : "Not installed", d.License);
                    grid.Rows[r].Tag = d;
                }
            };
            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 42 };
            var dl = new FlatButton("Download selected", Icons.Download) { Kind = ButtonKind.Primary }; dl.AutoSizeToText();
            var upd = new FlatButton("Update installed", Icons.Refresh); upd.AutoSizeToText();
            var rem = new FlatButton("Remove selected", Icons.Remove) { Kind = ButtonKind.Danger }; rem.AutoSizeToText();
            var open = new FlatButton("Open data folder", Icons.Open, (s, e) => System.Diagnostics.Process.Start(AppPaths.Data)); open.AutoSizeToText();
            var reloadBtn = new FlatButton("Reload catalogs", Icons.Refresh, (s, e) => Tasks.Run("Reloading catalogs", t => AppState.LoadData(), () => MainForm.Instance.Toast("Catalogs reloaded"))); reloadBtn.AutoSizeToText();
            bar.Controls.AddRange(new Control[] { dl, upd, rem, open, reloadBtn });
            var status = new Label { Dock = DockStyle.Top, Height = 60, Padding = new Padding(4, 8, 4, 0) };
            Action refreshStatus = () => status.Text = "Ephemeris: " + EphemerisManager.Describe() + "\r\nStars: " + Core.Catalogs.CatalogManager.Instance.StarSource + "   ·   Deep sky: " + Core.Catalogs.CatalogManager.Instance.DeepSkySource +
                "   ·   Minor bodies: " + Core.MinorBodies.MinorBodyStore.Instance.Asteroids.Count + " asteroids, " + Core.MinorBodies.MinorBodyStore.Instance.Comets.Count + " comets\r\nData folder: " + AppPaths.Data;
            Action<IEnumerable<Dataset>> download = sets =>
            {
                var list = sets.ToList();
                if (list.Count == 0) { MainForm.Instance.Toast("Nothing selected"); return; }
                long total = list.Sum(d => d.ApproxBytes);
                if (MessageBox.Show(this, string.Format("Download {0} dataset(s), about {1:0.0} MB, from public archives (JPL, MPC, GitHub, CelesTrak)?", list.Count, total / 1048576.0), "Download data", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
                Tasks.Run("Downloading data", t =>
                {
                    for (int i = 0; i < list.Count; i++)
                    {
                        var d = list[i];
                        t.Status = d.Name;
                        int idx = i;
                        DataManager.Install(d, (done, tot) => { if (tot > 0) Tasks.Report(t, (idx + (double)done / tot) / list.Count, d.Name); }, t.Cts.Token);
                    }
                    t.Status = "loading";
                    AppState.LoadData();
                }, () => { reload(); refreshStatus(); MainForm.Instance.Toast("Data installed and loaded"); });
            };
            dl.Click += (s, e) => { grid.EndEdit(); download(grid.Rows.Cast<DataGridViewRow>().Where(r => (bool)(r.Cells[0].Value ?? false)).Select(r => (Dataset)r.Tag)); };
            upd.Click += (s, e) => download(DataManager.Datasets.Where(d => d.Installed));
            rem.Click += (s, e) =>
            {
                grid.EndEdit();
                foreach (DataGridViewRow r in grid.Rows)
                {
                    if (!(bool)(r.Cells[0].Value ?? false)) continue;
                    var d = (Dataset)r.Tag;
                    if (d.Id == "de440s") EphemerisManager.UnloadSpk(); // the SPK file is held open while loaded
                    try { DataManager.Remove(d); }
                    catch (IOException ex) { MainForm.Instance.Toast("Could not remove " + d.Name + ": " + ex.Message, Icons.Warning, Theme.Current.Warning); }
                }
                reload();
            };
            var hint = Ui.Hint("Nothing is downloaded automatically. Recommended datasets are pre-selected. DE440s gives milliarcsecond positions of the Sun, Moon and planets for 1849–2150 (otherwise an analytic theory accurate to arcseconds–arcminutes is used). Comet and asteroid orbits should be refreshed weekly.");
            hint.Dock = DockStyle.Top;
            panel.Controls.Add(grid);
            panel.Controls.Add(status);
            panel.Controls.Add(bar);
            panel.Controls.Add(hint);
            reload(); refreshStatus();
            panel.VisibleChanged += (s, e) => { if (panel.Visible) { reload(); refreshStatus(); } };
            return panel;
        }

        // ------------------------------------------------------------------ observer & appearance

        private Control BuildObserver()
        {
            var card = new Card("Observer") { Dock = DockStyle.Fill, AutoScroll = true };
            var g = new FormGrid(220);
            var age = g.Row("Age (affects dark-adapted pupil)", Ui.Num(AppState.Settings.ObserverAge, 8, 99));
            var exp = g.Row("Visual experience", Ui.Combo("Beginner", "Average", "Experienced", "Expert"));
            exp.SelectedIndex = AppState.Settings.ObserverExperience;
            var aavso = g.Row("AAVSO observer code", new TextBox { Text = AppState.Settings.AavsoObserverCode });
            var mpcc = g.Row("Default MPC station code", new TextBox { Text = AppState.Settings.MpcStationCode });
            var nova = g.Row("nova.astrometry.net API key", new TextBox { Text = AppState.Settings.NovaApiKey, UseSystemPasswordChar = true });
            var folder = g.Row("Image folder", new TextBox { Text = AppState.Settings.ImageFolder });
            g.Full(Ui.Hint("Age and experience tune the visibility predictions (dark-adapted pupil ~7.8 mm at 20 years, decreasing ~0.45 mm per decade). The API key is only needed for online plate solving via astrometry.net; it is stored locally."));
            var save = new FlatButton("Save", Icons.Save) { Kind = ButtonKind.Primary }; save.AutoSizeToText();
            save.Click += (s, e) =>
            {
                AppState.Settings.ObserverAge = (int)age.Value; AppState.Settings.ObserverExperience = exp.SelectedIndex;
                AppState.Settings.AavsoObserverCode = aavso.Text.Trim(); AppState.Settings.MpcStationCode = mpcc.Text.Trim();
                AppState.Settings.NovaApiKey = nova.Text.Trim(); AppState.Settings.ImageFolder = folder.Text.Trim();
                AppState.Save(); MainForm.Instance.Toast("Saved");
            };
            g.Full(save);
            card.Controls.Add(g);
            return card;
        }

        private Control BuildAppearance()
        {
            var card = new Card("Appearance") { Dock = DockStyle.Fill };
            var g = new FormGrid(220);
            var night = g.Row("Night vision (red) mode", new CheckBox { Checked = AppState.Settings.NightMode, Text = "Use deep red colours to protect dark adaptation" });
            night.CheckedChanged += (s, e) => { AppState.Settings.NightMode = night.Checked; Theme.SetNightMode(night.Checked); AppState.Save(); };
            var refr = g.Row("Atmospheric refraction", new CheckBox { Checked = AppState.Settings.UseRefraction, Text = "Apply refraction to horizontal coordinates" });
            refr.CheckedChanged += (s, e) => { AppState.Settings.UseRefraction = refr.Checked; AppState.Save(); };
            g.Full(Ui.Hint("Keyboard: Ctrl+K command palette · Ctrl+1…9 switch workspace · Ctrl+Shift+N night mode · in charts: arrows pan, +/− zoom, N/E/S/W/Z look direction, double-click centre."));
            card.Controls.Add(g);
            return card;
        }

        private Control BuildAbout()
        {
            var card = new Card("Sidralune") { Dock = DockStyle.Fill };
            var t = new Label { Dock = DockStyle.Fill, Text =
                "Sidralune 0.1.0 — an integrated astronomy suite for planning, observing, imaging and science.\r\n\r\n" +
                "Algorithms: IAU 2006 precession and nutation, JPL DE440 (SPK) or Standish/ELP2000 analytic ephemerides, SGP4 (Vallado 2006),\r\n" +
                "Krisciunas & Schaefer sky brightness, Schaefer visual thresholds, triangle-invariant star matching with RANSAC, TAN-SIP WCS,\r\n" +
                "DeepSkyStacker-style stacking with kappa-sigma, winsorized, linear-fit and generalized-ESD rejection, drizzle (Fruchter & Hook),\r\n" +
                "Richardson-Lucy with total variation (Dey et al.), starlet wavelets (Starck & Murtagh), generalized hyperbolic stretch,\r\n" +
                "multi-point lucky imaging, WinJUPOS-style derotation, generalised Lomb-Scargle, BLS, PDM, Kwee-van Woerden, Mandel-Agol-type transit model with emcee-style MCMC.\r\n\r\n" +
                "Data © their providers: JPL/NASA, Minor Planet Center, HYG Database (D. Nash), OpenNGC (M. Verga), Stellarium, CDS (SIMBAD/VizieR), AAVSO, CelesTrak.\r\n\r\n" +
                "Settings and data: " + AppPaths.Root };
            card.Controls.Add(t);
            return card;
        }
    }
}
