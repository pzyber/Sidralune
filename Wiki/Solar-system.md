The **Solar sys.** workspace gathers everything about the Sun's family: ephemerides for any planet, comet or asteroid; a browser of minor bodies with an orbit diagram; planetary physical data and Jupiter's moons; asteroid and lunar occultations; meteor showers; satellite passes; and measuring and reporting asteroid and comet positions to the Minor Planet Center.

## Contents

- [Ephemeris](#ephemeris)
- [Minor bodies](#minor-bodies)
- [Planets & moons](#planets--moons)
- [Occultations](#occultations)
- [Meteors](#meteors)
- [Satellites](#satellites)
- [MPC astrometry](#mpc-astrometry)

## Ephemeris

An ephemeris is a table of an object's position and circumstances over time.

1. Choose or type the object: the Sun, Moon, planets and Pluto, and every comet and asteroid loaded (the list is searchable as you type).
2. Set the start date and time (UTC), the step (10 minutes, 1 hour, 6 hours, 1 day or 7 days) and the number of rows.
3. Click **Compute**.

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
| **Show in sky** | Selects the object and opens the Sky chart |

## Minor bodies

The **Minor bodies** tab lists comets and asteroids with their current circumstances and draws their orbits.

1. Choose **Comets**, **Asteroids**, **Near-Earth objects** (perihelion inside 1.3 AU) or **All**.
2. Set **brighter than** to a magnitude limit and optionally part of a **name**.
3. Click **List**.

The table shows the object, its type or orbit class, current magnitude, RA, Dec, distances from Earth and Sun, elongation, current altitude, and orbital elements: perihelion distance q (comets) or semi-major axis a (asteroids), eccentricity e and inclination i. Up to 2000 objects are shown, brightest first.

Select rows to highlight their orbits in the diagram. Double-click a row to show the object in the Sky chart.

The **orbit diagram** on the right is a top-down view of the solar system on the ecliptic plane, with the planets' orbits and current positions, the listed objects as dots, and full orbits for the selected ones (comets on near-parabolic orbits are drawn around perihelion). Scroll the mouse wheel to zoom from the inner planets out to the Kuiper belt.

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
4. Click **Predict passes**.

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
