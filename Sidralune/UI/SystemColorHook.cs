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

        // the delegates must stay referenced for as long as native code may call them
        private static readonly GetSysColorFn ColorHook = HookedGetSysColor;
        private static readonly GetSysColorBrushFn BrushHook = HookedGetSysColorBrush;
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

        /// <summary>Walk the PE import tables of a loaded module and redirect GetSysColor/GetSysColorBrush.</summary>
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
