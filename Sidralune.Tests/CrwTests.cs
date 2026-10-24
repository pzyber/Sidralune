using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sidralune.Imaging;
using Sidralune.Imaging.IO;
using static Sidralune.Tests.Checks;

namespace Sidralune.Tests
{
    /// <summary>Canon CRW: synthetic files from a minimal CRW writer, and real camera files when TestData\Crw holds some.</summary>
    [TestClass]
    public class CrwTests : TestBase
    {
        /// <summary>MSB-first bits with a zero stuffed after every 0xFF, as in Canon's coded data.</summary>
        private class BitSink
        {
            public readonly List<byte> Bytes = new List<byte>();
            private int _acc, _n;
            public void Put(int value, int bits)
            {
                for (int i = bits - 1; i >= 0; i--)
                {
                    _acc = (_acc << 1) | ((value >> i) & 1);
                    if (++_n == 8) { Bytes.Add((byte)_acc); if (_acc == 0xFF) Bytes.Add(0); _acc = 0; _n = 0; }
                }
            }
            public byte[] Done() { while (_n != 0) Put(0, 1); return Bytes.ToArray(); }
        }

        /// <summary>The canonical codes of one of the reader's tables, by symbol: (code, length).</summary>
        private static Dictionary<int, Tuple<int, int>> Codes(string table, int set)
        {
            var tree = ((byte[][])typeof(CrwIO).GetField(table, BindingFlags.NonPublic | BindingFlags.Static).GetValue(null))[set];
            var codes = new Dictionary<int, Tuple<int, int>>();
            int code = 0, k = 16;
            for (int len = 1; len <= 16; len++, code <<= 1)
                for (int n = 0; n < tree[len - 1]; n++, code++, k++)
                    if (!codes.ContainsKey(tree[k])) codes[tree[k]] = Tuple.Create(code, len);
            return codes;
        }

        private static int Category(int v) { int a = Math.Abs(v), n = 0; while (a > 0) { a >>= 1; n++; } return n; }

        /// <summary>Canon's block coding of 10-bit values: 64-pixel blocks, differences along the row per column parity.</summary>
        private static byte[] EncodeBlocks(ushort[] high, int w, int h, int set, out int zeroRuns)
        {
            var first = Codes("FirstTree", set);
            var second = Codes("SecondTree", set);
            var o = new BitSink();
            Action<Dictionary<int, Tuple<int, int>>, int> sym = (c, s) => o.Put(c[s].Item1, c[s].Item2);
            Action<int, int> value = (v, len) => { if (len > 0) o.Put(v >= 0 ? v : v + (1 << len) - 1, len); };
            int carry = 0, b0 = 512, b1 = 512;
            long pnum = 0;
            zeroRuns = 0;
            var diff = new int[64];
            for (int row = 0; row < h; row += 8)
            {
                int blocks = Math.Min(8, h - row) * w >> 6;
                for (int block = 0; block < blocks; block++)
                {
                    for (int i = 0; i < 64; i++)
                    {
                        if (pnum++ % w == 0) b0 = b1 = 512;
                        int v = high[row * w + (block << 6) + i];
                        if ((i & 1) == 0) { diff[i] = v - b0; b0 = v; } else { diff[i] = v - b1; b1 = v; }
                    }
                    int dc = diff[0] - carry;
                    carry = diff[0];
                    sym(first, Category(dc));
                    value(dc, Category(dc));
                    int last = 63;
                    while (last > 0 && diff[last] == 0) last--;
                    int run = 0;
                    for (int i = 1; i <= last; i++)
                    {
                        if (diff[i] == 0) { run++; continue; }
                        while (run > 15) { sym(second, 0xF0); run -= 16; zeroRuns++; }
                        int len = Category(diff[i]);
                        sym(second, run << 4 | len);
                        value(diff[i], len);
                        run = 0;
                    }
                    if (last < 63) sym(second, 0x00);
                }
            }
            return o.Done();
        }

        private static byte[] Le16(int v) => BitConverter.GetBytes((ushort)v);
        private static byte[] Le32(long v) => BitConverter.GetBytes((uint)v);

        /// <summary>A CIFF heap: the records' data, then the table of (tag, size, offset) entries, then the table's offset.</summary>
        private static byte[] Heap(params Tuple<int, byte[]>[] records)
        {
            var data = new List<byte>();
            var table = new List<byte>(Le16(records.Length));
            foreach (var r in records)
            {
                table.AddRange(Le16(r.Item1).Concat(Le32(r.Item2.Length)).Concat(Le32(data.Count)));
                data.AddRange(r.Item2);
                if (data.Count % 2 == 1) data.Add(0);
            }
            int at = data.Count;
            return data.Concat(table).Concat(Le32(at)).ToArray();
        }

        private class CrwSpec
        {
            public int Width = 256, Height = 100, Table, ZeroRuns;
            public bool LowBits = true, Sensor = true;
        }

        /// <summary>A CRW file of 12-bit values (10 when there are no low bits) with Canon's records.</summary>
        private static byte[] Crw(ushort[] raw, CrwSpec s)
        {
            int w = s.Width, h = s.Height;
            var high = raw.Select(v => (ushort)(s.LowBits ? v >> 2 : v)).ToArray();
            var data = new List<byte>();
            if (s.LowBits)
                for (int i = 0; i < w * h; i += 4)
                    data.Add((byte)Enumerable.Range(0, 4).Sum(k => (raw[i + k] & 3) << (2 * k)));
            data.AddRange(new byte[514]);
            int zr;
            data.AddRange(EncodeBlocks(high, w, h, s.Table, out zr));
            s.ZeroRuns = zr;
            var shot = new byte[66];
            Le16(192).CopyTo(shot, 4);  // ISO 200
            Le16(208).CopyTo(shot, 10); // 1/90 s
            var props = new List<Tuple<int, byte[]>>
            {
                Tuple.Create(0x080a, Encoding.ASCII.GetBytes("Canon\0Canon EOS 10D\0")),
                Tuple.Create(0x102a, shot),
                Tuple.Create(0x180e, Le32((long)(new DateTime(2003, 7, 28, 1, 31, 46) - new DateTime(1970, 1, 1)).TotalSeconds).Concat(new byte[8]).ToArray()),
                Tuple.Create(0x1835, Le32(s.Table).Concat(new byte[12]).ToArray()),
            };
            if (s.Sensor)
                props.Add(Tuple.Create(0x1031, new[] { 34, w, h, 1, 1, 18, 6, 249, 95, 0, 0, 0, 0, 0, 0, 0, 0 }.SelectMany(v => Le16(v)).ToArray()));
            var root = Heap(Tuple.Create(0x2005, data.ToArray()), Tuple.Create(0x300a, Heap(props.ToArray())));
            var head = new List<byte> { (byte)'I', (byte)'I' };
            head.AddRange(Le32(26));
            head.AddRange(Encoding.ASCII.GetBytes("HEAPCCDR"));
            head.AddRange(Le16(2).Concat(Le16(1)).Concat(new byte[8]));
            return head.Concat(root).ToArray();
        }

        /// <summary>Masked border, sky with stars, saturation, sharp jumps and flat stretches with lone spikes.</summary>
        private static ushort[] Scene(int w, int h, int seed, int max)
        {
            var rnd = new Random(seed);
            var raw = new ushort[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    double v = max / 8 + (x < 18 || y < 6 ? 0 : max / 16 + x * max / 2048.0) + rnd.Next(-4, 5);
                    if (y >= 30 && y < 40) v = max / 4 + (x % 40 == 39 ? max / 8 : 0);       // zero runs past 15 between spikes
                    if (y >= 60 && y < 62) v = (x * 7919 + y) % 3 == 0 ? 0 : max;          // the largest differences
                    raw[y * w + x] = (ushort)Math.Max(0, Math.Min(max, v));
                }
            foreach (var star in new[] { new[] { 100, 80 }, new[] { 200, 20 } })
                for (int y = star[1] - 5; y <= star[1] + 5; y++)
                    for (int x = star[0] - 5; x <= star[0] + 5; x++)
                        raw[y * w + x] = (ushort)Math.Min(max, raw[y * w + x] + 3 * max * Math.Exp(-((x - star[0]) * (x - star[0]) + (y - star[1]) * (y - star[1])) / 5.0));
            return raw;
        }

        [TestMethod]
        public void TestCrwSynthetic()
        {
            foreach (var low in new[] { true, false })
                for (int table = 0; table < 3; table++)
                {
                    var s = new CrwSpec { Table = table, LowBits = low };
                    int max = low ? 4095 : 1023;
                    var raw = Scene(s.Width, s.Height, table, max);
                    var file = Crw(raw, s);
                    var info = CrwIO.Parse(file);
                    int bits;
                    var back = CrwIO.Decode(file, info, out bits);
                    string name = "Table " + table + (low ? ", 12 bits" : ", 10 bits") + ": ";
                    CheckTrue(name + "decoded exactly, including a part strip of 4 rows", back.SequenceEqual(raw) && bits == (low ? 12 : 10));
                    if (table == 0 && low) CheckTrue("The scene uses runs of more than 15 zeros", s.ZeroRuns > 10, s.ZeroRuns + " runs");
                }

            var spec = new CrwSpec();
            var scene = Scene(spec.Width, spec.Height, 5, 4095);
            var crw = Crw(scene, spec);
            var img = CrwIO.Read(crw);
            CheckTrue("Cropped to the sensor area, RGGB kept by the even border", img.Width == 232 && img.Height == 90 && img.Bayer == BayerPattern.RGGB, img.Width + " x " + img.Height + " " + img.Bayer);
            double black = 0; int n = 0;
            for (int y = 6; y <= 95; y++) for (int x = 2; x < 16; x++) { black += scene[y * spec.Width + x] / 4095.0; n++; }
            black /= n;
            Check("Black level from the masked border (ADU)", img.Header.GetDouble("BLKLEVEL", 0), black * 4095, 0.1);
            Check("Pixels have the black level removed", img.Data[0][50 * 232 + 70], (scene[56 * spec.Width + 88] / 4095.0 - black) / (1 - black), 1e-5);
            CheckTrue("Camera", img.Header.GetString("INSTRUME") == "Canon EOS 10D", img.Header.GetString("INSTRUME"));
            Check("ISO from Canon's APEX steps", img.Header.GetDouble("ISOSPEED", 0), 200, 0);
            Check("Shutter from Canon's APEX steps (s)", img.ExposureSeconds, Math.Pow(2, -6.5), 1e-12);
            CheckTrue("Date (camera clock, local time)", img.DateObs == new DateTime(2003, 7, 28, 1, 31, 46, DateTimeKind.Local).ToUniversalTime());
            CheckTrue("Bit depth", img.OriginalBitDepth == 12);

            var path = Path.Combine(Path.GetTempPath(), "SidraluneTests", "synthetic.CRW");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, crw);
            var loaded = ImageFile.Load(path);
            CheckTrue("Opened by extension like the other formats", ImageFile.IsSupported(path) && loaded.Width == 232 && loaded.SourcePath == path
                && ImageFile.OpenFilter.Contains("*.crw") && ImageFile.RawExt.Contains(".crw"));

            Func<Action, Type> thrown = a => { try { a(); return null; } catch (Exception e) { return e.GetType(); } };
            CheckTrue("A file that is not a CRW is refused", thrown(() => CrwIO.Read(new byte[100])) == typeof(InvalidDataException));
            CheckTrue("A file without its sensor size is reported as unsupported", thrown(() => CrwIO.Read(Crw(scene, new CrwSpec { Sensor = false }))) == typeof(NotSupportedException));
            CheckTrue("A cut-off file is reported as damaged", thrown(() => CrwIO.Read(crw.Take(crw.Length - 500).ToArray())) == typeof(InvalidDataException));
            var broken = (byte[])crw.Clone();
            int coded = 26 + spec.Width * spec.Height / 4 + 514;
            for (int i = coded + 3000; i < coded + 3100; i++) broken[i] ^= 0x5A;
            CheckTrue("Garbled data is reported as damaged", thrown(() => CrwIO.Read(broken)) == typeof(InvalidDataException));
        }

        private static string SourceDir([CallerFilePath] string file = "") => Path.GetDirectoryName(file);

        [TestMethod]
        public void TestCrwCameraFiles()
        {
            var dir = Path.Combine(SourceDir(), "TestData", "Crw");
            var files = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.crw") : new string[0];
            if (files.Length == 0) { Info("No camera files in " + dir + ", skipped"); return; }
            // the visible picture of each camera
            var sizes = new Dictionary<string, int[]>
            {
                { "Canon EOS D30", new[] { 2160, 1440 } }, { "Canon EOS D60", new[] { 3072, 2048 } }, { "Canon EOS 10D", new[] { 3072, 2048 } },
                { "Canon EOS 300D DIGITAL", new[] { 3072, 2048 } }, { "Canon PowerShot G5", new[] { 2592, 1944 } }, { "Canon PowerShot Pro1", new[] { 3264, 2448 } },
            };
            foreach (var f in files)
            {
                var name = Path.GetFileName(f);
                var img = ImageFile.Load(f);
                var d = img.Data[0];
                string model = img.Header.GetString("INSTRUME");
                // the decoder checks that the coded data ends with its record; the picture must make sense as well
                CheckTrue(name + ": no broken values", d.All(v => v >= 0 && v <= 1 && !float.IsNaN(v)));
                if (sizes.ContainsKey(model))
                    CheckTrue(name + ": picture size of the " + model, img.Width == sizes[model][0] && img.Height == sizes[model][1], img.Width + " x " + img.Height);
                CheckTrue(name + ": 12-bit RGGB", img.OriginalBitDepth == 12 && img.Bayer == BayerPattern.RGGB);
                Check(name + ": black level (ADU)", img.Header.GetDouble("BLKLEVEL", 0), 300, 250);
                CheckTrue(name + ": metadata", img.ExposureSeconds > 0 && img.Header.GetDouble("ISOSPEED", 0) >= 50 && img.DateObs != null && model.StartsWith("Canon"));
                // the two greens of each 2 x 2 cell see the same light: equal means the colour layout is right
                double g1 = 0, g2 = 0;
                for (int y = 0; y + 1 < img.Height; y += 2) for (int x = 0; x + 1 < img.Width; x += 2) { g1 += d[y * img.Width + x + 1]; g2 += d[(y + 1) * img.Width + x]; }
                Check(name + ": the two greens agree", g1 / g2, 1, 0.05);
                Info(name + ": " + img.Width + " x " + img.Height + ", " + model + ", " + img.ExposureSeconds.ToString("G3") + " s, ISO " + img.Header.GetDouble("ISOSPEED", 0));
            }
        }
    }
}
