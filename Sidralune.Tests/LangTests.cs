using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sidralune.Core.Util;
using static Sidralune.Tests.Checks;

namespace Sidralune.Tests
{
    /// <summary>Interface languages: reading .po files, plural rules, looking texts up, the pseudo-language and finding texts in source.</summary>
    [TestClass]
    public class LangTests : TestBase
    {
        private const string Swedish =
            "# Swedish\n" +
            "msgid \"\"\n" +
            "msgstr \"\"\n" +
            "\"Language: sv\\n\"\n" +
            "\"Content-Type: text/plain; charset=UTF-8\\n\"\n" +
            "\"Plural-Forms: nplurals=2; plural=(n != 1);\\n\"\n" +
            "\n" +
            "#: Sidralune/MainForm.cs\n" +
            "msgid \"Tonight\"\n" +
            "msgstr \"I kväll\"\n" +
            "\n" +
            "msgid \"Go to {0}\"\n" +
            "msgstr \"Gå till {0}\"\n" +
            "\n" +
            "msgctxt \"file\"\n" +
            "msgid \"Open\"\n" +
            "msgstr \"Öppna\"\n" +
            "\n" +
            "msgctxt \"cluster\"\n" +
            "msgid \"Open\"\n" +
            "msgstr \"Öppen\"\n" +
            "\n" +
            "msgid \"{0} star\"\n" +
            "msgid_plural \"{0} stars\"\n" +
            "msgstr[0] \"{0} stjärna\"\n" +
            "msgstr[1] \"{0} stjärnor\"\n" +
            "\n" +
            "msgid \"\"\n" +
            "\"A long text \"\n" +
            "\"on two lines\\nwith a \\\"quote\\\"\"\n" +
            "msgstr \"\"\n" +
            "\"En lång text \"\n" +
            "\"på två rader\\nmed ett \\\"citat\\\"\"\n" +
            "\n" +
            "#, fuzzy\n" +
            "msgid \"Sky\"\n" +
            "msgstr \"Himmel?\"\n" +
            "\n" +
            "msgid \"Planner\"\n" +
            "msgstr \"\"\n";

        [TestMethod]
        public void TestPoFile()
        {
            var cat = PoCatalog.Parse(Swedish);
            CheckTrue("The header read", cat.Header["Language"] == "sv" && cat.Header["Plural-Forms"].Contains("nplurals=2"));
            CheckTrue("A plain translation", cat.Find(null, "Tonight") == "I kväll");
            CheckTrue("Where a text is used", cat.Entries.First(e => e.Id == "Tonight").References.SequenceEqual(new[] { "Sidralune/MainForm.cs" }));
            CheckTrue("The same English word in two contexts", cat.Find("file", "Open") == "Öppna" && cat.Find("cluster", "Open") == "Öppen" && cat.Find(null, "Open") == null);
            CheckTrue("Plural forms", cat.Find(null, "{0} star", 1) == "{0} stjärna" && cat.Find(null, "{0} star", 5) == "{0} stjärnor" && cat.Find(null, "{0} star", 0) == "{0} stjärnor");
            CheckTrue("Text over several lines, with escapes", cat.Find(null, "A long text on two lines\nwith a \"quote\"") == "En lång text på två rader\nmed ett \"citat\"");
            CheckTrue("Fuzzy and empty translations are not used", cat.Find(null, "Sky") == null && cat.Find(null, "Planner") == null);
            // written out and read back, the same entries
            var again = PoCatalog.Parse(PoCatalog.Write(cat.Header, cat.Entries));
            CheckTrue("Written and read back", again.Entries.Count == cat.Entries.Count && again.Entries.Zip(cat.Entries, (a, b) => a.Key == b.Key && a.Plural == b.Plural && a.Fuzzy == b.Fuzzy && a.Strings.SequenceEqual(b.Strings) && a.References.SequenceEqual(b.References)).All(x => x)
                && again.Header["Plural-Forms"] == cat.Header["Plural-Forms"]);
            CheckTrue("Entries without blank lines between them", PoCatalog.Parse("msgid \"A\"\nmsgstr \"a\"\nmsgid \"B\"\nmsgstr \"b\"\n").Find(null, "B") == "b");
            bool refused = false;
            try { PoCatalog.Parse("msgid \"A\nmsgstr \"a\"\n"); } catch (FormatException) { refused = true; }
            CheckTrue("A broken file is refused", refused);
        }

        [TestMethod]
        public void TestPluralRules()
        {
            Func<string, long, int> form = (rule, n) => PoCatalog.Parse("msgid \"\"\nmsgstr \"Plural-Forms: nplurals=3; plural=" + rule + ";\\n\"\n").PluralIndex(n);
            CheckTrue("Swedish and English: singular for 1 only", form("(n != 1)", 1) == 0 && form("(n != 1)", 0) == 1 && form("(n != 1)", 2) == 1);
            CheckTrue("French: singular for 0 and 1", form("(n > 1)", 0) == 0 && form("(n > 1)", 1) == 0 && form("(n > 1)", 2) == 1);
            const string polish = "(n==1 ? 0 : n%10>=2 && n%10<=4 && (n%100<10 || n%100>=20) ? 1 : 2)";
            CheckTrue("Polish: three forms", form(polish, 1) == 0 && form(polish, 3) == 1 && form(polish, 22) == 1 && form(polish, 12) == 2 && form(polish, 5) == 2 && form(polish, 25) == 2);
            CheckTrue("A broken rule falls back to singular for 1", form("n ?? 1", 1) == 0 && form("n ?? 1", 4) == 1);
            CheckTrue("No rule: singular for 1", new PoCatalog().PluralIndex(1) == 0 && new PoCatalog().PluralIndex(2) == 1);
        }

        [TestMethod]
        public void TestLanguages()
        {
            string folder = Path.Combine(Path.GetTempPath(), "SidraluneTests", "Languages");
            Directory.CreateDirectory(folder);
            string saved = Lang.Folder;
            try
            {
                File.WriteAllText(Path.Combine(folder, "sv.po"), Swedish, new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(folder, "nb.po"), "msgid \"Tonight\"\nmsgstr \"I kveld\"\n", new UTF8Encoding(false));
                Lang.Folder = folder;
                CheckTrue("Swedish for sv-SE", Lang.Use("sv-SE") == "sv" && Lang.T("Tonight") == "I kväll");
                CheckTrue("Formats filled in", Lang.T("Go to {0}", "Himlen") == "Gå till Himlen");
                CheckTrue("English on request, for texts kept as data", Lang.InEnglish(() => Lang.T("Tonight") + " " + Lang.Tn("{0} star", "{0} stars", 2)) == "Tonight 2 stars" && Lang.T("Tonight") == "I kväll");
                CheckTrue("Contexts",Lang.Tc("file", "Open") == "Öppna" && Lang.Tc("cluster", "Open") == "Öppen");
                CheckTrue("Plurals, filled in with the count", Lang.Tn("{0} star", "{0} stars", 1) == "1 stjärna" && Lang.Tn("{0} star", "{0} stars", 3) == "3 stjärnor");
                CheckTrue("Numbers stay invariant", Lang.T("Go to {0}", 1.5) == "Gå till 1.5");
                CheckTrue("Dates in a format get the language's names", Lang.T("On {0:ddd d MMM}", new DateTime(2026, 11, 2)) == "On mån 2 nov");
                CheckTrue("Untranslated text in English", Lang.T("Planner") == "Planner" && Lang.T("Not in the file") == "Not in the file" && Lang.Tn("{0} comet", "{0} comets", 2) == "2 comets");
                CheckTrue("Swedish day names for dates in words", string.Format(Lang.DateNames, "{0:ddd d MMMM}", new DateTime(2026, 11, 2)) == "mån 2 november");
                CheckTrue("Numbers in dates' culture stay invariant", string.Format(Lang.DateNames, "{0:0.00}", 1.25) == "1.25");
                CheckTrue("Norwegian (no) uses Bokmål", Lang.Use("no") == "nb" && Lang.T("Tonight") == "I kveld");
                CheckTrue("A language without a file falls back to English", Lang.Use("de-DE") == "en" && Lang.T("Tonight") == "Tonight" && Lang.DateNames.Equals(System.Globalization.CultureInfo.InvariantCulture));
                var available = Lang.Available();
                CheckTrue("Languages on offer, English first, by their own names", available.Select(l => l.Key).SequenceEqual(new[] { "en", "nb", "sv" }) && available[2].Value == "Svenska",
                    string.Join(", ", available.Select(l => l.Key + "=" + l.Value)));
                CheckTrue("Nynorsk uses Bokmål", Lang.Use("nn-NO") == "nb" && Lang.T("Tonight") == "I kveld");
                File.WriteAllText(Path.Combine(folder, "pt.po"), "msgid \"Tonight\"\nmsgstr \"Esta noite\"\n\nmsgid \"Sky\"\nmsgstr \"Céu\"\n", new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(folder, "pt-BR.po"), "msgid \"Tonight\"\nmsgstr \"Hoje à noite\"\n", new UTF8Encoding(false));
                CheckTrue("A regional file with only its differences, the rest from the language", Lang.Use("pt-BR") == "pt-BR" && Lang.T("Tonight") == "Hoje à noite" && Lang.T("Sky") == "Céu" && Lang.T("Planner") == "Planner");
                CheckTrue("The language alone without the regional file", Lang.Use("pt-PT") == "pt" && Lang.T("Tonight") == "Esta noite");
                var names = Lang.Available();
                CheckTrue("Languages sorted by their own names; Latin, which Windows lacks, named too", names.Select(l => l.Key).SequenceEqual(new[] { "en", "nb", "pt", "pt-BR", "sv" }) && Lang.NativeName("la") == "Latina",
                    string.Join(", ", names.Select(l => l.Key + "=" + l.Value)));
                // three plural forms: one, a few (2–4, 22–24 …) and many, as Polish counts
                File.WriteAllText(Path.Combine(folder, "pl.po"), "msgid \"\"\nmsgstr \"\"\n\"Plural-Forms: nplurals=3; plural=(n==1 ? 0 : n%10>=2 && n%10<=4 && (n%100<10 || n%100>=20) ? 1 : 2);\\n\"\n\n" +
                    "msgid \"{0} star\"\nmsgid_plural \"{0} stars\"\nmsgstr[0] \"{0} gwiazda\"\nmsgstr[1] \"{0} gwiazdy\"\nmsgstr[2] \"{0} gwiazd\"\n", new UTF8Encoding(false));
                Lang.Use("pl");
                var forms = new[] { 1, 2, 5, 12, 22, 25 }.Select(n => Lang.Tn("{0} star", "{0} stars", n)).ToArray();
                CheckTrue("Three plural forms", forms.SequenceEqual(new[] { "1 gwiazda", "2 gwiazdy", "5 gwiazd", "12 gwiazd", "22 gwiazdy", "25 gwiazd" }), string.Join(", ", forms));
                File.Delete(Path.Combine(folder, "pl.po"));
                CheckTrue("The pseudo-language", Lang.Use(Lang.PseudoCode) == Lang.PseudoCode && Lang.T("Tonight").StartsWith("[") && Lang.T("Tonight").EndsWith("]") && Lang.T("Tonight").Length > "Tonight".Length + 3);
                var p = Lang.T("{0} of {1:0.0} stars", 3, 2.5);
                CheckTrue("Pseudo-language keeps placeholders", p.Contains("3") && p.Contains("2.5") && !p.Contains("stars"), p);
                CheckTrue("Pseudo-language keeps line breaks", Lang.Pseudo("a\r\nb").Contains("\r\n"));
                CheckTrue("Option names in words, translated", Lang.Option("UntilTime").StartsWith("[") && Lang.InEnglish(() => Lang.Option("HotPixelRemoval")) == "Hot Pixel Removal");
                CheckTrue("Capitals and digits kept together in option names", Lang.Words("RGGB") == "RGGB" && Lang.Words("Polynomial2") == "Polynomial2" && Lang.Words("GainEPerAdu") == "Gain EPer Adu");
            }
            finally
            {
                Lang.Folder = saved;
                Lang.Use("en");
                try { Directory.Delete(folder, true); } catch { }
            }
        }

        [TestMethod]
        public void TestFindingTexts()
        {
            const string source =
                "var a = T(\"Tonight\");\n" +
                "var b = Lang.T(\"Go to {0}\", item.Title);\n" +
                "var c = Tc(\"file\", \"Open\");\n" +
                "var d = Tn(\"{0} star\", \"{0} stars\", n);\n" +
                "var e = T(\"A long text \" +\n    \"on two lines\\r\\nwith a \\\"quote\\\" and \\u00e5\");\n" +
                "// T(\"In a comment\")\n" +
                "/// <summary>Marked with T(\"…\").</summary>\n" +
                "var f = GetT(\"Not this\"); var g = AT(\"Nor this\"); var h = T(name);\n" +
                "var i = new Dataset { Name = Lang.N(\"Kept as data\") }; var j = GetN(\"Not this either\");\n";
            var found = Lang.Extract(source);
            CheckTrue("Texts in T, N, Tc and Tn calls", found.Count == 6, string.Join(" | ", found.Select(x => x.Id)));
            CheckTrue("Marked only, translated where shown", found[5].Id == "Kept as data" && Lang.N("Kept as data") == "Kept as data");
            CheckTrue("Plain and formatted", found[0].Id == "Tonight" && found[1].Id == "Go to {0}");
            CheckTrue("With a context", found[2].Context == "file" && found[2].Id == "Open");
            CheckTrue("With a plural", found[3].Id == "{0} star" && found[3].Plural == "{0} stars");
            CheckTrue("Joined over lines, with escapes", found[4].Id == "A long text on two lines\r\nwith a \"quote\" and å");
            var collected = Lang.Collect(new[] { new KeyValuePair<string, string>("A.cs", source), new KeyValuePair<string, string>("B.cs", "T(\"Tonight\"); T(\"Sky\");") });
            CheckTrue("Each text once, with the files that use it", collected.Count == 7 && collected[0].References.SequenceEqual(new[] { "A.cs", "B.cs" }) && collected.Last().Id == "Sky");
            CheckTrue("Placeholders", Lang.Placeholders("{1:0.0} of {0} and {0}, {{literal}}").SequenceEqual(new[] { 0, 1 }));
        }
    }
}
