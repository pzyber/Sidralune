using System;
using System.IO;
using Sidralune.Core.Mathematics;

namespace Sidralune.Core.Ephemeris
{
    /// <summary>
    /// Global ephemeris selection: a JPL SPK kernel (DE440 etc.) when available, otherwise the analytic theory.
    /// All positions are ICRF equatorial, AU, relative to a common origin per call.
    /// </summary>
    public static class EphemerisManager
    {
        public static readonly AnalyticEphemeris Analytic = new AnalyticEphemeris();
        private static SpkEphemeris _spk;

        public static event EventHandler SourceChanged;

        public static IEphemerisSource Primary => (IEphemerisSource)_spk ?? Analytic;

        public static bool HasHighPrecision => _spk != null;

        public static void LoadSpk(string path)
        {
            var spk = new SpkEphemeris(path);
            var old = _spk;
            _spk = spk;
            if (old != null) old.Dispose();
            if (SourceChanged != null) SourceChanged(null, EventArgs.Empty);
        }

        public static void UnloadSpk()
        {
            var old = _spk;
            _spk = null; // new calculations use the analytic theory from now on
            if (old != null) old.Dispose();
            if (SourceChanged != null) SourceChanged(null, EventArgs.Empty);
        }

        /// <summary>
        /// Closes the loaded kernel if it is the given file, so that the file can be replaced or deleted (it is held open
        /// while loaded). Returns true if it was closed.
        /// </summary>
        public static bool Release(string path)
        {
            var spk = _spk;
            if (spk == null || !string.Equals(Path.GetFullPath(spk.FilePath), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)) return false;
            UnloadSpk();
            return true;
        }

        /// <summary>Try to auto-load a DE kernel from the data folder.</summary>
        public static void TryAutoLoad(string dataFolder)
        {
            try
            {
                if (!Directory.Exists(dataFolder)) return;
                foreach (var name in new[] { "de440s.bsp", "de440.bsp", "de441.bsp", "de430.bsp", "de421.bsp" })
                {
                    string p = Path.Combine(dataFolder, name);
                    if (File.Exists(p)) { LoadSpk(p); return; }
                }
            }
            catch { }
        }

        private static IEphemerisSource SourceFor(double jdTDB)
        {
            var spk = _spk; // once: another thread may release it
            if (spk != null && spk.Covers(jdTDB)) return spk;
            return Analytic;
        }

        // A kernel can be closed (replaced by a data update, or removed) while a calculation on another thread is using
        // it. Such a calculation is done again with the source in use now, rather than failing.

        /// <summary>Position/velocity of a body; both values refer to the same origin as GetEarth/GetSun for this jd.</summary>
        public static void State(Body b, double jdTDB, out Vec3 pos, out Vec3 vel)
        {
            var src = SourceFor(jdTDB);
            try { src.State(b, jdTDB, out pos, out vel); }
            catch (ObjectDisposedException) when (src != Analytic) { SourceFor(jdTDB).State(b, jdTDB, out pos, out vel); }
        }

        public static Vec3 Position(Body b, double jdTDB)
        {
            Vec3 p, v;
            State(b, jdTDB, out p, out v);
            return p;
        }

        /// <summary>Heliocentric ICRF position of a planet (AU).</summary>
        public static Vec3 Heliocentric(Body b, double jdTDB)
        {
            var src = SourceFor(jdTDB);
            try { return Heliocentric(src, b, jdTDB); }
            catch (ObjectDisposedException) when (src != Analytic) { return Heliocentric(SourceFor(jdTDB), b, jdTDB); }
        }

        // both bodies from the same source, so that they share an origin
        private static Vec3 Heliocentric(IEphemerisSource src, Body b, double jdTDB)
        {
            Vec3 p, v, s, sv;
            src.State(b, jdTDB, out p, out v);
            src.State(Body.Sun, jdTDB, out s, out sv);
            return p - s;
        }

        /// <summary>Heliocentric ICRF state of the Earth (AU, AU/day).</summary>
        public static void EarthHeliocentric(double jdTDB, out Vec3 pos, out Vec3 vel)
        {
            var src = SourceFor(jdTDB);
            try { EarthHeliocentric(src, jdTDB, out pos, out vel); }
            catch (ObjectDisposedException) when (src != Analytic) { EarthHeliocentric(SourceFor(jdTDB), jdTDB, out pos, out vel); }
        }

        private static void EarthHeliocentric(IEphemerisSource src, double jdTDB, out Vec3 pos, out Vec3 vel)
        {
            Vec3 p, v, s, sv;
            src.State(Body.Earth, jdTDB, out p, out v);
            src.State(Body.Sun, jdTDB, out s, out sv);
            pos = p - s; vel = v - sv;
        }

        /// <summary>Barycentric velocity of Earth (AU/day) for aberration; heliocentric when only analytic data is loaded.</summary>
        public static Vec3 EarthVelocityForAberration(double jdTDB)
        {
            Vec3 p, v;
            State(Body.Earth, jdTDB, out p, out v);
            return v;
        }

        public static string Describe() => Primary.Name + " — " + Primary.Accuracy;
    }
}
