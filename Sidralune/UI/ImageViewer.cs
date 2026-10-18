using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Threading.Tasks;
using System.Windows.Forms;
using Sidralune.Core.Mathematics;
using Sidralune.Imaging;
using Sidralune.Imaging.Analysis;
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
        public string EmptyText = "Open an image (Ctrl+O) or drop files here";
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

        public void ApplyTheme(Palette p) => Invalidate();

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

        /// <summary>Recompute display bitmap (async for big images).</summary>
        public void Rebuild()
        {
            var img = _image;
            if (img == null) { _bitmap?.Dispose(); _bitmap = null; Invalidate(); return; }
            int version = ++_renderVersion;
            var stretch = _stretch;
            var custom = CustomStretch;
            bool inv = Invert, fc = FalseColor;
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
                Bitmap bmp = fc && img.Channels == 1 ? FalseColorBitmap(img, tf) : BitmapIO.ToBitmap(img, tf);
                if (IsDisposed) { bmp.Dispose(); return; }
                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        if (version != _renderVersion) { bmp.Dispose(); return; }
                        _bitmap?.Dispose();
                        _bitmap = bmp;
                        _stf = t;
                        Invalidate();
                    }));
                }
                catch { bmp.Dispose(); }
            });
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
                if (p.IsNight)
                {
                    // night vision: show luminance in red only
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
            else TextRenderer.DrawText(g, "Rendering…", Theme.UiFont, ClientRectangle, p.TextDim, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (ShowWcsGrid && Wcs != null) DrawWcsGrid(g, p);
            PaintOverlay?.Invoke(g, ImageToScreen);
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
