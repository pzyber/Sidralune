using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Sidralune.Core.Util;
using Sidralune.Devices;
using Sidralune.Devices.Alpaca;
using Sidralune.Devices.Ascom;
using Sidralune.Devices.Guiding;
using Sidralune.Devices.Indi;
using Sidralune.Devices.Serial;
using Sidralune.Devices.Simulators;
using Sidralune.Imaging;
using Sidralune.Imaging.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Sidralune.Tests.Checks;

namespace Sidralune.Tests
{
    /// <summary>
    /// Device protocols against fake servers on localhost (LX200/OnStep over TCP, PHD2 JSON-RPC, INDI XML, ASCOM Alpaca
    /// REST and discovery), ASCOM COM wrappers with stand-in drivers, and the device manager.
    /// </summary>
    [TestClass]
    public class DeviceProtocolTests : TestBase
    {
        private static TcpListener Listen(out int port, int from = 0, int to = 0)
        {
            for (int p = from; p <= to; p++)
            {
                try
                {
                    var l = new TcpListener(IPAddress.Loopback, p);
                    l.Start();
                    port = ((IPEndPoint)l.LocalEndpoint).Port;
                    return l;
                }
                catch (SocketException) { if (from == 0) throw; }
            }
            throw new InvalidOperationException("No free port");
        }

        private static bool WaitFor(Func<bool> cond, int ms = 3000)
        {
            var until = DateTime.UtcNow.AddMilliseconds(ms);
            while (DateTime.UtcNow < until) { if (cond()) return true; Thread.Sleep(20); }
            return cond();
        }

        // ------------------------------------------------------------------ LX200 / OnStep

        [TestMethod]
        public void TestLx200OnStep()
        {
            int port;
            var listener = Listen(out port);
            var log = new List<string>();
            bool high = false, slewing = false, parked = false;
            var server = new Thread(() =>
            {
                using (var c = listener.AcceptTcpClient())
                using (var ns = c.GetStream())
                {
                    var cmd = new StringBuilder();
                    var buf = new byte[1];
                    Action<string> reply = r => { var b = Encoding.ASCII.GetBytes(r); ns.Write(b, 0, b.Length); };
                    while (true)
                    {
                        int n;
                        try { n = ns.Read(buf, 0, 1); } catch { break; }
                        if (n <= 0) break;
                        cmd.Append((char)buf[0]);
                        if (buf[0] != '#') continue;
                        string s = cmd.ToString(); cmd.Clear();
                        lock (log) log.Add(s);
                        if (s == ":GR#") reply(high ? "05:34:32#" : "05:34.5#");
                        else if (s == ":U#") high = !high;
                        else if (s == ":GD#") reply("+22*00:52#");
                        else if (s == ":GVP#") reply("On-Step#");
                        else if (s == ":GU#") { reply((slewing ? "" : "N") + (parked ? "nP" : "") + "#"); slewing = false; }
                        else if (s == ":Gm#") reply("W#");
                        else if (s == ":Gt#") reply("+52*00#");
                        else if (s == ":Gg#") reply("-005*30#");
                        else if (s.StartsWith(":Sr") || s.StartsWith(":Sd")) reply("1");
                        else if (s == ":MS#") { if (log.Any(l => l.StartsWith(":Sd-8"))) reply("1Object below horizon#"); else { reply("0"); slewing = true; } }
                        else if (s == ":CM#") reply("N/A#");
                        else if (s == ":hP#") { parked = true; reply("1"); }
                        else if (s == ":hR#") { parked = false; reply("1"); }
                        else if (s == ":Te#" || s == ":Td#") reply("1");
                    }
                }
            }) { IsBackground = true };
            server.Start();
            var mount = new Lx200Telescope(new DeviceInfo { Kind = DeviceKind.Telescope, Protocol = DeviceProtocol.Lx200, Address = "127.0.0.1:" + port });
            try
            {
                mount.Connect();
                CheckTrue("Switches to high precision and recognises OnStep", mount.Connected && mount.IsOnStep && log.Contains(":U#"));
                Check("Right ascension (h)", mount.RightAscension, 5 + 34 / 60.0 + 32 / 3600.0, 1e-9);
                Check("Declination (deg)", mount.Declination, 22 + 52 / 3600.0, 1e-9);
                Check("Site latitude", mount.SiteLatitude, 52, 1e-9);
                Check("Site longitude (east positive)", mount.SiteLongitude, 5.5, 1e-9);
                CheckTrue("Status: tracking, not slewing, not parked, west of the pier", mount.Tracking && !mount.Slewing && !mount.AtPark && mount.SideOfPier == PierSide.West);
                mount.SlewToCoordinatesAsync(6.5, -10.25);
                CheckTrue("Slew commands in high precision", log.Contains(":Sr06:30:00#") && log.Contains(":Sd-10*15:00#") && log.Contains(":MS#"), string.Join(" ", log.Where(l => l.StartsWith(":S"))));
                CheckTrue("Slewing reported after a goto", mount.Slewing);
                bool rejected = false;
                try { mount.SlewToCoordinatesAsync(1, -85); } catch (InvalidOperationException) { rejected = true; }
                CheckTrue("Goto below the horizon rejected", rejected);
                mount.SyncToCoordinates(23.999999, 0);
                CheckTrue("Sync rounds 23:59:59.996 up to 00:00:00", log.Contains(":Sr00:00:00#") && log.Contains(":CM#"));
                mount.Park();
                CheckTrue("Park", log.Contains(":hP#") && mount.AtPark);
                mount.Unpark();
                mount.PulseGuide(GuideDirection.North, 500);
                mount.MoveAxis(0, 1.5);
                mount.MoveAxis(1, -0.1);
                mount.MoveAxis(0, 0);
                mount.Tracking = false;
                Thread.Sleep(200);
                CheckTrue("Guide pulse, axis moves, stop and tracking off", log.Contains(":Mgn0500#") && log.Contains(":RS#") && log.Contains(":Mw#") && log.Contains(":RC#") && log.Contains(":Ms#") && log.Contains(":Qe#") && log.Contains(":Qw#") && log.Contains(":Td#"));
            }
            finally { mount.Disconnect(); listener.Stop(); }
            CheckTrue("Disconnected", !mount.Connected);
        }

        // ------------------------------------------------------------------ PHD2

        private class FakePhd2
        {
            public readonly TcpListener Listener;
            public readonly int Port;
            public readonly List<string> Methods = new List<string>();
            public string Mode = "normal"; // normal, drop (close on guide), silent (never answer guide)

            public FakePhd2()
            {
                Listener = Listen(out Port, 4420, 4499);
                new Thread(Serve) { IsBackground = true }.Start();
            }

            private void Serve()
            {
                try
                {
                    using (var c = Listener.AcceptTcpClient())
                    using (var ns = c.GetStream())
                    using (var r = new StreamReader(ns))
                    using (var w = new StreamWriter(ns) { AutoFlush = true, NewLine = "\r\n" })
                    {
                        w.WriteLine("{\"Event\":\"Version\",\"PHDVersion\":\"2.6.13\",\"MsgVersion\":1}");
                        w.WriteLine("{\"Event\":\"AppState\",\"State\":\"Stopped\"}");
                        string line;
                        while ((line = r.ReadLine()) != null)
                        {
                            var m = Json.ParseObject(line);
                            string method = (string)m["method"];
                            long id = Convert.ToInt64(m["id"]);
                            lock (Methods) Methods.Add(method);
                            Action<string> result = v => w.WriteLine("{\"jsonrpc\":\"2.0\",\"result\":" + v + ",\"id\":" + id + "}");
                            switch (method)
                            {
                                case "get_pixel_scale": result("1.5"); break;
                                case "guide":
                                    if (Mode == "drop") return;
                                    if (Mode == "silent") break;
                                    result("0");
                                    w.WriteLine("{\"Event\":\"StartGuiding\"}");
                                    foreach (var d in new[] { 0.2, -0.2, 0.2 })
                                        w.WriteLine("{\"Event\":\"GuideStep\",\"RADistanceRaw\":" + d.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"DECDistanceRaw\":0.1,\"RADuration\":120,\"DECDuration\":0,\"SNR\":35.2,\"StarMass\":10000}");
                                    w.WriteLine("{\"Event\":\"SettleDone\",\"Status\":0}");
                                    break;
                                case "dither": result("0"); w.WriteLine("{\"Event\":\"SettleDone\",\"Status\":0}"); break;
                                case "stop_capture": result("0"); w.WriteLine("{\"Event\":\"GuidingStopped\"}"); break;
                                case "set_paused": result("0"); w.WriteLine("{\"Event\":\"Paused\"}"); break;
                                default: w.WriteLine("{\"jsonrpc\":\"2.0\",\"error\":{\"code\":1,\"message\":\"unknown method\"},\"id\":" + id + "}"); break;
                            }
                        }
                    }
                }
                catch { }
                finally { Listener.Stop(); }
            }
        }

        [TestMethod]
        public void TestPhd2()
        {
            var fake = new FakePhd2();
            var phd = (Phd2Client)new DeviceManager().Create(new DeviceInfo { Kind = DeviceKind.Guider, Protocol = DeviceProtocol.Phd2, Address = "127.0.0.1:" + fake.Port });
            CheckTrue("PHD2 address maps to the instance number", phd.Host == "127.0.0.1" && phd.Instance == fake.Port - 4399);
            phd.Connect();
            try
            {
                CheckTrue("Pixel scale read after connecting", WaitFor(() => phd.PixelScale == 1.5), phd.PixelScale.ToString());
                CheckTrue("Application state from events", WaitFor(() => phd.State == "Stopped"));
                int steps = 0;
                phd.Step += s => Interlocked.Increment(ref steps);
                var start = phd.StartGuiding(false, CancellationToken.None);
                CheckTrue("Start guiding completes after settling", start.Wait(3000) && !start.IsFaulted);
                CheckTrue("Guiding with three guide steps", phd.IsGuiding && WaitFor(() => steps == 3) && phd.History.Count == 3);
                Check("RA RMS in arcsec (0.2 px × 1.5″/px)", phd.RmsRaArcsec, 0.3, 1e-9);
                Check("Total RMS", phd.RmsTotalArcsec, Math.Sqrt(0.09 + 0.0225), 1e-9);
                var dither = phd.Dither(5, false, 1.5, 10, 60, CancellationToken.None);
                CheckTrue("Dither settles", dither.Wait(3000) && !dither.IsFaulted);
                var bogus = phd.Call("no_such_method");
                bool err = false;
                try { bogus.Wait(3000); } catch (AggregateException ex) { err = ex.InnerException.Message.Contains("unknown method"); }
                CheckTrue("JSON-RPC error reported", err);
                phd.Pause(true);
                CheckTrue("Paused", WaitFor(() => phd.State == "Paused"));
                phd.StopGuiding();
                CheckTrue("Stopped", WaitFor(() => phd.State == "Stopped") && !phd.IsGuiding);
                lock (fake.Methods) CheckTrue("Methods sent", fake.Methods.SequenceEqual(new[] { "get_pixel_scale", "guide", "dither", "no_such_method", "set_paused", "stop_capture" }), string.Join(",", fake.Methods));
            }
            finally { phd.Disconnect(); }

            // PHD2 closing the connection while starting to guide must not leave the caller waiting forever
            var dropping = new FakePhd2 { Mode = "drop" };
            var p2 = new Phd2Client { Host = "127.0.0.1", Instance = dropping.Port - 4399 };
            p2.Connect();
            var t2 = p2.StartGuiding(false, CancellationToken.None);
            bool finished = false;
            try { finished = t2.Wait(5000); } catch (AggregateException) { finished = true; }
            CheckTrue("Dropped connection fails the start instead of hanging", finished && t2.IsFaulted);
            CheckTrue("Calls after the drop are refused", WaitFor(() => !p2.Connected));
            bool refused = false;
            try { p2.Call("stop_capture"); } catch (InvalidOperationException) { refused = true; }
            CheckTrue("Not connected", refused);

            // PHD2 that never answers: cancellation ends the wait
            var silent = new FakePhd2 { Mode = "silent" };
            var p3 = new Phd2Client { Host = "127.0.0.1", Instance = silent.Port - 4399 };
            p3.Connect();
            using (var cts = new CancellationTokenSource(500))
            {
                var t3 = p3.StartGuiding(false, cts.Token);
                bool done = false;
                try { done = t3.Wait(5000); } catch (AggregateException) { done = true; }
                CheckTrue("Cancellation ends a start that PHD2 never answers", done && t3.IsCanceled);
            }
            p3.Disconnect();
        }

        // ------------------------------------------------------------------ INDI

        private class FakeIndi
        {
            public readonly int Port;
            private readonly TcpListener _l;
            public readonly List<string> Received = new List<string>();
            public double Ra = 5 + 34 / 60.0 + 32 / 3600.0, Dec = 22.0144;
            public int Focus = 1000;
            public byte[] Fits;

            public FakeIndi()
            {
                _l = Listen(out Port);
                new Thread(() => { try { while (true) { var c = _l.AcceptTcpClient(); new Thread(() => Serve(c)) { IsBackground = true }.Start(); } } catch { } }) { IsBackground = true }.Start();
            }

            public void Stop() => _l.Stop();

            /// <summary>Run later on a thread of its own (the thread pool may be busy with the other tests' work).</summary>
            private static void Later(int ms, Action a) => new Thread(() => { Thread.Sleep(ms); try { a(); } catch { } }) { IsBackground = true }.Start();

            private static string Num(double v) => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

            private void Serve(TcpClient c)
            {
                using (c)
                using (var ns = c.GetStream())
                {
                    Action<string> send = x => { var b = Encoding.UTF8.GetBytes(x); lock (ns) ns.Write(b, 0, b.Length); };
                    var sb = new StringBuilder();
                    var buf = new byte[8192];
                    var rx = new Regex(@"<(new\w+Vector|enableBLOB)\b([^>]*)>(.*?)</\1>|<getProperties[^>]*/>", RegexOptions.Singleline);
                    try
                    {
                        while (true)
                        {
                            int n = ns.Read(buf, 0, buf.Length);
                            if (n <= 0) break;
                            sb.Append(Encoding.UTF8.GetString(buf, 0, n));
                            Match m;
                            while ((m = rx.Match(sb.ToString())).Success)
                            {
                                sb.Remove(0, m.Index + m.Length);
                                lock (Received) Received.Add(m.Value);
                                if (m.Value.StartsWith("<getProperties")) { SendDefinitions(send); continue; }
                                string attrs = m.Groups[2].Value, body = m.Groups[3].Value;
                                string dev = Regex.Match(attrs, "device='([^']*)'").Groups[1].Value, name = Regex.Match(attrs, "name='([^']*)'").Groups[1].Value;
                                Func<string, double> el = e => double.Parse(Regex.Match(body, "name='" + e + "'>([^<]*)<").Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                                if (name == "CONNECTION" && dev == "Broken")
                                    send("<setSwitchVector device='Broken' name='CONNECTION' state='Alert'><oneSwitch name='CONNECT'>Off</oneSwitch><oneSwitch name='DISCONNECT'>On</oneSwitch></setSwitchVector>");
                                else if (name == "CONNECTION")
                                    send("<setSwitchVector device='" + dev + "' name='CONNECTION' state='Ok'><oneSwitch name='CONNECT'>On</oneSwitch><oneSwitch name='DISCONNECT'>Off</oneSwitch></setSwitchVector>");
                                else if (name == "EQUATORIAL_EOD_COORD")
                                {
                                    send("<setNumberVector device='Mount' name='EQUATORIAL_EOD_COORD' state='Busy'><oneNumber name='RA'>" + Num(Ra) + "</oneNumber><oneNumber name='DEC'>" + Num(Dec) + "</oneNumber></setNumberVector>");
                                    Ra = el("RA"); Dec = el("DEC");
                                    var ra = Ra; var de = Dec;
                                    Later(300, () => send("<setNumberVector device='Mount' name='EQUATORIAL_EOD_COORD' state='Ok'><oneNumber name='RA'>" + Num(ra) + "</oneNumber><oneNumber name='DEC'>" + Num(de) + "</oneNumber></setNumberVector>"));
                                }
                                else if (name == "ABS_FOCUS_POSITION")
                                {
                                    send("<setNumberVector device='Foc' name='ABS_FOCUS_POSITION' state='Busy'><oneNumber name='FOCUS_ABSOLUTE_POSITION'>" + Focus + "</oneNumber></setNumberVector>");
                                    Focus = (int)el("FOCUS_ABSOLUTE_POSITION");
                                    int f = Focus;
                                    Later(300, () => send("<setNumberVector device='Foc' name='ABS_FOCUS_POSITION' state='Ok'><oneNumber name='FOCUS_ABSOLUTE_POSITION'>" + f + "</oneNumber></setNumberVector>"));
                                }
                                else if (name == "CCD_EXPOSURE")
                                {
                                    send("<setNumberVector device='Cam' name='CCD_EXPOSURE' state='Busy'><oneNumber name='CCD_EXPOSURE_VALUE'>" + Num(el("CCD_EXPOSURE_VALUE")) + "</oneNumber></setNumberVector>");
                                    Later(200, () =>
                                    {
                                        send("<setBLOBVector device='Cam' name='CCD1' state='Ok'><oneBLOB name='CCD1' size='" + Fits.Length + "' format='.fits'>\n" + Convert.ToBase64String(Fits, Base64FormattingOptions.InsertLineBreaks) + "\n</oneBLOB></setBLOBVector>");
                                        send("<setNumberVector device='Cam' name='CCD_EXPOSURE' state='Ok'><oneNumber name='CCD_EXPOSURE_VALUE'>0</oneNumber></setNumberVector>");
                                    });
                                }
                            }
                        }
                    }
                    catch { }
                }
            }

            private void SendDefinitions(Action<string> send)
            {
                foreach (var dev in new[] { "Mount", "Cam", "Foc", "Broken" })
                    send("<defSwitchVector device='" + dev + "' name='CONNECTION' label='Connection' group='Main Control' state='Idle' perm='rw' rule='OneOfMany'><defSwitch name='CONNECT' label='Connect'>Off</defSwitch><defSwitch name='DISCONNECT' label='Disconnect'>On</defSwitch></defSwitchVector>");
                send("<defNumberVector device='Mount' name='EQUATORIAL_EOD_COORD' label='Eq. Coordinates' group='Main Control' state='Idle' perm='rw'><defNumber name='RA' format='%010.6m'>5:34:32</defNumber><defNumber name='DEC' format='%010.6m'>22 00 52</defNumber></defNumberVector>");
                send("<defSwitchVector device='Mount' name='TELESCOPE_TRACK_STATE' state='Ok' perm='rw' rule='OneOfMany'><defSwitch name='TRACK_ON'>On</defSwitch><defSwitch name='TRACK_OFF'>Off</defSwitch></defSwitchVector>");
                send("<defSwitchVector device='Mount' name='TELESCOPE_PIER_SIDE' state='Ok' perm='ro' rule='AtMostOne'><defSwitch name='PIER_WEST'>On</defSwitch><defSwitch name='PIER_EAST'>Off</defSwitch></defSwitchVector>");
                send("<defSwitchVector device='Mount' name='TELESCOPE_PARK' state='Ok' perm='rw' rule='OneOfMany'><defSwitch name='PARK'>Off</defSwitch><defSwitch name='UNPARK'>On</defSwitch></defSwitchVector>");
                send("<defNumberVector device='Mount' name='GEOGRAPHIC_COORD' state='Ok' perm='rw'><defNumber name='LAT'>52</defNumber><defNumber name='LONG'>5.5</defNumber><defNumber name='ELEV'>10</defNumber></defNumberVector>");
                send("<message device='Mount' timestamp='2026-10-24T20:00:00' message='Mount ready'/>");
                send("<defNumberVector device='Cam' name='CCD_INFO' state='Ok' perm='ro'><defNumber name='CCD_MAX_X'>32</defNumber><defNumber name='CCD_MAX_Y'>24</defNumber><defNumber name='CCD_PIXEL_SIZE_X'>3.76</defNumber><defNumber name='CCD_PIXEL_SIZE_Y'>3.76</defNumber><defNumber name='CCD_BITSPERPIXEL'>16</defNumber></defNumberVector>");
                send("<defNumberVector device='Cam' name='CCD_EXPOSURE' state='Idle' perm='rw'><defNumber name='CCD_EXPOSURE_VALUE'>1</defNumber></defNumberVector>");
                send("<defNumberVector device='Cam' name='CCD_TEMPERATURE' state='Ok' perm='rw'><defNumber name='CCD_TEMPERATURE_VALUE'>-10.5</defNumber></defNumberVector>");
                send("<defTextVector device='Cam' name='CCD_CFA' state='Ok' perm='ro'><defText name='CFA_OFFSET_X'>0</defText><defText name='CFA_OFFSET_Y'>0</defText><defText name='CFA_TYPE'>RGGB</defText></defTextVector>");
                send("<defBLOBVector device='Cam' name='CCD1' state='Idle' perm='ro'><defBLOB name='CCD1'/></defBLOBVector>");
                send("<defNumberVector device='Foc' name='ABS_FOCUS_POSITION' state='Ok' perm='rw'><defNumber name='FOCUS_ABSOLUTE_POSITION'>" + Focus + "</defNumber></defNumberVector>");
                send("<defNumberVector device='Foc' name='FOCUS_MAX' state='Ok' perm='rw'><defNumber name='FOCUS_MAX_VALUE'>50000</defNumber></defNumberVector>");
                send("<defNumberVector device='Foc' name='FOCUS_TEMPERATURE' state='Ok' perm='ro'><defNumber name='TEMPERATURE'>7.25</defNumber></defNumberVector>");
            }
        }

        [TestMethod]
        public void TestIndi()
        {
            Check("INDI sexagesimal number", IndiProperty.ParseIndiNumber("-12:30:36"), -12.51, 1e-9);
            Check("INDI number with spaces", IndiProperty.ParseIndiNumber("22 00 52"), 22.014444444, 1e-6);
            Check("INDI plain number", IndiProperty.ParseIndiNumber(" 1.5e2 "), 150, 0);
            CheckTrue("INDI bad number", double.IsNaN(IndiProperty.ParseIndiNumber("abc")));

            var fake = new FakeIndi();
            var img = new AstroImage(32, 24, 1);
            for (int i = 0; i < img.PixelCount; i++) img.Data[0][i] = (i % 50) / 65535f * 1000;
            using (var ms = new MemoryStream()) { FitsIO.Write(ms, img, 16); fake.Fits = ms.ToArray(); }
            Func<DeviceKind, string, IDevice> make = (k, dev) => IndiClient.Create(new DeviceInfo { Kind = k, Protocol = DeviceProtocol.Indi, Address = "127.0.0.1:" + fake.Port + "/" + dev });
            var mount = (IndiTelescope)make(DeviceKind.Telescope, "Mount");
            var cam = (IndiCamera)make(DeviceKind.Camera, "Cam");
            var foc = (IndiFocuser)make(DeviceKind.Focuser, "Foc");
            try
            {
                mount.Connect(); cam.Connect(); foc.Connect();
                CheckTrue("INDI devices connected", mount.Connected && cam.Connected && foc.Connected);
                lock (fake.Received) CheckTrue("CONNECT switch sent", fake.Received.Count(r => r.Contains("name='CONNECTION'") && r.Contains("<oneSwitch name='CONNECT'>On")) == 3);
                Check("RA from sexagesimal", mount.RightAscension, 5 + 34 / 60.0 + 32 / 3600.0, 1e-9);
                Check("Dec from sexagesimal with spaces", mount.Declination, 22 + 52 / 3600.0, 1e-6);
                CheckTrue("Tracking, pier side, park state and site", mount.Tracking && mount.SideOfPier == PierSide.West && !mount.AtPark && mount.CanPark && mount.SiteLatitude == 52 && mount.SiteLongitude == 5.5);
                mount.SlewToCoordinatesAsync(10.25, -5.5);
                CheckTrue("Goto shows as slewing (Busy)", WaitFor(() => mount.Slewing, 2000));
                CheckTrue("Goto finishes at the target", WaitFor(() => !mount.Slewing && Math.Abs(mount.RightAscension - 10.25) < 1e-9 && Math.Abs(mount.Declination + 5.5) < 1e-9, 3000));
                mount.Tracking = false;
                mount.PulseGuide(GuideDirection.East, 250);
                mount.MoveAxis(1, -1);
                Thread.Sleep(200);
                lock (fake.Received)
                    CheckTrue("TRACK, tracking off, guide and motion commands", fake.Received.Any(r => r.Contains("ON_COORD_SET") && r.Contains("<oneSwitch name='TRACK'>On")) && fake.Received.Any(r => r.Contains("TELESCOPE_TRACK_STATE") && r.Contains("<oneSwitch name='TRACK_OFF'>On"))
                        && fake.Received.Any(r => r.Contains("TELESCOPE_TIMED_GUIDE_WE") && r.Contains("<oneNumber name='TIMED_GUIDE_E'>250")) && fake.Received.Any(r => r.Contains("TELESCOPE_MOTION_NS") && r.Contains("<oneSwitch name='MOTION_SOUTH'>On")));
                CheckTrue("Camera properties", cam.SensorWidth == 32 && cam.SensorHeight == 24 && Math.Abs(cam.PixelSizeX - 3.76) < 1e-9 && cam.BitDepth == 16 && cam.CanSetTemperature && cam.CcdTemperature == -10.5 && cam.Bayer == BayerPattern.RGGB);
                cam.StartExposure(0.5, true);
                CheckTrue("Exposure delivered as a FITS BLOB", WaitFor(() => cam.ImageReady, 10000));
                var got = cam.GetImage();
                CheckTrue("BLOB image decoded", got != null && got.Width == 32 && got.Height == 24 && Math.Abs(got.Data[0][49] - img.Data[0][49]) < 1e-6);
                lock (fake.Received) CheckTrue("BLOBs enabled and exposure requested", fake.Received.Any(r => r.StartsWith("<enableBLOB") && r.Contains("Cam")) && fake.Received.Any(r => r.Contains("CCD_EXPOSURE_VALUE'>0.5<")));
                CheckTrue("Focuser position, range and temperature", foc.Position == 1000 && foc.MaxStep == 50000 && foc.Temperature == 7.25 && foc.Absolute);
                foc.Move(2500);
                CheckTrue("Focuser moving", WaitFor(() => foc.IsMoving, 2000));
                CheckTrue("Focuser arrives", WaitFor(() => !foc.IsMoving && foc.Position == 2500, 3000));
            }
            finally { mount.Disconnect(); cam.Disconnect(); foc.Disconnect(); }
            var broken = make(DeviceKind.Focuser, "Broken");
            bool refused = false;
            try { broken.Connect(); } catch (InvalidOperationException ex) { refused = ex.Message.Contains("could not connect"); }
            CheckTrue("A device that refuses to connect is reported", refused && !broken.Connected);
            fake.Stop();
            bool unsupported = false;
            try { IndiClient.Create(new DeviceInfo { Kind = DeviceKind.Dome, Address = "x:1/y" }); } catch (NotSupportedException) { unsupported = true; }
            CheckTrue("Unsupported INDI device kind", unsupported);
        }

        // ------------------------------------------------------------------ ASCOM Alpaca

        private class FakeAlpaca
        {
            public readonly int Port;
            private readonly TcpListener _l;
            public readonly List<string> Requests = new List<string>();
            public readonly Dictionary<string, string> Values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public Func<byte[]> ImageBytes;
            public string ImageJson;

            public FakeAlpaca()
            {
                _l = Listen(out Port);
                new Thread(() => { try { while (true) { var c = _l.AcceptTcpClient(); new Thread(() => Serve(c)) { IsBackground = true }.Start(); } } catch { } }) { IsBackground = true }.Start();
            }

            public void Stop() => _l.Stop();

            private static string ReadLine(Stream s)
            {
                var sb = new StringBuilder();
                int b;
                while ((b = s.ReadByte()) >= 0) { if (b == '\n') break; if (b != '\r') sb.Append((char)b); }
                return b < 0 && sb.Length == 0 ? null : sb.ToString();
            }

            private void Serve(TcpClient c)
            {
                using (c)
                using (var ns = c.GetStream())
                {
                    try
                    {
                        string start = ReadLine(ns);
                        if (start == null) return;
                        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        string h;
                        while (!string.IsNullOrEmpty(h = ReadLine(ns))) { int k = h.IndexOf(':'); if (k > 0) headers[h.Substring(0, k).Trim()] = h.Substring(k + 1).Trim(); }
                        string body = "";
                        string cl;
                        if (headers.TryGetValue("Content-Length", out cl) && int.Parse(cl) > 0)
                        {
                            string ex;
                            if (headers.TryGetValue("Expect", out ex) && ex.Contains("100")) { var cont = Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n"); ns.Write(cont, 0, cont.Length); }
                            var bb = new byte[int.Parse(cl)];
                            int got = 0;
                            while (got < bb.Length) { int n = ns.Read(bb, got, bb.Length - got); if (n <= 0) break; got += n; }
                            body = Encoding.UTF8.GetString(bb);
                        }
                        var parts = start.Split(' ');
                        string method = parts[0], url = parts[1];
                        lock (Requests) Requests.Add(method + " " + url + (body.Length > 0 ? " " + body : ""));
                        string path = url.Split('?')[0].ToLowerInvariant();
                        string member = path.Substring(path.LastIndexOf('/') + 1);
                        byte[] payload; string type = "application/json";
                        if (path == "/management/v1/configureddevices")
                            payload = Encoding.UTF8.GetBytes("{\"Value\":[{\"DeviceName\":\"Fake Mount\",\"DeviceType\":\"Telescope\",\"DeviceNumber\":0,\"UniqueID\":\"abc-1\"},{\"DeviceName\":\"Fake Cam\",\"DeviceType\":\"Camera\",\"DeviceNumber\":1,\"UniqueID\":\"abc-2\"},{\"DeviceName\":\"Odd\",\"DeviceType\":\"Teapot\",\"DeviceNumber\":0,\"UniqueID\":\"x\"}],\"ErrorNumber\":0,\"ErrorMessage\":\"\"}");
                        else if (member == "imagearray" && ImageBytes != null) { payload = ImageBytes(); type = "application/imagebytes"; }
                        else if (member == "imagearray") payload = Encoding.UTF8.GetBytes(ImageJson);
                        else if (method == "PUT")
                        {
                            foreach (var kv in body.Split('&').Select(p => p.Split('=')).Where(p => p.Length == 2))
                                if (!kv[0].StartsWith("Client")) lock (Values) Values[member + "." + Uri.UnescapeDataString(kv[0])] = Uri.UnescapeDataString(kv[1]);
                            if (member == "tracking") lock (Values) Values["tracking"] = Values["tracking.Tracking"];
                            payload = Encoding.UTF8.GetBytes("{\"ErrorNumber\":0,\"ErrorMessage\":\"\"}");
                        }
                        else
                        {
                            string v;
                            lock (Values) Values.TryGetValue(member, out v);
                            payload = Encoding.UTF8.GetBytes(v == null ? "{\"ErrorNumber\":1024,\"ErrorMessage\":\"Not implemented\"}" : "{\"Value\":" + v + ",\"ErrorNumber\":0,\"ErrorMessage\":\"\"}");
                        }
                        var head = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: " + type + "\r\nContent-Length: " + payload.Length + "\r\nConnection: close\r\n\r\n");
                        ns.Write(head, 0, head.Length);
                        ns.Write(payload, 0, payload.Length);
                    }
                    catch { }
                }
            }
        }

        private static byte[] ImageBytesOf(int w, int h, int planes, int xmit, int elem, Func<int, int, int, double> value)
        {
            int size = xmit == 6 ? 1 : xmit == 2 || xmit == 4 || xmit == 9 ? 4 : xmit == 3 || xmit == 7 || xmit == 10 ? 8 : 2;
            var ms = new MemoryStream();
            var bw = new BinaryWriter(ms);
            foreach (var v in new[] { 1, 0, 7, 8, 44, elem, xmit, planes > 1 ? 3 : 2, w, h, planes > 1 ? planes : 0 }) bw.Write(v);
            for (int x = 0; x < w; x++)
                for (int y = 0; y < h; y++)
                    for (int c = 0; c < planes; c++)
                    {
                        double v = value(x, y, c);
                        switch (xmit)
                        {
                            case 1: bw.Write((short)v); break;
                            case 2: bw.Write((int)v); break;
                            case 3: bw.Write(v); break;
                            case 4: bw.Write((float)v); break;
                            case 6: bw.Write((byte)v); break;
                            case 7: bw.Write((long)v); break;
                            case 9: bw.Write((uint)v); break;
                            case 10: bw.Write((ulong)v); break;
                            default: bw.Write((ushort)v); break;
                        }
                    }
            return ms.ToArray();
        }

        [TestMethod]
        public void TestAlpaca()
        {
            // ImageBytes decoding for every transmission type
            foreach (var xmit in new[] { 1, 2, 3, 4, 6, 7, 8, 9, 10 })
            {
                // element type 2 (Int32) except for Int16 transmission, which is sent with element type Int16
                var b = ImageBytesOf(5, 3, 1, xmit, xmit == 1 ? 1 : 2, (x, y, c) => 10 * x + y + (xmit == 1 ? 0 : 100));
                var img = AlpacaCamera.FromImageBytes(b, 1000);
                double expect = (10 * 4 + 2 + (xmit == 1 ? 0 : 100)) / 1000.0;
                CheckTrue("ImageBytes transmission type " + xmit, img.Width == 5 && img.Height == 3 && Math.Abs(img.Data[0][2 * 5 + 4] - expect) < 1e-6, img.Data[0][2 * 5 + 4].ToString("0.0000"));
            }
            var colour = AlpacaCamera.FromImageBytes(ImageBytesOf(4, 2, 3, 8, 8, (x, y, c) => 100 * c + x), 255);
            CheckTrue("ImageBytes three planes", colour.Channels == 3 && Math.Abs(colour.Data[2][3] - 203 / 255.0) < 1e-6);
            var errBytes = ImageBytesOf(1, 1, 1, 8, 8, (x, y, c) => 0);
            errBytes[4] = 5;
            bool errThrown = false;
            try { AlpacaCamera.FromImageBytes(errBytes, 1); } catch (InvalidOperationException) { errThrown = true; }
            CheckTrue("ImageBytes error number reported", errThrown);

            var fake = new FakeAlpaca();
            foreach (var kv in new Dictionary<string, string>
            {
                { "rightascension", "5.5" }, { "declination", "22.25" }, { "tracking", "false" }, { "slewing", "false" }, { "sideofpier", "1" }, { "destinationsideofpier", "0" },
                { "canpark", "true" }, { "cansync", "true" }, { "sitelatitude", "52.0" }, { "description", "\"Fake mount\"" },
                { "cameraxsize", "6" }, { "cameraysize", "4" }, { "maxadu", "4095" }, { "binx", "1" }, { "imageready", "true" }, { "sensortype", "2" }, { "bayeroffsetx", "1" }, { "bayeroffsety", "0" },
                { "lastexposureduration", "2.5" }, { "gain", "120" }, { "position", "3" }, { "names", "[\"L\",\"R\",\"G\",\"B\"]" }, { "focusoffsets", "[0,10,20,30]" },
                { "issafe", "true" }, { "shutterstatus", "1" }, { "azimuth", "180.5" }
            }) fake.Values[kv.Key] = kv.Value;
            fake.ImageBytes = () => ImageBytesOf(6, 4, 1, 8, 2, (x, y, c) => 100 * x + y);
            string host = "127.0.0.1:" + fake.Port;
            try
            {
                var devices = AlpacaDiscovery.Devices(host);
                CheckTrue("Configured devices listed (unknown types skipped)", devices.Count == 2 && devices[0].Kind == DeviceKind.Telescope && devices[0].Address == host + "/0" && devices[1].Kind == DeviceKind.Camera && devices[1].Address == host + "/1" && devices[1].Description == "abc-2");
                var mount = (AlpacaTelescope)AlpacaDiscovery.Create(devices[0]);
                mount.Connect();
                CheckTrue("Telescope readings", mount.Connected && mount.RightAscension == 5.5 && mount.Declination == 22.25 && mount.SideOfPier == PierSide.West && mount.CanPark && mount.SiteLatitude == 52 && mount.Description == "Fake mount");
                CheckTrue("Unimplemented members fall back", double.IsNaN(mount.Altitude) && !mount.AtPark);
                bool notImpl = false;
                try { var x = mount.RightAscension + 0; fake.Values.Remove("rightascension"); x = mount.RightAscension; } catch (InvalidOperationException ex) { notImpl = ex.Message.Contains("1024"); }
                CheckTrue("Alpaca error numbers raised", notImpl);
                mount.SlewToCoordinatesAsync(6.5, -10.25);
                CheckTrue("Goto turns tracking on first", fake.Values["tracking.Tracking"] == "true" && fake.Values["slewtocoordinatesasync.RightAscension"] == "6.5" && fake.Values["slewtocoordinatesasync.Declination"] == "-10.25");
                mount.PulseGuide(GuideDirection.West, 300);
                CheckTrue("Pulse guide parameters", fake.Values["pulseguide.Direction"] == "3" && fake.Values["pulseguide.Duration"] == "300");
                CheckTrue("Destination side of pier", mount.DestinationSideOfPier(1, 2) == PierSide.East);
                lock (fake.Requests)
                {
                    CheckTrue("Every request carries the client ids", fake.Requests.Where(r => r.Contains("/api/v1/")).All(r => r.Contains("ClientID=") && r.Contains("ClientTransactionID=")));
                    CheckTrue("A query string with one '?'", fake.Requests.Any(r => r.Contains("destinationsideofpier?RightAscension=1&Declination=2&ClientID=")) && fake.Requests.All(r => r.Split(' ')[1].Count(ch => ch == '?') <= 1));
                    CheckTrue("Device path", fake.Requests.Any(r => r.StartsWith("PUT /api/v1/telescope/0/connected")));
                }

                var cam = (AlpacaCamera)AlpacaDiscovery.Create(devices[1]);
                cam.Connect();
                CheckTrue("Camera readings", cam.SensorWidth == 6 && cam.SensorHeight == 4 && cam.BitDepth == 12 && cam.Bayer == BayerPattern.GRBG && cam.HasGain && cam.Gain == 120 && !cam.HasOffset);
                cam.StartExposure(2.5, false);
                CheckTrue("Start exposure parameters", fake.Values["startexposure.Duration"] == "2.5" && fake.Values["startexposure.Light"] == "false");
                var img = cam.GetImage();
                CheckTrue("Image downloaded as ImageBytes", img.Width == 6 && img.Height == 4 && Math.Abs(img.Data[0][3 * 6 + 5] - 503 / 4095.0) < 1e-6 && img.Bayer == BayerPattern.GRBG && img.ExposureSeconds == 2.5);
                fake.ImageBytes = null;
                fake.ImageJson = "{\"Type\":2,\"Rank\":2,\"Value\":[[0,1,2,3],[10,11,12,13],[20,21,22,23],[30,31,32,33],[40,41,42,43],[50,51,52,53]],\"ErrorNumber\":0,\"ErrorMessage\":\"\"}";
                var jsonImg = cam.GetImage();
                CheckTrue("Image downloaded as JSON", jsonImg.Width == 6 && jsonImg.Height == 4 && Math.Abs(jsonImg.Data[0][2 * 6 + 5] - 52 / 4095.0) < 1e-6);

                var wheel = (AlpacaFilterWheel)AlpacaDiscovery.Create(new DeviceInfo { Kind = DeviceKind.FilterWheel, Address = host + "/0" });
                CheckTrue("Filter wheel", wheel.Position == 3 && wheel.Names.SequenceEqual(new[] { "L", "R", "G", "B" }) && wheel.FocusOffsets[2] == 20 && !wheel.IsMoving);
                var safety = (AlpacaSafety)AlpacaDiscovery.Create(new DeviceInfo { Kind = DeviceKind.SafetyMonitor, Address = host + "/0" });
                var dome = (AlpacaDome)AlpacaDiscovery.Create(new DeviceInfo { Kind = DeviceKind.Dome, Address = host + "/0" });
                CheckTrue("Safety monitor and dome", safety.IsSafe && dome.Shutter == ShutterState.Closed && dome.Azimuth == 180.5);
                dome.OpenShutter();
                lock (fake.Requests) CheckTrue("Dome and safety paths", fake.Requests.Any(r => r.StartsWith("PUT /api/v1/dome/0/openshutter")) && fake.Requests.Any(r => r.StartsWith("GET /api/v1/safetymonitor/0/issafe")));
                var foc = (AlpacaFocuser)AlpacaDiscovery.Create(new DeviceInfo { Kind = DeviceKind.Focuser, Address = host });
                foc.Move(1234);
                CheckTrue("Device number defaults to 0", fake.Values["move.Position"] == "1234" && fake.Requests.Any(r => r.StartsWith("PUT /api/v1/focuser/0/move")));
                bool unsupported = false;
                try { AlpacaDiscovery.Create(new DeviceInfo { Kind = DeviceKind.Rotator, Address = host }); } catch (NotSupportedException) { unsupported = true; }
                CheckTrue("Unsupported Alpaca device kind", unsupported);
                mount.Disconnect();
                CheckTrue("Disconnect", !mount.Connected && fake.Values["connected.Connected"] == "false");
            }
            finally { fake.Stop(); }

            // discovery: a responder on the loopback interface answers the broadcast
            UdpClient responder = null;
            try { responder = new UdpClient(new IPEndPoint(IPAddress.Loopback, 32227)); }
            catch (SocketException) { Info("UDP port 32227 is in use; Alpaca discovery reply not tested"); }
            if (responder != null)
            {
                var reply = new Thread(() =>
                {
                    try
                    {
                        var ep = new IPEndPoint(IPAddress.Any, 0);
                        var msg = responder.Receive(ref ep);
                        if (Encoding.ASCII.GetString(msg) == "alpacadiscovery1")
                        {
                            var b = Encoding.ASCII.GetBytes("{\"AlpacaPort\":11111}");
                            responder.Send(b, b.Length, ep);
                        }
                    }
                    catch { }
                }) { IsBackground = true };
                reply.Start();
                var servers = AlpacaDiscovery.FindServers(1000);
                responder.Close();
                CheckTrue("Alpaca discovery finds the responder", servers.Contains("127.0.0.1:11111"), string.Join(", ", servers));
            }
        }

        // ------------------------------------------------------------------ ASCOM COM wrappers with stand-in drivers

        public class AxisRate { public double Minimum, Maximum; }

        public class FakeAscomTelescope
        {
            public readonly List<string> Calls = new List<string>();
            public bool Connected { get; set; } = true;
            public string Description => "Stand-in mount";
            public double RightAscension => 12.5;
            public double Declination => -30.25;
            public double Azimuth => 123.4;
            public double SiderealTime => 7.75;
            public bool Slewing => false;
            public bool Tracking { get; set; }
            public int TrackingRate { get; set; } = 0;
            public bool AtPark => false;
            public int SideOfPier => 0;
            public bool CanPark => true;
            public bool CanSync => true;
            public bool CanPulseGuide => true;
            public bool CanSlewAltAzAsync => false;
            public double SiteLatitude => 48.5;
            public double SiteLongitude => -2.25;
            public void SlewToCoordinatesAsync(double ra, double dec) => Calls.Add(string.Format(System.Globalization.CultureInfo.InvariantCulture, "slew {0} {1}", ra, dec));
            public void SyncToCoordinates(double ra, double dec) => Calls.Add(string.Format(System.Globalization.CultureInfo.InvariantCulture, "sync {0} {1}", ra, dec));
            public void AbortSlew() => Calls.Add("abort");
            public void Park() => Calls.Add("park");
            public void Unpark() => Calls.Add("unpark");
            public void PulseGuide(int dir, int ms) => Calls.Add("guide " + dir + " " + ms);
            public void MoveAxis(int axis, double rate) => Calls.Add(string.Format(System.Globalization.CultureInfo.InvariantCulture, "move {0} {1}", axis, rate));
            public AxisRate[] AxisRates(int axis) => new[] { new AxisRate { Minimum = 0, Maximum = 2.0 }, new AxisRate { Minimum = 0, Maximum = 5.0 } };
            public int DestinationSideOfPier(double ra, double dec) => 1;
        }

        public class FakeAscomCamera
        {
            public bool Connected { get; set; } = true;
            public int CameraXSize => 4;
            public int CameraYSize => 3;
            public double PixelSizeX => 2.9;
            public double PixelSizeY => 2.9;
            public short BinX { get; set; } = 1;
            public short BinY { get; set; } = 1;
            public short MaxBinX => 4;
            public int StartX { get; set; }
            public int StartY { get; set; }
            public int NumX { get; set; } = 4;
            public int NumY { get; set; } = 3;
            public int CameraState => 2;
            public bool ImageReady => true;
            public bool CanSetCCDTemperature => true;
            public bool CoolerOn { get; set; }
            public double CCDTemperature => -9.5;
            public double SetCCDTemperature { get; set; } = -10;
            public short Gain { get; set; } = 100;
            public int MaxADU => 255;
            public int SensorType => 2;
            public short BayerOffsetX => 0;
            public short BayerOffsetY => 1;
            public double LastExposureDuration => 3;
            public string LastExposureStartTime => "2026-10-24T21:15:30";
            public string Stopped;
            public void StartExposure(double s, bool light) { }
            public void AbortExposure() { throw new NotImplementedException(); }
            public void StopExposure() { Stopped = "yes"; }
            public object ImageArray
            {
                get
                {
                    var a = new int[4, 3];
                    for (int x = 0; x < 4; x++) for (int y = 0; y < 3; y++) a[x, y] = 10 * x + y;
                    return a;
                }
            }
        }

        public class FakeAscomMisc
        {
            public bool Connected { get; set; } = true;
            // focuser
            public int Position => 5000;
            public int MaxStep => 60000;
            public bool IsMoving => false;
            public bool Absolute => true;
            public double Temperature => 6.5;
            public double StepSize => 3.75;
            public int Moved;
            public void Move(int p) => Moved = p;
            // rotator (float position)
            public float RotatorAngle => 45.5f;
            // switch
            public short MaxSwitch => 2;
            public string GetSwitchName(short i) => "Dew " + i;
            public bool GetSwitch(short i) => i == 1;
            public double GetSwitchValue(short i) => 0.25 * (i + 1);
            public double LastSet;
            public void SetSwitchValue(short i, double v) => LastSet = v;
            // safety, weather, cover
            public bool IsSafe => true;
            public double Humidity => 71.5;
            public double SkyQuality => 21.2;
            public int CoverState => 3;
            public int Brightness => 7;
            public int MaxBrightness => 255;
            public int CalibratorLevel;
            public void CalibratorOn(int b) => CalibratorLevel = b;
        }

        public class FakeAscomWheel
        {
            public bool Connected { get; set; } = true;
            public short Position { get; set; } = 2;
            public string[] Names => new[] { "L", "Ha", "OIII" };
            public int[] FocusOffsets => new[] { 0, 40, 35 };
        }

        public class FakeAscomDome
        {
            public bool Connected { get; set; } = true;
            public double Azimuth => 90;
            public int ShutterStatus => 2;
            public bool AtPark => false;
            public bool CanSetAzimuth => true;
            public bool Slaved { get; set; }
            public double Target;
            public bool Opened;
            public void SlewToAzimuth(double az) => Target = az;
            public void OpenShutter() => Opened = true;
        }

        public class FakeAscomRotator
        {
            public bool Connected { get; set; } = true;
            public float Position => 45.5f;
            public bool Reverse { get; set; }
            public float Target;
            public void MoveAbsolute(float p) => Target = p;
        }

        private static T Wrap<T>(T device, object driver) where T : IDevice
        {
            typeof(AscomBase).GetField("D", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).SetValue(device, driver);
            return device;
        }

        [TestMethod]
        public void TestAscomWrappers()
        {
            Func<DeviceKind, DeviceInfo> info = k => new DeviceInfo { Kind = k, Protocol = DeviceProtocol.Ascom, Name = "Stand-in " + k, Address = "Sidralune.NoSuchDriver" };
            var drv = new FakeAscomTelescope();
            var tel = Wrap((AscomTelescope)AscomDiscovery.Create(info(DeviceKind.Telescope)), drv);
            CheckTrue("ASCOM telescope readings", tel.RightAscension == 12.5 && tel.Declination == -30.25 && tel.Azimuth == 123.4 && tel.SiderealTime == 7.75 && tel.SideOfPier == PierSide.East && tel.CanPark && tel.SiteLongitude == -2.25 && tel.Description == "Stand-in mount");
            CheckTrue("Missing driver members fall back", double.IsNaN(tel.Altitude));
            CheckTrue("Axis rates from the driver", tel.AxisRates.SequenceEqual(new[] { 2.0, 5.0 }));
            tel.SlewToCoordinatesAsync(1.5, 2.5);
            tel.PulseGuide(GuideDirection.South, 400);
            tel.MoveAxis(1, -0.5);
            tel.Park();
            CheckTrue("Goto switches tracking on, commands passed through", drv.Tracking && drv.Calls.SequenceEqual(new[] { "slew 1.5 2.5", "guide 1 400", "move 1 -0.5", "park" }), string.Join(", ", drv.Calls));
            CheckTrue("Destination side of pier", tel.DestinationSideOfPier(0, 0) == PierSide.West);
            tel.TrackingRate = TrackingRate.Lunar;
            CheckTrue("Tracking rate", drv.TrackingRate == 1 && tel.TrackingRate == TrackingRate.Lunar);

            var camDrv = new FakeAscomCamera();
            var cam = Wrap((AscomCamera)AscomDiscovery.Create(info(DeviceKind.Camera)), camDrv);
            CheckTrue("ASCOM camera readings", cam.SensorWidth == 4 && cam.SensorHeight == 3 && cam.MaxBin == 4 && cam.State == CameraState.Exposing && cam.BitDepth == 8 && cam.Bayer == BayerPattern.GBRG && cam.HasGain && !cam.HasOffset);
            cam.BinX = 2; cam.Gain = 250; cam.SetTemperature = -15; cam.CoolerOn = true;
            CheckTrue("Camera settings written with the driver's types", camDrv.BinX == 2 && camDrv.Gain == 250 && camDrv.SetCCDTemperature == -15 && camDrv.CoolerOn);
            cam.AbortExposure();
            CheckTrue("Abort falls back to StopExposure", camDrv.Stopped == "yes");
            var img = cam.GetImage();
            CheckTrue("Image array converted, header filled", img.Width == 4 && img.Height == 3 && Math.Abs(img.Data[0][2 * 4 + 3] - 32 / 255.0) < 1e-6 && img.DateObs == new DateTime(2026, 10, 24, 21, 15, 30) && img.ExposureSeconds == 3
                && img.Header.GetInt("XBINNING") == 2 && img.Header.GetDouble("CCD-TEMP") == -9.5 && img.Header.GetInt("GAIN") == 250);
            var a3 = new short[2, 2, 3];
            a3[1, 0, 2] = 200;
            var c3 = AscomCamera.ConvertArray(a3, 400);
            CheckTrue("Colour (three-plane) image array", c3.Channels == 3 && Math.Abs(c3.Data[2][1] - 0.5) < 1e-6);

            var misc = new FakeAscomMisc();
            var foc = Wrap((AscomFocuser)AscomDiscovery.Create(info(DeviceKind.Focuser)), misc);
            foc.Move(5100);
            CheckTrue("ASCOM focuser", foc.Position == 5000 && foc.MaxStep == 60000 && foc.Temperature == 6.5 && foc.StepSizeMicrons == 3.75 && misc.Moved == 5100);
            var wheelDrv = new FakeAscomWheel();
            var wheel = Wrap((AscomFilterWheel)AscomDiscovery.Create(info(DeviceKind.FilterWheel)), wheelDrv);
            wheel.Position = 1;
            CheckTrue("ASCOM filter wheel", wheelDrv.Position == 1 && wheel.Names.SequenceEqual(new[] { "L", "Ha", "OIII" }) && wheel.FocusOffsets[1] == 40);
            var rotDrv = new FakeAscomRotator();
            var rot = Wrap((AscomRotator)AscomDiscovery.Create(info(DeviceKind.Rotator)), rotDrv);
            rot.MoveAbsolute(90.25);
            CheckTrue("ASCOM rotator (float angles)", rot.Position == 45.5 && rotDrv.Target == 90.25f);
            var sw = Wrap((AscomSwitch)AscomDiscovery.Create(info(DeviceKind.Switch)), misc);
            sw.SetValue(0, 0.75);
            CheckTrue("ASCOM switch", sw.Count == 2 && sw.GetName(1) == "Dew 1" && sw.GetSwitch(1) && !sw.GetSwitch(0) && sw.GetValue(1) == 0.5 && misc.LastSet == 0.75);
            var safe = Wrap((AscomSafety)AscomDiscovery.Create(info(DeviceKind.SafetyMonitor)), misc);
            var wx = Wrap((AscomWeather)AscomDiscovery.Create(info(DeviceKind.ObservingConditions)), misc);
            var cover = Wrap((AscomCover)AscomDiscovery.Create(info(DeviceKind.CoverCalibrator)), misc);
            cover.CalibratorOn(42);
            CheckTrue("ASCOM safety, weather and cover", safe.IsSafe && wx.Humidity == 71.5 && wx.SkyQuality == 21.2 && double.IsNaN(wx.WindSpeed) && cover.CoverState == "3" && cover.MaxBrightness == 255 && misc.CalibratorLevel == 42);
            var domeDrv = new FakeAscomDome();
            var dome = Wrap((AscomDome)AscomDiscovery.Create(info(DeviceKind.Dome)), domeDrv);
            dome.SlewToAzimuth(123.5); dome.OpenShutter(); dome.Slaved = true;
            CheckTrue("ASCOM dome", dome.Azimuth == 90 && dome.Shutter == ShutterState.Opening && !dome.AtPark && dome.CanSetAzimuth && domeDrv.Target == 123.5 && domeDrv.Opened && dome.Slaved);
            tel.Disconnect();
            CheckTrue("Disconnect sets the driver's Connected to false", !drv.Connected && !tel.Connected);

            bool notRegistered = false;
            try { AscomDiscovery.Create(info(DeviceKind.Focuser)).Connect(); } catch (InvalidOperationException ex) { notRegistered = ex.Message.Contains("not registered"); }
            CheckTrue("Unregistered driver reported", notRegistered);
            bool unsupported = false;
            try { AscomDiscovery.Create(info(DeviceKind.Guider)); } catch (NotSupportedException) { unsupported = true; }
            CheckTrue("Guider is not an ASCOM device", unsupported);
            var drivers = AscomDiscovery.Drivers(DeviceKind.Telescope);
            CheckTrue("Driver listing works with or without the ASCOM Platform", drivers != null && drivers.All(d => d.Protocol == DeviceProtocol.Ascom && d.Kind == DeviceKind.Telescope));
            Info(AscomDiscovery.PlatformInstalled ? drivers.Count + " ASCOM telescope driver(s) registered" : "ASCOM Platform not installed");
        }

        // ------------------------------------------------------------------ device manager

        [TestMethod]
        public void TestDeviceManager()
        {
            var dm = new DeviceManager();
            int changes = 0;
            dm.DevicesChanged += (s, e) => changes++;
            CheckTrue("Created by protocol", dm.Create(new DeviceInfo { Kind = DeviceKind.Telescope, Protocol = DeviceProtocol.Lx200, Address = "COM9" }) is Lx200Telescope
                && dm.Create(new DeviceInfo { Kind = DeviceKind.Camera, Protocol = DeviceProtocol.Alpaca, Address = "h:1/0" }) is AlpacaCamera
                && dm.Create(new DeviceInfo { Kind = DeviceKind.Focuser, Protocol = DeviceProtocol.Indi, Address = "h:1/F" }) is IndiFocuser
                && dm.Create(new DeviceInfo { Kind = DeviceKind.Dome, Protocol = DeviceProtocol.Simulator }) is DomeSimulator);
            var guiders = dm.Discover(DeviceKind.Guider, false);
            CheckTrue("Discovery offers the simulator and PHD2", guiders.Any(d => d.Protocol == DeviceProtocol.Simulator) && guiders.Any(d => d.Protocol == DeviceProtocol.Phd2 && d.Address == "localhost:4400"));
            dm.ConnectSimulators();
            CheckTrue("Simulated observatory connected", dm.Telescope is TelescopeSimulator && dm.Camera is CameraSimulator && dm.Focuser != null && dm.FilterWheel != null && dm.Dome != null && dm.Weather != null && dm.Safety != null && changes == 6);
            var cs = (CameraSimulator)dm.Camera;
            CheckTrue("Camera simulator linked to mount, focuser and wheel", cs.Mount == dm.Telescope && cs.Focuser == dm.Focuser && cs.Wheel == dm.FilterWheel);
            var oldFocuser = dm.Focuser;
            dm.Connect(new DeviceInfo { Kind = DeviceKind.Focuser, Protocol = DeviceProtocol.Simulator });
            CheckTrue("Replacing a device disconnects the old one and relinks the camera", !oldFocuser.Connected && dm.Focuser != oldFocuser && cs.Focuser == dm.Focuser);
            CheckTrue("Get by kind", dm.Get(DeviceKind.Dome) == dm.Dome && dm.Get(DeviceKind.Rotator) == null && dm.All.Count() == 7);
            dm.Disconnect(DeviceKind.Dome);
            CheckTrue("Disconnect one", dm.Dome == null);
            dm.DisconnectAll();
            CheckTrue("Disconnect all", !dm.All.Any() && dm.Telescope == null);
            bool noSim = false;
            try { dm.Create(new DeviceInfo { Kind = DeviceKind.Rotator, Protocol = DeviceProtocol.Simulator }); } catch (NotSupportedException) { noSim = true; }
            CheckTrue("No rotator simulator", noSim);
        }
    }
}
