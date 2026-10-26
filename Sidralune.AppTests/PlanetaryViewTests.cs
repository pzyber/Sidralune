using System;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sidralune.Imaging;
using Sidralune.Imaging.IO;
using Sidralune.Imaging.Planetary;
using Sidralune.Tests;
using Sidralune.UI;
using Sidralune.Views;
using static Sidralune.AppTests.AppHost;
using static Sidralune.Tests.Checks;

namespace Sidralune.AppTests
{
    /// <summary>The Planetary workspace.</summary>
    [TestClass]
    public class PlanetaryViewTests : TestBase
    {
        [TestMethod]
        public void TestFramesShownAsRecorded()
        {
            var view = View<PlanetaryView>("planetary");
            var viewers = OnUi(() => All(view).OfType<ImageViewer>().Select(v => v.Stretch).ToList());
            // an automatic stretch blows the black sky around a planet up into noise
            CheckTrue("Every planetary tab shows images without a display stretch", viewers.Count >= 5 && viewers.All(s => s == DisplayStretch.None), string.Join(", ", viewers));
        }

        [TestMethod]
        public void TestDerotationPanelLayout()
        {
            var view = View<PlanetaryView>("planetary");
            OnUi(() => ((TabbedPanel)typeof(PlanetaryView).GetField("_tabs", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(view)).Select(3));
            Settle();
            var add = OnUi(() => Button(view, "Add images…"));
            var first = OnUi(() => Find<Label>(add.Parent.Parent, l => l.Text == "Limb darkening comp."));
            var list = OnUi(() => All(add.Parent.Parent).OfType<ListBox>().First());
            CheckTrue("Without images the panel says so, and the empty list is shown like Photometry's frame list", OnUi(() => Find<Label>(add.Parent.Parent, l => l.Text == "No images yet") != null && list.Visible));
            Func<Control, int> top = c => OnUi(() => c.Parent.PointToScreen(c.Location).Y);
            CheckTrue("The list sits between the options and the buttons, not above the options", top(first) < top(list) && top(list) < top(add), top(first) + ", " + top(list) + ", " + top(add));
        }

        [TestMethod]
        public void TestPreprocessResultToLuckyStacking()
        {
            var view = View<PlanetaryView>("planetary");
            string dir = Path.Combine(Root, "planetary"), outDir = Path.Combine(dir, "out");
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.CreateDirectory(outDir);
            // a small video of a disc drifting on a dark sky
            string video = Path.Combine(dir, "jupiter.ser");
            var rnd = new Random(2);
            using (var ser = new SerWriter(video, 160, 120, 16, 1))
                for (int f = 0; f < 12; f++)
                {
                    var img = new AstroImage(160, 120, 1);
                    double cx = 70 + f, cy = 55 + f / 2.0;
                    for (int y = 0; y < 120; y++)
                        for (int x = 0; x < 160; x++)
                            img.Data[0][y * 160 + x] = (float)((Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)) < 15 ? 0.7 : 0.02) + 0.01 * rnd.NextDouble());
                    ser.Write(img, new DateTime(2026, 10, 24, 23, 0, f, DateTimeKind.Utc));
                }
            var opt = OnUi(() => (PippOptions)All(view).OfType<PropertyGrid>().Select(g => g.SelectedObject).OfType<GridAdapter>().First(a => a.Target is PippOptions).Target);
            string folder = opt.OutputFolder;
            var lucky = Field<JobSlot>(view, "_luckyJob");
            try
            {
                opt.OutputFolder = outDir;
                foreach (var button in new[] { "Sharpen →", "Next stack", "Save stacks…" })
                {
                    int shown = Toasts().Count(t => t.StartsWith("Nothing stacked yet"));
                    OnUi(() => Button(view, button).PerformClick());
                    CheckTrue(button + " says when nothing is stacked yet", WaitFor(() => Toasts().Count(t => t.StartsWith("Nothing stacked yet")) > shown || Toasts().Any(t => t.StartsWith("Nothing stacked yet")) && shown > 0));
                }
                CheckTrue("It speaks of a sequence, which can be a video or images", Toasts().Any(t => t == "Nothing stacked yet: open a sequence and click Analyse & stack"));
                OnUi(() => view.OpenVideo(video));
                CheckTrue("The video opens in the background", WaitFor(() => OnUi(() => !Field<JobSlot>(view, "_ppJob").Busy) && Field<object>(view, "_ppSource") != null, 15000));
                OnUi(() => Button(view, "Stacking →").PerformClick());
                CheckTrue("Before processing the button says what to do", WaitFor(() => Toasts().Any(t => t.Contains("Process the sequence first"))));
                CheckTrue("Nothing is sent to Lucky stacking on its own", Field<object>(view, "_luckySource") == null || !((IFrameSource)Field<object>(view, "_luckySource")).Path.StartsWith(outDir));
                OnUi(() => Button(view, "Process").PerformClick());
                CheckTrue("Pre-processing writes its video", WaitFor(() => OnUi(() => !Field<JobSlot>(view, "_ppJob").Busy) && Directory.GetFiles(outDir, "*.ser").Length > 0, 60000));
                CheckTrue("Still nothing loaded in Lucky stacking until asked", Field<object>(view, "_luckySource") == null || !((IFrameSource)Field<object>(view, "_luckySource")).Path.StartsWith(outDir));
                OnUi(() => Button(view, "Stacking →").PerformClick());
                Func<IFrameSource> opened = () => (IFrameSource)Field<object>(view, "_luckySource");
                CheckTrue("Stacking → opens the result in Lucky stacking", WaitFor(() => opened() != null && opened().Path.StartsWith(outDir) && opened().FrameCount > 0, 15000), opened() != null ? opened().Path : "nothing");
                CheckTrue("Its first frame is shown there", WaitFor(() => OnUi(() => !lucky.Busy)));
                CheckTrue("The button waits while Lucky stacking runs", OnUi(() =>
                {
                    var gate = new System.Threading.ManualResetEventSlim();
                    lucky.Run("Test job", t => gate.Wait(30000));
                    bool locked = !Button(view, "Stacking →").Enabled;
                    gate.Set();
                    return locked;
                }));
                CheckTrue("And comes back afterwards", WaitFor(() => OnUi(() => !lucky.Busy && Button(view, "Stacking →").Enabled), 30000));
            }
            finally { opt.OutputFolder = folder; }
        }
    }
}
