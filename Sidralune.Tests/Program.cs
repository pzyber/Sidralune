using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;

namespace Sidralune.Tests
{
    /// <summary>Minimal self-contained test runner: every public static void method named Test* in this assembly is executed.</summary>
    internal static class Program
    {
        private static int _pass, _fail;
        private static readonly List<string> Failures = new List<string>();

        public static void Check(string name, double actual, double expected, double tol)
        {
            bool ok = !double.IsNaN(actual) && Math.Abs(actual - expected) <= tol;
            if (ok) _pass++; else { _fail++; Failures.Add(name); }
            Console.ForegroundColor = ok ? ConsoleColor.Green : ConsoleColor.Red;
            Console.Write(ok ? "  PASS " : "  FAIL ");
            Console.ResetColor();
            Console.WriteLine("{0}: got {1:G10}, expected {2:G10} (tol {3:G3}, diff {4:G3})", name, actual, expected, tol, actual - expected);
        }

        public static void CheckTrue(string name, bool cond, string detail = "")
        {
            if (cond) _pass++; else { _fail++; Failures.Add(name); }
            Console.ForegroundColor = cond ? ConsoleColor.Green : ConsoleColor.Red;
            Console.Write(cond ? "  PASS " : "  FAIL ");
            Console.ResetColor();
            Console.WriteLine("{0} {1}", name, detail);
        }

        public static void Info(string s) => Console.WriteLine("       " + s);

        private static int Main(string[] args)
        {
            string filter = args.Length > 0 ? args[0] : null;
            var tests = Assembly.GetExecutingAssembly().GetTypes()
                .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
                .Where(m => m.Name.StartsWith("Test") && m.GetParameters().Length == 0)
                .Where(m => filter == null || (m.DeclaringType.Name + "." + m.Name).IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(m => m.DeclaringType.Name).ThenBy(m => m.MetadataToken).ToList();
            var sw = Stopwatch.StartNew();
            foreach (var m in tests)
            {
                Console.WriteLine("[{0}.{1}]", m.DeclaringType.Name, m.Name);
                try { m.Invoke(null, null); }
                catch (TargetInvocationException ex)
                {
                    _fail++;
                    Failures.Add(m.Name + " (exception)");
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("  EXCEPTION " + ex.InnerException);
                    Console.ResetColor();
                }
            }
            Console.WriteLine();
            Console.WriteLine("{0} passed, {1} failed in {2:0.0}s", _pass, _fail, sw.Elapsed.TotalSeconds);
            foreach (var f in Failures) Console.WriteLine("  failed: " + f);
            return _fail == 0 ? 0 : 1;
        }
    }
}
