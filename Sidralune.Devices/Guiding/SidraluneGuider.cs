using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Sidralune.Core.Mathematics;
using Sidralune.Core.Util;
using Sidralune.Devices.Automation;
using Sidralune.Devices.Simulators;
using Sidralune.Imaging;

namespace Sidralune.Devices.Guiding
{
    /// <summary>
    /// Sidralune's own guider. It takes short exposures with the guide camera, follows one guide star, calibrates how
    /// guide pulses move it, and corrects its drift from the lock position with Hysteresis in RA and Resist Switch in Dec,
    /// through the mount or the guide camera's ST-4 port. One loop owns the camera: it watches, calibrates or guides.
    /// </summary>
    public class SidraluneGuider : DeviceBase, IGuider
    {
        // the states, in the words the interface already uses for PHD2's
        public const string Stopped = "Stopped", Looping = "Looping", Selected = "Selected", Calibrating = "Calibrating", Guiding = "Guiding", Paused = "Paused", LostLock = "LostLock";

        private enum Phase { Watching, Calibrating, Guiding }

        private readonly DeviceManager _dm;
        private readonly object _lock = new object();
        private readonly List<GuideStep> _history = new List<GuideStep>();
        private readonly Random _rng = new Random();
        private readonly GuideLog _log = new GuideLog();
        private readonly HysteresisAlgorithm _raAlgo = new HysteresisAlgorithm();
        private readonly ResistSwitchAlgorithm _decAlgo = new ResistSwitchAlgorithm();
        private CancellationTokenSource _session; // the running loop, null when stopped
        private Task _loop;
        private volatile string _state = Stopped;
        private volatile Phase _phase = Phase.Watching;
        private volatile bool _paused, _lost, _selectRequested, _calibrationRequested, _resetAlgorithms, _keepLock;
        private TaskCompletionSource<bool> _calibrationDone;
        private long _frames;   // frames measured so far
        private int _guideRun;  // counts the starts of guiding, so a wait notices guiding stopping and starting again
        private string _stopReason;
        // the guide camera as prepared for this session
        private int _bin = 1;
        private double _pixelUm, _focal;
        private string _cameraId = "";

        public GuiderSettings Settings { get; private set; }
        /// <summary>Where the settings are saved (null: Settings\guider.json).</summary>
        public string SettingsPath { get; private set; }
        /// <summary>Where guide logs go (null: the Logs folder).</summary>
        public string LogFolder { get; set; }
        public AstroImage LastFrame { get; private set; }
        /// <summary>The guide star's last measured position; when Lost, where it was last seen.</summary>
        public GuideStarInfo Star { get; private set; }
        public bool Lost => _lost;
        /// <summary>Where the guide star is held (image pixels), NaN when there is no star.</summary>
        public double LockX { get; private set; } = double.NaN;
        public double LockY { get; private set; } = double.NaN;
        /// <summary>Why the guider stopped by itself, until it is started again.</summary>
        public string LastError { get; private set; }
        public GuideCalibration Calibration => Settings.Calibration;
        public string GuideLogPath => _log.Path;
        /// <summary>
        /// When starting guiding has settled: the star within SettlePixels of the lock for SettleSeconds, giving up after
        /// SettleTimeoutSeconds (the values the PHD2 client asks PHD2 for).
        /// </summary>
        public double SettlePixels = 1.5, SettleSeconds = 10, SettleTimeoutSeconds = 120;

        public event Action<GuideStep> Step;
        /// <summary>A guide frame has been taken and measured (raised on the guider's thread).</summary>
        public event Action<AstroImage> FrameReady;
        public event Action CalibrationChanged;
        /// <summary>The guider stopped by itself (the camera failed, for instance), with the reason for the user.</summary>
        public event Action<string> Failed;

        public SidraluneGuider(DeviceManager dm, string settingsPath = null)
        {
            _dm = dm;
            SettingsPath = settingsPath;
            Info = new DeviceInfo { Kind = DeviceKind.Guider, Protocol = DeviceProtocol.Builtin, Name = DeviceManager.BuiltinGuiderName, Address = "builtin" };
            Settings = GuiderSettings.Load(settingsPath);
        }

        public override void Connect() { Connected = true; }
        public override void Disconnect() { EndSession(); Connected = false; }

        public string State => _state;
        public bool IsGuiding => _state == Guiding;
        public bool IsCalibrating => _phase == Phase.Calibrating;
        /// <summary>Looping, calibrating or guiding.</summary>
        public bool IsRunning { get { lock (_lock) return _session != null; } }

        /// <summary>The guide camera's arcseconds per pixel from its pixel size, the binning and the guide scope's focal length.</summary>
        public double PixelScale
        {
            get
            {
                double px = _pixelUm > 0 ? _pixelUm : Settings.Calibration != null ? Settings.Calibration.PixelSizeUm : 0;
                if (px <= 0 && _dm.GuideCamera != null) try { px = _dm.GuideCamera.PixelSizeX; } catch { }
                return px > 0 && Settings.FocalLengthMm > 0 ? 206.265 * px * Math.Max(1, Settings.Binning) / Settings.FocalLengthMm : 1;
            }
        }

        public double RmsRaArcsec => Rms(s => s.RaArcsec);
        public double RmsDecArcsec => Rms(s => s.DecArcsec);
        public double RmsTotalArcsec => Math.Sqrt(RmsRaArcsec * RmsRaArcsec + RmsDecArcsec * RmsDecArcsec);
        public List<GuideStep> History { get { lock (_history) return new List<GuideStep>(_history); } }

        private double Rms(Func<GuideStep, double> f)
        {
            lock (_history)
            {
                var recent = _history.Skip(Math.Max(0, _history.Count - 100)).ToList();
                return recent.Count == 0 ? double.NaN : Math.Sqrt(recent.Average(s => f(s) * f(s)));
            }
        }

        public void SaveSettings() => Settings.Save(SettingsPath);

        public void ClearCalibration()
        {
            Settings.Calibration = null;
            SaveSettings();
            Log.Info("Guider", "Calibration cleared");
            CalibrationChanged?.Invoke();
        }

        // ------------------------------------------------------------------ starting and stopping

        /// <summary>Starts taking guide frames (and following the star once there is one), if not already.</summary>
        public void Loop()
        {
            lock (_lock)
            {
                if (_session != null) return;
                var cam = _dm.GuideCamera;
                if (cam == null || !cam.Connected) throw new InvalidOperationException(Lang.T("No guide camera connected."));
                LastError = null;
                _stopReason = null;
                var cts = new CancellationTokenSource();
                _session = cts;
                _phase = Phase.Watching;
                var previous = _loop;
                // a loop still ending (Stop then Start at once) finishes with the camera first
                _loop = Task.Run(async () =>
                {
                    if (previous != null) try { await previous; } catch { }
                    await Run(cts);
                });
            }
            UpdateState();
        }

        /// <summary>Stops guiding and taking frames; any wait for settling ends.</summary>
        public void StopGuiding()
        {
            _stopReason = Lang.T("Guiding was stopped.");
            EndSession();
        }

        private void EndSession()
        {
            CancellationTokenSource s;
            // the star goes with the session: after a stop the mount may move, and the next start chooses again
            lock (_lock) { s = _session; _session = null; Star = null; _lost = false; LockX = LockY = double.NaN; }
            _phase = Phase.Watching;
            if (s != null)
            {
                s.Cancel();
                _log.Event("Stopped", "");
                _log.Dispose();
                Log.Info("Guider", "Stopped");
            }
            SetState(Stopped);
        }

        private CancellationToken SessionToken { get { lock (_lock) return _session != null ? _session.Token : new CancellationToken(true); } }

        /// <summary>Guide corrections stop (Pause(true)) or start again; frames go on, so the star is still followed.</summary>
        public void Pause(bool paused)
        {
            _paused = paused;
            _resetAlgorithms = true;
            _log.Event(paused ? "Paused" : "Resumed", "");
            UpdateState();
        }

        /// <summary>
        /// Chooses the guide star: the one nearest a point (a click on the frame), or the best in the frame. On the next
        /// frame when there is none yet.
        /// </summary>
        public void SelectStar(double x = double.NaN, double y = double.NaN)
        {
            if (_phase != Phase.Watching) throw new InvalidOperationException(Lang.T("Stop guiding before choosing another star."));
            var f = LastFrame;
            if (f == null) { _selectRequested = true; return; }
            var s = GuideStars.Select(f.Luminance(), f.Width, f.Height, Settings.SearchRadius, x, y, EdgeMargin);
            if (s == null) throw new InvalidOperationException(double.IsNaN(x) ? Lang.T("No suitable guide star in the frame.") : Lang.T("No star there to guide on."));
            lock (_lock) { Star = s; _lost = false; LockX = s.X; LockY = s.Y; }
            Log.Info("Guider", "Guide star " + s);
            UpdateState();
        }

        public async Task StartGuiding(bool recalibrate, CancellationToken ct)
        {
            var problem = new GuidePulser(_dm, Settings.Output).Problem();
            if (problem != null) throw new InvalidOperationException(problem);
            Loop();
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, SessionToken))
            {
                var lt = linked.Token;
                try
                {
                    await EnsureStar(lt);
                    if (recalibrate || !CalibrationFits())
                    {
                        if (_phase == Phase.Guiding) { _phase = Phase.Watching; UpdateState(); }
                        await Calibrate(lt);
                    }
                    if (_phase != Phase.Guiding) BeginGuiding();
                    if (!await WaitSettled(SettlePixels, SettleSeconds, SettleTimeoutSeconds, lt)) throw new InvalidOperationException(Lang.T("Guiding did not settle within {0} s.", SettleTimeoutSeconds));
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // the guider stopped (Stop, a camera failure), not the caller
                    throw new InvalidOperationException(LastError ?? _stopReason ?? Lang.T("Guiding was stopped."));
                }
            }
        }

        public async Task Dither(double pixels, bool raOnly, double settlePixels, double settleTimeSec, double timeoutSec, CancellationToken ct)
        {
            var cal = Settings.Calibration;
            if (_phase != Phase.Guiding || cal == null) throw new InvalidOperationException(Lang.T("Not guiding."));
            double a = (_rng.NextDouble() * 2 - 1) * pixels, b = raOnly ? 0 : (_rng.NextDouble() * 2 - 1) * pixels;
            double ox, oy;
            cal.FromAxes(a, b, MountPierSide(), Settings.MountReversesDecAfterFlip, out ox, out oy);
            var f = LastFrame;
            int margin = Settings.SearchRadius + 8;
            lock (_lock)
            {
                // keep the lock position well inside the frame
                if (f != null && (LockX + ox < margin || LockX + ox > f.Width - margin)) ox = -ox;
                if (f != null && (LockY + oy < margin || LockY + oy > f.Height - margin)) oy = -oy;
                LockX += ox; LockY += oy;
            }
            _resetAlgorithms = true;
            _log.Event("Dither", string.Format(CultureInfo.InvariantCulture, "{0:0.00} {1:0.00}", ox, oy));
            Log.Info("Guider", string.Format(CultureInfo.InvariantCulture, "Dither by {0:0.00}, {1:0.00} px", ox, oy));
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, SessionToken))
            {
                bool ok;
                try { ok = await WaitSettled(settlePixels, settleTimeSec, timeoutSec, linked.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new InvalidOperationException(LastError ?? _stopReason ?? Lang.T("Guiding was stopped.")); }
                _log.Event(ok ? "Settled" : "Settle timed out", "");
                if (!ok) Log.Warn("Guider", "Dither settle timed out");
            }
        }

        /// <summary>Room a chosen star needs from the edges: calibration moves it the calibration distance in any direction.</summary>
        private int EdgeMargin => Settings.SearchRadius + (int)Math.Ceiling(Settings.CalibrationDistancePx) + 5;

        private bool CalibrationFits()
        {
            var c = Settings.Calibration;
            return c != null && c.Fits(_bin, _pixelUm, Settings.FocalLengthMm, Settings.Output, _cameraId);
        }

        private async Task EnsureStar(CancellationToken ct)
        {
            if (Star != null && !_lost) return;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                _selectRequested = true;
                while (_selectRequested) await Task.Delay(50, ct);
                if (Star != null && !_lost) return;
            }
            throw new InvalidOperationException(Lang.T("No guide star found. Check the guide camera's focus and exposure."));
        }

        private async Task Calibrate(CancellationToken ct)
        {
            var done = new TaskCompletionSource<bool>();
            _calibrationDone = done;
            _calibrationRequested = true;
            using (ct.Register(() => done.TrySetCanceled())) await done.Task;
        }

        private void BeginGuiding()
        {
            lock (_lock)
            {
                if (!_keepLock && Star != null) { LockX = Star.X; LockY = Star.Y; }
                _keepLock = false;
            }
            _resetAlgorithms = true;
            _paused = false;
            Interlocked.Increment(ref _guideRun);
            _log.Open(LogFolder);
            _log.Event("Guiding", string.Format(CultureInfo.InvariantCulture, "lock {0:0.00} {1:0.00}", LockX, LockY));
            _phase = Phase.Guiding;
            Log.Info("Guider", "Guiding");
            UpdateState();
        }

        /// <summary>
        /// Waits until the star has stayed within px of the lock position for sec seconds; false after timeout seconds.
        /// Throws when guiding stops meanwhile (a slew, for instance).
        /// </summary>
        private async Task<bool> WaitSettled(double px, double sec, double timeout, CancellationToken ct)
        {
            var start = DateTime.UtcNow;
            var within = DateTime.MinValue;
            int run = _guideRun;
            long seen = Interlocked.Read(ref _frames);
            while (true)
            {
                if ((DateTime.UtcNow - start).TotalSeconds > timeout) return false;
                await Task.Delay(50, ct);
                if (_phase != Phase.Guiding || _guideRun != run) throw new InvalidOperationException(_stopReason ?? Lang.T("Guiding was stopped."));
                long f = Interlocked.Read(ref _frames);
                if (f == seen) continue;
                seen = f;
                var s = Star;
                if (_lost || s == null) { within = DateTime.MinValue; continue; }
                double dx, dy;
                lock (_lock) { dx = s.X - LockX; dy = s.Y - LockY; }
                if (Math.Sqrt(dx * dx + dy * dy) <= px)
                {
                    if (within == DateTime.MinValue) within = DateTime.UtcNow;
                    if ((DateTime.UtcNow - within).TotalSeconds >= sec) return true;
                }
                else within = DateTime.MinValue;
            }
        }

        // ------------------------------------------------------------------ the loop

        private async Task Run(CancellationTokenSource cts)
        {
            var ct = cts.Token;
            try
            {
                PrepareCamera();
                while (!ct.IsCancellationRequested)
                {
                    if (_calibrationRequested)
                    {
                        _calibrationRequested = false;
                        var done = _calibrationDone;
                        try { await RunCalibration(ct); done.TrySetResult(true); }
                        catch (OperationCanceledException) { done.TrySetCanceled(); throw; }
                        catch (Exception ex)
                        {
                            Log.Warn("Guider", ex.Message);
                            _log.Event("Calibration failed", ex.Message);
                            _phase = Phase.Watching;
                            UpdateState();
                            done.TrySetException(ex);
                        }
                        continue;
                    }
                    if (MountSlewing())
                    {
                        // a slew takes the star away: guiding ends, and the star must be chosen again after it
                        if (_phase == Phase.Guiding)
                        {
                            _stopReason = Lang.T("Guiding stopped because the mount slewed.");
                            Log.Info("Guider", "Guiding stopped: the mount is slewing");
                            _log.Event("Slew", "guiding stopped");
                        }
                        _phase = Phase.Watching;
                        lock (_lock) { Star = null; _lost = false; LockX = LockY = double.NaN; }
                        UpdateState();
                        while (MountSlewing()) await Task.Delay(200, ct);
                        continue;
                    }
                    if (_bin != Math.Max(1, Settings.Binning) || _focal != Settings.FocalLengthMm) PrepareCamera();
                    Track(await Frame(ct));
                    if (_phase == Phase.Guiding && !_paused && !_lost && Star != null) await Correct(ct);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Fail(ex.Message, cts); }
            finally
            {
                bool current;
                lock (_lock) { current = _session == cts || _session == null; if (_session == cts) _session = null; }
                if (current) { _phase = Phase.Watching; SetState(Stopped); }
            }
        }

        private void Fail(string message, CancellationTokenSource cts)
        {
            LastError = message;
            Log.Error("Guider", message);
            _log.Event("Error", message);
            lock (_lock) if (_session == cts) _session = null;
            cts.Cancel();
            _phase = Phase.Watching;
            _log.Dispose();
            SetState(Stopped);
            Failed?.Invoke(message);
        }

        private void PrepareCamera()
        {
            var cam = _dm.GuideCamera;
            if (cam == null || !cam.Connected) throw new InvalidOperationException(Lang.T("No guide camera connected."));
            int max = 1;
            try { max = Math.Max(1, cam.MaxBin); } catch { }
            int bin = Math.Max(1, Math.Min(Settings.Binning, max));
            try { cam.BinX = bin; cam.BinY = bin; } catch (Exception ex) { Log.Warn("Guider", "Binning: " + ex.Message); }
            try { cam.StartX = 0; cam.StartY = 0; cam.NumX = cam.SensorWidth / bin; cam.NumY = cam.SensorHeight / bin; } catch (Exception ex) { Log.Warn("Guider", "Frame: " + ex.Message); }
            _bin = Math.Max(1, Settings.Binning);
            _focal = Settings.FocalLengthMm;
            try { _pixelUm = cam.PixelSizeX; } catch { _pixelUm = 0; }
            _cameraId = cam.Info.Protocol + " " + cam.Info.Address;
            // the simulated guide camera sits on the guide scope the settings describe
            var sim = cam as CameraSimulator;
            if (sim != null) sim.FocalLengthMm = Settings.FocalLengthMm;
        }

        private async Task<AstroImage> Frame(CancellationToken ct)
        {
            for (int attempt = 1; ; attempt++)
            {
                var cam = _dm.GuideCamera;
                if (cam == null || !cam.Connected) throw new InvalidOperationException(Lang.T("The guide camera was disconnected."));
                try { return await CaptureService.GrabFrame(cam, Math.Max(0.001, Settings.ExposureSeconds), ct); }
                catch (Exception ex) when (!(ex is OperationCanceledException))
                {
                    Log.Warn("Guider", "Guide frame failed: " + ex.Message);
                    if (attempt >= 3) throw new InvalidOperationException(Lang.T("The guide camera failed three times in a row: {0}", ex.Message));
                    await Task.Delay(500, ct);
                }
            }
        }

        /// <summary>Measures the guide star in a new frame (or chooses one when asked).</summary>
        private void Track(AstroImage frame)
        {
            var lum = frame.Luminance();
            int w = frame.Width, h = frame.Height;
            if (_selectRequested)
            {
                var s = GuideStars.Select(lum, w, h, Settings.SearchRadius, double.NaN, double.NaN, EdgeMargin);
                lock (_lock)
                {
                    if (s != null) { Star = s; _lost = false; if (_phase == Phase.Watching) { LockX = s.X; LockY = s.Y; } }
                }
                if (s != null) Log.Info("Guider", "Guide star " + s);
                _selectRequested = false;
            }
            else if (Star != null)
            {
                var m = GuideStars.Measure(lum, w, h, Star.X, Star.Y, Settings.SearchRadius);
                lock (_lock)
                {
                    if (m != null)
                    {
                        if (_lost) { _log.Event("Star found", m.ToString()); Log.Info("Guider", "Guide star found again"); }
                        Star = m; _lost = false;
                        if (_phase == Phase.Watching) { LockX = m.X; LockY = m.Y; }
                    }
                    else if (!_lost)
                    {
                        _lost = true;
                        _log.Event("Star lost", "");
                        Log.Warn("Guider", "Guide star lost");
                    }
                }
            }
            LastFrame = frame;
            Interlocked.Increment(ref _frames);
            UpdateState();
            FrameReady?.Invoke(frame);
        }

        /// <summary>The star's offset from the lock position in the image and along the RA and Dec pulse directions.</summary>
        private void Offset(GuideStarInfo s, out double raErr, out double decErr, out double ex, out double ey)
        {
            lock (_lock) { ex = s.X - LockX; ey = s.Y - LockY; }
            Settings.Calibration.ToAxes(ex, ey, MountPierSide(), Settings.MountReversesDecAfterFlip, out raErr, out decErr);
        }

        /// <summary>One guide step: the star's offset from the lock position, split into RA and Dec, corrected by pulses.</summary>
        private async Task Correct(CancellationToken ct)
        {
            var cal = Settings.Calibration;
            if (cal == null) return;
            if (_resetAlgorithms) { _raAlgo.Reset(); _decAlgo.Reset(); _resetAlgorithms = false; }
            _raAlgo.MinMovePx = Settings.RaMinMovePx; _raAlgo.Hysteresis = Settings.RaHysteresis / 100; _raAlgo.Aggressiveness = Settings.RaAggressiveness / 100;
            _decAlgo.MinMovePx = Settings.DecMinMovePx; _decAlgo.Aggressiveness = Settings.DecAggressiveness / 100; _decAlgo.FastSwitch = Settings.DecFastSwitch;
            var s = Star;
            double raErr, decErr, ex, ey;
            Offset(s, out raErr, out decErr, out ex, out ey);
            double raC = _raAlgo.Correction(raErr), decC = _decAlgo.Correction(decErr);
            double raRate = cal.RaRateAt(MountDeclination()), decRate = cal.DecRatePxPerSec;
            int raMs = raRate > 0 ? (int)Math.Round(Math.Min(Settings.MaxRaPulseMs, Math.Abs(raC) / raRate * 1000)) : 0;
            int decMs = decRate > 0 ? (int)Math.Round(Math.Min(Settings.MaxDecPulseMs, Math.Abs(decC) / decRate * 1000)) : 0;
            // the star has moved the way a West pulse moves it: an East pulse brings it back (and South for North)
            var raDir = raC > 0 ? GuideDirection.East : GuideDirection.West;
            var decDir = decC > 0 ? GuideDirection.South : GuideDirection.North;
            double scale = PixelScale;
            var step = new GuideStep
            {
                Time = DateTime.Now, RaDistance = raErr, DecDistance = decErr, RaArcsec = raErr * scale, DecArcsec = decErr * scale,
                RaPulse = raDir == GuideDirection.East ? raMs : -raMs, DecPulse = decDir == GuideDirection.North ? decMs : -decMs,
                Snr = s.Snr, StarMass = s.Flux
            };
            lock (_history) { _history.Add(step); if (_history.Count > 5000) _history.RemoveRange(0, 1000); }
            _log.Row("Guide", s, ex, ey, raErr, decErr, step.RaPulse, step.DecPulse);
            Step?.Invoke(step);
            var pulser = new GuidePulser(_dm, Settings.Output);
            if (raMs > 0) await pulser.Pulse(raDir, raMs, ct);
            if (decMs > 0) await pulser.Pulse(decDir, decMs, ct);
        }

        // ------------------------------------------------------------------ calibration

        private class Leg
        {
            public readonly List<double> T = new List<double>(), X = new List<double>(), Y = new List<double>();
            public int Steps, TotalMs;
        }

        /// <summary>
        /// Calibrates: West steps until the star has moved the calibration distance, back East, North steps (after taking
        /// up Dec backlash) and back South. Each leg's direction and rate come from a line fitted through all its positions.
        /// </summary>
        private async Task RunCalibration(CancellationToken ct)
        {
            _phase = Phase.Calibrating;
            UpdateState();
            _log.Open(LogFolder);
            _log.Event("Calibration", "started");
            Log.Info("Guider", "Calibrating");
            var pulser = new GuidePulser(_dm, Settings.Output);
            var problem = pulser.Problem();
            if (problem != null) throw new InvalidOperationException(problem);
            double dec = MountDeclination();
            var pier = MountPierSide();
            await NextPosition(ct);
            double startX = Star.X, startY = Star.Y;
            double scale = PixelScale;
            var tel = _dm.Telescope;
            double gRa = double.NaN, gDec = double.NaN;
            if (Settings.Output == GuideOutputKind.Mount && tel != null) { try { gRa = tel.GuideRateRa; } catch { } try { gDec = tel.GuideRateDec; } catch { } }
            bool raKnown = gRa > 0, decKnown = gDec > 0;
            double cosd = double.IsNaN(dec) ? 1 : Math.Max(0.05, Math.Cos(dec * AstroMath.Deg2Rad));
            // without the mount's word, 0.5× sidereal, the most common guide rate
            const double halfSidereal = 0.5 * 15.0410686 / 3600;
            double predRa = (raKnown ? gRa : halfSidereal) * 3600 * cosd / scale;
            double predDec = (decKnown ? gDec : halfSidereal) * 3600 / scale;
            double dist = Math.Max(5, Settings.CalibrationDistancePx);
            int stepRa = Settings.CalibrationStepMs > 0 ? Settings.CalibrationStepMs : StepFor(dist, predRa);

            var west = await RunLeg(GuideDirection.West, stepRa, dist, startX, startY, pulser, ct);
            double raAngle, raRate;
            if (!GuideCalibration.FitLeg(west.T, west.X, west.Y, out raAngle, out raRate)) throw new InvalidOperationException(Lang.T("Calibration failed: the star did not move far enough in RA. Check the guide cable or the mount's connection and guide rate."));
            await Return(GuideDirection.East, west.TotalMs, raRate, Settings.MaxRaPulseMs, pulser, ct);

            // what a Dec step should do, from the measured RA rate (the guide rates the mount reports can be wrong)
            double expDec = raRate / cosd * (raKnown && decKnown ? gDec / gRa : 1);
            int stepDec = Settings.CalibrationStepMs > 0 ? Settings.CalibrationStepMs : StepFor(dist, expDec);
            await ClearBacklash(stepDec, expDec, pulser, ct);
            var north = await RunLeg(GuideDirection.North, stepDec, dist, Star.X, Star.Y, pulser, ct);
            double decAngle, decRate;
            if (!GuideCalibration.FitLeg(north.T, north.X, north.Y, out decAngle, out decRate)) throw new InvalidOperationException(Lang.T("Calibration failed: the star did not move far enough in Dec. Check the guide cable or the mount's connection and guide rate."));
            await Return(GuideDirection.South, north.TotalMs, decRate, Settings.MaxDecPulseMs, pulser, ct);

            var cal = new GuideCalibration
            {
                RaAngleDeg = raAngle, DecAngleDeg = decAngle, RaRatePxPerSec = raRate, DecRatePxPerSec = decRate,
                ExpectedRaRatePxPerSec = raKnown ? predRa : double.NaN, ExpectedDecRatePxPerSec = decKnown ? predDec : double.NaN,
                DecDeg = dec, PierSide = pier, Binning = _bin, PixelSizeUm = _pixelUm, FocalLengthMm = Settings.FocalLengthMm,
                Output = Settings.Output, Camera = _cameraId, When = DateTime.UtcNow, RaSteps = west.Steps, DecSteps = north.Steps
            };
            cal.Check();
            Settings.Calibration = cal;
            SaveSettings();
            string summary = string.Format(CultureInfo.InvariantCulture, "RA {0:0.0}° {1:0.00} px/s ({2} steps), Dec {3:0.0}° {4:0.00} px/s ({5} steps), {6:0.0}° from square{7}",
                raAngle, raRate, west.Steps, decAngle, decRate, north.Steps, cal.OrthogonalityErrorDeg, cal.Issues.Count > 0 ? "; " + string.Join(", ", cal.Issues) : "");
            _log.Event("Calibrated", summary);
            Log.Info("Guider", "Calibrated: " + summary);
            CalibrationChanged?.Invoke();
            // guide back to where the star started
            lock (_lock) { LockX = startX; LockY = startY; }
            _keepLock = true;
            _phase = Phase.Watching;
            UpdateState();
        }

        /// <summary>A pulse for about twelve steps over the distance, between 0.1 and 3 s.</summary>
        private static int StepFor(double dist, double ratePxPerSec) =>
            ratePxPerSec > 0 && !double.IsInfinity(ratePxPerSec) ? (int)AstroMath.Clamp(Math.Round(dist / 12 / ratePxPerSec * 1000), 100, 3000) : 750;

        /// <summary>The next frame's star position; throws when the star is lost or the mount slews.</summary>
        private async Task NextPosition(CancellationToken ct)
        {
            if (MountSlewing()) throw new InvalidOperationException(Lang.T("Calibration stopped because the mount slewed."));
            Track(await Frame(ct));
            if (Star == null || _lost) throw new InvalidOperationException(Lang.T("The guide star was lost during calibration."));
        }

        private async Task<Leg> RunLeg(GuideDirection dir, int stepMs, double dist, double x0, double y0, GuidePulser pulser, CancellationToken ct)
        {
            var leg = new Leg();
            leg.T.Add(0); leg.X.Add(x0); leg.Y.Add(y0);
            bool ra = dir == GuideDirection.West || dir == GuideDirection.East;
            while (true)
            {
                if (leg.Steps >= 40)
                    throw new InvalidOperationException(ra ? Lang.T("Calibration failed: the star did not move far enough in RA. Check the guide cable or the mount's connection and guide rate.")
                                                           : Lang.T("Calibration failed: the star did not move far enough in Dec. Check the guide cable or the mount's connection and guide rate."));
                await pulser.Pulse(dir, stepMs, ct);
                leg.Steps++;
                leg.TotalMs += stepMs;
                await NextPosition(ct);
                leg.T.Add(leg.TotalMs / 1000.0); leg.X.Add(Star.X); leg.Y.Add(Star.Y);
                double dx = Star.X - x0, dy = Star.Y - y0;
                _log.Row("Calibration " + dir, Star, dx, dy, double.NaN, double.NaN, ra ? stepMs : 0, ra ? 0 : stepMs);
                if (Math.Sqrt(dx * dx + dy * dy) >= dist) return leg;
            }
        }

        /// <summary>Brings the star back by the total pulse time of a leg, in pulses short enough to keep it in the search box.</summary>
        private async Task Return(GuideDirection dir, int totalMs, double ratePxPerSec, int maxPulseMs, GuidePulser pulser, CancellationToken ct)
        {
            int chunk = (int)Math.Max(100, Math.Min(Math.Max(100, maxPulseMs), 0.6 * Settings.SearchRadius / Math.Max(1e-3, ratePxPerSec) * 1000));
            for (int left = totalMs; left > 0; left -= chunk)
            {
                int ms = Math.Min(chunk, left);
                await pulser.Pulse(dir, ms, ct);
                await NextPosition(ct);
                _log.Row("Calibration " + dir, Star, double.NaN, double.NaN, double.NaN, double.NaN, 0, 0);
            }
        }

        /// <summary>
        /// North steps until the star moves steadily: three steps in a row, each at least 60 % of the expected move and in
        /// the same direction. After 20 s of pulses it goes on if the star has moved 3 pixels at all, and gives up if not.
        /// </summary>
        private async Task ClearBacklash(int stepMs, double expectedRate, GuidePulser pulser, CancellationToken ct)
        {
            double expectedMove = expectedRate * stepMs / 1000;
            double sx = Star.X, sy = Star.Y, lx = sx, ly = sy, pdx = 0, pdy = 0;
            int good = 0, totalMs = 0;
            while (good < 3)
            {
                if (totalMs >= 20000)
                {
                    double moved = Math.Sqrt((Star.X - sx) * (Star.X - sx) + (Star.Y - sy) * (Star.Y - sy));
                    if (moved >= 3) break;
                    throw new InvalidOperationException(Lang.T("Calibration failed: Dec did not start moving. Dec may not be connected, or the mount has a lot of backlash."));
                }
                await pulser.Pulse(GuideDirection.North, stepMs, ct);
                totalMs += stepMs;
                await NextPosition(ct);
                double dx = Star.X - lx, dy = Star.Y - ly;
                lx = Star.X; ly = Star.Y;
                bool same = good == 0 || dx * pdx + dy * pdy > 0;
                if (Math.Sqrt(dx * dx + dy * dy) >= 0.6 * expectedMove && same) { good++; pdx = dx; pdy = dy; }
                else { good = 0; pdx = pdy = 0; }
                _log.Row("Backlash", Star, dx, dy, double.NaN, double.NaN, 0, stepMs);
            }
        }

        // ------------------------------------------------------------------ the mount

        private bool MountSlewing()
        {
            var tel = _dm.Telescope;
            try { return tel != null && tel.Connected && tel.Slewing; } catch { return false; }
        }

        private double MountDeclination()
        {
            var tel = _dm.Telescope;
            try { return tel != null && tel.Connected ? tel.Declination : double.NaN; } catch { return double.NaN; }
        }

        private PierSide MountPierSide()
        {
            var tel = _dm.Telescope;
            try { return tel != null && tel.Connected ? tel.SideOfPier : PierSide.Unknown; } catch { return PierSide.Unknown; }
        }

        private void UpdateState()
        {
            lock (_lock)
            {
                if (_session == null) { SetState(Stopped); return; }
            }
            string s;
            if (_phase == Phase.Calibrating) s = Calibrating;
            else if (_phase == Phase.Guiding) s = _lost ? LostLock : _paused ? Paused : Guiding;
            else s = Star != null && !_lost ? Selected : Looping;
            SetState(s);
        }

        private void SetState(string s)
        {
            if (_state == s) return;
            _state = s;
            RaiseStateChanged();
        }
    }
}
