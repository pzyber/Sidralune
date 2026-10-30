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

        /// <summary>A slot's Stop button: greyed out unless a job it can stop runs; then it cancels that job and says so.</summary>
        private static void CheckStop(string what, JobSlot slot, string stoppable, Func<FlatButton> stop)
        {
            CheckTrue(what + ": Stop is greyed out with nothing running", OnUi(() => !stop().Enabled));
            var gate = new ManualResetEventSlim();
            var job = OnUi(() => slot.Run(stoppable, t => gate.Wait(30000)));
            try
            {
                CheckTrue(what + ": Stop is available while " + stoppable + " runs", OnUi(() => stop().Enabled));
                OnUi(() => stop().PerformClick());
                CheckTrue(what + ": Stop cancels it and says so", job.Cts.IsCancellationRequested && WaitFor(() => Toasts().Contains(stoppable + " stopped")));
            }
            finally { gate.Set(); }
            CheckTrue(what + ": Stop is greyed out again", WaitFor(() => OnUi(() => !slot.Busy && !stop().Enabled)));
        }

        [TestMethod]
        public void TestStopButtons()
        {
            var stacking = View<StackingView>("stacking");
            CheckStop("Deep sky", Field<JobSlot>(stacking, "_job"), "Stacking", () => Button(stacking, "Stop"));
            var planetary = View<PlanetaryView>("planetary");
            Func<string, Func<FlatButton>> stopBeside = b => () => All(Button(planetary, b).Parent).OfType<FlatButton>().First(x => x.Text == "Stop");
            CheckStop("Pre-process", Field<JobSlot>(planetary, "_ppJob"), "Pre-processing", stopBeside("Process"));
            CheckStop("Lucky stacking", Field<JobSlot>(planetary, "_luckyJob"), "Lucky stacking", stopBeside("Analyse & stack"));
            CheckStop("Sharpen & deconvolve", Field<JobSlot>(planetary, "_sharpJob"), "Deconvolution", stopBeside("Richardson-Lucy deconvolution (TV)"));
            CheckStop("Sharpen & deconvolve channel alignment", Field<JobSlot>(planetary, "_sharpJob"), "Aligning colour channels", stopBeside("Richardson-Lucy deconvolution (TV)"));
            CheckTrue("Sharpen & deconvolve: Stop sits under Align RGB channels", OnUi(() =>
            {
                var panel = Button(planetary, "Align RGB channels (ADC)").Parent;
                return panel.Controls.IndexOf(stopBeside("Align RGB channels (ADC)")()) == panel.Controls.IndexOf(Button(planetary, "Align RGB channels (ADC)")) + 1;
            }));
            CheckStop("Derotation", Field<JobSlot>(planetary, "_deroJob"), "Derotating", stopBeside("Derotate & stack to target time"));
            CheckStop("Solar & lunar", Field<JobSlot>(planetary, "_solarJob"), "Stitching mosaic", stopBeside("Stitch mosaic tiles…"));
            CheckStop("Solar & lunar tools", Field<JobSlot>(planetary, "_solarJob"), "Flattening limb darkening", stopBeside("Stitch mosaic tiles…"));
            var lucky = Field<JobSlot>(planetary, "_luckyJob");
            WhileBusy(lucky, () => CheckTrue("Opening a sequence cannot be stopped part way, so Stop stays greyed out", OnUi(() => !stopBeside("Analyse & stack")().Enabled)));
            var radio = View<RadioView>("radio");
            CheckStop("Radio", Field<JobSlot>(radio, "_iqJob"), "Processing IQ recording", () => Button(radio, "Stop"));
            CheckTrue("Radio: Stop sits under Analyse line", OnUi(() =>
            {
                var analyse = Button(radio, "Analyse line (selected range = line window)");
                var grid = (TableLayoutPanel)analyse.Parent;
                return grid.GetRow(Button(radio, "Stop")) == grid.GetRow(analyse) + 1;
            }));
            var processing = View<ProcessingView>("processing");
            CheckStop("Process plate solving", Field<JobSlot>(processing, "_job"), "Plate solving", () => Button(processing, "Stop"));
            CheckStop("Process deconvolution", Field<JobSlot>(processing, "_job"), "Deconvolution", () => Button(processing, "Stop"));
            CheckStop("Process channel alignment", Field<JobSlot>(processing, "_job"), "Align channels", () => Button(processing, "Stop"));
            CheckStop("Process NLM denoise", Field<JobSlot>(processing, "_job"), "NLM denoise", () => Button(processing, "Stop"));
            CheckStop("Process star removal", Field<JobSlot>(processing, "_job"), "Star removal", () => Button(processing, "Stop"));
            CheckStop("Process background extraction", Field<JobSlot>(processing, "_job"), "Background extraction", () => Button(processing, "Stop"));
            CheckStop("Process levels", Field<JobSlot>(processing, "_job"), "Levels", () => Button(processing, "Stop"));
            var photometry = View<PhotometryView>("photometry");
            Func<Control, string, Func<FlatButton>> stopNextTo = (root, b) => () => All(Button(root, b).Parent).OfType<FlatButton>().First(x => x.Text == "Stop");
            CheckStop("Photometry period search", Field<JobSlot>(photometry, "_analysisJob"), "Period search", stopNextTo(photometry, "Find period"));
            CheckStop("Photometry transit MCMC", Field<JobSlot>(photometry, "_transitJob"), "Transit MCMC", stopNextTo(photometry, "Posterior (MCMC)"));
            CheckStop("Photometry transit predictions", Field<JobSlot>(photometry, "_predictJob"), "Exoplanet transit predictions", stopNextTo(photometry, "Predict transits"));
            WhileBusy(Field<JobSlot>(photometry, "_transitJob"), () => CheckTrue("The quick transit fit has no Stop", OnUi(() => !stopNextTo(photometry, "Posterior (MCMC)")().Enabled)));
            var solar = View<SolarSystemView>("solarsystem");
            CheckStop("Satellite passes", Field<JobSlot>(solar, "_passJob"), "Satellite passes", stopNextTo(solar, "Predict passes"));
            CheckStop("Asteroid occultations", Field<JobSlot>(solar, "_occJob"), "Occultation search", stopNextTo(solar, "Asteroid occultations"));
            CheckStop("Lunar occultations", Field<JobSlot>(solar, "_occJob"), "Lunar occultations", stopNextTo(solar, "Asteroid occultations"));
            CheckStop("Minor body positions", Field<JobSlot>(solar, "_positionsJob"), "Minor body positions", stopNextTo(solar, "List"));
            CheckStop("Ephemeris", Field<JobSlot>(solar, "_ephemJob"), "Ephemeris", stopNextTo(solar, "Compute"));
            var planner = View<PlannerView>("planner");
            CheckStop("Planner", Field<JobSlot>(planner, "_generateJob"), "Generating list", () => Button(planner, "Stop"));
            WhileBusy(Field<JobSlot>(planner, "_generateJob"), () =>
                CheckTrue("Planner: Generate waits for the list being generated", OnUi(() => !Button(planner, "Generate best for tonight…").Enabled)));
            CheckTrue("Planner: Generate is back afterwards", OnUi(() => Button(planner, "Generate best for tonight…").Enabled));
            var settings = View<SettingsView>("settings");
            CheckTrue("Startup loading has finished", WaitFor(() => OnUi(() => !Tasks.Data.Busy), 60000));
            CheckStop("Settings downloads", Tasks.Data, "Downloading data", () => Button(settings, "Stop"));

            // a button two jobs lock waits for both: Stacking → in Pre-process waits for pre-processing and for lucky stacking
            var gate = new ManualResetEventSlim();
            var gate2 = new ManualResetEventSlim();
            var pp = Field<JobSlot>(planetary, "_ppJob");
            OnUi(() => lucky.Run("Test job", t => gate.Wait(30000)));
            OnUi(() => pp.Run("Test job", t => gate2.Wait(30000)));
            gate2.Set();
            CheckTrue("A button two jobs lock stays locked while one of them still runs", WaitFor(() => OnUi(() => !pp.Busy)) && OnUi(() => !Button(planetary, "Stacking →").Enabled));
            gate.Set();
            CheckTrue("It comes back when both have ended", WaitFor(() => OnUi(() => !lucky.Busy && Button(planetary, "Stacking →").Enabled)));
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
            int started = 0;
            OnUi(() => slot.Replace("Lookup A", t => { late.Wait(30000); throw new InvalidOperationException("late failure"); }, () => results.Add("A"), ex => results.Add("A failed")));
            OnUi(() => slot.Replace("Lookup B", t => Interlocked.Increment(ref started), () => results.Add("B")));
            OnUi(() => slot.Replace("Lookup C", t => Interlocked.Increment(ref started), () => results.Add("C")));
            Settle(500);
            CheckTrue("Replacing a job that is slow to stop starts nothing else meanwhile", started == 0 && OnUi(() => slot.Busy) && results.Count == 0, started + " started");
            late.Set();
            CheckTrue("The newest request runs once the replaced job has ended", WaitFor(() => OnUi(() => results.Contains("C"))));
            Settle(700);
            CheckTrue("The replaced job's error and the request in between are dropped", OnUi(() => results.SequenceEqual(new[] { "C" }) && !slot.Busy) && started == 1, string.Join(", ", results) + " / " + started + " started");
            var hold = new ManualResetEventSlim();
            OnUi(() => slot.Replace("Lookup D", t => hold.Wait(30000), () => results.Add("D")));
            OnUi(() => slot.Replace("Lookup E", t => { }, () => results.Add("E")));
            OnUi(() => slot.Cancel());
            hold.Set();
            Settle(700);
            CheckTrue("Cancel also drops the request waiting to replace the job", OnUi(() => !slot.Busy) && !results.Contains("D") && !results.Contains("E"), string.Join(", ", results));

            // a stopped job's calculations end: its stop request reaches the parallel loops it runs
            var looping = new ManualResetEventSlim();
            OnUi(() => slot.Run("Endless calculation", t =>
            {
                looping.Set();
                while (true) System.Threading.Tasks.Parallel.For(0, 1000, Core.Util.StopRequest.Options, i => Thread.SpinWait(2000));
            }));
            CheckTrue("The calculation runs", looping.Wait(10000));
            OnUi(() => slot.Cancel());
            CheckTrue("Stopping it ends its loops, so the job ends and nothing keeps running", WaitFor(() => OnUi(() => !slot.Busy), 5000)
                && WaitFor(() => { lock (Tasks.Running) return !Tasks.Running.Any(x => x.Name == "Endless calculation"); }, 5000));

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
                CheckTrue("Stop is available while solving", OnUi(() => Button(view, "Stop").Enabled));
                OnUi(() => Button(view, "Solve").PerformClick());
                OnUi(() => view.SolveImage(Stars(4)));
                CheckTrue("An image sent from another workspace waits too", OnUi(() => Field<AstroImage>(view, "_img") == img));
                int solving; lock (Tasks.Running) solving = Tasks.Running.Count(t => t.Name == "Plate solving" && !t.Done);
                CheckTrue("Only one plate solve runs", solving == 1, solving + " running");
            }
            finally { OnUi(() => Button(view, "Stop").PerformClick()); }
            CheckTrue("Stop says so", WaitFor(() => Toasts().Contains("Plate solving stopped")));
            CheckTrue("Stop frees the buttons, and is greyed out again", WaitFor(() => OnUi(() => !job.Busy && Button(view, "Solve").Enabled && !Button(view, "Stop").Enabled), 60000));
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
                CheckTrue("The tool buttons and Open, Save as, Undo and Redo wait for the edit", OnUi(() => job.Busy && !Button(view, "Apply levels").Enabled && !Button(view, "Deconvolve").Enabled
                    && !Button(view, "Open").Enabled && !Button(view, "Save as").Enabled && !Button(view, "Solve").Enabled));
                CheckTrue("Every edit can be stopped", OnUi(() => Button(view, "Stop").Enabled));
                CheckTrue("The tool settings stay editable (each tool reads them when clicked)", OnUi(() => Field<Panel>(view, "_tools").Enabled
                    && All(Field<Panel>(view, "_tools")).Where(c => c is NumericUpDown || c is ComboBox || c is TextBox).All(c => c.Enabled)));
                OnUi(() => Call(view, "Apply", "Second edit", new Action<AstroImage>(i => { })));
                OnUi(() => Call(view, "Undo"));
                CheckTrue("A second edit and Undo are refused", history().SequenceEqual(new[] { "Open first" }), string.Join(", ", history()));
                OnUi(() => view.OpenImage(Stars(6), "second"));
            }
            finally { gate.Set(); }
            CheckTrue("The edit ends", WaitFor(() => OnUi(() => !job.Busy), 30000));
            Settle(300);
            CheckTrue("An image sent from elsewhere replaces the document and drops the edit made for the old one", history().SequenceEqual(new[] { "Open second" }), string.Join(", ", history()));
            CheckTrue("The tools are back", OnUi(() => Button(view, "Apply levels").Enabled && Button(view, "Open").Enabled && Button(view, "Save as").Enabled && Button(view, "Solve").Enabled));

            // Solve stays here and runs as this workspace's job; a solution is added to the image it was found for, as a history step
            OnUi(() => MainForm.Instance.Navigate("processing"));
            CheckTrue("Solve stays in Process and runs as its job", OnUi(() => { Button(view, "Solve").PerformClick(); return job.Name == "Plate solving" && view.Visible && !Button(view, "Open").Enabled; }));
            OnUi(() => job.Cancel()); // a made-up field: the blind solve would keep searching
            CheckTrue("A cancelled solve frees the buttons", WaitFor(() => OnUi(() => !job.Busy && Button(view, "Solve").Enabled), 60000));
            var found = new Science.Astrometry.SolveResult { Success = true, Message = "Solved", Wcs = Science.Astrometry.Wcs.Create(83.8, -5.4, 2, 0, 640, 480) };
            var sent = OnUi(() => Field<AstroImage>(view, "_img"));
            OnUi(() => Call(view, "AddSolution", sent, found));
            CheckTrue("A solution is added to the image it was found for", OnUi(() => Science.Astrometry.Wcs.FromHeader(Field<AstroImage>(view, "_img").Header, 640, 480) != null
                && Field<ImageViewer>(view, "_viewer").Wcs != null && history().Last() == "Plate solve"), string.Join(", ", history()));
            OnUi(() => Call(view, "Undo"));
            CheckTrue("Undo takes the solution away again", OnUi(() => Science.Astrometry.Wcs.FromHeader(Field<AstroImage>(view, "_img").Header, 640, 480) == null));
            OnUi(() => Call(view, "AddSolution", sent, found));
            CheckTrue("A solution for an image that has been replaced is not added", OnUi(() => Science.Astrometry.Wcs.FromHeader(Field<AstroImage>(view, "_img").Header, 640, 480) == null));
            OnUi(() => Call(view, "AddSolution", Field<AstroImage>(view, "_img"), new Science.Astrometry.SolveResult { Success = false, Message = "No match" }));
            CheckTrue("Without a solution a message points to Astrometry", WaitFor(() => Toasts().Any(t => t.StartsWith("No plate solution (No match)") && t.Contains("Astrometry"))));
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
                var measuring = Field<JobSlot>(photometry, "_measureJob").Current;
                OnUi(() => Button(photometry, "Stop").PerformClick());
                CheckTrue("Photometry: Stop cancels the measurement", measuring.Cts.IsCancellationRequested && WaitFor(() => Toasts().Contains("Measurement stopped")));
            });
            CheckTrue("Photometry: Stop is only available while measuring", OnUi(() => !Button(photometry, "Stop").Enabled && Button(photometry, "Measure all frames").Enabled));

            var solar = View<SolarSystemView>("solarsystem");
            foreach (var slot in new[] { Tuple.Create("_passJob", new[] { "Predict passes" }), Tuple.Create("_ephemJob", new[] { "Compute" }), Tuple.Create("_positionsJob", new[] { "List" }),
                Tuple.Create("_galileanJob", new[] { "Galilean moon events (7 days)" }), Tuple.Create("_occJob", new[] { "Asteroid occultations", "Lunar occultations" }) })
            {
                WhileBusy(Field<JobSlot>(solar, slot.Item1), () =>
                    CheckTrue("Solar system: " + string.Join(" and ", slot.Item2) + " wait for the calculation", OnUi(() => slot.Item2.All(b => !Button(solar, b).Enabled))));
                CheckTrue("Solar system: " + string.Join(" and ", slot.Item2) + " back afterwards", OnUi(() => slot.Item2.All(b => Button(solar, b).Enabled)));
            }
            var databases = View<DatabaseView>("databases");
            var lookups = new[] { "Resolve name (Sesame)", "AAVSO VSX", "SIMBAD cone (selection)", "Run query" };
            WhileBusy(Field<JobSlot>(databases, "_query"), () =>
                CheckTrue("Databases: the online lookups wait for the one running", OnUi(() => lookups.All(b => !Button(databases, b).Enabled))));
            CheckTrue("Databases: lookups back afterwards", OnUi(() => lookups.All(b => Button(databases, b).Enabled)));

            var settings = View<SettingsView>("settings");
            CheckTrue("Startup loading has finished", WaitFor(() => OnUi(() => !Tasks.Data.Busy), 60000));
            WhileBusy(Tasks.Data, () =>
                CheckTrue("Settings: downloads and reloads wait for catalogue loading", OnUi(() => !Button(settings, "Download selected").Enabled && !Button(settings, "Reload catalogs").Enabled)));
            CheckTrue("Settings: buttons back", OnUi(() => Button(settings, "Download selected").Enabled && Button(settings, "Reload catalogs").Enabled));
        }
    }
}
