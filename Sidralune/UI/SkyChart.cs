using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Sidralune.Core;
using Sidralune.Core.Catalogs;
using Sidralune.Core.Coordinates;
using Sidralune.Core.Ephemeris;
using Sidralune.Core.Mathematics;
using Sidralune.Core.MinorBodies;
using Sidralune.Core.Time;
using Sidralune.Devices;

namespace Sidralune.UI
{
    public enum ChartMode { Planetarium, Atlas }

    public class ChartLayers
    {
        public bool Stars = true, StarNames = true, Bayer = false, DeepSky = true, DeepSkyLabels = true, Planets = true, MinorBodies = true;
        public bool ConstellationLines = true, ConstellationNames = true, Boundaries = false, EquatorialGrid = false, AltAzGrid = true, Ecliptic = true;
        public bool GalacticEquator = false, MilkyWay = true, Horizon = true, Cardinals = true, Atmosphere = true, Telescope = true, FovIndicators = true, Telrad = false, Meridian = false;
        public double MagLimit = 6.5;
        public bool AutoMagLimit = true;
        public double DsoMagLimit = 12;
    }

    public class ChartFov
    {
        public string Name;
        public double WidthDeg, HeightDeg; // circle when HeightDeg <= 0
        public double RotationDeg;
        public Color? Color;
        /// <summary>
        /// Optional outline fixed on the sky ([RA°, Dec°] ICRS points, e.g. the border of a plate-solved image). When set,
        /// the outline is drawn where it lies on the sky instead of as an eyepiece/camera frame at the chart centre.
        /// </summary>
        public List<double[]> Outline;
    }

    /// <summary>Interactive sky chart / planetarium renderer.</summary>
    public class SkyChart : Control, IThemed
    {
        public ChartMode Mode = ChartMode.Planetarium;
        public ChartLayers Layers = new ChartLayers();
        public readonly List<ChartFov> Fovs = new List<ChartFov>();
        /// <summary>Centre in frame coordinates (radians): lon = RA (atlas) or -Az (planetarium).</summary>
        public double CenterLon, CenterLat = 0.5;
        public double FovDeg = 100;
        public double RotationDeg;
        public bool FlipX, FlipY;
        public AstroTime? FixedTime;
        public Observer Site => AppState.Site;
        public event Action<SelectedObject> ObjectClicked;
        public event Action<double, double, MouseEventArgs> ContextRequested; // ra, dec J2000 deg
        public event Action ViewChanged;
        public string Caption;
        public List<double[]> MosaicTiles; // [ra, dec, w, h, rot] deg

        private class Hit { public PointF P; public float R; public SelectedObject O; public int Priority; }
        private readonly List<Hit> _hits = new List<Hit>();
        private Point _dragStart;
        private double _dragLon, _dragLat;
        private bool _dragging;
        private SkyContext _ctx;
        private Mat3 _toFrame;      // ICRS unit vector -> frame (horizon or equatorial)
        private Vec3 _c, _e1, _e2;
        private double _scale, _cx, _cy;
        private Palette _p;
        private bool _print;
        private Dictionary<MinorBody, BodyPlace> _minorCache = new Dictionary<MinorBody, BodyPlace>();
        private double _minorCacheJd = double.NaN;
        private bool _minorComputing;
        public SelectedObject Highlight;

        public SkyChart()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            TabStop = true;
            CenterLon = -Math.PI; // facing south
            CenterLat = 35 * AstroMath.Deg2Rad;
        }

        public void ApplyTheme(Palette p) => Invalidate();

        public AstroTime Time => FixedTime ?? AppState.Now;

        // ------------------------------------------------------------------ projection

        private void Setup(int w, int h)
        {
            _ctx = new SkyContext(Site, Time) { ApplyRefraction = AppState.Settings.UseRefraction };
            if (Mode == ChartMode.Planetarium)
            {
                // ICRS -> true of date (with nutation, without aberration for speed) -> local horizon with lon = -Az
                var m = Mat3.RotY(AstroMath.HalfPi - Site.LatRad) * Mat3.RotZ(_ctx.Last) * _ctx.Npb;
                // that gives (x towards south? ) -- convert explicitly below in ToFrame
                _toFrame = m;
            }
            else _toFrame = Mat3.Identity;
            double lon0 = CenterLon, lat0 = CenterLat;
            _c = Vec3.FromSpherical(lon0, lat0);
            _e1 = new Vec3(-Math.Sin(lon0), Math.Cos(lon0), 0);
            _e2 = new Vec3(-Math.Sin(lat0) * Math.Cos(lon0), -Math.Sin(lat0) * Math.Sin(lon0), Math.Cos(lat0));
            _scale = w / (4 * Math.Tan(FovDeg * AstroMath.Deg2Rad / 4));
            _cx = w / 2.0; _cy = h / 2.0;
        }

        /// <summary>ICRS unit vector -> frame vector (equatorial J2000, or horizon with lon = -Az, refraction applied).</summary>
        private Vec3 ToFrame(Vec3 icrs)
        {
            if (Mode == ChartMode.Atlas) return icrs;
            // horizontal: compute via true-of-date equatorial and hour angle (fast path)
            var tod = _ctx.Npb * icrs;
            double ra, dec;
            tod.ToSpherical(out ra, out dec);
            var hz = Transforms.HaDecToAltAz(_ctx.Last - ra, dec, Site.LatRad);
            double alt = hz.Alt;
            if (_ctx.ApplyRefraction && alt > -0.035) alt += Transforms.RefractionFromTrue(alt, Site.PressureHpa, Site.TemperatureC);
            return Vec3.FromSpherical(-hz.Az, alt);
        }

        private Vec3 HorizontalToFrame(double azRad, double altRad) => Vec3.FromSpherical(-azRad, altRad);

        private bool Project(Vec3 f, out PointF pt)
        {
            double d = Vec3.Dot(f, _c);
            pt = PointF.Empty;
            if (d < -0.5) return false;
            double k = 2 / (1 + d);
            double X = k * Vec3.Dot(f, _e1), Y = k * Vec3.Dot(f, _e2);
            double sx = -X * _scale, sy = -Y * _scale;
            if (FlipX) sx = -sx;
            if (FlipY) sy = -sy;
            if (RotationDeg != 0)
            {
                double r = RotationDeg * AstroMath.Deg2Rad, c = Math.Cos(r), s = Math.Sin(r);
                double t = sx * c - sy * s; sy = sx * s + sy * c; sx = t;
            }
            pt = new PointF((float)(_cx + sx), (float)(_cy + sy));
            return pt.X > -2000 && pt.X < Width + 2000 && pt.Y > -2000 && pt.Y < Height + 2000;
        }

        private bool ProjectIcrs(double ra, double dec, out PointF pt) => Project(ToFrame(Vec3.FromSpherical(ra, dec)), out pt);

        private Vec3 Unproject(double sx, double sy)
        {
            sx -= _cx; sy -= _cy;
            if (RotationDeg != 0)
            {
                double r = -RotationDeg * AstroMath.Deg2Rad, c = Math.Cos(r), s = Math.Sin(r);
                double t = sx * c - sy * s; sy = sx * s + sy * c; sx = t;
            }
            if (FlipX) sx = -sx;
            if (FlipY) sy = -sy;
            double X = -sx / _scale, Y = -sy / _scale;
            double rho2 = X * X + Y * Y;
            var v = (_c * (4 - rho2) + _e1 * (4 * X) + _e2 * (4 * Y)) / (4 + rho2);
            return v.Normalized();
        }

        /// <summary>Screen point to ICRS RA/Dec (radians).</summary>
        public bool ScreenToIcrs(Point p, out double ra, out double dec)
        {
            Setup(Width, Height);
            var f = Unproject(p.X, p.Y);
            double lon, lat;
            f.ToSpherical(out lon, out lat);
            if (Mode == ChartMode.Atlas) { ra = lon; dec = lat; return true; }
            var eq = _ctx.HorizontalToIcrs(-lon, lat);
            ra = eq.Ra; dec = eq.Dec;
            return true;
        }

        /// <summary>Centre the chart on ICRS coordinates (radians), optionally setting the field.</summary>
        public void CenterOn(double ra, double dec, double fovDeg = double.NaN)
        {
            Setup(Width, Height);
            var f = ToFrame(Vec3.FromSpherical(ra, dec));
            double lon, lat;
            f.ToSpherical(out lon, out lat);
            CenterLon = lon; CenterLat = lat;
            if (!double.IsNaN(fovDeg)) FovDeg = fovDeg;
            Invalidate();
            ViewChanged?.Invoke();
        }

        public void LookAt(double azDeg, double altDeg) { CenterLon = -azDeg * AstroMath.Deg2Rad; CenterLat = altDeg * AstroMath.Deg2Rad; Invalidate(); ViewChanged?.Invoke(); }

        public double EffectiveMagLimit
        {
            get
            {
                if (!Layers.AutoMagLimit) return Layers.MagLimit;
                // more stars when zoomed in (≈ +2.5 mag per 10x zoom), capped by the catalog
                double m = Layers.MagLimit + 2.2 * Math.Log10(Math.Max(1, 90 / FovDeg));
                return Math.Min(m, CatalogManager.Instance.StarLimitingMagnitude + 0.5);
            }
        }

        // ------------------------------------------------------------------ rendering

        protected override void OnPaint(PaintEventArgs e)
        {
            Render(e.Graphics, Width, Height, Theme.Current, false);
            if (Focused && ShowFocusCues) using (var pen = new Pen(Theme.Current.Focus)) e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }

        public void Render(Graphics g, int w, int h, Palette p, bool print)
        {
            _p = p; _print = print;
            Setup(w, h);
            _hits.Clear();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            var sun = _ctx.BodyPlace(Body.Sun);
            double sunAlt = sun.Horizontal.AltDeg;
            Color sky = p.SkyNight;
            if (!print && Mode == ChartMode.Planetarium && Layers.Atmosphere)
            {
                double t = AstroMath.Clamp((sunAlt + 18) / 24, 0, 1);
                sky = Theme.Blend(p.SkyNight, p.SkyDay, t * t);
            }
            g.Clear(print ? Color.White : (Mode == ChartMode.Atlas ? p.ChartBackground : sky));
            if (Layers.MilkyWay && !print && (Mode == ChartMode.Atlas || sunAlt < -10)) DrawMilkyWay(g, Mode == ChartMode.Atlas ? 1.0 : AstroMath.Clamp((-sunAlt - 10) / 8, 0, 1));
            if (Layers.EquatorialGrid) DrawEquatorialGrid(g);
            if (Layers.AltAzGrid && Mode == ChartMode.Planetarium) DrawAltAzGrid(g);
            if (Layers.Ecliptic) DrawGreatCircle(g, v => Transforms.EclipticToEquatorial(v, Precession.ObliquityJ2000), p.Ecliptic, "Ecliptic");
            if (Layers.GalacticEquator) DrawGreatCircle(g, v => { double ra, dec, l, b; v.ToSpherical(out l, out b); Transforms.GalacticToEquatorial(l, b, out ra, out dec); return Vec3.FromSpherical(ra, dec); }, p.GalacticEquator, "Galactic eq.");
            if (Layers.Meridian && Mode == ChartMode.Planetarium) DrawMeridian(g);
            if (Layers.Boundaries) DrawBoundaries(g);
            if (Layers.ConstellationLines) DrawConstellationLines(g);
            if (Layers.DeepSky) DrawDeepSky(g);
            if (Layers.Stars) DrawStars(g, sunAlt);
            if (Layers.ConstellationNames) DrawConstellationNames(g);
            if (Layers.MinorBodies) DrawMinorBodies(g);
            if (Layers.Planets) DrawSolarSystem(g);
            if (MosaicTiles != null) DrawMosaic(g);
            if (Layers.FovIndicators) DrawFovs(g);
            if (Layers.Telescope) DrawTelescope(g);
            if (Highlight != null) DrawHighlight(g);
            if (Mode == ChartMode.Planetarium && Layers.Horizon) DrawHorizon(g, sky);
            DrawInfo(g, w, h);
        }

        private void DrawMilkyWay(Graphics g, double strength)
        {
            // soft band along the galactic plane. The plane is a closed ring: draw it as one closed figure when all of it
            // projects, otherwise start each visible stretch just after a hidden point so no seam (an open end at the
            // starting longitude, which used to leave a gap at the anticentre in Auriga) falls in view.
            const int n = 180; // 2° steps in galactic longitude
            var ring = new PointF[n];
            var ok = new bool[n];
            int firstHidden = -1;
            for (int i = 0; i < n; i++)
            {
                double ra, dec;
                Transforms.GalacticToEquatorial((-180 + 2.0 * i) * AstroMath.Deg2Rad, 0, out ra, out dec);
                ok[i] = ProjectIcrs(ra, dec, out ring[i]);
                if (!ok[i] && firstHidden < 0) firstHidden = i;
            }
            for (int pass = 0; pass < 3; pass++)
            {
                float width = (float)(_scale * (pass == 0 ? 0.30 : pass == 1 ? 0.18 : 0.08));
                using (var pen = new Pen(Theme.WithAlpha(_p.MilkyWay, (int)((pass == 0 ? 50 : pass == 1 ? 70 : 90) * strength)), width) { LineJoin = LineJoin.Round, StartCap = LineCap.Flat, EndCap = LineCap.Flat })
                {
                    if (firstHidden < 0) { g.DrawPolygon(pen, ring); continue; }
                    var pts = new List<PointF>();
                    for (int k = 1; k <= n; k++)
                    {
                        int i = (firstHidden + k) % n;
                        if (ok[i]) pts.Add(ring[i]);
                        else { if (pts.Count > 1) g.DrawLines(pen, pts.ToArray()); pts.Clear(); }
                    }
                    if (pts.Count > 1) g.DrawLines(pen, pts.ToArray());
                }
            }
        }

        private void DrawPolyline(Graphics g, Pen pen, IEnumerable<Vec3> frameVectors)
        {
            var pts = new List<PointF>();
            PointF prev = PointF.Empty; bool hasPrev = false;
            foreach (var v in frameVectors)
            {
                PointF pt;
                bool ok = Project(v, out pt);
                if (ok && hasPrev && Math.Abs(pt.X - prev.X) + Math.Abs(pt.Y - prev.Y) > Width) ok = false;
                if (ok) { pts.Add(pt); prev = pt; hasPrev = true; }
                else { if (pts.Count > 1) g.DrawLines(pen, pts.ToArray()); pts.Clear(); hasPrev = false; }
            }
            if (pts.Count > 1) g.DrawLines(pen, pts.ToArray());
        }

        private void DrawEquatorialGrid(Graphics g)
        {
            using (var pen = new Pen(_p.GridEquatorial, 1f))
            {
                double stepDec = FovDeg > 60 ? 15 : FovDeg > 20 ? 10 : FovDeg > 6 ? 2 : FovDeg > 2 ? 1 : 0.25;
                double stepRa = FovDeg > 60 ? 15 : FovDeg > 20 ? 7.5 : FovDeg > 6 ? 2.5 : FovDeg > 2 ? 1.25 : 0.25;
                for (double dec = -90 + stepDec; dec < 90; dec += stepDec)
                    DrawPolyline(g, pen, Enumerable.Range(0, 361).Select(i => ToFrame(Vec3.FromSpherical(i * AstroMath.Deg2Rad, dec * AstroMath.Deg2Rad))));
                for (double ra = 0; ra < 360; ra += stepRa)
                    DrawPolyline(g, pen, Enumerable.Range(-89, 179).Select(i => ToFrame(Vec3.FromSpherical(ra * AstroMath.Deg2Rad, i * AstroMath.Deg2Rad))));
                // labels along the centre lines
                for (double ra = 0; ra < 360; ra += stepRa)
                {
                    double cra, cdec;
                    var centre = Mode == ChartMode.Atlas ? _c : Vec3.Zero;
                    PointF pt;
                    double labelDec = Mode == ChartMode.Atlas ? CenterLat : 0;
                    if (ProjectIcrs(ra * AstroMath.Deg2Rad, labelDec, out pt) && InView(pt))
                        TextRenderer.DrawText(g, AstroMath.FormatHms(ra / 15, 0, true).Substring(0, 5), Theme.SmallFont, Point.Round(pt), _p.GridEquatorial);
                }
            }
        }

        private void DrawAltAzGrid(Graphics g)
        {
            using (var pen = new Pen(_p.GridAltAz, 1f))
            {
                for (int alt = 15; alt < 90; alt += 15)
                    DrawPolyline(g, pen, Enumerable.Range(0, 361).Select(i => HorizontalToFrame(i * AstroMath.Deg2Rad, alt * AstroMath.Deg2Rad)));
                for (int az = 0; az < 360; az += 30)
                    DrawPolyline(g, pen, Enumerable.Range(0, 90).Select(i => HorizontalToFrame(az * AstroMath.Deg2Rad, i * AstroMath.Deg2Rad)));
            }
        }

        private void DrawMeridian(Graphics g)
        {
            using (var pen = new Pen(Theme.WithAlpha(_p.Warning, 140), 1f) { DashStyle = DashStyle.Dash })
                DrawPolyline(g, pen, Enumerable.Range(-90, 181).Select(i => HorizontalToFrame(i >= 0 ? 0 : Math.PI, Math.Abs(i) * AstroMath.Deg2Rad)));
        }

        private void DrawGreatCircle(Graphics g, Func<Vec3, Vec3> toIcrs, Color c, string label)
        {
            using (var pen = new Pen(c, 1.2f) { DashStyle = DashStyle.Dash })
                DrawPolyline(g, pen, Enumerable.Range(0, 361).Select(i => ToFrame(toIcrs(Vec3.FromSpherical(i * AstroMath.Deg2Rad, 0)))));
        }

        private void DrawBoundaries(Graphics g)
        {
            using (var pen = new Pen(_p.ConstellationBoundary, 1f) { DashStyle = DashStyle.Dot })
            {
                var cm = CatalogManager.Instance;
                foreach (var seg in cm.BoundarySegments)
                    DrawPolyline(g, pen, Densify(seg));
            }
        }

        private IEnumerable<Vec3> Densify(List<double[]> seg)
        {
            for (int i = 0; i < seg.Count; i++)
            {
                var a = Vec3.FromSpherical(seg[i][0], seg[i][1]);
                yield return ToFrame(a);
                if (i + 1 < seg.Count)
                {
                    var b = Vec3.FromSpherical(seg[i + 1][0], seg[i + 1][1]);
                    double ang = Vec3.AngleBetween(a, b);
                    int n = (int)(ang / (2 * AstroMath.Deg2Rad));
                    for (int k = 1; k < n; k++) yield return ToFrame((a * (n - k) + b * k).Normalized());
                }
            }
        }

        private void DrawConstellationLines(Graphics g)
        {
            using (var pen = new Pen(_p.ConstellationLine, FovDeg < 30 ? 1.4f : 1.1f))
                foreach (var c in CatalogManager.Instance.Constellations)
                    foreach (var l in c.Lines)
                    {
                        PointF a, b;
                        if (ProjectIcrs(l[0], l[1], out a) && ProjectIcrs(l[2], l[3], out b) && (InView(a) || InView(b)) && Math.Abs(a.X - b.X) < Width && Math.Abs(a.Y - b.Y) < Height)
                            g.DrawLine(pen, a, b);
                    }
        }

        private void DrawConstellationNames(Graphics g)
        {
            var font = FovDeg > 90 ? Theme.SmallFont : Theme.UiFont;
            foreach (var c in CatalogManager.Instance.Constellations)
            {
                PointF pt;
                if (!ProjectIcrs(c.LabelRa, c.LabelDec, out pt) || !InView(pt)) continue;
                string txt = FovDeg > 120 ? c.Abbr.ToUpperInvariant() : c.Name.ToUpperInvariant();
                var sz = TextRenderer.MeasureText(txt, font);
                TextRenderer.DrawText(g, txt, font, new Point((int)pt.X - sz.Width / 2, (int)pt.Y - sz.Height / 2), _p.ConstellationName);
            }
        }

        /// <summary>True when the screen point lies below the local horizon (including the site's horizon profile).</summary>
        private bool IsGround(double sx, double sy, double[] profileRad)
        {
            var f = Unproject(sx, sy);
            double lon, lat;
            f.ToSpherical(out lon, out lat);
            double az = AstroMath.NormalizeDeg(-lon * AstroMath.Rad2Deg);
            int i = Math.Min(359, (int)az);
            double horizon = profileRad[i] + (profileRad[i + 1] - profileRad[i]) * (az - i);
            return lat < horizon;
        }

        private bool InView(PointF p) => InView(p, 20);
        // a point this far outside the chart may still show: a disc, rings, a label or moons around it
        private bool InView(PointF p, float margin) => p.X >= -margin && p.Y >= -margin && p.X <= Width + margin && p.Y <= Height + margin;

        private double ViewRadius()
        {
            double diag = Math.Sqrt(Width * Width + Height * Height) / 2;
            return Math.Min(Math.PI, 4 * Math.Atan(diag / (2 * _scale)) * 1.05);
        }

        private Vec3 CenterIcrs()
        {
            if (Mode == ChartMode.Atlas) return _c;
            double lon, lat;
            _c.ToSpherical(out lon, out lat);
            var eq = _ctx.HorizontalToIcrs(-lon, lat);
            return Vec3.FromSpherical(eq.Ra, eq.Dec);
        }

        private void DrawStars(Graphics g, double sunAlt)
        {
            double lim = EffectiveMagLimit;
            if (Mode == ChartMode.Planetarium && Layers.Atmosphere && !_print)
            {
                // daylight and twilight wash out faint stars
                if (sunAlt > -0.8) lim = Math.Min(lim, -1.5);
                else if (sunAlt > -6) lim = Math.Min(lim, 1.5);
                else if (sunAlt > -12) lim = Math.Min(lim, 4.0);
                else if (sunAlt > -15) lim = Math.Min(lim, 5.5);
            }
            var stars = new List<Star>();
            var ci = CenterIcrs();
            double cra, cdec;
            ci.ToSpherical(out cra, out cdec);
            CatalogManager.Instance.Stars.Cone(cra, cdec, ViewRadius(), lim, stars);
            var labels = new List<RectangleF>();
            int labelled = 0;
            foreach (var s in stars.OrderBy(s => s.Mag))
            {
                PointF pt;
                var f = ToFrame(Vec3.FromSpherical(s.Ra, s.Dec));
                if (Mode == ChartMode.Planetarium && Layers.Horizon && f.Z < -0.01) continue;
                if (!Project(f, out pt) || !InView(pt)) continue;
                double m = s.Mag;
                float r = (float)Math.Max(0.7, 0.55 * Math.Pow(Math.Max(0.1, lim + 1.2 - m), 0.95));
                if (FovDeg < 5) r *= 1.2f;
                var col = _print ? Color.Black : _p.StarColor(s.BV);
                // atmospheric extinction dimming near the horizon (planetarium)
                int alpha = 255;
                if (Mode == ChartMode.Planetarium && !_print && f.Z < 0.15) alpha = (int)(255 * AstroMath.Clamp(0.35 + f.Z * 4, 0.3, 1));
                using (var b = new SolidBrush(Color.FromArgb(alpha, col)))
                {
                    if (r > 2.2 && !_print)
                        using (var glow = new SolidBrush(Color.FromArgb(alpha / 6, col))) g.FillEllipse(glow, pt.X - r * 1.8f, pt.Y - r * 1.8f, r * 3.6f, r * 3.6f);
                    g.FillEllipse(b, pt.X - r, pt.Y - r, 2 * r, 2 * r);
                    if (_print) using (var wpen = new Pen(Color.White, 0.8f)) g.DrawEllipse(wpen, pt.X - r - 0.5f, pt.Y - r - 0.5f, 2 * r + 1, 2 * r + 1);
                }
                _hits.Add(new Hit { P = pt, R = Math.Max(5, r + 3), O = SelectedObject.From(s), Priority = 2 });
                if (Layers.StarNames && labelled < 80)
                {
                    string label = !string.IsNullOrEmpty(s.Name) ? s.Name : (Layers.Bayer && !string.IsNullOrEmpty(s.Bayer) ? s.Bayer : null);
                    if (label != null && (m < lim - 3.0 || FovDeg < 15))
                    {
                        var sz = TextRenderer.MeasureText(label, Theme.SmallFont);
                        var lr = new RectangleF(pt.X + r + 3, pt.Y - sz.Height / 2f, sz.Width, sz.Height);
                        if (!labels.Any(x => x.IntersectsWith(lr)))
                        {
                            labels.Add(lr);
                            TextRenderer.DrawText(g, label, Theme.SmallFont, Point.Round(lr.Location), _print ? Color.Black : Theme.Blend(col, _p.TextDim, 0.4));
                            labelled++;
                        }
                    }
                }
            }
        }

        private void DrawDeepSky(Graphics g)
        {
            var list = new List<DeepSkyObject>();
            var ci = CenterIcrs();
            double cra, cdec;
            ci.ToSpherical(out cra, out cdec);
            double mlim = Layers.DsoMagLimit + 2 * Math.Log10(Math.Max(1, 60 / FovDeg));
            CatalogManager.Instance.DeepSky.Cone(cra, cdec, ViewRadius(), 99, list);
            // PGC galaxies (when installed), zoomed in: those within the magnitude limit, brightest first, and those at least a
            // third of the field width in arcminutes across (some have no magnitude)
            int ngc = list.Count;
            if (FovDeg <= 15) CatalogManager.Instance.PgcCone(cra, cdec, ViewRadius(), mlim, FovDeg / 3, 1000, list);
            int drawn = 0;
            for (int k = 0; k < list.Count; k++)
            {
                var d = list[k];
                bool messier = d.Messier != null, pgc = k >= ngc;
                double mag = double.IsNaN(d.Mag) ? 14 : d.Mag;
                if (!messier && !pgc && mag > mlim) continue;
                if (!messier && !pgc && FovDeg > 60 && mag > 9) continue;
                if (drawn > 1500) break;
                PointF pt;
                var f = ToFrame(Vec3.FromSpherical(d.Ra, d.Dec));
                if (Mode == ChartMode.Planetarium && Layers.Horizon && f.Z < 0) continue;
                if (!Project(f, out pt) || !InView(pt)) continue;
                drawn++;
                double sizeDeg = double.IsNaN(d.MajorAxis) ? 0 : d.MajorAxis / 60;
                float rmaj = (float)Math.Max(4, sizeDeg * AstroMath.Deg2Rad / 2 * _scale * 0.5 * 2);
                float rmin = (float)(double.IsNaN(d.MinorAxis) || double.IsNaN(d.MajorAxis) ? rmaj : Math.Max(2.5, rmaj * d.MinorAxis / d.MajorAxis));
                Color c = TypeColor(d.Type);
                using (var pen = new Pen(c, 1.3f))
                {
                    var st = g.Save();
                    g.TranslateTransform(pt.X, pt.Y);
                    // align major axis with the position angle relative to local north on screen
                    double north = NorthAngle(d.Ra, d.Dec, pt);
                    double pa = double.IsNaN(d.PositionAngleDeg) ? 0 : d.PositionAngleDeg;
                    g.RotateTransform((float)(north + (FlipX ^ FlipY ? pa : -pa)));
                    switch (d.Type)
                    {
                        case ObjectType.Galaxy: case ObjectType.GalaxyPair: case ObjectType.GalaxyGroup: g.DrawEllipse(pen, -rmin, -rmaj, 2 * rmin, 2 * rmaj); break;
                        case ObjectType.OpenCluster: case ObjectType.Asterism: case ObjectType.StarCloud: pen.DashStyle = DashStyle.Dot; g.DrawEllipse(pen, -rmaj, -rmaj, 2 * rmaj, 2 * rmaj); break;
                        case ObjectType.GlobularCluster: g.DrawEllipse(pen, -rmaj, -rmaj, 2 * rmaj, 2 * rmaj); g.DrawLine(pen, -rmaj, 0, rmaj, 0); g.DrawLine(pen, 0, -rmaj, 0, rmaj); break;
                        case ObjectType.PlanetaryNebula:
                            g.DrawEllipse(pen, -rmaj * 0.6f, -rmaj * 0.6f, rmaj * 1.2f, rmaj * 1.2f);
                            g.DrawLine(pen, 0, -rmaj, 0, -rmaj * 0.6f); g.DrawLine(pen, 0, rmaj, 0, rmaj * 0.6f); g.DrawLine(pen, -rmaj, 0, -rmaj * 0.6f, 0); g.DrawLine(pen, rmaj, 0, rmaj * 0.6f, 0); break;
                        case ObjectType.DarkNebula: pen.DashStyle = DashStyle.Dash; g.DrawRectangle(pen, -rmin, -rmaj, 2 * rmin, 2 * rmaj); break;
                        default: g.DrawRectangle(pen, -rmin, -rmaj, 2 * rmin, 2 * rmaj); break;
                    }
                    g.Restore(st);
                }
                _hits.Add(new Hit { P = pt, R = Math.Max(7, rmaj), O = SelectedObject.From(d), Priority = 1 });
                if (Layers.DeepSkyLabels && (messier || FovDeg < 25) && (!pgc || FovDeg < 3))
                {
                    string label = messier ? d.Messier + (FovDeg < 20 && d.Name != null ? " " + d.Name : "") : (d.Name != null && FovDeg < 10 ? d.Id + " " + d.Name : d.Id);
                    TextRenderer.DrawText(g, label, Theme.SmallFont, new Point((int)(pt.X + rmaj * 0.7f + 2), (int)(pt.Y + rmaj * 0.7f)), c);
                }
            }
        }

        private Color TypeColor(ObjectType t)
        {
            switch (t)
            {
                case ObjectType.Galaxy: case ObjectType.GalaxyPair: case ObjectType.GalaxyGroup: return _p.Galaxy;
                case ObjectType.OpenCluster: case ObjectType.GlobularCluster: case ObjectType.Asterism: case ObjectType.StarCloud: return _p.Cluster;
                case ObjectType.PlanetaryNebula: return _p.PlanetaryNebula;
                default: return _p.Nebula;
            }
        }

        /// <summary>Screen angle (deg, clockwise from up) of celestial north at a position.</summary>
        private double NorthAngle(double ra, double dec, PointF pt)
        {
            PointF n;
            double d2 = Math.Min(dec + 0.2 * AstroMath.Deg2Rad, AstroMath.HalfPi - 1e-6);
            if (!ProjectIcrs(ra, d2, out n)) return 0;
            return Math.Atan2(n.X - pt.X, -(n.Y - pt.Y)) * AstroMath.Rad2Deg;
        }

        private void DrawSolarSystem(Graphics g)
        {
            var bodies = new[] { Body.Mercury, Body.Venus, Body.Mars, Body.Jupiter, Body.Saturn, Body.Uranus, Body.Neptune, Body.Pluto };
            var sunPlace = _ctx.BodyPlace(Body.Sun);
            foreach (var b in bodies)
            {
                var pl = _ctx.BodyPlace(b);
                PlanetPhysics.Fill(b, pl, _ctx.JdTT);
                if (b == Body.Pluto && FovDeg > 20) continue;
                PointF pt;
                var f = ToFrame(pl.Astrometric.ToVector().Normalized());
                if (Mode == ChartMode.Planetarium && Layers.Horizon && f.Z < 0) continue;
                if (!Project(f, out pt)) continue;
                // the apparent radius, or a dot sized by brightness while that is larger
                float r = (float)Math.Max(Math.Max(2.5, 1.2 * (4 - pl.Magnitude * 0.6)), pl.DiameterArcsec / 2 / 3600 * AstroMath.Deg2Rad * _scale);
                // drawn while any of it is in view, not only its middle: the disc, Saturn's rings, the label to the right
                // and, zoomed in, Jupiter's moons at their places in the sky
                Science.SolarSystem.GalileanMoon[] moons = null;
                PointF[] moonPts = null;
                float reach = (b == Body.Saturn ? 2.2f * r : r) + 120;
                if (b == Body.Jupiter && FovDeg < 1)
                {
                    moons = Science.SolarSystem.GalileanMoons.Positions(_ctx);
                    moonPts = new PointF[moons.Length];
                    for (int i = 0; i < moons.Length; i++)
                    {
                        if (!ProjectIcrs(moons[i].Ra, moons[i].Dec, out moonPts[i])) moonPts[i] = pt;
                        reach = Math.Max(reach, Math.Max(Math.Abs(moonPts[i].X - pt.X), Math.Abs(moonPts[i].Y - pt.Y)) + 40);
                    }
                }
                if (!InView(pt, reach)) continue;
                using (var br = new SolidBrush(_p.Planet)) g.FillEllipse(br, pt.X - r, pt.Y - r, 2 * r, 2 * r);
                if (b == Body.Saturn && r > 3) using (var pen = new Pen(_p.Planet, 1.2f)) g.DrawEllipse(pen, pt.X - r * 2.2f, pt.Y - r * 0.7f, r * 4.4f, r * 1.4f);
                TextRenderer.DrawText(g, b.ToString(), Theme.UiFontBold, new Point((int)(pt.X + r + 3), (int)(pt.Y - 8)), _p.Planet);
                _hits.Add(new Hit { P = pt, R = Math.Max(8, r + 2), O = new SelectedObject { Kind = SelectedKind.Planet, Name = b.ToString(), Planet = b, RaDeg = pl.Astrometric.RaHours * 15, DecDeg = pl.Astrometric.DecDeg, Mag = pl.Magnitude, SizeArcmin = pl.DiameterArcsec / 60 }, Priority = 0 });
                if (moons != null) DrawGalileans(g, moons, moonPts);
            }
            // Sun
            {
                PointF pt;
                var f = ToFrame(sunPlace.Astrometric.ToVector().Normalized());
                float r = (float)Math.Max(7, 0.267 * AstroMath.Deg2Rad * _scale);
                // the glow reaches twice the radius out; the label is to the right
                if (!(Mode == ChartMode.Planetarium && Layers.Horizon && f.Z < -0.01) && Project(f, out pt) && InView(pt, 2 * r + 60))
                {
                    using (var glow = new SolidBrush(Theme.WithAlpha(_p.Sun, 60))) g.FillEllipse(glow, pt.X - r * 2, pt.Y - r * 2, r * 4, r * 4);
                    using (var br = new SolidBrush(_p.Sun)) g.FillEllipse(br, pt.X - r, pt.Y - r, 2 * r, 2 * r);
                    TextRenderer.DrawText(g, "Sun", Theme.UiFontBold, new Point((int)(pt.X + r + 3), (int)pt.Y - 8), _p.Sun);
                    _hits.Add(new Hit { P = pt, R = r + 3, O = new SelectedObject { Kind = SelectedKind.Planet, Name = "Sun", Planet = Body.Sun, RaDeg = sunPlace.Astrometric.RaHours * 15, DecDeg = sunPlace.Astrometric.DecDeg, Mag = -26.7 } });
                }
            }
            // Moon with phase
            {
                var moon = _ctx.BodyPlace(Body.Moon);
                PointF pt;
                var f = ToFrame(moon.Astrometric.ToVector().Normalized());
                float r = (float)Math.Max(8, Math.Asin(1737.4 / (moon.Distance * AstroMath.AU_km)) * _scale);
                if (!(Mode == ChartMode.Planetarium && Layers.Horizon && f.Z < -0.01) && Project(f, out pt) && InView(pt, r + 60))
                {
                    double k = moon.IlluminatedFraction;
                    double limbPa = PlanetPhysics.BrightLimbAngle(moon.Apparent, sunPlace.Apparent) * AstroMath.Rad2Deg;
                    double north = NorthAngle(moon.Astrometric.Ra, moon.Astrometric.Dec, pt);
                    DrawMoonPhase(g, pt, r, k, north + (FlipX ^ FlipY ? limbPa : -limbPa));
                    TextRenderer.DrawText(g, "Moon", Theme.UiFontBold, new Point((int)(pt.X + r + 3), (int)pt.Y - 8), _p.Moon);
                    _hits.Add(new Hit { P = pt, R = r + 3, O = new SelectedObject { Kind = SelectedKind.Planet, Name = "Moon", Planet = Body.Moon, RaDeg = moon.Astrometric.RaHours * 15, DecDeg = moon.Astrometric.DecDeg } });
                }
            }
        }

        private void DrawMoonPhase(Graphics g, PointF c, float r, double k, double brightLimbScreenDeg)
        {
            var st = g.Save();
            g.TranslateTransform(c.X, c.Y);
            g.RotateTransform((float)brightLimbScreenDeg);
            using (var dark = new SolidBrush(Theme.Blend(_p.ChartBackground, _p.Moon, 0.12))) g.FillEllipse(dark, -r, -r, 2 * r, 2 * r);
            // bright limb points "up" (-y) after rotation; terminator is an ellipse with semi-minor r·|2k-1|
            using (var path = new GraphicsPath())
            {
                path.AddArc(-r, -r, 2 * r, 2 * r, 180, 180); // upper half (bright side)
                float e = (float)(r * Math.Abs(2 * k - 1));
                if (k >= 0.5) path.AddArc(-r, -e, 2 * r, Math.Max(0.01f, 2 * e), 0, 180);
                else path.AddArc(-r, -e, 2 * r, Math.Max(0.01f, 2 * e), 0, -180);
                path.CloseFigure();
                using (var br = new SolidBrush(_p.Moon)) g.FillPath(br, path);
            }
            g.Restore(st);
        }

        // the moons at their projected places, so they follow the chart's orientation, mirroring and rotation
        private void DrawGalileans(Graphics g, Science.SolarSystem.GalileanMoon[] moons, PointF[] pts)
        {
            for (int i = 0; i < moons.Length; i++)
            {
                var m = moons[i];
                float x = pts[i].X, y = pts[i].Y;
                if (m.Occulted) continue;
                using (var b = new SolidBrush(m.InFront && m.Transit ? _p.TextDim : _p.Text)) g.FillEllipse(b, x - 2, y - 2, 4, 4);
                TextRenderer.DrawText(g, m.Name.Substring(0, 2), Theme.SmallFont, new Point((int)x - 6, (int)y + 4), _p.TextDim);
                var o = SelectedObject.From(m);
                _hits.Add(new Hit { P = pts[i], R = 6, O = o, Priority = 0 });
                // a selected moon is marked where it is now, as it moves around Jupiter
                if (Highlight != null && Highlight.Kind == SelectedKind.PlanetMoon && Highlight.Name == m.Name) { Highlight.RaDeg = o.RaDeg; Highlight.DecDeg = o.DecDeg; }
            }
        }

        private void DrawMinorBodies(Graphics g)
        {
            var store = MinorBodyStore.Instance;
            double jd = Time.JdUtc;
            if ((double.IsNaN(_minorCacheJd) || Math.Abs(jd - _minorCacheJd) > 1.0 / 96) && !_minorComputing)
            {
                _minorComputing = true;
                var site = Site; var t = Time;
                var bodies = store.All.ToList();
                Task.Run(() =>
                {
                    var ctx = new SkyContext(site, t);
                    var cache = new Dictionary<MinorBody, BodyPlace>();
                    foreach (var b in bodies)
                    {
                        try
                        {
                            var pl = MinorBodyStore.Place(ctx, b);
                            if (!double.IsNaN(pl.Magnitude) && pl.Magnitude < 16) cache[b] = pl;
                        }
                        catch { }
                    }
                    BeginInvoke(new Action(() => { _minorCache = cache; _minorCacheJd = t.JdUtc; _minorComputing = false; Invalidate(); }));
                });
            }
            double lim = Math.Min(EffectiveMagLimit + 2, 15);
            foreach (var kv in _minorCache)
            {
                var pl = kv.Value;
                if (pl.Magnitude > lim) continue;
                PointF pt;
                var f = ToFrame(pl.Astrometric.ToVector().Normalized());
                if (Mode == ChartMode.Planetarium && Layers.Horizon && f.Z < 0) continue;
                if (!Project(f, out pt) || !InView(pt)) continue;
                var c = kv.Key.IsComet ? _p.Comet : _p.Asteroid;
                using (var pen = new Pen(c, 1.3f))
                {
                    if (kv.Key.IsComet)
                    {
                        // tail points away from the Sun
                        PointF sp;
                        var sun = _ctx.BodyPlace(Body.Sun);
                        double ang = ProjectIcrs(sun.Astrometric.Ra, sun.Astrometric.Dec, out sp) ? Math.Atan2(pt.Y - sp.Y, pt.X - sp.X) : 0;
                        g.DrawLine(pen, pt.X, pt.Y, pt.X + (float)(Math.Cos(ang) * 14), pt.Y + (float)(Math.Sin(ang) * 14));
                        using (var b = new SolidBrush(c)) g.FillEllipse(b, pt.X - 3, pt.Y - 3, 6, 6);
                    }
                    else { g.DrawLine(pen, pt.X - 4, pt.Y, pt.X + 4, pt.Y); g.DrawLine(pen, pt.X, pt.Y - 4, pt.X, pt.Y + 4); }
                }
                if (pl.Magnitude < lim - 1.5 || FovDeg < 10) TextRenderer.DrawText(g, kv.Key.DisplayName, Theme.SmallFont, new Point((int)pt.X + 6, (int)pt.Y + 2), c);
                _hits.Add(new Hit { P = pt, R = 7, O = new SelectedObject { Kind = SelectedKind.MinorBody, Name = kv.Key.DisplayName, Minor = kv.Key, RaDeg = pl.Astrometric.RaHours * 15, DecDeg = pl.Astrometric.DecDeg, Mag = pl.Magnitude }, Priority = 1 });
            }
        }

        private void DrawFovs(Graphics g)
        {
            var centre = new PointF((float)_cx, (float)_cy);
            foreach (var f in Fovs)
            {
                var col = f.Color ?? _p.FovOverlay;
                if (f.Outline != null) { DrawSkyOutline(g, f, col); continue; }
                using (var pen = new Pen(col, 1.4f))
                {
                    // half-width in pixels of an angular size under the stereographic scale
                    float pw = (float)(_scale * 2 * Math.Tan(f.WidthDeg * AstroMath.Deg2Rad / 4));
                    if (f.HeightDeg <= 0) g.DrawEllipse(pen, centre.X - pw, centre.Y - pw, 2 * pw, 2 * pw);
                    else
                    {
                        float ph = (float)(_scale * 2 * Math.Tan(f.HeightDeg * AstroMath.Deg2Rad / 4));
                        var st = g.Save();
                        g.TranslateTransform(centre.X, centre.Y);
                        g.RotateTransform((float)f.RotationDeg);
                        g.DrawRectangle(pen, -pw, -ph, 2 * pw, 2 * ph);
                        g.Restore(st);
                    }
                    TextRenderer.DrawText(g, f.Name, Theme.SmallFont, new Point((int)(centre.X + pw * 0.72f), (int)(centre.Y - pw * 0.72f - 14)), col);
                }
            }
            if (Layers.Telrad)
                using (var pen = new Pen(Theme.WithAlpha(_p.Danger, 200), 1.4f))
                    foreach (var deg in new[] { 0.5, 2.0, 4.0 })
                    {
                        float pr = (float)(_scale * 2 * Math.Tan(deg * AstroMath.Deg2Rad / 4));
                        g.DrawEllipse(pen, centre.X - pr, centre.Y - pr, 2 * pr, 2 * pr);
                    }
        }

        private void DrawSkyOutline(Graphics g, ChartFov f, Color col)
        {
            var pts = new List<PointF>(f.Outline.Count);
            foreach (var v in f.Outline)
            {
                PointF pt;
                if (!ProjectIcrs(v[0] * AstroMath.Deg2Rad, v[1] * AstroMath.Deg2Rad, out pt)) return; // partly behind the view
                pts.Add(pt);
            }
            if (pts.Count < 2) return;
            using (var pen = new Pen(col, 1.4f)) g.DrawPolygon(pen, pts.ToArray());
            // label at the top-right-most point of the outline
            var label = pts.OrderBy(p => p.Y - p.X).First();
            TextRenderer.DrawText(g, f.Name, Theme.SmallFont, new Point((int)label.X + 6, (int)label.Y - 14), col);
        }

        private void DrawMosaic(Graphics g)
        {
            using (var pen = new Pen(_p.Info, 1.3f))
                foreach (var t in MosaicTiles)
                {
                    PointF c;
                    if (!ProjectIcrs(t[0] * AstroMath.Deg2Rad, t[1] * AstroMath.Deg2Rad, out c)) continue;
                    float pw = (float)(_scale * 2 * Math.Tan(t[2] * AstroMath.Deg2Rad / 4)), ph = (float)(_scale * 2 * Math.Tan(t[3] * AstroMath.Deg2Rad / 4));
                    var st = g.Save();
                    g.TranslateTransform(c.X, c.Y);
                    g.RotateTransform((float)(NorthAngle(t[0] * AstroMath.Deg2Rad, t[1] * AstroMath.Deg2Rad, c) + t[4]));
                    g.DrawRectangle(pen, -pw, -ph, 2 * pw, 2 * ph);
                    g.Restore(st);
                }
        }

        private void DrawTelescope(Graphics g)
        {
            var tel = DeviceManager.Instance.Telescope;
            if (tel == null || !tel.Connected) return;
            try
            {
                var j2000 = _ctx.ApparentToIcrs(tel.RightAscension * AstroMath.Hour2Rad, tel.Declination * AstroMath.Deg2Rad);
                PointF pt;
                if (!ProjectIcrs(j2000.Ra, j2000.Dec, out pt) || !InView(pt)) return;
                using (var pen = new Pen(_p.Success, 2f))
                {
                    g.DrawEllipse(pen, pt.X - 12, pt.Y - 12, 24, 24);
                    g.DrawLine(pen, pt.X - 20, pt.Y, pt.X - 8, pt.Y); g.DrawLine(pen, pt.X + 8, pt.Y, pt.X + 20, pt.Y);
                    g.DrawLine(pen, pt.X, pt.Y - 20, pt.X, pt.Y - 8); g.DrawLine(pen, pt.X, pt.Y + 8, pt.X, pt.Y + 20);
                }
                TextRenderer.DrawText(g, "Telescope", Theme.SmallFont, new Point((int)pt.X + 14, (int)pt.Y + 12), _p.Success);
            }
            catch { }
        }

        private void DrawHighlight(Graphics g)
        {
            PointF pt;
            if (!ProjectIcrs(Highlight.RaDeg * AstroMath.Deg2Rad, Highlight.DecDeg * AstroMath.Deg2Rad, out pt)) return;
            using (var pen = new Pen(_p.Selection, 2f))
            {
                float r = 14;
                g.DrawLine(pen, pt.X - r, pt.Y - r, pt.X - r / 2, pt.Y - r); g.DrawLine(pen, pt.X - r, pt.Y - r, pt.X - r, pt.Y - r / 2);
                g.DrawLine(pen, pt.X + r, pt.Y - r, pt.X + r / 2, pt.Y - r); g.DrawLine(pen, pt.X + r, pt.Y - r, pt.X + r, pt.Y - r / 2);
                g.DrawLine(pen, pt.X - r, pt.Y + r, pt.X - r / 2, pt.Y + r); g.DrawLine(pen, pt.X - r, pt.Y + r, pt.X - r, pt.Y + r / 2);
                g.DrawLine(pen, pt.X + r, pt.Y + r, pt.X + r / 2, pt.Y + r); g.DrawLine(pen, pt.X + r, pt.Y + r, pt.X + r, pt.Y + r / 2);
            }
        }

        private void DrawHorizon(Graphics g, Color sky)
        {
            int w = (int)Math.Round(_cx * 2), h = (int)Math.Round(_cy * 2);
            // horizon profile per degree of azimuth (0..360 inclusive), so the per-pixel test is cheap
            var profile = new double[361];
            for (int az = 0; az <= 360; az++) profile[az] = Site.HorizonAltitude(az) * AstroMath.Deg2Rad;

            // Ground: test the screen directly instead of building a polygon from the projected horizon, which breaks
            // when parts of the horizon project off-screen or behind the viewer. Each row is sampled coarsely, and every
            // ground/sky transition is refined by bisection to sub-pixel precision.
            const int rowStep = 2, colStep = 8;
            using (var path = new GraphicsPath())
            {
                for (int y = 0; y < h; y += rowStep)
                {
                    double yc = y + rowStep / 2.0;
                    float runStart = -1;
                    bool prev = IsGround(0, yc, profile);
                    if (prev) runStart = 0;
                    for (int x = colStep; ; x += colStep)
                    {
                        double xs = Math.Min(x, w);
                        bool cur = IsGround(xs, yc, profile);
                        if (cur != prev)
                        {
                            double lo = xs - colStep, hi = xs;
                            for (int k = 0; k < 6; k++) { double mid = (lo + hi) / 2; if (IsGround(mid, yc, profile) == prev) lo = mid; else hi = mid; }
                            float edge = (float)((lo + hi) / 2);
                            if (cur) runStart = edge;
                            else { path.AddRectangle(new RectangleF(runStart, y, edge - runStart, rowStep)); runStart = -1; }
                            prev = cur;
                        }
                        if (xs >= w) break;
                    }
                    if (runStart >= 0) path.AddRectangle(new RectangleF(runStart, y, w - runStart, rowStep));
                }
                var old = g.SmoothingMode;
                g.SmoothingMode = SmoothingMode.None; // no seams between the row strips
                using (var br = new SolidBrush(_print ? Color.FromArgb(240, 240, 240) : Theme.WithAlpha(_p.Ground, 245))) g.FillPath(br, path);
                g.SmoothingMode = old;
            }

            // horizon line, split wherever the projection fails or jumps (behind the viewer)
            var seg = new List<PointF>();
            float maxJump = Math.Max(w, h) * 0.5f;
            using (var pen = new Pen(_p.Horizon, 1.5f))
            {
                Action flush = () => { if (seg.Count > 1) g.DrawLines(pen, seg.ToArray()); seg.Clear(); };
                for (double az = 0; az <= 360; az += 0.5)
                {
                    PointF pt;
                    double alt = profile[(int)Math.Floor(az)] + (profile[Math.Min(360, (int)Math.Floor(az) + 1)] - profile[(int)Math.Floor(az)]) * (az - Math.Floor(az));
                    if (!Project(HorizontalToFrame(az * AstroMath.Deg2Rad, alt), out pt)) { flush(); continue; }
                    if (seg.Count > 0)
                    {
                        var q = seg[seg.Count - 1];
                        if (Math.Abs(pt.X - q.X) + Math.Abs(pt.Y - q.Y) > maxJump) flush();
                    }
                    seg.Add(pt);
                }
                flush();
            }
            if (Layers.Cardinals)
            {
                string[] names = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };
                for (int i = 0; i < 8; i++)
                {
                    PointF pt;
                    if (Project(HorizontalToFrame(i * 45 * AstroMath.Deg2Rad, -1.5 * AstroMath.Deg2Rad), out pt) && InView(pt))
                    {
                        var f = i % 2 == 0 ? Theme.HeaderFont : Theme.UiFont;
                        var sz = TextRenderer.MeasureText(names[i], f);
                        TextRenderer.DrawText(g, names[i], f, new Point((int)pt.X - sz.Width / 2, (int)pt.Y), i == 0 ? _p.Danger : _p.Warning);
                    }
                }
            }
        }

        private void DrawInfo(Graphics g, int w, int h)
        {
            var local = Site.ToLocal(Time.ToDateTimeUtc());
            string t = string.Format("{0:yyyy-MM-dd HH:mm:ss} local · {1}   FOV {2}   mag ≤ {3:0.0}{4}", local, Site.Name, AstroMath.FormatAngleAuto(FovDeg), EffectiveMagLimit, AppState.IsLive || FixedTime.HasValue ? "" : "   (simulated time)");
            if (!string.IsNullOrEmpty(Caption)) t = Caption + "   " + t;
            var sz = TextRenderer.MeasureText(t, Theme.SmallFont);
            using (var b = new SolidBrush(Theme.WithAlpha(_print ? Color.White : _p.Surface, 190))) g.FillRectangle(b, 6, h - sz.Height - 10, sz.Width + 10, sz.Height + 6);
            TextRenderer.DrawText(g, t, Theme.SmallFont, new Point(11, h - sz.Height - 7), _print ? Color.Black : _p.TextDim);
            if (Mode == ChartMode.Atlas)
            {
                // north/east indicator
                PointF n, e, c = new PointF(w - 40, 40);
                var ci = CenterIcrs(); double ra, dec; ci.ToSpherical(out ra, out dec);
                PointF cpt; ProjectIcrs(ra, dec, out cpt);
                double na = NorthAngle(ra, dec, cpt) * AstroMath.Deg2Rad;
                n = new PointF(c.X + (float)(Math.Sin(na) * 22), c.Y - (float)(Math.Cos(na) * 22));
                double ea = na + (FlipX ^ FlipY ? Math.PI / 2 : -Math.PI / 2);
                e = new PointF(c.X + (float)(Math.Sin(ea) * 18), c.Y - (float)(Math.Cos(ea) * 18));
                using (var pen = new Pen(_print ? Color.Black : _p.TextDim, 1.4f)) { g.DrawLine(pen, c, n); g.DrawLine(pen, c, e); }
                TextRenderer.DrawText(g, "N", Theme.SmallFont, Point.Round(new PointF(n.X - 4, n.Y - 14)), _print ? Color.Black : _p.Text);
                TextRenderer.DrawText(g, "E", Theme.SmallFont, Point.Round(new PointF(e.X - 12, e.Y - 6)), _print ? Color.Black : _p.Text);
            }
        }

        // ------------------------------------------------------------------ interaction

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();
            if (e.Button == MouseButtons.Left) { _dragging = true; _dragStart = e.Location; _dragLon = CenterLon; _dragLat = CenterLat; }
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_dragging && (Math.Abs(e.X - _dragStart.X) + Math.Abs(e.Y - _dragStart.Y) > 3))
            {
                double perPx = FovDeg / Width * AstroMath.Deg2Rad;
                double dx = (e.X - _dragStart.X) * perPx, dy = (e.Y - _dragStart.Y) * perPx;
                if (FlipX) dx = -dx;
                if (FlipY) dy = -dy;
                CenterLon = _dragLon + dx / Math.Max(0.15, Math.Cos(_dragLat));
                CenterLat = AstroMath.Clamp(_dragLat + dy, -AstroMath.HalfPi + 0.001, AstroMath.HalfPi - 0.001);
                Cursor = Cursors.SizeAll;
                Invalidate();
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            bool wasClick = !_dragging || Math.Abs(e.X - _dragStart.X) + Math.Abs(e.Y - _dragStart.Y) <= 3;
            if (_dragging && !wasClick) ViewChanged?.Invoke();
            _dragging = false;
            Cursor = Cursors.Default;
            if (wasClick && e.Button == MouseButtons.Left)
            {
                var o = HitTest(e.Location);
                if (o != null) { Highlight = o; ObjectClicked?.Invoke(o); Invalidate(); }
            }
            if (e.Button == MouseButtons.Right)
            {
                double ra, dec;
                ScreenToIcrs(e.Location, out ra, out dec);
                ContextRequested?.Invoke(ra * AstroMath.Rad2Deg, dec * AstroMath.Rad2Deg, e);
            }
            base.OnMouseUp(e);
        }

        public SelectedObject HitTest(Point p)
        {
            Hit best = null; double bd = double.MaxValue;
            foreach (var h in _hits)
            {
                double dx = h.P.X - p.X, dy = h.P.Y - p.Y, d = Math.Sqrt(dx * dx + dy * dy);
                if (d > h.R + 2) continue;
                double score = d + h.Priority * 4;
                if (score < bd) { bd = score; best = h; }
            }
            return best?.O;
        }

        protected override void OnDoubleClick(EventArgs e)
        {
            var p = PointToClient(MousePosition);
            double ra, dec;
            if (ScreenToIcrs(p, out ra, out dec)) CenterOn(ra, dec);
            base.OnDoubleClick(e);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            FovDeg = AstroMath.Clamp(FovDeg * (e.Delta > 0 ? 0.8 : 1.25), 0.05, Mode == ChartMode.Planetarium ? 200 : 180);
            Invalidate();
            ViewChanged?.Invoke();
            base.OnMouseWheel(e);
        }

        protected override bool IsInputKey(Keys k) => k == Keys.Left || k == Keys.Right || k == Keys.Up || k == Keys.Down || base.IsInputKey(k);

        protected override void OnKeyDown(KeyEventArgs e)
        {
            double step = FovDeg / 10 * AstroMath.Deg2Rad;
            switch (e.KeyCode)
            {
                case Keys.Left: CenterLon += step / Math.Max(0.2, Math.Cos(CenterLat)) * (FlipX ? -1 : 1); break;
                case Keys.Right: CenterLon -= step / Math.Max(0.2, Math.Cos(CenterLat)) * (FlipX ? -1 : 1); break;
                case Keys.Up: CenterLat = Math.Min(AstroMath.HalfPi - 0.001, CenterLat + step); break;
                case Keys.Down: CenterLat = Math.Max(-AstroMath.HalfPi + 0.001, CenterLat - step); break;
                case Keys.Oemplus: case Keys.Add: case Keys.PageUp: FovDeg = Math.Max(0.05, FovDeg * 0.8); break;
                case Keys.OemMinus: case Keys.Subtract: case Keys.PageDown: FovDeg = Math.Min(200, FovDeg * 1.25); break;
                case Keys.N: if (Mode == ChartMode.Planetarium) LookAt(0, 30); break;
                case Keys.E: if (Mode == ChartMode.Planetarium) LookAt(90, 30); break;
                case Keys.S: if (Mode == ChartMode.Planetarium) LookAt(180, 30); break;
                case Keys.W: if (Mode == ChartMode.Planetarium) LookAt(270, 30); break;
                case Keys.Z: if (Mode == ChartMode.Planetarium) LookAt(180, 89.9); break;
                default: base.OnKeyDown(e); return;
            }
            Invalidate();
            ViewChanged?.Invoke();
        }
    }
}
