using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Printing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using Sidralune.Core;
using Sidralune.Core.Catalogs;
using Sidralune.Core.Coordinates;
using Sidralune.Core.Ephemeris;
using Sidralune.Core.Mathematics;
using Sidralune.Core.Time;
using Sidralune.Science.SolarSystem;
using AstroEvents = Sidralune.Core.Ephemeris.Events;

namespace Sidralune.UI
{
    /// <summary>A paper size for printed star charts (portrait, mm).</summary>
    public class ChartPaper
    {
        public readonly string Name;
        public readonly double WidthMm, HeightMm;

        public static readonly ChartPaper[] All = { new ChartPaper("A4", 210, 297), new ChartPaper("A3", 297, 420), new ChartPaper("A3+", 329, 483), new ChartPaper("A2", 420, 594) };

        public ChartPaper(string name, double widthMm, double heightMm) { Name = name; WidthMm = widthMm; HeightMm = heightMm; }

        /// <summary>The page in pixels at a resolution, upright or on its side.</summary>
        public Size Pixels(int dpi, bool landscape)
        {
            int w = (int)Math.Round(WidthMm / 25.4 * dpi), h = (int)Math.Round(HeightMm / 25.4 * dpi);
            return landscape ? new Size(h, w) : new Size(w, h);
        }

        public override string ToString() => Name;
    }

    /// <summary>
    /// An all-sky star chart for one place and time, laid out on a page: the sky above the horizon as a circle with the
    /// zenith in the middle, north at the top and east on the left (as seen looking up), and beside it what is up that
    /// night. Everything is drawn as vectors in millimetres, so the same page goes to the screen, to a printer at its full
    /// resolution or to a 300 dpi image. Labels turn so that their tops point to the zenith: face any direction, hold that
    /// edge of the chart down, and the names near that horizon read the right way up.
    /// </summary>
    public class StarChartPage
    {
        public Observer Site;
        public AstroTime Time;
        public ChartLayers Layers = new ChartLayers();
        public Palette Palette = Palette.Print();
        /// <summary>Black on white for paper; otherwise the palette's own colours (dark, or red at night).</summary>
        public bool Print = true;
        public bool Refraction = true;
        public SelectedObject Highlight;

        public class Hit { public PointF P; public float R; public SelectedObject O; }

        // the layout of the last render (mm)
        public PointF Centre { get; private set; }
        public float Radius { get; private set; }
        /// <summary>Text and symbols grow a little on large paper so the chart reads from further away.</summary>
        public float FontScale { get; private set; }
        public double MagLimit { get; private set; }
        public RectangleF InfoArea { get; private set; }
        public readonly List<RectangleF> InfoBlocks = new List<RectangleF>();
        public readonly List<string> InfoHeadings = new List<string>();
        /// <summary>The rows written beside the circle, as "Heading: name value".</summary>
        public readonly List<string> InfoLines = new List<string>();
        /// <summary>Every label drawn in the circle, as its four corners on the page.</summary>
        public readonly List<PointF[]> LabelBoxes = new List<PointF[]>();
        public readonly List<string> LabelTexts = new List<string>();
        public readonly List<Hit> Hits = new List<Hit>();

        // English dates and decimal points on every page, whatever the computer's language
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private Graphics _g;
        private SkyContext _ctx;
        private float _s;
        private Color _text, _dim, _sky, _paper;
        private readonly StringFormat _sf = (StringFormat)StringFormat.GenericTypographic.Clone();
        // labels to place once every symbol is drawn, by importance (lowest first)
        private readonly List<Tuple<int, Action>> _labelJobs = new List<Tuple<int, Action>>();

        public StarChartPage() { _sf.FormatFlags |= StringFormatFlags.NoClip | StringFormatFlags.MeasureTrailingSpaces; }

        // ------------------------------------------------------------------ projection

        /// <summary>Altitude and azimuth (radians, azimuth from north through east) of an ICRS direction.</summary>
        public Horizontal AltAz(double ra, double dec)
        {
            var tod = _ctx.Npb * Vec3.FromSpherical(ra, dec);
            double r, d;
            tod.ToSpherical(out r, out d);
            var hz = Transforms.HaDecToAltAz(_ctx.Last - r, d, Site.LatRad);
            if (Refraction && hz.Alt > -0.035) hz.Alt += Transforms.RefractionFromTrue(hz.Alt, Site.PressureHpa, Site.TemperatureC);
            return hz;
        }

        /// <summary>
        /// A place in the sky on the page (stereographic, zenith at the centre, the horizon on the circle, north up and east
        /// to the left). False far below the horizon, where the projection runs away.
        /// </summary>
        public bool ToPage(double azRad, double altRad, out PointF pt)
        {
            pt = PointF.Empty;
            if (altRad < -1.2) return false;
            double r = Radius * Math.Tan((AstroMath.HalfPi - altRad) / 2);
            pt = new PointF((float)(Centre.X - r * Math.Sin(azRad)), (float)(Centre.Y - r * Math.Cos(azRad)));
            return true;
        }

        private bool ToPage(Horizontal h, out PointF pt) => ToPage(h.Az, h.Alt, out pt);

        /// <summary>
        /// The turn (deg, clockwise) of a label at a point so that its top faces the zenith; near the zenith, where the
        /// direction is unsettled, labels stay upright.
        /// </summary>
        public float LabelAngle(PointF p)
        {
            double dx = p.X - Centre.X, dy = p.Y - Centre.Y;
            if (Math.Sqrt(dx * dx + dy * dy) < 0.15 * Radius) return 0;
            return (float)(Math.Atan2(dx, -dy) * AstroMath.Rad2Deg - 180);
        }

        /// <summary>The faintest stars shown: deeper on larger paper (about 5.5 on A4, 6.5 on A2), or the chosen limit.</summary>
        public static double AutoMagLimit(double diameterMm) => AstroMath.Clamp(5.5 + 3 * Math.Log10(Math.Max(1, diameterMm) / 175), 4.5, 7);

        // ------------------------------------------------------------------ page

        /// <summary>Draws the page into a rectangle given in millimetres (the graphics' world units must be millimetres).</summary>
        public void Render(Graphics g, RectangleF page)
        {
            _g = g;
            LabelBoxes.Clear(); LabelTexts.Clear(); Hits.Clear(); InfoBlocks.Clear(); InfoHeadings.Clear(); InfoLines.Clear(); _labelJobs.Clear();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            var p = Palette;
            _paper = Print ? Color.White : p.ChartBackground;
            _sky = Print ? Color.White : p.SkyNight;
            _text = Print ? Color.Black : p.Text;
            _dim = Print ? Color.FromArgb(80, 80, 80) : p.TextDim;
            using (var b = new SolidBrush(_paper)) g.FillRectangle(b, page);
            _ctx = new SkyContext(Site, Time) { ApplyRefraction = Refraction };

            // layout: the circle with a ring for the directions, the title above, the night's details beside or below it
            _s = FontScale = (float)AstroMath.Clamp(Math.Sqrt(Math.Min(page.Width, page.Height) / 190), 1, 1.5);
            float ring = 9 * _s, titleH = 15 * _s, footH = 5 * _s, gap = 4 * _s;
            // the details go beside the circle or below it, whichever leaves the larger circle
            float dSide = Math.Min(page.Height - titleH - footH - 2 * ring, page.Width - 66 * _s - 2 * ring - gap);
            float dBelow = Math.Min(page.Width - 2 * ring, page.Height - titleH - footH - 64 * _s - 2 * ring);
            bool side = dSide > dBelow;
            float d = Math.Max(dSide, dBelow);
            if (d < 30) return;
            Radius = d / 2;
            if (side)
            {
                float top = page.Top + titleH, h = page.Bottom - footH - top;
                Centre = new PointF(page.Left + ring + Radius, top + h / 2);
                float x = page.Left + 2 * ring + d + gap;
                InfoArea = new RectangleF(x, top, page.Right - x, h);
            }
            else
            {
                Centre = new PointF(page.Left + page.Width / 2, page.Top + titleH + ring + Radius);
                float y = Centre.Y + Radius + ring + gap / 2;
                InfoArea = new RectangleF(page.Left, y, page.Width, page.Bottom - footH - y);
            }
            MagLimit = Layers.AutoMagLimit ? AutoMagLimit(d) : Layers.MagLimit;

            DrawTitle(page);
            DrawSky();
            DrawRing();
            DrawInfo();
            using (var f = Font(5.5f))
            using (var b = new SolidBrush(_dim))
            {
                var v = typeof(StarChartPage).Assembly.GetName().Version;
                string foot = string.Format(Inv, "Sidralune {0}.{1}.{2}  ·  stereographic, zenith at the centre  ·  stars to magnitude {3:0.0}  ·  Face a direction and hold that edge of the chart down", v.Major, v.Minor, v.Build, MagLimit);
                g.DrawString(foot, f, b, page.Left, page.Bottom - footH + 1.2f * _s, _sf);
            }
        }

        private Font Font(float pt, FontStyle style = FontStyle.Regular) => new Font("Segoe UI", pt * 0.3528f * _s, style, GraphicsUnit.World);

        private void DrawTitle(RectangleF page)
        {
            var local = Site.ToLocal(Time.ToDateTimeUtc());
            using (var tf = Font(15, FontStyle.Bold))
            using (var sf = Font(8))
            using (var tb = new SolidBrush(_text))
            using (var db = new SolidBrush(_dim))
            {
                _g.DrawString("The sky over " + Site.Name, tf, tb, page.Left, page.Top, _sf);
                string sub = string.Format(Inv, "{0:dddd d MMMM yyyy}, {0:HH:mm} local time  ·  {1:0.0}° {2}, {3:0.0}° {4}", local,
                    Math.Abs(Site.LatitudeDeg), Site.LatitudeDeg >= 0 ? "N" : "S", Math.Abs(Site.LongitudeDeg), Site.LongitudeDeg >= 0 ? "E" : "W");
                _g.DrawString(sub, sf, db, page.Left, page.Top + 7.6f * _s, _sf);
            }
        }

        // ------------------------------------------------------------------ the sky circle

        private void DrawSky()
        {
            var p = Palette;
            var g = _g;
            var circle = new RectangleF(Centre.X - Radius, Centre.Y - Radius, 2 * Radius, 2 * Radius);
            using (var b = new SolidBrush(_sky)) g.FillEllipse(b, circle);
            var state = g.Save();
            using (var clip = new GraphicsPath())
            {
                clip.AddEllipse(circle);
                g.SetClip(clip, CombineMode.Intersect);
                if (Layers.MilkyWay) DrawMilkyWay();
                if (Layers.Horizon) DrawLocalHorizon();
                if (Layers.AltAzGrid) DrawAltAzGrid();
                if (Layers.EquatorialGrid) DrawEquatorialGrid();
                if (Layers.Ecliptic) using (var pen = Pen(p.Ecliptic, 0.25f, DashStyle.Dash)) DrawCurve(pen, Enumerable.Range(0, 361).Select(i => Transforms.EclipticToEquatorial(Vec3.FromSpherical(i * AstroMath.Deg2Rad, 0), Precession.ObliquityJ2000)));
                if (Layers.GalacticEquator) using (var pen = Pen(p.GalacticEquator, 0.25f, DashStyle.Dash)) DrawCurve(pen, Enumerable.Range(0, 361).Select(i => { double ra, dec; Transforms.GalacticToEquatorial(i * AstroMath.Deg2Rad, 0, out ra, out dec); return Vec3.FromSpherical(ra, dec); }));
                if (Layers.Meridian) using (var pen = Pen(Print ? Color.FromArgb(150, 150, 150) : Theme.WithAlpha(p.Warning, 140), 0.25f, DashStyle.Dash)) g.DrawLine(pen, Centre.X, Centre.Y - Radius, Centre.X, Centre.Y + Radius);
                if (Layers.Boundaries) using (var pen = Pen(p.ConstellationBoundary, 0.2f, DashStyle.Dot)) foreach (var seg in CatalogManager.Instance.BoundarySegments) DrawCurve(pen, seg.Select(v => Vec3.FromSpherical(v[0], v[1])), true);
                if (Layers.ConstellationLines) DrawConstellationLines();
                if (Layers.DeepSky) DrawDeepSky();
                if (Layers.Stars) DrawStars();
                if (Layers.Planets) DrawSolarSystem();
            }
            g.Restore(state);
            if (Layers.ConstellationNames) _labelJobs.Add(Tuple.Create(2, (Action)DrawConstellationNames));
            // labels last and by importance, each turned to the zenith and kept clear of those already placed
            foreach (var job in _labelJobs.OrderBy(j => j.Item1).ToList()) job.Item2();
            if (Highlight != null && !Print) DrawHighlight();
            using (var pen = Pen(Print ? Color.Black : p.Horizon, 0.35f)) g.DrawEllipse(pen, circle);
        }

        private Pen Pen(Color c, float widthMm, DashStyle dash = DashStyle.Solid) => new Pen(c, widthMm * _s) { DashStyle = dash };

        // a curve through ICRS directions, broken where it dips far below the horizon (the clip trims the rest)
        private void DrawCurve(Pen pen, IEnumerable<Vec3> icrs, bool densify = false)
        {
            var pts = new List<PointF>();
            Vec3? prev = null;
            foreach (var v0 in icrs)
            {
                var steps = new List<Vec3>();
                if (densify && prev.HasValue)
                {
                    int n = (int)(Vec3.AngleBetween(prev.Value, v0) / (2 * AstroMath.Deg2Rad));
                    for (int k = 1; k < n; k++) steps.Add((prev.Value * (n - k) + v0 * k).Normalized());
                }
                steps.Add(v0);
                prev = v0;
                foreach (var v in steps)
                {
                    double ra, dec;
                    v.ToSpherical(out ra, out dec);
                    var h = AltAz(ra, dec);
                    PointF pt;
                    if (h.Alt > -0.6 && ToPage(h, out pt)) pts.Add(pt);
                    else { if (pts.Count > 1) _g.DrawLines(pen, pts.ToArray()); pts.Clear(); }
                }
            }
            if (pts.Count > 1) _g.DrawLines(pen, pts.ToArray());
        }

        private void DrawMilkyWay()
        {
            // the band along the galactic plane as three nested strips, like the planetarium's
            const int n = 180;
            var up = new PointF[n]; var lo = new PointF[n]; var ok = new bool[n];
            foreach (var strip in new[] { Tuple.Create(0.15, 50), Tuple.Create(0.09, 70), Tuple.Create(0.04, 90) })
            {
                for (int i = 0; i < n; i++)
                {
                    double l = (-180 + 2.0 * i) * AstroMath.Deg2Rad, ra, dec;
                    Transforms.GalacticToEquatorial(l, strip.Item1, out ra, out dec);
                    var a = AltAz(ra, dec);
                    Transforms.GalacticToEquatorial(l, -strip.Item1, out ra, out dec);
                    var b = AltAz(ra, dec);
                    ok[i] = a.Alt > -0.7 && b.Alt > -0.7 && ToPage(a, out up[i]) && ToPage(b, out lo[i]);
                }
                using (var path = new GraphicsPath(FillMode.Winding))
                {
                    for (int i = 0; i < n; i++)
                    {
                        int j = (i + 1) % n;
                        if (ok[i] && ok[j]) path.AddPolygon(new[] { up[i], up[j], lo[j], lo[i] });
                    }
                    var c = Print ? Color.FromArgb((int)(strip.Item2 * 0.8), 170, 180, 205) : Theme.WithAlpha(Palette.MilkyWay, strip.Item2);
                    using (var br = new SolidBrush(c)) _g.FillPath(br, path);
                }
            }
        }

        // trees and buildings from the site's horizon profile, shaded in from the edge
        private void DrawLocalHorizon()
        {
            var pts = new PointF[360];
            bool any = false;
            for (int az = 0; az < 360; az++)
            {
                double alt = Math.Max(0, Site.HorizonAltitude(az));
                any |= alt > 0.2;
                ToPage(az * AstroMath.Deg2Rad, alt * AstroMath.Deg2Rad, out pts[az]);
            }
            if (!any) return;
            using (var path = new GraphicsPath(FillMode.Alternate))
            {
                path.AddEllipse(Centre.X - Radius, Centre.Y - Radius, 2 * Radius, 2 * Radius);
                path.AddPolygon(pts);
                using (var b = new SolidBrush(Print ? Color.FromArgb(225, 225, 225) : Theme.WithAlpha(Palette.Ground, 220))) _g.FillPath(b, path);
            }
        }

        private void DrawAltAzGrid()
        {
            using (var pen = Pen(Print ? Color.FromArgb(185, 185, 185) : Palette.GridAltAz, 0.18f, DashStyle.Dot))
            {
                foreach (var alt in new[] { 30, 60 })
                {
                    float r = (float)(Radius * Math.Tan((90 - alt) * AstroMath.Deg2Rad / 2));
                    _g.DrawEllipse(pen, Centre.X - r, Centre.Y - r, 2 * r, 2 * r);
                }
                pen.DashStyle = DashStyle.Solid;
                float z = 1.5f * _s;
                _g.DrawLine(pen, Centre.X - z, Centre.Y, Centre.X + z, Centre.Y);
                _g.DrawLine(pen, Centre.X, Centre.Y - z, Centre.X, Centre.Y + z);
            }
        }

        private void DrawEquatorialGrid()
        {
            using (var pen = Pen(Print ? Color.FromArgb(205, 205, 205) : Palette.GridEquatorial, 0.18f))
            {
                for (int dec = -60; dec <= 60; dec += 30)
                    DrawCurve(pen, Enumerable.Range(0, 361).Select(i => Vec3.FromSpherical(i * AstroMath.Deg2Rad, dec * AstroMath.Deg2Rad)));
                for (int h = 0; h < 24; h += 2)
                    DrawCurve(pen, Enumerable.Range(-80, 161).Select(i => Vec3.FromSpherical(h * 15 * AstroMath.Deg2Rad, i * AstroMath.Deg2Rad)));
            }
        }

        private void DrawConstellationLines()
        {
            using (var pen = Pen(Palette.ConstellationLine, 0.22f))
                foreach (var c in CatalogManager.Instance.Constellations)
                    foreach (var l in c.Lines)
                    {
                        if (AltAz(l[0], l[1]).Alt < -0.3 && AltAz(l[2], l[3]).Alt < -0.3) continue;
                        DrawCurve(pen, new[] { Vec3.FromSpherical(l[0], l[1]), Vec3.FromSpherical(l[2], l[3]) }, true);
                    }
        }

        private void DrawConstellationNames()
        {
            using (var f = Font(5.5f))
                foreach (var c in CatalogManager.Instance.Constellations)
                {
                    PointF pt;
                    var h = AltAz(c.LabelRa, c.LabelDec);
                    if (h.Alt > 2 * AstroMath.Deg2Rad && ToPage(h, out pt)) Label(c.Name.ToUpperInvariant(), f, Palette.ConstellationName, pt, 0, true);
                }
        }

        private Color TypeColor(ObjectType t)
        {
            var p = Palette;
            switch (t)
            {
                case ObjectType.Galaxy: case ObjectType.GalaxyPair: case ObjectType.GalaxyGroup: return p.Galaxy;
                case ObjectType.OpenCluster: case ObjectType.GlobularCluster: case ObjectType.Asterism: case ObjectType.StarCloud: return p.Cluster;
                case ObjectType.PlanetaryNebula: return p.PlanetaryNebula;
                default: return p.Nebula;
            }
        }

        // an object's symbol at its size on the sky (at least a readable mark)
        private void DrawDsoSymbol(ObjectType t, PointF pt, float r, Color c)
        {
            var g = _g;
            using (var pen = Pen(c, 0.25f))
                switch (t)
                {
                    case ObjectType.Galaxy: case ObjectType.GalaxyPair: case ObjectType.GalaxyGroup: g.DrawEllipse(pen, pt.X - r, pt.Y - r * 0.55f, 2 * r, r * 1.1f); break;
                    case ObjectType.OpenCluster: case ObjectType.Asterism: case ObjectType.StarCloud: pen.DashStyle = DashStyle.Dot; g.DrawEllipse(pen, pt.X - r, pt.Y - r, 2 * r, 2 * r); break;
                    case ObjectType.GlobularCluster: g.DrawEllipse(pen, pt.X - r, pt.Y - r, 2 * r, 2 * r); g.DrawLine(pen, pt.X - r, pt.Y, pt.X + r, pt.Y); g.DrawLine(pen, pt.X, pt.Y - r, pt.X, pt.Y + r); break;
                    case ObjectType.PlanetaryNebula:
                        g.DrawEllipse(pen, pt.X - r * 0.6f, pt.Y - r * 0.6f, r * 1.2f, r * 1.2f);
                        g.DrawLine(pen, pt.X, pt.Y - r, pt.X, pt.Y - r * 0.6f); g.DrawLine(pen, pt.X, pt.Y + r, pt.X, pt.Y + r * 0.6f);
                        g.DrawLine(pen, pt.X - r, pt.Y, pt.X - r * 0.6f, pt.Y); g.DrawLine(pen, pt.X + r, pt.Y, pt.X + r * 0.6f, pt.Y); break;
                    default: g.DrawRectangle(pen, pt.X - r, pt.Y - r, 2 * r, 2 * r); break;
                }
        }

        private void DrawDeepSky()
        {
            var list = new List<DeepSkyObject>();
            var zen = _ctx.HorizontalToIcrs(0, AstroMath.HalfPi);
            CatalogManager.Instance.DeepSky.Cone(zen.Ra, zen.Dec, AstroMath.HalfPi + 0.02, 99, list);
            double ngcLimit = MagLimit + 1.5;
            var labels = new List<Tuple<string, PointF, float, Color, double>>();
            foreach (var o in list)
            {
                bool messier = o.Messier != null;
                if (!messier && (double.IsNaN(o.Mag) || o.Mag > ngcLimit)) continue;
                var h = AltAz(o.Ra, o.Dec);
                PointF pt;
                if (h.Alt < 0 || !ToPage(h, out pt)) continue;
                // the stereographic scale grows towards the horizon
                double perRad = Radius / 2 / Math.Pow(Math.Cos((AstroMath.HalfPi - h.Alt) / 2), 2);
                float r = (float)AstroMath.Clamp(double.IsNaN(o.MajorAxis) ? 0 : o.MajorAxis / 120 * AstroMath.Deg2Rad * perRad, 0.9 * _s, 5 * _s);
                var c = TypeColor(o.Type);
                DrawDsoSymbol(o.Type, pt, r, c);
                Hits.Add(new Hit { P = pt, R = Math.Max(1.5f * _s, r), O = SelectedObject.From(o) });
                if (messier && Layers.DeepSkyLabels) labels.Add(Tuple.Create(o.Messier, pt, r, c, double.IsNaN(o.Mag) ? 99 : o.Mag));
            }
            // brightest first, so M31 keeps its name beside M32 and M110
            _labelJobs.Add(Tuple.Create(4, (Action)(() => { using (var f = Font(5f)) foreach (var l in labels.OrderBy(x => x.Item5)) Label(l.Item1, f, l.Item4, l.Item2, l.Item3 + 0.4f * _s); })));
        }

        private static readonly Dictionary<string, string> Greek = new Dictionary<string, string>
        {
            { "Alp", "α" }, { "Bet", "β" }, { "Gam", "γ" }, { "Del", "δ" }, { "Eps", "ε" }, { "Zet", "ζ" }, { "Eta", "η" }, { "The", "θ" },
            { "Iot", "ι" }, { "Kap", "κ" }, { "Lam", "λ" }, { "Mu", "μ" }, { "Nu", "ν" }, { "Xi", "ξ" }, { "Omi", "ο" }, { "Pi", "π" },
            { "Rho", "ρ" }, { "Sig", "σ" }, { "Tau", "τ" }, { "Ups", "υ" }, { "Phi", "φ" }, { "Chi", "χ" }, { "Psi", "ψ" }, { "Ome", "ω" },
        };

        /// <summary>A Bayer or Flamsteed designation as printed on a chart: "Alp2 Cen" → "α²", "58 Ori" → "58".</summary>
        public static string BayerLabel(string bayer)
        {
            if (string.IsNullOrEmpty(bayer)) return null;
            string head = bayer.Split(' ')[0];
            int k = 0;
            while (k < head.Length && char.IsLetter(head[k])) k++;
            string letters = head.Substring(0, k), digits = head.Substring(k);
            if (letters.Length == 0) return digits;
            string g;
            if (!Greek.TryGetValue(letters, out g)) return null;
            return g + new string(digits.Select(ch => "⁰¹²³⁴⁵⁶⁷⁸⁹"[ch - '0']).ToArray());
        }

        private void DrawStars()
        {
            var g = _g;
            var stars = new List<Star>();
            var zen = _ctx.HorizontalToIcrs(0, AstroMath.HalfPi);
            CatalogManager.Instance.Stars.Cone(zen.Ra, zen.Dec, AstroMath.HalfPi + 0.02, MagLimit, stars);
            var named = new List<Tuple<string, PointF, float, double>>();
            var bayer = new List<Tuple<string, PointF, float>>();
            using (var halo = new SolidBrush(_sky))
                foreach (var s in stars.OrderByDescending(x => x.Mag))
                {
                    var h = AltAz(s.Ra, s.Dec);
                    PointF pt;
                    if (h.Alt < 0 || !ToPage(h, out pt)) continue;
                    float r = SkyChart.StarDotRadius(s.Mag, MagLimit) * 0.24f * _s;
                    // a thin ring of background keeps stars apart from the lines behind them
                    float hr = r + 0.18f * _s;
                    g.FillEllipse(halo, pt.X - hr, pt.Y - hr, 2 * hr, 2 * hr);
                    using (var b = new SolidBrush(Print ? Color.Black : Palette.StarColor(s.BV))) g.FillEllipse(b, pt.X - r, pt.Y - r, 2 * r, 2 * r);
                    if (s.Mag < MagLimit - 1) Hits.Add(new Hit { P = pt, R = Math.Max(1.2f * _s, r), O = SelectedObject.From(s) });
                    if (Layers.StarNames && !string.IsNullOrEmpty(s.Name) && s.Mag < MagLimit - 2.5) named.Add(Tuple.Create(s.Name, pt, r, s.Mag));
                    else if (Layers.Bayer && s.Mag < MagLimit - 1.5) { var bl = BayerLabel(s.Bayer); if (bl != null) bayer.Add(Tuple.Create(bl, pt, r)); }
                }
            Color col = Print ? Color.Black : Theme.Blend(_text, _dim, 0.3);
            // the brightest stars are named before the constellations, the others after them
            _labelJobs.Add(Tuple.Create(1, (Action)(() => { using (var f = Font(6f)) foreach (var l in Enumerable.Reverse(named).Where(x => x.Item4 < 2)) Label(l.Item1, f, col, l.Item2, l.Item3 + 0.5f * _s); })));
            _labelJobs.Add(Tuple.Create(3, (Action)(() => { using (var f = Font(6f)) foreach (var l in Enumerable.Reverse(named).Where(x => x.Item4 >= 2)) Label(l.Item1, f, col, l.Item2, l.Item3 + 0.5f * _s); })));
            _labelJobs.Add(Tuple.Create(5, (Action)(() => { using (var f = Font(5f)) foreach (var l in Enumerable.Reverse(bayer)) Label(l.Item1, f, _dim, l.Item2, l.Item3 + 0.3f * _s); })));
        }

        private void DrawSolarSystem()
        {
            var g = _g;
            var p = Palette;
            var planets = new List<Tuple<string, PointF, float>>();
            Color planetCol = Print ? Color.Black : p.Planet;
            foreach (var b in new[] { Body.Mercury, Body.Venus, Body.Mars, Body.Jupiter, Body.Saturn, Body.Uranus, Body.Neptune })
            {
                var pl = _ctx.BodyPlace(b);
                PlanetPhysics.Fill(b, pl, _ctx.JdTT);
                PointF pt;
                if (pl.Horizontal.Alt < 0 || !ToPage(pl.Horizontal, out pt)) continue;
                float r = (float)AstroMath.Clamp(1.55 - 0.15 * (double.IsNaN(pl.Magnitude) ? 6 : pl.Magnitude), 0.8, 2.1) * _s;
                using (var halo = new SolidBrush(_sky)) g.FillEllipse(halo, pt.X - r - 0.3f * _s, pt.Y - r - 0.3f * _s, 2 * r + 0.6f * _s, 2 * r + 0.6f * _s);
                using (var br = new SolidBrush(planetCol)) g.FillEllipse(br, pt.X - r, pt.Y - r, 2 * r, 2 * r);
                if (b == Body.Saturn) using (var pen = Pen(planetCol, 0.25f)) g.DrawEllipse(pen, pt.X - r * 1.9f, pt.Y - r * 0.6f, r * 3.8f, r * 1.2f);
                Hits.Add(new Hit { P = pt, R = r + 0.8f * _s, O = new SelectedObject { Kind = SelectedKind.Planet, Name = b.ToString(), Planet = b, RaDeg = pl.Astrometric.RaHours * 15, DecDeg = pl.Astrometric.DecDeg, Mag = pl.Magnitude, SizeArcmin = pl.DiameterArcsec / 60 } });
                planets.Add(Tuple.Create(b.ToString(), pt, b == Body.Saturn ? r * 1.9f : r));
            }
            var sun = _ctx.BodyPlace(Body.Sun);
            {
                PointF pt;
                if (sun.Horizontal.Alt > -0.01 && ToPage(sun.Horizontal, out pt))
                {
                    float r = 2.4f * _s;
                    using (var br = new SolidBrush(Print ? Color.White : p.Sun)) g.FillEllipse(br, pt.X - r, pt.Y - r, 2 * r, 2 * r);
                    using (var pen = Pen(Print ? Color.Black : p.Sun, 0.3f))
                    {
                        g.DrawEllipse(pen, pt.X - r, pt.Y - r, 2 * r, 2 * r);
                        for (int k = 0; k < 8; k++)
                        {
                            double a = k * Math.PI / 4;
                            g.DrawLine(pen, pt.X + (float)Math.Cos(a) * r * 1.3f, pt.Y + (float)Math.Sin(a) * r * 1.3f, pt.X + (float)Math.Cos(a) * r * 1.7f, pt.Y + (float)Math.Sin(a) * r * 1.7f);
                        }
                    }
                    Hits.Add(new Hit { P = pt, R = r, O = new SelectedObject { Kind = SelectedKind.Planet, Name = "Sun", Planet = Body.Sun, RaDeg = sun.Astrometric.RaHours * 15, DecDeg = sun.Astrometric.DecDeg, Mag = -26.7 } });
                    planets.Add(Tuple.Create("Sun", pt, r * 1.7f));
                }
            }
            var moon = _ctx.BodyPlace(Body.Moon);
            {
                PointF pt;
                if (moon.Horizontal.Alt > -0.01 && ToPage(moon.Horizontal, out pt))
                {
                    float r = 2.3f * _s;
                    // the lit side faces the Sun along the sky: a step from the Moon towards it, seen on the page
                    Func<Horizontal, Vec3> vec = h => Vec3.FromSpherical(h.Az, h.Alt);
                    Vec3 m = vec(moon.Horizontal), s = vec(sun.Horizontal);
                    var towards = (s - m * Vec3.Dot(m, s)).Normalized();
                    double az, alt;
                    (m + towards * 0.01).Normalized().ToSpherical(out az, out alt);
                    PointF step;
                    double angle = ToPage(az, alt, out step) ? Math.Atan2(step.X - pt.X, -(step.Y - pt.Y)) * AstroMath.Rad2Deg : 0;
                    DrawMoonPhase(pt, r, moon.IlluminatedFraction, (float)angle);
                    Hits.Add(new Hit { P = pt, R = r, O = new SelectedObject { Kind = SelectedKind.Planet, Name = "Moon", Planet = Body.Moon, RaDeg = moon.Astrometric.RaHours * 15, DecDeg = moon.Astrometric.DecDeg } });
                    planets.Add(Tuple.Create("Moon", pt, r));
                }
            }
            // the Sun, Moon and planets are named first, so nothing pushes their names away
            _labelJobs.Add(Tuple.Create(0, (Action)(() => { using (var f = Font(6.5f, FontStyle.Bold)) foreach (var l in planets) Label(l.Item1, f, Print ? Color.Black : (l.Item1 == "Moon" ? p.Moon : l.Item1 == "Sun" ? p.Sun : p.Planet), l.Item2, l.Item3 + 0.6f * _s); })));
        }

        // the lit part of a disc, its bright limb turned towards brightLimbDeg (clockwise from up on the page)
        private void DrawMoonPhase(PointF c, float r, double k, float brightLimbDeg)
        {
            var g = _g;
            var st = g.Save();
            g.TranslateTransform(c.X, c.Y);
            g.RotateTransform(brightLimbDeg);
            Color lit = Print ? Color.White : Palette.Moon, dark = Print ? Color.FromArgb(150, 150, 150) : Theme.Blend(_sky, Palette.Moon, 0.15);
            using (var b = new SolidBrush(dark)) g.FillEllipse(b, -r, -r, 2 * r, 2 * r);
            using (var path = new GraphicsPath())
            {
                path.AddArc(-r, -r, 2 * r, 2 * r, 180, 180);
                float e = (float)(r * Math.Abs(2 * k - 1));
                if (k >= 0.5) path.AddArc(-r, -e, 2 * r, Math.Max(0.01f, 2 * e), 0, 180);
                else path.AddArc(-r, -e, 2 * r, Math.Max(0.01f, 2 * e), 0, -180);
                path.CloseFigure();
                using (var b = new SolidBrush(lit)) g.FillPath(b, path);
            }
            using (var pen = Pen(Print ? Color.Black : Palette.Moon, 0.2f)) g.DrawEllipse(pen, -r, -r, 2 * r, 2 * r);
            g.Restore(st);
        }

        private void DrawHighlight()
        {
            var h = AltAz(Highlight.RaDeg * AstroMath.Deg2Rad, Highlight.DecDeg * AstroMath.Deg2Rad);
            PointF pt;
            if (h.Alt < 0 || !ToPage(h, out pt)) return;
            float r = 3 * _s, q = r / 2;
            using (var pen = Pen(Palette.Selection, 0.35f))
                foreach (var sx in new[] { -1, 1 })
                    foreach (var sy in new[] { -1, 1 })
                    {
                        _g.DrawLine(pen, pt.X + sx * r, pt.Y + sy * r, pt.X + sx * q, pt.Y + sy * r);
                        _g.DrawLine(pen, pt.X + sx * r, pt.Y + sy * r, pt.X + sx * r, pt.Y + sy * q);
                    }
        }

        /// <summary>
        /// A label turned to the zenith beside an object (to its right, left, outside or inside, whichever is free), or
        /// centred on its point; left out when it would overlap another label or reach past the horizon.
        /// </summary>
        private bool Label(string text, Font f, Color c, PointF at, float gap, bool centred = false)
        {
            var size = _g.MeasureString(text, f, PointF.Empty, _sf);
            float w = size.Width, h = size.Height, pad = 0.25f * _s;
            float angle = LabelAngle(at);
            double a = angle * AstroMath.Deg2Rad, ca = Math.Cos(a), sa = Math.Sin(a);
            // a centred name may move a little round its point to find room
            var offsets = centred ? new[] { 0f, -1.3f, 1.3f, -2.6f, 2.6f }.SelectMany(dy => new[] { 0f, -0.4f, 0.4f }.Select(dx => new PointF(-w / 2 + dx * w, -h / 2 + dy * h))).ToArray()
                : new[] { new PointF(gap, -h / 2), new PointF(-gap - w, -h / 2), new PointF(-w / 2, gap), new PointF(-w / 2, -gap - h) };
            foreach (var o in offsets)
            {
                var box = new[] { new PointF(o.X - pad, o.Y - pad), new PointF(o.X + w + pad, o.Y - pad), new PointF(o.X + w + pad, o.Y + h + pad), new PointF(o.X - pad, o.Y + h + pad) }
                    .Select(q => new PointF((float)(at.X + q.X * ca - q.Y * sa), (float)(at.Y + q.X * sa + q.Y * ca))).ToArray();
                if (box.Any(q => Math.Sqrt((q.X - Centre.X) * (q.X - Centre.X) + (q.Y - Centre.Y) * (q.Y - Centre.Y)) > Radius - 0.3f)) continue;
                if (LabelBoxes.Any(b => Overlap(b, box))) continue;
                LabelBoxes.Add(box); LabelTexts.Add(text);
                var st = _g.Save();
                _g.TranslateTransform(at.X, at.Y);
                _g.RotateTransform(angle);
                using (var b = new SolidBrush(c)) _g.DrawString(text, f, b, o.X, o.Y, _sf);
                _g.Restore(st);
                return true;
            }
            return false;
        }

        /// <summary>Whether two convex quadrilaterals overlap (separating axis test).</summary>
        public static bool Overlap(PointF[] a, PointF[] b) => !Separated(a, b) && !Separated(b, a);

        private static bool Separated(PointF[] a, PointF[] b)
        {
            for (int i = 0; i < a.Length; i++)
            {
                var p = a[i]; var q = a[(i + 1) % a.Length];
                double nx = q.Y - p.Y, ny = p.X - q.X;
                double amin = double.MaxValue, amax = double.MinValue, bmin = double.MaxValue, bmax = double.MinValue;
                foreach (var v in a) { double d = v.X * nx + v.Y * ny; amin = Math.Min(amin, d); amax = Math.Max(amax, d); }
                foreach (var v in b) { double d = v.X * nx + v.Y * ny; bmin = Math.Min(bmin, d); bmax = Math.Max(bmax, d); }
                if (amax < bmin || bmax < amin) return true;
            }
            return false;
        }

        // ------------------------------------------------------------------ directions round the edge

        private void DrawRing()
        {
            var g = _g;
            Color col = Print ? Color.Black : Palette.Horizon;
            using (var pen = Pen(col, 0.2f))
                for (int az = 0; az < 360; az += 5)
                {
                    float len = (az % 45 == 0 ? 2.2f : az % 15 == 0 ? 1.4f : 0.8f) * _s;
                    double a = az * AstroMath.Deg2Rad;
                    float sx = -(float)Math.Sin(a), sy = -(float)Math.Cos(a);
                    g.DrawLine(pen, Centre.X + sx * Radius, Centre.Y + sy * Radius, Centre.X + sx * (Radius + len), Centre.Y + sy * (Radius + len));
                }
            // the names of the directions read upright with that edge held down, like everything near that horizon
            string[] names = { "NORTH", "NE", "EAST", "SE", "SOUTH", "SW", "WEST", "NW" };
            using (var big = Font(9f, FontStyle.Bold))
            using (var small = Font(6.5f, FontStyle.Bold))
                for (int i = 0; i < 8; i++)
                {
                    var f = i % 2 == 0 ? big : small;
                    var sz = g.MeasureString(names[i], f, PointF.Empty, _sf);
                    var st = g.Save();
                    g.TranslateTransform(Centre.X, Centre.Y);
                    g.RotateTransform(-i * 45 - 180);
                    using (var b = new SolidBrush(i == 0 && !Print ? Palette.Danger : col)) g.DrawString(names[i], f, b, -sz.Width / 2, Radius + 2.8f * _s, _sf);
                    g.Restore(st);
                }
        }

        // ------------------------------------------------------------------ the night's details

        private class Block
        {
            public string Heading;
            public List<string[]> Rows = new List<string[]>();
            public Func<float, float> ExtraHeight;
            public Action<float, float, float> DrawExtra;
        }

        private string _infoKey;
        private List<Block> _info;

        private static string Hm(Observer site, AstroTime? t) => t.HasValue ? site.ToLocal(t.Value.ToDateTimeUtc()).ToString("HH:mm", Inv) : "—";

        /// <summary>
        /// The same stars stand in the same places about four minutes earlier each day: the chart's sky on other dates near
        /// it (the 1st and 15th of the months round it), as local times.
        /// </summary>
        public static List<Tuple<DateTime, DateTime>> SameSkyTimes(Observer site, AstroTime time)
        {
            var local = site.ToLocal(time.ToDateTimeUtc());
            var dates = new List<DateTime> { local.Date };
            for (int m = -1; m <= 1; m++)
                foreach (var day in new[] { 1, 15 })
                {
                    var d = new DateTime(local.Year, local.Month, day).AddMonths(m);
                    if (d != local.Date && Math.Abs((d - local.Date).TotalDays) <= 32) dates.Add(d);
                }
            var res = new List<Tuple<DateTime, DateTime>>();
            foreach (var d in dates.OrderBy(x => Math.Abs((x - local.Date).TotalDays)).Take(5).OrderBy(x => x))
            {
                double n = (d - local.Date).TotalDays;
                var utc = time.ToDateTimeUtc().AddDays(n * 0.99726957);
                res.Add(Tuple.Create(d, site.ToLocal(utc)));
            }
            return res;
        }

        private List<Block> BuildInfo()
        {
            string key = string.Format(Inv, "{0}|{1}|{2}|{3:0.0}|{4}", Site.Name, Site.LatitudeDeg, Site.LongitudeDeg, Time.JdUtc * 1440, MagLimit);
            if (key == _infoKey) return _info;
            var blocks = new List<Block>();
            var site = Site;
            var local = site.ToLocal(Time.ToDateTimeUtc());
            var noon = AstroTime.FromDateTime(site.ToUtc(local.Date.AddHours(local.Hour < 12 ? -12 : 12)));
            var tw = AstroEvents.Twilight(site, noon);

            var same = new Block { Heading = "The same sky" };
            foreach (var t in SameSkyTimes(site, Time))
                same.Rows.Add(new[] { t.Item1.ToString("d MMM", Inv), t.Item2.ToString("HH:mm", Inv) + (t.Item2.Date != t.Item1 ? " (next day)" : "") });
            blocks.Add(same);

            var sun = new Block { Heading = "Sun" };
            sun.Rows.Add(new[] { "Sets", Hm(site, tw.SunSet) });
            if (tw.AstronomicalDusk.HasValue) { sun.Rows.Add(new[] { "Dark from", Hm(site, tw.AstronomicalDusk) }); sun.Rows.Add(new[] { "Dark until", Hm(site, tw.AstronomicalDawn) }); }
            else sun.Rows.Add(new[] { "Darkness", tw.DarkHours >= 24 ? "all day" : "none, twilight all night" });
            sun.Rows.Add(new[] { "Rises", Hm(site, tw.SunRise) });
            blocks.Add(sun);

            var moonBlock = new Block { Heading = "Moon" };
            var mp = _ctx.BodyPlace(Body.Moon);
            double el = AstroEvents.MoonSunElongation(_ctx.JdTT);
            string phase = el < 10 || el >= 350 ? "New" : el < 80 ? "Waxing crescent" : el < 100 ? "First quarter" : el < 170 ? "Waxing gibbous" : el < 190 ? "Full" : el < 260 ? "Waning gibbous" : el < 280 ? "Last quarter" : "Waning crescent";
            moonBlock.Rows.Add(new[] { phase, string.Format(Inv, "{0:0}% lit", mp.IlluminatedFraction * 100) });
            var mrs = AstroEvents.BodyRiseSet(Body.Moon, site, noon);
            moonBlock.Rows.Add(new[] { "Rises", mrs.AlwaysUp ? "up all day" : mrs.NeverUp ? "does not rise" : Hm(site, mrs.Rise) });
            moonBlock.Rows.Add(new[] { "Sets", mrs.AlwaysUp || mrs.NeverUp ? "—" : Hm(site, mrs.Set) });
            moonBlock.Rows.Add(new[] { "In", CatalogManager.Instance.ConstellationAt(mp.Astrometric.Ra, mp.Astrometric.Dec)?.Name ?? "—" });
            blocks.Add(moonBlock);

            // planets above the horizon at some time between sunset and sunrise
            var planets = new Block { Heading = "Planets tonight" };
            double n0 = tw.SunSet?.JdUtc ?? noon.JdUtc + 0.25, n1 = tw.SunRise?.JdUtc ?? noon.JdUtc + 0.75;
            if (n1 < n0) n1 = n0;
            foreach (var b in new[] { Body.Mercury, Body.Venus, Body.Mars, Body.Jupiter, Body.Saturn, Body.Uranus, Body.Neptune })
            {
                bool up = false;
                for (int k = 0; k <= 16 && !up; k++) up = AstroEvents.BodyAltitude(b, site, new AstroTime(n0 + (n1 - n0) * k / 16)) > 0;
                if (!up) continue;
                var pl = _ctx.BodyPlace(b);
                PlanetPhysics.Fill(b, pl, _ctx.JdTT);
                var rs = AstroEvents.BodyRiseSet(b, site, noon);
                string when = rs.AlwaysUp ? "up all night" : string.Format("↑{0} ↓{1}", Hm(site, rs.Rise), Hm(site, rs.Set));
                string con = CatalogManager.Instance.ConstellationAt(pl.Astrometric.Ra, pl.Astrometric.Dec)?.Abbr ?? "";
                planets.Rows.Add(new[] { b.ToString(), string.Format(Inv, "{0:0.0}  {1}  {2}", pl.Magnitude, con, when) });
            }
            if (planets.Rows.Count == 0) planets.Rows.Add(new[] { "None", "" });
            blocks.Add(planets);

            blocks.Add(KeyBlock());

            // bright Messier objects well up at the chart's time
            var dso = new Block { Heading = "Worth a look" };
            var list = new List<DeepSkyObject>();
            var zen = _ctx.HorizontalToIcrs(0, AstroMath.HalfPi);
            CatalogManager.Instance.DeepSky.Cone(zen.Ra, zen.Dec, 65 * AstroMath.Deg2Rad, 99, list);
            foreach (var o in list.Where(x => x.Messier != null && !double.IsNaN(x.Mag) && x.Mag <= 7.5).OrderBy(x => x.Mag).Take(8))
                dso.Rows.Add(new[] { o.Messier + (o.Name != null ? " " + o.Name : ""), string.Format(Inv, "{0} {1:0.0}", DeepSkyObject.TypeLabel(o.Type).ToLowerInvariant(), o.Mag) });
            if (dso.Rows.Count > 0) blocks.Add(dso);

            var showers = new Block { Heading = "Meteor showers" };
            int doy = local.DayOfYear;
            foreach (var ms in MeteorShowers.Major)
            {
                bool active = ms.ActiveFromDay <= ms.ActiveToDay ? doy >= ms.ActiveFromDay && doy <= ms.ActiveToDay : doy >= ms.ActiveFromDay || doy <= ms.ActiveToDay;
                if (!active) continue;
                var peak = ms.PeakIn(local.Year);
                if (Math.Abs(peak.JdUtc - Time.JdUtc) > 200) peak = ms.PeakIn(local.Year + (peak.JdUtc < Time.JdUtc ? 1 : -1));
                showers.Rows.Add(new[] { ms.Name, string.Format(Inv, "peak {0:d MMM}, ZHR {1:0}", site.ToLocal(peak.ToDateTimeUtc()), ms.Zhr) });
            }
            if (showers.Rows.Count > 0) blocks.Add(showers);

            _infoKey = key;
            _info = blocks;
            return blocks;
        }

        // the symbols, with star dots at the sizes drawn for each magnitude
        private Block KeyBlock()
        {
            var items = new List<Tuple<string, Action<PointF, float>>>
            {
                Tuple.Create<string, Action<PointF, float>>("Galaxy", (pt, r) => DrawDsoSymbol(ObjectType.Galaxy, pt, r, Palette.Galaxy)),
                Tuple.Create<string, Action<PointF, float>>("Open cluster", (pt, r) => DrawDsoSymbol(ObjectType.OpenCluster, pt, r, Palette.Cluster)),
                Tuple.Create<string, Action<PointF, float>>("Globular cluster", (pt, r) => DrawDsoSymbol(ObjectType.GlobularCluster, pt, r, Palette.Cluster)),
                Tuple.Create<string, Action<PointF, float>>("Nebula", (pt, r) => DrawDsoSymbol(ObjectType.EmissionNebula, pt, r, Palette.Nebula)),
                Tuple.Create<string, Action<PointF, float>>("Planetary nebula", (pt, r) => DrawDsoSymbol(ObjectType.PlanetaryNebula, pt, r, Palette.PlanetaryNebula)),
                Tuple.Create<string, Action<PointF, float>>("Planet", (pt, r) => { using (var b = new SolidBrush(Print ? Color.Black : Palette.Planet)) _g.FillEllipse(b, pt.X - r * 0.8f, pt.Y - r * 0.8f, r * 1.6f, r * 1.6f); }),
                Tuple.Create<string, Action<PointF, float>>("Ecliptic", (pt, r) => { using (var pen = Pen(Palette.Ecliptic, 0.25f, DashStyle.Dash)) _g.DrawLine(pen, pt.X - r * 1.6f, pt.Y, pt.X + r * 1.6f, pt.Y); }),
                Tuple.Create<string, Action<PointF, float>>("Constellation", (pt, r) => { using (var pen = Pen(Palette.ConstellationLine, 0.22f)) _g.DrawLine(pen, pt.X - r * 1.6f, pt.Y + r * 0.5f, pt.X + r * 1.6f, pt.Y - r * 0.5f); }),
            };
            float row = 3.6f * _s, magRow = 7 * _s;
            return new Block
            {
                Heading = "Key",
                ExtraHeight = w => magRow + (items.Count + 1) / 2 * row,
                DrawExtra = (x, y, w) =>
                {
                    using (var f = Font(5.5f))
                    using (var tb = new SolidBrush(_text))
                    using (var halo = new SolidBrush(_sky))
                    {
                        // magnitudes from -1 to the limit, each dot as on the chart, its value below
                        int m0 = -1, m1 = (int)Math.Floor(MagLimit);
                        float step = Math.Min(7 * _s, w / (m1 - m0 + 1));
                        for (int m = m0; m <= m1; m++)
                        {
                            float cx = x + (m - m0 + 0.5f) * step, cy = y + 2.2f * _s;
                            float r = SkyChart.StarDotRadius(m, MagLimit) * 0.24f * _s;
                            using (var b = new SolidBrush(Print ? Color.Black : Palette.StarColor(0.6))) _g.FillEllipse(b, cx - r, cy - r, 2 * r, 2 * r);
                            string t = m.ToString(Inv);
                            var sz = _g.MeasureString(t, f, PointF.Empty, _sf);
                            _g.DrawString(t, f, tb, cx - sz.Width / 2, y + 4.4f * _s, _sf);
                        }
                        float colW = w / 2;
                        for (int i = 0; i < items.Count; i++)
                        {
                            float ix = x + (i % 2) * colW, iy = y + magRow + (i / 2) * row;
                            items[i].Item2(new PointF(ix + 2.4f * _s, iy + row / 2), 1.2f * _s);
                            _g.DrawString(items[i].Item1, f, tb, ix + 5.6f * _s, iy + row / 2 - f.Size * 0.65f, _sf);
                        }
                    }
                }
            };
        }

        private void DrawInfo()
        {
            var area = InfoArea;
            if (area.Width < 20 || area.Height < 15) return;
            var blocks = BuildInfo();
            float colMin = 54 * _s, gap = 5 * _s;
            int cols = Math.Max(1, (int)((area.Width + gap) / (colMin + gap)));
            float colW = (area.Width - (cols - 1) * gap) / cols;
            using (var hf = Font(7.5f, FontStyle.Bold))
            using (var bf = Font(6.5f))
            using (var tb = new SolidBrush(_text))
            using (var db = new SolidBrush(_dim))
            using (var rule = Pen(Print ? Color.FromArgb(160, 160, 160) : Palette.Border, 0.15f))
            {
                float headH = hf.Size * 1.55f, lineH = bf.Size * 1.4f;
                int col = 0;
                float y = area.Top;
                foreach (var b in blocks)
                {
                    float h = headH + b.Rows.Count * lineH + (b.ExtraHeight != null ? b.ExtraHeight(colW) : 0) + 2.5f * _s;
                    if (y + h > area.Bottom && y > area.Top) { col++; y = area.Top; }
                    if (col >= cols || y + h > area.Bottom + 0.5f) continue;
                    float x = area.Left + col * (colW + gap);
                    InfoBlocks.Add(new RectangleF(x, y, colW, h));
                    InfoHeadings.Add(b.Heading);
                    _g.DrawString(b.Heading, hf, tb, x, y, _sf);
                    _g.DrawLine(rule, x, y + headH - 0.8f * _s, x + colW, y + headH - 0.8f * _s);
                    float ry = y + headH;
                    foreach (var r in b.Rows)
                    {
                        var vs = _g.MeasureString(r[1], bf, PointF.Empty, _sf);
                        _g.DrawString(r[1], bf, db, x + colW - vs.Width, ry, _sf);
                        _g.DrawString(Fit(r[0], bf, colW - vs.Width - 2 * _s), bf, tb, x, ry, _sf);
                        InfoLines.Add(b.Heading + ": " + r[0] + " " + r[1]);
                        ry += lineH;
                    }
                    b.DrawExtra?.Invoke(x, ry + 0.5f * _s, colW);
                    y += h;
                }
            }
        }

        // text cut with an ellipsis to a width
        private string Fit(string s, Font f, float width)
        {
            if (_g.MeasureString(s, f, PointF.Empty, _sf).Width <= width) return s;
            while (s.Length > 1 && _g.MeasureString(s + "…", f, PointF.Empty, _sf).Width > width) s = s.Substring(0, s.Length - 1);
            return s.TrimEnd() + "…";
        }

        // ------------------------------------------------------------------ paper

        /// <summary>The printer's own paper of this size (any name: "A3+", "Super A3", "13 x 19 in"), or a custom one.</summary>
        public static PaperSize FindPaperSize(PrinterSettings printer, ChartPaper paper)
        {
            foreach (PaperSize s in printer.PaperSizes)
            {
                double w = Math.Min(s.Width, s.Height) * 0.254, h = Math.Max(s.Width, s.Height) * 0.254;
                if (Math.Abs(w - paper.WidthMm) < 3 && Math.Abs(h - paper.HeightMm) < 3) return s;
            }
            return new PaperSize(paper.Name, (int)Math.Round(paper.WidthMm / 0.254), (int)Math.Round(paper.HeightMm / 0.254));
        }

        /// <summary>
        /// Prints the page in black on white within the margins, as vectors at the printer's own resolution, laid out for
        /// whatever paper the printer reports.
        /// </summary>
        public void PrintPage(object sender, PrintPageEventArgs e)
        {
            Print = true;
            Palette = Palette.Print();
            var g = e.Graphics;
            g.PageUnit = GraphicsUnit.Millimeter;
            // the margins are measured from the paper's edge; a printer draws from the corner it can reach, a preview from the edge
            var doc = sender as PrintDocument;
            bool preview = doc?.PrintController != null && doc.PrintController.IsPreview;
            float hx = preview ? 0 : e.PageSettings.HardMarginX, hy = preview ? 0 : e.PageSettings.HardMarginY;
            var mb = e.MarginBounds;
            Render(g, new RectangleF((mb.X - hx) * 0.254f, (mb.Y - hy) * 0.254f, mb.Width * 0.254f, mb.Height * 0.254f));
        }

        /// <summary>The page as an image at a resolution, in black on white with 8 mm margins.</summary>
        public Bitmap RenderImage(ChartPaper paper, bool landscape, int dpi)
        {
            Print = true;
            Palette = Palette.Print();
            var px = paper.Pixels(dpi, landscape);
            var bmp = new Bitmap(px.Width, px.Height, PixelFormat.Format24bppRgb);
            bmp.SetResolution(dpi, dpi);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.White);
                float k = dpi / 25.4f, m = 8;
                g.ScaleTransform(k, k);
                Render(g, new RectangleF(m, m, px.Width / k - 2 * m, px.Height / k - 2 * m));
            }
            return bmp;
        }
    }

    /// <summary>The star chart page on screen: fitted to the control, in the app's colours, with objects to click.</summary>
    public class StarChartView : Control, IThemed
    {
        public readonly StarChartPage Page = new StarChartPage { Print = false };
        public AstroTime? FixedTime;
        public event Action<SelectedObject> ObjectClicked;
        private float _pxPerMm = 96 / 25.4f;
        private const float MarginMm = 5;

        public StarChartView()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            TabStop = true;
        }

        public ChartLayers Layers { get { return Page.Layers; } set { Page.Layers = value; } }

        public void ApplyTheme(Palette p) => Invalidate();

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var p = Theme.Current;
            g.Clear(p.ChartBackground);
            _pxPerMm = g.DpiX / 25.4f;
            Page.Site = AppState.Site;
            Page.Time = FixedTime ?? AppState.Now;
            Page.Palette = p;
            Page.Refraction = AppState.Settings.UseRefraction;
            var st = g.Save();
            g.ScaleTransform(_pxPerMm, _pxPerMm);
            Page.Render(g, new RectangleF(MarginMm, MarginMm, Width / _pxPerMm - 2 * MarginMm, Height / _pxPerMm - 2 * MarginMm));
            g.Restore(st);
            if (Focused && ShowFocusCues) using (var pen = new Pen(p.Focus)) g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }

        /// <summary>The object under a point of the control, or null.</summary>
        public SelectedObject HitTest(Point pt)
        {
            float x = pt.X / _pxPerMm, y = pt.Y / _pxPerMm;
            StarChartPage.Hit best = null;
            double bd = double.MaxValue;
            foreach (var h in Page.Hits)
            {
                double d = Math.Sqrt((h.P.X - x) * (h.P.X - x) + (h.P.Y - y) * (h.P.Y - y));
                if (d <= h.R + 0.5 && d < bd) { bd = d; best = h; }
            }
            return best?.O;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            Focus();
            if (e.Button == MouseButtons.Left)
            {
                var o = HitTest(e.Location);
                if (o != null) { Page.Highlight = o; ObjectClicked?.Invoke(o); Invalidate(); }
            }
            base.OnMouseUp(e);
        }
    }
}
