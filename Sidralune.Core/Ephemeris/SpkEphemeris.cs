using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Sidralune.Core.Mathematics;

namespace Sidralune.Core.Ephemeris
{
    /// <summary>
    /// Reader for NAIF SPK (DAF) binary kernels such as JPL DE440/DE441 (de440s.bsp) and small-body SPKs from Horizons.
    /// Supports Chebyshev segment types 2 (position) and 3 (position+velocity). Origin: Solar System Barycenter.
    /// </summary>
    public class SpkEphemeris : IEphemerisSource, IDisposable
    {
        private class Segment
        {
            public double StartEt, EndEt;
            public int Target, Center, Frame, Type, StartAddr, EndAddr;
            public double Init, IntLen;
            public int RSize, NRecords;
        }

        private readonly FileStream _fs;
        private readonly BinaryReader _br;
        private readonly bool _bigEndian;
        private readonly List<Segment> _segments = new List<Segment>();
        private readonly object _lock = new object();
        private readonly Dictionary<long, double[]> _cache = new Dictionary<long, double[]>();
        public string FilePath { get; }

        public string Name => "JPL SPK: " + Path.GetFileName(FilePath);
        public string Accuracy => "milliarcsecond (JPL DE)";

        public SpkEphemeris(string path)
        {
            FilePath = path;
            _fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            _br = new BinaryReader(_fs);
            var rec = _br.ReadBytes(1024);
            string idword = Encoding.ASCII.GetString(rec, 0, 8);
            if (!idword.StartsWith("DAF/")) throw new InvalidDataException("Not a DAF/SPK file");
            string fmt = Encoding.ASCII.GetString(rec, 88, 8);
            _bigEndian = fmt.StartsWith("BIG-IEEE");
            int nd = ReadInt(rec, 8), ni = ReadInt(rec, 12);
            int fward = ReadInt(rec, 76);
            int ss = nd + (ni + 1) / 2;
            int recNo = fward;
            while (recNo > 0)
            {
                var srec = ReadRecord(recNo);
                double next = ReadDouble(srec, 0);
                int nsum = (int)ReadDouble(srec, 16);
                for (int i = 0; i < nsum; i++)
                {
                    int off = 24 + i * ss * 8;
                    var seg = new Segment
                    {
                        StartEt = ReadDouble(srec, off),
                        EndEt = ReadDouble(srec, off + 8),
                    };
                    int ioff = off + nd * 8;
                    seg.Target = ReadInt(srec, ioff);
                    seg.Center = ReadInt(srec, ioff + 4);
                    seg.Frame = ReadInt(srec, ioff + 8);
                    seg.Type = ReadInt(srec, ioff + 12);
                    seg.StartAddr = ReadInt(srec, ioff + 16);
                    seg.EndAddr = ReadInt(srec, ioff + 20);
                    if (seg.Type == 2 || seg.Type == 3)
                    {
                        var tail = ReadDoubles(seg.EndAddr - 3, 4);
                        seg.Init = tail[0]; seg.IntLen = tail[1]; seg.RSize = (int)tail[2]; seg.NRecords = (int)tail[3];
                        _segments.Add(seg);
                    }
                }
                recNo = (int)next;
            }
        }

        public IEnumerable<string> Describe()
        {
            foreach (var s in _segments)
                yield return string.Format("{0} wrt {1}: JD {2:F1} - {3:F1} (type {4})", s.Target, s.Center,
                    2451545.0 + s.StartEt / 86400.0, 2451545.0 + s.EndEt / 86400.0, s.Type);
        }

        public bool Covers(double jdTDB)
        {
            double et = (jdTDB - 2451545.0) * 86400.0;
            foreach (var s in _segments) if (s.Target == 3 && et >= s.StartEt && et <= s.EndEt) return true;
            return false;
        }

        public bool HasTarget(int naifId)
        {
            foreach (var s in _segments) if (s.Target == naifId) return true;
            return false;
        }

        private byte[] ReadRecord(int recNo)
        {
            lock (_lock)
            {
                _fs.Position = (long)(recNo - 1) * 1024;
                return _br.ReadBytes(1024);
            }
        }

        private double[] ReadDoubles(int addr, int count)
        {
            byte[] b;
            lock (_lock)
            {
                _fs.Position = (long)(addr - 1) * 8;
                b = _br.ReadBytes(count * 8);
            }
            var r = new double[count];
            for (int i = 0; i < count; i++) r[i] = ReadDouble(b, i * 8);
            return r;
        }

        private int ReadInt(byte[] b, int off)
        {
            if (_bigEndian) return (b[off] << 24) | (b[off + 1] << 16) | (b[off + 2] << 8) | b[off + 3];
            return BitConverter.ToInt32(b, off);
        }

        private double ReadDouble(byte[] b, int off)
        {
            if (!_bigEndian) return BitConverter.ToDouble(b, off);
            var t = new byte[8];
            for (int i = 0; i < 8; i++) t[i] = b[off + 7 - i];
            return BitConverter.ToDouble(t, 0);
        }

        /// <summary>State (km, km/s) of target relative to its segment center.</summary>
        private bool SegmentState(int target, int center, double et, out Vec3 pos, out Vec3 vel)
        {
            pos = vel = Vec3.Zero;
            Segment seg = null;
            foreach (var s in _segments)
                if (s.Target == target && s.Center == center && et >= s.StartEt && et <= s.EndEt) { seg = s; break; }
            if (seg == null) return false;
            int idx = (int)Math.Floor((et - seg.Init) / seg.IntLen);
            if (idx >= seg.NRecords) idx = seg.NRecords - 1;
            if (idx < 0) idx = 0;
            long key = ((long)seg.StartAddr << 20) ^ idx;
            double[] rec;
            lock (_lock)
            {
                if (!_cache.TryGetValue(key, out rec))
                {
                    if (_cache.Count > 4096) _cache.Clear();
                    rec = null;
                }
            }
            if (rec == null)
            {
                rec = ReadDoubles(seg.StartAddr + idx * seg.RSize, seg.RSize);
                lock (_lock) _cache[key] = rec;
            }
            double mid = rec[0], radius = rec[1];
            int ncomp = seg.Type == 2 ? 3 : 6;
            int ncoef = (seg.RSize - 2) / ncomp;
            double tau = (et - mid) / radius;
            var T = new double[ncoef];
            var dT = new double[ncoef];
            T[0] = 1; if (ncoef > 1) T[1] = tau;
            dT[0] = 0; if (ncoef > 1) dT[1] = 1;
            for (int k = 2; k < ncoef; k++)
            {
                T[k] = 2 * tau * T[k - 1] - T[k - 2];
                dT[k] = 2 * T[k - 1] + 2 * tau * dT[k - 1] - dT[k - 2];
            }
            var p = new double[3];
            var v = new double[3];
            for (int c = 0; c < 3; c++)
            {
                double sp = 0, sv = 0;
                int o = 2 + c * ncoef;
                for (int k = 0; k < ncoef; k++) { sp += rec[o + k] * T[k]; sv += rec[o + k] * dT[k]; }
                p[c] = sp; v[c] = sv / radius;
            }
            if (seg.Type == 3)
                for (int c = 0; c < 3; c++)
                {
                    double sv = 0;
                    int o = 2 + (3 + c) * ncoef;
                    for (int k = 0; k < ncoef; k++) sv += rec[o + k] * T[k];
                    v[c] = sv;
                }
            pos = new Vec3(p[0], p[1], p[2]);
            vel = new Vec3(v[0], v[1], v[2]);
            return true;
        }

        /// <summary>State of a NAIF id relative to the SSB (km, km/s), chaining through segment centers.</summary>
        public bool BarycentricState(int naifId, double et, out Vec3 pos, out Vec3 vel)
        {
            pos = vel = Vec3.Zero;
            int cur = naifId;
            int guard = 0;
            while (cur != 0 && guard++ < 10)
            {
                Segment seg = null;
                foreach (var s in _segments)
                    if (s.Target == cur && et >= s.StartEt && et <= s.EndEt) { seg = s; break; }
                if (seg == null) return false;
                Vec3 p, v;
                if (!SegmentState(cur, seg.Center, et, out p, out v)) return false;
                pos = pos + p; vel = vel + v;
                cur = seg.Center;
            }
            return true;
        }

        public static int NaifId(Body b)
        {
            switch (b)
            {
                case Body.Sun: return 10;
                case Body.Mercury: return 1;
                case Body.Venus: return 2;
                case Body.Earth: return 399;
                case Body.EarthMoonBarycenter: return 3;
                case Body.Moon: return 301;
                case Body.Mars: return 4;
                case Body.Jupiter: return 5;
                case Body.Saturn: return 6;
                case Body.Uranus: return 7;
                case Body.Neptune: return 8;
                case Body.Pluto: return 9;
            }
            return 0;
        }

        public void State(Body body, double jdTDB, out Vec3 pos, out Vec3 vel)
        {
            double et = (jdTDB - 2451545.0) * 86400.0;
            Vec3 p, v;
            if (!BarycentricState(NaifId(body), et, out p, out v)) throw new ArgumentOutOfRangeException("jdTDB", "Outside SPK coverage");
            pos = p / AstroMath.AU_km;
            vel = v * (86400.0 / AstroMath.AU_km);
        }

        public void Dispose()
        {
            _br.Dispose();
            _fs.Dispose();
        }
    }
}
