using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Printing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using static Sidralune.Core.Util.Lang;

namespace Sidralune.UI
{
    /// <summary>
    /// Sidralune's print preview: the pages of a document in a window with the app's colours, toolbar and icon. In night
    /// mode the pages are shown in red on dark paper, so checking a chart at the telescope does not spoil dark adaptation; what
    /// is printed is unchanged.
    /// </summary>
    public class PrintPreviewWindow : Form
    {
        private static readonly float[] ZoomSteps = { 0.25f, 0.5f, 0.75f, 1f, 1.5f, 2f, 3f, 4f };

        private readonly PrintDocument _doc;
        private readonly bool _choosePrinter;
        private readonly PageView _view;
        private readonly FlatButton _print, _zoomIn, _zoomOut, _fit, _prev, _next, _two;
        private readonly Label _zoomText, _pageText;
        private readonly EventHandler _themeChanged;

        /// <summary>The pages as the printer will get them.</summary>
        public PreviewPageInfo[] Pages { get; }

        /// <summary>Renders the pages of <paramref name="doc"/>. <paramref name="choosePrinter"/>: Print asks for the printer first.</summary>
        public PrintPreviewWindow(PrintDocument doc, string title, bool choosePrinter)
        {
            _doc = doc; _choosePrinter = choosePrinter;
            Pages = RenderPages(doc);

            Text = string.IsNullOrEmpty(title) ? T("Print preview") : title;
            Icon = MainForm.AppIcon;
            Font = Theme.UiFont;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            KeyPreview = true;
            MinimumSize = new Size(520, 360);

            _view = new PageView(this) { Dock = DockStyle.Fill };
            _print = new FlatButton(choosePrinter ? T("Print…") : T("Print"), Icons.Print, (s, e) => PrintNow()) { Kind = ButtonKind.Primary, ToolTipText = T("Print") + "  Ctrl+P" };
            _zoomOut = IconButton(Icons.ZoomOut, T("Zoom out") + "  −", (s, e) => ZoomBy(-1));
            _zoomText = new Label { AutoSize = false, Width = 52, Height = 30, TextAlign = ContentAlignment.MiddleCenter };
            _zoomIn = IconButton(Icons.Zoom, T("Zoom in") + "  +", (s, e) => ZoomBy(1));
            _fit = new FlatButton(T("Whole page"), Icons.Fit, (s, e) => { _view.Zoom = 0; Changed(); }) { ToolTipText = T("Whole page") + "  0" };
            _prev = IconButton(Icons.Left, T("Previous page") + "  PgUp", (s, e) => Go(_view.First - 1));
            _pageText = new Label { AutoSize = true, Padding = new Padding(0, 7, 0, 0) };
            _next = IconButton(Icons.Right, T("Next page") + "  PgDn", (s, e) => Go(_view.First + 1));
            _two = new FlatButton(T("Two pages"), Icons.Grid) { Toggle = true };
            _two.CheckedChanged += (s, e) => { _view.TwoPages = _two.Checked; Go(_view.First); };
            var close = new FlatButton(T("Close"), Icons.Close, (s, e) => Close());

            var items = new List<Control> { _print, Ui.Separator(), _zoomOut, _zoomText, _zoomIn, _fit };
            if (Pages.Length > 1) items.AddRange(new Control[] { Ui.Separator(), _prev, _pageText, _next, _two });
            items.AddRange(new Control[] { Ui.Separator(), close });
            var bar = Ui.Toolbar(items.ToArray());
            Controls.Add(_view);
            Controls.Add(bar);

            Theme.Apply(this);
            HandleCreated += (s, e) => Theme.DarkTitleBar(this);
            _themeChanged = (s, e) => { Theme.Apply(this); Theme.DarkTitleBar(this); _view.ThemeChanged(); };
            Theme.Changed += _themeChanged;
            Changed();
        }

        private static FlatButton IconButton(string icon, string tip, EventHandler click)
        {
            var b = new FlatButton { Icon = icon, Width = 34, ToolTipText = tip };
            b.Click += click;
            return b;
        }

        /// <summary>Shows the preview of <paramref name="doc"/> as a dialog, or says that no printer is installed.</summary>
        public static void Show(IWin32Window owner, PrintDocument doc, string title, bool choosePrinter, Size size)
        {
            PrintPreviewWindow w;
            try { w = new PrintPreviewWindow(doc, title, choosePrinter); }
            catch (InvalidPrinterException) { MainForm.Instance?.Toast(T("No printer is installed"), Icons.Warning); return; }
            using (w)
            {
                var area = Screen.FromHandle(MainForm.Instance?.Handle ?? IntPtr.Zero).WorkingArea;
                w.Size = new Size(Math.Min(size.Width, area.Width), Math.Min(size.Height, area.Height));
                w.ShowDialog(owner);
            }
        }

        private static PreviewPageInfo[] RenderPages(PrintDocument doc)
        {
            var old = doc.PrintController;
            var preview = new PreviewPrintController { UseAntiAlias = true };
            doc.PrintController = preview;
            try { doc.Print(); }
            finally { doc.PrintController = old; }
            return preview.GetPreviewPageInfo();
        }

        private void PrintNow()
        {
            if (_choosePrinter)
                using (var pd = new PrintDialog { Document = _doc, UseEXDialog = true })
                    if (pd.ShowDialog(this) != DialogResult.OK) return;
            _doc.Print();
            Close();
        }

        private void ZoomBy(int dir)
        {
            float z = _view.ShownZoom;
            float next = dir > 0 ? ZoomSteps.FirstOrDefault(s => s > z * 1.001f) : ZoomSteps.LastOrDefault(s => s < z * 0.999f);
            if (next == 0) return;
            _view.Zoom = next;
            Changed();
        }

        private void Go(int page)
        {
            if (_view.TwoPages) page -= page % 2;
            _view.First = Math.Max(0, Math.Min(page, Pages.Length - 1));
            Changed();
        }

        private void Changed()
        {
            _view.Relayout();
            _zoomText.Text = _view.ShownZoom.ToString("P0", CultureInfo.CurrentCulture);
            _zoomIn.Enabled = _view.ShownZoom < ZoomSteps[ZoomSteps.Length - 1] * 0.999f;
            _zoomOut.Enabled = _view.ShownZoom > ZoomSteps[0] * 1.001f;
            int shown = _view.Shown.Count;
            _pageText.Text = shown > 1 ? T("Pages {0}–{1} of {2}", _view.First + 1, _view.First + shown, Pages.Length) : T("Page {0} of {1}", _view.First + 1, Pages.Length);
            _prev.Enabled = _view.First > 0;
            _next.Enabled = _view.First + shown < Pages.Length;
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            switch (keyData)
            {
                case Keys.Escape: Close(); return true;
                case Keys.Control | Keys.P: PrintNow(); return true;
                case Keys.Add: case Keys.Oemplus: case Keys.Control | Keys.Add: case Keys.Control | Keys.Oemplus: ZoomBy(1); return true;
                case Keys.Subtract: case Keys.OemMinus: case Keys.Control | Keys.Subtract: case Keys.Control | Keys.OemMinus: ZoomBy(-1); return true;
                case Keys.D0: case Keys.NumPad0: _view.Zoom = 0; Changed(); return true;
                case Keys.PageUp: if (_view.VerticalScroll.Visible && _view.VerticalScroll.Value > 0) break; Go(_view.First - (_view.TwoPages ? 2 : 1)); return true;
                case Keys.PageDown: if (_view.VerticalScroll.Visible && _view.VerticalScroll.Value < _view.VerticalScroll.Maximum - _view.VerticalScroll.LargeChange) break; Go(_view.First + (_view.TwoPages ? 2 : 1)); return true;
                case Keys.Control | Keys.Shift | Keys.N: MainForm.Instance?.SetNightMode(!Theme.Current.IsNight); return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        internal void ZoomWithWheel(int delta) => ZoomBy(Math.Sign(delta));

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Theme.Changed -= _themeChanged;
                foreach (var p in Pages) p.Image.Dispose();
            }
            base.Dispose(disposing);
        }

        /// <summary>The page as shown on screen at <paramref name="size"/>: as printed, or red on dark paper in night mode.</summary>
        public static Bitmap RenderPage(PreviewPageInfo page, Size size, bool night) => RenderPage(page, size, night, new Rectangle(Point.Empty, size));

        /// <summary>The part <paramref name="part"/> of the page shown at <paramref name="size"/> (for zooms too large to keep the whole page).</summary>
        public static Bitmap RenderPage(PreviewPageInfo page, Size size, bool night, Rectangle part)
        {
            var bmp = new Bitmap(Math.Max(1, part.Width), Math.Max(1, part.Height), PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(bmp))
            using (var ia = new ImageAttributes())
            {
                g.Clear(Color.White);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(page.Image, new Rectangle(-part.X, -part.Y, size.Width, size.Height));
                if (night)
                {
                    // like the night sky chart: dark paper and red ink, the darker the ink the brighter the red
                    // (out = ink - (ink - paper) * brightness, so white paper gets the paper colour and black ink the text colour)
                    var np = Palette.Night();
                    Color ink = np.Text, paper = np.SurfaceRaised;
                    float[] d = { (ink.R - paper.R) / 255f, (ink.G - paper.G) / 255f, (ink.B - paper.B) / 255f };
                    float[] w = { 0.299f, 0.587f, 0.114f };
                    ia.SetColorMatrix(new ColorMatrix(new[]
                    {
                        new[] { -d[0] * w[0], -d[1] * w[0], -d[2] * w[0], 0, 0 },
                        new[] { -d[0] * w[1], -d[1] * w[1], -d[2] * w[1], 0, 0 },
                        new[] { -d[0] * w[2], -d[1] * w[2], -d[2] * w[2], 0, 0 },
                        new[] { 0f, 0, 0, 1, 0 },
                        new[] { ink.R / 255f, ink.G / 255f, ink.B / 255f, 0, 1 },
                    }));
                    using (var copy = (Bitmap)bmp.Clone())
                    {
                        g.CompositingMode = CompositingMode.SourceCopy;
                        g.InterpolationMode = InterpolationMode.NearestNeighbor;
                        g.PixelOffsetMode = PixelOffsetMode.Half;
                        g.DrawImage(copy, new Rectangle(Point.Empty, bmp.Size), 0, 0, copy.Width, copy.Height, GraphicsUnit.Pixel, ia);
                    }
                }
            }
            return bmp;
        }

        /// <summary>The scrolling area with the pages, zoomed to fit or to a chosen size; drag to pan, Ctrl+wheel to zoom.</summary>
        private class PageView : ScrollableControl, IThemed
        {
            private const int Pad = 24, Gap = 16;
            private readonly PrintPreviewWindow _owner;
            private readonly Dictionary<int, Bitmap> _cache = new Dictionary<int, Bitmap>();
            private Point _dragFrom, _scrollFrom;
            private bool _dragging;

            /// <summary>0 = the whole page (or both pages) fits the window, otherwise the size on screen (1 = actual size).</summary>
            public float Zoom;
            public bool TwoPages;
            public int First;
            public float ShownZoom { get; private set; } = 1;
            public List<int> Shown { get; } = new List<int>();

            public PageView(PrintPreviewWindow owner)
            {
                _owner = owner;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
                AutoScroll = true;
                Cursor = Cursors.Hand;
            }

            public void ApplyTheme(Palette p) { BackColor = p.Background; Invalidate(); }

            public void ThemeChanged() { ClearCache(); Invalidate(); }

            private SizeF PageInches(int i) => new SizeF(_owner.Pages[i].PhysicalSize.Width / 100f, _owner.Pages[i].PhysicalSize.Height / 100f);

            private Size PagePixels(int i)
            {
                var inch = PageInches(i);
                float dpi = DeviceDpi * ShownZoom;
                return new Size((int)Math.Round(inch.Width * dpi), (int)Math.Round(inch.Height * dpi));
            }

            public void Relayout()
            {
                Shown.Clear();
                int n = _owner.Pages.Length;
                if (n == 0) return;
                First = Math.Max(0, Math.Min(First, n - 1));
                Shown.Add(First);
                if (TwoPages && First + 1 < n) Shown.Add(First + 1);
                if (Zoom > 0) ShownZoom = Zoom;
                else
                {
                    float w = Shown.Sum(i => PageInches(i).Width) * DeviceDpi, h = Shown.Max(i => PageInches(i).Height) * DeviceDpi;
                    float room = Math.Max(50, ClientSize.Width - 2 * Pad - Gap * (Shown.Count - 1)), roomH = Math.Max(50, ClientSize.Height - 2 * Pad);
                    ShownZoom = Math.Max(0.05f, Math.Min(room / w, roomH / h));
                }
                var sizes = Shown.Select(PagePixels).ToList();
                AutoScrollMinSize = Zoom > 0 ? new Size(sizes.Sum(s => s.Width) + Gap * (sizes.Count - 1) + 2 * Pad, sizes.Max(s => s.Height) + 2 * Pad) : Size.Empty;
                ClearCache();
                Invalidate();
            }

            protected override void OnResize(EventArgs e)
            {
                base.OnResize(e);
                if (Zoom == 0 && _owner.Pages.Length > 0) { Relayout(); _owner.Changed(); }
            }

            private void ClearCache() { foreach (var b in _cache.Values) b.Dispose(); _cache.Clear(); }

            protected override void OnPaint(PaintEventArgs e)
            {
                var p = Theme.Current;
                e.Graphics.Clear(p.Background);
                if (Shown.Count == 0) return;
                var sizes = Shown.Select(PagePixels).ToList();
                int total = sizes.Sum(s => s.Width) + Gap * (sizes.Count - 1), tall = sizes.Max(s => s.Height);
                int x = Math.Max(Pad, (ClientSize.Width - total) / 2) + AutoScrollPosition.X;
                int top = Math.Max(Pad, (ClientSize.Height - tall) / 2) + AutoScrollPosition.Y;
                for (int k = 0; k < Shown.Count; k++)
                {
                    var r = new Rectangle(x, top, sizes[k].Width, sizes[k].Height);
                    using (var shadow = new SolidBrush(p.BorderStrong)) e.Graphics.FillRectangle(shadow, r.X + 3, r.Y + 3, r.Width, r.Height);
                    if (r.IntersectsWith(e.ClipRectangle))
                    {
                        var bmp = Page(Shown[k], r.Size, p.IsNight);
                        if (bmp != null) e.Graphics.DrawImageUnscaled(bmp, r.Location);
                        else
                        {
                            // too large to keep: draw only the part in view (slower, but no huge bitmap)
                            var vis = Rectangle.Intersect(r, ClientRectangle);
                            if (vis.Width > 0 && vis.Height > 0)
                                using (var part = RenderPage(_owner.Pages[Shown[k]], r.Size, p.IsNight, new Rectangle(vis.X - r.X, vis.Y - r.Y, vis.Width, vis.Height)))
                                    e.Graphics.DrawImageUnscaled(part, vis.Location);
                        }
                    }
                    using (var pen = new Pen(p.Border)) e.Graphics.DrawRectangle(pen, r.X - 1, r.Y - 1, r.Width + 1, r.Height + 1);
                    x += r.Width + Gap;
                }
            }

            private Bitmap Page(int i, Size size, bool night)
            {
                if ((long)size.Width * size.Height > 40000000L) return null;
                Bitmap bmp;
                if (_cache.TryGetValue(i, out bmp) && bmp.Size == size) return bmp;
                bmp?.Dispose();
                return _cache[i] = RenderPage(_owner.Pages[i], size, night);
            }

            protected override void OnMouseWheel(MouseEventArgs e)
            {
                if ((ModifierKeys & Keys.Control) != 0) { _owner.ZoomWithWheel(e.Delta); return; }
                base.OnMouseWheel(e);
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                Focus();
                if (e.Button == MouseButtons.Left && AutoScrollMinSize != Size.Empty) { _dragging = true; _dragFrom = e.Location; _scrollFrom = new Point(-AutoScrollPosition.X, -AutoScrollPosition.Y); }
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                if (!_dragging) return;
                AutoScrollPosition = new Point(_scrollFrom.X - (e.X - _dragFrom.X), _scrollFrom.Y - (e.Y - _dragFrom.Y));
                Invalidate();
            }

            protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); _dragging = false; }

            protected override void OnScroll(ScrollEventArgs se) { base.OnScroll(se); Invalidate(); }

            protected override void Dispose(bool disposing)
            {
                if (disposing) ClearCache();
                base.Dispose(disposing);
            }
        }
    }
}
