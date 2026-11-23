using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Sidralune.Core.Catalogs;
using Sidralune.Core.Mathematics;
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
        private static bool SpinUntil(Func<bool> cond, int ms)
        {
            var sw = Stopwatch.StartNew();
            while (!cond()) { if (sw.ElapsedMilliseconds > ms) return false; Thread.Sleep(20); }
            return true;
        }

        private static string Folder(string name)
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

        private static DeviceManager GuideObservatory(string name)
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
            dm.Connect(new DeviceInfo { Kind = DeviceKind.Guider, Protocol = DeviceProtocol.Builtin, Name = DeviceManager.BuiltinGuiderName });
            var g = (SidraluneGuider)dm.Guider;
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
            dm.Disconnect(DeviceKind.GuideCamera);
            CheckTrue("A lost camera stops the guider", SpinUntil(() => g.State == SidraluneGuider.Stopped, 8000) && problems.Count == 1, string.Join(";", problems));
            dm.DisconnectAll();
        }
    }
}
