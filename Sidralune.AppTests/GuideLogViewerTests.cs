using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sidralune.Tests;
using Sidralune.UI;
using Sidralune.Views;
using static Sidralune.AppTests.AppHost;
using static Sidralune.Tests.Checks;

namespace Sidralune.AppTests
{
    [TestClass]
    public class GuideLogViewerTests : TestBase
    {
        /// <summary>An hour guided on a mount with a periodic error of ±4 px (±6″) over 480 s, dithering every 5 minutes.</summary>
        private static string WriteLog(string dir)
        {
            var sb = new StringBuilder("Time,Event,X,Y,Offset X,Offset Y,RA px,Dec px,RA ms,Dec ms,SNR,Flux,HFD,Stars,Note\n");
            var t0 = new DateTime(2026, 11, 26, 22, 0, 0);
            sb.Append("22:00:01.000,Settings,,,,,,,0,0,,,,,\"scale 1.500\"\"/px, exposure 2 s, worm period 480 s, RA rate 3.000 px/s\"\n");
            sb.Append("22:00:02.000,Guiding,,,,,,,0,0,,,,,lock 100 200\n");
            double cum = 0, lockRa = 0;
            var rng = new Random(3);
            for (double t = 10; t < 3600; t += 2)
            {
                double e = 4 * Math.Sin(2 * Math.PI * t / 480) - 3 * cum - lockRa + 0.1 * (rng.NextDouble() - 0.5);
                int ms = (int)Math.Round(0.7 * e / 3 * 1000);
                sb.AppendFormat(CultureInfo.InvariantCulture, "{0:HH:mm:ss.fff},Guide,100,200,0,0,{1:0.000},0.010,{2},0,40,0.1,2,5,\n", t0.AddSeconds(t), e, ms);
                cum += ms / 1000.0;
                if ((int)t % 300 == 0) { lockRa += 4; sb.AppendFormat(CultureInfo.InvariantCulture, "{0:HH:mm:ss.fff},Dither,,,,,,,0,0,,,,,4 0\n", t0.AddSeconds(t + 0.5)); }
                if ((int)t % 300 == 10 && t > 300) sb.AppendFormat(CultureInfo.InvariantCulture, "{0:HH:mm:ss.fff},Settled,,,,,,,0,0,,,,,\n", t0.AddSeconds(t + 0.5));
            }
            string path = Path.Combine(dir, "Guide 2026-11-26 220000.csv");
            File.WriteAllText(path, sb.ToString());
            return path;
        }

        [TestMethod]
        public void TestGuideLogViewer()
        {
            string dir = Path.Combine(Path.GetTempPath(), "SidraluneAppTests", "guide-log-viewer");
            Directory.CreateDirectory(dir);
            string log = WriteLog(dir);
            var view = View<ObservatoryView>("observatory");
            GuideLogWindow w = null;
            try
            {
                OnUi(() => Button(view, "Guide log viewer").PerformClick());
                CheckTrue("Guide log viewer opens the window", WaitFor(() => OnUi(() => (w = Application.OpenForms.OfType<GuideLogWindow>().FirstOrDefault()) != null)));
                CheckTrue("This session is greyed out without a log of the Sidralune guider's", OnUi(() =>
                {
                    var p = Sidralune.Devices.DeviceManager.Instance.BuiltinGuider.GuideLogPath;
                    return Button(w, "This session").Enabled == (!string.IsNullOrEmpty(p) && File.Exists(p));
                }));
                OnUi(() => w.Load(log));
                var grid = OnUi(() => Find<DataGridView>(w, g => true));
                CheckTrue("A row for the session and one for each frame between dithers", OnUi(() => grid.Rows.Count == 13 && (string)grid.Rows[0].Cells[0].Value == "Session"), OnUi(() => grid.Rows.Count.ToString()));
                CheckTrue("…in arcseconds", OnUi(() => ((string)grid.Rows[1].Cells[4].Value).EndsWith("″")));
                Func<string> spectrumText = () => OnUi(() => All(w).OfType<Label>().Select(l => l.Text).FirstOrDefault(t => t.Contains("Largest:")) ?? "");
                CheckTrue("The spectrum finds the periodic error", spectrumText().StartsWith("The mount's own RA error") && spectrumText().Contains("Periodic error: ±") && spectrumText().Contains("at 8.0 min"), spectrumText());
                OnUi(() => Button(w, "The mount's own error").PerformClick());
                CheckTrue("…and without the corrections added back, what guiding left", spectrumText().StartsWith("The RA error left after guiding"), spectrumText());
                string other = Path.Combine(dir, "notes.txt");
                File.WriteAllText(other, "nothing to see\n");
                OnUi(() => w.Load(other));
                CheckTrue("A file that is not a guide log says so", OnUi(() => All(w).OfType<Label>().Any(l => l.Text.Contains("Not a guide log from Sidralune or PHD2."))) && OnUi(() => grid.Rows.Count == 0));
            }
            finally
            {
                if (w != null) OnUi(() => w.Close());
            }
        }
    }
}
