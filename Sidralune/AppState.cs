using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Sidralune.Core;
using Sidralune.Core.Catalogs;
using Sidralune.Core.Ephemeris;
using Sidralune.Core.MinorBodies;
using Sidralune.Core.Observing;
using Sidralune.Core.Time;
using Sidralune.Core.Util;
using Sidralune.Devices;

namespace Sidralune
{
    public class EyepieceProfile { public string Name = "25 mm Plössl"; public double FocalMm = 25; public double AfovDeg = 52; }
    public class TelescopeProfile { public string Name = "8\" f/6 Dobsonian"; public double ApertureMm = 203; public double FocalMm = 1200; public double Obstruction = 0.22; public double Transmission = 0.85; }
    public class CameraProfile { public string Name = "IMX571 APS-C"; public double PixelUm = 3.76; public int Width = 6248, Height = 4176; public double ReadNoise = 1.5; public double Qe = 0.8; public bool Color = false; }

    /// <summary>Persisted user settings.</summary>
    public class Settings
    {
        public List<Observer> Sites = new List<Observer>();
        public int ActiveSite = 0;
        public List<TelescopeProfile> Telescopes = new List<TelescopeProfile> { new TelescopeProfile(), new TelescopeProfile { Name = "80 mm f/6 APO", ApertureMm = 80, FocalMm = 480, Obstruction = 0, Transmission = 0.92 } };
        public List<EyepieceProfile> Eyepieces = new List<EyepieceProfile> { new EyepieceProfile(), new EyepieceProfile { Name = "10 mm 82°", FocalMm = 10, AfovDeg = 82 }, new EyepieceProfile { Name = "6 mm 68°", FocalMm = 6, AfovDeg = 68 }, new EyepieceProfile { Name = "32 mm 50°", FocalMm = 32, AfovDeg = 50 } };
        public List<CameraProfile> Cameras = new List<CameraProfile> { new CameraProfile(), new CameraProfile { Name = "IMX462 planetary", PixelUm = 2.9, Width = 1920, Height = 1080, ReadNoise = 0.8, Qe = 0.8, Color = true } };
        public int ActiveTelescope, ActiveEyepiece, ActiveCamera;
        public int ObserverAge = 40, ObserverExperience = 1;
        public bool NightMode;
        public string LastView = "home";
        public string ImageFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Sidralune");
        public string NovaApiKey = "";
        public string AavsoObserverCode = "";
        public string MpcStationCode = "500";
        public double UiScale = 1.0;
        public bool UseRefraction = true;
        public double SkyChartMagLimit = 6.5;
        /// <summary>Asteroid and comet labels on solved images: "All" in the field, those "Visible" (bright enough to show) or "None".</summary>
        public string MinorBodyLabels = "All";
    }

    /// <summary>Central application state shared by all views.</summary>
    public static class AppState
    {
        public static Settings Settings = new Settings();
        private static string SettingsPath => Path.Combine(AppPaths.Settings, "settings.json");

        public static Observer Site => Settings.Sites.Count > 0 ? Settings.Sites[Math.Max(0, Math.Min(Settings.ActiveSite, Settings.Sites.Count - 1))] : new Observer();
        public static event EventHandler SiteChanged;
        public static void NotifySiteChanged() { DeviceManager.Instance.Site = Site; SiteChanged?.Invoke(null, EventArgs.Empty); Save(); }

        // ---------------------------------------------------------------- time
        private static double _offsetDays;
        private static bool _paused;
        private static double _pausedJd;
        public static double TimeRate = 1.0;
        private static DateTime _rateStart = DateTime.UtcNow;
        private static double _rateStartJd = double.NaN;
        public static event EventHandler TimeChanged;

        /// <summary>Displayed/simulated time (real time unless the user changed it).</summary>
        public static AstroTime Now
        {
            get
            {
                if (_paused) return new AstroTime(_pausedJd);
                if (TimeRate != 1.0 && !double.IsNaN(_rateStartJd)) return new AstroTime(_rateStartJd + (DateTime.UtcNow - _rateStart).TotalDays * TimeRate);
                return AstroTime.Now.AddDays(_offsetDays);
            }
        }

        public static bool IsLive => !_paused && Math.Abs(_offsetDays) < 1e-9 && TimeRate == 1.0;
        public static void SetTime(AstroTime t) { _paused = true; _pausedJd = t.JdUtc; TimeRate = 1; TimeChanged?.Invoke(null, EventArgs.Empty); }
        public static void Step(double days) { var t = Now.AddDays(days); SetTime(t); }
        public static void GoLive() { _paused = false; _offsetDays = 0; TimeRate = 1; _rateStartJd = double.NaN; TimeChanged?.Invoke(null, EventArgs.Empty); }
        public static void Play(double rate) { _rateStartJd = Now.JdUtc; _rateStart = DateTime.UtcNow; _paused = false; TimeRate = rate; _offsetDays = 0; TimeChanged?.Invoke(null, EventArgs.Empty); }

        // ---------------------------------------------------------------- selection
        public static event Action<SelectedObject> ObjectSelected;
        public static SelectedObject Selection { get; private set; }
        public static void Select(SelectedObject o) { Selection = o; ObjectSelected?.Invoke(o); }

        // ---------------------------------------------------------------- instruments
        public static VisualInstrument Instrument
        {
            get
            {
                var t = Settings.Telescopes.Count > 0 ? Settings.Telescopes[Math.Min(Settings.ActiveTelescope, Settings.Telescopes.Count - 1)] : new TelescopeProfile();
                var e = Settings.Eyepieces.Count > 0 ? Settings.Eyepieces[Math.Min(Settings.ActiveEyepiece, Settings.Eyepieces.Count - 1)] : new EyepieceProfile();
                return new VisualInstrument
                {
                    Name = t.Name, ApertureMm = t.ApertureMm, FocalLengthMm = t.FocalMm, CentralObstruction = t.Obstruction, Transmission = t.Transmission,
                    EyepieceFocalMm = e.FocalMm, EyepieceAfovDeg = e.AfovDeg, TrueFieldDeg = e.AfovDeg / (t.FocalMm / e.FocalMm)
                };
            }
        }

        public static ObserverEye Eye => new ObserverEye { Age = Settings.ObserverAge, Experience = Settings.ObserverExperience };

        public static ImagingSystem ImagingRig
        {
            get
            {
                var t = Settings.Telescopes.Count > 0 ? Settings.Telescopes[Math.Min(Settings.ActiveTelescope, Settings.Telescopes.Count - 1)] : new TelescopeProfile();
                var c = Settings.Cameras.Count > 0 ? Settings.Cameras[Math.Min(Settings.ActiveCamera, Settings.Cameras.Count - 1)] : new CameraProfile();
                return new ImagingSystem { Name = c.Name + " on " + t.Name, ApertureMm = t.ApertureMm, FocalLengthMm = t.FocalMm, CentralObstruction = t.Obstruction, PixelSizeUm = c.PixelUm, WidthPx = c.Width, HeightPx = c.Height, ReadNoiseE = c.ReadNoise, QuantumEfficiency = c.Qe, IsColor = c.Color };
            }
        }

        // ---------------------------------------------------------------- persistence
        public static void Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    var d = Json.ParseObject(File.ReadAllText(SettingsPath));
                    var s = (Settings)Json.PopulateType(typeof(Settings), d);
                    if (s != null) Settings = s;
                }
            }
            catch (Exception ex) { Log.Warn("Settings", "Could not read settings: " + ex.Message); }
            if (Settings.Sites == null || Settings.Sites.Count == 0)
            {
                // a sensible default from the machine's time zone offset (longitude ≈ 15° × UTC offset)
                var off = TimeZoneInfo.Local.GetUtcOffset(DateTime.Now).TotalHours;
                Settings.Sites = new List<Observer> { new Observer { Name = "Home", LatitudeDeg = 52.0, LongitudeDeg = Math.Round(off * 15, 1), ElevationM = 20, SqmZenith = 20.8, Bortle = 5 } };
            }
            DeviceManager.Instance.Site = Site;
        }

        public static void Save()
        {
            try { File.WriteAllText(SettingsPath, Json.Serialize(Settings, true)); }
            catch (Exception ex) { Log.Warn("Settings", ex.Message); }
        }

        /// <summary>Load catalogs, ephemerides and orbit files from the data folders (call once at start-up in the background).</summary>
        public static void LoadData()
        {
            EphemerisManager.TryAutoLoad(AppPaths.Ephemerides);
            CatalogManager.Instance.LoadAll(AppPaths.Catalogs);
            MinorBodyStore.Instance.LoadFromFolder(AppPaths.Catalogs);
        }
    }

    public enum SelectedKind { Star, DeepSky, Planet, MinorBody, Satellite, Coordinates, RadioSource }

    /// <summary>An object picked anywhere in the UI (sky chart, planner, search).</summary>
    public class SelectedObject
    {
        public SelectedKind Kind;
        public string Name;
        public double RaDeg, DecDeg; // J2000
        public SkyObject Catalog;
        public Body? Planet;
        public MinorBody Minor;
        public double Mag = double.NaN;
        public double SizeArcmin = double.NaN;
        public override string ToString() => Name;

        public static SelectedObject From(SkyObject o)
        {
            var s = new SelectedObject { Kind = o is Star ? SelectedKind.Star : SelectedKind.DeepSky, Name = o.DisplayName, RaDeg = o.Ra * 180 / Math.PI, DecDeg = o.Dec * 180 / Math.PI, Catalog = o, Mag = o.Mag };
            var d = o as DeepSkyObject;
            if (d != null && !double.IsNaN(d.MajorAxis)) s.SizeArcmin = double.IsNaN(d.MinorAxis) ? d.MajorAxis : Math.Sqrt(d.MajorAxis * d.MinorAxis);
            return s;
        }
    }

    public class BackgroundTask
    {
        public string Name;
        public double Progress = double.NaN;
        public string Status = "";
        public CancellationTokenSource Cts = new CancellationTokenSource();
        public DateTime Started = DateTime.Now;
        public bool Done;
        public Exception Error;
    }

    /// <summary>Runs long operations off the UI thread with progress, cancellation and error reporting.</summary>
    public static class Tasks
    {
        /// <summary>Loading, downloading and reloading the catalogues and ephemerides: one at a time, app-wide.</summary>
        public static readonly JobSlot Data = new JobSlot();

        public static readonly List<BackgroundTask> Running = new List<BackgroundTask>();
        public static event Action Changed;
        public static Control UiContext;

        /// <summary>
        /// Runs work off the UI thread. On the UI thread afterwards: onFinished always, then, unless it was cancelled, onDone
        /// when it succeeded or onError (a notification when not given) when it failed.
        /// </summary>
        public static BackgroundTask Run(string name, Action<BackgroundTask> work, Action onDone = null, Action<Exception> onError = null, Action onFinished = null)
            => Start(new BackgroundTask { Name = name }, work, onDone, onError, onFinished);

        internal static BackgroundTask Start(BackgroundTask t, Action<BackgroundTask> work, Action onDone, Action<Exception> onError, Action onFinished)
        {
            string name = t.Name;
            lock (Running) Running.Add(t);
            Changed?.Invoke();
            Task.Run(() =>
            {
                try { work(t); }
                catch (OperationCanceledException) { t.Status = "Cancelled"; }
                catch (Exception ex) { t.Error = ex; Log.Error(name, ex); }
                finally
                {
                    t.Done = true;
                    lock (Running) Running.Remove(t);
                    Post(() =>
                    {
                        Changed?.Invoke();
                        onFinished?.Invoke();
                        if (t.Cts.IsCancellationRequested) return; // neither the result nor the error of a cancelled job matters now
                        if (t.Error != null) { if (onError != null) onError(t.Error); else MainForm.Instance?.Toast(name + " failed: " + t.Error.Message, UI.Icons.Warning, UI.Theme.Current.Danger); }
                        else onDone?.Invoke();
                    });
                }
            });
            return t;
        }

        public static void Report(BackgroundTask t, double progress, string status = null)
        {
            t.Progress = progress;
            if (status != null) t.Status = status;
            Post(() => Changed?.Invoke());
        }

        public static void Post(Action a)
        {
            var c = UiContext;
            if (c == null || c.IsDisposed || !c.IsHandleCreated) { a(); return; }
            if (c.InvokeRequired) c.BeginInvoke(a); else a();
        }
    }

    /// <summary>
    /// The background job of one part of a view. Run allows one job at a time: while it runs, the controls handed to
    /// Guard are disabled and another start is refused with a message, so nothing the job works on can change under it.
    /// Replace suits recalculations that follow what you change: it cancels the job still running, whose result is then
    /// dropped, so an older result never overwrites a newer one. The new job starts when the cancelled one has ended, so
    /// only one runs however often Replace is called; requests made meanwhile are dropped except the newest.
    /// </summary>
    public sealed class JobSlot
    {
        private readonly List<Control> _controls = new List<Control>();
        private BackgroundTask _job;
        private Action _next; // the newest Replace request, waiting for the job it cancelled to end

        /// <summary>The running job's name, or null.</summary>
        public string Name => _job?.Name;
        public bool Busy => _job != null;
        public BackgroundTask Current => _job;

        /// <summary>Raised on the UI thread when a job starts or finishes.</summary>
        public event Action Changed;

        /// <summary>Controls disabled while a job runs: those that start one, and those that change what it works on.</summary>
        public JobSlot Guard(params Control[] controls)
        {
            _controls.AddRange(controls.Where(c => c != null));
            Sync();
            return this;
        }

        /// <summary>True, after telling the user, when a job is running; for actions that must wait for it.</summary>
        public bool Refuse()
        {
            if (_job == null) return false;
            MainForm.Instance?.Toast(_job.Name + " is still running. Wait for it to finish first.", UI.Icons.Info);
            return true;
        }

        /// <summary>Starts a job unless one is running (then the user is told and null returned).</summary>
        public BackgroundTask Run(string name, Action<BackgroundTask> work, Action onDone = null, Action<Exception> onError = null)
            => Refuse() ? null : Start(name, work, onDone, onError);

        /// <summary>
        /// Starts a job, cancelling the one running: only the newest result is used. With a job running, the new one waits
        /// for it to end (and null is returned), so cancelled jobs that are slow to stop do not pile up.
        /// </summary>
        public BackgroundTask Replace(string name, Action<BackgroundTask> work, Action onDone = null, Action<Exception> onError = null)
        {
            if (_job == null) return Start(name, work, onDone, onError);
            _job.Cts.Cancel();
            _next = () => Start(name, work, onDone, onError);
            return null;
        }

        /// <summary>Cancels the running job, and a Replace request waiting for it.</summary>
        public void Cancel() { _next = null; if (_job != null) _job.Cts.Cancel(); }

        private BackgroundTask Start(string name, Action<BackgroundTask> work, Action onDone, Action<Exception> onError)
        {
            var t = new BackgroundTask { Name = name };
            _job = t;
            Sync();
            return Tasks.Start(t, work, onDone, onError, () =>
            {
                if (_job != t) return;
                _job = null;
                var next = _next;
                _next = null;
                if (next != null) next(); else Sync();
            });
        }

        private void Sync()
        {
            foreach (var c in _controls) if (!c.IsDisposed) c.Enabled = _job == null;
            Changed?.Invoke();
        }
    }
}
