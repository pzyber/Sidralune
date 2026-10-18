# Astrometry and framing

The **Astrometry** workspace finds exactly where an image points on the sky (plate solving), labels the objects in it, plans the framing of a target or a mosaic before you image it, and estimates the exposure you need. It has three tabs: **Plate solving**, **Framing & mosaics** and **Exposure planner**.

## Contents

- [Plate solving](#plate-solving)
  - [How the internal solver works](#how-the-internal-solver-works)
  - [Solvers](#solvers)
  - [Hints](#hints)
  - [Solving an image](#solving-an-image)
  - [Results and annotation](#results-and-annotation)
  - [Using the solution](#using-the-solution)
  - [When solving fails](#when-solving-fails)
- [Framing and mosaics](#framing-and-mosaics)
- [Exposure planner](#exposure-planner)
- [Where plate solving is used elsewhere](#where-plate-solving-is-used-elsewhere)

## Plate solving

Plate solving matches the pattern of stars in your image against a star catalogue. The result is a world coordinate system (WCS): a precise mapping between pixel positions and sky coordinates, including the image's centre, scale, rotation and distortion.

### How the internal solver works

1. Stars are detected in the image (the brightest 80 are used for matching).
2. Catalogue stars around the hint position are projected onto a tangent plane.
3. Triangles are formed from neighbouring stars in both sets. The shapes of triangles do not depend on scale, rotation or position, so matching triangles reveal matching stars even when the scale and orientation are unknown.
4. The most consistent set of matches gives a first transformation, which is then refined iteratively using all matched stars and fitted as TAN-SIP (a gnomonic projection plus polynomial distortion terms of the chosen order).
5. Without a position hint, the solver searches the sky. Without a scale hint, it tries field sizes from 0.25° to 60°. Mirrored images (taken with a diagonal) are handled.

### Solvers

| Solver | Needs | Best for |
|---|---|---|
| **Internal + Gaia DR3 (online)** | An internet connection the first time for each field (Gaia data are cached), and a rough position | Any field size, including narrow fields of a few arcminutes |
| **Internal (local catalog)** | The installed star catalogue (HYG, to about magnitude 9, or the built-in bright stars) | Wide fields (a few degrees and up), blind solving, no internet |
| **ASTAP (installed)** | [ASTAP](https://www.hnsky.org/astap.htm) and one of its star databases, installed in the default folder | Fast solving of any field offline |
| **astrometry.net (online)** | A free API key from nova.astrometry.net in **Settings → Observer & accounts** | Blind solving of anything, when the other options fail; slower |

When you choose the Gaia solver without a position hint, Sidralune falls back to the local catalogue for the blind search.

### Hints

Hints make solving faster and more reliable:

| Field | Meaning |
|---|---|
| **Hint RA** / **Hint Dec** | Approximate centre. Filled in automatically from FITS headers (`RA`/`DEC`, `OBJCTRA`/`OBJCTDEC` or an existing WCS) |
| **Search radius (°)** | How far from the hint to search |
| **Scale ("/px, 0=auto)** | Image scale in arcseconds per pixel. Filled in from `FOCALLEN` and `XPIXSZ` when present. 0 means unknown |
| **SIP order** | Distortion polynomial order: 0 (none) to 4. 2 suits most telescopes; use 3–4 for camera lenses and wide fields |

Image scale is 206.265 × pixel size (µm) ÷ focal length (mm). Frames taken by Sidralune's [Observatory](Observatory-and-Devices.md#fits-headers) already contain everything needed.

### Solving an image

1. Click **Open image…** (or drop a file onto the viewer). Colour raw images are debayered automatically.
2. Check or enter the hints.
3. Choose the **Solver** and click **Solve**.

The solve runs in the background; the status bar shows progress.

### Results and annotation

The result panel reports the centre, the scale, the rotation angle (and "flipped" for mirror-reversed images), the field size, the number of stars detected and matched, the RMS residual in arcseconds and the time taken.

On the image:

- matched stars are circled in green,
- an RA/Dec grid is drawn,
- deep-sky objects, named and Bayer stars, and comets and asteroids in the field (positions computed for the image's `DATE-OBS`) are marked and labelled, with the minor bodies' predicted magnitudes,
- the readout under the image shows RA and Dec at the cursor.

The list of objects in the field is also printed in the result panel.

### Using the solution

| Button | Action |
|---|---|
| **Write WCS to FITS…** | Saves a copy of the image with the WCS keywords (`CTYPE`, `CRVAL`, `CRPIX`, `CD` matrix and SIP coefficients) in its header, readable by any astronomy program |
| **Sync telescope to solution** | Tells the connected mount where it is really pointing, which corrects its pointing for the next slews |
| **Show field in sky chart** | Opens the [Sky chart](Sky-Chart.md) in atlas mode centred on the image, with the image outline drawn |

The WCS is also stored in the image in memory, so other workspaces (photometry, colour calibration, MPC astrometry) can use it.

### When solving fails

- Check the scale: a wrong focal length or pixel size is the most common cause. Set **Scale** to 0 to let the solver search.
- Increase the **Search radius**, or clear the hints for a blind solve.
- Use the Gaia solver for fields narrower than about 1°; the local catalogue does not have enough faint stars.
- Make sure the image has at least 15–20 well-defined stars: not trailed, not badly out of focus, not dominated by clouds or a bright gradient.
- Try ASTAP or astrometry.net.

## Framing and mosaics

The **Framing & mosaics** tab shows your camera's field of view on a sky chart, so you can compose the image before you take it, and plans mosaics of large targets.

1. Type a target in **Target** and click **Update**, or use **Frame** in the [Sky chart](Sky-Chart.md#object-information-panel) or the right-click menu's **Frame with camera here**.
2. The rectangle shows the field of the active telescope and camera from **Settings → Equipment**; **Camera / scope** shows the field size and image scale.
3. Drag the chart to move the frame, and use the mouse wheel to zoom.
4. Set **Rotation (°)** to turn the camera.
5. For a mosaic, set **Mosaic columns**, **Mosaic rows** and **Overlap (%)** (15% is a safe minimum for stitching).

The tile list gives the centre of every tile (row, column, RA and Dec). **Copy tile list** copies it to the clipboard, ready to paste into target containers in the [sequencer](Sequencer.md).

Tiles are laid out on the tangent plane around the mosaic centre and rotated together, so the tiles stay aligned with each other even near the celestial pole. When stacking the tiles, use **Mode: Mosaic** in [Deep-sky stacking](Deep-Sky-Stacking.md#output-area).

## Exposure planner

The **Exposure & signal-to-noise planner** estimates how long you need to integrate, using the active telescope and camera and your site's sky brightness.

| Field | Meaning |
|---|---|
| **Imaging system** | The active telescope and camera, with image scale and focal ratio |
| **Target surface brightness (mag/″²)** | How bright the faint parts you want to show are: about 20–21 for bright nebula cores, 22–23 for galaxy arms and faint nebulae, 24+ for very faint outer structure |
| **Sky brightness (mag/″²)** | Your site's SQM value (filled in from the active site) |
| **Filter bandwidth (nm)** | About 88 nm for a luminance or broadband filter per channel, 3–7 nm for narrowband |
| **Sub-exposure (s)** | Length of each frame |
| **Target SNR** | Signal-to-noise ratio you want; 10 is a detection, 30 a clean result, 100 very smooth |

The results:

- signal and sky electrons per pixel per sub-exposure,
- the SNR of a single sub,
- the total integration time and number of subs for the target SNR,
- the **optimal sub-exposure**: the shortest sub for which read noise adds less than 5% to the sky noise; longer subs gain almost nothing but cost more when a frame is lost,
- the critical focus zone in micrometres (how precisely you must focus at your focal ratio),
- the focal length that samples the site's typical seeing at the Nyquist limit for your pixels.

The graph shows the SNR growing with total integration time, with your target SNR marked.

Narrowband filters dramatically reduce the sky background, which is why they work so well from light-polluted sites: compare 88 nm with 7 nm and see how the required time changes.

## Where plate solving is used elsewhere

- **Observatory → Imaging → Center** and the sequencer's **Center target** instruction solve and re-slew to put the target in the middle of the frame.
- **Observatory → Polar alignment** solves three images to measure the polar axis.
- **Process → Solve** sends an image here; solved images get photometric colour calibration and a WCS grid.
- **Photometry → Measure** solves frames without a WCS before measuring stars.
- **Solar sys. → MPC astrometry** measures positions on solved images.
- Scripts can call `PlateSolver.Solve` directly.
