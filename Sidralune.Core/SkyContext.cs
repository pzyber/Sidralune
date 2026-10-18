using System;
using Sidralune.Core.Coordinates;
using Sidralune.Core.Ephemeris;
using Sidralune.Core.Mathematics;
using Sidralune.Core.Time;

namespace Sidralune.Core
{
    /// <summary>
    /// Precomputed reduction frame for a given observer and instant: precession-nutation, sidereal time,
    /// Earth velocity (aberration) and observer geocentric offset. Converting thousands of stars per frame
    /// only needs a few matrix-vector products.
    /// </summary>
    public class SkyContext
    {
        public readonly Observer Observer;
        public readonly AstroTime Time;
        public readonly double T;
        public readonly double JdTT, JdTDB;
        /// <summary>GCRS to true equator/equinox of date.</summary>
        public readonly Mat3 Npb;
        public readonly Mat3 NpbT;
        /// <summary>Local apparent sidereal time, radians.</summary>
        public readonly double Last;
        /// <summary>Earth (geocentre) position wrt ephemeris origin, AU.</summary>
        public readonly Vec3 EarthPos;
        /// <summary>Observer velocity wrt SSB in units of c (for aberration).</summary>
        public readonly Vec3 Beta;
        /// <summary>Observer position wrt geocentre in GCRS, AU.</summary>
        public readonly Vec3 ObserverGcrs;
        public readonly Vec3 SunPos;
        /// <summary>Mean obliquity of date.</summary>
        public readonly double Obliquity;

        /// <summary>Local horizon basis vectors in true-of-date equatorial frame.</summary>
        private readonly double _sinLat, _cosLat;
        public bool ApplyRefraction = true;
        public bool ApplyAberration = true;

        public SkyContext(Observer obs, AstroTime time)
        {
            Observer = obs;
            Time = time;
            JdTT = time.JdTT;
            JdTDB = time.JdTDB;
            T = (JdTT - AstroTime.J2000) / AstroTime.JulianCentury;
            Npb = Precession.NpbMatrix(T);
            NpbT = Npb.Transpose();
            Obliquity = Precession.MeanObliquity(T);
            double gast = time.Gast;
            Last = AstroMath.NormalizeRad(gast + obs.LonRad);
            _sinLat = Math.Sin(obs.LatRad);
            _cosLat = Math.Cos(obs.LatRad);

            Vec3 ep, ev;
            EphemerisManager.State(Body.Earth, JdTDB, out ep, out ev);
            EarthPos = ep;
            SunPos = EphemerisManager.Position(Body.Sun, JdTDB);

            // observer offset: ITRS -> true of date by rotating with GAST, then to GCRS
            var itrs = obs.GeocentricItrs();
            var tod = Mat3.RotZ(-gast) * itrs;
            ObserverGcrs = (NpbT * tod) / AstroMath.AU_km;
            // diurnal velocity (km/s) in TOD
            double omega = 7.2921150e-5;
            var vtod = new Vec3(-omega * tod.Y, omega * tod.X, 0);
            var vg = NpbT * vtod; // km/s
            var vEarth = ev * (AstroMath.AU_km / 86400.0); // km/s
            Beta = (vEarth + vg) / AstroMath.C_kms;
        }

        /// <summary>Apply stellar aberration to a GCRS unit direction.</summary>
        public Vec3 Aberrate(Vec3 u)
        {
            if (!ApplyAberration) return u;
            double ub = Vec3.Dot(u, Beta);
            double binv = Math.Sqrt(1 - Beta.LengthSquared);
            var r = (u * binv + Beta * (1 + ub / (1 + binv))) / (1 + ub);
            return r.Normalized();
        }

        /// <summary>Remove aberration (inverse of Aberrate) by fixed-point iteration.</summary>
        public Vec3 Unaberrate(Vec3 apparent)
        {
            if (!ApplyAberration) return apparent;
            var u = apparent;
            for (int i = 0; i < 3; i++)
            {
                var a = Aberrate(u);
                u = (u + (apparent - a)).Normalized();
            }
            return u;
        }

        /// <summary>J2000/ICRS catalog direction to apparent true-of-date equatorial direction.</summary>
        public Vec3 IcrsToApparentTod(Vec3 icrsUnit) => Npb * Aberrate(icrsUnit);

        /// <summary>True-of-date equatorial unit vector to horizontal (with optional refraction).</summary>
        public Horizontal TodToHorizontal(Vec3 tod)
        {
            double ra, dec;
            tod.ToSpherical(out ra, out dec);
            var h = Transforms.HaDecToAltAz(Last - ra, dec, Observer.LatRad);
            if (ApplyRefraction) h.Alt += Transforms.RefractionFromTrue(h.Alt, Observer.PressureHpa, Observer.TemperatureC);
            return h;
        }

        /// <summary>Catalog (J2000/ICRS) RA/Dec in radians to observed horizontal coordinates.</summary>
        public Horizontal IcrsToHorizontal(double ra, double dec) => TodToHorizontal(IcrsToApparentTod(Vec3.FromSpherical(ra, dec)));

        /// <summary>Catalog position to apparent RA/Dec of date.</summary>
        public Equatorial IcrsToApparent(double ra, double dec) => Equatorial.FromVector(IcrsToApparentTod(Vec3.FromSpherical(ra, dec)));

        /// <summary>Observed horizontal (refracted) to ICRS RA/Dec — for picking objects on a planetarium display.</summary>
        public Equatorial HorizontalToIcrs(double az, double alt)
        {
            if (ApplyRefraction) alt -= Transforms.RefractionFromApparent(alt, Observer.PressureHpa, Observer.TemperatureC);
            double ha, dec;
            Transforms.AltAzToHaDec(az, alt, Observer.LatRad, out ha, out dec);
            var tod = Vec3.FromSpherical(Last - ha, dec);
            var gcrs = Unaberrate(NpbT * tod);
            return Equatorial.FromVector(gcrs);
        }

        /// <summary>Apparent of date to ICRS.</summary>
        public Equatorial ApparentToIcrs(double ra, double dec) => Equatorial.FromVector(Unaberrate(NpbT * Vec3.FromSpherical(ra, dec)));

        /// <summary>Hour angle (radians) of an apparent RA.</summary>
        public double HourAngle(double raApparent) => AstroMath.NormalizeRadPi(Last - raApparent);

        public double LastHours => Last * AstroMath.Rad2Hour;

        /// <summary>
        /// Topocentric astrometric vector (AU, GCRS axes) to an object whose position at time jd is given by posFn
        /// (same origin as EarthPos), iterating for light time.
        /// </summary>
        public Vec3 TopocentricWithLightTime(Func<double, Vec3> posFn, out double lightTimeDays)
        {
            var obsPos = EarthPos + ObserverGcrs;
            double tau = 0;
            Vec3 d = Vec3.Zero;
            for (int i = 0; i < 4; i++)
            {
                var p = posFn(JdTDB - tau);
                d = p - obsPos;
                tau = d.Length * AstroMath.LightTimeAU_days;
            }
            lightTimeDays = tau;
            return d;
        }

        /// <summary>Apparent topocentric place of a solar-system body.</summary>
        public BodyPlace BodyPlace(Body body)
        {
            double lt;
            var d = TopocentricWithLightTime(jd => EphemerisManager.Position(body, jd), out lt);
            return MakePlace(d, lt, jd => EphemerisManager.Position(body, jd));
        }

        /// <summary>Build a place from a topocentric astrometric vector and a function returning the body's position.</summary>
        public BodyPlace MakePlace(Vec3 topo, double lightTime, Func<double, Vec3> posFn)
        {
            var place = new BodyPlace();
            place.Distance = topo.Length;
            place.LightTimeDays = lightTime;
            var u = topo.Normalized();
            place.Astrometric = Equatorial.FromVector(u);
            var tod = IcrsToApparentTod(u);
            place.Apparent = Equatorial.FromVector(tod);
            place.Horizontal = TodToHorizontal(tod);
            var bodyHelio = posFn(JdTDB - lightTime) - EphemerisManager.Position(Body.Sun, JdTDB - lightTime);
            place.HelioDistance = bodyHelio.Length;
            var earthHelio = EarthPos + ObserverGcrs - SunPos;
            // phase angle: Sun-body-observer
            place.PhaseAngle = Vec3.AngleBetween(-bodyHelio, -topo);
            place.Elongation = Vec3.AngleBetween(-earthHelio, topo);
            place.HelioEcliptic = Transforms.EquatorialToEcliptic(bodyHelio, Precession.ObliquityJ2000);
            return place;
        }
    }

    /// <summary>Computed place of a solar-system object for display and planning.</summary>
    public class BodyPlace
    {
        public Equatorial Astrometric;
        public Equatorial Apparent;
        public Horizontal Horizontal;
        public double Distance;
        public double HelioDistance;
        public double LightTimeDays;
        /// <summary>Phase angle (Sun-object-observer), radians.</summary>
        public double PhaseAngle;
        /// <summary>Solar elongation, radians.</summary>
        public double Elongation;
        public Vec3 HelioEcliptic;
        public double Magnitude = double.NaN;
        /// <summary>Apparent equatorial diameter, arcsec.</summary>
        public double DiameterArcsec = double.NaN;
        public double IlluminatedFraction => (1 + Math.Cos(PhaseAngle)) / 2;
    }
}
