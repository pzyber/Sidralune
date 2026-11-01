The **Sky** workspace is Sidralune's planetarium and star atlas. It shows stars, deep-sky objects, planets, the Moon, comets and asteroids for your site and time, overlays the fields of view of your equipment, gives detailed information about any object, controls your telescope and prints finder charts and all-sky star charts.

## Contents

- [Layout](#layout)
- [Planetarium and Atlas modes](#planetarium-and-atlas-modes)
- [Star chart](#star-chart)
- [Moving around](#moving-around)
- [Finding objects](#finding-objects)
- [Fields of view](#fields-of-view)
- [Mirror and invert](#mirror-and-invert)
- [Layers](#layers)
- [Limiting magnitude](#limiting-magnitude)
- [What is drawn and how](#what-is-drawn-and-how)
- [Planets' moons](#planets-moons)
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

Switch with **Planetarium** / **Atlas** on the toolbar. The third tab, **Chart**, is the fixed all-sky [star chart](#star-chart).

| | Planetarium | Atlas |
|---|---|---|
| Orientation | Altitude-azimuth: the horizon is horizontal, as you see the sky | Equatorial: north up, east left, as in printed atlases |
| Ground and daylight | Drawn (ground below the horizon, blue sky by day) | Not drawn |
| Typical use | "What is up now and where?" | Star-hopping, framing, printed charts |

Both modes use a stereographic projection, which keeps the shapes of constellations and fields of view correct over a wide angle.

## Star chart

The **Chart** tab shows a classic all-sky star chart for your site and the current time, laid out as a page to read on screen or to print and take outside. It is fixed: there is nothing to pan or zoom, and it follows the time controls.

**The circle** is the whole sky above your horizon. The zenith is in the middle and the horizon is the edge. North is at the top and east is on the **left**, because the chart is meant to be held up over your head. Round the edge are tick marks every 5° and the directions: NORTH, EAST, SOUTH and WEST, with NE, SE, SW and NW between them. The circle uses the same stereographic projection as the other modes, so constellations keep their shapes, although they look larger near the horizon.

**Labels turn towards the zenith.** Every name is turned so that its top points at the middle of the chart. To use the chart, face a direction and hold that edge of the chart down. The direction name and everything near that horizon then read the right way up, and the chart matches the sky in front of you. Labels near the middle stay upright on the page.

The chart shows:

- stars to a limit that suits the size of the chart. On screen it follows the window. In print it is about magnitude 5.5 on A4, 6.0 on A3 and A3+, and 6.5 on A2. To choose the limit yourself, turn off **Layers → Limiting magnitude → Deeper when zoomed in** and pick a value.
- names of the brighter stars, constellation names in capitals, and Bayer letters (α, β…) when **Bayer / Flamsteed** is on,
- constellation lines, the Milky Way, the ecliptic, and the 30° and 60° altitude circles,
- Messier objects and the brighter NGC objects, with the same symbols as the atlas,
- the planets, the Sun when it is up, and the Moon with its phase, its lit side towards the Sun,
- your site's horizon profile (trees, buildings), shaded in from the edge.

The **Layers** menu turns these on and off as in the other modes. Click an object to select it, and the object panel shows its details. Searching marks the object on the chart without moving anything. An object below the horizon has no mark.

**Beside or below the circle**, depending on which leaves the larger circle, the page lists:

| Block | Contents |
|---|---|
| The same sky | Other dates (the 1st and 15th of the months round the chart's date) and the local time on each when the stars stand the same. That is about 4 minutes earlier each day, or 2 hours a month, with summer time allowed for. |
| Sun | Sunset, the start and end of astronomical darkness, and sunrise |
| Moon | Phase, how much of it is lit, moonrise and moonset, and the constellation it is in |
| Planets tonight | Each planet that is up at some time between sunset and sunrise, with its magnitude, constellation, rise (↑) and set (↓) |
| Key | Star dots for each whole magnitude, as drawn on this chart, and the symbols |
| Worth a look | The brightest Messier objects at least 25° up |
| Meteor showers | Showers active on the date, with their peak and ZHR |

The title gives the site, date, time and coordinates. The footer gives the projection and the magnitude limit, and a reminder of how to hold the chart.

### Printing and saving the star chart

On the **Chart** tab, a paper list replaces the field-of-view presets and the mirror and invert buttons. It offers A4, A3, A3+ (329 × 483 mm, also called Super A3 or 13 × 19 in) and A2, each in portrait or landscape.

- **Print** (the printer button) opens the print dialog with the chosen paper selected. If the printer has that size under any name, its own paper is used; otherwise a custom size is set. Pick the printer there, then check the page in the preview and print it. The chart is laid out again for the paper and margins the printer reports. It is sent as vector graphics, so lines and text are as sharp as the printer allows (300, 600 dpi or more), in black on white with 10 mm margins.
- **Export** (the save button) writes the page as a 300 dpi PNG of the chosen paper, also in black on white, with 8 mm margins. For example, A4 is 2480 × 3508 pixels and A3+ is 3886 × 5705.

Text and symbols are a little larger on larger paper, so a chart on the wall reads from further away. Larger paper also shows more stars.

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

The chart can zoom from the whole sky (200°) down to 36″ (0.01°), close enough to see Charon beside Pluto.

## Finding objects

- Type in the **find box** on the toolbar and press Enter. It accepts names (`Albireo`, `Ring Nebula`), designations (`M 57`, `NGC 6720`, `HIP 95947`), planets and their moons (`Titan`, `Oberon`), comets and asteroids, and coordinates in the form `hh mm ss +dd mm ss`.
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

The value chosen in **Layers → Limiting magnitude** (6.5 by default) is the faintest star magnitude shown for the whole sky. With **Deeper when zoomed in** ticked (the default), the limit grows by about 2.2 magnitudes for every tenfold zoom, so the chart always has a useful number of stars without clutter. Below a 1° field it grows faster, so at the narrowest field (36″) every star in the installed catalogue is shown, whatever limit you chose. Untick it to keep exactly the chosen limit at every zoom. The limit in use is shown in the caption at the bottom of the chart.

Deep-sky objects have their own limit, and their labels appear progressively as you zoom in. With the PGC galaxies installed (**Settings → Data**), galaxies that are not NGC/IC objects appear once the field is 15° or narrower. With the PGC galaxy magnitudes installed too, those within the deep-sky magnitude limit are shown, brightest first; galaxies at least a third of the field width in arcminutes across (5′ at 15°) are shown as well, which covers those without a magnitude. They are labelled with their PGC number below 3°. Comets and asteroids are shown down to about two magnitudes fainter than the star limit (at most magnitude 15).

## What is drawn and how

- **Stars** are dots sized by brightness and tinted by colour index (B−V), so you can tell red giants from blue stars. As in a printed atlas, the sizes follow the limit in use: the faintest star shown gets the smallest dot and the brightest the largest, whatever the limit. A deeper limit fits its extra stars in as small dots instead of making every star bigger, and zoomed in all the way every limit gives the same chart. As in an eyepiece of higher power, the stars grow as you zoom in: evenly with each tenfold zoom from 90°, to 2.5 times their size at the narrowest field.
- **Deep-sky objects** use standard atlas symbols: ellipses at true size and position angle for galaxies, circles with crosses for globular clusters, dashed circles for open clusters, squares for nebulae, circles with a ring for planetary nebulae.
- **Planets** are drawn at their apparent size once it is larger than a dot; Saturn gets its rings.
- **Moons** of Mars, Jupiter, Saturn, Uranus, Neptune and Pluto appear when the field is narrower than 3°, at their current places around the planet (see [Planets' moons](#planets-moons)). Click a moon to select it, as any other object.
- **The Moon** is drawn with its correct phase and orientation; the Sun at its true size.
- **Comets** are drawn with a short tail pointing away from the Sun, **asteroids** as small crosses, both labelled when bright enough or when you zoom in. They come from the installed MPC files, the four built-in asteroids, and anything you added in the [Solar system](Solar-system#minor-bodies) workspace.
- **Milky Way** is shaded along the galactic plane at night.
- **Telescope position** is a crosshair at the connected mount's current pointing.
- In planetarium mode during the day, the sky is coloured by the Sun's altitude and fainter stars fade out, as in reality.
- Your **horizon profile** from **Settings → Location** is drawn as part of the ground.

## Planets' moons

The chart has 65 moons of the planets:

| Planet | Moons |
|---|---|
| Mars | Phobos, Deimos |
| Jupiter | Io, Europa, Ganymede, Callisto, Amalthea, Thebe, Metis, Adrastea, Himalia, Elara, Lysithea, Leda, Pasiphae, Sinope, Carme, Ananke |
| Saturn | Mimas, Enceladus, Tethys, Dione, Rhea, Titan, Hyperion, Iapetus, Phoebe, Janus, Epimetheus, Helene, Telesto, Calypso, Atlas, Prometheus, Pandora, Pan |
| Uranus | Miranda, Ariel, Umbriel, Titania, Oberon, Cordelia, Ophelia, Cressida, Desdemona, Juliet, Puck |
| Neptune | Triton, Nereid, Proteus, Larissa, Galatea, Despina, Thalassa, Naiad, Halimede, Sao, Laomedeia, Psamathe, Neso |
| Pluto | Charon, Nix, Hydra, Kerberos, Styx |

- A moon is drawn as a dot sized by its brightness, with its name once it is clear of the planet. It is left out while it is too close to the planet to tell apart and while it is hidden behind it; a moon crossing the disc is drawn dimmer.
- With **Deeper when zoomed in** ticked (the default), every moon is drawn once it is clear of its planet, however faint. With it unticked, the classical moons (the Galilean moons, Saturn's eight largest, Uranus' five, Triton, Phobos, Deimos and Charon) are always drawn and the others down to two magnitudes fainter than the chosen limit. A moon you select or search for is always drawn.
- Searching for a moon centres it and zooms in until the moon and its planet are both in view and apart.
- Positions come from orbits fitted to JPL's ephemerides and are within 3″ of JPL Horizons from 2000 to 2050, most within 1″. Outside those years the regular moons stay close and the irregular outer moons are rough; see [Algorithms and accuracy](Algorithms-and-accuracy).

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
- for the planets' moons: where it is around its planet (how far east or west, in arcseconds or arcminutes and in planet radii, and whether it is in front, behind, crossing the disc or hidden), distance, diameter and apparent size, and orbital period, marked retrograde for moons that orbit against the planet's spin, such as Triton. Rise, set and the altitude graph are the planet's,
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

In Planetarium and Atlas modes, click the printer button to open a print preview of the current view. (On the Chart tab, it prints the star chart instead: see [Printing and saving the star chart](#printing-and-saving-the-star-chart).) The printed chart:

- uses a black-on-white print palette that saves ink and reads well under a red torch,
- is rendered at three times screen resolution for crisp output,
- is printed in landscape with a title (the selected object's name), the date and time, site, field of view, limiting magnitude and orientation (normal or mirror view),
- has a legend of the symbols used,
- includes field-of-view circles and rectangles, comets and asteroids, and any mirror or invert setting.

Typical use: select the target, choose **Finder (5°)** for a finder chart and **Eyepiece** for a detailed chart, switch to **Atlas** mode, set mirror or invert to match your telescope, and print both.

## Exporting images

In Planetarium and Atlas modes, the save button writes the chart as a PNG at twice the screen resolution, in the current colours. On the Chart tab it saves the star chart page at 300 dpi.

## Commands

The command palette (Ctrl+K) includes `Sky: planetarium mode`, `Sky: atlas mode`, `Sky: star chart mode`, `Sky: look north`, `Sky: look south`, `Sky: zenith`, `Sky: print finder chart` and `Sky: print star chart`.
