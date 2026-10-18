using System;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace Sidralune.Imaging.IO
{
    /// <summary>
    /// XISF 1.0 (PixInsight) monolithic file reader/writer. Supports UInt8/16/32 and Float32/64 samples, planar and
    /// normal pixel storage, zlib and LZ4/LZ4HC compression with optional byte shuffling, FITS keywords.
    /// </summary>
    public static class XisfIO
    {
        private static readonly XNamespace Ns = "http://www.pixinsight.com/xisf";

        public static AstroImage Read(string path)
        {
            using (var fs = File.OpenRead(path))
            {
                var sig = new byte[16];
                fs.Read(sig, 0, 16);
                if (Encoding.ASCII.GetString(sig, 0, 8) != "XISF0100") throw new InvalidDataException("Not an XISF file");
                int hlen = BitConverter.ToInt32(sig, 8);
                var hb = new byte[hlen];
                fs.Read(hb, 0, hlen);
                var xml = XDocument.Parse(Encoding.UTF8.GetString(hb).TrimEnd('\0'));
                var imgEl = xml.Root.Elements().FirstOrDefault(e => e.Name.LocalName == "Image");
                if (imgEl == null) throw new InvalidDataException("XISF contains no Image element");
                var geo = ((string)imgEl.Attribute("geometry")).Split(':').Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToArray();
                int w = geo[0], h = geo[1], ch = geo.Length > 2 ? geo[2] : 1;
                string fmt = (string)imgEl.Attribute("sampleFormat") ?? "UInt16";
                string storage = (string)imgEl.Attribute("pixelStorage") ?? "Planar";
                var loc = ((string)imgEl.Attribute("location")).Split(':');
                if (loc[0] != "attachment") throw new NotSupportedException("Only attached XISF data blocks are supported");
                long pos = long.Parse(loc[1], CultureInfo.InvariantCulture);
                int size = int.Parse(loc[2], CultureInfo.InvariantCulture);
                fs.Position = pos;
                var raw = new byte[size];
                int rd = 0;
                while (rd < size) { int r = fs.Read(raw, rd, size - rd); if (r <= 0) break; rd += r; }
                string comp = (string)imgEl.Attribute("compression");
                if (!string.IsNullOrEmpty(comp)) raw = Decompress(raw, comp);
                int bps = fmt == "UInt8" ? 1 : fmt == "UInt16" ? 2 : fmt == "UInt32" || fmt == "Float32" ? 4 : 8;
                var img = new AstroImage(w, h, ch);
                img.OriginalBitDepth = fmt == "UInt8" ? 8 : fmt == "UInt16" ? 16 : fmt == "UInt32" ? 32 : -32;
                double lo = 0, hi = 1;
                var bounds = (string)imgEl.Attribute("bounds");
                if (bounds != null && fmt.StartsWith("Float"))
                {
                    var b = bounds.Split(':');
                    lo = double.Parse(b[0], CultureInfo.InvariantCulture); hi = double.Parse(b[1], CultureInfo.InvariantCulture);
                }
                bool planar = storage == "Planar";
                int n = w * h;
                for (int c = 0; c < ch; c++)
                {
                    var dst = img.Data[c];
                    for (int i = 0; i < n; i++)
                    {
                        long idx = planar ? ((long)c * n + i) * bps : ((long)i * ch + c) * bps;
                        double v;
                        switch (fmt)
                        {
                            case "UInt8": v = raw[idx] / 255.0; break;
                            case "UInt16": v = BitConverter.ToUInt16(raw, (int)idx) / 65535.0; break;
                            case "UInt32": v = BitConverter.ToUInt32(raw, (int)idx) / 4294967295.0; break;
                            case "Float32": v = BitConverter.ToSingle(raw, (int)idx); break;
                            default: v = BitConverter.ToDouble(raw, (int)idx); break;
                        }
                        if (fmt.StartsWith("Float") && (lo != 0 || hi != 1)) v = (v - lo) / (hi - lo);
                        dst[i] = (float)v;
                    }
                }
                foreach (var k in imgEl.Elements().Where(e => e.Name.LocalName == "FITSKeyword"))
                {
                    string name = (string)k.Attribute("name");
                    string val = (string)k.Attribute("value");
                    string com = (string)k.Attribute("comment");
                    if (name == "HISTORY" || name == "COMMENT") img.Header.Cards.Add(new FitsCard(name, null, com));
                    else img.Header.Cards.Add(new FitsCard(name, val, com));
                }
                var cfa = imgEl.Elements().FirstOrDefault(e => e.Name.LocalName == "ColorFilterArray");
                if (cfa != null && ch == 1)
                {
                    BayerPattern bp;
                    if (Enum.TryParse(((string)cfa.Attribute("pattern") ?? "").ToUpperInvariant(), out bp)) img.Bayer = bp;
                }
                img.SourcePath = path;
                return img;
            }
        }

        private static byte[] Decompress(byte[] data, string spec)
        {
            var p = spec.Split(':');
            string codec = p[0];
            int usize = int.Parse(p[1], CultureInfo.InvariantCulture);
            int itemSize = p.Length > 2 ? int.Parse(p[2], CultureInfo.InvariantCulture) : 1;
            bool shuffled = codec.EndsWith("+sh");
            codec = codec.Replace("+sh", "");
            byte[] outp;
            if (codec == "zlib")
            {
                using (var ms = new MemoryStream(data, 2, data.Length - 2))
                using (var ds = new DeflateStream(ms, CompressionMode.Decompress))
                {
                    outp = new byte[usize];
                    int r = 0;
                    while (r < usize) { int k = ds.Read(outp, r, usize - r); if (k <= 0) break; r += k; }
                }
            }
            else if (codec == "lz4" || codec == "lz4hc") outp = Lz4Decode(data, usize);
            else throw new NotSupportedException("XISF compression codec '" + codec + "' is not supported");
            if (shuffled && itemSize > 1) outp = Unshuffle(outp, itemSize);
            return outp;
        }

        private static byte[] Unshuffle(byte[] s, int itemSize)
        {
            int n = s.Length / itemSize;
            var d = new byte[s.Length];
            for (int j = 0; j < itemSize; j++)
                for (int i = 0; i < n; i++)
                    d[i * itemSize + j] = s[j * n + i];
            Array.Copy(s, n * itemSize, d, n * itemSize, s.Length - n * itemSize);
            return d;
        }

        private static byte[] Shuffle(byte[] s, int itemSize)
        {
            int n = s.Length / itemSize;
            var d = new byte[s.Length];
            for (int j = 0; j < itemSize; j++)
                for (int i = 0; i < n; i++)
                    d[j * n + i] = s[i * itemSize + j];
            return d;
        }

        /// <summary>LZ4 block format decoder.</summary>
        public static byte[] Lz4Decode(byte[] src, int outSize)
        {
            var dst = new byte[outSize];
            int ip = 0, op = 0;
            while (ip < src.Length)
            {
                int token = src[ip++];
                int litLen = token >> 4;
                if (litLen == 15) { int b; do { b = src[ip++]; litLen += b; } while (b == 255); }
                Buffer.BlockCopy(src, ip, dst, op, litLen);
                ip += litLen; op += litLen;
                if (ip >= src.Length) break;
                int offset = src[ip] | (src[ip + 1] << 8);
                ip += 2;
                int matchLen = token & 15;
                if (matchLen == 15) { int b; do { b = src[ip++]; matchLen += b; } while (b == 255); }
                matchLen += 4;
                int m = op - offset;
                for (int k = 0; k < matchLen; k++) dst[op++] = dst[m + k];
            }
            return dst;
        }

        /// <summary>Write XISF (Float32 by default, or UInt16), optionally zlib-compressed with byte shuffling.</summary>
        public static void Write(string path, AstroImage img, bool float32 = true, bool compress = true)
        {
            int w = img.Width, h = img.Height, ch = img.Channels, n = w * h;
            int bps = float32 ? 4 : 2;
            var raw = new byte[(long)n * ch * bps];
            for (int c = 0; c < ch; c++)
            {
                var d = img.Data[c];
                for (int i = 0; i < n; i++)
                {
                    int o = (c * n + i) * bps;
                    if (float32) { var b = BitConverter.GetBytes(d[i]); Buffer.BlockCopy(b, 0, raw, o, 4); }
                    else
                    {
                        ushort v = (ushort)Math.Max(0, Math.Min(65535, Math.Round(d[i] * 65535)));
                        raw[o] = (byte)v; raw[o + 1] = (byte)(v >> 8);
                    }
                }
            }
            byte[] block = raw;
            string compAttr = null;
            if (compress)
            {
                var sh = Shuffle(raw, bps);
                using (var ms = new MemoryStream())
                {
                    ms.WriteByte(0x78); ms.WriteByte(0x9C);
                    using (var ds = new DeflateStream(ms, CompressionLevel.Optimal, true)) ds.Write(sh, 0, sh.Length);
                    uint adler = Adler32(sh);
                    ms.WriteByte((byte)(adler >> 24)); ms.WriteByte((byte)(adler >> 16)); ms.WriteByte((byte)(adler >> 8)); ms.WriteByte((byte)adler);
                    block = ms.ToArray();
                }
                compAttr = "zlib+sh:" + raw.Length + ":" + bps;
            }
            // header with placeholder position, then fix up
            Func<long, string> build = pos =>
            {
                var image = new XElement(Ns + "Image",
                    new XAttribute("geometry", w + ":" + h + ":" + ch),
                    new XAttribute("sampleFormat", float32 ? "Float32" : "UInt16"),
                    new XAttribute("colorSpace", ch >= 3 ? "RGB" : "Gray"),
                    new XAttribute("pixelStorage", "Planar"),
                    new XAttribute("location", "attachment:" + pos.ToString("D12", CultureInfo.InvariantCulture) + ":" + block.Length));
                if (float32) image.Add(new XAttribute("bounds", "0:1"));
                if (compAttr != null) image.Add(new XAttribute("compression", compAttr));
                foreach (var c in img.Header.Cards)
                {
                    if (c.Key == "SIMPLE" || c.Key == "BITPIX" || c.Key.StartsWith("NAXIS") || c.Key == "BZERO" || c.Key == "BSCALE" || c.Key == "END") continue;
                    image.Add(new XElement(Ns + "FITSKeyword", new XAttribute("name", c.Key), new XAttribute("value", c.Value ?? ""), new XAttribute("comment", c.Comment ?? "")));
                }
                if (img.Bayer != BayerPattern.None && ch == 1)
                    image.Add(new XElement(Ns + "ColorFilterArray", new XAttribute("pattern", img.Bayer.ToString()), new XAttribute("width", 2), new XAttribute("height", 2)));
                var root = new XElement(Ns + "xisf", new XAttribute("version", "1.0"), image,
                    new XElement(Ns + "Metadata", new XElement(Ns + "Property", new XAttribute("id", "XISF:CreatorApplication"), new XAttribute("type", "String"), "Sidralune")));
                return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" + root.ToString(SaveOptions.DisableFormatting);
            };
            string header = build(0);
            long dataPos = 16 + Encoding.UTF8.GetByteCount(header);
            dataPos = (dataPos + 4095) / 4096 * 4096;
            header = build(dataPos);
            var hb = Encoding.UTF8.GetBytes(header);
            using (var fs = File.Create(path))
            {
                fs.Write(Encoding.ASCII.GetBytes("XISF0100"), 0, 8);
                fs.Write(BitConverter.GetBytes(hb.Length), 0, 4);
                fs.Write(new byte[4], 0, 4);
                fs.Write(hb, 0, hb.Length);
                fs.Write(new byte[dataPos - 16 - hb.Length], 0, (int)(dataPos - 16 - hb.Length));
                fs.Write(block, 0, block.Length);
            }
        }

        private static uint Adler32(byte[] d)
        {
            uint a = 1, b = 0;
            for (int i = 0; i < d.Length; i++) { a = (a + d[i]) % 65521; b = (b + a) % 65521; }
            return (b << 16) | a;
        }
    }
}
