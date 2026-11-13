using System;

namespace Sidralune.Imaging.ColorManagement
{
    /// <summary>What happens to an opened image's profile.</summary>
    public enum ProfileChoice { Keep, ConvertToWorking, Discard, AssignWorking, LeaveUntagged }

    /// <summary>
    /// Applies Settings → Colour to an image the user opens in a view. Frames read in bulk (stacking, sequences,
    /// captures) are not passed through this: they keep what their files have, and nothing ever asks about them.
    /// </summary>
    public static class ProfilePolicy
    {
        /// <summary>The working profile for an image's channels.</summary>
        public static IccProfile WorkingFor(AstroImage img) => img.IsColor ? ColorSettings.WorkingProfile : ColorSettings.WorkingGrayProfile;

        /// <param name="ask">
        /// Called when the setting is Ask: (the image, true when its profile differs from the working profile or false
        /// when it has none) → the choice, or null to leave the image as it is.
        /// </param>
        /// <returns>What was done, or null when nothing needed doing.</returns>
        public static ProfileChoice? OnOpen(AstroImage img, Func<AstroImage, bool, ProfileChoice?> ask = null)
        {
            if (img == null) return null;
            var working = WorkingFor(img);
            ProfileChoice choice;
            if (img.Profile != null)
            {
                if (img.Profile.SameAs(working)) return null;
                switch (ColorSettings.OnMismatch)
                {
                    case ProfileMismatchPolicy.ConvertToWorking: choice = ProfileChoice.ConvertToWorking; break;
                    case ProfileMismatchPolicy.Discard: choice = ProfileChoice.Discard; break;
                    case ProfileMismatchPolicy.Ask: choice = ask?.Invoke(img, true) ?? ProfileChoice.Keep; break;
                    default: choice = ProfileChoice.Keep; break;
                }
            }
            else
            {
                switch (ColorSettings.OnMissing)
                {
                    case ProfileMissingPolicy.AssignWorking: choice = ProfileChoice.AssignWorking; break;
                    case ProfileMissingPolicy.Ask: choice = ask?.Invoke(img, false) ?? ProfileChoice.LeaveUntagged; break;
                    default: choice = ProfileChoice.LeaveUntagged; break;
                }
            }
            Apply(img, choice);
            return choice;
        }

        /// <summary>Carries out a choice on an image.</summary>
        public static void Apply(AstroImage img, ProfileChoice choice)
        {
            var working = WorkingFor(img);
            switch (choice)
            {
                case ProfileChoice.ConvertToWorking:
                    if (img.Profile != null && !img.Profile.SameAs(working)) ColorTransform.Create(img.Profile, working, ColorSettings.Intent).Apply(img);
                    img.Profile = working;
                    break;
                case ProfileChoice.Discard: img.Profile = null; break;
                case ProfileChoice.AssignWorking: img.Profile = working; break;
            }
        }
    }
}
