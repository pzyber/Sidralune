using System;
using System.Collections.Generic;
using Sidralune.Core.Mathematics;

namespace Sidralune.Devices.Guiding
{
    /// <summary>
    /// How guide pulses move the guide star, as measured by calibrating: the direction in the image in which a West pulse
    /// and a North pulse move the star, and how many pixels per second of pulse. Everything the guider needs to turn a star
    /// offset into pulses, and to tell whether the calibration still fits the equipment.
    /// </summary>
    public class GuideCalibration
    {
        /// <summary>Image angles (degrees, atan2 of y over x in image pixels) of the star's motion under West and North pulses.</summary>
        public double RaAngleDeg, DecAngleDeg;
        /// <summary>Pixels the star moves per second of West and North pulse.</summary>
        public double RaRatePxPerSec, DecRatePxPerSec;
        /// <summary>The rates predicted from the optics and the guide rates, NaN when the mount did not report its guide rates.</summary>
        public double ExpectedRaRatePxPerSec = double.NaN, ExpectedDecRatePxPerSec = double.NaN;
        /// <summary>The mount's declination and pier side when calibrated (NaN and Unknown when it could not tell).</summary>
        public double DecDeg = double.NaN;
        public PierSide PierSide = PierSide.Unknown;
        // the equipment it was made with
        public int Binning = 1;
        public double PixelSizeUm, FocalLengthMm;
        public GuideOutputKind Output;
        public string Camera = "";
        public DateTime When;
        public int RaSteps, DecSteps;
        /// <summary>What looked wrong (see the Issue constants), kept to show with the calibration.</summary>
        public List<string> Issues = new List<string>();

        public const string IssueOrthogonality = "Orthogonality", IssueRateRatio = "RateRatio", IssueFewSteps = "FewSteps", IssuePrediction = "Prediction", IssueNearPole = "NearPole";

        /// <summary>How far the two directions are from right angles (degrees).</summary>
        public double OrthogonalityErrorDeg => Math.Abs(Math.Abs(AstroMath.NormalizeDeg180(DecAngleDeg - RaAngleDeg)) - 90);

        /// <summary>The camera has turned 180° on the sky since calibrating: the mount is on the other side of the pier.</summary>
        public bool Flipped(PierSide now) => PierSide != PierSide.Unknown && now != PierSide.Unknown && now != PierSide;

        /// <summary>
        /// Unit vectors of the star's motion under West and North pulses now. After a meridian flip the camera has turned
        /// 180° on the sky, so West moves the star the other way in the image; North does too if the mount turns its Dec
        /// pulses round itself, and otherwise the two turns cancel.
        /// </summary>
        public void Directions(PierSide now, bool mountReversesDec, out double rx, out double ry, out double dx, out double dy)
        {
            double ra = RaAngleDeg, de = DecAngleDeg;
            if (Flipped(now)) { ra += 180; if (mountReversesDec) de += 180; }
            rx = Math.Cos(ra * AstroMath.Deg2Rad); ry = Math.Sin(ra * AstroMath.Deg2Rad);
            dx = Math.Cos(de * AstroMath.Deg2Rad); dy = Math.Sin(de * AstroMath.Deg2Rad);
        }

        /// <summary>
        /// Splits a star offset in the image (pixels) into its RA and Dec parts: the pixels along the West and North pulse
        /// directions. The axes need not be exactly at right angles.
        /// </summary>
        public void ToAxes(double ex, double ey, PierSide now, bool mountReversesDec, out double ra, out double dec)
        {
            double rx, ry, dx, dy;
            Directions(now, mountReversesDec, out rx, out ry, out dx, out dy);
            double det = rx * dy - dx * ry;
            if (Math.Abs(det) < 1e-3) { ra = ex * rx + ey * ry; dec = ex * dx + ey * dy; return; }
            ra = (ex * dy - dx * ey) / det;
            dec = (rx * ey - ex * ry) / det;
        }

        /// <summary>The image offset of moving ra pixels along the West direction and dec along the North one.</summary>
        public void FromAxes(double ra, double dec, PierSide now, bool mountReversesDec, out double ex, out double ey)
        {
            double rx, ry, dx, dy;
            Directions(now, mountReversesDec, out rx, out ry, out dx, out dy);
            ex = ra * rx + dec * dx; ey = ra * ry + dec * dy;
        }

        /// <summary>
        /// The RA rate at another declination: a guide pulse turns the RA axis by the same angle anywhere, which moves the
        /// sky by cos(dec) of it. Kept away from the poles, where it would go to zero.
        /// </summary>
        public double RaRateAt(double decDeg)
        {
            if (double.IsNaN(decDeg) || double.IsNaN(DecDeg)) return RaRatePxPerSec;
            double c0 = Math.Cos(AstroMath.Clamp(DecDeg, -80, 80) * AstroMath.Deg2Rad), c1 = Math.Cos(AstroMath.Clamp(decDeg, -80, 80) * AstroMath.Deg2Rad);
            return RaRatePxPerSec * c1 / c0;
        }

        /// <summary>The calibration was made with this equipment, so it can be used again.</summary>
        public bool Fits(int binning, double pixelSizeUm, double focalLengthMm, GuideOutputKind output, string camera) =>
            RaRatePxPerSec > 0 && DecRatePxPerSec > 0 && Binning == binning && Math.Abs(PixelSizeUm - pixelSizeUm) < 0.01
            && Math.Abs(FocalLengthMm - focalLengthMm) < 0.5 && Output == output && Camera == (camera ?? "");

        /// <summary>
        /// The direction (image degrees) and speed (pixels per second of pulse) of one calibration leg, from a straight-line
        /// fit of every measured position against the pulse time so far, so one poorly measured step counts for little.
        /// </summary>
        public static bool FitLeg(IList<double> pulseSeconds, IList<double> x, IList<double> y, out double angleDeg, out double ratePxPerSec)
        {
            angleDeg = ratePxPerSec = double.NaN;
            if (pulseSeconds.Count < 2) return false;
            var cx = LinAlg.PolyFit(pulseSeconds, x, 1);
            var cy = LinAlg.PolyFit(pulseSeconds, y, 1);
            if (cx == null || cy == null) return false;
            angleDeg = Math.Atan2(cy[1], cx[1]) * AstroMath.Rad2Deg;
            ratePxPerSec = Math.Sqrt(cx[1] * cx[1] + cy[1] * cy[1]);
            return ratePxPerSec > 0;
        }

        /// <summary>Notes what looks wrong with a new calibration (in Issues).</summary>
        public void Check()
        {
            Issues.Clear();
            if (OrthogonalityErrorDeg > 15) Issues.Add(IssueOrthogonality);
            if (!double.IsNaN(DecDeg) && Math.Abs(DecDeg) < 60 && RaRatePxPerSec > 0 && DecRatePxPerSec > 0)
            {
                // at the same guide rate on both axes, RA moves the star cos(dec) as fast as Dec
                double speedRatio = !double.IsNaN(ExpectedRaRatePxPerSec) && !double.IsNaN(ExpectedDecRatePxPerSec) && ExpectedDecRatePxPerSec > 0
                    ? ExpectedRaRatePxPerSec / ExpectedDecRatePxPerSec : Math.Cos(DecDeg * AstroMath.Deg2Rad);
                if (Math.Abs(RaRatePxPerSec / DecRatePxPerSec / speedRatio - 1) > 0.25) Issues.Add(IssueRateRatio);
            }
            if (RaSteps < 4 || DecSteps < 4) Issues.Add(IssueFewSteps);
            if ((!double.IsNaN(ExpectedRaRatePxPerSec) && Math.Abs(RaRatePxPerSec / ExpectedRaRatePxPerSec - 1) > 0.4)
                || (!double.IsNaN(ExpectedDecRatePxPerSec) && Math.Abs(DecRatePxPerSec / ExpectedDecRatePxPerSec - 1) > 0.4)) Issues.Add(IssuePrediction);
            if (!double.IsNaN(DecDeg) && Math.Abs(DecDeg) > 75) Issues.Add(IssueNearPole);
        }
    }
}
