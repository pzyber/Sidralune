using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Sidralune.Core.Mathematics;
using Sidralune.Imaging.IO;

namespace Sidralune.Imaging.Planetary
{
    public enum GlobalAlignMode { Planet, Surface }

    /// <summary>Options in the spirit of AutoStakkert! 3/4 by Emil Kraaikamp.</summary>
    public class LuckyOptions
    {
        /// <summary>A copy for one run, so the settings can be edited while it works.</summary>
        public LuckyOptions Clone() => (LuckyOptions)MemberwiseClone();

        public GlobalAlignMode Align = GlobalAlignMode.Planet;
        public QualityMetric Metric = QualityMetric.Gradient;
        public double NoiseRobust = 1.5;           // pre-blur sigma for quality
        public double[] StackPercents = { 25 };    // can produce several stacks (e.g. 10, 25, 50)
        public int ApSize = 48;                     // alignment point box size (pixels)
        public double ApMinBrightness = 0.12;       // fraction of max for AP placement
        public int ApSearchRadius = 8;              // local search (pixels)
        public bool MultiScaleAps = true;           // add larger APs for robustness
        public bool LocalQuality = true;            // per-AP frame selection
        public double DrizzleScale = 1.0;           // 1, 1.5, 2, 3
        public bool Normalize = true;
        public int ReferenceFrames = 0;             // 0 = auto (best ~5%)
        public bool UseFieldDerotation = false;     // apply per-frame rotation from PippFrameInfo
        public double[] FrameRotationDeg;           // optional per-frame rotation
        public int MaxFramesInMemory = 2000;
        public bool Sharpen = false;                // quick wavelet sharpening of the output
    }

    public class LuckyFrame
    {
        public int Index;
        public double Quality;
        public double Dx, Dy;          // global shift onto reference
        public double Brightness;
    }

    public class LuckyResult
    {
        public List<AstroImage> Stacks = new List<AstroImage>();
        public List<LuckyFrame> Frames = new List<LuckyFrame>();
        public List<int[]> AlignmentPoints = new List<int[]>(); // x, y, size
        public AstroImage Reference;
        public string Report;
    }

    /// <summary>Lucky-imaging stacker with global alignment, multi-point local alignment and per-AP frame selection.</summary>
    public class LuckyStacker
    {
        public LuckyOptions Options = new LuckyOptions();

        public LuckyResult Run(IFrameSource src, IProgress<string> status = null, IProgress<double> progress = null, CancellationToken ct = default(CancellationToken))
        {
            var o = Options;
            var res = new LuckyResult();
            int n = src.FrameCount;
            int w = src.Width, h = src.Height;
            // 1. analyse frames: quality + global alignment on luminance
            status?.Report("Analysing " + n + " frames");
            var frames = new LuckyFrame[n];
            var first = src.ReadFrame(0);
            bool debayer = first.Channels == 1 && first.Bayer != BayerPattern.None;
            Func<int, AstroImage> read = i => { var f = src.ReadFrame(i); return debayer ? Debayer.Apply(f, DebayerMethod.Bilinear) : f; };
            int ch = debayer ? 3 : first.Channels;
            var firstLum = read(0).Luminance();
            var obj0 = ObjectDetection.Detect(firstLum, w, h);
            int done = 0;
            Parallel.For(0, n, new ParallelOptions { CancellationToken = ct }, i =>
            {
                var lum = read(i).Luminance();
                var fr = new LuckyFrame { Index = i };
                double sum = 0; for (int k = 0; k < lum.Length; k += 7) sum += lum[k];
                fr.Brightness = sum / (lum.Length / 7.0);
                if (o.Align == GlobalAlignMode.Planet)
                {
                    var ob = ObjectDetection.Detect(lum, w, h);
                    if (ob.Found)
                    {
                        fr.Dx = obj0.Cx - ob.Cx; fr.Dy = obj0.Cy - ob.Cy;
                        fr.Quality = FrameQuality.Measure(lum, w, h, o.Metric, ob.X0, ob.Y0, ob.Width, ob.Height, o.NoiseRobust);
                    }
                }
                else
                {
                    fr.Quality = FrameQuality.Measure(lum, w, h, o.Metric, w / 4, h / 4, w / 2, h / 2, o.NoiseRobust);
                }
                frames[i] = fr;
                int d = Interlocked.Increment(ref done);
                if ((d & 31) == 0) progress?.Report(0.3 * d / n);
            });
            res.Frames = frames.ToList();
            var ranked = frames.OrderByDescending(f => f.Quality).ToList();
            // surface mode: global alignment by phase correlation to the best frame
            int pcs = Fft.NextPowerOfTwo(Math.Min(w, h)) / 2;
            if (o.Align == GlobalAlignMode.Surface)
            {
                var best = read(ranked[0].Index).Luminance();
                var win = Fft.Hann2D(pcs, pcs);
                var refCrop = CropCenter(best, w, h, pcs);
                Parallel.ForEach(frames, fr =>
                {
                    var lum = read(fr.Index).Luminance();
                    double dx, dy;
                    Fft.PhaseCorrelate(refCrop, CropCenter(lum, w, h, pcs), pcs, pcs, out dx, out dy, win);
                    fr.Dx = dx; fr.Dy = dy;
                });
            }
            // 2. reference: average of the best frames, globally aligned
            int nref = o.ReferenceFrames > 0 ? o.ReferenceFrames : Math.Max(3, Math.Min(50, n / 20));
            status?.Report("Building reference from " + nref + " best frames");
            var reference = new AstroImage(w, h, ch);
            foreach (var fr in ranked.Take(nref))
            {
                var img = Shifted(read(fr.Index), fr.Dx, fr.Dy, Rotation(fr.Index));
                for (int c = 0; c < ch; c++) for (int i = 0; i < w * h; i++) reference.Data[c][i] += img.Data[Math.Min(c, img.Channels - 1)][i] / nref;
            }
            res.Reference = reference;
            var refLum = reference.Luminance();
            // 3. alignment points
            var aps = PlaceAps(refLum, w, h, o);
            res.AlignmentPoints = aps;
            status?.Report(aps.Count + " alignment points");
            double maxPercent = o.StackPercents.Max();
            int maxFrames = Math.Max(1, (int)Math.Round(n * maxPercent / 100));
            var candidates = ranked.Take(Math.Min(n, Math.Max(maxFrames * 2, maxFrames + 10))).ToList();
            // 4. local shifts & local qualities for candidate frames
            var localShift = new double[candidates.Count][];
            var localQ = new double[candidates.Count][];
            done = 0;
            Parallel.For(0, candidates.Count, new ParallelOptions { CancellationToken = ct }, k =>
            {
                var fr = candidates[k];
                var lum = Shifted(read(fr.Index), fr.Dx, fr.Dy, Rotation(fr.Index)).Luminance();
                var sh = new double[aps.Count * 2];
                var q = new double[aps.Count];
                for (int a = 0; a < aps.Count; a++)
                {
                    int ax = aps[a][0], ay = aps[a][1], s = aps[a][2];
                    double dx, dy;
                    LocalShift(refLum, lum, w, h, ax, ay, s, o.ApSearchRadius, out dx, out dy);
                    sh[2 * a] = dx; sh[2 * a + 1] = dy;
                    q[a] = FrameQuality.Measure(lum, w, h, o.Metric, ax - s / 2, ay - s / 2, s, s, o.NoiseRobust);
                }
                localShift[k] = sh; localQ[k] = q;
                int d = Interlocked.Increment(ref done);
                if ((d & 7) == 0) progress?.Report(0.3 + 0.4 * d / candidates.Count);
            });
            // 5. stack for each requested percentage
            double scale = Math.Max(1, o.DrizzleScale);
            int ow = (int)Math.Round(w * scale), oh = (int)Math.Round(h * scale);
            foreach (var pct in o.StackPercents)
            {
                ct.ThrowIfCancellationRequested();
                int take = Math.Max(1, (int)Math.Round(n * pct / 100));
                status?.Report(string.Format("Stacking best {0}% ({1} frames)", pct, take));
                var acc = new double[ch][];
                for (int c = 0; c < ch; c++) acc[c] = new double[ow * oh];
                var wsum = new double[ow * oh];
                // per-AP frame selection
                var chosen = new List<int>[aps.Count];
                for (int a = 0; a < aps.Count; a++)
                {
                    int aa = a;
                    var order = Enumerable.Range(0, candidates.Count);
                    if (o.LocalQuality) order = order.OrderByDescending(k => localQ[k][aa]);
                    chosen[a] = order.Take(Math.Min(take, candidates.Count)).ToList();
                }
                var used = new HashSet<int>(chosen.SelectMany(c => c));
                var lockObj = new object();
                Parallel.ForEach(used, k =>
                {
                    var fr = candidates[k];
                    var img = Shifted(read(fr.Index), fr.Dx, fr.Dy, Rotation(fr.Index));
                    double bnorm = o.Normalize && fr.Brightness > 0 ? ranked[0].Brightness / fr.Brightness : 1;
                    var localAcc = new double[ch][];
                    for (int c = 0; c < ch; c++) localAcc[c] = new double[ow * oh];
                    var localW = new double[ow * oh];
                    for (int a = 0; a < aps.Count; a++)
                    {
                        if (!chosen[a].Contains(k)) continue;
                        int ax = aps[a][0], ay = aps[a][1], s = aps[a][2];
                        double dx = localShift[k][2 * a], dy = localShift[k][2 * a + 1];
                        int r = s; // blend window extends beyond the AP for smooth transitions
                        for (int oy = (int)((ay - r) * scale); oy < (int)((ay + r) * scale); oy++)
                        {
                            if (oy < 0 || oy >= oh) continue;
                            for (int ox = (int)((ax - r) * scale); ox < (int)((ax + r) * scale); ox++)
                            {
                                if (ox < 0 || ox >= ow) continue;
                                double sx = ox / scale - dx, sy = oy / scale - dy;
                                double ux = (ox / scale - ax) / r, uy = (oy / scale - ay) / r;
                                double wt = Math.Max(0, 1 - ux * ux) * Math.Max(0, 1 - uy * uy);
                                if (wt <= 0) continue;
                                int oi = oy * ow + ox;
                                for (int c = 0; c < ch; c++)
                                {
                                    float v = Geometry.SampleAt(img.Data[Math.Min(c, img.Channels - 1)], w, h, sx, sy, scale > 1 ? Interpolation.Bilinear : Interpolation.Bicubic);
                                    if (!float.IsNaN(v)) localAcc[c][oi] += v * bnorm * wt;
                                }
                                localW[oi] += wt;
                            }
                        }
                    }
                    lock (lockObj)
                    {
                        for (int c = 0; c < ch; c++) { var A = acc[c]; var L = localAcc[c]; for (int i = 0; i < A.Length; i++) A[i] += L[i]; }
                        for (int i = 0; i < wsum.Length; i++) wsum[i] += localW[i];
                    }
                });
                // areas not covered by APs fall back to the reference
                var outImg = new AstroImage(ow, oh, ch);
                for (int c = 0; c < ch; c++)
                    for (int y = 0; y < oh; y++)
                        for (int x = 0; x < ow; x++)
                        {
                            int i = y * ow + x;
                            outImg.Data[c][i] = wsum[i] > 1e-6 ? (float)(acc[c][i] / wsum[i]) : Geometry.SampleAt(reference.Data[c], w, h, x / scale, y / scale, Interpolation.Bicubic);
                        }
                outImg.Header.Set("NCOMBINE", take);
                outImg.Header.Set("STACKPCT", pct);
                outImg.Header.AddHistory(string.Format("Lucky stack: best {0}% of {1} frames, {2} APs (size {3}), drizzle {4}", pct, n, aps.Count, o.ApSize, scale));
                var ts = src.Timestamp(ranked[0].Index);
                if (ts.HasValue)
                {
                    // mid-time of the frames used (for WinJUPOS)
                    var times = candidates.Take(take).Select(c => src.Timestamp(c.Index)).Where(t => t.HasValue).Select(t => (double)t.Value.Ticks).ToList();
                    if (times.Count > 0) outImg.DateObs = new DateTime((long)times.Average(), DateTimeKind.Utc);
                }
                if (o.Sharpen) for (int c = 0; c < ch; c++) outImg.Data[c] = Wavelets.Sharpen(outImg.Data[c], ow, oh, new[] { 1.5, 0.8, 0.3, 0.0, 0.0 }, new[] { 0.5, 0.2, 0, 0, 0 });
                res.Stacks.Add(outImg);
            }
            progress?.Report(1);
            res.Report = string.Format("{0} frames analysed, {1} APs, stacks: {2}", n, aps.Count, string.Join(", ", o.StackPercents.Select(p => p + "%")));
            return res;
        }

        private double Rotation(int index) => Options.FrameRotationDeg != null && index < Options.FrameRotationDeg.Length ? Options.FrameRotationDeg[index] : 0;

        private static AstroImage Shifted(AstroImage img, double dx, double dy, double rotDeg)
        {
            var t = Transform2D.Translation(dx, dy);
            if (Math.Abs(rotDeg) > 1e-6) t = Transform2D.Rotation(-rotDeg * Math.PI / 180, img.Width / 2.0, img.Height / 2.0).Compose(t);
            if (Math.Abs(dx) < 1e-6 && Math.Abs(dy) < 1e-6 && Math.Abs(rotDeg) < 1e-6) return img;
            return Geometry.Warp(img, t, img.Width, img.Height, Interpolation.Bicubic);
        }

        private static float[] CropCenter(float[] d, int w, int h, int s)
        {
            var c = new float[s * s];
            int x0 = (w - s) / 2, y0 = (h - s) / 2;
            for (int y = 0; y < s; y++) Array.Copy(d, (y0 + y) * w + x0, c, y * s, s);
            return c;
        }

        /// <summary>Grid of alignment points on structure brighter than the threshold (AS!-style), optional multi-scale.</summary>
        public static List<int[]> PlaceAps(float[] refLum, int w, int h, LuckyOptions o)
        {
            var aps = new List<int[]>();
            float mx = refLum.Max();
            foreach (int size in o.MultiScaleAps ? new[] { o.ApSize, o.ApSize * 2 } : new[] { o.ApSize })
            {
                int step = size / 2 + size / 4;
                for (int y = size / 2 + o.ApSearchRadius; y < h - size / 2 - o.ApSearchRadius; y += step)
                    for (int x = size / 2 + o.ApSearchRadius; x < w - size / 2 - o.ApSearchRadius; x += step)
                    {
                        double s = 0; int cnt = 0, bright = 0;
                        for (int yy = y - size / 2; yy < y + size / 2; yy += 2)
                            for (int xx = x - size / 2; xx < x + size / 2; xx += 2)
                            {
                                float v = refLum[yy * w + xx];
                                s += v; cnt++;
                                if (v > o.ApMinBrightness * mx) bright++;
                            }
                        if (cnt > 0 && bright > cnt * 0.5 && s / cnt > o.ApMinBrightness * mx) aps.Add(new[] { x, y, size });
                    }
            }
            if (aps.Count == 0) aps.Add(new[] { w / 2, h / 2, Math.Min(w, h) / 2 });
            return aps;
        }

        /// <summary>Local shift (sub-pixel) of the AP patch in 'img' relative to 'reference' by normalised cross-correlation search.</summary>
        public static void LocalShift(float[] reference, float[] img, int w, int h, int cx, int cy, int size, int radius, out double dx, out double dy)
        {
            int hs = size / 2;
            double best = double.MinValue; int bx = 0, by = 0;
            var scores = new double[(2 * radius + 1) * (2 * radius + 1)];
            // precompute reference patch stats
            double rm = 0; int n = 0;
            for (int y = cy - hs; y < cy + hs; y += 2) for (int x = cx - hs; x < cx + hs; x += 2) { rm += reference[y * w + x]; n++; }
            rm /= n;
            for (int sy = -radius; sy <= radius; sy++)
                for (int sx = -radius; sx <= radius; sx++)
                {
                    double num = 0, d1 = 0, d2 = 0, im = 0;
                    for (int y = cy - hs; y < cy + hs; y += 2)
                        for (int x = cx - hs; x < cx + hs; x += 2)
                        {
                            int yy = y + sy, xx = x + sx;
                            if (yy < 0 || yy >= h || xx < 0 || xx >= w) continue;
                            im += img[yy * w + xx];
                        }
                    im /= n;
                    for (int y = cy - hs; y < cy + hs; y += 2)
                        for (int x = cx - hs; x < cx + hs; x += 2)
                        {
                            int yy = y + sy, xx = x + sx;
                            if (yy < 0 || yy >= h || xx < 0 || xx >= w) continue;
                            double a = reference[y * w + x] - rm, b = img[yy * w + xx] - im;
                            num += a * b; d1 += a * a; d2 += b * b;
                        }
                    double ncc = d1 > 0 && d2 > 0 ? num / Math.Sqrt(d1 * d2) : 0;
                    scores[(sy + radius) * (2 * radius + 1) + sx + radius] = ncc;
                    if (ncc > best) { best = ncc; bx = sx; by = sy; }
                }
            // parabolic sub-pixel refinement
            int W = 2 * radius + 1;
            Func<int, int, double> S = (x, y) => scores[AstroMath.Clamp(y + radius, 0, W - 1) * W + AstroMath.Clamp(x + radius, 0, W - 1)];
            double fx = 0, fy = 0;
            if (Math.Abs(bx) < radius) { double l = S(bx - 1, by), c = S(bx, by), r = S(bx + 1, by), den = l - 2 * c + r; if (Math.Abs(den) > 1e-12) fx = 0.5 * (l - r) / den; }
            if (Math.Abs(by) < radius) { double u = S(bx, by - 1), c = S(bx, by), d = S(bx, by + 1), den = u - 2 * c + d; if (Math.Abs(den) > 1e-12) fy = 0.5 * (u - d) / den; }
            // shift that maps img onto reference: content at (x+sx) in img corresponds to x in reference
            dx = -(bx + AstroMath.Clamp(fx, -0.5, 0.5));
            dy = -(by + AstroMath.Clamp(fy, -0.5, 0.5));
        }
    }
}
