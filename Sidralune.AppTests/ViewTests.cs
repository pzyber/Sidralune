using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sidralune.Tests;
using Sidralune.UI;
using static Sidralune.AppTests.AppHost;
using static Sidralune.Tests.Checks;

namespace Sidralune.AppTests
{
    /// <summary>Every workspace opens without errors, in day and night mode and at the smallest window size, and the command palette.</summary>
    [TestClass]
    public class ViewTests : TestBase
    {
        [TestMethod]
        public void TestEveryViewOpens()
        {
            var views = AllViews;
            CheckTrue("Fifteen workspaces", views.Count == 15, views.Count.ToString());
            foreach (var v in views)
            {
                int mark = LogMark();
                OnUi(() => App.Navigate(v.Key));
                Settle(800);
                var errors = ErrorsSince(mark);
                bool shown = OnUi(() => v.Instance != null && v.Instance.Visible && v.Instance.Width > 300 && v.Instance.Controls.Count > 0);
                CheckTrue(v.Title + " opens", shown && errors.Count == 0, string.Join(" | ", errors));
            }
        }

        [TestMethod]
        public void TestNightModeAndSmallWindow()
        {
            var views = AllViews;
            var size = OnUi(() => App.Size);
            try
            {
                OnUi(() => { App.SetNightMode(true); App.Size = App.MinimumSize; });
                foreach (var v in views)
                {
                    int mark = LogMark();
                    OnUi(() => App.Navigate(v.Key));
                    Settle(500);
                    var errors = ErrorsSince(mark);
                    // the visible parts of the workspace stay inside the window (no control pushed out of reach)
                    var outside = OnUi(() =>
                    {
                        var view = v.Instance;
                        return All(view).Where(c => c.Visible && c is FlatButton && c.Width > 0 && IsShown(c))
                            .Where(c => { var r = view.RectangleToClient(c.RectangleToScreen(c.ClientRectangle)); return r.Right > view.ClientSize.Width + 2 || r.Bottom > view.ClientSize.Height + 2; })
                            .Where(c => !InScrollable(c, view)).Select(c => c.Text).ToList();
                    });
                    CheckTrue(v.Title + " at 1100 × 700 in night mode", errors.Count == 0 && outside.Count == 0, string.Join(" | ", errors.Concat(outside.Select(o => "outside the window: " + o))));
                }
            }
            finally { OnUi(() => { App.SetNightMode(false); App.Size = size; }); }
        }

        private static bool IsShown(Control c)
        {
            for (var p = c; p != null; p = p.Parent) if (!p.Visible) return false;
            return true;
        }

        /// <summary>Inside a container that scrolls (or a strip that wraps), so it can be reached.</summary>
        private static bool InScrollable(Control c, Control stop)
        {
            for (var p = c.Parent; p != null && p != stop; p = p.Parent)
            {
                var sc = p as ScrollableControl;
                if (sc != null && sc.AutoScroll) return true;
                var flow = p as FlowLayoutPanel;
                if (flow != null && flow.WrapContents && flow.AutoSize) return true;
            }
            return false;
        }

        [TestMethod]
        public void TestCommands()
        {
            foreach (var v in AllViews) OnUi(() => App.Navigate(v.Key));
            Settle(500);
            var cmds = OnUi(() => App.Commands.ToList());
            CheckTrue("A Go to command for every workspace", AllViews.All(v => cmds.Any(c => c.Title == "Go to " + v.Title)));
            var dupes = cmds.GroupBy(c => c.Category + "/" + c.Title).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            CheckTrue("Command titles are unique", dupes.Count == 0, string.Join(", ", dupes));
            CheckTrue("Every command has a title, a category and an action", cmds.All(c => !string.IsNullOrWhiteSpace(c.Title) && !string.IsNullOrWhiteSpace(c.Category) && c.Run != null));
            OnUi(() => cmds.First(c => c.Title == "Go to Sky").Run());
            CheckTrue("Running a Go to command opens the workspace", OnUi(() => AllViews.First(v => v.Key == "sky").Instance.Visible));
            OnUi(() => cmds.First(c => c.Title == "Time: +1 hour").Run());
            CheckTrue("Time commands", !AppState.IsLive);
            OnUi(() => cmds.First(c => c.Title == "Return to live time").Run());
            CheckTrue("Back to live", AppState.IsLive);
            OnUi(() =>
            {
                using (var palette = new CommandPalette(cmds))
                {
                    var box = Find<TextBox>(palette);
                    CheckTrue("Command palette builds with a search box", box != null);
                }
            });
        }
    }
}
