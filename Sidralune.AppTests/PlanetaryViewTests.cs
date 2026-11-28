using System;
using System.Collections.Generic;
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

        [TestMethod]
        public void TestSharpenProfilesAndBatch()
        {
            var view = View<PlanetaryView>("planetary");
            OnUi(() => ((TabbedPanel)typeof(PlanetaryView).GetField("_tabs", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(view)).Select(2));
            Settle();
            string dir = Path.Combine(Root, "sharpen"), watched = Path.Combine(dir, "watched");
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.CreateDirectory(Path.Combine(dir, "Saturn"));
            Directory.CreateDirectory(watched);
            // a colour disc with its red and blue channels off the green one
            Func<int, AstroImage> disc = seed =>
            {
                var ch = Enumerable.Range(0, 3).Select(c => Synthetic.Planet(96, 96, 48 + 1.5 * (1 - c), 48, 30, 0, 1.5, 0.01, seed + c).Data[0]).ToArray();
                return new AstroImage(96, 96, ch);
            };
            string jupiter = Path.Combine(dir, "2026-11-28-2130_4-J_0000.tif"), saturn = Path.Combine(dir, "Saturn", "stack.tif"), unknown = Path.Combine(dir, "planet.tif");
            TiffIO.Write(jupiter, disc(1)); TiffIO.Write(saturn, disc(4)); TiffIO.Write(unknown, disc(7));
            var settings = AppState.Settings;
            var savedProfiles = settings.SharpenProfiles.ToList();
            bool savedApply = settings.ApplySharpenProfile;
            var jup = new SharpenProfile { Object = "Jupiter", Layers = new[] { 2.0, 0.3, 0, 0, 0, 0 }, Denoise = 0.1, DeconvFwhm = 2.5, DeconvIterations = 8, Steps = { new SharpenStep { Kind = SharpenStepKind.AlignChannels } } };
            var box = Field<ComboBox>(view, "_profileBox");
            var layers = Field<Slider[]>(view, "_layers");
            var sharpJob = Field<JobSlot>(view, "_sharpJob");
            Func<string, AstroImage> load = ImageFile.Load;
            Func<AstroImage, AstroImage, double> diff = (a, b) => Enumerable.Range(0, 3).Max(c => Enumerable.Range(0, a.PixelCount).Max(i => Math.Abs(a.Data[c][i] - b.Data[c][i])));
            Func<string> steps = () => OnUi(() => Field<Label>(view, "_stepsLabel").Text);
            try
            {
                settings.SharpenProfiles.Clear();
                settings.SharpenProfiles.Add(jup.Clone());
                OnUi(() => Field<CheckBox>(view, "_applyProfile").Checked = true);
                var img = load(jupiter);
                OnUi(() => Call(view, "SetSharpenImage", img, jupiter));
                CheckTrue("The profile's steps run on opening", WaitFor(() => !sharpJob.Busy && Field<List<SharpenStep>>(view, "_sharpSteps").Count == 1, 20000));
                CheckTrue("The file name chooses Jupiter's profile", OnUi(() => box.Text) == "Jupiter", OnUi(() => box.Text));
                CheckTrue("Its settings are shown", OnUi(() => layers[0].Value == 200 && layers[1].Value == 30 && Field<NumericUpDown>(view, "_decFwhm").Value == 2.5m && Field<NumericUpDown>(view, "_decIt").Value == 8));
                CheckTrue("And its steps listed", steps() == "Steps: Align RGB channels", steps());
                OnUi(() => Button(view, "Revert to the opened image").PerformClick());
                CheckTrue("Revert goes back to the image as opened", OnUi(() => Field<List<SharpenStep>>(view, "_sharpSteps").Count == 0 && Field<AstroImage>(view, "_sharpSource") == Field<AstroImage>(view, "_sharpOriginal")) && steps() == "Steps: none", steps());
                // choosing an object without a profile keeps the settings shown, to make one for it
                OnUi(() => box.SelectedIndex = 0);
                CheckTrue("Other has no profile yet: the settings stay", OnUi(() => box.Text == "Other" && layers[0].Value == 200));
                OnUi(() => layers[0].Value = 150);

                // Sharpen files: each image with its object's profile, the others with the settings shown
                OnUi(() => Call(view, "SharpenFiles", new List<string> { jupiter, saturn, unknown, SharpenBatch.OutputPath(unknown) }));
                CheckTrue("The images are sharpened", WaitFor(() => !sharpJob.Busy, 60000) && File.Exists(SharpenBatch.OutputPath(jupiter)) && File.Exists(SharpenBatch.OutputPath(saturn)) && File.Exists(SharpenBatch.OutputPath(unknown)));
                CheckTrue("A result is not sharpened again", !File.Exists(SharpenBatch.OutputPath(SharpenBatch.OutputPath(unknown))));
                var other = settings.SharpenProfiles.FirstOrDefault(x => x.Object == "");
                CheckTrue("The settings shown became the Other profile", other != null && other.Layers[0] == 1.5 && other.Steps.Count == 0 && settings.SharpenProfiles.Count(x => x.Object == "Jupiter") == 1);
                double dj = diff(load(SharpenBatch.OutputPath(jupiter)), jup.Apply(load(jupiter)));
                double ds = diff(load(SharpenBatch.OutputPath(saturn)), other.Apply(load(saturn)));
                double du = diff(load(SharpenBatch.OutputPath(unknown)), other.Apply(load(unknown)));
                CheckTrue("Jupiter with its profile; Saturn, which has none, and the unknown image with the settings shown", dj <= 2e-5 && ds <= 2e-5 && du <= 2e-5, dj + ", " + ds + ", " + du);
                CheckTrue("It says how many", WaitFor(() => Toasts().Any(x => x == "3 images sharpened and saved beside them, ending in _sharpened.tif")), string.Join(" | ", Toasts()));

                // Watch folder: an image that appears there, also in a folder under it, is sharpened with its profile
                OnUi(() => Call(view, "StartWatching", watched));
                CheckTrue("Watching is shown", OnUi(() => Field<FlatButton>(view, "_watchButton").Text == "Stop watching" && Field<Label>(view, "_watchLabel").Visible && Field<FlatButton>(view, "_watchButton").Enabled));
                string sub = Path.Combine(watched, "AS_P25");
                Directory.CreateDirectory(sub);
                string arriving = Path.Combine(sub, "Jupiter_213045_lapl5.tif");
                // written under another name first, then renamed, as a stacker may
                TiffIO.Write(arriving + ".part", disc(10));
                File.Move(arriving + ".part", arriving);
                CheckTrue("The new image is sharpened", WaitFor(() => File.Exists(SharpenBatch.OutputPath(arriving)) && !Field<JobSlot>(view, "_watchJob").Busy, 30000));
                Settle(1500);
                CheckTrue("With Jupiter's profile", diff(load(SharpenBatch.OutputPath(arriving)), jup.Apply(load(arriving))) <= 2e-5);
                CheckTrue("Its result is not sharpened again", !File.Exists(SharpenBatch.OutputPath(SharpenBatch.OutputPath(arriving))));
                CheckTrue("It says so", WaitFor(() => Toasts().Any(x => x == "Sharpened Jupiter_213045_lapl5.tif")), string.Join(" | ", Toasts()));
                OnUi(() => Call(view, "StopWatching"));
                CheckTrue("Watching stops", OnUi(() => Field<FlatButton>(view, "_watchButton").Text == "Watch folder…" && !Field<Label>(view, "_watchLabel").Visible && Field<FileSystemWatcher>(view, "_watcher") == null));
                string late = Path.Combine(watched, "Jupiter_late.tif");
                TiffIO.Write(late, disc(13));
                Settle(2000);
                CheckTrue("Nothing is sharpened after that", !File.Exists(SharpenBatch.OutputPath(late)));
            }
            finally
            {
                OnUi(() => Call(view, "StopWatching"));
                settings.SharpenProfiles.Clear();
                settings.SharpenProfiles.AddRange(savedProfiles);
                settings.ApplySharpenProfile = savedApply;
                OnUi(() => Field<CheckBox>(view, "_applyProfile").Checked = savedApply);
            }
        }

        [TestMethod]
        public void TestSharpenLiveChain()
        {
            var view = View<PlanetaryView>("planetary");
            OnUi(() => ((TabbedPanel)typeof(PlanetaryView).GetField("_tabs", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(view)).Select(2));
            Settle();
            string dir = Path.Combine(Root, "sharpenchain");
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.CreateDirectory(dir);
            var ch = Enumerable.Range(0, 3).Select(c =>
            {
                var d = Synthetic.Planet(96, 96, 48, 48, 30, 0, 1.5, 0.005, 2 + c).Data[0];
                for (int i = 0; i < d.Length; i++) d[i] *= 0.4f + 0.15f * c;
                return d;
            }).ToArray();
            var img = new AstroImage(96, 96, ch);
            var sharpJob = Field<JobSlot>(view, "_sharpJob");
            var wavelets = Field<JobSlot>(view, "_waveletJob");
            Func<AstroImage> shown = () => OnUi(() => Field<ImageViewer>(view, "_sharpViewer").Image);
            bool savedApply = AppState.Settings.ApplySharpenProfile;
            try
            {
                OnUi(() => Field<CheckBox>(view, "_applyProfile").Checked = false);
                OnUi(() => Call(view, "SetSharpenImage", img, null));
                CheckTrue("The image is sharpened", WaitFor(() => !wavelets.Busy && shown() != null && shown() != img, 10000));
                var titles = OnUi(() => All(view).OfType<Collapsible>().Select(c => c.Title).ToList());
                CheckTrue("The settings in sections", new[] { "Deconvolution", "Wavelets", "Unsharp mask", "Noise reduction", "Limb & halos", "Colour & tone" }.All(titles.Contains), string.Join(", ", titles));

                // a live setting changes the image shown: saturation 0 makes it grey
                OnUi(() => Field<Slider>(view, "_saturation").Value = 0);
                CheckTrue("Saturation 0 shows a grey image", WaitFor(() =>
                {
                    var s = OnUi(() => Field<ImageViewer>(view, "_sharpViewer").Image);
                    return !wavelets.Busy && s != null && s.Channels == 3 && Math.Abs(s.Data[0][48 * 96 + 48] - s.Data[2][48 * 96 + 48]) < 1e-4;
                }, 10000));
                OnUi(() => Field<Slider>(view, "_saturation").Value = 100);
                // the curve: the editor's points reach the profile, and Straight curve takes them off
                var curve = Field<CurveEditor>(view, "_curve");
                OnUi(() => curve.Points = new[] { 0, 0, 0.5, 0.8, 1, 1 });
                CheckTrue("The curve is in the settings", OnUi(() => ((SharpenProfile)Call(view, "CurrentProfile")).Curve).SequenceEqual(new[] { 0, 0, 0.5, 0.8, 1, 1 }));
                int changes = 0;
                OnUi(() => { All(view).OfType<Collapsible>().First(c => c.Title == "Colour & tone").Expanded = true; curve.CurveChanged += (s, e) => changes++; Button(view, "Straight curve").PerformClick(); });
                CheckTrue("Straight curve takes the points off and says so", OnUi(() => curve.Points) == null && changes == 1);

                // deconvolution: the settings the method and the PSF use, and the step it adds
                var method = Field<ComboBox>(view, "_decMethod");
                OnUi(() => { Field<NumericUpDown>(view, "_decFwhm").Value = 2.0m; Field<NumericUpDown>(view, "_decIt").Value = 15; Field<NumericUpDown>(view, "_wienerK").Value = 0.01m; });
                OnUi(() => method.SelectedIndex = 1);
                CheckTrue("Wiener: K, not iterations", OnUi(() => !Field<NumericUpDown>(view, "_decIt").Enabled && Field<NumericUpDown>(view, "_wienerK").Enabled && Field<NumericUpDown>(view, "_decFwhm").Enabled));
                OnUi(() => Button(view, "Deconvolve").PerformClick());
                CheckTrue("A Wiener step", WaitFor(() => !sharpJob.Busy && OnUi(() => Field<Label>(view, "_stepsLabel").Text) == "Steps: Wiener 2.0 px, K 0.01", 20000), OnUi(() => Field<Label>(view, "_stepsLabel").Text));
                string star = Path.Combine(dir, "star.fits");
                var frame = new AstroImage(32, 32, 1);
                var g = Psf.Gaussian(2.2);
                for (int y = 0; y < g.Size; y++) for (int x = 0; x < g.Size; x++) frame.Data[0][(10 + y) * 32 + 12 + x] = g.Data[y * g.Size + x] * 5;
                FitsIO.Write(star, frame);
                OnUi(() => Call(view, "ShowPsf", star));
                CheckTrue("A recorded PSF: named, and no FWHM", OnUi(() => Field<Label>(view, "_psfLabel").Text == "PSF: star.fits" && Field<Label>(view, "_psfLabel").Visible && !Field<NumericUpDown>(view, "_decFwhm").Enabled && Field<ComboBox>(view, "_psfKind").SelectedIndex == 1));
                OnUi(() => method.SelectedIndex = 0);
                OnUi(() => Button(view, "Deconvolve").PerformClick());
                CheckTrue("A Richardson-Lucy step with the recorded PSF", WaitFor(() => !sharpJob.Busy && OnUi(() => Field<Label>(view, "_stepsLabel").Text).EndsWith("→ Deconvolution with star.fits, 15 iterations"), 20000), OnUi(() => Field<Label>(view, "_stepsLabel").Text));
                OnUi(() => Call(view, "ShowPsf", ""));
                CheckTrue("Back to the Gaussian", OnUi(() => !Field<Label>(view, "_psfLabel").Visible && Field<NumericUpDown>(view, "_decFwhm").Enabled && Field<ComboBox>(view, "_psfKind").SelectedIndex == 0));

                // presets: a file's settings are shown and its steps run, as for a profile
                string preset = Path.Combine(dir, "soft.json");
                var p = new SharpenProfile { Object = "Saturn", Layers = new[] { 0.5, 0.2, 0, 0, 0, 0 }, Denoise = 1.0, LuminanceOnly = true, UnsharpAmount = 0.7, NoiseFilter = SharpenNoiseFilter.Sigma, NoiseStrength = 1.2, ProtectLimb = 0.5, SuppressHalos = 0.4,
                    NormalizeColour = true, Saturation = 1.4, Gamma = 1.2, Contrast = 0.3, Curve = new[] { 0, 0, 0.5, 0.6, 1, 1 }, DeconvMethod = DeconvolutionMethod.Wiener, WienerK = 0.05, Steps = { new SharpenStep { Kind = SharpenStepKind.AlignChannels } } };
                File.WriteAllText(preset, Core.Util.Json.Serialize(p, true));
                string profileBefore = OnUi(() => Field<ComboBox>(view, "_profileBox").Text);
                OnUi(() => Call(view, "UsePreset", preset));
                CheckTrue("The preset's steps run", WaitFor(() => !sharpJob.Busy && OnUi(() => Field<Label>(view, "_stepsLabel").Text) == "Steps: Align RGB channels", 20000));
                var now = OnUi(() => (SharpenProfile)Call(view, "CurrentProfile"));
                CheckTrue("Its settings are shown", now.Layers.SequenceEqual(p.Layers) && now.Denoise == 1.0 && now.LuminanceOnly && now.UnsharpAmount == 0.7 && now.NoiseFilter == SharpenNoiseFilter.Sigma && now.NoiseStrength == 1.2
                    && now.ProtectLimb == 0.5 && now.SuppressHalos == 0.4 && now.NormalizeColour && now.Saturation == 1.4 && now.Gamma == 1.2 && now.Contrast == 0.3 && now.Curve.SequenceEqual(p.Curve)
                    && now.DeconvMethod == DeconvolutionMethod.Wiener && now.WienerK == 0.05);
                CheckTrue("The object chosen stays", OnUi(() => Field<ComboBox>(view, "_profileBox").Text) == profileBefore);
                string bad = Path.Combine(dir, "notes.json");
                File.WriteAllText(bad, "not a preset");
                OnUi(() => Call(view, "UsePreset", bad));
                CheckTrue("A file that is not a preset says so", WaitFor(() => Toasts().Any(t => t == "notes.json is not a sharpening preset")), string.Join(" | ", Toasts()));
            }
            finally
            {
                AppState.Settings.ApplySharpenProfile = savedApply;
                OnUi(() => Field<CheckBox>(view, "_applyProfile").Checked = savedApply);
            }
        }
    }
}
