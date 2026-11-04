using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;

namespace Sidralune.Core.Util
{
    /// <summary>A downloadable dataset (catalog, ephemeris, orbit file).</summary>
    public class Dataset
    {
        public string Id, Name, Description, Url, FileName, Folder, License;
        /// <summary>Other places with the same file, tried in turn when Url cannot be reached.</summary>
        public string[] Mirrors;
        /// <summary>Turns the downloaded file (first path) into the installed one (second path), e.g. to keep only what is used.</summary>
        public Action<string, string> Prepare;
        public long ApproxBytes;
        public bool Gzip;
        public bool Recommended;
        public string LocalPath => Path.Combine(Folder, FileName);
        public bool Installed => File.Exists(LocalPath);
        public DateTime? InstalledDate => Installed ? File.GetLastWriteTime(LocalPath) : (DateTime?)null;
        public string SizeText => ApproxBytes > 1 << 20 ? (ApproxBytes / 1048576.0).ToString("0.0") + " MB" : (ApproxBytes / 1024.0).ToString("0") + " KB";
    }

    /// <summary>
    /// Registry of optional data that Sidralune can download on request. Nothing is downloaded automatically; the
    /// user chooses in Settings → Data. Sources are public scientific archives.
    /// </summary>
    public static class DataManager
    {
        public static readonly List<Dataset> Datasets = new List<Dataset>
        {
            new Dataset { Id = "de440s", Name = Lang.N("JPL DE440s planetary ephemeris"), Description = Lang.N("High precision Sun, Moon and planets 1849–2150 (milliarcsecond)."), Url = "https://ssd.jpl.nasa.gov/ftp/eph/planets/bsp/de440s.bsp", FileName = "de440s.bsp", Folder = AppPaths.Ephemerides, ApproxBytes = 32726016, Recommended = true, License = "JPL/NASA public" },
            new Dataset { Id = "hyg", Name = Lang.N("HYG star database v4.1"), Description = Lang.N("~120,000 stars (Hipparcos, Yale BSC, Gliese) with names, Bayer/Flamsteed, colours, proper motions."), Url = "https://raw.githubusercontent.com/astronexus/HYG-Database/main/hyg/CURRENT/hygdata_v41.csv", FileName = "hygdata_v41.csv", Folder = AppPaths.Catalogs, ApproxBytes = 33932548, Recommended = true, License = "CC BY-SA 4.0" },
            new Dataset { Id = "openngc", Name = Lang.N("OpenNGC catalogue"), Description = Lang.N("NGC/IC objects with sizes, magnitudes, surface brightness and common names (Mattia Verga)."), Url = "https://raw.githubusercontent.com/mattiaverga/OpenNGC/master/database_files/NGC.csv", FileName = "NGC.csv", Folder = AppPaths.Catalogs, ApproxBytes = 4200000, Recommended = true, License = "CC BY-SA 4.0" },
            new Dataset { Id = "openngc-add", Name = Lang.N("OpenNGC addendum"), Description = Lang.N("Additional objects (Messier not in NGC/IC, Caldwell, Barnard...)."), Url = "https://raw.githubusercontent.com/mattiaverga/OpenNGC/master/database_files/addendum.csv", FileName = "addendum.csv", Folder = AppPaths.Catalogs, ApproxBytes = 40000, Recommended = true, License = "CC BY-SA 4.0" },
            // kept compressed: 19 MB instead of 336 MB, and read straight from the .gz
            new Dataset { Id = "pgc", Name = Lang.N("PGC galaxies (HyperLEDA)"), Description = Lang.N("Principal Galaxies Catalogue: about 983,000 galaxies with positions, sizes, types and other names (UGC, MCG, ESO…); magnitudes come with the PGC galaxy magnitudes. Shown in the sky chart when zoomed in, found by search and labelled on plate-solved images."), Url = "https://cdsarc.cds.unistra.fr/ftp/VII/237/pgc.dat.gz", FileName = "pgc.dat.gz", Folder = AppPaths.Catalogs, ApproxBytes = 19338174,
                Mirrors = new[] { "https://vizier.cfa.harvard.edu/ftp/cats/VII/237/pgc.dat.gz" }, License = "CDS / HyperLEDA (Paturel et al. 2003), free for scientific use" },
            // HyperLEDA answers the query for every galaxy it knows (64 MB in about 20 s); only the PGC's are kept, compressed
            new Dataset { Id = "pgc-mag", Name = Lang.N("PGC galaxy magnitudes (HyperLEDA)"), Description = Lang.N("Total B magnitudes for about 917,000 of the PGC galaxies (93 %), from HyperLEDA. Used with the PGC galaxies: shown for each galaxy, and the sky chart and plate-solve labels pick galaxies by brightness. Kept as about 7 MB."), Url = "http://atlas.obs-hp.fr/hyperleda/fG.cgi?n=meandata&c=o&of=1,leda,simbad&nra=l&nakd=1&d=pgc%2C%20bt&sql=bt%20is%20not%20null&ob=&a=csv%5B%2C%5D", FileName = "pgc_bt.csv.gz", Folder = AppPaths.Catalogs, ApproxBytes = 64653552,
                Prepare = Catalogs.PgcCatalog.CompactMagnitudes, License = "HyperLEDA (Makarov et al. 2014), free for scientific use" },
            new Dataset { Id = "lines", Name = Lang.N("Constellation stick figures"), Description = Lang.N("Western constellation lines from Stellarium (HIP based)."), Url = "https://raw.githubusercontent.com/Stellarium/stellarium/v0.22.2/skycultures/western/constellationship.fab", FileName = "constellationship.fab", Folder = AppPaths.Catalogs, ApproxBytes = 12000, Recommended = true, License = "GPL (Stellarium)" },
            new Dataset { Id = "bounds", Name = Lang.N("IAU constellation boundaries"), Description = Lang.N("J2000 boundary segments (Davenhall & Leggett / Stellarium)."), Url = "https://raw.githubusercontent.com/Stellarium/stellarium/v0.22.2/data/constellation_boundaries.dat", FileName = "constellation_boundaries.dat", Folder = AppPaths.Catalogs, ApproxBytes = 600000, Recommended = true, License = "GPL (Stellarium)" },
            new Dataset { Id = "comets", Name = Lang.N("MPC comet elements"), Description = Lang.N("Orbital elements of all comets (Minor Planet Center), updated daily."), Url = "https://minorplanetcenter.net/iau/MPCORB/CometEls.txt", FileName = "CometEls.txt", Folder = AppPaths.Catalogs, ApproxBytes = 200000, Recommended = true, License = "MPC" },
            new Dataset { Id = "nea", Name = Lang.N("MPC near-Earth asteroids"), Description = Lang.N("Orbits of all known NEAs."), Url = "https://minorplanetcenter.net/iau/MPCORB/NEA.txt", FileName = "NEA.txt", Folder = AppPaths.Catalogs, ApproxBytes = 7000000, Recommended = false, License = "MPC" },
            new Dataset { Id = "mpcorb", Name = Lang.N("MPCORB (all minor planets)"), Description = Lang.N("Complete minor planet orbit file (1.4M objects; asteroids brighter than H=15.5 are loaded)."), Url = "https://minorplanetcenter.net/iau/MPCORB/MPCORB.DAT.gz", FileName = "MPCORB.DAT", Folder = AppPaths.Catalogs, ApproxBytes = 70000000, Gzip = true, Recommended = false, License = "MPC" },
            new Dataset { Id = "tle-visual", Name = Lang.N("Bright satellites (CelesTrak)"), Description = Lang.N("Two-line elements of ~150 visually bright satellites incl. ISS."), Url = "https://celestrak.org/NORAD/elements/gp.php?GROUP=visual&FORMAT=tle", FileName = "visual.tle", Folder = AppPaths.Catalogs, ApproxBytes = 30000, Recommended = false, License = "CelesTrak" },
            new Dataset { Id = "tle-stations", Name = Lang.N("Space stations (CelesTrak)"), Description = Lang.N("ISS, Tiangong and other crewed stations."), Url = "https://celestrak.org/NORAD/elements/gp.php?GROUP=stations&FORMAT=tle", FileName = "stations.tle", Folder = AppPaths.Catalogs, ApproxBytes = 10000, Recommended = false, License = "CelesTrak" },
        };

        public static Dataset Get(string id) => Datasets.Find(d => d.Id == id);

        /// <summary>
        /// Download (and gunzip if needed) a dataset. progress receives (bytesDone, bytesTotal). The installed copy, if
        /// any, is replaced only once the new one is complete, so a failed or stopped update keeps it.
        /// </summary>
        public static void Install(Dataset d, Action<long, long> progress = null, CancellationToken ct = default(CancellationToken))
        {
            Directory.CreateDirectory(d.Folder);
            // left behind by earlier versions when the installed copy could not be replaced (now never written)
            try { if (File.Exists(d.LocalPath + ".part")) File.Delete(d.LocalPath + ".part"); } catch (IOException) { }
            string target = d.LocalPath + ".download", fresh = d.Prepare != null || d.Gzip ? d.LocalPath + ".new" : target;
            // the dataset's own address first, then its mirrors when that server cannot be reached or fails; a server that
            // is only in trouble for a moment (an error of its own, a timeout) is asked once more first
            var urls = new[] { d.Url }.Concat(d.Mirrors ?? new string[0]).ToList();
            for (int k = 0, attempt = 1; ; )
            {
                try { Net.Download(urls[k], target, progress, ct); break; }
                catch (WebException ex) when (!StopRequest.Or(ct).IsCancellationRequested)
                {
                    bool again = attempt == 1 && Transient(ex);
                    if (!again && k == urls.Count - 1) throw;
                    Log.Warn("Data", d.Name + ": " + new Uri(urls[k]).Host + " failed (" + ex.Message + "), " + (again ? "trying again" : "trying " + new Uri(urls[k + 1]).Host));
                    if (again)
                    {
                        attempt++;
                        if (StopRequest.Or(ct).WaitHandle.WaitOne(RetryDelay)) StopRequest.Or(ct).ThrowIfCancellationRequested();
                    }
                    else { k++; attempt = 1; }
                }
            }
            if (fresh != target)
            {
                try
                {
                    if (d.Prepare != null) d.Prepare(target, fresh);
                    else Net.Gunzip(target, fresh);
                }
                catch { try { File.Delete(fresh); } catch { } throw; }
                finally { File.Delete(target); }
            }
            Replace(fresh, d.LocalPath);
            Log.Info("Data", "Installed " + d.Name);
        }

        /// <summary>How long to wait before asking a server again after a temporary failure.</summary>
        public static TimeSpan RetryDelay = TimeSpan.FromSeconds(3);

        /// <summary>A failure that may be gone a moment later: an error of the server's own (5xx) or a timeout.</summary>
        private static bool Transient(WebException ex)
        {
            var http = ex.Response as HttpWebResponse;
            if (ex.Status == WebExceptionStatus.ProtocolError) return http != null && (int)http.StatusCode >= 500;
            return ex.Status == WebExceptionStatus.Timeout || ex.Status == WebExceptionStatus.ConnectionClosed || ex.Status == WebExceptionStatus.ReceiveFailure
                || ex.Status == WebExceptionStatus.KeepAliveFailure;
        }

        /// <summary>
        /// Installs several datasets in turn. One that fails (its server unreachable or in trouble) does not stop the
        /// others; the failures are returned with their reasons. Stopping the job stops it all.
        /// </summary>
        /// <param name="progress">(index of the dataset, bytes done, bytes in all)</param>
        public static List<KeyValuePair<Dataset, Exception>> InstallAll(IList<Dataset> sets, Action<int, long, long> progress = null, CancellationToken ct = default(CancellationToken))
        {
            var failed = new List<KeyValuePair<Dataset, Exception>>();
            for (int i = 0; i < sets.Count; i++)
            {
                int index = i;
                var d = sets[i];
                try { Install(d, progress == null ? (Action<long, long>)null : (done, total) => progress(index, done, total), ct); }
                catch (Exception ex) when (!(ex is OperationCanceledException) && !StopRequest.Or(ct).IsCancellationRequested)
                {
                    failed.Add(new KeyValuePair<Dataset, Exception>(d, ex));
                }
            }
            return failed;
        }

        /// <summary>
        /// Puts a new copy of a file in place of the installed one. A file Sidralune holds open (the loaded JPL
        /// ephemeris) is closed first and opened again afterwards, the new copy or, if it could not be put in place,
        /// the old one.
        /// </summary>
        public static void Replace(string fresh, string path)
        {
            bool reopen = Ephemeris.EphemerisManager.Release(path);
            string old = path + ".old";
            bool aside = false;
            try
            {
                // the old copy is moved aside, not deleted, until the new one is in place
                if (File.Exists(old)) File.Delete(old);
                if (File.Exists(path)) { File.Move(path, old); aside = true; }
                File.Move(fresh, path);
                if (aside) try { File.Delete(old); } catch { }
            }
            catch
            {
                if (aside && !File.Exists(path)) try { File.Move(old, path); } catch { }
                try { File.Delete(fresh); } catch { }
                throw;
            }
            finally
            {
                if (reopen && File.Exists(path))
                    try { Ephemeris.EphemerisManager.LoadSpk(path); }
                    catch (Exception ex) { Log.Warn("Data", Path.GetFileName(path) + ": " + ex.Message); }
            }
        }

        public static void Remove(Dataset d)
        {
            Ephemeris.EphemerisManager.Release(d.LocalPath); // the loaded ephemeris holds its file open
            if (File.Exists(d.LocalPath)) File.Delete(d.LocalPath);
        }
    }
}
