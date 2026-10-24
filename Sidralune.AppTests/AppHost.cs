using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sidralune.Core.Util;
using Sidralune.Devices;
using Sidralune.UI;

namespace Sidralune.AppTests
{
    /// <summary>
    /// The application under test: the real main window on a UI thread of its own (STA, with a message loop), off screen,
    /// with settings and data in a temporary folder so the user's own settings are never touched. Tests run code on the
    /// UI thread with <see cref="Ui"/> and wait for background work with <see cref="WaitFor"/>.
    /// </summary>
    [TestClass]
    public class AppHost
    {
        public static MainForm App { get; private set; }
        public static string Root { get; private set; }
        private static Thread _ui;

        [AssemblyInitialize]
        public static void Start(TestContext context)
        {
            Root = Path.Combine(Path.GetTempPath(), "SidraluneAppTests");
            try { if (Directory.Exists(Root)) Directory.Delete(Root, true); } catch { }
            AppPaths.Root = Path.Combine(Root, "AppData");
            // an exception on a background thread ends the test process, and Visual Studio 2015's runner then waits for
            // ever: keep a record of it so a stuck run can be explained
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "SidraluneAppTests-crash.txt"), DateTime.Now + Environment.NewLine + e.ExceptionObject + Environment.NewLine + Environment.NewLine); } catch { }
            };
            var ready = new ManualResetEvent(false);
            Exception error = null;
            _ui = new Thread(() =>
            {
                try
                {
                    // as Program.Main does
                    Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
                    try { Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false); } catch (InvalidOperationException) { }
                    AppState.Load();
                    AppState.Settings.ImageFolder = Path.Combine(Root, "Images");
                    AppState.LoadData();
                    App = new MainForm { StartPosition = FormStartPosition.Manual, Location = new Point(-3000, 0), ShowInTaskbar = false };
                    App.Shown += (s, e) => ready.Set();
                    Application.Run(App);
                }
                catch (Exception ex) { error = ex; ready.Set(); }
            }) { IsBackground = true, Name = "Sidralune UI" };
            _ui.SetApartmentState(ApartmentState.STA);
            _ui.Start();
            if (!ready.WaitOne(120000)) throw new TimeoutException("The main window did not open");
            if (error != null) throw new InvalidOperationException("The main window could not be created: " + error, error);
        }

        [AssemblyCleanup]
        public static void Stop()
        {
            try { DeviceManager.Instance.DisconnectAll(); } catch { }
            if (App != null && !App.IsDisposed) try { App.Invoke(new Action(App.Close)); } catch { }
            _ui?.Join(10000);
        }

        /// <summary>Run on the UI thread.</summary>
        public static void OnUi(Action a) => OnUi<object>(() => { a(); return null; });

        /// <summary>Run on the UI thread and return the result; a UI thread that does not answer fails the test instead of hanging the run.</summary>
        public static T OnUi<T>(Func<T> f)
        {
            if (!App.InvokeRequired) return f();
            var call = App.BeginInvoke(f);
            if (!call.AsyncWaitHandle.WaitOne(60000)) throw new TimeoutException("The UI thread did not answer within a minute (a deadlock or a modal dialog?)");
            return (T)App.EndInvoke(call);
        }

        /// <summary>Wait (off the UI thread) until a condition, evaluated on the UI thread, holds.</summary>
        public static bool WaitFor(Func<bool> cond, int ms = 5000)
        {
            var until = DateTime.UtcNow.AddMilliseconds(ms);
            while (DateTime.UtcNow < until)
            {
                if (OnUi(cond)) return true;
                Thread.Sleep(50);
            }
            return OnUi(cond);
        }

        /// <summary>Let the UI thread work for a while (layout, timers, posted updates).</summary>
        public static void Settle(int ms = 300) { Thread.Sleep(ms); OnUi(() => { }); }

        public static IEnumerable<Control> All(Control root)
        {
            foreach (Control c in root.Controls)
            {
                yield return c;
                foreach (var k in All(c)) yield return k;
            }
        }

        public static T Find<T>(Control root, Func<T, bool> pred = null) where T : Control => All(root).OfType<T>().FirstOrDefault(c => pred == null || pred(c));

        public static FlatButton Button(Control root, string text) => Find<FlatButton>(root, b => b.Text == text);

        /// <summary>Open a view and return it.</summary>
        public static T View<T>(string key) where T : Control
        {
            OnUi(() => App.Navigate(key));
            Settle();
            return OnUi(() => App.GetView<T>(key));
        }

        public static List<NavItem> AllViews => OnUi(() => ((NavRail)typeof(MainForm).GetField("_rail", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(App)).Items.ToList());

        /// <summary>Texts of the notifications on screen.</summary>
        public static List<string> Toasts() => OnUi(() => App.Controls.OfType<Panel>().Where(p => p.Width == 360 && p.Height == 54).SelectMany(p => p.Controls.OfType<Label>()).Select(l => l.Text).Where(t => t.Length > 2).ToList());

        public static int LogMark() => Log.Snapshot().Count;

        /// <summary>Errors logged since a mark (errors in views are caught and logged rather than thrown).</summary>
        public static List<string> ErrorsSince(int mark) => Log.Snapshot().Skip(mark).Where(e => e.Level == Log.Level.Error).Select(e => e.Source + ": " + e.Message).ToList();

        public static object Call(object target, string method, params object[] args) =>
            target.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance).Invoke(target, args);

        public static T Field<T>(object target, string field) =>
            (T)target.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance).GetValue(target);
    }
}
