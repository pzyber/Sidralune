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
using Sidralune.Core.Util;

namespace Sidralune.Devices.Guiding
{
    /// <summary>PHD2 event-server client (JSON-RPC 2.0 over TCP, port 4400 + instance - 1).</summary>
    public class Phd2Client : DeviceBase, IGuider
    {
        private TcpClient _tcp;
        private StreamReader _reader;
        private StreamWriter _writer;
        private Thread _thread;
        private int _id;
        private readonly ConcurrentDictionary<int, TaskCompletionSource<object>> _pending = new ConcurrentDictionary<int, TaskCompletionSource<object>>();
        private readonly List<GuideStep> _history = new List<GuideStep>();
        private TaskCompletionSource<bool> _settle;
        public string Host = "localhost";
        public int Instance = 1;
        public string State { get; private set; } = "Stopped";
        public double PixelScale { get; private set; } = 1;
        public event Action<GuideStep> Step;

        public Phd2Client() { Info = new DeviceInfo { Kind = DeviceKind.Guider, Protocol = DeviceProtocol.Phd2, Name = "PHD2", Address = "localhost:4400" }; }

        public bool IsGuiding => State == "Guiding";
        public double RmsRaArcsec => Rms(s => s.RaArcsec);
        public double RmsDecArcsec => Rms(s => s.DecArcsec);
        public double RmsTotalArcsec => Math.Sqrt(RmsRaArcsec * RmsRaArcsec + RmsDecArcsec * RmsDecArcsec);

        public List<GuideStep> History { get { lock (_history) return new List<GuideStep>(_history); } }

        private double Rms(Func<GuideStep, double> f)
        {
            lock (_history)
            {
                var recent = _history.Skip(Math.Max(0, _history.Count - 100)).ToList();
                return recent.Count == 0 ? double.NaN : Math.Sqrt(recent.Average(s => f(s) * f(s)));
            }
        }

        public override void Connect()
        {
            _tcp = new TcpClient(Host, 4400 + Instance - 1);
            var ns = _tcp.GetStream();
            _reader = new StreamReader(ns, Encoding.UTF8);
            _writer = new StreamWriter(ns, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\r\n" };
            _thread = new Thread(ReadLoop) { IsBackground = true, Name = "PHD2" };
            _thread.Start();
            Connected = true;
            Task.Run(async () =>
            {
                try { PixelScale = Convert.ToDouble(await Call("get_pixel_scale"), CultureInfo.InvariantCulture); } catch { }
            });
        }

        public override void Disconnect() { try { _tcp?.Close(); } catch { } Connected = false; }

        private void ReadLoop()
        {
            try
            {
                string line;
                while ((line = _reader.ReadLine()) != null)
                {
                    Dictionary<string, object> msg;
                    try { msg = Json.ParseObject(line); } catch { continue; }
                    object ev;
                    if (msg.TryGetValue("Event", out ev)) HandleEvent((string)ev, msg);
                    else if (msg.ContainsKey("id"))
                    {
                        int id = Convert.ToInt32(msg["id"]);
                        TaskCompletionSource<object> tcs;
                        if (_pending.TryRemove(id, out tcs))
                        {
                            object err;
                            if (msg.TryGetValue("error", out err) && err != null) tcs.TrySetException(new InvalidOperationException("PHD2: " + Json.GetString(err, "message")));
                            else { object r; msg.TryGetValue("result", out r); tcs.TrySetResult(r); }
                        }
                    }
                }
            }
            catch { }
            Connected = false;
            // nothing more will arrive: fail the calls and the settle wait still outstanding instead of leaving them hanging
            foreach (var id in _pending.Keys.ToList())
            {
                TaskCompletionSource<object> tcs;
                if (_pending.TryRemove(id, out tcs)) tcs.TrySetException(new IOException("PHD2 connection closed"));
            }
            _settle?.TrySetException(new IOException("PHD2 connection closed"));
        }

        private void HandleEvent(string ev, Dictionary<string, object> m)
        {
            switch (ev)
            {
                case "AppState": State = Json.GetString(m, "State"); RaiseStateChanged(); break;
                case "GuideStep":
                    {
                        State = "Guiding";
                        var s = new GuideStep
                        {
                            Time = DateTime.Now,
                            RaDistance = Json.GetDouble(m, "RADistanceRaw", 0), DecDistance = Json.GetDouble(m, "DECDistanceRaw", 0),
                            RaPulse = (int)Json.GetDouble(m, "RADuration", 0), DecPulse = (int)Json.GetDouble(m, "DECDuration", 0),
                            Snr = Json.GetDouble(m, "SNR", double.NaN), StarMass = Json.GetDouble(m, "StarMass", double.NaN)
                        };
                        s.RaArcsec = s.RaDistance * PixelScale; s.DecArcsec = s.DecDistance * PixelScale;
                        lock (_history) { _history.Add(s); if (_history.Count > 5000) _history.RemoveRange(0, 1000); }
                        Step?.Invoke(s);
                        break;
                    }
                case "SettleDone":
                    {
                        int status = (int)Json.GetDouble(m, "Status", 1);
                        _settle?.TrySetResult(status == 0);
                        break;
                    }
                case "StartGuiding": State = "Guiding"; RaiseStateChanged(); break;
                case "GuidingStopped": State = "Stopped"; RaiseStateChanged(); break;
                case "Paused": State = "Paused"; RaiseStateChanged(); break;
                case "StarLost": State = "LostLock"; RaiseStateChanged(); break;
            }
        }

        public Task<object> Call(string method, object parameters = null)
        {
            if (_writer == null || !Connected) throw new InvalidOperationException("PHD2 is not connected");
            int id = Interlocked.Increment(ref _id);
            var tcs = new TaskCompletionSource<object>();
            _pending[id] = tcs;
            var req = new Dictionary<string, object> { { "method", method }, { "id", id } };
            if (parameters != null) req["params"] = parameters;
            try { lock (_writer) _writer.WriteLine(Json.Serialize(req)); }
            catch { TaskCompletionSource<object> t; _pending.TryRemove(id, out t); throw; }
            return tcs.Task;
        }

        /// <summary>A call's reply, or cancellation (the reply may never come if PHD2 hangs).</summary>
        private static async Task<object> WithCancel(Task<object> call, CancellationToken ct)
        {
            var cancelled = new TaskCompletionSource<object>();
            using (ct.Register(() => cancelled.TrySetCanceled()))
                return await await Task.WhenAny(call, cancelled.Task);
        }

        private static Dictionary<string, object> Settle(double pixels, double time, double timeout) =>
            new Dictionary<string, object> { { "pixels", pixels }, { "time", time }, { "timeout", timeout } };

        public async Task StartGuiding(bool recalibrate, CancellationToken ct)
        {
            _settle = new TaskCompletionSource<bool>();
            await WithCancel(Call("guide", new Dictionary<string, object> { { "settle", Settle(1.5, 10, 120) }, { "recalibrate", recalibrate } }), ct);
            using (ct.Register(() => _settle.TrySetCanceled()))
                if (!await _settle.Task) throw new InvalidOperationException("PHD2 failed to settle after starting guiding");
        }

        public void StopGuiding() { Call("stop_capture"); }

        public async Task Dither(double pixels, bool raOnly, double settlePixels, double settleTime, double timeout, CancellationToken ct)
        {
            _settle = new TaskCompletionSource<bool>();
            await WithCancel(Call("dither", new Dictionary<string, object> { { "amount", pixels }, { "raOnly", raOnly }, { "settle", Settle(settlePixels, settleTime, timeout) } }), ct);
            using (ct.Register(() => _settle.TrySetCanceled()))
                if (!await _settle.Task) Log.Warn("PHD2", "Dither settle timed out");
        }

        public void Pause(bool paused) { Call("set_paused", new object[] { paused }); }
    }
}
