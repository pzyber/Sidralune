using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Sidralune.Core;
using Sidralune.Core.Mathematics;
using Sidralune.Core.Time;
using Sidralune.Core.Util;
using Sidralune.Imaging;
using Sidralune.Imaging.Analysis;
using Sidralune.Imaging.Registration;
using Sidralune.Science.Astrometry;

namespace Sidralune.Devices.Automation
{
    /// <summary>What one drift correction found and did.</summary>
    public class DriftStep
    {
        /// <summary>How far the frame was from where it should have been (arcsec east and north on the sky); NaN when not measured.</summary>
        public double DriftEast = double.NaN, DriftNorth = double.NaN;
        /// <summary>The move made (arcsec east and north), dither included.</summary>
        public double MoveEast, MoveNorth;
        /// <summary>"reference" (the first frame, solved), "registration", "plate solve", or null when nothing could be measured.</summary>
        public string Method;
        public int Matches;
        /// <summary>"pulses", "slew", or null when no move was needed.</summary>
        public string MovedBy;
        public string Note;
        public double DriftArcsec => Math.Sqrt(DriftEast * DriftEast + DriftNorth * DriftNorth);
    }

    /// <summary>
    /// Drift correction without a guide camera: the first light frame is the reference, plate-solved once for its scale and
    /// orientation. Each later frame is registered against it (Deep sky's star matching), which says where its centre
    /// points; when registration fails, the frame is plate-solved instead. Between frames the mount is moved back by the
    /// difference, through guide pulses (their Dec direction checked against the mount's own coordinates) or, for a
    /// large offset or a mount without pulse guiding, a small slew. A dither is added to the same move.
    /// </summary>
    public class DriftCorrector
    {
        public DeviceManager Devices;
        public Action<string> Log;
        /// <summary>A drift under this (arcsec) is left alone; the longest pulse (ms) before a slew is used instead.</summary>
        public double MinMoveArcsec = 1, MaxPulseMs = 10000;
        /// <summary>How long the mount is given to settle after a move (s).</summary>
        public double SettleSeconds = 1;
        /// <summary>Random numbers for dithers (a seed for tests).</summary>
        public Random Random = new Random();

        private List<DetectedStar> _refStars;
        private Wcs _refWcs;
        private int _w, _h;
        private double _refRa = double.NaN, _refDec = double.NaN;     // the reference's centre on the sky (deg)
        private double _mountRa = double.NaN, _mountDec = double.NaN; // where the mount said it pointed (hours, deg)
        private double _ditherE, _ditherN;                            // where the frames are meant to be, from the reference (arcsec)
        private int _decSign = 1;                                     // +1: a North pulse moves the mount north here
        private int _waitFrames;                                      // frames left before a failed reference is tried again
        /// <summary>After a reference that could not be solved, how many frames pass before another is tried; how many stars a reference needs.</summary>
        public int RetryAfterFrames = 5, MinStars = 10;

        /// <summary>The reference is solved: drift can be measured on the sky.</summary>
        public bool HasReference => _refWcs != null;
        public DriftStep Last { get; private set; }

        /// <summary>Forgets the reference: the next light frame becomes the new one (after a slew to another field).</summary>
        public void Reset()
        {
            _refStars = null; _refWcs = null;
            _refRa = _refDec = _mountRa = _mountDec = double.NaN;
            _ditherE = _ditherN = 0;
            // a reference that failed on the field before says nothing about this one
            _waitFrames = 0;
        }

        private void Say(string s) { Log?.Invoke(s); }

        /// <summary>
        /// After a light frame: measures how far it drifted from the reference (the first frame of the field) and moves the
        /// mount back, adding a dither of up to ditherPx imaging-camera pixels when asked. Waits for the move to settle.
        /// </summary>
        public async Task<DriftStep> AfterFrame(AstroImage frame, double ditherPx, CancellationToken ct)
        {
            var tel = Devices.Telescope;
            if (tel == null || !tel.Connected) throw new InvalidOperationException(Lang.T("No telescope connected."));
            var step = new DriftStep();
            Last = step;
            // another field (a slew, a new target), or another frame size: a new reference
            // (also while waiting to try a reference again: _mountRa is where the one that failed was taken)
            if ((_refStars != null && (frame.Width != _w || frame.Height != _h)) || !MountNear(tel, _mountRa, _mountDec)) Reset();
            // a reference that could not be solved (the Moon, a planet, clouds) is not tried on every frame
            if (_refWcs == null && _waitFrames > 0) { _waitFrames--; step.Note = "waiting before trying a reference again"; return await DitherOnly(step, frame, tel, ditherPx, ct); }
            var lum = frame.Luminance();
            var stars = await Task.Run(() => StarDetector.Detect(lum, frame.Width, frame.Height, new StarDetectionOptions { MaxStars = 200 }), ct);
            double ra, dec;
            if (_refStars == null)
            {
                // the reference: its stars, and a plate solution for its scale and orientation
                if (stars.Count < MinStars)
                {
                    _refStars = null; _waitFrames = RetryAfterFrames;
                    // where it was tried: a slew to another field ends the wait
                    _mountRa = tel.RightAscension; _mountDec = tel.Declination;
                    step.Note = "too few stars for a reference frame";
                    Say("Drift correction: " + step.Note);
                    return await DitherOnly(step, frame, tel, ditherPx, ct);
                }
                _refStars = stars; _w = frame.Width; _h = frame.Height;
                _mountRa = tel.RightAscension; _mountDec = tel.Declination;
                _ditherE = _ditherN = 0;
                var wcs = await SolveFrame(frame, tel, ct);
                if (wcs == null)
                {
                    _waitFrames = RetryAfterFrames;
                    step.Note = "the reference frame could not be plate-solved";
                    Say(string.Format(CultureInfo.InvariantCulture, "Drift correction: {0}; trying again after {1} frames", step.Note, RetryAfterFrames));
                    return await DitherOnly(step, frame, tel, ditherPx, ct);
                }
                _refWcs = wcs;
                _refWcs.Centre(out _refRa, out _refDec);
                step.Method = "reference";
                step.DriftEast = step.DriftNorth = 0;
                Say(string.Format(CultureInfo.InvariantCulture, "Drift correction: reference frame, {0} stars, {1:0.00}\"/px", stars.Count, _refWcs.PixelScale));
            }
            else
            {
                if (_refWcs == null)
                {
                    // the reference could not be solved before: this frame becomes the reference if it can be
                    Reset();
                    return await AfterFrame(frame, ditherPx, ct);
                }
                // where this frame's centre points: registered onto the reference, else plate-solved
                var match = stars.Count >= 6 ? StarMatcher.Match(_refStars, stars, TransformModel.Similarity) : null;
                if (match != null && match.Success && match.Matches >= 6)
                {
                    double u, v;
                    match.Map((frame.Width - 1) / 2.0, (frame.Height - 1) / 2.0, out u, out v);
                    _refWcs.PixelToWorld(u, v, out ra, out dec);
                    step.Method = "registration"; step.Matches = match.Matches;
                }
                else
                {
                    var wcs = await SolveFrame(frame, tel, ct);
                    if (wcs == null) { step.Note = "the frame could neither be registered nor plate-solved"; Say("Drift correction: " + step.Note); return await DitherOnly(step, frame, tel, ditherPx, ct); }
                    wcs.Centre(out ra, out dec);
                    step.Method = "plate solve";
                }
                double cosd = Math.Cos(_refDec * AstroMath.Deg2Rad);
                double e = AstroMath.NormalizeDeg180(ra - _refRa) * cosd * 3600, n = (dec - _refDec) * 3600;
                step.DriftEast = e - _ditherE; step.DriftNorth = n - _ditherN;
            }

            // where the frame points from the reference, and where it should point: the dither place, a new one when asked
            double pointE = step.DriftEast + _ditherE, pointN = step.DriftNorth + _ditherN;
            double scale = _refWcs.PixelScale;
            if (ditherPx > 0)
            {
                _ditherE = (2 * Random.NextDouble() - 1) * ditherPx * scale;
                _ditherN = (2 * Random.NextDouble() - 1) * ditherPx * scale;
            }
            double moveE = _ditherE - pointE, moveN = _ditherN - pointN;
            if (ditherPx <= 0 && Math.Sqrt(moveE * moveE + moveN * moveN) < MinMoveArcsec)
            {
                if (step.Method != "reference")
                    Say(string.Format(CultureInfo.InvariantCulture, "Drift {0:0.0}\" ({1}): no correction needed", step.DriftArcsec, step.Method));
                return step;
            }
            await MoveBy(step, tel, moveE, moveN, ct);
            Say(string.Format(CultureInfo.InvariantCulture, "Drift {0:0.0}\" E, {1:0.0}\" N ({2}{3}): moved {4:0.0}\" E, {5:0.0}\" N by {6}{7}",
                step.DriftEast, step.DriftNorth, step.Method, step.Matches > 0 ? ", " + step.Matches + " stars" : "", moveE, moveN, step.MovedBy, ditherPx > 0 ? ", with a dither" : ""));
            return step;
        }

        /// <summary>
        /// No drift measured (no reference yet, or a frame that could not be measured): a dither asked for is still made,
        /// from the last dither place to a new one, so the frames are dithered and stay within the dither's reach.
        /// </summary>
        private async Task<DriftStep> DitherOnly(DriftStep step, AstroImage frame, ITelescope tel, double ditherPx, CancellationToken ct)
        {
            if (ditherPx <= 0) return step;
            double scale = _refWcs != null ? _refWcs.PixelScale : FrameScale(frame);
            if (!(scale > 0)) { Say("Drift correction: no dither, the camera's scale is not known"); return step; }
            double e = (2 * Random.NextDouble() - 1) * ditherPx * scale, n = (2 * Random.NextDouble() - 1) * ditherPx * scale;
            double moveE = e - _ditherE, moveN = n - _ditherN;
            _ditherE = e; _ditherN = n;
            await MoveBy(step, tel, moveE, moveN, ct);
            Say(string.Format(CultureInfo.InvariantCulture, "Drift correction: no drift measured; dithered {0:0.0}\" E, {1:0.0}\" N by {2}", moveE, moveN, step.MovedBy));
            return step;
        }

        /// <summary>The imaging camera's scale for a frame (arcsec per pixel), from its pixel size and the focal length; NaN when not known.</summary>
        private double FrameScale(AstroImage frame)
        {
            try
            {
                var cam = Devices.Camera;
                double bin = frame.Header.GetDouble("XBINNING", 1);
                double px = cam != null ? cam.PixelSizeX : frame.Header.GetDouble("XPIXSZ", double.NaN) / Math.Max(1, bin);
                return px > 0 && Devices.FocalLengthMm > 0 ? 206.265 * px * Math.Max(1, bin) / Devices.FocalLengthMm : double.NaN;
            }
            catch { return double.NaN; }
        }

        /// <summary>
        /// Moves the mount by an offset on the sky (arcsec east and north): guide pulses when the mount takes them and
        /// knows its guide rates, else (or for more than MaxPulseMs of pulsing) a small slew. A Dec pulse that moved the
        /// mount the wrong way (North moving south, as on one side of the pier for many mounts) is taken back and sent the
        /// other way, and the direction is kept for next time.
        /// </summary>
        private async Task MoveBy(DriftStep step, ITelescope tel, double moveE, double moveN, CancellationToken ct)
        {
            step.MoveEast = moveE; step.MoveNorth = moveN;
            double dec = tel.Declination, cosd = Math.Max(0.05, Math.Cos(dec * AstroMath.Deg2Rad));
            double dRa = moveE / 3600 / cosd, dDec = moveN / 3600;
            double gRa = double.NaN, gDec = double.NaN;
            bool canPulse = false;
            try { canPulse = tel.CanPulseGuide; gRa = tel.GuideRateRa; gDec = tel.GuideRateDec; } catch { }
            int raMs = gRa > 0 ? (int)Math.Round(Math.Abs(dRa) / gRa * 1000) : -1;
            int decMs = gDec > 0 ? (int)Math.Round(Math.Abs(dDec) / gDec * 1000) : -1;
            if (canPulse && raMs >= 0 && decMs >= 0 && raMs <= MaxPulseMs && decMs <= MaxPulseMs)
            {
                step.MovedBy = "pulses";
                if (raMs > 0) await Pulse(tel, dRa > 0 ? GuideDirection.East : GuideDirection.West, raMs, ct);
                if (decMs > 0)
                {
                    double before = tel.Declination;
                    var dir = dDec * _decSign > 0 ? GuideDirection.North : GuideDirection.South;
                    await Pulse(tel, dir, decMs, ct);
                    double moved = tel.Declination - before;
                    if (Math.Abs(moved) * 3600 > 0.3 * Math.Abs(moveN) && Math.Sign(moved) != Math.Sign(dDec))
                    {
                        _decSign = -_decSign;
                        Say("Drift correction: Dec pulses move the other way here; turned round");
                        await Pulse(tel, dir == GuideDirection.North ? GuideDirection.South : GuideDirection.North, 2 * decMs, ct);
                    }
                }
            }
            else
            {
                step.MovedBy = "slew";
                await Centering.Slew(tel, AstroMath.NormalizeHours(tel.RightAscension + dRa / 15), AstroMath.Clamp(tel.Declination + dDec, -90, 90), ct);
            }
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, SettleSeconds)), ct);
        }

        private static async Task Pulse(ITelescope tel, GuideDirection dir, int ms, CancellationToken ct)
        {
            tel.PulseGuide(dir, ms);
            await Task.Delay(ms, ct);
            var until = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < until)
            {
                bool busy;
                try { busy = tel.IsPulseGuiding; } catch { busy = false; }
                if (!busy) break;
                await Task.Delay(50, ct);
            }
        }

        /// <summary>Plate-solves an imaging frame near the mount's position, with the camera's scale; null when it fails.</summary>
        private async Task<Wcs> SolveFrame(AstroImage frame, ITelescope tel, CancellationToken ct)
        {
            try
            {
                var cam = Devices.Camera;
                var ctx = new SkyContext(Devices.Site, AstroTime.FromDateTime(DateTime.UtcNow));
                var c = ctx.ApparentToIcrs(tel.RightAscension * AstroMath.Hour2Rad, tel.Declination * AstroMath.Deg2Rad);
                var opt = new SolveOptions { HintRa = c.RaHours * 15, HintDec = c.DecDeg, SearchRadius = 2, AllowBlind = false };
                double scale = FrameScale(frame);
                if (scale > 0) opt.ScaleHint = scale;
                Devices.PrepareSolve(opt, cam);
                var sr = await Task.Run(() => PlateSolver.Solve(frame, opt, null, ct), ct);
                return sr.Success ? sr.Wcs : null;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Core.Util.Log.Warn("Drift", "Plate solve: " + ex.Message); return null; }
        }

        private static bool MountNear(ITelescope tel, double raHours, double decDeg)
        {
            if (double.IsNaN(raHours)) return true;
            double d = Math.Sqrt(Math.Pow(AstroMath.NormalizeDeg180((tel.RightAscension - raHours) * 15) * Math.Cos(decDeg * AstroMath.Deg2Rad), 2) + Math.Pow(tel.Declination - decDeg, 2));
            return d <= 0.25;
        }
    }
}
