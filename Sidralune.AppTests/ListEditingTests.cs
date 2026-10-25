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
