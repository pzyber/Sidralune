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
            new Dataset { Id = "de440s", Name = "JPL DE440s planetary ephemeris", Description = "High precision Sun, Moon and planets 1849–2150 (milliarcsecond).", Url = "https://ssd.jpl.nasa.gov/ftp/eph/planets/bsp/de440s.bsp", FileName = "de440s.bsp", Folder = AppPaths.Ephemerides, ApproxBytes = 32726016, Recommended = true, License = "JPL/NASA public" },
            new Dataset { Id = "hyg", Name = "HYG star database v4.1", Description = "~120,000 stars (Hipparcos, Yale BSC, Gliese) with names, Bayer/Flamsteed, colours, proper motions.", Url = "https://raw.githubusercontent.com/astronexus/HYG-Database/main/hyg/CURRENT/hygdata_v41.csv", FileName = "hygdata_v41.csv", Folder = AppPaths.Catalogs, ApproxBytes = 33932548, Recommended = true, License = "CC BY-SA 4.0" },
            new Dataset { Id = "openngc", Name = "OpenNGC catalogue", Description = "NGC/IC objects with sizes, magnitudes, surface brightness and common names (Mattia Verga).", Url = "https://raw.githubusercontent.com/mattiaverga/OpenNGC/master/database_files/NGC.csv", FileName = "NGC.csv", Folder = AppPaths.Catalogs, ApproxBytes = 4200000, Recommended = true, License = "CC BY-SA 4.0" },
            new Dataset { Id = "openngc-add", Name = "OpenNGC addendum", Description = "Additional objects (Messier not in NGC/IC, Caldwell, Barnard...).", Url = "https://raw.githubusercontent.com/mattiaverga/OpenNGC/master/database_files/addendum.csv", FileName = "addendum.csv", Folder = AppPaths.Catalogs, ApproxBytes = 40000, Recommended = true, License = "CC BY-SA 4.0" },
            // kept compressed: 19 MB instead of 336 MB, and read straight from the .gz
            new Dataset { Id = "pgc", Name = "PGC galaxies (HyperLEDA)", Description = "Principal Galaxies Catalogue: about 983,000 galaxies with positions, sizes, types and other names (UGC, MCG, ESO…), but no magnitudes. Shown in the sky chart when zoomed in, found by search and labelled on plate-solved images.", Url = "https://cdsarc.cds.unistra.fr/ftp/VII/237/pgc.dat.gz", FileName = "pgc.dat.gz", Folder = AppPaths.Catalogs, ApproxBytes = 19338174,
                Mirrors = new[] { "https://vizier.cfa.harvard.edu/ftp/cats/VII/237/pgc.dat.gz" }, License = "CDS / HyperLEDA (Paturel et al. 2003), free for scientific use" },
            new Dataset { Id = "lines", Name = "Constellation stick figures", Description = "Western constellation lines from Stellarium (HIP based).", Url = "https://raw.githubusercontent.com/Stellarium/stellarium/v0.22.2/skycultures/western/constellationship.fab", FileName = "constellationship.fab", Folder = AppPaths.Catalogs, ApproxBytes = 12000, Recommended = true, License = "GPL (Stellarium)" },
            new Dataset { Id = "bounds", Name = "IAU constellation boundaries", Description = "J2000 boundary segments (Davenhall & Leggett / Stellarium).", Url = "https://raw.githubusercontent.com/Stellarium/stellarium/v0.22.2/data/constellation_boundaries.dat", FileName = "constellation_boundaries.dat", Folder = AppPaths.Catalogs, ApproxBytes = 600000, Recommended = true, License = "GPL (Stellarium)" },
            new Dataset { Id = "comets", Name = "MPC comet elements", Description = "Orbital elements of all comets (Minor Planet Center), updated daily.", Url = "https://minorplanetcenter.net/iau/MPCORB/CometEls.txt", FileName = "CometEls.txt", Folder = AppPaths.Catalogs, ApproxBytes = 200000, Recommended = true, License = "MPC" },
            new Dataset { Id = "nea", Name = "MPC near-Earth asteroids", Description = "Orbits of all known NEAs.", Url = "https://minorplanetcenter.net/iau/MPCORB/NEA.txt", FileName = "NEA.txt", Folder = AppPaths.Catalogs, ApproxBytes = 7000000, Recommended = false, License = "MPC" },
            new Dataset { Id = "mpcorb", Name = "MPCORB (all minor planets)", Description = "Complete minor planet orbit file (1.4M objects; asteroids brighter than H=15.5 are loaded).", Url = "https://minorplanetcenter.net/iau/MPCORB/MPCORB.DAT.gz", FileName = "MPCORB.DAT", Folder = AppPaths.Catalogs, ApproxBytes = 70000000, Gzip = true, Recommended = false, License = "MPC" },
            new Dataset { Id = "tle-visual", Name = "Bright satellites (CelesTrak)", Description = "Two-line elements of ~150 visually bright satellites incl. ISS.", Url = "https://celestrak.org/NORAD/elements/gp.php?GROUP=visual&FORMAT=tle", FileName = "visual.tle", Folder = AppPaths.Catalogs, ApproxBytes = 30000, Recommended = false, License = "CelesTrak" },
            new Dataset { Id = "tle-stations", Name = "Space stations (CelesTrak)", Description = "ISS, Tiangong and other crewed stations.", Url = "https://celestrak.org/NORAD/elements/gp.php?GROUP=stations&FORMAT=tle", FileName = "stations.tle", Folder = AppPaths.Catalogs, ApproxBytes = 10000, Recommended = false, License = "CelesTrak" },
        };

        public static Dataset Get(string id) => Datasets.Find(d => d.Id == id);

        /// <summary>Download (and gunzip if needed) a dataset. progress receives (bytesDone, bytesTotal).</summary>
        public static void Install(Dataset d, Action<long, long> progress = null, CancellationToken ct = default(CancellationToken))
        {
            Directory.CreateDirectory(d.Folder);
            string target = d.Gzip ? d.LocalPath + ".gz" : d.LocalPath;
            // the dataset's own address first, then its mirrors when that server cannot be reached or fails
            var urls = new[] { d.Url }.Concat(d.Mirrors ?? new string[0]).ToList();
            for (int k = 0; ; k++)
            {
                try { Net.Download(urls[k], target, progress, ct); break; }
                catch (WebException ex) when (k < urls.Count - 1 && !StopRequest.Or(ct).IsCancellationRequested)
                {
                    Log.Warn("Data", d.Name + ": " + new Uri(urls[k]).Host + " failed (" + ex.Message + "), trying " + new Uri(urls[k + 1]).Host);
                }
            }
            if (d.Gzip)
            {
                Net.Gunzip(target, d.LocalPath);
                File.Delete(target);
            }
            Log.Info("Data", "Installed " + d.Name);
        }

        public static void Remove(Dataset d)
        {
            if (File.Exists(d.LocalPath)) File.Delete(d.LocalPath);
        }
    }
}
