using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using Sidralune.Core.Ephemeris;
using Sidralune.Core.Mathematics;
using Sidralune.Core.Util;
using static Sidralune.Core.Util.Lang;

namespace Sidralune.UI
{
    /// <summary>
    /// A world map (equirectangular, so the poles stay in view) with an eclipse's lines: zoom with the wheel at the cursor
    /// or the buttons, drag to move, click a place to see the eclipse there. The base map is Natural Earth
    /// (<see cref="WorldMap"/>); colours follow the theme, night mode included.
    /// </summary>
    public class EclipseMap : Control, IThemed
    {
        private EclipseMapData _data;
        private WorldMap _world;
        private double _lon0, _lat0, _scale = 3; // the centre (degrees) and pixels per degree
        private Point _down, _last;
        private bool _dragging, _pressed;
        private GeoPoint? _cursor, _clicked;
        private Bitmap _visibility;
        private readonly Panel _popup = new Panel { Visible = false, Padding = new Padding(10, 8, 10, 8) };
        private readonly Label _popupText = new Label { AutoSize = true, Font = Theme.MonoFont, Location = new Point(10, 8) };
        private readonly FlatButton _addSite, _openMaps, _copy, _closePopup;
        private readonly FlatButton _zoomIn, _zoomOut, _fit, _goTo;
        // Go to coordinates: a field next to its button
        private readonly Panel _goPanel = new Panel { Visible = false };
        private readonly Label _goHint = new Label { AutoSize = true, Tag = "dim", Location = new Point(10, 8) };
        private readonly TextBox _goBox = new TextBox { Width = 300 };
        private readonly FlatButton _goButton;
        private readonly Timer _copied = new Timer { Interval = 1500 };
        // the place's elevation: from the elevation data when installed, or typed in; a short pause before recomputing
        private readonly Label _elevLabel = new Label { AutoSize = true }, _elevNote = new Label { AutoSize = true, Tag = "dim" };
        private readonly NumericUpDown _elevBox = new NumericUpDown { Width = 80, Minimum = -500, Maximum = 9000, Increment = 10, DecimalPlaces = 0, TextAlign = HorizontalAlignment.Right };
        private readonly Timer _elevPause = new Timer { Interval = 400 };
        private GeoPoint? _elevPlace;
        private bool _settingElev, _noteShown;

        /// <summary>A place was clicked (not dragged): show what happens there with <see cref="ShowInfo"/>.</summary>
        public event Action<GeoPoint> PlaceClicked;
        /// <summary>The elevation in the popup was changed: show the place again with <see cref="ElevationFor"/>'s new value.</summary>
        public event Action<GeoPoint> ElevationChanged;
        /// <summary>Add as site in the popup.</summary>
        public event Action<GeoPoint> AddSiteClicked;

        /// <summary>The active site, marked on the map.</summary>
        public GeoPoint? Site { get { return _site; } set { _site = value; if (_home != null) _home.Enabled = value.HasValue; } }
        public string SiteName
        {
            get { return _siteName; }
            set { _siteName = value; if (_home != null) _home.ToolTipText = string.IsNullOrEmpty(value) ? T("Your site") : T("Your site: {0}", value); }
        }
        /// <summary>The active site's elevation, metres: used in the popup for the site itself.</summary>
        public double? SiteElevation { get; set; }
        private GeoPoint? _site;
        private string _siteName;
        private readonly FlatButton _home;

        public EclipseMap()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            _addSite = new FlatButton(T("Add as site"), Icons.Location, (s, e) => { if (_clicked.HasValue) AddSiteClicked?.Invoke(_clicked.Value); });
            _addSite.AutoSizeToText();
            _openMaps = new FlatButton(T("Open in Google Maps"), Icons.OpenInNew, (s, e) => { if (_clicked.HasValue) OpenUrl?.Invoke(GoogleMapsUrl(_clicked.Value)); }) { ToolTipText = T("Show this place in Google Maps in the web browser") };
            _openMaps.AutoSizeToText();
            _copy = new FlatButton { Icon = Icons.Copy, Width = 30, Height = _addSite.Height, ToolTipText = T("Copy the latitude and longitude") };
            _copy.Click += (s, e) => CopyPlace();
            _copied.Tick += (s, e) => { _copied.Stop(); _copy.Icon = Icons.Copy; _copy.Invalidate(); };
            _closePopup = new FlatButton { Icon = Icons.Close, Width = 30, ToolTipText = T("Close") };
            _closePopup.Click += (s, e) => HideInfo();
            _elevLabel.Text = T("Elevation (m)");
            _elevBox.ValueChanged += (s, e) => { if (_settingElev) return; _noteShown = false; _elevNote.Visible = false; _elevPause.Stop(); _elevPause.Start(); };
            _elevBox.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; _elevPause.Stop(); RaiseElevation(); } };
            _elevPause.Tick += (s, e) => { _elevPause.Stop(); RaiseElevation(); };
            _popup.Controls.Add(_popupText); _popup.Controls.Add(_elevLabel); _popup.Controls.Add(_elevBox); _popup.Controls.Add(_elevNote);
            _popup.Controls.Add(_addSite); _popup.Controls.Add(_openMaps); _popup.Controls.Add(_copy); _popup.Controls.Add(_closePopup);
            _popup.Paint += (s, e) => { using (var p = new Pen(Theme.Current.BorderStrong)) e.Graphics.DrawRectangle(p, 0, 0, _popup.Width - 1, _popup.Height - 1); };
            Controls.Add(_popup);
            _zoomIn = OverlayButton(Icons.Zoom, T("Zoom in"), (s, e) => ZoomAt(2, new Point(Width / 2, Height / 2)));
            _zoomOut = OverlayButton(Icons.ZoomOut, T("Zoom out"), (s, e) => ZoomAt(0.5, new Point(Width / 2, Height / 2)));
            _fit = OverlayButton(Icons.Fit, T("Whole eclipse"), (s, e) => FitData());
            _goTo = OverlayButton(Icons.Target, T("Go to coordinates"), (s, e) => { if (_goPanel.Visible) HideGoTo(); else ShowGoTo(); });
            _home = OverlayButton(Icons.Home, T("Your site"), (s, e) => GoToSite());
            _home.Enabled = false; // until the site is known
            _zoomIn.Location = new Point(8, 8); _zoomOut.Location = new Point(8, 44); _fit.Location = new Point(8, 80); _goTo.Location = new Point(8, 116); _home.Location = new Point(8, 152);

            // the Go to field: a hint (or what was wrong) above it, Enter or Go to go, Escape to close
            _goButton = new FlatButton(T("Go"), Icons.Right, (s, e) => TryGoTo());
            _goButton.AutoSizeToText();
            _goBox.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; TryGoTo(); }
                else if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; HideGoTo(); }
            };
            _goPanel.Controls.Add(_goHint); _goPanel.Controls.Add(_goBox); _goPanel.Controls.Add(_goButton);
            _goPanel.Paint += (s, e) => { using (var p = new Pen(Theme.Current.BorderStrong)) e.Graphics.DrawRectangle(p, 0, 0, _goPanel.Width - 1, _goPanel.Height - 1); };
            Controls.Add(_goPanel);
            _world = WorldMap.Current;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _copied.Dispose(); _elevPause.Dispose(); }
            base.Dispose(disposing);
        }

        private FlatButton OverlayButton(string icon, string tip, EventHandler click)
        {
            var b = new FlatButton { Icon = icon, Width = 32, Height = 32, ToolTipText = tip };
            b.Click += click;
            Controls.Add(b);
            return b;
        }

        public void ApplyTheme(Palette p)
        {
            BackColor = p.Background;
            _popup.BackColor = p.SurfaceRaised;
            _popupText.ForeColor = p.Text; _popupText.BackColor = p.SurfaceRaised;
            _visibility?.Dispose(); _visibility = null;
            Invalidate();
        }

        /// <summary>The eclipse shown; the view moves to it.</summary>
        public EclipseMapData Data
        {
            get { return _data; }
            set { _data = value; _visibility?.Dispose(); _visibility = null; HideInfo(); FitData(); }
        }

        /// <summary>Read the base map again (after the Natural Earth datasets were installed or removed).</summary>
        public void ReloadWorld() { _world = WorldMap.Current; Invalidate(); }

        public double Scale => _scale;
        public double CenterLat => _lat0;
        public double CenterLon => _lon0;

        // ------------------------------------------------------------------ projection

        private double MinScale => Math.Max(1, Math.Min(Width / 360.0, Height / 180.0));
        private const double MaxScale = 2000; // about 55 m a pixel

        public PointF ToScreen(double lat, double lon) => new PointF((float)(Width / 2 + (lon - _lon0) * _scale), (float)(Height / 2 - (lat - _lat0) * _scale));

        public GeoPoint ToGeo(Point p) => new GeoPoint(_lat0 - (p.Y - Height / 2) / _scale, AstroMath180(_lon0 + (p.X - Width / 2) / _scale));

        private static double AstroMath180(double lon) { lon = ((lon + 180) % 360 + 360) % 360 - 180; return lon; }

        /// <summary>Shows a region (west, south, east, north in degrees; east may run past 180).</summary>
        public void ShowRegion(double west, double south, double east, double north)
        {
            if (Width <= 0 || Height <= 0) return;
            _scale = Math.Max(MinScale, Math.Min(MaxScale, Math.Min(Width / Math.Max(1, east - west), Height / Math.Max(1, north - south))));
            _lon0 = AstroMath180((west + east) / 2); _lat0 = (south + north) / 2;
            Clamp();
            Invalidate();
        }

        private void FitData()
        {
            if (_data != null) ShowRegion(_data.West, _data.South, _data.East, _data.North); else ShowRegion(-180, -90, 180, 90);
        }

        /// <summary>Zooms by a factor keeping the place under a point of the control where it is.</summary>
        public void ZoomAt(double factor, Point at)
        {
            var g = ToGeo(at);
            double gx = _lon0 + (at.X - Width / 2) / _scale; // unwrapped, so the place stays under the cursor
            _scale = Math.Max(MinScale, Math.Min(MaxScale, _scale * factor));
            _lon0 = AstroMath180(gx - (at.X - Width / 2) / _scale);
            _lat0 = g.Lat + (at.Y - Height / 2) / _scale;
            Clamp();
            PlacePopup();
            Invalidate();
        }

        private void Clamp()
        {
            double half = Height / 2 / _scale;
            _lat0 = half >= 90 ? 0 : Math.Max(-90 + half, Math.Min(90 - half, _lat0));
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (_scale < MinScale) { _scale = MinScale; }
            Clamp(); PlacePopup();
        }

        // ------------------------------------------------------------------ mouse

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            ZoomAt(e.Delta > 0 ? 1.25 : 0.8, e.Location);
            base.OnMouseWheel(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();
            if (e.Button == MouseButtons.Left) { _pressed = true; _dragging = false; _down = _last = e.Location; }
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_pressed)
            {
                if (!_dragging && (Math.Abs(e.X - _down.X) > 4 || Math.Abs(e.Y - _down.Y) > 4)) { _dragging = true; Cursor = Cursors.SizeAll; }
                if (_dragging)
                {
                    _lon0 = AstroMath180(_lon0 - (e.X - _last.X) / _scale);
                    _lat0 += (e.Y - _last.Y) / _scale;
                    Clamp(); PlacePopup();
                    _last = e.Location;
                }
            }
            _cursor = ToGeo(e.Location);
            Invalidate();
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (_pressed && !_dragging && e.Button == MouseButtons.Left) ClickAt(e.Location);
            else if (e.Button == MouseButtons.Right && !_dragging) RightClickAt(e.Location);
            _pressed = _dragging = false;
            Cursor = Cursors.Default;
            base.OnMouseUp(e);
        }

        /// <summary>A click at a point of the map: exactly the place there, even right next to the site (a neighbour's).</summary>
        public void ClickAt(Point at)
        {
            var p = ToGeo(at);
            if (Math.Abs(p.Lat) <= 90) { _clicked = p; PlaceClicked?.Invoke(p); }
        }

        /// <summary>A right click on or next to the site's marker or name: the site itself, at its exact place.</summary>
        public void RightClickAt(Point at)
        {
            var site = SiteAt(at);
            if (site.HasValue) { _clicked = site; PlaceClicked?.Invoke(site.Value); }
        }

        /// <summary>The site when a point is on its marker or name, else null.</summary>
        private GeoPoint? SiteAt(Point at)
        {
            if (!_site.HasValue) return null;
            var s = ToScreen(_site.Value.Lat, Nearest(_site.Value.Lon));
            double dx = at.X - s.X, dy = at.Y - s.Y;
            if (dx * dx + dy * dy <= 10 * 10) return _site;
            if (!string.IsNullOrEmpty(_siteName))
            {
                var sz = TextRenderer.MeasureText(_siteName, Theme.SmallFont);
                if (new RectangleF(s.X + 8, s.Y - 6, sz.Width, sz.Height).Contains(at)) return _site;
            }
            return null;
        }

        /// <summary>Goes to the active site and shows the eclipse there, with the site's own elevation.</summary>
        public void GoToSite() { if (_site.HasValue) GoTo(_site.Value); }

        /// <summary>The Your site button, for tests.</summary>
        public Rectangle SiteButtonBounds => _home.Bounds;
        public string SiteButtonTip => _home.ToolTipText;

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            ZoomAt(2, e.Location);
            base.OnMouseDoubleClick(e);
        }

        protected override void OnMouseLeave(EventArgs e) { _cursor = null; Invalidate(); base.OnMouseLeave(e); }

        // ------------------------------------------------------------------ the popup

        /// <summary>Shows text about a place in a box next to it, with Add as site.</summary>
        public void ShowInfo(GeoPoint at, string text)
        {
            _clicked = at;
            _popupText.Text = text;
            // the text on the left, the close button in its own column on the right, the elevation below, then Add as site,
            // Google Maps and Copy
            var sz = _popupText.PreferredSize;
            _popupText.Location = new Point(10, 8);
            _elevLabel.Size = _elevLabel.PreferredSize; _elevNote.Size = _elevNote.PreferredSize;
            int elevRow = 10 + _elevLabel.Width + 6 + _elevBox.Width + (_noteShown ? 8 + _elevNote.Width : 0) + 10;
            int width = Math.Max(Math.Max(10 + sz.Width + 8 + _closePopup.Width + 6, 8 + _addSite.Width + 6 + _openMaps.Width + 6 + _copy.Width + 10), elevRow);
            _closePopup.Location = new Point(width - _closePopup.Width - 6, 6);
            int top = Math.Max(8 + sz.Height, _closePopup.Bottom) + 8;
            _elevBox.Location = new Point(10 + _elevLabel.Width + 6, top);
            _elevLabel.Location = new Point(10, top + (_elevBox.Height - _elevLabel.Height) / 2);
            _elevNote.Location = new Point(_elevBox.Right + 8, top + (_elevBox.Height - _elevNote.Height) / 2);
            top = _elevBox.Bottom + 8;
            _addSite.Location = new Point(8, top);
            _openMaps.Location = new Point(_addSite.Right + 6, top);
            _copy.Location = new Point(_openMaps.Right + 6, top);
            _copy.Icon = Icons.Copy; _copied.Stop();
            _popup.Size = new Size(width, top + _addSite.Height + 8);
            _popup.Visible = true;
            _popup.BringToFront();
            PlacePopup();
            Invalidate();
        }

        public void HideInfo() { _popup.Visible = false; _clicked = null; _elevPlace = null; _elevPause.Stop(); Invalidate(); }

        /// <summary>
        /// The elevation to use for a place, in metres: for a new place its height from the elevation data when installed
        /// (noted beside the field), else 0; for the place already shown, what the field holds (it may have been typed in).
        /// </summary>
        public double ElevationFor(GeoPoint p)
        {
            if (_elevPlace.HasValue && _elevPlace.Value.Lat == p.Lat && _elevPlace.Value.Lon == p.Lon) return (double)_elevBox.Value;
            _elevPlace = p;
            _elevPause.Stop();
            // the site itself: its own elevation from Settings; elsewhere the elevation data's
            bool site = _site.HasValue && _site.Value.Lat == p.Lat && _site.Value.Lon == p.Lon && SiteElevation.HasValue;
            var source = site ? null : Elevation.Current;
            double? h = site ? SiteElevation : source?.MetresAt(p.Lat, p.Lon);
            _settingElev = true;
            _elevBox.Value = (decimal)Math.Max((double)_elevBox.Minimum, Math.Min((double)_elevBox.Maximum, Math.Round(h ?? 0)));
            _settingElev = false;
            _elevNote.Text = site ? T("your site") : h.HasValue ? T("from {0}", source.Source) : "";
            _noteShown = h.HasValue; _elevNote.Visible = _noteShown;
            return (double)_elevBox.Value;
        }

        /// <summary>The elevation in the popup, metres; setting it shows the place again at once (tests).</summary>
        public double ElevationMetres
        {
            get { return (double)_elevBox.Value; }
            set { _elevBox.Value = (decimal)value; _elevPause.Stop(); RaiseElevation(); }
        }

        /// <summary>"from ETOPO1" beside the elevation, or null when it was typed in or there is no elevation data.</summary>
        public string ElevationNote => _noteShown ? _elevNote.Text : null;
        public Rectangle InfoElevationBounds => Rectangle.Union(_elevLabel.Bounds, _elevBox.Bounds);

        private void RaiseElevation() { if (_clicked.HasValue && _popup.Visible) ElevationChanged?.Invoke(_clicked.Value); }

        /// <summary>The popup's text, close button and Add as site, in the popup's coordinates, for tests.</summary>
        public Rectangle InfoTextBounds => new Rectangle(_popupText.Location, _popupText.PreferredSize);
        public Rectangle InfoCloseBounds => _closePopup.Bounds;
        public Rectangle InfoAddSiteBounds => _addSite.Bounds;
        public Rectangle InfoOpenMapsBounds => _openMaps.Bounds;
        public Rectangle InfoCopyBounds => _copy.Bounds;
        public Size InfoSize => _popup.Size;
        public Rectangle InfoBounds => _popup.Bounds;

        /// <summary>Opens a link in the web browser; replaced in tests.</summary>
        public Action<string> OpenUrl { get; set; } = url => { try { System.Diagnostics.Process.Start(url); } catch { } };

        /// <summary>Google Maps at a place (its Maps URLs, which need no key): a pin there, the satellite view a click away.</summary>
        public static string GoogleMapsUrl(GeoPoint p) =>
            "https://www.google.com/maps/search/?api=1&query=" + p.Lat.ToString("0.######", CultureInfo.InvariantCulture) + "%2C" + p.Lon.ToString("0.######", CultureInfo.InvariantCulture);

        /// <summary>The clicked place's latitude and longitude to the clipboard, in decimal degrees as map sites read them.</summary>
        public void CopyPlace()
        {
            if (!_clicked.HasValue) return;
            try { Clipboard.SetText(_clicked.Value.ToDecimalString()); } catch (System.Runtime.InteropServices.ExternalException) { return; } // the clipboard busy
            // a tick on the button for a moment says it was copied
            _copy.Icon = Icons.Check; _copy.Invalidate();
            _copied.Stop(); _copied.Start();
        }

        // ------------------------------------------------------------------ go to coordinates

        /// <summary>Opens the Go to field, with the clipboard's text in it when that is a place.</summary>
        public void ShowGoTo()
        {
            _goHint.Text = T("Latitude and longitude, such as 58.4887, 13.3348 or 58°29′N 13°20′E");
            _goHint.ForeColor = Theme.Current.TextDim;
            GeoPoint p;
            string clip = null;
            try { if (Clipboard.ContainsText()) clip = Clipboard.GetText(); } catch (System.Runtime.InteropServices.ExternalException) { }
            if (clip != null && clip.Length < 2000 && GeoPoint.TryParse(clip, out p)) _goBox.Text = clip.Trim();
            LayoutGoTo();
            _goPanel.BackColor = BackColor;
            _goPanel.Visible = true;
            _goPanel.BringToFront();
            _goBox.Focus(); _goBox.SelectAll();
        }

        /// <summary>The field sized for its hint or message, beside its button.</summary>
        private void LayoutGoTo()
        {
            var hint = _goHint.PreferredSize;
            int w = Math.Max(hint.Width, _goBox.Width + 6 + _goButton.Width);
            _goBox.Location = new Point(10, 8 + hint.Height + 6);
            _goButton.Location = new Point(_goBox.Right + 6, _goBox.Top + (_goBox.Height - _goButton.Height) / 2);
            _goPanel.Size = new Size(10 + w + 10, Math.Max(_goBox.Bottom, _goButton.Bottom) + 10);
            _goPanel.Location = new Point(_goTo.Right + 6, _goTo.Top);
            _goPanel.Invalidate();
        }

        public void HideGoTo() { _goPanel.Visible = false; Focus(); }

        /// <summary>The Go to field, for tests.</summary>
        public bool GoToOpen => _goPanel.Visible;
        public string GoToText { get { return _goBox.Text; } set { _goBox.Text = value; } }
        public string GoToHint => _goHint.Text;
        public Rectangle GoToButtonBounds => _goTo.Bounds;
        public Rectangle GoToPanelBounds => _goPanel.Bounds;

        /// <summary>Goes to the place in the field, or says what it reads.</summary>
        public bool TryGoTo()
        {
            GeoPoint p;
            if (!GeoPoint.TryParse(_goBox.Text, out p))
            {
                _goHint.Text = T("Can't read that as a place. Try 58.4887, 13.3348 or 58°29′N 13°20′E");
                _goHint.ForeColor = Theme.Current.Danger;
                LayoutGoTo();
                _goBox.Focus(); _goBox.SelectAll();
                return false;
            }
            HideGoTo();
            GoTo(p);
            return true;
        }

        /// <summary>Centres the map on a place, zoomed in to see the country round it, and shows the eclipse there.</summary>
        public void GoTo(GeoPoint p)
        {
            _scale = Math.Max(MinScale, Math.Max(_scale, Math.Min(MaxScale, Math.Min(Width, Height) / 10.0))); // at least about 10° across
            _lon0 = p.Lon; _lat0 = p.Lat;
            Clamp();
            _clicked = p;
            Invalidate();
            PlaceClicked?.Invoke(p);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            // Ctrl+V on the map goes straight to a place on the clipboard; Ctrl+G opens the field
            if (keyData == (Keys.Control | Keys.V) && Focused)
            {
                GeoPoint p; string clip = null;
                try { if (Clipboard.ContainsText()) clip = Clipboard.GetText(); } catch (System.Runtime.InteropServices.ExternalException) { }
                if (clip != null && clip.Length < 2000 && GeoPoint.TryParse(clip, out p)) { GoTo(p); return true; }
            }
            if (keyData == (Keys.Control | Keys.G) && (Focused || ContainsFocus)) { ShowGoTo(); return true; }
            if (keyData == Keys.Home && Focused) { GoToSite(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        /// <summary>The popup's text, for tests.</summary>
        public string InfoText => _popup.Visible ? _popupText.Text : null;

        private void PlacePopup()
        {
            if (!_popup.Visible || !_clicked.HasValue) return;
            var p = ToScreen(_clicked.Value.Lat, Nearest(_clicked.Value.Lon));
            int x = (int)p.X + 14, y = (int)p.Y - _popup.Height / 2;
            if (x + _popup.Width > Width - 4) x = (int)p.X - 14 - _popup.Width;
            if (x < 4)
            {
                // no room on either side: below the place, or above it, so the marker stays in sight
                x = (int)p.X - _popup.Width / 2;
                y = p.Y + 14 + _popup.Height <= Height - 4 ? (int)p.Y + 14 : (int)p.Y - 14 - _popup.Height;
            }
            _popup.Location = new Point(Math.Max(4, Math.Min(Width - _popup.Width - 4, x)), Math.Max(4, Math.Min(Height - _popup.Height - 4, y)));
        }

        /// <summary>The copy of a longitude nearest the centre of the view.</summary>
        private double Nearest(double lon) { while (lon - _lon0 > 180) lon -= 360; while (lon - _lon0 < -180) lon += 360; return lon; }

        // ------------------------------------------------------------------ drawing

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var pal = Theme.Current;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var water = new SolidBrush(pal.Background)) g.FillRectangle(water, ClientRectangle);
            // the copies of the world that are in view
            var offsets = new List<double>();
            for (double k = -2; k <= 2; k++)
            {
                float left = ToScreen(0, -180 + 360 * k).X, right = ToScreen(0, 180 + 360 * k).X;
                if (right >= 0 && left <= Width) offsets.Add(360 * k);
            }
            using (var land = new SolidBrush(pal.SurfaceRaised))
            using (var water = new SolidBrush(pal.Background))
            using (var border = new Pen(pal.Border, 1f))
            using (var coast = new Pen(pal.BorderStrong, 1f))
            using (var grid = new Pen(Color.FromArgb(90, pal.ChartGrid), 1f))
            {
                foreach (double off in offsets)
                {
                    using (var path = Polygons(_world.Land, off)) { g.FillPath(land, path); g.DrawPath(coast, path); }
                    using (var path = Polygons(_world.Lakes, off)) { g.FillPath(water, path); g.DrawPath(coast, path); }
                    foreach (var line in _world.Borders) DrawLine(g, border, line, off);
                }
                DrawGraticule(g, grid);
            }
            if (_data != null)
            {
                if (!_data.Solar) DrawVisibility(g, offsets);
                foreach (double off in offsets) DrawEclipse(g, pal, off);
            }
            DrawPlaces(g, pal, offsets);
            if (Site.HasValue) Marker(g, pal.Accent, Site.Value, SiteName, true);
            if (_clicked.HasValue) Marker(g, pal.Warning, _clicked.Value, null, false);
            DrawLegend(g, pal);
            DrawReadout(g, pal);
        }

        private GraphicsPath Polygons(List<float[]> rings, double off)
        {
            var path = new GraphicsPath(FillMode.Alternate);
            foreach (var r in rings)
            {
                if (r.Length < 6) continue;
                var pts = new PointF[r.Length / 2];
                for (int i = 0; i < pts.Length; i++) pts[i] = ToScreen(r[2 * i + 1], r[2 * i] + off);
                path.AddPolygon(pts);
            }
            return path;
        }

        private void DrawLine(Graphics g, Pen pen, float[] line, double off)
        {
            if (line.Length < 4) return;
            var pts = new PointF[line.Length / 2];
            for (int i = 0; i < pts.Length; i++) pts[i] = ToScreen(line[2 * i + 1], line[2 * i] + off);
            g.DrawLines(pen, pts);
        }

        private void DrawLine(Graphics g, Pen pen, List<GeoPoint> line, double off)
        {
            if (line.Count < 2) return;
            g.DrawLines(pen, line.Select(p => ToScreen(p.Lat, p.Lon + off)).ToArray());
        }

        private void DrawGraticule(Graphics g, Pen pen)
        {
            double span = Width / _scale;
            double step = span > 180 ? 30 : span > 60 ? 10 : span > 20 ? 5 : span > 6 ? 1 : span > 2 ? 0.5 : 0.1;
            var tl = ToGeo(new Point(0, 0));
            double westEdge = _lon0 - Width / 2 / _scale, eastEdge = _lon0 + Width / 2 / _scale;
            for (double lon = Math.Floor(westEdge / step) * step; lon <= eastEdge; lon += step)
            {
                float x = ToScreen(0, lon).X;
                g.DrawLine(pen, x, Math.Max(0, ToScreen(90, 0).Y), x, Math.Min(Height, ToScreen(-90, 0).Y));
            }
            for (double lat = -90; lat <= 90; lat += step)
            {
                float y = ToScreen(lat, 0).Y;
                if (y >= 0 && y <= Height) g.DrawLine(pen, 0, y, Width, y);
            }
        }

        /// <summary>The colour and style of each kind of line.</summary>
        private static Pen LinePen(Palette pal, EclipseLineKind kind, int index = 0)
        {
            switch (kind)
            {
                case EclipseLineKind.CentralLine: return new Pen(pal.Series[0], 2f);
                case EclipseLineKind.CentralLimit: return new Pen(pal.Series[6], 2f);
                case EclipseLineKind.PenumbralLimit: return new Pen(pal.Series[2], 1.6f);
                case EclipseLineKind.ContactAtHorizon: return new Pen(pal.Series[3], 1.6f);
                case EclipseLineKind.MaximumAtHorizon: return new Pen(pal.Series[3], 1.4f) { DashStyle = DashStyle.Dash };
                case EclipseLineKind.Obscuration: return new Pen(pal.TextFaint, 1f) { DashStyle = DashStyle.Dot };
                default: return new Pen(pal.Series[(1 + index) % pal.Series.Length], 1.6f);
            }
        }

        private void DrawEclipse(Graphics g, Palette pal, double off)
        {
            int moon = 0;
            using (var font = new Font(Theme.SmallFont, FontStyle.Bold))
            using (var text = new SolidBrush(pal.Text))
                foreach (var line in _data.Lines)
                    using (var pen = LinePen(pal, line.Kind, line.Kind == EclipseLineKind.MoonHorizon ? moon++ : 0))
                        foreach (var part in line.Parts)
                        {
                            DrawLine(g, pen, part, off);
                            if (part.Count < 2) continue;
                            string label = line.Kind == EclipseLineKind.Obscuration ? (line.Level * 100).ToString("0", CultureInfo.InvariantCulture) + "%" : line.Kind == EclipseLineKind.MoonHorizon ? line.Label : null;
                            if (label == null) continue;
                            var mid = part[part.Count / 2];
                            var p = ToScreen(mid.Lat, mid.Lon + off);
                            using (var lb = new SolidBrush(pen.Color)) g.DrawString(label, font, lb, p.X + 3, p.Y - 14);
                        }
            if (_data.GreatestPoint.HasValue) Marker(g, pal.Series[0], _data.GreatestPoint.Value, null, false, off);
            foreach (var s in _data.SubLunar)
            {
                var p = ToScreen(s.Item2.Lat, s.Item2.Lon + off);
                using (var b = new SolidBrush(pal.Moon)) g.FillEllipse(b, p.X - 3, p.Y - 3, 6, 6);
                using (var b = new SolidBrush(pal.TextDim)) g.DrawString(s.Item1, Theme.SmallFont, b, p.X + 4, p.Y + 2);
            }
        }

        private string _visibilityKey;

        /// <summary>
        /// Where the Moon is up for the whole eclipse (darker) and for part of it, worked out for the view itself (every
        /// other pixel, smoothed) from the circles where the Moon is up at each contact, so its edges follow the lines at
        /// any zoom.
        /// </summary>
        private void DrawVisibility(Graphics g, List<double> offsets)
        {
            if (_data.MoonUp == null || _data.MoonUp.Count == 0 || Width < 2 || Height < 2) return;
            int w = (Width + 1) / 2, h = (Height + 1) / 2;
            var accent = Theme.Current.Accent;
            string key = string.Join("|", _lon0, _lat0, _scale, Width, Height, accent.ToArgb());
            if (_visibility == null || key != _visibilityKey)
            {
                _visibility?.Dispose();
                _visibility = ShadeView(w, h, accent);
                _visibilityKey = key;
            }
            var old = g.InterpolationMode;
            g.InterpolationMode = InterpolationMode.Bilinear;
            using (var attr = new ImageAttributes())
            {
                attr.SetWrapMode(WrapMode.TileFlipXY); // no faint seam at the bitmap's edges
                g.DrawImage(_visibility, new Rectangle(0, 0, w * 2, h * 2), 0, 0, w, h, GraphicsUnit.Pixel, attr);
            }
            g.InterpolationMode = old;
        }

        private Bitmap ShadeView(int w, int h, Color accent)
        {
            var caps = _data.MoonUp;
            int n = caps.Count;
            double[] cx = new double[n], cy = new double[n], cz = new double[n], cosR = new double[n];
            for (int i = 0; i < n; i++)
            {
                double f = caps[i].Item2.Lat * AstroMath.Deg2Rad, l = caps[i].Item2.Lon * AstroMath.Deg2Rad;
                cx[i] = Math.Cos(f) * Math.Cos(l); cy[i] = Math.Cos(f) * Math.Sin(l); cz[i] = Math.Sin(f);
                cosR[i] = Math.Cos(caps[i].Item3 * AstroMath.Deg2Rad);
            }
            int from = caps.FindIndex(c => c.Item1 == _data.WholeFrom), to = caps.FindIndex(c => c.Item1 == _data.WholeTo), max = caps.FindIndex(c => c.Item1 == "MAX");
            var cosL = new double[w]; var sinL = new double[w];
            for (int x = 0; x < w; x++)
            {
                double lon = (_lon0 + (2 * x + 1 - Width / 2.0) / _scale) * AstroMath.Deg2Rad;
                cosL[x] = Math.Cos(lon); sinL[x] = Math.Sin(lon);
            }
            int whole = Color.FromArgb(80, accent).ToArgb(), part = Color.FromArgb(36, accent).ToArgb();
            var px = new int[w * h];
            var up = new bool[n];
            for (int y = 0; y < h; y++)
            {
                double lat = _lat0 - (2 * y + 1 - Height / 2.0) / _scale;
                if (lat > 90 || lat < -90) continue;
                double f = lat * AstroMath.Deg2Rad, cf = Math.Cos(f), sf = Math.Sin(f);
                for (int x = 0; x < w; x++)
                {
                    double vx = cf * cosL[x], vy = cf * sinL[x];
                    bool any = false;
                    for (int i = 0; i < n; i++) { up[i] = vx * cx[i] + vy * cy[i] + sf * cz[i] > cosR[i]; any |= up[i]; }
                    if (!any) continue;
                    bool all = (from < 0 || up[from]) && (to < 0 || up[to]) && (max < 0 || up[max]);
                    px[y * w + x] = all ? whole : part;
                }
            }
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            for (int y = 0; y < h; y++) System.Runtime.InteropServices.Marshal.Copy(px, y * w, data.Scan0 + y * data.Stride, w);
            bmp.UnlockBits(data);
            return bmp;
        }

        private void DrawPlaces(Graphics g, Palette pal, List<double> offsets)
        {
            if (_world.Places.Count == 0) return;
            int maxRank = _scale < 3 ? 0 : _scale < 6 ? 2 : _scale < 12 ? 4 : _scale < 30 ? 6 : _scale < 80 ? 8 : 10;
            var taken = new List<RectangleF>();
            using (var dot = new SolidBrush(pal.TextDim))
            using (var text = new SolidBrush(pal.TextDim))
                foreach (var p in _world.Places)
                {
                    if (p.Rank > maxRank) break; // sorted by rank
                    foreach (double off in offsets)
                    {
                        var s = ToScreen(p.Lat, p.Lon + off);
                        if (s.X < 0 || s.Y < 0 || s.X > Width || s.Y > Height) continue;
                        var size = g.MeasureString(p.Name, Theme.SmallFont);
                        var r = new RectangleF(s.X + 4, s.Y - size.Height / 2, size.Width, size.Height);
                        if (taken.Any(t => t.IntersectsWith(r))) continue;
                        taken.Add(r);
                        g.FillEllipse(dot, s.X - 2, s.Y - 2, 4, 4);
                        g.DrawString(p.Name, Theme.SmallFont, text, r.Location);
                    }
                    if (taken.Count > 250) break;
                }
        }

        private void Marker(Graphics g, Color c, GeoPoint at, string name, bool site, double off = double.NaN)
        {
            var p = ToScreen(at.Lat, double.IsNaN(off) ? Nearest(at.Lon) : at.Lon + off);
            using (var pen = new Pen(c, 2f))
            {
                if (site) g.DrawPolygon(pen, new[] { new PointF(p.X, p.Y - 7), new PointF(p.X + 6, p.Y + 4), new PointF(p.X - 6, p.Y + 4) });
                else { g.DrawEllipse(pen, p.X - 5, p.Y - 5, 10, 10); g.DrawLine(pen, p.X - 8, p.Y, p.X + 8, p.Y); g.DrawLine(pen, p.X, p.Y - 8, p.X, p.Y + 8); }
            }
            if (!string.IsNullOrEmpty(name)) using (var b = new SolidBrush(c)) g.DrawString(name, Theme.SmallFont, b, p.X + 8, p.Y - 6);
        }

        /// <summary>What each line means, in the lower left corner.</summary>
        private void DrawLegend(Graphics g, Palette pal)
        {
            if (_data == null) return;
            var rows = new List<Tuple<Pen, string>>();
            Func<EclipseLineKind, bool> has = k => _data.Lines.Any(l => l.Kind == k && l.Parts.Count > 0);
            if (_data.Solar)
            {
                bool total = _data.Elements != null && _data.GreatestPoint.HasValue && _data.Elements.Local(_data.GreatestPoint.Value.Lat, _data.GreatestPoint.Value.Lon).Kind == 1;
                if (has(EclipseLineKind.CentralLine)) rows.Add(Tuple.Create(LinePen(pal, EclipseLineKind.CentralLine), T("Central line")));
                if (has(EclipseLineKind.CentralLimit)) rows.Add(Tuple.Create(LinePen(pal, EclipseLineKind.CentralLimit), total ? T("Limits of totality") : T("Limits of annularity")));
                if (has(EclipseLineKind.PenumbralLimit)) rows.Add(Tuple.Create(LinePen(pal, EclipseLineKind.PenumbralLimit), T("Limit of the partial eclipse")));
                if (has(EclipseLineKind.ContactAtHorizon)) rows.Add(Tuple.Create(LinePen(pal, EclipseLineKind.ContactAtHorizon), T("Begins or ends at sunrise or sunset")));
                if (has(EclipseLineKind.MaximumAtHorizon)) rows.Add(Tuple.Create(LinePen(pal, EclipseLineKind.MaximumAtHorizon), T("Maximum at sunrise or sunset")));
                if (has(EclipseLineKind.Obscuration)) rows.Add(Tuple.Create(LinePen(pal, EclipseLineKind.Obscuration), T("Share of the Sun covered at maximum")));
            }
            else
            {
                rows.Add(Tuple.Create(new Pen(Color.FromArgb(160, pal.Accent), 8f), T("The whole eclipse with the Moon up")));
                rows.Add(Tuple.Create(new Pen(Color.FromArgb(70, pal.Accent), 8f), T("Part of the eclipse with the Moon up")));
                rows.Add(Tuple.Create(LinePen(pal, EclipseLineKind.MoonHorizon), T("The Moon on the horizon at a contact")));
            }
            if (rows.Count == 0) return;
            var font = Theme.SmallFont;
            float lh = font.Height + 4, w = rows.Max(r => g.MeasureString(r.Item2, font).Width) + 46;
            var box = new RectangleF(8, Height - 8 - rows.Count * lh - 10, w, rows.Count * lh + 10);
            using (var bg = new SolidBrush(Color.FromArgb(220, pal.Surface))) g.FillRectangle(bg, box);
            using (var text = new SolidBrush(pal.Text))
                for (int i = 0; i < rows.Count; i++)
                {
                    float y = box.Top + 5 + i * lh + lh / 2;
                    g.DrawLine(rows[i].Item1, box.Left + 8, y, box.Left + 34, y);
                    g.DrawString(rows[i].Item2, font, text, box.Left + 40, y - font.Height / 2f);
                    rows[i].Item1.Dispose();
                }
        }

        /// <summary>The cursor's place and the map's source, in the lower right corner.</summary>
        private void DrawReadout(Graphics g, Palette pal)
        {
            var lines = new List<string>();
            if (_cursor.HasValue && Math.Abs(_cursor.Value.Lat) <= 90) lines.Add(FormatPlace(_cursor.Value));
            if (!string.IsNullOrEmpty(_world.Source)) lines.Add(_world.Source);
            if (lines.Count == 0) return;
            string s = string.Join("\n", lines);
            var size = g.MeasureString(s, Theme.SmallFont);
            var r = new RectangleF(Width - size.Width - 14, Height - size.Height - 12, size.Width + 8, size.Height + 4);
            using (var bg = new SolidBrush(Color.FromArgb(200, pal.Surface))) g.FillRectangle(bg, r);
            using (var b = new SolidBrush(pal.TextDim)) g.DrawString(s, Theme.SmallFont, b, r.X + 4, r.Y + 2);
        }

        /// <summary>A place as degrees and minutes, for example 16°49′ S 129°38′ E.</summary>
        public static string FormatPlace(GeoPoint p)
        {
            Func<double, string, string, string> f = (v, pos, neg) =>
            {
                double a = Math.Abs(v); int d = (int)a; int m = (int)Math.Round((a - d) * 60); if (m == 60) { d++; m = 0; }
                return d + "°" + m.ToString("00") + "′ " + (v >= 0 ? pos : neg);
            };
            return f(p.Lat, Tc("compass", "N"), Tc("compass", "S")) + "  " + f(p.Lon, Tc("compass", "E"), Tc("compass", "W"));
        }
    }
}
