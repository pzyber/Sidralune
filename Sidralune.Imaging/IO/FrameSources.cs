using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;

namespace Sidralune.Imaging.IO
{
    /// <summary>A sequence of frames (video file or list of images).</summary>
    public interface IFrameSource : IDisposable
    {
        int FrameCount { get; }
        int Width { get; }
        int Height { get; }
        int Channels { get; }
        BayerPattern Bayer { get; }
        int BitDepth { get; }
        string Path { get; }
        AstroImage ReadFrame(int index);
        /// <summary>UTC timestamp of a frame if known.</summary>
        DateTime? Timestamp(int index);
    }

    /// <summary>SER video format (Grischa Hahn / Heiko Wilkens spec v3), mono, Bayer and RGB/BGR, 8-16 bit, with timestamps.</summary>
    public class SerReader : IFrameSource
    {
        private readonly FileStream _fs;
        private readonly object _lock = new object();
        public int ColorId, LittleEndian, PixelDepth;
        public string Observer, Instrument, Telescope;
        public DateTime? StartTimeUtc;
        private long[] _timestamps;
        public int FrameCount { get; private set; }
        public int Width { get; private set; }
        public int Height { get; private set; }
        public int Channels => ColorId >= 100 ? 3 : 1;
        public int BitDepth => PixelDepth;
        public string Path { get; }
        private int BytesPerPixel => (PixelDepth > 8 ? 2 : 1) * Channels;
        private long FrameBytes => (long)Width * Height * BytesPerPixel;

        public BayerPattern Bayer
        {
            get
            {
                switch (ColorId)
                {
                    case 8: return BayerPattern.RGGB;
                    case 9: return BayerPattern.GRBG;
                    case 10: return BayerPattern.GBRG;
                    case 11: return BayerPattern.BGGR;
                }
                return BayerPattern.None;
            }
        }

        public SerReader(string path)
        {
            Path = path;
            _fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
            var h = new byte[178];
            _fs.Read(h, 0, 178);
            string id = Encoding.ASCII.GetString(h, 0, 14);
            if (!id.StartsWith("LUCAM-RECORDER")) throw new InvalidDataException("Not a SER file");
            ColorId = BitConverter.ToInt32(h, 18);
            LittleEndian = BitConverter.ToInt32(h, 22);
            Width = BitConverter.ToInt32(h, 26);
            Height = BitConverter.ToInt32(h, 30);
            PixelDepth = BitConverter.ToInt32(h, 34);
            FrameCount = BitConverter.ToInt32(h, 38);
            Observer = Encoding.ASCII.GetString(h, 42, 40).TrimEnd('\0', ' ');
            Instrument = Encoding.ASCII.GetString(h, 82, 40).TrimEnd('\0', ' ');
            Telescope = Encoding.ASCII.GetString(h, 122, 40).TrimEnd('\0', ' ');
            long utc = BitConverter.ToInt64(h, 170);
            if (utc > 0) StartTimeUtc = TicksToDate(utc);
            long tsPos = 178 + FrameBytes * FrameCount;
            if (_fs.Length >= tsPos + 8L * FrameCount)
            {
                _fs.Position = tsPos;
                var tb = new byte[8 * FrameCount];
                _fs.Read(tb, 0, tb.Length);
                _timestamps = new long[FrameCount];
                for (int i = 0; i < FrameCount; i++) _timestamps[i] = BitConverter.ToInt64(tb, i * 8);
            }
        }

        private static DateTime? TicksToDate(long t)
        {
            try { return new DateTime(t, DateTimeKind.Utc); } catch { return null; }
        }

        public DateTime? Timestamp(int index) => _timestamps != null && index < _timestamps.Length && _timestamps[index] > 0 ? TicksToDate(_timestamps[index]) : null;

        /// <summary>Raw frame bytes (fast path for converters).</summary>
        public byte[] ReadRaw(int index)
        {
            var buf = new byte[FrameBytes];
            lock (_lock)
            {
                _fs.Position = 178 + FrameBytes * index;
                int r = 0;
                while (r < buf.Length) { int k = _fs.Read(buf, r, buf.Length - r); if (k <= 0) break; r += k; }
            }
            return buf;
        }

        public AstroImage ReadFrame(int index)
        {
            var buf = ReadRaw(index);
            int ch = Channels, n = Width * Height;
            var img = new AstroImage(Width, Height, ch) { Bayer = Bayer, OriginalBitDepth = PixelDepth };
            bool wide = PixelDepth > 8;
            // Spec says LittleEndian=0 means big endian, but most writers set 0 for little endian; follow common practice.
            bool little = LittleEndian == 0;
            double scale = 1.0 / ((1 << (wide ? Math.Min(PixelDepth, 16) : 8)) - 1);
            bool bgr = ColorId == 101;
            for (int i = 0; i < n; i++)
            {
                for (int c = 0; c < ch; c++)
                {
                    int s = (i * ch + c) * (wide ? 2 : 1);
                    double v = wide ? (little ? (buf[s] | (buf[s + 1] << 8)) : ((buf[s] << 8) | buf[s + 1])) : buf[s];
                    int dc = bgr ? 2 - c : c;
                    img.Data[dc][i] = (float)(v * scale);
                }
            }
            var ts = Timestamp(index);
            if (ts.HasValue) img.DateObs = ts;
            img.Header.Set("FRAMENO", index);
            return img;
        }

        public void Dispose() => _fs.Dispose();
    }

    /// <summary>SER writer (mono/Bayer/RGB, 8 or 16 bit) with timestamp trailer.</summary>
    public class SerWriter : IDisposable
    {
        private readonly FileStream _fs;
        private readonly List<long> _ts = new List<long>();
        private readonly int _w, _h, _depth, _colorId, _ch;
        private int _count;

        public SerWriter(string path, int width, int height, int bitDepth, int channels, BayerPattern bayer = BayerPattern.None, string observer = "", string instrument = "", string telescope = "")
        {
            _fs = File.Create(path);
            _w = width; _h = height; _depth = bitDepth > 8 ? 16 : 8; _ch = channels;
            _colorId = channels == 3 ? 100 : bayer == BayerPattern.RGGB ? 8 : bayer == BayerPattern.GRBG ? 9 : bayer == BayerPattern.GBRG ? 10 : bayer == BayerPattern.BGGR ? 11 : 0;
            var h = new byte[178];
            Encoding.ASCII.GetBytes("LUCAM-RECORDER").CopyTo(h, 0);
            BitConverter.GetBytes(0).CopyTo(h, 14);
            BitConverter.GetBytes(_colorId).CopyTo(h, 18);
            BitConverter.GetBytes(0).CopyTo(h, 22);
            BitConverter.GetBytes(_w).CopyTo(h, 26);
            BitConverter.GetBytes(_h).CopyTo(h, 30);
            BitConverter.GetBytes(_depth).CopyTo(h, 34);
            Encoding.ASCII.GetBytes(Fix(observer)).CopyTo(h, 42);
            Encoding.ASCII.GetBytes(Fix(instrument)).CopyTo(h, 82);
            Encoding.ASCII.GetBytes(Fix(telescope)).CopyTo(h, 122);
            BitConverter.GetBytes(DateTime.Now.Ticks).CopyTo(h, 162);
            BitConverter.GetBytes(DateTime.UtcNow.Ticks).CopyTo(h, 170);
            _fs.Write(h, 0, 178);
        }

        private static string Fix(string s) { s = s ?? ""; return s.Length > 40 ? s.Substring(0, 40) : s; }

        public void Write(AstroImage img, DateTime? utc = null)
        {
            int n = _w * _h, bpp = _depth > 8 ? 2 : 1;
            var buf = new byte[n * _ch * bpp];
            double max = _depth > 8 ? 65535 : 255;
            for (int i = 0; i < n; i++)
                for (int c = 0; c < _ch; c++)
                {
                    float f = img.Data[Math.Min(c, img.Channels - 1)][i];
                    int v = (int)Math.Max(0, Math.Min(max, Math.Round(f * max)));
                    int o = (i * _ch + c) * bpp;
                    if (bpp == 2) { buf[o] = (byte)v; buf[o + 1] = (byte)(v >> 8); } else buf[o] = (byte)v;
                }
            _fs.Write(buf, 0, buf.Length);
            _ts.Add((utc ?? img.DateObs ?? DateTime.UtcNow).Ticks);
            _count++;
        }

        public void Dispose()
        {
            foreach (var t in _ts) _fs.Write(BitConverter.GetBytes(t), 0, 8);
            _fs.Position = 38;
            _fs.Write(BitConverter.GetBytes(_count), 0, 4);
            _fs.Dispose();
        }
    }

    /// <summary>
    /// AVI (RIFF) reader for uncompressed RGB24/32, 8-bit grey (Y800/GREY/indexed), YUY2 and MJPEG streams,
    /// with OpenDML (AVIX) multi-RIFF support.
    /// </summary>
    public class AviReader : IFrameSource
    {
        private readonly FileStream _fs;
        private readonly object _lock = new object();
        private readonly List<long> _offsets = new List<long>();
        private readonly List<int> _sizes = new List<int>();
        private int _bpp;
        private string _fourcc = "";
        private bool _bottomUp = true;
        public double FramesPerSecond { get; private set; } = 30;
        public int FrameCount => _offsets.Count;
        public int Width { get; private set; }
        public int Height { get; private set; }
        public int Channels => _bpp == 8 || _fourcc == "Y800" || _fourcc == "GREY" || _fourcc == "Y8  " ? 1 : 3;
        public BayerPattern Bayer => BayerPattern.None;
        public int BitDepth => 8;
        public string Path { get; }
        public string Codec => _fourcc;

        public AviReader(string path)
        {
            Path = path;
            _fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
            var br = new BinaryReader(_fs);
            while (_fs.Position < _fs.Length - 12)
            {
                string riff = new string(br.ReadChars(4));
                uint size = br.ReadUInt32();
                string type = new string(br.ReadChars(4));
                if (riff != "RIFF") break;
                long end = _fs.Position - 4 + size;
                ParseList(br, end, type);
                _fs.Position = end + (size & 1);
            }
            if (_offsets.Count == 0) throw new InvalidDataException("No video frames found in AVI");
        }

        private void ParseList(BinaryReader br, long end, string listType)
        {
            while (_fs.Position < end - 8)
            {
                string id = new string(br.ReadChars(4));
                uint size = br.ReadUInt32();
                long next = _fs.Position + size + (size & 1);
                if (id == "LIST")
                {
                    string t = new string(br.ReadChars(4));
                    if (t == "movi") ParseMovi(br, _fs.Position - 4 + size);
                    else ParseList(br, _fs.Position - 4 + size, t);
                }
                else if (id == "avih")
                {
                    uint usPerFrame = br.ReadUInt32();
                    if (usPerFrame > 0) FramesPerSecond = 1e6 / usPerFrame;
                }
                else if (id == "strf" && Width == 0)
                {
                    br.ReadUInt32();
                    Width = br.ReadInt32();
                    int h = br.ReadInt32();
                    _bottomUp = h > 0;
                    Height = Math.Abs(h);
                    br.ReadUInt16();
                    _bpp = br.ReadUInt16();
                    _fourcc = new string(br.ReadChars(4));
                    if (_fourcc == "\0\0\0\0") _fourcc = "RAW ";
                }
                _fs.Position = next;
            }
        }

        private void ParseMovi(BinaryReader br, long end)
        {
            while (_fs.Position < end - 8)
            {
                string id = new string(br.ReadChars(4));
                uint size = br.ReadUInt32();
                if (id == "LIST") { br.ReadChars(4); continue; }
                if (id.Length == 4 && (id.EndsWith("db") || id.EndsWith("dc")) && id.StartsWith("00") && size > 0)
                {
                    _offsets.Add(_fs.Position);
                    _sizes.Add((int)size);
                }
                _fs.Position += size + (size & 1);
            }
        }

        public DateTime? Timestamp(int index) => null;

        public AstroImage ReadFrame(int index)
        {
            byte[] buf;
            lock (_lock)
            {
                _fs.Position = _offsets[index];
                buf = new byte[_sizes[index]];
                _fs.Read(buf, 0, buf.Length);
            }
            if (_fourcc == "MJPG" || (buf.Length > 2 && buf[0] == 0xFF && buf[1] == 0xD8))
            {
                using (var ms = new MemoryStream(buf))
                using (var bmp = new Bitmap(ms))
                    return BitmapIO.FromBitmap(bmp);
            }
            int w = Width, h = Height;
            var img = new AstroImage(w, h, Channels) { OriginalBitDepth = 8 };
            if (_fourcc == "YUY2" || _fourcc == "YUYV")
            {
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int o = (y * w + x) * 2;
                        float Y = buf[o] / 255f;
                        int ci = (y * w + (x & ~1)) * 2;
                        float U = buf[ci + 1] / 255f - 0.5f, V = buf[ci + 3] / 255f - 0.5f;
                        int i = y * w + x;
                        img.Data[0][i] = Math.Max(0, Y + 1.402f * V);
                        img.Data[1][i] = Math.Max(0, Y - 0.344f * U - 0.714f * V);
                        img.Data[2][i] = Math.Max(0, Y + 1.772f * U);
                    }
                return img;
            }
            int bytesPP = Math.Max(1, _bpp / 8);
            int stride = (w * bytesPP + 3) & ~3;
            if (Channels == 1 && _fourcc != "RAW " && _bpp != 8) stride = w;
            for (int y = 0; y < h; y++)
            {
                int srcRow = _bottomUp && (_fourcc == "RAW " || _fourcc == "DIB ") ? h - 1 - y : y;
                int o = srcRow * stride;
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    if (Channels == 1) img.Data[0][i] = buf[o + x] / 255f;
                    else
                    {
                        int p = o + x * bytesPP;
                        if (p + 2 >= buf.Length) continue;
                        img.Data[2][i] = buf[p] / 255f; img.Data[1][i] = buf[p + 1] / 255f; img.Data[0][i] = buf[p + 2] / 255f;
                    }
                }
            }
            return img;
        }

        public void Dispose() => _fs.Dispose();
    }

    /// <summary>Uncompressed AVI writer (8-bit grey with palette, or 24-bit RGB).</summary>
    public class AviWriter : IDisposable
    {
        private readonly FileStream _fs;
        private readonly BinaryWriter _bw;
        private readonly int _w, _h, _ch;
        private readonly double _fps;
        private readonly List<int> _index = new List<int>();
        private long _moviStart, _riffSizePos, _moviSizePos, _framesPos, _lengthPos;
        private int _frameBytes;

        public AviWriter(string path, int width, int height, int channels, double fps = 30)
        {
            _w = width; _h = height; _ch = channels >= 3 ? 3 : 1; _fps = fps;
            _fs = File.Create(path);
            _bw = new BinaryWriter(_fs);
            int stride = (_w * _ch + 3) & ~3;
            _frameBytes = stride * _h;
            W("RIFF"); _riffSizePos = _fs.Position; _bw.Write(0); W("AVI ");
            W("LIST"); long hdrl = _fs.Position; _bw.Write(0); W("hdrl");
            W("avih"); _bw.Write(56);
            _bw.Write((int)(1e6 / fps)); _bw.Write(_frameBytes * (int)fps); _bw.Write(0); _bw.Write(0x10);
            _framesPos = _fs.Position; _bw.Write(0); _bw.Write(0); _bw.Write(1); _bw.Write(_frameBytes); _bw.Write(_w); _bw.Write(_h);
            _bw.Write(0); _bw.Write(0); _bw.Write(0); _bw.Write(0);
            W("LIST"); long strl = _fs.Position; _bw.Write(0); W("strl");
            W("strh"); _bw.Write(56); W("vids"); W("DIB "); _bw.Write(0); _bw.Write(0); _bw.Write(0);
            _bw.Write(1000); _bw.Write((int)(fps * 1000)); _bw.Write(0); _lengthPos = _fs.Position; _bw.Write(0); _bw.Write(_frameBytes); _bw.Write(-1); _bw.Write(0);
            _bw.Write((short)0); _bw.Write((short)0); _bw.Write((short)_w); _bw.Write((short)_h);
            int palette = _ch == 1 ? 1024 : 0;
            W("strf"); _bw.Write(40 + palette);
            _bw.Write(40); _bw.Write(_w); _bw.Write(_h); _bw.Write((short)1); _bw.Write((short)(_ch * 8)); _bw.Write(0); _bw.Write(_frameBytes);
            _bw.Write(0); _bw.Write(0); _bw.Write(_ch == 1 ? 256 : 0); _bw.Write(0);
            if (_ch == 1) for (int i = 0; i < 256; i++) { _bw.Write((byte)i); _bw.Write((byte)i); _bw.Write((byte)i); _bw.Write((byte)0); }
            Patch(strl); Patch(hdrl);
            W("LIST"); _moviSizePos = _fs.Position; _bw.Write(0); _moviStart = _fs.Position; W("movi");
        }

        private void W(string s) => _bw.Write(Encoding.ASCII.GetBytes(s));

        private void Patch(long sizePos)
        {
            long cur = _fs.Position;
            _fs.Position = sizePos;
            _bw.Write((int)(cur - sizePos - 4));
            _fs.Position = cur;
        }

        public void Write(AstroImage img)
        {
            int stride = (_w * _ch + 3) & ~3;
            var buf = new byte[_frameBytes];
            for (int y = 0; y < _h; y++)
            {
                int row = (_h - 1 - y) * stride;
                for (int x = 0; x < _w; x++)
                {
                    int i = y * _w + x;
                    if (_ch == 1) buf[row + x] = B(img.Data[0][i]);
                    else
                    {
                        buf[row + x * 3] = B(img.Data[Math.Min(2, img.Channels - 1)][i]);
                        buf[row + x * 3 + 1] = B(img.Data[Math.Min(1, img.Channels - 1)][i]);
                        buf[row + x * 3 + 2] = B(img.Data[0][i]);
                    }
                }
            }
            _index.Add((int)(_fs.Position - _moviStart));
            W("00db"); _bw.Write(_frameBytes); _bw.Write(buf);
        }

        private static byte B(float v) => (byte)Math.Max(0, Math.Min(255, Math.Round(v * 255)));

        public void Dispose()
        {
            Patch(_moviSizePos);
            W("idx1"); _bw.Write(_index.Count * 16);
            foreach (var off in _index) { W("00db"); _bw.Write(0x10); _bw.Write(off); _bw.Write(_frameBytes); }
            Patch(_riffSizePos);
            _fs.Position = _framesPos; _bw.Write(_index.Count);
            _fs.Position = _lengthPos; _bw.Write(_index.Count);
            _bw.Flush();
            _fs.Dispose();
        }
    }

    /// <summary>A list of image files treated as a frame sequence.</summary>
    public class FileSequenceSource : IFrameSource
    {
        private readonly List<string> _files;
        private readonly AstroImage _first;
        public FileSequenceSource(IEnumerable<string> files)
        {
            _files = files.ToList();
            if (_files.Count == 0) throw new ArgumentException("No files");
            _first = ImageFile.Load(_files[0]);
        }
        public int FrameCount => _files.Count;
        public int Width => _first.Width;
        public int Height => _first.Height;
        public int Channels => _first.Channels;
        public BayerPattern Bayer => _first.Bayer;
        public int BitDepth => _first.OriginalBitDepth;
        public string Path => _files[0];
        public IReadOnlyList<string> Files => _files;
        public AstroImage ReadFrame(int index) => index == 0 ? _first.Clone() : ImageFile.Load(_files[index]);
        public DateTime? Timestamp(int index) => index == 0 ? _first.DateObs : ImageFile.Load(_files[index]).DateObs;
        public void Dispose() { }
    }

    public static class FrameSourceFactory
    {
        public static IFrameSource Open(string path)
        {
            string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".ser") return new SerReader(path);
            if (ext == ".avi") return new AviReader(path);
            return new FileSequenceSource(new[] { path });
        }

        public static IFrameSource Open(IList<string> paths)
        {
            if (paths.Count == 1) return Open(paths[0]);
            return new FileSequenceSource(paths);
        }
    }
}
