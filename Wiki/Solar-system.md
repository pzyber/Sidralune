The **Solar sys.** workspace gathers everything about the Sun's family: ephemerides for any planet, comet or asteroid; a browser of minor bodies with an orbit diagram; planetary physical data and Jupiter's moons; solar and lunar eclipses; asteroid and lunar occultations; meteor showers; satellite passes; and measuring and reporting asteroid and comet positions to the Minor Planet Center.

## Contents

- [Ephemeris](#ephemeris)
- [Minor bodies](#minor-bodies)
- [Planets & moons](#planets--moons)
- [Eclipses](#eclipses)
  - [The eclipse map](#the-eclipse-map)
- [Occultations](#occultations)
- [Meteors](#meteors)
- [Satellites](#satellites)
- [MPC astrometry](#mpc-astrometry)

## Ephemeris

An ephemeris is a table of an object's position and circumstances over time.

1. Choose or type the object: the Sun, Moon, planets and Pluto, and every comet and asteroid loaded (the list is searchable as you type).
2. Set the start date and time (UTC), the step (10 minutes, 1 hour, 6 hours, 1 day or 7 days) and the number of rows.
3. Click **Compute**. **Stop**, next to it, ends a long ephemeris part way.

| Column | Meaning |
|---|---|
| **Local time** | The row's time in your time zone |
| **RA (J2000)** / **Dec (J2000)** | Astrometric position, topocentric for your site |
| **Alt** / **Az** | Altitude and azimuth |
| **Δ (AU)** | Distance from the observer |
| **r (AU)** | Distance from the Sun |
| **Elong.** | Angular distance from the Sun |
| **Phase** | Phase angle (Sun-object-observer) |
| **Mag** | Predicted visual magnitude |
| **Size"** | Apparent diameter |
| **Const.** | Constellation |

| Button | Action |
|---|---|
| **CSV…** | Save the table |
| **Compare with JPL Horizons** | Fetches JPL's position for the start time and shows it next to Sidralune's, to check accuracy or an orbit |
| **Show in sky** | Opens the Sky chart centred on the object, with it selected |

## Minor bodies

The **Minor bodies** tab lists comets and asteroids with their current circumstances and draws their orbits.

1. Choose **Comets**, **Asteroids**, **Near-Earth objects** (perihelion inside 1.3 AU) or **All**.
2. Set **brighter than** to a magnitude limit and optionally part of a **name**.
3. Click **List**.

The table shows the object, its type or orbit class, current magnitude, RA, Dec, distances from Earth and Sun, elongation, current altitude, and orbital elements: perihelion distance q (comets) or semi-major axis a (asteroids), eccentricity e and inclination i. Up to 2000 objects are shown, brightest first.

The first object in the list is selected when it is filled, and its orbit is shown. Select other rows to highlight their orbits in the diagram. Double-click a row to show the object in the Sky chart, centred and selected.

The **orbit diagram** on the right is a top-down view of the solar system on the ecliptic plane, with the planets' orbits and current positions, the listed objects as dots, and full orbits for the selected ones (a comet on a near-parabolic or hyperbolic orbit, which never closes, is drawn out to the edge of the diagram on both sides of perihelion, so it is on its line wherever it is). Scroll the mouse wheel to zoom from the inner planets out to the Kuiper belt.

| Button | Action |
|---|---|
| **Add elements…** | Paste one line in MPCORB (asteroid) or CometEls (comet) format, for example from an MPC circular or the MPC's orbit page for a new comet |
| **Fetch from JPL SBDB** | Look up any object by number, name or designation (`433`, `2024 YR4`, `12P`, `C/2023 A3`) in JPL's Small-Body Database and add it |
| **Remove added** | Removes the selected objects that were added with **Add elements…** or **Fetch from JPL SBDB**. Objects from the downloaded catalogues stay; an object whose catalogue orbit was replaced gets the catalogue orbit back |
| **Close approaches (60 d)** | Lists asteroid and comet approaches to Earth within 0.05 AU over the next 60 days, from JPL's CNEOS service |

Objects you add appear immediately in the Sky chart, the command palette and the Ephemeris tab.

## Planets & moons

A text report for the current time and site:

- a table of the Sun, Moon and planets with magnitude, diameter, illuminated fraction, elongation, altitude, and rise, transit and set times,
- **Sun**: position angle of the rotation axis P, heliographic latitude of the disc centre B0, longitude of the central meridian L0 and the Carrington rotation number, for solar observers,
- **Moon**: libration in longitude and latitude, position angle of the axis, and the Sun's selenographic colongitude (which tells you where the terminator is),
- **Jupiter**: central meridian longitudes in Systems I, II and III, for planning Great Red Spot transits,
- **Saturn**: ring tilt B and position angle P,
- **Mars, Jupiter, Saturn, Venus**: central meridian, sub-Earth latitude and pole position angle.

**Refresh for current time** updates the report after you change the time.

The **Jupiter's Galilean moons** diagram at the top shows Io, Europa, Ganymede and Callisto east and west of Jupiter as in binoculars (north up, west right).

**Galilean moon events (7 days)** lists the start and end of every transit (moon crossing Jupiter's disc) and occultation (moon hidden behind Jupiter) in the next week, in local time. Events marked ✓ happen with Jupiter higher than 10° in a sky darker than civil twilight.

## Eclipses

**Eclipses** lists every solar and lunar eclipse in the coming years (10 by default; change **Years** and click **Compute**), starting at the time shown in the header. It is worked out when you first open the tab and again when you change the observing site.

| Column | Meaning |
|---|---|
| **When** | Local time. A solar eclipse seen from your site is timed at its maximum there (or the sunrise or sunset during it, when the maximum is below the horizon); one not seen from your site at greatest eclipse on Earth. A lunar eclipse is timed at greatest eclipse, the same moment everywhere |
| **Eclipse** | The type of the eclipse as a whole: total, annular or partial solar eclipse; total, partial or penumbral lunar eclipse |
| **At your site** | How it looks from your site: the local type of a solar eclipse, for example *Partial solar eclipse* when it is total elsewhere, with *at sunrise* or *at sunset* when the Sun rises or sets with the eclipse under way; for a lunar eclipse *Visible*, or that the Moon rises or sets during it; *Not visible* when the Sun (Moon) is below the horizon the whole time, or the Moon passes by the Sun without covering it from your site |
| **Magnitude** | Solar: the fraction of the Sun's diameter covered at maximum. Lunar: the umbral magnitude (the penumbral magnitude for a penumbral eclipse) |
| **Sun covered** | Solar: the share of the Sun's disc covered at maximum |

Select an eclipse to see its phases below, in local time to the second, with the altitude of the Sun (solar) or the Moon (lunar) at each:

- **Solar eclipses**: *Partial eclipse begins* (first contact), *Total* or *Annular eclipse begins* and *ends* when your site is in the path, *Maximum eclipse*, *Partial eclipse ends* (last contact), any *Sunrise* or *Sunset* in between, and *Greatest eclipse on Earth* for comparison with global predictions.
- **Lunar eclipses**: *Penumbral eclipse begins*, *Partial eclipse begins*, *Total eclipse begins*, *Greatest eclipse*, and the same phases ending, with any *Moonrise* or *Moonset* in between. These moments are the same everywhere; only the altitudes depend on your site.

**Show in sky** (or double-clicking an eclipse) sets the program's time to the eclipse and shows the Sun or the Moon in the [Sky chart](Sky-chart). Click **Live** in the header to go back to the current time. **CSV…** saves the list.

Solar eclipse times use the Moon's mean limb, so they can differ by a few seconds from predictions that follow the Moon's real profile; for 12 August 2026 the contacts and maximum agree with published local circumstances within about a minute and the magnitude within 0.01. The [Coming up](Tonight#coming-up) card on the Tonight tab shows the next eclipses in the same way.

### The eclipse map

The map beside the list shows where the selected eclipse can be seen, for planning a trip. It opens on the part of the world that sees the eclipse.

**Zoom** with the mouse wheel (at the cursor), the **+** and **−** buttons or a double-click, and **drag** to move. **Whole eclipse** goes back to the first view. The cursor's latitude and longitude are shown in the lower right corner.

**A solar eclipse** is drawn with these lines, and a marker at greatest eclipse on Earth:

| Line | Meaning |
|---|---|
| **Central line** | Where the shadow's axis touches the Earth: the longest total or annular phase |
| **Limits of totality** or **of annularity** | The edges of the path where the eclipse is total or annular |
| **Limit of the partial eclipse** | Beyond it the Moon misses the Sun |
| **Begins or ends at sunrise or sunset** | The eclipse is already under way at sunrise, or still under way at sunset |
| **Maximum at sunrise or sunset** (dashed) | Maximum happens with the Sun on the horizon |
| **Share of the Sun covered at maximum** (dotted, 25 %, 50 %, 75 %) | How much of the Sun's disc the Moon covers |

**A lunar eclipse** is shaded where the Moon is up for the whole eclipse (darker) and for part of it (lighter). The lines show where the Moon is on the horizon at each contact (P1, U1, U2, U3, U4, P4), and dots mark where the Moon is overhead at each contact and at greatest eclipse.

- **Click any place** for the eclipse there:
  - for a solar eclipse: the type, the duration of totality or annularity, the magnitude, the share of the Sun covered, the path's width and the distance from the central line, and each contact in UT with the Sun's altitude and azimuth;
  - for a lunar eclipse: each phase in UT with the Moon's altitude and azimuth, and any moonrise or moonset.

  Times are in UT because the time zone of an arbitrary place isn't known; the phases list under the eclipse list keeps your site's local time.
- **Add as site** in the box adds the place to your observing sites and opens it in **Settings → Location**, where you name it and set its time zone and elevation (it starts at 0 m, since the map has no terrain).
- **Open in Google Maps** shows the place in Google Maps in your web browser, to look at roads, terrain or where to stay. The place is sent to Google only when you click it.
- **Copy** (the button after it) puts the latitude and longitude on the clipboard in decimal degrees, such as `58.488667, 13.334811`.

**Go to coordinates** (the fourth button, under **Whole eclipse**, or Ctrl+G) opens a field for a latitude and longitude. Press Enter or **Go**, and the map moves there, zooms in and shows the eclipse at that place, as if you had clicked it. If the clipboard holds a place, the field starts with it. You can also click the map and press Ctrl+V to go straight to a place on the clipboard. The field reads the forms that places are usually copied in:

| Form | Example |
|---|---|
| Decimal degrees, latitude first (as Google Maps copies them) | `58.48866654226863, 13.334811397822232`, `-33.8688 151.2093` |
| Decimal commas | `58,4887 13,3348` |
| Degrees, minutes and seconds, with N, S, E or W before or after | `58°29′19.2″N 13°20′05.3″E`, `58 29 19.2 N, 13 20 05.3 E` |
| Degrees and decimal minutes | `N 58° 29.32′ E 13° 20.09′` |
| A map link with the place in it | a Google Maps link with `@58.4887,13.3348` or `query=58.4887,13.3348`, or `geo:58.4887,13.3348` |

Without letters, latitude comes first and south and west are negative. Besides N, S, E and W, the compass letters of the interface language are read too, such as Ö and V in Swedish. If the field can't read the text, it says so and stays open.

The map works offline with a built-in Natural Earth world at 1:110 million. For more detail, install the **World map** datasets (land, lakes, borders, and cities and towns) in **Settings → Data**; see [Data and catalogues](Data-and-catalogues). The lines come from Besselian elements fitted to the same ephemeris as the rest of Sidralune; with DE440s installed, contacts agree with Xavier Jubier's mean-limb predictions within about a second. See [Algorithms and accuracy](Algorithms-and-accuracy).

## Occultations

### Asteroid occultations

When an asteroid passes in front of a star, the star disappears for a few seconds along a narrow path on Earth. Timings from several observers reveal the asteroid's shape and size.

1. Enter the asteroid in **Body** (for example `(1) Ceres` or a number).
2. Set the number of **Days** ahead and the faintest star (**Star mag ≤**).
3. Click **Asteroid occultations**.

Sidralune propagates the asteroid's orbit, queries Gaia DR3 stars along its track (online), and finds close approaches. Each event lists the time of closest approach (UTC), the star's magnitude, the magnitude drop (Δmag), the maximum duration, the width of the shadow (from the asteroid's diameter, estimated from its absolute magnitude and an assumed albedo) and **Your offset (km)**: how far your site is from the shadow's centre line.

> Predictions from MPC orbits can be off by more than the shadow's width. Before travelling, check the event with IOTA or another occultation prediction service, which use improved orbits.

### Lunar occultations

**Lunar occultations** lists stars (to magnitude 9 or your limit) that the Moon will cover as seen from your site in the coming days, with the disappearance (D) and reappearance (R) times and position angles, the Moon's illumination and altitude, and the Sun's altitude.

Disappearances at the dark limb of a waxing Moon are the easiest to time.

## Meteors

A table of the major annual meteor showers with their next peak:

| Column | Meaning |
|---|---|
| **Shower**, **Code** | Name and IAU code |
| **Peak (local)** | Peak time, computed from the shower's solar longitude |
| **ZHR** | Zenithal hourly rate: the rate under ideal conditions |
| **Speed (km/s)** | Entry velocity; fast showers have more trains and fireballs |
| **Radiant** | RA and Dec of the radiant |
| **Moon at peak** | The Moon's illuminated fraction at the peak |
| **Best hourly rate here** | The highest rate you can expect during the dark hours around the peak, from the IMO formula: ZHR × sin(radiant altitude) ÷ r^(6.5 − limiting magnitude) |

Your limiting magnitude (shown in brackets) is derived from the site's SQM value, so the same shower gives very different rates from a city and a dark site.

## Satellites

1. Install **Bright satellites** and **Space stations** in **Settings → Data** (refresh them before each session; elements age quickly).
2. Click **Load TLEs** (or simply **Predict passes**).
3. Set the number of **Days**, the minimum altitude (**Min alt**) and whether to show **Visible passes only**.
4. Click **Predict passes**. It waits until the prediction is done; so do the other calculations in Solar system (**Compute**, **List**, **Galilean moon events**, and the occultation searches). **Stop**, next to it, ends the prediction part way; the minor body **List** and the occultation searches have a **Stop** next to their buttons too.

Each pass lists the satellite, the rise time and direction, the time and altitude of the highest point, the set time and direction, the predicted brightest magnitude, and whether it is visible. A pass counts as visible when, at some point, the satellite is above your horizon and sunlit while the Sun is more than 6° below your horizon.

Positions use the SGP4 model, the standard for two-line elements. Deep-space objects (orbital periods of 225 minutes or more, such as geostationary satellites) use the same secular model, which is adequate for pass predictions over a few days but less accurate than the full SDP4 model.

## MPC astrometry

Report positions of asteroids and comets to the Minor Planet Center:

1. Plate solve your images in [Astrometry](Astrometry-and-framing) and save them with the WCS (**Write WCS to FITS…**), or use images already solved.
2. Click **Open image…**.
3. Enter the object's **Designation**: a number for numbered asteroids (packed automatically, for example `433` → `00433`) or a provisional or temporary designation.
4. Check your **Station code** (from **Settings**) and **Observer** name.
5. Click on the moving object in each image. Sidralune refines your click to the object's centroid with a Gaussian PSF fit, converts it to RA and Dec through the WCS, and takes the time from `DATE-OBS` plus half the exposure. Each measurement is numbered on the image.
6. **Remove last measurement** undoes a click.

Every measurement needs the image's plate solution and its observation time. Clicking shows "Image has no WCS" for an image that is not plate solved, and "Image has no DATE-OBS" for one without the FITS `DATE-OBS` (or `DATE_OBS`) start time. TIFF, PNG and JPEG files have no FITS header, and stacked images often lose the keyword, so measure the individual calibrated FITS frames from your capture software.

The measurements appear in the MPC 1992 80-column format. **Copy MPC1992 (80-column)** copies them for an email report; **Save ADES PSV…** writes the modern ADES pipe-separated format with your telescope details. Uncertainties are taken from the plate solution's RMS residual.

Measure at least three positions per night, spread over 30 minutes or more, and check the designation format before submitting.
