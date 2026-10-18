using System;
using System.Collections.Generic;
using System.IO;
using Sidralune.Core.Catalogs;
using Sidralune.Core.Mathematics;
using Sidralune.Core.Util;
using static Sidralune.Tests.Program;

namespace Sidralune.Tests
{
    public static class CatalogTests
    {
        public static void TestInsideSphericalAntipode()
        {
            // 10° square around RA 0h, Dec 0°: the point itself is inside, its antipode must not be
            var sq = new List<double[]>();
            foreach (var c in new[] { new[] { -5.0, -5.0 }, new[] { 5.0, -5.0 }, new[] { 5.0, 5.0 }, new[] { -5.0, 5.0 } })
                sq.Add(new[] { c[0] * AstroMath.Deg2Rad, c[1] * AstroMath.Deg2Rad });
            CheckTrue("Centre inside", CatalogManager.InsideSpherical(0, 0, sq));
            CheckTrue("Antipode outside", !CatalogManager.InsideSpherical(Math.PI, 0, sq));
            CheckTrue("Far point outside", !CatalogManager.InsideSpherical(20 * AstroMath.Deg2Rad, 0, sq));
        }

        public static void TestConstellationBoundaries()
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
