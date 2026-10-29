using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Sidralune.Core.Mathematics;
using Sidralune.Imaging.Analysis;

namespace Sidralune.Imaging
{
    public enum ScnrMethod { AverageNeutral, MaximumNeutral, Additive }

    public static class ColorOps
    {
        /// <summary>Neutralise the background so that each channel's background equals the average (or a reference region).</summary>
        public static void BackgroundNeutralization(AstroImage img, int[] rect = null)
        {
            if (img.Channels < 3) return;
            var bg = new double[3];
            for (int c = 0; c < 3; c++)
            {
                float[] d = img.Data[c];
                if (rect != null)
                {
                    var vals = new List<double>();
                    for (int y = rect[1]; y < rect[1] + rect[3]; y++) for (int x = rect[0]; x < rect[0] + rect[2]; x++) vals.Add(d[y * img.Width + x]);
                    bg[c] = Stats.Median(vals);
                }
                else bg[c] = ImageStatistics.Compute(d, img.Width, 0).Background;
            }
            double t = bg.Average();
            for (int c = 0; c < 3; c++) { float o = (float)(t - bg[c]); var d = img.Data[c]; for (int i = 0; i < d.Length; i++) d[i] += o; }
        }

        /// <summary>Scale channels by factors (white balance).</summary>
        public static void WhiteBalance(AstroImage img, double kr, double kg, double kb, bool aroundBackground = true)
        {
            if (img.Channels < 3) return;
            var k = new[] { kr, kg, kb };
            for (int c = 0; c < 3; c++)
            {
                double bg = aroundBackground ? ImageStatistics.Compute(img.Data[c], img.Width, 0).Background : 0;
                var d = img.Data[c];
                float fk = (float)k[c], fb = (float)bg;
                for (int i = 0; i < d.Length; i++) d[i] = fb + (d[i] - fb) * fk;
            }
        }

        /// <summary>
        /// Star-based colour calibration: make the median star colour neutral (G2V-like average), using aperture fluxes
        /// of unsaturated stars in each channel. Returns the applied factors.
        /// </summary>
        public static double[] StarColorCalibration(AstroImage img, List<DetectedStar> stars = null)
        {
            if (img.Channels < 3) return new[] { 1.0, 1.0, 1.0 };
            stars = stars ?? StarDetector.Detect(img);
            var ratiosR = new List<double>(); var ratiosB = new List<double>();
            foreach (var s in stars.Where(s => !s.Saturated).Take(300))
            {
                double r = Aperture(img.Data[0], img, s), g = Aperture(img.Data[1], img, s), b = Aperture(img.Data[2], img, s);
                if (r > 0 && g > 0 && b > 0) { ratiosR.Add(g / r); ratiosB.Add(g / b); }
            }
            if (ratiosR.Count < 5) return new[] { 1.0, 1.0, 1.0 };
            double kr = Stats.Median(ratiosR), kb = Stats.Median(ratiosB);
            WhiteBalance(img, kr, 1, kb);
            return new[] { kr, 1.0, kb };
        }

        /// <summary>
        /// Photometric colour calibration: fit channel scale factors so that instrumental colour indices match the catalog
        /// B-V colours of matched stars (pairs of image star and catalog B-V). Uses a Planck-based mapping of B-V to
        /// expected R/G and B/G flux ratios.
        /// </summary>
        public static double[] PhotometricColorCalibration(AstroImage img, IList<Tuple<DetectedStar, double>> starsWithBv)
        {
            if (img.Channels < 3 || starsWithBv.Count < 5) return null;
            var kr = new List<double>(); var kb = new List<double>();
            foreach (var p in starsWithBv)
            {
                var s = p.Item1;
                if (s.Saturated) continue;
                double r = Aperture(img.Data[0], img, s), g = Aperture(img.Data[1], img, s), b = Aperture(img.Data[2], img, s);
                if (r <= 0 || g <= 0 || b <= 0) continue;
                double T = 4600 * (1 / (0.92 * p.Item2 + 1.7) + 1 / (0.92 * p.Item2 + 0.62));
                double er = Planck(610e-9, T) / Planck(530e-9, T), eb = Planck(460e-9, T) / Planck(530e-9, T);
                // normalise expectations to a G2V (5778 K) white point
                double er0 = Planck(610e-9, 5778) / Planck(530e-9, 5778), eb0 = Planck(460e-9, 5778) / Planck(530e-9, 5778);
                kr.Add((er / er0) / (r / g));
                kb.Add((eb / eb0) / (b / g));
            }
            if (kr.Count < 5) return null;
            double fr = Stats.Median(kr), fb = Stats.Median(kb);
            WhiteBalance(img, fr, 1, fb);
            return new[] { fr, 1.0, fb };
        }

        private static double Planck(double lambda, double T) => 1.0 / (Math.Pow(lambda, 5) * (Math.Exp(0.014387769 / (lambda * T)) - 1));

        public static double Aperture(float[] d, AstroImage img, DetectedStar s)
        {
            double r = Math.Max(2.5, 1.5 * s.Fwhm), r2 = r * r, ri = r * 1.5, ro = r * 2.5;
            int w = img.Width, h = img.Height;
            double sum = 0; int n = 0;
            var annulus = new List<double>();
            for (int y = (int)(s.Y - ro); y <= (int)(s.Y + ro); y++)
                for (int x = (int)(s.X - ro); x <= (int)(s.X + ro); x++)
                {
                    if (x < 0 || y < 0 || x >= w || y >= h) continue;
                    double dd = (x - s.X) * (x - s.X) + (y - s.Y) * (y - s.Y);
                    float v = d[y * w + x];
                    if (dd <= r2) { sum += v; n++; }
                    else if (dd >= ri * ri && dd <= ro * ro) annulus.Add(v);
                }
            double bg = annulus.Count > 5 ? Stats.Median(annulus) : 0;
            return sum - bg * n;
        }

        /// <summary>Subtractive Chromatic Noise Reduction for green (PixInsight SCNR).</summary>
        public static void Scnr(AstroImage img, ScnrMethod method = ScnrMethod.AverageNeutral, double amount = 1.0, int channel = 1)
        {
            if (img.Channels < 3) return;
            var c0 = img.Data[(channel + 1) % 3]; var c1 = img.Data[(channel + 2) % 3]; var g = img.Data[channel];
            for (int i = 0; i < g.Length; i++)
            {
                double m;
                switch (method)
                {
                    case ScnrMethod.MaximumNeutral: m = Math.Max(c0[i], c1[i]); break;
                    case ScnrMethod.Additive: m = Math.Min(1, c0[i] + c1[i]); break;
                    default: m = 0.5 * (c0[i] + c1[i]); break;
                }
                double ng = Math.Min(g[i], m);
                g[i] = (float)(g[i] * (1 - amount) + ng * amount);
            }
        }

        public static void RgbToHsl(double r, double g, double b, out double h, out double s, out double l)
        {
            double mx = Math.Max(r, Math.Max(g, b)), mn = Math.Min(r, Math.Min(g, b));
            l = (mx + mn) / 2;
            if (mx == mn) { h = s = 0; return; }
            double d = mx - mn;
            s = l > 0.5 ? d / (2 - mx - mn) : d / (mx + mn);
            if (mx == r) h = (g - b) / d + (g < b ? 6 : 0);
            else if (mx == g) h = (b - r) / d + 2;
            else h = (r - g) / d + 4;
            h /= 6;
        }

        public static void HslToRgb(double h, double s, double l, out double r, out double g, out double b)
        {
            if (s == 0) { r = g = b = l; return; }
            double q = l < 0.5 ? l * (1 + s) : l + s - l * s, p = 2 * l - q;
            r = Hue(p, q, h + 1.0 / 3); g = Hue(p, q, h); b = Hue(p, q, h - 1.0 / 3);
        }

        private static double Hue(double p, double q, double t)
        {
            if (t < 0) t += 1; if (t > 1) t -= 1;
            if (t < 1.0 / 6) return p + (q - p) * 6 * t;
            if (t < 0.5) return q;
            if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
            return p;
        }

        /// <summary>Saturation boost with optional protection of the background (only above bg + k σ).</summary>
        public static void Saturation(AstroImage img, double amount, double backgroundProtect = 0.0)
        {
            if (img.Channels < 3) return;
            var r = img.Data[0]; var g = img.Data[1]; var b = img.Data[2];
            Parallel.For(0, img.Height, Core.Util.StopRequest.Options, y =>
            {
                for (int x = 0; x < img.Width; x++)
                {
                    int i = y * img.Width + x;
                    double L = (r[i] + g[i] + b[i]) / 3;
                    double k = amount;
                    if (backgroundProtect > 0) k = 1 + (amount - 1) * AstroMath.Clamp((L - backgroundProtect) / Math.Max(1e-6, backgroundProtect), 0, 1);
                    r[i] = (float)(L + (r[i] - L) * k); g[i] = (float)(L + (g[i] - L) * k); b[i] = (float)(L + (b[i] - L) * k);
                }
            });
        }

        /// <summary>Hue shift (degrees) and saturation/lightness adjustments in HSL.</summary>
        public static void HueSaturationLightness(AstroImage img, double hueShiftDeg, double satScale, double lightScale)
        {
            if (img.Channels < 3) return;
            var R = img.Data[0]; var G = img.Data[1]; var B = img.Data[2];
            Parallel.For(0, img.PixelCount, Core.Util.StopRequest.Options, i =>
            {
                double h, s, l, r, g, b;
                RgbToHsl(AstroMath.Clamp(R[i], 0, 1), AstroMath.Clamp(G[i], 0, 1), AstroMath.Clamp(B[i], 0, 1), out h, out s, out l);
                h = (h + hueShiftDeg / 360.0 + 1) % 1;
                s = AstroMath.Clamp(s * satScale, 0, 1);
                l = AstroMath.Clamp(l * lightScale, 0, 1);
                HslToRgb(h, s, l, out r, out g, out b);
                R[i] = (float)r; G[i] = (float)g; B[i] = (float)b;
            });
        }

        /// <summary>LRGB combination in CIE L*a*b*-like space: replace lightness of RGB with L (both non-linear).</summary>
        public static AstroImage LrgbCombine(AstroImage lum, AstroImage rgb, double lumWeight = 1.0, double chromaBoost = 1.0)
        {
            var o = rgb.Clone();
            var L = lum.Data[0];
            var r = o.Data[0]; var g = o.Data[1]; var b = o.Data[2];
            Parallel.For(0, o.PixelCount, Core.Util.StopRequest.Options, i =>
            {
                double Y = 0.2126 * r[i] + 0.7152 * g[i] + 0.0722 * b[i];
                double Lt = L[i] * lumWeight + Y * (1 - lumWeight);
                double k = Y > 1e-6 ? Lt / Y : 1;
                double nr = r[i] * k, ng = g[i] * k, nb = b[i] * k;
                double m = (nr + ng + nb) / 3;
                r[i] = (float)(m + (nr - m) * chromaBoost); g[i] = (float)(m + (ng - m) * chromaBoost); b[i] = (float)(m + (nb - m) * chromaBoost);
            });
            return o;
        }

        /// <summary>Narrowband palette composition from Ha, OIII, SII (any may be null).</summary>
        public static AstroImage NarrowbandPalette(AstroImage ha, AstroImage oiii, AstroImage sii, string palette)
        {
            var any = ha ?? oiii ?? sii;
            int w = any.Width, h = any.Height, n = w * h;
            Func<AstroImage, float[]> D = im => im != null ? im.Data[0] : new float[n];
            var H = D(ha); var O = D(oiii); var S = D(sii);
            var o = new AstroImage(w, h, 3) { Header = any.Header.Clone() };
            var R = o.Data[0]; var G = o.Data[1]; var B = o.Data[2];
            for (int i = 0; i < n; i++)
            {
                switch (palette.ToUpperInvariant())
                {
                    case "SHO": R[i] = S[i]; G[i] = H[i]; B[i] = O[i]; break;          // Hubble palette
                    case "HOO": R[i] = H[i]; G[i] = O[i]; B[i] = O[i]; break;
                    case "HSO": R[i] = H[i]; G[i] = S[i]; B[i] = O[i]; break;
                    case "OHS": R[i] = O[i]; G[i] = H[i]; B[i] = S[i]; break;
                    case "FORAXX": // dynamic HOO blend (Paul Foraxx): G = f(Ha,OIII) by OIII strength
                        {
                            double t = Math.Pow(Math.Max(0, Math.Min(1, H[i] * O[i])), 1 - Math.Max(0, Math.Min(1, H[i] * O[i])));
                            R[i] = H[i]; G[i] = (float)(t * H[i] + (1 - t) * O[i]); B[i] = O[i]; break;
                        }
                    default: R[i] = H[i]; G[i] = 0.5f * (H[i] + O[i]); B[i] = O[i]; break; // natural-ish
                }
            }
            return o;
        }

        /// <summary>Invert (negative) — useful for solar images and faint feature hunting.</summary>
        public static void Invert(AstroImage img) => img.Apply(v => 1 - v);

        /// <summary>Colourise a mono image with a tint (e.g. H-alpha solar: 1.0, 0.45, 0.15).</summary>
        public static AstroImage Colorize(AstroImage mono, double r, double g, double b, double gammaR = 1.0, double gammaG = 1.4, double gammaB = 2.0)
        {
            var o = new AstroImage(mono.Width, mono.Height, 3) { Header = mono.Header.Clone() };
            var d = mono.Data[0];
            for (int i = 0; i < d.Length; i++)
            {
                double v = AstroMath.Clamp(d[i], 0, 1);
                o.Data[0][i] = (float)(r * Math.Pow(v, gammaR));
                o.Data[1][i] = (float)(g * Math.Pow(v, gammaG) * 2);
                o.Data[2][i] = (float)(b * Math.Pow(v, gammaB) * 3);
            }
            return o;
        }

        /// <summary>Align colour channels to green by phase correlation (atmospheric dispersion / chromatic aberration fix).</summary>
        public static double[][] AlignChannels(AstroImage img, System.Threading.CancellationToken ct = default(System.Threading.CancellationToken))
        {
            if (img.Channels < 3) return null;
            int w = img.Width, h = img.Height;
            int size = Math.Min(Core.Mathematics.Fft.NextPowerOfTwo(Math.Min(w, h)) / 2, 512);
            int x0 = (w - size) / 2, y0 = (h - size) / 2;
            Func<float[], float[]> crop = d => { var c = new float[size * size]; for (int y = 0; y < size; y++) Array.Copy(d, (y0 + y) * w + x0, c, y * size, size); return c; };
            var g = crop(img.Data[1]);
            var win = Core.Mathematics.Fft.Hann2D(size, size);
            var shifts = new double[3][];
            for (int c = 0; c < 3; c += 2)
            {
                ct.ThrowIfCancellationRequested(); // a stop takes effect between the channels
                double dx, dy;
                Core.Mathematics.Fft.PhaseCorrelate(g, crop(img.Data[c]), size, size, out dx, out dy, win);
                shifts[c] = new[] { dx, dy };
                var plane = new AstroImage(w, h, new[] { img.Data[c] });
                img.Data[c] = Geometry.Shift(plane, dx, dy, Interpolation.Bicubic).Data[0];
            }
            shifts[1] = new[] { 0.0, 0.0 };
            return shifts;
        }
    }
}
