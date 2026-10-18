using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Sidralune.Core.Mathematics
{
    /// <summary>Angle utilities, sexagesimal parsing/formatting and common constants.</summary>
    public static class AstroMath
    {
        public const double Deg2Rad = Math.PI / 180.0;
        public const double Rad2Deg = 180.0 / Math.PI;
        public const double Hour2Rad = Math.PI / 12.0;
        public const double Rad2Hour = 12.0 / Math.PI;
        public const double ArcSec2Rad = Math.PI / (180.0 * 3600.0);
        public const double Rad2ArcSec = 180.0 * 3600.0 / Math.PI;
        public const double TwoPi = 2.0 * Math.PI;
        public const double HalfPi = 0.5 * Math.PI;

        public const double AU_km = 149597870.700;
        public const double C_kms = 299792.458;
        /// <summary>Light time for one AU in days.</summary>
        public const double LightTimeAU_days = AU_km / C_kms / 86400.0;
        public const double EarthRadius_km = 6378.137;
        public const double EarthFlattening = 1.0 / 298.257223563;
        public const double GaussK = 0.01720209895;
        /// <summary>Heliocentric gravitational constant in AU^3/day^2.</summary>
        public const double GM_Sun = GaussK * GaussK;

        public static double NormalizeRad(double a)
        {
            a %= TwoPi;
            if (a < 0) a += TwoPi;
            return a;
        }

        public static double NormalizeDeg(double a)
        {
            a %= 360.0;
            if (a < 0) a += 360.0;
            return a;
        }

        /// <summary>Normalize to (-180, 180].</summary>
        public static double NormalizeDeg180(double a)
        {
            a = NormalizeDeg(a);
            return a > 180.0 ? a - 360.0 : a;
        }

        public static double NormalizeRadPi(double a)
        {
            a = NormalizeRad(a);
            return a > Math.PI ? a - TwoPi : a;
        }

        public static double NormalizeHours(double h)
        {
            h %= 24.0;
            if (h < 0) h += 24.0;
            return h;
        }

        public static double SinD(double d) => Math.Sin(d * Deg2Rad);
        public static double CosD(double d) => Math.Cos(d * Deg2Rad);
        public static double TanD(double d) => Math.Tan(d * Deg2Rad);
        public static double AsinD(double x) => Math.Asin(Clamp(x, -1, 1)) * Rad2Deg;
        public static double AcosD(double x) => Math.Acos(Clamp(x, -1, 1)) * Rad2Deg;
        public static double Atan2D(double y, double x) => Math.Atan2(y, x) * Rad2Deg;

        public static double Clamp(double v, double lo, double hi) => v < lo ? lo : (v > hi ? hi : v);
        public static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);
        public static float Clamp(float v, float lo, float hi) => v < lo ? lo : (v > hi ? hi : v);

        public static double Lerp(double a, double b, double t) => a + (b - a) * t;

        /// <summary>Great-circle separation of two points (radians in/out), Vincenty formula.</summary>
        public static double Separation(double ra1, double dec1, double ra2, double dec2)
        {
            double dra = ra2 - ra1;
            double sd1 = Math.Sin(dec1), cd1 = Math.Cos(dec1);
            double sd2 = Math.Sin(dec2), cd2 = Math.Cos(dec2);
            double cdra = Math.Cos(dra), sdra = Math.Sin(dra);
            double num1 = cd2 * sdra;
            double num2 = cd1 * sd2 - sd1 * cd2 * cdra;
            double den = sd1 * sd2 + cd1 * cd2 * cdra;
            return Math.Atan2(Math.Sqrt(num1 * num1 + num2 * num2), den);
        }

        /// <summary>Position angle (radians, N through E) of point 2 as seen from point 1.</summary>
        public static double PositionAngle(double ra1, double dec1, double ra2, double dec2)
        {
            double dra = ra2 - ra1;
            double y = Math.Sin(dra) * Math.Cos(dec2);
            double x = Math.Cos(dec1) * Math.Sin(dec2) - Math.Sin(dec1) * Math.Cos(dec2) * Math.Cos(dra);
            return NormalizeRad(Math.Atan2(y, x));
        }

        // ---------------------------------------------------------------- formatting

        /// <summary>Format hours as "HHh MMm SS.ss s".</summary>
        public static string FormatHms(double hours, int secDecimals = 1, bool compact = false)
        {
            hours = NormalizeHours(hours);
            int h, m; double s;
            Split(hours, secDecimals, out h, out m, out s);
            if (h == 24) h = 0;
            string sf = s.ToString("00." + new string('0', Math.Max(secDecimals, 0)), CultureInfo.InvariantCulture);
            if (secDecimals <= 0) sf = ((int)Math.Round(s)).ToString("00");
            return compact ? string.Format("{0:00}:{1:00}:{2}", h, m, sf) : string.Format("{0:00}h {1:00}m {2}s", h, m, sf);
        }

        /// <summary>Format degrees as "+DD° MM' SS.s\"".</summary>
        public static string FormatDms(double deg, int secDecimals = 0, bool forceSign = true, bool compact = false)
        {
            string sign = deg < 0 ? "-" : (forceSign ? "+" : "");
            int d, m; double s;
            Split(Math.Abs(deg), secDecimals, out d, out m, out s);
            string sf = secDecimals > 0 ? s.ToString("00." + new string('0', secDecimals), CultureInfo.InvariantCulture) : ((int)Math.Round(s)).ToString("00");
            return compact ? string.Format("{0}{1:00}:{2:00}:{3}", sign, d, m, sf) : string.Format("{0}{1:00}° {2:00}' {3}\"", sign, d, m, sf);
        }

        /// <summary>Format a longitude/azimuth-like degree value with 3-digit degrees.</summary>
        public static string FormatDms3(double deg, int secDecimals = 0)
        {
            int d, m; double s;
            Split(Math.Abs(deg), secDecimals, out d, out m, out s);
            string sf = secDecimals > 0 ? s.ToString("00." + new string('0', secDecimals), CultureInfo.InvariantCulture) : ((int)Math.Round(s)).ToString("00");
            return string.Format("{0}{1:000}° {2:00}' {3}\"", deg < 0 ? "-" : "", d, m, sf);
        }

        public static string FormatAngleAuto(double deg)
        {
            double a = Math.Abs(deg);
            if (a >= 1.0) return deg.ToString("0.00", CultureInfo.InvariantCulture) + "°";
            if (a * 60 >= 1.0) return (deg * 60).ToString("0.0", CultureInfo.InvariantCulture) + "'";
            return (deg * 3600).ToString("0.0", CultureInfo.InvariantCulture) + "\"";
        }

        private static void Split(double v, int secDecimals, out int a, out int b, out double c)
        {
            double round = Math.Pow(10, Math.Max(0, secDecimals));
            double totalSec = Math.Round(v * 3600.0 * round) / round;
            a = (int)(totalSec / 3600.0);
            totalSec -= a * 3600.0;
            b = (int)(totalSec / 60.0);
            c = totalSec - b * 60.0;
            if (c < 0) c = 0;
        }

        // ---------------------------------------------------------------- parsing

        private static readonly Regex SexRegex = new Regex(
            @"^\s*([+\-−]?)\s*(\d+(?:[.,]\d+)?)\s*(?:[hHdD°:\s]\s*(\d+(?:[.,]\d+)?)?\s*(?:[mM'′:\s]\s*(\d+(?:[.,]\d+)?)?\s*[sS""″]?)?)?\s*([NnSsEeWw])?\s*$",
            RegexOptions.Compiled);

        /// <summary>
        /// Parses sexagesimal or decimal values: "12 34 56.7", "12h34m56.7s", "-05°23'12\"", "12:34:56", "12.5824", "5.5S".
        /// Returns NaN if unparseable.
        /// </summary>
        public static double ParseSexagesimal(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return double.NaN;
            text = text.Trim().Replace("−", "-");
            var m = SexRegex.Match(text);
            if (!m.Success)
            {
                double dv;
                if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out dv)) return dv;
                return double.NaN;
            }
            double a = ParseNum(m.Groups[2].Value);
            double b = m.Groups[3].Success && m.Groups[3].Value.Length > 0 ? ParseNum(m.Groups[3].Value) : 0;
            double c = m.Groups[4].Success && m.Groups[4].Value.Length > 0 ? ParseNum(m.Groups[4].Value) : 0;
            double v = a + b / 60.0 + c / 3600.0;
            bool neg = m.Groups[1].Value == "-";
            string hemi = m.Groups[5].Success ? m.Groups[5].Value.ToUpperInvariant() : "";
            if (hemi == "S" || hemi == "W") neg = !neg;
            return neg ? -v : v;
        }

        private static double ParseNum(string s) => double.Parse(s.Replace(',', '.'), CultureInfo.InvariantCulture);

        /// <summary>Parses right ascension; accepts hours (sexagesimal) or explicit degrees with a trailing 'd' or '°'.</summary>
        public static double ParseRaHours(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return double.NaN;
            string t = text.Trim();
            if (t.EndsWith("d", StringComparison.OrdinalIgnoreCase) || t.EndsWith("°"))
            {
                double deg = ParseSexagesimal(t.TrimEnd('d', 'D', '°'));
                return deg / 15.0;
            }
            return ParseSexagesimal(t);
        }

        public static string ToInvariant(double v, string fmt = "G") => v.ToString(fmt, CultureInfo.InvariantCulture);

        public static double ParseInvariant(string s, double fallback = double.NaN)
        {
            double v;
            if (s != null && double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return v;
            return fallback;
        }

        public static string Pad(string s, int n) => s.Length >= n ? s.Substring(0, n) : s + new string(' ', n - s.Length);

        public static string Repeat(char c, int n)
        {
            var sb = new StringBuilder(n);
            sb.Append(c, n);
            return sb.ToString();
        }
    }
}
