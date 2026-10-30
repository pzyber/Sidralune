The **Databases** workspace searches the local catalogues with filters, queries the major online astronomical databases, and lists citizen-science projects where your observations are useful. It has three tabs: **Local catalogues**, **Online databases** and **Amateur science**.

## Contents

- [Local catalogues](#local-catalogues)
- [Online databases](#online-databases)
  - [Resolving names](#resolving-names)
  - [AAVSO VSX](#aavso-vsx)
  - [SIMBAD cone search](#simbad-cone-search)
  - [ADQL queries](#adql-queries)
  - [Working with results](#working-with-results)
- [Amateur science](#amateur-science)
- [Other places that use online data](#other-places-that-use-online-data)

## Local catalogues

Search the catalogues installed on your computer, offline.

| Filter | Meaning |
|---|---|
| Catalogue | **Deep-sky objects**, **Stars** (all), **Named stars** or **Constellations** |
| Type | **Any type**, or a single object type (galaxy, open cluster, globular cluster, planetary nebula, emission nebula, double star…) |
| **Const.** | Three-letter constellation abbreviation, for example `Cyg` |
| **Mag ≤** | Faintest magnitude |
| **Size ≥ (′)** | Smallest size in arcminutes (deep-sky objects) |
| **Name** | Part of a name or designation |

Click **Search**. Results (up to 5000) are sorted by brightness.

| Catalogue | Columns |
|---|---|
| Deep-sky objects | Object, type, constellation, RA, Dec, magnitude, size, surface brightness, current altitude, other names |
| Stars | Star, constellation, RA, Dec, V magnitude, B−V, spectral type, parallax, variable type, current altitude |
| Constellations | Abbreviation, name, genitive, label position, current altitude |

Double-click a row to show it in the Sky chart. Select rows (Ctrl or Shift for several) and click **Add to plan** to add them to the active [observing list](Observation-planning). **CSV…** exports the results.

Examples:

- all galaxies in Virgo brighter than magnitude 11: catalogue Deep-sky objects, type Galaxy, Const. `Vir`, Mag ≤ 11,
- large nebulae for a short focal length: type EmissionNebula, Size ≥ 30,
- bright red stars: catalogue Stars, sort by B−V.

## Online databases

These features need an internet connection.

### Resolving names

Type any object name in **Object** and click **Resolve name (Sesame)**. The CDS Sesame service looks it up in SIMBAD, NED and VizieR and returns its coordinates, type, V magnitude, spectral type and all its aliases (for example every catalogue designation of a star). The object becomes the selected object, so you can show it in the Sky chart or add it to a plan even if it is not in your local catalogues.

### AAVSO VSX

**AAVSO VSX** looks up a variable star in the AAVSO Variable Star Index: its type, magnitude range, period, epoch, spectral type and other data. Useful before planning photometry.

### SIMBAD cone search

**SIMBAD cone (selection)** lists up to 200 SIMBAD objects within 0.3° of the selected object, with their type, spectral type and V magnitude, sorted by brightness. Use it to identify what else is in your field.

### ADQL queries

ADQL (Astronomical Data Query Language) is a SQL dialect used by astronomical databases through the TAP protocol. Choose an **ADQL service**:

| Service | Contains |
|---|---|
| **SIMBAD TAP** | Basic data, identifiers, fluxes, object types, bibliography for millions of objects outside the solar system |
| **VizieR TAP** | Over 20,000 published catalogues, including Gaia DR3, 2MASS, UCAC4, APASS, Tycho-2 and the Washington Double Star Catalogue |
| **NASA Exoplanet Archive TAP** | Confirmed exoplanets with orbital, transit and host-star parameters |

Type a query in **ADQL**, or pick one from **Examples**, and click **Run query**. The examples are:

| Example | Query |
|---|---|
| SIMBAD: objects within 0.5° of M 42 | Cone search around the Orion Nebula |
| SIMBAD: Mira variables brighter than V=8 | Join of the basic and flux tables, filtered by object type |
| VizieR: Gaia DR3 stars G<12 near Vega | Gaia DR3 cone search with magnitude, colour and parallax |
| VizieR: Washington Double Star catalogue near Albireo | Double stars around β Cyg |
| Exoplanet Archive: transiting planets with host V < 10 | Bright transiting systems for amateur follow-up |

A minimal example to start from:

```sql
SELECT TOP 100 main_id, ra, dec, otype_txt
FROM basic
WHERE CONTAINS(POINT('ICRS', ra, dec), CIRCLE('ICRS', 202.47, 47.20, 0.5)) = 1
```

### Working with results

- The grid shows up to 20,000 rows; the panel on the right shows the row and column count and messages.
- Double-click a row that has RA and Dec columns (in degrees) to show that position in the Sky chart.
- **CSV…** saves the results.
- **Plot columns…** asks for two column names and opens a scatter plot of one against the other (magnitude axes are inverted automatically), for example `bp_rp,Gmag` for a colour-magnitude diagram of a Gaia cone search.

## Amateur science

The **Amateur science** tab lists 24 projects where amateur observations are used by professional astronomers. Double-click a row to open the project's website in your browser.

| Field | Projects | What Sidralune exports |
|---|---|---|
| Variable stars | AAVSO, BAA Variable Star Section | AAVSO Extended Format from [Photometry](Photometry-and-exoplanets#exporting-results) |
| Exoplanets | NASA Exoplanet Watch, ExoClock (Ariel), Exoplanet Transit Database | Light curves (CSV) and transit fits |
| Minor planets | Minor Planet Center, NEO Confirmation Page | MPC 80-column and ADES from [Solar system](Solar-system#mpc-astrometry) |
| Occultations | IOTA, IOTA/ES, Occult Watcher Cloud | Event predictions and timings |
| Planets | ALPO, PVOL, JUPOS | Stacked and derotated images with WinJUPOS-compatible names from [Planetary](Planetary,-lunar-and-solar-imaging) |
| Sun | SIDC / SILSO sunspot number | |
| Meteors | IMO, Global Meteor Network | Shower rate predictions |
| Spectroscopy | BeSS, ARAS | BeSS FITS from [Spectroscopy](Spectroscopy#saving) |
| Supernovae | Transient Name Server | |
| Radio | Radio JOVE, Society of Amateur Radio Astronomers | Spectra and time series (CSV) from [Radio](Radio-astronomy) |
| Online | Zooniverse space projects, Kilonova Seekers | |
| Dark skies | Globe at Night | Your SQM readings |

## Other places that use online data

| Feature | Service |
|---|---|
| Plate solving with Gaia DR3 | VizieR |
| Photometric colour calibration | VizieR (Gaia DR3) |
| Asteroid occultation search | VizieR (Gaia DR3) |
| AAVSO comparison stars | AAVSO VSP |
| Transit predictions | NASA Exoplanet Archive |
| Minor-body lookup and close approaches | JPL SBDB and CNEOS |
| Ephemeris comparison | JPL Horizons |
| Online plate solving | nova.astrometry.net |

All queries run in the background, and Sidralune identifies itself to the services with a `Sidralune/0.2.0` user agent. Only what each query needs is sent: names, coordinates or ADQL text, and for astrometry.net the image being solved and your API key.
