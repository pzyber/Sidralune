# Contributing to Sidralune

Thank you for helping to improve Sidralune. Whether you report a bug, check a calculation against an almanac, write documentation or send code, it all helps. This guide explains how to do each of these so that your contribution can be used quickly.

The project lives at <https://github.com/pzyber/Sidralune>.

## Contents

- [Reporting a bug](#reporting-a-bug)
  - [Wrong positions, times or predictions](#wrong-positions-times-or-predictions)
  - [Problems with equipment](#problems-with-equipment)
- [Reporting a security issue](#reporting-a-security-issue)
- [Suggesting a feature](#suggesting-a-feature)
- [Asking a question](#asking-a-question)
- [Improving the documentation](#improving-the-documentation)
- [Contributing code](#contributing-code)
  - [Setting up](#setting-up)
  - [Repository layout](#repository-layout)
  - [Architecture in brief](#architecture-in-brief)
- [Project guidelines](#project-guidelines)
  - [Language and framework](#language-and-framework)
  - [No third-party dependencies](#no-third-party-dependencies)
  - [Scientific correctness](#scientific-correctness)
  - [Code style](#code-style)
  - [User interface](#user-interface)
- [Testing your changes](#testing-your-changes)
  - [Unit tests](#unit-tests)
  - [Checking the interface](#checking-the-interface)
  - [Testing with equipment](#testing-with-equipment)
- [Submitting a pull request](#submitting-a-pull-request)
- [License](#license)

## Reporting a bug

1. Search the [existing issues](https://github.com/pzyber/Sidralune/issues) first. If someone has already reported it, add your details there instead of opening a new issue.
2. Make sure you are using the latest release.
3. Open a new issue with a title that describes the problem, for example "Planner shows wrong rise time for circumpolar objects" rather than "Planner bug".
4. Include:
   - What you did, step by step.
   - What you expected and what happened instead.
   - Your Windows version and the Sidralune version (**Settings → About**).
   - Your observing site's approximate latitude and longitude if the problem involves the sky (round them if you prefer not to share your exact location).
   - The simulated time, if you were not using live time.
   - Any error message, and the relevant lines from the log. Click **Log** in the bottom-right corner, or open `%LOCALAPPDATA%\Sidralune\Logs`. Crash reports are saved there as `crash-<date>-<time>.txt`.
   - A sample file, if the problem is with a particular image, video or spectrum and you can share it.

### Wrong positions, times or predictions

Astronomical errors are the most valuable reports, and the easiest to fix when they come with a reference. Please give:

- The object, the date and time (with time zone or UTC), and your site.
- The value Sidralune shows and the value you expected.
- Where the expected value comes from, for example JPL Horizons, the Astronomical Almanac, an IMO calendar or a published paper.

Remember that small differences can be legitimate: topocentric versus geocentric positions, J2000 versus apparent coordinates, refraction, or whether the DE440s ephemeris is installed. Saying which you compared helps a lot.

### Problems with equipment

Name the device, the protocol (ASCOM, Alpaca, INDI, LX200 or PHD2) and the driver version. If you can, say whether the same thing happens with the simulated device, which tells us whether the problem is in Sidralune or in the driver.

> Never leave a mount slewing or a sequence running unattended while you test something you are not sure about.

## Reporting a security issue

Do not open a public issue for security problems. Report them privately through the repository's **Security** tab ("Report a vulnerability"), and include enough detail to reproduce the problem.

## Suggesting a feature

Open an issue and describe the problem you want to solve, not only the solution. "I want to plan a comet mosaic that follows the comet's motion" is easier to design for than "add a checkbox to the mosaic planner".

If the feature exists in another program, a link to its documentation is useful. Please keep in mind the project's guidelines: features must work without third-party libraries, and scientific features need a published method behind them.

## Asking a question

Check the [README](README.md) and the [wiki](https://github.com/pzyber/Sidralune/wiki/Home) first; the [Troubleshooting and FAQ](https://github.com/pzyber/Sidralune/wiki/Troubleshooting-and-FAQ) page covers common problems. If the answer is not there, open an issue with your question. A question that the documentation should have answered is also a documentation bug, so it is never wasted.

## Improving the documentation

The user manual is the `Wiki` folder, written in GitHub-flavoured Markdown and published as the [GitHub wiki](https://github.com/pzyber/Sidralune/wiki). Corrections, clearer explanations and new examples are welcome as pull requests. Please:

- Write for an amateur astronomer, not a programmer. Explain jargon the first time you use it.
- Use the names exactly as they appear in the program, in bold, with arrows for navigation: **Settings → Data**.
- Name each file after the page's title, with hyphens for spaces (`Sky-chart.md` becomes the page "Sky chart"). The wiki shows the file name as the page heading, so do not start a page with a `#` heading.
- Link between pages by page name, without `.md`: `[Sky chart](Sky-chart)` or `[Printing](Sky-chart#printing-finder-charts)`. Add new pages to `_Sidebar.md` and `Home.md`.
- Update the page when you change the behaviour it describes, in the same pull request.

## Contributing code

Bug fixes, new algorithms, device support, performance work and tests are all welcome. For anything larger than a small fix, open an issue first and describe what you plan to do. That avoids two people working on the same thing, or work that does not fit the project.

### Setting up

1. Install the .NET Framework 4.8.1 Developer Pack and Visual Studio 2015 or later (the Community edition is fine), or the Visual Studio Build Tools.
2. Fork the repository on GitHub and clone your fork:

   ```
   git clone https://github.com/<your-name>/Sidralune.git
   cd Sidralune
   ```

3. Create a branch named after your change:

   ```
   git checkout -b fix-planner-circumpolar
   ```

4. Build the solution in Visual Studio, or from a developer command prompt:

   ```
   msbuild Sidralune.sln /p:Configuration=Debug
   ```

5. Run the tests (see [Unit tests](#unit-tests)) to make sure everything passes before you start.

### Repository layout

| Path | Contents |
|---|---|
| `Sidralune/Program.cs` | Entry point, crash reporting, screenshot mode |
| `Sidralune/MainForm.cs` | Main window, navigation rail, command palette, status bar |
| `Sidralune/AppState.cs` | Settings, simulated time, current selection, background tasks |
| `Sidralune/Views` | One `UserControl` per workspace (`SkyView`, `PlannerView`, `StackingView`, …) |
| `Sidralune/UI` | Theme and palettes, custom controls, `ImageViewer`, `PlotControl`, `SkyChart` |
| `Sidralune/Scripting` | C# script host and example scripts |
| `Sidralune.Core/Math` | Vectors, statistics, FFT, linear algebra, optimisation (Levenberg-Marquardt, Nelder-Mead, MCMC) |
| `Sidralune.Core/Time` | Time scales, Julian dates, leap seconds, ΔT |
| `Sidralune.Core/Coordinates` | Precession, nutation, coordinate transforms, refraction |
| `Sidralune.Core/Ephemeris` | Analytic planets, Moon, JPL SPK reader, physical ephemerides, rise/set and events |
| `Sidralune.Core/Catalogs` | Built-in data, catalogue loaders, sky index, constellation boundaries |
| `Sidralune.Core/MinorBodies` | MPC formats, orbit propagation, N-body integrator |
| `Sidralune.Core/Satellites` | TLE parsing and SGP4 |
| `Sidralune.Core/Observing` | Visibility model, exposure calculator |
| `Sidralune.Core/Util` | CSV, JSON, HTTP, paths, logging, dataset manager |
| `Sidralune.Imaging` | Image model, file formats (`IO`), analysis, processing, registration, stacking, planetary tools |
| `Sidralune.Science` | Astrometry, photometry, spectroscopy, solar system, radio, online databases |
| `Sidralune.Devices` | Device interfaces, simulators, ASCOM, Alpaca, INDI, LX200, PHD2, capture, sequencer |
| `Sidralune.Tests` | Console test runner and tests |
| `Wiki` | User manual |

The project files include every `.cs` file below the project folder automatically (`**\*.cs`), so you do not need to edit a `.csproj` to add a source file.

### Architecture in brief

- The four libraries do not depend on Windows Forms. `Sidralune.Core` depends on nothing; `Imaging`, `Science` and `Devices` build on it. Keep user-interface code in the `Sidralune` project so the engines stay usable from scripts and tests.
- A workspace is a `UserControl` registered in `MainForm.RegisterViews`. Views are created the first time they are opened.
- A view that implements `ICommandSource` adds its actions to the command palette through `GetCommands()`. Every important action should be reachable there.
- Long work runs through `Tasks.Run(name, work, onDone, onError)`. The work runs on a background thread, shows progress in the status bar and can be cancelled; `onDone` and `onError` run on the UI thread. Use `Tasks.Report(task, fraction, status)` for progress and `task.Cts.Token` for cancellation. Use `MainForm.Instance.Post(action)` to update controls from other threads.
- Notifications use `MainForm.Instance.Toast(text, icon, colour)`. Avoid message boxes except for confirmations.
- `AppState` holds the active site, equipment, simulated time and the selected object. Listen to its events (`TimeChanged`, `SiteChanged`, `ObjectSelected`) rather than polling.
- Option classes in the engines use public fields. `GridAdapter` exposes them to a `PropertyGrid`, so a new option appears in the interface without extra code.

## Project guidelines

### Language and framework

- Target .NET Framework 4.8.1 and **C# 6**. The project must build with MSBuild 14 (Visual Studio 2015). In practice this means no tuples, no pattern matching (`is T x`), no `out var`, no local functions, no throw expressions (`?? throw`), no `default` literals and no `in` parameters.
- The code is pure C#. Performance-critical loops use `unsafe` code and `Parallel.For`. Native C or C++ code is not used at the moment because it would add a dependency on the Windows SDK and C runtime.
- Keep the libraries AnyCPU with `Prefer32Bit` off, so large images and videos can use all available memory.

### No third-party dependencies

Sidralune deliberately uses only the .NET Framework. Do not add NuGet packages, bundled DLLs or calls to external programs. Optional bridges to installed software (ASCOM drivers, ASTAP, PHD2) are acceptable when they are loaded at run time and the feature degrades gracefully without them.

If you believe a dependency is truly necessary, open an issue to discuss it before writing the code.

### Scientific correctness

- Every algorithm must come from a citable source: a paper, a standard (IAU, SOFA, FITS, XISF), a well-known book (Meeus, Explanatory Supplement) or official documentation (JPL, MPC, AAVSO). Name the source in a comment next to the code.
- Do not use formulas or constants you cannot source, and do not "tune" results to match a reference without understanding why they differ.
- Add a test that compares the result against an independent reference value, with a tolerance that matches the method's stated accuracy.
- State units in names or comments (`raDeg`, `decRad`, `jdTT`, `fluxJy`). Angles are radians internally unless the name says otherwise; catalogue positions are J2000 (ICRS).

### Code style

- Follow the style of the surrounding code: four-space indentation, braces on new lines, `_camelCase` private fields, `PascalCase` public members.
- Public engine members may be public fields when they are options or plain data.
- Comment the why, not the what. A short summary comment on public classes and non-obvious methods is welcome.
- Use `CultureInfo.InvariantCulture` for every number you parse or format in files, so Sidralune works the same in every locale.
- In views, add the alias `using AstroEvents = Sidralune.Core.Ephemeris.Events;` instead of using `Events` directly, because `Control.Events` hides the class name.

### User interface

- Use the theme: colours come from `Theme.Current` (a `Palette`), fonts from `Theme.UiFont` and friends, icons from `Icons`. Never hard-code colours, so that the dark, night-vision and print palettes keep working.
- Use the shared controls (`FlatButton`, `Card`, `FormGrid`, `TabbedPanel`, `Collapsible`, `Ui.Grid()`, `Ui.Num()`, `Ui.Combo()`) so that new screens look and behave like the rest of the program.
- Keep the night-vision mode in mind: anything that lights up the screen in white breaks dark adaptation.
- Long operations must not freeze the window. Run them through `Tasks.Run`.
- Write interface text in plain language, in sentence case, and explain units.

## Testing your changes

### Unit tests

The tests are a small console program in `Sidralune.Tests`. Every `public static void Test…()` method in that project runs automatically. Use the helpers in `Program.cs`:

```csharp
public static void TestMyFeature()
{
    Check("Mars distance (AU)", actual, 0.523456, 1e-5);   // value with tolerance
    CheckTrue("Solution found", result != null);          // condition
    Info("Optional extra output");
}
```

Build the solution, then run all tests or a subset:

```
Sidralune.Tests\bin\Debug\Sidralune.Tests.exe
Sidralune.Tests\bin\Debug\Sidralune.Tests.exe Horizons
```

The filter matches part of `ClassName.MethodName`. The exit code is 0 when every check passes and 1 otherwise, so it can be used in scripts. Tests must run offline; if a test needs an optional dataset, skip it with an `Info` message when the dataset is missing, as `CatalogTests.TestConstellationBoundaries` does.

All tests must pass before a pull request is merged.

### Checking the interface

For visual changes, run Sidralune and look at the affected workspace in both the normal and the night-vision palette, and at a small window size. Screenshot mode makes this repeatable:

```
Sidralune\bin\Debug\Sidralune.exe --screenshot shot.png --view planner --night
Sidralune\bin\Debug\Sidralune.exe --screenshot shot.png --view observatory --sim
```

`--script file.cs` runs a C# script before the screenshot is taken. A script can call into views (through `MainForm.Instance`) to put them in a particular state. See [Command-line options](https://github.com/pzyber/Sidralune/wiki/Command-line-options).

Please attach before and after screenshots to pull requests that change the interface.

### Testing with equipment

Test device and automation changes against the simulators first, then with real hardware if you have it. Say in the pull request which devices and drivers you used. Keep a hand near the power switch the first time new code moves a mount.

## Submitting a pull request

1. Keep each pull request focused on one change. Separate refactoring from behaviour changes.
2. Write clear commit messages: a short summary line in the imperative ("Fix rise time for circumpolar objects"), a blank line, then what changed and why if it is not obvious.
3. Bring your branch up to date with `main` and make sure the solution builds without new warnings and all tests pass.
4. Update the wiki pages and README if you changed behaviour that they describe.
5. Open the pull request with a description that says what changed, why, how you tested it, and which issue it fixes (for example "Fixes #42").
6. Be ready to answer review questions. Reviews check correctness and sources first, then style.

## License

Sidralune is released under the [MIT License](LICENSE). By contributing, you agree that your contributions are licensed under the same terms.
