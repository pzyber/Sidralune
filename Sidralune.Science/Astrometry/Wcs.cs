using System;
using System.Collections.Generic;
using System.Globalization;
using Sidralune.Core.Mathematics;
using Sidralune.Imaging;

namespace Sidralune.Science.Astrometry
{
    /// <summary>
    /// World coordinate system: gnomonic (TAN) projection with CD matrix and optional SIP distortion polynomials.
    /// Public methods use Sidralune pixel coordinates (0-based, y down, as stored in AstroImage); FITS keywords use
    /// the FITS convention (1-based, y up) and are converted on read/write.
    /// </summary>
    public class Wcs
    {
        public double CrVal1, CrVal2;      // deg, reference RA/Dec
        public double CrPix1, CrPix2;      // FITS pixel (1-based, y up)
        public double Cd11, Cd12, Cd21, Cd22; // deg/pixel
        public int ImageWidth, ImageHeight;
        public int SipOrder;
        public double[,] A, B;             // forward SIP: pixel -> intermediate
        public int SipInvOrder;
        public double[,] AP, BP;           // inverse SIP
        public double Equinox = 2000.0;
        public int MatchedStars;
        public double RmsArcsec = double.NaN;

        public bool IsValid => !double.IsNaN(CrVal1) && (Cd11 != 0 || Cd12 != 0) && Math.Abs(Cd11 * Cd22 - Cd12 * Cd21) > 0;

        /// <summary>Pixel scale (arcsec/pixel).</summary>
        public double PixelScale => Math.Sqrt(Math.Abs(Cd11 * Cd22 - Cd12 * Cd21)) * 3600.0;

        /// <summary>
        /// Image rotation in the FITS CROTA2 sense (deg): angle of celestial north from the image +y axis (FITS, y up),
        /// derived from CD = [[c1 cosθ, -c2 sinθ], [c1 sinθ, c2 cosθ]] with c1 = -scale for the normal sky orientation.
        /// </summary>
        public double RotationDeg => AstroMath.NormalizeDeg((Determinant < 0 ? Math.Atan2(-Cd21, Cd22) : Math.Atan2(Cd21, Cd22)) * AstroMath.Rad2Deg);

        public double Determinant => Cd11 * Cd22 - Cd12 * Cd21;
        /// <summary>True when the image is mirrored relative to the sky (east-west flipped with north up).</summary>
        public bool Flipped => Determinant > 0;

        public double FieldWidthDeg => ImageWidth * PixelScale / 3600.0;
        public double FieldHeightDeg => ImageHeight * PixelScale / 3600.0;

        private void ToFits(double x, double y, out double fx, out double fy) { fx = x + 1; fy = ImageHeight - y; }
        private void FromFits(double fx, double fy, out double x, out double y) { x = fx - 1; y = ImageHeight - fy; }

        private static double Poly(double[,] c, int order, double u, double v)
        {
            if (c == null) return 0;
            double s = 0;
            for (int p = 0; p <= order; p++)
                for (int q = 0; q <= order - p; q++)
                    if (c[p, q] != 0) s += c[p, q] * Math.Pow(u, p) * Math.Pow(v, q);
            return s;
        }

        /// <summary>Pixel (Sidralune coords) to RA/Dec (degrees, ICRS).</summary>
        public void PixelToWorld(double x, double y, out double raDeg, out double decDeg)
        {
            double fx, fy;
            ToFits(x, y, out fx, out fy);
            double u = fx - CrPix1, v = fy - CrPix2;
            if (SipOrder > 0) { double du = Poly(A, SipOrder, u, v), dv = Poly(B, SipOrder, u, v); u += du; v += dv; }
            double xi = (Cd11 * u + Cd12 * v) * AstroMath.Deg2Rad, eta = (Cd21 * u + Cd22 * v) * AstroMath.Deg2Rad;
            StandardToSpherical(xi, eta, CrVal1 * AstroMath.Deg2Rad, CrVal2 * AstroMath.Deg2Rad, out raDeg, out decDeg);
        }

        /// <summary>RA/Dec (degrees) to pixel; returns false if the point is behind the tangent plane.</summary>
        public bool WorldToPixel(double raDeg, double decDeg, out double x, out double y)
        {
            double xi, eta;
            x = y = double.NaN;
            if (!SphericalToStandard(raDeg * AstroMath.Deg2Rad, decDeg * AstroMath.Deg2Rad, CrVal1 * AstroMath.Deg2Rad, CrVal2 * AstroMath.Deg2Rad, out xi, out eta)) return false;
            xi *= AstroMath.Rad2Deg; eta *= AstroMath.Rad2Deg;
            double det = Determinant;
            double u = (Cd22 * xi - Cd12 * eta) / det, v = (-Cd21 * xi + Cd11 * eta) / det;
            if (SipInvOrder > 0) { double du = Poly(AP, SipInvOrder, u, v), dv = Poly(BP, SipInvOrder, u, v); u += du; v += dv; }
            else if (SipOrder > 0)
            {
                // iterate the forward polynomial
                double uu = u, vv = v;
                for (int i = 0; i < 10; i++)
                {
                    double du = Poly(A, SipOrder, uu, vv), dv = Poly(B, SipOrder, uu, vv);
                    uu = u - du; vv = v - dv;
                }
                u = uu; v = vv;
            }
            FromFits(u + CrPix1, v + CrPix2, out x, out y);
            return true;
        }

        /// <summary>Gnomonic projection: spherical (radians) -> standard coordinates (radians).</summary>
        public static bool SphericalToStandard(double ra, double dec, double ra0, double dec0, out double xi, out double eta)
        {
            double cd = Math.Cos(dec), sd = Math.Sin(dec), cd0 = Math.Cos(dec0), sd0 = Math.Sin(dec0), da = ra - ra0;
            double den = sd * sd0 + cd * cd0 * Math.Cos(da);
            xi = eta = 0;
            if (den <= 1e-9) return false;
            xi = cd * Math.Sin(da) / den;
            eta = (sd * cd0 - cd * sd0 * Math.Cos(da)) / den;
            return true;
        }

        public static void StandardToSpherical(double xi, double eta, double ra0, double dec0, out double raDeg, out double decDeg)
        {
            double cd0 = Math.Cos(dec0), sd0 = Math.Sin(dec0);
            double den = cd0 - eta * sd0;
            double ra = ra0 + Math.Atan2(xi, den);
            double dec = Math.Atan2(sd0 + eta * cd0, Math.Sqrt(xi * xi + den * den));
            raDeg = AstroMath.NormalizeDeg(ra * AstroMath.Rad2Deg);
            decDeg = dec * AstroMath.Rad2Deg;
        }

        /// <summary>Centre of the image in RA/Dec (deg).</summary>
        public void Centre(out double ra, out double dec) => PixelToWorld((ImageWidth - 1) / 2.0, (ImageHeight - 1) / 2.0, out ra, out dec);

        // ---------------------------------------------------------------- FITS keywords

        public static Wcs FromHeader(FitsHeader h, int width, int height)
        {
            if (!h.Has("CRVAL1") || !h.Has("CRPIX1")) return null;
            var w = new Wcs
            {
                CrVal1 = h.GetDouble("CRVAL1"), CrVal2 = h.GetDouble("CRVAL2"),
                CrPix1 = h.GetDouble("CRPIX1"), CrPix2 = h.GetDouble("CRPIX2"),
                ImageWidth = width, ImageHeight = height
            };
            if (h.Has("CD1_1"))
            {
                w.Cd11 = h.GetDouble("CD1_1", 0); w.Cd12 = h.GetDouble("CD1_2", 0);
                w.Cd21 = h.GetDouble("CD2_1", 0); w.Cd22 = h.GetDouble("CD2_2", 0);
            }
            else if (h.Has("CDELT1"))
            {
                double c1 = h.GetDouble("CDELT1"), c2 = h.GetDouble("CDELT2");
                if (h.Has("PC1_1"))
                {
                    w.Cd11 = c1 * h.GetDouble("PC1_1", 1); w.Cd12 = c1 * h.GetDouble("PC1_2", 0);
                    w.Cd21 = c2 * h.GetDouble("PC2_1", 0); w.Cd22 = c2 * h.GetDouble("PC2_2", 1);
                }
                else
                {
                    double rot = h.GetDouble("CROTA2", 0) * AstroMath.Deg2Rad;
                    w.Cd11 = c1 * Math.Cos(rot); w.Cd12 = -c2 * Math.Sin(rot);
                    w.Cd21 = c1 * Math.Sin(rot); w.Cd22 = c2 * Math.Cos(rot);
                }
            }
            else return null;
            w.SipOrder = h.GetInt("A_ORDER", 0);
            if (w.SipOrder > 0)
            {
                w.A = new double[w.SipOrder + 1, w.SipOrder + 1]; w.B = new double[w.SipOrder + 1, w.SipOrder + 1];
                for (int p = 0; p <= w.SipOrder; p++)
                    for (int q = 0; q <= w.SipOrder - p; q++)
                    {
                        w.A[p, q] = h.GetDouble("A_" + p + "_" + q, 0);
                        w.B[p, q] = h.GetDouble("B_" + p + "_" + q, 0);
                    }
            }
            w.SipInvOrder = h.GetInt("AP_ORDER", 0);
            if (w.SipInvOrder > 0)
            {
                w.AP = new double[w.SipInvOrder + 1, w.SipInvOrder + 1]; w.BP = new double[w.SipInvOrder + 1, w.SipInvOrder + 1];
                for (int p = 0; p <= w.SipInvOrder; p++)
                    for (int q = 0; q <= w.SipInvOrder - p; q++)
                    {
                        w.AP[p, q] = h.GetDouble("AP_" + p + "_" + q, 0);
                        w.BP[p, q] = h.GetDouble("BP_" + p + "_" + q, 0);
                    }
            }
            return w.IsValid ? w : null;
        }

        public void WriteToHeader(FitsHeader h)
        {
            foreach (var k in new[] { "CDELT1", "CDELT2", "CROTA1", "CROTA2", "PC1_1", "PC1_2", "PC2_1", "PC2_2" }) h.Remove(k);
            h.Set("CTYPE1", SipOrder > 0 ? "RA---TAN-SIP" : "RA---TAN");
            h.Set("CTYPE2", SipOrder > 0 ? "DEC--TAN-SIP" : "DEC--TAN");
            h.Set("CUNIT1", "deg"); h.Set("CUNIT2", "deg");
            h.Set("EQUINOX", Equinox);
            h.Set("CRVAL1", CrVal1, "RA of reference point");
            h.Set("CRVAL2", CrVal2, "Dec of reference point");
            h.Set("CRPIX1", CrPix1); h.Set("CRPIX2", CrPix2);
            h.Set("CD1_1", Cd11); h.Set("CD1_2", Cd12); h.Set("CD2_1", Cd21); h.Set("CD2_2", Cd22);
            if (SipOrder > 0)
            {
                h.Set("A_ORDER", SipOrder); h.Set("B_ORDER", SipOrder);
                for (int p = 0; p <= SipOrder; p++)
                    for (int q = 0; q <= SipOrder - p; q++)
                    {
                        if (p + q < 2) continue;
                        h.Set("A_" + p + "_" + q, A[p, q]); h.Set("B_" + p + "_" + q, B[p, q]);
                    }
            }
            if (SipInvOrder > 0)
            {
                h.Set("AP_ORDER", SipInvOrder); h.Set("BP_ORDER", SipInvOrder);
                for (int p = 0; p <= SipInvOrder; p++)
                    for (int q = 0; q <= SipInvOrder - p; q++)
                    {
                        h.Set("AP_" + p + "_" + q, AP[p, q]); h.Set("BP_" + p + "_" + q, BP[p, q]);
                    }
            }
            h.Set("PLTSOLVD", true, "Plate solved by Sidralune");
            h.Set("SCALE", PixelScale, "Pixel scale [arcsec/px]");
        }

        public override string ToString()
        {
            double ra, dec;
            Centre(out ra, out dec);
            return string.Format(CultureInfo.InvariantCulture, "Centre {0} {1}, scale {2:0.000}\"/px, rotation {3:0.00}°{4}, FOV {5:0.00}°×{6:0.00}°",
                AstroMath.FormatHms(ra / 15, 1), AstroMath.FormatDms(dec, 0), PixelScale, RotationDeg, Flipped ? " (flipped)" : "", FieldWidthDeg, FieldHeightDeg);
        }

        /// <summary>
        /// Fit a TAN(+SIP) WCS to matched pixel/sky pairs (Sidralune pixel coords; RA/Dec deg). The tangent point is
        /// placed at the centroid of the matched sky positions; CRPIX is fitted.
        /// </summary>
        public static Wcs Fit(IList<double> x, IList<double> y, IList<double> ra, IList<double> dec, int width, int height, int sipOrder = 0)
        {
            int n = x.Count;
            if (n < 3) return null;
            // tangent point: mean direction
            double sx = 0, sy = 0, sz = 0;
            for (int i = 0; i < n; i++) { var v = Vec3.FromSpherical(ra[i] * AstroMath.Deg2Rad, dec[i] * AstroMath.Deg2Rad); sx += v.X; sy += v.Y; sz += v.Z; }
            double ra0, dec0;
            new Vec3(sx, sy, sz).ToSpherical(out ra0, out dec0);
            var w = new Wcs { ImageWidth = width, ImageHeight = height, CrVal1 = ra0 * AstroMath.Rad2Deg, CrVal2 = dec0 * AstroMath.Rad2Deg };
            var xi = new double[n]; var eta = new double[n]; var fx = new double[n]; var fy = new double[n];
            for (int i = 0; i < n; i++)
            {
                double a, b;
                SphericalToStandard(ra[i] * AstroMath.Deg2Rad, dec[i] * AstroMath.Deg2Rad, ra0, dec0, out a, out b);
                xi[i] = a * AstroMath.Rad2Deg; eta[i] = b * AstroMath.Rad2Deg;
                fx[i] = x[i] + 1; fy[i] = height - y[i];
            }
            // linear: xi = a fx + b fy + c ; eta = d fx + e fy + f
            var p = LinAlg.FitAffine(fx, fy, xi, eta);
            if (p == null) return null;
            w.Cd11 = p[0]; w.Cd12 = p[1]; w.Cd21 = p[3]; w.Cd22 = p[4];
            // crpix where xi = eta = 0
            double det = w.Cd11 * w.Cd22 - w.Cd12 * w.Cd21;
            w.CrPix1 = (-p[2] * w.Cd22 + p[5] * w.Cd12) / det;
            w.CrPix2 = (-p[5] * w.Cd11 + p[2] * w.Cd21) / det;
            // move CRPIX to image centre for numerical niceness
            double cra, cdec;
            w.PixelToWorld((width - 1) / 2.0, (height - 1) / 2.0, out cra, out cdec);
            w = Refit(x, y, ra, dec, width, height, cra, cdec, sipOrder);
            return w;
        }

        private static Wcs Refit(IList<double> x, IList<double> y, IList<double> ra, IList<double> dec, int width, int height, double ra0d, double dec0d, int sipOrder)
        {
            int n = x.Count;
            var w = new Wcs { ImageWidth = width, ImageHeight = height, CrVal1 = ra0d, CrVal2 = dec0d, CrPix1 = (width + 1) / 2.0, CrPix2 = (height + 1) / 2.0 };
            double ra0 = ra0d * AstroMath.Deg2Rad, dec0 = dec0d * AstroMath.Deg2Rad;
            var u = new double[n]; var v = new double[n]; var xi = new double[n]; var eta = new double[n];
            for (int i = 0; i < n; i++)
            {
                double a, b;
                SphericalToStandard(ra[i] * AstroMath.Deg2Rad, dec[i] * AstroMath.Deg2Rad, ra0, dec0, out a, out b);
                xi[i] = a * AstroMath.Rad2Deg; eta[i] = b * AstroMath.Rad2Deg;
                u[i] = x[i] + 1 - w.CrPix1; v[i] = height - y[i] - w.CrPix2;
            }
            var p = LinAlg.FitAffine(u, v, xi, eta);
            if (p == null) return null;
            w.Cd11 = p[0]; w.Cd12 = p[1]; w.Cd21 = p[3]; w.Cd22 = p[4];
            // absorb the residual constant into CRVAL by re-projecting the offset
            double det = w.Determinant;
            double du = (-p[2] * w.Cd22 + p[5] * w.Cd12) / det, dv = (-p[5] * w.Cd11 + p[2] * w.Cd21) / det;
            w.CrPix1 += du; w.CrPix2 += dv;
            if (sipOrder >= 2 && n >= 3 * (sipOrder + 1) * (sipOrder + 2) / 2)
            {
                // SIP: (u + A(u,v), v + B(u,v)) = CD^-1 (xi, eta)
                w.SipOrder = sipOrder;
                var terms = new List<int[]>();
                for (int pp = 0; pp <= sipOrder; pp++) for (int q = 0; q <= sipOrder - pp; q++) if (pp + q >= 2) terms.Add(new[] { pp, q });
                var D = new double[n, terms.Count];
                var ru = new double[n]; var rv = new double[n];
                for (int i = 0; i < n; i++)
                {
                    double uu = x[i] + 1 - w.CrPix1, vv = height - y[i] - w.CrPix2;
                    double tu = (w.Cd22 * xi[i] - w.Cd12 * eta[i]) / det, tv = (-w.Cd21 * xi[i] + w.Cd11 * eta[i]) / det;
                    for (int k = 0; k < terms.Count; k++) D[i, k] = Math.Pow(uu, terms[k][0]) * Math.Pow(vv, terms[k][1]);
                    ru[i] = tu - uu; rv[i] = tv - vv;
                }
                var ca = LinAlg.LeastSquares(D, ru); var cb = LinAlg.LeastSquares(D, rv);
                if (ca != null && cb != null)
                {
                    w.A = new double[sipOrder + 1, sipOrder + 1]; w.B = new double[sipOrder + 1, sipOrder + 1];
                    for (int k = 0; k < terms.Count; k++) { w.A[terms[k][0], terms[k][1]] = ca[k]; w.B[terms[k][0], terms[k][1]] = cb[k]; }
                    w.FitInverseSip();
                }
                else w.SipOrder = 0;
            }
            // statistics
            double se = 0;
            for (int i = 0; i < n; i++)
            {
                double r2, d2;
                w.PixelToWorld(x[i], y[i], out r2, out d2);
                double s = AstroMath.Separation(r2 * AstroMath.Deg2Rad, d2 * AstroMath.Deg2Rad, ra[i] * AstroMath.Deg2Rad, dec[i] * AstroMath.Deg2Rad) * AstroMath.Rad2ArcSec;
                se += s * s;
            }
            w.RmsArcsec = Math.Sqrt(se / n);
            w.MatchedStars = n;
            return w;
        }

        /// <summary>Fit inverse SIP polynomials on a grid (order = forward order + 1).</summary>
        public void FitInverseSip()
        {
            if (SipOrder <= 0) return;
            int order = SipOrder + 1;
            var terms = new List<int[]>();
            for (int p = 0; p <= order; p++) for (int q = 0; q <= order - p; q++) terms.Add(new[] { p, q });
            var rows = new List<double[]>(); var tu = new List<double>(); var tv = new List<double>();
            for (int gy = 0; gy <= 20; gy++)
                for (int gx = 0; gx <= 20; gx++)
                {
                    double u = (gx / 20.0 - 0.5) * ImageWidth * 1.1 + (ImageWidth + 1) / 2.0 - CrPix1;
                    double v = (gy / 20.0 - 0.5) * ImageHeight * 1.1 + (ImageHeight + 1) / 2.0 - CrPix2;
                    double U = u + Poly(A, SipOrder, u, v), V = v + Poly(B, SipOrder, u, v);
                    var row = new double[terms.Count];
                    for (int k = 0; k < terms.Count; k++) row[k] = Math.Pow(U, terms[k][0]) * Math.Pow(V, terms[k][1]);
                    rows.Add(row); tu.Add(u - U); tv.Add(v - V);
                }
            var D = new double[rows.Count, terms.Count];
            for (int i = 0; i < rows.Count; i++) for (int k = 0; k < terms.Count; k++) D[i, k] = rows[i][k];
            var a = LinAlg.LeastSquares(D, tu.ToArray()); var b = LinAlg.LeastSquares(D, tv.ToArray());
            if (a == null || b == null) return;
            SipInvOrder = order;
            AP = new double[order + 1, order + 1]; BP = new double[order + 1, order + 1];
            for (int k = 0; k < terms.Count; k++) { AP[terms[k][0], terms[k][1]] = a[k]; BP[terms[k][0], terms[k][1]] = b[k]; }
        }

        /// <summary>A simple WCS from centre, scale (arcsec/px) and CROTA2-style rotation (deg), e.g. for framing.</summary>
        public static Wcs Create(double raDeg, double decDeg, double scaleArcsec, double rotationDeg, int width, int height, bool flipped = false)
        {
            double sc = scaleArcsec / 3600.0, r = rotationDeg * AstroMath.Deg2Rad;
            double c1 = flipped ? sc : -sc, c2 = sc;
            return new Wcs
            {
                CrVal1 = raDeg, CrVal2 = decDeg, CrPix1 = (width + 1) / 2.0, CrPix2 = (height + 1) / 2.0, ImageWidth = width, ImageHeight = height,
                Cd11 = c1 * Math.Cos(r), Cd12 = -c2 * Math.Sin(r), Cd21 = c1 * Math.Sin(r), Cd22 = c2 * Math.Cos(r)
            };
        }
    }
}
