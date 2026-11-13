using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Sidralune.Imaging;
using Sidralune.Imaging.ColorManagement;
using static Sidralune.Core.Util.Lang;

namespace Sidralune.UI
{
    public enum ProfileKind { Rgb, Gray, Proofing }

    /// <summary>The colour management pieces of the interface: profile lists, the question on opening, names.</summary>
    public static class ColorUi
    {
        /// <summary>A profile's name as shown: built-in ones in the interface language, others as they call themselves.</summary>
        public static string Name(IccProfile p)
        {
            if (p == null) return "";
            switch (p.Id)
            {
                case "srgb": return "sRGB";
                case "srgb-linear": return "sRGB " + T("(linear)");
                case "displayp3": return "Display P3";
                case "displayp3-linear": return "Display P3 " + T("(linear)");
                case "adobergb": return T("{0} compatible", "Adobe RGB (1998)");
                case "adobergb-linear": return T("{0} compatible", "Adobe RGB (1998)") + " " + T("(linear)");
                case "prophoto": return "ProPhoto RGB";
                case "prophoto-linear": return "ProPhoto RGB " + T("(linear)");
                case "gray-srgb": return T("Grey, sRGB curve");
                case "gray-22": return T("Grey, gamma {0}", "2.2");
                case "gray-18": return T("Grey, gamma {0}", "1.8");
                case "gray-linear": return T("Grey, linear");
            }
            return p.Description;
        }

        /// <summary>An image's profile for the readout: its own, or "Untagged (working profile)".</summary>
        public static string ImageProfileText(AstroImage img) =>
            img.Profile != null ? Name(img.Profile) : T("Untagged ({0})", Name(img.EffectiveProfile));

        /// <summary>Rendering intents in the order they are offered (the enum keeps Windows' own numbers).</summary>
        public static readonly RenderingIntent[] Intents = { RenderingIntent.RelativeColorimetric, RenderingIntent.Perceptual, RenderingIntent.Saturation, RenderingIntent.AbsoluteColorimetric };

        public static string IntentName(RenderingIntent i)
        {
            switch (i)
            {
                case RenderingIntent.Perceptual: return T("Perceptual");
                case RenderingIntent.Saturation: return T("Saturation");
                case RenderingIntent.AbsoluteColorimetric: return T("Absolute colorimetric");
                default: return T("Relative colorimetric");
            }
        }

        /// <summary>A list of rendering intents, Relative colorimetric first; changed (optional) receives the new choice.</summary>
        public static ComboBox IntentCombo(RenderingIntent value, Action<RenderingIntent> changed = null)
        {
            var cb = Ui.Combo(Intents.Select(i => (object)IntentName(i)).ToArray());
            cb.SelectedIndex = Math.Max(0, Array.IndexOf(Intents, value));
            if (changed != null) cb.SelectedIndexChanged += (s, e) => changed(Intents[Math.Max(0, cb.SelectedIndex)]);
            return cb;
        }

        /// <summary>The intent a list made by IntentCombo shows.</summary>
        public static RenderingIntent IntentOf(ComboBox cb) => Intents[Math.Max(0, cb.SelectedIndex)];

        private sealed class Item
        {
            public string Text, Setting;
            public override string ToString() => Text;
        }

        /// <summary>
        /// A list of profiles: the built-in ones, those installed in Windows and "Other file…". The setting is a built-in
        /// id or a file path; changed receives the new one.
        /// </summary>
        public static ComboBox ProfileCombo(string current, ProfileKind kind, Action<string> changed)
        {
            var cb = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
            var items = new List<Item>();
            var builtIn = kind == ProfileKind.Gray ? IccProfiles.BuiltInGray : IccProfiles.BuiltInRgb;
            items.AddRange(builtIn.Select(p => new Item { Text = Name(p), Setting = p.Id }));
            var installed = kind == ProfileKind.Proofing ? IccProfiles.InstalledForProofing() : IccProfiles.Installed(kind == ProfileKind.Gray);
            items.AddRange(installed.Select(k => new Item { Text = k.Key, Setting = k.Value }));
            if (!string.IsNullOrEmpty(current) && !items.Any(i => string.Equals(i.Setting, current, StringComparison.OrdinalIgnoreCase)) && File.Exists(current))
            {
                string desc;
                try { desc = IccProfile.Load(current).Description; } catch { desc = Path.GetFileName(current); }
                items.Add(new Item { Text = desc, Setting = current });
            }
            var other = new Item { Text = T("Other file…"), Setting = null };
            foreach (var i in items) cb.Items.Add(i);
            cb.Items.Add(other);
            int at = items.FindIndex(i => string.Equals(i.Setting, current, StringComparison.OrdinalIgnoreCase));
            cb.SelectedIndex = Math.Max(0, at);
            int last = cb.SelectedIndex;
            cb.SelectedIndexChanged += (s, e) =>
            {
                var it = cb.SelectedItem as Item;
                if (it == null || cb.SelectedIndex == last) return;
                if (it == other)
                {
                    string path = PickFile(cb.FindForm(), kind);
                    if (path == null) { cb.SelectedIndex = last; return; }
                    var existing = cb.Items.Cast<Item>().FirstOrDefault(x => string.Equals(x.Setting, path, StringComparison.OrdinalIgnoreCase));
                    if (existing == null)
                    {
                        existing = new Item { Text = IccProfile.Load(path).Description, Setting = path };
                        cb.Items.Insert(cb.Items.Count - 1, existing);
                    }
                    last = cb.Items.IndexOf(existing);
                    cb.SelectedIndex = last;
                    changed(path);
                    return;
                }
                last = cb.SelectedIndex;
                changed(it.Setting);
            };
            return cb;
        }

        private static string PickFile(IWin32Window owner, ProfileKind kind)
        {
            using (var d = new OpenFileDialog { Filter = T("ICC profiles") + "|*.icc;*.icm|" + T("All files") + "|*.*", InitialDirectory = IccProfiles.ColorDirectory })
            {
                if (d.ShowDialog(owner) != DialogResult.OK) return null;
                try
                {
                    var p = IccProfile.Load(d.FileName);
                    bool fits = kind == ProfileKind.Gray ? p.IsGray : kind == ProfileKind.Rgb ? p.IsRgb : p.IsRgb || p.IsGray || p.ColorSpace == "CMYK";
                    if (fits) return d.FileName;
                    MainForm.Instance?.Toast(T("{0} is not an RGB profile", Path.GetFileName(d.FileName)), Icons.Warning, Theme.Current.Warning);
                }
                catch (Exception ex) { MainForm.Instance?.Toast(T("Could not read {0}: {1}", Path.GetFileName(d.FileName), ex.Message), Icons.Warning, Theme.Current.Warning); }
                return null;
            }
        }

        /// <summary>A profile from a list's setting (built-in id or file path), or null.</summary>
        public static IccProfile FromSetting(string setting)
        {
            if (string.IsNullOrEmpty(setting)) return null;
            return IccProfiles.ById(setting) ?? IccProfile.TryCreate(File.Exists(setting) ? File.ReadAllBytes(setting) : null);
        }

        /// <summary>
        /// Asks for a profile to assign to an image or convert it to (and, to convert, the rendering intent). Returns
        /// false when cancelled.
        /// </summary>
        public static bool ChooseProfile(IWin32Window owner, AstroImage img, bool convert, out IccProfile profile, out RenderingIntent intent)
        {
            profile = null; intent = ColorSettings.Intent;
            bool gray = !img.IsColor;
            var current = img.EffectiveProfile;
            string setting = current.Id ?? IccProfiles.ToSetting(gray ? ColorSettings.WorkingGrayProfile : ColorSettings.WorkingProfile);
            using (var f = new Form { Text = convert ? T("Convert to profile") : T("Assign profile"), Width = 520, Height = convert ? 250 : 210, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent, MaximizeBox = false, MinimizeBox = false, ShowInTaskbar = false })
            {
                var g = new FormGrid(150) { Dock = DockStyle.Fill, Padding = new Padding(10) };
                g.Row(T("Image now"), new Label { Text = ImageProfileText(img), AutoSize = true, Margin = new Padding(0, 6, 0, 6) });
                string chosen = setting;
                g.Row(T("Profile"), ProfileCombo(setting, gray ? ProfileKind.Gray : ProfileKind.Rgb, v => chosen = v)).Width = 320;
                ComboBox intents = null;
                if (convert)
                {
                    intents = g.Row(T("Rendering intent"), IntentCombo(ColorSettings.Intent));
                }
                g.Full(Ui.Hint(convert ? T("Converting changes the pixel values so the colours look the same in the new profile.") : T("Assigning changes how the pixel values are understood, not the values themselves, so the colours change.")));
                var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8, 4, 8, 4) };
                var cancel = new FlatButton(T("Cancel"), null) { DialogResult = DialogResult.Cancel };
                var ok = new FlatButton(convert ? T("Convert") : T("Assign"), null) { Kind = ButtonKind.Primary, DialogResult = DialogResult.OK };
                cancel.AutoSizeToText(); ok.AutoSizeToText();
                ok.Click += (s, e) => { f.DialogResult = DialogResult.OK; f.Close(); };
                cancel.Click += (s, e) => { f.DialogResult = DialogResult.Cancel; f.Close(); };
                buttons.Controls.Add(cancel); buttons.Controls.Add(ok);
                f.Controls.Add(g); f.Controls.Add(buttons);
                f.AcceptButton = ok; f.CancelButton = cancel;
                Theme.Apply(f);
                f.HandleCreated += (s, e) => Theme.DarkTitleBar(f);
                if (f.ShowDialog(owner) != DialogResult.OK) return false;
                profile = FromSetting(chosen);
                if (intents != null) intent = IntentOf(intents);
                return profile != null;
            }
        }

        /// <summary>
        /// Applies Settings → Colour to an image the user has just opened, asking when the setting says so. Call on the
        /// UI thread, before showing the image.
        /// </summary>
        public static void Opened(AstroImage img)
        {
            try { ProfilePolicy.OnOpen(img, Ask); }
            catch (Exception ex) { MainForm.Instance?.Toast(T("Could not convert the colours: {0}", ex.Message), Icons.Warning, Theme.Current.Warning); }
        }

        private static ProfileChoice? Ask(AstroImage img, bool different)
        {
            var owner = MainForm.Instance;
            string working = Name(ProfilePolicy.WorkingFor(img));
            string text = different
                ? T("This image has the colour profile “{0}”, which differs from the working profile “{1}”.", Name(img.Profile), working)
                : T("This image has no colour profile. Without one it is taken to be in the working profile “{0}”.", working);
            var choices = different
                ? new[] { Tuple.Create(T("Keep its profile"), ProfileChoice.Keep), Tuple.Create(T("Convert to the working profile"), ProfileChoice.ConvertToWorking), Tuple.Create(T("Discard the profile"), ProfileChoice.Discard) }
                : new[] { Tuple.Create(T("Leave it untagged"), ProfileChoice.LeaveUntagged), Tuple.Create(T("Assign the working profile"), ProfileChoice.AssignWorking) };
            using (var f = new Form { Text = T("Colour profile"), Width = 520, Height = 210, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent, MaximizeBox = false, MinimizeBox = false, ShowInTaskbar = false })
            {
                var label = new Label { Text = text, Left = 14, Top = 12, Width = 480, Height = 54 };
                var always = new CheckBox { Text = T("Do this from now on (Settings → Colour)"), Left = 14, Top = 70, AutoSize = true };
                var row = new FlowLayoutPanel { Left = 10, Top = 104, Width = 490, Height = 44, FlowDirection = FlowDirection.LeftToRight };
                ProfileChoice? picked = null;
                foreach (var c in choices)
                {
                    var choice = c.Item2;
                    var b = new FlatButton(c.Item1, null) { Kind = choice == choices[0].Item2 ? ButtonKind.Primary : ButtonKind.Normal };
                    b.AutoSizeToText();
                    b.Click += (s, e) => { picked = choice; f.DialogResult = DialogResult.OK; f.Close(); };
                    row.Controls.Add(b);
                }
                f.Controls.AddRange(new Control[] { label, always, row });
                Theme.Apply(f);
                f.HandleCreated += (s, e) => Theme.DarkTitleBar(f);
                f.ShowDialog(owner);
                if (picked != null && always.Checked)
                {
                    var st = AppState.Settings;
                    if (different) st.OnProfileMismatch = (picked == ProfileChoice.ConvertToWorking ? ProfileMismatchPolicy.ConvertToWorking : picked == ProfileChoice.Discard ? ProfileMismatchPolicy.Discard : ProfileMismatchPolicy.Keep).ToString();
                    else st.OnMissingProfile = (picked == ProfileChoice.AssignWorking ? ProfileMissingPolicy.AssignWorking : ProfileMissingPolicy.LeaveUntagged).ToString();
                    AppState.Save();
                    AppState.ApplyColorSettings();
                }
                return picked;
            }
        }
    }
}
