using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Sidralune.Core;
using Sidralune.Core.Catalogs;
using Sidralune.Core.Coordinates;
using Sidralune.Core.Mathematics;
using Sidralune.Core.Time;
using Sidralune.Devices;
using Sidralune.Devices.Automation;
using Sidralune.Devices.Simulators;
using Sidralune.Science.Astrometry;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Sidralune.Tests.Checks;

namespace Sidralune.Tests
{
    /// <summary>Plate-solve centring and three-point polar alignment end to end on the simulated observatory.</summary>
    [TestClass]
    public class AutomationTests : TestBase
    {
        [TestMethod]
        public void TestCenteringAndPolarAlignment()
        {
            var dm = new DeviceManager();
            dm.Site.LatitudeDeg = 52; dm.Site.LongitudeDeg = 5;
            foreach (var k in new[] { DeviceKind.Telescope, DeviceKind.Camera })
                dm.Connect(new DeviceInfo { Kind = k, Protocol = DeviceProtocol.Simulator, Name = k + " sim" });
            var tel = (TelescopeSimulator)dm.Telescope;
            tel.SlewRateDegPerSec = 90;
            // the camera's extra pseudo-stars (an aid when only a sparse catalog is loaded) are in no catalog to solve with
            ((CameraSimulator)dm.Camera).SyntheticFieldStars = false;

            // a dense field along the path of the test (the built-in catalog alone is too sparse for a 0.8° field):
            // the simulated camera renders these stars and the plate solver matches them
            var cm = CatalogManager.Instance;
            cm.LoadBuiltIn();
            var now = new SkyContext(dm.Site, AstroTime.Now);
            double raTarget = AstroMath.NormalizeDeg(now.ApparentToIcrs(AstroMath.NormalizeRad(now.Last - 0.3 * AstroMath.Hour2Rad), 45 * AstroMath.Deg2Rad).RaHours * 15);
            double decTarget = 45;
            var rnd = new Random(17);
            for (int i = 0; i < 40000; i++)
            {
                double ra = raTarget - 4 + 64 * rnd.NextDouble(), dec = 41.5 + 7 * rnd.NextDouble();
                cm.Stars.Add(new Star { Ra = AstroMath.NormalizeDeg(ra) * AstroMath.Deg2Rad, Dec = dec * AstroMath.Deg2Rad, Mag = 8 + 3.5 * Math.Pow(rnd.NextDouble(), 0.4) });
            }
            cm.Stars.Finish();
            try
            {
                var cap = new CaptureService { Devices = dm };
                tel.PointingErrorRa = 0.25; tel.PointingErrorDec = -0.2; // a poorly synced mount: 15′ and 12′ off
                var centring = new Centering { Capture = cap, Exposure = 1, Bin = 2, ToleranceArcmin = 1 };
                var steps = new System.Collections.Generic.List<string>();
                centring.Status += steps.Add;
                var wcs = centring.Center(raTarget, decTarget, CancellationToken.None).Result;
                double cra, cdec;
                wcs.Centre(out cra, out cdec);
                double err = AstroMath.Separation(cra * AstroMath.Deg2Rad, cdec * AstroMath.Deg2Rad, raTarget * AstroMath.Deg2Rad, decTarget * AstroMath.Deg2Rad) * AstroMath.Rad2Deg * 60;
                CheckTrue("Centred within 1′ after correcting a 19′ pointing error", err <= 1, err.ToString("0.00") + "′ after " + steps.Count(s => s.StartsWith("Centering attempt")) + " attempt(s)");
                CheckTrue("The first frame measured the pointing error", steps.Any(s => s.StartsWith("Pointing error") && double.Parse(s.Substring(15).TrimEnd('\''), System.Globalization.CultureInfo.InvariantCulture) > 10), string.Join(" | ", steps));
                CheckTrue("The mount was synced to the solution", Math.Abs(tel.PointingErrorRa) < 0.05 && Math.Abs(tel.PointingErrorDec) < 0.05, tel.PointingErrorRa.ToString("0.000") + ", " + tel.PointingErrorDec.ToString("0.000"));
                Check("Solved pixel scale at bin 2 (″/px)", wcs.PixelScale, 206.265 * 3.76 * 2 / dm.FocalLengthMm, 0.02);

                var pa = new PolarAlignment { Capture = cap, Exposure = 1, Bin = 2, StepDeg = 20 };
                double ra0 = tel.RightAscension, dec0 = tel.Declination;
                var r = pa.Run(CancellationToken.None).Result;
                Info(r.Advice);
                // the simulated mount is perfectly aligned; only refraction at the pole (≈0.8′ in altitude) remains
                CheckTrue("Polar alignment of a perfect mount", r.TotalErrorArcmin < 3 && Math.Abs(r.AzErrorArcmin) < 2, string.Format("alt {0:0.00}′, az {1:0.00}′", r.AltErrorArcmin, r.AzErrorArcmin));
                Check("Rotation axis at the pole", r.AxisDec, 90, 0.05);

                // a mount 10′ too high and 15′ east of the pole: three-point alignment reports it (against the refracted pole)
                tel.PolarErrorAltArcmin = 10; tel.PolarErrorAzArcmin = 15;
                Centering.Slew(tel, ra0, dec0, CancellationToken.None).Wait();
                r = pa.Run(CancellationToken.None).Result;
                double refraction = Transforms.RefractionFromTrue(52 * AstroMath.Deg2Rad, dm.Site.PressureHpa, dm.Site.TemperatureC) * AstroMath.Rad2Deg * 60;
                Info(string.Format("Three-point: alt {0:0.00}′ az {1:0.00}′ (refraction {2:0.00}′): {3}", r.AltErrorArcmin, r.AzErrorArcmin, refraction, r.Advice));
                Check("Three-point alignment: the altitude error (′)", r.AltErrorArcmin, 10 - refraction, 2.5);
                Check("Three-point alignment: the azimuth error (′)", r.AzErrorArcmin, 15, 3.75);
                StringAssert.Contains(r.Advice, "down");
                StringAssert.Contains(r.Advice, "west");
                tel.PolarErrorAltArcmin = tel.PolarErrorAzArcmin = 0;

                dm.Disconnect(DeviceKind.Telescope);
                bool noMount = false;
                try { new Centering { Capture = cap }.Center(0, 0, CancellationToken.None).Wait(); } catch (AggregateException ex) { noMount = ex.InnerException.Message == "No telescope connected"; }
                CheckTrue("Centring needs a telescope", noMount);
            }
            finally
            {
                dm.DisconnectAll();
                cm.LoadBuiltIn(); // drop the test stars from the shared catalog
            }
        }

        /// <summary>A catalogue that cannot be reached, as VizieR without a connection.</summary>
        private class UnreachableCatalog : IStarCatalogProvider
        {
            public int Calls;
            public string Name => "Unreachable";
            public bool IsOnline => true;
            public System.Collections.Generic.List<RefStar> Cone(double raDeg, double decDeg, double radiusDeg, double magLimit, int maxStars) { Calls++; throw new System.Net.WebException("offline"); }
        }

        [TestMethod]
        public void TestStoppingASlewStopsTheMount()
        {
            var tel = new TelescopeSimulator();
            tel.Connect();
            try
            {
                tel.SlewRateDegPerSec = 4;
                var cts = new CancellationTokenSource();
                double ra0 = tel.RightAscension, dec0 = tel.Declination;
                var slew = Centering.Slew(tel, AstroMath.NormalizeHours(ra0 + 4), Math.Max(-80, dec0 - 60), cts.Token);
                Thread.Sleep(1000);
                CheckTrue("The mount slews", tel.Slewing);
                cts.Cancel();
                bool cancelled = false;
                try { slew.Wait(5000); } catch (System.AggregateException ex) { cancelled = ex.InnerException is System.OperationCanceledException; }
                CheckTrue("Stopping the slew ends it", cancelled);
                CheckTrue("…and stops the mount", !tel.Slewing);
                double ra1 = tel.RightAscension, dec1 = tel.Declination;
                Thread.Sleep(1000);
                Info(string.Format("Stopped {0:0.0}° short of the target in Dec", Math.Abs(tel.Declination - Math.Max(-80, dec0 - 60))));
                CheckTrue("…where it was, not at the target", Math.Abs(tel.Declination - dec1) < 0.05 && Math.Abs(tel.Declination - Math.Max(-80, dec0 - 60)) > 10);
            }
            finally { tel.Disconnect(); }
        }

        [TestMethod]
        public void TestSolvingTheSimulatedSky()
        {
            // the simulator's faint synthetic stars are in no catalogue: the Observatory solves against the stars it draws
            var dm = new DeviceManager();
            dm.Site.LatitudeDeg = 52; dm.Site.LongitudeDeg = 5;
            foreach (var k in new[] { DeviceKind.Telescope, DeviceKind.Camera, DeviceKind.GuideCamera })
                dm.Connect(new DeviceInfo { Kind = k, Protocol = DeviceProtocol.Simulator, Name = k + " sim" });
            var tel = (TelescopeSimulator)dm.Telescope;
            tel.SlewRateDegPerSec = 90;
            CatalogManager.Instance.LoadBuiltIn();
            var cap = new CaptureService { Devices = dm };
            var ct = CancellationToken.None;
            try
            {
                var opt = new SolveOptions();
                dm.PrepareSolve(opt, dm.Camera);
                CheckTrue("A simulated camera is solved against the stars it draws", opt.Catalog is SimulatedSkyProvider && ((SimulatedSkyProvider)opt.Catalog).Synthetic && !opt.Catalog.IsOnline);
                var sky = new SimulatedSkyProvider();
                var a = sky.Cone(120, 40, 0.5, 14, 1000);
                var b = sky.Cone(120.2, 40.1, 0.5, 14, 1000);
                CheckTrue("The synthetic stars are the same whatever the query's centre", a.Count > 100 && a.Count(s => b.Any(t => Math.Abs(t.Ra - s.Ra) < 1e-9 && Math.Abs(t.Dec - s.Dec) < 1e-9)) > 0.3 * a.Count, a.Count + " / " + b.Count);
                var seam1 = sky.Cone(0.05, 10, 0.3, 14, 1000);
                var seam2 = sky.Cone(359.95, 10, 0.3, 14, 1000);
                CheckTrue("…across RA 0h too", seam1.Count > 20 && seam1.Count(s => seam2.Any(t => Math.Abs(t.Ra - s.Ra) < 1e-9 && Math.Abs(t.Dec - s.Dec) < 1e-9)) > 0.5 * seam1.Count, seam1.Count + " / " + seam2.Count);

                foreach (var dec in new[] { 10.0, 88.5 })
                {
                    Centering.Slew(tel, tel.SiderealTime - 0.5, dec, ct).Wait();
                    var frame = cap.Expose(new ExposureRequest { Seconds = 2, Bin = 2, Save = false, FrameType = "SNAPSHOT" }, ct).Result.Image;
                    var sctx = new SkyContext(dm.Site, AstroTime.Now);
                    var icrs = sctx.ApparentToIcrs(tel.RightAscension * AstroMath.Hour2Rad, tel.Declination * AstroMath.Deg2Rad);
                    var o = new SolveOptions { HintRa = icrs.RaHours * 15, HintDec = icrs.DecDeg, SearchRadius = 2, ScaleHint = 206.265 * 3.76 * 2 / dm.FocalLengthMm };
                    dm.PrepareSolve(o, dm.Camera);
                    var sr = PlateSolver.Solve(frame, o);
                    double cra = double.NaN, cdec = double.NaN;
                    if (sr.Success) sr.Wcs.Centre(out cra, out cdec);
                    double err = sr.Success ? AstroMath.Separation(cra * AstroMath.Deg2Rad, cdec * AstroMath.Deg2Rad, icrs.Ra, icrs.Dec) * AstroMath.Rad2Deg * 60 : double.NaN;
                    CheckTrue(string.Format("A simulated frame at Dec {0:0.0}° solves, with only the built-in catalogue loaded", dec), sr.Success && err < 2, sr.Message + " " + err.ToString("0.00") + "′");
                    if (dec > 80)
                    {
                        // a hint on the far side of the pole: the search goes over the pole to the field
                        var far = new SolveOptions { HintRa = AstroMath.NormalizeDeg(icrs.RaHours * 15 + 180), HintDec = 89.5, SearchRadius = 3, ScaleHint = o.ScaleHint, Catalog = o.Catalog };
                        var fr = PlateSolver.Solve(frame, far);
                        CheckTrue("…and with a hint on the far side of the pole", fr.Success, fr.Message);
                    }
                }

                // three-point alignment in the simulator, as from the Polar alignment tab
                Centering.Slew(tel, tel.SiderealTime - 0.5, 10, ct).Wait();
                tel.PolarErrorAltArcmin = 10; tel.PolarErrorAzArcmin = 15;
                var r = new PolarAlignment { Capture = cap, Exposure = 2, Bin = 2, StepDeg = 20 }.Run(ct).Result;
                double refraction = Transforms.RefractionFromTrue(52 * AstroMath.Deg2Rad, dm.Site.PressureHpa, dm.Site.TemperatureC) * AstroMath.Rad2Deg * 60;
                Check("Three-point alignment on the synthetic sky: the altitude error (′)", r.AltErrorArcmin, 10 - refraction, 2.5);
                Check("…and the azimuth error (′)", r.AzErrorArcmin, 15, 3.75);

                // a real camera: Gaia online, and the loaded catalogue when VizieR cannot be reached
                var unreachable = new UnreachableCatalog();
                var fb = new FallbackCatalogProvider(unreachable, new LocalCatalogProvider());
                var stars = fb.Cone(37.95, 89.26, 1, 6, 10);
                fb.Cone(37.95, 89.26, 1, 6, 10);
                CheckTrue("Without a connection the loaded catalogue answers, and the online one is not asked again", stars.Any(s => s.Mag < 2.5) && unreachable.Calls == 1 && !fb.IsOnline, unreachable.Calls.ToString());
                var real = new SolveOptions { SearchRadius = 10 };
                new DeviceManager().PrepareSolve(real, null);
                CheckTrue("A real camera is solved against Gaia, near the hint", real.Catalog is FallbackCatalogProvider && real.Catalog.IsOnline && real.SearchRadius == 2);
            }
            finally { dm.DisconnectAll(); }
        }

        [TestMethod]
        public void TestUnguidedDriftCorrection()
        {
            var dm = new DeviceManager();
            dm.Site.LatitudeDeg = 52; dm.Site.LongitudeDeg = 5;
            foreach (var k in new[] { DeviceKind.Telescope, DeviceKind.Camera })
                dm.Connect(new DeviceInfo { Kind = k, Protocol = DeviceProtocol.Simulator, Name = k + " sim" });
            CatalogManager.Instance.LoadBuiltIn();
            var tel = (TelescopeSimulator)dm.Telescope;
            tel.SlewRateDegPerSec = 90;
            var ct = CancellationToken.None;
            string dir = Path.Combine(Path.GetTempPath(), "SidraluneTests", "drift");
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            try
            {
                // east of the pier, where this mount's North pulses move it south
                Centering.Slew(tel, tel.SiderealTime + 0.5, 40, ct).Wait();
                CheckTrue("East of the pier", tel.SideOfPier == PierSide.East);
                tel.DriftRaArcsecPerMin = 30; tel.DriftDecArcsecPerMin = -20;
                double ra0 = tel.TrueRa, dec0 = tel.TrueDec;
                Func<double> off = () => Math.Sqrt(Math.Pow(AstroMath.NormalizeDeg180((tel.TrueRa - ra0) * 15) * Math.Cos(dec0 * AstroMath.Deg2Rad), 2) + Math.Pow(tel.TrueDec - dec0, 2)) * 3600;
                var capture = new CaptureService { Devices = dm, ImageFolder = dir };
                var log = new List<string>();
                var ctx = new SequenceContext { Devices = dm, Capture = capture, Log = m => { lock (log) log.Add(m); } };
                ctx.Drift = new DriftCorrector { Devices = dm, Log = ctx.Log, Random = new Random(5) };
                var sw = Stopwatch.StartNew();
                var shots = new TakeExposures { Count = 6, Exposure = 3, DitherEvery = 0, FrameType = "LIGHT" };
                shots.Run(ctx, ct).Wait();
                double minutes = sw.Elapsed.TotalMinutes, uncorrected = Math.Sqrt(30 * 30 + 20 * 20) * minutes;
                Info(string.Format("{0:0.0} min: off by {1:0.0}\" where uncorrected drift would be {2:0}\"", minutes, off(), uncorrected));
                Info(string.Join(" | ", log.Where(m => m.StartsWith("Drift"))));
                CheckTrue("The first frame is the reference, plate-solved", log.Any(m => m.StartsWith("Drift correction: reference frame")) && ctx.Drift.HasReference);
                CheckTrue("Each later frame is registered and the drift corrected by pulses", log.Count(m => m.Contains("(registration,") && m.Contains("by pulses")) >= 4, string.Join(" | ", log));
                CheckTrue("…Dec pulses turned round where they move the other way", log.Any(m => m.Contains("Dec pulses move the other way here")));
                CheckTrue("…so the mount stays where it began", off() < 12 && off() < uncorrected / 3, off().ToString("0.0"));

                // with dithers: the drift corrected and the dither made in one move, within the dither's reach
                log.Clear();
                var dithered = new TakeExposures { Count = 4, Exposure = 3, DitherEvery = 1, DitherPixels = 8, FrameType = "LIGHT" };
                dithered.Run(ctx, ct).Wait();
                CheckTrue("With dithers, each move adds a dither", log.Count(m => m.Contains("with a dither")) == 3, string.Join(" | ", log));
                CheckTrue("…and the mount stays within the dither's reach", off() < 8 * 1.6 * 1.5 + 12, off().ToString("0.0"));

                // a large offset, or a mount without pulse guiding: a small slew
                ctx.Drift.MaxPulseMs = 0;
                log.Clear();
                new TakeExposures { Count = 2, Exposure = 3, DitherEvery = 0, FrameType = "LIGHT" }.Run(ctx, ct).Wait();
                CheckTrue("Beyond the longest pulse, a small slew", log.Any(m => m.Contains("by slew")), string.Join(" | ", log));

                // guiding: no drift correction
                CheckTrue("Unguided frames only", !GuidingPause.Guiding(null));
                // a slew to another field: a new reference
                tel.DriftRaArcsecPerMin = tel.DriftDecArcsecPerMin = 0;
                Centering.Slew(tel, tel.SiderealTime + 1.5, 20, ct).Wait();
                log.Clear();
                new TakeExposures { Count = 1, Exposure = 3, DitherEvery = 0, FrameType = "LIGHT" }.Run(ctx, ct).Wait();
                CheckTrue("After a slew to another field, its first frame is the new reference", log.Any(m => m.StartsWith("Drift correction: reference frame")), string.Join(" | ", log));
                new TakeExposures { Count = 1, Exposure = 3, DitherEvery = 0, FrameType = "DARK" }.Run(ctx, ct).Wait();
                CheckTrue("Dark frames are left alone", ctx.Drift.Last.Method == "reference");
            }
            finally
            {
                tel.DriftRaArcsecPerMin = tel.DriftDecArcsecPerMin = 0;
                dm.DisconnectAll();
            }
        }
    }
}
