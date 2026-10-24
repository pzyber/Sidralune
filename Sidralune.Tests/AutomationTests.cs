using System;
using System.Linq;
using System.Threading;
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
    /// <summary>Plate-solve centring and three-point polar alignment end to end on the simulated observatory.</summary>
    [TestClass]
    public class AutomationTests : TestBase
    {
        [TestMethod]
        public void TestCenteringAndPolarAlignment()
        {
            var dm = new DeviceManager();
            dm.Site.LatitudeDeg = 52; dm.Site.LongitudeDeg = 5;
            foreach (var k in new[] { DeviceKind.Telescope, DeviceKind.Camera })
                dm.Connect(new DeviceInfo { Kind = k, Protocol = DeviceProtocol.Simulator, Name = k + " sim" });
            var tel = (TelescopeSimulator)dm.Telescope;
            tel.SlewRateDegPerSec = 90;
            // the camera's extra pseudo-stars (an aid when only a sparse catalog is loaded) are in no catalog to solve with
            ((CameraSimulator)dm.Camera).SyntheticFieldStars = false;

            // a dense field along the path of the test (the built-in catalog alone is too sparse for a 0.8° field):
            // the simulated camera renders these stars and the plate solver matches them
            var cm = CatalogManager.Instance;
            cm.LoadBuiltIn();
            var now = new SkyContext(dm.Site, AstroTime.Now);
            double raTarget = AstroMath.NormalizeDeg(now.ApparentToIcrs(AstroMath.NormalizeRad(now.Last - 0.3 * AstroMath.Hour2Rad), 45 * AstroMath.Deg2Rad).RaHours * 15);
            double decTarget = 45;
            var rnd = new Random(17);
            for (int i = 0; i < 40000; i++)
            {
                double ra = raTarget - 4 + 64 * rnd.NextDouble(), dec = 41.5 + 7 * rnd.NextDouble();
                cm.Stars.Add(new Star { Ra = AstroMath.NormalizeDeg(ra) * AstroMath.Deg2Rad, Dec = dec * AstroMath.Deg2Rad, Mag = 8 + 3.5 * Math.Pow(rnd.NextDouble(), 0.4) });
            }
            cm.Stars.Finish();
            try
            {
                var cap = new CaptureService { Devices = dm };
                tel.PointingErrorRa = 0.25; tel.PointingErrorDec = -0.2; // a poorly synced mount: 15′ and 12′ off
                var centring = new Centering { Capture = cap, Exposure = 1, Bin = 2, ToleranceArcmin = 1 };
                var steps = new System.Collections.Generic.List<string>();
                centring.Status += steps.Add;
                var wcs = centring.Center(raTarget, decTarget, CancellationToken.None).Result;
                double cra, cdec;
                wcs.Centre(out cra, out cdec);
                double err = AstroMath.Separation(cra * AstroMath.Deg2Rad, cdec * AstroMath.Deg2Rad, raTarget * AstroMath.Deg2Rad, decTarget * AstroMath.Deg2Rad) * AstroMath.Rad2Deg * 60;
                CheckTrue("Centred within 1′ after correcting a 19′ pointing error", err <= 1, err.ToString("0.00") + "′ after " + steps.Count(s => s.StartsWith("Centering attempt")) + " attempt(s)");
                CheckTrue("The first frame measured the pointing error", steps.Any(s => s.StartsWith("Pointing error") && double.Parse(s.Substring(15).TrimEnd('\'')) > 10), string.Join(" | ", steps));
                CheckTrue("The mount was synced to the solution", Math.Abs(tel.PointingErrorRa) < 0.05 && Math.Abs(tel.PointingErrorDec) < 0.05, tel.PointingErrorRa.ToString("0.000") + ", " + tel.PointingErrorDec.ToString("0.000"));
                Check("Solved pixel scale at bin 2 (″/px)", wcs.PixelScale, 206.265 * 3.76 * 2 / dm.FocalLengthMm, 0.02);

                var pa = new PolarAlignment { Capture = cap, Exposure = 1, Bin = 2, StepDeg = 20 };
                var r = pa.Run(CancellationToken.None).Result;
                Info(r.Advice);
                // the simulated mount is perfectly aligned; only refraction at the pole (≈0.8′ in altitude) remains
                CheckTrue("Polar alignment of a perfect mount", r.TotalErrorArcmin < 3 && Math.Abs(r.AzErrorArcmin) < 2, string.Format("alt {0:0.00}′, az {1:0.00}′", r.AltErrorArcmin, r.AzErrorArcmin));
                Check("Rotation axis at the pole", r.AxisDec, 90, 0.05);

                dm.Disconnect(DeviceKind.Telescope);
                bool noMount = false;
                try { new Centering { Capture = cap }.Center(0, 0, CancellationToken.None).Wait(); } catch (AggregateException ex) { noMount = ex.InnerException.Message == "No telescope connected"; }
                CheckTrue("Centring needs a telescope", noMount);
            }
            finally
            {
                dm.DisconnectAll();
                cm.LoadBuiltIn(); // drop the test stars from the shared catalog
            }
        }
    }
}
