The **Process** workspace turns a linear stacked image into a finished picture, and offers analysis tools for single images. It covers the whole processing chain: gradient removal, colour calibration, deconvolution, stretching, wavelet sharpening, noise reduction, star removal and reduction, narrowband combination and PixelMath, with unlimited undo.

## Contents

- [Layout](#layout)
- [Opening and saving](#opening-and-saving)
- [Display controls](#display-controls)
- [History and undo](#history-and-undo)
- [A typical workflow](#a-typical-workflow)
- [Histogram & history](#histogram--history)
- [Stretch](#stretch)
- [Background extraction](#background-extraction)
- [Colour](#colour)
- [Deconvolution](#deconvolution)
- [Wavelets](#wavelets)
- [Noise reduction](#noise-reduction)
- [Stars & structures](#stars--structures)
- [Geometry](#geometry)
- [Channels](#channels)
- [PixelMath](#pixelmath)
- [Statistics and plate solving](#statistics-and-plate-solving)

## Layout

The image fills the centre. The toolbar above it has file, undo and display controls. The panel on the right holds collapsible sections, each a group of tools; click a heading to open or close it. Every tool works on the whole image and adds a step to the history. While a tool is working, the other tools and **Open**, **Save as**, **Undo** and **Redo** wait for it, but the tool settings can still be changed: each tool reads its settings when you click it.

## Opening and saving

| Action | How |
|---|---|
| Open | **Open** or Ctrl+O. FITS, XISF, TIFF, PNG, JPEG, BMP, DNG, CRW, CR2 and CR3 files. Raw colour files are debayered. You can also drop a file on the image, or send an image from **Deep sky → Open in Process**, **Planetary**, or a script |
| Save | **Save as** or Ctrl+S. FITS (16-bit or 32-bit float), XISF (16-bit or 32-bit float, compressed), TIFF (16-bit or 32-bit float), PNG and JPEG (8-bit) |

When you save to PNG or JPEG while the display is auto-stretched, the stretch is applied to the saved file so it looks the same as on screen. FITS, XISF and TIFF files keep the actual data; save the linear image in one of these before you stretch it permanently.

## Display controls

Display controls change only what you see, never the data.

| Control | Effect |
|---|---|
| **Display** | **Auto linked** (automatic stretch, the same for all colours, so the colour balance is kept), **Auto unlinked** (each colour stretched separately, which hides colour casts such as the green of a raw image) or **None** (the values as they are, best for images that are already stretched). It only changes what you see, not the image |
| **Invert** | Show a negative, useful for spotting faint nebulosity and gradients |
| **False colour** | Map brightness to a colour scale to reveal gradients and faint structure |
| **WCS grid** | Draw an RA/Dec grid on plate-solved images |
| Fit, **1:1** | Fit the image to the window (key 0) or show actual pixels (key 1) |

Mouse wheel zooms around the cursor; drag to pan. The readout under the image shows the pixel position, values and, for plate-solved images, RA and Dec.

## History and undo

Every operation is recorded in the history list (in **Histogram & history**). **Undo** (Ctrl+Z) and **Redo** (Ctrl+Y) step through it as far as you like, so you can experiment freely.

## A typical workflow

For a deep-sky image straight from [Deep-sky stacking](Deep-sky-stacking):

1. **Geometry → Crop edges** to remove the ragged stacking borders.
2. **Background extraction → Extract background** to remove light-pollution gradients.
3. **Colour → Background neutralization**, then **Photometric colour calibration** (after plate solving) or **Star colour calibration**.
4. **Deconvolution** (optional), while the image is still linear.
5. **Noise reduction → Multiscale wavelet denoise** (gently) on the linear image.
6. **Stretch**: arcsinh to keep star colours, then GHS or levels for the final shape; or simply **Apply auto stretch (STF) permanently**.
7. **Stars & structures**: **Local contrast enhancement** or **HDR multiscale**, **Reduce stars** if the field is crowded.
8. **Colour → Remove green cast (SCNR)** and **Apply saturation**.
9. Save as TIFF for further editing and JPEG or PNG for sharing.

For planetary images, use the [Planetary](Planetary,-lunar-and-solar-imaging) workspace, which has dedicated wavelet and derotation tools.

## Histogram & history

Shows the histogram of each channel (logarithmic counts) and the history list.

## Stretch

Stretching makes faint linear data visible. Apply it after background extraction, colour calibration and deconvolution.

| Tool | Description |
|---|---|
| **Apply auto stretch (STF) permanently** | Applies the automatic display stretch (screen transfer function: shadows clipped at the median minus 2.8 normalised MAD, midtones chosen so the background lands at 25% brightness) to the data |
| **Apply generalized hyperbolic stretch** | Generalised hyperbolic stretch (Payne & Cranfield 2021). **GHS stretch D** sets the strength, **GHS local b** how concentrated it is around the **Symmetry point** (the brightness where contrast is increased most, usually just above the background), and **Protect shadows** / **Protect highlights** limit it below and above chosen levels. Apply it several times with moderate values for fine control |
| **Apply arcsinh stretch (colour preserving)** | Stretches brightness with the inverse hyperbolic sine while keeping the ratio between the colour channels, so stars keep their colour instead of turning white. **Arcsinh factor** sets the strength |
| **Apply levels** | Classic levels: **Levels black**, **Levels white** and **Levels gamma** |
| **Local contrast (CLAHE)** | Contrast-limited adaptive histogram equalisation; brings out detail in nebulae, use sparingly |

## Background extraction

Removes gradients caused by light pollution, the Moon, twilight and vignetting residuals.

1. Sidralune places samples on a grid (**Samples per axis**) and measures the background in each with robust statistics. Samples on stars and nebulosity are rejected using **Tolerance** (in units of background noise).
2. **Show samples** draws the accepted samples on the image, so you can check that none sit on the object.
3. A smooth surface is fitted through them: **Model** RadialBasis (thin-plate spline, flexible, default; **RBF smoothing** controls how closely it follows the samples) or Polynomial (very smooth; **Polynomial degree** 1–4).
4. **Extract background (gradient removal)** removes it with **Correction** Subtract (for additive light pollution, default) or Divide (for vignetting).

Apply it to the linear image, before colour calibration. For large nebulae that fill the frame, use the polynomial model with degree 1 or 2 so the nebula is not mistaken for background.

## Colour

| Tool | Description |
|---|---|
| **Background neutralization** | Makes the sky background neutral grey by equalising the channels' background levels |
| **Star colour calibration** | Measures unsaturated stars in each channel and scales the channels so that the average star is white (like a G2V star) |
| **Photometric colour calibration (needs WCS)** | Matches the stars in a plate-solved image to Gaia DR3 (online) and scales the channels so that their colours match the stars' catalogue colours through a blackbody model. The most accurate colour balance. Plate solve the image first with **Solve** |
| **Remove green cast (SCNR)** | Subtractive chromatic noise reduction removes the green tint common in one-shot-colour images. **SCNR amount** sets the strength |
| **Apply saturation** | Changes colour saturation (**Saturation**, 1 = unchanged) |
| **Apply hue shift** | Rotates hues by **Hue shift (°)** |
| **Align RGB channels** | Registers the red and blue channels onto green, correcting chromatic misalignment from atmospheric dispersion or optics. **Stop** in the toolbar ends it between the channels |

## Deconvolution

Deconvolution reverses part of the blurring caused by the atmosphere and optics, sharpening stars and fine detail. Apply it to the linear image before stretching, ideally to an image with good signal.

| Setting | Meaning |
|---|---|
| **Method** | RichardsonLucy, RichardsonLucyTV (with total-variation regularisation that suppresses noise amplification; default), Wiener (fast, frequency domain), VanCittert, Landweber, BlindRichardsonLucy (estimates the PSF while restoring) |
| **PSF** | The blur shape: From stars (empirical, measured from the stars in the image; best), Gaussian, Moffat or Airy (diffraction pattern of the aperture, including a central obstruction) |
| **PSF FWHM (px)** | Width of the PSF for the analytic shapes. **Measure FWHM** fills it in from the image's stars |
| **Iterations** | More iterations sharpen more and amplify noise more (10–50 typical) |
| **TV regularization** | Strength of the noise suppression for RichardsonLucyTV (0.001–0.005 typical) |
| **Deringing** | Suppresses the dark rings that deconvolution can create around bright stars |
| **Wiener K** | Noise-to-signal parameter for the Wiener method |

Click **Deconvolve**. **Stop** in the toolbar ends it part way, leaving the image as it was. If dark halos appear around stars, increase **Deringing** or reduce **Iterations**.

## Wavelets

Sharpening by wavelet layers. The image is split with the starlet (à trous) transform into detail layers of 1, 2, 4, 8, 16 and 32 pixels plus a residual. Each **Layer** slider amplifies its scale (0 leaves it unchanged); **Denoise layer 1-2** soft-thresholds the finest layers against the measured noise first, so noise is not sharpened.

Typical deep-sky values: layer 1 = 0, layers 2–3 = 0.3–1. Click **Apply wavelet sharpening**.

## Noise reduction

| Tool | Description |
|---|---|
| **Multiscale wavelet denoise** | Thresholds the fine wavelet layers against the noise level measured at each scale; fast and effective. **Strength** sets how much is removed |
| **Non-local means (slow, best quality)** | Averages similar patches across the image; preserves edges and detail best, but takes longer. **Stop** in the toolbar ends it part way |
| **Remove banding (rows)** | Removes horizontal banding from CMOS and DSLR sensors |
| **Cosmetic correction (hot/cold pixels)** | Replaces isolated hot and cold pixels by the median of their neighbours |

## Stars & structures

| Tool | Description |
|---|---|
| **Reduce stars** | Shrinks stars with morphological erosion blended through a star mask, so the background and nebula are not affected. **Reduction amount** sets the strength |
| **Remove stars (starless + save star layer)** | Detects stars, fills them in from their surroundings by harmonic diffusion and adds matching noise, producing a starless image. You are asked where to save the star layer, so you can process the nebula and stars separately and blend the stars back later in an image editor (screen blending). **Stop** in the toolbar ends it part way |
| **HDR multiscale (galaxy/nebula cores)** | Compresses large-scale brightness while keeping small-scale detail, revealing structure in bright cores such as M 42, M 31 or globular clusters |
| **Local contrast enhancement** | Increases contrast of medium-scale structure |

## Geometry

| Tool | Description |
|---|---|
| **Crop edges** | Removes **Crop margins (%)** from every side |
| **Rotate** | Rotates by **Rotate (°)** |
| **Flip horizontal** / **Flip vertical** | Mirror the image |
| **Resample (Lanczos-3)** | Resizes to **Resample (%)** with high-quality Lanczos interpolation |
| **Bin 2×2** | Averages 2×2 blocks, halving the size and doubling the signal-to-noise ratio |

## Channels

| Tool | Description |
|---|---|
| **Extract luminance** | Replaces a colour image by its luminance |
| **Split channels to files…** | Saves R, G and B as separate mono FITS files |
| **RGB combine (R, G, B files)…** | Combines three registered mono images into a colour image |
| **LRGB: apply luminance file…** | Replaces the luminance of the current colour image with a mono luminance file, keeping its colour |
| **Narrowband combine (Ha, OIII, SII files)…** | Combines narrowband images with the chosen **Palette**: SHO (Hubble palette), HOO (bicolour), HSO, FORAXX (a dynamic palette that blends Ha and OIII by brightness) or Natural (approximate true colour). File names containing Ha, OIII or SII are recognised automatically |

Combine registered, stretched or linear images of the same size. For mono imaging, stack each filter in [Deep sky](Deep-sky-stacking) with the same reference frame so they align.

## PixelMath

PixelMath applies a formula to every pixel. Type it in **f(x) =** and click **Apply PixelMath**.

| Element | Meaning |
|---|---|
| `$T` | The current image |
| `$x`, `$y`, `$c` | Pixel column, row and channel |
| Operators | `+ - * / ^`, comparisons `< > <= >= == !=`, logic `&& ||`, unary minus |
| Functions | `min`, `max`, `avg`, `abs`, `sqrt`, `exp`, `ln`, `log`, `pow`, `sin`, `cos`, `mtf(m, x)`, `iif(condition, a, b)`, `clamp(x, lo, hi)` |
| Constants | `pi`, `e` |

Examples:

| Formula | Effect |
|---|---|
| `mtf(0.25, $T)` | Midtones transfer: brightens with midtone balance 0.25 |
| `$T * 1.2` | Brighten by 20% |
| `iif($T > 0.9, 0.9, $T)` | Clip highlights at 0.9 |
| `clamp(($T - 0.05) / 0.9, 0, 1)` | Linear stretch from 0.05 to 0.95 |

## Statistics and plate solving

**Stats** shows the image size, statistics for each channel (minimum, maximum, mean, median, standard deviation, MAD and noise), a star summary (count, FWHM, HFR and eccentricity) and the full FITS header. **Solve** plate solves the image without leaving Process, with the solver, search radius and SIP order chosen in [Astrometry → Plate solving](Astrometry-and-framing#plate-solving) and the pointing and scale hints in the image's FITS header. It runs like a tool: the tools, **Open**, **Save as**, **Undo** and **Redo** wait for it. **Stop** ends it part way. The solution is added to the image as a **Plate solve** step in the history, so the image can then be colour-calibrated photometrically and shown with a WCS grid; **Undo** takes it away again. If no solution is found, a message says so: save the image and open it in Astrometry to try other hints or another solver there. **Solve** itself waits while a tool is working.
