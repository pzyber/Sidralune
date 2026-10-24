The **Planner** workspace manages observing lists, predicts how well each object will be seen with your equipment tonight, plots altitudes through the night, and keeps a logbook of what you observed. Together with the [Sky chart](Sky-chart) it covers everything you need to plan an observing session.

## Contents

- [Layout](#layout)
- [Observing lists](#observing-lists)
- [Adding objects](#adding-objects)
- [The plan table](#the-plan-table)
- [Generating a list for tonight](#generating-a-list-for-tonight)
- [The altitude graph](#the-altitude-graph)
- [The logbook](#the-logbook)
- [Importing and exporting](#importing-and-exporting)
- [How visibility is predicted](#how-visibility-is-predicted)
- [Tips for a good plan](#tips-for-a-good-plan)

## Layout

| Area | Contents |
|---|---|
| **Observing lists** (left) | Your lists and the buttons to manage them |
| Centre, top | Toolbar, the plan table for the selected list |
| Centre, bottom | **Nightly planner — altitude** graph |
| **Logbook** (right) | Observations of the selected object |

## Observing lists

A list is a named set of objects with your notes and an "observed" tick for each. Sidralune starts with a **Messier catalogue** list and an empty **Tonight** list. Lists are saved automatically in `%LOCALAPPDATA%\Sidralune\Settings\planner.json`.

| Button | Action |
|---|---|
| **New** | Create an empty list |
| **Rename** | Rename the selected list |
| **Delete** | Delete the selected list (asks for confirmation) |
| **Import** | Import objects from a CSV or text file |
| **Export** | Export the list as CSV |

The number after each list name is how many objects it contains.

## Adding objects

- Type a name in the box on the toolbar and click **Add** (or press Enter). Stars, deep-sky objects and planets (`Mars`) are accepted.
- In the [Sky chart](Sky-chart), select an object (including comets and asteroids) and click **Add to list**, or right-click it and choose **Add … to observing list**.
- In **Databases → Local catalogues**, select rows and click **Add to plan**.
- Use **Generate best for tonight…** to build a list automatically.

**Remove** deletes the selected rows from the list.

## The plan table

For every object in the active list, Sidralune computes tonight's circumstances for your site and equipment:

| Column | Meaning |
|---|---|
| **✓** | Tick when you have observed the object |
| **Object**, **Type**, **Con**, **Mag**, **Size** | Catalogue data |
| **Rise**, **Transit**, **Set** | Local times; "circ." for circumpolar objects |
| **Best** | When the object is best seen during the dark hours, taking altitude, moonlight and twilight into account |
| **Alt now** | Current altitude |
| **Visibility** | Predicted detectability at the best time: Obvious, Easy, Detectable, Difficult or NotVisible |
| **Best ×** | The magnification that gives the best view with the active telescope |
| **Moon** | Angular distance from the Moon |
| **Notes** | Your notes. Click the cell to edit |

Click a column heading to sort, for example by **Best** to get an observing order through the night. Tick **Up tonight only** to hide objects that do not rise above the horizon during the dark hours. **Recalculate** refreshes the table, for example after changing the time.

The table recalculates automatically when you change the active site. After changing the time or your equipment, click **Recalculate**.

## Generating a list for tonight

**Generate best for tonight…** (or the `Planner: generate tonight's best list` command) asks which kind of object you want:

- All deep-sky
- Galaxies
- Nebulae (emission, reflection, supernova remnants and cluster-nebula complexes)
- Clusters (open and globular)
- Planetary nebulae

Sidralune then examines every deep-sky object brighter than magnitude 13 at hourly steps through tonight's dark period. An object counts only while it is above 25° altitude. At each step it predicts the detection margin with your telescope, eyepiece, eyes and sky, including moonlight. The 60 objects with the largest margins become a new list named after the type and date, for example "Galaxies 2026-10-18".

## The altitude graph

The **Nightly planner — altitude** graph plots, for the selected objects (up to eight), their altitude from evening to morning. It also shows:

- the Moon's altitude,
- shaded bands for twilight and darkness,
- a horizontal line at 30°, a common minimum for good views,
- a marker at the current time.

Select several rows in the table to compare objects and decide the order of the night.

Times on the axis are the site's local clock. On the night the clocks change, time still runs evenly along the axis: when summer time ends, the repeated hour is labelled twice (for example 02:00 appears two times), and when it begins, the skipped hour is missing from the labels. The Sky chart's altitude graph works the same way.

## The logbook

Select an object to see its earlier observations in the **Logbook** on the right: date, a rating in stars, the instrument and your notes.

Click **Log observation…** to record a new one. The form is pre-filled with the time, site, telescope and eyepiece, and has fields for:

| Field | Meaning |
|---|---|
| Seeing | Atmospheric steadiness on a 1–5 scale |
| Transparency | How clear the sky was, 1–5 |
| SQM | Measured sky brightness, if you have a meter |
| Rating | 1 to 5 stars |
| Notes | What you saw |

**Export** in the logbook card writes all observations to CSV with the columns UTC, Object, Site, Instrument, Eyepiece, Seeing, Transparency, SQM, Rating and Notes.

## Importing and exporting

**Import** reads a CSV or text file with one object per line. The first column is the object name; a header row named `Name` or `Object` is skipped. Each name is looked up in the catalogues, so `M 31`, `NGC 891` or `Albireo` all work. Other columns are ignored.

**Export** writes Name, Type, Constellation, RA, Dec, Mag, Size_arcmin, Observed and Notes. The file opens in any spreadsheet program, and can be imported again or shared with other observers.

## How visibility is predicted

Sidralune predicts whether you will actually see an object, not just whether it is above the horizon. The model, used in the Planner, the Tonight page and the Sky chart, works in four steps:

1. **Sky brightness** at the object's position. It starts from your site's SQM value, brightens towards the horizon, and adds moonlight with the Krisciunas & Schaefer (1991) model (which depends on the Moon's phase, altitude and distance from the object) and twilight from the Sun's altitude.
2. **The object's light at your eye**. Its magnitude is dimmed by atmospheric extinction (your site's k(V) times the airmass) and by the telescope's transmission and central obstruction, and gathered by its aperture.
3. **Contrast at each magnification**. Magnification darkens the background once the exit pupil is smaller than your eye's pupil, and spreads extended objects over a larger area. Small objects are blurred by seeing and diffraction. Sidralune scans the useful range of magnifications.
4. **The detection threshold**. Schaefer's (1990) model of night-vision thresholds, with spatial summation for extended objects (Ricco's law), gives the faintest detectable object on that background. Your age sets the eye's pupil (about 7.8 mm at 20, 0.45 mm less per decade) and your experience and averted vision lower the threshold.

The result is a **margin** in magnitudes: how much brighter than the threshold the object is at the best magnification. The rating follows from it:

| Margin | Rating |
|---|---|
| below −0.5 | Not visible |
| −0.5 to 0 | Difficult (averted vision) |
| 0 to 0.7 | Detectable |
| 0.7 to 1.8 | Easy |
| 1.8 or more | Obvious |

> The model is a guide based on laboratory data and typical observers. Your eyes, dark adaptation, transparency and experience on the night all matter. Treat "Difficult" objects as a challenge rather than a promise.

## Tips for a good plan

- Set your real SQM, extinction and age in **Settings**; they make a large difference to the predictions.
- Choose the eyepiece you will actually use as the active one, or check **Best ×** to see which magnification to use.
- Sort by **Best** to observe objects in the west first, before they set.
- Print a finder chart for each target from the [Sky chart](Sky-chart#printing-finder-charts) before the session.
- Use the **Moon** column to avoid faint galaxies near a bright Moon.
