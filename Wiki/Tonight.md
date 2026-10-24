**Tonight** is the first workspace on the rail and the page Sidralune opens with. It answers the questions you ask before going outside: how dark will it get, where is the Moon, which planets are up, what is worth looking at, and what is coming up.

Everything on the page is for the active site and the current (or simulated) time. It updates every minute, whenever you change the time, and fully recalculates when you change the site or install new catalogues or orbits. Double-click any row in its tables to show that object in the [Sky chart](Sky-chart).

## Contents

- [Tonight card](#tonight-card)
- [Moon](#moon)
- [Planets](#planets)
- [Best deep-sky targets tonight](#best-deep-sky-targets-tonight)
- [Coming up](#coming-up)
- [Jupiter's moons](#jupiters-moons)
- [Comets & asteroids](#comets--asteroids)
- [Getting started](#getting-started)

## Tonight card

The title shows the site name and the date of the night. The card lists:

- **Sunset**, then the end of civil (Sun 6° below the horizon), nautical (12°) and astronomical (18°) twilight.
- **Astronomical dawn** and **sunrise** the next morning.
- **Astronomical darkness**: how many hours the Sun stays more than 18° below the horizon.
- **Moonlight-free**: how many of those hours the Moon is also below the horizon, which is the time that matters for faint deep-sky objects and for imaging without moonlight.

At high latitudes in summer the Sun may never reach −18°. The "best targets" list then uses the nautically dark period (Sun below −12°) instead; if the Sun does not even get that low, the list stays empty.

## Moon

A drawing of the Moon's current phase, with:

- the phase name (waxing crescent, first quarter, waxing gibbous, full Moon and so on),
- illuminated percentage and age in days since new Moon,
- rise and set times,
- distance from Earth in kilometres,
- current altitude.

## Planets

A table of Mercury to Neptune with **Rise**, **Transit** (highest point) and **Set** in local time, **Mag** (visual magnitude), **Size** (apparent diameter), **Con** (constellation) and **Alt** (altitude now).

## Best deep-sky targets tonight

Sidralune ranks the brighter deep-sky objects (magnitude 11.5 and brighter) by how well you will actually see them tonight, with your telescope, your eyepiece, your eyes and your sky. For each object it samples the dark hours and, at each hour, computes:

1. the object's altitude (it must be above 20° and above your horizon profile),
2. the sky brightness at the object's position, including moonlight, twilight and your site's light pollution (Krisciunas-Schaefer model),
3. the detection margin with the [visibility model](Observation-planning#how-visibility-is-predicted): how far, in magnitudes, the object is above the threshold of visibility at the best magnification.

The 40 objects with the best margin are listed, with a small bonus for Messier objects. Columns:

| Column | Meaning |
|---|---|
| **Object**, **Type**, **Con**, **Mag**, **Size** | Catalogue data |
| **Best** | Local time when the object is best placed during the dark hours |
| **Alt** | Altitude at that time |
| **Visibility** | Obvious, Easy, Detectable, Difficult or Not visible |
| **Margin** | Detection margin in magnitudes. Positive numbers mean visible; the larger, the easier |

The list depends on the active telescope and eyepiece in **Settings → Equipment** and your age and experience in **Settings → Observer & accounts**, so a large telescope at a dark site produces a very different list from binoculars in a city.

## Coming up

The next 30 events over the coming weeks:

- Moon phases (new, first quarter, full, last quarter),
- conjunctions of the planets with the Sun (inferior and superior conjunctions for Mercury and Venus), oppositions of the outer planets and greatest elongations of Mercury and Venus,
- lunar perigee and apogee,
- the next lunar and solar eclipses within about a year,
- peaks of the major meteor showers within 90 days, with their ZHR.

Times are local. For more detail on eclipses, occultations and meteors, see [Solar system](Solar-system).

## Jupiter's moons

A live diagram of Io, Europa, Ganymede and Callisto on either side of Jupiter, as seen in binoculars or a small telescope (north up). The caption names any moon that is in transit across the disc or hidden behind the planet. For a list of transits and occultations over the coming week, use **Solar sys. → Planets & moons → Galilean moon events**.

## Comets & asteroids

The 20 brightest comets and asteroids from your installed orbit files that are brighter than magnitude 12 tonight, with **Mag**, **Con**, **Elong.** (angular distance from the Sun), **Alt** and **Δ** (distance from Earth in AU). Install **MPC comet elements** in **Settings → Data** to fill this card; asteroids come from the built-in four or from the MPC asteroid files.

## Getting started

This card, at the end of the page, explains what the optional data adds and has quick links:

| Button | Action |
|---|---|
| **Set observing site** | Opens **Settings → Location** |
| **Open data manager** | Opens **Settings → Data** |
| **Try the simulated observatory** | Connects the simulated telescope, camera, focuser, filter wheel, dome and weather station |
