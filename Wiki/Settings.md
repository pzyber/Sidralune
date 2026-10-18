The **Settings** workspace, at the bottom of the navigation rail, holds everything Sidralune needs to know about you, your sites and your equipment, plus the data manager. Settings are saved automatically to `%LOCALAPPDATA%\Sidralune\Settings\settings.json`.

## Contents

- [Location](#location)
- [Equipment](#equipment)
- [Observer & accounts](#observer--accounts)
- [Appearance](#appearance)
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
| **Night vision (red) mode** | Replaces every colour with deep reds. Same as the moon button and Ctrl+Shift+N |
| **Atmospheric refraction** | Apply refraction to altitudes, so objects near the horizon appear where you actually see them. Turn it off to compare with geometric (airless) tables |

A list of keyboard shortcuts is shown on this tab; see also [Keyboard shortcuts](Keyboard-shortcuts).

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
| **Update installed** | Downloads fresh copies of everything already installed. Do this every week or two for comets and asteroids |
| **Remove selected** | Deletes the ticked datasets. Sidralune falls back to its built-in data |
| **Open data folder** | Opens `%LOCALAPPDATA%\Sidralune\Data` in Explorer |
| **Reload catalogs** | Reloads every catalogue from disk, for example after copying files in by hand |

The status line shows which ephemeris and catalogues are loaded. See [Data and catalogues](Data-and-catalogues) for details of each dataset.

> If you have no internet access at the telescope, download the data at home; everything works offline afterwards. You can also copy the `Data` folder from another computer.

## About

Shows the version, the main algorithms Sidralune uses, the data credits and the folder where settings and data are stored.
