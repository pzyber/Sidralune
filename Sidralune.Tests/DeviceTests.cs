using System;
using System.IO;
using System.Linq;
using System.Threading;
using Sidralune.Core.Catalogs;
using Sidralune.Devices;
using Sidralune.Devices.Automation;
using Sidralune.Devices.Simulators;
using static Sidralune.Tests.Program;

namespace Sidralune.Tests
{
    public static class DeviceTests
    {
        public static void TestSimulatedObservatory()
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

        public static void TestSequenceSerialization()
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
    }
}
