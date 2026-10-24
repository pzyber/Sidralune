using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sidralune.Core;
using Sidralune.Core.Catalogs;
using Sidralune.Core.Time;
using Sidralune.Core.Util;
using Sidralune.Tests;
using static Sidralune.AppTests.AppHost;
using static Sidralune.Tests.Checks;

namespace Sidralune.AppTests
{
    /// <summary>Settings, profiles, time control, selection and background tasks.</summary>
    [TestClass]
    public class AppStateTests : TestBase
    {
        private static string SettingsFile => Directory.GetFiles(AppPaths.Settings, "*.json").FirstOrDefault(f => Path.GetFileName(f).StartsWith("settings", StringComparison.OrdinalIgnoreCase));

        [TestMethod]
        public void TestSettingsRoundTrip()
        {
            OnUi(() =>
            {
                var s = AppState.Settings;
                s.Sites.Add(new Observer { Name = "Test site", LatitudeDeg = -33.8678, LongitudeDeg = 151.2, ElevationM = 58, SqmZenith = 21.4, HorizonProfile = new List<double[]> { new[] { 0.0, 10 }, new[] { 180.0, 5 } } });
                s.Telescopes.Add(new TelescopeProfile { Name = "Test 130/650", ApertureMm = 130, FocalMm = 650 });
                s.MpcStationCode = "Z80"; s.AavsoObserverCode = "XYZ"; s.SkyChartMagLimit = 7.25; s.UseRefraction = false;
                AppState.Save();
            });
            string path = SettingsFile;
            CheckTrue("Settings saved under the data folder", path != null && path.StartsWith(Root), path ?? "no file");
            var json = path != null ? File.ReadAllText(path) : "";
            CheckTrue("Settings file is readable JSON with the new site", json.Contains("Test site") && Json.ParseObject(json) != null);
            OnUi(() =>
            {
                var before = AppState.Settings;
                AppState.Settings = new Settings();
                AppState.Load();
                var s = AppState.Settings;
                var site = s.Sites.FirstOrDefault(o => o.Name == "Test site");
                CheckTrue("Site restored", site != null && site.LatitudeDeg == -33.8678 && site.ElevationM == 58 && site.HorizonProfile.Count == 2 && site.HorizonProfile[1][1] == 5);
                CheckTrue("Profiles and options restored", s.Telescopes.Any(t => t.Name == "Test 130/650" && t.FocalMm == 650) && s.MpcStationCode == "Z80" && s.AavsoObserverCode == "XYZ" && s.SkyChartMagLimit == 7.25 && !s.UseRefraction);
                CheckTrue("Image folder restored", s.ImageFolder == before.ImageFolder);
                s.Sites.Remove(site);
                s.Telescopes.RemoveAll(t => t.Name == "Test 130/650");
                s.UseRefraction = true;
                AppState.Save();
            });
        }

        [TestMethod]
        public void TestDamagedSettingsFile()
        {
            OnUi(AppState.Save);
            string path = SettingsFile;
            string good = File.ReadAllText(path);
            try
            {
                File.WriteAllText(path, good.Substring(0, good.Length / 2));
                int mark = LogMark();
                OnUi(() =>
                {
                    var keep = AppState.Settings;
                    AppState.Load();
                    CheckTrue("A cut-off settings file is reported, not half loaded", AppState.Settings == keep && Log.Snapshot().Skip(mark).Any(e => e.Source == "Settings" && e.Level == Log.Level.Warning));
                });
                File.WriteAllText(path, "not json at all");
                OnUi(() => { AppState.Load(); CheckTrue("Garbage keeps the current settings", AppState.Settings.Sites.Count > 0); });
            }
            finally { File.WriteAllText(path, good); }
        }

        [TestMethod]
        public void TestDefaultSite()
        {
            string path = SettingsFile;
            string saved = File.ReadAllText(path);
            try
            {
                File.Delete(path);
                OnUi(() =>
                {
                    var keep = AppState.Settings;
                    AppState.Settings = new Settings();
                    AppState.Load();
                    var site = AppState.Site;
                    CheckTrue("First start: a home site from the time zone", AppState.Settings.Sites.Count == 1 && site.Name == "Home" && site.LatitudeDeg == 52 && Math.Abs(site.LongitudeDeg - Math.Round(TimeZoneInfo.Local.GetUtcOffset(DateTime.Now).TotalHours * 15, 1)) < 1e-9);
                    CheckTrue("Device manager follows the site", Devices.DeviceManager.Instance.Site == site);
                    AppState.Settings = keep;
                });
            }
            finally { File.WriteAllText(path, saved); OnUi(AppState.Load); }
        }

        [TestMethod]
        public void TestInstrumentAndRig()
        {
            OnUi(() =>
            {
                var s = AppState.Settings;
                int t = s.ActiveTelescope, e = s.ActiveEyepiece, c = s.ActiveCamera;
                s.ActiveTelescope = 0; s.ActiveEyepiece = 0; s.ActiveCamera = 0;
                var ins = AppState.Instrument;
                Check("Magnification from telescope and eyepiece", ins.Magnification, s.Telescopes[0].FocalMm / s.Eyepieces[0].FocalMm, 1e-9);
                Check("True field = apparent field / magnification", ins.TrueFieldDeg, s.Eyepieces[0].AfovDeg / ins.Magnification, 1e-6);
                var rig = AppState.ImagingRig;
                Check("Pixel scale of the imaging rig", rig.PixelScaleArcsec, 206.265 * s.Cameras[0].PixelUm / s.Telescopes[0].FocalMm, 1e-9);
                CheckTrue("Rig named after camera and telescope", rig.Name.Contains(s.Cameras[0].Name) && rig.Name.Contains(s.Telescopes[0].Name));
                s.ActiveTelescope = 99;
                CheckTrue("An out-of-range profile index does not crash", AppState.Instrument != null && AppState.ImagingRig != null);
                s.ActiveTelescope = t; s.ActiveEyepiece = e; s.ActiveCamera = c;
                var eye = AppState.Eye;
                CheckTrue("Observer eye from settings", eye.Age == s.ObserverAge && eye.Experience == s.ObserverExperience);
            });
        }

        [TestMethod]
        public void TestTimeControl()
        {
            int changes = 0;
            EventHandler h = (s, e) => Interlocked.Increment(ref changes);
            AppState.TimeChanged += h;
            try
            {
                var t = AstroTime.FromCalendar(2026, 1, 10, 22);
                AppState.SetTime(t);
                CheckTrue("Set time pauses at that time", !AppState.IsLive && Math.Abs(AppState.Now.JdUtc - t.JdUtc) < 1e-9);
                AppState.Step(1);
                Check("Step one day", AppState.Now.JdUtc - t.JdUtc, 1, 1e-9);
                AppState.Play(3600);
                double j0 = AppState.Now.JdUtc;
                Thread.Sleep(500);
                Check("Playing at 3600× runs 30 min in 0.5 s (days)", AppState.Now.JdUtc - j0, 0.5 * 3600 / 86400.0, 0.004);
                AppState.GoLive();
                CheckTrue("Back to live time", AppState.IsLive && Math.Abs(AppState.Now.JdUtc - AstroTime.Now.JdUtc) < 1e-5);
                CheckTrue("Each change raises TimeChanged", changes >= 4, changes.ToString());
            }
            finally { AppState.TimeChanged -= h; AppState.GoLive(); }
        }

        [TestMethod]
        public void TestSelection()
        {
            SelectedObject got = null;
            Action<SelectedObject> h = o => got = o;
            AppState.ObjectSelected += h;
            try
            {
                var vega = CatalogManager.Instance.Find("Vega");
                var sel = SelectedObject.From(vega);
                CheckTrue("Selected object from a catalogue star", sel.Kind == SelectedKind.Star && sel.Name.Contains("Vega") && Math.Abs(sel.RaDeg - 279.23) < 0.05 && sel.Catalog == vega);
                var m31 = SelectedObject.From(CatalogManager.Instance.Find("M31") ?? CatalogManager.Instance.Find("M 31"));
                CheckTrue("Deep-sky object with a size", m31.Kind == SelectedKind.DeepSky && m31.SizeArcmin > 60);
                OnUi(() => AppState.Select(sel));
                CheckTrue("Selecting raises ObjectSelected", got == sel && AppState.Selection == sel);
            }
            finally { AppState.ObjectSelected -= h; }
        }

        [TestMethod]
        public void TestBackgroundTasks()
        {
            bool done = false, onUiThread = false;
            var t = Tasks.Run("Test work", bt => { Tasks.Report(bt, 0.5, "half"); Thread.Sleep(100); }, () => { done = true; onUiThread = !App.InvokeRequired; });
            CheckTrue("A finished task calls back on the UI thread", WaitFor(() => done) && onUiThread && t.Done && t.Error == null && t.Progress == 0.5 && t.Status == "half");
            Exception err = null;
            var f = Tasks.Run("Failing work", bt => { throw new InvalidOperationException("nope"); }, () => done = false, ex => err = ex);
            CheckTrue("A failed task reports its error and skips the success call", WaitFor(() => err != null) && err.Message == "nope" && f.Error == err && done);
            bool cancelledDone = false;
            var c = Tasks.Run("Cancelled work", bt => { while (true) { bt.Cts.Token.ThrowIfCancellationRequested(); Thread.Sleep(20); } }, () => cancelledDone = true);
            Thread.Sleep(100);
            c.Cts.Cancel();
            CheckTrue("A cancelled task ends without the success call", WaitFor(() => c.Done) && c.Status == "Cancelled" && !cancelledDone);
            CheckTrue("Finished tasks leave the running list", WaitFor(() => { lock (Tasks.Running) return !Tasks.Running.Contains(t) && !Tasks.Running.Contains(f) && !Tasks.Running.Contains(c); }));
            int failedToasts = Toasts().Count(x => x.StartsWith("Quiet failure failed"));
            Tasks.Run("Quiet failure", bt => { throw new Exception("shown"); });
            CheckTrue("A failure without a handler is shown as a notification", WaitFor(() => Toasts().Count(x => x.StartsWith("Quiet failure failed: shown")) > failedToasts));
        }
    }
}
