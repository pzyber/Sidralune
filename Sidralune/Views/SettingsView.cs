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
using Sidralune.Imaging.ColorManagement;
using Sidralune.UI;
using static Sidralune.Core.Util.Lang;

namespace Sidralune.Views
{
    public class SettingsView : UserControl
    {
        private readonly TabbedPanel _tabs = new TabbedPanel { Dock = DockStyle.Fill, Padding = new Padding(12) };

        public SettingsView()
        {
            Controls.Add(_tabs);
            _tabs.AddPage(T("Location"), BuildLocation());
            _tabs.AddPage(T("Equipment"), BuildEquipment());
            _tabs.AddPage(T("Data"), BuildData());
            _tabs.AddPage(T("Observer & accounts"), BuildObserver());
            _tabs.AddPage(T("Appearance"), BuildAppearance());
            _tabs.AddPage(T("Colour"), BuildColour());
            _tabs.AddPage(T("About"), BuildAbout());
        }

        // ------------------------------------------------------------------ location

        private Control BuildLocation()
        {
            var panel = new Panel { Dock = DockStyle.Fill };
            var list = new ListBox { Dock = DockStyle.Left, Width = 220 };
            var editor = new Card(T("Site")) { Dock = DockStyle.Fill };
            var grid = new FormGrid(170);
            var name = grid.Row(T("Name"), new TextBox());
            var lat = grid.Row(T("Latitude (°, N positive)"), new TextBox());
            var lon = grid.Row(T("Longitude (°, E positive)"), new TextBox());
            var elev = grid.Row(T("Elevation (m)"), Ui.Num(0, -500, 9000));
            var tz = grid.Row(T("Time zone"), new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList });
            foreach (var z in TimeZoneInfo.GetSystemTimeZones()) tz.Items.Add(z.Id);
            var sqm = grid.Row(T("Sky brightness (SQM, mag/arcsec²)"), Ui.Num(21, 15, 22.5m, 2, 0.05m));
            var bortle = grid.Row(T("Bortle class"), new Label { Tag = "dim" });
            var ext = grid.Row(T("Extinction k(V) (mag/airmass)"), Ui.Num(0.2m, 0.05m, 1, 2, 0.01m));
            var seeing = grid.Row(T("Typical seeing (arcsec)"), Ui.Num(2.5m, 0.3m, 10, 1, 0.1m));
            var temp = grid.Row(T("Temperature (°C) for refraction"), Ui.Num(10, -50, 50, 1));
            var press = grid.Row(T("Pressure (hPa)"), Ui.Num(1010, 500, 1100));
            var mpc = grid.Row(T("MPC observatory code"), new TextBox());
            var horizon = grid.Row(T("Horizon profile (az:alt, …)"), new TextBox { Multiline = true, Height = 60 });
            grid.Full(Ui.Hint(T("Coordinates accept decimal degrees or sexagesimal (e.g. 51 28 38 N, -0 00 05). The horizon profile masks trees/buildings in charts and planners. SQM drives the visibility model; Bortle is derived from it.")));
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40 };
            var save = new FlatButton(T("Save site"), Icons.Save) { Kind = ButtonKind.Primary };
            var add = new FlatButton(T("New site"), Icons.Add);
            var del = new FlatButton(T("Delete"), Icons.Remove) { Kind = ButtonKind.Danger };
            var use = new FlatButton(T("Use this site"), Icons.Check);
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
            Func<double, string> bortleText = q => T("Bortle {0}  ·  naked-eye limit ≈ {1:0.0}", Observer.SqmToBortle(q), Core.Observing.VisibilityModel.NakedEyeLimit(q));
            Action<Observer> show = o =>
            {
                name.Text = o.Name; lat.Text = o.LatitudeDeg.ToString("0.00000", CultureInfo.InvariantCulture); lon.Text = o.LongitudeDeg.ToString("0.00000", CultureInfo.InvariantCulture);
                elev.Value = (decimal)AstroMath.Clamp(o.ElevationM, -500, 9000); tz.SelectedItem = o.TimeZoneId ?? TimeZoneInfo.Local.Id;
                sqm.Value = (decimal)AstroMath.Clamp(o.SqmZenith, 15, 22.5); ext.Value = (decimal)AstroMath.Clamp(o.ExtinctionV, 0.05, 1); seeing.Value = (decimal)AstroMath.Clamp(o.SeeingArcsec, 0.3, 10);
                temp.Value = (decimal)AstroMath.Clamp(o.TemperatureC, -50, 50); press.Value = (decimal)AstroMath.Clamp(o.PressureHpa, 500, 1100); mpc.Text = o.MpcCode;
                horizon.Text = string.Join(", ", (o.HorizonProfile ?? new List<double[]>()).Select(h => h[0].ToString("0", CultureInfo.InvariantCulture) + ":" + h[1].ToString("0.#", CultureInfo.InvariantCulture)));
                bortle.Text = bortleText(o.SqmZenith);
            };
            list.SelectedIndexChanged += (s, e) => { if (list.SelectedIndex >= 0) show(AppState.Settings.Sites[list.SelectedIndex]); };
            sqm.ValueChanged += (s, e) => bortle.Text = bortleText((double)sqm.Value);
            save.Click += (s, e) =>
            {
                if (list.SelectedIndex < 0) return;
                var o = AppState.Settings.Sites[list.SelectedIndex];
                double la = AstroMath.ParseSexagesimal(lat.Text), lo = AstroMath.ParseSexagesimal(lon.Text);
                if (double.IsNaN(la) || la < -90 || la > 90 || double.IsNaN(lo)) { MainForm.Instance.Toast(T("Invalid coordinates"), Icons.Warning, Theme.Current.Danger); return; }
                o.Name = name.Text; o.LatitudeDeg = la; o.LongitudeDeg = AstroMath.NormalizeDeg180(lo); o.ElevationM = (double)elev.Value; o.TimeZoneId = tz.SelectedItem as string;
                o.SqmZenith = (double)sqm.Value; o.Bortle = Observer.SqmToBortle(o.SqmZenith); o.ExtinctionV = (double)ext.Value; o.SeeingArcsec = (double)seeing.Value;
                o.TemperatureC = (double)temp.Value; o.PressureHpa = (double)press.Value; o.MpcCode = mpc.Text.Trim();
                o.HorizonProfile = horizon.Text.Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split(':')).Where(p => p.Length == 2)
                    .Select(p => new[] { AstroMath.ParseInvariant(p[0]), AstroMath.ParseInvariant(p[1]) }).Where(p => !double.IsNaN(p[0]) && !double.IsNaN(p[1])).OrderBy(p => p[0]).ToList();
                int sel = list.SelectedIndex;
                reload(); list.SelectedIndex = sel;
                AppState.NotifySiteChanged();
                MainForm.Instance.Toast(T("Site saved"));
            };
            add.Click += (s, e) => { AppState.Settings.Sites.Add(new Observer { Name = T("New site"), LatitudeDeg = AppState.Site.LatitudeDeg, LongitudeDeg = AppState.Site.LongitudeDeg, TimeZoneId = AppState.Site.TimeZoneId }); reload(); list.SelectedIndex = list.Items.Count - 1; };
            del.Click += (s, e) => { if (AppState.Settings.Sites.Count > 1 && list.SelectedIndex >= 0) { AppState.Settings.Sites.RemoveAt(list.SelectedIndex); AppState.Settings.ActiveSite = 0; reload(); list.SelectedIndex = 0; AppState.NotifySiteChanged(); } };
            use.Click += (s, e) => { if (list.SelectedIndex >= 0) { AppState.Settings.ActiveSite = list.SelectedIndex; int sel = list.SelectedIndex; reload(); list.SelectedIndex = sel; AppState.NotifySiteChanged(); MainForm.Instance.Toast(T("Active site: {0}", AppState.Site.Name)); } };
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
            t.Controls.Add(ProfileEditor(T("Telescopes"), AppState.Settings.Telescopes, () => new TelescopeProfile { Name = T("New telescope") }, () => AppState.Settings.ActiveTelescope, v => AppState.Settings.ActiveTelescope = v), 0, 0);
            t.Controls.Add(ProfileEditor(T("Eyepieces"), AppState.Settings.Eyepieces, () => new EyepieceProfile { Name = T("New eyepiece") }, () => AppState.Settings.ActiveEyepiece, v => AppState.Settings.ActiveEyepiece = v), 1, 0);
            t.Controls.Add(ProfileEditor(T("Cameras"), AppState.Settings.Cameras, () => new CameraProfile { Name = T("New camera") }, () => AppState.Settings.ActiveCamera, v => AppState.Settings.ActiveCamera = v), 2, 0);
            var info = new Label { Dock = DockStyle.Fill, Padding = new Padding(8) };
            Action upd = () =>
            {
                var ins = AppState.Instrument; var rig = AppState.ImagingRig;
                info.Text = T("Visual: {0} with the selected eyepiece gives ×{1:0}, true field {2}, exit pupil {3:0.0} mm, Dawes limit {4:0.00}\".",
                        ins.Name, ins.Magnification, AstroMath.FormatAngleAuto(ins.TrueFieldDeg), ins.ExitPupilMm, ins.DawesLimit) + "\r\n" +
                    T("Imaging: {0}: {1:0.00}\"/px, field {2:0.00}° × {3:0.00}°, f/{4:0.0}. Optimal sub-exposure at SQM {5:0.0}: {6:0} s.  Planetary f-ratio for this pixel size: f/{7:0.0} (Nyquist).",
                        rig.Name, rig.PixelScaleArcsec, rig.FieldWidthDeg, rig.FieldHeightDeg, rig.FocalRatio, AppState.Site.SqmZenith,
                        Core.Observing.ExposureCalculator.OptimalSubExposure(rig, AppState.Site.SqmZenith), Core.Observing.ExposureCalculator.OptimalPlanetaryFRatio(rig.PixelSizeUm));
            };
            upd();
            var card = new Card(T("Combination summary")) { Dock = DockStyle.Fill };
            card.Controls.Add(info);
            t.Controls.Add(card, 0, 1);
            t.SetColumnSpan(card, 3);
            t.VisibleChanged += (s, e) => upd();
            return t;
        }

        private Control ProfileEditor<TProfile>(string title, List<TProfile> items, Func<TProfile> create, Func<int> getActive, Action<int> setActive) where TProfile : class
        {
            var card = new Card(title) { Dock = DockStyle.Fill };
            var list = new ListBox { Dock = DockStyle.Top, Height = 120 };
            var pg = new PropertyGrid { Dock = DockStyle.Fill, HelpVisible = false, PropertySort = PropertySort.NoSort };
            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 38 };
            var add = new FlatButton(T("Add"), Icons.Add); add.AutoSizeToText();
            var del = new FlatButton(T("Remove"), Icons.Remove) { Kind = ButtonKind.Danger }; del.AutoSizeToText();
            var act = new FlatButton(T("Use"), Icons.Check); act.AutoSizeToText();
            bar.Controls.Add(act); bar.Controls.Add(add); bar.Controls.Add(del);
            Action reload = () =>
            {
                int sel = list.SelectedIndex;
                list.Items.Clear();
                for (int i = 0; i < items.Count; i++) list.Items.Add((i == getActive() ? "● " : "   ") + (string)typeof(TProfile).GetField("Name").GetValue(items[i]));
                list.SelectedIndex = Math.Min(Math.Max(0, sel), list.Items.Count - 1);
            };
            list.SelectedIndexChanged += (s, e) => { if (list.SelectedIndex >= 0) pg.SelectedObject = new GridAdapter(items[list.SelectedIndex], title); };
            pg.PropertyValueChanged += (s, e) => { AppState.Save(); reload(); };
            add.Click += (s, e) => { items.Add(create()); reload(); list.SelectedIndex = items.Count - 1; AppState.Save(); };
            del.Click += (s, e) => { if (items.Count > 1 && list.SelectedIndex >= 0) { items.RemoveAt(list.SelectedIndex); setActive(0); reload(); AppState.Save(); } };
            act.Click += (s, e) => { if (list.SelectedIndex >= 0) { setActive(list.SelectedIndex); reload(); AppState.Save(); MainForm.Instance.Toast(T("{0}: active profile changed", title)); } };
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
            grid.Columns.Add("name", T("Dataset")); grid.Columns.Add("desc", T("Description")); grid.Columns.Add("size", T("Size")); grid.Columns.Add("status", T("Status")); grid.Columns.Add("lic", T("License"));
            grid.Columns[1].FillWeight = 60; grid.Columns[2].FillWeight = 130; grid.Columns[3].FillWeight = 25; grid.Columns[4].FillWeight = 40; grid.Columns[5].FillWeight = 30;
            for (int i = 1; i < grid.Columns.Count; i++) grid.Columns[i].ReadOnly = true;
            Action reload = () =>
            {
                grid.Rows.Clear();
                foreach (var d in DataManager.Datasets)
                {
                    int r = grid.Rows.Add(!d.Installed && d.Recommended, T(d.Name), T(d.Description), d.SizeText, d.Installed ? T("Installed {0:yyyy-MM-dd}", d.InstalledDate.Value) : T("Not installed"), d.License);
                    grid.Rows[r].Tag = d;
                }
            };
            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 42 };
            var dl = new FlatButton(T("Download selected"), Icons.Download) { Kind = ButtonKind.Primary }; dl.AutoSizeToText();
            var upd = new FlatButton(T("Update installed"), Icons.Refresh); upd.AutoSizeToText();
            var rem = new FlatButton(T("Remove selected"), Icons.Remove) { Kind = ButtonKind.Danger }; rem.AutoSizeToText();
            var open = new FlatButton(T("Open data folder"), Icons.Open, (s, e) => System.Diagnostics.Process.Start(AppPaths.Data)); open.AutoSizeToText();
            var reloadBtn = new FlatButton(T("Reload catalogs"), Icons.Refresh, (s, e) => Tasks.Data.Run(T("Reloading catalogs"), t => AppState.LoadData(), () => MainForm.Instance.Toast(T("Catalogs reloaded")))); reloadBtn.AutoSizeToText();
            string downloading = T("Downloading data");
            var stopDl = Tasks.Data.StopButton(n => n == downloading); // ends a download; what is not complete is not kept
            bar.Controls.AddRange(new Control[] { dl, upd, stopDl, rem, open, reloadBtn });
            Tasks.Data.Guard(dl, upd, rem, reloadBtn);
            var status = new Label { Dock = DockStyle.Top, Height = 60, Padding = new Padding(4, 8, 4, 0) };
            Action refreshStatus = () => status.Text = T("Ephemeris: {0}", EphemerisManager.Describe()) + "\r\n" +
                T("Stars: {0}   ·   Deep sky: {1}   ·   Minor bodies: {2} asteroids, {3} comets", Core.Catalogs.CatalogManager.Instance.StarSource, Core.Catalogs.CatalogManager.Instance.DeepSkySource,
                    Core.MinorBodies.MinorBodyStore.Instance.Asteroids.Count, Core.MinorBodies.MinorBodyStore.Instance.Comets.Count) + "\r\n" +
                T("Data folder: {0}", AppPaths.Data);
            Action<IEnumerable<Dataset>> download = sets =>
            {
                var list = sets.ToList();
                if (list.Count == 0) { MainForm.Instance.Toast(T("Nothing selected")); return; }
                if (Tasks.Data.Refuse()) return;
                long total = list.Sum(d => d.ApproxBytes);
                if (MessageBox.Show(this, Tn("Download {0} dataset, about {1:0.0} MB, from public archives (JPL, MPC, GitHub, CelesTrak)?", "Download {0} datasets, about {1:0.0} MB, from public archives (JPL, MPC, GitHub, CelesTrak)?", list.Count, list.Count, total / 1048576.0),
                    T("Download data"), MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
                // a dataset whose server fails does not stop the others: what did arrive is loaded, and the ones that
                // did not are named afterwards (and kept as they were)
                var failed = new List<KeyValuePair<Dataset, Exception>>();
                Tasks.Data.Run(downloading, t =>
                {
                    t.Status = T(list[0].Name);
                    failed = DataManager.InstallAll(list, (idx, done, tot) => { if (tot > 0) Tasks.Report(t, (idx + (double)done / tot) / list.Count, T(list[idx].Name)); }, t.Cts.Token);
                    foreach (var f in failed) Log.Error(downloading, f.Key.Name + ": " + f.Value.Message);
                    t.Status = T("loading");
                    AppState.LoadData();
                }, () =>
                {
                    reload(); refreshStatus();
                    if (failed.Count < list.Count) MainForm.Instance.Toast(T("Data installed and loaded"));
                    foreach (var f in failed) MainForm.Instance.Toast(T("{0} failed: {1}", T(f.Key.Name), f.Value.Message), Icons.Warning, Theme.Current.Danger);
                });
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
                    try { DataManager.Remove(d); }
                    catch (IOException ex) { MainForm.Instance.Toast(T("Could not remove {0}: {1}", T(d.Name), ex.Message), Icons.Warning, Theme.Current.Warning); }
                }
                reload();
            };
            var hint = Ui.Hint(T("Nothing is downloaded automatically. Recommended datasets are pre-selected. DE440s gives milliarcsecond positions of the Sun, Moon and planets for 1849–2150 (otherwise an analytic theory accurate to arcseconds–arcminutes is used). Comet and asteroid orbits should be refreshed weekly."));
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
            var card = new Card(T("Observer")) { Dock = DockStyle.Fill, AutoScroll = true };
            var g = new FormGrid(220);
            var age = g.Row(T("Age (affects dark-adapted pupil)"), Ui.Num(AppState.Settings.ObserverAge, 8, 99));
            var exp = g.Row(T("Visual experience"), Ui.Combo(T("Beginner"), T("Average"), T("Experienced"), T("Expert")));
            exp.SelectedIndex = AppState.Settings.ObserverExperience;
            var aavso = g.Row(T("AAVSO observer code"), new TextBox { Text = AppState.Settings.AavsoObserverCode });
            var mpcc = g.Row(T("Default MPC station code"), new TextBox { Text = AppState.Settings.MpcStationCode });
            var nova = g.Row(T("nova.astrometry.net API key"), new TextBox { Text = AppState.Settings.NovaApiKey, UseSystemPasswordChar = true });
            var folder = g.Row(T("Image folder"), new TextBox { Text = AppState.Settings.ImageFolder });
            g.Full(Ui.Hint(T("Age and experience tune the visibility predictions (dark-adapted pupil ~7.8 mm at 20 years, decreasing ~0.45 mm per decade). The API key is only needed for online plate solving via astrometry.net; it is stored locally.")));
            var save = new FlatButton(T("Save"), Icons.Save) { Kind = ButtonKind.Primary }; save.AutoSizeToText();
            save.Click += (s, e) =>
            {
                AppState.Settings.ObserverAge = (int)age.Value; AppState.Settings.ObserverExperience = exp.SelectedIndex;
                AppState.Settings.AavsoObserverCode = aavso.Text.Trim(); AppState.Settings.MpcStationCode = mpcc.Text.Trim();
                AppState.Settings.NovaApiKey = nova.Text.Trim(); AppState.Settings.ImageFolder = folder.Text.Trim();
                AppState.Save(); MainForm.Instance.Toast(T("Saved"));
            };
            g.Full(save);
            card.Controls.Add(g);
            return card;
        }

        private static int EnumIndex<TEnum>(string value, TEnum fallback) where TEnum : struct
        {
            TEnum v;
            return Convert.ToInt32(Enum.TryParse(value ?? "", out v) ? v : fallback);
        }

        private Control BuildColour()
        {
            var card = new Card(T("Colour management")) { Dock = DockStyle.Fill, AutoScroll = true };
            var g = new FormGrid(240);
            var s = AppState.Settings;
            Action save = () => { AppState.Save(); AppState.ApplyColorSettings(); };
            Func<ComboBox> intents = () => Ui.Combo(T("Perceptual"), T("Relative colorimetric"), T("Saturation"), T("Absolute colorimetric"));

            var on = g.Row(T("Colour management"), new CheckBox { Checked = s.ColorManagement, Text = T("Show images through the monitor's colour profile") });
            on.CheckedChanged += (o, e) => { s.ColorManagement = on.Checked; save(); };
            g.Row(T("Working profile"), ColorUi.ProfileCombo(s.WorkingProfile, ProfileKind.Rgb, v => { s.WorkingProfile = v; save(); }));
            var monitor = g.Row(T("Monitor"), new Label { AutoSize = true, MaximumSize = new Size(560, 0), Margin = new Padding(0, 6, 0, 6) });
            Action showMonitor = () =>
            {
                var m = MonitorColor.For(card);
                monitor.Text = m.WindowsManaged ? T("Windows manages colour on this display (HDR or automatic colour management), so images are sent as sRGB")
                    : m.Path != null ? T("{0} (from Windows)", m.Profile.Description) : T("No profile set in Windows (taken to be sRGB)");
            };
            card.VisibleChanged += (o, e) => { if (card.Visible) { MonitorColor.Refresh(); showMonitor(); } };
            var intent = g.Row(T("Rendering intent"), intents());
            intent.SelectedIndex = EnumIndex(s.RenderingIntent, RenderingIntent.Perceptual);
            intent.SelectedIndexChanged += (o, e) => { s.RenderingIntent = ((RenderingIntent)intent.SelectedIndex).ToString(); save(); };

            var mismatch = g.Row(T("Image with another profile"), Ui.Combo(T("Keep its profile"), T("Convert to the working profile"), T("Discard the profile"), T("Ask")));
            mismatch.SelectedIndex = EnumIndex(s.OnProfileMismatch, ProfileMismatchPolicy.Keep);
            mismatch.SelectedIndexChanged += (o, e) => { s.OnProfileMismatch = ((ProfileMismatchPolicy)mismatch.SelectedIndex).ToString(); save(); };
            var missing = g.Row(T("Image without a profile"), Ui.Combo(T("Leave it untagged"), T("Assign the working profile"), T("Ask")));
            missing.SelectedIndex = EnumIndex(s.OnMissingProfile, ProfileMissingPolicy.LeaveUntagged);
            missing.SelectedIndexChanged += (o, e) => { s.OnMissingProfile = ((ProfileMissingPolicy)missing.SelectedIndex).ToString(); save(); };
            var embed = g.Row(T("Saving"), new CheckBox { Checked = s.EmbedProfiles, Text = T("Embed the image's profile in saved files") });
            embed.CheckedChanged += (o, e) => { s.EmbedProfiles = embed.Checked; save(); };
            var export8 = g.Row(T("8-bit PNG and JPEG"), Ui.Combo(T("Keep the image's profile"), T("Convert to sRGB")));
            export8.SelectedIndex = s.Export8BitAsSrgb ? 1 : 0;
            export8.SelectedIndexChanged += (o, e) => { s.Export8BitAsSrgb = export8.SelectedIndex == 1; save(); };

            g.Row(T("Proofing profile"), ColorUi.ProfileCombo(s.ProofingProfile, ProfileKind.Proofing, v => { s.ProofingProfile = v; save(); }));
            var proofIntent = g.Row(T("Proofing intent"), intents());
            proofIntent.SelectedIndex = EnumIndex(s.ProofingIntent, RenderingIntent.RelativeColorimetric);
            proofIntent.SelectedIndexChanged += (o, e) => { s.ProofingIntent = ((RenderingIntent)proofIntent.SelectedIndex).ToString(); save(); };
            var warnRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            var swatch = new Panel { Width = 40, Height = 22, Margin = new Padding(0, 6, 8, 0), Tag = "keepcolor" };
            Func<Color> warning = () => { try { return ColorTranslator.FromHtml(s.GamutWarningColor); } catch { return Color.Gray; } };
            swatch.Paint += (o, e) =>
            {
                var c = warning();
                // in night vision mode only its brightness, in red, as images are shown
                if (Theme.Current.IsNight) { int l = (c.R * 77 + c.G * 150 + c.B * 29) >> 8; c = Color.FromArgb(l, l * 6 / 100, l * 4 / 100); }
                using (var b = new SolidBrush(c)) e.Graphics.FillRectangle(b, swatch.ClientRectangle);
                using (var pen = new Pen(Theme.Current.Border)) e.Graphics.DrawRectangle(pen, 0, 0, swatch.Width - 1, swatch.Height - 1);
            };
            Action showSwatch = () => swatch.Invalidate();
            var pick = new FlatButton(T("Choose…"), null, (o, e) =>
            {
                using (var d = new ColorDialog { Color = warning(), FullOpen = true })
                    if (d.ShowDialog(FindForm()) == DialogResult.OK) { s.GamutWarningColor = ColorTranslator.ToHtml(Color.FromArgb(d.Color.R, d.Color.G, d.Color.B)); showSwatch(); save(); }
            });
            pick.AutoSizeToText();
            warnRow.Controls.Add(swatch); warnRow.Controls.Add(pick);
            g.Row(T("Gamut warning colour"), warnRow);
            g.Full(Ui.Hint(T("Images are shown through the monitor's profile, which Windows provides. Files without a profile (FITS, raw frames, stacks) are taken to be in the working profile. 8-bit files in a wide profile such as ProPhoto RGB look dull in programs without colour management and can show banding; convert them to sRGB for the web. Night vision mode shows only brightness, so colour management does not apply there.")));
            card.Controls.Add(g);
            return card;
        }

        private Control BuildAppearance()
        {
            var card = new Card(T("Appearance")) { Dock = DockStyle.Fill };
            var g = new FormGrid(220);
            // the language takes effect at the next start, since every view sets its text when it is built
            var codes = new List<string> { "" };
            var lang = g.Row(T("Language"), new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList });
            lang.Items.Add(T("Same as Windows"));
            foreach (var l in Lang.Available()) { codes.Add(l.Key); lang.Items.Add(l.Value); }
            codes.Add(PseudoCode); lang.Items.Add(T("Pseudo-language (for testing layouts)"));
            string started = AppState.Settings.Language ?? "";
            lang.SelectedIndex = Math.Max(0, codes.IndexOf(started));
            var restart = new FlatButton(T("Restart now to change the language"), Icons.Refresh, (s, e) => Program.Restart());
            restart.AutoSizeToText();
            var restartRow = new FlowLayoutPanel { Height = 40, WrapContents = false, Visible = false };
            restartRow.Controls.Add(restart);
            lang.SelectedIndexChanged += (s, e) =>
            {
                AppState.Settings.Language = codes[lang.SelectedIndex];
                AppState.Save();
                restartRow.Visible = AppState.Settings.Language != started;
            };
            g.Full(restartRow);
            var night = g.Row(T("Night vision (red) mode"), new CheckBox { Checked = Theme.Current.IsNight, Text = T("Use deep red colours to protect dark adaptation") });
            bool syncing = false;
            night.CheckedChanged += (s, e) => { if (!syncing) MainForm.Instance.SetNightMode(night.Checked); };
            EventHandler sync = (s, e) => { syncing = true; night.Checked = Theme.Current.IsNight; syncing = false; };
            Theme.Changed += sync;
            night.Disposed += (s, e) => Theme.Changed -= sync;
            var refr = g.Row(T("Atmospheric refraction"), new CheckBox { Checked = AppState.Settings.UseRefraction, Text = T("Apply refraction to horizontal coordinates") });
            refr.CheckedChanged += (s, e) => { AppState.Settings.UseRefraction = refr.Checked; AppState.Save(); };
            g.Full(Ui.Hint(T("Keyboard: Ctrl+K command palette · Ctrl+1…9 switch workspace · Ctrl+Shift+N night mode · in charts: arrows pan, +/− zoom, N/E/S/W/Z look direction, double-click centre.")));
            card.Controls.Add(g);
            return card;
        }

        private Control BuildAbout()
        {
            var card = new Card("Sidralune") { Dock = DockStyle.Fill };
            var t = new Label { Dock = DockStyle.Fill, UseMnemonic = false, Text =
                T("Sidralune {0} — an integrated astronomy suite for planning, observing, imaging and science.", "0.6.0") + "\r\n\r\n" +
                "Copyright © Jimmy Nordström\r\n\r\n" +
                T("Algorithms: IAU 2006 precession and nutation, JPL DE440 (SPK) or Standish/ELP2000 analytic ephemerides, SGP4 (Vallado 2006),\n" +
                "Krisciunas & Schaefer sky brightness, Schaefer visual thresholds, triangle-invariant star matching with RANSAC, TAN-SIP WCS,\n" +
                "stacking with kappa-sigma, winsorized, linear-fit and generalized-ESD rejection, drizzle (Fruchter & Hook),\n" +
                "Richardson-Lucy with total variation (Dey et al.), starlet wavelets (Starck & Murtagh), generalized hyperbolic stretch,\n" +
                "multi-point lucky imaging, spherical planetary derotation, generalised Lomb-Scargle, BLS, PDM, Kwee-van Woerden, Mandel-Agol-type transit model with ensemble MCMC.").Replace("\n", "\r\n") + "\r\n\r\n" +
                T("Data © their providers: JPL/NASA, Minor Planet Center, HYG Database (D. Nash), OpenNGC (M. Verga), Stellarium, CDS (SIMBAD/VizieR), AAVSO, CelesTrak.") + "\r\n\r\n" +
                T("Settings and data: {0}", AppPaths.Root) };
            card.Controls.Add(t);
            return card;
        }
    }
}
