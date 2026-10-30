using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sidralune.Core.MinorBodies;
using Sidralune.Imaging;
using Sidralune.Tests;
using Sidralune.UI;
using Sidralune.Views;
using static Sidralune.AppTests.AppHost;
using static Sidralune.Tests.Checks;

namespace Sidralune.AppTests
{
    /// <summary>Lists you add to can be emptied again without restarting: Derotation images, Photometry frames and stars, added minor bodies.</summary>
    [TestClass]
    public class ListEditingTests : TestBase
    {
        private static bool Toasted(string start) => WaitFor(() => Toasts().Any(t => t.StartsWith(start)));

        [TestMethod]
        public void TestPlannerUnknownValues()
        {
            // a star has no size: blank in the Planner, also after Sidralune is restarted (the lists are saved and loaded)
            var view = View<PlannerView>("planner");
            var data = OnUi(() => Field<PlannerData>(view, "_data"));
            var list = new ObservingList { Name = "Unknown values test" };
            list.Entries.Add(new ListEntry { Name = "Test star", RaDeg = 10, DecDeg = 20, Mag = 2.5 });
            list.Entries.Add(new ListEntry { Name = "Saved with a size of 0", RaDeg = 11, DecDeg = 21, Mag = 3, SizeArcmin = 0 });
            OnUi(() => { data.Lists.Add(list); Call(view, "Save"); Call(view, "Load"); });
            var loaded = OnUi(() => Field<PlannerData>(view, "_data").Lists.First(l => l.Name == list.Name));
            CheckTrue("An unknown size stays unknown after saving and loading", double.IsNaN(loaded.Entries[0].SizeArcmin) && loaded.Entries[0].Mag == 2.5);
            CheckTrue("A size of 0 from an earlier save is treated as unknown", double.IsNaN(loaded.Entries[1].SizeArcmin));
            OnUi(() => { var d = Field<PlannerData>(view, "_data"); d.Lists.RemoveAll(l => l.Name == list.Name); Call(view, "Save"); });
        }

        [TestMethod]
        public void TestLabelsFit()
        {
            // every button and tick box in every workspace shows its whole text (no "Generate best for tonig…", "Up tonight o…")
            var keys = new[] { "home", "sky", "planner", "observatory", "stacking", "processing", "planetary", "astrometry", "photometry", "spectro", "solarsystem", "radio", "databases", "scripting", "settings" };
            var cut = new List<string>();
            foreach (var key in keys)
            {
                var view = OnUi(() => MainForm.Instance.GetView<Control>(key));
                OnUi(() =>
                {
                    foreach (var b in All(view).OfType<FlatButton>().Where(b => !string.IsNullOrEmpty(b.Text) && b.Dock == DockStyle.None))
                    {
                        int tw = TextRenderer.MeasureText(b.Text, b.Font, System.Drawing.Size.Empty, TextFormatFlags.NoPrefix).Width;
                        int room = string.IsNullOrEmpty(b.Icon) ? b.Width : b.Width - (8 + TextRenderer.MeasureText(b.Icon, Theme.IconFont).Width + 2) - 4;
                        if (tw > room) cut.Add(key + ": button \"" + b.Text + "\" (" + tw + " > " + room + " px)");
                    }
                    foreach (var c in All(view).OfType<CheckBox>().Where(c => !string.IsNullOrEmpty(c.Text) && c.Appearance == Appearance.Normal && c.Dock == DockStyle.None))
                    {
                        int box = (int)Math.Round(13 * c.DeviceDpi / 96.0);
                        int tw = TextRenderer.MeasureText(c.Text, c.Font, System.Drawing.Size.Empty, TextFormatFlags.NoPrefix).Width;
                        if (tw > c.Width - box - 5) cut.Add(key + ": tick box \"" + c.Text + "\" (" + tw + " > " + (c.Width - box - 5) + " px)");
                    }
                });
            }
            CheckTrue("No button or tick box cuts its text off", cut.Count == 0, string.Join("; ", cut));
            var planner = View<PlannerView>("planner");
            CheckTrue("Planner: Generate best for tonight… and Up tonight only are found", OnUi(() => Button(planner, "Generate best for tonight…") != null && Find<CheckBox>(planner, c => c.Text == "Up tonight only") != null));
        }

        [TestMethod]
        public void TestGridSorting()
        {
            double v;
            CheckTrue("Numbers, with or without units, have values", Theme.SortValue("9.2", out v) && v == 9.2 && Theme.SortValue("14.0", out v) && v == 14
                && Theme.SortValue("35°", out v) && v == 35 && Theme.SortValue("4.2″", out v) && v == 4.2 && Theme.SortValue("12 km", out v) && v == 12 && Theme.SortValue("+0.5", out v) && v == 0.5);
            CheckTrue("Angles and times have values, signs included", Theme.SortValue("-04:25:32", out v) && Math.Abs(v + (4 + 25 / 60.0 + 32 / 3600.0)) < 1e-9
                && Theme.SortValue("+19:24:02", out v) && v > 19 && Theme.SortValue("21:26", out v) && Math.Abs(v - (21 + 26 / 60.0)) < 1e-9 && Theme.SortValue("−3.5", out v) && v == -3.5);
            CheckTrue("Names do not", !Theme.SortValue("(192) Nausikaa", out v) && !Theme.SortValue("78P/Gehrels", out v) && !Theme.SortValue("2026 AB", out v) && !Theme.SortValue("", out v));

            // a grid like Solar sys. → Minor bodies: magnitudes and declinations as text, one magnitude unknown
            var order = OnUi(() =>
            {
                using (var f = new Form())
                {
                    var g = new DataGridView { Dock = DockStyle.Fill, AllowUserToAddRows = false };
                    f.Controls.Add(g);
                    g.Columns.Add("Object", "Object"); g.Columns.Add("Mag", "Mag now"); g.Columns.Add("Dec", "Dec");
                    g.Rows.Add("(192) Nausikaa", "9.2", "+08:46:40");
                    g.Rows.Add("(2) Pallas", "8.5", "-19:16:33");
                    g.Rows.Add("(171) Ophelia", "14.0", "+19:24:02");
                    g.Rows.Add("(4) Vesta", "6.8", "-04:25:32");
                    g.Rows.Add("78P/Gehrels", "", "+13:58:50");
                    Theme.Apply(g);
                    f.Show();
                    Func<string> names = () => string.Join(", ", g.Rows.Cast<DataGridViewRow>().Select(r => ((string)r.Cells[0].Value).Split(' ').Last()));
                    g.Sort(g.Columns["Mag"], System.ComponentModel.ListSortDirection.Ascending);
                    string up = names();
                    g.Sort(g.Columns["Mag"], System.ComponentModel.ListSortDirection.Descending);
                    string down = names();
                    g.Sort(g.Columns["Dec"], System.ComponentModel.ListSortDirection.Ascending);
                    string dec = names();
                    g.Sort(g.Columns["Object"], System.ComponentModel.ListSortDirection.Ascending);
                    string text = names();
                    return new[] { up, down, dec, text };
                }
            });
            CheckTrue("Magnitudes sort by value, the unknown one last", order[0] == "Vesta, Pallas, Nausikaa, Ophelia, 78P/Gehrels", order[0]);
            CheckTrue("…and the other way round", order[1] == "78P/Gehrels, Ophelia, Nausikaa, Pallas, Vesta", order[1]);
            CheckTrue("Declinations sort by value, south first", order[2] == "Pallas, Vesta, Nausikaa, 78P/Gehrels, Ophelia", order[2]);
            CheckTrue("Names still sort as text", order[3] == "(171) Ophelia, (192) Nausikaa, (2) Pallas, (4) Vesta, 78P/Gehrels".Split(',').Select(n => n.Trim().Split(' ').Last()).Aggregate((a, b) => a + ", " + b), order[3]);
        }

        [TestMethod]
        public void TestTickBoxes()
        {
            // Settings → Data: a click on a dataset's tick box ticks it at once, without choosing another row first
            var view = View<SettingsView>("settings");
            OnUi(() => ((TabbedPanel)typeof(SettingsView).GetField("_tabs", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(view)).Select(2));
            Settle();
            var grid = OnUi(() => All(view).OfType<DataGridView>().First(g => g.Columns.Count > 0 && g.Columns[0] is DataGridViewCheckBoxColumn));
            Func<int, bool> ticked = row => OnUi(() => Equals(grid.Rows[row].Cells[0].Value, true));
            int r = OnUi(() => Enumerable.Range(0, grid.Rows.Count).First(i => !Equals(grid.Rows[i].Cells[0].Value, true)));
            Action click = () => OnUi(() =>
            {
                // what a click on the box does: the cell becomes current, its editing value flips and the grid is told
                grid.CurrentCell = grid.Rows[r].Cells[0];
                grid.BeginEdit(false);
                var cell = (DataGridViewCheckBoxCell)grid.CurrentCell;
                ((IDataGridViewEditingCell)cell).EditingCellFormattedValue = !Equals(cell.EditedFormattedValue, true);
                grid.NotifyCurrentCellDirty(true);
            });
            click();
            CheckTrue("The tick shows at once, with the same row still chosen", ticked(r) && OnUi(() => grid.CurrentCell.RowIndex == r),
                OnUi(() => string.Format("visible {0}, current {1}, edit {2}, dirty {3}, value {4}", grid.Visible, grid.CurrentCell == null ? "none" : grid.CurrentCell.RowIndex.ToString(), grid.IsCurrentCellInEditMode, grid.IsCurrentCellDirty, grid.Rows[r].Cells[0].Value)));
            click();
            CheckTrue("A second click takes it away again", !ticked(r));
        }

        [TestMethod]
        public void TestDerotationImages()
        {
            var view = View<PlanetaryView>("planetary");
            OnUi(() => ((TabbedPanel)typeof(PlanetaryView).GetField("_tabs", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(view)).Select(3)); // the Derotation tab, so its controls are shown
            Settle();
            var type = typeof(PlanetaryView).GetNestedType("DeroItem", BindingFlags.NonPublic);
            var items = Field<IList>(view, "_deroItems");
            Func<string> heading = () => OnUi(() => All(view).OfType<Label>().First(l => l.Text == "No images yet" || l.Text.EndsWith("image") || l.Text.Contains("images, in time order")).Text);
            Func<string> target = () => OnUi(() =>
            {
                var label = Find<Label>(view, l => l.Text == "Target time (UTC)");
                var row = (TableLayoutPanel)label.Parent;
                return row.GetControlFromPosition(1, row.GetRow(label)).Text;
            });
            var list = OnUi(() => All(view).OfType<ListBox>().First(l => l.SelectionMode == SelectionMode.MultiExtended && l.Parent == Button(view, "Add images…").Parent.Parent));
            OnUi(() =>
            {
                foreach (int min in new[] { 20, 0, 10 })
                {
                    var it = Activator.CreateInstance(type);
                    type.GetField("Path").SetValue(it, "jupiter_" + min + ".tif");
                    type.GetField("Image").SetValue(it, new AstroImage(32, 32, 1));
                    type.GetField("Time").SetValue(it, new DateTime(2026, 10, 24, 20, min, 0, DateTimeKind.Utc));
                    items.Add(it);
                }
                Field<Action>(view, "_deroRefresh")();
            });
            CheckTrue("Three images in time order, target time in the middle", heading() == "3 images, in time order" && target() == "2026-10-24 20:10:00", heading() + " / " + target());
            OnUi(() => Button(view, "Remove selected").PerformClick());
            CheckTrue("Remove selected without a selection says what to do", Toasted("Select the images to remove"));
            OnUi(() => { list.ClearSelected(); list.SetSelected(0, true); list.SetSelected(1, true); Button(view, "Remove selected").PerformClick(); });
            CheckTrue("The selected images are removed and the target time follows", items.Count == 1 && heading() == "1 image" && target() == "2026-10-24 20:20:00", heading() + " / " + target());
            OnUi(() => { list.SetSelected(0, true); list.Focus(); });
            OnUi(() => Call(list, "OnKeyDown", new KeyEventArgs(Keys.Delete)));
            CheckTrue("Delete removes the selected image as well", items.Count == 0 && heading() == "No images yet" && target() == "" && OnUi(() => list.Visible && list.Items.Count == 0));
            OnUi(() => { items.Add(Activator.CreateInstance(type)); type.GetField("Time").SetValue(items[0], DateTime.UtcNow); type.GetField("Image").SetValue(items[0], new AstroImage(8, 8, 1)); Field<Action>(view, "_deroRefresh")(); });
            OnUi(() => Button(view, "Clear list").PerformClick());
            CheckTrue("Clear list empties it", items.Count == 0 && heading() == "No images yet");
            OnUi(() => Button(view, "Derotate & stack to target time").PerformClick());
            CheckTrue("Derotating without images says so", Toasted("Add images first"));
        }

        [TestMethod]
        public void TestPhotometryFramesAndStars()
        {
            var view = View<PhotometryView>("photometry");
            var frames = Field<List<string>>(view, "_frames");
            var list = Field<ListBox>(view, "_frameList");
            var grid = Field<DataGridView>(view, "_targets");
            Func<string, FlatButton> removeBeside = sibling => OnUi(() => Button(Button(view, sibling).Parent, "Remove selected"));
            OnUi(() => { frames.Clear(); frames.AddRange(new[] { @"C:\obs\a.fits", @"C:\obs\b.fits", @"C:\obs\c.fits" }); Call(view, "RefreshFrames"); list.SetSelected(0, true); list.SetSelected(2, true); });
            OnUi(() => removeBeside("Add frames…").PerformClick());
            CheckTrue("Selected frames are removed, the others stay", frames.SequenceEqual(new[] { @"C:\obs\b.fits" }) && OnUi(() => list.Items.Count) == 1);
            OnUi(() => { list.SetSelected(0, true); Call(list, "OnKeyDown", new KeyEventArgs(Keys.Delete)); });
            CheckTrue("Delete removes the selected frame", frames.Count == 0);

            OnUi(() =>
            {
                grid.Rows.Clear();
                Call(view, "AddTargetRow", "SS Cyg", 325.68, 43.59, double.NaN, double.NaN, "Target");
                Call(view, "AddTargetRow", "Comp 1", 325.70, 43.60, 12.1, 0.6, "Comparison");
                grid.ClearSelection();
                grid.Rows[1].Cells[0].Selected = true;
            });
            OnUi(() => removeBeside("Add selected object").PerformClick());
            CheckTrue("The selected star is removed", OnUi(() => grid.Rows.Cast<DataGridViewRow>().Where(r => !r.IsNewRow).Select(r => (string)r.Cells[0].Value).SequenceEqual(new[] { "SS Cyg" })));
            OnUi(() => grid.Rows.Clear());
        }

        [TestMethod]
        public void TestAddedMinorBodies()
        {
            var view = View<SolarSystemView>("solarsystem");
            var grid = Field<DataGridView>(view, "_mbGrid");
            var store = MinorBodyStore.Instance;
            var catalogue = store.All.First(m => !store.IsAdded(m));
            var mb = new MinorBody { Designation = "2026 ZZ9", Name = "2026 ZZ9 (test)", Elements = catalogue.Elements };
            try
            {
                OnUi(() =>
                {
                    Call(view, "AddBody", mb);
                    grid.Rows.Clear();
                    grid.Rows[grid.Rows.Add(catalogue.DisplayName)].Tag = catalogue;
                    grid.Rows[grid.Rows.Add(mb.DisplayName)].Tag = mb;
                    grid.ClearSelection();
                    grid.Rows[0].Cells[0].Selected = true;
                });
                OnUi(() => Button(view, "Remove added").PerformClick());
                CheckTrue("Catalogue objects stay, and the message says why", Toasted("Only objects you added") && store.All.Contains(catalogue));
                OnUi(() => { grid.ClearSelection(); grid.Rows[1].Cells[0].Selected = true; Button(view, "Remove added").PerformClick(); });
                CheckTrue("An added object is removed from the store, the list and the ephemeris choices",
                    !store.All.Contains(mb) && OnUi(() => grid.Rows.Count == 1 && !Field<ComboBox>(view, "_ephBody").Items.Contains(mb)));
            }
            finally { store.Remove(mb); OnUi(() => grid.Rows.Clear()); }
        }
    }
}
