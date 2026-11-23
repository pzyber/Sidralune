using System;
using System.Collections.Generic;

namespace Sidralune.Devices.Guiding
{
    /// <summary>Turns one axis's guide error (pixels along that axis) into the correction to make (pixels, same sign).</summary>
    public interface IAxisAlgorithm
    {
        double Correction(double errorPx);
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

        public void Reset() { _recent.Clear(); _side = 0; }
    }
}
