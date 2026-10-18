using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Sidralune.Core.Mathematics;

namespace Sidralune.Imaging.Planetary
{
    public static class SolarTools
    {
        /// <summary>
        /// Flatten limb darkening: divide the disc by its azimuthally averaged radial profile (robust median per ring),
        /// revealing faculae, granulation and filaments uniformly across the disc.
        /// </summary>
        public static void FlattenLimbDarkening(AstroImage img, double cx, double cy, double radius, double strength = 1.0)
        {
            int w = img.Width, h = img.Height;
            int bins = (int)Math.Ceiling(radius) + 1;
            for (int c = 0; c < img.Channels; c++)
            {
                var d = img.Data[c];
                var rings = new List<double>[bins];
                for (int i = 0; i < bins; i++) rings[i] = new List<double>();
                for (int y = 0; y < h; y += 2)
                    for (int x = 0; x < w; x += 2)
                    {
                        double r = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                        if (r < radius) rings[(int)r].Add(d[y * w + x]);
                    }
                var prof = new double[bins];
                for (int i = 0; i < bins; i++) prof[i] = rings[i].Count > 3 ? Stats.Median(rings[i]) : (i > 0 ? prof[i - 1] : 1);
                // smooth the profile
                var sm = new double[bins];
                for (int i = 0; i < bins; i++)
                {
                    double s = 0; int n = 0;
                    for (int k = Math.Max(0, i - 3); k <= Math.Min(bins - 1, i + 3); k++) { s += prof[k]; n++; }
                    sm[i] = s / n;
                }
                double centre = sm[0];
                Parallel.For(0, h, y =>
                {
                    for (int x = 0; x < w; x++)
                    {
                        double r = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                        if (r >= radius) continue;
                        double p = sm[Math.Min(bins - 1, (int)r)];
                        if (p <= 1e-6) continue;
                        double f = centre / p;
                        d[y * w + x] = (float)(d[y * w + x] * (1 + (f - 1) * strength));
                    }
                });
            }
            img.Header.AddHistory("Limb darkening flattened");
        }

        /// <summary>
        /// Prominence enhancement: stretch the region outside the limb strongly (gain/gamma) and blend with a soft edge,
        /// producing the classic "disc + prominences" composite from a single exposure set.
        /// </summary>
        public static void EnhanceProminences(AstroImage img, double cx, double cy, double radius, double gain = 6.0, double gamma = 0.6, double feather = 3.0)
        {
            int w = img.Width, h = img.Height;
            for (int c = 0; c < img.Channels; c++)
            {
                var d = img.Data[c];
                Parallel.For(0, h, y =>
                {
                    for (int x = 0; x < w; x++)
                    {
                        double r = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                        double t = AstroMath.Clamp((r - radius) / feather, 0, 1);
                        if (t <= 0) continue;
                        int i = y * w + x;
                        double v = Math.Pow(Math.Max(0, d[i] * gain), gamma);
                        d[i] = (float)(d[i] * (1 - t) + Math.Min(1, v) * t);
                    }
                });
            }
        }

        /// <summary>Artificial occulting disc (fill the solar disc), for prominence-only views.</summary>
        public static void OccultDisc(AstroImage img, double cx, double cy, double radius, float value = 0)
        {
            for (int c = 0; c < img.Channels; c++)
                for (int y = 0; y < img.Height; y++)
                    for (int x = 0; x < img.Width; x++)
                        if ((x - cx) * (x - cx) + (y - cy) * (y - cy) < radius * radius) img.Data[c][y * img.Width + x] = value;
        }
    }

    /// <summary>Translation-only mosaic stitching of overlapping tiles (lunar/solar full disc), feathered blending.</summary>
    public static class MosaicStitcher
    {
        public class Tile { public AstroImage Image; public double X, Y; }

        /// <summary>
        /// Estimate offsets between consecutive tiles with phase correlation on their overlap (initial offsets give the
        /// approximate layout), then blend into one canvas.
        /// </summary>
        public static AstroImage Stitch(List<Tile> tiles, bool refine = true)
        {
            if (tiles.Count == 0) return null;
            if (refine)
            {
                for (int i = 1; i < tiles.Count; i++)
                {
                    // compare with the most-overlapping already placed tile
                    var t = tiles[i];
                    Tile best = null; double bestArea = 0;
                    for (int j = 0; j < i; j++)
                    {
                        var p = tiles[j];
                        double ox = Math.Max(0, Math.Min(p.X + p.Image.Width, t.X + t.Image.Width) - Math.Max(p.X, t.X));
                        double oy = Math.Max(0, Math.Min(p.Y + p.Image.Height, t.Y + t.Image.Height) - Math.Max(p.Y, t.Y));
                        if (ox * oy > bestArea) { bestArea = ox * oy; best = p; }
                    }
                    if (best == null) continue;
                    int x0 = (int)Math.Max(best.X, t.X), y0 = (int)Math.Max(best.Y, t.Y);
                    int x1 = (int)Math.Min(best.X + best.Image.Width, t.X + t.Image.Width), y1 = (int)Math.Min(best.Y + best.Image.Height, t.Y + t.Image.Height);
                    int s = Fft.NextPowerOfTwo(Math.Min(x1 - x0, y1 - y0)) / 2;
                    if (s < 32) continue;
                    int cx = (x0 + x1) / 2 - s / 2, cy = (y0 + y1) / 2 - s / 2;
                    var a = Patch(best, cx, cy, s); var b = Patch(t, cx, cy, s);
                    double dx, dy;
                    Fft.PhaseCorrelate(a, b, s, s, out dx, out dy, Fft.Hann2D(s, s));
                    t.X += dx; t.Y += dy;
                }
            }
            double minX = tiles.Min(t => t.X), minY = tiles.Min(t => t.Y);
            int W = (int)Math.Ceiling(tiles.Max(t => t.X + t.Image.Width) - minX), H = (int)Math.Ceiling(tiles.Max(t => t.Y + t.Image.Height) - minY);
            int ch = tiles.Max(t => t.Image.Channels);
            var acc = new double[ch][]; for (int c = 0; c < ch; c++) acc[c] = new double[W * H];
            var ws = new double[W * H];
            foreach (var t in tiles)
            {
                var img = t.Image;
                double ox = t.X - minX, oy = t.Y - minY;
                for (int y = 0; y < img.Height; y++)
                    for (int x = 0; x < img.Width; x++)
                    {
                        int X = (int)Math.Round(x + ox), Y = (int)Math.Round(y + oy);
                        if (X < 0 || Y < 0 || X >= W || Y >= H) continue;
                        // feather weight: distance to tile border
                        double wt = Math.Min(Math.Min(x + 1, img.Width - x), Math.Min(y + 1, img.Height - y));
                        wt = Math.Min(1, wt / 40.0);
                        int i = Y * W + X;
                        for (int c = 0; c < ch; c++) acc[c][i] += img.Data[Math.Min(c, img.Channels - 1)][y * img.Width + x] * wt;
                        ws[i] += wt;
                    }
            }
            var o = new AstroImage(W, H, ch);
            for (int c = 0; c < ch; c++) for (int i = 0; i < ws.Length; i++) o.Data[c][i] = ws[i] > 0 ? (float)(acc[c][i] / ws[i]) : 0;
            return o;
        }

        private static float[] Patch(Tile t, int gx, int gy, int s)
        {
            var p = new float[s * s];
            var lum = t.Image.Luminance();
            for (int y = 0; y < s; y++)
                for (int x = 0; x < s; x++)
                {
                    double sx = gx + x - t.X, sy = gy + y - t.Y;
                    float v = Geometry.SampleAt(lum, t.Image.Width, t.Image.Height, sx, sy, Interpolation.Bilinear);
                    p[y * s + x] = float.IsNaN(v) ? 0 : v;
                }
            return p;
        }
    }
}
