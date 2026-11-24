using System;
using System.Collections.Generic;
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

        /// <summary>Auto exposure: the exposure follows the guide star's SNR between a minimum and a maximum (s).</summary>
        public bool AutoExposure = false;
        public double AutoExposureMin = 1, AutoExposureMax = 5, TargetSnr = 10;
        /// <summary>Dark frames subtracted from guide frames, or bad pixels mended; how many darks make a master, and the bad-pixel threshold (σ).</summary>
        public DarkFrameKind DarkFrames = DarkFrameKind.None;
        public int DarkFrameCount = 5;
        public double BadPixelSigma = 5;

        /// <summary>Guiding on the guide star and up to MaxStars - 1 others.</summary>
        public bool MultiStar = true;
        public int MaxStars = 9;
        /// <summary>Frames whose guide star changes brightness by more than the tolerance (%) are not used for a correction.</summary>
        public bool StarMassCheck = true;
        public double StarMassTolerance = 50;

        /// <summary>The algorithm for each axis, and the settings of Lowpass2 (aggressiveness, %) and the Z-filter (exposure factor).</summary>
        public GuideAlgorithmKind RaAlgorithm = GuideAlgorithmKind.Hysteresis, DecAlgorithm = GuideAlgorithmKind.ResistSwitch;
        public double RaLowpassAggressiveness = 80, DecLowpassAggressiveness = 80;
        public double RaZFactor = 4, DecZFactor = 4;
        public DecGuideMode DecMode = DecGuideMode.Auto;
        /// <summary>Dec backlash compensation: the amount (ms), adapting between the minimum and maximum.</summary>
        public bool BacklashCompensation = false;
        public double BacklashMs = 0, BacklashMinMs = 0, BacklashMaxMs = 0;

        /// <summary>
        /// A sticky lock position stays where it is through a stop, a lost star and choosing a star again (not through a
        /// slew), and is kept here with the camera it belongs to.
        /// </summary>
        public bool StickyLock = false;
        public double StickyLockX = double.NaN, StickyLockY = double.NaN;
        public string StickyLockCamera = "";
        /// <summary>How far the lock position buttons move it on the sky (arcsec; pixels without a calibration).</summary>
        public double NudgeArcsec = 1;
        /// <summary>The length of a manual guide pulse (ms).</summary>
        public int ManualPulseMs = 1000;

        /// <summary>Comet tracking: where the rates come from, the rates (arcsec/h; RA as sky motion) and the comet.</summary>
        public CometRateSource CometSource = CometRateSource.Entered;
        public double CometRaArcsecPerHour, CometDecArcsecPerHour;
        public string CometName = "";

        /// <summary>The last Guiding Assistant runs, newest first.</summary>
        public List<GuidingAssistantResult> AssistantResults = new List<GuidingAssistantResult>();

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
