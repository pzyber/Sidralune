# Radio astronomy

The **Radio** workspace supports amateur radio astronomy with inexpensive software-defined radios (SDRs) such as the RTL-SDR, Airspy or HackRF: hydrogen-line spectroscopy of the Milky Way, a radio telescope calculator, predictions of Jupiter's decametric radio storms, and tools for drift scans, pulsars and solar flare detection.

It has four tabs: **Hydrogen line / spectra**, **Radio telescope calculator**, **Jupiter & Sun** and **Time domain**.

## Contents

- [Hydrogen line / spectra](#hydrogen-line--spectra)
  - [Recording](#recording)
  - [Receiver settings](#receiver-settings)
  - [Opening recordings](#opening-recordings)
  - [Live capture](#live-capture)
  - [Calibration and baseline](#calibration-and-baseline)
  - [Velocities and the Milky Way](#velocities-and-the-milky-way)
- [Radio telescope calculator](#radio-telescope-calculator)
- [Jupiter & Sun](#jupiter--sun)
- [Time domain](#time-domain)
- [Getting started with hydrogen-line observing](#getting-started-with-hydrogen-line-observing)

## Hydrogen line / spectra

Neutral hydrogen gas in the Milky Way emits at 1420.405 MHz (21 cm). Its Doppler shift reveals how the gas moves, which traces the Galaxy's spiral arms and rotation.

### Recording

Record raw IQ data with your SDR software, centred near 1420.4 MHz, for one to several minutes. Record a second "OFF" recording too, either pointing at a cold part of the sky or tuned 1–2 MHz away (frequency switching).

### Receiver settings

| Setting | Meaning |
|---|---|
| **IQ format** | RtlSdrUint8 (RTL-SDR `.cu8`, `.bin`), HackRfInt8 (`.cs8`), Int16 (`.cs16`), Float32 (`.cf32`), WavStereo16 (stereo WAV IQ recordings). Detected from the file extension where possible |
| **Centre (MHz)** | The centre frequency of the recording |
| **Sample rate (MS/s)** | The recording's sample rate |
| **FFT size** | Spectral channels, 256 to 8192. 2048 at 2.4 MS/s gives 1.2 kHz channels (about 0.25 km/s) |
| **Max blocks** | Upper limit on how much of the file is averaged |
| **Tsys (K)** | System temperature, used for calibrating ON−OFF spectra |
| **Pointing (RA Dec)** | Where the antenna pointed, for velocity corrections and galactic coordinates. **Use selected object as pointing** fills it from the Sky chart |
| **X axis** | Frequency (MHz) or Velocity LSR (km/s) |
| **Baseline degree** | Polynomial degree for baseline removal |

### Opening recordings

**Open ON recording…** reads the file in blocks, computes averaged power spectra with a Blackman-Harris window, removes the DC spike typical of zero-IF receivers, and shows:

- the averaged **Spectrum**, with the hydrogen line marked,
- a **Waterfall** (time downwards, frequency across, false colour), which shows interference and whether the signal is steady.

**Open OFF / reference recording…** loads the reference.

### Live capture

**Live capture (rtl_tcp, 127.0.0.1:1234)** connects to an `rtl_tcp` server (run `rtl_tcp` on the computer with the dongle, or on a Raspberry Pi at the antenna) and shows a running-average spectrum and waterfall that update continuously. Click the button again to stop. Gain is set to 40 dB with AGC off.

### Calibration and baseline

- With an OFF spectrum loaded, the plot shows **(ON − OFF) / OFF × Tsys**: the antenna temperature in kelvin. This removes the receiver's bandpass shape.
- Drag across the hydrogen emission in the plot to mark the line window. With **Baseline degree** above 0, a polynomial is fitted outside the window and subtracted, flattening what remains of the bandpass.
- **Export CSV…** saves the spectrum.

### Velocities and the Milky Way

Set **X axis** to **Velocity LSR (km/s)** and enter the pointing. Velocities are radio velocities relative to the local standard of rest (LSR), correcting for the Earth's rotation and orbit and the Sun's motion, so measurements from different days can be compared.

Drag across the line and click **Analyse line (selected range = line window)** to get:

| Result | Meaning |
|---|---|
| Peak | Brightness and velocity of the strongest emission |
| Integrated | Area under the line (K km/s when calibrated) |
| Galactic l, b | Galactic coordinates of the pointing |
| N(HI) | Column density of hydrogen atoms, 1.823 × 10¹⁸ × integrated intensity (optically thin approximation) |
| Kinematic distance(s) | The distances along the line of sight where gas at the peak velocity sits, from a flat Galactic rotation curve (R₀ = 8.18 kpc, V₀ = 229 km/s). Inside the solar circle there are two (near and far) solutions |
| Tangent point | For longitudes in the first and fourth quadrants: the galactocentric radius and rotation speed at the tangent point, from the extreme velocity. A set of these across longitudes is a measurement of the Galaxy's rotation curve |

## Radio telescope calculator

Enter your system to see what it can detect:

| Input | Meaning |
|---|---|
| **Frequency (MHz)** | Observing frequency |
| **Dish diameter (m)** | Antenna diameter (or equivalent) |
| **Aperture efficiency** | 0.5–0.6 for typical home dishes |
| **System temperature (K)** | Receiver plus sky noise |
| **Bandwidth (MHz)** | Detection bandwidth |
| **Integration (s)** | Integration time |
| **Y-factor (hot/cold, dB)** | Optional: a measured hot/cold ratio, from which Tsys is computed instead |

Results: wavelength, half-power beam width, gain in dBi, effective area, Tsys, system equivalent flux density (SEFD) in jansky, and the radiometer sensitivity ΔT = Tsys / √(bandwidth × time) in kelvin and jansky.

The table lists bright radio sources (Cassiopeia A, Cygnus A, Taurus A and others) with their flux density at your frequency (from their spectral index), the antenna temperature they would produce, the expected signal-to-noise ratio, and their current altitude, so you can see which ones your system can detect.

## Jupiter & Sun

Jupiter emits powerful bursts at 18–40 MHz when its magnetic field interacts with its moon Io. The bursts come from specific combinations of Jupiter's System III central meridian longitude (CML) and Io's orbital phase, known as the Io-A, Io-B, Io-C and Io-D sources (and non-Io sources).

The header shows the current CML, Io phase and which source region is active. **Predict storms** lists, for the chosen number of **Days**, every window when one of the source regions (Io-A, Io-B, Io-C, Io-D, or the non-Io-A region) is active while Jupiter is more than 5° above your horizon and the Sun is down (UTC times). Io-related sources are the most likely to produce strong storms.

A simple dual-dipole antenna at 20.1 MHz with a Radio JOVE receiver or an SDR is enough to hear Jupiter's bursts as "ocean waves" and clicks.

## Time domain

Tools for time series (time, value) loaded with **Open time series…** from CSV or text files:

| Tool | Description |
|---|---|
| **Fit drift scan** | Fits a Gaussian to a drift scan (a source drifting through a fixed beam). Reports the transit time, the FWHM duration and the peak, and converts the duration to the beam width at the source's declination **δ** (beam = 15.041″/s × FWHM × cos δ) |
| **Fold at period** | Folds the series at **P (s)** into 64 phase bins and reports the peak signal-to-noise ratio, for detecting pulsars (the default is PSR B0329+54's 0.7145 s) |
| **Detect SIDs** | Finds sudden ionospheric disturbances (rapid rises in VLF signal strength caused by solar flares) in a VLF receiver log, and marks them on the plot |
| **DM delay** | Dispersion delay of a pulse between 408 and 1420 MHz for a dispersion measure **DM** (pc cm⁻³) |

## Getting started with hydrogen-line observing

A working hydrogen-line telescope can be built from:

- an RTL-SDR v3 or v4, or an Airspy,
- a low-noise amplifier with a 1420 MHz band-pass filter (for example the Nooelec SAWbird+ H1),
- a horn antenna (cardboard and aluminium foil works) or a 1–2 m dish.

Point it at the Milky Way (Cygnus, Cassiopeia, or the galactic centre region in Sagittarius when it is up), record a few minutes ON and a few minutes OFF, and open both here. Even a small horn shows the hydrogen line clearly, and a series of pointings along the galactic plane traces the spiral arms.

See [Databases and citizen science](Databases-and-Citizen-Science.md#amateur-science) for radio astronomy groups.
