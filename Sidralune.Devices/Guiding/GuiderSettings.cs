using System;
using System.IO;
using Sidralune.Core.Util;

namespace Sidralune.Devices.Guiding
{
    /// <summary>Where the Sidralune guider sends its corrections.</summary>
    public enum GuideOutputKind { Mount, Camera }

    /// <summary>The Sidralune guider's options and its last calibration, kept in Settings\guider.json.</summary>
    public class GuiderSettings
    {
        public double ExposureSeconds = 2;
        public int Binning = 1;
        /// <summary>Pulses through the mount's driver, or through the guide camera's ST-4 port.</summary>
        public GuideOutputKind Output = GuideOutputKind.Mount;
        /// <summary>The guide scope's focal length (mm), for the pixel scale.</summary>
        public double FocalLengthMm = 240;
        /// <summary>Half the side of the square searched for the guide star (pixels).</summary>
        public int SearchRadius = 15;
        /// <summary>Calibration pulse (ms); 0 works it out from the guide rate, the pixel scale and the declination.</summary>
        public int CalibrationStepMs = 0;
        /// <summary>How far each calibration leg moves the star (pixels).</summary>
        public double CalibrationDistancePx = 20;
        /// <summary>RA (Hysteresis): percentages and the minimum move in pixels.</summary>
        public double RaAggressiveness = 70, RaHysteresis = 10, RaMinMovePx = 0.2;
        /// <summary>Dec (Resist Switch): percentage and the minimum move in pixels.</summary>
        public double DecAggressiveness = 100, DecMinMovePx = 0.2;
        public bool DecFastSwitch = true;
        public int MaxRaPulseMs = 2500, MaxDecPulseMs = 2500;
        /// <summary>
        /// The mount turns its Dec pulses round itself after a meridian flip, so North still moves north. Most mounts do
        /// not, which is the default.
        /// </summary>
        public bool MountReversesDecAfterFlip = false;
        /// <summary>The last calibration, reused while it fits the equipment; null until there is one.</summary>
        public GuideCalibration Calibration;

        public static string DefaultPath => Path.Combine(AppPaths.Settings, "guider.json");

        /// <summary>The saved settings, or the defaults when there are none (or they cannot be read).</summary>
        public static GuiderSettings Load(string path = null)
        {
            path = path ?? DefaultPath;
            try
            {
                if (File.Exists(path))
                {
                    var s = Json.PopulateType(typeof(GuiderSettings), Json.Parse(File.ReadAllText(path))) as GuiderSettings;
                    if (s != null) return s;
                }
            }
            catch (Exception ex) { Log.Warn("Guider", "Could not read the guider settings: " + ex.Message); }
            return new GuiderSettings();
        }

        public void Save(string path = null)
        {
            try { File.WriteAllText(path ?? DefaultPath, Json.Serialize(this, true)); }
            catch (Exception ex) { Log.Warn("Guider", "Could not save the guider settings: " + ex.Message); }
        }
    }
}
