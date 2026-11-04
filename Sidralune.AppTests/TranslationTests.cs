using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sidralune.Devices.Automation;
using Sidralune.Core.Util;
using Sidralune.Tests;
using Sidralune.UI;
using Sidralune.Views;
using static Sidralune.AppTests.AppHost;
using static Sidralune.Tests.Checks;

namespace Sidralune.AppTests
{
    /// <summary>
    /// The interface languages: the template and the .po files against the texts marked in the source, Swedish in the
    /// Settings view, and the pseudo-language finding text that is not marked for translation.
    /// </summary>
    [TestClass]
    public class TranslationTests : TestBase
    {
        private static readonly string[] Projects = { "Sidralune", "Sidralune.Core", "Sidralune.Imaging", "Sidralune.Science", "Sidralune.Devices" };

        // the repository, found from this file's place when it was compiled
        private static string RepoRoot([CallerFilePath] string here = "") => Path.GetDirectoryName(Path.GetDirectoryName(here));

        private static string SourceLanguages => Path.Combine(RepoRoot(), "Sidralune", "Languages");

        /// <summary>The texts marked for translation in the source, each once, with the files using them.</summary>
        private static List<PoEntry> SourceTexts()
        {
            string root = RepoRoot();
            var files = Projects.SelectMany(p => Directory.GetFiles(Path.Combine(root, p), "*.cs", SearchOption.AllDirectories))
                .Select(f => f.Substring(root.Length + 1).Replace('\\', '/'))
                .Where(f => !f.Contains("/bin/") && !f.Contains("/obj/"))
                .OrderBy(f => f, StringComparer.Ordinal);
            var texts = Lang.Collect(files.Select(f => new KeyValuePair<string, string>(f, File.ReadAllText(Path.Combine(root, f)))));
            // option names and values in the property grids come from the members, not from literals
            var options = OptionTypes.SelectMany(GridAdapter.Texts).Concat(Sequencer.ItemTypes.Select(t => GridAdapter.Words(t.Name))).Concat(new[] { "True", "False" });
            foreach (var text in options.Distinct())
                if (!texts.Any(e => e.Context == "option" && e.Id == text))
                    texts.Add(new PoEntry { Context = "option", Id = text, References = { "property grid" } });
            return texts;
        }

        /// <summary>The option classes the views show in property grids.</summary>
        private static readonly Type[] OptionTypes = new[]
        {
            typeof(TelescopeProfile), typeof(EyepieceProfile), typeof(CameraProfile), typeof(LogEntry), typeof(Sidralune.Imaging.Stacking.StackingSettings),
            typeof(Sidralune.Imaging.Planetary.PippOptions), typeof(Sidralune.Imaging.Planetary.LuckyOptions), typeof(Sidralune.Science.Photometry.ApertureSettings)
        }.Concat(Sequencer.ItemTypes).ToArray();

        [TestMethod]
        public void TestTranslationFiles()
        {
            var texts = SourceTexts();
            var keys = new HashSet<string>(texts.Select(t => t.Key));
            CheckTrue("Texts marked for translation", texts.Count > 150, texts.Count.ToString());
            // the template lists exactly the marked texts; when it does not, a fresh one is written for copying in
            string potPath = Path.Combine(SourceLanguages, "Sidralune.pot");
            var pot = File.Exists(potPath) ? PoCatalog.Load(potPath) : new PoCatalog();
            var potKeys = new HashSet<string>(pot.Entries.Select(e => e.Key));
            var missing = texts.Where(t => !potKeys.Contains(t.Key)).Select(t => t.Id).ToList();
            var stale = pot.Entries.Where(e => !keys.Contains(e.Key)).Select(e => e.Id).ToList();
            bool plurals = texts.All(t => pot.Entries.Any(e => e.Key == t.Key && e.Plural == t.Plural));
            if (missing.Count > 0 || stale.Count > 0 || !plurals)
            {
                var header = new[]
                {
                    new KeyValuePair<string, string>("Project-Id-Version", "Sidralune"),
                    new KeyValuePair<string, string>("Language", ""),
                    new KeyValuePair<string, string>("MIME-Version", "1.0"),
                    new KeyValuePair<string, string>("Content-Type", "text/plain; charset=UTF-8"),
                    new KeyValuePair<string, string>("Content-Transfer-Encoding", "8bit"),
                    new KeyValuePair<string, string>("Plural-Forms", "nplurals=2; plural=(n != 1);")
                };
                File.WriteAllText(Path.Combine(Root, "Sidralune.pot"), PoCatalog.Write(header, texts), new UTF8Encoding(false));
            }
            CheckTrue("The template has every marked text (a fresh one is in %TEMP%\\SidraluneAppTests\\Sidralune.pot)", missing.Count == 0, string.Join(" | ", missing.Take(20)));
            CheckTrue("The template has no texts that are no longer used", stale.Count == 0, string.Join(" | ", stale.Take(20)));
            CheckTrue("The template has the plural forms", plurals);
            // each language: only texts in use, the same placeholders, and Swedish complete
            var languages = Directory.GetFiles(SourceLanguages, "*.po");
            CheckTrue("Swedish is there", languages.Any(f => Path.GetFileName(f) == "sv.po"));
            foreach (var file in languages)
            {
                string name = Path.GetFileName(file);
                PoCatalog po;
                try { po = PoCatalog.Load(file); }
                catch (FormatException ex) { CheckTrue(name + " can be read", false, ex.Message); continue; }
                var unused = po.Entries.Where(e => !keys.Contains(e.Key)).Select(e => e.Id).ToList();
                CheckTrue(name + ": no texts that are no longer used", unused.Count == 0, string.Join(" | ", unused.Take(20)));
                var wrong = po.Entries.Where(e => e.Strings.Any(s => s.Length > 0 && !Lang.Placeholders(s).SequenceEqual(Lang.Placeholders(e.Plural ?? e.Id)) && !Lang.Placeholders(s).SequenceEqual(Lang.Placeholders(e.Id)))).Select(e => e.Id).ToList();
                CheckTrue(name + ": the same {n} placeholders as the English", wrong.Count == 0, string.Join(" | ", wrong.Take(20)));
                CheckTrue(name + ": the plural rule is given", po.Header.ContainsKey("Plural-Forms"));
                // every counted text has as many forms as the rule asks for (three for Polish, four for Slovene)
                var forms = System.Text.RegularExpressions.Regex.Match(po.Header.ContainsKey("Plural-Forms") ? po.Header["Plural-Forms"] : "", "nplurals\\s*=\\s*(\\d+)");
                int nplurals = forms.Success ? int.Parse(forms.Groups[1].Value) : 2;
                var short_ = po.Entries.Where(e => e.Plural != null && e.Strings.Count(s => s.Length > 0) != nplurals).Select(e => e.Id).ToList();
                CheckTrue(name + ": " + nplurals + " plural forms for each counted text", short_.Count == 0, string.Join(" | ", short_.Take(10)));
                var untranslated = texts.Where(t => { var e = po.Entries.FirstOrDefault(x => x.Key == t.Key); return e == null || e.Fuzzy || e.Strings.Length == 0 || e.Strings.Any(s => s.Length == 0); }).Select(t => t.Id).ToList();
                Info(string.Format("{0}: {1} of {2} texts translated", name, texts.Count - untranslated.Count, texts.Count));
                // a regional file (pt-BR.po) has only the texts that differ from its language's file, which must be there
                if (name.Contains("-")) CheckTrue(name + ": its language's own file is there", File.Exists(Path.Combine(SourceLanguages, name.Split('-')[0] + ".po")));
                else CheckTrue(name + " is complete", untranslated.Count == 0, string.Join(" | ", untranslated.Take(20)));
            }
        }

        // the texts of a view as shown: labels, buttons, check boxes, card titles and tabs, and each row label with its width
        private static List<Tuple<string, Control>> Texts(Control root)
        {
            var list = new List<Tuple<string, Control>>();
            // a property grid's option names and values are checked in TestOptionTexts
            Func<Control, bool> inPropertyGrid = c => { for (var p = c.Parent; p != null; p = p.Parent) if (p is PropertyGrid) return true; return false; };
            foreach (var c in new[] { root }.Concat(All(root)).Where(c => !inPropertyGrid(c)))
            {
                if (c is Label || c is FlatButton || c is CheckBox) list.Add(Tuple.Create(c.Text, c));
                var card = c as Card;
                if (card != null && card.Title != null) list.Add(Tuple.Create(card.Title, c));
                if (card != null && card.Subtitle != null) list.Add(Tuple.Create(card.Subtitle, c));
                var tabs = c as SegmentedTabs;
                if (tabs != null) list.AddRange(tabs.Tabs.Select(t => Tuple.Create(t, c)));
                var grid = c as DataGridView;
                if (grid != null) list.AddRange(grid.Columns.Cast<DataGridViewColumn>().Select(col => Tuple.Create(col.HeaderText, c)));
                var plot = c as PlotControl;
                if (plot != null) list.Add(Tuple.Create(plot.Title, c));
            }
            // icons are characters of the icon font's private use area, not text
            return list.Where(t => !string.IsNullOrWhiteSpace(t.Item1) && !t.Item1.All(ch => ch >= '' && ch <= '')).ToList();
        }

        // texts shown in the pseudo-language that are not marked for translation, apart from names that stay as they are
        private static List<string> Unmarked(Control view, params string[] names) =>
            Texts(view).Where(t => !t.Item1.TrimStart().StartsWith("[") && !names.Contains(t.Item1)).Select(t => t.Item1 + " (" + t.Item2.GetType().Name + ")").ToList();

        private static string LanguagesFolder => Path.Combine(Path.GetDirectoryName(typeof(TranslationTests).Assembly.Location), "Languages");

        [TestMethod]
        public void TestSwedishSettings()
        {
            CheckTrue("The language files are installed with the program", File.Exists(Path.Combine(LanguagesFolder, "sv.po")), LanguagesFolder);
            string saved = Lang.Folder;
            try
            {
                var result = OnUi(() =>
                {
                    Lang.Folder = LanguagesFolder;
                    Lang.Use("sv");
                    using (var view = new SettingsView { Size = new Size(1300, 800) })
                    {
                        Theme.Apply(view);
                        view.CreateControl();
                        var texts = Texts(view);
                        // row labels have a fixed width: a Swedish one must still fit on its line
                        var tight = All(view).OfType<Label>().Where(l => { var g = l.Parent as FormGrid; return g != null && g.GetColumn(l) == 0 && g.GetColumnSpan(l) == 1; })
                            .Where(l => l.Width > 0 && TextRenderer.MeasureText(l.Text, l.Font).Width > l.Width).Select(l => l.Text).ToList();
                        // the workspace names under the icons of the navigation rail
                        var rail = Field<NavRail>(App, "_rail");
                        tight.AddRange(rail.Items.Select(i => Lang.T(i.Title)).Where(t => TextRenderer.MeasureText(t, Theme.SmallFont).Width > rail.Width - 4));
                        var dir = Environment.GetEnvironmentVariable("SIDRALUNE_RENDER_DIR");
                        if (!string.IsNullOrEmpty(dir))
                            for (int i = 0; i < 6; i++)
                            {
                                Find<SegmentedTabs>(view).SelectedIndex = i;
                                using (var bmp = new Bitmap(view.Width, view.Height))
                                {
                                    view.DrawToBitmap(bmp, new Rectangle(Point.Empty, bmp.Size));
                                    bmp.Save(Path.Combine(dir, "settings-sv-" + i + ".png"));
                                }
                            }
                        // choosing a language keeps it for the next start and offers a restart; Windows' language again does not
                        Find<SegmentedTabs>(view).SelectedIndex = 4;
                        var combo = All(view).OfType<ComboBox>().First(c => c.Items.Contains("Svenska"));
                        var restart = All(view).OfType<FlatButton>().First(b => b.Text == "Starta om nu för att byta språk");
                        bool hidden = !restart.Parent.Visible;
                        combo.SelectedItem = "Svenska";
                        bool chosen = AppState.Settings.Language == "sv" && restart.Parent.Visible;
                        combo.SelectedIndex = 0;
                        bool back = AppState.Settings.Language == "" && !restart.Parent.Visible;
                        if (!(hidden && chosen && back)) tight.Add("choosing a language: " + hidden + " " + chosen + " " + back);
                        return Tuple.Create(texts.Select(t => t.Item1).ToList(), tight);
                    }
                });
                CheckTrue("Settings in Swedish: tabs, rows and buttons", new[] { "Plats", "Utrustning", "Utseende", "Spara plats", "Språk" }.All(result.Item1.Contains), string.Join(" | ", result.Item1.Take(30)));
                CheckTrue("Swedish row labels and workspace names fit; choosing a language offers a restart", result.Item2.Count == 0, string.Join(" | ", result.Item2));
            }
            finally
            {
                OnUi(() => { Lang.Folder = saved; Lang.Use("en"); });
            }
        }

        [TestMethod]
        public void TestEveryLanguageFits()
        {
            string saved = Lang.Folder;
            try
            {
                var codes = OnUi(() => { Lang.Folder = LanguagesFolder; return Lang.Available().Select(l => l.Key).Where(c => c != "en").ToList(); });
                CheckTrue("The languages are installed", codes.Count >= 28, string.Join(", ", codes));
                foreach (var code in codes)
                {
                    var tight = OnUi(() =>
                    {
                        Lang.Use(code);
                        var found = new List<string>();
                        // the workspace names under the icons of the navigation rail
                        var rail = Field<NavRail>(App, "_rail");
                        found.AddRange(rail.Items.Select(i => Lang.T(i.Title)).Where(t => TextRenderer.MeasureText(t, Theme.SmallFont).Width > rail.Width - 4).Select(t => "rail: " + t));
                        // row labels in every tab of every workspace, which widen only so far
                        foreach (var view in new Control[] { new SettingsView(), new TonightView(), new SkyView(), new PlannerView(), new ObservatoryView(), new StackingView(), new ProcessingView(),
                            new PlanetaryView(), new AstrometryView(), new PhotometryView(), new SpectroscopyView(), new SolarSystemView(), new RadioView(), new DatabaseView(), new ScriptingView() })
                            using (view)
                            {
                                view.Size = new Size(1500, 900);
                                Theme.Apply(view);
                                view.CreateControl();
                                var tabs = Find<SegmentedTabs>(view);
                                for (int i = 0; i < (tabs != null ? tabs.Tabs.Count : 1); i++)
                                {
                                    if (tabs != null) tabs.SelectedIndex = i;
                                    foreach (var g in All(view).OfType<FormGrid>()) g.PerformLayout();
                                    found.AddRange(All(view).OfType<Label>().Where(l => { var g = l.Parent as FormGrid; return g != null && g.Visible && g.GetColumn(l) == 0 && g.GetColumnSpan(l) == 1; })
                                        .Where(l => l.Width > 0 && TextRenderer.MeasureText(l.Text, l.Font).Width > l.Width).Select(l => view.GetType().Name + ": " + l.Text));
                                }
                            }
                        return found.Distinct().ToList();
                    });
                    CheckTrue(code + ": workspace names and row labels fit", tight.Count == 0, string.Join(" | ", tight.Take(30)));
                }
            }
            finally
            {
                OnUi(() => { Lang.Folder = saved; Lang.Use("en"); });
            }
        }

        [TestMethod]
        public void TestPseudoLanguage()
        {
            try
            {
                OnUi(() => Lang.Use(Lang.PseudoCode));
                // fresh views, built in the pseudo-language; the app's own stay in English for the other tests
                var settings = OnUi(() =>
                {
                    using (var view = new SettingsView { Size = new Size(1300, 800) })
                    {
                        view.CreateControl();
                        return Unmarked(view, "Sidralune", "Copyright © Jimmy Nordström");
                    }
                });
                CheckTrue("Every text in Settings is marked for translation", settings.Count == 0, string.Join(" | ", settings.Take(30)));
                // Tonight with the night worked out: its summary, cards and tables
                var tonight = OnUi(() => new TonightView { Size = new Size(1500, 900) });
                OnUi(() => typeof(TonightView).GetMethod("Refresh", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance, null, new[] { typeof(bool) }, null).Invoke(tonight, new object[] { true }));
                CheckTrue("Tonight works out the night", WaitFor(() => Field<DataGridView>(tonight, "_planetGrid").Columns.Count > 0 && Field<DataGridView>(tonight, "_eventGrid").Columns.Count > 0, 60000));
                var night = OnUi(() => Unmarked(tonight, "Δ"));
                CheckTrue("Every text in Tonight is marked for translation", night.Count == 0, string.Join(" | ", night.Take(30)));
                var rows = OnUi(() => Field<DataGridView>(tonight, "_planetGrid").Rows.Cast<DataGridViewRow>().Select(r => (string)r.Cells[0].Value).ToList());
                CheckTrue("Tonight's planets by their translated names", rows.Count == 7 && rows.All(n => n.StartsWith("[")), string.Join(", ", rows));
                var events = OnUi(() => Field<DataGridView>(tonight, "_eventGrid").Rows.Cast<DataGridViewRow>().Select(r => (string)r.Cells[0].Value + " " + (string)r.Cells[1].Value).ToList());
                CheckTrue("Coming events with translated descriptions and day names", events.Count > 5 && events.All(e => e.StartsWith("[") && e.Contains(" [")), string.Join(" | ", events.Where(e => !e.StartsWith("[") || !e.Contains(" [")).Take(10)));
                // the Sky view with Saturn selected, and the star chart page
                var sky = OnUi(() =>
                {
                    var view = new SkyView { Size = new Size(1500, 900) };
                    Call(view, "ShowInfo", SkyView.PlanetObject(Core.Ephemeris.Body.Saturn));
                    var info = Field<TextBox>(view, "_objInfo").Text.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries).Where(l => !l.StartsWith("[")).Select(l => l + " (info)");
                    var page = view.StarChart.Page;
                    using (var bmp = new Bitmap(1200, 800))
                    using (var g = Graphics.FromImage(bmp))
                    {
                        g.ScaleTransform(96 / 25.4f, 96 / 25.4f);
                        page.Site = AppState.Site; page.Time = AppState.Now; page.Layers = new ChartLayers();
                        page.Render(g, new RectangleF(0, 0, 300, 200));
                    }
                    var headings = page.InfoHeadings.Where(h => !h.StartsWith("[")).Select(h => h + " (star chart)");
                    return Unmarked(view).Concat(info).Concat(headings).ToList();
                });
                CheckTrue("Every text in Sky, its object information and the star chart's headings is marked for translation", sky.Count == 0, string.Join(" | ", sky.Take(30)));
                // Planner with its table filled: the types are saved in English and shown translated
                var planner = OnUi(() => new PlannerView { Size = new Size(1500, 900) });
                CheckTrue("Planner fills its table", WaitFor(() => Field<DataGridView>(planner, "_grid").Rows.Count > 0, 60000));
                // the logbook's heading names the selected object
                var plan = OnUi(() => Unmarked(planner, "✓", Field<Label>(planner, "_logTitle").Text));
                CheckTrue("Every text in Planner is marked for translation", plan.Count == 0, string.Join(" | ", plan.Take(30)));
                var types = OnUi(() => Field<DataGridView>(planner, "_grid").Rows.Cast<DataGridViewRow>().Select(r => (string)r.Cells["Type"].Value).Where(t => !string.IsNullOrEmpty(t)).ToList());
                var stored = OnUi(() => Field<PlannerData>(planner, "_data").Lists.SelectMany(l => l.Entries).Select(e => e.Type).Where(t => !string.IsNullOrEmpty(t)).ToList());
                CheckTrue("Planner shows the types translated and keeps them in English", types.Count > 0 && types.All(t => t.StartsWith("[")) && stored.All(t => !t.StartsWith("[")), string.Join(", ", types.Concat(stored).Where(t => t.StartsWith("[") != types.Contains(t)).Distinct().Take(10)));
                var observatory = OnUi(() =>
                {
                    var view = new ObservatoryView { Size = new Size(1500, 900) };
                    var tabs = Find<SegmentedTabs>(view);
                    for (int i = 0; i < tabs.Tabs.Count; i++) tabs.SelectedIndex = i;
                    return Unmarked(view, "ASCOM…");
                });
                CheckTrue("Every text in Observatory is marked for translation", observatory.Count == 0, string.Join(" | ", observatory.Take(30)));
                // Deep sky, Process and Planetary, every tab
                var images = OnUi(() =>
                {
                    var found = new List<string>();
                    foreach (var view in new Control[] { new StackingView(), new ProcessingView(), new PlanetaryView(), new AstrometryView(), new PhotometryView(), new SpectroscopyView(),
                        new SolarSystemView(), new RadioView(), new DatabaseView(), new ScriptingView() })
                    {
                        view.Size = new Size(1500, 900);
                        var tabs = Find<SegmentedTabs>(view);
                        if (tabs != null) for (int i = 0; i < tabs.Tabs.Count; i++) tabs.SelectedIndex = i;
                        found.AddRange(Unmarked(view, "1:1", "FWHM", "RA", "B−V", "SNR", "δ", "ADQL", "X", "Y").Select(t => view.GetType().Name + ": " + t));
                    }
                    return found;
                });
                CheckTrue("Every text in the other workspaces is marked for translation", images.Count == 0, string.Join(" | ", images.Take(30)));
            }
            finally
            {
                OnUi(() => Lang.Use("en"));
            }
        }

        [TestMethod]
        public void TestSwedishTexts()
        {
            string saved = Lang.Folder;
            try
            {
                var texts = OnUi(() =>
                {
                    Lang.Folder = LanguagesFolder;
                    Lang.Use("sv");
                    var phases = Core.Ephemeris.Events.MoonPhases(2461346.5, 2461376.5).Select(e => e.Description).ToList();
                    return new[]
                    {
                        Core.Ephemeris.BodyNames.Name(Core.Ephemeris.Body.Saturn), Core.Ephemeris.BodyNames.Name(Core.Ephemeris.Body.Moon),
                        Core.Catalogs.DeepSkyObject.TypeLabel(Core.Catalogs.ObjectType.OpenCluster), Core.Observing.VisibilityResult.Label(Core.Observing.Detectability.Obvious),
                        string.Join(", ", phases), new DateTime(2026, 11, 2).ToString("ddd d MMM", Lang.DateNames),
                        Lang.T(DataManager.Get("hyg").Name), Lang.T(DataManager.Get("comets").Description), Core.Catalogs.CatalogManager.Instance.StarSource,
                        Lang.InEnglish(() => Core.Catalogs.CatalogManager.Instance.StarSource), DataManager.Get("hyg").Name
                    };
                });
                CheckTrue("Planet names, object types, visibility, moon phases and day names in Swedish",
                    texts[0] == "Saturnus" && texts[1] == "Månen" && texts[2] == "Öppen stjärnhop" && texts[3] == "Tydlig" && texts[4].Contains("Fullmåne") && texts[4].Contains("Nymåne") && texts[5] == "mån 2 nov", string.Join(" | ", texts));
                CheckTrue("Datasets and the loaded catalogues in Swedish, kept in English as data",
                    texts[6] == "HYG stjärndatabas v4.1" && texts[7].StartsWith("Banelement") && texts[8] != texts[9] && texts[9].StartsWith("built-in") == texts[8].StartsWith("inbyggd") && texts[10] == "HYG star database v4.1", string.Join(" | ", texts.Skip(6)));
            }
            finally
            {
                OnUi(() => { Lang.Folder = saved; Lang.Use("en"); });
            }
        }

        [TestMethod]
        public void TestRestartCommandLine()
        {
            // restarting for a new language keeps the other options, leaves out --lang and quotes what Windows splits
            CheckTrue("Same options, without --lang", Program.RestartArguments(new[] { "--lang", "qps", "--view", "sky" }) == "--view sky", Program.RestartArguments(new[] { "--lang", "qps", "--view", "sky" }));
            var quoted = Program.RestartArguments(new[] { "--script", @"C:\My Scripts\a.cs", "say \"hi\"", @"C:\Ends In\", "" });
            CheckTrue("Spaces, quotes, a trailing backslash and empty arguments quoted", quoted == "--script \"C:\\My Scripts\\a.cs\" \"say \\\"hi\\\"\" \"C:\\Ends In\\\\\" \"\"", quoted);
            CheckTrue("Nothing to keep", Program.RestartArguments(new string[0]) == "" && Program.RestartArguments(new[] { "--lang", "sv" }) == "");
            CheckTrue("Not restarting unless asked", !Program.RestartRequested);
        }

        [TestMethod]
        public void TestOptionTexts()
        {
            string saved = Lang.Folder;
            try
            {
                OnUi(() => Lang.Use(Lang.PseudoCode));
                // every option name in the grids, nested ones included, and the values of choices and yes/no
                var unmarked = OnUi(() =>
                {
                    var list = new List<string>();
                    Action<object> walk = null;
                    walk = o =>
                    {
                        foreach (PropertyDescriptor p in TypeDescriptor.GetProperties(new GridAdapter(o, "Test")))
                        {
                            if (!p.DisplayName.StartsWith("[")) list.Add(o.GetType().Name + "." + p.Name);
                            var v = p.GetValue(null);
                            if (v is GridAdapter) walk(((GridAdapter)v).Target);
                            else if ((v is Enum || v is bool) && !p.Converter.ConvertToString(v).StartsWith("[")) list.Add(o.GetType().Name + "." + p.Name + " = " + v);
                        }
                    };
                    foreach (var t in OptionTypes) walk(Activator.CreateInstance(t));
                    return list;
                });
                CheckTrue("Option names and values are marked in the pseudo-language", unmarked.Count == 0, string.Join(" | ", unmarked.Take(20)));
                var back = OnUi(() =>
                {
                    var p = TypeDescriptor.GetProperties(new GridAdapter(new Sidralune.Imaging.Stacking.StackingSettings(), "Test"))["HotPixelRemoval"];
                    var shown = p.Converter.ConvertToString(false);
                    var m = TypeDescriptor.GetProperties(new GridAdapter(new LoopContainer(), "Test"))["Condition"];
                    return Tuple.Create(shown, p.Converter.ConvertFromString(shown), m.Converter.ConvertFromString(m.Converter.ConvertToString(LoopCondition.UntilAltitudeBelow)), m.Converter.ConvertFromString("UntilTime"));
                });
                CheckTrue("A shown choice reads back, and so does its English name", (bool)back.Item2 == false && (LoopCondition)back.Item3 == LoopCondition.UntilAltitudeBelow && (LoopCondition)back.Item4 == LoopCondition.UntilTime, back.Item1);
                var frame = OnUi(() => TypeDescriptor.GetProperties(new GridAdapter(new TakeExposures(), "Test"))["FrameType"].Converter);
                CheckTrue("A member's own drop-down is kept", frame is FrameTypeConverter, frame.GetType().Name);
                var sv = OnUi(() =>
                {
                    Lang.Folder = LanguagesFolder;
                    Lang.Use("sv");
                    var p = TypeDescriptor.GetProperties(new GridAdapter(new Sidralune.Imaging.Stacking.StackingSettings(), "Test"))["HotPixelRemoval"];
                    return new[] { p.Converter.ConvertToString(true), new SlewToTarget().Title, Sequencer.DescribeMissing(new List<string> { "camera", "focuser" }) };
                });
                CheckTrue("Yes/no, instructions and missing devices in Swedish", sv[0] == "Ja" && sv[1] == "Peka mot målet" && sv[2].StartsWith("Sekvensen behöver en kamera och en fokuserare"), string.Join(" | ", sv));
            }
            finally
            {
                OnUi(() => { Lang.Folder = saved; Lang.Use("en"); });
            }
        }
    }
}
