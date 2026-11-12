using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace Sidralune.Imaging.ColorManagement
{
    /// <summary>
    /// The colour spaces Sidralune offers as working spaces, built as standard ICC v2 matrix/TRC profiles from their
    /// published primaries, white points and curves (nothing to license), plus the profiles installed in Windows.
    /// </summary>
    public static class IccProfiles
    {
        private static readonly double[] D65 = { 0.3127, 0.3290 }, D50 = { 0.3457, 0.3585 };

        // curves as written into the profiles: 0 = the sRGB curve, otherwise a power (as an ICC u8.8 value)
        private const double SrgbCurve = 0;
        private const double AdobeGamma = 563 / 256.0;   // 2.19921875, as Adobe RGB (1998) specifies
        private const double ProPhotoGamma = 1.8;

        private static readonly Lazy<List<IccProfile>> All = new Lazy<List<IccProfile>>(() => new List<IccProfile>
        {
            Make("srgb", "sRGB", new[] { 0.64, 0.33 }, new[] { 0.30, 0.60 }, new[] { 0.15, 0.06 }, D65, SrgbCurve),
            Make("srgb-linear", "sRGB (linear)", new[] { 0.64, 0.33 }, new[] { 0.30, 0.60 }, new[] { 0.15, 0.06 }, D65, 1),
            Make("displayp3", "Display P3", new[] { 0.680, 0.320 }, new[] { 0.265, 0.690 }, new[] { 0.150, 0.060 }, D65, SrgbCurve),
            Make("displayp3-linear", "Display P3 (linear)", new[] { 0.680, 0.320 }, new[] { 0.265, 0.690 }, new[] { 0.150, 0.060 }, D65, 1),
            Make("adobergb", "Adobe RGB (1998) compatible", new[] { 0.64, 0.33 }, new[] { 0.21, 0.71 }, new[] { 0.15, 0.06 }, D65, AdobeGamma),
            Make("adobergb-linear", "Adobe RGB (1998) compatible (linear)", new[] { 0.64, 0.33 }, new[] { 0.21, 0.71 }, new[] { 0.15, 0.06 }, D65, 1),
            Make("prophoto", "ProPhoto RGB", new[] { 0.7347, 0.2653 }, new[] { 0.1596, 0.8404 }, new[] { 0.0366, 0.0001 }, D50, ProPhotoGamma),
            Make("prophoto-linear", "ProPhoto RGB (linear)", new[] { 0.7347, 0.2653 }, new[] { 0.1596, 0.8404 }, new[] { 0.0366, 0.0001 }, D50, 1),
            MakeGray("gray-srgb", "Gray (sRGB curve)", SrgbCurve),
            MakeGray("gray-22", "Gray (gamma 2.2)", AdobeGamma),
            MakeGray("gray-18", "Gray (gamma 1.8)", ProPhotoGamma),
            MakeGray("gray-linear", "Gray (linear)", 1),
        });

        /// <summary>The built-in profiles, RGB first, in the order they are offered.</summary>
        public static IReadOnlyList<IccProfile> BuiltIn => All.Value;
        public static IEnumerable<IccProfile> BuiltInRgb => All.Value.Where(p => p.IsRgb);
        public static IEnumerable<IccProfile> BuiltInGray => All.Value.Where(p => p.IsGray);

        public static IccProfile Srgb => ById("srgb");
        public static IccProfile ProPhoto => ById("prophoto");
        public static IccProfile ById(string id) => All.Value.FirstOrDefault(p => p.Id == id);

        /// <summary>
        /// The profile a setting names: a built-in id or a file path. Falls back to ProPhoto RGB when the file is gone
        /// or unreadable.
        /// </summary>
        public static IccProfile FromSetting(string idOrPath)
        {
            if (string.IsNullOrEmpty(idOrPath)) return ProPhoto;
            var b = ById(idOrPath);
            if (b != null) return b;
            try { var p = IccProfile.Load(idOrPath); if (p.IsRgb || p.IsGray) return p; } catch { }
            return ProPhoto;
        }

        /// <summary>How a profile is stored in the settings: its built-in id, or its file path.</summary>
        public static string ToSetting(IccProfile p, string path = null) => p?.Id ?? path ?? "prophoto";

        /// <summary>The grey profile with the same tone curve as an RGB profile (for one-channel images).</summary>
        public static IccProfile GrayFor(IccProfile rgb)
        {
            if (rgb == null || rgb.IsGray) return rgb;
            var c = rgb.Curves != null ? rgb.Curves[1] : ToneCurve.Srgb;
            if (c.IsLinear) return ById("gray-linear");
            if (c.IsSrgb) return ById("gray-srgb");
            if (c.PowerGamma > 0) return Math.Abs(c.PowerGamma - 1.8) < 0.05 ? ById("gray-18") : ById("gray-22");
            return ById("gray-22");
        }

        /// <summary>The built-in RGB profile with a grey profile's curve (for writing grey images as RGB), or null for a custom curve.</summary>
        public static IccProfile RgbFor(IccProfile gray)
        {
            if (gray == null || gray.IsRgb) return gray;
            switch (gray.Id)
            {
                case "gray-srgb": return Srgb;
                case "gray-22": return ById("adobergb");
                case "gray-18": return ProPhoto;
                case "gray-linear": return ById("srgb-linear");
            }
            var c = gray.Curves?[0];
            if (c == null) return null;
            if (c.IsSrgb) return Srgb;
            if (c.IsLinear) return ById("srgb-linear");
            if (Math.Abs(c.PowerGamma - 1.8) < 0.01) return ProPhoto;
            if (Math.Abs(c.PowerGamma - 2.2) < 0.01) return ById("adobergb");
            return null;
        }

        // ------------------------------------------------------------------ installed profiles

        [DllImport("mscms.dll", CharSet = CharSet.Unicode)] private static extern bool GetColorDirectoryW(string machine, StringBuilder dir, ref int size);

        /// <summary>The Windows colour profile folder (C:\Windows\System32\spool\drivers\color).</summary>
        public static string ColorDirectory
        {
            get
            {
                try
                {
                    int size = 520;
                    var sb = new StringBuilder(size);
                    if (GetColorDirectoryW(null, sb, ref size)) return sb.ToString();
                }
                catch { }
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "spool", "drivers", "color");
            }
        }

        /// <summary>The RGB (or grey) profiles installed in Windows, by description, with their paths.</summary>
        public static List<KeyValuePair<string, string>> Installed(bool gray = false) => InstalledWhere(s => gray ? s.Gray : s.Rgb);

        /// <summary>
        /// Profiles a proof can simulate: installed RGB, CMYK and grey output profiles (printers, presses, displays,
        /// colour spaces), not device links, abstract or named-colour profiles.
        /// </summary>
        public static List<KeyValuePair<string, string>> InstalledForProofing() =>
            InstalledWhere(s => (s.Rgb || s.Gray || s.Space == "CMYK") && s.Class != "link" && s.Class != "abst" && s.Class != "nmcl" && s.Class != "scnr");

        // what is known about each installed profile (description, colour space, class), read again when the folder changes
        private sealed class Summary { public string Path, Description, Space, Class; public bool Gray => Space == "GRAY"; public bool Rgb => Space == "RGB "; }
        private static List<Summary> _summaries;
        private static DateTime _summariesStamp;
        private static readonly object SummaryLock = new object();

        private static List<Summary> Summaries()
        {
            lock (SummaryLock)
            {
                DateTime stamp;
                try { stamp = Directory.GetLastWriteTimeUtc(ColorDirectory); } catch { stamp = DateTime.MinValue; }
                if (_summaries != null && stamp == _summariesStamp) return _summaries;
                var list = new List<Summary>();
                try
                {
                    foreach (var f in Directory.GetFiles(ColorDirectory).Where(f => f.EndsWith(".icc", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".icm", StringComparison.OrdinalIgnoreCase)))
                    {
                        try
                        {
                            var p = IccProfile.Load(f);
                            list.Add(new Summary { Path = f, Description = p.Description, Space = p.ColorSpace, Class = p.DeviceClass });
                        }
                        catch { }
                    }
                }
                catch { }
                _summaries = list;
                _summariesStamp = stamp;
                return list;
            }
        }

        private static List<KeyValuePair<string, string>> InstalledWhere(Func<Summary, bool> keep) =>
            Summaries().Where(keep).Select(s => new KeyValuePair<string, string>(s.Description, s.Path)).OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase).ToList();

        /// <summary>A profile for proofing from a setting (built-in id or file path), any colour space; sRGB when unusable.</summary>
        public static IccProfile ProofingFromSetting(string idOrPath)
        {
            if (string.IsNullOrEmpty(idOrPath)) return Srgb;
            var b = ById(idOrPath);
            if (b != null) return b;
            try { return IccProfile.Load(idOrPath); } catch { return Srgb; }
        }

        // ------------------------------------------------------------------ building profiles

        private static IccProfile Make(string id, string name, double[] r, double[] g, double[] b, double[] white, double curve)
        {
            var p = new IccProfile(BuildRgb(name, r, g, b, white, curve)) { Id = id };
            return p;
        }

        private static IccProfile MakeGray(string id, string name, double curve) => new IccProfile(BuildGray(name, curve)) { Id = id };

        /// <summary>XYZ (Y = 1) of a chromaticity.</summary>
        private static double[] Xyz(double[] xy) => new[] { xy[0] / xy[1], 1.0, (1 - xy[0] - xy[1]) / xy[1] };

        /// <summary>RGB → XYZ matrix (rows X, Y, Z) for primaries and a white point, adapted to D50 (Bradford).</summary>
        public static double[,] RgbToXyzD50(double[] r, double[] g, double[] b, double[] white)
        {
            var P = new double[3, 3];
            var cols = new[] { Xyz(r), Xyz(g), Xyz(b) };
            for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) P[i, j] = cols[j][i];
            var W = Xyz(white);
            var S = Mat.Mul(Mat.Inverse(P), W);
            var M = new double[3, 3];
            for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) M[i, j] = P[i, j] * S[j];
            return Mat.Mul(Adaptation(W, Xyz(D50)), M);
        }

        private static readonly double[,] Bradford = { { 0.8951, 0.2664, -0.1614 }, { -0.7502, 1.7135, 0.0367 }, { 0.0389, -0.0685, 1.0296 } };

        /// <summary>Bradford chromatic adaptation from one white (XYZ) to another.</summary>
        public static double[,] Adaptation(double[] from, double[] to)
        {
            var a = Mat.Mul(Bradford, from);
            var c = Mat.Mul(Bradford, to);
            var d = new double[3, 3];
            for (int i = 0; i < 3; i++) d[i, i] = c[i] / a[i];
            return Mat.Mul(Mat.Inverse(Bradford), Mat.Mul(d, Bradford));
        }

        public static byte[] BuildRgb(string name, double[] r, double[] g, double[] b, double[] white, double curve)
        {
            var M = RgbToXyzD50(r, g, b, white);
            var tags = new List<Tuple<string, byte[]>>
            {
                Tuple.Create("desc", Desc(name)),
                Tuple.Create("cprt", Text("No copyright, use freely")),
                Tuple.Create("wtpt", XyzTag(Xyz(white))),
            };
            if (white != D50) tags.Add(Tuple.Create("chad", Sf32(Adaptation(Xyz(white), Xyz(D50)))));
            tags.Add(Tuple.Create("rXYZ", XyzTag(new[] { M[0, 0], M[1, 0], M[2, 0] })));
            tags.Add(Tuple.Create("gXYZ", XyzTag(new[] { M[0, 1], M[1, 1], M[2, 1] })));
            tags.Add(Tuple.Create("bXYZ", XyzTag(new[] { M[0, 2], M[1, 2], M[2, 2] })));
            var trc = Curve(curve);
            tags.Add(Tuple.Create("rTRC", trc)); tags.Add(Tuple.Create("gTRC", trc)); tags.Add(Tuple.Create("bTRC", trc));
            return Assemble("RGB ", tags);
        }

        public static byte[] BuildGray(string name, double curve)
        {
            var tags = new List<Tuple<string, byte[]>>
            {
                Tuple.Create("desc", Desc(name)),
                Tuple.Create("cprt", Text("No copyright, use freely")),
                Tuple.Create("wtpt", XyzTag(Xyz(D50))),
                Tuple.Create("kTRC", Curve(curve)),
            };
            return Assemble("GRAY", tags);
        }

        private static byte[] Assemble(string space, List<Tuple<string, byte[]>> tags)
        {
            // header (128) + tag count + table; tag data 4-byte aligned; identical data (the three TRCs) shared
            int tableEnd = 128 + 4 + tags.Count * 12;
            var offsets = new List<int>();
            var data = new MemoryStream();
            var placed = new Dictionary<byte[], int>();
            foreach (var t in tags)
            {
                int at;
                if (!placed.TryGetValue(t.Item2, out at))
                {
                    while (data.Length % 4 != 0) data.WriteByte(0);
                    at = tableEnd + (int)data.Length;
                    data.Write(t.Item2, 0, t.Item2.Length);
                    placed[t.Item2] = at;
                }
                offsets.Add(at);
            }
            while (data.Length % 4 != 0) data.WriteByte(0);
            int size = tableEnd + (int)data.Length;
            var o = new byte[size];
            Be(o, 0, (uint)size);
            Be(o, 8, 0x02400000);                       // version 2.4
            Asc(o, 12, "mntr"); Asc(o, 16, space); Asc(o, 20, "XYZ ");
            // a fixed creation date, so the same profile always has the same bytes
            Be16(o, 24, 2026); Be16(o, 26, 1); Be16(o, 28, 1);
            Asc(o, 36, "acsp"); Asc(o, 40, "MSFT");
            Be(o, 68, ToS15(0.9642)); Be(o, 72, ToS15(1.0)); Be(o, 76, ToS15(0.8249));
            Asc(o, 80, "SDRL");
            Be(o, 128, (uint)tags.Count);
            for (int i = 0; i < tags.Count; i++)
            {
                int e = 132 + i * 12;
                Asc(o, e, tags[i].Item1); Be(o, e + 4, (uint)offsets[i]); Be(o, e + 8, (uint)tags[i].Item2.Length);
            }
            var d = data.ToArray();
            Array.Copy(d, 0, o, tableEnd, d.Length);
            return o;
        }

        private static byte[] Desc(string s)
        {
            var a = Encoding.ASCII.GetBytes(s);
            var b = new byte[12 + a.Length + 1 + 4 + 4 + 2 + 1 + 67];
            Asc(b, 0, "desc");
            Be(b, 8, (uint)(a.Length + 1));
            Array.Copy(a, 0, b, 12, a.Length);
            return b;
        }

        private static byte[] Text(string s)
        {
            var a = Encoding.ASCII.GetBytes(s);
            var b = new byte[8 + a.Length + 1];
            Asc(b, 0, "text");
            Array.Copy(a, 0, b, 8, a.Length);
            return b;
        }

        private static byte[] XyzTag(double[] v)
        {
            var b = new byte[20];
            Asc(b, 0, "XYZ ");
            for (int i = 0; i < 3; i++) Be(b, 8 + i * 4, ToS15(v[i]));
            return b;
        }

        private static byte[] Sf32(double[,] m)
        {
            var b = new byte[8 + 36];
            Asc(b, 0, "sf32");
            for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) Be(b, 8 + (i * 3 + j) * 4, ToS15(m[i, j]));
            return b;
        }

        private static byte[] Curve(double gamma)
        {
            if (gamma == 1) { var b0 = new byte[12]; Asc(b0, 0, "curv"); return b0; }
            if (gamma > 0)
            {
                var b1 = new byte[14];
                Asc(b1, 0, "curv"); Be(b1, 8, 1);
                int v = (int)Math.Round(gamma * 256);
                b1[12] = (byte)(v >> 8); b1[13] = (byte)v;
                return b1;
            }
            // the sRGB curve, sampled
            const int n = 4096;
            var b = new byte[12 + n * 2];
            Asc(b, 0, "curv"); Be(b, 8, n);
            for (int i = 0; i < n; i++)
            {
                int v = (int)Math.Round(ToneCurve.SrgbToLinear((double)i / (n - 1)) * 65535);
                b[12 + i * 2] = (byte)(v >> 8); b[13 + i * 2] = (byte)v;
            }
            return b;
        }

        private static uint ToS15(double v) => (uint)(int)Math.Round(v * 65536);
        private static void Be(byte[] b, int o, uint v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }
        private static void Be16(byte[] b, int o, int v) { b[o] = (byte)(v >> 8); b[o + 1] = (byte)v; }
        private static void Asc(byte[] b, int o, string s) { for (int i = 0; i < s.Length; i++) b[o + i] = (byte)s[i]; }
    }

    /// <summary>Small 3×3 matrix helpers.</summary>
    internal static class Mat
    {
        public static double[] Mul(double[,] m, double[] v) => new[]
        {
            m[0, 0] * v[0] + m[0, 1] * v[1] + m[0, 2] * v[2],
            m[1, 0] * v[0] + m[1, 1] * v[1] + m[1, 2] * v[2],
            m[2, 0] * v[0] + m[2, 1] * v[1] + m[2, 2] * v[2]
        };

        public static double[,] Mul(double[,] a, double[,] b)
        {
            var r = new double[3, 3];
            for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) r[i, j] = a[i, 0] * b[0, j] + a[i, 1] * b[1, j] + a[i, 2] * b[2, j];
            return r;
        }

        public static double[,] Inverse(double[,] m)
        {
            double a = m[0, 0], b = m[0, 1], c = m[0, 2], d = m[1, 0], e = m[1, 1], f = m[1, 2], g = m[2, 0], h = m[2, 1], k = m[2, 2];
            double det = a * (e * k - f * h) - b * (d * k - f * g) + c * (d * h - e * g);
            if (Math.Abs(det) < 1e-12) throw new InvalidOperationException("Singular colour matrix");
            return new[,]
            {
                { (e * k - f * h) / det, (c * h - b * k) / det, (b * f - c * e) / det },
                { (f * g - d * k) / det, (a * k - c * g) / det, (c * d - a * f) / det },
                { (d * h - e * g) / det, (b * g - a * h) / det, (a * e - b * d) / det }
            };
        }
    }
}
