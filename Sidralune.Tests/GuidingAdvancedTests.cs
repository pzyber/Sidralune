using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Sidralune.Core.Catalogs;
using Sidralune.Core.Coordinates;
using Sidralune.Core.Mathematics;
using Sidralune.Devices;
using Sidralune.Devices.Automation;
using Sidralune.Devices.Guiding;
using Sidralune.Devices.Simulators;
using Sidralune.Imaging;
using Sidralune.Science.Astrometry;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Sidralune.Tests.Checks;

namespace Sidralune.Tests
{
    /// <summary>
    /// The Sidralune guider's advanced parts: the matrix routines and Predictive PEC, subframes and noise reduction, the
    /// rotator, adaptive optics, the PHD2-compatible server and polar alignment with the guide camera.
    /// </summary>
    [TestClass]
    public class GuidingAdvancedTests : TestBase
    {
        private static double Gauss(Random r) => Math.Sqrt(-2 * Math.Log(1 - r.NextDouble())) * Math.Cos(2 * Math.PI * r.NextDouble());

        [TestMethod]
        public void TestCholesky()
        {
            var rng = new Random(2);
            int n = 30;
            var B = new double[n, n];
            for (int i = 0; i < n; i++) for (int j = 0; j < n; j++) B[i, j] = rng.NextDouble() - 0.5;
            // A = B Bᵀ + n I is symmetric positive definite
            var A = new double[n, n];
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                {
                    double s = i == j ? n : 0;
                    for (int k = 0; k < n; k++) s += B[i, k] * B[j, k];
                    A[i, j] = s;
                }
            var b = Enumerable.Range(0, n).Select(i => rng.NextDouble()).ToArray();
            var x1 = LinAlg.CholeskySolve(A, b);
            var x2 = LinAlg.Solve(A, b);
            CheckTrue("Cholesky solves like Gaussian elimination", x1 != null && Enumerable.Range(0, n).All(i => Math.Abs(x1[i] - x2[i]) < 1e-10));
            var L = LinAlg.Cholesky(A);
            double worst = 0;
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                {
                    double s = 0;
                    for (int k = 0; k < n; k++) s += L[i, k] * L[j, k];
                    worst = Math.Max(worst, Math.Abs(s - A[i, j]));
                }
            Check("L Lᵀ gives back the matrix", worst, 0, 1e-9);
            // a kernel matrix with two equal points is singular: the jitter makes it solvable
            var pts = new[] { 0.0, 1, 1, 2 };
            var K = LinAlg.KernelMatrix(pts, pts, (p, q) => Math.Exp(-(p - q) * (p - q)));
            CheckTrue("A nearly singular kernel matrix is still factorised", LinAlg.Cholesky(K) != null);
            CheckTrue("…and a negative definite one is refused", LinAlg.Cholesky(new double[,] { { -1, 0 }, { 0, -1 } }) == null);
        }

        /// <summary>A mount with periodic error guided by one algorithm, one frame a second: the RMS of where the star really was.</summary>
        private static double PecLoop(IAxisAlgorithm alg, double amplitude, double period, double noise, int frames, int measureFrom, int seed, Func<int, double> drift = null)
        {
            var rng = new Random(seed);
            double corrected = 0, ss = 0;
            int n = 0;
            for (int i = 0; i < frames; i++)
            {
                double gear = amplitude * Math.Sin(2 * Math.PI * i / period) + 0.3 * amplitude * Math.Sin(4 * Math.PI * i / period + 1) + (drift != null ? drift(i) : 0);
                double p = gear - corrected;
                double c = alg.Correction(p + noise * Gauss(rng));
                alg.Sent(c);
                corrected += c;
                if (i >= measureFrom) { double after = gear - corrected; ss += after * after; n++; }
            }
            return Math.Sqrt(ss / n);
        }

        [TestMethod]
        public void TestPredictivePec()
        {
            var origin = new DateTime(2026, 11, 24, 20, 0, 0, DateTimeKind.Utc);
            int tick = 0;
            Func<PredictivePecAlgorithm> make = () => new PredictivePecAlgorithm { PeriodSec = 120, AutoPeriod = false, MinMovePx = 0.05, Clock = () => origin.AddSeconds(tick) };

            // the model alone: fitted on two periods of noisy periodic error and drift, it predicts the next half minute
            var pec = make();
            var rng = new Random(4);
            Func<double, double> gear = t => 3 * Math.Sin(2 * Math.PI * t / 120) + 0.8 * Math.Sin(4 * Math.PI * t / 120 + 1) + 0.004 * t;
            for (tick = 0; tick <= 240; tick++) { pec.Correction(gear(tick) + 0.15 * Gauss(rng)); pec.Sent(0); }
            double[] ahead;
            var times = Enumerable.Range(1, 30).Select(k => 240.0 + k).ToArray();
            CheckTrue("The model predicts", pec.Predict(times, out ahead));
            double worst = times.Select((t, k) => Math.Abs(ahead[k] - gear(t))).Max();
            Check("Worst prediction error over the next 30 s (px)", worst, 0, 0.35);
            CheckTrue("Two periods recorded: predicting", pec.Predicting);

            // guiding: after training, predicting beats Hysteresis on periodic error
            tick = 0;
            var guided = make();
            var hy = new HysteresisAlgorithm { MinMovePx = 0.05, Hysteresis = 0.1, Aggressiveness = 0.7 };
            int frames = 600;
            double pecRms = 0, hyRms;
            {
                var r = new Random(7);
                double corrected = 0, ss = 0; int n = 0;
                for (tick = 0; tick < frames; tick++)
                {
                    double g = 8 * Math.Sin(2 * Math.PI * tick / 120) + 2.4 * Math.Sin(4 * Math.PI * tick / 120 + 1);
                    double c = guided.Correction(g - corrected + 0.1 * Gauss(r));
                    guided.Sent(c); corrected += c;
                    if (tick >= 360) { ss += (g - corrected) * (g - corrected); n++; }
                }
                pecRms = Math.Sqrt(ss / n);
            }
            hyRms = PecLoop(hy, 8, 120, 0.1, frames, 360, 7);
            Info(string.Format("Periodic error 8 px over 120 s: Predictive PEC {0:0.000} px RMS, Hysteresis {1:0.000} px", pecRms, hyRms));
            CheckTrue("Predictive PEC guides periodic error at less than half Hysteresis's RMS", pecRms < 0.5 * hyRms, pecRms.ToString("0.000") + " vs " + hyRms.ToString("0.000"));

            // the period: from a wrong start it is found
            tick = 0;
            var learner = new PredictivePecAlgorithm { PeriodSec = 100, AutoPeriod = true, MinMovePx = 0.05, Clock = () => origin.AddSeconds(tick) };
            var rr = new Random(9);
            for (tick = 0; tick < 2400; tick++) { learner.Correction(gear(tick) + 0.15 * Gauss(rr)); learner.Sent(0); }
            Check("The period found from a start of 100 s (s)", learner.PeriodSec, 120, 3.6);

            // the model is kept through a lock move and a short pause, and forgotten after a long one
            tick = 0;
            var keeper = make();
            for (tick = 0; tick < 300; tick++) { keeper.Correction(gear(tick)); keeper.Sent(0); }
            keeper.LockMoved(5);
            keeper.Correction(gear(300) - 5);
            double[] after;
            keeper.Predict(new[] { 330.0 }, out after);
            Check("After a lock move of 5 px the model still knows the gear error (px)", after[0], gear(330), 0.4);
            CheckTrue("…and is still predicting", keeper.Predicting);
            tick = 330;
            keeper.Correction(gear(330));
            tick = 400; // 70 s on: more than 40 % of the period
            keeper.Correction(gear(400));
            CheckTrue("A stop longer than the model is kept starts it again", !keeper.Predicting && keeper.Span < 1);
            keeper.Forget();
            CheckTrue("Forget empties it", keeper.Span == 0);
        }

        private static bool Spin(Func<bool> cond, int ms) => GuidingTests.SpinUntil(cond, ms);
        /// <summary>The RMS of guide steps (arcsec), NaN without any (a check on it then fails instead of the test throwing).</summary>
        private static double Rms(IEnumerable<GuideStep> steps)
        {
            var list = steps.ToList();
            return list.Count == 0 ? double.NaN : Math.Sqrt(list.Average(s => s.RaArcsec * s.RaArcsec + s.DecArcsec * s.DecArcsec));
        }

        [TestMethod]
        public void TestAdaptiveOptics()
        {
            var dm = GuidingTests.GuideObservatory("guide-ao");
            dm.Connect(new DeviceInfo { Kind = DeviceKind.AdaptiveOptics, Protocol = DeviceProtocol.Simulator, Name = "AO", Address = "sim" });
            var ao = (AoSimulator)dm.Ao;
            var tel = (TelescopeSimulator)dm.Telescope;
            var g = dm.BuiltinGuider;
            var ct = CancellationToken.None;
            try
            {
                CheckTrue("The AO steps", ao.Step(AoDirection.West, 10) && ao.PositionX == 10 && ao.Step(AoDirection.South, 4) && ao.PositionY == -4);
                CheckTrue("…and stops at its travel, saying so", !ao.Step(AoDirection.West, 100) && ao.PositionX == ao.MaxSteps);
                ao.Center();
                CheckTrue("Centring brings it back", ao.PositionX == 0 && ao.PositionY == 0);
                CheckTrue("The simulated guide camera sees the AO", ((CameraSimulator)dm.GuideCamera).Ao == ao);

                Centering.Slew(tel, tel.SiderealTime - 0.5, 30, ct).Wait();
                g.StartGuiding(false, ct).Wait();
                var ac = g.AoCalibration;
                double perStep = ao.StepArcsec / g.PixelScale;
                CheckTrue("Starting to guide with an AO calibrates the mount, then the AO", g.Calibration != null && ac != null && g.IsGuiding);
                Info(string.Format("AO calibration: X {0:0.0}° {1:0.000} px/step, Y {2:0.0}° {3:0.000} px/step (simulated 5°, 95°, {4:0.000})", ac.XAngleDeg, ac.XPxPerStep, ac.YAngleDeg, ac.YPxPerStep, perStep));
                Check("AO X direction (°)", AstroMath.NormalizeDeg180(ac.XAngleDeg - 5), 0, 3);
                Check("AO Y direction (°)", AstroMath.NormalizeDeg180(ac.YAngleDeg - 95), 0, 3);
                Check("AO pixels per step", ac.XPxPerStep, perStep, 0.1 * perStep);

                // a fast drift: the AO follows it, nears its limit, and bumps bring it back
                tel.DriftRaArcsecPerMin = 120;
                int from = g.History.Count, maxX = 0;
                bool bumped = false;
                var sw = Stopwatch.StartNew();
                while (sw.Elapsed.TotalSeconds < 20) { maxX = Math.Max(maxX, Math.Abs(ao.PositionX)); bumped |= g.Bumping; Thread.Sleep(50); }
                double rms = Rms(g.History.Skip(from + 5));
                Info(string.Format("With 2″/s of drift: RMS {0:0.00}″, AO at most {1} steps", rms, maxX));
                CheckTrue("Bumps take over from the AO near its limit", bumped && maxX <= ao.MaxSteps);
                // a drift of 2″ a second, far more than a mount drifts: slower frames on a busy machine let it run further between corrections
                CheckTrue("…and the AO holds the star meanwhile", rms < 2, rms.ToString("0.00"));
                tel.DriftRaArcsecPerMin = 0;
                CheckTrue("When the drift stops the AO comes back from its limit", Spin(() => Math.Abs(ao.PositionX) < 0.8 * ao.MaxSteps && !g.Bumping, 20000), ao.PositionX.ToString());

                // a dither with a bump afterwards leaves the AO near its centre
                g.Settings.AoBumpOnDither = true;
                g.Dither(3, false, 0.6, 1, 30, ct).Wait();
                CheckTrue("After a dither with a bump the AO is near its centre", Math.Abs(ao.PositionX) <= 8 && Math.Abs(ao.PositionY) <= 8, ao.PositionX + ", " + ao.PositionY);
            }
            finally
            {
                g.StopGuiding();
                dm.DisconnectAll();
            }
        }

        [TestMethod]
        public void TestRotatorCalibration()
        {
            var dm = GuidingTests.GuideObservatory("guide-rotator");
            dm.Connect(new DeviceInfo { Kind = DeviceKind.Rotator, Protocol = DeviceProtocol.Simulator, Name = "Rotator", Address = "sim" });
            var rot = (RotatorSimulator)dm.Rotator;
            rot.DegPerSec = 90;
            var cam = (CameraSimulator)dm.GuideCamera;
            cam.OnRotator = true;
            var tel = (TelescopeSimulator)dm.Telescope;
            var g = dm.BuiltinGuider;
            g.Settings.GuideCameraOnRotator = true;
            var ct = CancellationToken.None;
            try
            {
                Centering.Slew(tel, tel.SiderealTime - 0.5, 30, ct).Wait();
                g.StartGuiding(false, ct).Wait();
                var cal = g.Calibration;
                CheckTrue("The calibration notes the rotator's angle", cal.RotatorDeg == 0);
                rot.MoveAbsolute(90);
                CheckTrue("The rotator turns", Spin(() => !rot.IsMoving, 5000) && Math.Abs(rot.Position - 90) < 0.01);
                int from = g.History.Count;
                Spin(() => g.History.Count >= from + 20, 20000);
                double rms = Rms(g.History.Skip(from + 8));
                Info(string.Format("RMS after turning the guide camera 90°: {0:0.00}″", rms));
                CheckTrue("Guiding goes on after a 90° turn, with a guide star chosen again, on the same calibration", g.IsGuiding && g.Calibration.When == cal.When && rms < 1.2, rms.ToString("0.00"));
                Check("…turned with the rotator (°)", cal.Rotated(), 90, 0.01);
            }
            finally
            {
                g.StopGuiding();
                dm.DisconnectAll();
            }
        }

        /// <summary>A client speaking PHD2's protocol: requests by id, and every event kept in order.</summary>
        private sealed class RpcClient : IDisposable
        {
            private readonly System.Net.Sockets.TcpClient _tcp;
            private readonly StreamWriter _w;
            private int _id;
            public readonly System.Collections.Concurrent.ConcurrentQueue<Dictionary<string, object>> Lines = new System.Collections.Concurrent.ConcurrentQueue<Dictionary<string, object>>();
            public readonly List<Dictionary<string, object>> Seen = new List<Dictionary<string, object>>();

            public RpcClient(int port)
            {
                _tcp = new System.Net.Sockets.TcpClient("127.0.0.1", port);
                var ns = _tcp.GetStream();
                var r = new StreamReader(ns);
                _w = new StreamWriter(ns) { AutoFlush = true, NewLine = "\r\n" };
                new Thread(() => { try { string l; while ((l = r.ReadLine()) != null) Lines.Enqueue(Sidralune.Core.Util.Json.ParseObject(l)); } catch { } }) { IsBackground = true }.Start();
            }

            private int _eventCursor;

            /// <summary>
            /// The first message from Seen[from] on that matches, reading more for up to ms; everything read is kept in
            /// Seen, so an event that came before a reply is still found.
            /// </summary>
            private int Find(Func<Dictionary<string, object>, bool> match, int from, int ms)
            {
                var sw = Stopwatch.StartNew();
                int i = from;
                while (true)
                {
                    Dictionary<string, object> m;
                    while (Lines.TryDequeue(out m)) Seen.Add(m);
                    for (; i < Seen.Count; i++) if (match(Seen[i])) return i;
                    if (sw.ElapsedMilliseconds >= ms) return -1;
                    Thread.Sleep(10);
                }
            }

            public Dictionary<string, object> Wait(Func<Dictionary<string, object>, bool> match, int ms)
            {
                int i = Find(match, 0, ms);
                return i < 0 ? null : Seen[i];
            }

            /// <summary>The next event of that name after the last one waited for.</summary>
            public Dictionary<string, object> WaitEvent(string name, int ms = 10000)
            {
                int i = Find(m => m.ContainsKey("Event") && (string)m["Event"] == name, _eventCursor, ms);
                if (i < 0) return null;
                _eventCursor = i + 1;
                return Seen[i];
            }

            public Dictionary<string, object> Call(string method, object prms = null, int ms = 10000)
            {
                int id = ++_id;
                var req = new Dictionary<string, object> { { "method", method }, { "id", id } };
                if (prms != null) req["params"] = prms;
                _w.WriteLine(Sidralune.Core.Util.Json.Serialize(req));
                return Wait(m => m.ContainsKey("id") && m["id"] != null && Convert.ToInt32(m["id"]) == id, ms);
            }

            public void Dispose() { try { _tcp.Close(); } catch { } }
        }

        private static Dictionary<string, object> Settle(double px, double t, double timeout) =>
            new Dictionary<string, object> { { "pixels", px }, { "time", t }, { "timeout", timeout } };

        [TestMethod]
        public void TestGuiderServer()
        {
            var dm = GuidingTests.GuideObservatory("guide-server");
            var tel = (TelescopeSimulator)dm.Telescope;
            var g = dm.BuiltinGuider;
            var ct = CancellationToken.None;
            Centering.Slew(tel, tel.SiderealTime - 0.5, 30, ct).Wait();
            var free = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            free.Start(); int port = ((System.Net.IPEndPoint)free.LocalEndpoint).Port; free.Stop();
            var server = new GuiderServer(dm);
            CheckTrue("The server listens", server.Start(port) && server.Running);
            var second = new GuiderServer(dm);
            CheckTrue("A second server on the same port says the port is taken", !second.Start(port) && second.Problem != null && second.Problem.Contains(port.ToString()), second.Problem);
            try
            {
                using (var c = new RpcClient(port))
                {
                    var version = c.WaitEvent("Version", 3000);
                    CheckTrue("It introduces itself like PHD2, as Sidralune", version != null && (string)version["PHDSubver"] == "Sidralune" && version.ContainsKey("PHDVersion"));
                    var state = c.WaitEvent("AppState", 3000);
                    CheckTrue("…and gives the state", state != null && (string)state["State"] == "Stopped");
                    var scale = c.Call("get_pixel_scale");
                    Check("get_pixel_scale", Convert.ToDouble(scale["result"]), g.PixelScale, 1e-6);
                    var unknown = c.Call("no_such_method");
                    CheckTrue("An unknown method answers \"method not found\"", unknown != null && unknown.ContainsKey("error") && Convert.ToInt32(((Dictionary<string, object>)unknown["error"])["code"]) == -32601);
                    CheckTrue("loop", c.Call("loop") != null && c.WaitEvent("LoopingExposures") != null);
                    Spin(() => g.LastFrame != null, 5000);
                    var star = c.Call("find_star");
                    CheckTrue("find_star gives the star", star != null && star["result"] is System.Collections.IList && Math.Abs(Convert.ToDouble(((System.Collections.IList)star["result"])[0]) - g.Star.X) < 1e-6);
                    var guide = c.Call("guide", new Dictionary<string, object> { { "settle", Settle(1.5, 1, 60) }, { "recalibrate", false } });
                    CheckTrue("guide answers at once", guide != null && Convert.ToInt32(guide["result"]) == 0);
                    CheckTrue("…then settling begins", c.WaitEvent("SettleBegin", 3000) != null);
                    var done = c.WaitEvent("SettleDone", 90000);
                    CheckTrue("…and ends settled, after calibrating", done != null && Convert.ToInt32(done["Status"]) == 0, done != null && done.ContainsKey("Error") ? (string)done["Error"] : "no SettleDone");
                    CheckTrue("Calibration and guide steps are told", c.Seen.Any(m => m.ContainsKey("Event") && (string)m["Event"] == "StartCalibration") && c.WaitEvent("GuideStep", 5000) != null);
                    var step = c.WaitEvent("GuideStep", 5000);
                    CheckTrue("A guide step has PHD2's fields", step != null && step.ContainsKey("RADistanceRaw") && step.ContainsKey("DECDuration") && step.ContainsKey("SNR"));
                    CheckTrue("get_app_state while guiding", (string)c.Call("get_app_state")["result"] == "Guiding");
                    c.Call("dither", new Dictionary<string, object> { { "amount", 3 }, { "raOnly", false }, { "settle", Settle(1.0, 1, 30) } });
                    CheckTrue("dither moves the lock position and says so", c.WaitEvent("GuidingDithered", 5000) != null);
                    var dd = c.WaitEvent("SettleDone", 40000);
                    CheckTrue("…and settles", dd != null && Convert.ToInt32(dd["Status"]) == 0);
                    c.Call("set_paused", new object[] { true });
                    CheckTrue("set_paused pauses", c.WaitEvent("Paused", 5000) != null && (bool)c.Call("get_paused")["result"]);
                    c.Call("set_paused", new object[] { false });
                    CheckTrue("…and resumes", c.WaitEvent("Resumed", 5000) != null);
                    c.Call("stop_capture");
                    CheckTrue("stop_capture stops guiding", c.WaitEvent("GuidingStopped", 5000) != null && Spin(() => g.State == SidraluneGuider.Stopped, 5000));
                }

                // Sidralune's own PHD2 client: it does not connect to this server, unless allowed to (here, to drive it end to end)
                string refused = null;
                try { new Phd2Client { Host = "127.0.0.1", Instance = port - 4399 }.Connect(); } catch (Exception ex) { refused = ex.Message; }
                CheckTrue("Connect PHD2 refuses Sidralune's own server", refused == "That is Sidralune's own PHD2-compatible server, not PHD2.", refused);
                var client = new Phd2Client { Host = "127.0.0.1", Instance = port - 4399, AllowSidralune = true };
                client.Connect();
                g.SettleSeconds = 1;
                client.StartGuiding(false, ct).Wait();
                CheckTrue("The PHD2 client starts the Sidralune guider through the server", g.IsGuiding && Spin(() => client.IsGuiding, 5000));
                client.Dither(2, false, 1.0, 1, 30, ct).Wait();
                CheckTrue("…dithers it", g.IsGuiding);
                client.StopGuiding();
                CheckTrue("…and stops it", Spin(() => g.State == SidraluneGuider.Stopped, 5000));
                client.Disconnect();
            }
            finally
            {
                server.Stop();
                g.StopGuiding();
                dm.DisconnectAll();
            }
            CheckTrue("Stopped, it no longer listens", !server.Running);
        }

        [TestMethod]
        public void TestSubframesAndNoiseReduction()
        {
            var dm = GuidingTests.GuideObservatory("guide-subframes");
            var tel = (TelescopeSimulator)dm.Telescope;
            var cam = (CameraSimulator)dm.GuideCamera;
            var g = dm.BuiltinGuider;
            var ct = CancellationToken.None;
            try
            {
                g.Settings.UseSubframes = true;
                g.Settings.MultiStar = false;
                Centering.Slew(tel, tel.SiderealTime - 0.5, 30, ct).Wait();
                g.StartGuiding(false, ct).Wait();
                CheckTrue("Guiding reads a subframe", Spin(() => g.SubframeRead != null, 5000) && cam.NumX < cam.SensorWidth / 2, cam.NumX + " of " + cam.SensorWidth);
                CheckTrue("…and the frame stays full size, with the stars in full-frame pixels", g.LastFrame.Width == cam.SensorWidth && Math.Abs(g.Star.X - g.LockX) < 3);
                int from = g.History.Count;
                Spin(() => g.History.Count >= from + 20, 15000);
                double rms = Rms(g.History.Skip(from + 3));
                CheckTrue("Guiding on subframes holds the star", rms < 1.2, rms.ToString("0.00"));
                double zp = cam.ZeroPointMag;
                cam.ZeroPointMag = 5;
                CheckTrue("A lost star goes back to full frames", Spin(() => g.Lost && g.SubframeRead == null && cam.NumX == cam.SensorWidth, 8000));
                cam.ZeroPointMag = zp;
                Spin(() => g.IsGuiding, 8000);
            }
            finally
            {
                g.StopGuiding();
                dm.DisconnectAll();
            }

            // noise reduction: a hot pixel beside a star is taken out by the median, and the star's centre hardly moves
            var stars = new[] { new Imaging.Synthetic.SynthStar { X = 60.3, Y = 50.7, Flux = 0.6 } };
            var img = Imaging.Synthetic.Render(120, 100, stars, Imaging.Transform2D.Identity, 3.0, 0.02, 0.002, 5, 0);
            var clean = GuideStars.Measure(img.Luminance(), 120, 100, 60, 51, 10);
            var plain = img.Clone();
            img.Data[0][47 * 120 + 64] = 0.9f;
            var hot = img.Clone();
            SidraluneGuider.ReduceNoise(hot, NoiseReduction.Median3x3);
            CheckTrue("The median takes out a hot pixel", hot.Data[0][47 * 120 + 64] < 0.1);
            var m = GuideStars.Measure(hot.Luminance(), 120, 100, 60, 51, 10);
            CheckTrue("…and the star's centre hardly moves", m != null && Math.Abs(m.X - clean.X) < 0.1 && Math.Abs(m.Y - clean.Y) < 0.1, m?.ToString());
            SidraluneGuider.ReduceNoise(plain, NoiseReduction.Mean2x2);
            var mm = GuideStars.Measure(plain.Luminance(), 120, 100, 60, 51, 10);
            CheckTrue("A 2×2 mean moves the star half a pixel back, the same in every frame", mm != null && Math.Abs(mm.X - clean.X + 0.5) < 0.15 && Math.Abs(mm.Y - clean.Y + 0.5) < 0.15, mm?.ToString());
        }

        [TestMethod]
        public void TestDriftAlignment()
        {
            var dm = GuidingTests.GuideObservatory("guide-drift-alignment");
            var tel = (TelescopeSimulator)dm.Telescope;
            var cam = (CameraSimulator)dm.GuideCamera;
            cam.JitterArcsec = 0;
            var g = dm.BuiltinGuider;
            var ct = CancellationToken.None;
            tel.PolarErrorAltArcmin = 10; tel.PolarErrorAzArcmin = 15;
            var da = new DriftAlignment(g, dm);
            try
            {
                // near the meridian and the equator: the azimuth error
                Centering.Slew(tel, tel.SiderealTime - 0.2, 0, ct).Wait();
                StringAssert.Contains(da.PointingAdvice(), "azimuth");
                bool needsGuiding = false;
                try { da.Start(); } catch (InvalidOperationException) { needsGuiding = true; }
                CheckTrue("Drift alignment measures while guiding", needsGuiding);
                g.StartGuiding(false, ct).Wait();
                da.Start();
                CheckTrue("Dec corrections are held back", g.DecOutputOff);
                Thread.Sleep(40000);
                var m = da.Stop();
                Info(string.Format("Azimuth: drift {0:0.00} ± {1:0.00}″/min over {2:0} s, {3} frames → {4:0.0} ± {5:0.0}′ ({6})", m.DriftArcsecPerMin, m.UncertaintyArcsecPerMin, m.Seconds, m.Frames, m.ErrorArcmin, m.UncertaintyArcmin, m.Advice));
                CheckTrue("Dec corrections start again", !g.DecOutputOff);
                CheckTrue("Near the meridian it measures the azimuth error", !m.Altitude);
                Check("Azimuth error from the drift near the meridian (′)", m.ErrorArcmin, 15, 3.75);
                StringAssert.Contains(m.Advice, "west");

                // low in the west: the altitude error, with the azimuth error measured already taken out
                g.StopGuiding();
                Centering.Slew(tel, tel.SiderealTime - 3, 0, ct).Wait();
                StringAssert.Contains(da.PointingAdvice(), "altitude");
                g.StartGuiding(false, ct).Wait();
                da.Start();
                Thread.Sleep(40000);
                m = da.Stop();
                Info(string.Format("Altitude: drift {0:0.00} ± {1:0.00}″/min → {2:0.0} ± {3:0.0}′ ({4}); both: {5}", m.DriftArcsecPerMin, m.UncertaintyArcsecPerMin, m.ErrorArcmin, m.UncertaintyArcmin, m.Advice, m.Result.Advice));
                CheckTrue("Low in the west it measures the altitude error", m.Altitude);
                Check("Altitude error from the drift low in the west (′)", m.ErrorArcmin, 10, 2.5);
                Check("…with the azimuth error kept from before (′)", m.Result.AzErrorArcmin, 15, 3.75);
                StringAssert.Contains(m.Advice, "down");
            }
            finally
            {
                da.Cancel();
                g.StopGuiding();
                dm.DisconnectAll();
            }
        }

        /// <summary>Plate-solves a guide frame against the test stars.</summary>
        private static Wcs SolveGuideFrame(SidraluneGuider g, AstroImage f)
        {
            var opt = new SolveOptions { ScaleHint = g.PixelScale, SearchRadius = 6, HintRa = 0, HintDec = 89.5 };
            var sr = PlateSolver.Solve(f, opt);
            if (!sr.Success) Info(string.Format("Frame {0}×{1} from {2:HH:mm:ss} not solved: {3} ({4} stars)", f.Width, f.Height, f.DateObs, sr.Message, sr.ImageStars));
            return sr.Success ? sr.Wcs : null;
        }

        /// <summary>Where a star of one frame is in a later one, after the polar axis was moved.</summary>
        private static double[] Moved(Wcs before, Wcs after, double x, double y)
        {
            double ra, dec, x2, y2;
            before.PixelToWorld(x, y, out ra, out dec);
            after.WorldToPixel(ra, dec, out x2, out y2);
            return new[] { x2, y2 };
        }

        /// <summary>Only catalog stars, so frames can be plate-solved: a dense field round the pole (LoadBuiltIn drops it).</summary>
        private static CatalogManager PoleField()
        {
            var cm = CatalogManager.Instance;
            var rnd = new Random(23);
            for (int i = 0; i < 16000; i++)
            {
                double dec = 90 - 4 * Math.Sqrt(rnd.NextDouble()), ra = 360 * rnd.NextDouble();
                cm.Stars.Add(new Star { Ra = ra * AstroMath.Deg2Rad, Dec = Math.Min(89.999, dec) * AstroMath.Deg2Rad, Mag = 8 + 4 * Math.Pow(rnd.NextDouble(), 0.4) });
            }
            cm.Stars.Finish();
            return cm;
        }

        [TestMethod]
        public void TestStaticAndPolarDriftAlignment()
        {
            var dm = GuidingTests.GuideObservatory("guide-static-alignment");
            var tel = (TelescopeSimulator)dm.Telescope;
            var cam = (CameraSimulator)dm.GuideCamera;
            // only catalog stars, so the frames can be plate-solved: a dense field round the pole
            cam.SyntheticFieldStars = false;
            cam.JitterArcsec = 0;
            var cm = PoleField();
            var g = dm.BuiltinGuider;
            var ct = CancellationToken.None;
            var sa = new StaticPolarAlignment(g, dm);
            var pd = new PolarDriftAlignment(g, dm);
            try
            {
                tel.PolarErrorAltArcmin = 10; tel.PolarErrorAzArcmin = 15;
                Centering.Slew(tel, tel.SiderealTime - 2, 89, ct).Wait();
                var steps = new List<string>();
                sa.Status += steps.Add;
                var r = sa.RunAutomatic(ct).Result;
                double refraction = Transforms.RefractionFromTrue(52 * AstroMath.Deg2Rad, dm.Site.PressureHpa, dm.Site.TemperatureC) * AstroMath.Rad2Deg * 60;
                Info(string.Format("Static: alt {0:0.00}′ az {1:0.00}′ (refraction {2:0.00}′), axis ({3:0}, {4:0}), pole ({5:0}, {6:0}); {7}", r.AltErrorArcmin, r.AzErrorArcmin, refraction, r.AxisX, r.AxisY, r.PoleX, r.PoleY, string.Join(" | ", steps)));
                Check("Static alignment: altitude error against the refracted pole (′)", r.AltErrorArcmin, 10 - refraction, 2);
                Check("Static alignment: azimuth error (′)", r.AzErrorArcmin, 15, 2);
                CheckTrue("A star to move, with its target in the frame", !double.IsNaN(r.StarX) && r.TargetX > 0 && r.TargetX < r.Frame.Width && r.TargetY > 0 && r.TargetY < r.Frame.Height,
                    string.Format("({0:0}, {1:0}) → ({2:0}, {3:0})", r.StarX, r.StarY, r.TargetX, r.TargetY));
                sa.StartTracking();
                CheckTrue("The star is followed", Spin(() => !double.IsNaN(sa.RemainingArcmin), 10000));
                Info(string.Format("Remaining before turning the knobs: {0:0.0}′", sa.RemainingArcmin));

                // the knobs turned: the axis on the pole; the star lands on its target (the refraction apart)
                var before = r.Wcs;
                tel.PolarErrorAltArcmin = 0; tel.PolarErrorAzArcmin = 0;
                var after = SolveGuideFrame(g, StaticPolarAlignment.NextFrame(g, DateTime.UtcNow, ct).Result);
                CheckTrue("The frame after aligning solves", after != null);
                var at = Moved(before, after, r.StarX, r.StarY);
                double miss = Math.Sqrt((at[0] - r.TargetX) * (at[0] - r.TargetX) + (at[1] - r.TargetY) * (at[1] - r.TargetY)) * r.PixelScale / 60;
                Info(string.Format("The star moved to ({0:0}, {1:0}), its target ({2:0}, {3:0}): {4:0.00}′ off", at[0], at[1], r.TargetX, r.TargetY, miss));
                CheckTrue("Aligning the axis moves the star onto its target", miss < 2, miss.ToString("0.00"));
                sa.StopTracking();

                // polar drift alignment: no calibration, no plate solving
                tel.PolarErrorAltArcmin = 10; tel.PolarErrorAzArcmin = 15;
                g.StopGuiding();
                Thread.Sleep(500);
                CheckTrue("No calibration: the image is taken as not mirrored", g.Calibration == null && PolarDriftAlignment.MirroredFromCalibration(g) == null);
                pd.Start();
                Thread.Sleep(45000);
                var frame = g.LastFrame;
                var d = pd.Stop();
                double truth = Math.Sqrt(10 * 10 + 15 * 15);
                Info(string.Format("Polar drift: {0:0.00} px/min over {1:0} s, {2} frames → {3:0.0} ± {4:0.0}′ (truth {5:0.0}′); star ({6:0}, {7:0}) → ({8:0}, {9:0})",
                    d.DriftPxPerMin, d.Seconds, d.Frames, d.ErrorArcmin, d.UncertaintyArcmin, truth, d.StarX, d.StarY, d.TargetX, d.TargetY));
                Check("Polar drift alignment finds the error (′)", d.ErrorArcmin, truth, 0.25 * truth);
                before = SolveGuideFrame(g, frame);
                tel.PolarErrorAltArcmin = 0; tel.PolarErrorAzArcmin = 0;
                after = SolveGuideFrame(g, StaticPolarAlignment.NextFrame(g, DateTime.UtcNow, ct).Result);
                CheckTrue("Both frames solve", before != null && after != null);
                at = Moved(before, after, d.StarX, d.StarY);
                double shift = Math.Sqrt((d.TargetX - d.StarX) * (d.TargetX - d.StarX) + (d.TargetY - d.StarY) * (d.TargetY - d.StarY));
                double off = Math.Sqrt((at[0] - d.TargetX) * (at[0] - d.TargetX) + (at[1] - d.TargetY) * (at[1] - d.TargetY));
                Info(string.Format("The star moved to ({0:0}, {1:0}): {2:0} px from its target, of a {3:0} px move", at[0], at[1], off, shift));
                CheckTrue("…and its target is where aligning moves the star", off < 0.3 * shift, string.Format("{0:0} of {1:0} px", off, shift));
            }
            finally
            {
                sa.StopTracking();
                pd.Cancel();
                g.StopGuiding();
                dm.DisconnectAll();
                cm.LoadBuiltIn(); // drop the test stars from the shared catalog
            }
        }

        [TestMethod]
        public void TestStaticAndPolarDriftWithTheImagingCamera()
        {
            var dm = GuidingTests.GuideObservatory("guide-static-alignment-camera");
            dm.Connect(new DeviceInfo { Kind = DeviceKind.Camera, Protocol = DeviceProtocol.Simulator, Name = "Camera sim", Address = "sim" });
            var tel = (TelescopeSimulator)dm.Telescope;
            var cam = (CameraSimulator)dm.Camera;
            cam.SyntheticFieldStars = false;
            cam.JitterArcsec = 0;
            var cm = PoleField();
            var frames = new CameraFrames(new CaptureService { Devices = dm }) { Exposure = 1, Bin = 2 };
            var cts = new CancellationTokenSource();
            var ct = CancellationToken.None;
            var sa = new StaticPolarAlignment(frames, dm);
            var pd = new PolarDriftAlignment(frames, dm);
            Task run = null;
            try
            {
                // no frames until they are taken
                frames.Ensure();
                try { frames.Next(DateTime.UtcNow, new CancellationTokenSource(300).Token).Wait(); CheckTrue("Waiting for a frame that does not come ends", false); }
                catch (AggregateException ex) { CheckTrue("Waiting for a frame that does not come can be cancelled", ex.InnerException is OperationCanceledException); }
                run = frames.Run(cts.Token);
                CheckTrue("The imaging camera's pixel scale", Math.Abs(frames.PixelScale - 206.265 * cam.PixelSizeX * 2 / dm.FocalLengthMm) < 1e-9);

                tel.PolarErrorAltArcmin = 10; tel.PolarErrorAzArcmin = 15;
                Centering.Slew(tel, tel.SiderealTime - 2, 89, ct).Wait();
                var r = sa.RunAutomatic(ct).Result;
                double refraction = Transforms.RefractionFromTrue(52 * AstroMath.Deg2Rad, dm.Site.PressureHpa, dm.Site.TemperatureC) * AstroMath.Rad2Deg * 60;
                Info(string.Format("Static with the imaging camera: alt {0:0.00}′ az {1:0.00}′ (refraction {2:0.00}′), frame {3}×{4}", r.AltErrorArcmin, r.AzErrorArcmin, refraction, r.Frame.Width, r.Frame.Height));
                Check("Static alignment with the imaging camera: altitude error (′)", r.AltErrorArcmin, 10 - refraction, 2);
                Check("Static alignment with the imaging camera: azimuth error (′)", r.AzErrorArcmin, 15, 2);
                sa.StartTracking();
                CheckTrue("The star is followed in the imaging camera's frames", Spin(() => !double.IsNaN(sa.RemainingArcmin), 15000));
                sa.StopTracking();
                CheckTrue("The guider was not used", !dm.BuiltinGuider.IsRunning);

                // polar drift alignment follows its own star in the imaging camera's frames
                pd.Start();
                CheckTrue("No calibration for the imaging camera: mirroring is said by hand", pd.MirroredFromSource() == null);
                Thread.Sleep(45000);
                var d = pd.Stop();
                double truth = Math.Sqrt(10 * 10 + 15 * 15);
                Info(string.Format("Polar drift with the imaging camera: {0:0.00} px/min over {1:0} s, {2} frames → {3:0.0} ± {4:0.0}′ (truth {5:0.0}′)",
                    d.DriftPxPerMin, d.Seconds, d.Frames, d.ErrorArcmin, d.UncertaintyArcmin, truth));
                Check("Polar drift alignment with the imaging camera finds the error (′)", d.ErrorArcmin, truth, 0.25 * truth);
                CheckTrue("After the result the star is still followed", pd.Following && !pd.Measuring && !double.IsNaN(pd.StarX));
                pd.Cancel();
                CheckTrue("…until it is stopped", !pd.Following);
            }
            finally
            {
                sa.StopTracking();
                pd.Cancel();
                cts.Cancel();
                try { run?.Wait(10000); } catch { }
                CheckTrue("The frames end when cancelled", run == null || run.IsCompleted);
                dm.DisconnectAll();
                cm.LoadBuiltIn();
            }
        }
    }
}
