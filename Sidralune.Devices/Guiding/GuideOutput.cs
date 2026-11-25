using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Sidralune.Core.Util;

namespace Sidralune.Devices.Guiding
{
    /// <summary>
    /// Sends guide pulses through the mount's driver or the guide camera's ST-4 port and waits until each is over:
    /// some drivers return at once and report IsPulseGuiding, others return when the pulse has ended.
    /// </summary>
    internal class GuidePulser
    {
        private readonly DeviceManager _dm;
        public GuideOutputKind Output;

        public GuidePulser(DeviceManager dm, GuideOutputKind output) { _dm = dm; Output = output; }

        /// <summary>Why pulses cannot be sent now, or null when they can.</summary>
        public string Problem()
        {
            if (Output == GuideOutputKind.Camera)
            {
                var cam = _dm.GuideCamera;
                if (cam == null || !cam.Connected) return Lang.T("No guide camera connected.");
                if (!cam.CanPulseGuide) return Lang.T("The guide camera has no guide port. Guide through the mount instead.");
                return null;
            }
            var tel = _dm.Telescope;
            if (tel == null || !tel.Connected) return Lang.T("No telescope connected.");
            if (!tel.CanPulseGuide) return Lang.T("The mount cannot take guide pulses. Guide through the guide camera's port instead.");
            return null;
        }

        private bool Busy() => Output == GuideOutputKind.Camera ? _dm.GuideCamera?.IsPulseGuiding == true : _dm.Telescope?.IsPulseGuiding == true;

        /// <summary>Starts a pulse without waiting for it (an AO bump: AO frames go on meanwhile).</summary>
        public void Start(GuideDirection dir, int ms)
        {
            if (ms <= 0) return;
            var tel = _dm.Telescope;
            if (tel != null && tel.Slewing) return;
            if (Output == GuideOutputKind.Camera) _dm.GuideCamera.PulseGuide(dir, ms);
            else tel.PulseGuide(dir, ms);
        }

        /// <summary>One pulse, waited for. Nothing is sent while the mount slews.</summary>
        public async Task Pulse(GuideDirection dir, int ms, CancellationToken ct)
        {
            if (ms <= 0) return;
            var tel = _dm.Telescope;
            if (tel != null && tel.Slewing) return;
            var sw = Stopwatch.StartNew();
            if (Output == GuideOutputKind.Camera) _dm.GuideCamera.PulseGuide(dir, ms);
            else tel.PulseGuide(dir, ms);
            int left = ms - (int)sw.ElapsedMilliseconds;
            if (left > 0) await Task.Delay(left, ct);
            var until = DateTime.UtcNow.AddSeconds(2);
            while (Busy() && DateTime.UtcNow < until) await Task.Delay(10, ct);
        }
    }

    /// <summary>The guide log: one CSV file per guiding session, a row for each frame and each event.</summary>
    internal class GuideLog : IDisposable
    {
        private StreamWriter _w;
        public string Path { get; private set; }

        public void Open(string folder)
        {
            if (_w != null) return;
            try
            {
                Path = System.IO.Path.Combine(folder ?? AppPaths.Logs, "Guide " + DateTime.Now.ToString("yyyy-MM-dd HHmmss", CultureInfo.InvariantCulture) + ".csv");
                _w = new StreamWriter(Path, true) { AutoFlush = true };
                _w.WriteLine("Time,Event,X,Y,Offset X,Offset Y,RA px,Dec px,RA ms,Dec ms,SNR,Flux,HFD,Stars,Note");
            }
            catch (Exception ex) { Log.Warn("Guider", "Could not open the guide log: " + ex.Message); _w = null; }
        }

        public void Row(string ev, GuideStarInfo star, double ox, double oy, double ra, double dec, int raMs, int decMs, string note = "", int stars = 0)
        {
            if (_w == null) return;
            var cells = new object[]
            {
                DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture), ev,
                star != null ? (object)Math.Round(star.X, 3) : "", star != null ? (object)Math.Round(star.Y, 3) : "",
                Num(ox), Num(oy), Num(ra), Num(dec), raMs, decMs,
                star != null ? (object)Math.Round(star.Snr, 1) : "", star != null ? (object)star.Flux.ToString("G5", CultureInfo.InvariantCulture) : "", star != null ? (object)Math.Round(star.Hfd, 2) : "",
                stars > 0 ? (object)stars : "", note
            };
            try { lock (this) _w.WriteLine(string.Join(",", Array.ConvertAll(cells, c => CsvWriter.Escape(CsvWriter.Format(c))))); } catch { }
        }

        public void Event(string ev, string note) => Row(ev, null, double.NaN, double.NaN, double.NaN, double.NaN, 0, 0, note);

        private static object Num(double v) => double.IsNaN(v) ? (object)"" : Math.Round(v, 3);

        public void Dispose() { try { _w?.Dispose(); } catch { } _w = null; }
    }
}
