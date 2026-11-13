using System;

namespace Sidralune.Imaging.ColorManagement
{
    /// <summary>What to do with an image whose embedded profile differs from the working profile.</summary>
    public enum ProfileMismatchPolicy { Keep, ConvertToWorking, Discard, Ask }

    /// <summary>What to do with an image that has no profile.</summary>
    public enum ProfileMissingPolicy { LeaveUntagged, AssignWorking, Ask }

    /// <summary>
    /// The colour management choices (Settings → Colour), for the imaging code: the app fills them in from its
    /// settings and calls RaiseChanged when they change.
    /// </summary>
    public static class ColorSettings
    {
        /// <summary>Show images through the monitor's profile (and allow proofing).</summary>
        public static bool Enabled = true;

        private static IccProfile _working;
        /// <summary>The profile untagged colour images are taken to be in. ProPhoto RGB unless set.</summary>
        public static IccProfile WorkingProfile
        {
            get { return _working ?? IccProfiles.ProPhoto; }
            set { _working = value != null && value.IsRgb ? value : null; }
        }

        /// <summary>The grey profile untagged one-channel images are taken to be in (the working profile's curve).</summary>
        public static IccProfile WorkingGrayProfile => IccProfiles.GrayFor(WorkingProfile);

        public static RenderingIntent Intent = RenderingIntent.RelativeColorimetric;
        public static ProfileMismatchPolicy OnMismatch = ProfileMismatchPolicy.Keep;
        public static ProfileMissingPolicy OnMissing = ProfileMissingPolicy.LeaveUntagged;
        /// <summary>Write the image's profile into saved files.</summary>
        public static bool EmbedProfiles = true;
        /// <summary>Convert 8-bit PNG and JPEG exports to sRGB (otherwise they keep the image's profile).</summary>
        public static bool Export8BitAsSrgb = false;

        private static IccProfile _proofing;
        /// <summary>The profile soft-proofing simulates (sRGB unless set).</summary>
        public static IccProfile ProofingProfile
        {
            get { return _proofing ?? IccProfiles.Srgb; }
            set { _proofing = value; }
        }

        public static RenderingIntent ProofingIntent = RenderingIntent.RelativeColorimetric;
        /// <summary>The colour that marks out-of-gamut pixels (ARGB).</summary>
        public static int GamutWarningColor = unchecked((int)0xFF808080);

        public static event Action Changed;
        public static void RaiseChanged() => Changed?.Invoke();
    }
}
