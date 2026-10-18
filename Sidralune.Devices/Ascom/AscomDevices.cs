using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Sidralune.Imaging;

namespace Sidralune.Devices.Ascom
{
    /// <summary>
    /// ASCOM Platform access through late-bound COM. No ASCOM assemblies are referenced: drivers are created from their
    /// ProgID and called dynamically, so Sidralune runs whether or not the ASCOM Platform is installed.
    /// </summary>
    public static class AscomDiscovery
    {
        public static bool PlatformInstalled => Type.GetTypeFromProgID("ASCOM.Utilities.Util") != null;

        private static string KeyName(DeviceKind k)
        {
            switch (k)
            {
                case DeviceKind.Telescope: return "Telescope";
                case DeviceKind.Camera: return "Camera";
                case DeviceKind.Focuser: return "Focuser";
                case DeviceKind.FilterWheel: return "FilterWheel";
                case DeviceKind.Dome: return "Dome";
                case DeviceKind.Rotator: return "Rotator";
                case DeviceKind.Switch: return "Switch";
                case DeviceKind.SafetyMonitor: return "SafetyMonitor";
                case DeviceKind.ObservingConditions: return "ObservingConditions";
                case DeviceKind.CoverCalibrator: return "CoverCalibrator";
            }
            return null;
        }

        /// <summary>Registered drivers of a type (from the ASCOM profile store in the 32-bit registry view).</summary>
        public static List<DeviceInfo> Drivers(DeviceKind kind)
        {
            var list = new List<DeviceInfo>();
            string key = KeyName(kind);
            if (key == null) return list;
            foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
            {
                try
                {
                    using (var hk = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                    using (var k = hk.OpenSubKey(@"SOFTWARE\ASCOM\" + key + " Drivers"))
                    {
                        if (k == null) continue;
                        foreach (var progId in k.GetSubKeyNames())
                        {
                            if (list.Any(d => d.Address == progId)) continue;
                            using (var dk = k.OpenSubKey(progId))
                            {
                                string name = dk?.GetValue("") as string ?? progId;
                                list.Add(new DeviceInfo { Kind = kind, Protocol = DeviceProtocol.Ascom, Name = name, Address = progId });
                            }
                        }
                    }
                }
                catch { }
            }
            return list;
        }

        /// <summary>Show the ASCOM Chooser (if installed); returns the selected ProgID or null.</summary>
        public static string Choose(DeviceKind kind, string current = "")
        {
            var t = Type.GetTypeFromProgID("ASCOM.Utilities.Chooser");
            if (t == null) return null;
            dynamic ch = Activator.CreateInstance(t);
            try
            {
                ch.DeviceType = KeyName(kind);
                string id = ch.Choose(current ?? "");
                return string.IsNullOrEmpty(id) ? null : id;
            }
            finally { Marshal.ReleaseComObject(ch); }
        }

        public static IDevice Create(DeviceInfo info)
        {
            switch (info.Kind)
            {
                case DeviceKind.Telescope: return new AscomTelescope(info);
                case DeviceKind.Camera: return new AscomCamera(info);
                case DeviceKind.Focuser: return new AscomFocuser(info);
                case DeviceKind.FilterWheel: return new AscomFilterWheel(info);
                case DeviceKind.Dome: return new AscomDome(info);
                case DeviceKind.Rotator: return new AscomRotator(info);
                case DeviceKind.Switch: return new AscomSwitch(info);
                case DeviceKind.SafetyMonitor: return new AscomSafety(info);
                case DeviceKind.ObservingConditions: return new AscomWeather(info);
                case DeviceKind.CoverCalibrator: return new AscomCover(info);
            }
            throw new NotSupportedException(info.Kind.ToString());
        }
    }

    /// <summary>Base for ASCOM COM drivers.</summary>
    public abstract class AscomBase : DeviceBase
    {
        protected dynamic D;

        protected AscomBase(DeviceInfo info) { Info = info; }

        public override void Connect()
        {
            var t = Type.GetTypeFromProgID(Info.Address);
            if (t == null) throw new InvalidOperationException("ASCOM driver not registered: " + Info.Address);
            D = Activator.CreateInstance(t);
            try { D.Connected = true; }
            catch { try { D.Link = true; } catch { throw; } } // very old focuser drivers use Link
            Connected = true;
        }

        public override void Disconnect()
        {
            if (D == null) return;
            try { D.Connected = false; } catch { }
            try { Marshal.ReleaseComObject(D); } catch { }
            D = null;
            Connected = false;
        }

        public override string Description { get { try { return D?.Description ?? Info.Name; } catch { return Info.Name; } } }

        /// <summary>Open the driver's own setup dialog.</summary>
        public void SetupDialog()
        {
            bool created = D == null;
            if (created) D = Activator.CreateInstance(Type.GetTypeFromProgID(Info.Address));
            try { D.SetupDialog(); }
            finally { if (created) { try { Marshal.ReleaseComObject(D); } catch { } D = null; } }
        }

        protected T Try<T>(Func<T> f, T fallback) { try { return f(); } catch { return fallback; } }
    }

    public class AscomTelescope : AscomBase, ITelescope
    {
        public AscomTelescope(DeviceInfo i) : base(i) { }
        public double RightAscension => D.RightAscension;
        public double Declination => D.Declination;
        public double Altitude => Try(() => (double)D.Altitude, double.NaN);
        public double Azimuth => Try(() => (double)D.Azimuth, double.NaN);
        public double SiderealTime => Try(() => (double)D.SiderealTime, double.NaN);
        public bool Slewing => Try(() => (bool)D.Slewing, false);
        public bool Tracking { get { return Try(() => (bool)D.Tracking, false); } set { D.Tracking = value; } }
        public TrackingRate TrackingRate { get { return (TrackingRate)Try(() => (int)D.TrackingRate, 0); } set { D.TrackingRate = (int)value; } }
        public bool AtPark => Try(() => (bool)D.AtPark, false);
        public PierSide SideOfPier => (PierSide)Try(() => (int)D.SideOfPier, -1);
        public bool CanPark => Try(() => (bool)D.CanPark, false);
        public bool CanSync => Try(() => (bool)D.CanSync, false);
        public bool CanPulseGuide => Try(() => (bool)D.CanPulseGuide, false);
        public bool CanSlewAltAz => Try(() => (bool)D.CanSlewAltAzAsync, false);
        public double SiteLatitude => Try(() => (double)D.SiteLatitude, double.NaN);
        public double SiteLongitude => Try(() => (double)D.SiteLongitude, double.NaN);
        public void SlewToCoordinatesAsync(double ra, double dec) { if (!Tracking) Tracking = true; D.SlewToCoordinatesAsync(ra, dec); }
        public void SlewToAltAzAsync(double az, double alt) => D.SlewToAltAzAsync(az, alt);
        public void SyncToCoordinates(double ra, double dec) => D.SyncToCoordinates(ra, dec);
        public void AbortSlew() => D.AbortSlew();
        public void Park() => D.Park();
        public void Unpark() => D.Unpark();
        public void SetPark() => D.SetPark();
        public void PulseGuide(GuideDirection dir, int ms) => D.PulseGuide((int)dir, ms);
        public void MoveAxis(int axis, double rate) => D.MoveAxis(axis, rate);
        public double[] AxisRates
        {
            get
            {
                try
                {
                    var rates = new List<double>();
                    foreach (dynamic r in D.AxisRates(0)) rates.Add((double)r.Maximum);
                    return rates.ToArray();
                }
                catch { return new[] { 0.0083, 0.25, 1.0, 3.0 }; }
            }
        }
        public PierSide DestinationSideOfPier(double ra, double dec) => (PierSide)Try(() => (int)D.DestinationSideOfPier(ra, dec), -1);
    }

    public class AscomCamera : AscomBase, ICamera
    {
        public AscomCamera(DeviceInfo i) : base(i) { }
        public int SensorWidth => D.CameraXSize;
        public int SensorHeight => D.CameraYSize;
        public double PixelSizeX => D.PixelSizeX;
        public double PixelSizeY => D.PixelSizeY;
        public int BinX { get { return D.BinX; } set { D.BinX = (short)value; } }
        public int BinY { get { return D.BinY; } set { D.BinY = (short)value; } }
        public int MaxBin => Try(() => (int)D.MaxBinX, 1);
        public int StartX { get { return D.StartX; } set { D.StartX = value; } }
        public int StartY { get { return D.StartY; } set { D.StartY = value; } }
        public int NumX { get { return D.NumX; } set { D.NumX = value; } }
        public int NumY { get { return D.NumY; } set { D.NumY = value; } }
        public CameraState State => (CameraState)Try(() => (int)D.CameraState, 0);
        public bool ImageReady => Try(() => (bool)D.ImageReady, false);
        public bool CanSetTemperature => Try(() => (bool)D.CanSetCCDTemperature, false);
        public bool CoolerOn { get { return Try(() => (bool)D.CoolerOn, false); } set { D.CoolerOn = value; } }
        public double CcdTemperature => Try(() => (double)D.CCDTemperature, double.NaN);
        public double SetTemperature { get { return Try(() => (double)D.SetCCDTemperature, double.NaN); } set { D.SetCCDTemperature = value; } }
        public double CoolerPower => Try(() => (double)D.CoolerPower, double.NaN);
        public bool HasGain => Try(() => { var g = (short)D.Gain; return true; }, false);
        public int Gain { get { return Try(() => (int)D.Gain, 0); } set { D.Gain = (short)value; } }
        public int GainMin => Try(() => (int)D.GainMin, 0);
        public int GainMax => Try(() => (int)D.GainMax, 0);
        public bool HasOffset => Try(() => { var o = (int)D.Offset; return true; }, false);
        public int Offset { get { return Try(() => (int)D.Offset, 0); } set { D.Offset = value; } }
        public int BitDepth { get { int max = Try(() => (int)D.MaxADU, 65535); int b = 1; while ((1L << b) - 1 < max && b < 32) b++; return b; } }
        public BayerPattern Bayer
        {
            get
            {
                int st = Try(() => (int)D.SensorType, 0);
                if (st != 2) return BayerPattern.None;
                int ox = Try(() => (int)D.BayerOffsetX, 0), oy = Try(() => (int)D.BayerOffsetY, 0);
                return Debayer.Shift(BayerPattern.RGGB, ox, oy);
            }
        }
        public double ElectronsPerAdu => Try(() => (double)D.ElectronsPerADU, 1.0);
        public double PercentCompleted => Try(() => (double)D.PercentCompleted, 0);
        public void StartExposure(double s, bool light) => D.StartExposure(s, light);
        public void AbortExposure() { try { D.AbortExposure(); } catch { D.StopExposure(); } }

        public AstroImage GetImage()
        {
            object arr = D.ImageArray;
            int maxAdu = Try(() => (int)D.MaxADU, 65535);
            var img = ConvertArray(arr, maxAdu);
            img.Bayer = Bayer;
            img.ExposureSeconds = Try(() => (double)D.LastExposureDuration, 0);
            try
            {
                string start = D.LastExposureStartTime;
                DateTime dt;
                if (DateTime.TryParse(start, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out dt)) img.DateObs = dt;
            }
            catch { }
            img.Header.Set("INSTRUME", Info.Name);
            img.Header.Set("XPIXSZ", PixelSizeX * BinX); img.Header.Set("YPIXSZ", PixelSizeY * BinY);
            img.Header.Set("XBINNING", BinX); img.Header.Set("YBINNING", BinY);
            if (!double.IsNaN(CcdTemperature)) img.Header.Set("CCD-TEMP", CcdTemperature);
            if (HasGain) img.Header.Set("GAIN", Gain);
            if (HasOffset) img.Header.Set("OFFSET", Offset);
            return img;
        }

        /// <summary>Convert an ASCOM ImageArray (int[x,y] or int[x,y,plane]) to an AstroImage.</summary>
        public static AstroImage ConvertArray(object arr, int maxAdu)
        {
            var a = (Array)arr;
            int w = a.GetLength(0), h = a.GetLength(1);
            int planes = a.Rank == 3 ? a.GetLength(2) : 1;
            var img = new AstroImage(w, h, planes);
            double s = 1.0 / Math.Max(1, maxAdu);
            if (a.Rank == 2)
            {
                var ia = arr as int[,];
                for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) img.Data[0][y * w + x] = (float)((ia != null ? ia[x, y] : Convert.ToDouble(a.GetValue(x, y))) * s);
            }
            else
            {
                for (int c = 0; c < planes; c++) for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) img.Data[c][y * w + x] = (float)(Convert.ToDouble(a.GetValue(x, y, c)) * s);
            }
            img.OriginalBitDepth = 16;
            return img;
        }
    }

    public class AscomFocuser : AscomBase, IFocuser
    {
        public AscomFocuser(DeviceInfo i) : base(i) { }
        public int Position => Try(() => (int)D.Position, 0);
        public int MaxStep => Try(() => (int)D.MaxStep, 100000);
        public bool IsMoving => Try(() => (bool)D.IsMoving, false);
        public bool Absolute => Try(() => (bool)D.Absolute, true);
        public double Temperature => Try(() => (double)D.Temperature, double.NaN);
        public double StepSizeMicrons => Try(() => (double)D.StepSize, double.NaN);
        public void Move(int p) => D.Move(p);
        public void Halt() => D.Halt();
    }

    public class AscomFilterWheel : AscomBase, IFilterWheel
    {
        public AscomFilterWheel(DeviceInfo i) : base(i) { }
        public int Position { get { return Try(() => (int)D.Position, -1); } set { D.Position = (short)value; } }
        public string[] Names => Try(() => ((Array)D.Names).Cast<object>().Select(o => o.ToString()).ToArray(), new string[0]);
        public int[] FocusOffsets => Try(() => ((Array)D.FocusOffsets).Cast<object>().Select(Convert.ToInt32).ToArray(), new int[0]);
        public bool IsMoving => Position < 0;
    }

    public class AscomDome : AscomBase, IDome
    {
        public AscomDome(DeviceInfo i) : base(i) { }
        public double Azimuth => Try(() => (double)D.Azimuth, double.NaN);
        public ShutterState Shutter => (ShutterState)Try(() => (int)D.ShutterStatus, 5);
        public bool Slewing => Try(() => (bool)D.Slewing, false);
        public bool AtPark => Try(() => (bool)D.AtPark, false);
        public bool CanSetAzimuth => Try(() => (bool)D.CanSetAzimuth, false);
        public bool Slaved { get { return Try(() => (bool)D.Slaved, false); } set { D.Slaved = value; } }
        public void SlewToAzimuth(double az) => D.SlewToAzimuth(az);
        public void OpenShutter() => D.OpenShutter();
        public void CloseShutter() => D.CloseShutter();
        public void Park() => D.Park();
        public void FindHome() => D.FindHome();
        public void AbortSlew() => D.AbortSlew();
    }

    public class AscomRotator : AscomBase, IRotator
    {
        public AscomRotator(DeviceInfo i) : base(i) { }
        public double Position => Try(() => (double)D.Position, double.NaN);
        public bool IsMoving => Try(() => (bool)D.IsMoving, false);
        public bool Reverse { get { return Try(() => (bool)D.Reverse, false); } set { D.Reverse = value; } }
        public void MoveAbsolute(double deg) => D.MoveAbsolute((float)deg);
        public void Halt() => D.Halt();
    }

    public class AscomSwitch : AscomBase, ISwitchDevice
    {
        public AscomSwitch(DeviceInfo i) : base(i) { }
        public int Count => Try(() => (int)D.MaxSwitch, 0);
        public string GetName(int i) => D.GetSwitchName((short)i);
        public bool GetSwitch(int i) => D.GetSwitch((short)i);
        public void SetSwitch(int i, bool on) => D.SetSwitch((short)i, on);
        public double GetValue(int i) => D.GetSwitchValue((short)i);
        public void SetValue(int i, double v) => D.SetSwitchValue((short)i, v);
    }

    public class AscomSafety : AscomBase, ISafetyMonitor
    {
        public AscomSafety(DeviceInfo i) : base(i) { }
        public bool IsSafe => Try(() => (bool)D.IsSafe, false);
    }

    public class AscomWeather : AscomBase, IObservingConditions
    {
        public AscomWeather(DeviceInfo i) : base(i) { }
        public double Temperature => Try(() => (double)D.Temperature, double.NaN);
        public double Humidity => Try(() => (double)D.Humidity, double.NaN);
        public double DewPoint => Try(() => (double)D.DewPoint, double.NaN);
        public double Pressure => Try(() => (double)D.Pressure, double.NaN);
        public double CloudCover => Try(() => (double)D.CloudCover, double.NaN);
        public double WindSpeed => Try(() => (double)D.WindSpeed, double.NaN);
        public double RainRate => Try(() => (double)D.RainRate, double.NaN);
        public double SkyQuality => Try(() => (double)D.SkyQuality, double.NaN);
        public double SkyTemperature => Try(() => (double)D.SkyTemperature, double.NaN);
    }

    public class AscomCover : AscomBase, ICoverCalibrator
    {
        public AscomCover(DeviceInfo i) : base(i) { }
        public string CoverState => Try(() => ((int)D.CoverState).ToString(), "?");
        public int Brightness => Try(() => (int)D.Brightness, 0);
        public int MaxBrightness => Try(() => (int)D.MaxBrightness, 0);
        public void OpenCover() => D.OpenCover();
        public void CloseCover() => D.CloseCover();
        public void CalibratorOn(int b) => D.CalibratorOn(b);
        public void CalibratorOff() => D.CalibratorOff();
    }
}
