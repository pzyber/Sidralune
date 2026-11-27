using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Sidralune.Core.Mathematics;
using Sidralune.Core.Util;
using Sidralune.Imaging;
using Sidralune.Imaging.Analysis;
using Sidralune.Imaging.IO;
using Sidralune.Imaging.Stacking;

namespace Sidralune.Devices.Guiding
{
    /// <summary>What is done to guide frames before the star is measured.</summary>
    public enum DarkFrameKind { None, DarkLibrary, BadPixelMap }

    /// <summary>The guide camera as it was set up when dark frames were taken: they fit only the same setup.</summary>
    public class DarkSetup
    {
        public string Camera = "";
        public int Width, Height, Binning = 1, Gain = -1, Offset = -1;

        public bool Same(DarkSetup o) =>
            o != null && Camera == o.Camera && Width == o.Width && Height == o.Height && Binning == o.Binning && Gain == o.Gain && Offset == o.Offset;

        public override string ToString() => string.Format(CultureInfo.InvariantCulture, "{0} {1}×{2} bin {3} gain {4} offset {5}", Camera, Width, Height, Binning, Gain, Offset);
    }

    /// <summary>The dark library's index: the setup, and the exposures that have a master dark.</summary>
    public class DarkLibraryInfo
    {
        public DarkSetup Setup = new DarkSetup();
        public List<double> Exposures = new List<double>();
        public int FramesEach;
        public double Temperature = double.NaN;
        public DateTime When;
    }

    /// <summary>A bad-pixel map: the setup, and the hot and cold pixels (indices into the frame).</summary>
    public class BadPixelMapInfo
    {
        public DarkSetup Setup = new DarkSetup();
        public List<int> Pixels = new List<int>();
        public int Hot, Cold;
        public double ExposureSeconds, Sigma;
        public DateTime When;
    }

    /// <summary>
    /// The guide camera's dark library and bad-pixel map, kept per camera in a folder beside guider.json. A dark library
    /// is a median master dark for each guide exposure, subtracted from guide frames; a bad-pixel map lists the hot and cold
    /// pixels, which are replaced by the median of their neighbours. Either fits only the camera setup it was made with.
    /// </summary>
    public class GuideDarks
    {
        private readonly string _root;
        private readonly Dictionary<string, AstroImage> _masters = new Dictionary<string, AstroImage>();
        private readonly Dictionary<string, double> _pedestals = new Dictionary<string, double>();
        private string _camera;

        public DarkLibraryInfo Library { get; private set; }
        public BadPixelMapInfo Map { get; private set; }

        /// <param name="root">The folder that holds one folder per camera.</param>
        public GuideDarks(string root) { _root = root; }

        public string Folder(string camera) => Path.Combine(_root, Safe(camera));

        private static string Safe(string s)
        {
            var bad = Path.GetInvalidFileNameChars();
            var t = new string((s ?? "").Select(c => bad.Contains(c) || c == ' ' ? '_' : c).ToArray()).Trim('_');
            return t.Length == 0 ? "camera" : t;
        }

        private static string Key(double exposure) => exposure.ToString("0.###", CultureInfo.InvariantCulture);

        /// <summary>Reads what exists for a camera (when not read already).</summary>
        public void Load(string camera)
        {
            if (camera == _camera) return;
            _camera = camera;
            _masters.Clear(); _pedestals.Clear();
            Library = Read<DarkLibraryInfo>(Path.Combine(Folder(camera), "darks.json"));
            Map = Read<BadPixelMapInfo>(Path.Combine(Folder(camera), "bad pixels.json"));
        }

        /// <summary>Forgets what was read, so the next Load reads the files again (after building).</summary>
        public void Reload(string camera) { _camera = null; Load(camera); }

        private static T Read<T>(string path) where T : class
        {
            try { return File.Exists(path) ? Json.PopulateType(typeof(T), Json.Parse(File.ReadAllText(path))) as T : null; }
            catch (Exception ex) { Log.Warn("Guider", "Could not read " + path + ": " + ex.Message); return null; }
        }

        public bool LibraryFits(DarkSetup now) => Library != null && Library.Exposures.Count > 0 && Library.Setup.Same(now);
        public bool MapFits(DarkSetup now) => Map != null && Map.Setup.Same(now);

        /// <summary>The library's exposure closest to the one asked for (NaN when there is no library).</summary>
        public double Nearest(double exposure) =>
            Library == null || Library.Exposures.Count == 0 ? double.NaN : Library.Exposures.OrderBy(e => Math.Abs(Math.Log(e / Math.Max(1e-3, exposure)))).First();

        /// <summary>
        /// Treats a guide frame in place: subtracts the nearest master dark, or mends the bad pixels. Nothing is done when
        /// the kind is None or what was built does not fit this setup. Returns the exposure of the dark used, or NaN.
        /// </summary>
        public double Apply(AstroImage frame, DarkFrameKind kind, DarkSetup now, double exposure)
        {
            if (frame == null) return double.NaN;
            if (kind == DarkFrameKind.DarkLibrary && LibraryFits(now))
            {
                double e = Nearest(exposure);
                var master = Master(e);
                if (master == null || master.Width != frame.Width || master.Height != frame.Height) return double.NaN;
                Calibration.Subtract(frame, master, _pedestals[Key(e)]);
                return e;
            }
            if (kind == DarkFrameKind.BadPixelMap && MapFits(now)) Mend(frame, Map.Pixels);
            return double.NaN;
        }

        private AstroImage Master(double exposure)
        {
            string key = Key(exposure);
            AstroImage m;
            if (_masters.TryGetValue(key, out m)) return m;
            try
            {
                string path = Path.Combine(Folder(_camera), "dark " + key + " s.fits");
                m = File.Exists(path) ? FitsIO.Read(path) : null;
            }
            catch (Exception ex) { Log.Warn("Guider", "Could not read a master dark: " + ex.Message); m = null; }
            _masters[key] = m;
            // added back after subtracting, so the background stays where it was and nothing goes below zero
            _pedestals[key] = m != null ? ImageStatistics.Compute(m.Data[0], m.Width, 0).Median : 0;
            return m;
        }

        /// <summary>Replaces each listed pixel by the median of its neighbours that are not listed themselves.</summary>
        public static void Mend(AstroImage frame, IList<int> pixels)
        {
            int w = frame.Width, h = frame.Height;
            var bad = new HashSet<int>(pixels);
            var buf = new float[8];
            foreach (var d in frame.Data)
                foreach (int i in pixels)
                {
                    if (i < 0 || i >= d.Length) continue;
                    int x = i % w, y = i / w, n = 0;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            if (dx == 0 && dy == 0) continue;
                            int xx = x + dx, yy = y + dy;
                            if (xx < 0 || yy < 0 || xx >= w || yy >= h || bad.Contains(yy * w + xx)) continue;
                            buf[n++] = d[yy * w + xx];
                        }
                    if (n > 0) { Array.Sort(buf, 0, n); d[i] = n % 2 == 1 ? buf[n / 2] : 0.5f * (buf[n / 2 - 1] + buf[n / 2]); }
                }
        }

        /// <summary>The median, pixel by pixel, of dark frames of one size.</summary>
        public static AstroImage MedianOf(IList<AstroImage> frames)
        {
            var first = frames[0];
            var m = new AstroImage(first.Width, first.Height, 1) { Header = first.Header.Clone() };
            var opt = new CombineOptions { Method = CombineMethod.Median };
            var combiner = new PixelCombiner(frames.Count, opt, Enumerable.Repeat(1.0, frames.Count).ToArray());
            var buf = new float[frames.Count];
            var dst = m.Data[0];
            for (int i = 0; i < dst.Length; i++)
            {
                for (int k = 0; k < frames.Count; k++) buf[k] = frames[k].Data[0][i];
                dst[i] = combiner.Combine(buf, frames.Count);
            }
            m.ExposureSeconds = first.ExposureSeconds;
            m.Header.Set("IMAGETYP", "Master Dark");
            m.Header.Set("NCOMBINE", frames.Count);
            return m;
        }

        /// <summary>Saves a master dark into the camera's library and records it in the index.</summary>
        public void SaveMaster(DarkSetup setup, double exposure, AstroImage master, int framesEach, double temperature)
        {
            string dir = Folder(setup.Camera);
            Directory.CreateDirectory(dir);
            FitsIO.Write(Path.Combine(dir, "dark " + Key(exposure) + " s.fits"), master);
            var lib = Read<DarkLibraryInfo>(Path.Combine(dir, "darks.json"));
            // a library of another setup is replaced, not mixed
            if (lib == null || !lib.Setup.Same(setup)) lib = new DarkLibraryInfo { Setup = setup };
            if (!lib.Exposures.Any(e => Key(e) == Key(exposure))) lib.Exposures.Add(exposure);
            lib.Exposures.Sort();
            lib.FramesEach = framesEach; lib.Temperature = temperature; lib.When = DateTime.UtcNow;
            File.WriteAllText(Path.Combine(dir, "darks.json"), Json.Serialize(lib, true));
            Reload(setup.Camera);
        }

        /// <summary>Starts a new library for a setup: the old master darks of the camera go.</summary>
        public void ClearLibrary(string camera)
        {
            string dir = Folder(camera);
            if (Directory.Exists(dir))
            {
                foreach (var f in Directory.GetFiles(dir, "dark *.fits")) try { File.Delete(f); } catch { }
                try { File.Delete(Path.Combine(dir, "darks.json")); } catch { }
            }
            Reload(camera);
        }

        /// <summary>
        /// Finds the bad pixels in a master dark: hot ones more than sigma noise levels above the median, cold ones as far
        /// below it.
        /// </summary>
        public static BadPixelMapInfo FindBadPixels(AstroImage master, DarkSetup setup, double sigma)
        {
            var d = master.Data[0];
            var hot = Calibration.HotPixelMap(master, sigma);
            var st = ImageStatistics.Compute(d, master.Width, 0);
            double cold = st.Median - sigma * Math.Max(st.Mad * Stats.MadToSigma, 1e-7);
            var info = new BadPixelMapInfo { Setup = setup, ExposureSeconds = master.ExposureSeconds, Sigma = sigma, When = DateTime.UtcNow };
            for (int i = 0; i < d.Length; i++)
            {
                if (hot[i]) { info.Pixels.Add(i); info.Hot++; }
                else if (d[i] < cold) { info.Pixels.Add(i); info.Cold++; }
            }
            return info;
        }

        public void SaveMap(BadPixelMapInfo map)
        {
            string dir = Folder(map.Setup.Camera);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "bad pixels.json"), Json.Serialize(map, false));
            Reload(map.Setup.Camera);
        }
    }
}
