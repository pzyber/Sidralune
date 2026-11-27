using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Sidralune.Devices;
using Sidralune.Devices.Automation;
using Sidralune.Devices.Guiding;
using Sidralune.Devices.Simulators;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Sidralune.Tests.Checks;

namespace Sidralune.Tests
{
    [TestClass]
    public class GuideLogTests : TestBase
    {
        /// <summary>
        /// A night guided on a mount with a periodic error of the given amplitude (pixels) and period, written as Sidralune's
        /// guide log: frames every 2 s (some dropped and some slow when uneven), a guider correcting 70 % of each error at
        /// 3 px per second of pulse, dithers of 4 px every 5 minutes, settled 10 s later.
        /// </summary>
        private static string WriteNight(string dir, string name, double amplitude, double period, double hours, bool uneven, bool withRate = true)
        {
            var rng = new Random(7);
            var sb = new StringBuilder("Time,Event,X,Y,Offset X,Offset Y,RA px,Dec px,RA ms,Dec ms,SNR,Flux,HFD,Stars,Note\n");
            var t0 = new DateTime(2026, 11, 26, 23, 50, 0);
            // notes as the guider writes them: quoted when they hold a comma or a quote
            Action<double, string, string> ev = (at, e, note) => sb.AppendFormat(CultureInfo.InvariantCulture, "{0:HH:mm:ss.fff},{1},,,,,,,0,0,,,,,{2}\n", t0.AddSeconds(at), e,
                note.IndexOfAny(new[] { ',', '"' }) >= 0 ? "\"" + note.Replace("\"", "\"\"") + "\"" : note);
            ev(0, "Calibration", "started");
            for (int k = 1; k <= 5; k++) sb.AppendFormat(CultureInfo.InvariantCulture, "{0:HH:mm:ss.fff},Calibration West,{1},{2},{3},{4},,,500,0,50,0.1,2,,\n", t0.AddSeconds(k), 100 + 4 * k, 200 + 2 * k, 4 * k, 2 * k);
            ev(6, "Calibrated", "RA 26.6° 8.94 px/s (5 steps), Dec 116.6° 8.9 px/s (5 steps), 0.0° from square");
            ev(7, "Settings", "scale 1.500\"/px, exposure 2 s, worm period 480 s" + (withRate ? ", RA rate 3.000 px/s" : ""));
            ev(8, "Guiding", "lock 100 200");
            const double rate = 3;
            double cum = 0, lockRa = 0, nextDither = 300, settleAt = double.NaN, t = 10;
            while (t < hours * 3600)
            {
                double gear = amplitude * Math.Sin(2 * Math.PI * t / period) + 0.002 * t;
                double e = gear - rate * cum - lockRa + 0.1 * (rng.NextDouble() - 0.5);
                double pulse = 0.7 * e / rate; // s, East positive: brings a positive error back
                bool drop = uneven && rng.NextDouble() < 0.15;
                if (!drop)
                {
                    sb.AppendFormat(CultureInfo.InvariantCulture, "{0:HH:mm:ss.fff},Guide,100,200,0,0,{1:0.000},{2:0.000},{3},0,40,0.1,2,5,\n", t0.AddSeconds(t), e, 0.05 * (rng.NextDouble() - 0.5), (int)Math.Round(pulse * 1000));
                    cum += Math.Round(pulse * 1000) / 1000;
                }
                if (t >= nextDither) { lockRa += 4; ev(t + 0.01, "Dither", "4 0"); nextDither += 300; settleAt = t + 10; }
                if (!double.IsNaN(settleAt) && t >= settleAt) { ev(t + 0.01, "Settled", ""); settleAt = double.NaN; }
                t += uneven && rng.NextDouble() < 0.1 ? 5 : 2;
            }
            ev(t, "Stopped", "");
            string path = Path.Combine(dir, name);
            File.WriteAllText(path, sb.ToString());
            return path;
        }

        [TestMethod]
        public void TestGuideLogAnalysis()
        {
            string dir = GuidingTests.Folder("guide-logs");
            // a night past midnight with a periodic error of ±4 px (±6″) over 480 s, at an even pace
            var log = GuideLogFile.Read(WriteNight(dir, "Guide 2026-11-26 235000.csv", 4, 480, 1.5, false));
            CheckTrue("A Sidralune guide log", log.Format == "Sidralune" && log.Sessions.Count == 1);
            var s = log.Sessions[0];
            CheckTrue("…its scale, worm period and calibration", s.ScaleArcsec == 1.5 && s.WormPeriodSec == 480 && s.Calibration.StartsWith("RA 26.6°") && s.CalibrationSteps.Count == 5);
            CheckTrue("…the date follows past midnight", s.Steps[s.Steps.Count - 1].Time.Date == new DateTime(2026, 11, 27));
            var frames = s.Frames();
            CheckTrue("A frame for each stretch between dithers", frames.Count == 18, frames.Count.ToString());
            CheckTrue("…settling left out", s.Steps.Count(x => x.Settling) >= 17 * 4 && frames.Sum(f => f.Steps) == s.Steps.Count(x => !x.Settling) && s.Total.Steps == frames.Sum(f => f.Steps));
            CheckTrue("…and RMS for each", frames.All(f => f.RmsTotal > 0 && f.Peak >= f.RmsTotal * 0.99));

            var sp = GuideSpectrum.Of(s, true, 480);
            Info(string.Format("Even: {0}, rate {1:0.00} px/s, worm {2:0} s ±{3:0.00}″, peak {4:0} s ±{5:0.00}″", sp.Method, sp.RaRatePxPerSec, sp.WormPeriodSec, sp.WormAmplitude, sp.PeakPeriodSec, sp.PeakAmplitude));
            CheckTrue("At an even pace the FFT, with the corrections added back at the RA rate the log gives", sp.Method == "FFT" && sp.CorrectionsAdded && sp.RaRatePxPerSec == 3 && s.RaRatePxPerSec == 3);
            CheckTrue("…finds the periodic error: its period and amplitude in arcseconds", sp.InArcsec && Math.Abs(sp.WormPeriodSec / 480 - 1) < 0.04 && Math.Abs(sp.WormAmplitude / 6 - 1) < 0.12
                && Math.Abs(sp.PeakPeriodSec / 480 - 1) < 0.04);
            var left = GuideSpectrum.Of(s, false, 480);
            CheckTrue("What guiding left of it is far smaller", !left.CorrectionsAdded && left.WormAmplitude < 0.5 * sp.WormAmplitude, left.WormAmplitude.ToString("0.00"));
            var noRate = GuideLogFile.Read(WriteNight(dir, "Guide 2026-11-26 235003.csv", 4, 480, 1.5, false, false)).Sessions[0];
            var nr = GuideSpectrum.Of(noRate, true, 480);
            CheckTrue("Without the RA rate in the log, only what was left", double.IsNaN(noRate.RaRatePxPerSec) && !nr.CorrectionsAdded && nr.WormAmplitude < 0.5 * sp.WormAmplitude);

            // an uneven pace: Lomb–Scargle
            var uneven = GuideLogFile.Read(WriteNight(dir, "Guide 2026-11-26 235001.csv", 4, 480, 1.5, true)).Sessions[0];
            var su = GuideSpectrum.Of(uneven, true, 480);
            Info(string.Format("Uneven: {0}, worm {1:0} s ±{2:0.00}″", su.Method, su.WormPeriodSec, su.WormAmplitude));
            CheckTrue("At an uneven pace Lomb–Scargle finds it too", su.Method == "Lomb–Scargle" && Math.Abs(su.WormPeriodSec / 480 - 1) < 0.04 && Math.Abs(su.WormAmplitude / 6 - 1) < 0.15);
            var shortLog = GuideLogFile.Read(WriteNight(dir, "Guide 2026-11-26 235002.csv", 4, 480, 0.15, false)).Sessions[0];
            CheckTrue("Too short a log for the worm period gives none", double.IsNaN(GuideSpectrum.Of(shortLog, true, 480).WormAmplitude));

            // PHD2's guide log
            var phd = new StringBuilder();
            phd.Append("PHD2 version 2.6.13, Log version 2.5. Log enabled at 2026-11-26 21:00:00\n\n");
            phd.Append("Calibration Begins at 2026-11-26 21:01:00\nRA = 5.00 hr, Dec = 0.0 deg, Hour angle = 1.00 hr, Pier side = West\nDirection,Step,dx,dy,x,y,Dist\n");
            for (int k = 1; k <= 4; k++) phd.AppendFormat(CultureInfo.InvariantCulture, "West,{0},{1}.0,{2}.0,{3},{4},{5}\n", k, 5 * k, k, 100 + 5 * k, 200 + k, 5 * k);
            phd.Append("West calibration complete. Angle = 11.3 deg, Rate = 9.810 px/sec, Parity = Even\nNorth calibration complete. Angle = 101.3 deg, Rate = 9.800 px/sec, Parity = Even\nCalibration complete, mount = Mount.\n\n");
            for (int session = 0; session < 2; session++)
            {
                phd.AppendFormat("Guiding Begins at 2026-11-26 {0}:00:00\nDither = both axes, Dither scale = 1.000\nPixel scale = 1.55 arc-sec/px, Binning = 1, Focal length = 500 mm\n", 21 + session);
                if (session == 1) phd.Append("RA = 6.00 hr, Dec = 60.0 deg, Hour angle = 0.50 hr, Pier side = West\n");
                phd.Append("Frame,Time,mount,dx,dy,RARawDistance,DECRawDistance,RAGuideDistance,DECGuideDistance,RADuration,RADirection,DECDuration,DECDirection,XStep,YStep,StarMass,SNR,ErrorCode\n");
                for (int k = 1; k <= 30; k++)
                {
                    if (k == 10) phd.Append("INFO: DITHER by 2.000, -1.000, new lock pos = 102.000, 199.000\nINFO: SETTLING STATE CHANGE, Settling started\n");
                    if (k == 14) phd.Append("INFO: SETTLING STATE CHANGE, Settling complete\n");
                    if (k == 20) { phd.AppendFormat(CultureInfo.InvariantCulture, "{0},{1:0.000},\"DROP\",,,,,,,,,,,,,0,0.0,2,\"Star lost - low SNR\"\n", k, 2.0 * k); continue; }
                    phd.AppendFormat(CultureInfo.InvariantCulture, "{0},{1:0.000},\"Mount\",0.1,0.2,{2:0.000},{3:0.000},0.1,0.1,{4},{5},50,N,,,12345,40.1,0\n", k, 2.0 * k, 0.3 * Math.Sin(k), -0.2, 120, k % 2 == 0 ? "E" : "W");
                }
                phd.AppendFormat("Guiding Ends at 2026-11-26 {0}:01:00\n\n", 21 + session);
            }
            string phdPath = Path.Combine(dir, "PHD2_GuideLog_2026-11-26_210000.txt");
            File.WriteAllText(phdPath, phd.ToString());
            var p = GuideLogFile.Read(phdPath);
            CheckTrue("A PHD2 guide log, in its sessions", p.Format == "PHD2" && p.Sessions.Count == 2 && p.Sessions.All(x => x.Steps.Count == 29));
            var p0 = p.Sessions[0];
            CheckTrue("…its scale, calibration and steps", p0.ScaleArcsec == 1.55 && p0.Calibration.Contains("Rate = 9.810") && p0.CalibrationSteps.Count == 4 && p0.Steps[1].RaMs == 120 && p0.Steps[0].RaMs == -120
                && p0.Steps[0].Time == new DateTime(2026, 11, 26, 21, 0, 2));
            CheckTrue("…its RA rate, scaled to where it guided", p0.RaRatePxPerSec == 9.81 && Math.Abs(p.Sessions[1].RaRatePxPerSec - 9.81 * 0.5) < 1e-9);
            CheckTrue("…its dither and settling", p0.Events.Count(e => e.Name == "Dither") == 1 && p0.Steps.Count(x => x.Settling) == 4 && p0.Frames().Count == 2);
            CheckTrue("Something else is not a guide log", Throws(() => { File.WriteAllText(Path.Combine(dir, "x.csv"), "a,b\n1,2\n"); GuideLogFile.Read(Path.Combine(dir, "x.csv")); }));

            // the guider's own log, read back
            var dm = GuidingTests.GuideObservatory("guide-log-own");
            var tel = (TelescopeSimulator)dm.Telescope;
            var g = dm.BuiltinGuider;
            try
            {
                Centering.Slew(tel, tel.SiderealTime - 0.5, 30, CancellationToken.None).Wait();
                g.StartGuiding(false, CancellationToken.None).Wait();
                g.Dither(3, false, 1.5, 1, 30, CancellationToken.None).Wait();
                Thread.Sleep(3000);
                var own = GuideLogFile.Read(g.GuideLogPath);
                var os = own.Sessions.LastOrDefault();
                CheckTrue("The guider's own log reads back", own.Format == "Sidralune" && os != null && os.Steps.Count > 10 && Math.Abs(os.ScaleArcsec - g.PixelScale) < 0.001
                    && os.WormPeriodSec == g.Settings.PecPeriodSec && Math.Abs(os.RaRatePxPerSec / g.Calibration.RaRateAt(tel.Declination) - 1) < 0.01 && os.Calibration != null && os.Events.Any(e => e.Name == "Dither") && os.Frames().Count == 2,
                    os == null ? "none" : os.Steps.Count + " steps, " + os.Frames().Count + " frames");
            }
            finally
            {
                g.StopGuiding();
                dm.DisconnectAll();
            }
        }

        private static bool Throws(Action a)
        {
            try { a(); return false; } catch (InvalidDataException) { return true; }
        }
    }
}
