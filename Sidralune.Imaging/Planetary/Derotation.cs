using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Sidralune.Core;
using Sidralune.Core.Coordinates;
using Sidralune.Core.Mathematics;
using Sidralune.Core.Time;

namespace Sidralune.Imaging.Planetary
{
    /// <summary>
    /// Field rotation for alt-azimuth (or poorly polar aligned) mounts. On an alt-az mount the sky rotates in the
    /// camera frame by the change of the parallactic angle q; frames are rotated by -(q - q_ref) about the object.
    /// </summary>
    public static class FieldRotation
    {
        /// <summary>Parallactic angle (deg) for a J2000 target at time t.</summary>
        public static double ParallacticAngleDeg(Observer obs, AstroTime t, double raJ2000, double decJ2000)
        {
            var ctx = new SkyContext(obs, t);
            var app = ctx.IcrsToApparent(raJ2000, decJ2000);
            double ha = ctx.HourAngle(app.Ra);
            return Transforms.ParallacticAngle(ha, app.Dec, obs.LatRad) * AstroMath.Rad2Deg;
        }

        /// <summary>
        /// Rotation (deg) to apply to each frame so that all match the reference time. For an equatorial mount with polar
        /// axis error (dAzDeg, dAltDeg) the field rotation is computed from the difference of the true and assumed pole
        /// position angles (small-angle model).
        /// </summary>
        public static double[] Angles(Observer obs, IList<DateTime> utc, double raJ2000, double decJ2000, int referenceIndex = 0, bool altAz = true,
            double polarErrorAzDeg = 0, double polarErrorAltDeg = 0)
        {
            var r = new double[utc.Count];
            if (utc.Count == 0) return r;
            if (altAz)
            {
                double q0 = ParallacticAngleDeg(obs, AstroTime.FromDateTime(utc[referenceIndex]), raJ2000, decJ2000);
                for (int i = 0; i < utc.Count; i++)
                    r[i] = -AstroMath.NormalizeDeg180(ParallacticAngleDeg(obs, AstroTime.FromDateTime(utc[i]), raJ2000, decJ2000) - q0);
                return r;
            }
            // equatorial mount pointing at a mis-aligned pole: the mount's "pole" is displaced; the camera stays fixed
            // relative to that pole, so the sky rotates by the change in position angle of the true pole as seen from the target.
            var mountObs = obs.Clone();
            mountObs.LatitudeDeg = obs.LatitudeDeg + polarErrorAltDeg;
            mountObs.LongitudeDeg = obs.LongitudeDeg;
            Func<DateTime, double> pa = dt =>
            {
                var t = AstroTime.FromDateTime(dt);
                var ctx = new SkyContext(obs, t);
                var app = ctx.IcrsToApparent(raJ2000, decJ2000);
                // mount pole direction in true-of-date equatorial frame: pole tilted by errors (az error rotates about zenith)
                double lat = obs.LatRad;
                double poleAz = polarErrorAzDeg * AstroMath.Deg2Rad, poleAlt = lat + polarErrorAltDeg * AstroMath.Deg2Rad;
                double ha, dec;
                Transforms.AltAzToHaDec(poleAz, poleAlt, lat, out ha, out dec);
                double poleRa = ctx.Last - ha;
                return AstroMath.PositionAngle(app.Ra, app.Dec, poleRa, dec) * AstroMath.Rad2Deg;
            };
            double p0 = pa(utc[referenceIndex]);
            for (int i = 0; i < utc.Count; i++) r[i] = -AstroMath.NormalizeDeg180(pa(utc[i]) - p0);
            return r;
        }

        /// <summary>Rotate a frame about (cx, cy) by angle degrees (positive = counter-clockwise on screen).</summary>
        public static AstroImage RotateAbout(AstroImage img, double angleDeg, double cx, double cy, Interpolation interp = Interpolation.Bicubic)
        {
            var t = Transform2D.Rotation(-angleDeg * Math.PI / 180, cx, cy);
            return Geometry.Warp(img, t, img.Width, img.Height, interp);
        }
    }

    /// <summary>Image-based rotation estimation between two frames of an extended object (polar resampling + circular correlation).</summary>
    public static class RotationEstimator
    {
        /// <summary>Angle (deg, same convention as FieldRotation.RotateAbout) by which 'b' is rotated relative to 'a' around (cx, cy), searching ±maxDeg.</summary>
        public static double Estimate(float[] a, float[] b, int w, int h, double cx, double cy, double rMin, double rMax, double maxDeg = 30)
        {
            int nt = 1440, nr = Math.Max(8, (int)((rMax - rMin) / 1.0));
            var pa = Polar(a, w, h, cx, cy, rMin, rMax, nt, nr);
            var pb = Polar(b, w, h, cx, cy, rMin, rMax, nt, nr);
            // circular cross-correlation along theta via FFT, summed over radii
            var acc = new double[nt];
            var re1 = new double[nt]; var im1 = new double[nt]; var re2 = new double[nt]; var im2 = new double[nt];
            for (int r = 0; r < nr; r++)
            {
                double m1 = 0, m2 = 0;
                for (int t = 0; t < nt; t++) { m1 += pa[r * nt + t]; m2 += pb[r * nt + t]; }
                m1 /= nt; m2 /= nt;
                for (int t = 0; t < nt; t++) { re1[t] = pa[r * nt + t] - m1; im1[t] = 0; re2[t] = pb[r * nt + t] - m2; im2[t] = 0; }
                Fft.Transform(re1, im1, false); Fft.Transform(re2, im2, false);
                for (int t = 0; t < nt; t++)
                {
                    double x = re1[t] * re2[t] + im1[t] * im2[t], y = im1[t] * re2[t] - re1[t] * im2[t];
                    re1[t] = x; im1[t] = y;
                }
                Fft.Transform(re1, im1, true);
                for (int t = 0; t < nt; t++) acc[t] += re1[t];
            }
            int lim = (int)(maxDeg / 360.0 * nt), best = 0; double bv = double.MinValue;
            for (int k = -lim; k <= lim; k++)
            {
                int idx = (k + nt) % nt;
                if (acc[idx] > bv) { bv = acc[idx]; best = k; }
            }
            // parabolic refinement
            double l = acc[(best - 1 + nt) % nt], c = acc[(best + nt) % nt], rr = acc[(best + 1 + nt) % nt];
            double den = l - 2 * c + rr;
            double frac = Math.Abs(den) > 1e-12 ? 0.5 * (l - rr) / den : 0;
            return (best + frac) * 360.0 / nt;
        }

        private static float[] Polar(float[] d, int w, int h, double cx, double cy, double rMin, double rMax, int nt, int nr)
        {
            var p = new float[nt * nr];
            Parallel.For(0, nr, Core.Util.StopRequest.Options, r =>
            {
                double rad = rMin + (rMax - rMin) * r / Math.Max(1, nr - 1);
                for (int t = 0; t < nt; t++)
                {
                    double a = 2 * Math.PI * t / nt;
                    float v = Geometry.SampleAt(d, w, h, cx + rad * Math.Cos(a), cy + rad * Math.Sin(a), Interpolation.Bilinear);
                    p[r * nt + t] = float.IsNaN(v) ? 0 : v;
                }
            });
            return p;
        }
    }

    /// <summary>Geometry of a planetary disc in an image for spherical derotation.</summary>
    public class DiscGeometry
    {
        public double Cx, Cy;
        /// <summary>Equatorial radius (pixels).</summary>
        public double Radius;
        /// <summary>Polar/equatorial ratio (Jupiter 0.935, Saturn 0.902, Mars 0.994).</summary>
        public double Flattening = 1.0;
        /// <summary>Position angle of the planet's north pole in the image (deg, measured from image "up" towards image left, i.e. N through E with north up/east left).</summary>
        public double PoleAngleDeg;
        /// <summary>Sub-observer planetographic latitude (deg).</summary>
        public double SubObserverLatDeg;
        /// <summary>Image is mirrored (e.g. diagonal / Newtonian odd reflections).</summary>
        public bool Mirrored;
        /// <summary>Rings in the equatorial plane, inner and outer edge in equatorial radii (Saturn 1.236 and 2.270); 0 for none.</summary>
        public double RingInner, RingOuter;

        public DiscGeometry Clone() => (DiscGeometry)MemberwiseClone();
    }

    /// <summary>
    /// Something on the disc that moves with its own orbit rather than with the planet's rotation, such as a moon of
    /// Jupiter in transit or its shadow: where it is in one image (pixels), and its size. Id pairs the same moon or
    /// shadow across images.
    /// </summary>
    public class MovingSpot
    {
        public int Id;
        public double X, Y, Radius;
    }

    /// <summary>How images are weighed and combined when derotated to one time.</summary>
    public class DerotationOptions
    {
        /// <summary>Images farther than this from the target time, in degrees of the planet's rotation, are left out (0: none are).</summary>
        public double MaxRotationDeg = 20;
        /// <summary>The weight of an image at MaxRotationDeg, the nearest weighing 1; weights fall linearly between (1: all alike).</summary>
        public double FarWeight = 0.5;
        public bool LimbDarkeningCompensation;
        /// <summary>Per image, the moons and shadows on it (null for none), and where they are at the target time in the output.</summary>
        public IList<IList<MovingSpot>> Spots;
        public IList<MovingSpot> TargetSpots;
        /// <summary>
        /// The disc the result is centred on, and the size of image it is drawn on, so stacks of several filters line
        /// up; the first image's when null.
        /// </summary>
        public DiscGeometry Reference;
        public int Width, Height;
    }

    public class DerotationResult
    {
        public AstroImage Image;
        /// <summary>Per image: how far it was rotated (degrees of the planet's rotation), its weight (0 when left out).</summary>
        public double[] RotationDeg, Weight;
        public int Used => Weight.Count(w => w > 0);
    }

    /// <summary>
    /// WinJUPOS-style derotation: re-projects a planetary image as if taken at a different time by rotating the
    /// globe about its axis (orthographic projection with oblateness), allowing long stacks/RGB composites without smear.
    /// </summary>
    public static class SphereDerotation
    {
        /// <summary>Saturn's rings: the C ring's inner edge and the A ring's outer edge, in Saturn's equatorial radii.</summary>
        public const double SaturnRingInner = 1.236, SaturnRingOuter = 2.270;

        /// <summary>Rotation periods (hours, negative = retrograde): Jupiter System II 9.92492, Saturn 10.656, Mars 24.6229, Venus clouds -96.</summary>
        public static double PeriodHours(Core.Ephemeris.Body b)
        {
            switch (b)
            {
                case Core.Ephemeris.Body.Jupiter: return 9.92492;
                case Core.Ephemeris.Body.Saturn: return 10.6562;
                case Core.Ephemeris.Body.Mars: return 24.6229;
                case Core.Ephemeris.Body.Venus: return -96.0; // retrograde cloud super-rotation (~4 days)
                case Core.Ephemeris.Body.Uranus: return -17.24;
                case Core.Ephemeris.Body.Neptune: return 16.11;
                case Core.Ephemeris.Body.Moon: return 655.72;
            }
            return 24;
        }

        /// <summary>How far the planet turns in dtSeconds, in degrees.</summary>
        public static double RotationDeg(double dtSeconds, double periodHours) => 360.0 * dtSeconds / (periodHours * 3600.0);

        /// <summary>
        /// The image pixel of a point in the planet's sky frame: X towards the planet's west limb and Y towards its north
        /// pole, in equatorial radii (as Meeus gives the Galilean moons).
        /// </summary>
        public static void ImagePoint(DiscGeometry g, double X, double Y, out double ix, out double iy)
        {
            double pa = g.PoleAngleDeg * AstroMath.Deg2Rad, ca = Math.Cos(pa), sa = Math.Sin(pa);
            double u = X * g.Radius, v = Y * g.Radius;
            ix = (u * ca - v * sa) * (g.Mirrored ? -1 : 1) + g.Cx;
            iy = -(u * sa + v * ca) + g.Cy;
        }

        /// <summary>
        /// The planet's surface under an image pixel: longitude in the body frame (radians, 0 at the central meridian,
        /// increasing towards the west limb) and planetographic latitude. False off the globe.
        /// </summary>
        public static bool SurfacePoint(DiscGeometry g, double x, double y, out double lon, out double lat)
        {
            double pa = g.PoleAngleDeg * AstroMath.Deg2Rad, ca = Math.Cos(pa), sa = Math.Sin(pa);
            double D = g.SubObserverLatDeg * AstroMath.Deg2Rad, cD = Math.Cos(D), sD = Math.Sin(D);
            double Re = g.Radius, Rp = g.Radius * g.Flattening;
            double dx = (x - g.Cx) * (g.Mirrored ? -1 : 1), dy = -(y - g.Cy);
            double u = dx * ca + dy * sa, v = -dx * sa + dy * ca;
            double X, Y, Z;
            lon = lat = 0;
            if (!Unproject(u, v, Re, Rp, cD, sD, out X, out Y, out Z)) return false;
            double bx = X, by = Y * cD + Z * sD, bz = -Y * sD + Z * cD;
            lon = Math.Atan2(bx, bz);
            // planetographic: the normal's angle to the equator
            lat = Math.Atan2(by * Re * Re, Math.Sqrt(bx * bx + bz * bz) * Rp * Rp);
            return true;
        }

        /// <summary>
        /// Derotate by dtSeconds (positive: show the planet as it would appear dtSeconds later).
        /// Pixels whose source lies on the hidden hemisphere are filled from the original image (or 0 if fillOriginal=false).
        /// Rings (g.RingOuter &gt; 0) look the same as the planet turns, so where they are seen, in front of the globe or
        /// beside it, they are taken as they are.
        /// </summary>
        public static AstroImage Derotate(AstroImage img, DiscGeometry g, double dtSeconds, double periodHours, bool fillOriginal = true, bool limbDarkeningCompensation = false)
        {
            int w = img.Width, h = img.Height;
            var o = img.CloneEmpty();
            double dLon = 2 * Math.PI * dtSeconds / (periodHours * 3600.0);
            double pa = g.PoleAngleDeg * AstroMath.Deg2Rad, ca = Math.Cos(pa), sa = Math.Sin(pa);
            double D = g.SubObserverLatDeg * AstroMath.Deg2Rad, cD = Math.Cos(D), sD = Math.Sin(D);
            double Re = g.Radius, Rp = g.Radius * g.Flattening;
            double mir = g.Mirrored ? -1 : 1;
            // rings seen nearly edge-on are a line, left to the globe's handling
            bool rings = g.RingOuter > 0 && Math.Abs(sD) > 0.02;
            double rIn = g.RingInner * Re, rOut = g.RingOuter * Re;
            for (int c = 0; c < img.Channels; c++)
            {
                var src = img.Data[c]; var dst = o.Data[c];
                Parallel.For(0, h, Core.Util.StopRequest.Options, y =>
                {
                    for (int x = 0; x < w; x++)
                    {
                        // image -> disc frame aligned with the projected pole (u towards +x, i.e. the west limb when north is up, v towards north)
                        double dx = (x - g.Cx) * mir, dy = -(y - g.Cy);
                        double u = dx * ca + dy * sa, v = -dx * sa + dy * ca;
                        int i = y * w + x;
                        // ellipsoid in observer frame: invert orthographic projection for an oblate spheroid tilted by D
                        double X, Y, Z;
                        bool globe = Unproject(u, v, Re, Rp, cD, sD, out X, out Y, out Z);
                        if (rings)
                        {
                            // the ring plane (by = 0) under this pixel: its depth towards the observer and its radius
                            double zr = -v * cD / sD, rho = Math.Sqrt(u * u + v * v / (sD * sD));
                            if (rho >= rIn && rho <= rOut && (!globe || zr > Z)) { dst[i] = src[i]; continue; }
                        }
                        if (!globe) { dst[i] = fillOriginal ? src[i] : 0; continue; }
                        // rotate about planet axis: in body frame (axis = z'), longitude increases with time.
                        // observer frame -> body frame: rotate about x by -D
                        double bx = X, by = Y * cD + Z * sD, bz = -Y * sD + Z * cD;
                        // source point is the one that rotated into this position. For a prograde planet seen north-up,
                        // surface features drift from the east (left) limb to the west (right) limb, so the source of a
                        // destination pixel lies further east (towards -x).
                        double cl = Math.Cos(dLon), sl = Math.Sin(dLon);
                        double sx = bx * cl - bz * sl, sz = bx * sl + bz * cl, sy = by;
                        // body -> observer
                        double oX = sx, oY = sy * cD - sz * sD, oZ = sy * sD + sz * cD;
                        if (oZ < 0) { dst[i] = fillOriginal ? src[i] : 0; continue; } // on far side
                        double su = oX, sv = oY;
                        double ix = (su * ca - sv * sa) * mir + g.Cx, iy = -(su * sa + sv * ca) + g.Cy;
                        float val = Geometry.SampleAt(src, w, h, ix, iy, Interpolation.Bicubic);
                        if (limbDarkeningCompensation)
                        {
                            // Minnaert-like correction: scale by ratio of cosines of emission angles at source/destination
                            double muSrc = Math.Max(0.15, oZ / Re), muDst = Math.Max(0.15, Z / Re);
                            val = (float)(val * Math.Pow(muDst / muSrc, 0.7));
                        }
                        dst[i] = float.IsNaN(val) ? src[i] : val;
                    }
                });
            }
            o.Header.AddHistory(string.Format("Spherical derotation by {0:0.0} s (Δλ {1:0.00}°)", dtSeconds, dLon * AstroMath.Rad2Deg));
            return o;
        }

        /// <summary>Find the visible surface point (observer frame, z towards observer) projecting to (u, v).</summary>
        private static bool Unproject(double u, double v, double Re, double Rp, double cD, double sD, out double X, out double Y, out double Z)
        {
            // Ellipsoid in body frame: (bx²+bz²)/Re² + by²/Rp² = 1, observer frame = body rotated by D about x.
            // Point: (X=u, Y=v, Z=t). Solve quadratic in t.
            X = u; Y = v;
            double a = (cD * cD) / (Rp * Rp) + (sD * sD) / (Re * Re);
            // by = Y cD + Z sD ; bz = -Y sD + Z cD
            double A = (sD * sD) / (Rp * Rp) + (cD * cD) / (Re * Re);
            double B = 2 * v * (cD * sD / (Rp * Rp) - sD * cD / (Re * Re));
            double C = u * u / (Re * Re) + v * v * a - 1;
            double disc = B * B - 4 * A * C;
            if (disc < 0) { Z = 0; return false; }
            Z = (-B + Math.Sqrt(disc)) / (2 * A);
            return true;
        }

        /// <summary>Derotate a set of images to a common time and average them (WinJUPOS "de-rotation of images").</summary>
        public static AstroImage DerotateAndStack(IList<AstroImage> images, IList<DiscGeometry> geoms, IList<DateTime> times, DateTime target, double periodHours,
            System.Threading.CancellationToken ct = default(System.Threading.CancellationToken)) =>
            Stack(images, geoms, times, target, periodHours, new DerotationOptions { MaxRotationDeg = 0, FarWeight = 1 }, ct).Image;

        /// <summary>
        /// Derotate images to a common time and combine them, centred on the first image's disc. Each image is weighed
        /// by how far it was rotated (see DerotationOptions); those too far are left out, though the nearest is always
        /// used. Moons and shadows (opt.Spots) are left out where each image has them, and put where they are at the
        /// target time from the images that have them, so they follow their own orbits instead of the planet's rotation.
        /// </summary>
        public static DerotationResult Stack(IList<AstroImage> images, IList<DiscGeometry> geoms, IList<DateTime> times, DateTime target, double periodHours,
            DerotationOptions opt, System.Threading.CancellationToken ct = default(System.Threading.CancellationToken))
        {
            opt = opt ?? new DerotationOptions();
            int n = images.Count;
            var res = new DerotationResult { RotationDeg = new double[n], Weight = new double[n] };
            for (int k = 0; k < n; k++)
            {
                res.RotationDeg[k] = RotationDeg((target - times[k]).TotalSeconds, periodHours);
                double off = Math.Abs(res.RotationDeg[k]);
                res.Weight[k] = opt.MaxRotationDeg <= 0 ? 1 : off > opt.MaxRotationDeg ? 0 : 1 - (1 - Math.Max(0, Math.Min(1, opt.FarWeight))) * off / opt.MaxRotationDeg;
            }
            if (res.Used == 0)
            {
                int near = Enumerable.Range(0, n).OrderBy(k => Math.Abs(res.RotationDeg[k])).First();
                res.Weight[near] = 1;
            }
            var g0 = opt.Reference ?? geoms[0];
            int w = opt.Reference != null && opt.Width > 0 ? opt.Width : images[0].Width, h = opt.Reference != null && opt.Height > 0 ? opt.Height : images[0].Height;
            var acc = new AstroImage(w, h, images.Max(im => im.Channels));
            var wsum = new float[acc.PixelCount];
            bool spots = opt.Spots != null && opt.TargetSpots != null && opt.TargetSpots.Count > 0;
            // each image moved onto the result's canvas, its disc's centre on the reference's
            Func<AstroImage, double, double, AstroImage> place = (im, dx, dy) =>
                im.Width == w && im.Height == h ? Geometry.Shift(im, dx, dy) : Geometry.Warp(im, Transform2D.Translation(dx, dy), w, h, Interpolation.Bicubic);
            // the moons and shadows at the target time, built from each image's own, moved there
            var spotAcc = spots ? new AstroImage(w, h, acc.Channels) : null;
            var spotW = spots ? new float[acc.PixelCount] : null;
            for (int k = 0; k < n; k++)
            {
                ct.ThrowIfCancellationRequested(); // a stop takes effect between images
                if (res.Weight[k] <= 0) continue;
                float wk = (float)res.Weight[k];
                double dt = (target - times[k]).TotalSeconds;
                var d = Derotate(images[k], geoms[k], dt, periodHours, false, opt.LimbDarkeningCompensation);
                // align disc centres to the first geometry
                double sx = g0.Cx - geoms[k].Cx, sy = g0.Cy - geoms[k].Cy;
                var shifted = place(d, sx, sy);
                float[] keep = null;
                var own = spots && k < opt.Spots.Count ? opt.Spots[k] : null;
                if (own != null && own.Count > 0)
                {
                    // where this image's moons and shadows went in its derotated copy: left out there
                    var mask = new AstroImage(images[k].Width, images[k].Height, 1);
                    foreach (var s in own) PaintSpot(mask.Data[0], mask.Width, mask.Height, s.X, s.Y, s.Radius);
                    var moved = place(Derotate(mask, geoms[k], dt, periodHours, false), sx, sy).Data[0];
                    keep = new float[moved.Length];
                    for (int i = 0; i < keep.Length; i++) keep[i] = 1 - Math.Max(0, Math.Min(1, moved[i]));
                    // and each moved, as it is in the image, to where it is at the target time
                    foreach (var s in own)
                    {
                        var t = opt.TargetSpots.FirstOrDefault(q => q.Id == s.Id);
                        if (t == null) continue;
                        // the spot is in the image's own pixels, the target in the result's
                        var at = place(images[k], t.X - s.X, t.Y - s.Y);
                        AddSpot(spotAcc, spotW, at, t.X, t.Y, t.Radius * 1.4, wk);
                    }
                }
                for (int c = 0; c < acc.Channels; c++)
                    for (int i = 0; i < acc.PixelCount; i++)
                    {
                        float v = shifted.Data[Math.Min(c, shifted.Channels - 1)][i];
                        if (v <= 0) continue;
                        float wi = keep != null ? wk * keep[i] : wk;
                        acc.Data[c][i] += v * wi;
                        if (c == 0) wsum[i] += wi;
                    }
            }
            for (int c = 0; c < acc.Channels; c++) for (int i = 0; i < acc.PixelCount; i++) acc.Data[c][i] = wsum[i] > 1e-6f ? acc.Data[c][i] / wsum[i] : 0;
            if (spots)
            {
                // the moved moons and shadows over the derotated planet, faded out at their edges
                foreach (var t in opt.TargetSpots)
                {
                    double r1 = t.Radius * 1.1, r2 = t.Radius * 1.4;
                    for (int y = Math.Max(0, (int)(t.Y - r2)); y <= Math.Min(h - 1, (int)(t.Y + r2) + 1); y++)
                        for (int x = Math.Max(0, (int)(t.X - r2)); x <= Math.Min(w - 1, (int)(t.X + r2) + 1); x++)
                        {
                            int i = y * w + x;
                            if (spotW[i] <= 1e-6f) continue;
                            double r = Math.Sqrt((x - t.X) * (x - t.X) + (y - t.Y) * (y - t.Y));
                            double b = r <= r1 ? 1 : r >= r2 ? 0 : 0.5 * (1 + Math.Cos(Math.PI * (r - r1) / (r2 - r1)));
                            if (b <= 0) continue;
                            for (int c = 0; c < acc.Channels; c++) acc.Data[c][i] = (float)(acc.Data[c][i] * (1 - b) + spotAcc.Data[c][i] / spotW[i] * b);
                        }
                }
            }
            res.Image = acc;
            return res;
        }

        // a disc of ones with a soft edge, a little larger than the spot so its blur goes too
        private static void PaintSpot(float[] d, int w, int h, double cx, double cy, double radius)
        {
            double r1 = radius * 1.15, r2 = radius * 1.5;
            for (int y = Math.Max(0, (int)(cy - r2)); y <= Math.Min(h - 1, (int)(cy + r2) + 1); y++)
                for (int x = Math.Max(0, (int)(cx - r2)); x <= Math.Min(w - 1, (int)(cx + r2) + 1); x++)
                {
                    double r = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                    double v = r <= r1 ? 1 : r >= r2 ? 0 : 0.5 * (1 + Math.Cos(Math.PI * (r - r1) / (r2 - r1)));
                    d[y * w + x] = (float)Math.Max(d[y * w + x], v);
                }
        }

        private static void AddSpot(AstroImage acc, float[] wsum, AstroImage img, double cx, double cy, double radius, float weight)
        {
            int w = acc.Width, h = acc.Height;
            for (int y = Math.Max(0, (int)(cy - radius)); y <= Math.Min(h - 1, (int)(cy + radius) + 1); y++)
                for (int x = Math.Max(0, (int)(cx - radius)); x <= Math.Min(w - 1, (int)(cx + radius) + 1); x++)
                {
                    if ((x - cx) * (x - cx) + (y - cy) * (y - cy) > radius * radius) continue;
                    int i = y * w + x;
                    for (int c = 0; c < acc.Channels; c++) acc.Data[c][i] += img.Data[Math.Min(c, img.Channels - 1)][i] * weight;
                    wsum[i] += weight;
                }
        }

        /// <summary>
        /// The disc of a ringed planet (Saturn): the rings reach farther than the globe and would spoil a fit to the
        /// limb, so the centre is the middle of the bright object along the ring's long axis and across it, and the
        /// equatorial radius is half its length along the long axis (the line of nodes, which the outer ring edge always
        /// spans) divided by the outer ring radius. Null when nothing bright is found.
        /// </summary>
        public static double[] RingedDisc(float[] lum, int w, int h, ObjectInfo ob, double poleAngleDeg, bool mirrored, double ringOuter)
        {
            if (ob == null || !ob.Found) return null;
            var sorted = (float[])lum.Clone();
            Array.Sort(sorted);
            double sky = sorted[sorted.Length / 2], top = sorted[(int)(0.999 * (sorted.Length - 1))], thr = sky + 0.2 * (top - sky);
            double pa = poleAngleDeg * AstroMath.Deg2Rad;
            // the ring's long axis in the image: the planet's west direction (u) of the disc frame
            double ex = Math.Cos(pa) * (mirrored ? -1 : 1), ey = -Math.Sin(pa);
            double nx = -Math.Sin(pa) * (mirrored ? -1 : 1), ny = -Math.Cos(pa);
            Func<double, double, double, double, double> reach = (x0, y0, dx, dy) =>
            {
                // the farthest bright pixel along the line, allowing for gaps such as the Cassini division
                double far = 0;
                for (double s = 0; s < Math.Max(w, h); s += 0.5)
                {
                    int x = (int)Math.Round(x0 + dx * s), y = (int)Math.Round(y0 + dy * s);
                    if (x < 0 || y < 0 || x >= w || y >= h) break;
                    if (lum[y * w + x] > thr) far = s;
                }
                return far;
            };
            double cx = ob.Cx, cy = ob.Cy;
            for (int it = 0; it < 3; it++)
            {
                double a = reach(cx, cy, ex, ey), b = reach(cx, cy, -ex, -ey);
                cx += ex * (a - b) / 2; cy += ey * (a - b) / 2;
                double c = reach(cx, cy, nx, ny), d = reach(cx, cy, -nx, -ny);
                cx += nx * (c - d) / 2; cy += ny * (c - d) / 2;
            }
            double len = (reach(cx, cy, ex, ey) + reach(cx, cy, -ex, -ey)) / 2;
            if (len <= 0) return null;
            return new[] { cx, cy, len / ringOuter };
        }
    }

    /// <summary>
    /// The orientation of a planet in images: the pole's position angle and whether the images are mirrored, found
    /// from two images minutes apart by derotating the earlier to the later's time with each orientation tried and
    /// keeping the one that matches best. Nothing needs to be measured by hand; the planet must have turned enough
    /// between the two (a few degrees) for its features to have moved.
    /// </summary>
    public static class OrientationEstimator
    {
        /// <summary>The least rotation between the two images, in degrees, for a measurement.</summary>
        public const double MinRotationDeg = 3;

        public class Result
        {
            public double PoleAngleDeg;
            public bool Mirrored;
            /// <summary>The match (normalised correlation of the fine detail, -1..1) of the best orientation, and of the best with the image the other way round.</summary>
            public double Score, OtherMirrorScore;
        }

        /// <summary>
        /// ga and gb give each image's disc (centre, radius, flattening, sub-observer latitude); their pole angle and
        /// mirroring are what is found. Null when the planet turned less than MinRotationDeg between them.
        /// </summary>
        public static Result Estimate(AstroImage a, DiscGeometry ga, DateTime ta, AstroImage b, DiscGeometry gb, DateTime tb, double periodHours,
            System.Threading.CancellationToken ct = default(System.Threading.CancellationToken))
        {
            double dt = (tb - ta).TotalSeconds;
            if (Math.Abs(SphereDerotation.RotationDeg(dt, periodHours)) < MinRotationDeg) return null;
            // both on a small common grid around the disc: radius 60 pixels
            const double R = 60;
            int size = 2 * (int)(R * 1.2) + 1;
            Func<AstroImage, DiscGeometry, float[]> small = (img, g) =>
            {
                var lum = img.Channels == 1 ? img.Data[0] : img.Luminance();
                var o = new float[size * size];
                double k = g.Radius / R;
                for (int y = 0; y < size; y++)
                    for (int x = 0; x < size; x++)
                    {
                        float v = Geometry.SampleAt(lum, img.Width, img.Height, g.Cx + (x - size / 2) * k, g.Cy + (y - size / 2) * k, Interpolation.Bilinear);
                        o[y * size + x] = float.IsNaN(v) ? 0 : v;
                    }
                return o;
            };
            var sa = small(a, ga);
            var sb = small(b, gb);
            // the fine detail only: the bands' broad brightness and the limb darkening would match any orientation
            Func<float[], float[]> detail = d =>
            {
                var blur = Filters.GaussianBlur(d, size, size, R / 10);
                var o = new float[d.Length];
                for (int i = 0; i < d.Length; i++) o[i] = d[i] - blur[i];
                return o;
            };
            var db = detail(sb);
            var imgA = new AstroImage(size, size, new[] { sa });
            Func<double, bool, double> score = (angle, mirrored) =>
            {
                ct.ThrowIfCancellationRequested();
                var g = new DiscGeometry { Cx = size / 2, Cy = size / 2, Radius = R, Flattening = ga.Flattening, SubObserverLatDeg = ga.SubObserverLatDeg, PoleAngleDeg = angle, Mirrored = mirrored };
                var moved = SphereDerotation.Derotate(imgA, g, dt, periodHours, false).Data[0];
                var dm = detail(moved);
                double sab = 0, saa = 0, sbb = 0;
                for (int y = 0; y < size; y++)
                    for (int x = 0; x < size; x++)
                    {
                        double rx = (x - size / 2) / R, ry = (y - size / 2) / R;
                        int i = y * size + x;
                        if (rx * rx + ry * ry > 0.75 * 0.75 || moved[i] <= 0) continue;
                        sab += dm[i] * db[i]; saa += dm[i] * dm[i]; sbb += db[i] * db[i];
                    }
                return saa > 0 && sbb > 0 ? sab / Math.Sqrt(saa * sbb) : -1;
            };
            var best = new Result { Score = double.NegativeInfinity };
            double otherBest = double.NegativeInfinity;
            foreach (bool m in new[] { false, true })
            {
                double bestA = 0, bestS = double.NegativeInfinity;
                for (double ang = 0; ang < 360; ang += 5) { double s = score(ang, m); if (s > bestS) { bestS = s; bestA = ang; } }
                foreach (double step in new[] { 1.0, 0.25 })
                {
                    double c = bestA;
                    for (double ang = c - 5 * step; ang <= c + 5 * step; ang += step) { double s = score(ang, m); if (s > bestS) { bestS = s; bestA = ang; } }
                }
                bestA = ((bestA % 360) + 360) % 360;
                // unmirrored is kept when the two match alike (seen almost from the equator they cannot be told apart)
                if (bestS > best.Score + 0.01 || double.IsNegativeInfinity(best.Score)) { otherBest = best.Score; best = new Result { PoleAngleDeg = bestA, Mirrored = m, Score = bestS }; }
                else otherBest = Math.Max(otherBest, bestS);
            }
            best.OtherMirrorScore = otherBest;
            return best;
        }
    }
}
