using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Sidralune.UI
{
    /// <summary>
    /// Themed scroll bars. Windows draws scroll bars itself, including hover animations on timers and a private drawing
    /// loop while the thumb is dragged, so painting over them always loses some of the time. Instead, each native
    /// scroll bar gets a themed bar of our own placed exactly on top of it (as a child of the form, so the native bar
    /// underneath is clipped away). Our bar handles the mouse and drives the control's scrolling; the native bar keeps
    /// providing the range, page and position, which are read back after every message.
    /// </summary>
    internal static class ScrollOverlays
    {
        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct SCROLLBARINFO
        {
            public int cbSize;
            public RECT rcScrollBar;
            public int dxyLineButton, xyThumbTop, xyThumbBottom, reserved;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)] public int[] rgstate;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SCROLLINFO { public int cbSize, fMask, nMin, nMax, nPage, nPos, nTrackPos; }

        [DllImport("user32.dll")] private static extern bool GetScrollBarInfo(IntPtr hwnd, int idObject, ref SCROLLBARINFO psbi);
        [DllImport("user32.dll")] private static extern bool GetScrollInfo(IntPtr hwnd, int bar, ref SCROLLINFO si);
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);

        private const int OBJID_HSCROLL = unchecked((int)0xFFFFFFFA), OBJID_VSCROLL = unchecked((int)0xFFFFFFFB);
        private const int SB_HORZ = 0, SB_VERT = 1, SIF_ALL = 0x17;
        private const int WM_HSCROLL = 0x0114, WM_VSCROLL = 0x0115, WM_MOUSEWHEEL = 0x020A;
        private const int SB_LINEUP = 0, SB_LINEDOWN = 1, SB_PAGEUP = 2, SB_PAGEDOWN = 3, SB_THUMBPOSITION = 4, SB_THUMBTRACK = 5, SB_ENDSCROLL = 8;
        private const int STATE_INVISIBLE = 0x8000, STATE_UNAVAILABLE = 0x0001;

        private static readonly List<Binding> Bindings = new List<Binding>();
        private static Timer _timer;
        private static readonly PropertyInfo VerticalOffsetProperty = typeof(DataGridView).GetProperty("VerticalOffset", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        private static readonly MethodInfo OnScrollMethod = typeof(ScrollBar).GetMethod("OnScroll", BindingFlags.Instance | BindingFlags.NonPublic);

        /// <summary>Cover the scroll bars of a control (window scroll bars) or a ScrollBar control with themed ones.</summary>
        public static void Attach(Control target)
        {
            Bindings.Add(new Binding(target));
            if (_timer == null)
            {
                // fallback for layout changes of ancestors that send no message to the control itself
                _timer = new Timer { Interval = 100 };
                _timer.Tick += (s, e) => SyncAll();
                _timer.Start();
            }
        }

        public static void SyncAll()
        {
            for (int i = Bindings.Count - 1; i >= 0; i--)
            {
                var b = Bindings[i];
                if (b.Dead) { b.Dispose(); Bindings.RemoveAt(i); }
                else b.Sync();
            }
        }

        // ------------------------------------------------------------------ binding between a control and its overlays

        private sealed class Binding : NativeWindow
        {
            public readonly Control Target;
            private readonly ScrollBar _sb;
            private readonly Bar _v, _h;
            private readonly Panel _corner;
            private Form _host;
            private bool _busy, _adding;
            public bool Dead;

            public Binding(Control target)
            {
                Target = target;
                _sb = target as ScrollBar;
                _v = new Bar(this, true); _h = new Bar(this, false);
                _corner = new Panel { Visible = false };
                if (target.IsHandleCreated) AssignHandle(target.Handle);
                target.HandleCreated += (s, e) => { AssignHandle(target.Handle); Sync(); };
                target.HandleDestroyed += (s, e) => { ReleaseHandle(); HideAll(); };
                target.Disposed += (s, e) => Dead = true;
                target.VisibleChanged += (s, e) => Sync();
                target.SizeChanged += (s, e) => Sync();
                target.LocationChanged += (s, e) => Sync();
                target.ParentChanged += (s, e) => Sync();
            }

            public void Dispose() { HideAll(); _v.Dispose(); _h.Dispose(); _corner.Dispose(); }

            protected override void WndProc(ref Message m)
            {
                base.WndProc(ref m);
                // 0x00EB = SBM_GETSCROLLBARINFO (our own queries), 0x0082 = WM_NCDESTROY
                if (_busy || m.Msg == 0x00EB || m.Msg == 0x0082) return;
                Sync();
            }

            private void HideAll() { _v.Visible = false; _h.Visible = false; _corner.Visible = false; }

            public void Sync()
            {
                if (_busy || Dead) return;
                _busy = true;
                try { SyncCore(); }
                catch (Exception ex) { Sidralune.Core.Util.Log.Debug("Scroll", ex.Message); }
                finally { _busy = false; }
            }

            private void SyncCore()
            {
                var form = Target.FindForm();
                if (form == null || !Target.IsHandleCreated || form.IsDisposed || !IsWindowVisible(Target.Handle)) { HideAll(); return; }
                if (_host != form)
                {
                    // Add the overlays to the form later: Sync can run while something is enumerating the form's
                    // controls (theme application, layout), and changing the collection then would throw.
                    HideAll();
                    if (!_adding && form.IsHandleCreated)
                    {
                        _adding = true;
                        form.BeginInvoke(new Action(() =>
                        {
                            _adding = false;
                            if (Dead || form.IsDisposed) return;
                            if (_host != null && !_host.IsDisposed) { _host.Controls.Remove(_v); _host.Controls.Remove(_h); _host.Controls.Remove(_corner); }
                            form.Controls.Add(_corner); form.Controls.Add(_h); form.Controls.Add(_v);
                            _corner.BringToFront(); _h.BringToFront(); _v.BringToFront();
                            _host = form;
                            Sync();
                        }));
                    }
                    return;
                }
                var clip = VisibleArea(_sb != null ? _sb.Parent : Target.Parent);
                var p = Theme.Current;
                if (_corner.BackColor != p.Surface) _corner.BackColor = p.Surface;

                if (_sb != null)
                {
                    var screen = _sb.RectangleToScreen(_sb.ClientRectangle);
                    var bar = _sb is VScrollBar ? _v : _h;
                    (bar == _v ? _h : _v).Visible = false;
                    _corner.Visible = false;
                    bar.Update(ToForm(screen), ToForm(Rectangle.Intersect(screen, clip)), _sb.Minimum, _sb.Maximum, _sb.LargeChange, _sb.Value, _sb.Enabled);
                    return;
                }

                Rectangle vr, hr;
                bool vOn = WindowBar(OBJID_VSCROLL, SB_VERT, _v, clip, out vr);
                bool hOn = WindowBar(OBJID_HSCROLL, SB_HORZ, _h, clip, out hr);
                if (vOn && hOn)
                {
                    var corner = Rectangle.FromLTRB(vr.Left, hr.Top, vr.Right, hr.Bottom);
                    var c = ToForm(Rectangle.Intersect(corner, clip));
                    if (c.Width > 0 && c.Height > 0) { _corner.Bounds = c; _corner.Visible = true; } else _corner.Visible = false;
                }
                else _corner.Visible = false;
            }

            private bool WindowBar(int objId, int bar, Bar overlay, Rectangle clip, out Rectangle screen)
            {
                screen = Rectangle.Empty;
                var info = new SCROLLBARINFO { cbSize = Marshal.SizeOf(typeof(SCROLLBARINFO)), rgstate = new int[6] };
                if (!GetScrollBarInfo(Target.Handle, objId, ref info) || (info.rgstate[0] & STATE_INVISIBLE) != 0) { overlay.Visible = false; return false; }
                screen = Rectangle.FromLTRB(info.rcScrollBar.Left, info.rcScrollBar.Top, info.rcScrollBar.Right, info.rcScrollBar.Bottom);
                if (screen.Width <= 0 || screen.Height <= 0) { overlay.Visible = false; return false; }
                var si = new SCROLLINFO { cbSize = Marshal.SizeOf(typeof(SCROLLINFO)), fMask = SIF_ALL };
                GetScrollInfo(Target.Handle, bar, ref si);
                bool enabled = (info.rgstate[0] & STATE_UNAVAILABLE) == 0 && Target.Enabled;
                overlay.Update(ToForm(screen), ToForm(Rectangle.Intersect(screen, clip)), si.nMin, si.nMax, Math.Max(1, si.nPage), si.nPos, enabled);
                return true;
            }

            /// <summary>Screen rectangle in which the control can be seen: the intersection of its ancestors' client areas.</summary>
            private static Rectangle VisibleArea(Control from)
            {
                var r = new Rectangle(-100000, -100000, 200000, 200000);
                for (var c = from; c != null; c = c.Parent)
                {
                    if (!c.IsHandleCreated) break;
                    r = Rectangle.Intersect(r, c.RectangleToScreen(c.ClientRectangle));
                    if (c is Form) break;
                }
                return r;
            }

            private Rectangle ToForm(Rectangle screen)
            {
                if (screen.Width <= 0 || screen.Height <= 0) return Rectangle.Empty;
                var tl = _host.PointToClient(screen.Location);
                return new Rectangle(tl, screen.Size);
            }

            // -------------------------------------------------------------- scrolling actions from the overlays

            public void Line(bool vertical, int dir) => Step(vertical, dir < 0 ? SB_LINEUP : SB_LINEDOWN, dir < 0 ? ScrollEventType.SmallDecrement : ScrollEventType.SmallIncrement, dir * (_sb != null ? _sb.SmallChange : 0));
            public void Page(bool vertical, int dir) => Step(vertical, dir < 0 ? SB_PAGEUP : SB_PAGEDOWN, dir < 0 ? ScrollEventType.LargeDecrement : ScrollEventType.LargeIncrement, dir * (_sb != null ? _sb.LargeChange : 0));

            private void Step(bool vertical, int code, ScrollEventType type, int delta)
            {
                if (_sb != null) ApplyControl(type, _sb.Value + delta);
                else SendMessage(Target.Handle, vertical ? WM_VSCROLL : WM_HSCROLL, (IntPtr)code, IntPtr.Zero);
                Sync();
            }

            public void Thumb(bool vertical, int pos, bool final)
            {
                if (_sb != null && _sb.Parent is DataGridView)
                {
                    // grids ignore thumb notifications raised this way; set their scroll offset directly
                    var grid = (DataGridView)_sb.Parent;
                    try
                    {
                        if (vertical) VerticalOffsetProperty?.SetValue(grid, Math.Max(0, pos), null);
                        else grid.HorizontalScrollingOffset = Math.Max(0, pos);
                    }
                    catch (Exception ex) { Sidralune.Core.Util.Log.Debug("Scroll", (ex.InnerException ?? ex).Message); }
                }
                else if (_sb != null)
                {
                    ApplyControl(final ? ScrollEventType.ThumbPosition : ScrollEventType.ThumbTrack, pos);
                    if (final) ApplyControl(ScrollEventType.EndScroll, _sb.Value);
                }
                else if (Target is ScrollableControl && !(Target is UpDownBase))
                {
                    // WinForms panels read the track position from the native bar, so set the position directly
                    var sc = (ScrollableControl)Target;
                    var cur = sc.AutoScrollPosition;
                    sc.AutoScrollPosition = vertical ? new Point(-cur.X, pos) : new Point(pos, -cur.Y);
                }
                else
                {
                    int msg = vertical ? WM_VSCROLL : WM_HSCROLL;
                    int p16 = Math.Max(0, Math.Min(0xFFFF, pos));
                    SendMessage(Target.Handle, msg, (IntPtr)((p16 << 16) | (final ? SB_THUMBPOSITION : SB_THUMBTRACK)), IntPtr.Zero);
                    if (final) SendMessage(Target.Handle, msg, (IntPtr)SB_ENDSCROLL, IntPtr.Zero);
                }
                Sync();
            }

            public void Wheel(int delta, Point screen)
            {
                var receiver = _sb != null ? _sb.Parent : Target;
                if (receiver == null || !receiver.IsHandleCreated) return;
                SendMessage(receiver.Handle, WM_MOUSEWHEEL, (IntPtr)(delta << 16), (IntPtr)((screen.Y << 16) | (screen.X & 0xFFFF)));
                Sync();
            }

            /// <summary>Scroll a ScrollBar control the way a native notification would: raise Scroll, then set Value.</summary>
            private void ApplyControl(ScrollEventType type, int value)
            {
                int max = Math.Max(_sb.Minimum, _sb.Maximum - _sb.LargeChange + 1);
                int nv = Math.Max(_sb.Minimum, Math.Min(max, value));
                var se = new ScrollEventArgs(type, _sb.Value, nv, _sb is VScrollBar ? ScrollOrientation.VerticalScroll : ScrollOrientation.HorizontalScroll);
                try
                {
                    OnScrollMethod?.Invoke(_sb, new object[] { se });
                    if (_sb.Value != se.NewValue) _sb.Value = Math.Max(_sb.Minimum, Math.Min(_sb.Maximum, se.NewValue));
                }
                catch (Exception ex) { Sidralune.Core.Util.Log.Debug("Scroll", (ex.InnerException ?? ex).Message); } // e.g. a grid emptied meanwhile
            }
        }

        // ------------------------------------------------------------------ the themed bar

        private enum Part { None, Arrow1, Track1, Thumb, Track2, Arrow2 }

        private sealed class Bar : Control
        {
            private readonly Binding _owner;
            private readonly bool _vertical;
            private int _min, _max = 100, _page = 10, _pos;
            private bool _enabled = true;
            private Point _offset;     // full bar origin relative to our (possibly clipped) bounds
            private Size _full;
            private Part _hot, _pressed;
            private Point _dragMouse; private int _dragPos, _trackPos = int.MinValue;
            private readonly Timer _repeat = new Timer();

            public Bar(Binding owner, bool vertical)
            {
                _owner = owner; _vertical = vertical;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
                SetStyle(ControlStyles.Selectable, false);
                TabStop = false; Visible = false;
                _repeat.Tick += (s, e) => Repeat();
            }

            protected override void Dispose(bool disposing) { if (disposing) _repeat.Dispose(); base.Dispose(disposing); }

            public void Update(Rectangle full, Rectangle clipped, int min, int max, int page, int pos, bool enabled)
            {
                if (full.Width <= 0 || full.Height <= 0 || clipped.Width <= 0 || clipped.Height <= 0) { Visible = false; return; }
                bool changed = min != _min || max != _max || page != _page || pos != _pos || enabled != _enabled || full.Size != _full;
                _min = min; _max = max; _page = page; _pos = pos; _enabled = enabled; _full = full.Size;
                var offset = new Point(full.X - clipped.X, full.Y - clipped.Y);
                if (offset != _offset) { _offset = offset; changed = true; }
                if (Bounds != clipped) Bounds = clipped;
                if (!Visible) Visible = true;
                if (changed) Invalidate();
            }

            // geometry in full-bar coordinates
            private int Length => _vertical ? _full.Height : _full.Width;
            private int Thickness => _vertical ? _full.Width : _full.Height;
            private int ArrowSize => Math.Min(Thickness, Length / 2);
            private int MaxPos => Math.Max(_min, _max - _page + 1);

            private void Thumb(out int start, out int length)
            {
                int track = Length - 2 * ArrowSize;
                int range = _max - _min + 1;
                if (!_enabled || range <= _page || track <= 0) { start = ArrowSize; length = 0; return; }
                length = Math.Max(Math.Min(track, (int)Math.Round(8 * DeviceDpi / 96.0) + Thickness / 2), (int)((long)track * _page / range));
                int pos = _trackPos != int.MinValue ? _trackPos : _pos;
                start = ArrowSize + (MaxPos > _min ? (int)((long)(track - length) * (pos - _min) / (MaxPos - _min)) : 0);
            }

            private Part HitTest(Point client)
            {
                var pt = new Point(client.X - _offset.X, client.Y - _offset.Y);
                int v = _vertical ? pt.Y : pt.X;
                if (v < ArrowSize) return Part.Arrow1;
                if (v >= Length - ArrowSize) return Part.Arrow2;
                int ts, tl; Thumb(out ts, out tl);
                if (tl <= 0) return Part.None;
                if (v < ts) return Part.Track1;
                if (v < ts + tl) return Part.Thumb;
                return Part.Track2;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var p = Theme.Current; var g = e.Graphics;
                g.Clear(p.Surface);
                g.TranslateTransform(_offset.X, _offset.Y);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                int a = ArrowSize, L = Length, T = Thickness;
                var a1 = _vertical ? new Rectangle(0, 0, T, a) : new Rectangle(0, 0, a, T);
                var a2 = _vertical ? new Rectangle(0, L - a, T, a) : new Rectangle(L - a, 0, a, T);
                DrawArrow(g, a1, true, Part.Arrow1);
                DrawArrow(g, a2, false, Part.Arrow2);
                int ts, tl; Thumb(out ts, out tl);
                if (tl > 0)
                {
                    var col = _pressed == Part.Thumb ? p.TextDim : _hot == Part.Thumb ? Theme.Blend(p.TextFaint, p.TextDim, 0.5) : p.TextFaint;
                    int pad = Math.Max(2, T / 4);
                    var r = _vertical ? new RectangleF(pad, ts + 1, T - 2 * pad, tl - 2) : new RectangleF(ts + 1, pad, tl - 2, T - 2 * pad);
                    if (r.Width > 0 && r.Height > 0)
                        using (var path = Rounded(r, Math.Min(r.Width, r.Height) / 2f))
                        using (var b = new SolidBrush(col)) g.FillPath(b, path);
                }
            }

            private void DrawArrow(Graphics g, Rectangle r, bool first, Part part)
            {
                if (r.Width <= 0 || r.Height <= 0) return;
                var p = Theme.Current;
                if (_enabled && (_pressed == part || _hot == part))
                    using (var b = new SolidBrush(_pressed == part ? p.SurfacePressed : p.SurfaceHover)) g.FillRectangle(b, r);
                var col = !_enabled ? p.TextFaint : (_pressed == part || _hot == part ? p.Text : p.TextDim);
                float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f, s = Math.Max(2.5f, Math.Min(r.Width, r.Height) / 5f);
                using (var pen = new Pen(col, 1.4f))
                {
                    if (_vertical)
                    {
                        if (first) g.DrawLines(pen, new[] { new PointF(cx - s, cy + s / 2), new PointF(cx, cy - s / 2), new PointF(cx + s, cy + s / 2) });
                        else g.DrawLines(pen, new[] { new PointF(cx - s, cy - s / 2), new PointF(cx, cy + s / 2), new PointF(cx + s, cy - s / 2) });
                    }
                    else
                    {
                        if (first) g.DrawLines(pen, new[] { new PointF(cx + s / 2, cy - s), new PointF(cx - s / 2, cy), new PointF(cx + s / 2, cy + s) });
                        else g.DrawLines(pen, new[] { new PointF(cx - s / 2, cy - s), new PointF(cx + s / 2, cy), new PointF(cx - s / 2, cy + s) });
                    }
                }
            }

            private static GraphicsPath Rounded(RectangleF r, float rad)
            {
                var path = new GraphicsPath();
                float d = Math.Max(0.5f, Math.Min(rad * 2, Math.Min(r.Width, r.Height)));
                path.AddArc(r.X, r.Y, d, d, 180, 90);
                path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
                path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
                path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
                path.CloseFigure();
                return path;
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                if (_pressed == Part.Thumb)
                {
                    int ts, tl; Thumb(out ts, out tl);
                    int track = Length - 2 * ArrowSize - tl;
                    int delta = _vertical ? e.Y - _dragMouse.Y : e.X - _dragMouse.X;
                    int pos = track > 0 ? _dragPos + (int)Math.Round((double)delta * (MaxPos - _min) / track) : _dragPos;
                    pos = Math.Max(_min, Math.Min(MaxPos, pos));
                    if (pos != _trackPos) { _trackPos = pos; Invalidate(); _owner.Thumb(_vertical, pos, false); }
                }
                else
                {
                    var hot = _enabled ? HitTest(e.Location) : Part.None;
                    if (hot != _hot) { _hot = hot; Invalidate(); }
                }
                base.OnMouseMove(e);
            }

            protected override void OnMouseLeave(EventArgs e) { if (_hot != Part.None) { _hot = Part.None; Invalidate(); } base.OnMouseLeave(e); }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Left || !_enabled) return;
                _pressed = HitTest(e.Location);
                Capture = true;
                if (_pressed == Part.Thumb) { _dragMouse = e.Location; _dragPos = _pos; _trackPos = _pos; }
                else if (_pressed != Part.None) { Act(); _repeat.Interval = 400; _repeat.Start(); }
                Invalidate();
                base.OnMouseDown(e);
            }

            protected override void OnMouseUp(MouseEventArgs e)
            {
                _repeat.Stop();
                if (_pressed == Part.Thumb && _trackPos != int.MinValue) _owner.Thumb(_vertical, _trackPos, true);
                _pressed = Part.None; _trackPos = int.MinValue; Capture = false;
                _hot = ClientRectangle.Contains(e.Location) ? HitTest(e.Location) : Part.None;
                Invalidate();
                base.OnMouseUp(e);
            }

            protected override void OnMouseWheel(MouseEventArgs e) { _owner.Wheel(e.Delta, PointToScreen(e.Location)); base.OnMouseWheel(e); }

            private void Repeat()
            {
                _repeat.Interval = 50;
                var under = HitTest(PointToClient(Cursor.Position));
                if (under == _pressed) Act();
            }

            private void Act()
            {
                switch (_pressed)
                {
                    case Part.Arrow1: _owner.Line(_vertical, -1); break;
                    case Part.Arrow2: _owner.Line(_vertical, 1); break;
                    case Part.Track1: _owner.Page(_vertical, -1); break;
                    case Part.Track2: _owner.Page(_vertical, 1); break;
                }
            }
        }
    }
}
