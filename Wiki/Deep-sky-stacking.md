The **Deep sky** workspace turns a night's worth of light frames into one clean, deep image. The workflow is simple (add frames, register, stack), with modern rejection methods, drizzle, comet stacking and mosaics.

## Contents

- [Overview of the process](#overview-of-the-process)
- [Taking calibration frames](#taking-calibration-frames)
- [The Deep sky workspace](#the-deep-sky-workspace)
- [Adding frames](#adding-frames)
- [The frame list](#the-frame-list)
- [Registering](#registering)
- [Choosing frames](#choosing-frames)
- [Stacking settings](#stacking-settings)
  - [Calibration](#calibration)
  - [Registration](#registration-settings)
  - [Combination and rejection](#combination-and-rejection)
  - [Normalisation and weighting](#normalisation-and-weighting)
  - [Output area](#output-area)
  - [Drizzle](#drizzle)
  - [Comets](#comets)
- [Stacking](#stacking)
- [The result](#the-result)
- [File lists](#file-lists)
- [Recommended settings](#recommended-settings)

## Overview of the process

1. **Calibration** removes the camera's own signal from each light frame: the bias (read-out offset), the dark current and hot pixels (darks), and vignetting and dust shadows (flats).
2. **Registration** finds the stars in every frame and works out how each frame is shifted, rotated and scaled relative to a reference frame.
3. **Stacking** warps every frame onto the reference and combines them pixel by pixel, rejecting satellite trails, aircraft, cosmic rays and hot pixels.

Stacking N frames improves the signal-to-noise ratio by about √N, so 16 frames are four times less noisy than one.

## Taking calibration frames

| Frame | How to take it | How many |
|---|---|---|
| **Lights** | The target | As many as possible |
| **Darks** | Same exposure, gain, offset and sensor temperature as the lights, with the telescope covered | 20–50 |
| **Flats** | Evenly illuminated (flat panel, sky flat or T-shirt), same focus, camera rotation and filter as the lights, exposed to about half the full well | 20–50 per filter |
| **Dark flats** | Same exposure as the flats, telescope covered | 20–50 |
| **Bias / offset** | Shortest possible exposure, telescope covered | 50–100 |

You need either dark flats or bias frames for the flats. With cooled cameras you can reuse darks and bias frames for months at the same temperature and gain. You can capture all of these with the [Observatory](Observatory-and-devices#camera) or the [sequencer](Sequencer).

## The Deep sky workspace

| Area | Contents |
|---|---|
| Left | Action buttons in workflow order |
| Centre, top | Preview of the selected frame, or the stacked result |
| Centre, bottom | The frame list |
| Right | **Stacking settings** (a property grid) and **Stacked image adjustments** |
| Summary line | Numbers of lights (total, checked, and total exposure in minutes), darks, flats, dark flats and bias frames |

## Adding frames

| Button | Adds |
|---|---|
| **Light frames…** | Images of the target |
| **Dark frames…** | Darks |
| **Flat frames…** | Flats |
| **Dark flat frames…** | Darks for the flats |
| **Bias / offset frames…** | Bias frames |
| **Master frames (FITS)…** | Ready-made master bias, dark or flat files. The type is taken from the file name (it must contain "bias", "dark" or "flat") |

Supported formats are FITS, XISF, TIFF, DNG, CRW, CR2 and CR3 raw files, and PNG, JPEG and BMP. Colour (Bayer) sensors are detected from the FITS `BAYERPAT` keyword or the raw file, and debayered after calibration. You can also drag files onto the preview.

## The frame list

| Column | Meaning |
|---|---|
| ☑ | Whether the frame is used |
| **File** | File name |
| **Type** | Light, Dark, Flat, DarkFlat or Bias |
| **Exposure** | Seconds |
| **ISO/Gain** | From the file |
| **Temp** | Sensor temperature |
| **Filter** | From the file header |
| **Score** | Quality score after registration (★ marks the reference frame) |
| **Stars** | Stars detected |
| **FWHM** | Mean star full width at half maximum in pixels |
| **Ecc.** | Mean star eccentricity (0 is perfectly round; above about 0.5 shows trailing) |
| **Background** | Sky background level |
| **Status** | Registration result or error |

Click a column heading to sort, for example by **Score** or **FWHM**. Click a row to preview the frame. **Check all**, **Uncheck all**, **Remove selected** and **Clear list** manage the list.

## Registering

**Register checked frames** analyses every checked light frame:

1. Calibrates it (if masters are available), so hot pixels are not mistaken for stars.
2. Detects stars with an adaptive moment method and measures their brightness, FWHM, half-flux radius and eccentricity.
3. Computes a **score**: the sum of the square roots of the stars' signal-to-noise ratios, divided by FWHM × (1 + eccentricity). Many sharp, round stars give a high score.
4. Picks the best frame as the reference (unless you chose one with **Use selected as reference**).
5. Matches each frame's stars to the reference with triangle-similarity pattern matching and fits the chosen transform with RANSAC, which ignores false matches. The number of matched stars and the residual error are recorded.

Frames that cannot be matched (clouds, wrong target, trailing) are marked in **Status** and excluded from stacking.

## Choosing frames

After registering, sort by **Score**, **FWHM** or **Ecc.** and uncheck poor frames: those with trailing, clouds, or much larger stars than the rest. The settings **Keep percent** and **Min score ratio** can do this automatically at stacking time.

## Stacking settings

The settings grid on the right has the following options.

### Calibration

| Setting | Meaning |
|---|---|
| **Dark combine**, **Flat combine**, **Bias combine** | How calibration frames are combined into masters. Defaults: median kappa-sigma for darks, median for flats and bias |
| **Optimize dark** | Scale the master dark to minimise noise in each light. Use it when darks were taken at a slightly different temperature or exposure |
| **Hot pixel removal** / **Cold pixel removal** | Replace defective pixels, found from the master dark and by comparison with their neighbours, by the median of same-colour neighbours |
| **Pedestal** | A small constant added after dark subtraction so that noise is not clipped at zero |
| **Save calibrated** / **Calibrated folder** | Also save each calibrated light frame |

Calibration order follows standard practice: subtract bias (or use the bias-subtracted dark), subtract the dark (scaled by exposure time when it differs), and divide by the flat normalised to a mean of 1 for each colour of the Bayer matrix. Flats are calibrated with dark flats or bias first.

### Registration settings

| Setting | Meaning |
|---|---|
| **Detection sigma** | Star detection threshold in units of background noise (5 by default). Lower it for sparse fields, raise it for noisy frames |
| **Max stars** | Maximum stars used per frame (1500) |
| **Alignment** | Transform model: Translation, Similarity (shift, rotation, scale), Affine (default), Homography (perspective), or Polynomial2 (second-order, for wide fields with distortion) |
| **Debayer** | Bilinear, SuperPixel (2×2 binning, no interpolation), MalvarHeCutler, or EdgeDirected (default, sharpest) |
| **Bayer override** | Force a Bayer pattern (RGGB, BGGR, GRBG, GBRG) if the file does not state it correctly |
| **Interp** | Interpolation when warping frames: Nearest, Bilinear, Bicubic or Lanczos3 (default, sharpest) |

### Combination and rejection

**Light combine** chooses how the aligned light frames are combined:

| Method | Description | Best for |
|---|---|---|
| **Average** | Plain mean; no rejection | Very few frames |
| **Median** | Median of each pixel | Quick results, 5–10 frames |
| **KappaSigma** | Mean after iteratively rejecting values more than κ standard deviations from the mean (default: κ 2.5, 5 iterations) | General use, 10+ frames |
| **MedianKappaSigma** | Like kappa-sigma but rejected values are replaced by the median | Darks and bias |
| **WinsorizedSigma** | Sigma clipping with Winsorized (robust) statistics | 10–20+ frames |
| **LinearFitClipping** | Fits a line to the sorted values and rejects points far from it | 15+ frames with varying sky |
| **GeneralizedEsd** | Generalised extreme Studentized deviate test (Rosner 1983) | Large stacks (50+) |
| **PercentileClipping** | Rejects values outside percentile limits | Very small stacks |
| **AutoAdaptiveWeightedAverage** | Iterative re-weighting that down-weights outliers smoothly (Stetson) | Many frames, no hard cut-off |
| **EntropyWeightedAverage** | Weights each frame locally by the information content of its histogram, keeping bright cores from frames where they are not saturated | High-dynamic-range targets such as M 42 |
| **Maximum** / **Minimum** | Brightest or faintest value | Star trails, special effects |

The κ low and high values, iterations, percentile limits and the ESD significance and maximum outlier fraction are in the expandable **Light combine** group.

### Normalisation and weighting

| Setting | Options |
|---|---|
| **Normalization** | None, Additive (match background offsets), AdditiveScaling (match offsets and noise, default), Multiplicative (match levels by scaling) |
| **Weighting** | None, Noise (inverse noise variance, default), Score (registration score), Snr |
| **Per channel background calibration** | Equalise the background of the red, green and blue channels so the sky is neutral grey |
| **Keep percent** | Use only the best N% of frames by score |
| **Min score ratio** | Reject frames scoring below this fraction of the reference frame's score |

Normalisation compensates for the sky getting brighter or fainter during the night (moonrise, twilight, light pollution changing with altitude), so that rejection compares like with like.

### Output area

| **Mode** | Result |
|---|---|
| **Standard** | The reference frame's area |
| **Mosaic** | The union of all frames: everything any frame covered, for mosaics and drifting fields |
| **Intersection** | Only the area covered by every frame, with no ragged edges |
| **Custom** | A rectangle you specify in **Custom rect** (x, y, width, height in reference pixels) |

### Drizzle

**Drizzle scale** (1, 2 or 3) reconstructs a higher-resolution image from dithered, undersampled frames (Fruchter & Hook 2002). Each input pixel is shrunk to a "drop" of size **Drizzle pix frac** (0.8 by default) and dropped onto the finer output grid. Drizzle helps when your stars are only 1–2 pixels wide and you dithered between frames; it needs many frames (30 or more) and costs memory and time.

### Comets

Comets move against the stars during the night. **Comet** chooses how to handle this:

| Mode | Result |
|---|---|
| **None** | Normal star-aligned stack; the comet is smeared |
| **CometAligned** | Aligned on the comet's nucleus; the comet is sharp and the stars are trailed |
| **StarsAndComet** | Both stacks are made and combined, keeping sharp stars and a sharp comet |

Mark the comet's nucleus with **Mark comet in selected frame**: select a frame in the list, click the button, then click the nucleus in the preview. Do this in at least two frames, preferably the first and the last; Sidralune interpolates the comet's position in every other frame from the observation times.

## Stacking

Click **Stack checked frames**. Sidralune:

1. builds master bias, dark, dark flat and flat frames from the calibration frames (or uses the masters you loaded),
2. calibrates and debayers each light frame and warps it onto the reference,
3. writes the warped frames to temporary files, so memory use does not grow with the number of frames,
4. combines them in strips, in parallel on all processor cores, with the chosen rejection,
5. neutralises the background if requested.

Progress and the current stage appear in the status bar. When it finishes, the result is shown and a summary reports the number of frames used, the total exposure, the time taken and the method.

## The result

The stacked image is linear (unstretched) 32-bit floating point data. The preview shows it auto-stretched. **Stacked image adjustments** changes the preview only:

| Slider | Effect |
|---|---|
| **Black point** | Where black starts |
| **Midtones** | Brightness of the midtones |
| **Saturation** | Colour saturation |

| Button | Action |
|---|---|
| **Save stacked image…** | Save as 32-bit FITS (`Autosave.fits` by default), XISF or TIFF to keep the full data, or as PNG or JPEG with the preview stretch applied |
| **Open in Process** | Send the result to the [Process](Image-processing) workspace for background extraction, stretching and so on |

Keep the 32-bit linear file: all processing should start from it.

## File lists

**Save file list…** stores the frame list, check marks and types in a `.sdstack` file; **Open file list…** loads it again. Use this to come back to a project or to add frames from a second night.

## Recommended settings

| Situation | Settings |
|---|---|
| Fewer than 10 frames | Median, Alignment Affine |
| 10–30 frames | KappaSigma or WinsorizedSigma, AdditiveScaling, Noise weighting |
| 30+ frames | WinsorizedSigma or LinearFitClipping; consider GeneralizedEsd for 50+ |
| Satellite trails | WinsorizedSigma or GeneralizedEsd |
| Bright cores (M 42, M 31) | EntropyWeightedAverage |
| Undersampled, dithered frames | Drizzle scale 2, pix frac 0.7–0.8 |
| Wide field with a camera lens | Alignment Polynomial2 or Homography |
| Two nights with different framing | Mode Intersection (or Mosaic to keep everything) |
| Colour camera | Debayer EdgeDirected, Per channel background calibration on |
