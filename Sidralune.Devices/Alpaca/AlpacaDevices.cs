using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Sidralune.Core.Util;
using Sidralune.Imaging;

namespace Sidralune.Devices.Alpaca
{
    /// <summary>ASCOM Alpaca discovery (UDP broadcast) and management API.</summary>
    public static class AlpacaDiscovery
    {
        /// <summary>Broadcast "alpacadiscovery1" on UDP 32227 and collect responding servers (host:port).</summary>
        public static List<string> FindServers(int timeoutMs = 1500)
        {
            var result = new List<string>();
            using (var udp = new UdpClient())
            {
                udp.EnableBroadcast = true;
                udp.Client.ReceiveTimeout = 300;
                var msg = Encoding.ASCII.GetBytes("alpacadiscovery1");
                udp.Send(msg, msg.Length, new IPEndPoint(IPAddress.Broadcast, 32227));
                try { udp.Send(msg, msg.Length, new IPEndPoint(IPAddress.Loopback, 32227)); } catch { }
                var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                while (DateTime.UtcNow < until)
                {
                    try
                    {
                        var ep = new IPEndPoint(IPAddress.Any, 0);
                        var data = udp.Receive(ref ep);
                        var json = Json.ParseObject(Encoding.UTF8.GetString(data));
                        int port = Convert.ToInt32(json["AlpacaPort"]);
                        string s = ep.Address + ":" + port;
                        if (!result.Contains(s)) result.Add(s);
                    }
                    catch (SocketException) { }
                    catch { }
                }
            }
            return result;
        }

        /// <summary>Devices configured on a server via /management/v1/configureddevices.</summary>
        public static List<DeviceInfo> Devices(string server)
        {
            var list = new List<DeviceInfo>();
            var text = Net.GetString("http://" + server + "/management/v1/configureddevices", 5000);
            var d = Json.ParseObject(text);
            foreach (Dictionary<string, object> dev in (List<object>)d["Value"])
            {
                DeviceKind kind;
                if (!Enum.TryParse((string)dev["DeviceType"], true, out kind)) continue;
                list.Add(new DeviceInfo
                {
                    Kind = kind, Protocol = DeviceProtocol.Alpaca, Name = (string)dev["DeviceName"],
                    Address = server + "/" + Convert.ToInt32(dev["DeviceNumber"]), Description = Convert.ToString(dev["UniqueID"])
                });
            }
            return list;
        }

        public static IDevice Create(DeviceInfo info)
        {
            switch (info.Kind)
            {
                case DeviceKind.Telescope: return new AlpacaTelescope(info);
                case DeviceKind.Camera: return new AlpacaCamera(info);
                case DeviceKind.Focuser: return new AlpacaFocuser(info);
                case DeviceKind.FilterWheel: return new AlpacaFilterWheel(info);
                case DeviceKind.Dome: return new AlpacaDome(info);
                case DeviceKind.SafetyMonitor: return new AlpacaSafety(info);
            }
            throw new NotSupportedException("Alpaca " + info.Kind + " is not supported yet");
        }
    }

    /// <summary>Alpaca REST client for one device ("host:port/number").</summary>
    public abstract class AlpacaBase : DeviceBase
    {
        private static int _clientId = new Random().Next(1, 65535);
        private static int _tx;
        protected string Base;

        protected AlpacaBase(DeviceInfo info)
        {
            Info = info;
            var parts = info.Address.Split('/');
            Base = "http://" + parts[0] + "/api/v1/" + info.Kind.ToString().ToLowerInvariant() + "/" + (parts.Length > 1 ? parts[1] : "0") + "/";
        }

        protected object Get(string member)
        {
            int tx = Interlocked.Increment(ref _tx);
            var text = Net.GetString(Base + member + "?ClientID=" + _clientId + "&ClientTransactionID=" + tx, 30000);
            return Check(text);
        }

        protected void Put(string member, params string[] kv)
        {
            int tx = Interlocked.Increment(ref _tx);
            var form = new Dictionary<string, string> { { "ClientID", _clientId.ToString() }, { "ClientTransactionID", tx.ToString() } };
            for (int i = 0; i + 1 < kv.Length; i += 2) form[kv[i]] = kv[i + 1];
            var req = (HttpWebRequest)WebRequest.Create(Base + member);
            req.Method = "PUT";
            req.ContentType = "application/x-www-form-urlencoded";
            var body = Encoding.UTF8.GetBytes(string.Join("&", form.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value))));
            req.ContentLength = body.Length;
            using (var s = req.GetRequestStream()) s.Write(body, 0, body.Length);
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var r = new StreamReader(resp.GetResponseStream())) Check(r.ReadToEnd());
        }

        private static object Check(string text)
        {
            var d = Json.ParseObject(text);
            object err;
            if (d.TryGetValue("ErrorNumber", out err) && Convert.ToInt32(err) != 0)
                throw new InvalidOperationException("Alpaca error " + err + ": " + d["ErrorMessage"]);
            object v;
            return d.TryGetValue("Value", out v) ? v : null;
        }

        protected double D(string m) => Convert.ToDouble(Get(m), CultureInfo.InvariantCulture);
        protected int I(string m) => Convert.ToInt32(Get(m), CultureInfo.InvariantCulture);
        protected bool B(string m) => Convert.ToBoolean(Get(m), CultureInfo.InvariantCulture);
        protected double TryD(string m, double f) { try { return D(m); } catch { return f; } }
        protected bool TryB(string m, bool f) { try { return B(m); } catch { return f; } }
        protected int TryI(string m, int f) { try { return I(m); } catch { return f; } }
        protected static string S(double v) => v.ToString("R", CultureInfo.InvariantCulture);

        public override void Connect() { Put("connected", "Connected", "true"); Connected = true; }
        public override void Disconnect() { try { Put("connected", "Connected", "false"); } catch { } Connected = false; }
        public override string Description { get { try { return Convert.ToString(Get("description")); } catch { return Info.Name; } } }
    }

    public class AlpacaTelescope : AlpacaBase, ITelescope
    {
        public AlpacaTelescope(DeviceInfo i) : base(i) { }
        public double RightAscension => D("rightascension");
        public double Declination => D("declination");
        public double Altitude => TryD("altitude", double.NaN);
        public double Azimuth => TryD("azimuth", double.NaN);
        public double SiderealTime => TryD("siderealtime", double.NaN);
        public bool Slewing => TryB("slewing", false);
        public bool Tracking { get { return TryB("tracking", false); } set { Put("tracking", "Tracking", value ? "true" : "false"); } }
        public TrackingRate TrackingRate { get { return (TrackingRate)TryI("trackingrate", 0); } set { Put("trackingrate", "TrackingRate", ((int)value).ToString()); } }
        public bool AtPark => TryB("atpark", false);
        public PierSide SideOfPier => (PierSide)TryI("sideofpier", -1);
        public bool CanPark => TryB("canpark", false);
        public bool CanSync => TryB("cansync", false);
        public bool CanPulseGuide => TryB("canpulseguide", false);
        public bool CanSlewAltAz => TryB("canslewaltazasync", false);
        public double SiteLatitude => TryD("sitelatitude", double.NaN);
        public double SiteLongitude => TryD("sitelongitude", double.NaN);
        public double[] AxisRates => new[] { 0.0083, 0.25, 1.0, 3.0 };
        public void SlewToCoordinatesAsync(double ra, double dec) { if (!Tracking) Tracking = true; Put("slewtocoordinatesasync", "RightAscension", S(ra), "Declination", S(dec)); }
        public void SlewToAltAzAsync(double az, double alt) => Put("slewtoaltazasync", "Azimuth", S(az), "Altitude", S(alt));
        public void SyncToCoordinates(double ra, double dec) => Put("synctocoordinates", "RightAscension", S(ra), "Declination", S(dec));
        public void AbortSlew() => Put("abortslew");
        public void Park() => Put("park");
        public void Unpark() => Put("unpark");
        public void SetPark() => Put("setpark");
        public void PulseGuide(GuideDirection dir, int ms) => Put("pulseguide", "Direction", ((int)dir).ToString(), "Duration", ms.ToString());
        public void MoveAxis(int axis, double rate) => Put("moveaxis", "Axis", axis.ToString(), "Rate", S(rate));
        public PierSide DestinationSideOfPier(double ra, double dec)
        {
            try { return (PierSide)Convert.ToInt32(Get("destinationsideofpier?RightAscension=" + S(ra) + "&Declination=" + S(dec) + "&dummy=")); } catch { return PierSide.Unknown; }
        }
    }

    public class AlpacaCamera : AlpacaBase, ICamera
    {
        public AlpacaCamera(DeviceInfo i) : base(i) { }
        public int SensorWidth => I("cameraxsize");
        public int SensorHeight => I("cameraysize");
        public double PixelSizeX => TryD("pixelsizex", 0);
        public double PixelSizeY => TryD("pixelsizey", 0);
        public int BinX { get { return I("binx"); } set { Put("binx", "BinX", value.ToString()); } }
        public int BinY { get { return I("biny"); } set { Put("biny", "BinY", value.ToString()); } }
        public int MaxBin => TryI("maxbinx", 1);
        public int StartX { get { return I("startx"); } set { Put("startx", "StartX", value.ToString()); } }
        public int StartY { get { return I("starty"); } set { Put("starty", "StartY", value.ToString()); } }
        public int NumX { get { return I("numx"); } set { Put("numx", "NumX", value.ToString()); } }
        public int NumY { get { return I("numy"); } set { Put("numy", "NumY", value.ToString()); } }
        public CameraState State => (CameraState)TryI("camerastate", 0);
        public bool ImageReady => TryB("imageready", false);
        public bool CanSetTemperature => TryB("cansetccdtemperature", false);
        public bool CoolerOn { get { return TryB("cooleron", false); } set { Put("cooleron", "CoolerOn", value ? "true" : "false"); } }
        public double CcdTemperature => TryD("ccdtemperature", double.NaN);
        public double SetTemperature { get { return TryD("setccdtemperature", double.NaN); } set { Put("setccdtemperature", "SetCCDTemperature", S(value)); } }
        public double CoolerPower => TryD("coolerpower", double.NaN);
        public bool HasGain { get { try { I("gain"); return true; } catch { return false; } } }
        public int Gain { get { return TryI("gain", 0); } set { Put("gain", "Gain", value.ToString()); } }
        public int GainMin => TryI("gainmin", 0);
        public int GainMax => TryI("gainmax", 0);
        public bool HasOffset { get { try { I("offset"); return true; } catch { return false; } } }
        public int Offset { get { return TryI("offset", 0); } set { Put("offset", "Offset", value.ToString()); } }
        public int BitDepth { get { int max = TryI("maxadu", 65535); int b = 1; while ((1L << b) - 1 < max && b < 32) b++; return b; } }
        public BayerPattern Bayer => TryI("sensortype", 0) == 2 ? Debayer.Shift(BayerPattern.RGGB, TryI("bayeroffsetx", 0), TryI("bayeroffsety", 0)) : BayerPattern.None;
        public double ElectronsPerAdu => TryD("electronsperadu", 1);
        public double PercentCompleted => TryD("percentcompleted", 0);
        public void StartExposure(double s, bool light) => Put("startexposure", "Duration", S(s), "Light", light ? "true" : "false");
        public void AbortExposure() => Put("abortexposure");

        /// <summary>Download via the binary ImageBytes protocol when available, otherwise JSON.</summary>
        public AstroImage GetImage()
        {
            int max = TryI("maxadu", 65535);
            var req = (HttpWebRequest)WebRequest.Create(Base + "imagearray?ClientID=1&ClientTransactionID=1");
            req.Accept = "application/imagebytes";
            req.Timeout = 120000;
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var s = resp.GetResponseStream())
            using (var ms = new MemoryStream())
            {
                s.CopyTo(ms);
                var b = ms.ToArray();
                AstroImage img;
                if ((resp.ContentType ?? "").Contains("imagebytes")) img = FromImageBytes(b, max);
                else
                {
                    var d = Json.ParseObject(Encoding.UTF8.GetString(b));
                    var rows = (List<object>)d["Value"];
                    int w = rows.Count, h = ((List<object>)rows[0]).Count;
                    img = new AstroImage(w, h, 1);
                    for (int x = 0; x < w; x++)
                    {
                        var col = (List<object>)rows[x];
                        for (int y = 0; y < h; y++) img.Data[0][y * w + x] = (float)(Convert.ToDouble(col[y], CultureInfo.InvariantCulture) / max);
                    }
                }
                img.Bayer = Bayer;
                img.ExposureSeconds = TryD("lastexposureduration", 0);
                img.Header.Set("INSTRUME", Info.Name);
                return img;
            }
        }

        /// <summary>Decode Alpaca ImageBytes (metadata version 1, 44-byte header).</summary>
        public static AstroImage FromImageBytes(byte[] b, int maxAdu)
        {
            int metaVer = BitConverter.ToInt32(b, 0), err = BitConverter.ToInt32(b, 4);
            if (err != 0) throw new InvalidOperationException("Alpaca camera error " + err);
            int dataStart = BitConverter.ToInt32(b, 16), elemType = BitConverter.ToInt32(b, 20), xmitType = BitConverter.ToInt32(b, 24);
            int rank = BitConverter.ToInt32(b, 28), d1 = BitConverter.ToInt32(b, 32), d2 = BitConverter.ToInt32(b, 36), d3 = BitConverter.ToInt32(b, 40);
            int planes = rank == 3 ? d3 : 1;
            var img = new AstroImage(d1, d2, planes);
            int size = xmitType == 1 || xmitType == 9 ? 2 : xmitType == 2 || xmitType == 4 ? 4 : xmitType == 6 ? 1 : xmitType == 3 ? 8 : 2;
            double s = 1.0 / Math.Max(1, maxAdu);
            int p = dataStart;
            for (int x = 0; x < d1; x++)
                for (int y = 0; y < d2; y++)
                    for (int c = 0; c < planes; c++)
                    {
                        double v;
                        switch (xmitType)
                        {
                            case 1: v = BitConverter.ToInt16(b, p); break;
                            case 8: v = BitConverter.ToUInt16(b, p); break;
                            case 2: v = BitConverter.ToInt32(b, p); break;
                            case 6: v = b[p]; break;
                            case 4: v = BitConverter.ToSingle(b, p); break;
                            case 3: v = BitConverter.ToDouble(b, p); break;
                            default: v = BitConverter.ToUInt16(b, p); break;
                        }
                        if (xmitType == 1 && elemType == 2) v += 32768; // int16 transmission of uint16 data
                        img.Data[c][y * d1 + x] = (float)(v * s);
                        p += size;
                    }
            return img;
        }
    }

    public class AlpacaFocuser : AlpacaBase, IFocuser
    {
        public AlpacaFocuser(DeviceInfo i) : base(i) { }
        public int Position => TryI("position", 0);
        public int MaxStep => TryI("maxstep", 100000);
        public bool IsMoving => TryB("ismoving", false);
        public bool Absolute => TryB("absolute", true);
        public double Temperature => TryD("temperature", double.NaN);
        public double StepSizeMicrons => TryD("stepsize", double.NaN);
        public void Move(int p) => Put("move", "Position", p.ToString());
        public void Halt() => Put("halt");
    }

    public class AlpacaFilterWheel : AlpacaBase, IFilterWheel
    {
        public AlpacaFilterWheel(DeviceInfo i) : base(i) { }
        public int Position { get { return TryI("position", -1); } set { Put("position", "Position", value.ToString()); } }
        public string[] Names { get { try { return ((List<object>)Get("names")).Select(o => o.ToString()).ToArray(); } catch { return new string[0]; } } }
        public int[] FocusOffsets { get { try { return ((List<object>)Get("focusoffsets")).Select(Convert.ToInt32).ToArray(); } catch { return new int[0]; } } }
        public bool IsMoving => Position < 0;
    }

    public class AlpacaDome : AlpacaBase, IDome
    {
        public AlpacaDome(DeviceInfo i) : base(i) { }
        public double Azimuth => TryD("azimuth", double.NaN);
        public ShutterState Shutter => (ShutterState)TryI("shutterstatus", 5);
        public bool Slewing => TryB("slewing", false);
        public bool AtPark => TryB("atpark", false);
        public bool CanSetAzimuth => TryB("cansetazimuth", false);
        public bool Slaved { get { return TryB("slaved", false); } set { Put("slaved", "Slaved", value ? "true" : "false"); } }
        public void SlewToAzimuth(double az) => Put("slewtoazimuth", "Azimuth", S(az));
        public void OpenShutter() => Put("openshutter");
        public void CloseShutter() => Put("closeshutter");
        public void Park() => Put("park");
        public void FindHome() => Put("findhome");
        public void AbortSlew() => Put("abortslew");
    }

    public class AlpacaSafety : AlpacaBase, ISafetyMonitor
    {
        public AlpacaSafety(DeviceInfo i) : base(i) { }
        public bool IsSafe => TryB("issafe", false);
    }
}
