using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace Sidralune.Imaging.IO
{
    /// <summary>
    /// FITS reader/writer (primary HDU and IMAGE extensions). Supports BITPIX 8/16/32/64/-32/-64, BZERO/BSCALE,
    /// 2D mono and 3-plane colour cubes, and Bayer keywords (BAYERPAT, XBAYROFF, YBAYROFF).
    /// </summary>
    public static class FitsIO
    {
        private const int Block = 2880;

        public static FitsHeader ReadHeader(Stream s)
        {
            var h = new FitsHeader();
            var card = new byte[80];
            bool end = false;
            while (!end)
            {
                for (int i = 0; i < 36; i++)
                {
                    if (s.Read(card, 0, 80) != 80) throw new EndOfStreamException("Truncated FITS header");
                    if (end) continue;
                    string line = Encoding.ASCII.GetString(card);
                    string key = line.Substring(0, 8).Trim();
                    if (key == "END") { end = true; continue; }
                    if (key.Length == 0) continue;
                    if (line.Length > 9 && line[8] == '=' && key != "HISTORY" && key != "COMMENT")
                    {
                        string rest = line.Substring(10);
                        string val, comment = null;
                        SplitValue(rest, out val, out comment);
                        h.Cards.Add(new FitsCard(key, val, comment));
                    }
                    else h.Cards.Add(new FitsCard(key, null, line.Length > 8 ? line.Substring(8).Trim() : ""));
                }
            }
            return h;
        }

        private static void SplitValue(string rest, out string val, out string comment)
        {
            comment = null;
            string t = rest.TrimStart();
            if (t.StartsWith("'"))
            {
                int i = 1;
                while (i < t.Length)
                {
                    if (t[i] == '\'')
                    {
                        if (i + 1 < t.Length && t[i + 1] == '\'') { i += 2; continue; }
                        break;
                    }
                    i++;
                }
                val = t.Substring(0, Math.Min(i + 1, t.Length));
                int slash = t.IndexOf('/', Math.Min(i + 1, t.Length));
                if (slash >= 0) comment = t.Substring(slash + 1).Trim();
            }
            else
            {
                int slash = t.IndexOf('/');
                val = (slash >= 0 ? t.Substring(0, slash) : t).Trim();
                if (slash >= 0) comment = t.Substring(slash + 1).Trim();
            }
        }

        public static AstroImage Read(string path, int hduIndex = -1)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
            {
                var img = Read(fs, hduIndex);
                img.SourcePath = path;
                return img;
            }
        }

        /// <summary>Reads the first HDU with image data (or a specific index).</summary>
        public static AstroImage Read(Stream fs, int hduIndex = -1)
        {
            int idx = 0;
            FitsHeader primary = null;
            while (fs.Position < fs.Length)
            {
                var h = ReadHeader(fs);
                if (primary == null) primary = h;
                int bitpix = h.GetInt("BITPIX"), naxis = h.GetInt("NAXIS");
                long n = naxis > 0 ? 1 : 0;
                var dims = new int[naxis];
                for (int i = 0; i < naxis; i++) { dims[i] = h.GetInt("NAXIS" + (i + 1)); n *= dims[i]; }
                int pcount = h.GetInt("PCOUNT"), gcount = Math.Max(1, h.GetInt("GCOUNT", 1));
                long bytes = Math.Abs(bitpix) / 8 * (n + pcount) * gcount;
                bool isImage = naxis >= 2 && (h.GetString("XTENSION") == null || h.GetString("XTENSION") == "IMAGE");
                if (isImage && (hduIndex < 0 || hduIndex == idx))
                {
                    if (idx > 0)
                        foreach (var c in primary.Cards)
                            if (!h.Has(c.Key) && c.Key != "SIMPLE" && c.Key != "EXTEND" && c.Key != "NAXIS" && !c.Key.StartsWith("NAXIS")) h.Cards.Add(c);
                    return ReadData(fs, h, bitpix, dims);
                }
                long skip = (bytes + Block - 1) / Block * Block;
                fs.Seek(skip, SeekOrigin.Current);
                idx++;
            }
            throw new InvalidDataException("No image HDU found");
        }

        private static AstroImage ReadData(Stream fs, FitsHeader h, int bitpix, int[] dims)
        {
            int w = dims[0], ht = dims[1], planes = dims.Length > 2 ? dims[2] : 1;
            double bzero = h.GetDouble("BZERO", 0), bscale = h.GetDouble("BSCALE", 1);
            int bpp = Math.Abs(bitpix) / 8;
            long planeBytes = (long)w * ht * bpp;
            int channels = planes == 3 ? 3 : 1;
            var img = new AstroImage(w, ht, channels) { Header = h };
            // normalisation to 0..1 for integer data
            double norm = 1.0, offset = 0;
            if (bitpix == 8) { norm = 1.0 / 255; img.OriginalBitDepth = 8; }
            else if (bitpix == 16) { norm = 1.0 / 65535; img.OriginalBitDepth = 16; }
            else if (bitpix == 32) { norm = 1.0 / 4294967295.0; img.OriginalBitDepth = 32; }
            else if (bitpix == 64) { norm = 1.0 / 18446744073709551615.0; img.OriginalBitDepth = 64; }
            else { img.OriginalBitDepth = -32; }
            bool floatData = bitpix < 0;
            bool topDown = (h.GetString("ROWORDER") ?? "BOTTOM-UP").Trim().ToUpperInvariant() == "TOP-DOWN";
            var buf = new byte[planeBytes];
            for (int c = 0; c < channels; c++)
            {
                int read = 0;
                while (read < planeBytes)
                {
                    int r = fs.Read(buf, read, (int)Math.Min(planeBytes - read, int.MaxValue));
                    if (r <= 0) throw new EndOfStreamException("Truncated FITS data");
                    read += r;
                }
                var dst = img.Data[c];
                // FITS rows run bottom-up (first row = lowest y); flip to top-down for display convention
                Parallel.For(0, ht, y =>
                {
                    int dstRow = (topDown ? y : ht - 1 - y) * w;
                    long src = (long)y * w * bpp;
                    for (int x = 0; x < w; x++, src += bpp)
                    {
                        double v;
                        switch (bitpix)
                        {
                            case 8: v = buf[src]; break;
                            case 16: v = (short)((buf[src] << 8) | buf[src + 1]); break;
                            case 32: v = (int)((buf[src] << 24) | (buf[src + 1] << 16) | (buf[src + 2] << 8) | buf[src + 3]); break;
                            case 64:
                                {
                                    long l = 0;
                                    for (int k = 0; k < 8; k++) l = (l << 8) | buf[src + k];
                                    v = l; break;
                                }
                            case -32:
                                {
                                    var t = new byte[] { buf[src + 3], buf[src + 2], buf[src + 1], buf[src] };
                                    v = BitConverter.ToSingle(t, 0); break;
                                }
                            default:
                                {
                                    var t = new byte[8];
                                    for (int k = 0; k < 8; k++) t[k] = buf[src + 7 - k];
                                    v = BitConverter.ToDouble(t, 0); break;
                                }
                        }
                        v = v * bscale + bzero;
                        if (!floatData) v = (v - offset) * norm;
                        dst[dstRow + x] = (float)v;
                    }
                });
            }
            // floating point FITS from other software may be 0..65535; normalise if clearly so
            if (floatData)
            {
                float max = 0;
                foreach (var ch in img.Data) for (int i = 0; i < ch.Length; i += 97) if (ch[i] > max) max = ch[i];
                if (max > 2.0f)
                {
                    float s = max > 256 ? 1f / 65535f : 1f / 255f;
                    foreach (var ch in img.Data) for (int i = 0; i < ch.Length; i++) ch[i] *= s;
                    h.Set("SDRLNORM", (double)s, "Sidralune float normalisation applied");
                }
            }
            string bayer = h.GetString("BAYERPAT") ?? h.GetString("COLORTYP");
            if (channels == 1 && bayer != null)
            {
                BayerPattern bp;
                if (Enum.TryParse(bayer.Trim().ToUpperInvariant(), out bp))
                {
                    // FITS Bayer pattern refers to the bottom-up row order; since rows were flipped, adjust for odd height
                    int yoff = h.GetInt("YBAYROFF", 0), xoff = h.GetInt("XBAYROFF", 0);
                    if (topDown) { if ((yoff & 1) == 1) bp = Debayer.FlipVertical(bp); }
                    else if (((ht + yoff) & 1) == 0) bp = Debayer.FlipVertical(bp);
                    if ((xoff & 1) == 1) bp = Debayer.FlipHorizontal(bp);
                    img.Bayer = bp;
                }
            }
            return img;
        }

        /// <summary>Write FITS. bitpix: 16 (unsigned via BZERO=32768), 32 (int), -32 (float, default).</summary>
        public static void Write(string path, AstroImage img, int bitpix = -32)
        {
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                Write(fs, img, bitpix);
        }

        public static void Write(Stream fs, AstroImage img, int bitpix = -32)
        {
            var h = new FitsHeader();
            h.Set("SIMPLE", true, "conforms to FITS standard");
            h.Set("BITPIX", bitpix, "array data type");
            h.Set("NAXIS", img.Channels > 1 ? 3 : 2);
            h.Set("NAXIS1", img.Width);
            h.Set("NAXIS2", img.Height);
            if (img.Channels > 1) h.Set("NAXIS3", img.Channels);
            if (bitpix == 16) { h.Set("BZERO", 32768.0); h.Set("BSCALE", 1.0); }
            if (bitpix == 32) { h.Set("BZERO", 2147483648.0); h.Set("BSCALE", 1.0); }
            h.Set("ROWORDER", "BOTTOM-UP");
            var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "SIMPLE", "BITPIX", "NAXIS", "NAXIS1", "NAXIS2", "NAXIS3", "BZERO", "BSCALE", "EXTEND", "END", "ROWORDER", "SDRLNORM" };
            foreach (var c in img.Header.Cards) if (!skip.Contains(c.Key)) h.Cards.Add(new FitsCard(c.Key, c.Value, c.Comment));
            if (img.Bayer != BayerPattern.None && img.Channels == 1)
            {
                var bp = (img.Height & 1) == 0 ? Debayer.FlipVertical(img.Bayer) : img.Bayer;
                h.Set("BAYERPAT", bp.ToString());
            }
            h.Set("SWCREATE", "Sidralune");
            WriteHeader(fs, h);
            int w = img.Width, ht = img.Height, bpp = Math.Abs(bitpix) / 8;
            var row = new byte[w * bpp];
            long total = 0;
            for (int c = 0; c < img.Channels; c++)
            {
                var d = img.Data[c];
                for (int y = ht - 1; y >= 0; y--)
                {
                    int o = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        float v = d[o + x];
                        int p = x * bpp;
                        switch (bitpix)
                        {
                            case 8: row[p] = (byte)Math.Max(0, Math.Min(255, Math.Round(v * 255))); break;
                            case 16:
                                {
                                    int iv = (int)Math.Max(0, Math.Min(65535, Math.Round(v * 65535))) - 32768;
                                    row[p] = (byte)(iv >> 8); row[p + 1] = (byte)iv; break;
                                }
                            case 32:
                                {
                                    long lv = (long)Math.Max(0, Math.Min(4294967295.0, Math.Round(v * 4294967295.0))) - 2147483648L;
                                    int iv = (int)lv;
                                    row[p] = (byte)(iv >> 24); row[p + 1] = (byte)(iv >> 16); row[p + 2] = (byte)(iv >> 8); row[p + 3] = (byte)iv; break;
                                }
                            case -64:
                                {
                                    var b = BitConverter.GetBytes((double)v);
                                    for (int k = 0; k < 8; k++) row[p + k] = b[7 - k];
                                    break;
                                }
                            default:
                                {
                                    var b = BitConverter.GetBytes(v);
                                    row[p] = b[3]; row[p + 1] = b[2]; row[p + 2] = b[1]; row[p + 3] = b[0]; break;
                                }
                        }
                    }
                    fs.Write(row, 0, row.Length);
                    total += row.Length;
                }
            }
            long pad = (Block - total % Block) % Block;
            fs.Write(new byte[pad], 0, (int)pad);
        }

        public static void WriteHeader(Stream fs, FitsHeader h)
        {
            var sb = new StringBuilder();
            foreach (var c in h.Cards)
            {
                string line;
                if (c.Value == null)
                    line = c.Key.PadRight(8) + (c.Comment ?? "");
                else
                {
                    string v = c.Value.StartsWith("'") ? c.Value.PadRight(20) : c.Value.PadLeft(20);
                    line = c.Key.PadRight(8).Substring(0, 8) + "= " + v + (c.Comment != null ? " / " + c.Comment : "");
                }
                // long string values: split using CONTINUE convention is skipped; truncate
                if (line.Length > 80) line = line.Substring(0, 80);
                sb.Append(line.PadRight(80));
            }
            sb.Append("END".PadRight(80));
            while (sb.Length % Block != 0) sb.Append(' ');
            var bytes = Encoding.ASCII.GetBytes(sb.ToString());
            fs.Write(bytes, 0, bytes.Length);
        }
    }
}
