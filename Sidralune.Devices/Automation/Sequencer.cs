using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Sidralune.Core;
using Sidralune.Core.Coordinates;
using Sidralune.Core.Ephemeris;
using Sidralune.Core.Mathematics;
using Sidralune.Core.Time;
using Sidralune.Core.Util;

namespace Sidralune.Devices.Automation
{
    public enum ItemStatus { Pending, Running, Done, Skipped, Failed }

    public class SequenceTarget
    {
        public string Name = "Target";
        public double RaDeg, DecDeg;     // J2000
        public double RotationDeg = double.NaN;
    }

    /// <summary>Shared state while a sequence runs.</summary>
    public class SequenceContext
    {
        public DeviceManager Devices = DeviceManager.Instance;
        public CaptureService Capture;
        public SequenceTarget Target;
        public List<SequenceTrigger> Triggers = new List<SequenceTrigger>();
        public Action<string> Log = s => { };
        public double LastAutofocusHfr = double.NaN;
        public double LastAutofocusTemperature = double.NaN;
        public int ExposuresSinceAutofocus;
        public string CurrentFilter;
        public List<double> RecentHfr = new List<double>();
        public Observer Site => Devices.Site;
        public int TotalExposures;
    }

    public abstract class SequenceItem
    {
        public bool Enabled { get; set; } = true;
        [Browsable] public ItemStatus Status { get; set; }
        public string Error { get; set; }
        public abstract string Title { get; }
        public virtual string Summary => "";
        public virtual List<SequenceItem> Children => null;

        public async Task Run(SequenceContext ctx, CancellationToken ct)
        {
            if (!Enabled) { Status = ItemStatus.Skipped; return; }
            Status = ItemStatus.Running;
            try
            {
                await Execute(ctx, ct);
                Status = ItemStatus.Done;
            }
            catch (OperationCanceledException) { Status = ItemStatus.Pending; throw; }
            catch (Exception ex)
            {
                Status = ItemStatus.Failed;
                Error = ex.Message;
                ctx.Log(Title + " failed: " + ex.Message);
                if (StopOnError) throw;
            }
        }

        public bool StopOnError { get; set; } = false;
        protected abstract Task Execute(SequenceContext ctx, CancellationToken ct);

        /// <summary>Devices this instruction cannot run without ("camera", "telescope", "focuser", "filter wheel").</summary>
        public virtual string[] RequiredDevices => new string[0];

        public virtual void Reset()
        {
            Status = ItemStatus.Pending; Error = null;
            if (Children != null) foreach (var c in Children) c.Reset();
        }
    }

    [AttributeUsage(AttributeTargets.Property)] public class BrowsableAttribute : Attribute { }

    // ------------------------------------------------------------------ containers

    public class SequenceContainer : SequenceItem
    {
        public string Name { get; set; } = "Sequence";
        public List<SequenceItem> Items { get; set; } = new List<SequenceItem>();
        public override List<SequenceItem> Children => Items;
        public override string Title => Name;
        protected override async Task Execute(SequenceContext ctx, CancellationToken ct)
        {
            foreach (var it in Items) { ct.ThrowIfCancellationRequested(); await it.Run(ctx, ct); }
        }
    }

    public class TargetContainer : SequenceContainer
    {
        public SequenceTarget Target { get; set; } = new SequenceTarget();
        public override string Title => "Target: " + Target.Name;
        public override string Summary => AstroMath.FormatHms(Target.RaDeg / 15, 0) + " " + AstroMath.FormatDms(Target.DecDeg, 0);
        protected override async Task Execute(SequenceContext ctx, CancellationToken ct)
        {
            var prev = ctx.Target;
            ctx.Target = Target;
            ctx.Log("Starting target " + Target.Name);
            try { await base.Execute(ctx, ct); }
            finally { ctx.Target = prev; }
        }
    }

    public enum LoopCondition { Count, UntilTime, UntilAltitudeBelow, UntilAstronomicalDawn, Forever }

    public class LoopContainer : SequenceContainer
    {
        public LoopCondition Condition { get; set; } = LoopCondition.Count;
        public int Iterations { get; set; } = 2;
        public DateTime UntilUtc { get; set; } = DateTime.UtcNow.AddHours(4);
        public double AltitudeDeg { get; set; } = 25;
        public int Completed { get; private set; }
        public override string Title => "Loop (" + Condition + ")";
        public override string Summary => Condition == LoopCondition.Count ? Completed + "/" + Iterations : Condition == LoopCondition.UntilAltitudeBelow ? "alt < " + AltitudeDeg + "°" : "";

        private bool Continue(SequenceContext ctx)
        {
            switch (Condition)
            {
                case LoopCondition.Count: return Completed < Iterations;
                case LoopCondition.UntilTime: return DateTime.UtcNow < UntilUtc;
                case LoopCondition.UntilAltitudeBelow:
                    if (ctx.Target == null) return false;
                    return new SkyContext(ctx.Site, AstroTime.Now).IcrsToHorizontal(ctx.Target.RaDeg * AstroMath.Deg2Rad, ctx.Target.DecDeg * AstroMath.Deg2Rad).AltDeg > AltitudeDeg;
                case LoopCondition.UntilAstronomicalDawn: return Events.SunAltitudeFast(ctx.Site, AstroTime.Now) < -18;
            }
            return true;
        }

        protected override async Task Execute(SequenceContext ctx, CancellationToken ct)
        {
            Completed = 0;
            while (Continue(ctx))
            {
                ct.ThrowIfCancellationRequested();
                foreach (var it in Items) it.Reset();
                var started = DateTime.UtcNow;
                await base.Execute(ctx, ct);
                Completed++;
                // a pass in which nothing succeeded (every instruction failed or was skipped) would only repeat itself:
                // without this the loop spins as fast as the failures come and floods the log and the window
                if (!AnySucceeded(Items))
                    throw new InvalidOperationException("Loop stopped: no instruction in it succeeded in the last pass (see the errors above)");
                var elapsed = DateTime.UtcNow - started;
                if (elapsed < TimeSpan.FromSeconds(1)) await Task.Delay(TimeSpan.FromSeconds(1) - elapsed, ct);
            }
        }

        private static bool AnySucceeded(IEnumerable<SequenceItem> items)
        {
            foreach (var it in items)
            {
                if (it.Children == null) { if (it.Status == ItemStatus.Done) return true; }
                else if (it.Status != ItemStatus.Skipped && AnySucceeded(it.Children)) return true;
            }
            return false;
        }
    }

    // ------------------------------------------------------------------ instructions

    public class SlewToTarget : SequenceItem
    {
        public override string Title => "Slew to target";
        public override string[] RequiredDevices => new[] { "telescope" };
        protected override async Task Execute(SequenceContext ctx, CancellationToken ct)
        {
            var t = ctx.Target; if (t == null) throw new InvalidOperationException("No target");
            var app = new SkyContext(ctx.Site, AstroTime.Now).IcrsToApparent(t.RaDeg * AstroMath.Deg2Rad, t.DecDeg * AstroMath.Deg2Rad);
            ctx.Log("Slewing to " + t.Name);
            await Centering.Slew(ctx.Devices.Telescope, app.RaHours, app.DecDeg, ct);
        }
    }

    public class CenterTarget : SequenceItem
    {
        public double ToleranceArcmin { get; set; } = 1.0;
        public double Exposure { get; set; } = 5;
        public int Bin { get; set; } = 2;
        public override string Title => "Slew, plate solve & center";
        public override string[] RequiredDevices => new[] { "telescope", "camera" };
        public override string Summary => "±" + ToleranceArcmin + "'";
        protected override async Task Execute(SequenceContext ctx, CancellationToken ct)
        {
            var t = ctx.Target; if (t == null) throw new InvalidOperationException("No target");
            var c = new Centering { Capture = ctx.Capture, ToleranceArcmin = ToleranceArcmin, Exposure = Exposure, Bin = Bin };
            c.Status += ctx.Log;
            await c.Center(t.RaDeg, t.DecDeg, ct);
        }
    }

    public class StartGuiding : SequenceItem
    {
        public bool ForceCalibration { get; set; }
        public override string Title => "Start guiding";
        protected override async Task Execute(SequenceContext ctx, CancellationToken ct)
        {
            if (ctx.Devices.Guider == null) { ctx.Log("No guider connected - skipping"); return; }
            await ctx.Devices.Guider.StartGuiding(ForceCalibration, ct);
        }
    }

    public class StopGuiding : SequenceItem
    {
        public override string Title => "Stop guiding";
        protected override Task Execute(SequenceContext ctx, CancellationToken ct) { ctx.Devices.Guider?.StopGuiding(); return Task.FromResult(0); }
    }

    public class TakeExposures : SequenceItem
    {
        public int Count { get; set; } = 10;
        public double Exposure { get; set; } = 120;
        public string Filter { get; set; } = "";
        public int Bin { get; set; } = 1;
        public int Gain { get; set; } = -1;
        public int Offset { get; set; } = -1;
        public string FrameType { get; set; } = "LIGHT";
        public int DitherEvery { get; set; } = 1;
        public double DitherPixels { get; set; } = 5;
        public int Completed { get; private set; }
        public override string Title => "Expose " + FrameType.ToLowerInvariant() + " frames";
        public override string[] RequiredDevices => new[] { "camera" };
        public override string Summary => string.Format("{0}/{1} × {2}s {3} bin{4}", Completed, Count, Exposure, Filter, Bin);

        protected override async Task Execute(SequenceContext ctx, CancellationToken ct)
        {
            Completed = 0;
            for (int i = 0; i < Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                foreach (var tr in ctx.Triggers.Where(t => t.Enabled)) await tr.Check(ctx, this, ct);
                var r = new ExposureRequest
                {
                    Seconds = Exposure, Bin = Bin, Gain = Gain, Offset = Offset, Filter = string.IsNullOrEmpty(Filter) ? null : Filter,
                    FrameType = FrameType, Light = FrameType != "DARK" && FrameType != "BIAS", Target = ctx.Target != null ? ctx.Target.Name : ""
                };
                var res = await ctx.Capture.Expose(r, ct);
                ctx.CurrentFilter = r.Filter;
                if (!double.IsNaN(res.Hfr)) { ctx.RecentHfr.Add(res.Hfr); if (ctx.RecentHfr.Count > 20) ctx.RecentHfr.RemoveAt(0); }
                ctx.ExposuresSinceAutofocus++;
                ctx.TotalExposures++;
                Completed++;
                ctx.Log(string.Format("Saved {0} (HFR {1:0.00}, {2} stars)", Path.GetFileName(res.Path ?? ""), res.Hfr, res.Stars));
                if (FrameType == "LIGHT" && DitherEvery > 0 && Completed % DitherEvery == 0 && i < Count - 1 && ctx.Devices.Guider != null && ctx.Devices.Guider.IsGuiding)
                    await ctx.Devices.Guider.Dither(DitherPixels, false, 1.5, 10, 120, ct);
            }
        }
    }

    /// <summary>Planetary video: short exposures back to back into a SER file (see CaptureService.RecordVideo).</summary>
    public class RecordVideo : SequenceItem
    {
        public double ExposureMs { get; set; } = 10;
        /// <summary>Region of interest centred on the sensor, one of VideoRequest.Regions.</summary>
        [System.ComponentModel.TypeConverter(typeof(VideoRegionConverter))]
        public string Region { get; set; } = "640 × 480";
        public int Bin { get; set; } = 1;
        public int Gain { get; set; } = -1;
        public int Offset { get; set; } = -1;
        public string Filter { get; set; } = "";
        public int MaxFrames { get; set; } = 1000;
        public double MaxSeconds { get; set; } = 60;
        public override string Title => "Record video";
        public override string[] RequiredDevices => new[] { "camera" };
        public override string Summary => string.Format("{0} ms {1}{2} · {3} frames / {4} s", ExposureMs, Region, string.IsNullOrEmpty(Filter) ? "" : " " + Filter, MaxFrames, MaxSeconds);

        protected override async Task Execute(SequenceContext ctx, CancellationToken ct)
        {
            foreach (var tr in ctx.Triggers.Where(t => t.Enabled)) await tr.Check(ctx, this, ct);
            int rw, rh;
            VideoRequest.ParseRegion(Region, out rw, out rh);
            var r = new VideoRequest
            {
                ExposureMs = ExposureMs, Bin = Bin, Gain = Gain, Offset = Offset, RoiWidth = rw, RoiHeight = rh,
                MaxFrames = Math.Max(1, MaxFrames), MaxSeconds = Math.Max(1, MaxSeconds), Filter = string.IsNullOrEmpty(Filter) ? null : Filter,
                Target = ctx.Target != null ? ctx.Target.Name : ""
            };
            var res = await ctx.Capture.RecordVideo(r, null, ct);
            if (r.Filter != null) ctx.CurrentFilter = r.Filter;
            if (res.Path != null) ctx.Log(string.Format("Saved {0} ({1} frames {2}×{3} at {4:0.0} fps)", Path.GetFileName(res.Path), res.Frames, res.Width, res.Height, res.Fps));
            // Stop ends the recording but keeps the file; the sequence still stops here
            ct.ThrowIfCancellationRequested();
            if (res.Path == null) throw new InvalidOperationException("The camera delivered no frames");
        }
    }

    /// <summary>Shows VideoRequest.Regions as a drop-down list in the property grid.</summary>
    public class VideoRegionConverter : System.ComponentModel.StringConverter
    {
        public override bool GetStandardValuesSupported(System.ComponentModel.ITypeDescriptorContext context) => true;
        public override bool GetStandardValuesExclusive(System.ComponentModel.ITypeDescriptorContext context) => true;
        public override StandardValuesCollection GetStandardValues(System.ComponentModel.ITypeDescriptorContext context) => new StandardValuesCollection(VideoRequest.Regions);
    }

    public class RunAutofocus : SequenceItem
    {
        public double Exposure { get; set; } = 3;
        public int StepSize { get; set; } = 100;
        public int Steps { get; set; } = 9;
        public override string Title => "Autofocus";
        public override string[] RequiredDevices => new[] { "camera", "focuser" };
        protected override async Task Execute(SequenceContext ctx, CancellationToken ct)
        {
            var af = new Autofocus { Capture = ctx.Capture, Exposure = Exposure, StepSize = StepSize, Steps = Steps, Filter = ctx.CurrentFilter };
            af.Status += ctx.Log;
            var r = await af.Run(ct);
            if (r.Success) { ctx.LastAutofocusHfr = r.BestHfr; ctx.LastAutofocusTemperature = r.Temperature; ctx.ExposuresSinceAutofocus = 0; ctx.RecentHfr.Clear(); }
        }
    }

    public class SwitchFilter : SequenceItem
    {
        public string Filter { get; set; } = "L";
        public override string Title => "Switch filter";
        public override string[] RequiredDevices => new[] { "filter wheel" };
        public override string Summary => Filter;
        protected override async Task Execute(SequenceContext ctx, CancellationToken ct) { await ctx.Capture.SetFilter(Filter, ct); ctx.CurrentFilter = Filter; }
    }

    public class CoolCamera : SequenceItem
    {
        public double Temperature { get; set; } = -10;
        public double Minutes { get; set; } = 10;
        public bool Warm { get; set; }
        public override string Title => Warm ? "Warm camera" : "Cool camera";
        public override string Summary => Warm ? "" : Temperature + " °C over " + Minutes + " min";
        protected override async Task Execute(SequenceContext ctx, CancellationToken ct)
        {
            var cam = ctx.Devices.Camera;
            if (cam == null || !cam.CanSetTemperature) return;
            double start = cam.CcdTemperature, target = Warm ? 10 : Temperature;
            if (!Warm) cam.CoolerOn = true;
            int steps = Math.Max(1, (int)(Minutes * 2));
            for (int i = 1; i <= steps; i++)
            {
                cam.SetTemperature = start + (target - start) * i / steps;
                await Task.Delay(30000 * (Minutes > 0 ? 1 : 0), ct);
            }
            if (Warm) cam.CoolerOn = false;
        }
    }

    public enum WaitKind { Seconds, UntilTime, UntilAltitude, UntilAstronomicalDusk, UntilNauticalDusk, UntilSafe }

    public class Wait : SequenceItem
    {
        public WaitKind Kind { get; set; } = WaitKind.Seconds;
        public double Seconds { get; set; } = 60;
        public DateTime TimeUtc { get; set; } = DateTime.UtcNow;
        public double AltitudeDeg { get; set; } = 30;
        public override string Title => "Wait";
        public override string Summary => Kind == WaitKind.Seconds ? Seconds + " s" : Kind == WaitKind.UntilAltitude ? "until target alt > " + AltitudeDeg + "°" : Kind.ToString();
        protected override async Task Execute(SequenceContext ctx, CancellationToken ct)
        {
            Func<bool> done;
            switch (Kind)
            {
                case WaitKind.UntilTime: done = () => DateTime.UtcNow >= TimeUtc; break;
                case WaitKind.UntilAltitude:
                    done = () => ctx.Target == null || new SkyContext(ctx.Site, AstroTime.Now).IcrsToHorizontal(ctx.Target.RaDeg * AstroMath.Deg2Rad, ctx.Target.DecDeg * AstroMath.Deg2Rad).AltDeg >= AltitudeDeg; break;
                case WaitKind.UntilAstronomicalDusk: done = () => Events.SunAltitudeFast(ctx.Site, AstroTime.Now) <= -18; break;
                case WaitKind.UntilNauticalDusk: done = () => Events.SunAltitudeFast(ctx.Site, AstroTime.Now) <= -12; break;
                case WaitKind.UntilSafe: done = () => ctx.Devices.Safety == null || ctx.Devices.Safety.IsSafe; break;
                default: { await Task.Delay(TimeSpan.FromSeconds(Seconds), ct); return; }
            }
            while (!done()) await Task.Delay(5000, ct);
        }
    }

    public class ParkTelescope : SequenceItem
    {
        public bool Unpark { get; set; }
        public override string Title => Unpark ? "Unpark telescope" : "Park telescope";
        public override string[] RequiredDevices => new[] { "telescope" };
        protected override Task Execute(SequenceContext ctx, CancellationToken ct) => Task.Run(() => { if (Unpark) ctx.Devices.Telescope.Unpark(); else ctx.Devices.Telescope.Park(); }, ct);
    }

    public class DomeShutter : SequenceItem
    {
        public bool Open { get; set; } = true;
        public override string Title => Open ? "Open dome" : "Close dome";
        protected override async Task Execute(SequenceContext ctx, CancellationToken ct)
        {
            var d = ctx.Devices.Dome; if (d == null) return;
            if (Open) d.OpenShutter(); else d.CloseShutter();
            var want = Open ? ShutterState.Open : ShutterState.Closed;
            var until = DateTime.UtcNow.AddMinutes(5);
            while (d.Shutter != want) { if (DateTime.UtcNow > until) throw new TimeoutException("Dome shutter timeout"); await Task.Delay(1000, ct); }
        }
    }

    public class RunScript : SequenceItem
    {
        public string Command { get; set; } = "";
        public string Arguments { get; set; } = "";
        public int TimeoutSec { get; set; } = 300;
        public override string Title => "Run external program";
        public override string Summary => Path.GetFileName(Command);
        protected override Task Execute(SequenceContext ctx, CancellationToken ct) => Task.Run(() =>
        {
            using (var p = Process.Start(new ProcessStartInfo(Command, Arguments) { UseShellExecute = false, CreateNoWindow = true }))
                if (!p.WaitForExit(TimeoutSec * 1000)) { try { p.Kill(); } catch { } throw new TimeoutException("Script timeout"); }
        }, ct);
    }

    public class Annotation : SequenceItem
    {
        public string Text { get; set; } = "Note";
        public override string Title => "Note";
        public override string Summary => Text;
        protected override Task Execute(SequenceContext ctx, CancellationToken ct) { ctx.Log(Text); return Task.FromResult(0); }
    }

    // ------------------------------------------------------------------ triggers

    public abstract class SequenceTrigger
    {
        public bool Enabled { get; set; } = true;
        public abstract string Title { get; }
        public abstract Task Check(SequenceContext ctx, SequenceItem current, CancellationToken ct);
    }

    public class AutofocusOnHfrIncrease : SequenceTrigger
    {
        public double PercentIncrease { get; set; } = 15;
        public int SampleSize { get; set; } = 5;
        public override string Title => "Autofocus when HFR rises " + PercentIncrease + "%";
        public override async Task Check(SequenceContext ctx, SequenceItem current, CancellationToken ct)
        {
            if (double.IsNaN(ctx.LastAutofocusHfr) || ctx.RecentHfr.Count < SampleSize) return;
            double med = Stats.Median(ctx.RecentHfr.Skip(ctx.RecentHfr.Count - SampleSize).ToList());
            if (med > ctx.LastAutofocusHfr * (1 + PercentIncrease / 100)) { ctx.Log("HFR increased to " + med.ToString("0.00") + " - refocusing"); await new RunAutofocus().Run(ctx, ct); }
        }
    }

    public class AutofocusOnTemperature : SequenceTrigger
    {
        public double DeltaC { get; set; } = 1.5;
        public override string Title => "Autofocus after " + DeltaC + " °C change";
        public override async Task Check(SequenceContext ctx, SequenceItem current, CancellationToken ct)
        {
            var f = ctx.Devices.Focuser;
            if (f == null || double.IsNaN(ctx.LastAutofocusTemperature) || double.IsNaN(f.Temperature)) return;
            if (Math.Abs(f.Temperature - ctx.LastAutofocusTemperature) >= DeltaC) { ctx.Log("Temperature changed - refocusing"); await new RunAutofocus().Run(ctx, ct); }
        }
    }

    public class AutofocusEveryN : SequenceTrigger
    {
        public int Exposures { get; set; } = 30;
        public override string Title => "Autofocus every " + Exposures + " exposures";
        public override async Task Check(SequenceContext ctx, SequenceItem current, CancellationToken ct)
        {
            if (ctx.ExposuresSinceAutofocus >= Exposures) await new RunAutofocus().Run(ctx, ct);
        }
    }

    public class MeridianFlipTrigger : SequenceTrigger
    {
        public double MinutesAfterMeridian { get; set; } = 5;
        public override string Title => "Meridian flip " + MinutesAfterMeridian + " min after transit";
        public override async Task Check(SequenceContext ctx, SequenceItem current, CancellationToken ct)
        {
            var tel = ctx.Devices.Telescope;
            if (tel == null || ctx.Target == null || !MeridianFlip.IsDue(tel, MinutesAfterMeridian)) return;
            ctx.Log("Performing meridian flip");
            var app = new SkyContext(ctx.Site, AstroTime.Now).IcrsToApparent(ctx.Target.RaDeg * AstroMath.Deg2Rad, ctx.Target.DecDeg * AstroMath.Deg2Rad);
            await MeridianFlip.Perform(tel, app.RaHours, app.DecDeg, ctx.Devices.Guider, ct);
            await new CenterTarget().Run(ctx, ct);
        }
    }

    public class SafetyTrigger : SequenceTrigger
    {
        public override string Title => "Park and close when unsafe";
        public override async Task Check(SequenceContext ctx, SequenceItem current, CancellationToken ct)
        {
            var s = ctx.Devices.Safety;
            if (s == null || s.IsSafe) return;
            ctx.Log("UNSAFE conditions: stopping guiding, parking and closing");
            ctx.Devices.Guider?.StopGuiding();
            if (ctx.Devices.Telescope != null) await Task.Run(() => ctx.Devices.Telescope.Park(), ct);
            if (ctx.Devices.Dome != null) await new DomeShutter { Open = false }.Run(ctx, ct);
            while (!s.IsSafe) await Task.Delay(30000, ct);
            ctx.Log("Conditions safe again: resuming");
            if (ctx.Devices.Dome != null) await new DomeShutter { Open = true }.Run(ctx, ct);
            ctx.Devices.Telescope?.Unpark();
            if (ctx.Target != null) await new CenterTarget().Run(ctx, ct);
            if (ctx.Devices.Guider != null) await ctx.Devices.Guider.StartGuiding(false, ct);
        }
    }

    // ------------------------------------------------------------------ runner and persistence

    public class Sequencer
    {
        public SequenceContainer Root = new SequenceContainer { Name = "Sequence" };
        public List<SequenceTrigger> Triggers = new List<SequenceTrigger> { new SafetyTrigger(), new MeridianFlipTrigger(), new AutofocusOnHfrIncrease() };
        public bool Running { get; private set; }
        public event Action<string> Log;
        public event Action Changed;
        private CancellationTokenSource _cts;

        /// <summary>Devices that enabled instructions need but that are not connected, in the order first needed.</summary>
        public List<string> MissingDevices()
        {
            var dm = DeviceManager.Instance;
            var missing = new List<string>();
            Action<SequenceItem> walk = null;
            walk = it =>
            {
                if (!it.Enabled) return;
                foreach (var d in it.RequiredDevices)
                {
                    bool have = d == "camera" ? dm.Camera != null : d == "telescope" ? dm.Telescope != null : d == "focuser" ? dm.Focuser != null : d == "filter wheel" ? dm.FilterWheel != null : true;
                    if (!have && !missing.Contains(d)) missing.Add(d);
                }
                if (it.Children != null) foreach (var c in it.Children) walk(c);
            };
            walk(Root);
            return missing;
        }

        /// <summary>"This sequence needs a camera and a focuser. Connect them …" for the list from MissingDevices.</summary>
        public static string DescribeMissing(List<string> missing)
        {
            var named = missing.Select(d => "a " + d).ToList();
            string list = named.Count == 1 ? named[0] : string.Join(", ", named.Take(named.Count - 1)) + " and " + named[named.Count - 1];
            return "This sequence needs " + list + ". Connect " + (named.Count == 1 ? "it" : "them") + " in Observatory → Equipment";
        }

        public async Task Start(CaptureService capture)
        {
            if (Running) return;
            var missing = MissingDevices();
            if (missing.Count > 0) throw new InvalidOperationException(DescribeMissing(missing) + " first.");
            Running = true;
            _cts = new CancellationTokenSource();
            Root.Reset();
            var ctx = new SequenceContext { Capture = capture, Triggers = Triggers, Log = s => { Core.Util.Log.Info("Sequencer", s); Log?.Invoke(s); Changed?.Invoke(); } };
            ctx.Log("Sequence started");
            try { await Root.Run(ctx, _cts.Token); ctx.Log("Sequence finished"); }
            catch (OperationCanceledException) { ctx.Log("Sequence stopped"); }
            finally { Running = false; Changed?.Invoke(); }
        }

        public void Stop() => _cts?.Cancel();

        public static readonly Type[] ItemTypes =
        {
            typeof(TargetContainer), typeof(LoopContainer), typeof(SequenceContainer), typeof(SlewToTarget), typeof(CenterTarget), typeof(StartGuiding), typeof(StopGuiding),
            typeof(TakeExposures), typeof(RecordVideo), typeof(RunAutofocus), typeof(SwitchFilter), typeof(CoolCamera), typeof(Wait), typeof(ParkTelescope), typeof(DomeShutter), typeof(RunScript), typeof(Annotation)
        };

        public static Dictionary<string, object> ToDict(SequenceItem item)
        {
            var d = new Dictionary<string, object> { { "$type", item.GetType().Name } };
            foreach (var p in item.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!p.CanWrite || p.Name == "Status" || p.Name == "Error" || p.Name == "Completed") continue;
                var v = p.GetValue(item, null);
                if (p.Name == "Items") d["Items"] = ((List<SequenceItem>)v).Select(ToDict).ToList();
                else if (v is SequenceTarget) d[p.Name] = new Dictionary<string, object> { { "Name", ((SequenceTarget)v).Name }, { "RaDeg", ((SequenceTarget)v).RaDeg }, { "DecDeg", ((SequenceTarget)v).DecDeg } };
                else d[p.Name] = v;
            }
            return d;
        }

        public static SequenceItem FromDict(Dictionary<string, object> d)
        {
            var t = ItemTypes.FirstOrDefault(x => x.Name == (string)d["$type"]);
            if (t == null) return new Annotation { Text = "Unknown item " + d["$type"] };
            var item = (SequenceItem)Activator.CreateInstance(t);
            foreach (var kv in d)
            {
                if (kv.Key == "$type") continue;
                var p = t.GetProperty(kv.Key);
                if (p == null || !p.CanWrite) continue;
                if (kv.Key == "Items") { var list = new List<SequenceItem>(); foreach (Dictionary<string, object> c in (List<object>)kv.Value) list.Add(FromDict(c)); p.SetValue(item, list, null); }
                else if (p.PropertyType == typeof(SequenceTarget))
                {
                    var td = (Dictionary<string, object>)kv.Value;
                    p.SetValue(item, new SequenceTarget { Name = (string)td["Name"], RaDeg = Convert.ToDouble(td["RaDeg"]), DecDeg = Convert.ToDouble(td["DecDeg"]) }, null);
                }
                else try { p.SetValue(item, Json.PopulateType(p.PropertyType, kv.Value), null); } catch { }
            }
            return item;
        }

        public void Save(string path) => File.WriteAllText(path, Json.Serialize(ToDict(Root), true));

        public void Load(string path)
        {
            var root = FromDict(Json.ParseObject(File.ReadAllText(path))) as SequenceContainer;
            if (root != null) Root = root;
            Changed?.Invoke();
        }

        /// <summary>A typical deep-sky night template for a target.</summary>
        public static SequenceContainer Template(string name, double raDeg, double decDeg)
        {
            var t = new TargetContainer { Name = name, Target = new SequenceTarget { Name = name, RaDeg = raDeg, DecDeg = decDeg } };
            t.Items.Add(new Wait { Kind = WaitKind.UntilAltitude, AltitudeDeg = 25 });
            t.Items.Add(new CenterTarget());
            t.Items.Add(new RunAutofocus());
            t.Items.Add(new StartGuiding());
            var loop = new LoopContainer { Condition = LoopCondition.UntilAltitudeBelow, AltitudeDeg = 20 };
            loop.Items.Add(new TakeExposures { Count = 10, Exposure = 180, Filter = "L" });
            t.Items.Add(loop);
            t.Items.Add(new StopGuiding());
            var root = new SequenceContainer { Name = "Night of " + DateTime.Now.ToString("yyyy-MM-dd") };
            root.Items.Add(new CoolCamera { Temperature = -10, Minutes = 5 });
            root.Items.Add(new ParkTelescope { Unpark = true });
            root.Items.Add(new Wait { Kind = WaitKind.UntilAstronomicalDusk });
            root.Items.Add(t);
            root.Items.Add(new ParkTelescope());
            root.Items.Add(new CoolCamera { Warm = true, Minutes = 5 });
            return root;
        }
    }
}
