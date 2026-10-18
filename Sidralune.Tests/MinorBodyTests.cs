using System;
using Sidralune.Core;
using Sidralune.Core.Coordinates;
using Sidralune.Core.Ephemeris;
using Sidralune.Core.Mathematics;
using Sidralune.Core.MinorBodies;
using Sidralune.Core.Observing;
using Sidralune.Core.Satellites;
using Sidralune.Core.Time;
using static Sidralune.Tests.Program;

namespace Sidralune.Tests
{
    public static class MinorBodyTests
    {
        private static double VestaSep(double jd, double raDeg, double decDeg, bool perturbed, out double mag)
        {
            var vesta = MinorBodyStore.BuiltInAsteroids()[3];
            if (perturbed) vesta.Elements = PerturbedPropagator.ToEpoch(vesta.Elements, jd);
            var e = EphemerisManager.Position(Body.Earth, jd);
            Vec3 d = Vec3.Zero; double tau = 0;
            for (int i = 0; i < 4; i++) { d = vesta.PositionForEphemeris(jd - tau) - e; tau = d.Length * AstroMath.LightTimeAU_days; }
            var eq = Equatorial.FromVector(d);
            var helio = vesta.HeliocentricEquatorial(jd - tau);
            double phase = Vec3.AngleBetween(-helio, -d);
            mag = vesta.Magnitude(helio.Length, d.Length, phase);
            return AstroMath.Separation(eq.Ra, eq.Dec, raDeg * AstroMath.Deg2Rad, decDeg * AstroMath.Deg2Rad) * AstroMath.Rad2ArcSec;
        }

        public static void TestVestaVsHorizons()
        {
            double mag;
            double s1 = VestaSep(2461331.5, 21.39126, -3.73930, false, out mag);
            Info(string.Format("Vesta 2026-10-18 two-body: {0:0.0}\"  V={1:0.00} (Horizons 6.43)", s1, mag));
            CheckTrue("Vesta near epoch < 60\"", s1 < 60);
            Check("Vesta magnitude", mag, 6.428, 0.15);
            double s2 = VestaSep(2462500.5, 326.15088, -18.54960, false, out mag);
            double s3 = VestaSep(2462500.5, 326.15088, -18.54960, true, out mag);
            Info(string.Format("Vesta 2029-12-30 two-body {0:0.0}\", perturbed {1:0.0}\"", s2, s3));
            CheckTrue("Perturbed propagation improves 3-yr prediction", s3 < s2 && s3 < 120);
        }

        public static void TestMpcPacked()
        {
            Check("Packed epoch K24AH", MpcFormats.UnpackEpoch("K24AH"), AstroTime.CalendarToJd(2024, 10, 17), 1e-9);
            CheckTrue("Packed number A0001", MpcFormats.UnpackDesignation("A0001") == "100001", MpcFormats.UnpackDesignation("A0001"));
            CheckTrue("Packed provisional K24A01B", MpcFormats.UnpackDesignation("K24A01B") == "2024 AB1", MpcFormats.UnpackDesignation("K24A01B"));
            CheckTrue("Pack 100001", MpcFormats.PackDesignationNumber(100001) == "A0001");
        }

        public static void TestSgp4Vallado()
        {
            // Vallado et al. 2006 verification case 00005
            var tle = Tle.Parse("TEST",
                "1 00005U 58002B   00179.78495062  .00000023  00000-0  28098-4 0  4753",
                "2 00005  34.2682 348.7242 1859667 331.7664  19.3264 10.82419157413667");
            var sgp = new Sgp4(tle);
            Vec3 p, v;
            sgp.Propagate(0, out p, out v);
            Check("SGP4 t=0 x", p.X, 7022.46529266, 1e-3);
            Check("SGP4 t=0 y", p.Y, -1400.08296755, 1e-3);
            Check("SGP4 t=0 vx", v.X, 1.893841015, 1e-6);
            sgp.Propagate(360, out p, out v);
            Check("SGP4 t=360 x", p.X, -7154.03120202, 1e-2);
            Check("SGP4 t=360 y", p.Y, -3783.17682504, 1e-2);
            Check("SGP4 t=360 z", p.Z, -3536.19412294, 1e-2);
        }

        public static void TestVisibilityModel()
        {
            double nelm = VisibilityModel.NakedEyeLimit(21.8);
            Info("NELM at 21.8 mag/arcsec² = " + nelm.ToString("0.00"));
            CheckTrue("Dark-sky NELM between 6.2 and 7.2", nelm > 6.2 && nelm < 7.2);
            CheckTrue("Bright sky NELM < 5", VisibilityModel.NakedEyeLimit(18.0) < 5.0);
            var obs = new Observer { SqmZenith = 21.5 };
            var ins = new VisualInstrument();
            var eye = new ObserverEye();
            var m13 = VisibilityModel.Predict(5.8, 16, 60 * AstroMath.Deg2Rad, 21.5, obs, ins, eye);
            Info("M13 in 8\": " + m13.Summary + ", best ×" + m13.BestMagnification.ToString("0"));
            CheckTrue("M13 obvious in 8\" under dark sky", m13.Rating >= Detectability.Easy);
            var faint = VisibilityModel.Predict(14.5, 3, 60 * AstroMath.Deg2Rad, 19.0, obs, ins, eye);
            CheckTrue("Mag 14.5 galaxy invisible under suburban sky", faint.Rating <= Detectability.Difficult, faint.Summary);
            double sky = VisibilityModel.SkyBrightness(obs, 45 * AstroMath.Deg2Rad, -30, 40 * AstroMath.Deg2Rad, 10, 30 * AstroMath.Deg2Rad);
            Info("Sky with near-full moon 30° away: " + sky.ToString("0.00"));
            CheckTrue("Full moon brightens sky by > 2 mag", sky < 19.5);
        }
    }
}
