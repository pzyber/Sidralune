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
                ConstellationLine = r(0x60), ConstellationBoundary = r(0x38), ConstellationName = r(0x80), GridEquatorial = r(0x30), GridAltAz = r(0x2C), Ecliptic = r(0x50), GalacticEquator = r(0x40), MilkyWay = r(0x30), // as far above the night sky as the day band is
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
            try
            {
                // also hook HandleCreated so the theme survives handle re-creation; redraw the frame so scroll bars update
                if (c.IsHandleCreated) { SetWindowTheme(c.Handle, "DarkMode_Explorer", null); RedrawAll(c); }
                c.HandleCreated += (s, e) => SetWindowTheme(c.Handle, "DarkMode_Explorer", null);
            }
            catch { }
        }

        private static void DarkNativeTree(Control c)
        {
            foreach (Control k in c.Controls) { DarkNative(k); DarkNativeTree(k); }
            c.ControlAdded += (s, e) => { DarkNative(e.Control); DarkNativeTree(e.Control); };
        }

        /// <summary>Recursively style standard controls (custom controls paint themselves).</summary>
        public static void Apply(Control root)
        {
            var p = Current;
            StyleOne(root, p);
            foreach (Control c in root.Controls) Apply(c);
        }

        // ------------------------------------------------------------------ painting over native parts
        // Several parts of standard controls are drawn by Windows with system colours (borders, drop-down and spin
        // buttons, check boxes, selection highlights) and ignore BackColor/ForeColor. They are repainted here with
        // the palette, so every theme (including the red night theme) covers them.

        [DllImport("user32.dll")] private static extern IntPtr GetWindowDC(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
        [DllImport("user32.dll")] private static extern bool RedrawWindow(IntPtr hwnd, IntPtr rect, IntPtr rgn, uint flags);
        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out RECT r);
        [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hwnd, ref POINT p);
        [DllImport("user32.dll")] private static extern bool ValidateRect(IntPtr hwnd, ref RECT r);

        /// <summary>The four strips of a frame <paramref name="t"/> pixels wide around a window of size <paramref name="sz"/>.</summary>
        private static Rectangle[] Frame(Size sz, int t) => new[]
        {
            new Rectangle(0, 0, sz.Width, t), new Rectangle(0, sz.Height - t, sz.Width, t),
            new Rectangle(0, 0, t, sz.Height), new Rectangle(sz.Width - t, 0, t, sz.Height)
        };

        private static Rectangle[] Whole(Size sz) => new[] { new Rectangle(Point.Empty, sz) };

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Control, object> Hooked = new System.Runtime.CompilerServices.ConditionalWeakTable<Control, object>();

        /// <summary>True the first time it is called for a control, so hooks are attached once even when themes are re-applied.</summary>
        private static bool FirstHook(Control c)
        {
            object o;
            if (Hooked.TryGetValue(c, out o)) return false;
            Hooked.Add(c, new object());
            return true;
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Control, object> TreeHooked = new System.Runtime.CompilerServices.ConditionalWeakTable<Control, object>();
        private static bool FirstHookTree(Control c) { object o; if (TreeHooked.TryGetValue(c, out o)) return false; TreeHooked.Add(c, new object()); return true; }

        /// <summary>Lets Windows paint a window, then paints over it in window coordinates (including the non-client border).</summary>
        private sealed class OverPainter : NativeWindow
        {
            private readonly Action<Graphics, Size> _paint;
            private readonly Func<Size, Rectangle[]> _owned;

            /// <param name="owned">
            /// Optional: the parts of the window (in window coordinates) that <paramref name="paint"/> covers completely. They
            /// are taken out of the update region before the control paints, so its own grey borders and hot (hover) states
            /// never reach the screen in those parts, not even for the moment before they are painted over.
            /// </param>
            public OverPainter(Control c, Action<Graphics, Size> paint, Func<Size, Rectangle[]> owned = null)
            {
                _paint = paint;
                _owned = owned;
                if (c.IsHandleCreated) AssignHandle(c.Handle);
                c.HandleCreated += (s, e) => AssignHandle(c.Handle);
                c.HandleDestroyed += (s, e) => ReleaseHandle();
            }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == 0x000F && _owned != null) ValidateOwned();
                base.WndProc(ref m);
                // WM_PAINT, WM_NCPAINT, WM_SETFOCUS, WM_KILLFOCUS, WM_ENABLE, CB_SETCURSEL, WM_MOUSEMOVE, WM_MOUSELEAVE: paint
                // on the window (mouse messages: some controls draw a hot border directly when the pointer enters or leaves)
                if (m.Msg == 0x000F || m.Msg == 0x0085 || m.Msg == 0x0007 || m.Msg == 0x0008 || m.Msg == 0x000A || m.Msg == 0x014E || m.Msg == 0x0200 || m.Msg == 0x02A3) Draw();
                // WM_PRINT (used by DrawToBitmap and print previews): paint into the supplied device context
                else if (m.Msg == 0x0317 && m.WParam != IntPtr.Zero && ((long)m.LParam & 0x0002) != 0)
                {
                    RECT r;
                    if (!GetWindowRect(Handle, out r)) return;
                    try { using (var g = Graphics.FromHdc(m.WParam)) _paint(g, new Size(r.Right - r.Left, r.Bottom - r.Top)); } catch { }
                }
            }

            private void ValidateOwned()
            {
                RECT wr, cr;
                if (!GetWindowRect(Handle, out wr) || !GetClientRect(Handle, out cr)) return;
                var origin = new POINT();
                ClientToScreen(Handle, ref origin);
                // only when the client area is the whole window: then window and client coordinates are the same
                if (origin.X != wr.Left || origin.Y != wr.Top || cr.Right != wr.Right - wr.Left || cr.Bottom != wr.Bottom - wr.Top) return;
                try
                {
                    foreach (var r in _owned(new Size(cr.Right, cr.Bottom)))
                    {
                        var rr = new RECT { Left = r.Left, Top = r.Top, Right = r.Right, Bottom = r.Bottom };
                        ValidateRect(Handle, ref rr);
                    }
                }
                catch { }
            }

            private void Draw()
            {
                if (Handle == IntPtr.Zero) return;
                RECT r;
                if (!GetWindowRect(Handle, out r)) return;
                var dc = GetWindowDC(Handle);
                if (dc == IntPtr.Zero) return;
                try { using (var g = Graphics.FromHdc(dc)) _paint(g, new Size(r.Right - r.Left, r.Bottom - r.Top)); }
                catch { }
                finally { ReleaseDC(Handle, dc); }
            }
        }

        // ------------------------------------------------------------------ scroll bars
        // Windows draws scroll bars itself (at best dark grey with the dark Explorer theme). ScrollPainter lets Windows
        // paint and handle input as usual, asks it for the exact layout (arrows, track, thumb, hot/pressed states) with
        // GetScrollBarInfo, and paints the bar again in the palette whenever that layout or state changes.

        [StructLayout(LayoutKind.Sequential)]
        private struct SCROLLBARINFO
        {
            public int cbSize;
            public RECT rcScrollBar;
            public int dxyLineButton, xyThumbTop, xyThumbBottom, reserved;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)] public int[] rgstate;
        }

        [DllImport("user32.dll")] private static extern bool GetScrollBarInfo(IntPtr hwnd, int idObject, ref SCROLLBARINFO psbi);

        private const int OBJID_HSCROLL = unchecked((int)0xFFFFFFFA), OBJID_VSCROLL = unchecked((int)0xFFFFFFFB), OBJID_CLIENT = unchecked((int)0xFFFFFFFC);
        private const int STATE_INVISIBLE = 0x8000, STATE_UNAVAILABLE = 0x0001, STATE_PRESSED = 0x0008, STATE_HOT = 0x0080;

        private sealed class ScrollPainter : NativeWindow
        {
            private readonly bool _isControl; // a SCROLLBAR control (grid and property grid scroll bars) rather than window scroll bars
            private readonly bool _vertical;

            public ScrollPainter(Control c, bool isControl, bool vertical)
            {
                _isControl = isControl; _vertical = vertical;
                if (c.IsHandleCreated) AssignHandle(c.Handle);
                c.HandleCreated += (s, e) => AssignHandle(c.Handle);
                c.HandleDestroyed += (s, e) => ReleaseHandle();
            }

            private readonly bool _border;
            public ScrollPainter(IntPtr hwnd) { _border = true; AssignHandle(hwnd); }

            private bool _busy;

            protected override void WndProc(ref Message m)
            {
                base.WndProc(ref m);
                // GetScrollBarInfo sends SBM_GETSCROLLBARINFO (0x00EB) to scroll bar controls, so never react to our own queries
                if (_busy || Handle == IntPtr.Zero || m.Msg == 0x00EB || m.Msg == 0x0082) return;
                _busy = true;
                try
                {
                    if (m.Msg == 0x0317) { PaintTo(m.WParam, ((long)m.LParam & (_isControl ? 0x0004 : 0x0002)) != 0); return; } // WM_PRINT
                    // Windows can redraw a scroll bar while handling almost any message (SetScrollInfo during text edits,
                    // scrolling, layout, mouse tracking), so the bar is painted again after every message (the drop-down
                    // list this is used for is switched to the classic style, which has no hover animations).
                    Paint();
                }
                finally { _busy = false; }
            }

            private SCROLLBARINFO Info(int id)
            {
                var i = new SCROLLBARINFO { cbSize = Marshal.SizeOf(typeof(SCROLLBARINFO)), rgstate = new int[6] };
                if (!GetScrollBarInfo(Handle, id, ref i)) i.rgstate[0] = STATE_INVISIBLE;
                return i;
            }

            private void Paint()
            {
                var dc = GetWindowDC(Handle);
                if (dc == IntPtr.Zero) return;
                try { PaintTo(dc, true); } finally { ReleaseDC(Handle, dc); }
            }

            private void PaintTo(IntPtr dc, bool include)
            {
                if (!include || dc == IntPtr.Zero) return;
                RECT w;
                if (!GetWindowRect(Handle, out w)) return;
                try
                {
                    using (var g = Graphics.FromHdc(dc))
                    {
                        var origin = new Point(w.Left, w.Top);
                        if (_border) using (var pen = new Pen(Current.BorderStrong)) g.DrawRectangle(pen, 0, 0, w.Right - w.Left - 1, w.Bottom - w.Top - 1);
                        if (_isControl) DrawBar(g, Info(OBJID_CLIENT), _vertical, origin);
                        else
                        {
                            var v = Info(OBJID_VSCROLL); var h = Info(OBJID_HSCROLL);
                            DrawBar(g, v, true, origin);
                            DrawBar(g, h, false, origin);
                            // the corner between two visible bars
                            if ((v.rgstate[0] & STATE_INVISIBLE) == 0 && (h.rgstate[0] & STATE_INVISIBLE) == 0)
                                using (var b = new SolidBrush(Current.Surface))
                                    g.FillRectangle(b, v.rcScrollBar.Left - origin.X, h.rcScrollBar.Top - origin.Y, v.rcScrollBar.Right - v.rcScrollBar.Left, h.rcScrollBar.Bottom - h.rcScrollBar.Top);
                        }
                    }
                }
                catch { }
            }

            private static void DrawBar(Graphics g, SCROLLBARINFO i, bool vertical, Point origin)
            {
                if ((i.rgstate[0] & STATE_INVISIBLE) != 0) return;
                var p = Current;
                var r = Rectangle.FromLTRB(i.rcScrollBar.Left - origin.X, i.rcScrollBar.Top - origin.Y, i.rcScrollBar.Right - origin.X, i.rcScrollBar.Bottom - origin.Y);
                if (r.Width <= 0 || r.Height <= 0) return;
                bool disabled = (i.rgstate[0] & STATE_UNAVAILABLE) != 0;
                using (var b = new SolidBrush(p.Surface)) g.FillRectangle(b, r);
                int btn = Math.Max(0, i.dxyLineButton);
                Rectangle a1, a2;
                if (vertical) { a1 = new Rectangle(r.X, r.Y, r.Width, btn); a2 = new Rectangle(r.X, r.Bottom - btn, r.Width, btn); }
                else { a1 = new Rectangle(r.X, r.Y, btn, r.Height); a2 = new Rectangle(r.Right - btn, r.Y, btn, r.Height); }
                Action<Rectangle, int, bool> arrow = (ar, state, first) =>
                {
                    if (ar.Width <= 0 || ar.Height <= 0) return;
                    bool pressed = (state & STATE_PRESSED) != 0, hot = (state & STATE_HOT) != 0;
                    if (pressed || hot) using (var b = new SolidBrush(pressed ? p.SurfacePressed : p.SurfaceHover)) g.FillRectangle(b, ar);
                    var col = disabled || (state & STATE_UNAVAILABLE) != 0 ? p.TextFaint : (pressed || hot ? p.Text : p.TextDim);
                    if (vertical) DrawChevron(g, ar, first, col);
                    else DrawSideChevron(g, ar, first, col);
                };
                arrow(a1, i.rgstate[1], true);
                arrow(a2, i.rgstate[5], false);
                if (!disabled && i.xyThumbBottom > i.xyThumbTop)
                {
                    int st = i.rgstate[3];
                    var col = (st & STATE_PRESSED) != 0 ? p.TextDim : (st & STATE_HOT) != 0 ? Blend(p.TextFaint, p.TextDim, 0.5) : p.TextFaint;
                    int pad = Math.Max(2, (vertical ? r.Width : r.Height) / 4);
                    var thumb = vertical
                        ? new Rectangle(r.X + pad, r.Y + i.xyThumbTop + 1, r.Width - 2 * pad, i.xyThumbBottom - i.xyThumbTop - 2)
                        : new Rectangle(r.X + i.xyThumbTop + 1, r.Y + pad, i.xyThumbBottom - i.xyThumbTop - 2, r.Height - 2 * pad);
                    if (thumb.Width > 0 && thumb.Height > 0)
                    {
                        var old = g.SmoothingMode;
                        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                        using (var path = Rounded(thumb, Math.Min(thumb.Width, thumb.Height) / 2f))
                        using (var b = new SolidBrush(col)) g.FillPath(b, path);
                        g.SmoothingMode = old;
                    }
                }
            }
        }

        private static System.Drawing.Drawing2D.GraphicsPath Rounded(RectangleF r, float rad)
        {
            var path = new System.Drawing.Drawing2D.GraphicsPath();
            float d = Math.Max(0.5f, Math.Min(rad * 2, Math.Min(r.Width, r.Height)));
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        private static void DrawSideChevron(Graphics g, RectangleF r, bool left, Color col)
        {
            float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f, s = Math.Max(2.5f, Math.Min(r.Width, r.Height) / 5f);
            var old = g.SmoothingMode;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var pen = new Pen(col, 1.4f))
            {
                if (left) g.DrawLines(pen, new[] { new PointF(cx + s / 2, cy - s), new PointF(cx - s / 2, cy), new PointF(cx + s / 2, cy + s) });
                else g.DrawLines(pen, new[] { new PointF(cx - s / 2, cy - s), new PointF(cx + s / 2, cy), new PointF(cx - s / 2, cy + s) });
            }
            g.SmoothingMode = old;
        }

        private static void HookScroll(Control c)
        {
            // panels that never scroll need no overlays
            var sc = c as ScrollableControl;
            if (sc != null && !sc.AutoScroll) return;
            ScrollOverlays.Attach(c);
        }

        [DllImport("user32.dll")] private static extern bool GetComboBoxInfo(IntPtr hwnd, ref COMBOBOXINFO info);
        [StructLayout(LayoutKind.Sequential)]
        private struct COMBOBOXINFO { public int cbSize; public RECT rcItem, rcButton; public int stateButton; public IntPtr hwndCombo, hwndItem, hwndList; }
        private static readonly HashSet<IntPtr> HookedLists = new HashSet<IntPtr>();

        /// <summary>The drop-down list of a combo box is a separate window with its own scroll bar.</summary>
        private static void HookComboList(ComboBox cb)
        {
            cb.DropDown += (s, e) =>
            {
                try
                {
                    var info = new COMBOBOXINFO { cbSize = Marshal.SizeOf(typeof(COMBOBOXINFO)) };
                    if (!GetComboBoxInfo(cb.Handle, ref info) || info.hwndList == IntPtr.Zero || !HookedLists.Add(info.hwndList)) return;
                    SetWindowTheme(info.hwndList, "", "");
                    new ScrollPainter(info.hwndList);
                }
                catch { }
            };
        }

        /// <summary>Repaint a control including its non-client area (after a theme change).</summary>
        private static void RedrawAll(Control c)
        {
            // RDW_INVALIDATE | RDW_FRAME | RDW_ALLCHILDREN | RDW_UPDATENOW
            if (c.IsHandleCreated) RedrawWindow(c.Handle, IntPtr.Zero, IntPtr.Zero, 0x0001 | 0x0400 | 0x0080 | 0x0100);
        }

        private static void DrawChevron(Graphics g, RectangleF r, bool up, Color col)
        {
            float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f, s = Math.Max(2.5f, Math.Min(r.Width, r.Height) / 5f);
            var old = g.SmoothingMode;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var pen = new Pen(col, 1.4f))
            {
                if (up) g.DrawLines(pen, new[] { new PointF(cx - s, cy + s / 2), new PointF(cx, cy - s / 2), new PointF(cx + s, cy + s / 2) });
                else g.DrawLines(pen, new[] { new PointF(cx - s, cy - s / 2), new PointF(cx, cy + s / 2), new PointF(cx + s, cy - s / 2) });
            }
            g.SmoothingMode = old;
        }

        private static Color BorderFor(Control c) => c.Focused || c.ContainsFocus ? Current.Accent : Current.BorderStrong;

        private static void HookBorder(Control c)
        {
            new OverPainter(c, (g, sz) =>
            {
                using (var pen = new Pen(BorderFor(c)))
                {
                    g.DrawRectangle(pen, 0, 0, sz.Width - 1, sz.Height - 1);
                    // a text box with scroll bars draws a grey border edge in its last client column/row, beside the bar
                    // (the outer edge is under the scroll bar overlay): close the box there in the palette
                    RECT wr;
                    if (!(c is TextBoxBase) || !c.IsHandleCreated || !GetWindowRect(c.Handle, out wr)) return;
                    var cr = c.RectangleToScreen(c.ClientRectangle);
                    int right = cr.Right - wr.Left, bottom = cr.Bottom - wr.Top;
                    if (right < sz.Width - 1) g.DrawLine(pen, right - 1, 0, right - 1, bottom - 1);
                    if (bottom < sz.Height - 1) g.DrawLine(pen, 0, bottom - 1, right - 1, bottom - 1);
                }
            }, sz => Frame(sz, 1));
        }

        private static void HookComboBox(ComboBox cb)
        {
            new OverPainter(cb, (g, sz) =>
            {
                var p = Current;
                int bw = Math.Max(SystemInformation.HorizontalScrollBarArrowWidth, (int)(17 * cb.DeviceDpi / 96.0)) + 2;
                var btn = new Rectangle(sz.Width - bw - 1, 1, bw, sz.Height - 2);
                using (var b = new SolidBrush(cb.Enabled ? p.SurfaceRaised : p.Surface)) g.FillRectangle(b, btn);
                DrawChevron(g, btn, false, cb.Enabled ? p.TextDim : p.TextFaint);
                using (var pen = new Pen(cb.Enabled ? p.SurfaceRaised : p.Surface)) g.DrawRectangle(pen, 1, 1, sz.Width - 3, sz.Height - 3);
                using (var pen = new Pen(BorderFor(cb))) g.DrawRectangle(pen, 0, 0, sz.Width - 1, sz.Height - 1);
            }, sz =>
            {
                int bw = Math.Max(SystemInformation.HorizontalScrollBarArrowWidth, (int)(17 * cb.DeviceDpi / 96.0)) + 2;
                var f = Frame(sz, 2); return new[] { f[0], f[1], f[2], f[3], new Rectangle(sz.Width - bw - 1, 1, bw, sz.Height - 2) };
            });
        }

        private static void HookUpDown(NumericUpDown n)
        {
            HookBorder(n);
            foreach (Control child in n.Controls)
            {
                if (child is TextBox) continue; // the edit part
                var buttons = child;
                new OverPainter(buttons, (g, sz) =>
                {
                    var p = Current;
                    using (var b = new SolidBrush(p.SurfaceRaised)) g.FillRectangle(b, 0, 0, sz.Width, sz.Height);
                    using (var pen = new Pen(p.Border))
                    {
                        g.DrawLine(pen, 0, 0, 0, sz.Height);
                        g.DrawLine(pen, 0, sz.Height / 2, sz.Width, sz.Height / 2);
                    }
                    var col = n.Enabled ? p.TextDim : p.TextFaint;
                    DrawChevron(g, new RectangleF(1, 0, sz.Width - 1, sz.Height / 2f), true, col);
                    DrawChevron(g, new RectangleF(1, sz.Height / 2f, sz.Width - 1, sz.Height / 2f), false, col);
                }, Whole);
            }
        }

        /// <summary>Draw a check box (or radio button) glyph in the palette.</summary>
        public static void DrawCheckGlyph(Graphics g, Rectangle box, bool isChecked, bool enabled, bool radio = false)
        {
            var p = Current;
            var old = g.SmoothingMode;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var fill = new SolidBrush(isChecked && enabled ? p.Accent : p.SurfaceRaised))
            using (var pen = new Pen(isChecked && enabled ? p.Accent : (enabled ? p.TextDim : p.TextFaint)))
            {
                if (radio)
                {
                    using (var bg = new SolidBrush(p.SurfaceRaised)) g.FillEllipse(bg, box);
                    g.DrawEllipse(pen, box);
                    if (isChecked) { var dot = Rectangle.Inflate(box, -box.Width / 4, -box.Height / 4); g.FillEllipse(fill, dot); }
                }
                else
                {
                    g.FillRectangle(fill, box);
                    g.DrawRectangle(pen, box);
                    if (isChecked)
                        using (var tick = new Pen(p.AccentText, Math.Max(1.6f, box.Width / 7f)))
                            g.DrawLines(tick, new[] { new PointF(box.X + box.Width * 0.22f, box.Y + box.Height * 0.52f), new PointF(box.X + box.Width * 0.42f, box.Y + box.Height * 0.72f), new PointF(box.X + box.Width * 0.78f, box.Y + box.Height * 0.3f) });
                }
            }
            g.SmoothingMode = old;
        }

        private static void HookCheckBox(ButtonBase b)
        {
            // ButtonBase raises Paint after its own drawing, so the whole control is redrawn here
            b.Paint += (s, e) =>
            {
                var p = Current;
                var cb = b as CheckBox; var rb = b as RadioButton;
                if ((cb != null && cb.Appearance == Appearance.Button) || (rb != null && rb.Appearance == Appearance.Button)) return;
                bool isChecked = cb != null ? cb.CheckState != CheckState.Unchecked : rb != null && rb.Checked;
                var g = e.Graphics;
                using (var bg = new SolidBrush(b.BackColor.A == 0 ? (b.Parent != null ? b.Parent.BackColor : p.Background) : b.BackColor)) g.FillRectangle(bg, b.ClientRectangle);
                int size = (int)Math.Round(13 * b.DeviceDpi / 96.0);
                var box = new Rectangle(1, (b.Height - size) / 2, size - 1, size - 1);
                DrawCheckGlyph(g, box, isChecked, b.Enabled, rb != null);
                if (!string.IsNullOrEmpty(b.Text))
                {
                    var tr = new Rectangle(size + 5, 0, b.Width - size - 5, b.Height);
                    TextRenderer.DrawText(g, b.Text, b.Font, tr, b.Enabled ? b.ForeColor : p.TextFaint, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                }
                if (b.Focused) using (var pen = new Pen(p.Focus) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dot }) g.DrawRectangle(pen, 0, 0, b.Width - 1, b.Height - 1);
            };
        }

        private static void HookListBox(ListBox lb)
        {
            if (lb is CheckedListBox || lb.DrawMode != DrawMode.Normal) return;
            lb.DrawMode = DrawMode.OwnerDrawFixed;
            lb.ItemHeight = Math.Max(lb.ItemHeight, (int)(20 * lb.DeviceDpi / 96.0));
            lb.DrawItem += (s, e) =>
            {
                var p = Current;
                bool sel = (e.State & DrawItemState.Selected) != 0;
                using (var b = new SolidBrush(sel ? p.AccentSoft : lb.BackColor)) e.Graphics.FillRectangle(b, e.Bounds);
                if (e.Index >= 0 && e.Index < lb.Items.Count)
                    TextRenderer.DrawText(e.Graphics, lb.GetItemText(lb.Items[e.Index]), lb.Font, new Rectangle(e.Bounds.X + 4, e.Bounds.Y, e.Bounds.Width - 4, e.Bounds.Height), lb.Enabled ? p.Text : p.TextFaint, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            };
        }

        /// <summary>
        /// Toolbars and button rows are flow panels docked to the top or bottom with a fixed height, so in a narrow window
        /// the controls that do not fit on the row are cut off. Let them wrap onto further rows and grow the panel to fit;
        /// the designed height stays the minimum, so wide windows look as before.
        /// </summary>
        private static void FitFlowHeight(FlowLayoutPanel f)
        {
            if (f.AutoSize || (f.Dock != DockStyle.Top && f.Dock != DockStyle.Bottom)) return;
            if (f.FlowDirection != FlowDirection.LeftToRight && f.FlowDirection != FlowDirection.RightToLeft) return;
            int min = f.Height;
            f.WrapContents = true;
            bool busy = false;
            Action fit = () =>
            {
                if (busy || f.Width <= 0 || f.IsDisposed) return;
                busy = true;
                try
                {
                    int h = Math.Max(min, f.GetPreferredSize(new Size(f.Width, 0)).Height);
                    if (f.Height != h) f.Height = h;
                }
                finally { busy = false; }
            };
            f.Layout += (s, e) => fit();
            f.SizeChanged += (s, e) => fit();
        }

        private static void HookTreeView(TreeView tv)
        {
            tv.FullRowSelect = false;
            tv.DrawMode = TreeViewDrawMode.OwnerDrawText;
            tv.DrawNode += (s, e) =>
            {
                var p = Current;
                if (e.Bounds.IsEmpty) return;
                bool sel = e.Node.IsSelected && (tv.Focused || !tv.HideSelection);
                var r = new Rectangle(e.Bounds.X, e.Bounds.Y, Math.Max(e.Bounds.Width, tv.ClientSize.Width - e.Bounds.X), e.Bounds.Height);
                using (var b = new SolidBrush(sel ? p.AccentSoft : tv.BackColor)) e.Graphics.FillRectangle(b, r);
                var col = e.Node.ForeColor.IsEmpty ? p.Text : e.Node.ForeColor;
                TextRenderer.DrawText(e.Graphics, e.Node.Text, e.Node.NodeFont ?? tv.Font, r, col, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix);
                // the expand/collapse glyph is drawn by the theme in grey/white; draw it again in the palette
                if (tv.ShowPlusMinus && e.Node.Nodes.Count > 0 && (tv.ShowRootLines || e.Node.Parent != null))
                {
                    int image = tv.ImageList != null ? tv.ImageList.ImageSize.Width + 3 : 0;
                    float cx = e.Bounds.X - image - tv.Indent / 2f, cy = e.Bounds.Y + e.Bounds.Height / 2f;
                    float half = Math.Min(tv.Indent, e.Bounds.Height) * 0.4f;
                    using (var b = new SolidBrush(tv.BackColor)) e.Graphics.FillRectangle(b, cx - half, cy - half, half * 2, half * 2);
                    DrawTreeGlyph(e.Graphics, cx, cy, Math.Max(2.5f, half * 0.45f), e.Node.IsExpanded, p.TextDim);
                }
            };
        }

        /// <summary>Chevron pointing right (collapsed) or down (expanded), centred on (cx, cy).</summary>
        private static void DrawTreeGlyph(Graphics g, float cx, float cy, float s, bool expanded, Color col)
        {
            var old = g.SmoothingMode;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var pen = new Pen(col, 1.4f))
            {
                if (expanded) g.DrawLines(pen, new[] { new PointF(cx - s, cy - s / 2), new PointF(cx, cy + s / 2), new PointF(cx + s, cy - s / 2) });
                else g.DrawLines(pen, new[] { new PointF(cx - s / 2, cy - s), new PointF(cx + s / 2, cy), new PointF(cx - s / 2, cy + s) });
            }
            g.SmoothingMode = old;
        }

        private static void HookGrid(DataGridView g)
        {
            // a click on a tick box changes its value at once: the grid would otherwise keep the edit, and show the old
            // tick, until another cell is chosen
            g.CurrentCellDirtyStateChanged += (s, e) => { if (g.IsCurrentCellDirty && g.CurrentCell is DataGridViewCheckBoxCell) g.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            // cells hold text, which sorts alphabetically ("9.2" after "14.0", "-04:25" after "+19:24"); numbers, angles and
            // times sort by value instead, and empty cells come after them
            g.SortCompare += (s, e) =>
            {
                double a, b;
                bool hasA = SortValue(e.CellValue1, out a), hasB = SortValue(e.CellValue2, out b);
                bool emptyA = IsEmptyCell(e.CellValue1), emptyB = IsEmptyCell(e.CellValue2);
                if (!(hasA || emptyA) || !(hasB || emptyB) || (emptyA && emptyB)) return; // text: the grid's own order
                e.SortResult = hasA && hasB ? a.CompareTo(b) : hasA ? -1 : 1;
                e.Handled = true;
            };
            // check-box cells are drawn by Windows; paint them in the palette instead
            g.CellPainting += (s, e) =>
            {
                if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
                if (g.Columns[e.ColumnIndex] is DataGridViewComboBoxColumn)
                {
                    // the drop-down button of combo cells is drawn by Windows; draw the cell ourselves
                    var pal = Current;
                    e.PaintBackground(e.CellBounds, (e.State & DataGridViewElementStates.Selected) != 0);
                    int bw = (int)(16 * g.DeviceDpi / 96.0);
                    var btn = new Rectangle(e.CellBounds.Right - bw - 2, e.CellBounds.Y + 3, bw, e.CellBounds.Height - 6);
                    var text = new Rectangle(e.CellBounds.X + 4, e.CellBounds.Y, e.CellBounds.Width - bw - 8, e.CellBounds.Height);
                    TextRenderer.DrawText(e.Graphics, Convert.ToString(e.FormattedValue), e.CellStyle.Font ?? g.Font, text, pal.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
                    using (var b = new SolidBrush(pal.SurfaceRaised)) e.Graphics.FillRectangle(b, btn);
                    using (var pen = new Pen(pal.BorderStrong)) e.Graphics.DrawRectangle(pen, btn);
                    DrawChevron(e.Graphics, btn, false, pal.TextDim);
                    e.Handled = true;
                    return;
                }
                if (!(g.Columns[e.ColumnIndex] is DataGridViewCheckBoxColumn)) return;
                e.PaintBackground(e.CellBounds, true);
                bool isChecked = e.Value is bool && (bool)e.Value;
                int size = (int)Math.Round(13 * g.DeviceDpi / 96.0);
                var box = new Rectangle(e.CellBounds.X + (e.CellBounds.Width - size) / 2, e.CellBounds.Y + (e.CellBounds.Height - size) / 2, size - 1, size - 1);
                DrawCheckGlyph(e.Graphics, box, isChecked, !g.ReadOnly || !g.Columns[e.ColumnIndex].ReadOnly);
                e.Handled = true;
            };
            // editing controls (combo boxes, text boxes in cells) are created on demand
            g.EditingControlShowing += (s, e) => Apply(e.Control);
            if (FirstHookTree(g)) DarkNativeTree(g); // grid scroll bars are child controls
        }

        private static readonly System.Text.RegularExpressions.Regex Sexagesimal =
            new System.Text.RegularExpressions.Regex(@"^([+-]?)(\d+):(\d+)(?::(\d+(?:\.\d+)?))?$");
        private static readonly System.Text.RegularExpressions.Regex NumberWithUnit =
            new System.Text.RegularExpressions.Regex(@"^([+-]?(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?)\s*(?:°|′|″|'|""|%|km|AU|au|h|min|m|s|ms|d|px|ppt|mag|MHz|K|y|ly|pc|kpc|Mpc)?$");

        /// <summary>
        /// The value a grid cell sorts by: a number, possibly with a unit ("35°", "4.2″", "12 km"), or hours/degrees and
        /// minutes (and seconds) such as "-04:25:32" or "21:26". False for anything else, which sorts as text.
        /// </summary>
        public static bool SortValue(object cell, out double value)
        {
            value = double.NaN;
            if (cell == null || cell is DBNull) return false;
            if (cell is double || cell is float || cell is int || cell is long || cell is decimal || cell is short)
            {
                value = Convert.ToDouble(cell, System.Globalization.CultureInfo.InvariantCulture);
                return !double.IsNaN(value);
            }
            var t = (cell as string ?? cell.ToString()).Trim().Replace('\u2212', '-'); // the typographic minus
            if (t.Length == 0) return false;
            var m = Sexagesimal.Match(t);
            if (m.Success)
            {
                double sec = m.Groups[4].Success ? double.Parse(m.Groups[4].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
                value = (int.Parse(m.Groups[2].Value) + int.Parse(m.Groups[3].Value) / 60.0 + sec / 3600) * (m.Groups[1].Value == "-" ? -1 : 1);
                return true;
            }
            m = NumberWithUnit.Match(t);
            return m.Success && double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value);
        }

        // an empty cell, or one saying a value is unknown
        private static bool IsEmptyCell(object cell)
        {
            if (cell == null || cell is DBNull) return true;
            var t = cell as string;
            if (t == null) return false;
            t = t.Trim();
            return t.Length == 0 || t == "—" || t == "–" || t == "-" || t == "?" || t == "n/a";
        }

        /// <summary>A tooltip drawn in the palette instead of the system's light yellow or white.</summary>
        public static ToolTip CreateToolTip()
        {
            var t = new ToolTip { OwnerDraw = true };
            t.Draw += (s, e) =>
            {
                var p = Current;
                using (var b = new SolidBrush(p.SurfaceRaised)) e.Graphics.FillRectangle(b, e.Bounds);
                using (var pen = new Pen(p.BorderStrong)) e.Graphics.DrawRectangle(pen, 0, 0, e.Bounds.Width - 1, e.Bounds.Height - 1);
                TextRenderer.DrawText(e.Graphics, e.ToolTipText, UiFont, e.Bounds, p.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix);
            };
            t.Popup += (s, e) =>
            {
                var sz = TextRenderer.MeasureText(t.GetToolTip(e.AssociatedControl) ?? "", UiFont);
                e.ToolTipSize = new Size(sz.Width + 12, sz.Height + 8);
            };
            return t;
        }

        private static void StyleOne(Control c, Palette p)
        {
            bool first = FirstHook(c);
            if (c is IThemed)
            {
                ((IThemed)c).ApplyTheme(p);
                if (first && c is ScrollableControl) { DarkNative(c); HookScroll(c); }
                else if (c is ScrollableControl) RedrawAll(c);
                return;
            }
            if (c is Form || c is UserControl || c is Panel || c is SplitContainer || c is TableLayoutPanel || c is FlowLayoutPanel || c is SplitterPanel)
            {
                if (!(c.Tag is string && (string)c.Tag == "keepcolor")) { c.BackColor = c is Form ? p.Background : (c.Parent != null && !(c.Parent is Form) ? c.Parent.BackColor : p.Background); }
                c.ForeColor = p.Text;
                if (c is SplitContainer) { ((SplitContainer)c).BackColor = p.Border; }
                if (first && !(c is Form)) { DarkNative(c); HookScroll(c); } // scroll bars on scrolling panels
                if (first && c is FlowLayoutPanel) FitFlowHeight((FlowLayoutPanel)c);
                else if (!(c is Form)) RedrawAll(c);
            }
            else if (c is TextBox || c is RichTextBox || c is MaskedTextBox)
            {
                c.BackColor = p.SurfaceRaised; c.ForeColor = p.Text;
                var tb = c as TextBoxBase;
                // text boxes inside a NumericUpDown or a grid cell have no border of their own
                bool embedded = c.Parent is UpDownBase || c.Parent is DataGridView || (c.Parent != null && c.Parent.Parent is DataGridView);
                if (tb != null && !embedded && tb.BorderStyle != BorderStyle.None) tb.BorderStyle = BorderStyle.FixedSingle;
                if (first && !embedded && tb != null && tb.BorderStyle == BorderStyle.FixedSingle) HookBorder(c);
                if (first) { DarkNative(c); SystemColorHook.Install(); c.HandleCreated += (s2, e2) => SystemColorHook.Install(); }
                if (first && (c is RichTextBox || (c is TextBox && ((TextBox)c).Multiline))) HookScroll(c);
                RedrawAll(c);
            }
            else if (c is NumericUpDown)
            {
                c.BackColor = p.SurfaceRaised; c.ForeColor = p.Text; ((NumericUpDown)c).BorderStyle = BorderStyle.FixedSingle;
                if (first) HookUpDown((NumericUpDown)c);
                foreach (Control k in c.Controls) { k.BackColor = p.SurfaceRaised; k.ForeColor = p.Text; }
                RedrawAll(c);
                return; // children handled here
            }
            else if (c is ComboBox)
            {
                var cb = (ComboBox)c;
                if (first) { HookComboBox(cb); HookComboList(cb); SystemColorHook.Install(); }
                RedrawAll(cb);
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
            else if (c is ListBox || c is CheckedListBox)
            {
                c.BackColor = p.Surface; c.ForeColor = p.Text; ((ListBox)c).BorderStyle = BorderStyle.None;
                if (first) { HookListBox((ListBox)c); DarkNative(c); HookScroll(c); }
                RedrawAll(c);
            }
            else if (c is ListView) { var lv = (ListView)c; lv.BackColor = p.Surface; lv.ForeColor = p.Text; lv.BorderStyle = BorderStyle.None; if (first) { DarkNative(lv); HookScroll(lv); } else RedrawAll(lv); }
            else if (c is TreeView)
            {
                var tv = (TreeView)c; tv.BackColor = p.Surface; tv.ForeColor = p.Text; tv.BorderStyle = BorderStyle.None; tv.LineColor = p.BorderStrong;
                if (first) { HookTreeView(tv); DarkNative(tv); HookScroll(tv); }
                RedrawAll(tv);
            }
            else if (c is DataGridView) { if (first) HookGrid((DataGridView)c); StyleGrid((DataGridView)c, p); c.Invalidate(); return; }
            else if (c is PropertyGrid) { StylePropertyGrid((PropertyGrid)c, p); return; }
            else if (c is CheckBox || c is RadioButton)
            {
                c.ForeColor = p.Text; c.BackColor = c.Parent?.BackColor ?? p.Background; ((ButtonBase)c).FlatStyle = FlatStyle.Flat;
                if (first) HookCheckBox((ButtonBase)c);
                c.Invalidate();
            }
            else if (c is Button) { var b = (Button)c; b.FlatStyle = FlatStyle.Flat; b.BackColor = p.SurfaceRaised; b.ForeColor = p.Text; b.FlatAppearance.BorderColor = p.BorderStrong; b.FlatAppearance.MouseOverBackColor = p.SurfaceHover; b.FlatAppearance.MouseDownBackColor = p.SurfacePressed; }
            else if (c is Label || c is LinkLabel) { c.ForeColor = c.Tag is string && (string)c.Tag == "dim" ? p.TextDim : p.Text; c.BackColor = Color.Transparent; if (c is LinkLabel) ((LinkLabel)c).LinkColor = p.Accent; }
            else if (c is ScrollBar) { if (first) { DarkNative(c); HookScroll(c); } RedrawAll(c); }
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
            if (FirstHookTree(g)) DarkNativeTree(g); // the scroll bar is a child of the inner grid view
            PropertyGridPainter.Attach(g);
            PropertyGridPainter.ResetBrushes(g);
            g.Refresh();
        }

        /// <summary>
        /// The property grid draws its expand glyphs and its drop-down and ellipsis value buttons with the system theme, and
        /// caches the selection brush when it first paints. This repaints the glyphs and buttons in the palette and drops
        /// the cached selection brush when the palette changes.
        /// </summary>
        private sealed class PropertyGridPainter : NativeWindow
        {
            private const System.Reflection.BindingFlags Any = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<PropertyGrid, object> Attached = new System.Runtime.CompilerServices.ConditionalWeakTable<PropertyGrid, object>();
            private readonly Control _view;

            private PropertyGridPainter(Control view)
            {
                _view = view;
                if (view.IsHandleCreated) AssignHandle(view.Handle);
                view.HandleCreated += (s, e) => AssignHandle(view.Handle);
                view.HandleDestroyed += (s, e) => ReleaseHandle();
            }

            private static Control View(PropertyGrid g)
            {
                foreach (Control k in g.Controls) if (k.GetType().Name == "PropertyGridView") return k;
                return null;
            }

            public static void Attach(PropertyGrid g)
            {
                object o;
                if (Attached.TryGetValue(g, out o)) return;
                var view = View(g);
                if (view == null) return;
                Attached.Add(g, new object());
                new PropertyGridPainter(view);
                try
                {
                    // the value buttons are created on first use; create them now so they can be hooked once
                    foreach (var name in new[] { "DropDownButton", "DialogButton" })
                    {
                        var btn = view.GetType().GetProperty(name, Any).GetValue(view, null) as Button;
                        if (btn == null) continue;
                        bool dialog = name == "DialogButton";
                        new OverPainter(btn, (bg, sz) => PaintValueButton(bg, sz, btn, dialog), Whole);
                    }
                    // the value drop-down list: draw its items and the border of the pop-up that holds it in the palette
                    var list = view.GetType().GetProperty("DropDownListBox", Any).GetValue(view, null) as ListBox;
                    if (list != null)
                    {
                        list.DrawItem += (s, e) => PaintListItem(list, e);
                        // the pop-up already has a border: blend the list's own 3D border (2 px, system colours) into its background
                        if (list.BorderStyle != BorderStyle.None)
                            new OverPainter(list, (lg, lsz) => { using (var pen = new Pen(list.BackColor)) for (int i = 0; i < 2; i++) lg.DrawRectangle(pen, i, i, lsz.Width - 1 - 2 * i, lsz.Height - 1 - 2 * i); });
                        list.VisibleChanged += (s, e) =>
                        {
                            var holder = list.Parent;
                            if (holder == null || !FirstHookTree(holder)) return;
                            new OverPainter(holder, (hg, hsz) => { using (var pen = new Pen(Current.BorderStrong)) hg.DrawRectangle(pen, 0, 0, hsz.Width - 1, hsz.Height - 1); });
                        };
                    }
                }
                catch (Exception ex) { Sidralune.Core.Util.Log.Debug("Theme", "property grid buttons: " + ex.Message); }
            }

            /// <summary>
            /// The selection brush is created once and kept when SelectedItemWithFocusBackColor changes; dropping it makes
            /// the grid create it again from the new colour. The value drop-down list is recoloured as well. (The line and background brushes are renewed by their colour
            /// setters, and the background brush must not be cleared: the grid paints with it without recreating it.)
            /// </summary>
            public static void ResetBrushes(PropertyGrid g)
            {
                try
                {
                    var f = typeof(PropertyGrid).GetField("selectedItemWithFocusBackBrush", Any);
                    if (f != null) f.SetValue(g, null);
                    // the value drop-down list lives in a separate pop-up and keeps the colours it was created with
                    var view = View(g);
                    var list = view == null ? null : view.GetType().GetProperty("DropDownListBox", Any).GetValue(view, null) as ListBox;
                    if (list != null)
                    {
                        list.BackColor = g.ViewBackColor; list.ForeColor = g.ViewForeColor;
                        if (list.Parent != null) list.Parent.BackColor = g.ViewBackColor;
                    }
                }
                catch (Exception ex) { Sidralune.Core.Util.Log.Debug("Theme", "property grid brushes: " + ex.Message); }
            }

            /// <summary>Runs after the grid's own item drawing, which uses system colours for the focus line.</summary>
            private static void PaintListItem(ListBox list, DrawItemEventArgs e)
            {
                if (e.Index < 0 || e.Index >= list.Items.Count) return;
                var p = Current;
                bool sel = (e.State & DrawItemState.Selected) != 0;
                using (var b = new SolidBrush(sel ? p.AccentSoft : list.BackColor)) e.Graphics.FillRectangle(b, e.Bounds);
                var text = new Rectangle(e.Bounds.X + 2, e.Bounds.Y, e.Bounds.Width - 2, e.Bounds.Height);
                TextRenderer.DrawText(e.Graphics, list.GetItemText(list.Items[e.Index]), list.Font, text, p.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }

            private static void PaintValueButton(Graphics g, Size sz, Button btn, bool dialog)
            {
                var p = Current;
                var r = new Rectangle(0, 0, sz.Width, sz.Height);
                bool hot = btn.Enabled && r.Contains(btn.PointToClient(Control.MousePosition));
                bool pressed = hot && (Control.MouseButtons & MouseButtons.Left) != 0;
                using (var b = new SolidBrush(pressed ? p.SurfacePressed : hot ? p.SurfaceHover : p.SurfaceRaised)) g.FillRectangle(b, r);
                using (var pen = new Pen(btn.Focused ? p.Accent : p.BorderStrong)) g.DrawRectangle(pen, 0, 0, sz.Width - 1, sz.Height - 1);
                var col = btn.Enabled ? p.TextDim : p.TextFaint;
                if (!dialog) { DrawChevron(g, r, false, col); return; }
                float d = Math.Max(1.5f, sz.Height / 10f), cy = sz.Height / 2f, cx = sz.Width / 2f;
                using (var b = new SolidBrush(col))
                    for (int i = -1; i <= 1; i++) g.FillEllipse(b, cx + i * d * 2.2f - d / 2, cy + d * 1.2f - d / 2, d, d);
            }

            protected override void WndProc(ref Message m)
            {
                base.WndProc(ref m);
                if (m.Msg == 0x000F) { try { using (var g = _view.CreateGraphics()) PaintGlyphs(g); } catch { } }
                // WM_PRINT / WM_PRINTCLIENT (DrawToBitmap): paint into the supplied device context
                else if ((m.Msg == 0x0317 || m.Msg == 0x0318) && m.WParam != IntPtr.Zero) { try { using (var g = Graphics.FromHdc(m.WParam)) PaintGlyphs(g); } catch { } }
            }

            private void PaintGlyphs(Graphics g)
            {
                var t = _view.GetType();
                int rows = (int)t.GetField("visibleRows", Any).GetValue(_view);
                var fromRow = t.GetMethod("GetGridEntryFromRow", Any);
                var rect = t.GetMethod("GetRectangle", Any);
                var p = Current;
                var grid = (PropertyGrid)_view.Parent;
                for (int r = 0; r <= rows; r++)
                {
                    var entry = fromRow.Invoke(_view, new object[] { r }) as GridItem;
                    if (entry == null || !entry.Expandable) continue;
                    var outline = (Rectangle)entry.GetType().GetProperty("OutlineRect", Any).GetValue(entry, null);
                    if (outline.Width <= 0) continue;
                    var label = (Rectangle)rect.Invoke(_view, new object[] { r, 1 }); // ROWLABEL
                    var box = new Rectangle(outline.X, label.Y - 1 + outline.Y, outline.Width, outline.Height);
                    bool category = entry.GetType().Name == "CategoryGridEntry";
                    // the glyph sits in the left margin (line colour) for categories and top-level properties
                    using (var b = new SolidBrush(category || outline.Right <= label.X + outline.Width + 4 ? grid.LineColor : grid.ViewBackColor))
                        g.FillRectangle(b, box);
                    DrawTreeGlyph(g, box.X + box.Width / 2f, box.Y + box.Height / 2f, Math.Max(2.5f, box.Width * 0.22f), entry.Expanded, p.TextDim);
                }
            }
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

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            // the default check mark is a black bitmap; draw it in the palette
            var r = e.ImageRectangle;
            int s = Math.Min(r.Width, r.Height) - 2;
            var box = new Rectangle(r.X + (r.Width - s) / 2, r.Y + (r.Height - s) / 2, s, s);
            Theme.DrawCheckGlyph(e.Graphics, box, true, e.Item.Enabled);
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
