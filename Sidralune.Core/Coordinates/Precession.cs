using System;
using Sidralune.Core.Mathematics;

namespace Sidralune.Core.Coordinates
{
    /// <summary>IAU 2006 precession (Fukushima-Williams angles, frame bias included).</summary>
    public static class Precession
    {
        /// <summary>Mean obliquity of the ecliptic (IAU 2006), radians. t = Julian centuries TT since J2000.</summary>
        public static double MeanObliquity(double t) =>
            (84381.406 + t * (-46.836769 + t * (-0.0001831 + t * (0.00200340 + t * (-0.000000576 + t * -0.0000000434))))) * AstroMath.ArcSec2Rad;

        public static readonly double ObliquityJ2000 = MeanObliquity(0);

        private static void FwAngles(double t, out double gamb, out double phib, out double psib, out double epsa)
        {
            gamb = (-0.052928 + t * (10.556378 + t * (0.4932044 + t * (-0.00031238 + t * (-0.000002788 + t * 0.0000000260))))) * AstroMath.ArcSec2Rad;
            phib = (84381.412819 + t * (-46.811016 + t * (0.0511268 + t * (0.00053289 + t * (-0.000000440 + t * -0.0000000176))))) * AstroMath.ArcSec2Rad;
            psib = (-0.041775 + t * (5038.481484 + t * (1.5584175 + t * (-0.00018522 + t * (-0.000026452 + t * -0.0000000148))))) * AstroMath.ArcSec2Rad;
            epsa = MeanObliquity(t);
        }

        /// <summary>Bias-precession matrix: GCRS (≈ J2000/ICRS) to mean equator and equinox of date.</summary>
        public static Mat3 BiasPrecessionMatrix(double t)
        {
            double g, p, s, e;
            FwAngles(t, out g, out p, out s, out e);
            return Mat3.RotX(-e) * Mat3.RotZ(-s) * Mat3.RotX(p) * Mat3.RotZ(g);
        }

        /// <summary>Full bias-precession-nutation matrix: GCRS to true equator and equinox of date.</summary>
        public static Mat3 NpbMatrix(double t)
        {
            double g, p, s, e, dpsi, deps;
            FwAngles(t, out g, out p, out s, out e);
            Nutation.Compute(t, out dpsi, out deps);
            return Mat3.RotX(-(e + deps)) * Mat3.RotZ(-(s + dpsi)) * Mat3.RotX(p) * Mat3.RotZ(g);
        }

        /// <summary>Precess J2000 RA/Dec (radians) to mean equinox of date.</summary>
        public static void J2000ToMeanOfDate(double ra, double dec, double t, out double raOut, out double decOut)
        {
            var v = BiasPrecessionMatrix(t) * Vec3.FromSpherical(ra, dec);
            v.ToSpherical(out raOut, out decOut);
        }

        /// <summary>Precess mean-of-date RA/Dec (radians) back to J2000.</summary>
        public static void MeanOfDateToJ2000(double ra, double dec, double t, out double raOut, out double decOut)
        {
            var v = BiasPrecessionMatrix(t).Transpose() * Vec3.FromSpherical(ra, dec);
            v.ToSpherical(out raOut, out decOut);
        }

        /// <summary>Precess between two arbitrary epochs given as Julian centuries from J2000.</summary>
        public static void Precess(double ra, double dec, double tFrom, double tTo, out double raOut, out double decOut)
        {
            var m = BiasPrecessionMatrix(tTo) * BiasPrecessionMatrix(tFrom).Transpose();
            var v = m * Vec3.FromSpherical(ra, dec);
            v.ToSpherical(out raOut, out decOut);
        }
    }

    /// <summary>Nutation in longitude and obliquity using the leading terms of the IAU 1980/2000 series (~0.01" level).</summary>
    public static class Nutation
    {
        // l, l', F, D, Om, psi0 (0.0001"), psiT, eps0, epsT
        private static readonly double[,] Terms =
        {
            { 0, 0, 0, 0, 1, -171996, -174.2, 92025, 8.9 },
            { 0, 0, 2, -2, 2, -13187, -1.6, 5736, -3.1 },
            { 0, 0, 2, 0, 2, -2274, -0.2, 977, -0.5 },
            { 0, 0, 0, 0, 2, 2062, 0.2, -895, 0.5 },
            { 0, 1, 0, 0, 0, 1426, -3.4, 54, -0.1 },
            { 1, 0, 0, 0, 0, 712, 0.1, -7, 0 },
            { 0, 1, 2, -2, 2, -517, 1.2, 224, -0.6 },
            { 0, 0, 2, 0, 1, -386, -0.4, 200, 0 },
            { 1, 0, 2, 0, 2, -301, 0, 129, -0.1 },
            { 0, -1, 2, -2, 2, 217, -0.5, -95, 0.3 },
            { 1, 0, 0, -2, 0, -158, 0, 0, 0 },
            { 0, 0, 2, -2, 1, 129, 0.1, -70, 0 },
            { -1, 0, 2, 0, 2, 123, 0, -53, 0 },
            { 1, 0, 0, 0, 1, 63, 0.1, -33, 0 },
            { 0, 0, 0, 2, 0, 63, 0, 0, 0 },
            { -1, 0, 2, 2, 2, -59, 0, 26, 0 },
            { -1, 0, 0, 0, 1, -58, -0.1, 32, 0 },
            { 1, 0, 2, 0, 1, -51, 0, 27, 0 },
            { 2, 0, 0, -2, 0, 48, 0, 0, 0 },
            { -2, 0, 2, 0, 1, 46, 0, -24, 0 },
            { 0, 0, 2, 2, 2, -38, 0, 16, 0 },
            { 2, 0, 2, 0, 2, -31, 0, 13, 0 },
            { 2, 0, 0, 0, 0, 29, 0, 0, 0 },
            { 1, 0, 2, -2, 2, 29, 0, -12, 0 },
            { 0, 0, 2, 0, 0, 26, 0, 0, 0 },
            { 0, 0, 2, -2, 0, -22, 0, 0, 0 },
            { -1, 0, 2, 0, 1, 21, 0, -10, 0 },
            { 0, 2, 0, 0, 0, 17, -0.1, 0, 0 },
            { 0, 2, 2, -2, 2, -16, 0.1, 7, 0 },
            { -1, 0, 0, 2, 1, 16, 0, -8, 0 },
            { 0, 1, 0, 0, 1, -15, 0, 9, 0 },
            { 1, 0, 0, -2, 1, -13, 0, 7, 0 },
            { 0, -1, 0, 0, 1, -12, 0, 6, 0 },
            { 2, 0, -2, 0, 0, 11, 0, 0, 0 },
            { -1, 0, 2, 2, 1, -10, 0, 5, 0 },
            { 1, 0, 2, 2, 2, -8, 0, 3, 0 },
            { 0, -1, 2, 0, 2, -7, 0, 3, 0 },
            { 0, 0, 2, 2, 1, -7, 0, 3, 0 },
            { 1, 1, 0, -2, 0, -7, 0, 0, 0 },
            { 0, 1, 2, 0, 2, 7, 0, -3, 0 },
        };

        // cache last evaluation for speed (rendering calls this per frame)
        private static double _lastT = double.NaN, _lastPsi, _lastEps;
        private static readonly object _lock = new object();

        /// <summary>Δψ, Δε in radians for t Julian centuries TT since J2000.</summary>
        public static void Compute(double t, out double dpsi, out double deps)
        {
            lock (_lock)
            {
                if (Math.Abs(t - _lastT) < 1e-9) { dpsi = _lastPsi; deps = _lastEps; return; }
            }
            double D = (297.85036 + 445267.111480 * t - 0.0019142 * t * t + t * t * t / 189474) * AstroMath.Deg2Rad;
            double M = (357.52772 + 35999.050340 * t - 0.0001603 * t * t - t * t * t / 300000) * AstroMath.Deg2Rad;
            double Mp = (134.96298 + 477198.867398 * t + 0.0086972 * t * t + t * t * t / 56250) * AstroMath.Deg2Rad;
            double F = (93.27191 + 483202.017538 * t - 0.0036825 * t * t + t * t * t / 327270) * AstroMath.Deg2Rad;
            double Om = (125.04452 - 1934.136261 * t + 0.0020708 * t * t + t * t * t / 450000) * AstroMath.Deg2Rad;
            double sp = 0, se = 0;
            int n = Terms.GetLength(0);
            for (int i = 0; i < n; i++)
            {
                double arg = Terms[i, 0] * Mp + Terms[i, 1] * M + Terms[i, 2] * F + Terms[i, 3] * D + Terms[i, 4] * Om;
                sp += (Terms[i, 5] + Terms[i, 6] * t) * Math.Sin(arg);
                se += (Terms[i, 7] + Terms[i, 8] * t) * Math.Cos(arg);
            }
            dpsi = sp * 1e-4 * AstroMath.ArcSec2Rad;
            deps = se * 1e-4 * AstroMath.ArcSec2Rad;
            lock (_lock) { _lastT = t; _lastPsi = dpsi; _lastEps = deps; }
        }
    }
}
