using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Sidralune.Core.Mathematics;
using Sidralune.Science.Photometry;

namespace Sidralune.Devices.Guiding
{
    /// <summary>One guide step read from a guide log: the star's offset along RA and Dec (pixels) and the pulses sent (ms, East and North positive).</summary>
    public class GuideLogStep
    {
        public DateTime Time;
        public double RaPx, DecPx, RaMs, DecMs;
        public double Snr = double.NaN;
        /// <summary>Taken while settling after a dither: left out of the statistics.</summary>
        public bool Settling;
    }

    /// <summary>Something that happened while guiding: a dither, a lost star, a plate solve.</summary>
    public class GuideLogEvent
    {
        public DateTime Time;
        public string Name, Note;
    }

    /// <summary>Guiding over a stretch of time: RMS per axis and in total, and the largest error (pixels).</summary>
    public class GuideLogStats
    {
        public DateTime Start, End;
        public int Steps;
        public double RmsRa = double.NaN, RmsDec = double.NaN, RmsTotal = double.NaN, Peak = double.NaN;

        public static GuideLogStats Of(IList<GuideLogStep> steps)
        {
            var s = new GuideLogStats { Steps = steps.Count };
            if (steps.Count == 0) return s;
            s.Start = steps[0].Time; s.End = steps[steps.Count - 1].Time;
            s.RmsRa = Math.Sqrt(steps.Average(t => t.RaPx * t.RaPx));
            s.RmsDec = Math.Sqrt(steps.Average(t => t.DecPx * t.DecPx));
            s.RmsTotal = Math.Sqrt(s.RmsRa * s.RmsRa + s.RmsDec * s.RmsDec);
            s.Peak = steps.Max(t => Math.Sqrt(t.RaPx * t.RaPx + t.DecPx * t.DecPx));
            return s;
        }
    }

    /// <summary>One guiding session in a guide log: from guiding starting until it stops.</summary>
    public class GuideLogSession
    {
        public DateTime Start;
        /// <summary>Arcseconds per pixel, and the worm period (s), when the log says; NaN otherwise.</summary>
        public double ScaleArcsec = double.NaN, WormPeriodSec = double.NaN;
        /// <summary>
        /// How fast an RA pulse moves the star while guiding (pixels per second of pulse; an East pulse moves it back
        /// against a positive RA offset), from the calibration in the log; NaN when the log does not say.
        /// </summary>
        public double RaRatePxPerSec = double.NaN;
        public readonly List<GuideLogStep> Steps = new List<GuideLogStep>();
        public readonly List<GuideLogEvent> Events = new List<GuideLogEvent>();
        /// <summary>The calibration it guided with, as the log describes it; null when the log does not say.</summary>
        public string Calibration;
        /// <summary>The calibration's steps: the leg (West, North, …) and the star's offset from where it began (pixels).</summary>
        public readonly List<Tuple<string, double, double>> CalibrationSteps = new List<Tuple<string, double, double>>();

        /// <summary>The whole session, settling left out.</summary>
        public GuideLogStats Total => GuideLogStats.Of(Steps.Where(s => !s.Settling).ToList());

        /// <summary>
        /// The stretches between dithers, settling left out: with dithering between frames, one for each frame taken.
        /// Without dithers, the session as one stretch.
        /// </summary>
        public List<GuideLogStats> Frames()
        {
            var cuts = Events.Where(e => e.Name == "Dither").Select(e => e.Time).OrderBy(t => t).ToList();
            var list = new List<GuideLogStats>();
            var part = new List<GuideLogStep>();
            int c = 0;
            foreach (var s in Steps)
            {
                bool cut = false;
                while (c < cuts.Count && cuts[c] <= s.Time) { c++; cut = true; }
                if (cut && part.Count > 0) { list.Add(GuideLogStats.Of(part)); part = new List<GuideLogStep>(); }
                if (!s.Settling) part.Add(s);
            }
            if (part.Count > 0) list.Add(GuideLogStats.Of(part));
            return list;
        }
    }

    /// <summary>
    /// A guide log read back: Sidralune's own (CSV, one row per frame and event) or PHD2's (PHD2_GuideLog_*.txt), split into
    /// its guiding sessions.
    /// </summary>
    public class GuideLogFile
    {
        public string Path, Format;
        public readonly List<GuideLogSession> Sessions = new List<GuideLogSession>();

        public static GuideLogFile Read(string path)
        {
            string[] lines;
            using (var rd = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)))
                lines = rd.ReadToEnd().Replace("\r", "").Split('\n');
            var f = new GuideLogFile { Path = path };
            var first = lines.FirstOrDefault(l => l.Trim().Length > 0) ?? "";
            if (first.StartsWith("Time,Event,", StringComparison.Ordinal)) { f.Format = "Sidralune"; f.ReadSidralune(lines); }
            else if (lines.Any(l => l.StartsWith("Guiding Begins at", StringComparison.Ordinal) || l.StartsWith("PHD2 version", StringComparison.Ordinal))) { f.Format = "PHD2"; f.ReadPhd2(lines); }
            else throw new InvalidDataException(Core.Util.Lang.T("Not a guide log from Sidralune or PHD2."));
            f.Sessions.RemoveAll(s => s.Steps.Count == 0);
            return f;
        }

        private static string[] SplitCsv(string line)
        {
            using (var r = new Core.Util.CsvReader(new StringReader(line), ',', false)) return r.ReadRow() ?? new string[0];
        }

        // ------------------------------------------------------------------ Sidralune

        private static readonly Regex Scale = new Regex(@"scale ([0-9.]+)""/px", RegexOptions.CultureInvariant);
        private static readonly Regex Worm = new Regex(@"worm period ([0-9.]+) s", RegexOptions.CultureInvariant);
        private static readonly Regex RaRate = new Regex(@"RA rate ([0-9.]+) px/s", RegexOptions.CultureInvariant);
        private static readonly Regex DecLine = new Regex(@"^RA = [-0-9.]+ hr, Dec = ([-0-9.]+) deg", RegexOptions.CultureInvariant);

        private void ReadSidralune(string[] lines)
        {
            // the date is in the file name ("Guide 2026-11-26 181535.csv"); the rows hold the time of day
            var m = Regex.Match(System.IO.Path.GetFileNameWithoutExtension(Path) ?? "", @"(\d{4}-\d{2}-\d{2})");
            DateTime day;
            if (!m.Success || !DateTime.TryParseExact(m.Groups[1].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day)) day = File.GetCreationTime(Path).Date;
            TimeSpan last = TimeSpan.Zero;
            GuideLogSession cur = null;
            string calibration = null;
            var calSteps = new List<Tuple<string, double, double>>();
            double calX = double.NaN, calY = double.NaN, scale = double.NaN, worm = double.NaN, raRate = double.NaN;
            bool settling = false;
            foreach (var line in lines.Skip(1))
            {
                if (line.Trim().Length == 0) continue;
                var c = SplitCsv(line);
                if (c.Length < 10) continue;
                TimeSpan tod;
                if (!TimeSpan.TryParseExact(c[0], @"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture, out tod)) continue;
                if (tod < last - TimeSpan.FromHours(1)) day = day.AddDays(1); // past midnight
                last = tod;
                var t = day + tod;
                string ev = c[1], note = c.Length > 14 ? c[14] : "";
                Func<int, double> num = i => { double v; return i < c.Length && double.TryParse(c[i], NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : double.NaN; };
                if (ev.StartsWith("Calibration ", StringComparison.Ordinal) || ev == "Backlash")
                {
                    double x = num(2), y = num(3);
                    if (double.IsNaN(calX)) { calX = x; calY = y; calSteps.Clear(); }
                    if (!double.IsNaN(x)) calSteps.Add(Tuple.Create(ev == "Backlash" ? "North" : ev.Substring(12), x - calX, y - calY));
                    continue;
                }
                switch (ev)
                {
                    case "Calibration":
                        if (note == "started") { calX = calY = double.NaN; calSteps.Clear(); }
                        break;
                    case "Calibrated":
                        calibration = note;
                        break;
                    case "Settings":
                        var sm = Scale.Match(note); if (sm.Success) scale = double.Parse(sm.Groups[1].Value, CultureInfo.InvariantCulture);
                        var wm = Worm.Match(note); if (wm.Success) worm = double.Parse(wm.Groups[1].Value, CultureInfo.InvariantCulture);
                        var rm = RaRate.Match(note); raRate = rm.Success ? double.Parse(rm.Groups[1].Value, CultureInfo.InvariantCulture) : double.NaN;
                        break;
                    case "Guiding":
                        cur = new GuideLogSession { Start = t, ScaleArcsec = scale, WormPeriodSec = worm, RaRatePxPerSec = raRate, Calibration = calibration };
                        cur.CalibrationSteps.AddRange(calSteps);
                        Sessions.Add(cur);
                        settling = false;
                        break;
                    case "Guide":
                        if (cur == null) break;
                        cur.Steps.Add(new GuideLogStep { Time = t, RaPx = num(6), DecPx = num(7), RaMs = num(8), DecMs = num(9), Snr = num(10), Settling = settling });
                        break;
                    default:
                        if (cur == null) break;
                        cur.Events.Add(new GuideLogEvent { Time = t, Name = ev, Note = note });
                        if (ev == "Dither") settling = true;
                        else if (ev == "Settled" || ev == "Settle timed out") settling = false;
                        break;
                }
            }
            foreach (var s in Sessions) s.Steps.RemoveAll(x => double.IsNaN(x.RaPx) || double.IsNaN(x.DecPx));
        }

        // ------------------------------------------------------------------ PHD2

        private void ReadPhd2(string[] lines)
        {
            GuideLogSession cur = null;
            string[] header = null;
            var cal = new List<string>();
            var calSteps = new List<Tuple<string, double, double>>();
            string calibration = null;
            bool settling = false, inCal = false;
            string[] calHeader = null;
            double calRate = double.NaN, calDec = double.NaN;
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith("Calibration Begins at", StringComparison.Ordinal)) { inCal = true; cal.Clear(); calSteps.Clear(); calHeader = null; calRate = calDec = double.NaN; continue; }
                if (line.StartsWith("Guiding Begins at", StringComparison.Ordinal))
                {
                    inCal = false;
                    DateTime start;
                    DateTime.TryParse(line.Substring(17).Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out start);
                    cur = new GuideLogSession { Start = start, Calibration = calibration, RaRatePxPerSec = calRate };
                    cur.CalibrationSteps.AddRange(calSteps);
                    Sessions.Add(cur);
                    header = null; settling = false;
                    continue;
                }
                if (line.StartsWith("Guiding Ends", StringComparison.Ordinal)) { cur = null; continue; }
                if (inCal)
                {
                    if (line.StartsWith("Direction,", StringComparison.Ordinal)) { calHeader = line.Split(','); continue; }
                    var cc = line.Split(',');
                    if (calHeader != null && cc.Length >= 4 && (cc[0] == "West" || cc[0] == "East" || cc[0] == "North" || cc[0] == "South" || cc[0] == "Backlash"))
                    {
                        int ix = Array.IndexOf(calHeader, "dx"), iy = Array.IndexOf(calHeader, "dy");
                        double dx, dy;
                        if (ix > 0 && iy > 0 && ix < cc.Length && iy < cc.Length && double.TryParse(cc[ix], NumberStyles.Float, CultureInfo.InvariantCulture, out dx) && double.TryParse(cc[iy], NumberStyles.Float, CultureInfo.InvariantCulture, out dy))
                            calSteps.Add(Tuple.Create(cc[0] == "Backlash" ? "North" : cc[0], dx, dy));
                        continue;
                    }
                    if (line.IndexOf("calibration complete", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        cal.Add(line); calibration = string.Join("; ", cal);
                        // "West calibration complete. Angle = 11.3 deg, Rate = 9.810 px/sec, Parity = Even"
                        var rm = Regex.Match(line, @"^West calibration complete.*Rate = ([0-9.]+) px/(sec|ms)", RegexOptions.CultureInvariant);
                        if (rm.Success) calRate = double.Parse(rm.Groups[1].Value, CultureInfo.InvariantCulture) * (rm.Groups[2].Value == "ms" ? 1000 : 1);
                    }
                    else { var dm = DecLine.Match(line); if (dm.Success) calDec = double.Parse(dm.Groups[1].Value, CultureInfo.InvariantCulture); }
                    continue;
                }
                if (cur == null) continue;
                var gd = DecLine.Match(line);
                if (gd.Success)
                {
                    // the RA rate goes with cos(Dec): from where PHD2 calibrated to where it guides
                    double d = double.Parse(gd.Groups[1].Value, CultureInfo.InvariantCulture);
                    if (!double.IsNaN(cur.RaRatePxPerSec) && !double.IsNaN(calDec))
                        cur.RaRatePxPerSec *= Math.Cos(AstroMath.Clamp(d, -80, 80) * AstroMath.Deg2Rad) / Math.Cos(AstroMath.Clamp(calDec, -80, 80) * AstroMath.Deg2Rad);
                    continue;
                }
                if (line.StartsWith("Pixel scale =", StringComparison.Ordinal))
                {
                    var m = Regex.Match(line, @"Pixel scale = ([0-9.]+)");
                    if (m.Success) cur.ScaleArcsec = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                    continue;
                }
                if (line.StartsWith("Frame,", StringComparison.Ordinal)) { header = line.Split(','); continue; }
                if (line.StartsWith("INFO:", StringComparison.Ordinal))
                {
                    var t = cur.Steps.Count > 0 ? cur.Steps[cur.Steps.Count - 1].Time : cur.Start;
                    if (line.IndexOf("DITHER", StringComparison.Ordinal) >= 0) { cur.Events.Add(new GuideLogEvent { Time = t.AddMilliseconds(1), Name = "Dither", Note = line.Substring(5).Trim() }); settling = true; }
                    else if (line.IndexOf("Settling complete", StringComparison.OrdinalIgnoreCase) >= 0 || line.IndexOf("Settling failed", StringComparison.OrdinalIgnoreCase) >= 0) settling = false;
                    continue;
                }
                if (header == null || !char.IsDigit(line[0])) continue;
                var c = SplitCsv(line);
                Func<string, double> col = name =>
                {
                    int i = Array.IndexOf(header, name);
                    double v;
                    return i >= 0 && i < c.Length && double.TryParse(c[i], NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : double.NaN;
                };
                Func<string, string> text = name => { int i = Array.IndexOf(header, name); return i >= 0 && i < c.Length ? c[i].Trim('"') : ""; };
                string mount = text("mount");
                if (mount != "Mount") continue;
                double ra = col("RARawDistance"), dec = col("DECRawDistance"), time = col("Time");
                if (double.IsNaN(ra) || double.IsNaN(dec) || double.IsNaN(time)) continue; // a dropped frame: the star was lost
                double raMs = col("RADuration"), decMs = col("DECDuration");
                if (double.IsNaN(raMs)) raMs = 0;
                if (double.IsNaN(decMs)) decMs = 0;
                if (text("RADirection") == "W") raMs = -raMs;
                if (text("DECDirection") == "S") decMs = -decMs;
                cur.Steps.Add(new GuideLogStep { Time = cur.Start.AddSeconds(time), RaPx = ra, DecPx = dec, RaMs = raMs, DecMs = decMs, Snr = col("SNR"), Settling = settling });
            }
        }
    }

    /// <summary>
    /// The spectrum of the RA error: how much of it repeats at each period, which shows the mount's periodic error (its
    /// amplitude and period) and its harmonics.
    /// </summary>
    public class GuideSpectrum
    {
        /// <summary>The periods (s) and the amplitude at each: half the peak-to-peak swing (arcseconds with a scale, else pixels).</summary>
        public double[] PeriodSec = new double[0], Amplitude = new double[0];
        /// <summary>"FFT" for frames taken at an even pace, "Lomb–Scargle" otherwise.</summary>
        public string Method;
        /// <summary>The corrections were added back (the mount's own error, not what was left of it after guiding).</summary>
        public bool CorrectionsAdded;
        /// <summary>The RA speed of the corrections (pixels per second of pulse) from the log's calibration; NaN when the log does not give it.</summary>
        public double RaRatePxPerSec = double.NaN;
        /// <summary>The largest peak, and the largest within 15 % of the worm period (NaN when the log is too short for it).</summary>
        public double PeakPeriodSec = double.NaN, PeakAmplitude = double.NaN, WormPeriodSec = double.NaN, WormAmplitude = double.NaN;
        public bool InArcsec;
        /// <summary>The span of the log (s).</summary>
        public double SpanSec;

        /// <summary>
        /// The RA error of a session as a series: with addCorrections and the RA rate known from the log, the corrections
        /// sent are added back and the jumps of dithers taken out, which gives the mount's own error; else the error that was
        /// left. (Fitting the rate from the log itself is biased: guiding ties each pulse to the error's trend and its noise.)
        /// Times in seconds from the start; arcseconds with a scale.
        /// </summary>
        public static void Series(GuideLogSession s, bool addCorrections, out double[] t, out double[] y, out double rate)
        {
            var st = s.Steps;
            int n = st.Count;
            t = new double[n]; y = new double[n];
            rate = double.NaN;
            if (n == 0) return;
            for (int i = 0; i < n; i++) { t[i] = (st[i].Time - st[0].Time).TotalSeconds; y[i] = st[i].RaPx; }
            var dithers = s.Events.Where(e => e.Name == "Dither").Select(e => e.Time).ToList();
            if (addCorrections && s.RaRatePxPerSec > 0)
            {
                // each RA pulse moved the star rate × its length, East back against a positive offset: added back
                rate = s.RaRatePxPerSec;
                double sum = 0, offset = 0;
                var g = new double[n];
                for (int i = 0; i < n; i++)
                {
                    if (i > 0) sum += st[i - 1].RaMs / 1000;
                    g[i] = y[i] + rate * sum;
                }
                // a dither moves the lock: the error jumps without the mount moving, so the jump is taken out
                y[0] = g[0];
                for (int i = 1; i < n; i++)
                {
                    if (dithers.Any(d => d > st[i - 1].Time && d <= st[i].Time)) offset += g[i] - g[i - 1];
                    y[i] = g[i] - offset;
                }
            }
            if (!double.IsNaN(s.ScaleArcsec)) for (int i = 0; i < n; i++) y[i] *= s.ScaleArcsec;
        }

        /// <summary>The spectrum of a session's RA error (see Series), with peaks looked for near a worm period (s; NaN for none).</summary>
        public static GuideSpectrum Of(GuideLogSession s, bool addCorrections, double wormPeriodSec)
        {
            double[] t, y;
            double rate;
            Series(s, addCorrections, out t, out y, out rate);
            var r = new GuideSpectrum { CorrectionsAdded = !double.IsNaN(rate), RaRatePxPerSec = rate, InArcsec = !double.IsNaN(s.ScaleArcsec), WormPeriodSec = wormPeriodSec };
            int n = t.Length;
            if (n < 16) return r;
            r.SpanSec = t[n - 1] - t[0];
            Detrend(t, y);
            var dts = new List<double>();
            for (int i = 1; i < n; i++) dts.Add(t[i] - t[i - 1]);
            double dt = Median(dts);
            double sd = Math.Sqrt(dts.Average(d => (d - dt) * (d - dt)));
            // at least one and a half cycles in the log, and at least three frames to a cycle
            double minP = Math.Max(3 * dt, 10), maxP = r.SpanSec / 1.5;
            if (maxP <= minP) return r;
            if (dts.Max() <= 2.5 * dt && sd <= 0.2 * dt) Fourier(r, t, y, dt, minP, maxP);
            else LombScargle(r, t, y, minP, maxP);
            if (r.Amplitude.Length == 0) return r;
            int best = 0;
            for (int i = 1; i < r.Amplitude.Length; i++) if (r.Amplitude[i] > r.Amplitude[best]) best = i;
            r.PeakPeriodSec = r.PeriodSec[best]; r.PeakAmplitude = r.Amplitude[best];
            if (wormPeriodSec > 0 && wormPeriodSec <= maxP)
            {
                double a = double.NaN, p = double.NaN;
                for (int i = 0; i < r.PeriodSec.Length; i++)
                    if (Math.Abs(r.PeriodSec[i] / wormPeriodSec - 1) <= 0.15 && !(r.Amplitude[i] <= a)) { a = r.Amplitude[i]; p = r.PeriodSec[i]; }
                r.WormAmplitude = a;
                if (!double.IsNaN(p)) r.WormPeriodSec = p;
            }
            return r;
        }

        /// <summary>
        /// Frames at an even pace: interpolated onto an exact grid, a Hann window, and the FFT padded for finer periods.
        /// The amplitude of a sine comes out as its amplitude.
        /// </summary>
        private static void Fourier(GuideSpectrum r, double[] t, double[] y, double dt, double minP, double maxP)
        {
            r.Method = "FFT";
            int m = (int)Math.Floor((t[t.Length - 1] - t[0]) / dt) + 1;
            int size = Fft.NextPowerOfTwo(m) * 4;
            var re = new double[size]; var im = new double[size];
            double wsum = 0;
            int j = 0;
            for (int k = 0; k < m; k++)
            {
                double tk = t[0] + k * dt;
                while (j < t.Length - 2 && t[j + 1] < tk) j++;
                double f = t[j + 1] > t[j] ? (tk - t[j]) / (t[j + 1] - t[j]) : 0;
                double v = y[j] + AstroMath.Clamp(f, 0, 1) * (y[j + 1] - y[j]);
                double w = 0.5 - 0.5 * Math.Cos(2 * Math.PI * k / Math.Max(1, m - 1));
                re[k] = v * w; wsum += w;
            }
            Fft.Transform(re, im, false);
            var per = new List<double>(); var amp = new List<double>();
            for (int k = size / 2; k >= 1; k--)
            {
                double p = size * dt / k;
                if (p < minP || p > maxP) continue;
                per.Add(p); amp.Add(2 * Math.Sqrt(re[k] * re[k] + im[k] * im[k]) / wsum);
            }
            r.PeriodSec = per.ToArray(); r.Amplitude = amp.ToArray();
        }

        /// <summary>Frames at an uneven pace (lost stars, changes of exposure): a Lomb–Scargle periodogram, its power turned into an amplitude.</summary>
        private static void LombScargle(GuideSpectrum r, double[] t, double[] y, double minP, double maxP)
        {
            r.Method = "Lomb–Scargle";
            double mean = y.Average(), variance = y.Average(v => (v - mean) * (v - mean));
            var p = PeriodSearch.LombScargle(t, y, null, minP, maxP, 5);
            int n = p.Frequencies.Length;
            var per = new double[n]; var amp = new double[n];
            // the power is the share of the variance a sine explains; a sine's variance is half its amplitude squared
            for (int i = 0; i < n; i++) { per[n - 1 - i] = 1 / p.Frequencies[i]; amp[n - 1 - i] = Math.Sqrt(2 * Math.Max(0, p.Power[i]) * variance); }
            r.PeriodSec = per; r.Amplitude = amp;
        }

        private static void Detrend(double[] t, double[] y)
        {
            int n = t.Length;
            double mt = t.Average(), my = y.Average(), stt = 0, sty = 0;
            for (int i = 0; i < n; i++) { stt += (t[i] - mt) * (t[i] - mt); sty += (t[i] - mt) * (y[i] - my); }
            double b = stt > 0 ? sty / stt : 0;
            for (int i = 0; i < n; i++) y[i] -= my + b * (t[i] - mt);
        }

        private static double Median(List<double> v)
        {
            var s = v.OrderBy(x => x).ToList();
            return s.Count == 0 ? double.NaN : s.Count % 2 == 1 ? s[s.Count / 2] : 0.5 * (s[s.Count / 2 - 1] + s[s.Count / 2]);
        }
    }
}
