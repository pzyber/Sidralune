using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Sidralune.Imaging;
using Sidralune.Imaging.IO;

namespace Sidralune.Devices.Indi
{
    public class IndiProperty
    {
        public string Device, Name, Label, Group, Type, State, Perm;
        public Dictionary<string, string> Values = new Dictionary<string, string>();
        public byte[] Blob; public string BlobFormat;
        public double Num(string element) { string v; return Values.TryGetValue(element, out v) ? ParseIndiNumber(v) : double.NaN; }
        public static double ParseIndiNumber(string s)
        {
            s = s.Trim();
            if (s.Contains(":") || s.Contains(" ")) return Core.Mathematics.AstroMath.ParseSexagesimal(s);
            double d; return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? d : double.NaN;
        }
    }

    /// <summary>INDI protocol client (XML over TCP, default port 7624) with property cache and BLOB support.</summary>
    public class IndiClient : IDisposable
    {
        private TcpClient _tcp;
        private NetworkStream _ns;
        private Thread _reader;
        public readonly ConcurrentDictionary<string, IndiProperty> Properties = new ConcurrentDictionary<string, IndiProperty>();
        public event Action<IndiProperty> PropertyUpdated;
        public event Action<string, string> Message;
        public bool IsConnected => _tcp != null && _tcp.Connected;

        public void Connect(string host = "localhost", int port = 7624)
        {
            _tcp = new TcpClient(host, port);
            _ns = _tcp.GetStream();
            _reader = new Thread(ReadLoop) { IsBackground = true, Name = "INDI reader" };
            _reader.Start();
            Send("<getProperties version='1.7'/>");
        }

        public IEnumerable<string> Devices => Properties.Values.Select(p => p.Device).Distinct();

        public void Send(string xml)
        {
            var b = Encoding.UTF8.GetBytes(xml);
            lock (this) _ns.Write(b, 0, b.Length);
        }

        public void EnableBlob(string device, string mode = "Also") => Send(string.Format("<enableBLOB device='{0}'>{1}</enableBLOB>", Esc(device), mode));

        public void SetNumber(string device, string prop, params Tuple<string, double>[] values)
        {
            var sb = new StringBuilder();
            sb.AppendFormat("<newNumberVector device='{0}' name='{1}'>", Esc(device), Esc(prop));
            foreach (var v in values) sb.AppendFormat("<oneNumber name='{0}'>{1}</oneNumber>", Esc(v.Item1), v.Item2.ToString("R", CultureInfo.InvariantCulture));
            sb.Append("</newNumberVector>");
            Send(sb.ToString());
        }

        public void SetSwitch(string device, string prop, params Tuple<string, bool>[] values)
        {
            var sb = new StringBuilder();
            sb.AppendFormat("<newSwitchVector device='{0}' name='{1}'>", Esc(device), Esc(prop));
            foreach (var v in values) sb.AppendFormat("<oneSwitch name='{0}'>{1}</oneSwitch>", Esc(v.Item1), v.Item2 ? "On" : "Off");
            sb.Append("</newSwitchVector>");
            Send(sb.ToString());
        }

        public void SetText(string device, string prop, string element, string value) =>
            Send(string.Format("<newTextVector device='{0}' name='{1}'><oneText name='{2}'>{3}</oneText></newTextVector>", Esc(device), Esc(prop), Esc(element), Esc(value)));

        private static string Esc(string s) => System.Security.SecurityElement.Escape(s);

        public IndiProperty Get(string device, string prop)
        {
            IndiProperty p;
            return Properties.TryGetValue(device + "." + prop, out p) ? p : null;
        }

        /// <summary>Wait until a property leaves the Busy state (or timeout).</summary>
        public bool WaitIdle(string device, string prop, int timeoutMs)
        {
            var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < until)
            {
                var p = Get(device, prop);
                if (p != null && p.State != "Busy") return p.State != "Alert";
                Thread.Sleep(100);
            }
            return false;
        }

        private void ReadLoop()
        {
            // INDI is a stream of XML elements without a root: wrap in a fragment reader
            var settings = new XmlReaderSettings { ConformanceLevel = ConformanceLevel.Fragment, IgnoreWhitespace = true };
            try
            {
                using (var xr = XmlReader.Create(new StreamReader(_ns, Encoding.UTF8), settings))
                {
                    while (xr.Read())
                    {
                        if (xr.NodeType != XmlNodeType.Element || xr.Depth != 0) continue;
                        string el = xr.Name;
                        if (el == "message") { Message?.Invoke(xr.GetAttribute("device"), xr.GetAttribute("message")); continue; }
                        if (el == "delProperty") { string key = xr.GetAttribute("device") + "." + xr.GetAttribute("name"); IndiProperty rem; Properties.TryRemove(key, out rem); continue; }
                        if (!(el.StartsWith("def") || el.StartsWith("set")) || !el.EndsWith("Vector")) continue;
                        string dev = xr.GetAttribute("device"), name = xr.GetAttribute("name");
                        var prop = Properties.GetOrAdd(dev + "." + name, k => new IndiProperty { Device = dev, Name = name });
                        prop.Type = el.Substring(3, el.Length - 9);
                        prop.State = xr.GetAttribute("state") ?? prop.State;
                        if (el.StartsWith("def")) { prop.Label = xr.GetAttribute("label"); prop.Group = xr.GetAttribute("group"); prop.Perm = xr.GetAttribute("perm"); }
                        var sub = xr.ReadSubtree();
                        while (sub.Read())
                        {
                            if (sub.NodeType != XmlNodeType.Element || sub.Depth != 1) continue;
                            string en = sub.GetAttribute("name");
                            if (sub.Name == "oneBLOB")
                            {
                                prop.BlobFormat = sub.GetAttribute("format");
                                string b64 = sub.ReadElementContentAsString();
                                prop.Blob = Convert.FromBase64String(b64.Trim());
                            }
                            else prop.Values[en] = sub.ReadElementContentAsString().Trim();
                        }
                        PropertyUpdated?.Invoke(prop);
                    }
                }
            }
            catch (Exception ex) { Message?.Invoke("INDI", "Connection closed: " + ex.Message); }
        }

        public void Dispose() { try { _ns?.Dispose(); _tcp?.Close(); } catch { } }

        public static IDevice Create(DeviceInfo info)
        {
            switch (info.Kind)
            {
                case DeviceKind.Telescope: return new IndiTelescope(info);
                case DeviceKind.Camera: return new IndiCamera(info);
                case DeviceKind.Focuser: return new IndiFocuser(info);
            }
            throw new NotSupportedException("INDI " + info.Kind + " is not supported yet");
        }
    }

    /// <summary>Base for INDI devices: address "host:port/Device Name".</summary>
    public abstract class IndiBase : DeviceBase
    {
        protected IndiClient C;
        protected string Dev;
        protected IndiBase(DeviceInfo info)
        {
            Info = info;
            Dev = info.Address.Substring(info.Address.IndexOf('/') + 1);
        }

        public override void Connect()
        {
            var hp = Info.Address.Split('/')[0].Split(':');
            C = new IndiClient();
            C.Connect(hp[0], hp.Length > 1 ? int.Parse(hp[1]) : 7624);
            Thread.Sleep(500);
            C.SetSwitch(Dev, "CONNECTION", Tuple.Create("CONNECT", true), Tuple.Create("DISCONNECT", false));
            C.WaitIdle(Dev, "CONNECTION", 10000);
            Connected = true;
        }

        public override void Disconnect()
        {
            try { C?.SetSwitch(Dev, "CONNECTION", Tuple.Create("CONNECT", false), Tuple.Create("DISCONNECT", true)); } catch { }
            C?.Dispose(); C = null;
            Connected = false;
        }

        protected double Num(string prop, string el) { var p = C.Get(Dev, prop); return p != null ? p.Num(el) : double.NaN; }
        protected bool Sw(string prop, string el) { var p = C.Get(Dev, prop); string v; return p != null && p.Values.TryGetValue(el, out v) && v == "On"; }
        protected bool Busy(string prop) { var p = C.Get(Dev, prop); return p != null && p.State == "Busy"; }
    }

    public class IndiTelescope : IndiBase, ITelescope
    {
        public IndiTelescope(DeviceInfo i) : base(i) { }
        public double RightAscension => Num("EQUATORIAL_EOD_COORD", "RA");
        public double Declination => Num("EQUATORIAL_EOD_COORD", "DEC");
        public double Altitude => Num("HORIZONTAL_COORD", "ALT");
        public double Azimuth => Num("HORIZONTAL_COORD", "AZ");
        public double SiderealTime => Num("TIME_LST", "LST");
        public bool Slewing => Busy("EQUATORIAL_EOD_COORD");
        public bool Tracking { get { return Sw("TELESCOPE_TRACK_STATE", "TRACK_ON"); } set { C.SetSwitch(Dev, "TELESCOPE_TRACK_STATE", Tuple.Create("TRACK_ON", value), Tuple.Create("TRACK_OFF", !value)); } }
        public TrackingRate TrackingRate { get; set; }
        public bool AtPark => Sw("TELESCOPE_PARK", "PARK");
        public PierSide SideOfPier => Sw("TELESCOPE_PIER_SIDE", "PIER_WEST") ? PierSide.West : Sw("TELESCOPE_PIER_SIDE", "PIER_EAST") ? PierSide.East : PierSide.Unknown;
        public bool CanPark => C.Get(Dev, "TELESCOPE_PARK") != null;
        public bool CanSync => true;
        public bool CanPulseGuide => C.Get(Dev, "TELESCOPE_TIMED_GUIDE_NS") != null;
        public bool CanSlewAltAz => C.Get(Dev, "HORIZONTAL_COORD") != null;
        public double SiteLatitude => Num("GEOGRAPHIC_COORD", "LAT");
        public double SiteLongitude => Num("GEOGRAPHIC_COORD", "LONG");
        public double[] AxisRates => new[] { 0.0083, 0.25, 1.0, 3.0 };
        private void Coord(string mode, double ra, double dec)
        {
            C.SetSwitch(Dev, "ON_COORD_SET", Tuple.Create("TRACK", mode == "TRACK"), Tuple.Create("SLEW", mode == "SLEW"), Tuple.Create("SYNC", mode == "SYNC"));
            C.SetNumber(Dev, "EQUATORIAL_EOD_COORD", Tuple.Create("RA", ra), Tuple.Create("DEC", dec));
        }
        public void SlewToCoordinatesAsync(double ra, double dec) => Coord("TRACK", ra, dec);
        public void SlewToAltAzAsync(double az, double alt) => C.SetNumber(Dev, "HORIZONTAL_COORD", Tuple.Create("AZ", az), Tuple.Create("ALT", alt));
        public void SyncToCoordinates(double ra, double dec) => Coord("SYNC", ra, dec);
        public void AbortSlew() => C.SetSwitch(Dev, "TELESCOPE_ABORT_MOTION", Tuple.Create("ABORT", true));
        public void Park() => C.SetSwitch(Dev, "TELESCOPE_PARK", Tuple.Create("PARK", true), Tuple.Create("UNPARK", false));
        public void Unpark() => C.SetSwitch(Dev, "TELESCOPE_PARK", Tuple.Create("PARK", false), Tuple.Create("UNPARK", true));
        public void SetPark() => C.SetSwitch(Dev, "TELESCOPE_PARK_OPTION", Tuple.Create("PARK_CURRENT", true));
        public void PulseGuide(GuideDirection dir, int ms)
        {
            if (dir == GuideDirection.North || dir == GuideDirection.South)
                C.SetNumber(Dev, "TELESCOPE_TIMED_GUIDE_NS", Tuple.Create("TIMED_GUIDE_N", dir == GuideDirection.North ? (double)ms : 0), Tuple.Create("TIMED_GUIDE_S", dir == GuideDirection.South ? (double)ms : 0));
            else
                C.SetNumber(Dev, "TELESCOPE_TIMED_GUIDE_WE", Tuple.Create("TIMED_GUIDE_W", dir == GuideDirection.West ? (double)ms : 0), Tuple.Create("TIMED_GUIDE_E", dir == GuideDirection.East ? (double)ms : 0));
        }
        public void MoveAxis(int axis, double rate)
        {
            string prop = axis == 0 ? "TELESCOPE_MOTION_WE" : "TELESCOPE_MOTION_NS";
            string a = axis == 0 ? "MOTION_WEST" : "MOTION_NORTH", b = axis == 0 ? "MOTION_EAST" : "MOTION_SOUTH";
            C.SetSwitch(Dev, prop, Tuple.Create(a, rate > 0), Tuple.Create(b, rate < 0));
        }
        public PierSide DestinationSideOfPier(double ra, double dec) => PierSide.Unknown;
    }

    public class IndiCamera : IndiBase, ICamera
    {
        private volatile bool _ready;
        private AstroImage _img;
        public IndiCamera(DeviceInfo i) : base(i) { }
        public override void Connect()
        {
            base.Connect();
            C.EnableBlob(Dev);
            C.PropertyUpdated += p =>
            {
                if (p.Device == Dev && p.Type == "BLOB" && p.Blob != null)
                {
                    try { using (var ms = new MemoryStream(p.Blob)) _img = FitsIO.Read(ms); _ready = true; } catch { }
                }
            };
        }
        public int SensorWidth => (int)Num("CCD_INFO", "CCD_MAX_X");
        public int SensorHeight => (int)Num("CCD_INFO", "CCD_MAX_Y");
        public double PixelSizeX => Num("CCD_INFO", "CCD_PIXEL_SIZE_X");
        public double PixelSizeY => Num("CCD_INFO", "CCD_PIXEL_SIZE_Y");
        public int BinX { get { return (int)Num("CCD_BINNING", "HOR_BIN"); } set { C.SetNumber(Dev, "CCD_BINNING", Tuple.Create("HOR_BIN", (double)value), Tuple.Create("VER_BIN", (double)value)); } }
        public int BinY { get { return (int)Num("CCD_BINNING", "VER_BIN"); } set { } }
        public int MaxBin => 4;
        public int StartX { get; set; }
        public int StartY { get; set; }
        public int NumX { get; set; }
        public int NumY { get; set; }
        public CameraState State => Busy("CCD_EXPOSURE") ? CameraState.Exposing : CameraState.Idle;
        public bool ImageReady => _ready;
        public bool CanSetTemperature => C.Get(Dev, "CCD_TEMPERATURE") != null;
        public bool CoolerOn { get { return Sw("CCD_COOLER", "COOLER_ON"); } set { C.SetSwitch(Dev, "CCD_COOLER", Tuple.Create("COOLER_ON", value), Tuple.Create("COOLER_OFF", !value)); } }
        public double CcdTemperature => Num("CCD_TEMPERATURE", "CCD_TEMPERATURE_VALUE");
        public double SetTemperature { get { return CcdTemperature; } set { C.SetNumber(Dev, "CCD_TEMPERATURE", Tuple.Create("CCD_TEMPERATURE_VALUE", value)); } }
        public double CoolerPower => Num("CCD_COOLER_POWER", "CCD_COOLER_VALUE");
        public bool HasGain => C.Get(Dev, "CCD_GAIN") != null || C.Get(Dev, "CCD_CONTROLS") != null;
        public int Gain { get { double g = Num("CCD_GAIN", "GAIN"); if (double.IsNaN(g)) g = Num("CCD_CONTROLS", "Gain"); return double.IsNaN(g) ? 0 : (int)g; } set { if (C.Get(Dev, "CCD_GAIN") != null) C.SetNumber(Dev, "CCD_GAIN", Tuple.Create("GAIN", (double)value)); else C.SetNumber(Dev, "CCD_CONTROLS", Tuple.Create("Gain", (double)value)); } }
        public int GainMin => 0;
        public int GainMax => 1000;
        public bool HasOffset => C.Get(Dev, "CCD_OFFSET") != null;
        public int Offset { get { return (int)Num("CCD_OFFSET", "OFFSET"); } set { C.SetNumber(Dev, "CCD_OFFSET", Tuple.Create("OFFSET", (double)value)); } }
        public int BitDepth => (int)Num("CCD_INFO", "CCD_BITSPERPIXEL");
        public BayerPattern Bayer { get { var p = C.Get(Dev, "CCD_CFA"); string v; BayerPattern b; return p != null && p.Values.TryGetValue("CFA_TYPE", out v) && Enum.TryParse(v, out b) ? b : BayerPattern.None; } }
        public double ElectronsPerAdu => 1;
        public double PercentCompleted => 0;
        public void StartExposure(double s, bool light)
        {
            _ready = false; _img = null;
            C.SetSwitch(Dev, "CCD_FRAME_TYPE", Tuple.Create("FRAME_LIGHT", light), Tuple.Create("FRAME_DARK", !light));
            C.SetNumber(Dev, "CCD_EXPOSURE", Tuple.Create("CCD_EXPOSURE_VALUE", s));
        }
        public void AbortExposure() => C.SetSwitch(Dev, "CCD_ABORT_EXPOSURE", Tuple.Create("ABORT", true));
        public AstroImage GetImage() => _img;
    }

    public class IndiFocuser : IndiBase, IFocuser
    {
        public IndiFocuser(DeviceInfo i) : base(i) { }
        public int Position => (int)Num("ABS_FOCUS_POSITION", "FOCUS_ABSOLUTE_POSITION");
        public int MaxStep => double.IsNaN(Num("FOCUS_MAX", "FOCUS_MAX_VALUE")) ? 100000 : (int)Num("FOCUS_MAX", "FOCUS_MAX_VALUE");
        public bool IsMoving => Busy("ABS_FOCUS_POSITION");
        public bool Absolute => true;
        public double Temperature => Num("FOCUS_TEMPERATURE", "TEMPERATURE");
        public double StepSizeMicrons => double.NaN;
        public void Move(int p) => C.SetNumber(Dev, "ABS_FOCUS_POSITION", Tuple.Create("FOCUS_ABSOLUTE_POSITION", (double)p));
        public void Halt() => C.SetSwitch(Dev, "FOCUS_ABORT_MOTION", Tuple.Create("ABORT", true));
    }
}
