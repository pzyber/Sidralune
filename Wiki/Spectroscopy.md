The **Spectra** workspace reduces raw spectrum images into calibrated one-dimensional spectra and analyses them: measuring lines, radial velocities, temperatures and spectral classes, correcting for the instrument's response, and exporting to the BeSS database format. It suits slit spectrographs (Alpy, LHIRES, LISA and others) and slitless gratings (Star Analyser).

It has two tabs: **Reduce 2D spectrum** and **Analyse spectrum**.

## Contents

- [Reduce 2D spectrum](#reduce-2d-spectrum)
  - [Extraction settings](#extraction-settings)
  - [Step by step](#step-by-step)
  - [Wavelength calibration](#wavelength-calibration)
- [Analyse spectrum](#analyse-spectrum)
  - [Opening a spectrum](#opening-a-spectrum)
  - [Line lists](#line-lists)
  - [Normalising](#normalising)
  - [Measuring lines](#measuring-lines)
  - [Radial velocity](#radial-velocity)
  - [Instrument response](#instrument-response)
  - [Temperature and spectral class](#temperature-and-spectral-class)
  - [Saving](#saving)
- [Tips](#tips)

## Reduce 2D spectrum

The raw image from a spectrograph is a stripe of light: the star's spectrum, spread out horizontally (the dispersion direction) and a few pixels high. Reduction turns it into a table of flux against wavelength.

Calibrate the frames first (darks, flats, bias) in [Deep sky](Deep-sky-stacking) if you can, and orient them so the dispersion runs along X with blue on the left.

### Extraction settings

| Setting | Meaning |
|---|---|
| **Aperture half-width** | Half the height, in pixels, of the strip summed across the spectrum |
| **Sky gap / width** | Gap between the spectrum and the sky strips on either side, and the sky strips' width. The sky is subtracted |
| **Optimal extraction** | Weight each pixel by the expected profile and its noise (Horne 1986), which improves the signal-to-noise ratio of faint spectra |
| **Lamp** | Calibration lamp line list: Neon, Argon, Mercury, Hydrogen, or Neon+Argon (Alpy) |
| **Approx. start (Å)** | Approximate wavelength of the first (leftmost) pixel |
| **Approx. Å / pixel** | Approximate dispersion |
| **Polynomial degree** | Degree of the pixel-to-wavelength fit (3 is typical) |

### Step by step

1. **1 · Open science frame…**: the star's spectrum image.
2. **2 · Trace & extract**: finds the spectrum's trace (its centre line, fitted with a polynomial so tilted or curved spectra work; it is drawn in green) and extracts it with sky subtraction. The **Extracted spectrum** plot shows flux against pixel.
3. **3 · Open lamp frame…**: an image of the calibration lamp taken through the same slit without moving anything.
4. **4 · Auto wavelength calibration**: extracts the lamp spectrum along the same trace, finds its emission lines, identifies them in the chosen line list using your approximate start and dispersion, and fits the dispersion polynomial. The result lists every identified line (pixel and wavelength) and the RMS error in ångströms. The science spectrum and the lamp are then plotted against wavelength.
5. **Send to analysis →** moves the calibrated spectrum to the **Analyse spectrum** tab.

### Wavelength calibration

If you have no lamp frame, **Linear calibration from start/dispersion** applies a simple linear scale from **Approx. start** and **Approx. Å / pixel**. With a slitless grating, find the dispersion from two known lines (for example Hα at 6562.8 Å and Hβ at 4861.3 Å in an A-type star) and the zero-order image position.

A good lamp calibration has an RMS below a tenth of the dispersion. If automatic identification fails, check the approximate start and dispersion: they need to be within roughly 10% for the line pattern to be recognised.

## Analyse spectrum

### Opening a spectrum

**Open spectrum (FITS / text)…** reads:

- 1D FITS spectra with linear wavelength keywords (`CRVAL1`, `CDELT1`, `CRPIX1`), as produced by most spectroscopy software and the BeSS and ARAS databases,
- text and CSV files with wavelength and flux columns.

Or arrive from the reduction tab. The summary shows the number of points, the wavelength range and the dispersion.

### Line lists

**Line list** marks lines on the plot: **Stellar** (hydrogen Balmer series, helium, sodium D, magnesium b, calcium H and K, iron…), **Nebular** (Hα, Hβ, [O III], [N II], [S II], He I…), **Telluric** (Earth's atmospheric oxygen and water bands) or **None**.

### Normalising

**Normalise to continuum** fits a smooth curve through the continuum, iteratively ignoring absorption and emission lines, and divides by it, so the continuum sits at 1. Do this before measuring equivalent widths or comparing line depths.

### Measuring lines

1. Drag across a line in the plot to select its wavelength range (include a little continuum on both sides).
2. Optionally type the line's laboratory wavelength in **Rest λ for RV (Å)**; leave it empty to use the nearest listed line.
3. Click **Measure selected line**.

Sidralune fits the line and reports its centre, FWHM, equivalent width, depth, whether it is in emission, and the radial velocity from the shift between the measured centre and the rest wavelength. The line is marked on the plot.

### Radial velocity

| Tool | Description |
|---|---|
| **Measure selected line** | Velocity from one line's shift: v = c × (λ_observed − λ_rest) / λ_rest |
| **Cross-correlation RV vs template…** | Cross-correlates the whole spectrum with a template spectrum of a similar star (both normalised) and reports the velocity shift; more precise than single lines |
| **Barycentric correction** | The Earth's orbital and rotational velocity towards the star at the time of observation (needs `DATE-OBS` and the star's RA/Dec in the header, or a selected object). Add it to measured velocities to get heliocentric (barycentric) values |

The Earth's motion changes measured velocities by up to ±30 km/s over a year, so always apply the barycentric correction before comparing spectra from different nights.

### Instrument response

The camera, grating and optics transmit some wavelengths better than others, so raw spectra have the wrong overall shape. **Instrument response from reference star** fixes this:

1. Observe a reference star of known spectrum (an A or B star near your target, at a similar altitude) with the same setup.
2. Open its spectrum and click the button.
3. Choose a reference spectrum file (for example from the MILES or Pickles libraries, or BeSS), or answer **No** to use a blackbody at **Reference Teff (K)**.

The response is the smoothed ratio of observed to reference flux. It is applied to the current spectrum and to every spectrum you open afterwards in the session. **Reset to original** returns to the uncorrected spectrum.

### Temperature and spectral class

**Temperature & spectral class** reports:

- a blackbody temperature fitted to the whole spectrum (meaningful only for response-corrected spectra),
- the wavelength of peak flux and the corresponding Wien temperature,
- an estimated spectral class (O, B, A, F, G, K or M) from the equivalent widths of key features: He II and He I, Hβ and Hγ, Ca II K, Mg b, Na D and the TiO band near 7054 Å. It is a quick guide to the temperature class, not a full classification.

### Saving

**Save (BeSS FITS / text)…** writes:

- **BeSS FITS**: the format required by the BeSS database of Be-star spectra and the ARAS database, with the linear wavelength keywords and the object name, observer, instrument, site and heliocentric-correction keywords (`OBJNAME`, `OBSERVER`, `BSS_INST`, `BSS_SITE`, `BSS_VHEL`…),
- **Text**: two columns of wavelength and flux.

## Tips

- Take lamp frames immediately before or after the star, without moving the telescope, so flexure does not shift the calibration.
- Expose the star to at least 50% of full scale in the brightest part of the spectrum, but never saturate.
- Use the telluric line list to identify the atmospheric bands near 6870 Å (O₂ B band) and 7600 Å (O₂ A band) rather than mistaking them for stellar lines.
- For Be stars, measure the equivalent width of Hα regularly and submit spectra to BeSS; see [Databases and citizen science](Databases-and-citizen-science#amateur-science).
