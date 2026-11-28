using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using Sidralune.Imaging;

namespace Sidralune.UI
{
    /// <summary>
    /// A tone curve drawn in the palette: click to add a point, drag a point to move it, right-click or double-click a
    /// point to remove it. The end points stay at the left and right edges and move only up and down.
    /// </summary>
    public class CurveEditor : Control, IThemed
    {
        // the points in curve units, kept as given so they come back exactly
        private struct Pt { public double X, Y; public Pt(double x, double y) { X = x; Y = y; } }
        private readonly List<Pt> _pts = new List<Pt> { new Pt(0, 0), new Pt(1, 1) };
        private int _drag = -1;
        public event EventHandler CurveChanged;

        public CurveEditor()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            Height = 180; TabStop = true;
        }

        /// <summary>The points as x0, y0, x1, y1…, or null for the straight line.</summary>
        public double[] Points
        {
            get { return IsStraight ? null : _pts.SelectMany(p => new double[] { p.X, p.Y }).ToArray(); }
            set
            {
                _pts.Clear();
                if (value != null)
                    for (int i = 0; i + 1 < value.Length; i += 2) _pts.Add(new Pt(Clamp(value[i]), Clamp(value[i + 1])));
                Tidy();
                Invalidate();
            }
        }

        private bool IsStraight => _pts.Count == 2 && _pts[0].Y == 0 && _pts[1].Y == 1;

        /// <summary>Back to the straight line.</summary>
        public void Reset() { Points = null; CurveChanged?.Invoke(this, EventArgs.Empty); }

        public void ApplyTheme(Palette p) => Invalidate();

        private static double Clamp(double v) => v < 0 ? 0 : v > 1 ? 1 : v;

        // sorted, with the ends at x = 0 and x = 1
        private void Tidy()
        {
            var s = _pts.OrderBy(p => p.X).ToList();
            _pts.Clear(); _pts.AddRange(s);
            if (_pts.Count == 0 || _pts[0].X > 0) _pts.Insert(0, new Pt(0, _pts.Count > 0 ? _pts[0].Y : 0));
            if (_pts[_pts.Count - 1].X < 1) _pts.Add(new Pt(1, _pts.Count > 1 ? _pts[_pts.Count - 1].Y : 1));
            _pts[0] = new Pt(0, _pts[0].Y);
            _pts[_pts.Count - 1] = new Pt(1, _pts[_pts.Count - 1].Y);
        }

        private int Pad => (int)Math.Round(8 * DeviceDpi / 96.0);
        private Rectangle Box => new Rectangle(Pad, Pad, Math.Max(1, Width - 2 * Pad - 1), Math.Max(1, Height - 2 * Pad - 1));
        private PointF ToScreen(Pt p) { var b = Box; return new PointF((float)(b.X + p.X * b.Width), (float)(b.Bottom - p.Y * b.Height)); }
        private Pt ToCurve(Point s) { var b = Box; return new Pt(Clamp((s.X - b.X) / (double)b.Width), Clamp((b.Bottom - s.Y) / (double)b.Height)); }

        private int Hit(Point s)
        {
            float r = Pad;
            for (int i = 0; i < _pts.Count; i++)
            {
                var q = ToScreen(_pts[i]);
                if (Math.Abs(q.X - s.X) <= r && Math.Abs(q.Y - s.Y) <= r) return i;
            }
            return -1;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var p = Theme.Current; var g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : p.Background);
            var b = Box;
            using (var bg = new SolidBrush(p.SurfaceRaised)) g.FillRectangle(bg, b);
            using (var grid = new Pen(p.Border))
            {
                for (int i = 1; i < 4; i++)
                {
                    float x = b.X + b.Width * i / 4f, y = b.Y + b.Height * i / 4f;
                    g.DrawLine(grid, x, b.Y, x, b.Bottom);
                    g.DrawLine(grid, b.X, y, b.Right, y);
                }
                g.DrawRectangle(grid, b);
            }
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var diag = new Pen(p.TextFaint) { DashStyle = DashStyle.Dash }) g.DrawLine(diag, b.X, b.Bottom, b.Right, b.Y);
            var curve = new MonotoneCurve(_pts.SelectMany(q => new double[] { q.X, q.Y }).ToArray());
            var line = new PointF[Math.Max(2, b.Width + 1)];
            for (int i = 0; i < line.Length; i++)
            {
                double x = i / (double)(line.Length - 1);
                line[i] = ToScreen(new Pt(x, curve[x]));
            }
            var col = Enabled ? p.Accent : p.TextFaint;
            using (var pen = new Pen(col, 2f)) g.DrawLines(pen, line);
            float r = Pad * 0.6f;
            using (var fill = new SolidBrush(col))
            using (var ring = new Pen(Focused ? p.Focus : p.Surface, 1.5f))
                foreach (var q in _pts.Select(ToScreen))
                {
                    g.FillEllipse(fill, q.X - r, q.Y - r, 2 * r, 2 * r);
                    g.DrawEllipse(ring, q.X - r, q.Y - r, 2 * r, 2 * r);
                }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();
            int i = Hit(e.Location);
            if (e.Button == MouseButtons.Right) { Remove(i); return; }
            if (e.Button != MouseButtons.Left) return;
            if (i < 0)
            {
                var c = ToCurve(e.Location);
                if (c.X <= 0 || c.X >= 1) return;
                _pts.Add(c); Tidy();
                i = _pts.IndexOf(c);
                Invalidate();
                CurveChanged?.Invoke(this, EventArgs.Empty);
            }
            _drag = i;
            base.OnMouseDown(e);
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e) { Remove(Hit(e.Location)); base.OnMouseDoubleClick(e); }

        private void Remove(int i)
        {
            if (i <= 0 || i >= _pts.Count - 1) return; // the ends stay
            _pts.RemoveAt(i);
            _drag = -1;
            Invalidate();
            CurveChanged?.Invoke(this, EventArgs.Empty);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_drag >= 0 && e.Button == MouseButtons.Left)
            {
                var c = ToCurve(e.Location);
                double x;
                if (_drag == 0) x = 0;
                else if (_drag == _pts.Count - 1) x = 1;
                else
                {
                    // between its neighbours, never on them
                    double lo = _pts[_drag - 1].X + 0.01, hi = _pts[_drag + 1].X - 0.01;
                    x = Math.Max(lo, Math.Min(hi, c.X));
                }
                _pts[_drag] = new Pt(x, c.Y);
                Invalidate();
                CurveChanged?.Invoke(this, EventArgs.Empty);
            }
            Cursor = _drag >= 0 || Hit(e.Location) >= 0 ? Cursors.Hand : Cursors.Cross;
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e) { _drag = -1; base.OnMouseUp(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    }
}
