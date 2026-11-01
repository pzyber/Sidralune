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
        public void TestPlanetMoonsList()
        {
            // the moons of each planet (and Pluto)
            var expected = new Dictionary<Core.Ephemeris.Body, string[]>
            {
                { Core.Ephemeris.Body.Mars, new[] { "Phobos", "Deimos" } },
                { Core.Ephemeris.Body.Jupiter, new[] { "Io", "Europa", "Ganymede", "Callisto", "Amalthea", "Himalia", "Elara", "Pasiphae", "Sinope", "Lysithea", "Carme", "Ananke", "Leda", "Thebe", "Adrastea", "Metis" } },
                { Core.Ephemeris.Body.Saturn, new[] { "Mimas", "Enceladus", "Tethys", "Dione", "Rhea", "Titan", "Hyperion", "Iapetus", "Phoebe", "Janus", "Epimetheus", "Helene", "Telesto", "Calypso", "Atlas", "Prometheus", "Pandora", "Pan" } },
                { Core.Ephemeris.Body.Uranus, new[] { "Ariel", "Umbriel", "Titania", "Oberon", "Miranda", "Cordelia", "Ophelia", "Cressida", "Desdemona", "Juliet", "Puck" } },
                { Core.Ephemeris.Body.Neptune, new[] { "Triton", "Nereid", "Naiad", "Thalassa", "Despina", "Galatea", "Larissa", "Proteus", "Halimede", "Psamathe", "Sao", "Laomedeia", "Neso" } },
                { Core.Ephemeris.Body.Pluto, new[] { "Charon", "Nix", "Hydra", "Kerberos", "Styx" } },
            };
            foreach (var kv in expected)
            {
                var missing = kv.Value.Where(n => PlanetMoons.Find(n) == null || PlanetMoons.Find(n).Planet != kv.Key).ToList();
                CheckTrue(kv.Key + ": all " + kv.Value.Length + " moons", missing.Count == 0 && PlanetMoons.Of(kv.Key).Count() == kv.Value.Length, string.Join(", ", missing));
            }
            CheckTrue("Found by name in any case; not the Earth's Moon or a planet", PlanetMoons.Find(" titania ") != null && PlanetMoons.Find("Moon") == null && PlanetMoons.Find("Saturn") == null);
            CheckTrue("Every moon has a size, a magnitude and a period", PlanetMoons.All.All(m => m.RadiusKm > 1 && m.AbsMag > -3 && m.AbsMag < 15 && m.PeriodDays > 0.25),
                string.Join(", ", PlanetMoons.All.Where(m => !(m.RadiusKm > 1 && m.AbsMag > -3 && m.AbsMag < 15 && m.PeriodDays > 0.25)).Select(m => m.Name)));
            var retro = new[] { "Triton", "Phoebe", "Sinope", "Pasiphae", "Carme", "Ananke", "Halimede", "Psamathe", "Neso" };
            CheckTrue("Retrograde: " + string.Join(", ", retro) + " and no others", PlanetMoons.All.All(m => m.Retrograde == retro.Contains(m.Name)),
                string.Join(", ", PlanetMoons.All.Where(m => m.Retrograde != retro.Contains(m.Name)).Select(m => m.Name)));
        }

        [TestMethod]
        public void TestPlanetMoonsAgainstHorizons()
        {
            // JPL Horizons, geometric ICRF positions (km) from the planet's centre (barycentre for Himalia, Sinope, Phoebe,
            // Nereid, Neso and Hydra), at 2026-10-31 and 2043-04-23 0h TDB: each kind of orbit model in use
            var refs = new object[,]
            {
                { "Phobos", 2461344.5, 2870, -7235, -5186 },
                { "Phobos", 2467344.5, 6573, -3936, -5593 },
                { "Io", 2461344.5, 153072, 353117, 170949 },
                { "Io", 2467344.5, -197361, -333311, -162434 },
                { "Ganymede", 2461344.5, -325038, 923037, 437537 },
                { "Ganymede", 2467344.5, 951723, -444908, -194505 },
                { "Himalia", 2461344.5, -9280762, 1069000, 5033543 },
                { "Himalia", 2467344.5, -6964596, 3527122, 5648301 },
                { "Sinope", 2461344.5, -27170216, 7250927, -6632105 },
                { "Sinope", 2467344.5, -26235372, -270091, -10464878 },
                { "Mimas", 2461344.5, 144669, -111711, 830 },
                { "Mimas", 2467344.5, -153519, -97377, 17097 },
                { "Titan", 2461344.5, 1117631, -393371, -72647 },
                { "Titan", 2467344.5, 74319, 1214980, -87169 },
                { "Hyperion", 2461344.5, -39451, -1363048, 80277 },
                { "Hyperion", 2467344.5, -288143, -1303173, 102968 },
                { "Iapetus", 2461344.5, 2181803, 2708532, 67393 },
                { "Iapetus", 2467344.5, 158705, -3506352, -700580 },
                { "Phoebe", 2461344.5, 3342114, -9391134, -4490752 },
                { "Phoebe", 2467344.5, 10737612, -4715898, -3405477 },
                { "Janus", 2461344.5, -140753, -52225, 16112 },
                { "Janus", 2467344.5, -16738, -149886, 12064 },
                { "Epimetheus", 2461344.5, -1462, -151364, 11107 },
                { "Epimetheus", 2467344.5, 145803, 36974, -15754 },
                { "Helene", 2461344.5, -359418, -111473, 39369 },
                { "Helene", 2467344.5, 129947, -351915, 13387 },
                { "Titania", 2461344.5, 332517, -142732, 244796 },
                { "Titania", 2467344.5, 369998, -19196, -230292 },
                { "Miranda", 2461344.5, -35708, 42906, -117392 },
                { "Miranda", 2467344.5, 76838, 1804, -104879 },
                { "Triton", 2461344.5, 229445, 34179, -268450 },
                { "Triton", 2467344.5, 266641, 103161, -210070 },
                { "Nereid", 2461344.5, -2010285, 5793266, 2856576 },
                { "Nereid", 2467344.5, 2623729, 8181771, 4381931 },
                { "Neso", 2461344.5, -49714686, 64972667, -23163084 },
                { "Neso", 2467344.5, -50336912, 20367912, -36216652 },
                { "Charon", 2461344.5, -13266, -10974, 9363 },
                { "Charon", 2467344.5, 13364, 13274, 5392 },
                { "Hydra", 2461344.5, -28261, -18469, 55644 },
                { "Hydra", 2467344.5, -12060, -1681, 63870 },
            };
            // the planets' usual distances (AU), to turn kilometres into arcseconds
            var au = new Dictionary<Core.Ephemeris.Body, double> { { Core.Ephemeris.Body.Mars, 0.6 }, { Core.Ephemeris.Body.Jupiter, 4.2 }, { Core.Ephemeris.Body.Saturn, 8.5 }, { Core.Ephemeris.Body.Uranus, 18.2 }, { Core.Ephemeris.Body.Neptune, 29 }, { Core.Ephemeris.Body.Pluto, 33 } };
            for (int k = 0; k < refs.GetLength(0); k++)
            {
                var m = PlanetMoons.Find((string)refs[k, 0]);
                double jd = (double)refs[k, 1];
                var truth = new Vec3((int)refs[k, 2], (int)refs[k, 3], (int)refs[k, 4]);
                double km = (PlanetMoons.OffsetKm(m, jd) - truth).Length, arcsec = km / (au[m.Planet] * AstroMath.AU_km) * AstroMath.Rad2ArcSec;
                // within 3" as seen from Earth, and 5 % of its distance from the planet
                CheckTrue(string.Format("{0} {1:0.0}: within 3\" of Horizons", m.Name, jd), arcsec < 3 && km < 0.05 * truth.Length, string.Format("{0:0} km, {1:0.00}\"", km, arcsec));
            }
            // outside 2000-2050 the ellipses still keep each moon on its orbit
            foreach (var m in PlanetMoons.All)
            {
                double r = PlanetMoons.OffsetKm(m, 2451545.0 - 36525 / 2.0).Length, a = m.SemiMajorAxisKm;
                CheckTrue(m.Name + " in 1950: at a sensible distance from its planet", r > a * (1 - m.Eccentricity) * 0.8 && r < a * (1 + m.Eccentricity) * 1.2, r.ToString("0") + " km");
            }
        }

        [TestMethod]
        public void TestPlanetMoonsInTheSky()
        {
            var ctx = new Core.SkyContext(new Core.Observer { LatitudeDeg = 59.3, LongitudeDeg = 18.1 }, Core.Time.AstroTime.FromDateTime(new DateTime(2026, 10, 31, 1, 0, 0, DateTimeKind.Utc)));
            foreach (var planet in PlanetMoons.Planets)
            {
                var pl = ctx.BodyPlace(planet);
                double rKm = Core.Ephemeris.PlanetPhysics.EquatorialRadiusKm(planet);
                foreach (var m in PlanetMoons.Places(ctx, planet))
                {
                    string n = m.Moon.Name + ": ";
                    var a = m.Place.Astrometric;
                    double sep = AstroMath.Separation(pl.Astrometric.Ra, pl.Astrometric.Dec, a.Ra, a.Dec) * AstroMath.Rad2ArcSec;
                    // the moon's own light time differs from the planet's: a few hundredths of an arcsecond for the outer ones
                    Check(n + "RA/Dec are the stated distance from " + planet + " (\")", sep, m.SeparationArcsec, Math.Max(0.01, 0.001 * sep));
                    double pa = AstroMath.PositionAngle(pl.Astrometric.Ra, pl.Astrometric.Dec, a.Ra, a.Dec);
                    if (sep > 1) CheckTrue(n + "east and north parts agree with RA/Dec", Math.Abs(AstroMath.NormalizeDeg180((Math.Atan2(m.EastArcsec, m.NorthArcsec) - pa) * AstroMath.Rad2Deg)) < 0.2);
                    Check(n + "planet radii from the distance across the line of sight", m.PlanetRadii, m.SeparationArcsec / AstroMath.Rad2ArcSec * m.Place.Distance * AstroMath.AU_km / rKm, 1e-3 * m.PlanetRadii + 1e-6);
                    CheckTrue(n + "in front or behind, from its distance", m.Behind == (m.Place.Distance > pl.Distance), m.Place.Distance + " vs " + pl.Distance);
                    CheckTrue(n + "over the disc only when within a radius", (m.Transit || m.Occulted) == (m.PlanetRadii < 1));
                }
            }
            Func<string, MoonPlace> place = name => PlanetMoons.Place(ctx, PlanetMoons.Find(name));
            // magnitudes near those published, for the planets' distances on this date
            var mags = new Dictionary<string, double[]>
            {
                { "Ganymede", new[] { 4.9, 5.4 } }, { "Callisto", new[] { 5.9, 6.5 } }, { "Titan", new[] { 8.0, 8.9 } }, { "Rhea", new[] { 9.4, 10.3 } },
                { "Titania", new[] { 13.6, 14.3 } }, { "Miranda", new[] { 16.0, 16.8 } }, { "Triton", new[] { 13.2, 13.8 } }, { "Charon", new[] { 16.5, 17.5 } },
            };
            foreach (var kv in mags)
            {
                var m = place(kv.Key);
                CheckTrue(kv.Key + ": magnitude " + kv.Value[0] + " to " + kv.Value[1], m.Mag > kv.Value[0] && m.Mag < kv.Value[1], m.Mag.ToString("0.00"));
            }
            var g = place("Ganymede");
            CheckTrue("Ganymede 1.2-1.9\" across", g.DiameterArcsec > 1.2 && g.DiameterArcsec < 1.9, g.DiameterArcsec.ToString("0.00"));
            CheckTrue("Titan is within 23 Saturn radii, Iapetus within 62", place("Titan").PlanetRadii < 23 && place("Iapetus").PlanetRadii < 62);
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
