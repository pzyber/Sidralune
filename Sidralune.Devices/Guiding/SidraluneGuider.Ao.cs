using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Sidralune.Core.Mathematics;
using Sidralune.Core.Util;

namespace Sidralune.Devices.Guiding
{
    /// <summary>
    /// How an AO unit's steps move the guide star: the image direction of a West (+X) and a North (+Y) step and the
    /// pixels per step, measured by AO calibration, with the camera setup it was made with.
    /// </summary>
    public class AoCalibration
    {
        public double XAngleDeg, YAngleDeg, XPxPerStep, YPxPerStep;
        public int Binning = 1;
        public string Camera = "";
        public DateTime When;

        public bool Fits(int binning, string camera) => XPxPerStep > 0 && YPxPerStep > 0 && Binning == binning && Camera == (camera ?? "");

        /// <summary>Splits an image offset into pixels along the X and Y step directions (axes need not be square).</summary>
        public void ToAxes(double ex, double ey, out double ax, out double ay)
        {
            double xx = Math.Cos(XAngleDeg * AstroMath.Deg2Rad), xy = Math.Sin(XAngleDeg * AstroMath.Deg2Rad);
            double yx = Math.Cos(YAngleDeg * AstroMath.Deg2Rad), yy = Math.Sin(YAngleDeg * AstroMath.Deg2Rad);
            double det = xx * yy - yx * xy;
            if (Math.Abs(det) < 1e-3) { ax = ex * xx + ey * xy; ay = ex * yx + ey * yy; return; }
            ax = (ex * yy - yx * ey) / det;
            ay = (xx * ey - ex * xy) / det;
        }

        /// <summary>The image offset of ax pixels along X and ay along Y.</summary>
        public void FromAxes(double ax, double ay, out double ex, out double ey)
        {
            ex = ax * Math.Cos(XAngleDeg * AstroMath.Deg2Rad) + ay * Math.Cos(YAngleDeg * AstroMath.Deg2Rad);
            ey = ax * Math.Sin(XAngleDeg * AstroMath.Deg2Rad) + ay * Math.Sin(YAngleDeg * AstroMath.Deg2Rad);
        }
    }

    public partial class SidraluneGuider
    {
        private readonly HysteresisAlgorithm _aoX = new HysteresisAlgorithm { Hysteresis = 0.1 }, _aoY = new HysteresisAlgorithm { Hysteresis = 0.1 };
        private double _bumpScale = 1;
        private bool _bumping, _aoOnlyCalibration;
        private int _bumpAxis;

        /// <summary>An AO unit is connected, so the AO guides and the mount only takes over large offsets.</summary>
        public bool UsingAo { get { var ao = _dm.Ao; return ao != null && ao.Connected && _dm.Phd2Connected == false; } }
        public AoCalibration AoCalibration => Settings.AoCalibration;
        /// <summary>The mount is being bumped to bring the AO back towards its centre.</summary>
        public bool Bumping => _bumping;

        private bool AoCalibrationFits() => Settings.AoCalibration != null && Settings.AoCalibration.Fits(_bin, _cameraId);

        public void ClearAoCalibration()
        {
            Settings.AoCalibration = null;
            SaveSettings();
            Log.Info("Guider", "AO calibration cleared");
            CalibrationChanged?.Invoke();
        }

        private IAdaptiveOptics PrepareAo()
        {
            var ao = _dm.Ao;
            if (ao == null || !ao.Connected) return null;
            if (Settings.AoTravel > 0 && ao.MaxSteps != Settings.AoTravel) ao.MaxSteps = Settings.AoTravel;
            return ao;
        }

        /// <summary>
        /// AO calibration: from the centre, X steps West until the calibration length, back, then Y steps North and back.
        /// Each axis's direction and pixels per step come from a line through every position.
        /// </summary>
        private async Task RunAoCalibration(CancellationToken ct)
        {
            var ao = PrepareAo();
            if (ao == null) return;
            _log.Event("AO calibration", "started");
            Log.Info("Guider", "Calibrating the AO");
            ao.Center();
            await NextPosition(ct);
            int step = Math.Max(1, Settings.AoCalStep), total = Math.Max(step * 3, Math.Min(Settings.AoCalSteps, ao.MaxSteps));
            var x = await AoLeg(ao, AoDirection.West, AoDirection.East, step, total, ct);
            var y = await AoLeg(ao, AoDirection.North, AoDirection.South, step, total, ct);
            double xa, xr, ya, yr;
            if (!GuideCalibration.FitLeg(x.T, x.X, x.Y, out xa, out xr) || xr < 0.02)
                throw new InvalidOperationException(Lang.T("AO calibration failed: the star did not move with the AO's X steps."));
            if (!GuideCalibration.FitLeg(y.T, y.X, y.Y, out ya, out yr) || yr < 0.02)
                throw new InvalidOperationException(Lang.T("AO calibration failed: the star did not move with the AO's Y steps."));
            Settings.AoCalibration = new AoCalibration { XAngleDeg = xa, YAngleDeg = ya, XPxPerStep = xr, YPxPerStep = yr, Binning = _bin, Camera = _cameraId, When = DateTime.UtcNow };
            SaveSettings();
            string summary = string.Format(CultureInfo.InvariantCulture, "X {0:0.0}° {1:0.000} px/step, Y {2:0.0}° {3:0.000} px/step", xa, xr, ya, yr);
            _log.Event("AO calibrated", summary);
            Log.Info("Guider", "AO calibrated: " + summary);
            _aoX.Reset(); _aoY.Reset();
            CalibrationChanged?.Invoke();
        }

        private async Task<Leg> AoLeg(IAdaptiveOptics ao, AoDirection out_, AoDirection back, int step, int total, CancellationToken ct)
        {
            var leg = new Leg();
            leg.T.Add(0); leg.X.Add(Star.X); leg.Y.Add(Star.Y);
            int done = 0;
            while (done < total)
            {
                int n = Math.Min(step, total - done);
                if (!ao.Step(out_, n)) break;
                done += n;
                await NextPosition(ct);
                leg.T.Add(done); leg.X.Add(Star.X); leg.Y.Add(Star.Y);
                _log.Row("AO calibration " + out_, Star, double.NaN, double.NaN, double.NaN, double.NaN, 0, 0, done + " steps");
            }
            ao.Step(back, done);
            await NextPosition(ct);
            leg.Steps = done;
            return leg;
        }

        /// <summary>One guide step with the AO: the AO takes the whole correction; the mount is bumped when the AO nears its limits.</summary>
        private void CorrectWithAo(IAdaptiveOptics ao, GuideCalibration cal, double ex, double ey, out string note)
        {
            note = "";
            var ac = Settings.AoCalibration;
            double ax, ay;
            ac.ToAxes(ex, ey, out ax, out ay);
            _aoX.MinMovePx = _aoY.MinMovePx = Settings.AoMinMovePx;
            _aoX.Aggressiveness = _aoY.Aggressiveness = Settings.AoAggressiveness / 100;
            double cx = _aoX.Correction(ax), cy = _aoY.Correction(ay);
            int nx = (int)Math.Round(Math.Abs(cx) / ac.XPxPerStep), ny = (int)Math.Round(Math.Abs(cy) / ac.YPxPerStep);
            // the star moved the way a West step moves it: an East step brings it back (South for North)
            bool limit = false;
            if (nx > 0) limit |= !ao.Step(cx > 0 ? AoDirection.East : AoDirection.West, nx);
            if (ny > 0) limit |= !ao.Step(cy > 0 ? AoDirection.South : AoDirection.North, ny);
            note = string.Format(CultureInfo.InvariantCulture, "AO {0} {1}{2}", ao.PositionX, ao.PositionY, limit ? " at its limit" : "");
            // near the end of its travel: the mount moves the star so the AO can come back
            double threshold = Settings.AoBumpPercent / 100.0 * ao.MaxSteps;
            if (Math.Abs(ao.PositionX) > threshold || Math.Abs(ao.PositionY) > threshold)
            {
                _bumpScale = _bumping ? Math.Min(10, _bumpScale * 1.5) : 1;
                if (!_bumping) _log.Event("Bump", "started");
                _bumping = true;
                Bump(ao, cal, _bumpScale);
                note += " bump";
            }
            else if (_bumping) { _bumping = false; _log.Event("Bump", "done"); }
        }

        /// <summary>
        /// A mount pulse towards recentring the AO: the star is moved by the shift the AO now gives (so the AO can step
        /// back), at most scale × the bump step, on one axis a frame, without waiting for it.
        /// </summary>
        private void Bump(IAdaptiveOptics ao, GuideCalibration cal, double scale, bool whole = false)
        {
            var ac = Settings.AoCalibration;
            if (cal == null || ac == null) return;
            double vx, vy, ra, dec;
            ac.FromAxes(ao.PositionX * ac.XPxPerStep, ao.PositionY * ac.YPxPerStep, out vx, out vy);
            cal.ToAxes(vx, vy, MountPierSide(), Settings.MountReversesDecAfterFlip, out ra, out dec);
            // moving the star by +v along an axis is the correction of an error of -v
            double raC = -ra, decC = -dec;
            double limit = whole ? double.MaxValue : Math.Max(0.05, Settings.AoBumpStepArcsec / PixelScale * scale);
            raC = Math.Sign(raC) * Math.Min(Math.Abs(raC), limit);
            decC = Math.Sign(decC) * Math.Min(Math.Abs(decC), limit);
            double raRate = cal.RaRateAt(MountDeclination()), decRate = cal.DecRatePxPerSec;
            var pulser = new GuidePulser(_dm, Settings.Output);
            bool doRa = whole || _bumpAxis++ % 2 == 0;
            if ((doRa || Math.Abs(decC) < 1e-6) && raRate > 0 && Math.Abs(raC) > 1e-6)
                pulser.Start(raC > 0 ? GuideDirection.East : GuideDirection.West, (int)Math.Min(Settings.MaxRaPulseMs, Math.Abs(raC) / raRate * 1000));
            if ((!doRa || whole || Math.Abs(raC) < 1e-6) && decRate > 0 && Math.Abs(decC) > 1e-6)
            {
                var dir = decC > 0 ? GuideDirection.South : GuideDirection.North;
                int ms = (int)Math.Min(Settings.MaxDecPulseMs, Math.Abs(decC) / decRate * 1000);
                // backlash compensation at its set amount: it cannot adapt while the AO hides the mount's moves
                int sign = dir == GuideDirection.North ? 1 : -1;
                if (Settings.BacklashCompensation && _backlash.LastDirection != 0 && _backlash.LastDirection != sign) ms += (int)Settings.BacklashMs;
                pulser.Start(dir, ms);
                _backlash.Pulsed(sign);
            }
        }

        /// <summary>
        /// One AO step move while looping, measured in the next frame, with what the AO calibration expected.
        /// </summary>
        public async Task<ManualPulseResult> ManualAoStep(AoDirection dir, int steps, CancellationToken ct)
        {
            if (!IsRunning) throw new InvalidOperationException(Lang.T("Click Loop first, so the pulse's effect can be seen."));
            if (_phase != Phase.Watching) throw new InvalidOperationException(Lang.T("Manual pulses are for looping, not while calibrating or guiding."));
            var ao = PrepareAo();
            if (ao == null) throw new InvalidOperationException(Lang.T("No AO connected."));
            var res = new ManualPulseResult { Ms = Math.Max(1, steps) };
            await RunInLoop(async t =>
            {
                var before = Star != null && !_lost ? Star : null;
                ao.Step(dir, res.Ms);
                Track(await Frame(t));
                var after = Star != null && !_lost ? Star : null;
                if (before == null || after == null) return;
                res.Dx = after.X - before.X; res.Dy = after.Y - before.Y;
                res.Distance = Math.Sqrt(res.Dx * res.Dx + res.Dy * res.Dy);
                res.AngleDeg = Math.Atan2(res.Dy, res.Dx) * AstroMath.Rad2Deg;
                var ac = Settings.AoCalibration;
                if (ac == null) return;
                bool x = dir == AoDirection.West || dir == AoDirection.East;
                double sign = dir == AoDirection.West || dir == AoDirection.North ? 1 : -1;
                double a = (x ? ac.XAngleDeg : ac.YAngleDeg) * AstroMath.Deg2Rad;
                res.ExpectedAngleDeg = Math.Atan2(sign * Math.Sin(a), sign * Math.Cos(a)) * AstroMath.Rad2Deg;
                res.ExpectedDistance = res.Ms * (x ? ac.XPxPerStep : ac.YPxPerStep);
                res.Matches = Math.Abs(AstroMath.NormalizeDeg180(res.AngleDeg - res.ExpectedAngleDeg)) <= 30 && Math.Abs(res.Distance / res.ExpectedDistance - 1) <= 0.5;
            }, ct);
            _log.Row("Manual AO " + dir, Star, res.Dx, res.Dy, double.NaN, double.NaN, 0, 0, res.Ms + " steps");
            return res;
        }
    }
}
