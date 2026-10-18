using System;
using System.Collections.Generic;
using Sidralune.Core.Coordinates;
using Sidralune.Core.Mathematics;
using Sidralune.Core.Time;

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
                double hp = Math.Asin(AstroMath.EarthRadius_km / (distAu * AstroMath.AU_km)) * AstroMath.Rad2Deg;
                return 0.7275 * hp - 0.5667;
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
            var names = new[] { "New Moon", "First Quarter", "Full Moon", "Last Quarter" };
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
            var names = new[] { "March equinox", "June solstice", "September equinox", "December solstice" };
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
                if (prev < 0 && cur >= 0 && cur - prev < 90)
                {
                    double r = Roots.Brent(lonDiff, t - step, t, 1e-6);
                    list.Add(new AstroEvent { Time = AstroTime.FromTT(r), Kind = EventKind.Conjunction, Description = b + " in conjunction with the Sun" });
                }
                if (!inferior && Math.Abs(prev) > 170 && Math.Sign(prev) != Math.Sign(cur) && Math.Abs(cur) > 170)
                {
                    Func<double, double> g = jd => AstroMath.NormalizeDeg180(lonDiff(jd) - 180);
                    double r = Roots.Brent(g, t - step, t, 1e-6);
                    if (!double.IsNaN(r)) list.Add(new AstroEvent { Time = AstroTime.FromTT(r), Kind = EventKind.Opposition, Description = b + " at opposition" });
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
                            Description = string.Format("{0} greatest elongation {1} ({2:0.0}°)", b, el > 0 ? "east" : "west", Math.Abs(el)),
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
                    list.Add(new AstroEvent { Time = AstroTime.FromTT(r), Kind = EventKind.Perigee, Description = string.Format("Moon at perigee ({0:N0} km)", dist(r)), Value = dist(r) });
                }
                if (p1 > p0 && p1 > p2)
                {
                    double r = Roots.GoldenMin(jd => -dist(jd), t - 2 * step, t, 1e-5);
                    list.Add(new AstroEvent { Time = AstroTime.FromTT(r), Kind = EventKind.Apogee, Description = string.Format("Moon at apogee ({0:N0} km)", dist(r)), Value = dist(r) });
                }
                p0 = p1; p1 = p2;
            }
            return list;
        }

        /// <summary>
        /// Lunar eclipses: at each full moon test the Moon's distance from the Earth's shadow axis (umbra/penumbra radii via Danjon rule).
        /// </summary>
        public static List<AstroEvent> LunarEclipses(double jd1, double jd2)
        {
            var list = new List<AstroEvent>();
            foreach (var fm in MoonPhases(jd1, jd2))
            {
                if (fm.Kind != EventKind.FullMoon) continue;
                double jd = fm.Time.JdTT;
                Func<double, double> sep = t =>
                {
                    Vec3 e, v;
                    EphemerisManager.EarthHeliocentric(t, out e, out v);
                    var moon = LunarTheory.GeocentricEquatorialJ2000(t);
                    var anti = e.Normalized();
                    return Vec3.AngleBetween(anti, moon) * AstroMath.Rad2Deg;
                };
                double tmin = Roots.GoldenMin(sep, jd - 0.3, jd + 0.3, 1e-6);
                double s = sep(tmin);
                Vec3 ee, ev;
                EphemerisManager.EarthHeliocentric(tmin, out ee, out ev);
                double ml, mb, md;
                LunarTheory.EclipticOfDate(tmin, out ml, out mb, out md);
                double piM = Math.Asin(AstroMath.EarthRadius_km / md) * AstroMath.Rad2Deg;
                double piS = 8.794 / 3600.0 / ee.Length;
                double sS = 959.63 / 3600.0 / ee.Length;
                double sM = 0.2725076 * piM;
                double umbra = 1.02 * (piM + piS - sS);
                double penumbra = 1.02 * (piM + piS + sS);
                if (s < penumbra + sM)
                {
                    string type;
                    double umag = (umbra + sM - s) / (2 * sM);
                    double pmag = (penumbra + sM - s) / (2 * sM);
                    if (s + sM < umbra) type = "Total lunar eclipse";
                    else if (s - sM < umbra) type = "Partial lunar eclipse";
                    else type = "Penumbral lunar eclipse";
                    list.Add(new AstroEvent
                    {
                        Time = AstroTime.FromTT(tmin), Kind = EventKind.LunarEclipse, Value = umag > 0 ? umag : pmag,
                        Description = string.Format("{0} (umbral mag {1:0.000}, penumbral {2:0.000})", type, umag, pmag)
                    });
                }
            }
            return list;
        }

        /// <summary>Solar eclipses (geocentric test at new moon: Sun-Moon separation vs. sum of radii + parallax).</summary>
        public static List<AstroEvent> SolarEclipses(double jd1, double jd2)
        {
            var list = new List<AstroEvent>();
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
                {
                    string type = "Partial solar eclipse";
                    if (s < piM - Math.Abs(sM - sS)) type = sM > sS ? "Total solar eclipse" : "Annular solar eclipse";
                    list.Add(new AstroEvent { Time = AstroTime.FromTT(tmin), Kind = EventKind.SolarEclipse, Description = type + " (visible somewhere on Earth)", Value = s });
                }
            }
            return list;
        }
    }
}
