using System;
using System.Collections.Generic;
using System.Linq;
using Sidralune.Core.Coordinates;
using Sidralune.Core.Mathematics;
using Sidralune.Core.Time;
using Sidralune.Core.Util;

namespace Sidralune.Core.Ephemeris
{
    public class RiseSetResult
    {
        public AstroTime? Rise, Transit, Set;
        public double TransitAltitudeDeg = double.NaN;
        public double RiseAzimuthDeg = double.NaN, SetAzimuthDeg = double.NaN;
        public bool AlwaysUp, NeverUp;
    }

    public class TwilightTimes
    {
        public AstroTime? SunSet, CivilDusk, NauticalDusk, AstronomicalDusk;
        public AstroTime? AstronomicalDawn, NauticalDawn, CivilDawn, SunRise;
        public AstroTime? SolarMidnight;
        /// <summary>Duration of astronomical darkness in hours.</summary>
        public double DarkHours;
    }

    public enum EventKind
    {
        NewMoon, FirstQuarter, FullMoon, LastQuarter, MarchEquinox, JuneSolstice, SeptemberEquinox, DecemberSolstice,
        Conjunction, Opposition, GreatestElongationEast, GreatestElongationWest, Perigee, Apogee, LunarEclipse, SolarEclipse,
        Occultation, MeteorShowerPeak, Other
    }

    public class AstroEvent
    {
        public AstroTime Time;
        public EventKind Kind;
        public string Description;
        public double Value;
        public override string ToString() => Time + "  " + Description;
    }

    /// <summary>Rise/set, twilight and almanac event finders.</summary>
    public static class Events
    {
        public const double SunStandardAltitude = -0.8333;
        public const double StarStandardAltitude = -0.5667;

        /// <summary>Geometric (unrefracted) altitude of a body in degrees at time t.</summary>
        public static double BodyAltitude(Body b, Observer obs, AstroTime t)
        {
            var ctx = new SkyContext(obs, t) { ApplyRefraction = false };
            return ctx.BodyPlace(b).Horizontal.AltDeg;
        }

        public static double StandardAltitude(Body b, double distAu = 0.00257)
        {
            if (b == Body.Sun) return SunStandardAltitude;
            if (b == Body.Moon)
            {
                // the altitudes compared with this are topocentric (BodyPlace is seen from the site), so the parallax is already
                // in them: the upper limb on the horizon is the centre one semidiameter (0.2725 × parallax) plus refraction below
                // it. The almanac value 0.7275 × parallax − 0.5667° is for geocentric altitudes; used here it counted the
                // parallax twice and put moonrise several minutes late and moonset early.
                double hp = Math.Asin(AstroMath.EarthRadius_km / (distAu * AstroMath.AU_km)) * AstroMath.Rad2Deg;
                return -0.2725 * hp - 0.5667;
            }
            return StarStandardAltitude;
        }

        /// <summary>
        /// Generic rise/transit/set search over [start, start+1 day) for an altitude function (deg, geometric) against a threshold.
        /// </summary>
        public static RiseSetResult RiseSet(Func<AstroTime, double> altitudeDeg, Func<AstroTime, double> azimuthDeg, AstroTime start, double thresholdDeg, double spanDays = 1.0)
        {
            var res = new RiseSetResult();
            double step = 1.0 / 48.0; // 30 minutes
            Func<double, double> f = jd => altitudeDeg(new AstroTime(jd)) - thresholdDeg;
            double a = start.JdUtc, fa = f(a);
            double maxAlt = double.MinValue, maxJd = a;
            bool anyUp = fa > 0, anyDown = fa <= 0;
            for (double t = a; t < start.JdUtc + spanDays - 1e-9; t += step)
            {
                double tb = Math.Min(t + step, start.JdUtc + spanDays);
                double fb = f(tb);
                if (fb > 0) anyUp = true; else anyDown = true;
                if (fa <= 0 && fb > 0 && res.Rise == null)
                    res.Rise = new AstroTime(Roots.Brent(f, t, tb, 1e-6));
                else if (fa > 0 && fb <= 0 && res.Set == null)
                    res.Set = new AstroTime(Roots.Brent(f, t, tb, 1e-6));
                double alt = fb + thresholdDeg;
                if (alt > maxAlt) { maxAlt = alt; maxJd = tb; }
                fa = fb;
            }
            // refine transit near maximum
            double tr = Roots.GoldenMin(jd => -altitudeDeg(new AstroTime(jd)), maxJd - step * 1.5, maxJd + step * 1.5, 1e-6);
            res.Transit = new AstroTime(tr);
            res.TransitAltitudeDeg = altitudeDeg(new AstroTime(tr));
            res.AlwaysUp = !anyDown;
            res.NeverUp = !anyUp;
            if (azimuthDeg != null)
            {
                if (res.Rise.HasValue) res.RiseAzimuthDeg = azimuthDeg(res.Rise.Value);
                if (res.Set.HasValue) res.SetAzimuthDeg = azimuthDeg(res.Set.Value);
            }
            return res;
        }

        public static RiseSetResult BodyRiseSet(Body b, Observer obs, AstroTime start, double spanDays = 1.0)
        {
            double thr = b == Body.Moon ? StandardAltitude(Body.Moon, 0.00257) : StandardAltitude(b);
            return RiseSet(t => BodyAltitude(b, obs, t),
                t => new SkyContext(obs, t) { ApplyRefraction = false }.BodyPlace(b).Horizontal.AzDeg, start, thr, spanDays);
        }

        /// <summary>Fixed-object (star/DSO) rise/set using catalog J2000 coordinates (radians).</summary>
        public static RiseSetResult FixedRiseSet(double ra, double dec, Observer obs, AstroTime start, double thresholdDeg = StarStandardAltitude, double spanDays = 1.0)
        {
            return RiseSet(t => FixedAltitude(ra, dec, obs, t), t => FixedAzimuth(ra, dec, obs, t), start, thresholdDeg, spanDays);
        }

        /// <summary>Fast geometric altitude for a fixed J2000 position (precession applied, nutation/aberration ignored).</summary>
        public static double FixedAltitude(double ra, double dec, Observer obs, AstroTime t)
        {
            double r2, d2;
            Precession.J2000ToMeanOfDate(ra, dec, t.T, out r2, out d2);
            double lst = t.Gmst + obs.LonRad;
            var h = Transforms.HaDecToAltAz(lst - r2, d2, obs.LatRad);
            return h.AltDeg;
        }

        public static double FixedAzimuth(double ra, double dec, Observer obs, AstroTime t)
        {
            double r2, d2;
            Precession.J2000ToMeanOfDate(ra, dec, t.T, out r2, out d2);
            double lst = t.Gmst + obs.LonRad;
            return Transforms.HaDecToAltAz(lst - r2, d2, obs.LatRad).AzDeg;
        }

        /// <summary>Sun altitude (deg) – fast low-precision path used heavily by planners.</summary>
        public static double SunAltitudeFast(Observer obs, AstroTime t)
        {
            double ra, dec;
            SunRaDecFast(t.JdTT, out ra, out dec);
            double lst = t.Gmst + obs.LonRad;
            return Transforms.HaDecToAltAz(lst - ra, dec, obs.LatRad).AltDeg;
        }

        /// <summary>Low-precision solar RA/Dec of date (radians), ~0.01°.</summary>
        public static void SunRaDecFast(double jdTT, out double ra, out double dec)
        {
            double n = jdTT - 2451545.0;
            double L = AstroMath.NormalizeDeg(280.460 + 0.9856474 * n);
            double g = AstroMath.NormalizeDeg(357.528 + 0.9856003 * n) * AstroMath.Deg2Rad;
            double lambda = (L + 1.915 * Math.Sin(g) + 0.020 * Math.Sin(2 * g)) * AstroMath.Deg2Rad;
            double eps = (23.439 - 0.0000004 * n) * AstroMath.Deg2Rad;
            ra = Math.Atan2(Math.Cos(eps) * Math.Sin(lambda), Math.Cos(lambda));
            dec = Math.Asin(Math.Sin(eps) * Math.Sin(lambda));
        }

        /// <summary>Twilight times for the night starting on the local date containing 'localNoonUtc' (pass local noon in UTC).</summary>
        public static TwilightTimes Twilight(Observer obs, AstroTime localNoonUtc)
        {
            var tw = new TwilightTimes();
            Func<double, double> sunAlt = jd => SunAltitudeFast(obs, new AstroTime(jd));
            double t0 = localNoonUtc.JdUtc, t1 = t0 + 1.0;
            Action<double, Action<AstroTime?>, Action<AstroTime?>> find = (thr, setDusk, setDawn) =>
            {
                var roots = Roots.FindAll(jd => sunAlt(jd) - thr, t0, t1, 1.0 / 96.0, 1e-6);
                foreach (var r in roots)
                {
                    double slope = sunAlt(r + 1e-3) - sunAlt(r - 1e-3);
                    if (slope < 0) setDusk(new AstroTime(r)); else setDawn(new AstroTime(r));
                }
            };
            find(SunStandardAltitude, v => tw.SunSet = v, v => tw.SunRise = v);
            find(-6, v => tw.CivilDusk = v, v => tw.CivilDawn = v);
            find(-12, v => tw.NauticalDusk = v, v => tw.NauticalDawn = v);
            find(-18, v => tw.AstronomicalDusk = v, v => tw.AstronomicalDawn = v);
            double mid = Roots.GoldenMin(sunAlt, t0 + 0.25, t0 + 0.75, 1e-5);
            tw.SolarMidnight = new AstroTime(mid);
            if (tw.AstronomicalDusk.HasValue && tw.AstronomicalDawn.HasValue)
                tw.DarkHours = (tw.AstronomicalDawn.Value.JdUtc - tw.AstronomicalDusk.Value.JdUtc) * 24;
            else if (sunAlt(mid) < -18) tw.DarkHours = 24;
            return tw;
        }

        // ------------------------------------------------------------------ Moon phases and seasons

        /// <summary>Elongation (Moon - Sun apparent ecliptic longitude) in degrees, 0..360.</summary>
        public static double MoonSunElongation(double jdTT)
        {
            double sl, sb, sr;
            PlanetPhysics.SunApparentEcliptic(jdTT, out sl, out sb, out sr);
            double ml, mb, md;
            LunarTheory.EclipticOfDate(jdTT, out ml, out mb, out md);
            return AstroMath.NormalizeDeg(ml * AstroMath.Rad2Deg - sl);
        }

        public static double MoonAgeDays(double jdTT)
        {
            // find previous new moon
            double e = MoonSunElongation(jdTT);
            double guess = jdTT - e / 360.0 * 29.530588;
            double nm = RefinePhase(guess, 0);
            if (nm > jdTT) nm = RefinePhase(nm - 29.530588, 0);
            return jdTT - nm;
        }

        private static double RefinePhase(double guess, double targetDeg)
        {
            Func<double, double> f = jd => AstroMath.NormalizeDeg180(MoonSunElongation(jd) - targetDeg);
            return BrentWrap(f, guess);
        }

        private static double BrentWrap(Func<double, double> f, double guess)
        {
            for (double w = 2; w <= 8; w += 2)
            {
                double a = guess - w, b = guess + w;
                if (f(a) * f(b) < 0 && Math.Abs(f(a) - f(b)) < 300)
                {
                    double r = Roots.Brent(f, a, b, 1e-7);
                    if (!double.IsNaN(r)) return r;
                }
            }
            return guess;
        }

        /// <summary>All principal Moon phases between jd1 and jd2 (TT).</summary>
        public static List<AstroEvent> MoonPhases(double jd1, double jd2)
        {
            var list = new List<AstroEvent>();
            var names = new[] { Lang.T("New Moon"), Lang.T("First Quarter"), Lang.T("Full Moon"), Lang.T("Last Quarter") };
            var kinds = new[] { EventKind.NewMoon, EventKind.FirstQuarter, EventKind.FullMoon, EventKind.LastQuarter };
            for (int q = 0; q < 4; q++)
            {
                double target = q * 90.0;
                Func<double, double> f = jd => AstroMath.NormalizeDeg180(MoonSunElongation(jd) - target);
                double step = 1.0;
                double a = jd1 - 1, fa = f(a);
                for (double t = jd1 - 1; t < jd2; t += step)
                {
                    double fb = f(t + step);
                    if (fa < 0 && fb >= 0 && fb - fa < 90)
                    {
                        double r = Roots.Brent(f, t, t + step, 1e-7);
                        if (r >= jd1 && r <= jd2)
                            list.Add(new AstroEvent { Time = AstroTime.FromTT(r), Kind = kinds[q], Description = names[q] });
                    }
                    fa = fb;
                }
            }
            list.Sort((x, y) => x.Time.JdUtc.CompareTo(y.Time.JdUtc));
            return list;
        }

        /// <summary>Equinoxes and solstices within a year.</summary>
        public static List<AstroEvent> Seasons(int year)
        {
            var list = new List<AstroEvent>();
            var kinds = new[] { EventKind.MarchEquinox, EventKind.JuneSolstice, EventKind.SeptemberEquinox, EventKind.DecemberSolstice };
            var names = new[] { Lang.T("March equinox"), Lang.T("June solstice"), Lang.T("September equinox"), Lang.T("December solstice") };
            for (int k = 0; k < 4; k++)
            {
                double target = k * 90.0;
                double guess = AstroTime.CalendarToJd(year, 3 + 3 * k, 20.5);
                Func<double, double> f = jd =>
                {
                    double l, b, r;
                    PlanetPhysics.SunApparentEcliptic(jd, out l, out b, out r);
                    return AstroMath.NormalizeDeg180(l - target);
                };
                double root = Roots.Brent(f, guess - 5, guess + 5, 1e-7);
                if (!double.IsNaN(root)) list.Add(new AstroEvent { Time = AstroTime.FromTT(root), Kind = kinds[k], Description = names[k] });
            }
            return list;
        }

        /// <summary>Conjunctions/oppositions with the Sun and greatest elongations for a planet over a span.</summary>
        public static List<AstroEvent> PlanetEvents(Body b, double jd1, double jd2)
        {
            var list = new List<AstroEvent>();
            Func<double, double> lonDiff = jd =>
            {
                Vec3 e, v;
                EphemerisManager.EarthHeliocentric(jd, out e, out v);
                var p = EphemerisManager.Heliocentric(b, jd);
                var geo = Transforms.EquatorialToEcliptic(p - e, Precession.ObliquityJ2000);
                var sun = Transforms.EquatorialToEcliptic(-e, Precession.ObliquityJ2000);
                double l1, b1, l2, b2;
                geo.ToSpherical(out l1, out b1);
                sun.ToSpherical(out l2, out b2);
                return AstroMath.NormalizeDeg180((l1 - l2) * AstroMath.Rad2Deg);
            };
            double step = 2.0;
            bool inferior = b == Body.Mercury || b == Body.Venus;
            double prev = lonDiff(jd1);
            double prevPrev = double.NaN;
            for (double t = jd1 + step; t <= jd2; t += step)
            {
                double cur = lonDiff(t);
                if (Math.Sign(prev) != Math.Sign(cur) && Math.Abs(prev) < 90 && Math.Abs(cur) < 90)
                {
                    double r = Roots.Brent(lonDiff, t - step, t, 1e-6);
                    if (!double.IsNaN(r))
                    {
                        string what = Lang.T("{0} in conjunction with the Sun", BodyNames.Name(b));
                        if (inferior)
                        {
                            Vec3 e, v;
                            EphemerisManager.EarthHeliocentric(r, out e, out v);
                            what = (EphemerisManager.Heliocentric(b, r) - e).Length < e.Length ? Lang.T("{0} at inferior conjunction", BodyNames.Name(b)) : Lang.T("{0} at superior conjunction", BodyNames.Name(b));
                        }
                        list.Add(new AstroEvent { Time = AstroTime.FromTT(r), Kind = EventKind.Conjunction, Description = what });
                    }
                }
                if (!inferior && Math.Abs(prev) > 170 && Math.Sign(prev) != Math.Sign(cur) && Math.Abs(cur) > 170)
                {
                    Func<double, double> g = jd => AstroMath.NormalizeDeg180(lonDiff(jd) - 180);
                    double r = Roots.Brent(g, t - step, t, 1e-6);
                    if (!double.IsNaN(r)) list.Add(new AstroEvent { Time = AstroTime.FromTT(r), Kind = EventKind.Opposition, Description = Lang.T("{0} at opposition", BodyNames.Name(b)) });
                }
                if (inferior && !double.IsNaN(prevPrev))
                {
                    // extremum of |elongation|
                    if (Math.Abs(prev) > Math.Abs(prevPrev) && Math.Abs(prev) > Math.Abs(cur) && Math.Abs(prev) > 15)
                    {
                        double r = Roots.GoldenMin(jd => -Math.Abs(lonDiff(jd)), t - 2 * step, t, 1e-5);
                        double el = lonDiff(r);
                        list.Add(new AstroEvent
                        {
                            Time = AstroTime.FromTT(r),
                            Kind = el > 0 ? EventKind.GreatestElongationEast : EventKind.GreatestElongationWest,
                            Description = el > 0 ? Lang.T("{0} greatest elongation east ({1:0.0}°)", BodyNames.Name(b), Math.Abs(el)) : Lang.T("{0} greatest elongation west ({1:0.0}°)", BodyNames.Name(b), Math.Abs(el)),
                            Value = Math.Abs(el)
                        });
                    }
                }
                prevPrev = prev;
                prev = cur;
            }
            return list;
        }

        /// <summary>Moon perigee/apogee events.</summary>
        public static List<AstroEvent> MoonDistanceExtremes(double jd1, double jd2)
        {
            var list = new List<AstroEvent>();
            Func<double, double> dist = jd => { double l, b, d; LunarTheory.EclipticOfDate(jd, out l, out b, out d); return d; };
            double step = 0.5;
            double p0 = dist(jd1 - step), p1 = dist(jd1);
            for (double t = jd1 + step; t <= jd2 + step; t += step)
            {
                double p2 = dist(t);
                if (p1 < p0 && p1 < p2)
                {
                    double r = Roots.GoldenMin(dist, t - 2 * step, t, 1e-5);
                    list.Add(new AstroEvent { Time = AstroTime.FromTT(r), Kind = EventKind.Perigee, Description = Lang.T("Moon at perigee ({0:N0} km)", dist(r)), Value = dist(r) });
                }
                if (p1 > p0 && p1 > p2)
                {
                    double r = Roots.GoldenMin(jd => -dist(jd), t - 2 * step, t, 1e-5);
                    list.Add(new AstroEvent { Time = AstroTime.FromTT(r), Kind = EventKind.Apogee, Description = Lang.T("Moon at apogee ({0:N0} km)", dist(r)), Value = dist(r) });
                }
                p0 = p1; p1 = p2;
            }
            return list;
        }

        /// <summary>The Moon against the Earth's shadow at time t (TT): its distance from the shadow axis, the umbra and penumbra radii (Danjon rule) and its own radius, degrees.</summary>
        private static void Shadow(double t, out double s, out double umbra, out double penumbra, out double sM)
        {
            Vec3 e, v;
            EphemerisManager.EarthHeliocentric(t, out e, out v);
            var moon = LunarTheory.GeocentricEquatorialJ2000(t);
            s = Vec3.AngleBetween(e.Normalized(), moon) * AstroMath.Rad2Deg;
            double ml, mb, md;
            LunarTheory.EclipticOfDate(t, out ml, out mb, out md);
            double piM = Math.Asin(AstroMath.EarthRadius_km / md) * AstroMath.Rad2Deg;
            double piS = 8.794 / 3600.0 / e.Length;
            double sS = 959.63 / 3600.0 / e.Length;
            sM = 0.2725076 * piM;
            umbra = 1.02 * (piM + piS - sS);
            penumbra = 1.02 * (piM + piS + sS);
        }

        private static double ShadowDistance(double t) { double s, u, p, m; Shadow(t, out s, out u, out p, out m); return s; }

        /// <summary>A lunar eclipse found by <see cref="FindLunarEclipses"/>: greatest eclipse (TT), kind (0 penumbral, 1 partial, 2 total) and magnitudes.</summary>
        private class LunarFound { public double Jd; public int Kind; public double Umag, Pmag; }

        private static List<LunarFound> FindLunarEclipses(double jd1, double jd2)
        {
            var list = new List<LunarFound>();
            foreach (var fm in MoonPhases(jd1, jd2))
            {
                if (fm.Kind != EventKind.FullMoon) continue;
                double jd = fm.Time.JdTT;
                double tmin = Roots.GoldenMin(ShadowDistance, jd - 0.3, jd + 0.3, 1e-6);
                double s, umbra, penumbra, sM;
                Shadow(tmin, out s, out umbra, out penumbra, out sM);
                if (s < penumbra + sM)
                    list.Add(new LunarFound { Jd = tmin, Kind = s + sM < umbra ? 2 : s - sM < umbra ? 1 : 0, Umag = (umbra + sM - s) / (2 * sM), Pmag = (penumbra + sM - s) / (2 * sM) });
            }
            return list;
        }

        private static string LunarTypeName(int kind) => kind == 2 ? Lang.T("Total lunar eclipse") : kind == 1 ? Lang.T("Partial lunar eclipse") : Lang.T("Penumbral lunar eclipse");

        private static AstroEvent LunarEvent(LunarFound f, string description) =>
            new AstroEvent { Time = AstroTime.FromTT(f.Jd), Kind = EventKind.LunarEclipse, Value = f.Umag > 0 ? f.Umag : f.Pmag, Description = description };

        /// <summary>
        /// Lunar eclipses: at each full moon test the Moon's distance from the Earth's shadow axis (umbra/penumbra radii via Danjon rule).
        /// </summary>
        public static List<AstroEvent> LunarEclipses(double jd1, double jd2)
        {
            var list = new List<AstroEvent>();
            foreach (var f in FindLunarEclipses(jd1, jd2))
                list.Add(LunarEvent(f, Lang.T("{0} (umbral mag {1:0.000}, penumbral {2:0.000})", LunarTypeName(f.Kind), f.Umag, f.Pmag)));
            return list;
        }

        /// <summary>
        /// Lunar eclipses as seen from a site. Greatest eclipse is the same moment everywhere, so the time stays; the text says
        /// when the Moon is below the horizon for the whole eclipse, or rises or sets during it.
        /// </summary>
        public static List<AstroEvent> LunarEclipses(Observer site, double jd1, double jd2)
        {
            var list = new List<AstroEvent>();
            foreach (var f in FindLunarEclipses(jd1, jd2))
            {
                var l = LunarEclipseAt(site, AstroTime.FromTT(f.Jd));
                string type = LunarTypeName(f.Kind);
                string text = !l.Visible ? Lang.T("{0} (not visible from {1})", type, site.Name)
                    : Lang.T("{0} (umbral mag {1:0.000}, penumbral {2:0.000})", type, f.Umag, f.Pmag)
                      + (l.Horizon > 0 ? ", " + Lang.T("the Moon sets during the eclipse") : l.Horizon < 0 ? ", " + Lang.T("the Moon rises during the eclipse") : "");
                list.Add(LunarEvent(f, text));
            }
            return list;
        }

        /// <summary>
        /// A lunar eclipse from a site, from its greatest eclipse: the umbral phase (the penumbral one for a penumbral eclipse)
        /// and whether the Moon is above the horizon during it.
        /// </summary>
        public static LocalEclipse LunarEclipseAt(Observer site, AstroTime greatest)
        {
            double tg = greatest.JdTT, s0, umbra0, penumbra0, sM0;
            Shadow(tg, out s0, out umbra0, out penumbra0, out sM0);
            bool umbral = s0 - sM0 < umbra0;
            // inside the phase while the Moon's disc touches the umbra (penumbra)
            Func<double, double> inside = t => { double s, u, p, m; Shadow(t, out s, out u, out p, out m); return (umbral ? u : p) + m - s; };
            var res = new LocalEclipse { Time = greatest, Maximum = greatest, Covers = true };
            double c1 = Contact(inside, tg, -1), c4 = Contact(inside, tg, +1);
            res.Start = AstroTime.FromTT(c1); res.End = AstroTime.FromTT(c4);
            Func<double, bool> up = t =>
            {
                var ctx = new SkyContext(site, AstroTime.FromTT(t)) { ApplyRefraction = false };
                var p = ctx.BodyPlace(Body.Moon);
                return p.Horizontal.AltDeg > StandardAltitude(Body.Moon, p.Distance);
            };
            Visibility(up, c1, tg, c4, res);
            return res;
        }

        /// <summary>A solar eclipse found by <see cref="FindSolarEclipses"/>: greatest eclipse (TT), kind (0 partial, 1 total, 2 annular) and the geocentric Sun-Moon separation.</summary>
        private class SolarFound { public double Jd; public int Kind; public double Sep; }

        private static List<SolarFound> FindSolarEclipses(double jd1, double jd2)
        {
            var list = new List<SolarFound>();
            foreach (var nm in MoonPhases(jd1, jd2))
            {
                if (nm.Kind != EventKind.NewMoon) continue;
                double jd = nm.Time.JdTT;
                Func<double, double> sep = t =>
                {
                    Vec3 e, v;
                    EphemerisManager.EarthHeliocentric(t, out e, out v);
                    var moon = LunarTheory.GeocentricEquatorialJ2000(t);
                    return Vec3.AngleBetween(-e, moon) * AstroMath.Rad2Deg;
                };
                double tmin = Roots.GoldenMin(sep, jd - 0.3, jd + 0.3, 1e-6);
                double s = sep(tmin);
                Vec3 ee, ev;
                EphemerisManager.EarthHeliocentric(tmin, out ee, out ev);
                double ml, mb, md;
                LunarTheory.EclipticOfDate(tmin, out ml, out mb, out md);
                double piM = Math.Asin(AstroMath.EarthRadius_km / md) * AstroMath.Rad2Deg;
                double sS = 959.63 / 3600.0 / ee.Length;
                double sM = 0.2725076 * piM;
                if (s < piM + sS + sM)
                    list.Add(new SolarFound { Jd = tmin, Sep = s, Kind = s < piM - Math.Abs(sM - sS) ? (sM > sS ? 1 : 2) : 0 });
            }
            return list;
        }

        private static string SolarTypeName(int kind) => kind == 1 ? Lang.T("Total solar eclipse") : kind == 2 ? Lang.T("Annular solar eclipse") : Lang.T("Partial solar eclipse");

        /// <summary>Solar eclipses (geocentric test at new moon: Sun-Moon separation vs. sum of radii + parallax), at greatest eclipse.</summary>
        public static List<AstroEvent> SolarEclipses(double jd1, double jd2)
        {
            var list = new List<AstroEvent>();
            foreach (var f in FindSolarEclipses(jd1, jd2))
                list.Add(new AstroEvent { Time = AstroTime.FromTT(f.Jd), Kind = EventKind.SolarEclipse, Description = Lang.T("{0} (visible somewhere on Earth)", SolarTypeName(f.Kind)), Value = f.Sep });
            return list;
        }

        /// <summary>
        /// Solar eclipses as seen from a site: at the local maximum (or the sunrise or sunset during the eclipse when the
        /// maximum is below the horizon), with the local type and how much of the Sun is covered, and what it is elsewhere.
        /// An eclipse not seen from the site keeps the time of greatest eclipse and says so.
        /// </summary>
        public static List<AstroEvent> SolarEclipses(Observer site, double jd1, double jd2)
        {
            var list = new List<AstroEvent>();
            foreach (var f in FindSolarEclipses(jd1, jd2))
            {
                var l = SolarEclipseAt(site, AstroTime.FromTT(f.Jd));
                string text;
                if (!l.Visible) text = Lang.T("{0} (not visible from {1})", SolarTypeName(f.Kind), site.Name);
                else
                {
                    text = SolarTypeName(l.Kind);
                    if (l.Kind != 1) text += ", " + Lang.T("{0:0}% of the Sun covered", l.Obscuration * 100);
                    if (l.AtHorizon) text += " " + (l.Horizon > 0 ? Lang.T("at sunset") : Lang.T("at sunrise"));
                    if (f.Kind != 0 && f.Kind != l.Kind) text += " (" + (f.Kind == 1 ? Lang.T("total elsewhere") : Lang.T("annular elsewhere")) + ")";
                }
                list.Add(new AstroEvent { Time = l.Visible ? l.Time : AstroTime.FromTT(f.Jd), Kind = EventKind.SolarEclipse, Description = text, Value = l.Visible ? l.Magnitude : 0 });
            }
            return list;
        }

        /// <summary>The Sun and the Moon from a site at time t (TT): their separation and radii in degrees, and whether the Sun is above the horizon.</summary>
        private static void SunAndMoon(Observer site, double t, out double sep, out double sS, out double sM, out bool sunUp)
        {
            var ctx = new SkyContext(site, AstroTime.FromTT(t)) { ApplyRefraction = false };
            double lt;
            var sun = ctx.TopocentricWithLightTime(jd => EphemerisManager.Position(Body.Sun, jd), out lt);
            var moon = ctx.TopocentricWithLightTime(jd => EphemerisManager.Position(Body.Moon, jd), out lt);
            sep = Vec3.AngleBetween(sun, moon) * AstroMath.Rad2Deg;
            sS = 959.63 / 3600.0 / sun.Length;
            sM = Math.Asin(0.2725076 * AstroMath.EarthRadius_km / (moon.Length * AstroMath.AU_km)) * AstroMath.Rad2Deg;
            sunUp = ctx.TodToHorizontal(ctx.IcrsToApparentTod(sun.Normalized())).AltDeg > SunStandardAltitude;
        }

        /// <summary>
        /// A solar eclipse from a site, from its greatest eclipse: the partial phase there (first and last contact), the local
        /// maximum with its magnitude, the share of the Sun's disc covered and the local type, and whether the Sun is up.
        /// </summary>
        public static LocalEclipse SolarEclipseAt(Observer site, AstroTime greatest)
        {
            double tg = greatest.JdTT, step = 2 / 1440.0;
            Func<double, double> sepAt = t => { double s, a, b; bool u; SunAndMoon(site, t, out s, out a, out b, out u); return s; };
            Func<double, double> overlap = t => { double s, a, b; bool u; SunAndMoon(site, t, out s, out a, out b, out u); return a + b - s; };
            // the local maximum is within a few hours of greatest eclipse: the closest approach in 2-minute steps, then refined
            double tBest = tg, best = double.MaxValue;
            for (double t = tg - 0.17; t <= tg + 0.17; t += step)
            {
                double s = sepAt(t);
                if (s < best) { best = s; tBest = t; }
            }
            double tmax = Roots.GoldenMin(sepAt, tBest - step, tBest + step, 1e-7);
            var res = new LocalEclipse { Time = AstroTime.FromTT(tmax), Maximum = AstroTime.FromTT(tmax) };
            if (overlap(tmax) <= 0) return res; // the Moon passes by the Sun without covering it from here
            res.Covers = true;
            double c1 = Contact(overlap, tmax, -1), c4 = Contact(overlap, tmax, +1);
            res.Start = AstroTime.FromTT(c1); res.End = AstroTime.FromTT(c4);
            Func<double, bool> up = t => { double s, a, b; bool u; SunAndMoon(site, t, out s, out a, out b, out u); return u; };
            Visibility(up, c1, tmax, c4, res);
            if (res.Visible && !up(tmax))
            {
                // the maximum is below the horizon: the most of it seen is at the sunrise after it or the sunset before it
                double edge = up(c1) ? c1 : c4;
                double lo = Math.Min(edge, tmax), hi = Math.Max(edge, tmax);
                for (int i = 0; i < 40; i++) { double mid = (lo + hi) / 2; if (up(mid) == up(lo)) lo = mid; else hi = mid; }
                res.Time = AstroTime.FromTT(up(lo) ? lo : hi);
                res.AtHorizon = true;
            }
            double sep, sS, sM; bool sunUp;
            SunAndMoon(site, res.Time.JdTT, out sep, out sS, out sM, out sunUp);
            res.Magnitude = (sS + sM - sep) / (2 * sS);
            res.Obscuration = DiscOverlap(sS, sM, sep);
            res.Kind = sep <= sM - sS ? 1 : sep <= sS - sM ? 2 : 0;
            return res;
        }

        /// <summary>The share of a disc of radius r1 covered by a disc of radius r2 whose centre is d away.</summary>
        public static double DiscOverlap(double r1, double r2, double d)
        {
            if (d >= r1 + r2) return 0;
            if (d <= Math.Abs(r1 - r2)) return r2 >= r1 ? 1 : (r2 * r2) / (r1 * r1);
            double a1 = r1 * r1 * Math.Acos((d * d + r1 * r1 - r2 * r2) / (2 * d * r1));
            double a2 = r2 * r2 * Math.Acos((d * d + r2 * r2 - r1 * r1) / (2 * d * r2));
            double k = 0.5 * Math.Sqrt((-d + r1 + r2) * (d + r1 - r2) * (d - r1 + r2) * (d + r1 + r2));
            return (a1 + a2 - k) / (Math.PI * r1 * r1);
        }

        /// <summary>The moment f falls to zero going from t0 (where f &gt; 0) in direction dir (−1 earlier, +1 later), within 6 hours.</summary>
        private static double Contact(Func<double, double> f, double t0, int dir)
        {
            double step = 5 / 1440.0, a = t0, b = t0;
            for (int i = 0; i < 72; i++) { b = a + dir * step; if (f(b) <= 0) break; a = b; }
            for (int i = 0; i < 40; i++) { double mid = (a + b) / 2; if (f(mid) > 0) a = mid; else b = mid; }
            return (a + b) / 2;
        }

        /// <summary>Whether the body is up at some minute between c1 and c4, and whether it rises (−1) or sets (+1) during that time.</summary>
        private static void Visibility(Func<double, bool> up, double c1, double tmax, double c4, LocalEclipse res)
        {
            bool atStart = up(c1), atEnd = up(c4), any = atStart || atEnd || up(tmax);
            for (double t = c1; !any && t < c4; t += 1 / 1440.0) any = up(t);
            res.Visible = any;
            res.Horizon = !any || (atStart && atEnd) ? 0 : atStart ? 1 : -1;
        }

        /// <summary>The name of an eclipse type: solar 0 partial, 1 total, 2 annular; lunar 0 penumbral, 1 partial, 2 total.</summary>
        public static string EclipseTypeName(bool solar, int kind) => solar ? SolarTypeName(kind) : LunarTypeName(kind);

        /// <summary>
        /// Every solar and lunar eclipse between jd1 and jd2 (TT) in time order, each with its global type and as seen from the
        /// site. Solar eclipses are sorted by greatest eclipse, like the lunar ones.
        /// </summary>
        public static List<EclipseInfo> Eclipses(Observer site, double jd1, double jd2, System.Threading.CancellationToken ct = default(System.Threading.CancellationToken))
        {
            var list = new List<EclipseInfo>();
            foreach (var f in FindSolarEclipses(jd1, jd2))
            {
                ct.ThrowIfCancellationRequested();
                var g = AstroTime.FromTT(f.Jd);
                list.Add(new EclipseInfo { Solar = true, Greatest = g, Kind = f.Kind, Local = SolarEclipseAt(site, g) });
            }
            foreach (var f in FindLunarEclipses(jd1, jd2))
            {
                ct.ThrowIfCancellationRequested();
                var g = AstroTime.FromTT(f.Jd);
                list.Add(new EclipseInfo { Solar = false, Greatest = g, Kind = f.Kind, Magnitude = f.Kind > 0 ? f.Umag : f.Pmag, Local = LunarEclipseAt(site, g) });
            }
            return list.OrderBy(e => e.Greatest.JdTT).ToList();
        }

        /// <summary>
        /// The phases of a solar eclipse from a site, in time order, with the Sun's altitude at each: first contact, the total
        /// or annular phase, the local maximum, last contact, any sunrise or sunset in between, and greatest eclipse on Earth.
        /// </summary>
        public static List<EclipsePhase> SolarEclipsePhases(Observer site, AstroTime greatest)
        {
            var l = SolarEclipseAt(site, greatest);
            var list = new List<EclipsePhase>();
            Func<string, double, EclipsePhase> phase = (name, t) => new EclipsePhase { Name = name, Time = AstroTime.FromTT(t), Altitude = Events.Altitude(site, Body.Sun, t) };
            list.Add(phase(Lang.T("Greatest eclipse on Earth"), greatest.JdTT));
            if (l.Covers)
            {
                double tmax = l.Maximum.JdTT;
                list.Add(phase(Lang.T("Partial eclipse begins"), l.Start.Value.JdTT));
                list.Add(phase(Lang.T("Maximum eclipse"), tmax));
                list.Add(phase(Lang.T("Partial eclipse ends"), l.End.Value.JdTT));
                double s0, sS0, sM0; bool u0;
                SunAndMoon(site, tmax, out s0, out sS0, out sM0, out u0);
                if (s0 < Math.Abs(sM0 - sS0))
                {
                    // the Moon's disc wholly inside the Sun's (annular) or over it (total)
                    bool total = sM0 > sS0;
                    Func<double, double> central = t => { double s, a, b; bool u; SunAndMoon(site, t, out s, out a, out b, out u); return Math.Abs(b - a) - s; };
                    list.Add(phase(total ? Lang.T("Total eclipse begins") : Lang.T("Annular eclipse begins"), Contact(central, tmax, -1)));
                    list.Add(phase(total ? Lang.T("Total eclipse ends") : Lang.T("Annular eclipse ends"), Contact(central, tmax, +1)));
                }
                Func<double, bool> up = t => { double s, a, b; bool u; SunAndMoon(site, t, out s, out a, out b, out u); return u; };
                foreach (var x in HorizonCrossings(up, l.Start.Value.JdTT, l.End.Value.JdTT))
                    list.Add(phase(x.Item2 ? Lang.T("Sunrise") : Lang.T("Sunset"), x.Item1));
            }
            return list.OrderBy(p => p.Time.JdTT).ToList();
        }

        /// <summary>
        /// The phases of a lunar eclipse, in time order, with the Moon's altitude at the site at each: the penumbral, partial and
        /// total phases, greatest eclipse, and any moonrise or moonset in between.
        /// </summary>
        public static List<EclipsePhase> LunarEclipsePhases(Observer site, AstroTime greatest)
        {
            double tg = greatest.JdTT, s0, u0, p0, m0;
            Shadow(tg, out s0, out u0, out p0, out m0);
            var list = new List<EclipsePhase>();
            Func<string, double, EclipsePhase> phase = (name, t) => new EclipsePhase { Name = name, Time = AstroTime.FromTT(t), Altitude = Events.Altitude(site, Body.Moon, t) };
            list.Add(phase(Lang.T("Greatest eclipse"), tg));
            // inside each phase while the Moon's disc touches the penumbra, touches the umbra, or is wholly in the umbra
            Func<double, double> penumbral = t => { double s, u, p, m; Shadow(t, out s, out u, out p, out m); return p + m - s; };
            Func<double, double> partial = t => { double s, u, p, m; Shadow(t, out s, out u, out p, out m); return u + m - s; };
            Func<double, double> total = t => { double s, u, p, m; Shadow(t, out s, out u, out p, out m); return u - m - s; };
            double p1 = Contact(penumbral, tg, -1), p4 = Contact(penumbral, tg, +1);
            list.Add(phase(Lang.T("Penumbral eclipse begins"), p1));
            list.Add(phase(Lang.T("Penumbral eclipse ends"), p4));
            if (partial(tg) > 0)
            {
                list.Add(phase(Lang.T("Partial eclipse begins"), Contact(partial, tg, -1)));
                list.Add(phase(Lang.T("Partial eclipse ends"), Contact(partial, tg, +1)));
            }
            if (total(tg) > 0)
            {
                list.Add(phase(Lang.T("Total eclipse begins"), Contact(total, tg, -1)));
                list.Add(phase(Lang.T("Total eclipse ends"), Contact(total, tg, +1)));
            }
            Func<double, bool> up = t =>
            {
                var ctx = new SkyContext(site, AstroTime.FromTT(t)) { ApplyRefraction = false };
                var pl = ctx.BodyPlace(Body.Moon);
                return pl.Horizontal.AltDeg > StandardAltitude(Body.Moon, pl.Distance);
            };
            foreach (var x in HorizonCrossings(up, p1, p4))
                list.Add(phase(x.Item2 ? Lang.T("Moonrise") : Lang.T("Moonset"), x.Item1));
            return list.OrderBy(p => p.Time.JdTT).ToList();
        }

        /// <summary>
        /// The contacts of a lunar eclipse, the same moments everywhere: P1, U1, U2, MAX, U3, U4, P4 (those there are), in
        /// time order.
        /// </summary>
        public static List<Tuple<string, AstroTime>> LunarContacts(AstroTime greatest)
        {
            double tg = greatest.JdTT;
            Func<double, double> penumbral = t => { double s, u, p, m; Shadow(t, out s, out u, out p, out m); return p + m - s; };
            Func<double, double> partial = t => { double s, u, p, m; Shadow(t, out s, out u, out p, out m); return u + m - s; };
            Func<double, double> total = t => { double s, u, p, m; Shadow(t, out s, out u, out p, out m); return u - m - s; };
            var list = new List<Tuple<string, double>> { Tuple.Create("MAX", tg) };
            if (penumbral(tg) > 0) { list.Add(Tuple.Create("P1", Contact(penumbral, tg, -1))); list.Add(Tuple.Create("P4", Contact(penumbral, tg, +1))); }
            if (partial(tg) > 0) { list.Add(Tuple.Create("U1", Contact(partial, tg, -1))); list.Add(Tuple.Create("U4", Contact(partial, tg, +1))); }
            if (total(tg) > 0) { list.Add(Tuple.Create("U2", Contact(total, tg, -1))); list.Add(Tuple.Create("U3", Contact(total, tg, +1))); }
            return list.OrderBy(x => x.Item2).Select(x => Tuple.Create(x.Item1, AstroTime.FromTT(x.Item2))).ToList();
        }

        /// <summary>Observed (refracted) altitude of the Sun or Moon in degrees at time t (TT).</summary>
        private static double Altitude(Observer site, Body b, double t) => new SkyContext(site, AstroTime.FromTT(t)).BodyPlace(b).Horizontal.AltDeg;

        /// <summary>The moments between t1 and t2 when up changes, found in 2-minute steps and refined: (time, true for a rising).</summary>
        private static List<Tuple<double, bool>> HorizonCrossings(Func<double, bool> up, double t1, double t2)
        {
            var list = new List<Tuple<double, bool>>();
            double step = 2 / 1440.0, a = t1;
            bool ua = up(a);
            while (a < t2)
            {
                double b = Math.Min(a + step, t2);
                bool ub = up(b);
                if (ub != ua)
                {
                    double lo = a, hi = b;
                    for (int i = 0; i < 30; i++) { double mid = (lo + hi) / 2; if (up(mid) == ua) lo = mid; else hi = mid; }
                    list.Add(Tuple.Create((lo + hi) / 2, ub));
                }
                a = b; ua = ub;
            }
            return list;
        }
    }

    /// <summary>An eclipse in a list from <see cref="Events.Eclipses"/>: its global type and how it looks from the site.</summary>
    public class EclipseInfo
    {
        public bool Solar;
        /// <summary>Greatest eclipse on Earth.</summary>
        public AstroTime Greatest;
        /// <summary>The global type: solar 0 partial, 1 total, 2 annular; lunar 0 penumbral, 1 partial, 2 total.</summary>
        public int Kind;
        /// <summary>Lunar: the umbral magnitude (the penumbral one for a penumbral eclipse). Solar: see Local.</summary>
        public double Magnitude;
        public LocalEclipse Local;
        public string TypeName => Events.EclipseTypeName(Solar, Kind);
    }

    /// <summary>One phase of an eclipse from a site: what happens, when, and the Sun's (lunar: the Moon's) observed altitude then.</summary>
    public class EclipsePhase
    {
        public string Name;
        public AstroTime Time;
        public double Altitude;
    }

    /// <summary>A solar or lunar eclipse as seen from one site.</summary>
    public class LocalEclipse
    {
        /// <summary>Solar: the Moon covers part of the Sun from the site at some moment, whether the Sun is up or not. Lunar: always.</summary>
        public bool Covers;
        /// <summary>Part of the eclipse happens with the Sun (lunar: the Moon) above the site's horizon.</summary>
        public bool Visible;
        /// <summary>
        /// Solar: the local maximum, or when that is below the horizon the sunrise or sunset during the eclipse (see AtHorizon).
        /// Lunar: greatest eclipse, the same moment everywhere.
        /// </summary>
        public AstroTime Time;
        /// <summary>Solar: the local maximum, whether the Sun is up then or not. Lunar: greatest eclipse.</summary>
        public AstroTime Maximum;
        /// <summary>The phase that counts: solar the partial phase at the site, lunar the umbral phase (the penumbral one for a penumbral eclipse).</summary>
        public AstroTime? Start, End;
        /// <summary>−1 the Sun (Moon) rises during the eclipse, +1 it sets during it, 0 it is up all through (or the eclipse is not visible).</summary>
        public int Horizon;
        /// <summary>Solar: Time is the sunrise or sunset, because the maximum is below the horizon.</summary>
        public bool AtHorizon;
        /// <summary>Solar at Time: the fraction of the Sun's diameter covered, and of its disc.</summary>
        public double Magnitude, Obscuration;
        /// <summary>Solar at Time: 0 partial, 1 total, 2 annular.</summary>
        public int Kind;
    }
}
