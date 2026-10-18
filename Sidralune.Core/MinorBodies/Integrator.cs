using System;
using Sidralune.Core.Coordinates;
using Sidralune.Core.Ephemeris;
using Sidralune.Core.Mathematics;

namespace Sidralune.Core.MinorBodies
{
    /// <summary>
    /// Heliocentric N-body propagation of a massless body perturbed by the planets (direct + indirect terms),
    /// integrated with an adaptive Dormand-Prince 5(4) scheme. Planet positions come from the active ephemeris.
    /// </summary>
    public static class PerturbedPropagator
    {
        // GM of planets relative to Sun (mass ratios), Mercury..Neptune (EMB for Earth)
        private static readonly Body[] Perturbers = { Body.Mercury, Body.Venus, Body.EarthMoonBarycenter, Body.Mars, Body.Jupiter, Body.Saturn, Body.Uranus, Body.Neptune };
        private static readonly double[] MassRatio = { 1 / 6023682.0, 1 / 408523.72, 1 / 328900.56, 1 / 3098703.6, 1 / 1047.348644, 1 / 3497.9018, 1 / 22902.951, 1 / 19412.26 };

        private static Vec3 Accel(double jd, Vec3 r)
        {
            double mu = AstroMath.GM_Sun;
            double rr = r.Length;
            var a = r * (-mu / (rr * rr * rr));
            var sun = EphemerisManager.Position(Body.Sun, jd);
            for (int i = 0; i < Perturbers.Length; i++)
            {
                var p = EphemerisManager.Position(Perturbers[i], jd) - sun; // heliocentric planet
                var d = p - r;
                double dl = d.Length, pl = p.Length;
                double m = mu * MassRatio[i];
                a = a + d * (m / (dl * dl * dl)) - p * (m / (pl * pl * pl));
            }
            return a;
        }

        /// <summary>Propagate heliocentric ICRF equatorial state (AU, AU/d) from jd0 to jd1.</summary>
        public static void Propagate(ref Vec3 r, ref Vec3 v, double jd0, double jd1, double tol = 1e-11)
        {
            double t = jd0, h = Math.Sign(jd1 - jd0) * Math.Min(1.0, Math.Abs(jd1 - jd0));
            if (h == 0) return;
            // Dormand-Prince coefficients
            const double c2 = 1.0 / 5, c3 = 3.0 / 10, c4 = 4.0 / 5, c5 = 8.0 / 9;
            const double a21 = 1.0 / 5;
            const double a31 = 3.0 / 40, a32 = 9.0 / 40;
            const double a41 = 44.0 / 45, a42 = -56.0 / 15, a43 = 32.0 / 9;
            const double a51 = 19372.0 / 6561, a52 = -25360.0 / 2187, a53 = 64448.0 / 6561, a54 = -212.0 / 729;
            const double a61 = 9017.0 / 3168, a62 = -355.0 / 33, a63 = 46732.0 / 5247, a64 = 49.0 / 176, a65 = -5103.0 / 18656;
            const double b1 = 35.0 / 384, b3 = 500.0 / 1113, b4 = 125.0 / 192, b5 = -2187.0 / 6784, b6 = 11.0 / 84;
            const double e1 = 71.0 / 57600, e3 = -71.0 / 16695, e4 = 71.0 / 1920, e5 = -17253.0 / 339200, e6 = 22.0 / 525, e7 = -1.0 / 40;
            int guard = 0;
            while (Math.Sign(jd1 - t) == Math.Sign(h) && Math.Abs(jd1 - t) > 1e-9 && guard++ < 1000000)
            {
                if (Math.Abs(h) > Math.Abs(jd1 - t)) h = jd1 - t;
                Vec3 k1r = v, k1v = Accel(t, r);
                Vec3 r2 = r + h * (a21 * k1r), v2 = v + h * (a21 * k1v);
                Vec3 k2r = v2, k2v = Accel(t + c2 * h, r2);
                Vec3 r3 = r + h * (a31 * k1r + a32 * k2r), v3 = v + h * (a31 * k1v + a32 * k2v);
                Vec3 k3r = v3, k3v = Accel(t + c3 * h, r3);
                Vec3 r4 = r + h * (a41 * k1r + a42 * k2r + a43 * k3r), v4 = v + h * (a41 * k1v + a42 * k2v + a43 * k3v);
                Vec3 k4r = v4, k4v = Accel(t + c4 * h, r4);
                Vec3 r5 = r + h * (a51 * k1r + a52 * k2r + a53 * k3r + a54 * k4r), v5 = v + h * (a51 * k1v + a52 * k2v + a53 * k3v + a54 * k4v);
                Vec3 k5r = v5, k5v = Accel(t + c5 * h, r5);
                Vec3 r6 = r + h * (a61 * k1r + a62 * k2r + a63 * k3r + a64 * k4r + a65 * k5r), v6 = v + h * (a61 * k1v + a62 * k2v + a63 * k3v + a64 * k4v + a65 * k5v);
                Vec3 k6r = v6, k6v = Accel(t + h, r6);
                Vec3 rn = r + h * (b1 * k1r + b3 * k3r + b4 * k4r + b5 * k5r + b6 * k6r);
                Vec3 vn = v + h * (b1 * k1v + b3 * k3v + b4 * k4v + b5 * k5v + b6 * k6v);
                Vec3 k7r = vn, k7v = Accel(t + h, rn);
                Vec3 er = h * (e1 * k1r + e3 * k3r + e4 * k4r + e5 * k5r + e6 * k6r + e7 * k7r);
                Vec3 ev = h * (e1 * k1v + e3 * k3v + e4 * k4v + e5 * k5v + e6 * k6v + e7 * k7v);
                double err = Math.Max(er.Length / Math.Max(1e-3, rn.Length), ev.Length / Math.Max(1e-6, vn.Length));
                if (err <= tol || Math.Abs(h) < 1e-6)
                {
                    t += h; r = rn; v = vn;
                }
                double fac = err > 0 ? 0.9 * Math.Pow(tol / err, 0.2) : 5;
                h *= AstroMath.Clamp(fac, 0.2, 5.0);
                if (Math.Abs(h) > 40) h = Math.Sign(h) * 40;
            }
        }

        /// <summary>Return new osculating elements of a minor body propagated (with perturbations) to a new epoch.</summary>
        public static OrbitalElements ToEpoch(OrbitalElements el, double newEpoch)
        {
            Vec3 pe, ve;
            el.StateAt(el.Epoch, out pe, out ve);
            double eps = Precession.ObliquityJ2000;
            var r = Transforms.EclipticToEquatorial(pe, eps);
            var v = Transforms.EclipticToEquatorial(ve, eps);
            Propagate(ref r, ref v, el.Epoch, newEpoch);
            return OrbitalElements.FromState(Transforms.EquatorialToEcliptic(r, eps), Transforms.EquatorialToEcliptic(v, eps), newEpoch);
        }
    }
}
