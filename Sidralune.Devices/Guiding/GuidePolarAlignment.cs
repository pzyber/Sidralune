using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Sidralune.Core;
using Sidralune.Core.Mathematics;
using Sidralune.Core.Time;
using Sidralune.Core.Util;
using Sidralune.Devices.Automation;
using Sidralune.Imaging;
using Sidralune.Science.Astrometry;

namespace Sidralune.Devices.Guiding
{
    /// <summary>One drift alignment measurement: the Dec drift where the mount pointed, and the error it gives.</summary>
    public class DriftMeasurement
    {
        public double Seconds;
        public int Frames;
        public double HourAngleHours = double.NaN, DecDeg = double.NaN;
        /// <summary>The telescope's drift north against the stars (arcsec a minute), and its uncertainty.</summary>
        public double DriftArcsecPerMin = double.NaN, UncertaintyArcsecPerMin = double.NaN;
        /// <summary>Which error this place measures: altitude (low in the east or west) or azimuth (near the meridian).</summary>
        public bool Altitude;
        /// <summary>That error (arcmin, signed as in PolarAlignmentResult) and its uncertainty.</summary>
        public double ErrorArcmin = double.NaN, UncertaintyArcmin = double.NaN;
        /// <summary>Both errors: the one measured here, and the other's last measurement (0 until there is one).</summary>
        public PolarAlignmentResult Result;
        /// <summary>Which way to move the axis for the error measured here.</summary>
        public string Advice;
    }

    /// <summary>
    /// Drift alignment: while guiding with the Dec corrections held back, the telescope drifts in Dec against the stars
    /// at a rate set by the polar axis's error and the hour angle (ω (m sin H + e cos H), with m and e the axis's offsets
    /// from the pole towards the meridian and the east). Near the meridian that is the azimuth error; low in the east or
    /// west, the altitude error. Each measurement takes out the other error's last measurement, so the two converge.
    /// </summary>
    public class DriftAlignment
    {
        private readonly SidraluneGuider _g;
        private readonly DeviceManager _dm;
        private readonly List<double[]> _samples = new List<double[]>(); // seconds, telescope north arcsec
        private DateTime _start;
        private double _ha, _dec;

        /// <summary>The axis's offsets from the pole (radians) towards the meridian and the east, as last measured (NaN until then).</summary>
        public double TowardMeridian = double.NaN, TowardEast = double.NaN;
        public bool Measuring { get; private set; }
        public DriftMeasurement Last { get; private set; }

        public DriftAlignment(SidraluneGuider g, DeviceManager dm) { _g = g; _dm = dm; }

        private ITelescope Mount()
        {
            var tel = _dm.Telescope;
            if (tel == null || !tel.Connected) throw new InvalidOperationException(Lang.T("Drift alignment needs the mount connected, to know where it points."));
            return tel;
        }

        /// <summary>Where the mount points now: hour angle (hours, west positive) and Dec (degrees).</summary>
        public void Pointing(out double haHours, out double decDeg)
        {
            var tel = Mount();
            haHours = AstroMath.NormalizeDeg180((tel.SiderealTime - tel.RightAscension) * 15) / 15;
            decDeg = tel.Declination;
        }

        /// <summary>Whether a place measures the altitude error (else the azimuth error).</summary>
        public static bool MeasuresAltitude(double haHours) => Math.Abs(Math.Sin(haHours * AstroMath.Hour2Rad)) > Math.Abs(Math.Cos(haHours * AstroMath.Hour2Rad));

        /// <summary>What the place the mount points at is good for, or where to point instead.</summary>
        public string PointingAdvice()
        {
            double ha, dec;
            Pointing(out ha, out dec);
            if (Math.Abs(dec) > 30) return Lang.T("Point near the celestial equator (Dec within ±30°): near the meridian for the azimuth error, or 25–35° up in the east or west for the altitude error.");
            if (Math.Abs(ha) <= 1) return Lang.T("Near the meridian: this measures the azimuth error.");
            if (Math.Abs(ha) >= 2 && Math.Abs(ha) <= 6) return ha < 0 ? Lang.T("In the east: this measures the altitude error.") : Lang.T("In the west: this measures the altitude error.");
            return MeasuresAltitude(ha) ? Lang.T("This measures the altitude error, though lower in the east or west would tell it better.")
                                        : Lang.T("This measures the azimuth error, though nearer the meridian would tell it better.");
        }

        /// <summary>Starts recording the Dec drift. Guiding must be running; Dec corrections stop until Stop.</summary>
        public void Start()
        {
            if (Measuring) return;
            if (!_g.InGuidingPhase || _g.Calibration == null) throw new InvalidOperationException(Lang.T("Start guiding first: drift alignment measures while guiding with the Dec corrections held back."));
            Pointing(out _ha, out _dec);
            lock (_samples) _samples.Clear();
            Last = null;
            _start = DateTime.Now;
            _g.Step += OnStep;
            _g.DecOutputOff = true;
            Measuring = true;
            Log.Info("Guider", string.Format("Drift alignment at hour angle {0:0.0} h, Dec {1:0.0}°", _ha, _dec));
        }

        private void OnStep(GuideStep s)
        {
            var cal = _g.Calibration;
            var tel = _dm.Telescope;
            if (cal == null || double.IsNaN(s.Dx) || double.IsNaN(s.Dy)) return;
            double east, north;
            // the star moved (Dx, Dy) from the lock: the telescope moved the way a lock move of that much asks for
            cal.ImageToSky(s.Dx, s.Dy, _g.PixelScale, tel != null ? tel.SideOfPier : PierSide.Unknown, _g.Settings.MountReversesDecAfterFlip, out east, out north);
            lock (_samples) _samples.Add(new[] { (s.Time - _start).TotalSeconds, north });
        }

        /// <summary>The measurement so far.</summary>
        public DriftMeasurement Current
        {
            get
            {
                List<double[]> copy;
                lock (_samples) copy = new List<double[]>(_samples);
                return Analyse(copy, _ha, _dec, TowardMeridian, TowardEast, _dm.Site);
            }
        }

        /// <summary>Stops recording; the measured error is kept for the next measurement. Dec corrections start again.</summary>
        public DriftMeasurement Stop()
        {
            if (!Measuring) return Last;
            _g.Step -= OnStep;
            _g.DecOutputOff = false;
            Measuring = false;
            var m = Current;
            if (!double.IsNaN(m.ErrorArcmin))
            {
                var tm = TowardMeridian; var te = TowardEast;
                Solve(m, ref tm, ref te);
                TowardMeridian = tm; TowardEast = te;
                Log.Info("Guider", string.Format("Drift alignment: Dec drift {0:0.00}\"/min, {1} error {2:0.0}'", m.DriftArcsecPerMin, m.Altitude ? "altitude" : "azimuth", m.ErrorArcmin));
            }
            Last = m;
            return m;
        }

        /// <summary>Stops without keeping a result; Dec corrections start again.</summary>
        public void Cancel()
        {
            if (!Measuring) return;
            _g.Step -= OnStep;
            _g.DecOutputOff = false;
            Measuring = false;
        }

        /// <summary>Forgets both errors (after a large change, for instance).</summary>
        public void Forget() { TowardMeridian = TowardEast = double.NaN; Last = null; }

        private static double DriftOf(DriftMeasurement m) => m.DriftArcsecPerMin / 60 * AstroMath.ArcSec2Rad / PolarAxis.SiderealRadPerSec;

        /// <summary>The axis offset the measurement gives, with the other offset as known before.</summary>
        private static void Solve(DriftMeasurement m, ref double towardMeridian, ref double towardEast)
        {
            double h = m.HourAngleHours * AstroMath.Hour2Rad, c = DriftOf(m);
            if (m.Altitude) towardMeridian = (c - (double.IsNaN(towardEast) ? 0 : towardEast) * Math.Cos(h)) / Math.Sin(h);
            else towardEast = (c - (double.IsNaN(towardMeridian) ? 0 : towardMeridian) * Math.Sin(h)) / Math.Cos(h);
        }

        /// <summary>
        /// A straight line through (t, y): its slope and the slope's standard error. Points more than three times the
        /// scatter off the line (a star mismeasured, a gust) are left out and the line fitted again.
        /// </summary>
        internal static bool Line(IList<double> t, IList<double> y, out double slope, out double se)
        {
            slope = se = double.NaN;
            var tt = new List<double>(t);
            var yy = new List<double>(y);
            for (int pass = 0; pass < 3; pass++)
            {
                var f = LinAlg.PolyFit(tt, yy, 1);
                if (f == null) return false;
                double tm = tt.Average(), stt = tt.Sum(x => (x - tm) * (x - tm)), rss = 0;
                var res = new double[tt.Count];
                for (int i = 0; i < tt.Count; i++) { res[i] = yy[i] - (f[0] + f[1] * tt[i]); rss += res[i] * res[i]; }
                double sigma = Math.Sqrt(rss / Math.Max(1, tt.Count - 2));
                slope = f[1];
                se = stt > 0 ? sigma / Math.Sqrt(stt) : double.NaN;
                var keep = Enumerable.Range(0, tt.Count).Where(i => Math.Abs(res[i]) <= 3 * sigma).ToList();
                if (keep.Count == tt.Count || keep.Count < 5) break;
                tt = keep.Select(i => tt[i]).ToList();
                yy = keep.Select(i => yy[i]).ToList();
            }
            return !double.IsNaN(slope);
        }

        /// <summary>The Dec drift of samples (seconds, telescope north arcsec) at a place, and the error it gives.</summary>
        public static DriftMeasurement Analyse(IList<double[]> s, double haHours, double decDeg, double towardMeridian, double towardEast, Observer site)
        {
            var m = new DriftMeasurement { HourAngleHours = haHours, DecDeg = decDeg, Frames = s.Count, Altitude = MeasuresAltitude(haHours) };
            if (s.Count < 5) return m;
            m.Seconds = s[s.Count - 1][0] - s[0][0];
            double slope, se;
            if (m.Seconds <= 0 || !Line(s.Select(v => v[0]).ToList(), s.Select(v => v[1]).ToList(), out slope, out se)) return m;
            m.DriftArcsecPerMin = slope * 60;
            m.UncertaintyArcsecPerMin = se * 60;
            double mer = towardMeridian, east = towardEast;
            Solve(m, ref mer, ref east);
            m.Result = PolarAxis.Error(PolarAxis.FromOffsets(double.IsNaN(mer) ? 0 : mer, double.IsNaN(east) ? 0 : east), site, false);
            m.ErrorArcmin = m.Altitude ? m.Result.AltErrorArcmin : m.Result.AzErrorArcmin;
            // the drift's uncertainty through the same factor
            double h = haHours * AstroMath.Hour2Rad, k = Math.Abs(m.Altitude ? Math.Sin(h) : Math.Cos(h));
            m.UncertaintyArcmin = k > 0 ? m.UncertaintyArcsecPerMin / 60 * AstroMath.ArcSec2Rad / PolarAxis.SiderealRadPerSec / k * AstroMath.Rad2Deg * 60 : double.NaN;
            m.Advice = m.Altitude ? Lang.T("Move the polar axis {0} by {1:0.0}′", PolarAxis.AltWord(m.ErrorArcmin), Math.Abs(m.ErrorArcmin))
                                  : Lang.T("Move the polar axis {0} by {1:0.0}′", PolarAxis.AzWord(m.ErrorArcmin, site), Math.Abs(m.ErrorArcmin));
            return m;
        }
    }

    /// <summary>
    /// Where polar alignment gets its frames: the Sidralune guider's loop (the guide camera) or the imaging camera. Frames
    /// arrive through FrameReady, and Next waits for the first begun after a moment.
    /// </summary>
    public abstract class AlignmentFrames
    {
        public abstract event Action<AstroImage> FrameReady;
        public abstract ICamera Camera { get; }
        /// <summary>Arcseconds per pixel of the frames.</summary>
        public abstract double PixelScale { get; }
        /// <summary>How far a star is looked for round where it was (pixels).</summary>
        public abstract int SearchRadius { get; }
        /// <summary>Makes sure frames are coming, or says why they cannot.</summary>
        public abstract void Ensure();
        protected abstract double FrameSeconds { get; }
        protected abstract string NoFrameReason { get; }

        /// <summary>The next frame begun after a moment.</summary>
        public async Task<AstroImage> Next(DateTime afterUtc, CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<AstroImage>();
            Action<AstroImage> h = f => { if ((f.DateObs ?? DateTime.UtcNow) >= afterUtc) tcs.TrySetResult(f); };
            FrameReady += h;
            try
            {
                using (ct.Register(() => tcs.TrySetCanceled()))
                {
                    var done = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(60 + 2 * FrameSeconds), ct));
                    if (done != tcs.Task)
                    {
                        ct.ThrowIfCancellationRequested();
                        throw new InvalidOperationException(NoFrameReason);
                    }
                    return await tcs.Task;
                }
            }
            finally { FrameReady -= h; }
        }
    }

    /// <summary>The guide camera's frames, from the Sidralune guider's loop (started when it is not running).</summary>
    public class GuiderFrames : AlignmentFrames
    {
        public readonly SidraluneGuider Guider;
        /// <summary>The loop was started here (so stopping can stop it again).</summary>
        public bool StartedLoop { get; private set; }

        public GuiderFrames(SidraluneGuider g) { Guider = g; }

        public override event Action<AstroImage> FrameReady { add { Guider.FrameReady += value; } remove { Guider.FrameReady -= value; } }
        public override ICamera Camera => Guider.Devices.GuideCamera;
        public override double PixelScale => Guider.PixelScale;
        public override int SearchRadius => Math.Max(10, Guider.Settings.SearchRadius);
        protected override double FrameSeconds => Guider.CurrentExposure;
        protected override string NoFrameReason => Guider.LastError ?? Lang.T("No guide frame came.");

        public override void Ensure()
        {
            if (Guider.InGuidingPhase || Guider.IsCalibrating) throw new InvalidOperationException(Lang.T("Stop guiding first: the mount only tracks for this method."));
            if (!Guider.IsRunning) { Guider.Loop(); StartedLoop = true; }
        }

        /// <summary>Stops the loop when it was started here.</summary>
        public void StopIfStarted()
        {
            if (StartedLoop && !Guider.InGuidingPhase && !Guider.IsCalibrating) Guider.StopGuiding();
            StartedLoop = false;
        }
    }

    /// <summary>
    /// The imaging camera's frames: short exposures, binned, not saved, taken one after another by Run until it is
    /// cancelled.
    /// </summary>
    public class CameraFrames : AlignmentFrames
    {
        private readonly CaptureService _capture;
        private volatile bool _running;
        public double Exposure = 2;
        public int Bin = 2;

        public CameraFrames(CaptureService capture) { _capture = capture; }

        public override event Action<AstroImage> FrameReady;
        public override ICamera Camera => _capture.Devices.Camera;
        public override double PixelScale
        {
            get
            {
                var cam = Camera;
                double px = 0;
                try { if (cam != null) px = cam.PixelSizeX; } catch { }
                double f = _capture.Devices.FocalLengthMm;
                return px > 0 && f > 0 ? 206.265 * px * Math.Max(1, Bin) / f : 1;
            }
        }
        public override int SearchRadius => 20;
        protected override double FrameSeconds => Exposure;
        protected override string NoFrameReason => _running ? Lang.T("No frame came from the camera.") : Lang.T("The camera is not taking frames.");
        public bool Running => _running;

        /// <summary>Frames come while Run runs, which the caller starts (as a camera job); here only the camera is checked.</summary>
        public override void Ensure()
        {
            if (Camera == null) throw new InvalidOperationException(Lang.T("No camera connected."));
        }

        /// <summary>Takes frames one after another until cancelled.</summary>
        public async Task Run(CancellationToken ct)
        {
            if (Camera == null) throw new InvalidOperationException(Lang.T("No camera connected."));
            _running = true;
            try
            {
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    var r = await _capture.Expose(new ExposureRequest { Seconds = Exposure, Bin = Bin, Save = false, FrameType = "LIGHT" }, ct);
                    if (r.Image.DateObs == null) r.Image.DateObs = DateTime.UtcNow.AddSeconds(-Exposure);
                    FrameReady?.Invoke(r.Image);
                }
            }
            finally { _running = false; }
        }
    }

    /// <summary>A guide frame plate-solved, with when it was taken.</summary>
    internal class SolvedGuideFrame
    {
        public DateTime Time;
        public Wcs Wcs;
        public AstroImage Image;
    }

    /// <summary>The static polar alignment result: the errors, and where the axis, the pole and a star to move are in the last frame.</summary>
    public class StaticAlignmentResult : PolarAlignmentResult
    {
        public double AxisX = double.NaN, AxisY = double.NaN, PoleX = double.NaN, PoleY = double.NaN;
        /// <summary>A star to move with the alignment knobs, and where the correction takes it (pixels).</summary>
        public double StarX = double.NaN, StarY = double.NaN, TargetX = double.NaN, TargetY = double.NaN;
        public double PixelScale = double.NaN;
        /// <summary>The last frame and its plate solution.</summary>
        public AstroImage Frame;
        public Wcs Wcs;
    }

    /// <summary>
    /// Static polar alignment near the pole: frames at three RA positions (the mount turning about 15° west each time, or
    /// turned by hand), from the imaging camera or the guide camera, are plate-solved. A point fixed to the camera then traces a circle about the mount's axis,
    /// which gives the axis; the plate solution places the refracted pole in the frame. Moving the pole's image onto the
    /// axis's corrects the alignment, so a star is given a target: the same move away.
    /// </summary>
    public class StaticPolarAlignment
    {
        private readonly AlignmentFrames _source;
        private readonly DeviceManager _dm;
        private readonly List<SolvedGuideFrame> _frames = new List<SolvedGuideFrame>();
        private double _trackX = double.NaN, _trackY = double.NaN;

        public double StepDeg = 15;
        public event Action<string> Status;
        /// <summary>While following the reference star: its position in each frame (NaN when not found).</summary>
        public event Action<double, double> StarMoved;
        public StaticAlignmentResult Result { get; private set; }
        public int Frames { get { lock (_frames) return _frames.Count; } }
        public bool Tracking { get; private set; }

        public StaticPolarAlignment(AlignmentFrames source, DeviceManager dm) { _source = source; _dm = dm; }
        /// <summary>With the guide camera.</summary>
        public StaticPolarAlignment(SidraluneGuider g, DeviceManager dm) : this(new GuiderFrames(g), dm) { }
        public AlignmentFrames Source => _source;

        public void Reset() { lock (_frames) _frames.Clear(); Result = null; StopTracking(); }

        private void Say(string s) { Status?.Invoke(s); }

        /// <summary>Frames must be coming, without guiding: the mount is turned between them.</summary>
        private void EnsureLooping()
        {
            var gf = _source as GuiderFrames;
            if (gf != null && (gf.Guider.InGuidingPhase || gf.Guider.IsCalibrating)) throw new InvalidOperationException(Lang.T("Stop guiding first: the mount turns between the frames."));
            _source.Ensure();
        }

        /// <summary>The next guide frame begun after a moment.</summary>
        public static Task<AstroImage> NextFrame(SidraluneGuider g, DateTime afterUtc, CancellationToken ct) => new GuiderFrames(g).Next(afterUtc, ct);

        /// <summary>Takes a frame here and plate-solves it.</summary>
        public async Task AddFrame(CancellationToken ct)
        {
            EnsureLooping();
            int n = Frames + 1;
            Say(Lang.T("Frame {0} of 3…", n));
            var f = await _source.Next(DateTime.UtcNow, ct);
            var opt = new SolveOptions { ScaleHint = _source.PixelScale, SearchRadius = 6 };
            var site = _dm.Site;
            var ctx = new SkyContext(site, AstroTime.FromDateTime(f.DateObs ?? DateTime.UtcNow));
            var tel = _dm.Telescope;
            // near the pole the mount's own coordinates are close enough; without one, the pole itself
            var guess = tel != null && tel.Connected
                ? ctx.ApparentToIcrs(tel.RightAscension * AstroMath.Hour2Rad, tel.Declination * AstroMath.Deg2Rad)
                : ctx.ApparentToIcrs(0, (site.LatitudeDeg >= 0 ? 90 : -90) * AstroMath.Deg2Rad);
            opt.HintRa = guess.RaHours * 15; opt.HintDec = guess.DecDeg;
            _dm.PrepareSolve(opt, _source.Camera);
            var sr = await Task.Run(() => PlateSolver.Solve(f, opt, null, ct), ct);
            if (!sr.Success) throw new InvalidOperationException(Lang.T("Frame {0} could not be plate-solved.", n) + " " + sr.Message);
            lock (_frames) _frames.Add(new SolvedGuideFrame { Time = (f.DateObs ?? DateTime.UtcNow).AddSeconds(f.ExposureSeconds / 2), Wcs = sr.Wcs, Image = f });
        }

        /// <summary>The mount takes the three frames itself, turning RA about StepDeg west between them.</summary>
        public async Task<StaticAlignmentResult> RunAutomatic(CancellationToken ct)
        {
            var tel = _dm.Telescope;
            if (tel == null || !tel.Connected) throw new InvalidOperationException(Lang.T("No telescope connected."));
            Reset();
            for (int k = 0; k < 3; k++)
            {
                if (k > 0)
                {
                    // west: the hour angle grows, so RA falls; east instead where going west would flip the mount
                    double ra = AstroMath.NormalizeHours(tel.RightAscension - StepDeg / 15), dec = tel.Declination;
                    bool east = false;
                    try { east = tel.DestinationSideOfPier(ra, dec) != tel.SideOfPier; } catch { }
                    if (east) ra = AstroMath.NormalizeHours(tel.RightAscension + StepDeg / 15);
                    Say(east ? Lang.T("Turning RA {0:0}° east…", StepDeg) : Lang.T("Turning RA {0:0}° west…", StepDeg));
                    await Centering.Slew(tel, ra, dec, ct);
                }
                await AddFrame(ct);
            }
            return Compute();
        }

        /// <summary>The axis, the pole and the errors from the three frames.</summary>
        public StaticAlignmentResult Compute()
        {
            List<SolvedGuideFrame> fr;
            lock (_frames) fr = new List<SolvedGuideFrame>(_frames);
            if (fr.Count < 3) throw new InvalidOperationException(Lang.T("Three frames are needed."));
            fr = fr.Skip(fr.Count - 3).ToList();
            var site = _dm.Site;
            var last = fr[2];
            int w = last.Image.Width, h = last.Image.Height;
            // points fixed to the camera, in the Earth-fixed frame in each frame: each traces a circle about the axis, and
            // the one with the widest circle tells it best
            Vec3 best = Vec3.Zero;
            foreach (var p in new[] { new[] { 0.5, 0.5 }, new[] { 0.1, 0.1 }, new[] { 0.9, 0.1 }, new[] { 0.1, 0.9 }, new[] { 0.9, 0.9 } })
            {
                var v = fr.Select(f =>
                {
                    double ra, dec;
                    f.Wcs.PixelToWorld(p[0] * (w - 1), p[1] * (h - 1), out ra, out dec);
                    var ctx = new SkyContext(site, AstroTime.FromDateTime(f.Time));
                    var app = ctx.IcrsToApparent(ra * AstroMath.Deg2Rad, dec * AstroMath.Deg2Rad);
                    return PolarAxis.FromApparent(app.Ra, app.Dec, ctx.Last);
                }).ToList();
                var c = Vec3.Cross(v[1] - v[0], v[2] - v[0]);
                if (c.Length > best.Length) best = c;
            }
            if (best.Length < 1e-12) throw new InvalidOperationException(Lang.T("The frames did not turn: was RA moved between them?"));
            var e = PolarAxis.Error(best, site, true);
            var res = new StaticAlignmentResult
            {
                AltErrorArcmin = e.AltErrorArcmin, AzErrorArcmin = e.AzErrorArcmin, TotalErrorArcmin = e.TotalErrorArcmin, AxisRa = e.AxisRa, AxisDec = e.AxisDec,
                Advice = e.Advice, Frame = last.Image, Wcs = last.Wcs, PixelScale = last.Wcs.PixelScale
            };
            var axis = best.Normalized();
            if (axis.Z * (site.LatitudeDeg >= 0 ? 1 : -1) < 0) axis = -axis;
            var lctx = new SkyContext(site, AstroTime.FromDateTime(last.Time));
            Func<Vec3, double[]> toPixel = v =>
            {
                double ra, dec, x, y;
                PolarAxis.ToApparent(v, lctx.Last, out ra, out dec);
                var icrs = lctx.ApparentToIcrs(ra, dec);
                return last.Wcs.WorldToPixel(icrs.RaHours * 15, icrs.DecDeg, out x, out y) ? new[] { x, y } : new[] { double.NaN, double.NaN };
            };
            var a = toPixel(axis);
            var pl = toPixel(PolarAxis.Pole(site, true));
            res.AxisX = a[0]; res.AxisY = a[1]; res.PoleX = pl[0]; res.PoleY = pl[1];
            // a star whose target is in the frame too: the correction moves the pole's image onto the axis's, and every
            // star the same way
            double sx = res.AxisX - res.PoleX, sy = res.AxisY - res.PoleY;
            var lum = last.Image.Luminance();
            var stars = GuideStars.SelectMore(lum, w, h, 12, 20, new List<double[]>(), 40);
            Func<double, double, bool> inside = (x, y) => x >= 20 && y >= 20 && x < w - 20 && y < h - 20;
            var pick = stars.FirstOrDefault(s => inside(s.X + sx, s.Y + sy)) ?? stars.FirstOrDefault();
            if (pick != null) { res.StarX = pick.X; res.StarY = pick.Y; res.TargetX = pick.X + sx; res.TargetY = pick.Y + sy; }
            Result = res;
            Log.Info("Guider", "Static polar alignment: " + res.Advice);
            return res;
        }

        /// <summary>Follows the reference star in each new frame, while the knobs are turned.</summary>
        public void StartTracking()
        {
            var r = Result;
            if (r == null || double.IsNaN(r.StarX) || Tracking) return;
            EnsureLooping();
            _trackX = r.StarX; _trackY = r.StarY;
            _source.FrameReady += OnFrame;
            Tracking = true;
        }

        public void StopTracking()
        {
            if (!Tracking) return;
            _source.FrameReady -= OnFrame;
            Tracking = false;
        }

        private void OnFrame(AstroImage f)
        {
            var lum = f.Luminance();
            var m = GuideStars.Measure(lum, f.Width, f.Height, _trackX, _trackY, Math.Max(40, 2 * _source.SearchRadius));
            if (m != null && !m.Saturated && m.Snr >= 5) { _trackX = m.X; _trackY = m.Y; StarMoved?.Invoke(m.X, m.Y); }
            else StarMoved?.Invoke(double.NaN, double.NaN);
        }

        /// <summary>The reference star's distance from its target now (arcmin), NaN when it is not followed.</summary>
        public double RemainingArcmin
        {
            get
            {
                var r = Result;
                if (r == null || double.IsNaN(_trackX) || double.IsNaN(r.TargetX)) return double.NaN;
                return Math.Sqrt((_trackX - r.TargetX) * (_trackX - r.TargetX) + (_trackY - r.TargetY) * (_trackY - r.TargetY)) * r.PixelScale / 60;
            }
        }

        public double StarX => _trackX;
        public double StarY => _trackY;
    }

    /// <summary>The polar drift result: the error, and where to move the star followed.</summary>
    public class PolarDriftResult
    {
        public double Seconds;
        public int Frames;
        public double DriftPxPerMin = double.NaN;
        public double ErrorArcmin = double.NaN, UncertaintyArcmin = double.NaN;
        /// <summary>The star now, and where correcting the alignment takes it (pixels).</summary>
        public double StarX = double.NaN, StarY = double.NaN, TargetX = double.NaN, TargetY = double.NaN;
        public bool Mirrored;
    }

    /// <summary>
    /// Polar drift alignment near the pole, without calibration or plate solving: a star is followed while the mount
    /// only tracks, in the imaging camera's frames or the guide camera's. In the camera's frame the sky turns about the
    /// pole while the camera turns about the mount's axis, so the stars drift at ω × the error, at right angles to it: the
    /// drift turned by 90° and divided by ω is the error. Which way the 90° turn goes depends on whether the image is
    /// mirrored (from the guider's calibration, for the guide camera with one).
    /// </summary>
    public class PolarDriftAlignment
    {
        private readonly AlignmentFrames _source;
        private readonly DeviceManager _dm;
        private readonly List<double[]> _samples = new List<double[]>(); // seconds, x, y
        private DateTime _start;
        private double _x = double.NaN, _y = double.NaN;

        /// <summary>The image is mirrored (an off-axis guider's prism, a diagonal); null: from the calibration, not mirrored without one.</summary>
        public bool? Mirrored;
        public bool Measuring { get; private set; }
        /// <summary>The star is followed in each frame (while measuring, and afterwards while it is moved into its circle).</summary>
        public bool Following { get; private set; }
        public PolarDriftResult Result { get; private set; }
        public AlignmentFrames Source => _source;
        /// <summary>The star followed, where it was last found (NaN before one is chosen).</summary>
        public double StarX => _x;
        public double StarY => _y;

        public PolarDriftAlignment(AlignmentFrames source, DeviceManager dm) { _source = source; _dm = dm; }
        /// <summary>With the guide camera.</summary>
        public PolarDriftAlignment(SidraluneGuider g, DeviceManager dm) : this(new GuiderFrames(g), dm) { }

        /// <summary>Whether the guider's calibration says the image is mirrored (null without a calibration).</summary>
        public static bool? MirroredFromCalibration(SidraluneGuider g)
        {
            var cal = g.Calibration;
            if (cal == null) return null;
            var tel = g.Devices.Telescope;
            var pier = tel != null && tel.Connected ? tel.SideOfPier : PierSide.Unknown;
            double e1, n1, e2, n2;
            cal.ImageToSky(1, 0, 1, pier, g.Settings.MountReversesDecAfterFlip, out e1, out n1);
            cal.ImageToSky(0, 1, 1, pier, g.Settings.MountReversesDecAfterFlip, out e2, out n2);
            return e1 * n2 - e2 * n1 < 0;
        }

        /// <summary>Whether the source's image is known to be mirrored: from the guider's calibration for the guide camera.</summary>
        public bool? MirroredFromSource()
        {
            var gf = _source as GuiderFrames;
            return gf != null ? MirroredFromCalibration(gf.Guider) : null;
        }

        /// <summary>Starts following a star (the brightest fit one in the next frame) and measuring its drift.</summary>
        public void Start()
        {
            if (Measuring) return;
            var gf = _source as GuiderFrames;
            if (gf != null && (gf.Guider.InGuidingPhase || gf.Guider.IsCalibrating)) throw new InvalidOperationException(Lang.T("Stop guiding first: polar drift alignment follows a star while the mount only tracks."));
            _source.Ensure();
            lock (_samples) _samples.Clear();
            Result = null;
            _x = _y = double.NaN;
            _start = DateTime.UtcNow;
            if (!Following) _source.FrameReady += OnFrame;
            Following = true;
            Measuring = true;
            Log.Info("Guider", "Polar drift alignment started");
        }

        private void OnFrame(AstroImage f)
        {
            var lum = f.Luminance();
            int r = _source.SearchRadius;
            GuideStarInfo s;
            if (double.IsNaN(_x)) s = GuideStars.Select(lum, f.Width, f.Height, r, double.NaN, double.NaN, 4 * r);
            else s = GuideStars.Measure(lum, f.Width, f.Height, _x, _y, Measuring ? r : 2 * r);
            if (s == null || s.Saturated) return;
            _x = s.X; _y = s.Y;
            if (!Measuring) return;
            double t = ((f.DateObs ?? DateTime.UtcNow).AddSeconds(f.ExposureSeconds / 2) - _start).TotalSeconds;
            lock (_samples) _samples.Add(new[] { t, s.X, s.Y });
        }

        public PolarDriftResult Current
        {
            get
            {
                List<double[]> copy;
                lock (_samples) copy = new List<double[]>(_samples);
                bool mirrored = Mirrored ?? MirroredFromSource() ?? false;
                return Analyse(copy, _source.PixelScale, mirrored, _dm.Site.LatitudeDeg >= 0);
            }
        }

        /// <summary>Ends measuring and gives the error and the target; the star is still followed, to move it into its circle.</summary>
        public PolarDriftResult Stop()
        {
            if (!Measuring) return Result;
            Measuring = false;
            Result = Current;
            Log.Info("Guider", string.Format("Polar drift alignment: {0:0.0}' ± {1:0.0}'", Result.ErrorArcmin, Result.UncertaintyArcmin));
            return Result;
        }

        /// <summary>Stops following and measuring, without a result.</summary>
        public void Cancel()
        {
            if (Following) _source.FrameReady -= OnFrame;
            Following = false;
            Measuring = false;
        }

        /// <summary>The error from a star's positions (seconds, x, y) at a pixel scale (arcsec).</summary>
        public static PolarDriftResult Analyse(IList<double[]> s, double arcsecPerPx, bool mirrored, bool north)
        {
            var r = new PolarDriftResult { Frames = s.Count, Mirrored = mirrored };
            if (s.Count < 5) return r;
            r.Seconds = s[s.Count - 1][0] - s[0][0];
            var t = s.Select(v => v[0]).ToList();
            var x = s.Select(v => v[1]).ToList();
            var y = s.Select(v => v[2]).ToList();
            double vx, vy, sx, sy;
            if (r.Seconds <= 0 || !DriftAlignment.Line(t, x, out vx, out sx) || !DriftAlignment.Line(t, y, out vy, out sy)) return r;
            double w = PolarAxis.SiderealRadPerSec;
            r.DriftPxPerMin = Math.Sqrt(vx * vx + vy * vy) * 60;
            // the error in pixels: the drift turned by 90° (one way in a plain image, the other in a mirrored one), over ω
            double ax = (mirrored ? vy : -vy) / w, ay = (mirrored ? -vx : vx) / w;
            // correcting the axis moves the stars by the error near the north pole, and the other way near the south one
            double k = north ? 1 : -1;
            r.StarX = x[x.Count - 1]; r.StarY = y[y.Count - 1];
            r.TargetX = r.StarX + k * ax; r.TargetY = r.StarY + k * ay;
            r.ErrorArcmin = Math.Sqrt(ax * ax + ay * ay) * arcsecPerPx / 60;
            // the uncertainty from the two slopes'
            r.UncertaintyArcmin = Math.Sqrt((sx * sx + sy * sy) / 2) / w * arcsecPerPx / 60;
            return r;
        }
    }
}
