The **Observatory** workspace connects and controls your equipment: mount, camera, focuser, filter wheel, rotator, dome or roof, weather station, safety monitor, cover calibrator and guider. It takes images, focuses automatically, centres targets by plate solving, guides through PHD2, polar-aligns your mount and runs automated sequences.

The sequencer has its own page: [Sequencer](Sequencer).

> Automation moves real equipment. Stay with your mount the first time you use slewing, parking, meridian flips or sequences, and set slew limits in the mount or driver so the telescope cannot hit the pier or tripod.

## Contents

- [Supported equipment](#supported-equipment)
- [Equipment tab](#equipment-tab)
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
- [Polar alignment tab](#polar-alignment-tab)
- [Dome & weather tab](#dome--weather-tab)
- [Meridian flips](#meridian-flips)
- [Commands](#commands)

## Supported equipment

| Device | ASCOM | Alpaca | INDI | Other | Simulator |
|---|---|---|---|---|---|
| Mount (telescope) | Yes | Yes | Yes | LX200 / OnStep (serial or TCP) | Yes |
| Camera | Yes | Yes | Yes | | Yes, renders real catalogue stars |
| Focuser | Yes | Yes | Yes | | Yes |
| Filter wheel | Yes | Yes | Yes | | Yes |
| Rotator | Yes | | | | |
| Dome or roll-off roof | Yes | Yes | | | Yes |
| Weather station (ObservingConditions) | Yes | | | | Yes |
| Safety monitor | Yes | Yes | | | Yes (from the simulated weather) |
| Switch | Yes | | | | |
| Cover calibrator (flat panel) | Yes | | | | |
| Guider | | | | PHD2 | |

## Equipment tab

The tab has one row per device type. Each row has a list of available devices, **Connect** (which becomes **Disconnect**), **Setup…**, **ASCOM…** and a status message.

At the bottom:

| Button | Action |
|---|---|
| **Connect simulated observatory** | Connects a simulated mount, camera, focuser, filter wheel, dome and weather station in one go |
| **Discover Alpaca devices** | Searches the local network for Alpaca servers and adds their devices to the lists |
| **Add INDI server…** | Connects to an INDI server and adds its devices to the lists |
| **Disconnect all** | Disconnects every device |

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

- The **mount** slews at a realistic speed, tracks, parks, reports its pier side and accepts syncs.
- The **camera** renders a star field around the mount's pointing from the loaded catalogues (plus synthetic faint stars), with a realistic point-spread function that widens when the focuser is out of focus, sky background, read noise and shot noise. Plate solving, autofocus, centring and polar alignment therefore all work for real.
- The **focuser** has a hidden best-focus position; stars grow along a hyperbola away from it, exactly what autofocus expects. It also reports a slowly varying temperature.
- The **filter wheel** has per-filter focus offsets, and the **dome** and **weather station** (with a safety monitor) respond to commands and report plausible values.

### Optics

The **Optics** card holds the **Imaging focal length (mm)** and **Aperture (mm)** of the imaging train. They are written into FITS headers and used as plate-solving hints, so set them to match your telescope including any reducer or flattener.

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
| **Stop** | Abort the current exposure or series |

After each frame the status line shows the number of stars, the half-flux radius (HFR) and FWHM in pixels, the median level and the saved file name. These numbers are the quickest way to judge focus and sky conditions.

The viewer shows every frame the camera takes, not only the ones started here: frames from a running [sequence](Sequencer) and from autofocus appear too, and the status line starts with "Sequence ·" for sequence frames.

The camera can take only one exposure at a time, so only one job may use it: a capture or preview loop, a video recording, **Run autofocus**, **Center** (plate-solve centring), **Start polar alignment**, or a sequence. While one of them runs, the buttons that start the others are disabled and a note under the camera controls (on both tabs) says what is using the camera. **Stop** on either tab ends it, including a video recording, autofocus, centring, polar alignment or a sequence. Switching between **Loop** and **Start** is one exception: the new capture replaces the old one and starts as soon as its current exposure has ended. The other is **Record**, which takes over from **Live view** (but **Live view** does not interrupt a recording).

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
- `SWCREATE = Sidralune`.

### Cooling

Set the **Set point (°C)** and tick **Cooler**. The status shows the sensor temperature and cooler power. Cool down gradually and keep the set point within reach of the cooler (typically 30 to 35 °C below ambient) so that the power stays below about 80%.

### Focuser and autofocus

The **Focuser** section shows the position, whether it is moving and its temperature probe reading. **In** and **Out** move by **Step**.

**Run autofocus** takes a series of exposures (**AF exposure** long, binned 2×2) at focuser positions **AF step** apart on both sides of the current position, measuring the stars' half-flux radius in each. The points are plotted as a V-curve, a hyperbola is fitted to them, and the focuser moves to the minimum, approaching from one direction with backlash overshoot. If the minimum lies at the edge of the sweep, the routine recentres the sweep and continues, so it also works when you start far from focus.

Tips: use an exposure long enough to show 20 or more stars, choose a step that changes the HFR by roughly 10–20% per step near focus, and refocus when the temperature changes by a degree or two (the [sequencer](Sequencer#triggers) can do this automatically).

### Mount

The **Mount** section shows the mount's RA, Dec, altitude, azimuth, pier side and state. The arrow buttons move the mount at the selected **Rate**: **Guide 0.5×**, **Centre 8×**, **Find 64×** or **Slew max** (multiples of the sidereal rate).

Type an object name or coordinates in **Go to**, then:

- **Slew** slews there (converting J2000 to the apparent position of date),
- **Center** slews, then plate-solves and corrects until the object is centred (see below).

**Park**, **Unpark** and **Tracking** do what they say.

### Centring by plate solving

**Center** (and the sequencer's Center instruction) runs a closed loop: slew, take a short binned exposure, plate solve it, sync the mount to the solution, and slew again, until the target is within 1′ of the image centre or five attempts have been made. This removes pointing-model errors, and after a sync the mount's next slews are more accurate too. It needs a camera and the plate solver (see [Astrometry](Astrometry-and-framing#plate-solving)).

## Guiding tab

Sidralune uses [PHD2](https://openphdguiding.org) for guiding. In PHD2, choose **Tools → Enable Server**, then click **Connect PHD2**.

| Button | Action |
|---|---|
| **Connect PHD2** | Connects to PHD2's event server (port 4400 for the first instance) |
| **Start guiding** | Starts guiding with the existing calibration |
| **Calibrate & guide** | Recalibrates, then guides |
| **Dither** | Moves the guide star by a few pixels and waits for it to settle, so that hot pixels and noise patterns fall on different sky in each frame |
| **Stop** | Stops guiding |
| **Clear graph** | Clears the error graph |

The **Guiding error** graph plots the RA and Dec corrections in arcseconds. The status line shows the guider state and the RMS error in RA, Dec and total. Total RMS well below your image scale (arcseconds per pixel) means round stars.

## Polar alignment tab

**Three-point polar alignment** measures how far your equatorial mount's polar axis is from the celestial pole, without needing to see Polaris:

1. Point the mount at a part of the sky near the meridian and celestial equator with clear sky on one side (to the east or west).
2. Set **RA step (°)** (25° by default) and **Exposure (s)**.
3. Click **Start polar alignment**.

Sidralune takes and plate-solves an image, rotates the RA axis by the step, repeats twice more, and computes the rotation axis of the mount from the three solutions. It reports:

- **Altitude error** and **Azimuth error** in arcminutes, with their directions,
- the **Total** error,
- advice on which adjustment to make.

The calculation compares the axis with the refracted pole, so the result is correct for the altitude you are at. Adjust your mount's altitude and azimuth bolts, then run the routine again until the total error is below a few arcminutes. The routine works with any mount that can slew in RA, and also in the [simulator](#the-simulated-observatory).

## Dome & weather tab

**Dome / roof** shows the shutter state and azimuth.

| Button | Action |
|---|---|
| **Open** / **Close** | Open or close the shutter or roof |
| **Slew** | Turn the dome to the **Azimuth** entered |
| **Park** | Park the dome |
| **Follow telescope** | Keeps the dome slit aligned with the telescope as it moves (slaving) |

**Weather & safety** shows the readings of the weather station: temperature, humidity and dew point, cloud cover, wind speed, rain rate, sky quality and sky temperature, and the safety monitor's verdict (**SAFE** or **UNSAFE**). The [sequencer's safety trigger](Sequencer#triggers) uses this to stop imaging, park and close up when conditions turn bad.

## Meridian flips

German equatorial mounts must flip from one side of the pier to the other after an object crosses the meridian. When an imaging target passes the meridian by a set number of minutes while the mount is on the east side of the pier, the [sequencer's meridian flip trigger](Sequencer#triggers) stops guiding, re-slews to the target (the mount then chooses the other pier side), re-centres by plate solving and resumes guiding.

## Commands

`Observatory: open equipment`, `Observatory: sequencer`, `Observatory: polar alignment`, `Observatory: abort all` (stops exposures, sequences and slews), `Connect simulated observatory` and `Disconnect all devices`.
