using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Sidralune.Core.Mathematics;
using Sidralune.Core.Util;
using Sidralune.Imaging;
using Sidralune.Imaging.Analysis;
using Sidralune.Imaging.Registration;

namespace Sidralune.Science.Astrometry
{
    public class SolveOptions
    {
        /// <summary>Approximate centre (deg); NaN for blind.</summary>
        public double HintRa = double.NaN, HintDec = double.NaN;
        /// <summary>Search radius around the hint (deg).</summary>
        public double SearchRadius = 5;
        /// <summary>Approximate pixel scale (arcsec/px); NaN = unknown (scale-free matching).</summary>
        public double ScaleHint = double.NaN;
        public double ScaleTolerance = 0.2;
        public int MaxImageStars = 80;
        public int MaxCatalogStars = 400;
        public double MagLimit = double.NaN;  // auto from field size
        public int SipOrder = 2;
        public double DetectionSigma = 5;
        public IStarCatalogProvider Catalog;
        public bool AllowBlind = true;
    }

    public class SolveResult
    {
        public bool Success;
        public Wcs Wcs;
        public string Message;
        public TimeSpan Elapsed;
        public int ImageStars;
        public List<Tuple<DetectedStar, RefStar>> Matches = new List<Tuple<DetectedStar, RefStar>>();
    }

    /// <summary>
    /// Internal plate solver. Image stars are matched against catalog stars projected on the tangent plane using
    /// scale/rotation-invariant triangle matching, followed by an iteratively refined TAN-SIP fit.
    /// Near solving uses a position hint; blind solving tiles the sky (practical for wide fields with the local catalog
    /// or any field via online Gaia when a rough position is known).
    /// </summary>
    public static class PlateSolver
    {
        public static SolveResult Solve(AstroImage img, SolveOptions opt, IProgress<string> status = null, CancellationToken ct = default(CancellationToken))
        {
            var sw = Stopwatch.StartNew();
            var stars = StarDetector.Detect(img, new StarDetectionOptions { Sigma = opt.DetectionSigma, MaxStars = 1000 });
            var r = Solve(stars, img.Width, img.Height, opt, status, ct);
            r.Elapsed = sw.Elapsed;
            r.ImageStars = stars.Count;
            if (r.Success) r.Wcs.WriteToHeader(img.Header);
            return r;
        }

        public static SolveResult Solve(List<DetectedStar> stars, int width, int height, SolveOptions opt, IProgress<string> status = null, CancellationToken ct = default(CancellationToken))
        {
            var res = new SolveResult();
            if (stars.Count < 6) { res.Message = "Too few stars detected (" + stars.Count + ")"; return res; }
            var catalog = opt.Catalog ?? new LocalCatalogProvider();
            var imgStars = stars.Where(s => !s.Saturated).OrderByDescending(s => s.Flux).Take(opt.MaxImageStars).ToList();
            if (imgStars.Count < 6) imgStars = stars.OrderByDescending(s => s.Flux).Take(opt.MaxImageStars).ToList();
            var fovs = new List<double>();
            if (!double.IsNaN(opt.ScaleHint)) fovs.Add(Math.Sqrt(width * width + height * height) * opt.ScaleHint / 3600.0);
            // near a hint, the likely field sizes come first; a blind search tiles the whole sky, so it starts with the wide
            // sizes (tens to hundreds of tiles) before the narrow ones (tens of thousands)
            else if (!double.IsNaN(opt.HintRa)) foreach (var f in new[] { 1.0, 2.0, 0.5, 4.0, 0.25, 8.0, 16.0, 32.0, 60.0 }) fovs.Add(f);
            else foreach (var f in new[] { 60.0, 32.0, 16.0, 8.0, 4.0, 2.0, 1.0, 0.5, 0.25 }) fovs.Add(f);
            int tried = 0;
            foreach (var fovDeg in fovs)
            {
                var centres = new List<double[]>();
                if (!double.IsNaN(opt.HintRa))
                {
                    centres.Add(new[] { opt.HintRa, opt.HintDec });
                    double step = Math.Max(0.1, fovDeg * 0.5);
                    for (double rad = step; rad <= opt.SearchRadius; rad += step)
                    {
                        int n = (int)Math.Ceiling(2 * Math.PI * rad / step);
                        for (int k = 0; k < n; k++)
                        {
                            double a = 2 * Math.PI * k / n;
                            double dec = opt.HintDec + rad * Math.Cos(a);
                            if (dec > 90 || dec < -90) continue;
                            double ra = opt.HintRa + rad * Math.Sin(a) / Math.Max(0.05, Math.Cos(dec * AstroMath.Deg2Rad));
                            centres.Add(new[] { AstroMath.NormalizeDeg(ra), dec });
                        }
                        if (centres.Count > 400) break;
                    }
                }
                else if (opt.AllowBlind)
                {
                    // blind: cover the sphere with tiles of ~FOV size; only practical for wide fields with a local catalog
                    if (fovDeg < 2 && !(opt.Catalog != null && !opt.Catalog.IsOnline)) continue;
                    double step = Math.Max(0.5, fovDeg * 0.6);
                    for (double dec = -90 + step / 2; dec < 90; dec += step)
                    {
                        int n = Math.Max(1, (int)Math.Round(360 * Math.Cos(dec * AstroMath.Deg2Rad) / step));
                        for (int k = 0; k < n; k++) centres.Add(new[] { 360.0 * k / n, dec });
                    }
                }
                else { res.Message = "No position hint and blind solving disabled"; return res; }
                double searchR = fovDeg * 0.6;
                double magLim = !double.IsNaN(opt.MagLimit) ? opt.MagLimit : AstroMath.Clamp(9.5 + 2.5 * Math.Log10(Math.Max(1e-3, 30.0 / (fovDeg * fovDeg))), 6, 18);
                foreach (var c in centres)
                {
                    ct.ThrowIfCancellationRequested();
                    tried++;
                    if (status != null && (tried % 10 == 1)) status.Report(string.Format("Searching {0} {1}, field {2:0.##}° ({3}/{4})", AstroMath.FormatHms(c[0] / 15, 0), AstroMath.FormatDms(c[1], 0), fovDeg, tried, centres.Count));
                    List<RefStar> cat;
                    try { cat = catalog.Cone(c[0], c[1], searchR, magLim, opt.MaxCatalogStars); }
                    catch (Exception ex) { res.Message = "Catalog query failed: " + ex.Message; return res; }
                    if (cat.Count < 6) continue;
                    var w = TryMatch(imgStars, stars, cat, c[0], c[1], width, height, opt, false) ?? TryMatch(imgStars, stars, cat, c[0], c[1], width, height, opt, true);
                    if (w != null && Plausible(w, fovDeg, width, height))
                    {
                        double cra, cdec;
                        w.Centre(out cra, out cdec);
                        var cat2 = catalog.Cone(cra, cdec, Math.Max(w.FieldWidthDeg, w.FieldHeightDeg) * 0.8, magLim + 1.5, opt.MaxCatalogStars * 3);
                        var matches = new List<Tuple<DetectedStar, RefStar>>();
                        var refined = RefineAll(w, stars, cat2, width, height, opt.SipOrder, matches) ?? w;
                        // a chance match of a handful of stars does not survive matching the whole field
                        if (matches.Count < MinRefinedMatches || !Plausible(refined, fovDeg, width, height)) continue;
                        res.Matches.AddRange(matches);
                        res.Wcs = refined;
                        res.Success = true;
                        res.Message = "Solved: " + res.Wcs;
                        return res;
                    }
                }
            }
            res.Message = "No solution found (" + tried + " fields tried)";
            return res;
        }

        private const int MinRefinedMatches = 10;

        /// <summary>
        /// Reject geometrically impossible solutions: a camera has square pixels and no skew, so the CD matrix must be a
        /// rotation times one scale (a flattened matrix can have a tiny area-based scale while one axis spans tens of
        /// degrees), and the solved field must be about the size of the field that was searched.
        /// </summary>
        private static bool Plausible(Wcs w, double fovDeg, int width, int height)
        {
            if (w == null || !w.IsValid) return false;
            double sx = Math.Sqrt(w.Cd11 * w.Cd11 + w.Cd21 * w.Cd21), sy = Math.Sqrt(w.Cd12 * w.Cd12 + w.Cd22 * w.Cd22);
            if (sx <= 0 || sy <= 0 || Math.Max(sx, sy) / Math.Min(sx, sy) > 1.15) return false;
            if (Math.Abs(w.Cd11 * w.Cd12 + w.Cd21 * w.Cd22) / (sx * sy) > 0.1) return false;
            double diagDeg = w.PixelScale * Math.Sqrt((double)width * width + (double)height * height) / 3600.0;
            return diagDeg > fovDeg / 3 && diagDeg < fovDeg * 3;
        }

        private static Wcs TryMatch(List<DetectedStar> imgStars, List<DetectedStar> allStars, List<RefStar> cat, double ra0, double dec0, int width, int height, SolveOptions opt, bool mirrored)
        {
            double sx = mirrored ? 1 : -1;
            // project catalog onto tangent plane in arcsec, flip x so it behaves like an image (east left)
            var proj = new List<DetectedStar>();
            foreach (var s in cat)
            {
                double xi, eta;
                if (!Wcs.SphericalToStandard(s.Ra * AstroMath.Deg2Rad, s.Dec * AstroMath.Deg2Rad, ra0 * AstroMath.Deg2Rad, dec0 * AstroMath.Deg2Rad, out xi, out eta)) continue;
                proj.Add(new DetectedStar { X = sx * xi * AstroMath.Rad2ArcSec, Y = -eta * AstroMath.Rad2ArcSec, Flux = Math.Pow(10, -0.4 * s.Mag) });
            }
            if (proj.Count < 6) return null;
            var saveN = StarMatcher.MaxStars;
            StarMatcher.MaxStars = Math.Min(60, Math.Max(imgStars.Count, proj.Count));
            // tolerance scaled: matching in mixed units (pixels vs arcsec) is fine as RANSAC fits the similarity scale
            double thr = !double.IsNaN(opt.ScaleHint) ? 3.0 * opt.ScaleHint : 15.0;
            var m = StarMatcher.Match(proj, imgStars, TransformModel.Similarity, thr / 2);
            StarMatcher.MaxStars = saveN;
            if (!m.Success || m.Matches < 6) return null;
            // check scale plausibility (arcsec per pixel)
            double scale = m.Transform.Scale;
            if (!double.IsNaN(opt.ScaleHint) && Math.Abs(scale - opt.ScaleHint) > opt.ScaleTolerance * opt.ScaleHint + 0.05) return null;
            if (scale < 0.05 || scale > 600) return null;
            // build pairs image star -> catalog star
            var xs = new List<double>(); var ys = new List<double>(); var ras = new List<double>(); var decs = new List<double>();
            foreach (var p in m.Pairs)
            {
                int ci = proj.IndexOf(p.Item2);
                if (ci < 0) continue;
                xs.Add(p.Item1.X); ys.Add(p.Item1.Y);
                // recover catalog star: inverse projection
                double ra, dec;
                Wcs.StandardToSpherical(sx * p.Item2.X * AstroMath.ArcSec2Rad, -p.Item2.Y * AstroMath.ArcSec2Rad, ra0 * AstroMath.Deg2Rad, dec0 * AstroMath.Deg2Rad, out ra, out dec);
                ras.Add(ra); decs.Add(dec);
            }
            if (xs.Count < 6) return null;
            var w = Wcs.Fit(xs, ys, ras, decs, width, height, 0);
            if (w == null || double.IsNaN(w.RmsArcsec) || w.RmsArcsec > Math.Max(5, 3 * w.PixelScale)) return null;
            return w;
        }

        /// <summary>Match all detected stars to catalog stars using the provisional WCS and fit TAN-SIP.</summary>
        public static Wcs RefineAll(Wcs w, List<DetectedStar> stars, List<RefStar> cat, int width, int height, int sipOrder, List<Tuple<DetectedStar, RefStar>> matchesOut = null)
        {
            for (int pass = 0; pass < 3; pass++)
            {
                var proj = new List<Tuple<double, double, RefStar>>();
                foreach (var c in cat)
                {
                    double x, y;
                    if (w.WorldToPixel(c.Ra, c.Dec, out x, out y) && x > -20 && y > -20 && x < width + 20 && y < height + 20) proj.Add(Tuple.Create(x, y, c));
                }
                double tol = Math.Max(2.0, 3 * (w.RmsArcsec / Math.Max(1e-6, w.PixelScale)));
                var pairs = new List<Tuple<DetectedStar, RefStar>>();
                foreach (var s in stars)
                {
                    Tuple<double, double, RefStar> best = null; double bd = tol * tol;
                    foreach (var p in proj)
                    {
                        double d = (p.Item1 - s.X) * (p.Item1 - s.X) + (p.Item2 - s.Y) * (p.Item2 - s.Y);
                        if (d < bd) { bd = d; best = p; }
                    }
                    if (best != null) pairs.Add(Tuple.Create(s, best.Item3));
                }
                // remove duplicate catalog assignments
                pairs = pairs.GroupBy(p => p.Item2).Select(g => g.OrderBy(p => p.Item1.Flux).Last()).ToList();
                if (pairs.Count < 6) break;
                int order = pairs.Count > 40 ? sipOrder : 0;
                var nw = Wcs.Fit(pairs.Select(p => p.Item1.X).ToList(), pairs.Select(p => p.Item1.Y).ToList(),
                    pairs.Select(p => p.Item2.Ra).ToList(), pairs.Select(p => p.Item2.Dec).ToList(), width, height, order);
                if (nw == null) break;
                w = nw;
                if (matchesOut != null) { matchesOut.Clear(); matchesOut.AddRange(pairs); }
            }
            return w;
        }
    }

    /// <summary>Bridges to external solvers when installed: ASTAP command line and nova.astrometry.net web API.</summary>
    public static class ExternalSolvers
    {
        public static string AstapPath
        {
            get
            {
                foreach (var p in new[] { @"C:\Program Files\astap\astap.exe", @"C:\Program Files (x86)\astap\astap.exe", @"C:\astap\astap.exe" })
                    if (File.Exists(p)) return p;
                return null;
            }
        }

        /// <summary>Run ASTAP (-f file -r radius -fov fov [-ra -spd]) and read the resulting .wcs file.</summary>
        public static Wcs SolveWithAstap(string fitsPath, int width, int height, double hintRaDeg, double hintDecDeg, double fovDeg, double radiusDeg = 30, string astap = null)
        {
            astap = astap ?? AstapPath;
            if (astap == null) throw new FileNotFoundException("ASTAP is not installed");
            var args = string.Format(CultureInfo.InvariantCulture, "-f \"{0}\" -r {1:0.#} -fov {2:0.###}", fitsPath, radiusDeg, double.IsNaN(fovDeg) ? 0 : fovDeg);
            if (!double.IsNaN(hintRaDeg)) args += string.Format(CultureInfo.InvariantCulture, " -ra {0:0.#####} -spd {1:0.#####}", hintRaDeg / 15.0, hintDecDeg + 90);
            var psi = new ProcessStartInfo(astap, args) { UseShellExecute = false, CreateNoWindow = true };
            using (var p = Process.Start(psi)) p.WaitForExit(180000);
            string wcs = Path.ChangeExtension(fitsPath, ".wcs");
            if (!File.Exists(wcs)) return null;
            var h = new FitsHeader();
            foreach (var line in ReadCards(wcs))
            {
                if (line.Length < 10 || line[8] != '=') continue;
                string key = line.Substring(0, 8).Trim(), rest = line.Substring(10);
                int slash = rest.IndexOf('/');
                h.Cards.Add(new FitsCard(key, (slash >= 0 && !rest.TrimStart().StartsWith("'") ? rest.Substring(0, slash) : rest).Trim()));
            }
            return Wcs.FromHeader(h, width, height);
        }

        private static IEnumerable<string> ReadCards(string path)
        {
            var text = File.ReadAllText(path);
            if (text.Contains("\n")) return text.Split('\n');
            var l = new List<string>();
            for (int i = 0; i + 80 <= text.Length; i += 80) l.Add(text.Substring(i, 80));
            return l;
        }

        /// <summary>
        /// Solve via nova.astrometry.net (requires the user's API key). Uploads the file, polls the job and downloads the WCS.
        /// </summary>
        public static Wcs SolveWithNova(string imagePath, string apiKey, int width, int height, IProgress<string> status = null, CancellationToken ct = default(CancellationToken))
        {
            const string api = "http://nova.astrometry.net/api/";
            var login = Json.ParseObject(Net.PostForm(api + "login", new Dictionary<string, string> { { "request-json", Json.Serialize(new Dictionary<string, object> { { "apikey", apiKey } }) } }));
            string session = Json.GetString(login, "session");
            if (string.IsNullOrEmpty(session)) throw new InvalidOperationException("astrometry.net login failed");
            status?.Report("Uploading to astrometry.net");
            var req = Json.Serialize(new Dictionary<string, object> { { "session", session }, { "allow_commercial_use", "n" }, { "allow_modifications", "n" }, { "publicly_visible", "n" } });
            var up = Json.ParseObject(Net.PostMultipart(api + "upload", new Dictionary<string, string> { { "request-json", req } }, "file", Path.GetFileName(imagePath), File.ReadAllBytes(imagePath)));
            string subid = Json.GetString(up, "subid");
            string jobId = null;
            for (int i = 0; i < 120 && jobId == null; i++)
            {
                ct.ThrowIfCancellationRequested();
                Thread.Sleep(5000);
                var sub = Json.ParseObject(Net.GetString(api + "submissions/" + subid));
                var jobs = Json.Get(sub, "jobs") as List<object>;
                if (jobs != null && jobs.Count > 0 && jobs[0] != null) jobId = Convert.ToString(jobs[0], CultureInfo.InvariantCulture);
                status?.Report("Waiting for astrometry.net job...");
            }
            if (jobId == null) return null;
            for (int i = 0; i < 120; i++)
            {
                ct.ThrowIfCancellationRequested();
                var js = Json.ParseObject(Net.GetString(api + "jobs/" + jobId));
                string st = Json.GetString(js, "status");
                if (st == "success")
                {
                    string wcsText = Net.GetString("http://nova.astrometry.net/wcs_file/" + jobId);
                    var h = new FitsHeader();
                    for (int k = 0; k + 80 <= wcsText.Length; k += 80)
                    {
                        string line = wcsText.Substring(k, 80);
                        if (line[8] != '=') continue;
                        string rest = line.Substring(10);
                        int slash = rest.IndexOf('/');
                        h.Cards.Add(new FitsCard(line.Substring(0, 8).Trim(), (slash >= 0 && !rest.TrimStart().StartsWith("'") ? rest.Substring(0, slash) : rest).Trim()));
                    }
                    return Wcs.FromHeader(h, width, height);
                }
                if (st == "failure") return null;
                Thread.Sleep(5000);
                status?.Report("astrometry.net solving (" + st + ")");
            }
            return null;
        }
    }
}
