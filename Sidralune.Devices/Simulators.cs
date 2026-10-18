using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Sidralune.Core;
using Sidralune.Core.Catalogs;
using Sidralune.Core.Coordinates;
using Sidralune.Core.Mathematics;
using Sidralune.Core.Time;
using Sidralune.Imaging;

namespace Sidralune.Devices.Simulators
{
    /// <summary>German equatorial mount simulator with realistic slewing, tracking, parking and pier side.</summary>
    public class TelescopeSimulator : DeviceBase, ITelescope
    {
        public Observer Site = new Observer();
        private double _ra = 0, _dec = 90, _targetRa, _targetDec;
        private bool _slewing, _tracking = true, _atPark;
        private Timer _timer;
        private DateTime _last = DateTime.UtcNow;
        private readonly object _lock = new object();
        private double[] _axisRate = new double[2];
        public double SlewRateDegPerSec = 4.0;
        /// <summary>Pointing model error (deg) injected for plate-solving/centering tests.</summary>
        public double PointingErrorRa = 0.0, PointingErrorDec = 0.0;

        public TelescopeSimulator() { Info = new DeviceInfo { Kind = DeviceKind.Telescope, Protocol = DeviceProtocol.Simulator, Name = "Telescope Simulator" }; }

        public override void Connect()
        {
            _ra = Lst(); _dec = 90;
            _timer = new Timer(Tick, null, 0, 100);
            Connected = true;
        }

        public override void Disconnect() { _timer?.Dispose(); _timer = null; Connected = false; }

        private double Lst() => AstroTime.Now.LocalSiderealHours(Site.LongitudeDeg);

        private void Tick(object state)
        {
            lock (_lock)
            {
                var now = DateTime.UtcNow;
                double dt = (now - _last).TotalSeconds;
                _last = now;
                if (!_tracking || _atPark) _ra += dt * 1.00273791 / 3600.0; // RA drifts with sidereal time when not tracking
                if (_slewing)
                {
                    double dra = AstroMath.NormalizeDeg180((_targetRa - _ra) * 15), ddec = _targetDec - _dec;
                    double step = SlewRateDegPerSec * dt;
                    double dist = Math.Sqrt(dra * dra + ddec * ddec);
                    if (dist <= step) { _ra = _targetRa; _dec = _targetDec; _slewing = false; RaiseStateChanged(); }
                    else { _ra += dra / dist * step / 15; _dec += ddec / dist * step; }
                }
                _ra += _axisRate[0] * dt / 15; _dec = AstroMath.Clamp(_dec + _axisRate[1] * dt, -90, 90);
                _ra = AstroMath.NormalizeHours(_ra);
            }
        }

        public double RightAscension { get { lock (_lock) return AstroMath.NormalizeHours(_ra + PointingErrorRa / 15); } }
        public double Declination { get { lock (_lock) return AstroMath.Clamp(_dec + PointingErrorDec, -90, 90); } }
        /// <summary>True optical axis pointing (without the injected model error), used by the camera simulator.</summary>
        public double TrueRa { get { lock (_lock) return _ra; } }
        public double TrueDec { get { lock (_lock) return _dec; } }

        public double Altitude => AltAz().AltDeg;
        public double Azimuth => AltAz().AzDeg;
        private Horizontal AltAz()
        {
            double ha = (Lst() - RightAscension) * AstroMath.Hour2Rad;
            return Transforms.HaDecToAltAz(ha, Declination * AstroMath.Deg2Rad, Site.LatRad);
        }
        public double SiderealTime => Lst();
        public bool Slewing { get { lock (_lock) return _slewing || _axisRate[0] != 0 || _axisRate[1] != 0; } }
        public bool Tracking { get { return _tracking; } set { _tracking = value; RaiseStateChanged(); } }
        public TrackingRate TrackingRate { get; set; } = TrackingRate.Sidereal;
        public bool AtPark => _atPark;
        public PierSide SideOfPier => DestinationSideOfPier(RightAscension, Declination);
        public bool CanPark => true;
        public bool CanSync => true;
        public bool CanPulseGuide => true;
        public bool CanSlewAltAz => true;
        public double SiteLatitude => Site.LatitudeDeg;
        public double SiteLongitude => Site.LongitudeDeg;
        public double[] AxisRates => new[] { 0.0083, 0.033, 0.25, 1.0, 4.0 };

        public void SlewToCoordinatesAsync(double raHours, double decDeg)
        {
            if (_atPark) throw new InvalidOperationException("Mount is parked");
            lock (_lock)
            {
                // remove the injected pointing error so the scope ends at the requested apparent coordinates of its own model
                _targetRa = AstroMath.NormalizeHours(raHours - PointingErrorRa / 15);
                _targetDec = AstroMath.Clamp(decDeg - PointingErrorDec, -90, 90);
                _slewing = true;
            }
            RaiseStateChanged();
        }

        public void SlewToAltAzAsync(double azDeg, double altDeg)
        {
            double ha, dec;
            Transforms.AltAzToHaDec(azDeg * AstroMath.Deg2Rad, altDeg * AstroMath.Deg2Rad, Site.LatRad, out ha, out dec);
            SlewToCoordinatesAsync(AstroMath.NormalizeHours(Lst() - ha * AstroMath.Rad2Hour), dec * AstroMath.Rad2Deg);
        }

        public void SyncToCoordinates(double raHours, double decDeg)
        {
            lock (_lock)
            {
                // a sync corrects the model: the error becomes zero for the synced position
                PointingErrorRa = AstroMath.NormalizeDeg180((raHours - _ra) * 15);
                PointingErrorDec = decDeg - _dec;
            }
            RaiseStateChanged();
        }

        public void AbortSlew() { lock (_lock) { _slewing = false; _axisRate[0] = _axisRate[1] = 0; } RaiseStateChanged(); }
        public void Park() { SlewToCoordinatesAsync(Lst(), 90); while (Slewing) Thread.Sleep(100); _atPark = true; _tracking = false; RaiseStateChanged(); }
        public void Unpark() { _atPark = false; _tracking = true; RaiseStateChanged(); }
        public void SetPark() { }

        public void PulseGuide(GuideDirection dir, int ms)
        {
            double rate = 0.5 * 15.041 / 3600.0; // 0.5x sidereal in deg/s
            double d = rate * ms / 1000.0;
            lock (_lock)
            {
                if (dir == GuideDirection.North) _dec += d;
                if (dir == GuideDirection.South) _dec -= d;
                if (dir == GuideDirection.East) _ra += d / 15;
                if (dir == GuideDirection.West) _ra -= d / 15;
            }
        }

        public void MoveAxis(int axis, double rate) { lock (_lock) _axisRate[AstroMath.Clamp(axis, 0, 1)] = rate; RaiseStateChanged(); }

        public PierSide DestinationSideOfPier(double raHours, double decDeg)
        {
            double ha = AstroMath.NormalizeHours(Lst() - raHours);
            return ha < 12 ? PierSide.West : PierSide.East; // west of meridian -> pier side west (counterweight east)
        }
    }

    /// <summary>Focuser simulator; star size follows a hyperbola around a hidden best-focus position.</summary>
    public class FocuserSimulator : DeviceBase, IFocuser
    {
        private int _pos = 25000, _target = 25000;
        public int BestFocus = 23700;
        public double CriticalSteps = 120; // steps for FWHM to double at ~ this distance scale
        public FocuserSimulator() { Info = new DeviceInfo { Kind = DeviceKind.Focuser, Protocol = DeviceProtocol.Simulator, Name = "Focuser Simulator" }; }
        public override void Connect() { Connected = true; }
        public override void Disconnect() { Connected = false; }
        public int Position => _pos;
        public int MaxStep => 60000;
        public bool IsMoving => _pos != _target;
        public bool Absolute => true;
        public double Temperature => 8.5 + 1.5 * Math.Sin(DateTime.Now.TimeOfDay.TotalHours / 3);
        public double StepSizeMicrons => 4.0;
        public void Move(int position)
        {
            _target = Math.Max(0, Math.Min(MaxStep, position));
            Task.Run(() => { while (_pos != _target) { int d = Math.Sign(_target - _pos) * Math.Min(Math.Abs(_target - _pos), 400); _pos += d; Thread.Sleep(20); } RaiseStateChanged(); });
        }
        public void Halt() { _target = _pos; }
        /// <summary>Defocus FWHM multiplier (hyperbolic V-curve).</summary>
        public double FwhmFactor => Math.Sqrt(1 + Math.Pow((_pos - BestFocus) / CriticalSteps, 2));
    }

    public class FilterWheelSimulator : DeviceBase, IFilterWheel
    {
        private int _pos;
        private bool _moving;
        public FilterWheelSimulator() { Info = new DeviceInfo { Kind = DeviceKind.FilterWheel, Protocol = DeviceProtocol.Simulator, Name = "Filter Wheel Simulator" }; }
        public override void Connect() { Connected = true; }
        public override void Disconnect() { Connected = false; }
        public int Position
        {
            get { return _moving ? -1 : _pos; }
            set { _moving = true; RaiseStateChanged(); Task.Run(() => { Thread.Sleep(600); _pos = value; _moving = false; RaiseStateChanged(); }); }
        }
        public string[] Names { get; } = { "L", "R", "G", "B", "Ha", "OIII", "SII" };
        public int[] FocusOffsets { get; } = { 0, 12, 8, 20, 35, 30, 38 };
        public bool IsMoving => _moving;
    }

    public class DomeSimulator : DeviceBase, IDome
    {
        private double _az, _target;
        private ShutterState _shutter = ShutterState.Closed;
        public DomeSimulator() { Info = new DeviceInfo { Kind = DeviceKind.Dome, Protocol = DeviceProtocol.Simulator, Name = "Dome Simulator" }; }
        public override void Connect() { Connected = true; }
        public override void Disconnect() { Connected = false; }
        public double Azimuth => _az;
        public ShutterState Shutter => _shutter;
        public bool Slewing => Math.Abs(AstroMath.NormalizeDeg180(_target - _az)) > 0.5;
        public bool AtPark => Math.Abs(_az) < 0.5 && !Slewing;
        public bool CanSetAzimuth => true;
        public bool Slaved { get; set; }
        public void SlewToAzimuth(double az)
        {
            _target = AstroMath.NormalizeDeg(az);
            Task.Run(() => { while (Slewing) { _az = AstroMath.NormalizeDeg(_az + Math.Sign(AstroMath.NormalizeDeg180(_target - _az)) * 2); Thread.Sleep(50); } RaiseStateChanged(); });
        }
        public void OpenShutter() { _shutter = ShutterState.Opening; RaiseStateChanged(); Task.Run(() => { Thread.Sleep(3000); _shutter = ShutterState.Open; RaiseStateChanged(); }); }
        public void CloseShutter() { _shutter = ShutterState.Closing; RaiseStateChanged(); Task.Run(() => { Thread.Sleep(3000); _shutter = ShutterState.Closed; RaiseStateChanged(); }); }
        public void Park() => SlewToAzimuth(0);
        public void FindHome() => SlewToAzimuth(0);
        public void AbortSlew() { _target = _az; }
    }

    public class WeatherSimulator : DeviceBase, IObservingConditions, ISafetyMonitor
    {
        public bool ForceUnsafe;
        public WeatherSimulator() { Info = new DeviceInfo { Kind = DeviceKind.ObservingConditions, Protocol = DeviceProtocol.Simulator, Name = "Weather Simulator" }; }
        public override void Connect() { Connected = true; }
        public override void Disconnect() { Connected = false; }
        private double Phase => DateTime.Now.TimeOfDay.TotalMinutes / 60.0;
        public double Temperature => 9 - 2 * Math.Sin(Phase / 4);
        public double Humidity => 65 + 10 * Math.Sin(Phase / 3);
        public double DewPoint => Temperature - (100 - Humidity) / 5;
        public double Pressure => 1013;
        public double CloudCover => ForceUnsafe ? 90 : 5;
        public double WindSpeed => 2.5 + Math.Sin(Phase * 2);
        public double RainRate => ForceUnsafe ? 1.2 : 0;
        public double SkyQuality => 21.2;
        public double SkyTemperature => -28;
        public bool IsSafe => !ForceUnsafe && CloudCover < 50 && RainRate == 0;
    }

    /// <summary>
    /// Camera simulator rendering the sky at the simulated mount's true pointing from the loaded star catalog, with
    /// focus-dependent PSF, sky background, dark current, read noise, hot pixels and optional Bayer matrix.
    /// </summary>
    public class CameraSimulator : DeviceBase, ICamera
    {
        public TelescopeSimulator Mount;
        public FocuserSimulator Focuser;
        public FilterWheelSimulator Wheel;
        public double FocalLengthMm = 530;
        public double SeeingArcsec = 2.2;
        public double ZeroPointMag = 21.5;     // mag giving 1 e-/s
        public double SkyMagArcsec2 = 20.8;
        private int _w = 2048, _h = 1536;
        private CameraState _state = CameraState.Idle;
        private AstroImage _last;
        private CancellationTokenSource _cts;
        private DateTime _start;
        private double _duration;
        private double _ccdTemp = 15, _setTemp = -10;
        private bool _cooler;
        private readonly Random _rng = new Random();
        public bool ColorSensor = false;

        public CameraSimulator() { Info = new DeviceInfo { Kind = DeviceKind.Camera, Protocol = DeviceProtocol.Simulator, Name = "Camera Simulator" }; NumX = _w; NumY = _h; }
        public override void Connect() { Connected = true; Task.Run(() => CoolLoop()); }
        public override void Disconnect() { Connected = false; }

        private void CoolLoop()
        {
            while (Connected)
            {
                double target = _cooler ? _setTemp : 15;
                _ccdTemp += (target - _ccdTemp) * 0.05;
                Thread.Sleep(1000);
            }
        }

        public int SensorWidth => _w;
        public int SensorHeight => _h;
        public double PixelSizeX => 3.76;
        public double PixelSizeY => 3.76;
        public int BinX { get; set; } = 1;
        public int BinY { get; set; } = 1;
        public int MaxBin => 4;
        public int StartX { get; set; }
        public int StartY { get; set; }
        public int NumX { get; set; }
        public int NumY { get; set; }
        public CameraState State => _state;
        public bool ImageReady => _state == CameraState.Idle && _last != null;
        public bool CanSetTemperature => true;
        public bool CoolerOn { get { return _cooler; } set { _cooler = value; } }
        public double CcdTemperature => _ccdTemp;
        public double SetTemperature { get { return _setTemp; } set { _setTemp = value; } }
        public double CoolerPower => _cooler ? AstroMath.Clamp((15 - _ccdTemp) * 3, 0, 100) : 0;
        public bool HasGain => true;
        public int Gain { get; set; } = 100;
        public int GainMin => 0;
        public int GainMax => 300;
        public bool HasOffset => true;
        public int Offset { get; set; } = 30;
        public int BitDepth => 16;
        public BayerPattern Bayer => ColorSensor ? BayerPattern.RGGB : BayerPattern.None;
        public double ElectronsPerAdu => 1.0 / Math.Pow(10, Gain / 200.0);
        public double PercentCompleted => _state == CameraState.Exposing ? Math.Min(100, (DateTime.UtcNow - _start).TotalSeconds / Math.Max(0.001, _duration) * 100) : 0;

        public void StartExposure(double seconds, bool light)
        {
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            _duration = seconds;
            _start = DateTime.UtcNow;
            _state = CameraState.Exposing;
            _last = null;
            RaiseStateChanged();
            double ra = Mount != null ? Mount.TrueRa : 0, dec = Mount != null ? Mount.TrueDec : 0;
            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(seconds, 30)), ct);
                    _state = CameraState.Download;
                    _last = Render(seconds, light, ra, dec);
                    _state = CameraState.Idle;
                }
                catch (OperationCanceledException) { _state = CameraState.Idle; }
                catch (Exception ex) { _state = CameraState.Error; Core.Util.Log.Error("CameraSim", ex); }
                RaiseStateChanged();
            });
        }

        public void AbortExposure() { _cts?.Cancel(); }

        public AstroImage GetImage() => _last;

        private AstroImage Render(double exp, bool light, double raH, double decD)
        {
            int w = Math.Max(16, NumX / BinX), h = Math.Max(16, NumY / BinY);
            double scale = 206.265 * PixelSizeX * BinX / FocalLengthMm; // arcsec/px
            double fwhmPx = SeeingArcsec / scale * (Focuser != null ? Focuser.FwhmFactor : 1);
            var img = new AstroImage(w, h, 1);
            var d = img.Data[0];
            double gainE = ElectronsPerAdu;
            double bias = Offset * 10.0;
            double dark = 0.01 * Math.Pow(2, (_ccdTemp + 10) / 6.0); // e-/s/px, doubling every 6 °C
            double skyE = light ? Math.Pow(10, -0.4 * (SkyMagArcsec2 - ZeroPointMag)) * scale * scale * exp : 0;
            if (light)
            {
                // the mount reports JNow (apparent) coordinates; catalog stars are ICRS
                var site = Mount != null ? Mount.Site : new Observer();
                var icrs = new SkyContext(site, AstroTime.FromDateTime(_start)).ApparentToIcrs(raH * AstroMath.Hour2Rad, decD * AstroMath.Deg2Rad);
                // tangent-plane rendering of catalog stars
                double ra0 = icrs.Ra, de0 = icrs.Dec;
                var stars = new List<Star>();
                double radius = Math.Sqrt(w * w + h * h) * scale / 3600.0 * 0.6 * AstroMath.Deg2Rad;
                CatalogManager.Instance.Stars.Cone(ra0, de0, radius, 16, stars);
                double sigma = Math.Max(0.6, fwhmPx / 2.3548);
                int r = (int)Math.Ceiling(sigma * 4);
                foreach (var s in stars)
                {
                    double xi, eta;
                    double cd = Math.Cos(s.Dec), sd = Math.Sin(s.Dec), cd0 = Math.Cos(de0), sd0 = Math.Sin(de0), da = s.Ra - ra0;
                    double den = sd * sd0 + cd * cd0 * Math.Cos(da);
                    if (den <= 0) continue;
                    xi = cd * Math.Sin(da) / den; eta = (sd * cd0 - cd * sd0 * Math.Cos(da)) / den;
                    double x = w / 2.0 - xi * AstroMath.Rad2ArcSec / scale, y = h / 2.0 - eta * AstroMath.Rad2ArcSec / scale;
                    if (x < -r || y < -r || x > w + r || y > h + r) continue;
                    double flux = Math.Pow(10, -0.4 * (s.Mag - ZeroPointMag)) * exp; // electrons
                    double amp = flux / (2 * Math.PI * sigma * sigma);
                    Splat(d, w, h, x, y, amp, sigma, r);
                }
                if (SyntheticFieldStars)
                {
                    // deterministic pseudo-stars tied to sky cells (simulation aid when only a sparse catalog is loaded)
                    double cell = 0.25 * AstroMath.Deg2Rad;
                    int ci0 = (int)Math.Floor((ra0 * Math.Cos(de0) - radius) / cell), ci1 = (int)Math.Floor((ra0 * Math.Cos(de0) + radius) / cell);
                    int cj0 = (int)Math.Floor((de0 - radius) / cell), cj1 = (int)Math.Floor((de0 + radius) / cell);
                    for (int cj = cj0; cj <= cj1; cj++)
                        for (int ci = ci0; ci <= ci1; ci++)
                        {
                            var rng = new Random(unchecked(ci * 73856093 ^ cj * 19349663));
                            for (int k = 0; k < 60; k++)
                            {
                                double sdec = (cj + rng.NextDouble()) * cell;
                                double sra = (ci + rng.NextDouble()) * cell / Math.Max(0.05, Math.Cos(de0));
                                double mag = 9 + 6.5 * Math.Pow(rng.NextDouble(), 0.35);
                                double cd = Math.Cos(sdec), sd = Math.Sin(sdec), cd0 = Math.Cos(de0), sd0 = Math.Sin(de0), da = sra - ra0;
                                double den = sd * sd0 + cd * cd0 * Math.Cos(da);
                                if (den <= 0) continue;
                                double xi = cd * Math.Sin(da) / den, eta = (sd * cd0 - cd * sd0 * Math.Cos(da)) / den;
                                double x = w / 2.0 - xi * AstroMath.Rad2ArcSec / scale, y = h / 2.0 - eta * AstroMath.Rad2ArcSec / scale;
                                if (x < -r || y < -r || x > w + r || y > h + r) continue;
                                double flux = Math.Pow(10, -0.4 * (mag - ZeroPointMag)) * exp;
                                Splat(d, w, h, x, y, flux / (2 * Math.PI * sigma * sigma), sigma, r);
                            }
                        }
                }
            }
            double rn = 1.8;
            var hot = new Random(1234);
            for (int i = 0; i < d.Length; i++)
            {
                double e = d[i] + skyE + dark * exp;
                double noisy = e + Math.Sqrt(Math.Max(e, 0)) * Gauss() + rn * Gauss();
                double adu = bias + noisy / gainE;
                d[i] = (float)AstroMath.Clamp(adu / 65535.0, 0, 1);
            }
            for (int i = 0; i < 40; i++) { int p = hot.Next(d.Length); d[p] = (float)Math.Min(1, d[p] + 0.3 * exp / 60); }
            img.ExposureSeconds = exp;
            img.DateObs = _start;
            img.Header.Set("IMAGETYP", light ? "Light Frame" : "Dark Frame");
            img.Header.Set("XPIXSZ", PixelSizeX * BinX); img.Header.Set("YPIXSZ", PixelSizeY * BinY);
            img.Header.Set("XBINNING", BinX); img.Header.Set("YBINNING", BinY);
            img.Header.Set("FOCALLEN", FocalLengthMm);
            img.Header.Set("CCD-TEMP", _ccdTemp);
            img.Header.Set("GAIN", Gain); img.Header.Set("OFFSET", Offset);
            img.Header.Set("RA", raH * 15); img.Header.Set("DEC", decD);
            img.Header.Set("INSTRUME", "Sidralune Camera Simulator");
            if (Focuser != null) img.Header.Set("FOCPOS", Focuser.Position);
            if (Wheel != null && Wheel.Position >= 0) img.Header.Set("FILTER", Wheel.Names[Wheel.Position]);
            return img;
        }

        /// <summary>Add synthetic faint field stars (deterministic per sky position). Simulation only.</summary>
        public bool SyntheticFieldStars = true;

        private static void Splat(float[] d, int w, int h, double x, double y, double amp, double sigma, int r)
        {
            for (int yy = (int)y - r; yy <= (int)y + r; yy++)
            {
                if (yy < 0 || yy >= h) continue;
                for (int xx = (int)x - r; xx <= (int)x + r; xx++)
                {
                    if (xx < 0 || xx >= w) continue;
                    double dx = xx - x, dy = yy - y;
                    d[yy * w + xx] += (float)(amp * Math.Exp(-0.5 * (dx * dx + dy * dy) / (sigma * sigma)));
                }
            }
        }

        private double Gauss()
        {
            double u1 = 1.0 - _rng.NextDouble(), u2 = _rng.NextDouble();
            return Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
        }
    }
}
