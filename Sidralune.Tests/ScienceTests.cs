using System;
using System.Collections.Generic;
using System.Linq;
using Sidralune.Core.Mathematics;
using Sidralune.Science.Photometry;
using Sidralune.Science.Radio;
using Sidralune.Science.SolarSystem;
using Sidralune.Science.Spectroscopy;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Sidralune.Tests.Checks;

namespace Sidralune.Tests
{
    [TestClass]
    public class ScienceTests : TestBase
    {
        [TestMethod]
        public void TestGalileanMoons()
        {
            // Meeus example 44.a, 1992 Dec 16 0h UT (JDE 2448972.50068)
            var m = GalileanMoons.Positions(2448972.50068);
            Check("Io X", m[0].X, -3.44, 0.02); Check("Io Y", m[0].Y, 0.21, 0.02);
            Check("Europa X", m[1].X, 7.44, 0.02); Check("Europa Y", m[1].Y, 0.25, 0.02);
            Check("Ganymede X", m[2].X, 1.24, 0.02); Check("Ganymede Y", m[2].Y, 0.65, 0.02);
            Check("Callisto X", m[3].X, 7.08, 0.02); Check("Callisto Y", m[3].Y, 1.10, 0.02);
        }

        [TestMethod]
        public void TestGalileanMoonsInTheSky()
        {
            var ctx = new Core.SkyContext(new Core.Observer { LatitudeDeg = 59.3, LongitudeDeg = 18.1 }, Core.Time.AstroTime.FromDateTime(new DateTime(2026, 10, 31, 1, 0, 0, DateTimeKind.Utc)));
            var jup = ctx.BodyPlace(Core.Ephemeris.Body.Jupiter);
            Core.Ephemeris.PlanetPhysics.Fill(Core.Ephemeris.Body.Jupiter, jup, ctx.JdTT);
            double cm, lat, pole;
            Core.Ephemeris.PlanetPhysics.Orientation(Core.Ephemeris.Body.Jupiter, ctx.JdTDB, jup.Astrometric.ToVector(), jup.LightTimeDays, out cm, out lat, out pole);
            CheckTrue("Jupiter's axis is within 35° of celestial north", Math.Cos(pole * AstroMath.Deg2Rad) > Math.Cos(35 * AstroMath.Deg2Rad), pole + "°");
            var moons = GalileanMoons.Positions(ctx);
            var plain = GalileanMoons.Positions(ctx.JdTT);
            for (int i = 0; i < 4; i++)
            {
                var m = moons[i];
                string n = m.Name + ": ";
                Check(n + "same place around Jupiter as without the sky (X)", m.X, plain[i].X, 1e-12);
                double sep = AstroMath.Separation(jup.Astrometric.Ra, jup.Astrometric.Dec, m.Ra, m.Dec) * AstroMath.Rad2Deg * 3600;
                Check(n + "RA/Dec are the stated distance from Jupiter (\")", sep, m.SeparationArcsec, 0.05);
                Check(n + "that distance is X, Y in Jupiter radii (\")", m.SeparationArcsec, Math.Sqrt(m.X * m.X + m.Y * m.Y) * jup.DiameterArcsec / 2, 0.01);
                if (Math.Abs(m.X) > 3)
                {
                    // the moons lie along Jupiter's equator, 90° from its pole: west of it for positive X
                    double pa = AstroMath.PositionAngle(jup.Astrometric.Ra, jup.Astrometric.Dec, m.Ra, m.Dec) * AstroMath.Rad2Deg;
                    double expected = pole + (m.X > 0 ? -90 : 90), off = Math.Abs(AstroMath.NormalizeDeg(pa - expected + 180) - 180);
                    CheckTrue(n + "along Jupiter's equator, on the " + (m.X > 0 ? "west" : "east") + " side", off < 6, pa.ToString("0.0") + "° vs " + expected.ToString("0.0") + "°");
                    CheckTrue(n + "east and north parts agree with RA/Dec", Math.Abs(AstroMath.NormalizeDeg(Math.Atan2(m.EastArcsec, m.NorthArcsec) * AstroMath.Rad2Deg - pa + 180) - 180) < 0.1);
                }
                // 4.6 to 5.7 at opposition; Jupiter is 5.5 AU away on this date
                CheckTrue(n + "magnitude 4.3 to 6.6", m.Mag > 4.3 && m.Mag < 6.6, m.Mag.ToString("0.00"));
            }
            CheckTrue("Ganymede is the brightest and largest, Callisto the faintest", moons[2].Mag < moons.Where((x, i) => i != 2).Min(x => x.Mag) && moons[3].Mag > moons.Take(3).Max(x => x.Mag)
                && moons[2].DiameterArcsec > 1.2 && moons[2].DiameterArcsec < 1.9, string.Join(", ", moons.Select(x => x.Name + " " + x.Mag.ToString("0.00") + " " + x.DiameterArcsec.ToString("0.00") + "\"")));
        }

        [TestMethod]
        public void TestPeriodograms()
        {
            var rng = new Random(2);
            var t = new List<double>(); var y = new List<double>(); var e = new List<double>();
            for (int i = 0; i < 300; i++)
            {
                double ti = rng.NextDouble() * 40;
                t.Add(ti); y.Add(0.3 * Math.Sin(2 * Math.PI * ti / 2.7134) + 0.05 * (rng.NextDouble() - 0.5)); e.Add(0.02);
            }
            var ls = PeriodSearch.LombScargle(t, y, e, 0.5, 10);
            Check("Lomb-Scargle period", ls.BestPeriod, 2.7134, 0.005);
            var pdm = PeriodSearch.Pdm(t, y, 0.5, 10);
            Check("PDM period", pdm.BestPeriod, 2.7134, 0.01);
            // transit search
            var model = new TransitModel { T0 = 1.3, Period = 3.524, Rp = 0.12, A = 9, Inclination = 88.5, U1 = 0.4, U2 = 0.2 };
            var tt = new List<double>(); var ff = new List<double>();
            for (double ti = 0; ti < 30; ti += 0.01) { tt.Add(ti); ff.Add(model.Flux(ti) + 0.001 * (rng.NextDouble() - 0.5)); }
            var bls = PeriodSearch.Bls(tt, ff, 1, 8);
            Check("BLS period", bls.BestPeriod, 3.524, 0.01);
            Info(string.Format("transit depth {0:0.00000}, duration {1:0.000} d", model.Depth, model.Duration));
            Check("Depth ≈ p² (limb darkened slightly deeper)", model.Depth, 0.0144 * 1.15, 0.004);
        }

        [TestMethod]
        public void TestTransitFit()
        {
            var truth = new TransitModel { T0 = 0.0, Period = 2.2, Rp = 0.1, A = 8, Inclination = 87.5, U1 = 0.45, U2 = 0.2 };
            var rng = new Random(4);
            var t = new List<double>(); var f = new List<double>(); var e = new List<double>();
            for (double ti = -0.12; ti <= 0.12; ti += 0.0015) { t.Add(ti); f.Add(truth.Flux(ti) + 0.0008 * Math.Sqrt(-2 * Math.Log(1 - rng.NextDouble())) * Math.Cos(2 * Math.PI * rng.NextDouble())); e.Add(0.0008); }
            var fit = new TransitModel { T0 = 0.003, Period = 2.2, Rp = 0.08, A = 7, Inclination = 88.5, U1 = 0.45, U2 = 0.2 };
            var r = fit.Fit(t, f, e);
            Info(string.Format("fit: T0 {0:0.0000} Rp {1:0.0000} a {2:0.00} i {3:0.00} chi2r {4:0.00}", fit.T0, fit.Rp, fit.A, fit.Inclination, r.ReducedChiSquare));
            Check("Transit T0", fit.T0, 0.0, 0.0008);
            Check("Transit Rp/Rs", fit.Rp, 0.1, 0.004);
        }

        [TestMethod]
        public void TestKweeVanWoerden()
        {
            var t = new List<double>(); var m = new List<double>();
            for (double ti = 0.4; ti <= 0.6; ti += 0.002) { t.Add(ti); m.Add(10 + 0.8 * Math.Exp(-Math.Pow((ti - 0.5123) / 0.02, 2))); }
            double err;
            double tmin = PeriodSearch.KweeVanWoerden(t, m, out err);
            Check("KvW time of minimum", tmin, 0.5123, 0.0005);
        }

        [TestMethod]
        public void TestSpectroscopy()
        {
            // synthetic neon lamp with dispersion 2.1 Å/px starting at 5800 Å plus slight curvature
            int n = 1200;
            Func<double, double> disp = p => 5800 + 2.1 * p + 2e-5 * p * p;
            var lamp = new double[n];
            var lines = LineLists.Lamps["Neon"];
            for (int i = 0; i < n; i++)
            {
                double wl = disp(i);
                foreach (var l in lines) lamp[i] += 1000 * Math.Exp(-0.5 * Math.Pow((wl - l) / 2.5, 2));
                lamp[i] += 10;
            }
            double rms; List<double[]> ident;
            var c = SpectroTools.AutoCalibrate(lamp, lines, 5790, 2.12, 2, out rms, out ident);
            Info(string.Format("autocal: {0} lines identified, rms {1:0.000} Å", ident.Count, rms));
            CheckTrue("Auto calibration identifies lines", ident.Count >= 12);
            Check("Dispersion at pixel 900", LinAlg.PolyEval(c, 900), disp(900), 0.3);
            // radial velocity of Hα absorption shifted by +30 km/s
            var s = new Spectrum(2000);
            for (int i = 0; i < 2000; i++) { s.Wavelength[i] = 6400 + 0.2 * i; s.Flux[i] = 1 - 0.6 * Math.Exp(-0.5 * Math.Pow((s.Wavelength[i] - 6562.81 * (1 + 30 / 299792.458)) / 1.5, 2)); }
            var mline = SpectroTools.MeasureLine(s, 6540, 6590, 6562.81, "Hα");
            Check("Hα RV (km/s)", mline.RadialVelocity, 30, 1.0);
            Check("Hα EW (Å)", mline.EquivalentWidth, 0.6 * 1.5 * Math.Sqrt(2 * Math.PI), 0.1);
            double scale;
            var bb = new Spectrum(400);
            for (int i = 0; i < 400; i++) { bb.Wavelength[i] = 3800 + 10 * i; bb.Flux[i] = 3.0 * SpectroTools.Planck(bb.Wavelength[i], 9500); }
            Check("Blackbody temperature", SpectroTools.BlackbodyTemperature(bb, out scale), 9500, 200);
        }

        [TestMethod]
        public void TestRadio()
        {
            Check("HPBW 3 m dish at 1420 MHz (deg)", RadioMath.BeamWidthDeg(1420.405751768e6, 3.0), 4.93, 0.05);
            var d = VelocityFrames.KinematicDistances(30, VelocityFrames.ModelVlsr(30, 0, 4.0));
            CheckTrue("Kinematic distance recovers near solution", d.Length > 0 && Math.Abs(d[0] - 4.0) < 0.01, string.Join(", ", d.Select(x => x.ToString("0.000"))));
            Check("Radio velocity", RadioMath.RadioVelocity(RadioMath.FrequencyForVelocity(-50, 1420.405751768e6), 1420.405751768e6), -50, 1e-9);
            Check("Dispersion delay (DM 10, 400->800 MHz)", TimeDomainRadio.DispersionDelay(10, 400, 800), 0.1944, 0.001);
        }

        [TestMethod]
        public void TestMpcFormat()
        {
            var o = new AstrometricObservation { PackedNumber = "00433", TimeUtc = new DateTime(2025, 3, 10, 21, 34, 56, DateTimeKind.Utc), RaDeg = 123.456789, DecDeg = -12.345678, Mag = 14.2, Band = "G", Station = "Z80" };
            string line = MpcReporting.Obs80(o);
            Info("[" + line + "]");
            CheckTrue("Obs80 length", line.Length == 80);
            CheckTrue("Obs80 station in cols 78-80", line.Substring(77, 3) == "Z80");
            CheckTrue("Obs80 date", line.Substring(15, 10) == "2025 03 10");
            Check("Diameter H=10 p=0.15", MpcReporting.DiameterKm(10, 0.15), 34.3, 0.2);
        }
    }
}
