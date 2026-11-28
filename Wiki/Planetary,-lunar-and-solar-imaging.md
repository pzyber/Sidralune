The **Planetary** workspace processes high-frame-rate videos of the planets, the Moon and the Sun using the "lucky imaging" technique: record thousands of short frames, keep the sharpest moments when the atmosphere is steady, align and stack them, then sharpen. Five tabs follow the workflow:

| Tab | Does what |
|---|---|
| **Pre-process** | Centre, crop, debayer, sort and reject frames; correct field rotation |
| **Lucky stacking** | Analyse frame quality, align with many alignment points, stack the best frames |
| **Sharpen & deconvolve** | Wavelet sharpening and deconvolution, with a profile for each object, for one image or a whole night's |
| **Derotation** | Combine images taken minutes apart by undoing the planet's rotation |
| **Solar & lunar** | Limb tools, prominences, colourising, mosaics |

> Never point a telescope at the Sun without a certified full-aperture solar filter or a dedicated solar telescope.

## Contents

- [Recording tips](#recording-tips)
- [Pre-process](#pre-process)
- [Lucky stacking](#lucky-stacking)
- [Sharpen & deconvolve](#sharpen--deconvolve)
  - [Profiles](#profiles)
  - [Sharpening many images](#sharpening-many-images)
- [Derotation](#derotation)
- [Solar & lunar](#solar--lunar)
- [Field rotation on alt-az mounts](#field-rotation-on-alt-az-mounts)
- [WinJUPOS file names](#winjupos-file-names)
- [A complete Jupiter workflow](#a-complete-jupiter-workflow)

## Recording tips

- Use a planetary camera and record SER files. SER keeps the full bit depth and a time stamp for every frame. AVI and image sequences also work.
- Sidralune can frame, focus (with **Live view**) and record SER videos itself in **Observatory → Imaging → Camera → [Planetary](Observatory-and-devices#planetary-video)** with any ASCOM, Alpaca or INDI camera, and **Open in Planetary** brings the result straight into the Pre-process tab. Those interfaces transfer whole frames, so for the highest frame rates record with the camera maker's software or a dedicated capture program (SharpCap, FireCapture) and open the file here.
- Choose a focal ratio of about 5 × the pixel size in micrometres (f/15 for 2.9 µm pixels). **Settings → Equipment** shows the Nyquist focal ratio for your camera.
- Keep exposures short (2–15 ms) and gain high enough for a histogram around 50–70%.
- Limit each video to the time the planet takes to rotate noticeably: about 60–90 seconds for Jupiter, 3 minutes for Saturn and Mars. [Derotation](#derotation) lets you combine several such videos.

## Pre-process

The **Pre-process** tab prepares a video for stacking. Click **Open source…** to load a SER or AVI file or a sequence of images, and drag the slider to preview individual frames. All options are in the property grid on the right:

| Group | Options |
|---|---|
| Frame range | **First frame**, **Last frame** (−1 for the end), **Frame step** |
| Colour | **Debayer** (with **Debayer method** and **Force bayer**), **Convert to mono** |
| Object detection | **Object mode**: Planetary (small object, centred), FullDisc (Sun or Moon whole disc), Surface (close-ups of the Moon or Sun where the disc fills the frame; no centring), None. **Detection threshold** (−1 for automatic, Otsu's method) |
| Centring and cropping | **Centre object**, **Crop width** / **Crop height** (0 for automatic: object size × **Auto crop margin**) |
| Rejection | **Reject edge frames** (object touching the frame edge), **Min/Max object size fraction** (relative to the median size, rejects frames distorted by seeing or clouds), **Reject brightness outliers** with **Brightness tolerance** |
| Orientation | **Rotate deg**, **Flip horizontal**, **Flip vertical** |
| Field rotation | **Field derotation** (rotate frames to undo alt-az field rotation), **Field derotation measure** (estimate the rotation from the images instead of computing it), **Alt az mount**. Choose the target in **Field derotation target** on the toolbar |
| Dispersion | **Align RGB channels**: corrects atmospheric dispersion (colour fringes) at low altitude |
| Levels | **Gain**, **Gamma** (also brighten the preview of dim videos) |
| Quality | **Estimate quality** with **Metric** (Laplacian, Gradient or LocalContrast), **Keep best percent** or **Keep best count**, **Order** (Original or Quality) |
| Output | **Format** (Ser, Avi, Fits, Tiff, Png, Bmp, Jpeg), **Output bit depth**, **Output folder**, **Output name**, **Max frames per file** (split long videos), **Win jupos naming** and **Planet code** |

Frames are shown as recorded, without the automatic stretch of the deep-sky workspaces, which would turn the black sky around a planet into noise. The same goes for every tab of the Planetary workspace.

The options can be changed while a run is working: the run keeps the settings it started with, and the changes apply to the next run. The same holds in Lucky stacking.

Click **Process**. The **Frame quality** plot shows every frame's quality score and which were kept, and a summary toast reports how many frames were written and why others were rejected. Click **Stacking →** to open the result in the Lucky stacking tab (the first file when **Max frames per file** split it; frames saved as images open together). The button waits while a stack is running there.

Pre-process and Lucky stacking each have their own video, so you can pre-process one recording while another is being stacked. While a tab works, its own **Open source…** and start button wait for it, and while a deconvolution runs in Sharpen & deconvolve, **Sharpen →** waits too.

Pre-processing is optional for good videos of planets, but it shrinks files dramatically (a cropped 300 × 300 video instead of full frame), removes bad frames, and is essential for field derotation.

## Lucky stacking

The **Lucky stacking** tab is a lucky-imaging stacker with multi-point alignment.

1. Click **Open source…**, or **Stacking →** in the Pre-process tab to bring its result here.
2. Enter the percentages of frames to stack in **Stack %**, for example `10, 25, 50`; one stack is made for each.
3. Click **Analyse & stack**. **Stop**, next to it, ends a stack part way. **Stop** next to **Process** in Pre-process does the same for pre-processing.

What happens:

1. **Quality analysis**: every frame is scored with the chosen metric after a light blur (**Noise robust**) so that noise does not count as detail. The **Frame quality (sorted)** plot shows the scores from best to worst, with markers at your percentages; a steep curve means variable seeing.
2. **Reference**: the best frames (about 5%, or **Reference frames**) are averaged into a clean reference image.
3. **Global alignment**: every frame is aligned to the reference, either on the planet's disc (**Align** Planet) or on surface features (Surface, for lunar and solar close-ups).
4. **Alignment points**: a grid of boxes (**Ap size** pixels, placed where the image is brighter than **Ap min brightness**) is laid over the object. Each box is aligned separately in every frame within **Ap search radius**, which corrects the local distortions caused by seeing. **Multi scale aps** adds larger boxes for robustness. The boxes are drawn on the preview.
5. **Local frame selection**: with **Local quality** on, each alignment point uses the best frames for that area, not just the globally best frames, since seeing varies across the image.
6. **Stacking**: the selected parts of the selected frames are combined with smooth blending between alignment points. **Drizzle scale** (1, 1.5, 2 or 3) can increase resolution for undersampled data. **Normalize** equalises brightness between frames.

**Next stack** cycles through the stacks you made. **Save stacks…** writes them as 16-bit TIFF files, named with the WinJUPOS convention and the percentage (for example `2026-10-18-2215_3_0000_p25.tif`) when the video has time stamps. When the video's name or folder names the planet, its one-letter code follows the time (`2026-10-18-2215_3-J_0000_p25.tif`), so the Sharpen & deconvolve tab knows which profile to use. **Sharpen →** sends the current stack to the next tab.

Other options: **Use field derotation** applies the per-frame rotation from pre-processing; **Max frames in memory** limits memory use for long videos; **Sharpen** applies a quick wavelet sharpening to the output.

## Sharpen & deconvolve

Stacked planetary images look soft until they are sharpened. Open an image with **Open image…** or send one from Lucky stacking with **Sharpen →**.

The tab works in two layers: the **source image** (the image you opened, with the steps you applied to it), and **wavelet sharpening** applied live on top of it. Deconvolution and channel alignment are steps: they change the source, and the line under the buttons lists them in order ("Steps: Deconvolution 2.0 px, 15 iterations → Align RGB channels"). The sliders only change what is shown on top of it.

**Wavelets**: six **Layer** sliders amplify detail at 1, 2, 4, 8, 16 and 32 pixel scales using the à trous (B3 spline) wavelet transform, and **Denoise** soft-thresholds the finest layers against the measured noise first. There is no button: a moment after you move a slider, the image is sharpened again from the source (the status bar shows a "Wavelets" task meanwhile). The sliders never pile up, so moving them back undoes the sharpening. Start from the defaults (layers 1–3 at 120, 60 and 20, the rest at 0, Denoise at 60) and adjust: layer 1 brings out the finest detail but also the noise, so for a noisy stack lower it or raise **Denoise**.

**Deconvolution**: **Richardson-Lucy deconvolution (TV)** restores detail with a Gaussian PSF of **Deconv. FWHM** pixels over **Iterations**, with total-variation regularisation to limit noise. Use it gently (FWHM 1.5–3 px, 10–30 iterations). It is a step, so it always sits underneath the wavelets, and the wavelets are applied again to the result.

**Align RGB channels (ADC)** registers the red and blue channels onto green, removing the colour fringes of atmospheric dispersion. It is a step too.

**Stop**, under these two buttons, ends a deconvolution part way, or a channel alignment between the red and the blue channel; the image is left as it was. **Revert to the opened image** takes all the steps off again; the sliders stay where they are.

**Save result…** saves the image as shown: the source with the wavelet sharpening. It suggests the image's own name ending in `_sharpened`, in the image's folder.

The **Solar & lunar** tab has a separate **Wavelet sharpen** button with fixed settings, for quick full-disc sharpening.

### Profiles

Jupiter, Saturn, Mars and the Moon each need their own sharpening, so the tab keeps a **profile** for each object: the steps, the six layers, **Denoise** and the deconvolution settings. **Profile** shows the object the settings are for: **Other** (images whose object is not known), the Sun, the Moon or a planet.

- **The object comes from the file name.** When you open an image, Sidralune looks for the object in its name, then in the names of the folders above it:
  - an English name, also at the start of a word (`Jupiter`, `JupiterRGB`), or a short name as a whole word (`Jup`, `Sat`, `Ven`, `Mer`, `Ura`, `Nep`), as FireCapture writes them;
  - the one-letter code that Pre-process and **Save stacks…** put after a WinJUPOS time (`2026-11-28-2130_4-J_0000.tif`);
  - the folder SharpCap files captures under (`Saturn\2026-11-28\21_30_45\`), or the video's folder above AutoStakkert's `AS_P25`.

  **Profile** then switches to that object. When nothing names it, **Profile** stays as it was.
- **When opening: Apply the profile** (on by default) applies the object's profile to the image: its settings are shown and its steps are run on the image, in order. Turn it off to open images as they are.
- **Choosing an object in Profile** applies its profile to the image in the same way. An object without a profile keeps the settings shown, so you can make one for it.
- **A profile is kept** when you click **Save result…**, **Sharpen files…** or **Watch folder…**: the settings shown become the profile of the object in **Profile**. A toast after **Save result…** says which profile was kept. Profiles are kept with the other settings, so they are there next time.

### Sharpening many images

**Sharpen files…** sharpens a whole night's stacks at once. Choose the files (the dialog opens in the folder of the image you have open; Ctrl+A selects them all). Each image gets its own object's profile, so stacks of Jupiter, Saturn and Mars can be done together. An image whose object is not known, or whose object has no profile yet, gets the settings shown. Each result is saved beside its image as a 16-bit TIFF, with `_sharpened` added to the name (`2026-11-28-2130_4-J_0000_sharpened.tif`), so WinJUPOS still reads the time. Results are never sharpened again. A toast says how many images were done, and names the first that could not be. **Stop** ends it after the image being sharpened.

**Watch folder…** does the same with each new image that appears in a folder, or in a folder under it, while you go on stacking. Choose the folder your stacker saves into, or the folder of the videos (AutoStakkert puts its stacks in a folder such as `AS_P25` beside the video). Sidralune waits until a new image has been written completely, then sharpens it with its object's profile and says so in a toast. An image that is saved again later is sharpened again. Watching runs beside the tab's own work, and the line under the buttons says which folder is watched. Click **Stop watching** to end it.

## Derotation

Jupiter rotates once every 9 h 55 min, so features move visibly in a couple of minutes. The **Derotation** tab combines several stacked images taken over 10–30 minutes, which raises the signal-to-noise ratio and allows stronger sharpening.

1. Click **Add images…** and select stacks of the same planet. Each image's time comes from its FITS `DATE-OBS` or from a WinJUPOS-compatible file name (`YYYY-MM-DD-HHMM_T`, see below). The images appear in a list in time order; click one to view it. **Remove selected** (or the Delete key) takes images out again and **Clear list** empties it; the target time follows the images that are left.
2. Choose the **Planet** (Jupiter, Saturn, Mars or Venus).
3. Set the **Target time (UTC)**, normally the middle of the series.
4. Set **Image north angle (°)** if your images are not north-up, and tick **Mirrored** if they are mirror-reversed (taken with a diagonal).
5. Tick **Compensate** next to **Limb darkening comp.** to correct for the darkening towards the limb.
6. Click **Derotate & stack to target time**. **Stop**, under it, ends derotating (or loading images) part way.

Each image's disc is detected (limb fitting), then re-projected on the planet's oblate spheroid as if it had been taken at the target time, using the planet's rotation period, the sub-Earth latitude and the pole's position angle from the ephemeris. The re-projected images are averaged, and **Save result…** stores the derotated stack.

Rotation periods used: Jupiter System III (9 h 55 m 29.7 s), Saturn System III (10 h 39 m 22 s), Mars (24 h 37 m 22 s) and Venus' cloud tops (about 96 hours, retrograde).

## Solar & lunar

Tools for full-disc and close-up images of the Sun and Moon. Click **Open image…**.

| Tool | Description |
|---|---|
| **Detect limb** | Fits a circle to the disc edge (radial gradient edges, RANSAC and least squares). Needed by the other tools |
| **Flatten limb darkening** | Divides out the Sun's limb darkening so the disc has an even brightness and surface detail is visible to the edge. **Limb flattening** sets the strength |
| **Enhance prominences** | Brightens the region just outside the limb (with **Prominence gain** and **Prominence gamma**) so that H-alpha prominences show at the same time as the disc |
| **Colourise H-alpha** | Maps a mono H-alpha image to the familiar orange-red palette |
| **Invert (negative)** | Inverts the disc, a popular way to show solar surface detail |
| **Stitch mosaic tiles…** | Combines overlapping tiles of the Moon or Sun into one image. Select the tiles in order (left to right, top to bottom); their overlaps are matched and blended |
| **Stop** | Ends whichever tool is working (stitching included), usually within a moment; the image is left as it was |
| **Save…** | Saves the result |

## Field rotation on alt-az mounts

On an altitude-azimuth mount (a Dobsonian, a fork mount without a wedge, most GoTo mounts), the field of view rotates around its centre as the object crosses the sky, at a rate that depends on the latitude, the altitude and the azimuth. For planetary videos of a minute or two this rarely matters, but for long sequences, mosaics and derotation it does.

In **Pre-process**, choose the object in **Field derotation target**, tick **Field derotation** and **Alt az mount**. Sidralune computes the parallactic angle for each frame's time stamp from your site and rotates every frame back to the orientation of the first one. If the time stamps are missing or wrong, tick **Field derotation measure** to estimate the rotation between frames from the images themselves instead.

Note that this field rotation is different from the planet's own rotation, which is handled by [Derotation](#derotation).

## WinJUPOS file names

WinJUPOS identifies images by the time in their file names, in the form `YYYY-MM-DD-HHMM_T`, where `T` is the tenth of a minute (UTC) at the middle of the recording. Sidralune writes `YYYY-MM-DD-HHMM_T-Planet_NNNN` when pre-processing with **Win jupos naming** (the planet part comes from **Planet code** and is left out when empty; `NNNN` is a running number), and `YYYY-MM-DD-HHMM_T-Planet_NNNN_pXX` from **Save stacks…** (`XX` is the stack percentage; the planet part is the one-letter code of the planet the video's name or folder names, and is left out when none does). WinJUPOS reads the time from these names, and so does the Derotation tab. **Sharpen files…** adds `_sharpened` at the end, which keeps the time readable.

## A complete Jupiter workflow

1. Record five 90-second SER videos in a row with a colour camera (a [sequence](Sequencer#instructions) with **Record video** in a loop can do this unattended).
2. **Pre-process** each: Object mode Planetary, Centre object, Reject edge frames, Keep best percent 100, Format Ser, Win jupos naming on.
3. **Lucky stacking**: Stack % `25`, Ap size 48, Local quality on. **Save stacks…**.
4. **Derotation**: add the five stacks, planet Jupiter, target time in the middle, **Derotate & stack to target time**, **Save result…**.
5. **Sharpen & deconvolve**: open the derotated image, deconvolve gently, then wavelet sharpen with layer 2 strongest, **Align RGB channels (ADC)**, **Save result…**. These settings become the Jupiter profile.
6. Optionally fine-tune colour and levels in [Process](Image-processing).
7. On later nights, **Sharpen files…** sharpens all the Jupiter stacks with that profile in one go, or **Watch folder…** sharpens each stack as soon as it is saved.
