using System;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sidralune.Tests;
using Sidralune.UI;
using static Sidralune.AppTests.AppHost;
using static Sidralune.Tests.Checks;

namespace Sidralune.AppTests
{
    /// <summary>Plots keep drawing however far they are zoomed in.</summary>
    [TestClass]
    public class PlotTests : TestBase
    {
        // a plot like Photometry → Transit fit: data with error bars, a model line, plus a step, bars, a band and markers
        private static PlotControl TransitLike()
        {
            var plot = new PlotControl { Size = new Size(600, 400), Title = "Transit" };
            var data = plot.AddSeries("data", SeriesStyle.Scatter);
            var model = plot.AddSeries("model", SeriesStyle.Line);
            var step = plot.AddSeries("step", SeriesStyle.Step);
            var bars = plot.AddSeries("bars", SeriesStyle.Bars);
            var rnd = new Random(1);
            for (int i = 0; i < 2000; i++)
            {
                double h = -36 + 72 * i / 1999.0, f = 1 - (Math.Abs(h) < 1.5 ? 0.012 : 0);
                data.Add(h, f + (rnd.NextDouble() - 0.5) * 0.004, 0.002);
                model.Add(h, f);
                if (i % 50 == 0) { step.Add(h, f); bars.Add(h, f); }
            }
            plot.Bands.Add(new PlotBand { X0 = -1.5, X1 = 1.5, Color = Color.FromArgb(40, Color.Gray) });
            plot.Markers.Add(new PlotMarker { X = 0, Label = "mid-transit" });
            plot.Markers.Add(new PlotMarker { X = 1, Label = "baseline", Horizontal = true });
            return plot;
        }

        private static void Drag(PlotControl plot, Point a, Point b)
        {
            Call(plot, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, a.X, a.Y, 0));
            Call(plot, "OnMouseMove", new MouseEventArgs(MouseButtons.Left, 0, b.X, b.Y, 0));
            Call(plot, "OnMouseUp", new MouseEventArgs(MouseButtons.Left, 1, b.X, b.Y, 0));
        }

        [TestMethod]
        public void TestDeepZoom()
        {
            var result = OnUi(() =>
            {
                using (var plot = TransitLike())
                {
                    string error = null;
                    int zooms = 0;
                    try
                    {
                        plot.Render(600, 400).Dispose();
                        // zoom into a box of 8 × 8 pixels again and again: each zoom is about 60 times deeper
                        for (; zooms < 12; zooms++)
                        {
                            Drag(plot, new Point(330, 200), new Point(338, 208));
                            plot.Render(600, 400).Dispose();
                        }
                    }
                    catch (Exception ex) { error = zooms + " zooms: " + ex.GetType().Name + " " + ex.Message; }
                    double span = Field<double>(plot, "_x1") - Field<double>(plot, "_x0"), height = Field<double>(plot, "_y1") - Field<double>(plot, "_y0");
                    return Tuple.Create(error, span, height);
                }
            });
            CheckTrue("A plot zoomed in again and again still draws", result.Item1 == null, result.Item1);
            // the hours stop near 0 at 1e-10, the fluxes near 1 at a ten-billionth of 1
            CheckTrue("Zooming stops at a ten-billionth of the values", result.Item2 >= 0.99e-10 && result.Item2 < 1e-8 && result.Item3 >= 0.99e-10 && result.Item3 < 1e-8,
                result.Item2 + " h, " + result.Item3);

            var far = OnUi(() =>
            {
                using (var plot = TransitLike())
                {
                    try
                    {
                        // a range away from all data, given the wrong way round, and one far narrower than allowed
                        plot.ZoomTo(1e6 + 1, 1e6, 5, 6);
                        plot.Render(600, 400).Dispose();
                        plot.ZoomTo(0.5, 0.5, 1, 1);
                        plot.Render(600, 400).Dispose();
                        return Field<double>(plot, "_x1") > Field<double>(plot, "_x0") ? null : "empty range";
                    }
                    catch (Exception ex) { return ex.GetType().Name + " " + ex.Message; }
                }
            });
            CheckTrue("Ranges away from the data, reversed or empty still draw", far == null, far);
        }
    }
}
