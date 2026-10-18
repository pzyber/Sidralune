using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Sidralune.UI
{
    /// <summary>
    /// Colour palette. Dark theme follows current dark-UI guidance: no pure black (reduces halation), elevation expressed
    /// by progressively lighter surfaces, desaturated accents, ≥ 4.5:1 contrast for body text (WCAG 2.2 AA) and a visible
    /// focus ring. Night theme maps everything to deep reds to preserve scotopic dark adaptation at the telescope.
    /// </summary>
    public class Palette
    {
        public string Name;
        public Color Background, Surface, SurfaceRaised, SurfaceHover, SurfacePressed, Border, BorderStrong;
        public Color Text, TextDim, TextFaint, Accent, AccentText, AccentSoft, Success, Warning, Danger, Info, Focus;
        public Color ChartBackground, ChartGrid, ChartText;
        public Color SkyDay, SkyNight, Ground, Horizon;
        public Color ConstellationLine, ConstellationBoundary, ConstellationName, GridEquatorial, GridAltAz, Ecliptic, GalacticEquator, MilkyWay;
        public Color Planet, Moon, Sun, Galaxy, Nebula, Cluster, PlanetaryNebula, Comet, Asteroid, Selection, FovOverlay;
        public Color[] Series;
        public bool IsNight;

        public static Palette Dark() => new Palette
        {
            Name = "Dark",
            Background = C(0x15, 0x17, 0x1C), Surface = C(0x1C, 0x1F, 0x26), SurfaceRaised = C(0x24, 0x28, 0x31),
            SurfaceHover = C(0x2C, 0x31, 0x3C), SurfacePressed = C(0x34, 0x3A, 0x47),
            Border = C(0x2E, 0x33, 0x3D), BorderStrong = C(0x46, 0x4D, 0x5B),
            Text = C(0xE6, 0xE8, 0xEC), TextDim = C(0xA3, 0xAB, 0xB8), TextFaint = C(0x6E, 0x77, 0x85),
            Accent = C(0x6C, 0xA6, 0xFF), AccentText = C(0x0E, 0x1A, 0x2E), AccentSoft = C(0x23, 0x35, 0x52),
            Success = C(0x4C, 0xC3, 0x8A), Warning = C(0xE3, 0xB3, 0x41), Danger = C(0xF2, 0x6D, 0x6D), Info = C(0x5F, 0xC8, 0xE0), Focus = C(0x8F, 0xBC, 0xFF),
            ChartBackground = C(0x0B, 0x0E, 0x16), ChartGrid = C(0x2A, 0x35, 0x4A), ChartText = C(0xB8, 0xC2, 0xD3),
            SkyDay = C(0x3E, 0x6E, 0xA8), SkyNight = C(0x05, 0x07, 0x10), Ground = C(0x16, 0x1A, 0x14), Horizon = C(0x5A, 0x6B, 0x55),
            ConstellationLine = C(0x3D, 0x6A, 0x9E), ConstellationBoundary = C(0x4A, 0x3A, 0x5E), ConstellationName = C(0x6F, 0x8F, 0xBF),
            GridEquatorial = C(0x23, 0x3C, 0x5C), GridAltAz = C(0x2D, 0x4F, 0x3A), Ecliptic = C(0x8A, 0x6D, 0x2A), GalacticEquator = C(0x6B, 0x3D, 0x6E), MilkyWay = C(0x1E, 0x24, 0x36),
            Planet = C(0xFF, 0xC8, 0x6B), Moon = C(0xEE, 0xEE, 0xDD), Sun = C(0xFF, 0xE0, 0x80), Galaxy = C(0xE0, 0x7A, 0x9A), Nebula = C(0x5F, 0xD0, 0xB0),
            Cluster = C(0xE8, 0xD0, 0x60), PlanetaryNebula = C(0x80, 0xC8, 0xFF), Comet = C(0x7F, 0xFF, 0xD4), Asteroid = C(0xC8, 0xA0, 0xFF),
            Selection = C(0xFF, 0x6B, 0x6B), FovOverlay = C(0xFF, 0x9F, 0x43),
            Series = new[] { C(0x6C, 0xA6, 0xFF), C(0xF2, 0x8B, 0x5C), C(0x4C, 0xC3, 0x8A), C(0xE3, 0xB3, 0x41), C(0xC0, 0x8B, 0xF2), C(0x5F, 0xC8, 0xE0), C(0xF2, 0x6D, 0x9C) }
        };

        public static Palette Night()
        {
            Func<int, Color> r = v => C(v, (int)(v * 0.06), (int)(v * 0.04));
            return new Palette
            {
                Name = "Night (red)", IsNight = true,
                Background = C(0x0A, 0x00, 0x00), Surface = C(0x12, 0x01, 0x01), SurfaceRaised = C(0x1A, 0x02, 0x02),
                SurfaceHover = C(0x26, 0x03, 0x03), SurfacePressed = C(0x33, 0x04, 0x04),
                Border = C(0x2A, 0x04, 0x04), BorderStrong = C(0x44, 0x06, 0x06),
                Text = r(0xD8), TextDim = r(0xA0), TextFaint = r(0x68), Accent = r(0xFF), AccentText = C(0x10, 0, 0), AccentSoft = r(0x40),
                Success = r(0xC0), Warning = r(0xE0), Danger = r(0xFF), Info = r(0xB0), Focus = r(0xFF),
                ChartBackground = C(0x05, 0, 0), ChartGrid = r(0x30), ChartText = r(0xA0),
                SkyDay = r(0x30), SkyNight = C(0x03, 0, 0), Ground = C(0x10, 0, 0), Horizon = r(0x60),
                ConstellationLine = r(0x60), ConstellationBoundary = r(0x38), ConstellationName = r(0x80), GridEquatorial = r(0x30), GridAltAz = r(0x2C), Ecliptic = r(0x50), GalacticEquator = r(0x40), MilkyWay = r(0x14),
                Planet = r(0xFF), Moon = r(0xE0), Sun = r(0xFF), Galaxy = r(0xC0), Nebula = r(0xB0), Cluster = r(0xD0), PlanetaryNebula = r(0xC8), Comet = r(0xD0), Asteroid = r(0xB8),
                Selection = r(0xFF), FovOverlay = r(0xE8),
                Series = new[] { r(0xFF), r(0xB0), r(0xD8), r(0x88), r(0xE8), r(0xA0), r(0xC0) }
            };
        }

        /// <summary>Print palette: black on white for paper charts.</summary>
        public static Palette Print()
        {
            var p = Dark();
            p.Name = "Print";
            p.ChartBackground = Color.White; p.ChartGrid = C(0xC8, 0xC8, 0xC8); p.ChartText = Color.Black;
            p.SkyNight = Color.White; p.SkyDay = Color.White; p.Ground = C(0xF0, 0xF0, 0xF0); p.Horizon = C(0x60, 0x60, 0x60);
            p.ConstellationLine = C(0x70, 0x70, 0x70); p.ConstellationBoundary = C(0xB0, 0xB0, 0xB0); p.ConstellationName = C(0x40, 0x40, 0x40);
            p.GridEquatorial = C(0xD0, 0xD0, 0xD0); p.GridAltAz = C(0xC0, 0xE0, 0xC0); p.Ecliptic = C(0xC0, 0xA0, 0x40); p.GalacticEquator = C(0xC0, 0x90, 0xC0);
            p.Planet = Color.Black; p.Moon = Color.Black; p.Sun = Color.Black; p.Galaxy = C(0xA0, 0x10, 0x30); p.Nebula = C(0x10, 0x70, 0x50);
            p.Cluster = C(0x80, 0x60, 0x00); p.PlanetaryNebula = C(0x10, 0x40, 0xA0); p.Comet = C(0x00, 0x60, 0x60); p.Asteroid = C(0x50, 0x20, 0x90);
            p.Selection = Color.Red; p.FovOverlay = C(0xD0, 0x40, 0x00); p.MilkyWay = C(0xE8, 0xE8, 0xF0);
            return p;
        }

        private static Color C(int r, int g, int b) => Color.FromArgb(Math.Min(255, r), Math.Min(255, g), Math.Min(255, b));

        /// <summary>Star colour from B-V (Ballesteros temperature to blackbody RGB, desaturated for display).</summary>
        public Color StarColor(double bv, int alpha = 255)
        {
            if (IsNight) return Color.FromArgb(alpha, 255, 40, 30);
            if (double.IsNaN(bv)) bv = 0.6;
            double t = 4600 * (1 / (0.92 * bv + 1.7) + 1 / (0.92 * bv + 0.62));
            double r, g, b;
            t /= 100;
            if (t <= 66) { r = 255; g = 99.47 * Math.Log(t) - 161.12; b = t <= 19 ? 0 : 138.52 * Math.Log(t - 10) - 305.04; }
            else { r = 329.7 * Math.Pow(t - 60, -0.1332); g = 288.12 * Math.Pow(t - 60, -0.0755); b = 255; }
            // desaturate towards white by 55% so colours read as tints
            r = 255 - (255 - Clamp(r)) * 0.45; g = 255 - (255 - Clamp(g)) * 0.45; b = 255 - (255 - Clamp(b)) * 0.45;
            return Color.FromArgb(alpha, (int)r, (int)g, (int)b);
        }

        private static double Clamp(double v) => v < 0 ? 0 : v > 255 ? 255 : v;
    }

    /// <summary>Global theme state and helpers to apply it to standard WinForms controls.</summary>
    public static class Theme
    {
        public static Palette Current { get; private set; } = Palette.Dark();
        public static event EventHandler Changed;
        public static Font UiFont = new Font("Segoe UI", 9f);
        public static Font UiFontBold = new Font("Segoe UI Semibold", 9f);
        public static Font SmallFont = new Font("Segoe UI", 8f);
        public static Font TitleFont = new Font("Segoe UI Semibold", 13f);
        public static Font HeaderFont = new Font("Segoe UI Semibold", 10.5f);
        public static Font MonoFont = new Font("Consolas", 9.5f);
        public static Font IconFont = CreateIconFont(12f);
        public static Font IconFontLarge = CreateIconFont(16f);
        public static bool HasIconFont { get; private set; }

        private static Font CreateIconFont(float size)
        {
            foreach (var name in new[] { "Segoe Fluent Icons", "Segoe MDL2 Assets" })
            {
                using (var f = new Font(name, size))
                    if (f.Name == name) { HasIconFont = true; return new Font(name, size); }
            }
            return new Font("Segoe UI Symbol", size);
        }

        public static void SetNightMode(bool on)
        {
            Current = on ? Palette.Night() : Palette.Dark();
            Changed?.Invoke(null, EventArgs.Empty);
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr hwnd, string app, string idList);

        /// <summary>Dark title bar (Windows 10 1809+ / 11) and caption colour on Windows 11.</summary>
        public static void DarkTitleBar(Form f)
        {
            try
            {
                int on = 1;
                if (DwmSetWindowAttribute(f.Handle, 20, ref on, 4) != 0) DwmSetWindowAttribute(f.Handle, 19, ref on, 4);
                int caption = ColorTranslator.ToWin32(Current.Background);
                DwmSetWindowAttribute(f.Handle, 35, ref caption, 4); // DWMWA_CAPTION_COLOR (Win11)
                int text = ColorTranslator.ToWin32(Current.Text);
                DwmSetWindowAttribute(f.Handle, 36, ref text, 4);
            }
            catch { }
        }

        /// <summary>Use the Explorer dark theme for native scrollbars and common controls.</summary>
        public static void DarkNative(Control c)
        {
            try { if (c.IsHandleCreated) SetWindowTheme(c.Handle, "DarkMode_Explorer", null); else c.HandleCreated += (s, e) => SetWindowTheme(c.Handle, "DarkMode_Explorer", null); } catch { }
        }

        /// <summary>Recursively style standard controls (custom controls paint themselves).</summary>
        public static void Apply(Control root)
        {
            var p = Current;
            StyleOne(root, p);
            foreach (Control c in root.Controls) Apply(c);
        }

        private static void StyleOne(Control c, Palette p)
        {
            if (c is IThemed) { ((IThemed)c).ApplyTheme(p); return; }
            if (c is Form || c is UserControl || c is Panel || c is SplitContainer || c is TableLayoutPanel || c is FlowLayoutPanel || c is SplitterPanel)
            {
                if (!(c.Tag is string && (string)c.Tag == "keepcolor")) { c.BackColor = c is Form ? p.Background : (c.Parent != null && !(c.Parent is Form) ? c.Parent.BackColor : p.Background); }
                c.ForeColor = p.Text;
                if (c is SplitContainer) { ((SplitContainer)c).BackColor = p.Border; }
            }
            else if (c is TextBox || c is RichTextBox || c is MaskedTextBox)
            {
                c.BackColor = p.SurfaceRaised; c.ForeColor = p.Text;
                var tb = c as TextBoxBase; if (tb != null) tb.BorderStyle = BorderStyle.FixedSingle;
                if (c is RichTextBox || (c is TextBox && ((TextBox)c).Multiline)) DarkNative(c);
            }
            else if (c is NumericUpDown) { c.BackColor = p.SurfaceRaised; c.ForeColor = p.Text; ((NumericUpDown)c).BorderStyle = BorderStyle.FixedSingle; }
            else if (c is ComboBox)
            {
                var cb = (ComboBox)c;
                cb.FlatStyle = FlatStyle.Flat; cb.BackColor = p.SurfaceRaised; cb.ForeColor = p.Text;
                if (cb.DrawMode == DrawMode.Normal)
                {
                    // owner-draw so the closed box and the list follow the palette everywhere
                    cb.DrawMode = DrawMode.OwnerDrawFixed;
                    cb.ItemHeight = Math.Max(cb.ItemHeight, 18);
                    cb.DrawItem += (s, e) =>
                    {
                        var pal = Current;
                        bool sel = (e.State & DrawItemState.Selected) != 0 && (e.State & DrawItemState.ComboBoxEdit) == 0;
                        using (var b = new SolidBrush(sel ? pal.AccentSoft : pal.SurfaceRaised)) e.Graphics.FillRectangle(b, e.Bounds);
                        if (e.Index >= 0) TextRenderer.DrawText(e.Graphics, cb.GetItemText(cb.Items[e.Index]), cb.Font, e.Bounds, cb.Enabled ? pal.Text : pal.TextFaint, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
                    };
                }
                try { if (cb.IsHandleCreated) SetWindowTheme(cb.Handle, "DarkMode_CFD", null); else cb.HandleCreated += (s, e) => SetWindowTheme(cb.Handle, "DarkMode_CFD", null); } catch { }
            }
            else if (c is ListBox || c is CheckedListBox) { c.BackColor = p.Surface; c.ForeColor = p.Text; ((ListBox)c).BorderStyle = BorderStyle.None; DarkNative(c); }
            else if (c is ListView) { var lv = (ListView)c; lv.BackColor = p.Surface; lv.ForeColor = p.Text; lv.BorderStyle = BorderStyle.None; DarkNative(lv); }
            else if (c is TreeView) { var tv = (TreeView)c; tv.BackColor = p.Surface; tv.ForeColor = p.Text; tv.BorderStyle = BorderStyle.None; tv.LineColor = p.BorderStrong; DarkNative(tv); }
            else if (c is DataGridView) StyleGrid((DataGridView)c, p);
            else if (c is PropertyGrid) StylePropertyGrid((PropertyGrid)c, p);
            else if (c is CheckBox || c is RadioButton) { c.ForeColor = p.Text; c.BackColor = c.Parent?.BackColor ?? p.Background; ((ButtonBase)c).FlatStyle = FlatStyle.Flat; }
            else if (c is Button) { var b = (Button)c; b.FlatStyle = FlatStyle.Flat; b.BackColor = p.SurfaceRaised; b.ForeColor = p.Text; b.FlatAppearance.BorderColor = p.BorderStrong; b.FlatAppearance.MouseOverBackColor = p.SurfaceHover; b.FlatAppearance.MouseDownBackColor = p.SurfacePressed; }
            else if (c is Label || c is LinkLabel) { c.ForeColor = c.Tag is string && (string)c.Tag == "dim" ? p.TextDim : p.Text; c.BackColor = Color.Transparent; if (c is LinkLabel) ((LinkLabel)c).LinkColor = p.Accent; }
            else if (c is TrackBar) { c.BackColor = c.Parent?.BackColor ?? p.Background; }
            else if (c is DateTimePicker) { c.BackColor = p.SurfaceRaised; c.ForeColor = p.Text; }
            else if (c is ToolStrip) { ((ToolStrip)c).Renderer = new DarkToolStripRenderer(); c.BackColor = p.Surface; c.ForeColor = p.Text; }
            else if (c is TabControl) { c.BackColor = p.Background; }
            else if (c is ProgressBar) { }
            else { c.BackColor = c.Parent?.BackColor ?? p.Background; c.ForeColor = p.Text; }
        }

        public static void StyleGrid(DataGridView g, Palette p)
        {
            g.BackgroundColor = p.Surface;
            g.BorderStyle = BorderStyle.None;
            g.GridColor = p.Border;
            g.EnableHeadersVisualStyles = false;
            g.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            g.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
            g.RowHeadersVisible = false;
            g.ColumnHeadersDefaultCellStyle.BackColor = p.SurfaceRaised;
            g.ColumnHeadersDefaultCellStyle.ForeColor = p.TextDim;
            g.ColumnHeadersDefaultCellStyle.Font = UiFontBold;
            g.ColumnHeadersDefaultCellStyle.SelectionBackColor = p.SurfaceRaised;
            g.ColumnHeadersDefaultCellStyle.Padding = new Padding(4, 4, 4, 4);
            g.ColumnHeadersHeight = 30;
            g.DefaultCellStyle.BackColor = p.Surface;
            g.DefaultCellStyle.ForeColor = p.Text;
            g.DefaultCellStyle.SelectionBackColor = p.AccentSoft;
            g.DefaultCellStyle.SelectionForeColor = p.Text;
            g.DefaultCellStyle.Font = UiFont;
            g.AlternatingRowsDefaultCellStyle.BackColor = Blend(p.Surface, p.SurfaceRaised, 0.4);
            g.RowTemplate.Height = 26;
            DarkNative(g);
        }

        public static void StylePropertyGrid(PropertyGrid g, Palette p)
        {
            g.BackColor = p.Surface; g.ViewBackColor = p.Surface; g.ViewForeColor = p.Text; g.LineColor = p.SurfaceRaised;
            g.CategoryForeColor = p.Accent; g.CategorySplitterColor = p.Border; g.HelpBackColor = p.SurfaceRaised; g.HelpForeColor = p.TextDim;
            g.CommandsBackColor = p.Surface; g.CommandsForeColor = p.Text; g.ViewBorderColor = p.Border; g.HelpBorderColor = p.Border;
            g.SelectedItemWithFocusBackColor = p.AccentSoft; g.SelectedItemWithFocusForeColor = p.Text;
            g.DisabledItemForeColor = p.TextFaint;
            g.ToolbarVisible = false;
            DarkNative(g);
        }

        public static Color Blend(Color a, Color b, double t) =>
            Color.FromArgb((int)(a.A + (b.A - a.A) * t), (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

        public static Color WithAlpha(Color c, int a) => Color.FromArgb(Math.Max(0, Math.Min(255, a)), c);
    }

    /// <summary>Custom controls implement this to repaint with the active palette.</summary>
    public interface IThemed { void ApplyTheme(Palette p); }

    /// <summary>Segoe Fluent/MDL2 icon glyphs used throughout the UI.</summary>
    public static class Icons
    {
        public const string Home = "", Sky = "", Planner = "", Observatory = "", Stack = "", Process = "",
            Planet = "", Solve = "", Photometry = "", Spectrum = "", Comet = "", Radio = "", Database = "",
            Code = "", Settings = "", Search = "", Night = "", Play = "", Pause = "", Stop = "",
            Add = "", Remove = "", Open = "", Save = "", Print = "", Refresh = "", Zoom = "", ZoomOut = "",
            Fit = "", Undo = "", Redo = "", Up = "", Down = "", Left = "", Right = "", Connect = "",
            Camera = "", Target = "", Clock = "", Location = "", Info = "", Warning = "", Check = "",
            Close = "", More = "", Filter = "", Chart = "", Export = "", Download = "", List = "",
            Calendar = "", Eye = "", Grid = "", Layers = "", Moon = "", Star = "", Lock = "";
    }

    public class DarkToolStripRenderer : ToolStripProfessionalRenderer
    {
        public DarkToolStripRenderer() : base(new DarkColorTable()) { RoundedEdges = false; }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Theme.Current.Text : Theme.Current.TextFaint;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = Theme.Current.TextDim;
            base.OnRenderArrow(e);
        }

        private class DarkColorTable : ProfessionalColorTable
        {
            private static Palette P => Theme.Current;
            public override Color ToolStripGradientBegin => P.Surface;
            public override Color ToolStripGradientMiddle => P.Surface;
            public override Color ToolStripGradientEnd => P.Surface;
            public override Color ToolStripBorder => P.Border;
            public override Color MenuStripGradientBegin => P.Surface;
            public override Color MenuStripGradientEnd => P.Surface;
            public override Color MenuItemSelected => P.SurfaceHover;
            public override Color MenuItemBorder => P.BorderStrong;
            public override Color MenuItemSelectedGradientBegin => P.SurfaceHover;
            public override Color MenuItemSelectedGradientEnd => P.SurfaceHover;
            public override Color MenuItemPressedGradientBegin => P.SurfacePressed;
            public override Color MenuItemPressedGradientEnd => P.SurfacePressed;
            public override Color ToolStripDropDownBackground => P.SurfaceRaised;
            public override Color ImageMarginGradientBegin => P.SurfaceRaised;
            public override Color ImageMarginGradientMiddle => P.SurfaceRaised;
            public override Color ImageMarginGradientEnd => P.SurfaceRaised;
            public override Color SeparatorDark => P.Border;
            public override Color SeparatorLight => P.Border;
            public override Color ButtonSelectedHighlight => P.SurfaceHover;
            public override Color ButtonSelectedGradientBegin => P.SurfaceHover;
            public override Color ButtonSelectedGradientEnd => P.SurfaceHover;
            public override Color ButtonPressedGradientBegin => P.SurfacePressed;
            public override Color ButtonPressedGradientEnd => P.SurfacePressed;
            public override Color ButtonCheckedGradientBegin => P.AccentSoft;
            public override Color ButtonCheckedGradientEnd => P.AccentSoft;
            public override Color CheckBackground => P.AccentSoft;
            public override Color StatusStripGradientBegin => P.Surface;
            public override Color StatusStripGradientEnd => P.Surface;
        }
    }
}
