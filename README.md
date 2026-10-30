# Sidralune

Sidralune is a free astronomy suite for Windows. It brings planning, telescope control, image capture, image processing and amateur science together in one dark-themed desktop application, so you can go from "what can I see tonight?" to a calibrated light curve or a stacked planetary image without switching programs.

It is written in C# for .NET Framework, uses no NuGet packages or third-party libraries, and works offline. Online services are only contacted when you ask for something that needs them, such as a SIMBAD lookup or an astrometry.net solve.

The full manual is in the [wiki](https://github.com/pzyber/Sidralune/wiki/Home).

## Contents

- [What Sidralune does](#what-sidralune-does)
- [Requirements and building](#requirements-and-building)
- [Quick start](#quick-start)
- [The workspaces](#the-workspaces)
- [Downloading catalogues and ephemerides](#downloading-catalogues-and-ephemerides)
- [Connecting equipment](#connecting-equipment)
- [Supported file formats](#supported-file-formats)
- [Keyboard shortcuts](#keyboard-shortcuts)
- [Command-line options](#command-line-options)
- [Where Sidralune stores its data](#where-sidralune-stores-its-data)
- [Accuracy](#accuracy)
- [Running the tests](#running-the-tests)
- [Repository layout](#repository-layout)
- [Documentation](#documentation)
- [Contributing](#contributing)
- [License](#license)
- [Disclaimer](#disclaimer)

## What Sidralune does

| Area | Highlights |
|---|---|
| Planning | Tonight dashboard, planetarium and atlas charts, printable finder charts, comets and asteroids on every chart, observing lists that rank targets by predicted visibility, logbook |
| Observatory | ASCOM, ASCOM Alpaca, INDI, LX200/OnStep and PHD2; a full simulated observatory; imaging, planetary video (SER) recording, autofocus, plate-solve centring, three-point polar alignment, dome and weather safety, sequencer with triggers and planetary video |
| Deep-sky imaging | Calibration with masters, star registration, 12 pixel-combine methods, drizzle, comet stacking, mosaics |
| Processing | Background extraction, stretches (auto, GHS, arcsinh, levels), deconvolution, wavelets, noise reduction, star removal and reduction, colour calibration, narrowband palettes, PixelMath, undo history |
| Planetary, lunar and solar | Video pre-processing (centring, cropping, frame rejection), stacking with alignment points, wavelet sharpening, deconvolution, field derotation for alt-az mounts, planetary rotation correction across several videos, solar and lunar tools, mosaic stitching |
| Astrometry | Built-in blind plate solver with TAN-SIP fitting, ASTAP and astrometry.net bridges, annotation, framing and mosaic planner, exposure and SNR calculator |
| Photometry | Ensemble differential photometry, AAVSO reports, Lomb-Scargle, BLS and PDM period searches, eclipse timing, O−C, transit fitting with MCMC, transit predictions |
| Spectroscopy | Trace and optimal extraction, lamp wavelength calibration, line measurement, radial velocity, instrument response, temperature and spectral class, BeSS export |
| Solar system | Ephemerides, minor-body browser with orbit diagram, Galilean moons, asteroid and lunar occultations, meteor showers, satellite passes, MPC astrometry reports |
| Radio | Hydrogen-line spectra from SDR recordings or a live rtl_tcp server, waterfall, LSR velocities, kinematic distances, dish calculator, Jupiter storm predictions, drift scans, pulsar folding, SID detection |
| Databases | Local catalogue search, Sesame, SIMBAD, VizieR, AAVSO VSX and NASA Exoplanet Archive queries, ADQL console, citizen-science directory |
| Automation | C# scripting with access to the whole Sidralune API, data-analysis workbench, command palette for every action |

The interface follows current usability research: a navigation rail with one workspace per task, a searchable command palette (Ctrl+K), progressive disclosure through collapsible panels, non-blocking toast notifications, background tasks with progress in the status bar, and a red night-vision palette that protects dark adaptation.

## Requirements and building

You need:

- Windows 10 or 11 (64-bit recommended).
- .NET Framework 4.8.1. It is included with recent Windows 11 builds; otherwise install the runtime from Microsoft.
- To build: the .NET Framework 4.8.1 Developer Pack and either Visual Studio (2015 or later) or MSBuild 14 or later. The unit tests need Visual Studio's MSTest framework, which comes with Visual Studio 2015 or later; without Visual Studio, build only the program (`msbuild Sidralune\Sidralune.csproj`).

No other software is required. ASCOM, ASTAP, PHD2 and SDR software are optional and only needed for the features that use them.

To build in Visual Studio, open `Sidralune.sln` and build the solution. From a command prompt:

```
msbuild Sidralune.sln /p:Configuration=Release
```

The program is written to `Sidralune\bin\Release\Sidralune.exe`. Copy the whole `bin\Release` folder if you want to run it elsewhere; the `Sidralune.*.dll` files must stay next to the executable.

> The code base is limited to C# 6 so that it builds with the oldest supported toolchain. See [CONTRIBUTING.md](CONTRIBUTING.md) before changing it.

## Quick start

1. Start `Sidralune.exe`. The **Tonight** page opens with a summary of the night for the default site.
2. Open **Settings → Location**, enter your latitude, longitude, elevation, time zone and sky brightness, and click **Use this site**.
3. Open **Settings → Data**, keep the recommended datasets ticked and click **Download selected**. This adds 120,000 stars, every NGC/IC object, constellation figures and boundaries, comets, and the JPL DE440s ephemeris. Sidralune works without them, using its small built-in catalogue.
4. Enter your telescopes, eyepieces and cameras in **Settings → Equipment**. They drive the field-of-view indicators, the visibility predictions and the exposure calculators.
5. Go to **Sky**, press Ctrl+K and type an object name, for example `M 13`. Click **Add to list**, then open **Planner** to see when it is best placed.
6. To try the observatory without hardware, open **Observatory → Equipment** and click **Connect simulated observatory**. You get a simulated mount, camera (rendering real catalogue stars), focuser, filter wheel, dome and weather station.

The [Getting started](https://github.com/pzyber/Sidralune/wiki/Getting-started) wiki page walks through each step in more detail.

## The workspaces

Each workspace is a button on the navigation rail on the left. Ctrl+1 to Ctrl+9 open the first nine.

| Workspace | Purpose | Wiki page |
|---|---|---|
| Tonight | Darkness times, Moon, planets, best targets, upcoming events, Jupiter's moons, bright comets | [Tonight](https://github.com/pzyber/Sidralune/wiki/Tonight) |
| Sky | Planetarium and atlas chart, object information, printing | [Sky chart](https://github.com/pzyber/Sidralune/wiki/Sky-chart) |
| Planner | Observing lists, visibility ranking, altitude graph, logbook | [Observation planning](https://github.com/pzyber/Sidralune/wiki/Observation-planning) |
| Observatory | Equipment, imaging and planetary video recording, guiding, dome and weather, polar alignment, sequencer | [Observatory and devices](https://github.com/pzyber/Sidralune/wiki/Observatory-and-devices), [Sequencer](https://github.com/pzyber/Sidralune/wiki/Sequencer) |
| Deep sky | Calibrate, register and stack deep-sky frames | [Deep-sky stacking](https://github.com/pzyber/Sidralune/wiki/Deep-sky-stacking) |
| Process | Post-processing of stacked images | [Image processing](https://github.com/pzyber/Sidralune/wiki/Image-processing) |
| Planetary | Pre-process, Lucky stacking, Sharpen & deconvolve, Derotation, Solar & lunar | [Planetary, lunar and solar imaging](https://github.com/pzyber/Sidralune/wiki/Planetary,-lunar-and-solar-imaging) |
| Astrometry | Plate solving, framing and mosaics, exposure planner | [Astrometry and framing](https://github.com/pzyber/Sidralune/wiki/Astrometry-and-framing) |
| Photometry | Measuring, light-curve analysis, transits, transit predictions | [Photometry and exoplanets](https://github.com/pzyber/Sidralune/wiki/Photometry-and-exoplanets) |
| Spectra | 2D reduction and 1D analysis | [Spectroscopy](https://github.com/pzyber/Sidralune/wiki/Spectroscopy) |
| Solar sys. | Ephemerides, minor bodies, moons, occultations, meteors, satellites, MPC reports | [Solar system](https://github.com/pzyber/Sidralune/wiki/Solar-system) |
| Radio | Hydrogen line, calculator, Jupiter, time-domain tools | [Radio astronomy](https://github.com/pzyber/Sidralune/wiki/Radio-astronomy) |
| Databases | Local catalogues, online databases, citizen science | [Databases and citizen science](https://github.com/pzyber/Sidralune/wiki/Databases-and-citizen-science) |
| Scripts | C# scripts and data analysis | [Scripting and data analysis](https://github.com/pzyber/Sidralune/wiki/Scripting-and-data-analysis) |
| Settings | Location, equipment, observer, appearance, data, about | [Settings](https://github.com/pzyber/Sidralune/wiki/Settings) |

## Downloading catalogues and ephemerides

Nothing is downloaded automatically. **Settings → Data** lists every dataset with its size, source and licence. The recommended ones are ticked when you first open the page.

| Dataset | Size | Recommended | Adds |
|---|---|---|---|
| JPL DE440s | 31 MB | Yes | Milliarcsecond positions of the Sun, Moon and planets, 1849–2150 |
| HYG v4.1 | 32 MB | Yes | About 120,000 stars with names, Bayer/Flamsteed, colours and proper motions |
| OpenNGC and addendum | 4 MB | Yes | All NGC/IC objects plus Messier, Caldwell and other extras |
| PGC galaxies (HyperLEDA) | 19 MB | No | About 983,000 galaxies (Principal Galaxies Catalogue) for search, the sky chart and plate-solve labels |
| PGC galaxy magnitudes (HyperLEDA) | 64 MB (kept as 7 MB) | No | B magnitudes for 93 % of the PGC galaxies |
| Constellation lines | 12 KB | Yes | Western stick figures (Stellarium) |
| IAU constellation boundaries | 600 KB | Yes | Boundary lines and "which constellation is this in?" |
| MPC comet elements | 200 KB | Yes | Every known comet |
| MPC near-Earth asteroids | 7 MB | No | Every known NEA |
| MPCORB | 70 MB | No | All numbered and multi-opposition minor planets (those brighter than H = 15.5 are loaded) |
| CelesTrak bright satellites | 30 KB | No | Two-line elements for about 150 bright satellites |
| CelesTrak space stations | 10 KB | No | ISS, Tiangong and other crewed stations |

Without these files, Sidralune uses a built-in catalogue of 114 bright stars, 169 Messier and showpiece objects, all 88 constellations, four bright asteroids, and an analytic planetary theory. See [Data and catalogues](https://github.com/pzyber/Sidralune/wiki/Data-and-catalogues).

## Connecting equipment

| Protocol | Devices | Notes |
|---|---|---|
| ASCOM | Mounts, cameras, focusers, filter wheels, domes, rotators, switches, safety monitors, weather stations, cover calibrators | Install the ASCOM Platform and your drivers. Sidralune uses them through COM, so no SDK is needed. |
| ASCOM Alpaca | Same as ASCOM, over the network | Found automatically by UDP discovery. |
| INDI | Devices on a Raspberry Pi or any other INDI server | Connect to `host:7624`. |
| LX200 / OnStep | Mounts over a serial port or Wi-Fi | For example `COM3:9600` or `192.168.0.1:9999`. |
| PHD2 | Guiding | Through PHD2's event server on port 4400. |
| Simulators | Every device type | For learning, testing sequences and demonstrations. |

See [Observatory and devices](https://github.com/pzyber/Sidralune/wiki/Observatory-and-devices).

## Supported file formats

| Kind | Read | Write |
|---|---|---|
| Images | FITS (8/16/32-bit integer, 32/64-bit float), XISF (including zlib and LZ4 compression), TIFF, PNG, JPEG, BMP, DNG, CRW, CR2, CR3 | FITS, XISF, TIFF, PNG, JPEG |
| Video | SER, AVI (uncompressed RGB or grey, YUY2, MJPEG), image sequences | SER, AVI, FITS/TIFF/PNG sequences |
| Spectra | FITS 1D, text and CSV | BeSS FITS, text |
| Data | CSV, text tables, MPC 1-line orbits, CelesTrak TLE, JPL SPK (.bsp) | CSV, AAVSO extended format, MPC 80-column, ADES PSV |
| Projects | Stacking lists (`.sdstack`), sequences (`.json`), scripts (`.cs`) | Same |

Full details in [File formats](https://github.com/pzyber/Sidralune/wiki/File-formats).

## Keyboard shortcuts

| Keys | Action |
|---|---|
| Ctrl+K, Ctrl+F or Ctrl+Shift+P | Command palette: search objects and commands |
| Ctrl+1 … Ctrl+9 | Open the first nine workspaces |
| Ctrl+Shift+N | Night vision (red) mode |
| Arrow keys, +/−, Page Up/Down | Pan and zoom charts and images |
| N, E, S, W, Z | Look north, east, south, west or at the zenith (planetarium) |
| 0, 1 | Fit image to window, actual pixels (image viewers) |
| Ctrl+O, Ctrl+S, Ctrl+Z, Ctrl+Y | Open, save, undo, redo (Process workspace) |
| F5, Ctrl+S | Run and save a script (Scripts workspace) |

## Command-line options

Sidralune normally starts without arguments. A screenshot mode exists for documentation and automated visual checks:

```
Sidralune.exe --screenshot out.png --view sky --time 2026-12-14T22:00:00 --night --size 1600x1000
```

| Option | Meaning |
|---|---|
| `--screenshot <file>` | Render the window to a PNG file and exit |
| `--view <key>` | Workspace to show: `home`, `sky`, `planner`, `observatory`, `stacking`, `processing`, `planetary`, `astrometry`, `photometry`, `spectro`, `solarsystem`, `radio`, `databases`, `scripting`, `settings` |
| `--time <ISO 8601>` | Simulated time (UTC) |
| `--sim` | Connect the simulated observatory first |
| `--night` | Use the red night-vision palette |
| `--size <W>x<H>` | Window size in pixels (default 1500x950) |
| `--wait <seconds>` | Time to let the view settle before capturing (default 4) |
| `--script <file.cs>` | Run a C# script in the Scripts workspace before capturing |

See [Command-line options](https://github.com/pzyber/Sidralune/wiki/Command-line-options).

## Where Sidralune stores its data

Everything lives under `%LOCALAPPDATA%\Sidralune`:

```
%LOCALAPPDATA%\Sidralune
├── Settings     settings.json (sites, equipment, preferences), planner.json (lists, logbook)
├── Data
│   ├── Catalogs      downloaded catalogues, orbits and TLEs
│   ├── Ephemerides   de440s.bsp
│   └── Scripts       your saved scripts
├── Cache        cached online query results (for example Gaia DR3 fields)
└── Logs         daily logs and crash reports
```

Uninstalling is a matter of deleting the program folder and this folder.

## Accuracy

| Calculation | Method | Typical accuracy |
|---|---|---|
| Sun, Moon, planets with DE440s | JPL SPK Chebyshev ephemeris, light time, aberration, IAU 2006 precession, nutation | Milliarcseconds |
| Planets without DE440s | Standish (JPL) Keplerian elements with Jupiter perturbation terms | About 2′ RMS against JPL Horizons, 1990–2045 |
| Moon without DE440s | ELP/Meeus series | Arcseconds to tens of arcseconds |
| Comets and asteroids | Two-body (universal-variable) Kepler propagation from the orbit epoch | Depends mostly on the orbit quality and how far you are from its epoch |
| Satellites | SGP4 (Vallado 2006); deep-space orbits use the same secular model | Kilometres near the TLE epoch for low orbits |
| Time | Leap seconds, ΔT from Espenak-Meeus | Sub-second for modern dates |
| Visibility | Krisciunas-Schaefer sky brightness and Schaefer's threshold model | A guide, not a guarantee |

The test suite compares positions against JPL Horizons, Meeus worked examples and published values. See [Algorithms and accuracy](https://github.com/pzyber/Sidralune/wiki/Algorithms-and-accuracy).

## Running the tests

The solution has two MSTest projects (Visual Studio's own test framework, no NuGet packages):

| Project | Tests | Runs |
|---|---|---|
| `Sidralune.Tests` | Every library: about a hundred tests with a thousand checks | Before the application is built, in two to three minutes |
| `Sidralune.AppTests` | The application: settings, background tasks, scripting, planner, controls, the Observatory workspace and every workspace in day and night mode | After the application is built, in about a minute |

In Visual Studio, build the solution, open **Test Explorer** from the **Test** menu and choose **Run All**. On the command line, run the library tests, build the application only when they pass, then run the application tests:

```
msbuild Sidralune.Tests\Sidralune.Tests.csproj
"C:\Program Files (x86)\Microsoft Visual Studio 14.0\Common7\IDE\CommonExtensions\Microsoft\TestWindow\vstest.console.exe" Sidralune.Tests\bin\Debug\Sidralune.Tests.dll
msbuild Sidralune.AppTests\Sidralune.AppTests.csproj
"C:\Program Files (x86)\Microsoft Visual Studio 14.0\Common7\IDE\CommonExtensions\Microsoft\TestWindow\vstest.console.exe" Sidralune.AppTests\bin\Debug\Sidralune.AppTests.dll
```

Building `Sidralune.AppTests` builds the application first. Use the `vstest.console.exe` of your Visual Studio version (for newer versions it is under `Common7\IDE\Extensions\TestPlatform`). If the repository is on a network drive, the test runner may refuse to load the test assembly (error `0x80131515`); copy the test project's `bin\Debug` folder to a local folder and run the tests there.

The tests need no network or equipment: devices are tested against the simulators and against stand-in servers on the local machine, and the application tests keep their settings in a temporary folder, never in yours. A few tests are skipped, with a note, when an optional dataset is not installed.

## Repository layout

| Path | Contents |
|---|---|
| `Sidralune` | The Windows application: main window, workspaces (`Views`), controls and theme (`UI`), script host (`Scripting`) |
| `Sidralune.Core` | Mathematics, time scales, coordinates, ephemerides, catalogues, minor bodies, satellites, visibility, utilities |
| `Sidralune.Imaging` | Image model, file formats, debayering, analysis, processing, registration, stacking, planetary tools |
| `Sidralune.Science` | Astrometry, photometry, spectroscopy, solar-system science, radio astronomy, online databases |
| `Sidralune.Devices` | Device interfaces, simulators, ASCOM, Alpaca, INDI, LX200, PHD2, capture and automation |
| `Sidralune.Tests` | Library tests (MSTest) |
| `Sidralune.AppTests` | Application tests (MSTest) |
| `Wiki` | The user manual |

## Documentation

Start at the [wiki home page](https://github.com/pzyber/Sidralune/wiki/Home). It has a page for every workspace, plus reference pages on file formats, algorithms, the command line and troubleshooting.

## Contributing

Bug reports, feature ideas and pull requests are welcome. Please read [CONTRIBUTING.md](CONTRIBUTING.md) first. It explains how to report problems, the project's rules (C# 6, no third-party dependencies, cited algorithms) and how to test your changes.

## License

Sidralune is released under the [MIT License](LICENSE).

## Disclaimer

Never point a telescope, finder or camera at the Sun without a certified solar filter over the front of the instrument. Permanent eye damage happens instantly and without pain.

Telescope automation moves real equipment. Watch your mount the first time you use slewing, meridian flips, parking or sequences, and set limits in your mount or driver.

Predictions of visibility, occultations, transits and passes depend on orbits, models and your settings. Check critical events against a second source before you travel or commit observing time. Sidralune is provided as is, without warranty of any kind.
