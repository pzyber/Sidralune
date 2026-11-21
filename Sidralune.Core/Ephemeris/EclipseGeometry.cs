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

        /// <summary>Decimal degrees to six places (about 0.1 m), as map sites write them: "58.488667, 13.334811".</summary>
        public string ToDecimalString() => Lat.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture) + ", " + Lon.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>
        /// Reads a latitude and longitude as people copy them: decimal degrees ("58.48866654226863, 13.334811397822232",
        /// also with decimal commas "58,4887 13,3348"), degrees, minutes and seconds ("58°29′19.2″N 13°20′05.3″E",
        /// "N 58° 29.32′ E 13° 20.09′", "58 29 19 N, 13 20 05 E"), or a map link with the place in it (Google Maps'
        /// "@58.4887,13.3348" or "query=58.4887,13.3348", a "geo:58.4887,13.3348" link). Latitude comes first unless the
        /// letters say otherwise; besides N, S, E and W the compass letters of the interface language are read.
        /// </summary>
        public static bool TryParse(string text, out GeoPoint p)
        {
            p = new GeoPoint();
            if (string.IsNullOrWhiteSpace(text)) return false;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            const string num = @"[-+−]?\d+(?:\.\d+)?";
            // a link: the place after @, query=, q=, ll= or geo:
            var link = System.Text.RegularExpressions.Regex.Match(text, @"(?:@|[?&](?:query|q|ll|center)=|geo:)\s*(" + num + @")\s*(?:,|%2C)\s*(" + num + ")", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (link.Success)
                return Make(double.Parse(link.Groups[1].Value.Replace("−", "-"), inv), double.Parse(link.Groups[2].Value.Replace("−", "-"), inv), out p);

            string s = text.Trim().ToUpperInvariant().Replace("−", "-").Replace("–", "-");
            // decimal commas: two numbers each with one ("58,4887 13,3348" or "58,4887; 13,3348"), when there is no point
            if (!s.Contains(".") && System.Text.RegularExpressions.Regex.Matches(s, @"\d,\d").Count == 2)
                s = System.Text.RegularExpressions.Regex.Replace(s, @"(\d),(\d)", "$1.$2");
            // units to one sign each, and the letters of the compass (the interface language's too)
            s = s.Replace("º", "°").Replace("˚", "°").Replace("''", "″").Replace("\"", "″").Replace("”", "″").Replace("’’", "″")
                 .Replace("'", "′").Replace("’", "′").Replace("´", "′");
            var letters = new Dictionary<char, char> { { 'N', 'N' }, { 'S', 'S' }, { 'E', 'E' }, { 'W', 'W' } };
            var local = new[] { Tuple.Create(Util.Lang.Tc("compass", "N"), 'N'), Tuple.Create(Util.Lang.Tc("compass", "S"), 'S'), Tuple.Create(Util.Lang.Tc("compass", "E"), 'E'), Tuple.Create(Util.Lang.Tc("compass", "W"), 'W') };
            foreach (var l in local)
            {
                string u = l.Item1.ToUpperInvariant();
                if (u.Length == 1 && !letters.ContainsKey(u[0])) letters[u[0]] = l.Item2;
            }

            // the items: numbers with their unit, compass letters and separators
            var items = new List<Tuple<char, double, char>>(); // kind ('n' number, 'h' letter, ',' separator), value, unit or letter
            int i = 0;
            while (i < s.Length)
            {
                char c = s[i];
                var m = System.Text.RegularExpressions.Regex.Match(s.Substring(i), "^" + num);
                if (m.Success && (char.IsDigit(c) || ((c == '-' || c == '+') && i + 1 < s.Length && char.IsDigit(s[i + 1]))))
                {
                    i += m.Length;
                    while (i < s.Length && s[i] == ' ') i++;
                    char unit = i < s.Length && (s[i] == '°' || s[i] == '′' || s[i] == '″' || s[i] == ':') ? s[i] : ' ';
                    if (unit != ' ') i++;
                    items.Add(Tuple.Create('n', double.Parse(m.Value, inv), unit));
                }
                else if (letters.ContainsKey(c) && (i + 1 >= s.Length || !char.IsLetter(s[i + 1])) && (i == 0 || !char.IsLetter(s[i - 1])))
                { items.Add(Tuple.Create('h', 0.0, letters[c])); i++; }
                else if (c == ',' || c == ';' || c == '/') { items.Add(Tuple.Create(',', 0.0, c)); i++; }
                else if (c == ' ' || c == '\t' || c == '\r' || c == '\n' || c == '(' || c == ')') i++;
                else return false; // something else: not a place
            }
            var nums = items.Where(x => x.Item1 == 'n').ToList();
            var hemis = items.Where(x => x.Item1 == 'h').ToList();
            if (nums.Count < 2 || nums.Count > 6 || hemis.Count > 2) return false;

            // into the two coordinates
            var groups = new List<List<Tuple<char, double, char>>>();
            char[] hemi = new char[2];
            if (hemis.Count == 2)
            {
                // the letters before their numbers ("N 58 29, E 13 20") or after them ("58 29 N 13 20 E")
                bool prefix = items[0].Item1 == 'h';
                List<Tuple<char, double, char>> cur = null;
                int g = -1;
                foreach (var x in items)
                {
                    if (x.Item1 == 'h')
                    {
                        if (prefix) { g++; cur = new List<Tuple<char, double, char>>(); groups.Add(cur); hemi[g] = x.Item3; }
                        else { if (cur == null) return false; g++; hemi[g] = x.Item3; cur = null; }
                    }
                    else if (x.Item1 == 'n')
                    {
                        if (cur == null) { if (prefix) return false; cur = new List<Tuple<char, double, char>>(); groups.Add(cur); }
                        cur.Add(x);
                    }
                }
                if (groups.Count != 2 || g != 1) return false;
            }
            else if (hemis.Count == 0)
            {
                int sep = items.FindIndex(x => x.Item1 == ',');
                if (sep > 0 && items.Count(x => x.Item1 == ',') == 1)
                {
                    groups.Add(items.Take(sep).Where(x => x.Item1 == 'n').ToList());
                    groups.Add(items.Skip(sep + 1).Where(x => x.Item1 == 'n').ToList());
                }
                else if (nums.Count(x => x.Item3 == '°') == 2)
                {
                    // each coordinate begins with its degrees
                    foreach (var x in nums) { if (x.Item3 == '°' || groups.Count == 0) groups.Add(new List<Tuple<char, double, char>>()); groups[groups.Count - 1].Add(x); }
                }
                else if (nums.Count % 2 == 0)
                {
                    groups.Add(nums.Take(nums.Count / 2).ToList());
                    groups.Add(nums.Skip(nums.Count / 2).ToList());
                }
                if (groups.Count != 2) return false;
            }
            else return false; // one letter only: which number it belongs to is a guess

            // each coordinate: degrees, minutes and seconds, with its sign or letter
            var vals = new double[2];
            for (int k = 0; k < 2; k++)
            {
                var gr = groups[k];
                if (gr.Count < 1 || gr.Count > 3) return false;
                double v = 0;
                bool neg = gr[0].Item2 < 0;
                for (int j = 0; j < gr.Count; j++)
                {
                    double a = Math.Abs(gr[j].Item2);
                    if (j > 0 && (a >= 60 || gr[j].Item2 < 0 || gr[j - 1].Item2 != Math.Floor(gr[j - 1].Item2))) return false; // 58.5 29 is no angle
                    v += a / Math.Pow(60, j);
                }
                if (hemi[k] == 'S' || hemi[k] == 'W') { if (neg) return false; neg = true; }
                vals[k] = neg ? -v : v;
            }
            // the letters may put longitude first
            bool lonFirst = hemis.Count == 2 && (hemi[0] == 'E' || hemi[0] == 'W');
            if (hemis.Count == 2 && (hemi[0] == 'E' || hemi[0] == 'W') == (hemi[1] == 'E' || hemi[1] == 'W')) return false; // two latitudes
            return lonFirst ? Make(vals[1], vals[0], out p) : Make(vals[0], vals[1], out p);
        }

        private static bool Make(double lat, double lon, out GeoPoint p)
        {
            p = new GeoPoint();
            if (double.IsNaN(lat) || double.IsNaN(lon) || Math.Abs(lat) > 90 || lon < -180 || lon > 360) return false;
            if (lon > 180) lon -= 360;
            p = new GeoPoint(lat, lon);
            return true;
        }
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
        /// <summary>
        /// Lunar: for each contact, the circle within which the Moon is up then (label, centre, angular radius in degrees),
        /// for shading at any zoom; the whole eclipse is seen inside the circles of WholeFrom, WholeTo and MAX.
        /// </summary>
        public List<Tuple<string, GeoPoint, double>> MoonUp = new List<Tuple<string, GeoPoint, double>>();
        public string WholeFrom, WholeTo;
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

            // every line solved on the fundamental plane, minute by minute, at the geometric horizon (the Sun's centre),
            // so they meet where they should: the central line ends on the maximum-at-horizon curve, the limits on the
            // sunrise and sunset curves
            Func<double, List<GeoPoint>> axis = t =>
            {
                double lat, lon;
                // a place right under the axis is in the central phase at that moment, by definition
                return be.AxisOnEarth(t, out lat, out lon) ? new List<GeoPoint> { new GeoPoint(lat, lon) } : new List<GeoPoint>();
            };
            var central = Sweep(axis, 1 / 120.0, ct, 0);
            bool isCentral = central.Count > 0;
            // both limits at once, joined into lines by continuity (near the horizon which side a point is on is ambiguous)
            var penumbral = Sweep(t => Limits(be, t, true), 1 / 60.0, ct, 5);
            var umbral = isCentral ? Sweep(t => Limits(be, t, false), 1 / 60.0, ct, 0.5) : new List<List<GeoPoint>>();
            var contactHorizon = Sweep(t => OnTheLimb(be, t, false), 1 / 60.0, ct, 5, true);
            var maxHorizon = Sweep(t => OnTheLimb(be, t, true), 1 / 60.0, ct, 5);
            // right at the horizon a line's last stretch is too short to sample: each is carried on in its own direction to
            // the curve it ends on (the edges of the path and the central line on the maximum-at-horizon curve, the
            // partial-eclipse limits at the tips of the sunrise and sunset loops), when that is close
            ExtendTo(umbral, maxHorizon, 1);
            ExtendTo(central, maxHorizon, 1);
            // (or, where a limit runs into the narrow tip of a loop and its straight course just misses it, at the tip
            // itself: the end of the maximum-at-horizon curve)
            ExtendTo(penumbral, contactHorizon, 1, maxHorizon.SelectMany(l => new[] { l[0], l[l.Count - 1] }).ToList());
            if (isCentral)
            {
                map.Lines.Add(new EclipseLine { Kind = EclipseLineKind.CentralLimit, Parts = SplitAtDateLine(umbral) });
                map.Lines.Add(new EclipseLine { Kind = EclipseLineKind.CentralLine, Parts = SplitAtDateLine(central) });
            }
            map.Lines.Add(new EclipseLine { Kind = EclipseLineKind.PenumbralLimit, Parts = SplitAtDateLine(penumbral) });
            map.Lines.Add(new EclipseLine { Kind = EclipseLineKind.ContactAtHorizon, Parts = SplitAtDateLine(contactHorizon) });
            map.Lines.Add(new EclipseLine { Kind = EclipseLineKind.MaximumAtHorizon, Parts = SplitAtDateLine(maxHorizon) });

            // the obscuration lines from a 0.5° grid, kept where the Sun is up at maximum, and the part of the world to show
            const double step = 0.5;
            int rows = (int)(180 / step) + 1, cols = (int)(360 / step) + 1;
            var obs = new double[rows, cols];
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
                    obs[r, c] = l.Covers ? l.Obscuration : double.NaN;
                    if (l.Visible) { minLat = Math.Min(minLat, lat); maxLat = Math.Max(maxLat, lat); lons.Add(lon); }
                }
            }
            foreach (double level in new[] { 0.25, 0.5, 0.75 })
                map.Lines.Add(new EclipseLine { Kind = EclipseLineKind.Obscuration, Level = level, Parts = SplitAtDateLine(JoinEnds(WhereSunUp(be, Contour(obs, step, level)), 1e-4, true)) });
            if (lons.Count > 0) SetView(map, minLat, maxLat, lons);
            return map;
        }

        /// <summary>
        /// The northern and southern limits at t hours: the places on the Sun-facing side whose maximum is now, at the edge
        /// of the penumbra (or umbra). On the fundamental plane they are on the circle of the shadow's radius round the axis,
        /// where the place's motion relative to the shadow runs along the circle, found as roots around the circle.
        /// </summary>
        private static List<GeoPoint> Limits(BesselianElements be, double t, bool penumbral)
        {
            double x, y, d, mu, l1, l2, dx, dy, dd, dmu;
            be.At(t, out x, out y, out d, out mu, out l1, out l2, out dx, out dy, out dd, out dmu);
            var res = new List<GeoPoint>();
            // the place at angle th on the circle, its radius settled for the place's own ζ; NaN off the Earth
            double limbW = be.LimbOmega(t);
            Func<double, double[]> place = th =>
            {
                double r = penumbral ? l1 : Math.Abs(l2), lat = 0, lon = 0, u = 0, v = 0, a = 0, b = 0, zeta = 0;
                for (int i = 0; i < 3; i++)
                {
                    double L1, L2;
                    // the radius settled for the place's height above the fundamental plane can take a point next to the
                    // outline just over it: such a point is put on the outline, so the search works right up to the edge
                    double xi = x + r * Math.Cos(th), eta = y + r * Math.Sin(th), q = Math.Sqrt(xi * xi + limbW * limbW * eta * eta);
                    if (q > 1 && q < 1 + 1e-3) { xi /= q; eta /= q; }
                    if (!be.PlaneToEarth(t, xi, eta, out lat, out lon)) return null;
                    be.Relative(t, lat, lon, out u, out v, out a, out b, out L1, out L2, out zeta);
                    r = penumbral ? L1 : Math.Abs(L2);
                }
                return new[] { lat, lon, u * a + v * b, zeta };
            };
            Func<double, double> g = th => { var p = place(th); return p == null ? double.NaN : p[2]; };
            // only the arcs of the circle on the Earth are searched, each in many steps however short it is: just after
            // sunrise and before sunset only a sliver of the circle is on the Earth, which steps round the whole circle miss
            double w = be.LimbOmega(t), r0 = penumbral ? l1 : Math.Abs(l2);
            Func<double, double> outside = th => { double xi = x + r0 * Math.Cos(th), eta = y + r0 * Math.Sin(th); return xi * xi + w * w * eta * eta - 1; };
            foreach (var arc in Arcs(outside, 720))
                foreach (var th in Roots(g, arc.Item1, arc.Item2, 120))
                {
                    var p = place(th);
                    if (p == null || p[3] < -1e-6) continue; // the night side
                    res.Add(new GeoPoint(p[0], p[1]));
                }
            return res;
        }

        /// <summary>
        /// The parts of lines where the Sun is up at maximum, cut exactly where it is on the horizon (on the
        /// maximum-at-horizon curve): the obscuration lines come from a grid, which on its own would stop a cell short.
        /// </summary>
        private static List<List<GeoPoint>> WhereSunUp(BesselianElements be, List<List<GeoPoint>> lines)
        {
            Func<GeoPoint, double> alt = p => be.Local(p.Lat, p.Lon).Maximum.Altitude;
            // the place where the altitude passes zero between a and b; null when it jumps there instead (a place can have
            // two nearly equal closest approaches to the axis, one with the Sun up and one with it down, and which is
            // nearer changes from place to place: that is no horizon)
            Func<GeoPoint, GeoPoint, GeoPoint?> crossing = (a, b) =>
            {
                double fa = alt(a);
                double dl = b.Lon - a.Lon; if (dl > 180) dl -= 360; else if (dl < -180) dl += 360;
                Func<double, GeoPoint> at = m => new GeoPoint(a.Lat + m * (b.Lat - a.Lat), AstroMath.NormalizeDeg180(a.Lon + m * dl));
                double lo = 0, hi = 1;
                for (int k = 0; k < 30; k++)
                {
                    double m = (lo + hi) / 2;
                    if ((alt(at(m)) > 0) == (fa > 0)) lo = m; else hi = m;
                }
                if (Math.Abs(alt(at(lo))) > 0.5 || Math.Abs(alt(at(hi))) > 0.5) return null;
                return at((lo + hi) / 2);
            };
            var res = new List<List<GeoPoint>>();
            foreach (var line in lines)
            {
                List<GeoPoint> cur = null;
                bool prevUp = false;
                for (int i = 0; i < line.Count; i++)
                {
                    bool up = alt(line[i]) > 0;
                    if (i > 0 && up != prevUp)
                    {
                        var x = crossing(line[i - 1], line[i]);
                        if (!x.HasValue) up = prevUp; // a jump, not the horizon: the line goes on as it was
                        else if (cur != null) { cur.Add(x.Value); if (cur.Count > 1) res.Add(cur); cur = null; }
                        else cur = new List<GeoPoint> { x.Value };
                    }
                    if (up) { if (cur == null) cur = new List<GeoPoint>(); cur.Add(line[i]); }
                    prevUp = up;
                }
                if (cur != null && cur.Count > 1) res.Add(cur);
            }
            return res;
        }

        /// <summary>
        /// Each line's ends carried on straight, in the direction of its last stretch, to where they first cross one of the
        /// target lines, when that is within maxDeg (on a local flat map round the end, fine over so short a distance), or
        /// to the nearest of the tips within maxDeg when there is one.
        /// </summary>
        private static void ExtendTo(List<List<GeoPoint>> lines, List<List<GeoPoint>> targets, double maxDeg, List<GeoPoint> tips = null)
        {
            foreach (var line in lines)
            {
                if (line.Count < 3) continue;
                foreach (bool atEnd in new[] { false, true })
                {
                    var end = atEnd ? line[line.Count - 1] : line[0];
                    // the direction from a point a little way back (the last few points can be very close together)
                    GeoPoint back = end;
                    for (int k = 1; k < line.Count; k++)
                    {
                        back = atEnd ? line[line.Count - 1 - k] : line[k];
                        if (Distance(back, end) > 0.02) break;
                    }
                    double c = Math.Cos(end.Lat * AstroMath.Deg2Rad);
                    Func<GeoPoint, double[]> local = p => { double dl = p.Lon - end.Lon; if (dl > 180) dl -= 360; else if (dl < -180) dl += 360; return new[] { dl * c, p.Lat - end.Lat }; };
                    var bk = local(back);
                    double len = Math.Sqrt(bk[0] * bk[0] + bk[1] * bk[1]);
                    if (len < 1e-9) continue;
                    // a tip within reach is where the line ends (the limits of the partial eclipse end where they meet both
                    // horizon curves, at the ends of the maximum-at-horizon curve): carried on straight, the line could cross
                    // the other curve a little before it
                    var tip = tips?.Where(p => Distance(p, end) < maxDeg).OrderBy(p => Distance(p, end)).Cast<GeoPoint?>().FirstOrDefault();
                    if (tip.HasValue)
                    {
                        if (Distance(tip.Value, end) > 1e-7) { if (atEnd) line.Add(tip.Value); else line.Insert(0, tip.Value); }
                        continue;
                    }
                    double dx = -bk[0] / len, dy = -bk[1] / len, best = double.MaxValue;
                    foreach (var target in targets)
                        for (int i = 0; i + 1 < target.Count; i++)
                        {
                            var a = local(target[i]); var b = local(target[i + 1]);
                            if (Math.Min(Math.Abs(a[0]), Math.Abs(b[0])) > maxDeg + 1 && Math.Min(Math.Abs(a[1]), Math.Abs(b[1])) > maxDeg + 1) continue;
                            // the ray s·(dx, dy) against the segment a + u·(b − a)
                            double ex = b[0] - a[0], ey = b[1] - a[1], den = dx * ey - dy * ex;
                            if (Math.Abs(den) < 1e-12) continue;
                            double s = (a[0] * ey - a[1] * ex) / den, u = (a[0] * dy - a[1] * dx) / den;
                            if (u >= 0 && u <= 1 && s > 1e-9 && s < Math.Min(best, maxDeg)) best = s;
                        }
                    if (best == double.MaxValue) continue;
                    var p2 = new GeoPoint(end.Lat + best * dy, AstroMath.NormalizeDeg180(end.Lon + best * dx / c));
                    if (atEnd) line.Add(p2); else line.Insert(0, p2);
                }
            }
        }

        /// <summary>The ranges of angle (from, to; to may pass 2π) where f is below zero, found in n steps and refined.</summary>
        private static List<Tuple<double, double>> Arcs(Func<double, double> f, int n)
        {
            var res = new List<Tuple<double, double>>();
            double step = 2 * Math.PI / n;
            var inside = new bool[n];
            for (int i = 0; i < n; i++) inside[i] = f(i * step) < 0;
            if (inside.All(b => b)) { res.Add(Tuple.Create(0.0, 2 * Math.PI)); return res; }
            Func<double, double, double> edge = (a, b) => { for (int k = 0; k < 50; k++) { double m = (a + b) / 2; if ((f(m) < 0) == (f(a) < 0)) a = m; else b = m; } return (a + b) / 2; };
            int start = Array.FindIndex(inside, b => !b); // begin outside, so every arc is seen whole
            for (int k = 1; k <= n; k++)
            {
                int i = (start + k) % n, prev = (start + k - 1) % n;
                if (inside[i] && !inside[prev])
                {
                    double from = edge((start + k - 1) * step, (start + k) * step);
                    int j = k;
                    while (j <= n && inside[(start + j) % n]) j++;
                    double to = edge((start + j - 1) * step, (start + j) * step);
                    res.Add(Tuple.Create(from, to));
                    k = j;
                }
            }
            // an arc too short for the steps (the circle only just touching the outline) is looked for near the step
            // closest to inside
            if (res.Count == 0)
            {
                int best = 0;
                for (int i = 1; i < n; i++) if (f(i * step) < f(best * step)) best = i;
                double lo = (best - 1) * step, hi = (best + 1) * step;
                var fine = Enumerable.Range(0, 201).Select(i => lo + (hi - lo) * i / 200).Where(th => f(th) < 0).ToList();
                if (fine.Count > 0) res.Add(Tuple.Create(fine.First(), fine.Last()));
            }
            return res;
        }

        /// <summary>
        /// The angles between from and to where f changes sign, found in n steps and refined by bisection. The steps are
        /// close together near the ends (on the Earth's outline), where a limit is born in a pair with another root that
        /// runs out to the horizon: the two are only told apart by steps finer than the distance between them.
        /// </summary>
        private static List<double> Roots(Func<double, double> f, double from, double to, int n)
        {
            var res = new List<double>();
            const double eps = 1e-9; // just inside the ends, which are on the Earth's outline
            double a = from + eps, fa = f(a);
            for (int i = 1; i <= n; i++)
            {
                double b = i == n ? to - eps : from + (to - from) * (0.5 - 0.5 * Math.Cos(Math.PI * i / n)), fb = f(b);
                if (!double.IsNaN(fa) && !double.IsNaN(fb) && (fa < 0) != (fb < 0))
                {
                    double lo = a, hi = b, flo = fa;
                    for (int k = 0; k < 50; k++) { double mid = (lo + hi) / 2, fm = f(mid); if (double.IsNaN(fm)) break; if ((fm < 0) == (flo < 0)) { lo = mid; flo = fm; } else hi = mid; }
                    res.Add((lo + hi) / 2);
                }
                a = b; fa = fb;
            }
            return res;
        }

        /// <summary>
        /// The lines a function of time traces (it gives the points on them at each moment), over the elements' span: every
        /// step, halved down to about a second wherever the number of points changes, so each line runs right up to where
        /// it begins and ends (where it meets the others).
        /// </summary>
        private static List<List<GeoPoint>> Sweep(Func<double, List<GeoPoint>> f, double step, CancellationToken ct, double joinDeg, bool closeRings = false)
        {
            var tracker = new Tracker();
            double t0 = -BesselianElements.Span;
            var prev = f(t0);
            tracker.Step(prev);
            for (double t = t0 + step; t <= BesselianElements.Span + 1e-9; t += step)
            {
                ct.ThrowIfCancellationRequested();
                prev = Refine(f, t - step, prev, tb: t, tracker: tracker);
            }
            return JoinEnds(tracker.Lines.Where(l => l.Count > 1).ToList(), joinDeg, closeRings).Select(Despike).Where(l => l.Count > 1).ToList();
        }

        /// <summary>
        /// A line without the short back-and-forth steps that tracking two nearly equal roots near a fold leaves (a point
        /// where the line turns back by more than 120° over less than 1° is dropped, until none is left), nor repeated points.
        /// </summary>
        private static List<GeoPoint> Despike(List<GeoPoint> line)
        {
            var l = new List<GeoPoint>();
            foreach (var p in line) if (l.Count == 0 || Distance(l[l.Count - 1], p) > 1e-7) l.Add(p);
            bool changed = true;
            while (changed && l.Count > 2)
            {
                changed = false;
                for (int i = 1; i + 1 < l.Count; i++)
                {
                    double ab = Distance(l[i - 1], l[i]), bc = Distance(l[i], l[i + 1]);
                    if (Math.Min(ab, bc) >= 1) continue;
                    double turn = Math.Abs(((Bearing(l[i], l[i + 1]) - Bearing(l[i - 1], l[i])) % 360 + 540) % 360 - 180);
                    if (turn > 120) { l.RemoveAt(i); changed = true; i--; }
                }
            }
            return l;
        }

        /// <summary>
        /// Lines whose ends are within maxDeg of each other joined into one, nearest ends first: near the horizon two
        /// pieces of one curve can run towards each other in time and meet, which the step-by-step tracking leaves apart.
        /// </summary>
        private static List<List<GeoPoint>> JoinEnds(List<List<GeoPoint>> lines, double maxDeg, bool closeRings)
        {
            var res = lines.Select(l => l.ToList()).ToList();
            while (true)
            {
                int bi = -1, bj = -1; bool bEndI = false, bEndJ = false; double bd = maxDeg;
                for (int i = 0; i < res.Count; i++)
                    for (int j = i + 1; j < res.Count; j++)
                        foreach (bool ei in new[] { false, true })
                            foreach (bool ej in new[] { false, true })
                            {
                                var a = ei ? res[i][res[i].Count - 1] : res[i][0];
                                var b = ej ? res[j][res[j].Count - 1] : res[j][0];
                                double d = Distance(a, b);
                                if (d < bd) { bd = d; bi = i; bj = j; bEndI = ei; bEndJ = ej; }
                            }
                if (bi < 0)
                {
                    // a loop whose two ends nearly meet (the tip of a sunrise or sunset curve) is closed
                    if (closeRings)
                        foreach (var l in res)
                            if (l.Count > 10 && Distance(l[0], l[l.Count - 1]) < maxDeg) l.Add(l[0]);
                    return res;
                }
                // line i ending at the joined end, followed by line j starting there
                var first = res[bi]; var second = res[bj];
                if (!bEndI) first.Reverse();
                if (bEndJ) second.Reverse();
                first.AddRange(second);
                res.RemoveAt(bj);
            }
        }

        /// <summary>Steps the tracker to tb, halving the step where points appear or vanish or move far (fast near the horizon).</summary>
        private static List<GeoPoint> Refine(Func<double, List<GeoPoint>> f, double ta, List<GeoPoint> pa, double tb, Tracker tracker)
        {
            var pb = f(tb);
            // where a line begins or ends or moves far, down to a hundred-thousandth of a second (near the horizon a tiny
            // step on the fundamental plane is a long way on the ground: a line rushes inland from it, and is followed)
            bool appear = pb.Count != pa.Count;
            bool far = !appear && pb.Any(p => pa.Min(q => Distance(p, q)) > 1);
            if ((appear || far) && tb - ta > 1 / 3.6e8)
            {
                double tm = (ta + tb) / 2;
                var pm = Refine(f, ta, pa, tm, tracker);
                return Refine(f, tm, pm, tb, tracker);
            }
            tracker.Step(pb);
            return pb;
        }

        /// <summary>
        /// The places on the Earth's limb (the Sun's centre on the horizon) at t hours where the penumbra's edge is (first
        /// or last contact at sunrise or sunset), or with maxima true where the maximum is (inside the penumbra).
        /// </summary>
        private static List<GeoPoint> OnTheLimb(BesselianElements be, double t, bool maxima)
        {
            List<GeoPoint> c, m;
            OnTheLimb(be, t, out c, out m, maxima);
            return maxima ? m : c;
        }

        private static void OnTheLimb(BesselianElements be, double t, out List<GeoPoint> contacts, out List<GeoPoint> maxima, bool findMaxima)
        {
            double x, y, d, mu, l1, l2, dx, dy, dd, dmu;
            be.At(t, out x, out y, out d, out mu, out l1, out l2, out dx, out dy, out dd, out dmu);
            double w = be.LimbOmega(t);
            Func<double, double> limbX = th => Math.Cos(th), limbY = th => Math.Sin(th) / w;
            Func<double, double> edge = th => { double ex = limbX(th) - x, ey = limbY(th) - y; return ex * ex + ey * ey - l1 * l1; };
            Func<double, double> max = th =>
            {
                double lat, lon, u, v, a, b, L1, L2, zeta;
                if (!be.PlaneToEarth(t, limbX(th), limbY(th), out lat, out lon)) return double.NaN;
                be.Relative(t, lat, lon, out u, out v, out a, out b, out L1, out L2, out zeta);
                return u * a + v * b;
            };
            contacts = new List<GeoPoint>(); maxima = new List<GeoPoint>();
            // the penumbra reaches the limb only when its centre is within its radius of the outline
            double r = Math.Sqrt(x * x + y * y);
            if (r > 1 + l1 + 0.05) return;
            if (!findMaxima) { foreach (var root in Roots(edge, 720)) Add(be, t, limbX(root), limbY(root), contacts); return; }
            foreach (var root in Roots(max, 720))
            {
                double ex = limbX(root) - x, ey = limbY(root) - y;
                if (ex * ex + ey * ey < l1 * l1) Add(be, t, limbX(root), limbY(root), maxima);
            }
        }

        private static void Add(BesselianElements be, double t, double xi, double eta, List<GeoPoint> to)
        {
            double lat, lon;
            if (be.PlaneToEarth(t, xi, eta, out lat, out lon)) to.Add(new GeoPoint(lat, lon));
        }

        /// <summary>The angles (0 to 2π) where f changes sign, found in n steps and refined by bisection.</summary>
        private static List<double> Roots(Func<double, double> f, int n)
        {
            var res = new List<double>();
            double step = 2 * Math.PI / n, a = 0, fa = f(0);
            for (int i = 1; i <= n; i++)
            {
                double b = i * step, fb = f(b);
                double lo = a, hi = b, flo = fa, fhi = fb;
                // one end off the Earth: the root may lie between the other end and the Earth's edge, so find the edge
                if (double.IsNaN(flo) != double.IsNaN(fhi))
                {
                    double valid = double.IsNaN(flo) ? hi : lo, invalid = double.IsNaN(flo) ? lo : hi, fv = double.IsNaN(flo) ? fhi : flo;
                    double edge = valid, fe = fv;
                    for (int k = 0; k < 40; k++)
                    {
                        double mid = (valid + invalid) / 2, fm = f(mid);
                        if (double.IsNaN(fm)) invalid = mid; else { valid = mid; edge = mid; fe = fm; }
                    }
                    if (double.IsNaN(flo)) { lo = edge; flo = fe; } else { hi = edge; fhi = fe; }
                }
                if (!double.IsNaN(flo) && !double.IsNaN(fhi) && (flo < 0) != (fhi < 0))
                {
                    for (int k = 0; k < 40; k++) { double mid = (lo + hi) / 2, fm = f(mid); if (double.IsNaN(fm)) break; if ((fm < 0) == (flo < 0)) { lo = mid; flo = fm; } else hi = mid; }
                    res.Add((lo + hi) / 2);
                }
                a = b; fa = fb;
            }
            return res;
        }

        /// <summary>
        /// Builds polylines from points found step by step: each new point continues the line whose last point is nearest
        /// (within a few degrees), or starts a new one; lines not continued are finished.
        /// </summary>
        private sealed class Tracker
        {
            private readonly List<List<GeoPoint>> _active = new List<List<GeoPoint>>();
            public readonly List<List<GeoPoint>> Lines = new List<List<GeoPoint>>();
            private const double MaxJump = 8;

            public void Step(List<GeoPoint> points)
            {
                // each line's next point foreseen from its last two, and the pairing of points with lines that keeps the
                // total distance from those smallest (lines close together, like the edges of a narrow path, stay apart)
                var lines = _active.ToList();
                var predicted = lines.Select(Predict).ToList();
                int[] best = null; double bestCost = double.MaxValue;
                foreach (var pairing in Pairings(points.Count, lines.Count))
                {
                    double cost = 0; bool ok = true;
                    for (int i = 0; i < points.Count && ok; i++)
                    {
                        if (pairing[i] < 0) { cost += MaxJump; continue; } // a new line costs as much as the longest join
                        double dd = Distance(predicted[pairing[i]], points[i]);
                        if (dd > MaxJump) ok = false; else cost += dd;
                    }
                    if (ok && cost < bestCost) { bestCost = cost; best = pairing; }
                }
                var next = new List<List<GeoPoint>>();
                for (int i = 0; i < points.Count; i++)
                {
                    if (best != null && best[i] >= 0) { lines[best[i]].Add(points[i]); next.Add(lines[best[i]]); }
                    else { var line = new List<GeoPoint> { points[i] }; Lines.Add(line); next.Add(line); }
                }
                _active.Clear(); _active.AddRange(next);
            }

            private static GeoPoint Predict(List<GeoPoint> line)
            {
                var a = line[line.Count - 1];
                if (line.Count < 2) return a;
                var b = line[line.Count - 2];
                double dlon = a.Lon - b.Lon;
                if (dlon > 180) dlon -= 360; else if (dlon < -180) dlon += 360;
                return new GeoPoint(Math.Max(-90, Math.Min(90, 2 * a.Lat - b.Lat)), AstroMath.NormalizeDeg180(a.Lon + dlon));
            }

            /// <summary>Every way to give each of n points a different one of m lines, or none (−1).</summary>
            private static IEnumerable<int[]> Pairings(int n, int m)
            {
                var cur = new int[n];
                var used = new bool[m];
                return Fill(0, n, m, cur, used);
            }

            private static IEnumerable<int[]> Fill(int i, int n, int m, int[] cur, bool[] used)
            {
                if (i == n) { yield return (int[])cur.Clone(); yield break; }
                cur[i] = -1;
                foreach (var p in Fill(i + 1, n, m, cur, used)) yield return p;
                for (int j = 0; j < m; j++)
                {
                    if (used[j]) continue;
                    used[j] = true; cur[i] = j;
                    foreach (var p in Fill(i + 1, n, m, cur, used)) yield return p;
                    used[j] = false;
                }
            }
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
                map.MoonUp.Add(Tuple.Create(c.Item1, sub, radius));
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
            map.WholeFrom = first; map.WholeTo = last;
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
                    if (cur.Count > 0 && Math.Abs(p[i].Lon - cur[cur.Count - 1].Lon) > 180)
                    {
                        // both pieces run right up to the date line, so no gap is left there
                        var a = cur[cur.Count - 1]; var b = p[i];
                        double edge = a.Lon > 0 ? 180 : -180, bLon = b.Lon + (a.Lon > 0 ? 360 : -360);
                        double k = bLon == a.Lon ? 0 : (edge - a.Lon) / (bLon - a.Lon); // 0 when a is on the date line itself
                        double lat = a.Lat + k * (b.Lat - a.Lat);
                        cur.Add(new GeoPoint(lat, edge));
                        if (cur.Count > 1) res.Add(cur);
                        cur = new List<GeoPoint> { new GeoPoint(lat, -edge) };
                    }
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
