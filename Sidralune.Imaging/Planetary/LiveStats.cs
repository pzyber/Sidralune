using System;

namespace Sidralune.Imaging.Planetary
{
    /// <summary>
    /// What a planetary live view shows beside each frame: how bright the brightest pixel is, how much of the object is
    /// clipped, and a sharpness number that rises as focus improves.
    /// </summary>
    public class LiveStats
    {
        /// <summary>The brightest pixel as a fraction of full scale (0-1).</summary>
        public double Peak;
        /// <summary>Fraction of the object's pixels at or above the saturation level (of the whole frame when no object is found).</summary>
        public double Saturated;
        /// <summary>Sharpness of the object (FrameQuality's gradient measure, brightness-normalised): larger is sharper.</summary>
        public double Focus;
        public bool ObjectFound;

        public static LiveStats Measure(AstroImage img, double saturation = 0.98)
        {
            var lum = img.Channels == 1 ? img.Data[0] : img.Luminance();
            int w = img.Width, h = img.Height;
            var s = new LiveStats();
            float peak = 0;
            foreach (var v in lum) if (v > peak) peak = v;
            s.Peak = peak;
            // the object with a margin around it, so sharpness and clipping are judged on the planet, not the black sky
            int x0 = 0, y0 = 0, x1 = w, y1 = h;
            var ob = ObjectDetection.Detect(lum, w, h);
            if (ob.Found)
            {
                s.ObjectFound = true;
                int mx = Math.Max(4, (ob.X1 - ob.X0) / 4), my = Math.Max(4, (ob.Y1 - ob.Y0) / 4);
                x0 = Math.Max(0, ob.X0 - mx); y0 = Math.Max(0, ob.Y0 - my);
                x1 = Math.Min(w, ob.X1 + mx + 1); y1 = Math.Min(h, ob.Y1 + my + 1);
            }
            // clipping is counted on the object's own pixels (the whole frame when there is no object)
            int sx0 = ob.Found ? ob.X0 : 0, sy0 = ob.Found ? ob.Y0 : 0, sx1 = ob.Found ? Math.Min(w, ob.X1 + 1) : w, sy1 = ob.Found ? Math.Min(h, ob.Y1 + 1) : h;
            long sat = 0, n = 0;
            for (int y = sy0; y < sy1; y++)
                for (int x = sx0; x < sx1; x++, n++)
                    if (lum[y * w + x] >= saturation) sat++;
            s.Saturated = n > 0 ? (double)sat / n : 0;
            s.Focus = x1 - x0 > 4 && y1 - y0 > 4 ? FrameQuality.Measure(lum, w, h, QualityMetric.Gradient, x0, y0, x1 - x0, y1 - y0) : 0;
            return s;
        }
    }
}
