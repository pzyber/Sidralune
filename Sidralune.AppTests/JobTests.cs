using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sidralune.Imaging;
using Sidralune.Tests;
using Sidralune.UI;
using Sidralune.Views;
using static Sidralune.AppTests.AppHost;
using static Sidralune.Tests.Checks;

namespace Sidralune.AppTests
{
    /// <summary>Long jobs run one at a time where they share their input, and lookups keep only the newest result.</summary>
    [TestClass]
    public class JobTests : TestBase
    {
        private static AstroImage Stars(int seed) => Synthetic.Render(640, 480, Synthetic.RandomStars(250, 640, 480, seed), Transform2D.Identity, 3, 0.1, 0.01, seed);

        /// <summary>Runs check while slot holds a job that waits, then lets the job end.</summary>
        private static void WhileBusy(JobSlot slot, Action check)
        {
            var gate = new ManualResetEventSlim();
            OnUi(() => slot.Run("Test job", t => gate.Wait(30000)));
            try { check(); }
            finally { gate.Set(); }
            CheckTrue("The slot is free again afterwards", WaitFor(() => OnUi(() => !slot.Busy), 30000));
        }

        [TestMethod]
        public void TestJobSlot()
        {
            var button = OnUi(() => new FlatButton("Go", null));
            var slot = OnUi(() => new JobSlot().Guard(button));
            var gate = new ManualResetEventSlim();
            int done = 0;
            var first = OnUi(() => slot.Run("Slow job", t => gate.Wait(30000), () => done++));
            CheckTrue("A running job disables the controls it guards", first != null && OnUi(() => slot.Busy && !button.Enabled));
            var second = OnUi(() => slot.Run("Second job", t => { }, () => done += 10));
            CheckTrue("A second job is refused, with a message", second == null && WaitFor(() => Toasts().Any(x => x.StartsWith("Slow job is still running"))));
            CheckTrue("A disabled button ignores clicks", OnUi(() => { bool clicked = false; button.Click += (s, e) => clicked = true; button.PerformClick(); return !clicked; }));
            gate.Set();
            CheckTrue("When it ends the controls come back and only its result is used", WaitFor(() => OnUi(() => !slot.Busy && button.Enabled) && done == 1));

            OnUi(() => slot.Run("Failing job", t => { throw new InvalidOperationException("boom"); }, null, ex => done += 100));
            CheckTrue("A failed job frees the slot and reports its error", WaitFor(() => OnUi(() => !slot.Busy && button.Enabled) && done == 101));

            var late = new ManualResetEventSlim();
            var results = new List<string>();
            OnUi(() => slot.Replace("Lookup A", t => { late.Wait(30000); throw new InvalidOperationException("late failure"); }, () => results.Add("A"), ex => results.Add("A failed")));
            OnUi(() => slot.Replace("Lookup B", t => { }, () => results.Add("B")));
            CheckTrue("The newest lookup's result is shown", WaitFor(() => OnUi(() => results.Contains("B"))));
            late.Set();
            Settle(700);
            CheckTrue("A replaced lookup that ends later is dropped, error and all", OnUi(() => results.SequenceEqual(new[] { "B" }) && !slot.Busy), string.Join(", ", results));

            var stop = new ManualResetEventSlim();
            OnUi(() => slot.Run("Cancelled job", t => { stop.Wait(30000); }, () => done += 1000));
            OnUi(() => slot.Cancel());
            stop.Set();
            CheckTrue("A cancelled job frees the slot without using its result", WaitFor(() => OnUi(() => !slot.Busy && button.Enabled)) && done == 101);
        }

        [TestMethod]
        public void TestPlateSolvingOneAtATime()
        {
            var view = View<AstrometryView>("astrometry");
            var job = Field<JobSlot>(view, "_solveJob");
            OnUi(() => view.SolveImage(Stars(3))); // a made-up field: the blind solve keeps searching until cancelled
            var img = OnUi(() => Field<AstroImage>(view, "_img"));
            try
            {
                CheckTrue("Solving", OnUi(() => job.Busy));
                var locked = new[] { "Open image…", "Solve", "Write WCS to FITS…", "Sync telescope to solution" };
                CheckTrue("Open, Solve, Write WCS and Sync wait for the solve", OnUi(() => locked.All(t => !Button(view, t).Enabled)));
                OnUi(() => Button(view, "Solve").PerformClick());
                OnUi(() => view.SolveImage(Stars(4)));
                CheckTrue("An image sent from another workspace waits too", OnUi(() => Field<AstroImage>(view, "_img") == img));
                int solving; lock (Tasks.Running) solving = Tasks.Running.Count(t => t.Name == "Plate solving" && !t.Done);
                CheckTrue("Only one plate solve runs", solving == 1, solving + " running");
            }
            finally { OnUi(() => job.Cancel()); }
            CheckTrue("Cancelling frees the buttons", WaitFor(() => OnUi(() => !job.Busy && Button(view, "Solve").Enabled), 60000));
            CheckTrue("A cancelled solve leaves no solution on the image", OnUi(() => Field<object>(view, "_solve") == null && Field<ImageViewer>(view, "_viewer").Wcs == null));
        }

        [TestMethod]
        public void TestProcessingEditsOneAtATime()
        {
            var view = View<ProcessingView>("processing");
            var job = Field<JobSlot>(view, "_job");
            OnUi(() => view.OpenImage(Stars(5), "first"));
            var gate = new ManualResetEventSlim();
            OnUi(() => Call(view, "Apply", "Slow edit", new Action<AstroImage>(i => gate.Wait(30000))));
            Func<List<string>> history = () => OnUi(() => Field<List<Tuple<string, AstroImage>>>(view, "_undo").Select(u => u.Item1).ToList());
            try
            {
                CheckTrue("The tools and Open, Undo and Redo wait for the edit", OnUi(() => job.Busy && !Field<Panel>(view, "_tools").Enabled && !Button(view, "Open").Enabled));
                OnUi(() => Call(view, "Apply", "Second edit", new Action<AstroImage>(i => { })));
                OnUi(() => Call(view, "Undo"));
                CheckTrue("A second edit and Undo are refused", history().SequenceEqual(new[] { "Open first" }), string.Join(", ", history()));
                OnUi(() => view.OpenImage(Stars(6), "second"));
            }
            finally { gate.Set(); }
            CheckTrue("The edit ends", WaitFor(() => OnUi(() => !job.Busy), 30000));
            Settle(300);
            CheckTrue("An image sent from elsewhere replaces the document and drops the edit made for the old one", history().SequenceEqual(new[] { "Open second" }), string.Join(", ", history()));
            CheckTrue("The tools are back", OnUi(() => Field<Panel>(view, "_tools").Enabled && Button(view, "Open").Enabled));
        }

        [TestMethod]
        public void TestOtherWorkspacesWaitForTheirJobs()
        {
            var stacking = View<StackingView>("stacking");
            var engine = Field<Imaging.Stacking.StackingEngine>(stacking, "_engine");
            WhileBusy(Field<JobSlot>(stacking, "_job"), () =>
            {
                CheckTrue("Stacking: frame buttons and adjustments wait", OnUi(() => !Button(stacking, "Stack checked frames").Enabled && !Button(stacking, "Light frames…").Enabled));
                CheckTrue("Stacking: the settings stay editable (a run has its own copy)", OnUi(() => Field<PropertyGrid>(stacking, "_props").Enabled));
                CheckTrue("Stacking: the frame list stays visible but its check boxes wait", OnUi(() => Field<DataGridView>(stacking, "_grid").ReadOnly));
                int before = engine.Frames.Count;
                OnUi(() => Call(stacking, "AddPaths", new[] { System.IO.Path.Combine(Root, "dropped.fits") }, (Imaging.Stacking.FrameKind?)null));
                CheckTrue("Stacking: dropped files wait", engine.Frames.Count == before);
            });
            CheckTrue("Stacking: between runs the engine uses the settings shown", OnUi(() => engine.Settings == Field<Imaging.Stacking.StackingSettings>(stacking, "_settings")));
            CheckTrue("Stacking: check boxes editable again, other columns not", OnUi(() => { var g = Field<DataGridView>(stacking, "_grid"); return !g.ReadOnly && !g.Columns[0].ReadOnly && g.Columns.Cast<DataGridViewColumn>().Skip(1).All(c => c.ReadOnly); }));

            var planetary = View<PlanetaryView>("planetary");
            // each tab has an Open source… button; tell them apart by a button beside it
            Func<string, bool> openBeside = sibling => OnUi(() => Button(Button(planetary, sibling).Parent, "Open source…").Enabled);
            Func<string, bool> enabled = text => OnUi(() => Button(planetary, text).Enabled);
            var ppSource = Field<object>(planetary, "_ppSource");
            WhileBusy(Field<JobSlot>(planetary, "_ppJob"), () =>
            {
                CheckTrue("Planetary: pre-processing locks Open source and Process in Pre-process", !openBeside("Process") && !enabled("Process"));
                CheckTrue("Planetary: Lucky stacking stays free during pre-processing", openBeside("Analyse & stack") && enabled("Analyse & stack"));
                CheckTrue("Planetary: Pre-process options stay editable (the run has its own copy)", OnUi(() => All(planetary).OfType<PropertyGrid>().First(g => ((GridAdapter)g.SelectedObject).Target is Imaging.Planetary.PippOptions).Enabled));
                OnUi(() => planetary.OpenVideo(System.IO.Path.Combine(Root, "no such video.ser")));
                CheckTrue("Planetary: a video sent from the Observatory waits (no attempt to open it)", Field<object>(planetary, "_ppSource") == ppSource);
            });
            WhileBusy(Field<JobSlot>(planetary, "_luckyJob"), () =>
            {
                CheckTrue("Planetary: lucky stacking locks Open source, Analyse & stack and Sharpen →", !openBeside("Analyse & stack") && !enabled("Analyse & stack") && !enabled("Sharpen →"));
                CheckTrue("Planetary: Pre-process stays free during lucky stacking", openBeside("Process") && enabled("Process"));
                CheckTrue("Planetary: Lucky stacking options stay editable (the run has its own copy)", OnUi(() => All(planetary).OfType<PropertyGrid>().First(g => ((GridAdapter)g.SelectedObject).Target is Imaging.Planetary.LuckyOptions).Enabled));
            });
            var sharp = Field<AstroImage>(planetary, "_sharpSource");
            WhileBusy(Field<JobSlot>(planetary, "_sharpJob"), () =>
            {
                CheckTrue("Planetary: Sharpen → in Lucky stacking waits for a deconvolution, the rest of the tab does not", !enabled("Sharpen →") && enabled("Analyse & stack"));
                OnUi(() => Call(planetary, "SetSharpenImage", Stars(7)));
                CheckTrue("Planetary: deconvolution keeps its image", Field<AstroImage>(planetary, "_sharpSource") == sharp);
            });
            CheckTrue("Planetary: Sharpen → is back afterwards", enabled("Sharpen →"));

            var photometry = View<PhotometryView>("photometry");
            WhileBusy(Field<JobSlot>(photometry, "_measureJob"), () =>
            {
                CheckTrue("Photometry: frames, stars and Measure wait for a measurement", OnUi(() => !Button(photometry, "Measure all frames").Enabled && !Button(photometry, "Add frames…").Enabled
                    && !Field<DataGridView>(photometry, "_targets").Enabled));
                CheckTrue("Photometry: the aperture settings stay editable (a measurement has its own copy)", OnUi(() => All(photometry).OfType<PropertyGrid>().First(g => ((GridAdapter)g.SelectedObject).Target is Science.Photometry.ApertureSettings).Enabled));
            });

            var settings = View<SettingsView>("settings");
            CheckTrue("Startup loading has finished", WaitFor(() => OnUi(() => !Tasks.Data.Busy), 60000));
            WhileBusy(Tasks.Data, () =>
                CheckTrue("Settings: downloads and reloads wait for catalogue loading", OnUi(() => !Button(settings, "Download selected").Enabled && !Button(settings, "Reload catalogs").Enabled)));
            CheckTrue("Settings: buttons back", OnUi(() => Button(settings, "Download selected").Enabled && Button(settings, "Reload catalogs").Enabled));
        }
    }
}
