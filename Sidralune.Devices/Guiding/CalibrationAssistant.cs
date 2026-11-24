using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Sidralune.Core.Mathematics;
using Sidralune.Core.Util;
using Sidralune.Devices.Automation;

namespace Sidralune.Devices.Guiding
{
    public enum CalibrationRating { Good, Acceptable, Poor }

    /// <summary>
    /// The Calibration Assistant: calibrates where a calibration is most reliable, near the meridian and the celestial
    /// equator, on the side of the meridian the mount already points to (no flip), and raised towards the pole where the
    /// equator is low. It checks the mount first, rates the calibration afterwards, and can take the mount back.
    /// </summary>
    public class CalibrationAssistant
    {
        private readonly DeviceManager _dm;
        private readonly SidraluneGuider _g;
        /// <summary>Where to calibrate: hour angle (hours, positive west) and declination (degrees).</summary>
        public double TargetHaHours = double.NaN, TargetDecDeg = double.NaN;
        /// <summary>Where the mount pointed before (RA hours, Dec degrees), NaN until it has slewed.</summary>
        public double StartRa = double.NaN, StartDec = double.NaN;
        public const double MinAltitudeDeg = 30;

        public CalibrationAssistant(DeviceManager dm, SidraluneGuider g) { _dm = dm; _g = g; }

        /// <summary>Proposes the target for where the mount points now.</summary>
        public void Propose()
        {
            var tel = _dm.Telescope;
            if (tel == null || !tel.Connected) throw new InvalidOperationException(Lang.T("No telescope connected."));
            double ha = AstroMath.NormalizeHours(tel.SiderealTime - tel.RightAscension);
            ProposeFor(ha, _dm.Site.LatitudeDeg, out TargetHaHours, out TargetDecDeg);
        }

        /// <summary>
        /// 5° from the meridian on the side it points to now (hourAngle in hours, 0..24), at the equator, or nearer the pole
        /// until the altitude there is at least 30°.
        /// </summary>
        public static void ProposeFor(double hourAngle, double latitudeDeg, out double haHours, out double decDeg)
        {
            bool west = AstroMath.NormalizeHours(hourAngle) < 12;
            haHours = west ? 5 / 15.0 : -5 / 15.0;
            double pole = latitudeDeg >= 0 ? 1 : -1;
            decDeg = 0;
            for (int i = 0; i <= 60; i++)
            {
                double d = pole * i;
                if (Altitude(haHours, d, latitudeDeg) >= MinAltitudeDeg) { decDeg = d; return; }
            }
            decDeg = pole * 60;
        }

        public static double Altitude(double haHours, double decDeg, double latDeg)
        {
            double h = haHours * 15 * AstroMath.Deg2Rad, d = decDeg * AstroMath.Deg2Rad, p = latDeg * AstroMath.Deg2Rad;
            return Math.Asin(Math.Sin(p) * Math.Sin(d) + Math.Cos(p) * Math.Cos(d) * Math.Cos(h)) * AstroMath.Rad2Deg;
        }

        /// <summary>What to know before slewing: an empty list when all is well.</summary>
        public List<string> Checks()
        {
            var list = new List<string>();
            var tel = _dm.Telescope;
            if (tel == null || !tel.Connected) { list.Add(Lang.T("No telescope connected: the assistant needs to know where the mount points and to slew it.")); return list; }
            double rate = double.NaN;
            try { rate = tel.GuideRateRa; } catch { }
            const double sidereal = 15.0410686 / 3600;
            if (double.IsNaN(rate) || rate <= 0) list.Add(Lang.T("The mount does not report its guide rate. 0.5× sidereal or faster is best."));
            else if (rate < 0.5 * sidereal - 1e-9) list.Add(Lang.T("The guide rate is {0:0.0#}× sidereal. 0.5× or faster calibrates and guides better.", rate / sidereal));
            if (_g.Settings.CalibrationStepMs > 0) list.Add(Lang.T("The calibration step is fixed at {0} ms. 0 works it out for the place and the guide rate.", _g.Settings.CalibrationStepMs));
            return list;
        }

        /// <summary>
        /// Slews to the target (the last degree from the south, so Dec's gears are taken up northwards), calibrates, and
        /// stops guiding.
        /// </summary>
        public async Task Run(CancellationToken ct)
        {
            var tel = _dm.Telescope;
            if (tel == null || !tel.Connected) throw new InvalidOperationException(Lang.T("No telescope connected."));
            if (double.IsNaN(TargetHaHours) || double.IsNaN(TargetDecDeg)) Propose();
            StartRa = tel.RightAscension; StartDec = tel.Declination;
            _g.StopGuiding();
            double ra = AstroMath.NormalizeHours(tel.SiderealTime - TargetHaHours);
            Log.Info("Guider", string.Format("Calibration Assistant: slewing to hour angle {0:0.00} h, Dec {1:0.0}°", TargetHaHours, TargetDecDeg));
            await Centering.Slew(tel, ra, Math.Max(-89, TargetDecDeg - 1), ct);
            await Centering.Slew(tel, AstroMath.NormalizeHours(tel.SiderealTime - TargetHaHours), TargetDecDeg, ct);
            try { await _g.StartGuiding(true, ct); }
            finally { _g.StopGuiding(); }
        }

        /// <summary>Slews back to where the mount pointed before the assistant moved it.</summary>
        public async Task Return(CancellationToken ct)
        {
            var tel = _dm.Telescope;
            if (tel == null || !tel.Connected) throw new InvalidOperationException(Lang.T("No telescope connected."));
            if (double.IsNaN(StartRa)) throw new InvalidOperationException(Lang.T("The assistant has not moved the mount."));
            _g.StopGuiding();
            await Centering.Slew(tel, StartRa, StartDec, ct);
        }

        /// <summary>
        /// How good a calibration is from what looked wrong with it (its Issues, which say why): poor when the axes are far
        /// from square or their rates do not fit the declination, acceptable with only the lesser issues, good with none.
        /// </summary>
        public static CalibrationRating Rate(GuideCalibration c)
        {
            if (c == null) return CalibrationRating.Poor;
            if (c.Issues.Contains(GuideCalibration.IssueOrthogonality) || c.Issues.Contains(GuideCalibration.IssueRateRatio)) return CalibrationRating.Poor;
            return c.Issues.Count > 0 ? CalibrationRating.Acceptable : CalibrationRating.Good;
        }
    }
}
