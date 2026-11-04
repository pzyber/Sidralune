using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;

namespace Sidralune.UI
{
    /// <summary>
    /// Text selection in edit and rich-edit controls is drawn with the system highlight colours (blue/white), which no
    /// control property can change. Those controls live in comctl32 and msftedit/riched20 inside this process and ask
    /// user32 for the colours through their import tables. This redirects GetSysColor and GetSysColorBrush in those
    /// modules only (nothing outside Sidralune is affected) so that COLOR_HIGHLIGHT and COLOR_HIGHLIGHTTEXT follow the
    /// active palette; every other colour is passed through unchanged.
    /// Rich edit asks on every paint, so it follows the palette. comctl32 reads the highlight colours once when it
    /// loads (before this can run), so plain text boxes and combo-box edits still use the system colours.
    /// <para>
    /// The same way, comctl32's AnimateWindow is redirected for combo-box drop-down lists: Windows slides the list open
    /// from a copy of it that starts out white, so the list flashed white on dark palettes. The lists are shown at once
    /// instead, as when combo-box animation is turned off in Windows. comctl32's SetScrollInfo, SetScrollPos and
    /// SetScrollRange are redirected for the windows whose scroll bars the theme paints itself (those lists), so Windows
    /// moves the bar without first drawing it in the system colours.
    /// </para>
    /// </summary>
    internal static class SystemColorHook
    {
        private const int COLOR_HIGHLIGHT = 13, COLOR_HIGHLIGHTTEXT = 14;

        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int GetSysColorFn(int index);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate IntPtr GetSysColorBrushFn(int index);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string name);
        [DllImport("kernel32.dll")] private static extern bool VirtualProtect(IntPtr address, UIntPtr size, uint protect, out uint oldProtect);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateSolidBrush(int color);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
        [DllImport("user32.dll", EntryPoint = "GetSysColor")] private static extern int RealGetSysColor(int index);
        [DllImport("user32.dll", EntryPoint = "GetSysColorBrush")] private static extern IntPtr RealGetSysColorBrush(int index);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int AnimateWindowFn(IntPtr hwnd, int time, int flags);
        [DllImport("user32.dll", EntryPoint = "AnimateWindow")] private static extern int RealAnimateWindow(IntPtr hwnd, int time, int flags);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder name, int max);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int SetScrollInfoFn(IntPtr hwnd, int bar, IntPtr info, int redraw);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int SetScrollPosFn(IntPtr hwnd, int bar, int pos, int redraw);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int SetScrollRangeFn(IntPtr hwnd, int bar, int min, int max, int redraw);
        [DllImport("user32.dll", EntryPoint = "SetScrollInfo")] private static extern int RealSetScrollInfo(IntPtr hwnd, int bar, IntPtr info, int redraw);
        [DllImport("user32.dll", EntryPoint = "SetScrollPos")] private static extern int RealSetScrollPos(IntPtr hwnd, int bar, int pos, int redraw);
        [DllImport("user32.dll", EntryPoint = "SetScrollRange")] private static extern int RealSetScrollRange(IntPtr hwnd, int bar, int min, int max, int redraw);

        /// <summary>Set by the theme: true for a window whose scroll bar the theme paints itself.</summary>
        internal static Func<IntPtr, bool> PaintsScrollBar;

        /// <summary>Set by the theme: paints that window's scroll bar.</summary>
        internal static Action<IntPtr> RepaintScrollBar;

        // the delegates must stay referenced for as long as native code may call them
        private static readonly GetSysColorFn ColorHook = HookedGetSysColor;
        private static readonly GetSysColorBrushFn BrushHook = HookedGetSysColorBrush;
        private static readonly AnimateWindowFn AnimateHook = HookedAnimateWindow;
        private static readonly SetScrollInfoFn ScrollInfoHook = HookedSetScrollInfo;
        private static readonly SetScrollPosFn ScrollPosHook = HookedSetScrollPos;
        private static readonly SetScrollRangeFn ScrollRangeHook = HookedSetScrollRange;

        // A list scrolls by moving its scroll bar, which Windows draws at once, before the list paints its items; on a
        // dark palette the system-coloured bar showed for a frame. For the bars the theme paints, move without drawing.
        private static bool Painted(IntPtr hwnd)
        {
            try { var f = PaintsScrollBar; return f != null && RepaintScrollBar != null && f(hwnd); } catch { return false; }
        }

        private static void Repaint(IntPtr hwnd) { try { RepaintScrollBar(hwnd); } catch { } }

        private static int HookedSetScrollInfo(IntPtr hwnd, int bar, IntPtr info, int redraw)
        {
            if (redraw == 0 || !Painted(hwnd)) return RealSetScrollInfo(hwnd, bar, info, redraw);
            int r = RealSetScrollInfo(hwnd, bar, info, 0);
            Repaint(hwnd);
            return r;
        }

        private static int HookedSetScrollPos(IntPtr hwnd, int bar, int pos, int redraw)
        {
            if (redraw == 0 || !Painted(hwnd)) return RealSetScrollPos(hwnd, bar, pos, redraw);
            int r = RealSetScrollPos(hwnd, bar, pos, 0);
            Repaint(hwnd);
            return r;
        }

        private static int HookedSetScrollRange(IntPtr hwnd, int bar, int min, int max, int redraw)
        {
            if (redraw == 0 || !Painted(hwnd)) return RealSetScrollRange(hwnd, bar, min, max, redraw);
            int r = RealSetScrollRange(hwnd, bar, min, max, 0);
            Repaint(hwnd);
            return r;
        }
        private static readonly HashSet<long> Patched = new HashSet<long>();
        private static readonly string[] Modules = { "comctl32.dll", "gdi32full.dll", "msftedit.dll", "riched20.dll" };
        private static readonly object Gate = new object();
        private static IntPtr _brushHighlight, _brushHighlightText;
        private static int _brushColorHighlight = -1, _brushColorHighlightText = -1;

        public static Color Highlight => Theme.Current.AccentSoft;
        public static Color HighlightText => Theme.Current.Text;

        /// <summary>Patch every loaded copy of the modules that draw edit controls (safe to call repeatedly).</summary>
        public static void Install()
        {
            try
            {
                // GetModuleHandle finds the copy in use (comctl32 v6 under the application manifest); it is cheap,
                // so this can run whenever a text control is created (rich edit loads with the first RichTextBox)
                foreach (var name in Modules)
                {
                    var m = GetModuleHandle(name);
                    if (m == IntPtr.Zero) continue;
                    lock (Gate)
                    {
                        if (!Patched.Add(m.ToInt64())) continue;
                        PatchImports(m);
                    }
                }
            }
            catch (Exception ex) { Sidralune.Core.Util.Log.Debug("Theme", "colour hook: " + ex.Message); }
        }

        private static int HookedGetSysColor(int index)
        {
            if (index == COLOR_HIGHLIGHT) return ColorTranslator.ToWin32(Highlight);
            if (index == COLOR_HIGHLIGHTTEXT) return ColorTranslator.ToWin32(HighlightText);
            return RealGetSysColor(index);
        }

        private static IntPtr HookedGetSysColorBrush(int index)
        {
            if (index != COLOR_HIGHLIGHT && index != COLOR_HIGHLIGHTTEXT) return RealGetSysColorBrush(index);
            lock (Gate)
            {
                // system colour brushes must not be deleted by callers, so keep one per colour and replace it on change
                if (index == COLOR_HIGHLIGHT)
                {
                    int c = ColorTranslator.ToWin32(Highlight);
                    if (c != _brushColorHighlight) { if (_brushHighlight != IntPtr.Zero) DeleteObject(_brushHighlight); _brushHighlight = CreateSolidBrush(c); _brushColorHighlight = c; }
                    return _brushHighlight;
                }
                int t = ColorTranslator.ToWin32(HighlightText);
                if (t != _brushColorHighlightText) { if (_brushHighlightText != IntPtr.Zero) DeleteObject(_brushHighlightText); _brushHighlightText = CreateSolidBrush(t); _brushColorHighlightText = t; }
                return _brushHighlightText;
            }
        }

        /// <summary>True for the drop-down list window of a combo box.</summary>
        internal static bool IsComboList(IntPtr hwnd)
        {
            var name = new System.Text.StringBuilder(32);
            return hwnd != IntPtr.Zero && GetClassName(hwnd, name, name.Capacity) > 0 && name.ToString() == "ComboLBox";
        }

        private static int HookedAnimateWindow(IntPtr hwnd, int time, int flags)
        {
            try
            {
                if (!IsComboList(hwnd)) return RealAnimateWindow(hwnd, time, flags);
                const int AW_HIDE = 0x10000, AW_ACTIVATE = 0x20000;
                // SWP_NOSIZE | SWP_NOMOVE | SWP_NOZORDER, plus SWP_HIDEWINDOW or SWP_SHOWWINDOW (and SWP_NOACTIVATE)
                uint swp = 0x0001 | 0x0002 | 0x0004;
                if ((flags & AW_HIDE) != 0) swp |= 0x0080;
                else swp |= 0x0040 | ((flags & AW_ACTIVATE) != 0 ? 0u : 0x0010u);
                return SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, swp) ? 1 : 0;
            }
            catch { return RealAnimateWindow(hwnd, time, flags); }
        }

        /// <summary>Walk the PE import tables of a loaded module and redirect GetSysColor/GetSysColorBrush/AnimateWindow.</summary>
        private static void PatchImports(IntPtr module)
        {
            long b = module.ToInt64();
            int peOffset = Marshal.ReadInt32(module, 0x3C);
            long optional = b + peOffset + 24;
            ushort magic = (ushort)Marshal.ReadInt16(new IntPtr(optional));
            bool pe64 = magic == 0x20B;
            long directories = optional + (pe64 ? 112 : 96);
            // regular imports (directory 1, 20-byte descriptors) and delay-load imports (directory 13, 32-byte
            // descriptors; rich edit pulls GetSysColor in that way). Overwriting a delay-load slot before or after
            // it is resolved both work: the loader's resolver only runs through the slot.
            PatchTable(b, Marshal.ReadInt32(new IntPtr(directories + 1 * 8)), 20, 0, 16, pe64);
            PatchTable(b, Marshal.ReadInt32(new IntPtr(directories + 13 * 8)), 32, 16, 12, pe64);
        }

        private static void PatchTable(long b, int tableRva, int size, int namesAt, int slotsAt, bool pe64)
        {
            if (tableRva == 0) return;
            int ptr = pe64 ? 8 : 4;
            var colorFn = Marshal.GetFunctionPointerForDelegate(ColorHook);
            var brushFn = Marshal.GetFunctionPointerForDelegate(BrushHook);
            for (long desc = b + tableRva; ; desc += size)
            {
                int originalThunk = Marshal.ReadInt32(new IntPtr(desc + namesAt));
                int firstThunk = Marshal.ReadInt32(new IntPtr(desc + slotsAt));
                if (originalThunk == 0 && firstThunk == 0) break;
                if (originalThunk == 0 || firstThunk == 0) continue; // no name table: nothing to match by name
                for (int i = 0; ; i++)
                {
                    long entry = pe64 ? Marshal.ReadInt64(new IntPtr(b + originalThunk + (long)i * ptr)) : (uint)Marshal.ReadInt32(new IntPtr(b + originalThunk + (long)i * ptr));
                    if (entry == 0) break;
                    bool ordinal = pe64 ? (entry & unchecked((long)0x8000000000000000)) != 0 : (entry & 0x80000000L) != 0;
                    if (ordinal) continue;
                    string fn = Marshal.PtrToStringAnsi(new IntPtr(b + (entry & 0x7FFFFFFF) + 2));
                    IntPtr replacement;
                    if (fn == "GetSysColor") replacement = colorFn;
                    else if (fn == "GetSysColorBrush") replacement = brushFn;
                    else if (fn == "AnimateWindow") replacement = Marshal.GetFunctionPointerForDelegate(AnimateHook);
                    else if (fn == "SetScrollInfo") replacement = Marshal.GetFunctionPointerForDelegate(ScrollInfoHook);
                    else if (fn == "SetScrollPos") replacement = Marshal.GetFunctionPointerForDelegate(ScrollPosHook);
                    else if (fn == "SetScrollRange") replacement = Marshal.GetFunctionPointerForDelegate(ScrollRangeHook);
                    else continue;
                    var slot = new IntPtr(b + firstThunk + (long)i * ptr);
                    uint old;
                    if (!VirtualProtect(slot, (UIntPtr)(uint)ptr, 0x04 /* PAGE_READWRITE */, out old)) continue;
                    Marshal.WriteIntPtr(slot, replacement);
                    VirtualProtect(slot, (UIntPtr)(uint)ptr, old, out old);
                }
            }
        }
    }
}
