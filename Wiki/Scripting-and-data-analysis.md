The **Scripts** workspace lets you automate Sidralune and analyse data with C# scripts that have access to the whole program: catalogues, ephemerides, image processing, plate solving, photometry, devices and the user interface. It also has a **Data analysis** tab for quick plots and fits of tabular data without writing code.

## Contents

- [C# scripts](#c-scripts)
  - [Writing and running a script](#writing-and-running-a-script)
  - [The script API](#the-script-api)
  - [Namespaces](#namespaces)
  - [Helper methods](#helper-methods)
  - [Language level](#language-level)
  - [Examples](#examples)
  - [Saving scripts](#saving-scripts)
  - [Running scripts from the command line](#running-scripts-from-the-command-line)
- [Data analysis](#data-analysis)
- [Other automation](#other-automation)

## C# scripts

### Writing and running a script

The editor on the left holds the body of the script: the statements to run. Pick a starting point from the examples list on the toolbar, edit it, and press **F5** or click **Run (F5)**.

The script is compiled in memory with the C# compiler built into the .NET Framework, so no extra software is needed. Compilation takes a fraction of a second. If there are errors, they are listed in **Output** with line and column numbers, and the first error's line is highlighted in the editor.

While the script runs, the button changes to **Stop**; click it to cancel. Scripts run in the background, so the window stays responsive.

The right side has three tabs for results:

| Tab | Filled by |
|---|---|
| **Output** | `Print(...)`, compiler messages, run time, and any exception with its stack trace |
| **Plot** | `Plot(...)`, `PlotTitle(...)` |
| **Table** | `Table(...)` |

When a script finishes with only a few lines of output, the **Table** or **Plot** tab is brought forward automatically if the script filled it.

### The script API

These members can be used directly in a script:

| Member | Description |
|---|---|
| `Print(object)`, `Print(format, args…)` | Write a line to Output, for example `Print("{0} stars", n)` |
| `Progress(fraction, status)` | Show progress (0–1) in the status bar |
| `CheckCancel()`, `Token` | Stop promptly when the user clicks Stop: call `CheckCancel()` in long loops, or pass `Token` to methods that accept a cancellation token |
| `Sleep(seconds)` | Wait, cancellably |
| `Site` | The active observing site (`Observer`): latitude, longitude, elevation, time zone, SQM… |
| `Now` | The program's current time (`AstroTime`), live or simulated |
| `Selection` | The selected object (`SelectedObject`), or null |
| `Sky` | A `SkyContext` for the site and current time: `Sky.BodyPlace(Body.Mars)`, `Sky.IcrsToHorizontal(ra, dec)`… |
| `Catalogs` | The catalogue manager: `Catalogs.Find("M 31")`, `Catalogs.Search("Ring", 10)`, `Catalogs.DeepSkyList`, `Catalogs.ConstellationAt(ra, dec)` |
| `Devices` | The device manager: `Devices.Telescope`, `Devices.Camera`, `Devices.Focuser`, `Devices.FilterWheel`, `Devices.Dome`, `Devices.Guider`… |
| `ClearPlot()` | Clear the plot |
| `PlotTitle(title, xLabel, yLabel, invertY)` | Set the plot's title and axis labels; `invertY` for magnitudes |
| `Plot(name, xs, ys, scatter)` | Add a series from two lists of numbers, as a line or points |
| `Plot(name, f, x0, x1, n)` | Plot a function `f(x)` from `x0` to `x1` |
| `Table(columns, rows)` | Show a table: a list of column names and a list of rows (each a list of values) |
| `LoadImage(path)` | Load any supported image as an `AstroImage` |
| `SaveFits(path, image)` | Save an image as 32-bit FITS |
| `Files(folder, pattern)` | Sorted list of files in a folder, for example `Files(@"D:\Lights", "*.fits")` |
| `Show(image, name)` | Open an image in the Process workspace |

**API help** on the toolbar lists all of these with their exact signatures, and the namespaces available.

### Namespaces

The script is compiled with these namespaces already imported, so their classes can be used without prefixes:

| Area | Namespaces | Useful classes |
|---|---|---|
| .NET | `System`, `System.Collections.Generic`, `System.Linq`, `System.IO`, `System.Text`, `System.Drawing`, `System.Threading` | `List<T>`, `Math`, `File`, LINQ |
| Application | `Sidralune`, `Sidralune.UI`, `Sidralune.Views` | `AppState`, `MainForm`, `Theme`, the workspace views |
| Core | `Sidralune.Core`, `.Mathematics`, `.Time`, `.Coordinates`, `.Ephemeris`, `.Catalogs`, `.MinorBodies`, `.Satellites`, `.Util` | `AstroMath`, `Stats`, `LevenbergMarquardt`, `AstroTime`, `Transforms`, `Events`, `PlanetPhysics`, `MinorBodyStore`, `Sgp4`, `Csv` |
| Imaging | `Sidralune.Imaging`, `.IO`, `.Analysis`, `.Processing`, `.Stacking`, `.Registration`, `.Planetary` | `AstroImage`, `ImageFile`, `FitsIO`, `StarDetector`, `StretchOps`, `Deconvolution`, `StackingEngine`, `StarMatcher`, `LuckyStacker`, `Preprocessor` |
| Science | `Sidralune.Science.Astrometry`, `.Photometry`, `.Spectroscopy`, `.SolarSystem`, `.Radio`, `.Databases` | `PlateSolver`, `Wcs`, `AperturePhotometry`, `PeriodSearch`, `TransitModel`, `SpectroTools`, `GalileanMoons`, `PlanetMoons`, `OccultationPredictor`, `RadioMath`, `OnlineServices` |
| Devices | `Sidralune.Devices`, `.Automation` | `DeviceManager`, `CaptureService`, `Autofocus`, `Centering`, `PolarAlignment`, `Sequencer` |

To change the user interface from a script, run the code on the UI thread: `MainForm.Instance.Invoke((Action)(() => { … }))`.

### Helper methods

Everything above a line containing `//@members` is the body of the script; everything below it becomes extra members of the script class, so you can define helper methods and fields:

```csharp
for (int n = 1; n <= 10; n++) Print("{0}! = {1}", n, Factorial(n));
//@members
static long Factorial(int n) { return n <= 1 ? 1 : n * Factorial(n - 1); }
```

### Language level

Scripts are compiled by the C# compiler that ships with the .NET Framework, which supports C# 5. That means no string interpolation (`$"..."`), no `?.` operator, no expression-bodied members and no `nameof`. Use `string.Format` (or `Print` with a format) and ordinary null checks instead.

### Examples

The examples list contains:

| Example | Shows |
|---|---|
| **Hello sky: planets now** | Positions, magnitudes and sizes of the planets in a table |
| **Altitude curve of the selection tonight** | The selected object's and the Sun's altitude over 24 hours as a plot |
| **Batch: FWHM and star count of a folder** | Measuring every FITS file in a folder: star count, FWHM and eccentricity in a table and a plot, to spot frames with bad focus or tracking |
| **Simulated camera: capture and stretch** | Taking an exposure with the connected camera, stretching it and opening it in Process |
| **Lomb-Scargle of a synthetic variable** | Generating a time series and finding its period |
| **Helper methods (//@members)** | Defining helper methods |

A few more ideas:

```csharp
// When does each Messier object transit tonight?
var rows = new List<IList<object>>();
foreach (var d in Catalogs.DeepSkyList.Where(o => o.Messier != null))
{
    var rs = Events.FixedRiseSet(d.Ra, d.Dec, Site, Now);
    rows.Add(new object[] { d.Messier, d.Name, rs.Transit.HasValue ? Site.ToLocal(rs.Transit.Value.ToDateTimeUtc()).ToString("HH:mm") : "-" });
}
Table(new[] { "Object", "Name", "Transit" }, rows);
```

```csharp
// Plate solve every FITS file in a folder and print the centres
foreach (var f in Files(@"D:\Astro\2026-10-18\M 31\LIGHT", "*.fits"))
{
    CheckCancel();
    var img = LoadImage(f);
    var r = PlateSolver.Solve(img, new SolveOptions { Catalog = new LocalCatalogProvider() }, null, Token);
    Print("{0}: {1}", Path.GetFileName(f), r.Success ? r.Wcs.ToString() : r.Message);
}
```

```csharp
// Slew the telescope to the selected object (with a connected mount)
var o = Selection;
var app = Sky.IcrsToApparent(o.RaDeg * AstroMath.Deg2Rad, o.DecDeg * AstroMath.Deg2Rad);
Devices.Telescope.SlewToCoordinatesAsync(app.RaHours, app.DecDeg);
Print("Slewing to {0}", o.Name);
```

### Saving scripts

**Save…** (or Ctrl+S, which saves straight away once the script has a name) stores the script as a `.cs` file; **Open…** loads one. The default folder is `%LOCALAPPDATA%\Sidralune\Data\Scripts`.

### Running scripts from the command line

`Sidralune.exe --screenshot out.png --script myscript.cs` starts Sidralune, runs the script in the Scripts workspace and saves a screenshot when the wait time is over. See [Command-line options](Command-line-options).

## Data analysis

The **Data analysis** tab plots and fits tabular data without code.

1. Click **Open CSV / table…** or **Paste from clipboard** (for example cells copied from a spreadsheet). Comma, tab, semicolon and space separators are detected automatically, and a header row is used for column names if present.
2. Choose the **X**, **Y** and optional **Y error** columns.
3. Choose a **Fit**: None, Polynomial (of **Degree** 1–10), Linear (weighted by the errors), Power law y = a·x^b, or Exponential y = a·e^(bx).
4. Optionally tick **Invert Y** (for magnitudes), **log₁₀ X** or **log₁₀ Y**.
5. Click **Plot**.

The results panel shows the number of points, the mean, standard deviation, median and range of Y, the Pearson correlation coefficient, the fitted equation and the RMS of the residuals. The table of data is shown under the plot.

Right-click the plot to copy it, save it as PNG or export the data as CSV.

## Other automation

- The [Sequencer](Sequencer) automates imaging nights without code; its **Run script** instruction can start external programs.
- The [command palette](The-user-interface#command-palette) runs any command by name.
- Sequences are JSON files that scripts can generate.
