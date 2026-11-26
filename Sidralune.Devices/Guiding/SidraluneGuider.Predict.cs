using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Sidralune.Core.Mathematics;
using Sidralune.Core.Util;

namespace Sidralune.Devices.Guiding
{
    public partial class SidraluneGuider
    {
        /// <summary>A prediction checked with a few steps: the calibration made from it, or why it was not used.</summary>
        private class PredictionCheck
        {
            public GuideCalibration Calibration;
            public string Miss, Why;
            public double MissBy = double.NaN;
        }

        /// <summary>How far (degrees) a measured direction may be from the predicted one, and a speed from the predicted (fraction).</summary>
        public const double PredictionAngleTolerance = 20, PredictionSpeedTolerance = 0.3;

        /// <summary>
        /// The image directions (degrees, as in GuideCalibration) in which West and North pulses should move the star at
        /// (x, y), from the guide frame solved at this pointing: a West pulse moves the telescope west, so the stars move
        /// east in the image; a North pulse moves it north (on this side of the pier, unless the mount reverses it; the
        /// steps that confirm the prediction find out which), so they move south. False without a solve here.
        /// </summary>
        private bool PredictDirections(double x, double y, out double westDeg, out double northDeg)
        {
            westDeg = northDeg = double.NaN;
            var sky = _sky;
            if (sky == null || !Settings.SolveGuideFrames || !SkyStillHere()) return false;
            double ra, dec, ex, ey, nx, ny;
            sky.Wcs.PixelToWorld(x, y, out ra, out dec);
            const double step = 30.0 / 3600;
            double cosd = Math.Max(0.01, Math.Cos(dec * AstroMath.Deg2Rad));
            if (!sky.Wcs.WorldToPixel(ra + step / cosd, dec, out ex, out ey)) return false;
            if (!sky.Wcs.WorldToPixel(ra, AstroMath.Clamp(dec + step, -90, 90), out nx, out ny)) return false;
            westDeg = Math.Atan2(ey - y, ex - x) * AstroMath.Rad2Deg;
            northDeg = Math.Atan2(y - ny, x - nx) * AstroMath.Rad2Deg;
            return true;
        }

        /// <summary>A pulse for about three steps over the distance, between 0.1 and 5 s.</summary>
        private static int ConfirmStepFor(double dist, double ratePxPerSec) =>
            ratePxPerSec > 0 && !double.IsInfinity(ratePxPerSec) ? (int)AstroMath.Clamp(Math.Round(dist / 3 / ratePxPerSec * 1000), 100, 5000) : 1500;

        /// <summary>
        /// Three steps West (and back East), the Dec backlash taken up, three steps North (and back South): each direction
        /// must be within PredictionAngleTolerance of the prediction (North may come out reversed: the mount's North moves
        /// the telescope south on this side of the pier), and each speed within PredictionSpeedTolerance of what the guide
        /// rate the mount reports gives. The calibration then is the prediction: the solve's directions and the reported
        /// guide rates' speeds (measured speeds when the mount does not report its guide rates).
        /// </summary>
        private async Task<PredictionCheck> ConfirmPrediction(double predWest, double predNorth, double predRa, double predDec, bool raKnown, bool decKnown,
            double gRa, double gDec, double cosd, double dist, GuidePulser pulser, CancellationToken ct)
        {
            var res = new PredictionCheck();
            _log.Event("Calibration", string.Format(CultureInfo.InvariantCulture, "predicted from the plate solve: RA {0:0.0}°, Dec {1:0.0}°, {2:0.00} and {3:0.00} px/s", predWest, predNorth, predRa, predDec));
            Log.Info("Guider", "Confirming the calibration predicted from the plate solve");

            double ra0 = MountRa();
            var west = await RunLeg(GuideDirection.West, ConfirmStepFor(dist, predRa), dist, Star.X, Star.Y, pulser, ct, 3);
            int westSign = Sign(-AstroMath.NormalizeDeg180((MountRa() - ra0) * 15) * 3600 * cosd);
            double wAngle, wRate;
            bool wOk = GuideCalibration.FitLeg(west.T, west.X, west.Y, out wAngle, out wRate);
            await Return(GuideDirection.East, west.TotalMs, wOk ? wRate : predRa, Settings.MaxRaPulseMs, pulser, ct);
            if (!wOk) return Missed(res, GuideCalibration.MissRaSpeed, 0, "RA did not move the star");
            double off = Math.Abs(AstroMath.NormalizeDeg180(wAngle - predWest));
            if (off > PredictionAngleTolerance) return Missed(res, GuideCalibration.MissRaDirection, off, string.Format(CultureInfo.InvariantCulture, "RA moved the star {0:0}° from the predicted direction", off));
            if (raKnown && Math.Abs(wRate / predRa - 1) > PredictionSpeedTolerance)
                return Missed(res, GuideCalibration.MissRaSpeed, 100 * wRate / predRa, string.Format(CultureInfo.InvariantCulture, "RA moved the star at {0:0}% of the predicted speed", 100 * wRate / predRa));
            double raRate = raKnown ? predRa : wRate;

            double expDec = raRate / cosd * (raKnown && decKnown ? gDec / gRa : 1);
            int clearingMs = await ClearBacklash(StepFor(dist, expDec), expDec, pulser, ct);
            double dec0 = MountDeclination();
            var north = await RunLeg(GuideDirection.North, ConfirmStepFor(dist, expDec), dist, Star.X, Star.Y, pulser, ct, 3);
            int northSign = Sign((MountDeclination() - dec0) * 3600);
            double nAngle, nRate;
            bool nOk = GuideCalibration.FitLeg(north.T, north.X, north.Y, out nAngle, out nRate);
            await Return(GuideDirection.South, north.TotalMs + clearingMs, nOk ? nRate : expDec, Settings.MaxDecPulseMs, pulser, ct);
            if (!nOk) return Missed(res, GuideCalibration.MissDecSpeed, 0, "Dec did not move the star");
            double nOff = Math.Abs(AstroMath.NormalizeDeg180(nAngle - predNorth));
            bool reversed = nOff > 90;
            if (reversed) nOff = 180 - nOff;
            if (nOff > PredictionAngleTolerance) return Missed(res, GuideCalibration.MissDecDirection, nOff, string.Format(CultureInfo.InvariantCulture, "Dec moved the star {0:0}° from the predicted direction", nOff));
            if (decKnown && Math.Abs(nRate / predDec - 1) > PredictionSpeedTolerance)
                return Missed(res, GuideCalibration.MissDecSpeed, 100 * nRate / predDec, string.Format(CultureInfo.InvariantCulture, "Dec moved the star at {0:0}% of the predicted speed", 100 * nRate / predDec));

            res.Calibration = new GuideCalibration
            {
                RaAngleDeg = AstroMath.NormalizeDeg180(predWest), DecAngleDeg = AstroMath.NormalizeDeg180(reversed ? predNorth + 180 : predNorth),
                RaRatePxPerSec = raRate, DecRatePxPerSec = decKnown ? predDec : nRate,
                RaSteps = west.Steps, DecSteps = north.Steps, WestSign = westSign, NorthSign = northSign, Predicted = true
            };
            return res;
        }

        private static PredictionCheck Missed(PredictionCheck res, string miss, double by, string why)
        {
            res.Miss = miss; res.MissBy = by; res.Why = why;
            return res;
        }
    }
}
