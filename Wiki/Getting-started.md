This page takes you from a fresh download to a working setup: building or running Sidralune, telling it where you observe, downloading the catalogues, describing your equipment and trying the simulated observatory.

## Contents

- [System requirements](#system-requirements)
- [Building from source](#building-from-source)
- [First run](#first-run)
- [Step 1: set your observing site](#step-1-set-your-observing-site)
- [Step 2: download the catalogues](#step-2-download-the-catalogues)
- [Step 3: describe your equipment](#step-3-describe-your-equipment)
- [Step 4: find your way around](#step-4-find-your-way-around)
- [Step 5: try the simulated observatory](#step-5-try-the-simulated-observatory)
- [Typical workflows](#typical-workflows)
- [Next steps](#next-steps)

## System requirements

| Item | Requirement |
|---|---|
| Operating system | Windows 10 or Windows 11. 64-bit is strongly recommended for stacking and planetary videos. |
| Runtime | .NET Framework 4.8.1 |
| Memory | 4 GB works for planning and processing single images; 16 GB or more is comfortable for stacking large sensors or long planetary videos. |
| Display | 1280 × 800 or larger. Per-monitor high-DPI scaling is supported. |
| Disk | About 10 MB for the program, plus about 70 MB for the recommended datasets (up to 150 MB with all optional ones). |
| Optional software | ASCOM Platform and drivers, PHD2, ASTAP, an SDR program or rtl_tcp, only for the features that use them |

## Building from source

Sidralune has no third-party dependencies, so building is a single step.

1. Install the **.NET Framework 4.8.1 Developer Pack** and **Visual Studio 2015 or later** (any edition), or the Visual Studio Build Tools.
2. Clone the repository:

   ```
   git clone https://github.com/pzyber/Sidralune.git
   ```

3. Open `Sidralune.sln` in Visual Studio and choose **Build → Build Solution**, or run in a developer command prompt:

   ```
   msbuild Sidralune.sln /p:Configuration=Release
   ```

4. Start `Sidralune\bin\Release\Sidralune.exe`.

To move Sidralune to another computer, copy the whole `bin\Release` folder. The program keeps its settings and downloads in `%LOCALAPPDATA%\Sidralune`, not next to the executable.

## First run

On first start Sidralune creates a site called **Home** at latitude 52° N, with a longitude guessed from your computer's time zone, and opens the **Tonight** page. The night summary is already useful, but it is for an approximate location, so the first thing to do is set your real site.

Catalogues load in the background; the status bar shows "Loading catalogs" until they are ready. Without downloaded data, Sidralune uses its built-in catalogue (bright stars, Messier and showpiece objects, constellations and four large asteroids) and an analytic planetary theory.

## Step 1: set your observing site

1. Click **Settings** at the bottom of the navigation rail and open the **Location** tab.
2. Fill in the fields:

   | Field | What to enter |
   |---|---|
   | **Name** | Anything you like, for example "Back garden" |
   | **Latitude** / **Longitude** | Decimal degrees (north and east positive) or sexagesimal, for example `51 28 38 N` and `0 00 05 W` |
   | **Elevation (m)** | Height above sea level |
   | **Time zone** | Your Windows time zone; daylight saving time is handled automatically |
   | **Sky brightness (SQM)** | A Sky Quality Meter reading at the zenith, or an estimate (see the table below) |
   | **Extinction k(V)** | 0.15 at a high, dry site, 0.2 typical, 0.3 or more in hazy or low-lying places |
   | **Typical seeing** | Full width at half maximum of stars in arcseconds; 2.5″ is typical |
   | **Temperature** / **Pressure** | Used for atmospheric refraction near the horizon |
   | **MPC observatory code** | Your Minor Planet Center code if you have one (otherwise 500) |
   | **Horizon profile** | Optional pairs of azimuth:altitude in degrees, for example `0:10, 90:25, 180:5, 270:15`, to mark trees and buildings |

3. Click **Save site**, then **Use this site**.

You can store several sites, for example home and a dark-sky location, and switch between them with **Use this site**.

| Sky | SQM (mag/arcsec²) | Bortle |
|---|---|---|
| City centre | 17.0–18.0 | 8–9 |
| Suburbs | 18.5–19.5 | 6–7 |
| Rural outskirts | 20.0–20.8 | 4–5 |
| Dark rural site | 21.3–21.7 | 2–3 |
| Exceptional site | 21.8–22.0 | 1 |

The SQM value drives the visibility predictions in the Sky chart and Planner. Bortle is derived from it automatically.

## Step 2: download the catalogues

1. Open **Settings → Data**.
2. The recommended datasets are already ticked: the DE440s ephemeris, the HYG star catalogue, OpenNGC, constellation lines and boundaries, and MPC comet elements.
3. Tick the optional ones if you want them: near-Earth asteroids, the full MPCORB file, and CelesTrak satellite elements.
4. Click **Download selected**. Progress is shown in the status bar, and the catalogues are loaded as soon as the download finishes.

Refresh comet and asteroid orbits every week or two with **Update installed**, because new comets are discovered constantly and orbits are improved as observations accumulate. See [Data and catalogues](Data-and-catalogues) for what each dataset provides.

## Step 3: describe your equipment

Open **Settings → Equipment**. There are three lists: **Telescopes**, **Eyepieces** and **Cameras**. Select an item to edit it, **Add** to create a new one, **Remove** to delete it and **Use** to make it the active one (marked with ●).

- **Telescope**: name, aperture, focal length, central obstruction (as a fraction of the aperture diameter, 0 for refractors) and light transmission.
- **Eyepiece**: name, focal length and apparent field of view.
- **Camera**: name, pixel size, sensor width and height in pixels, read noise, quantum efficiency and whether it is a colour (Bayer) sensor.

The **Combination summary** shows the resulting magnification, true field, exit pupil and Dawes limit for visual use, and the image scale, field of view, focal ratio, optimal sub-exposure for your sky and the Nyquist focal ratio for planetary imaging. These values are used for:

- the eyepiece and camera field-of-view rectangles on the [Sky chart](Sky-chart),
- the visibility predictions and best magnification in the [Planner](Observation-planning),
- the [framing and exposure planners](Astrometry-and-framing),
- plate-solving hints.

## Step 4: find your way around

- The **navigation rail** on the left switches between workspaces. Ctrl+1 to Ctrl+9 open the first nine.
- **Ctrl+K** opens the command palette. Type an object name (`M 42`, `Vega`, `Jupiter`, `C/2023 A3`) to select it, or part of a command (`stack`, `polar`, `print`) to run it.
- The clock at the top shows the site, the date and time used for every calculation, and the local sidereal time. To look at another date, type it into the date field on the **Sky** toolbar or use the step buttons next to it. The clock turns amber while you are away from the present; click **Live** to return.
- **Ctrl+Shift+N** switches to the red night-vision palette.

[The user interface](The-user-interface) explains all of this in detail.

## Step 5: try the simulated observatory

You can learn the observatory tools without any hardware:

1. Open **Observatory → Equipment** and click **Connect simulated observatory**.
2. The status bar shows a simulated telescope, camera, focuser, filter wheel, dome and weather station as connected.
3. In **Sky**, select a bright object and click **Slew**. The telescope marker moves across the chart.
4. In **Observatory → Imaging**, take an exposure. The simulated camera renders the real catalogue stars around the telescope's position, with noise, so you can try autofocus, plate solving and centring.
5. In **Observatory → Sequencer**, run the template sequence to see a whole automated session.

## Typical workflows

| I want to… | Go to |
|---|---|
| See what is up tonight | **Tonight**, then **Sky** |
| Plan a session at the eyepiece | **Planner → Generate best for tonight…**, then print finder charts from **Sky** |
| Image a galaxy with a cooled camera | **Astrometry → Framing & mosaics**, then **Observatory → Sequencer**, then **Deep sky**, then **Process** |
| Image Jupiter with a planetary camera | Record a SER video, then **Planetary → Pre-process → Stacking → Sharpen & deconvolve**, and **Derotation** for several videos |
| Measure a variable star | **Photometry → Measure**, then **Light curve analysis**, then export an AAVSO report |
| Time an exoplanet transit | **Photometry → Transit predictions**, observe, then **Measure** and **Exoplanet transits** |
| Report asteroid positions | **Astrometry** to plate solve, then **Solar sys. → MPC astrometry** |
| Record the hydrogen line | Record IQ data with your SDR, then **Radio → Hydrogen line / spectra** |

## Next steps

- [Tonight](Tonight) and [Sky chart](Sky-chart) for planning.
- [Observatory and devices](Observatory-and-devices) to connect real equipment.
- [Troubleshooting and FAQ](Troubleshooting-and-FAQ) if something does not work as expected.
