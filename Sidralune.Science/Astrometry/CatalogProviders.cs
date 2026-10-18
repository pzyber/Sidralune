using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Sidralune.Core.Catalogs;
using Sidralune.Core.Mathematics;
using Sidralune.Core.Util;

namespace Sidralune.Science.Astrometry
{
    /// <summary>A reference star for astrometry/photometry (J2000/ICRS, degrees).</summary>
    public class RefStar
    {
        public double Ra, Dec, Mag;
        public double BpMag = double.NaN, RpMag = double.NaN, BV = double.NaN;
        public double PmRa, PmDec; // mas/yr
        public string Id;
        public double Epoch = 2000.0;
    }

    public interface IStarCatalogProvider
    {
        string Name { get; }
        bool IsOnline { get; }
        /// <summary>Stars within radiusDeg of (raDeg, decDeg) brighter than magLimit, sorted by brightness.</summary>
        List<RefStar> Cone(double raDeg, double decDeg, double radiusDeg, double magLimit, int maxStars);
    }

    /// <summary>Stars from the locally loaded catalog (HYG or built-in).</summary>
    public class LocalCatalogProvider : IStarCatalogProvider
    {
        public string Name => "Local (" + CatalogManager.Instance.StarSource + ")";
        public bool IsOnline => false;

        public List<RefStar> Cone(double raDeg, double decDeg, double radiusDeg, double magLimit, int maxStars)
        {
            var res = new List<Star>();
            CatalogManager.Instance.Stars.Cone(raDeg * AstroMath.Deg2Rad, decDeg * AstroMath.Deg2Rad, radiusDeg * AstroMath.Deg2Rad, magLimit, res);
            return res.OrderBy(s => s.Mag).Take(maxStars).Select(s => new RefStar
            {
                Ra = s.Ra * AstroMath.Rad2Deg, Dec = s.Dec * AstroMath.Rad2Deg, Mag = s.Mag, BV = s.BV, PmRa = s.PmRa, PmDec = s.PmDec,
                Id = s.DisplayName
            }).ToList();
        }
    }

    /// <summary>
    /// VizieR cone search (ASU-TSV) for Gaia DR3 (I/355/gaiadr3), UCAC4 (I/322A), Tycho-2 (I/259) or APASS DR9 (II/336),
    /// with a disk cache so repeated solves of the same field work offline.
    /// </summary>
    public class VizierProvider : IStarCatalogProvider
    {
        public enum Catalog { GaiaDR3, Ucac4, Tycho2, ApassDr9 }
        public Catalog Source = Catalog.GaiaDR3;
        public string Mirror = "https://vizier.cds.unistra.fr";

        public string Name => "VizieR " + Source;
        public bool IsOnline => true;

        private string Spec(out string raCol, out string decCol, out string magCol, out string extra)
        {
            switch (Source)
            {
                case Catalog.Ucac4: raCol = "RAJ2000"; decCol = "DEJ2000"; magCol = "f.mag"; extra = "Bmag,Vmag,pmRA,pmDE"; return "I/322A/out";
                case Catalog.Tycho2: raCol = "RAmdeg"; decCol = "DEmdeg"; magCol = "VTmag"; extra = "BTmag,pmRA,pmDE"; return "I/259/tyc2";
                case Catalog.ApassDr9: raCol = "RAJ2000"; decCol = "DEJ2000"; magCol = "Vmag"; extra = "Bmag,g'mag,r'mag"; return "II/336/apass9";
                default: raCol = "RA_ICRS"; decCol = "DE_ICRS"; magCol = "Gmag"; extra = "BPmag,RPmag,pmRA,pmDE,Source"; return "I/355/gaiadr3";
            }
        }

        public List<RefStar> Cone(double raDeg, double decDeg, double radiusDeg, double magLimit, int maxStars)
        {
            string ra, de, mag, extra;
            string cat = Spec(out ra, out de, out mag, out extra);
            string key = string.Format(CultureInfo.InvariantCulture, "{0}_{1:0.000}_{2:0.000}_{3:0.000}_{4:0.0}_{5}", Source, raDeg, decDeg, radiusDeg, magLimit, maxStars);
            string cache = Path.Combine(AppPaths.Cache, "vizier_" + key.Replace('/', '_').Replace('-', 'm') + ".tsv");
            string text;
            if (File.Exists(cache)) text = File.ReadAllText(cache);
            else
            {
                string url = string.Format(CultureInfo.InvariantCulture,
                    "{0}/viz-bin/asu-tsv?-source={1}&-c={2:0.000000}%20{3:+0.000000;-0.000000}&-c.rd={4:0.0000}&-out={5},{6},{7},{8}&-sort={7}&-out.max={9}&{7}=%3C{10:0.0}",
                    Mirror, cat, raDeg, decDeg, radiusDeg, ra, de, mag, extra, maxStars, magLimit);
                text = Net.GetString(url, 90000);
                try { File.WriteAllText(cache, text); } catch { }
            }
            return ParseTsv(text, ra, de, mag);
        }

        private List<RefStar> ParseTsv(string text, string raCol, string decCol, string magCol)
        {
            var list = new List<RefStar>();
            string[] header = null;
            bool afterDashes = false;
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.StartsWith("#") || line.Length == 0) continue;
                var f = line.Split('\t');
                if (header == null) { header = f.Select(h => h.Trim()).ToArray(); continue; }
                if (!afterDashes) { if (line.StartsWith("-")) { afterDashes = true; } continue; }
                Func<string, double> col = name =>
                {
                    int i = Array.IndexOf(header, name);
                    return i >= 0 && i < f.Length ? AstroMath.ParseInvariant(f[i]) : double.NaN;
                };
                var s = new RefStar { Ra = col(raCol), Dec = col(decCol), Mag = col(magCol) };
                if (double.IsNaN(s.Ra))
                {
                    // sexagesimal RA/Dec columns (some catalogs)
                    int ri = Array.IndexOf(header, raCol), di = Array.IndexOf(header, decCol);
                    if (ri >= 0 && di >= 0 && ri < f.Length && di < f.Length) { s.Ra = AstroMath.ParseSexagesimal(f[ri]) * 15; s.Dec = AstroMath.ParseSexagesimal(f[di]); }
                }
                if (double.IsNaN(s.Ra) || double.IsNaN(s.Dec) || double.IsNaN(s.Mag)) continue;
                s.BpMag = col("BPmag"); s.RpMag = col("RPmag");
                double b = col("Bmag"), v = col("Vmag");
                if (!double.IsNaN(b) && !double.IsNaN(v)) s.BV = b - v;
                if (!double.IsNaN(s.BpMag) && !double.IsNaN(s.RpMag))
                {
                    // Gaia BP-RP -> B-V (Riello et al. 2021 polynomial inverse, approx.)
                    double c = s.BpMag - s.RpMag;
                    s.BV = 0.0981 + 0.6849 * c - 0.0279 * c * c + 0.0106 * c * c * c;
                }
                double pmr = col("pmRA"), pmd = col("pmDE");
                if (!double.IsNaN(pmr)) s.PmRa = pmr;
                if (!double.IsNaN(pmd)) s.PmDec = pmd;
                if (Source == Catalog.GaiaDR3) s.Epoch = 2016.0;
                int si = Array.IndexOf(header, "Source");
                if (si >= 0 && si < f.Length) s.Id = "Gaia DR3 " + f[si].Trim();
                list.Add(s);
            }
            return list;
        }
    }
}
