using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Sidralune.Imaging.ColorManagement;

namespace Sidralune.Imaging.IO
{
    /// <summary>
    /// ICC profiles inside PNG (the iCCP chunk; an sRGB chunk means sRGB) and JPEG (APP2 "ICC_PROFILE" segments):
    /// read from file bytes, and written into the files System.Drawing produces.
    /// </summary>
    public static class EmbeddedProfiles
    {
        private static readonly byte[] PngSignature = { 137, 80, 78, 71, 13, 10, 26, 10 };
        private static readonly byte[] IccMarker = Encoding.ASCII.GetBytes("ICC_PROFILE\0");

        /// <summary>The profile a PNG or JPEG file carries, or null.</summary>
        public static IccProfile Read(byte[] file)
        {
            try
            {
                if (IsPng(file)) return ReadPng(file);
                if (file.Length > 4 && file[0] == 0xFF && file[1] == 0xD8) return ReadJpeg(file);
            }
            catch { }
            return null;
        }

        /// <summary>The file with the profile embedded (null: any colour space information removed).</summary>
        public static byte[] Write(byte[] file, IccProfile profile)
        {
            if (IsPng(file)) return WritePng(file, profile);
            if (file.Length > 4 && file[0] == 0xFF && file[1] == 0xD8) return WriteJpeg(file, profile);
            return file;
        }

        // ------------------------------------------------------------------ PNG

        private static bool IsPng(byte[] f) => f.Length > 8 && PngSignature.SequenceEqual(f.Take(8));

        private static IEnumerable<Tuple<string, int, int>> Chunks(byte[] f)
        {
            // (type, data offset, data length)
            int p = 8;
            while (p + 12 <= f.Length)
            {
                int len = (int)IccProfile.U32(f, p);
                string type = Encoding.ASCII.GetString(f, p + 4, 4);
                if (len < 0 || p + 12 + len > f.Length) yield break;
                yield return Tuple.Create(type, p + 8, len);
                if (type == "IEND") yield break;
                p += 12 + len;
            }
        }

        private static IccProfile ReadPng(byte[] f)
        {
            foreach (var c in Chunks(f))
            {
                if (c.Item1 == "iCCP")
                {
                    int nul = Array.IndexOf(f, (byte)0, c.Item2, Math.Min(80, c.Item3));
                    if (nul < 0) return null;
                    int start = nul + 2; // name, NUL, compression method (0 = zlib)
                    return IccProfile.TryCreate(Inflate(f, start, c.Item2 + c.Item3 - start));
                }
                if (c.Item1 == "sRGB") return IccProfiles.Srgb;
                if (c.Item1 == "IDAT") break; // colour chunks come before the image data
            }
            return null;
        }

        private static byte[] WritePng(byte[] f, IccProfile profile)
        {
            var o = new MemoryStream();
            o.Write(f, 0, 8);
            foreach (var c in Chunks(f))
            {
                // the encoder's own colour chunks (it always claims sRGB) give way to the profile
                if (c.Item1 == "sRGB" || c.Item1 == "gAMA" || c.Item1 == "cHRM" || c.Item1 == "iCCP") continue;
                o.Write(f, c.Item2 - 8, c.Item3 + 12);
                if (c.Item1 == "IHDR" && profile != null)
                {
                    var name = Encoding.GetEncoding("ISO-8859-1").GetBytes(PngName(profile.Description));
                    var data = new MemoryStream();
                    data.Write(name, 0, name.Length);
                    data.WriteByte(0); data.WriteByte(0);
                    var z = Deflate(profile.Bytes);
                    data.Write(z, 0, z.Length);
                    WriteChunk(o, "iCCP", data.ToArray());
                }
            }
            return o.ToArray();
        }

        private static string PngName(string s)
        {
            var n = new string(s.Where(ch => ch >= 32 && ch <= 126).ToArray()).Trim();
            if (n.Length == 0) n = "ICC profile";
            return n.Length > 79 ? n.Substring(0, 79) : n;
        }

        private static void WriteChunk(Stream o, string type, byte[] data)
        {
            var t = Encoding.ASCII.GetBytes(type);
            WriteBe(o, (uint)data.Length);
            o.Write(t, 0, 4);
            o.Write(data, 0, data.Length);
            uint crc = Crc32(t, 0, 4, 0xFFFFFFFF);
            crc = Crc32(data, 0, data.Length, crc) ^ 0xFFFFFFFF;
            WriteBe(o, crc);
        }

        private static void WriteBe(Stream o, uint v) { o.WriteByte((byte)(v >> 24)); o.WriteByte((byte)(v >> 16)); o.WriteByte((byte)(v >> 8)); o.WriteByte((byte)v); }

        private static uint[] _crcTable;
        private static uint Crc32(byte[] b, int off, int len, uint crc)
        {
            var t = _crcTable;
            if (t == null)
            {
                t = new uint[256];
                for (uint n = 0; n < 256; n++) { uint c = n; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1; t[n] = c; }
                _crcTable = t;
            }
            for (int i = off; i < off + len; i++) crc = t[(crc ^ b[i]) & 0xFF] ^ (crc >> 8);
            return crc;
        }

        /// <summary>zlib-wrapped deflate (as PNG and XISF use).</summary>
        internal static byte[] Deflate(byte[] data)
        {
            using (var ms = new MemoryStream())
            {
                ms.WriteByte(0x78); ms.WriteByte(0x9C);
                using (var ds = new DeflateStream(ms, CompressionLevel.Optimal, true)) ds.Write(data, 0, data.Length);
                uint a = 1, b = 0;
                foreach (var x in data) { a = (a + x) % 65521; b = (b + a) % 65521; }
                uint adler = (b << 16) | a;
                ms.WriteByte((byte)(adler >> 24)); ms.WriteByte((byte)(adler >> 16)); ms.WriteByte((byte)(adler >> 8)); ms.WriteByte((byte)adler);
                return ms.ToArray();
            }
        }

        internal static byte[] Inflate(byte[] b, int off, int len)
        {
            using (var ms = new MemoryStream(b, off + 2, len - 2)) // skip the zlib header
            using (var ds = new DeflateStream(ms, CompressionMode.Decompress))
            using (var o = new MemoryStream())
            {
                ds.CopyTo(o);
                return o.ToArray();
            }
        }

        // ------------------------------------------------------------------ JPEG

        private static IccProfile ReadJpeg(byte[] f)
        {
            var parts = new SortedDictionary<int, byte[]>();
            int p = 2;
            while (p + 4 <= f.Length && f[p] == 0xFF)
            {
                int marker = f[p + 1];
                if (marker == 0xDA || marker == 0xD9) break; // image data: no more headers
                int len = (f[p + 2] << 8) | f[p + 3];
                if (marker == 0xE2 && len > 16 && f.Skip(p + 4).Take(12).SequenceEqual(IccMarker))
                {
                    int seq = f[p + 16];
                    var chunk = new byte[len - 16];
                    Array.Copy(f, p + 18, chunk, 0, chunk.Length);
                    parts[seq] = chunk;
                }
                p += 2 + len;
            }
            if (parts.Count == 0) return null;
            return IccProfile.TryCreate(parts.Values.SelectMany(x => x).ToArray());
        }

        private static byte[] WriteJpeg(byte[] f, IccProfile profile)
        {
            var o = new MemoryStream();
            o.Write(f, 0, 2); // SOI
            int p = 2;
            bool written = profile == null;
            while (p + 4 <= f.Length && f[p] == 0xFF)
            {
                int marker = f[p + 1];
                if (marker == 0xDA) break;
                int len = (f[p + 2] << 8) | f[p + 3];
                bool icc = marker == 0xE2 && len > 16 && f.Skip(p + 4).Take(12).SequenceEqual(IccMarker);
                // the profile goes after the JFIF/EXIF application segments, before everything else
                if (!written && marker != 0xE0 && marker != 0xE1) { WriteIccSegments(o, profile.Bytes); written = true; }
                if (!icc) o.Write(f, p, 2 + len);
                p += 2 + len;
            }
            if (!written) WriteIccSegments(o, profile.Bytes);
            o.Write(f, p, f.Length - p);
            return o.ToArray();
        }

        private static void WriteIccSegments(Stream o, byte[] icc)
        {
            const int max = 65519; // 65535 - length (2) - "ICC_PROFILE\0" (12) - sequence and count (2)
            int count = (icc.Length + max - 1) / max;
            for (int i = 0; i < count; i++)
            {
                int n = Math.Min(max, icc.Length - i * max);
                int len = n + 16;
                o.WriteByte(0xFF); o.WriteByte(0xE2);
                o.WriteByte((byte)(len >> 8)); o.WriteByte((byte)len);
                o.Write(IccMarker, 0, 12);
                o.WriteByte((byte)(i + 1)); o.WriteByte((byte)count);
                o.Write(icc, i * max, n);
            }
        }
    }
}
