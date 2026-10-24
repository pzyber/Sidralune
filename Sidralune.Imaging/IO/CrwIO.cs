using System;
using System.IO;
using System.Linq;
using System.Text;

namespace Sidralune.Imaging.IO
{
    /// <summary>
    /// Canon CRW raw reader (EOS D30, D60, 10D, 300D and the PowerShots of 2000-2004): the CIFF heap of records and
    /// Canon's block-coded raw data, 10 bits per pixel plus two low bits stored separately on most cameras.
    /// The three sets of Huffman tables are format data from dcraw (Dave Coffin), whose code outside its Foveon
    /// functions is free for all uses; the decoder itself is Sidralune's own.
    /// </summary>
    public static class CrwIO
    {
        // per table: 16 code-length counts, then the symbols in code order
        private static readonly byte[][] FirstTree =
        {
            new byte[] { 0,1,4,2,3,1,2,0,0,0,0,0,0,0,0,0, 0x04,0x03,0x05,0x06,0x02,0x07,0x01,0x08,0x09,0x00,0x0a,0x0b,0xff },
            new byte[] { 0,2,2,3,1,1,1,1,2,0,0,0,0,0,0,0, 0x03,0x02,0x04,0x01,0x05,0x00,0x06,0x07,0x09,0x08,0x0a,0x0b,0xff },
            new byte[] { 0,0,6,3,1,1,2,0,0,0,0,0,0,0,0,0, 0x06,0x05,0x07,0x04,0x08,0x03,0x09,0x02,0x00,0x0a,0x01,0x0b,0xff },
        };

        private static readonly byte[][] SecondTree =
        {
            new byte[] { 0,2,2,2,1,4,2,1,2,5,1,1,0,0,0,139,
                0x03,0x04,0x02,0x05,0x01,0x06,0x07,0x08,0x12,0x13,0x11,0x14,0x09,0x15,0x22,0x00,0x21,0x16,0x0a,0xf0,
                0x23,0x17,0x24,0x31,0x32,0x18,0x19,0x33,0x25,0x41,0x34,0x42,0x35,0x51,0x36,0x37,0x38,0x29,0x79,0x26,0x1a,0x39,0x56,0x57,
                0x28,0x27,0x52,0x55,0x58,0x43,0x76,0x59,0x77,0x54,0x61,0xf9,0x71,0x78,0x75,0x96,0x97,0x49,0xb7,0x53,0xd7,0x74,0xb6,0x98,
                0x47,0x48,0x95,0x69,0x99,0x91,0xfa,0xb8,0x68,0xb5,0xb9,0xd6,0xf7,0xd8,0x67,0x46,0x45,0x94,0x89,0xf8,0x81,0xd5,0xf6,0xb4,
                0x88,0xb1,0x2a,0x44,0x72,0xd9,0x87,0x66,0xd4,0xf5,0x3a,0xa7,0x73,0xa9,0xa8,0x86,0x62,0xc7,0x65,0xc8,0xc9,0xa1,0xf4,0xd1,
                0xe9,0x5a,0x92,0x85,0xa6,0xe7,0x93,0xe8,0xc1,0xc6,0x7a,0x64,0xe1,0x4a,0x6a,0xe6,0xb3,0xf1,0xd3,0xa5,0x8a,0xb2,0x9a,0xba,
                0x84,0xa4,0x63,0xe5,0xc5,0xf3,0xd2,0xc4,0x82,0xaa,0xda,0xe4,0xf2,0xca,0x83,0xa3,0xa2,0xc3,0xea,0xc2,0xe2,0xe3,0xff,0xff },
            new byte[] { 0,2,2,1,4,1,4,1,3,3,1,0,0,0,0,140,
                0x02,0x03,0x01,0x04,0x05,0x12,0x11,0x06,0x13,0x07,0x08,0x14,0x22,0x09,0x21,0x00,0x23,0x15,0x31,0x32,
                0x0a,0x16,0xf0,0x24,0x33,0x41,0x42,0x19,0x17,0x25,0x18,0x51,0x34,0x43,0x52,0x29,0x35,0x61,0x39,0x71,0x62,0x36,0x53,0x26,
                0x38,0x1a,0x37,0x81,0x27,0x91,0x79,0x55,0x45,0x28,0x72,0x59,0xa1,0xb1,0x44,0x69,0x54,0x58,0xd1,0xfa,0x57,0xe1,0xf1,0xb9,
                0x49,0x47,0x63,0x6a,0xf9,0x56,0x46,0xa8,0x2a,0x4a,0x78,0x99,0x3a,0x75,0x74,0x86,0x65,0xc1,0x76,0xb6,0x96,0xd6,0x89,0x85,
                0xc9,0xf5,0x95,0xb4,0xc7,0xf7,0x8a,0x97,0xb8,0x73,0xb7,0xd8,0xd9,0x87,0xa7,0x7a,0x48,0x82,0x84,0xea,0xf4,0xa6,0xc5,0x5a,
                0x94,0xa4,0xc6,0x92,0xc3,0x68,0xb5,0xc8,0xe4,0xe5,0xe6,0xe9,0xa2,0xa3,0xe3,0xc2,0x66,0x67,0x93,0xaa,0xd4,0xd5,0xe7,0xf8,
                0x88,0x9a,0xd7,0x77,0xc4,0x64,0xe2,0x98,0xa5,0xca,0xda,0xe8,0xf3,0xf6,0xa9,0xb2,0xb3,0xf2,0xd2,0x83,0xba,0xd3,0xff,0xff },
            new byte[] { 0,0,6,2,1,3,3,2,5,1,2,2,8,10,0,117,
                0x04,0x05,0x03,0x06,0x02,0x07,0x01,0x08,0x09,0x12,0x13,0x14,0x11,0x15,0x0a,0x16,0x17,0xf0,0x00,0x22,
                0x21,0x18,0x23,0x19,0x24,0x32,0x31,0x25,0x33,0x38,0x37,0x34,0x35,0x36,0x39,0x79,0x57,0x58,0x59,0x28,0x56,0x78,0x27,0x41,
                0x29,0x77,0x26,0x42,0x76,0x99,0x1a,0x55,0x98,0x97,0xf9,0x48,0x54,0x96,0x89,0x47,0xb7,0x49,0xfa,0x75,0x68,0xb6,0x67,0x69,
                0xb9,0xb8,0xd8,0x52,0xd7,0x88,0xb5,0x74,0x51,0x46,0xd9,0xf8,0x3a,0xd6,0x87,0x45,0x7a,0x95,0xd5,0xf6,0x86,0xb4,0xa9,0x94,
                0x53,0x2a,0xa8,0x43,0xf5,0xf7,0xd4,0x66,0xa7,0x5a,0x44,0x8a,0xc9,0xe8,0xc8,0xe7,0x9a,0x6a,0x73,0x4a,0x61,0xc7,0xf4,0xc6,
                0x65,0xe9,0x72,0xe6,0x71,0x91,0x93,0xa6,0xda,0x92,0x85,0x62,0xf3,0xc5,0xb2,0xa4,0x84,0xba,0x64,0xa5,0xb3,0xd2,0x81,0xe5,
                0xd3,0xaa,0xc4,0xca,0xf2,0xb1,0xe4,0xd1,0x83,0x63,0xea,0xc3,0xe2,0x82,0xf1,0xa3,0xc2,0xa1,0xc1,0xe3,0xa2,0xe1,0xff,0xff },
        };

        /// <summary>The records of a CRW file that matter here.</summary>
        public class Info
        {
            public string Model;
            public long DataOffset = -1, DataSize;
            public int Table, RawWidth, RawHeight;
            public long[] Sensor;
            public double Iso, Exposure;
            public DateTime? Taken;
        }

        public static bool IsCrw(byte[] d) => d.Length > 14 && (d[0] == 'I' || d[0] == 'M') && Encoding.ASCII.GetString(d, 6, 8) == "HEAPCCDR";

        public static AstroImage Read(string path)
        {
            var img = Read(File.ReadAllBytes(path));
            img.SourcePath = path;
            return img;
        }

        public static AstroImage Read(byte[] d)
        {
            var info = Parse(d);
            if (info.DataOffset < 0) throw new InvalidDataException("No raw image in the CRW file");
            if (info.RawWidth <= 0 || info.RawHeight <= 0) throw new NotSupportedException("This CRW file does not give its sensor size (cameras before 2001): convert it to DNG with Adobe DNG Converter");
            int bits;
            var px = Decode(d, info, out bits);
            int w = info.RawWidth, h = info.RawHeight;
            var img = new AstroImage(w, h, 1) { OriginalBitDepth = bits, Bayer = BayerPattern.RGGB };
            var dst = img.Data[0];
            float maxV = (1 << bits) - 1;
            for (int i = 0; i < dst.Length; i++) dst[i] = px[i] / maxV;
            img = Cr3IO.CropToSensor(img, info.Sensor, maxV);
            img.Header.Set("INSTRUME", string.IsNullOrEmpty(info.Model) ? "Canon camera" : info.Model);
            if (info.Iso > 0) img.Header.Set("ISOSPEED", Math.Round(info.Iso));
            if (info.Exposure > 0) img.ExposureSeconds = info.Exposure;
            if (info.Taken != null) img.DateObs = info.Taken;
            return img;
        }

        // ------------------------------------------------------------------ CIFF records

        public static Info Parse(byte[] d)
        {
            if (!IsCrw(d)) throw new InvalidDataException("Not a Canon CRW file");
            bool big = d[0] == 'M';
            Func<long, int> u16 = o => big ? (d[o] << 8) | d[o + 1] : d[o] | (d[o + 1] << 8);
            Func<long, uint> u32 = o => big ? (uint)((d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3]) : (uint)(d[o] | (d[o + 1] << 8) | (d[o + 2] << 16) | (d[o + 3] << 24));
            var info = new Info();
            long hlen = u32(2);
            Action<long, long, int> heap = null;
            heap = (start, length, depth) =>
            {
                if (depth > 8 || length < 6 || start + length > d.Length) return;
                long table = start + u32(start + length - 4);
                if (table < start || table + 2 > start + length) return;
                int n = u16(table);
                for (int i = 0; i < n && table + 2 + 10 * i + 10 <= start + length; i++)
                {
                    long e = table + 2 + 10 * i;
                    int tag = u16(e);
                    long size = u32(e + 2), at = start + u32(e + 6);
                    bool inEntry = tag >> 14 == 1;  // values of up to 8 bytes are kept in the entry itself
                    if (inEntry) { at = e + 2; size = 8; }
                    if (at < 0 || at + size > d.Length) continue;
                    int type = tag & 0x3fff;
                    if (!inEntry && ((tag & 0x3800) == 0x2800 || (tag & 0x3800) == 0x3000)) { heap(at, size, depth + 1); continue; }
                    switch (type)
                    {
                        case 0x2005: info.DataOffset = at; info.DataSize = size; break;
                        case 0x080a: // make and model, each ending in a zero
                            var parts = Encoding.ASCII.GetString(d, (int)at, (int)Math.Min(size, 64)).Split('\0');
                            if (parts.Length > 1) info.Model = parts[1].Trim();
                            break;
                        case 0x1835: if (size >= 4) info.Table = (int)Math.Min(2, u32(at)); break;
                        case 0x1031: if (size >= 18) info.Sensor = Enumerable.Range(0, (int)(size / 2)).Select(k => (long)u16(at + 2 * k)).ToArray(); break;
                        case 0x180e: if (size >= 4) info.Taken = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Local).AddSeconds(u32(at)).ToUniversalTime(); break;
                        case 0x102a: // shot info: ISO and shutter in Canon's APEX steps of 1/32
                            if (size >= 12)
                            {
                                info.Iso = Math.Pow(2, u16(at + 4) / 32.0 - 4) * 50;
                                double t = Math.Pow(2, -(short)u16(at + 10) / 32.0);
                                if (t > 1e6 && size >= 50) t = u16(at + 48) / 10.0;
                                if (info.Exposure == 0) info.Exposure = t;
                            }
                            break;
                        case 0x1818: // exposure info: the shutter as a float APEX value, preferred when present
                            if (size >= 8) info.Exposure = Math.Pow(2, -BitConverter.ToSingle(BitConverter.GetBytes(u32(at + 4)), 0));
                            break;
                    }
                }
            };
            heap(hlen, d.Length - hlen, 0);
            if (info.Sensor != null)
            {
                info.RawWidth = (int)info.Sensor[1];
                info.RawHeight = (int)info.Sensor[2];
            }
            return info;
        }

        // ------------------------------------------------------------------ raw data

        /// <summary>
        /// Most cameras store the two low bits of every pixel first (four pixels a byte), then 514 bytes later the
        /// coded high bits. Coded data never has a 0xFF that is not followed by 0, so a different 0xFF pair means low bits.
        /// </summary>
        private static bool HasLowBits(byte[] d, long data)
        {
            bool low = true;
            for (long i = data + 514; i < Math.Min(d.Length - 1, data + 0x4000 - 27); i++)
                if (d[i] == 0xFF)
                {
                    if (d[i + 1] != 0) return true;
                    low = false;
                }
            return low;
        }

        /// <summary>A canonical Huffman code as a 16-bit lookup: symbol in the low byte, code length above it.</summary>
        private static ushort[] Lookup(byte[] tree)
        {
            var look = new ushort[65536];
            int code = 0, k = 16;
            for (int len = 1; len <= 16; len++, code <<= 1)
                for (int n = 0; n < tree[len - 1]; n++, code++, k++)
                {
                    if (k >= tree.Length) throw new InvalidDataException("Bad CRW decoder table");
                    int first = code << (16 - len), count = 1 << (16 - len);
                    for (int j = 0; j < count; j++) look[first + j] = (ushort)(len << 8 | tree[k]);
                }
            return look;
        }

        private sealed class BitReader
        {
            private readonly byte[] _d;
            private readonly long _end;
            private long _pos;
            private ulong _buf;
            private int _n;
            public bool Ended;
            /// <summary>Bytes of the data not reached yet.</summary>
            public long Unused => _end - (_pos - _n / 8);

            public BitReader(byte[] d, long start, long end) { _d = d; _pos = start; _end = Math.Min(end, d.Length); }

            private void Fill()
            {
                while (_n <= 56)
                {
                    ulong b = 0;
                    if (_pos < _end && !Ended)
                    {
                        b = _d[_pos++];
                        // a stuffed zero follows each 0xFF of data; anything else is the end
                        if (b == 0xFF) { if (_pos < _end && _d[_pos] == 0) _pos++; else { Ended = true; b = 0; } }
                    }
                    else Ended = true;
                    _buf |= b << (56 - _n);
                    _n += 8;
                }
            }

            public int Peek16() { if (_n < 16) Fill(); return (int)(_buf >> 48); }
            public void Skip(int k) { _buf <<= k; _n -= k; }
            public int Bits(int k) { if (k == 0) return 0; if (_n < k) Fill(); int v = (int)(_buf >> (64 - k)); Skip(k); return v; }
        }

        /// <summary>Decodes the raw data to RawWidth x RawHeight values of 10 or 12 bits.</summary>
        public static ushort[] Decode(byte[] d, Info info, out int bits)
        {
            int w = info.RawWidth, h = info.RawHeight;
            long data = info.DataOffset, end = info.DataSize > 0 ? data + info.DataSize : d.Length;
            bool low = HasLowBits(d, data);
            bits = low ? 12 : 10;
            long coded = data + 514 + (low ? (long)w * h / 4 : 0);
            if (coded >= end) throw new InvalidDataException("CRW raw data ends early");
            var first = Lookup(FirstTree[info.Table]);
            var second = Lookup(SecondTree[info.Table]);
            var br = new BitReader(d, coded, end);
            var px = new ushort[w * h];
            var diff = new int[64];
            int carry = 0, base0 = 512, base1 = 512, errors = 0;
            long pnum = 0;
            for (int row = 0; row < h; row += 8)
            {
                int strip = row * w, blocks = Math.Min(8, h - row) * w >> 6;
                for (int block = 0; block < blocks; block++)
                {
                    Array.Clear(diff, 0, 64);
                    // like JPEG AC coding: a first-table code for the first value, then (zeros skipped, length) codes
                    for (int i = 0; i < 64; i++)
                    {
                        int e = (i > 0 ? second : first)[br.Peek16()];
                        br.Skip(e >> 8);
                        int leaf = e & 0xFF;
                        if (leaf == 0 && i > 0) break;
                        if (leaf == 0xFF) continue;
                        i += leaf >> 4;
                        int len = leaf & 15;
                        if (len == 0) continue;
                        int v = br.Bits(len);
                        if ((v & (1 << (len - 1))) == 0) v -= (1 << len) - 1;
                        if (i < 64) diff[i] = v;
                    }
                    diff[0] += carry;
                    carry = diff[0];
                    // the values are differences along the row, separately for even and odd columns
                    int o = strip + (block << 6);
                    for (int i = 0; i < 64; i++)
                    {
                        if (pnum++ % w == 0) base0 = base1 = 512;
                        int v = (i & 1) == 0 ? (base0 += diff[i]) : (base1 += diff[i]);
                        if (v < 0 || v > 1023) { errors++; v = v < 0 ? 0 : 1023; }
                        px[o + i] = (ushort)v;
                    }
                }
                if (br.Ended && row + 8 < h || errors > w) throw new InvalidDataException("Damaged CRW raw data");
                if (low)
                {
                    // the low bits of this strip of 8 rows: four pixels to a byte, the lowest bits first
                    long lb = data + (long)row * w / 4;
                    int count = Math.Min(8, h - row) * w;
                    for (int i = 0; i < count; i++)
                    {
                        int v = (px[strip + i] << 2) + ((d[lb + i / 4] >> (2 * (i & 3))) & 3);
                        if (w == 2672 && v < 512) v += 2; // PowerShot G3, G5 and kin: as dcraw does
                        px[strip + i] = (ushort)v;
                    }
                }
            }
            // the coded data ends a few bytes before the end of its record in every camera's files; elsewhere means lost step
            if (br.Unused > 64) throw new InvalidDataException("Damaged CRW raw data (the coded data does not end where the record does)");
            return px;
        }
    }
}
