The **Photometry** workspace measures the brightness of stars in image sequences and analyses the resulting light curves. Use it for variable stars, eclipsing binaries, exoplanet transits and asteroid rotation. It has four tabs: **Measure**, **Light curve analysis**, **Exoplanet transits** and **Transit predictions**.

## Contents

- [Measure](#measure)
  - [Frames](#frames)
  - [Stars](#stars)
  - [Aperture settings](#aperture-settings)
  - [Time stamps](#time-stamps)
  - [Measuring](#measuring)
  - [Exporting results](#exporting-results)
- [Light curve analysis](#light-curve-analysis)
  - [Loading a light curve](#loading-a-light-curve)
  - [Period search](#period-search)
  - [Phase folding](#phase-folding)
  - [Timing minima](#timing-minima)
  - [O−C diagrams](#oc-diagrams)
- [Exoplanet transits](#exoplanet-transits)
- [Transit predictions](#transit-predictions)
- [Good practice](#good-practice)

## Measure

### Frames

Click **Add frames…** to add calibrated images of the field (FITS is best, since it carries the observation time). **Remove selected** (or the Delete key) takes out the frames selected in the list, and **Clear** empties it. Colour raw frames are debayered automatically.

Frames that have no WCS in their header are plate solved before measuring, using the first star's position as the hint. Frames without a `DATE-OBS` time are skipped.

### Stars

The stars table lists the stars to measure, each with a **Role**:

| Role | Meaning |
|---|---|
| **Target** | A star whose brightness you want: the variable, the exoplanet host, the asteroid's field position |
| **Comparison** | A constant star of known magnitude used as reference. Use several; Sidralune combines them into an ensemble |
| **Check** | A constant star measured like the target, to show whether the comparison ensemble is stable |

Enter **Name**, **RA**, **Dec** (J2000, sexagesimal or decimal), **Cat. mag** (catalogue magnitude in the filter you used) and **B−V** colour. Ways to fill the table:

- **Add selected object** adds the object selected in the Sky chart or Planner as a target.
- **AAVSO comps…** asks for a variable star's name and downloads the AAVSO comparison-star sequence for it (VSP), with V magnitudes and B−V colours, as comparison stars.
- Type rows directly, or paste them.

**Remove selected** (or the Delete key in the table) removes the selected stars.

### Aperture settings

The property grid sets the aperture photometry:

| Setting | Meaning |
|---|---|
| **Radius** | Aperture radius in pixels |
| **Inner annulus** / **Outer annulus** | The ring used to measure the sky background |
| **Fwhm multiple** | If above 0, the aperture radius is set to this multiple of each frame's measured FWHM, which adapts to changing seeing (1.5–2 is typical) |
| **Gain e per adu** | Camera gain, for error estimates |
| **Read noise e**, **Dark current e** | For error estimates |
| **Adu scale** | Full-scale ADU value (65535 for 16-bit data) |
| **Saturation level** | Fraction of full scale above which a star counts as saturated and is not used |
| **Recentre** | Refine each star's position on its centroid before measuring |

The sky background is estimated robustly from the annulus: values are sigma-clipped, then the mode is estimated (3 × median − 2 × mean), so nearby faint stars do not bias it.

### Time stamps

**Time stamps** chooses the time written for each point, always at mid-exposure:

| Option | Use |
|---|---|
| **BJD (TDB)** | Barycentric Julian Date in Barycentric Dynamical Time: the time at the solar system's barycentre. Standard for exoplanets and timing work |
| **HJD (UTC)** | Heliocentric Julian Date: the traditional choice for variable stars and the AAVSO |
| **JD (UTC)** | Plain Julian Date |

Light-travel time across the Earth's orbit changes timings by up to ±8.3 minutes, so use BJD or HJD for anything that will be compared over months.

### Measuring

Click **Measure all frames**. For each frame Sidralune finds each star from its RA/Dec through the WCS, measures it, and computes the target's magnitude against the ensemble of comparison stars: each comparison star gives a zero point (catalogue minus instrumental magnitude), and the zero points are averaged with weights from their errors. A colour-term correction is applied if transformation coefficients are set. Errors include photon noise, sky noise, read noise and the ensemble scatter. The airmass of each frame is recorded.

The **Light curves** plot shows each target's magnitude over time with error bars, and the check star's in grey. The first target's light curve is loaded into the **Light curve analysis** tab automatically.

### Exporting results

| Button | Output |
|---|---|
| **AAVSO report…** | AAVSO Extended File Format, ready to submit through WebObs. Asks for your observer code the first time (saved in **Settings → Observer & accounts**). Uses the comparison star's name, or "ENSEMBLE" when several were used, and the check star |
| **CSV…** | One CSV per target with JD, magnitude, error, relative flux and error, airmass, filter, check-star magnitude, sky level and file name |

## Light curve analysis

### Loading a light curve

| Source | How |
|---|---|
| Measured data | Automatic after **Measure all frames** |
| A file | **Load CSV / text…**: CSV or whitespace-separated columns. Sidralune recognises columns named like BJD, HJD, JD, time or date; mag, flux, rel_flux or norm; and an error column (err, sig, e_…). Without headers it uses the first three columns. Normalised flux (values around 1) is recognised automatically |
| Demo | **Demo data** loads a synthetic eclipsing binary to try the tools |

The summary shows the number of points, the time span and the RMS scatter.

### Period search

Choose a **Method**, the **Min period (d)** and **Max period (d)**, and optionally **Detrend order** (a polynomial removed first, for slow trends from airmass or the instrument). Click **Find period**.

| Method | Best for |
|---|---|
| **Lomb-Scargle (generalised)** | Sinusoidal and smooth variations: pulsating stars, rotation, ellipsoidal variables. Reports a false-alarm probability |
| **Box least squares (transits)** | Short box-shaped dips: exoplanet transits and detached eclipsing binaries. Works on flux; magnitudes are converted automatically |
| **Phase dispersion minimisation** | Any shape of variation, including sharp eclipses; makes no assumption about the curve's shape |

The **Periodogram** plot shows power against period, with the best period marked. Click another peak to fold at that period instead.

> Eclipsing binaries with two similar eclipses often show their strongest Lomb-Scargle peak at half the true period. Check the phase plot: if both eclipses fall on top of each other, double the period.

**Sigma clip** removes outliers more than 4 standard deviations from the curve.

### Phase folding

The **Phase folded** plot shows the light curve folded at the chosen period, repeated once so a full cycle is visible around phase 1, with a binned average curve. Phase 0 is at the deepest point.

### Timing minima

To time an eclipse or transit minimum precisely:

1. Drag across the minimum in the **Light curve** plot to select it (at least 7 points).
2. Click **Time minimum (Kwee-van Woerden)**.

The Kwee-van Woerden method (1956) finds the time of symmetry of the minimum and its uncertainty. The result is added to the output and copied to the clipboard, and a marker is drawn on the plot. Times are in the light curve's time system, so use BJD or HJD.

### O−C diagrams

**O−C from minima list…** compares observed minima with an ephemeris. Enter a list of minimum times; the first is used as epoch, and the period from the period search. The plot shows observed minus calculated times in minutes against cycle number: a straight sloping line means the period needs correcting, a parabola means the period is changing, and a wave can mean a third body. A refined linear ephemeris is reported.

## Exoplanet transits

The **Exoplanet transits** tab fits a physical transit model to a light curve from the analysis tab.

| Field | Meaning |
|---|---|
| **Mid-transit T0** | Approximate time of mid-transit (pre-filled with the deepest point) |
| **Period (d)** | Orbital period (pre-filled after a box least squares search) |
| **Star radius (R☉)** | The host star's radius, to convert the fit into the planet's size |
| **Fit limb darkening** | Also fit the quadratic limb-darkening coefficients (needs very good data) |
| **MCMC steps** | Length of the Markov-chain Monte Carlo run |

**Fit (Levenberg-Marquardt)** fits the mid-transit time, the planet-to-star radius ratio Rp/R*, the orbit's semi-major axis a/R*, the inclination, the baseline flux and a linear slope, using a quadratic limb-darkened model (Mandel & Agol geometry). Results include parameter errors and the reduced χ².

**Posterior (MCMC)** then runs an affine-invariant ensemble sampler on T0, Rp/R*, a/R* and the impact parameter b, and reports the median and ±1σ of each, which are more realistic uncertainties than the least-squares errors.

Both report the transit depth (ppt), duration (hours), impact parameter, the planet's radius in Jupiter and Earth radii, the semi-major axis in AU, and the residual scatter. The plot shows the data in hours from mid-transit with the model.

## Transit predictions

The **Transit predictions** tab lists upcoming exoplanet transits you can observe from the active site, using the NASA Exoplanet Archive's confirmed planets.

| Field | Meaning |
|---|---|
| **Host V ≤** | Faintest host star to include |
| **Depth ≥ (ppt)** | Shallowest transit to include (parts per thousand; 1 ppt = 0.001 = about 1.1 mmag) |
| **Nights** | How many nights ahead to search |

Click **Predict transits**. A transit is listed only if the host is above 25° and the Sun below −12° at ingress, mid-transit and egress, so the whole event is observable. Columns show the planet, host magnitude, depth, duration, ingress, mid and egress times in local time, the altitude at mid-transit and the Sun's altitude. Double-click a row to select the host star for the Sky chart and Planner.

The archive is queried once per session; predictions are based on the archive's published ephemeris, whose uncertainty grows with time since it was measured. Add an hour of baseline before ingress and after egress.

## Good practice

- Calibrate frames (darks and flats) before photometry; flats matter most.
- Defocus slightly for bright stars so they are not saturated and are spread over more pixels.
- Use comparison stars of similar colour and brightness to the target, close to it in the field.
- Keep the field on the same pixels all night (good guiding, no meridian flip during a transit if you can avoid it).
- Watch the check star: if it varies, the ensemble or the conditions are at fault, not the target.
- Submit variable-star data to the AAVSO and transit timings to Exoplanet Watch or ExoClock; see [Databases and citizen science](Databases-and-citizen-science#amateur-science).
