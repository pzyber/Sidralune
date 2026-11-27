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
using Sidralune.Imaging.Analysis;
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
            /// <summary>The mount's side of the pier: on the other side the field is turned round (a meridian flip).</summary>
            public PierSide Pier = PierSide.Unknown;

            /// <summary>A copy with another solution and mount position: a field in use is never changed, only replaced.</summary>
            public SkyField With(Wcs wcs, double mountRa, double mountDec)
            {
                var c = (SkyField)MemberwiseClone();
                c.Wcs = wcs; c.MountRa = mountRa; c.MountDec = mountDec;
                return c;
            }
        }

        // the last guide frame solved at this pointing (null after a slew or a rotator turn), and the guide star's place on
        // the sky (degrees; NaN when it is not known)
        private volatile SkyField _sky;
        // the solve and the positions kept with it are changed together under this (a sync comes from another thread)
        private readonly object _skyLock = new object();
        // the solve was kept through a stop: the first start after it checks it against a fresh frame
        private bool _skyKept;
        /// <summary>How many of the catalogue's brightest stars in the frame a kept solve is checked with.</summary>
        public const int KeptSkyStars = 12;
        /// <summary>How many frames a kept solve is checked on before it is given up.</summary>
        public const int KeptSkyFrames = 2;
        /// <summary>Why the solve kept through the last stop did not fit the frames at the start after it; null when it did.</summary>
        public string KeptSolveCheck { get; private set; }
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
            // a meridian flip brings the mount back to the same coordinates with the field turned round
            var pier = MountPierSide();
            if (sky.Pier != PierSide.Unknown && pier != PierSide.Unknown && pier != sky.Pier) { ForgetSky(); return false; }
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
            lock (_skyLock)
            {
                _sky = null;
                _triedRa = _triedDec = double.NaN;
                _stopNoted = false;
                _skyKept = false;
            }
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
            double ra = MountRa(), dec = MountDeclination(), rot = RotatorAngle();
            lock (_skyLock) { _stopRa = ra; _stopDec = dec; _stopRotator = rot; _stopNoted = true; }
        }

        /// <summary>
        /// Frames start again: the solve kept at the stop is forgotten if the mount has moved since by more than a few
        /// guide pixels (centring stops guiding for its slews), the rotator has turned, or either cannot be told. A solve
        /// kept is checked against a fresh frame before guiding (SolveBeforeGuiding).
        /// </summary>
        private void CheckSkyAfterStop()
        {
            SkyField sky;
            double stopRa, stopDec, stopRot, tried;
            lock (_skyLock)
            {
                if (!_stopNoted) return;
                _stopNoted = false;
                sky = _sky; stopRa = _stopRa; stopDec = _stopDec; stopRot = _stopRotator; tried = _triedRa;
            }
            if (sky == null && double.IsNaN(tried)) return;
            double ra = MountRa(), dec = MountDeclination(), rot = RotatorAngle();
            bool moved = double.IsNaN(ra) || double.IsNaN(dec) || double.IsNaN(stopRa) || double.IsNaN(stopDec);
            if (!moved)
            {
                double arcsec = 3600 * Math.Sqrt(Math.Pow(AstroMath.NormalizeDeg180((ra - stopRa) * 15) * Math.Cos(dec * AstroMath.Deg2Rad), 2) + Math.Pow(dec - stopDec, 2));
                double scale = sky != null ? sky.Wcs.PixelScale : PixelScale;
                moved = arcsec > Math.Max(5, 3 * (scale > 0 ? scale : 1));
            }
            if (!double.IsNaN(stopRot) || !double.IsNaN(rot))
                moved |= double.IsNaN(stopRot) || double.IsNaN(rot) || Math.Abs(AstroMath.NormalizeDeg180(rot - stopRot)) > 0.2;
            if (moved) ForgetSky();
            else lock (_skyLock) _skyKept = _sky != null && _sky == sky;
        }

        /// <summary>
        /// The mount was synced: its coordinates jumped without it moving (centring syncs, then slews to the target). The
        /// places kept in its old coordinates (the solve's, the stop's, the last try's) are moved by the same jump, so a
        /// slew afterwards is still seen; when the jump cannot be told, the solve is forgotten.
        /// </summary>
        /// <param name="raBeforeHours">Where the mount said it pointed just before the sync (hours).</param>
        /// <param name="decBeforeDeg">…and its declination (degrees).</param>
        /// <param name="raSyncedHours">The coordinates it was synced to: what it says afterwards, read from the sync
        /// itself, as a mount may report them only a moment later (INDI).</param>
        /// <param name="decSyncedDeg">…and the declination.</param>
        public void MountSynced(double raBeforeHours, double decBeforeDeg, double raSyncedHours, double decSyncedDeg)
        {
            if (double.IsNaN(raSyncedHours) || double.IsNaN(decSyncedDeg) || double.IsNaN(raBeforeHours) || double.IsNaN(decBeforeDeg)) { ForgetSky(); return; }
            double dRa = AstroMath.NormalizeDeg180((raSyncedHours - raBeforeHours) * 15) / 15, dDec = decSyncedDeg - decBeforeDeg;
            Func<double, double> shift = v => double.IsNaN(v) ? v : AstroMath.NormalizeHours(v + dRa);
            lock (_skyLock)
            {
                var sky = _sky;
                if (sky != null) _sky = sky.With(sky.Wcs, shift(sky.MountRa), sky.MountDec + dDec);
                _stopRa = shift(_stopRa); _stopDec += dDec;
                _triedRa = shift(_triedRa); _triedDec += dDec;
            }
        }

        /// <summary>
        /// A solve kept through a stop, checked against a fresh frame: the mount may have been moved without its
        /// coordinates showing it (another program centring with a sync), or drifted while guiding was stopped. The
        /// catalogue's brightest stars in the frame are looked for around where the solve puts them; most must be found,
        /// all shifted alike, and the shift (drift) is taken into the solve. A frame that does not fit is followed by a
        /// second (one frame can be spoilt: a gust, a satellite, a passing cloud). Null when it fits, else why not, and
        /// whether the frame could tell at all (a cloud leaves too few stars in it to judge by).
        /// </summary>
        private async Task<Tuple<string, bool>> CheckKeptSky(CancellationToken ct)
        {
            var sky = _sky;
            if (sky == null) return Tuple.Create("no solve", true);
            Tuple<string, bool> result = null, misfit = null;
            for (int frame = 0; frame < KeptSkyFrames; frame++)
            {
                var f = await StaticPolarAlignment.NextFrame(this, DateTime.UtcNow, ct);
                double mx, my;
                result = KeptSkyFits(sky, f, out mx, out my);
                if (result.Item1 != null)
                {
                    // a frame that could tell it does not fit outweighs one that could not (a cloud coming after it)
                    if (result.Item2) misfit = result;
                    continue;
                }
                // the stars drifted: the solve moves with them
                if (Math.Abs(mx) > 0.3 || Math.Abs(my) > 0.3)
                    lock (_skyLock) if (_sky == sky) _sky = sky.With(sky.Wcs.Offset(mx, my), sky.MountRa, sky.MountDec);
                return result;
            }
            return misfit ?? result;
        }

        /// <summary>One frame against a kept solve (see CheckKeptSky), with the stars' shift from it when they fit.</summary>
        private static Tuple<string, bool> KeptSkyFits(SkyField sky, AstroImage f, out double mx, out double my)
        {
            mx = my = 0;
            int w = f.Width, h = f.Height;
            if (w != sky.Wcs.ImageWidth || h != sky.Wcs.ImageHeight) return Tuple.Create("another frame size", true);
            var lum = f.Luminance();
            double scale = sky.Wcs.PixelScale;
            // drift of up to 30″ (or 10 pixels) is looked through
            int reach = (int)Math.Ceiling(Math.Max(10, 30 / Math.Max(0.01, scale)));
            var dx = new List<double>();
            var dy = new List<double>();
            int looked = 0;
            foreach (var s in sky.Stars.OrderBy(s => s.Mag))
            {
                double x, y;
                if (!sky.Wcs.WorldToPixel(s.Ra, s.Dec, out x, out y) || x < reach || y < reach || x >= w - reach || y >= h - reach) continue;
                looked++;
                var m = GuideStars.Measure(lum, w, h, x, y, reach);
                if (m != null) { dx.Add(m.X - x); dy.Add(m.Y - y); }
                if (looked >= KeptSkyStars) break;
            }
            if (looked < 3) return Tuple.Create("too few catalogue stars in the frame", false);
            int agree = 0;
            if (dx.Count >= 3)
            {
                mx = MedianOf(dx); my = MedianOf(dy);
                double tol = Math.Max(1.5, GuideStarCatalog.MatchArcsec(scale) / scale);
                for (int i = 0; i < dx.Count; i++) if (Math.Abs(dx[i] - mx) <= tol && Math.Abs(dy[i] - my) <= tol) agree++;
            }
            int needed = Math.Max(3, (int)Math.Ceiling(0.6 * looked));
            if (agree < needed)
            {
                string why = string.Format(CultureInfo.InvariantCulture, "{0} of {1} catalogue stars where it puts them, {2} found, offsets {3}", agree, looked, dx.Count,
                    string.Join("; ", dx.Select((v, i) => string.Format(CultureInfo.InvariantCulture, "{0:0.0},{1:0.0}", v, dy[i]))));
                // the stars are elsewhere (the mount moved), or hardly any are to be seen (a cloud): only the first tells
                int seen = StarDetector.Detect(lum, w, h, new StarDetectionOptions { Sigma = 5, MaxStars = 50 }).Count;
                return Tuple.Create(seen >= needed ? why : why + string.Format(CultureInfo.InvariantCulture, ", {0} stars in the frame", seen), seen >= needed);
            }
            return Tuple.Create<string, bool>(null, true);
        }

        private static double MedianOf(List<double> v)
        {
            var s = v.OrderBy(a => a).ToList();
            int n = s.Count;
            return n == 0 ? double.NaN : n % 2 == 1 ? s[n / 2] : 0.5 * (s[n / 2 - 1] + s[n / 2]);
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
            var field = new SkyField { Wcs = wcs, Stars = stars, MountRa = MountRa(), MountDec = MountDeclination(), Binning = Math.Max(1, Settings.Binning), Camera = CameraId(), Pier = MountPierSide() };            // of where the mount points now: nothing from a stop before to check
            lock (_skyLock) { _sky = field; _stopNoted = false; _skyKept = false; }
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
            if (!Settings.SolveGuideFrames) return;
            bool kept;
            lock (_skyLock) { kept = _skyKept && _sky != null; _skyKept = false; }
            SkyField unjudged = null;
            if (kept)
            {
                Tuple<string, bool> check;
                try { check = await CheckKeptSky(ct); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { check = Tuple.Create(ex.Message, false); }
                KeptSolveCheck = check.Item1;
                if (check.Item1 != null)
                {
                    // a frame that cannot tell (a cloud, no frame): solved again all the same, and the kept solve used
                    // if that fails too
                    if (!check.Item2) unjudged = _sky;
                    Log.Info("Guider", "The solve kept from the last stop does not fit the frame (" + check.Item1 + "): solving again");
                    _log.Event("Plate solve", "the kept solve does not fit the frame (" + check.Item1 + "): solving again");
                    ForgetSky();
                }
                else if (Star != null && !_lost) RememberStarOnSky(Star);
            }
            try { await SolveIfNoSky(ct); }
            finally
            {
                if (unjudged != null)
                {
                    bool back;
                    lock (_skyLock) { back = _sky == null; if (back) _sky = unjudged; }
                    if (back)
                    {
                        Log.Info("Guider", "The frame could neither check the kept solve nor be solved: the kept solve is used");
                        _log.Event("Plate solve", "the kept solve is used: the frame had too few stars to check it");
                    }
                }
            }
        }

        /// <summary>A fresh guide frame solved, unless there is a solve here, or one was tried at this pointing.</summary>
        private async Task SolveIfNoSky(CancellationToken ct)
        {
            if (_sky != null) return;
            var tel = _dm.Telescope;
            if (tel == null || !tel.Connected)
            {
                // nothing to solve with: said once, not at every start, and tried once a mount is connected
                string why = Lang.T("Solving a guide frame needs the mount's position: no telescope connected.");
                if (SolveMessage != why) NoteSolveFailed(why);
                return;
            }
            double triedRa, triedDec;
            lock (_skyLock) { triedRa = _triedRa; triedDec = _triedDec; }
            if (!double.IsNaN(triedRa) && MountNear(triedRa, triedDec)) return;
            double nowRa = MountRa(), nowDec = MountDeclination();
            lock (_skyLock) { _triedRa = nowRa; _triedDec = nowDec; }
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
            try
            {
                await PulseFor(pulser, raPx > 0 ? GuideDirection.East : GuideDirection.West, raMs, ct);
                await PulseFor(pulser, decPx > 0 ? GuideDirection.South : GuideDirection.North, decMs, ct);
            }
            catch
            {
                // stopped (or failed) part of the way back: how far the field has come is not known, so the solve of the
                // displaced frame no longer says where anything is
                ForgetSky();
                throw;
            }
            // the field came back with the star, which guiding then holds at the lock position: the solve of the displaced
            // frame is moved with it, so it stays of the frames guided on
            double ra = MountRa(), dec = MountDeclination();
            lock (_skyLock) { var sky = _sky; if (sky != null && sky.Wcs == wcs) _sky = sky.With(wcs.Offset(-dx, -dy), ra, dec); }
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
            // farther out in the search box is not taken instead. It must be about as bright as before the knock (the
            // frames around it may be smeared, or dimmed by a cloud): a much fainter or much brighter star is another one
            var m = GuideStars.Measure(lum, w, h, LockX, LockY, Math.Max(5, Settings.SearchRadius / 2));
            bool other = m != null && !double.IsNaN(_reacquireFlux) && _reacquireFlux > 0 && (m.Flux < 0.3 * _reacquireFlux || m.Flux > 3 * _reacquireFlux);
            if (m == null || m.Saturated || other)
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
