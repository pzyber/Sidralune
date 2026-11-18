using System;
using System.Linq;
using Sidralune.Core;
using Sidralune.Core.Ephemeris;
using Sidralune.Core.Mathematics;

namespace Sidralune.Science.SolarSystem
{
    /// <summary>
    /// Saturn as seen in a telescope: how far its rings are tilted towards the observer and which way, and where its
    /// brighter moons are. Drawn on the Tonight tab.
    /// </summary>
    public class SaturnView
    {
        /// <summary>The rings' outer edge (the A ring's) and the B ring's inner edge, in Saturn's equatorial radii.</summary>
        public const double RingOuter = 136775.0 / 60268.0, RingInner = 92000.0 / 60268.0;
        /// <summary>Saturn's polar radius over its equatorial radius.</summary>
        public const double Flattening = 54364.0 / 60268.0;
        /// <summary>Faintest moon shown: Mimas (about 13) and brighter, not Hyperion (about 14).</summary>
        public const double MoonMagLimit = 13.5;

        /// <summary>
        /// Sub-observer latitude on the ring plane (deg): positive when the rings' north face is towards the observer.
        /// </summary>
        public double TiltDeg;
        /// <summary>Position angle of Saturn's north pole (deg, from north through east).</summary>
        public double PoleAngleDeg;
        /// <summary>Saturn's equatorial radius on the sky (arcsec).</summary>
        public double RadiusArcsec;
        /// <summary>The rings are opening: tilted more a year from now (a year apart, so the Earth's own yearly wobble cancels).</summary>
        public bool Opening;
        /// <summary>The classical moons bright enough to see in a small telescope, nearest Saturn first.</summary>
        public MoonPlace[] Moons;

        /// <summary>Almost edge-on: the rings are a line or invisible.</summary>
        public bool EdgeOn => Math.Abs(TiltDeg) < 0.5;
        public bool NorthFace => TiltDeg > 0;

        public static SaturnView Compute(SkyContext ctx)
        {
            var v = new SaturnView();
            var p = ctx.BodyPlace(Body.Saturn);
            double u;
            PlanetPhysics.SaturnRings(ctx.JdTT, p, out v.TiltDeg, out v.PoleAngleDeg, out u);
            v.RadiusArcsec = PlanetPhysics.EquatorialRadiusKm(Body.Saturn) / (p.Distance * AstroMath.AU_km) * AstroMath.Rad2ArcSec;
            var later = new SkyContext(ctx.Observer, ctx.Time.AddDays(365.25));
            double b2, p2;
            PlanetPhysics.SaturnRings(later.JdTT, later.BodyPlace(Body.Saturn), out b2, out p2, out u);
            v.Opening = Math.Abs(b2) > Math.Abs(v.TiltDeg);
            v.Moons = PlanetMoons.Places(ctx, Body.Saturn).Where(m => m.Moon.Major && m.Mag <= MoonMagLimit).OrderBy(m => m.Moon.SemiMajorAxisKm).ToArray();
            return v;
        }
    }
}
