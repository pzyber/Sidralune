using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using Sidralune.Core.Util;

namespace Sidralune
{
    public static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => ReportCrash(e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => ReportCrash(e.ExceptionObject as Exception);
            Thread.CurrentThread.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;

            AppState.Load();
            Lang.Use(ArgValue(args, "--lang") ?? AppState.Settings.Language);
            var form = new MainForm();
            string shot = ArgValue(args, "--screenshot");
            if (shot != null)
            {
                // headless-ish rendering of a view to PNG for documentation and visual checks
                AppState.LoadData();
                var size = (ArgValue(args, "--size") ?? "1500x950").Split('x');
                form.Size = new Size(int.Parse(size[0]), int.Parse(size[1]));
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(-form.Width - 100, 0);
                form.ShowInTaskbar = false;
                form.Show();
                if (args.Contains("--night")) Sidralune.UI.Theme.SetNightMode(true);
                if (args.Contains("--sim")) Devices.DeviceManager.Instance.ConnectSimulators();
                var tArg = ArgValue(args, "--time");
                if (tArg != null) AppState.SetTime(Core.Time.AstroTime.FromDateTime(DateTime.Parse(tArg, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal)));
                form.Navigate(ArgValue(args, "--view") ?? "home");
                var script = ArgValue(args, "--script");
                if (script != null) { form.Navigate("scripting"); form.GetView<Views.ScriptingView>("scripting").RunScript(File.ReadAllText(script)); }
                var until = DateTime.Now.AddSeconds(double.Parse(ArgValue(args, "--wait") ?? "4", System.Globalization.CultureInfo.InvariantCulture));
                while (DateTime.Now < until) { Application.DoEvents(); Thread.Sleep(30); }
                using (var bmp = new Bitmap(form.Width, form.Height))
                {
                    form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));
                    // DrawToBitmap paints child controls in reverse z-order, so floating controls on top of the layout
                    // (themed scroll bars, notifications) end up hidden; draw them again, back to front
                    var offset = form.PointToScreen(Point.Empty);
                    var origin = new Point(offset.X - form.Left, offset.Y - form.Top);
                    for (int i = form.Controls.Count - 1; i >= 0; i--)
                    {
                        var c = form.Controls[i];
                        if (!c.Visible || c.Dock != DockStyle.None || c.Width <= 0 || c.Height <= 0) continue;
                        using (var part = new Bitmap(c.Width, c.Height))
                        using (var g = Graphics.FromImage(bmp))
                        {
                            c.DrawToBitmap(part, new Rectangle(0, 0, c.Width, c.Height));
                            g.DrawImage(part, origin.X + c.Left, origin.Y + c.Top);
                        }
                    }
                    bmp.Save(shot, ImageFormat.Png);
                }
                form.Close();
                return;
            }
            // load catalogs and ephemerides in the background so the window appears immediately
            Tasks.Data.Run(Lang.T("Loading catalogs"), t => AppState.LoadData(), () => MainForm.Instance?.Toast(Lang.T("Catalogs and ephemerides loaded")));
            form.Shown += (s, e) => form.Start();
            Application.Run(form);
            if (RestartRequested) StartAgain(args);
        }

        /// <summary>Set by Restart: when the window has closed (and saved the settings), the program starts again.</summary>
        public static bool RestartRequested { get; private set; }

        /// <summary>Closes the window and starts Sidralune again, for example to show a newly chosen language.</summary>
        public static void Restart()
        {
            RestartRequested = true;
            MainForm.Instance.Close();
        }

        // started directly rather than through the shell: from a network drive the shell's security check can cancel the start
        private static void StartAgain(string[] args)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Application.ExecutablePath, RestartArguments(args))
                {
                    UseShellExecute = false, WorkingDirectory = Environment.CurrentDirectory
                });
            }
            catch (Exception ex) { Log.Error("Restart", ex.ToString()); }
        }

        /// <summary>
        /// The command line for starting again: the same options, quoted where needed, without --lang so that the
        /// language chosen in Settings is used.
        /// </summary>
        public static string RestartArguments(string[] args)
        {
            var kept = new System.Collections.Generic.List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--lang") { i++; continue; }
                kept.Add(args[i]);
            }
            return string.Join(" ", kept.Select(Quote));
        }

        // a command-line argument as Windows reads it back: quoted when it has spaces or quotes, with quotes and the
        // backslashes before them escaped
        private static string Quote(string a)
        {
            if (a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return a;
            var sb = new System.Text.StringBuilder("\"");
            int slashes = 0;
            foreach (var ch in a)
            {
                if (ch == '\\') { slashes++; continue; }
                if (ch == '"') sb.Append('\\', slashes * 2 + 1).Append('"');
                else sb.Append('\\', slashes).Append(ch);
                slashes = 0;
            }
            return sb.Append('\\', slashes * 2).Append('"').ToString();
        }

        private static string ArgValue(string[] args, string key)
        {
            int i = Array.IndexOf(args, key);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        private static void ReportCrash(Exception ex)
        {
            if (ex == null) return;
            try
            {
                Log.Error("Crash", ex.ToString());
                File.WriteAllText(Path.Combine(AppPaths.Logs, "crash-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt"), ex.ToString());
            }
            catch { }
            try { MainForm.Instance?.Toast(Lang.T("Unexpected error: {0} (details in the log)", ex.Message),Sidralune.UI.Icons.Warning, Sidralune.UI.Theme.Current.Danger); } catch { }
        }
    }
}
