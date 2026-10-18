using System;
using System.Collections.Generic;
using System.Linq;
using Sidralune.Core.Mathematics;
using Sidralune.Imaging.Analysis;

namespace Sidralune.Imaging.Registration
{
    public enum TransformModel { Translation, Similarity, Affine, Homography, Polynomial2 }

    public class MatchResult
    {
        /// <summary>Maps target (moving) image coordinates onto reference coordinates.</summary>
        public Transform2D Transform;
        /// <summary>Optional 2nd-order polynomial (x,y) -> (u,v) when Polynomial2 is requested: 6 coefs each.</summary>
        public double[] PolyX, PolyY;
        public int Matches;
        public double RmsError;
        public List<Tuple<DetectedStar, DetectedStar>> Pairs = new List<Tuple<DetectedStar, DetectedStar>>();
        public bool Success => Transform != null && Matches >= 3;

        public void Map(double x, double y, out double u, out double v)
        {
            if (PolyX != null)
            {
                u = PolyX[0] + PolyX[1] * x + PolyX[2] * y + PolyX[3] * x * x + PolyX[4] * x * y + PolyX[5] * y * y;
                v = PolyY[0] + PolyY[1] * x + PolyY[2] * y + PolyY[3] * x * x + PolyY[4] * x * y + PolyY[5] * y * y;
                return;
            }
            Transform.Apply(x, y, out u, out v);
        }
    }

    /// <summary>
    /// Triangle-similarity star pattern matching (Valdes et al. 1995; Beroiz et al. 2020 "astroalign" variant using
    /// each star's nearest neighbours), with RANSAC robust model fitting.
    /// </summary>
    public static class StarMatcher
    {
        private struct Tri
        {
            public int A, B, C;      // indices ordered by opposite side length
            public double R1, R2;    // invariants: L1/L0, L2/L0 with L0 the longest side
        }

        public static int MaxStars = 60;
        public static int Neighbours = 5;
        public static double InvariantTolerance = 0.01;

        private static List<Tri> BuildTriangles(List<DetectedStar> s)
        {
            var tris = new List<Tri>();
            var seen = new HashSet<long>();
            int n = s.Count;
            for (int i = 0; i < n; i++)
            {
                var nb = Enumerable.Range(0, n).Where(j => j != i)
                    .OrderBy(j => Sq(s[i].X - s[j].X) + Sq(s[i].Y - s[j].Y)).Take(Neighbours).ToList();
                nb.Add(i);
                for (int a = 0; a < nb.Count; a++)
                    for (int b = a + 1; b < nb.Count; b++)
                        for (int c = b + 1; c < nb.Count; c++)
                        {
                            var ids = new[] { nb[a], nb[b], nb[c] };
                            Array.Sort(ids);
                            long key = ((long)ids[0] << 40) | ((long)ids[1] << 20) | (long)ids[2];
                            if (!seen.Add(key)) continue;
                            var t = MakeTri(s, ids[0], ids[1], ids[2]);
                            if (t.HasValue) tris.Add(t.Value);
                        }
            }
            return tris;
        }

        private static double Sq(double v) => v * v;

        private static Tri? MakeTri(List<DetectedStar> s, int i, int j, int k)
        {
            // side opposite each vertex
            double dij = Math.Sqrt(Sq(s[i].X - s[j].X) + Sq(s[i].Y - s[j].Y));
            double djk = Math.Sqrt(Sq(s[j].X - s[k].X) + Sq(s[j].Y - s[k].Y));
            double dik = Math.Sqrt(Sq(s[i].X - s[k].X) + Sq(s[i].Y - s[k].Y));
            var sides = new[] { Tuple.Create(djk, i), Tuple.Create(dik, j), Tuple.Create(dij, k) }.OrderByDescending(t => t.Item1).ToArray();
            if (sides[2].Item1 < 3) return null; // degenerate
            return new Tri
            {
                A = sides[0].Item2, B = sides[1].Item2, C = sides[2].Item2,
                R1 = sides[1].Item1 / sides[0].Item1, R2 = sides[2].Item1 / sides[0].Item1
            };
        }

        /// <summary>Match target stars to reference stars and compute the transform target -> reference.</summary>
        public static MatchResult Match(List<DetectedStar> reference, List<DetectedStar> target, TransformModel model = TransformModel.Affine, double ransacThreshold = 2.0)
        {
            var R = reference.OrderByDescending(s => s.Flux).Take(MaxStars).ToList();
            var T = target.OrderByDescending(s => s.Flux).Take(MaxStars).ToList();
            var res = new MatchResult();
            if (R.Count < 3 || T.Count < 3) return res;
            var rt = BuildTriangles(R);
            var tt = BuildTriangles(T);
            // sort reference triangles by R1 for fast range search
            rt.Sort((a, b) => a.R1.CompareTo(b.R1));
            var r1 = rt.Select(t => t.R1).ToArray();
            var votes = new Dictionary<long, int>();
            foreach (var t in tt)
            {
                int lo = LowerBound(r1, t.R1 - InvariantTolerance);
                for (int k = lo; k < rt.Count && rt[k].R1 <= t.R1 + InvariantTolerance; k++)
                {
                    if (Math.Abs(rt[k].R2 - t.R2) > InvariantTolerance) continue;
                    Vote(votes, t.A, rt[k].A); Vote(votes, t.B, rt[k].B); Vote(votes, t.C, rt[k].C);
                }
            }
            // candidate correspondences: for each target star keep its best-voted reference star
            var cand = votes.GroupBy(kv => (int)(kv.Key >> 32))
                .Select(g => g.OrderByDescending(kv => kv.Value).First())
                .Where(kv => kv.Value >= 2)
                .Select(kv => Tuple.Create((int)(kv.Key >> 32), (int)(kv.Key & 0xFFFFFFFF)))
                .ToList();
            if (cand.Count < 3) return res;
            // RANSAC on similarity (needs 2 points) for robustness, then refine with requested model
            var rng = new Random(1234);
            List<int> bestInliers = null;
            int iters = Math.Min(2000, cand.Count * cand.Count);
            for (int it = 0; it < iters; it++)
            {
                int a = rng.Next(cand.Count), b = rng.Next(cand.Count);
                if (a == b) continue;
                var tr = SimilarityFrom2(T[cand[a].Item1], R[cand[a].Item2], T[cand[b].Item1], R[cand[b].Item2]);
                if (tr == null) continue;
                var inl = new List<int>();
                for (int k = 0; k < cand.Count; k++)
                {
                    double u, v;
                    tr.Apply(T[cand[k].Item1].X, T[cand[k].Item1].Y, out u, out v);
                    if (Sq(u - R[cand[k].Item2].X) + Sq(v - R[cand[k].Item2].Y) < ransacThreshold * ransacThreshold * 4) inl.Add(k);
                }
                if (bestInliers == null || inl.Count > bestInliers.Count) bestInliers = inl;
                if (bestInliers.Count > 0.8 * cand.Count && bestInliers.Count > 10) break;
            }
            if (bestInliers == null || bestInliers.Count < 3) return res;
            var pairs = bestInliers.Select(k => Tuple.Create(T[cand[k].Item1], R[cand[k].Item2])).ToList();
            // extend with nearest-neighbour matches of all stars using the provisional fit, then iterate refinement
            var full = Refine(pairs, model, res);
            for (int pass = 0; pass < 2 && full != null; pass++)
            {
                var all = new List<Tuple<DetectedStar, DetectedStar>>();
                foreach (var ts in target)
                {
                    double u, v;
                    res.Map(ts.X, ts.Y, out u, out v);
                    DetectedStar best = null; double bd = ransacThreshold * ransacThreshold;
                    foreach (var rs in reference)
                    {
                        double dd = Sq(rs.X - u) + Sq(rs.Y - v);
                        if (dd < bd) { bd = dd; best = rs; }
                    }
                    if (best != null) all.Add(Tuple.Create(ts, best));
                }
                if (all.Count >= pairs.Count) { pairs = all; full = Refine(pairs, model, res); }
            }
            return res;
        }

        private static int LowerBound(double[] a, double v)
        {
            int lo = 0, hi = a.Length;
            while (lo < hi) { int m = (lo + hi) >> 1; if (a[m] < v) lo = m + 1; else hi = m; }
            return lo;
        }

        private static void Vote(Dictionary<long, int> votes, int t, int r)
        {
            long key = ((long)t << 32) | (uint)r;
            int c;
            votes.TryGetValue(key, out c);
            votes[key] = c + 1;
        }

        private static Transform2D SimilarityFrom2(DetectedStar t1, DetectedStar r1, DetectedStar t2, DetectedStar r2)
        {
            double tx = t2.X - t1.X, ty = t2.Y - t1.Y, rx = r2.X - r1.X, ry = r2.Y - r1.Y;
            double lt = tx * tx + ty * ty;
            if (lt < 1e-6) return null;
            double a = (tx * rx + ty * ry) / lt, b = (tx * ry - ty * rx) / lt;
            double c = r1.X - (a * t1.X - b * t1.Y), d = r1.Y - (b * t1.X + a * t1.Y);
            return Transform2D.Affine(a, -b, c, b, a, d);
        }

        private static object Refine(List<Tuple<DetectedStar, DetectedStar>> pairs, TransformModel model, MatchResult res)
        {
            var xs = pairs.Select(p => p.Item1.X).ToList(); var ys = pairs.Select(p => p.Item1.Y).ToList();
            var us = pairs.Select(p => p.Item2.X).ToList(); var vs = pairs.Select(p => p.Item2.Y).ToList();
            Transform2D t = null;
            res.PolyX = res.PolyY = null;
            if (model == TransformModel.Translation)
            {
                double dx = Stats.Median(us.Zip(xs, (u, x) => u - x).ToList()), dy = Stats.Median(vs.Zip(ys, (v, y) => v - y).ToList());
                t = Transform2D.Translation(dx, dy);
            }
            else if (model == TransformModel.Similarity || pairs.Count < 3)
            {
                // linear LS for a, b, c, d in u = a x - b y + c, v = b x + a y + d
                int n = pairs.Count;
                var D = new double[2 * n, 4]; var Y = new double[2 * n];
                for (int i = 0; i < n; i++)
                {
                    D[2 * i, 0] = xs[i]; D[2 * i, 1] = -ys[i]; D[2 * i, 2] = 1; Y[2 * i] = us[i];
                    D[2 * i + 1, 0] = ys[i]; D[2 * i + 1, 1] = xs[i]; D[2 * i + 1, 3] = 1; Y[2 * i + 1] = vs[i];
                }
                var p = LinAlg.LeastSquares(D, Y);
                if (p != null) t = Transform2D.Affine(p[0], -p[1], p[2], p[1], p[0], p[3]);
            }
            else if (model == TransformModel.Homography && pairs.Count >= 6)
            {
                var hm = LinAlg.FitHomography(xs, ys, us, vs);
                if (hm != null) t = new Transform2D { M = hm };
            }
            else
            {
                var a = LinAlg.FitAffine(xs, ys, us, vs);
                if (a != null) t = Transform2D.Affine(a[0], a[1], a[2], a[3], a[4], a[5]);
                if (model == TransformModel.Polynomial2 && pairs.Count >= 12)
                {
                    int n = pairs.Count;
                    var D = new double[n, 6];
                    for (int i = 0; i < n; i++) { D[i, 0] = 1; D[i, 1] = xs[i]; D[i, 2] = ys[i]; D[i, 3] = xs[i] * xs[i]; D[i, 4] = xs[i] * ys[i]; D[i, 5] = ys[i] * ys[i]; }
                    res.PolyX = LinAlg.LeastSquares(D, us.ToArray());
                    res.PolyY = LinAlg.LeastSquares(D, vs.ToArray());
                    if (res.PolyX == null || res.PolyY == null) res.PolyX = res.PolyY = null;
                }
            }
            if (t == null) return null;
            res.Transform = t;
            res.Pairs = pairs;
            res.Matches = pairs.Count;
            double se = 0;
            foreach (var p in pairs)
            {
                double u, v;
                res.Map(p.Item1.X, p.Item1.Y, out u, out v);
                se += Sq(u - p.Item2.X) + Sq(v - p.Item2.Y);
            }
            res.RmsError = Math.Sqrt(se / Math.Max(1, pairs.Count));
            return t;
        }
    }
}
