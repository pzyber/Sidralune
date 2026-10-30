The **Sky** workspace is Sidralune's planetarium and star atlas. It shows stars, deep-sky objects, planets, the Moon, comets and asteroids for your site and time, overlays the fields of view of your equipment, gives detailed information about any object, controls your telescope and prints finder charts.

## Contents

- [Layout](#layout)
- [Planetarium and Atlas modes](#planetarium-and-atlas-modes)
- [Moving around](#moving-around)
- [Finding objects](#finding-objects)
- [Fields of view](#fields-of-view)
- [Mirror and invert](#mirror-and-invert)
- [Layers](#layers)
- [Limiting magnitude](#limiting-magnitude)
- [What is drawn and how](#what-is-drawn-and-how)
- [Object information panel](#object-information-panel)
- [Right-click menu](#right-click-menu)
- [Controlling the telescope](#controlling-the-telescope)
- [Changing the time](#changing-the-time)
- [Printing finder charts](#printing-finder-charts)
- [Exporting images](#exporting-images)
- [Commands](#commands)

## Layout

- The **toolbar** across the top: mode switch, field-of-view presets, time controls, mirror and invert, layers, print and export, and the find box.
- The **chart** fills the middle.
- The **object panel** on the right shows the selected object, its action buttons, detailed information and an altitude graph for tonight.
- A caption at the bottom of the chart shows the local time, site, field of view and limiting magnitude.

## Planetarium and Atlas modes

Switch with **Planetarium** / **Atlas** on the toolbar.

| | Planetarium | Atlas |
|---|---|---|
| Orientation | Altitude-azimuth: the horizon is horizontal, as you see the sky | Equatorial: north up, east left, as in printed atlases |
| Ground and daylight | Drawn (ground below the horizon, blue sky by day) | Not drawn |
| Typical use | "What is up now and where?" | Star-hopping, framing, printed charts |

Both modes use a stereographic projection, which keeps the shapes of constellations and fields of view correct over a wide angle.

## Moving around

| Action | Mouse | Keyboard |
|---|---|---|
| Pan | Drag with the left button | Arrow keys |
| Zoom | Mouse wheel | + and −, or Page Up and Page Down |
| Centre on a point | Double-click | |
| Select an object | Click it | |
| Look north, east, south, west | | N, E, S, W (planetarium) |
| Look at the zenith | | Z (planetarium) |
| Context menu | Right-click | |

The chart can zoom from the whole sky (200°) down to a few arcminutes.

## Finding objects

- Type in the **find box** on the toolbar and press Enter. It accepts names (`Albireo`, `Ring Nebula`), designations (`M 57`, `NGC 6720`, `HIP 95947`), planets, comets and asteroids, and coordinates in the form `hh mm ss +dd mm ss`.
- Or press **Ctrl+K** anywhere for the [command palette](The-user-interface#command-palette).

The chart centres on the object and selects it. If a name is not found in the local catalogues, try **Databases → Online databases → Resolve name**, which asks the CDS Sesame service.

## Fields of view

The field-of-view list on the toolbar sets the zoom and draws matching indicators:

| Preset | Zoom | Indicator |
|---|---|---|
| **Naked eye (100°)** | 100° | None |
| **Wide (60°)** | 60° | None |
| **Binoculars (7°)** | 12° | 6.5° circle for 10×50 binoculars |
| **Finder (5°)** | 9° | 5° circle for an 8×50 finder, plus Telrad rings |
| **Low power (2°)** | 4° | None |
| **Eyepiece** | Fits the eyepiece field | Circle for the active telescope and eyepiece, labelled with the magnification |
| **Camera** | Fits the sensor | Rectangle for the active telescope and camera |
| **Custom** | Unchanged | None |

The eyepiece and camera indicators come from **Settings → Equipment**. For mosaics and rotated framing, use [Astrometry → Framing & mosaics](Astrometry-and-framing#framing-and-mosaics), which shares the same chart.

## Mirror and invert

Telescopes show the sky flipped. Match the chart to the view in your eyepiece:

| Button | Matches |
|---|---|
| **Mirror** | A refractor, SCT or Maksutov with a star diagonal (mirror image) |
| **Invert** | A Newtonian reflector or a straight-through finder (rotated 180°) |
| Both | A Newtonian reflector with a diagonal or similar setups |

The settings are also used when printing.

## Layers

**Layers** opens a menu of everything the chart can draw. Tick or untick each item:

| Group | Layers |
|---|---|
| Objects | Stars, Star names, Bayer / Flamsteed, Deep-sky objects, Deep-sky labels, Planets, Sun and Moon, Comets and asteroids |
| Constellations | Constellation lines, Constellation names, Constellation boundaries, Milky Way |
| Reference lines | Equatorial grid, Alt-azimuth grid, Ecliptic, Galactic equator, Meridian |
| Surroundings | Horizon and ground, Atmosphere (daylight sky), Cardinal points |
| Equipment | Telescope position, Field-of-view indicators, Telrad rings |
| Calculation | Refraction |

The **Limiting magnitude** submenu sets the faintest stars shown (4.0 to 12.0) and whether the chart goes deeper as you zoom in. The current choices are ticked.

## Limiting magnitude

The value chosen in **Layers → Limiting magnitude** (6.5 by default) is the faintest star magnitude shown for the whole sky. With **Deeper when zoomed in** ticked (the default), the limit grows by about 2.2 magnitudes for every tenfold zoom, so the chart always has a useful number of stars without clutter; untick it to keep exactly the chosen limit at every zoom. The limit in use is shown in the caption at the bottom of the chart.

Deep-sky objects have their own limit, and their labels appear progressively as you zoom in. With the PGC galaxies installed (**Settings → Data**), galaxies that are not NGC/IC objects appear once the field is 15° or narrower. With the PGC galaxy magnitudes installed too, those within the deep-sky magnitude limit are shown, brightest first; galaxies at least a third of the field width in arcminutes across (5′ at 15°) are shown as well, which covers those without a magnitude. They are labelled with their PGC number below 3°. Comets and asteroids are shown down to about two magnitudes fainter than the star limit (at most magnitude 15).

## What is drawn and how

- **Stars** are dots sized by brightness and tinted by colour index (B−V), so you can tell red giants from blue stars.
- **Deep-sky objects** use standard atlas symbols: ellipses at true size and position angle for galaxies, circles with crosses for globular clusters, dashed circles for open clusters, squares for nebulae, circles with a ring for planetary nebulae.
- **Planets** are drawn at their apparent size once it is larger than a dot; Saturn gets its rings, and when zoomed in below 1°, Jupiter shows its four Galilean moons in their current positions.
- **The Moon** is drawn with its correct phase and orientation; the Sun at its true size.
- **Comets** are drawn with a short tail pointing away from the Sun, **asteroids** as small crosses, both labelled when bright enough or when you zoom in. They come from the installed MPC files, the four built-in asteroids, and anything you added in the [Solar system](Solar-system#minor-bodies) workspace.
- **Milky Way** is shaded along the galactic plane at night.
- **Telescope position** is a crosshair at the connected mount's current pointing.
- In planetarium mode during the day, the sky is coloured by the Sun's altitude and fainter stars fade out, as in reality.
- Your **horizon profile** from **Settings → Location** is drawn as part of the ground.

## Object information panel

Click any object to select it. The panel shows its name and type, and these buttons:

| Button | Action |
|---|---|
| **Center** | Centre the chart on the object |
| **Slew** | Slew the connected telescope to it |
| **Add to list** | Add it to the active observing list in the [Planner](Observation-planning) |
| **Frame** | Open it in [Astrometry → Framing & mosaics](Astrometry-and-framing#framing-and-mosaics) |
| **Copy** | Copy its name and coordinates to the clipboard |

The details include, as applicable:

- rise, transit (with altitude) and set times, or "circumpolar" / "never rises",
- predicted **visibility** with your instrument (for example "Easy, margin +2.1 mag"), the **best magnification** and the sky brightness at the object's position,
- magnitude, size, J2000 and apparent RA/Dec, altitude and azimuth, airmass and hour angle,
- for stars: B−V and temperature, spectral type, distance from parallax, HIP and HD numbers, variability,
- for deep-sky objects: morphology, surface brightness, other names,
- for planets: distance, illuminated fraction, phase angle, elongation, apparent size, and central meridians (Jupiter's systems I, II and III; Mars, Saturn and Venus central meridian, sub-Earth latitude and pole angle),
- for the Moon: age in days,
- for comets and asteroids: distance from Earth and Sun, elongation, phase angle and orbit type.

The **Altitude tonight** graph below shows the object's altitude through the night, with the dark period shaded and a marker at the current time.

## Right-click menu

Right-clicking the chart shows the coordinates under the cursor and offers:

- **Select** and **Add … to observing list** for the object under the cursor,
- **Center here**,
- **Slew telescope here**,
- **Frame with camera here**.

## Controlling the telescope

With a mount connected (see [Observatory and devices](Observatory-and-devices)), **Slew** or **Slew telescope here** converts the J2000 position to the apparent position of date and sends the mount there. The telescope crosshair shows where the mount is pointing and moves with it.

> Watch the mount when you slew, especially the first time, and make sure cables and the tube cannot hit the pier.

## Changing the time

The toolbar has the time controls:

| Control | Action |
|---|---|
| ⏪ | Back one day |
| ◀ | Back one hour |
| **Now** | Return to live time |
| ▶ | Forward one hour |
| ⏩ | Forward one day |
| Play | Animate time at 300× (one hour every 12 seconds) |
| Date field | Type a local date and time and press Enter |

Changing the time here changes it for the whole program; see [Time](The-user-interface#time).

## Printing finder charts

Click the printer button to open a print preview of the current view. The printed chart:

- uses a black-on-white print palette that saves ink and reads well under a red torch,
- is rendered at three times screen resolution for crisp output,
- is printed in landscape with a title (the selected object's name), the date and time, site, field of view, limiting magnitude and orientation (normal or mirror view),
- has a legend of the symbols used,
- includes field-of-view circles and rectangles, comets and asteroids, and any mirror or invert setting.

Typical use: select the target, choose **Finder (5°)** for a finder chart and **Eyepiece** for a detailed chart, switch to **Atlas** mode, set mirror or invert to match your telescope, and print both.

## Exporting images

The save button writes the chart as a PNG at twice the screen resolution, in the current colours.

## Commands

The command palette (Ctrl+K) includes `Sky: planetarium mode`, `Sky: atlas mode`, `Sky: look north`, `Sky: look south`, `Sky: zenith` and `Sky: print finder chart`.
