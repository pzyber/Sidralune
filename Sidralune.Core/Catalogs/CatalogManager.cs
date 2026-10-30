using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Sidralune.Core.Mathematics;
using Sidralune.Core.Util;

namespace Sidralune.Core.Catalogs
{
    /// <summary>Equal-declination-band grid index for fast cone/rectangle queries on the celestial sphere.</summary>
    public class SkyIndex<T> where T : SkyObject
    {
        private const int Bands = 90; // 2° bands
        private readonly List<T>[][] _cells;
        private readonly int[] _raCells;
        public int Count { get; private set; }

        public SkyIndex()
        {
            _cells = new List<T>[Bands][];
            _raCells = new int[Bands];
            for (int b = 0; b < Bands; b++)
            {
                double decMid = -90 + (b + 0.5) * 180.0 / Bands;
                int n = Math.Max(1, (int)Math.Round(180 * Math.Cos(decMid * AstroMath.Deg2Rad)));
                _raCells[b] = n;
                _cells[b] = new List<T>[n];
                for (int i = 0; i < n; i++) _cells[b][i] = new List<T>();
            }
        }

        private static int Band(double dec) => AstroMath.Clamp((int)((dec * AstroMath.Rad2Deg + 90) / (180.0 / Bands)), 0, Bands - 1);

        public void Add(T o)
        {
            int b = Band(o.Dec);
            int n = _raCells[b];
            int i = AstroMath.Clamp((int)(AstroMath.NormalizeRad(o.Ra) / AstroMath.TwoPi * n), 0, n - 1);
            _cells[b][i].Add(o);
            Count++;
        }

        /// <summary>Sort each cell by magnitude so queries can stop early at the limiting magnitude.</summary>
        public void Finish()
        {
            foreach (var band in _cells) foreach (var c in band) c.Sort((a, b) => Nz(a.Mag).CompareTo(Nz(b.Mag)));
        }

        private static double Nz(double m) => double.IsNaN(m) ? 99 : m;

        /// <summary>All objects within radius (radians) of (ra, dec) brighter than magLimit.</summary>
        public void Cone(double ra, double dec, double radius, double magLimit, List<T> result)
        {
            double dmin = dec - radius, dmax = dec + radius;
            int b0 = Band(Math.Max(dmin, -AstroMath.HalfPi)), b1 = Band(Math.Min(dmax, AstroMath.HalfPi));
            double cosR = Math.Cos(radius);
            var c = Vec3.FromSpherical(ra, dec);
            for (int b = b0; b <= b1; b++)
            {
                int n = _raCells[b];
                double bandDecLo = (-90 + b * 180.0 / Bands) * AstroMath.Deg2Rad;
                double bandDecHi = bandDecLo + 180.0 / Bands * AstroMath.Deg2Rad;
                double maxAbs = Math.Max(Math.Abs(bandDecLo), Math.Abs(bandDecHi));
                bool allRa = dmax >= AstroMath.HalfPi || dmin <= -AstroMath.HalfPi || maxAbs + radius >= AstroMath.HalfPi;
                int i0 = 0, i1 = n - 1;
                if (!allRa)
                {
                    double dra = Math.Asin(Math.Min(1, Math.Sin(radius) / Math.Cos(maxAbs)));
                    double w = dra / AstroMath.TwoPi * n;
                    double ci = AstroMath.NormalizeRad(ra) / AstroMath.TwoPi * n;
                    i0 = (int)Math.Floor(ci - w) - 1;
                    i1 = (int)Math.Floor(ci + w) + 1;
                    if (i1 - i0 + 1 >= n) { i0 = 0; i1 = n - 1; }
                }
                for (int ii = i0; ii <= i1; ii++)
                {
                    var cell = _cells[b][((ii % n) + n) % n];
                    for (int k = 0; k < cell.Count; k++)
                    {
                        var o = cell[k];
                        if (Nz(o.Mag) > magLimit) break;
                        double cd = Math.Cos(o.Dec);
                        double dot = c.X * cd * Math.Cos(o.Ra) + c.Y * cd * Math.Sin(o.Ra) + c.Z * Math.Sin(o.Dec);
                        if (dot >= cosR) result.Add(o);
                    }
                }
            }
        }

        public IEnumerable<T> All()
        {
            foreach (var band in _cells) foreach (var c in band) foreach (var o in c) yield return o;
        }
    }

    /// <summary>
    /// Central catalog store: stars, deep-sky objects and constellations from built-in data and downloaded catalogs.
    /// </summary>
    public class CatalogManager
    {
        public static CatalogManager Instance { get; } = new CatalogManager();

        public SkyIndex<Star> Stars { get; private set; } = new SkyIndex<Star>();
        public SkyIndex<DeepSkyObject> DeepSky { get; private set; } = new SkyIndex<DeepSkyObject>();
        public List<Constellation> Constellations { get; } = new List<Constellation>();
        public Dictionary<int, Star> StarsByHip { get; } = new Dictionary<int, Star>();
        private readonly Dictionary<string, SkyObject> _names = new Dictionary<string, SkyObject>(StringComparer.OrdinalIgnoreCase);
        // the deep-sky object (OpenNGC) of a PGC number, so a galaxy in both catalogues is found and shown once
        private readonly Dictionary<int, DeepSkyObject> _dsoByPgc = new Dictionary<int, DeepSkyObject>();
        // PGC galaxies made for Find and Search, kept so a galaxy found twice is the same object
        private readonly Dictionary<int, DeepSkyObject> _pgcObjects = new Dictionary<int, DeepSkyObject>();

        /// <summary>The Principal Galaxies Catalogue when installed (about a million galaxies, kept compactly), else null.</summary>
        public PgcCatalog Pgc { get; private set; }
        public List<DeepSkyObject> DeepSkyList { get; } = new List<DeepSkyObject>();
        public List<Star> NamedStars { get; } = new List<Star>();
        public string StarSource { get; private set; } = "built-in";
        public string DeepSkySource { get; private set; } = "built-in";
        public bool HasBoundaries { get; private set; }
        public double StarLimitingMagnitude { get; private set; } = 4.0;
        public event EventHandler CatalogsChanged;

        public CatalogManager() { }

        public void LoadAll(string folder)
        {
            Pgc = null;
            LoadBuiltIn();
            try
            {
                string hyg = FirstExisting(folder, "hygdata_v41.csv", "hygdata_v40.csv", "hyg_v38.csv", "hygdata_v3.csv", "hyg.csv");
                if (hyg != null) LoadHyg(hyg);
                string ngc = FirstExisting(folder, "NGC.csv", "openngc.csv");
                if (ngc != null) LoadOpenNgc(ngc);
                string addendum = FirstExisting(folder, "addendum.csv");
                if (addendum != null) LoadOpenNgc(addendum, true);
                string pgc = FirstExisting(folder, "pgc.dat.gz", "pgc.dat");
                if (pgc != null) LoadPgc(pgc);
                string lines = FirstExisting(folder, "constellationship.fab");
                if (lines != null) LoadStellariumLines(lines);
                string bounds = FirstExisting(folder, "constellation_boundaries.dat");
                if (bounds != null) LoadBoundaries(bounds);
            }
            catch (Exception ex) { Log.Error("Catalogs", ex); }
            ResolveLines();
            if (CatalogsChanged != null) CatalogsChanged(this, EventArgs.Empty);
        }

        private static string FirstExisting(string folder, params string[] names)
        {
            foreach (var n in names)
            {
                var p = Path.Combine(folder, n);
                if (File.Exists(p)) return p;
            }
            return null;
        }

        // ------------------------------------------------------------------ built-in

        public void LoadBuiltIn()
        {
            Stars = new SkyIndex<Star>();
            DeepSky = new SkyIndex<DeepSkyObject>();
            StarsByHip.Clear(); _names.Clear(); DeepSkyList.Clear(); NamedStars.Clear(); Constellations.Clear();
            _dsoByPgc.Clear();
            lock (_pgcObjects) _pgcObjects.Clear();
            foreach (var line in Lines(BuiltInData.BrightStars))
            {
                var f = line.Split('|');
                var s = new Star
                {
                    Name = f[0],
                    Ra = AstroMath.ParseSexagesimal(f[1]) * AstroMath.Hour2Rad,
                    Dec = AstroMath.ParseSexagesimal(f[2]) * AstroMath.Deg2Rad,
                    Mag = AstroMath.ParseInvariant(f[3]),
                    BV = (float)AstroMath.ParseInvariant(f[4]),
                    Bayer = f[5],
                    Constellation = f[6]
                };
                AddStar(s);
            }
            foreach (var line in Lines(BuiltInData.DeepSky))
            {
                var f = line.Split('|');
                var d = new DeepSkyObject
                {
                    Id = f[0],
                    Name = f[1].Length > 0 ? f[1] : null,
                    Ra = AstroMath.ParseSexagesimal(f[2]) * AstroMath.Hour2Rad,
                    Dec = AstroMath.ParseSexagesimal(f[3]) * AstroMath.Deg2Rad,
                    Type = ParseTypeCode(f[4]),
                    Mag = AstroMath.ParseInvariant(f[5]),
                    Constellation = f[8]
                };
                ParseSize(f[6], d);
                d.PositionAngleDeg = AstroMath.ParseInvariant(f[7]);
                if (d.Id.StartsWith("M") && char.IsDigit(d.Id[1])) { d.Messier = d.Id; }
                AddDso(d);
            }
            foreach (var line in Lines(BuiltInData.Constellations))
            {
                var f = line.Split('|');
                Constellations.Add(new Constellation
                {
                    Abbr = f[0], Name = f[1], Genitive = f[2],
                    LabelRa = AstroMath.ParseInvariant(f[3]) * AstroMath.Hour2Rad,
                    LabelDec = AstroMath.ParseInvariant(f[4]) * AstroMath.Deg2Rad
                });
            }
            foreach (var line in Lines(BuiltInData.BuiltInLines))
            {
                var f = line.Split('|');
                var con = Constellations.FirstOrDefault(c => c.Abbr == f[0]);
                if (con == null) continue;
                for (int i = 1; i < f.Length; i++)
                {
                    var pair = f[i].Split('-');
                    var a = Find(pair[0]) as Star; var b = Find(pair[1]) as Star;
                    if (a != null && b != null) con.Lines.Add(new[] { a.Ra, a.Dec, b.Ra, b.Dec });
                }
            }
            Stars.Finish(); DeepSky.Finish();
            StarSource = "built-in (" + Stars.Count + " stars)";
            DeepSkySource = "built-in (" + DeepSkyList.Count + " objects)";
        }

        private static IEnumerable<string> Lines(string block)
        {
            foreach (var l in block.Split('\n'))
            {
                var t = l.Trim();
                if (t.Length > 0 && !t.StartsWith("#")) yield return t;
            }
        }

        private void AddStar(Star s)
        {
            Stars.Add(s);
            if (s.Hip > 0) StarsByHip[s.Hip] = s;
            if (!string.IsNullOrEmpty(s.Name)) { _names[s.Name] = s; NamedStars.Add(s); }
            if (!string.IsNullOrEmpty(s.Bayer) && !_names.ContainsKey(s.Bayer)) _names[s.Bayer] = s;
            if (s.Hip > 0) _names["HIP " + s.Hip] = s;
            if (s.Hd > 0) _names["HD " + s.Hd] = s;
            if (s.Hr > 0) _names["HR " + s.Hr] = s;
        }

        private void AddDso(DeepSkyObject d)
        {
            DeepSky.Add(d);
            DeepSkyList.Add(d);
            foreach (var key in new[] { d.Id, d.Messier, d.Ngc, d.Ic, d.Caldwell, d.Name })
                if (!string.IsNullOrEmpty(key)) _names[Normalize(key)] = d;
            foreach (var n in d.AltNames) if (!string.IsNullOrEmpty(n)) _names[Normalize(n)] = d;
            int pgc;
            foreach (var n in d.AltNames) if (PgcCatalog.TryNumber(n, out pgc)) _dsoByPgc[pgc] = d;
        }

        // "PGC 002557" -> "PGC 2557", "UGC 00454" -> "UGC 454"; names with more than digits after the acronym stay as they are
        private static string WithoutLeadingZeros(string name)
        {
            int sp = name.LastIndexOf(' ');
            if (sp < 0 || sp == name.Length - 1) return name;
            string num = name.Substring(sp + 1);
            if (num[0] != '0' || !num.All(char.IsDigit)) return name;
            num = num.TrimStart('0');
            return name.Substring(0, sp + 1) + (num.Length == 0 ? "0" : num);
        }

        internal static string Normalize(string s)
        {
            s = s.Trim();
            // "M31" -> "M 31", "NGC224" -> "NGC 224"
            int i = 0;
            while (i < s.Length && char.IsLetter(s[i])) i++;
            if (i > 0 && i < s.Length && char.IsDigit(s[i])) s = s.Substring(0, i) + " " + s.Substring(i);
            return s.Replace("  ", " ");
        }

        public static ObjectType ParseTypeCode(string code)
        {
            switch (code.Trim())
            {
                case "Gx": case "G": return ObjectType.Galaxy;
                case "GPair": case "GTrpl": return ObjectType.GalaxyPair;
                case "GxG": case "GGroup": return ObjectType.GalaxyGroup;
                case "GC": case "GCl": return ObjectType.GlobularCluster;
                case "OC": case "OCl": return ObjectType.OpenCluster;
                case "CN": case "Cl+N": return ObjectType.ClusterNebula;
                case "EN": case "EmN": case "Neb": return ObjectType.EmissionNebula;
                case "RN": case "RfN": return ObjectType.ReflectionNebula;
                case "PN": return ObjectType.PlanetaryNebula;
                case "SNR": return ObjectType.SupernovaRemnant;
                case "DN": case "DrkN": return ObjectType.DarkNebula;
                case "HII": return ObjectType.HIIRegion;
                case "Ast": case "*Ass": return ObjectType.Asterism;
                case "SC": return ObjectType.StarCloud;
                case "DS": case "**": return ObjectType.DoubleStar;
                case "*": return ObjectType.Star;
                case "Nova": return ObjectType.Nova;
                case "Other": case "NonEx": case "Dup": return ObjectType.Other;
            }
            return ObjectType.Unknown;
        }

        private static void ParseSize(string s, DeepSkyObject d)
        {
            if (string.IsNullOrWhiteSpace(s)) return;
            var p = s.Split('x');
            d.MajorAxis = AstroMath.ParseInvariant(p[0]);
            d.MinorAxis = p.Length > 1 ? AstroMath.ParseInvariant(p[1]) : d.MajorAxis;
        }

        // ------------------------------------------------------------------ external catalogs

        /// <summary>HYG database v3/v4 (CSV). Keeps built-in names for matching HIP stars.</summary>
        public void LoadHyg(string path)
        {
            var builtInNames = NamedStars.ToDictionary(s => s.Name, s => s, StringComparer.OrdinalIgnoreCase);
            var stars = new SkyIndex<Star>();
            StarsByHip.Clear();
            var keepDso = _names.Where(kv => kv.Value is DeepSkyObject).ToList();
            _names.Clear();
            NamedStars.Clear();
            foreach (var kv in keepDso) _names[kv.Key] = kv.Value;
            Stars = stars;
            double maxMag = 0;
            using (var r = CsvReader.Open(path))
            {
                int cHip = r.Column("hip"), cHd = r.Column("hd"), cHr = r.Column("hr"), cProper = r.Column("proper"),
                    cRa = r.Column("ra"), cDec = r.Column("dec"), cMag = r.Column("mag"), cPmRa = r.Column("pmra"), cPmDec = r.Column("pmdec"),
                    cSpect = r.Column("spect"), cCi = r.Column("ci"), cBf = r.Column("bf"), cCon = r.Column("con"), cDist = r.Column("dist"),
                    cRv = r.Column("rv"), cVar = r.Column("var"), cVmin = r.Column("var_min"), cVmax = r.Column("var_max"), cId = r.Column("id");
                string[] row;
                while ((row = r.ReadRow()) != null)
                {
                    if (row.Length < 5) continue;
                    if (CsvReader.I(row, cId, -1) == 0) continue; // the Sun
                    var s = new Star
                    {
                        Hip = CsvReader.I(row, cHip), Hd = CsvReader.I(row, cHd), Hr = CsvReader.I(row, cHr),
                        Name = NullIfEmpty(CsvReader.S(row, cProper)),
                        Ra = CsvReader.D(row, cRa) * AstroMath.Hour2Rad,
                        Dec = CsvReader.D(row, cDec) * AstroMath.Deg2Rad,
                        Mag = CsvReader.D(row, cMag),
                        PmRa = (float)CsvReader.D(row, cPmRa, 0), PmDec = (float)CsvReader.D(row, cPmDec, 0),
                        SpectralType = NullIfEmpty(CsvReader.S(row, cSpect)),
                        BV = (float)CsvReader.D(row, cCi),
                        Bayer = NullIfEmpty(CsvReader.S(row, cBf)),
                        Constellation = NullIfEmpty(CsvReader.S(row, cCon)),
                        RadialVelocity = (float)CsvReader.D(row, cRv),
                        VariableType = NullIfEmpty(CsvReader.S(row, cVar)),
                    };
                    double dist = CsvReader.D(row, cDist);
                    if (dist > 0 && dist < 100000) s.Parallax = (float)(1000.0 / dist);
                    double vmin = CsvReader.D(row, cVmin);
                    if (!double.IsNaN(vmin)) s.MagMin = (float)vmin;
                    if (double.IsNaN(s.Ra) || double.IsNaN(s.Dec)) continue;
                    if (s.Name != null && builtInNames.ContainsKey(s.Name)) builtInNames.Remove(s.Name);
                    if (!double.IsNaN(s.Mag) && s.Mag > maxMag) maxMag = s.Mag;
                    AddStar(s);
                }
            }
            // keep any built-in named stars not in HYG
            foreach (var s in builtInNames.Values) AddStar(s);
            stars.Finish();
            StarSource = "HYG (" + stars.Count + " stars)";
            StarLimitingMagnitude = maxMag;
            Log.Info("Catalogs", "Loaded " + StarSource);
        }

        /// <summary>OpenNGC catalog (semicolon separated) by Mattia Verga.</summary>
        public void LoadOpenNgc(string path, bool append = false)
        {
            if (!append)
            {
                // keep built-in objects that are not NGC/IC (Sh2, LMC...), drop the rest to avoid duplicates
                var keep = DeepSkyList.Where(d => !(d.Id.StartsWith("NGC") || d.Id.StartsWith("IC") || d.Id.StartsWith("M"))).ToList();
                var builtinMessier = DeepSkyList.Where(d => d.Messier != null).ToDictionary(d => d.Messier);
                DeepSky = new SkyIndex<DeepSkyObject>();
                DeepSkyList.Clear();
                foreach (var k in _names.Where(kv => kv.Value is DeepSkyObject).Select(kv => kv.Key).ToList()) _names.Remove(k);
                _dsoByPgc.Clear();
                foreach (var d in keep) AddDso(d);
                LoadOpenNgcRows(path, builtinMessier);
                // re-add Messier objects that OpenNGC lacks (e.g. M40, M45 handled in addendum)
                foreach (var m in builtinMessier.Values) if (Find(m.Messier) == null) AddDso(m);
            }
            else LoadOpenNgcRows(path, null);
            DeepSky.Finish();
            DeepSkySource = "OpenNGC (" + DeepSkyList.Count + " objects)";
            Log.Info("Catalogs", "Loaded " + DeepSkySource);
        }

        private void LoadOpenNgcRows(string path, Dictionary<string, DeepSkyObject> builtinMessier)
        {
            using (var r = CsvReader.Open(path, ';'))
            {
                int cName = r.Column("Name"), cType = r.Column("Type"), cRa = r.Column("RA"), cDec = r.Column("Dec"), cCon = r.Column("Const"),
                    cMaj = r.Column("MajAx"), cMin = r.Column("MinAx"), cPa = r.Column("PosAng"), cB = r.Column("B-Mag"), cV = r.Column("V-Mag"),
                    cSb = r.Column("SurfBr"), cHub = r.Column("Hubble"), cM = r.Column("M"), cCommon = r.Column("Common names"), cIdent = r.Column("Identifiers"),
                    cZ = r.Column("Redshift");
                string[] row;
                while ((row = r.ReadRow()) != null)
                {
                    if (row.Length < 5) continue;
                    string type = CsvReader.S(row, cType);
                    if (type == "Dup" || type == "NonEx") continue;
                    string raS = CsvReader.S(row, cRa), decS = CsvReader.S(row, cDec);
                    if (raS.Length == 0) continue;
                    string id = CsvReader.S(row, cName);
                    var d = new DeepSkyObject
                    {
                        Id = Normalize(id.StartsWith("NGC") || id.StartsWith("IC") ? id.TrimStart() : id),
                        Type = ParseTypeCode(type),
                        Ra = AstroMath.ParseSexagesimal(raS) * AstroMath.Hour2Rad,
                        Dec = AstroMath.ParseSexagesimal(decS) * AstroMath.Deg2Rad,
                        Constellation = CsvReader.S(row, cCon),
                        MajorAxis = CsvReader.D(row, cMaj), MinorAxis = CsvReader.D(row, cMin),
                        PositionAngleDeg = CsvReader.D(row, cPa),
                        SurfaceBrightness = CsvReader.D(row, cSb),
                        Morphology = NullIfEmpty(CsvReader.S(row, cHub)),
                        Redshift = CsvReader.D(row, cZ)
                    };
                    // strip leading zeros: "NGC 0224" -> "NGC 224"
                    d.Id = StripZeros(d.Id);
                    if (d.Id.StartsWith("NGC")) d.Ngc = d.Id; else if (d.Id.StartsWith("IC")) d.Ic = d.Id;
                    double v = CsvReader.D(row, cV), b = CsvReader.D(row, cB);
                    d.Mag = !double.IsNaN(v) ? v : b;
                    string m = CsvReader.S(row, cM);
                    if (m.Length > 0) d.Messier = "M" + int.Parse(m, CultureInfo.InvariantCulture);
                    string common = CsvReader.S(row, cCommon);
                    if (common.Length > 0)
                    {
                        var names = common.Split(',');
                        d.Name = names[0].Trim();
                        for (int i = 1; i < names.Length; i++) d.AltNames.Add(names[i].Trim());
                    }
                    string ident = CsvReader.S(row, cIdent);
                    if (ident.Length > 0)
                        foreach (var x in ident.Split(','))
                        {
                            // OpenNGC pads numbers with zeros ("PGC 002557"); both spellings find the object
                            string id2 = x.Trim(), plain = WithoutLeadingZeros(id2);
                            d.AltNames.Add(id2);
                            if (plain != id2) d.AltNames.Add(plain);
                        }
                    if (d.Messier != null && builtinMessier != null)
                    {
                        DeepSkyObject bm;
                        if (builtinMessier.TryGetValue(d.Messier, out bm))
                        {
                            if (d.Name == null) d.Name = bm.Name;
                            if (double.IsNaN(d.Mag)) d.Mag = bm.Mag;
                            builtinMessier.Remove(d.Messier);
                        }
                    }
                    if (double.IsNaN(d.Ra) || double.IsNaN(d.Dec)) continue;
                    AddDso(d);
                }
            }
        }

        private static string StripZeros(string id)
        {
            int sp = id.IndexOf(' ');
            if (sp < 0) return id;
            string num = id.Substring(sp + 1).TrimStart('0');
            return id.Substring(0, sp) + " " + (num.Length == 0 ? "0" : num);
        }

        /// <summary>Stellarium constellationship.fab: "Ori 16 26727 26207 ..." (pairs of HIP numbers).</summary>
        public void LoadStellariumLines(string path)
        {
            foreach (var con in Constellations) con.LineHip.Clear();
            foreach (var line in File.ReadAllLines(path))
            {
                var f = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (f.Length < 3) continue;
                var con = Constellations.FirstOrDefault(c => string.Equals(c.Abbr, f[0], StringComparison.OrdinalIgnoreCase));
                if (con == null) continue;
                for (int i = 2; i + 1 < f.Length; i += 2)
                {
                    int a, b;
                    if (int.TryParse(f[i], out a) && int.TryParse(f[i + 1], out b)) con.LineHip.Add(new[] { a, b });
                }
            }
        }

        private void ResolveLines()
        {
            foreach (var con in Constellations)
            {
                if (con.LineHip.Count == 0) continue;
                var segs = new List<double[]>();
                foreach (var p in con.LineHip)
                {
                    Star a, b;
                    if (StarsByHip.TryGetValue(p[0], out a) && StarsByHip.TryGetValue(p[1], out b))
                        segs.Add(new[] { a.Ra, a.Dec, b.Ra, b.Dec });
                }
                if (segs.Count > 0) { con.Lines.Clear(); con.Lines.AddRange(segs); }
            }
        }

        /// <summary>Boundary polylines (J2000, radians) for drawing: each is a list of [ra, dec].</summary>
        public List<List<double[]>> BoundarySegments { get; } = new List<List<double[]>>();

        /// <summary>
        /// IAU constellation boundaries in Stellarium's constellation_boundaries.dat format:
        /// "N ra1 dec1 ... raN decN 2 CON1 CON2" (RA hours, Dec degrees, J2000), possibly wrapped over several lines.
        /// Segments are chained into a closed polygon per constellation.
        /// </summary>
        public void LoadBoundaries(string path)
        {
            BoundarySegments.Clear();
            foreach (var c in Constellations) c.Boundary.Clear();
            var tokens = File.ReadAllText(path).Split(new[] { (char)32, (char)9, (char)13, (char)10 }, StringSplitOptions.RemoveEmptyEntries);
            var perCon = new Dictionary<string, List<List<double[]>>>(StringComparer.OrdinalIgnoreCase);
            int i = 0;
            while (i < tokens.Length)
            {
                int n;
                if (!int.TryParse(tokens[i], out n) || i + 1 + 2 * n + 1 >= tokens.Length) break;
                i++;
                var seg = new List<double[]>();
                for (int k = 0; k < n; k++, i += 2)
                    seg.Add(new[] { AstroMath.ParseInvariant(tokens[i]) * AstroMath.Hour2Rad, AstroMath.ParseInvariant(tokens[i + 1]) * AstroMath.Deg2Rad });
                int nc;
                if (!int.TryParse(tokens[i], out nc)) break;
                i++;
                for (int k = 0; k < nc && i < tokens.Length; k++, i++)
                {
                    string con = tokens[i].Trim();
                    List<List<double[]>> l;
                    if (!perCon.TryGetValue(con, out l)) perCon[con] = l = new List<List<double[]>>();
                    l.Add(seg);
                }
                BoundarySegments.Add(seg);
            }
            foreach (var c in Constellations)
            {
                List<List<double[]>> segs;
                string key = c.Abbr.ToUpperInvariant();
                if (key == "SER") { var s1 = perCon.ContainsKey("SER1") ? perCon["SER1"] : null; segs = s1; }
                else perCon.TryGetValue(key, out segs);
                if (segs != null) c.Boundary.AddRange(Chain(segs));
            }
            HasBoundaries = Constellations.Count(c => c.Boundary.Count > 2) > 40;
        }

        private static List<double[]> Chain(List<List<double[]>> segs)
        {
            var remaining = new List<List<double[]>>(segs);
            var poly = new List<double[]>(remaining[0]);
            remaining.RemoveAt(0);
            Func<double[], double[], bool> same = (a, b) => Math.Abs(AstroMath.NormalizeRadPi(a[0] - b[0])) < 1e-5 && Math.Abs(a[1] - b[1]) < 1e-5;
            bool progress = true;
            while (remaining.Count > 0 && progress)
            {
                progress = false;
                var end = poly[poly.Count - 1];
                for (int k = 0; k < remaining.Count; k++)
                {
                    var s = remaining[k];
                    if (same(s[0], end)) { poly.AddRange(s.Skip(1)); remaining.RemoveAt(k); progress = true; break; }
                    if (same(s[s.Count - 1], end)) { var r = new List<double[]>(s); r.Reverse(); poly.AddRange(r.Skip(1)); remaining.RemoveAt(k); progress = true; break; }
                }
                if (same(poly[0], poly[poly.Count - 1])) break;
            }
            return poly;
        }

        /// <summary>Spherical point-in-polygon by winding angle (valid for polygons around a pole).</summary>
        public static bool InsideSpherical(double ra, double dec, List<double[]> poly)
        {
            var p = Vec3.FromSpherical(ra, dec);
            // the tangent-plane winding test cannot tell p from its antipode, so require p on the polygon's side
            var centroid = new Vec3(0, 0, 0);
            foreach (var v in poly) centroid = centroid + Vec3.FromSpherical(v[0], v[1]);
            if (Vec3.Dot(centroid, p) <= 0) return false;
            double sum = 0;
            for (int i = 0; i < poly.Count; i++)
            {
                var a = Vec3.FromSpherical(poly[i][0], poly[i][1]);
                var b = Vec3.FromSpherical(poly[(i + 1) % poly.Count][0], poly[(i + 1) % poly.Count][1]);
                // tangent-plane directions from p to a and b
                var ta = a - p * Vec3.Dot(a, p); var tb = b - p * Vec3.Dot(b, p);
                double ang = Math.Atan2(Vec3.Dot(Vec3.Cross(ta, tb), p), Vec3.Dot(ta, tb));
                sum += ang;
            }
            return Math.Abs(sum) > Math.PI;
        }

        // ------------------------------------------------------------------ queries

        public SkyObject Find(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            SkyObject o;
            if (_names.TryGetValue(name.Trim(), out o)) return o;
            if (_names.TryGetValue(Normalize(name), out o)) return o;
            var pgc = Pgc;
            if (pgc != null)
            {
                int i = pgc.IndexOf(name);
                if (i >= 0) return PgcObject(pgc, i);
            }
            return null;
        }

        /// <summary>
        /// Loads the Principal Galaxies Catalogue (pgc.dat.gz from CDS). A galaxy that is also an OpenNGC object stays
        /// that object: OpenNGC lists the PGC number of most of its galaxies, and the others are matched by their names.
        /// </summary>
        public void LoadPgc(string path)
        {
            var p = PgcCatalog.Load(path);
            for (int i = 0; i < p.Count; i++)
            {
                if (_dsoByPgc.ContainsKey(p.Number(i))) continue;
                foreach (var nm in p.Names(i))
                {
                    SkyObject o;
                    if (_names.TryGetValue(Normalize(nm), out o) && o is DeepSkyObject) { _dsoByPgc[p.Number(i)] = (DeepSkyObject)o; break; }
                }
            }
            lock (_pgcObjects) _pgcObjects.Clear();
            Pgc = p;
            DeepSkySource += " + PGC (" + p.Count + " galaxies)";
            Log.Info("Catalogs", "Loaded PGC: " + p.Count + " galaxies");
        }

        private DeepSkyObject PgcObject(PgcCatalog pgc, int i)
        {
            DeepSkyObject d;
            if (_dsoByPgc.TryGetValue(pgc.Number(i), out d)) return d;
            lock (_pgcObjects)
            {
                if (!_pgcObjects.TryGetValue(pgc.Number(i), out d))
                {
                    d = pgc.Object(i);
                    var con = ConstellationAt(d.Ra, d.Dec);
                    if (con != null) d.Constellation = con.Abbr;
                    _pgcObjects[pgc.Number(i)] = d;
                }
                return d;
            }
        }

        /// <summary>
        /// PGC galaxies (when the catalogue is installed) within radius (radians) of a position and at least
        /// minSizeArcmin across, largest first, at most max. Galaxies that are also OpenNGC objects are left out: those
        /// come from DeepSky, with their magnitudes and common names.
        /// </summary>
        public void PgcCone(double ra, double dec, double radius, double minSizeArcmin, int max, List<DeepSkyObject> result)
        {
            var pgc = Pgc;
            if (pgc == null) return;
            foreach (var i in pgc.Cone(ra, dec, radius, minSizeArcmin, max))
                if (!_dsoByPgc.ContainsKey(pgc.Number(i))) result.Add(pgc.Object(i));
        }

        /// <summary>Prefix/substring search across all names, best matches first.</summary>
        public List<SkyObject> Search(string text, int max = 50)
        {
            var res = new List<KeyValuePair<int, SkyObject>>();
            if (string.IsNullOrWhiteSpace(text)) return new List<SkyObject>();
            string t = text.Trim();
            string tn = Normalize(t);
            var seen = new HashSet<SkyObject>();
            foreach (var kv in _names)
            {
                int score = -1;
                if (string.Equals(kv.Key, t, StringComparison.OrdinalIgnoreCase) || string.Equals(kv.Key, tn, StringComparison.OrdinalIgnoreCase)) score = 0;
                else if (kv.Key.StartsWith(tn, StringComparison.OrdinalIgnoreCase) || kv.Key.StartsWith(t, StringComparison.OrdinalIgnoreCase)) score = 1 + kv.Key.Length;
                else if (t.Length >= 3 && kv.Key.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0) score = 100 + kv.Key.Length;
                if (score >= 0 && seen.Add(kv.Value)) res.Add(new KeyValuePair<int, SkyObject>(score, kv.Value));
            }
            var list = res.OrderBy(k => k.Key).ThenBy(k => double.IsNaN(k.Value.Mag) ? 99 : k.Value.Mag).Take(max).Select(k => k.Value).ToList();
            // then PGC galaxies by their numbers and other names
            var pgc = Pgc;
            if (pgc != null && list.Count < max)
                foreach (var i in pgc.Search(t, max - list.Count))
                {
                    var d = PgcObject(pgc, i);
                    if (!list.Contains(d)) list.Add(d);
                }
            return list;
        }

        /// <summary>Constellation containing a J2000 position, via boundaries if loaded, else nearest label.</summary>
        public Constellation ConstellationAt(double ra, double dec)
        {
            if (HasBoundaries)
            {
                foreach (var c in Constellations)
                    if (c.Boundary.Count > 2 && InsideSpherical(ra, dec, c.Boundary)) return c;
            }
            Constellation best = null;
            double bd = double.MaxValue;
            foreach (var c in Constellations)
            {
                double s = AstroMath.Separation(ra, dec, c.LabelRa, c.LabelDec);
                if (s < bd) { bd = s; best = c; }
            }
            return best;
        }

        private static bool PointInPolygonPlanar(double ra, double dec, List<double[]> poly)
        {
            bool inside = false;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            {
                double xi = poly[i][0], yi = poly[i][1], xj = poly[j][0], yj = poly[j][1];
                // unwrap RA relative to test point
                xi = ra + AstroMath.NormalizeRadPi(xi - ra);
                xj = ra + AstroMath.NormalizeRadPi(xj - ra);
                if (((yi > dec) != (yj > dec)) && (ra < (xj - xi) * (dec - yi) / (yj - yi + 1e-300) + xi)) inside = !inside;
            }
            return inside;
        }

        private static string NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;
    }
}
