using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Sidralune.Imaging.ColorManagement;

namespace Sidralune.UI
{
    /// <summary>
    /// The colour profile Windows uses for each monitor, for showing images through it. When Windows itself manages
    /// colour on a display (HDR, or automatic colour management on Windows 11), programs that draw with GDI are taken
    /// to be sRGB and converted by Windows, so then images are sent as sRGB to avoid converting twice.
    /// </summary>
    public static class MonitorColor
    {
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateDC(string driver, string device, string output, IntPtr init);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern bool GetICMProfile(IntPtr dc, ref int size, StringBuilder name);
        [DllImport("user32.dll")] private static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
        [DllImport("user32.dll")] private static extern int QueryDisplayConfig(uint flags, ref uint numPaths, byte[] paths, ref uint numModes, byte[] modes, IntPtr topology);
        [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(byte[] packet);

        public sealed class Info
        {
            /// <summary>The profile images are converted to.</summary>
            public IccProfile Profile;
            /// <summary>The monitor profile's file, or null when Windows has none set (then sRGB).</summary>
            public string Path;
            /// <summary>Windows converts from sRGB itself (HDR or automatic colour management).</summary>
            public bool WindowsManaged;
            public string Device;
        }

        private static readonly Dictionary<string, Info> Cache = new Dictionary<string, Info>();

        /// <summary>For tests: the profile every viewer converts to, whatever the monitor.</summary>
        public static IccProfile Override;

        /// <summary>Forget what is known about the monitors (after a change in Windows' colour settings).</summary>
        public static void Refresh() { lock (Cache) Cache.Clear(); }

        /// <summary>The monitor a control is on (UI thread).</summary>
        public static Info For(Control c)
        {
            if (Override != null) return new Info { Profile = Override, Device = "override" };
            string device;
            try { device = Screen.FromControl(c).DeviceName; } catch { device = Screen.PrimaryScreen.DeviceName; }
            return For(device);
        }

        public static Info For(string device)
        {
            lock (Cache)
            {
                Info info;
                if (Cache.TryGetValue(device, out info)) return info;
                info = Read(device);
                Cache[device] = info;
                return info;
            }
        }

        private static Info Read(string device)
        {
            var info = new Info { Device = device, Profile = IccProfiles.Srgb };
            try { info.WindowsManaged = AdvancedColor(device); } catch { }
            if (info.WindowsManaged) return info;
            try
            {
                var dc = CreateDC(null, device, null, IntPtr.Zero);
                if (dc != IntPtr.Zero)
                {
                    try
                    {
                        int size = 520;
                        var name = new StringBuilder(size);
                        if (GetICMProfile(dc, ref size, name) && File.Exists(name.ToString()))
                        {
                            var p = IccProfile.Load(name.ToString());
                            if (p.IsRgb) { info.Profile = p; info.Path = name.ToString(); }
                        }
                    }
                    finally { DeleteDC(dc); }
                }
            }
            catch (Exception ex) { Core.Util.Log.Warn("Colour", "Monitor profile of " + device + ": " + ex.Message); }
            return info;
        }

        /// <summary>True when Windows' advanced colour (HDR, or automatic colour management) is on for the display.</summary>
        private static bool AdvancedColor(string gdiDevice)
        {
            const uint QDC_ONLY_ACTIVE_PATHS = 2;
            uint np, nm;
            if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out np, out nm) != 0) return false;
            const int pathSize = 72, modeSize = 64;
            var paths = new byte[np * pathSize];
            var modes = new byte[nm * modeSize];
            if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref np, paths, ref nm, modes, IntPtr.Zero) != 0) return false;
            for (int i = 0; i < np; i++)
            {
                int p = i * pathSize;
                // DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME (1): header (type, size, adapter LUID, id) + 32 WCHARs
                var src = new byte[20 + 64];
                Put(src, 0, 1); Put(src, 4, src.Length);
                Array.Copy(paths, p, src, 8, 8);        // source adapter LUID
                Array.Copy(paths, p + 8, src, 16, 4);   // source id
                if (DisplayConfigGetDeviceInfo(src) != 0) continue;
                string name = Encoding.Unicode.GetString(src, 20, 64).TrimEnd('\0');
                if (!string.Equals(name, gdiDevice, StringComparison.OrdinalIgnoreCase)) continue;
                // DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO (9): header + value bits + encoding + bits per channel
                var adv = new byte[20 + 12];
                Put(adv, 0, 9); Put(adv, 4, adv.Length);
                Array.Copy(paths, p + 20, adv, 8, 8);   // target adapter LUID
                Array.Copy(paths, p + 28, adv, 16, 4);  // target id
                if (DisplayConfigGetDeviceInfo(adv) != 0) return false;
                uint bits = BitConverter.ToUInt32(adv, 20);
                bool enabled = (bits & 2) != 0, wideColourEnforced = (bits & 4) != 0;
                return enabled || wideColourEnforced;
            }
            return false;
        }

        private static void Put(byte[] b, int o, int v) { var x = BitConverter.GetBytes(v); Array.Copy(x, 0, b, o, 4); }
    }
}
