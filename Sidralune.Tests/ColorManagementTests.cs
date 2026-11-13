using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sidralune.Imaging;
using Sidralune.Imaging.ColorManagement;
using static Sidralune.Tests.Checks;

namespace Sidralune.Tests
{
    /// <summary>ICC profiles, the built-in working spaces and colour conversions.</summary>
    [TestClass]
    public class ColorManagementTests : TestBase
    {
        private static string Fmt(double[] v) => string.Join(", ", v.Select(x => x.ToString("0.00000")));

        [TestMethod]
        public void TestBuiltInProfiles()
        {
            var names = IccProfiles.BuiltIn.Select(p => p.Description).ToList();
            CheckTrue("The working spaces are built in, Rec. 2020 is not", names.Contains("sRGB") && names.Contains("Display P3") && names.Contains("Adobe RGB (1998) compatible") && names.Contains("ProPhoto RGB")
                && names.Contains("ProPhoto RGB (linear)") && !names.Any(n => n.Contains("2020")), string.Join(" | ", names));
            CheckTrue("Every built-in profile reads back as a simple matrix/TRC profile", IccProfiles.BuiltIn.All(p => p.IsSimple && p.Id != null && new IccProfile(p.Bytes).IsSimple));
            CheckTrue("The same profile always has the same bytes", IccProfiles.Srgb.Fingerprint == new IccProfile(IccProfiles.Srgb.Bytes).Fingerprint);

            // sRGB's D50 matrix as published (ICC sRGB profile)
            var m = IccProfiles.Srgb.Matrix;
            double[,] srgb = { { 0.4361, 0.3851, 0.1431 }, { 0.2225, 0.7169, 0.0606 }, { 0.0139, 0.0971, 0.7141 } };
            double worst = 0;
            for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) worst = Math.Max(worst, Math.Abs(m[i, j] - srgb[i, j]));
            Check("sRGB matrix (D50)", worst, 0, 2e-4);
            CheckTrue("sRGB uses the sRGB curve", IccProfiles.Srgb.Curves.All(c => c.IsSrgb));
            CheckTrue("ProPhoto RGB uses gamma 1.8", IccProfiles.ProPhoto.Curves.All(c => Math.Abs(c.PowerGamma - 1.8) < 0.01));
            CheckTrue("Linear versions are linear", IccProfiles.ById("prophoto-linear").Curves.All(c => c.IsLinear));
            var w = IccProfiles.ProPhoto.LuminanceWeights;
            CheckTrue("ProPhoto luminance weights (almost none on blue)", Math.Abs(w.Sum() - 1) < 1e-9 && w[2] < 0.001 && Math.Abs(w[0] - 0.288) < 0.002, Fmt(w));
            var ws = IccProfiles.Srgb.LuminanceWeights;
            CheckTrue("sRGB luminance weights", Math.Abs(ws[0] - 0.2225) < 0.001 && Math.Abs(ws[1] - 0.7169) < 0.001, Fmt(ws));
            CheckTrue("Grey profiles follow the RGB curve", IccProfiles.GrayFor(IccProfiles.ProPhoto).Id == "gray-18" && IccProfiles.GrayFor(IccProfiles.Srgb).Id == "gray-srgb"
                && IccProfiles.GrayFor(IccProfiles.ById("adobergb")).Id == "gray-22" && IccProfiles.GrayFor(IccProfiles.ById("srgb-linear")).Id == "gray-linear");

            // Windows' own profiles agree with ours
            string dir = IccProfiles.ColorDirectory;
            string winSrgb = Path.Combine(dir, "sRGB Color Space Profile.icm");
            if (File.Exists(winSrgb))
            {
                var p = IccProfile.Load(winSrgb);
                double d = 0;
                for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) d = Math.Max(d, Math.Abs(p.Matrix[i, j] - m[i, j]));
                CheckTrue("Windows' sRGB profile matches the built-in one", d < 1e-3 && p.Curves.All(c => c.IsSrgb), d.ToString("0.00000"));
            }
            CheckTrue("Settings name built-in profiles by id and fall back to ProPhoto", IccProfiles.FromSetting("srgb") == IccProfiles.Srgb && IccProfiles.FromSetting("") == IccProfiles.ProPhoto
                && IccProfiles.FromSetting(Path.Combine(dir, "no such profile.icc")) == IccProfiles.ProPhoto && IccProfiles.ToSetting(IccProfiles.Srgb) == "srgb");

            bool rejected = false;
            try { new IccProfile(new byte[200]); } catch (InvalidDataException) { rejected = true; }
            CheckTrue("Bytes without the ICC signature are refused", rejected && IccProfile.TryCreate(new byte[10]) == null);
        }

        [TestMethod]
        public void TestConversions()
        {
            var srgb = IccProfiles.Srgb; var pro = IccProfiles.ProPhoto;
            var t = ColorTransform.Create(srgb, pro, RenderingIntent.RelativeColorimetric);
            CheckTrue("Between working spaces the conversion is exact (floating point)", t.IsExact);
            // sRGB red in ProPhoto, by hand: XYZ(D50) = (0.4361, 0.2225, 0.0139) → linear ProPhoto (0.5294, 0.0984, 0.0168) → gamma 1.8
            var p = new double[] { 1, 0, 0 };
            t.Pixel(p);
            CheckTrue("sRGB red in ProPhoto RGB", Math.Abs(p[0] - 0.7023) < 2e-3 && Math.Abs(p[1] - 0.2759) < 2e-3 && Math.Abs(p[2] - 0.1036) < 2e-3, Fmt(p));
            var back = ColorTransform.Create(pro, srgb, RenderingIntent.RelativeColorimetric);
            back.Pixel(p);
            CheckTrue("…and back", Math.Abs(p[0] - 1) < 1e-5 && Math.Abs(p[1]) < 1e-5 && Math.Abs(p[2]) < 1e-5, Fmt(p));
            // a ProPhoto green far outside sRGB keeps its values through sRGB (negative and above 1) and comes back
            var g = new double[] { 0.1, 0.9, 0.05 };
            back.Pixel(g);
            CheckTrue("Colours outside sRGB are kept (not clipped)", g.Any(v => v < 0) || g.Any(v => v > 1), Fmt(g));
            t.Pixel(g);
            CheckTrue("…and come back unchanged", Math.Abs(g[0] - 0.1) < 1e-5 && Math.Abs(g[1] - 0.9) < 1e-5 && Math.Abs(g[2] - 0.05) < 1e-5, Fmt(g));
            // neutral greys stay neutral between spaces with different whites (D65 sRGB, D50 ProPhoto)
            var grey = new double[] { 0.5, 0.5, 0.5 };
            t.Pixel(grey);
            CheckTrue("Grey stays grey", Math.Abs(grey[0] - grey[1]) < 1e-4 && Math.Abs(grey[1] - grey[2]) < 1e-4, Fmt(grey));
            // every pair of RGB working spaces converts back and forth
            var rgb = IccProfiles.BuiltInRgb.ToList();
            double worst = 0;
            foreach (var a in rgb)
                foreach (var b in rgb)
                {
                    var v = new double[] { 0.8, 0.35, 0.12 };
                    ColorTransform.Create(a, b, RenderingIntent.Perceptual).Pixel(v);
                    ColorTransform.Create(b, a, RenderingIntent.Perceptual).Pixel(v);
                    worst = Math.Max(worst, Math.Max(Math.Abs(v[0] - 0.8), Math.Max(Math.Abs(v[1] - 0.35), Math.Abs(v[2] - 0.12))));
                }
            Check("Round trips between all working spaces", worst, 0, 1e-5);

            // Windows' conversion agrees with ours for matrix profiles (to 16-bit rounding)
            using (var wcs = new WcsTransform(new[] { srgb, pro }, new[] { RenderingIntent.RelativeColorimetric, RenderingIntent.RelativeColorimetric }, false))
            {
                var samples = new[] { new[] { 1.0, 0, 0 }, new[] { 0, 1.0, 0 }, new[] { 0, 0, 1.0 }, new[] { 0.5, 0.5, 0.5 }, new[] { 0.9, 0.6, 0.2 }, new[] { 0.1, 0.3, 0.7 } };
                var buf = new ushort[samples.Length * 3];
                for (int i = 0; i < samples.Length; i++) { buf[i * 3] = (ushort)(samples[i][2] * 65535); buf[i * 3 + 1] = (ushort)(samples[i][1] * 65535); buf[i * 3 + 2] = (ushort)(samples[i][0] * 65535); }
                var outp = wcs.Translate16(buf, samples.Length, 1, true);
                double diff = 0;
                for (int i = 0; i < samples.Length; i++)
                {
                    var v = (double[])samples[i].Clone();
                    t.Pixel(v);
                    diff = Math.Max(diff, Math.Max(Math.Abs(outp[i * 3 + 2] / 65535.0 - v[0]), Math.Max(Math.Abs(outp[i * 3 + 1] / 65535.0 - v[1]), Math.Abs(outp[i * 3] / 65535.0 - v[2]))));
                }
                CheckTrue("Windows and Sidralune agree (blue, green, red order)", diff < 1.0 / 255, (diff * 255).ToString("0.00") + "/255");
            }

            // whole images; grey images convert their curve only
            var img = new AstroImage(2, 1, 3) { Profile = srgb };
            img.Data[0][0] = 1; img.Data[1][1] = 1;
            ColorTransform.Create(srgb, pro, RenderingIntent.Perceptual).Apply(img);
            CheckTrue("An image converts pixel by pixel", Math.Abs(img.Data[0][0] - 0.7023) < 2e-3 && img.Data[1][1] > 0.8 && img.Data[1][1] < 0.95);
            var mono = new AstroImage(1, 1, 1);
            mono.Data[0][0] = 0.5f;
            ColorTransform.Create(IccProfiles.ById("gray-linear"), IccProfiles.ById("gray-srgb"), RenderingIntent.Perceptual).Apply(mono);
            Check("A linear grey 0.5 in the sRGB curve", mono.Data[0][0], ToneCurve.LinearToSrgb(0.5), 1e-6);
            bool refused = false;
            try { ColorTransform.Create(srgb, IccProfiles.ById("gray-22"), RenderingIntent.Perceptual); } catch (ArgumentException) { refused = true; }
            CheckTrue("RGB and grey profiles do not mix", refused);
        }

        private static AstroImage Sample(int channels, IccProfile profile)
        {
            var img = new AstroImage(8, 6, channels) { Profile = profile };
            for (int c = 0; c < channels; c++)
                for (int i = 0; i < img.PixelCount; i++) img.Data[c][i] = (float)((i * (c + 3) % 47) / 46.0);
            if (channels == 3) { img.Data[0][0] = 1; img.Data[1][0] = 0; img.Data[2][0] = 0; } // a pure red first pixel
            return img;
        }

        private static string Dir()
        {
            string d = Path.Combine(Path.GetTempPath(), "SidraluneTests", "colour");
            Directory.CreateDirectory(d);
            return d;
        }

        [TestMethod]
        public void TestProfilesInFiles()
        {
            var adobe = IccProfiles.ById("adobergb");
            string dir = Dir();
            var formats = new[]
            {
                Tuple.Create(Sidralune.Imaging.IO.ImageSaveFormat.Fits32Float, ".fits", 1e-6), Tuple.Create(Sidralune.Imaging.IO.ImageSaveFormat.Fits16, ".fits", 1e-4),
                Tuple.Create(Sidralune.Imaging.IO.ImageSaveFormat.Xisf32Float, ".xisf", 1e-6), Tuple.Create(Sidralune.Imaging.IO.ImageSaveFormat.Xisf16, ".xisf", 1e-4),
                Tuple.Create(Sidralune.Imaging.IO.ImageSaveFormat.Tiff16, ".tif", 1e-4), Tuple.Create(Sidralune.Imaging.IO.ImageSaveFormat.Tiff32Float, ".tif", 1e-6),
                Tuple.Create(Sidralune.Imaging.IO.ImageSaveFormat.Png8, ".png", 0.003), Tuple.Create(Sidralune.Imaging.IO.ImageSaveFormat.Jpeg8, ".jpg", 0.2),
            };
            bool embed = ColorSettings.EmbedProfiles, srgb8 = ColorSettings.Export8BitAsSrgb;
            try
            {
                ColorSettings.EmbedProfiles = true; ColorSettings.Export8BitAsSrgb = false;
                foreach (var f in formats)
                {
                    string path = Path.Combine(dir, "rgb_" + f.Item1 + f.Item2);
                    var img = Sample(3, adobe);
                    Sidralune.Imaging.IO.ImageFile.Save(path, img, f.Item1);
                    var back = Sidralune.Imaging.IO.ImageFile.Load(path);
                    // largest difference for lossless formats, average for JPEG (compression changes a busy 8×6 image a lot)
                    double diff = 0, sum = 0;
                    for (int c = 0; c < 3; c++) for (int i = 0; i < img.PixelCount; i++) { double d = Math.Abs(back.Data[c][i] - img.Data[c][i]); diff = Math.Max(diff, d); sum += d; }
                    if (f.Item2 == ".jpg") diff = sum / (3 * img.PixelCount);
                    CheckTrue(f.Item1 + ": the profile is embedded and read back, pixels unchanged", back.Profile != null && back.Profile.SameAs(adobe) && diff <= f.Item3,
                        (back.Profile?.Description ?? "no profile") + ", " + diff.ToString("0.00000"));
                    // grey images keep a grey profile (8-bit files carry the RGB profile with the same curve)
                    string gpath = Path.Combine(dir, "grey_" + f.Item1 + f.Item2);
                    Sidralune.Imaging.IO.ImageFile.Save(gpath, Sample(1, IccProfiles.ById("gray-22")), f.Item1);
                    var g = Sidralune.Imaging.IO.ImageFile.Load(gpath);
                    bool bit8 = f.Item2 == ".png" || f.Item2 == ".jpg";
                    CheckTrue(f.Item1 + ": a grey image keeps its curve", g.Profile != null && (bit8 ? g.Profile.SameAs(adobe) : g.Profile.Id == null && g.Profile.SameAs(IccProfiles.ById("gray-22"))),
                        g.Profile?.Description ?? "no profile");
                }
                // FITS stores the profile as PixInsight and Siril do
                var text = System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(Path.Combine(dir, "rgb_Fits32Float.fits")));
                int ext = text.IndexOf("XTENSION= 'IMAGE'", StringComparison.Ordinal);
                var extHeader = ext >= 0 ? text.Substring(ext, Math.Min(2880, text.Length - ext)) : "";
                Func<string, string, bool> card = (t, re) => System.Text.RegularExpressions.Regex.IsMatch(t, re);
                CheckTrue("FITS: an 8-bit IMAGE extension named ICCProfile after the image", ext > 0 && ext % 2880 == 0 && card(text.Substring(0, ext), @"EXTEND  =\s+T")
                    && card(extHeader, @"BITPIX  =\s+8\s") && card(extHeader, @"NAXIS   =\s+1\s") && card(extHeader, @"NAXIS1  =\s+" + adobe.Bytes.Length + @"\s") && card(extHeader, @"EXTNAME = 'ICCProfile'"),
                    ext + " " + extHeader.Substring(0, Math.Min(400, extHeader.Length)).Replace("  ", ""));

                // untagged images stay untagged in FITS, XISF and TIFF; 8-bit files carry the profile their colours are in
                var untagged = Sample(3, null);
                Sidralune.Imaging.IO.ImageFile.Save(Path.Combine(dir, "untagged.fits"), untagged, Sidralune.Imaging.IO.ImageSaveFormat.Fits32Float);
                Sidralune.Imaging.IO.ImageFile.Save(Path.Combine(dir, "untagged.png"), untagged, Sidralune.Imaging.IO.ImageSaveFormat.Png8);
                CheckTrue("An untagged image stays untagged in FITS", Sidralune.Imaging.IO.ImageFile.Load(Path.Combine(dir, "untagged.fits")).Profile == null);
                CheckTrue("…and an 8-bit PNG says it is in the working profile", Sidralune.Imaging.IO.ImageFile.Load(Path.Combine(dir, "untagged.png")).Profile?.SameAs(IccProfiles.ProPhoto) == true);

                // 8-bit exports converted to sRGB
                ColorSettings.Export8BitAsSrgb = true;
                Sidralune.Imaging.IO.ImageFile.Save(Path.Combine(dir, "to_srgb.png"), Sample(3, IccProfiles.ProPhoto), Sidralune.Imaging.IO.ImageSaveFormat.Png8);
                var s = Sidralune.Imaging.IO.ImageFile.Load(Path.Combine(dir, "to_srgb.png"));
                // ProPhoto pure red is far outside sRGB: red stays at the top, green and blue clip to 0
                CheckTrue("8-bit export converted to sRGB and tagged sRGB", s.Profile != null && s.Profile.SameAs(IccProfiles.Srgb) && s.Data[0][0] > 0.99 && s.Data[1][0] < 0.01,
                    s.Profile?.Description + " " + s.Data[0][0] + " " + s.Data[1][0]);
                ColorSettings.Export8BitAsSrgb = false;

                // embedding switched off
                ColorSettings.EmbedProfiles = false;
                Sidralune.Imaging.IO.ImageFile.Save(Path.Combine(dir, "plain.fits"), Sample(3, adobe), Sidralune.Imaging.IO.ImageSaveFormat.Fits32Float);
                Sidralune.Imaging.IO.ImageFile.Save(Path.Combine(dir, "plain.png"), Sample(3, adobe), Sidralune.Imaging.IO.ImageSaveFormat.Png8);
                var plainPng = File.ReadAllBytes(Path.Combine(dir, "plain.png"));
                CheckTrue("Without embedding, no profile and no sRGB claim is written", Sidralune.Imaging.IO.ImageFile.Load(Path.Combine(dir, "plain.fits")).Profile == null
                    && Sidralune.Imaging.IO.ImageFile.Load(Path.Combine(dir, "plain.png")).Profile == null && !System.Text.Encoding.ASCII.GetString(plainPng).Contains("sRGB"));
            }
            finally { ColorSettings.EmbedProfiles = embed; ColorSettings.Export8BitAsSrgb = srgb8; }

            // a large profile spreads over several JPEG segments
            var big = IccProfiles.BuildRgb("Big test profile", new[] { 0.64, 0.33 }, new[] { 0.30, 0.60 }, new[] { 0.15, 0.06 }, new[] { 0.3127, 0.3290 }, 0);
            Array.Resize(ref big, big.Length + 150000);
            big[0] = (byte)(big.Length >> 24); big[1] = (byte)(big.Length >> 16); big[2] = (byte)(big.Length >> 8); big[3] = (byte)big.Length;
            var bigProfile = new IccProfile(big);
            using (var bmp = new System.Drawing.Bitmap(4, 4))
            using (var ms = new MemoryStream())
            {
                bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Jpeg);
                var jpeg = Sidralune.Imaging.IO.EmbeddedProfiles.Write(ms.ToArray(), bigProfile);
                var read = Sidralune.Imaging.IO.EmbeddedProfiles.Read(jpeg);
                CheckTrue("A large profile in three JPEG segments comes back whole", read != null && read.Bytes.SequenceEqual(big));
                using (var check = new System.Drawing.Bitmap(new MemoryStream(jpeg))) CheckTrue("…and the JPEG still opens", check.Width == 4);
            }

            // an XISF written by other software, with the profile as an attachment
            string xisf = Path.Combine(dir, "attachment.xisf");
            var icc = adobe.Bytes;
            var pixels = new byte[12];
            Buffer.BlockCopy(new[] { 0.25f, 0.5f, 0.75f }, 0, pixels, 0, 12);
            Func<int, int, string> header = (pixPos, iccPos) => "<?xml version=\"1.0\" encoding=\"UTF-8\"?><xisf version=\"1.0\" xmlns=\"http://www.pixinsight.com/xisf\">"
                + "<Image geometry=\"1:1:3\" sampleFormat=\"Float32\" bounds=\"0:1\" colorSpace=\"RGB\" location=\"attachment:" + pixPos + ":12\">"
                + "<ICCProfile location=\"attachment:" + iccPos + ":" + icc.Length + "\"/></Image></xisf>";
            int hl = System.Text.Encoding.UTF8.GetByteCount(header(100000, 100000));
            int pix = 16 + hl, iccAt = pix + 12;
            var hb = System.Text.Encoding.UTF8.GetBytes(header(pix, iccAt).PadRight(hl));
            using (var fs = File.Create(xisf))
            {
                fs.Write(System.Text.Encoding.ASCII.GetBytes("XISF0100"), 0, 8);
                fs.Write(BitConverter.GetBytes(hb.Length), 0, 4); fs.Write(new byte[4], 0, 4);
                fs.Write(hb, 0, hb.Length); fs.Write(pixels, 0, 12); fs.Write(icc, 0, icc.Length);
            }
            var x = Sidralune.Imaging.IO.ImageFile.Load(xisf);
            CheckTrue("An XISF profile stored as an attachment is read", x.Profile != null && x.Profile.SameAs(adobe) && Math.Abs(x.Data[1][0] - 0.5) < 1e-6);
        }

        [TestMethod]
        public void TestOpeningPolicies()
        {
            var keepMis = ColorSettings.OnMismatch; var keepMiss = ColorSettings.OnMissing;
            try
            {
                var adobe = IccProfiles.ById("adobergb");
                ColorSettings.OnMismatch = ProfileMismatchPolicy.Keep;
                var img = Sample(3, adobe);
                CheckTrue("Keep: the embedded profile stays", ProfilePolicy.OnOpen(img) == ProfileChoice.Keep && img.Profile == adobe && img.Data[0][0] == 1);
                ColorSettings.OnMismatch = ProfileMismatchPolicy.ConvertToWorking;
                img = Sample(3, adobe);
                ProfilePolicy.OnOpen(img);
                CheckTrue("Convert: the pixels are converted to the working profile", img.Profile == IccProfiles.ProPhoto && img.Data[0][0] < 0.95 && img.Data[1][0] > 0.05);
                ColorSettings.OnMismatch = ProfileMismatchPolicy.Discard;
                img = Sample(3, adobe);
                ProfilePolicy.OnOpen(img);
                CheckTrue("Discard: the profile goes, the pixels stay", img.Profile == null && img.Data[0][0] == 1);
                ColorSettings.OnMismatch = ProfileMismatchPolicy.Ask;
                bool? asked = null;
                img = Sample(3, adobe);
                ProfilePolicy.OnOpen(img, (i, different) => { asked = different; return ProfileChoice.Discard; });
                CheckTrue("Ask: the question says the profile differs, and the answer is carried out", asked == true && img.Profile == null);
                img = Sample(3, IccProfiles.ProPhoto);
                CheckTrue("An image already in the working profile needs nothing", ProfilePolicy.OnOpen(img, (i, d) => { throw new InvalidOperationException(); }) == null);

                ColorSettings.OnMissing = ProfileMissingPolicy.LeaveUntagged;
                img = Sample(3, null);
                CheckTrue("Leave untagged (the default)", ProfilePolicy.OnOpen(img) == ProfileChoice.LeaveUntagged && img.Profile == null);
                ColorSettings.OnMissing = ProfileMissingPolicy.AssignWorking;
                img = Sample(1, null);
                ProfilePolicy.OnOpen(img);
                CheckTrue("Assign: a grey image gets the working grey", img.Profile != null && img.Profile.Id == "gray-18");
                ColorSettings.OnMissing = ProfileMissingPolicy.Ask;
                asked = null;
                img = Sample(3, null);
                ProfilePolicy.OnOpen(img, (i, different) => { asked = different; return null; });
                CheckTrue("Ask about a missing profile; no answer leaves it untagged", asked == false && img.Profile == null);
            }
            finally { ColorSettings.OnMismatch = keepMis; ColorSettings.OnMissing = keepMiss; }
        }

        [TestMethod]
        public void TestImageProfiles()
        {
            var keep = ColorSettings.WorkingProfile;
            try
            {
                var img = new AstroImage(4, 4, 3);
                CheckTrue("A new image is untagged and taken to be in the working profile (ProPhoto RGB by default)", img.Profile == null && img.EffectiveProfile == IccProfiles.ProPhoto);
                CheckTrue("A one-channel image is taken to be in the matching grey", new AstroImage(2, 2, 1).EffectiveProfile.Id == "gray-18");
                ColorSettings.WorkingProfile = IccProfiles.Srgb;
                CheckTrue("…which follows the working profile", img.EffectiveProfile == IccProfiles.Srgb && new AstroImage(2, 2, 1).EffectiveProfile.Id == "gray-srgb");
                img.Profile = IccProfiles.ById("adobergb");
                CheckTrue("Copies keep the profile", img.Clone().Profile == img.Profile && img.CloneEmpty().Profile == img.Profile);
                CheckTrue("…but not when the channels change", img.CloneEmpty(1).Profile == null && img.ExtractChannel(0).Profile == null && img.ToMono().Profile == null);
            }
            finally { ColorSettings.WorkingProfile = keep; }
        }
    }
}
