using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace Sidralune.UI
{
    public enum SeriesStyle { Line, Scatter, LineScatter, Step, Bars }

    public class PlotSeries
    {
        public string Name;
        public List<double> X = new List<double>(), Y = new List<double>(), YErr;
        public SeriesStyle Style = SeriesStyle.Line;
        public Color? Color;
        /// <summary>Colour taken from the palette when drawn, so the series follows theme switches (overrides Color).</summary>
        public Func<Palette, Color> ThemeColor;
        public float Width = 1.6f;
        public float MarkerSize = 4f;
        public bool Visible = true;
        public bool DashDot;
        public PlotSeries(string name, SeriesStyle style = SeriesStyle.Line) { Name = name; Style = style; }
        public void Add(double x, double y, double err = double.NaN) { X.Add(x); Y.Add(y); if (!double.IsNaN(err)) { if (YErr == null) YErr = new List<double>(); YErr.Add(err); } }
    }

    public class PlotBand { public double X0, X1; public Color Color; public Func<Palette, Color> ThemeColor; public string Label; }
    public class PlotMarker { public double X; public string Label; public Color? Color; public Func<Palette, Color> ThemeColor; public bool Horizontal; }

    public enum AxisFormat { Number, Time, Hours, Jd }

    /// <summary>2D plotting control.</summary>
    public class PlotControl : Control, IThemed
    {
        public readonly List<PlotSeries> Series = new List<PlotSeries>();
        public readonly List<PlotBand> Bands = new List<PlotBand>();
        public readonly List<PlotMarker> Markers = new List<PlotMarker>();
        public string Title, XLabel, YLabel;
        public bool InvertY;
        public AxisFormat XFormat = AxisFormat.Number;
        /// <summary>
        /// For AxisFormat.Time, X values are OADates (DateTime.ToOADate). Without a time zone they are clock times as they
        /// are; with one they are UTC and labelled with that zone's clock, so a change to or from daylight saving time
        /// neither folds the curves back (autumn) nor tears a gap in them (spring).
        /// </summary>
        public TimeZoneInfo TimeZone;
        public double? FixedXMin, FixedXMax, FixedYMin, FixedYMax;
        public bool ShowLegend = true;
        public event Action<double, double> PlotClick;
        public event Action<double, double> RangeSelected;
        public bool SelectRangeMode;
        private double _x0, _x1, _y0, _y1;
        private bool _zoomed;
        private Point? _dragStart, _dragEnd;
        private Point _mouse = new Point(-1, -1);
        private Rectangle _plot;

        public PlotControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            var menu = new ContextMenuStrip { Renderer = new DarkToolStripRenderer() };
            menu.Items.Add("Reset zoom", null, (s, e) => ResetZoom());
            menu.Items.Add("Copy image", null, (s, e) => { using (var b = Render(Width, Height)) Clipboard.SetImage(b); });
            menu.Items.Add("Save as PNG…", null, (s, e) => SavePng());
            menu.Items.Add("Export data as CSV…", null, (s, e) => ExportCsv());
            ContextMenuStrip = menu;
        }

        public void ApplyTheme(Palette p) => Invalidate();

        public PlotSeries AddSeries(string name, SeriesStyle style = SeriesStyle.Line, Color? color = null)
        {
            var s = new PlotSeries(name, style) { Color = color };
            Series.Add(s);
            return s;
        }

        /// <summary>Add a series coloured by a palette role (e.g. <c>p =&gt; p.Moon</c>), resolved each time the plot is drawn.</summary>
        public PlotSeries AddSeries(string name, SeriesStyle style, Func<Palette, Color> color)
        {
            var s = AddSeries(name, style);
            s.ThemeColor = color;
            return s;
        }

        public void Clear() { Series.Clear(); Bands.Clear(); Markers.Clear(); _zoomed = false; Invalidate(); }
        public void ResetZoom() { _zoomed = false; Invalidate(); }

        /// <summary>
        /// Shows the given ranges, as a dragged box does. A range narrower than a ten-billionth of its values (or than
        /// 1e-10) is widened to that around its middle: deeper than that the axis steps can no longer be told apart.
        /// </summary>
        public void ZoomTo(double x0, double x1, double y0, double y1)
        {
            Widen(ref x0, ref x1); Widen(ref y0, ref y1);
            _x0 = x0; _x1 = x1; _y0 = y0; _y1 = y1; _zoomed = true;
            Invalidate();
        }

        private static void Widen(ref double a, ref double b)
        {
            if (a > b) { var t = a; a = b; b = t; }
            double mid = (a + b) / 2, min = Math.Max(Math.Abs(mid) * 1e-10, 1e-10);
            if (b - a < min) { a = mid - min / 2; b = mid + min / 2; }
        }

        private void ComputeRange()
        {
            if (_zoomed) return;
            double xmin = double.MaxValue, xmax = double.MinValue, ymin = double.MaxValue, ymax = double.MinValue;
            foreach (var s in Series.Where(s => s.Visible))
                for (int i = 0; i < s.X.Count; i++)
                {
                    double x = s.X[i], y = s.Y[i];
                    if (double.IsNaN(x) || double.IsNaN(y) || double.IsInfinity(y)) continue;
                    double e = s.YErr != null && i < s.YErr.Count && !double.IsNaN(s.YErr[i]) ? s.YErr[i] : 0;
                    xmin = Math.Min(xmin, x); xmax = Math.Max(xmax, x); ymin = Math.Min(ymin, y - e); ymax = Math.Max(ymax, y + e);
                }
            if (xmin > xmax) { xmin = 0; xmax = 1; ymin = 0; ymax = 1; }
            if (xmax == xmin) { xmin -= 0.5; xmax += 0.5; }
            if (ymax == ymin) { ymin -= 0.5; ymax += 0.5; }
            double py = (ymax - ymin) * 0.06;
            _x0 = FixedXMin ?? xmin; _x1 = FixedXMax ?? xmax; _y0 = FixedYMin ?? ymin - py; _y1 = FixedYMax ?? ymax + py;
        }

        private double SxD(double x) => _plot.Left + (x - _x0) / (_x1 - _x0) * _plot.Width;
        private double SyD(double y) => InvertY ? _plot.Top + (y - _y0) / (_y1 - _y0) * _plot.Height : _plot.Bottom - (y - _y0) / (_y1 - _y0) * _plot.Height;
        private float Sx(double x) => (float)SxD(x);
        private float Sy(double y) => (float)SyD(y);

        // GDI+ fails with an OverflowException on coordinates far outside the plot, and zoomed in deeply the points around
        // the shown range are millions of pixels away: what is drawn is first cut to the plot and this much around it
        private const float Reach = 1000;
        private RectangleF Reachable => RectangleF.FromLTRB(_plot.Left - Reach, _plot.Top - Reach, _plot.Right + Reach, _plot.Bottom + Reach);
        private float ClampX(float x) => Math.Max(_plot.Left - Reach, Math.Min(_plot.Right + Reach, x));
        private float ClampY(float y) => Math.Max(_plot.Top - Reach, Math.Min(_plot.Bottom + Reach, y));
        private bool InReach(float x, float y) => x >= _plot.Left - Reach && x <= _plot.Right + Reach && y >= _plot.Top - Reach && y <= _plot.Bottom + Reach;

        /// <summary>Liang–Barsky: cuts the segment (x0, y0)–(x1, y1) to r; false when none of it is inside.</summary>
        private static bool ClipSegment(RectangleF r, ref double x0, ref double y0, ref double x1, ref double y1, out bool endCut)
        {
            endCut = false;
            if (double.IsNaN(x0 + y0 + x1 + y1) || double.IsInfinity(x0 + y0 + x1 + y1)) return false;
            double dx = x1 - x0, dy = y1 - y0, t0 = 0, t1 = 1;
            double[] p = { -dx, dx, -dy, dy }, q = { x0 - r.Left, r.Right - x0, y0 - r.Top, r.Bottom - y0 };
            for (int k = 0; k < 4; k++)
            {
                if (p[k] == 0) { if (q[k] < 0) return false; continue; }
                double t = q[k] / p[k];
                if (p[k] < 0) { if (t > t1) return false; if (t > t0) t0 = t; }
                else { if (t < t0) return false; if (t < t1) t1 = t; }
            }
            endCut = t1 < 1;
            double ax = x0 + t0 * dx, ay = y0 + t0 * dy;
            x1 = x0 + t1 * dx; y1 = y0 + t1 * dy; x0 = ax; y0 = ay;
            return true;
        }

        // adds the visible part of a segment to the polyline being built, drawing it when the line leaves the plot
        private void AddSegment(Graphics g, Pen pen, List<PointF> pts, double x0, double y0, double x1, double y1)
        {
            bool endCut;
            if (!ClipSegment(Reachable, ref x0, ref y0, ref x1, ref y1, out endCut)) { DrawPolyline(g, pen, pts); return; }
            if (pts.Count == 0) pts.Add(new PointF((float)x0, (float)y0));
            pts.Add(new PointF((float)x1, (float)y1));
            if (endCut) DrawPolyline(g, pen, pts);
        }

        private static void DrawPolyline(Graphics g, Pen pen, List<PointF> pts)
        {
            if (pts.Count > 1) g.DrawLines(pen, pts.ToArray());
            pts.Clear();
        }
        private double Ix(float sx) => _x0 + (sx - _plot.Left) / (double)_plot.Width * (_x1 - _x0);
        private double Iy(float sy) => InvertY ? _y0 + (sy - _plot.Top) / (double)_plot.Height * (_y1 - _y0) : _y0 + (_plot.Bottom - sy) / (double)_plot.Height * (_y1 - _y0);

        private static double NiceStep(double range, int target)
        {
            double raw = range / Math.Max(1, target), mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
            double n = raw / mag;
            return (n < 1.5 ? 1 : n < 3 ? 2 : n < 7 ? 5 : 10) * mag;
        }

        private string FormatX(double v, double step)
        {
            switch (XFormat)
            {
                case AxisFormat.Time: try { return Clock(v).ToString(step < 1 ? "HH:mm" : "MM-dd"); } catch { return ""; }
                case AxisFormat.Hours: { double h = ((v % 24) + 24) % 24; return ((int)h).ToString("00") + ":" + ((int)((h - (int)h) * 60)).ToString("00"); }
                case AxisFormat.Jd: return v.ToString(step < 0.01 ? "0.0000" : "0.00", CultureInfo.InvariantCulture);
            }
            return Fmt(v, step);
        }

        /// <summary>The clock time shown for an X value of a time axis.</summary>
        public DateTime Clock(double v)
        {
            var d = DateTime.FromOADate(v);
            return TimeZone == null ? d : TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(d, DateTimeKind.Utc), TimeZone);
        }

        /// <summary>
        /// Grid lines of a time axis: where the clock shows a whole multiple of the step. With a time zone these follow
        /// the local clock, so they stay on whole hours across a clock change (an autumn hour is then labelled twice).
        /// </summary>
        public List<double> TimeTicks(double x0, double x1, double step)
        {
            if (TimeZone == null) return LinearTicks(x0, x1, step);
            var ticks = new List<double>();
            // every quarter hour (zones are offset by whole quarter hours) or finer steps, kept where the local clock fits
            double scan = Math.Min(step, 0.25 / 24);
            double stepMin = Math.Round(step * 1440);
            for (double u = Math.Ceiling(x0 / scan - 1e-9) * scan; u <= x1 + 1e-12; u += scan)
            {
                var c = Clock(u);
                double min = Math.Round(c.TimeOfDay.TotalMinutes);
                bool on = stepMin < 1440 ? min % stepMin == 0 : min == 0 && (int)(c.Date - new DateTime(2000, 1, 1)).TotalDays % (int)(stepMin / 1440) == 0;
                if (on) ticks.Add(u);
            }
            return ticks;
        }

        private static List<double> LinearTicks(double x0, double x1, double step)
        {
            var ticks = new List<double>();
            if (!(step > 0) || x0 + step == x0) return ticks; // a step too small to add would never end
            for (double x = Math.Ceiling(x0 / step) * step; x <= x1 + 1e-12 && ticks.Count < 1000; x += step) ticks.Add(x);
            return ticks;
        }

        private static string Fmt(double v, double step)
        {
            if (Math.Abs(v) >= 1e5 || (Math.Abs(v) < 1e-3 && v != 0)) return v.ToString("0.##E0", CultureInfo.InvariantCulture);
            int dec = Math.Max(0, (int)Math.Ceiling(-Math.Log10(step) + 0.01));
            return v.ToString("F" + Math.Min(dec, 6), CultureInfo.InvariantCulture);
        }

        protected override void OnPaint(PaintEventArgs e) => Draw(e.Graphics, Width, Height, Theme.Current);

        public Bitmap Render(int w, int h)
        {
            var b = new Bitmap(w, h);
            using (var g = Graphics.FromImage(b)) Draw(g, w, h, Theme.Current);
            return b;
        }

        private void Draw(Graphics g, int w, int h, Palette p)
        {
            g.Clear(p.Surface);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            ComputeRange();
            int left = 64, right = 16, top = string.IsNullOrEmpty(Title) ? 12 : 34, bottom = 40;
            _plot = new Rectangle(left, top, Math.Max(10, w - left - right), Math.Max(10, h - top - bottom));
            using (var bg = new SolidBrush(p.ChartBackground)) g.FillRectangle(bg, _plot);
            if (!string.IsNullOrEmpty(Title)) TextRenderer.DrawText(g, Title, Theme.UiFontBold, new Point(left, 8), p.Text);
            var clip = g.Clip;
            // bands
            g.SetClip(_plot);
            foreach (var b in Bands)
            {
                float bx0 = ClampX(Sx(b.X0)), bx1 = ClampX(Sx(b.X1));
                using (var br = new SolidBrush(b.ThemeColor != null ? b.ThemeColor(p) : b.Color)) g.FillRectangle(br, Math.Min(bx0, bx1), _plot.Top, Math.Abs(bx1 - bx0), _plot.Height);
            }
            g.Clip = clip;
            // grid and ticks
            double xs = NiceStep(_x1 - _x0, Math.Max(2, _plot.Width / 90)), ys = NiceStep(_y1 - _y0, Math.Max(2, _plot.Height / 50));
            if (XFormat == AxisFormat.Time)
            {
                double hours = (_x1 - _x0) * 24;
                double[] steps = { 1.0 / 60, 5.0 / 60, 10.0 / 60, 0.25, 0.5, 1, 2, 3, 6, 12, 24, 48 };
                xs = steps.FirstOrDefault(s => hours / s <= Math.Max(2, _plot.Width / 90)) / 24;
                if (xs <= 0) xs = 2;
            }
            using (var gp = new Pen(p.ChartGrid) { DashStyle = DashStyle.Dot })
            {
                foreach (var x in XFormat == AxisFormat.Time ? TimeTicks(_x0, _x1, xs) : LinearTicks(_x0, _x1, xs))
                {
                    float sx = Sx(x);
                    g.DrawLine(gp, sx, _plot.Top, sx, _plot.Bottom);
                    string t = FormatX(x, xs);
                    var sz = TextRenderer.MeasureText(t, Theme.SmallFont);
                    TextRenderer.DrawText(g, t, Theme.SmallFont, new Point((int)sx - sz.Width / 2, _plot.Bottom + 4), p.ChartText);
                }
                foreach (var y in LinearTicks(_y0, _y1, ys))
                {
                    float sy = Sy(y);
                    g.DrawLine(gp, _plot.Left, sy, _plot.Right, sy);
                    string t = Fmt(y, ys);
                    var sz = TextRenderer.MeasureText(t, Theme.SmallFont);
                    TextRenderer.DrawText(g, t, Theme.SmallFont, new Point(_plot.Left - sz.Width - 4, (int)sy - sz.Height / 2), p.ChartText);
                }
            }
            using (var bp = new Pen(p.BorderStrong)) g.DrawRectangle(bp, _plot);
            if (!string.IsNullOrEmpty(XLabel)) TextRenderer.DrawText(g, XLabel, Theme.SmallFont, new Rectangle(_plot.Left, h - 18, _plot.Width, 16), p.TextDim, TextFormatFlags.HorizontalCenter);
            if (!string.IsNullOrEmpty(YLabel))
            {
                var st = g.Save();
                g.TranslateTransform(12, _plot.Top + _plot.Height / 2f);
                g.RotateTransform(-90);
                using (var br = new SolidBrush(p.TextDim)) g.DrawString(YLabel, Theme.SmallFont, br, -g.MeasureString(YLabel, Theme.SmallFont).Width / 2, -6);
                g.Restore(st);
            }
            // series
            g.SetClip(_plot);
            int si = 0;
            foreach (var s in Series)
            {
                var col = s.ThemeColor != null ? s.ThemeColor(p) : s.Color ?? p.Series[si % p.Series.Length];
                si++;
                if (!s.Visible || s.X.Count == 0) continue;
                using (var pen = new Pen(col, s.Width) { DashStyle = s.DashDot ? DashStyle.Dash : DashStyle.Solid })
                using (var br = new SolidBrush(col))
                using (var ep = new Pen(Theme.WithAlpha(col, 140), 1f))
                {
                    if (s.Style == SeriesStyle.Line || s.Style == SeriesStyle.LineScatter || s.Style == SeriesStyle.Step)
                    {
                        var pts = new List<PointF>();
                        double px = double.NaN, py = double.NaN; // the previous point, in pixels
                        for (int i = 0; i < s.X.Count; i++)
                        {
                            if (double.IsNaN(s.Y[i]) || double.IsNaN(s.X[i])) { DrawPolyline(g, pen, pts); px = double.NaN; continue; }
                            double x = SxD(s.X[i]), y = SyD(s.Y[i]);
                            if (!double.IsNaN(px))
                            {
                                if (s.Style == SeriesStyle.Step) { AddSegment(g, pen, pts, px, py, x, py); px = x; }
                                AddSegment(g, pen, pts, px, py, x, y);
                            }
                            px = x; py = y;
                        }
                        DrawPolyline(g, pen, pts);
                    }
                    if (s.Style == SeriesStyle.Bars)
                    {
                        float bw = s.X.Count > 1 ? Math.Min(_plot.Width, Math.Max(1, Math.Abs(Sx(s.X[1]) - Sx(s.X[0])) - 1)) : 4;
                        for (int i = 0; i < s.X.Count; i++)
                        {
                            float x = Sx(s.X[i]), y0 = ClampY(Sy(Math.Max(_y0, 0))), y1 = ClampY(Sy(s.Y[i]));
                            if (double.IsNaN(s.Y[i]) || !InReach(x, _plot.Top)) continue;
                            g.FillRectangle(br, x - bw / 2, Math.Min(y0, y1), bw, Math.Abs(y1 - y0));
                        }
                    }
                    if (s.Style == SeriesStyle.Scatter || s.Style == SeriesStyle.LineScatter)
                    {
                        float m = s.MarkerSize;
                        for (int i = 0; i < s.X.Count; i++)
                        {
                            if (double.IsNaN(s.Y[i])) continue;
                            float x = Sx(s.X[i]), y = Sy(s.Y[i]);
                            if (!InReach(x, _plot.Top)) continue;
                            if (s.YErr != null && i < s.YErr.Count && !double.IsNaN(s.YErr[i]))
                            {
                                float e1 = ClampY(Sy(s.Y[i] - s.YErr[i])), e2 = ClampY(Sy(s.Y[i] + s.YErr[i]));
                                if (e1 != e2) g.DrawLine(ep, x, e1, x, e2);
                            }
                            if (InReach(x, y)) g.FillEllipse(br, x - m / 2, y - m / 2, m, m);
                        }
                    }
                }
            }
            foreach (var mk in Markers)
            {
                var mc = mk.ThemeColor != null ? mk.ThemeColor(p) : mk.Color ?? p.Warning;
                using (var pen = new Pen(mc, 1.2f) { DashStyle = DashStyle.Dash })
                {
                    if (mk.Horizontal ? !InReach(_plot.Left, Sy(mk.X)) : !InReach(Sx(mk.X), _plot.Top)) continue;
                    if (mk.Horizontal) { float y = Sy(mk.X); g.DrawLine(pen, _plot.Left, y, _plot.Right, y); if (mk.Label != null) TextRenderer.DrawText(g, mk.Label, Theme.SmallFont, new Point(_plot.Left + 4, (int)y - 16), mc); }
                    else { float x = Sx(mk.X); g.DrawLine(pen, x, _plot.Top, x, _plot.Bottom); if (mk.Label != null) TextRenderer.DrawText(g, mk.Label, Theme.SmallFont, new Point((int)x + 3, _plot.Top + 2), mc); }
                }
            }
            if (_dragStart.HasValue && _dragEnd.HasValue)
            {
                var r = RectFrom(_dragStart.Value, _dragEnd.Value);
                using (var br = new SolidBrush(Theme.WithAlpha(p.Accent, 40))) g.FillRectangle(br, r);
                using (var pen = new Pen(p.Accent)) g.DrawRectangle(pen, r);
            }
            // hover readout
            if (_plot.Contains(_mouse) && !_dragStart.HasValue)
            {
                using (var pen = new Pen(Theme.WithAlpha(p.TextDim, 90))) { g.DrawLine(pen, _mouse.X, _plot.Top, _mouse.X, _plot.Bottom); g.DrawLine(pen, _plot.Left, _mouse.Y, _plot.Right, _mouse.Y); }
                string txt = FormatX(Ix(_mouse.X), (_x1 - _x0) / 1000) + ",  " + Fmt(Iy(_mouse.Y), (_y1 - _y0) / 1000);
                var near = Nearest(_mouse);
                if (near != null) txt = near;
                var sz = TextRenderer.MeasureText(txt, Theme.SmallFont);
                var tr = new Rectangle(Math.Min(_mouse.X + 10, _plot.Right - sz.Width - 6), Math.Max(_plot.Top + 4, _mouse.Y - sz.Height - 8), sz.Width + 6, sz.Height + 4);
                using (var br = new SolidBrush(Theme.WithAlpha(p.SurfaceRaised, 230))) g.FillRectangle(br, tr);
                TextRenderer.DrawText(g, txt, Theme.SmallFont, new Point(tr.X + 3, tr.Y + 2), p.Text);
            }
            g.Clip = clip;
            if (ShowLegend && Series.Count(s => !string.IsNullOrEmpty(s.Name)) > 1)
            {
                int ly = _plot.Top + 6, lx = _plot.Right - 8;
                int maxw = Series.Where(s => !string.IsNullOrEmpty(s.Name)).Max(s => TextRenderer.MeasureText(s.Name, Theme.SmallFont).Width) + 26;
                si = 0;
                foreach (var s in Series)
                {
                    var col = s.ThemeColor != null ? s.ThemeColor(p) : s.Color ?? p.Series[si % p.Series.Length]; si++;
                    if (string.IsNullOrEmpty(s.Name)) continue;
                    using (var br = new SolidBrush(Theme.WithAlpha(p.Surface, 200))) g.FillRectangle(br, lx - maxw, ly - 1, maxw, 16);
                    using (var br = new SolidBrush(s.Visible ? col : p.TextFaint)) g.FillRectangle(br, lx - maxw + 4, ly + 5, 14, 4);
                    TextRenderer.DrawText(g, s.Name, Theme.SmallFont, new Point(lx - maxw + 22, ly), s.Visible ? p.Text : p.TextFaint);
                    ly += 16;
                }
            }
        }

        private string Nearest(Point m)
        {
            double best = 12 * 12; string res = null;
            foreach (var s in Series.Where(s => s.Visible))
                for (int i = 0; i < s.X.Count; i += Math.Max(1, s.X.Count / 4000))
                {
                    if (double.IsNaN(s.Y[i])) continue;
                    double dx = Sx(s.X[i]) - m.X, dy = Sy(s.Y[i]) - m.Y, d = dx * dx + dy * dy;
                    if (d < best) { best = d; res = (s.Name != null ? s.Name + ": " : "") + FormatX(s.X[i], (_x1 - _x0) / 1000) + ", " + Fmt(s.Y[i], (_y1 - _y0) / 1000); }
                }
            return res;
        }

        private static Rectangle RectFrom(Point a, Point b) => new Rectangle(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left && _plot.Contains(e.Location)) _dragStart = e.Location; base.OnMouseDown(e); }
        protected override void OnMouseMove(MouseEventArgs e) { _mouse = e.Location; if (_dragStart.HasValue) _dragEnd = e.Location; Invalidate(); base.OnMouseMove(e); }
        protected override void OnMouseLeave(EventArgs e) { _mouse = new Point(-1, -1); Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (_dragStart.HasValue && e.Button == MouseButtons.Left)
            {
                var r = RectFrom(_dragStart.Value, e.Location);
                if (r.Width > 6 && (r.Height > 6 || SelectRangeMode))
                {
                    double ax = Ix(r.Left), bx = Ix(r.Right);
                    if (SelectRangeMode) RangeSelected?.Invoke(ax, bx);
                    else
                    {
                        ZoomTo(ax, bx, Iy(r.Top), Iy(r.Bottom));
                    }
                }
                else PlotClick?.Invoke(Ix(e.X), Iy(e.Y));
            }
            _dragStart = null; _dragEnd = null;
            Invalidate();
            base.OnMouseUp(e);
        }

        protected override void OnDoubleClick(EventArgs e) { ResetZoom(); base.OnDoubleClick(e); }

        private void SavePng()
        {
            using (var d = new SaveFileDialog { Filter = "PNG|*.png", FileName = (Title ?? "plot") + ".png" })
                if (d.ShowDialog() == DialogResult.OK) using (var b = Render(Math.Max(Width, 1200), Math.Max(Height, 700))) b.Save(d.FileName);
        }

        private void ExportCsv()
        {
            using (var d = new SaveFileDialog { Filter = "CSV|*.csv", FileName = (Title ?? "plot") + ".csv" })
            {
                if (d.ShowDialog() != DialogResult.OK) return;
                using (var w = new StreamWriter(d.FileName))
                {
                    foreach (var s in Series)
                    {
                        w.WriteLine("# " + s.Name);
                        w.WriteLine("x,y" + (s.YErr != null ? ",err" : ""));
                        for (int i = 0; i < s.X.Count; i++)
                            w.WriteLine(s.X[i].ToString("R", CultureInfo.InvariantCulture) + "," + s.Y[i].ToString("R", CultureInfo.InvariantCulture) + (s.YErr != null && i < s.YErr.Count ? "," + s.YErr[i].ToString("R", CultureInfo.InvariantCulture) : ""));
                    }
                }
            }
        }
    }
}
