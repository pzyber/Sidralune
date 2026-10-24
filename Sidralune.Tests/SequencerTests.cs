using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Sidralune.Core;
using Sidralune.Core.Catalogs;
using Sidralune.Core.Mathematics;
using Sidralune.Core.Time;
using Sidralune.Devices;
using Sidralune.Devices.Automation;
using Sidralune.Devices.Simulators;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Sidralune.Tests.Checks;

namespace Sidralune.Tests
{
    /// <summary>Every sequencer instruction, containers, error handling, triggers, the simulators and meridian-flip helpers.</summary>
    [TestClass]
    public class SequencerTests : TestBase
    {
        private static DeviceManager Observatory(params DeviceKind[] kinds)
        {
            CatalogManager.Instance.LoadBuiltIn();
            var dm = new DeviceManager();
            dm.Site.LatitudeDeg = 52; dm.Site.LongitudeDeg = 5;
            foreach (var k in kinds) dm.Connect(new DeviceInfo { Kind = k, Protocol = DeviceProtocol.Simulator, Name = k + " sim" });
            var tel = dm.Telescope as TelescopeSimulator;
            if (tel != null) tel.SlewRateDegPerSec = 90;
            return dm;
        }

        private static SequenceContext Context(DeviceManager dm, List<string> log, string folder = "sequence")
        {
            string dir = Path.Combine(Path.GetTempPath(), "SidraluneTests", folder);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            return new SequenceContext { Devices = dm, Capture = new CaptureService { Devices = dm, ImageFolder = dir }, Log = s => { lock (log) log.Add(s); } };
        }

        private static void Run(SequenceItem item, SequenceContext ctx) => item.Run(ctx, CancellationToken.None).Wait();

        private class Failing : SequenceItem
        {
            public int Runs;
            public override string Title => "Always fails";
            protected override Task Execute(SequenceContext ctx, CancellationToken ct) { Runs++; throw new InvalidOperationException("boom"); }
        }

        private class Counting : SequenceItem
        {
            public int Runs;
            public override string Title => "Counts";
            protected override Task Execute(SequenceContext ctx, CancellationToken ct) { Runs++; return Task.FromResult(0); }
        }

        private class CountingTrigger : SequenceTrigger
        {
            public int Checks;
            public override string Title => "Counts checks";
            public override Task Check(SequenceContext ctx, SequenceItem current, CancellationToken ct) { Checks++; return Task.FromResult(0); }
        }

        [TestMethod]
        public void TestContainersAndErrors()
        {
            var log = new List<string>();
            var ctx = Context(new DeviceManager(), log);
            var a = new Counting(); var b = new Counting { Enabled = false }; var fail = new Failing();
            var root = new SequenceContainer { Items = { a, fail, b, new Annotation { Text = "after the failure" } } };
            Run(root, ctx);
            CheckTrue("A failure is logged and the sequence continues", root.Status == ItemStatus.Done && fail.Status == ItemStatus.Failed && fail.Error == "boom" && log.Contains("Always fails failed: boom") && log.Contains("after the failure"));
            CheckTrue("Disabled instructions are skipped", b.Status == ItemStatus.Skipped && b.Runs == 0 && a.Runs == 1);
            // stop on error ends the container the instruction is in (the rest of a target, say); closing instructions
            // after that container, such as a park, still run
            var stopper = new Failing { StopOnError = true };
            var after = new Counting();
            var closing = new Counting();
            var inner = new SequenceContainer { Name = "Target", Items = { stopper, after } };
            var stopRoot = new SequenceContainer { Items = { inner, closing } };
            Run(stopRoot, ctx);
            CheckTrue("Stop on error skips the rest of its container", after.Runs == 0 && inner.Status == ItemStatus.Failed && inner.Error == "boom");
            CheckTrue("Instructions after that container still run", closing.Runs == 1 && stopRoot.Status == ItemStatus.Done);
            root.Reset();
            CheckTrue("Reset clears status and errors recursively", root.Status == ItemStatus.Pending && fail.Status == ItemStatus.Pending && fail.Error == null);

            var counted = new Counting();
            var loop = new LoopContainer { Condition = LoopCondition.Count, Iterations = 3, Items = { counted } };
            Run(loop, ctx);
            CheckTrue("Counted loop", loop.Completed == 3 && counted.Runs == 3 && loop.Summary == "3/3");
            var past = new LoopContainer { Condition = LoopCondition.UntilTime, UntilUtc = DateTime.UtcNow.AddMinutes(-1), Items = { new Counting() } };
            Run(past, ctx);
            CheckTrue("Loop until a time already past runs no pass", past.Completed == 0);
            var failing = new LoopContainer { Condition = LoopCondition.Forever, Items = { new Failing(), new Counting { Enabled = false } } };
            var sw = Stopwatch.StartNew();
            Run(failing, ctx);
            CheckTrue("A loop where everything fails stops after one pass", failing.Status == ItemStatus.Failed && failing.Completed == 1 && failing.Error.StartsWith("Loop stopped") && sw.Elapsed.TotalSeconds < 5);
            var forever = new LoopContainer { Condition = LoopCondition.Forever, Items = { new Counting() } };
            using (var cts = new CancellationTokenSource(2500))
            {
                bool cancelled = false;
                try { forever.Run(ctx, cts.Token).Wait(); } catch (AggregateException ex) { cancelled = ex.InnerException is OperationCanceledException; }
                CheckTrue("A forever loop runs at most one pass a second and stops on cancel", cancelled && forever.Completed >= 2 && forever.Completed <= 3 && forever.Status == ItemStatus.Pending, forever.Completed + " passes");
            }
            var noTarget = new LoopContainer { Condition = LoopCondition.UntilAltitudeBelow, Items = { new Counting() } };
            Run(noTarget, ctx);
            CheckTrue("Altitude loop without a target does not run", noTarget.Completed == 0);

            string seen = null;
            var probe = new Probe(c => seen = c.Target?.Name);
            var target = new TargetContainer { Target = new SequenceTarget { Name = "M 42", RaDeg = 83.82, DecDeg = -5.39 }, Items = { probe } };
            Run(target, ctx);
            CheckTrue("Target container sets the target for its instructions and restores it", seen == "M 42" && ctx.Target == null && target.Title == "Target: M 42" && target.Summary.Length > 0);
        }

        private class Probe : SequenceItem
        {
            private readonly Action<SequenceContext> _a;
            public Probe(Action<SequenceContext> a) { _a = a; }
            public override string Title => "Probe";
            protected override Task Execute(SequenceContext ctx, CancellationToken ct) { _a(ctx); return Task.FromResult(0); }
        }

        [TestMethod]
        public void TestMountDomeAndWaits()
        {
            var dm = Observatory(DeviceKind.Telescope, DeviceKind.Dome, DeviceKind.ObservingConditions);
            var log = new List<string>();
            var ctx = Context(dm, log);
            var tel = (TelescopeSimulator)dm.Telescope;
            ctx.Target = new SequenceTarget { Name = "Capella", RaDeg = 79.17, DecDeg = 45.998 };
            var slew = new SlewToTarget();
            Run(slew, ctx);
            var app = new SkyContext(dm.Site, AstroTime.Now).IcrsToApparent(79.17 * AstroMath.Deg2Rad, 45.998 * AstroMath.Deg2Rad);
            CheckTrue("Slew to the target's apparent position", slew.Status == ItemStatus.Done && Math.Abs(AstroMath.NormalizeDeg180((tel.RightAscension - app.RaHours) * 15)) < 0.05 && Math.Abs(tel.Declination - app.DecDeg) < 0.05, slew.Error);
            ctx.Target = null;
            var noTarget = new SlewToTarget();
            Run(noTarget, ctx);
            CheckTrue("Slew without a target fails", noTarget.Status == ItemStatus.Failed && noTarget.Error == "No target");
            var centre = new CenterTarget();
            Run(centre, ctx);
            CheckTrue("Centre without a target fails", centre.Status == ItemStatus.Failed && centre.Error == "No target");

            var park = new ParkTelescope();
            Run(park, ctx);
            CheckTrue("Park", park.Status == ItemStatus.Done && tel.AtPark && !tel.Tracking);
            var parkAgain = new ParkTelescope();
            Run(parkAgain, ctx);
            CheckTrue("Parking a parked mount is harmless", parkAgain.Status == ItemStatus.Done && tel.AtPark);
            var unpark = new ParkTelescope { Unpark = true };
            Run(unpark, ctx);
            CheckTrue("Unpark", unpark.Status == ItemStatus.Done && !tel.AtPark && tel.Tracking && unpark.Title == "Unpark telescope");

            var open = new DomeShutter { Open = true };
            var sw = Stopwatch.StartNew();
            Run(open, ctx);
            CheckTrue("Dome opens and waits for the shutter", open.Status == ItemStatus.Done && dm.Dome.Shutter == ShutterState.Open && sw.Elapsed.TotalSeconds > 2.5);

            var w = new Wait { Kind = WaitKind.Seconds, Seconds = 0.3 };
            sw.Restart();
            Run(w, ctx);
            CheckTrue("Wait seconds", sw.Elapsed.TotalSeconds >= 0.3 && sw.Elapsed.TotalSeconds < 2 && w.Summary == (0.3).ToString() + " s");
            sw.Restart();
            Run(new Wait { Kind = WaitKind.UntilTime, TimeUtc = DateTime.UtcNow.AddSeconds(-5) }, ctx);
            Run(new Wait { Kind = WaitKind.UntilSafe }, ctx);
            ctx.Target = new SequenceTarget { Name = "Polaris", RaDeg = 37.95, DecDeg = 89.26 };
            Run(new Wait { Kind = WaitKind.UntilAltitude, AltitudeDeg = 20 }, ctx);
            CheckTrue("Waits already satisfied return at once", sw.Elapsed.TotalSeconds < 1);
            ((WeatherSimulator)dm.Weather).ForceUnsafe = true;
            using (var cts = new CancellationTokenSource(800))
            {
                var unsafeWait = new Wait { Kind = WaitKind.UntilSafe };
                bool cancelled = false;
                try { unsafeWait.Run(ctx, cts.Token).Wait(); } catch (AggregateException ex) { cancelled = ex.InnerException is OperationCanceledException; }
                CheckTrue("Wait until safe keeps waiting while unsafe", cancelled);
            }
            ((WeatherSimulator)dm.Weather).ForceUnsafe = false;

            var noGuider = new StartGuiding();
            Run(noGuider, ctx);
            Run(new StopGuiding(), ctx);
            CheckTrue("Guiding without a guider is skipped", noGuider.Status == ItemStatus.Done && log.Contains("No guider connected - skipping"));
            Run(new Annotation { Text = "Hello from the sequence" }, ctx);
            CheckTrue("Note written to the log", log.Contains("Hello from the sequence"));

            var script = new RunScript { Command = "cmd.exe", Arguments = "/c exit 0" };
            Run(script, ctx);
            CheckTrue("External program", script.Status == ItemStatus.Done && script.Summary == "cmd.exe");
            var slow = new RunScript { Command = "cmd.exe", Arguments = "/c ping -n 6 127.0.0.1 >nul", TimeoutSec = 1 };
            Run(slow, ctx);
            CheckTrue("External program timeout", slow.Status == ItemStatus.Failed && slow.Error == "Script timeout");
            var missing = new RunScript { Command = "no-such-program-sidralune.exe" };
            Run(missing, ctx);
            CheckTrue("Missing program fails", missing.Status == ItemStatus.Failed);
            var close = new DomeShutter { Open = false };
            Run(close, ctx);
            CheckTrue("Dome closes", dm.Dome.Shutter == ShutterState.Closed);
            dm.DisconnectAll();
        }

        [TestMethod]
        public void TestCameraInstructions()
        {
            var dm = Observatory(DeviceKind.Telescope, DeviceKind.Focuser, DeviceKind.FilterWheel, DeviceKind.Camera);
            var log = new List<string>();
            var ctx = Context(dm, log, "sequence-camera");
            var trig = new CountingTrigger();
            ctx.Triggers.Add(trig);
            ctx.Target = new SequenceTarget { Name = "Field" };
            var tel = (TelescopeSimulator)dm.Telescope;
            Centering.Slew(tel, tel.SiderealTime, 52, CancellationToken.None).Wait();

            var take = new TakeExposures { Count = 3, Exposure = 0.2, Filter = "R", FrameType = "LIGHT" };
            Run(take, ctx);
            CheckTrue("Exposures taken and saved", take.Status == ItemStatus.Done && take.Completed == 3 && log.Count(l => l.StartsWith("Saved ")) == 3, take.Error);
            CheckTrue("Counters and current filter", ctx.TotalExposures == 3 && ctx.ExposuresSinceAutofocus == 3 && ctx.CurrentFilter == "R" && dm.FilterWheel.Names[dm.FilterWheel.Position] == "R");
            CheckTrue("Triggers checked before each exposure", trig.Checks == 3);
            CheckTrue("HFR history kept", ctx.RecentHfr.Count == 3 && ctx.RecentHfr.All(h => h > 0.5));
            var files = Directory.GetFiles(ctx.Capture.ImageFolder, "*.fit*", SearchOption.AllDirectories);
            CheckTrue("Files on disk", files.Length == 3, files.Length + " files");
            CheckTrue("Summary and title", take.Title == "Expose light frames" && take.Summary.StartsWith("3/3"));

            var dark = new TakeExposures { Count = 1, Exposure = 0.1, FrameType = "DARK" };
            Run(dark, ctx);
            CheckTrue("Dark frame", dark.Status == ItemStatus.Done && dark.Title == "Expose dark frames");

            var sf = new SwitchFilter { Filter = "Ha" };
            Run(sf, ctx);
            CheckTrue("Switch filter", sf.Status == ItemStatus.Done && ctx.CurrentFilter == "Ha" && dm.FilterWheel.Names[dm.FilterWheel.Position] == "Ha");
            var bad = new SwitchFilter { Filter = "Purple" };
            Run(bad, ctx);
            CheckTrue("Unknown filter fails", bad.Status == ItemStatus.Failed && bad.Error.Contains("Purple"));

            var cool = new CoolCamera { Temperature = -12, Minutes = 0 };
            Run(cool, ctx);
            CheckTrue("Cool camera", cool.Status == ItemStatus.Done && dm.Camera.CoolerOn && Math.Abs(dm.Camera.SetTemperature + 12) < 1e-9);
            var warm = new CoolCamera { Warm = true, Minutes = 0 };
            Run(warm, ctx);
            CheckTrue("Warm camera", warm.Status == ItemStatus.Done && !dm.Camera.CoolerOn && warm.Title == "Warm camera");

            var af = new RunAutofocus { Exposure = 0.2, Steps = 7, StepSize = 150 };
            ((FocuserSimulator)dm.Focuser).Move(((FocuserSimulator)dm.Focuser).BestFocus + 300);
            Thread.Sleep(300);
            Run(af, ctx);
            CheckTrue("Autofocus instruction", af.Status == ItemStatus.Done && !double.IsNaN(ctx.LastAutofocusHfr) && ctx.ExposuresSinceAutofocus == 0 && ctx.RecentHfr.Count == 0, af.Error);
            CheckTrue("Focuser ends near best focus", Math.Abs(dm.Focuser.Position - ((FocuserSimulator)dm.Focuser).BestFocus) < 150, dm.Focuser.Position.ToString());
            dm.DisconnectAll();

            // without devices every camera instruction fails cleanly
            var none = Context(new DeviceManager(), log, "sequence-none");
            foreach (SequenceItem it in new SequenceItem[] { new TakeExposures { Count = 1, Exposure = 0.1 }, new RunAutofocus(), new SwitchFilter() })
            {
                Run(it, none);
                CheckTrue(it.Title + " without its device fails", it.Status == ItemStatus.Failed && !string.IsNullOrEmpty(it.Error), it.Error);
            }
            var coolNone = new CoolCamera();
            Run(coolNone, none);
            CheckTrue("Cooling without a camera is skipped", coolNone.Status == ItemStatus.Done);
        }

        [TestMethod]
        public void TestTriggers()
        {
            var log = new List<string>();
            var ctx = Context(new DeviceManager(), log, "sequence-triggers");
            var hfr = new AutofocusOnHfrIncrease { PercentIncrease = 15, SampleSize = 3 };
            ctx.LastAutofocusHfr = 2.0;
            ctx.RecentHfr.AddRange(new[] { 2.05, 2.1, 2.0 });
            hfr.Check(ctx, null, CancellationToken.None).Wait();
            CheckTrue("No refocus while HFR is steady", !log.Any(l => l.Contains("refocusing")));
            ctx.RecentHfr.AddRange(new[] { 2.5, 2.6, 2.4 });
            hfr.Check(ctx, null, CancellationToken.None).Wait();
            CheckTrue("Refocus when HFR rises 15%", log.Any(l => l.StartsWith("HFR increased to " + (2.5).ToString("0.00")) && l.EndsWith("refocusing")), string.Join(" | ", log));
            var noAf = new AutofocusOnHfrIncrease();
            var fresh = Context(new DeviceManager(), log, "sequence-triggers2");
            fresh.RecentHfr.AddRange(new[] { 9.0, 9, 9, 9, 9 });
            int before = log.Count;
            noAf.Check(fresh, null, CancellationToken.None).Wait();
            CheckTrue("No refocus before the first autofocus", log.Count == before);
            CheckTrue("Trigger titles", hfr.Title == "Autofocus when HFR rises 15%" && new AutofocusEveryN { Exposures = 30 }.Title == "Autofocus every 30 exposures" && new MeridianFlipTrigger().Title.StartsWith("Meridian flip"));

            // temperature-compensating refocus: a focuser without a camera, so the autofocus it starts fails at once
            var fdm = Observatory(DeviceKind.Focuser);
            var tctx = Context(fdm, log, "sequence-temperature");
            var tempTrigger = new AutofocusOnTemperature { DeltaC = 1.5 };
            tctx.LastAutofocusTemperature = fdm.Focuser.Temperature - 0.5;
            before = log.Count;
            tempTrigger.Check(tctx, null, CancellationToken.None).Wait();
            CheckTrue("No refocus for a small temperature change", log.Count == before);
            tctx.LastAutofocusTemperature = fdm.Focuser.Temperature - 2;
            tempTrigger.Check(tctx, null, CancellationToken.None).Wait();
            CheckTrue("Refocus after a 1.5 degree change", log.Skip(before).Any(l => l == "Temperature changed - refocusing"));
            var everyN = new AutofocusEveryN { Exposures = 5 };
            tctx.ExposuresSinceAutofocus = 4;
            before = log.Count;
            everyN.Check(tctx, null, CancellationToken.None).Wait();
            CheckTrue("No refocus before N exposures", log.Count == before);
            tctx.ExposuresSinceAutofocus = 5;
            everyN.Check(tctx, null, CancellationToken.None).Wait();
            CheckTrue("Refocus after N exposures", log.Skip(before).Any(l => l.StartsWith("Autofocus failed")));
            fdm.DisconnectAll();

            var dm = Observatory(DeviceKind.Telescope, DeviceKind.Dome, DeviceKind.ObservingConditions);
            var sctx = Context(dm, log, "sequence-safety");
            var weather = (WeatherSimulator)dm.Weather;
            var tel = (TelescopeSimulator)dm.Telescope;
            var safety = new SafetyTrigger { RecheckSeconds = 0.2 };
            safety.Check(sctx, null, CancellationToken.None).Wait();
            CheckTrue("Safe: the safety trigger does nothing", !tel.AtPark && dm.Dome.Shutter == ShutterState.Closed);
            dm.Dome.OpenShutter();
            WaitShutter(dm.Dome, ShutterState.Open);
            weather.ForceUnsafe = true;
            var run = safety.Check(sctx, null, CancellationToken.None);
            bool parked = SpinUntil(() => tel.AtPark && dm.Dome.Shutter == ShutterState.Closed, 15000);
            CheckTrue("Unsafe: parks and closes the dome", parked && !run.IsCompleted);
            weather.ForceUnsafe = false;
            bool resumed = run.Wait(15000);
            CheckTrue("Safe again: opens the dome and unparks", resumed && !run.IsFaulted && dm.Dome.Shutter == ShutterState.Open && !tel.AtPark && log.Contains("Conditions safe again: resuming"));
            CheckTrue("Meridian flip not due right after a slew to the west", !MeridianFlip.IsDue(tel) && MeridianFlip.MinutesPastMeridian(tel, AstroMath.NormalizeHours(tel.SiderealTime - 0.5)) > 29.9);
            dm.DisconnectAll();
        }

        private static bool SpinUntil(Func<bool> cond, int ms)
        {
            var until = DateTime.UtcNow.AddMilliseconds(ms);
            while (DateTime.UtcNow < until) { if (cond()) return true; Thread.Sleep(50); }
            return cond();
        }

        private static void WaitShutter(IDome d, ShutterState s) => SpinUntil(() => d.Shutter == s, 6000);

        [TestMethod]
        public void TestSequencerRunner()
        {
            var seq = new Sequencer();
            var log = new List<string>();
            seq.Log += s => log.Add(s);
            int changed = 0;
            seq.Changed += () => changed++;
            seq.Root = new SequenceContainer { Items = { new Annotation { Text = "one" }, new Wait { Seconds = 0.1 }, new Annotation { Text = "two" } } };
            CheckTrue("Nothing missing for notes and waits", seq.MissingDevices().Count == 0);
            seq.Start(new CaptureService()).Wait();
            CheckTrue("Runner logs start, notes and finish", log.First() == "Sequence started" && log.Contains("one") && log.Contains("two") && log.Last() == "Sequence finished" && !seq.Running && changed > 0);

            seq.Root = new SequenceContainer { Items = { new Wait { Seconds = 30 } } };
            log.Clear();
            var t = seq.Start(new CaptureService());
            Thread.Sleep(300);
            CheckTrue("Running while the wait lasts", seq.Running);
            seq.Stop();
            CheckTrue("Stop ends the sequence", t.Wait(3000) && !seq.Running && log.Last() == "Sequence stopped");

            seq.Root = new SequenceContainer { Items = { new TakeExposures(), new RunAutofocus(), new SwitchFilter(), new SlewToTarget(), new TakeExposures { Enabled = false } } };
            var missing = DeviceManager.Instance.Camera == null ? seq.MissingDevices() : null;
            if (missing != null)
            {
                CheckTrue("Missing devices in the order first needed", missing.SequenceEqual(new[] { "camera", "focuser", "filter wheel", "telescope" }), string.Join(", ", missing));
                Check("Description of what is missing", Sequencer.DescribeMissing(missing).Length, "This sequence needs a camera, a focuser, a filter wheel and a telescope. Connect them in Observatory → Equipment".Length, 0);
                bool refused = false;
                try { seq.Start(new CaptureService()).Wait(); } catch (AggregateException ex) { refused = ex.InnerException.Message.StartsWith("This sequence needs a camera"); }
                CheckTrue("Start refused while devices are missing", refused && !seq.Running);
                CheckTrue("One missing device", Sequencer.DescribeMissing(new List<string> { "camera" }) == "This sequence needs a camera. Connect it in Observatory → Equipment");
            }

            // every instruction type survives saving and loading with its settings
            var all = new SequenceContainer { Name = "All" };
            foreach (var type in Sequencer.ItemTypes) all.Items.Add((SequenceItem)Activator.CreateInstance(type));
            ((Wait)all.Items.Single(i => i is Wait)).Kind = WaitKind.UntilNauticalDusk;
            ((LoopContainer)all.Items.Single(i => i is LoopContainer)).Iterations = 7;
            ((RunScript)all.Items.Single(i => i is RunScript)).Arguments = "--flag \"quoted\"";
            ((TargetContainer)all.Items.Single(i => i is TargetContainer)).Target = new SequenceTarget { Name = "NGC 7000", RaDeg = 314.75, DecDeg = 44.33 };
            all.Items[3].Enabled = false;
            all.Items[4].StopOnError = true;
            string p = Path.Combine(Path.GetTempPath(), "SidraluneTests", "all-items.json");
            var s1 = new Sequencer { Root = all };
            s1.Save(p);
            var s2 = new Sequencer();
            s2.Load(p);
            var back = s2.Root;
            CheckTrue("Every instruction type round trips", back.Items.Count == Sequencer.ItemTypes.Length && back.Items.Select(i => i.GetType()).SequenceEqual(Sequencer.ItemTypes));
            CheckTrue("Settings round trip", ((Wait)back.Items.Single(i => i is Wait)).Kind == WaitKind.UntilNauticalDusk && ((LoopContainer)back.Items.Single(i => i is LoopContainer)).Iterations == 7
                && ((RunScript)back.Items.Single(i => i is RunScript)).Arguments == "--flag \"quoted\"" && ((TargetContainer)back.Items.Single(i => i is TargetContainer)).Target.Name == "NGC 7000"
                && !back.Items[3].Enabled && back.Items[4].StopOnError && back.Name == "All");
            var frameType = new FrameTypeConverter();
            CheckTrue("Frame type is a drop-down of the saved frame types", frameType.GetStandardValuesSupported(null) && frameType.GetStandardValuesExclusive(null)
                && frameType.GetStandardValues(null).Cast<string>().SequenceEqual(new[] { "LIGHT", "DARK", "FLAT", "BIAS" }));
            var typeAttr = typeof(TakeExposures).GetProperty("FrameType").GetCustomAttributes(typeof(System.ComponentModel.TypeConverterAttribute), false).Cast<System.ComponentModel.TypeConverterAttribute>().FirstOrDefault();
            CheckTrue("Take exposures uses it", typeAttr != null && typeAttr.ConverterTypeName.StartsWith(typeof(FrameTypeConverter).FullName));
            var filters = new FilterConverter().GetStandardValues(null).Cast<string>().ToList();
            var optional = new OptionalFilterConverter().GetStandardValues(null).Cast<string>().ToList();
            CheckTrue("Filter drop-down offers the wheel's names (the simulator's without a wheel), typing still allowed",
                (DeviceManager.Instance.FilterWheel != null || filters.SequenceEqual(FilterConverter.DefaultNames)) && !new FilterConverter().GetStandardValuesExclusive(null));
            CheckTrue("Optional filters start with an empty choice", optional[0] == "" && optional.Skip(1).SequenceEqual(filters));
            Func<Type, string, Type> converterOf = (ty, prop) => Type.GetType(ty.GetProperty(prop).GetCustomAttributes(typeof(System.ComponentModel.TypeConverterAttribute), false).Cast<System.ComponentModel.TypeConverterAttribute>().Single().ConverterTypeName);
            CheckTrue("Filters of the instructions use the drop-downs", converterOf(typeof(TakeExposures), "Filter") == typeof(OptionalFilterConverter)
                && converterOf(typeof(RecordVideo), "Filter") == typeof(OptionalFilterConverter) && converterOf(typeof(SwitchFilter), "Filter") == typeof(FilterConverter));
            var editor = typeof(RunScript).GetProperty("Command").GetCustomAttributes(typeof(System.ComponentModel.EditorAttribute), false).Cast<System.ComponentModel.EditorAttribute>().SingleOrDefault();
            CheckTrue("Program path has a file picker", editor != null && Type.GetType(editor.EditorTypeName) != null && Type.GetType(editor.EditorTypeName).Name == "FileNameEditor");
            var unknown = Sequencer.FromDict(new Dictionary<string, object> { { "$type", "TeleportTelescope" } });
            CheckTrue("Unknown instruction loads as a note", unknown is Annotation && ((Annotation)unknown).Text.Contains("TeleportTelescope"));
            var template = Sequencer.Template("M 31", 10.684, 41.269);
            CheckTrue("Night template", template.Items.Count == 6 && template.Items.OfType<TargetContainer>().Single().Items.OfType<LoopContainer>().Any());
        }

        [TestMethod]
        public void TestSimulators()
        {
            var dm = Observatory(DeviceKind.Telescope, DeviceKind.Focuser, DeviceKind.FilterWheel, DeviceKind.Dome, DeviceKind.ObservingConditions, DeviceKind.Camera);
            var tel = (TelescopeSimulator)dm.Telescope;
            Centering.Slew(tel, tel.SiderealTime - 1, 30, CancellationToken.None).Wait();
            CheckTrue("Mount west of the meridian on the west pier side", tel.SideOfPier == PierSide.West && tel.DestinationSideOfPier(tel.SiderealTime + 1, 30) == PierSide.East);
            double dec0 = tel.Declination;
            tel.PulseGuide(GuideDirection.North, 1000);
            Check("Guide pulse north at 0.5× sidereal (″)", (tel.Declination - dec0) * 3600, 0.5 * 15.041, 0.01);
            tel.SyncToCoordinates(tel.RightAscension, tel.Declination + 0.1);
            Check("Sync shifts the reported position", tel.Declination - (dec0 + 0.5 * 15.041 / 3600), 0.1, 1e-6);
            bool parkedRefusal = false;
            tel.Park();
            try { tel.SlewToCoordinatesAsync(1, 1); } catch (InvalidOperationException) { parkedRefusal = true; }
            CheckTrue("A parked mount refuses to slew", parkedRefusal && tel.AtPark);
            tel.Unpark();

            var foc = (FocuserSimulator)dm.Focuser;
            foc.Move(foc.BestFocus);
            SpinUntil(() => !foc.IsMoving, 5000);
            Check("Star size factor at best focus", foc.FwhmFactor, 1, 1e-9);
            foc.Move(foc.BestFocus + (int)foc.CriticalSteps);
            SpinUntil(() => !foc.IsMoving, 5000);
            Check("Star size factor one critical step away", foc.FwhmFactor, Math.Sqrt(2), 1e-9);

            var wheel = dm.FilterWheel;
            wheel.Position = 4;
            CheckTrue("Filter wheel moves, then arrives", wheel.IsMoving && SpinUntil(() => !wheel.IsMoving, 3000) && wheel.Position == 4 && wheel.Names[4] == "Ha" && wheel.FocusOffsets.Length == wheel.Names.Length);

            var dome = dm.Dome;
            dome.SlewToAzimuth(40);
            CheckTrue("Dome rotates to an azimuth", dome.Slewing && SpinUntil(() => !dome.Slewing, 5000) && Math.Abs(dome.Azimuth - 40) <= 2);
            dome.Park();
            CheckTrue("Dome parks at 0°", SpinUntil(() => dome.AtPark, 5000));

            var wx = (WeatherSimulator)dm.Weather;
            CheckTrue("Weather readings plausible", wx.Humidity > 40 && wx.Humidity < 90 && wx.DewPoint < wx.Temperature && wx.CloudCover < 50 && wx.IsSafe);
            wx.ForceUnsafe = true;
            CheckTrue("Forced unsafe", !wx.IsSafe && wx.CloudCover > 50 && !dm.Safety.IsSafe);
            wx.ForceUnsafe = false;

            var cam = dm.Camera;
            cam.BinX = 2; cam.BinY = 2; cam.StartX = 0; cam.StartY = 0; cam.NumX = cam.SensorWidth / 2; cam.NumY = cam.SensorHeight / 2;
            cam.StartExposure(0.1, true);
            CheckTrue("Camera exposes and delivers", SpinUntil(() => cam.ImageReady, 5000));
            var img = cam.GetImage();
            CheckTrue("Binned image size", img.Width == cam.SensorWidth / 2 && img.Height == cam.SensorHeight / 2, img.Width + "×" + img.Height);
            double startTemp = cam.CcdTemperature;
            cam.CoolerOn = true; cam.SetTemperature = -15;
            CheckTrue("Cooling", cam.CanSetTemperature && cam.CoolerOn && SpinUntil(() => cam.CcdTemperature < startTemp - 2, 10000) && cam.CoolerPower > 0, startTemp.ToString("0.0") + " → " + cam.CcdTemperature.ToString("0.0"));
            dm.DisconnectAll();
        }
    }
}
