using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Sidralune.Core;
using Sidralune.Core.Catalogs;
using Sidralune.Core.Coordinates;
using Sidralune.Core.Ephemeris;
using Sidralune.Core.Mathematics;
using Sidralune.Core.Observing;
using Sidralune.Core.Satellites;
using Sidralune.Core.Time;
using Sidralune.Core.Util;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Sidralune.Tests.Checks;

namespace Sidralune.Tests
{
    /// <summary>Utilities, statistics, numerical methods, coordinate helpers, events and observing tools in Sidralune.Core.</summary>
    [TestClass]
    public class CoreUtilTests : TestBase
    {
        private enum Colour { Red, Green }

        private class Settings
        {
            public string Name { get; set; }
            public double Value { get; set; }
            public int Count;
            public bool On { get; set; }
            public Colour Colour { get; set; }
            public DateTime When { get; set; }
            public List<double> Values { get; set; } = new List<double>();
            public int[] Numbers;
            public Dictionary<string, double> Map { get; set; } = new Dictionary<string, double>();
        }

        [TestMethod]
        public void TestCancelledDownload()
        {
            // a local server that sends a large file slowly; the download is stopped part way
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var server = Task.Run(() =>
            {
                using (var c = listener.AcceptTcpClient())
                using (var s = c.GetStream())
                {
                    s.Read(new byte[4096], 0, 4096);
                    var head = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 100000000\r\nConnection: close\r\n\r\n");
                    s.Write(head, 0, head.Length);
                    var chunk = new byte[65536];
                    try { for (int i = 0; i < 1000; i++) { s.Write(chunk, 0, chunk.Length); Thread.Sleep(20); } } catch { } // the client hangs up
                }
            });
            string dir = Path.Combine(Path.GetTempPath(), "sidralune_download_test");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "data.bin");
            using (var cts = new CancellationTokenSource())
            {
                bool stopped = false;
                try { Net.Download("http://127.0.0.1:" + port + "/data.bin", path, (done, total) => { if (done > 200000) cts.Cancel(); }, cts.Token); }
                catch (Exception) { stopped = true; }
                CheckTrue("A stopped download ends", stopped);
                CheckTrue("It leaves neither the file nor a partial one", !File.Exists(path) && !File.Exists(path + ".part"));
            }

            // a request whose answer never comes ends at once when its job is stopped
            var silent = new TcpListener(IPAddress.Loopback, 0);
            silent.Start();
            int silentPort = ((IPEndPoint)silent.LocalEndpoint).Port;
            using (var cts = new CancellationTokenSource())
            {
                cts.CancelAfter(500);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                bool ended = false;
                try { StopRequest.Run(cts.Token, () => Net.GetString("http://127.0.0.1:" + silentPort + "/", 60000)); }
                catch (WebException) { ended = true; }
                CheckTrue("A stopped job's request ends at once", ended && sw.ElapsedMilliseconds < 5000, sw.ElapsedMilliseconds + " ms");
            }
            silent.Stop();
            listener.Stop();
            server.Wait(10000);
        }

        [TestMethod]
        public void TestDatasetMirrors()
        {
            // the dataset's server is down (nothing listens on its port), its mirror has the file
            var gone = new TcpListener(IPAddress.Loopback, 0);
            gone.Start();
            int deadPort = ((IPEndPoint)gone.LocalEndpoint).Port;
            gone.Stop();
            var mirror = new TcpListener(IPAddress.Loopback, 0);
            mirror.Start();
            int port = ((IPEndPoint)mirror.LocalEndpoint).Port;
            var body = Encoding.ASCII.GetBytes("catalogue data");
            var server = Task.Run(() =>
            {
                using (var c = mirror.AcceptTcpClient())
                using (var s = c.GetStream())
                {
                    s.Read(new byte[4096], 0, 4096);
                    var head = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n");
                    s.Write(head, 0, head.Length);
                    s.Write(body, 0, body.Length);
                }
            });
            string dir = Path.Combine(Path.GetTempPath(), "sidralune_mirror_test");
            Directory.CreateDirectory(dir);
            var d = new Dataset { Name = "Test data", Url = "http://127.0.0.1:" + deadPort + "/data.txt", Mirrors = new[] { "http://127.0.0.1:" + port + "/data.txt" }, FileName = "data.txt", Folder = dir };
            if (File.Exists(d.LocalPath)) File.Delete(d.LocalPath);
            DataManager.Install(d);
            CheckTrue("When the dataset's server cannot be reached, its mirror is used", File.Exists(d.LocalPath) && File.ReadAllText(d.LocalPath) == "catalogue data");
            server.Wait(5000);
            mirror.Stop();
            bool failed = false;
            try { DataManager.Install(new Dataset { Name = "Test data", Url = d.Url, FileName = "none.txt", Folder = dir }); }
            catch (WebException) { failed = true; }
            CheckTrue("Without a mirror the failure is reported", failed && !File.Exists(Path.Combine(dir, "none.txt")));
            CheckTrue("The PGC galaxies have a mirror", DataManager.Get("pgc").Mirrors.Length > 0);

            // a dataset that is prepared after the download: only the prepared file is kept
            var again = new TcpListener(IPAddress.Loopback, 0);
            again.Start();
            int port2 = ((IPEndPoint)again.LocalEndpoint).Port;
            var prepared = new Dataset { Name = "Prepared", Url = "http://127.0.0.1:" + port2 + "/raw.txt", FileName = "prepared.txt", Folder = dir, Prepare = (from, to) => File.WriteAllText(to, File.ReadAllText(from).ToUpperInvariant()) };
            var server2 = Task.Run(() =>
            {
                using (var c = again.AcceptTcpClient())
                using (var s = c.GetStream())
                {
                    s.Read(new byte[4096], 0, 4096);
                    var head = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n");
                    s.Write(head, 0, head.Length);
                    s.Write(body, 0, body.Length);
                }
            });
            if (File.Exists(prepared.LocalPath)) File.Delete(prepared.LocalPath);
            DataManager.Install(prepared);
            server2.Wait(5000);
            again.Stop();
            CheckTrue("A prepared dataset keeps only the prepared file", File.ReadAllText(prepared.LocalPath) == "CATALOGUE DATA" && !File.Exists(prepared.LocalPath + ".download"));
            CheckTrue("The PGC magnitudes are prepared", DataManager.Get("pgc-mag").Prepare != null);
        }

        [TestMethod]
        public void TestJson()
        {
            var o = Json.ParseObject("{ \"a\": 1, \"b\": -2.5e3, \"s\": \"x\\\"y\\\\z\\n\\u00e9\", \"t\": true, \"f\": false, \"n\": null, \"arr\": [1, [2, 3], {\"k\": \"v\"}], \"e\": {}, \"ea\": [] }");
            CheckTrue("Integer parsed as long", o["a"] is long && (long)o["a"] == 1);
            Check("Exponent number", (double)o["b"], -2500, 0);
            CheckTrue("String escapes", (string)o["s"] == "x\"y\\z\né", (string)o["s"]);
            CheckTrue("Booleans and null", (bool)o["t"] && !(bool)o["f"] && o["n"] == null && o.ContainsKey("n"));
            Check("Nested path", Json.GetDouble(o, "arr.1.0"), 2, 0);
            CheckTrue("Path into object in array", Json.GetString(o, "arr.2.k") == "v");
            CheckTrue("Missing path is null", Json.Get(o, "arr.9") == null && Json.Get(o, "nope.x") == null && double.IsNaN(Json.GetDouble(o, "zzz")));
            CheckTrue("Empty object and array", ((Dictionary<string, object>)o["e"]).Count == 0 && ((List<object>)o["ea"]).Count == 0);
            CheckTrue("NaN literal", double.IsNaN((double)Json.Parse("NaN")));

            bool threw = false;
            try { Json.Parse("{\"a\": 1,"); } catch (FormatException) { threw = true; } catch (IndexOutOfRangeException) { threw = true; }
            CheckTrue("Truncated JSON throws", threw);
            threw = false;
            try { Json.Parse("\"open"); } catch (FormatException) { threw = true; }
            CheckTrue("Unterminated string throws", threw);

            // serialization: special values, culture independence and a round trip through Populate
            var prev = System.Threading.Thread.CurrentThread.CurrentCulture;
            System.Threading.Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("sv-SE");
            try
            {
                CheckTrue("Doubles use a decimal point in any culture", Json.Serialize(1.5) == "1.5" && Json.Serialize(0.1) == "0.1");
            }
            finally { System.Threading.Thread.CurrentThread.CurrentCulture = prev; }
            CheckTrue("NaN and infinity become null", Json.Serialize(double.NaN) == "null" && Json.Serialize(double.PositiveInfinity) == "null");
            CheckTrue("Control characters escaped", Json.Serialize("a\tb\u0001") == "\"a\\tb\\u0001\"");
            var s = new Settings
            {
                Name = "Test \"quoted\"", Value = Math.PI, Count = 7, On = true, Colour = Colour.Green, When = new DateTime(2026, 10, 24, 12, 30, 15, DateTimeKind.Utc),
                Values = new List<double> { 1, 2.5 }, Numbers = new[] { 3, 4 }, Map = new Dictionary<string, double> { { "x", 1.25 } }
            };
            foreach (bool indent in new[] { false, true })
            {
                var back = Json.Populate<Settings>(Json.ParseObject(Json.Serialize(s, indent)));
                CheckTrue("Object round trip" + (indent ? " (indented)" : ""), back.Name == s.Name && back.Value == s.Value && back.Count == 7 && back.On && back.Colour == Colour.Green
                    && back.When == s.When && back.Values.SequenceEqual(s.Values) && back.Numbers.SequenceEqual(s.Numbers) && back.Map["x"] == 1.25);
            }
            var partial = Json.Populate<Settings>(Json.ParseObject("{\"Name\": \"only\", \"Unknown\": 5, \"Colour\": \"red\"}"));
            CheckTrue("Missing members keep defaults, unknown ignored, enum case-insensitive", partial.Name == "only" && partial.Count == 0 && partial.Values != null && partial.Colour == Colour.Red);
        }

        [TestMethod]
        public void TestCsv()
        {
            string text = "Name,Mag;x,\"Note\"\r\nVega,0.03,\"bright, blue\"\n\"Say \"\"hi\"\"\",  1.5 ,\"two\nlines\"\nEmpty,,\n";
            using (var r = new CsvReader(new StringReader(text)))
            {
                CheckTrue("Header read", r.Header.Length == 3 && r.Header[2] == "Note");
                CheckTrue("Column lookup by any of several names", r.Column("missing", "note") == 2 && r.Column("nope") == -1);
                var row = r.ReadRow();
                CheckTrue("Quoted separator", row[2] == "bright, blue" && CsvReader.S(row, 0) == "Vega");
                Check("Number cell", CsvReader.D(row, 1), 0.03, 0);
                row = r.ReadRow();
                CheckTrue("Doubled quotes and embedded newline", row[0] == "Say \"hi\"" && row[2] == "two\nlines", row[0] + " | " + row[2]);
                Check("Number with spaces", CsvReader.D(row, 1), 1.5, 0);
                row = r.ReadRow();
                CheckTrue("Empty cells fall back", double.IsNaN(CsvReader.D(row, 1)) && CsvReader.I(row, 2, -1) == -1 && CsvReader.S(row, 7) == "");
                CheckTrue("End of file", r.ReadRow() == null);
            }
            using (var r = new CsvReader(new StringReader("1;2;3"), ';', false))
            {
                var row = r.ReadRow();
                CheckTrue("Other separator, no header, no final newline", r.Header == null && row.Length == 3 && CsvReader.I(row, 2) == 3);
            }
            CheckTrue("Escape only when needed", CsvWriter.Escape("plain") == "plain" && CsvWriter.Escape("a,b") == "\"a,b\"" && CsvWriter.Escape("q\"") == "\"q\"\"\"" && CsvWriter.Escape(null) == "");
            CheckTrue("Invariant formatting", CsvWriter.Format(1.5) == "1.5" && CsvWriter.Format(new DateTime(2026, 1, 2, 3, 4, 5, 6)) == "2026-01-02T03:04:05.006" && CsvWriter.Format(null) == "");
            string path = Path.Combine(Path.GetTempPath(), "SidraluneTests", "test.csv");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            CsvWriter.Write(path, new[] { "a", "b,c" }, new[] { new object[] { 1.25, "x,y" }, new object[] { null, "z\"" } });
            using (var r = CsvReader.Open(path))
            {
                var r1 = r.ReadRow(); var r2 = r.ReadRow();
                CheckTrue("Write then read", r.Header[1] == "b,c" && CsvReader.D(r1, 0) == 1.25 && r1[1] == "x,y" && r2[0] == "" && r2[1] == "z\"" && r.ReadRow() == null);
            }
        }

        [TestMethod]
        public void TestStatistics()
        {
            var v = new List<double> { 2, 4, 4, 4, 5, 5, 7, 9 };
            Check("Mean", Stats.Mean(v), 5, 1e-12);
            Check("Sample standard deviation", Stats.StdDev(v), Math.Sqrt(32.0 / 7), 1e-12);
            Check("Median (even count)", Stats.Median(v), 4.5, 1e-12);
            Check("Median (odd count)", Stats.Median(new List<double> { 9, 1, 5 }), 5, 0);
            Check("Float median", Stats.Median(new float[] { 3, 1, 2, 10 }), 2.5, 1e-6);
            Check("MAD", Stats.Mad(v, 4.5), 0.5, 1e-12);
            Check("Percentile 0", Stats.Percentile(v, 0), 2, 0);
            Check("Percentile 100", Stats.Percentile(v, 100), 9, 0);
            Check("Percentile 50 interpolates", Stats.Percentile(v, 50), 4.5, 1e-12);
            var a = new double[] { 5, 3, 8, 1, 9, 2 };
            Check("QuickSelect k-th smallest", Stats.QuickSelect((double[])a.Clone(), a.Length, 2), 3, 0);

            // a Gaussian sample with outliers: the robust estimators ignore them
            var rnd = new Random(1);
            var g = new List<double>();
            for (int i = 0; i < 4000; i++) g.Add(10 + 2 * Stats.NormalQuantile((i + 0.5) / 4000.0) + 0 * rnd.NextDouble());
            var noisy = new List<double>(g);
            for (int i = 0; i < 40; i++) noisy.Add(1000);
            double mean, sigma; int used;
            Stats.SigmaClip(noisy, 3, 10, out mean, out sigma, out used);
            Check("Sigma clip rejects outliers (mean)", mean, 10, 0.05);
            Check("Sigma clip sigma", sigma, 2, 0.1);
            CheckTrue("Sigma clip removed the outliers", used <= 4000 && used > 3900, used + " used");
            double loc, scale;
            Stats.Biweight(noisy, out loc, out scale);
            Check("Biweight location", loc, 10, 0.05);
            Check("Biweight scale", scale, 2, 0.15);
            Check("MAD to sigma", Stats.Mad(g, Stats.Median(g)) * Stats.MadToSigma, 2, 0.02);

            double ia, ib, r2;
            CheckTrue("Linear fit", Stats.LinearFit(new[] { 0.0, 1, 2, 3 }, new[] { 1.0, 3, 5, 7 }, out ia, out ib, out r2) && Math.Abs(ia - 1) < 1e-12 && Math.Abs(ib - 2) < 1e-12 && Math.Abs(r2 - 1) < 1e-12);
            CheckTrue("Degenerate linear fit refused", !Stats.LinearFit(new[] { 1.0, 1 }, new[] { 2.0, 3 }, out ia, out ib, out r2));
            double err;
            Check("Weighted mean", Stats.WeightedMean(new[] { 1.0, 3 }, new[] { 1.0, 1 }, out err), 2, 1e-12);
            Check("Weighted mean error", err, Math.Sqrt(0.5), 1e-12);
            var h = Stats.Histogram(new float[] { 0, 0.1f, 0.5f, 0.99f, 5, -3, float.NaN }, 2, 0, 1);
            CheckTrue("Histogram clamps and skips NaN", h[0] == 3 && h[1] == 3, h[0] + "," + h[1]);

            // noise from first differences of white noise
            var noise = new float[100000];
            var rr = new Random(7);
            for (int i = 0; i < noise.Length; i++) noise[i] = (float)(3 * Stats.NormalQuantile(Math.Max(1e-9, Math.Min(1 - 1e-9, rr.NextDouble()))));
            Check("Difference noise of σ=3 white noise", Stats.DifferenceNoise(noise, noise.Length), 3, 0.06);

            Check("Normal quantile 0.975", Stats.NormalQuantile(0.975), 1.959963985, 1e-8);
            Check("Normal CDF 1", Stats.NormalCdf(1), 0.841344746, 1e-7);
            Check("erf(0.5)", Stats.Erf(0.5), 0.520499878, 1e-7);
            // Cornish-Fisher expansion: about 1e-4 at 10 degrees of freedom, better above
            Check("Student t 0.975, 10 dof", Stats.StudentTQuantile(0.975, 10), 2.228138852, 3e-4);
            Check("Student t 0.975, 30 dof", Stats.StudentTQuantile(0.975, 30), 2.042272456, 2e-5);
        }

        [TestMethod]
        public void TestNumericalMethods()
        {
            // Nelder-Mead on the Rosenbrock function
            var x = NelderMead.Minimize(p => (1 - p[0]) * (1 - p[0]) + 100 * Math.Pow(p[1] - p[0] * p[0], 2), new[] { -1.2, 1.0 }, new[] { 0.5, 0.5 }, 10000, 1e-14);
            Check("Nelder-Mead Rosenbrock x", x[0], 1, 1e-4);
            Check("Nelder-Mead Rosenbrock y", x[1], 1, 1e-4);
            Check("Brent root of cos on [0, 3]", Roots.Brent(Math.Cos, 0, 3), Math.PI / 2, 1e-9);
            CheckTrue("Brent without a sign change", double.IsNaN(Roots.Brent(t => t * t + 1, -1, 1)));
            var roots = Roots.FindAll(Math.Sin, 0.5, 10, 0.3);
            CheckTrue("All roots of sin in [0.5, 10]", roots.Count == 3 && Math.Abs(roots[0] - Math.PI) < 1e-6 && Math.Abs(roots[2] - 3 * Math.PI) < 1e-6, string.Join(", ", roots.Select(q => q.ToString("0.000000"))));
            Check("Golden-section minimum", Roots.GoldenMin(t => (t - 2) * (t - 2) + 1, 0, 5), 2, 1e-6);

            var xs = Enumerable.Range(0, 21).Select(i => i * 0.3).ToList();
            var spline = new CubicSpline(xs, xs.Select(Math.Sin).ToList());
            Check("Cubic spline at a knot", spline.Evaluate(1.5), Math.Sin(1.5), 1e-12);
            Check("Cubic spline between knots", spline.Evaluate(2.05), Math.Sin(2.05), 2e-4);

            // MCMC on a Gaussian: median and 1σ interval
            var sampler = new EnsembleSampler(24, 1, p => -0.5 * Math.Pow((p[0] - 3) / 0.5, 2), 42);
            var samples = sampler.Run(new[] { 2.0 }, new[] { 0.1 }, 1500, 300);
            double[] med, lo, hi;
            EnsembleSampler.Summarize(samples, 1, out med, out lo, out hi);
            Check("MCMC median", med[0], 3, 0.05);
            Check("MCMC 1σ lower error", lo[0], 0.5, 0.06);
            Check("MCMC 1σ upper error", hi[0], 0.5, 0.06);
            CheckTrue("MCMC acceptance fraction sensible", sampler.AcceptanceFraction > 0.2 && sampler.AcceptanceFraction < 0.9, sampler.AcceptanceFraction.ToString("0.00"));

            // vectors and rotation matrices
            var v = new Vec3(1, 2, 3);
            Check("Dot product", Vec3.Dot(v, new Vec3(4, -5, 6)), 12, 0);
            var c = Vec3.Cross(Vec3.UnitX, Vec3.UnitY);
            CheckTrue("Cross product x × y = z", c.X == 0 && c.Y == 0 && c.Z == 1);
            Check("Angle between axes", Vec3.AngleBetween(Vec3.UnitX, Vec3.UnitZ), Math.PI / 2, 1e-15);
            Check("Normalized length", v.Normalized().Length, 1, 1e-15);
            double lon, lat, r;
            Vec3.FromSpherical(2.5, -0.7, 3).ToSpherical(out lon, out lat, out r);
            CheckTrue("Spherical round trip", Math.Abs(lon - 2.5) < 1e-12 && Math.Abs(lat + 0.7) < 1e-12 && Math.Abs(r - 3) < 1e-12);
            var rz = Mat3.RotZ(0.3);
            var back = rz.Transpose() * (rz * v);
            CheckTrue("Rotation transpose is its inverse", (back - v).Length < 1e-14);
            var prod = Mat3.RotX(0.2) * Mat3.RotX(0.5) * Vec3.UnitY;
            CheckTrue("Rotations compose", (prod - Mat3.RotX(0.7) * Vec3.UnitY).Length < 1e-14);
            Check("Rotation preserves length", (Mat3.RotY(1.1) * v).Length, v.Length, 1e-14);
        }

        [TestMethod]
        public void TestCoordinateHelpers()
        {
            double d2r = AstroMath.Deg2Rad;
            // Meeus 13.b: Venus from Washington, H = 64.352133°, δ = -6.719892°, φ = 38.921389° -> A = 68.0337° from south, h = 15.1249°
            var hz = Transforms.HaDecToAltAz(64.352133 * d2r, -6.719892 * d2r, 38.921389 * d2r);
            Check("Altitude (Meeus 13.b)", hz.AltDeg, 15.1249, 1e-4);
            Check("Azimuth from north (Meeus 13.b)", hz.AzDeg, 68.0337 + 180, 1e-4);
            double ha, dec;
            Transforms.AltAzToHaDec(hz.Az, hz.Alt, 38.921389 * d2r, out ha, out dec);
            Check("Alt-az back to hour angle", ha / d2r, 64.352133, 1e-9);
            Check("Alt-az back to declination", dec / d2r, -6.719892, 1e-9);
            Check("Parallactic angle on the meridian", Transforms.ParallacticAngle(0, 0.3, 0.9), 0, 1e-15);
            // Meeus 13.a: Pollux, ε = 23.4392911° -> λ = 113.215630°, β = 6.684170°
            double l, b, eps = 23.4392911 * d2r;
            Transforms.EquatorialToEcliptic(116.328942 * d2r, 28.026183 * d2r, eps, out l, out b);
            Check("Ecliptic longitude (Meeus 13.a)", l / d2r, 113.215630, 1e-5);
            Check("Ecliptic latitude (Meeus 13.a)", b / d2r, 6.684170, 1e-5);
            Transforms.EquatorialToGalactic(192.85948 * d2r, 27.12825 * d2r, out l, out b);
            Check("Galactic north pole", b / d2r, 90, 1e-3);
            Transforms.EquatorialToGalactic(266.40499 * d2r, -28.93617 * d2r, out l, out b);
            Check("Galactic centre longitude", AstroMath.NormalizeDeg180(l / d2r), 0, 0.01);
            Check("Galactic centre latitude", b / d2r, 0, 0.01);
            double ra, de;
            Transforms.GalacticToEquatorial(1.0, 0.4, out ra, out de);
            Transforms.EquatorialToGalactic(ra, de, out l, out b);
            CheckTrue("Galactic round trip", Math.Abs(l - 1.0) < 1e-10 && Math.Abs(b - 0.4) < 1e-10);
            Check("Airmass at the zenith", Transforms.Airmass(90 * d2r), 1, 1e-3);
            Check("Airmass at 30°", Transforms.Airmass(30 * d2r), 1.995, 0.01);
            CheckTrue("Airmass below the horizon", double.IsPositiveInfinity(Transforms.Airmass(-5 * d2r)));
            Check("Refraction at the horizon (arcmin)", Transforms.RefractionFromApparent(0) / d2r * 60, 34.5, 0.5);
            Check("Refraction at 45° (arcmin)", Transforms.RefractionFromApparent(45 * d2r) / d2r * 60, 0.99, 0.03);
            double hTrue = 10 * d2r;
            // Saemundsson and Bennett are fits to the same tables and agree to about 0.1′
            Check("Refraction formulas agree", Transforms.RefractionFromTrue(hTrue), Transforms.RefractionFromApparent(hTrue + Transforms.RefractionFromTrue(hTrue)), 0.1 / 60 * d2r);
            Check("No refraction deep below the horizon", Transforms.RefractionFromTrue(-10 * d2r), 0, 0);
            CheckTrue("Blue disperses above red", Transforms.AtmosphericDispersionArcsec(30 * d2r, 450, 650) > 1 && Transforms.AtmosphericDispersionArcsec(90 * d2r, 450, 650) < 1e-6);
            Check("Field rotation at the equator looking north on the horizon", Transforms.FieldRotationRate(0, 0, 0), 7.2921150e-5, 1e-12);
            double r50, d50;
            EpochConversion.B1950ToJ2000(0, 0, out r50, out d50);
            Check("B1950 origin precesses to RA ≈ 0.64°", r50 / d2r, 0.6406, 0.01);
        }

        [TestMethod]
        public void TestEvents()
        {
            var seasons = Events.Seasons(2024);
            var expect = new[] { new DateTime(2024, 3, 20, 3, 6, 0), new DateTime(2024, 6, 20, 20, 51, 0), new DateTime(2024, 9, 22, 12, 44, 0), new DateTime(2024, 12, 21, 9, 21, 0) };
            CheckTrue("Four seasons", seasons.Count == 4);
            // the analytic Earth orbit is good to several arcseconds, a few minutes of time for the Sun
            for (int i = 0; i < Math.Min(4, seasons.Count); i++)
                Check(seasons[i].Description + " 2024 (min)", (seasons[i].Time.ToDateTimeUtc() - expect[i]).TotalMinutes, 0, 5);

            var lunar = Events.LunarEclipses(AstroTime.CalendarToJd(2025, 1, 1), AstroTime.CalendarToJd(2026, 1, 1));
            CheckTrue("Two lunar eclipses in 2025", lunar.Count == 2, string.Join(" / ", lunar));
            if (lunar.Count == 2)
            {
                Check("Total lunar eclipse 2025-03-14 (min)", (lunar[0].Time.ToDateTimeUtc() - new DateTime(2025, 3, 14, 6, 58, 43)).TotalMinutes, 0, 10);
                Check("Total lunar eclipse 2025-09-07 (min)", (lunar[1].Time.ToDateTimeUtc() - new DateTime(2025, 9, 7, 18, 11, 48)).TotalMinutes, 0, 10);
                CheckTrue("Both are total (umbral magnitude > 1)", lunar[0].Value > 1 && lunar[1].Value > 1, lunar[0].Value.ToString("0.000") + ", " + lunar[1].Value.ToString("0.000"));
            }
            var solar = Events.SolarEclipses(AstroTime.CalendarToJd(2024, 4, 1), AstroTime.CalendarToJd(2024, 4, 30));
            CheckTrue("Solar eclipse in April 2024", solar.Count == 1, string.Join(" / ", solar));
            if (solar.Count == 1) Check("Total solar eclipse 2024-04-08 (min)", (solar[0].Time.ToDateTimeUtc() - new DateTime(2024, 4, 8, 18, 17, 21)).TotalMinutes, 0, 15);

            var mars = Events.PlanetEvents(Body.Mars, AstroTime.CalendarToJd(2024, 12, 1), AstroTime.CalendarToJd(2025, 3, 1)).Where(e => e.Kind == EventKind.Opposition).ToList();
            CheckTrue("Mars opposition found", mars.Count == 1);
            if (mars.Count == 1) Check("Mars opposition 2025-01-16 (h)", (mars[0].Time.ToDateTimeUtc() - new DateTime(2025, 1, 16, 2, 38, 0)).TotalHours, 0, 6);
            var venus = Events.PlanetEvents(Body.Venus, AstroTime.CalendarToJd(2024, 12, 1), AstroTime.CalendarToJd(2025, 4, 30));
            var ge = venus.Where(e => e.Kind == EventKind.GreatestElongationEast).ToList();
            CheckTrue("Venus greatest eastern elongation found", ge.Count == 1);
            if (ge.Count == 1) Check("Venus greatest elongation 2025-01-10 (days)", (ge[0].Time.ToDateTimeUtc() - new DateTime(2025, 1, 10, 3, 0, 0)).TotalDays, 0, 1);
            var conj = venus.Where(e => e.Kind == EventKind.Conjunction).ToList();
            CheckTrue("Venus inferior conjunction found", conj.Count == 1 && conj[0].Description.Contains("inferior"), conj.Count == 1 ? conj[0].Description : conj.Count + " conjunctions");
            if (conj.Count == 1) Check("Venus conjunction 2025-03-23 (days)", (conj[0].Time.ToDateTimeUtc() - new DateTime(2025, 3, 23, 1, 0, 0)).TotalDays, 0, 1);
            var vsup = Events.PlanetEvents(Body.Venus, AstroTime.CalendarToJd(2025, 12, 1), AstroTime.CalendarToJd(2026, 2, 28)).Where(e => e.Kind == EventKind.Conjunction).ToList();
            CheckTrue("Venus superior conjunction 2026-01-06", vsup.Count == 1 && vsup[0].Description.Contains("superior") && Math.Abs((vsup[0].Time.ToDateTimeUtc() - new DateTime(2026, 1, 6, 17, 0, 0)).TotalDays) < 1.5, string.Join(" / ", vsup));
            var jup = Events.PlanetEvents(Body.Jupiter, AstroTime.CalendarToJd(2025, 5, 1), AstroTime.CalendarToJd(2025, 8, 1)).Where(e => e.Kind == EventKind.Conjunction).ToList();
            CheckTrue("Jupiter conjunction 2025-06-24", jup.Count == 1 && Math.Abs((jup[0].Time.ToDateTimeUtc() - new DateTime(2025, 6, 24, 12, 0, 0)).TotalDays) < 1.5, string.Join(" / ", jup));
            var marsConj = Events.PlanetEvents(Body.Mars, AstroTime.CalendarToJd(2025, 12, 1), AstroTime.CalendarToJd(2026, 2, 28)).Where(e => e.Kind == EventKind.Conjunction).ToList();
            CheckTrue("Mars conjunction 2026-01-09", marsConj.Count == 1 && Math.Abs((marsConj[0].Time.ToDateTimeUtc() - new DateTime(2026, 1, 9, 12, 0, 0)).TotalDays) < 1.5, string.Join(" / ", marsConj));

            // twilight at Greenwich around the December solstice
            var greenwich = new Observer();
            var tw = Events.Twilight(greenwich, AstroTime.FromCalendar(2024, 12, 21, 12));
            CheckTrue("All twilight times found in winter", tw.SunSet.HasValue && tw.CivilDusk.HasValue && tw.NauticalDusk.HasValue && tw.AstronomicalDusk.HasValue && tw.AstronomicalDawn.HasValue && tw.SunRise.HasValue);
            if (tw.SunSet.HasValue && tw.SunRise.HasValue && tw.CivilDusk.HasValue && tw.NauticalDusk.HasValue && tw.AstronomicalDusk.HasValue && tw.AstronomicalDawn.HasValue)
            {
                Check("Sunset Greenwich 2024-12-21 (min)", (tw.SunSet.Value.ToDateTimeUtc() - new DateTime(2024, 12, 21, 15, 53, 0)).TotalMinutes, 0, 3);
                Check("Sunrise Greenwich 2024-12-22 (min)", (tw.SunRise.Value.ToDateTimeUtc() - new DateTime(2024, 12, 22, 8, 5, 0)).TotalMinutes, 0, 3);
                CheckTrue("Twilight in order", tw.SunSet < tw.CivilDusk && tw.CivilDusk < tw.NauticalDusk && tw.NauticalDusk < tw.AstronomicalDusk && tw.AstronomicalDusk < tw.AstronomicalDawn && tw.AstronomicalDawn < tw.SunRise);
            }
            var north = new Observer { LatitudeDeg = 56, LongitudeDeg = 13 };
            var summer = Events.Twilight(north, AstroTime.FromCalendar(2025, 6, 21, 12));
            CheckTrue("No astronomical darkness at 56° N at midsummer", !summer.AstronomicalDusk.HasValue && summer.SunSet.HasValue);

            var t0 = AstroTime.FromCalendar(2025, 1, 1);
            CheckTrue("Circumpolar star always up", Events.FixedRiseSet(0, 80 * AstroMath.Deg2Rad, north, t0).AlwaysUp);
            CheckTrue("Southern star never up", Events.FixedRiseSet(0, -60 * AstroMath.Deg2Rad, north, t0).NeverUp);
            var rs = Events.FixedRiseSet(0, 0, greenwich, t0);
            CheckTrue("Equatorial star rises, transits, sets", rs.Rise.HasValue && rs.Transit.HasValue && rs.Set.HasValue);
            // J2000 RA 0, Dec 0 has precessed to Dec ≈ +0.14° by 2025
            double decNow = new SkyContext(greenwich, t0).IcrsToApparent(0, 0).DecDeg;
            if (rs.Transit.HasValue) Check("Transit altitude of a star on the equator", rs.TransitAltitudeDeg, 90 - greenwich.LatitudeDeg + decNow, 0.05);
            Check("Moon age a day after new moon (2025-01-29 12:36 UTC)", Events.MoonAgeDays(AstroTime.FromCalendar(2025, 1, 30, 12.6).JdTT), 1, 0.05);
            Check("Moon age just before new moon", Events.MoonAgeDays(AstroTime.FromCalendar(2025, 1, 29, 10.6).JdTT), 29.5, 0.15);
            var apsides = Events.MoonDistanceExtremes(AstroTime.CalendarToJd(2025, 1, 1), AstroTime.CalendarToJd(2025, 3, 1));
            CheckTrue("Perigees and apogees alternate", apsides.Count >= 3 && Enumerable.Range(1, apsides.Count - 1).All(i => apsides[i].Kind != apsides[i - 1].Kind), apsides.Count + " events");
        }

        [TestMethod]
        public void TestPlanetPhysics()
        {
            Check("Jupiter equatorial radius (km)", PlanetPhysics.EquatorialRadiusKm(Body.Jupiter), 71492, 1);
            CheckTrue("Jupiter is oblate", PlanetPhysics.PolarRadiusKm(Body.Jupiter) < PlanetPhysics.EquatorialRadiusKm(Body.Jupiter));
            Check("Jupiter diameter at 4.2 AU (\")", PlanetPhysics.AngularDiameterArcsec(Body.Jupiter, 4.2), 46.94, 0.05);
            Check("Jupiter at opposition, r 5 Δ 4 (mag)", PlanetPhysics.Magnitude(Body.Jupiter, 5, 4, 0), -2.89, 0.05);
            CheckTrue("A larger phase angle is fainter", PlanetPhysics.Magnitude(Body.Mars, 1.5, 1, 40) > PlanetPhysics.Magnitude(Body.Mars, 1.5, 1, 5));
            // Meeus 29.a: 1992 October 13, 0h TT -> P = 26.27°, B0 = 5.99°, L0 = 238.63°
            double P, B0, L0; int cr;
            PlanetPhysics.SunOrientation(2448908.5, out P, out B0, out L0, out cr);
            Check("Sun P (Meeus 29.a)", P, 26.27, 0.02);
            Check("Sun B0 (Meeus 29.a)", B0, 5.99, 0.02);
            Check("Sun L0 (Meeus 29.a)", L0, 238.63, 0.05);
            // Meeus 53.a: 1992 April 12, 0h TT -> optical libration l' = -1.206°, b' = +4.194°, P = 15.08°
            double ll, lb, pa;
            PlanetPhysics.MoonLibration(2448724.5, out ll, out lb, out pa);
            Check("Moon libration in longitude (Meeus 53.a)", ll, -1.206, 0.03);
            Check("Moon libration in latitude (Meeus 53.a)", lb, 4.194, 0.03);
            Check("Moon axis position angle (Meeus 53.a)", pa, 15.08, 0.2);

            // a full place for Saturn and for the Moon
            var ctx = new SkyContext(new Observer(), AstroTime.FromCalendar(2025, 9, 21, 0));
            var sat = ctx.BodyPlace(Body.Saturn);
            PlanetPhysics.Fill(Body.Saturn, sat, ctx.JdTT);
            CheckTrue("Saturn near opposition: elongation > 170°", sat.Elongation * AstroMath.Rad2Deg > 170, (sat.Elongation * AstroMath.Rad2Deg).ToString("0.0"));
            CheckTrue("Saturn distance and magnitude", sat.Distance > 8 && sat.Distance < 9 && sat.Magnitude > -0.5 && sat.Magnitude < 1.5, sat.Distance.ToString("0.00") + " AU, mag " + sat.Magnitude.ToString("0.00"));
            Check("Saturn illuminated fraction", sat.IlluminatedFraction, 1, 0.001);
            var moon = ctx.BodyPlace(Body.Moon);
            CheckTrue("Moon distance 356 000 - 407 000 km", moon.Distance * 149597870.7 > 356000 && moon.Distance * 149597870.7 < 407000, (moon.Distance * 149597870.7).ToString("0"));
        }

        [TestMethod]
        public void TestObservingTools()
        {
            var sys = new ImagingSystem { ApertureMm = 80, FocalLengthMm = 480, PixelSizeUm = 3.76 };
            Check("Pixel scale (\"/px)", sys.PixelScaleArcsec, 1.6157, 1e-3);
            Check("Field width (°)", sys.FieldWidthDeg, 6248 * 1.6157 / 3600, 1e-3);
            Check("Focal ratio", sys.FocalRatio, 6, 0);
            Check("Critical focus zone f/5 (µm)", ExposureCalculator.CriticalFocusZoneUm(5), 67.1, 0.1);
            double one = ExposureCalculator.ExtendedSnr(sys, 22, 20, 300, 10), four = ExposureCalculator.ExtendedSnr(sys, 22, 20, 300, 40);
            Check("SNR grows with √(subs)", four / one, 2, 0.01);
            double t = ExposureCalculator.TimeForSnr(sys, 22, 20, 300, 20);
            Check("Time for SNR 20 gives SNR 20", ExposureCalculator.ExtendedSnr(sys, 22, 20, 300, (int)Math.Round(t / 300)), 20, 1.0);
            CheckTrue("Darker sky allows longer subs", ExposureCalculator.OptimalSubExposure(sys, 21.5) > ExposureCalculator.OptimalSubExposure(sys, 18.5));
            CheckTrue("Brighter star has higher SNR", ExposureCalculator.StarSnr(sys, 10, 20, 2.5, 60) > ExposureCalculator.StarSnr(sys, 14, 20, 2.5, 60));
            Check("Optimal planetary f-ratio for 2.9 µm at 550 nm", ExposureCalculator.OptimalPlanetaryFRatio(2.9), 10.55, 0.05);

            var eye = new ObserverEye { Age = 20 };
            Check("Young eye pupil (mm)", eye.PupilMm, 7.8, 1e-9);
            Check("Old eye pupil (mm)", new ObserverEye { Age = 90 }.PupilMm, 4.65, 1e-9);
            Check("Pupil never below 4.5 mm", new ObserverEye { Age = 120 }.PupilMm, 4.5, 1e-9);
            CheckTrue("Naked-eye limit is fainter under a dark sky", VisibilityModel.NakedEyeLimit(21.5) > VisibilityModel.NakedEyeLimit(18));
            Check("Sky brightness unit round trip", VisibilityModel.NanoLambertToMagArcsec2(VisibilityModel.MagArcsec2ToNanoLambert(20.5)), 20.5, 1e-9);
            var ins = new VisualInstrument();
            Check("Dawes limit of 203 mm (\")", ins.DawesLimit, 0.571, 0.001);
            Check("Magnification", ins.Magnification, 48, 0);
            Check("Bortle from SQM 22.0", Observer.SqmToBortle(22.0), 1, 0);
            Check("Bortle from SQM 21.9", Observer.SqmToBortle(21.9), 2, 0);
            Check("Bortle from SQM 17", Observer.SqmToBortle(17), 9, 0);
            CheckTrue("Bortle round trip", Enumerable.Range(1, 9).All(bo => Observer.SqmToBortle(Observer.BortleToSqm(bo)) == bo));

            // horizon profile interpolation
            var site = new Observer { HorizonProfile = new List<double[]> { new[] { 0.0, 10 }, new[] { 90.0, 20 }, new[] { 180.0, 0 } } };
            Check("Horizon profile interpolates", site.HorizonAltitude(45), 15, 1e-9);
        }

        [TestMethod]
        public void TestSatellitePasses()
        {
            var tle = Tle.Parse("ISS (ZARYA)", "1 25544U 98067A   08264.51782528 -.00002182  00000-0 -11606-4 0  2927",
                "2 25544  51.6416 247.4627 0006703 130.5360 325.0288 15.72125391563537");
            CheckTrue("TLE fields", tle.CatalogNumber == 25544 && Math.Abs(tle.Inclination - 51.6416) < 1e-9 && Math.Abs(tle.Eccentricity - 0.0006703) < 1e-12);
            Check("TLE B* drag term", tle.Bstar, -0.11606e-4, 1e-12);
            Check("TLE epoch 2008 day 264.5178", tle.EpochJd, AstroTime.CalendarToJd(2008, 1, 0) + 264.51782528, 1e-9);
            var sat = new Sgp4(tle);
            CheckTrue("Near-Earth orbit", !sat.IsDeepSpace && sat.Error == null);
            var obs = new Observer { LatitudeDeg = 52, LongitudeDeg = 5, ElevationM = 0 };
            var passes = SatelliteTools.Passes(sat, obs, new AstroTime(tle.EpochJd), 2, 10);
            CheckTrue("ISS passes found in two days", passes.Count >= 3, passes.Count + " passes");
            bool ordered = passes.All(p => p.Rise < p.Culmination && p.Culmination < p.Set && p.MaxAltDeg >= 10 && p.MaxAltDeg <= 90);
            CheckTrue("Each pass rises, culminates and sets", ordered);
            bool culminates = passes.All(p => Math.Abs(SatelliteTools.Look(sat, obs, p.Culmination).AltDeg - p.MaxAltDeg) < 0.5);
            CheckTrue("Culmination is the highest point", culminates);
            var look = SatelliteTools.Look(sat, obs, new AstroTime(tle.EpochJd));
            CheckTrue("Height of the ISS 330-370 km in 2008", look.HeightKm > 320 && look.HeightKm < 380, look.HeightKm.ToString("0"));
        }

        [TestMethod]
        public void TestCatalogQueries()
        {
            var cm = new CatalogManager();
            cm.LoadBuiltIn();
            var vega = cm.Find("Vega");
            CheckTrue("Find a star by name", vega is Star && Math.Abs(vega.RaHours - 18.6156) < 0.01, vega == null ? "null" : vega.RaHours.ToString("0.0000"));
            var m31 = cm.Find("M31") ?? cm.Find("M 31");
            CheckTrue("Find M31", m31 is DeepSkyObject && Math.Abs(m31.DecDeg - 41.27) < 0.1);
            CheckTrue("Unknown and empty names", cm.Find("No such object 123") == null && cm.Find("  ") == null);
            var hits = cm.Search("Veg");
            CheckTrue("Prefix search finds Vega first", hits.Count > 0 && hits[0] == vega, hits.Count > 0 ? hits[0].ToString() : "none");
            CheckTrue("Search limit", cm.Search("M", 5).Count <= 5);
            var cons = cm.ConstellationAt(vega.Ra, vega.Dec);
            CheckTrue("Vega is in Lyra", cons != null && (cons.Name == "Lyra" || cons.Abbr == "Lyr"), cons == null ? "null" : cons.Name);

            var index = new SkyIndex<Star>();
            var rnd = new Random(3);
            var all = new List<Star>();
            for (int i = 0; i < 5000; i++)
            {
                var s = new Star { Ra = rnd.NextDouble() * AstroMath.TwoPi, Dec = Math.Asin(2 * rnd.NextDouble() - 1), Mag = rnd.NextDouble() * 10 };
                all.Add(s); index.Add(s);
            }
            index.Finish();
            CheckTrue("Index count", index.Count == 5000 && index.All().Count() == 5000);
            foreach (var probe in new[] { new[] { 1.0, 0.2 }, new[] { 0.01, 1.5 }, new[] { 6.27, -1.4 } })
            {
                var found = new List<Star>();
                double radius = 5 * AstroMath.Deg2Rad;
                index.Cone(probe[0], probe[1], radius, 7, found);
                var c = Vec3.FromSpherical(probe[0], probe[1]);
                var brute = all.Where(s => s.Mag <= 7 && Vec3.AngleBetween(c, Vec3.FromSpherical(s.Ra, s.Dec)) <= radius).ToList();
                CheckTrue(string.Format("Cone search matches brute force at ({0}, {1})", probe[0], probe[1]), found.Count == brute.Count && !brute.Except(found).Any(), found.Count + " vs " + brute.Count);
            }
        }

        /// <summary>Write a minimal NAIF SPK (DAF) file with type 2 Chebyshev segments.</summary>
        private static void WriteSpk(string path, bool bigEndian, IList<Tuple<int, int, double, double, double[][]>> segments, int coefficients)
        {
            // segments: target, centre, start ET, interval length, per record [x coeffs, y coeffs, z coeffs] flattened
            var data = new List<double>();
            var summaries = new List<Tuple<int, int, double, double, int, int>>();
            int firstAddr = 3 * 128 + 1; // data start in record 4 (addresses count 8-byte words from 1)
            foreach (var seg in segments)
            {
                int start = firstAddr + data.Count;
                double init = seg.Item3, len = seg.Item4;
                for (int r = 0; r < seg.Item5.Length; r++)
                {
                    data.Add(init + (r + 0.5) * len); data.Add(len / 2);
                    data.AddRange(seg.Item5[r]);
                }
                data.Add(init); data.Add(len); data.Add(2 + 3 * coefficients); data.Add(seg.Item5.Length);
                summaries.Add(Tuple.Create(seg.Item1, seg.Item2, init, init + len * seg.Item5.Length, start, firstAddr + data.Count - 1));
            }
            Func<double, byte[]> D = v => { var b = BitConverter.GetBytes(v); if (bigEndian) Array.Reverse(b); return b; };
            Func<int, byte[]> I = v => { var b = BitConverter.GetBytes(v); if (bigEndian) Array.Reverse(b); return b; };
            var file = new byte[3 * 1024 + data.Count * 8];
            Action<int, byte[]> put = (off, b) => Array.Copy(b, 0, file, off, b.Length);
            put(0, Encoding.ASCII.GetBytes("DAF/SPK "));
            put(8, I(2)); put(12, I(6));
            put(16, Encoding.ASCII.GetBytes("Sidralune test kernel".PadRight(60)));
            put(76, I(2)); put(80, I(2)); put(84, I(firstAddr + data.Count));
            put(88, Encoding.ASCII.GetBytes(bigEndian ? "BIG-IEEE" : "LTL-IEEE"));
            int sr = 1024;
            put(sr, D(0)); put(sr + 8, D(0)); put(sr + 16, D(summaries.Count));
            for (int i = 0; i < summaries.Count; i++)
            {
                int off = sr + 24 + i * 40;
                var m = summaries[i];
                put(off, D(m.Item3)); put(off + 8, D(m.Item4));
                put(off + 16, I(m.Item1)); put(off + 20, I(m.Item2)); put(off + 24, I(1)); put(off + 28, I(2)); put(off + 32, I(m.Item5)); put(off + 36, I(m.Item6));
            }
            for (int i = 0; i < data.Count; i++) put(3 * 1024 + i * 8, D(data[i]));
            File.WriteAllBytes(path, file);
        }

        [TestMethod]
        public void TestSpkReader()
        {
            // Earth-Moon barycentre about the SSB and the Earth about the EMB, as Chebyshev series over 10-day records:
            // x = a0 + a1 T1(tau) + a2 T2(tau), so positions and velocities are known exactly
            double day = 86400, start = -5 * day, len = 10 * day;
            Func<double, double[]> emb = k => new[] { 1.4e8 + k, 2e6, 3e3, 5e7, -1e6, 0, 2e7, 4e5, 1e2 };
            Func<double, double[]> earth = k => new[] { 4000 + k, 100, 0, -3000, 0, 50, 1500, 0, 0 };
            var segs = new List<Tuple<int, int, double, double, double[][]>>
            {
                Tuple.Create(3, 0, start, len, new[] { emb(0), emb(1000) }),
                Tuple.Create(399, 3, start, len, new[] { earth(0), earth(10) })
            };
            foreach (bool big in new[] { false, true })
            {
                string path = Path.Combine(Path.GetTempPath(), "SidraluneTests", big ? "big.bsp" : "little.bsp");
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                WriteSpk(path, big, segs, 3);
                using (var spk = new SpkEphemeris(path))
                {
                    string order = big ? " (big-endian)" : " (little-endian)";
                    CheckTrue("SPK segments" + order, spk.HasTarget(3) && spk.HasTarget(399) && !spk.HasTarget(301) && spk.Describe().Count() == 2);
                    CheckTrue("SPK coverage" + order, spk.Covers(2451545.0) && spk.Covers(2451545.0 + 14.9) && !spk.Covers(2451545.0 + 15.1));
                    // second record, tau = 0.5 at ET = 12.5 days
                    double et = 12.5 * day, tau = 0.5;
                    Func<double, double, double, double> cheb = (a0, a1, a2) => a0 + a1 * tau + a2 * (2 * tau * tau - 1);
                    Func<double, double, double> dcheb = (a1, a2) => (a1 + a2 * 4 * tau) / (len / 2);
                    double x = cheb(1.4e8 + 1000, 2e6, 3e3) + cheb(4010, 100, 0), y = cheb(5e7, -1e6, 0) + cheb(-3000, 0, 50), z = cheb(2e7, 4e5, 1e2) + cheb(1500, 0, 0);
                    Vec3 p, v;
                    CheckTrue("Barycentric state through the EMB" + order, spk.BarycentricState(399, et, out p, out v));
                    CheckTrue("SPK position (km)" + order, Math.Abs(p.X - x) < 1e-6 && Math.Abs(p.Y - y) < 1e-6 && Math.Abs(p.Z - z) < 1e-6, p.ToString());
                    Check("SPK velocity (km/s)" + order, v.X, dcheb(2e6, 3e3) + dcheb(100, 0), 1e-12);
                    spk.State(Body.Earth, 2451545.0 + 12.5, out p, out v);
                    Check("State in AU" + order, p.X, x / AstroMath.AU_km, 1e-15);
                    Check("Velocity in AU/day" + order, v.Y, (dcheb(-1e6, 0) + dcheb(0, 50)) * 86400 / AstroMath.AU_km, 1e-15);
                    bool outside = false;
                    try { spk.State(Body.Earth, 2451545.0 + 40, out p, out v); } catch (ArgumentOutOfRangeException) { outside = true; }
                    CheckTrue("Outside coverage reported" + order, outside);
                    CheckTrue("Missing body reported" + order, !spk.BarycentricState(SpkEphemeris.NaifId(Body.Mars), et, out p, out v));
                }
            }
            CheckTrue("NAIF ids", SpkEphemeris.NaifId(Body.Moon) == 301 && SpkEphemeris.NaifId(Body.Earth) == 399 && SpkEphemeris.NaifId(Body.Sun) == 10);
            string notSpk = Path.Combine(Path.GetTempPath(), "SidraluneTests", "not.bsp");
            File.WriteAllBytes(notSpk, new byte[2048]);
            bool rejected = false;
            try { new SpkEphemeris(notSpk).Dispose(); } catch (InvalidDataException) { rejected = true; }
            CheckTrue("Not an SPK file", rejected);
        }

        [TestMethod]
        public void TestLogAndPaths()
        {
            var got = new List<string>();
            Action<Log.Entry> h = e => got.Add(e.Level + ":" + e.Source + ":" + e.Message);
            Log.Logged += h;
            try
            {
                Log.Warn("Test", "careful");
                Log.Error("Test", new InvalidOperationException("boom"));
            }
            finally { Log.Logged -= h; }
            CheckTrue("Log entries raised", got.Contains("Warning:Test:careful") && got.Contains("Error:Test:boom"), string.Join(" | ", got));
            CheckTrue("Log snapshot keeps entries", Log.Snapshot().Any(e => e.Message == "careful"));

            string old = AppPaths.Root;
            string root = Path.Combine(Path.GetTempPath(), "SidraluneTests", "paths");
            try
            {
                AppPaths.Root = root;
                CheckTrue("Data folders created under the root", Directory.Exists(AppPaths.Catalogs) && AppPaths.Catalogs.StartsWith(root) && Directory.Exists(AppPaths.Logs));
            }
            finally { AppPaths.Root = old; }
            CheckTrue("URL encoding", Net.UrlEncode("a b&c=é") == "a%20b%26c%3D%C3%A9" && Net.UrlEncode(null) == "");
            var ds = new Dataset { ApproxBytes = 3 * 1048576, Folder = root, FileName = "none.dat" };
            CheckTrue("Dataset size text and installed state", ds.SizeText == (3.0).ToString("0.0") + " MB" && !ds.Installed && ds.InstalledDate == null);
            CheckTrue("Known datasets have ids and URLs", DataManager.Datasets.Count > 0 && DataManager.Datasets.All(d => !string.IsNullOrEmpty(d.Id) && !string.IsNullOrEmpty(d.Url)) && DataManager.Get(DataManager.Datasets[0].Id) == DataManager.Datasets[0]);
        }
    }
}
