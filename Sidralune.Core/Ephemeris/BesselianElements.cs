using System;
using Sidralune.Core.Coordinates;
using Sidralune.Core.Mathematics;
using Sidralune.Core.Time;

namespace Sidralune.Core.Ephemeris
{
    /// <summary>
    /// Besselian elements of a solar eclipse: the Moon's shadow described on the fundamental plane (through the Earth's
    /// centre, perpendicular to the shadow axis), as cubic polynomials in hours from T0. Fitted to the ephemeris's
    /// geocentric Sun and Moon (light time, no aberration, as the shadow falls), true equator and equinox of date.
    /// Lengths are in Earth equatorial radii, angles in degrees (d, μ); μ is the Greenwich hour angle of the axis.
    /// </summary>
    public sealed class BesselianElements
    {
        /// <summary>The Moon's radius in Earth radii for the penumbra and for the umbra (NASA's values).</summary>
        public const double K1 = 0.2725076, K2 = 0.272281;
        /// <summary>WGS84 first eccentricity squared and polar/equatorial axis ratio.</summary>
        public const double E2 = 0.00669437999014, AxisRatio = 0.996647189335;
        /// <summary>Hours either side of T0 that the polynomials cover.</summary>
        public const double Span = 3.5;

        /// <summary>Reference time (TT Julian date, a whole TT hour near greatest eclipse).</summary>
        public double T0;
        /// <summary>ΔT at T0, seconds.</summary>
        public double DeltaT;
        public double[] X, Y, D, Mu, L1, L2;
        public double TanF1, TanF2;

        /// <summary>Fits the elements over ±3.5 hours around greatest eclipse (any time near it will do).</summary>
        public static BesselianElements Compute(AstroTime greatest)
        {
            double t0 = Math.Floor(greatest.JdTT * 24 + 0.5) / 24;
            const int n = 43; // every 10 minutes
            var ts = new double[n];
            double[] x = new double[n], y = new double[n], d = new double[n], mu = new double[n], l1 = new double[n], l2 = new double[n];
            double f1 = 0, f2 = 0;
            for (int i = 0; i < n; i++)
            {
                ts[i] = -Span + i * (2 * Span / (n - 1));
                double tf1, tf2;
                Sample(t0 + ts[i] / 24, out x[i], out y[i], out d[i], out mu[i], out l1[i], out l2[i], out tf1, out tf2);
                f1 += tf1 / n; f2 += tf2 / n;
                if (i > 0) while (mu[i] < mu[i - 1] - 180) mu[i] += 360; // unwrapped: μ grows by about 15° an hour
            }
            return new BesselianElements
            {
                T0 = t0, DeltaT = AstroTime.DeltaT(AstroTime.FromTT(t0).JdUtc),
                X = Fit(ts, x), Y = Fit(ts, y), D = Fit(ts, d), Mu = Fit(ts, mu), L1 = Fit(ts, l1), L2 = Fit(ts, l2), TanF1 = f1, TanF2 = f2
            };
        }

        /// <summary>The shadow's geometry at a TT Julian date.</summary>
        private static void Sample(double jdTT, out double x, out double y, out double dDeg, out double muDeg, out double l1, out double l2, out double tanF1, out double tanF2)
        {
            var time = AstroTime.FromTT(jdTT);
            double jdTDB = time.JdTDB;
            Vec3 ep, ev;
            EphemerisManager.State(Body.Earth, jdTDB, out ep, out ev);
            Func<Body, Vec3> geo = b =>
            {
                double tau = 0; Vec3 r = Vec3.Zero;
                for (int i = 0; i < 4; i++) { r = EphemerisManager.Position(b, jdTDB - tau) - ep; tau = r.Length * AstroMath.LightTimeAU_days; }
                return r;
            };
            var npb = Precession.NpbMatrix(time.T);
            double toRadii = AstroMath.AU_km / AstroMath.EarthRadius_km;
            Vec3 sun = npb * geo(Body.Sun) * toRadii, moon = npb * geo(Body.Moon) * toRadii;
            var g = sun - moon;
            double gl = g.Length;
            var gu = g / gl;
            double a = Math.Atan2(gu.Y, gu.X), d = Math.Asin(gu.Z);
            double sa = Math.Sin(a), ca = Math.Cos(a), sd = Math.Sin(d), cd = Math.Cos(d);
            x = -moon.X * sa + moon.Y * ca;
            y = -moon.X * sd * ca - moon.Y * sd * sa + moon.Z * cd;
            double z = moon.X * cd * ca + moon.Y * cd * sa + moon.Z * sd;
            double sunRadius = AstroMath.AU_km * Math.Tan(959.63 / 3600.0 * AstroMath.Deg2Rad) / AstroMath.EarthRadius_km;
            double f1 = Math.Asin((sunRadius + K1) / gl), f2 = Math.Asin((sunRadius - K2) / gl);
            tanF1 = Math.Tan(f1); tanF2 = Math.Tan(f2);
            l1 = z * tanF1 + K1 / Math.Cos(f1);
            l2 = z * tanF2 - K2 / Math.Cos(f2);
            dDeg = d * AstroMath.Rad2Deg;
            muDeg = AstroMath.NormalizeDeg((time.Gast - a) * AstroMath.Rad2Deg);
        }

        /// <summary>Least-squares cubic through (t, v).</summary>
        private static double[] Fit(double[] t, double[] v)
        {
            const int m = 4;
            var a = new double[m, m + 1];
            for (int i = 0; i < t.Length; i++)
            {
                var p = new double[m];
                p[0] = 1; for (int k = 1; k < m; k++) p[k] = p[k - 1] * t[i];
                for (int r = 0; r < m; r++) { for (int c = 0; c < m; c++) a[r, c] += p[r] * p[c]; a[r, m] += p[r] * v[i]; }
            }
            for (int c = 0; c < m; c++) // Gauss-Jordan with partial pivoting
            {
                int piv = c;
                for (int r = c + 1; r < m; r++) if (Math.Abs(a[r, c]) > Math.Abs(a[piv, c])) piv = r;
                for (int k = 0; k <= m; k++) { double tmp = a[c, k]; a[c, k] = a[piv, k]; a[piv, k] = tmp; }
                for (int r = 0; r < m; r++)
                {
                    if (r == c) continue;
                    double f = a[r, c] / a[c, c];
                    for (int k = c; k <= m; k++) a[r, k] -= f * a[c, k];
                }
            }
            var res = new double[m];
            for (int r = 0; r < m; r++) res[r] = a[r, m] / a[r, r];
            return res;
        }

        private static double Poly(double[] c, double t) => c[0] + t * (c[1] + t * (c[2] + t * c[3]));
        private static double Deriv(double[] c, double t) => c[1] + t * (2 * c[2] + t * 3 * c[3]);

        /// <summary>The TT Julian date of t hours from T0.</summary>
        public double JdTT(double t) => T0 + t / 24;
        /// <summary>Hours from T0 of a TT Julian date.</summary>
        public double Hours(double jdTT) => (jdTT - T0) * 24;

        /// <summary>The elements and their hourly rates (d′ and μ′ in radians an hour) at t hours from T0.</summary>
        public void At(double t, out double x, out double y, out double d, out double mu, out double l1, out double l2, out double dx, out double dy, out double dd, out double dmu)
        {
            x = Poly(X, t); y = Poly(Y, t); d = Poly(D, t) * AstroMath.Deg2Rad; mu = Poly(Mu, t) * AstroMath.Deg2Rad;
            l1 = Poly(L1, t); l2 = Poly(L2, t);
            dx = Deriv(X, t); dy = Deriv(Y, t); dd = Deriv(D, t) * AstroMath.Deg2Rad; dmu = Deriv(Mu, t) * AstroMath.Deg2Rad;
        }

        /// <summary>
        /// Where the shadow axis meets the Earth at t hours from T0 (geodetic latitude and east longitude, degrees);
        /// false when it misses the Earth.
        /// </summary>
        public bool AxisOnEarth(double t, out double latDeg, out double lonDeg)
        {
            double x, y, d, mu, l1, l2, dx, dy, dd, dmu;
            At(t, out x, out y, out d, out mu, out l1, out l2, out dx, out dy, out dd, out dmu);
            return PlaneToEarth(t, x, y, out latDeg, out lonDeg);
        }

        /// <summary>
        /// The place on the Earth's sunward side whose position on the fundamental plane is (ξ, η) at t hours from T0; false
        /// beyond the Earth's limb. A point a hair outside the limb (rounding) is put on it.
        /// </summary>
        public bool PlaneToEarth(double t, double xi, double eta, out double latDeg, out double lonDeg)
        {
            double x, y, d, mu, l1, l2, dx, dy, dd, dmu;
            At(t, out x, out y, out d, out mu, out l1, out l2, out dx, out dy, out dd, out dmu);
            double w = 1 / Math.Sqrt(1 - E2 * Math.Cos(d) * Math.Cos(d));
            double y1 = w * eta, b1 = w * Math.Sin(d), b2 = AxisRatio * w * Math.Cos(d);
            double bb = 1 - xi * xi - y1 * y1;
            latDeg = lonDeg = double.NaN;
            if (bb < -1e-9) return false;
            double b = Math.Sqrt(Math.Max(0, bb));
            double phi1 = Math.Asin(Math.Max(-1, Math.Min(1, b * b1 + y1 * b2)));
            double h = Math.Atan2(xi, b * b2 - y1 * b1);
            latDeg = Math.Atan(Math.Tan(phi1) / AxisRatio) * AstroMath.Rad2Deg;
            lonDeg = AstroMath.NormalizeDeg180((h - mu) * AstroMath.Rad2Deg);
            return true;
        }

        /// <summary>The limb's scale in η at t hours (the Earth's outline on the fundamental plane is ξ² + (ω η)² = 1).</summary>
        internal double LimbOmega(double t)
        {
            double d = Poly(D, t) * AstroMath.Deg2Rad;
            return 1 / Math.Sqrt(1 - E2 * Math.Cos(d) * Math.Cos(d));
        }

        /// <summary>
        /// A place at sea level against the shadow at t hours: its offset from the axis (u, v) and their rates (a, b), the
        /// penumbra's and umbra's radii there, and ζ (above zero when the Sun is up, geometrically).
        /// </summary>
        internal void Relative(double t, double latDeg, double lonDeg, out double u, out double v, out double a, out double b, out double L1, out double L2, out double zeta)
        {
            double rs, rc, h, d;
            Geocentric(latDeg, 0, out rs, out rc);
            Against(t, rs, rc, lonDeg, out u, out v, out a, out b, out L1, out L2, out zeta, out h, out d);
        }

        /// <summary>An observer's place on the Earth: ρ sin φ′ and ρ cos φ′ from geodetic latitude and elevation.</summary>
        public static void Geocentric(double latDeg, double elevationM, out double rhoSin, out double rhoCos)
        {
            double phi = latDeg * AstroMath.Deg2Rad;
            double u = Math.Atan(AxisRatio * Math.Tan(phi));
            double h = elevationM / (AstroMath.EarthRadius_km * 1000);
            rhoSin = AxisRatio * Math.Sin(u) + h * Math.Sin(phi);
            rhoCos = Math.Cos(u) + h * Math.Cos(phi);
        }

        /// <summary>The observer against the shadow at t hours: the quantities of the local-circumstances method.</summary>
        private void Against(double t, double rhoSin, double rhoCos, double lonDeg,
            out double u, out double v, out double a, out double b, out double L1, out double L2, out double zeta, out double h, out double d)
        {
            double x, y, mu, l1, l2, dx, dy, dd, dmu;
            At(t, out x, out y, out d, out mu, out l1, out l2, out dx, out dy, out dd, out dmu);
            h = mu + lonDeg * AstroMath.Deg2Rad;
            double sh = Math.Sin(h), ch = Math.Cos(h), sd = Math.Sin(d), cd = Math.Cos(d);
            double xi = rhoCos * sh;
            double eta = rhoSin * cd - rhoCos * ch * sd;
            zeta = rhoSin * sd + rhoCos * ch * cd;
            double dxi = dmu * rhoCos * ch;
            double deta = dmu * xi * sd - zeta * dd;
            u = x - xi; v = y - eta; a = dx - dxi; b = dy - deta;
            L1 = l1 - zeta * TanF1; L2 = l2 - zeta * TanF2;
        }

        /// <summary>
        /// The eclipse at a place on the Earth: contacts, maximum, magnitude, the share of the Sun covered, the local type and
        /// the Sun's place at each contact, whether the Sun is up or not (Covers false when the Moon misses the Sun there).
        /// </summary>
        public SolarLocalCircumstances Local(double latDeg, double lonDeg, double elevationM = 0)
        {
            double rs, rc;
            Geocentric(latDeg, elevationM, out rs, out rc);
            var res = new SolarLocalCircumstances { Latitude = latDeg, Longitude = lonDeg };
            double u, v, a, b, L1, L2, zeta, h, d;
            // the maximum: closest approach to the shadow axis, from the closest of a coarse scan (starting at T0 can settle
            // on the wrong minimum for places that see the eclipse near its beginning or end)
            double t = 0, best = double.MaxValue;
            for (double s = -Span; s <= Span + 1e-9; s += 1 / 3.0)
            {
                Against(s, rs, rc, lonDeg, out u, out v, out a, out b, out L1, out L2, out zeta, out h, out d);
                double mm = u * u + v * v;
                if (mm < best) { best = mm; t = s; }
            }
            for (int i = 0; i < 20; i++)
            {
                Against(t, rs, rc, lonDeg, out u, out v, out a, out b, out L1, out L2, out zeta, out h, out d);
                double tau = -(u * a + v * b) / (a * a + b * b);
                t += tau;
                if (Math.Abs(tau) < 1e-7) break;
            }
            if (t < -Span || t > Span) t = Math.Max(-Span, Math.Min(Span, t));
            Against(t, rs, rc, lonDeg, out u, out v, out a, out b, out L1, out L2, out zeta, out h, out d);
            double m = Math.Sqrt(u * u + v * v);
            res.Maximum = SunAt(t, latDeg, h, d, zeta);
            res.AxisDistance = m;
            res.PenumbraRadius = L1; res.UmbraRadius = L2;
            res.Covers = m < L1;
            res.Magnitude = (L1 - m) / (L1 + L2);
            res.MoonSunRatio = (L1 - L2) / (L1 + L2);
            res.Obscuration = res.Covers ? Events.DiscOverlap(1, res.MoonSunRatio, 1 + res.MoonSunRatio - 2 * res.Magnitude) : 0;
            res.Kind = m < Math.Abs(L2) ? (L2 < 0 ? 1 : 2) : 0;
            if (!res.Covers) return res;
            res.C1 = Contact(t, rs, rc, latDeg, lonDeg, true, -1);
            res.C4 = Contact(t, rs, rc, latDeg, lonDeg, true, +1);
            if (res.Kind != 0)
            {
                res.C2 = Contact(t, rs, rc, latDeg, lonDeg, false, -1);
                res.C3 = Contact(t, rs, rc, latDeg, lonDeg, false, +1);
            }
            return res;
        }

        /// <summary>A contact: penumbral (C1 or C4) or umbral (C2 or C3), dir −1 for the beginning and +1 for the end.</summary>
        private SolarContact Contact(double tMax, double rs, double rc, double latDeg, double lonDeg, bool penumbral, int dir)
        {
            double t = tMax;
            double u, v, a, b, L1, L2, zeta, h, d;
            for (int i = 0; i < 20; i++)
            {
                Against(t, rs, rc, lonDeg, out u, out v, out a, out b, out L1, out L2, out zeta, out h, out d);
                double L = penumbral ? L1 : Math.Abs(L2);
                double n = Math.Sqrt(a * a + b * b);
                double s = (a * v - u * b) / (n * L);
                if (Math.Abs(s) > 1) return null;
                double tau = -(u * a + v * b) / (n * n) + dir * L / n * Math.Sqrt(1 - s * s);
                t += tau;
                if (Math.Abs(tau) < 1e-7) break;
            }
            Against(t, rs, rc, lonDeg, out u, out v, out a, out b, out L1, out L2, out zeta, out h, out d);
            return SunAt(t, latDeg, h, d, zeta);
        }

        private SolarContact SunAt(double t, double latDeg, double h, double d, double zeta)
        {
            var hz = Transforms.HaDecToAltAz(h, d, latDeg * AstroMath.Deg2Rad);
            return new SolarContact { Time = AstroTime.FromTT(JdTT(t)), Altitude = hz.AltDeg, Azimuth = hz.AzDeg };
        }
    }

    /// <summary>A moment of a solar eclipse at a place, with the Sun's geometric altitude and azimuth (degrees) then.</summary>
    public class SolarContact
    {
        public AstroTime Time;
        public double Altitude, Azimuth;
        /// <summary>The Sun's upper limb is above the horizon (refraction included).</summary>
        public bool SunUp => Altitude > Events.SunStandardAltitude;
    }

    /// <summary>A solar eclipse at one place on the Earth, from the Besselian elements.</summary>
    public class SolarLocalCircumstances
    {
        public double Latitude, Longitude;
        /// <summary>The Moon covers part of the Sun there at some moment, whether the Sun is up or not.</summary>
        public bool Covers;
        /// <summary>First contact, the beginning and end of the total or annular phase, and last contact (null when there is none).</summary>
        public SolarContact C1, C2, C3, C4;
        public SolarContact Maximum;
        /// <summary>At maximum: the fraction of the Sun's diameter covered, of its disc, and the ratio of the Moon's and Sun's apparent sizes.</summary>
        public double Magnitude, Obscuration, MoonSunRatio;
        /// <summary>0 partial, 1 total, 2 annular.</summary>
        public int Kind;
        /// <summary>At maximum, on the fundamental plane (Earth radii): the distance from the shadow axis and the penumbra's and umbra's radii there (the umbra's negative when total).</summary>
        public double AxisDistance, PenumbraRadius, UmbraRadius;
        /// <summary>The total or annular phase in seconds (0 when there is none).</summary>
        public double CentralSeconds => C2 != null && C3 != null ? (C3.Time.JdTT - C2.Time.JdTT) * 86400 : 0;
        /// <summary>How far inside the umbra's edge the place is at maximum (positive in the path), Earth radii on the fundamental plane.</summary>
        public double Centrality => Math.Abs(UmbraRadius) - AxisDistance;
        /// <summary>Some of the eclipse happens with the Sun up.</summary>
        public bool Visible => Covers && (C1 != null && C1.SunUp || C4 != null && C4.SunUp || Maximum.SunUp);
    }
}
