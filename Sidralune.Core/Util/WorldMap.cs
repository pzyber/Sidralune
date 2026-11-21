using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace Sidralune.Core.Util
{
    /// <summary>A place named on the map.</summary>
    public class MapPlace
    {
        public string Name;
        public float Lat, Lon;
        /// <summary>Natural Earth's scale rank: 0 for the most important places, larger for smaller ones.</summary>
        public int Rank;
        public long Population;
    }

    /// <summary>
    /// The world for the eclipse map, from Natural Earth (public domain): land, lakes, country borders and populated places.
    /// The 1:110m land, lakes and borders are built in; the 1:50m ones and the places come with the Natural Earth datasets
    /// in Settings → Data. Each outline is a float array of longitude, latitude pairs (degrees).
    /// </summary>
    public sealed class WorldMap
    {
        public List<float[]> Land = new List<float[]>(), Lakes = new List<float[]>(), Borders = new List<float[]>();
        public List<MapPlace> Places = new List<MapPlace>();
        /// <summary>What the map was made from, for the map's corner.</summary>
        public string Source = "";

        public static readonly string[] DatasetIds = { "ne-land", "ne-lakes", "ne-borders", "ne-places" };

        private static WorldMap _current;
        private static string _currentStamp;
        private static readonly object Sync = new object();

        /// <summary>
        /// The most detailed map installed: the Natural Earth datasets where they are, the built-in 1:110m map for the rest.
        /// Read once and kept until the datasets change.
        /// </summary>
        public static WorldMap Current
        {
            get
            {
                lock (Sync)
                {
                    var files = DatasetIds.Select(id => { var d = DataManager.Get(id); return d == null ? null : Path.Combine(d.Folder, d.FileName); }).ToArray();
                    string stamp = string.Join("|", files.Select(f => f != null && File.Exists(f) ? f + ":" + new FileInfo(f).Length + ":" + File.GetLastWriteTimeUtc(f).Ticks : "-"));
                    if (_current != null && stamp == _currentStamp) return _current;
                    _current = Build(files, stamp);
                    _currentStamp = stamp;
                    return _current;
                }
            }
        }

        private static WorldMap Build(string[] files, string stamp)
        {
            var builtIn = BuiltIn();
            if (files.All(f => f == null || !File.Exists(f))) return builtIn;
            // the downloaded files read once, then kept in a compact cache next to them
            string cache = Path.Combine(AppPaths.Maps, "worldmap.cache");
            string stampFile = cache + ".stamp";
            try
            {
                if (File.Exists(cache) && File.Exists(stampFile) && File.ReadAllText(stampFile) == stamp)
                    using (var s = File.OpenRead(cache)) return Read(s);
            }
            catch (Exception) { }
            Func<int, string> file = i => files[i] != null && File.Exists(files[i]) ? files[i] : null;
            WorldMap map;
            try { map = FromGeoJson(file(0), file(1), file(2), file(3)); }
            catch (Exception ex) { Log.Write(Log.Level.Warning, "World map", ex.Message); return builtIn; }
            if (file(0) == null) { map.Land = builtIn.Land; }
            if (file(1) == null) { map.Lakes = builtIn.Lakes; }
            if (file(2) == null) { map.Borders = builtIn.Borders; }
            map.Source = file(0) != null ? "Natural Earth 1:50m" : builtIn.Source;
            try
            {
                using (var s = File.Create(cache)) map.Write(s);
                File.WriteAllText(stampFile, stamp);
            }
            catch (Exception) { }
            return map;
        }

        /// <summary>The built-in 1:110m map (an empty one if the resource is missing).</summary>
        public static WorldMap BuiltIn()
        {
            var asm = typeof(WorldMap).Assembly;
            using (var s = asm.GetManifestResourceStream("Sidralune.Core.Resources.world110.bin"))
                return s == null ? new WorldMap { Source = "" } : Read(s);
        }

        // ------------------------------------------------------------------ GeoJSON

        /// <summary>A map from Natural Earth GeoJSON files (any may be null).</summary>
        public static WorldMap FromGeoJson(string landFile, string lakesFile, string bordersFile, string placesFile)
        {
            var map = new WorldMap { Source = "Natural Earth" };
            if (landFile != null) ReadShapes(landFile, map.Land, null);
            if (lakesFile != null) ReadShapes(lakesFile, map.Lakes, null);
            if (bordersFile != null) ReadShapes(bordersFile, map.Borders, null);
            if (placesFile != null) ReadShapes(placesFile, null, map.Places);
            map.Places = map.Places.OrderBy(p => p.Rank).ThenByDescending(p => p.Population).ToList();
            return map;
        }

        private static void ReadShapes(string file, List<float[]> lines, List<MapPlace> places)
        {
            var root = Json.Parse(File.ReadAllText(file, Encoding.UTF8)) as Dictionary<string, object>;
            var features = root?["features"] as List<object>;
            if (features == null) throw new InvalidDataException(Path.GetFileName(file) + " is not GeoJSON");
            foreach (var fo in features)
            {
                var f = fo as Dictionary<string, object>;
                var g = f?["geometry"] as Dictionary<string, object>;
                if (g == null) continue;
                string type = g["type"] as string;
                var coords = g["coordinates"] as List<object>;
                if (coords == null) continue;
                if (type == "Point" && places != null)
                {
                    var props = f.ContainsKey("properties") ? f["properties"] as Dictionary<string, object> : null;
                    Func<string, object> prop = name => props == null ? null : props.Where(kv => string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Value).FirstOrDefault();
                    places.Add(new MapPlace
                    {
                        Name = Convert.ToString(prop("name") ?? prop("nameascii") ?? ""),
                        Lon = (float)Convert.ToDouble(coords[0]), Lat = (float)Convert.ToDouble(coords[1]),
                        Rank = prop("scalerank") != null ? Convert.ToInt32(prop("scalerank")) : 10,
                        Population = prop("pop_max") != null ? Convert.ToInt64(Convert.ToDouble(prop("pop_max"))) : 0
                    });
                }
                if (lines == null) continue;
                if (type == "LineString") lines.Add(Ring(coords));
                else if (type == "MultiLineString" || type == "Polygon") foreach (var r in coords) lines.Add(Ring((List<object>)r));
                else if (type == "MultiPolygon") foreach (var poly in coords) foreach (var r in (List<object>)poly) lines.Add(Ring((List<object>)r));
            }
        }

        private static float[] Ring(List<object> pts)
        {
            var a = new float[pts.Count * 2];
            for (int i = 0; i < pts.Count; i++)
            {
                var p = (List<object>)pts[i];
                a[2 * i] = (float)Convert.ToDouble(p[0]); a[2 * i + 1] = (float)Convert.ToDouble(p[1]);
            }
            return a;
        }

        // ------------------------------------------------------------------ compact form

        private const string Magic = "SLWM1";

        public void Write(Stream s)
        {
            using (var z = new GZipStream(s, CompressionLevel.Optimal, true))
            using (var w = new BinaryWriter(z, Encoding.UTF8))
            {
                w.Write(Magic); w.Write(Source ?? "");
                foreach (var layer in new[] { Land, Lakes, Borders })
                {
                    w.Write(layer.Count);
                    foreach (var r in layer) { w.Write(r.Length); foreach (var v in r) w.Write(v); }
                }
                w.Write(Places.Count);
                foreach (var p in Places) { w.Write(p.Name ?? ""); w.Write(p.Lat); w.Write(p.Lon); w.Write(p.Rank); w.Write(p.Population); }
            }
        }

        public static WorldMap Read(Stream s)
        {
            using (var z = new GZipStream(s, CompressionMode.Decompress, true))
            using (var r = new BinaryReader(z, Encoding.UTF8))
            {
                if (r.ReadString() != Magic) throw new InvalidDataException("Not a Sidralune world map");
                var map = new WorldMap { Source = r.ReadString() };
                foreach (var layer in new[] { map.Land, map.Lakes, map.Borders })
                {
                    int n = r.ReadInt32();
                    for (int i = 0; i < n; i++)
                    {
                        var a = new float[r.ReadInt32()];
                        for (int k = 0; k < a.Length; k++) a[k] = r.ReadSingle();
                        layer.Add(a);
                    }
                }
                int np = r.ReadInt32();
                for (int i = 0; i < np; i++) map.Places.Add(new MapPlace { Name = r.ReadString(), Lat = r.ReadSingle(), Lon = r.ReadSingle(), Rank = r.ReadInt32(), Population = r.ReadInt64() });
                return map;
            }
        }

        // ------------------------------------------------------------------ queries

        /// <summary>Whether a place is on land (not in the sea or a lake), by the outlines (even-odd rule).</summary>
        public bool IsLand(double lat, double lon) => Inside(Land, lat, lon) && !Inside(Lakes, lat, lon);

        private static bool Inside(List<float[]> rings, double lat, double lon)
        {
            bool inside = false;
            foreach (var r in rings)
                for (int i = 0, j = r.Length - 2; i < r.Length; j = i, i += 2)
                {
                    double xi = r[i], yi = r[i + 1], xj = r[j], yj = r[j + 1];
                    if ((yi > lat) != (yj > lat) && lon < (xj - xi) * (lat - yi) / (yj - yi) + xi) inside = !inside;
                }
            return inside;
        }
    }
}
