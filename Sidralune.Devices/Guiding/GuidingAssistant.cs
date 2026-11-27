using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Sidralune.Core.Mathematics;
using Sidralune.Core.Util;

namespace Sidralune.Devices.Guiding
{
    /// <summary>What a Guiding Assistant run measured (arcsec on the sky unless said otherwise).</summary>
    public class GuidingAssistantResult
    {
        public DateTime When;
        public double Seconds;
        public int Frames;
        public double PixelScale = double.NaN, ExposureSeconds = double.NaN;
        /// <summary>The star's quick wander (seeing and measuring noise): RMS per axis from frame-to-frame differences.</summary>
        public double SeeingRaArcsec = double.NaN, SeeingDecArcsec = double.NaN;
        /// <summary>The steady drift per axis (arcsec per minute; positive the way West and North pulses move the star).</summary>
        public double DriftRaArcsecPerMin = double.NaN, DriftDecArcsecPerMin = double.NaN;
        /// <summary>RA's swing after the drift is taken out, and its fastest change (arcsec per second).</summary>
        public double RaPeakToPeakArcsec = double.NaN, RaMaxRateArcsecPerSec = double.NaN;
        /// <summary>The polar alignment error the Dec drift needs at least (arcmin).</summary>
        public double PolarErrorArcmin = double.NaN;
        public double BacklashMs = double.NaN, BacklashUncertaintyMs = double.NaN;
        public bool BacklashMeasured, BacklashValid;
        public string BacklashProblem;
    }

    /// <summary>A change the Guiding Assistant suggests: a sentence, and how to make it (null for advice only).</summary>
    public class GuidingRecommendation
    {
        public string Text;
        public Action<GuiderSettings> Apply;
    }

    /// <summary>
    /// The Guiding Assistant: while guiding, corrections are paused and the stars' unguided motion is recorded, giving the
    /// seeing, the drift, RA's swing and speed, and the least polar alignment error that explains the Dec drift. A Dec
    /// backlash test can follow. From these come suggested settings.
    /// </summary>
    public class GuidingAssistant
    {
        private readonly SidraluneGuider _g;
        private readonly List<double[]> _samples = new List<double[]>(); // seconds, RA arcsec, Dec arcsec
        private DateTime _start;
        private double _scale = 1;

        public bool Measuring { get; private set; }
        public GuidingAssistantResult Result { get; private set; }

        public GuidingAssistant(SidraluneGuider g) { _g = g; }

        /// <summary>Pauses corrections and starts recording. Guiding must be running.</summary>
        public void Start()
        {
            if (Measuring) return;
            if (!_g.InGuidingPhase || _g.Calibration == null) throw new InvalidOperationException(Lang.T("Start guiding first: the Guiding Assistant measures while guiding is paused."));
            lock (_samples) _samples.Clear();
            Result = null;
            _scale = _g.PixelScale;
            _start = DateTime.UtcNow;
            _g.Measured += OnMeasured;
            _g.Pause(true);
            Measuring = true;
            Log.Info("Guider", "Guiding Assistant started");
        }

        private void OnMeasured(GuideFrame f)
        {
            lock (_samples) _samples.Add(new[] { (f.Time - _start).TotalSeconds, f.RaPx * _scale, f.DecPx * _scale });
        }

        /// <summary>The numbers so far.</summary>
        public GuidingAssistantResult Current
        {
            get
            {
                List<double[]> copy;
                lock (_samples) copy = new List<double[]>(_samples);
                var r = Analyse(copy);
                r.PixelScale = _scale; r.ExposureSeconds = _g.CurrentExposure;
                return r;
            }
        }

        /// <summary>
        /// Stops recording; with measureBacklash, runs the Dec backlash test. Guiding resumes either way. The result is kept
        /// with the guider's settings.
        /// </summary>
        public async Task<GuidingAssistantResult> Stop(bool measureBacklash, CancellationToken ct)
        {
            if (!Measuring) return Result;
            _g.Measured -= OnMeasured;
            Measuring = false;
            var r = Current;
            try
            {
                if (measureBacklash && _g.InGuidingPhase)
                {
                    r.BacklashMeasured = true;
                    var b = await _g.MeasureBacklash(ct);
                    r.BacklashMs = b.Ms; r.BacklashUncertaintyMs = b.UncertaintyMs; r.BacklashValid = b.Valid;
                    if (!b.Valid) r.BacklashProblem = Lang.T("Dec came back more slowly than it went, so the backlash could not be told apart.");
                }
            }
            catch (OperationCanceledException) { r.BacklashProblem = Lang.T("The backlash test was stopped."); }
            catch (Exception ex) { r.BacklashProblem = ex.Message; }
            finally { if (_g.InGuidingPhase) _g.Pause(false); }
            Result = r;
            _g.RecordAssistant(r);
            Log.Info("Guider", string.Format("Guiding Assistant: seeing RA {0:0.00}\" Dec {1:0.00}\", drift RA {2:0.00} Dec {3:0.00}\"/min, polar alignment ≥ {4:0.0}', backlash {5:0} ms",
                r.SeeingRaArcsec, r.SeeingDecArcsec, r.DriftRaArcsecPerMin, r.DriftDecArcsecPerMin, r.PolarErrorArcmin, r.BacklashMs));
            return r;
        }

        /// <summary>Stops without a result (the window closed, guiding stopped); guiding resumes.</summary>
        public void Cancel()
        {
            if (!Measuring) return;
            _g.Measured -= OnMeasured;
            Measuring = false;
            if (_g.InGuidingPhase) _g.Pause(false);
        }

        /// <summary>The statistics of samples (seconds, RA arcsec, Dec arcsec).</summary>
        public static GuidingAssistantResult Analyse(IList<double[]> s)
        {
            var r = new GuidingAssistantResult { When = DateTime.UtcNow, Frames = s.Count };
            if (s.Count < 3) return r;
            r.Seconds = s[s.Count - 1][0] - s[0][0];
            var t = s.Select(v => v[0]).ToList();
            var ra = s.Select(v => v[1]).ToList();
            var dec = s.Select(v => v[2]).ToList();
            // seeing: frame-to-frame differences, which slow drift and periodic error hardly touch
            r.SeeingRaArcsec = QuickWander(ra);
            r.SeeingDecArcsec = QuickWander(dec);
            var fr = LinAlg.PolyFit(t, ra, 1);
            var fd = LinAlg.PolyFit(t, dec, 1);
            if (fr != null)
            {
                r.DriftRaArcsecPerMin = fr[1] * 60;
                var rest = ra.Select((v, i) => v - (fr[0] + fr[1] * t[i])).ToList();
                r.RaPeakToPeakArcsec = rest.Max() - rest.Min();
            }
            if (fd != null)
            {
                r.DriftDecArcsecPerMin = fd[1] * 60;
                // the sky turns 2π a sidereal day (1436 min): a pole off by E arcmin drifts Dec by up to E × 2π / 1436 × 60 arcsec a minute
                r.PolarErrorArcmin = Math.Abs(r.DriftDecArcsecPerMin) * 1436.0 / (120 * Math.PI);
            }
            // RA's fastest change, on a running average of five frames so the seeing does not count
            var smooth = new List<double>();
            for (int i = 0; i < ra.Count; i++)
            {
                int a = Math.Max(0, i - 2), b = Math.Min(ra.Count - 1, i + 2);
                double sum = 0;
                for (int k = a; k <= b; k++) sum += ra[k];
                smooth.Add(sum / (b - a + 1));
            }
            double fastest = 0;
            for (int i = 1; i < smooth.Count - 1; i++)
            {
                double dt = t[i + 1] - t[i - 1];
                if (dt > 0) fastest = Math.Max(fastest, Math.Abs(smooth[i + 1] - smooth[i - 1]) / dt);
            }
            r.RaMaxRateArcsecPerSec = fastest;
            return r;
        }

        private static double QuickWander(List<double> v)
        {
            double ss = 0;
            for (int i = 1; i < v.Count; i++) ss += (v[i] - v[i - 1]) * (v[i] - v[i - 1]);
            return Math.Sqrt(ss / (2.0 * (v.Count - 1)));
        }

        /// <summary>The settings the result suggests.</summary>
        public static List<GuidingRecommendation> Recommend(GuidingAssistantResult r, GuiderSettings st)
        {
            var list = new List<GuidingRecommendation>();
            double scale = r.PixelScale > 0 ? r.PixelScale : 1;
            if (!double.IsNaN(r.SeeingRaArcsec) && !double.IsNaN(r.SeeingDecArcsec))
            {
                // a little under the seeing's wander, so corrections are made for drift and not for seeing
                double raMin = Math.Max(0.05, Math.Round(0.65 * r.SeeingRaArcsec / scale, 2)), decMin = Math.Max(0.05, Math.Round(0.65 * r.SeeingDecArcsec / scale, 2));
                list.Add(new GuidingRecommendation
                {
                    Text = Lang.T("Minimum move RA {0:0.00} px and Dec {1:0.00} px, a little under the seeing's wander.", raMin, decMin),
                    Apply = s => { s.RaMinMovePx = raMin; s.DecMinMovePx = decMin; }
                });
            }
            if (r.BacklashMeasured && r.BacklashValid && !double.IsNaN(r.BacklashMs))
            {
                double ms = Math.Round(r.BacklashMs / 10) * 10;
                if (ms < 100) list.Add(new GuidingRecommendation { Text = Lang.T("Dec backlash is small ({0:0} ms): no compensation needed.", ms) });
                else if (ms <= 3000)
                    list.Add(new GuidingRecommendation
                    {
                        Text = Lang.T("Turn on Dec backlash compensation at {0:0} ms (adapting between {1:0} and {2:0} ms).", ms, 0.2 * ms, 1.5 * ms),
                        Apply = s => { s.BacklashCompensation = true; s.BacklashMs = ms; s.BacklashMinMs = Math.Round(0.2 * ms); s.BacklashMaxMs = Math.Round(1.5 * ms); }
                    });
                else
                {
                    // the star drifts the way North pulses move it: South pulses hold it, and the other way is never needed
                    var mode = r.DriftDecArcsecPerMin > 0 ? DecGuideMode.South : DecGuideMode.North;
                    list.Add(new GuidingRecommendation
                    {
                        Text = mode == DecGuideMode.North ? Lang.T("Dec backlash is large ({0:0.0} s): guide Dec North only, against the drift.", ms / 1000)
                                                          : Lang.T("Dec backlash is large ({0:0.0} s): guide Dec South only, against the drift.", ms / 1000),
                        Apply = s => { s.DecMode = mode; s.BacklashCompensation = false; }
                    });
                }
            }
            if (!double.IsNaN(r.PolarErrorArcmin) && r.PolarErrorArcmin > 5)
                list.Add(new GuidingRecommendation { Text = Lang.T("Polar alignment is off by at least {0:0}′. Observatory → Polar alignment can measure and correct it.", r.PolarErrorArcmin) });
            double exposure = r.ExposureSeconds;
            if (!st.AutoExposure && exposure > 0 && r.RaMaxRateArcsecPerSec > 0)
            {
                double minMoveArcsec = st.RaMinMovePx * scale;
                if (r.RaMaxRateArcsecPerSec * exposure > Math.Max(minMoveArcsec, 0.05 * scale))
                {
                    double best = SidraluneGuider.AutoExposureSteps(0.5, exposure).LastOrDefault(e => e < exposure && r.RaMaxRateArcsecPerSec * e <= Math.Max(minMoveArcsec, 0.05 * scale));
                    if (best <= 0) best = 0.5;
                    if (best < exposure)
                        list.Add(new GuidingRecommendation
                        {
                            Text = Lang.T("RA changes quickly ({0:0.00}″/s): a {1:0.0#} s exposure follows it better.", r.RaMaxRateArcsecPerSec, best),
                            Apply = s => s.ExposureSeconds = best
                        });
                }
            }
            return list;
        }
    }
}
