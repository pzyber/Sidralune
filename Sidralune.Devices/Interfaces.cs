using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Sidralune.Imaging;

namespace Sidralune.Devices
{
    public enum DeviceKind { Telescope, Camera, Focuser, FilterWheel, Dome, Rotator, Switch, SafetyMonitor, ObservingConditions, CoverCalibrator, Guider }

    public enum DeviceProtocol { Simulator, Ascom, Alpaca, Indi, Lx200, Phd2 }

    /// <summary>Identifies a device a user can connect to.</summary>
    public class DeviceInfo
    {
        public DeviceKind Kind;
        public DeviceProtocol Protocol;
        public string Name;
        /// <summary>Protocol-specific address: ASCOM ProgID, Alpaca "host:port/number", INDI "host:port/device", serial port or "host:port".</summary>
        public string Address;
        public string Description;
        public override string ToString() => Name + " (" + Protocol + ")";
    }

    public interface IDevice : IDisposable
    {
        DeviceInfo Info { get; }
        bool Connected { get; }
        void Connect();
        void Disconnect();
        string Description { get; }
        /// <summary>Raised when connection state or a significant property changes.</summary>
        event EventHandler StateChanged;
    }

    public enum PierSide { Unknown = -1, East = 0, West = 1 }
    public enum TrackingRate { Sidereal = 0, Lunar = 1, Solar = 2, King = 3 }
    public enum GuideDirection { North = 0, South = 1, East = 2, West = 3 }

    public interface ITelescope : IDevice
    {
        /// <summary>Apparent (JNow) right ascension in hours and declination in degrees.</summary>
        double RightAscension { get; }
        double Declination { get; }
        double Altitude { get; }
        double Azimuth { get; }
        double SiderealTime { get; }
        bool Slewing { get; }
        bool Tracking { get; set; }
        TrackingRate TrackingRate { get; set; }
        bool AtPark { get; }
        PierSide SideOfPier { get; }
        bool CanPark { get; }
        bool CanSync { get; }
        bool CanPulseGuide { get; }
        bool CanSlewAltAz { get; }
        double SiteLatitude { get; }
        double SiteLongitude { get; }
        /// <summary>Slew to JNow coordinates (hours, degrees). Returns when the slew has started.</summary>
        void SlewToCoordinatesAsync(double raHours, double decDeg);
        void SlewToAltAzAsync(double azDeg, double altDeg);
        void SyncToCoordinates(double raHours, double decDeg);
        void AbortSlew();
        void Park();
        void Unpark();
        void SetPark();
        void PulseGuide(GuideDirection dir, int ms);
        /// <summary>Move an axis at a rate (deg/s); 0 stops. axis 0 = primary (RA/Az), 1 = secondary.</summary>
        void MoveAxis(int axis, double rateDegPerSec);
        double[] AxisRates { get; }
        /// <summary>Destination side of pier for a target (for meridian flip logic).</summary>
        PierSide DestinationSideOfPier(double raHours, double decDeg);
    }

    public enum CameraState { Idle = 0, Waiting = 1, Exposing = 2, Reading = 3, Download = 4, Error = 5 }

    public interface ICamera : IDevice
    {
        int SensorWidth { get; }
        int SensorHeight { get; }
        double PixelSizeX { get; }
        double PixelSizeY { get; }
        int BinX { get; set; }
        int BinY { get; set; }
        int MaxBin { get; }
        int StartX { get; set; }
        int StartY { get; set; }
        int NumX { get; set; }
        int NumY { get; set; }
        CameraState State { get; }
        bool ImageReady { get; }
        bool CanSetTemperature { get; }
        bool CoolerOn { get; set; }
        double CcdTemperature { get; }
        double SetTemperature { get; set; }
        double CoolerPower { get; }
        bool HasGain { get; }
        int Gain { get; set; }
        int GainMin { get; }
        int GainMax { get; }
        bool HasOffset { get; }
        int Offset { get; set; }
        int BitDepth { get; }
        BayerPattern Bayer { get; }
        double ElectronsPerAdu { get; }
        double PercentCompleted { get; }
        void StartExposure(double seconds, bool light);
        void AbortExposure();
        /// <summary>Download the last image (normalised 0..1).</summary>
        AstroImage GetImage();
    }

    public interface IFocuser : IDevice
    {
        int Position { get; }
        int MaxStep { get; }
        bool IsMoving { get; }
        bool Absolute { get; }
        double Temperature { get; }
        double StepSizeMicrons { get; }
        void Move(int position);
        void Halt();
    }

    public interface IFilterWheel : IDevice
    {
        int Position { get; set; }
        string[] Names { get; }
        int[] FocusOffsets { get; }
        bool IsMoving { get; }
    }

    public enum ShutterState { Open = 0, Closed = 1, Opening = 2, Closing = 3, Error = 4, Unknown = 5 }

    public interface IDome : IDevice
    {
        double Azimuth { get; }
        ShutterState Shutter { get; }
        bool Slewing { get; }
        bool AtPark { get; }
        bool CanSetAzimuth { get; }
        bool Slaved { get; set; }
        void SlewToAzimuth(double azDeg);
        void OpenShutter();
        void CloseShutter();
        void Park();
        void FindHome();
        void AbortSlew();
    }

    public interface IRotator : IDevice
    {
        double Position { get; }
        bool IsMoving { get; }
        bool Reverse { get; set; }
        void MoveAbsolute(double deg);
        void Halt();
    }

    public interface ISwitchDevice : IDevice
    {
        int Count { get; }
        string GetName(int i);
        bool GetSwitch(int i);
        void SetSwitch(int i, bool on);
        double GetValue(int i);
        void SetValue(int i, double v);
    }

    public interface ISafetyMonitor : IDevice
    {
        bool IsSafe { get; }
    }

    public interface IObservingConditions : IDevice
    {
        double Temperature { get; }
        double Humidity { get; }
        double DewPoint { get; }
        double Pressure { get; }
        double CloudCover { get; }
        double WindSpeed { get; }
        double RainRate { get; }
        double SkyQuality { get; }
        double SkyTemperature { get; }
    }

    public interface ICoverCalibrator : IDevice
    {
        string CoverState { get; }
        int Brightness { get; }
        int MaxBrightness { get; }
        void OpenCover();
        void CloseCover();
        void CalibratorOn(int brightness);
        void CalibratorOff();
    }

    public class GuideStep
    {
        public DateTime Time;
        public double RaDistance, DecDistance; // pixels or arcsec
        public double RaArcsec, DecArcsec;
        public int RaPulse, DecPulse;
        public double Snr;
        public double StarMass;
    }

    public interface IGuider : IDevice
    {
        bool IsGuiding { get; }
        string State { get; }
        double RmsTotalArcsec { get; }
        double RmsRaArcsec { get; }
        double RmsDecArcsec { get; }
        double PixelScale { get; }
        event Action<GuideStep> Step;
        Task StartGuiding(bool recalibrate, CancellationToken ct);
        void StopGuiding();
        /// <summary>Dither by up to 'pixels' and wait until settled.</summary>
        Task Dither(double pixels, bool raOnly, double settlePixels, double settleTimeSec, double timeoutSec, CancellationToken ct);
        void Pause(bool paused);
    }

    /// <summary>Convenience base implementing state notification.</summary>
    public abstract class DeviceBase : IDevice
    {
        public DeviceInfo Info { get; protected set; }
        private bool _connected;
        public bool Connected
        {
            get { return _connected; }
            protected set { if (_connected != value) { _connected = value; RaiseStateChanged(); } }
        }
        public virtual string Description => Info?.Name ?? GetType().Name;
        public event EventHandler StateChanged;
        protected void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);
        public abstract void Connect();
        public abstract void Disconnect();
        public virtual void Dispose() { try { if (Connected) Disconnect(); } catch { } }
    }
}
