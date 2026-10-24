using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Sidralune.Tests
{
    /// <summary>
    /// Checks for the tests. A test method makes many checks; each one is printed (the test's output in Test Explorer)
    /// and a failed one does not stop the test, so a single run lists every failed check. <see cref="TestBase"/> then
    /// fails the test if any check failed.
    /// </summary>
    public static class Checks
    {
        private static readonly List<string> Failures = new List<string>();

        internal static void Reset() { lock (Failures) Failures.Clear(); }

        internal static string[] Failed() { lock (Failures) return Failures.ToArray(); }

        /// <summary>A value within a tolerance of the expected value (NaN always fails).</summary>
        public static void Check(string name, double actual, double expected, double tol)
        {
            bool ok = !double.IsNaN(actual) && Math.Abs(actual - expected) <= tol;
            Report(ok, string.Format("{0}: got {1:G10}, expected {2:G10} (tol {3:G3}, diff {4:G3})", name, actual, expected, tol, actual - expected));
        }

        /// <summary>A condition, with optional detail printed after the name.</summary>
        public static void CheckTrue(string name, bool cond, string detail = "")
        {
            Report(cond, (name + " " + detail).TrimEnd());
        }

        /// <summary>Extra output that is not a check.</summary>
        public static void Info(string s) => Console.WriteLine("       " + s);

        private static void Report(bool ok, string line)
        {
            Console.WriteLine((ok ? "  PASS " : "  FAIL ") + line);
            if (!ok) lock (Failures) Failures.Add(line);
        }
    }

    /// <summary>Base of every test class: clears the checks before a test and fails the test if any check failed.</summary>
    public abstract class TestBase
    {
        public TestContext TestContext { get; set; }

        [TestInitialize]
        public void StartChecks() => Checks.Reset();

        [TestCleanup]
        public void FinishChecks()
        {
            var failed = Checks.Failed();
            if (failed.Length > 0) Assert.Fail(failed.Length + " check(s) failed:" + Environment.NewLine + string.Join(Environment.NewLine, failed));
        }
    }
}
