using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Sidralune.Devices.Guiding
{
    /// <summary>How well guiding went during one exposure, from the guide steps taken while the shutter was open.</summary>
    public class GuidingStats
    {
        /// <summary>Guide steps taken during the exposure.</summary>
        public int Steps;
        /// <summary>RMS of the guide error per axis and in total, and the largest single error (arcseconds).</summary>
        public double RmsRa = double.NaN, RmsDec = double.NaN, RmsTotal = double.NaN, Peak = double.NaN;
        /// <summary>Guiding was not on for part of the exposure: the star was lost, guiding paused or stopped.</summary>
        public bool Interrupted;
        /// <summary>Worse than the limit the exposure was taken with (ExposureRequest.MaxGuidingRms).</summary>
        public bool Bad;

        public static GuidingStats From(IList<GuideStep> steps, bool interrupted)
        {
            var s = new GuidingStats { Steps = steps.Count, Interrupted = interrupted || steps.Count == 0 };
            if (steps.Count == 0) return s;
            s.RmsRa = Math.Sqrt(steps.Average(t => t.RaArcsec * t.RaArcsec));
            s.RmsDec = Math.Sqrt(steps.Average(t => t.DecArcsec * t.DecArcsec));
            s.RmsTotal = Math.Sqrt(s.RmsRa * s.RmsRa + s.RmsDec * s.RmsDec);
            s.Peak = steps.Max(t => Math.Sqrt(t.RaArcsec * t.RaArcsec + t.DecArcsec * t.DecArcsec));
            return s;
        }

        /// <summary>
        /// Too poor for a limit (arcseconds RMS, 0 for none): worse than it, or guiding was interrupted, so the frame may
        /// be trailed or clouded.
        /// </summary>
        public bool WorseThan(double maxRms) => maxRms > 0 && (Interrupted || !(RmsTotal <= maxRms));

        /// <summary>The FITS keywords: GUIDRMS, GUIDRMSR, GUIDRMSD, GUIDPEAK, GUIDSTPS, and GUIDLOST and GUIDBAD when set.</summary>
        public void WriteTo(Sidralune.Imaging.AstroImage img)
        {
            var h = img.Header;
            if (!double.IsNaN(RmsTotal))
            {
                h.Set("GUIDRMS", Math.Round(RmsTotal, 3), "Guiding RMS error, total [arcsec]");
                h.Set("GUIDRMSR", Math.Round(RmsRa, 3), "Guiding RMS error in RA [arcsec]");
                h.Set("GUIDRMSD", Math.Round(RmsDec, 3), "Guiding RMS error in Dec [arcsec]");
                h.Set("GUIDPEAK", Math.Round(Peak, 3), "Largest guiding error [arcsec]");
            }
            h.Set("GUIDSTPS", Steps, "Guide steps during the exposure");
            if (Interrupted) h.Set("GUIDLOST", true, "Guiding was interrupted during the exposure");
            if (Bad) h.Set("GUIDBAD", true, "Guiding was worse than the limit");
        }
    }

    /// <summary>Collects the guide steps of one exposure: started as the shutter opens, ended as it closes.</summary>
    public class GuidingRecorder : IDisposable
    {
        private readonly IGuider _guider;
        private readonly List<GuideStep> _steps = new List<GuideStep>();
        private int _interrupted;
        private bool _done;

        private GuidingRecorder(IGuider g) { _guider = g; g.Step += OnStep; }

        /// <summary>
        /// A recorder for a guider in its guiding phase; null when there is none or it is not guiding (unguided frames). A
        /// frame begun while the star is lost or guiding is paused is interrupted from the start.
        /// </summary>
        public static GuidingRecorder Start(IGuider g)
        {
            if (g == null) return null;
            try { if (!g.Connected || !Automation.GuidingPause.Guiding(g)) return null; } catch { return null; }
            var rec = new GuidingRecorder(g);
            rec.Check();
            return rec;
        }

        private void OnStep(GuideStep s) { lock (_steps) if (!_done) _steps.Add(s); }

        /// <summary>Looks at the guider during the exposure: guiding that is not on (a lost star, a pause) interrupts it.</summary>
        public void Check()
        {
            bool on;
            try { on = _guider.Connected && _guider.IsGuiding; } catch { on = false; }
            if (!on) Interlocked.Exchange(ref _interrupted, 1);
        }

        /// <summary>Ends the recording (the shutter has closed) and returns how guiding went.</summary>
        public GuidingStats Stop()
        {
            Check();
            List<GuideStep> steps;
            lock (_steps) { _done = true; steps = new List<GuideStep>(_steps); }
            _guider.Step -= OnStep;
            return GuidingStats.From(steps, _interrupted != 0);
        }

        public void Dispose() { lock (_steps) _done = true; _guider.Step -= OnStep; }
    }
}
