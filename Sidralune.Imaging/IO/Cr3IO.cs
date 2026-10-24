using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Sidralune.Imaging.IO
{
    /// <summary>
    /// Canon CR3 raw reader: the ISO base media container, the TIFF metadata blocks (CMT1-CMT3) and the CRX codec's
    /// lossless mode (full-size RAW). The compressed C-RAW mode (wavelet levels above 0) is reported as unsupported.
    /// Written from the published descriptions of the format (lclevy/canon_cr3, ExifTool's tag tables).
    /// </summary>
    public static class Cr3IO
    {
        /// <summary>Canon's metadata box inside moov (CNCV, CMT1-CMT4, THMB).</summary>
        private static readonly byte[] CanonUuid = { 0x85, 0xc0, 0xb6, 0x87, 0x82, 0x0f, 0x11, 0xe0, 0x81, 0x11, 0xf4, 0xce, 0x46, 0x2b, 0x6a, 0x48 };

        /// <summary>The CMP1 box of a raw track: how the CRX picture is laid out.</summary>
        public class CrxHeader
        {
            public int Version, Width, Height, TileWidth, TileHeight, Bits, Planes, Cfa, EncType, Levels, HeaderSize;
        }

        private class Track
        {
            public CrxHeader Crx;
            public long Offset, Size;
        }

        private struct Box
        {
            public string Type;
            public long Start, Content, End;
        }

        public static bool IsCr3(byte[] d) => d.Length > 12 && Encoding.ASCII.GetString(d, 4, 8) == "ftypcrx ";

        public static AstroImage Read(string path)
        {
            var img = Read(File.ReadAllBytes(path));
            img.SourcePath = path;
            return img;
        }

        public static AstroImage Read(byte[] d)
        {
            if (!IsCr3(d)) throw new InvalidDataException("Not a Canon CR3 file");
            byte[] cmt1 = null, cmt2 = null, cmt3 = null;
            var tracks = new List<Track>();
            foreach (var moov in Boxes(d, 0, d.Length).Where(b => b.Type == "moov"))
                foreach (var b in Boxes(d, moov.Content, moov.End))
                {
                    if (b.Type == "uuid" && b.Content + 16 <= b.End && Enumerable.Range(0, 16).All(i => d[b.Content + i] == CanonUuid[i]))
                        foreach (var m in Boxes(d, b.Content + 16, b.End))
                        {
                            if (m.Type == "CMT1") cmt1 = Slice(d, m.Content, m.End);
                            else if (m.Type == "CMT2") cmt2 = Slice(d, m.Content, m.End);
                            else if (m.Type == "CMT3") cmt3 = Slice(d, m.Content, m.End);
                        }
                    else if (b.Type == "trak")
                    {
                        var t = ReadTrack(d, b);
                        if (t != null) tracks.Add(t);
                    }
                }
            var raw = tracks.Where(t => t.Crx.Planes == 4).OrderByDescending(t => (long)t.Crx.Width * t.Crx.Height).FirstOrDefault();
            if (raw == null) throw new InvalidDataException("No raw image in the CR3 file");
            var h = raw.Crx;
            if (h.Levels > 0) throw new NotSupportedException("Compressed C-RAW files are not supported yet: shoot RAW, or convert the files to DNG with Adobe DNG Converter");
            if (h.EncType != 0) throw new NotSupportedException("This CR3 raw encoding (type " + h.EncType + ") is not supported");

            var px = DecodeCrx(d, raw.Offset, raw.Size, h);
            var img = new AstroImage(h.Width, h.Height, 1) { OriginalBitDepth = h.Bits };
            var dst = img.Data[0];
            float maxV = (1 << h.Bits) - 1;
            for (int i = 0; i < dst.Length; i++) dst[i] = px[i] / maxV;
            img.Bayer = new[] { BayerPattern.RGGB, BayerPattern.GRBG, BayerPattern.GBRG, BayerPattern.BGGR }[h.Cfa];

            // Canon SensorInfo (maker note 0xE0): the visible area inside the masked border
            img = CropToSensor(img, cmt3 != null ? TiffIO.FirstIfdTag(cmt3, 0xE0) : null, maxV);
            TiffIO.ApplyExif(img, cmt1, cmt2);
            if (!img.Header.Has("INSTRUME")) img.Header.Set("INSTRUME", "Canon camera");
            return img;
        }

        /// <summary>
        /// Crops a Canon raw frame to the visible area of its SensorInfo (left, top, right and bottom at 5-8, inclusive)
        /// after removing the black level measured in the masked columns to the left of it, as for CR2.
        /// </summary>
        internal static AstroImage CropToSensor(AstroImage img, long[] sensor, float maxV)
        {
            if (sensor == null || sensor.Length <= 8) return img;
            int left = (int)sensor[5], top = (int)sensor[6], right = (int)sensor[7], bottom = (int)sensor[8], w = img.Width;
            if (left < 0 || top < 0 || left >= right || top >= bottom || right >= w || bottom >= img.Height) return img;
            var dst = img.Data[0];
            if (left >= 8)
            {
                double s = 0; long n = 0;
                for (int y = top; y <= bottom; y++) for (int x = 2; x < left - 2; x++) { s += dst[y * w + x]; n++; }
                float bl = (float)(s / n);
                for (int i = 0; i < dst.Length; i++) dst[i] = Math.Max(0, (dst[i] - bl) / (1 - bl));
                img.Header.Set("BLKLEVEL", Math.Round(bl * maxV, 1));
            }
            return Geometry.Crop(img, left, top, right - left + 1, bottom - top + 1);
        }

        // ------------------------------------------------------------------ container

        private static uint U32(byte[] d, long o) => (uint)((d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3]);
        private static int U16(byte[] d, long o) => (d[o] << 8) | d[o + 1];

        private static byte[] Slice(byte[] d, long from, long to)
        {
            var b = new byte[to - from];
            Array.Copy(d, from, b, 0, b.Length);
            return b;
        }

        /// <summary>The boxes between from and to (size, type, then a 64-bit size when size is 1; size 0 runs to the end).</summary>
        private static IEnumerable<Box> Boxes(byte[] d, long from, long to)
        {
            long p = from;
            while (p + 8 <= to)
            {
                long size = U32(d, p), content = p + 8;
                if (size == 1)
                {
                    if (p + 16 > to) yield break;
                    size = ((long)U32(d, p + 8) << 32) | U32(d, p + 12);
                    content = p + 16;
                }
                else if (size == 0) size = to - p;
                if (size < content - p || p + size > to) yield break;
                yield return new Box { Type = Encoding.ASCII.GetString(d, (int)p + 4, 4), Start = p, Content = content, End = p + size };
                p += size;
            }
        }

        private static Box? Child(byte[] d, Box parent, string type, long skip = 0)
        {
            foreach (var b in Boxes(d, parent.Content + skip, parent.End)) if (b.Type == type) return b;
            return null;
        }

        /// <summary>A track whose sample description is a CRAW entry with a CMP1 box, with its data position in mdat.</summary>
        private static Track ReadTrack(byte[] d, Box trak)
        {
            var stbl = Child(d, trak, "mdia");
            if (stbl != null) stbl = Child(d, stbl.Value, "minf");
            if (stbl != null) stbl = Child(d, stbl.Value, "stbl");
            if (stbl == null) return null;
            var stsd = Child(d, stbl.Value, "stsd");
            var stsz = Child(d, stbl.Value, "stsz");
            var co64 = Child(d, stbl.Value, "co64");
            var stco = Child(d, stbl.Value, "stco");
            if (stsd == null || stsz == null || (co64 == null && stco == null)) return null;
            var craw = Child(d, stsd.Value, "CRAW", 8); // version, flags and entry count come first
            if (craw == null) return null;
            // the CRAW entry has a fixed-size image description before its boxes; find CMP1 among them
            Box? cmp1 = null;
            for (long p = craw.Value.Content; p + 8 <= craw.Value.End && cmp1 == null; p++)
                if (d[p + 4] == 'C' && d[p + 5] == 'M' && d[p + 6] == 'P' && d[p + 7] == '1')
                {
                    long size = U32(d, p);
                    if (size >= 8 + 32 && p + size <= craw.Value.End) cmp1 = new Box { Type = "CMP1", Start = p, Content = p + 8, End = p + size };
                }
            if (cmp1 == null) return null;
            long c = cmp1.Value.Content;
            var h = new CrxHeader
            {
                Version = U16(d, c + 4),
                Width = (int)U32(d, c + 8),
                Height = (int)U32(d, c + 12),
                TileWidth = (int)U32(d, c + 16),
                TileHeight = (int)U32(d, c + 20),
                Bits = d[c + 24],
                Planes = d[c + 25] >> 4,
                Cfa = d[c + 25] & 15,
                EncType = d[c + 26] >> 4,
                Levels = d[c + 26] & 15,
                HeaderSize = (int)U32(d, c + 28),
            };
            if (h.Version != 0x100 && h.Version != 0x200) return null;
            if (h.Planes == 4 && (h.Width <= 0 || h.Height <= 0 || h.TileWidth <= 0 || h.TileHeight <= 0 || ((h.Width | h.Height | h.TileWidth | h.TileHeight) & 1) != 0
                || h.Cfa > 3 || h.Bits < 9 || h.Bits > 16 || h.Levels > 3 || h.HeaderSize <= 0))
                throw new InvalidDataException("Damaged CR3 raw description (CMP1)");
            long s0 = stsz.Value.Content;
            long size0 = U32(d, s0 + 4);
            if (size0 == 0) size0 = U32(d, s0 + 12); // no common size: the first entry of the table
            long off = co64 != null ? ((long)U32(d, co64.Value.Content + 8) << 32) | U32(d, co64.Value.Content + 12) : U32(d, stco.Value.Content + 8);
            if (off <= 0 || off + size0 > d.Length) throw new InvalidDataException("CR3 raw data lies outside the file");
            return new Track { Crx = h, Offset = off, Size = size0 };
        }

        // ------------------------------------------------------------------ CRX headers

        private class Plane
        {
            public long Offset, Size;
        }

        /// <summary>Decodes a lossless CRX picture into Width x Height raw values (CFA order as in the header).</summary>
        public static ushort[] DecodeCrx(byte[] d, long offset, long size, CrxHeader h)
        {
            int pw = h.Width / 2, ph = h.Height / 2, tw = h.TileWidth / 2, th = h.TileHeight / 2;
            int cols = (pw + tw - 1) / tw, rows = (ph + th - 1) / th, bands = 3 * h.Levels + 1;
            long p = offset, hdrEnd = offset + h.HeaderSize, data = hdrEnd, end = offset + size;
            Func<int, int, bool> header = (sign, len) =>
            {
                if (p + 4 + len > hdrEnd) throw new InvalidDataException("CR3 raw headers end early");
                if (U16(d, p) == 0xFF11 || U16(d, p) == 0xFF12 || U16(d, p) == 0xFF13)
                    throw new NotSupportedException("CR3 files from this camera generation (CRX version 2) are not supported yet: convert them to DNG with Adobe DNG Converter");
                return U16(d, p) == sign && U16(d, p + 2) == len;
            };
            var planes = new Plane[rows * cols, h.Planes];
            for (int t = 0; t < rows * cols; t++)
            {
                if (!header(0xFF01, 8)) throw new InvalidDataException("Damaged CR3 tile header");
                long tileSize = U32(d, p + 4);
                p += 12;
                long planeData = data;
                for (int c = 0; c < h.Planes; c++)
                {
                    if (!header(0xFF02, 8) || d[p + 8] >> 4 != c) throw new InvalidDataException("Damaged CR3 plane header");
                    // flags: the line above is referenced (all lossless files so far), and residuals rounded to a step
                    bool partial = (d[p + 8] & 8) != 0;
                    int rounded = (d[p + 8] >> 1) & 3;
                    var pl = new Plane { Offset = planeData, Size = U32(d, p + 4) };
                    planeData += pl.Size;
                    p += 12;
                    for (int b = 0; b < bands; b++)
                    {
                        if (!header(0xFF03, 8) || d[p + 8] >> 4 != b) throw new InvalidDataException("Damaged CR3 subband header");
                        p += 12;
                    }
                    if (!partial || rounded != 0) throw new NotSupportedException("This CR3 raw coding (" + (partial ? "rounded residuals" : "planes without line references") + ") is not supported yet: convert the files to DNG with Adobe DNG Converter");
                    if (pl.Offset + pl.Size > end) throw new InvalidDataException("CR3 raw data ends early");
                    planes[t, c] = pl;
                }
                data += tileSize;
            }

            var raw = new ushort[h.Width * h.Height];
            try
            {
                Parallel.For(0, rows * cols * h.Planes, job => DecodePlane(d, h, planes, raw, job));
            }
            catch (AggregateException e)
            {
                // report what went wrong in a plane, not that a parallel loop failed
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerExceptions[0]).Throw();
            }
            return raw;
        }

        private static void DecodePlane(byte[] d, CrxHeader h, Plane[,] planes, ushort[] raw, int job)
        {
            int pw = h.Width / 2, ph = h.Height / 2, tw = h.TileWidth / 2, th = h.TileHeight / 2, cols = (pw + tw - 1) / tw;
            int median = 1 << (h.Bits - 1), maxV = (1 << h.Bits) - 1;
            int t = job / h.Planes, c = job % h.Planes;
            int x0 = t % cols * tw, y0 = t / cols * th, w = Math.Min(tw, pw - x0), hh = Math.Min(th, ph - y0);
            // plane 0 is red, 1 the green beside it, 2 the green below it, 3 blue; the CFA code mirrors the 2x2 cell
            int dx = (c & 1) ^ (h.Cfa & 1), dy = (c >> 1) ^ (h.Cfa >> 1);
            var pl = planes[t, c];
            var bits = new BitReader(d, pl.Offset, pl.Size);
            var band = new LineCoder(bits, w);
            for (int y = 0; y < hh; y++)
            {
                var line = band.Next();
                int o = (2 * (y0 + y) + dy) * h.Width + 2 * x0 + dx;
                for (int i = 0; i < w; i++)
                {
                    int v = median + line[i + 1];
                    raw[o + 2 * i] = (ushort)(v < 0 ? 0 : v > maxV ? maxV : v);
                }
            }
            // Canon's planes end within a few bytes of their stated size; anything else means the decoder lost step
            if (bits.Unused < 0 || bits.Unused > 16) throw new InvalidDataException("Damaged CR3 raw data (a plane does not end where its header says)");
        }

        // ------------------------------------------------------------------ CRX lossless line coding

        /// <summary>MSB-first bit reader; reads past the end as zeros, and fails when a code runs far beyond it.</summary>
        private sealed class BitReader
        {
            private static readonly byte[] Lz = Enumerable.Range(0, 256).Select(b => { int n = 8; for (; b != 0; b >>= 1) n--; return (byte)n; }).ToArray();
            private readonly byte[] _d;
            private readonly long _end;
            private long _pos;
            private ulong _buf;
            private int _n;

            public BitReader(byte[] d, long start, long length)
            {
                _d = d;
                _pos = start;
                _end = Math.Min(d.Length, start + length);
            }

            private void Fill()
            {
                while (_n <= 56)
                {
                    ulong b = _pos < _end ? _d[_pos] : 0UL;
                    _pos++;
                    _buf |= b << (56 - _n);
                    _n += 8;
                }
            }

            public uint Bits(int k)
            {
                if (k == 0) return 0;
                if (_n < k) Fill();
                uint v = (uint)(_buf >> (64 - k));
                _buf <<= k;
                _n -= k;
                return v;
            }

            /// <summary>Bytes of the data not reached yet (negative when the codes ran past its end).</summary>
            public long Unused => _end - (_pos - _n / 8);

            /// <summary>Counts zero bits up to the next one bit, which is consumed as well.</summary>
            public int Zeros()
            {
                int z = 0;
                while (true)
                {
                    if (_buf != 0)
                    {
                        int lz = 0;
                        while ((_buf >> 56) == 0) { _buf <<= 8; lz += 8; }
                        lz += Lz[_buf >> 56];
                        z += lz;
                        _buf <<= lz + 1 - (lz / 8) * 8; // the whole bytes were shifted out above
                        _n -= lz + 1;
                        return z;
                    }
                    z += _n;
                    _n = 0;
                    if (_pos > _end + 16) throw new InvalidDataException("CR3 raw data ends early");
                    Fill();
                }
            }
        }

        /// <summary>
        /// One plane of a lossless CRX picture, a line at a time: median edge prediction from the line above, adaptive
        /// Golomb-Rice residuals, and runs of the left value coded with the JPEG-LS run-length tables. Lines hold a
        /// padding value on each side (index 0 and width + 1).
        /// </summary>
        private sealed class LineCoder
        {
            private static readonly int[] J = { 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 9, 10, 11, 12, 13, 14, 15 };
            private readonly BitReader _bits;
            private readonly int _w;
            private int[] _prev, _cur;
            private int _k, _s, _line;

            public LineCoder(BitReader bits, int width)
            {
                _bits = bits;
                _w = width;
                _prev = new int[width + 2];
                _cur = new int[width + 2];
            }

            /// <summary>The next line, with the samples at 1..width.</summary>
            public int[] Next()
            {
                var t = _prev; _prev = _cur; _cur = t;
                if (_line++ == 0) Top();
                else Line();
                return _cur;
            }

            private static int Predict(int k, int code)
            {
                int q = code >> k;
                k += (q > 2 ? 1 : 0) + (q > 5 ? 1 : 0) - (code < (1 << k >> 1) ? 1 : 0);
                return k < 15 ? k : 15;
            }

            private int Code()
            {
                int z = _bits.Zeros();
                if (z >= 41) return (int)_bits.Bits(21);
                return _k > 0 ? (z << _k) | (int)_bits.Bits(_k) : z;
            }

            private static int Signed(int code) => -(code & 1) ^ (code >> 1);

            private static int Med(int a, int b, int c)
            {
                int mx = a > b ? a : b, mn = a > b ? b : a;
                return c >= mx ? mn : c <= mn ? mx : a + b - c;
            }

            /// <summary>Run length after a set run bit: unary steps of the run table, then the remainder in J[s] bits.</summary>
            private int Run(int remaining)
            {
                int n = 1;
                while (_bits.Bits(1) != 0)
                {
                    n += 1 << J[_s];
                    if (n > remaining) return remaining;
                    if (_s < 31) _s++;
                    if (n == remaining) return n;
                }
                if (n == remaining) return n;
                n += (int)_bits.Bits(J[_s]);
                if (_s > 0) _s--;
                if (n > remaining) throw new InvalidDataException("Damaged CR3 raw data (run past the end of a line)");
                return n;
            }

            private void Top()
            {
                var c = _cur;
                c[0] = 0;
                int x = 1, left = _w;
                for (; left > 1; left--, x++)
                {
                    if (c[x - 1] != 0) c[x] = c[x - 1];
                    else
                    {
                        if (_bits.Bits(1) != 0)
                        {
                            int n = Run(left);
                            for (int i = 0; i < n; i++, x++) c[x] = c[x - 1];
                            left -= n;
                            if (left <= 0) break;
                        }
                        c[x] = 0;
                    }
                    int code = Code();
                    c[x] += Signed(code);
                    _k = Predict(_k, code);
                }
                if (left == 1)
                {
                    int code = Code();
                    c[x] = c[x - 1] + Signed(code);
                    _k = Predict(_k, code);
                    x++;
                }
                c[x] = c[x - 1] + 1;
            }

            private void Line()
            {
                int[] c = _cur, p = _prev;
                c[0] = p[1];
                int x = 1, left = _w;
                for (; left > 1; left--)
                {
                    if (c[x - 1] != p[x] || c[x - 1] != p[x + 1])
                    {
                        Symbol(x, Med(c[x - 1], p[x], p[x - 1]), true);
                        x++;
                    }
                    else
                    {
                        if (_bits.Bits(1) != 0)
                        {
                            int n = Run(left);
                            for (int i = 0; i < n; i++, x++) c[x] = c[x - 1];
                            left -= n;
                            if (left <= 0) break;
                        }
                        Symbol(x, p[x], true);
                        x++;
                    }
                }
                if (left == 1)
                {
                    Symbol(x, Med(c[x - 1], p[x], p[x - 1]), false);
                    x++;
                }
                c[x] = c[x - 1] + 1;
            }

            /// <summary>A predicted sample plus its residual; inside a line the next step of the line above refines k.</summary>
            private void Symbol(int x, int predicted, bool more)
            {
                int code = Code();
                _cur[x] = predicted + Signed(code);
                if (more) code = (code + Math.Abs((_prev[x + 1] - _prev[x]) << 1)) >> 1;
                _k = Predict(_k, code);
            }
        }
    }
}
