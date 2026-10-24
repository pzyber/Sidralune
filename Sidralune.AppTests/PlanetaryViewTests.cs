using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sidralune.Tests;
using Sidralune.UI;
using Sidralune.Views;
using static Sidralune.AppTests.AppHost;
using static Sidralune.Tests.Checks;

namespace Sidralune.AppTests
{
    /// <summary>The Planetary workspace.</summary>
    [TestClass]
    public class PlanetaryViewTests : TestBase
    {
        [TestMethod]
        public void TestFramesShownAsRecorded()
        {
            var view = View<PlanetaryView>("planetary");
            var viewers = OnUi(() => All(view).OfType<ImageViewer>().Select(v => v.Stretch).ToList());
            // an automatic stretch blows the black sky around a planet up into noise
            CheckTrue("Every planetary tab shows images without a display stretch", viewers.Count >= 5 && viewers.All(s => s == DisplayStretch.None), string.Join(", ", viewers));
        }
    }
}
