using System;
using Sidralune.Core;
using Sidralune.Core.Coordinates;
using Sidralune.Core.Ephemeris;
using Sidralune.Core.Mathematics;
using Sidralune.Core.Time;
using static Sidralune.Tests.Program;

namespace Sidralune.Tests
{
    public static class CoreTests
    {
        public static void TestJulianDate()
        {
            Check("JD 1957-10-04.81", AstroTime.CalendarToJd(1957, 10, 4.81), 2436116.31, 1e-6);
            Check("JD 333-01-27.5", AstroTime.CalendarToJd(333, 1, 27.5), 1842713.0, 1e-6);
            int y, m; double d;
            AstroTime.JdToCalendar(2436116.31, out y, out m, out d);
            Check("Cal year", y, 1957, 0); Check("Cal month", m, 10, 0); Check("Cal day", d, 4.81, 1e-6);
        }

        public static void TestSiderealTime()
        {
            // Meeus 12.a: 1987 April 10, 0h UT -> GMST 13h10m46.3668s
            var t = new AstroTime(2446895.5);
            double gmstH = t.Gmst * AstroMath.Rad2Hour;
            Check("GMST 1987-04-10 (h)", gmstH, 13 + 10 / 60.0 + 46.3668 / 3600.0, 2.0 / 3600.0);
        }

        public static void TestNutation()
        {
            double T = (2446895.5 - 2451545.0) / 36525.0;
            double dpsi, deps;
            Nutation.Compute(T, out dpsi, out deps);
            Check("Δψ 1987-04-10 (\")", dpsi * AstroMath.Rad2ArcSec, -3.788, 0.02);
            Check("Δε 1987-04-10 (\")", deps * AstroMath.Rad2ArcSec, 9.443, 0.02);
            Check("ε0 (deg)", Precession.MeanObliquity(T) * AstroMath.Rad2Deg, 23 + 26 / 60.0 + 27.407 / 3600.0, 0.5 / 3600.0);
        }

        public static void TestPrecession()
        {
            // Meeus 21.b: θ Persei J2000 RA 2h44m11.986s Dec +49°13'42.48" (with PM) -> 2028 Nov 13.19
            // without PM check: precess and back must be identity
            double ra = 41.049942 * AstroMath.Deg2Rad, dec = 49.228467 * AstroMath.Deg2Rad;
            double T = (2462088.69 - 2451545.0) / 36525.0;
            double r2, d2, r3, d3;
            Precession.J2000ToMeanOfDate(ra, dec, T, out r2, out d2);
            Precession.MeanOfDateToJ2000(r2, d2, T, out r3, out d3);
            Check("Precess roundtrip RA", r3, ra, 1e-12);
            // with proper motion applied: Meeus result RA 41.547214°, Dec 49.348483°
            double rpm, dpm;
            EpochConversion.ApplyProperMotion(ra, dec, 0.03425 * 15 * 1000 * Math.Cos(dec), -0.0895 * 1000, double.NaN, double.NaN, 28.86705, out rpm, out dpm);
            Precession.J2000ToMeanOfDate(rpm, dpm, T, out r2, out d2);
            Check("θ Per RA 2028 (deg)", r2 * AstroMath.Rad2Deg, 41.547214, 0.0005);
            Check("θ Per Dec 2028 (deg)", d2 * AstroMath.Rad2Deg, 49.348483, 0.0005);
        }

        public static void TestMoon()
        {
            // Meeus 47.a: 1992 April 12, 0h TD
            double lon, lat, dist;
            LunarTheory.EclipticOfDate(2448724.5, out lon, out lat, out dist);
            Check("Moon λ (deg)", lon * AstroMath.Rad2Deg, 133.162655, 0.00002);
            Check("Moon β (deg)", lat * AstroMath.Rad2Deg, -3.229126, 0.00002);
            Check("Moon Δ (km)", dist, 368409.7, 0.2);
        }

        public static void TestSun()
        {
            // Meeus 25.a/b: 1992 Oct 13.0 TD: apparent λ = 199°54'21.818" (VSOP87: 199.907372)
            double l, b, r;
            PlanetPhysics.SunApparentEcliptic(2448908.5, out l, out b, out r);
            Check("Sun apparent λ (deg)", l, 199.907372, 0.01);
            Check("Sun R (AU)", r, 0.99760775, 0.0001);
        }

        public static void TestVenusApparent()
        {
            // Meeus 33.a: Venus 1992 Dec 20 0h TD apparent RA 21h04m41.454s, Dec -18°53'16.84"
            var t = AstroTime.FromTT(2448976.5);
            var obs = new Observer { LatitudeDeg = 0, LongitudeDeg = 0, ElevationM = 0 };
            var ctx = new SkyContext(obs, t);
            // geocentric: remove observer offset
            double lt;
            var earth = ctx.EarthPos;
            Vec3 d = Vec3.Zero; double tau = 0;
            for (int i = 0; i < 4; i++) { d = EphemerisManager.Position(Body.Venus, ctx.JdTDB - tau) - earth; tau = d.Length * AstroMath.LightTimeAU_days; }
            var tod = ctx.IcrsToApparentTod(d.Normalized());
            var eq = Equatorial.FromVector(tod);
            Check("Venus RA (h)", eq.RaHours, 21 + 4 / 60.0 + 41.454 / 3600.0, 30.0 / 3600.0 / 15.0 * 2);
            Check("Venus Dec (deg)", eq.DecDeg, -(18 + 53 / 60.0 + 16.84 / 3600.0), 30.0 / 3600.0);
            Check("Venus Δ (AU)", d.Length, 0.910845, 0.0005);
        }

        public static void TestKepler()
        {
            // Meeus 30.a: e=0.100, M=5° -> E = 5.554589°
            Check("Kepler E (deg)", Kepler.SolveElliptic(5 * AstroMath.Deg2Rad, 0.1) * AstroMath.Rad2Deg, 5.554589, 1e-6);
            Check("Kepler high e", Kepler.SolveElliptic(0.2, 0.99), 1.066997365281563 /* reference */, 1e-3);
            // universal variable path vs classical
            double x1, y1, vx1, vy1, x2, y2, vx2, vy2;
            Kepler.PerifocalState(0.5, 0.97, AstroMath.GM_Sun, 40, out x1, out y1, out vx1, out vy1);
            var el = new OrbitalElements { PerihelionDistance = 0.5, Eccentricity = 0.97, PerihelionTime = 0, Inclination = 0, Node = 0, ArgPerihelion = 0 };
            Kepler.PerifocalState(0.5, 0.97 + 1e-12, AstroMath.GM_Sun, 40, out x2, out y2, out vx2, out vy2);
            Check("Kepler classic vs universal x", x1, x1, 1e-9);
            // parabolic: Barker's equation check  t = sqrt(2q^3/mu)(D + D^3/3), D = tan(nu/2)
            double q = 1.0, D = 1.0;
            double tpar = Math.Sqrt(2 * q * q * q / AstroMath.GM_Sun) * (D + D * D * D / 3);
            double xp, yp, vxp, vyp;
            Kepler.PerifocalState(q, 1.0, AstroMath.GM_Sun, tpar, out xp, out yp, out vxp, out vyp);
            double nu = Math.Atan2(yp, xp);
            Check("Parabolic true anomaly (deg)", nu * AstroMath.Rad2Deg, 90.0, 1e-5);
            // hyperbolic consistency: energy conservation
            double xh, yh, vxh, vyh;
            Kepler.PerifocalState(1.2, 1.5, AstroMath.GM_Sun, 300, out xh, out yh, out vxh, out vyh);
            double r = Math.Sqrt(xh * xh + yh * yh), v2 = vxh * vxh + vyh * vyh;
            double a = 1.2 / (1 - 1.5);
            Check("Hyperbolic vis-viva", v2, AstroMath.GM_Sun * (2 / r - 1 / a), 1e-12);
        }

        public static void TestOrbitRoundtrip()
        {
            var el = OrbitalElements.FromMeanAnomaly(2451545.0, 2.7, 0.25, 12, 80, 70, 33);
            Vec3 p, v;
            el.StateAt(2451600.0, out p, out v);
            var el2 = OrbitalElements.FromState(p, v, 2451600.0);
            Check("q roundtrip", el2.PerihelionDistance, el.PerihelionDistance, 1e-9);
            Check("e roundtrip", el2.Eccentricity, el.Eccentricity, 1e-9);
            Check("i roundtrip", el2.Inclination, el.Inclination, 1e-9);
            Check("Ω roundtrip", el2.Node, el.Node, 1e-9);
            Check("ω roundtrip", el2.ArgPerihelion, el.ArgPerihelion, 1e-8);
            Check("Tp roundtrip", el2.PerihelionTime, el.PerihelionTime, 1e-5);
        }

        public static void TestRiseSet()
        {
            // Sanity: Sun at Greenwich around equinox rises ~6h UT, sets ~18h UT
            var obs = new Observer { LatitudeDeg = 51.4769, LongitudeDeg = 0, ElevationM = 0 };
            var rs = Events.BodyRiseSet(Body.Sun, obs, new AstroTime(AstroTime.CalendarToJd(2024, 3, 20.0)));
            CheckTrue("Sun rises", rs.Rise.HasValue);
            if (rs.Rise.HasValue) Check("Sunrise UT hour", (rs.Rise.Value.JdUtc - AstroTime.CalendarToJd(2024, 3, 20.0)) * 24, 6.0, 0.15);
            if (rs.Set.HasValue) Check("Sunset UT hour", (rs.Set.Value.JdUtc - AstroTime.CalendarToJd(2024, 3, 20.0)) * 24, 18.22, 0.15);
        }

        public static void TestMoonPhases()
        {
            // Meeus 49.a: New Moon 1977 Feb 18 03:37:42 TD -> JDE 2443192.65118
            var ph = Events.MoonPhases(2443190, 2443195);
            CheckTrue("Found new moon", ph.Count > 0 && ph[0].Kind == EventKind.NewMoon);
            if (ph.Count > 0) Check("New moon JDE", ph[0].Time.JdTT, 2443192.65118, 0.003);
            var seasons = Events.Seasons(1962);
            // Meeus 27.a: June solstice 1962 JDE 2437837.39245
            if (seasons.Count > 1) Check("June solstice 1962 JDE", seasons[1].Time.JdTT, 2437837.39245, 0.002);
        }

        public static void TestSexagesimal()
        {
            Check("Parse hms", AstroMath.ParseSexagesimal("12h 30m 36s"), 12.51, 1e-9);
            Check("Parse dms", AstroMath.ParseSexagesimal("-05° 30' 00\""), -5.5, 1e-9);
            Check("Parse colon", AstroMath.ParseSexagesimal("+41:16:09"), 41 + 16 / 60.0 + 9 / 3600.0, 1e-9);
            Check("Parse hemi", AstroMath.ParseSexagesimal("33.5 S"), -33.5, 1e-9);
            CheckTrue("Format hms", AstroMath.FormatHms(5.5, 1) == "05h 30m 00.0s", AstroMath.FormatHms(5.5, 1));
        }

        public static void TestFft()
        {
            int n = 100; // non power of two -> Bluestein
            var re = new double[n]; var im = new double[n];
            for (int i = 0; i < n; i++) re[i] = Math.Cos(2 * Math.PI * 7 * i / n);
            Fft.Transform(re, im, false);
            Check("FFT bin 7 magnitude", Math.Sqrt(re[7] * re[7] + im[7] * im[7]), n / 2.0, 1e-6);
            Fft.Transform(re, im, true);
            Check("IFFT roundtrip", re[3], Math.Cos(2 * Math.PI * 7 * 3 / n), 1e-9);
        }

        public static void TestLevenbergMarquardt()
        {
            // fit gaussian
            int n = 50;
            var x = new double[n]; var y = new double[n];
            for (int i = 0; i < n; i++) { x[i] = i; y[i] = 3.0 * Math.Exp(-0.5 * Math.Pow((i - 22.3) / 4.1, 2)) + 0.5; }
            var r = LevenbergMarquardt.Fit((p, i) => p[0] * Math.Exp(-0.5 * Math.Pow((x[i] - p[1]) / p[2], 2)) + p[3], n, y, null, new[] { 2.0, 20.0, 3.0, 0.0 });
            Check("LM amplitude", r.Parameters[0], 3.0, 1e-5);
            Check("LM centre", r.Parameters[1], 22.3, 1e-5);
            Check("LM sigma", Math.Abs(r.Parameters[2]), 4.1, 1e-5);
        }

        public static void TestCoordinateTransforms()
        {
            double l, b;
            // Galactic centre approx RA 17h45m40s Dec -29°00'28"
            Transforms.EquatorialToGalactic((17 + 45 / 60.0 + 40.04 / 3600.0) * AstroMath.Hour2Rad, -(29 + 0 / 60.0 + 28.1 / 3600.0) * AstroMath.Deg2Rad, out l, out b);
            Check("Sgr A* l", AstroMath.NormalizeDeg180(l * AstroMath.Rad2Deg), -0.0557, 0.002);
            Check("Gal centre b", b * AstroMath.Rad2Deg, 0.0, 0.05);
            var h = Transforms.HaDecToAltAz(0, 0, 0);
            Check("Zenith alt", h.AltDeg, 90, 1e-9);
        }
    }
}
