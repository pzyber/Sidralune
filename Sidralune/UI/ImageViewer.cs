using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Sidralune.Core.Mathematics;
using Sidralune.Imaging;
using Sidralune.Imaging.Analysis;
using Sidralune.Imaging.ColorManagement;
using Sidralune.Imaging.IO;
using Sidralune.Science.Astrometry;

namespace Sidralune.UI
{
    public enum DisplayStretch { None, AutoLinked, AutoUnlinked, Custom }

    /// <summary>Zoomable/pannable viewer for AstroImage with screen stretch, readout and overlay painting.</summary>
    public class ImageViewer : Control, IThemed
    {
        private AstroImage _image;
        private Bitmap _bitmap;
        private double _zoom = 1;
        private PointF _offset;          // image coordinate shown at the top-left of the control
        private Point _dragStart;
        private PointF _dragOffset;
        private bool _dragging;
        private DisplayStretch _stretch = DisplayStretch.AutoLinked;
        private HistogramTransform[] _stf;
        public HistogramTransform[] CustomStretch;
        public Wcs Wcs;
        public bool ShowWcsGrid;
        public bool Invert;
        public bool FalseColor;
        public string EmptyText = Sidralune.Core.Util.Lang.T("Open an image (Ctrl+O) or drop files here");
        /// <summary>Overlay painter in image coordinates: (graphics, imageToScreen).</summary>
        public event Action<Graphics, Func<double, double, PointF>> PaintOverlay;
        public event Action<double, double, MouseEventArgs> ImageClick;
        public event Action<string> ReadoutChanged;
        public event Action<string[]> FilesDropped;
        public Func<double, double, string> ExtraReadout;
        private int _renderVersion;

        public ImageViewer()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            AllowDrop = true;
            TabStop = true;
        }

        public void ApplyTheme(Palette p) { if (_image != null && _bitmapNight != p.IsNight) Rebuild(); else Invalidate(); }
        private bool _bitmapNight;

        public AstroImage Image => _image;
        public double Zoom => _zoom;

        public DisplayStretch Stretch
        {
            get { return _stretch; }
            set { _stretch = value; Rebuild(); }
        }

        public void SetImage(AstroImage img, bool fit = true)
        {
            bool sizeChanged = _image == null || img == null || img.Width != _image.Width || img.Height != _image.Height;
            _image = img;
            if (img != null)
            {
                var w = Wcs.FromHeader(img.Header, img.Width, img.Height);
                Wcs = w;
            }
            else Wcs = null;
            if (fit && sizeChanged) FitToWindow();
            Rebuild();
        }

        /// <summary>The monitor (device name) the current bitmap was converted for.</summary>
        private string _renderedFor;

        /// <summary>Show the image as proofed in the proofing profile (Settings → Colour). Ctrl+Shift+O.</summary>
        public bool Proof { get { return _proof; } set { if (_proof != value) { _proof = value; ProofingChanged?.Invoke(); Rebuild(); } } }
        private bool _proof;

        /// <summary>Mark pixels the monitor (or, when proofing, the proofing profile) cannot show. Ctrl+Shift+Y.</summary>
        public bool GamutWarning { get { return _gamut; } set { if (_gamut != value) { _gamut = value; ProofingChanged?.Invoke(); Rebuild(); } } }
        private bool _gamut;

        /// <summary>Proof or GamutWarning was switched (also from the keyboard).</summary>
        public event Action ProofingChanged;

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ColorSettings.Changed += OnColorSettingsChanged;
            HookForm();
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            ColorSettings.Changed -= OnColorSettingsChanged;
            base.OnHandleDestroyed(e);
        }

        protected override void OnParentChanged(EventArgs e) { base.OnParentChanged(e); HookForm(); }

        private Form _hookedForm;
        private void HookForm()
        {
            var f = FindForm();
            if (f == _hookedForm) return;
            if (_hookedForm != null) _hookedForm.Move -= OnFormMoved;
            _hookedForm = f;
            if (f != null) f.Move += OnFormMoved;
        }

        // moved onto another monitor: convert for that monitor's profile
        private void OnFormMoved(object s, EventArgs e)
        {
            if (_image == null || _renderedFor == null || !IsHandleCreated) return;
            string device;
            try { device = MonitorColor.Override != null ? "override" : Screen.FromControl(this).DeviceName; } catch { return; }
            if (device != _renderedFor) Rebuild();
        }

        private void OnColorSettingsChanged()
        {
            if (!IsHandleCreated || IsDisposed) return;
            try { BeginInvoke(new Action(() => { if (_image != null) Rebuild(); })); } catch (InvalidOperationException) { }
        }

        /// <summary>Recompute display bitmap (async for big images).</summary>
        public void Rebuild()
        {
            var img = _image;
            if (img == null) { _bitmap?.Dispose(); _bitmap = null; Invalidate(); return; }
            int version = ++_renderVersion;
            var stretch = _stretch;
            var custom = CustomStretch;
            bool inv = Invert, fc = FalseColor, night = Theme.Current.IsNight;
            // colour management: from the image's profile to the monitor's (not for false colour, nor in night vision mode)
            bool managed = ColorSettings.Enabled && !night && !(fc && img.Channels == 1);
            var monitor = managed ? MonitorColor.For(this) : null;
            var render = managed ? new ManagedRender
            {
                Source = img.EffectiveProfile, Target = monitor.Profile, Intent = ColorSettings.Intent,
                Proofing = _proof ? ColorSettings.ProofingProfile : null, ProofingIntent = ColorSettings.ProofingIntent,
                Gamut = _gamut, WarningColor = ColorSettings.GamutWarningColor
            } : null;
            string device = monitor?.Device;
            Task.Run(() =>
            {
                HistogramTransform[] t = null;
                if (stretch == DisplayStretch.AutoLinked) t = StretchOps.AutoStretch(img, true);
                else if (stretch == DisplayStretch.AutoUnlinked) t = StretchOps.AutoStretch(img, false);
                else if (stretch == DisplayStretch.Custom) t = custom;
                var luts = t != null ? new float[t.Length][] : null;
                if (t != null) for (int i = 0; i < t.Length; i++) luts[i] = t[i].Lut(4096);
                Func<int, float, float> tf = (c, v) =>
                {
                    float r = v;
                    if (luts != null)
                    {
                        var l = luts[Math.Min(c, luts.Length - 1)];
                        int k = (int)(Math.Max(0, Math.Min(1, v)) * 4095);
                        r = l[k];
                    }
                    if (inv) r = 1 - r;
                    return r;
                };
                Bitmap bmp = null;
                if (render != null)
                {
                    try { bmp = render.Draw(img, tf); }
                    catch (Exception ex) { LogOnce("Colour management is off for this image: " + ex.Message); }
                }
                if (bmp == null) bmp = fc && img.Channels == 1 ? FalseColorBitmap(img, tf) : BitmapIO.ToBitmap(img, tf);
                if (night) NightTint(bmp);
                if (IsDisposed) { bmp.Dispose(); return; }
                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        if (version != _renderVersion) { bmp.Dispose(); return; }
                        _bitmap?.Dispose();
                        _bitmap = bmp;
                        _stf = t;
                        _bitmapNight = night;
                        _renderedFor = device;
                        Invalidate();
                    }));
                }
                catch { bmp.Dispose(); }
            });
        }

        private static string _lastLogged;
        private static void LogOnce(string message)
        {
            if (message == _lastLogged) return;
            _lastLogged = message;
            Sidralune.Core.Util.Log.Warn("Colour", message);
        }

        /// <summary>
        /// Draws an image converted from its profile to the monitor's (optionally through a proofing profile, and marking
        /// colours out of gamut), with Windows' colour system. The stretched values go in at 16 bits per channel.
        /// </summary>
        private sealed class ManagedRender
        {
            public IccProfile Source, Target, Proofing;
            public RenderingIntent Intent, ProofingIntent;
            public bool Gamut;
            public int WarningColor;

            public unsafe Bitmap Draw(AstroImage img, Func<int, float, float> tf)
            {
                int w = img.Width, h = img.Height;
                bool mono = img.Channels < 3;
                // a grey image goes in as linear light (whatever its curve) and is converted as neutral linear sRGB
                var src = mono ? IccProfiles.ById("srgb-linear") : Source;
                var grayCurve = mono && Source.IsGray && Source.Curves != null ? Source.Curves[0] : ToneCurve.Linear;
                // Proofing goes in two real steps, into the proofing profile's own values (where what it cannot show is
                // lost) and from those to the monitor; one chained transform does not confine colours to the middle
                // profile. The second step is colorimetric, so the proof is shown as it is.
                var midFormat = Proofing != null ? WcsTransform.FormatFor(Proofing) : WcsTransform.Format.Rgb16;
                var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                try
                {
                    // strips converted in parallel, each worker with its own transform
                    int workers = Math.Max(1, Math.Min(Environment.ProcessorCount, h / 64 + 1));
                    int rowsEach = (h + workers - 1) / workers;
                    var warn = System.Drawing.Color.FromArgb(WarningColor);
                    Parallel.For(0, workers, k =>
                    {
                        int y0 = k * rowsEach, y1 = Math.Min(h, y0 + rowsEach);
                        if (y0 >= y1) return;
                        using (var t = Proofing == null ? new WcsTransform(new[] { src, Target }, new[] { Intent, Intent }, false)
                                                         : new WcsTransform(new[] { src, Proofing }, new[] { ProofingIntent, ProofingIntent }, false))
                        using (var t2 = Proofing != null ? new WcsTransform(new[] { Proofing, Target }, new[] { RenderingIntent.RelativeColorimetric, RenderingIntent.RelativeColorimetric }, false) : null)
                        using (var g = Gamut ? new WcsTransform(Proofing != null ? new[] { src, Proofing } : new[] { src, Target }, new[] { ProofingIntent, ProofingIntent }, true) : null)
                        {
                            const int strip = 32;
                            var buf = new ushort[w * strip * 3];
                            for (int y = y0; y < y1; y += strip)
                            {
                                int rows = Math.Min(strip, y1 - y);
                                for (int r = 0; r < rows; r++)
                                {
                                    int o = (y + r) * w, b = r * w * 3;
                                    for (int x = 0; x < w; x++, b += 3)
                                    {
                                        int i = o + x;
                                        if (mono)
                                        {
                                            ushort v = ColorTransform.U16((float)grayCurve.ToLinear(tf(0, img.Data[0][i])));
                                            buf[b] = buf[b + 1] = buf[b + 2] = v;
                                        }
                                        else
                                        {
                                            // Windows' 16-bit layout: blue, green, red
                                            buf[b] = ColorTransform.U16(tf(2, img.Data[2][i]));
                                            buf[b + 1] = ColorTransform.U16(tf(1, img.Data[1][i]));
                                            buf[b + 2] = ColorTransform.U16(tf(0, img.Data[0][i]));
                                        }
                                    }
                                }
                                var part = rows == strip ? buf : buf.Take(w * rows * 3).ToArray();
                                var dst = (IntPtr)((byte*)data.Scan0 + (long)y * data.Stride);
                                if (t2 == null) t.TranslateToBitmap(part, w, rows, dst, data.Stride);
                                else t2.TranslateToBitmap(t.Translate(part, WcsTransform.Format.Rgb16, w, rows, midFormat), midFormat, w, rows, dst, data.Stride);
                                if (g != null)
                                {
                                    var outside = g.CheckGamut(part, w, rows);
                                    for (int r = 0; r < rows; r++)
                                    {
                                        byte* row = (byte*)dst + (long)r * data.Stride;
                                        for (int x = 0; x < w; x++)
                                            if (outside[r * w + x]) { row[x * 4] = warn.B; row[x * 4 + 1] = warn.G; row[x * 4 + 2] = warn.R; }
                                    }
                                }
                            }
                        }
                    });
                }
                catch { bmp.UnlockBits(data); bmp.Dispose(); throw; }
                bmp.UnlockBits(data);
                return bmp;
            }
        }

        /// <summary>Night vision: show brightness in red only, so images do not spoil dark adaptation.</summary>
        private static unsafe void NightTint(Bitmap bmp)
        {
            var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            try
            {
                int h = bmp.Height, w = bmp.Width, stride = data.Stride;
                byte* scan0 = (byte*)data.Scan0;
                System.Threading.Tasks.Parallel.For(0, h, y =>
                {
                    byte* row = scan0 + (long)y * stride;
                    for (int x = 0; x < w; x++)
                    {
                        byte* px = row + x * 4; // B, G, R, A
                        int lum = (px[0] * 29 + px[1] * 150 + px[2] * 77) >> 8;
                        px[2] = (byte)lum; px[1] = (byte)(lum * 6 / 100); px[0] = (byte)(lum * 4 / 100);
                    }
                });
            }
            finally { bmp.UnlockBits(data); }
        }

        private static unsafe Bitmap FalseColorBitmap(AstroImage img, Func<int, float, float> tf)
        {
            int w = img.Width, h = img.Height;
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            var d = img.Data[0];
            Parallel.For(0, h, y =>
            {
                byte* row = (byte*)data.Scan0 + (long)y * data.Stride;
                for (int x = 0; x < w; x++)
                {
                    float v = Math.Max(0, Math.Min(1, tf(0, d[y * w + x])));
                    // perceptual "inferno-like" map
                    double r = Math.Min(1, 1.6 * v), g = Math.Max(0, Math.Min(1, 1.6 * v - 0.5)), b = Math.Max(0, Math.Min(1, v < 0.4 ? 0.8 * v : 1.0 - v + 0.2 * v * v * 4));
                    row[x * 4] = (byte)(b * 255); row[x * 4 + 1] = (byte)(g * 255); row[x * 4 + 2] = (byte)(r * 255); row[x * 4 + 3] = 255;
                }
            });
            bmp.UnlockBits(data);
            return bmp;
        }

        public void FitToWindow()
        {
            if (_image == null || Width < 10 || Height < 10) return;
            _zoom = Math.Min((double)Width / _image.Width, (double)Height / _image.Height) * 0.98;
            _offset = new PointF((float)(_image.Width / 2.0 - Width / 2.0 / _zoom), (float)(_image.Height / 2.0 - Height / 2.0 / _zoom));
            Invalidate();
        }

        public void SetZoom(double z, Point? anchor = null)
        {
            if (_image == null) return;
            var a = anchor ?? new Point(Width / 2, Height / 2);
            double ix = _offset.X + a.X / _zoom, iy = _offset.Y + a.Y / _zoom;
            _zoom = AstroMath.Clamp(z, 0.02, 64);
            _offset = new PointF((float)(ix - a.X / _zoom), (float)(iy - a.Y / _zoom));
            Invalidate();
        }

        public void CenterOn(double x, double y)
        {
            _offset = new PointF((float)(x - Width / 2.0 / _zoom), (float)(y - Height / 2.0 / _zoom));
            Invalidate();
        }

        public PointF ImageToScreen(double x, double y) => new PointF((float)((x - _offset.X) * _zoom), (float)((y - _offset.Y) * _zoom));
        public void ScreenToImage(Point p, out double x, out double y) { x = _offset.X + p.X / _zoom; y = _offset.Y + p.Y / _zoom; }

        protected override void OnResize(EventArgs e) { base.OnResize(e); Invalidate(); }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            double f = e.Delta > 0 ? 1.25 : 0.8;
            SetZoom(_zoom * f, e.Location);
            base.OnMouseWheel(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();
            if (e.Button == MouseButtons.Left || e.Button == MouseButtons.Middle) { _dragging = true; _dragStart = e.Location; _dragOffset = _offset; Cursor = Cursors.SizeAll; }
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (_dragging && Math.Abs(e.X - _dragStart.X) + Math.Abs(e.Y - _dragStart.Y) < 4 && _image != null)
            {
                double x, y;
                ScreenToImage(e.Location, out x, out y);
                ImageClick?.Invoke(x, y, e);
            }
            else if (e.Button == MouseButtons.Right && _image != null)
            {
                double x, y;
                ScreenToImage(e.Location, out x, out y);
                ImageClick?.Invoke(x, y, e);
            }
            _dragging = false;
            Cursor = Cursors.Cross;
            base.OnMouseUp(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_dragging)
            {
                _offset = new PointF((float)(_dragOffset.X - (e.X - _dragStart.X) / _zoom), (float)(_dragOffset.Y - (e.Y - _dragStart.Y) / _zoom));
                Invalidate();
            }
            else if (_image != null) ReadoutChanged?.Invoke(Readout(e.Location));
            base.OnMouseMove(e);
        }

        protected override void OnDoubleClick(EventArgs e) { if (_zoom < 0.99) SetZoom(1, PointToClient(MousePosition)); else FitToWindow(); base.OnDoubleClick(e); }

        protected override bool IsInputKey(Keys k) => k == Keys.Left || k == Keys.Right || k == Keys.Up || k == Keys.Down || base.IsInputKey(k);

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Control && e.Shift && e.KeyCode == Keys.O) { Proof = !Proof; e.Handled = true; base.OnKeyDown(e); return; }
            if (e.Control && e.Shift && e.KeyCode == Keys.Y) { GamutWarning = !GamutWarning; e.Handled = true; base.OnKeyDown(e); return; }
            switch (e.KeyCode)
            {
                case Keys.Oemplus: case Keys.Add: SetZoom(_zoom * 1.25); break;
                case Keys.OemMinus: case Keys.Subtract: SetZoom(_zoom * 0.8); break;
                case Keys.D0: case Keys.NumPad0: FitToWindow(); break;
                case Keys.D1: case Keys.NumPad1: SetZoom(1); break;
                case Keys.Left: _offset.X -= (float)(50 / _zoom); Invalidate(); break;
                case Keys.Right: _offset.X += (float)(50 / _zoom); Invalidate(); break;
                case Keys.Up: _offset.Y -= (float)(50 / _zoom); Invalidate(); break;
                case Keys.Down: _offset.Y += (float)(50 / _zoom); Invalidate(); break;
            }
            base.OnKeyDown(e);
        }

        protected override void OnDragEnter(DragEventArgs e) { if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy; base.OnDragEnter(e); }
        protected override void OnDragDrop(DragEventArgs e) { var f = e.Data.GetData(DataFormats.FileDrop) as string[]; if (f != null) FilesDropped?.Invoke(f); base.OnDragDrop(e); }

        public string Readout(Point p)
        {
            double x, y;
            ScreenToImage(p, out x, out y);
            int ix = (int)Math.Floor(x), iy = (int)Math.Floor(y);
            if (_image == null || ix < 0 || iy < 0 || ix >= _image.Width || iy >= _image.Height) return "";
            var sb = new System.Text.StringBuilder();
            sb.AppendFormat("x {0}  y {1}   ", ix, iy);
            int i = iy * _image.Width + ix;
            if (_image.Channels == 1) sb.AppendFormat("{0:0.00000} ({1:0} ADU)", _image.Data[0][i], _image.Data[0][i] * 65535);
            else sb.AppendFormat("R {0:0.0000}  G {1:0.0000}  B {2:0.0000}", _image.Data[0][i], _image.Data[1][i], _image.Data[2][i]);
            if (Wcs != null)
            {
                double ra, dec;
                Wcs.PixelToWorld(x, y, out ra, out dec);
                sb.AppendFormat("   α {0}  δ {1}", AstroMath.FormatHms(ra / 15, 1), AstroMath.FormatDms(dec, 0));
            }
            if (ExtraReadout != null) sb.Append("   " + ExtraReadout(x, y));
            if (_image.IsColor) sb.Append("   " + ColorUi.ImageProfileText(_image));
            sb.AppendFormat("   zoom {0:0.##}×", _zoom);
            return sb.ToString();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var p = Theme.Current;
            var g = e.Graphics;
            g.Clear(p.ChartBackground);
            if (_image == null)
            {
                TextRenderer.DrawText(g, EmptyText, Theme.HeaderFont, ClientRectangle, p.TextFaint, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
                return;
            }
            if (_bitmap != null)
            {
                g.InterpolationMode = _zoom >= 2 ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBilinear;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                var dest = new RectangleF((float)(-_offset.X * _zoom), (float)(-_offset.Y * _zoom), (float)(_image.Width * _zoom), (float)(_image.Height * _zoom));
                if (p.IsNight && !_bitmapNight)
                {
                    // night vision: show luminance in red only. Rebuild already tints the bitmap in night mode; this only
                    // covers the moment after switching, before the tinted bitmap is ready (tinting twice keeps ~30%)
                    var cm = new ColorMatrix(new[]
                    {
                        new float[] { 0.30f, 0, 0, 0, 0 }, new float[] { 0.59f, 0, 0, 0, 0 }, new float[] { 0.11f, 0, 0, 0, 0 },
                        new float[] { 0, 0, 0, 1, 0 }, new float[] { 0, 0, 0, 0, 1 }
                    });
                    using (var ia = new ImageAttributes())
                    {
                        ia.SetColorMatrix(cm);
                        var pts = new[] { dest.Location, new PointF(dest.Right, dest.Top), new PointF(dest.Left, dest.Bottom) };
                        g.DrawImage(_bitmap, pts, new RectangleF(0, 0, _bitmap.Width, _bitmap.Height), GraphicsUnit.Pixel, ia);
                    }
                }
                else g.DrawImage(_bitmap, dest);
            }
            else TextRenderer.DrawText(g, Sidralune.Core.Util.Lang.T("Rendering…"), Theme.UiFont, ClientRectangle, p.TextDim, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (ShowWcsGrid && Wcs != null) DrawWcsGrid(g, p);
            PaintOverlay?.Invoke(g, ImageToScreen);
            // proofing and the gamut warning say so, so neither is left on unnoticed
            if ((_proof || _gamut) && ColorSettings.Enabled && !p.IsNight)
            {
                var parts = new List<string>();
                if (_proof) parts.Add(Sidralune.Core.Util.Lang.T("Proof: {0}", ColorUi.Name(ColorSettings.ProofingProfile)));
                if (_gamut) parts.Add(Sidralune.Core.Util.Lang.T("Gamut warning"));
                string text = string.Join("   ·   ", parts);
                var size = TextRenderer.MeasureText(text, Theme.SmallFont);
                using (var b = new SolidBrush(Theme.WithAlpha(p.Surface, 210))) g.FillRectangle(b, 6, 6, size.Width + 12, size.Height + 6);
                TextRenderer.DrawText(g, text, Theme.SmallFont, new Point(12, 9), p.Text);
            }
            if (Focused && ShowFocusCues) using (var pen = new Pen(p.Focus)) g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }

        private void DrawWcsGrid(Graphics g, Palette p)
        {
            double cra, cdec;
            Wcs.Centre(out cra, out cdec);
            double fov = Math.Max(Wcs.FieldWidthDeg, Wcs.FieldHeightDeg);
            double step = fov > 20 ? 10 : fov > 8 ? 5 : fov > 3 ? 1 : fov > 1 ? 0.5 : fov > 0.4 ? 0.25 : 1.0 / 12;
            double rstep = step / Math.Max(0.1, Math.Cos(cdec * AstroMath.Deg2Rad));
            using (var pen = new Pen(Theme.WithAlpha(p.Info, 120)) { DashStyle = DashStyle.Dot })
            {
                for (double dec = Math.Floor((cdec - fov) / step) * step; dec <= cdec + fov; dec += step)
                {
                    var pts = new List<PointF>();
                    for (double ra = cra - fov * 2; ra <= cra + fov * 2; ra += rstep / 10)
                    {
                        double x, y;
                        if (Wcs.WorldToPixel(AstroMath.NormalizeDeg(ra), dec, out x, out y) && x > -Wcs.ImageWidth && x < 2 * Wcs.ImageWidth && y > -Wcs.ImageHeight && y < 2 * Wcs.ImageHeight) pts.Add(ImageToScreen(x, y));
                    }
                    if (pts.Count > 1) g.DrawLines(pen, pts.ToArray());
                }
                for (double ra = Math.Floor((cra - fov * 2) / rstep) * rstep; ra <= cra + fov * 2; ra += rstep)
                {
                    var pts = new List<PointF>();
                    for (double dec = cdec - fov; dec <= cdec + fov; dec += step / 10)
                    {
                        double x, y;
                        if (Math.Abs(dec) < 90 && Wcs.WorldToPixel(AstroMath.NormalizeDeg(ra), dec, out x, out y) && x > -Wcs.ImageWidth && x < 2 * Wcs.ImageWidth && y > -Wcs.ImageHeight && y < 2 * Wcs.ImageHeight) pts.Add(ImageToScreen(x, y));
                    }
                    if (pts.Count > 1) g.DrawLines(pen, pts.ToArray());
                }
            }
        }

        protected override void Dispose(bool disposing) { if (disposing) _bitmap?.Dispose(); base.Dispose(disposing); }
    }
}
