The **Observatory** workspace connects and controls your equipment: mount, camera, guide camera, AO unit, focuser, filter wheel, rotator, dome or roof, weather station, safety monitor, switch box, flat panel and guider. It takes images, focuses automatically, centres targets by plate solving, guides (with its own guider or through PHD2), polar-aligns your mount and runs automated sequences.

The sequencer has its own page: [Sequencer](Sequencer).

> Automation moves real equipment. Stay with your mount the first time you use slewing, parking, meridian flips or sequences, and set slew limits in the mount or driver so the telescope cannot hit the pier or tripod.

## Contents

- [Supported equipment](#supported-equipment)
- [Equipment tab](#equipment-tab)
  - [Equipment profiles](#equipment-profiles)
  - [Connecting a device](#connecting-a-device)
  - [ASCOM](#ascom)
  - [ASCOM Alpaca](#ascom-alpaca)
  - [INDI](#indi)
  - [LX200 and OnStep](#lx200-and-onstep)
  - [The simulated observatory](#the-simulated-observatory)
  - [Optics](#optics)
- [Imaging tab](#imaging-tab)
  - [Camera](#camera)
    - [Deep sky](#deep-sky)
    - [Planetary video](#planetary-video)
  - [File names](#file-names)
  - [FITS headers](#fits-headers)
  - [Cooling](#cooling)
  - [Focuser and autofocus](#focuser-and-autofocus)
  - [Mount](#mount)
  - [Centring by plate solving](#centring-by-plate-solving)
- [Guiding tab](#guiding-tab)
  - [The Sidralune guider](#the-sidralune-guider)
  - [Calibration](#calibration)
  - [Guider settings](#guider-settings)
  - [Guiding on several stars](#guiding-on-several-stars)
  - [Dark frames](#dark-frames)
  - [Guiding Assistant](#guiding-assistant)
  - [Calibration Assistant](#calibration-assistant)
  - [Star-Cross test](#star-cross-test)
  - [Lock position, comet tracking and manual guide](#lock-position-comet-tracking-and-manual-guide)
  - [Lost stars, slews and the guide log](#lost-stars-slews-and-the-guide-log)
  - [Guiding while imaging](#guiding-while-imaging)
  - [Solving guide frames](#solving-guide-frames)
  - [Guide log viewer](#guide-log-viewer)
  - [Predictive PEC](#predictive-pec)
  - [Adaptive optics](#adaptive-optics)
  - [A guide camera on a rotator](#a-guide-camera-on-a-rotator)
  - [Subframes and noise reduction](#subframes-and-noise-reduction)
  - [The PHD2-compatible server](#the-phd2-compatible-server)
  - [PHD2](#phd2)
- [Polar alignment tab](#polar-alignment-tab)
  - [Three-point polar alignment](#three-point-polar-alignment)
  - [Drift alignment](#drift-alignment)
  - [Which camera](#which-camera)
  - [Static polar alignment](#static-polar-alignment)
  - [Polar drift alignment](#polar-drift-alignment)
- [Dome & weather tab](#dome--weather-tab)
- [Meridian flips](#meridian-flips)
- [Commands](#commands)

## Supported equipment

| Device | ASCOM | Alpaca | INDI | Other | Simulator |
|---|---|---|---|---|---|
| Mount (telescope) | Yes | Yes | Yes | LX200 / OnStep (serial or TCP) | Yes |
| Camera | Yes | Yes | Yes | | Yes, renders real catalogue stars |
| Guide camera | Yes | Yes | Yes | | Yes, a small camera on a guide scope |
| Focuser | Yes | Yes | Yes | | Yes |
| Filter wheel | Yes | Yes | Yes | | Yes |
| Rotator | Yes | | | | Yes |
| AO unit (adaptive optics) | | | Yes | SX AO (serial port) | Yes |
| Dome or roll-off roof | Yes | Yes | | | Yes |
| Weather station (ObservingConditions) | Yes | | | | Yes |
| Safety monitor | Yes | Yes | | | Yes (from the simulated weather) |
| Switch | Yes | | | | Yes, a power and dew-heater box |
| Flat panel (ASCOM: cover calibrator) | Yes | | | | Yes |
| Guider | | | | The Sidralune guider (built in), or PHD2 (connected on the Guiding tab) | The Sidralune guider guides the simulators |

## Equipment tab

At the top is the **Equipment profile** in use. Below it, the tab has one row per device type. Each row has a list of available devices, **Connect** (which becomes **Disconnect**), **Setup…**, **ASCOM…** and a status message.

Above the rows:

| Button | Action |
|---|---|
| **Connect all** | Connects the device the profile remembers in each row, one after another. A device that cannot connect does not stop the others; a message names those that failed. Available while the profile remembers a device that is not connected |
| **Connect simulated observatory** | Connects a simulated mount, camera, guide camera, focuser, filter wheel, rotator, dome, weather station, switch box and flat panel in one go, guided by the Sidralune guider (PHD2 is disconnected first) |
| **Discover Alpaca devices** | Searches the local network for Alpaca servers and adds their devices to the lists |
| **Add INDI server…** | Connects to an INDI server and adds its devices to the lists |
| **Disconnect all** | Disconnects every device. Available when at least one device is connected |

### Equipment profiles

A profile is one rig, so Sidralune needs no second program set up beside it. It holds:

- the device connected in each row: connecting a device in its row makes it the profile's for that row, and **Connect all** connects them all again next time;
- the imaging telescope and camera, chosen from the lists in [Settings → Equipment](Settings#equipment), so a telescope's focal length is entered in one place;
- a **Reducer or Barlow (×)** in front of the imaging camera;
- how the guide camera sees the sky (**Guides through**): **An off-axis guider** uses the imaging telescope's focal length, **A guide scope** its own (**Guide scope focal length (mm)**);
- the Sidralune guider's own settings and calibration, which belong to the rig.

The site is not part of a profile: the same rig goes to another site, and the site is chosen in [Settings → Location](Settings#location). The guider takes the focal length from the profile, the pixel size and binning from the camera, the guide rate and pier side from the mount, and the site from Settings.

**New…** makes a profile from the one in use, with a copy of its guider settings in a file of its own; **Rename…** renames it and **Delete** removes it (the last one stays). Another profile is chosen while nothing is connected, since what is connected belongs to the rig in use; **New…** and **Delete** wait likewise. The rig's own details can change at any time and are used at once. Choosing another profile also makes its telescope and camera the planner's.

The first time, Sidralune makes a profile called **My equipment** from what it knew: the planner's telescope and camera in use, and a guide scope of the focal length the guider had.

### Connecting a device

1. Choose the device in its row's list. The list contains the ASCOM drivers installed for that device type, discovered Alpaca and INDI devices, and the simulator.
2. Click **Setup…** if the driver needs configuring (COM port, site, gear ratios…). For ASCOM devices this opens the driver's own setup dialog.
3. Click **Connect**. The status changes to "● Connected" and the status-bar pill turns green.

Devices are disconnected cleanly when you close Sidralune.

### ASCOM

Install the [ASCOM Platform](https://ascom-standards.org) and the drivers for your equipment. Sidralune talks to ASCOM drivers directly through COM, so you do not need the ASCOM developer components.

**ASCOM…** opens the ASCOM Chooser for that device type, so you can pick and configure any installed driver, including ones that are not listed yet. If the ASCOM Platform is not installed, Sidralune says so.

### ASCOM Alpaca

Alpaca is ASCOM over the network, used by many modern devices and by ASCOM Remote. Sidralune supports Alpaca mounts, cameras, focusers, filter wheels, domes and safety monitors. **Discover Alpaca devices** broadcasts a discovery request on UDP port 32227 and lists every device that answers. The toast reports how many devices and servers were found. If nothing appears, check that the Alpaca server is running and that your firewall allows UDP broadcasts on the local network.

### INDI

INDI is the standard on Linux, Raspberry Pi astronomy systems and many observatory controllers. **Add INDI server…** asks for `host:port` (the default port is 7624), connects, reads the device properties and adds the mounts, cameras and focusers it finds to the lists. Images from INDI cameras are received as FITS BLOBs.

### LX200 and OnStep

Mounts that speak the Meade LX200 command set (including OnStep, OnStepX and many Chinese mounts) can be connected directly without a driver. Choose the LX200 entry in the telescope row and click **Setup…** to enter the address:

- a serial port with optional baud rate, for example `COM3:9600`,
- or a TCP address for Wi-Fi and Ethernet controllers, for example `192.168.0.1:9999`.

### The simulated observatory

The simulators behave like real devices, so you can learn Sidralune, test sequences and demonstrate features indoors:

- The **mount** slews at a realistic speed, tracks, parks and accepts syncs. Like a real German equatorial mount it keeps its pier side while it tracks through the meridian, until a slew flips it. It tracks with a little periodic error (±6″ over 8 minutes), drifts slowly as if its polar alignment were slightly off, and has some Dec backlash, so guiding has something to correct. Guide pulses take their time, at 0.5× sidereal.
- The **guide camera** is a small uncooled camera (1280 × 960 pixels of 3.75 µm) on a 240 mm guide scope, turned 30° on the sky, on the same mount. The seeing moves its stars a little from frame to frame, and it has some hot pixels for dark frames to remove. It has a guide (ST-4) port.
- The **camera** renders a star field around the mount's pointing from the loaded catalogues (plus synthetic faint stars), with a realistic point-spread function that widens when the focuser is out of focus, sky background, read noise and shot noise. Plate solving, autofocus, centring and polar alignment therefore all work for real.
- The **focuser** has a hidden best-focus position; stars grow along a hyperbola away from it, exactly what autofocus expects. It also reports a slowly varying temperature.
- The **filter wheel** has per-filter focus offsets, and the **dome** and **weather station** (with a safety monitor) respond to commands and report plausible values.
- The **switch box** has two dew heaters with a level from 0 to 100 %, a fan and the mount's power (on at the start), and the **flat panel** has a dust cover that takes 3 seconds to open or close and a light from 0 to 255. Both are on the [Dome & weather tab](#dome--weather-tab), and in scripts as Devices.Switch and Devices.Cover.
- The **rotator** turns the imaging camera at 10° a second. The **AO simulator** (in the AO row; not part of the simulated observatory) tilts the guide camera's image 0.4″ a step, 45 steps each way.
- The mount can be given a **polar misalignment** (from a script or a test): it then turns about its own tilted axis, so the stars drift and the camera's angle on the sky changes exactly as with a real misaligned mount, and every polar alignment method can be tried against a known error.

### Optics

The **Optics** card, under the rows, holds the profile's rig: **Telescope**, **Reducer or Barlow (×)**, **Camera**, **Guides through** and **Guide scope focal length (mm)**. It shows the resulting focal length, aperture and focal ratio. The imaging focal length and aperture are written into FITS headers and used as plate-solving hints.

## Imaging tab

The image viewer fills the centre. The readout under it shows the pixel position and value under the cursor. The panel on the left holds the **Camera** and **Cooling** sections, and the panel on the right the **Mount** and **Focuser** sections; click a section heading to open or close it.

### Camera

The **Camera** section has two tabs at the top, **Deep sky** and **Planetary**, and each keeps its own settings. Planetary and deep-sky settings are nearly opposite (millisecond exposures, high gain and a small region against exposures of seconds or minutes), so switching between them is one click instead of re-entering gain, offset and binning. **Cooling** below applies to both.

#### Deep sky

| Field | Meaning |
|---|---|
| **Exposure (s)** | Exposure time |
| **Count** | Number of frames to take |
| **Frame type** | LIGHT, DARK, FLAT, BIAS or SNAPSHOT. Darks and bias frames are taken with the shutter closed if the camera has one |
| **Binning** | 1×1 to 4×4 |
| **Gain** / **Offset** | Camera gain and offset (−1 leaves the driver's value unchanged) |
| **Filter** | Filter to use; the list comes from the filter wheel |
| **Target** | Object name, written into the file name and the FITS `OBJECT` keyword |
| **Save** | Whether to save frames |
| **File pattern** | Folder and file naming pattern, see below |

| Button | Action |
|---|---|
| **Start** | Take the requested number of frames |
| **Loop** | Continuous preview; frames are not saved. Useful for framing and manual focusing |
| **Stop** | Abort the current exposure or series, or end any other camera work. Available only while something uses the camera |

After each frame the status line shows the number of stars, the half-flux radius (HFR) and FWHM in pixels, the median level and the saved file name. A frame taken while guiding gets a line with its guiding during the exposure: the total RMS, RA and Dec RMS and the peak error in arcseconds, with **interrupted** when guiding was not on all the time and **flagged** when a sequence's limit flagged it. These numbers are the quickest way to judge focus and sky conditions.

The viewer shows every frame the camera takes, not only the ones started here: frames from a running [sequence](Sequencer) and from autofocus appear too, and the status line starts with "Sequence ·" for sequence frames.

The camera can take only one exposure at a time, so only one job may use it: a capture or preview loop, a video recording, **Run autofocus**, **Center** (plate-solve centring), **Start polar alignment**, or a sequence. While one of them runs, the buttons that start camera work are greyed out, **Start** and **Loop** included, and a note under the camera controls (on both tabs) says what is using the camera. **Stop** on either tab ends it, including a video recording, autofocus, centring, polar alignment or a sequence. The one exception is **Record**, which takes over from **Live view** (but **Live view** does not interrupt a recording).

#### Planetary video

The **Planetary** tab records a SER video for [planetary, lunar and solar imaging](Planetary,-lunar-and-solar-imaging): the camera takes short exposures back to back and every frame goes straight into the file, with no star analysis or separate files, so it runs as fast as the camera driver allows.

| Field | Meaning |
|---|---|
| **Exposure (ms)** | Exposure of each frame, typically 2–15 ms for planets |
| **Region** | Region of interest, centred on the sensor: **Full sensor** or a fixed size from 1920 × 1080 down to 320 × 240. A smaller region transfers faster, so it gives a higher frame rate |
| **Binning**, **Gain** / **Offset**, **Filter**, **Target** | As on the Deep sky tab, but kept separately for planetary work |
| **Max frames** / **Max seconds** | The recording stops at whichever comes first |

| Button | Action |
|---|---|
| **Record** | Start recording. The viewer shows a frame twice a second, and the status line shows the frame count, the frame rate and the time. From live view, the recording takes over straight away |
| **Live view** | Show frames with the settings above, without saving them, for framing, focusing and setting the exposure. The status line shows the frame rate, the brightest pixel as a percentage of full scale, how much of the planet is clipped (with a ⚠ when any is), and a **Focus** number with the best value so far: it rises as focus improves, so focus for the highest number |
| **Stop** | End the recording early (the frames recorded so far are kept and the file is complete), end live view, or end any other camera work, like the Deep sky tab's **Stop** |
| **Open in Planetary** | Open the last recording in **Planetary → Pre-process** |

With the Planetary tab selected, the viewer shows frames as recorded, without the automatic stretch of the Deep sky tab, which would turn the black sky around a planet into noise. Live view counts as camera work: while it runs, the Deep sky tab's capture, autofocus, centring, polar alignment and sequences wait, while the mount controls stay usable. Only **Record** may take over from it. Live view is not started on its own, because a DSLR would be exposing all the time.

Videos recorded by a [sequence](Sequencer#instructions) (the **Record video** instruction) are shown here too, with "Sequence ·" in front of the status line, and **Open in Planetary** opens them as well. A sequence with **Record video** in a loop is the way to record a rotation series unattended.

Videos are saved as 16-bit SER files (8-bit for 8-bit cameras), with a UTC time stamp at mid-exposure for every frame, under the image folder in `Planetary\<target>\`. The file is named after the recording's mid-time in the [WinJUPOS style](Planetary,-lunar-and-solar-imaging#winjupos-file-names), for example `2026-10-23-2020_2-Jupiter.ser`, so the [Derotation](Planetary,-lunar-and-solar-imaging#derotation) tab and WinJUPOS read the time from the name.

Recording works with any ASCOM, Alpaca or INDI camera. These interfaces transfer each frame whole, so the frame rate is lower than in dedicated planetary capture programs: expect a few to a few tens of frames per second, depending on the driver, the region and the exposure. For the highest frame rates, record with the camera maker's software or a program such as SharpCap or FireCapture and open the SER file in the Planetary workspace.

### File names

Frames are saved under the **Image folder** from **Settings → Observer & accounts**. The default pattern is:

```
$NIGHT\$TARGET\$TYPE\$TARGET_$FILTER_$EXPOSUREs_$DATETIME_$FRAMENR
```

which produces files like `2026-10-18\M 31\LIGHT\M 31_L_180s_20261018-221503_0007.fits`. Available tokens:

| Token | Replaced by |
|---|---|
| `$NIGHT` | Date of the evening the night began; it changes at noon, so a whole night goes in one folder |
| `$DATE`, `$TIME`, `$DATETIME` | Date, time, and both, of the exposure |
| `$TARGET` | Target name |
| `$TYPE` | Frame type |
| `$FILTER` | Filter name |
| `$EXPOSURE` | Exposure time in seconds |
| `$BIN` | Binning, for example `2x2` |
| `$GAIN` | Gain |
| `$TEMP` | Sensor temperature |
| `$HFR` | Measured half-flux radius |
| `$FRAMENR` | Running frame number |

Use `\` to create folders.

### FITS headers

Saved frames are 16-bit FITS files with a full header, so stacking and plate-solving programs can use them without extra input:

- `DATE-OBS`, `EXPTIME`, `IMAGETYP`, `OBJECT`, `FILTER`,
- from the camera driver: `XBINNING`/`YBINNING`, `XPIXSZ`/`YPIXSZ` (pixel size including binning), `CCD-TEMP`, `GAIN` and `OFFSET` where available,
- `SITELAT`, `SITELONG`, `SITEELEV`,
- `FOCALLEN` and `APTDIA`,
- the mount's pointing converted to J2000: `RA`, `DEC`, `OBJCTRA`, `OBJCTDEC`, plus `CENTALT`, `CENTAZ` and `AIRMASS`,
- for a frame taken while guiding, how guiding went during its exposure (arcseconds): `GUIDRMS` (total RMS), `GUIDRMSR` and `GUIDRMSD` (RA and Dec), `GUIDPEAK` (the largest error) and `GUIDSTPS` (the guide steps), plus `GUIDLOST = T` when guiding was interrupted (a lost star, a pause) and `GUIDBAD = T` when a sequence's limit flagged it (see [Guiding while imaging](#guiding-while-imaging)),
- `SWCREATE = Sidralune`.

### Cooling

Set the **Set point (°C)** and tick **Cooler**. The status shows the sensor temperature and cooler power. Cool down gradually and keep the set point within reach of the cooler (typically 30 to 35 °C below ambient) so that the power stays below about 80%.

### Focuser and autofocus

The **Focuser** section shows the position, whether it is moving and its temperature probe reading. **In** and **Out** move by **Step**.

**Run autofocus** takes a series of exposures (**AF exposure** long, binned 2×2) at focuser positions **AF step** apart on both sides of the current position, measuring the stars' half-flux radius in each. The points are plotted as a V-curve, a hyperbola is fitted to them, and the focuser moves to the minimum, approaching from one direction with backlash overshoot. If the minimum lies at the edge of the sweep, the routine recentres the sweep and continues, so it also works when you start far from focus.

Tips: use an exposure long enough to show 20 or more stars, choose a step that changes the HFR by roughly 10–20% per step near focus, and refocus when the temperature changes by a degree or two (the [sequencer](Sequencer#triggers) can do this automatically).

### Mount

The **Mount** section shows the mount's RA, Dec, altitude, azimuth, pier side and state. The arrow buttons move the mount at the selected **Rate**: **Guide 0.5×**, **Centre 8×**, **Find 64×** or **Slew max** (multiples of the sidereal rate). The stop button in the middle of the arrows stops any slew; it is available whenever a telescope is connected, so a slew started from another program can be stopped too.

Type an object name or coordinates in **Go to**, then:

- **Slew** slews there (converting J2000 to the apparent position of date),
- **Center** slews, then plate-solves and corrects until the object is centred (see below).

Stopping a slew (its task, centring, polar alignment, the Calibration Assistant or a sequence) stops the mount too, wherever it is.

**Park**, **Unpark** and **Tracking** do what they say.

### Centring by plate solving

**Center** (and the sequencer's Center instruction) runs a closed loop: slew, take a short binned exposure, plate solve it, sync the mount to the solution, and slew again, until the target is within 1′ of the image centre or five attempts have been made. This removes pointing-model errors, and after a sync the mount's next slews are more accurate too. It needs a camera and the plate solver (see [Astrometry](Astrometry-and-framing#plate-solving)).

Centring, three-point and static polar alignment solve against Gaia DR3 from VizieR, near the position the mount reports. Each field downloaded is cached, so solving the same field again works offline. Without a connection they use the loaded star catalogue instead, which only reaches faint enough stars for wide fields. With the simulated cameras they solve against the stars the simulator draws, including its extra faint ones, so they work offline in the simulator.

## Guiding tab

Sidralune guides with its own guider, the **Sidralune guider**, or through [PHD2](https://openphdguiding.org). The Sidralune guider is part of Sidralune, so there is nothing to connect: it guides as soon as a guide camera is connected in the Equipment tab. To guide with PHD2 instead, click **Connect PHD2**.

| Button | Action |
|---|---|
| **Connect PHD2** | Connects to PHD2's event server (port 4400 for the first instance), which then guides instead of the Sidralune guider. It becomes **Disconnect PHD2**, which goes back to the Sidralune guider. Greyed out while a guide camera is connected in Sidralune |
| **Loop** | Takes guide frames and follows the star, without guiding (Sidralune guider only) |
| **Select star** | Chooses the best guide star in the frame (Sidralune guider only). You can also click a star in the frame |
| **Start guiding** | Starts guiding, calibrating first when there is no calibration for this equipment, and waits until the guiding has settled |
| **Calibrate & guide** | Recalibrates, then guides |
| **Dither** | Moves the guide star by a few pixels and waits for it to settle, so that hot pixels and noise patterns fall on different sky in each frame |
| **Stop** | Stops guiding, and a running Start guiding or Dither. Available while the guider is taking frames, calibrating or guiding |
| **Clear graph** | Clears the error graph |

Start guiding, Calibrate & guide and Dither run one at a time; the others are greyed out while one runs.

Under the frame are three pages:

- **Graph**: the **Guiding error** graph, the star's offset in RA and Dec in arcseconds at each guide step;
- **Target**: the last 100 offsets as points round the lock position (RA across, Dec up, the newest highlighted) on rings of whole arcseconds, with the RMS as a dashed circle. It is scaled to nine in ten of the points, so one large jump does not shrink the rest; points further out sit on the edge;
- **Star profile**: the guide star's cross-sections through X and Y, with its peak, HFD and FWHM.

The status line shows the guider state and the RMS error in RA, Dec and total over the last 100 steps, then how many stars it guides on, the exposure auto exposure has chosen, **Subframe** while only a part of the frame is read, the AO's position, and **Comet** while it follows a comet. Total RMS well below your image scale (arcseconds per pixel) means round stars.

Beside the frame, the Sidralune guider has four pages: **Settings**, **Assistants** (the [Guiding Assistant](#guiding-assistant) and the [Calibration Assistant](#calibration-assistant)), **Darks** ([dark frames](#dark-frames)) and **Tools** ([lock position, comet tracking and manual guide](#lock-position-comet-tracking-and-manual-guide)). All are greyed out while PHD2 guides instead.

### The Sidralune guider

The Sidralune guider needs a **guide camera** (the Guide camera row of the Equipment tab) and a way to send guide pulses: through the **mount**'s driver (ASCOM, Alpaca, INDI or LX200 pulse guiding), or through the **guide camera's ST-4 port** with a cable to the mount. The guide camera must be a different camera from the imaging camera.

The tab shows the latest guide frame. A square marks the area searched for the guide star and a cross the lock position, where guiding holds it: yellow while looping and calibrating, green while guiding, red when the star is lost or a frame is left out because the star's brightness jumped. Thin squares mark the other stars guided on. Under the frame are the star's position, signal-to-noise ratio (SNR) and half-flux diameter (HFD), and a warning when it is saturated.

**Select star** (and Start guiding when no star is chosen) picks a single, unsaturated star with a good SNR and a normal size, with no bright neighbour in its search area and enough room from the edges for calibration and dithers. Clicking the frame chooses the star nearest the click instead. A right click offers **Select star here** and **Set lock position here**.

Each axis has its own algorithm, chosen on the Settings page; only that algorithm's settings are shown:

- **Hysteresis** (RA's default) blends each new error with the previous ones (the hysteresis share), corrects a share of the result (the aggressiveness), and leaves errors smaller than the minimum move alone, since those are mostly seeing.
- **Resist Switch** (Dec's default) keeps correcting Dec in one direction, and changes direction only after three errors in a row the other way, each over the minimum move and not shrinking. Backlash makes every change of direction in Dec costly. With **Correct at once** on, an error more than three minimum moves the other way (a gust of wind, a cable) is corrected straight away.
- **Lowpass** corrects the median of the last ten errors, so a frame thrown by the seeing counts for little, plus the slope of a line through them times the **Lowpass slope weight** (5), which meets a steady drift ahead. It never corrects more than the error just measured, nor the other way, and leaves errors under the minimum move alone. It suits errors that wander slowly under average seeing.
- **Lowpass2** fits a straight line through the last ten errors and corrects a share of where the line is now, so the seeing averages out. It never corrects more than the error just measured. It suits mounts that drift smoothly, in good seeing.
- **Z-filter** guides as if each exposure were several times longer (the exposure factor), by low-pass filtering the star's motion. It suits slow errors in poor seeing; with short exposures, try a factor of 2–4.
- **Predictive PEC** (RA only) learns the mount's periodic error while it guides and corrects it before it shows. See [Predictive PEC](#predictive-pec).

**Guide Dec** can be limited to **North only** or **South only** (for a mount with a lot of backlash and a steady Dec drift: guide against the drift only) or **Off**. **Backlash compensation** lengthens the first Dec pulse after Dec changes direction by the backlash, so the gears are taken up and the correction still works. The amount adapts as it guides: a turn that overshoots shortens it by 10 %, one that hardly moved the star lengthens it by 10 %, always between the least and most you set (with no most set, up to twice the amount). The [Guiding Assistant](#guiding-assistant) measures the backlash and fills these in.

RA and Dec pulses are sent one after the other, each no longer than its maximum pulse (Dec's plus any backlash compensation), and nothing is sent while the mount slews. The RA rate is adjusted for the declination, so one calibration serves the whole sky.

### Calibration

Calibration measures how guide pulses move the star on the guide camera: in which direction a West pulse and a North pulse move it, and how many pixels per second. The guider pulses West in steps until the star has moved the calibration distance, back East, North (after first taking up any Dec backlash) and back South. Each direction and rate comes from a straight line fitted through every step's position, so one poorly measured step counts for little.

The calibration step is worked out from the mount's guide rate (0.5× sidereal if the mount does not report it), the guide camera's pixel scale and the declination, for about twelve steps. Calibrate between about 20° north and south of the celestial equator, away from the horizon. Near the pole RA moves the star little and the RA measurement is poor.

**A predicted calibration:** with **Predict from the plate solve** on (the default) and a guide frame [solved](#solving-guide-frames) at this pointing, which starting guiding does first, the guider does not measure the calibration from scratch. The solve gives the directions in which West and North pulses move the star, and the mount's guide rates, the declination and the solved scale give the speeds. Three larger steps West and back, then (after taking up the Dec backlash) three North and back, only confirm it: each direction must be within 20° of the prediction and each speed within 30 % of it. North may come out reversed, as it does on one side of the pier for most mounts, which just turns it round. The calibration is then the prediction, with its exact right angle and the solve's directions, in about half the time. When the confirmation disagrees, the guider calibrates in full and the panel says why, for instance that RA moved the star at 200 % of the predicted speed (a mount that reports the wrong guide rate). A mount that does not report its guide rates has its speeds measured by the same three steps. Without a solve the calibration is the full one.

Afterwards the panel shows the calibration and warns when something looks wrong:

- RA and Dec are more than 15° from square: backlash or poor seeing may have upset the calibration;
- the RA and Dec rates do not fit the declination (RA should move the star cos(Dec) as fast as Dec at equal guide rates);
- a leg took fewer than four steps (not for a predicted calibration, which only confirms with three);
- the star moved more than 40 % faster or slower than the guide rate and focal length predict: check the guide scope's focal length and the mount's guide rate;
- it was made near the pole.

The calibration is saved and used again as long as the guide camera, binning, guide scope focal length and guide output stay the same. **Clear calibration** forgets it. After a meridian flip the camera has turned 180° on the sky, and the guider turns its calibration round to match. Most mounts keep their Dec pulse direction after a flip; if yours turns its Dec pulses round itself, Dec runs away after a flip. Then tick **Mount reverses Dec** and calibrate again. A change of declination needs no new calibration either: the RA rate is scaled by cos(Dec) from where it was calibrated.

### Guider settings

The Settings page holds the Sidralune guider's settings, saved as you change them:

| Setting | Default | |
|---|---|---|
| Exposure (s) | 2 | Long enough to average out the seeing, short enough to correct often |
| Auto exposure | Off | The exposure follows the star's SNR between the shortest and longest exposure (1 and 5 s), aiming at the target SNR (10) |
| Binning | 1 | |
| Guide through | The mount | Or the guide camera's ST-4 port |
| Focal length | From the profile | The guide scope's, or the imaging telescope's through an off-axis guider, set in the [equipment profile](#equipment-profiles); for the pixel scale and the calibration step |
| Plate solving: Solve guide frames | On | Solve a guide frame when guiding starts and to find a lost star; see [Solving guide frames](#solving-guide-frames) |
| Last solve | | The scale (″/px), angle and focal length the last solved guide frame gave, why it could not be solved, or **Not solved yet** |
| Search box radius (px) | 15 | How far the star may move between frames |
| Subframes | Off | See [Subframes and noise reduction](#subframes-and-noise-reduction) |
| Noise reduction | None | Or 2×2 mean or 3×3 median |
| Diagnosis: Save frames on trouble | Off | When the guide star is lost, the five guide frames before it and the three after are saved as FITS (with the lock position, the star's position and the guider's state in the header) in `Logs\Guide frames\<date time> Star lost`; when a calibration fails, the five before it, in `… Calibration failed`. At most once every five minutes, so a cloudy night does not fill the disk |
| Multi-star: Guide on several stars | On, up to 9 stars | See [Guiding on several stars](#guiding-on-several-stars) |
| Star brightness: Skip frames where it jumps | On, 50 % | A frame in which the guide star's brightness changes by more than the tolerance is not used for a correction |
| Dither pattern | Random | **Random** dithers anywhere within the amount. **Spiral** steps out by the amount along a square spiral round where it began (24 places, then it begins again), so no two frames share a place; a lock position moved by hand begins it again there |
| Prediction: Predict from the plate solve | On | Predict the calibration from a solved guide frame and confirm it with three steps each way; see [Calibration](#calibration) |
| Calibration step (ms) | 0 (automatic) | |
| Calibration distance (px) | 20 | How far each calibration leg moves the star |
| After a meridian flip: Mount reverses Dec | Off | |
| Rotator: The guide camera turns with it; Rotator direction: Reversed | Off, off | See [A guide camera on a rotator](#a-guide-camera-on-a-rotator) |
| RA algorithm | Hysteresis | Or Lowpass, Lowpass2, Z-filter or Predictive PEC (predictive gain 50 %, reactive gain 60 %, worm period 480 s adjusted from the guiding, model kept through gaps of 40 % of a period) |
| RA aggressiveness, hysteresis, minimum move | 70 %, 10 %, 0.2 px | Lowpass: slope weight 5; Lowpass2: aggressiveness 80 %; Z-filter: exposure factor 4 |
| RA maximum pulse | 2500 ms | |
| Dec algorithm | Resist Switch | Or Lowpass, Lowpass2 or Z-filter |
| Dec aggressiveness, minimum move | 100 %, 0.2 px | |
| Dec maximum pulse | 2500 ms | |
| Large Dec errors: Correct at once | On | Resist Switch only |
| Guide Dec | Both ways | Or North only, South only, Off |
| Backlash compensation | Off | With the backlash and the least and most it may adapt to |
| Adaptive optics | Travel 45 steps, calibration step 5 and leg 25 steps, bump at 80 %, bump step 1″, AO aggressiveness 80 %, minimum move 0.1 px, no bump after a dither | Shown when an AO is connected. See [Adaptive optics](#adaptive-optics) |
| PHD2-compatible server | Off, port 4400, this computer only | See [The PHD2-compatible server](#the-phd2-compatible-server) |

If guiding chases the seeing (many small corrections back and forth), raise the minimum moves. If it lags behind the drift, raise the aggressiveness. The [Guiding Assistant](#guiding-assistant) suggests both minimum moves from the seeing it measures.

Auto exposure starts at the longest exposure. Once there is a star, it takes one step shorter (0.5, 1, 1.5, 2, 2.5, 3, 4, 5, 6, 8 or 10 s) after three frames over 1.5 × the target SNR, and one step longer after two frames under it. With a dark library, the dark of the nearest exposure is used.

The brightness check compares each frame's guide star with the median of its last ten frames. After three frames in a row outside the tolerance the new brightness counts, so thin cloud or a focus change does not stop guiding for good.

### Guiding on several stars

With **Multi-star** on, choosing the guide star also chooses up to eight more around it: unsaturated, well measured, alone in their search area and at least a search area apart. Each frame measures every star in its own search area. While guiding, their displacements from where they should be are averaged, each weighted by its SNR, so the measuring noise of one star counts for less.

- A star that disagrees with the others by more than 2 px (or three times their spread) is left out of that frame; three frames in a row and it is dropped.
- When the guide star jumps more than 2.5 px (a bump), that frame follows the guide star alone.
- When the guide star cannot be seen, guiding goes on with the others as long as two are found, and after ten frames the best of them becomes the guide star.
- When fewer than half the stars are left, more are looked for, at most once a minute.
- Calibration uses the guide star alone, since its moves are large.

A star you click is the guide star, and others are still chosen around it.

### Dark frames

The **Darks** page treats each guide frame before the stars are measured. Under **Use** choose:

- **The dark library**: a master dark for each guide exposure (the median of several dark frames, 5 by default) is subtracted. With auto exposure, the library holds every exposure from the shortest to the longest; otherwise the exposure set. A frame of another exposure uses the nearest dark.
- **The bad-pixel map**: the hot and cold pixels found in a master dark (10 dark frames of 10 s, more than 5 noise levels from the median by default) are replaced by the median of their neighbours.

Either removes hot pixels that could be taken for stars. **Build dark library** and **Build bad-pixel map** first ask you to cover the guide scope (or close the camera's shutter), then take the frames, with progress and **Stop**. The guider must be stopped, and while the frames are taken **Loop**, **Select star**, **Start guiding**, **Calibrate & guide** and **Dither** are greyed out. The settings are read when you click a Build button, so they stay editable while it runs. The page says what exists and whether it still fits the guide camera: a library or map fits only the camera, frame size, binning, gain and offset it was made with, and is not used otherwise. They are kept per camera in `%LOCALAPPDATA%\Sidralune\Settings\Guider darks`.

### Guiding Assistant

The Guiding Assistant (Assistants page) measures how the stars move without corrections. Start guiding, then click **Start measuring**: corrections pause while frames go on. Give it two minutes, longer for an accurate polar alignment error, then click **Stop and analyse**. It shows:

- the seeing in RA and Dec: the RMS of the frame-to-frame differences divided by √2, which slow drift and periodic error hardly touch;
- the drift in RA and Dec, from a straight line through the run;
- RA's swing from peak to peak once the drift is taken out, and how fast RA changes at most;
- the polar alignment error that the Dec drift needs at least.

With **Measure Dec backlash** ticked, a backlash test follows. Dec is stepped North (after taking up the backlash) until the star has moved the calibration distance, then South until it is back; the backlash is the pulse time South before the star moved. It is only reported when the star came back at 90 % or more of the speed it went.

The suggestions can be ticked and applied with **Apply the ticked settings**: minimum moves a little under the seeing (0.65 × its RMS), backlash compensation for a backlash between 100 ms and 3 s, Dec guided one way only for a larger one, a shorter exposure when RA changes fast, and advice to [polar-align](#polar-alignment-tab) when the pole is off by more than 5′. Guiding resumes either way. The last three runs are kept and shown again.

### Calibration Assistant

The Calibration Assistant (Assistants page) calibrates where calibration is most reliable: 5° from the meridian, on the side the mount already points to so it does not flip, on the celestial equator, or nearer the pole where the equator is lower than 30° (at high latitudes). The hour angle and declination can be changed; **Suggest again** brings back the proposal.

It first says what to check: a mount that does not report its guide rate, a guide rate under 0.5× sidereal, or a fixed calibration step. **Slew and calibrate** slews there (the last degree from the south, so Dec's gears are taken up northwards), calibrates and stops guiding, then rates the calibration **Good**, **Acceptable** or **Poor** and says why. **Return** slews back to where the mount was.

### Star-Cross test

The Star-Cross test (Assistants page, while guiding) drives the star out and back along each axis in a cross, the way a long exposure during such pulses draws a cross of star trails: West until the star has moved the calibration distance, East until it is as far the other way, West back to the middle, then the same North, South and North. Backlash only makes a leg take more pulses. Corrections wait meanwhile, and guiding goes on afterwards. The result shows:

- how far RA and Dec are **from square** (a mount whose axes are not at right angles, or a cone error);
- the **speed** each way in pixels per second of pulse, which should match the calibration and each other;
- the **backlash** when each axis turns round (East after West and back, South after North and back): the pulse time lost before the star moved back, from a line through the steps once it got going. RA seldom has any; Dec often does, which [backlash compensation](#guider-settings) can take up;
- how far the **first pulse from rest** moved the star compared with one later on: much less is stiction, or Dec backlash left from guiding;
- the track as a plot, and the guide frames laid over each other as an image of the cross, saved as `Star-Cross <date> <time>.fits` in the log folder.

### Lock position, comet tracking and manual guide

The **Tools** page:

- **Lock position**: where guiding holds the star. Type X and Y and click **Set**, or right-click the frame. While guiding, the stars are guided there like a dither. **North**, **South**, **East** and **West** move it by the step: on the sky through the calibration, so North moves the telescope north by that many arcseconds whatever the camera's angle; without a calibration, that many pixels in the frame. **Sticky lock position** keeps it through stops, lost stars and choosing the star again (the star nearest it is chosen), so the framing stays the same; a slew forgets it.
- **Comet tracking**: while guiding with **Follow the comet** on, the lock position moves with the comet, so the comet stays put in the imaging camera and the stars trail. The rates (arcseconds an hour, RA as motion on the sky) come from entered rates, from a comet's orbit in Sidralune's comet list (worked out again every 10 minutes, for your site), or from **training**: start training, keep the comet centred by moving the lock position, and stop training; the moves over the time become the rates.
- **Manual guide**: while looping, one pulse North, South, East or West of the set length, and how far the star moved and which way. With a calibration it says whether that agrees with the calibration: a quick check of the guide cable, the guide rate and Dec backlash. With an AO connected, **AO steps** moves the AO the same way, along its own axes, and checks it against the AO calibration.

### Lost stars, slews and the guide log

When the star is not found in its search area (a cloud, a tree), the state becomes **Star lost**. The guider keeps taking frames and searching the same area, and guides on when the star is back. With **Solve guide frames** on it also solves the frame to find the star when it has moved away, and brings it back (see [Solving guide frames](#solving-guide-frames)); otherwise it never moves the mount to look for it. When the mount slews, guiding stops and the star is forgotten. Sidralune's own slews start it again by themselves (see [Guiding while imaging](#guiding-while-imaging)); after a slew of your own, start guiding again. If the guide camera fails three times in a row, the guider connects it again with the same driver (up to three tries, 2, 5 and 10 seconds apart) and guides on where it was; only when that does not help does it stop and say why. A guide camera you disconnect yourself is not connected again.

### Guiding while imaging

Sidralune's own work takes care of guiding by itself, with the Sidralune guider or PHD2:

- **Dithering** in a sequence starts as soon as a frame has been read out from the sensor, while it downloads and is measured and saved; the next frame starts once guiding has settled.
- **Autofocus** (the instruction, its triggers and **Run autofocus**) pauses guiding while the focuser moves, since the guide star changes shape too, and lets it settle again before going on. Guiding paused by hand stays paused.
- **Centring** (**Center** on the Imaging tab, the sequence's centring) stops guiding for its slews and starts it again afterwards. Centring the field it guided on, on the same pier side, it guides on the star nearest the old lock position and holds it there, so the frames line up with those before.
- A **meridian flip** stops guiding, flips, re-centres and starts guiding again on a star chosen afresh, also when the star was lost at the time.
- A sequence that guides waits for guiding before each light frame: a lost star (a cloud) is waited for, and guiding that stopped (a slew, the guide camera failing) is started again, once a minute. After **Guiding wait minutes** (30) the frames go on without it, and the log says so. See [Sequencer](Sequencer#running-a-sequence).
- **Every guided frame** records how guiding went while its shutter was open: the RMS in RA, Dec and total and the peak error, in its [FITS header](#fits-headers) and the Imaging status line. A sequence's **Max guiding RMS** flags a light frame guided worse than that, or during which guiding was interrupted: `GUIDBAD = T` in its header and `_BADGUIDING` at the end of its file name, so stacking can leave it out. **When guiding is bad** keeps it (**Flag**) or keeps it and takes the frame again (**Retake**, up to three times in a row). A flagged frame is not used for the HFR autofocus trigger.

### Solving guide frames

With **Solve guide frames** on (the default), starting guiding first takes a guide frame and plate-solves it near the mount's position, with the same solver and catalogues as [static polar alignment](#static-polar-alignment). A frame is solved once at each pointing; after a slew, the next start solves again.

- **Scale and angle:** the solve gives the guide camera's scale in ″/px and its angle on the sky, before any calibration. **Last solve** shows them with the focal length they give: one far from the profile's says the profile's focal length is off. The guider uses the solved scale for its arcseconds while the equipment (camera, binning, focal length) stays the same.
- **Guide stars from the catalogue:** the guide star, and the other stars of multi-star guiding, are chosen first among the catalogue's single stars: a star the catalogue knows (not a hot pixel, a galaxy or a cosmic ray), with no other catalogue star a tenth as bright or brighter within its search box (an unresolved double pulls the measurement), and not known to the local catalogue as variable or double. The image still decides that a star is unsaturated, bright enough and away from the edges; with no catalogue star among those, the best one is used as before. A star you click is always the one used.
- **Finding a lost star again:** the guider knows the guide star's place on the sky. When the star has been lost for 10 seconds, it solves the frame and works out where the star is now, also far beyond its search box (a knock, a gust, the mount drifting under clouds), and guide pulses bring it back to the lock position; a star still at the lock position (behind a cloud) is waited for, and the solve is tried again every 30 seconds. A knock can also put another star in the search box, which would then be guided on instead: when the guide star's brightness jumps or another star takes over in multi-star guiding, the frame is solved too, and the right star is brought back. Afterwards the star at the lock position is the guide star again, with its partners chosen afresh. More than two minutes of guide pulses away is too far: the guider says so and leaves it to you.
- **A predicted calibration:** the solve's directions and the mount's guide rates predict the calibration, which three steps each way only confirm; see [Calibration](#calibration).

A guide frame that cannot be solved (clouds, no telescope connected, too few stars) only means guiding goes on without the catalogue; **Last solve** and the guide log say why.

Every guiding session writes a guide log, a CSV file named `Guide <date> <time>.csv` in `%LOCALAPPDATA%\Sidralune\Logs`: one row per frame with the star's position, its offset in RA and Dec, the pulses, SNR, flux, HFD and the number of stars, plus the calibration steps, dithers, lost and found stars, plate solves, settling, stars added or dropped, exposure changes, lock position moves, comet tracking, manual pulses and backlash tests. When guiding starts it notes the pixel scale, the exposure, the worm period (from the Predictive PEC settings) and the RA rate of the calibration in use, which the [guide log viewer](#guide-log-viewer) needs.

### Guide log viewer

**Guide log viewer** on the Guiding tab's **Tools** page opens a window with this session's guide log; **Open…** reads any other, Sidralune's own (`Guide <date> <time>.csv`) or PHD2's (`PHD2_GuideLog_<date>.txt`). **This session** shows the current one again, read afresh, and is greyed out while the Sidralune guider has no log yet. A log holding several guiding sessions has them in a list.

- **The statistics:** a table with the whole session and each frame, meaning each stretch between two dithers (with a dither between frames, one row per frame taken): its start, length, steps, RMS in RA, Dec and total, and the largest error, in arcseconds when the log gives the pixel scale and in pixels otherwise. Settling after dithers is left out. Clicking a row shows its stretch in the graph.
- **The graph:** the RA and Dec error over time, settling shaded and lost stars marked. It opens scaled to the guiding itself, so the jumps of dithers run off it; **Reset zoom** (right-click) shows everything.
- **The calibration** the session guided with, and its steps as a small plot. A log that reused an earlier calibration says so.
- **The spectrum of the RA error:** how much of it repeats at each period, which shows the mount's periodic error, its amplitude (± half the peak-to-peak swing) and its period. With **The mount's own error** on (the default), the corrections that were sent are added back at the calibration's RA rate and the jumps of dithers taken out, which gives the error the mount made, not what guiding left of it; that needs the RA rate, which Sidralune's logs give from now on and PHD2's give in their calibration. Frames at an even pace are analysed with an FFT; frames at an uneven pace (lost stars, exposure changes) with a Lomb–Scargle periodogram. The **Worm period (s)**, from the log or the Predictive PEC settings, and half of it are marked, and the text gives the largest peak and the periodic error near the worm period. The log needs at least one and a half worm periods of guiding for that.

### Predictive PEC

Most mounts' RA has a **periodic error**: the worm gear's turn (often 4–10 minutes) pushes RA back and forth by some arcseconds, again and again. Ordinary algorithms only correct it after the star has moved. **Predictive PEC** (choose it as the RA algorithm) learns the error's shape while guiding and corrects it in advance.

- It rebuilds the mount's own error from each frame (the star's offset plus every RA correction sent so far) and fits a Gaussian-process model to it: a smooth trend plus a periodic part with the worm's period.
- Each frame it corrects a share of the error measured (the **reactive gain**, 60 %, with the minimum move) plus a share of the change the model predicts over the next frame (the **predictive gain**, 50 %).
- For the first two worm periods it **trains**: the correction blends from Hysteresis to the model. The RA card says how far training has got, then **Predicting, period 478 s**.
- **Worm period (s)**: your mount's worm period (480 s by default). With **Adjust it from the guiding**, it is measured again every five minutes once two periods are recorded, and moved a quarter of the way to a clear peak.
- Dithers and lock position moves are taken out of the record, so the model survives them. Pauses and stops shorter than **Retain model** (40 % of a period) keep it; a slew or a longer gap starts it again.

It helps most with a mount whose periodic error is large and regular, in seeing good enough to see it. It needs a stable star: train it on a clear night.

### Adaptive optics

An **AO unit** (adaptive optics) is a small tilting window in front of the guide camera (and the imaging camera) that moves the image in fast small steps, so it can correct seeing and wind faster than the mount. Connect it in the **AO** row of the Equipment tab: the **SX AO** on its serial port, an INDI AO, or the simulator. The Settings page then shows an **Adaptive optics** card.

- **Calibration**: after the mount's calibration, the AO is calibrated too: its X and Y axes are stepped (5 steps at a time to 25, by default) and the guider measures which way and how far each step moves the star. It is kept separately from the mount's and redone when the camera or binning changes. **Clear AO calibration** forgets it.
- **Guiding**: with a calibrated AO, the AO corrects every frame (its own aggressiveness and minimum move). When it passes **Bump the mount at** (80 % of its travel) on either axis, the mount takes over in small moves of the **bump step** (1″, growing by half again each frame while the AO stays out there), sent without waiting so the AO frames go on, until the AO is back near its middle. Mount backlash compensation applies to the bumps at a fixed amount.
- **Dither**: moves the AO's lock position. With **Recentre the AO with the mount** on, the mount takes over the AO's offset after each dither and the stars settle again.
- The mount must be calibrated too, for the bumps.

A small square in the frame's corner shows the AO's position in its travel, with the bump point dashed; the dot turns red while the mount is being bumped. The status line shows the position in steps.

### A guide camera on a rotator

With an off-axis guider on a camera rotator, turning the rotator turns the guide camera too. Tick **The guide camera turns with it** (Rotator, Calibration card) and the calibration notes the rotator's angle; when the rotator turns, both guide directions turn with it, so no new calibration is needed. While the rotator moves, corrections wait; afterwards a guide star is chosen again and guiding goes on. If your optical train mirrors the image (a prism or an extra mirror), the image turns the other way: tick **Reversed (a mirrored image)**.

### Subframes and noise reduction

With **Subframes** on, once guiding has settled only a box round the stars guided on is read out of the camera (with multi-star, round all of them), which is quicker with slow cameras or connections. The box goes back to the whole frame while looping, choosing a star and calibrating, after a lost star, and whenever it would cover more than half the frame. Positions stay in whole-frame pixels and darks apply to the matching part; the frame shown is the last whole frame with each box pasted in, so it still looks live.

**Noise reduction** treats each frame (after the darks) before the stars are measured: a **2×2 mean** averages neighbouring pixels, a **3×3 median** removes single hot pixels and noise spikes. Use it for noisy cameras or very short exposures; with good frames, None measures the star most precisely.

### The PHD2-compatible server

Any program that can guide with PHD2 can use the Sidralune guider instead: Sidralune answers it as PHD2 would. Turn it on in the **PHD2-compatible server** card on the Settings page: **Let other programs guide with Sidralune**, the **Port** (4400, PHD2's own) and **Allow them to connect** from other computers (off: this computer only). It starts with Sidralune while it is on, and the card says **Listening on port 4400 · 1 connected**, or that the port is in use.

In the other program, choose PHD2 as the guider, on this computer and port 4400. It can then loop, choose the star, guide (calibrating when needed), dither, pause, stop, take a single frame, change the exposure and Dec guide mode, and read the pixel scale, the calibration, the star image and the guide steps. Settling, dithers and star loss reach it as PHD2's events. Sidralune's own Guiding tab keeps working alongside.

PHD2 itself must not use the same port. **Connect PHD2** knows Sidralune's server and refuses to connect to it, so Sidralune never guides itself in a loop.

### PHD2

To guide with PHD2, choose **Tools → Enable Server** in PHD2 and click **Connect PHD2**. PHD2 opens the guide camera itself, and most camera drivers serve one program at a time, so the guide camera belongs to one guider at a time: while a guide camera is connected in Sidralune, **Connect PHD2** is greyed out until you disconnect it, and while PHD2 is connected the Guide camera row in the Equipment tab says **In use by PHD2** until you click **Disconnect PHD2**. PHD2 takes and shows its guide frames and has its own settings and tools, so Loop, Select star and the Settings, Assistants, Darks and Tools pages are greyed out meanwhile. The sequencer, dithering, the meridian flip and the graph work the same with either guider, and the status line and the Guider pill name the guider in use.

## Polar alignment tab

The tab offers four methods under **Method**. Three-point uses the imaging camera and plate solving, and drift alignment the guide camera through the Sidralune guider (not while PHD2 is connected). Static and polar drift alignment work with either: see [Which camera](#which-camera).

| Method | Where to point | Needs |
|---|---|---|
| **Three-point (plate solving)** | Near the meridian and equator | The imaging camera, a mount that slews, plate solving |
| **Drift alignment** | Near the meridian and equator, then low in the east or west | The guide camera, guiding, the mount connected |
| **Static polar alignment** | Near the celestial pole | The imaging or the guide camera, plate solving; a mount that slews, or RA turned by hand |
| **Polar drift alignment** | Near the celestial pole | The imaging or the guide camera only: no calibration, no plate solving |

All four report the error in arcminutes and which way to move the polar axis. One runs at a time, and nothing else that moves the mount starts meanwhile: the other methods' start buttons, **Run sequence** and **Center** are greyed out until it ends. Static and polar drift alignment need the mount only tracking, whichever camera they use: they are greyed out while guiding, and guiding is held back while they run. **Start guiding**, **Calibrate & guide**, **Dither**, the manual guide buttons and the assistants' start buttons are greyed out meanwhile, and a sequence or a program asking through the [PHD2-compatible server](#the-phd2-compatible-server) is refused. With the mount connected, each checks first that it points where the method needs it: three-point and drift alignment within 60° of the celestial equator, static and polar drift alignment within 20° of the pole. Polar alignment is done with the mount's altitude and azimuth adjusters; Sidralune measures and shows the way.

### Three-point polar alignment

**Three-point polar alignment** measures how far your equatorial mount's polar axis is from the celestial pole, without needing to see Polaris:

1. Point the mount at a part of the sky near the meridian and celestial equator with clear sky on one side (to the east or west).
2. Set **RA step (°)** (25° by default) and **Exposure (s)**.
3. Click **Start polar alignment**. **Stop** ends it at any point.

Sidralune takes and plate-solves an image, rotates the RA axis by the step, repeats twice more, and computes the rotation axis of the mount from the three solutions. It reports:

- **Altitude error** and **Azimuth error** in arcminutes, with their directions,
- the **Total** error,
- advice on which adjustment to make.

The calculation compares the axis with the refracted pole, so the result is correct for the altitude you are at. Adjust your mount's altitude and azimuth bolts, then run the routine again until the total error is below a few arcminutes. The routine works with any mount that can slew in RA, and also in the [simulator](#the-simulated-observatory).

### Drift alignment

The classic method, with the guider doing the watching. A misaligned polar axis makes the stars drift in Dec at a rate that depends on the hour angle: near the meridian the drift tells the **azimuth** error, low in the east or west (25–35° up) the **altitude** error.

1. Point near the meridian and the celestial equator. The page says what the place measures, or where to point instead.
2. Click **Start measuring**. Sidralune starts guiding if it is not guiding yet, then holds back the Dec corrections (RA is still guided) and follows the Dec drift. The page shows the drift and the error it gives, with its uncertainty.
3. After a minute or two, when the error has settled, click **Stop**: the measurement ends and the error is shown. Dec corrections start again, and guiding goes on for the next measurement. Turn the azimuth adjuster the way it says, by the amount it says.
4. Measure again until the error is small, then point low in the east or west and do the same for the altitude.

Each measurement takes out the other error's last measurement, so the two converge. Unlike the usual drift method, which way to turn comes from the drift's sign and the hour angle, not a guess. **Start over** forgets both errors. Clicked while guiding is still starting, **Stop** cancels that instead (and stops guiding again when the page started it); guiding itself is stopped on the Guiding tab.

### Which camera

Static and polar drift alignment have a **Camera** choice: **Imaging camera** or **Guide camera**. The imaging camera is chosen when one is connected, the guide camera otherwise, until you choose yourself.

- **Imaging camera**: Sidralune takes short frames one after another (**Exposure (s)**, 2 s by default, and **Binning**, 2 by default), not saved. They show on the page and on the Imaging tab. The camera does one thing at a time, so captures, autofocus and sequences wait meanwhile. The camera tabs' **Stop** ends the frames, and the alignment with them.
- **Guide camera**: the frames come from the Sidralune guider's loop, started when it is not running (and stopped again when the page started it). Not while PHD2 is connected. The Guiding tab's **Stop** ends the loop, and the alignment with it.

An alignment reads its settings (the camera, **Exposure (s)**, **Binning**, **RA step**, **The mount turns itself**, **Mirrored**) when it starts, as the rest of Sidralune does: they stay editable while it runs, and a change is for the next start. When its frames stop (stopped elsewhere, or the camera failed), the page says so.

### Static polar alignment

Fast, near the pole, with either camera:

1. Point near the celestial pole (a degree or two away is fine). Plate solving must work there: the star catalogue must cover the camera's field.
2. Click **Start alignment**. Sidralune takes a frame, turns RA about 15° (**RA step**) west, takes another, turns again and takes a third, and plate-solves each. Without **The mount turns itself**, turn RA by hand (or with the hand controller) between the frames and click **Next frame**.
3. A point fixed to the camera turns about the mount's axis, so the three frames give the axis; the plate solution places the refracted celestial pole. The frame shows the **Mount axis** (a cross), the **Pole** (a circle) and a star with its **Target** circle: correcting the alignment moves every star the way the pole must move to reach the axis.
4. Turn the altitude and azimuth adjusters until the star is in its circle. The page follows the star in each new frame and says how far it still has to go. The alignment runs until you click **Stop**, which ends following the star, and the frames; **Start alignment** stays greyed out until then. Clicked while the mount turns, **Stop** stops the mount too.

The errors are shown too, with the advice. If the axis is outside the frame (a large error), the target may be too; turn the adjusters roughly first and start again.

### Polar drift alignment

Near the pole, with no calibration and no plate solving:

1. Point near the celestial pole and click **Follow a star**. Sidralune chooses the brightest star that is not saturated and follows it while the mount only tracks.
2. With a misaligned axis the stars drift slowly, at right angles to the error, at a rate set by its size. The page shows the error and its uncertainty as they settle (give it a few minutes for small errors).
3. Click **Stop**: measuring ends, and a circle shows where the star must go. The star is still followed.
4. Move the star into its circle with the altitude and azimuth adjusters, then click **Stop** again to end following it, and the frames. **Follow a star** stays greyed out until then.

Which way the error lies depends on whether the camera sees a mirror image (an off-axis guider's prism or a diagonal mirrors it). For the guide camera with a guider calibration this is known; otherwise, tick **Mirrored** if your image is mirrored.

## Dome & weather tab

**Dome / roof** shows the shutter state and azimuth.

| Button | Action |
|---|---|
| **Open** / **Close** | Open or close the shutter or roof |
| **Slew** | Turn the dome to the **Azimuth** entered (Follow telescope turns off) |
| **Park** | Park the dome (Follow telescope turns off, so the dome stays parked) |
| **Follow telescope** | Keeps the dome slit aligned with the telescope as it moves (slaving) |

**Weather & safety** shows the readings of the weather station: temperature, humidity and dew point, cloud cover, wind speed, rain rate, sky quality and sky temperature, and the safety monitor's verdict (**SAFE** or **UNSAFE**). The [sequencer's safety trigger](Sequencer#triggers) uses this to stop imaging, park and close up when conditions turn bad.

**Switches** lists the switches of the switch box connected in the Switch row (a power box, for example), as its driver names them: a tick box for one that is only on or off, a number for one with levels, such as a dew heater from 0 to 100 %. They follow the switch box, so a change made elsewhere shows within a second.

**Flat panel** (the Flat panel row in Equipment; ASCOM calls it a cover calibrator) shows the cover's state and the light's brightness. **Open cover** and **Close cover** move the dust cover; **Light on** lights the panel at the **Brightness** set, and **Light off** puts it out. For flat frames, close the cover, light the panel and take FLAT exposures; the sequencer's **Flat panel** and **Set switch** instructions do the same in a sequence.

## Meridian flips

German equatorial mounts must flip from one side of the pier to the other after an object crosses the meridian. When an imaging target passes the meridian by a set number of minutes while the mount is on the east side of the pier, the [sequencer's meridian flip trigger](Sequencer#triggers) stops guiding, re-slews to the target (the mount then chooses the other pier side), re-centres by plate solving and then resumes guiding.

## Commands

`Observatory: open equipment`, `Observatory: sequencer`, `Observatory: polar alignment`, `Observatory: abort all` (stops exposures, sequences and slews), `Connect simulated observatory` and `Disconnect all devices`.
