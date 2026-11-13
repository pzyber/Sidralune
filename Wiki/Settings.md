The **Settings** workspace, at the bottom of the navigation rail, holds everything Sidralune needs to know about you, your sites and your equipment, plus the data manager. Settings are saved automatically to `%LOCALAPPDATA%\Sidralune\Settings\settings.json`.

## Contents

- [Location](#location)
- [Equipment](#equipment)
- [Observer & accounts](#observer--accounts)
- [Appearance](#appearance)
  - [Languages](#languages)
- [Colour](#colour)
- [Data](#data)
- [About](#about)

## Location

A site describes where you observe and what your sky is like. You can keep as many sites as you like and switch between them.

The list on the left shows your sites. **New site** adds one, **Delete** removes the selected one, **Save site** stores your edits and **Use this site** makes it the active site used by every calculation.

| Field | Meaning | Used by |
|---|---|---|
| **Name** | A label for the site | Clock, printed charts, reports |
| **Latitude (°, N positive)** | Geodetic latitude. Decimal (`51.4769`) or sexagesimal (`51 28 38 N`) | Everything |
| **Longitude (°, E positive)** | Decimal (`-0.0005`) or sexagesimal (`0 00 02 W`) | Everything |
| **Elevation (m)** | Height above sea level | Topocentric positions, satellite passes, occultations |
| **Time zone** | Windows time zone, including daylight saving rules | Every displayed time |
| **Sky brightness (SQM, mag/arcsec²)** | Zenith sky brightness on a moonless night | Visibility predictions, exposure calculations, meteor rates |
| **Bortle class** | Derived from the SQM value | Display only |
| **Extinction k(V) (mag/airmass)** | How much the atmosphere dims stars per airmass | Visibility predictions |
| **Typical seeing (arcsec)** | Typical star FWHM | Visibility of small objects, focal length recommendations |
| **Temperature (°C) for refraction** / **Pressure (hPa)** | Typical night-time conditions | Refraction near the horizon |
| **MPC observatory code** | Your Minor Planet Center station code | MPC reports |
| **Horizon profile (az:alt, …)** | Pairs of azimuth and altitude in degrees, for example `0:10, 45:18, 90:25, 180:5, 270:15`. Values in between are interpolated | Sky chart ground, planner "up" times, best targets |

> If you do not know your SQM value, the [Getting started](Getting-started#step-1-set-your-observing-site) page has a table of typical values. Online light-pollution maps also give SQM estimates for any location.

## Equipment

Three lists describe your equipment. Select an entry to edit its properties in the grid below the list. **Add** creates an entry, **Remove** deletes it and **Use** makes it active (marked with ●).

### Telescopes

| Property | Meaning |
|---|---|
| **Name** | For example `8" f/6 Dobsonian` |
| **Aperture mm** | Clear aperture |
| **Focal mm** | Focal length, including any reducer or Barlow you normally use |
| **Obstruction** | Central obstruction as a fraction of the aperture diameter (0.22 for a typical Newtonian, 0.33 for an SCT, 0 for a refractor) |
| **Transmission** | Fraction of light reaching the eye or sensor (0.85 for a reflector with an eyepiece, 0.92 for a good refractor) |

### Eyepieces

| Property | Meaning |
|---|---|
| **Name** | For example `10 mm 82°` |
| **Focal mm** | Focal length |
| **Afov deg** | Apparent field of view |

### Cameras

| Property | Meaning |
|---|---|
| **Name** | For example `IMX571 APS-C` |
| **Pixel um** | Pixel size in micrometres |
| **Width** / **Height** | Sensor size in pixels |
| **Read noise** | Read noise in electrons at the gain you use |
| **Qe** | Peak quantum efficiency (0–1) |
| **Color** | True for one-shot-colour (Bayer) sensors |

### Combination summary

The summary at the bottom combines the active telescope with the active eyepiece and camera:

- **Visual**: magnification, true field of view, exit pupil and Dawes resolution limit.
- **Imaging**: image scale in arcseconds per pixel, field of view, focal ratio, the optimal sub-exposure length for your sky (when read noise becomes negligible compared with sky noise) and the focal ratio that samples your pixels at the Nyquist limit for planetary imaging.

## Observer & accounts

| Field | Meaning |
|---|---|
| **Age (affects dark-adapted pupil)** | The eye's maximum pupil shrinks with age (about 7.8 mm at 20, about 0.45 mm less per decade), which limits useful low magnifications and changes visibility predictions |
| **Visual experience** | Beginner, Average, Experienced or Expert. Experienced observers detect fainter, lower-contrast objects; the visibility model accounts for this |
| **AAVSO observer code** | Written into AAVSO photometry reports |
| **Default MPC station code** | Used for MPC astrometry reports when the site has no code |
| **nova.astrometry.net API key** | Needed only for online plate solving via astrometry.net. Get a free key from your nova.astrometry.net profile. Stored locally and hidden on screen |
| **Image folder** | Default folder for captured images |

Click **Save** to store the changes.

## Appearance

| Setting | Meaning |
|---|---|
| **Language** | The interface language. **Same as Windows** follows the Windows display language and falls back to English if Sidralune has no translation for it. The new language is used after a restart; **Restart now to change the language** appears when you choose another one, and closes Sidralune and starts it again in the new language |
| **Night vision (red) mode** | Replaces every colour with deep reds. Same as the moon button and Ctrl+Shift+N; this box and the moon button always show the same state |
| **Atmospheric refraction** | Apply refraction to altitudes, so objects near the horizon appear where you actually see them. Turn it off to compare with geometric (airless) tables |

A list of keyboard shortcuts is shown on this tab; see also [Keyboard shortcuts](Keyboard-shortcuts).

### Languages

Sidralune is written in English and translated into:

| Language | Code | Language | Code |
|---|---|---|---|
| Albanian (Shqip) | `sq` | Italian (Italiano) | `it` |
| Bulgarian (Български) | `bg` | Latin (Latina) | `la` |
| Croatian (Hrvatski) | `hr` | Latvian (Latviešu) | `lv` |
| Czech (Čeština) | `cs` | Lithuanian (Lietuvių) | `lt` |
| Danish (Dansk) | `da` | Macedonian (Македонски) | `mk` |
| Dutch (Nederlands) | `nl` | Norwegian Bokmål (Norsk bokmål) | `nb` |
| Estonian (Eesti) | `et` | Polish (Polski) | `pl` |
| Faroese (Føroyskt) | `fo` | Portuguese (Português, European) | `pt` |
| Finnish (Suomi) | `fi` | Romanian (Română) | `ro` |
| French (Français) | `fr` | Serbian (Srpski, Latin script) | `sr` |
| German (Deutsch) | `de` | Slovak (Slovenčina) | `sk` |
| Greek (Ελληνικά) | `el` | Slovene (Slovenščina) | `sl` |
| Hungarian (Magyar) | `hu` | Spanish (Español) | `es` |
| Icelandic (Íslenska) | `is` | Swedish (Svenska) | `sv` |

Norwegian Nynorsk uses Bokmål, and Brazilian Portuguese uses European Portuguese. Serbian is written in the Latin script. Windows has no settings of its own for Latin, so the few places that show day or month names show them in English. Swedish was written first and checked most closely; the others are new, so corrections from native speakers are very welcome (see [CONTRIBUTING.md](https://github.com/pzyber/Sidralune/blob/main/CONTRIBUTING.md#translating-sidralune)).

The whole interface is translated:

- the main window, the command palette and Settings, including the downloadable datasets in Settings → Data and the loaded catalogues in the status bar,
- **Tonight** and **Sky**, including the object information, the layers menu and the printed star chart and finder chart,
- **Planner** and **Observatory**, including the sequencer's instructions and triggers and the messages from autofocus, centring and polar alignment,
- **Deep sky**, **Process** and **Planetary**, including the progress messages and results of stacking, pre-processing and lucky imaging,
- **Astrometry**, **Photometry** and **Spectra**, including the messages from the plate solvers (spectral line names such as Hα and [O III] stay as they are),
- **Solar system**, **Radio**, **Databases** and **Scripts** (results that online services return, such as SIMBAD tables, and the scripting API stay as they are),
- the option names and choices in the property grids (stacking, planetary, photometry, equipment profiles, observing log and sequencer settings), and Yes/No for on/off options,
- planet names, object types, visibility ratings, moon phases, eclipses and other events, and meteor shower names, wherever they appear.

A language whose file lacks some texts shows those in English.

Only words change with the language. Numbers are always written with a decimal point, and dates as `yyyy-MM-dd`, so values look the same as in reports, FITS headers and scripts; day and month names such as `mån 2 nov` do follow the language. Constellation and star names stay as they are. Searching accepts the translated planet names as well as the English ones. Log messages and files written for other programs (AAVSO and MPC reports, FITS headers) stay in English.

The **Pseudo-language** entry is for testing: it shows every translated text with accents, in brackets and a third longer, so text that is not yet translated, and places where a longer translation would not fit, stand out. To help translate Sidralune into your language, see [CONTRIBUTING.md](https://github.com/pzyber/Sidralune/blob/main/CONTRIBUTING.md#translating-sidralune).

## Colour

How images are shown and how their colour profiles are handled. See [Colour management](Colour-management) for what these mean and how to use them.

| Setting | Meaning |
|---|---|
| **Colour management** | Show images through the monitor's colour profile. On by default; off shows the values as they are |
| **Working profile** | The profile images without one (FITS, raw frames, stacks) are taken to be in. ProPhoto RGB by default; the list has the built-in profiles, those installed in Windows and **Other file…** |
| **Monitor** | The profile Windows uses for the monitor this window is on, or a note when Windows manages colour on that display itself (HDR or automatic colour management) |
| **Rendering intent** | How colours are brought onto the monitor and into another profile: **Relative colorimetric** (default), **Perceptual**, **Saturation** or **Absolute colorimetric** |
| **Image with another profile** | What happens when an opened image's profile differs from the working profile: **Keep its profile** (default), **Convert to the working profile**, **Discard the profile** or **Ask** |
| **Image without a profile** | **Leave it untagged** (default), **Assign the working profile** or **Ask** |
| **Saving** | **Embed the image's profile in saved files** (on by default) |
| **8-bit PNG and JPEG** | **Keep the image's profile** (default) or **Convert to sRGB** (best for the web) |
| **Proofing profile** | The profile **Proof colours** simulates: sRGB by default, or any installed RGB, CMYK or printer profile |
| **Proofing intent** | How colours are brought into the proofing profile (Relative colorimetric by default) |
| **Gamut warning colour** | The colour that marks out-of-gamut pixels (grey by default) |

## Data

The data manager lists the catalogues, orbit files and ephemerides you can download. Nothing is downloaded until you ask.

| Column | Meaning |
|---|---|
| ☐ | Tick the datasets to download or remove |
| **Dataset** | Name |
| **Description** | What the dataset adds |
| **Size** | Approximate download size |
| **Status** | Installed (with date) or not installed |
| **License** | The dataset's licence |

| Button | Action |
|---|---|
| **Download selected** | Downloads and installs the ticked datasets, then loads them |
| **Update installed** | Downloads fresh copies of everything already installed. Do this every week or two for comets and asteroids. Each dataset is replaced only once its new copy is complete, so a failed or stopped update keeps the installed one; the DE440s ephemeris in use is swapped for the new copy straight away |
| **Stop** | Ends a download. A dataset that was not completely downloaded is not kept |
| **Remove selected** | Deletes the ticked datasets. Sidralune falls back to its built-in data |
| **Open data folder** | Opens `%LOCALAPPDATA%\Sidralune\Data` in Explorer |
| **Reload catalogs** | Reloads every catalogue from disk, for example after copying files in by hand |

When a server cannot be reached or reports an error, the other datasets are still downloaded and loaded, and a message names each dataset that could not be downloaded and why; those keep their installed copy, so try them again later. A server that fails for a moment is asked once more before giving up, and a dataset with a mirror tries the mirror.

The status line shows which ephemeris and catalogues are loaded. See [Data and catalogues](Data-and-catalogues) for details of each dataset.

> If you have no internet access at the telescope, download the data at home; everything works offline afterwards. You can also copy the `Data` folder from another computer.

## About

Shows the version, the copyright, the main algorithms Sidralune uses, the data credits and the folder where settings and data are stored.
