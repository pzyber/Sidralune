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
        /// <summary>An adaptive optics unit in front of the guide camera, or null.</summary>
        public IAdaptiveOptics Ao { get; private set; }
        public ISwitchDevice Switch { get; private set; }
        public ISafetyMonitor Safety { get; private set; }
        public IObservingConditions Weather { get; private set; }
        public ICoverCalibrator Cover { get; private set; }
        /// <summary>
        /// The guider in use: PHD2 when it is connected, otherwise the Sidralune guider once there is a guide camera for it,
        /// otherwise none.
        /// </summary>
        public IGuider Guider => _phd2 ?? (GuideCamera != null ? BuiltinGuider : null);
        private IGuider _phd2;
        private SidraluneGuider _builtin;
        private readonly object _builtinLock = new object();

        /// <summary>
        /// Sidralune's own guider. It is part of Sidralune, so it is never connected or disconnected: it guides with the guide
        /// camera whenever PHD2 is not connected. Its settings can be changed at any time.
        /// </summary>
        public SidraluneGuider BuiltinGuider
        {
            get
            {
                lock (_builtinLock)
                {
                    if (_builtin == null) { _builtin = new SidraluneGuider(this, GuiderSettingsPath) { LogFolder = GuideLogFolder }; _builtin.Connect(); }
                    return _builtin;
                }
            }
        }

        /// <summary>PHD2 is connected and guides instead of the Sidralune guider.</summary>
        public bool Phd2Connected => _phd2 != null;
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

        public IEnumerable<IDevice> All => new IDevice[] { Telescope, Camera, GuideCamera, Focuser, FilterWheel, Dome, Rotator, Switch, Safety, Weather, Cover, Ao, Guider }.Where(d => d != null);

        /// <summary>The name of the built-in guider (in the interface language).</summary>
        public static string BuiltinGuiderName => Lang.T("Sidralune guider");

        private GuiderServer _server;

        /// <summary>The PHD2-compatible server for the Sidralune guider (not listening until started).</summary>
        public GuiderServer GuiderServer => _server ?? (_server = new GuiderServer(this));

        /// <summary>
        /// Plate solving a camera's frames: against the stars a simulated camera draws; otherwise against Gaia DR3 online
        /// (cached, so a field solves offline the second time), falling back to the loaded catalogue without a connection.
        /// Each online miss is a query, so the search round the hint is kept to 2°.
        /// </summary>
        public void PrepareSolve(Science.Astrometry.SolveOptions opt, ICamera cam)
        {
            if (opt.Catalog == null)
            {
                var sim = cam as CameraSimulator;
                opt.Catalog = sim != null ? (Science.Astrometry.IStarCatalogProvider)new SimulatedSkyProvider { Synthetic = sim.SyntheticFieldStars }
                    : new Science.Astrometry.FallbackCatalogProvider(new Science.Astrometry.VizierProvider(), new Science.Astrometry.LocalCatalogProvider());
            }
            if (opt.Catalog.IsOnline) opt.SearchRadius = Math.Min(opt.SearchRadius, 2);
        }

        /// <summary>Starts the PHD2-compatible server when the guider's settings have it on.</summary>
        public void StartGuiderServerIfWanted()
        {
            var st = BuiltinGuider.Settings;
            if (st.ServerEnabled) GuiderServer.Start(st.ServerPort, st.ServerAllowOthers);
        }

        /// <summary>Discover available devices of a kind across protocols (Alpaca discovery may take ~1.5 s).</summary>
        public List<DeviceInfo> Discover(DeviceKind kind, bool includeAlpaca = true)
        {
            var list = new List<DeviceInfo>();
            // the Sidralune guider is not a device to choose; PHD2 is the guider there is to connect
            if (kind != DeviceKind.Guider) list.Add(new DeviceInfo { Kind = kind, Protocol = DeviceProtocol.Simulator, Name = SimulatorName(kind), Address = "sim" });
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
            if (kind == DeviceKind.AdaptiveOptics)
                foreach (var p in Lx200Telescope.SerialPorts())
                    list.Add(new DeviceInfo { Kind = kind, Protocol = DeviceProtocol.SxAo, Name = "SX AO on " + p, Address = p });
            return list;
        }

        private static string SimulatorName(DeviceKind kind) => kind == DeviceKind.GuideCamera ? "Guide Camera Simulator" : kind == DeviceKind.AdaptiveOptics ? "AO Simulator"
            : kind == DeviceKind.CoverCalibrator ? "Flat Panel Simulator" : kind + " Simulator";

        public IDevice Create(DeviceInfo info)
        {
            switch (info.Protocol)
            {
                case DeviceProtocol.Ascom: return AscomDiscovery.Create(info);
                case DeviceProtocol.Alpaca: return AlpacaDiscovery.Create(info);
                case DeviceProtocol.Indi: return IndiClient.Create(info);
                case DeviceProtocol.Lx200: return new Lx200Telescope(info);
                case DeviceProtocol.SxAo: return new SxAo(info);
                case DeviceProtocol.Phd2:
                    {
                        var p = new Phd2Client();
                        var hp = info.Address.Split(':');
                        p.Host = hp[0];
                        if (hp.Length > 1) p.Instance = int.Parse(hp[1]) - 4399;
                        return p;
                    }
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
                case DeviceKind.Rotator: return new RotatorSimulator();
                case DeviceKind.AdaptiveOptics: return new AoSimulator();
                case DeviceKind.Switch: return new SwitchSimulator();
                case DeviceKind.CoverCalibrator: return new CoverCalibratorSimulator();
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
            // the guide camera belongs to one guider at a time: PHD2 opens it itself, and most drivers serve one program
            if (info.Kind == DeviceKind.GuideCamera && _phd2 != null)
                throw new InvalidOperationException(Lang.T("PHD2 is connected and uses the guide camera itself. Disconnect PHD2 to guide with Sidralune."));
            if (info.Kind == DeviceKind.Guider && GuideCamera != null)
                throw new InvalidOperationException(Lang.T("Disconnect the guide camera to use PHD2."));
            var dev = Create(info);
            dev.Connect();
            Assign(info.Kind, dev);
            // link simulators together for a coherent virtual observatory
            var cs = Camera as CameraSimulator;
            if (cs != null)
            {
                cs.Mount = Telescope as TelescopeSimulator ?? cs.Mount; cs.Focuser = Focuser as FocuserSimulator ?? cs.Focuser; cs.Wheel = FilterWheel as FilterWheelSimulator ?? cs.Wheel;
                // the imaging camera sits on the rotator
                cs.Rotator = Rotator as RotatorSimulator; cs.OnRotator = true;
            }
            var gs = GuideCamera as CameraSimulator;
            if (gs != null) { gs.Mount = Telescope as TelescopeSimulator ?? gs.Mount; gs.Rotator = Rotator as RotatorSimulator; gs.Ao = Ao as AoSimulator; }
            Log.Info("Devices", "Connected " + info);
            DevicesChanged?.Invoke(this, EventArgs.Empty);
            return dev;
        }

        public void Disconnect(DeviceKind kind)
        {
            // the Sidralune guider stays; disconnecting the guider disconnects PHD2, or stops the Sidralune guider
            if (kind == DeviceKind.Guider && _phd2 == null) { _builtin?.StopGuiding(); return; }
            if (kind == DeviceKind.GuideCamera) _builtin?.StopGuiding();
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
                case DeviceKind.AdaptiveOptics: return Ao;
                case DeviceKind.Guider: return Guider;
            }
            return null;
        }

        private void Assign(DeviceKind kind, IDevice d)
        {
            var old = kind == DeviceKind.Guider ? _phd2 : Get(kind);
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
                case DeviceKind.AdaptiveOptics: Ao = (IAdaptiveOptics)d; break;
                case DeviceKind.Guider: _phd2 = (IGuider)d; break;
            }
        }

        /// <summary>
        /// Connect the full simulated observatory (mount, camera, guide camera, focuser, wheel, rotator, dome, weather, switch box,
        /// flat panel), guided by the
        /// Sidralune guider (PHD2 is disconnected first, as it would use the guide camera itself).
        /// </summary>
        public void ConnectSimulators()
        {
            if (_phd2 != null) Disconnect(DeviceKind.Guider);
            foreach (var k in new[] { DeviceKind.Telescope, DeviceKind.Focuser, DeviceKind.FilterWheel, DeviceKind.Rotator, DeviceKind.Camera, DeviceKind.GuideCamera, DeviceKind.Dome, DeviceKind.ObservingConditions,
                                      DeviceKind.Switch, DeviceKind.CoverCalibrator })
                Connect(new DeviceInfo { Kind = k, Protocol = DeviceProtocol.Simulator, Name = SimulatorName(k), Address = "sim" });
        }

        public void DisconnectAll()
        {
            foreach (DeviceKind k in Enum.GetValues(typeof(DeviceKind))) Disconnect(k);
        }
    }
}
