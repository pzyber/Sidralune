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
            Parallel.For(0, nr, r =>
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
    }

    /// <summary>
    /// WinJUPOS-style derotation: re-projects a planetary image as if taken at a different time by rotating the
    /// globe about its axis (orthographic projection with oblateness), allowing long stacks/RGB composites without smear.
    /// </summary>
    public static class SphereDerotation
    {
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

        /// <summary>
        /// Derotate by dtSeconds (positive: show the planet as it would appear dtSeconds later).
        /// Pixels whose source lies on the hidden hemisphere are filled from the original image (or 0 if fillOriginal=false).
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
            for (int c = 0; c < img.Channels; c++)
            {
                var src = img.Data[c]; var dst = o.Data[c];
                Parallel.For(0, h, y =>
                {
                    for (int x = 0; x < w; x++)
                    {
                        // image -> disc frame aligned with the projected pole (u towards +x, i.e. the west limb when north is up, v towards north)
                        double dx = (x - g.Cx) * mir, dy = -(y - g.Cy);
                        double u = dx * ca + dy * sa, v = -dx * sa + dy * ca;
                        int i = y * w + x;
                        // ellipsoid in observer frame: invert orthographic projection for an oblate spheroid tilted by D
                        double X, Y, Z;
                        if (!Unproject(u, v, Re, Rp, cD, sD, out X, out Y, out Z)) { dst[i] = fillOriginal ? src[i] : 0; continue; }
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
            System.Threading.CancellationToken ct = default(System.Threading.CancellationToken))
        {
            var acc = images[0].CloneEmpty();
            var wsum = new float[acc.PixelCount];
            for (int k = 0; k < images.Count; k++)
            {
                ct.ThrowIfCancellationRequested(); // a stop takes effect between images
                double dt = (target - times[k]).TotalSeconds;
                var d = Derotate(images[k], geoms[k], dt, periodHours, false);
                // align disc centres to the first geometry
                var shifted = Geometry.Shift(d, geoms[0].Cx - geoms[k].Cx, geoms[0].Cy - geoms[k].Cy);
                for (int c = 0; c < acc.Channels; c++)
                    for (int i = 0; i < acc.PixelCount; i++)
                    {
                        float v = shifted.Data[Math.Min(c, shifted.Channels - 1)][i];
                        if (v > 0) { acc.Data[c][i] += v; if (c == 0) wsum[i] += 1; }
                    }
            }
            for (int c = 0; c < acc.Channels; c++) for (int i = 0; i < acc.PixelCount; i++) acc.Data[c][i] = wsum[i] > 0 ? acc.Data[c][i] / wsum[i] : 0;
            return acc;
        }
    }
}
