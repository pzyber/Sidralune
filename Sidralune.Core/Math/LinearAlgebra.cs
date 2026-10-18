using System;
using System.Collections.Generic;

namespace Sidralune.Core.Mathematics
{
    /// <summary>Dense linear algebra helpers (row-major double[,]).</summary>
    public static class LinAlg
    {
        /// <summary>Solve A x = b with partial-pivot Gaussian elimination. A and b are copied.</summary>
        public static double[] Solve(double[,] A, double[] b)
        {
            int n = b.Length;
            var a = (double[,])A.Clone();
            var x = (double[])b.Clone();
            for (int k = 0; k < n; k++)
            {
                int piv = k; double mx = Math.Abs(a[k, k]);
                for (int i = k + 1; i < n; i++) if (Math.Abs(a[i, k]) > mx) { mx = Math.Abs(a[i, k]); piv = i; }
                if (mx < 1e-300) return null;
                if (piv != k)
                {
                    for (int j = 0; j < n; j++) { double t = a[k, j]; a[k, j] = a[piv, j]; a[piv, j] = t; }
                    double tb = x[k]; x[k] = x[piv]; x[piv] = tb;
                }
                for (int i = k + 1; i < n; i++)
                {
                    double f = a[i, k] / a[k, k];
                    if (f == 0) continue;
                    for (int j = k; j < n; j++) a[i, j] -= f * a[k, j];
                    x[i] -= f * x[k];
                }
            }
            for (int i = n - 1; i >= 0; i--)
            {
                double s = x[i];
                for (int j = i + 1; j < n; j++) s -= a[i, j] * x[j];
                x[i] = s / a[i, i];
            }
            return x;
        }

        /// <summary>Matrix inverse via Gauss-Jordan. Returns null if singular.</summary>
        public static double[,] Inverse(double[,] A)
        {
            int n = A.GetLength(0);
            var a = (double[,])A.Clone();
            var inv = new double[n, n];
            for (int i = 0; i < n; i++) inv[i, i] = 1;
            for (int k = 0; k < n; k++)
            {
                int piv = k; double mx = Math.Abs(a[k, k]);
                for (int i = k + 1; i < n; i++) if (Math.Abs(a[i, k]) > mx) { mx = Math.Abs(a[i, k]); piv = i; }
                if (mx < 1e-300) return null;
                if (piv != k)
                    for (int j = 0; j < n; j++)
                    {
                        double t = a[k, j]; a[k, j] = a[piv, j]; a[piv, j] = t;
                        t = inv[k, j]; inv[k, j] = inv[piv, j]; inv[piv, j] = t;
                    }
                double d = a[k, k];
                for (int j = 0; j < n; j++) { a[k, j] /= d; inv[k, j] /= d; }
                for (int i = 0; i < n; i++)
                {
                    if (i == k) continue;
                    double f = a[i, k];
                    if (f == 0) continue;
                    for (int j = 0; j < n; j++) { a[i, j] -= f * a[k, j]; inv[i, j] -= f * inv[k, j]; }
                }
            }
            return inv;
        }

        /// <summary>
        /// Weighted linear least squares via Householder QR on the design matrix (numerically better than normal equations).
        /// rows = observations, cols = parameters. Returns parameter vector; covariance optional.
        /// </summary>
        public static double[] LeastSquares(double[,] design, double[] y, double[] weights, out double[,] covariance)
        {
            int m = design.GetLength(0), n = design.GetLength(1);
            covariance = null;
            if (m < n) return null;
            var A = new double[m, n];
            var b = new double[m];
            for (int i = 0; i < m; i++)
            {
                double w = weights != null ? Math.Sqrt(Math.Max(0, weights[i])) : 1.0;
                for (int j = 0; j < n; j++) A[i, j] = design[i, j] * w;
                b[i] = y[i] * w;
            }
            var rdiag = new double[n];
            for (int k = 0; k < n; k++)
            {
                double nrm = 0;
                for (int i = k; i < m; i++) nrm = Hypot(nrm, A[i, k]);
                if (nrm == 0) return null;
                if (A[k, k] < 0) nrm = -nrm;
                for (int i = k; i < m; i++) A[i, k] /= nrm;
                A[k, k] += 1.0;
                for (int j = k + 1; j < n; j++)
                {
                    double s = 0;
                    for (int i = k; i < m; i++) s += A[i, k] * A[i, j];
                    s = -s / A[k, k];
                    for (int i = k; i < m; i++) A[i, j] += s * A[i, k];
                }
                // apply to b
                {
                    double s = 0;
                    for (int i = k; i < m; i++) s += A[i, k] * b[i];
                    s = -s / A[k, k];
                    for (int i = k; i < m; i++) b[i] += s * A[i, k];
                }
                rdiag[k] = -nrm;
            }
            var x = new double[n];
            for (int k = n - 1; k >= 0; k--)
            {
                double s = b[k];
                for (int j = k + 1; j < n; j++) s -= A[k, j] * x[j];
                if (Math.Abs(rdiag[k]) < 1e-300) return null;
                x[k] = s / rdiag[k];
            }
            // covariance = (R^T R)^-1 ; build R explicitly
            var R = new double[n, n];
            for (int i = 0; i < n; i++)
            {
                R[i, i] = rdiag[i];
                for (int j = i + 1; j < n; j++) R[i, j] = A[i, j];
            }
            var RtR = new double[n, n];
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                {
                    double s = 0;
                    for (int k = 0; k < n; k++) s += R[k, i] * R[k, j];
                    RtR[i, j] = s;
                }
            covariance = Inverse(RtR);
            return x;
        }

        public static double[] LeastSquares(double[,] design, double[] y, double[] weights = null)
        {
            double[,] cov;
            return LeastSquares(design, y, weights, out cov);
        }

        private static double Hypot(double a, double b)
        {
            double r;
            if (Math.Abs(a) > Math.Abs(b)) { r = b / a; return Math.Abs(a) * Math.Sqrt(1 + r * r); }
            if (b != 0) { r = a / b; return Math.Abs(b) * Math.Sqrt(1 + r * r); }
            return 0;
        }

        /// <summary>Jacobi eigen decomposition of a symmetric matrix. Eigenvectors are columns of V.</summary>
        public static void SymmetricEigen(double[,] S, out double[] values, out double[,] V)
        {
            int n = S.GetLength(0);
            var a = (double[,])S.Clone();
            V = new double[n, n];
            for (int i = 0; i < n; i++) V[i, i] = 1;
            for (int sweep = 0; sweep < 100; sweep++)
            {
                double off = 0;
                for (int i = 0; i < n; i++) for (int j = i + 1; j < n; j++) off += a[i, j] * a[i, j];
                if (off < 1e-22) break;
                for (int p = 0; p < n; p++)
                    for (int q = p + 1; q < n; q++)
                    {
                        if (Math.Abs(a[p, q]) < 1e-300) continue;
                        double theta = (a[q, q] - a[p, p]) / (2 * a[p, q]);
                        double t = Math.Sign(theta) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1));
                        if (theta == 0) t = 1;
                        double c = 1 / Math.Sqrt(t * t + 1), s = t * c;
                        for (int k = 0; k < n; k++)
                        {
                            double akp = a[k, p], akq = a[k, q];
                            a[k, p] = c * akp - s * akq;
                            a[k, q] = s * akp + c * akq;
                        }
                        for (int k = 0; k < n; k++)
                        {
                            double apk = a[p, k], aqk = a[q, k];
                            a[p, k] = c * apk - s * aqk;
                            a[q, k] = s * apk + c * aqk;
                        }
                        for (int k = 0; k < n; k++)
                        {
                            double vkp = V[k, p], vkq = V[k, q];
                            V[k, p] = c * vkp - s * vkq;
                            V[k, q] = s * vkp + c * vkq;
                        }
                    }
            }
            values = new double[n];
            for (int i = 0; i < n; i++) values[i] = a[i, i];
        }

        /// <summary>
        /// Solve a 3x3 homogeneous-free system for a 2D affine transform mapping (x,y)->(u,v) by least squares.
        /// Returns [a,b,c,d,e,f] with u = a x + b y + c, v = d x + e y + f.
        /// </summary>
        public static double[] FitAffine(IList<double> x, IList<double> y, IList<double> u, IList<double> v, IList<double> w = null)
        {
            int m = x.Count;
            if (m < 3) return null;
            var D = new double[m, 3];
            var wu = new double[m];
            for (int i = 0; i < m; i++) { D[i, 0] = x[i]; D[i, 1] = y[i]; D[i, 2] = 1; wu[i] = w != null ? w[i] : 1; }
            var U = new double[m]; var Vv = new double[m];
            for (int i = 0; i < m; i++) { U[i] = u[i]; Vv[i] = v[i]; }
            var p1 = LeastSquares(D, U, wu);
            var p2 = LeastSquares(D, Vv, wu);
            if (p1 == null || p2 == null) return null;
            return new[] { p1[0], p1[1], p1[2], p2[0], p2[1], p2[2] };
        }

        /// <summary>
        /// Fit a projective homography (8 params) mapping (x,y)->(u,v) using normalized DLT with linear least squares.
        /// Returns h[9] with h[8]=1.
        /// </summary>
        public static double[] FitHomography(IList<double> x, IList<double> y, IList<double> u, IList<double> v)
        {
            int m = x.Count;
            if (m < 4) return null;
            var D = new double[2 * m, 8];
            var b = new double[2 * m];
            for (int i = 0; i < m; i++)
            {
                double X = x[i], Y = y[i], U = u[i], V = v[i];
                D[2 * i, 0] = X; D[2 * i, 1] = Y; D[2 * i, 2] = 1; D[2 * i, 6] = -X * U; D[2 * i, 7] = -Y * U; b[2 * i] = U;
                D[2 * i + 1, 3] = X; D[2 * i + 1, 4] = Y; D[2 * i + 1, 5] = 1; D[2 * i + 1, 6] = -X * V; D[2 * i + 1, 7] = -Y * V; b[2 * i + 1] = V;
            }
            var p = LeastSquares(D, b);
            if (p == null) return null;
            return new[] { p[0], p[1], p[2], p[3], p[4], p[5], p[6], p[7], 1.0 };
        }

        /// <summary>Fit polynomial y = sum c_k x^k (degree deg) with optional weights.</summary>
        public static double[] PolyFit(IList<double> x, IList<double> y, int deg, IList<double> w = null)
        {
            int m = x.Count, n = deg + 1;
            if (m < n) return null;
            // scale x to [-1,1] for conditioning then convert back
            double xmin = double.MaxValue, xmax = double.MinValue;
            for (int i = 0; i < m; i++) { if (x[i] < xmin) xmin = x[i]; if (x[i] > xmax) xmax = x[i]; }
            double c0 = 0.5 * (xmin + xmax), s0 = xmax > xmin ? 2.0 / (xmax - xmin) : 1.0;
            var D = new double[m, n];
            var Y = new double[m];
            var W = new double[m];
            for (int i = 0; i < m; i++)
            {
                double t = (x[i] - c0) * s0, p = 1;
                for (int j = 0; j < n; j++) { D[i, j] = p; p *= t; }
                Y[i] = y[i];
                W[i] = w != null ? w[i] : 1;
            }
            var q = LeastSquares(D, Y, W);
            if (q == null) return null;
            // expand q(t), t = s0 (x - c0) into powers of x
            var coef = new double[n];
            for (int j = 0; j < n; j++)
            {
                // q_j * s0^j * (x - c0)^j = q_j s0^j sum_k C(j,k) x^k (-c0)^(j-k)
                double qs = q[j] * Math.Pow(s0, j);
                for (int k = 0; k <= j; k++)
                    coef[k] += qs * Binom(j, k) * Math.Pow(-c0, j - k);
            }
            return coef;
        }

        public static double PolyEval(double[] c, double x)
        {
            double r = 0;
            for (int i = c.Length - 1; i >= 0; i--) r = r * x + c[i];
            return r;
        }

        private static double Binom(int n, int k)
        {
            double r = 1;
            for (int i = 1; i <= k; i++) r = r * (n - k + i) / i;
            return r;
        }
    }
}
