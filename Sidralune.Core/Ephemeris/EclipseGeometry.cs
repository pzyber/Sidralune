using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Sidralune.Core.Coordinates;
using Sidralune.Core.Mathematics;
using Sidralune.Core.Time;

namespace Sidralune.Core.Ephemeris
{
    /// <summary>A place on the Earth: geodetic latitude and east longitude in degrees.</summary>
    public struct GeoPoint
    {
        public double Lat, Lon;
        public GeoPoint(double lat, double lon) { Lat = lat; Lon = lon; }
        public override string ToString() => Lat.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) + ", " + Lon.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>The kinds of line on an eclipse map.</summary>
    public enum EclipseLineKind
    {
        /// <summary>Solar: the shadow axis on the Earth.</summary>
        CentralLine,
        /// <summary>Solar: the northern and southern edges of the path of totality or annularity.</summary>
        CentralLimit,
        /// <summary>Solar: where the Moon just touches the Sun at maximum (the edge of the partial eclipse).</summary>
        PenumbralLimit,
        /// <summary>Solar: where the eclipse begins or ends at sunrise or sunset.</summary>
        ContactAtHorizon,
        /// <summary>Solar: where maximum is at sunrise or sunset.</summary>
        MaximumAtHorizon,
        /// <summary>Solar: equal share of the Sun covered at maximum (Level).</summary>
        Obscuration,
        /// <summary>Lunar: where the Moon is on the horizon at a contact (Label).</summary>
        MoonHorizon
    }

    /// <summary>A line on an eclipse map, in parts that are each drawn as one polyline.</summary>
    public class EclipseLine
    {
        public EclipseLineKind Kind;
        public List<List<GeoPoint>> Parts = new List<List<GeoPoint>>();
        /// <summary>Obscuration: the share of the Sun covered (0–1).</summary>
        public double Level;
        /// <summary>Lunar: the contact (P1, U1, U2, U3, U4, P4).</summary>
        public string Label;
    }

    /// <summary>Everything drawn on the eclipse map for one eclipse.</summary>
    public class EclipseMapData
    {
        public bool Solar;
        public AstroTime Greatest;
        /// <summary>Solar: the elements behind the lines, for the circumstances at a clicked place.</summary>
        public BesselianElements Elements;
        public List<EclipseLine> Lines = new List<EclipseLine>();
        /// <summary>Solar: greatest eclipse on the Earth (where the axis passes closest to the Earth's centre).</summary>
        public GeoPoint? GreatestPoint;
        /// <summary>Lunar: where the Moon is overhead at each contact, with its label.</summary>
        public List<Tuple<string, GeoPoint>> SubLunar = new List<Tuple<string, GeoPoint>>();
        /// <summary>
        /// Lunar: 1° cells from latitude 89.5° down and longitude −179.5° east: 2 the whole eclipse with the Moon up, 1 part
        /// of it, 0 none.
        /// </summary>
        public byte[,] Visibility;
        /// <summary>The part of the world worth showing first (west, south, east, north in degrees; east can exceed 180).</summary>
        public double West = -180, South = -90, East = 180, North = 90;
    }

    /// <summary>The lines of solar and lunar eclipse maps.</summary>
    public static class EclipseGeometry
    {
        private const double EarthKm = 6371.0;

        /// <summary>The map of a solar eclipse from its greatest eclipse.</summary>
        public static EclipseMapData Solar(AstroTime greatest, CancellationToken ct = default(CancellationToken))
        {
            var be = BesselianElements.Compute(greatest);
            var map = new EclipseMapData { Solar = true, Greatest = greatest, Elements = be };
            double gl, gn;
            if (be.AxisOnEarth(be.Hours(greatest.JdTT), out gl, out gn)) map.GreatestPoint = new GeoPoint(gl, gn);

            // the central line every 30 seconds, and the edges of the path across it
            var central = new EclipseLine { Kind = EclipseLineKind.CentralLine };
            var north = new EclipseLine { Kind = EclipseLineKind.CentralLimit };
            var south = new EclipseLine { Kind = EclipseLineKind.CentralLimit };
            List<GeoPoint> part = null;
            for (double t = -BesselianElements.Span; t <= BesselianElements.Span + 1e-9; t += 1 / 120.0)
            {
                double lat, lon;
                bool on = be.AxisOnEarth(t, out lat, out lon) && be.Local(lat, lon).Kind != 0;
                if (on) { if (part == null) { part = new List<GeoPoint>(); central.Parts.Add(part); } part.Add(new GeoPoint(lat, lon)); }
                else part = null;
            }
            foreach (var cp in central.Parts)
            {
                ct.ThrowIfCancellationRequested();
                List<GeoPoint> np = null, sp = null;
                for (int i = 0; i < cp.Count; i++)
                {
                    var a = cp[Math.Max(0, i - 1)]; var b = cp[Math.Min(cp.Count - 1, i + 1)];
                    if (cp.Count < 2) break;
                    double bearing = Bearing(a, b);
                    var n = Edge(be, cp[i], bearing - 90);
                    var s = Edge(be, cp[i], bearing + 90);
                    if (n.HasValue) { if (np == null) { np = new List<GeoPoint>(); north.Parts.Add(np); } np.Add(n.Value); } else np = null;
                    if (s.HasValue) { if (sp == null) { sp = new List<GeoPoint>(); south.Parts.Add(sp); } sp.Add(s.Value); } else sp = null;
                }
            }
            foreach (var l in new[] { central, north, south }) l.Parts = SplitAtDateLine(l.Parts);
            if (central.Parts.Count > 0) { north.Parts.AddRange(south.Parts); map.Lines.Add(north); map.Lines.Add(central); }

            // the penumbral limits, the sunrise and sunset curves and the obscuration lines from a 0.5° grid
            const double step = 0.5;
            int rows = (int)(180 / step) + 1, cols = (int)(360 / step) + 1;
            var pen = new double[rows, cols]; var c1 = new double[rows, cols]; var c4 = new double[rows, cols];
            var mx = new double[rows, cols]; var obs = new double[rows, cols];
            double minLat = 90, maxLat = -90;
            var lons = new List<double>();
            for (int r = 0; r < rows; r++)
            {
                ct.ThrowIfCancellationRequested();
                double lat = 90 - r * step;
                for (int c = 0; c < cols; c++)
                {
                    double lon = -180 + c * step;
                    var l = be.Local(lat, lon);
                    double zeta = Math.Sin(l.Maximum.Altitude * AstroMath.Deg2Rad);
                    pen[r, c] = zeta > -0.05 ? l.PenumbraRadius - l.AxisDistance : double.NaN;
                    c1[r, c] = l.Covers && l.C1 != null ? l.C1.Altitude - Events.SunStandardAltitude : double.NaN;
                    c4[r, c] = l.Covers && l.C4 != null ? l.C4.Altitude - Events.SunStandardAltitude : double.NaN;
                    mx[r, c] = l.Covers ? l.Maximum.Altitude - Events.SunStandardAltitude : double.NaN;
                    obs[r, c] = l.Covers && l.Maximum.SunUp ? l.Obscuration : double.NaN;
                    if (l.Visible) { minLat = Math.Min(minLat, lat); maxLat = Math.Max(maxLat, lat); lons.Add(lon); }
                }
            }
            map.Lines.Add(new EclipseLine { Kind = EclipseLineKind.PenumbralLimit, Parts = Contour(pen, step, 0) });
            var horizon = new EclipseLine { Kind = EclipseLineKind.ContactAtHorizon, Parts = Contour(c1, step, 0) };
            horizon.Parts.AddRange(Contour(c4, step, 0));
            map.Lines.Add(horizon);
            map.Lines.Add(new EclipseLine { Kind = EclipseLineKind.MaximumAtHorizon, Parts = Contour(mx, step, 0) });
            foreach (double level in new[] { 0.25, 0.5, 0.75 })
                map.Lines.Add(new EclipseLine { Kind = EclipseLineKind.Obscuration, Level = level, Parts = Contour(obs, step, level) });
            if (lons.Count > 0) SetView(map, minLat, maxLat, lons);
            return map;
        }

        /// <summary>The edge of the central path from a point of the central line along a bearing (degrees), within 1000 km.</summary>
        private static GeoPoint? Edge(BesselianElements be, GeoPoint from, double bearing)
        {
            Func<double, double> inside = km => { var p = Destination(from, bearing, km); return be.Local(p.Lat, p.Lon).Centrality; };
            double a = 0, b = 0, fa = inside(0);
            if (fa <= 0) return null;
            for (double km = 10; km <= 1000; km += 10)
            {
                double fb = inside(km);
                if (fb <= 0) { b = km; break; }
                a = km;
            }
            if (b == 0) return null;
            for (int i = 0; i < 30; i++) { double m = (a + b) / 2; if (inside(m) > 0) a = m; else b = m; }
            return Destination(from, bearing, (a + b) / 2);
        }

        /// <summary>
        /// The width of the central path across from a place, and the place's distance from the central line (km); false
        /// for a partial eclipse, or a place more than a path's width from it.
        /// </summary>
        public static bool PathAt(EclipseMapData map, GeoPoint p, out double widthKm, out double fromCentralKm)
        {
            widthKm = fromCentralKm = double.NaN;
            if (map == null || !map.Solar || map.Elements == null) return false;
            List<GeoPoint> best = null; int bi = -1; double bd = double.MaxValue;
            foreach (var part in map.Lines.Where(l => l.Kind == EclipseLineKind.CentralLine).SelectMany(l => l.Parts))
                for (int i = 0; i < part.Count; i++)
                {
                    double d = Distance(p, part[i]);
                    if (d < bd) { bd = d; best = part; bi = i; }
                }
            if (best == null || best.Count < 2) return false;
            var a = best[Math.Max(0, bi - 1)]; var b = best[Math.Min(best.Count - 1, bi + 1)]; var c = best[bi];
            double bearing = Bearing(a, b);
            var n = Edge(map.Elements, c, bearing - 90);
            var s = Edge(map.Elements, c, bearing + 90);
            if (!n.HasValue || !s.HasValue) return false;
            widthKm = DistanceKm(n.Value, s.Value);
            // across the line, not to the nearest of its points (which are about half a minute of the shadow's travel apart)
            double d13 = Distance(c, p) * AstroMath.Deg2Rad, t13 = Bearing(c, p) * AstroMath.Deg2Rad, t12 = bearing * AstroMath.Deg2Rad;
            fromCentralKm = Math.Abs(Math.Asin(Math.Sin(d13) * Math.Sin(t13 - t12))) * EarthKm;
            return fromCentralKm <= widthKm;
        }

        /// <summary>The map of a lunar eclipse from its greatest eclipse: where the Moon is on the horizon at each contact.</summary>
        public static EclipseMapData Lunar(AstroTime greatest, CancellationToken ct = default(CancellationToken))
        {
            var map = new EclipseMapData { Solar = false, Greatest = greatest };
            var contacts = Events.LunarContacts(greatest);
            var circles = new List<Tuple<GeoPoint, double>>();
            foreach (var c in contacts)
            {
                ct.ThrowIfCancellationRequested();
                double radius;
                var sub = SubLunar(c.Item2, out radius);
                map.SubLunar.Add(Tuple.Create(c.Item1, sub));
                circles.Add(Tuple.Create(sub, radius));
                if (c.Item1 == "MAX") continue;
                var line = new EclipseLine { Kind = EclipseLineKind.MoonHorizon, Label = c.Item1 };
                var ring = new List<GeoPoint>();
                for (double az = 0; az <= 360; az += 2) ring.Add(Destination(sub, az, radius * AstroMath.Deg2Rad * EarthKm));
                line.Parts = SplitAtDateLine(new List<List<GeoPoint>> { ring });
                map.Lines.Add(line);
            }
            // the whole eclipse visible where the Moon is up at its first and last contact (umbral, or penumbral for a penumbral eclipse)
            var phase = contacts.Where(c => c.Item1 != "MAX").ToList();
            var first = phase.Any(c => c.Item1 == "U1") ? "U1" : "P1"; var last = phase.Any(c => c.Item1 == "U4") ? "U4" : "P4";
            map.Visibility = new byte[180, 360];
            for (int r = 0; r < 180; r++)
                for (int col = 0; col < 360; col++)
                {
                    var p = new GeoPoint(89.5 - r, -179.5 + col);
                    Func<string, bool> up = name => { int i = contacts.FindIndex(c => c.Item1 == name); return i >= 0 && Distance(p, circles[i].Item1) < circles[i].Item2; };
                    bool whole = up(first) && up(last) && up("MAX");
                    bool some = contacts.Select(c => c.Item1).Any(up);
                    map.Visibility[r, col] = (byte)(whole ? 2 : some ? 1 : 0);
                }
            return map;
        }

        /// <summary>Where the Moon is overhead at a time, and the angular radius (degrees) of the circle where it is on the horizon.</summary>
        private static GeoPoint SubLunar(AstroTime t, out double horizonRadius)
        {
            Vec3 ep, ev;
            EphemerisManager.State(Body.Earth, t.JdTDB, out ep, out ev);
            double tau = 0; Vec3 r = Vec3.Zero;
            for (int i = 0; i < 4; i++) { r = EphemerisManager.Position(Body.Moon, t.JdTDB - tau) - ep; tau = r.Length * AstroMath.LightTimeAU_days; }
            var tod = Precession.NpbMatrix(t.T) * r;
            double ra, dec;
            tod.ToSpherical(out ra, out dec);
            double hp = Math.Asin(AstroMath.EarthRadius_km / (r.Length * AstroMath.AU_km)) * AstroMath.Rad2Deg;
            // seen from the Earth's centre the Moon rises and sets at 0.7275 × parallax − 0.5667°
            horizonRadius = 90 - (0.7275 * hp - 0.5667);
            return new GeoPoint(dec * AstroMath.Rad2Deg, AstroMath.NormalizeDeg180((ra - t.Gast) * AstroMath.Rad2Deg));
        }

        private static void SetView(EclipseMapData map, double minLat, double maxLat, List<double> lons)
        {
            // the smallest longitude range holding all the places that see the eclipse, allowing for the date line
            var sorted = lons.Distinct().OrderBy(x => x).ToList();
            double gap = 0, gapEnd = sorted[0];
            for (int i = 0; i < sorted.Count; i++)
            {
                double next = i + 1 < sorted.Count ? sorted[i + 1] : sorted[0] + 360;
                if (next - sorted[i] > gap) { gap = next - sorted[i]; gapEnd = next; }
            }
            double west = gapEnd, east = gapEnd + 360 - gap;
            if (west >= 180) { west -= 360; east -= 360; }
            map.West = west - 5; map.East = east + 5;
            map.South = Math.Max(-90, minLat - 5); map.North = Math.Min(90, maxLat + 5);
        }

        // ------------------------------------------------------------------ geometry on the sphere

        public static double Bearing(GeoPoint a, GeoPoint b)
        {
            double f1 = a.Lat * AstroMath.Deg2Rad, f2 = b.Lat * AstroMath.Deg2Rad, dl = (b.Lon - a.Lon) * AstroMath.Deg2Rad;
            return Math.Atan2(Math.Sin(dl) * Math.Cos(f2), Math.Cos(f1) * Math.Sin(f2) - Math.Sin(f1) * Math.Cos(f2) * Math.Cos(dl)) * AstroMath.Rad2Deg;
        }

        public static GeoPoint Destination(GeoPoint a, double bearingDeg, double km)
        {
            double d = km / EarthKm, th = bearingDeg * AstroMath.Deg2Rad, f1 = a.Lat * AstroMath.Deg2Rad, l1 = a.Lon * AstroMath.Deg2Rad;
            double f2 = Math.Asin(Math.Sin(f1) * Math.Cos(d) + Math.Cos(f1) * Math.Sin(d) * Math.Cos(th));
            double l2 = l1 + Math.Atan2(Math.Sin(th) * Math.Sin(d) * Math.Cos(f1), Math.Cos(d) - Math.Sin(f1) * Math.Sin(f2));
            return new GeoPoint(f2 * AstroMath.Rad2Deg, AstroMath.NormalizeDeg180(l2 * AstroMath.Rad2Deg));
        }

        /// <summary>Angular distance in degrees.</summary>
        public static double Distance(GeoPoint a, GeoPoint b)
        {
            double f1 = a.Lat * AstroMath.Deg2Rad, f2 = b.Lat * AstroMath.Deg2Rad, dl = (b.Lon - a.Lon) * AstroMath.Deg2Rad;
            double h = Math.Sin((f2 - f1) / 2), k = Math.Sin(dl / 2);
            return 2 * Math.Asin(Math.Min(1, Math.Sqrt(h * h + Math.Cos(f1) * Math.Cos(f2) * k * k))) * AstroMath.Rad2Deg;
        }

        /// <summary>Distance in kilometres on a sphere of the Earth's mean radius.</summary>
        public static double DistanceKm(GeoPoint a, GeoPoint b) => Distance(a, b) * AstroMath.Deg2Rad * EarthKm;

        /// <summary>Lines broken where they cross the date line, so none is drawn across the whole map.</summary>
        private static List<List<GeoPoint>> SplitAtDateLine(List<List<GeoPoint>> parts)
        {
            var res = new List<List<GeoPoint>>();
            foreach (var p in parts)
            {
                var cur = new List<GeoPoint>();
                for (int i = 0; i < p.Count; i++)
                {
                    if (cur.Count > 0 && Math.Abs(p[i].Lon - cur[cur.Count - 1].Lon) > 180) { if (cur.Count > 1) res.Add(cur); cur = new List<GeoPoint>(); }
                    cur.Add(p[i]);
                }
                if (cur.Count > 1) res.Add(cur);
            }
            return res;
        }

        // ------------------------------------------------------------------ contours

        /// <summary>
        /// The lines where a grid (rows from latitude 90° down, columns from longitude −180° east, both every step degrees)
        /// equals level, by marching squares; cells with a missing (NaN) corner are left out.
        /// </summary>
        public static List<List<GeoPoint>> Contour(double[,] f, double step, double level)
        {
            int rows = f.GetLength(0), cols = f.GetLength(1);
            var segs = new List<Tuple<GeoPoint, GeoPoint>>();
            Func<int, int, GeoPoint> at = (r, c) => new GeoPoint(90 - r * step, -180 + c * step);
            Func<GeoPoint, GeoPoint, double, double, GeoPoint> cut = (p, q, fp, fq) =>
            {
                double k = (level - fp) / (fq - fp);
                return new GeoPoint(p.Lat + k * (q.Lat - p.Lat), p.Lon + k * (q.Lon - p.Lon));
            };
            for (int r = 0; r + 1 < rows; r++)
                for (int c = 0; c + 1 < cols; c++)
                {
                    double a = f[r, c], b = f[r, c + 1], cc = f[r + 1, c + 1], d = f[r + 1, c];
                    if (double.IsNaN(a) || double.IsNaN(b) || double.IsNaN(cc) || double.IsNaN(d)) continue;
                    int idx = (a > level ? 8 : 0) | (b > level ? 4 : 0) | (cc > level ? 2 : 0) | (d > level ? 1 : 0);
                    if (idx == 0 || idx == 15) continue;
                    GeoPoint pa = at(r, c), pb = at(r, c + 1), pc = at(r + 1, c + 1), pd = at(r + 1, c);
                    GeoPoint top = cut(pa, pb, a, b), right = cut(pb, pc, b, cc), bottom = cut(pd, pc, d, cc), left = cut(pa, pd, a, d);
                    switch (idx)
                    {
                        case 1: case 14: segs.Add(Tuple.Create(left, bottom)); break;
                        case 2: case 13: segs.Add(Tuple.Create(bottom, right)); break;
                        case 3: case 12: segs.Add(Tuple.Create(left, right)); break;
                        case 4: case 11: segs.Add(Tuple.Create(top, right)); break;
                        case 6: case 9: segs.Add(Tuple.Create(top, bottom)); break;
                        case 7: case 8: segs.Add(Tuple.Create(left, top)); break;
                        case 5: segs.Add(Tuple.Create(left, top)); segs.Add(Tuple.Create(bottom, right)); break;
                        case 10: segs.Add(Tuple.Create(top, right)); segs.Add(Tuple.Create(left, bottom)); break;
                    }
                }
            return Join(segs);
        }

        /// <summary>Segments joined end to end into polylines.</summary>
        private static List<List<GeoPoint>> Join(List<Tuple<GeoPoint, GeoPoint>> segs)
        {
            Func<GeoPoint, long> key = p => (long)Math.Round((p.Lat + 90) * 1e6) * 1000000000L + (long)Math.Round((p.Lon + 180) * 1e6);
            var ends = new Dictionary<long, List<int>>();
            for (int i = 0; i < segs.Count; i++)
                foreach (var p in new[] { segs[i].Item1, segs[i].Item2 })
                {
                    List<int> l;
                    if (!ends.TryGetValue(key(p), out l)) ends[key(p)] = l = new List<int>();
                    l.Add(i);
                }
            var used = new bool[segs.Count];
            var lines = new List<List<GeoPoint>>();
            for (int i = 0; i < segs.Count; i++)
            {
                if (used[i]) continue;
                used[i] = true;
                var line = new LinkedList<GeoPoint>(new[] { segs[i].Item1, segs[i].Item2 });
                foreach (bool forward in new[] { true, false })
                    while (true)
                    {
                        var end = forward ? line.Last.Value : line.First.Value;
                        int next = -1;
                        foreach (int j in ends[key(end)]) if (!used[j]) { next = j; break; }
                        if (next < 0) break;
                        used[next] = true;
                        var s = segs[next];
                        var other = key(s.Item1) == key(end) ? s.Item2 : s.Item1;
                        if (forward) line.AddLast(other); else line.AddFirst(other);
                    }
                lines.Add(line.ToList());
            }
            return lines;
        }
    }
}
