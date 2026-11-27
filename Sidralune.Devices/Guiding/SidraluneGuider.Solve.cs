using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Sidralune.Core;
using Sidralune.Core.Catalogs;
using Sidralune.Core.Mathematics;
using Sidralune.Core.Time;
using Sidralune.Core.Util;
using Sidralune.Imaging;
using Sidralune.Science.Astrometry;

namespace Sidralune.Devices.Guiding
{
    /// <summary>The guide camera's scale and angle on the sky from a solved guide frame, with the equipment it was solved with.</summary>
    public class GuideFrameSolve
    {
        /// <summary>Arcseconds per (binned) pixel, and the angle of the frame's up direction from north through east (degrees).</summary>
        public double ScaleArcsec, AngleDeg;
        /// <summary>The image is mirrored on the sky (an odd number of reflections in the guide scope's light path).</summary>
        public bool Mirrored;
        public int Binning = 1;
        public double PixelSizeUm, FocalLengthMm;
        public string Camera = "";

        public bool Fits(int binning, double pixelSizeUm, double focalLengthMm, string camera) =>
            ScaleArcsec > 0 && Binning == binning && Math.Abs(PixelSizeUm - pixelSizeUm) < 0.01 && Math.Abs(FocalLengthMm - focalLengthMm) < 0.5 && Camera == (camera ?? "");

        /// <summary>The guide scope's focal length the solve gives (mm), from the pixel size and the binning.</summary>
        public double SolvedFocalMm => ScaleArcsec > 0 && PixelSizeUm > 0 ? 206.265 * PixelSizeUm * Math.Max(1, Binning) / ScaleArcsec : double.NaN;
    }

    /// <summary>Judging guide stars by the catalogue, once a guide frame has been plate-solved.</summary>
    public static class GuideStarCatalog
    {
        /// <summary>
        /// A star at (x, y) suits guiding by the catalogue: it is a catalogue star (so not a hot pixel, a galaxy or a
        /// cosmic ray), no other catalogue star with a tenth of its brightness or more (2.5 magnitudes) lies within its
        /// search box (an unresolved double pulls the measurement), and it is not known to vary or to be double.
        /// </summary>
        public static bool Suits(Wcs wcs, IList<RefStar> stars, double x, double y, int searchRadius, Func<double, double, double, bool> varies)
        {
            if (wcs == null || stars == null) return false;
            double ra, dec;
            wcs.PixelToWorld(x, y, out ra, out dec);
            double scale = wcs.PixelScale;
            var match = Nearest(stars, ra, dec, MatchArcsec(scale));
            if (match == null) return false;
            double box = (searchRadius + 3) * scale;
            foreach (var s in stars)
                if (s != match && s.Mag < match.Mag + 2.5 && SeparationArcsec(s.Ra, s.Dec, match.Ra, match.Dec) <= box) return false;
            return varies == null || !varies(match.Ra, match.Dec, MatchArcsec(scale));
        }

        /// <summary>How far a star in the frame may be from its catalogue position and still be that star (arcsec).</summary>
        public static double MatchArcsec(double scale) => Math.Max(2 * scale, 4);

        /// <summary>The catalogue star nearest a place (degrees), within maxArcsec; null when there is none.</summary>
        public static RefStar Nearest(IList<RefStar> stars, double raDeg, double decDeg, double maxArcsec)
        {
            RefStar best = null;
            double bestSep = maxArcsec;
            foreach (var s in stars)
            {
                double d = SeparationArcsec(raDeg, decDeg, s.Ra, s.Dec);
                if (d <= bestSep) { bestSep = d; best = s; }
            }
            return best;
        }

        /// <summary>Known to vary or to be double in the local star catalogue, within radiusArcsec of a place (degrees).</summary>
        public static bool KnownToVary(double raDeg, double decDeg, double radiusArcsec)
        {
            var near = new List<Star>();
            try { CatalogManager.Instance.Stars.Cone(raDeg * AstroMath.Deg2Rad, decDeg * AstroMath.Deg2Rad, radiusArcsec / 3600 * AstroMath.Deg2Rad, 99, near); }
            catch { return false; }
            return near.Any(s => !string.IsNullOrEmpty(s.VariableType) || s.Type == ObjectType.VariableStar || s.Type == ObjectType.DoubleStar);
        }

        private static double SeparationArcsec(double ra1, double dec1, double ra2, double dec2)
        {
            double dra = AstroMath.NormalizeDeg180(ra1 - ra2) * Math.Cos(0.5 * (dec1 + dec2) * AstroMath.Deg2Rad), ddec = dec1 - dec2;
            return Math.Sqrt(dra * dra + ddec * ddec) * 3600;
        }
    }

    public partial class SidraluneGuider
    {
        /// <summary>A solved guide frame at the present pointing, and the catalogue's stars in it.</summary>
        private class SkyField
        {
            public Wcs Wcs;
            public List<RefStar> Stars;
            /// <summary>Where the mount said it pointed (hours, degrees; NaN without a mount).</summary>
            public double MountRa = double.NaN, MountDec = double.NaN;
            /// <summary>The binning and camera the frame was taken with: its pixels mean nothing with others.</summary>
            public int Binning;
            public string Camera;
        }

        // the last guide frame solved at this pointing (null after a slew or a rotator turn), and the guide star's place on
        // the sky (degrees; NaN when it is not known)
        private volatile SkyField _sky;
        // where the mount pointed when a solve before guiding was last tried: a failure is not tried again at every start there
        private double _triedRa = double.NaN, _triedDec = double.NaN;
        // where the mount pointed and the rotator stood when frames last stopped: the solve is kept for the next start if
        // neither has moved since
        private bool _stopNoted;
        private double _stopRa = double.NaN, _stopDec = double.NaN, _stopRotator = double.NaN;
        private double _starRa = double.NaN, _starDec = double.NaN;
        // when the guide star was lost, and when a solve may next look for it
        private DateTime _lostSince = DateTime.MaxValue, _nextFind = DateTime.MinValue;
        // another star took over or the star's brightness jumped: a solve checks it is still the star guided on
        private volatile bool _checkStar;
        // frames to wait before the check solve, and the guide star's brightness before the event (to know it again)
        private int _checkWait;
        private double _eventFlux = double.NaN, _reacquireFlux = double.NaN;
        // the star was brought back: the next frames take the star at the lock position as the guide star again
        private volatile bool _reacquire;
        private int _reacquireFrames;

        /// <summary>A lost star is looked for by solving once it has been gone this long (s), and again this often (s).</summary>
        public double FindLostAfterSeconds = 10, FindLostEverySeconds = 30;
        /// <summary>The longest guide pulse (ms, each axis) a found star is brought back with: farther needs a slew.</summary>
        public const int MaxBringBackMs = 120000;

        /// <summary>A guide frame was solved, or could not be (SolveMessage says why).</summary>
        public event Action Solved;
        /// <summary>Why the last guide frame could not be solved; null when it was.</summary>
        public string SolveMessage { get; private set; }
        /// <summary>The guide star's place on the sky is known, so it can be found again by solving when it is lost.</summary>
        public bool StarOnSkyKnown => SkyStillHere() && !double.IsNaN(_starRa);

        /// <summary>
        /// The solve is still of where the mount points: a slew (even one the guider did not see, while it was not
        /// running) makes it forget the solve and the star's place. Guiding and dithers move the mount far less.
        /// </summary>
        private bool SkyStillHere()
        {
            var sky = _sky;
            if (sky == null) return true;
            if (sky.Binning != Math.Max(1, Settings.Binning) || sky.Camera != CameraId()) { ForgetSky(); return false; }
            if (double.IsNaN(sky.MountRa)) return true;
            if (MountNear(sky.MountRa, sky.MountDec)) return true;
            ForgetSky();
            return false;
        }

        /// <summary>The mount points within a quarter of a degree of a place (hours, degrees), or cannot say.</summary>
        private bool MountNear(double raHours, double decDeg)
        {
            double ra = MountRa(), dec = MountDeclination();
            if (double.IsNaN(ra) || double.IsNaN(dec) || double.IsNaN(raHours)) return true;
            double d = Math.Sqrt(Math.Pow(AstroMath.NormalizeDeg180((ra - raHours) * 15) * Math.Cos(dec * AstroMath.Deg2Rad), 2) + Math.Pow(dec - decDeg, 2));
            return d <= 0.25;
        }

        /// <summary>The last solve, while it fits the equipment in use; null otherwise.</summary>
        public GuideFrameSolve CurrentSolve
        {
            get
            {
                var s = Settings.LastSolve;
                return s != null && s.Fits(Math.Max(1, Settings.Binning), CameraPixelUm(), Settings.FocalLengthMm, CameraId()) ? s : null;
            }
        }

        /// <summary>The guide camera, as prepared for the session, or as connected before one.</summary>
        private string CameraId()
        {
            if (!string.IsNullOrEmpty(_cameraId)) return _cameraId;
            var cam = _dm.GuideCamera;
            return cam != null && cam.Info != null ? cam.Info.Protocol + " " + cam.Info.Address : "";
        }

        private double CameraPixelUm()
        {
            double px = _pixelUm > 0 ? _pixelUm : Settings.Calibration != null ? Settings.Calibration.PixelSizeUm : 0;
            if (px <= 0 && _dm.GuideCamera != null) try { px = _dm.GuideCamera.PixelSizeX; } catch { }
            return px;
        }

        /// <summary>With a solved frame here: stars the catalogue says suit guiding are chosen first.</summary>
        private Func<double, double, bool> CataloguePreference()
        {
            var sky = _sky;
            if (sky == null || !Settings.SolveGuideFrames || !SkyStillHere()) return null;
            int r = Settings.SearchRadius;
            return (x, y) => GuideStarCatalog.Suits(sky.Wcs, sky.Stars, x, y, r, GuideStarCatalog.KnownToVary);
        }

        /// <summary>The sky has moved (a slew) or turned (a rotator): the solve and the star's place on the sky are forgotten.</summary>
        private void ForgetSky()
        {
            _sky = null;
            _triedRa = _triedDec = double.NaN;
            _stopNoted = false;
            _checkStar = false;
            _reacquire = false;
            _starRa = _starDec = double.NaN;
        }

        /// <summary>
        /// Frames stopped: the guide star goes with the session, the solve stays, with where the mount pointed and the
        /// rotator stood, so a start at the same place (around autofocus, a program stopping and starting guiding) does
        /// not take a frame and solve again.
        /// </summary>
        private void KeepSkyAtStop()
        {
            _checkStar = false;
            _reacquire = false;
            _starRa = _starDec = double.NaN;
            _stopRa = MountRa(); _stopDec = MountDeclination(); _stopRotator = RotatorAngle();
            _stopNoted = true;
        }

        /// <summary>
        /// Frames start again: the solve kept at the stop is forgotten if the mount has moved since by more than a few
        /// guide pixels (centring stops guiding for its slews), the rotator has turned, or either cannot be told.
        /// </summary>
        private void CheckSkyAfterStop()
        {
            if (!_stopNoted) return;
            _stopNoted = false;
            var sky = _sky;
            if (sky == null && double.IsNaN(_triedRa)) return;
            double ra = MountRa(), dec = MountDeclination(), rot = RotatorAngle();
            bool moved = double.IsNaN(ra) || double.IsNaN(dec) || double.IsNaN(_stopRa) || double.IsNaN(_stopDec);
            if (!moved)
            {
                double arcsec = 3600 * Math.Sqrt(Math.Pow(AstroMath.NormalizeDeg180((ra - _stopRa) * 15) * Math.Cos(dec * AstroMath.Deg2Rad), 2) + Math.Pow(dec - _stopDec, 2));
                double scale = sky != null ? sky.Wcs.PixelScale : PixelScale;
                moved = arcsec > Math.Max(5, 3 * (scale > 0 ? scale : 1));
            }
            if (!double.IsNaN(_stopRotator) || !double.IsNaN(rot))
                moved |= double.IsNaN(_stopRotator) || double.IsNaN(rot) || Math.Abs(AstroMath.NormalizeDeg180(rot - _stopRotator)) > 0.2;
            if (moved) ForgetSky();
        }

        /// <summary>
        /// Plate-solves a guide frame near the last solve here, or else the mount's position, with the guide camera's scale.
        /// The solution gives the camera's scale and angle (kept in the settings) and the catalogue's stars in the frame.
        /// </summary>
        public async Task<Wcs> SolveFrame(AstroImage frame, CancellationToken ct)
        {
            if (frame == null) throw new InvalidOperationException(Lang.T("No guide frame yet."));
            double ra, dec;
            var sky = _sky;
            if (sky != null) sky.Wcs.Centre(out ra, out dec);
            else
            {
                var tel = _dm.Telescope;
                if (tel == null || !tel.Connected) throw new InvalidOperationException(Lang.T("Solving a guide frame needs the mount's position: no telescope connected."));
                var ctx = new SkyContext(_dm.Site, AstroTime.FromDateTime(DateTime.UtcNow));
                var c = ctx.ApparentToIcrs(tel.RightAscension * AstroMath.Hour2Rad, tel.Declination * AstroMath.Deg2Rad);
                ra = c.RaHours * 15; dec = c.DecDeg;
            }
            var opt = new SolveOptions { HintRa = ra, HintDec = dec, SearchRadius = 3, ScaleHint = PixelScale, ScaleTolerance = 0.3, AllowBlind = false };
            _dm.PrepareSolve(opt, _dm.GuideCamera);
            var sr = await Task.Run(() => PlateSolver.Solve(frame, opt, null, ct), ct);
            if (!sr.Success || sr.Wcs == null)
                throw new InvalidOperationException(Lang.T("The guide frame could not be plate-solved.") + (string.IsNullOrEmpty(sr.Message) ? "" : " " + sr.Message));
            RememberSky(sr.Wcs, opt.Catalog);
            return sr.Wcs;
        }

        private void RememberSky(Wcs wcs, IStarCatalogProvider catalog)
        {
            double ra, dec;
            wcs.Centre(out ra, out dec);
            double radius = 0.55 * Math.Sqrt((double)wcs.ImageWidth * wcs.ImageWidth + (double)wcs.ImageHeight * wcs.ImageHeight) * wcs.PixelScale / 3600;
            List<RefStar> stars;
            try { stars = catalog.Cone(ra, dec, radius, 15, 5000); }
            catch (Exception ex) { stars = new List<RefStar>(); Log.Warn("Guider", "No catalogue stars for the guide frame: " + ex.Message); }
            _sky = new SkyField { Wcs = wcs, Stars = stars, MountRa = MountRa(), MountDec = MountDeclination(), Binning = Math.Max(1, Settings.Binning), Camera = CameraId() };
            // of where the mount points now: nothing from a stop before to check
            _stopNoted = false;
            var s = new GuideFrameSolve
            {
                ScaleArcsec = wcs.PixelScale, AngleDeg = wcs.RotationDeg, Mirrored = wcs.Determinant > 0,
                Binning = Math.Max(1, Settings.Binning), PixelSizeUm = CameraPixelUm(), FocalLengthMm = Settings.FocalLengthMm, Camera = CameraId()
            };
            // written only when it says something new: a solve every 30 s while a star is lost would otherwise rewrite
            // guider.json from the guide loop again and again
            var old = Settings.LastSolve;
            bool same = old != null && old.Fits(s.Binning, s.PixelSizeUm, s.FocalLengthMm, s.Camera) && old.Mirrored == s.Mirrored
                && Math.Abs(old.ScaleArcsec / s.ScaleArcsec - 1) < 0.002 && Math.Abs(AstroMath.NormalizeDeg180(old.AngleDeg - s.AngleDeg)) < 0.2;
            if (!same)
            {
                Settings.LastSolve = s;
                SaveSettings();
            }
            SolveMessage = null;
            string what = string.Format(CultureInfo.InvariantCulture, "{0:0.000}\"/px, angle {1:0.0}, focal length {2:0} mm", s.ScaleArcsec, s.AngleDeg, s.SolvedFocalMm);
            Log.Info("Guider", "Guide frame solved: " + what);
            _log.Event("Plate solved", what);
            Solved?.Invoke();
        }

        /// <summary>Where a guide star is on the sky: its catalogue position when it is a catalogue star, else the solve's.</summary>
        private void RememberStarOnSky(GuideStarInfo star)
        {
            var sky = _sky;
            if (sky == null || star == null || !Settings.SolveGuideFrames) { _starRa = _starDec = double.NaN; return; }
            double ra, dec;
            sky.Wcs.PixelToWorld(star.X, star.Y, out ra, out dec);
            var match = GuideStarCatalog.Nearest(sky.Stars, ra, dec, GuideStarCatalog.MatchArcsec(sky.Wcs.PixelScale));
            if (match != null) { ra = match.Ra; dec = match.Dec; }
            _starRa = ra; _starDec = dec;
        }

        /// <summary>The guide star's place on the sky moved by an offset in the image: another star took over.</summary>
        private void ShiftStarOnSky(double dx, double dy)
        {
            var sky = _sky;
            double x, y;
            if (sky == null || !StarOnSkyKnown || !sky.Wcs.WorldToPixel(_starRa, _starDec, out x, out y)) { _starRa = _starDec = double.NaN; return; }
            RememberStarOnSky(new GuideStarInfo { X = x + dx, Y = y + dy });
        }

        /// <summary>
        /// Before choosing a star and calibrating: a fresh guide frame is solved, unless one was solved or tried at this
        /// pointing. A failure is only noted: guiding goes on without the catalogue.
        /// </summary>
        private async Task SolveBeforeGuiding(CancellationToken ct)
        {
            SkyStillHere();
            if (!Settings.SolveGuideFrames || _sky != null) return;
            var tel = _dm.Telescope;
            if (tel == null || !tel.Connected)
            {
                // nothing to solve with: said once, not at every start, and tried once a mount is connected
                string why = Lang.T("Solving a guide frame needs the mount's position: no telescope connected.");
                if (SolveMessage != why) NoteSolveFailed(why);
                return;
            }
            if (!double.IsNaN(_triedRa) && MountNear(_triedRa, _triedDec)) return;
            _triedRa = MountRa(); _triedDec = MountDeclination();
            try
            {
                var f = await StaticPolarAlignment.NextFrame(this, DateTime.UtcNow, ct);
                await SolveFrame(f, ct);
                if (Star != null && !_lost) RememberStarOnSky(Star);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { NoteSolveFailed(ex.Message); }
        }

        private void NoteSolveFailed(string why)
        {
            SolveMessage = why;
            Log.Warn("Guider", "Guide frame not solved: " + why);
            _log.Event("Plate solve", "failed: " + why);
            Solved?.Invoke();
        }

        /// <summary>
        /// Time to solve for the guide star, its place on the sky being known: it has been lost long enough, or another
        /// star took over or its brightness jumped (a knock can put a different star in the search box).
        /// </summary>
        private bool FindLostStarDue()
        {
            var now = DateTime.UtcNow;
            bool lostLong = _lost && (now - _lostSince).TotalSeconds >= FindLostAfterSeconds;
            if (!_checkStar && !lostLong) return false;
            if (!lostLong && _checkWait > 0) { _checkWait--; return false; }
            if (_phase != Phase.Guiding || _paused || !Settings.SolveGuideFrames || !StarOnSkyKnown || Settings.Calibration == null) { _checkStar = false; return false; }
            return now >= _nextFind;
        }

        /// <summary>
        /// A lost guide star found again by solving the frame: its known place on the sky gives where it is now, even far
        /// beyond the search box (a bump, a gust, clouds the mount drifted under), and guide pulses bring it back to the
        /// lock position, where guiding finds it. A star still at the lock position (behind a cloud) is waited for.
        /// </summary>
        private async Task FindLostStar(CancellationToken ct)
        {
            _nextFind = DateTime.UtcNow.AddSeconds(FindLostEverySeconds);
            _checkStar = false;
            bool lost = _lost;
            var cal = Settings.Calibration;
            Wcs wcs;
            try { wcs = await SolveFrame(LastFrame, ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log.Info("Guider", "Looking for the lost guide star: " + ex.Message);
                _log.Event("Plate solve", "lost star: " + ex.Message);
                return;
            }
            double sx, sy;
            if (!wcs.WorldToPixel(_starRa, _starDec, out sx, out sy)) return;
            double dx, dy;
            lock (_lock) { dx = sx - LockX; dy = sy - LockY; }
            if (double.IsNaN(dx) || double.IsNaN(dy)) return;
            if (Math.Sqrt(dx * dx + dy * dy) <= Settings.SearchRadius)
            {
                _log.Event("Plate solved", lost ? "the lost star is at the lock position: waiting for it" : "the guide star is where it should be");
                return;
            }
            double raPx, decPx;
            cal.ToAxes(dx, dy, MountPierSide(), Settings.MountReversesDecAfterFlip, out raPx, out decPx);
            double raRate = cal.RaRateAt(MountDeclination()), decRate = cal.DecRatePxPerSec;
            int raMs = raRate > 0 ? (int)Math.Round(Math.Abs(raPx) / raRate * 1000) : 0;
            int decMs = decRate > 0 ? (int)Math.Round(Math.Abs(decPx) / decRate * 1000) : 0;
            string where = string.Format(CultureInfo.InvariantCulture, "{0:0.0}, {1:0.0} px from the lock position", dx, dy);
            if (raMs > MaxBringBackMs || decMs > MaxBringBackMs)
            {
                Log.Warn("Guider", "The lost guide star is " + where + ": too far to bring back with guide pulses");
                _log.Event("Plate solved", "lost star " + where + ", too far to bring back");
                return;
            }
            Log.Info("Guider", string.Format(CultureInfo.InvariantCulture, "{0} guide star found by plate solving, {1}: bringing it back (RA {2} ms, Dec {3} ms)", lost ? "Lost" : "Displaced", where, raMs, decMs));
            _log.Event("Star found by plate solve", string.Format(CultureInfo.InvariantCulture, "{0}; RA {1} ms, Dec {2} ms", where, raMs, decMs));
            var pulser = new GuidePulser(_dm, Settings.Output);
            // the star moved the way a West pulse moves it: East brings it back (and South for North), as in a correction
            await PulseFor(pulser, raPx > 0 ? GuideDirection.East : GuideDirection.West, raMs, ct);
            await PulseFor(pulser, decPx > 0 ? GuideDirection.South : GuideDirection.North, decMs, ct);
            _resetAlgorithms = true;
            // the stars measured since the knock may be others: the guide star is taken again at the lock position, if it
            // is about as bright as it was
            _reacquireFlux = _eventFlux;
            _reacquireFrames = 0;
            _reacquire = true;
        }

        /// <summary>
        /// After bringing a star back: the star at the lock position becomes the guide star again, its brightness the
        /// reference and its partners chosen afresh (called under the lock). Null while there is none there yet; after a
        /// few frames without one, ordinary tracking (and, if need be, another solve) takes over.
        /// </summary>
        private GuideStarInfo Reacquire(float[] lum, int w, int h)
        {
            // brought back by a solve, the star is within a few pixels of the lock: a small box, so a brighter neighbour
            // farther out in the search box is not taken instead
            var m = GuideStars.Measure(lum, w, h, LockX, LockY, Math.Max(5, Settings.SearchRadius / 2));
            if (m == null || m.Saturated || (!double.IsNaN(_reacquireFlux) && m.Flux < 0.3 * _reacquireFlux))
            {
                if (++_reacquireFrames >= 5) _reacquire = false;
                return null;
            }
            _reacquire = false;
            Star = m;
            _stars.SetPrimary(m, lum, w, h, EdgeMargin);
            _resetAlgorithms = true;
            return m;
        }

        private static async Task PulseFor(GuidePulser pulser, GuideDirection dir, int ms, CancellationToken ct)
        {
            while (ms > 0)
            {
                int part = Math.Min(ms, 5000);
                await pulser.Pulse(dir, part, ct);
                ms -= part;
            }
        }
    }
}
