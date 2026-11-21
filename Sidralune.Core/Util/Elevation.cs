using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace Sidralune.Core.Util
{
    /// <summary>
    /// The height of the land above sea level anywhere on the Earth, from NOAA's ETOPO1 (1 arc-minute, about 1.85 km) when
    /// it is installed in Settings → Data. Kept as a <see cref="TileGrid"/> without the open sea. Heights are above mean
    /// sea level (the geoid), within about ±100 m of the height above the ellipsoid that eclipse calculations use.
    /// </summary>
    public sealed class Elevation
    {
        public const string DatasetId = "etopo1";
        private readonly TileGrid _grid;
        private static readonly object Sync = new object();
        private static Elevation _current;
        private static string _currentStamp;

        /// <summary>A short name of the source, for "from ETOPO1".</summary>
        public string Source { get; private set; }

        private Elevation(TileGrid g, string source) { _grid = g; Source = source; }

        /// <summary>Reads an elevation file (as <see cref="Prepare"/> writes it).</summary>
        public static Elevation Open(string path, string source = "ETOPO1") => new Elevation(TileGrid.Open(path), source);

        /// <summary>Used instead of the installed dataset when set (tests).</summary>
        public static Elevation Override;
        /// <summary>False to leave the installed dataset unused (tests that need the same results with it or without).</summary>
        public static bool UseInstalled = true;

        /// <summary>The installed elevation data, or null when there is none.</summary>
        public static Elevation Current
        {
            get
            {
                if (Override != null) return Override;
                if (!UseInstalled) return null;
                lock (Sync)
                {
                    var d = DataManager.Get(DatasetId);
                    string f = d?.LocalPath;
                    string stamp = f != null && File.Exists(f) ? new FileInfo(f).Length + ":" + File.GetLastWriteTimeUtc(f).Ticks : "-";
                    if (stamp == _currentStamp) return _current;
                    _currentStamp = stamp;
                    _current = null;
                    if (stamp != "-")
                        try { _current = Open(f); }
                        catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException) { Log.Warn("Data", "The elevation data could not be read: " + ex.Message); }
                    return _current;
                }
            }
        }

        /// <summary>
        /// The height in metres at a place: bilinear between the grid's nodes, 0 at sea. Depths are kept as 0, so a coast
        /// isn't pulled below sea level by the sea floor next to it; the few places on land below sea level (the Dead Sea)
        /// read 0 too, a difference of a tenth of a second at most in an eclipse. Null outside the grid.
        /// </summary>
        public double? MetresAt(double lat, double lon)
        {
            lon = ((lon + 180) % 360 + 360) % 360 - 180;
            double h = _grid.At(lat, lon);
            if (double.IsNaN(h)) return null;
            return h;
        }

        /// <summary>
        /// Turns the downloaded ETOPO1 zip (an ESRI-style .hdr and a .bin of 16-bit heights, rows from north to south)
        /// into the tile grid: the tiles with any land above sea level, the rest read as 0.
        /// </summary>
        public static void Prepare(string zipPath, string outPath)
        {
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                var bin = zip.Entries.FirstOrDefault(e => e.Name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) || e.Name.EndsWith(".flt", StringComparison.OrdinalIgnoreCase));
                var hdr = zip.Entries.FirstOrDefault(e => e.Name.EndsWith(".hdr", StringComparison.OrdinalIgnoreCase));
                if (bin == null) throw new InvalidDataException("No .bin grid in " + Path.GetFileName(zipPath));
                var h = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (hdr != null)
                    using (var r = new StreamReader(hdr.Open()))
                        for (string line; (line = r.ReadLine()) != null;)
                        {
                            var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                            if (parts.Length >= 2) h[parts[0]] = parts[1];
                        }
                Func<string, double, double> num = (k, def) => { string v; double x; return h.TryGetValue(k, out v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out x) ? x : def; };
                int cols = (int)num("NCOLS", 21601), rows = (int)num("NROWS", 10801);
                double cell = num("CELLSIZE", 1 / 60.0);
                double west = h.ContainsKey("XLLCENTER") ? num("XLLCENTER", -180) : num("XLLCORNER", -180 - cell / 2) + cell / 2;
                double south = h.ContainsKey("YLLCENTER") ? num("YLLCENTER", -90) : num("YLLCORNER", -90 - cell / 2) + cell / 2;
                int nodata = (int)num("NODATA_VALUE", -32768);
                string order; h.TryGetValue("BYTEORDER", out order);
                bool swap = order != null && order.StartsWith("MSB", StringComparison.OrdinalIgnoreCase);
                if (bin.Length != (long)cols * rows * 2) throw new InvalidDataException("The grid is " + bin.Length + " bytes, not " + cols + " × " + rows + " heights");
                Write(outPath, cols, rows, west, south + (rows - 1) * cell, cell, bin.Open(), swap, nodata);
            }
        }

        /// <summary>Writes heights (16-bit, a row at a time from north to south) as the tile grid.</summary>
        public static void Write(string outPath, int cols, int rows, double west, double north, double cell, Stream heights, bool bigEndian = false, int nodata = short.MinValue)
        {
            using (heights)
            using (var w = new TileGrid.Writer(outPath, cols, rows, west, north, cell, 1, false, (tr, tc, t) => { foreach (short v in t) if (v > 0) return true; return false; }))
            {
                var bytes = new byte[cols * 2];
                var row = new short[cols];
                for (int r = 0; r < rows; r++)
                {
                    int got = 0;
                    while (got < bytes.Length) { int k = heights.Read(bytes, got, bytes.Length - got); if (k <= 0) throw new EndOfStreamException("The grid ends at row " + r); got += k; }
                    for (int c = 0; c < cols; c++)
                    {
                        int v = bigEndian ? (short)(bytes[2 * c] << 8 | bytes[2 * c + 1]) : (short)(bytes[2 * c] | bytes[2 * c + 1] << 8);
                        row[c] = v == nodata || v == short.MaxValue || v < 0 ? (short)0 : (short)v; // the sea floor as sea level
                    }
                    w.AddRow(row);
                }
            }
        }
    }
}
