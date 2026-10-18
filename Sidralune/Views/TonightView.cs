using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
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
using Sidralune.Science.SolarSystem;
using Sidralune.UI;
using AstroEvents = Sidralune.Core.Ephemeris.Events;

namespace Sidralune.Views
{
    /// <summary>Home dashboard: what's up tonight, at a glance (SkyTools "Nightly Planner" summary).</summary>
    public class TonightView : UserControl, IActivatable
    {
        private readonly FlowLayoutPanel _flow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(10), WrapContents = true };
        private readonly Card _night = new Card("Tonight");
        private readonly NightTimeline _timeline = new NightTimeline { Dock = DockStyle.Top, Height = 76 };
        private readonly Label _nightText = new Label { Dock = DockStyle.Fill, Font = Theme.UiFont };
        private readonly Card _moon = new Card("Moon");
        private readonly MoonGlyph _moonGlyph = new MoonGlyph { Width = 96, Height = 96, Dock = DockStyle.Left };
        private readonly Label _moonText = new Label { Dock = DockStyle.Fill, Padding = new Padding(12, 0, 0, 0) };
        private readonly Card _planets = new Card("Planets");
        private readonly DataGridView _planetGrid = Ui.Grid();
        private readonly Card _targets = new Card("Best deep-sky targets tonight");
        private readonly DataGridView _targetGrid = Ui.Grid();
        private readonly Card _events = new Card("Coming up");
        private readonly DataGridView _eventGrid = Ui.Grid();
        private readonly Card _jupiter = new Card("Jupiter's moons");
        private readonly JupiterMoonsGlyph _jupGlyph = new JupiterMoonsGlyph { Dock = DockStyle.Fill };
        private readonly Card _minor = new Card("Comets & asteroids");
        private readonly DataGridView _minorGrid = Ui.Grid();
        private readonly Card _data = new Card("Getting started");
        private DateTime _lastRefresh = DateTime.MinValue;

        public TonightView()
        {
            Controls.Add(_flow);
            _night.Controls.Add(_nightText); _night.Controls.Add(_timeline);
            _moon.Controls.Add(_moonText); _moon.Controls.Add(_moonGlyph);
            _planets.Controls.Add(_planetGrid);
            _targets.Controls.Add(_targetGrid);
            _events.Controls.Add(_eventGrid);
            _jupiter.Controls.Add(_jupGlyph);
            _minor.Controls.Add(_minorGrid);
            BuildDataCard();
            foreach (var c in new Control[] { _night, _moon, _planets, _targets, _events, _jupiter, _minor, _data }) _flow.Controls.Add(c);
            _flow.Resize += (s, e) => LayoutCards();
            foreach (var g in new[] { _planetGrid, _targetGrid, _minorGrid })
                g.CellDoubleClick += (s, e) => { if (e.RowIndex >= 0 && ((DataGridView)s).Rows[e.RowIndex].Tag is SelectedObject) MainForm.Instance.ShowObject((SelectedObject)((DataGridView)s).Rows[e.RowIndex].Tag); };
            _targets.Subtitle = "double-click to show in the sky chart";
            AppState.SiteChanged += (s, e) => Refresh(true);
            AppState.TimeChanged += (s, e) => Refresh(false);
            CatalogManager.Instance.CatalogsChanged += (s, e) => { if (IsHandleCreated) BeginInvoke(new Action(() => Refresh(true))); };
            MinorBodyStore.Instance.Changed += (s, e) => { if (IsHandleCreated) BeginInvoke(new Action(() => Refresh(true))); };
            var t = new Timer { Interval = 60000 };
            t.Tick += (s, e) => { if (Visible) Refresh(false); };
            t.Start();
        }

        public void Activated() { if ((DateTime.Now - _lastRefresh).TotalMinutes > 5) Refresh(true); }

        private void LayoutCards()
        {
            int w = _flow.ClientSize.Width - _flow.Padding.Horizontal - 20;
            int cols = w > 1300 ? 3 : w > 820 ? 2 : 1;
            int cw = w / cols - 12;
            _night.Size = new Size(cw, 230);
            _moon.Size = new Size(cw, 230);
            _planets.Size = new Size(cols >= 3 ? cw : cw, 300);
            _targets.Size = new Size(cols >= 2 ? cw * 2 + 12 : cw, 360);
            _events.Size = new Size(cw, 360);
            _jupiter.Size = new Size(cw, 180);
            _minor.Size = new Size(cw, 300);
            _data.Size = new Size(cw, 300);
        }

        private void BuildDataCard()
        {
            var f = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
            f.Controls.Add(Ui.Hint("Sidralune works offline with a small built-in catalog. For full star fields (120,000 stars), all NGC/IC objects, constellation figures, comets and milliarcsecond planet positions, download the recommended data in Settings → Data."));
            var b1 = new FlatButton("Open data manager", Icons.Download, (s, e) => MainForm.Instance.Navigate("settings")) { Kind = ButtonKind.Primary };
            b1.AutoSizeToText();
            var b2 = new FlatButton("Set observing site", Icons.Location, (s, e) => MainForm.Instance.Navigate("settings"));
            b2.AutoSizeToText();
            var b3 = new FlatButton("Try the simulated observatory", Icons.Observatory, (s, e) => { Tasks.Run("Connect simulators", t => Devices.DeviceManager.Instance.ConnectSimulators(), () => { MainForm.Instance.Toast("Simulated observatory connected"); MainForm.Instance.Navigate("observatory"); }); });
            b3.AutoSizeToText();
            f.Controls.Add(b1); f.Controls.Add(b2); f.Controls.Add(b3);
            f.Controls.Add(Ui.Hint("Tip: press Ctrl+K anywhere to search objects and commands. Ctrl+Shift+N toggles red night-vision mode."));
            _data.Controls.Add(f);
        }

        private class NightData
        {
            public TwilightTimes Tw;
            public AstroTime Noon;
            public BodyPlace Moon;
            public double MoonAge;
            public RiseSetResult MoonRs;
            public string Phase;
            public List<object[]> Planets = new List<object[]>();
            public List<object[]> Targets = new List<object[]>();
            public List<object[]> Events = new List<object[]>();
            public List<object[]> Minor = new List<object[]>();
            public double JdTT;
        }

        private void Refresh(bool full)
        {
            _lastRefresh = DateTime.Now;
            var site = AppState.Site;
            var now = AppState.Now;
            var local = site.ToLocal(now.ToDateTimeUtc());
            var noon = AstroTime.FromDateTime(site.ToUtc(local.Date.AddHours(local.Hour < 12 ? -12 : 12)));
            _night.Title = "Tonight at " + site.Name;
            _night.Subtitle = noon.AddDays(0.5).ToDateTimeUtc().ToString("ddd d MMM yyyy");
            Tasks.Run("Computing tonight's sky", t =>
            {
                var d = new NightData { Noon = noon, JdTT = now.JdTT };
                d.Tw = AstroEvents.Twilight(site, noon);
                var ctx = new SkyContext(site, now);
                d.Moon = ctx.BodyPlace(Body.Moon);
                d.MoonAge = AstroEvents.MoonAgeDays(now.JdTT);
                d.MoonRs = AstroEvents.BodyRiseSet(Body.Moon, site, noon);
                double e = AstroEvents.MoonSunElongation(now.JdTT);
                d.Phase = e < 10 || e > 350 ? "New Moon" : e < 80 ? "Waxing crescent" : e < 100 ? "First quarter" : e < 170 ? "Waxing gibbous" : e < 190 ? "Full Moon" : e < 260 ? "Waning gibbous" : e < 280 ? "Last quarter" : "Waning crescent";
                Func<AstroTime?, string> hm = x => x.HasValue ? site.ToLocal(x.Value.ToDateTimeUtc()).ToString("HH:mm") : "—";
                foreach (var b in new[] { Body.Mercury, Body.Venus, Body.Mars, Body.Jupiter, Body.Saturn, Body.Uranus, Body.Neptune })
                {
                    var pl = ctx.BodyPlace(b);
                    PlanetPhysics.Fill(b, pl, ctx.JdTT);
                    var rs = AstroEvents.BodyRiseSet(b, site, noon);
                    var con = CatalogManager.Instance.ConstellationAt(pl.Astrometric.Ra, pl.Astrometric.Dec);
                    var so = new SelectedObject { Kind = SelectedKind.Planet, Planet = b, Name = b.ToString(), RaDeg = pl.Astrometric.RaHours * 15, DecDeg = pl.Astrometric.DecDeg, Mag = pl.Magnitude };
                    d.Planets.Add(new object[] { so, b.ToString(), hm(rs.Rise), hm(rs.Transit), hm(rs.Set), pl.Magnitude.ToString("0.0"), pl.DiameterArcsec.ToString("0.0") + "\"", con != null ? con.Abbr : "", pl.Horizontal.AltDeg.ToString("0") + "°" });
                }
                Tasks.Report(t, 0.3);
                // best targets: sample the dark hours, take each object's best detection margin with the current instrument
                if (d.Tw.AstronomicalDusk.HasValue || d.Tw.NauticalDusk.HasValue)
                {
                    var start = d.Tw.AstronomicalDusk ?? d.Tw.NauticalDusk.Value;
                    var end = d.Tw.AstronomicalDawn ?? d.Tw.NauticalDawn ?? start.AddHours(6);
                    var samples = new List<AstroTime>();
                    for (var x = start; x < end; x = x.AddHours(1)) samples.Add(x);
                    var ctxs = samples.Select(x => new SkyContext(site, x)).ToList();
                    var moons = ctxs.Select(c => c.BodyPlace(Body.Moon)).ToList();
                    var ins = AppState.Instrument; var eye = AppState.Eye;
                    var dsos = CatalogManager.Instance.DeepSkyList.Where(o => !double.IsNaN(o.Mag) && o.Mag < 11.5).ToList();
                    var scored = new List<Tuple<double, DeepSkyObject, AstroTime, double, Detectability>>();
                    foreach (var o in dsos)
                    {
                        double best = double.MinValue, bestAlt = 0; AstroTime bestT = start; Detectability rating = Detectability.NotVisible;
                        double size = double.IsNaN(o.MajorAxis) ? 1 : (double.IsNaN(o.MinorAxis) ? o.MajorAxis : Math.Sqrt(o.MajorAxis * o.MinorAxis));
                        for (int i = 0; i < ctxs.Count; i++)
                        {
                            var hz = ctxs[i].IcrsToHorizontal(o.Ra, o.Dec);
                            if (hz.AltDeg < 20 || hz.AltDeg < site.HorizonAltitude(hz.AzDeg)) continue;
                            double sep = AstroMath.Separation(o.Ra, o.Dec, moons[i].Astrometric.Ra, moons[i].Astrometric.Dec);
                            double sunAlt = AstroEvents.SunAltitudeFast(site, samples[i]);
                            double sky = VisibilityModel.SkyBrightness(site, hz.Alt, sunAlt, moons[i].Horizontal.Alt, moons[i].PhaseAngle * AstroMath.Rad2Deg, sep);
                            var v = VisibilityModel.Predict(o.Mag, size, hz.Alt, sky, site, ins, eye);
                            if (v.BestMargin > best) { best = v.BestMargin; bestT = samples[i]; bestAlt = hz.AltDeg; rating = v.Rating; }
                        }
                        if (best > -0.5) scored.Add(Tuple.Create(best, o, bestT, bestAlt, rating));
                    }
                    foreach (var s in scored.OrderByDescending(x => x.Item1 + (x.Item2.Messier != null ? 0.3 : 0)).Take(40))
                    {
                        var o = s.Item2;
                        d.Targets.Add(new object[] { SelectedObject.From(o), o.DisplayName, DeepSkyObject.TypeLabel(o.Type), o.Constellation, double.IsNaN(o.Mag) ? "" : o.Mag.ToString("0.0"),
                            double.IsNaN(o.MajorAxis) ? "" : AstroMath.FormatAngleAuto(o.MajorAxis / 60), hm(s.Item3), s.Item4.ToString("0") + "°", s.Item5.ToString(), s.Item1.ToString("+0.0") });
                    }
                }
                Tasks.Report(t, 0.7);
                if (full)
                {
                    double j1 = now.JdTT, j2 = j1 + 45;
                    var ev = new List<AstroEvent>();
                    ev.AddRange(AstroEvents.MoonPhases(j1, j2));
                    foreach (var b in new[] { Body.Mercury, Body.Venus, Body.Mars, Body.Jupiter, Body.Saturn }) ev.AddRange(AstroEvents.PlanetEvents(b, j1, j2));
                    ev.AddRange(AstroEvents.LunarEclipses(j1, j1 + 400).Take(2));
                    ev.AddRange(AstroEvents.SolarEclipses(j1, j1 + 400).Take(2));
                    ev.AddRange(AstroEvents.MoonDistanceExtremes(j1, j2));
                    int year = now.ToDateTimeUtc().Year;
                    foreach (var ms in MeteorShowers.Major)
                        foreach (var yr in new[] { year, year + 1 })
                        {
                            var pk = ms.PeakIn(yr);
                            if (pk.JdTT >= j1 && pk.JdTT <= j1 + 90) ev.Add(new AstroEvent { Time = pk, Kind = EventKind.MeteorShowerPeak, Description = string.Format("{0} meteor shower peak (ZHR {1})", ms.Name, ms.Zhr) });
                        }
                    foreach (var x in ev.OrderBy(x => x.Time.JdUtc).Take(30))
                        d.Events.Add(new object[] { null, site.ToLocal(x.Time.ToDateTimeUtc()).ToString("ddd dd MMM HH:mm"), x.Description });
                }
                Tasks.Report(t, 0.85);
                foreach (var mb in MinorBodyStore.Instance.All)
                {
                    try
                    {
                        var pl = MinorBodyStore.Place(ctx, mb);
                        if (double.IsNaN(pl.Magnitude) || pl.Magnitude > 12) continue;
                        var so = new SelectedObject { Kind = SelectedKind.MinorBody, Minor = mb, Name = mb.DisplayName, RaDeg = pl.Astrometric.RaHours * 15, DecDeg = pl.Astrometric.DecDeg, Mag = pl.Magnitude };
                        var con = CatalogManager.Instance.ConstellationAt(pl.Astrometric.Ra, pl.Astrometric.Dec);
                        d.Minor.Add(new object[] { so, mb.DisplayName, pl.Magnitude.ToString("0.0"), con != null ? con.Abbr : "", pl.Elongation * AstroMath.Rad2Deg > 0 ? (pl.Elongation * AstroMath.Rad2Deg).ToString("0") + "°" : "", pl.Horizontal.AltDeg.ToString("0") + "°", pl.Distance.ToString("0.00") + " AU" });
                    }
                    catch { }
                }
                d.Minor = d.Minor.OrderBy(r => double.Parse((string)r[2], System.Globalization.CultureInfo.InvariantCulture)).Take(20).ToList();
                t.Status = "done";
                MainForm.Instance.Post(() => Show(d, full));
            });
        }

        private void Show(NightData d, bool full)
        {
            var site = AppState.Site;
            Func<AstroTime?, string> hm = x => x.HasValue ? site.ToLocal(x.Value.ToDateTimeUtc()).ToString("HH:mm") : "—";
            var tw = d.Tw;
            _timeline.Set(tw, d.Noon, site);
            _nightText.Text = string.Format(
                "Sunset {0}   ·   civil {1}   ·   nautical {2}   ·   astronomical {3}\r\n" +
                "Astronomical dawn {4}   ·   sunrise {5}\r\n\r\n" +
                "Astronomical darkness: {6}   ·   moonlight-free: {7}",
                hm(tw.SunSet), hm(tw.CivilDusk), hm(tw.NauticalDusk), hm(tw.AstronomicalDusk), hm(tw.AstronomicalDawn), hm(tw.SunRise),
                tw.DarkHours > 0 ? string.Format("{0:0.0} h", tw.DarkHours) : "none (twilight all night)", MoonFreeText(d));
            _moonGlyph.Set(d.Moon.IlluminatedFraction, AstroEvents.MoonSunElongation(d.JdTT) < 180);
            _moonText.Text = string.Format("{0}\r\n{1:0}% illuminated · {2:0.0} days old\r\nRise {3} · set {4}\r\nDistance {5:N0} km\r\nAltitude now {6:+0;-0}°",
                d.Phase, d.Moon.IlluminatedFraction * 100, d.MoonAge, hm(d.MoonRs.Rise), hm(d.MoonRs.Set), d.Moon.Distance * AstroMath.AU_km, d.Moon.Horizontal.AltDeg);
            Fill(_planetGrid, new[] { "Planet", "Rise", "Transit", "Set", "Mag", "Size", "Con", "Alt" }, d.Planets);
            Fill(_targetGrid, new[] { "Object", "Type", "Con", "Mag", "Size", "Best", "Alt", "Visibility", "Margin" }, d.Targets);
            if (full) Fill(_eventGrid, new[] { "When", "Event" }, d.Events);
            if (_eventGrid.Columns.Count > 1) { _eventGrid.Columns[0].FillWeight = 35; }
            Fill(_minorGrid, new[] { "Object", "Mag", "Con", "Elong.", "Alt", "Δ" }, d.Minor);
            if (d.Targets.Count == 0) _targets.Subtitle = "no dark sky tonight, or no objects above 20°";
            _jupGlyph.Set(GalileanMoons.Positions(d.JdTT));
            Theme.Apply(this);
        }

        private string MoonFreeText(NightData d)
        {
            if (!d.Tw.AstronomicalDusk.HasValue || !d.Tw.AstronomicalDawn.HasValue) return "—";
            double a = d.Tw.AstronomicalDusk.Value.JdUtc, b = d.Tw.AstronomicalDawn.Value.JdUtc, dark = 0;
            for (double t = a; t < b; t += 1.0 / 96)
                if (AstroEvents.BodyAltitude(Body.Moon, AppState.Site, new AstroTime(t)) < -0.5) dark += 0.25;
            return string.Format("{0:0.0} h", dark);
        }

        private static void Fill(DataGridView g, string[] cols, List<object[]> rows)
        {
            g.SuspendLayout();
            g.Rows.Clear();
            g.Columns.Clear();
            foreach (var c in cols) g.Columns.Add(c, c);
            foreach (var r in rows)
            {
                int i = g.Rows.Add(r.Skip(1).ToArray());
                g.Rows[i].Tag = r[0];
            }
            if (g.Columns.Count > 0) g.Columns[0].FillWeight = 160;
            g.ResumeLayout();
        }
    }

    /// <summary>Horizontal bar visualising day, twilights and night (noon to noon).</summary>
    public class NightTimeline : Control, IThemed
    {
        private TwilightTimes _tw;
        private AstroTime _noon;
        private Observer _site;
        public NightTimeline() { SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true); }
        public void ApplyTheme(Palette p) => Invalidate();
        public void Set(TwilightTimes tw, AstroTime noon, Observer site) { _tw = tw; _noon = noon; _site = site; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var p = Theme.Current;
            var g = e.Graphics;
            g.Clear(Parent?.BackColor ?? p.Surface);
            if (_tw == null) return;
            var r = new Rectangle(4, 6, Width - 8, 26);
            Func<double, float> X = jd => (float)(r.Left + (jd - _noon.JdUtc) * r.Width);
            // sample the sun altitude along the day for a smooth gradient
            for (int i = 0; i < r.Width; i++)
            {
                double jd = _noon.JdUtc + (double)i / r.Width;
                double alt = AstroEvents.SunAltitudeFast(_site, new AstroTime(jd));
                double t = AstroMath.Clamp((alt + 18) / 18, 0, 1);
                var c = alt > 0 ? Theme.Blend(p.SkyDay, p.Warning, 0.15) : Theme.Blend(p.SkyNight, p.SkyDay, t * t);
                using (var pen = new Pen(c)) g.DrawLine(pen, r.Left + i, r.Top, r.Left + i, r.Bottom);
            }
            using (var pen = new Pen(p.Border)) g.DrawRectangle(pen, r);
            // hour ticks
            for (int h = 0; h <= 24; h += 2)
            {
                float x = r.Left + h / 24f * r.Width;
                var lt = _site.ToLocal(_noon.AddHours(h).ToDateTimeUtc());
                TextRenderer.DrawText(g, lt.ToString("HH"), Theme.SmallFont, new Point((int)x - 7, r.Bottom + 4), p.TextDim);
            }
            var now = AppState.Now.JdUtc;
            if (now > _noon.JdUtc && now < _noon.JdUtc + 1)
                using (var pen = new Pen(p.Danger, 2)) g.DrawLine(pen, X(now), r.Top - 4, X(now), r.Bottom + 2);
        }
    }

    /// <summary>Moon phase icon.</summary>
    public class MoonGlyph : Control, IThemed
    {
        private double _k = 0.5;
        private bool _waxing = true;
        public MoonGlyph() { SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true); }
        public void ApplyTheme(Palette p) => Invalidate();
        public void Set(double k, bool waxing) { _k = k; _waxing = waxing; Invalidate(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var p = Theme.Current;
            var g = e.Graphics;
            g.Clear(Parent?.BackColor ?? p.Surface);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float r = Math.Min(Width, Height) / 2f - 6;
            float cx = Width / 2f, cy = Height / 2f;
            using (var b = new SolidBrush(Theme.Blend(p.Surface, p.Moon, 0.12))) g.FillEllipse(b, cx - r, cy - r, 2 * r, 2 * r);
            using (var path = new GraphicsPath())
            {
                // northern-hemisphere view: waxing lit on the right
                float e2 = (float)(r * Math.Abs(2 * _k - 1));
                if (_waxing) path.AddArc(cx - r, cy - r, 2 * r, 2 * r, -90, 180); else path.AddArc(cx - r, cy - r, 2 * r, 2 * r, 90, 180);
                if (_waxing) path.AddArc(cx - e2, cy - r, Math.Max(0.1f, 2 * e2), 2 * r, 90, _k >= 0.5 ? 180 : -180);
                else path.AddArc(cx - e2, cy - r, Math.Max(0.1f, 2 * e2), 2 * r, -90, _k >= 0.5 ? 180 : -180);
                path.CloseFigure();
                using (var b = new SolidBrush(p.Moon)) g.FillPath(b, path);
            }
        }
    }

    /// <summary>Galilean satellites diagram (east left, north up).</summary>
    public class JupiterMoonsGlyph : Control, IThemed
    {
        private GalileanMoon[] _m;
        public JupiterMoonsGlyph() { SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true); }
        public void ApplyTheme(Palette p) => Invalidate();
        public void Set(GalileanMoon[] m) { _m = m; Invalidate(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var p = Theme.Current;
            var g = e.Graphics;
            g.Clear(Parent?.BackColor ?? p.Surface);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (_m == null) return;
            float cx = Width / 2f, cy = Height / 2f - 8;
            float scale = (Width / 2f - 20) / 28f;
            using (var b = new SolidBrush(p.Planet)) g.FillEllipse(b, cx - scale, cy - scale * 0.935f, 2 * scale, 2 * scale * 0.935f);
            TextRenderer.DrawText(g, "E", Theme.SmallFont, new Point(4, (int)cy - 6), p.TextDim);
            TextRenderer.DrawText(g, "W", Theme.SmallFont, new Point(Width - 16, (int)cy - 6), p.TextDim);
            for (int i = 0; i < _m.Length; i++)
            {
                var m = _m[i];
                float x = cx + (float)m.X * scale, y = cy - (float)m.Y * scale;
                if (m.Occulted) continue;
                using (var b = new SolidBrush(m.Transit ? p.TextDim : p.Text)) g.FillEllipse(b, x - 3, y - 3, 6, 6);
                TextRenderer.DrawText(g, m.Name, Theme.SmallFont, new Point((int)x - 14, (int)y + (i % 2 == 0 ? 8 : -22)), p.TextDim);
            }
            string ev = string.Join(", ", _m.Where(m => m.Transit || m.Occulted).Select(m => m.Name + (m.Transit ? " in transit" : " hidden")));
            if (ev.Length > 0) TextRenderer.DrawText(g, ev, Theme.SmallFont, new Point(6, Height - 18), p.Warning);
        }
    }
}
