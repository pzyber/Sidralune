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
- [Translating Sidralune](#translating-sidralune)
  - [Sky names](#sky-names)
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

## Translating Sidralune

The interface is written in English and translated with gettext `.po` files in `Sidralune/Languages`, one per language and named after its code: `sv.po` for Swedish, `nb.po` for Norwegian Bokmål, `pt-BR.po` for Brazilian Portuguese. `Sidralune.pot` is the template that lists every text to translate. Any text without a translation is shown in English, so a language can be added a little at a time.

To start a new language:

1. Copy `Sidralune.pot` to `<code>.po`, or open the template in [Poedit](https://poedit.net) (free) and choose **Create new translation**.
2. Set `Language:` in the header to the code, and `Plural-Forms:` to your language's rule (Poedit fills it in). Most languages here use `nplurals=2; plural=(n != 1);`; French uses `plural=(n > 1)` and Icelandic `plural=(n%10!=1 || n%100==11)`. Many languages have more than two forms: Polish, Czech, Slovak, Croatian, Serbian, Latvian, Lithuanian and Romanian use three (for example Polish `nplurals=3; plural=(n==1 ? 0 : n%10>=2 && n%10<=4 && (n%100<10 || n%100>=20) ? 1 : 2);`, giving *1 gwiazda, 2 gwiazdy, 5 gwiazd*), and Slovene uses four. Give every counted text exactly as many `msgstr[n]` lines as `nplurals` says.
3. Translate the `msgstr` lines. Keep placeholders such as `{0}` and `{1:0.0}` exactly as they are; you may move them within the sentence. Keep `\n` line breaks and keyboard keys (`Ctrl+K`, `N/E/S/W`) as they are.
4. Leave out what should not be translated: star names, catalogue designations (`M 31`, `NGC 7000`) and units. Constellation names and the nicknames of well-known objects are translated only in their own entries, with the contexts `constellation` and `object name`; use the name your language actually uses (see [Sky names](#sky-names)).
5. To try it, copy the file to `Languages` next to `Sidralune.exe` (the build does this for you), then choose the language in **Settings → Appearance → Language** and restart. `Sidralune.exe --lang <code>` starts in a language without changing the setting.

A regional variant needs only the texts that differ: a `pt-BR.po` with the Brazilian word choices would take everything else from `pt.po`. Name the file `<language>-<region>.po`; the language's own file must be there too. A language that Windows does not know, such as Latin, gets its name from `OwnNames` in `Lang.cs`.

When you update a translation after the template has changed, Poedit's **Update from POT file** adds the new texts and marks the changed ones as fuzzy; fuzzy entries are not used until you have checked them. Every full language file must translate every text except object nicknames (see below), and the application tests check that each language's workspace names fit under the icons of the navigation rail (80 pixels in the rail's font, about 12 characters; abbreviate with a full stop if needed, such as `Glob. nebo`) and that its row labels fit (the label column widens up to a limit for longer languages). Keep both short.

The translations other than Swedish were written in one go and have not yet been reviewed by native speakers; corrections are very welcome.

### Sky names

The constellation names and object nicknames follow the title or opening sentence of the language's own Wikipedia article where there is one (Malströmsgalaxen, not "Virvelgalaxen"), then Wikidata and other established sources.

- **Constellations** have names in every language, so every language file names all 88. A name that no source confirms is a best guess and its entry carries the comment `# No checked source confirms this name. Please correct it if your language uses another.` If you know the name your language uses, change the `msgstr` and remove the comment; if the guess is right, just remove the comment.
- **Object nicknames** are every nickname in the built-in data and OpenNGC, apart from designations such as Centaurus A or h Persei. They are mostly English coinages ("Pacman Nebula", "Blinking Planetary") that many languages have no name of their own for. A nickname is translated only where the language's Wikipedia or Wikidata gives a name of its own; otherwise its `msgstr` is left empty and the English nickname is shown, as it is when **Sky names** is off. Translations in other astronomy software do not count on their own: they are often word-for-word, and agreeing with another word-for-word translation shows only that the wording is obvious. Fill one in when your language uses a name of its own, not a word-for-word translation, and say where it is used.

## Contributing code

> [!NOTE]  
> Pull requsts will be locked until December 14th.

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

Always build and run the library tests before building the application, build the application only when they all pass, and then run the application tests (see [Unit tests](#unit-tests)).

### Repository layout

| Path | Contents |
|---|---|
| `Sidralune/Program.cs` | Entry point, crash reporting, screenshot mode |
| `Sidralune/MainForm.cs` | Main window, navigation rail, command palette, status bar |
| `Sidralune/AppState.cs` | Settings, simulated time, current selection, background tasks |
| `Sidralune/Views` | One `UserControl` per workspace (`SkyView`, `PlannerView`, `StackingView`, …), and the guide log viewer's window (`GuideLogWindow`) |
| `Sidralune/UI` | Theme and palettes, custom controls, `ImageViewer`, `PlotControl`, `SkyChart`, `EclipseMap` (the world map of an eclipse in Solar sys. → Eclipses), `StarChart` (the all-sky star chart page, drawn in millimetres for screen, printer and 300 dpi images), `PrintPreview` (the themed print preview window), and the hooks that theme native Windows parts (`ScrollOverlays` for scroll bars, `SystemColorHook` for text selection) |
| `Sidralune/Scripting` | C# script host and example scripts |
| `Sidralune/Languages` | Interface translations: `Sidralune.pot` (the template) and one `.po` file per language, copied next to the program when it is built |
| `Sidralune.Core/Math` | Vectors, statistics, FFT, linear algebra, optimisation (Levenberg-Marquardt, Nelder-Mead, MCMC) |
| `Sidralune.Core/Time` | Time scales, Julian dates, leap seconds, ΔT |
| `Sidralune.Core/Coordinates` | Precession, nutation, coordinate transforms, refraction |
| `Sidralune.Core/Ephemeris` | Analytic planets, Moon, JPL SPK reader, physical ephemerides, rise/set and events, Besselian elements and the eclipse map's lines (`BesselianElements`, `EclipseGeometry`), the lunar limb profile from LRO LOLA (`LunarLimb`) |
| `Sidralune.Core/Catalogs` | Built-in data, catalogue loaders, sky index, constellation boundaries |
| `Sidralune.Core/MinorBodies` | MPC formats, orbit propagation, N-body integrator |
| `Sidralune.Core/Satellites` | TLE parsing and SGP4 |
| `Sidralune.Core/Observing` | Visibility model, exposure calculator |
| `Sidralune.Core/Util` | CSV, JSON, HTTP, paths, logging, dataset manager, the eclipse map's world (`WorldMap`: Natural Earth, 1:110m built in as `Resources/world110.bin`) and elevations (`Elevation`: ETOPO1), large height grids kept in tiles and read on demand (`TileGrid`), interface languages (`Lang`: `T()` and the `.po` reader) |
| `Sidralune.Imaging` | Image model, file formats (`IO`), analysis, processing, registration, stacking, planetary tools |
| `Sidralune.Science` | Astrometry, photometry, spectroscopy, solar system, radio, online databases |
| `Sidralune.Devices` | Device interfaces, simulators, ASCOM, Alpaca, INDI, LX200, PHD2, capture, sequencer, guiding around Sidralune's own work (`GuidingPause`: autofocus pauses it, centring and meridian flips start it again; `DriftCorrector` keeps unguided light frames in place by registering them against the field's first; `GuidingRecorder` collects the guide steps of each exposure into `GuidingStats` for its FITS header), solving guide frames (`SidraluneGuider.Solve.cs`: the camera's scale and angle in `GuideFrameSolve`, catalogue guide stars through `GuideStarCatalog`, finding a lost or displaced star again; `SidraluneGuider.Predict.cs`: the calibration predicted from the solve and the guide rates, confirmed with three steps each way), the Star-Cross test and guide frames saved for diagnosis (`SidraluneGuider.Diagnosis.cs`), equipment profiles (`EquipmentProfile`; the app keeps the list in its settings and hands the one in use to `DeviceManager` with `AppState.ApplyProfile`) |
| `Sidralune.Devices/Guiding` | The Sidralune guider (`SidraluneGuider`: the guide loop, calibration, dithering, settling and auto exposure; `SidraluneGuider.Tools`: lock position, comet tracking, manual guide, building dark frames and the Dec backlash test), its settings and calibration (`GuiderSettings`, `GuideCalibration`, saved in `guider.json`), choosing and measuring the guide star (`GuideStars`) and guiding on several stars (`GuideStarSet`), the algorithms (`HysteresisAlgorithm`, `ResistSwitchAlgorithm`, `LowpassAlgorithm`, `Lowpass2Algorithm`, `ZFilterAlgorithm`, `PredictivePecAlgorithm`, `BacklashCompensation`), adaptive optics (`SidraluneGuider.Ao`, `AoCalibration`), dark library and bad-pixel map (`GuideDarks`), the assistants (`GuidingAssistant`, `CalibrationAssistant`), polar alignment (`DriftAlignment`, `StaticPolarAlignment`, `PolarDriftAlignment` and their frame sources `GuiderFrames` and `CameraFrames` in `GuidePolarAlignment.cs`), comet rates (`CometRates`), pulses and the guide log (`GuidePulser`, `GuideLog`), reading guide logs back, Sidralune's and PHD2's, with their statistics and the spectrum of the RA error (`GuideLogFile`, `GuideSpectrum` in `GuideLogAnalysis.cs`), the PHD2 client (`Phd2Client`) and the PHD2-compatible server (`GuiderServer`). The SX AO driver is in `Sidralune.Devices/Serial/SxAo.cs`; Cholesky factorisation and the other matrix routines in `Sidralune.Core/Math/LinearAlgebra.cs` |
| `Sidralune.Tests` | Library tests (MSTest) |
| `Sidralune.AppTests` | Application tests (MSTest): settings, scripting, planner, controls and every workspace |
| `Wiki` | User manual |

The library and test projects (`Sidralune.Core`, `Sidralune.Imaging`, `Sidralune.Science`, `Sidralune.Devices`, `Sidralune.Tests`, `Sidralune.AppTests`) include every `.cs` file below their folder automatically (`**\*.cs`), so a new source file there needs no `.csproj` change. The application project, `Sidralune/Sidralune.csproj`, lists its files one by one: add a `<Compile Include="Folder\File.cs" />` line for each new file (Visual Studio does this for you when you add the file through Solution Explorer), or the build will not see it.

### Architecture in brief

- The four libraries do not depend on Windows Forms. `Sidralune.Core` depends on nothing; `Imaging`, `Science` and `Devices` build on it. Keep user-interface code in the `Sidralune` project so the engines stay usable from scripts and tests.
- A workspace is a `UserControl` registered in `MainForm.RegisterViews`. Views are created the first time they are opened.
- A view that implements `ICommandSource` adds its actions to the command palette through `GetCommands()`. Every important action should be reachable there.
- Long work runs through `Tasks.Run(name, work, onDone, onError)`. The work runs on a background thread, shows progress in the status bar and can be cancelled; `onDone` and `onError` run on the UI thread, and neither runs for a cancelled task. Use `Tasks.Report(task, fraction, status)` for progress and `task.Cts.Token` for cancellation. Use `MainForm.Instance.Post(action)` to update controls from other threads.
- Start a view's long work through a `JobSlot` (in `AppState.cs`) rather than `Tasks.Run` directly. `slot.Run(...)` allows one job at a time: hand the slot the controls that start the job or change its input with `slot.Guard(...)`, and they are disabled while it runs; entry points that are not buttons (dropped files, images sent from other workspaces, keyboard shortcuts) call `slot.Refuse()` first. `slot.Replace(...)` is for recalculations and look-ups: it cancels the running job, so only the newest result is shown. Build results in local variables and apply them in `onDone`, never by writing view fields from the background thread. Opening an image is long work too (`ImageFile.Load` of a large raw or FITS file, debayering): show the file dialog on the UI thread, then load in the slot's job; read any settings from controls before the job starts. A job that uses an options object shown in a property grid gets a copy of it (`Clone()`), so the grid can stay editable while the job works; the result is shown with the options it was made with. Catalogue loading, downloads and reloads share the app-wide `Tasks.Data` slot.
- A button that cannot be used because something elsewhere is busy (the camera, the mount, the guider) is greyed out, not answered with a message. Give it its condition with `Availability.When(button, () => ...)` (in `AppState.cs`): it is enabled only while every condition holds and no slot guarding it runs, so set its `Enabled` nowhere else. `Availability.Sync()` looks again; the Observatory view calls it on its timer and whenever its jobs start or end. A device that is not connected is still said when the button is clicked, since the message tells what to connect.
- Notifications use `MainForm.Instance.Toast(text, icon, colour)`. Avoid message boxes except for confirmations.
- Device properties such as `DeviceManager.Instance.Guider` are `null` when nothing of that kind is connected. Check before you call into a device, and before you start a background task that will: in `ObservatoryView` use `Need(device, NoGuider)` inside button helpers that show exceptions as toasts, or `Have(device, NoCamera)` in plain click handlers. Both give the user the same "No … connected" message, written as a whole sentence for each kind of device so it translates well.
- A new sequencer instruction that cannot run without a device overrides `RequiredDevices` (`"camera"`, `"telescope"`, `"focuser"`, `"filter wheel"`), so **Run sequence** refuses to start without it. Instructions that can do without (guiding, cooling, dome) skip themselves instead.
- The camera takes one exposure at a time. In `ObservatoryView`, anything new that exposes must check `CanUseCamera()`, start through `RunCameraJob(name, work)` (or the sequence or manual-capture paths), and add its button to `_cameraButtons`, so it is disabled while another job uses the camera and **Stop** can end it. Frames reach the Imaging viewer through `CaptureService.ImageCaptured` and, for video, `VideoFrame` and `VideoRecorded`, so anything that captures through `CaptureService` (a sequencer instruction included) is shown there without extra code.
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
- Look colours up when you paint, not when you build data. The palette changes the moment the user switches night mode, so a colour copied from `Theme.Current` into a plot series, marker or annotation list stays in the old theme. Store a palette role instead and resolve it while painting: `PlotControl` takes one through `AddSeries(name, style, p => p.Warning)` and the `ThemeColor` of bands and markers.
- Standard WinForms controls are themed by `Theme.Apply`, including their native parts (scroll bars, drop-down lists, selected text, property-grid buttons and glyphs). Custom controls that paint themselves implement `IThemed` and repaint in `ApplyTheme`.
- Painting over what Windows has drawn is not enough on a dark palette: the screen can show Windows' grey or white version for a frame before the cover arrives. Keep Windows from drawing those parts at all, as `Theme` and `SystemColorHook` do for combo boxes (the classic style, which has no hover animations; drop-down lists shown without the slide animation and kept cloaked until painted; their scroll bars moved without being drawn, painted off-screen and copied in one step, and covered by a window of our own that the mouse passes through, because Windows draws the arrows itself while it tracks a click on them). `ControlsTests.TestComboBoxDropDownInNightMode` checks this.
- Show images with `ImageViewer`: it converts them from their colour profile (`AstroImage.EffectiveProfile`) to the monitor's, draws a large image at the size it has on screen (a reduced copy) until you zoom in, shows a newly opened image with `ShowOpened` (finished pictures unstretched, linear data stretched), applies proofing and the gamut warning, and leaves night mode alone. Code that creates an image from others should keep the profile where the colours stay the same (`Clone` and `CloneEmpty` do), and colour maths should weigh the channels with `EffectiveProfile.LuminanceWeights` rather than fixed Rec. 709 values. Call `ColorUi.Opened` when the user opens a single image in a view, so Settings → Colour applies; frames read in bulk keep what their files carry. Profile handling lives in `Sidralune.Imaging.ColorManagement` (`IccProfile`, `IccProfiles`, `ColorTransform`, `WcsTransform`, `ProfilePolicy`). `ImageViewer` draws on worker threads into locked bitmaps; when the program ends (or a test host unloads it) it waits for the draws still running and starts no more (`ImageViewer.Ending`), since what they write into is torn down under them. Other code drawing into bitmaps in the background at the end of the program needs the same care.
- For the median, MAD or background of an image, use `ImageStatistics.Robust`: `ImageStatistics.Compute` also measures the noise with a wavelet pass over the whole image, which takes seconds on a large one.
- Use the shared controls (`FlatButton`, `Card`, `FormGrid`, `TabbedPanel`, `Collapsible`, `Ui.Grid()`, `Ui.Num()`, `Ui.Combo()`) so that new screens look and behave like the rest of the program.
- Button colours follow one rule: the main action of a page (Start, Run, Solve, Process, Stack) uses `ButtonKind.Primary`, and Stop, Abort, Disconnect and other buttons that interrupt or end something use `ButtonKind.Danger`. A button that switches between the two (Run ⇄ Stop) switches its `Kind` too.
- Keep the night-vision mode in mind: anything that lights up the screen in white breaks dark adaptation.
- Make every control reachable at the minimum window size (1100 × 700). Toolbar rows (a `FlowLayoutPanel` docked to the top or bottom) wrap onto further rows automatically; give side panels `AutoScroll` so they scroll when the window is short.
- When you fill a `DataGridView` whose `SelectionChanged` handler reads `Row.Tag`, create each row with its `Tag` set before adding it: adding the first row selects it immediately, before a `Tag` assigned afterwards exists.
- Long operations must not freeze the window. Run them through `Tasks.Run`.
- Write interface text in plain language, in sentence case, and explain units.
- Mark interface text for translation with the functions of `Lang` (add `using static Sidralune.Core.Util.Lang;`):
  - `T("Save site")` for plain text, and `T("Active site: {0}", name)` for text with values. Write the whole sentence as one format rather than joining pieces (`"Could not open " + name`), because word order differs between languages.
  - `Tn("{0} dataset", "{0} datasets", n)` when a count decides the form.
  - `Tc("file", "Open")` when the same English word needs different translations.
  - `Lang.N("JPL DE440s planetary ephemeris")` marks text that is kept in English as data (a dataset's name, which is also logged) without translating it; show it with `T(dataset.Name)`.
  - The text must be a literal string (or literals joined with `+`) so the template can be built from the source; `T(name)` of a variable is not found.
  - Do not translate what other programs read: log messages, file contents, reports, FITS keywords, settings values and the keys of views and commands. Code that compares a shown text, such as a job's name for a Stop button or a button's text for `Ui.InsertAfter`, must compare it with the same `T(...)` text. Read a choice from a drop-down list by its index, not by its (translated) text.
  - Text that is saved as data and shown as well, like the type of a planner entry, is saved in English and translated when shown. `Lang.InEnglish(() => DeepSkyObject.TypeLabel(t))` gives the English of a shared label.
  - Show the Sun, Moon and planets with `BodyNames.Name(body)`, object types with `DeepSkyObject.TypeLabel`, visibility with `VisibilityResult.Label` and meteor showers with `DisplayName`; `Body.ToString()` and `SelectedObject.Name` stay English, because they are also used as data.
  - Show constellations with `SkyNames.ConstellationName(c)` and deep-sky objects with `SkyObject.ShownName`, which puts a well-known nickname in the interface language ("M51 Malströmsgalaxen"); `DisplayName`, `Name` and `Constellation.Name` stay as the catalogue has them, for planner entries, files and reports. The names to translate are listed in `SkyNames` with `Lang.Nc("constellation", "Ursa Major")` and `Lang.Nc("object name", "Whirlpool Galaxy")`, which mark text with a context without translating it. A nickname is matched on the catalogue's own spelling, so list each spelling the built-in data and OpenNGC use; `SkyNamesTests` checks that every listed nickname is still in the catalogues (OpenNGC when it is installed). `CatalogManager.Find` and `Search` also find objects by their translated nicknames, and `SkyNames.FindConstellation` finds a constellation by abbreviation, IAU name or translated name. Use the name the language actually uses, not a word-for-word translation; [Sky names](#sky-names) says where the names come from and how the unconfirmed ones are marked.
  - Dates written with words (`ddd`, `MMM`) take `Lang.DateNames` as their format provider, so day and month names follow the language while numbers stay invariant. `T("…", date)` does this already.
  - Option names and choices in property grids come from the member names: `GridAdapter` shows `Lang.Option(name)`, the words of the name translated with the context "option" (`HotPixelRemoval` is "Hot Pixel Removal"), and Yes/No for `bool` members. They are not literals, so `TranslationTests` adds them to the template from the classes in its `OptionTypes` list; add a new option class there when a view shows it in a grid. A member with its own `[TypeConverter]`, like the frame type, keeps its drop-down.
  - Messages from the engines that the interface shows (progress, results, errors of a step) are marked with `Lang.T` as well. When such a message is also logged, log it in English: pass the text as a function and log `Lang.InEnglish(text)`, as `StackingEngine.Msg` does.
- After adding or changing marked text, run `TranslationTests.TestTranslationFiles`. When the template is out of date it fails and writes a fresh one to `%TEMP%\SidraluneAppTests\Sidralune.pot`: copy it to `Sidralune/Languages`, then add the Swedish translations to `sv.po` (or ask in the pull request for help with them).

## Testing your changes

### Unit tests

The tests use MSTest, Visual Studio's own test framework (`Microsoft.VisualStudio.QualityTools.UnitTestFramework`, included with Visual Studio 2015 or later; no NuGet packages). There are two test projects, run in this order:

1. `Sidralune.Tests` tests the libraries (about six minutes). It does not depend on the application, so it runs before the application is built.
2. `Sidralune.AppTests` tests the application (about five and a half minutes). It references `Sidralune.exe`, so building it builds the application first.

A test class derives from `TestBase` and is marked `[TestClass]`; each test is a `[TestMethod]`. A test makes many checks with the helpers in `Checks.cs` (shared by both projects). Every check is printed in the test's output, a failed check does not stop the test, and the test fails at the end with the list of every failed check:

```csharp
[TestClass]
public class MyFeatureTests : TestBase
{
    [TestMethod]
    public void TestMyFeature()
    {
        Check("Mars distance (AU)", actual, 0.523456, 1e-5);   // value with tolerance
        CheckTrue("Solution found", result != null);          // condition
        Info("Optional extra output");
    }
}
```

In Visual Studio, build the solution, open **Test Explorer** from the **Test** menu and choose **Run All**; you can also run, rerun or debug a single test from there. On the command line:

```
msbuild Sidralune.Tests\Sidralune.Tests.csproj
"C:\Program Files (x86)\Microsoft Visual Studio 14.0\Common7\IDE\CommonExtensions\Microsoft\TestWindow\vstest.console.exe" Sidralune.Tests\bin\Debug\Sidralune.Tests.dll
msbuild Sidralune.AppTests\Sidralune.AppTests.csproj
"C:\Program Files (x86)\Microsoft Visual Studio 14.0\Common7\IDE\CommonExtensions\Microsoft\TestWindow\vstest.console.exe" Sidralune.AppTests\bin\Debug\Sidralune.AppTests.dll
```

Run a subset while you work with a filter, for example `/TestCaseFilter:"FullyQualifiedName~Horizons"` or `/Tests:TestPhd2`. Use the `vstest.console.exe` of your Visual Studio version (for newer versions it is under `Common7\IDE\Extensions\TestPlatform`). If the repository is on a network drive, the test runner may refuse to load the test assembly (error `0x80131515`); copy the test project's `bin\Debug` folder to a local folder and run the tests there. The library tests take about six minutes and the application tests about five and a half (checking that every language fits takes two of them). Visual Studio 2015's command-line runner waits for ever if a test process dies; the application tests record such a crash in `%TEMP%\SidraluneAppTests-crash.txt`, and a test whose UI thread stops answering fails after a minute.

Every part of the libraries has tests, grouped by area:

| File | Covers |
|---|---|
| `CoreTests.cs`, `CoreUtilTests.cs`, `HorizonsTests.cs`, `MinorBodyTests.cs`, `CatalogTests.cs` | Time, coordinates, ephemerides and SPK kernels (including replacing the loaded kernel during a data update), data downloads (mirrors, retries, servers that fail), events (including eclipses as seen from a site), JSON and CSV, statistics and numerical methods, observing tools, satellites, catalogues |
| `EclipseGeometryTests.cs` | a site's eclipse in the eclipse list and phases being the map's (every contact, the maximum, type, magnitude and altitudes with refraction, for total, partial and annular eclipses), Besselian elements and an eclipse at any place against Xavier Jubier's predictions for 22 July 2028 (within 2 s with the DE440s installed for Sidralune, roughly with the analytic Moon) and the topocentric calculation, the central line, the edges of the path and its width, the lines meeting as on his maps (unbroken limits from tip to tip of closed sunrise/sunset loops, the central line ending on the maximum-at-horizon curves, every line cut at the date line, also for two eclipses across it in 2035), the lunar visibility regions, reading a place as people copy it (decimal degrees, decimal commas, degrees, minutes and seconds with letters before or after, map links, the interface language's compass letters, and text that is no place), the built-in world map and Natural Earth GeoJSON |
| `LimbAndElevationTests.cs` | The total or annular phase with the Moon's real edge (a round outline the size of the mean limb gives the mean contacts, a higher one a longer totality, mountains on one side move one contact, deep valleys end totality, mountains make a short one just outside the path, annular eclipses the other way round), the site's phase list with it (the same as the map's), the IAU lunar rotation model against Meeus's librations, LRO LOLA grids kept in the limb band and read back (between points, round the poles and longitudes), the profile they give seen from the Earth, ETOPO1 grids read from their zip with the sea at sea level, the elevation moving contacts, the new datasets, and with the real data installed: known heights, and the limb corrections of C2 and C3 for 22 July 2028 against Xavier Jubier's within 0.5 s (with DE440s) |
| `LangTests.cs` | Interface languages: reading and writing `.po` files, plural rules (including Polish with three forms), looking up and filling in texts, the pseudo-language, regional files with only their differences, Nynorsk using Bokmål, language names, option names in words, finding marked texts (T, N, Tc, Nc, Tn) in source |
| `SkyNamesTests.cs` | Constellation names and object nicknames in the interface language: the lists match the built-in constellations and the nicknames in the built-in data and the installed OpenNGC, Swedish names shown while the catalogue's names stay as data (and in English with `Lang.InEnglish`), star names and untranslated nicknames left as they are, finding objects and constellations by Swedish, IAU and English names and abbreviations, the setting off, and every language translating every listed name |
| `ImagingTests.cs`, `ImagingProcessingTests.cs`, `ImagingGeometryTests.cs`, `StackingTests.cs`, `PlanetaryTests.cs`, `CrwTests.cs`, `Cr3Tests.cs` | Image files (including Canon CRW and CR3 through small writers of each, and LZW-compressed TIFF as Windows writes it), quick and full image statistics, debayering, processing, geometry, star detection (including stars about one pixel wide) and matching, calibration, stacking, drizzle, video and raw formats, planetary tools |
| `ColorManagementTests.cs` | ICC profiles (the built-in working spaces against published values and Windows' own sRGB profile), exact conversions between profiles and agreement with Windows' colour system, profiles in FITS, XISF, TIFF, PNG and JPEG files, the policies on opening, 8-bit exports |
| `AstrometryTests.cs`, `ScienceTests.cs`, `ScienceExtraTests.cs` | Plate solving, image depth, photometry, timing, light curves, spectroscopy, radio, meteors, occultations, Saturn's ring tilt and moons, catalogue providers |
| `GuidingAdvancedTests.cs` | The Cholesky routines; Predictive PEC against Hysteresis on a periodic error, re-estimating the period, surviving a lock move and a slew; adaptive optics calibration, guiding with bumps under a strong drift and bumping after a dither; a guide camera on a rotator turned 90° while guiding; subframes and noise reduction; the PHD2-compatible server with a raw client and with Sidralune's own PHD2 client; drift alignment, static polar alignment and polar drift alignment against a simulated polar error of 10′ and 15′, with the guide camera and with the imaging camera; an alignment ending when its frames stop, and holding guiding back while it runs |
| `GuidingTests.cs` | The guide algorithms (Hysteresis, Resist Switch, Lowpass2, Z-filter, backlash compensation), calibration geometry and fit, choosing and measuring guide stars, multi-star guiding and the star-mass check, the simulators' guide pulses, tracking errors, pier side and guide camera, and the Sidralune guider in a closed loop on the simulators: calibration, guiding, dithers, pause, lost stars, a meridian flip, slews and failures; dark library, bad-pixel map and auto exposure; the Guiding and Calibration Assistants; lock position, sticky lock, Dec guide modes, comet tracking and training, and manual guide; equipment profiles (one device per kind, focal lengths through a reducer and an off-axis guider, the guider's settings file per profile, Connect all); guiding while imaging (the dither spiral, autofocus pausing guiding, centring holding the old lock position, a guide camera connected again, a sequence dithering on read-out, starting guiding again and waiting out a cloud); guiding quality in frames (RMS and peak per exposure, the FITS keywords, unguided and dark frames, a cloud interrupting a frame, a sequence flagging and retaking frames over its limit); solving guide frames (the catalogue's judgement of single, double and variable stars, the solved scale kept per equipment, a knocked mount brought back by solving, the solve kept through a stop and start in place and made again after a centring move, a centring that synced the mount, a meridian flip and a move the mount's coordinates do not show, a drift taken into it, a cloud at the start keeping it, forgetting it after a slew); the predicted calibration (the same as a full one, kept through a flip and a Dec change, a mount reporting the wrong guide rate calibrated in full); the toolbox (Lowpass, the Star-Cross test on a mount with Dec backlash, guide frames saved around a lost star and a failed calibration) |
| `GuideLogTests.cs` | Guide logs read back: Sidralune's (past midnight, sessions, calibration, frames between dithers, settling) and PHD2's (sessions, scale, the RA rate scaled for the declination, dithers); the spectrum of the RA error, by FFT at an even pace and Lomb–Scargle at an uneven one, finding a known periodic error with the corrections added back; the guider's own log |
| `DeviceTests.cs`, `DeviceProtocolTests.cs`, `SequencerTests.cs`, `AutomationTests.cs` | Capture, video recording and live view, the switch box and flat panel simulators, LX200/OnStep, PHD2, INDI, Alpaca and ASCOM against stand-ins, the device manager, every sequencer instruction and trigger (Set switch and Flat panel included), centring and polar alignment, plate solving the simulated cameras' sky and the catalogue used without a connection, drift correction of unguided frames (registration, pulses with Dec turned round, dithers, slews, a new field, dithers without a reference and a slew ending the wait for one) |

The application tests (`Sidralune.AppTests`) start the real main window once, off screen, on a UI thread of its own (`AppHost.cs`), with its settings and data in `%TEMP%\SidraluneAppTests`. Tests run code on that thread with `OnUi(…)`, wait for background work with `WaitFor(…)`, open a workspace with `View<T>(key)`, click with `PerformClick()`, read notifications with `Toasts()` and check the log for errors with `LogMark()` and `ErrorsSince(…)`:

| File | Covers |
|---|---|
| `AppStateTests.cs` | Settings saving and loading (including damaged files), profiles, time control, selection, background tasks (including thousands of progress reports while the interface is busy, which no longer queue up) |
| `ScriptingTests.cs` | Script compilation and error lines, helper members, cancellation, every example, the Scripts workspace (each run starts with an empty plot and table, `ClearTable()`, the batch example's missing folder) |
| `PlannerAndSettingsTests.cs` | Observing lists, sites, night mode in sync everywhere and the night palette |
| `ControlsTests.cs` | Buttons, icons centred in icon-only buttons, check boxes that show their check while disabled or read-only (a grid while a job runs), disabled drop-down lists and number fields drawn in the palette by day and at night, grids redrawn whole when they or a column turn read-only or editable, tabs, collapsible sections, form grids, time fields, sliders, status pills, notifications, the property-grid adapter, the Jupiter and Saturn diagrams on Tonight (names clear of each other, everything inside the drawing), combo-box drop-down lists in night mode (opened, scrolled and redrawn without system colours, the cover over their scroll bar) |
| `ColorManagementAppTests.cs` | Colour management in the interface: the Colour tab, the colour-managed image viewer (with a fixed target profile instead of the real monitor), night mode and false colour left alone, assign and convert in Process with undo, proofing and the gamut warning, large images drawn at screen size until zoomed in, the display stretch of opened images |
| `ViewTests.cs` | Every workspace opens without errors, in night mode and at the smallest window size, Tonight packs its cards in columns without gaps at three window sizes and scrolls to the lowest card as soon as it opens, its Coming up list follows the time you set and shows eclipses from the site, Solar sys. → Eclipses (the list in time order with each eclipse from the site, the phases of the selected one, Show in sky) and its map (the lines of a solar and a lunar eclipse, zooming, a clicked place's circumstances and the popup's layout (clear of the place, also in a narrow map), its elevation (typed in, kept for the place, from elevation data with a note), the Moon's real edge in it, Open in Google Maps and Copy, Go to coordinates, Your site (its own elevation, right-clicking its marker, a left click next to it giving the place clicked), Add as site with the elevation, by day and at night, the lunar shading zoomed in), the commands, that the windows Sidralune opens show its icon without a white flash, and the print preview (opens before the pages are drawn, icon, colours, every page, red on dark paper at night) |
| `TimeAxisTests.cs` | Time axes across daylight saving changes: the altitude charts never run backwards, ticks follow the local clock |
| `SkyChartTests.cs` | The sky chart: a planet partly in view, planets' moons drawn and clickable (also far out when zoomed in), the deeper magnitude limit when zoomed in, star dot sizes, the Milky Way zoomed in, and a selected comet or asteroid drawn at its place whatever the limit, also before the other minor bodies' positions are ready, comets loaded or added after the chart was first drawn, comet tails pointing away from the Sun at any zoom |
| `AstrometryViewTests.cs` | Which minor body (asteroid and comet) labels a solved image shows, the image depth and step times in the result panel, and the Display stretch in Astrometry and Process |
| `PlanetaryViewTests.cs` | Planetary frames are shown as recorded, without a display stretch, and a pre-processed video goes to Lucky stacking with **Stacking →**, and the Lucky stacking buttons say when nothing is stacked yet |
| `BackgroundLoadingTests.cs` | Images and videos open in the background as their tab's job (Planetary, Process channel combining, Astrometry) |
| `ListEditingTests.cs` | Lists you add to can be emptied again: Derotation images, Photometry frames and stars, added minor bodies; the orbit of the first listed minor body shown at once, and double-clicking one centres it in the Sky tab, and an open comet orbit drawn through the comet and out to the edge of the diagram |
| `JobTests.cs` | One job at a time where jobs share their input (plate solving, processing, stacking, planetary, photometry, data downloads), Deep sky's Show stacked image bringing the result back after a frame preview, and Show, Save and Open in Process greyed out until something is stacked and while a job runs, newest-result-wins look-ups, and cancelled or failed jobs freeing their controls |
| `ObservatoryViewTests.cs` | Equipment and equipment profiles (optics, the guider's focal length, remembered devices and Connect all, switching rigs), Disconnect all, the Guiding tab with the Sidralune guider on the simulators (its pages, algorithm settings, lock position, the Guiding Assistant and the dark-frame prompt), the advanced guider settings (Predictive PEC, the AO card and AO steps, rotator, subframes and noise reduction, the PHD2-compatible server starting and stopping, the graph's pages), the polar alignment methods (the camera choice, a camera tab's Stop ending an alignment, guiding and other mount work greyed out while one runs), the switches and flat panel on the Dome & weather tab, one camera job at a time, planetary live view handing over to recording, messages for missing devices, sequence editing |
| `TranslationTests.cs` | The template and every `.po` file against the texts marked in the source and the option names of the property grids (every full language complete, placeholders kept, as many plural forms as each language's rule asks for, regional files backed by their language), workspace names and row labels fitting in every language, option names and choices marked in the pseudo-language and read back from what is shown, Yes/No, sequencer instructions and missing devices in Swedish, Settings in Swedish with labels that fit, choosing a language, the command line for restarting in a new language, Swedish planet names, object types, events, datasets and loaded catalogues (kept in English as data), and the pseudo-language finding unmarked text in Settings, Tonight (with the night worked out), Sky (with an object selected, and the star chart's headings), Planner (with its table filled, its types saved in English), and every tab of all the other workspaces |

Rules for tests:

- A new feature or fix comes with tests in the same change, and a fixed bug gets a test that fails without the fix.
- Tests must run offline and without equipment. Test devices against the simulators, protocols against a stand-in server on `127.0.0.1` (see `DeviceProtocolTests.cs`), and ASCOM drivers against a stand-in object (the wrappers call drivers through `dynamic`).
- Write files only under `%TEMP%\SidraluneTests` (or `%TEMP%\SidraluneAppTests` for the application tests), never in the user's image folder or settings; set `AppPaths.Root` to a temporary folder when a test needs it, and restore it afterwards.
- A change to a workspace comes with an application test, and a new workspace is added to the checks in `ViewTests.cs`. Run the affected test class while you work on it, for example `/TestCaseFilter:"FullyQualifiedName~PlannerAndSettingsTests"`.
- Compare numbers with a tolerance and format expected text with the current culture or `CultureInfo.InvariantCulture`, so the tests pass on any system language.
- Prefer independent references (published examples, a brute-force computation, a round trip) over the code's own output.
- If a test needs an optional dataset, skip it with an `Info` message when the dataset is missing, as `CatalogTests.TestConstellationBoundaries` does. `CrwTests.TestCrwCameraFiles` and `Cr3Tests.TestCr3CameraFiles` read any camera files in `Sidralune.Tests\TestData\Crw` and `TestData\Cr3` the same way. `TestData` is ignored by git, so large sample files stay on your own machine; public raw samples of most cameras are at [raw.pixls.us](https://raw.pixls.us).

All tests must pass before a pull request is merged.

### Checking the interface

For visual changes, run Sidralune and look at the affected workspace in both the normal and the night-vision palette, and at a small window size. Screenshot mode makes this repeatable:

```
Sidralune\bin\Debug\Sidralune.exe --screenshot shot.png --view planner --night
Sidralune\bin\Debug\Sidralune.exe --screenshot shot.png --view observatory --sim
Sidralune\bin\Debug\Sidralune.exe --screenshot shot.png --view settings --lang qps
```

`--lang qps` shows the pseudo-language: every translated text is accented, bracketed and a third longer, so text left in plain English has not been marked for translation, and anything cut off would not fit a longer translation either.

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
