using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sidralune.Imaging;
using Sidralune.Imaging.IO;
using Sidralune.Tests;
using Sidralune.UI;
using Sidralune.Views;
using static Sidralune.AppTests.AppHost;
using static Sidralune.Tests.Checks;

namespace Sidralune.AppTests
{
    /// <summary>Images and videos are opened in the background: the window keeps working, and the tab's buttons wait meanwhile.</summary>
    [TestClass]
    public class BackgroundLoadingTests : TestBase
    {
        private static string Folder()
        {
            var dir = Path.Combine(Root, "background");
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static string Video(string name)
        {
            string path = Path.Combine(Folder(), name);
            using (var ser = new SerWriter(path, 64, 48, 16, 1))
                for (int f = 0; f < 3; f++) ser.Write(new AstroImage(64, 48, 1), DateTime.UtcNow.AddSeconds(f));
            return path;
        }

        [TestMethod]
        public void TestPlanetaryOpensInTheBackground()
        {
            var view = View<PlanetaryView>("planetary");
            string video = Video("background.ser");
            var gate = new ManualResetEventSlim();
            var openPre = Field<Action<Func<IFrameSource>>>(view, "_openPreprocess");
            var pp = Field<JobSlot>(view, "_ppJob");
            OnUi(() => openPre(() => { gate.Wait(30000); return FrameSourceFactory.Open(video); }));
            CheckTrue("Pre-process: opening returns at once and runs as the tab's job", OnUi(() => pp.Busy && !Button(view, "Process").Enabled));
            gate.Set();
            CheckTrue("Pre-process: the video is ready when it has loaded", WaitFor(() => OnUi(() => !pp.Busy) && ((IFrameSource)Field<object>(view, "_ppSource"))?.Path == video, 15000));

            gate.Reset();
            var openLucky = Field<Action<Func<IFrameSource>, Action>>(view, "_openLucky");
            var lucky = Field<JobSlot>(view, "_luckyJob");
            bool done = false;
            OnUi(() => openLucky(() => { gate.Wait(30000); return FrameSourceFactory.Open(video); }, () => done = true));
            CheckTrue("Lucky stacking: opening returns at once and runs as the tab's job", OnUi(() => lucky.Busy && !Button(view, "Analyse & stack").Enabled && Button(view, "Process").Enabled));
            gate.Set();
            CheckTrue("Lucky stacking: the video is ready when it has loaded", WaitFor(() => done && ((IFrameSource)Field<object>(view, "_luckySource"))?.Path == video, 15000));
        }

        [TestMethod]
        public void TestProcessCombinesInTheBackground()
        {
            var view = View<ProcessingView>("processing");
            var job = Field<JobSlot>(view, "_job");
            var gate = new ManualResetEventSlim();
            OnUi(() => Call(view, "Compose", "RGB composite", new Func<AstroImage>(() => { gate.Wait(30000); return new AstroImage(40, 30, 3); })));
            CheckTrue("Combining channels (loading included) returns at once and runs as an edit", OnUi(() => job.Busy && !Field<Panel>(view, "_tools").Enabled));
            gate.Set();
            CheckTrue("The composite opens when it is ready", WaitFor(() => OnUi(() => !job.Busy && Field<List<Tuple<string, AstroImage>>>(view, "_undo").Select(u => u.Item1).SequenceEqual(new[] { "Open RGB composite" })), 15000));
        }

        [TestMethod]
        public void TestAstrometryOpensThroughTheSolveJob()
        {
            var view = View<AstrometryView>("astrometry");
            string path = Path.Combine(Folder(), "field.fits");
            FitsIO.Write(path, Synthetic.Render(320, 240, Synthetic.RandomStars(40, 320, 240, 9), Transform2D.Identity, 3, 0.1, 0.01, 9), -32);
            var job = Field<JobSlot>(view, "_solveJob");
            Action open = () => Call(view, "OpenFile", path, Field<TextBox>(view, "_hintRa"), Field<TextBox>(view, "_hintDec"), Field<NumericUpDown>(view, "_hintScale"));
            var before = OnUi(() => Field<AstroImage>(view, "_img"));
            var gate = new ManualResetEventSlim();
            OnUi(() => job.Run("Test job", t => gate.Wait(30000)));
            OnUi(open);
            CheckTrue("While a solve runs, an image waits", OnUi(() => Field<AstroImage>(view, "_img")) == before);
            gate.Set();
            CheckTrue("The solve job is free again", WaitFor(() => OnUi(() => !job.Busy), 15000));
            OnUi(open);
            CheckTrue("Then it loads in the background and is shown", WaitFor(() => OnUi(() => !job.Busy && Field<AstroImage>(view, "_img") != null && Field<AstroImage>(view, "_img").Width == 320), 15000));
        }
    }
}
