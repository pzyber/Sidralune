using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Sidralune.Core;
using Sidralune.Core.Ephemeris;
using Sidralune.Core.Mathematics;

namespace Sidralune.Science.SolarSystem
{
    /// <summary>A moon of a planet (or of Pluto), with its orbit and physical data.</summary>
    public class PlanetMoon
    {
        public string Name;
        public Body Planet;
        /// <summary>Mean radius (km), absolute magnitude V(1, 0) and orbital period (days).</summary>
        public double RadiusKm, AbsMag, PeriodDays;
        /// <summary>One of the classical, bright moons, drawn at every magnitude limit (the others only when bright enough).</summary>
        public bool Major;
        /// <summary>Orbits against the planet's rotation.</summary>
        public bool Retrograde;
        /// <summary>The mean orbit's semi-major axis (km) and eccentricity.</summary>
        public double SemiMajorAxisKm => A;
        public double Eccentricity => E;

        // a precessing ellipse in the plane with this pole (ICRF, deg), with x towards the plane's ascending node on the
        // ICRF equator: a (km), e, inclination, node, longitude of periapsis and mean longitude (deg, + rate per day from J2000 TDB)
        internal double PoleRa, PoleDec, A, E, I0, Id, N0, Nd, W0, Wd, L0, Ld;
        // optional corrections along the orbit (deg) and to the distance (fraction), and e cos/sin of the longitude of
        // periapsis in place of the ellipse's, linear between points CorrStep days apart
        internal double CorrStart, CorrStep;
        internal double[] CorrL, CorrR, CorrK, CorrH;
        // moons pulled around by the Sun or another moon: states (km, km/day, ICRF) StateStep days apart, integrated with
        // the planet's gravity (GM, and J2 with its reference radius), the Sun's, and a pulling moon's
        internal double StateStart, StateStep, Gm, Step, J2, J2RadiusKm, PullGm;
        internal string PullName;
        internal double[][] States;
        // the last integrated place, from state CacheState
        internal int CacheState = -1;
        internal double CacheJd;
        internal Vec3 CacheR, CacheV;
    }

    /// <summary>A moon as seen from the observer.</summary>
    public class MoonPlace
    {
        public PlanetMoon Moon;
        /// <summary>The moon's own place: RA/Dec, horizontal, distances.</summary>
        public BodyPlace Place;
        /// <summary>From the planet's centre (km, ICRF axes), at the time the light left.</summary>
        public Vec3 OffsetKm;
        public double Mag, DiameterArcsec;
        /// <summary>Distance from the planet's centre on the sky (arcsec), with eastward and northward parts, and in planet radii.</summary>
        public double SeparationArcsec, EastArcsec, NorthArcsec, PlanetRadii;
        /// <summary>Farther than the planet; in front of its disc (a transit); hidden behind it.</summary>
        public bool Behind, Transit, Occulted;
    }

    /// <summary>
    /// 65 moons of Mars, Jupiter, Saturn, Uranus, Neptune and Pluto. Regular moons follow
    /// precessing ellipses fitted to JPL's ephemerides for 2000-2050, with corrections along the orbit where moons
    /// librate or share orbits (Mimas and Tethys, Janus and Epimetheus, the Trojans of Tethys and Dione, the ring
    /// shepherds). Hyperion and the irregular outer moons are integrated from JPL states a year or four apart, with the
    /// planet's, the Sun's and (for Hyperion) Titan's gravity. Within 2000-2050 all are within about 3" of JPL Horizons;
    /// outside it every moon falls back to its ellipse, which keeps the regular moons close and the irregular ones rough.
    /// </summary>
    public static partial class PlanetMoons
    {
        private static List<PlanetMoon> _all;
        private static readonly object _lock = new object();

        public static IReadOnlyList<PlanetMoon> All { get { Load(); return _all; } }

        public static IEnumerable<PlanetMoon> Of(Body planet) => All.Where(m => m.Planet == planet);

        /// <summary>A moon by name (any case), or null.</summary>
        public static PlanetMoon Find(string name)
        {
            name = name?.Trim();
            return string.IsNullOrEmpty(name) ? null : All.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>The planets that have moons here.</summary>
        public static readonly Body[] Planets = { Body.Mars, Body.Jupiter, Body.Saturn, Body.Uranus, Body.Neptune, Body.Pluto };

        /// <summary>Position from the planet's centre (barycentre for the irregular moons and Pluto's), km, ICRF, at a JD (TDB).</summary>
        public static Vec3 OffsetKm(PlanetMoon m, double jdTdb)
        {
            if (m.States != null)
            {
                // from the nearest state, or one step beyond the first and last
                double x = (jdTdb - m.StateStart) / m.StateStep;
                if (x > -1 && x < m.States.Length)
                {
                    int k = Math.Max(0, Math.Min(m.States.Length - 1, (int)Math.Round(x)));
                    lock (m)
                    {
                        // carry on from the last result when that is nearer (time running, a chart redrawn)
                        double jd0 = m.StateStart + k * m.StateStep;
                        Vec3 r, v;
                        if (m.CacheState == k && Math.Abs(jdTdb - m.CacheJd) < Math.Abs(jdTdb - jd0)) { jd0 = m.CacheJd; r = m.CacheR; v = m.CacheV; }
                        else { var s = m.States[k]; r = new Vec3(s[0], s[1], s[2]); v = new Vec3(s[3], s[4], s[5]); }
                        if (jdTdb != jd0) Integrate(m, jd0, ref r, ref v, jdTdb);
                        m.CacheState = k; m.CacheJd = jdTdb; m.CacheR = r; m.CacheV = v;
                        return r;
                    }
                }
            }
            return Ellipse(m, jdTdb);
        }

        private static Vec3 Ellipse(PlanetMoon m, double jd)
        {
            double t = jd - 2451545.0, d2r = AstroMath.Deg2Rad;
            double i = (m.I0 + m.Id * t) * d2r, node = (m.N0 + m.Nd * t) * d2r, peri = (m.W0 + m.Wd * t) * d2r, L = (m.L0 + m.Ld * t) * d2r;
            double dl = 0, dr = 0, e = m.E;
            if (m.CorrL != null)
            {
                double x = (jd - m.CorrStart) / m.CorrStep;
                int k = (int)Math.Floor(x);
                if (k >= 0 && k < m.CorrL.Length - 1)
                {
                    double f = x - k;
                    Func<double[], double> at = c => c[k] + (c[k + 1] - c[k]) * f;
                    dl = at(m.CorrL);
                    if (m.CorrR != null) dr = at(m.CorrR);
                    if (m.CorrK != null)
                    {
                        double ck = at(m.CorrK), ch = at(m.CorrH);
                        e = Math.Sqrt(ck * ck + ch * ch); peri = Math.Atan2(ch, ck);
                    }
                }
            }
            double M = L - peri, w = peri - node, E = M;
            for (int it = 0; it < 30; it++)
            {
                double dE = (E - e * Math.Sin(E) - M) / (1 - e * Math.Cos(E));
                E -= dE;
                if (Math.Abs(dE) < 1e-12) break;
            }
            double x0 = m.A * (Math.Cos(E) - e), y0 = m.A * Math.Sqrt(1 - e * e) * Math.Sin(E);
            // the corrections turn the moon along its orbit and move it in or out
            double r = Math.Sqrt(x0 * x0 + y0 * y0) * (1 + dr), v = Math.Atan2(y0, x0) + w + dl * d2r;
            double xp = r * Math.Cos(v), yp = r * Math.Sin(v);
            double cn = Math.Cos(node), sn = Math.Sin(node), ci = Math.Cos(i), si = Math.Sin(i);
            var loc = new Vec3(xp * cn - yp * ci * sn, xp * sn + yp * ci * cn, yp * si);
            return FrameToIcrf(m.PoleRa, m.PoleDec) * loc;
        }

        /// <summary>Rotation from a frame with this pole (deg) and x at its ascending node on the ICRF equator, to ICRF.</summary>
        internal static Mat3 FrameToIcrf(double poleRaDeg, double poleDecDeg)
        {
            double a = poleRaDeg * AstroMath.Deg2Rad, d = poleDecDeg * AstroMath.Deg2Rad;
            var z = new Vec3(Math.Cos(d) * Math.Cos(a), Math.Cos(d) * Math.Sin(a), Math.Sin(d));
            var x = new Vec3(-Math.Sin(a), Math.Cos(a), 0);
            var y = Vec3.Cross(z, x);
            return new Mat3(x.X, y.X, z.X, x.Y, y.Y, z.Y, x.Z, y.Z, z.Z);
        }

        // the planets' heliocentric places for the Sun's pull, from JPL's approximate Keplerian elements (1800-2050):
        // a (AU), e, I, L, long. of perihelion, node (deg), each with its rate per Julian century
        private static readonly Dictionary<Body, double[]> Kepler = new Dictionary<Body, double[]>
        {
            { Body.Jupiter, new[] { 5.20288700, -0.00011607, 0.04838624, -0.00013253, 1.30439695, -0.00183714, 34.39644051, 3034.74612775, 14.72847983, 0.21252668, 100.47390909, 0.20469106 } },
            { Body.Saturn, new[] { 9.53667594, -0.00125060, 0.05386179, -0.00050991, 2.48599187, 0.00193609, 49.95424423, 1222.49362201, 92.59887831, -0.41897216, 113.66242448, -0.28867794 } },
            { Body.Uranus, new[] { 19.18916464, -0.00196176, 0.04725744, -0.00004397, 0.77263783, -0.00242939, 313.23810451, 428.48202785, 170.95427630, 0.40805281, 74.01692503, 0.04240589 } },
            { Body.Neptune, new[] { 30.06992276, 0.00026291, 0.00859048, 0.00005105, 1.77004347, 0.00035372, -55.12002969, 218.45945325, 44.96476227, -0.32241464, 131.78422574, -0.00508664 } },
        };

        /// <summary>The planet's approximate heliocentric position (km, ICRF).</summary>
        internal static Vec3 Heliocentric(Body planet, double jd)
        {
            var k = Kepler[planet];
            double T = (jd - 2451545.0) / 36525, d2r = AstroMath.Deg2Rad;
            double a = k[0] + k[1] * T, e = k[2] + k[3] * T, I = (k[4] + k[5] * T) * d2r, L = k[6] + k[7] * T, wb = k[8] + k[9] * T, O = (k[10] + k[11] * T) * d2r;
            double M = (L - wb) * d2r, w = wb * d2r - O, E = M;
            for (int it = 0; it < 30; it++) E -= (E - e * Math.Sin(E) - M) / (1 - e * Math.Cos(E));
            double x = a * (Math.Cos(E) - e), y = a * Math.Sqrt(1 - e * e) * Math.Sin(E);
            double cw = Math.Cos(w), sw = Math.Sin(w), cO = Math.Cos(O), sO = Math.Sin(O), cI = Math.Cos(I), sI = Math.Sin(I);
            double xe = (cw * cO - sw * sO * cI) * x + (-sw * cO - cw * sO * cI) * y;
            double ye = (cw * sO + sw * cO * cI) * x + (-sw * sO + cw * cO * cI) * y;
            double ze = sw * sI * x + cw * sI * y;
            double eps = 23.43928 * d2r, au = AstroMath.AU_km;
            return new Vec3(xe * au, (ye * Math.Cos(eps) - ze * Math.Sin(eps)) * au, (ye * Math.Sin(eps) + ze * Math.Cos(eps)) * au);
        }

        private const double GmSun = 1.32712440018e11 * 86400.0 * 86400.0; // km^3/day^2

        // RK4 with the planet's gravity (and oblateness), the Sun's tide and a pulling moon's
        private static void Integrate(PlanetMoon m, double jd0, ref Vec3 r, ref Vec3 v, double jd)
        {
            double dt = jd - jd0;
            int steps = Math.Max(1, (int)Math.Ceiling(Math.Abs(dt) / m.Step));
            double h = dt / steps, t = jd0, gm = m.Gm;
            var pull = m.PullName == null ? null : Find(m.PullName);
            Vec3 pole = Vec3.Zero;
            if (m.J2 != 0)
            {
                double ra0, dec0, w;
                PlanetPhysics.RotationElements(m.Planet, 2451545.0, out ra0, out dec0, out w);
                pole = Vec3.FromSpherical(ra0 * AstroMath.Deg2Rad, dec0 * AstroMath.Deg2Rad);
            }
            Func<double, Vec3, Vec3> acc = (time, p) =>
            {
                double l = p.Length;
                var a = p * (-gm / (l * l * l));
                if (m.J2 != 0)
                {
                    double z = Vec3.Dot(p, pole) / l, f = 1.5 * m.J2 * gm * m.J2RadiusKm * m.J2RadiusKm / Math.Pow(l, 5);
                    a = a + (p * (5 * z * z - 1) - pole * (2 * z * l)) * f;
                }
                if (pull != null)
                {
                    var q = Ellipse(pull, time);
                    var dq = q - p;
                    double lq = q.Length, ldq = dq.Length;
                    a = a + dq * (m.PullGm / (ldq * ldq * ldq)) - q * (m.PullGm / (lq * lq * lq));
                }
                var sun = -Heliocentric(m.Planet, time);
                var d = sun - p;
                double ld = d.Length, ls = sun.Length;
                return a + d * (GmSun / (ld * ld * ld)) - sun * (GmSun / (ls * ls * ls));
            };
            for (int s = 0; s < steps; s++)
            {
                var a1 = acc(t, r);
                var r2 = r + v * (h / 2); var v2 = v + a1 * (h / 2);
                var a2 = acc(t + h / 2, r2);
                var r3 = r + v2 * (h / 2); var v3 = v + a2 * (h / 2);
                var a3 = acc(t + h / 2, r3);
                var r4 = r + v3 * h; var v4 = v + a3 * h;
                var a4 = acc(t + h, r4);
                r = r + (v + 2 * v2 + 2 * v3 + v4) * (h / 6);
                v = v + (a1 + 2 * a2 + 2 * a3 + a4) * (h / 6);
                t += h;
            }
        }

        /// <summary>
        /// A planet's moons as seen from the context's site and time: each moon's place with light time from the planet,
        /// its magnitude, size and where it is around the planet.
        /// </summary>
        public static MoonPlace[] Places(SkyContext ctx, Body planet) => Places(ctx, planet, Of(planet).ToArray());

        public static MoonPlace Place(SkyContext ctx, PlanetMoon m) => Places(ctx, m.Planet, new[] { m })[0];

        private static MoonPlace[] Places(SkyContext ctx, Body planet, PlanetMoon[] moons)
        {
            double lt;
            Func<double, Vec3> planetPos = jd => EphemerisManager.Position(planet, jd);
            var topo = ctx.TopocentricWithLightTime(planetPos, out lt);
            var u = topo.Normalized();
            double rKm = PlanetPhysics.EquatorialRadiusKm(planet);
            var east = new Vec3(-u.Y, u.X, 0).Normalized();
            var north = Vec3.Cross(u, east);
            var res = new MoonPlace[moons.Length];
            for (int i = 0; i < moons.Length; i++)
            {
                var m = moons[i];
                var off = OffsetKm(m, ctx.JdTDB - lt);
                var offAu = off / AstroMath.AU_km;
                var place = ctx.MakePlace(topo + offAu, lt, jd => planetPos(jd) + offAu);
                var p = new MoonPlace { Moon = m, Place = place, OffsetKm = off };
                // on the sky from the moon's own direction; across the line of sight in kilometres
                var um = (topo + offAu).Normalized();
                double toward = Vec3.Dot(um, u);
                p.EastArcsec = Vec3.Dot(um, east) / toward * AstroMath.Rad2ArcSec;
                p.NorthArcsec = Vec3.Dot(um, north) / toward * AstroMath.Rad2ArcSec;
                p.SeparationArcsec = Math.Sqrt(p.EastArcsec * p.EastArcsec + p.NorthArcsec * p.NorthArcsec);
                double across = (off - u * Vec3.Dot(off, u)).Length;
                p.PlanetRadii = across / rKm;
                p.Behind = Vec3.Dot(off, u) > 0;
                bool overDisc = across < rKm;
                p.Transit = overDisc && !p.Behind;
                p.Occulted = overDisc && p.Behind;
                p.Mag = m.AbsMag + 5 * Math.Log10(place.HelioDistance * place.Distance);
                p.DiameterArcsec = 2 * m.RadiusKm / (place.Distance * AstroMath.AU_km) * AstroMath.Rad2ArcSec;
                res[i] = p;
            }
            return res;
        }

        private static void Load()
        {
            if (_all != null) return;
            lock (_lock)
            {
                if (_all != null) return;
                var c = CultureInfo.InvariantCulture;
                var list = new List<PlanetMoon>();
                Func<string, double[]> nums = s => s.Split(',').Select(x => double.Parse(x, c)).ToArray();
                foreach (var raw in Data.Split('\n'))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#') continue;
                    var f = line.Split('|');
                    if (f[0] == "moon")
                    {
                        var e = nums(f[7]);
                        list.Add(new PlanetMoon
                        {
                            Name = f[1], Planet = (Body)Enum.Parse(typeof(Body), f[2]), Major = f[3] == "1",
                            AbsMag = double.Parse(f[4], c), RadiusKm = double.Parse(f[5], c), PeriodDays = double.Parse(f[6], c),
                            PoleRa = e[0], PoleDec = e[1], A = e[2], E = e[3], I0 = e[4], Id = e[5], N0 = e[6], Nd = e[7], W0 = e[8], Wd = e[9], L0 = e[10], Ld = e[11],
                        });
                        continue;
                    }
                    var moon = list.First(x => x.Name == f[1]);
                    if (f[0] == "corr")
                    {
                        moon.CorrStart = double.Parse(f[2], c); moon.CorrStep = double.Parse(f[3], c);
                        moon.CorrL = nums(f[4]);
                        if (f[5].Length > 0) moon.CorrR = nums(f[5]);
                        if (f.Length > 7) { moon.CorrK = nums(f[6]); moon.CorrH = nums(f[7]); }
                    }
                    else if (f[0] == "states")
                    {
                        moon.StateStart = double.Parse(f[2], c); moon.StateStep = double.Parse(f[3], c); moon.Gm = double.Parse(f[4], c);
                        moon.Step = double.Parse(f[5], c); moon.J2 = double.Parse(f[6], c); moon.J2RadiusKm = double.Parse(f[7], c);
                        moon.PullName = f[8].Length > 0 ? f[8] : null; moon.PullGm = double.Parse(f[9], c);
                        moon.States = f[10].Split(';').Select(nums).ToArray();
                    }
                }
                foreach (var m in list)
                {
                    // the orbit's pole against the planet's spin axis (the IAU north pole turns backwards for Uranus)
                    Vec3 h;
                    if (m.States != null) h = Vec3.Cross(new Vec3(m.States[0][0], m.States[0][1], m.States[0][2]), new Vec3(m.States[0][3], m.States[0][4], m.States[0][5]));
                    else
                    {
                        double i = m.I0 * AstroMath.Deg2Rad, n = m.N0 * AstroMath.Deg2Rad;
                        h = FrameToIcrf(m.PoleRa, m.PoleDec) * new Vec3(Math.Sin(i) * Math.Sin(n), -Math.Sin(i) * Math.Cos(n), Math.Cos(i));
                    }
                    double ra0, dec0, w0, w1;
                    PlanetPhysics.RotationElements(m.Planet, 2451545.0, out ra0, out dec0, out w0);
                    PlanetPhysics.RotationElements(m.Planet, 2451545.1, out ra0, out dec0, out w1);
                    var spin = Vec3.FromSpherical(ra0 * AstroMath.Deg2Rad, dec0 * AstroMath.Deg2Rad) * (AstroMath.NormalizeDeg180(w1 - w0) > 0 ? 1 : -1);
                    m.Retrograde = Vec3.Dot(h, spin) < 0;
                }
                _all = list;
            }
        }
    }
}
