using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Sidralune.Core.Mathematics;

namespace Sidralune.Core.Catalogs
{
    /// <summary>
    /// The Principal Galaxies Catalogue (HyperLEDA PGC 2003, VizieR VII/237): about a million galaxies with positions,
    /// sizes, axis ratios, position angles, types and other names, but no magnitudes. They are kept in compact arrays
    /// (about 50 MB for the whole catalogue) and become DeepSkyObjects only when a chart, a search or a label needs one.
    /// </summary>
    public class PgcCatalog
    {
        private const int Zones = 180; // 1° declination zones for searches around a position

        private int[] _num;            // PGC number
        private float[] _ra, _dec;     // J2000, radians
        private float[] _size;         // major axis (D25), arcmin; NaN when unknown
        private float[] _ratio;        // major / minor axis; NaN when unknown
        private short[] _pa;           // position angle, degrees; -1 when unknown
        private byte[] _type;          // 0 galaxy, 1 multiple system, 2 galaxy in a multiple system
        private byte[] _morph;         // index into _morphs
        private string[] _morphs;      // morphological types (RC2 code), "" when unknown
        private string[][] _names;     // other names, null when none
        private int[] _byNumber;       // indices in PGC number order
        private int[][] _zones;        // per declination zone, indices in right ascension order
        private string[] _keys;        // every other name, sorted (ignoring case), for lookups and prefix searches
        private int[] _keyIndex;       // the galaxy of each key

        public int Count => _num.Length;
        public int Number(int i) => _num[i];
        public double SizeArcmin(int i) => _size[i];

        /// <summary>Reads pgc.dat (or pgc.dat.gz as downloaded from CDS).</summary>
        public static PgcCatalog Load(string path)
        {
            using (var f = File.OpenRead(path))
            {
                Stream s = path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ? new GZipStream(f, CompressionMode.Decompress) : (Stream)f;
                using (var r = new StreamReader(new BufferedStream(s, 1 << 20), Encoding.ASCII))
                    return Read(r);
            }
        }

        /// <summary>Reads catalogue lines in the fixed-width layout of VII/237 pgc.dat.</summary>
        public static PgcCatalog Read(TextReader r)
        {
            var num = new List<int>(1000000); var ra = new List<float>(1000000); var dec = new List<float>(1000000);
            var size = new List<float>(1000000); var ratio = new List<float>(1000000); var pa = new List<short>(1000000);
            var type = new List<byte>(1000000); var morph = new List<byte>(1000000); var names = new List<string[]>(1000000);
            var morphs = new List<string> { "" };
            var morphIndex = new Dictionary<string, byte> { { "", 0 } };
            string line;
            while ((line = r.ReadLine()) != null)
            {
                if (line.Length < 27 || !line.StartsWith("PGC")) continue;
                int n = (int)Num(line, 3, 7);
                double raH = Num(line, 12, 2) + Num(line, 14, 2) / 60 + Num(line, 16, 4) / 3600;
                double decD = Num(line, 21, 2) + Num(line, 23, 2) / 60 + Num(line, 25, 2) / 3600;
                if (line[20] == '-') decD = -decD;
                if (double.IsNaN(raH) || double.IsNaN(decD)) continue;
                num.Add(n);
                ra.Add((float)(raH * AstroMath.Hour2Rad));
                dec.Add((float)(decD * AstroMath.Deg2Rad));
                string ot = Field(line, 28, 2);
                type.Add((byte)(ot == "M" ? 1 : ot == "GM" ? 2 : 0));
                string mt = Field(line, 31, 5);
                byte mi;
                if (!morphIndex.TryGetValue(mt, out mi))
                {
                    mi = morphs.Count < 255 ? (byte)morphs.Count : (byte)0;
                    if (mi != 0) { morphIndex[mt] = mi; morphs.Add(mt); }
                }
                morph.Add(mi);
                double logD = Num(line, 36, 5), logR = Num(line, 50, 4), p = Num(line, 63, 4);
                size.Add(double.IsNaN(logD) || logD > 9.9 ? float.NaN : (float)(0.1 * Math.Pow(10, logD)));
                ratio.Add(double.IsNaN(logR) || logR > 9.9 ? float.NaN : (float)Math.Pow(10, logR));
                pa.Add(double.IsNaN(p) || p > 360 ? (short)-1 : (short)p);
                int count = (int)Num(line, 75, 2);
                string[] other = null;
                if (count > 0)
                {
                    var list = new List<string>(count);
                    for (int k = 0; k < count; k++)
                    {
                        string nm = Clean(Field(line, 78 + 22 * k, 22));
                        if (nm.Length > 0) list.Add(nm);
                    }
                    if (list.Count > 0) other = list.ToArray();
                }
                names.Add(other);
            }
            var c = new PgcCatalog
            {
                _num = num.ToArray(), _ra = ra.ToArray(), _dec = dec.ToArray(), _size = size.ToArray(), _ratio = ratio.ToArray(),
                _pa = pa.ToArray(), _type = type.ToArray(), _morph = morph.ToArray(), _morphs = morphs.ToArray(), _names = names.ToArray()
            };
            c.Index();
            return c;
        }

        private void Index()
        {
            int n = _num.Length;
            _byNumber = Enumerable.Range(0, n).ToArray();
            Array.Sort((int[])_num.Clone(), _byNumber);
            var zones = new List<int>[Zones];
            for (int z = 0; z < Zones; z++) zones[z] = new List<int>();
            for (int i = 0; i < n; i++) zones[Zone(_dec[i])].Add(i);
            _zones = new int[Zones][];
            for (int z = 0; z < Zones; z++)
            {
                var idx = zones[z].ToArray();
                Array.Sort(idx.Select(i => _ra[i]).ToArray(), idx);
                _zones[z] = idx;
            }
            var keys = new List<string>(); var owners = new List<int>();
            for (int i = 0; i < n; i++)
                if (_names[i] != null)
                    foreach (var nm in _names[i]) { keys.Add(CatalogManager.Normalize(nm)); owners.Add(i); }
            _keys = keys.ToArray();
            _keyIndex = owners.ToArray();
            Array.Sort(_keys, _keyIndex, StringComparer.OrdinalIgnoreCase);
        }

        private static int Zone(double dec) => Math.Max(0, Math.Min(Zones - 1, (int)Math.Floor(dec * AstroMath.Rad2Deg + 90)));

        /// <summary>The galaxy with a name: "PGC 2557", "PGC2557", "LEDA 2557" or another name such as "UGC 454"; -1 when none.</summary>
        public int IndexOf(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return -1;
            string t = name.Trim();
            int n;
            if (TryNumber(t, out n)) return IndexOfNumber(n);
            int k = LowerBound(CatalogManager.Normalize(Clean(t)));
            return k < _keys.Length && string.Equals(_keys[k], CatalogManager.Normalize(Clean(t)), StringComparison.OrdinalIgnoreCase) ? _keyIndex[k] : -1;
        }

        public int IndexOfNumber(int pgc)
        {
            int lo = 0, hi = _byNumber.Length - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2, v = _num[_byNumber[mid]];
                if (v == pgc) return _byNumber[mid];
                if (v < pgc) lo = mid + 1; else hi = mid - 1;
            }
            return -1;
        }

        /// <summary>"PGC 2557", "PGC002557" or "LEDA 2557" as a number.</summary>
        public static bool TryNumber(string name, out int pgc)
        {
            pgc = 0;
            string t = name.Trim();
            string rest = t.StartsWith("PGC", StringComparison.OrdinalIgnoreCase) ? t.Substring(3) : t.StartsWith("LEDA", StringComparison.OrdinalIgnoreCase) ? t.Substring(4) : null;
            if (rest == null) return false;
            rest = rest.Trim();
            return rest.Length > 0 && rest.All(char.IsDigit) && int.TryParse(rest, out pgc);
        }

        /// <summary>Galaxies whose other names start with text, best (shortest) names first; a PGC number matches exactly.</summary>
        public List<int> Search(string text, int max)
        {
            var res = new List<int>();
            if (string.IsNullOrWhiteSpace(text) || max <= 0) return res;
            int n;
            if (TryNumber(text, out n)) { int i = IndexOfNumber(n); if (i >= 0) res.Add(i); return res; }
            string p = CatalogManager.Normalize(Clean(text));
            var found = new List<KeyValuePair<string, int>>();
            for (int k = LowerBound(p); k < _keys.Length && _keys[k].StartsWith(p, StringComparison.OrdinalIgnoreCase) && found.Count < max * 20; k++)
                found.Add(new KeyValuePair<string, int>(_keys[k], _keyIndex[k]));
            foreach (var kv in found.OrderBy(kv => kv.Key.Length)) if (!res.Contains(kv.Value)) { res.Add(kv.Value); if (res.Count >= max) break; }
            return res;
        }

        private int LowerBound(string key)
        {
            int lo = 0, hi = _keys.Length;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (string.Compare(_keys[mid], key, StringComparison.OrdinalIgnoreCase) < 0) lo = mid + 1; else hi = mid;
            }
            return lo;
        }

        /// <summary>
        /// Galaxies within radius (radians) of a position and at least minSizeArcmin across (galaxies of unknown size
        /// count as 0), largest first, at most max.
        /// </summary>
        public List<int> Cone(double ra, double dec, double radius, double minSizeArcmin, int max)
        {
            var found = new List<int>();
            double cosR = Math.Cos(radius), sinD = Math.Sin(dec), cosD = Math.Cos(dec);
            int z0 = Zone(dec - radius), z1 = Zone(dec + radius);
            for (int z = z0; z <= z1; z++)
            {
                var zone = _zones[z];
                if (zone.Length == 0) continue;
                double edge = Math.Max(Math.Abs(z - 90), Math.Abs(z + 1 - 90)) * AstroMath.Deg2Rad; // the zone's edge nearest a pole
                double half = edge >= AstroMath.HalfPi - 1e-6 ? Math.PI : radius / Math.Cos(edge) + 1e-6;
                if (half >= Math.PI) Scan(zone, 0, zone.Length, ra, cosR, sinD, cosD, minSizeArcmin, found);
                else
                {
                    double lo = AstroMath.NormalizeRad(ra - half), hi = AstroMath.NormalizeRad(ra + half);
                    if (lo <= hi) Scan(zone, RaBound(zone, lo), RaBound(zone, hi), ra, cosR, sinD, cosD, minSizeArcmin, found);
                    else
                    {
                        Scan(zone, RaBound(zone, lo), zone.Length, ra, cosR, sinD, cosD, minSizeArcmin, found);
                        Scan(zone, 0, RaBound(zone, hi), ra, cosR, sinD, cosD, minSizeArcmin, found);
                    }
                }
            }
            return found.OrderByDescending(i => float.IsNaN(_size[i]) ? 0 : _size[i]).Take(max).ToList();
        }

        private int RaBound(int[] zone, double ra)
        {
            int lo = 0, hi = zone.Length;
            while (lo < hi) { int mid = (lo + hi) / 2; if (_ra[zone[mid]] < ra) lo = mid + 1; else hi = mid; }
            return lo;
        }

        private void Scan(int[] zone, int from, int to, double ra, double cosR, double sinD, double cosD, double minSize, List<int> found)
        {
            for (int k = from; k < to; k++)
            {
                int i = zone[k];
                double s = float.IsNaN(_size[i]) ? 0 : _size[i];
                if (s < minSize) continue;
                double cosSep = sinD * Math.Sin(_dec[i]) + cosD * Math.Cos(_dec[i]) * Math.Cos(_ra[i] - ra);
                if (cosSep >= cosR) found.Add(i);
            }
        }

        /// <summary>The other names of galaxy i (as written in the catalogue, cleaned), or none.</summary>
        public string[] Names(int i) => _names[i] ?? NoNames;
        private static readonly string[] NoNames = new string[0];

        /// <summary>Galaxy i as a deep-sky object, named "PGC n", with its other names; its magnitude is unknown.</summary>
        public DeepSkyObject Object(int i)
        {
            var d = new DeepSkyObject
            {
                Id = "PGC " + _num[i],
                Type = _type[i] == 1 ? ObjectType.GalaxyGroup : ObjectType.Galaxy,
                Ra = _ra[i], Dec = _dec[i],
                MajorAxis = _size[i],
                MinorAxis = float.IsNaN(_size[i]) || float.IsNaN(_ratio[i]) ? double.NaN : _size[i] / _ratio[i],
                PositionAngleDeg = _pa[i] < 0 ? double.NaN : _pa[i],
                Morphology = _morphs[_morph[i]].Length > 0 ? _morphs[_morph[i]] : null,
                Mag = double.NaN
            };
            if (_names[i] != null) foreach (var nm in _names[i]) d.AltNames.Add(CatalogManager.Normalize(nm));
            return d;
        }

        // a name without the marks of the catalogue: (…) for a multiple system, ? for a doubtful and ! for a moved name
        private static string Clean(string s) => s.Replace("(", "").Replace(")", "").Replace("?", "").Replace("!", "").Trim();

        private static string Field(string line, int start, int length) => start >= line.Length ? "" : line.Substring(start, Math.Min(length, line.Length - start)).Trim();

        /// <summary>A number in a fixed-width field, without allocating; NaN when empty or not a number.</summary>
        private static double Num(string line, int start, int length)
        {
            int end = Math.Min(line.Length, start + length);
            double v = 0, scale = 0;
            bool any = false, neg = false;
            for (int i = start; i < end; i++)
            {
                char ch = line[i];
                if (ch == ' ') continue;
                if (ch == '-') { neg = true; continue; }
                if (ch == '+') continue;
                if (ch == '.') { scale = 1; continue; }
                if (ch < '0' || ch > '9') return double.NaN;
                any = true;
                if (scale > 0) { scale *= 10; v += (ch - '0') / scale; } else v = v * 10 + (ch - '0');
            }
            return any ? (neg ? -v : v) : double.NaN;
        }
    }
}
