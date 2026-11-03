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
using Sidralune.Science.SolarSystem;
using Sidralune.UI;
using AstroEvents = Sidralune.Core.Ephemeris.Events;
using static Sidralune.Core.Util.Lang;

namespace Sidralune.Views
{
    public class SkyView : UserControl, ICommandSource, IActivatable
    {
        public readonly SkyChart Chart = new SkyChart { Dock = DockStyle.Fill };
        /// <summary>The all-sky star chart of the Chart tab, sharing the chart's layers.</summary>
        public readonly StarChartView StarChart = new StarChartView { Dock = DockStyle.Fill, Visible = false };
        private readonly Panel _side = new Panel { Dock = DockStyle.Right, Width = 340, Padding = new Padding(8), AutoScroll = true };
        private readonly SegmentedTabs _mode = new SegmentedTabs { Width = 300 };
        // paper for printing and exporting the star chart, upright or on its side
        private readonly ComboBox _paper = Ui.Combo(ChartPaper.All.SelectMany(p => new[] { T("{0} portrait", p.Name), T("{0} landscape", p.Name) }).ToArray());
        // controls for the planetarium and atlas only, hidden on the fixed star chart
        private readonly List<Control> _viewOnly = new List<Control>();
        private readonly TextBox _find = new TextBox { Width = 200 };
        private readonly ComboBox _fov = Ui.Combo(T("Naked eye (100°)"), T("Wide (60°)"), T("Binoculars (7°)"), T("Finder (5°)"), T("Low power (2°)"), T("Eyepiece"), T("Camera"), T("Custom"));
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
            _mode.Tabs.AddRange(new[] { T("Planetarium"), T("Atlas"), T("Chart") });
            _mode.SelectedChanged += i => { if (i == 2) ShowStarChart(true); else { ShowStarChart(false); SetMode(i == 0 ? ChartMode.Planetarium : ChartMode.Atlas); } };
            bar.Controls.Add(_mode);
            bar.Controls.Add(Ui.Separator());
            _find.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { FindObject(_find.Text); e.SuppressKeyPress = true; } };
            var cue = new Label { Text = Icons.Search, Font = Theme.IconFont, AutoSize = true, Margin = new Padding(0, 7, 2, 0) };
            bar.Controls.Add(cue); bar.Controls.Add(_find);
            bar.Controls.Add(Ui.Separator());
            bar.Controls.Add(Btn(null, Icons.Left + Icons.Left, T("Back 1 day"), () => AppState.Step(-1)));
            bar.Controls.Add(Btn(null, Icons.Left, T("Back 1 hour"), () => AppState.Step(-1 / 24.0)));
            bar.Controls.Add(Btn(T("Now"), Icons.Clock, T("Return to live time"), AppState.GoLive));
            bar.Controls.Add(Btn(null, Icons.Right, T("Forward 1 hour"), () => AppState.Step(1 / 24.0)));
            bar.Controls.Add(Btn(null, Icons.Right + Icons.Right, T("Forward 1 day"), () => AppState.Step(1)));
            var play = new FlatButton { Icon = Icons.Play, Toggle = true, Width = 34, ToolTipText = T("Animate time (×300)") };
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
            var mirror = new FlatButton { Text = T("Mirror"), Toggle = true, ToolTipText = T("Mirror image (star diagonal / refractor view)") };
            mirror.AutoSizeToText();
            mirror.CheckedChanged += (s, e) => { Chart.FlipX = mirror.Checked; Chart.Invalidate(); };
            bar.Controls.Add(mirror);
            var flip = new FlatButton { Text = T("Invert"), Toggle = true, ToolTipText = T("Rotate 180° (Newtonian view)") };
            flip.AutoSizeToText();
            flip.CheckedChanged += (s, e) => { Chart.FlipX = flip.Checked ^ mirror.Checked; Chart.FlipY = flip.Checked; Chart.Invalidate(); };
            bar.Controls.Add(flip);
            _viewOnly.AddRange(new Control[] { _fov, mirror, flip });
            _paper.Width = 130;
            _paper.Visible = false;
            bar.Controls.Add(_paper);
            bar.Controls.Add(Btn(null, Icons.Print, T("Print the chart…"), Print));
            bar.Controls.Add(Btn(null, Icons.Save, T("Export the chart as PNG…"), Export));

            BuildSide();
            StarChart.Layers = Chart.Layers;
            Controls.Add(Chart);
            Controls.Add(StarChart);
            Controls.Add(_side);
            Controls.Add(bar);

            Chart.ObjectClicked += o => { AppState.Select(o); };
            Chart.ContextRequested += ShowContext;
            StarChart.ObjectClicked += o => { AppState.Select(o); };
            AppState.ObjectSelected += o => { if (o != null) ShowInfo(o); };
            AppState.TimeChanged += (s, e) => { Redraw(); dt.Value = AppState.Site.ToLocal(AppState.Now.ToDateTimeUtc()); if (_sel != null) ShowInfo(_sel); };
            AppState.SiteChanged += (s, e) => Redraw();
            _live.Tick += (s, e) => { if (Visible) { Redraw(); if (_sel != null) UpdateLiveInfo(); } };
            _live.Start();
            Theme.Changed += (s, e) => Redraw();
            CatalogManager.Instance.CatalogsChanged += (s, e) => { if (IsHandleCreated) BeginInvoke(new Action(Redraw)); };
            dt.Value = AppState.Site.ToLocal(AppState.Now.ToDateTimeUtc());
        }

        public void Activated() { if (StarChart.Visible) StarChart.Focus(); else Chart.Focus(); }

        private void Redraw() { Chart.Invalidate(); StarChart.Invalidate(); }

        /// <summary>Show the fixed all-sky star chart in place of the planetarium or atlas, or go back to them.</summary>
        private void ShowStarChart(bool on)
        {
            StarChart.Visible = on;
            Chart.Visible = !on;
            foreach (var c in _viewOnly) c.Visible = !on;
            _paper.Visible = on;
            if (on) StarChart.Focus(); else Chart.Focus();
        }

        private FlatButton Btn(string text, string icon, string tip, Action a)
        {
            var b = new FlatButton(text, icon, (s, e) => a()) { ToolTipText = tip };
            if (text == null) b.Width = 34;
            return b;
        }

        private FlatButton LayersButton()
        {
            var b = new FlatButton(T("Layers"), Icons.Layers);
            var menu = new ContextMenuStrip { Renderer = new DarkToolStripRenderer() };
            var L = Chart.Layers;
            // ticks are refreshed every time the menu opens, since other controls (FOV presets, scripts) change layers too
            var refresh = new List<Action>();
            Action<string, Func<bool>, Action<bool>> add = (name, get, set) =>
            {
                var it = new ToolStripMenuItem(name) { CheckOnClick = true, Checked = get() };
                it.Click += (s, e) => { set(it.Checked); Redraw(); };
                refresh.Add(() => it.Checked = get());
                menu.Items.Add(it);
            };
            add(T("Stars"), () => L.Stars, v => L.Stars = v);
            add(T("Star names"), () => L.StarNames, v => L.StarNames = v);
            add(T("Bayer / Flamsteed"), () => L.Bayer, v => L.Bayer = v);
            add(T("Deep-sky objects"), () => L.DeepSky, v => L.DeepSky = v);
            add(T("Deep-sky labels"), () => L.DeepSkyLabels, v => L.DeepSkyLabels = v);
            add(T("Planets, Sun and Moon"), () => L.Planets, v => L.Planets = v);
            add(T("Comets and asteroids"), () => L.MinorBodies, v => L.MinorBodies = v);
            menu.Items.Add(new ToolStripSeparator());
            add(T("Constellation lines"), () => L.ConstellationLines, v => L.ConstellationLines = v);
            add(T("Constellation names"), () => L.ConstellationNames, v => L.ConstellationNames = v);
            add(T("Constellation boundaries"), () => L.Boundaries, v => L.Boundaries = v);
            add(T("Milky Way"), () => L.MilkyWay, v => L.MilkyWay = v);
            menu.Items.Add(new ToolStripSeparator());
            add(T("Equatorial grid"), () => L.EquatorialGrid, v => L.EquatorialGrid = v);
            add(T("Alt-azimuth grid"), () => L.AltAzGrid, v => L.AltAzGrid = v);
            add(T("Ecliptic"), () => L.Ecliptic, v => L.Ecliptic = v);
            add(T("Galactic equator"), () => L.GalacticEquator, v => L.GalacticEquator = v);
            add(T("Meridian"), () => L.Meridian, v => L.Meridian = v);
            menu.Items.Add(new ToolStripSeparator());
            add(T("Horizon and ground"), () => L.Horizon, v => L.Horizon = v);
            add(T("Atmosphere (daylight sky)"), () => L.Atmosphere, v => L.Atmosphere = v);
            add(T("Cardinal points"), () => L.Cardinals, v => L.Cardinals = v);
            add(T("Telescope position"), () => L.Telescope, v => L.Telescope = v);
            add(T("Field-of-view indicators"), () => L.FovIndicators, v => L.FovIndicators = v);
            add(T("Telrad rings"), () => L.Telrad, v => L.Telrad = v);
            add(T("Refraction"), () => AppState.Settings.UseRefraction, v => AppState.Settings.UseRefraction = v);
            menu.Items.Add(new ToolStripSeparator());
            var mag = new ToolStripMenuItem(T("Limiting magnitude"));
            foreach (var m in new[] { 4.0, 5.0, 6.0, 6.5, 7.5, 8.5, 10.0, 12.0 })
            {
                var mm = m;
                var mi = new ToolStripMenuItem(m.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
                mi.Click += (s, e) => { L.MagLimit = mm; Redraw(); };
                refresh.Add(() => mi.Checked = Math.Abs(L.MagLimit - mm) < 0.01);
                mag.DropDownItems.Add(mi);
            }
            mag.DropDownItems.Add(new ToolStripSeparator());
            var auto = new ToolStripMenuItem(T("Deeper when zoomed in")) { CheckOnClick = true, ToolTipText = T("Show fainter stars as the field of view gets smaller, and on the star chart as the paper gets larger") };
            auto.Click += (s, e) => { L.AutoMagLimit = auto.Checked; Redraw(); };
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
            _objTitle.Text = T("No object selected");
            _objSub.Text = T("Click an object in the chart, or search (Ctrl+K).");
            _actions.Controls.Add(new FlatButton(T("Center"), Icons.Target, (s, e) => { if (_sel != null) Goto(_sel); }));
            _actions.Controls.Add(new FlatButton(T("Slew"), Icons.Observatory, (s, e) => SlewTo(_sel)) { ToolTipText = T("Slew the connected telescope") });
            _actions.Controls.Add(new FlatButton(T("Add to list"), Icons.Add, (s, e) => { if (_sel != null) { MainForm.Instance.GetView<PlannerView>("planner")?.AddObject(_sel); MainForm.Instance.Toast(T("{0} added to the observing list", DisplayName(_sel))); } }));
            _actions.Controls.Add(new FlatButton(T("Frame"), Icons.Camera, (s, e) => { if (_sel != null) { MainForm.Instance.Navigate("astrometry"); MainForm.Instance.GetView<AstrometryView>("astrometry")?.FrameTarget(_sel); } }) { ToolTipText = T("Plan framing / mosaic") });
            _actions.Controls.Add(new FlatButton(T("Copy"), Icons.List, (s, e) => { if (_sel != null) Clipboard.SetText(_sel.Name + " " + AstroMath.FormatHms(_sel.RaDeg / 15, 1) + " " + AstroMath.FormatDms(_sel.DecDeg, 0)); }) { ToolTipText = T("Copy name and coordinates") });
            foreach (Control c in _actions.Controls) ((FlatButton)c).AutoSizeToText();
            _alt.Title = T("Altitude tonight");
        }

        /// <summary>Show a field fixed on the sky (e.g. a plate-solved image) in atlas mode, centred and framed.</summary>
        public void ShowField(string name, List<double[]> outline, double raRad, double decRad, double fovDeg)
        {
            _mode.SelectedIndex = 1; // atlas, keeping the tab and the chart in step
            Chart.Fovs.RemoveAll(f => f.Name == name);
            Chart.Fovs.Add(new ChartFov { Name = name, Outline = outline });
            Chart.Layers.FovIndicators = true;
            Chart.CenterOn(raRad, decRad, fovDeg);
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
                case 2: Chart.FovDeg = 12; Chart.Fovs.Add(new ChartFov { Name = T("10×50 binoculars"), WidthDeg = 6.5 }); break;
                case 3: Chart.FovDeg = 9; Chart.Fovs.Add(new ChartFov { Name = T("Finder 8×50"), WidthDeg = 5 }); Chart.Layers.Telrad = true; break;
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

        /// <summary>A moon of a planet (Ganymede, Titan, Triton…) now; null for any other name.</summary>
        public static SelectedObject MoonObject(string name)
        {
            var m = PlanetMoons.Find(name);
            return m == null ? null : SelectedObject.From(PlanetMoons.Place(new SkyContext(AppState.Site, AppState.Now), m));
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
            // the Sun, Moon or a planet by its English name or the one shown ("Saturnus")
            string name = q.Trim();
            var body = Enum.GetValues(typeof(Body)).Cast<Body>().Where(x => x != Body.Earth && x != Body.EarthMoonBarycenter)
                .Where(x => string.Equals(x.ToString(), name, StringComparison.OrdinalIgnoreCase) || string.Equals(BodyNames.Name(x), name, StringComparison.CurrentCultureIgnoreCase)).ToList();
            if (body.Count > 0) { var o = PlanetObject(body[0]); AppState.Select(o); Goto(o); return; }
            var moon = MoonObject(q);
            if (moon != null) { AppState.Select(moon); Goto(moon); return; }
            var hit = CatalogManager.Instance.Find(q) ?? CatalogManager.Instance.Search(q, 1).FirstOrDefault();
            if (hit != null) { var o = SelectedObject.From(hit); AppState.Select(o); Goto(o); return; }
            var mb = MinorBodyStore.Instance.Find(q);
            if (mb != null) { var o = MinorObject(mb); AppState.Select(o); Goto(o); return; }
            // coordinates "hh mm ss +dd mm ss"
            var parts = q.Split(new[] { ',', ';' });
            MainForm.Instance.Toast(T("Not found: {0} (try the Databases view for SIMBAD lookups)", q), Icons.Warning);
        }

        public void Goto(SelectedObject o)
        {
            if (o == null) return;
            Chart.Highlight = o;
            if (StarChart.Visible)
            {
                // the star chart stays fixed: the object is only marked, where it is above the horizon
                ShowInfo(o);
                return;
            }
            double fov = Chart.FovDeg;
            if (o.Kind == SelectedKind.DeepSky && !double.IsNaN(o.SizeArcmin)) fov = Math.Max(0.3, Math.Min(Chart.FovDeg, o.SizeArcmin / 60 * 8));
            if (o.Kind == SelectedKind.PlanetMoon && o.Planet.HasValue)
            {
                // close enough to tell the moon from its planet, with the planet in view too
                var pl = PlanetObject(o.Planet.Value);
                double sep = AstroMath.Separation(o.RaDeg * AstroMath.Deg2Rad, o.DecDeg * AstroMath.Deg2Rad, pl.RaDeg * AstroMath.Deg2Rad, pl.DecDeg * AstroMath.Deg2Rad) * AstroMath.Rad2Deg;
                fov = Math.Min(Chart.FovDeg, Math.Max(SkyChart.MinFovDeg, sep * 4));
            }
            Chart.CenterOn(o.RaDeg * AstroMath.Deg2Rad, o.DecDeg * AstroMath.Deg2Rad, fov);
            ShowInfo(o);
        }

        private void ShowInfo(SelectedObject o)
        {
            _sel = o;
            Chart.Highlight = o;
            StarChart.Page.Highlight = o;
            StarChart.Invalidate();
            _objTitle.Text = DisplayName(o);
            var con = CatalogManager.Instance.ConstellationAt(o.RaDeg * AstroMath.Deg2Rad, o.DecDeg * AstroMath.Deg2Rad);
            _objSub.Text = con != null ? T("{0} in {1}", KindLabel(o), con.Name) : KindLabel(o);
            UpdateLiveInfo();
            // altitude curve for the coming night
            _alt.Clear();
            var site = AppState.Site;
            var start = AppState.Now;
            var local = site.ToLocal(start.ToDateTimeUtc());
            var noon = site.ToUtc(local.Date.AddHours(local.Hour < 12 ? -12 : 12));
            var series = _alt.AddSeries(o.Name);
            // UTC on the axis, labelled with the site's clock: a daylight saving change must not fold the curve
            _alt.TimeZone = site.TimeZone;
            for (int i = 0; i <= 96; i++)
            {
                var t = AstroTime.FromDateTime(noon).AddMinutes(i * 15);
                double alt = AltitudeAt(o, t);
                series.Add(t.ToDateTimeUtc().ToOADate(), alt);
            }
            var tw = AstroEvents.Twilight(site, AstroTime.FromDateTime(noon));
            Action<AstroTime?, AstroTime?, int> band = (a, b2, alpha) =>
            {
                if (a.HasValue && b2.HasValue) _alt.Bands.Add(new PlotBand { X0 = a.Value.ToDateTimeUtc().ToOADate(), X1 = b2.Value.ToDateTimeUtc().ToOADate(), ThemeColor = pal => Theme.WithAlpha(pal.Accent, alpha) });
            };
            band(tw.SunSet, tw.SunRise, 18);
            band(tw.NauticalDusk, tw.NauticalDawn, 18);
            band(tw.AstronomicalDusk, tw.AstronomicalDawn, 22);
            _alt.FixedYMin = 0; _alt.FixedYMax = 90;
            _alt.Markers.Add(new PlotMarker { X = AppState.Now.ToDateTimeUtc().ToOADate(), Label = T("now") });
            _alt.Invalidate();
        }

        /// <summary>An object's name as shown: the Sun, Moon and planets in the interface language.</summary>
        public static string DisplayName(SelectedObject o) => o.Kind == SelectedKind.Planet && o.Planet.HasValue ? BodyNames.Name(o.Planet.Value) : o.Name;

        // what kind of object it is, in words
        private static string KindLabel(SelectedObject o)
        {
            switch (o.Kind)
            {
                case SelectedKind.DeepSky: return DeepSkyObject.TypeLabel(o.Catalog.Type);
                case SelectedKind.PlanetMoon: return T("Moon of {0}", o.Planet.HasValue ? BodyNames.Name(o.Planet.Value) : "");
                case SelectedKind.Star: return T("Star");
                case SelectedKind.Planet: return o.Planet == Body.Sun ? T("Star") : o.Planet == Body.Moon ? T("Moon") : T("Planet");
                case SelectedKind.MinorBody: return o.Minor != null && o.Minor.IsComet ? T("Comet") : T("Asteroid");
                case SelectedKind.Satellite: return T("Satellite");
                case SelectedKind.Coordinates: return T("Position");
                case SelectedKind.RadioSource: return T("Radio source");
                default: return o.Kind.ToString();
            }
        }

        private double AltitudeAt(SelectedObject o, AstroTime t)
        {
            // a moon of a planet rises and sets with it
            if ((o.Kind == SelectedKind.Planet || o.Kind == SelectedKind.PlanetMoon) && o.Planet.HasValue)
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
            // a label and its value; the labels are padded so short ones line up
            Func<string, string, string> row = (label, value) => (label + " ").PadRight(16) + value;
            double ra = o.RaDeg, dec = o.DecDeg, mag = o.Mag, size = o.SizeArcmin;
            Horizontal hz;
            Equatorial app;
            if (o.Kind == SelectedKind.Planet && o.Planet.HasValue)
            {
                var p = ctx.BodyPlace(o.Planet.Value);
                if (o.Planet.Value != Body.Sun && o.Planet.Value != Body.Moon) PlanetPhysics.Fill(o.Planet.Value, p, ctx.JdTT);
                ra = p.Astrometric.RaHours * 15; dec = p.Astrometric.DecDeg; hz = p.Horizontal; app = p.Apparent;
                if (o.Planet.Value != Body.Moon && o.Planet.Value != Body.Sun) { mag = p.Magnitude; size = p.DiameterArcsec / 60; }
                lines.Add(row(T("Distance"), T("{0:0.0000} AU ({1:N0} km)", p.Distance, p.Distance * AstroMath.AU_km)));
                if (o.Planet.Value != Body.Sun)
                {
                    lines.Add(row(T("Illuminated"), T("{0:0.0}%   phase angle {1:0.0}°", p.IlluminatedFraction * 100, p.PhaseAngle * AstroMath.Rad2Deg)));
                    lines.Add(row(T("Elongation"), T("{0:0.0}°", p.Elongation * AstroMath.Rad2Deg)));
                }
                if (o.Planet.Value == Body.Moon) lines.Add(row(T("Age"), T("{0:0.0} days", AstroEvents.MoonAgeDays(ctx.JdTT))));
                if (!double.IsNaN(p.DiameterArcsec)) lines.Add(row(T("Apparent size"), T("{0:0.0}\"", p.DiameterArcsec)));
                if (o.Planet.Value == Body.Jupiter)
                {
                    double s1, s2, s3;
                    PlanetPhysics.JupiterCentralMeridians(ctx.JdTDB, p.Astrometric.ToVector(), p.LightTimeDays, out s1, out s2, out s3);
                    lines.Add(T("CM  I {0:0.0}°  II {1:0.0}°  III {2:0.0}°", s1, s2, s3));
                }
                if (o.Planet.Value == Body.Mars || o.Planet.Value == Body.Saturn)
                {
                    double cm, lat, pa;
                    PlanetPhysics.Orientation(o.Planet.Value, ctx.JdTDB, p.Astrometric.ToVector(), p.LightTimeDays, out cm, out lat, out pa);
                    lines.Add(T("CM {0:0.0}°  sub-Earth lat {1:+0.0;-0.0}°  pole PA {2:0.0}°", cm, lat, pa));
                }
            }
            else if (o.Kind == SelectedKind.PlanetMoon && PlanetMoons.Find(o.Name) != null)
            {
                var moon = PlanetMoons.Find(o.Name);
                var m = PlanetMoons.Place(ctx, moon);
                var p = m.Place;
                ra = p.Astrometric.RaHours * 15; dec = p.Astrometric.DecDeg; hz = p.Horizontal; app = p.Apparent; mag = m.Mag; size = m.DiameterArcsec / 60;
                // the chart and other views follow the moon as it moves
                o.RaDeg = ra; o.DecDeg = dec;
                string planet = BodyNames.Name(moon.Planet);
                string place = m.Occulted ? T("hidden behind {0}", planet) : m.Transit ? T("crossing {0}'s disc", planet) :
                    (m.EastArcsec >= 0 ? T("{0} east of {1}", FormatOffset(m.SeparationArcsec), planet) : T("{0} west of {1}", FormatOffset(m.SeparationArcsec), planet)) + " (" +
                    T("{0:0.0} {1} radii", m.PlanetRadii, planet) + ", " + (m.Behind ? T("behind") : T("in front")) + ")";
                lines.Add(row(T("Position"), place));
                lines.Add(row(T("Distance"), T("{0:0.0000} AU ({1:N0} km)", p.Distance, p.Distance * AstroMath.AU_km)));
                lines.Add(row(T("Diameter"), T("{0:N0} km   apparent {1}\"", 2 * moon.RadiusKm, m.DiameterArcsec.ToString(m.DiameterArcsec < 0.01 ? "0.0000" : m.DiameterArcsec < 0.1 ? "0.000" : "0.00"))));
                lines.Add(row(T("Orbital period"), T("{0} days", moon.PeriodDays.ToString(moon.PeriodDays < 100 ? "0.000" : "0.0")) + (moon.Retrograde ? " " + T("(retrograde)") : "")));
            }
            else if (o.Kind == SelectedKind.MinorBody && o.Minor != null)
            {
                var p = MinorBodyStore.Place(ctx, o.Minor);
                ra = p.Astrometric.RaHours * 15; dec = p.Astrometric.DecDeg; hz = p.Horizontal; app = p.Apparent; mag = p.Magnitude;
                lines.Add(row(T("Distance"), string.Format("Δ {0:0.000} AU   r {1:0.000} AU", p.Distance, p.HelioDistance)));
                lines.Add(row(T("Elongation"), T("{0:0.0}°   phase {1:0.0}°", p.Elongation * AstroMath.Rad2Deg, p.PhaseAngle * AstroMath.Rad2Deg)));
                lines.Add(row(T("Orbit"), o.Minor.OrbitType));
            }
            else
            {
                hz = ctx.IcrsToHorizontal(ra * AstroMath.Deg2Rad, dec * AstroMath.Deg2Rad);
                app = ctx.IcrsToApparent(ra * AstroMath.Deg2Rad, dec * AstroMath.Deg2Rad);
                var st = o.Catalog as Star;
                if (st != null)
                {
                    if (!float.IsNaN(st.BV)) lines.Add(row("B−V", string.Format("{0:0.00}   (~{1:N0} K)", st.BV, st.TemperatureK)));
                    if (!string.IsNullOrEmpty(st.SpectralType)) lines.Add(row(T("Spectral type"), st.SpectralType));
                    if (!float.IsNaN(st.Parallax) && st.Parallax > 0) lines.Add(row(T("Distance"), T("{0:0.0} ly", 3261.56 / st.Parallax)));
                    if (st.Hip > 0) lines.Add("HIP " + st.Hip + (st.Hd > 0 ? "   HD " + st.Hd : "") + (st.Bayer != null ? "   " + st.Bayer : ""));
                    if (st.VariableType != null) lines.Add(row(T("Variable"), st.VariableType + (float.IsNaN(st.MagMin) ? "" : " " + T("(to {0:0.0})", st.MagMin))));
                }
                var d = o.Catalog as DeepSkyObject;
                if (d != null)
                {
                    if (d.Morphology != null) lines.Add(row(T("Morphology"), d.Morphology));
                    if (!double.IsNaN(d.EffectiveSurfaceBrightness)) lines.Add(row(T("Surface bright."), T("{0:0.0} mag/arcsec²", d.EffectiveSurfaceBrightness)));
                    if (d.AltNames.Count > 0) lines.Add(row(T("Also"), string.Join(", ", d.AltNames.Take(4))));
                }
            }
            var head = new List<string>
            {
                row(T("Magnitude"), double.IsNaN(mag) ? "—" : mag.ToString("0.0")),
                row(T("Size"), double.IsNaN(size) ? "—" : AstroMath.FormatAngleAuto(size / 60)),
                row(T("RA/Dec J2000"), AstroMath.FormatHms(ra / 15, 1) + "  " + AstroMath.FormatDms(dec, 0)),
                row(T("RA/Dec apparent"), AstroMath.FormatHms(app.RaHours, 1) + "  " + AstroMath.FormatDms(app.DecDeg, 0)),
                row(T("Alt/Az"), T("{0:+0.0;-0.0}°  /  {1:0.0}°   airmass {2}", hz.AltDeg, hz.AzDeg, hz.Alt > 0 ? Transforms.Airmass(hz.Alt).ToString("0.00") : "—")),
                row(T("Hour angle"), AstroMath.FormatHms(AstroMath.NormalizeHours(ctx.LastHours - app.RaHours), 0, true))
            };
            // rise / transit / set
            try
            {
                RiseSetResult rs = (o.Kind == SelectedKind.Planet || o.Kind == SelectedKind.PlanetMoon) && o.Planet.HasValue ? AstroEvents.BodyRiseSet(o.Planet.Value, site, AppState.Now.AddHours(-12))
                    : AstroEvents.FixedRiseSet(ra * AstroMath.Deg2Rad, dec * AstroMath.Deg2Rad, site, AppState.Now.AddHours(-12));
                Func<AstroTime?, string> fmt = t => t.HasValue ? site.ToLocal(t.Value.ToDateTimeUtc()).ToString("HH:mm") : "—";
                head.Add(rs.AlwaysUp ? T("Circumpolar (never sets)") : rs.NeverUp ? T("Never rises today") : T("Rise {0}  Transit {1} ({2:0}°)  Set {3}", fmt(rs.Rise), fmt(rs.Transit), rs.TransitAltitudeDeg, fmt(rs.Set)));
            }
            catch { }
            // visibility with the current instrument (SkyTools-style)
            // not for planets and their moons, whose light the model (for stars and nebulae against the sky) does not cover
            if (!double.IsNaN(mag) && o.Kind != SelectedKind.Planet && o.Kind != SelectedKind.PlanetMoon)
            {
                double alt;
                double sky = VisibilityModel.SkyBrightnessAt(ctx, ra * AstroMath.Deg2Rad, dec * AstroMath.Deg2Rad, out alt);
                var vis = VisibilityModel.Predict(mag, double.IsNaN(size) ? 0 : size, alt, sky, site, AppState.Instrument, AppState.Eye);
                head.Add(row(T("Visibility"), T("{0}  (margin {1:+0.0;-0.0} mag)", vis.Summary, vis.BestMargin)));
                head.Add(row(T("Best view"), T("×{0:0} with {1}", vis.BestMagnification, AppState.Instrument.Name)));
                head.Add(row(T("Sky brightness"), T("{0:0.00} mag/arcsec²", sky)));
            }
            _objInfo.Text = string.Join("\r\n", head.Concat(new[] { "" }).Concat(lines));
        }

        // a moon's distance from its planet: arcseconds, arcminutes for the outer moons
        public static string FormatOffset(double arcsec) =>
            arcsec < 60 ? arcsec.ToString("0.0") + "\"" : arcsec < 3600 ? (arcsec / 60).ToString("0.0") + "′" : (arcsec / 3600).ToString("0.00") + "°";

        private void SlewTo(SelectedObject o)
        {
            if (o == null) return;
            var tel = DeviceManager.Instance.Telescope;
            if (tel == null) { MainForm.Instance.Toast(T("No telescope connected (Observatory → Equipment)"), Icons.Warning); return; }
            var app = new SkyContext(AppState.Site, Core.Time.AstroTime.Now).IcrsToApparent(o.RaDeg * AstroMath.Deg2Rad, o.DecDeg * AstroMath.Deg2Rad);
            Tasks.Run(T("Slewing to {0}", DisplayName(o)), t => tel.SlewToCoordinatesAsync(app.RaHours, app.DecDeg), () => MainForm.Instance.Toast(T("Slewing to {0}", DisplayName(o))));
        }

        private void ShowContext(double ra, double dec, MouseEventArgs e)
        {
            var m = new ContextMenuStrip { Renderer = new DarkToolStripRenderer() };
            m.Items.Add(string.Format("{0}  {1}", AstroMath.FormatHms(ra / 15, 0), AstroMath.FormatDms(dec, 0))).Enabled = false;
            var hit = Chart.HitTest(e.Location);
            if (hit != null)
            {
                m.Items.Add(T("Select {0}", DisplayName(hit)), null, (s, a) => AppState.Select(hit));
                m.Items.Add(T("Add {0} to observing list", DisplayName(hit)), null, (s, a) => MainForm.Instance.GetView<PlannerView>("planner")?.AddObject(hit));
            }
            m.Items.Add(T("Center here"), null, (s, a) => Chart.CenterOn(ra * AstroMath.Deg2Rad, dec * AstroMath.Deg2Rad));
            m.Items.Add(T("Slew telescope here"), null, (s, a) => SlewTo(new SelectedObject { Name = T("position"), RaDeg = ra, DecDeg = dec }));
            m.Items.Add(T("Frame with camera here"), null, (s, a) => { MainForm.Instance.Navigate("astrometry"); MainForm.Instance.GetView<AstrometryView>("astrometry")?.FrameTarget(new SelectedObject { Name = T("Field"), RaDeg = ra, DecDeg = dec }); });
            m.Show(Chart, e.Location);
        }

        // ------------------------------------------------------------------ printing / export

        private void PrintChart()
        {
            var doc = new PrintDocument { DocumentName = T("Sidralune sky chart") };
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
                using (var tf = new Font("Segoe UI Semibold", 14f)) g.DrawString((_sel != null ? DisplayName(_sel) + " — " : "") + T("Sidralune finder chart"), tf, Brushes.Black, r.X, r.Y);
                using (var sf = new Font("Segoe UI", 9f))
                {
                    g.DrawString(T("{0:yyyy-MM-dd HH:mm} local, {1}   ·   field {2}   ·   limiting magnitude {3:0.0}   ·   {4}", local, AppState.Site, AstroMath.FormatAngleAuto(Chart.FovDeg), Chart.EffectiveMagLimit, Chart.FlipX ? T("mirror view") : T("normal view")), sf, Brushes.Black, r.X, r.Y + 26);
                    int ly = chartRect.Bottom + 10, lx = r.X;
                    string[] items = { "● " + T("stars (size ∝ brightness)"), "◯ " + T("galaxy (true size & PA)"), "⊕ " + T("globular cluster"), "◌ " + T("open cluster"), "□ " + T("nebula"), "◎ " + T("planetary nebula"), "+ " + T("asteroid"), "• " + T("comet") };
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

        private void Print() { if (StarChart.Visible) PrintStarChart(); else PrintChart(); }

        private void Export() { if (StarChart.Visible) ExportStarChart(); else ExportPng(); }

        private ChartPaper SelectedPaper(out bool landscape)
        {
            int i = Math.Max(0, _paper.SelectedIndex);
            landscape = i % 2 == 1;
            return ChartPaper.All[i / 2];
        }

        private StarChartPage PaperPage() => new StarChartPage { Site = AppState.Site, Time = AppState.Now, Layers = Chart.Layers, Refraction = AppState.Settings.UseRefraction };

        /// <summary>The star chart on the chosen paper: pick the printer, check the page in the preview, then print.</summary>
        private void PrintStarChart()
        {
            bool landscape;
            var paper = SelectedPaper(out landscape);
            var doc = new PrintDocument { DocumentName = T("Sidralune star chart") };
            doc.DefaultPageSettings.Landscape = landscape;
            doc.DefaultPageSettings.Margins = new Margins(39, 39, 39, 39); // 10 mm
            try { doc.DefaultPageSettings.PaperSize = StarChartPage.FindPaperSize(doc.PrinterSettings, paper); }
            catch (InvalidPrinterException) { MainForm.Instance.Toast(T("No printer is installed"), Icons.Warning); return; }
            var page = PaperPage();
            doc.PrintPage += (s, e) => page.PrintPage(s, e);
            using (var pd = new PrintDialog { Document = doc, UseEXDialog = true })
                if (pd.ShowDialog(this) != DialogResult.OK) return;
            using (var dlg = new PrintPreviewDialog { Document = doc, Width = 1000, Height = 1100, Text = T("Star chart on {0}", paper.Name) })
                dlg.ShowDialog(this);
        }

        private void ExportStarChart()
        {
            bool landscape;
            var paper = SelectedPaper(out landscape);
            using (var d = new SaveFileDialog { Filter = "PNG|*.png", FileName = T("Star chart {0}", paper.Name) + ".png" })
            {
                if (d.ShowDialog() != DialogResult.OK) return;
                try
                {
                    using (var bmp = PaperPage().RenderImage(paper, landscape, 300)) bmp.Save(d.FileName, System.Drawing.Imaging.ImageFormat.Png);
                    MainForm.Instance.Toast(T("Star chart saved: {0} at 300 dpi", paper.Name));
                }
                catch (Exception ex) { MainForm.Instance.Toast(T("Could not save the star chart: {0}", ex.Message), Icons.Warning); }
            }
        }

        public IEnumerable<AppCommand> GetCommands()
        {
            yield return new AppCommand { Title = T("Sky: planetarium mode"), Run = () => { MainForm.Instance.Navigate("sky"); _mode.SelectedIndex = 0; } };
            yield return new AppCommand { Title = T("Sky: atlas mode"), Run = () => { MainForm.Instance.Navigate("sky"); _mode.SelectedIndex = 1; } };
            yield return new AppCommand { Title = T("Sky: star chart mode"), Run = () => { MainForm.Instance.Navigate("sky"); _mode.SelectedIndex = 2; } };
            yield return new AppCommand { Title = T("Sky: print star chart"), Icon = Icons.Print, Run = () => { MainForm.Instance.Navigate("sky"); _mode.SelectedIndex = 2; PrintStarChart(); } };
            yield return new AppCommand { Title = T("Sky: print finder chart"), Icon = Icons.Print, Run = PrintChart };
            yield return new AppCommand { Title = T("Sky: look south"), Run = () => { _mode.SelectedIndex = 0; Chart.LookAt(180, 30); } };
            yield return new AppCommand { Title = T("Sky: look north"), Run = () => { _mode.SelectedIndex = 0; Chart.LookAt(0, 30); } };
            yield return new AppCommand { Title = T("Sky: zenith"), Run = () => { _mode.SelectedIndex = 0; Chart.LookAt(180, 89.9); } };
        }
    }
}
