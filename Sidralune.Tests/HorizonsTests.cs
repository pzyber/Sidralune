using System;
using Sidralune.Core.Coordinates;
using Sidralune.Core.Ephemeris;
using Sidralune.Core.Mathematics;
using Sidralune.Core.Time;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Sidralune.Tests.Checks;

namespace Sidralune.Tests
{
    /// <summary>Analytic ephemeris vs JPL Horizons astrometric geocentric RA/Dec (0h TT).</summary>
    [TestClass]
    public class HorizonsTests : TestBase
    {
        // jd, ra, dec
        private static readonly double[,] Saturn = {
            {2447892.5,287.03993,-22.22028},{2451696.5,51.27283,16.62827},{2455256.5,183.50621,1.24099},{2458362.5,272.50102,-22.69987},
            {2461331.5,10.13988,1.42016},{2464448.5,121.05937,20.87328},{2467981.5,244.93650,-19.64623}};
        private static readonly double[,] Jupiter = {
            {2447892.5,95.81940,23.21442},{2451696.5,51.33508,17.83080},{2455256.5,341.63463,-8.78699},{2458362.5,224.69734,-16.06955},
            {2461331.5,144.61571,14.77290},{2464448.5,28.09387,10.43002},{2467981.5,312.45086,-18.40040}};

        private static double SepArcsec(Body b, double jd, double raDeg, double decDeg)
        {
            var e = EphemerisManager.Position(Body.Earth, jd);
            Vec3 d = Vec3.Zero; double tau = 0;
            for (int i = 0; i < 4; i++) { d = EphemerisManager.Position(b, jd - tau) - e; tau = d.Length * AstroMath.LightTimeAU_days; }
            var eq = Equatorial.FromVector(d);
            return AstroMath.Separation(eq.Ra, eq.Dec, raDeg * AstroMath.Deg2Rad, decDeg * AstroMath.Deg2Rad) * AstroMath.Rad2ArcSec;
        }

        private static double Rms(Body b, double[,] t)
        {
            double s = 0; int n = t.GetLength(0);
            for (int i = 0; i < n; i++) { double x = SepArcsec(b, t[i, 0], t[i, 1], t[i, 2]); s += x * x; }
            return Math.Sqrt(s / n);
        }

        [TestMethod]
        public void TestAnalyticVsHorizons()
        {
            double j = Rms(Body.Jupiter, Jupiter), sa = Rms(Body.Saturn, Saturn);
            Info(string.Format("Jupiter rms {0:0.0}\"  Saturn rms {1:0.0}\"", j, sa));
            CheckTrue("Jupiter rms < 150\"", j < 150);
            CheckTrue("Saturn rms < 330\"", sa < 330);
        }
    }
}
