using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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
    /// <summary>One measured frame while guiding (paused or not): the stars' offset from the lock position along RA and Dec.</summary>
    public class GuideFrame
    {
        public DateTime Time;
        /// <summary>Pixels along the West and North pulse directions.</summary>
        public double RaPx, DecPx;
        public bool Paused;
    }

    /// <summary>
    /// Sidralune's own guider. It takes short exposures with the guide camera, follows the guide star (and, with multi-star,
    /// others with it), calibrates how guide pulses move it, and corrects its drift from the lock position with the chosen
    /// algorithm on each axis, through the mount or the guide camera's ST-4 port. One loop owns the camera: it watches,
    /// calibrates or guides, and runs the jobs the tools give it (manual pulses, the backlash test) between frames.
    /// </summary>
    public partial class SidraluneGuider : DeviceBase, IGuider
    {
        // the states, in the words the interface already uses for PHD2's
        public const string Stopped = "Stopped", Looping = "Looping", Selected = "Selected", Calibrating = "Calibrating", Guiding = "Guiding", Paused = "Paused", LostLock = "LostLock";

        private enum Phase { Watching, Calibrating, Guiding }

        private readonly DeviceManager _dm;
        private readonly object _lock = new object();
        private readonly List<GuideStep> _history = new List<GuideStep>();
        private readonly Random _rng = new Random();
        private readonly GuideLog _log = new GuideLog();
        private readonly GuideStarSet _stars = new GuideStarSet();
        private IAxisAlgorithm _raAlgo, _decAlgo;
        private GuideAlgorithmKind? _raKind, _decKind;
        private readonly BacklashCompensation _backlash = new BacklashCompensation();
        private double _backlashFrom = double.NaN, _backlashMinFrom = double.NaN, _backlashMaxFrom = double.NaN;
        private CancellationTokenSource _session; // the running loop, null when stopped
        private Task _loop;
        private volatile string _state = Stopped;
        private volatile Phase _phase = Phase.Watching;
        private volatile bool _paused, _lost, _selectRequested, _calibrationRequested, _resetAlgorithms, _keepLock, _userLock, _usable, _rejected;
        private TaskCompletionSource<bool> _calibrationDone;
        private long _frames;   // frames measured so far
        private int _guideRun;  // counts the starts of guiding, so a wait notices guiding stopping and starting again
        // the next start chooses the star nearest this place and holds it here (after Sidralune's own centring)
        private double _resumeX = double.NaN, _resumeY = double.NaN;
        // the spiral of dithers: the lock position it turns round, its step, and where the last dither put the lock
        private double _spiralX = double.NaN, _spiralY = double.NaN, _spiralLastX = double.NaN, _spiralLastY = double.NaN;
        private int _spiralStep;
        private string _stopReason;
        private double _errX, _errY; // the stars' offset from the lock position in the last frame (image pixels)
        // the guide camera as prepared for this session
        private int _bin = 1, _gain = -1, _offset = -1;
        private double _pixelUm, _focal;
        private string _cameraId = "";
        private double _autoExposure = double.NaN;
        private int _snrHigh, _snrLow;
        private double _lastDarkUsed = double.NaN;
        private bool _rotatorTurned, _relock;

        public GuiderSettings Settings { get; private set; }
        /// <summary>The devices the guider works with.</summary>
        public DeviceManager Devices => _dm;
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
        /// <summary>A frame was measured while guiding, paused or not (raised on the guider's thread); the Guiding Assistant listens.</summary>
        public event Action<GuideFrame> Measured;

        public SidraluneGuider(DeviceManager dm, string settingsPath = null)
        {
            _dm = dm;
            SettingsPath = settingsPath;
            Info = new DeviceInfo { Kind = DeviceKind.Guider, Protocol = DeviceProtocol.Builtin, Name = DeviceManager.BuiltinGuiderName, Address = "builtin" };
            Settings = GuiderSettings.Load(settingsPath);
        }

        public override void Connect() { Connected = true; }

        /// <summary>
        /// Uses another settings file (another equipment profile's): its calibration, algorithms and tuning. Refused while
        /// the guider takes frames.
        /// </summary>
        public void UseSettingsFile(string path)
        {
            if (string.Equals(path ?? GuiderSettings.DefaultPath, SettingsPath ?? GuiderSettings.DefaultPath, StringComparison.OrdinalIgnoreCase)) return;
            if (IsRunning || _building) throw new InvalidOperationException(Lang.T("Stop the guider first."));
            SettingsPath = path;
            Settings = GuiderSettings.Load(path);
            // the algorithms are made again from the new settings
            _raAlgo = null; _decAlgo = null;
            CalibrationChanged?.Invoke();
            SettingsReplaced?.Invoke();
        }

        /// <summary>Raised when another settings file is used (another equipment profile).</summary>
        public event Action SettingsReplaced;
        public override void Disconnect() { EndSession(); Connected = false; }

        public string State => _state;
        public bool IsGuiding => _state == Guiding;
        public bool IsCalibrating => _phase == Phase.Calibrating;
        /// <summary>Guiding, whether or not corrections are paused or the star is lost for now.</summary>
        public bool InGuidingPhase => _phase == Phase.Guiding;
        /// <summary>Looping, calibrating or guiding.</summary>
        public bool IsRunning { get { lock (_lock) return _session != null; } }
        /// <summary>How many stars the last frame was guided on.</summary>
        public int StarsUsed => _stars.LastUsed;
        /// <summary>The last frame was not used: the guide star's brightness changed too much.</summary>
        public bool FrameRejected => _rejected;
        /// <summary>The other stars guided on: x, y and 1 when found in the last frame.</summary>
        public List<double[]> SecondaryStars { get { lock (_lock) return _stars.Secondaries.Select(s => new[] { s.Info.X, s.Info.Y, s.FoundNow ? 1.0 : 0.0 }).ToList(); } }
        /// <summary>The Dec backlash compensation in use now (ms), as it has adapted.</summary>
        public double BacklashCompensationMs => _backlash.AmountMs;

        /// <summary>
        /// The guide camera's arcseconds per pixel: from a solved guide frame while it fits the equipment, else from its
        /// pixel size, the binning and the guide scope's focal length.
        /// </summary>
        public double PixelScale
        {
            get
            {
                var solved = CurrentSolve;
                if (solved != null) return solved.ScaleArcsec;
                double px = CameraPixelUm();
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

        // ------------------------------------------------------------------ exposure

        private static readonly double[] ExposureSteps = { 0.5, 1, 1.5, 2, 2.5, 3, 4, 5, 6, 8, 10 };

        /// <summary>The exposures auto exposure chooses from, between a minimum and a maximum.</summary>
        public static double[] AutoExposureSteps(double min, double max)
        {
            var s = ExposureSteps.Where(e => e >= min - 1e-9 && e <= max + 1e-9).ToArray();
            return s.Length > 0 ? s : new[] { Math.Max(0.05, min) };
        }

        /// <summary>The exposure of the next guide frame: the set one, or what auto exposure has chosen.</summary>
        public double CurrentExposure
        {
            get
            {
                if (!Settings.AutoExposure) return Math.Max(0.001, Settings.ExposureSeconds);
                double e = _autoExposure;
                return double.IsNaN(e) ? AutoExposureSteps(Settings.AutoExposureMin, Settings.AutoExposureMax).Last() : e;
            }
        }

        /// <summary>
        /// Auto exposure: the maximum until there is a star; then one step shorter after three frames over 1.5 × the
        /// target SNR, one step longer after two frames under it.
        /// </summary>
        private void AdjustExposure()
        {
            if (!Settings.AutoExposure) { _autoExposure = double.NaN; _snrHigh = _snrLow = 0; return; }
            var steps = AutoExposureSteps(Settings.AutoExposureMin, Settings.AutoExposureMax);
            int i = Array.IndexOf(steps, _autoExposure);
            var s = Star;
            if (i < 0 || s == null)
            {
                if (i != steps.Length - 1) SetAutoExposure(steps[steps.Length - 1]);
                return;
            }
            if (_lost || _rejected) return;
            double target = Math.Max(1, Settings.TargetSnr);
            if (s.Snr > 1.5 * target) { _snrHigh++; _snrLow = 0; }
            else if (s.Snr < target) { _snrLow++; _snrHigh = 0; }
            else _snrHigh = _snrLow = 0;
            if (_snrHigh >= 3 && i > 0) SetAutoExposure(steps[i - 1]);
            else if (_snrLow >= 2 && i < steps.Length - 1) SetAutoExposure(steps[i + 1]);
        }

        private void SetAutoExposure(double e)
        {
            double old = _autoExposure;
            _autoExposure = e;
            _snrHigh = _snrLow = 0;
            if (!double.IsNaN(old))
            {
                _log.Event("Exposure", e.ToString("0.0#", CultureInfo.InvariantCulture) + " s");
                Log.Info("Guider", "Auto exposure " + e.ToString("0.0#", CultureInfo.InvariantCulture) + " s");
            }
        }

        // ------------------------------------------------------------------ dark frames

        private GuideDarks _darks;

        /// <summary>The guide camera's dark library and bad-pixel map, kept beside the settings.</summary>
        public GuideDarks Darks =>
            _darks ?? (_darks = new GuideDarks(Path.Combine(Path.GetDirectoryName(SettingsPath ?? GuiderSettings.DefaultPath) ?? ".", "Guider darks")));

        private DarkSetup SetupOf(AstroImage frame) =>
            new DarkSetup { Camera = _cameraId, Width = frame.Width, Height = frame.Height, Binning = _bin, Gain = _gain, Offset = _offset };

        /// <summary>The guide camera as the settings would set it up now (null without one), to tell whether darks fit.</summary>
        public DarkSetup SetupNow()
        {
            var cam = _dm.GuideCamera;
            if (cam == null || !cam.Connected) return null;
            try
            {
                int max = Math.Max(1, cam.MaxBin), bin = Math.Max(1, Math.Min(Settings.Binning, max));
                return new DarkSetup
                {
                    Camera = cam.Info.Protocol + " " + cam.Info.Address, Width = cam.SensorWidth / bin, Height = cam.SensorHeight / bin, Binning = Math.Max(1, Settings.Binning),
                    Gain = SafeGain(cam), Offset = SafeOffset(cam)
                };
            }
            catch { return null; }
        }

        private static int SafeGain(ICamera cam) { try { return cam.HasGain ? cam.Gain : -1; } catch { return -1; } }
        private static int SafeOffset(ICamera cam) { try { return cam.HasOffset ? cam.Offset : -1; } catch { return -1; } }

        private void ApplyDarks(AstroImage frame)
        {
            if (Settings.DarkFrames == DarkFrameKind.None) return;
            var setup = SetupOf(frame);
            Darks.Load(setup.Camera);
            double used = Darks.Apply(frame, Settings.DarkFrames, setup, CurrentExposure);
            // a dark of another exposure is said once
            if (!double.IsNaN(used) && Math.Abs(used - CurrentExposure) > 1e-3 && used != _lastDarkUsed)
            {
                _log.Event("Dark", string.Format(CultureInfo.InvariantCulture, "{0:0.0##} s dark for {1:0.0##} s frames", used, CurrentExposure));
                Log.Info("Guider", string.Format(CultureInfo.InvariantCulture, "Using the {0:0.0##} s dark for {1:0.0##} s guide frames", used, CurrentExposure));
            }
            _lastDarkUsed = used;
        }

        // ------------------------------------------------------------------ starting and stopping

        /// <summary>Starts taking guide frames (and following the star once there is one), if not already.</summary>
        public void Loop()
        {
            lock (_lock)
            {
                if (_session != null) return;
                if (_building) throw new InvalidOperationException(Lang.T("Dark frames are being taken with the guide camera."));
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
            LoopJob pending;
            // the star goes with the session: after a stop the mount may move, and the next start chooses again
            lock (_lock)
            {
                s = _session; _session = null; Star = null; _lost = false; LockX = LockY = double.NaN; _keepLock = _userLock = false;
                _stars.Clear();
                pending = _job; _job = null;
            }
            pending?.Done.TrySetCanceled();
            _phase = Phase.Watching;
            if (CometFollowing) { CometFollowing = false; Log.Info("Guider", "Comet tracking off"); }
            CometTraining = false;
            if (s != null)
            {
                s.Cancel();
                if (Settings.StickyLock) SaveSettings();
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

        private void ConfigureStars()
        {
            _stars.SearchRadius = Settings.SearchRadius;
            _stars.MultiStar = Settings.MultiStar;
            _stars.MaxStars = Math.Max(1, Settings.MaxStars);
            _stars.MassCheck = Settings.StarMassCheck;
            _stars.MassTolerance = Math.Max(0.05, Settings.StarMassTolerance / 100);
            _stars.Prefer = CataloguePreference();
        }

        /// <summary>
        /// Chooses the guide star: the one nearest a point (a click on the frame), or the best in the frame. On the next
        /// frame when there is none yet. With multi-star, other stars are chosen around it.
        /// </summary>
        public void SelectStar(double x = double.NaN, double y = double.NaN)
        {
            if (_phase != Phase.Watching) throw new InvalidOperationException(Lang.T("Stop guiding before choosing another star."));
            var f = LastFrame;
            if (f == null) { _selectRequested = true; return; }
            var lum = f.Luminance();
            ConfigureStars();
            var s = GuideStars.Select(lum, f.Width, f.Height, Settings.SearchRadius, x, y, EdgeMargin, CataloguePreference());
            if (s == null) throw new InvalidOperationException(double.IsNaN(x) ? Lang.T("No suitable guide star in the frame.") : Lang.T("No star there to guide on."));
            RememberStarOnSky(s);
            lock (_lock)
            {
                Star = s; _lost = false; LockX = s.X; LockY = s.Y; _keepLock = _userLock = false;
                _stars.SetPrimary(s, lum, f.Width, f.Height, EdgeMargin);
            }
            Log.Info("Guider", "Guide star " + s + (Settings.MultiStar ? string.Format(", {0} more", _stars.Secondaries.Count) : ""));
            UpdateState();
        }

        /// <summary>
        /// What holds guiding back (null when nothing does): a polar alignment that needs the mount only tracking. Until it
        /// is released, starting guiding or calibrating and manual pulses are refused, whoever asks (the Guiding tab, a
        /// sequence, a program through the PHD2-compatible server). Frames can still be taken.
        /// </summary>
        public string GuidingHeldBy => _heldBy;
        private volatile string _heldBy;

        /// <summary>Holds guiding back for something that needs the mount only tracking; refused while guiding or calibrating.</summary>
        public void HoldGuiding(string who)
        {
            if (InGuidingPhase || IsCalibrating) throw new InvalidOperationException(Lang.T("Stop guiding first: the mount only tracks for this method."));
            _heldBy = who;
        }

        /// <summary>Lets guiding start again, when it was held for this.</summary>
        public void ReleaseGuiding(string who)
        {
            if (_heldBy == who) _heldBy = null;
        }

        /// <summary>Throws when guiding is held back.</summary>
        internal void CheckNotHeld()
        {
            var h = _heldBy;
            if (h != null) throw new InvalidOperationException(Lang.T("{0}. Stop it first.", Lang.T("{0} is running and needs the mount only tracking", h)));
        }

        /// <summary>
        /// The next start of guiding chooses the star nearest a place (within 40 pixels) and holds it there: after
        /// Sidralune's own centring brought the mount back to the field it guided on, so frames line up with those before.
        /// </summary>
        public void ResumeAt(double x, double y)
        {
            lock (_lock) { _resumeX = x; _resumeY = y; }
        }

        public async Task StartGuiding(bool recalibrate, CancellationToken ct)
        {
            CheckNotHeld();
            var problem = new GuidePulser(_dm, Settings.Output).Problem();
            if (problem != null) throw new InvalidOperationException(problem);
            Loop();
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, SessionToken))
            {
                var lt = linked.Token;
                try
                {
                    // the frame solved first: the camera's scale and angle, and the catalogue's stars to choose from
                    await SolveBeforeGuiding(lt);
                    await EnsureStar(lt);
                    if (recalibrate || !CalibrationFits())
                    {
                        if (_phase == Phase.Guiding) { _phase = Phase.Watching; UpdateState(); }
                        await Calibrate(lt);
                    }
                    else if (UsingAo && !AoCalibrationFits())
                    {
                        // the mount's calibration still fits: only the AO is calibrated
                        if (_phase == Phase.Guiding) { _phase = Phase.Watching; UpdateState(); }
                        _aoOnlyCalibration = true;
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
            SyncRotation();
            double ox, oy;
            var f = LastFrame;
            int margin = Settings.SearchRadius + 8;
            Func<double, double, bool> inside = (x, y) => f == null || (x >= margin && x <= f.Width - margin && y >= margin && y <= f.Height - margin);
            if (Settings.DitherMode == DitherPattern.Spiral)
            {
                lock (_lock)
                {
                    // the spiral turns round where the lock was when it began; a lock moved since (by hand, another star)
                    // begins it again there
                    if (double.IsNaN(_spiralX) || Math.Abs(LockX - _spiralLastX) > 0.5 || Math.Abs(LockY - _spiralLastY) > 0.5) { _spiralX = LockX; _spiralY = LockY; _spiralStep = 0; }
                    _spiralStep = _spiralStep % (raOnly ? 8 : 24) + 1;
                    int i, j;
                    SpiralPoint(_spiralStep, raOnly, out i, out j);
                    double tx, ty;
                    cal.FromAxes(i * pixels, j * pixels, MountPierSide(), Settings.MountReversesDecAfterFlip, out tx, out ty);
                    ox = _spiralX + tx - LockX; oy = _spiralY + ty - LockY;
                    // a place off the frame: back to the spiral's centre, to begin again
                    if (!inside(LockX + ox, LockY + oy)) { ox = _spiralX - LockX; oy = _spiralY - LockY; _spiralStep = 0; }
                    LockX += ox; LockY += oy;
                    _spiralLastX = LockX; _spiralLastY = LockY;
                }
            }
            else
            {
                double a = (_rng.NextDouble() * 2 - 1) * pixels, b = raOnly ? 0 : (_rng.NextDouble() * 2 - 1) * pixels;
                cal.FromAxes(a, b, MountPierSide(), Settings.MountReversesDecAfterFlip, out ox, out oy);
                lock (_lock)
                {
                    // keep the lock position well inside the frame
                    if (f != null && (LockX + ox < margin || LockX + ox > f.Width - margin)) ox = -ox;
                    if (f != null && (LockY + oy < margin || LockY + oy > f.Height - margin)) oy = -oy;
                    LockX += ox; LockY += oy;
                }
            }
            RememberLock();
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
                LastDitherSettled = ok;
                // with an AO: the mount takes over the AO's offset, and the stars settle again
                var ao = UsingAo && AoCalibrationFits() && Settings.AoBumpOnDither ? PrepareAo() : null;
                if (ok && ao != null && (ao.PositionX != 0 || ao.PositionY != 0))
                {
                    Bump(ao, cal, 1, true);
                    _log.Event("Bump", "after dither");
                    try { ok = await WaitSettled(settlePixels, settleTimeSec, timeoutSec, linked.Token); }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new InvalidOperationException(LastError ?? _stopReason ?? Lang.T("Guiding was stopped.")); }
                }
            }
        }

        /// <summary>
        /// The k-th place (from 1) of the dither spiral, in steps along RA and Dec: a square spiral out from the centre, or
        /// along RA only, alternating sides (1, −1, 2, −2…).
        /// </summary>
        public static void SpiralPoint(int k, bool raOnly, out int i, out int j)
        {
            i = 0; j = 0;
            if (raOnly) { i = k % 2 == 1 ? (k + 1) / 2 : -(k / 2); return; }
            int di = 1, dj = 0, run = 1, done = 0, turns = 0;
            for (int s = 0; s < k; s++)
            {
                i += di; j += dj;
                if (++done == run)
                {
                    done = 0;
                    int t = di; di = -dj; dj = t;
                    if (++turns % 2 == 0) run++;
                }
            }
        }

        /// <summary>Whether the last dither settled within its time.</summary>
        public bool LastDitherSettled { get; private set; } = true;
        /// <summary>While settling: the stars' distance from the lock position (pixels), and how long they have stayed within it (s).</summary>
        public double SettlingDistance { get; private set; } = double.NaN;
        public double SettlingSeconds { get; private set; }
        /// <summary>Dec corrections are measured but not sent, whatever the Dec guide mode (drift alignment measures the drift).</summary>
        public bool DecOutputOff { get; set; }
        /// <summary>Guide corrections are sent (a server client can turn them off; frames and steps go on).</summary>
        public bool GuideOutputEnabled { get; set; } = true;

        /// <summary>While guiding: waits until the stars have settled as asked (false after the timeout).</summary>
        public async Task<bool> Settle(double px, double sec, double timeout, CancellationToken ct)
        {
            if (_phase != Phase.Guiding) throw new InvalidOperationException(Lang.T("Not guiding."));
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, SessionToken))
            {
                try { return await WaitSettled(px, sec, timeout, linked.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new InvalidOperationException(LastError ?? _stopReason ?? Lang.T("Guiding was stopped.")); }
            }
        }

        /// <summary>One guide frame while the guider is stopped (it becomes the frame shown).</summary>
        public async Task<AstroImage> CaptureSingleFrame(double seconds, CancellationToken ct)
        {
            var cam = BeginBuilding();
            try
            {
                var f = await CaptureService.GrabFrame(cam, Math.Max(0.001, seconds), ct);
                ApplyDarks(f);
                LastFrame = f;
                FrameReady?.Invoke(f);
                return f;
            }
            finally { _building = false; }
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

        /// <summary>A sticky lock position kept for this camera, if there is one to use.</summary>
        private bool StickyLockApplies =>
            Settings.StickyLock && !double.IsNaN(Settings.StickyLockX) && !double.IsNaN(Settings.StickyLockY) && Settings.StickyLockCamera == _cameraId;

        private void BeginGuiding()
        {
            bool sticky = false, tooFar = false;
            lock (_lock)
            {
                // a sticky lock position is for the star guided there: one far from it would drag the mount across the sky
                bool near = Star != null && Math.Abs(Star.X - Settings.StickyLockX) <= 40 && Math.Abs(Star.Y - Settings.StickyLockY) <= 40;
                tooFar = StickyLockApplies && !near && !_userLock;
                // a lock position the user set wins, then a sticky one, then where calibration left it, then the star
                if (_userLock) { }
                else if (StickyLockApplies && near) { LockX = Settings.StickyLockX; LockY = Settings.StickyLockY; sticky = true; }
                else if (!_keepLock && Star != null) { LockX = Star.X; LockY = Star.Y; }
                _keepLock = _userLock = false;
            }
            if (tooFar) Log.Info("Guider", "The sticky lock position was not used: the guide star is far from it");
            _resetAlgorithms = true;
            _paused = false;
            _aoX.Reset(); _aoY.Reset(); _bumping = false;
            _backlashFrom = double.NaN; // the compensation starts again from the settings
            _pecLockX = _pecLockY = double.NaN; // a new start: Predictive PEC continues from its model
            Interlocked.Increment(ref _guideRun);
            _log.Open(LogFolder);
            // what a guide log viewer needs to show arcseconds and look for the worm's period
            var calNow = Settings.Calibration;
            double raRateNow = calNow != null ? calNow.RaRateAt(MountDeclination()) : double.NaN;
            _log.Event("Settings", string.Format(CultureInfo.InvariantCulture, "scale {0:0.000}\"/px, exposure {1:0.###} s, worm period {2:0} s{3}", PixelScale, CurrentExposure, Settings.PecPeriodSec,
                double.IsNaN(raRateNow) ? "" : string.Format(CultureInfo.InvariantCulture, ", RA rate {0:0.000} px/s", raRateNow)));
            _log.Event("Guiding", string.Format(CultureInfo.InvariantCulture, "lock {0:0.00} {1:0.00}{2}", LockX, LockY, sticky ? " (sticky)" : ""));
            _phase = Phase.Guiding;
            RememberLock();
            Log.Info("Guider", "Guiding");
            UpdateState();
        }

        /// <summary>
        /// Waits until the stars have stayed within px of the lock position for sec seconds; false after timeout seconds.
        /// Throws when guiding stops meanwhile (a slew, for instance).
        /// </summary>
        private async Task<bool> WaitSettled(double px, double sec, double timeout, CancellationToken ct)
        {
            var start = DateTime.UtcNow;
            var within = DateTime.MinValue;
            int run = _guideRun;
            SettlingSeconds = 0; SettlingDistance = double.NaN;
            long seen = Interlocked.Read(ref _frames);
            while (true)
            {
                if ((DateTime.UtcNow - start).TotalSeconds > timeout) return false;
                await Task.Delay(50, ct);
                if (_phase != Phase.Guiding || _guideRun != run) throw new InvalidOperationException(_stopReason ?? Lang.T("Guiding was stopped."));
                long f = Interlocked.Read(ref _frames);
                if (f == seen) continue;
                seen = f;
                if (_lost || !_usable || Star == null) { within = DateTime.MinValue; continue; }
                double dx, dy;
                lock (_lock) { dx = _errX; dy = _errY; }
                SettlingDistance = Math.Sqrt(dx * dx + dy * dy);
                if (SettlingDistance <= px)
                {
                    if (within == DateTime.MinValue) within = DateTime.UtcNow;
                    SettlingSeconds = (DateTime.UtcNow - within).TotalSeconds;
                    if (SettlingSeconds >= sec) return true;
                }
                else { within = DateTime.MinValue; SettlingSeconds = 0; }
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
                    LoopJob job;
                    lock (_lock) { job = _job; _job = null; }
                    if (job != null)
                    {
                        try { await job.Work(job.Cts.Token); job.Done.TrySetResult(true); }
                        catch (OperationCanceledException) { job.Done.TrySetCanceled(); if (ct.IsCancellationRequested) throw; }
                        catch (Exception ex) { job.Done.TrySetException(ex); }
                        finally { job.Cts.Dispose(); }
                        continue;
                    }
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
                        lock (_lock) { Star = null; _lost = false; LockX = LockY = double.NaN; _keepLock = _userLock = false; _stars.Clear(); }
                        ForgetSky();
                        // the sky has moved: a sticky lock position and comet tracking mean nothing any more
                        if (!double.IsNaN(Settings.StickyLockX)) { Settings.StickyLockX = Settings.StickyLockY = double.NaN; SaveSettings(); }
                        if (CometFollowing) { CometFollowing = false; Log.Info("Guider", "Comet tracking off: the mount slewed"); }
                        CometTraining = false;
                        // the sky and the gears' place in their cycle are new
                        (_raAlgo as PredictivePecAlgorithm)?.Forget();
                        UpdateState();
                        while (MountSlewing()) await Task.Delay(200, ct);
                        continue;
                    }
                    if (_bin != Math.Max(1, Settings.Binning) || _focal != Settings.FocalLengthMm) PrepareCamera();
                    Track(await Frame(ct, _phase == Phase.Guiding));
                    if (FindLostStarDue()) await FindLostStar(ct);
                    if (_phase == Phase.Guiding)
                    {
                        Report();
                        if (CometFollowing) FollowComet();
                        // a rotator turning the guide camera: corrections wait until it has stopped
                        bool turning = RotatorMoving();
                        if (turning && !_rotatorTurned) { _rotatorTurned = true; _log.Event("Rotator", "turning: corrections wait"); }
                        else if (!turning && _rotatorTurned)
                        {
                            // the field has turned with the camera: a guide star is chosen again, on the turned calibration
                            _rotatorTurned = false;
                            _relock = true; _selectRequested = true;
                            ForgetSky();
                            _pecLockX = _pecLockY = double.NaN;
                            _log.Event("Rotator", "turned: choosing the guide star again");
                            Log.Info("Guider", "The rotator turned: guiding goes on with a guide star chosen again");
                        }
                        if (!_paused && !_lost && _usable && Star != null && !turning && !_selectRequested) await Correct(ct);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Fail(ex.Message, cts); }
            finally
            {
                bool current;
                LoopJob pending = null;
                lock (_lock)
                {
                    current = _session == cts || _session == null;
                    if (_session == cts) _session = null;
                    if (current) { pending = _job; _job = null; }
                }
                pending?.Done.TrySetCanceled();
                if (current) { _phase = Phase.Watching; SetState(Stopped); }
            }
        }

        /// <summary>Tells listeners (the Guiding Assistant) where the stars were in this frame, along RA and Dec.</summary>
        private void Report()
        {
            var cal = Settings.Calibration;
            var h = Measured;
            if (h == null || cal == null || _lost || !_usable) return;
            double ex, ey, ra, dec;
            lock (_lock) { ex = _errX; ey = _errY; }
            cal.ToAxes(ex, ey, MountPierSide(), Settings.MountReversesDecAfterFlip, out ra, out dec);
            h(new GuideFrame { Time = DateTime.UtcNow, RaPx = ra, DecPx = dec, Paused = _paused });
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
            _readout = new[] { 0, 0, cam.SensorWidth / bin, cam.SensorHeight / bin };
            _rawFull = null;
            _focal = Settings.FocalLengthMm;
            try { _pixelUm = cam.PixelSizeX; } catch { _pixelUm = 0; }
            _gain = SafeGain(cam);
            _offset = SafeOffset(cam);
            _cameraId = cam.Info.Protocol + " " + cam.Info.Address;
            // the simulated guide camera sits on the guide scope the settings describe
            var sim = cam as CameraSimulator;
            if (sim != null) sim.FocalLengthMm = Settings.FocalLengthMm;
        }

        /// <summary>
        /// The next guide frame. While guiding with subframes on, only a box around the stars is read out and pasted into
        /// a copy of the last full frame, so positions stay in full-frame pixels and the rest of the frame keeps its last
        /// look; full frames otherwise.
        /// </summary>
        private async Task<AstroImage> Frame(CancellationToken ct, bool mayUseSubframe = false)
        {
            int reconnects = 0;
            for (int attempt = 1; ; attempt++)
            {
                var cam = _dm.GuideCamera;
                if (cam == null || !cam.Connected) throw new InvalidOperationException(Lang.T("The guide camera was disconnected."));
                try
                {
                    SetReadout(cam, mayUseSubframe ? SubframeBox() : null);
                    var img = await CaptureService.GrabFrame(cam, CurrentExposure, ct);
                    return Compose(img);
                }
                catch (Exception ex) when (!(ex is OperationCanceledException))
                {
                    Log.Warn("Guider", "Guide frame failed: " + ex.Message);
                    if (attempt >= 3)
                    {
                        // the driver may have dropped the camera: connected again, guiding goes on where it was
                        if (reconnects < 2 && await ReconnectCamera(ct)) { reconnects++; attempt = 0; continue; }
                        throw new InvalidOperationException(Lang.T("The guide camera failed three times in a row: {0}", ex.Message));
                    }
                    await Task.Delay(500, ct);
                }
            }
        }

        /// <summary>Connects the guide camera again with the same driver, a few times with pauses between; true when it answers again.</summary>
        private async Task<bool> ReconnectCamera(CancellationToken ct)
        {
            foreach (var seconds in new[] { 2, 5, 10 })
            {
                await Task.Delay(TimeSpan.FromSeconds(seconds), ct);
                try
                {
                    Log.Warn("Guider", "The guide camera stopped answering: connecting it again");
                    _log.Event("Guide camera", "connecting again");
                    _dm.Reconnect(DeviceKind.GuideCamera);
                    PrepareCamera();
                    _log.Event("Guide camera", "connected again");
                    Log.Info("Guider", "The guide camera is connected again");
                    return true;
                }
                catch (Exception ex) when (!(ex is OperationCanceledException)) { Log.Warn("Guider", "Connecting the guide camera again failed: " + ex.Message); }
            }
            return false;
        }

        // ------------------------------------------------------------------ subframes

        private AstroImage _rawFull;              // the last full frame as read, before darks
        private int[] _readout;                   // the part of the sensor read now: x, y, width, height (binned pixels)

        /// <summary>The box to read while guiding: all the stars guided on and their search areas, or null for the full frame.</summary>
        private int[] SubframeBox()
        {
            if (!Settings.UseSubframes || _rawFull == null || Star == null || _lost) return null;
            int w = _rawFull.Width, h = _rawFull.Height, pad = Settings.SearchRadius * 2 + 4;
            double x0, y0, x1, y1;
            lock (_lock)
            {
                x0 = x1 = Star.X; y0 = y1 = Star.Y;
                if (!double.IsNaN(LockX)) { x0 = Math.Min(x0, LockX); x1 = Math.Max(x1, LockX); y0 = Math.Min(y0, LockY); y1 = Math.Max(y1, LockY); }
                foreach (var s in _stars.Secondaries) { x0 = Math.Min(x0, s.Info.X); x1 = Math.Max(x1, s.Info.X); y0 = Math.Min(y0, s.Info.Y); y1 = Math.Max(y1, s.Info.Y); }
            }
            int bx = Math.Max(0, (int)Math.Floor(x0) - pad), by = Math.Max(0, (int)Math.Floor(y0) - pad);
            int bw = Math.Min(w, (int)Math.Ceiling(x1) + pad + 1) - bx, bh = Math.Min(h, (int)Math.Ceiling(y1) + pad + 1) - by;
            // with stars spread over the frame a box gains little
            if (bw <= 0 || bh <= 0 || (double)bw * bh > 0.5 * w * h) return null;
            return new[] { bx, by, bw, bh };
        }

        private void SetReadout(ICamera cam, int[] box)
        {
            int bin = Math.Max(1, Math.Min(Settings.Binning, SafeMaxBin(cam)));
            var want = box ?? new[] { 0, 0, cam.SensorWidth / bin, cam.SensorHeight / bin };
            if (_readout != null && _readout.SequenceEqual(want)) return;
            try { cam.StartX = want[0]; cam.StartY = want[1]; cam.NumX = want[2]; cam.NumY = want[3]; _readout = want; }
            catch (Exception ex) { Log.Warn("Guider", "Subframe: " + ex.Message); _readout = null; }
        }

        private static int SafeMaxBin(ICamera cam) { try { return Math.Max(1, cam.MaxBin); } catch { return 1; } }

        /// <summary>A full frame is kept as read; a subframe is pasted into a copy of the last full frame.</summary>
        private AstroImage Compose(AstroImage img)
        {
            var box = _readout;
            bool sub = box != null && _rawFull != null && (box[0] != 0 || box[1] != 0 || box[2] != _rawFull.Width || box[3] != _rawFull.Height);
            if (!sub || img.Width != box[2] || img.Height != box[3])
            {
                _rawFull = img.Clone();
                return img;
            }
            var full = _rawFull.Clone();
            int w = full.Width;
            for (int c = 0; c < Math.Min(full.Channels, img.Channels); c++)
            {
                var dst = full.Data[c]; var src = img.Data[c];
                for (int y = 0; y < img.Height; y++) Array.Copy(src, y * img.Width, dst, (y + box[1]) * w + box[0], img.Width);
            }
            full.ExposureSeconds = img.ExposureSeconds;
            full.DateObs = img.DateObs;
            return full;
        }

        /// <summary>The part of the frame read out last (x, y, width, height), or null for the whole frame.</summary>
        public int[] SubframeRead { get { var b = _readout; var f = _rawFull; return b != null && f != null && (b[2] < f.Width || b[3] < f.Height) ? (int[])b.Clone() : null; } }

        // ------------------------------------------------------------------ noise reduction

        /// <summary>Smooths the frame in place before stars are measured: a 2x2 mean or a 3x3 median.</summary>
        public static void ReduceNoise(AstroImage frame, NoiseReduction kind)
        {
            if (kind == NoiseReduction.None) return;
            int w = frame.Width, h = frame.Height;
            var nine = new float[9];
            foreach (var d in frame.Data)
            {
                var src = (float[])d.Clone();
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        if (kind == NoiseReduction.Mean2x2)
                        {
                            int x1 = Math.Min(w - 1, x + 1), y1 = Math.Min(h - 1, y + 1);
                            d[y * w + x] = 0.25f * (src[y * w + x] + src[y * w + x1] + src[y1 * w + x] + src[y1 * w + x1]);
                        }
                        else
                        {
                            int n = 0;
                            for (int dy = -1; dy <= 1; dy++)
                                for (int dx = -1; dx <= 1; dx++)
                                {
                                    int xx = Math.Max(0, Math.Min(w - 1, x + dx)), yy = Math.Max(0, Math.Min(h - 1, y + dy));
                                    nine[n++] = src[yy * w + xx];
                                }
                            Array.Sort(nine);
                            d[y * w + x] = nine[4];
                        }
                    }
            }
        }

        // ------------------------------------------------------------------ the rotator

        /// <summary>The rotator's angle, when the guide camera turns with it (NaN otherwise).</summary>
        private double RotatorAngle()
        {
            if (!Settings.GuideCameraOnRotator) return double.NaN;
            var r = _dm.Rotator;
            try { return r != null && r.Connected ? r.Position : double.NaN; } catch { return double.NaN; }
        }

        private bool RotatorMoving()
        {
            if (!Settings.GuideCameraOnRotator) return false;
            var r = _dm.Rotator;
            try { return r != null && r.Connected && r.IsMoving; } catch { return false; }
        }

        /// <summary>Turns the calibration by the rotator's move since calibrating.</summary>
        private void SyncRotation()
        {
            var cal = Settings.Calibration;
            if (cal == null) return;
            double now = RotatorAngle();
            cal.SetRotated(double.IsNaN(now) || double.IsNaN(cal.RotatorDeg) ? 0 : AstroMath.NormalizeDeg180(now - cal.RotatorDeg) * (Settings.RotatorReversed ? -1 : 1));
        }

        /// <summary>Measures the stars in a new frame (or chooses the guide star when asked).</summary>
        private void Track(AstroImage frame)
        {
            SyncRotation();
            ApplyDarks(frame);
            ReduceNoise(frame, Settings.NoiseReduction);
            var lum = frame.Luminance();
            int w = frame.Width, h = frame.Height;
            bool guiding = _phase == Phase.Guiding;
            ConfigureStars();
            StarSetFrame r = null;
            GuideStarInfo chosen = null, reacquired = null;
            bool found = false, lostNow = false;
            lock (_lock)
            {
                if (_selectRequested)
                {
                    // after Sidralune's own centring: the star guided before (the one nearest its old lock position), held there
                    bool resume = false;
                    if (!double.IsNaN(_resumeX))
                    {
                        var near = GuideStars.Select(lum, w, h, Settings.SearchRadius, _resumeX, _resumeY, EdgeMargin);
                        if (near != null && Math.Abs(near.X - _resumeX) <= 40 && Math.Abs(near.Y - _resumeY) <= 40) { chosen = near; resume = true; }
                    }
                    // with a sticky lock position, the star that was guided there (the one nearest it)
                    if (chosen == null && StickyLockApplies) chosen = GuideStars.Select(lum, w, h, Settings.SearchRadius, Settings.StickyLockX, Settings.StickyLockY, EdgeMargin);
                    if (chosen == null) chosen = GuideStars.Select(lum, w, h, Settings.SearchRadius, double.NaN, double.NaN, EdgeMargin, _stars.Prefer);
                    if (chosen != null)
                    {
                        Star = chosen; _lost = false; _rejected = false; _usable = !guiding;
                        _stars.SetPrimary(chosen, lum, w, h, EdgeMargin);
                        if ((!guiding && !_keepLock) || _relock) { LockX = chosen.X; LockY = chosen.Y; _relock = false; _resetAlgorithms = true; }
                        if (resume) { LockX = _resumeX; LockY = _resumeY; _keepLock = true; }
                    }
                    _resumeX = _resumeY = double.NaN;
                    _selectRequested = false;
                }
                else if (_reacquire && guiding && (reacquired = Reacquire(lum, w, h)) != null)
                {
                    // brought back after solving: the star at the lock position is the guide star again, with new partners
                    if (_lost) { _lost = false; found = true; }
                    _usable = false; _rejected = false;
                }
                else if (Star != null)
                {
                    r = _stars.Track(lum, w, h, LockX, LockY, guiding, EdgeMargin);
                    if (r.Promoted) { LockX = r.NewLockX; LockY = r.NewLockY; }
                    if (r.PrimaryFound || r.Promoted) Star = _stars.Primary;
                    _usable = r.Usable;
                    _rejected = r.PrimaryRejected && !r.Usable;
                    if (r.Usable && guiding) { _errX = r.Dx; _errY = r.Dy; }
                    // a frame left out for the star's brightness is not a lost star
                    bool missing = !r.Usable && !r.PrimaryRejected;
                    if (!missing && _lost) { _lost = false; found = true; }
                    else if (missing && !_lost) { _lost = true; lostNow = true; }
                    if (!guiding && r.PrimaryFound && !_keepLock) { LockX = Star.X; LockY = Star.Y; }
                }
            }
            if (lostNow) _lostSince = DateTime.UtcNow;
            // another star took over, or the star's brightness jumped: a knock may have put a different star in the box
            if (r != null && guiding)
            {
                if (r.Promoted) ShiftStarOnSky(r.PromotedOffX, r.PromotedOffY);
                if ((r.Promoted || r.PrimaryRejected) && StarOnSkyKnown) _checkStar = true;
            }
            if (found || chosen != null) _lostSince = DateTime.MaxValue;
            if (chosen != null) RememberStarOnSky(chosen);
            if (reacquired != null)
            {
                _lostSince = DateTime.MaxValue;
                _log.Event("Stars", "guide star taken again at the lock position " + reacquired);
                Log.Info("Guider", "Guide star taken again at the lock position " + reacquired);
            }
            if (chosen != null) Log.Info("Guider", "Guide star " + chosen + (Settings.MultiStar ? string.Format(", {0} more", _stars.Secondaries.Count) : ""));
            if (found) { _log.Event("Star found", Star.ToString()); Log.Info("Guider", "Guide star found again"); }
            if (lostNow) { _log.Event("Star lost", ""); Log.Warn("Guider", "Guide star lost"); }
            if (r != null) foreach (var n in r.Notes) { _log.Event("Stars", n); Log.Info("Guider", n); }
            if (r != null && r.Promoted) RememberLock();
            AdjustExposure();
            LastFrame = frame;
            Interlocked.Increment(ref _frames);
            UpdateState();
            FrameReady?.Invoke(frame);
        }

        // ------------------------------------------------------------------ corrections

        private static IAxisAlgorithm Create(GuideAlgorithmKind k)
        {
            switch (k)
            {
                case GuideAlgorithmKind.ResistSwitch: return new ResistSwitchAlgorithm();
                case GuideAlgorithmKind.Lowpass2: return new Lowpass2Algorithm();
                case GuideAlgorithmKind.ZFilter: return new ZFilterAlgorithm();
                case GuideAlgorithmKind.PredictivePec: return new PredictivePecAlgorithm();
                default: return new HysteresisAlgorithm();
            }
        }

        /// <summary>The algorithms and the backlash compensation follow the settings, which may change while guiding.</summary>
        private void ConfigureAlgorithms()
        {
            var st = Settings;
            if (_raAlgo == null || _raKind != st.RaAlgorithm)
            {
                _raAlgo = Create(st.RaAlgorithm); _raKind = st.RaAlgorithm; _pecPeriodFrom = double.NaN;
                var pec = _raAlgo as PredictivePecAlgorithm;
                if (pec != null) pec.Note += n => { _log.Event("Predictive PEC", n); Log.Info("Guider", n); };
            }
            if (_decAlgo == null || _decKind != st.DecAlgorithm) { _decAlgo = Create(st.DecAlgorithm); _decKind = st.DecAlgorithm; }
            Configure(_raAlgo, true);
            Configure(_decAlgo, false);
            _backlash.Enabled = st.BacklashCompensation;
            if (st.BacklashMs != _backlashFrom || st.BacklashMinMs != _backlashMinFrom || st.BacklashMaxMs != _backlashMaxFrom)
            {
                _backlashFrom = st.BacklashMs; _backlashMinFrom = st.BacklashMinMs; _backlashMaxFrom = st.BacklashMaxMs;
                _backlash.AmountMs = Math.Max(0, st.BacklashMs);
                _backlash.MinMs = Math.Max(0, st.BacklashMinMs);
                // no maximum set: up to twice the amount
                _backlash.MaxMs = st.BacklashMaxMs > 0 ? st.BacklashMaxMs : 2 * Math.Max(0, st.BacklashMs);
            }
        }

        private void Configure(IAxisAlgorithm a, bool ra)
        {
            var st = Settings;
            double minMove = ra ? st.RaMinMovePx : st.DecMinMovePx, aggr = (ra ? st.RaAggressiveness : st.DecAggressiveness) / 100;
            var h = a as HysteresisAlgorithm;
            if (h != null) { h.MinMovePx = minMove; h.Hysteresis = st.RaHysteresis / 100; h.Aggressiveness = aggr; }
            var r = a as ResistSwitchAlgorithm;
            if (r != null) { r.MinMovePx = minMove; r.Aggressiveness = aggr; r.FastSwitch = st.DecFastSwitch; }
            var l = a as Lowpass2Algorithm;
            if (l != null) { l.MinMovePx = minMove; l.Aggressiveness = (ra ? st.RaLowpassAggressiveness : st.DecLowpassAggressiveness) / 100; }
            var z = a as ZFilterAlgorithm;
            if (z != null) { z.MinMovePx = minMove; z.ExposureFactor = ra ? st.RaZFactor : st.DecZFactor; }
            var pec = a as PredictivePecAlgorithm;
            if (pec != null)
            {
                pec.MinMovePx = minMove; pec.ReactiveGain = st.PecReactiveGain / 100; pec.PredictiveGain = st.PecPredictiveGain / 100;
                pec.AutoPeriod = st.PecAutoPeriod; pec.RetainFraction = st.PecRetainPercent / 100;
                // the period the model refines is set again only when the setting changes
                if (st.PecPeriodSec != _pecPeriodFrom) { pec.PeriodSec = Math.Max(10, st.PecPeriodSec); _pecPeriodFrom = st.PecPeriodSec; }
            }
        }

        /// <summary>One guide step: the stars' offset from the lock position, split into RA and Dec, corrected by pulses.</summary>
        private async Task Correct(CancellationToken ct)
        {
            var cal = Settings.Calibration;
            if (cal == null) return;
            ConfigureAlgorithms();
            if (_resetAlgorithms) { _raAlgo.Reset(); _decAlgo.Reset(); _backlash.Reset(); _resetAlgorithms = false; }
            var s = Star;
            double ex, ey, raErr, decErr;
            lock (_lock) { ex = _errX; ey = _errY; }
            cal.ToAxes(ex, ey, MountPierSide(), Settings.MountReversesDecAfterFlip, out raErr, out decErr);
            TellPecAboutLock(cal);
            // with a calibrated AO, the AO corrects and the mount only takes over large offsets
            var ao = UsingAo && AoCalibrationFits() ? PrepareAo() : null;
            if (ao != null)
            {
                string aoNote;
                CorrectWithAo(ao, cal, ex, ey, out aoNote);
                double sc = PixelScale;
                var aoStep = new GuideStep { Time = DateTime.Now, RaDistance = raErr, DecDistance = decErr, RaArcsec = raErr * sc, DecArcsec = decErr * sc, Snr = s.Snr, StarMass = s.Flux, Dx = ex, Dy = ey, Hfd = s.Hfd };
                lock (_history) { _history.Add(aoStep); if (_history.Count > 5000) _history.RemoveRange(0, 1000); }
                _log.Row("Guide", s, ex, ey, raErr, decErr, 0, 0, aoNote, _stars.LastUsed);
                Step?.Invoke(aoStep);
                return;
            }
            _backlash.Next(decErr, Settings.DecMinMovePx);
            double raC = _raAlgo.Correction(raErr), decC = _decAlgo.Correction(decErr);
            double raRate = cal.RaRateAt(MountDeclination()), decRate = cal.DecRatePxPerSec;
            int raMs = raRate > 0 ? (int)Math.Round(Math.Min(Settings.MaxRaPulseMs, Math.Abs(raC) / raRate * 1000)) : 0;
            int decMs = decRate > 0 ? (int)Math.Round(Math.Min(Settings.MaxDecPulseMs, Math.Abs(decC) / decRate * 1000)) : 0;
            // the star has moved the way a West pulse moves it: an East pulse brings it back (and South for North)
            var raDir = raC > 0 ? GuideDirection.East : GuideDirection.West;
            var decDir = decC > 0 ? GuideDirection.South : GuideDirection.North;
            string note = "";
            var mode = DecOutputOff ? DecGuideMode.Off : Settings.DecMode;
            if (decMs > 0 && (mode == DecGuideMode.Off || (mode == DecGuideMode.North && decDir == GuideDirection.South) || (mode == DecGuideMode.South && decDir == GuideDirection.North)))
            {
                note = "Dec " + decDir + " not sent (" + mode + " only)";
                decMs = 0;
            }
            // a client of the PHD2-compatible server may turn the guide output off: everything is measured, nothing is sent
            if (!GuideOutputEnabled) { raMs = decMs = 0; note = "output off"; }
            // what actually goes out, for the algorithms that keep account of it
            _raAlgo.Sent(raRate > 0 ? Math.Sign(raC) * raMs / 1000.0 * raRate : 0);
            _decAlgo.Sent(decRate > 0 ? Math.Sign(decC) * decMs / 1000.0 * decRate : 0);
            int extra = decMs > 0 ? _backlash.Extra(decDir == GuideDirection.North ? 1 : -1, decErr) : 0;
            if (extra > 0) { decMs += extra; note = "backlash +" + extra + " ms"; }
            double scale = PixelScale;
            var step = new GuideStep
            {
                Time = DateTime.Now, RaDistance = raErr, DecDistance = decErr, RaArcsec = raErr * scale, DecArcsec = decErr * scale,
                RaPulse = raDir == GuideDirection.East ? raMs : -raMs, DecPulse = decDir == GuideDirection.North ? decMs : -decMs,
                Snr = s.Snr, StarMass = s.Flux, Dx = ex, Dy = ey, Hfd = s.Hfd
            };
            lock (_history) { _history.Add(step); if (_history.Count > 5000) _history.RemoveRange(0, 1000); }
            _log.Row("Guide", s, ex, ey, raErr, decErr, step.RaPulse, step.DecPulse, note, _stars.LastUsed);
            Step?.Invoke(step);
            var pulser = new GuidePulser(_dm, Settings.Output);
            if (raMs > 0) await pulser.Pulse(raDir, raMs, ct);
            if (decMs > 0) await SendPulse(pulser, decDir, decMs, ct);
        }

        private double _pecPeriodFrom = double.NaN, _pecLockX = double.NaN, _pecLockY = double.NaN;

        /// <summary>The Predictive PEC model, when RA uses it.</summary>
        public PredictivePecAlgorithm PredictivePec => _raAlgo as PredictivePecAlgorithm;

        /// <summary>
        /// Predictive PEC follows the gear error, which a lock position move does not change: a known move is taken out of
        /// its track, and a new start (where the old lock position is not known) continues from the model.
        /// </summary>
        private void TellPecAboutLock(GuideCalibration cal)
        {
            var pec = _raAlgo as PredictivePecAlgorithm;
            double lx, ly;
            lock (_lock) { lx = LockX; ly = LockY; }
            if (pec != null)
            {
                if (double.IsNaN(_pecLockX)) pec.Reanchor();
                else if (lx != _pecLockX || ly != _pecLockY)
                {
                    double ra, dec;
                    cal.ToAxes(lx - _pecLockX, ly - _pecLockY, MountPierSide(), Settings.MountReversesDecAfterFlip, out ra, out dec);
                    pec.LockMoved(ra);
                }
            }
            _pecLockX = lx; _pecLockY = ly;
        }

        /// <summary>Sends a pulse, noting the way Dec last turned for the backlash compensation.</summary>
        private async Task SendPulse(GuidePulser pulser, GuideDirection dir, int ms, CancellationToken ct)
        {
            await pulser.Pulse(dir, ms, ct);
            if (dir == GuideDirection.North) _backlash.Pulsed(1);
            else if (dir == GuideDirection.South) _backlash.Pulsed(-1);
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
            if (_aoOnlyCalibration)
            {
                _aoOnlyCalibration = false;
                try { await RunAoCalibration(ct); }
                finally { _phase = Phase.Watching; UpdateState(); }
                return;
            }
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

            // with a solved guide frame here: the prediction, confirmed with a few steps each way, or else the full calibration
            GuideCalibration cal = null;
            PredictionCheck check = null;
            double predWest, predNorth;
            if (Settings.PredictCalibration && PredictDirections(startX, startY, out predWest, out predNorth))
            {
                check = await ConfirmPrediction(predWest, predNorth, predRa, predDec, raKnown, decKnown, gRa, gDec, cosd, dist, pulser, ct);
                cal = check.Calibration;
                if (cal == null)
                {
                    Log.Info("Guider", "The predicted calibration was not used: " + check.Why + "; calibrating in full");
                    _log.Event("Calibration", "prediction not used: " + check.Why);
                }
            }
            if (cal == null)
            {
                double ra0 = MountRa();
                var west = await RunLeg(GuideDirection.West, stepRa, dist, Star.X, Star.Y, pulser, ct);
                int westSign = Sign(-AstroMath.NormalizeDeg180((MountRa() - ra0) * 15) * 3600 * cosd);
                double raAngle, raRate;
                if (!GuideCalibration.FitLeg(west.T, west.X, west.Y, out raAngle, out raRate)) throw new InvalidOperationException(Lang.T("Calibration failed: the star did not move far enough in RA. Check the guide cable or the mount's connection and guide rate."));
                await Return(GuideDirection.East, west.TotalMs, raRate, Settings.MaxRaPulseMs, pulser, ct);

                // what a Dec step should do, from the measured RA rate (the guide rates the mount reports can be wrong)
                double expDec = raRate / cosd * (raKnown && decKnown ? gDec / gRa : 1);
                int stepDec = Settings.CalibrationStepMs > 0 ? Settings.CalibrationStepMs : StepFor(dist, expDec);
                int clearingMs = await ClearBacklash(stepDec, expDec, pulser, ct);
                double dec0 = MountDeclination();
                var north = await RunLeg(GuideDirection.North, stepDec, dist, Star.X, Star.Y, pulser, ct);
                int northSign = Sign((MountDeclination() - dec0) * 3600);
                double decAngle, decRate;
                if (!GuideCalibration.FitLeg(north.T, north.X, north.Y, out decAngle, out decRate)) throw new InvalidOperationException(Lang.T("Calibration failed: the star did not move far enough in Dec. Check the guide cable or the mount's connection and guide rate."));
                // back by the North leg and the steps that took up the backlash, so the star ends near where it started
                await Return(GuideDirection.South, north.TotalMs + clearingMs, decRate, Settings.MaxDecPulseMs, pulser, ct);
                cal = new GuideCalibration
                {
                    RaAngleDeg = raAngle, DecAngleDeg = decAngle, RaRatePxPerSec = raRate, DecRatePxPerSec = decRate,
                    RaSteps = west.Steps, DecSteps = north.Steps, WestSign = westSign, NorthSign = northSign,
                    PredictionMiss = check != null ? check.Miss : null, PredictionMissBy = check != null ? check.MissBy : double.NaN
                };
            }
            cal.ExpectedRaRatePxPerSec = raKnown ? predRa : double.NaN;
            cal.ExpectedDecRatePxPerSec = decKnown ? predDec : double.NaN;
            cal.DecDeg = dec; cal.PierSide = pier; cal.Binning = _bin; cal.PixelSizeUm = _pixelUm; cal.FocalLengthMm = Settings.FocalLengthMm;
            cal.Output = Settings.Output; cal.Camera = _cameraId; cal.When = DateTime.UtcNow; cal.RotatorDeg = RotatorAngle();
            cal.Check();
            Settings.Calibration = cal;
            SaveSettings();
            // with an AO, it is calibrated after the mount
            if (UsingAo) await RunAoCalibration(ct);
            string summary = string.Format(CultureInfo.InvariantCulture, "{8}RA {0:0.0}° {1:0.00} px/s ({2} steps), Dec {3:0.0}° {4:0.00} px/s ({5} steps), {6:0.0}° from square{7}",
                cal.RaAngleDeg, cal.RaRatePxPerSec, cal.RaSteps, cal.DecAngleDeg, cal.DecRatePxPerSec, cal.DecSteps, cal.OrthogonalityErrorDeg,
                cal.Issues.Count > 0 ? "; " + string.Join(", ", cal.Issues) : "", cal.Predicted ? "predicted and confirmed: " : "");
            _log.Event("Calibrated", summary);
            Log.Info("Guider", "Calibrated: " + summary);
            CalibrationChanged?.Invoke();
            // guide back to where the star started
            lock (_lock) { LockX = startX; LockY = startY; }
            _keepLock = true;
            _phase = Phase.Watching;
            UpdateState();
        }

        /// <summary>+1, -1, or 0 for a change under half an arcsecond (the mount did not say).</summary>
        private static int Sign(double arcsec) => double.IsNaN(arcsec) || Math.Abs(arcsec) < 0.5 ? 0 : Math.Sign(arcsec);

        /// <summary>A pulse for about twelve steps over the distance, between 0.1 and 3 s.</summary>
        private static int StepFor(double dist, double ratePxPerSec) =>
            ratePxPerSec > 0 && !double.IsInfinity(ratePxPerSec) ? (int)AstroMath.Clamp(Math.Round(dist / 12 / ratePxPerSec * 1000), 100, 3000) : 750;

        /// <summary>The next frame's star position; throws when the star is lost or the mount slews.</summary>
        private async Task NextPosition(CancellationToken ct, string slewed = null, string lost = null)
        {
            if (MountSlewing()) throw new InvalidOperationException(slewed ?? Lang.T("Calibration stopped because the mount slewed."));
            Track(await Frame(ct));
            if (Star == null || _lost) throw new InvalidOperationException(lost ?? Lang.T("The guide star was lost during calibration."));
        }

        private async Task<Leg> RunLeg(GuideDirection dir, int stepMs, double dist, double x0, double y0, GuidePulser pulser, CancellationToken ct, int maxSteps = 0)
        {
            var leg = new Leg();
            leg.T.Add(0); leg.X.Add(x0); leg.Y.Add(y0);
            bool ra = dir == GuideDirection.West || dir == GuideDirection.East;
            while (true)
            {
                if (leg.Steps >= 40)
                    throw new InvalidOperationException(ra ? Lang.T("Calibration failed: the star did not move far enough in RA. Check the guide cable or the mount's connection and guide rate.")
                                                           : Lang.T("Calibration failed: the star did not move far enough in Dec. Check the guide cable or the mount's connection and guide rate."));
                await SendPulse(pulser, dir, stepMs, ct);
                leg.Steps++;
                leg.TotalMs += stepMs;
                await NextPosition(ct);
                leg.T.Add(leg.TotalMs / 1000.0); leg.X.Add(Star.X); leg.Y.Add(Star.Y);
                double dx = Star.X - x0, dy = Star.Y - y0;
                _log.Row("Calibration " + dir, Star, dx, dy, double.NaN, double.NaN, ra ? stepMs : 0, ra ? 0 : stepMs);
                if (Math.Sqrt(dx * dx + dy * dy) >= dist || (maxSteps > 0 && leg.Steps >= maxSteps)) return leg;
            }
        }

        /// <summary>Brings the star back by the total pulse time of a leg, in pulses short enough to keep it in the search box.</summary>
        private async Task Return(GuideDirection dir, int totalMs, double ratePxPerSec, int maxPulseMs, GuidePulser pulser, CancellationToken ct)
        {
            int chunk = (int)Math.Max(100, Math.Min(Math.Max(100, maxPulseMs), 0.6 * Settings.SearchRadius / Math.Max(1e-3, ratePxPerSec) * 1000));
            for (int left = totalMs; left > 0; left -= chunk)
            {
                int ms = Math.Min(chunk, left);
                await SendPulse(pulser, dir, ms, ct);
                await NextPosition(ct);
                _log.Row("Calibration " + dir, Star, double.NaN, double.NaN, double.NaN, double.NaN, 0, 0);
            }
        }

        /// <summary>
        /// North steps until the star moves steadily: three steps in a row, each at least 60 % of the expected move and in
        /// the same direction. After 20 s of pulses it goes on if the star has moved 3 pixels at all, and gives up if not.
        /// </summary>
        private async Task<int> ClearBacklash(int stepMs, double expectedRate, GuidePulser pulser, CancellationToken ct, string lost = null)
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
                await SendPulse(pulser, GuideDirection.North, stepMs, ct);
                totalMs += stepMs;
                await NextPosition(ct, null, lost);
                double dx = Star.X - lx, dy = Star.Y - ly;
                lx = Star.X; ly = Star.Y;
                bool same = good == 0 || dx * pdx + dy * pdy > 0;
                if (Math.Sqrt(dx * dx + dy * dy) >= 0.6 * expectedMove && same) { good++; pdx = dx; pdy = dy; }
                else { good = 0; pdx = pdy = 0; }
                _log.Row("Backlash", Star, dx, dy, double.NaN, double.NaN, 0, stepMs);
            }
            return totalMs;
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

        private double MountRa()
        {
            var tel = _dm.Telescope;
            try { return tel != null && tel.Connected ? tel.RightAscension : double.NaN; } catch { return double.NaN; }
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
