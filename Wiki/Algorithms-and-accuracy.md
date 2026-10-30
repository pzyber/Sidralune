Every calculation in Sidralune is based on published methods. This page lists them by area, with the accuracy you can expect and the references. It is meant for users who want to know how far to trust a number, and for contributors.

## Contents

- [Time](#time)
- [Coordinates and reference frames](#coordinates-and-reference-frames)
- [Sun, Moon and planets](#sun-moon-and-planets)
- [Physical ephemerides](#physical-ephemerides)
- [Rise, set, twilight and events](#rise-set-twilight-and-events)
- [Comets and asteroids](#comets-and-asteroids)
- [Satellites](#satellites)
- [Visibility and sky brightness](#visibility-and-sky-brightness)
- [Star detection and measurement](#star-detection-and-measurement)
- [Registration and plate solving](#registration-and-plate-solving)
- [Calibration and stacking](#calibration-and-stacking)
- [Image processing](#image-processing)
- [Planetary imaging](#planetary-imaging)
- [Photometry and time series](#photometry-and-time-series)
- [Spectroscopy](#spectroscopy)
- [Radio astronomy](#radio-astronomy)
- [Numerical methods](#numerical-methods)
- [How accuracy is tested](#how-accuracy-is-tested)

## Time

| Item | Method |
|---|---|
| Time scales | UTC, TAI, TT, UT1 and TDB. TDB uses the dominant periodic term (Fairhead & Bretagnon 1990), good to about 30 µs |
| Leap seconds | IERS table, 1972 to the present |
| ΔT (TT − UT1) | From leap seconds for 1972 onwards; Espenak & Meeus (2006) polynomials for other dates |
| Sidereal time | Earth rotation angle (IAU 2000) and IAU 2006 GMST; apparent sidereal time with the equation of the equinoxes |
| Julian dates | Meeus, *Astronomical Algorithms*, chapter 7 |

## Coordinates and reference frames

| Item | Method |
|---|---|
| Catalogue frame | ICRS / J2000, with proper motion applied to the date |
| Precession | IAU 2006, Fukushima-Williams angles with frame bias |
| Nutation | Leading terms of the IAU 1980/2000 series (about 0.01″ level) |
| Aberration | Annual aberration from the Earth's barycentric velocity |
| Light time | Iterated for solar-system bodies |
| Topocentric correction | Observer position on the WGS 84 ellipsoid |
| Refraction | Sæmundsson / Bennett formula scaled by temperature and pressure |
| Airmass | Pickering (2002), valid down to the horizon |
| Galactic and supergalactic coordinates | IAU rotation matrices |

## Sun, Moon and planets

| Source | Method | Accuracy |
|---|---|---|
| JPL DE440s (when installed) | NAIF SPK reader, Chebyshev segment types 2 and 3, barycentric states | Milliarcseconds, 1849–2150 |
| Analytic planets | Standish (JPL) Keplerian elements and rates, with Jupiter–Saturn perturbation terms for Jupiter | About 127″ RMS against JPL Horizons over 1990–2045 (the Saturn and Uranus terms made the agreement worse and are disabled) |
| Analytic Moon | ELP-2000/82 truncated series as in Meeus chapter 47 | About 10″ in longitude, 4″ in latitude |
| Magnitudes | Mallama & Hilton (2018) planetary magnitude formulas, including Saturn's rings | |

## Physical ephemerides

| Item | Method |
|---|---|
| Planet rotation, orientation and radii | IAU WGCCRE (2015) pole positions, prime meridians and radii |
| Jupiter central meridians | Systems I, II and III |
| Saturn's rings | Ring-plane tilt B and position angle P (Meeus chapter 45) |
| Sun | P, B0, L0 and Carrington rotation (Meeus chapter 29) |
| Moon | Optical libration, position angle of the axis, selenographic colongitude (Meeus chapter 53) |
| Galilean moons | Meeus chapter 44 (lower-accuracy method), with transit and occultation geometry |

## Rise, set, twilight and events

| Item | Method |
|---|---|
| Rise, transit and set | Altitude sampling with root refinement, standard altitudes −0.833° (Sun), −0.567° (stars) and a parallax-dependent value for the Moon |
| Twilight | Sun at −6°, −12° and −18° |
| Moon phases, perigee and apogee | Root finding on elongation and distance |
| Conjunctions, oppositions, elongations | Root finding on the planets' ecliptic longitude difference from the Sun |
| Eclipses | Lunar: the Moon's distance from the Earth's shadow axis at each full Moon, with umbra and penumbra radii from Danjon's rule. Solar: geocentric Sun-Moon separation against the sum of their radii plus parallax at each new Moon |
| Meteor shower peaks | Solar longitude of the peak (IMO calendar values, on the J2000.0 ecliptic and equinox as the IMO gives them) |

## Comets and asteroids

| Item | Method |
|---|---|
| Orbit formats | MPC packed designations and epochs, MPCORB, CometEls, JPL SBDB |
| Propagation | Two-body motion with universal variables (Stumpff functions), valid for elliptic, parabolic and hyperbolic orbits; light-time correction |
| N-body integration | Dormand-Prince 5(4) adaptive integrator with planetary perturbations, available to scripts |
| Asteroid magnitudes | IAU H, G system (Bowell et al. 1989) |
| Comet magnitudes | Total magnitude m = H + 5 log Δ + 2.5 K log r |
| Diameters | D = 1329 km / √albedo × 10^(−H/5) |
| Occultations | Close approaches of the asteroid's geocentric track to Gaia DR3 stars; shadow width from the estimated diameter; ground track and observer offset from the shadow's motion |
| MPC reports | MPC 1992 80-column format and ADES 2017 PSV |

Accuracy is limited by the orbit: for well-observed numbered asteroids, arcseconds; for newly discovered comets and NEAs, possibly arcminutes or more. Refresh orbits regularly.

## Satellites

SGP4 (Hoots & Roehrich 1980, as revised by Vallado et al. 2006, "Revisiting Spacetrack Report #3") with WGS-72 constants. Deep-space objects (period of 225 minutes or more) use the same secular model rather than full SDP4, which is adequate for visual pass predictions over a few days. Accuracy for low orbits is about 1 km at the TLE epoch, degrading by a few kilometres per day.

## Visibility and sky brightness

| Item | Method |
|---|---|
| Natural and artificial sky brightness | Site SQM value at the zenith, brightening with airmass (van Rhijn-like), dimmed by extinction |
| Moonlight | Krisciunas & Schaefer (1991) |
| Twilight | Empirical twilight brightness as a function of the Sun's altitude |
| Detection threshold | Schaefer (1990): threshold illuminance I = c₁ (1 + √(c₂ B))² for a background B, with Ricco/Piper spatial summation for extended objects |
| Eye pupil | 7.8 mm at 20 years, decreasing about 0.45 mm per decade (after Watson & Yellott 2012) |
| Telescope | Light grasp, transmission, central obstruction, exit pupil, seeing and diffraction blur |
| Naked-eye limiting magnitude from SQM | NELM = 7.93 − 5 log₁₀(10^(4.316 − SQM/5) + 1) |

The model predicts typical detectability, not certainty: individual eyes and nights vary by half a magnitude or more.

## Star detection and measurement

| Item | Method |
|---|---|
| Background | Mesh of robust (sigma-clipped) local medians with interpolation |
| Noise | Multiresolution support / difference noise estimators |
| Detection | Thresholding above the background noise with connected components |
| Centroid, FWHM, eccentricity | Adaptive (Gaussian-weighted) moments, as in Bernstein & Jarvis (2002); unbiased FWHM even for undersampled stars |
| Half-flux radius | Radius containing half the background-subtracted flux |
| PSF fitting | Gaussian and Moffat fits by Levenberg-Marquardt |

## Registration and plate solving

| Item | Method |
|---|---|
| Star matching | Triangle-similarity invariants (Valdes et al. 1995), using each star's nearest neighbours (Beroiz et al. 2020) |
| Stars used | The brightest stars detected (at most 80). Saturated stars are left out while at least 20 others remain; in short exposures of bright fields, such as polar alignment frames, they are matched too |
| Robust fitting | RANSAC, then least squares on the inliers |
| Transforms | Translation, similarity, affine, homography, 2nd-order polynomial |
| World coordinates | TAN (gnomonic) projection with a CD matrix and SIP distortion polynomials (Shupe et al. 2005), iteratively refined |
| Blind solving | Field-size ladder from 0.25° to 60° and sky tiling, with mirrored parity tested |
| External solvers | ASTAP command line; nova.astrometry.net web API |

## Calibration and stacking

| Item | Method |
|---|---|
| Masters | Row-band streamed combination to bound memory |
| Dark scaling and optimisation | Exposure-time scaling; golden-section search for the dark factor that minimises the calibrated frame's noise |
| Flats | Normalised to a mean of 1 per Bayer colour |
| Hot and cold pixels | From the master dark and by local-median deviation; replaced by same-colour neighbours |
| Debayering | Bilinear, super-pixel, Malvar-He-Cutler (2004) high-quality linear interpolation, and an edge-directed method |
| Frame score | Σ√SNR of stars ÷ (FWHM × (1 + eccentricity)) |
| Rejection | Kappa-sigma, median kappa-sigma, Winsorized sigma (Huber 1981), linear fit clipping, generalised ESD (Rosner 1983), percentile clipping, auto-adaptive weighting (Stetson 1989), entropy-weighted average |
| Normalisation | Additive, additive with scaling, multiplicative, on robust location and scale |
| Weighting | Inverse noise variance, score, SNR |
| Drizzle | Variable-pixel linear reconstruction (Fruchter & Hook 2002) with a square drop kernel |
| Interpolation | Lanczos-3, bicubic, bilinear, nearest |

## Image processing

| Item | Method |
|---|---|
| Wavelets | Starlet (à trous) transform with the B3 spline kernel (Starck, Murtagh & Fadili 2010) |
| Background extraction | Grid sampling with robust rejection; polynomial or thin-plate spline radial basis function surface |
| Auto stretch | Screen transfer function: shadows at median − 2.8 × normalised MAD, background mapped to 0.25 with the midtones transfer function |
| Generalised hyperbolic stretch | Payne & Cranfield (2021) |
| Arcsinh stretch | Lupton et al. (2004), colour preserving |
| Deconvolution | Richardson (1972) / Lucy (1974); with total-variation regularisation (Dey et al. 2006); Wiener; Van Cittert; Landweber; blind Richardson-Lucy (Fish et al. 1995) |
| PSFs | Empirical (stacked from stars), Gaussian, Moffat, Airy with central obstruction |
| Noise reduction | Wavelet soft thresholding at k × layer noise; non-local means (Buades et al. 2005) |
| Local contrast | CLAHE (Zuiderveld 1994) |
| Star removal | Star mask, harmonic (Laplace) inpainting and noise injection |
| Star reduction | Morphological erosion blended through a star mask |
| Colour calibration | Star-based white balance; photometric calibration against Gaia DR3 colours with a blackbody model |
| SCNR | Average-neutral, maximum-neutral and additive protection methods |
| PixelMath | Expression compiler to a stack machine, evaluated in parallel |

## Planetary imaging

| Item | Method |
|---|---|
| Object detection | Otsu threshold, largest connected component, intensity-weighted centroid |
| Frame quality | Laplacian energy, gradient energy or local contrast, after a noise-robust blur |
| Global alignment | Phase correlation and centroid alignment |
| Local alignment | Multi-point alignment boxes with sub-pixel cross-correlation, multi-scale, per-point frame selection, smooth blending |
| Field rotation | Parallactic angle from site, time and target; or estimated from the images by polar cross-correlation |
| Limb fitting | Radial gradient edges, RANSAC and Kåsa algebraic circle fit |
| Sphere derotation | Re-projection on an oblate spheroid using the rotation period, sub-Earth latitude and pole angle from the ephemeris, with limb-darkening compensation |

## Photometry and time series

| Item | Method |
|---|---|
| Aperture photometry | Circular apertures with partial-pixel weighting, sky annulus with sigma clipping and mode estimate, CCD noise equation (Merline & Howell 1995) |
| Ensemble differential photometry | Weighted mean of comparison-star zero points, optional colour transformation |
| Time corrections | HJD and BJD_TDB from the light-travel (Rømer) delay to the Sun and to the solar-system barycentre |
| Lomb-Scargle | Generalised (floating-mean, weighted) periodogram (Zechmeister & Kürster 2009), with false-alarm probability |
| Box least squares | Kovács, Zucker & Mazeh (2002) |
| Phase dispersion minimisation | Stellingwerf (1978) |
| Minimum timing | Kwee & van Woerden (1956) |
| Transit model | Quadratic limb darkening with Mandel & Agol (2002) geometry, numerically integrated |
| Fitting | Levenberg-Marquardt with parameter bounds and covariance errors |
| MCMC | Affine-invariant ensemble sampler (Goodman & Weare 2010; Foreman-Mackey et al. 2013) |
| AAVSO reports | AAVSO Extended File Format specification |

## Spectroscopy

| Item | Method |
|---|---|
| Trace | Peak tracking in blocks along the dispersion axis, polynomial fit |
| Extraction | Box or optimal extraction (Horne 1986) with sky subtraction |
| Wavelength calibration | Lamp line identification by pattern matching from an approximate solution, polynomial dispersion fit |
| Continuum normalisation | Iterative asymmetric sigma-clipped polynomial fit |
| Lines | Centroid, FWHM, equivalent width and depth from a local continuum |
| Radial velocity | Doppler shift of lines; cross-correlation with a template; barycentric correction |
| Temperature | Planck fit and Wien's law |
| Instrument response | Smoothed ratio of observed to reference spectrum |

## Radio astronomy

| Item | Method |
|---|---|
| Spectra | Windowed FFT (Blackman-Harris) averaging, DC spike suppression |
| Calibration | (ON − OFF) / OFF × Tsys; Y-factor method for Tsys |
| Velocity frames | LSR correction with the solar motion of Schönrich, Binney & Dehnen (2010) |
| Galactic rotation | Flat rotation curve, R₀ = 8.18 kpc (GRAVITY 2019), V₀ = 229 km/s (Eilers et al. 2019); kinematic distances and tangent points |
| HI column density | N = 1.823 × 10¹⁸ ∫ T_B dv (optically thin) |
| Jupiter | Io phase and System III CML source regions (Carr, Desch & Alexander 1983) |
| Pulsars | Epoch folding; incoherent dedispersion with the cold-plasma dispersion constant 4.148808 × 10³ s MHz² pc⁻¹ cm³ |
| Radiometer equation | ΔT = Tsys / √(B τ) |

## Numerical methods

Fast Fourier transform (radix-2 and Bluestein), cubic splines, LU and QR linear algebra, polynomial fitting, Levenberg-Marquardt and Nelder-Mead optimisation, Brent root finding, golden-section search, and an ensemble MCMC sampler, all implemented in `Sidralune.Core/Math`. Image loops run in parallel on all processor cores with `Parallel.For` and unsafe pointer code.

## How accuracy is tested

The test suite (`Sidralune.Tests`, about a thousand checks across every library, plus `Sidralune.AppTests` for the application itself) checks results against independent references, including:

- Julian dates, sidereal time, nutation, precession and Kepler's equation against Meeus' worked examples, and coordinate transforms, refraction, the Sun's orientation and the Moon's libration against Meeus' examples,
- planetary positions against JPL Horizons at many dates (the analytic theory's error budget is measured, not assumed),
- seasons, eclipses, conjunctions, oppositions, elongations and twilight against published times, and meteor shower peaks against the IMO calendar,
- SGP4 against the Vallado test cases, and satellite passes for consistency,
- reading JPL SPK kernels (both byte orders) from small kernels with known Chebyshev series,
- MPC format packing and unpacking against MPC documentation examples, and ADES and AAVSO report formats,
- plate solving, centring, polar alignment, registration (every transform model) and stacking on synthetic star fields with known transforms, including rotated and mirrored fields without hints,
- calibration (bias, dark scaling, flats, hot and cold pixels), every pixel rejection method, drizzle and mosaics,
- debayering, background extraction, colour tools, filters, wavelets, PixelMath, star tools and stretches,
- every image and video format Sidralune writes, and raw (lossless JPEG) decoding, read back and compared,
- star FWHM and PSF fits against synthetic stars of known width,
- photometry, period searches, transit fitting, spectral extraction and line measurement on synthetic data with known answers,
- radio tools (IQ files in every format, dedispersion, drift scans) and an rtl_tcp server,
- constellation identification of known stars, and catalogue searches against a brute-force search,
- device protocols against stand-in servers (LX200/OnStep, PHD2, INDI, ASCOM Alpaca) and stand-in ASCOM drivers,
- every sequencer instruction, loops, triggers, autofocus and device behaviour with the simulators.

See [CONTRIBUTING.md](https://github.com/pzyber/Sidralune/blob/main/CONTRIBUTING.md#scientific-correctness) for the rules new algorithms must follow.
