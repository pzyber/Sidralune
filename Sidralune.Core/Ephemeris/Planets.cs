using System;
using Sidralune.Core.Coordinates;
using Sidralune.Core.Mathematics;

namespace Sidralune.Core.Ephemeris
{
    public enum Body
    {
        Sun = 0, Mercury = 1, Venus = 2, Earth = 3, Mars = 4, Jupiter = 5, Saturn = 6, Uranus = 7, Neptune = 8, Pluto = 9, Moon = 10, EarthMoonBarycenter = 11
    }

    /// <summary>Source of solar-system body positions in the ICRF (equatorial J2000), AU, all relative to one common origin.</summary>
    public interface IEphemerisSource
    {
        string Name { get; }
        /// <summary>Accuracy class for UI display.</summary>
        string Accuracy { get; }
        bool Covers(double jdTDB);
        /// <summary>Position and velocity (AU, AU/day) relative to this source's origin (SSB or Sun).</summary>
        void State(Body body, double jdTDB, out Vec3 pos, out Vec3 vel);
    }

    /// <summary>
    /// Analytic ephemeris: JPL "Keplerian elements for approximate positions of the major planets"
    /// (Standish, 1800-2050 table with 3000BC-3000AD extensions for outer planets) plus Meeus/ELP2000 Moon.
    /// Origin is the Sun. Typical accuracy: arcseconds (inner) to ~arcminutes (Saturn).
    /// </summary>
    public class AnalyticEphemeris : IEphemerisSource
    {
        public string Name => "Analytic (JPL Keplerian + ELP2000/Meeus)";
        public string Accuracy => "≈ 10\"–2' (planets), ≈ 10\" (Moon)";
        public bool Covers(double jd) => jd > 625000 && jd < 2816000;

        // a, e, I, L, varpi, Omega and their rates per century (Standish Table 1, 1800-2050)
        private static readonly double[,] El =
        {
            { 0.38709927, 0.20563593, 7.00497902, 252.25032350, 77.45779628, 48.33076593, 0.00000037, 0.00001906, -0.00594749, 149472.67411175, 0.16047689, -0.12534081 },
            { 0.72333566, 0.00677672, 3.39467605, 181.97909950, 131.60246718, 76.67984255, 0.00000390, -0.00004107, -0.00078890, 58517.81538729, 0.00268329, -0.27769418 },
            { 1.00000261, 0.01671123, -0.00001531, 100.46457166, 102.93768193, 0.0, 0.00000562, -0.00004392, -0.01294668, 35999.37244981, 0.32327364, 0.0 },
            { 1.52371034, 0.09339410, 1.84969142, -4.55343205, -23.94362959, 49.55953891, 0.00001847, 0.00007882, -0.00813131, 19140.30268499, 0.44441088, -0.29257343 },
            { 5.20288700, 0.04838624, 1.30439695, 34.39644051, 14.72847983, 100.47390909, -0.00011607, -0.00013253, -0.00183714, 3034.74612775, 0.21252668, 0.20469106 },
            { 9.53667594, 0.05386179, 2.48599187, 49.95424423, 92.59887831, 113.66242448, -0.00125060, -0.00050991, 0.00193609, 1222.49362201, -0.41897216, -0.28867794 },
            { 19.18916464, 0.04725744, 0.77263783, 313.23810451, 170.95427630, 74.01692503, -0.00196176, -0.00004397, -0.00242939, 428.48202785, 0.40805281, 0.04240589 },
            { 30.06992276, 0.00859048, 1.77004347, -55.12002969, 44.96476227, 131.78422574, 0.00026291, 0.00005105, 0.00035372, 218.45945325, -0.32241464, -0.00508664 },
            { 39.48211675, 0.24882730, 17.14001206, 238.92903833, 224.06891629, 110.30393684, -0.00031596, 0.00005170, 0.00004818, 145.20780515, -0.04062942, -0.01183482 },
        };

        private static readonly double EclToEq = Precession.ObliquityJ2000;
        public const double EarthMoonMassRatio = 81.30056907;
        /// <summary>Apply the Jupiter/Saturn/Uranus mutual perturbation terms.</summary>
        public static bool UsePerturbations = true;
        /// <summary>Validated against JPL Horizons 1990-2045: Jupiter terms reduce rms error 167"->127"; the Saturn/Uranus terms do not help with Standish elements, so they are off.</summary>
        public static double SaturnScale = 0.0, UranusScale = 0.0;

        /// <summary>Heliocentric ecliptic J2000 position (AU) of a planet (index 0=Mercury ... 8=Pluto; 2 = EMB).</summary>
        public static Vec3 HeliocentricEcliptic(int idx, double jd)
        {
            double T = (jd - 2451545.0) / 36525.0;
            double a = El[idx, 0] + El[idx, 6] * T;
            double e = El[idx, 1] + El[idx, 7] * T;
            double I = (El[idx, 2] + El[idx, 8] * T) * AstroMath.Deg2Rad;
            double L = El[idx, 3] + El[idx, 9] * T;
            double wbar = El[idx, 4] + El[idx, 10] * T;
            double Om = (El[idx, 5] + El[idx, 11] * T) * AstroMath.Deg2Rad;
            double Mdeg = L - wbar;
            // Great-inequality and resonance perturbations in mean longitude (Schlyter/Meeus low-precision terms)
            double Mj = (19.8950 + 0.0830853001 * (jd - 2451543.5)) * AstroMath.Deg2Rad;
            double Ms = (316.9670 + 0.0334442282 * (jd - 2451543.5)) * AstroMath.Deg2Rad;
            double Mu = (142.5905 + 0.011725806 * (jd - 2451543.5)) * AstroMath.Deg2Rad;
            double dLon = 0, dLat = 0;
            if (idx == 4)
            {
                dLon = -0.332 * Math.Sin(2 * Mj - 5 * Ms - 67.6 * AstroMath.Deg2Rad) - 0.056 * Math.Sin(2 * Mj - 2 * Ms + 21 * AstroMath.Deg2Rad)
                    + 0.042 * Math.Sin(3 * Mj - 5 * Ms + 21 * AstroMath.Deg2Rad) - 0.036 * Math.Sin(Mj - 2 * Ms) + 0.022 * Math.Cos(Mj - Ms)
                    + 0.023 * Math.Sin(2 * Mj - 3 * Ms + 52 * AstroMath.Deg2Rad) - 0.016 * Math.Sin(Mj - 5 * Ms - 69 * AstroMath.Deg2Rad);
            }
            else if (idx == 5)
            {
                dLon = 0.812 * Math.Sin(2 * Mj - 5 * Ms - 67.6 * AstroMath.Deg2Rad) - 0.229 * Math.Cos(2 * Mj - 4 * Ms - 2 * AstroMath.Deg2Rad)
                    + 0.119 * Math.Sin(Mj - 2 * Ms - 3 * AstroMath.Deg2Rad) + 0.046 * Math.Sin(2 * Mj - 6 * Ms - 69 * AstroMath.Deg2Rad)
                    + 0.014 * Math.Sin(Mj - 3 * Ms + 32 * AstroMath.Deg2Rad);
                dLat = -0.020 * Math.Cos(2 * Mj - 4 * Ms - 2 * AstroMath.Deg2Rad) + 0.018 * Math.Sin(2 * Mj - 6 * Ms - 49 * AstroMath.Deg2Rad);
                dLon *= SaturnScale; dLat *= SaturnScale;
            }
            else if (idx == 6)
            {
                dLon = 0.040 * Math.Sin(Ms - 2 * Mu + 6 * AstroMath.Deg2Rad) + 0.035 * Math.Sin(Ms - 3 * Mu + 33 * AstroMath.Deg2Rad)
                    - 0.015 * Math.Sin(Mj - Mu + 20 * AstroMath.Deg2Rad);
                dLon *= UranusScale;
            }
            double w = (wbar - El[idx, 5] - El[idx, 11] * T) * AstroMath.Deg2Rad;
            double M = AstroMath.NormalizeRadPi(Mdeg * AstroMath.Deg2Rad);
            double E = Kepler.SolveElliptic(M, e);
            double xp = a * (Math.Cos(E) - e);
            double yp = a * Math.Sqrt(1 - e * e) * Math.Sin(E);
            double cw = Math.Cos(w), sw = Math.Sin(w), cO = Math.Cos(Om), sO = Math.Sin(Om), cI = Math.Cos(I), sI = Math.Sin(I);
            var v = new Vec3(
                (cw * cO - sw * sO * cI) * xp + (-sw * cO - cw * sO * cI) * yp,
                (cw * sO + sw * cO * cI) * xp + (-sw * sO + cw * cO * cI) * yp,
                (sw * sI) * xp + (cw * sI) * yp);
            if (UsePerturbations && (dLon != 0 || dLat != 0))
            {
                double lon, lat, r;
                v.ToSpherical(out lon, out lat, out r);
                v = Vec3.FromSpherical(lon + dLon * AstroMath.Deg2Rad, lat + dLat * AstroMath.Deg2Rad, r);
            }
            return v;
        }

        private static Vec3 PlanetEq(int idx, double jd) => Transforms.EclipticToEquatorial(HeliocentricEcliptic(idx, jd), EclToEq);

        public void State(Body body, double jdTDB, out Vec3 pos, out Vec3 vel)
        {
            const double h = 0.01;
            pos = Position(body, jdTDB);
            vel = (Position(body, jdTDB + h) - Position(body, jdTDB - h)) / (2 * h);
        }

        public Vec3 Position(Body body, double jd)
        {
            switch (body)
            {
                case Body.Sun: return Vec3.Zero;
                case Body.Mercury: return PlanetEq(0, jd);
                case Body.Venus: return PlanetEq(1, jd);
                case Body.EarthMoonBarycenter: return PlanetEq(2, jd);
                case Body.Earth:
                    {
                        var emb = PlanetEq(2, jd);
                        var moon = LunarTheory.GeocentricEquatorialJ2000(jd);
                        return emb - moon / (1 + EarthMoonMassRatio);
                    }
                case Body.Moon:
                    {
                        var emb = PlanetEq(2, jd);
                        var moon = LunarTheory.GeocentricEquatorialJ2000(jd);
                        return emb + moon * (EarthMoonMassRatio / (1 + EarthMoonMassRatio));
                    }
                case Body.Mars: return PlanetEq(3, jd);
                case Body.Jupiter: return PlanetEq(4, jd);
                case Body.Saturn: return PlanetEq(5, jd);
                case Body.Uranus: return PlanetEq(6, jd);
                case Body.Neptune: return PlanetEq(7, jd);
                case Body.Pluto: return PlanetEq(8, jd);
            }
            return Vec3.Zero;
        }
    }

    /// <summary>
    /// Lunar theory: ELP-2000/82 truncated series as tabulated by Meeus (Astronomical Algorithms ch. 47).
    /// Accuracy ≈ 10" in longitude, 4" in latitude.
    /// </summary>
    public static class LunarTheory
    {
        // D, M, M', F, Σl (1e-6 deg), Σr (1e-3 km)
        private static readonly int[,] LR =
        {
            {0,0,1,0,6288774,-20905355},{2,0,-1,0,1274027,-3699111},{2,0,0,0,658314,-2955968},{0,0,2,0,213618,-569925},
            {0,1,0,0,-185116,48888},{0,0,0,2,-114332,-3149},{2,0,-2,0,58793,246158},{2,-1,-1,0,57066,-152138},
            {2,0,1,0,53322,-170733},{2,-1,0,0,45758,-204586},{0,1,-1,0,-40923,-129620},{1,0,0,0,-34720,108743},
            {0,1,1,0,-30383,104755},{2,0,0,-2,15327,10321},{0,0,1,2,-12528,0},{0,0,1,-2,10980,79661},
            {4,0,-1,0,10675,-34782},{0,0,3,0,10034,-23210},{4,0,-2,0,8548,-21636},{2,1,-1,0,-7888,24208},
            {2,1,0,0,-6766,30824},{1,0,-1,0,-5163,-8379},{1,1,0,0,4987,-16675},{2,-1,1,0,4036,-12831},
            {2,0,2,0,3994,-10445},{4,0,0,0,3861,-11650},{2,0,-3,0,3665,14403},{0,1,-2,0,-2689,-7003},
            {2,0,-1,2,-2602,0},{2,-1,-2,0,2390,10056},{1,0,1,0,-2348,6322},{2,-2,0,0,2236,-9884},
            {0,1,2,0,-2120,5751},{0,2,0,0,-2069,0},{2,-2,-1,0,2048,-4950},{2,0,1,-2,-1773,4130},
            {2,0,0,2,-1595,0},{4,-1,-1,0,1215,-3958},{0,0,2,2,-1110,0},{3,0,-1,0,-892,3258},
            {2,1,1,0,-810,2616},{4,-1,-2,0,759,-1897},{0,2,-1,0,-713,-2117},{2,2,-1,0,-700,2354},
            {2,1,-2,0,691,0},{2,-1,0,-2,596,0},{4,0,1,0,549,-1423},{0,0,4,0,537,-1117},
            {4,-1,0,0,520,-1571},{1,0,-2,0,-487,-1739},{2,1,0,-2,-399,0},{0,0,2,-2,-381,-4421},
            {1,1,1,0,351,0},{3,0,-2,0,-340,0},{4,0,-3,0,330,0},{2,-1,2,0,327,0},
            {0,2,1,0,-323,1165},{1,1,-1,0,299,0},{2,0,3,0,294,0},{2,0,-1,-2,0,8752},
        };

        // D, M, M', F, Σb (1e-6 deg)
        private static readonly int[,] B =
        {
            {0,0,0,1,5128122},{0,0,1,1,280602},{0,0,1,-1,277693},{2,0,0,-1,173237},{2,0,-1,1,55413},
            {2,0,-1,-1,46271},{2,0,0,1,32573},{0,0,2,1,17198},{2,0,1,-1,9266},{0,0,2,-1,8822},
            {2,-1,0,-1,8216},{2,0,-2,-1,4324},{2,0,1,1,4200},{2,1,0,-1,-3359},{2,-1,-1,1,2463},
            {2,-1,0,1,2211},{2,-1,-1,-1,2065},{0,1,-1,-1,-1870},{4,0,-1,-1,1828},{0,1,0,1,-1794},
            {0,0,0,3,-1749},{0,1,-1,1,-1565},{1,0,0,1,-1491},{0,1,1,1,-1475},{0,1,1,-1,-1410},
            {0,1,0,-1,-1344},{1,0,0,-1,-1335},{0,0,3,1,1107},{4,0,0,-1,1021},{4,0,-1,1,833},
            {0,0,1,-3,777},{4,0,-2,1,671},{2,0,0,-3,607},{2,0,2,-1,596},{2,-1,1,-1,491},
            {2,0,-2,1,-451},{0,0,3,-1,439},{2,0,2,1,422},{2,0,-3,-1,421},{2,1,-1,1,-366},
            {2,1,0,1,-351},{4,0,0,1,331},{2,-1,1,1,315},{2,-2,0,-1,302},{0,0,1,3,-283},
            {2,1,1,-1,-229},{1,1,0,-1,223},{1,1,0,1,223},{0,1,-2,-1,-220},{2,1,-1,-1,-220},
            {1,0,1,1,-185},{2,-1,-2,-1,181},{0,1,2,1,-177},{4,0,-2,-1,176},{4,-1,-1,-1,166},
            {1,0,1,-1,-164},{4,0,1,-1,132},{1,0,-1,-1,-119},{4,-1,0,-1,115},{2,-2,0,1,107},
        };

        /// <summary>Geocentric ecliptic longitude/latitude (radians, mean equinox of date) and distance (km).</summary>
        public static void EclipticOfDate(double jdTT, out double lon, out double lat, out double distKm)
        {
            double T = (jdTT - 2451545.0) / 36525.0;
            double Lp = 218.3164477 + 481267.88123421 * T - 0.0015786 * T * T + T * T * T / 538841 - T * T * T * T / 65194000;
            double D = 297.8501921 + 445267.1114034 * T - 0.0018819 * T * T + T * T * T / 545868 - T * T * T * T / 113065000;
            double M = 357.5291092 + 35999.0502909 * T - 0.0001536 * T * T + T * T * T / 24490000;
            double Mp = 134.9633964 + 477198.8675055 * T + 0.0087414 * T * T + T * T * T / 69699 - T * T * T * T / 14712000;
            double F = 93.2720950 + 483202.0175233 * T - 0.0036539 * T * T - T * T * T / 3526000 + T * T * T * T / 863310000;
            double A1 = 119.75 + 131.849 * T, A2 = 53.09 + 479264.290 * T, A3 = 313.45 + 481266.484 * T;
            double E = 1 - 0.002516 * T - 0.0000074 * T * T;
            double d = D * AstroMath.Deg2Rad, m = M * AstroMath.Deg2Rad, mp = Mp * AstroMath.Deg2Rad, f = F * AstroMath.Deg2Rad;
            double sl = 0, sr = 0, sb = 0;
            int n = LR.GetLength(0);
            for (int i = 0; i < n; i++)
            {
                double arg = LR[i, 0] * d + LR[i, 1] * m + LR[i, 2] * mp + LR[i, 3] * f;
                double ef = Math.Abs(LR[i, 1]) == 1 ? E : (Math.Abs(LR[i, 1]) == 2 ? E * E : 1);
                sl += LR[i, 4] * ef * Math.Sin(arg);
                sr += LR[i, 5] * ef * Math.Cos(arg);
            }
            n = B.GetLength(0);
            for (int i = 0; i < n; i++)
            {
                double arg = B[i, 0] * d + B[i, 1] * m + B[i, 2] * mp + B[i, 3] * f;
                double ef = Math.Abs(B[i, 1]) == 1 ? E : (Math.Abs(B[i, 1]) == 2 ? E * E : 1);
                sb += B[i, 4] * ef * Math.Sin(arg);
            }
            sl += 3958 * AstroMath.SinD(A1) + 1962 * AstroMath.SinD(Lp - F) + 318 * AstroMath.SinD(A2);
            sb += -2235 * AstroMath.SinD(Lp) + 382 * AstroMath.SinD(A3) + 175 * AstroMath.SinD(A1 - F)
                + 175 * AstroMath.SinD(A1 + F) + 127 * AstroMath.SinD(Lp - Mp) - 115 * AstroMath.SinD(Lp + Mp);
            lon = AstroMath.NormalizeRad((Lp + sl / 1e6) * AstroMath.Deg2Rad);
            lat = sb / 1e6 * AstroMath.Deg2Rad;
            distKm = 385000.56 + sr / 1000.0;
        }

        /// <summary>Geocentric geometric Moon position in AU, ICRF/J2000 equatorial frame.</summary>
        public static Vec3 GeocentricEquatorialJ2000(double jdTT)
        {
            double lon, lat, dist;
            EclipticOfDate(jdTT, out lon, out lat, out dist);
            double t = (jdTT - 2451545.0) / 36525.0;
            double eps = Precession.MeanObliquity(t);
            var vOfDate = Transforms.EclipticToEquatorial(Vec3.FromSpherical(lon, lat, dist / AstroMath.AU_km), eps);
            return Precession.BiasPrecessionMatrix(t).Transpose() * vOfDate;
        }

        /// <summary>Mean longitude of the ascending node (radians), used for libration and eclipses.</summary>
        public static double MeanNode(double jdTT)
        {
            double T = (jdTT - 2451545.0) / 36525.0;
            return AstroMath.NormalizeRad((125.0445479 - 1934.1362891 * T + 0.0020754 * T * T + T * T * T / 467441) * AstroMath.Deg2Rad);
        }
    }
}
