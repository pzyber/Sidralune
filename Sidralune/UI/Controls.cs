using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

namespace Sidralune.UI
{
    public enum ButtonKind { Normal, Primary, Subtle, Danger }

    /// <summary>Flat button with icon glyph, hover/pressed states, keyboard focus ring and optional toggle state.</summary>
    public class FlatButton : Control, IThemed, IButtonControl
    {
        private bool _hover, _down, _checked;
        public string Icon { get; set; }
        public ButtonKind Kind { get; set; } = ButtonKind.Normal;
        public bool Toggle { get; set; }
        public bool Checked { get { return _checked; } set { _checked = value; Invalidate(); } }
        public DialogResult DialogResult { get; set; }
        public event EventHandler CheckedChanged;
        private readonly ToolTip _tip = new ToolTip();
        private string _tooltip;
        public string ToolTipText { get { return _tooltip; } set { _tooltip = value; _tip.SetToolTip(this, value); } }

        public FlatButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.SupportsTransparentBackColor, true);
            Font = Theme.UiFont;
            Height = 30;
            Cursor = Cursors.Hand;
            TabStop = true;
        }

        public FlatButton(string text, string icon = null, EventHandler click = null) : this()
        {
            Text = text; Icon = icon;
            if (click != null) Click += click;
            AutoSizeToText();
        }

        public void AutoSizeToText()
        {
            using (var g = CreateGraphics())
            {
                int w = string.IsNullOrEmpty(Text) ? 0 : (int)g.MeasureString(Text, Font).Width + 18;
                if (!string.IsNullOrEmpty(Icon)) w += string.IsNullOrEmpty(Text) ? 34 : 22;
                Width = Math.Max(32, w);
            }
        }

        public void ApplyTheme(Palette p) => Invalidate();
        public void NotifyDefault(bool value) { }
        public void PerformClick() => OnClick(EventArgs.Empty);

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { _down = true; Focus(); Invalidate(); } base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }

        protected override void OnClick(EventArgs e)
        {
            if (Toggle) { _checked = !_checked; CheckedChanged?.Invoke(this, EventArgs.Empty); Invalidate(); }
            base.OnClick(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) { OnClick(EventArgs.Empty); e.Handled = true; }
            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var p = Theme.Current;
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            Color bg, fg, border;
            switch (Kind)
            {
                case ButtonKind.Primary: bg = _down ? Theme.Blend(p.Accent, Color.Black, 0.2) : _hover ? Theme.Blend(p.Accent, Color.White, 0.1) : p.Accent; fg = p.AccentText; border = bg; break;
                case ButtonKind.Danger: bg = _down ? p.SurfacePressed : _hover ? Theme.Blend(p.SurfaceRaised, p.Danger, 0.25) : p.SurfaceRaised; fg = p.Danger; border = Theme.Blend(p.Border, p.Danger, 0.5); break;
                case ButtonKind.Subtle: bg = _down ? p.SurfacePressed : _hover ? p.SurfaceHover : (_checked ? p.AccentSoft : Parent?.BackColor ?? p.Background); fg = _checked ? p.Accent : p.Text; border = bg; break;
                default: bg = _down ? p.SurfacePressed : _hover ? p.SurfaceHover : (_checked ? p.AccentSoft : p.SurfaceRaised); fg = _checked ? p.Accent : p.Text; border = _checked ? p.Accent : p.BorderStrong; break;
            }
            if (!Enabled) { fg = p.TextFaint; bg = Kind == ButtonKind.Subtle ? (Parent?.BackColor ?? p.Background) : p.Surface; border = p.Border; }
            g.Clear(Parent?.BackColor ?? p.Background);
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = Rounded(r, 5))
            {
                using (var b = new SolidBrush(bg)) g.FillPath(b, path);
                using (var pen = new Pen(border)) g.DrawPath(pen, path);
            }
            if (Focused && ShowFocusCues)
            {
                using (var path = Rounded(new Rectangle(1, 1, Width - 3, Height - 3), 4))
                using (var pen = new Pen(p.Focus, 1.5f)) g.DrawPath(pen, path);
            }
            int x = 8;
            bool hasText = !string.IsNullOrEmpty(Text);
            if (!string.IsNullOrEmpty(Icon))
            {
                var isz = TextRenderer.MeasureText(Icon, Theme.IconFont);
                int ix = hasText ? x : (Width - isz.Width) / 2 + 2;
                TextRenderer.DrawText(g, Icon, Theme.IconFont, new Point(ix, (Height - isz.Height) / 2 + 1), fg, TextFormatFlags.NoPadding);
                x = ix + isz.Width + 2;
            }
            if (hasText)
            {
                var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | (string.IsNullOrEmpty(Icon) ? TextFormatFlags.HorizontalCenter : TextFormatFlags.Left);
                var tr = string.IsNullOrEmpty(Icon) ? new Rectangle(0, 0, Width, Height) : new Rectangle(x, 0, Width - x - 4, Height);
                TextRenderer.DrawText(g, Text, Font, tr, fg, flags);
            }
        }

        public static GraphicsPath Rounded(Rectangle r, int rad)
        {
            var p = new GraphicsPath();
            int d = rad * 2;
            if (d <= 0 || r.Width < d || r.Height < d) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    /// <summary>A navigation rail entry.</summary>
    public class NavItem
    {
        public string Key, Title, Icon, Group, Tooltip;
        public Func<Control> Factory;
        public Control Instance;
    }

    /// <summary>Vertical navigation rail (Material 3 style): icon + short label, grouped, with selection indicator.</summary>
    public class NavRail : Control, IThemed
    {
        public readonly List<NavItem> Items = new List<NavItem>();
        public NavItem Selected { get; private set; }
        public event Action<NavItem> SelectedChanged;
        private int _hover = -1;
        private int ItemH => Math.Max(40, Math.Min(56, (Height - 16 - 12 * 4) / Math.Max(1, Items.Count)));
        private readonly ToolTip _tip = new ToolTip { InitialDelay = 500 };
        public bool Compact { get; set; }

        public NavRail()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            Width = 84;
            TabStop = true;
        }

        public void ApplyTheme(Palette p) => Invalidate();

        private Rectangle ItemRect(int i)
        {
            int y = 8, lastGroup = -1;
            string grp = null;
            for (int k = 0; k <= i; k++)
            {
                if (k > 0 && Items[k].Group != grp) y += 12;
                grp = Items[k].Group;
                if (k == i) return new Rectangle(4, y, Width - 8, ItemH);
                y += ItemH;
            }
            return Rectangle.Empty;
        }

        public void Select(string key)
        {
            var it = Items.FirstOrDefault(i => i.Key == key);
            if (it == null || it == Selected) return;
            Selected = it;
            Invalidate();
            SelectedChanged?.Invoke(it);
        }

        private int HitTest(Point pt)
        {
            for (int i = 0; i < Items.Count; i++) if (ItemRect(i).Contains(pt)) return i;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int h = HitTest(e.Location);
            if (h != _hover)
            {
                _hover = h; Invalidate();
                _tip.SetToolTip(this, h >= 0 ? (Items[h].Tooltip ?? Items[h].Title) : null);
            }
            Cursor = h >= 0 ? Cursors.Hand : Cursors.Default;
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e) { _hover = -1; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseClick(MouseEventArgs e) { int h = HitTest(e.Location); if (h >= 0) Select(Items[h].Key); base.OnMouseClick(e); }

        protected override bool IsInputKey(Keys keyData) => keyData == Keys.Up || keyData == Keys.Down || base.IsInputKey(keyData);

        protected override void OnKeyDown(KeyEventArgs e)
        {
            int i = Selected == null ? 0 : Items.IndexOf(Selected);
            if (e.KeyCode == Keys.Down) Select(Items[Math.Min(Items.Count - 1, i + 1)].Key);
            if (e.KeyCode == Keys.Up) Select(Items[Math.Max(0, i - 1)].Key);
            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var p = Theme.Current;
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(p.Surface);
            using (var pen = new Pen(p.Border)) g.DrawLine(pen, Width - 1, 0, Width - 1, Height);
            string grp = null;
            for (int i = 0; i < Items.Count; i++)
            {
                var it = Items[i];
                var r = ItemRect(i);
                if (i > 0 && it.Group != grp)
                    using (var pen = new Pen(p.Border)) g.DrawLine(pen, 16, r.Top - 7, Width - 16, r.Top - 7);
                grp = it.Group;
                bool sel = it == Selected, hov = i == _hover;
                // pill indicator behind the icon (Material 3 navigation rail)
                var pill = new Rectangle(r.X + (r.Width - 52) / 2, r.Y + (ItemH >= 48 ? 6 : (ItemH - 28) / 2), 52, 28);
                if (sel || hov)
                    using (var path = FlatButton.Rounded(pill, 14))
                    using (var b = new SolidBrush(sel ? p.AccentSoft : p.SurfaceHover)) g.FillPath(b, path);
                var fg = sel ? p.Accent : (hov ? p.Text : p.TextDim);
                var isz = TextRenderer.MeasureText(it.Icon, Theme.IconFontLarge);
                TextRenderer.DrawText(g, it.Icon, Theme.IconFontLarge, new Point(pill.X + (pill.Width - isz.Width) / 2 + 1, pill.Y + (pill.Height - isz.Height) / 2 + 1), fg, TextFormatFlags.NoPadding);
                if (ItemH >= 48) TextRenderer.DrawText(g, it.Title, Theme.SmallFont, new Rectangle(r.X - 2, pill.Bottom + 1, r.Width + 4, 16), sel ? p.Text : p.TextDim, TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis);
            }
            if (Focused && ShowFocusCues && Selected != null)
                using (var pen = new Pen(p.Focus)) g.DrawRectangle(pen, ItemRect(Items.IndexOf(Selected)));
        }
    }

    /// <summary>Card container with a header title, optional subtitle and header actions.</summary>
    public class Card : Panel, IThemed
    {
        public string Title { get { return _title; } set { _title = value; Invalidate(); } }
        public string Subtitle { get { return _sub; } set { _sub = value; Invalidate(); } }
        private string _title, _sub;
        public readonly FlowLayoutPanel Actions = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, WrapContents = false, BackColor = Color.Transparent };
        public int HeaderHeight => string.IsNullOrEmpty(_title) ? 8 : 40;

        public Card(string title = null)
        {
            _title = title;
            DoubleBuffered = true;
            Padding = new Padding(12, 44, 12, 12);
            Actions.Visible = false;
            Controls.Add(Actions);
            Actions.ControlAdded += (s, e) => { Actions.Visible = true; Actions.BringToFront(); LayoutActions(); };
            Resize += (s, e) => LayoutActions();
            Actions.SizeChanged += (s, e) => LayoutActions();
            Margin = new Padding(6);
        }

        private void LayoutActions() { Actions.Location = new Point(Width - Actions.Width - 8, 6); }

        // keep the scroll position when a child gets focus (WinForms otherwise jumps to it and hides the header)
        protected override Point ScrollToControl(Control activeControl) => DisplayRectangle.Location;

        protected override void OnScroll(ScrollEventArgs se) { base.OnScroll(se); Invalidate(); }
        protected override void OnMouseWheel(MouseEventArgs e) { base.OnMouseWheel(e); Invalidate(); }

        public void ApplyTheme(Palette p)
        {
            BackColor = p.Surface;
            Padding = new Padding(12, HeaderHeight + 4, 12, 12);
            foreach (Control c in Controls) Theme.Apply(c);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var p = Theme.Current;
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var pen = new Pen(p.Border)) g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            if (!string.IsNullOrEmpty(_title))
            {
                int oy = AutoScroll ? AutoScrollPosition.Y : 0;
                TextRenderer.DrawText(g, _title, Theme.HeaderFont, new Point(12, 10 + oy), p.Text, TextFormatFlags.NoPrefix);
                if (!string.IsNullOrEmpty(_sub))
                {
                    var tw = TextRenderer.MeasureText(_title, Theme.HeaderFont, Size.Empty, TextFormatFlags.NoPrefix).Width;
                    TextRenderer.DrawText(g, _sub, Theme.UiFont, new Point(14 + tw, 12 + oy), p.TextDim, TextFormatFlags.NoPrefix);
                }
            }
        }
    }

    /// <summary>Segmented control / tab header (switches child panels).</summary>
    public class SegmentedTabs : Control, IThemed
    {
        public readonly List<string> Tabs = new List<string>();
        private int _sel, _hover = -1;
        public int SelectedIndex { get { return _sel; } set { if (value != _sel && value >= 0 && value < Tabs.Count) { _sel = value; Invalidate(); SelectedChanged?.Invoke(_sel); } } }
        public event Action<int> SelectedChanged;

        public SegmentedTabs()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            Height = 36; Font = Theme.UiFont; TabStop = true;
        }

        public void ApplyTheme(Palette p) => Invalidate();

        private Rectangle TabRect(int i)
        {
            int x = 0;
            for (int k = 0; k < Tabs.Count; k++)
            {
                int w = TextRenderer.MeasureText(Tabs[k], Font, Size.Empty, TextFormatFlags.NoPrefix).Width + 28;
                if (k == i) return new Rectangle(x, 0, w, Height);
                x += w;
            }
            return Rectangle.Empty;
        }

        protected override void OnMouseMove(MouseEventArgs e) { int h = -1; for (int i = 0; i < Tabs.Count; i++) if (TabRect(i).Contains(e.Location)) h = i; if (h != _hover) { _hover = h; Invalidate(); } Cursor = h >= 0 ? Cursors.Hand : Cursors.Default; base.OnMouseMove(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = -1; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseClick(MouseEventArgs e) { for (int i = 0; i < Tabs.Count; i++) if (TabRect(i).Contains(e.Location)) SelectedIndex = i; Focus(); base.OnMouseClick(e); }
        protected override bool IsInputKey(Keys k) => k == Keys.Left || k == Keys.Right || base.IsInputKey(k);
        protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Left) SelectedIndex = Math.Max(0, _sel - 1); if (e.KeyCode == Keys.Right) SelectedIndex = Math.Min(Tabs.Count - 1, _sel + 1); base.OnKeyDown(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var p = Theme.Current;
            var g = e.Graphics;
            g.Clear(Parent?.BackColor ?? p.Background);
            using (var pen = new Pen(p.Border)) g.DrawLine(pen, 0, Height - 1, Width, Height - 1);
            for (int i = 0; i < Tabs.Count; i++)
            {
                var r = TabRect(i);
                bool sel = i == _sel;
                if (i == _hover && !sel) using (var b = new SolidBrush(p.SurfaceHover)) g.FillRectangle(b, r.X, r.Y + 4, r.Width, r.Height - 8);
                TextRenderer.DrawText(g, Tabs[i], sel ? Theme.UiFontBold : Font, r, sel ? p.Text : p.TextDim, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                if (sel) using (var b = new SolidBrush(p.Accent)) g.FillRectangle(b, r.X + 10, r.Bottom - 3, r.Width - 20, 3);
                if (sel && Focused && ShowFocusCues) using (var pen = new Pen(p.Focus)) g.DrawRectangle(pen, r.X + 2, r.Y + 3, r.Width - 5, r.Height - 8);
            }
        }
    }

    /// <summary>Hosts several pages under a SegmentedTabs header.</summary>
    public class TabbedPanel : Panel
    {
        public readonly SegmentedTabs Header = new SegmentedTabs { Dock = DockStyle.Top };
        private readonly Panel _body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 8, 0, 0) };
        private readonly List<Control> _pages = new List<Control>();

        public TabbedPanel()
        {
            Controls.Add(_body);
            Controls.Add(Header);
            Header.SelectedChanged += i => Show(i);
        }

        public Control AddPage(string title, Control page)
        {
            Header.Tabs.Add(title);
            page.Dock = DockStyle.Fill;
            page.Visible = _pages.Count == 0;
            _pages.Add(page);
            _body.Controls.Add(page);
            Header.Invalidate();
            return page;
        }

        private void Show(int i)
        {
            for (int k = 0; k < _pages.Count; k++) _pages[k].Visible = k == i;
        }

        public void Select(int i) { Header.SelectedIndex = i; Show(i); }
    }

    /// <summary>Collapsible section used in tool panels (progressive disclosure).</summary>
    public class Collapsible : Panel, IThemed
    {
        private static readonly Font Chevron = new Font(Theme.IconFont.FontFamily, 8f);
        private bool _expanded = true;
        private readonly Label _header;
        public readonly Panel Body;
        public string Title { get { return _header.Text; } set { _header.Text = value; } }
        public bool Expanded { get { return _expanded; } set { _expanded = value; Body.Visible = value; UpdateHeight(); _header.Invalidate(); } }

        public Collapsible(string title, Control content, bool expanded = true)
        {
            _header = new Label { Text = title, Dock = DockStyle.Top, Height = 30, Font = Theme.UiFontBold, Padding = new Padding(22, 0, 0, 0), TextAlign = ContentAlignment.MiddleLeft, Cursor = Cursors.Hand };
            _header.Paint += (s, e) =>
            {
                var p = Theme.Current;
                TextRenderer.DrawText(e.Graphics, _expanded ? "" : "", Chevron, new Point(4, 9), p.TextDim);
            };
            _header.Click += (s, e) => Expanded = !Expanded;
            Body = new Panel { Dock = DockStyle.Top, Padding = new Padding(8, 4, 8, 10) };
            content.Dock = DockStyle.Top;
            Body.Controls.Add(content);
            Body.Height = content.Height + Body.Padding.Vertical;
            content.SizeChanged += (s, e) => { Body.Height = content.Height + Body.Padding.Vertical; UpdateHeight(); };
            Controls.Add(Body);
            Controls.Add(_header);
            Dock = DockStyle.Top;
            Expanded = expanded;
        }

        private void UpdateHeight() => Height = _header.Height + (_expanded ? Body.Height : 0) + 1;

        public void ApplyTheme(Palette p)
        {
            BackColor = p.Surface;
            _header.BackColor = p.SurfaceRaised; _header.ForeColor = p.Text;
            Body.BackColor = p.Surface;
            foreach (Control c in Body.Controls) Theme.Apply(c);
        }
    }

    /// <summary>Small coloured status dot with label (device states etc.).</summary>
    public class StatusPill : Control, IThemed
    {
        public Color DotColor = Color.Gray;
        public StatusPill() { SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true); Height = 22; Font = Theme.SmallFont; }
        public void ApplyTheme(Palette p) => Invalidate();
        public void Set(string text, Color c) { Text = text; DotColor = c; Width = TextRenderer.MeasureText(text, Font).Width + 26; Invalidate(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var p = Theme.Current;
            e.Graphics.Clear(Parent?.BackColor ?? p.Surface);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var b = new SolidBrush(DotColor)) e.Graphics.FillEllipse(b, 6, Height / 2 - 4, 8, 8);
            TextRenderer.DrawText(e.Graphics, Text, Font, new Point(18, (Height - Font.Height) / 2), p.TextDim);
        }
    }

    /// <summary>Non-modal toast notifications in the bottom-right corner.</summary>
    public class ToastHost
    {
        private readonly Form _owner;
        private readonly List<Panel> _toasts = new List<Panel>();
        public ToastHost(Form owner) { _owner = owner; }

        public void Show(string text, string icon = Icons.Info, int ms = 4000, Color? accent = null)
        {
            if (_owner.IsDisposed) return;
            if (_owner.InvokeRequired) { _owner.BeginInvoke(new Action(() => Show(text, icon, ms, accent))); return; }
            var p = Theme.Current;
            var panel = new Panel { Width = 360, Height = 54, BackColor = p.SurfaceRaised };
            var lbl = new Label { Text = text, ForeColor = p.Text, Font = Theme.UiFont, Location = new Point(40, 0), Size = new Size(310, 54), TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
            var ic = new Label { Text = icon, Font = Theme.IconFontLarge, ForeColor = accent ?? p.Accent, Location = new Point(10, 15), AutoSize = true };
            panel.Controls.Add(lbl); panel.Controls.Add(ic);
            panel.Paint += (s, e) => { using (var pen = new Pen(accent ?? p.Accent)) e.Graphics.DrawRectangle(pen, 0, 0, panel.Width - 1, panel.Height - 1); };
            panel.Click += (s, e) => Remove(panel);
            lbl.Click += (s, e) => Remove(panel);
            _toasts.Add(panel);
            _owner.Controls.Add(panel);
            panel.BringToFront();
            Relayout();
            var t = new Timer { Interval = ms };
            t.Tick += (s, e) => { t.Stop(); t.Dispose(); Remove(panel); };
            t.Start();
        }

        private void Remove(Panel p) { if (!_toasts.Remove(p)) return; _owner.Controls.Remove(p); p.Dispose(); Relayout(); }

        public void Relayout()
        {
            int y = _owner.ClientSize.Height - 40;
            for (int i = _toasts.Count - 1; i >= 0; i--)
            {
                var t = _toasts[i];
                y -= t.Height + 8;
                t.Location = new Point(_owner.ClientSize.Width - t.Width - 20, y);
            }
        }
    }

    /// <summary>Layout helper building label + editor rows.</summary>
    public class FormGrid : TableLayoutPanel
    {
        public FormGrid(int labelWidth = 120)
        {
            ColumnCount = 2;
            ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, labelWidth));
            ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Dock = DockStyle.Top;
            Padding = new Padding(0);
        }

        public T Row<T>(string label, T editor) where T : Control
        {
            int r = RowCount++;
            RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var l = new Label { Text = label, AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Tag = "dim", Margin = new Padding(0, 3, 6, 3) };
            editor.Dock = DockStyle.Fill;
            editor.Margin = new Padding(0, 3, 0, 3);
            Controls.Add(l, 0, r);
            Controls.Add(editor, 1, r);
            return editor;
        }

        public void Full(Control c)
        {
            int r = RowCount++;
            RowStyles.Add(new RowStyle(SizeType.AutoSize));
            c.Dock = DockStyle.Fill;
            c.Margin = new Padding(0, 4, 0, 4);
            Controls.Add(c, 0, r);
            SetColumnSpan(c, 2);
        }
    }

    public static class Ui
    {
        public static NumericUpDown Num(decimal value, decimal min, decimal max, int decimals = 0, decimal inc = 1) =>
            new NumericUpDown { Minimum = min, Maximum = max, DecimalPlaces = decimals, Increment = inc, Value = Math.Max(min, Math.Min(max, value)), Width = 90 };

        public static ComboBox Combo(params object[] items)
        {
            var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
            c.Items.AddRange(items);
            if (c.Items.Count > 0) c.SelectedIndex = 0;
            return c;
        }

        public static ComboBox EnumCombo<T>(T value)
        {
            var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
            foreach (var v in Enum.GetValues(typeof(T))) c.Items.Add(v);
            c.SelectedItem = value;
            return c;
        }

        public static FlowLayoutPanel Toolbar(params Control[] items)
        {
            var f = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 42, Padding = new Padding(4, 6, 4, 2), WrapContents = false, AutoScroll = false };
            foreach (var i in items) { i.Margin = new Padding(0, 0, 6, 0); f.Controls.Add(i); }
            return f;
        }

        public static Label Hint(string text) => new Label { Text = text, AutoSize = true, Tag = "dim", Margin = new Padding(0, 4, 0, 4), MaximumSize = new Size(600, 0) };

        public static Label Separator() => new Label { Width = 1, Height = 24, BackColor = Theme.Current.Border, Margin = new Padding(4, 2, 8, 0) };

        public static DataGridView Grid()
        {
            var g = new DataGridView
            {
                Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false, ReadOnly = true, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, MultiSelect = true, AllowUserToResizeRows = false
            };
            Theme.StyleGrid(g, Theme.Current);
            return g;
        }
    }

    /// <summary>
    /// Exposes public fields and properties of any object to the PropertyGrid (option classes in the engine use public
    /// fields). Names are split into words; fields of nested option objects are expandable.
    /// </summary>
    public class GridAdapter : ICustomTypeDescriptor
    {
        public readonly object Target;
        public readonly string Category;
        public GridAdapter(object target, string category = "Settings") { Target = target; Category = category; }

        public PropertyDescriptorCollection GetProperties(Attribute[] attributes) => GetProperties();

        public PropertyDescriptorCollection GetProperties()
        {
            var list = new List<PropertyDescriptor>();
            var t = Target.GetType();
            foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
                if (Editable(f.FieldType)) list.Add(new MemberDescriptor(f.Name, f.FieldType, () => f.GetValue(Target), v => f.SetValue(Target, v), Category));
            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                if (p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0 && Editable(p.PropertyType))
                    list.Add(new MemberDescriptor(p.Name, p.PropertyType, () => p.GetValue(Target, null), v => p.SetValue(Target, v, null), Category));
            return new PropertyDescriptorCollection(list.ToArray());
        }

        private static bool Editable(Type t)
        {
            var u = Nullable.GetUnderlyingType(t) ?? t;
            return u.IsPrimitive || u.IsEnum || u == typeof(string) || u == typeof(double) || u == typeof(decimal) || u == typeof(DateTime) || u == typeof(Color)
                || (u.IsClass && u.Namespace != null && u.Namespace.StartsWith("Sidralune") && !typeof(System.Collections.IEnumerable).IsAssignableFrom(u));
        }

        public static string Words(string name)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < name.Length; i++)
            {
                if (i > 0 && char.IsUpper(name[i]) && !char.IsUpper(name[i - 1])) sb.Append(' ');
                sb.Append(name[i]);
            }
            return sb.ToString();
        }

        private class MemberDescriptor : PropertyDescriptor
        {
            private readonly Type _type; private readonly Func<object> _get; private readonly Action<object> _set; private readonly string _cat;
            public MemberDescriptor(string name, Type type, Func<object> get, Action<object> set, string cat) : base(name, null) { _type = type; _get = get; _set = set; _cat = cat; }
            public override string DisplayName => Words(Name);
            public override string Category => _cat;
            public override Type ComponentType => typeof(GridAdapter);
            public override bool IsReadOnly => false;
            public override Type PropertyType => _type;
            public override bool CanResetValue(object c) => false;
            public override object GetValue(object c)
            {
                var v = _get();
                if (v != null && _type.IsClass && _type != typeof(string) && _type.Namespace != null && _type.Namespace.StartsWith("Sidralune")) return new GridAdapter(v, Words(Name));
                return v;
            }
            public override void SetValue(object c, object v) { if (v is GridAdapter) return; _set(v); OnValueChanged(c, EventArgs.Empty); }
            public override void ResetValue(object c) { }
            public override bool ShouldSerializeValue(object c) => false;
            public override TypeConverter Converter => _type.IsClass && _type != typeof(string) && _type.Namespace != null && _type.Namespace.StartsWith("Sidralune") ? new ExpandableObjectConverter() : base.Converter;
        }

        public AttributeCollection GetAttributes() => AttributeCollection.Empty;
        public string GetClassName() => Target.GetType().Name;
        public string GetComponentName() => null;
        public TypeConverter GetConverter() => new ExpandableObjectConverter();
        public EventDescriptor GetDefaultEvent() => null;
        public PropertyDescriptor GetDefaultProperty() => null;
        public object GetEditor(Type editorBaseType) => null;
        public EventDescriptorCollection GetEvents(Attribute[] attributes) => EventDescriptorCollection.Empty;
        public EventDescriptorCollection GetEvents() => EventDescriptorCollection.Empty;
        public object GetPropertyOwner(PropertyDescriptor pd) => this;
        public override string ToString() => "";
    }
}

namespace Sidralune.UI
{
    /// <summary>Themed date/time entry (local time, "yyyy-MM-dd HH:mm[:ss]"); raises ValueCommitted on Enter or focus loss.</summary>
    public class TimeField : TextBox
    {
        public event Action<DateTime> ValueCommitted;
        private DateTime _value = DateTime.Now;
        public string Format = "yyyy-MM-dd HH:mm";

        public TimeField() { Width = 140; Text = _value.ToString(Format); }

        public DateTime Value
        {
            get { return _value; }
            set { _value = value; if (!Focused) Text = value.ToString(Format); }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter) { Commit(); e.SuppressKeyPress = true; }
            if (e.KeyCode == Keys.Escape) { Text = _value.ToString(Format); e.SuppressKeyPress = true; }
            base.OnKeyDown(e);
        }

        protected override void OnLostFocus(EventArgs e) { Commit(); base.OnLostFocus(e); }

        private void Commit()
        {
            DateTime d;
            if (DateTime.TryParse(Text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out d))
            {
                if (d != _value) { _value = d; ValueCommitted?.Invoke(d); }
                Text = d.ToString(Format);
            }
            else Text = _value.ToString(Format);
        }
    }
}
