using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Sidralune.Core.Catalogs;
using Sidralune.Core.Mathematics;
using Sidralune.Core.Util;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Sidralune.Tests.Checks;

namespace Sidralune.Tests
{
    [TestClass]
    public class CatalogTests : TestBase
    {
        [TestMethod]
        public void TestInsideSphericalAntipode()
        {
            // 10° square around RA 0h, Dec 0°: the point itself is inside, its antipode must not be
            var sq = new List<double[]>();
            foreach (var c in new[] { new[] { -5.0, -5.0 }, new[] { 5.0, -5.0 }, new[] { 5.0, 5.0 }, new[] { -5.0, 5.0 } })
                sq.Add(new[] { c[0] * AstroMath.Deg2Rad, c[1] * AstroMath.Deg2Rad });
            CheckTrue("Centre inside", CatalogManager.InsideSpherical(0, 0, sq));
            CheckTrue("Antipode outside", !CatalogManager.InsideSpherical(Math.PI, 0, sq));
            CheckTrue("Far point outside", !CatalogManager.InsideSpherical(20 * AstroMath.Deg2Rad, 0, sq));
        }

        [TestMethod]
        public void TestPgc()
        {
            // four lines of the Principal Galaxies Catalogue (VizieR VII/237 pgc.dat), read from a .gz as downloaded
            string dir = Path.Combine(Path.GetTempPath(), "sidralune_pgc_test");
            Directory.CreateDirectory(dir);
            string gz = Path.Combine(dir, "pgc.dat.gz");
            var lines = new[]
            {
                "PGC0002557 J004244.3+411610 G  Sb    3.30+/-0.06  0.45+/-0.07   35.+/-  6.  5 NGC224                UGC454                MCG7-02-016           CGCG535-17            GIN801",
                "PGC0000008 J000005.1-772012 GM       0.50+/-0.07  0.25+/-0.05   85.+/- 10.  2 (ESO12-11)            ESO-LV120112",
                "PGC0000190 J000250.0+312906 G  S?    0.83+/-0.05  0.04+/-0.02  999.+/-999.  3 MCG5-01-027           CGCG499-39            CGCG498-67",
                "PGC0130029 J000008.8-460450 M        0.70+/-0.10  0.21+/-0.04  116.+/- 17.  0"
            };
            using (var f = File.Create(gz))
            using (var z = new GZipStream(f, CompressionMode.Compress))
            using (var w = new StreamWriter(z)) foreach (var l in lines) w.WriteLine(l);
            var pgc = PgcCatalog.Load(gz);
            CheckTrue("Every line is read", pgc.Count == 4, pgc.Count.ToString());
            int m31 = pgc.IndexOf("PGC 2557");
            CheckTrue("Found by number, written in any of the usual ways", m31 >= 0 && pgc.IndexOf("PGC2557") == m31 && pgc.IndexOf("PGC 002557") == m31 && pgc.IndexOf("LEDA 2557") == m31);
            CheckTrue("Found by its other names, with or without a space", pgc.IndexOf("UGC 454") == m31 && pgc.IndexOf("UGC454") == m31 && pgc.IndexOf("mcg 7-02-016") == m31 && pgc.IndexOf("CGCG 535-17") == m31);
            CheckTrue("Unknown names are not found", pgc.IndexOf("UGC 455") < 0 && pgc.IndexOf("PGC 3") < 0);
            var d = pgc.Object(m31);
            CheckTrue("Its position", Math.Abs(d.RaHours - (0 + 42 / 60.0 + 44.3 / 3600)) < 1e-5 && Math.Abs(d.DecDeg - (41 + 16 / 60.0 + 10 / 3600.0)) < 1e-4, d.RaHours + " / " + d.DecDeg);
            Check("Its size from log D25 (arcmin)", d.MajorAxis, 0.1 * Math.Pow(10, 3.30), 1e-3);
            Check("Its minor axis from log R25 (arcmin)", d.MinorAxis, 0.1 * Math.Pow(10, 3.30) / Math.Pow(10, 0.45), 1e-3);
            CheckTrue("Its position angle, type and morphology", d.PositionAngleDeg == 35 && d.Type == ObjectType.Galaxy && d.Morphology == "Sb");
            CheckTrue("Named PGC n, with its other names", d.Id == "PGC 2557" && d.AltNames.Contains("NGC 224") && d.AltNames.Contains("MCG 7-02-016") && double.IsNaN(d.Mag));
            var group = pgc.Object(pgc.IndexOf("PGC 130029"));
            CheckTrue("A multiple system is a galaxy group, a galaxy in one a galaxy", group.Type == ObjectType.GalaxyGroup && pgc.Object(pgc.IndexOf("PGC 8")).Type == ObjectType.Galaxy);
            CheckTrue("Names of multiple systems lose their brackets", pgc.IndexOf("ESO 12-11") == pgc.IndexOf("PGC 8"));
            CheckTrue("An unknown position angle stays unknown", double.IsNaN(pgc.Object(pgc.IndexOf("PGC 190")).PositionAngleDeg));
            var near = pgc.Cone(d.Ra, d.Dec, 1 * AstroMath.Deg2Rad, 99, 0, 10);
            CheckTrue("A search around a position finds the galaxies there", near.Count == 1 && near[0] == m31);
            CheckTrue("…and leaves out smaller ones than asked for", pgc.Cone(d.Ra, d.Dec, 1 * AstroMath.Deg2Rad, 99, 300, 10).Count == 0);
            var wide = pgc.Cone(0, 0, 180 * AstroMath.Deg2Rad, 99, 0, 10);
            CheckTrue("…across right ascension 0h and the whole sky, the largest first", wide.Count == 4 && wide[0] == m31);
            CheckTrue("Without magnitudes loaded, none is known", double.IsNaN(d.Mag) && pgc.WithMagnitude == 0);
            var prefix = pgc.Search("CGCG 49", 10);
            CheckTrue("Names are searched by their beginning", prefix.Count == 1 && pgc.Number(prefix[0]) == 190);

            // HyperLEDA's B magnitudes: its answer (comments, header, galaxies beyond the PGC's numbers) is compacted, then loaded
            string raw = Path.Combine(dir, "leda.csv"), mags = Path.Combine(dir, "pgc_bt.csv.gz");
            File.WriteAllLines(raw, new[] { "# Query Description", "#---,---", "pgc, bt", "#---,---", "2557,4.295", "190,15.8", "8,", "130029,17.4", "4000000,18.0", "# Date: 2026-10-30" });
            PgcCatalog.CompactMagnitudes(raw, mags);
            string kept;
            using (var z = new StreamReader(new GZipStream(File.OpenRead(mags), CompressionMode.Decompress))) kept = z.ReadToEnd();
            CheckTrue("Only galaxies of the PGC's number range with a magnitude are kept", kept == "2557,4.295\n190,15.8\n130029,17.4\n", kept);
            CheckTrue("Their magnitudes are loaded", pgc.LoadMagnitudes(mags) == 3 && pgc.WithMagnitude == 3);
            Check("…and given to the galaxy", pgc.Object(m31).Mag, 4.295, 1e-6);
            CheckTrue("A galaxy without one stays without", double.IsNaN(pgc.Object(pgc.IndexOf("PGC 8")).Mag));
            var bright = pgc.Cone(0, 0, 180 * AstroMath.Deg2Rad, 16, 99, 10);
            CheckTrue("Brighter galaxies than the limit are picked, brightest first", bright.Count == 2 && bright[0] == m31 && pgc.Number(bright[1]) == 190);
            var mixed = pgc.Cone(0, 0, 180 * AstroMath.Deg2Rad, 16, 0.3, 10);
            CheckTrue("…and the large ones of the rest, after them", mixed.Count == 4 && pgc.Number(mixed[2]) == 130029 && pgc.Number(mixed[3]) == 8,
                string.Join(", ", mixed.Select(i => pgc.Number(i))));

            // with OpenNGC, a galaxy in both catalogues is found and shown once, as its NGC object
            string csv = Path.Combine(dir, "NGC.csv");
            File.WriteAllLines(csv, new[]
            {
                "Name;Type;RA;Dec;Const;MajAx;MinAx;PosAng;B-Mag;V-Mag;SurfBr;Hubble;Redshift;M;Identifiers;Common names",
                "NGC0224;G;00:42:44.35;+41:16:08.6;And;177.83;69.66;35;4.29;3.44;13.35;Sb;;31;PGC 002557,UGC 00454,MCG +07-02-016;Andromeda Galaxy"
            });
            var cm = new CatalogManager();
            cm.LoadBuiltIn();
            cm.LoadOpenNgc(csv);
            var ngc = cm.Find("NGC 224");
            CheckTrue("OpenNGC's zero-padded numbers are found without the zeros", cm.Find("PGC 2557") == ngc && cm.Find("UGC 454") == ngc && cm.Find("UGC 00454") == ngc);
            cm.LoadPgc(gz, mags);
            CheckTrue("A PGC galaxy that is also an NGC object stays that object", cm.Find("PGC 2557") == ngc && cm.Find("CGCG 535-17") == ngc && cm.Find("GIN 801") == ngc);
            var only = cm.Find("PGC 190") as DeepSkyObject;
            CheckTrue("Other PGC galaxies are found, with their constellation", only != null && only.Id == "PGC 190" && !string.IsNullOrEmpty(only.Constellation) && cm.Find("MCG 5-01-027") == only);
            CheckTrue("Found twice, it is the same object", cm.Find("PGC 190") == only);
            CheckTrue("Search lists PGC galaxies after the other catalogues", cm.Search("CGCG 49").Contains(only) && cm.Search("PGC 2557").Count(o => o == ngc) == 1);
            var shown = new List<DeepSkyObject>();
            cm.PgcCone(ngc.Ra, ngc.Dec, 180 * AstroMath.Deg2Rad, 99, 0, 10, shown);
            CheckTrue("Charts get the PGC galaxies that are not NGC objects", shown.Count == 3 && shown.All(o => o.Id != "PGC 2557"), string.Join(", ", shown.Select(o => o.Id)));
            CheckTrue("The deep-sky source says PGC is loaded, with how many have magnitudes", cm.DeepSkySource.Contains("PGC (4 galaxies, 3 with magnitudes)"), cm.DeepSkySource);
            CheckTrue("A PGC galaxy found by name has its magnitude", Math.Abs(only.Mag - 15.8) < 1e-6);
        }

        [TestMethod]
        public void TestMessierOnce()
        {
            // OpenNGC's main file lacks M40 and M45, its addendum has them: they replace the built-in ones, not join them
            string dir = Path.Combine(Path.GetTempPath(), "sidralune_messier_test");
            Directory.CreateDirectory(dir);
            const string header = "Name;Type;RA;Dec;Const;MajAx;MinAx;PosAng;B-Mag;V-Mag;SurfBr;Hubble;Redshift;M;Identifiers;Common names";
            string ngc = Path.Combine(dir, "NGC.csv"), add = Path.Combine(dir, "addendum.csv");
            File.WriteAllLines(ngc, new[] { header, "NGC0224;G;00:42:44.35;+41:16:08.6;And;177.83;69.66;35;4.29;3.44;13.35;Sb;;31;PGC 002557;Andromeda Galaxy" });
            File.WriteAllLines(add, new[]
            {
                header,
                "M040;**;12:22:12.53;+58:04:59.0;UMa;;;;9.65;9.04;;;;040;WNC 4;Winnecke 4",
                "M102;Dup;15:06:29.56;+55:45:47.9;Dra;;;;;;;;;101;;",
                "Mel022;OCl;03:47:28.6;+24:06:19;Tau;150.0;;;;1.20;;;;045;MWSC 0305;Pleiades"
            });
            foreach (bool withMain in new[] { true, false })
            {
                var cm = new CatalogManager();
                cm.LoadBuiltIn();
                if (withMain) cm.LoadOpenNgc(ngc);
                cm.LoadOpenNgc(add, true);
                string how = withMain ? "with the main file: " : "with the addendum only: ";
                var m45 = cm.DeepSkyList.Where(d => d.Messier == "M45").ToList();
                CheckTrue(how + "one M45, the addendum's", m45.Count == 1 && m45[0].AltNames.Contains("MWSC 0305") && Math.Abs(m45[0].Mag - 1.2) < 1e-6,
                    string.Join(", ", m45.Select(d => d.Id + " " + d.Mag)));
                CheckTrue(how + "the charts have it once", cm.DeepSky.All().Count(d => d.Messier == "M45") == 1);
                CheckTrue(how + "found by its names", cm.Find("M45") == m45[0] && cm.Find("Pleiades") == m45[0] && cm.Find("MWSC 0305") == m45[0]);
                CheckTrue(how + "one M40, the addendum's", cm.DeepSkyList.Count(d => d.Messier == "M40") == 1 && ((DeepSkyObject)cm.Find("M40")).AltNames.Contains("WNC 4"));
                CheckTrue(how + "a duplicate row leaves the built-in M102", cm.DeepSkyList.Count(d => d.Messier == "M102") == 1 && cm.Find("M102") != null);
                var twice = cm.DeepSkyList.Where(d => d.Messier != null).GroupBy(d => d.Messier).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
                CheckTrue(how + "every Messier object once", twice.Count == 0 && cm.DeepSkyList.Count(d => d.Messier != null) == 110, string.Join(", ", twice));
            }
            // and with the installed OpenNGC files
            var main = DataManager.Get("openngc"); var addendum = DataManager.Get("openngc-add");
            if (main == null || !main.Installed || addendum == null || !addendum.Installed) { Info("OpenNGC not installed, the installed files skipped"); return; }
            var real = new CatalogManager();
            real.LoadBuiltIn();
            real.LoadOpenNgc(main.LocalPath);
            real.LoadOpenNgc(addendum.LocalPath, true);
            var dup = real.DeepSkyList.Where(d => d.Messier != null).GroupBy(d => d.Messier).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            CheckTrue("Installed OpenNGC: every Messier object once", dup.Count == 0 && real.DeepSkyList.Select(d => d.Messier).Where(m => m != null).Distinct().Count() == 110, string.Join(", ", dup));
        }

        [TestMethod]
        public void TestConstellationBoundaries()
        {
            var ds = DataManager.Get("bounds");
            if (ds == null || !ds.Installed) { Info("constellation_boundaries.dat not installed, skipped"); return; }
            var cm = CatalogManager.Instance;
            if (cm.Constellations.Count == 0) cm.LoadAll(AppPaths.Data);
            cm.LoadBoundaries(ds.LocalPath);
            Func<double, double, string> at = (raH, decD) => { var c = cm.ConstellationAt(raH * AstroMath.Hour2Rad, decD * AstroMath.Deg2Rad); return c != null ? c.Abbr : "?"; };
            CheckTrue("Acubens (α Cnc) in Cnc", at(8.9748, 11.8577) == "Cnc", at(8.9748, 11.8577));
            CheckTrue("Subra (ο Leo) in Leo", at(9.6858, 9.8923) == "Leo", at(9.6858, 9.8923));
            CheckTrue("Vega in Lyr", at(18.6156, 38.78) == "Lyr", at(18.6156, 38.78));
            CheckTrue("Sirius in CMa", at(6.7525, -16.716) == "CMa", at(6.7525, -16.716));
            CheckTrue("Polaris in UMi", at(2.53, 89.26) == "UMi", at(2.53, 89.26));
            CheckTrue("South pole in Oct", at(0, -89.9) == "Oct", at(0, -89.9));
            CheckTrue("21.6h -14 in Cap", at(21.6, -14) == "Cap", at(21.6, -14));
            CheckTrue("Betelgeuse in Ori", at(5.9195, 7.407) == "Ori", at(5.9195, 7.407));
        }
    }
}
