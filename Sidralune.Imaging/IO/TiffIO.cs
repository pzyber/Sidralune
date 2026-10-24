using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace Sidralune.Imaging.IO
{
    /// <summary>
    /// TIFF reader (8/16/32-bit integer and 32/64-bit float, chunky or planar, strips or tiles, none/LZW/Deflate/PackBits,
    /// horizontal and floating-point predictors) plus DNG (CFA, lossless JPEG, black/white levels) and Canon CR2 raw (CR3 is in Cr3IO).
    /// Writer produces uncompressed 16-bit or 32-bit float TIFF.
    /// </summary>
    public static class TiffIO
    {
        private class Ifd
        {
            public readonly Dictionary<int, long[]> Tags = new Dictionary<int, long[]>();
            public readonly Dictionary<int, double[]> Rationals = new Dictionary<int, double[]>();
            public readonly Dictionary<int, byte[]> Bytes = new Dictionary<int, byte[]>();
            public readonly Dictionary<int, string> Strings = new Dictionary<int, string>();
            public long Get(int tag, long def = 0) { long[] v; return Tags.TryGetValue(tag, out v) && v.Length > 0 ? v[0] : def; }
            public long[] GetArr(int tag) { long[] v; return Tags.TryGetValue(tag, out v) ? v : null; }
            public double GetR(int tag, double def) { double[] v; return Rationals.TryGetValue(tag, out v) && v.Length > 0 ? v[0] : (Tags.ContainsKey(tag) ? Get(tag) : def); }
        }

        private class Reader
        {
            public byte[] D;
            public bool Big;
            public ushort U16(long o) => Big ? (ushort)((D[o] << 8) | D[o + 1]) : (ushort)(D[o] | (D[o + 1] << 8));
            public uint U32(long o) => Big ? (uint)((D[o] << 24) | (D[o + 1] << 16) | (D[o + 2] << 8) | D[o + 3]) : (uint)(D[o] | (D[o + 1] << 8) | (D[o + 2] << 16) | (D[o + 3] << 24));
        }

        private static List<Ifd> ParseIfds(Reader r, long first)
        {
            var list = new List<Ifd>();
            var seen = new HashSet<long>();
            long off = first;
            while (off > 0 && off < r.D.Length && seen.Add(off))
            {
                var ifd = ParseIfd(r, off);
                list.Add(ifd);
                // SubIFDs (DNG raw is often there)
                var sub = ifd.GetArr(330);
                if (sub != null) foreach (var s in sub) if (s > 0 && s < r.D.Length && seen.Add(s)) list.Add(ParseIfd(r, s));
                // EXIF IFD is ignored for image data
                int n = r.U16(off);
                off = r.U32(off + 2 + n * 12);
            }
            return list;
        }

        private static Ifd ParseIfd(Reader r, long off)
        {
            var ifd = new Ifd();
            int n = r.U16(off);
            for (int i = 0; i < n; i++)
            {
                long e = off + 2 + i * 12;
                int tag = r.U16(e), type = r.U16(e + 2);
                long count = r.U32(e + 4);
                int size = type == 3 || type == 8 ? 2 : type == 4 || type == 9 || type == 11 || type == 13 ? 4 : type == 5 || type == 10 || type == 12 ? 8 : 1;
                long dataOff = size * count <= 4 ? e + 8 : r.U32(e + 8);
                if (dataOff + size * count > r.D.Length) continue;
                if (type == 2)
                {
                    ifd.Strings[tag] = Encoding.ASCII.GetString(r.D, (int)dataOff, (int)Math.Max(0, count - 1));
                    continue;
                }
                if (type == 1 || type == 7 || type == 6)
                {
                    var b = new byte[count];
                    Array.Copy(r.D, dataOff, b, 0, count);
                    ifd.Bytes[tag] = b;
                    ifd.Tags[tag] = b.Select(x => (long)x).ToArray();
                    continue;
                }
                if (type == 5 || type == 10)
                {
                    var rv = new double[count];
                    for (int k = 0; k < count; k++)
                    {
                        double num = type == 5 ? (double)r.U32(dataOff + k * 8) : (double)(int)r.U32(dataOff + k * 8);
                        double den = type == 5 ? (double)r.U32(dataOff + k * 8 + 4) : (double)(int)r.U32(dataOff + k * 8 + 4);
                        rv[k] = den != 0 ? num / den : 0;
                    }
                    ifd.Rationals[tag] = rv;
                    continue;
                }
                if (count > 10000000) continue;
                var v = new long[count];
                for (int k = 0; k < count; k++)
                {
                    long o = dataOff + k * size;
                    v[k] = size == 2 ? r.U16(o) : size == 4 ? r.U32(o) : r.D[o];
                }
                ifd.Tags[tag] = v;
            }
            return ifd;
        }

        public static AstroImage Read(string path)
        {
            var data = File.ReadAllBytes(path);
            var r = new Reader { D = data, Big = data[0] == 'M' };
            int magic = r.U16(2);
            if (magic != 42 && magic != 0x4F52 && magic != 0x5352) throw new InvalidDataException("Not a TIFF file");
            var ifds = ParseIfds(r, r.U32(4));
            bool cr2 = data.Length > 10 && data[8] == 'C' && data[9] == 'R';
            if (cr2) return ReadCr2(r, ifds, path);
            // choose: DNG raw (NewSubfileType 0 with CFA) > largest full-resolution image
            Ifd best = null;
            foreach (var ifd in ifds)
            {
                if (!ifd.Tags.ContainsKey(256)) continue;
                bool isCfa = ifd.Get(262) == 32803;
                if (ifd.Get(254) == 0 && isCfa) { best = ifd; break; }
                if (best == null || ifd.Get(256) * ifd.Get(257) > best.Get(256) * best.Get(257)) best = ifd;
            }
            if (best == null) throw new InvalidDataException("No image in TIFF");
            var img = Decode(r, best, ifds);
            img.SourcePath = path;
            ApplyExif(ifds, img);
            return img;
        }

        private static AstroImage Decode(Reader r, Ifd ifd, List<Ifd> all)
        {
            int w = (int)ifd.Get(256), h = (int)ifd.Get(257);
            var bpsArr = ifd.GetArr(258);
            int bps = bpsArr != null ? (int)bpsArr[0] : 1;
            int spp = (int)ifd.Get(277, 1);
            int comp = (int)ifd.Get(259, 1);
            int photometric = (int)ifd.Get(262, 1);
            int planar = (int)ifd.Get(284, 1);
            int predictor = (int)ifd.Get(317, 1);
            int sampleFormat = (int)ifd.Get(339, 1);
            bool tiled = ifd.Tags.ContainsKey(322);
            int tw = tiled ? (int)ifd.Get(322) : w;
            int th = tiled ? (int)ifd.Get(323) : (int)Math.Min(ifd.Get(278, h), h);
            var offsets = ifd.GetArr(tiled ? 324 : 273);
            var counts = ifd.GetArr(tiled ? 325 : 279);
            if (offsets == null) throw new InvalidDataException("TIFF without image data offsets");
            int ch = photometric == 32803 ? 1 : (spp >= 3 ? 3 : 1);
            var img = new AstroImage(w, h, ch);
            img.OriginalBitDepth = sampleFormat == 3 ? -32 : bps;
            double maxV = sampleFormat == 3 ? 1.0 : (bps >= 32 ? 4294967295.0 : (1L << bps) - 1);
            // DNG levels
            double black = 0, white = maxV;
            if (photometric == 32803)
            {
                double[] bl; long[] bli;
                if (ifd.Rationals.TryGetValue(50714, out bl) && bl.Length > 0) black = bl.Average();
                else if ((bli = ifd.GetArr(50714)) != null && bli.Length > 0) black = bli.Average();
                if (ifd.Tags.ContainsKey(50717)) white = ifd.Get(50717);
            }
            var lin = ifd.GetArr(50712);
            int tilesAcross = (w + tw - 1) / tw, tilesDown = (h + th - 1) / th;
            bool packed = comp == 1 && (bps == 10 || bps == 12 || bps == 14);
            int sampleBits = packed ? 16 : bps;
            bool sampleBig = r.Big && !packed;
            int bytesPerSample = Math.Max(1, sampleBits / 8);
            int planes = planar == 2 ? spp : 1;
            int perPixel = planar == 2 ? 1 : spp;
            for (int t = 0; t < offsets.Length; t++)
            {
                int plane = planar == 2 ? t / (tilesAcross * tilesDown) : 0;
                int tIdx = planar == 2 ? t % (tilesAcross * tilesDown) : t;
                int tx = (tIdx % tilesAcross) * tw, ty = (tIdx / tilesAcross) * th;
                int segH = tiled ? th : Math.Min(th, h - ty);
                int segW = tiled ? tw : w;
                ushort[] ljSamples = null;
                byte[] raw = null;
                int ljW = 0, ljC = 1;
                long off = offsets[t], cnt = counts != null && t < counts.Length ? counts[t] : r.D.Length - off;
                if (comp == 7)
                {
                    var lj = LosslessJpeg.Decode(r.D, (int)off, (int)cnt);
                    ljSamples = lj.Samples; ljW = lj.Width; ljC = lj.Components;
                }
                else raw = Decompress(r.D, off, cnt, comp, segW * segH * perPixel * bytesPerSample, bps);
                if (raw != null && predictor == 2) UndoHorizontalPredictor(raw, segW, segH, perPixel, bytesPerSample, sampleBig);
                if (raw != null && predictor == 3) raw = UndoFloatPredictor(raw, segW, segH, perPixel, bytesPerSample, sampleBig);
                for (int y = 0; y < segH; y++)
                {
                    int iy = ty + y;
                    if (iy >= h) break;
                    for (int x = 0; x < segW; x++)
                    {
                        int ix = tx + x;
                        if (ix >= w) continue;
                        for (int s = 0; s < perPixel; s++)
                        {
                            int c = planar == 2 ? plane : s;
                            if (c >= ch) continue;
                            double v;
                            if (ljSamples != null)
                            {
                                // lossless JPEG tiles may be encoded as (w/n) x n interleaved components: same linear order
                                int si = y * Math.Max(segW, ljW * ljC) + x;
                                v = si < ljSamples.Length ? ljSamples[si] : 0;
                            }
                            else
                            {
                                long p = ((long)y * segW + x) * perPixel * bytesPerSample + s * bytesPerSample;
                                if (p + bytesPerSample > raw.Length) continue;
                                v = ReadSample(raw, p, sampleBits, sampleFormat, sampleBig);
                            }
                            if (lin != null && v < lin.Length) v = lin[(int)v];
                            double norm = photometric == 32803 ? (v - black) / (white - black) : v / maxV;
                            if (photometric == 0 && sampleFormat != 3) norm = 1 - norm; // WhiteIsZero
                            img.Data[c][iy * w + ix] = (float)norm;
                        }
                    }
                }
            }
            if (photometric == 32803)
            {
                var pat = ifd.GetArr(33422);
                if (pat != null && pat.Length >= 4)
                {
                    string s = string.Concat(pat.Take(4).Select(p => p == 0 ? "R" : p == 1 ? "G" : "B"));
                    BayerPattern bp;
                    if (Enum.TryParse(s, out bp)) img.Bayer = bp;
                }
                // DNG active area crop
                var area = ifd.GetArr(50829);
                if (area != null && area.Length == 4 && (area[0] != 0 || area[1] != 0 || area[2] != h || area[3] != w))
                    img = Geometry.Crop(img, (int)area[1], (int)area[0], (int)(area[3] - area[1]), (int)(area[2] - area[0]));
            }
            return img;
        }

        private static double ReadSample(byte[] b, long p, int bps, int fmt, bool big)
        {
            switch (bps)
            {
                case 8: return b[p];
                case 16:
                    {
                        int v = big ? (b[p] << 8) | b[p + 1] : b[p] | (b[p + 1] << 8);
                        return fmt == 2 ? (short)v : v;
                    }
                case 32:
                    {
                        var t = new byte[4];
                        for (int k = 0; k < 4; k++) t[k] = big ? b[p + 3 - k] : b[p + k];
                        if (fmt == 3) return BitConverter.ToSingle(t, 0);
                        return fmt == 2 ? (double)BitConverter.ToInt32(t, 0) : (double)BitConverter.ToUInt32(t, 0);
                    }
                case 64:
                    {
                        var t = new byte[8];
                        for (int k = 0; k < 8; k++) t[k] = big ? b[p + 7 - k] : b[p + k];
                        return BitConverter.ToDouble(t, 0);
                    }
                default: return b[p];
            }
        }

        private static byte[] Decompress(byte[] d, long off, long cnt, int comp, int expected, int bps)
        {
            if (comp == 1)
            {
                var o = new byte[Math.Min(cnt, d.Length - off)];
                Array.Copy(d, off, o, 0, o.Length);
                if (bps == 12 || bps == 14 || bps == 10) return Unpack(o, bps, expected / 2);
                return o;
            }
            if (comp == 5) return LzwDecode(d, (int)off, (int)cnt, expected);
            if (comp == 8 || comp == 32946)
            {
                using (var ms = new MemoryStream(d, (int)off + 2, (int)cnt - 2))
                using (var ds = new DeflateStream(ms, CompressionMode.Decompress))
                {
                    var o = new byte[expected];
                    int r = 0;
                    while (r < expected) { int k = ds.Read(o, r, expected - r); if (k <= 0) break; r += k; }
                    return o;
                }
            }
            if (comp == 32773)
            {
                var o = new byte[expected];
                int i = (int)off, end = (int)(off + cnt), p = 0;
                while (i < end && p < expected)
                {
                    sbyte n = (sbyte)d[i++];
                    if (n >= 0) { int len = n + 1; Array.Copy(d, i, o, p, Math.Min(len, expected - p)); i += len; p += len; }
                    else if (n != -128) { int len = 1 - n; byte v = d[i++]; for (int k = 0; k < len && p < expected; k++) o[p++] = v; }
                }
                return o;
            }
            throw new NotSupportedException("TIFF compression " + comp + " not supported");
        }

        /// <summary>Unpack big-endian packed n-bit samples into 16-bit little-endian.</summary>
        private static byte[] Unpack(byte[] src, int bits, int samples)
        {
            var o = new byte[samples * 2];
            long bitPos = 0;
            for (int i = 0; i < samples; i++)
            {
                int v = 0;
                for (int k = 0; k < bits; k++)
                {
                    long byteIdx = bitPos >> 3;
                    if (byteIdx >= src.Length) break;
                    int bit = (src[byteIdx] >> (7 - (int)(bitPos & 7))) & 1;
                    v = (v << 1) | bit;
                    bitPos++;
                }
                o[2 * i] = (byte)v; o[2 * i + 1] = (byte)(v >> 8);
            }
            return o;
        }

        private static byte[] LzwDecode(byte[] d, int off, int cnt, int expected)
        {
            var output = new byte[expected];
            int op = 0;
            var dict = new byte[4096][];
            for (int i = 0; i < 256; i++) dict[i] = new[] { (byte)i };
            int next = 258, codeLen = 9;
            long bitPos = (long)off * 8, end = (long)(off + cnt) * 8;
            byte[] prev = null;
            while (bitPos + codeLen <= end && op < expected)
            {
                int code = 0;
                for (int k = 0; k < codeLen; k++)
                {
                    long bi = bitPos + k;
                    code = (code << 1) | ((d[bi >> 3] >> (7 - (int)(bi & 7))) & 1);
                }
                bitPos += codeLen;
                if (code == 257) break;
                if (code == 256)
                {
                    next = 258; codeLen = 9; prev = null;
                    continue;
                }
                byte[] entry;
                if (code < next && dict[code] != null) entry = dict[code];
                else if (prev != null) { entry = new byte[prev.Length + 1]; Array.Copy(prev, entry, prev.Length); entry[prev.Length] = prev[0]; }
                else break;
                int n = Math.Min(entry.Length, expected - op);
                Array.Copy(entry, 0, output, op, n);
                op += n;
                if (prev != null && next < 4096)
                {
                    var ne = new byte[prev.Length + 1];
                    Array.Copy(prev, ne, prev.Length);
                    ne[prev.Length] = entry[0];
                    dict[next++] = ne;
                }
                prev = entry;
                if (next + 1 >= (1 << codeLen) && codeLen < 12) codeLen++;
            }
            return output;
        }

        private static void UndoHorizontalPredictor(byte[] b, int w, int h, int spp, int bytes, bool big)
        {
            for (int y = 0; y < h; y++)
            {
                long row = (long)y * w * spp * bytes;
                for (int x = 1; x < w; x++)
                    for (int s = 0; s < spp; s++)
                    {
                        long p = row + ((long)x * spp + s) * bytes, q = p - spp * bytes;
                        if (p + bytes > b.Length) return;
                        if (bytes == 1) b[p] = (byte)(b[p] + b[q]);
                        else if (bytes == 2)
                        {
                            int a = big ? (b[p] << 8) | b[p + 1] : b[p] | (b[p + 1] << 8);
                            int c = big ? (b[q] << 8) | b[q + 1] : b[q] | (b[q + 1] << 8);
                            int v = (a + c) & 0xFFFF;
                            if (big) { b[p] = (byte)(v >> 8); b[p + 1] = (byte)v; } else { b[p] = (byte)v; b[p + 1] = (byte)(v >> 8); }
                        }
                    }
            }
        }

        private static byte[] UndoFloatPredictor(byte[] b, int w, int h, int spp, int bytes, bool big)
        {
            var o = new byte[b.Length];
            int rowBytes = w * spp * bytes;
            for (int y = 0; y < h; y++)
            {
                int r0 = y * rowBytes;
                for (int i = 1; i < rowBytes; i++) b[r0 + i] = (byte)(b[r0 + i] + b[r0 + i - spp]);
                int n = w * spp;
                for (int i = 0; i < n; i++)
                    for (int k = 0; k < bytes; k++)
                        o[r0 + i * bytes + (big ? k : bytes - 1 - k)] = b[r0 + k * n + i]; // byte planes are stored most-significant first
            }
            return o;
        }

        private static AstroImage ReadCr2(Reader r, List<Ifd> ifds, string path)
        {
            // The raw image is in IFD #3 (index 3) with old-style JPEG (compression 6) lossless data
            Ifd raw = ifds.Count > 3 ? ifds[3] : ifds.Last();
            long off = raw.Get(273), cnt = raw.Get(279);
            var lj = LosslessJpeg.Decode(r.D, (int)off, (int)cnt);
            int fullW = lj.Width * lj.Components, fullH = lj.Height;
            var slices = raw.GetArr(0xC640);
            var img = new AstroImage(fullW, fullH, 1) { OriginalBitDepth = lj.Precision, SourcePath = path };
            var dst = img.Data[0];
            double maxV = (1 << lj.Precision) - 1;
            if (slices != null && slices.Length == 3 && slices[0] > 0)
            {
                int n = (int)slices[0], sw = (int)slices[1], lw = (int)slices[2];
                int idx = 0;
                for (int s = 0; s <= n; s++)
                {
                    int width = s < n ? sw : lw, x0 = s * sw;
                    for (int y = 0; y < fullH; y++)
                        for (int x = 0; x < width; x++)
                        {
                            if (idx >= lj.Samples.Length) break;
                            dst[y * fullW + x0 + x] = (float)(lj.Samples[idx++] / maxV);
                        }
                }
            }
            else for (int i = 0; i < Math.Min(dst.Length, lj.Samples.Length); i++) dst[i] = (float)(lj.Samples[i] / maxV);
            // subtract an estimate of the black level from the left masked border (first 40 columns typically)
            int border = Math.Min(40, fullW / 20);
            if (border > 4)
            {
                double s = 0; long nb = 0;
                for (int y = 0; y < fullH; y += 4) for (int x = 2; x < border - 2; x++) { s += dst[y * fullW + x]; nb++; }
                float bl = (float)(s / Math.Max(1, nb));
                for (int i = 0; i < dst.Length; i++) dst[i] = Math.Max(0, (dst[i] - bl) / (1 - bl));
                img = Geometry.Crop(img, border, 0, fullW - border, fullH);
            }
            img.Bayer = BayerPattern.RGGB;
            ApplyExif(ifds, img);
            img.Header.Set("INSTRUME", "Canon DSLR");
            return img;
        }

        private static List<Ifd> EmbeddedIfds(byte[] tiff)
        {
            if (tiff == null || tiff.Length < 8 || (tiff[0] != 'I' && tiff[0] != 'M')) return new List<Ifd>();
            var r = new Reader { D = tiff, Big = tiff[0] == 'M' };
            return ParseIfds(r, r.U32(4));
        }

        /// <summary>A numeric tag of the first IFD of a TIFF block embedded in another format (a CR3's maker notes), or null.</summary>
        internal static long[] FirstIfdTag(byte[] tiff, int tag)
        {
            var ifds = EmbeddedIfds(tiff);
            return ifds.Count > 0 ? ifds[0].GetArr(tag) : null;
        }

        /// <summary>Camera, date, exposure and ISO from TIFF blocks embedded in another format (a CR3's CMT1 and CMT2).</summary>
        internal static void ApplyExif(AstroImage img, params byte[][] tiffs) => ApplyExif(tiffs.SelectMany(EmbeddedIfds).ToList(), img);

        private static void ApplyExif(List<Ifd> ifds, AstroImage img)
        {
            foreach (var ifd in ifds)
            {
                string make, model, dt;
                if (ifd.Strings.TryGetValue(271, out make) || ifd.Strings.TryGetValue(272, out model))
                {
                    string m = ifd.Strings.ContainsKey(272) ? ifd.Strings[272] : "";
                    string mk = ifd.Strings.ContainsKey(271) ? ifd.Strings[271].Trim() : "";
                    // Canon and others repeat the make in the model ("Canon" + "Canon EOS Ra")
                    if (!img.Header.Has("INSTRUME")) img.Header.Set("INSTRUME", m.StartsWith(mk, StringComparison.OrdinalIgnoreCase) ? m.Trim() : (mk + " " + m).Trim());
                }
                if (ifd.Strings.TryGetValue(306, out dt))
                {
                    DateTime d;
                    if (DateTime.TryParseExact(dt, "yyyy:MM:dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeLocal, out d))
                        img.DateObs = d.ToUniversalTime();
                }
                double[] et;
                if (ifd.Rationals.TryGetValue(33434, out et) && et.Length > 0) img.ExposureSeconds = et[0];
                long iso = ifd.Get(34855, 0);
                if (iso > 0) img.Header.Set("ISOSPEED", iso);
                // EXIF sub-IFD pointer (34665) is parsed lazily: not needed for most astro use
            }
        }

        /// <summary>Write an uncompressed TIFF (16-bit integer or 32-bit IEEE float).</summary>
        public static void Write(string path, AstroImage img, bool float32 = false)
        {
            int w = img.Width, h = img.Height, ch = img.Channels >= 3 ? 3 : 1;
            int bps = float32 ? 32 : 16, bytes = bps / 8;
            long dataLen = (long)w * h * ch * bytes;
            using (var fs = File.Create(path))
            using (var bw = new BinaryWriter(fs))
            {
                bw.Write((byte)'I'); bw.Write((byte)'I'); bw.Write((ushort)42);
                bw.Write((uint)8);
                var tags = new List<Tuple<ushort, ushort, uint, uint>>();
                int ntags = 12;
                long ifdSize = 2 + ntags * 12 + 4;
                long extra = 8 + ifdSize; // after header + IFD
                long bpsOff = extra; extra += ch > 1 ? ch * 2 : 0;
                long sfOff = extra; extra += ch > 1 ? ch * 2 : 0;
                long xresOff = extra; extra += 8;
                long yresOff = extra; extra += 8;
                long dataOff = extra;
                Action<ushort, ushort, uint, uint> T = (tag, type, count, value) => tags.Add(Tuple.Create(tag, type, count, value));
                T(256, 4, 1, (uint)w);
                T(257, 4, 1, (uint)h);
                T(258, 3, (uint)ch, ch > 1 ? (uint)bpsOff : (uint)bps);
                T(259, 3, 1, 1);
                T(262, 3, 1, ch > 1 ? 2u : 1u);
                T(273, 4, 1, (uint)dataOff);
                T(277, 3, 1, (uint)ch);
                T(278, 4, 1, (uint)h);
                T(279, 4, 1, (uint)dataLen);
                T(282, 5, 1, (uint)xresOff);
                T(283, 5, 1, (uint)yresOff);
                T(339, 3, (uint)ch, ch > 1 ? (uint)sfOff : (float32 ? 3u : 1u));
                bw.Write((ushort)tags.Count);
                foreach (var t in tags)
                {
                    bw.Write(t.Item1); bw.Write(t.Item2); bw.Write(t.Item3);
                    if (t.Item2 == 3 && t.Item3 == 1) { bw.Write((ushort)t.Item4); bw.Write((ushort)0); }
                    else bw.Write(t.Item4);
                }
                bw.Write((uint)0);
                if (ch > 1) { for (int i = 0; i < ch; i++) bw.Write((ushort)bps); for (int i = 0; i < ch; i++) bw.Write((ushort)(float32 ? 3 : 1)); }
                bw.Write((uint)72); bw.Write((uint)1); bw.Write((uint)72); bw.Write((uint)1);
                var row = new byte[w * ch * bytes];
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                        for (int c = 0; c < ch; c++)
                        {
                            float v = img.Data[c][y * w + x];
                            int p = (x * ch + c) * bytes;
                            if (float32) Buffer.BlockCopy(BitConverter.GetBytes(v), 0, row, p, 4);
                            else { int iv = (int)Math.Max(0, Math.Min(65535, Math.Round(v * 65535))); row[p] = (byte)iv; row[p + 1] = (byte)(iv >> 8); }
                        }
                    bw.Write(row);
                }
            }
        }
    }
}
