using System;
using System.Collections.Generic;
using System.Linq;

namespace Sidralune.Devices
{
    /// <summary>How the guide camera sees the sky: through the imaging telescope (an off-axis guider) or through a guide scope of its own.</summary>
    public enum GuideOptics { OffAxisGuider, GuideScope }

    /// <summary>
    /// One rig: the device connected for each kind, the imaging telescope (one of the planner's telescopes, so its focal
    /// length is entered once) with any reducer or Barlow, the camera the planner uses for it, how the guide camera sees
    /// the sky, and the guider's own settings file (its calibration and tuning belong to the rig). The site is not part of
    /// it: the same rig goes to another site, and the planner needs a site without any equipment.
    /// </summary>
    public class EquipmentProfile
    {
        public string Name = "";
        /// <summary>The imaging telescope: a telescope of the planner's list, by name.</summary>
        public string Telescope = "";
        /// <summary>A reducer (below 1) or Barlow (above 1) in front of the imaging camera.</summary>
        public double FocalFactor = 1;
        /// <summary>The imaging camera as the planner knows it (a camera of its list, by name), for planning with this rig.</summary>
        public string Camera = "";
        public GuideOptics Guiding = GuideOptics.GuideScope;
        /// <summary>The guide scope's focal length (mm); with an off-axis guider the imaging telescope's is used.</summary>
        public double GuideFocalLengthMm = 240;
        /// <summary>The guider's settings file in the Settings folder: its calibration, algorithms and tuning for this rig.</summary>
        public string GuiderFile = "guider.json";
        /// <summary>The device last connected for each kind on this rig.</summary>
        public List<DeviceInfo> Devices = new List<DeviceInfo>();

        /// <summary>The device remembered for a kind, or null.</summary>
        public DeviceInfo DeviceFor(DeviceKind kind) => Devices.FirstOrDefault(d => d != null && d.Kind == kind);

        /// <summary>Remembers a device as this rig's for its kind (replacing the one remembered before).</summary>
        public void Remember(DeviceInfo d)
        {
            if (d == null) return;
            Devices.RemoveAll(x => x == null || x.Kind == d.Kind);
            Devices.Add(new DeviceInfo { Kind = d.Kind, Protocol = d.Protocol, Name = d.Name, Address = d.Address, Description = d.Description });
        }

        /// <summary>The imaging focal length (mm): the telescope's, through the reducer or Barlow.</summary>
        public double ImagingFocalLength(double telescopeFocalMm) => telescopeFocalMm * (FocalFactor > 0 ? FocalFactor : 1);

        /// <summary>The guide camera's focal length (mm): the imaging focal length through an off-axis guider, else the guide scope's.</summary>
        public double GuideFocalLength(double imagingFocalMm) => Guiding == GuideOptics.OffAxisGuider ? imagingFocalMm : GuideFocalLengthMm;

        /// <summary>A copy under another name, with its own guider settings file.</summary>
        public EquipmentProfile Copy(string name, string guiderFile)
        {
            var p = (EquipmentProfile)MemberwiseClone();
            p.Name = name;
            p.GuiderFile = guiderFile;
            p.Devices = new List<DeviceInfo>();
            foreach (var d in Devices) p.Remember(d);
            return p;
        }

        /// <summary>A guider settings file name no profile uses yet.</summary>
        public static string NewGuiderFile(IEnumerable<EquipmentProfile> existing)
        {
            var used = new HashSet<string>(existing.Select(p => p.GuiderFile), StringComparer.OrdinalIgnoreCase);
            for (int i = 2; ; i++)
            {
                var f = "guider-" + i + ".json";
                if (!used.Contains(f)) return f;
            }
        }
    }
}
