using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Sidralune.Core;
using Sidralune.Core.Catalogs;
using Sidralune.Core.Coordinates;
using Sidralune.Core.Mathematics;
using Sidralune.Core.Time;
using Sidralune.Imaging;
using Sidralune.Science.Astrometry;

namespace Sidralune.Devices.Simulators
{
    /// <summary>
    /// The simulated cameras' extra faint stars (a simulation aid when only a sparse catalogue is loaded): pseudo-stars
    /// tied to cells of the sky, 0.25° of Dec high and as near 0.25° of RA wide as divides the circle, each cell's stars
    /// always the same. They stay where they are wherever a camera points, and the plate solver finds them through
    /// SimulatedSkyProvider.
    /// </summary>
    public static class SyntheticSky
    {
        private const double Cell = 0.25 * AstroMath.Deg2Rad;
        private const int StarsPerCell = 60;

        /// <summary>The stars within radius of (ra0, de0), all in radians (ICRS): RA, Dec and magnitude of each.</summary>
        public static void Stars(double ra0, double de0, double radius, Action<double, double, double> add)
        {
            int half = (int)Math.Round(Math.PI / 2 / Cell); // the bands run from -half to half - 1
            int cj0 = Math.Max(-half, (int)Math.Floor((de0 - radius) / Cell)), cj1 = Math.Min(half - 1, (int)Math.Floor((de0 + radius) / Cell));
            double cosR = Math.Cos(radius), sd0 = Math.Sin(de0), cd0 = Math.Cos(de0);
            double reach = Math.Min(Math.PI, radius / Math.Max(0.05, Math.Cos(Math.Min(Math.Abs(de0) + radius, 1.5))));
            for (int cj = cj0; cj <= cj1; cj++)
            {
                int n = Math.Max(1, (int)Math.Floor(2 * Math.PI * Math.Cos((cj + 0.5) * Cell) / Cell));
                double raCell = 2 * Math.PI / n;
                int ci0 = (int)Math.Floor((ra0 - reach) / raCell), ci1 = (int)Math.Floor((ra0 + reach) / raCell);
                // the whole band when the reach goes round it
                if (ci1 - ci0 + 1 >= n) { ci0 = 0; ci1 = n - 1; }
                for (int ci = ci0; ci <= ci1; ci++)
                {
                    int cell = ((ci % n) + n) % n;
                    var rng = new Random(unchecked(cell * 73856093 ^ cj * 19349663));
                    for (int k = 0; k < StarsPerCell; k++)
                    {
                        double sdec = (cj + rng.NextDouble()) * Cell;
                        double sra = (cell + rng.NextDouble()) * raCell;
                        double mag = 9 + 6.5 * Math.Pow(rng.NextDouble(), 0.35);
                        double sd = Math.Sin(sdec), cd = Math.Cos(sdec);
                        if (sd * sd0 + cd * cd0 * Math.Cos(sra - ra0) < cosR) continue;
                        add(sra, sdec, mag);
                    }
                }
            }
        }
    }

    /// <summary>
    /// The stars a simulated camera draws, as a catalogue to plate-solve against: the loaded catalogue and, when the camera
    /// adds them, the synthetic faint stars.
    /// </summary>
    public class SimulatedSkyProvider : IStarCatalogProvider
    {
        public bool Synthetic = true;
        public string Name => "Simulated sky";
        public bool IsOnline => false;

        public List<RefStar> Cone(double raDeg, double decDeg, double radiusDeg, double magLimit, int maxStars)
        {
            var list = new LocalCatalogProvider().Cone(raDeg, decDeg, radiusDeg, magLimit, int.MaxValue);
            if (Synthetic)
                SyntheticSky.Stars(raDeg * AstroMath.Deg2Rad, decDeg * AstroMath.Deg2Rad, radiusDeg * AstroMath.Deg2Rad, (ra, dec, mag) =>
                {
                    if (mag <= magLimit) list.Add(new RefStar { Ra = AstroMath.NormalizeDeg(ra * AstroMath.Rad2Deg), Dec = dec * AstroMath.Rad2Deg, Mag = mag });
                });
            return list.OrderBy(s => s.Mag).Take(maxStars).ToList();
        }
    }

    /// <summary>
    /// German equatorial mount simulator with realistic slewing, tracking, parking and pier side. Guide pulses take their
    /// time, and optional tracking errors (periodic error, drift, Dec backlash) give a guider something to correct.
    /// </summary>
    public class TelescopeSimulator : DeviceBase, ITelescope
    {
        public Observer Site = new Observer();
        private double _ra = 0, _dec = 90, _targetRa, _targetDec;
        private bool _slewing, _tracking = true, _atPark;
        private Timer _timer;
        private DateTime _last = DateTime.UtcNow;
        private readonly object _lock = new object();
        private double[] _axisRate = new double[2];
        private PierSide _pier = PierSide.West;
        public double SlewRateDegPerSec = 4.0;
        /// <summary>Pointing model error (deg) injected for plate-solving/centering tests.</summary>
        public double PointingErrorRa = 0.0, PointingErrorDec = 0.0;

        public const double SiderealDegPerSec = 15.0410686 / 3600;
        /// <summary>Guide rates (deg/s), 0.5× sidereal unless set.</summary>
        public double GuideRateRa { get; set; } = 0.5 * SiderealDegPerSec;
        public double GuideRateDec { get; set; } = 0.5 * SiderealDegPerSec;

        // Tracking errors. They move the true pointing only: the mount's encoders do not see them.
        /// <summary>Periodic error: a sine of this amplitude (arcsec on the sky, in RA) over the worm period.</summary>
        public double PeriodicErrorArcsec, WormPeriodSec = 480;
        /// <summary>Drift from polar misalignment (arcsec per minute on the sky), starting again after each slew.</summary>
        public double DriftRaArcsecPerMin, DriftDecArcsecPerMin;
        /// <summary>Dec backlash: after the Dec motor reverses, this much of the pulses (ms) moves nothing.</summary>
        public double DecBacklashMs;
        /// <summary>
        /// Dec pulses move towards the sky's north on both sides of the pier, as on mounts that reverse Dec themselves after
        /// a meridian flip. Off (the default): North turns the Dec motor one way, which points south of the pier's east side.
        /// </summary>
        public bool DecPulsesFollowSky;
        /// <summary>
        /// Polar misalignment: the RA axis points this far above the celestial pole (arcmin) and this far east of it (arcmin
        /// on the sky). The mount turns about its own axis, so the true pointing drifts against the stars while tracking and
        /// the camera's angle on the sky changes with it.
        /// </summary>
        public double PolarErrorAltArcmin, PolarErrorAzArcmin;

        private double _driftRa, _driftDec; // arcsec accumulated since the last slew
        private readonly DateTime[] _pulseStart = new DateTime[2], _pulseEnd = new DateTime[2];
        private readonly double[] _pulseRate = new double[2]; // axis 0: RA hours/s, axis 1: Dec deg/s
        private int _decMotor; // last Dec motor direction, +1 north, -1 south
        private double _decSlackMs;

        public TelescopeSimulator() { Info = new DeviceInfo { Kind = DeviceKind.Telescope, Protocol = DeviceProtocol.Simulator, Name = "Telescope Simulator" }; }

        public override void Connect()
        {
            lock (_lock) { _ra = Lst(); _dec = 90; _last = DateTime.UtcNow; _pier = DestinationSideOfPier(_ra, _dec); }
            _timer = new Timer(Tick, null, 0, 100);
            Connected = true;
        }

        public override void Disconnect() { _timer?.Dispose(); _timer = null; Connected = false; }

        private double Lst() => AstroTime.Now.LocalSiderealHours(Site.LongitudeDeg);

        private bool _arrived; // a slew ended since the last tick

        private void Tick(object state)
        {
            bool arrived;
            lock (_lock) { Advance(DateTime.UtcNow); arrived = _arrived; _arrived = false; }
            if (arrived) RaiseStateChanged();
        }

        /// <summary>Moves the simulation on to now (under the lock).</summary>
        private void Advance(DateTime now)
        {
            double dt = (now - _last).TotalSeconds;
            if (dt <= 0) return;
            // the part of each guide pulse that falls between the last update and now
            for (int a = 0; a < 2; a++)
            {
                if (_pulseEnd[a] <= _last) continue;
                var from = _pulseStart[a] > _last ? _pulseStart[a] : _last;
                var to = _pulseEnd[a] < now ? _pulseEnd[a] : now;
                double s = (to - from).TotalSeconds;
                if (s <= 0) continue;
                if (a == 0) _ra += _pulseRate[0] * s; else _dec = AstroMath.Clamp(_dec + _pulseRate[1] * s, -90, 90);
            }
            if (!_tracking || _atPark) _ra += dt * 1.00273791 / 3600.0; // RA drifts with sidereal time when not tracking
            else if (!_slewing) { _driftRa += DriftRaArcsecPerMin * dt / 60; _driftDec += DriftDecArcsecPerMin * dt / 60; }
            if (_slewing)
            {
                double dra = AstroMath.NormalizeDeg180((_targetRa - _ra) * 15), ddec = _targetDec - _dec;
                double step = SlewRateDegPerSec * dt;
                double dist = Math.Sqrt(dra * dra + ddec * ddec);
                if (dist <= step) { _ra = _targetRa; _dec = _targetDec; _slewing = false; _driftRa = _driftDec = 0; _arrived = true; }
                else { _ra += dra / dist * step / 15; _dec += ddec / dist * step; }
            }
            _ra += _axisRate[0] * dt / 15; _dec = AstroMath.Clamp(_dec + _axisRate[1] * dt, -90, 90);
            _ra = AstroMath.NormalizeHours(_ra);
            _last = now;
        }

        private double PeriodicError(DateTime now) =>
            PeriodicErrorArcsec == 0 || WormPeriodSec <= 0 || !_tracking || _atPark ? 0 : PeriodicErrorArcsec * Math.Sin(2 * Math.PI * (now.Ticks / 1e7 % WormPeriodSec) / WormPeriodSec);

        /// <summary>The mount's axis in the Earth-fixed equatorial frame (longitude = −hour angle), and its rotation from the pole.</summary>
        private void AxisRotation(out Vec3 k, out double angle)
        {
            k = Vec3.UnitZ; angle = 0;
            if (PolarErrorAltArcmin == 0 && PolarErrorAzArcmin == 0) return;
            double lat = Site.LatRad, poleAlt = Math.Abs(lat), poleAz = lat >= 0 ? 0 : Math.PI;
            double alt = poleAlt + PolarErrorAltArcmin / 60 * AstroMath.Deg2Rad;
            double az = poleAz + PolarErrorAzArcmin / 60 * AstroMath.Deg2Rad / Math.Max(0.05, Math.Cos(poleAlt));
            double ha, dec;
            Transforms.AltAzToHaDec(az, alt, lat, out ha, out dec);
            var axis = Vec3.FromSpherical(-ha, dec);
            var pole = lat >= 0 ? Vec3.UnitZ : -Vec3.UnitZ;
            var c = Vec3.Cross(pole, axis);
            angle = Vec3.AngleBetween(pole, axis);
            k = c.Length > 0 ? c.Normalized() : Vec3.UnitZ;
        }

        private static Vec3 Rotate(Vec3 v, Vec3 k, double a) =>
            a == 0 ? v : v * Math.Cos(a) + Vec3.Cross(k, v) * Math.Sin(a) + k * (Vec3.Dot(k, v) * (1 - Math.Cos(a)));

        /// <summary>
        /// Where the optical axis truly points (hour angle and Dec, radians) for the mount's own coordinates: they are
        /// coordinates about its axis, which polar misalignment turns away from the pole.
        /// </summary>
        private void TrueHaDec(double lstHours, double raHours, double decDeg, out double ha, out double dec)
        {
            ha = (lstHours - raHours) * AstroMath.Hour2Rad; dec = decDeg * AstroMath.Deg2Rad;
            Vec3 k; double a;
            AxisRotation(out k, out a);
            if (a == 0) return;
            var v = Rotate(Vec3.FromSpherical(-ha, dec), k, a);
            double lon, lat;
            v.ToSpherical(out lon, out lat);
            ha = -lon; dec = lat;
        }

        /// <summary>
        /// How far polar misalignment turns the camera on the sky (degrees, north through east): the camera keeps its angle
        /// to the mount's own north, which points to the mount's axis rather than to the pole.
        /// </summary>
        public double TrueRotationOffsetDeg
        {
            get
            {
                Vec3 k; double a;
                AxisRotation(out k, out a);
                if (a == 0) return 0;
                double ra, dec, lst = Lst();
                lock (_lock) { Advance(DateTime.UtcNow); ra = _ra; dec = _dec; }
                double ha, de;
                TrueHaDec(lst, ra, dec, out ha, out de);
                var v = Vec3.FromSpherical(-ha, de);
                var mountNorth = Rotate(Vec3.UnitZ, k, a);
                var east = Vec3.Cross(Vec3.UnitZ, v);
                if (east.Length < 1e-9) return 0;
                east = east.Normalized();
                var north = Vec3.Cross(v, east);
                var m = mountNorth - v * Vec3.Dot(mountNorth, v);
                return Math.Atan2(Vec3.Dot(m, east), Vec3.Dot(m, north)) * AstroMath.Rad2Deg;
            }
        }

        public double RightAscension { get { lock (_lock) { Advance(DateTime.UtcNow); return AstroMath.NormalizeHours(_ra + PointingErrorRa / 15); } } }
        public double Declination { get { lock (_lock) { Advance(DateTime.UtcNow); return AstroMath.Clamp(_dec + PointingErrorDec, -90, 90); } } }
        /// <summary>True optical axis pointing (without the injected model error, with the tracking errors), used by the camera simulator.</summary>
        public double TrueRa
        {
            get
            {
                lock (_lock)
                {
                    var now = DateTime.UtcNow;
                    Advance(now);
                    double lst = Lst(), ha, dec;
                    TrueHaDec(lst, _ra, _dec, out ha, out dec);
                    double cosd = Math.Max(0.05, Math.Cos(dec));
                    return AstroMath.NormalizeHours(lst - ha * AstroMath.Rad2Hour + (PeriodicError(now) + _driftRa) / 3600.0 / 15.0 / cosd);
                }
            }
        }
        public double TrueDec
        {
            get
            {
                lock (_lock)
                {
                    Advance(DateTime.UtcNow);
                    double ha, dec;
                    TrueHaDec(Lst(), _ra, _dec, out ha, out dec);
                    return AstroMath.Clamp(dec * AstroMath.Rad2Deg + _driftDec / 3600.0, -90, 90);
                }
            }
        }
        public bool IsPulseGuiding { get { lock (_lock) { var now = DateTime.UtcNow; return _pulseEnd[0] > now || _pulseEnd[1] > now; } } }

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
        /// <summary>The side chosen at the last slew: a German mount keeps it while tracking through the meridian, until it flips.</summary>
        public PierSide SideOfPier { get { lock (_lock) return _pier; } }
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
                Advance(DateTime.UtcNow);
                // remove the injected pointing error so the scope ends at the requested apparent coordinates of its own model
                _targetRa = AstroMath.NormalizeHours(raHours - PointingErrorRa / 15);
                _targetDec = AstroMath.Clamp(decDeg - PointingErrorDec, -90, 90);
                _pier = DestinationSideOfPier(raHours, decDeg);
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
                Advance(DateTime.UtcNow);
                // a sync corrects the model: the error becomes zero for the synced position
                PointingErrorRa = AstroMath.NormalizeDeg180((raHours - _ra) * 15);
                PointingErrorDec = decDeg - _dec;
            }
            RaiseStateChanged();
        }

        public void AbortSlew() { lock (_lock) { _slewing = false; _axisRate[0] = _axisRate[1] = 0; } RaiseStateChanged(); }
        public void Park() { if (_atPark) return; SlewToCoordinatesAsync(Lst(), 90); while (Slewing) Thread.Sleep(100); _atPark = true; _tracking = false; RaiseStateChanged(); }
        public void Unpark() { _atPark = false; _tracking = true; RaiseStateChanged(); }
        public void SetPark() { }

        /// <summary>
        /// Starts a guide pulse and returns at once (as ASCOM asks); the mount moves at the guide rate for ms milliseconds,
        /// IsPulseGuiding meanwhile. A new pulse on the same axis replaces one still running.
        /// </summary>
        public void PulseGuide(GuideDirection dir, int ms)
        {
            if (ms <= 0) return;
            lock (_lock)
            {
                var now = DateTime.UtcNow;
                Advance(now);
                bool dec = dir == GuideDirection.North || dir == GuideDirection.South;
                int axis = dec ? 1 : 0;
                double sign = dir == GuideDirection.North || dir == GuideDirection.East ? 1 : -1;
                double delayMs = 0;
                if (dec)
                {
                    // backlash belongs to the motor: reversing it takes up the slack before anything moves
                    int motor = dir == GuideDirection.North ? 1 : -1;
                    if (_decMotor != 0 && motor != _decMotor) _decSlackMs = DecBacklashMs;
                    _decMotor = motor;
                    delayMs = Math.Min(_decSlackMs, ms);
                    _decSlackMs -= delayMs;
                    if (!DecPulsesFollowSky && _pier == PierSide.East) sign = -sign;
                }
                _pulseStart[axis] = now.AddMilliseconds(delayMs);
                _pulseEnd[axis] = now.AddMilliseconds(ms);
                _pulseRate[axis] = dec ? sign * GuideRateDec : sign * GuideRateRa / 15.0;
            }
        }

        public void MoveAxis(int axis, double rate) { lock (_lock) { Advance(DateTime.UtcNow); _axisRate[AstroMath.Clamp(axis, 0, 1)] = rate; } RaiseStateChanged(); }

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

    /// <summary>
    /// An adaptive optics unit: each step tilts the image StepArcsec on the sky, along axes turned AngleDeg from the
    /// camera's, within MaxSteps each way. The guide camera simulator shows its tilt.
    /// </summary>
    public class AoSimulator : DeviceBase, IAdaptiveOptics
    {
        private int _x, _y;
        public int MaxSteps { get; set; } = 45;
        public double StepArcsec = 0.4, AngleDeg = 5;
        public int PositionX => _x;
        public int PositionY => _y;

        public AoSimulator() { Info = new DeviceInfo { Kind = DeviceKind.AdaptiveOptics, Protocol = DeviceProtocol.Simulator, Name = "AO Simulator" }; }
        public override void Connect() { Connected = true; }
        public override void Disconnect() { Connected = false; }

        public bool Step(AoDirection dir, int count)
        {
            if (count <= 0) return true;
            int sign = dir == AoDirection.North || dir == AoDirection.West ? 1 : -1;
            bool ns = dir == AoDirection.North || dir == AoDirection.South;
            int now = ns ? _y : _x, to = Math.Max(-MaxSteps, Math.Min(MaxSteps, now + sign * count));
            if (ns) _y = to; else _x = to;
            return Math.Abs(to - now) == count;
        }

        public void Center() { _x = _y = 0; }

        /// <summary>The image shift (pixels) the tilt gives at a pixel scale.</summary>
        public void Shift(double arcsecPerPx, out double dx, out double dy)
        {
            double a = AngleDeg * AstroMath.Deg2Rad, k = StepArcsec / arcsecPerPx;
            dx = k * (_x * Math.Cos(a) - _y * Math.Sin(a));
            dy = k * (_x * Math.Sin(a) + _y * Math.Cos(a));
        }
    }

    /// <summary>A camera rotator: turns at 10° a second to the angle asked for.</summary>
    public class RotatorSimulator : DeviceBase, IRotator
    {
        private readonly object _lock = new object();
        private double _pos, _target;
        private DateTime _last = DateTime.UtcNow;
        public double DegPerSec = 10;

        public RotatorSimulator() { Info = new DeviceInfo { Kind = DeviceKind.Rotator, Protocol = DeviceProtocol.Simulator, Name = "Rotator Simulator" }; }
        public override void Connect() { Connected = true; }
        public override void Disconnect() { Connected = false; }

        private void Advance()
        {
            var now = DateTime.UtcNow;
            double step = DegPerSec * (now - _last).TotalSeconds;
            _last = now;
            double d = AstroMath.NormalizeDeg180(_target - _pos);
            _pos = Math.Abs(d) <= step ? _target : AstroMath.NormalizeDeg(_pos + Math.Sign(d) * step);
        }

        public double Position { get { lock (_lock) { Advance(); return _pos; } } }
        public bool IsMoving { get { lock (_lock) { Advance(); return _pos != _target; } } }
        public bool Reverse { get; set; }
        public void MoveAbsolute(double deg) { lock (_lock) { Advance(); _target = AstroMath.NormalizeDeg(deg); } RaiseStateChanged(); }
        public void Halt() { lock (_lock) { Advance(); _target = _pos; } }
    }

    /// <summary>
    /// A power and dew-heater box: two dew heaters with a level from 0 to 100 %, a fan and the mount's power, each turned
    /// on and off. The mount's power starts on.
    /// </summary>
    public class SwitchSimulator : DeviceBase, ISwitchDevice
    {
        private readonly object _lock = new object();
        private readonly string[] _names = { "Dew heater 1", "Dew heater 2", "Fan", "Mount power" };
        private readonly double[] _max = { 100, 100, 1, 1 };
        private readonly double[] _values = { 0, 0, 0, 1 };

        public SwitchSimulator() { Info = new DeviceInfo { Kind = DeviceKind.Switch, Protocol = DeviceProtocol.Simulator, Name = "Switch Simulator" }; }
        public override void Connect() { Connected = true; }
        public override void Disconnect() { Connected = false; }

        public int Count => _names.Length;
        public string GetName(int i) => _names[Check(i)];
        public double GetMaxValue(int i) => _max[Check(i)];
        public bool GetSwitch(int i) { lock (_lock) return _values[Check(i)] > 0; }
        public void SetSwitch(int i, bool on) { lock (_lock) _values[Check(i)] = on ? _max[i] : 0; RaiseStateChanged(); }
        public double GetValue(int i) { lock (_lock) return _values[Check(i)]; }
        public void SetValue(int i, double v) { lock (_lock) _values[Check(i)] = AstroMath.Clamp(Math.Round(v), 0, _max[Check(i)]); RaiseStateChanged(); }

        private int Check(int i)
        {
            if (i < 0 || i >= _names.Length) throw new ArgumentOutOfRangeException(nameof(i), "No switch " + i);
            return i;
        }
    }

    /// <summary>
    /// A flat panel with a motorised dust cover: the cover takes 3 seconds to open or close (MoveSeconds), and the panel
    /// lights at a brightness from 0 to 255.
    /// </summary>
    public class CoverCalibratorSimulator : DeviceBase, ICoverCalibrator
    {
        private readonly object _lock = new object();
        private string _state = "Closed";
        private int _brightness, _move;
        public double MoveSeconds = 3;

        public CoverCalibratorSimulator() { Info = new DeviceInfo { Kind = DeviceKind.CoverCalibrator, Protocol = DeviceProtocol.Simulator, Name = "Flat Panel Simulator" }; }
        public override void Connect() { Connected = true; }
        public override void Disconnect() { Connected = false; }

        /// <summary>Closed, Moving or Open.</summary>
        public string CoverState { get { lock (_lock) return _state; } }
        public int Brightness { get { lock (_lock) return _brightness; } }
        public int MaxBrightness => 255;

        public void OpenCover() => Move("Open");
        public void CloseCover() => Move("Closed");

        private void Move(string to)
        {
            int run;
            lock (_lock)
            {
                if (_state == to) return;
                _state = "Moving";
                run = ++_move;
            }
            RaiseStateChanged();
            Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, MoveSeconds)));
                // a later move takes over from this one
                lock (_lock) { if (run != _move) return; _state = to; }
                RaiseStateChanged();
            });
        }

        public void CalibratorOn(int brightness) { lock (_lock) _brightness = Math.Max(0, Math.Min(MaxBrightness, brightness)); RaiseStateChanged(); }
        public void CalibratorOff() { lock (_lock) _brightness = 0; RaiseStateChanged(); }
    }

    /// <summary>A dome turning at 40° a second (2° every 50 ms) to the azimuth asked for, with a shutter taking 3 seconds.</summary>
    public class DomeSimulator : DeviceBase, IDome
    {
        private readonly object _lock = new object();
        private double _az, _target;
        private int _move;
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
            int run;
            lock (_lock) { _target = AstroMath.NormalizeDeg(az); run = ++_move; }
            Task.Run(() =>
            {
                while (true)
                {
                    lock (_lock)
                    {
                        // a later slew takes over; the last step lands on the target rather than going past it
                        if (run != _move) return;
                        double d = AstroMath.NormalizeDeg180(_target - _az);
                        if (Math.Abs(d) < 1e-9) break;
                        _az = AstroMath.NormalizeDeg(_az + Math.Sign(d) * Math.Min(2, Math.Abs(d)));
                    }
                    Thread.Sleep(50);
                }
                RaiseStateChanged();
            });
        }
        public void OpenShutter() { _shutter = ShutterState.Opening; RaiseStateChanged(); Task.Run(() => { Thread.Sleep(3000); _shutter = ShutterState.Open; RaiseStateChanged(); }); }
        public void CloseShutter() { _shutter = ShutterState.Closing; RaiseStateChanged(); Task.Run(() => { Thread.Sleep(3000); _shutter = ShutterState.Closed; RaiseStateChanged(); }); }
        public void Park() => SlewToAzimuth(0);
        public void FindHome() => SlewToAzimuth(0);
        public void AbortSlew() { lock (_lock) { _target = _az; _move++; } }
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
        /// <summary>The camera's angle on the sky (degrees, north through east) on the west side of the pier; it turns 180° on the east side.</summary>
        public double RotationDeg;
        /// <summary>Seeing's wander of the star image: RMS (arcsec, per axis) in a 1 s exposure, less in longer ones.</summary>
        public double JitterArcsec;
        /// <summary>Hot pixels, always at the same places in a frame of a given size, and how bright they grow (fraction of full scale per minute).</summary>
        public int HotPixels = 40;
        public double HotPixelLevel = 0.3;
        /// <summary>The share of starlight that gets through (1 clear, less under thin cloud, 0 behind thick cloud).</summary>
        public double Transparency = 1;
        /// <summary>The camera stops answering, as when its driver drops it: exposures fail until it is connected again (a new one).</summary>
        public bool Unplugged;
        private int _w = 2048, _h = 1536;
        private double _pixelUm = 3.76;
        private CameraState _state = CameraState.Idle;
        private AstroImage _last;
        private CancellationTokenSource _cts;
        private DateTime _start;
        private double _duration;
        private double _ccdTemp = 15, _setTemp = -10;
        private bool _cooler;
        private readonly Random _rng = new Random();
        public bool ColorSensor = false;
        /// <summary>A rotator the camera sits on (the imaging camera, or a guide camera on an off-axis guider): its angle adds to the camera's.</summary>
        public RotatorSimulator Rotator;
        public bool OnRotator;
        /// <summary>An AO unit in front of this camera: its tilt shifts the image.</summary>
        public AoSimulator Ao;

        public CameraSimulator() { Info = new DeviceInfo { Kind = DeviceKind.Camera, Protocol = DeviceProtocol.Simulator, Name = "Camera Simulator" }; NumX = _w; NumY = _h; }

        /// <summary>A small uncooled guide camera on a 240 mm guide scope, turned 30° on the sky, guiding through its ST-4 port.</summary>
        public static CameraSimulator GuideCamera(TelescopeSimulator mount)
        {
            var c = new CameraSimulator { Mount = mount, FocalLengthMm = 240, RotationDeg = 30, JitterArcsec = 0.6, Gain = 50, Offset = 10, HotPixels = 60 };
            c.Info = new DeviceInfo { Kind = DeviceKind.GuideCamera, Protocol = DeviceProtocol.Simulator, Name = "Guide Camera Simulator" };
            c.SetSensor(1280, 960, 3.75);
            return c;
        }

        /// <summary>Sets the sensor's size (pixels) and pixel size (µm), and the frame to the whole sensor.</summary>
        public void SetSensor(int width, int height, double pixelUm)
        {
            _w = width; _h = height; _pixelUm = pixelUm;
            StartX = StartY = 0; NumX = _w / Math.Max(1, BinX); NumY = _h / Math.Max(1, BinY);
        }

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
        public double PixelSizeX => _pixelUm;
        public double PixelSizeY => _pixelUm;
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
            if (Unplugged) throw new InvalidOperationException("The camera does not answer");
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            _duration = seconds;
            _start = DateTime.UtcNow;
            _state = CameraState.Exposing;
            _last = null;
            RaiseStateChanged();
            double ra0 = Mount != null ? Mount.TrueRa : 0, dec0 = Mount != null ? Mount.TrueDec : 0;
            double rot = RotationDeg + (Mount != null && Mount.SideOfPier == PierSide.East ? 180 : 0) + (OnRotator && Rotator != null ? Rotator.Position : 0)
                + (Mount != null ? Mount.TrueRotationOffsetDeg : 0);
            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(seconds, 30)), ct);
                    _state = CameraState.Download;
                    // the star image lies where the mount pointed on average during the exposure
                    double ra1 = Mount != null ? Mount.TrueRa : ra0, dec1 = Mount != null ? Mount.TrueDec : dec0;
                    double ra = AstroMath.NormalizeHours(ra0 + AstroMath.NormalizeDeg180((ra1 - ra0) * 15) / 30), dec = (dec0 + dec1) / 2;
                    _last = Render(seconds, light, ra, dec, rot);
                    _state = CameraState.Idle;
                }
                catch (OperationCanceledException) { _state = CameraState.Idle; }
                catch (Exception ex) { _state = CameraState.Error; Core.Util.Log.Error("CameraSim", ex); }
                RaiseStateChanged();
            });
        }

        public void AbortExposure() { _cts?.Cancel(); }

        public AstroImage GetImage() => _last;

        // the guide port passes pulses to the mount
        public bool CanPulseGuide => Mount != null;
        public bool IsPulseGuiding => Mount != null && Mount.IsPulseGuiding;
        public void PulseGuide(GuideDirection dir, int ms)
        {
            if (Mount == null) throw new InvalidOperationException("Nothing is connected to the camera's guide port");
            Mount.PulseGuide(dir, ms);
        }

        private AstroImage Render(double exp, bool light, double raH, double decD, double rotDeg)
        {
            int w = Math.Max(16, NumX), h = Math.Max(16, NumY); // NumX/NumY are in binned pixels (ASCOM convention)
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
                var sky = new SkyContext(site, AstroTime.FromDateTime(_start));
                var icrs = sky.ApparentToIcrs(raH * AstroMath.Hour2Rad, decD * AstroMath.Deg2Rad);
                // the camera's angle is from the north of date, which precession has turned away from the catalog's north
                // (by degrees near the pole): the camera keeps its angle to the mount as RA turns
                var pole = sky.ApparentToIcrs(0, Math.PI / 2);
                double dpa = pole.Ra - icrs.Ra;
                rotDeg += Math.Atan2(Math.Sin(dpa) * Math.Cos(pole.Dec), Math.Cos(icrs.Dec) * Math.Sin(pole.Dec) - Math.Sin(icrs.Dec) * Math.Cos(pole.Dec) * Math.Cos(dpa)) * AstroMath.Rad2Deg;
                // tangent-plane rendering of catalog stars; the optical axis is at the centre of the whole sensor, and
                // the frame read out starts at StartX, StartY
                double ra0 = icrs.Ra, de0 = icrs.Dec;
                double cx = _w / (double)Math.Max(1, BinX) / 2 - StartX, cy = _h / (double)Math.Max(1, BinY) / 2 - StartY;
                double reachX = Math.Max(Math.Abs(cx), Math.Abs(w - cx)), reachY = Math.Max(Math.Abs(cy), Math.Abs(h - cy));
                var stars = new List<Star>();
                double radius = Math.Sqrt(reachX * reachX + reachY * reachY) * 1.2 * scale / 3600.0 * AstroMath.Deg2Rad;
                CatalogManager.Instance.Stars.Cone(ra0, de0, radius, 16, stars);
                double sigma = Math.Max(0.6, fwhmPx / 2.3548);
                int r = (int)Math.Ceiling(sigma * 4);
                // seeing moves the whole field a little in each exposure
                double jit = JitterArcsec / Math.Sqrt(Math.Max(0.05, exp));
                double jx = jit > 0 ? jit * Gauss() : 0, jy = jit > 0 ? jit * Gauss() : 0;
                double aox = 0, aoy = 0;
                if (Ao != null) Ao.Shift(scale, out aox, out aoy);
                double cr = Math.Cos(rotDeg * AstroMath.Deg2Rad), sr = Math.Sin(rotDeg * AstroMath.Deg2Rad);
                // east is left and north up at no rotation
                Func<double, double, Pt> place = (xi, eta) =>
                {
                    double e = xi * AstroMath.Rad2ArcSec + jx, n = eta * AstroMath.Rad2ArcSec + jy;
                    return new Pt { X = cx - (e * cr - n * sr) / scale + aox, Y = cy - (e * sr + n * cr) / scale + aoy };
                };
                foreach (var s in stars)
                {
                    double xi, eta;
                    double cd = Math.Cos(s.Dec), sd = Math.Sin(s.Dec), cd0 = Math.Cos(de0), sd0 = Math.Sin(de0), da = s.Ra - ra0;
                    double den = sd * sd0 + cd * cd0 * Math.Cos(da);
                    if (den <= 0) continue;
                    xi = cd * Math.Sin(da) / den; eta = (sd * cd0 - cd * sd0 * Math.Cos(da)) / den;
                    var p = place(xi, eta);
                    double x = p.X, y = p.Y;
                    if (x < -r || y < -r || x > w + r || y > h + r) continue;
                    double flux = Math.Pow(10, -0.4 * (s.Mag - ZeroPointMag)) * exp * Transparency; // electrons
                    Splat(d, w, h, x, y, flux, sigma, r);
                }
                if (SyntheticFieldStars)
                    SyntheticSky.Stars(ra0, de0, radius, (sra, sdec, mag) =>
                    {
                        double cd = Math.Cos(sdec), sd = Math.Sin(sdec), cd0 = Math.Cos(de0), sd0 = Math.Sin(de0), da = sra - ra0;
                        double den = sd * sd0 + cd * cd0 * Math.Cos(da);
                        if (den <= 0) return;
                        var p = place(cd * Math.Sin(da) / den, (sd * cd0 - cd * sd0 * Math.Cos(da)) / den);
                        double x = p.X, y = p.Y;
                        if (x < -r || y < -r || x > w + r || y > h + r) return;
                        double flux = Math.Pow(10, -0.4 * (mag - ZeroPointMag)) * exp * Transparency;
                        Splat(d, w, h, x, y, flux, sigma, r);
                    });
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
            for (int i = 0; i < HotPixels; i++) { int p = hot.Next(d.Length); d[p] = (float)Math.Min(1, d[p] + HotPixelLevel * exp / 60); }
            img.ExposureSeconds = exp;
            img.DateObs = _start;
            img.Header.Set("IMAGETYP", light ? "Light Frame" : "Dark Frame");
            img.Header.Set("XPIXSZ", PixelSizeX * BinX); img.Header.Set("YPIXSZ", PixelSizeY * BinY);
            img.Header.Set("XBINNING", BinX); img.Header.Set("YBINNING", BinY);
            img.Header.Set("FOCALLEN", FocalLengthMm);
            img.Header.Set("CCD-TEMP", _ccdTemp);
            img.Header.Set("GAIN", Gain); img.Header.Set("OFFSET", Offset);
            img.Header.Set("RA", raH * 15); img.Header.Set("DEC", decD);
            img.Header.Set("INSTRUME", Info.Kind == DeviceKind.GuideCamera ? "Sidralune Guide Camera Simulator" : "Sidralune Camera Simulator");
            if (Focuser != null) img.Header.Set("FOCPOS", Focuser.Position);
            if (Wheel != null && Wheel.Position >= 0) img.Header.Set("FILTER", Wheel.Names[Wheel.Position]);
            return img;
        }

        private struct Pt { public double X, Y; }

        /// <summary>Add synthetic faint field stars (deterministic per sky position). Simulation only.</summary>
        public bool SyntheticFieldStars = true;

        /// <summary>
        /// Adds a Gaussian star of the given total flux, integrated over each pixel's area as a sensor collects it. Sampling
        /// the Gaussian only at pixel centres made undersampled stars (bin 2, about one pixel wide) look narrower or more
        /// elongated depending on where they fell within a pixel, so the star detector dropped many as hot pixels.
        /// </summary>
        private static void Splat(float[] d, int w, int h, double x, double y, double flux, double sigma, int r)
        {
            int x0 = (int)x - r, y0 = (int)y - r, n = 2 * r + 1;
            var fx = PixelFractions(x, x0, n, sigma);
            var fy = PixelFractions(y, y0, n, sigma);
            for (int j = 0; j < n; j++)
            {
                int yy = y0 + j;
                if (yy < 0 || yy >= h) continue;
                for (int i = 0; i < n; i++)
                {
                    int xx = x0 + i;
                    if (xx < 0 || xx >= w) continue;
                    d[yy * w + xx] += (float)(flux * fx[i] * fy[j]);
                }
            }
        }

        /// <summary>The fraction of a 1-D Gaussian centred at c that falls in each pixel from p0 (pixel p spans p ± 0.5).</summary>
        private static double[] PixelFractions(double c, int p0, int n, double sigma)
        {
            var f = new double[n];
            double prev = Stats.NormalCdf((p0 - 0.5 - c) / sigma);
            for (int i = 0; i < n; i++)
            {
                double next = Stats.NormalCdf((p0 + i + 0.5 - c) / sigma);
                f[i] = next - prev;
                prev = next;
            }
            return f;
        }

        private double Gauss()
        {
            double u1 = 1.0 - _rng.NextDouble(), u2 = _rng.NextDouble();
            return Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
        }
    }
}
