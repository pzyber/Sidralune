using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Sidralune.Imaging.ColorManagement
{
    public enum RenderingIntent { Perceptual = 0, RelativeColorimetric = 1, Saturation = 2, AbsoluteColorimetric = 3 }

    /// <summary>
    /// A tone response curve of an ICC profile: device value (0..1) to linear light and back. Works outside 0..1 too
    /// (negative values are mirrored, values above 1 follow the curve on), so float images keep everything.
    /// </summary>
    public sealed class ToneCurve
    {
        private readonly double _gamma;          // > 0: a pure power curve
        private readonly bool _srgb;             // the sRGB curve
        private readonly double[] _para;         // ICC parametric curve: type and parameters
        private readonly int _paraType;
        private readonly double[] _table;        // sampled curve (0..1 in, 0..1 out)

        private ToneCurve(double gamma, bool srgb, int paraType, double[] para, double[] table)
        {
            _gamma = gamma; _srgb = srgb; _paraType = paraType; _para = para; _table = table;
        }

        public static readonly ToneCurve Linear = new ToneCurve(1, false, -1, null, null);
        public static readonly ToneCurve Srgb = new ToneCurve(0, true, -1, null, null);
        public static ToneCurve Gamma(double g) => new ToneCurve(g, false, -1, null, null);
        public static ToneCurve Parametric(int type, double[] p) => new ToneCurve(0, false, type, p, null);

        public static ToneCurve Table(double[] t)
        {
            // a sampled sRGB curve (Windows' own sRGB profile, our built-ins) is evaluated exactly
            if (t.Length >= 16)
            {
                bool srgb = true;
                for (int i = 0; i < t.Length && srgb; i++) srgb = Math.Abs(t[i] - SrgbToLinear((double)i / (t.Length - 1))) < 2e-4;
                if (srgb) return Srgb;
                // a sampled power curve
                double g = Math.Log(t[t.Length / 2]) / Math.Log(0.5);
                if (!double.IsNaN(g) && g > 0.2 && g < 5)
                {
                    bool power = true;
                    for (int i = 1; i < t.Length && power; i++) power = Math.Abs(t[i] - Math.Pow((double)i / (t.Length - 1), g)) < 2e-4;
                    if (power) return Gamma(Math.Round(g * 256) / 256);
                }
            }
            return new ToneCurve(0, false, -1, null, t);
        }

        public bool IsLinear => _gamma == 1 && _table == null && _para == null && !_srgb;
        public bool IsSrgb => _srgb;
        public double PowerGamma => _gamma;

        public static double SrgbToLinear(double v) => v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        public static double LinearToSrgb(double v) => v <= 0.0031308 ? v * 12.92 : 1.055 * Math.Pow(v, 1 / 2.4) - 0.055;

        /// <summary>Device value to linear light.</summary>
        public double ToLinear(double v)
        {
            if (v < 0) return -ToLinear(-v);
            if (_srgb) return SrgbToLinear(v);
            if (_gamma > 0) return _gamma == 1 ? v : Math.Pow(v, _gamma);
            if (_para != null) return Para(v);
            return Sample(_table, v);
        }

        /// <summary>Linear light to device value.</summary>
        public double FromLinear(double y)
        {
            if (y < 0) return -FromLinear(-y);
            if (_srgb) return LinearToSrgb(y);
            if (_gamma > 0) return _gamma == 1 ? y : Math.Pow(y, 1 / _gamma);
            if (_para != null) return InvertMonotonic(Para, y);
            return InvertMonotonic(v => Sample(_table, v), y);
        }

        private double Para(double x)
        {
            var p = _para;
            double g = p[0];
            switch (_paraType)
            {
                case 0: return Math.Pow(x, g);
                case 1: return x >= -p[2] / p[1] ? Math.Pow(p[1] * x + p[2], g) : 0;
                case 2: return x >= -p[2] / p[1] ? Math.Pow(p[1] * x + p[2], g) + p[3] : p[3];
                case 3: return x >= p[4] ? Math.Pow(p[1] * x + p[2], g) : p[3] * x;
                default: return x >= p[4] ? Math.Pow(p[1] * x + p[2], g) + p[5] : p[3] * x + p[6];
            }
        }

        private static double Sample(double[] t, double v)
        {
            int n = t.Length - 1;
            if (v >= 1) return t[n] + (v - 1) * (t[n] - t[n - 1]) * n; // straight on beyond the end
            double f = v * n;
            int i = (int)f;
            return t[i] + (f - i) * (t[i + 1] - t[i]);
        }

        // the curve sampled densely once, for a fast inverse of tables and parametric curves
        private double[] _forward;
        private const int InverseSamples = 8192;

        private double InvertMonotonic(Func<double, double> f, double y)
        {
            var s = _forward;
            if (s == null)
            {
                s = new double[InverseSamples + 1];
                for (int i = 0; i <= InverseSamples; i++) s[i] = f((double)i / InverseSamples);
                _forward = s;
            }
            double top = s[InverseSamples];
            if (y >= top)
            {
                double slope = (top - s[InverseSamples - 1]) * InverseSamples;
                return slope > 0 ? 1 + (y - top) / slope : 1;
            }
            if (y <= s[0]) return 0;
            int lo = 0, hi = InverseSamples;
            while (hi - lo > 1) { int m = (lo + hi) / 2; if (s[m] < y) lo = m; else hi = m; }
            double d = s[hi] - s[lo];
            return (lo + (d > 0 ? (y - s[lo]) / d : 0)) / InverseSamples;
        }
    }

    /// <summary>
    /// An ICC colour profile (v2 or v4). Keeps the original bytes, so it is written back unchanged, and reads what
    /// colour management needs: the description, the matrix and tone curves of matrix/TRC profiles, and whether the
    /// profile has lookup tables (then only Windows can apply it).
    /// </summary>
    public sealed class IccProfile
    {
        public byte[] Bytes { get; }
        public string Description { get; private set; } = "";
        /// <summary>'RGB ', 'GRAY', 'CMYK'…</summary>
        public string ColorSpace { get; private set; } = "";
        /// <summary>'mntr', 'prtr', 'scnr', 'spac'…</summary>
        public string DeviceClass { get; private set; } = "";
        public int Version { get; private set; }
        /// <summary>RGB → XYZ (D50) matrix, rows X, Y, Z (null when not a matrix/TRC RGB profile).</summary>
        public double[,] Matrix { get; private set; }
        public ToneCurve[] Curves { get; private set; }
        public double[] WhitePoint { get; private set; } = { 0.9642, 1.0, 0.8249 };
        public bool HasLuts { get; private set; }
        /// <summary>The built-in profile's id, or null.</summary>
        public string Id { get; internal set; }

        public bool IsGray => ColorSpace == "GRAY";
        public bool IsRgb => ColorSpace == "RGB ";
        public bool IsMatrixShaper => Curves != null && (IsGray || Matrix != null);
        /// <summary>Can be applied exactly by Sidralune itself (in floating point); otherwise Windows does it.</summary>
        public bool IsSimple => IsMatrixShaper && !HasLuts;

        private string _fingerprint;
        /// <summary>Identifies the profile's content (MD5 of the bytes with the profile ID field cleared).</summary>
        public string Fingerprint
        {
            get
            {
                if (_fingerprint == null)
                {
                    var b = (byte[])Bytes.Clone();
                    if (b.Length >= 100) for (int i = 84; i < 100; i++) b[i] = 0;
                    using (var md5 = System.Security.Cryptography.MD5.Create()) _fingerprint = BitConverter.ToString(md5.ComputeHash(b)).Replace("-", "");
                }
                return _fingerprint;
            }
        }

        /// <summary>Luminance (Y) of each channel: the Y row of the RGB → XYZ matrix, normalised to sum 1.</summary>
        public double[] LuminanceWeights
        {
            get
            {
                if (Matrix == null) return new[] { 0.2126, 0.7152, 0.0722 };
                double s = Matrix[1, 0] + Matrix[1, 1] + Matrix[1, 2];
                return new[] { Matrix[1, 0] / s, Matrix[1, 1] / s, Matrix[1, 2] / s };
            }
        }

        public IccProfile(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 132) throw new InvalidDataException("Not an ICC profile (too short)");
            if (Ascii(bytes, 36, 4) != "acsp") throw new InvalidDataException("Not an ICC profile (no 'acsp' signature)");
            Bytes = bytes;
            Parse();
        }

        public static IccProfile Load(string path) => new IccProfile(File.ReadAllBytes(path));

        /// <summary>A profile from bytes, or null when they are not a usable profile.</summary>
        public static IccProfile TryCreate(byte[] bytes)
        {
            try { return bytes == null ? null : new IccProfile(bytes); } catch { return null; }
        }

        public override string ToString() => Description;

        public bool SameAs(IccProfile other) => other != null && (ReferenceEquals(this, other) || Fingerprint == other.Fingerprint);

        // ------------------------------------------------------------------ parsing

        private void Parse()
        {
            var b = Bytes;
            Version = b[8];
            DeviceClass = Ascii(b, 12, 4);
            ColorSpace = Ascii(b, 16, 4);
            int count = (int)U32(b, 128);
            var tags = new Dictionary<string, Tuple<int, int>>();
            for (int i = 0; i < count && 132 + i * 12 + 12 <= b.Length; i++)
            {
                int e = 132 + i * 12;
                string sig = Ascii(b, e, 4);
                int off = (int)U32(b, e + 4), size = (int)U32(b, e + 8);
                if (off >= 0 && size > 0 && off + size <= b.Length && !tags.ContainsKey(sig)) tags[sig] = Tuple.Create(off, size);
            }
            Tuple<int, int> t;
            if (tags.TryGetValue("desc", out t)) Description = ReadText(t.Item1, t.Item2);
            if (string.IsNullOrWhiteSpace(Description)) Description = "Unnamed profile";
            if (tags.TryGetValue("wtpt", out t)) WhitePoint = ReadXyz(t.Item1) ?? WhitePoint;
            HasLuts = tags.ContainsKey("A2B0") || tags.ContainsKey("B2A0") || tags.ContainsKey("A2B1") || tags.ContainsKey("B2A1");
            if (IsGray)
            {
                if (tags.TryGetValue("kTRC", out t)) Curves = new[] { ReadCurve(t.Item1) };
            }
            else if (IsRgb && tags.ContainsKey("rXYZ") && tags.ContainsKey("gXYZ") && tags.ContainsKey("bXYZ") && tags.ContainsKey("rTRC") && tags.ContainsKey("gTRC") && tags.ContainsKey("bTRC"))
            {
                var r = ReadXyz(tags["rXYZ"].Item1); var g = ReadXyz(tags["gXYZ"].Item1); var bl = ReadXyz(tags["bXYZ"].Item1);
                var curves = new[] { ReadCurve(tags["rTRC"].Item1), ReadCurve(tags["gTRC"].Item1), ReadCurve(tags["bTRC"].Item1) };
                if (r != null && g != null && bl != null && curves.All(c => c != null))
                {
                    Matrix = new double[3, 3];
                    for (int k = 0; k < 3; k++) { Matrix[k, 0] = r[k]; Matrix[k, 1] = g[k]; Matrix[k, 2] = bl[k]; }
                    Curves = curves;
                }
            }
        }

        private string ReadText(int off, int size)
        {
            var b = Bytes;
            string type = Ascii(b, off, 4);
            try
            {
                if (type == "desc")
                {
                    int n = (int)U32(b, off + 8);
                    return Encoding.ASCII.GetString(b, off + 12, Math.Max(0, Math.Min(n, size - 12))).TrimEnd('\0').Trim();
                }
                if (type == "mluc")
                {
                    int records = (int)U32(b, off + 8), recSize = (int)U32(b, off + 12);
                    // prefer English
                    int pick = 0;
                    for (int i = 0; i < records; i++) if (Ascii(b, off + 16 + i * recSize, 2) == "en") { pick = i; break; }
                    int rec = off + 16 + pick * recSize;
                    int len = (int)U32(b, rec + 4), at = (int)U32(b, rec + 8);
                    return Encoding.BigEndianUnicode.GetString(b, off + at, len).TrimEnd('\0').Trim();
                }
                if (type == "text") return Encoding.ASCII.GetString(b, off + 8, size - 8).TrimEnd('\0').Trim();
            }
            catch (ArgumentException) { }
            return "";
        }

        private double[] ReadXyz(int off)
        {
            if (Ascii(Bytes, off, 4) != "XYZ ") return null;
            return new[] { S15(Bytes, off + 8), S15(Bytes, off + 12), S15(Bytes, off + 16) };
        }

        private ToneCurve ReadCurve(int off)
        {
            var b = Bytes;
            string type = Ascii(b, off, 4);
            if (type == "curv")
            {
                int n = (int)U32(b, off + 8);
                if (n == 0) return ToneCurve.Linear;
                if (n == 1) return ToneCurve.Gamma(((b[off + 12] << 8) | b[off + 13]) / 256.0);
                if (off + 12 + n * 2 > b.Length) return null;
                var t = new double[n];
                for (int i = 0; i < n; i++) t[i] = ((b[off + 12 + i * 2] << 8) | b[off + 13 + i * 2]) / 65535.0;
                return ToneCurve.Table(t);
            }
            if (type == "para")
            {
                int f = (b[off + 8] << 8) | b[off + 9];
                int[] counts = { 1, 3, 4, 5, 7 };
                if (f < 0 || f > 4) return null;
                var p = new double[counts[f]];
                for (int i = 0; i < p.Length; i++) p[i] = S15(b, off + 12 + i * 4);
                if (f == 0) return ToneCurve.Gamma(p[0]);
                // the sRGB curve written parametrically (v4 sRGB profiles)
                if (f == 3 && Math.Abs(p[0] - 2.4) < 1e-3 && Math.Abs(p[1] - 1 / 1.055) < 1e-3 && Math.Abs(p[3] - 1 / 12.92) < 1e-3) return ToneCurve.Srgb;
                return ToneCurve.Parametric(f, p);
            }
            return null;
        }

        internal static uint U32(byte[] b, int o) => (uint)((b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3]);
        private static double S15(byte[] b, int o) => (int)U32(b, o) / 65536.0;
        private static string Ascii(byte[] b, int o, int n) => o + n <= b.Length ? Encoding.ASCII.GetString(b, o, n) : "";
    }
}
