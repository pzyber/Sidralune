using System;
using System.IO;
using System.Linq;
using System.Threading;
using Sidralune.Core.Catalogs;
using Sidralune.Devices;
using Sidralune.Devices.Automation;
using Sidralune.Devices.Simulators;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Sidralune.Tests.Checks;

namespace Sidralune.Tests
{
    [TestClass]
    public class DeviceTests : TestBase
    {
        [TestMethod]
        public void TestSimulatedObservatory()
        {
            CatalogManager.Instance.LoadBuiltIn();
            var dm = new DeviceManager();
            dm.Site.LatitudeDeg = 52; dm.Site.LongitudeDeg = 5;
            foreach (var k in new[] { DeviceKind.Telescope, DeviceKind.Focuser, DeviceKind.FilterWheel, DeviceKind.Camera })
                dm.Connect(new DeviceInfo { Kind = k, Protocol = DeviceProtocol.Simulator, Address = "sim", Name = k + " sim" });
            var tel = (TelescopeSimulator)dm.Telescope;
            tel.SlewRateDegPerSec = 60;
            
            var cap = new CaptureService { Devices = dm, ImageFolder = Path.Combine(Path.GetTempPath(), "SidraluneTests", "capture") };
            // slew near zenith so the simulated field is up
            double lst = tel.SiderealTime;
            Centering.Slew(tel, lst, 52, CancellationToken.None).Wait();
            Check("Telescope reached Dec", tel.Declination, 52, 0.01);
            var res = cap.Expose(new ExposureRequest { Seconds = 1, Target = "Test", Filter = "L" }, CancellationToken.None).Result;
            Info(string.Format("captured {0}, {1} stars, HFR {2:0.00}, saved {3}", res.Image, res.Stars, res.Hfr, Path.GetFileName(res.Path)));
            CheckTrue("Captured image has stars", res.Stars > 20);
            CheckTrue("Image saved", File.Exists(res.Path));
            var af = new Autofocus { Capture = cap, Exposure = 1, Steps = 9, StepSize = 150, Bin = 1 };
            var r = af.Run(CancellationToken.None).Result;
            Info(r.Message);
            CheckTrue("Autofocus succeeded", r.Success);
            Check("Autofocus best position", r.BestPosition, ((FocuserSimulator)dm.Focuser).BestFocus, 80);
            dm.DisconnectAll();
        }

        [TestMethod]
        public void TestSequenceSerialization()
        {
            var seq = Sequencer.Template("M 31", 10.684, 41.269);
            string p = Path.Combine(Path.GetTempPath(), "SidraluneTests", "seq.json");
            var s = new Sequencer { Root = seq };
            s.Save(p);
            var s2 = new Sequencer();
            s2.Load(p);
            CheckTrue("Sequence roundtrip item count", s2.Root.Items.Count == seq.Items.Count, s2.Root.Items.Count + " items");
            var tc = s2.Root.Items.OfType<TargetContainer>().FirstOrDefault();
            CheckTrue("Target container restored", tc != null && Math.Abs(tc.Target.RaDeg - 10.684) < 1e-9 && tc.Items.Count == 6);
        }

        // ------------------------------------------------------------------ planetary video

        private static DeviceManager VideoObservatory()
        {
            CatalogManager.Instance.LoadBuiltIn();
            var dm = new DeviceManager();
            dm.Site.LatitudeDeg = 52; dm.Site.LongitudeDeg = 5;
            foreach (var k in new[] { DeviceKind.Telescope, DeviceKind.FilterWheel, DeviceKind.Camera })
                dm.Connect(new DeviceInfo { Kind = k, Protocol = DeviceProtocol.Simulator, Address = "sim", Name = k + " sim" });
            return dm;
        }

        private static string EmptyFolder(string name)
        {
            string folder = Path.Combine(Path.GetTempPath(), "SidraluneTests", name);
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
            Directory.CreateDirectory(folder);
            return folder;
        }

        [TestMethod]
        public void TestVideoRegions()
        {
            int w, h;
            VideoRequest.ParseRegion("Full sensor", out w, out h);
            CheckTrue("Full sensor is 0 × 0", w == 0 && h == 0);
            VideoRequest.ParseRegion("640 × 480", out w, out h);
            CheckTrue("640 × 480 parsed", w == 640 && h == 480, w + "×" + h);
            VideoRequest.ParseRegion("800x600", out w, out h);
            CheckTrue("800x600 parsed", w == 800 && h == 600, w + "×" + h);
            VideoRequest.ParseRegion("nonsense", out w, out h);
            CheckTrue("Unknown region is the full sensor", w == 0 && h == 0);
            VideoRequest.ParseRegion(null, out w, out h);
            CheckTrue("No region is the full sensor", w == 0 && h == 0);
            bool allParse = VideoRequest.Regions.Skip(1).All(r => { int rw, rh; VideoRequest.ParseRegion(r, out rw, out rh); return rw > 0 && rh > 0; });
            CheckTrue("Every listed region except Full sensor has a size", VideoRequest.Regions[0] == "Full sensor" && allParse);
            var conv = new VideoRegionConverter();
            CheckTrue("Sequencer drop-down offers the same regions", conv.GetStandardValuesExclusive(null)
                && conv.GetStandardValues(null).Cast<string>().SequenceEqual(VideoRequest.Regions));
        }

        [TestMethod]
        public void TestVideoRecording()
        {
            var dm = VideoObservatory();
            string folder = EmptyFolder("video");
            var cap = new CaptureService { Devices = dm, ImageFolder = folder };
            int frameEvents = 0; VideoResult recorded = null;
            cap.VideoFrame += (img, p) => frameEvents++;
            cap.VideoRecorded += r => recorded = r;
            var req = new VideoRequest { ExposureMs = 5, RoiWidth = 320, RoiHeight = 240, MaxFrames = 12, MaxSeconds = 60, Filter = "R", Target = "Jupiter" };
            var res = cap.RecordVideo(req, null, CancellationToken.None).Result;
            Info(string.Format("{0} frames {1}×{2} at {3:0.0} fps, saved {4}", res.Frames, res.Width, res.Height, res.Fps, Path.GetFileName(res.Path)));
            CheckTrue("Recorded the requested frames", res.Frames == 12 && !res.Stopped, res.Frames + " frames");
            CheckTrue("Region of interest size", res.Width == 320 && res.Height == 240, res.Width + "×" + res.Height);
            var cam = dm.Camera;
            CheckTrue("Region centred on the sensor", cam.StartX == ((cam.SensorWidth - 320) / 2 & ~1) && cam.StartY == ((cam.SensorHeight - 240) / 2 & ~1), cam.StartX + "," + cam.StartY);
            CheckTrue("Filter changed", dm.FilterWheel.Names[dm.FilterWheel.Position] == "R");
            CheckTrue("Saved under Planetary\\Jupiter", res.Path != null && File.Exists(res.Path) && Path.GetDirectoryName(res.Path) == Path.Combine(folder, "Planetary", "Jupiter"));
            CheckTrue("WinJUPOS-style file name", res.Path != null && System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(res.Path), @"^\d{4}-\d\d-\d\d-\d{4}_\d-Jupiter\.ser$"), Path.GetFileName(res.Path));
            CheckTrue("No temporary file left", Directory.GetFiles(folder, "*.part", SearchOption.AllDirectories).Length == 0);
            CheckTrue("Frame events raised", frameEvents >= 1, frameEvents + " events");
            CheckTrue("Recorded event raised with the file", recorded != null && recorded.Path == res.Path);
            using (var ser = new Sidralune.Imaging.IO.SerReader(res.Path))
            {
                CheckTrue("SER frame count and size", ser.FrameCount == 12 && ser.Width == 320 && ser.Height == 240, ser.FrameCount + " frames " + ser.Width + "×" + ser.Height);
                CheckTrue("SER is 16-bit", ser.BitDepth == 16);
                bool increasing = true;
                for (int i = 1; i < ser.FrameCount; i++) increasing &= ser.Timestamp(i) > ser.Timestamp(i - 1);
                CheckTrue("Time stamps increase", ser.Timestamp(0).HasValue && increasing);
                var first = ser.Timestamp(0).Value; var last = ser.Timestamp(ser.FrameCount - 1).Value;
                var mid = first.AddTicks((last - first).Ticks / 2);
                string expected = string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:yyyy-MM-dd-HHmm}_{1}-Jupiter.ser", mid, mid.Second / 6);
                CheckTrue("File named after the mid-time", Path.GetFileName(res.Path) == expected, expected);
                var frame = ser.ReadFrame(0);
                CheckTrue("Frame readable", frame != null && frame.Width == 320 && frame.Height == 240);
            }
            // a second recording in the same minute does not overwrite the first
            var res2 = cap.RecordVideo(new VideoRequest { ExposureMs = 5, RoiWidth = 320, RoiHeight = 240, MaxFrames = 3, Target = "Jupiter" }, null, CancellationToken.None).Result;
            CheckTrue("Second recording kept separately", res2.Path != null && res2.Path != res.Path && File.Exists(res.Path) && File.Exists(res2.Path), Path.GetFileName(res2.Path));
            dm.DisconnectAll();
        }

        [TestMethod]
        public void TestVideoRecordingStop()
        {
            var dm = VideoObservatory();
            string folder = EmptyFolder("video-stop");
            var cap = new CaptureService { Devices = dm, ImageFolder = folder };
            var req = new VideoRequest { ExposureMs = 5, RoiWidth = 320, RoiHeight = 240, MaxFrames = 100000, MaxSeconds = 60, Target = "Mars" };
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1)))
            {
                var res = cap.RecordVideo(req, null, cts.Token).Result;
                CheckTrue("Stop ends the recording", res.Stopped && res.Frames > 0 && res.Elapsed.TotalSeconds < 10, res.Frames + " frames in " + res.Elapsed.TotalSeconds.ToString("0.0") + " s");
                CheckTrue("Stopped recording saved", res.Path != null && File.Exists(res.Path));
                using (var ser = new Sidralune.Imaging.IO.SerReader(res.Path))
                    CheckTrue("Stopped recording is a complete SER file", ser.FrameCount == res.Frames, ser.FrameCount + " frames in the file");
            }
            // stopped before the first frame: nothing is saved
            string empty = EmptyFolder("video-none");
            cap.ImageFolder = empty;
            using (var cts = new CancellationTokenSource())
            {
                cts.Cancel();
                var res = cap.RecordVideo(req, null, cts.Token).Result;
                CheckTrue("Nothing saved without frames", res.Path == null && res.Frames == 0 && Directory.GetFiles(empty, "*", SearchOption.AllDirectories).Length == 0);
            }
            dm.DisconnectAll();
            CheckTrue("Recording without a camera fails", cap.RecordVideo(req, null, CancellationToken.None).Exception?.InnerException is InvalidOperationException);
        }

        [TestMethod]
        public void TestLiveView()
        {
            var dm = VideoObservatory();
            string folder = EmptyFolder("live");
            var cap = new CaptureService { Devices = dm, ImageFolder = folder };
            int shown = 0, recordingEvents = 0;
            cap.VideoFrame += (img, p) => recordingEvents++;
            cap.VideoRecorded += r => recordingEvents++;
            VideoResult res;
            using (var cts = new CancellationTokenSource(1500))
                res = cap.LiveView(new VideoRequest { ExposureMs = 5, RoiWidth = 320, RoiHeight = 240, Filter = "G" }, (img, p) => { if (img.Width == 320) shown++; }, cts.Token, 0.1).Result;
            Info(string.Format("{0} frames at {1:0.0} fps, {2} shown", res.Frames, res.Fps, shown));
            CheckTrue("Runs until stopped, with the region and filter of a recording", res.Stopped && res.Width == 320 && res.Height == 240 && dm.FilterWheel.Names[dm.FilterWheel.Position] == "G");
            CheckTrue("Shows some of the frames, not all", shown >= 2 && shown < res.Frames, shown + " of " + res.Frames);
            CheckTrue("Saves nothing and is not a recording", !Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.AllDirectories).Any(f => File.Exists(f)) && recordingEvents == 0);
            dm.DisconnectAll();
            CheckTrue("Live view without a camera fails", cap.LiveView(new VideoRequest(), null, CancellationToken.None).Exception?.InnerException is InvalidOperationException);
        }

        [TestMethod]
        public void TestRecordVideoInstruction()
        {
            var dm = VideoObservatory();
            string folder = EmptyFolder("video-sequence");
            var cap = new CaptureService { Devices = dm, ImageFolder = folder };
            var log = new System.Collections.Generic.List<string>();
            var ctx = new SequenceContext { Devices = dm, Capture = cap, Target = new SequenceTarget { Name = "Saturn" }, Log = log.Add };
            var rv = new RecordVideo { ExposureMs = 5, Region = "320 × 240", MaxFrames = 5, MaxSeconds = 60, Filter = "G" };
            CheckTrue("Needs a camera", rv.RequiredDevices.SequenceEqual(new[] { "camera" }));
            CheckTrue("Offered in the Add menu", Sequencer.ItemTypes.Contains(typeof(RecordVideo)));
            rv.Run(ctx, CancellationToken.None).Wait();
            CheckTrue("Instruction done", rv.Status == ItemStatus.Done, rv.Status + " " + rv.Error);
            var files = Directory.GetFiles(Path.Combine(folder, "Planetary", "Saturn"), "*.ser");
            CheckTrue("Video saved under the target's name", files.Length == 1 && files[0].EndsWith("-Saturn.ser"), files.Length + " file(s)");
            if (files.Length == 1)
                using (var ser = new Sidralune.Imaging.IO.SerReader(files[0]))
                    CheckTrue("Video has the region and frame count", ser.FrameCount == 5 && ser.Width == 320 && ser.Height == 240);
            CheckTrue("Saved file logged", log.Any(l => l.StartsWith("Saved ") && l.Contains("5 frames")), string.Join(" / ", log));
            CheckTrue("Current filter remembered", ctx.CurrentFilter == "G");

            // stopping the sequence keeps the frames so far but stops the sequence there
            var long_ = new RecordVideo { ExposureMs = 5, Region = "320 × 240", MaxFrames = 100000, MaxSeconds = 60 };
            bool cancelled = false;
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1)))
                try { long_.Run(ctx, cts.Token).Wait(); }
                catch (AggregateException ex) { cancelled = ex.InnerException is OperationCanceledException; }
            CheckTrue("Stop stops the sequence", cancelled && long_.Status == ItemStatus.Pending, long_.Status.ToString());
            CheckTrue("Stopped video kept", Directory.GetFiles(Path.Combine(folder, "Planetary", "Saturn"), "*.ser").Length == 2);

            // without a camera the instruction fails instead of hanging
            dm.DisconnectAll();
            var none = new RecordVideo { ExposureMs = 5, MaxFrames = 5 };
            none.Run(ctx, CancellationToken.None).Wait();
            CheckTrue("Fails without a camera", none.Status == ItemStatus.Failed && none.Error == "No camera connected", none.Error);

            // the settings survive saving and loading a sequence
            string p = Path.Combine(Path.GetTempPath(), "SidraluneTests", "video-seq.json");
            var s = new Sequencer { Root = new SequenceContainer { Items = { new RecordVideo { ExposureMs = 7.5, Region = "1280 × 960", Bin = 2, Gain = 250, Offset = 40, Filter = "R", MaxFrames = 3000, MaxSeconds = 90 } } } };
            s.Save(p);
            var s2 = new Sequencer();
            s2.Load(p);
            var back = s2.Root.Items.FirstOrDefault() as RecordVideo;
            CheckTrue("Record video settings round trip", back != null && back.ExposureMs == 7.5 && back.Region == "1280 × 960" && back.Bin == 2 && back.Gain == 250
                && back.Offset == 40 && back.Filter == "R" && back.MaxFrames == 3000 && back.MaxSeconds == 90);
        }
    }
}
