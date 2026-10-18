using System;
using Sidralune.Core.Coordinates;
using Sidralune.Core.Mathematics;
using Sidralune.Core.Time;

namespace Sidralune.Core.Ephemeris
{
    /// <summary>Physical ephemeris for planets, Moon and Sun.</summary>
    public static class PlanetPhysics
    {
        /// <summary>Equatorial radius, km (IAU WGCCRE 2015).</summary>
        public static double EquatorialRadiusKm(Body b)
        {
            switch (b)
            {
                case Body.Sun: return 695700;
                case Body.Mercury: return 2440.53;
                case Body.Venus: return 6051.8;
                case Body.Earth: return 6378.137;
                case Body.Moon: return 1737.4;
                case Body.Mars: return 3396.19;
                case Body.Jupiter: return 71492;
                case Body.Saturn: return 60268;
                case Body.Uranus: return 25559;
                case Body.Neptune: return 24764;
                case Body.Pluto: return 1188.3;
            }
            return 0;
        }

        public static double PolarRadiusKm(Body b)
        {
            switch (b)
            {
                case Body.Mars: return 3376.20;
                case Body.Jupiter: return 66854;
                case Body.Saturn: return 54364;
                case Body.Uranus: return 24973;
                case Body.Neptune: return 24341;
            }
            return EquatorialRadiusKm(b);
        }

        /// <summary>Angular diameter in arcsec for a distance in AU.</summary>
        public static double AngularDiameterArcsec(Body b, double distAu) =>
            2 * Math.Atan(EquatorialRadiusKm(b) / (distAu * AstroMath.AU_km)) * AstroMath.Rad2ArcSec;

        /// <summary>
        /// Visual magnitude using Mallama &amp; Hilton (2018) phase curves. r, delta in AU, phase angle in degrees.
        /// saturnRingTiltDeg is the sub-observer ring latitude B (degrees) for Saturn.
        /// </summary>
        public static double Magnitude(Body b, double r, double delta, double phaseDeg, double saturnRingTiltDeg = 0)
        {
            double a = phaseDeg;
            double d = 5 * Math.Log10(r * delta);
            switch (b)
            {
                case Body.Mercury:
                    return -0.613 + d + 6.3280e-02 * a - 1.6336e-03 * a * a + 3.3644e-05 * Math.Pow(a, 3)
                           - 3.4265e-07 * Math.Pow(a, 4) + 1.6893e-09 * Math.Pow(a, 5) - 3.0334e-12 * Math.Pow(a, 6);
                case Body.Venus:
                    if (a < 163.7) return -4.384 + d - 1.044e-03 * a + 3.687e-04 * a * a - 2.814e-06 * Math.Pow(a, 3) + 8.938e-09 * Math.Pow(a, 4);
                    return 236.05828 + d - 2.81914 * a + 8.39034e-03 * a * a;
                case Body.Mars:
                    if (a <= 50) return -1.601 + d + 2.267e-02 * a - 1.302e-04 * a * a;
                    return -0.367 + d - 0.02573 * a + 0.0003445 * a * a;
                case Body.Jupiter:
                    if (a <= 12) return -9.395 + d - 3.7e-04 * a + 6.16e-04 * a * a;
                    return -9.428 + d - 2.5 * Math.Log10(1 - 1.507 * (a / 180) - 0.363 * Math.Pow(a / 180, 2) - 0.062 * Math.Pow(a / 180, 3) + 2.809 * Math.Pow(a / 180, 4) - 1.876 * Math.Pow(a / 180, 5));
                case Body.Saturn:
                    {
                        double sb = Math.Sin(Math.Abs(saturnRingTiltDeg) * AstroMath.Deg2Rad);
                        if (a <= 6.5) return -8.914 + d - 1.825 * sb + 0.026 * a - 0.378 * sb * Math.Exp(-2.25 * a);
                        return -8.94 + d + 2.446e-4 * a + 2.672e-4 * a * a - 1.506e-6 * Math.Pow(a, 3) + 4.767e-9 * Math.Pow(a, 4);
                    }
                case Body.Uranus:
                    return -7.110 + d + 6.587e-3 * a + 1.045e-4 * a * a;
                case Body.Neptune:
                    return -7.00 + d + 7.944e-3 * a + 9.617e-5 * a * a;
                case Body.Pluto:
                    return -1.01 + d + 0.041 * a;
                case Body.Moon:
                    {
                        // Allen / Hapke-like fit for the full lunar disc (Lane & Irvine), r in AU from Sun, delta in AU from observer
                        double am = Math.Abs(a);
                        return 0.23 + 5 * Math.Log10(r * delta) + 0.026 * am + 4.0e-9 * Math.Pow(am, 4);
                    }
                case Body.Sun:
                    return -26.74 + 5 * Math.Log10(delta);
            }
            return double.NaN;
        }

        /// <summary>Apparent magnitude and diameter fill-in for a BodyPlace.</summary>
        public static void Fill(Body b, BodyPlace p, double jdTT)
        {
            double phaseDeg = p.PhaseAngle * AstroMath.Rad2Deg;
            double B = 0;
            if (b == Body.Saturn)
            {
                double P, Bdeg, U;
                SaturnRings(jdTT, p, out Bdeg, out P, out U);
                B = Bdeg;
            }
            if (b == Body.Moon)
                p.Magnitude = Magnitude(b, p.HelioDistance, p.Distance, phaseDeg);
            else if (b == Body.Sun)
                p.Magnitude = Magnitude(b, 1, p.Distance, 0);
            else
                p.Magnitude = Magnitude(b, p.HelioDistance, p.Distance, phaseDeg, B);
            p.DiameterArcsec = AngularDiameterArcsec(b, p.Distance);
        }

        // ------------------------------------------------------------------ rotation models (IAU WGCCRE)

        /// <summary>North pole RA/Dec (deg, ICRF) and prime meridian W (deg) of a body at JD TDB.</summary>
        public static void RotationElements(Body b, double jd, out double ra0, out double dec0, out double W)
        {
            double d = jd - 2451545.0, T = d / 36525.0;
            switch (b)
            {
                case Body.Sun: ra0 = 286.13; dec0 = 63.87; W = 84.176 + 14.1844000 * d; break;
                case Body.Mercury: ra0 = 281.0103 - 0.0328 * T; dec0 = 61.4155 - 0.0049 * T; W = 329.5988 + 6.1385108 * d; break;
                case Body.Venus: ra0 = 272.76; dec0 = 67.16; W = 160.20 - 1.4813688 * d; break;
                case Body.Mars:
                    ra0 = 317.269202 - 0.10927547 * T; dec0 = 54.432516 - 0.05827105 * T; W = 176.049863 + 350.891982443297 * d; break;
                case Body.Jupiter: ra0 = 268.056595 - 0.006499 * T; dec0 = 64.495303 + 0.002413 * T; W = 284.95 + 870.5360000 * d; break; // System III
                case Body.Saturn: ra0 = 40.589 - 0.036 * T; dec0 = 83.537 - 0.004 * T; W = 38.90 + 810.7939024 * d; break;
                case Body.Uranus: ra0 = 257.311; dec0 = -15.175; W = 203.81 - 501.1600928 * d; break;
                case Body.Neptune: ra0 = 299.36; dec0 = 43.46; W = 249.978 + 541.1397757 * d; break;
                case Body.Pluto: ra0 = 132.993; dec0 = -6.163; W = 302.695 + 56.3625225 * d; break;
                case Body.Moon: ra0 = 269.9949 + 0.0031 * T; dec0 = 66.5392 + 0.0130 * T; W = 38.3213 + 13.17635815 * d; break;
                default: ra0 = 0; dec0 = 90; W = 0; break;
            }
            W = AstroMath.NormalizeDeg(W);
        }

        /// <summary>
        /// Planetocentric sub-observer longitude (central meridian, deg, west longitude convention for prograde bodies),
        /// sub-observer latitude (deg) and position angle of the north pole (deg) for a body seen from direction 'toBody' (ICRF unit vector from observer).
        /// lightTime in days.
        /// </summary>
        public static void Orientation(Body b, double jdTDB, Vec3 toBody, double lightTime, out double cmDeg, out double subLatDeg, out double poleAngleDeg)
        {
            double ra0, dec0, W;
            RotationElements(b, jdTDB - lightTime, out ra0, out dec0, out W);
            double a0 = ra0 * AstroMath.Deg2Rad, d0 = dec0 * AstroMath.Deg2Rad;
            var pole = Vec3.FromSpherical(a0, d0);
            var u = toBody.Normalized();
            // sub-observer latitude
            subLatDeg = -Math.Asin(AstroMath.Clamp(Vec3.Dot(pole, u), -1, 1)) * AstroMath.Rad2Deg;
            // prime meridian node: Q = node of equator on ICRF equator at RA = a0 + 90
            var node = Vec3.FromSpherical(a0 + AstroMath.HalfPi, 0);
            var east = Vec3.Cross(pole, node);
            // vector from body to observer projected into body equator
            var v = -u;
            double x = Vec3.Dot(v, node), y = Vec3.Dot(v, east);
            double lonOfObs = Math.Atan2(y, x) * AstroMath.Rad2Deg; // angle from node measured eastward
            // W is angle from node to prime meridian; west longitude of sub-observer point
            cmDeg = AstroMath.NormalizeDeg(W - lonOfObs);
            if (b == Body.Venus || b == Body.Uranus || b == Body.Pluto) cmDeg = AstroMath.NormalizeDeg(lonOfObs - W);
            double ra, dec;
            u.ToSpherical(out ra, out dec);
            poleAngleDeg = AstroMath.NormalizeDeg(AstroMath.PositionAngle(ra, dec, a0, d0) * AstroMath.Rad2Deg);
        }

        /// <summary>Jupiter central meridian longitudes in System I, II and III (deg).</summary>
        public static void JupiterCentralMeridians(double jdTDB, Vec3 toBody, double lightTime, out double sysI, out double sysII, out double sysIII)
        {
            double cm3, lat, pa;
            Orientation(Body.Jupiter, jdTDB, toBody, lightTime, out cm3, out lat, out pa);
            double d = jdTDB - lightTime - 2451545.0;
            // System I/II rotate at 877.90 and 870.27 deg/day; offsets from WGCCRE W0 values
            double wI = 67.1 + 877.900 * d, wII = 43.3 + 870.270 * d, wIII = 284.95 + 870.536 * d;
            sysIII = cm3;
            sysI = AstroMath.NormalizeDeg(cm3 + (wI - wIII));
            sysII = AstroMath.NormalizeDeg(cm3 + (wII - wIII));
        }

        /// <summary>Saturn ring geometry: B (sub-observer latitude on ring plane), P (position angle of pole), U (deg).</summary>
        public static void SaturnRings(double jdTT, BodyPlace p, out double Bdeg, out double Pdeg, out double U)
        {
            double cm;
            Orientation(Body.Saturn, jdTT, p.Astrometric.ToVector(), p.LightTimeDays, out cm, out Bdeg, out Pdeg);
            U = cm;
        }

        /// <summary>Position angle (radians) of the midpoint of the bright limb (Meeus 48.5).</summary>
        public static double BrightLimbAngle(Equatorial body, Equatorial sun)
        {
            double y = Math.Cos(sun.Dec) * Math.Sin(sun.Ra - body.Ra);
            double x = Math.Sin(sun.Dec) * Math.Cos(body.Dec) - Math.Cos(sun.Dec) * Math.Sin(body.Dec) * Math.Cos(sun.Ra - body.Ra);
            return AstroMath.NormalizeRad(Math.Atan2(y, x));
        }

        /// <summary>Solar physical ephemeris P, B0, L0 (Carrington longitude), degrees, and Carrington rotation number.</summary>
        public static void SunOrientation(double jdTT, out double P, out double B0, out double L0, out int carringtonRotation)
        {
            // Meeus ch.29
            double T = (jdTT - 2451545.0) / 36525.0;
            double theta = AstroMath.NormalizeDeg((jdTT - 2398220.0) * 360.0 / 25.38);
            double I = 7.25;
            double K = 73.6667 + 1.3958333 * (jdTT - 2396758.0) / 36525.0;
            double L, Bsun, R;
            SunApparentEcliptic(jdTT, out L, out Bsun, out R);
            double lambda = L; // apparent longitude, deg
            double eps = (Precession.MeanObliquity(T)) * AstroMath.Rad2Deg;
            double dpsi, deps;
            Nutation.Compute(T, out dpsi, out deps);
            eps += deps * AstroMath.Rad2Deg;
            double lambdaP = lambda + dpsi * AstroMath.Rad2Deg;
            double x = Math.Atan(-AstroMath.CosD(lambdaP) * AstroMath.TanD(eps)) * AstroMath.Rad2Deg;
            double y = Math.Atan(-AstroMath.CosD(lambda - K) * AstroMath.TanD(I)) * AstroMath.Rad2Deg;
            P = x + y;
            B0 = AstroMath.AsinD(AstroMath.SinD(lambda - K) * AstroMath.SinD(I));
            double eta = Math.Atan2(-AstroMath.SinD(lambda - K) * AstroMath.CosD(I), -AstroMath.CosD(lambda - K)) * AstroMath.Rad2Deg;
            L0 = AstroMath.NormalizeDeg(eta - theta);
            carringtonRotation = (int)Math.Floor((jdTT - 2398140.2270) / 27.2752316) + 1;
        }

        /// <summary>Apparent geocentric ecliptic longitude/latitude of the Sun (deg) and distance (AU), true equinox of date.</summary>
        public static void SunApparentEcliptic(double jdTT, out double lonDeg, out double latDeg, out double rAu)
        {
            Vec3 e, v;
            EphemerisManager.EarthHeliocentric(jdTT, out e, out v);
            var sunGeo = -e;
            double T = (jdTT - 2451545.0) / 36525.0;
            // light-time aberration ≈ -20.4898"/R
            var tod = Precession.NpbMatrix(T) * sunGeo;
            double dpsi, deps;
            Nutation.Compute(T, out dpsi, out deps);
            var ecl = Transforms.EquatorialToEcliptic(tod, Precession.MeanObliquity(T) + deps);
            double lon, lat, r;
            ecl.ToSpherical(out lon, out lat, out r);
            lonDeg = AstroMath.NormalizeDeg(lon * AstroMath.Rad2Deg - 20.4898 / 3600.0 / r);
            latDeg = lat * AstroMath.Rad2Deg;
            rAu = r;
        }

        /// <summary>Lunar optical libration (deg, Meeus ch.53): longitude l, latitude b and position angle of axis.</summary>
        public static void MoonLibration(double jdTT, out double l, out double b, out double paAxis)
        {
            double T = (jdTT - 2451545.0) / 36525.0;
            double lon, lat, dist;
            LunarTheory.EclipticOfDate(jdTT, out lon, out lat, out dist);
            double dpsi, deps;
            Nutation.Compute(T, out dpsi, out deps);
            double lambda = lon * AstroMath.Rad2Deg + dpsi * AstroMath.Rad2Deg;
            double beta = lat * AstroMath.Rad2Deg;
            double I = 1.54242;
            double Om = LunarTheory.MeanNode(jdTT) * AstroMath.Rad2Deg;
            double F = 93.2720950 + 483202.0175233 * T - 0.0036539 * T * T;
            double W = lambda - dpsi * AstroMath.Rad2Deg - Om;
            double A = Math.Atan2(AstroMath.SinD(W) * AstroMath.CosD(beta) * AstroMath.CosD(I) - AstroMath.SinD(beta) * AstroMath.SinD(I),
                AstroMath.CosD(W) * AstroMath.CosD(beta)) * AstroMath.Rad2Deg;
            l = AstroMath.NormalizeDeg180(A - F);
            b = AstroMath.AsinD(-AstroMath.SinD(W) * AstroMath.CosD(beta) * AstroMath.SinD(I) - AstroMath.SinD(beta) * AstroMath.CosD(I));
            // position angle of axis (approximate, ignoring physical libration)
            double eps = Precession.MeanObliquity(T) * AstroMath.Rad2Deg + deps * AstroMath.Rad2Deg;
            double V = Om + dpsi * AstroMath.Rad2Deg;
            double X = AstroMath.SinD(I) * AstroMath.SinD(V);
            double Y = AstroMath.SinD(I) * AstroMath.CosD(V) * AstroMath.CosD(eps) - AstroMath.CosD(I) * AstroMath.SinD(eps);
            double omega = Math.Atan2(X, Y) * AstroMath.Rad2Deg;
            double ra, dec;
            Transforms.EclipticToEquatorial(lambda * AstroMath.Deg2Rad, beta * AstroMath.Deg2Rad, eps * AstroMath.Deg2Rad, out ra, out dec);
            paAxis = AstroMath.NormalizeDeg(AstroMath.AsinD(Math.Sqrt(X * X + Y * Y) * Math.Cos(ra - omega * AstroMath.Deg2Rad) / AstroMath.CosD(b)));
        }

        /// <summary>Selenographic colongitude of the Sun (deg), useful for lunar terminator planning.</summary>
        public static double MoonColongitude(double jdTT)
        {
            double T = (jdTT - 2451545.0) / 36525.0;
            double sl, sb, sr;
            SunApparentEcliptic(jdTT, out sl, out sb, out sr);
            double lonM, latM, dist;
            LunarTheory.EclipticOfDate(jdTT, out lonM, out latM, out dist);
            // heliocentric direction of Moon ≈ Sun direction + 180, corrected by Moon offset
            double lambdaH = sl + 180 + dist / (sr * AstroMath.AU_km) * 57.296 * AstroMath.CosD(latM * AstroMath.Rad2Deg) * AstroMath.SinD(sl - lonM * AstroMath.Rad2Deg);
            double betaH = dist / (sr * AstroMath.AU_km) * latM * AstroMath.Rad2Deg;
            double I = 1.54242;
            double Om = LunarTheory.MeanNode(jdTT) * AstroMath.Rad2Deg;
            double F = 93.2720950 + 483202.0175233 * T;
            double W = lambdaH - Om;
            double A = Math.Atan2(AstroMath.SinD(W) * AstroMath.CosD(betaH) * AstroMath.CosD(I) - AstroMath.SinD(betaH) * AstroMath.SinD(I),
                AstroMath.CosD(W) * AstroMath.CosD(betaH)) * AstroMath.Rad2Deg;
            double l0 = A - F;
            return AstroMath.NormalizeDeg(90 - l0);
        }
    }
}
