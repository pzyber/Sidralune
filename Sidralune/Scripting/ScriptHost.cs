using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Microsoft.CSharp;
using Sidralune.Core;
using Sidralune.Core.Time;
using Sidralune.Devices;
using Sidralune.Imaging;
using Sidralune.UI;

namespace Sidralune.Scripting
{
    /// <summary>
    /// Base class of every user script. Scripts are C# method bodies compiled in-process with the C# compiler that
    /// ships with the .NET Framework (CodeDom), so no external tools are needed. Members here form the script API.
    /// </summary>
    public abstract class ScriptBase
    {
        internal ScriptHost Host;

        /// <summary>Cancellation token; long loops should call CheckCancel().</summary>
        public CancellationToken Token => Host.Cts.Token;
        public void CheckCancel() => Token.ThrowIfCancellationRequested();

        public void Print(object o) => Host.Output(Convert.ToString(o, System.Globalization.CultureInfo.InvariantCulture) + Environment.NewLine);
        public void Print(string format, params object[] args) => Host.Output(string.Format(System.Globalization.CultureInfo.InvariantCulture, format, args) + Environment.NewLine);
        public void Progress(double fraction, string status = null) => Host.Progress(fraction, status);

        /// <summary>Observing site, current (simulated) time and selected object.</summary>
        public Observer Site => AppState.Site;
        public AstroTime Now => AppState.Now;
        public SelectedObject Selection => AppState.Selection;
        public SkyContext Sky => new SkyContext(AppState.Site, AppState.Now);
        public DeviceManager Devices => DeviceManager.Instance;
        public Core.Catalogs.CatalogManager Catalogs => Core.Catalogs.CatalogManager.Instance;

        // ------------------------------------------------------------ plotting & tables

        public void ClearPlot() => Host.Ui(() => Host.Plot.Clear());
        public void PlotTitle(string title, string xLabel = null, string yLabel = null, bool invertY = false) => Host.Ui(() => { Host.Plot.Title = title; Host.Plot.XLabel = xLabel; Host.Plot.YLabel = yLabel; Host.Plot.InvertY = invertY; Host.Plot.Invalidate(); });
        public void Plot(string name, IEnumerable<double> x, IEnumerable<double> y, bool scatter = false)
        {
            var xs = x.ToList(); var ys = y.ToList();
            Host.Ui(() =>
            {
                var s = Host.Plot.AddSeries(name, scatter ? SeriesStyle.Scatter : SeriesStyle.Line);
                for (int i = 0; i < Math.Min(xs.Count, ys.Count); i++) s.Add(xs[i], ys[i]);
                Host.Plot.Invalidate();
            });
        }
        public void Plot(string name, Func<double, double> f, double x0, double x1, int n = 400)
        {
            var xs = Enumerable.Range(0, n + 1).Select(i => x0 + (x1 - x0) * i / n).ToList();
            Plot(name, xs, xs.Select(f));
        }

        public void Table(IList<string> columns, IEnumerable<IList<object>> rows)
        {
            var cols = columns.ToList(); var rs = rows.Select(r => r.ToArray()).ToList();
            Host.Ui(() =>
            {
                var g = Host.Grid;
                g.Columns.Clear(); g.Rows.Clear();
                foreach (var c in cols) g.Columns.Add(c, c);
                foreach (var r in rs) g.Rows.Add(r);
            });
        }

        // ------------------------------------------------------------ images

        public AstroImage LoadImage(string path) => Imaging.IO.ImageFile.Load(path);
        public void SaveFits(string path, AstroImage img) => Imaging.IO.FitsIO.Write(path, img);
        public string[] Files(string folder, string pattern = "*.fit*") => Directory.GetFiles(folder, pattern).OrderBy(f => f).ToArray();
        /// <summary>Open an image in the Process view.</summary>
        public void Show(AstroImage img, string name = "script result") => Host.Ui(() => { MainForm.Instance.GetView<Views.ProcessingView>("processing").OpenImage(img, name); MainForm.Instance.Navigate("processing"); });

        /// <summary>Wait (cancellable).</summary>
        public void Sleep(double seconds) { if (Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(seconds))) Token.ThrowIfCancellationRequested(); }

        public abstract void Run();
    }

    public class CompileError
    {
        public int Line, Column;
        public string Text;
        public bool Warning;
        public override string ToString() => string.Format("line {0}, col {1}: {2}{3}", Line, Column, Warning ? "warning: " : "", Text);
    }

    public class ScriptHost
    {
        public PlotControl Plot;
        public System.Windows.Forms.DataGridView Grid;
        public Action<string> Output = s => { };
        public Action<double, string> Progress = (p, s) => { };
        public Action<Action> Ui = a => a();
        public CancellationTokenSource Cts = new CancellationTokenSource();

        public static readonly string[] Usings =
        {
            "System", "System.Collections.Generic", "System.Linq", "System.IO", "System.Text", "System.Drawing", "System.Threading",
            "Sidralune", "Sidralune.UI", "Sidralune.Views", "Sidralune.Core", "Sidralune.Core.Mathematics", "Sidralune.Core.Time", "Sidralune.Core.Coordinates", "Sidralune.Core.Ephemeris",
            "Sidralune.Core.Catalogs", "Sidralune.Core.MinorBodies", "Sidralune.Core.Satellites", "Sidralune.Core.Util",
            "Sidralune.Imaging", "Sidralune.Imaging.IO", "Sidralune.Imaging.Analysis", "Sidralune.Imaging.Processing", "Sidralune.Imaging.Stacking", "Sidralune.Imaging.Registration", "Sidralune.Imaging.Planetary",
            "Sidralune.Science.Astrometry", "Sidralune.Science.Photometry", "Sidralune.Science.Spectroscopy", "Sidralune.Science.SolarSystem", "Sidralune.Science.Radio", "Sidralune.Science.Databases",
            "Sidralune.Devices", "Sidralune.Devices.Automation"
        };

        private const string Marker = "//@members";

        /// <summary>
        /// Compile a script. The body goes inside Run(); anything after a line "//@members" becomes extra class members
        /// (helper methods, fields). Returns the script type or null with errors.
        /// </summary>
        public static Type Compile(string code, out List<CompileError> errors)
        {
            errors = new List<CompileError>();
            string body = code, members = "";
            int m = code.IndexOf(Marker, StringComparison.Ordinal);
            if (m >= 0) { body = code.Substring(0, m); members = code.Substring(m + Marker.Length); }
            var sb = new System.Text.StringBuilder();
            // only namespaces that exist in the loaded assemblies, so a missing one never breaks every script
            foreach (var an in Assembly.GetExecutingAssembly().GetReferencedAssemblies()) try { Assembly.Load(an); } catch { }
            var known = new HashSet<string>(AppDomain.CurrentDomain.GetAssemblies().Where(a => a.FullName.StartsWith("Sidralune") || a.FullName.StartsWith("System"))
                .SelectMany(a => { try { return a.GetTypes().Select(t => t.Namespace); } catch { return Enumerable.Empty<string>(); } }).Where(n => n != null));
            int headerLines = 0;
            foreach (var u in Usings) if (known.Contains(u)) { sb.AppendLine("using " + u + ";"); headerLines++; }
            sb.AppendLine("public class UserScript : Sidralune.Scripting.ScriptBase {");
            sb.AppendLine("public override void Run() {");
            headerLines += 2;
            sb.AppendLine(body);
            sb.AppendLine("}");
            int membersOffset = headerLines + body.Split('\n').Length + 1;
            sb.AppendLine(members);
            sb.AppendLine("}");

            var opts = new CompilerParameters { GenerateInMemory = true, GenerateExecutable = false, TreatWarningsAsErrors = false, CompilerOptions = "/optimize+ /unsafe" };
            foreach (var asm in new[] { "System.dll", "System.Core.dll", "System.Drawing.dll", "System.Windows.Forms.dll", "System.Data.dll", "System.Xml.dll", "Microsoft.CSharp.dll" }) opts.ReferencedAssemblies.Add(asm);
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            foreach (var f in Directory.GetFiles(dir, "Sidralune*.dll")) opts.ReferencedAssemblies.Add(f);
            opts.ReferencedAssemblies.Add(Assembly.GetExecutingAssembly().Location);
            CompilerResults res;
            using (var provider = new CSharpCodeProvider()) res = provider.CompileAssemblyFromSource(opts, sb.ToString());
            int bodyLines = body.Split('\n').Length;
            foreach (CompilerError e in res.Errors)
            {
                int line = e.Line - headerLines;
                if (line > bodyLines) line = e.Line - membersOffset + bodyLines + 1;
                errors.Add(new CompileError { Line = line, Column = e.Column, Text = e.ErrorText, Warning = e.IsWarning });
            }
            if (res.Errors.HasErrors) return null;
            return res.CompiledAssembly.GetType("UserScript");
        }

        public void Execute(Type t)
        {
            var s = (ScriptBase)Activator.CreateInstance(t);
            s.Host = this;
            try { s.Run(); }
            catch (TargetInvocationException ex) { throw ex.InnerException ?? ex; }
        }
    }

    /// <summary>Example scripts shown in the Scripts view.</summary>
    public static class ScriptExamples
    {
        public static readonly string[][] All =
        {
            new[] { "Hello sky: planets now", @"var ctx = Sky;
Print(""Planets at {0} for {1}"", Now, Site.Name);
var rows = new List<IList<object>>();
foreach (Body b in new[] { Body.Mercury, Body.Venus, Body.Mars, Body.Jupiter, Body.Saturn, Body.Uranus, Body.Neptune })
{
    var p = ctx.BodyPlace(b);
    PlanetPhysics.Fill(b, p, ctx.JdTT);
    rows.Add(new object[] { b, p.Horizontal.AltDeg.ToString(""0.0""), p.Magnitude.ToString(""0.0""), p.DiameterArcsec.ToString(""0.0"") });
}
Table(new[] { ""Planet"", ""Altitude"", ""Mag"", ""Diameter"" }, rows);" },

            new[] { "Altitude curve of the selection tonight", @"var o = Selection;
if (o == null) { Print(""Select an object first""); return; }
var xs = new List<double>(); var alt = new List<double>(); var sun = new List<double>();
for (double h = 0; h <= 24; h += 0.1)
{
    var t = new AstroTime(Now.JdUtc + h / 24);
    xs.Add(h);
    alt.Add(Events.FixedAltitude(o.RaDeg * AstroMath.Deg2Rad, o.DecDeg * AstroMath.Deg2Rad, Site, t));
    sun.Add(Events.SunAltitudeFast(Site, t));
}
ClearPlot();
PlotTitle(o.Name + "" altitude"", ""hours from now"", ""altitude (deg)"");
Plot(o.Name, xs, alt);
Plot(""Sun"", xs, sun);" },

            new[] { "Batch: FWHM and star count of a folder", @"string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), ""Sidralune"");
var rows = new List<IList<object>>();
var xs = new List<double>(); var ys = new List<double>();
int i = 0;
foreach (var f in Directory.GetFiles(folder, ""*.fit*"", SearchOption.AllDirectories))
{
    CheckCancel();
    var img = LoadImage(f);
    var stars = StarDetector.Detect(img);
    double fwhm, hfr, ecc;
    StarDetector.Summary(stars, out fwhm, out hfr, out ecc);
    rows.Add(new object[] { Path.GetFileName(f), stars.Count, fwhm.ToString(""0.00""), ecc.ToString(""0.00"") });
    xs.Add(i++); ys.Add(fwhm);
}
Table(new[] { ""File"", ""Stars"", ""FWHM px"", ""Eccentricity"" }, rows);
ClearPlot(); PlotTitle(""FWHM per frame"", ""frame"", ""FWHM (px)"");
Plot(""FWHM"", xs, ys, true);" },

            new[] { "Simulated camera: capture and stretch", @"var cam = Devices.Camera;
if (cam == null) { Print(""Connect a camera (or the simulators in Observatory)""); return; }
cam.StartExposure(5, true);
while (!cam.ImageReady) Sleep(0.2);
var img = cam.GetImage();
Print(""Captured {0}x{1}"", img.Width, img.Height);
StretchOps.ApplyHistogram(img, StretchOps.AutoStretch(img));
Show(img, ""script capture"");" },

            new[] { "Lomb-Scargle of a synthetic variable", @"var rnd = new Random(1);
var t = new List<double>(); var y = new List<double>();
for (int i = 0; i < 300; i++) { double ti = rnd.NextDouble() * 30; t.Add(ti); y.Add(Math.Sin(2 * Math.PI * ti / 2.37) + rnd.NextDouble() * 0.5); }
var pg = PeriodSearch.LombScargle(t, y, null, 0.5, 10);
Print(""Best period {0:0.0000} d"", pg.BestPeriod);
ClearPlot(); PlotTitle(""Lomb-Scargle"", ""frequency (1/d)"", ""power"");
Plot(""power"", pg.Frequencies, pg.Power);" },

            new[] { "Helper methods (//@members)", @"for (int n = 1; n <= 10; n++) Print(""{0}! = {1}"", n, Factorial(n));
//@members
static long Factorial(int n) { return n <= 1 ? 1 : n * Factorial(n - 1); }" },
        };
    }
}
