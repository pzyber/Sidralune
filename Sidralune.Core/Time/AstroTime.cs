using System;
using System.Globalization;
using Sidralune.Core.Mathematics;

namespace Sidralune.Core.Time
{
    /// <summary>
    /// An instant expressed in UTC Julian Date with conversions to TT, UT1 and TDB.
    /// ΔT uses leap seconds for 1972..present and Espenak &amp; Meeus (2006) polynomials elsewhere.
    /// </summary>
    public struct AstroTime : IComparable<AstroTime>
    {
        public const double J2000 = 2451545.0;
        public const double JulianCentury = 36525.0;
        public const double UnixEpochJd = 2440587.5;

        /// <summary>Julian date on the UTC scale.</summary>
        public readonly double JdUtc;

        public AstroTime(double jdUtc) { JdUtc = jdUtc; }

        public static AstroTime FromDateTime(DateTime dt)
        {
            if (dt.Kind == DateTimeKind.Local) dt = dt.ToUniversalTime();
            return new AstroTime(UnixEpochJd + (dt - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalDays);
        }

        public static AstroTime Now => FromDateTime(DateTime.UtcNow);

        public static AstroTime FromTT(double jdTT)
        {
            // iterate since ΔT depends on UTC
            double jd = jdTT;
            for (int i = 0; i < 3; i++) jd = jdTT - TTminusUTC(jd) / 86400.0;
            return new AstroTime(jd);
        }

        public static AstroTime FromCalendar(int year, int month, double day, double hourUtc = 0)
            => new AstroTime(CalendarToJd(year, month, day + hourUtc / 24.0));

        public DateTime ToDateTimeUtc()
        {
            double ms = (JdUtc - UnixEpochJd) * 86400000.0;
            if (ms < -62135596800000.0) ms = -62135596800000.0;
            if (ms > 253402300799000.0) ms = 253402300799000.0;
            return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(ms);
        }

        public double JdTT => JdUtc + TTminusUTC(JdUtc) / 86400.0;
        public double JdUT1 => JdTT - DeltaT(JdUtc) / 86400.0;

        /// <summary>TDB, using the dominant periodic term (Fairhead &amp; Bretagnon), good to ~30 µs.</summary>
        public double JdTDB
        {
            get
            {
                double tt = JdTT;
                double g = (357.53 + 0.98560028 * (tt - J2000)) * AstroMath.Deg2Rad;
                return tt + (0.001657 * Math.Sin(g) + 0.000014 * Math.Sin(2 * g)) / 86400.0;
            }
        }

        public double Mjd => JdUtc - 2400000.5;

        /// <summary>Julian centuries of TT since J2000.</summary>
        public double T => (JdTT - J2000) / JulianCentury;

        public AstroTime AddDays(double d) => new AstroTime(JdUtc + d);
        public AstroTime AddHours(double h) => new AstroTime(JdUtc + h / 24.0);
        public AstroTime AddMinutes(double m) => new AstroTime(JdUtc + m / 1440.0);
        public AstroTime AddSeconds(double s) => new AstroTime(JdUtc + s / 86400.0);

        public int CompareTo(AstroTime other) => JdUtc.CompareTo(other.JdUtc);
        public static bool operator <(AstroTime a, AstroTime b) => a.JdUtc < b.JdUtc;
        public static bool operator >(AstroTime a, AstroTime b) => a.JdUtc > b.JdUtc;
        public static bool operator <=(AstroTime a, AstroTime b) => a.JdUtc <= b.JdUtc;
        public static bool operator >=(AstroTime a, AstroTime b) => a.JdUtc >= b.JdUtc;
        public static double operator -(AstroTime a, AstroTime b) => a.JdUtc - b.JdUtc;

        public override string ToString() => ToDateTimeUtc().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC";

        public string ToIso() => ToDateTimeUtc().ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture);

        public double DecimalYear
        {
            get
            {
                var dt = ToDateTimeUtc();
                var y0 = new DateTime(dt.Year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                var y1 = y0.AddYears(1);
                return dt.Year + (dt - y0).TotalDays / (y1 - y0).TotalDays;
            }
        }

        public double BesselianEpoch => 1900.0 + (JdTT - 2415020.31352) / 365.242198781;
        public double JulianEpoch => 2000.0 + (JdTT - J2000) / 365.25;

        // ------------------------------------------------------------------ calendar

        /// <summary>Meeus ch.7: calendar date (Gregorian after 1582-10-15) to JD.</summary>
        public static double CalendarToJd(int year, int month, double day)
        {
            if (month <= 2) { year -= 1; month += 12; }
            int B = 0;
            bool gregorian = year > 1582 || (year == 1582 && (month > 10 || (month == 10 && day >= 15)));
            if (gregorian) { int A = year / 100; B = 2 - A + A / 4; }
            return Math.Floor(365.25 * (year + 4716)) + Math.Floor(30.6001 * (month + 1)) + day + B - 1524.5;
        }

        public static void JdToCalendar(double jd, out int year, out int month, out double day)
        {
            double z = Math.Floor(jd + 0.5), f = jd + 0.5 - z, a;
            if (z < 2299161) a = z;
            else { double alpha = Math.Floor((z - 1867216.25) / 36524.25); a = z + 1 + alpha - Math.Floor(alpha / 4); }
            double b = a + 1524, c = Math.Floor((b - 122.1) / 365.25), d = Math.Floor(365.25 * c), e = Math.Floor((b - d) / 30.6001);
            day = b - d - Math.Floor(30.6001 * e) + f;
            month = (int)(e < 14 ? e - 1 : e - 13);
            year = (int)(month > 2 ? c - 4716 : c - 4715);
        }

        // ------------------------------------------------------------------ ΔT, leap seconds

        // TAI-UTC since 1972 (JD UTC of start, seconds)
        private static readonly double[,] LeapSeconds =
        {
            { 2441317.5, 10 }, { 2441499.5, 11 }, { 2441683.5, 12 }, { 2442048.5, 13 }, { 2442413.5, 14 },
            { 2442778.5, 15 }, { 2443144.5, 16 }, { 2443509.5, 17 }, { 2443874.5, 18 }, { 2444239.5, 19 },
            { 2444786.5, 20 }, { 2445151.5, 21 }, { 2445516.5, 22 }, { 2446247.5, 23 }, { 2447161.5, 24 },
            { 2447892.5, 25 }, { 2448257.5, 26 }, { 2448804.5, 27 }, { 2449169.5, 28 }, { 2449534.5, 29 },
            { 2450083.5, 30 }, { 2450630.5, 31 }, { 2451179.5, 32 }, { 2453736.5, 33 }, { 2454832.5, 34 },
            { 2456109.5, 35 }, { 2457204.5, 36 }, { 2457754.5, 37 },
        };

        /// <summary>Optional user-supplied DUT1 = UT1-UTC in seconds (from IERS Bulletin A).</summary>
        public static double Dut1Override = double.NaN;

        public static double TaiMinusUtc(double jdUtc)
        {
            if (jdUtc < LeapSeconds[0, 0]) return double.NaN;
            double v = 10;
            for (int i = 0; i < LeapSeconds.GetLength(0); i++)
                if (jdUtc >= LeapSeconds[i, 0]) v = LeapSeconds[i, 1];
            return v;
        }

        /// <summary>TT - UTC in seconds. Before 1972 UTC≈UT so this equals ΔT.</summary>
        public static double TTminusUTC(double jdUtc)
        {
            double tai = TaiMinusUtc(jdUtc);
            if (double.IsNaN(tai)) return DeltaT(jdUtc);
            return 32.184 + tai;
        }

        /// <summary>ΔT = TT - UT1 in seconds.</summary>
        public static double DeltaT(double jdUtc)
        {
            double tai = TaiMinusUtc(jdUtc);
            int y; int m; double d;
            JdToCalendar(jdUtc, out y, out m, out d);
            double year = y + (m - 0.5) / 12.0;
            if (!double.IsNaN(tai))
            {
                double dut1 = !double.IsNaN(Dut1Override) ? Dut1Override : ApproxDut1(year);
                return 32.184 + tai - dut1;
            }
            return EspenakMeeus(year);
        }

        /// <summary>Rough UT1-UTC model for when no IERS data is supplied (|err| &lt; ~0.5 s for 1972-2026).</summary>
        private static double ApproxDut1(double year)
        {
            if (year > 2026.0)
            {
                // Earth's rotation has been slightly fast since 2020; assume slow drift.
                double dt = 69.10 + 0.05 * (year - 2026.0);
                return 32.184 + 37 - dt;
            }
            double em = EspenakMeeus(year);
            double tai = 37;
            for (int i = 0; i < LeapSeconds.GetLength(0); i++)
            {
                int yy, mm; double dd;
                JdToCalendar(LeapSeconds[i, 0], out yy, out mm, out dd);
                if (yy + (mm - 1) / 12.0 <= year) tai = LeapSeconds[i, 1];
            }
            double v = 32.184 + tai - em;
            return AstroMath.Clamp(v, -0.9, 0.9);
        }

        /// <summary>Espenak &amp; Meeus (2006) polynomial ΔT, with 2005-2050 branch refit to recent observations.</summary>
        public static double EspenakMeeus(double y)
        {
            double t, u;
            if (y < -500) { u = (y - 1820) / 100; return -20 + 32 * u * u; }
            if (y < 500)
            {
                u = y / 100;
                return 10583.6 - 1014.41 * u + 33.78311 * u * u - 5.952053 * u * u * u - 0.1798452 * Math.Pow(u, 4) + 0.022174192 * Math.Pow(u, 5) + 0.0090316521 * Math.Pow(u, 6);
            }
            if (y < 1600)
            {
                u = (y - 1000) / 100;
                return 1574.2 - 556.01 * u + 71.23472 * u * u + 0.319781 * u * u * u - 0.8503463 * Math.Pow(u, 4) - 0.005050998 * Math.Pow(u, 5) + 0.0083572073 * Math.Pow(u, 6);
            }
            if (y < 1700) { t = y - 1600; return 120 - 0.9808 * t - 0.01532 * t * t + t * t * t / 7129; }
            if (y < 1800) { t = y - 1700; return 8.83 + 0.1603 * t - 0.0059285 * t * t + 0.00013336 * t * t * t - Math.Pow(t, 4) / 1174000; }
            if (y < 1860)
            {
                t = y - 1800;
                return 13.72 - 0.332447 * t + 0.0068612 * t * t + 0.0041116 * t * t * t - 0.00037436 * Math.Pow(t, 4) + 0.0000121272 * Math.Pow(t, 5) - 0.0000001699 * Math.Pow(t, 6) + 0.000000000875 * Math.Pow(t, 7);
            }
            if (y < 1900) { t = y - 1860; return 7.62 + 0.5737 * t - 0.251754 * t * t + 0.01680668 * t * t * t - 0.0004473624 * Math.Pow(t, 4) + Math.Pow(t, 5) / 233174; }
            if (y < 1920) { t = y - 1900; return -2.79 + 1.494119 * t - 0.0598939 * t * t + 0.0061966 * t * t * t - 0.000197 * Math.Pow(t, 4); }
            if (y < 1941) { t = y - 1920; return 21.20 + 0.84493 * t - 0.076100 * t * t + 0.0020936 * t * t * t; }
            if (y < 1961) { t = y - 1950; return 29.07 + 0.407 * t - t * t / 233 + t * t * t / 2547; }
            if (y < 1986) { t = y - 1975; return 45.45 + 1.067 * t - t * t / 260 - t * t * t / 718; }
            if (y < 2005) { t = y - 2000; return 63.86 + 0.3345 * t - 0.060374 * t * t + 0.0017275 * t * t * t + 0.000651814 * Math.Pow(t, 4) + 0.00002373599 * Math.Pow(t, 5); }
            if (y < 2015) { t = y - 2005; return 64.69 + 0.293 * t; }
            // 2015-2026 from IERS observed values; afterwards blend towards the long-term parabola.
            if (y < 2020) { t = y - 2015; return 67.62 + 0.348 * t; }
            if (y < 2026) { t = y - 2020; return 69.36 - 0.04 * t; }
            if (y < 2050) { t = y - 2026; return 69.12 + (93.0 - 69.12) * t / 24.0; }
            if (y < 2150) { u = (y - 1820) / 100; return -20 + 32 * u * u - 0.5628 * (2150 - y); }
            u = (y - 1820) / 100;
            return -20 + 32 * u * u;
        }

        // ------------------------------------------------------------------ sidereal time

        /// <summary>Earth Rotation Angle (IAU 2000) in radians.</summary>
        public double EarthRotationAngle
        {
            get
            {
                double du = JdUT1 - J2000;
                double f = du % 1.0;
                return AstroMath.NormalizeRad(AstroMath.TwoPi * (f + 0.7790572732640 + 0.00273781191135448 * du));
            }
        }

        /// <summary>Greenwich mean sidereal time (IAU 2006), radians.</summary>
        public double Gmst
        {
            get
            {
                double t = T;
                double poly = 0.014506 + t * (4612.156534 + t * (1.3915817 + t * (-0.00000044 + t * (-0.000029956 + t * -0.0000000368))));
                return AstroMath.NormalizeRad(EarthRotationAngle + poly * AstroMath.ArcSec2Rad);
            }
        }

        /// <summary>Greenwich apparent sidereal time, radians.</summary>
        public double Gast
        {
            get
            {
                double dpsi, deps;
                Coordinates.Nutation.Compute(T, out dpsi, out deps);
                double eps = Coordinates.Precession.MeanObliquity(T);
                double om = (125.04452 - 1934.136261 * T) * AstroMath.Deg2Rad;
                double ee = dpsi * Math.Cos(eps) + (0.00264 * Math.Sin(om) + 0.000063 * Math.Sin(2 * om)) * AstroMath.ArcSec2Rad;
                return AstroMath.NormalizeRad(Gmst + ee);
            }
        }

        /// <summary>Local apparent sidereal time in hours for east-positive longitude in degrees.</summary>
        public double LocalSiderealHours(double longitudeDeg) =>
            AstroMath.NormalizeHours((Gast + longitudeDeg * AstroMath.Deg2Rad) * AstroMath.Rad2Hour);
    }
}
