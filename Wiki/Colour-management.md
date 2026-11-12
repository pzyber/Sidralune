Sidralune manages colour with ICC profiles: every image is understood through a colour profile, images are shown through your monitor's profile, profiles are kept when files are opened and written when they are saved, and you can preview how an image will look in another profile (soft-proofing). Choose how it works in **Settings → Colour** (see [Settings](Settings#colour)).

## Contents

- [Why it matters](#why-it-matters)
- [Profiles and the working profile](#profiles-and-the-working-profile)
- [On screen](#on-screen)
- [Opening images](#opening-images)
- [Assigning and converting](#assigning-and-converting)
- [Saving](#saving)
- [Soft-proofing and the gamut warning](#soft-proofing-and-the-gamut-warning)
- [Night vision mode](#night-vision-mode)
- [Good practice](#good-practice)

## Why it matters

The numbers in an image only become colours through a colour profile, which says what red, green and blue mean. The same numbers look different in sRGB and in ProPhoto RGB, and a monitor shows numbers in its own way. Without colour management, a wide-gamut monitor (one that shows more saturated colours than sRGB, as many photo monitors and newer laptops do) makes every image look too saturated, and images in a wide profile such as Adobe RGB or ProPhoto RGB look dull. With colour management, colours look the same on every monitor that has a profile, and as in other colour-managed programs.

## Profiles and the working profile

- An image's **profile** comes from its file (FITS, XISF, TIFF, PNG and JPEG can carry one; see [File formats](File-formats#colour-profiles)), or is assigned in Process.
- The **working profile** is the profile Sidralune takes images to be in when they have none. FITS files, raw frames, stacks and videos never carry a profile, so for astronomical data the working profile decides how the colours are understood. It is **ProPhoto RGB** unless you choose another in Settings → Colour.
- Sidralune has these profiles built in: sRGB, Display P3, Adobe RGB (1998) compatible and ProPhoto RGB, each also in a linear (gamma 1.0) version, and grey profiles with the sRGB curve, gamma 2.2, gamma 1.8 and linear. Every profile installed in Windows can be chosen too, or any `.icc`/`.icm` file with **Other file…**.
- Each space keeps its standard tone curve. ProPhoto RGB's gamma of 1.8 is part of its definition; the monitor's own response is handled by the monitor profile, so the working profile does not need to match the screen.
- Profiles are listed by name. The readout under an image shows its profile, for example "ProPhoto RGB", or "Untagged (ProPhoto RGB)" for an image without one.

The working profile is deliberately not the monitor's profile: a monitor profile describes one particular screen (and changes when you calibrate or change monitors), so images in it would change colour on every other screen.

## On screen

Every image view (Deep sky, Process, Planetary, Astrometry, Observatory and the others) shows images through the profile Windows uses for the monitor the window is on. Move the window to another monitor and the image is converted for that one. The conversion uses the **rendering intent** set in Settings → Colour (Perceptual unless you choose another) and only changes what you see, never the image.

- Calibrate your monitor and make its profile the monitor's default in Windows (calibration software does this; otherwise use Windows' **Colour Management**, `colorcpl`). Without one, the monitor is taken to be sRGB.
- When Windows itself manages colour on a display (HDR, or automatic colour management on Windows 11), Windows converts what programs draw from sRGB. Sidralune then sends images as sRGB so they are not converted twice; Settings → Colour says so.
- **False colour** is a display mapping of brightness and is not converted.
- Switch **Colour management** off in Settings → Colour to show the values as they are, as before.

## Opening images

When you open an image in a workspace, Settings → Colour decides what happens to its profile:

| Situation | Choices | Default |
|---|---|---|
| **Image with another profile** (than the working profile) | **Keep its profile**, **Convert to the working profile**, **Discard the profile**, **Ask** | Keep its profile |
| **Image without a profile** | **Leave it untagged** (it is taken to be in the working profile), **Assign the working profile**, **Ask** | Leave it untagged |

With **Ask**, a question appears when such an image is opened; tick **Do this from now on** to make the answer the setting. Frames read in bulk (light frames for stacking, image sequences, captured frames) are never asked about: they keep what their files carry.

A stack keeps the profile of its light frames when they all share one; otherwise it is untagged.

## Assigning and converting

In **Process → Colour**:

- **Assign profile…** changes how the pixel values are understood, not the values, so the colours change. Use it when an image carries the wrong profile or none.
- **Convert to profile…** changes the pixel values so the colours stay the same in the new profile, with the rendering intent you choose. Use it to move an image into another space, for example to sRGB before publishing it.

Both are steps in the history, so they can be undone. Converting between the built-in profiles is exact and keeps values outside 0–1; profiles with lookup tables (for example printer profiles) are converted by Windows.

Saturation, LRGB combination and the colour-preserving arcsinh stretch weigh the channels as the image's profile does, so they keep brightness right in any working profile.

## Saving

- With **Embed the image's profile in saved files** on (the default), FITS, XISF, TIFF, PNG and JPEG files carry the image's profile. An untagged image stays untagged in FITS, XISF and TIFF.
- **8-bit PNG and JPEG** carry the profile their colours are in, since viewers otherwise take them to be sRGB. With **Keep the image's profile** (the default) they keep it, ProPhoto RGB included; with **Convert to sRGB** they are converted to sRGB first, which is what to choose for the web.
- 8-bit files in a wide profile such as ProPhoto RGB look dull in programs without colour management, and smooth gradients can show bands. Save in 16 or 32 bits for further work, and convert to sRGB for sharing.

FITS files store the profile in an image extension named `ICCProfile`, where other colour-managed astronomy programs look for it, so the profile travels with the file.

## Soft-proofing and the gamut warning

- **Proof colours** (Ctrl+Shift+O, or the button in Process) shows the image as it will look in the **proofing profile** chosen in Settings → Colour: another colour space such as sRGB, or a printer and paper profile. Colours the proofing profile cannot reproduce are shown as they will come out.
- **Gamut warning** (Ctrl+Shift+Y, or the button in Process) marks the pixels the monitor cannot show, or, while proofing, those the proofing profile cannot reproduce, in the **gamut warning colour** (grey unless you choose another).

The keys work in every image view; the view says when proofing or the gamut warning is on. The **proofing intent** sets how colours are brought into the proofing profile (Relative colorimetric unless you choose another).

## Night vision mode

Night vision mode shows images as brightness in deep red, so colour management, proofing and the gamut warning do not apply while it is on.

## Good practice

- Calibrate and profile your monitor; it matters more than any setting.
- Keep the default working profile (ProPhoto RGB) for processing, or choose the space you publish in if you prefer to see exactly that while working.
- Calibrate colour (Photometric colour calibration) before judging colours: the profile says what the numbers mean, calibration makes the numbers right.
- Convert to sRGB (or set 8-bit exports to sRGB) for images you share online.
- To print, proof with your printer's profile for the paper you use and look at the gamut warning before printing.
