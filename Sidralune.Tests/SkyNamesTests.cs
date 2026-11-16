using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sidralune.Core.Catalogs;
using Sidralune.Core.Util;
using static Sidralune.Tests.Checks;

namespace Sidralune.Tests
{
    /// <summary>
    /// Constellation names and object nicknames in the interface language: the lists match the catalogues, the names are
    /// shown translated while the catalogue's names stay as data, and searching finds both.
    /// </summary>
    [TestClass]
    public class SkyNamesTests : TestBase
    {
        // the program's language files, found from this file's place when it was compiled
        private static string Languages([CallerFilePath] string here = "") => Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(here)), "Sidralune", "Languages");

        [TestMethod]
        public void TestSkyNamesMatchCatalogues()
        {
            var cm = new CatalogManager();
            cm.LoadBuiltIn();
            var builtIn = cm.Constellations.Select(c => c.Name).ToList();
            CheckTrue("The 88 constellations, as the built-in data names them", SkyNames.Constellations.Length == 88 && SkyNames.Constellations.Distinct().Count() == 88
                && new HashSet<string>(SkyNames.Constellations).SetEquals(builtIn), string.Join(", ", SkyNames.Constellations.Except(builtIn).Concat(builtIn.Except(SkyNames.Constellations))));
            CheckTrue("Each nickname listed once", SkyNames.Nicknames.Distinct().Count() == SkyNames.Nicknames.Length);
            // a translated nickname must be a name the catalogues have: the built-in data, or OpenNGC when it is installed
            Func<CatalogManager, HashSet<string>> names = c => new HashSet<string>(c.DeepSkyList.SelectMany(d => new[] { d.Name }.Concat(d.AltNames)).Where(n => n != null));
            var inBuiltIn = names(cm);
            var notBuiltIn = SkyNames.Nicknames.Where(n => !inBuiltIn.Contains(n)).ToList();
            var main = DataManager.Get("openngc"); var addendum = DataManager.Get("openngc-add");
            if (main == null || !main.Installed || addendum == null || !addendum.Installed)
            {
                Info("OpenNGC not installed; these nicknames are only in OpenNGC and were not checked: " + string.Join(", ", notBuiltIn));
                return;
            }
            var real = new CatalogManager();
            real.LoadBuiltIn();
            real.LoadOpenNgc(main.LocalPath);
            real.LoadOpenNgc(addendum.LocalPath, true);
            var inOpenNgc = names(real);
            var unknown = notBuiltIn.Where(n => !inOpenNgc.Contains(n)).ToList();
            CheckTrue("Every translated nickname is a name in the built-in data or the installed OpenNGC", unknown.Count == 0, string.Join(", ", unknown));
        }

        [TestMethod]
        public void TestSkyNamesInSwedish()
        {
            string saved = Lang.Folder;
            try
            {
                Lang.Folder = Languages();
                CheckTrue("Swedish is used", Lang.Use("sv") == "sv");
                var cm = new CatalogManager();
                cm.LoadBuiltIn();
                var m51 = (DeepSkyObject)cm.Find("M51");
                var uma = cm.Constellations.First(c => c.Abbr == "UMa");
                CheckTrue("A constellation in Swedish", SkyNames.ConstellationName(uma) == "Stora björnen", SkyNames.ConstellationName(uma));
                CheckTrue("A nickname shown in Swedish, kept in English as data", m51.ShownName == "M51 Virvelgalaxen" && m51.DisplayName == "M51 Whirlpool Galaxy" && m51.Name == "Whirlpool Galaxy", m51.ShownName);
                CheckTrue("In English where texts are kept as data", Lang.InEnglish(() => m51.ShownName) == "M51 Whirlpool Galaxy" && Lang.InEnglish(() => SkyNames.ConstellationName(uma)) == "Ursa Major");
                var vega = cm.Find("Vega");
                CheckTrue("Star names stay as they are", vega != null && vega.ShownName == vega.DisplayName);
                CheckTrue("A name without a translation stays as the catalogue has it", SkyNames.Nickname("Winnecke 4") == "Winnecke 4");
                CheckTrue("Found by the Swedish name, in any case, and by the English one", cm.Find("Virvelgalaxen") == m51 && cm.Find("virvelgalaxen") == m51 && cm.Find("Whirlpool Galaxy") == m51);
                CheckTrue("Searching finds part of the Swedish name", cm.Search("Virvel").Contains(m51) && cm.Search("Andromedagal").Any(o => ((DeepSkyObject)o).Messier == "M31"));
                CheckTrue("A constellation by its Swedish or IAU name or abbreviation", SkyNames.FindConstellation(cm.Constellations, "stora björnen") == uma
                    && SkyNames.FindConstellation(cm.Constellations, "Ursa Major") == uma && SkyNames.FindConstellation(cm.Constellations, "UMa") == uma);
                var bears = SkyNames.SearchConstellations(cm.Constellations, "björn").Select(c => c.Abbr).ToList();
                CheckTrue("Constellations by part of the Swedish name", bears.Contains("UMa") && bears.Contains("UMi") && bears.Contains("Boo"), string.Join(", ", bears));
                // with the setting off: the IAU names and the catalogue's nicknames
                SkyNames.InLanguage = false;
                CheckTrue("Setting off: the IAU name", SkyNames.ConstellationName(uma) == "Ursa Major");
                CheckTrue("Setting off: the catalogue's nickname", m51.ShownName == "M51 Whirlpool Galaxy");
                CheckTrue("Setting off: the Swedish name is not searched", cm.Find("Virvelgalaxen") == null && SkyNames.FindConstellation(cm.Constellations, "Stora björnen") == null);
                SkyNames.InLanguage = true;
                // every language has a name for each constellation and listed nickname
                foreach (var file in Directory.GetFiles(Languages(), "*.po"))
                {
                    var po = PoCatalog.Load(file);
                    var missing = SkyNames.Constellations.Where(n => po.Find(SkyNames.ConstellationContext, n) == null)
                        .Concat(SkyNames.Nicknames.Where(n => po.Find(SkyNames.ObjectContext, n) == null)).ToList();
                    CheckTrue(Path.GetFileName(file) + ": every constellation and nickname translated", missing.Count == 0, string.Join(", ", missing.Take(10)));
                }
            }
            finally
            {
                SkyNames.InLanguage = true;
                Lang.Folder = saved;
                Lang.Use("en");
            }
        }
    }
}
