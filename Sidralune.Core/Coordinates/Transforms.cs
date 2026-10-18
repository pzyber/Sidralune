using System;
using Sidralune.Core.Mathematics;

namespace Sidralune.Core.Coordinates
{
    /// <summary>Equatorial position (radians) with optional distance (AU).</summary>
    public struct Equatorial
    {
        public double Ra, Dec, Distance;
        public Equatorial(double ra, double dec, double dist = double.NaN) { Ra = ra; Dec = dec; Distance = dist; }
        public double RaHours => AstroMath.NormalizeRad(Ra) * AstroMath.Rad2Hour;
        public double DecDeg => Dec * AstroMath.Rad2Deg;
        public Vec3 ToVector() => Vec3.FromSpherical(Ra, Dec, double.IsNaN(Distance) ? 1.0 : Distance);
        public static Equatorial FromVector(Vec3 v)
        {
            double lon, lat, r;
            v.ToSpherical(out lon, out lat, out r);
            return new Equatorial(lon, lat, r);
        }
        public static Equatorial FromHoursDegrees(double raH, double decD) => new Equatorial(raH * AstroMath.Hour2Rad, decD * AstroMath.Deg2Rad);
        public override string ToString() => AstroMath.FormatHms(RaHours) + "  " + AstroMath.FormatDms(DecDeg);
    }

    /// <summary>Horizontal coordinates: azimuth measured from North through East, radians.</summary>
    public struct Horizontal
    {
        public double Az, Alt;
        public Horizontal(double az, double alt) { Az = az; Alt = alt; }
        public double AzDeg => AstroMath.NormalizeRad(Az) * AstroMath.Rad2Deg;
        public double AltDeg => Alt * AstroMath.Rad2Deg;
        public override string ToString() => string.Format("Az {0:0.00}°  Alt {1:+0.00;-0.00}°", AzDeg, AltDeg);
    }

    public static class Transforms
    {
        /// <summary>Hour angle/declination to horizontal. All radians.</summary>
        public static Horizontal HaDecToAltAz(double ha, double dec, double lat)
        {
            double sh = Math.Sin(ha), ch = Math.Cos(ha), sd = Math.Sin(dec), cd = Math.Cos(dec), sl = Math.Sin(lat), cl = Math.Cos(lat);
            double x = -ch * cd * sl + sd * cl;
            double y = -sh * cd;
            double z = ch * cd * cl + sd * sl;
            double r = Math.Sqrt(x * x + y * y);
            double az = r != 0 ? Math.Atan2(y, x) : 0;
            return new Horizontal(AstroMath.NormalizeRad(az), Math.Atan2(z, r));
        }

        public static void AltAzToHaDec(double az, double alt, double lat, out double ha, out double dec)
        {
            double sa = Math.Sin(az), ca = Math.Cos(az), se = Math.Sin(alt), ce = Math.Cos(alt), sl = Math.Sin(lat), cl = Math.Cos(lat);
            double x = -ca * ce * sl + se * cl;
            double y = -sa * ce;
            double z = ca * ce * cl + se * sl;
            double r = Math.Sqrt(x * x + y * y);
            ha = r != 0 ? Math.Atan2(y, x) : 0;
            dec = Math.Atan2(z, r);
        }

        /// <summary>Parallactic angle q (radians) for hour angle and declination at latitude.</summary>
        public static double ParallacticAngle(double ha, double dec, double lat)
        {
            double sq = Math.Sin(ha) * Math.Cos(lat);
            double cq = Math.Sin(lat) * Math.Cos(dec) - Math.Cos(lat) * Math.Sin(dec) * Math.Cos(ha);
            return Math.Atan2(sq, cq);
        }

        /// <summary>Equatorial to ecliptic (lambda, beta) for obliquity eps (radians).</summary>
        public static void EquatorialToEcliptic(double ra, double dec, double eps, out double lon, out double lat)
        {
            var v = Mat3.RotX(eps) * Vec3.FromSpherical(ra, dec);
            v.ToSpherical(out lon, out lat);
        }

        public static void EclipticToEquatorial(double lon, double lat, double eps, out double ra, out double dec)
        {
            var v = Mat3.RotX(-eps) * Vec3.FromSpherical(lon, lat);
            v.ToSpherical(out ra, out dec);
        }

        public static Vec3 EclipticToEquatorial(Vec3 v, double eps) => Mat3.RotX(-eps) * v;
        public static Vec3 EquatorialToEcliptic(Vec3 v, double eps) => Mat3.RotX(eps) * v;

        // ICRS -> Galactic rotation (Hipparcos definition)
        private static readonly Mat3 IcrsToGal = new Mat3(
            -0.0548755604162154, -0.8734370902348850, -0.4838350155487132,
            +0.4941094278755837, -0.4448296299600112, +0.7469822444972189,
            -0.8676661490190047, -0.1980763734312015, +0.4559837761750669);

        public static void EquatorialToGalactic(double ra, double dec, out double l, out double b)
        {
            (IcrsToGal * Vec3.FromSpherical(ra, dec)).ToSpherical(out l, out b);
        }

        public static void GalacticToEquatorial(double l, double b, out double ra, out double dec)
        {
            (IcrsToGal.Transpose() * Vec3.FromSpherical(l, b)).ToSpherical(out ra, out dec);
        }

        // Galactic -> supergalactic (de Vaucouleurs)
        private static readonly Mat3 GalToSgal = new Mat3(
            -0.7357425748043749, 0.6772612964138943, 0.0,
            -0.0745537783652337, -0.0809914713069767, 0.9939225903997749,
            0.6731453021092076, 0.7312711658169645, 0.1100812622247821);

        public static void GalacticToSupergalactic(double l, double b, out double sgl, out double sgb)
        {
            (GalToSgal * Vec3.FromSpherical(l, b)).ToSpherical(out sgl, out sgb);
        }

        /// <summary>
        /// Atmospheric refraction (radians) to add to a true (airless) altitude — Saemundsson (1986) with
        /// pressure/temperature correction. Returns 0 well below the horizon.
        /// </summary>
        public static double RefractionFromTrue(double altTrue, double pressureHpa = 1010, double tempC = 10)
        {
            double h = altTrue * AstroMath.Rad2Deg;
            if (h < -1.9) return 0;
            double R = 1.02 / Math.Tan((h + 10.3 / (h + 5.11)) * AstroMath.Deg2Rad); // arcmin
            R *= (pressureHpa / 1010.0) * (283.0 / (273.0 + tempC));
            return Math.Max(0, R) / 60.0 * AstroMath.Deg2Rad;
        }

        /// <summary>Refraction (radians) for an apparent altitude — Bennett (1982).</summary>
        public static double RefractionFromApparent(double altApp, double pressureHpa = 1010, double tempC = 10)
        {
            double h = altApp * AstroMath.Rad2Deg;
            if (h < -1.9) return 0;
            double R = 1.0 / Math.Tan((h + 7.31 / (h + 4.4)) * AstroMath.Deg2Rad);
            R *= (pressureHpa / 1010.0) * (283.0 / (273.0 + tempC));
            return Math.Max(0, R) / 60.0 * AstroMath.Deg2Rad;
        }

        /// <summary>Relative airmass, Pickering (2002) formula, valid down to the horizon. altApp in radians.</summary>
        public static double Airmass(double altApp)
        {
            double h = altApp * AstroMath.Rad2Deg;
            if (h <= -1) return double.PositiveInfinity;
            return 1.0 / Math.Sin((h + 244.0 / (165.0 + 47.0 * Math.Pow(Math.Max(h, 0), 1.1))) * AstroMath.Deg2Rad);
        }

        /// <summary>
        /// Differential (atmospheric dispersion) between two wavelengths in arcsec at a given apparent altitude,
        /// used for ADC/RGB channel alignment. Uses Edlén/Ciddor-like approximation of (n-1).
        /// </summary>
        public static double AtmosphericDispersionArcsec(double altApp, double lambda1Nm, double lambda2Nm, double pressureHpa = 1010, double tempC = 10)
        {
            Func<double, double> nMinus1 = lam =>
            {
                double s2 = 1.0 / Math.Pow(lam / 1000.0, 2); // 1/um^2
                double n = 1e-8 * (8342.13 + 2406030.0 / (130.0 - s2) + 15997.0 / (38.9 - s2));
                return n * (pressureHpa / 1013.25) * (288.15 / (273.15 + tempC));
            };
            double z = AstroMath.HalfPi - altApp;
            return (nMinus1(lambda1Nm) - nMinus1(lambda2Nm)) * Math.Tan(z) * AstroMath.Rad2ArcSec;
        }

        /// <summary>Field rotation rate (rad/s) for an alt-az mount: ω cos(lat) cos(Az) / cos(Alt).</summary>
        public static double FieldRotationRate(double az, double alt, double lat)
        {
            const double omega = 7.2921150e-5;
            return omega * Math.Cos(lat) * Math.Cos(az) / Math.Cos(alt);
        }
    }

    /// <summary>Equinox/epoch conversions for catalog input (B1950 FK4 -> J2000 FK5 approximate).</summary>
    public static class EpochConversion
    {
        /// <summary>
        /// Apply space motion from catalog epoch to target epoch (rigorous linear space motion).
        /// pmRa is mu_alpha* (includes cos dec) in mas/yr, pmDec mas/yr, parallax mas, rv km/s.
        /// </summary>
        public static void ApplyProperMotion(double ra, double dec, double pmRaMasYr, double pmDecMasYr, double parallaxMas, double rvKms,
            double years, out double raOut, out double decOut)
        {
            if ((pmRaMasYr == 0 && pmDecMasYr == 0) || years == 0 || double.IsNaN(pmRaMasYr)) { raOut = ra; decOut = dec; return; }
            double sa = Math.Sin(ra), ca = Math.Cos(ra), sd = Math.Sin(dec), cd = Math.Cos(dec);
            var p = new Vec3(cd * ca, cd * sa, sd);
            var pE = new Vec3(-sa, ca, 0);
            var pN = new Vec3(-sd * ca, -sd * sa, cd);
            double k = AstroMath.ArcSec2Rad / 1000.0;
            double rvTerm = 0;
            if (!double.IsNaN(parallaxMas) && parallaxMas > 0 && !double.IsNaN(rvKms))
                rvTerm = rvKms * parallaxMas / 4.740470446 / 1000.0 * k * 1000.0; // rad/yr scaled radial component
            var vel = pE * (pmRaMasYr * k) + pN * (pmDecMasYr * k) + p * rvTerm;
            var pos = p + vel * years;
            pos.ToSpherical(out raOut, out decOut);
        }

        /// <summary>Approximate B1950 (FK4) to J2000 (FK5) via precession only (sub-arcsec errors from E-terms ignored).</summary>
        public static void B1950ToJ2000(double ra, double dec, out double raOut, out double decOut)
        {
            double t = (2433282.4235 - 2451545.0) / 36525.0;
            Precession.Precess(ra, dec, t, 0, out raOut, out decOut);
        }
    }
}
