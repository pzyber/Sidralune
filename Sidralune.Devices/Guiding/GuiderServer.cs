using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Sidralune.Core.Util;

namespace Sidralune.Devices.Guiding
{
    /// <summary>
    /// A server that speaks PHD2's protocol for the Sidralune guider, so programs written for PHD2 can drive it: newline-separated JSON, JSON-RPC 2.0 requests from each client, and events to all of them.
    /// It listens on localhost (or every address when allowed) on PHD2's port, 4400 by default.
    /// </summary>
    public class GuiderServer : IDisposable
    {
        private readonly DeviceManager _dm;
        private readonly object _lock = new object();
        private readonly List<Client> _clients = new List<Client>();
        private TcpListener _listener;
        private Thread _accept;
        private SidraluneGuider _g;
        private string _lastState = SidraluneGuider.Stopped;
        private int _frame;
        private double _avgDist;
        private DateTime _guideStart = DateTime.UtcNow;
        private volatile bool _settling;
        private DateTime _settleStart;

        public int Port { get; private set; }
        public bool Running => _listener != null;
        /// <summary>Why the server could not start (the port taken), or null.</summary>
        public string Problem { get; private set; }
        public int ClientCount { get { lock (_lock) return _clients.Count; } }
        public event Action Changed;

        public GuiderServer(DeviceManager dm) { _dm = dm; }

        private SidraluneGuider G => _dm.BuiltinGuider;

        /// <summary>Starts listening; false (with Problem) when the port is taken.</summary>
        public bool Start(int port = 4400, bool allowOthers = false)
        {
            Stop();
            Port = port;
            Problem = null;
            try
            {
                var l = new TcpListener(allowOthers ? IPAddress.Any : IPAddress.Loopback, port);
                l.Start();
                _listener = l;
            }
            catch (SocketException)
            {
                Problem = Lang.T("Port {0} is in use (is PHD2 running?)", port);
                Log.Warn("Guider server", Problem);
                Changed?.Invoke();
                return false;
            }
            Hook();
            _accept = new Thread(AcceptLoop) { IsBackground = true, Name = "Guider server" };
            _accept.Start();
            Log.Info("Guider server", "Listening on port " + port);
            Changed?.Invoke();
            return true;
        }

        public void Stop()
        {
            var l = _listener;
            _listener = null;
            if (l != null) { try { l.Stop(); } catch { } Log.Info("Guider server", "Stopped"); }
            List<Client> cs;
            lock (_lock) { cs = new List<Client>(_clients); _clients.Clear(); }
            foreach (var c in cs) c.Close();
            Unhook();
            Changed?.Invoke();
        }

        public void Dispose() => Stop();

        // ------------------------------------------------------------------ the guider's events

        private void Hook()
        {
            Unhook();
            _g = G;
            _g.StateChanged += OnState;
            _g.Step += OnStep;
            _g.FrameReady += OnFrame;
            _g.CalibrationChanged += OnCalibration;
            _g.Failed += OnFailed;
            _lastState = _g.State;
        }

        private void Unhook()
        {
            if (_g == null) return;
            _g.StateChanged -= OnState;
            _g.Step -= OnStep;
            _g.FrameReady -= OnFrame;
            _g.CalibrationChanged -= OnCalibration;
            _g.Failed -= OnFailed;
            _g = null;
        }

        private void OnState(object sender, EventArgs e)
        {
            string s = G.State, was = _lastState;
            if (s == was) return;
            _lastState = s;
            switch (s)
            {
                case SidraluneGuider.Looping: if (was == SidraluneGuider.Stopped) Broadcast(Event("LoopingExposures", "Frame", _frame)); break;
                case SidraluneGuider.Selected:
                    if (G.Star != null) Broadcast(Event("StarSelected", "X", G.Star.X, "Y", G.Star.Y));
                    break;
                case SidraluneGuider.Calibrating: Broadcast(Event("StartCalibration", "Mount", "Mount")); break;
                case SidraluneGuider.Guiding:
                    if (was == SidraluneGuider.Paused) Broadcast(Event("Resumed"));
                    else if (was != SidraluneGuider.LostLock)
                    {
                        _guideStart = DateTime.UtcNow; _avgDist = 0;
                        Broadcast(Event("StartGuiding"));
                        if (!double.IsNaN(G.LockX)) Broadcast(Event("LockPositionSet", "X", G.LockX, "Y", G.LockY));
                    }
                    break;
                case SidraluneGuider.Paused: Broadcast(Event("Paused")); break;
                case SidraluneGuider.LostLock:
                    Broadcast(Event("StarLost", "Frame", _frame, "Time", Elapsed(), "StarMass", 0, "SNR", 0, "AvgDist", _avgDist, "ErrorCode", 8, "Status", "Star lost"));
                    break;
                case SidraluneGuider.Stopped:
                    if (was == SidraluneGuider.Guiding || was == SidraluneGuider.Paused || was == SidraluneGuider.LostLock) Broadcast(Event("GuidingStopped"));
                    Broadcast(Event("LoopingExposuresStopped"));
                    break;
            }
        }

        private double Elapsed() => (DateTime.UtcNow - _guideStart).TotalSeconds;

        private void OnStep(GuideStep s)
        {
            double dist = Math.Sqrt(s.RaDistance * s.RaDistance + s.DecDistance * s.DecDistance);
            _avgDist = _avgDist <= 0 ? dist : 0.7 * _avgDist + 0.3 * dist;
            var m = Event("GuideStep",
                "Frame", _frame, "Time", Elapsed(), "Mount", "Mount",
                "dx", Num(s.Dx), "dy", Num(s.Dy),
                "RADistanceRaw", s.RaDistance, "DECDistanceRaw", s.DecDistance, "RADistanceGuide", s.RaDistance, "DECDistanceGuide", s.DecDistance,
                "RADuration", Math.Abs(s.RaPulse), "RADirection", s.RaPulse >= 0 ? "East" : "West",
                "DECDuration", Math.Abs(s.DecPulse), "DECDirection", s.DecPulse >= 0 ? "North" : "South",
                "StarMass", Num(s.StarMass), "SNR", Num(s.Snr), "HFD", Num(s.Hfd), "AvgDist", _avgDist);
            Broadcast(m);
        }

        private static object Num(double v) => double.IsNaN(v) ? (object)0.0 : v;

        private void OnFrame(Imaging.AstroImage f)
        {
            _frame++;
            if (_settling)
                Broadcast(Event("Settling", "Distance", Num(G.SettlingDistance), "Time", G.SettlingSeconds, "SettleTime", (DateTime.UtcNow - _settleStart).TotalSeconds, "StarLocked", G.Star != null && !G.Lost));
        }

        private void OnCalibration()
        {
            if (G.Calibration != null) Broadcast(Event("CalibrationComplete", "Mount", "Mount"));
        }

        private void OnFailed(string message) => Broadcast(Event("Alert", "Msg", message, "Type", "error"));

        private Dictionary<string, object> Event(string name, params object[] kv)
        {
            var m = new Dictionary<string, object>
            {
                { "Event", name },
                { "Timestamp", (DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds },
                { "Host", Environment.MachineName }, { "Inst", 1 }
            };
            for (int i = 0; i + 1 < kv.Length; i += 2) m[(string)kv[i]] = kv[i + 1];
            return m;
        }

        private void Broadcast(Dictionary<string, object> m)
        {
            string line = Json.Serialize(m);
            List<Client> cs;
            lock (_lock) cs = new List<Client>(_clients);
            foreach (var c in cs) if (!c.Send(line)) Remove(c);
        }

        private void Remove(Client c)
        {
            bool removed;
            lock (_lock) removed = _clients.Remove(c);
            c.Close();
            if (removed) Changed?.Invoke();
        }

        // ------------------------------------------------------------------ clients

        private void AcceptLoop()
        {
            var l = _listener;
            while (l != null && _listener == l)
            {
                TcpClient tcp;
                try { tcp = l.AcceptTcpClient(); } catch { break; }
                var c = new Client(tcp);
                lock (_lock) _clients.Add(c);
                Changed?.Invoke();
                // introduces itself the way PHD2 does: the version, then the state as it is
                c.Send(Json.Serialize(Event("Version", "PHDVersion", "2.6.14", "PHDSubver", "Sidralune", "MsgVersion", 1, "OverlapSupport", true)));
                var g = G;
                if (g.Star != null && !double.IsNaN(g.LockX)) c.Send(Json.Serialize(Event("LockPositionSet", "X", g.LockX, "Y", g.LockY)));
                if (g.Star != null) c.Send(Json.Serialize(Event("StarSelected", "X", g.Star.X, "Y", g.Star.Y)));
                if (g.Calibration != null) c.Send(Json.Serialize(Event("CalibrationComplete", "Mount", "Mount")));
                if (g.State == SidraluneGuider.Guiding) c.Send(Json.Serialize(Event("StartGuiding")));
                if (g.State == SidraluneGuider.Paused) c.Send(Json.Serialize(Event("Paused")));
                c.Send(Json.Serialize(Event("AppState", "State", AppState())));
                var t = new Thread(() => ReadLoop(c)) { IsBackground = true, Name = "Guider server client" };
                t.Start();
            }
        }

        private string AppState() => G.State;

        private void ReadLoop(Client c)
        {
            try
            {
                string line;
                while ((line = c.Reader.ReadLine()) != null)
                {
                    if (line.Trim().Length == 0) continue;
                    Dictionary<string, object> req;
                    try { req = Json.ParseObject(line); }
                    catch { c.Send(Json.Serialize(Error(null, -32700, "parse error"))); continue; }
                    object id = null, method, prms;
                    req.TryGetValue("id", out id);
                    if (!req.TryGetValue("method", out method)) { c.Send(Json.Serialize(Error(id, -32600, "invalid request"))); continue; }
                    req.TryGetValue("params", out prms);
                    Dictionary<string, object> reply;
                    try { reply = Result(id, Handle(Convert.ToString(method), prms)); }
                    catch (MissingMethodException) { reply = Error(id, -32601, "method not found"); }
                    catch (Exception ex) { reply = Error(id, 1, ex.Message); }
                    if (id != null) c.Send(Json.Serialize(reply));
                }
            }
            catch { }
            Remove(c);
        }

        private static Dictionary<string, object> Result(object id, object result) =>
            new Dictionary<string, object> { { "jsonrpc", "2.0" }, { "result", result }, { "id", id } };

        private static Dictionary<string, object> Error(object id, int code, string message) =>
            new Dictionary<string, object> { { "jsonrpc", "2.0" }, { "error", new Dictionary<string, object> { { "code", code }, { "message", message } } }, { "id", id } };

        // ------------------------------------------------------------------ methods

        /// <summary>A parameter by name (an object's member) or by place (an array's item).</summary>
        private static object Param(object prms, string name, int index)
        {
            var d = prms as Dictionary<string, object>;
            object v;
            if (d != null && d.TryGetValue(name, out v)) return v;
            var list = prms as System.Collections.IList;
            if (list != null && index >= 0 && index < list.Count) return list[index];
            return null;
        }

        private static double D(object v, double fallback) { try { return v == null ? fallback : Convert.ToDouble(v, CultureInfo.InvariantCulture); } catch { return fallback; } }
        private static bool B(object v, bool fallback) { try { return v == null ? fallback : Convert.ToBoolean(v, CultureInfo.InvariantCulture); } catch { return fallback; } }

        private static void SettleParams(object prms, out double px, out double time, out double timeout)
        {
            var s = Param(prms, "settle", 0) as Dictionary<string, object>;
            px = D(s != null && s.ContainsKey("pixels") ? s["pixels"] : null, 1.5);
            time = D(s != null && s.ContainsKey("time") ? s["time"] : null, 10);
            timeout = D(s != null && s.ContainsKey("timeout") ? s["timeout"] : null, 60);
        }

        private object Handle(string method, object prms)
        {
            var g = G;
            var st = g.Settings;
            switch (method)
            {
                case "get_app_state": return AppState();
                case "get_connected": return _dm.GuideCamera != null && _dm.GuideCamera.Connected;
                case "set_connected":
                    if (B(Param(prms, "connect", 0), true))
                    {
                        if (_dm.GuideCamera == null) throw new InvalidOperationException(Lang.T("Connect the guide camera in Sidralune's Observatory → Equipment."));
                    }
                    else g.StopGuiding();
                    return 0;
                case "get_current_equipment":
                    return new Dictionary<string, object>
                    {
                        { "camera", Equipment(_dm.GuideCamera) }, { "mount", Equipment(_dm.Telescope) },
                        { "AO", Equipment(_dm.Ao) }, { "rotator", Equipment(_dm.Rotator) }
                    };
                case "get_profiles": return new object[] { new Dictionary<string, object> { { "id", 1 }, { "name", "Sidralune" } } };
                case "get_profile": return new Dictionary<string, object> { { "id", 1 }, { "name", "Sidralune" } };
                case "set_profile": return 0;
                case "get_pixel_scale": return g.PixelScale;
                case "get_camera_frame_size":
                    {
                        var cam = _dm.GuideCamera;
                        if (cam == null) throw new InvalidOperationException(Lang.T("No guide camera connected."));
                        int bin = Math.Max(1, st.Binning);
                        return new object[] { cam.SensorWidth / bin, cam.SensorHeight / bin };
                    }
                case "get_exposure": return (int)Math.Round(g.CurrentExposure * 1000);
                case "set_exposure": st.ExposureSeconds = Math.Max(0.001, D(Param(prms, "exposure", 0), 2000) / 1000); st.AutoExposure = false; g.SaveSettings(); return 0;
                case "get_exposure_durations": return new object[] { 100, 200, 500, 1000, 1500, 2000, 2500, 3000, 4000, 5000, 6000, 8000, 10000, 15000 };
                case "get_search_region": return st.SearchRadius;
                case "get_use_subframes": return st.UseSubframes;
                case "loop": g.Loop(); return 0;
                case "stop_capture": g.StopGuiding(); return 0;
                case "find_star":
                    {
                        if (!g.IsRunning || g.LastFrame == null) throw new InvalidOperationException(Lang.T("Not looping."));
                        g.SelectStar();
                        return new object[] { g.Star.X, g.Star.Y };
                    }
                case "get_lock_position": return double.IsNaN(g.LockX) ? null : new object[] { g.LockX, g.LockY };
                case "set_lock_position":
                    {
                        double x = D(Param(prms, "X", 0), double.NaN), y = D(Param(prms, "Y", 1), double.NaN);
                        bool exact = B(Param(prms, "EXACT", 2), true);
                        if (!exact && !g.InGuidingPhase) { g.SelectStar(x, y); return 0; }
                        g.SetLockPosition(x, y);
                        Broadcast(Event("LockPositionSet", "X", g.LockX, "Y", g.LockY));
                        return 0;
                    }
                case "get_paused": return g.State == SidraluneGuider.Paused;
                case "set_paused": g.Pause(B(Param(prms, "paused", 0), true)); return 0;
                case "guide":
                    {
                        double px, time, timeout;
                        SettleParams(prms, out px, out time, out timeout);
                        bool recal = B(Param(prms, "recalibrate", 1), false);
                        if (_settling) throw new InvalidOperationException(Lang.T("Settling is still going on."));
                        BeginSettle();
                        Task.Run(async () =>
                        {
                            try
                            {
                                if (g.InGuidingPhase && !recal) EndSettle(await g.Settle(px, time, timeout, CancellationToken.None), null);
                                else
                                {
                                    g.SettlePixels = px; g.SettleSeconds = time; g.SettleTimeoutSeconds = timeout;
                                    await g.StartGuiding(recal, CancellationToken.None);
                                    EndSettle(true, null);
                                }
                            }
                            catch (Exception ex) { EndSettle(false, ex.Message); }
                        });
                        return 0;
                    }
                case "dither":
                    {
                        double px, time, timeout;
                        SettleParams(prms, out px, out time, out timeout);
                        double amount = D(Param(prms, "amount", 0), 5);
                        bool raOnly = B(Param(prms, "raOnly", 1), false);
                        if (!g.InGuidingPhase) throw new InvalidOperationException(Lang.T("Not guiding."));
                        if (_settling) throw new InvalidOperationException(Lang.T("Settling is still going on."));
                        double lx = g.LockX, ly = g.LockY;
                        BeginSettle();
                        Task.Run(async () =>
                        {
                            try
                            {
                                var dither = g.Dither(amount, raOnly, px, time, timeout, CancellationToken.None);
                                // the lock position moves at once
                                await Task.Delay(50);
                                Broadcast(Event("GuidingDithered", "dx", g.LockX - lx, "dy", g.LockY - ly));
                                await dither;
                                EndSettle(g.LastDitherSettled, g.LastDitherSettled ? null : Lang.T("Settling timed out."));
                            }
                            catch (Exception ex) { EndSettle(false, ex.Message); }
                        });
                        return 0;
                    }
                case "get_calibrated": return g.Calibration != null;
                case "get_calibration_data":
                    {
                        string which = Convert.ToString(Param(prms, "which", 0) ?? "Mount");
                        if (which.Equals("AO", StringComparison.OrdinalIgnoreCase))
                        {
                            var a = g.AoCalibration;
                            return new Dictionary<string, object> { { "calibrated", a != null }, { "xAngle", a != null ? a.XAngleDeg : 0 }, { "xRate", a != null ? a.XPxPerStep : 0 }, { "yAngle", a != null ? a.YAngleDeg : 0 }, { "yRate", a != null ? a.YPxPerStep : 0 } };
                        }
                        var c = g.Calibration;
                        return new Dictionary<string, object>
                        {
                            { "calibrated", c != null }, { "xAngle", c != null ? c.RaAngleDeg : 0 }, { "xRate", c != null ? c.RaRatePxPerSec : 0 },
                            { "xParity", "+" }, { "yAngle", c != null ? c.DecAngleDeg : 0 }, { "yRate", c != null ? c.DecRatePxPerSec : 0 },
                            { "yParity", "+" }, { "declination", c != null && !double.IsNaN(c.DecDeg) ? c.DecDeg : 0 }
                        };
                    }
                case "clear_calibration":
                    {
                        string which = Convert.ToString(Param(prms, "which", 0) ?? "both").ToLowerInvariant();
                        if (which == "mount" || which == "both") g.ClearCalibration();
                        if (which == "ao" || which == "both") g.ClearAoCalibration();
                        return 0;
                    }
                // the Sidralune guider turns its calibration after a meridian flip by itself
                case "flip_calibration": return 0;
                case "get_dec_guide_mode": return st.DecMode.ToString();
                case "set_dec_guide_mode":
                    {
                        DecGuideMode mode;
                        if (!Enum.TryParse(Convert.ToString(Param(prms, "mode", 0)), true, out mode)) throw new InvalidOperationException("Off, Auto, North or South");
                        st.DecMode = mode; g.SaveSettings();
                        return 0;
                    }
                case "get_guide_output_enabled": return g.GuideOutputEnabled;
                case "set_guide_output_enabled": g.GuideOutputEnabled = B(Param(prms, "enabled", 0), true); return 0;
                case "guide_pulse":
                    {
                        int amount = (int)D(Param(prms, "amount", 0), 0);
                        string dir = Convert.ToString(Param(prms, "direction", 1) ?? "").ToLowerInvariant();
                        string which = Convert.ToString(Param(prms, "which", 2) ?? "Mount");
                        if (which.Equals("AO", StringComparison.OrdinalIgnoreCase))
                        {
                            var ao = _dm.Ao;
                            if (ao == null) throw new InvalidOperationException(Lang.T("No AO connected."));
                            var ad = dir == "north" || dir == "up" ? AoDirection.North : dir == "south" || dir == "down" ? AoDirection.South : dir == "east" || dir == "left" ? AoDirection.East : AoDirection.West;
                            ao.Step(ad, Math.Abs(amount));
                            return 0;
                        }
                        var gd = dir == "north" || dir == "n" || dir == "up" ? GuideDirection.North : dir == "south" || dir == "s" || dir == "down" ? GuideDirection.South
                               : dir == "east" || dir == "e" || dir == "left" ? GuideDirection.East : GuideDirection.West;
                        var pulser = new GuidePulser(_dm, st.Output);
                        var problem = pulser.Problem();
                        if (problem != null) throw new InvalidOperationException(problem);
                        pulser.Start(gd, Math.Abs(amount));
                        return 0;
                    }
                case "capture_single_frame":
                    {
                        if (g.IsRunning) throw new InvalidOperationException(Lang.T("Stop looping first."));
                        double ms = D(Param(prms, "exposure", 0), g.CurrentExposure * 1000);
                        Task.Run(async () => { try { await g.CaptureSingleFrame(ms / 1000, CancellationToken.None); } catch (Exception ex) { Broadcast(Event("Alert", "Msg", ex.Message, "Type", "error")); } });
                        return 0;
                    }
                case "get_star_image": return StarImage(g, (int)D(Param(prms, "size", 0), 15));
                case "get_algo_param_names":
                    {
                        string axis = Convert.ToString(Param(prms, "axis", 0) ?? "ra").ToLowerInvariant();
                        return axis.StartsWith("d") || axis == "y" ? new object[] { "algorithmName", "minMove", "aggression" } : new object[] { "algorithmName", "minMove", "aggression", "hysteresis" };
                    }
                case "get_algo_param":
                case "set_algo_param":
                    return AlgoParam(st, method == "set_algo_param", Convert.ToString(Param(prms, "axis", 0) ?? "ra").ToLowerInvariant(), Convert.ToString(Param(prms, "name", 1)), Param(prms, "value", 2), g);
            }
            throw new MissingMethodException(method);
        }

        private object AlgoParam(GuiderSettings st, bool set, string axis, string name, object value, SidraluneGuider g)
        {
            bool dec = axis.StartsWith("d") || axis == "y";
            double v = D(value, double.NaN);
            switch (name)
            {
                case "algorithmName": return (dec ? st.DecAlgorithm : st.RaAlgorithm).ToString();
                case "minMove":
                    if (set && !double.IsNaN(v)) { if (dec) st.DecMinMovePx = v; else st.RaMinMovePx = v; g.SaveSettings(); return 0; }
                    return dec ? st.DecMinMovePx : st.RaMinMovePx;
                case "aggression":
                case "aggressiveness":
                    if (set && !double.IsNaN(v)) { if (dec) st.DecAggressiveness = v; else st.RaAggressiveness = v; g.SaveSettings(); return 0; }
                    return dec ? st.DecAggressiveness : st.RaAggressiveness;
                case "hysteresis":
                    if (set && !double.IsNaN(v)) { st.RaHysteresis = v; g.SaveSettings(); return 0; }
                    return st.RaHysteresis;
            }
            throw new InvalidOperationException("Unknown parameter " + name);
        }

        /// <summary>The pixels around the guide star, as 16-bit values in base64, the way PHD2 sends them.</summary>
        private static object StarImage(SidraluneGuider g, int size)
        {
            var f = g.LastFrame;
            var s = g.Star;
            if (f == null || s == null) throw new InvalidOperationException(Lang.T("No guide star."));
            int half = Math.Max(7, size / 2), cx = (int)Math.Round(s.X), cy = (int)Math.Round(s.Y);
            int x0 = Math.Max(0, cx - half), y0 = Math.Max(0, cy - half), x1 = Math.Min(f.Width, cx + half + 1), y1 = Math.Min(f.Height, cy + half + 1);
            int w = x1 - x0, h = y1 - y0;
            var bytes = new byte[w * h * 2];
            var d = f.Luminance();
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int v = (int)Math.Round(Math.Max(0, Math.Min(1, d[(y + y0) * f.Width + x + x0])) * 65535);
                    bytes[(y * w + x) * 2] = (byte)(v & 0xff);
                    bytes[(y * w + x) * 2 + 1] = (byte)(v >> 8);
                }
            return new Dictionary<string, object>
            {
                { "frame", g.State }, { "width", w }, { "height", h }, { "star_pos", new object[] { s.X - x0, s.Y - y0 } }, { "pixels", Convert.ToBase64String(bytes) }
            };
        }

        private static object Equipment(IDevice d) =>
            d == null ? null : new Dictionary<string, object> { { "name", d.Info != null ? d.Info.Name : "" }, { "connected", d.Connected } };

        private void BeginSettle()
        {
            _settling = true;
            _settleStart = DateTime.UtcNow;
            Broadcast(Event("SettleBegin"));
        }

        private void EndSettle(bool ok, string error)
        {
            _settling = false;
            var m = Event("SettleDone", "Status", ok ? 0 : 1, "TotalFrames", _frame, "DroppedFrames", 0);
            if (!ok) m["Error"] = error ?? Lang.T("Settling failed.");
            Broadcast(m);
        }

        private sealed class Client
        {
            private readonly TcpClient _tcp;
            private readonly StreamWriter _writer;
            public readonly StreamReader Reader;

            public Client(TcpClient tcp)
            {
                _tcp = tcp;
                var ns = tcp.GetStream();
                Reader = new StreamReader(ns, Encoding.UTF8);
                _writer = new StreamWriter(ns, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\r\n" };
            }

            public bool Send(string line)
            {
                try { lock (_writer) _writer.WriteLine(line); return true; }
                catch { return false; }
            }

            public void Close() { try { _tcp.Close(); } catch { } }
        }
    }
}
