using System;
using System.Collections.Generic;

namespace Sidralune.Imaging
{
    /// <summary>Synthetic star field rendering (used by simulators and tests).</summary>
    public static class Synthetic
    {
        public class SynthStar { public double X, Y, Flux; }

        public static List<SynthStar> RandomStars(int count, int w, int h, int seed)
        {
            var rng = new Random(seed);
            var list = new List<SynthStar>();
            for (int i = 0; i < count; i++)
            {
                // power-law flux distribution, like real star counts
                double f = 0.002 * Math.Pow(10, 2.5 * rng.NextDouble());
                list.Add(new SynthStar { X = rng.NextDouble() * w, Y = rng.NextDouble() * h, Flux = f });
            }
            return list;
        }

        /// <summary>
        /// Render stars (Gaussian PSF, total flux in normalised units) after applying transform t (star coords -> image),
        /// with sky level, read noise, Poisson-like noise and optional hot pixels.
        /// </summary>
        public static AstroImage Render(int w, int h, IEnumerable<SynthStar> stars, Transform2D t, double fwhm, double sky, double noise, int seed,
            int hotPixels = 0, double gainElectrons = 20000)
        {
            var img = new AstroImage(w, h, 1);
            var d = img.Data[0];
            var rng = new Random(seed);
            double sigma = fwhm / 2.3548;
            int r = (int)Math.Ceiling(sigma * 4);
            foreach (var s in stars)
            {
                double x, y;
                t.Apply(s.X, s.Y, out x, out y);
                if (x < -r || y < -r || x > w + r || y > h + r) continue;
                double amp = s.Flux / (2 * Math.PI * sigma * sigma);
                for (int yy = (int)y - r; yy <= (int)y + r; yy++)
                {
                    if (yy < 0 || yy >= h) continue;
                    for (int xx = (int)x - r; xx <= (int)x + r; xx++)
                    {
                        if (xx < 0 || xx >= w) continue;
                        double dx = xx - x, dy = yy - y;
                        d[yy * w + xx] += (float)(amp * Math.Exp(-0.5 * (dx * dx + dy * dy) / (sigma * sigma)));
                    }
                }
            }
            for (int i = 0; i < d.Length; i++)
            {
                double v = d[i] + sky;
                double shot = Math.Sqrt(Math.Max(0, v) / gainElectrons);
                d[i] = (float)Math.Max(0, Math.Min(1, v + Gauss(rng) * Math.Sqrt(noise * noise + shot * shot)));
            }
            for (int i = 0; i < hotPixels; i++) d[rng.Next(d.Length)] = 1.0f;
            return img;
        }

        private static double Gauss(Random r)
        {
            double u1 = 1.0 - r.NextDouble(), u2 = r.NextDouble();
            return Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
        }

        /// <summary>Render a simple planetary disc with banding, limb darkening and a moving feature (for planetary tests).</summary>
        public static AstroImage Planet(int w, int h, double cx, double cy, double radius, double rotationDeg, double blur, double noise, int seed)
        {
            var img = new AstroImage(w, h, 1);
            var d = img.Data[0];
            var rng = new Random(seed);
            double rot = rotationDeg * Math.PI / 180;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    double dx = (x - cx) / radius, dy = (y - cy) / radius;
                    double rr = dx * dx + dy * dy;
                    if (rr > 1) continue;
                    double mu = Math.Sqrt(1 - rr);
                    double lat = Math.Asin(dy);
                    double lon = Math.Asin(Math.Max(-1, Math.Min(1, dx / Math.Max(1e-6, Math.Cos(lat))))) + rot;
                    double bands = 0.75 + 0.15 * Math.Sin(lat * 9) + 0.05 * Math.Sin(lat * 23);
                    double spot = Math.Exp(-(Math.Pow((lon - 0.3) / 0.15, 2) + Math.Pow((lat + 0.35) / 0.08, 2))) * 0.3;
                    d[y * w + x] = (float)((bands + spot) * (0.4 + 0.6 * mu) * 0.8);
                }
            if (blur > 0)
            {
                var b = Filters.GaussianBlur(d, w, h, blur);
                Array.Copy(b, d, d.Length);
            }
            for (int i = 0; i < d.Length; i++) d[i] = (float)Math.Max(0, d[i] + 0.02 + Gauss(rng) * noise);
            return img;
        }
    }
}
