using System;
using Sidralune.Imaging;
using Sidralune.Imaging.Planetary;

namespace Sidralune.Tests
{
    /// <summary>Synthetic planets for the derotation tests (linked into the application tests too).</summary>
    public static class PlanetRenderer
    {
        // a planet seen as the derotation sees it: an oblate spheroid with its pole at g.PoleAngleDeg, tilted by the
        // sub-observer latitude, perhaps mirrored, with belts and spots fixed on its surface that turn with it, rings
        // when g has them, and a dark shadow at a sky position of its own (X west, Y north, in equatorial radii)
        public static AstroImage Render(int w, int h, DiscGeometry g, double seconds, double periodHours, double[] shadow = null, double shadowRadius = 0)
        {
            bool[] ring;
            return Render(w, h, g, seconds, periodHours, out ring, shadow, shadowRadius);
        }

        public static AstroImage Render(int w, int h, DiscGeometry g, double seconds, double periodHours, out bool[] ringMask, double[] shadow = null, double shadowRadius = 0)
        {
            var img = new AstroImage(w, h, 1);
            var ringPx = new bool[w * h];
            var d = img.Data[0];
            double turn = 2 * Math.PI * seconds / (periodHours * 3600);
            var spots = new[] { new[] { 0.3, -0.35, 0.25 }, new[] { -0.6, 0.2, -0.2 }, new[] { 1.2, 0.45, 0.2 }, new[] { -1.4, -0.1, 0.25 }, new[] { 2.2, 0.05, -0.25 }, new[] { 0.9, -0.6, 0.2 }, new[] { -2.4, 0.5, 0.2 }, new[] { 2.9, -0.3, -0.2 } };
            double pa = g.PoleAngleDeg * Math.PI / 180, ca = Math.Cos(pa), sa = Math.Sin(pa);
            double D = g.SubObserverLatDeg * Math.PI / 180, cD = Math.Cos(D), sD = Math.Sin(D);
            double sx = 0, sy = 0;
            if (shadow != null) SphereDerotation.ImagePoint(g, shadow[0], shadow[1], out sx, out sy);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    double lon, lat;
                    float v = 0;
                    bool globe = SphereDerotation.SurfacePoint(g, x, y, out lon, out lat);
                    if (globe)
                    {
                        double L = lon - turn, val = 0.6 + 0.15 * Math.Sin(9 * lat);
                        foreach (var s in spots)
                        {
                            double dl = Math.IEEERemainder(L - s[0], 2 * Math.PI);
                            val += s[2] * Math.Exp(-(dl * dl + (lat - s[1]) * (lat - s[1])) / (2 * 0.1 * 0.1));
                        }
                        v = (float)val;
                        if (shadow != null && (x - sx) * (x - sx) + (y - sy) * (y - sy) < shadowRadius * shadowRadius) v *= 0.15f;
                    }
                    if (g.RingOuter > 0)
                    {
                        double dx = (x - g.Cx) * (g.Mirrored ? -1 : 1), dy = -(y - g.Cy);
                        double u = dx * ca + dy * sa, vv = -dx * sa + dy * ca;
                        double rho = Math.Sqrt(u * u + vv * vv / (sD * sD)) / g.Radius, zr = -vv * cD / sD;
                        // the globe's depth under the pixel, to know whether the ring is in front of it
                        double zg = double.NegativeInfinity;
                        if (globe)
                        {
                            double Re = g.Radius, Rp = g.Radius * g.Flattening;
                            double A = sD * sD / (Rp * Rp) + cD * cD / (Re * Re), B = 2 * vv * (cD * sD / (Rp * Rp) - sD * cD / (Re * Re)), C = u * u / (Re * Re) + vv * vv * (cD * cD / (Rp * Rp) + sD * sD / (Re * Re)) - 1;
                            zg = (-B + Math.Sqrt(B * B - 4 * A * C)) / (2 * A);
                        }
                        if (rho >= g.RingInner && rho <= g.RingOuter && zr > zg)
                        {
                            v = (float)(rho > 1.95 && rho < 2.02 ? 0.08 : 0.45 + 0.1 * Math.Sin(rho * 25));
                            ringPx[y * w + x] = true;
                        }
                    }
                    d[y * w + x] = v;
                }
            ringMask = ringPx;
            return img;
        }
    }
}
