using System;
using System.Collections.Generic;

namespace Sidralune.Devices.Guiding
{
    /// <summary>The guide algorithms an axis can use (RA: Hysteresis, Lowpass2, Z-filter; Dec: Resist Switch, Lowpass2, Z-filter).</summary>
    public enum GuideAlgorithmKind { Hysteresis, ResistSwitch, Lowpass2, ZFilter }

    /// <summary>Turns one axis's guide error (pixels along that axis) into the correction to make (pixels, same sign).</summary>
    public interface IAxisAlgorithm
    {
        double Correction(double errorPx);
        /// <summary>The correction actually sent after Correction (smaller when a pulse was cut short or not sent at all).</summary>
        void Sent(double correctionPx);
        /// <summary>Forgets the history: after calibrating, a dither or a pause.</summary>
        void Reset();
    }

    /// <summary>
    /// For RA, where the error changes smoothly: the error is smoothed by blending in a share of the previous blend
    /// (hysteresis), and a share of that is corrected (aggressiveness). A smoothed error under the minimum move is left
    /// alone, because that much is mostly seeing.
    /// </summary>
    public class HysteresisAlgorithm : IAxisAlgorithm
    {
        public double MinMovePx = 0.2;
        /// <summary>Weight of the previous blend, 0..1.</summary>
        public double Hysteresis = 0.1;
        /// <summary>Share of the error corrected, 0..1.</summary>
        public double Aggressiveness = 0.7;
        private double _blend;

        public double Correction(double errorPx)
        {
            _blend = (1 - Hysteresis) * errorPx + Hysteresis * _blend;
            return Math.Abs(_blend) < MinMovePx ? 0 : _blend * Aggressiveness;
        }

        public void Sent(double correctionPx) { }
        public void Reset() { _blend = 0; }
    }

    /// <summary>
    /// For Dec, where backlash makes every change of direction costly: corrections keep going the way they went until
    /// three errors in a row, each over the minimum move, point the other way without shrinking. An error over three
    /// minimum moves the other way (wind, a cable) turns at once when fast switching is on.
    /// </summary>
    public class ResistSwitchAlgorithm : IAxisAlgorithm
    {
        public double MinMovePx = 0.2;
        /// <summary>Share of the error corrected, 0..1.</summary>
        public double Aggressiveness = 1.0;
        public bool FastSwitch = true;
        private readonly List<double> _recent = new List<double>();
        private int _side; // the direction corrections go: +1, -1, or 0 before the first

        public double Correction(double errorPx)
        {
            _recent.Add(errorPx);
            if (_recent.Count > 3) _recent.RemoveAt(0);
            if (Math.Abs(errorPx) < MinMovePx) return 0;
            int s = Math.Sign(errorPx);
            if (s != _side)
            {
                bool fast = FastSwitch && Math.Abs(errorPx) > 3 * MinMovePx;
                bool convincing = _recent.Count == 3 && _recent.TrueForAll(v => Math.Sign(v) == s && Math.Abs(v) >= MinMovePx) && Math.Abs(_recent[2]) >= Math.Abs(_recent[0]);
                if (!fast && !convincing) return 0;
                _side = s;
            }
            return errorPx * Aggressiveness;
        }

        public void Sent(double correctionPx) { }
        public void Reset() { _recent.Clear(); _side = 0; }
    }

    /// <summary>
    /// For mounts that drift smoothly in good seeing: a straight line through the last errors (since the last reset)
    /// estimates the error now with most of the seeing averaged out, and a share of that is corrected. The correction is
    /// never larger than the error just measured, nor the other way; a turn the other way of more than five minimum moves
    /// starts the line again.
    /// </summary>
    public class Lowpass2Algorithm : IAxisAlgorithm
    {
        public double MinMovePx = 0.2;
        /// <summary>Share of the fitted error corrected, 0..1.</summary>
        public double Aggressiveness = 0.8;
        /// <summary>How many errors the line goes through.</summary>
        public int Length = 10;
        private readonly List<double> _errors = new List<double>();

        public double Correction(double errorPx)
        {
            // a turn the other way of more than five minimum moves (a bump, a gust) is a new situation; seeing seldom does that
            if (_errors.Count > 0 && Math.Sign(errorPx) != Math.Sign(_errors[_errors.Count - 1]) && Math.Abs(errorPx) > 5 * MinMovePx) _errors.Clear();
            _errors.Add(errorPx);
            if (_errors.Count > Math.Max(2, Length)) _errors.RemoveAt(0);
            double now = errorPx;
            int n = _errors.Count;
            if (n >= 3)
            {
                // least squares through (i, error i); the line's value at the newest point
                double sx = 0, sy = 0, sxx = 0, sxy = 0;
                for (int i = 0; i < n; i++) { sx += i; sy += _errors[i]; sxx += i * i; sxy += i * _errors[i]; }
                double den = n * sxx - sx * sx;
                if (den > 0)
                {
                    double b = (n * sxy - sx * sy) / den, a = (sy - b * sx) / n;
                    now = a + b * (n - 1);
                }
            }
            if (Math.Abs(now) < MinMovePx || Math.Sign(now) != Math.Sign(errorPx)) return 0;
            double c = now * Aggressiveness;
            return Math.Abs(c) > Math.Abs(errorPx) ? errorPx : c;
        }

        public void Sent(double correctionPx) { }
        public void Reset() { _errors.Clear(); }
    }

    /// <summary>
    /// For long-period errors in poor seeing: guides as if each exposure were ExposureFactor times longer. The star's
    /// uncorrected track (the error plus every correction sent so far) goes through a second-order Butterworth low-pass
    /// filter whose cutoff is the virtual exposure's Nyquist frequency, and the correction is what the filtered track asks
    /// for beyond the corrections already made. A factor of 1 filters nothing.
    /// </summary>
    public class ZFilterAlgorithm : IAxisAlgorithm
    {
        public double MinMovePx = 0.2;
        /// <summary>The virtual exposure in exposures, 1..8.</summary>
        public double ExposureFactor = 4;
        private double _corrected;       // corrections sent since the last reset (px)
        private double _x1, _x2, _y1, _y2;
        private bool _started;

        public double Correction(double errorPx)
        {
            double c = Filter(errorPx + _corrected) - _corrected;
            return Math.Abs(c) < MinMovePx ? 0 : c;
        }

        private double Filter(double x)
        {
            double factor = Math.Max(1, Math.Min(8, ExposureFactor));
            if (!_started) { _x1 = _x2 = _y1 = _y2 = x; _started = true; }
            if (factor <= 1.0001) { _x2 = _x1; _x1 = x; _y2 = _y1; _y1 = x; return x; }
            // bilinear-transformed Butterworth, cutoff 1/(2 × factor) cycles per frame
            double k = Math.Tan(Math.PI / (2 * factor)), q = Math.Sqrt(2);
            double norm = 1 / (1 + q * k + k * k);
            double b0 = k * k * norm, b1 = 2 * b0, b2 = b0;
            double a1 = 2 * (k * k - 1) * norm, a2 = (1 - q * k + k * k) * norm;
            double y = b0 * x + b1 * _x1 + b2 * _x2 - a1 * _y1 - a2 * _y2;
            _x2 = _x1; _x1 = x; _y2 = _y1; _y1 = y;
            return y;
        }

        public void Sent(double correctionPx) { _corrected += correctionPx; }
        public void Reset() { _corrected = 0; _started = false; }
    }

    /// <summary>Which way Dec may be guided: both ways, one way only (against a steady drift, with much backlash), or not at all.</summary>
    public enum DecGuideMode { Auto, North, South, Off }

    /// <summary>
    /// Dec backlash compensation: when Dec turns round, the first pulse the new way is lengthened by the backlash, so the
    /// gears are taken up and the correction still does its work. The amount adapts: a turn that overshoots shortens it,
    /// one that hardly moved the star lengthens it, always within the minimum and maximum.
    /// </summary>
    public class BacklashCompensation
    {
        public bool Enabled;
        public double AmountMs, MinMs, MaxMs;
        private int _lastDir;        // +1 North, -1 South, 0 not known
        private bool _watching;      // a compensated turn waits for the next frame
        private double _errorBefore;

        /// <summary>The direction of the last Dec pulse, whoever sent it (guiding, calibration, a manual pulse).</summary>
        public int LastDirection => _lastDir;

        /// <summary>The extra milliseconds for a Dec pulse the given way (+1 North, -1 South), given the Dec error it corrects.</summary>
        public int Extra(int dir, double errorPx)
        {
            int extra = 0;
            if (Enabled && _lastDir != 0 && dir != _lastDir && AmountMs > 0)
            {
                extra = (int)Math.Round(AmountMs);
                _watching = true;
                _errorBefore = errorPx;
            }
            return extra;
        }

        /// <summary>A Dec pulse went out the given way.</summary>
        public void Pulsed(int dir) { if (dir != 0) _lastDir = dir; }

        /// <summary>
        /// The Dec error in the frame after a compensated turn: past zero by more than the minimum move is an overshoot (10 %
        /// less), still 80 % or more of the error on the same side is too little (10 % more).
        /// </summary>
        public void Next(double errorPx, double minMovePx)
        {
            if (!_watching) return;
            _watching = false;
            double lo = Math.Max(0, MinMs), hi = Math.Max(lo, MaxMs);
            if (Math.Sign(errorPx) == -Math.Sign(_errorBefore) && Math.Abs(errorPx) > minMovePx) AmountMs = Math.Max(lo, AmountMs * 0.9);
            else if (Math.Sign(errorPx) == Math.Sign(_errorBefore) && Math.Abs(errorPx) >= 0.8 * Math.Abs(_errorBefore)) AmountMs = Math.Min(hi, AmountMs * 1.1);
        }

        /// <summary>Forgets a pending adaptation (a dither or a pause), not which way the gears were last turned.</summary>
        public void Reset() { _watching = false; }
    }
}
