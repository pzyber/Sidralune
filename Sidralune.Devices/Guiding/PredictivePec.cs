using System;
using System.Collections.Generic;
using System.Linq;
using Sidralune.Core.Mathematics;
using Sidralune.Science.Photometry;

namespace Sidralune.Devices.Guiding
{
    /// <summary>
    /// Predictive periodic error correction for RA, after Klenske, Zeilinger, Schölkopf &amp; Hennig (2016): the mount's
    /// gear error (the measured error plus every correction sent) is modelled as a Gaussian process with a periodic part at
    /// the worm period and a smooth long-term part, and each frame the change the model predicts over the next frame is
    /// corrected in advance, on top of a share of the error just measured. Until two periods are recorded it guides like
    /// Hysteresis, blending into prediction as the model learns. Sidralune's own choices: the offset and drift are fitted
    /// and removed first, the kernel's signal variance follows the data, the noise comes from the frame-to-frame scatter,
    /// and the period is refined with a Lomb–Scargle periodogram.
    /// </summary>
    public class PredictivePecAlgorithm : IAxisAlgorithm
    {
        public double MinMovePx = 0.2;
        /// <summary>Share of the error just measured that is corrected (0..1).</summary>
        public double ReactiveGain = 0.6;
        /// <summary>Share of the predicted change that is corrected in advance (0..1).</summary>
        public double PredictiveGain = 0.5;
        /// <summary>The worm period (s); refined while guiding when AutoPeriod is on.</summary>
        public double PeriodSec = 480;
        public bool AutoPeriod = true;
        /// <summary>How long guiding may stop before the model is forgotten, as a share of the period.</summary>
        public double RetainFraction = 0.4;
        /// <summary>The clock (seconds will be measured from its first reading); tests set their own.</summary>
        public Func<DateTime> Clock = () => DateTime.UtcNow;
        /// <summary>The grid the gear error is averaged on (s).</summary>
        public const double GridSec = 5;
        /// <summary>
        /// The kernel: shares of the signal variance in the long smooth part (length one period), the periodic part
        /// (smoothness PeriodicLength, in the usual sin² form) and the short part (ShortLengthSec). The short part is off:
        /// it takes up some of the periodic signal and then fades, so predictions further ahead fall short.
        /// </summary>
        public double LongShare = 0.3, PeriodicShare = 0.7, ShortShare = 0, PeriodicLength = 0.6, ShortLengthSec = 15;

        private readonly List<double> _t = new List<double>(), _g = new List<double>();
        private readonly HysteresisAlgorithm _fallback = new HysteresisAlgorithm { Hysteresis = 0.1 };
        private DateTime _origin;
        private bool _started;
        private double _corrected;      // corrections sent, plus lock moves, so error + this is the gear error
        private bool _reanchor;         // the next sample continues the model instead of the raw track
        private double _lastPeriodCheck = double.NaN;
        private double _lastNow = double.NaN;
        private readonly List<double> _intervals = new List<double>();

        /// <summary>Seconds of gear error recorded, and how far through the two-period training (0..1).</summary>
        public double Span => _t.Count > 1 ? _t[_t.Count - 1] - _t[0] : 0;
        public double Training => Math.Min(1, Span / (2 * Math.Max(10, PeriodSec)));
        /// <summary>The model is predicting (training done).</summary>
        public bool Predicting => Training >= 1;
        public event Action<string> Note;

        public double Correction(double errorPx)
        {
            var nowTime = Clock();
            if (!_started) { _origin = nowTime; _started = true; }
            double now = (nowTime - _origin).TotalSeconds;
            // stopped for longer than the model is kept: start again
            if (!double.IsNaN(_lastNow) && now - _lastNow > RetainFraction * Math.Max(10, PeriodSec)) Forget(now);
            if (!double.IsNaN(_lastNow) && now > _lastNow) { _intervals.Add(now - _lastNow); if (_intervals.Count > 20) _intervals.RemoveAt(0); }
            _lastNow = now;

            double gear = errorPx + _corrected;
            if (_reanchor)
            {
                _reanchor = false;
                double[] model;
                if (_t.Count > 10 && Predict(new[] { now }, out model)) { _corrected += model[0] - gear; gear = model[0]; }
            }
            _t.Add(now); _g.Add(gear);
            // six periods (at least half an hour) for finding the period; the model uses the last 2.2
            double keep = Math.Max(6 * Math.Max(10, PeriodSec), 1800);
            while (_t.Count > 2 && now - _t[0] > keep) { _t.RemoveAt(0); _g.RemoveAt(0); }

            _fallback.MinMovePx = MinMovePx; _fallback.Aggressiveness = Math.Max(0.3, ReactiveGain);
            double fallback = _fallback.Correction(errorPx);
            double reactive = Math.Abs(errorPx) < MinMovePx ? 0 : ReactiveGain * errorPx;
            double w = Training;
            if (w <= 0 || _t.Count < 12) return fallback;
            MaybeRefinePeriod(now);
            double dt = _intervals.Count > 0 ? Median(_intervals) : 1;
            double[] f;
            double predictive = 0;
            if (Predict(new[] { now, now + dt }, out f)) predictive = PredictiveGain * (f[1] - f[0]);
            double control = reactive + predictive;
            return w * control + (1 - w) * fallback;
        }

        public void Sent(double correctionPx) { _corrected += correctionPx; _fallback.Sent(correctionPx); }

        /// <summary>A reset (dither, pause, calibration) only resets the Hysteresis it starts with; the model is kept.</summary>
        public void Reset() { _fallback.Reset(); }

        /// <summary>
        /// The lock position moved by this much along RA (pixels): the error measured from it drops by as much while the
        /// gear error has not changed, so the move is added to the track.
        /// </summary>
        public void LockMoved(double raPx) { _corrected += raPx; }

        /// <summary>Guiding starts again on a lock position not known to match the last one: continue from the model.</summary>
        public void Reanchor() { _reanchor = true; }

        /// <summary>Forgets the model (after a slew, or a stop longer than the model is kept).</summary>
        public void Forget() { Forget(double.NaN); }

        private void Forget(double now)
        {
            if (_t.Count > 0) Note?.Invoke("Predictive PEC model forgotten");
            _t.Clear(); _g.Clear(); _intervals.Clear();
            _corrected = 0; _reanchor = false; _lastPeriodCheck = double.NaN;
            _fallback.Reset();
            if (double.IsNaN(now)) { _started = false; _lastNow = double.NaN; }
        }

        // ------------------------------------------------------------------ the model

        /// <summary>The gear error the model expects at the given times (offset and drift included); false without enough data.</summary>
        public bool Predict(IList<double> times, out double[] values)
        {
            values = null;
            double[] gx, gy, gv;
            Grid(out gx, out gy, out gv);
            int n = gx.Length;
            if (n < 4) return false;
            // the offset and drift first, so the process models what is left
            var line = LinAlg.PolyFit(gx, gy, 1);
            if (line == null) return false;
            var r = new double[n];
            double ss = 0;
            for (int i = 0; i < n; i++) { r[i] = gy[i] - (line[0] + line[1] * gx[i]); ss += r[i] * r[i]; }
            double signal = Math.Max(1e-4, ss / n);
            double P = Math.Max(10, PeriodSec);
            // kernel: a long smooth part, the periodic part, and a short part for structure the others miss
            double lp = Math.Max(0.05, PeriodicLength), ls = Math.Max(1, ShortLengthSec);
            Func<double, double, double> kernel = (a, b) =>
            {
                double d = a - b, s = Math.Sin(Math.PI * d / P);
                return signal * (LongShare * Math.Exp(-d * d / (2 * P * P)) + PeriodicShare * Math.Exp(-2 * s * s / (lp * lp)) + ShortShare * Math.Exp(-d * d / (2 * ls * ls)));
            };
            var K = LinAlg.KernelMatrix(gx, gx, kernel);
            for (int i = 0; i < n; i++) K[i, i] += gv[i];
            var alpha = LinAlg.CholeskySolve(K, r);
            if (alpha == null) return false;
            values = new double[times.Count];
            for (int m = 0; m < times.Count; m++)
            {
                double s = 0;
                for (int i = 0; i < n; i++) s += kernel(times[m], gx[i]) * alpha[i];
                values[m] = s + line[0] + line[1] * times[m];
            }
            return true;
        }

        /// <summary>The gear error averaged on the 5 s grid, with each bin's variance (the noise over its count).</summary>
        private void Grid(out double[] x, out double[] y, out double[] v)
        {
            // the noise: frame-to-frame differences, which the periodic error and drift hardly touch
            double noise = 0;
            int k = 0;
            for (int i = 1; i < _g.Count; i++) { double d = _g[i] - _g[i - 1]; noise += d * d; k++; }
            noise = Math.Max(1e-4, k > 0 ? noise / (2.0 * k) : 0.01);
            var bins = new SortedDictionary<long, double[]>();
            double from = _t.Count > 0 ? _t[_t.Count - 1] - 2.2 * Math.Max(10, PeriodSec) : 0;
            for (int i = 0; i < _t.Count; i++)
            {
                if (_t[i] < from) continue;
                long b = (long)Math.Floor(_t[i] / GridSec);
                double[] acc;
                if (!bins.TryGetValue(b, out acc)) bins[b] = acc = new double[3];
                acc[0] += _t[i]; acc[1] += _g[i]; acc[2]++;
            }
            x = new double[bins.Count]; y = new double[bins.Count]; v = new double[bins.Count];
            int j = 0;
            foreach (var acc in bins.Values) { x[j] = acc[0] / acc[2]; y[j] = acc[1] / acc[2]; v[j] = noise / acc[2]; j++; }
        }

        /// <summary>
        /// Every 5 minutes once two periods are recorded: a Lomb–Scargle periodogram of the detrended gear error between
        /// half and twice the period; a clear peak moves the period a quarter of the way to it.
        /// </summary>
        private void MaybeRefinePeriod(double now)
        {
            if (!AutoPeriod || Span < 2 * Math.Max(10, PeriodSec)) return;
            if (!double.IsNaN(_lastPeriodCheck) && now - _lastPeriodCheck < 300) return;
            _lastPeriodCheck = now;
            try
            {
                var line = LinAlg.PolyFit(_t, _g, 1);
                if (line == null) return;
                var y = _g.Select((g, i) => g - (line[0] + line[1] * _t[i])).ToList();
                double lo = Math.Max(30, 0.5 * PeriodSec), hi = Math.Min(0.5 * Span + PeriodSec, 2 * PeriodSec);
                if (hi <= lo) return;
                var p = PeriodSearch.LombScargle(_t, y, null, lo, hi, 5);
                if (p.BestPower < 0.2 || double.IsNaN(p.BestPeriod)) return;
                double old = PeriodSec;
                PeriodSec += 0.25 * (p.BestPeriod - PeriodSec);
                if (Math.Abs(PeriodSec - old) > 1) Note?.Invoke(string.Format(System.Globalization.CultureInfo.InvariantCulture, "Predictive PEC period {0:0} s", PeriodSec));
            }
            catch { }
        }

        private static double Median(List<double> v)
        {
            var a = v.ToArray();
            Array.Sort(a);
            return a.Length == 0 ? 0 : a.Length % 2 == 1 ? a[a.Length / 2] : 0.5 * (a[a.Length / 2 - 1] + a[a.Length / 2]);
        }
    }
}
