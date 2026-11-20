using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sidralune.Scripting;
using Sidralune.Tests;
using Sidralune.UI;
using Sidralune.Views;
using static Sidralune.AppTests.AppHost;
using static Sidralune.Tests.Checks;

namespace Sidralune.AppTests
{
    /// <summary>The C# script host and the Scripts view.</summary>
    [TestClass]
    public class ScriptingTests : TestBase
    {
        private static string Run(string code)
        {
            List<CompileError> errors;
            var t = ScriptHost.Compile(code, out errors);
            if (t == null) return "COMPILE: " + string.Join("; ", errors);
            var output = new System.Text.StringBuilder();
            var host = new ScriptHost { Output = s => output.Append(s) };
            host.Execute(t);
            return output.ToString();
        }

        [TestMethod]
        public void TestCompileAndRun()
        {
            CheckTrue("A script prints", Run("Print(1 + 2);").Trim() == "3");
            CheckTrue("Formatted output is culture-independent", Run("Print(\"{0:0.00}\", 1.5);").Trim() == "1.50");
            CheckTrue("Helpers after //@members", Run("Print(Twice(21));\n//@members\nint Twice(int v) { return 2 * v; }").Trim() == "42");
            CheckTrue("The libraries are available", Run("Print(Sidralune.Core.Mathematics.AstroMath.FormatHms(12.5, 0));").Contains("12"));
            CheckTrue("Site, time and catalogues", Run("Print(Site.Name); Print(Now.JdUtc > 2460000); Print(Catalogs.Find(\"Vega\") != null);").Contains("True" + Environment.NewLine + "True"));
        }

        [TestMethod]
        public void TestCompileErrors()
        {
            List<CompileError> errors;
            CheckTrue("Syntax error found", ScriptHost.Compile("int x = ;", out errors) == null && errors.Any(e => !e.Warning));
            var first = errors.FirstOrDefault(e => !e.Warning);
            CheckTrue("Error on the script's own line 1", first != null && first.Line == 1, first?.ToString());
            ScriptHost.Compile("Print(1);\nPrint(2);\nundefinedName++;", out errors);
            first = errors.FirstOrDefault(e => !e.Warning);
            CheckTrue("Error line counted from the script's first line", first != null && first.Line == 3, first?.ToString());
            ScriptHost.Compile("Print(1);\n//@members\nvoid F()\n{\n    int y = \"text\";\n}", out errors);
            first = errors.FirstOrDefault(e => !e.Warning);
            CheckTrue("Error in a helper member on its own line", first != null && first.Line == 5, first?.ToString());
        }

        [TestMethod]
        public void TestCancellation()
        {
            List<CompileError> errors;
            var t = ScriptHost.Compile("while (true) { Sleep(0.05); }", out errors);
            var host = new ScriptHost();
            var run = Task.Run(() => host.Execute(t));
            Thread.Sleep(300);
            host.Cts.Cancel();
            bool cancelled = false;
            try { cancelled = run.Wait(3000) && false; } catch (AggregateException ex) { cancelled = ex.InnerException is OperationCanceledException; }
            CheckTrue("Stop ends a running script", cancelled);
        }

        [TestMethod]
        public void TestExamplesCompile()
        {
            CheckTrue("Examples listed", ScriptExamples.All.Length >= 5);
            foreach (var ex in ScriptExamples.All)
            {
                List<CompileError> errors;
                var t = ScriptHost.Compile(ex[1], out errors);
                CheckTrue("Example compiles: " + ex[0], t != null, string.Join("; ", errors.Where(e => !e.Warning)));
            }
        }

        [TestMethod]
        public void TestScriptsView()
        {
            var view = View<ScriptingView>("scripting");
            var console = OnUi(() => Find<TextBox>(view, b => b.ReadOnly && b.Multiline));
            OnUi(() => view.RunScript("Print(\"hello from the view\");"));
            CheckTrue("A script runs in the view and reports when it is done", WaitFor(() => console.Text.Contains("hello from the view") && console.Text.Contains("Finished")), OnUi(() => console.Text));
            OnUi(() => view.RunScript("int x = ;"));
            CheckTrue("A compile error is shown with its line", WaitFor(() => console.Text.Contains("error line 1")) && WaitFor(() => Toasts().Contains("Compilation failed")), OnUi(() => console.Text));
            OnUi(() => view.RunScript("throw new InvalidOperationException(\"from the script\");"));
            CheckTrue("An exception in a script is reported in the view", WaitFor(() => console.Text.Contains("InvalidOperationException: from the script")), OnUi(() => console.Text));
            var lomb = ScriptExamples.All.First(e => e[0].StartsWith("Lomb"));
            OnUi(() => view.RunScript(lomb[1]));
            CheckTrue("The Lomb-Scargle example runs", WaitFor(() => console.Text.Contains("Best period") && console.Text.Contains("Finished"), 20000), OnUi(() => console.Text));
            var hello = ScriptExamples.All[0];
            OnUi(() => view.RunScript(hello[1]));
            var grid = OnUi(() => Find<DataGridView>(view));
            CheckTrue("The planets example fills the table", WaitFor(() => console.Text.Contains("Finished") && grid.Rows.Count == 7, 60000), OnUi(() => console.Text + " / rows " + grid.Rows.Count));

            // each run starts with an empty plot and table: the Lomb-Scargle plot is gone after the planets example, and its table after Lomb-Scargle
            var plot = OnUi(() => Find<PlotControl>(view));
            CheckTrue("A new run clears the last script's plot", OnUi(() => plot.Series.Count == 0 && plot.Title == "Script plot"), OnUi(() => plot.Series.Count + " series, " + plot.Title));
            OnUi(() => view.RunScript(lomb[1]));
            CheckTrue("A new run clears the last script's table", WaitFor(() => console.Text.Contains("Finished"), 20000) && OnUi(() => grid.Rows.Count == 0 && grid.Columns.Count == 0 && plot.Series.Count == 1), OnUi(() => grid.Rows.Count + " rows, " + plot.Series.Count + " series"));
            // a script that doesn't compile leaves the results in place while it is fixed
            OnUi(() => view.RunScript("int x = ;"));
            CheckTrue("A compile error keeps the last plot", WaitFor(() => console.Text.Contains("error line 1")) && OnUi(() => plot.Series.Count == 1));
            // ClearTable() empties the table while the script runs
            OnUi(() => view.RunScript("Table(new[] { \"A\" }, new[] { new object[] { 1 } }); Sleep(0.3); ClearTable();"));
            CheckTrue("ClearTable empties the table", WaitFor(() => console.Text.Contains("Finished")) && WaitFor(() => OnUi(() => grid.Columns.Count == 0)), OnUi(() => console.Text + " / columns " + grid.Columns.Count));

            // the batch example says when its folder is missing instead of failing
            var batch = ScriptExamples.All.First(e => e[0].StartsWith("Batch"));
            string missing = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Sidralune no such folder");
            string code = "string folder = @\"" + missing + "\";" + batch[1].Substring(batch[1].IndexOf('\n'));
            OnUi(() => view.RunScript(code));
            CheckTrue("The batch example names a missing folder", WaitFor(() => console.Text.Contains("Finished"), 20000) && OnUi(() => console.Text.Contains("Put some FITS files in " + missing) && !console.Text.Contains("Exception")), OnUi(() => console.Text));
        }
    }
}
