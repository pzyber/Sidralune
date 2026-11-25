using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Sidralune.Core.Mathematics;
using Sidralune.Core.Util;
using Sidralune.Devices.Automation;
using Sidralune.Imaging;

namespace Sidralune.Devices.Guiding
{
    /// <summary>What a manual guide pulse did: how far the star moved, and what the calibration expected.</summary>
    public class ManualPulseResult
    {
        public GuideDirection Direction;
        public int Ms;
        /// <summary>The star's move in the image (pixels), its length and direction (degrees); NaN without a star.</summary>
        public double Dx = double.NaN, Dy = double.NaN, Distance = double.NaN, AngleDeg = double.NaN;
        /// <summary>What the calibration expected (NaN without one), and whether the move agrees: within 30° and half the distance.</summary>
        public double ExpectedDistance = double.NaN, ExpectedAngleDeg = double.NaN;
        public bool? Matches;
    }

    /// <summary>The Dec backlash test: how long Dec takes to turn round, and how sure that is.</summary>
    public class BacklashMeasurement
    {
        public double Ms = double.NaN, UncertaintyMs = double.NaN, NorthRatePxPerSec = double.NaN, SouthRatePxPerSec = double.NaN;
        public int NorthSteps, SouthSteps, StepMs;
        /// <summary>The star came back at 90 % or more of the speed it went north, so the time it waited is backlash.</summary>
        public bool Valid;
    }

    public partial class SidraluneGuider
    {
        // ------------------------------------------------------------------ jobs run by the loop between frames

        private class LoopJob
        {
            public Func<CancellationToken, Task> Work;
            public CancellationTokenSource Cts;
            public TaskCompletionSource<bool> Done;
        }

        private LoopJob _job;

        /// <summary>
        /// Runs work in the guider's loop between two frames, so it can pulse and take frames (NextPosition) without the
        /// loop taking frames of its own meanwhile. Ends with the work, or when ct or the session is cancelled.
        /// </summary>
        private Task RunInLoop(Func<CancellationToken, Task> work, CancellationToken ct)
        {
            LoopJob j;
            lock (_lock)
            {
                if (_session == null) throw new InvalidOperationException(Lang.T("The guider is not running."));
                if (_job != null || _calibrationRequested || _phase == Phase.Calibrating) throw new InvalidOperationException(Lang.T("The guider is busy."));
                j = new LoopJob { Work = work, Cts = CancellationTokenSource.CreateLinkedTokenSource(ct, _session.Token), Done = new TaskCompletionSource<bool>() };
                _job = j;
            }
            return j.Done.Task;
        }

        // ------------------------------------------------------------------ the lock position

        /// <summary>
        /// Puts the lock position at a point in the frame. While guiding the stars are guided there, like a dither; while
        /// looping it is where guiding will hold them.
        /// </summary>
        public void SetLockPosition(double x, double y)
        {
            var f = LastFrame;
            int margin = Settings.SearchRadius;
            if (f == null || Star == null) throw new InvalidOperationException(Lang.T("Choose a guide star first."));
            if (x < margin || y < margin || x > f.Width - margin || y > f.Height - margin) throw new InvalidOperationException(Lang.T("The lock position must be inside the frame."));
            if (_phase == Phase.Calibrating) throw new InvalidOperationException(Lang.T("Wait until calibration has finished."));
            double ox, oy;
            lock (_lock)
            {
                ox = double.IsNaN(LockX) ? 0 : x - LockX; oy = double.IsNaN(LockY) ? 0 : y - LockY;
                LockX = x; LockY = y;
                if (_phase != Phase.Guiding) _keepLock = _userLock = true;
            }
            if (_phase == Phase.Guiding) _resetAlgorithms = true;
            Train(ox, oy);
            RememberLock();
            _log.Event("Lock position", string.Format(CultureInfo.InvariantCulture, "{0:0.00} {1:0.00}", x, y));
            Log.Info("Guider", string.Format(CultureInfo.InvariantCulture, "Lock position set to {0:0.00}, {1:0.00}", x, y));
        }

        /// <summary>
        /// Moves the lock position by the nudge step: on the sky through the calibration (North moves the telescope north,
        /// whatever the camera's angle), or without one along the image (North up, East left, a step in pixels).
        /// </summary>
        public void NudgeLock(GuideDirection dir)
        {
            if (double.IsNaN(LockX)) throw new InvalidOperationException(Lang.T("Choose a guide star first."));
            double step = Math.Max(0.01, Settings.NudgeArcsec), ox, oy;
            var cal = Settings.Calibration;
            SyncRotation();
            if (cal != null)
            {
                double e = dir == GuideDirection.East ? step : dir == GuideDirection.West ? -step : 0, n = dir == GuideDirection.North ? step : dir == GuideDirection.South ? -step : 0;
                cal.SkyToImage(e, n, PixelScale, MountPierSide(), Settings.MountReversesDecAfterFlip, out ox, out oy);
            }
            else
            {
                ox = dir == GuideDirection.East ? -step : dir == GuideDirection.West ? step : 0;
                oy = dir == GuideDirection.North ? -step : dir == GuideDirection.South ? step : 0;
            }
            MoveLock(ox, oy, true);
            _log.Event("Lock moved", dir + " " + step.ToString("0.##", CultureInfo.InvariantCulture) + (cal != null ? "\"" : " px"));
        }

        /// <summary>Moves the lock position (and every star's reference with it); counted for comet training when asked.</summary>
        private void MoveLock(double ox, double oy, bool counts)
        {
            lock (_lock)
            {
                if (double.IsNaN(LockX) || double.IsNaN(LockY)) return;
                LockX += ox; LockY += oy;
                if (_phase != Phase.Guiding) _keepLock = _userLock = true;
            }
            if (counts) Train(ox, oy);
            RememberLock();
        }

        /// <summary>A sticky lock position follows the lock while guiding (saved when guiding stops).</summary>
        private void RememberLock()
        {
            if (!Settings.StickyLock || _phase != Phase.Guiding) return;
            lock (_lock) { Settings.StickyLockX = LockX; Settings.StickyLockY = LockY; Settings.StickyLockCamera = _cameraId; }
        }

        // ------------------------------------------------------------------ comet tracking

        private DateTime _cometLast, _cometRatesAt;
        private double _trainEast, _trainNorth;
        private DateTime _trainStart;

        /// <summary>The lock position follows a comet's motion while guiding.</summary>
        public bool CometFollowing { get; private set; }
        /// <summary>The rates followed (arcsec/h: RA as sky motion, positive east; Dec positive north).</summary>
        public double CometRaRate { get; private set; }
        public double CometDecRate { get; private set; }
        /// <summary>Lock position moves are being counted to learn the comet's rates.</summary>
        public bool CometTraining { get; private set; }

        /// <summary>Starts or stops following the comet. The rates are taken from the settings (or the comet's orbit) now.</summary>
        public void FollowComet(bool on)
        {
            if (on)
            {
                if (Settings.Calibration == null) throw new InvalidOperationException(Lang.T("Calibrate first: comet tracking moves the lock position through the calibration."));
                UpdateCometRates();
                _cometLast = DateTime.UtcNow;
            }
            if (CometFollowing == on) return;
            CometFollowing = on;
            string what = on ? string.Format(CultureInfo.InvariantCulture, "Comet tracking on: RA {0:0.0}\"/h, Dec {1:0.0}\"/h", CometRaRate, CometDecRate) : "Comet tracking off";
            _log.Event("Comet", what);
            Log.Info("Guider", what);
        }

        /// <summary>Takes the comet's rates from the settings, or works them out from its orbit for now.</summary>
        public void UpdateCometRates()
        {
            if (Settings.CometSource == CometRateSource.Comet)
            {
                var comet = CometRates.Find(Settings.CometName);
                if (comet == null) throw new InvalidOperationException(Lang.T("The comet {0} is not in the comet list. Download the comet data in Settings, or enter the rates.", Settings.CometName));
                double ra, dec;
                CometRates.Compute(comet, _dm.Site, DateTime.UtcNow, out ra, out dec);
                CometRaRate = ra; CometDecRate = dec;
            }
            else { CometRaRate = Settings.CometRaArcsecPerHour; CometDecRate = Settings.CometDecArcsecPerHour; }
            _cometRatesAt = DateTime.UtcNow;
        }

        /// <summary>Moves the lock position by the comet's motion since the last frame.</summary>
        private void FollowComet()
        {
            var now = DateTime.UtcNow;
            double dt = (now - _cometLast).TotalSeconds;
            _cometLast = now;
            var cal = Settings.Calibration;
            if (cal == null || dt <= 0 || dt > 300) return;
            if (Settings.CometSource == CometRateSource.Comet && (now - _cometRatesAt).TotalMinutes >= 10)
                try { UpdateCometRates(); } catch (Exception ex) { Log.Warn("Guider", ex.Message); }
            double ox, oy;
            cal.SkyToImage(CometRaRate * dt / 3600, CometDecRate * dt / 3600, PixelScale, MountPierSide(), Settings.MountReversesDecAfterFlip, out ox, out oy);
            MoveLock(ox, oy, true);
        }

        /// <summary>Starts learning the comet's rates: from now, the lock position moves (yours and the comet's) are added up.</summary>
        public void StartCometTraining()
        {
            if (_phase != Phase.Guiding || Settings.Calibration == null) throw new InvalidOperationException(Lang.T("Start guiding first, then keep the comet centred by moving the lock position."));
            _trainEast = _trainNorth = 0;
            _trainStart = DateTime.UtcNow;
            CometTraining = true;
            _log.Event("Comet", "training started");
        }

        /// <summary>Ends training: the lock position moves since the start, over the time, become the comet's rates.</summary>
        public void StopCometTraining()
        {
            if (!CometTraining) return;
            CometTraining = false;
            double hours = (DateTime.UtcNow - _trainStart).TotalHours;
            if (hours < 10 / 3600.0) throw new InvalidOperationException(Lang.T("Train for longer: at least a minute gives usable rates."));
            Settings.CometRaArcsecPerHour = _trainEast / hours;
            Settings.CometDecArcsecPerHour = _trainNorth / hours;
            Settings.CometSource = CometRateSource.Trained;
            SaveSettings();
            if (CometFollowing) UpdateCometRates();
            string what = string.Format(CultureInfo.InvariantCulture, "Comet rates trained: RA {0:0.0}\"/h, Dec {1:0.0}\"/h", Settings.CometRaArcsecPerHour, Settings.CometDecArcsecPerHour);
            _log.Event("Comet", what);
            Log.Info("Guider", what);
        }

        private void Train(double ox, double oy)
        {
            var cal = Settings.Calibration;
            if (!CometTraining || cal == null) return;
            double e, n;
            cal.ImageToSky(ox, oy, PixelScale, MountPierSide(), Settings.MountReversesDecAfterFlip, out e, out n);
            _trainEast += e; _trainNorth += n;
        }

        // ------------------------------------------------------------------ manual guide

        /// <summary>
        /// Sends one guide pulse while looping (not while calibrating or guiding) and measures, in the next frame, how far
        /// the guide star moved; with a calibration, compares it with what was expected.
        /// </summary>
        public async Task<ManualPulseResult> ManualPulse(GuideDirection dir, int ms, CancellationToken ct)
        {
            if (!IsRunning) throw new InvalidOperationException(Lang.T("Click Loop first, so the pulse's effect can be seen."));
            if (_phase != Phase.Watching) throw new InvalidOperationException(Lang.T("Manual pulses are for looping, not while calibrating or guiding."));
            var pulser = new GuidePulser(_dm, Settings.Output);
            var problem = pulser.Problem();
            if (problem != null) throw new InvalidOperationException(problem);
            var res = new ManualPulseResult { Direction = dir, Ms = Math.Max(1, ms) };
            await RunInLoop(async t =>
            {
                var before = Star != null && !_lost ? Star : null;
                await SendPulse(pulser, dir, res.Ms, t);
                Track(await Frame(t));
                var after = Star != null && !_lost ? Star : null;
                if (before == null || after == null) return;
                res.Dx = after.X - before.X; res.Dy = after.Y - before.Y;
                res.Distance = Math.Sqrt(res.Dx * res.Dx + res.Dy * res.Dy);
                res.AngleDeg = Math.Atan2(res.Dy, res.Dx) * AstroMath.Rad2Deg;
                var cal = Settings.Calibration;
                if (cal == null) return;
                double rx, ry, nx, ny;
                cal.Directions(MountPierSide(), Settings.MountReversesDecAfterFlip, out rx, out ry, out nx, out ny);
                bool ra = dir == GuideDirection.West || dir == GuideDirection.East;
                double sign = dir == GuideDirection.West || dir == GuideDirection.North ? 1 : -1;
                double ux = sign * (ra ? rx : nx), uy = sign * (ra ? ry : ny);
                res.ExpectedAngleDeg = Math.Atan2(uy, ux) * AstroMath.Rad2Deg;
                res.ExpectedDistance = res.Ms / 1000.0 * (ra ? cal.RaRateAt(MountDeclination()) : cal.DecRatePxPerSec);
                res.Matches = Math.Abs(AstroMath.NormalizeDeg180(res.AngleDeg - res.ExpectedAngleDeg)) <= 30 && Math.Abs(res.Distance / res.ExpectedDistance - 1) <= 0.5;
            }, ct);
            _log.Row("Manual " + dir, Star, res.Dx, res.Dy, double.NaN, double.NaN, dir == GuideDirection.West || dir == GuideDirection.East ? res.Ms : 0, dir == GuideDirection.North || dir == GuideDirection.South ? res.Ms : 0);
            Log.Info("Guider", string.Format(CultureInfo.InvariantCulture, "Manual {0} pulse of {1} ms moved the star {2:0.00} px", dir, res.Ms, res.Distance));
            return res;
        }

        // ------------------------------------------------------------------ dark frames

        private volatile bool _building;

        /// <summary>
        /// Takes a dark library: framesEach dark frames at each exposure, combined into master darks. The guide scope must
        /// be covered; the guider must not be running.
        /// </summary>
        public async Task BuildDarkLibrary(IList<double> exposures, int framesEach, IProgress<double> progress, CancellationToken ct)
        {
            var cam = BeginBuilding();
            try
            {
                double temp = double.NaN;
                try { temp = cam.CcdTemperature; } catch { }
                Darks.ClearLibrary(_cameraId);
                int total = Math.Max(1, exposures.Count * framesEach), done = 0;
                foreach (var e in exposures)
                {
                    var frames = new List<AstroImage>();
                    for (int i = 0; i < framesEach; i++)
                    {
                        frames.Add(await CaptureService.GrabFrame(cam, e, ct, false));
                        progress?.Report(++done / (double)total);
                    }
                    var master = GuideDarks.MedianOf(frames);
                    master.ExposureSeconds = e;
                    Darks.SaveMaster(SetupOf(frames[0]), e, master, framesEach, temp);
                }
                Log.Info("Guider", string.Format(CultureInfo.InvariantCulture, "Dark library built: {0} exposures, {1} frames each", exposures.Count, framesEach));
            }
            finally { _building = false; }
        }

        /// <summary>Takes dark frames, combines them, and keeps the hot and cold pixels as a bad-pixel map.</summary>
        public async Task<BadPixelMapInfo> BuildBadPixelMap(double exposure, int frameCount, double sigma, IProgress<double> progress, CancellationToken ct)
        {
            var cam = BeginBuilding();
            try
            {
                var frames = new List<AstroImage>();
                for (int i = 0; i < frameCount; i++)
                {
                    frames.Add(await CaptureService.GrabFrame(cam, exposure, ct, false));
                    progress?.Report((i + 1) / (double)frameCount);
                }
                var master = GuideDarks.MedianOf(frames);
                master.ExposureSeconds = exposure;
                var map = GuideDarks.FindBadPixels(master, SetupOf(frames[0]), sigma);
                Darks.SaveMap(map);
                Log.Info("Guider", string.Format("Bad-pixel map built: {0} hot and {1} cold pixels", map.Hot, map.Cold));
                return map;
            }
            finally { _building = false; }
        }

        private ICamera BeginBuilding()
        {
            lock (_lock)
            {
                if (_session != null) throw new InvalidOperationException(Lang.T("Stop the guider before taking dark frames."));
                if (_building) throw new InvalidOperationException(Lang.T("Dark frames are being taken with the guide camera."));
                _building = true;
            }
            try
            {
                PrepareCamera();
                return _dm.GuideCamera;
            }
            catch { _building = false; throw; }
        }

        // ------------------------------------------------------------------ the Dec backlash test

        /// <summary>
        /// Measures Dec backlash while guiding is paused: North steps (after taking up the backlash) until the star has moved
        /// the calibration distance, then South steps until it is back. The backlash is where a line fitted through the
        /// South steps' positions meets the start: the pulse time spent before the star moved back.
        /// </summary>
        public async Task<BacklashMeasurement> MeasureBacklash(CancellationToken ct)
        {
            var cal = Settings.Calibration;
            if (_phase != Phase.Guiding || cal == null) throw new InvalidOperationException(Lang.T("Not guiding."));
            var res = new BacklashMeasurement();
            await RunInLoop(async t =>
            {
                string lost = Lang.T("The guide star was lost during the backlash test.");
                string slewed = Lang.T("The backlash test stopped because the mount slewed.");
                var pulser = new GuidePulser(_dm, Settings.Output);
                double decRate = cal.DecRatePxPerSec, dist = Math.Max(5, Settings.CalibrationDistancePx);
                double stepPx = Math.Min(0.6 * Settings.SearchRadius, dist / 8);
                res.StepMs = (int)AstroMath.Clamp(stepPx / Math.Max(1e-3, decRate) * 1000, 50, 3000);
                double rx, ry, nx, ny;
                cal.Directions(MountPierSide(), Settings.MountReversesDecAfterFlip, out rx, out ry, out nx, out ny);
                Func<double> north = () => Star.X * nx + Star.Y * ny;
                await NextPosition(t, slewed, lost);
                await ClearBacklash(res.StepMs, decRate, pulser, t, lost);
                double n0 = north();
                var tn = new List<double> { 0 }; var pn = new List<double> { 0 };
                int total = 0;
                while (north() - n0 < dist && res.NorthSteps < 40)
                {
                    await SendPulse(pulser, GuideDirection.North, res.StepMs, t);
                    total += res.StepMs; res.NorthSteps++;
                    await NextPosition(t, slewed, lost);
                    tn.Add(total / 1000.0); pn.Add(north() - n0);
                    _log.Row("Backlash test North", Star, double.NaN, double.NaN, double.NaN, pn[pn.Count - 1], 0, res.StepMs);
                }
                var fn = LinAlg.PolyFit(tn, pn, 1);
                res.NorthRatePxPerSec = fn != null ? fn[1] : double.NaN;
                double top = north(), went = top - n0;
                var ts = new List<double> { 0 }; var ps = new List<double> { 0 };
                total = 0;
                while (top - north() < 0.95 * went && res.SouthSteps < 60)
                {
                    await SendPulse(pulser, GuideDirection.South, res.StepMs, t);
                    total += res.StepMs; res.SouthSteps++;
                    await NextPosition(t, slewed, lost);
                    ts.Add(total / 1000.0); ps.Add(top - north());
                    _log.Row("Backlash test South", Star, double.NaN, double.NaN, double.NaN, -ps[ps.Count - 1], 0, res.StepMs);
                }
                // the steps after the star started back: more than a pixel and 15 % of the way
                double moving = Math.Max(1, 0.15 * went);
                var mt = new List<double>(); var mp = new List<double>();
                for (int i = 0; i < ts.Count; i++) if (ps[i] > moving) { mt.Add(ts[i]); mp.Add(ps[i]); }
                if (mt.Count >= 3)
                {
                    var fs = LinAlg.PolyFit(mt, mp, 1);
                    if (fs != null && fs[1] > 0)
                    {
                        res.SouthRatePxPerSec = fs[1];
                        res.Ms = Math.Max(0, -fs[0] / fs[1] * 1000);
                        double ss = 0;
                        for (int i = 0; i < mt.Count; i++) { double d = mp[i] - (fs[0] + fs[1] * mt[i]); ss += d * d; }
                        double sigma = Math.Sqrt(ss / Math.Max(1, mt.Count - 2));
                        res.UncertaintyMs = sigma / fs[1] * 1000;
                        res.Valid = res.NorthRatePxPerSec > 0 && res.SouthRatePxPerSec >= 0.9 * res.NorthRatePxPerSec;
                    }
                }
                _log.Event("Backlash test", string.Format(CultureInfo.InvariantCulture, "{0:0} ± {1:0} ms, North {2:0.00} px/s, South {3:0.00} px/s{4}",
                    res.Ms, res.UncertaintyMs, res.NorthRatePxPerSec, res.SouthRatePxPerSec, res.Valid ? "" : " (not valid)"));
            }, ct);
            return res;
        }

        /// <summary>Keeps a Guiding Assistant result (the last three) with the settings.</summary>
        public void RecordAssistant(GuidingAssistantResult r)
        {
            Settings.AssistantResults.Insert(0, r);
            while (Settings.AssistantResults.Count > 3) Settings.AssistantResults.RemoveAt(Settings.AssistantResults.Count - 1);
            SaveSettings();
        }
    }
}
