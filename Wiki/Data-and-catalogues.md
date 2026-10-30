Sidralune works out of the box with a small built-in catalogue and an analytic planetary theory. Downloading the optional datasets turns it into a full planetarium with 120,000 stars, every NGC/IC object, all known comets and milliarcsecond planetary positions. This page explains what each source provides and where the files live.

## Contents

- [Built-in data](#built-in-data)
- [Downloadable datasets](#downloadable-datasets)
- [Ephemeris sources](#ephemeris-sources)
- [Comets and asteroids](#comets-and-asteroids)
- [Satellites](#satellites)
- [Online services](#online-services)
- [Where files are stored](#where-files-are-stored)
- [Adding files by hand](#adding-files-by-hand)

## Built-in data

These are compiled into the program and always available:

| Data | Contents |
|---|---|
| Bright stars | 114 named stars with positions, magnitudes and colours |
| Deep-sky objects | 169 objects: the full Messier catalogue plus popular showpieces |
| Constellations | All 88, with names, genitives, abbreviations and label positions |
| Constellation lines | A built-in set of stick figures for the bright stars |
| Asteroids | (1) Ceres, (2) Pallas, (3) Juno and (4) Vesta, with JPL SBDB elements |
| Planets | Standish (JPL) Keplerian elements with Jupiter perturbations, and an ELP/Meeus lunar theory |
| Meteor showers | 13 major showers with radiants, speeds and ZHR |
| Radio sources | Bright calibrators such as Cassiopeia A, Cygnus A and Taurus A |
| Spectral lines | Stellar, nebular and telluric line lists and calibration lamp lines (neon, argon, mercury, hydrogen) |
| Citizen-science projects | A directory of 24 projects |

## Downloadable datasets

Open **Settings → Data** to download these. The first seven are recommended and ticked by default.

| Dataset | File | Size | Licence | What it adds |
|---|---|---|---|---|
| JPL DE440s planetary ephemeris | `de440s.bsp` | 31 MB | JPL/NASA public | Positions of the Sun, Moon and planets for 1849–2150 to milliarcseconds |
| HYG star database v4.1 | `hygdata_v41.csv` | 32 MB | CC BY-SA 4.0 | About 120,000 stars combining Hipparcos, the Yale Bright Star Catalogue and Gliese, with proper names, Bayer and Flamsteed designations, HD and HIP numbers, B−V colours, spectral types, parallaxes, proper motions and variable star data |
| OpenNGC | `NGC.csv` | 4 MB | CC BY-SA 4.0 | All NGC and IC objects with types, sizes, position angles, magnitudes, surface brightness, Hubble types and common names (by Mattia Verga) |
| OpenNGC addendum | `addendum.csv` | 40 KB | CC BY-SA 4.0 | Objects outside NGC/IC: Messier objects not in NGC, Caldwell objects, Barnard dark nebulae and others |
| PGC galaxies (HyperLEDA) | `pgc.dat.gz` | 19 MB | CDS / HyperLEDA, free for scientific use | The Principal Galaxies Catalogue (Paturel et al. 2003, VizieR VII/237): about 983,000 galaxies with positions, sizes, axis ratios, position angles, types and up to 12 other names each (UGC, MCG, CGCG, ESO…). It has no magnitudes of its own (see the next row). Kept compressed; loading it takes a few seconds in the background and about 80 MB of memory. Downloaded from CDS, or from its Harvard mirror when CDS cannot be reached |
| PGC galaxy magnitudes (HyperLEDA) | `pgc_bt.csv.gz` | 64 MB download, kept as 7 MB | HyperLEDA, free for scientific use | Total B magnitudes for about 917,000 of the PGC galaxies (93 %), from HyperLEDA (Makarov et al. 2014). HyperLEDA sends every galaxy it knows; only the PGC's number range is kept. With it, PGC galaxies show their magnitude and are picked by brightness in the sky chart and plate-solve labels |
| Constellation stick figures | `constellationship.fab` | 12 KB | GPL (Stellarium) | Western constellation lines between HIP stars |
| IAU constellation boundaries | `constellation_boundaries.dat` | 600 KB | GPL (Stellarium) | Official boundaries; also used to name the constellation of any position |
| MPC comet elements | `CometEls.txt` | 200 KB | MPC | Orbits of every known comet, updated daily by the Minor Planet Center |
| MPC near-Earth asteroids | `NEA.txt` | 7 MB | MPC | Orbits of all known near-Earth asteroids |
| MPCORB | `MPCORB.DAT` | 70 MB (gzip download) | MPC | The complete minor planet orbit file. Asteroids brighter than absolute magnitude H = 15.5 are loaded, which keeps memory use reasonable |
| Bright satellites | `visual.tle` | 30 KB | CelesTrak | Two-line elements for about 150 visually bright satellites |
| Space stations | `stations.tle` | 10 KB | CelesTrak | ISS, Tiangong and other crewed stations |

When a dataset is installed, it replaces the corresponding built-in data. HYG replaces the 114 built-in stars. OpenNGC replaces the built-in NGC, IC and Messier entries; built-in showpieces from other catalogues (Sharpless objects, the Magellanic Clouds and so on) are kept, and any Messier object missing from OpenNGC is added back.

## Ephemeris sources

| Source | When used | Accuracy |
|---|---|---|
| JPL DE440s | When `de440s.bsp` is installed, for 1849–2150 | Milliarcseconds; limited in practice by Earth orientation (ΔT, polar motion) and your site coordinates |
| Analytic theory | Otherwise, and outside the DE440s date range | About 2′ RMS for the planets over 1990–2045 when compared with JPL Horizons; arcseconds to tens of arcseconds for the Moon |

The status bar's data pill shows "DE440" or "Analytic ephemeris". The analytic theory is good enough for charts, planning and visual observing; use DE440s for occultations, astrometry, timing work and anything that depends on arcsecond accuracy.

## Comets and asteroids

Comets and asteroids from the MPC files appear everywhere: on the Sky chart (planetarium and atlas, and printed charts), in the command palette, the Tonight page's "Comets & asteroids" card, the minor-body browser, ephemerides, plate-solve annotations, occultation predictions and scripts.

Positions are computed by two-body Kepler propagation from the orbit's epoch, with light-time correction. Magnitudes use the H, G system for asteroids and the H, K (total magnitude) model for comets. Accuracy depends on how recent the orbit is, which is why you should run **Update installed** regularly.

You can also add individual objects without downloading the large files:

- **Solar sys. → Minor bodies → Fetch from JPL SBDB** looks up any comet or asteroid by name or designation in JPL's Small-Body Database.
- **Solar sys. → Minor bodies → Add elements…** accepts one line in MPCORB or CometEls format, for example copied from an MPC circular or the MPC's orbit pages.

Objects added this way last until Sidralune is closed. Adding an object that is already listed replaces its orbit rather than listing it twice, and **Remove added** takes added objects away again.

## Satellites

Satellite passes use the SGP4 model with CelesTrak two-line elements. Elements age quickly (low satellites drift by kilometres per day), so update them before each session. See [Solar system → Satellites](Solar-system#satellites).

## Online services

These are only contacted when you use a feature that needs them:

| Service | Used for |
|---|---|
| CDS Sesame | Resolving any object name to coordinates |
| CDS SIMBAD (TAP) | Object information, cone searches, ADQL queries |
| CDS VizieR (TAP) | Gaia DR3 stars for plate solving, photometric colour calibration and occultations; any VizieR catalogue via ADQL |
| NASA Exoplanet Archive (TAP) | Exoplanet parameters and transit predictions |
| JPL Horizons | Ephemeris comparison |
| JPL SBDB and CNEOS | Minor-body orbits, close approaches |
| AAVSO VSX and VSP | Variable star data and comparison-star sequences |
| nova.astrometry.net | Online plate solving (needs a free API key) |
| CelesTrak | Fresh satellite elements |

Gaia DR3 fields downloaded for plate solving are cached in `%LOCALAPPDATA%\Sidralune\Cache`, so solving the same field again works offline.

## Where files are stored

```
%LOCALAPPDATA%\Sidralune
├── Settings
│   ├── settings.json      sites, equipment, observer details, preferences
│   └── planner.json       observing lists and logbook
├── Data
│   ├── Catalogs           HYG, OpenNGC, constellation files, MPC orbits, TLEs
│   ├── Ephemerides        de440s.bsp
│   └── Scripts            saved C# scripts
├── Cache                  cached online query results
└── Logs                   daily log files and crash reports
```

**Settings → About** shows the exact path on your computer, and **Settings → Data → Open data folder** opens it.

## Adding files by hand

If the computer at the telescope has no internet connection, download the files elsewhere (their URLs are listed in `Sidralune.Core/Util/DataManager.cs`) and copy them into the folders above with the file names shown in the table. Then click **Settings → Data → Reload catalogs**. Gzip-compressed downloads (MPCORB) must be decompressed first.
