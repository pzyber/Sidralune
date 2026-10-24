using System;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sidralune.Core.Catalogs;
using Sidralune.Core.Ephemeris;
using Sidralune.Core.Util;
using Sidralune.Tests;
using Sidralune.UI;
using Sidralune.Views;
using static Sidralune.AppTests.AppHost;
using static Sidralune.Tests.Checks;

namespace Sidralune.AppTests
{
    /// <summary>The planner's observing lists and log, and the Settings view (sites, night mode).</summary>
    [TestClass]
    public class PlannerAndSettingsTests : TestBase
    {
        [TestMethod]
        public void TestObservingLists()
        {
            var view = View<PlannerView>("planner");
            string file = Path.Combine(AppPaths.Settings, "planner.json");
            Func<int> entries = () => OnUi(() => { var d = Field<PlannerData>(view, "_data"); return d.Lists[d.Active].Entries.Count; });
            int before = entries();
            // the starter list holds the Messier catalogue: adding M31 again changes nothing
            OnUi(() => view.AddObject(SelectedObject.From(CatalogManager.Instance.Find("M31") ?? CatalogManager.Instance.Find("M 31"))));
            CheckTrue("A Messier list to start with", entries() == before && OnUi(() => Field<PlannerData>(view, "_data").Lists.Any(l => l.Name == "Messier catalogue" && l.Entries.Count >= 100)));
            OnUi(() => Call(view, "AddByName", "Arcturus"));
            OnUi(() => Call(view, "AddByName", "Vega"));
            OnUi(() => Call(view, "AddByName", "jupiter"));
            OnUi(() => view.AddObject(SelectedObject.From(CatalogManager.Instance.Find("Vega"))));
            CheckTrue("Objects added once each, a planet by name too", entries() == before + 3, entries() - before + " added");
            CheckTrue("Lists saved to planner.json", File.Exists(file) && File.ReadAllText(file).Contains("Vega") && File.ReadAllText(file).Contains("Jupiter"));
            int notFound = Toasts().Count(t => t.StartsWith("Not found"));
            OnUi(() => Call(view, "AddByName", "No such object 12345"));
            CheckTrue("Unknown name reported", WaitFor(() => Toasts().Count(t => t.StartsWith("Not found: No such object 12345")) > notFound));
            var data = (PlannerData)Json.PopulateType(typeof(PlannerData), Json.Parse(File.ReadAllText(file)));
            var entry = data.Lists[data.Active].Entries.FirstOrDefault(e => e.Name.Contains("Vega"));
            CheckTrue("Saved entries keep coordinates and magnitude", entry != null && Math.Abs(entry.RaDeg - 279.23) < 0.05 && entry.Mag < 0.5);
            CheckTrue("Planets are stored by name", data.Lists[data.Active].Entries.Any(e => e.Planet == "Jupiter"));
            var reopened = OnUi(() => new PlannerView());
            CheckTrue("A new planner view loads the saved lists", WaitFor(() => Field<PlannerData>(reopened, "_data").Lists.SelectMany(l => l.Entries).Count(e => e.Name.Contains("Vega")) == 1));
            OnUi(() => reopened.Dispose());
            CheckTrue("Planner commands", OnUi(() => view.GetCommands().Any()));
        }

        [TestMethod]
        public void TestSites()
        {
            var view = View<SettingsView>("settings");
            Func<string, TextBox> box = label => OnUi(() =>
            {
                var l = Find<Label>(view, x => x.Text == label);
                var grid = (TableLayoutPanel)l.Parent;
                return (TextBox)grid.GetControlFromPosition(1, grid.GetRow(l));
            });
            var name = box("Name"); var lat = box("Latitude (°, N positive)"); var lon = box("Longitude (°, E positive)"); var horizon = box("Horizon profile (az:alt, …)");
            int sites = OnUi(() => AppState.Settings.Sites.Count);
            OnUi(() => Button(view, "New site").PerformClick());
            CheckTrue("New site", OnUi(() => AppState.Settings.Sites.Count) == sites + 1 && OnUi(() => name.Text) == "New site");
            int siteChanges = 0;
            EventHandler h = (s, e) => siteChanges++;
            AppState.SiteChanged += h;
            try
            {
                OnUi(() => { name.Text = "Sydney"; lat.Text = "-33 52 04"; lon.Text = "151.2093"; horizon.Text = "180:12, 0:5, 90:8"; Button(view, "Save site").PerformClick(); });
                var site = OnUi(() => AppState.Settings.Sites.Last());
                CheckTrue("Site saved from sexagesimal and decimal input", site.Name == "Sydney" && Math.Abs(site.LatitudeDeg + (33 + 52 / 60.0 + 4 / 3600.0)) < 1e-9 && Math.Abs(site.LongitudeDeg - 151.2093) < 1e-9);
                CheckTrue("Horizon profile parsed and sorted by azimuth", site.HorizonProfile.Count == 3 && site.HorizonProfile[0][0] == 0 && site.HorizonProfile[2][1] == 12);
                CheckTrue("Saving raises SiteChanged and writes the settings", siteChanges >= 1 && File.ReadAllText(Directory.GetFiles(AppPaths.Settings, "settings*.json")[0]).Contains("Sydney"));
                OnUi(() => { lat.Text = "95"; Button(view, "Save site").PerformClick(); });
                CheckTrue("Impossible latitude refused", WaitFor(() => Toasts().Contains("Invalid coordinates")) && OnUi(() => AppState.Settings.Sites.Last().LatitudeDeg) < 0);
                OnUi(() => Button(view, "Use this site").PerformClick());
                CheckTrue("Use this site makes it the active site", OnUi(() => AppState.Site.Name) == "Sydney" && Devices.DeviceManager.Instance.Site.Name == "Sydney");
                OnUi(() => Button(view, "Delete").PerformClick());
                CheckTrue("Delete removes it and falls back to the first site", OnUi(() => AppState.Settings.Sites.Count) == sites && OnUi(() => AppState.Settings.ActiveSite) == 0);
            }
            finally { AppState.SiteChanged -= h; }
        }

        [TestMethod]
        public void TestNightModeEverywhere()
        {
            var view = View<SettingsView>("settings");
            var check = OnUi(() => Find<CheckBox>(view, c => c.Text.StartsWith("Use deep red")));
            try
            {
                OnUi(() => App.SetNightMode(true));
                CheckTrue("Top-bar night mode ticks the Settings box", OnUi(() => Theme.Current.IsNight && check.Checked && AppState.Settings.NightMode));
                OnUi(() => check.Checked = false);
                CheckTrue("The Settings box switches night mode off everywhere", OnUi(() => !Theme.Current.IsNight && !AppState.Settings.NightMode));
                OnUi(() => check.Checked = true);
                var p = Theme.Current;
                var colours = typeof(Palette).GetFields().Where(f => f.FieldType == typeof(System.Drawing.Color)).Select(f => new { f.Name, C = (System.Drawing.Color)f.GetValue(p) }).ToList();
                var bad = colours.Where(c => c.C.G > c.C.R || c.C.B > c.C.R).Select(c => c.Name).ToList();
                CheckTrue("Night palette has no colour with more green or blue than red", bad.Count == 0, string.Join(", ", bad));
                // the Milky Way band must stand out from the night sky at least as much as it does by day
                var day = Palette.Dark();
                Func<System.Drawing.Color, int> top = c => Math.Max(c.R, Math.Max(c.G, c.B));
                int dayLift = top(day.MilkyWay) - top(day.SkyNight), nightLift = p.MilkyWay.R - p.SkyNight.R;
                CheckTrue("Milky Way as visible at night as by day", nightLift >= dayLift, nightLift + " vs " + dayLift);
            }
            finally { OnUi(() => App.SetNightMode(false)); }
            CheckTrue("Day palette back", OnUi(() => !Theme.Current.IsNight && !check.Checked));
        }
    }
}
