using System;
using System.IO;
using Sidralune.Core.Mathematics;
using Sidralune.Core.Util;

namespace Sidralune.Core.Ephemeris
{
    /// <summary>
    /// The Moon's real edge for eclipse contacts: heights from LRO LOLA (16 or 64 pixels per degree, installed in Settings →
    /// Data), kept only in the band that can ever be on the limb, and the limb profile they make as seen from the Earth at
    /// a moment: how far the Moon's outline reaches beyond a sphere of 1737.4 km at each position angle on the sky.
    /// </summary>
    public sealed class LunarLimb
    {
        public const string Dataset16 = "lola-16", Dataset64 = "lola-64";
        /// <summary>LOLA's reference radius, km: heights are measured from it.</summary>
        public const double RadiusKm = 1737.4;
        /// <summary>The band kept: angles from the mean sub-Earth point (0°, 0°) from 90° − this to 90° + this.</summary>
        public const double BandDeg = 15;
        /// <summary>The depth either side of the limb plane looked at for each position angle (a point 5° behind or in front
        /// of the limb shows above it only if it stands more than 6.6 km higher, rarer than that on the Moon).</summary>
        public const double DepthDeg = 5;

        private readonly TileGrid _grid;
        public int PixelsPerDegree { get; private set; }

        private LunarLimb(TileGrid g, int ppd) { _grid = g; PixelsPerDegree = ppd; }

        public static LunarLimb Open(string path) { var g = TileGrid.Open(path); return new LunarLimb(g, (int)Math.Round(1 / g.Cell)); }

        /// <summary>Used instead of the installed data when set (tests).</summary>
        public static LunarLimb Override;
        /// <summary>False to leave the installed data unused (tests that need the same results with it or without).</summary>
        public static bool UseInstalled = true;
        private static readonly object Sync = new object();
        private static LunarLimb _current;
        private static string _currentStamp;

        /// <summary>The finest installed topography (64 pixels per degree before 16), or null when none is.</summary>
        public static LunarLimb Current
        {
            get
            {
                if (Override != null) return Override;
                if (!UseInstalled) return null;
                lock (Sync)
                {
                    string path = null;
                    foreach (var id in new[] { Dataset64, Dataset16 })
                    {
                        var d = DataManager.Get(id);
                        if (d != null && File.Exists(d.LocalPath)) { path = d.LocalPath; break; }
                    }
                    string stamp = path == null ? "-" : path + ":" + new FileInfo(path).Length + ":" + File.GetLastWriteTimeUtc(path).Ticks;
                    if (stamp == _currentStamp) return _current;
                    _currentStamp = stamp;
                    _current = null;
                    if (path != null)
                        try { _current = Open(path); }
                        catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException) { Log.Warn("Data", "The Moon's topography could not be read: " + ex.Message); }
                    return _current;
                }
            }
        }

        /// <summary>The height above 1737.4 km at a selenographic place (east longitude), km, between the grid's points.</summary>
        public double HeightKm(double latDeg, double lonDeg) => _grid.At(latDeg, lonDeg);

        /// <summary>
        /// Turns a downloaded LOLA LDEM grid (16-bit little-endian heights in half metres above 1737.4 km, simple
        /// cylindrical, rows from 90° N, columns from 0° E, pixel centres at half steps) into the tile grid, keeping the
        /// tiles with any point in the limb band.
        /// </summary>
        public static void Prepare(string imgPath, string outPath, int ppd)
        {
            int cols = 360 * ppd, rows = 180 * ppd;
            long expected = (long)cols * rows * 2;
            using (var f = new FileStream(imgPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
            {
                if (f.Length != expected) throw new InvalidDataException(Path.GetFileName(imgPath) + " is " + f.Length + " bytes, not a " + ppd + " pixels per degree LOLA grid (" + expected + ")");
                Write(outPath, ppd, f);
            }
        }

        /// <summary>Writes LDEM heights (a row at a time from the north) as the tile grid of the limb band.</summary>
        public static void Write(string outPath, int ppd, Stream heights)
        {
            int cols = 360 * ppd, rows = 180 * ppd;
            double cell = 1.0 / ppd;
            // a point is in the band when its angle from (0°, 0°) is within BandDeg of 90°: |cos φ cos λ| ≤ sin BandDeg
            var cosLat = new double[rows]; var cosLon = new double[cols];
            for (int r = 0; r < rows; r++) cosLat[r] = Math.Cos((90 - (r + 0.5) * cell) * AstroMath.Deg2Rad);
            for (int c = 0; c < cols; c++) cosLon[c] = Math.Cos((c + 0.5) * cell * AstroMath.Deg2Rad);
            double limit = Math.Sin(BandDeg * AstroMath.Deg2Rad);
            const int tile = 64;
            using (var w = new TileGrid.Writer(outPath, cols, rows, 0.5 * cell, 90 - 0.5 * cell, cell, 0.0005, true, (tr, tc, t) =>
            {
                // the tile's corners and edges are enough: |cos φ cos λ| changes smoothly across a tile
                int r0 = tr * tile, c0 = tc * tile, r1 = Math.Min(rows, r0 + tile) - 1, c1 = Math.Min(cols, c0 + tile) - 1;
                double lo = double.MaxValue, hi = double.MinValue;
                for (int r = r0; r <= r1; r++)
                    for (int c = c0; c <= c1; c += (r == r0 || r == r1) ? 1 : Math.Max(1, c1 - c0))
                    {
                        double v = cosLat[r] * cosLon[c];
                        lo = Math.Min(lo, v); hi = Math.Max(hi, v);
                    }
                return !(lo > limit || hi < -limit);
            }, tile))
            {
                var bytes = new byte[cols * 2];
                var row = new short[cols];
                for (int r = 0; r < rows; r++)
                {
                    int got = 0;
                    while (got < bytes.Length) { int k = heights.Read(bytes, got, bytes.Length - got); if (k <= 0) throw new EndOfStreamException("The grid ends at row " + r); got += k; }
                    Buffer.BlockCopy(bytes, 0, row, 0, bytes.Length); // little-endian, as Windows
                    w.AddRow(row);
                }
            }
        }

        /// <summary>
        /// The limb profile seen looking along <paramref name="view"/> (ICRF unit vector from the observer to the Moon) at a
        /// TDB Julian date, its position angles measured from <paramref name="north"/> through <paramref name="east"/> (ICRF
        /// unit vectors across the line of sight): the Moon's outline beyond 1737.4 km, km, every <see cref="LimbProfile.StepDeg"/>.
        /// </summary>
        public LimbProfile Profile(Vec3 view, Vec3 north, Vec3 east, double jdTDB)
        {
            // the Moon's mean Earth/polar axis frame (the one LOLA uses), from the IAU model at the time the light left
            double ra0, dec0, W;
            PlanetPhysics.RotationElements(Body.Moon, jdTDB - 1.28 / 86400, out ra0, out dec0, out W);
            double a0 = ra0 * AstroMath.Deg2Rad, d0 = dec0 * AstroMath.Deg2Rad, w = W * AstroMath.Deg2Rad;
            var pole = Vec3.FromSpherical(a0, d0);
            var node = Vec3.FromSpherical(a0 + AstroMath.HalfPi, 0);
            var q = Vec3.Cross(pole, node);
            var meridian = node * Math.Cos(w) + q * Math.Sin(w);
            var eastward = Vec3.Cross(pole, meridian);
            var toObserver = -1 * view;

            var p = new LimbProfile { StepDeg = 360.0 / LimbProfile.Samples, Heights = new double[LimbProfile.Samples] };
            double dth = Math.Max(1.0 / PixelsPerDegree, 0.03125), maxAll = double.MinValue;
            int nth = (int)Math.Ceiling(DepthDeg / dth);
            var cth = new double[2 * nth + 1]; var sth = new double[2 * nth + 1];
            for (int k = -nth; k <= nth; k++) { double th = k * dth * AstroMath.Deg2Rad; cth[k + nth] = Math.Cos(th); sth[k + nth] = Math.Sin(th); }
            _grid.Batch(() =>
            {
                for (int i = 0; i < LimbProfile.Samples; i++)
                {
                    double pa = i * p.StepDeg * AstroMath.Deg2Rad;
                    var limb = north * Math.Cos(pa) + east * Math.Sin(pa);
                    double best = double.MinValue;
                    for (int k = 0; k < cth.Length; k++)
                    {
                        // a point at depth θ behind (θ < 0) or in front of the limb plane: it reaches (R + h) cos θ across
                        var s = limb * cth[k] + toObserver * sth[k];
                        double lat = Math.Asin(Math.Max(-1, Math.Min(1, Vec3.Dot(s, pole)))) * AstroMath.Rad2Deg;
                        double lon = Math.Atan2(Vec3.Dot(s, eastward), Vec3.Dot(s, meridian)) * AstroMath.Rad2Deg;
                        double h = _grid.At(lat, lon < 0 ? lon + 360 : lon);
                        if (double.IsNaN(h)) continue;
                        double reach = (RadiusKm + h) * cth[k] - RadiusKm;
                        if (reach > best) best = reach;
                    }
                    p.Heights[i] = best == double.MinValue ? 0 : best;
                    maxAll = Math.Max(maxAll, p.Heights[i]);
                }
                return 0;
            });
            p.MaxKm = maxAll;
            return p;
        }
    }

    /// <summary>A lunar limb profile: the outline's height above 1737.4 km (km) by position angle on the sky.</summary>
    public sealed class LimbProfile
    {
        public const int Samples = 1800;
        public double StepDeg;
        public double[] Heights;
        public double MaxKm;

        /// <summary>The height at a position angle (degrees, any value), between samples.</summary>
        public double At(double paDeg)
        {
            double f = paDeg / StepDeg;
            f %= Samples; if (f < 0) f += Samples;
            int i = (int)f; double d = f - i;
            return Heights[i] * (1 - d) + Heights[(i + 1) % Samples] * d;
        }

        /// <summary>A profile of one height all round (tests).</summary>
        public static LimbProfile Uniform(double km)
        {
            var p = new LimbProfile { StepDeg = 360.0 / Samples, Heights = new double[Samples], MaxKm = km };
            for (int i = 0; i < Samples; i++) p.Heights[i] = km;
            return p;
        }
    }
}
