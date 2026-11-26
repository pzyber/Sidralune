using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Sidralune.Core.Mathematics;
using Sidralune.Core.Util;
using Sidralune.Imaging;
using Sidralune.Imaging.IO;

namespace Sidralune.Devices.Guiding
{
    /// <summary>One position of the star in the Star-Cross test: the leg it was on and its offset from where it began (pixels).</summary>
    public class StarCrossPoint
    {
        public string Leg;
        public double X, Y;
    }

    /// <summary>
    /// The Star-Cross test: the star driven out and back along each axis in a cross, which shows whether RA and Dec are at
    /// right angles, how fast each direction moves it, and the backlash and stiction when an axis starts or turns round.
    /// </summary>
    public class StarCrossResult
    {
        public readonly List<StarCrossPoint> Track = new List<StarCrossPoint>();
        /// <summary>The pulse of each step (ms), and about how many steps go out by the calibration distance.</summary>
        public int RaStepMs, DecStepMs, StepsOut;
        /// <summary>The image directions (degrees) West and North pulses moved the star, and how far from square they are.</summary>
        public double RaAngleDeg = double.NaN, DecAngleDeg = double.NaN, OrthogonalityErrorDeg = double.NaN;
        /// <summary>The speed of each direction (pixels per second of pulse), once moving.</summary>
        public double WestRate = double.NaN, EastRate = double.NaN, NorthRate = double.NaN, SouthRate = double.NaN;
        /// <summary>The pulse time lost when an axis turned round (ms): RA West to East and back, Dec North to South and back.</summary>
        public double RaTurnMs = double.NaN, RaReturnMs = double.NaN, DecTurnMs = double.NaN, DecReturnMs = double.NaN;
        /// <summary>How far the first pulse from rest moved the star, as a share of a pulse once moving (1: no stiction).</summary>
        public double RaFirstStep = double.NaN, DecFirstStep = double.NaN;
        /// <summary>The guide frames of the test laid over each other, which draws the cross as a trail; null when not saved.</summary>
        public string ImagePath;
    }

    public partial class SidraluneGuider
    {
        // ------------------------------------------------------------------ the Star-Cross test

        /// <summary>
        /// The Star-Cross test, run while guiding (corrections wait meanwhile): the star is driven West in steps until it has
        /// moved the calibration distance, East until twice as far the other way, West back to the middle, then the same
        /// North, South and North; backlash only takes more steps. Each step is measured; the guide frames are laid over each
        /// other into an image of the cross. Guiding goes on afterwards.
        /// </summary>
        public async Task<StarCrossResult> RunStarCross(CancellationToken ct)
        {
            var cal = Settings.Calibration;
            if (_phase != Phase.Guiding || cal == null) throw new InvalidOperationException(Lang.T("Not guiding."));
            var res = new StarCrossResult();
            await RunInLoop(async t =>
            {
                string lost = Lang.T("The guide star was lost during the Star-Cross test.");
                string slewed = Lang.T("The Star-Cross test stopped because the mount slewed.");
                var pulser = new GuidePulser(_dm, Settings.Output);
                double dist = Math.Max(10, Settings.CalibrationDistancePx), stepPx = Math.Min(0.6 * Settings.SearchRadius, dist / 5);
                res.StepsOut = Math.Max(3, (int)Math.Ceiling(dist / stepPx));
                res.RaStepMs = (int)AstroMath.Clamp(stepPx / Math.Max(1e-3, cal.RaRateAt(MountDeclination())) * 1000, 50, 5000);
                res.DecStepMs = (int)AstroMath.Clamp(stepPx / Math.Max(1e-3, cal.DecRatePxPerSec) * 1000, 50, 5000);
                double rx, ry, nx, ny;
                cal.Directions(MountPierSide(), Settings.MountReversesDecAfterFlip, out rx, out ry, out nx, out ny);
                await NextPosition(t, slewed, lost);
                double x0 = Star.X, y0 = Star.Y;
                float[] trail = null;
                int tw = 0, th = 0;
                Action addFrame = () =>
                {
                    var f = LastFrame;
                    if (f == null) return;
                    var lum = f.Luminance();
                    if (trail == null) { trail = (float[])lum.Clone(); tw = f.Width; th = f.Height; return; }
                    if (f.Width != tw || f.Height != th) return;
                    for (int i = 0; i < trail.Length; i++) if (lum[i] > trail[i]) trail[i] = lum[i];
                };
                addFrame();
                res.Track.Add(new StarCrossPoint { Leg = "Start", X = 0, Y = 0 });
                // one leg: pulses one way until the star has gone far enough along the axis (backlash only makes it take
                // more pulses), its position along the axis after each
                Func<GuideDirection, int, Func<double, double, bool>, int, Task<List<double>>> leg = async (dir, ms, far, maxSteps) =>
                {
                    bool ra = dir == GuideDirection.West || dir == GuideDirection.East;
                    Func<double> pos = () => ra ? (Star.X - x0) * rx + (Star.Y - y0) * ry : (Star.X - x0) * nx + (Star.Y - y0) * ny;
                    var along = new List<double> { pos() };
                    for (int k = 0; k < maxSteps && !far(along[0], along[along.Count - 1]); k++)
                    {
                        await SendPulse(pulser, dir, ms, t);
                        await NextPosition(t, slewed, lost);
                        addFrame();
                        res.Track.Add(new StarCrossPoint { Leg = dir.ToString(), X = Star.X - x0, Y = Star.Y - y0 });
                        along.Add(pos());
                        _log.Row("Star-Cross " + dir, Star, Star.X - x0, Star.Y - y0, double.NaN, double.NaN, ra ? ms : 0, ra ? 0 : ms);
                    }
                    return along;
                };
                // out by the distance, across twice as far, back to the middle: West, East, West, then North, South, North
                Func<double, double, bool> outward = (from, now) => now - from >= dist;
                Func<double, double, bool> across = (from, now) => from - now >= 2 * dist;
                int trackFrom = res.Track.Count;
                var west = await leg(GuideDirection.West, res.RaStepMs, outward, 40);
                int westEnd = res.Track.Count;
                var east = await leg(GuideDirection.East, res.RaStepMs, across, 80);
                double raMid = west[0];
                var westBack = await leg(GuideDirection.West, res.RaStepMs, (from, now) => now >= raMid, 60);
                double xm = Star.X, ym = Star.Y;
                int northFrom = res.Track.Count;
                var north = await leg(GuideDirection.North, res.DecStepMs, outward, 40);
                int northEnd = res.Track.Count;
                var south = await leg(GuideDirection.South, res.DecStepMs, across, 80);
                double decMid = north[0];
                var northBack = await leg(GuideDirection.North, res.DecStepMs, (from, now) => now >= decMid, 60);

                // the directions: lines through the first West and North legs
                res.RaAngleDeg = LegAngle(res.Track.Skip(trackFrom - 1).Take(westEnd - trackFrom + 1).ToList());
                var nPts = new List<StarCrossPoint> { new StarCrossPoint { X = xm - x0, Y = ym - y0 } };
                nPts.AddRange(res.Track.Skip(northFrom).Take(northEnd - northFrom));
                res.DecAngleDeg = LegAngle(nPts);
                if (!double.IsNaN(res.RaAngleDeg) && !double.IsNaN(res.DecAngleDeg))
                    res.OrthogonalityErrorDeg = Math.Abs(Math.Abs(AstroMath.NormalizeDeg180(res.DecAngleDeg - res.RaAngleDeg)) - 90);
                // the speeds once moving, the time lost turning round, and how far the first pulse from rest went
                double lag;
                res.WestRate = Moving(west, res.RaStepMs, +1, out lag);
                res.EastRate = Moving(east, res.RaStepMs, -1, out res.RaTurnMs);
                Moving(westBack, res.RaStepMs, +1, out res.RaReturnMs);
                res.NorthRate = Moving(north, res.DecStepMs, +1, out lag);
                res.SouthRate = Moving(south, res.DecStepMs, -1, out res.DecTurnMs);
                Moving(northBack, res.DecStepMs, +1, out res.DecReturnMs);
                res.RaFirstStep = FirstStep(west);
                res.DecFirstStep = FirstStep(north);
                _resetAlgorithms = true;

                if (trail != null)
                {
                    try
                    {
                        var img = new AstroImage(tw, th, new[] { trail });
                        img.DateObs = DateTime.UtcNow;
                        img.Header.Set("OBJECT", "Star-Cross test");
                        string folder = LogFolder ?? AppPaths.Logs;
                        Directory.CreateDirectory(folder);
                        res.ImagePath = Path.Combine(folder, "Star-Cross " + DateTime.Now.ToString("yyyy-MM-dd HHmmss", CultureInfo.InvariantCulture) + ".fits");
                        ImageFile.Save(res.ImagePath, img, ImageSaveFormat.Fits16);
                    }
                    catch (Exception ex) { res.ImagePath = null; Log.Warn("Guider", "The Star-Cross image was not saved: " + ex.Message); }
                }
                _log.Event("Star-Cross test", string.Format(CultureInfo.InvariantCulture,
                    "{0:0.0}° from square; px/s W {1:0.00} E {2:0.00} N {3:0.00} S {4:0.00}; turns RA {5:0} / {6:0} ms, Dec {7:0} / {8:0} ms; first steps RA {9:0.00} Dec {10:0.00}",
                    res.OrthogonalityErrorDeg, res.WestRate, res.EastRate, res.NorthRate, res.SouthRate, res.RaTurnMs, res.RaReturnMs, res.DecTurnMs, res.DecReturnMs, res.RaFirstStep, res.DecFirstStep));
            }, ct);
            return res;
        }

        /// <summary>The image direction (degrees) of a line fitted through a leg's positions in order.</summary>
        private static double LegAngle(List<StarCrossPoint> pts)
        {
            if (pts.Count < 3) return double.NaN;
            var k = Enumerable.Range(0, pts.Count).Select(i => (double)i).ToList();
            double a, r;
            return GuideCalibration.FitLeg(k, pts.Select(p => p.X).ToList(), pts.Select(p => p.Y).ToList(), out a, out r) ? a : double.NaN;
        }

        /// <summary>
        /// A leg once the star got going (over 15 % of the way, and a pixel): its speed (pixels per second of pulse) from a
        /// line through those steps, and the pulse time lost before it moved (ms), where that line meets the leg's start:
        /// backlash after turning round. Sign +1 for a leg along the axis's direction, -1 against it. NaN with too few steps.
        /// </summary>
        private static double Moving(List<double> along, int stepMs, int sign, out double lagMs)
        {
            lagMs = double.NaN;
            double went = Math.Abs(along[along.Count - 1] - along[0]);
            if (went < 1) return double.NaN;
            var t = new List<double>(); var q = new List<double>();
            for (int i = 1; i < along.Count; i++)
            {
                double moved = sign * (along[i] - along[0]);
                if (moved > Math.Max(1, 0.15 * went)) { t.Add(i * stepMs / 1000.0); q.Add(moved); }
            }
            if (t.Count < 3) return double.NaN;
            var f = LinAlg.PolyFit(t, q, 1);
            if (f == null || f[1] <= 0) return double.NaN;
            lagMs = Math.Max(0, -f[0] / f[1] * 1000);
            return f[1];
        }

        /// <summary>The first step's move as a share of the median step's later in the leg.</summary>
        private static double FirstStep(List<double> along)
        {
            if (along.Count < 4) return double.NaN;
            var d = new List<double>();
            for (int i = 2; i < along.Count; i++) d.Add(Math.Abs(along[i] - along[i - 1]));
            d.Sort();
            double med = d[d.Count / 2];
            return med > 0 ? Math.Abs(along[1] - along[0]) / med : double.NaN;
        }

        // ------------------------------------------------------------------ guide frames kept for diagnosis

        private class KeptFrame
        {
            public AstroImage Frame;
            public double LockX, LockY, StarX, StarY;
            public string State;
            public DateTime Time;
        }

        private readonly Queue<KeptFrame> _kept = new Queue<KeptFrame>();
        private List<KeptFrame> _saving;
        private int _savingAfter;
        private string _savingWhy;
        private DateTime _lastDiagnosis = DateTime.MinValue;
        /// <summary>The frames kept before an event and taken after it, and the shortest time between two saves.</summary>
        public int DiagnosisFramesBefore = 5, DiagnosisFramesAfter = 3;
        public TimeSpan DiagnosisInterval = TimeSpan.FromMinutes(5);
        /// <summary>The folder the last guide frames were saved to for diagnosis; null until then.</summary>
        public string LastDiagnosisFolder { get; private set; }

        /// <summary>Each measured frame is kept a while, and taken for a save under way.</summary>
        private void KeepForDiagnosis(AstroImage frame)
        {
            if (!Settings.SaveDiagnosticFrames) { _kept.Clear(); return; }
            var k = new KeptFrame { Frame = frame, LockX = LockX, LockY = LockY, StarX = Star != null ? Star.X : double.NaN, StarY = Star != null ? Star.Y : double.NaN, State = _state, Time = DateTime.UtcNow };
            if (_saving != null)
            {
                _saving.Add(k);
                if (--_savingAfter <= 0) WriteDiagnosis();
                return;
            }
            _kept.Enqueue(k);
            while (_kept.Count > Math.Max(1, DiagnosisFramesBefore)) _kept.Dequeue();
        }

        /// <summary>
        /// Something went wrong (a lost star, a failed calibration): the frames before it and the next few are saved, at
        /// most once every DiagnosisInterval, in Guide frames\&lt;date time&gt; &lt;why&gt; in the log folder.
        /// </summary>
        private void SaveDiagnosis(string why, bool waitForMore = true)
        {
            if (!Settings.SaveDiagnosticFrames || _saving != null || DateTime.UtcNow - _lastDiagnosis < DiagnosisInterval || _kept.Count == 0) return;
            _lastDiagnosis = DateTime.UtcNow;
            _saving = new List<KeptFrame>(_kept);
            _kept.Clear();
            _savingWhy = why;
            _savingAfter = waitForMore ? DiagnosisFramesAfter : 0;
            if (_savingAfter <= 0) WriteDiagnosis();
        }

        private void WriteDiagnosis()
        {
            var frames = _saving;
            string why = _savingWhy;
            _saving = null;
            if (frames == null || frames.Count == 0) return;
            string folder = Path.Combine(LogFolder ?? AppPaths.Logs, "Guide frames", DateTime.Now.ToString("yyyy-MM-dd HHmmss", CultureInfo.InvariantCulture) + " " + why);
            LastDiagnosisFolder = folder;
            _log.Event("Frames saved", folder);
            Task.Run(() =>
            {
                try
                {
                    Directory.CreateDirectory(folder);
                    for (int i = 0; i < frames.Count; i++)
                    {
                        var k = frames[i];
                        var h = k.Frame.Header;
                        h.Set("OBJECT", why);
                        h.Set("GUIDSTAT", k.State ?? "", "Guider state");
                        if (!double.IsNaN(k.LockX)) { h.Set("LOCKX", Math.Round(k.LockX, 3), "Lock position X [px]"); h.Set("LOCKY", Math.Round(k.LockY, 3), "Lock position Y [px]"); }
                        if (!double.IsNaN(k.StarX)) { h.Set("STARX", Math.Round(k.StarX, 3), "Guide star X [px]"); h.Set("STARY", Math.Round(k.StarY, 3), "Guide star Y [px]"); }
                        ImageFile.Save(Path.Combine(folder, string.Format(CultureInfo.InvariantCulture, "{0:000} {1:HHmmss.fff}.fits", i + 1, k.Time.ToLocalTime())), k.Frame, ImageSaveFormat.Fits16);
                    }
                    Log.Info("Guider", "Guide frames saved for diagnosis (" + why + "): " + folder);
                }
                catch (Exception ex) { Log.Warn("Guider", "The guide frames for diagnosis were not saved: " + ex.Message); }
            });
        }
    }
}
