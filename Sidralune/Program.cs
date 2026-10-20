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
    internal static class Program
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
            Tasks.Run("Loading catalogs", t => AppState.LoadData(), () => MainForm.Instance?.Toast("Catalogs and ephemerides loaded"));
            form.Shown += (s, e) => form.Start();
            Application.Run(form);
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
            try { MainForm.Instance?.Toast("Unexpected error: " + ex.Message + " (details in the log)", Sidralune.UI.Icons.Warning, Sidralune.UI.Theme.Current.Danger); } catch { }
        }
    }
}
