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
using Sidralune.Imaging.ColorManagement;
using static Sidralune.Core.Util.Lang;

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
        /// <summary>The equipment profiles (rigs) and the one in use; see EquipmentProfile.</summary>
        public List<EquipmentProfile> Profiles = new List<EquipmentProfile>();
        public int ActiveProfile;
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
        /// <summary>The interface language: "" for the Windows display language, "en", a code with a .po file ("sv"), or the pseudo-language.</summary>
        public string Language = "";
        /// <summary>Constellation names and well-known object nicknames in the interface language, or Latin and English.</summary>
        public bool SkyNamesInLanguage = true;

        // colour management (Settings → Colour); profiles are a built-in id ("prophoto") or a file path
        public bool ColorManagement = true;
        public string WorkingProfile = "prophoto";
        public string RenderingIntent = "RelativeColorimetric";
        public string OnProfileMismatch = "Keep";
        public string OnMissingProfile = "LeaveUntagged";
        public bool EmbedProfiles = true;
        public bool Export8BitAsSrgb = false;
        public string ProofingProfile = "srgb";
        public string ProofingIntent = "RelativeColorimetric";
        /// <summary>The colour marking out-of-gamut pixels, as #RRGGBB.</summary>
        public string GamutWarningColor = "#808080";
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
                // the rig's reducer or Barlow, when the planner shows the rig's telescope
                var p = Profile;
                double factor = p != null && p.Telescope == t.Name && p.FocalFactor > 0 ? p.FocalFactor : 1;
                return new ImagingSystem { Name = c.Name + " on " + t.Name, ApertureMm = t.ApertureMm, FocalLengthMm = t.FocalMm * factor, CentralObstruction = t.Obstruction, PixelSizeUm = c.PixelUm, WidthPx = c.Width, HeightPx = c.Height, ReadNoiseE = c.ReadNoise, QuantumEfficiency = c.Qe, IsColor = c.Color };
            }
        }

        // ---------------------------------------------------------------- equipment profiles

        /// <summary>The equipment profile in use (the rig).</summary>
        public static EquipmentProfile Profile => Settings.Profiles.Count > 0 ? Settings.Profiles[Math.Max(0, Math.Min(Settings.ActiveProfile, Settings.Profiles.Count - 1))] : null;
        /// <summary>Raised when another profile is used, or the one in use changes.</summary>
        public static event EventHandler ProfileChanged;

        /// <summary>The profile's imaging telescope from the planner's list (the first when it is no longer there).</summary>
        public static TelescopeProfile ProfileTelescope
        {
            get
            {
                var p = Profile;
                return Settings.Telescopes.FirstOrDefault(t => p != null && t.Name == p.Telescope) ?? Settings.Telescopes.FirstOrDefault() ?? new TelescopeProfile();
            }
        }

        /// <summary>The profile's camera from the planner's list (the first when it is no longer there).</summary>
        public static CameraProfile ProfileCamera
        {
            get
            {
                var p = Profile;
                return Settings.Cameras.FirstOrDefault(c => p != null && c.Name == p.Camera) ?? Settings.Cameras.FirstOrDefault() ?? new CameraProfile();
            }
        }

        /// <summary>
        /// Hands the profile in use to the observatory: the guider's own settings file, the imaging focal length and
        /// aperture, and the guide camera's focal length, which the guider takes from here. Switched to another profile,
        /// the planner shows the rig's telescope and camera too.
        /// </summary>
        public static void ApplyProfile(bool switched = false)
        {
            var p = Profile;
            if (p == null) return;
            var t = ProfileTelescope;
            var dm = DeviceManager.Instance;
            if (switched)
            {
                int ti = Settings.Telescopes.IndexOf(t), ci = Settings.Cameras.IndexOf(ProfileCamera);
                if (ti >= 0) Settings.ActiveTelescope = ti;
                if (ci >= 0) Settings.ActiveCamera = ci;
            }
            // refused only while the guider takes frames, and the Equipment tab does not switch profiles then
            try { dm.UseGuiderSettings(Path.Combine(AppPaths.Settings, p.GuiderFile)); }
            catch (Exception ex) { Log.Warn("Settings", "Guider settings of " + p.Name + ": " + ex.Message); }
            double fl = p.ImagingFocalLength(t.FocalMm);
            dm.ApplyOptics(fl, t.ApertureMm, p.GuideFocalLength(fl));
            ProfileChanged?.Invoke(null, EventArgs.Empty);
        }

        /// <summary>A profile's name as shown: the first profile, not named yet, is "My equipment" in the interface language.</summary>
        public static string NameOf(EquipmentProfile p) => p == null || string.IsNullOrWhiteSpace(p.Name) ? T("My equipment") : p.Name;

        /// <summary>A telescope or camera of the planner's lists was renamed: the profiles using it follow.</summary>
        public static void RenamedInPlanner(object item, string oldName, string newName)
        {
            foreach (var p in Settings.Profiles)
            {
                if (item is TelescopeProfile && p.Telescope == oldName) p.Telescope = newName;
                if (item is CameraProfile && p.Camera == oldName) p.Camera = newName;
            }
        }

        /// <summary>
        /// The first profile, from the setup so far: the planner's telescope and camera in use, a guide scope of the
        /// focal length the guider had, and the guider's settings file as it was.
        /// </summary>
        private static void FirstProfile()
        {
            if (Settings.Profiles == null) Settings.Profiles = new List<EquipmentProfile>();
            if (Settings.Profiles.Count > 0) return;
            var t = Settings.Telescopes.Count > 0 ? Settings.Telescopes[Math.Max(0, Math.Min(Settings.ActiveTelescope, Settings.Telescopes.Count - 1))] : null;
            var c = Settings.Cameras.Count > 0 ? Settings.Cameras[Math.Max(0, Math.Min(Settings.ActiveCamera, Settings.Cameras.Count - 1))] : null;
            double guideFocal = 240;
            try { guideFocal = Devices.Guiding.GuiderSettings.Load().FocalLengthMm; } catch { }
            Settings.Profiles.Add(new EquipmentProfile
            {
                Name = "", Telescope = t != null ? t.Name : "", Camera = c != null ? c.Name : "",
                Guiding = GuideOptics.GuideScope, GuideFocalLengthMm = guideFocal, GuiderFile = "guider.json"
            });
            Settings.ActiveProfile = 0;
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
            FirstProfile();
            ApplyProfile();
            Core.Catalogs.SkyNames.InLanguage = Settings.SkyNamesInLanguage;
            ApplyColorSettings();
        }

        /// <summary>Hands the colour settings to the imaging code (and tells the image views to redraw).</summary>
        public static void ApplyColorSettings()
        {
            var s = Settings;
            ColorSettings.Enabled = s.ColorManagement;
            ColorSettings.WorkingProfile = IccProfiles.FromSetting(s.WorkingProfile);
            ColorSettings.Intent = ParseEnum(s.RenderingIntent, Imaging.ColorManagement.RenderingIntent.RelativeColorimetric);
            ColorSettings.OnMismatch = ParseEnum(s.OnProfileMismatch, ProfileMismatchPolicy.Keep);
            ColorSettings.OnMissing = ParseEnum(s.OnMissingProfile, ProfileMissingPolicy.LeaveUntagged);
            ColorSettings.EmbedProfiles = s.EmbedProfiles;
            ColorSettings.Export8BitAsSrgb = s.Export8BitAsSrgb;
            ColorSettings.ProofingProfile = IccProfiles.ProofingFromSetting(s.ProofingProfile);
            ColorSettings.ProofingIntent = ParseEnum(s.ProofingIntent, Imaging.ColorManagement.RenderingIntent.RelativeColorimetric);
            try { ColorSettings.GamutWarningColor = System.Drawing.ColorTranslator.FromHtml(s.GamutWarningColor ?? "#808080").ToArgb(); }
            catch { ColorSettings.GamutWarningColor = unchecked((int)0xFF808080); }
            ColorSettings.RaiseChanged();
        }

        private static T ParseEnum<T>(string s, T fallback) where T : struct
        {
            T v;
            return Enum.TryParse(s ?? "", out v) ? v : fallback;
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

    public enum SelectedKind { Star, DeepSky, Planet, MinorBody, Satellite, Coordinates, RadioSource, PlanetMoon }

    /// <summary>An object picked anywhere in the UI (sky chart, planner, search).</summary>
    public class SelectedObject
    {
        public SelectedKind Kind;
        public string Name;
        public double RaDeg, DecDeg; // J2000
        public SkyObject Catalog;
        public Body? Planet; // for a PlanetMoon, the planet it orbits
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

        /// <summary>A moon of a planet, placed by PlanetMoons.Places.</summary>
        public static SelectedObject From(Science.SolarSystem.MoonPlace m) =>
            new SelectedObject { Kind = SelectedKind.PlanetMoon, Name = m.Moon.Name, Planet = m.Moon.Planet, RaDeg = m.Place.Astrometric.RaHours * 15, DecDeg = m.Place.Astrometric.DecDeg, Mag = m.Mag, SizeArcmin = m.DiameterArcsec / 60 };
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
                try { StopRequest.Run(t.Cts.Token, () => work(t)); } // long loops anywhere end when the job is stopped
                catch (OperationCanceledException) { t.Status = T("Cancelled"); }
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
                        if (t.Error != null) { if (onError != null) onError(t.Error); else MainForm.Instance?.Toast(T("{0} failed: {1}", name, t.Error.Message), UI.Icons.Warning, UI.Theme.Current.Danger); }
                        else onDone?.Invoke();
                    });
                }
            });
            return t;
        }

        /// <summary>
        /// A job's progress. Shown once the interface is free: while an update is still waiting to be shown, more do not
        /// queue up behind it (a fast download reports every 64 KB, thousands of times, which used to stall the interface).
        /// </summary>
        public static void Report(BackgroundTask t, double progress, string status = null)
        {
            t.Progress = progress;
            if (status != null) t.Status = status;
            if (Interlocked.Exchange(ref _reportWaiting, 1) == 1) return; // the waiting update will show this too
            Post(() => { Interlocked.Exchange(ref _reportWaiting, 0); Changed?.Invoke(); });
        }

        private static int _reportWaiting;

        /// <summary>How many progress updates are waiting to be shown (0 or 1; tests).</summary>
        public static int ReportsWaiting => _reportWaiting;

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
        // the slots guarding each control: a control two slots guard is enabled only while both are idle
        private static readonly Dictionary<Control, List<JobSlot>> Guards = new Dictionary<Control, List<JobSlot>>();
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
            foreach (var c in controls.Where(c => c != null))
            {
                _controls.Add(c);
                List<JobSlot> slots;
                if (!Guards.TryGetValue(c, out slots)) Guards[c] = slots = new List<JobSlot>();
                if (!slots.Contains(this)) slots.Add(this);
            }
            Sync();
            return this;
        }

        /// <summary>
        /// A Stop button for this slot: available while a job runs that can be stopped (stoppable gets its name; any job
        /// when not given), it cancels the job and says so.
        /// </summary>
        public UI.FlatButton StopButton(Func<string, bool> stoppable = null)
        {
            var b = new UI.FlatButton(T("Stop"), UI.Icons.Stop) { Kind = UI.ButtonKind.Danger, Enabled = false };
            b.AutoSizeToText();
            b.Click += (s, e) =>
            {
                if (_job == null) return;
                MainForm.Instance?.Toast(T("{0} stopped", _job.Name), UI.Icons.Info);
                Cancel();
            };
            Changed += () => { if (!b.IsDisposed) b.Enabled = _job != null && (stoppable == null || stoppable(_job.Name)); };
            return b;
        }

        /// <summary>True, after telling the user, when a job is running; for actions that must wait for it.</summary>
        public bool Refuse()
        {
            if (_job == null) return false;
            MainForm.Instance?.Toast(T("{0} is still running. Wait for it to finish first.", _job.Name), UI.Icons.Info);
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

        /// <summary>Cancels the running job, and a Replace request waiting for it; the slot is free once the job has ended.</summary>
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
            foreach (var c in _controls) if (!c.IsDisposed) c.Enabled = IdleFor(c) && Availability.Holds(c);
            Changed?.Invoke();
        }

        /// <summary>Whether every slot guarding a control is idle (true for a control no slot guards).</summary>
        internal static bool IdleFor(Control c)
        {
            List<JobSlot> slots;
            return !Guards.TryGetValue(c, out slots) || slots.All(s => s._job == null);
        }
    }

    /// <summary>
    /// When buttons are available: greyed out while something elsewhere is busy with what they need (the camera, the mount,
    /// the guider), as well as while a JobSlot guarding them runs. Their Enabled is set here and by JobSlot only, so the two
    /// never disagree. Sync looks again after a change neither can see (a view calls it on its timer).
    /// </summary>
    public static class Availability
    {
        private static readonly Dictionary<Control, List<Func<bool>>> Conditions = new Dictionary<Control, List<Func<bool>>>();

        /// <summary>The control is also greyed out while available does not hold. Several conditions all must hold.</summary>
        public static void When(Control c, Func<bool> available)
        {
            List<Func<bool>> list;
            if (!Conditions.TryGetValue(c, out list)) Conditions[c] = list = new List<Func<bool>>();
            list.Add(available);
            Apply(c);
        }

        /// <summary>Whether a control's conditions all hold (true without any).</summary>
        internal static bool Holds(Control c)
        {
            List<Func<bool>> list;
            if (!Conditions.TryGetValue(c, out list)) return true;
            // a condition that cannot be told yet (a view still being built) does not grey the control out
            foreach (var f in list) { bool ok; try { ok = f(); } catch { ok = true; } if (!ok) return false; }
            return true;
        }

        private static void Apply(Control c)
        {
            if (!c.IsDisposed) c.Enabled = JobSlot.IdleFor(c) && Holds(c);
        }

        /// <summary>Sets every control with conditions again; call on the UI thread.</summary>
        public static void Sync()
        {
            foreach (var c in Conditions.Keys.ToList())
            {
                if (c.IsDisposed) { Conditions.Remove(c); continue; }
                Apply(c);
            }
        }
    }
}
