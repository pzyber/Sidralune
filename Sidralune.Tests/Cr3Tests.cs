using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sidralune.Imaging;
using Sidralune.Imaging.IO;
using static Sidralune.Tests.Checks;

namespace Sidralune.Tests
{
    /// <summary>Canon CR3: synthetic files from a minimal CR3 writer, and real camera files when TestData\Cr3 holds some.</summary>
    [TestClass]
    public class Cr3Tests : TestBase
    {
        private static readonly int[] J = { 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 9, 10, 11, 12, 13, 14, 15 };

        private class BitSink
        {
            public readonly List<byte> Bytes = new List<byte>();
            private int _acc, _n;
            public void Put(long value, int bits)
            {
                for (int i = bits - 1; i >= 0; i--)
                {
                    _acc = (_acc << 1) | (int)((value >> i) & 1);
                    if (++_n == 8) { Bytes.Add((byte)_acc); _acc = 0; _n = 0; }
                }
            }
            public void Zeros(int n) { for (int i = 0; i < n; i++) Put(0, 1); }
            public byte[] Done() { while (_n != 0) Put(0, 1); return Bytes.ToArray(); }
        }

        /// <summary>The encoder side of the CRX lossless line coding, step for step with the decoder's choices.</summary>
        private class CrxPlaneWriter
        {
            private readonly BitSink _o = new BitSink();
            private readonly int _w;
            private int[] _prev, _cur;
            private int _k, _s, _line;
            public int Escapes, Runs;

            public CrxPlaneWriter(int w) { _w = w; _prev = new int[w + 2]; _cur = new int[w + 2]; }

            public byte[] Done() => _o.Done();

            private static int Predict(int k, int code)
            {
                int q = code >> k;
                k += (q > 2 ? 1 : 0) + (q > 5 ? 1 : 0) - (code < (1 << k >> 1) ? 1 : 0);
                return Math.Min(k, 15);
            }

            private static int Med(int a, int b, int c) => c >= Math.Max(a, b) ? Math.Min(a, b) : c <= Math.Min(a, b) ? Math.Max(a, b) : a + b - c;

            private void Code(int code)
            {
                if (code >> _k >= 41) { _o.Zeros(41); _o.Put(1, 1); _o.Put(code, 21); Escapes++; }
                else { _o.Zeros(code >> _k); _o.Put(1, 1); _o.Put(code & ((1 << _k) - 1), _k); }
            }

            private void Residual(int x, int predicted, bool more)
            {
                int e = _cur[x] - predicted, code = e >= 0 ? 2 * e : -2 * e - 1;
                Code(code);
                if (more) code = (code + Math.Abs((_prev[x + 1] - _prev[x]) << 1)) >> 1;
                _k = Predict(_k, code);
            }

            /// <summary>Codes a run of r copies of the left value: up to the end of the line, or followed by another value.</summary>
            private void Run(int r, int remaining)
            {
                Runs++;
                _o.Put(1, 1);
                int n = 1;
                if (r == remaining)
                    while (n < remaining)
                    {
                        _o.Put(1, 1);
                        n += 1 << J[_s];
                        if (n > remaining) return;
                        if (_s < 31) _s++;
                    }
                else
                {
                    while (n + (1 << J[_s]) <= r) { _o.Put(1, 1); n += 1 << J[_s]; if (_s < 31) _s++; }
                    _o.Put(0, 1);
                    _o.Put(r - n, J[_s]);
                    if (_s > 0) _s--;
                }
            }

            public void Line(int[] values)
            {
                var t = _prev; _prev = _cur; _cur = t;
                int[] c = _cur, p = _prev;
                Array.Copy(values, 0, c, 1, _w);
                int x = 1, left = _w;
                bool top = _line++ == 0;
                c[0] = top ? 0 : p[1];
                for (; left > 1; left--)
                {
                    bool run = top ? c[x - 1] == 0 : c[x - 1] == p[x] && c[x - 1] == p[x + 1];
                    if (!run)
                    {
                        if (top) { int e = c[x] - c[x - 1], code = e >= 0 ? 2 * e : -2 * e - 1; Code(code); _k = Predict(_k, code); }
                        else Residual(x, Med(c[x - 1], p[x], p[x - 1]), true);
                        x++;
                        continue;
                    }
                    int r = 0;
                    while (r < left && c[x + r] == c[x - 1]) r++;
                    if (r == 0) _o.Put(0, 1);
                    else
                    {
                        Run(r, left);
                        x += r;
                        left -= r;
                        if (left <= 0) break;
                    }
                    if (top) { int code = c[x] >= 0 ? 2 * c[x] : -2 * c[x] - 1; Code(code); _k = Predict(_k, code); }
                    else Residual(x, p[x], true);
                    x++;
                }
                if (left == 1)
                {
                    if (top) { int e = c[x] - c[x - 1], code = e >= 0 ? 2 * e : -2 * e - 1; Code(code); _k = Predict(_k, code); }
                    else Residual(x, Med(c[x - 1], p[x], p[x - 1]), false);
                    x++;
                }
                c[x] = c[x - 1] + 1;
            }
        }

        // ------------------------------------------------------------------ CR3 writer

        private static byte[] Be32(long v) => new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };
        private static byte[] Be16(int v) => new[] { (byte)(v >> 8), (byte)v };

        private static byte[] Box(string type, params byte[][] content)
        {
            var body = content.SelectMany(b => b).ToArray();
            return Be32(8 + body.Length).Concat(Encoding.ASCII.GetBytes(type)).Concat(body).ToArray();
        }

        /// <summary>A little-endian TIFF block with one IFD: (tag, type, values) with ASCII, SHORT or RATIONAL values.</summary>
        private static byte[] Tiff(params Tuple<int, int, object>[] tags)
        {
            var head = new List<byte> { (byte)'I', (byte)'I', 42, 0, 8, 0, 0, 0 };
            var data = new List<byte>();
            int dataStart = 8 + 2 + tags.Length * 12 + 4;
            var ifd = new List<byte>(BitConverter.GetBytes((ushort)tags.Length));
            foreach (var t in tags.OrderBy(t => t.Item1))
            {
                byte[] v; int count;
                if (t.Item2 == 2) { v = Encoding.ASCII.GetBytes((string)t.Item3 + "\0"); count = v.Length; }
                else if (t.Item2 == 3) { var s = (int[])t.Item3; v = s.SelectMany(x => BitConverter.GetBytes((ushort)x)).ToArray(); count = s.Length; }
                else { var r = (uint[])t.Item3; v = r.SelectMany(BitConverter.GetBytes).ToArray(); count = r.Length / 2; }
                ifd.AddRange(BitConverter.GetBytes((ushort)t.Item1));
                ifd.AddRange(BitConverter.GetBytes((ushort)t.Item2));
                ifd.AddRange(BitConverter.GetBytes(count));
                if (v.Length <= 4) ifd.AddRange(v.Concat(new byte[4 - v.Length]));
                else { ifd.AddRange(BitConverter.GetBytes(dataStart + data.Count)); data.AddRange(v); }
            }
            ifd.AddRange(new byte[4]);
            return head.Concat(ifd).Concat(data).ToArray();
        }

        private class Cr3Spec
        {
            public int Width = 236, Height = 164, TileWidth = 128, TileHeight = 96, Cfa, Levels, Bits = 14, PlaneFlags = 8;
            public int[] Sensor; // SensorInfo: left, top, right, bottom (inclusive)
            public int Escapes, Runs;
        }

        /// <summary>The CRX picture data: tile, plane and subband headers, then the coded planes.</summary>
        private static byte[] Crx(ushort[] raw, Cr3Spec s, out int headerSize)
        {
            int pw = s.Width / 2, ph = s.Height / 2, tw = s.TileWidth / 2, th = s.TileHeight / 2, cols = (pw + tw - 1) / tw, rows = (ph + th - 1) / th;
            var head = new List<byte>();
            var body = new List<byte>();
            int median = 1 << (s.Bits - 1);
            for (int t = 0; t < rows * cols; t++)
            {
                int x0 = t % cols * tw, y0 = t / cols * th, w = Math.Min(tw, pw - x0), h = Math.Min(th, ph - y0);
                var planes = new byte[4][];
                for (int c = 0; c < 4; c++)
                {
                    int dx = (c & 1) ^ (s.Cfa & 1), dy = (c >> 1) ^ (s.Cfa >> 1);
                    var wr = new CrxPlaneWriter(w);
                    for (int y = 0; y < h; y++)
                        wr.Line(Enumerable.Range(0, w).Select(i => raw[(2 * (y0 + y) + dy) * s.Width + 2 * (x0 + i) + dx] - median).ToArray());
                    planes[c] = wr.Done();
                    s.Escapes += wr.Escapes;
                    s.Runs += wr.Runs;
                }
                head.AddRange(Be16(0xFF01).Concat(Be16(8)).Concat(Be32(planes.Sum(p => p.Length))).Concat(Be16(t)).Concat(Be16(0)));
                for (int c = 0; c < 4; c++)
                {
                    head.AddRange(Be16(0xFF02).Concat(Be16(8)).Concat(Be32(planes[c].Length)).Concat(new byte[] { (byte)(c << 4 | s.PlaneFlags), 0, 0, 0 }));
                    head.AddRange(Be16(0xFF03).Concat(Be16(8)).Concat(Be32(planes[c].Length)).Concat(Be32(0x00200000)));
                    body.AddRange(planes[c]);
                }
            }
            headerSize = head.Count;
            return head.Concat(body).ToArray();
        }

        private static byte[] Track(int width, int height, Cr3Spec s, int headerSize, long offset, long size)
        {
            var cmp1 = new byte[60];
            Be32(0xFF000030).CopyTo(cmp1, 0);
            Be16(0x100).CopyTo(cmp1, 4);
            Be32(width).CopyTo(cmp1, 8);
            Be32(height).CopyTo(cmp1, 12);
            Be32(Math.Min(s.TileWidth, width)).CopyTo(cmp1, 16);
            Be32(Math.Min(s.TileHeight, height)).CopyTo(cmp1, 20);
            cmp1[24] = (byte)s.Bits;
            cmp1[25] = (byte)(4 << 4 | s.Cfa);
            cmp1[26] = (byte)s.Levels;
            Be32(headerSize).CopyTo(cmp1, 28);
            var desc = new byte[82];
            Be16(width).CopyTo(desc, 24);
            Be16(height).CopyTo(desc, 26);
            var craw = Box("CRAW", desc, Box("CMP1", cmp1), Box("free", new byte[4]));
            var stsd = Box("stsd", new byte[] { 0, 0, 0, 0, 0, 0, 0, 1 }, craw);
            var stsz = Box("stsz", new byte[4], Be32(size), Be32(1));
            var co64 = Box("co64", new byte[4], Be32(1), Be32(offset >> 32), Be32(offset));
            return Box("trak", Box("tkhd", new byte[84]), Box("mdia", Box("minf", Box("stbl", stsd, stsz, co64))));
        }

        /// <summary>A CR3 file holding raw (Width x Height, CFA order) with Canon metadata and a smaller raw track before it.</summary>
        private static byte[] Cr3(ushort[] raw, Cr3Spec s)
        {
            int hdr;
            var crx = Crx(raw, s, out hdr);
            var uuid = "85c0b687820f11e08111f4ce462b6a48";
            var tags = new List<byte[]>
            {
                Box("CNCV", Encoding.ASCII.GetBytes("CanonCR3_001/00.09.00/00.00.00")),
                Box("CMT1", Tiff(Tuple.Create(271, 2, (object)"Canon"), Tuple.Create(272, 2, (object)"Canon EOS Ra"), Tuple.Create(306, 2, (object)"2022:03:12 21:38:01"))),
                Box("CMT2", Tiff(Tuple.Create(33434, 5, (object)new uint[] { 149, 10 }), Tuple.Create(34855, 3, (object)new[] { 6400 }))),
            };
            if (s.Sensor != null)
                tags.Add(Box("CMT3", Tiff(Tuple.Create(0xE0, 3, (object)new[] { 34, s.Width, s.Height, 0, 0, s.Sensor[0], s.Sensor[1], s.Sensor[2], s.Sensor[3], 0, 0, 0, 0, 0, 0, 0, 0 }))));
            var canon = Box("uuid", Enumerable.Range(0, 16).Select(i => Convert.ToByte(uuid.Substring(2 * i, 2), 16)).ToArray(), tags.SelectMany(b => b).ToArray());
            var ftyp = Box("ftyp", Encoding.ASCII.GetBytes("crx "), Be32(1), Encoding.ASCII.GetBytes("crx isom"));
            // the track offsets depend on the size of moov, which does not depend on them
            Func<long, byte[]> moov = mdat => Box("moov", canon,
                Track(s.Width / 2, s.Height / 2, s, hdr, mdat, crx.Length), // a smaller raw, as cameras store; the reader must take the full one
                Track(s.Width, s.Height, s, hdr, mdat, crx.Length));
            long dataAt = ftyp.Length + moov(0).Length + 8;
            return ftyp.Concat(moov(dataAt)).Concat(Box("mdat", crx)).ToArray();
        }

        /// <summary>Sky with stars, a flat masked border, runs of equal values, saturation and wild jumps (escape codes).</summary>
        private static ushort[] Scene(int w, int h, int seed)
        {
            var rnd = new Random(seed);
            var raw = new ushort[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    double v = 2048 + (x < 13 || y < 6 ? 0 : 400 + 3 * x + 2 * y) + rnd.Next(-6, 7);
                    if (y >= 30 && y < 40) v = 3000;                                   // flat band: runs to the end of lines
                    if (y >= 50 && y < 56 && x > 40 && x < 120) v = 3000 + (x / 9) * 7;  // short runs
                    if (y >= 70 && y < 74) v = (x * 7919 + y) % 3 == 0 ? 0 : 16383;     // jumps beyond the Rice range
                    raw[y * w + x] = (ushort)Math.Max(0, Math.Min(16383, v));
                }
            foreach (var star in new[] { new[] { 100, 120 }, new[] { 180, 60 }, new[] { 30, 140 } })
                for (int y = star[1] - 6; y <= star[1] + 6; y++)
                    for (int x = star[0] - 6; x <= star[0] + 6; x++)
                    {
                        double r2 = (x - star[0]) * (x - star[0]) + (y - star[1]) * (y - star[1]);
                        raw[y * w + x] = (ushort)Math.Min(16383, raw[y * w + x] + 30000 * Math.Exp(-r2 / 6));
                    }
            return raw;
        }

        [TestMethod]
        public void TestCr3Synthetic()
        {
            for (int cfa = 0; cfa < 4; cfa++)
            {
                var s = new Cr3Spec { Cfa = cfa };
                var raw = Scene(s.Width, s.Height, cfa);
                var file = Cr3(raw, s);
                int hdr;
                var crx = Crx(raw, new Cr3Spec { Cfa = cfa }, out hdr);
                var back = Cr3IO.DecodeCrx(crx, 0, crx.Length, new Cr3IO.CrxHeader { Width = s.Width, Height = s.Height, TileWidth = s.TileWidth, TileHeight = s.TileHeight, Bits = 14, Planes = 4, Cfa = cfa, HeaderSize = hdr });
                CheckTrue("CFA " + cfa + ": 2 x 2 tiles of uneven size decode exactly", back.SequenceEqual(raw));
                if (cfa == 0) CheckTrue("The scene uses runs and escape codes", s.Runs > 20 && s.Escapes > 10, s.Runs + " runs, " + s.Escapes + " escapes");
                var img = Cr3IO.Read(file);
                CheckTrue("CFA " + cfa + ": the full raw is chosen, without a sensor area nothing is cropped", img.Width == s.Width && img.Height == s.Height
                    && img.Bayer == new[] { BayerPattern.RGGB, BayerPattern.GRBG, BayerPattern.GBRG, BayerPattern.BGGR }[cfa]);
                Check("CFA " + cfa + ": values scaled to 0-1", img.Data[0][1000], raw[1000] / 16383.0, 1e-6);
            }

            // the sensor area: crop, black level from the masked columns, CFA shifted by an odd left border
            var spec = new Cr3Spec { Sensor = new[] { 13, 6, 233, 161 } };
            var scene = Scene(spec.Width, spec.Height, 7);
            var cr3 = Cr3(scene, spec);
            var im = Cr3IO.Read(cr3);
            CheckTrue("Cropped to the sensor area", im.Width == 221 && im.Height == 156, im.Width + " x " + im.Height);
            CheckTrue("An odd left border turns RGGB into GRBG", im.Bayer == BayerPattern.GRBG);
            double black = 0; int n = 0;
            for (int y = 6; y <= 161; y++) for (int x = 2; x < 11; x++) { black += scene[y * spec.Width + x] / 16383.0; n++; }
            black /= n;
            Check("Black level from the masked border (ADU)", im.Header.GetDouble("BLKLEVEL", 0), black * 16383, 0.1);
            Check("Pixels have the black level removed", im.Data[0][20 * 221 + 50], (scene[26 * spec.Width + 63] / 16383.0 - black) / (1 - black), 1e-5);
            CheckTrue("Camera without the make twice", im.Header.GetString("INSTRUME") == "Canon EOS Ra", im.Header.GetString("INSTRUME"));
            Check("Exposure", im.ExposureSeconds, 14.9, 1e-9);
            Check("ISO", im.Header.GetDouble("ISOSPEED", 0), 6400, 0);
            CheckTrue("Date (camera clock, local time)", im.DateObs == new DateTime(2022, 3, 12, 21, 38, 1, DateTimeKind.Local).ToUniversalTime());
            CheckTrue("Bit depth", im.OriginalBitDepth == 14);

            var path = Path.Combine(Path.GetTempPath(), "SidraluneTests", "synthetic.CR3");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, cr3);
            var loaded = ImageFile.Load(path);
            CheckTrue("Opened by extension like the other formats", ImageFile.IsSupported(path) && loaded.Width == 221 && loaded.SourcePath == path
                && ImageFile.OpenFilter.Contains("*.cr3") && ImageFile.RawExt.Contains(".cr3"));

            Func<Action, Type> thrown = a => { try { a(); return null; } catch (Exception e) { return e.GetType(); } };
            CheckTrue("A file that is not a CR3 is refused", thrown(() => Cr3IO.Read(new byte[100])) == typeof(InvalidDataException));
            CheckTrue("C-RAW (wavelet) files are reported as unsupported", thrown(() => Cr3IO.Read(Cr3(scene, new Cr3Spec { Levels = 3 }))) == typeof(NotSupportedException));
            CheckTrue("Rounded residuals are reported as unsupported", thrown(() => Cr3IO.Read(Cr3(scene, new Cr3Spec { PlaneFlags = 8 | 2 }))) == typeof(NotSupportedException));
            CheckTrue("A cut-off file is reported as damaged", thrown(() => Cr3IO.Read(cr3.Take(cr3.Length - 500).ToArray())) == typeof(InvalidDataException));
            var broken = (byte[])cr3.Clone();
            for (int i = broken.Length - 3000; i < broken.Length - 2900; i++) broken[i] ^= 0x5A;
            CheckTrue("Garbled data is reported as damaged", thrown(() => Cr3IO.Read(broken)) == typeof(InvalidDataException));
        }

        private static string SourceDir([CallerFilePath] string file = "") => Path.GetDirectoryName(file);

        [TestMethod]
        public void TestCr3CameraFiles()
        {
            var dir = Path.Combine(SourceDir(), "TestData", "Cr3");
            var files = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.cr3") : new string[0];
            if (files.Length == 0) { Info("No camera files in " + dir + ", skipped"); return; }
            foreach (var f in files)
            {
                var name = Path.GetFileName(f);
                var img = ImageFile.Load(f);
                var d = img.Data[0];
                // every plane of the file must end where its header says (the decoder checks), and the picture must make sense
                CheckTrue(name + ": no empty or broken values", d.All(v => v >= 0 && v <= 1 && !float.IsNaN(v)));
                Check(name + ": Canon's black level (ADU)", img.Header.GetDouble("BLKLEVEL", 0), 2048, 200);
                CheckTrue(name + ": metadata", img.ExposureSeconds > 0 && img.Header.GetDouble("ISOSPEED", 0) > 0 && img.DateObs != null && img.Header.GetString("INSTRUME").StartsWith("Canon"));
                if (img.Header.GetString("INSTRUME") == "Canon EOS Ra")
                    CheckTrue(name + ": EOS Ra picture size", img.Width == 6720 && img.Height == 4480 && img.Bayer == BayerPattern.RGGB, img.Width + " x " + img.Height + " " + img.Bayer);
                // the two halves come from separate tiles: they must match in level and noise
                Func<int, int, float> median = (x0, x1) =>
                {
                    var v = new List<float>();
                    for (int y = img.Height / 4; y < img.Height * 3 / 4; y += 7) for (int x = x0; x < x1; x += 2) v.Add(d[y * img.Width + x]);
                    v.Sort();
                    return v[v.Count / 2];
                };
                int mid = img.Width / 2;
                float l = median(mid - 400, mid - 100), r = median(mid + 100, mid + 400);
                CheckTrue(name + ": no step at the tile seam", Math.Abs(l - r) < 0.1 * Math.Max(l, r), l + " vs " + r);
                Info(name + ": " + img.Width + " x " + img.Height + ", " + img.Header.GetString("INSTRUME") + ", " + img.ExposureSeconds + " s, ISO " + img.Header.GetDouble("ISOSPEED", 0));
            }
        }
    }
}
