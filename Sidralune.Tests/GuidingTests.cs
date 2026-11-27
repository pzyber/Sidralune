using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Sidralune.Core;
using Sidralune.Core.Catalogs;
using Sidralune.Core.Ephemeris;
using Sidralune.Core.Mathematics;
using Sidralune.Core.MinorBodies;
using Sidralune.Core.Time;
using Sidralune.Devices;
using Sidralune.Devices.Automation;
using Sidralune.Devices.Guiding;
using Sidralune.Devices.Simulators;
using Sidralune.Imaging;
using Sidralune.Imaging.Analysis;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Sidralune.Tests.Checks;

namespace Sidralune.Tests
{
    /// <summary>The Sidralune guider: its algorithms, calibration geometry, star choice, the simulators it is tested on, and guiding in a closed loop.</summary>
    [TestClass]
    public class GuidingTests : TestBase
    {
        internal static bool SpinUntil(Func<bool> cond, int ms)
        {
            var sw = Stopwatch.StartNew();
            while (!cond()) { if (sw.ElapsedMilliseconds > ms) return false; Thread.Sleep(20); }
            return true;
        }

        internal static string Folder(string name)
        {
            string dir = Path.Combine(Path.GetTempPath(), "SidraluneTests", name);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.CreateDirectory(dir);
            return dir;
        }

        [TestMethod]
        public void TestGuideAlgorithms()
        {
            var h = new HysteresisAlgorithm { MinMovePx = 0.2, Hysteresis = 0.1, Aggressiveness = 0.7 };
            Check("Hysteresis leaves an error under the minimum move alone", h.Correction(0.1), 0, 1e-12);
            h.Reset();
            Check("Hysteresis: a share of the blended error", h.Correction(1.0), 0.9 * 0.7, 1e-12);
            Check("Hysteresis: the previous blend counts", h.Correction(1.0), (0.9 + 0.1 * 0.9) * 0.7, 1e-12);
            h.Reset();
            Check("Hysteresis reset forgets", h.Correction(-1.0), -0.63, 1e-12);

            var r = new ResistSwitchAlgorithm { MinMovePx = 0.2, Aggressiveness = 1, FastSwitch = true };
            CheckTrue("Resist Switch: no correction before three errors agree", r.Correction(0.5) == 0 && r.Correction(0.5) == 0);
            Check("Resist Switch: the third error the same way starts corrections", r.Correction(0.5), 0.5, 1e-12);
            Check("Resist Switch keeps its direction", r.Correction(0.3), 0.3, 1e-12);
            Check("Resist Switch: one error the other way is not enough", r.Correction(-0.5), 0, 1e-12);
            r.Correction(-0.4);
            Check("Resist Switch: shrinking errors the other way do not turn it", r.Correction(-0.3), 0, 1e-12);
            r.Reset(); r.Correction(0.5); r.Correction(0.5); r.Correction(0.5);
            CheckTrue("…growing ones do, on the third", r.Correction(-0.3) == 0 && r.Correction(-0.4) == 0 && r.Correction(-0.5) == -0.5);
            Check("Resist Switch: fast switch for a large error", r.Correction(0.7), 0.7, 1e-12);
            r.FastSwitch = false; r.Reset();
            Check("Without fast switch even a large error waits", r.Correction(2), 0, 1e-12);
            var half = new ResistSwitchAlgorithm { Aggressiveness = 0.5 };
            Check("Resist Switch aggressiveness", half.Correction(1), 0.5, 1e-12);
            Check("Resist Switch ignores errors under the minimum move", half.Correction(0.1), 0, 1e-12);
        }

        private static GuideCalibration SampleCalibration() => new GuideCalibration
        {
            RaAngleDeg = 30, DecAngleDeg = 125, RaRatePxPerSec = 2, DecRatePxPerSec = 2, DecDeg = 0, PierSide = PierSide.West,
            Binning = 1, PixelSizeUm = 3.75, FocalLengthMm = 240, Output = GuideOutputKind.Mount, Camera = "Simulator sim", RaSteps = 12, DecSteps = 12
        };

        [TestMethod]
        public void TestCalibrationGeometry()
        {
            var c = SampleCalibration();
            double ex, ey, ra, dec;
            c.FromAxes(1.5, -0.7, PierSide.West, false, out ex, out ey);
            c.ToAxes(ex, ey, PierSide.West, false, out ra, out dec);
            CheckTrue("Offsets split into RA and Dec with axes 95° apart", Math.Abs(ra - 1.5) < 1e-9 && Math.Abs(dec + 0.7) < 1e-9);
            Check("Distance from right angles", c.OrthogonalityErrorDeg, 5, 1e-9);

            // after a flip the West motion is turned round; North is too only if the mount reverses Dec itself
            double wx = Math.Cos(30 * AstroMath.Deg2Rad), wy = Math.Sin(30 * AstroMath.Deg2Rad), nx = Math.Cos(125 * AstroMath.Deg2Rad), ny = Math.Sin(125 * AstroMath.Deg2Rad);
            c.ToAxes(wx, wy, PierSide.East, false, out ra, out dec);
            CheckTrue("After a flip a West-like offset reads as East", Math.Abs(ra + 1) < 1e-9 && Math.Abs(dec) < 1e-9);
            c.ToAxes(nx, ny, PierSide.East, false, out ra, out dec);
            CheckTrue("After a flip, North stays when the mount does not reverse Dec", Math.Abs(dec - 1) < 1e-9);
            c.ToAxes(nx, ny, PierSide.East, true, out ra, out dec);
            CheckTrue("After a flip, North turns round when the mount reverses Dec", Math.Abs(dec + 1) < 1e-9);
            c.ToAxes(wx, wy, PierSide.Unknown, false, out ra, out dec);
            CheckTrue("An unknown pier side is no flip", Math.Abs(ra - 1) < 1e-9);

            Check("RA rate falls with cos(dec)", c.RaRateAt(60), 1.0, 1e-9);
            Check("RA rate with no declination", c.RaRateAt(double.NaN), 2, 1e-12);

            // a leg fitted from noisy positions
            var rng = new Random(5);
            var t = new List<double>(); var xs = new List<double>(); var ys = new List<double>();
            for (int i = 0; i <= 12; i++)
            {
                t.Add(i * 0.75); xs.Add(100 + 2.5 * Math.Cos(-150 * AstroMath.Deg2Rad) * i * 0.75 + (rng.NextDouble() - 0.5) * 0.3);
                ys.Add(80 + 2.5 * Math.Sin(-150 * AstroMath.Deg2Rad) * i * 0.75 + (rng.NextDouble() - 0.5) * 0.3);
            }
            double angle, rate;
            CheckTrue("A leg is fitted", GuideCalibration.FitLeg(t, xs, ys, out angle, out rate));
            Check("Fitted leg direction (°)", AstroMath.NormalizeDeg180(angle + 150), 0, 1);
            Check("Fitted leg rate (px/s)", rate, 2.5, 0.075);

            c.Check();
            CheckTrue("A good calibration has no issues", c.Issues.Count == 0);
            var bad = SampleCalibration();
            bad.DecAngleDeg = 140; bad.RaRatePxPerSec = 3; bad.DecSteps = 2; bad.ExpectedRaRatePxPerSec = 6; bad.ExpectedDecRatePxPerSec = 2; bad.DecDeg = 80;
            bad.Check();
            CheckTrue("Issues: far from square, few steps, unlike the prediction, near the pole",
                bad.Issues.Contains(GuideCalibration.IssueOrthogonality) && bad.Issues.Contains(GuideCalibration.IssueFewSteps)
                && bad.Issues.Contains(GuideCalibration.IssuePrediction) && bad.Issues.Contains(GuideCalibration.IssueNearPole), string.Join(",", bad.Issues));
            var ratio = SampleCalibration();
            ratio.RaRatePxPerSec = 4; // RA as fast as twice Dec at the equator
            ratio.Check();
            CheckTrue("Issue: RA and Dec rates do not agree with the declination", ratio.Issues.Contains(GuideCalibration.IssueRateRatio));

            CheckTrue("The calibration fits the equipment it was made with", c.Fits(1, 3.75, 240, GuideOutputKind.Mount, "Simulator sim"));
            CheckTrue("…and not other binning, optics, output or camera", !c.Fits(2, 3.75, 240, GuideOutputKind.Mount, "Simulator sim") && !c.Fits(1, 3.75, 300, GuideOutputKind.Mount, "Simulator sim")
                && !c.Fits(1, 3.75, 240, GuideOutputKind.Camera, "Simulator sim") && !c.Fits(1, 3.75, 240, GuideOutputKind.Mount, "Ascom X"));

            // the settings, calibration included, are saved and read back
            string path = Path.Combine(Folder("guider-settings"), "guider.json");
            var s = new GuiderSettings { ExposureSeconds = 1.5, Output = GuideOutputKind.Camera, MountReversesDecAfterFlip = true, Calibration = bad };
            s.Save(path);
            var back = GuiderSettings.Load(path);
            CheckTrue("Guider settings read back", back.ExposureSeconds == 1.5 && back.Output == GuideOutputKind.Camera && back.MountReversesDecAfterFlip && back.Calibration != null
                && back.Calibration.DecAngleDeg == 140 && back.Calibration.PierSide == PierSide.West && back.Calibration.Issues.Count == bad.Issues.Count);
            CheckTrue("Missing settings give the defaults", GuiderSettings.Load(Path.Combine(Folder("guider-none"), "x.json")).ExposureSeconds == 2);
        }

        private static AstroImage Field(IEnumerable<Synthetic.SynthStar> stars, int hot = 0, int seed = 3) =>
            Synthetic.Render(400, 300, stars, Transform2D.Identity, 3.0, 0.02, 0.003, seed, hot);

        [TestMethod]
        public void TestGuideStarChoice()
        {
            var stars = new List<Synthetic.SynthStar>
            {
                new Synthetic.SynthStar { X = 120.3, Y = 140.6, Flux = 0.6 },   // the one to choose
                new Synthetic.SynthStar { X = 300, Y = 150, Flux = 40 },        // saturated
                new Synthetic.SynthStar { X = 8, Y = 60, Flux = 2 },            // at the edge
                new Synthetic.SynthStar { X = 250, Y = 60, Flux = 1.5 },        // bright but with a close neighbour
                new Synthetic.SynthStar { X = 258, Y = 66, Flux = 0.8 },
                new Synthetic.SynthStar { X = 60, Y = 240, Flux = 0.1 },        // fainter
            };
            var img = Field(stars, 30);
            var lum = img.Luminance();
            var pick = GuideStars.Select(lum, img.Width, img.Height, 15);
            CheckTrue("The single, unsaturated, well-placed star is chosen", pick != null && Math.Abs(pick.X - 120.3) < 0.5 && Math.Abs(pick.Y - 140.6) < 0.5, pick?.ToString());
            var click = GuideStars.Select(lum, img.Width, img.Height, 15, 62, 238);
            CheckTrue("A click chooses the star there", click != null && Math.Abs(click.X - 60) < 0.5 && Math.Abs(click.Y - 240) < 0.5, click?.ToString());
            CheckTrue("A click on empty sky chooses nothing", GuideStars.Select(lum, img.Width, img.Height, 15, 200, 250) == null);

            // measured again in later frames, to a small fraction of a pixel
            double worst = 0;
            for (int i = 0; i < 8; i++)
            {
                double dx = 0.37 * i - 1.2, dy = -0.23 * i + 0.8;
                var moved = Field(stars.Select(s => new Synthetic.SynthStar { X = s.X + dx, Y = s.Y + dy, Flux = s.Flux }), 0, 10 + i);
                var m = GuideStars.Measure(moved.Luminance(), moved.Width, moved.Height, pick.X, pick.Y, 15);
                if (m == null) { worst = 99; break; }
                worst = Math.Max(worst, Math.Sqrt(Math.Pow(m.X - (120.3 + dx), 2) + Math.Pow(m.Y - (140.6 + dy), 2)));
            }
            Check("Worst centroid error in the search box (px)", worst, 0, 0.15);
            var empty = Field(stars.Where(s => s.X != 120.3));
            CheckTrue("A star that has gone is reported lost", GuideStars.Measure(empty.Luminance(), empty.Width, empty.Height, pick.X, pick.Y, 15) == null);
            var flat = Field(new Synthetic.SynthStar[0], 50);
            CheckTrue("Hot pixels alone give no guide star", GuideStars.Select(flat.Luminance(), flat.Width, flat.Height, 15) == null);
        }

        internal static DeviceManager GuideObservatory(string name)
        {
            CatalogManager.Instance.LoadBuiltIn();
            string dir = Folder(name);
            var dm = new DeviceManager { GuiderSettingsPath = Path.Combine(dir, "guider.json"), GuideLogFolder = dir };
            dm.Site.LatitudeDeg = 52; dm.Site.LongitudeDeg = 5;
            foreach (var k in new[] { DeviceKind.Telescope, DeviceKind.GuideCamera }) dm.Connect(new DeviceInfo { Kind = k, Protocol = DeviceProtocol.Simulator, Name = k + " sim", Address = "sim" });
            var tel = (TelescopeSimulator)dm.Telescope;
            tel.SlewRateDegPerSec = 90;
            // fast guide rates and a long guide scope, so calibration takes seconds
            tel.GuideRateRa = tel.GuideRateDec = 2 * TelescopeSimulator.SiderealDegPerSec;
            var cam = (CameraSimulator)dm.GuideCamera;
            cam.JitterArcsec = 0.1;
            // the Sidralune guider is built in: with a guide camera it is the guider
            var g = dm.BuiltinGuider;
            g.Settings.ExposureSeconds = 0.2;
            g.Settings.FocalLengthMm = 500;
            g.Settings.CalibrationDistancePx = 12;
            g.SettleSeconds = 1;
            return dm;
        }

        [TestMethod]
        public void TestGuidingSimulators()
        {
            var dm = GuideObservatory("guide-sims");
            var tel = (TelescopeSimulator)dm.Telescope;
            Centering.Slew(tel, tel.SiderealTime - 0.5, 30, CancellationToken.None).Wait();
            double dec0 = tel.Declination;
            tel.PulseGuide(GuideDirection.North, 600);
            CheckTrue("A pulse takes its time", tel.IsPulseGuiding && (tel.Declination - dec0) * 3600 < 9);
            SpinUntil(() => !tel.IsPulseGuiding, 2000);
            Check("Pulse north at 2× sidereal for 0.6 s (″)", (tel.Declination - dec0) * 3600, 2 * 15.0410686 * 0.6, 0.05);
            tel.DecBacklashMs = 300;
            dec0 = tel.Declination;
            tel.PulseGuide(GuideDirection.South, 600);
            SpinUntil(() => !tel.IsPulseGuiding, 2000);
            Check("Reversing Dec first takes up the backlash (″)", (dec0 - tel.Declination) * 3600, 2 * 15.0410686 * 0.3, 0.05);
            tel.DecBacklashMs = 0;

            tel.PeriodicErrorArcsec = 5; tel.WormPeriodSec = 2;
            double ra0 = tel.RightAscension, lo = double.MaxValue, hi = double.MinValue;
            for (int i = 0; i < 25; i++) { double v = tel.TrueRa; lo = Math.Min(lo, v); hi = Math.Max(hi, v); Thread.Sleep(80); }
            Check("Periodic error moves the true RA (″ on the sky)", (hi - lo) * 15 * 3600 * Math.Cos(30 * AstroMath.Deg2Rad), 10, 1.5);
            Check("…which the mount does not report", (tel.RightAscension - ra0) * 15 * 3600, 0, 0.01);
            tel.PeriodicErrorArcsec = 0;

            Centering.Slew(tel, tel.SiderealTime + 0.5, 30, CancellationToken.None).Wait();
            CheckTrue("East of the meridian on the east side of the pier", tel.SideOfPier == PierSide.East);
            dec0 = tel.Declination;
            tel.PulseGuide(GuideDirection.North, 300);
            SpinUntil(() => !tel.IsPulseGuiding, 2000);
            CheckTrue("On the east side North turns the Dec motor towards the south", tel.Declination < dec0);
            tel.DecPulsesFollowSky = true;
            dec0 = tel.Declination;
            tel.PulseGuide(GuideDirection.North, 300);
            SpinUntil(() => !tel.IsPulseGuiding, 2000);
            CheckTrue("…unless the mount reverses Dec itself", tel.Declination > dec0);

            // the guide camera: rotation, the 180° turn on the east side, sub-frames and its guide port
            var cam = (CameraSimulator)dm.GuideCamera;
            cam.FocalLengthMm = 500; cam.JitterArcsec = 0;
            Func<AstroImage> grab = () => CaptureService.GrabFrame(cam, 0.05, CancellationToken.None).Result;
            Func<AstroImage, DetectedStar> brightest = f => StarDetector.Detect(f.Luminance(), f.Width, f.Height).Where(s => !s.Saturated).OrderByDescending(s => s.Flux).First();
            var full = grab();
            var star = brightest(full);
            cam.StartX = (int)star.X - 50; cam.StartY = (int)star.Y - 40; cam.NumX = 100; cam.NumY = 80;
            var sub = grab();
            var inSub = StarDetector.Detect(sub.Luminance(), sub.Width, sub.Height).OrderByDescending(s => s.Flux).FirstOrDefault();
            CheckTrue("A sub-frame shows its own part of the sensor", inSub != null && Math.Abs(inSub.X + cam.StartX - star.X) < 0.3 && Math.Abs(inSub.Y + cam.StartY - star.Y) < 0.3);
            cam.SetSensor(1280, 960, 3.75);
            // a target just east of the meridian; once it has crossed, slewing to it again flips the mount: the same field
            // on the other side of the pier, turned 180° about the centre
            double target = AstroMath.NormalizeHours(tel.SiderealTime + 0.0008);
            Centering.Slew(tel, target, 30, CancellationToken.None).Wait();
            CheckTrue("Just east of the meridian: east side of the pier", tel.SideOfPier == PierSide.East);
            var e1 = brightest(grab());
            Thread.Sleep(4500);
            CheckTrue("Past the meridian the mount stays on the east side until it slews", tel.SideOfPier == PierSide.East && MeridianFlip.MinutesPastMeridian(tel, target) > 0);
            Centering.Slew(tel, target, 30, CancellationToken.None).Wait();
            CheckTrue("Slewing again flips it to the west side", tel.SideOfPier == PierSide.West);
            var w = StarDetector.Detect(grab().Luminance(), 1280, 960);
            var turned = w.OrderBy(s => Math.Abs(s.X - (1280 - e1.X)) + Math.Abs(s.Y - (960 - e1.Y))).First();
            Info("Star east of the pier " + e1 + ", west " + turned);
            CheckTrue("The field turns 180° on the other side of the pier", Math.Abs(turned.X - (1280 - e1.X)) < 1 && Math.Abs(turned.Y - (960 - e1.Y)) < 1 && Math.Abs(turned.Flux / e1.Flux - 1) < 0.3);
            CheckTrue("The guide camera has a guide port", cam.CanPulseGuide);
            dec0 = tel.Declination;
            tel.DecPulsesFollowSky = false;
            cam.PulseGuide(GuideDirection.North, 300);
            CheckTrue("…which pulses the mount", cam.IsPulseGuiding);
            SpinUntil(() => !cam.IsPulseGuiding, 2000);
            Check("…by the guide rate (″)", (tel.Declination - dec0) * 3600, 2 * 15.0410686 * 0.3, 0.05);
            dm.DisconnectAll();
        }

        private static double Rms(IEnumerable<GuideStep> steps) => Math.Sqrt(steps.Average(s => s.RaArcsec * s.RaArcsec + s.DecArcsec * s.DecArcsec));

        [TestMethod]
        public void TestGuiderClosedLoop()
        {
            var dm = GuideObservatory("guide-loop");
            var tel = (TelescopeSimulator)dm.Telescope;
            var g = (SidraluneGuider)dm.Guider;
            var ct = CancellationToken.None;
            Centering.Slew(tel, tel.SiderealTime - 0.5, 30, ct).Wait();
            // a mount to correct: periodic error, drift and backlash
            tel.PeriodicErrorArcsec = 4; tel.WormPeriodSec = 120; tel.DriftRaArcsecPerMin = 3; tel.DriftDecArcsecPerMin = 6; tel.DecBacklashMs = 200;
            var problems = new List<string>();
            g.Failed += m => problems.Add(m);

            var sw = Stopwatch.StartNew();
            g.StartGuiding(false, ct).Wait();
            Info("Calibrated and settled in " + sw.Elapsed.TotalSeconds.ToString("0.0") + " s");
            var cal = g.Calibration;
            CheckTrue("A calibration was made", cal != null && g.IsGuiding);
            double scale = 206.265 * 3.75 / 500;
            // the simulated guide camera is turned 30°: West moves the star towards 210° in the image and North towards 120°
            Check("Calibrated West direction (°)", AstroMath.NormalizeDeg180(cal.RaAngleDeg - 210), 0, 3);
            Check("Calibrated North direction (°)", AstroMath.NormalizeDeg180(cal.DecAngleDeg - 120), 0, 3);
            Check("Calibrated RA rate (px/s)", cal.RaRatePxPerSec, 2 * 15.0410686 * Math.Cos(30 * AstroMath.Deg2Rad) / scale, 0.1 * 2 * 15.04 / scale);
            Check("Calibrated Dec rate (px/s)", cal.DecRatePxPerSec, 2 * 15.0410686 / scale, 0.1 * 2 * 15.04 / scale);
            CheckTrue("The calibration looks right to itself", cal.Issues.Count == 0, string.Join(",", cal.Issues));
            CheckTrue("…and was saved", File.Exists(dm.GuiderSettingsPath) && GuiderSettings.Load(dm.GuiderSettingsPath).Calibration != null);
            // calibration brings the star back past the backlash steps too, so guiding starts near the lock position
            var first = g.History.First();
            CheckTrue("Calibration leaves the star near where it started", Math.Sqrt(first.RaDistance * first.RaDistance + first.DecDistance * first.DecDistance) < g.Settings.SearchRadius,
                string.Format("{0:0.0}, {1:0.0} px", first.RaDistance, first.DecDistance));

            int from = g.History.Count;
            SpinUntil(() => g.History.Count >= from + 20, 30000);
            var guided = g.History.Skip(from + 3).ToList();
            double rms = Rms(guided);
            Info(string.Format("Guided RMS {0:0.00}″ over {1} steps", rms, guided.Count));
            CheckTrue("Guided for 20 steps", guided.Count >= 17);
            CheckTrue("Guiding holds the star near the lock", rms < 1.2, rms.ToString("0.00"));

            // dithering moves the lock and settles again
            double lx = g.LockX, ly = g.LockY;
            sw.Restart();
            g.Dither(3, false, 0.6, 1, 30, ct).Wait();
            CheckTrue("Dither moved the lock position and settled", Math.Abs(g.LockX - lx) + Math.Abs(g.LockY - ly) > 0.1 && sw.Elapsed.TotalSeconds < 30 && g.IsGuiding);
            lx = g.LockX; ly = g.LockY;
            g.Dither(3, true, 0.6, 1, 30, ct).Wait();
            double rdx = g.LockX - lx, rdy = g.LockY - ly, rcos = Math.Cos(cal.RaAngleDeg * AstroMath.Deg2Rad), rsin = Math.Sin(cal.RaAngleDeg * AstroMath.Deg2Rad);
            CheckTrue("An RA-only dither moves along RA", Math.Abs(rdx * rsin - rdy * rcos) < 0.05, rdx + " " + rdy);

            // paused: frames go on, corrections stop
            g.Pause(true);
            CheckTrue("Paused", g.State == SidraluneGuider.Paused && !g.IsGuiding);
            int n = g.History.Count;
            Thread.Sleep(800);
            CheckTrue("No guide steps while paused", g.History.Count == n);
            g.Pause(false);
            CheckTrue("Guiding again", SpinUntil(() => g.IsGuiding, 3000));

            // a star that disappears (cloud) is waited for, and guiding goes on when it is back
            var cam = (CameraSimulator)dm.GuideCamera;
            double zp = cam.ZeroPointMag;
            cam.ZeroPointMag = 5;
            CheckTrue("Lost star", SpinUntil(() => g.State == SidraluneGuider.LostLock, 5000), g.State);
            cam.ZeroPointMag = zp;
            CheckTrue("Star found again, guiding", SpinUntil(() => g.IsGuiding, 5000), g.State);

            // stop, and start again: the calibration is reused
            var when = cal.When;
            g.StopGuiding();
            CheckTrue("Stopped", g.State == SidraluneGuider.Stopped);
            sw.Restart();
            g.StartGuiding(false, ct).Wait();
            CheckTrue("Starting again reuses the calibration", g.Calibration.When == when && g.IsGuiding);
            Info("Restarted in " + sw.Elapsed.TotalSeconds.ToString("0.0") + " s");

            // a meridian flip: the mount moves to the other side of the pier and guiding goes on with the calibration turned
            MeridianFlip.Perform(tel, AstroMath.NormalizeHours(tel.SiderealTime + 0.4), 30, g, ct).Wait();
            CheckTrue("After the flip the mount is on the east side", tel.SideOfPier == PierSide.East);
            CheckTrue("…and guiding resumed with the same calibration", g.IsGuiding && g.Calibration.When == when);
            from = g.History.Count;
            SpinUntil(() => g.History.Count >= from + 15, 20000);
            double flipped = Rms(g.History.Skip(from + 3));
            Info(string.Format("RMS after the flip {0:0.00}″", flipped));
            CheckTrue("Guiding after the flip holds the star (no runaway)", flipped < 1.5, flipped.ToString("0.00"));

            // a slew ends guiding
            tel.SlewRateDegPerSec = 5;
            tel.SlewToCoordinatesAsync(AstroMath.NormalizeHours(tel.SiderealTime + 1), 40);
            CheckTrue("A slew stops guiding", SpinUntil(() => !g.IsGuiding && g.State != SidraluneGuider.LostLock, 5000), g.State);
            SpinUntil(() => !tel.Slewing, 10000);
            g.StopGuiding();
            CheckTrue("No failures", problems.Count == 0, string.Join("; ", problems));
            CheckTrue("A guide log was written", Directory.GetFiles(dm.GuideLogFolder, "Guide *.csv").Any(f => File.ReadAllLines(f).Length > 30));
            dm.DisconnectAll();
        }

        [TestMethod]
        public void TestGuiderProblems()
        {
            var dm = GuideObservatory("guide-problems");
            var g = (SidraluneGuider)dm.Guider;
            var ct = CancellationToken.None;
            Func<Action, string> error = a => { try { a(); return null; } catch (AggregateException ex) { return ex.InnerException.Message; } catch (Exception ex) { return ex.Message; } };
            g.Settings.Output = GuideOutputKind.Camera;
            dm.Disconnect(DeviceKind.GuideCamera);
            CheckTrue("No guide camera", error(() => g.StartGuiding(false, ct).Wait()) == "No guide camera connected.");
            g.Settings.Output = GuideOutputKind.Mount;
            CheckTrue("…looping needs one too", error(() => g.Loop()) == "No guide camera connected.");
            dm.Connect(new DeviceInfo { Kind = DeviceKind.GuideCamera, Protocol = DeviceProtocol.Simulator, Name = "g", Address = "sim" });
            ((CameraSimulator)dm.GuideCamera).FocalLengthMm = 500;
            CheckTrue("Not guiding: no dither", error(() => g.Dither(2, false, 1, 1, 5, ct).Wait()) == "Not guiding.");

            // a mount that does not move: calibration fails and says why
            var tel = (TelescopeSimulator)dm.Telescope;
            Centering.Slew(tel, tel.SiderealTime - 0.5, 20, ct).Wait();
            tel.GuideRateRa = tel.GuideRateDec = 0;
            g.Settings.CalibrationStepMs = 50;
            g.Settings.ExposureSeconds = 0.1;
            string msg = error(() => g.StartGuiding(true, ct).Wait());
            CheckTrue("A mount that does not move fails calibration in RA", msg != null && msg.Contains("did not move far enough in RA"), msg);
            CheckTrue("…and the loop goes on", g.IsRunning && !g.IsGuiding);

            // Stop during a wait ends it with a reason
            tel.GuideRateRa = tel.GuideRateDec = 2 * TelescopeSimulator.SiderealDegPerSec;
            g.Settings.CalibrationStepMs = 0;
            var start = g.StartGuiding(true, ct);
            SpinUntil(() => g.IsCalibrating, 5000);
            g.StopGuiding();
            msg = error(() => start.Wait());
            CheckTrue("Stopping during calibration ends the start", msg == "Guiding was stopped.", msg);

            // the selected star follows a click
            g.Loop();
            SpinUntil(() => g.LastFrame != null, 5000);
            g.SelectStar();
            var s = g.Star;
            CheckTrue("Select star picks one", s != null && g.State == SidraluneGuider.Selected);
            CheckTrue("A click far from any star is refused", error(() => g.SelectStar(-500, -500)) == "No star there to guide on.");

            // a camera that stops delivering frames stops the guider with a message
            var problems = new List<string>();
            g.Failed += m => problems.Add(m);
            dm.GuideCamera.Disconnect();
            CheckTrue("A lost camera stops the guider", SpinUntil(() => g.State == SidraluneGuider.Stopped, 8000) && problems.Count == 1, string.Join(";", problems));
            dm.DisconnectAll();
        }

        private static double Gauss(Random r) => Math.Sqrt(-2 * Math.Log(1 - r.NextDouble())) * Math.Cos(2 * Math.PI * r.NextDouble());

        /// <summary>A star moving by drift pixels a frame, measured with noise, guided by one algorithm: the RMS of where it really was, and of the corrections.</summary>
        private static double[] Loop(IAxisAlgorithm alg, double drift, double noise, int frames, int seed)
        {
            var rng = new Random(seed);
            double p = 0, ss = 0, cs = 0;
            int n = 0;
            for (int i = 0; i < frames; i++)
            {
                p += drift;
                double c = alg.Correction(p + noise * Gauss(rng));
                alg.Sent(c);
                p -= c;
                if (i >= frames / 2) { ss += p * p; cs += c * c; n++; }
            }
            return new[] { Math.Sqrt(ss / n), Math.Sqrt(cs / n), p };
        }

        [TestMethod]
        public void TestGuideAlgorithmsStage2()
        {
            var lp = new Lowpass2Algorithm { MinMovePx = 0.2, Aggressiveness = 0.8 };
            Check("Lowpass2 leaves an error under the minimum move alone", lp.Correction(0.1), 0, 1e-12);
            lp.Reset();
            double last = 0;
            for (int i = 1; i <= 10; i++) last = lp.Correction(0.1 * i);
            Check("Lowpass2 follows a ramp: a share of the line's value now", last, 0.8 * 1.0, 1e-9);
            Check("…never more than the error just measured", new Lowpass2Algorithm { Aggressiveness = 1 }.Correction(0.5), 0.5, 1e-12);
            lp.Reset();
            for (int i = 0; i < 10; i++) lp.Correction(0.5);
            Check("A turn the other way of over five minimum moves starts the line again", lp.Correction(-1.5), -1.2, 1e-9);
            // seeing alone: the line averages it out, so it is corrected far less than with Hysteresis
            var rng = new Random(11);
            var noise = Enumerable.Range(0, 400).Select(i => 0.35 * Gauss(rng)).ToList();
            lp.Reset();
            var hy = new HysteresisAlgorithm { MinMovePx = 0.2, Hysteresis = 0.1, Aggressiveness = 0.8 };
            double lpSum = noise.Sum(e => Math.Abs(lp.Correction(e))), hySum = noise.Sum(e => Math.Abs(hy.Correction(e)));
            CheckTrue("Lowpass2 chases seeing far less than Hysteresis", lpSum < 0.6 * hySum, lpSum.ToString("0.0") + " vs " + hySum.ToString("0.0"));
            var lpDrift = Loop(new Lowpass2Algorithm { MinMovePx = 0.2, Aggressiveness = 0.8 }, 0.1, 0.1, 200, 4);
            CheckTrue("Lowpass2 holds a drifting star", lpDrift[0] < 0.5, lpDrift[0].ToString("0.00"));

            var z1 = new ZFilterAlgorithm { MinMovePx = 0, ExposureFactor = 1 };
            Check("Z-filter at factor 1 passes the error through", z1.Correction(0.7), 0.7, 1e-12);
            var zNoise = Loop(new ZFilterAlgorithm { MinMovePx = 0, ExposureFactor = 4 }, 0, 0.5, 400, 7);
            var hNoise = Loop(new HysteresisAlgorithm { MinMovePx = 0, Hysteresis = 0, Aggressiveness = 1 }, 0, 0.5, 400, 7);
            CheckTrue("Z-filter at factor 4 corrects much less of the seeing", zNoise[1] < 0.5 * hNoise[1], zNoise[1].ToString("0.00") + " vs " + hNoise[1].ToString("0.00"));
            var zDrift = Loop(new ZFilterAlgorithm { MinMovePx = 0.05, ExposureFactor = 4 }, 0.1, 0, 200, 3);
            CheckTrue("Z-filter keeps up with a steady drift, a little behind", zDrift[0] < 0.6 && Math.Abs(zDrift[2]) < 0.6, zDrift[0].ToString("0.00") + " " + zDrift[2].ToString("0.00"));

            var b = new BacklashCompensation { Enabled = true, AmountMs = 500, MinMs = 100, MaxMs = 800 };
            CheckTrue("No compensation before Dec has turned at all", b.Extra(1, -1) == 0);
            b.Pulsed(1);
            CheckTrue("…nor going on the same way", b.Extra(1, -1) == 0);
            b.Pulsed(1);
            Check("A turn adds the backlash (ms)", b.Extra(-1, 1), 500, 0);
            b.Pulsed(-1);
            b.Next(-0.8, 0.2);
            Check("An overshoot shortens it by 10 %", b.AmountMs, 450, 1e-9);
            b.Extra(1, -1); b.Pulsed(1);
            b.Next(-0.9, 0.2);
            Check("A turn that hardly moved the star lengthens it by 10 %", b.AmountMs, 495, 1e-9);
            b.AmountMs = 790;
            b.Extra(-1, 1); b.Pulsed(-1); b.Next(1, 0.2);
            Check("…up to the maximum", b.AmountMs, 800, 1e-9);
            b.Enabled = false;
            CheckTrue("Off: nothing added", b.Extra(1, -1) == 0);
        }

        [TestMethod]
        public void TestMultiStar()
        {
            var field = new List<Synthetic.SynthStar>
            {
                new Synthetic.SynthStar { X = 200, Y = 150, Flux = 0.45 },   // the guide star
                new Synthetic.SynthStar { X = 80, Y = 60, Flux = 0.3 }, new Synthetic.SynthStar { X = 320, Y = 70, Flux = 0.3 },
                new Synthetic.SynthStar { X = 90, Y = 230, Flux = 0.3 }, new Synthetic.SynthStar { X = 310, Y = 240, Flux = 0.3 },
                new Synthetic.SynthStar { X = 200, Y = 60, Flux = 0.3 }, new Synthetic.SynthStar { X = 120, Y = 150, Flux = 0.3 },
            };
            Func<double, double, int, Func<int, Synthetic.SynthStar, Synthetic.SynthStar>, AstroImage> frame = (sx, sy, seed, change) =>
                Field(field.Select((s, i) => change(i, new Synthetic.SynthStar { X = s.X + sx, Y = s.Y + sy, Flux = s.Flux })).Where(s => s != null), 0, seed);
            Func<int, Synthetic.SynthStar, Synthetic.SynthStar> same = (i, s) => s;
            var f0 = frame(0, 0, 100, same);
            var lum0 = f0.Luminance();
            var primary = GuideStars.Select(lum0, f0.Width, f0.Height, 10, double.NaN, double.NaN, 20);
            CheckTrue("The brightest single star is the guide star", primary != null && Math.Abs(primary.X - 200) < 0.5 && Math.Abs(primary.Y - 150) < 0.5, primary?.ToString());
            var multi = new GuideStarSet { SearchRadius = 10, MultiStar = true, MaxStars = 9, MassCheck = false };
            var single = new GuideStarSet { SearchRadius = 10, MultiStar = false, MassCheck = false };
            multi.SetPrimary(primary, lum0, f0.Width, f0.Height, 20);
            single.SetPrimary(primary, lum0, f0.Width, f0.Height, 20);
            CheckTrue("Six other stars are chosen with it", multi.Secondaries.Count == 6, multi.Secondaries.Count.ToString());
            double lx = primary.X, ly = primary.Y;

            // the same shifts measured with one star and with seven: the spread of the error
            var rng = new Random(9);
            var em = new List<double>(); var es = new List<double>();
            for (int k = 1; k <= 40; k++)
            {
                double sx = rng.NextDouble() * 2 - 1, sy = rng.NextDouble() * 2 - 1;
                var f = frame(sx, sy, 100 + k, same);
                var lum = f.Luminance();
                var rm = multi.Track(lum, f.Width, f.Height, lx, ly, true, 20);
                var rs = single.Track(lum, f.Width, f.Height, lx, ly, true, 20);
                if (!rm.Usable || !rs.Usable) { em.Add(99); continue; }
                em.Add(rm.Dx - sx); es.Add(rs.Dx - sx);
            }
            Func<List<double>, double> spread = v => Math.Sqrt(v.Average(x => (x - v.Average()) * (x - v.Average())));
            Info(string.Format("Error spread: seven stars {0:0.000} px, one star {1:0.000} px", spread(em), spread(es)));
            CheckTrue("Seven stars measure the shift better than one", spread(em) < 0.75 * spread(es), spread(em).ToString("0.000") + " vs " + spread(es).ToString("0.000"));

            // one star jumps (a hot pixel, a satellite next to it): it is left out
            var jump = frame(0.3, -0.2, 300, (i, s) => i == 2 ? new Synthetic.SynthStar { X = s.X + 4, Y = s.Y, Flux = s.Flux } : s);
            var rj = multi.Track(jump.Luminance(), jump.Width, jump.Height, lx, ly, true, 20);
            CheckTrue("A star that disagrees is left out", rj.Usable && Math.Abs(rj.Dx - 0.3) < 0.25 && rj.StarsUsed == 6, string.Format("{0:0.00} with {1} stars", rj.Dx, rj.StarsUsed));

            // the guide star disappears: the others carry on, and after ten frames the best of them takes over
            bool usable = true, promoted = false;
            for (int k = 0; k < 10; k++)
            {
                var f = frame(0.2, 0.1, 400 + k, (i, s) => i == 0 ? null : s);
                var r = multi.Track(f.Luminance(), f.Width, f.Height, lx, ly, true, 20);
                usable &= r.Usable && !r.PrimaryFound;
                if (r.Promoted) { promoted = true; lx = r.NewLockX; ly = r.NewLockY; }
                if (usable && !r.Promoted) CheckTrue("…the others still give the shift", Math.Abs(r.Dx - 0.2) < 0.25, r.Dx.ToString("0.00"));
            }
            CheckTrue("Without the guide star the others guide", usable);
            CheckTrue("After ten frames another star is the guide star", promoted && Math.Abs(multi.Primary.X - 200) + Math.Abs(multi.Primary.Y - 150) > 5, multi.Primary?.ToString());
            var after = frame(0.2, 0.1, 500, (i, s) => i == 0 ? null : s);
            var ra = multi.Track(after.Luminance(), after.Width, after.Height, lx, ly, true, 20);
            CheckTrue("…and the shift is the same from its lock position", ra.Usable && ra.PrimaryFound && Math.Abs(ra.Dx - 0.2) < 0.25 && Math.Abs(ra.Dy - 0.1) < 0.25, ra.Dx.ToString("0.00") + ", " + ra.Dy.ToString("0.00"));

            // star mass: one star alone, its brightness jumping
            var mass = new GuideStarSet { SearchRadius = 10, MultiStar = false, MassCheck = true, MassTolerance = 0.5 };
            mass.SetPrimary(primary, lum0, f0.Width, f0.Height, 20);
            for (int k = 0; k < 4; k++) { var f = frame(0, 0, 600 + k, same); mass.Track(f.Luminance(), f.Width, f.Height, primary.X, primary.Y, true, 20); }
            Func<int, Synthetic.SynthStar, Synthetic.SynthStar> bright = (i, s) => i == 0 ? new Synthetic.SynthStar { X = s.X, Y = s.Y, Flux = s.Flux * 3 } : s;
            var m1 = frame(0, 0, 700, bright); var r1 = mass.Track(m1.Luminance(), m1.Width, m1.Height, primary.X, primary.Y, true, 20);
            var m2 = frame(0, 0, 701, bright); var r2 = mass.Track(m2.Luminance(), m2.Width, m2.Height, primary.X, primary.Y, true, 20);
            var m3 = frame(0, 0, 702, bright); var r3 = mass.Track(m3.Luminance(), m3.Width, m3.Height, primary.X, primary.Y, true, 20);
            CheckTrue("A frame with the star three times as bright is not used", r1.PrimaryRejected && !r1.Usable && r2.PrimaryRejected && !r2.Usable);
            CheckTrue("…the third such frame in a row makes it the star's brightness", !r3.PrimaryRejected && r3.Usable && r3.Notes.Count > 0);
            // with other stars, a frame the guide star sits out is still guided on
            var withOthers = new GuideStarSet { SearchRadius = 10, MultiStar = true, MaxStars = 9, MassCheck = true, MassTolerance = 0.5 };
            withOthers.SetPrimary(primary, lum0, f0.Width, f0.Height, 20);
            for (int k = 0; k < 4; k++) { var f = frame(0, 0, 900 + k, same); withOthers.Track(f.Luminance(), f.Width, f.Height, primary.X, primary.Y, true, 20); }
            var m4 = frame(0.4, 0, 910, bright);
            var r4 = withOthers.Track(m4.Luminance(), m4.Width, m4.Height, primary.X, primary.Y, true, 20);
            CheckTrue("…the others still guide when the guide star sits a frame out", r4.PrimaryRejected && r4.Usable && r4.StarsUsed == 6 && Math.Abs(r4.Dx - 0.4) < 0.25, r4.StarsUsed + " stars, " + r4.Dx.ToString("0.00"));
        }

        [TestMethod]
        public void TestGuideDarksAndExposure()
        {
            var dm = GuideObservatory("guide-darks");
            var g = dm.BuiltinGuider;
            var cam = (CameraSimulator)dm.GuideCamera;
            var ct = CancellationToken.None;
            Func<Action, string> error = a => { try { a(); return null; } catch (AggregateException ex) { return ex.InnerException.Message; } catch (Exception ex) { return ex.Message; } };

            g.BuildDarkLibrary(new[] { 0.2, 0.5 }, 3, null, ct).Wait();
            var setup = g.SetupNow();
            g.Darks.Load(setup.Camera);
            CheckTrue("A dark library of two exposures that fits the guide camera", g.Darks.LibraryFits(setup) && g.Darks.Library.Exposures.Count == 2, setup.ToString());
            Check("The nearest dark to 0.3 s", g.Darks.Nearest(0.3), 0.2, 1e-9);
            var dark = CaptureService.GrabFrame(cam, 0.5, ct, false).Result;
            var d = dark.Data[0];
            double med = ImageStatistics.Compute(d, dark.Width, 0).Median;
            var hot = Enumerable.Range(0, d.Length).Where(i => d[i] > med + 0.001).ToList();
            CheckTrue("The simulated guide camera has about 60 hot pixels", hot.Count >= 55 && hot.Count <= 60, hot.Count.ToString());
            var treated = CaptureService.GrabFrame(cam, 0.5, ct, false).Result;
            double used = g.Darks.Apply(treated, DarkFrameKind.DarkLibrary, setup, 0.5);
            double tmed = ImageStatistics.Compute(treated.Data[0], treated.Width, 0).Median;
            CheckTrue("Subtracting the 0.5 s dark removes the hot pixels", used == 0.5 && hot.All(i => Math.Abs(treated.Data[0][i] - tmed) < 0.0003));

            var map = g.BuildBadPixelMap(1, 3, 5, null, ct).Result;
            // a few noise pixels pass five noise levels now and then, either way
            CheckTrue("A bad-pixel map finds the hot pixels", map.Hot >= hot.Count && map.Hot <= hot.Count + 4 && map.Cold <= 6, map.Hot + " hot, " + map.Cold + " cold");
            var mended = CaptureService.GrabFrame(cam, 0.5, ct, false).Result;
            g.Darks.Apply(mended, DarkFrameKind.BadPixelMap, setup, 0.5);
            double mmed = ImageStatistics.Compute(mended.Data[0], mended.Width, 0).Median;
            CheckTrue("…and mends them", hot.Count(i => Math.Abs(mended.Data[0][i] - mmed) < 0.0003) >= hot.Count - 2);

            g.Settings.Binning = 2;
            var binned = g.SetupNow();
            CheckTrue("Neither fits another binning", !g.Darks.LibraryFits(binned) && !g.Darks.MapFits(binned));
            g.Settings.Binning = 1;

            // the loop uses them
            g.Settings.DarkFrames = DarkFrameKind.DarkLibrary;
            g.Loop();
            CheckTrue("No dark frames while the guider runs", error(() => g.BuildDarkLibrary(new[] { 0.2 }, 1, null, ct).Wait()) == "Stop the guider before taking dark frames.");
            SpinUntil(() => g.LastFrame != null, 5000);
            Thread.Sleep(600);
            var lf = g.LastFrame;
            double lmed = ImageStatistics.Compute(lf.Data[0], lf.Width, 0).Median;
            CheckTrue("Guide frames have the dark subtracted", hot.Count(i => Math.Abs(lf.Data[0][i] - lmed) < 0.0003) >= hot.Count - 3);

            // auto exposure: down when the star is bright enough, up when it is not
            g.Settings.AutoExposure = true; g.Settings.AutoExposureMin = 0.5; g.Settings.AutoExposureMax = 1; g.Settings.TargetSnr = 1;
            g.SelectStar();
            CheckTrue("Auto exposure starts at the maximum", g.CurrentExposure == 1);
            CheckTrue("A bright star shortens it", SpinUntil(() => g.CurrentExposure == 0.5, 12000), g.CurrentExposure.ToString());
            g.Settings.TargetSnr = 100000;
            CheckTrue("A star too faint for the target lengthens it", SpinUntil(() => g.CurrentExposure == 1, 12000), g.CurrentExposure.ToString());
            g.Settings.AutoExposure = false;
            CheckTrue("Off: the exposure set", g.CurrentExposure == g.Settings.ExposureSeconds);
            g.StopGuiding();
            dm.DisconnectAll();
        }

        [TestMethod]
        public void TestGuidingAssistant()
        {
            var dm = GuideObservatory("guide-assistant");
            var tel = (TelescopeSimulator)dm.Telescope;
            var g = (SidraluneGuider)dm.Guider;
            var cam = (CameraSimulator)dm.GuideCamera;
            var ct = CancellationToken.None;
            Centering.Slew(tel, tel.SiderealTime - 0.5, 30, ct).Wait();
            g.StartGuiding(false, ct).Wait();
            CheckTrue("Guiding", g.IsGuiding);
            var ga = new GuidingAssistant(g);
            // seeing, and a pole far enough off to measure in half a minute
            cam.JitterArcsec = 0.6;
            tel.DriftDecArcsecPerMin = 24;
            ga.Start();
            CheckTrue("The assistant pauses corrections", g.State == SidraluneGuider.Paused);
            Thread.Sleep(25000);
            var live = ga.Current;
            // about 60 frames in 25 s, fewer on a busy machine
            CheckTrue("Live numbers while it measures", live.Frames > 40 && !double.IsNaN(live.SeeingRaArcsec), live.Frames.ToString());
            // a quiet sky and real backlash for the backlash test
            cam.JitterArcsec = 0.05;
            tel.DriftDecArcsecPerMin = 0;
            tel.DecBacklashMs = 800;
            var r = ga.Stop(true, ct).Result;
            double seeing = 0.6 / Math.Sqrt(0.2);
            Info(string.Format("Seeing RA {0:0.00}\" Dec {1:0.00}\" (simulated {2:0.00}\"), Dec drift {3:0.0}\"/min, polar error {4:0}', backlash {5:0} ± {6:0} ms",
                r.SeeingRaArcsec, r.SeeingDecArcsec, seeing, r.DriftDecArcsecPerMin, r.PolarErrorArcmin, r.BacklashMs, r.BacklashUncertaintyMs));
            Check("Seeing in RA (″)", r.SeeingRaArcsec, seeing, 0.3 * seeing);
            Check("Seeing in Dec (″)", r.SeeingDecArcsec, seeing, 0.3 * seeing);
            Check("Dec drift (″/min)", Math.Abs(r.DriftDecArcsecPerMin), 24, 7);
            CheckTrue("No RA drift to speak of", Math.Abs(r.DriftRaArcsecPerMin) < 5, r.DriftRaArcsecPerMin.ToString("0.0"));
            Check("Polar alignment error from the Dec drift (′)", r.PolarErrorArcmin, Math.Abs(r.DriftDecArcsecPerMin) * 3.81, 0.5);
            CheckTrue("Backlash measured", r.BacklashMeasured && r.BacklashValid, r.BacklashProblem);
            Check("Dec backlash (ms)", r.BacklashMs, 800, 200);
            CheckTrue("Guiding resumes", SpinUntil(() => g.IsGuiding, 5000), g.State);

            var recs = GuidingAssistant.Recommend(r, g.Settings);
            Info(string.Join(" | ", recs.Select(x => x.Text)));
            var st = new GuiderSettings();
            foreach (var x in recs) x.Apply?.Invoke(st);
            Check("Recommended RA minimum move (px)", st.RaMinMovePx, 0.65 * r.SeeingRaArcsec / g.PixelScale, 0.02);
            CheckTrue("Backlash compensation is recommended at the measured amount", st.BacklashCompensation && Math.Abs(st.BacklashMs - r.BacklashMs) <= 10 && st.BacklashMaxMs > st.BacklashMs);
            CheckTrue("Polar alignment advice", recs.Any(x => x.Apply == null && x.Text.Contains("Polar alignment")));
            CheckTrue("The result is kept", GuiderSettings.Load(dm.GuiderSettingsPath).AssistantResults.Count == 1);
            g.StopGuiding();
            dm.DisconnectAll();
        }

        [TestMethod]
        public void TestCalibrationAssistant()
        {
            double ha, dec;
            CalibrationAssistant.ProposeFor(1, 52, out ha, out dec);
            CheckTrue("West of the meridian: 5° west, on the equator at 52° N", Math.Abs(ha - 1 / 3.0) < 1e-9 && dec == 0);
            CalibrationAssistant.ProposeFor(23, 52, out ha, out dec);
            CheckTrue("East of the meridian: 5° east", Math.Abs(ha + 1 / 3.0) < 1e-9 && dec == 0);
            CalibrationAssistant.ProposeFor(1, 66, out ha, out dec);
            CheckTrue("At 66° N the equator is low: raised until 30° up", dec > 0 && CalibrationAssistant.Altitude(ha, dec, 66) >= 30 && CalibrationAssistant.Altitude(ha, dec - 1, 66) < 30, dec.ToString());
            CalibrationAssistant.ProposeFor(1, -70, out ha, out dec);
            CheckTrue("…towards the south pole in the south", dec < 0 && CalibrationAssistant.Altitude(ha, dec, -70) >= 30, dec.ToString());

            var dm = GuideObservatory("guide-cal-assistant");
            var tel = (TelescopeSimulator)dm.Telescope;
            var g = (SidraluneGuider)dm.Guider;
            var ct = CancellationToken.None;
            Centering.Slew(tel, AstroMath.NormalizeHours(tel.SiderealTime - 2), 50, ct).Wait();
            double ra0 = tel.RightAscension;
            var ca = new CalibrationAssistant(dm, g);
            ca.Propose();
            CheckTrue("Proposed on the same side of the meridian, on the equator", Math.Abs(ca.TargetHaHours - 1 / 3.0) < 1e-9 && ca.TargetDecDeg == 0);
            var checks = ca.Checks();
            CheckTrue("The simulator passes the checks", checks.Count == 0, string.Join(" ", checks));
            ca.Run(ct).Wait();
            double haNow = AstroMath.NormalizeDeg180(AstroMath.NormalizeHours(tel.SiderealTime - tel.RightAscension) * 15) / 15;
            CheckTrue("It calibrated near the meridian and the equator", Math.Abs(haNow - 1 / 3.0) < 0.1 && Math.Abs(tel.Declination) < 0.5, haNow.ToString("0.00") + " h, " + tel.Declination.ToString("0.0") + "°");
            var rating = CalibrationAssistant.Rate(g.Calibration);
            CheckTrue("The simulator calibrates well", rating == CalibrationRating.Good, string.Join(" ", g.Calibration.Issues));
            CheckTrue("…and guiding stopped after", g.State == SidraluneGuider.Stopped);
            ca.Return(ct).Wait();
            CheckTrue("Return slews back", Math.Abs(tel.Declination - 50) < 0.01 && Math.Abs(AstroMath.NormalizeDeg180((tel.RightAscension - ra0) * 15)) < 0.01);
            var poor = SampleCalibration();
            poor.DecAngleDeg = 150; poor.Check();
            CheckTrue("Axes far from square rate poor", CalibrationAssistant.Rate(poor) == CalibrationRating.Poor && poor.Issues.Count == 1);
            var few = SampleCalibration();
            few.RaSteps = 3; few.Check();
            CheckTrue("Only few steps rates acceptable", CalibrationAssistant.Rate(few) == CalibrationRating.Acceptable);
            dm.DisconnectAll();
        }

        private static double TrueArcsec(Func<double> read)
        {
            double s = 0;
            for (int i = 0; i < 5; i++) { s += read(); Thread.Sleep(30); }
            return s / 5;
        }

        [TestMethod]
        public void TestGuiderTools()
        {
            var dm = GuideObservatory("guide-tools");
            var tel = (TelescopeSimulator)dm.Telescope;
            var g = (SidraluneGuider)dm.Guider;
            var ct = CancellationToken.None;
            Func<Action, string> error = a => { try { a(); return null; } catch (AggregateException ex) { return ex.InnerException.Message; } catch (Exception ex) { return ex.Message; } };

            // a comet's rates from its orbit agree with its places ten minutes apart
            var comet = new MinorBody { Name = "Test comet", Designation = "C/2026 T1", IsComet = true, Elements = OrbitalElements.FromMeanAnomaly(2461200.5, 3.1, 0.6, 12, 80, 120, 10) };
            var now = DateTime.UtcNow;
            double cra, cdec;
            CometRates.Compute(comet, dm.Site, now, out cra, out cdec);
            var pa = MinorBodyStore.Place(new SkyContext(dm.Site, AstroTime.FromDateTime(now.AddMinutes(-5))), comet).Apparent;
            var pb = MinorBodyStore.Place(new SkyContext(dm.Site, AstroTime.FromDateTime(now.AddMinutes(5))), comet).Apparent;
            double fra = (AstroMath.NormalizeRad(pb.Ra - pa.Ra + Math.PI) - Math.PI) * Math.Cos(pb.Dec) * AstroMath.Rad2ArcSec * 6, fdec = (pb.Dec - pa.Dec) * AstroMath.Rad2ArcSec * 6;
            Info(string.Format("Comet rates RA {0:0.0}\"/h Dec {1:0.0}\"/h", cra, cdec));
            CheckTrue("Comet rates from its orbit (″/h)", Math.Abs(cra - fra) < 0.01 * Math.Abs(fra) + 0.5 && Math.Abs(cdec - fdec) < 0.01 * Math.Abs(fdec) + 0.5, fra.ToString("0.0") + ", " + fdec.ToString("0.0"));

            Centering.Slew(tel, tel.SiderealTime - 0.5, 30, ct).Wait();
            CheckTrue("Manual pulses need the guider looping", error(() => g.ManualPulse(GuideDirection.West, 300, ct).Wait()) == "Click Loop first, so the pulse's effect can be seen.");
            g.StartGuiding(false, ct).Wait();
            CheckTrue("…and not guiding", error(() => g.ManualPulse(GuideDirection.West, 300, ct).Wait()) == "Manual pulses are for looping, not while calibrating or guiding.");
            CheckTrue("The calibration knows which way North and West move the mount", g.Calibration.NorthSign == 1 && g.Calibration.WestSign == 1, g.Calibration.NorthSign + " " + g.Calibration.WestSign);

            // a new lock position while guiding: the star is guided there
            double lx = g.LockX + 4, ly = g.LockY - 3;
            g.SetLockPosition(lx, ly);
            CheckTrue("The star is guided to a new lock position", SpinUntil(() => Math.Abs(g.Star.X - lx) < 0.6 && Math.Abs(g.Star.Y - ly) < 0.6, 10000), g.Star.ToString());
            // nudges move the telescope on the sky
            g.Settings.NudgeArcsec = 5;
            Thread.Sleep(1500);
            double dec0 = TrueArcsec(() => tel.TrueDec * 3600), ra0 = TrueArcsec(() => tel.TrueRa * 15 * 3600);
            g.NudgeLock(GuideDirection.North);
            g.NudgeLock(GuideDirection.East);
            Thread.Sleep(4000);
            double dec1 = TrueArcsec(() => tel.TrueDec * 3600), ra1 = TrueArcsec(() => tel.TrueRa * 15 * 3600);
            // Resist Switch leaves Dec errors up to about 0.9″ uncorrected, so a whole step is measured to 1″
            Check("North 5″ moves the telescope 5″ north", dec1 - dec0, 5, 1);
            Check("East 5″ moves it 5″ east", (ra1 - ra0) * Math.Cos(dec1 / 3600 * AstroMath.Deg2Rad), 5, 1);

            // Dec guide modes and Lowpass2
            int from = g.History.Count;
            g.Settings.DecMode = DecGuideMode.Off;
            SpinUntil(() => g.History.Count >= from + 8, 10000);
            CheckTrue("Dec guide mode Off sends no Dec pulses", g.History.Skip(from + 1).All(s => s.DecPulse == 0));
            g.Settings.DecMode = DecGuideMode.North;
            from = g.History.Count;
            SpinUntil(() => g.History.Count >= from + 8, 10000);
            CheckTrue("North only sends no South pulses", g.History.Skip(from + 1).All(s => s.DecPulse >= 0));
            g.Settings.DecMode = DecGuideMode.Auto;
            g.Settings.DecAlgorithm = GuideAlgorithmKind.Lowpass2;
            tel.DriftDecArcsecPerMin = 30;
            from = g.History.Count;
            SpinUntil(() => g.History.Count >= from + 30, 20000);
            double decRms = Math.Sqrt(g.History.Skip(from + 10).Average(s => s.DecArcsec * s.DecArcsec));
            CheckTrue("Lowpass2 holds Dec against a drift of 30″ a minute", decRms < 1.0, decRms.ToString("0.00"));
            tel.DriftDecArcsecPerMin = 0;
            g.Settings.DecAlgorithm = GuideAlgorithmKind.ResistSwitch;

            // comet tracking at entered rates: the telescope follows them
            g.Settings.CometSource = CometRateSource.Entered;
            g.Settings.CometRaArcsecPerHour = 1800; g.Settings.CometDecArcsecPerHour = -3600;
            g.FollowComet(true);
            Thread.Sleep(3000);
            // RA in arcseconds of RA; the sky motion is its change times cos(dec)
            dec0 = TrueArcsec(() => tel.TrueDec * 3600); ra0 = TrueArcsec(() => tel.TrueRa * 15 * 3600);
            var sw = Stopwatch.StartNew();
            Thread.Sleep(12000);
            dec1 = TrueArcsec(() => tel.TrueDec * 3600); ra1 = TrueArcsec(() => tel.TrueRa * 15 * 3600);
            double hours = sw.Elapsed.TotalHours;
            double east = (ra1 - ra0) * Math.Cos(dec1 / 3600 * AstroMath.Deg2Rad);
            Info(string.Format("Comet: {0:0.00}″ east and {1:0.00}″ north in {2:0.0} s", east, dec1 - dec0, hours * 3600));
            Check("The telescope follows the comet in RA (″/h)", east / hours, 1800, 270);
            Check("…and in Dec (″/h)", (dec1 - dec0) / hours, -3600, 540);
            g.FollowComet(false);
            g.Settings.CometSource = CometRateSource.Comet; g.Settings.CometName = "No such comet";
            CheckTrue("A comet not in the list is said", error(() => g.FollowComet(true)).StartsWith("The comet No such comet is not in the comet list."));

            // training: the lock moved east 1″ a second for twelve seconds
            g.Settings.NudgeArcsec = 1;
            g.StartCometTraining();
            sw.Restart();
            for (int i = 0; i < 12; i++) { g.NudgeLock(GuideDirection.East); Thread.Sleep(1000); }
            hours = sw.Elapsed.TotalHours;
            g.StopCometTraining();
            Check("Trained RA rate (″/h)", g.Settings.CometRaArcsecPerHour, 12 / hours, 0.05 * 12 / hours);
            CheckTrue("…no Dec rate", Math.Abs(g.Settings.CometDecArcsecPerHour) < 0.02 * 12 / hours && g.Settings.CometSource == CometRateSource.Trained, g.Settings.CometDecArcsecPerHour.ToString("0.0"));

            // a sticky lock position comes back after a stop
            g.Settings.StickyLock = true;
            g.SetLockPosition(g.LockX + 2, g.LockY);
            double sx = g.LockX, sy = g.LockY;
            g.StopGuiding();
            CheckTrue("The sticky lock position is kept", g.Settings.StickyLockX == sx && GuiderSettings.Load(dm.GuiderSettingsPath).StickyLockY == sy);
            g.StartGuiding(false, ct).Wait();
            CheckTrue("Guiding again holds the stars at the sticky lock position", g.LockX == sx && g.LockY == sy && g.IsGuiding, g.LockX.ToString("0.00"));
            // a slew slow enough for the guider to see it between frames
            tel.SlewRateDegPerSec = 5;
            tel.SlewToCoordinatesAsync(AstroMath.NormalizeHours(tel.SiderealTime - 1), 40);
            SpinUntil(() => !g.IsGuiding, 5000);
            SpinUntil(() => !tel.Slewing, 10000);
            CheckTrue("A slew forgets it", SpinUntil(() => double.IsNaN(g.Settings.StickyLockX), 3000));
            g.Settings.StickyLock = false;

            // manual pulses while looping, measured against the calibration, on a star in a frame taken after the slew
            var before = g.LastFrame;
            SpinUntil(() => g.LastFrame != before && g.State == SidraluneGuider.Looping, 5000);
            g.SelectStar();
            var mp = g.ManualPulse(GuideDirection.West, 300, ct).Result;
            Info(string.Format("West 300 ms: {0:0.00} px towards {1:0}° (expected {2:0.00} px towards {3:0}°)", mp.Distance, mp.AngleDeg, mp.ExpectedDistance, mp.ExpectedAngleDeg));
            CheckTrue("A West pulse moves the star as calibrated", mp.Matches == true);
            double expected = 2 * 15.0410686 * Math.Cos(40 * AstroMath.Deg2Rad) * 0.3 / g.PixelScale;
            Check("…by the guide rate (px)", mp.Distance, expected, 0.25 * expected);
            var mn = g.ManualPulse(GuideDirection.North, 300, ct).Result;
            CheckTrue("A North pulse too", mn.Matches == true, mn.Distance.ToString("0.00"));
            g.StopGuiding();
            dm.DisconnectAll();
        }

        [TestMethod]
        public void TestEquipmentProfiles()
        {
            // the model: one device per kind, focal lengths through a reducer and an off-axis guider, copies of their own
            var p = new EquipmentProfile { Name = "Refractor", FocalFactor = 0.8, Guiding = GuideOptics.GuideScope, GuideFocalLengthMm = 180 };
            p.Remember(new DeviceInfo { Kind = DeviceKind.Telescope, Protocol = DeviceProtocol.Simulator, Name = "Mount A", Address = "sim" });
            p.Remember(new DeviceInfo { Kind = DeviceKind.Telescope, Protocol = DeviceProtocol.Simulator, Name = "Mount B", Address = "sim" });
            p.Remember(new DeviceInfo { Kind = DeviceKind.GuideCamera, Protocol = DeviceProtocol.Simulator, Name = "Guide camera", Address = "sim" });
            CheckTrue("A profile remembers one device per kind, the last connected", p.Devices.Count == 2 && p.DeviceFor(DeviceKind.Telescope).Name == "Mount B");
            Check("The imaging focal length goes through the reducer (mm)", p.ImagingFocalLength(600), 480, 1e-9);
            Check("With a guide scope the guide camera has its focal length (mm)", p.GuideFocalLength(480), 180, 1e-9);
            p.Guiding = GuideOptics.OffAxisGuider;
            Check("Through an off-axis guider it has the imaging focal length (mm)", p.GuideFocalLength(480), 480, 1e-9);
            var copy = p.Copy("Refractor 2", EquipmentProfile.NewGuiderFile(new[] { p }));
            copy.Remember(new DeviceInfo { Kind = DeviceKind.Camera, Protocol = DeviceProtocol.Simulator, Name = "Camera", Address = "sim" });
            CheckTrue("A copy has its own devices and guider settings file", p.Devices.Count == 2 && copy.Devices.Count == 3 && copy.GuiderFile != p.GuiderFile && copy.Name == "Refractor 2");
            var back = (EquipmentProfile)Core.Util.Json.PopulateType(typeof(EquipmentProfile), Core.Util.Json.ParseObject(Core.Util.Json.Serialize(copy)));
            CheckTrue("A profile is saved and read back", back.Name == copy.Name && back.Guiding == GuideOptics.OffAxisGuider && back.FocalFactor == 0.8 && back.Devices.Count == 3
                && back.DeviceFor(DeviceKind.Camera).Protocol == DeviceProtocol.Simulator && back.GuiderFile == copy.GuiderFile);

            // the observatory takes the rig's optics; the guider its focal length, also when it is made later
            var dm = GuideObservatory("equipment-profiles");
            string dir = Path.GetDirectoryName(dm.GuiderSettingsPath);
            try
            {
                dm.ApplyOptics(480, 80, 180);
                var g = dm.BuiltinGuider;
                CheckTrue("The imaging optics and the guide focal length come from the profile", dm.FocalLengthMm == 480 && dm.ApertureMm == 80 && g.Settings.FocalLengthMm == 180
                    && GuiderSettings.Load(dm.GuiderSettingsPath).FocalLengthMm == 180);
                var fresh = new DeviceManager { GuiderSettingsPath = Path.Combine(dir, "fresh.json"), GuideLogFolder = dir };
                fresh.ApplyOptics(600, 100, 600);
                CheckTrue("…also for a guider made afterwards", fresh.BuiltinGuider.Settings.FocalLengthMm == 600);

                // each profile has its own guider settings: switching files brings the other rig's settings and calibration
                g.Settings.ExposureSeconds = 1.5; g.SaveSettings();
                string second = Path.Combine(dir, "guider-2.json");
                if (File.Exists(second)) File.Delete(second);
                bool replaced = false;
                g.SettingsReplaced += () => replaced = true;
                dm.UseGuiderSettings(second);
                g.Settings.ExposureSeconds = 3; g.SaveSettings();
                CheckTrue("Another profile's guider settings are used", replaced && g.SettingsPath == second && g.Settings.ExposureSeconds == 3 && GuiderSettings.Load(second).ExposureSeconds == 3);
                CheckTrue("…and the guide focal length stays the profile's", g.Settings.FocalLengthMm == 180);
                dm.UseGuiderSettings(Path.Combine(dir, "guider.json"));
                CheckTrue("Back to the first profile's", g.Settings.ExposureSeconds == 1.5);
                g.Loop();
                bool refused = false;
                try { dm.UseGuiderSettings(second); } catch (InvalidOperationException) { refused = true; }
                CheckTrue("Not while the guider takes frames", refused && g.SettingsPath == Path.Combine(dir, "guider.json"));
                g.StopGuiding();

                // Connect all: the remembered devices not connected yet; one that fails does not stop the others
                dm.DisconnectAll();
                var rig = new EquipmentProfile();
                rig.Remember(new DeviceInfo { Kind = DeviceKind.Telescope, Protocol = DeviceProtocol.Simulator, Name = "Telescope", Address = "sim" });
                rig.Remember(new DeviceInfo { Kind = DeviceKind.Focuser, Protocol = DeviceProtocol.Ascom, Name = "No such focuser", Address = "Sidralune.NoSuchFocuser" });
                rig.Remember(new DeviceInfo { Kind = DeviceKind.Camera, Protocol = DeviceProtocol.Simulator, Name = "Camera", Address = "sim" });
                var failed = dm.ConnectAll(rig.Devices);
                Info("Failed: " + string.Join("; ", failed));
                CheckTrue("Connect all connects the profile's devices", dm.Telescope != null && dm.Telescope.Connected && dm.Camera != null && dm.Camera.Connected);
                CheckTrue("…and says which could not connect", failed.Count == 1 && failed[0].StartsWith("No such focuser") && dm.Focuser == null);
                var mount = dm.Telescope;
                dm.ConnectAll(rig.Devices);
                CheckTrue("…leaving those already connected alone", dm.Telescope == mount);
            }
            finally { dm.DisconnectAll(); }
        }

        [TestMethod]
        public void TestGuidingAroundImaging()
        {
            // the dither spiral: a square spiral out from the centre, or along RA alternating sides
            var square = Enumerable.Range(1, 9).Select(k => { int i, j; SidraluneGuider.SpiralPoint(k, false, out i, out j); return i + "," + j; }).ToList();
            CheckTrue("The spiral turns out from the centre", string.Join(" ", square) == "1,0 1,1 0,1 -1,1 -1,0 -1,-1 0,-1 1,-1 2,-1", string.Join(" ", square));
            var line = Enumerable.Range(1, 4).Select(k => { int i, j; SidraluneGuider.SpiralPoint(k, true, out i, out j); return i + "," + j; }).ToList();
            CheckTrue("…and along RA only, side to side", string.Join(" ", line) == "1,0 -1,0 2,0 -2,0", string.Join(" ", line));

            var dm = GuideObservatory("guide-imaging");
            string dir = Path.GetDirectoryName(dm.GuiderSettingsPath);
            dm.Connect(new DeviceInfo { Kind = DeviceKind.Camera, Protocol = DeviceProtocol.Simulator, Name = "Camera", Address = "sim" });
            var tel = (TelescopeSimulator)dm.Telescope;
            var g = dm.BuiltinGuider;
            var ct = CancellationToken.None;
            var problems = new List<string>();
            g.Failed += m => problems.Add(m);
            try
            {
                Centering.Slew(tel, tel.SiderealTime - 0.5, 30, ct).Wait();
                g.StartGuiding(false, ct).Wait();
                CheckTrue("Guiding", g.IsGuiding);
                var cal = g.Calibration;
                var pier = tel.SideOfPier;
                Func<double, double, double[]> axes = (x, y) => { double ra, dec; cal.ToAxes(x, y, pier, g.Settings.MountReversesDecAfterFlip, out ra, out dec); return new[] { ra, dec }; };

                // spiral dithers: each a step of the amount along the spiral round where it began
                g.Settings.DitherMode = DitherPattern.Spiral;
                double x0 = g.LockX, y0 = g.LockY;
                var places = new List<string>();
                for (int k = 0; k < 3; k++)
                {
                    g.Dither(3, false, 0.6, 1, 30, ct).Wait();
                    var a = axes(g.LockX - x0, g.LockY - y0);
                    places.Add(Math.Round(a[0] / 3) + "," + Math.Round(a[1] / 3));
                }
                CheckTrue("Spiral dithers step out along the spiral", string.Join(" ", places) == "1,0 1,1 0,1", string.Join(" ", places));
                g.Settings.DitherMode = DitherPattern.Random;

                // autofocus pauses guiding, which settles again afterwards
                string during = null;
                GuidingPause.During(g, async () => { await Task.Delay(1500); during = g.State; return 0; }, ct).Wait();
                CheckTrue("Guiding is paused while the work runs", during == SidraluneGuider.Paused, during);
                CheckTrue("…and guides again afterwards", g.IsGuiding);

                // centring the same field: guiding stops for the slews, and starts again on the star held at the old lock position
                double lx = g.LockX, ly = g.LockY, ra0 = tel.RightAscension, dec0 = tel.Declination;
                GuidingPause.AroundSlew(g, tel, async () =>
                {
                    await Centering.Slew(tel, ra0, dec0 + 0.05, ct);
                    await Centering.Slew(tel, ra0, dec0, ct);
                    return 0;
                }, true, ct).Wait();
                CheckTrue("After centring the same field, guiding goes on at the old lock position", g.IsGuiding && Math.Abs(g.LockX - lx) < 1e-6 && Math.Abs(g.LockY - ly) < 1e-6,
                    string.Format("{0:0.00},{1:0.00} vs {2:0.00},{3:0.00}", g.LockX, g.LockY, lx, ly));

                // a guide camera that stops answering is connected again, and guiding goes on
                var old = dm.GuideCamera;
                ((CameraSimulator)old).Unplugged = true;
                CheckTrue("A guide camera that stops answering is connected again", SpinUntil(() => dm.GuideCamera != old && dm.GuideCamera.Connected, 30000));
                CheckTrue("…and guiding goes on", SpinUntil(() => g.IsGuiding, 20000) && problems.Count == 0, g.State + " " + string.Join("; ", problems));
                // the camera connected again is a new simulator: as steady as the test observatory's, so dithers settle quickly
                ((CameraSimulator)dm.GuideCamera).JitterArcsec = 0.1;

                // a sequence: dithered as soon as a frame is read out, before it is analysed and saved
                var capture = new CaptureService { Devices = dm, ImageFolder = Path.Combine(dir, "Images") };
                var log = new List<string>();
                var ctx = new SequenceContext { Devices = dm, Capture = capture, Log = m => { lock (log) log.Add(m); }, GuidingWanted = true };
                var locks = new List<double[]>();
                capture.ImageCaptured += r => locks.Add(new[] { g.LockX, g.LockY });
                double sx = g.LockX, sy = g.LockY;
                var shots = new TakeExposures { Count = 3, Exposure = 1, DitherEvery = 1, DitherPixels = 2, FrameType = "LIGHT" };
                var sw = Stopwatch.StartNew();
                shots.Run(ctx, ct).Wait();
                Info(string.Format("Three frames with two dithers in {0:0.0} s", sw.Elapsed.TotalSeconds));
                Func<double[], double[], bool> moved = (p1, p2) => Math.Abs(p1[0] - p2[0]) + Math.Abs(p1[1] - p2[1]) > 0.01;
                CheckTrue("The dither began before the frame was analysed and saved", shots.Status == ItemStatus.Done && locks.Count == 3 && moved(locks[0], new[] { sx, sy }) && moved(locks[1], locks[0]));
                CheckTrue("…not after the last frame", !moved(locks[2], locks[1]));

                // guiding stopped (a slew elsewhere): the next light frame starts it again
                g.StopGuiding();
                new TakeExposures { Count = 1, Exposure = 1, FrameType = "LIGHT" }.Run(ctx, ct).Wait();
                CheckTrue("A stopped guider is started again before a light frame", g.IsGuiding && log.Any(m => m.StartsWith("Guiding has stopped")));

                // clouds: the light frame waits for the star to come back
                var cam = (CameraSimulator)dm.GuideCamera;
                cam.Transparency = 0;
                CheckTrue("Behind a cloud the star is lost", SpinUntil(() => g.State == SidraluneGuider.LostLock, 10000), g.State);
                int before = locks.Count;
                var waiting = new TakeExposures { Count = 1, Exposure = 1, FrameType = "LIGHT", DitherEvery = 0 }.Run(ctx, ct);
                Thread.Sleep(3000);
                CheckTrue("The frame waits while the star is lost", !waiting.IsCompleted && locks.Count == before && log.Any(m => m.StartsWith("The guide star is lost")));
                cam.Transparency = 1;
                CheckTrue("…and is taken once guiding is back", waiting.Wait(30000) && locks.Count == before + 1 && log.Contains("Guiding again"));
                CheckTrue("No guider failures", problems.Count == 0, string.Join("; ", problems));
            }
            finally
            {
                g.Settings.DitherMode = DitherPattern.Random;
                g.StopGuiding();
                dm.DisconnectAll();
            }
        }

        [TestMethod]
        public void TestGuidingQualityInFrames()
        {
            // the numbers from the guide steps of one exposure
            var st = GuidingStats.From(new List<GuideStep> { new GuideStep { RaArcsec = 0.3, DecArcsec = -0.4 }, new GuideStep { RaArcsec = -0.3, DecArcsec = 0 } }, false);
            CheckTrue("RMS per axis, in total and the peak", Math.Abs(st.RmsRa - 0.3) < 1e-9 && Math.Abs(st.RmsDec - Math.Sqrt(0.08)) < 1e-9
                && Math.Abs(st.RmsTotal - Math.Sqrt(0.17)) < 1e-9 && Math.Abs(st.Peak - 0.5) < 1e-9 && !st.Interrupted);
            CheckTrue("No limit, or within it, is not bad; over it, or interrupted, is", !st.WorseThan(0) && !st.WorseThan(0.5) && st.WorseThan(0.4)
                && GuidingStats.From(new List<GuideStep>(), false).WorseThan(5) && !GuidingStats.From(new List<GuideStep>(), false).WorseThan(0));

            var dm = GuideObservatory("guide-quality");
            string dir = Path.GetDirectoryName(dm.GuiderSettingsPath);
            dm.Connect(new DeviceInfo { Kind = DeviceKind.Camera, Protocol = DeviceProtocol.Simulator, Name = "Camera", Address = "sim" });
            var tel = (TelescopeSimulator)dm.Telescope;
            var g = dm.BuiltinGuider;
            var ct = CancellationToken.None;
            var capture = new CaptureService { Devices = dm, ImageFolder = Path.Combine(dir, "Images") };
            try
            {
                // unguided: no guiding numbers
                Centering.Slew(tel, tel.SiderealTime - 0.5, 30, ct).Wait();
                var res = capture.Expose(new ExposureRequest { Seconds = 1, Target = "Q" }, ct).Result;
                CheckTrue("An unguided frame has no guiding numbers", res.Guiding == null && !res.Image.Header.Has("GUIDRMS"));

                g.StartGuiding(false, ct).Wait();
                CheckTrue("Guiding", g.IsGuiding);
                res = capture.Expose(new ExposureRequest { Seconds = 2, Target = "Q" }, ct).Result;
                var gs = res.Guiding;
                CheckTrue("A guided frame has the guiding of its exposure", gs != null && gs.Steps >= 4 && gs.Steps <= 12 && gs.RmsTotal > 0 && gs.RmsTotal < 1 && gs.Peak >= gs.RmsTotal * 0.99 && !gs.Interrupted && !gs.Bad,
                    gs == null ? "none" : gs.Steps + " steps, " + gs.RmsTotal.ToString("0.000"));
                var saved = Sidralune.Imaging.IO.ImageFile.Load(res.Path);
                CheckTrue("…in its FITS header", Math.Abs(saved.Header.GetDouble("GUIDRMS") - Math.Round(gs.RmsTotal, 3)) < 1e-9 && saved.Header.Has("GUIDRMSR") && saved.Header.Has("GUIDRMSD")
                    && saved.Header.Has("GUIDPEAK") && saved.Header.GetInt("GUIDSTPS") == gs.Steps && !saved.Header.Has("GUIDBAD") && !saved.Header.Has("GUIDLOST"));
                res = capture.Expose(new ExposureRequest { Seconds = 1, Light = false, FrameType = "DARK", Target = "Q" }, ct).Result;
                CheckTrue("A dark frame has none", res.Guiding == null);

                // over the limit: flagged in the header and the file name
                res = capture.Expose(new ExposureRequest { Seconds = 1, Target = "Q", MaxGuidingRms = 0.001 }, ct).Result;
                CheckTrue("A frame guided worse than the limit is flagged", res.Guiding.Bad && res.Path.EndsWith("_BADGUIDING.fits") && File.Exists(res.Path)
                    && Sidralune.Imaging.IO.ImageFile.Load(res.Path).Header.Get("GUIDBAD") == "T", res.Path);
                res = capture.Expose(new ExposureRequest { Seconds = 1, Target = "Q", MaxGuidingRms = 50 }, ct).Result;
                CheckTrue("…within it, not", !res.Guiding.Bad && !res.Path.Contains("BADGUIDING"));

                // a cloud during the exposure interrupts guiding
                var gcam = (CameraSimulator)dm.GuideCamera;
                var cloudy = capture.Expose(new ExposureRequest { Seconds = 4, Target = "Q", MaxGuidingRms = 50 }, ct);
                Thread.Sleep(800);
                gcam.Transparency = 0;
                res = cloudy.Result;
                gcam.Transparency = 1;
                CheckTrue("Guiding interrupted by a cloud flags the frame", res.Guiding.Interrupted && res.Guiding.Bad && res.Image.Header.Get("GUIDLOST") == "T");
                CheckTrue("The star comes back", SpinUntil(() => g.IsGuiding, 20000), g.State);

                // a sequence: flagged frames are kept, or taken again up to three times in a row
                var log = new List<string>();
                var ctx = new SequenceContext { Devices = dm, Capture = capture, Log = m => { lock (log) log.Add(m); }, GuidingWanted = true };
                int frames = 0;
                capture.ImageCaptured += r => frames++;
                var flag = new TakeExposures { Count = 1, Exposure = 1, DitherEvery = 0, FrameType = "LIGHT", MaxGuidingRms = 0.001 };
                flag.Run(ctx, ct).Wait();
                CheckTrue("Flagged and kept", flag.Completed == 1 && frames == 1 && log.Any(m => m.Contains("_BADGUIDING") && m.Contains("flagged: guiding RMS")), string.Join(" | ", log));
                frames = 0; log.Clear();
                var again = new TakeExposures { Count = 2, Exposure = 1, DitherEvery = 0, FrameType = "LIGHT", MaxGuidingRms = 0.001, WhenGuidingIsBad = BadGuidingAction.Retake };
                again.Run(ctx, ct).Wait();
                CheckTrue("Taken again three times, then kept", again.Completed == 2 && frames == 8 && log.Count(m => m.EndsWith("taking it again")) == 6, frames + ": " + string.Join(" | ", log));
                frames = 0;
                var good = new TakeExposures { Count = 2, Exposure = 1, DitherEvery = 0, FrameType = "LIGHT", MaxGuidingRms = 50, WhenGuidingIsBad = BadGuidingAction.Retake };
                good.Run(ctx, ct).Wait();
                CheckTrue("Good frames are not taken again", good.Completed == 2 && frames == 2);
            }
            finally
            {
                g.StopGuiding();
                dm.DisconnectAll();
            }
        }

        [TestMethod]
        public void TestGuideFrameSolving()
        {
            // the catalogue's judgement of a star in a solved frame
            var wcs = Sidralune.Science.Astrometry.Wcs.Create(100, 30, 3, 0, 640, 480);
            Func<double, double, Sidralune.Science.Astrometry.RefStar> at = (x, y) => { double ra, dec; wcs.PixelToWorld(x, y, out ra, out dec); return new Sidralune.Science.Astrometry.RefStar { Ra = ra, Dec = dec }; };
            var cat = new List<Sidralune.Science.Astrometry.RefStar>();
            Action<double, double, double> add = (x, y, mag) => { var r = at(x, y); r.Mag = mag; cat.Add(r); };
            add(100, 100, 9);                       // alone
            add(300, 200, 10); add(303, 200, 11);  // a close double, the partner a magnitude fainter
            add(500, 300, 10); add(504, 300, 14);  // a partner far fainter does not matter
            add(200, 400, 9);                      // known to vary
            Func<double, double, double, bool> varies = (ra, dec, r) => Math.Abs(ra - at(200, 400).Ra) * 3600 < r && Math.Abs(dec - at(200, 400).Dec) * 3600 < r;
            CheckTrue("A single catalogue star suits", GuideStarCatalog.Suits(wcs, cat, 100.5, 99.6, 15, varies));
            CheckTrue("…a spot the catalogue does not know does not", !GuideStarCatalog.Suits(wcs, cat, 150, 150, 15, varies));
            CheckTrue("…nor a double", !GuideStarCatalog.Suits(wcs, cat, 300, 200, 15, varies));
            CheckTrue("…a much fainter neighbour does not matter", GuideStarCatalog.Suits(wcs, cat, 500, 300, 15, varies));
            CheckTrue("…a variable does not", !GuideStarCatalog.Suits(wcs, cat, 200, 400, 15, varies));
            var solve = new GuideFrameSolve { ScaleArcsec = 1.55, PixelSizeUm = 3.75, Binning = 1, FocalLengthMm = 500, Camera = "c" };
            CheckTrue("The solve gives the guide scope's focal length", Math.Abs(solve.SolvedFocalMm - 206.265 * 3.75 / 1.55) < 1e-9 && solve.Fits(1, 3.75, 500, "c") && !solve.Fits(2, 3.75, 500, "c") && !solve.Fits(1, 3.75, 400, "c"));

            var dm = GuideObservatory("guide-solve");
            var tel = (TelescopeSimulator)dm.Telescope;
            var g = dm.BuiltinGuider;
            var ct = CancellationToken.None;
            var problems = new List<string>();
            g.Failed += m => problems.Add(m);
            try
            {
                Centering.Slew(tel, tel.SiderealTime - 0.5, 30, ct).Wait();
                double focalScale = g.PixelScale;
                CheckTrue("No solve yet", g.CurrentSolve == null && g.Settings.SolveGuideFrames);
                g.StartGuiding(false, ct).Wait();
                var sv = g.CurrentSolve;
                CheckTrue("Starting guiding solves a guide frame first", sv != null && g.IsGuiding, g.SolveMessage ?? g.State);
                Info(string.Format("Solved {0:0.000}\"/px (focal length gives {1:0.000}), angle {2:0.0}°, {3:0} mm", sv.ScaleArcsec, focalScale, sv.AngleDeg, sv.SolvedFocalMm));
                CheckTrue("…giving the camera's scale", Math.Abs(sv.ScaleArcsec / focalScale - 1) < 0.03 && Math.Abs(g.PixelScale - sv.ScaleArcsec) < 1e-12);
                CheckTrue("…kept in the settings", GuiderSettings.Load(dm.GuiderSettingsPath).LastSolve != null);
                CheckTrue("…and the guide star's place on the sky", g.StarOnSkyKnown);

                // a knock moves the stars far beyond their search boxes: other stars may land in them, but solving finds the
                // guide star, and pulses bring the mount back to where it pointed
                g.FindLostAfterSeconds = 2;
                double sc = g.PixelScale, ra0 = tel.TrueRa, dec0 = tel.TrueDec;
                Func<double> off = () => Math.Sqrt(Math.Pow(AstroMath.NormalizeDeg180((tel.TrueRa - ra0) * 15) * Math.Cos(dec0 * AstroMath.Deg2Rad), 2) + Math.Pow(tel.TrueDec - dec0, 2)) * 3600;
                tel.Bump(80 * sc, -60 * sc);
                Info(string.Format("Knocked {0:0}\" away", off()));
                CheckTrue("After a knock, solving brings the mount back to where it pointed", SpinUntil(() => g.IsGuiding && !g.Lost && off() < 4 * sc, 90000),
                    string.Format("{0}, {1:0.0}\" away", g.State, off()));
                // the star is taken again once the pulses have brought it back, and guiding then holds it at the lock position
                CheckTrue("…with the guide star at the lock position", SpinUntil(() => g.IsGuiding && Math.Abs(g.Star.X - g.LockX) < 3 && Math.Abs(g.Star.Y - g.LockY) < 3 && off() < 4 * sc, 20000),
                    string.Format("{0:0.0},{1:0.0} vs {2:0.0},{3:0.0}, {4:0.0}\" away", g.Star.X, g.Star.Y, g.LockX, g.LockY, off()));
                string log;
                using (var rd = new StreamReader(new FileStream(g.GuideLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))) log = rd.ReadToEnd();
                CheckTrue("The guide log says how it was found", log.Contains("Star found by plate solve"));
                Info(string.Join(" | ", log.Split('\n').Where(l => l.Contains("Plate") || l.Contains("plate") || l.Contains("Star") || l.Contains("Stars")).Take(12)));

                // stopped and started where it was (around autofocus, a program's stop and guide): the solve is kept; after
                // a centring move of a few arcminutes it is made again
                int solves = 0;
                Action counted = () => { if (g.SolveMessage == null) solves++; };
                g.Solved += counted;
                try
                {
                    g.StopGuiding();
                    g.StartGuiding(false, ct).Wait();
                    CheckTrue("Started again where it stopped, it guides without solving again", g.IsGuiding && solves == 0 && g.StarOnSkyKnown, solves.ToString());
                    g.StopGuiding();
                    Centering.Slew(tel, tel.RightAscension, tel.Declination + 4.0 / 60, ct).Wait();
                    g.StartGuiding(false, ct).Wait();
                    CheckTrue("…after a centring move of 4′ it solves again", g.IsGuiding && solves == 1 && g.StarOnSkyKnown, solves.ToString());
                    // centring syncs the mount, then slews to the target: the mount says it points where it did, but has moved
                    g.StopGuiding();
                    double sra = tel.RightAscension, sdec = tel.Declination;
                    dm.SyncTelescope(tel, sra, sdec + 3.0 / 60);
                    Centering.Slew(tel, sra, sdec, ct).Wait();
                    g.StartGuiding(false, ct).Wait();
                    CheckTrue("…and after a centring that synced the mount first", g.IsGuiding && solves == 2 && g.StarOnSkyKnown, solves.ToString());
                    // a meridian flip: the same coordinates on the other side of the pier, with the field turned round
                    g.StopGuiding();
                    var pier = typeof(TelescopeSimulator).GetField("_pier", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    pier.SetValue(tel, tel.SideOfPier == PierSide.East ? PierSide.West : PierSide.East);
                    g.StartGuiding(false, ct).Wait();
                    CheckTrue("…and after a meridian flip back to the same coordinates", g.IsGuiding && solves == 3 && g.StarOnSkyKnown, solves.ToString());
                }
                finally { g.Solved -= counted; }

                // a slew: the place on the sky is forgotten, the scale kept
                g.StopGuiding();
                Centering.Slew(tel, tel.SiderealTime - 1, 40, ct).Wait();
                SpinUntil(() => false, 1500);
                CheckTrue("After a slew the star's place is forgotten, the scale kept", !g.StarOnSkyKnown && g.CurrentSolve != null);

                // off: no solving
                g.Settings.SolveGuideFrames = false;
                g.Settings.LastSolve = null;
                g.StartGuiding(false, ct).Wait();
                CheckTrue("With solving off, guiding starts without it", g.IsGuiding && g.CurrentSolve == null && !g.StarOnSkyKnown);
                CheckTrue("No guider failures", problems.Count == 0, string.Join("; ", problems));
            }
            finally
            {
                g.Settings.SolveGuideFrames = true;
                g.StopGuiding();
                dm.DisconnectAll();
            }
        }

        [TestMethod]
        public void TestPredictedCalibration()
        {
            var dm = GuideObservatory("guide-predict");
            var tel = (TelescopeSimulator)dm.Telescope;
            var g = dm.BuiltinGuider;
            var ct = CancellationToken.None;
            var problems = new List<string>();
            g.Failed += m => problems.Add(m);
            Func<double, double, double> angleOff = (a, b) => Math.Abs(AstroMath.NormalizeDeg180(a - b));
            try
            {
                Centering.Slew(tel, tel.SiderealTime - 0.5, 30, ct).Wait();
                // the full calibration, for comparison
                g.Settings.PredictCalibration = false;
                var sw = Stopwatch.StartNew();
                g.StartGuiding(true, ct).Wait();
                double fullSec = sw.Elapsed.TotalSeconds;
                var full = g.Calibration;
                CheckTrue("A full calibration", full != null && !full.Predicted && full.RaSteps >= 4 && full.DecSteps >= 4 && g.IsGuiding);
                g.StopGuiding();

                // predicted from the solve, confirmed with three steps each way
                g.Settings.PredictCalibration = true;
                sw.Restart();
                g.StartGuiding(true, ct).Wait();
                double predSec = sw.Elapsed.TotalSeconds;
                var pred = g.Calibration;
                Info(string.Format("Full: RA {0:0.0}° {1:0.00} px/s, Dec {2:0.0}° {3:0.00} px/s, {4:0} s; predicted: RA {5:0.0}° {6:0.00} px/s, Dec {7:0.0}° {8:0.00} px/s, {9:0} s",
                    full.RaAngleDeg, full.RaRatePxPerSec, full.DecAngleDeg, full.DecRatePxPerSec, fullSec, pred.RaAngleDeg, pred.RaRatePxPerSec, pred.DecAngleDeg, pred.DecRatePxPerSec, predSec));
                CheckTrue("Calibrating predicts from the solve and confirms with three steps each way", pred.Predicted && pred.RaSteps == 3 && pred.DecSteps == 3 && pred.PredictionMiss == null && pred.Issues.Count == 0,
                    string.Join(", ", pred.Issues) + " " + pred.PredictionMiss);
                CheckTrue("…the same directions as a full calibration", angleOff(pred.RaAngleDeg, full.RaAngleDeg) < 4 && angleOff(pred.DecAngleDeg, full.DecAngleDeg) < 4);
                CheckTrue("…and speeds", Math.Abs(pred.RaRatePxPerSec / full.RaRatePxPerSec - 1) < 0.1 && Math.Abs(pred.DecRatePxPerSec / full.DecRatePxPerSec - 1) < 0.1);
                CheckTrue("…and guides", g.IsGuiding && g.RmsTotalArcsec < 1.5, g.RmsTotalArcsec.ToString("0.00"));

                // a meridian flip and another declination: no new calibration
                var when = pred.When;
                g.StopGuiding();
                Centering.Slew(tel, tel.SiderealTime + 0.5, 30, ct).Wait();
                CheckTrue("The mount is on the other side of the pier", pred.Flipped(tel.SideOfPier), tel.SideOfPier.ToString());
                g.StartGuiding(false, ct).Wait();
                CheckTrue("After a flip it guides without calibrating again", g.IsGuiding && g.Calibration.When == when);
                g.StopGuiding();
                Centering.Slew(tel, tel.SiderealTime + 0.5, 65, ct).Wait();
                g.StartGuiding(false, ct).Wait();
                CheckTrue("…and at another declination", g.IsGuiding && g.Calibration.When == when);
                g.StopGuiding();

                // a mount that reports half its real guide rate: the prediction misses, and it calibrates in full
                tel.TrueGuideRateFactor = 2;
                Centering.Slew(tel, tel.SiderealTime - 0.5, 30, ct).Wait();
                g.StartGuiding(true, ct).Wait();
                var miss = g.Calibration;
                CheckTrue("A wrong guide rate: the prediction is not used, and it calibrates in full", !miss.Predicted && miss.PredictionMiss == GuideCalibration.MissRaSpeed
                    && Math.Abs(miss.PredictionMissBy - 200) < 30 && miss.RaSteps >= 4 && Math.Abs(miss.RaRatePxPerSec / full.RaRatePxPerSec - 2) < 0.25 && g.IsGuiding,
                    miss.PredictionMiss + " " + miss.PredictionMissBy.ToString("0"));
                CheckTrue("No guider failures", problems.Count == 0, string.Join("; ", problems));
            }
            finally
            {
                tel.TrueGuideRateFactor = 1;
                g.StopGuiding();
                dm.DisconnectAll();
            }
        }

        [TestMethod]
        public void TestGuidingToolbox()
        {
            // Lowpass: the median of the last errors plus their slope, never more than the error nor the other way
            var lp = new LowpassAlgorithm { MinMovePx = 0.2, SlopeWeight = 0 };
            double c = 0;
            foreach (var e in new[] { 1.0, 1.1, 0.9, 3.0, 1.0 }) c = lp.Correction(e);
            CheckTrue("Lowpass without slope corrects the median, a frame thrown by the seeing counting for little", Math.Abs(c - 1.0) < 1e-9, c.ToString());
            lp = new LowpassAlgorithm { MinMovePx = 0.2, SlopeWeight = 5 };
            foreach (var e in new[] { 0.3, 0.4, 0.5, 0.6, 0.7, 0.8 }) c = lp.Correction(e);
            CheckTrue("…with slope it meets a drift ahead, but no further than the error", Math.Abs(c - 0.8) < 1e-9, c.ToString());
            lp.Reset();
            foreach (var e in new[] { 0.3, 0.32, 0.34, 0.36 }) c = lp.Correction(e);
            CheckTrue("…its correction lies between the median and the error", c > 0.33 && c <= 0.36, c.ToString());
            CheckTrue("…an error under the minimum move is left alone", lp.Correction(0.1) == 0);
            var flat = new LowpassAlgorithm { MinMovePx = 0.2, SlopeWeight = 0 };
            foreach (var e in new[] { 1.0, 1.0, 1.0, 1.0 }) flat.Correction(e);
            CheckTrue("…and nothing the other way", flat.Correction(-0.5) == 0);

            var dm = GuideObservatory("guide-toolbox");
            var tel = (TelescopeSimulator)dm.Telescope;
            var g = dm.BuiltinGuider;
            var ct = CancellationToken.None;
            string dir = Path.GetDirectoryName(dm.GuiderSettingsPath);
            try
            {
                Centering.Slew(tel, tel.SiderealTime - 0.5, 30, ct).Wait();
                g.Settings.RaAlgorithm = GuideAlgorithmKind.Lowpass;
                g.Settings.DecAlgorithm = GuideAlgorithmKind.Lowpass;
                g.StartGuiding(false, ct).Wait();
                CheckTrue("It guides with Lowpass on both axes", g.IsGuiding && g.RmsTotalArcsec < 2, g.RmsTotalArcsec.ToString("0.00"));

                // the Star-Cross test: square axes, equal speeds both ways, the Dec backlash at each turn and none in RA
                tel.DecBacklashMs = 800;
                var cal = g.Calibration;
                var sc = g.RunStarCross(ct).Result;
                Info(string.Format("Star-Cross: {0:0.0}° from square; W {1:0.00} E {2:0.00} N {3:0.00} S {4:0.00} px/s (calibration RA {5:0.00}, Dec {6:0.00}); turns RA {7:0}/{8:0} ms, Dec {9:0}/{10:0} ms; first steps {11:0.00}/{12:0.00}",
                    sc.OrthogonalityErrorDeg, sc.WestRate, sc.EastRate, sc.NorthRate, sc.SouthRate, cal.RaRateAt(tel.Declination), cal.DecRatePxPerSec,
                    sc.RaTurnMs, sc.RaReturnMs, sc.DecTurnMs, sc.DecReturnMs, sc.RaFirstStep, sc.DecFirstStep));
                Func<double, double, bool> near = (a, b) => Math.Abs(a / b - 1) < 0.15;
                CheckTrue("The Star-Cross test draws a cross", sc.Track.Count >= 1 + 8 * sc.StepsOut && new[] { "West", "East", "North", "South" }.All(l => sc.Track.Any(p => p.Leg == l)), sc.Track.Count.ToString());
                CheckTrue("…RA and Dec at right angles", sc.OrthogonalityErrorDeg < 3);
                CheckTrue("…each direction at the calibration's speed", near(sc.WestRate, cal.RaRateAt(tel.Declination)) && near(sc.EastRate, sc.WestRate) && near(sc.NorthRate, cal.DecRatePxPerSec) && near(sc.SouthRate, sc.NorthRate));
                CheckTrue("…no backlash in RA", sc.RaTurnMs < 150 && sc.RaReturnMs < 150);
                CheckTrue("…the Dec backlash at both turns", Math.Abs(sc.DecTurnMs - 800) < 250 && Math.Abs(sc.DecReturnMs - 800) < 250);
                CheckTrue("…no stiction in RA", sc.RaFirstStep > 0.7 && sc.RaFirstStep < 1.3);
                CheckTrue("…and an image of the cross", sc.ImagePath != null && File.Exists(sc.ImagePath) && Sidralune.Imaging.IO.ImageFile.Load(sc.ImagePath).Width > 0);
                CheckTrue("Guiding goes on afterwards", g.Settle(3, 2, 60, ct).Result && g.IsGuiding);
                tel.DecBacklashMs = 0;

                // guide frames saved around a lost star: the five before and the three after
                g.Settings.SaveDiagnosticFrames = true;
                g.DiagnosisInterval = TimeSpan.Zero;
                Thread.Sleep(4000); // frames kept before the cloud
                var gcam = (CameraSimulator)dm.GuideCamera;
                gcam.Transparency = 0;
                CheckTrue("Behind a cloud the star is lost", SpinUntil(() => g.Lost, 10000));
                CheckTrue("…and the frames around it are saved", SpinUntil(() => g.LastDiagnosisFolder != null && Directory.Exists(g.LastDiagnosisFolder) && Directory.GetFiles(g.LastDiagnosisFolder, "*.fits").Length == 8, 15000),
                    g.LastDiagnosisFolder ?? "none");
                var saved = Directory.GetFiles(g.LastDiagnosisFolder, "*.fits").OrderBy(f => f).ToList();
                var first = Sidralune.Imaging.IO.ImageFile.Load(saved[0]);
                CheckTrue("…named and marked for what happened", g.LastDiagnosisFolder.EndsWith("Star lost") && (first.Header.Get("OBJECT") ?? "").Contains("Star lost") && first.Header.Has("LOCKX"),
                    first.Header.Get("OBJECT"));
                gcam.Transparency = 1;
                CheckTrue("The star comes back", SpinUntil(() => g.IsGuiding && !g.Lost, 20000));
                g.StopGuiding();

                // and around a failed calibration: a mount that does not move
                string before = g.LastDiagnosisFolder;
                g.Settings.SolveGuideFrames = false;
                tel.TrueGuideRateFactor = 0;
                bool failed = false;
                try { g.StartGuiding(true, ct).Wait(); } catch (AggregateException) { failed = true; }
                CheckTrue("A calibration that fails…", failed);
                CheckTrue("…saves the frames before it", SpinUntil(() => g.LastDiagnosisFolder != before && g.LastDiagnosisFolder.EndsWith("Calibration failed") && Directory.Exists(g.LastDiagnosisFolder) && Directory.GetFiles(g.LastDiagnosisFolder, "*.fits").Length == 5, 15000),
                    g.LastDiagnosisFolder ?? "none");
            }
            finally
            {
                tel.TrueGuideRateFactor = 1;
                g.Settings.SaveDiagnosticFrames = false;
                g.Settings.SolveGuideFrames = true;
                g.Settings.RaAlgorithm = GuideAlgorithmKind.Hysteresis;
                g.Settings.DecAlgorithm = GuideAlgorithmKind.ResistSwitch;
                g.StopGuiding();
                dm.DisconnectAll();
            }
        }
    }
}
