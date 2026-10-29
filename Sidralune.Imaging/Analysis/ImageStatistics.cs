using System;
using System.Threading.Tasks;
using Sidralune.Core.Mathematics;

namespace Sidralune.Imaging.Analysis
{
    public class ChannelStats
    {
        public double Min, Max, Mean, Median, StdDev, Mad, Noise;
        /// <summary>Robust background (sigma-clipped median) and its sigma.</summary>
        public double Background, BackgroundSigma;
        public long Saturated;
        public int[] Histogram;
        public override string ToString() => string.Format("min {0:0.0000} max {1:0.0000} mean {2:0.0000} median {3:0.0000} σ {4:0.0000} MAD {5:0.0000} noise {6:0.00000}", Min, Max, Mean, Median, StdDev, Mad, Noise);
    }

    public static class ImageStatistics
    {
        /// <summary>Statistics of a plane. Uses subsampling for median/MAD on large frames.</summary>
        public static ChannelStats Compute(float[] d, int width, int histBins = 1024, double saturation = 0.999)
        {
            var s = new ChannelStats { Min = double.MaxValue, Max = double.MinValue };
            int n = d.Length;
            double sum = 0, sum2 = 0;
            long sat = 0, count = 0;
            object lk = new object();
            Parallel.For(0, (n + 65535) / 65536, Core.Util.StopRequest.Options, () => new double[5] { double.MaxValue, double.MinValue, 0, 0, 0 }, (blk, st, acc) =>
            {
                int a = blk * 65536, b = Math.Min(n, a + 65536);
                for (int i = a; i < b; i++)
                {
                    float v = d[i];
                    if (float.IsNaN(v)) continue;
                    if (v < acc[0]) acc[0] = v;
                    if (v > acc[1]) acc[1] = v;
                    acc[2] += v; acc[3] += (double)v * v;
                    if (v >= saturation) acc[4]++;
                }
                return acc;
            }, acc =>
            {
                lock (lk)
                {
                    if (acc[0] < s.Min) s.Min = acc[0];
                    if (acc[1] > s.Max) s.Max = acc[1];
                    sum += acc[2]; sum2 += acc[3]; sat += (long)acc[4];
                }
            });
            count = n;
            s.Mean = sum / Math.Max(1, count);
            s.StdDev = Math.Sqrt(Math.Max(0, sum2 / Math.Max(1, count) - s.Mean * s.Mean));
            s.Saturated = sat;
            int step = Math.Max(1, n / 500000);
            int m = (n + step - 1) / step;
            var sample = new float[m];
            int k = 0;
            for (int i = 0; i < n && k < m; i += step) { float v = d[i]; if (!float.IsNaN(v)) sample[k++] = v; }
            s.Median = Stats.MedianInPlace(sample, k);
            for (int i = 0; i < k; i++) sample[i] = Math.Abs(sample[i] - (float)s.Median);
            s.Mad = Stats.MedianInPlace(sample, k);
            // sigma-clipped background
            double bg = s.Median, sig = s.Mad * Stats.MadToSigma;
            for (int it = 0; it < 4; it++)
            {
                k = 0;
                for (int i = 0; i < n && k < m; i += step) { float v = d[i]; if (!float.IsNaN(v) && Math.Abs(v - bg) < 3 * sig + 1e-12) sample[k++] = v; }
                if (k < 10) break;
                bg = Stats.MedianInPlace(sample, k);
                for (int i = 0; i < k; i++) sample[i] = Math.Abs(sample[i] - (float)bg);
                sig = Stats.MedianInPlace(sample, k) * Stats.MadToSigma;
            }
            s.Background = bg;
            s.BackgroundSigma = sig;
            s.Noise = MrsNoise(d, width, d.Length / width);
            if (histBins > 0)
            {
                double lo = Math.Min(0, s.Min), hi = Math.Max(1, s.Max);
                s.Histogram = Stats.Histogram(d, histBins, lo, hi);
            }
            return s;
        }

        /// <summary>
        /// Noise estimate from the first à trous wavelet layer (B3 spline), with iterative k-sigma clipping —
        /// the essence of the multiresolution support noise estimator (Starck &amp; Murtagh).
        /// </summary>
        public static double MrsNoise(float[] d, int w, int h)
        {
            if (w < 8 || h < 8) return 0;
            var smooth = Wavelets.AtrousSmooth(d, w, h, 1);
            int step = Math.Max(1, d.Length / 400000);
            var res = new float[d.Length / step + 1];
            int k = 0;
            for (int i = 0; i < d.Length; i += step) res[k++] = d[i] - smooth[i];
            double sigma = Stats.StdDev(res, k, 0);
            for (int it = 0; it < 5; it++)
            {
                int kk = 0;
                double s2 = 0;
                for (int i = 0; i < k; i++) if (Math.Abs(res[i]) < 3 * sigma) { s2 += res[i] * res[i]; kk++; }
                if (kk < 10) break;
                double ns = Math.Sqrt(s2 / kk);
                if (Math.Abs(ns - sigma) < 1e-4 * sigma) { sigma = ns; break; }
                sigma = ns;
            }
            // first wavelet layer of B3 spline holds ~0.889 of white Gaussian noise sigma
            return sigma / 0.889;
        }

        /// <summary>SNR estimate (PixInsight-like): (median - background floor)/noise; purely a ranking metric.</summary>
        public static double SnrEstimate(ChannelStats s) => s.Noise > 0 ? (s.Mean - s.Background + s.BackgroundSigma) / s.Noise : 0;
    }

    /// <summary>Mesh-based background estimation (SExtractor style) with bilinear interpolation.</summary>
    public static class BackgroundMesh
    {
        /// <summary>Returns a full-resolution background map and the global background RMS.</summary>
        public static float[] Estimate(float[] d, int w, int h, int cell, out float[] rmsMap, double kappa = 2.5)
        {
            int nx = Math.Max(1, w / cell), ny = Math.Max(1, h / cell);
            var bg = new float[nx * ny];
            var rms = new float[nx * ny];
            const int maxSamples = 4096;
            Parallel.For(0, ny, Core.Util.StopRequest.Options, j =>
            {
                float[] buf = null;
                for (int i = 0; i < nx; i++)
                {
                    int x0 = i * w / nx, x1 = (i + 1) * w / nx, y0 = j * h / ny, y1 = (j + 1) * h / ny;
                    // the same stride in x and y, so a large cell is thinned evenly instead of losing its last rows
                    int step = Math.Max(1, (int)Math.Ceiling(Math.Sqrt((x1 - x0) * (double)(y1 - y0) / maxSamples)));
                    int need = ((x1 - x0 + step - 1) / step) * ((y1 - y0 + step - 1) / step);
                    if (buf == null || buf.Length < need) buf = new float[need];
                    int k = 0;
                    for (int y = y0; y < y1; y += step)
                        for (int x = x0; x < x1; x += step)
                        {
                            float v = d[y * w + x];
                            if (!float.IsNaN(v)) buf[k++] = v;
                        }
                    float med = Stats.MedianInPlace(buf, k);
                    float sig = 0;
                    for (int it = 0; it < 3 && k > 5; it++)
                    {
                        // MAD-based sigma, then clip
                        var tmp = new float[k];
                        for (int q = 0; q < k; q++) tmp[q] = Math.Abs(buf[q] - med);
                        sig = Stats.MedianInPlace(tmp, k) * 1.4826f;
                        int kk = 0;
                        for (int q = 0; q < k; q++) if (Math.Abs(buf[q] - med) <= kappa * sig + 1e-12f) buf[kk++] = buf[q];
                        if (kk == k) break;
                        k = kk;
                        med = Stats.MedianInPlace(buf, k);
                    }
                    // SExtractor mode estimate is unstable in crowded fields; median is robust
                    bg[j * nx + i] = med;
                    rms[j * nx + i] = sig;
                }
            });
            // 3x3 median filter of the mesh to reject cells dominated by bright objects
            bg = MedianFilterMesh(bg, nx, ny);
            rms = MedianFilterMesh(rms, nx, ny);
            rmsMap = Interpolate(rms, nx, ny, w, h);
            return Interpolate(bg, nx, ny, w, h);
        }

        private static float[] MedianFilterMesh(float[] m, int nx, int ny)
        {
            var r = new float[m.Length];
            var buf = new float[9];
            for (int j = 0; j < ny; j++)
                for (int i = 0; i < nx; i++)
                {
                    int k = 0;
                    // the edge is repeated outside the mesh: a lopsided neighbourhood at the border would pull a
                    // gradient towards the centre by half a cell
                    for (int dj = -1; dj <= 1; dj++)
                        for (int di = -1; di <= 1; di++)
                        {
                            int ii = Math.Max(0, Math.Min(nx - 1, i + di)), jj = Math.Max(0, Math.Min(ny - 1, j + dj));
                            buf[k++] = m[jj * nx + ii];
                        }
                    r[j * nx + i] = Stats.MedianInPlace(buf, k);
                }
            return r;
        }

        public static float[] Interpolate(float[] mesh, int nx, int ny, int w, int h)
        {
            var o = new float[w * h];
            Parallel.For(0, h, Core.Util.StopRequest.Options, y =>
            {
                double gy = (y + 0.5) * ny / h - 0.5;
                int j0 = (int)Math.Floor(gy);
                double fy = gy - j0;
                int ja = Math.Max(0, Math.Min(ny - 1, j0)), jb = Math.Max(0, Math.Min(ny - 1, j0 + 1));
                for (int x = 0; x < w; x++)
                {
                    double gx = (x + 0.5) * nx / w - 0.5;
                    int i0 = (int)Math.Floor(gx);
                    double fx = gx - i0;
                    int ia = Math.Max(0, Math.Min(nx - 1, i0)), ib = Math.Max(0, Math.Min(nx - 1, i0 + 1));
                    o[y * w + x] = (float)(mesh[ja * nx + ia] * (1 - fx) * (1 - fy) + mesh[ja * nx + ib] * fx * (1 - fy) + mesh[jb * nx + ia] * (1 - fx) * fy + mesh[jb * nx + ib] * fx * fy);
                }
            });
            return o;
        }
    }
}
