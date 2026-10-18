using System;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Sidralune.Core.Mathematics;

namespace Sidralune.Devices.Serial
{
    /// <summary>
    /// Meade LX200 command set (also OnStep, Celestron in LX200 mode, iOptron/Skywatcher via adapters). Address is a
    /// serial port ("COM3[:9600]") or TCP endpoint ("192.168.0.1:9999") for WiFi controllers.
    /// </summary>
    public class Lx200Telescope : DeviceBase, ITelescope
    {
        private SerialPort _port;
        private TcpClient _tcp;
        private Stream _stream;
        private readonly object _lock = new object();
        private bool _highPrecision;
        private bool _tracking = true;
        public bool IsOnStep { get; private set; }

        public Lx200Telescope(DeviceInfo info) { Info = info; }

        public override void Connect()
        {
            string a = Info.Address;
            if (a.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
            {
                var p = a.Split(':');
                _port = new SerialPort(p[0], p.Length > 1 ? int.Parse(p[1]) : 9600, Parity.None, 8, StopBits.One) { ReadTimeout = 2000, WriteTimeout = 2000, NewLine = "#" };
                _port.Open();
                _stream = _port.BaseStream;
            }
            else
            {
                var p = a.Split(':');
                _tcp = new TcpClient(p[0], p.Length > 1 ? int.Parse(p[1]) : 9999);
                _tcp.ReceiveTimeout = 2000;
                _stream = _tcp.GetStream();
            }
            // ensure high precision coordinates
            string ra = Query(":GR#");
            if (ra.Length < 8) { Command(":U#"); _highPrecision = Query(":GR#").Length >= 8; } else _highPrecision = true;
            try { IsOnStep = Query(":GVP#").IndexOf("On-Step", StringComparison.OrdinalIgnoreCase) >= 0; } catch { }
            Connected = true;
        }

        public override void Disconnect()
        {
            _stream?.Dispose(); _port?.Dispose(); _tcp?.Close();
            _stream = null; _port = null; _tcp = null;
            Connected = false;
        }

        private void Write(string s) { var b = Encoding.ASCII.GetBytes(s); _stream.Write(b, 0, b.Length); _stream.Flush(); }

        private string ReadUntilHash(int timeoutMs = 2000)
        {
            var sb = new StringBuilder();
            var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            var buf = new byte[1];
            while (DateTime.UtcNow < until)
            {
                int n;
                try { n = _stream.Read(buf, 0, 1); } catch (IOException) { break; } catch (TimeoutException) { break; }
                if (n <= 0) continue;
                char c = (char)buf[0];
                if (c == '#') return sb.ToString();
                sb.Append(c);
            }
            return sb.ToString();
        }

        private char ReadChar(int timeoutMs = 2000)
        {
            var buf = new byte[1];
            var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < until)
            {
                try { if (_stream.Read(buf, 0, 1) == 1) return (char)buf[0]; } catch { break; }
            }
            return '\0';
        }

        public string Query(string cmd) { lock (_lock) { Write(cmd); return ReadUntilHash(); } }
        public void Command(string cmd) { lock (_lock) Write(cmd); }
        public char CommandChar(string cmd) { lock (_lock) { Write(cmd); return ReadChar(); } }

        private static double ParseRa(string s) => AstroMath.ParseSexagesimal(s.Replace('*', ':').Replace('\'', ':'));
        private static double ParseDec(string s) => AstroMath.ParseSexagesimal(s.Replace('*', ':').Replace('\'', ':').Replace('ß', ':').Replace((char)223, ':'));

        public double RightAscension => ParseRa(Query(":GR#"));
        public double Declination => ParseDec(Query(":GD#"));
        public double Altitude => ParseDec(Query(":GA#"));
        public double Azimuth => ParseDec(Query(":GZ#"));
        public double SiderealTime => ParseRa(Query(":GS#"));
        public bool Slewing
        {
            get
            {
                if (IsOnStep) { var s = Query(":GU#"); return s.IndexOf('N') < 0; }
                var d = Query(":D#");
                return d.Trim().Length > 0;
            }
        }
        public bool Tracking
        {
            get { if (IsOnStep) return Query(":GU#").IndexOf('n') < 0; return _tracking; }
            set { _tracking = value; if (IsOnStep) CommandChar(value ? ":Te#" : ":Td#"); }
        }
        public TrackingRate TrackingRate { get; set; }
        public bool AtPark => IsOnStep && Query(":GU#").IndexOf('P') >= 0;
        public PierSide SideOfPier
        {
            get
            {
                if (!IsOnStep) return PierSide.Unknown;
                var s = Query(":Gm#");
                return s.StartsWith("W") ? PierSide.West : s.StartsWith("E") ? PierSide.East : PierSide.Unknown;
            }
        }
        public bool CanPark => IsOnStep;
        public bool CanSync => true;
        public bool CanPulseGuide => true;
        public bool CanSlewAltAz => true;
        public double SiteLatitude => ParseDec(Query(":Gt#"));
        public double SiteLongitude => -ParseDec(Query(":Gg#")); // LX200 longitude is west positive
        public double[] AxisRates => new[] { 0.0042, 0.0167, 0.25, 1.5 };

        private string RaStr(double h)
        {
            h = AstroMath.NormalizeHours(h);
            int hh = (int)h; double m = (h - hh) * 60; int mm = (int)m; int ss = (int)Math.Round((m - mm) * 60);
            if (ss == 60) { ss = 0; mm++; } if (mm == 60) { mm = 0; hh = (hh + 1) % 24; }
            return _highPrecision ? string.Format("{0:00}:{1:00}:{2:00}", hh, mm, ss) : string.Format("{0:00}:{1:00.0}", hh, m);
        }

        private string DecStr(double d)
        {
            string sign = d < 0 ? "-" : "+";
            d = Math.Abs(d);
            int dd = (int)d; double m = (d - dd) * 60; int mm = (int)m; int ss = (int)Math.Round((m - mm) * 60);
            if (ss == 60) { ss = 0; mm++; } if (mm == 60) { mm = 0; dd++; }
            return _highPrecision ? string.Format("{0}{1:00}*{2:00}:{3:00}", sign, dd, mm, ss) : string.Format("{0}{1:00}*{2:00}", sign, dd, mm);
        }

        public void SlewToCoordinatesAsync(double ra, double dec)
        {
            if (CommandChar(":Sr" + RaStr(ra) + "#") != '1') throw new InvalidOperationException("Mount rejected RA");
            if (CommandChar(":Sd" + DecStr(dec) + "#") != '1') throw new InvalidOperationException("Mount rejected Dec");
            char r = CommandChar(":MS#");
            if (r != '0') { ReadUntilHash(500); throw new InvalidOperationException("Slew rejected (object below horizon or limits), code " + r); }
            RaiseStateChanged();
        }

        public void SlewToAltAzAsync(double az, double alt)
        {
            CommandChar(":Sz" + string.Format(CultureInfo.InvariantCulture, "{0:000}*{1:00}", (int)az, (int)((az - (int)az) * 60)) + "#");
            CommandChar(":Sa" + DecStr(alt) + "#");
            CommandChar(":MA#");
        }

        public void SyncToCoordinates(double ra, double dec)
        {
            CommandChar(":Sr" + RaStr(ra) + "#");
            CommandChar(":Sd" + DecStr(dec) + "#");
            Query(":CM#");
        }

        public void AbortSlew() => Command(":Q#");
        public void Park() { if (IsOnStep) CommandChar(":hP#"); else Command(":hP#"); }
        public void Unpark() { if (IsOnStep) CommandChar(":hR#"); }
        public void SetPark() { if (IsOnStep) CommandChar(":hQ#"); }
        public void PulseGuide(GuideDirection dir, int ms)
        {
            string d = dir == GuideDirection.North ? "n" : dir == GuideDirection.South ? "s" : dir == GuideDirection.East ? "e" : "w";
            Command(":Mg" + d + ms.ToString("0000") + "#");
        }

        public void MoveAxis(int axis, double rate)
        {
            if (rate == 0) { Command(axis == 0 ? ":Qe#:Qw#" : ":Qn#:Qs#"); return; }
            double a = Math.Abs(rate);
            Command(a >= 1 ? ":RS#" : a >= 0.2 ? ":RM#" : a >= 0.01 ? ":RC#" : ":RG#");
            Command(axis == 0 ? (rate > 0 ? ":Mw#" : ":Me#") : (rate > 0 ? ":Mn#" : ":Ms#"));
        }

        public PierSide DestinationSideOfPier(double ra, double dec) => PierSide.Unknown;

        public static string[] SerialPorts() => SerialPort.GetPortNames();
    }
}
