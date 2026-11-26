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
        /// <summary>
        /// The sequence guides: it was guiding when the sequence started, or a Start guiding instruction started it (Stop
        /// guiding ends it). Light frames then wait for guiding, and start it again when it stopped.
        /// </summary>
        public bool GuidingWanted;

        /// <summary>
        /// Before a light frame, guiding as the sequence wants it: a lost star (a cloud) is waited for, and guiding that
        /// stopped (a slew, the guide camera failing) is started again, once a minute. After waitMinutes the frames go on
        /// without it, and the log says so.
        /// </summary>
        public async Task WaitForGuiding(double waitMinutes, CancellationToken ct)
        {
            var g = Devices.Guider;
            if (!GuidingWanted || g == null || g.IsGuiding) return;
            Log(g.State == Guiding.SidraluneGuider.LostLock ? "The guide star is lost: waiting for it before the next frame" : "Guiding has stopped: starting it again before the next frame");
            var until = DateTime.UtcNow.AddMinutes(Math.Max(0, waitMinutes));
            var nextStart = DateTime.MinValue;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                g = Devices.Guider;
                if (g != null && g.IsGuiding) { Log("Guiding again"); return; }
                if (DateTime.UtcNow >= until)
                {
                    Log(string.Format(System.Globalization.CultureInfo.InvariantCulture, "Guiding did not come back within {0:0} min: going on without it", waitMinutes));
                    GuidingWanted = false;
                    return;
                }
                // a lost star comes back by itself; calibrating or paused (by hand) is waited for too
                string st = g != null ? g.State : null;
                bool waiting = st == Guiding.SidraluneGuider.LostLock || st == Guiding.SidraluneGuider.Calibrating || st == Guiding.SidraluneGuider.Paused;
                if (g != null && !waiting && DateTime.UtcNow >= nextStart)
                {
                    nextStart = DateTime.UtcNow.AddMinutes(1);
                    try { await g.StartGuiding(false, ct); continue; }
                    catch (Exception ex) when (!ct.IsCancellationRequested) { Log("Could not start guiding: " + ex.Message); }
                }
                await Task.Delay(1000, ct);
            }
        }
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
                ctx.Log(Lang.InEnglish(() => Title) + " failed: " + ex.Message);
                if (StopOnError) throw;
            }
        }

        public bool StopOnError { get; set; } = false;
        protected abstract Task Execute(SequenceContext ctx, CancellationToken ct);

        /// <summary>Devices this instruction cannot run without ("camera", "telescope", "focuser", "filter wheel", "switch", "flat panel").</summary>
        public virtual string[] RequiredDevices => new string[0];

        public virtual void Reset()
        {
            Status = ItemStatus.Pending; Error = null;
            if (Children != null) foreach (var c in Snapshot(Children)) c.Reset();
        }

        /// <summary>A copy of a list of instructions, taken while it cannot change (it can be edited while the sequence runs).</summary>
        protected static List<SequenceItem> Snapshot(List<SequenceItem> items) { lock (items) return items.ToList(); }
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
            // the list can be edited while it runs: after each instruction the next is the first one below it that has
            // not run yet, so added instructions below run, those above have been passed, and moved ones run once
            var ran = new HashSet<SequenceItem>();
            SequenceItem it = null;
            int at = -1;
            while ((it = Next(it, ran, ref at)) != null)
            {
                ct.ThrowIfCancellationRequested();
                ran.Add(it);
                await it.Run(ctx, ct);
            }
        }

        private SequenceItem Next(SequenceItem prev, HashSet<SequenceItem> ran, ref int at)
        {
            lock (Items)
            {
                int i = prev == null ? -1 : Items.IndexOf(prev);
                // an instruction removed after it ran: go on from where it was
                int from = prev == null ? 0 : i >= 0 ? i + 1 : Math.Max(0, at);
                for (int j = from; j < Items.Count; j++)
                    if (!ran.Contains(Items[j])) { at = j; return Items[j]; }
                return null;
            }
        }

        /// <summary>Adds an instruction at a position; allowed while the sequence runs.</summary>
        public void Insert(int index, SequenceItem item)
        {
            lock (Items) Items.Insert(Math.Max(0, Math.Min(index, Items.Count)), item);
        }

        /// <summary>Removes an instruction, unless it is running (a container is running while its instructions run).</summary>
        public bool Remove(SequenceItem item)
        {
            if (item.Status == ItemStatus.Running) return false;
            lock (Items) return Items.Remove(item);
        }

        /// <summary>Moves an instruction one step up (-1) or down (1), unless it is running or already at the end.</summary>
        public bool Move(SequenceItem item, int dir)
        {
            if (item.Status == ItemStatus.Running) return false;
            lock (Items)
            {
                int i = Items.IndexOf(item), j = i + dir;
                if (i < 0 || j < 0 || j >= Items.Count) return false;
                Items.RemoveAt(i); Items.Insert(j, item);
                return true;
            }
        }
    }

    public class TargetContainer : SequenceContainer
    {
        public SequenceTarget Target { get; set; } = new SequenceTarget();
        public override string Title => Lang.T("Target: {0}", Target.Name);
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

    /// <summary>What a sequence does with a frame whose guiding was worse than its limit.</summary>
    public enum BadGuidingAction { Flag, Retake }

    public enum LoopCondition { Count, UntilTime, UntilAltitudeBelow, UntilAstronomicalDawn, Forever }

    public class LoopContainer : SequenceContainer
    {
        public LoopCondition Condition { get; set; } = LoopCondition.Count;
        public int Iterations { get; set; } = 2;
        public DateTime UntilUtc { get; set; } = DateTime.UtcNow.AddHours(4);
        public double AltitudeDeg { get; set; } = 25;
        public int Completed { get; private set; }
        public override string Title => Lang.T("Loop ({0})", Lang.Option(Condition.ToString()));
        public override string Summary => Condition == LoopCondition.Count ? Completed + "/" + Iterations : Condition == LoopCondition.UntilAltitudeBelow ? Lang.T("alt < {0}°", AltitudeDeg) : "";

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
                foreach (var it in Snapshot(Items)) it.Reset();
                var started = DateTime.UtcNow;
                await base.Execute(ctx, ct);
                Completed++;
                // a pass in which nothing succeeded (every instruction failed or was skipped) would only repeat itself:
                // without this the loop spins as fast as the failures come and floods the log and the window
                if (!AnySucceeded(Snapshot(Items)))
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
                else if (it.Status != ItemStatus.Skipped && AnySucceeded(Snapshot(it.Children))) return true;
            }
            return false;
        }
    }

    // ------------------------------------------------------------------ instructions

    public class SlewToTarget : SequenceItem
    {
        public override string Title => Lang.T("Slew to target");
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
        public override string Title => Lang.T("Slew, plate solve & center");
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
        public override string Title => Lang.T("Start guiding");
        protected override async Task Execute(SequenceContext ctx, CancellationToken ct)
        {
            if (ctx.Devices.Guider == null) { ctx.Log("No guider connected - skipping"); return; }
            await ctx.Devices.Guider.StartGuiding(ForceCalibration, ct);
            ctx.GuidingWanted = true;
        }
    }

    public class StopGuiding : SequenceItem
    {
        public override string Title => Lang.T("Stop guiding");
        protected override Task Execute(SequenceContext ctx, CancellationToken ct) { ctx.GuidingWanted = false; ctx.Devices.Guider?.StopGuiding(); return Task.FromResult(0); }
    }

    public class TakeExposures : SequenceItem
    {
        public int Count { get; set; } = 10;
        public double Exposure { get; set; } = 120;
        [System.ComponentModel.TypeConverter(typeof(OptionalFilterConverter))]
        public string Filter { get; set; } = "";
        public int Bin { get; set; } = 1;
        public int Gain { get; set; } = -1;
        public int Offset { get; set; } = -1;
        [System.ComponentModel.TypeConverter(typeof(FrameTypeConverter))]
        public string FrameType { get; set; } = "LIGHT";
        public int DitherEvery { get; set; } = 1;
        public double DitherPixels { get; set; } = 5;
        /// <summary>How long a light frame waits for guiding to come back (a cloud, a stopped guider) before going on without it.</summary>
        public double GuidingWaitMinutes { get; set; } = 30;
        /// <summary>
        /// A light frame guided worse than this (total RMS, arcseconds; 0 for no limit), or during which guiding was
        /// interrupted, is flagged: GUIDBAD in its header and _BADGUIDING at the end of its file name.
        /// </summary>
        public double MaxGuidingRms { get; set; }
        /// <summary>What happens to a flagged frame: kept as it is, or kept and taken again (at most three times in a row).</summary>
        public BadGuidingAction WhenGuidingIsBad { get; set; } = BadGuidingAction.Flag;
        public int Completed { get; private set; }
        public override string Title => FrameType == "DARK" ? Lang.T("Expose dark frames") : FrameType == "BIAS" ? Lang.T("Expose bias frames") : FrameType == "FLAT" ? Lang.T("Expose flat frames")
            : FrameType == "LIGHT" ? Lang.T("Expose light frames") : Lang.T("Expose {0} frames", FrameType.ToLowerInvariant());
        public override string[] RequiredDevices => new[] { "camera" };
        public override string Summary => string.Format("{0}/{1} × {2}s {3} bin{4}", Completed, Count, Exposure, Filter, Bin);

        protected override async Task Execute(SequenceContext ctx, CancellationToken ct)
        {
            Completed = 0;
            int retakes = 0;
            for (int i = 0; i < Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                foreach (var tr in ctx.Triggers.Where(t => t.Enabled)) await tr.Check(ctx, this, ct);
                var r = new ExposureRequest
                {
                    Seconds = Exposure, Bin = Bin, Gain = Gain, Offset = Offset, Filter = string.IsNullOrEmpty(Filter) ? null : Filter,
                    FrameType = FrameType, Light = FrameType != "DARK" && FrameType != "BIAS", Target = ctx.Target != null ? ctx.Target.Name : "",
                    MaxGuidingRms = FrameType == "LIGHT" ? MaxGuidingRms : 0
                };
                if (FrameType == "LIGHT") await ctx.WaitForGuiding(GuidingWaitMinutes, ct);
                // dithered as soon as the frame is read out, while it downloads and is measured and saved; the next frame
                // starts once guiding has settled
                var g = ctx.Devices.Guider;
                Task dithering = null;
                if (FrameType == "LIGHT" && DitherEvery > 0 && (Completed + 1) % DitherEvery == 0 && i < Count - 1 && g != null && g.IsGuiding)
                    r.ReadOut = () => dithering = g.Dither(DitherPixels, false, 1.5, 10, 120, ct);
                CaptureResult res;
                try { res = await ctx.Capture.Expose(r, ct); }
                catch
                {
                    // the frame failed after the dither began: its result is not waited for, but it is looked at
                    dithering?.ContinueWith(t => { var ignored = t.Exception; });
                    throw;
                }
                ctx.CurrentFilter = r.Filter;
                var gs = res.Guiding;
                bool bad = gs != null && gs.Bad;
                string flag = !bad ? "" : gs.Interrupted ? "flagged: guiding was interrupted"
                    : string.Format(System.Globalization.CultureInfo.InvariantCulture, "flagged: guiding RMS {0:0.00}\" over {1:0.00}\"", gs.RmsTotal, MaxGuidingRms);
                ctx.ExposuresSinceAutofocus++;
                if (bad && WhenGuidingIsBad == BadGuidingAction.Retake && retakes < 3)
                {
                    // kept, but it does not count: the same frame is taken again
                    ctx.Log(string.Format("Saved {0} {1} - taking it again", Path.GetFileName(res.Path ?? ""), flag));
                    if (dithering != null) await dithering;
                    retakes++; i--;
                    continue;
                }
                retakes = 0;
                // a trailed or clouded frame says nothing about focus
                if (!bad && !double.IsNaN(res.Hfr)) { ctx.RecentHfr.Add(res.Hfr); if (ctx.RecentHfr.Count > 20) ctx.RecentHfr.RemoveAt(0); }
                ctx.TotalExposures++;
                Completed++;
                ctx.Log(string.Format("Saved {0} (HFR {1:0.00}, {2} stars{3}){4}", Path.GetFileName(res.Path ?? ""), res.Hfr, res.Stars,
                    gs != null && !double.IsNaN(gs.RmsTotal) ? string.Format(System.Globalization.CultureInfo.InvariantCulture, ", guiding {0:0.00}\" RMS", gs.RmsTotal) : "",
                    bad ? " " + flag : ""));
                if (dithering != null) await dithering;
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
        [System.ComponentModel.TypeConverter(typeof(OptionalFilterConverter))]
        public string Filter { get; set; } = "";
        public int MaxFrames { get; set; } = 1000;
        public double MaxSeconds { get; set; } = 60;
        public override string Title => Lang.T("Record video");
        public override string[] RequiredDevices => new[] { "camera" };
        public override string Summary => Lang.T("{0} ms {1}{2} · {3} frames / {4} s", ExposureMs, Region, string.IsNullOrEmpty(Filter) ? "" : " " + Filter, MaxFrames, MaxSeconds);

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

    /// <summary>
    /// The filter wheel's filter names as a drop-down list (the simulator's names when no wheel is connected). Typing
    /// another name stays possible, for a wheel that is not connected while the sequence is edited.
    /// </summary>
    public class FilterConverter : System.ComponentModel.StringConverter
    {
        public static readonly string[] DefaultNames = { "L", "R", "G", "B", "Ha", "OIII", "SII" };

        public static string[] Names()
        {
            try
            {
                var fw = DeviceManager.Instance.FilterWheel;
                var names = fw != null ? fw.Names : null;
                if (names != null && names.Length > 0) return names;
            }
            catch { }
            return DefaultNames;
        }

        protected virtual bool Optional => false;
        public override bool GetStandardValuesSupported(System.ComponentModel.ITypeDescriptorContext context) => true;
        public override bool GetStandardValuesExclusive(System.ComponentModel.ITypeDescriptorContext context) => false;
        public override StandardValuesCollection GetStandardValues(System.ComponentModel.ITypeDescriptorContext context) =>
            new StandardValuesCollection(Optional ? new[] { "" }.Concat(Names()).ToArray() : Names());
    }

    /// <summary>A filter that may be left empty (keep the current filter).</summary>
    public class OptionalFilterConverter : FilterConverter
    {
        protected override bool Optional => true;
    }

    /// <summary>Shows ExposureRequest.FrameTypes as a drop-down list in the property grid.</summary>
    public class FrameTypeConverter : System.ComponentModel.StringConverter
    {
        public override bool GetStandardValuesSupported(System.ComponentModel.ITypeDescriptorContext context) => true;
        public override bool GetStandardValuesExclusive(System.ComponentModel.ITypeDescriptorContext context) => true;
        public override StandardValuesCollection GetStandardValues(System.ComponentModel.ITypeDescriptorContext context) => new StandardValuesCollection(ExposureRequest.FrameTypes);
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
        public override string Title => Lang.T("Autofocus");
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
        [System.ComponentModel.TypeConverter(typeof(FilterConverter))]
        public string Filter { get; set; } = "L";
        public override string Title => Lang.T("Switch filter");
        public override string[] RequiredDevices => new[] { "filter wheel" };
        public override string Summary => Filter;
        protected override async Task Execute(SequenceContext ctx, CancellationToken ct) { await ctx.Capture.SetFilter(Filter, ct); ctx.CurrentFilter = Filter; }
    }

    public class CoolCamera : SequenceItem
    {
        public double Temperature { get; set; } = -10;
        public double Minutes { get; set; } = 10;
        public bool Warm { get; set; }
        public override string Title => Warm ? Lang.T("Warm camera") : Lang.T("Cool camera");
        public override string Summary => Warm ? "" : Lang.T("{0} °C over {1} min", Temperature, Minutes);
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
        public override string Title => Lang.T("Wait");
        public override string Summary => Kind == WaitKind.Seconds ? Lang.T("{0} s", Seconds) : Kind == WaitKind.UntilAltitude ? Lang.T("until target alt > {0}°", AltitudeDeg) : Lang.Option(Kind.ToString());
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
        public override string Title => Unpark ? Lang.T("Unpark telescope") : Lang.T("Park telescope");
        public override string[] RequiredDevices => new[] { "telescope" };
        protected override Task Execute(SequenceContext ctx, CancellationToken ct) => Task.Run(() => { if (Unpark) ctx.Devices.Telescope.Unpark(); else ctx.Devices.Telescope.Park(); }, ct);
    }

    public class DomeShutter : SequenceItem
    {
        public bool Open { get; set; } = true;
        public override string Title => Open ? Lang.T("Open dome") : Lang.T("Close dome");
        protected override async Task Execute(SequenceContext ctx, CancellationToken ct)
        {
            var d = ctx.Devices.Dome; if (d == null) return;
            if (Open) d.OpenShutter(); else d.CloseShutter();
            var want = Open ? ShutterState.Open : ShutterState.Closed;
            var until = DateTime.UtcNow.AddMinutes(5);
            while (d.Shutter != want) { if (DateTime.UtcNow > until) throw new TimeoutException("Dome shutter timeout"); await Task.Delay(1000, ct); }
        }
    }

    /// <summary>The switch box's switch names as a drop-down list (the simulator's when none is connected); others can be typed.</summary>
    public class SwitchNameConverter : System.ComponentModel.StringConverter
    {
        public static string[] Names()
        {
            try
            {
                var sw = DeviceManager.Instance.Switch;
                if (sw != null && sw.Count > 0) return Enumerable.Range(0, sw.Count).Select(sw.GetName).ToArray();
            }
            catch { }
            return new[] { "Dew heater 1", "Dew heater 2", "Fan", "Mount power" };
        }

        public override bool GetStandardValuesSupported(System.ComponentModel.ITypeDescriptorContext context) => true;
        public override bool GetStandardValuesExclusive(System.ComponentModel.ITypeDescriptorContext context) => false;
        public override StandardValuesCollection GetStandardValues(System.ComponentModel.ITypeDescriptorContext context) => new StandardValuesCollection(Names());
    }

    /// <summary>Sets one of the switch box's switches: on (any value above 0) or off, or a level for one that has levels.</summary>
    public class SetSwitch : SequenceItem
    {
        [System.ComponentModel.TypeConverter(typeof(SwitchNameConverter))]
        public string Switch { get; set; } = "Dew heater 1";
        /// <summary>0 is off; an on/off switch is on at any value above 0; one with levels takes the value (up to its highest).</summary>
        public double Value { get; set; } = 1;
        public override string Title => Lang.T("Set switch");
        public override string Summary => Lang.T("{0} to {1}", Switch, Value);
        public override string[] RequiredDevices => new[] { "switch" };

        protected override Task Execute(SequenceContext ctx, CancellationToken ct) => Task.Run(() =>
        {
            var sw = ctx.Devices.Switch;
            int i = Enumerable.Range(0, sw.Count).Where(k => string.Equals(sw.GetName(k), Switch, StringComparison.OrdinalIgnoreCase)).DefaultIfEmpty(-1).First();
            if (i < 0) throw new InvalidOperationException(Lang.T("No switch named {0}.", Switch));
            double max = sw.GetMaxValue(i);
            if (max <= 1) sw.SetSwitch(i, Value > 0);
            else sw.SetValue(i, Math.Max(0, Math.Min(max, Value)));
            ctx.Log(string.Format("{0} set to {1}", Switch, Value));
        }, ct);
    }

    public enum FlatPanelAction { OpenCover, CloseCover, LightOn, LightOff }

    /// <summary>The flat panel: its cover opened or closed (waiting until it has), or its light on at a brightness or off.</summary>
    public class FlatPanel : SequenceItem
    {
        public FlatPanelAction Action { get; set; } = FlatPanelAction.CloseCover;
        /// <summary>For LightOn: from 0 to the panel's highest brightness.</summary>
        public int Brightness { get; set; } = 128;
        public override string Title => Lang.T("Flat panel");
        public override string Summary => Lang.Option(Action.ToString()) + (Action == FlatPanelAction.LightOn ? " " + Brightness : "");
        public override string[] RequiredDevices => new[] { "flat panel" };

        protected override async Task Execute(SequenceContext ctx, CancellationToken ct)
        {
            var c = ctx.Devices.Cover;
            switch (Action)
            {
                case FlatPanelAction.LightOn: c.CalibratorOn(Math.Max(0, Math.Min(c.MaxBrightness, Brightness))); return;
                case FlatPanelAction.LightOff: c.CalibratorOff(); return;
            }
            bool open = Action == FlatPanelAction.OpenCover;
            if (open) c.OpenCover(); else c.CloseCover();
            string want = open ? "Open" : "Closed";
            var until = DateTime.UtcNow.AddMinutes(2);
            while (c.CoverState != want)
            {
                if (DateTime.UtcNow > until) throw new TimeoutException(Lang.T("The flat panel's cover did not finish moving."));
                await Task.Delay(250, ct);
            }
        }
    }

    public class RunScript : SequenceItem
    {
        // a "…" button that opens a file dialog (named by type so this library needs no Windows Forms reference)
        [System.ComponentModel.Editor("System.Windows.Forms.Design.FileNameEditor, System.Design, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a",
            "System.Drawing.Design.UITypeEditor, System.Drawing, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a")]
        public string Command { get; set; } = "";
        public string Arguments { get; set; } = "";
        public int TimeoutSec { get; set; } = 300;
        public override string Title => Lang.T("Run external program");
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
        public override string Title => Lang.T("Note");
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
        public override string Title => Lang.T("Autofocus when HFR rises {0}%", PercentIncrease);
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
        public override string Title => Lang.T("Autofocus after {0} °C change", DeltaC);
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
        public override string Title => Lang.Tn("Autofocus after every exposure", "Autofocus every {0} exposures", Exposures);
        public override async Task Check(SequenceContext ctx, SequenceItem current, CancellationToken ct)
        {
            if (ctx.ExposuresSinceAutofocus >= Exposures) await new RunAutofocus().Run(ctx, ct);
        }
    }

    public class MeridianFlipTrigger : SequenceTrigger
    {
        public double MinutesAfterMeridian { get; set; } = 5;
        public override string Title => Lang.T("Meridian flip {0} min after transit", MinutesAfterMeridian);
        public override async Task Check(SequenceContext ctx, SequenceItem current, CancellationToken ct)
        {
            var tel = ctx.Devices.Telescope;
            if (tel == null || ctx.Target == null || !MeridianFlip.IsDue(tel, MinutesAfterMeridian)) return;
            ctx.Log("Performing meridian flip");
            var app = new SkyContext(ctx.Site, AstroTime.Now).IcrsToApparent(ctx.Target.RaDeg * AstroMath.Deg2Rad, ctx.Target.DecDeg * AstroMath.Deg2Rad);
            // re-centre before guiding resumes, as after unsafe conditions
            await MeridianFlip.Perform(tel, app.RaHours, app.DecDeg, ctx.Devices.Guider, ct, () => new CenterTarget().Run(ctx, ct));
        }
    }

    public class SafetyTrigger : SequenceTrigger
    {
        /// <summary>How often (seconds) the safety monitor is read again while waiting for safe conditions.</summary>
        public double RecheckSeconds { get; set; } = 30;
        public override string Title => Lang.T("Park and close when unsafe");
        public override async Task Check(SequenceContext ctx, SequenceItem current, CancellationToken ct)
        {
            var s = ctx.Devices.Safety;
            if (s == null || s.IsSafe) return;
            ctx.Log("UNSAFE conditions: stopping guiding, parking and closing");
            ctx.Devices.Guider?.StopGuiding();
            if (ctx.Devices.Telescope != null) await Task.Run(() => ctx.Devices.Telescope.Park(), ct);
            if (ctx.Devices.Dome != null) await new DomeShutter { Open = false }.Run(ctx, ct);
            while (!s.IsSafe) await Task.Delay(TimeSpan.FromSeconds(Math.Max(0.05, RecheckSeconds)), ct);
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
                    bool have = d == "camera" ? dm.Camera != null : d == "telescope" ? dm.Telescope != null : d == "focuser" ? dm.Focuser != null : d == "filter wheel" ? dm.FilterWheel != null
                        : d == "switch" ? dm.Switch != null : d == "flat panel" ? dm.Cover != null : true;
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
            var named = missing.Select(d => d == "camera" ? Lang.T("a camera") : d == "telescope" ? Lang.T("a telescope") : d == "focuser" ? Lang.T("a focuser")
                : d == "filter wheel" ? Lang.T("a filter wheel") : d == "switch" ? Lang.T("a switch box") : d == "flat panel" ? Lang.T("a flat panel") : d).ToList();
            string list = named.Count == 1 ? named[0] : Lang.T("{0} and {1}", string.Join(", ", named.Take(named.Count - 1)), named[named.Count - 1]);
            return Lang.Tn("This sequence needs {0}. Connect it in Observatory → Equipment", "This sequence needs {0}. Connect them in Observatory → Equipment", named.Count, list);
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
            ctx.Devices = capture.Devices;
            // guiding when the sequence starts: its light frames keep it
            ctx.GuidingWanted = GuidingPause.Guiding(ctx.Devices.Guider);
            ctx.Log("Sequence started");
            try { await Root.Run(ctx, _cts.Token); ctx.Log("Sequence finished"); }
            catch (OperationCanceledException) { ctx.Log("Sequence stopped"); }
            finally { Running = false; Changed?.Invoke(); }
        }

        public void Stop() => _cts?.Cancel();

        public static readonly Type[] ItemTypes =
        {
            typeof(TargetContainer), typeof(LoopContainer), typeof(SequenceContainer), typeof(SlewToTarget), typeof(CenterTarget), typeof(StartGuiding), typeof(StopGuiding),
            typeof(TakeExposures), typeof(RecordVideo), typeof(RunAutofocus), typeof(SwitchFilter), typeof(CoolCamera), typeof(Wait), typeof(ParkTelescope), typeof(DomeShutter), typeof(SetSwitch), typeof(FlatPanel), typeof(RunScript), typeof(Annotation)
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
