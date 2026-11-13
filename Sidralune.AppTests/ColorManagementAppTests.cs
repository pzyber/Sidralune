using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sidralune.Imaging;
using Sidralune.Imaging.ColorManagement;
using Sidralune.Tests;
using Sidralune.UI;
using Sidralune.Views;
using static Sidralune.AppTests.AppHost;
using static Sidralune.Tests.Checks;

namespace Sidralune.AppTests
{
    /// <summary>Colour management in the interface: the Colour tab, the colour-managed image viewer, assign/convert, proofing.</summary>
    [TestClass]
    public class ColorManagementAppTests : TestBase
    {
        /// <summary>A viewer in a small window, showing images unstretched.</summary>
        private static Tuple<Form, ImageViewer> Viewer()
        {
            return OnUi(() =>
            {
                var f = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-3000, 200), Size = new Size(200, 150), ShowInTaskbar = false };
                var v = new ImageViewer { Dock = DockStyle.Fill, Stretch = DisplayStretch.None };
                f.Controls.Add(v);
                f.Show(App);
                return Tuple.Create(f, v);
            });
        }

        /// <summary>Shows an image and waits for its new bitmap; returns the colour of pixel (x, 0).</summary>
        private static Color Render(ImageViewer v, AstroImage img, int x = 0, Action before = null)
        {
            var old = OnUi(() => Field<Bitmap>(v, "_bitmap"));
            OnUi(() => { before?.Invoke(); v.SetImage(img, false); });
            if (!WaitFor(() => { var b = Field<Bitmap>(v, "_bitmap"); return b != null && b != old; }, 10000)) return Color.Empty;
            return OnUi(() => Field<Bitmap>(v, "_bitmap").GetPixel(x, 0));
        }

        private static AstroImage Rgb(IccProfile p, params float[][] pixels)
        {
            var img = new AstroImage(pixels.Length, 1, 3) { Profile = p };
            for (int i = 0; i < pixels.Length; i++) for (int c = 0; c < 3; c++) img.Data[c][i] = pixels[i][c];
            return img;
        }

        private static bool Near(Color c, int r, int g, int b, int tol = 2) => Math.Abs(c.R - r) <= tol && Math.Abs(c.G - g) <= tol && Math.Abs(c.B - b) <= tol;
        private static string S(Color c) => c.R + "," + c.G + "," + c.B;

        [TestMethod]
        public void TestColourManagedViewer()
        {
            var keep = MonitorColor.Override;
            var v = Viewer();
            try
            {
                OnUi(() => { AppState.Settings.ColorManagement = true; AppState.ApplyColorSettings(); });
                // sRGB red on a ProPhoto RGB "monitor" (worked out by hand: 0.7023, 0.2759, 0.1036)
                MonitorColor.Override = IccProfiles.ProPhoto;
                var c = Render(v.Item2, Rgb(IccProfiles.Srgb, new[] { 1f, 0f, 0f }));
                CheckTrue("sRGB red is converted to the monitor's profile", Near(c, 179, 70, 26), S(c));
                // an untagged image is in the working profile (ProPhoto): nothing to convert
                c = Render(v.Item2, Rgb(null, new[] { 0.6f, 0.4f, 0.2f }));
                CheckTrue("An untagged image in the working profile on a monitor with that profile is unchanged", Near(c, 153, 102, 51), S(c));
                // a grey image follows its curve: gamma 2.2 grey 0.5 on an sRGB monitor
                MonitorColor.Override = IccProfiles.Srgb;
                var mono = new AstroImage(1, 1, 1) { Profile = IccProfiles.ById("gray-22") };
                mono.Data[0][0] = 0.5f;
                c = Render(v.Item2, mono);
                int expected = (int)Math.Round(ToneCurve.LinearToSrgb(Math.Pow(0.5, 563 / 256.0)) * 255);
                CheckTrue("A grey image is shown through its own curve", Near(c, expected, expected, expected), S(c) + " vs " + expected);
                var readout = OnUi(() =>
                {
                    v.Item2.SetImage(Rgb(null, new[] { 0.5f, 0.5f, 0.5f }), false);
                    var at = v.Item2.ImageToScreen(0.5, 0.5);
                    return v.Item2.Readout(new Point((int)at.X, (int)at.Y));
                });
                CheckTrue("The readout names the image's profile", readout.Contains("ProPhoto RGB"), readout);

                // switched off: values go to the screen as they are
                OnUi(() => { AppState.Settings.ColorManagement = false; AppState.ApplyColorSettings(); });
                c = Render(v.Item2, Rgb(IccProfiles.Srgb, new[] { 1f, 0f, 0f }));
                CheckTrue("With colour management off nothing is converted", Near(c, 255, 0, 0, 0), S(c));
                OnUi(() => { AppState.Settings.ColorManagement = true; AppState.ApplyColorSettings(); });

                // night vision mode: brightness only, no conversion
                MonitorColor.Override = IccProfiles.ProPhoto;
                c = Render(v.Item2, Rgb(IccProfiles.Srgb, new[] { 1f, 1f, 1f }), 0, () => App.SetNightMode(true));
                CheckTrue("Night vision mode shows brightness in red, unconverted", c.R >= 250 && c.G < 20 && c.B < 20, S(c));
            }
            finally
            {
                OnUi(() => { App.SetNightMode(false); AppState.Settings.ColorManagement = true; AppState.ApplyColorSettings(); v.Item1.Dispose(); });
                MonitorColor.Override = keep;
            }
        }

        [TestMethod]
        public void TestProofingAndGamutWarning()
        {
            var keep = MonitorColor.Override;
            var v = Viewer();
            try
            {
                OnUi(() => { AppState.Settings.ColorManagement = true; AppState.Settings.ProofingProfile = "srgb"; AppState.Settings.GamutWarningColor = "#808080"; AppState.ApplyColorSettings(); });
                // a ProPhoto green far outside sRGB, next to a grey
                var img = Rgb(IccProfiles.ProPhoto, new[] { 0f, 1f, 0f }, new[] { 0.5f, 0.5f, 0.5f });
                MonitorColor.Override = IccProfiles.Srgb;
                OnUi(() => v.Item2.GamutWarning = true);
                var c = Render(v.Item2, img, 0);
                var grey = OnUi(() => Field<Bitmap>(v.Item2, "_bitmap").GetPixel(1, 0));
                CheckTrue("The gamut warning marks a colour the monitor cannot show", Near(c, 128, 128, 128, 0), S(c));
                CheckTrue("…and leaves the others", !Near(grey, 128, 128, 128, 0) && Math.Abs(grey.R - grey.G) <= 2, S(grey));
                OnUi(() => v.Item2.GamutWarning = false);

                // proofing in sRGB on a ProPhoto monitor: the green becomes sRGB's greenest green
                MonitorColor.Override = IccProfiles.ProPhoto;
                var unproofed = Render(v.Item2, img, 0);
                var proofed = Render(v.Item2, img, 0, () => v.Item2.Proof = true);
                // shown in ProPhoto; back in sRGB terms the unproofed green is far outside, the proofed one inside
                // (Windows maps it to a nearby colour sRGB can show, not by clipping each channel)
                var toSrgb = ColorTransform.Create(IccProfiles.ProPhoto, IccProfiles.Srgb, RenderingIntent.RelativeColorimetric);
                var a = new[] { unproofed.R / 255.0, unproofed.G / 255.0, unproofed.B / 255.0 };
                var b = new[] { proofed.R / 255.0, proofed.G / 255.0, proofed.B / 255.0 };
                toSrgb.Pixel(a); toSrgb.Pixel(b);
                CheckTrue("Unproofed, the green is shown as it is (outside sRGB)", Near(unproofed, 0, 255, 0) && a.Any(x => x < -0.1 || x > 1.1), S(unproofed));
                CheckTrue("Proofed in sRGB, it is a green sRGB can show", b.All(x => x > -0.03 && x < 1.03) && b[1] > 0.9 && b[0] < 0.2,
                    S(proofed) + " = sRGB " + string.Join(", ", b.Select(x => x.ToString("0.000"))));
                CheckTrue("Ctrl+Shift+O and Ctrl+Shift+Y switch proofing and the gamut warning", OnUi(() =>
                {
                    var m = typeof(Control).GetMethod("OnKeyDown", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    m.Invoke(v.Item2, new object[] { new KeyEventArgs(Keys.Control | Keys.Shift | Keys.O) });
                    m.Invoke(v.Item2, new object[] { new KeyEventArgs(Keys.Control | Keys.Shift | Keys.Y) });
                    return !v.Item2.Proof && v.Item2.GamutWarning;
                }));
            }
            finally
            {
                OnUi(() => { v.Item2.Proof = false; v.Item2.GamutWarning = false; v.Item1.Dispose(); });
                MonitorColor.Override = keep;
            }
        }

        [TestMethod]
        public void TestAssignAndConvertInProcess()
        {
            var view = View<ProcessingView>("processing");
            var adobe = IccProfiles.ById("adobergb");
            OnUi(() => view.OpenImage(Rgb(IccProfiles.Srgb, new[] { 1f, 0f, 0f }, new[] { 0.2f, 0.4f, 0.6f }), "test"));
            Func<AstroImage> img = () => OnUi(() => Field<AstroImage>(view, "_img"));
            Func<bool> idle = () => !Field<JobSlot>(view, "_job").Busy;
            CheckTrue("The opened image keeps its sRGB profile (Keep is the default)", img().Profile == IccProfiles.Srgb);
            OnUi(() => view.ApplyProfileStep(false, adobe, RenderingIntent.Perceptual));
            CheckTrue("Assign: the profile changes, the values stay", WaitFor(() => idle() && Field<AstroImage>(view, "_img").Profile == adobe) && img().Data[0][0] == 1f && img().Data[1][0] == 0f);
            OnUi(() => Call(view, "Undo"));
            CheckTrue("Undo brings the old profile back", img().Profile == IccProfiles.Srgb);
            OnUi(() => view.ApplyProfileStep(true, IccProfiles.ProPhoto, RenderingIntent.RelativeColorimetric));
            CheckTrue("Convert: the values change, the colours stay", WaitFor(() => idle() && Field<AstroImage>(view, "_img").Profile == IccProfiles.ProPhoto)
                && Math.Abs(img().Data[0][0] - 0.7023) < 2e-3 && Math.Abs(img().Data[1][0] - 0.2759) < 2e-3, img().Data[0][0] + ", " + img().Data[1][0]);
            CheckTrue("The steps are in the history", OnUi(() => Field<System.Collections.IList>(view, "_undo").Count) >= 2);
        }

        [TestMethod]
        public void TestColourSettingsTab()
        {
            var view = View<SettingsView>("settings");
            var st = OnUi(() => new { AppState.Settings.WorkingProfile, AppState.Settings.RenderingIntent, AppState.Settings.OnProfileMismatch, AppState.Settings.OnMissingProfile, AppState.Settings.EmbedProfiles, AppState.Settings.Export8BitAsSrgb, AppState.Settings.ColorManagement });
            CheckTrue("Defaults: on, ProPhoto RGB, relative colorimetric, keep, leave untagged, embed, 8-bit keeps its profile",
                st.ColorManagement && st.WorkingProfile == "prophoto" && st.RenderingIntent == "RelativeColorimetric" && OnUi(() => ColorSettings.Intent) == RenderingIntent.RelativeColorimetric && st.OnProfileMismatch == "Keep" && st.OnMissingProfile == "LeaveUntagged" && st.EmbedProfiles && !st.Export8BitAsSrgb,
                st.ToString());
            var card = OnUi(() => All(view).OfType<Card>().FirstOrDefault(c => c.Title == "Colour management"));
            CheckTrue("Settings has a Colour tab", card != null);
            if (card == null) return;
            var combos = OnUi(() => All(card).OfType<ComboBox>().ToList());
            var working = combos.FirstOrDefault(c => OnUi(() => c.Text) == "ProPhoto RGB");
            CheckTrue("The working profile list shows ProPhoto RGB and offers the others", working != null && OnUi(() => working.Items.Cast<object>().Select(o => o.ToString()).ToList()).Contains("sRGB")
                && OnUi(() => working.Items.Cast<object>().Last().ToString()) == "Other file…");
            // the rendering intent comes before the proofing intent; both start with Relative colorimetric
            var intents = combos.Where(c => OnUi(() => c.Items.Count == 4 && c.Items[0].ToString() == "Relative colorimetric" && c.Items[1].ToString() == "Perceptual")).ToList();
            var intent = intents.FirstOrDefault();
            try
            {
                CheckTrue("Both intent lists start with Relative colorimetric, which is chosen", intents.Count == 2 && intents.All(c => OnUi(() => c.Text) == "Relative colorimetric"), intents.Count + " lists");
                if (intent != null)
                {
                    OnUi(() => intent.SelectedIndex = 1);
                    CheckTrue("Changing it is saved and handed to the imaging code", OnUi(() => AppState.Settings.RenderingIntent == "Perceptual" && ColorSettings.Intent == RenderingIntent.Perceptual));
                }
                if (working != null)
                {
                    OnUi(() => working.SelectedIndex = 0); // sRGB
                    CheckTrue("A new working profile applies at once", OnUi(() => AppState.Settings.WorkingProfile == "srgb" && ColorSettings.WorkingProfile == IccProfiles.Srgb));
                }
            }
            finally
            {
                OnUi(() =>
                {
                    if (intent != null) intent.SelectedIndex = 0;
                    if (working != null) working.SelectedIndex = working.Items.Cast<object>().Select(o => o.ToString()).ToList().IndexOf("ProPhoto RGB");
                    AppState.Settings.WorkingProfile = "prophoto"; AppState.Settings.RenderingIntent = "RelativeColorimetric"; AppState.ApplyColorSettings();
                });
            }
        }
    }
}
