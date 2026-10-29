using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace Sidralune.Imaging
{
    public enum BayerPattern { None, RGGB, BGGR, GRBG, GBRG }

    /// <summary>One FITS header card.</summary>
    public class FitsCard
    {
        public string Key;
        public string Value;
        public string Comment;
        public FitsCard(string k, string v, string c = null) { Key = k; Value = v; Comment = c; }
        public override string ToString() => Key + " = " + Value + (Comment != null ? " / " + Comment : "");
    }

    /// <summary>Ordered FITS-style key/value header with typed accessors.</summary>
    public class FitsHeader
    {
        public readonly List<FitsCard> Cards = new List<FitsCard>();

        public FitsHeader Clone()
        {
            var h = new FitsHeader();
            foreach (var c in Cards) h.Cards.Add(new FitsCard(c.Key, c.Value, c.Comment));
            return h;
        }

        public string Get(string key)
        {
            foreach (var c in Cards) if (string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase)) return c.Value;
            return null;
        }

        public string GetString(string key)
        {
            var v = Get(key);
            if (v == null) return null;
            v = v.Trim();
            if (v.StartsWith("'")) { v = v.Substring(1); int e = v.LastIndexOf('\''); if (e >= 0) v = v.Substring(0, e); v = v.Replace("''", "'").TrimEnd(); }
            return v;
        }

        public double GetDouble(string key, double fallback = double.NaN)
        {
            var v = GetString(key);
            double d;
            if (v != null && double.TryParse(v.Replace('D', 'E'), NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
            return fallback;
        }

        public double GetDouble(string[] keys, double fallback = double.NaN)
        {
            foreach (var k in keys) { double d = GetDouble(k); if (!double.IsNaN(d)) return d; }
            return fallback;
        }

        public int GetInt(string key, int fallback = 0)
        {
            double d = GetDouble(key);
            return double.IsNaN(d) ? fallback : (int)d;
        }

        public bool Has(string key) => Get(key) != null;

        public void Set(string key, object value, string comment = null)
        {
            string v;
            if (value is string) v = "'" + ((string)value).Replace("'", "''") + "'";
            else if (value is bool) v = (bool)value ? "T" : "F";
            else if (value is double) v = ((double)value).ToString("G12", CultureInfo.InvariantCulture);
            else if (value is float) v = ((float)value).ToString("G8", CultureInfo.InvariantCulture);
            else v = Convert.ToString(value, CultureInfo.InvariantCulture);
            foreach (var c in Cards)
                if (string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase)) { c.Value = v; if (comment != null) c.Comment = comment; return; }
            Cards.Add(new FitsCard(key.ToUpperInvariant(), v, comment));
        }

        public void Remove(string key) => Cards.RemoveAll(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase));

        public void AddHistory(string text) => Cards.Add(new FitsCard("HISTORY", null, text));
    }

    /// <summary>
    /// Floating-point image with 1..n planar channels (row-major). Pixel values are normalised to 0..1 for integer
    /// sources (e.g. 16-bit 65535 -> 1.0) but may exceed that range after processing.
    /// </summary>
    public class AstroImage
    {
        public int Width { get; private set; }
        public int Height { get; private set; }
        public float[][] Data { get; private set; }
        public FitsHeader Header { get; set; } = new FitsHeader();
        public BayerPattern Bayer { get; set; } = BayerPattern.None;
        /// <summary>Optional per-pixel weight/coverage map (e.g. drizzle or mosaic).</summary>
        public float[] Weight;
        public string SourcePath;
        /// <summary>Original integer bit depth (for export and saturation tests).</summary>
        public int OriginalBitDepth = 16;

        public int Channels => Data.Length;
        public int PixelCount => Width * Height;
        public bool IsColor => Data.Length >= 3;

        public AstroImage(int w, int h, int channels)
        {
            Width = w; Height = h;
            Data = new float[channels][];
            for (int c = 0; c < channels; c++) Data[c] = new float[w * h];
        }

        public AstroImage(int w, int h, float[][] data)
        {
            Width = w; Height = h; Data = data;
        }

        public float this[int c, int x, int y]
        {
            get { return Data[c][y * Width + x]; }
            set { Data[c][y * Width + x] = value; }
        }

        public AstroImage Clone()
        {
            var d = new float[Channels][];
            for (int c = 0; c < Channels; c++) d[c] = (float[])Data[c].Clone();
            return new AstroImage(Width, Height, d)
            {
                Header = Header.Clone(), Bayer = Bayer, SourcePath = SourcePath, OriginalBitDepth = OriginalBitDepth,
                Weight = Weight != null ? (float[])Weight.Clone() : null
            };
        }

        public AstroImage CloneEmpty(int channels = -1) =>
            new AstroImage(Width, Height, channels < 0 ? Channels : channels) { Header = Header.Clone(), OriginalBitDepth = OriginalBitDepth };

        public void ReplaceData(int w, int h, float[][] data)
        {
            Width = w; Height = h; Data = data; Weight = null;
        }

        /// <summary>Luminance (Rec.709 weights for colour, the plane itself for mono).</summary>
        public float[] Luminance()
        {
            if (Channels == 1) return Data[0];
            var l = new float[PixelCount];
            var r = Data[0]; var g = Data[1]; var b = Data[2];
            Parallel.For(0, Height, Core.Util.StopRequest.Options, y =>
            {
                int o = y * Width;
                for (int x = 0; x < Width; x++) l[o + x] = 0.2126f * r[o + x] + 0.7152f * g[o + x] + 0.0722f * b[o + x];
            });
            return l;
        }

        public AstroImage ToMono()
        {
            var img = new AstroImage(Width, Height, new[] { (float[])Luminance().Clone() }) { Header = Header.Clone(), OriginalBitDepth = OriginalBitDepth };
            return img;
        }

        public AstroImage ExtractChannel(int c) =>
            new AstroImage(Width, Height, new[] { (float[])Data[c].Clone() }) { Header = Header.Clone(), OriginalBitDepth = OriginalBitDepth };

        public static AstroImage Combine(AstroImage r, AstroImage g, AstroImage b)
        {
            var img = new AstroImage(r.Width, r.Height, new[] { (float[])r.Data[0].Clone(), (float[])g.Data[0].Clone(), (float[])b.Data[0].Clone() });
            img.Header = r.Header.Clone();
            return img;
        }

        public void Apply(Func<float, float> f)
        {
            foreach (var ch in Data)
            {
                var a = ch;
                Parallel.For(0, Height, Core.Util.StopRequest.Options, y =>
                {
                    int o = y * Width;
                    for (int x = 0; x < Width; x++) a[o + x] = f(a[o + x]);
                });
            }
        }

        public void Clip(float lo = 0, float hi = 1) => Apply(v => v < lo ? lo : (v > hi ? hi : (float.IsNaN(v) ? lo : v)));

        /// <summary>Bilinear sample with edge clamping.</summary>
        public float Sample(int c, double x, double y)
        {
            int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
            double fx = x - x0, fy = y - y0;
            var d = Data[c];
            int w = Width, h = Height;
            int xa = Clampi(x0, w), xb = Clampi(x0 + 1, w), ya = Clampi(y0, h), yb = Clampi(y0 + 1, h);
            double v = d[ya * w + xa] * (1 - fx) * (1 - fy) + d[ya * w + xb] * fx * (1 - fy) + d[yb * w + xa] * (1 - fx) * fy + d[yb * w + xb] * fx * fy;
            return (float)v;
        }

        private static int Clampi(int v, int n) => v < 0 ? 0 : (v >= n ? n - 1 : v);

        // ---------------------------------------------------------------- common metadata accessors

        public double ExposureSeconds
        {
            get { return Header.GetDouble(new[] { "EXPTIME", "EXPOSURE", "EXP_TIME" }, 0); }
            set { Header.Set("EXPTIME", value, "Exposure time [s]"); }
        }

        public DateTime? DateObs
        {
            get
            {
                var s = Header.GetString("DATE-OBS") ?? Header.GetString("DATE_OBS");
                DateTime d;
                if (s != null && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out d)) return d;
                return null;
            }
            set { if (value.HasValue) Header.Set("DATE-OBS", value.Value.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture), "UTC start of exposure"); }
        }

        public double Temperature => Header.GetDouble(new[] { "CCD-TEMP", "CCDTEMP", "SENSOR-TEMP", "TEMPERAT" });
        public double Gain => Header.GetDouble(new[] { "GAIN", "EGAIN" });
        public string Filter => Header.GetString("FILTER");
        public string ObjectName => Header.GetString("OBJECT");
        public string ImageType => Header.GetString("IMAGETYP") ?? Header.GetString("FRAME");

        public override string ToString() => string.Format("{0}×{1}×{2}", Width, Height, Channels);
    }
}
