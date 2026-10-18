using System;
using System.IO;

namespace Sidralune.Imaging.IO
{
    /// <summary>
    /// ITU T.81 lossless JPEG (SOF3, Huffman, predictors 1-7, restart markers) decoder as used by DNG and Canon CR2.
    /// Output is an interleaved ushort array of width*components samples per row.
    /// </summary>
    public class LosslessJpeg
    {
        public int Width, Height, Components, Precision;
        public ushort[] Samples;

        private class Huff
        {
            public int[] MaxCode = new int[18];
            public int[] ValPtr = new int[17];
            public int[] MinCode = new int[17];
            public byte[] Vals;
            // fast lookup 9 bits
            public short[] Look = new short[512];
            public byte[] LookLen = new byte[512];
        }

        private readonly Huff[] _tables = new Huff[4];
        private byte[] _d;
        private int _pos;
        private uint _bitBuf;
        private int _bitCnt;
        private bool _hitMarker;

        public static LosslessJpeg Decode(byte[] data, int offset = 0, int length = -1)
        {
            var j = new LosslessJpeg();
            j.Run(data, offset, length < 0 ? data.Length - offset : length);
            return j;
        }

        private void Run(byte[] data, int off, int len)
        {
            _d = data;
            _pos = off;
            int end = off + len;
            if (data[_pos] != 0xFF || data[_pos + 1] != 0xD8) throw new InvalidDataException("Not a JPEG stream");
            _pos += 2;
            int restartInterval = 0;
            int[] compTable = new int[4];
            int predictor = 1, pt = 0;
            while (_pos < end)
            {
                if (data[_pos] != 0xFF) { _pos++; continue; }
                int marker = data[_pos + 1];
                _pos += 2;
                if (marker == 0xD8 || (marker >= 0xD0 && marker <= 0xD7)) continue;
                if (marker == 0xD9) break;
                int segLen = (data[_pos] << 8) | data[_pos + 1];
                int seg = _pos + 2;
                switch (marker)
                {
                    case 0xC3: // SOF3
                        Precision = data[seg];
                        Height = (data[seg + 1] << 8) | data[seg + 2];
                        Width = (data[seg + 3] << 8) | data[seg + 4];
                        Components = data[seg + 5];
                        break;
                    case 0xC4: // DHT
                        {
                            int p = seg;
                            while (p < _pos + segLen)
                            {
                                int tc = data[p] & 0x0F;
                                var counts = new int[17];
                                int total = 0;
                                for (int i = 1; i <= 16; i++) { counts[i] = data[p + i]; total += counts[i]; }
                                var h = new Huff { Vals = new byte[total] };
                                Array.Copy(data, p + 17, h.Vals, 0, total);
                                BuildTable(h, counts);
                                _tables[tc] = h;
                                p += 17 + total;
                            }
                            break;
                        }
                    case 0xDD:
                        restartInterval = (data[seg] << 8) | data[seg + 1];
                        break;
                    case 0xDA: // SOS
                        {
                            int ns = data[seg];
                            for (int i = 0; i < ns; i++) compTable[i] = data[seg + 2 + i * 2] >> 4;
                            int q = seg + 1 + ns * 2;
                            predictor = data[q];
                            pt = data[q + 2] & 0x0F;
                            _pos += segLen;
                            DecodeScan(compTable, predictor, pt, restartInterval);
                            return;
                        }
                }
                _pos += segLen;
            }
        }

        private static void BuildTable(Huff h, int[] counts)
        {
            int code = 0, k = 0;
            for (int l = 1; l <= 16; l++)
            {
                h.ValPtr[l] = k;
                h.MinCode[l] = code;
                code += counts[l];
                k += counts[l];
                h.MaxCode[l] = counts[l] > 0 ? code - 1 : -1;
                code <<= 1;
            }
            h.MaxCode[17] = int.MaxValue;
            for (int i = 0; i < 512; i++) h.LookLen[i] = 0;
            code = 0; k = 0;
            for (int l = 1; l <= 9; l++)
            {
                for (int c = 0; c < counts[l]; c++)
                {
                    int shift = 9 - l;
                    for (int fill = 0; fill < (1 << shift); fill++)
                    {
                        int idx = (code << shift) | fill;
                        h.Look[idx] = h.Vals[k];
                        h.LookLen[idx] = (byte)l;
                    }
                    code++; k++;
                }
                code <<= 1;
            }
        }

        private void Fill()
        {
            while (_bitCnt <= 24)
            {
                int b = 0;
                if (!_hitMarker && _pos < _d.Length)
                {
                    b = _d[_pos];
                    if (b == 0xFF)
                    {
                        int n = _pos + 1 < _d.Length ? _d[_pos + 1] : 0;
                        if (n == 0x00) _pos += 2;
                        else { _hitMarker = true; b = 0; }
                    }
                    else _pos++;
                }
                _bitBuf |= (uint)b << (24 - _bitCnt);
                _bitCnt += 8;
            }
        }

        private int GetBits(int n)
        {
            if (n == 0) return 0;
            Fill();
            int v = (int)(_bitBuf >> (32 - n));
            _bitBuf <<= n;
            _bitCnt -= n;
            return v;
        }

        private int DecodeHuff(Huff h)
        {
            Fill();
            int look = (int)(_bitBuf >> 23);
            int len = h.LookLen[look];
            if (len > 0) { _bitBuf <<= len; _bitCnt -= len; return h.Look[look]; }
            int code = GetBits(1), l = 1;
            while (code > h.MaxCode[l]) { code = (code << 1) | GetBits(1); l++; if (l > 16) return 0; }
            return h.Vals[h.ValPtr[l] + code - h.MinCode[l]];
        }

        private int Diff(Huff h)
        {
            int s = DecodeHuff(h);
            if (s == 0) return 0;
            if (s == 16) return -32768;
            int v = GetBits(s);
            if (v < (1 << (s - 1))) v -= (1 << s) - 1;
            return v;
        }

        private void DecodeScan(int[] compTable, int predictor, int pt, int restart)
        {
            int nc = Components, w = Width, h = Height;
            Samples = new ushort[w * h * nc];
            int initial = 1 << (Precision - pt - 1);
            int mcu = 0;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    if (restart > 0 && mcu > 0 && mcu % restart == 0)
                    {
                        // re-sync: skip to RST marker
                        _bitBuf = 0; _bitCnt = 0; _hitMarker = false;
                        while (_pos + 1 < _d.Length && !(_d[_pos] == 0xFF && _d[_pos + 1] >= 0xD0 && _d[_pos + 1] <= 0xD7)) _pos++;
                        _pos += 2;
                    }
                    for (int c = 0; c < nc; c++)
                    {
                        int pred;
                        int idx = (y * w + x) * nc + c;
                        bool restartRow = restart > 0 && mcu % restart == 0 && false;
                        if (x == 0 && y == 0 || restartRow) pred = initial;
                        else if (y == 0) pred = Samples[idx - nc];
                        else if (x == 0) pred = Samples[idx - w * nc];
                        else
                        {
                            int Ra = Samples[idx - nc], Rb = Samples[idx - w * nc], Rc = Samples[idx - w * nc - nc];
                            switch (predictor)
                            {
                                case 1: pred = Ra; break;
                                case 2: pred = Rb; break;
                                case 3: pred = Rc; break;
                                case 4: pred = Ra + Rb - Rc; break;
                                case 5: pred = Ra + ((Rb - Rc) >> 1); break;
                                case 6: pred = Rb + ((Ra - Rc) >> 1); break;
                                case 7: pred = (Ra + Rb) >> 1; break;
                                default: pred = Ra; break;
                            }
                        }
                        var tbl = _tables[compTable[c]] ?? _tables[0];
                        int v = (pred + Diff(tbl)) & 0xFFFF;
                        Samples[idx] = (ushort)(v << pt);
                    }
                    mcu++;
                }
            }
        }
    }
}
