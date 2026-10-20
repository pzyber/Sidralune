using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Printing;
using System.Linq;
using System.Windows.Forms;
using Sidralune.Core;
using Sidralune.Core.Catalogs;
using Sidralune.Core.Coordinates;
using Sidralune.Core.Ephemeris;
using Sidralune.Core.Mathematics;
using Sidralune.Core.MinorBodies;
using Sidralune.Core.Observing;
using Sidralune.Core.Time;
using Sidralune.Devices;
using Sidralune.UI;
using AstroEvents = Sidralune.Core.Ephemeris.Events;

namespace Sidralune.Views
{
    public class SkyView : UserControl, ICommandSource, IActivatable
    {
        public readonly SkyChart Chart = new SkyChart { Dock = DockStyle.Fill };
        private readonly Panel _side = new Panel { Dock = DockStyle.Right, Width = 340, Padding = new Padding(8) };
        private readonly SegmentedTabs _mode = new SegmentedTabs { Width = 220 };
        private readonly TextBox _find = new TextBox { Width = 200 };
        private readonly ComboBox _fov = Ui.Combo("Naked eye (100°)", "Wide (60°)", "Binoculars (7°)", "Finder (5°)", "Low power (2°)", "Eyepiece", "Camera", "Custom");
        private readonly Timer _anim = new Timer { Interval = 100 };
        private readonly Label _objTitle = new Label { Font = Theme.TitleFont, AutoSize = false, Height = 30, Dock = DockStyle.Top };
        private readonly Label _objSub = new Label { AutoSize = false, Height = 20, Dock = DockStyle.Top, Tag = "dim" };
        private readonly TextBox _objInfo = new TextBox { Multiline = true, ReadOnly = true, Dock = DockStyle.Top, Height = 260, BorderStyle = BorderStyle.None, Font = Theme.UiFont, ScrollBars = ScrollBars.Vertical };
        private readonly PlotControl _alt = new PlotControl { Dock = DockStyle.Top, Height = 170, XFormat = AxisFormat.Time, ShowLegend = false };
        private readonly FlowLayoutPanel _actions = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 76, Padding = new Padding(0, 6, 0, 0) };
        private SelectedObject _sel;
        private readonly Timer _live = new Timer { Interval = 2000 };

        public SkyView()
        {
            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 44, Padding = new Padding(6, 6, 6, 0), WrapContents = false };
            _mode.Tabs.AddRange(new[] { "Planetarium", "Atlas" });
            _mode.SelectedChanged += i => { SetMode(i == 0 ? ChartMode.Planetarium : ChartMode.Atlas); };
            bar.Controls.Add(_mode);
            bar.Controls.Add(Ui.Separator());
            _find.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { FindObject(_find.Text); e.SuppressKeyPress = true; } };
            var cue = new Label { Text = Icons.Search, Font = Theme.IconFont, AutoSize = true, Margin = new Padding(0, 7, 2, 0) };
            bar.Controls.Add(cue); bar.Controls.Add(_find);
            bar.Controls.Add(Ui.Separator());
            bar.Controls.Add(Btn(null, Icons.Left + Icons.Left, "Back 1 day", () => AppState.Step(-1)));
            bar.Controls.Add(Btn(null, Icons.Left, "Back 1 hour", () => AppState.Step(-1 / 24.0)));
            bar.Controls.Add(Btn("Now", Icons.Clock, "Return to live time", AppState.GoLive));
            bar.Controls.Add(Btn(null, Icons.Right, "Forward 1 hour", () => AppState.Step(1 / 24.0)));
            bar.Controls.Add(Btn(null, Icons.Right + Icons.Right, "Forward 1 day", () => AppState.Step(1)));
            var play = new FlatButton { Icon = Icons.Play, Toggle = true, Width = 34, ToolTipText = "Animate time (×300)" };
            play.CheckedChanged += (s, e) => { if (play.Checked) AppState.Play(300); else AppState.SetTime(AppState.Now); };
            bar.Controls.Add(play);
            var dt = new TimeField();
            dt.ValueCommitted += v => AppState.SetTime(AstroTime.FromDateTime(AppState.Site.ToUtc(v)));
            bar.Controls.Add(dt);
            bar.Controls.Add(Ui.Separator());
            _fov.Width = 150;
            _fov.SelectedIndexChanged += (s, e) => ApplyFovPreset();
            bar.Controls.Add(_fov);
            bar.Controls.Add(LayersButton());
            var mirror = new FlatButton { Text = "Mirror", Toggle = true, ToolTipText = "Mirror image (star diagonal / refractor view)" };
            mirror.AutoSizeToText();
            mirror.CheckedChanged += (s, e) => { Chart.FlipX = mirror.Checked; Chart.Invalidate(); };
            bar.Controls.Add(mirror);
            var flip = new FlatButton { Text = "Invert", Toggle = true, ToolTipText = "Rotate 180° (Newtonian view)" };
            flip.AutoSizeToText();
            flip.CheckedChanged += (s, e) => { Chart.FlipX = flip.Checked ^ mirror.Checked; Chart.FlipY = flip.Checked; Chart.Invalidate(); };
            bar.Controls.Add(flip);
            bar.Controls.Add(Btn(null, Icons.Print, "Print chart for the telescope…", PrintChart));
            bar.Controls.Add(Btn(null, Icons.Save, "Export chart as PNG…", ExportPng));

            BuildSide();
            Controls.Add(Chart);
            Controls.Add(_side);
            Controls.Add(bar);

            Chart.ObjectClicked += o => { AppState.Select(o); };
            Chart.ContextRequested += ShowContext;
            AppState.ObjectSelected += o => { if (o != null) ShowInfo(o); };
            AppState.TimeChanged += (s, e) => { Chart.Invalidate(); dt.Value = AppState.Site.ToLocal(AppState.Now.ToDateTimeUtc()); if (_sel != null) ShowInfo(_sel); };
            AppState.SiteChanged += (s, e) => Chart.Invalidate();
            _live.Tick += (s, e) => { if (Visible) { Chart.Invalidate(); if (_sel != null) UpdateLiveInfo(); } };
            _live.Start();
            Theme.Changed += (s, e) => Chart.Invalidate();
            CatalogManager.Instance.CatalogsChanged += (s, e) => { if (IsHandleCreated) BeginInvoke(new Action(Chart.Invalidate)); };
            dt.Value = AppState.Site.ToLocal(AppState.Now.ToDateTimeUtc());
        }

        public void Activated() => Chart.Focus();

        private FlatButton Btn(string text, string icon, string tip, Action a)
        {
            var b = new FlatButton(text, icon, (s, e) => a()) { ToolTipText = tip };
            if (text == null) b.Width = 34;
            return b;
        }

        private FlatButton LayersButton()
        {
            var b = new FlatButton("Layers", Icons.Layers);
            var menu = new ContextMenuStrip { Renderer = new DarkToolStripRenderer() };
            var L = Chart.Layers;
            // ticks are refreshed every time the menu opens, since other controls (FOV presets, scripts) change layers too
            var refresh = new List<Action>();
            Action<string, Func<bool>, Action<bool>> add = (name, get, set) =>
            {
                var it = new ToolStripMenuItem(name) { CheckOnClick = true, Checked = get() };
                it.Click += (s, e) => { set(it.Checked); Chart.Invalidate(); };
                refresh.Add(() => it.Checked = get());
                menu.Items.Add(it);
            };
            add("Stars", () => L.Stars, v => L.Stars = v);
            add("Star names", () => L.StarNames, v => L.StarNames = v);
            add("Bayer / Flamsteed", () => L.Bayer, v => L.Bayer = v);
            add("Deep-sky objects", () => L.DeepSky, v => L.DeepSky = v);
            add("Deep-sky labels", () => L.DeepSkyLabels, v => L.DeepSkyLabels = v);
            add("Planets, Sun and Moon", () => L.Planets, v => L.Planets = v);
            add("Comets and asteroids", () => L.MinorBodies, v => L.MinorBodies = v);
            menu.Items.Add(new ToolStripSeparator());
            add("Constellation lines", () => L.ConstellationLines, v => L.ConstellationLines = v);
            add("Constellation names", () => L.ConstellationNames, v => L.ConstellationNames = v);
            add("Constellation boundaries", () => L.Boundaries, v => L.Boundaries = v);
            add("Milky Way", () => L.MilkyWay, v => L.MilkyWay = v);
            menu.Items.Add(new ToolStripSeparator());
            add("Equatorial grid", () => L.EquatorialGrid, v => L.EquatorialGrid = v);
            add("Alt-azimuth grid", () => L.AltAzGrid, v => L.AltAzGrid = v);
            add("Ecliptic", () => L.Ecliptic, v => L.Ecliptic = v);
            add("Galactic equator", () => L.GalacticEquator, v => L.GalacticEquator = v);
            add("Meridian", () => L.Meridian, v => L.Meridian = v);
            menu.Items.Add(new ToolStripSeparator());
            add("Horizon and ground", () => L.Horizon, v => L.Horizon = v);
            add("Atmosphere (daylight sky)", () => L.Atmosphere, v => L.Atmosphere = v);
            add("Cardinal points", () => L.Cardinals, v => L.Cardinals = v);
            add("Telescope position", () => L.Telescope, v => L.Telescope = v);
            add("Field-of-view indicators", () => L.FovIndicators, v => L.FovIndicators = v);
            add("Telrad rings", () => L.Telrad, v => L.Telrad = v);
            add("Refraction", () => AppState.Settings.UseRefraction, v => AppState.Settings.UseRefraction = v);
            menu.Items.Add(new ToolStripSeparator());
            var mag = new ToolStripMenuItem("Limiting magnitude");
            foreach (var m in new[] { 4.0, 5.0, 6.0, 6.5, 7.5, 8.5, 10.0, 12.0 })
            {
                var mm = m;
                var mi = new ToolStripMenuItem(m.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
                mi.Click += (s, e) => { L.MagLimit = mm; Chart.Invalidate(); };
                refresh.Add(() => mi.Checked = Math.Abs(L.MagLimit - mm) < 0.01);
                mag.DropDownItems.Add(mi);
            }
            mag.DropDownItems.Add(new ToolStripSeparator());
            var auto = new ToolStripMenuItem("Deeper when zoomed in") { CheckOnClick = true, ToolTipText = "Show fainter stars as the field of view gets smaller" };
            auto.Click += (s, e) => { L.AutoMagLimit = auto.Checked; Chart.Invalidate(); };
            refresh.Add(() => auto.Checked = L.AutoMagLimit);
            mag.DropDownItems.Add(auto);
            menu.Items.Add(mag);
            menu.Opening += (s, e) => { foreach (var r in refresh) r(); };
            b.Click += (s, e) => menu.Show(b, new Point(0, b.Height));
            return b;
        }

        private void BuildSide()
        {
            _side.Controls.Add(_actions);
            _side.Controls.Add(_alt);
            _side.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 6 });
            _side.Controls.Add(_objInfo);
            _side.Controls.Add(_objSub);
            _side.Controls.Add(_objTitle);
            _objTitle.Text = "No object selected";
            _objSub.Text = "Click an object in the chart, or search (Ctrl+K).";
            _actions.Controls.Add(new FlatButton("Center", Icons.Target, (s, e) => { if (_sel != null) Goto(_sel); }));
            _actions.Controls.Add(new FlatButton("Slew", Icons.Observatory, (s, e) => SlewTo(_sel)) { ToolTipText = "Slew the connected telescope" });
            _actions.Controls.Add(new FlatButton("Add to list", Icons.Add, (s, e) => { if (_sel != null) { MainForm.Instance.GetView<PlannerView>("planner")?.AddObject(_sel); MainForm.Instance.Toast(_sel.Name + " added to the observing list"); } }));
            _actions.Controls.Add(new FlatButton("Frame", Icons.Camera, (s, e) => { if (_sel != null) { MainForm.Instance.Navigate("astrometry"); MainForm.Instance.GetView<AstrometryView>("astrometry")?.FrameTarget(_sel); } }) { ToolTipText = "Plan framing / mosaic" });
            _actions.Controls.Add(new FlatButton("Copy", Icons.List, (s, e) => { if (_sel != null) Clipboard.SetText(_sel.Name + " " + AstroMath.FormatHms(_sel.RaDeg / 15, 1) + " " + AstroMath.FormatDms(_sel.DecDeg, 0)); }) { ToolTipText = "Copy name and coordinates" });
            foreach (Control c in _actions.Controls) ((FlatButton)c).AutoSizeToText();
            _alt.Title = "Altitude tonight";
        }

        private void SetMode(ChartMode m)
        {
            // keep the same sky position centred when switching
            double ra, dec;
            Chart.ScreenToIcrs(new Point(Chart.Width / 2, Chart.Height / 2), out ra, out dec);
            Chart.Mode = m;
            if (m == ChartMode.Atlas && Chart.FovDeg > 90) Chart.FovDeg = 60;
            Chart.CenterOn(ra, dec);
        }

        private void ApplyFovPreset()
        {
            Chart.Fovs.Clear();
            var ins = AppState.Instrument;
            switch (_fov.SelectedIndex)
            {
                case 0: Chart.FovDeg = 100; break;
                case 1: Chart.FovDeg = 60; break;
                case 2: Chart.FovDeg = 12; Chart.Fovs.Add(new ChartFov { Name = "10×50 binoculars", WidthDeg = 6.5 }); break;
                case 3: Chart.FovDeg = 9; Chart.Fovs.Add(new ChartFov { Name = "Finder 8×50", WidthDeg = 5 }); Chart.Layers.Telrad = true; break;
                case 4: Chart.FovDeg = 4; break;
                case 5:
                    Chart.FovDeg = Math.Max(0.2, ins.TrueFieldDeg * 2.2);
                    Chart.Fovs.Add(new ChartFov { Name = string.Format("{0} ×{1:0}", AppState.Settings.Eyepieces[Math.Min(AppState.Settings.ActiveEyepiece, AppState.Settings.Eyepieces.Count - 1)].Name, ins.Magnification), WidthDeg = ins.TrueFieldDeg });
                    break;
                case 6:
                    {
                        var rig = AppState.ImagingRig;
                        Chart.FovDeg = Math.Max(0.1, Math.Max(rig.FieldWidthDeg, rig.FieldHeightDeg) * 2);
                        Chart.Fovs.Add(new ChartFov { Name = rig.Name, WidthDeg = rig.FieldWidthDeg, HeightDeg = rig.FieldHeightDeg });
                        break;
                    }
            }
            Chart.Invalidate();
        }

        public static SelectedObject PlanetObject(Body b)
        {
            var ctx = new SkyContext(AppState.Site, AppState.Now);
            var p = ctx.BodyPlace(b);
            if (b != Body.Moon && b != Body.Sun) PlanetPhysics.Fill(b, p, ctx.JdTT);
            return new SelectedObject { Kind = SelectedKind.Planet, Name = b.ToString(), Planet = b, RaDeg = p.Astrometric.RaHours * 15, DecDeg = p.Astrometric.DecDeg, Mag = p.Magnitude, SizeArcmin = p.DiameterArcsec / 60 };
        }

        public static SelectedObject MinorObject(MinorBody m)
        {
            var ctx = new SkyContext(AppState.Site, AppState.Now);
            var p = MinorBodyStore.Place(ctx, m);
            return new SelectedObject { Kind = SelectedKind.MinorBody, Name = m.DisplayName, Minor = m, RaDeg = p.Astrometric.RaHours * 15, DecDeg = p.Astrometric.DecDeg, Mag = p.Magnitude };
        }

        private void FindObject(string q)
        {
            if (string.IsNullOrWhiteSpace(q)) return;
            Body b;
            if (Enum.TryParse(q.Trim(), true, out b) && b != Body.Earth) { var o = PlanetObject(b); AppState.Select(o); Goto(o); return; }
            var hit = CatalogManager.Instance.Find(q) ?? CatalogManager.Instance.Search(q, 1).FirstOrDefault();
            if (hit != null) { var o = SelectedObject.From(hit); AppState.Select(o); Goto(o); return; }
            var mb = MinorBodyStore.Instance.Find(q);
            if (mb != null) { var o = MinorObject(mb); AppState.Select(o); Goto(o); return; }
            // coordinates "hh mm ss +dd mm ss"
            var parts = q.Split(new[] { ',', ';' });
            MainForm.Instance.Toast("Not found: " + q + " (try the Databases view for SIMBAD lookups)", Icons.Warning);
        }

        public void Goto(SelectedObject o)
        {
            if (o == null) return;
            Chart.Highlight = o;
            double fov = Chart.FovDeg;
            if (o.Kind == SelectedKind.DeepSky && !double.IsNaN(o.SizeArcmin)) fov = Math.Max(0.3, Math.Min(Chart.FovDeg, o.SizeArcmin / 60 * 8));
            Chart.CenterOn(o.RaDeg * AstroMath.Deg2Rad, o.DecDeg * AstroMath.Deg2Rad, fov);
            ShowInfo(o);
        }

        private void ShowInfo(SelectedObject o)
        {
            _sel = o;
            Chart.Highlight = o;
            _objTitle.Text = o.Name;
            string type = o.Kind == SelectedKind.DeepSky ? DeepSkyObject.TypeLabel(o.Catalog.Type) : o.Kind.ToString();
            var con = CatalogManager.Instance.ConstellationAt(o.RaDeg * AstroMath.Deg2Rad, o.DecDeg * AstroMath.Deg2Rad);
            _objSub.Text = type + (con != null ? " in " + con.Name : "");
            UpdateLiveInfo();
            // altitude curve for the coming night
            _alt.Clear();
            var site = AppState.Site;
            var start = AppState.Now;
            var local = site.ToLocal(start.ToDateTimeUtc());
            var noon = site.ToUtc(local.Date.AddHours(local.Hour < 12 ? -12 : 12));
            var series = _alt.AddSeries(o.Name);
            for (int i = 0; i <= 96; i++)
            {
                var t = AstroTime.FromDateTime(noon).AddMinutes(i * 15);
                double alt = AltitudeAt(o, t);
                series.Add(site.ToLocal(t.ToDateTimeUtc()).ToOADate(), alt);
            }
            var tw = AstroEvents.Twilight(site, AstroTime.FromDateTime(noon));
            Action<AstroTime?, AstroTime?, int> band = (a, b2, alpha) =>
            {
                if (a.HasValue && b2.HasValue) _alt.Bands.Add(new PlotBand { X0 = site.ToLocal(a.Value.ToDateTimeUtc()).ToOADate(), X1 = site.ToLocal(b2.Value.ToDateTimeUtc()).ToOADate(), ThemeColor = pal => Theme.WithAlpha(pal.Accent, alpha) });
            };
            band(tw.SunSet, tw.SunRise, 18);
            band(tw.NauticalDusk, tw.NauticalDawn, 18);
            band(tw.AstronomicalDusk, tw.AstronomicalDawn, 22);
            _alt.FixedYMin = 0; _alt.FixedYMax = 90;
            _alt.Markers.Add(new PlotMarker { X = site.ToLocal(AppState.Now.ToDateTimeUtc()).ToOADate(), Label = "now" });
            _alt.Invalidate();
        }

        private double AltitudeAt(SelectedObject o, AstroTime t)
        {
            if (o.Kind == SelectedKind.Planet && o.Planet.HasValue)
                return new SkyContext(AppState.Site, t) { ApplyRefraction = false }.BodyPlace(o.Planet.Value).Horizontal.AltDeg;
            if (o.Kind == SelectedKind.MinorBody && o.Minor != null)
                return MinorBodyStore.Place(new SkyContext(AppState.Site, t) { ApplyRefraction = false }, o.Minor).Horizontal.AltDeg;
            return AstroEvents.FixedAltitude(o.RaDeg * AstroMath.Deg2Rad, o.DecDeg * AstroMath.Deg2Rad, AppState.Site, t);
        }

        private void UpdateLiveInfo()
        {
            var o = _sel;
            if (o == null) return;
            var site = AppState.Site;
            var ctx = new SkyContext(site, AppState.Now);
            var lines = new List<string>();
            double ra = o.RaDeg, dec = o.DecDeg, mag = o.Mag, size = o.SizeArcmin;
            Horizontal hz;
            Equatorial app;
            if (o.Kind == SelectedKind.Planet && o.Planet.HasValue)
            {
                var p = ctx.BodyPlace(o.Planet.Value);
                if (o.Planet.Value != Body.Sun && o.Planet.Value != Body.Moon) PlanetPhysics.Fill(o.Planet.Value, p, ctx.JdTT);
                ra = p.Astrometric.RaHours * 15; dec = p.Astrometric.DecDeg; hz = p.Horizontal; app = p.Apparent;
                if (o.Planet.Value != Body.Moon && o.Planet.Value != Body.Sun) { mag = p.Magnitude; size = p.DiameterArcsec / 60; }
                lines.Add(string.Format("Distance        {0:0.0000} AU ({1:N0} km)", p.Distance, p.Distance * AstroMath.AU_km));
                if (o.Planet.Value != Body.Sun)
                {
                    lines.Add(string.Format("Illuminated     {0:0.0}%   phase angle {1:0.0}°", p.IlluminatedFraction * 100, p.PhaseAngle * AstroMath.Rad2Deg));
                    lines.Add(string.Format("Elongation      {0:0.0}°", p.Elongation * AstroMath.Rad2Deg));
                }
                if (o.Planet.Value == Body.Moon) lines.Add(string.Format("Age             {0:0.0} days", AstroEvents.MoonAgeDays(ctx.JdTT)));
                if (!double.IsNaN(p.DiameterArcsec)) lines.Add(string.Format("Apparent size   {0:0.0}\"", p.DiameterArcsec));
                if (o.Planet.Value == Body.Jupiter)
                {
                    double s1, s2, s3;
                    PlanetPhysics.JupiterCentralMeridians(ctx.JdTDB, p.Astrometric.ToVector(), p.LightTimeDays, out s1, out s2, out s3);
                    lines.Add(string.Format("CM  I {0:0.0}°  II {1:0.0}°  III {2:0.0}°", s1, s2, s3));
                }
                if (o.Planet.Value == Body.Mars || o.Planet.Value == Body.Saturn)
                {
                    double cm, lat, pa;
                    PlanetPhysics.Orientation(o.Planet.Value, ctx.JdTDB, p.Astrometric.ToVector(), p.LightTimeDays, out cm, out lat, out pa);
                    lines.Add(string.Format("CM {0:0.0}°  sub-Earth lat {1:+0.0;-0.0}°  pole PA {2:0.0}°", cm, lat, pa));
                }
            }
            else if (o.Kind == SelectedKind.MinorBody && o.Minor != null)
            {
                var p = MinorBodyStore.Place(ctx, o.Minor);
                ra = p.Astrometric.RaHours * 15; dec = p.Astrometric.DecDeg; hz = p.Horizontal; app = p.Apparent; mag = p.Magnitude;
                lines.Add(string.Format("Distance        Δ {0:0.000} AU   r {1:0.000} AU", p.Distance, p.HelioDistance));
                lines.Add(string.Format("Elongation      {0:0.0}°   phase {1:0.0}°", p.Elongation * AstroMath.Rad2Deg, p.PhaseAngle * AstroMath.Rad2Deg));
                lines.Add("Orbit           " + o.Minor.OrbitType);
            }
            else
            {
                hz = ctx.IcrsToHorizontal(ra * AstroMath.Deg2Rad, dec * AstroMath.Deg2Rad);
                app = ctx.IcrsToApparent(ra * AstroMath.Deg2Rad, dec * AstroMath.Deg2Rad);
                var st = o.Catalog as Star;
                if (st != null)
                {
                    if (!float.IsNaN(st.BV)) lines.Add(string.Format("B−V             {0:0.00}   (~{1:N0} K)", st.BV, st.TemperatureK));
                    if (!string.IsNullOrEmpty(st.SpectralType)) lines.Add("Spectral type   " + st.SpectralType);
                    if (!float.IsNaN(st.Parallax) && st.Parallax > 0) lines.Add(string.Format("Distance        {0:0.0} ly", 3261.56 / st.Parallax));
                    if (st.Hip > 0) lines.Add("HIP " + st.Hip + (st.Hd > 0 ? "   HD " + st.Hd : "") + (st.Bayer != null ? "   " + st.Bayer : ""));
                    if (st.VariableType != null) lines.Add("Variable        " + st.VariableType + (float.IsNaN(st.MagMin) ? "" : string.Format(" (to {0:0.0})", st.MagMin)));
                }
                var d = o.Catalog as DeepSkyObject;
                if (d != null)
                {
                    if (d.Morphology != null) lines.Add("Morphology      " + d.Morphology);
                    if (!double.IsNaN(d.EffectiveSurfaceBrightness)) lines.Add(string.Format("Surface bright. {0:0.0} mag/arcsec²", d.EffectiveSurfaceBrightness));
                    if (d.AltNames.Count > 0) lines.Add("Also            " + string.Join(", ", d.AltNames.Take(4)));
                }
            }
            var head = new List<string>
            {
                string.Format("Magnitude       {0}", double.IsNaN(mag) ? "—" : mag.ToString("0.0")),
                string.Format("Size            {0}", double.IsNaN(size) ? "—" : AstroMath.FormatAngleAuto(size / 60)),
                "RA/Dec J2000    " + AstroMath.FormatHms(ra / 15, 1) + "  " + AstroMath.FormatDms(dec, 0),
                "RA/Dec apparent " + AstroMath.FormatHms(app.RaHours, 1) + "  " + AstroMath.FormatDms(app.DecDeg, 0),
                string.Format("Alt/Az          {0:+0.0;-0.0}°  /  {1:0.0}°   airmass {2}", hz.AltDeg, hz.AzDeg, hz.Alt > 0 ? Transforms.Airmass(hz.Alt).ToString("0.00") : "—"),
                "Hour angle      " + AstroMath.FormatHms(AstroMath.NormalizeHours(ctx.LastHours - app.RaHours), 0, true)
            };
            // rise / transit / set
            try
            {
                RiseSetResult rs = o.Kind == SelectedKind.Planet && o.Planet.HasValue ? AstroEvents.BodyRiseSet(o.Planet.Value, site, AppState.Now.AddHours(-12))
                    : AstroEvents.FixedRiseSet(ra * AstroMath.Deg2Rad, dec * AstroMath.Deg2Rad, site, AppState.Now.AddHours(-12));
                Func<AstroTime?, string> fmt = t => t.HasValue ? site.ToLocal(t.Value.ToDateTimeUtc()).ToString("HH:mm") : "—";
                head.Add(rs.AlwaysUp ? "Circumpolar (never sets)" : rs.NeverUp ? "Never rises today" : string.Format("Rise {0}  Transit {1} ({2:0}°)  Set {3}", fmt(rs.Rise), fmt(rs.Transit), rs.TransitAltitudeDeg, fmt(rs.Set)));
            }
            catch { }
            // visibility with the current instrument (SkyTools-style)
            if (!double.IsNaN(mag) && o.Kind != SelectedKind.Planet)
            {
                double alt;
                double sky = VisibilityModel.SkyBrightnessAt(ctx, ra * AstroMath.Deg2Rad, dec * AstroMath.Deg2Rad, out alt);
                var vis = VisibilityModel.Predict(mag, double.IsNaN(size) ? 0 : size, alt, sky, site, AppState.Instrument, AppState.Eye);
                head.Add(string.Format("Visibility      {0}  (margin {1:+0.0;-0.0} mag)", vis.Summary, vis.BestMargin));
                head.Add(string.Format("Best view       ×{0:0} with {1}", vis.BestMagnification, AppState.Instrument.Name));
                head.Add(string.Format("Sky brightness  {0:0.00} mag/arcsec²", sky));
            }
            _objInfo.Text = string.Join("\r\n", head.Concat(new[] { "" }).Concat(lines));
        }

        private void SlewTo(SelectedObject o)
        {
            if (o == null) return;
            var tel = DeviceManager.Instance.Telescope;
            if (tel == null) { MainForm.Instance.Toast("No telescope connected (Observatory → Equipment)", Icons.Warning); return; }
            var app = new SkyContext(AppState.Site, Core.Time.AstroTime.Now).IcrsToApparent(o.RaDeg * AstroMath.Deg2Rad, o.DecDeg * AstroMath.Deg2Rad);
            Tasks.Run("Slewing to " + o.Name, t => tel.SlewToCoordinatesAsync(app.RaHours, app.DecDeg), () => MainForm.Instance.Toast("Slewing to " + o.Name));
        }

        private void ShowContext(double ra, double dec, MouseEventArgs e)
        {
            var m = new ContextMenuStrip { Renderer = new DarkToolStripRenderer() };
            m.Items.Add(string.Format("{0}  {1}", AstroMath.FormatHms(ra / 15, 0), AstroMath.FormatDms(dec, 0))).Enabled = false;
            var hit = Chart.HitTest(e.Location);
            if (hit != null)
            {
                m.Items.Add("Select " + hit.Name, null, (s, a) => AppState.Select(hit));
                m.Items.Add("Add " + hit.Name + " to observing list", null, (s, a) => MainForm.Instance.GetView<PlannerView>("planner")?.AddObject(hit));
            }
            m.Items.Add("Center here", null, (s, a) => Chart.CenterOn(ra * AstroMath.Deg2Rad, dec * AstroMath.Deg2Rad));
            m.Items.Add("Slew telescope here", null, (s, a) => SlewTo(new SelectedObject { Name = "position", RaDeg = ra, DecDeg = dec }));
            m.Items.Add("Frame with camera here", null, (s, a) => { MainForm.Instance.Navigate("astrometry"); MainForm.Instance.GetView<AstrometryView>("astrometry")?.FrameTarget(new SelectedObject { Name = "Field", RaDeg = ra, DecDeg = dec }); });
            m.Show(Chart, e.Location);
        }

        // ------------------------------------------------------------------ printing / export

        private void PrintChart()
        {
            var doc = new PrintDocument { DocumentName = "Sidralune sky chart" };
            doc.DefaultPageSettings.Landscape = true;
            doc.PrintPage += (s, e) =>
            {
                var r = e.MarginBounds;
                var g = e.Graphics;
                int titleH = 50, legendH = 70;
                var chartRect = new Rectangle(r.X, r.Y + titleH, r.Width, r.Height - titleH - legendH);
                // render the chart into a bitmap at higher resolution for crisp print
                int pw = chartRect.Width * 3, ph = chartRect.Height * 3;
                using (var bmp = new Bitmap(pw, ph))
                using (var bg = Graphics.FromImage(bmp))
                {
                    var oldW = Chart.Width;
                    var mode = Chart.Mode;
                    // render as atlas with print palette at scaled size
                    var clone = new SkyChart { Mode = Chart.Mode, Layers = Chart.Layers, CenterLon = Chart.CenterLon, CenterLat = Chart.CenterLat, FovDeg = Chart.FovDeg, FlipX = Chart.FlipX, FlipY = Chart.FlipY, Highlight = Chart.Highlight, Width = pw, Height = ph };
                    clone.Fovs.AddRange(Chart.Fovs);
                    var oldFont = Theme.SmallFont;
                    clone.Render(bg, pw, ph, Palette.Print(), true);
                    g.DrawImage(bmp, chartRect);
                }
                g.DrawRectangle(Pens.Black, chartRect);
                var local = AppState.Site.ToLocal(AppState.Now.ToDateTimeUtc());
                using (var tf = new Font("Segoe UI Semibold", 14f)) g.DrawString((_sel != null ? _sel.Name + " — " : "") + "Sidralune finder chart", tf, Brushes.Black, r.X, r.Y);
                using (var sf = new Font("Segoe UI", 9f))
                {
                    g.DrawString(string.Format("{0:yyyy-MM-dd HH:mm} local, {1}   ·   field {2}   ·   limiting magnitude {3:0.0}   ·   {4}", local, AppState.Site, AstroMath.FormatAngleAuto(Chart.FovDeg), Chart.EffectiveMagLimit, Chart.FlipX ? "mirror view" : "normal view"), sf, Brushes.Black, r.X, r.Y + 26);
                    int ly = chartRect.Bottom + 10, lx = r.X;
                    string[] items = { "● stars (size ∝ brightness)", "◯ galaxy (true size & PA)", "⊕ globular cluster", "◌ open cluster", "□ nebula", "◎ planetary nebula", "+ asteroid", "• comet" };
                    foreach (var it in items) { g.DrawString(it, sf, Brushes.Black, lx, ly); lx += 160; if (lx > r.Right - 160) { lx = r.X; ly += 18; } }
                }
            };
            using (var dlg = new PrintPreviewDialog { Document = doc, Width = 1100, Height = 800 })
                dlg.ShowDialog(this);
        }

        private void ExportPng()
        {
            using (var d = new SaveFileDialog { Filter = "PNG|*.png", FileName = "skychart.png" })
            {
                if (d.ShowDialog() != DialogResult.OK) return;
                using (var bmp = new Bitmap(Chart.Width * 2, Chart.Height * 2))
                using (var g = Graphics.FromImage(bmp))
                {
                    var clone = new SkyChart { Mode = Chart.Mode, Layers = Chart.Layers, CenterLon = Chart.CenterLon, CenterLat = Chart.CenterLat, FovDeg = Chart.FovDeg, FlipX = Chart.FlipX, FlipY = Chart.FlipY, Highlight = Chart.Highlight, Width = bmp.Width, Height = bmp.Height };
                    clone.Fovs.AddRange(Chart.Fovs);
                    clone.Render(g, bmp.Width, bmp.Height, Theme.Current, false);
                    bmp.Save(d.FileName);
                }
            }
        }

        public IEnumerable<AppCommand> GetCommands()
        {
            yield return new AppCommand { Title = "Sky: planetarium mode", Run = () => { MainForm.Instance.Navigate("sky"); _mode.SelectedIndex = 0; } };
            yield return new AppCommand { Title = "Sky: atlas mode", Run = () => { MainForm.Instance.Navigate("sky"); _mode.SelectedIndex = 1; } };
            yield return new AppCommand { Title = "Sky: print finder chart", Icon = Icons.Print, Run = PrintChart };
            yield return new AppCommand { Title = "Sky: look south", Run = () => { _mode.SelectedIndex = 0; Chart.LookAt(180, 30); } };
            yield return new AppCommand { Title = "Sky: look north", Run = () => { _mode.SelectedIndex = 0; Chart.LookAt(0, 30); } };
            yield return new AppCommand { Title = "Sky: zenith", Run = () => { _mode.SelectedIndex = 0; Chart.LookAt(180, 89.9); } };
        }
    }
}
