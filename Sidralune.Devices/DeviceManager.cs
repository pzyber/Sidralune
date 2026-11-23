using System;
using System.Collections.Generic;
using System.Linq;
using Sidralune.Core;
using Sidralune.Core.Util;
using Sidralune.Devices.Alpaca;
using Sidralune.Devices.Ascom;
using Sidralune.Devices.Guiding;
using Sidralune.Devices.Indi;
using Sidralune.Devices.Serial;
using Sidralune.Devices.Simulators;

namespace Sidralune.Devices
{
    /// <summary>Holds the observatory's connected devices and provides discovery across protocols.</summary>
    public class DeviceManager
    {
        public static DeviceManager Instance { get; } = new DeviceManager();

        public ITelescope Telescope { get; private set; }
        public ICamera Camera { get; private set; }
        public ICamera GuideCamera { get; private set; }
        public IFocuser Focuser { get; private set; }
        public IFilterWheel FilterWheel { get; private set; }
        public IDome Dome { get; private set; }
        public IRotator Rotator { get; private set; }
        public ISwitchDevice Switch { get; private set; }
        public ISafetyMonitor Safety { get; private set; }
        public IObservingConditions Weather { get; private set; }
        public ICoverCalibrator Cover { get; private set; }
        public IGuider Guider { get; private set; }
        public Observer Site = new Observer();
        public double FocalLengthMm = 530;
        public double ApertureMm = 106;
        /// <summary>
        /// Give a simulated mount created from now on periodic error, drift and Dec backlash, so that guiding has something to
        /// correct. Off by default so tests get a perfect mount; the app turns it on.
        /// </summary>
        public bool SimulatedMountErrors;
        /// <summary>Where the Sidralune guider keeps its settings and writes guide logs (null: the usual folders); tests point them elsewhere.</summary>
        public string GuiderSettingsPath, GuideLogFolder;

        public event EventHandler DevicesChanged;

        public IEnumerable<IDevice> All => new IDevice[] { Telescope, Camera, GuideCamera, Focuser, FilterWheel, Dome, Rotator, Switch, Safety, Weather, Cover, Guider }.Where(d => d != null);

        /// <summary>The name of the built-in guider, as listed with the guiders (in the interface language).</summary>
        public static string BuiltinGuiderName => Lang.T("Sidralune guider");

        /// <summary>Discover available devices of a kind across protocols (Alpaca discovery may take ~1.5 s).</summary>
        public List<DeviceInfo> Discover(DeviceKind kind, bool includeAlpaca = true)
        {
            var list = new List<DeviceInfo>();
            if (kind == DeviceKind.Guider) list.Add(new DeviceInfo { Kind = kind, Protocol = DeviceProtocol.Builtin, Name = BuiltinGuiderName, Address = "builtin" });
            else list.Add(new DeviceInfo { Kind = kind, Protocol = DeviceProtocol.Simulator, Name = SimulatorName(kind), Address = "sim" });
            try { list.AddRange(AscomDiscovery.Drivers(kind)); } catch { }
            if (includeAlpaca)
            {
                // a guide camera is one of the server's cameras
                var type = kind == DeviceKind.GuideCamera ? DeviceKind.Camera : kind;
                try
                {
                    foreach (var s in AlpacaDiscovery.FindServers())
                        try { list.AddRange(AlpacaDiscovery.Devices(s).Where(d => d.Kind == type).Select(d => { d.Kind = kind; return d; })); } catch { }
                }
                catch { }
            }
            if (kind == DeviceKind.Telescope)
                foreach (var p in Lx200Telescope.SerialPorts())
                    list.Add(new DeviceInfo { Kind = kind, Protocol = DeviceProtocol.Lx200, Name = "LX200 / OnStep on " + p, Address = p + ":9600" });
            if (kind == DeviceKind.Guider) list.Add(new DeviceInfo { Kind = kind, Protocol = DeviceProtocol.Phd2, Name = "PHD2 (localhost)", Address = "localhost:4400" });
            return list;
        }

        private static string SimulatorName(DeviceKind kind) => kind == DeviceKind.GuideCamera ? "Guide Camera Simulator" : kind + " Simulator";

        public IDevice Create(DeviceInfo info)
        {
            switch (info.Protocol)
            {
                case DeviceProtocol.Ascom: return AscomDiscovery.Create(info);
                case DeviceProtocol.Alpaca: return AlpacaDiscovery.Create(info);
                case DeviceProtocol.Indi: return IndiClient.Create(info);
                case DeviceProtocol.Lx200: return new Lx200Telescope(info);
                case DeviceProtocol.Phd2:
                    {
                        var p = new Phd2Client();
                        var hp = info.Address.Split(':');
                        p.Host = hp[0];
                        if (hp.Length > 1) p.Instance = int.Parse(hp[1]) - 4399;
                        return p;
                    }
                case DeviceProtocol.Builtin:
                    if (info.Kind != DeviceKind.Guider) throw new NotSupportedException("No built-in " + info.Kind);
                    return new SidraluneGuider(this, GuiderSettingsPath) { LogFolder = GuideLogFolder };
                default: return CreateSimulator(info.Kind);
            }
        }

        private IDevice CreateSimulator(DeviceKind kind)
        {
            switch (kind)
            {
                case DeviceKind.Telescope:
                    var mount = new TelescopeSimulator { Site = Site };
                    if (SimulatedMountErrors) { mount.PeriodicErrorArcsec = 6; mount.WormPeriodSec = 480; mount.DriftRaArcsecPerMin = 0.3; mount.DriftDecArcsecPerMin = 0.6; mount.DecBacklashMs = 800; }
                    return mount;
                case DeviceKind.Camera:
                    return new CameraSimulator { Mount = Telescope as TelescopeSimulator, Focuser = Focuser as FocuserSimulator, Wheel = FilterWheel as FilterWheelSimulator, FocalLengthMm = FocalLengthMm };
                case DeviceKind.GuideCamera:
                    return CameraSimulator.GuideCamera(Telescope as TelescopeSimulator);
                case DeviceKind.Focuser: return new FocuserSimulator();
                case DeviceKind.FilterWheel: return new FilterWheelSimulator();
                case DeviceKind.Dome: return new DomeSimulator();
                case DeviceKind.SafetyMonitor:
                case DeviceKind.ObservingConditions: return new WeatherSimulator();
            }
            throw new NotSupportedException("No simulator for " + kind);
        }

        /// <summary>Connect a device and register it in its slot (disconnecting any previous one).</summary>
        public IDevice Connect(DeviceInfo info)
        {
            if (info.Kind == DeviceKind.Camera || info.Kind == DeviceKind.GuideCamera)
            {
                // one driver cannot run the imaging and the guide exposures at once
                var other = info.Kind == DeviceKind.Camera ? GuideCamera : Camera;
                if (other != null && other.Connected && info.Protocol != DeviceProtocol.Simulator && other.Info.Protocol == info.Protocol && other.Info.Address == info.Address)
                    throw new InvalidOperationException(info.Kind == DeviceKind.Camera ? Lang.T("This camera is already connected as the guide camera.") : Lang.T("This camera is already connected as the imaging camera."));
            }
            var dev = Create(info);
            dev.Connect();
            Assign(info.Kind, dev);
            // link simulators together for a coherent virtual observatory
            var cs = Camera as CameraSimulator;
            if (cs != null) { cs.Mount = Telescope as TelescopeSimulator ?? cs.Mount; cs.Focuser = Focuser as FocuserSimulator ?? cs.Focuser; cs.Wheel = FilterWheel as FilterWheelSimulator ?? cs.Wheel; }
            var gs = GuideCamera as CameraSimulator;
            if (gs != null) gs.Mount = Telescope as TelescopeSimulator ?? gs.Mount;
            Log.Info("Devices", "Connected " + info);
            DevicesChanged?.Invoke(this, EventArgs.Empty);
            return dev;
        }

        public void Disconnect(DeviceKind kind)
        {
            var d = Get(kind);
            if (d == null) return;
            try { d.Disconnect(); } catch (Exception ex) { Log.Warn("Devices", ex.Message); }
            Assign(kind, null);
            DevicesChanged?.Invoke(this, EventArgs.Empty);
        }

        public IDevice Get(DeviceKind kind)
        {
            switch (kind)
            {
                case DeviceKind.Telescope: return Telescope;
                case DeviceKind.Camera: return Camera;
                case DeviceKind.GuideCamera: return GuideCamera;
                case DeviceKind.Focuser: return Focuser;
                case DeviceKind.FilterWheel: return FilterWheel;
                case DeviceKind.Dome: return Dome;
                case DeviceKind.Rotator: return Rotator;
                case DeviceKind.Switch: return Switch;
                case DeviceKind.SafetyMonitor: return Safety;
                case DeviceKind.ObservingConditions: return Weather;
                case DeviceKind.CoverCalibrator: return Cover;
                case DeviceKind.Guider: return Guider;
            }
            return null;
        }

        private void Assign(DeviceKind kind, IDevice d)
        {
            var old = Get(kind);
            if (old != null && old != d) try { old.Disconnect(); } catch { }
            switch (kind)
            {
                case DeviceKind.Telescope: Telescope = (ITelescope)d; break;
                case DeviceKind.Camera: Camera = (ICamera)d; break;
                case DeviceKind.GuideCamera: GuideCamera = (ICamera)d; break;
                case DeviceKind.Focuser: Focuser = (IFocuser)d; break;
                case DeviceKind.FilterWheel: FilterWheel = (IFilterWheel)d; break;
                case DeviceKind.Dome: Dome = (IDome)d; break;
                case DeviceKind.Rotator: Rotator = (IRotator)d; break;
                case DeviceKind.Switch: Switch = (ISwitchDevice)d; break;
                case DeviceKind.SafetyMonitor: Safety = d as ISafetyMonitor; break;
                case DeviceKind.ObservingConditions: Weather = d as IObservingConditions; if (d is ISafetyMonitor && Safety == null) Safety = (ISafetyMonitor)d; break;
                case DeviceKind.CoverCalibrator: Cover = (ICoverCalibrator)d; break;
                case DeviceKind.Guider: Guider = (IGuider)d; break;
            }
        }

        /// <summary>Connect the full simulated observatory (mount, camera, guide camera, focuser, wheel, dome, weather) and the Sidralune guider.</summary>
        public void ConnectSimulators()
        {
            foreach (var k in new[] { DeviceKind.Telescope, DeviceKind.Focuser, DeviceKind.FilterWheel, DeviceKind.Camera, DeviceKind.GuideCamera, DeviceKind.Dome, DeviceKind.ObservingConditions })
                Connect(new DeviceInfo { Kind = k, Protocol = DeviceProtocol.Simulator, Name = SimulatorName(k), Address = "sim" });
            Connect(new DeviceInfo { Kind = DeviceKind.Guider, Protocol = DeviceProtocol.Builtin, Name = BuiltinGuiderName, Address = "builtin" });
        }

        public void DisconnectAll()
        {
            foreach (DeviceKind k in Enum.GetValues(typeof(DeviceKind))) Disconnect(k);
        }
    }
}
