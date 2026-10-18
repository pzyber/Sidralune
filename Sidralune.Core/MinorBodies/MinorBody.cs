using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Sidralune.Core.Catalogs;
using Sidralune.Core.Coordinates;
using Sidralune.Core.Ephemeris;
using Sidralune.Core.Mathematics;
using Sidralune.Core.Time;
using Sidralune.Core.Util;

namespace Sidralune.Core.MinorBodies
{
    /// <summary>An asteroid, comet or other small body with osculating heliocentric elements.</summary>
    public class MinorBody
    {
        public string Designation;      // packed or readable designation
        public string Name;             // readable name, e.g. "(1) Ceres" or "C/2023 A3 (Tsuchinshan-ATLAS)"
        public int Number;
        public bool IsComet;
        public OrbitalElements Elements;
        /// <summary>Asteroid absolute magnitude H and slope G; comet total magnitude parameters g, k.</summary>
        public double H = double.NaN, G = 0.15;
        public double CometK = 4.0;
        public double NucleusH = double.NaN;
        public string OrbitType;  // NEO class, MBA, TNO, etc.
        public double Uncertainty = double.NaN; // MPC U parameter
        public string Reference;

        public string DisplayName => !string.IsNullOrEmpty(Name) ? Name : Designation;
        public override string ToString() => DisplayName;

        /// <summary>Heliocentric ICRF equatorial position (AU) at JD TDB using two-body motion.</summary>
        public Vec3 HeliocentricEquatorial(double jd)
        {
            var ecl = Elements.PositionAt(jd);
            return Transforms.EclipticToEquatorial(ecl, Precession.ObliquityJ2000);
        }

        /// <summary>Position wrt the ephemeris origin (for SkyContext light-time loops).</summary>
        public Vec3 PositionForEphemeris(double jd) => HeliocentricEquatorial(jd) + EphemerisManager.Position(Body.Sun, jd);

        /// <summary>Apparent magnitude for heliocentric distance r, observer distance delta (AU) and phase angle (rad).</summary>
        public double Magnitude(double r, double delta, double phase)
        {
            if (double.IsNaN(H)) return double.NaN;
            if (IsComet) return H + 5 * Math.Log10(delta) + 2.5 * CometK * Math.Log10(r);
            return HG(H, G, r, delta, phase);
        }

        /// <summary>IAU H,G magnitude system (Bowell et al. 1989).</summary>
        public static double HG(double H, double G, double r, double delta, double phase)
        {
            double t = Math.Tan(Math.Abs(phase) / 2);
            double phi1 = Math.Exp(-3.33 * Math.Pow(t, 0.63));
            double phi2 = Math.Exp(-1.87 * Math.Pow(t, 1.22));
            return H + 5 * Math.Log10(r * delta) - 2.5 * Math.Log10((1 - G) * phi1 + G * phi2);
        }
    }

    /// <summary>Parsers for MPC orbit formats and helpers for packed designations/dates.</summary>
    public static class MpcFormats
    {
        /// <summary>Decode an MPC packed date such as "K24AH" (2024 Oct 17).</summary>
        public static double UnpackEpoch(string p)
        {
            p = p.Trim();
            if (p.Length < 5) return double.NaN;
            int century = p[0] == 'I' ? 1800 : p[0] == 'J' ? 1900 : p[0] == 'K' ? 2000 : p[0] == 'L' ? 2100 : 0;
            if (century == 0) return double.NaN;
            int year = century + int.Parse(p.Substring(1, 2), CultureInfo.InvariantCulture);
            int month = Code(p[3]);
            double day = Code(p[4]);
            if (p.Length > 5) day += double.Parse("0." + p.Substring(5), CultureInfo.InvariantCulture);
            return AstroTime.CalendarToJd(year, month, day);
        }

        private static int Code(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'A' && c <= 'Z') return c - 'A' + 10;
            if (c >= 'a' && c <= 'z') return c - 'a' + 36;
            return 0;
        }

        private static char Encode(int v) => v < 10 ? (char)('0' + v) : v < 36 ? (char)('A' + v - 10) : (char)('a' + v - 36);

        /// <summary>Unpack a packed provisional designation ("K24A01B" -> "2024 AB1") or numbered ("00001" -> 1, "A0001" -> 100001).</summary>
        public static string UnpackDesignation(string p)
        {
            p = p.Trim();
            if (p.Length == 5)
            {
                if (char.IsDigit(p[0])) return int.Parse(p, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
                if (p[0] == '~')
                {
                    // numbers >= 620000 in base-62
                    long n = 0;
                    for (int i = 1; i < 5; i++) n = n * 62 + Code(p[i]);
                    return (620000 + n).ToString(CultureInfo.InvariantCulture);
                }
                return (Code(p[0]) * 10000 + int.Parse(p.Substring(1), CultureInfo.InvariantCulture)).ToString(CultureInfo.InvariantCulture);
            }
            if (p.Length == 7)
            {
                int century = p[0] == 'I' ? 18 : p[0] == 'J' ? 19 : p[0] == 'K' ? 20 : 0;
                if (century == 0) return p;
                string year = century.ToString(CultureInfo.InvariantCulture) + p.Substring(1, 2);
                char half = p[3], second = p[6];
                int cycle = Code(p[4]) * 10 + (p[5] - '0');
                // survey designations like "PLS2040" handled elsewhere
                return year + " " + half + (second == '0' ? "" : second.ToString()) + (cycle > 0 ? cycle.ToString(CultureInfo.InvariantCulture) : "");
            }
            return p;
        }

        public static string PackDesignationNumber(int number)
        {
            if (number < 100000) return number.ToString("00000", CultureInfo.InvariantCulture);
            if (number < 620000) return Encode(number / 10000) + (number % 10000).ToString("0000", CultureInfo.InvariantCulture);
            long n = number - 620000;
            var c = new char[4];
            for (int i = 3; i >= 0; i--) { c[i] = Encode((int)(n % 62)); n /= 62; }
            return "~" + new string(c);
        }

        private static double F(string line, int col1, int col2)
        {
            if (line.Length < col1) return double.NaN;
            int len = Math.Min(col2, line.Length) - col1 + 1;
            return AstroMath.ParseInvariant(line.Substring(col1 - 1, len));
        }

        private static string S(string line, int col1, int col2)
        {
            if (line.Length < col1) return "";
            int len = Math.Min(col2, line.Length) - col1 + 1;
            return line.Substring(col1 - 1, len).Trim();
        }

        /// <summary>Parse one MPCORB.DAT asteroid line (also used by NEA.txt, Distant.txt etc.).</summary>
        public static MinorBody ParseMpcOrb(string line)
        {
            if (line.Length < 103) return null;
            double epoch = UnpackEpoch(S(line, 21, 25));
            if (double.IsNaN(epoch)) return null;
            double M = F(line, 27, 35), w = F(line, 38, 46), node = F(line, 49, 57), inc = F(line, 60, 68), e = F(line, 71, 79), a = F(line, 93, 103);
            if (double.IsNaN(a) || double.IsNaN(e)) return null;
            var b = new MinorBody
            {
                Designation = S(line, 1, 7),
                H = F(line, 9, 13),
                G = F(line, 15, 19),
                Elements = OrbitalElements.FromMeanAnomaly(epoch, a, e, inc, node, w, M),
            };
            if (double.IsNaN(b.G)) b.G = 0.15;
            string u = S(line, 106, 106);
            double uv;
            if (double.TryParse(u, NumberStyles.Integer, CultureInfo.InvariantCulture, out uv)) b.Uncertainty = uv;
            string name = S(line, 167, 194);
            b.Name = name.Length > 0 ? name : UnpackDesignation(b.Designation);
            int num;
            if (name.StartsWith("(") && int.TryParse(name.Substring(1, name.IndexOf(')') - 1), out num)) b.Number = num;
            int flags;
            if (int.TryParse(S(line, 162, 165), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out flags))
                b.OrbitType = OrbitTypeFromFlags(flags & 0x3F);
            return b;
        }

        private static string OrbitTypeFromFlags(int t)
        {
            switch (t)
            {
                case 1: return "Atira";
                case 2: return "Aten";
                case 3: return "Apollo";
                case 4: return "Amor";
                case 5: return "Mars-crosser (q<1.665)";
                case 6: return "Hungaria";
                case 7: return "Phocaea";
                case 8: return "Hilda";
                case 9: return "Jupiter Trojan";
                case 10: return "Centaur";
                case 14: return "Plutino";
                case 15: return "Other resonant TNO";
                case 16: return "Cubewano";
                case 17: return "Scattered disk";
            }
            return "Main belt";
        }

        /// <summary>Parse one line of MPC CometEls.txt.</summary>
        public static MinorBody ParseCometEls(string line)
        {
            if (line.Length < 100) return null;
            int y = (int)F(line, 15, 18), mo = (int)F(line, 20, 21);
            double d = F(line, 23, 29);
            double q = F(line, 31, 39), e = F(line, 42, 49), w = F(line, 52, 59), node = F(line, 62, 69), inc = F(line, 72, 79);
            if (double.IsNaN(q) || double.IsNaN(e) || y == 0) return null;
            double tp = AstroTime.CalendarToJd(y, mo, d);
            double epoch = tp;
            int ey = (int)F(line, 82, 85);
            if (ey > 0) epoch = AstroTime.CalendarToJd(ey, (int)F(line, 86, 87), F(line, 88, 89));
            var el = new OrbitalElements
            {
                Epoch = epoch, PerihelionTime = tp, PerihelionDistance = q, Eccentricity = e,
                ArgPerihelion = w * AstroMath.Deg2Rad, Node = node * AstroMath.Deg2Rad, Inclination = inc * AstroMath.Deg2Rad
            };
            var b = new MinorBody
            {
                IsComet = true,
                Elements = el,
                H = F(line, 92, 95),
                CometK = F(line, 97, 100),
                Name = S(line, 103, 158),
                Designation = (S(line, 1, 4) + S(line, 5, 5) + S(line, 6, 12)).Trim(),
                OrbitType = e < 1 ? "Periodic/elliptic" : (e == 1 ? "Parabolic" : "Hyperbolic")
            };
            if (double.IsNaN(b.CometK)) b.CometK = 4.0;
            return b;
        }

        public static List<MinorBody> LoadMpcOrb(string path, double maxH = 99, int max = int.MaxValue)
        {
            var list = new List<MinorBody>();
            bool started = !Path.GetFileName(path).StartsWith("MPCORB", StringComparison.OrdinalIgnoreCase);
            foreach (var line in File.ReadLines(path))
            {
                if (!started) { if (line.StartsWith("-----")) started = true; continue; }
                var b = ParseMpcOrb(line);
                if (b == null) continue;
                if (!double.IsNaN(b.H) && b.H > maxH) continue;
                list.Add(b);
                if (list.Count >= max) break;
            }
            return list;
        }

        public static List<MinorBody> LoadComets(string path)
        {
            var list = new List<MinorBody>();
            foreach (var line in File.ReadLines(path))
            {
                var b = ParseCometEls(line);
                if (b != null) list.Add(b);
            }
            return list;
        }

        /// <summary>Format elements back into a MPC-style one-line summary for export/logs.</summary>
        public static string Describe(MinorBody b)
        {
            var el = b.Elements;
            return string.Format(CultureInfo.InvariantCulture, "{0}: q={1:0.000000} e={2:0.000000} i={3:0.0000} Ω={4:0.0000} ω={5:0.0000} Tp=JD{6:0.00000} H={7:0.0}",
                b.DisplayName, el.PerihelionDistance, el.Eccentricity, el.Inclination * AstroMath.Rad2Deg, el.Node * AstroMath.Rad2Deg,
                el.ArgPerihelion * AstroMath.Rad2Deg, el.PerihelionTime, b.H);
        }
    }

    /// <summary>Holds loaded minor bodies and computes their places.</summary>
    public class MinorBodyStore
    {
        public static MinorBodyStore Instance { get; } = new MinorBodyStore();
        public List<MinorBody> Asteroids { get; private set; } = new List<MinorBody>();
        public List<MinorBody> Comets { get; private set; } = new List<MinorBody>();
        public event EventHandler Changed;

        public IEnumerable<MinorBody> All => Asteroids.Concat(Comets);

        public void LoadFromFolder(string folder)
        {
            try
            {
                foreach (var name in new[] { "MPCORB.DAT", "mpcorb_extended.dat", "NEA.txt", "Distant.txt", "Unusual.txt", "Bright.txt" })
                {
                    var p = Path.Combine(folder, name);
                    if (File.Exists(p))
                    {
                        // MPCORB is huge; keep bright objects only by default
                        var list = MpcFormats.LoadMpcOrb(p, name.StartsWith("MPCORB", StringComparison.OrdinalIgnoreCase) ? 15.5 : 99);
                        MergeAsteroids(list);
                    }
                }
                var c = Path.Combine(folder, "CometEls.txt");
                if (File.Exists(c)) Comets = MpcFormats.LoadComets(c);
                if (Asteroids.Count == 0) Asteroids = BuiltInAsteroids();
                Log.Info("MinorBodies", string.Format("{0} asteroids, {1} comets loaded", Asteroids.Count, Comets.Count));
            }
            catch (Exception ex) { Log.Error("MinorBodies", ex); }
            if (Changed != null) Changed(this, EventArgs.Empty);
        }

        private void MergeAsteroids(List<MinorBody> list)
        {
            var map = Asteroids.ToDictionary(a => a.Designation, a => a);
            foreach (var a in list) map[a.Designation] = a;
            Asteroids = map.Values.ToList();
        }

        public void Add(MinorBody b)
        {
            if (b.IsComet) Comets.Add(b); else Asteroids.Add(b);
            if (Changed != null) Changed(this, EventArgs.Empty);
        }

        public MinorBody Find(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            name = name.Trim();
            foreach (var b in All)
                if (string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase) || string.Equals(b.Designation, name, StringComparison.OrdinalIgnoreCase)) return b;
            foreach (var b in All)
                if (b.Name != null && b.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0) return b;
            return null;
        }

        /// <summary>Apparent place including magnitude for a minor body.</summary>
        public static BodyPlace Place(SkyContext ctx, MinorBody b)
        {
            double lt;
            Func<double, Vec3> pos = b.PositionForEphemeris;
            var topo = ctx.TopocentricWithLightTime(pos, out lt);
            var p = ctx.MakePlace(topo, lt, pos);
            p.Magnitude = b.Magnitude(p.HelioDistance, p.Distance, p.PhaseAngle);
            return p;
        }

        /// <summary>Osculating elements of the first four asteroids (JPL SBDB, epoch JD 2461200.5 TDB) for offline use.</summary>
        public static List<MinorBody> BuiltInAsteroids()
        {
            double ep = 2461200.5;
            return new List<MinorBody>
            {
                new MinorBody { Number = 1, Name = "(1) Ceres", Designation = "00001", H = 3.34, G = 0.12, Elements = OrbitalElements.FromMeanAnomaly(ep, 2.765552595034094, 0.07969229514816586, 10.58802780183462, 80.24862682043221, 73.29421453021587, 274.4193463761342) },
                new MinorBody { Number = 2, Name = "(2) Pallas", Designation = "00002", H = 4.12, G = 0.11, Elements = OrbitalElements.FromMeanAnomaly(ep, 2.769559010737709, 0.2307000995648547, 34.93279321851542, 172.8866193357694, 310.9699161652136, 254.2496521742734) },
                new MinorBody { Number = 3, Name = "(3) Juno", Designation = "00003", H = 5.19, G = 0.32, Elements = OrbitalElements.FromMeanAnomaly(ep, 2.670989527103278, 0.2556999836681878, 12.98659236598085, 169.8115953492418, 247.8950743075613, 262.7322944883855) },
                new MinorBody { Number = 4, Name = "(4) Vesta", Designation = "00004", H = 3.25, G = 0.32, Elements = OrbitalElements.FromMeanAnomaly(ep, 2.361365965127599, 0.09020374382834395, 7.143925545058711, 103.701293265032, 151.4686478221564, 81.19015607686903) },
            };
        }
    }
}
