using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sidralune.Science.Astrometry;
using Sidralune.Tests;
using Sidralune.UI;
using Sidralune.Views;
using static Sidralune.AppTests.AppHost;
using static Sidralune.Tests.Checks;

namespace Sidralune.AppTests
{
    /// <summary>The Astrometry workspace: which labels a solved image shows.</summary>
    [TestClass]
    public class AstrometryViewTests : TestBase
    {
        [TestMethod]
        public void TestAsteroidLabelOption()
        {
            var view = View<AstrometryView>("astrometry");
            var type = typeof(AstrometryView).GetNestedType("Annotation", BindingFlags.NonPublic);
            Func<string, double, bool, object> label = (name, mag, minor) =>
            {
                var a = Activator.CreateInstance(type, true);
                type.GetField("Name").SetValue(a, name);
                type.GetField("Mag").SetValue(a, mag);
                type.GetField("MinorBody").SetValue(a, minor);
                type.GetField("Color").SetValue(a, new Func<Palette, Color>(p => p.Asteroid));
                return a;
            };
            var labels = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(type));
            labels.Add(label("M 42", double.NaN, false));
            labels.Add(label("(1) Ceres (8.9)", 8.9, true));
            labels.Add(label("(9999) Faintling (19.5)", 19.5, true));
            Func<List<string>> shown = () => OnUi(() => ((IEnumerable)typeof(AstrometryView).GetProperty("ShownAnnotations", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(view))
                .Cast<object>().Select(a => (string)type.GetField("Name").GetValue(a)).ToList());
            Func<string> text = () => OnUi(() => Field<TextBox>(view, "_result").Text);
            var combo = OnUi(() => Find<ComboBox>(view, c => c.Items.Contains("Bright enough to show")));
            CheckTrue("Form labels show an & as text, not as a shortcut marker", OnUi(() => { var l = Find<Label>(view, x => x.Text == "Minor body labels"); return l != null && !l.UseMnemonic; }));
            string before = OnUi(() => AppState.Settings.MinorBodyLabels);
            try
            {
                OnUi(() =>
                {
                    typeof(AstrometryView).GetField("_annotations", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(view, labels);
                    typeof(AstrometryView).GetField("_depthInfo", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(view, new ImageDepth.Result { Limit = 15.0, Calibrators = 40, Stars = 2500 });
                    typeof(AstrometryView).GetField("_solve", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(view, new SolveResult { Success = true, Message = "Solved", Wcs = Wcs.Create(83.8, -5.4, 6, 0, 640, 480), Elapsed = TimeSpan.FromSeconds(22.6), ImageStars = 1000 });
                    typeof(AstrometryView).GetField("_steps", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(view, new[] { 7.1, 13.5, 0.4, 1.6 }.Select(TimeSpan.FromSeconds).ToArray());
                    combo.SelectedIndex = 1;
                });
                CheckTrue("Bright enough to show: objects and the asteroids down to the image's depth", shown().SequenceEqual(new[] { "M 42", "(1) Ceres (8.9)" }), string.Join(", ", shown()));
                CheckTrue("The object list and the depth follow", text().Contains("Ceres") && !text().Contains("Faintling") && text().Contains(string.Format("about magnitude {0:0.0}", 15.0)), text());
                CheckTrue("The time taken is split into its steps", text().Contains(string.Format("Elapsed {0:0.0} s (finding stars {1:0.0} s, solving {2:0.0} s, labels {3:0.0} s, image depth {4:0.0} s), 1000 stars used for solving", 22.6, 7.1, 13.5, 0.4, 1.6)), text());
                CheckTrue("The choice is kept in the settings", OnUi(() => AppState.Settings.MinorBodyLabels) == "Visible");
                OnUi(() => combo.SelectedIndex = 2);
                CheckTrue("None: no asteroids or comets, other objects stay", shown().SequenceEqual(new[] { "M 42" }) && !text().Contains("Ceres"));
                OnUi(() => combo.SelectedIndex = 0);
                CheckTrue("All in the field", shown().Count == 3 && text().Contains("Faintling"));
                OnUi(() => { typeof(AstrometryView).GetField("_depthInfo", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(view, new ImageDepth.Result { Stars = 2500 }); combo.SelectedIndex = 1; });
                CheckTrue("When the depth cannot be told, everything is labelled and the text says why", shown().Count == 3 && text().Contains("Image depth: unknown"), text());
            }
            finally
            {
                OnUi(() =>
                {
                    combo.SelectedIndex = Math.Max(0, Array.IndexOf(new[] { "All", "Visible", "None" }, before));
                    typeof(AstrometryView).GetField("_solve", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(view, null);
                    ((IList)typeof(AstrometryView).GetField("_annotations", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(view)).Clear();
                });
            }
        }
    }
}
