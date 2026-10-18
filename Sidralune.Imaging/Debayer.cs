using System;
using System.Threading.Tasks;

namespace Sidralune.Imaging
{
    public enum DebayerMethod { Bilinear, SuperPixel, MalvarHeCutler, EdgeDirected, None }

    /// <summary>Colour filter array demosaicing.</summary>
    public static class Debayer
    {
        /// <summary>0 = R, 1 = G, 2 = B at (x, y) for a pattern.</summary>
        public static int ColorAt(BayerPattern p, int x, int y)
        {
            int i = ((y & 1) << 1) | (x & 1);
            switch (p)
            {
                case BayerPattern.RGGB: return new[] { 0, 1, 1, 2 }[i];
                case BayerPattern.BGGR: return new[] { 2, 1, 1, 0 }[i];
                case BayerPattern.GRBG: return new[] { 1, 0, 2, 1 }[i];
                case BayerPattern.GBRG: return new[] { 1, 2, 0, 1 }[i];
            }
            return 1;
        }

        public static BayerPattern FlipVertical(BayerPattern p)
        {
            switch (p)
            {
                case BayerPattern.RGGB: return BayerPattern.GBRG;
                case BayerPattern.GBRG: return BayerPattern.RGGB;
                case BayerPattern.BGGR: return BayerPattern.GRBG;
                case BayerPattern.GRBG: return BayerPattern.BGGR;
            }
            return p;
        }

        public static BayerPattern FlipHorizontal(BayerPattern p)
        {
            switch (p)
            {
                case BayerPattern.RGGB: return BayerPattern.GRBG;
                case BayerPattern.GRBG: return BayerPattern.RGGB;
                case BayerPattern.BGGR: return BayerPattern.GBRG;
                case BayerPattern.GBRG: return BayerPattern.BGGR;
            }
            return p;
        }

        /// <summary>Pattern after cropping at an (x, y) offset.</summary>
        public static BayerPattern Shift(BayerPattern p, int dx, int dy)
        {
            if ((dx & 1) == 1) p = FlipHorizontal(p);
            if ((dy & 1) == 1) p = FlipVertical(p);
            return p;
        }

        public static AstroImage Apply(AstroImage cfa, DebayerMethod method, BayerPattern? patternOverride = null)
        {
            var p = patternOverride ?? cfa.Bayer;
            if (p == BayerPattern.None || cfa.Channels != 1 || method == DebayerMethod.None) return cfa;
            AstroImage r;
            switch (method)
            {
                case DebayerMethod.SuperPixel: r = SuperPixel(cfa, p); break;
                case DebayerMethod.MalvarHeCutler: r = Malvar(cfa, p); break;
                case DebayerMethod.EdgeDirected: r = EdgeDirected(cfa, p); break;
                default: r = Bilinear(cfa, p); break;
            }
            r.Header = cfa.Header.Clone();
            r.Header.Remove("BAYERPAT");
            r.Header.AddHistory("Debayered (" + method + ", " + p + ")");
            r.SourcePath = cfa.SourcePath;
            r.OriginalBitDepth = cfa.OriginalBitDepth;
            return r;
        }

        private static float Px(float[] d, int w, int h, int x, int y)
        {
            if (x < 0) x = -x; else if (x >= w) x = 2 * w - x - 2;
            if (y < 0) y = -y; else if (y >= h) y = 2 * h - y - 2;
            return d[y * w + x];
        }

        public static AstroImage SuperPixel(AstroImage cfa, BayerPattern p)
        {
            int w = cfa.Width / 2, h = cfa.Height / 2, W = cfa.Width;
            var o = new AstroImage(w, h, 3);
            var d = cfa.Data[0];
            Parallel.For(0, h, y =>
            {
                for (int x = 0; x < w; x++)
                {
                    float r = 0, g = 0, b = 0;
                    for (int dy = 0; dy < 2; dy++)
                        for (int dx = 0; dx < 2; dx++)
                        {
                            int c = ColorAt(p, dx, dy);
                            float v = d[(2 * y + dy) * W + 2 * x + dx];
                            if (c == 0) r = v; else if (c == 2) b = v; else g += 0.5f * v;
                        }
                    int i = y * w + x;
                    o.Data[0][i] = r; o.Data[1][i] = g; o.Data[2][i] = b;
                }
            });
            return o;
        }

        public static AstroImage Bilinear(AstroImage cfa, BayerPattern p)
        {
            int w = cfa.Width, h = cfa.Height;
            var o = new AstroImage(w, h, 3);
            var d = cfa.Data[0];
            Parallel.For(0, h, y =>
            {
                for (int x = 0; x < w; x++)
                {
                    int c = ColorAt(p, x, y);
                    float v = d[y * w + x];
                    float n = Px(d, w, h, x, y - 1), s = Px(d, w, h, x, y + 1), e = Px(d, w, h, x + 1, y), wv = Px(d, w, h, x - 1, y);
                    float ne = Px(d, w, h, x + 1, y - 1), nw = Px(d, w, h, x - 1, y - 1), se = Px(d, w, h, x + 1, y + 1), sw = Px(d, w, h, x - 1, y + 1);
                    float cross = 0.25f * (n + s + e + wv), diag = 0.25f * (ne + nw + se + sw);
                    float r, g, b;
                    if (c == 1)
                    {
                        g = v;
                        // which neighbours are red: horizontal or vertical
                        bool redHoriz = ColorAt(p, x + 1, y) == 0;
                        float horiz = 0.5f * (e + wv), vert = 0.5f * (n + s);
                        r = redHoriz ? horiz : vert;
                        b = redHoriz ? vert : horiz;
                    }
                    else if (c == 0) { r = v; g = cross; b = diag; }
                    else { b = v; g = cross; r = diag; }
                    int i = y * w + x;
                    o.Data[0][i] = r; o.Data[1][i] = g; o.Data[2][i] = b;
                }
            });
            return o;
        }

        /// <summary>Malvar, He &amp; Cutler (2004) gradient-corrected linear interpolation (5x5 kernels).</summary>
        public static AstroImage Malvar(AstroImage cfa, BayerPattern p)
        {
            int w = cfa.Width, h = cfa.Height;
            var o = new AstroImage(w, h, 3);
            var d = cfa.Data[0];
            Parallel.For(0, h, y =>
            {
                for (int x = 0; x < w; x++)
                {
                    int c = ColorAt(p, x, y);
                    float v = Px(d, w, h, x + (0), y + (0));
                    // G at R/B
                    float gAtRB = (4 * v + 2 * (Px(d, w, h, x + (0), y + (-1)) + Px(d, w, h, x + (-1), y + (0)) + Px(d, w, h, x + (1), y + (0)) + Px(d, w, h, x + (0), y + (1))) - (Px(d, w, h, x + (0), y + (-2)) + Px(d, w, h, x + (-2), y + (0)) + Px(d, w, h, x + (2), y + (0)) + Px(d, w, h, x + (0), y + (2)))) / 8f;
                    // R/B at G in row with R/B (horizontal neighbours)
                    float rowRB = (5 * v + 4 * (Px(d, w, h, x + (-1), y + (0)) + Px(d, w, h, x + (1), y + (0))) - (Px(d, w, h, x + (-2), y + (0)) + Px(d, w, h, x + (2), y + (0)) + Px(d, w, h, x + (-1), y + (-1)) + Px(d, w, h, x + (1), y + (-1)) + Px(d, w, h, x + (-1), y + (1)) + Px(d, w, h, x + (1), y + (1))) + 0.5f * (Px(d, w, h, x + (0), y + (-2)) + Px(d, w, h, x + (0), y + (2)))) / 8f;
                    // R/B at G in column (vertical neighbours)
                    float colRB = (5 * v + 4 * (Px(d, w, h, x + (0), y + (-1)) + Px(d, w, h, x + (0), y + (1))) - (Px(d, w, h, x + (0), y + (-2)) + Px(d, w, h, x + (0), y + (2)) + Px(d, w, h, x + (-1), y + (-1)) + Px(d, w, h, x + (1), y + (-1)) + Px(d, w, h, x + (-1), y + (1)) + Px(d, w, h, x + (1), y + (1))) + 0.5f * (Px(d, w, h, x + (-2), y + (0)) + Px(d, w, h, x + (2), y + (0)))) / 8f;
                    // R at B or B at R (diagonals)
                    float diag = (6 * v + 2 * (Px(d, w, h, x + (-1), y + (-1)) + Px(d, w, h, x + (1), y + (-1)) + Px(d, w, h, x + (-1), y + (1)) + Px(d, w, h, x + (1), y + (1))) - 1.5f * (Px(d, w, h, x + (0), y + (-2)) + Px(d, w, h, x + (-2), y + (0)) + Px(d, w, h, x + (2), y + (0)) + Px(d, w, h, x + (0), y + (2)))) / 8f;
                    float r, g, b;
                    if (c == 1)
                    {
                        g = v;
                        bool redHoriz = ColorAt(p, x + 1, y) == 0;
                        r = redHoriz ? rowRB : colRB;
                        b = redHoriz ? colRB : rowRB;
                    }
                    else if (c == 0) { r = v; g = gAtRB; b = diag; }
                    else { b = v; g = gAtRB; r = diag; }
                    int i = y * w + x;
                    o.Data[0][i] = Math.Max(0, r); o.Data[1][i] = Math.Max(0, g); o.Data[2][i] = Math.Max(0, b);
                }
            });
            return o;
        }

        /// <summary>
        /// Edge-directed demosaic: Hamilton-Adams green interpolation (gradient + Laplacian correction, choosing the
        /// smoother direction) followed by colour-difference interpolation of R and B. Sharper than bilinear with
        /// far less zippering; the green step is the core of AHD.
        /// </summary>
        public static AstroImage EdgeDirected(AstroImage cfa, BayerPattern p)
        {
            int w = cfa.Width, h = cfa.Height;
            var d = cfa.Data[0];
            var G = new float[w * h];
            Parallel.For(0, h, y =>
            {
                for (int x = 0; x < w; x++)
                {
                    int c = ColorAt(p, x, y);
                    float v = d[y * w + x];
                    if (c == 1) { G[y * w + x] = v; continue; }
                    float gl = Px(d, w, h, x - 1, y), gr = Px(d, w, h, x + 1, y), gu = Px(d, w, h, x, y - 1), gd = Px(d, w, h, x, y + 1);
                    float cl2 = Px(d, w, h, x - 2, y), cr2 = Px(d, w, h, x + 2, y), cu2 = Px(d, w, h, x, y - 2), cd2 = Px(d, w, h, x, y + 2);
                    float dh = Math.Abs(gl - gr) + Math.Abs(2 * v - cl2 - cr2);
                    float dv = Math.Abs(gu - gd) + Math.Abs(2 * v - cu2 - cd2);
                    float eh = 0.5f * (gl + gr) + 0.25f * (2 * v - cl2 - cr2);
                    float ev = 0.5f * (gu + gd) + 0.25f * (2 * v - cu2 - cd2);
                    float g;
                    if (dh < dv) g = eh; else if (dv < dh) g = ev; else g = 0.5f * (eh + ev);
                    G[y * w + x] = Math.Max(0, g);
                }
            });
            // colour differences R-G and B-G known at R and B sites
            var o = new AstroImage(w, h, 3);
            Array.Copy(G, o.Data[1], G.Length);
            for (int ch = 0; ch < 3; ch += 2)
            {
                var diff = new float[w * h];
                var known = new bool[w * h];
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                        if (ColorAt(p, x, y) == ch) { diff[y * w + x] = d[y * w + x] - G[y * w + x]; known[y * w + x] = true; }
                var outp = o.Data[ch];
                int chLocal = ch;
                Parallel.For(0, h, y =>
                {
                    for (int x = 0; x < w; x++)
                    {
                        int i = y * w + x;
                        float df;
                        if (known[i]) df = diff[i];
                        else
                        {
                            float s = 0; int n = 0;
                            for (int dy = -1; dy <= 1; dy++)
                                for (int dx = -1; dx <= 1; dx++)
                                {
                                    int xx = x + dx, yy = y + dy;
                                    if (xx < 0 || yy < 0 || xx >= w || yy >= h) continue;
                                    int j = yy * w + xx;
                                    if (known[j]) { s += diff[j]; n++; }
                                }
                            df = n > 0 ? s / n : 0;
                        }
                        outp[i] = Math.Max(0, G[i] + df);
                    }
                });
            }
            return o;
        }

        /// <summary>Split a CFA frame into its four sub-planes (R, G1, G2, B) at half resolution, e.g. for per-channel stats.</summary>
        public static float[][] SplitCfa(AstroImage cfa, BayerPattern p)
        {
            int w = cfa.Width / 2, h = cfa.Height / 2, W = cfa.Width;
            var planes = new float[4][];
            for (int i = 0; i < 4; i++) planes[i] = new float[w * h];
            var d = cfa.Data[0];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    for (int k = 0; k < 4; k++)
                        planes[k][y * w + x] = d[(2 * y + (k >> 1)) * W + 2 * x + (k & 1)];
            return planes;
        }
    }
}
