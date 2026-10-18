using System;
using Sidralune.Core.Mathematics;

namespace Sidralune.Core.Ephemeris
{
    /// <summary>
    /// Heliocentric osculating orbital elements (ecliptic and equinox J2000). Handles elliptic, parabolic and
    /// hyperbolic orbits through a universal-variable propagator.
    /// </summary>
    public class OrbitalElements
    {
        /// <summary>Epoch of osculation, JD TT.</summary>
        public double Epoch;
        /// <summary>Perihelion distance q (AU).</summary>
        public double PerihelionDistance;
        public double Eccentricity;
        /// <summary>Inclination, radians.</summary>
        public double Inclination;
        /// <summary>Longitude of ascending node, radians.</summary>
        public double Node;
        /// <summary>Argument of perihelion, radians.</summary>
        public double ArgPerihelion;
        /// <summary>Time of perihelion passage, JD TT.</summary>
        public double PerihelionTime;
        /// <summary>Gravitational parameter (AU^3/d^2); defaults to the Sun.</summary>
        public double Mu = AstroMath.GM_Sun;

        public double SemiMajorAxis => Eccentricity != 1.0 ? PerihelionDistance / (1 - Eccentricity) : double.PositiveInfinity;

        /// <summary>Mean motion (rad/day) for elliptical orbits.</summary>
        public double MeanMotion
        {
            get
            {
                double a = Math.Abs(SemiMajorAxis);
                return Math.Sqrt(Mu / (a * a * a));
            }
        }

        public double PeriodDays => Eccentricity < 1 ? AstroMath.TwoPi / MeanMotion : double.PositiveInfinity;

        public static OrbitalElements FromMeanAnomaly(double epoch, double a, double e, double iDeg, double nodeDeg, double periDeg, double meanAnomDeg)
        {
            var el = new OrbitalElements
            {
                Epoch = epoch,
                Eccentricity = e,
                PerihelionDistance = a * (1 - e),
                Inclination = iDeg * AstroMath.Deg2Rad,
                Node = nodeDeg * AstroMath.Deg2Rad,
                ArgPerihelion = periDeg * AstroMath.Deg2Rad,
            };
            double n = Math.Sqrt(el.Mu / (a * a * a));
            double M = AstroMath.NormalizeRadPi(meanAnomDeg * AstroMath.Deg2Rad);
            el.PerihelionTime = epoch - M / n;
            return el;
        }

        /// <summary>Position and velocity (AU, AU/day) in ecliptic J2000 frame at time jdTT.</summary>
        public void StateAt(double jdTT, out Vec3 pos, out Vec3 vel)
        {
            double q = PerihelionDistance, e = Eccentricity;
            double dt = jdTT - PerihelionTime;
            double x, y, vx, vy;
            Kepler.PerifocalState(q, e, Mu, dt, out x, out y, out vx, out vy);
            // rotate perifocal -> ecliptic
            double cw = Math.Cos(ArgPerihelion), sw = Math.Sin(ArgPerihelion);
            double cO = Math.Cos(Node), sO = Math.Sin(Node);
            double ci = Math.Cos(Inclination), si = Math.Sin(Inclination);
            double Px = cw * cO - sw * sO * ci, Py = cw * sO + sw * cO * ci, Pz = sw * si;
            double Qx = -sw * cO - cw * sO * ci, Qy = -sw * sO + cw * cO * ci, Qz = cw * si;
            pos = new Vec3(Px * x + Qx * y, Py * x + Qy * y, Pz * x + Qz * y);
            vel = new Vec3(Px * vx + Qx * vy, Py * vx + Qy * vy, Pz * vx + Qz * vy);
        }

        public Vec3 PositionAt(double jdTT)
        {
            Vec3 p, v;
            StateAt(jdTT, out p, out v);
            return p;
        }

        /// <summary>Build elements from a state vector (ecliptic J2000, AU and AU/day).</summary>
        public static OrbitalElements FromState(Vec3 r, Vec3 v, double epoch, double mu = AstroMath.GM_Sun)
        {
            var h = Vec3.Cross(r, v);
            double rm = r.Length, v2 = v.LengthSquared;
            var evec = Vec3.Cross(v, h) / mu - r / rm;
            double e = evec.Length;
            double inc = Math.Acos(AstroMath.Clamp(h.Z / h.Length, -1, 1));
            var nvec = new Vec3(-h.Y, h.X, 0);
            double node = nvec.Length > 1e-14 ? Math.Atan2(nvec.Y, nvec.X) : 0;
            double argp;
            if (nvec.Length > 1e-14 && e > 1e-12)
            {
                argp = Math.Acos(AstroMath.Clamp(Vec3.Dot(nvec, evec) / (nvec.Length * e), -1, 1));
                if (evec.Z < 0) argp = AstroMath.TwoPi - argp;
            }
            else argp = e > 1e-12 ? Math.Atan2(evec.Y, evec.X) : 0;
            double p = h.LengthSquared / mu;
            double q = p / (1 + e);
            // true anomaly
            double nu = e > 1e-12 ? Math.Atan2(Vec3.Dot(Vec3.Cross(evec, r), h) / h.Length, Vec3.Dot(evec, r)) : 0;
            double tp;
            if (e < 1)
            {
                double a = 1 / (2 / rm - v2 / mu);
                double E = 2 * Math.Atan(Math.Sqrt((1 - e) / (1 + e)) * Math.Tan(nu / 2));
                double M = E - e * Math.Sin(E);
                tp = epoch - M / Math.Sqrt(mu / (a * a * a));
            }
            else if (e > 1)
            {
                double a = 1 / (2 / rm - v2 / mu);
                double H = 2 * Atanh(Math.Sqrt((e - 1) / (e + 1)) * Math.Tan(nu / 2));
                double M = e * Math.Sinh(H) - H;
                tp = epoch - M / Math.Sqrt(mu / Math.Pow(-a, 3));
            }
            else
            {
                double D = Math.Tan(nu / 2);
                double M = q * D + D * D * D / 3 * q;
                tp = epoch - Math.Sqrt(2 * q * q * q / mu) * (D + D * D * D / 3);
            }
            return new OrbitalElements
            {
                Epoch = epoch, Eccentricity = e, PerihelionDistance = q, Inclination = inc,
                Node = AstroMath.NormalizeRad(node), ArgPerihelion = AstroMath.NormalizeRad(argp), PerihelionTime = tp, Mu = mu
            };
        }

        private static double Atanh(double x) => 0.5 * Math.Log((1 + x) / (1 - x));
    }

    /// <summary>Kepler equation solvers and universal-variable two-body propagation.</summary>
    public static class Kepler
    {
        /// <summary>Solve M = E - e sin E (elliptic) robustly for all e &lt; 1.</summary>
        public static double SolveElliptic(double M, double e)
        {
            M = AstroMath.NormalizeRadPi(M);
            double E = e < 0.8 ? M : Math.PI * Math.Sign(M);
            if (M == 0) return 0;
            for (int i = 0; i < 60; i++)
            {
                double f = E - e * Math.Sin(E) - M;
                double fp = 1 - e * Math.Cos(E);
                double fpp = e * Math.Sin(E);
                // Halley step
                double d = -f / (fp - 0.5 * f * fpp / fp);
                E += d;
                if (Math.Abs(d) < 1e-14) break;
            }
            return E;
        }

        /// <summary>Stumpff functions c2, c3.</summary>
        public static void Stumpff(double z, out double c2, out double c3)
        {
            if (z > 1e-6)
            {
                double s = Math.Sqrt(z);
                c2 = (1 - Math.Cos(s)) / z;
                c3 = (s - Math.Sin(s)) / (s * s * s);
            }
            else if (z < -1e-6)
            {
                double s = Math.Sqrt(-z);
                c2 = (1 - Math.Cosh(s)) / z;
                c3 = (Math.Sinh(s) - s) / Math.Pow(-z, 1.5);
            }
            else
            {
                c2 = 0.5 - z / 24 + z * z / 720;
                c3 = 1.0 / 6 - z / 120 + z * z / 5040;
            }
        }

        /// <summary>
        /// Perifocal position/velocity at time dt (days) after perihelion, for perihelion distance q and eccentricity e.
        /// Uses universal variables so it is valid for any e.
        /// </summary>
        public static void PerifocalState(double q, double e, double mu, double dt, out double x, out double y, out double vx, out double vy)
        {
            if (e < 0.98)
            {
                // fast classical path
                double a = q / (1 - e);
                double n = Math.Sqrt(mu / (a * a * a));
                double E = SolveElliptic(n * dt, e);
                double cE = Math.Cos(E), sE = Math.Sin(E);
                double b = a * Math.Sqrt(1 - e * e);
                x = a * (cE - e);
                y = b * sE;
                double r = a * (1 - e * cE);
                double Edot = n * a / r;
                vx = -a * sE * Edot;
                vy = b * cE * Edot;
                return;
            }
            // Universal variable: initial state at perihelion r0 = (q,0), v0 = (0, vp)
            double vp = Math.Sqrt(mu * (1 + e) / q);
            double r0 = q;
            double alpha = 2 / r0 - vp * vp / mu; // 1/a
            double sqmu = Math.Sqrt(mu);
            double c2, c3, rr;
            // t(chi) is odd and strictly increasing, so solve for |dt| with a bracketed safeguarded Newton.
            double adt = Math.Abs(dt);
            Func<double, double> tOf = c =>
            {
                double ps = c * c * alpha, a2, a3;
                Stumpff(ps, out a2, out a3);
                return (c * c * c * a3 + r0 * c * (1 - ps * a3)) / sqmu;
            };
            double lo = 0, hi = Math.Max(1e-6, sqmu * adt / r0);
            int guard = 0;
            while (tOf(hi) < adt && guard++ < 200) { lo = hi; hi *= 2; }
            double chi = 0.5 * (lo + hi);
            for (int i = 0; i < 200 && adt > 0; i++)
            {
                double psi = chi * chi * alpha;
                Stumpff(psi, out c2, out c3);
                rr = chi * chi * c2 + r0 * (1 - psi * c2);
                double tn = (chi * chi * chi * c3 + r0 * chi * (1 - psi * c3)) / sqmu;
                if (tn < adt) lo = chi; else hi = chi;
                double next = chi + (adt - tn) * sqmu / rr;
                if (next <= lo || next >= hi || double.IsNaN(next)) next = 0.5 * (lo + hi);
                if (Math.Abs(next - chi) < 1e-13 * Math.Max(1, Math.Abs(chi))) { chi = next; break; }
                chi = next;
            }
            if (adt == 0) chi = 0;
            if (dt < 0) chi = -chi;
            {
                double psi = chi * chi * alpha;
                Stumpff(psi, out c2, out c3);
                double f = 1 - chi * chi / r0 * c2;
                double g = dt - chi * chi * chi / sqmu * c3;
                rr = chi * chi * c2 + r0 * (1 - psi * c2);
                double fd = sqmu / (rr * r0) * chi * (psi * c3 - 1);
                double gd = 1 - chi * chi / rr * c2;
                x = f * r0; y = g * vp;
                vx = fd * r0; vy = gd * vp;
            }
        }
    }
}
