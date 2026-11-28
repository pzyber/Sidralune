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
- [Guiding](#guiding)
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
| Galilean moons (Tonight, Solar system, Radio) | Meeus chapter 44 (lower-accuracy method), with transit and occultation geometry |
| Planets' moons (sky chart) | 65 moons of Mars, Jupiter, Saturn, Uranus, Neptune and Pluto: precessing ellipses fitted to JPL Horizons for 2000–2050, with tables of corrections along the orbit for moons that librate or share an orbit (Mimas, Tethys, Janus, Epimetheus, Helene, Telesto, Calypso, Atlas, Prometheus, Pandora); Hyperion and the irregular outer moons integrated from JPL states a year (Jupiter's, Hyperion) or four years apart, with the planet's and the Sun's gravity and, for Hyperion, Saturn's oblateness and Titan. Within 2000–2050 every moon is within 3″ of JPL Horizons (most within 1″); outside it they follow their mean ellipses, which keeps the regular moons close and the irregular ones rough |

## Rise, set, twilight and events

| Item | Method |
|---|---|
| Rise, transit and set | Altitude sampling with root refinement, standard altitudes −0.833° (Sun), −0.567° (stars) and for the Moon −0.567° less its semidiameter (about −0.83°), since its altitude is already seen from the site, parallax included. Moonset in Stockholm on 28 August 2026 agrees with published times within 2 minutes |
| Twilight | Sun at −6°, −12° and −18° |
| Moon phases, perigee and apogee | Root finding on elongation and distance |
| Conjunctions, oppositions, elongations | Root finding on the planets' ecliptic longitude difference from the Sun |
| Eclipses | Lunar: the Moon's distance from the Earth's shadow axis at each full Moon, with umbra and penumbra radii from Danjon's rule. Solar: geocentric Sun-Moon separation against the sum of their radii plus parallax at each new Moon. From a site, a solar eclipse is found with the topocentric Sun and Moon (Moon radius k = 0.2725076 Earth radii, mean limb), which also give whether the Sun is up and any sunrise or sunset during it (in 2-minute steps, refined); its contacts, maximum, type, magnitude and the share of the Sun's disc covered then come from the Besselian elements below, as in standard local circumstances: the maximum is the moment the site is closest to the shadow's axis (where the Sun's and Moon's centres look closest comes a few seconds apart in a shallow partial eclipse, as the Moon's distance changes with the Earth's turning), and the total or annular phase uses the umbral radius k = 0.272281 (the Moon's mean radius would make it seconds longer). The eclipse list, the phases and the map therefore agree to the tenth of a second, and the Sun's altitudes include refraction everywhere (with the site's pressure and temperature for the site, standard ones elsewhere). A lunar eclipse's umbral phase is checked against moonrise and moonset; its phases (penumbral, partial and total) are found by bisection where the Moon and the shadow touch, and moonrise and moonset during them in 2-minute steps, refined. For 12 August 2026 the contacts and maximum agree with published local circumstances within about a minute, the magnitude within 0.01. The eclipse map uses Besselian elements: cubic polynomials fitted every 10 minutes over ±3.5 hours to the geocentric Sun and Moon (light time, true equator of date), k = 0.2725076 for the penumbra and 0.272281 for the umbra as NASA uses, the WGS84 ellipsoid, and the local-circumstances method of the Explanatory Supplement; they agree with the topocentric calculation above within 0.4 s. All its lines are solved on the fundamental plane minute by minute, with the step halved down to a hundred-thousandth of a second where a line begins, ends or moves fast (near the horizon a tiny step on the plane is a long way on the ground), and pieces that meet at a fold (where a limit is born together with a second root that runs out to the horizon) joined: the limits of the path and of the partial eclipse as the places on the shadow's edge whose motion runs along it, the sunrise and sunset curves on the Earth's limb (the Sun's centre on the geometric horizon), and the central line from the shadow's axis, so they meet where they should; only the obscuration lines come from a 0.5° grid, cut exactly where the Sun is on the horizon at maximum. A lunar eclipse's shading is worked out for the view on screen from the circles where the Moon is up at each contact, so its edges follow those lines at any zoom. With DE440s, contacts for 22 July 2028 in Australia agree with Xavier Jubier's mean-limb predictions within 0.5 s and the path width within about 2 km. Clicked places are at the elevation in the box: 0 m, ETOPO1's height there when installed (1 arc-minute grid, above mean sea level, which is within about ±100 m of the height above the ellipsoid the method wants: a few hundredths of a second), or a typed value; the lines, path width and distance from the central line are for sea level. With the Moon's topography installed (LRO LOLA, 16 or 64 pixels per degree, heights above a 1737.4 km sphere), the total or annular phase uses the lunar limb profile: the Moon's mean-Earth frame from the IAU WGCCRE rotation model with its periodic terms (Archinal et al. 2011, agreeing with Meeus's librations within 0.15°), the outline seen along the shadow axis every 0.2° of position angle as the highest reach (R + h) cos θ of the surface within 5° in front of or behind the limb plane, one profile a minute shared by every place in the shadow (they look along the axis within 0.03°), and the phase lasting while the outline covers the whole Sun (total) or lies wholly inside it (annular), found in 2 s steps and bisected to 1 ms. For 22 July 2028 at three places in the Kimberley, the limb corrections of C2 and C3 agree with Xavier Jubier's within 0.3 s (16 or 64 pixels per degree, with DE440s) and the durations within 0.8 s. Without the topography, durations near the path edges can differ by several seconds from limb-corrected predictions. The eclipse list and the site's phases use the same limb-corrected phase |
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
| Centroid, FWHM, eccentricity | Adaptive (Gaussian-weighted) moments, as in Bernstein & Jarvis (2002). The weight is never narrower than a pixel, so stars only one or two pixels wide get the same FWHM wherever they fall within a pixel |
| Hot pixels | Sources narrower than a pixel (FWHM below 0.8 px) are rejected as hot pixels or cosmic rays |
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
| Planetary sharpening | À trous wavelets with soft-thresholded fine layers; unsharp mask; bilateral (Tomasi & Manduchi 1998) and sigma (Lee 1983) filters scaled to the grain measured on the disc (the median absolute residual of a 2-pixel Gaussian blur); optionally on the luminance only, the change added to each channel alike |
| Limb and halo protection | The sharpening blended back to the source in a band along the fitted limb; each pixel clamped towards the minimum and maximum of its 5 × 5 neighbourhood in the source |
| Tone curve | Monotone cubic Hermite interpolation (Fritsch & Carlson 1980), with gamma and a smoothstep contrast curve in one lookup table |
| Recorded PSF | The brightest point source above the median sky, out to the ring whose mean falls below 1% of its peak, normalised |
| Channel alignment (dispersion) | Red and blue registered onto green by phase correlation over the whole image (up to 1024 pixels around the centre of light, so a planet's limb is included), with a window tapered only at its edges and a second pass for the sub-pixel remainder |
| Sphere derotation | Re-projection on an oblate spheroid using the rotation period, sub-Earth latitude and pole angle from the ephemeris, with limb-darkening compensation; images weighed linearly by rotation from the target time and cut off beyond a limit; ring pixels (Saturn, in the equatorial plane between 1.236 and 2.270 radii, where in front of the globe or beside it) kept unrotated; Saturn's disc from the length of its rings along the line of nodes |
| Orientation from images | Pole angle and mirroring that make the first image, derotated to the last one's time, best match it: normalised correlation of the fine detail inside 0.75 of the radius, searched every 5°, then 1° and 0.25°, both mirrorings |
| Galilean moons and shadows | Meeus (1998) chapter 44 low-accuracy theory; each shadow where the line from the Sun through the moon (at the Sun's jovicentric declination and the phase angle) meets the spheroid, projected with the Earth's jovicentric declination. Moons and shadows left out of each derotated image and moved, as imaged, to their target-time positions |

## Guiding

The Sidralune guider (Observatory → Guiding) is written for Sidralune. Its ideas follow the long-established practice of autoguiders, in particular PHD2 (Open PHD Guiding). Predictive PEC is built from the published paper while the matrix routines are Sidralune's own.

| Item | Method |
|---|---|
| Choosing the star | Sidralune's star detector over the whole frame; single, unsaturated stars with SNR 8 or more, HFD 1–12 px, no neighbour with a fifth of the flux inside the search area, and room from the edges for calibration |
| Measuring the star | In a square around the last position: background and noise from the square's outer ring (median and MAD), the brightest spot on a 3×3 median (so single hot pixels do not count), then a Gaussian-weighted centroid (σ 1.5 px) iterated until it settles. Typically better than 0.1 px on synthetic stars with noise |
| Calibration | West and North legs in fixed steps until the star has moved the calibration distance (20 px), with the step chosen for about twelve steps from the guide rate, pixel scale and declination. North starts after three steady steps have taken up the Dec backlash. Each leg's direction and rate come from a least-squares line through all its positions, not only its end points |
| Calibration checks | Axes more than 15° from square; RA/Dec rate ratio more than 25 % from cos(Dec) (at equal guide rates); fewer than four steps; rates more than 40 % from those predicted by the focal length and guide rate; a declination beyond ±75° |
| Offsets | The star's offset from the lock position split into RA and Dec with a 2×2 solve, so axes not exactly at right angles still separate. The RA rate scales with cos(Dec) / cos(Dec at calibration) (limited at ±80°). After a meridian flip the RA direction turns 180°, and the Dec direction too when the mount reverses Dec itself |
| RA: Hysteresis | Blend = (1 − h) × error + h × previous blend; correction = aggressiveness × blend when the blend reaches the minimum move |
| Dec: Resist Switch | Corrections continue in the current direction; a reversal needs three errors in a row the other way, each over the minimum move and not shrinking, or (fast switch) one error over three minimum moves |
| Pulses | Pixels ÷ calibrated rate, limited to the maximum pulse; RA, then Dec; waited for by time and by the driver's IsPulseGuiding |
| Settling | Done when the star has stayed within 1.5 px of the lock position for 10 s, given up after 120 s (Start guiding); a dither moves the lock by a random amount up to the dither size along RA and Dec and uses the sequence's settle values |
| Multi-star | Up to 8 more stars (SNR 6 or more, a search area apart) with fixed offsets from the guide star while guiding. Each frame: every star measured in its own search area; displacements from the lock position plus each star's offset; stars more than max(2 px, 3 × 1.4826 × the median distance from the median displacement) left out; SNR-weighted mean. A guide star jump over 2.5 px is followed on the guide star alone. Without the guide star, two others suffice; after 10 frames the one with the best SNR becomes the guide star and the lock position moves to its reference |
| Star mass | The guide star's flux against the median of its last 10 accepted frames; a frame more than the tolerance (50 %) away is not used, and the third in a row resets the reference |
| Auto exposure | Steps 0.5–10 s within the set range; one shorter after 3 frames with SNR over 1.5 × target, one longer after 2 frames under the target |
| Dark library and bad-pixel map | Master darks as the per-pixel median of N dark frames, subtracted with the master's median added back; bad pixels are those more than k σ (MAD-based) above or below the master's median, each replaced by the median of its good neighbours |
| Lowpass2 | Least-squares line through the last 10 errors since a reset; correction = aggressiveness × the line's value now, zero under the minimum move or of the other sign, never more than the current error; a turn the other way over five minimum moves starts the line again |
| Z-filter | The uncorrected track (error + corrections sent so far) through a second-order Butterworth low-pass (bilinear transform) with its cutoff at 1/(2 × factor) cycles a frame; correction = filtered track − corrections so far |
| Backlash compensation | On a Dec reversal the amount is added to the pulse; in the next frame an overshoot past the minimum move takes 10 % off, an error still 80 % or more of the one before on the same side adds 10 %, within the least and most |
| Guiding Assistant | Seeing = √(Σ Δ² / 2(n − 1)) of frame-to-frame differences per axis; drift from a least-squares line; RA peak-to-peak of the residuals and the fastest change of a five-frame running mean; polar alignment error ≥ the size of the Dec drift (″/min) × 1436 / (120 π) arcmin (the sky turns once a sidereal day). Backlash: the x-intercept of a line through the South steps that moved the star more than 15 % of the way back, valid when the South rate is ≥ 90 % of the North rate; uncertainty = residual σ ÷ South rate. Suggested minimum move = 0.65 × seeing RMS |
| Calibration Assistant | Hour angle ±5° on the current side of the meridian; declination 0°, or the nearest declination towards the pole with altitude ≥ 30° there; the final slew approaches from 1° south. Rated poor for axes far from square or rates unlike cos(Dec), acceptable for the lesser warnings |
| Sky directions | Calibration notes which way the mount's own RA and Dec moved under West and North pulses. A North nudge or comet motion is turned into image pixels through the calibrated directions, with North turned round after a flip when the mount does not reverse Dec |
| Comet rates | From the comet's apparent topocentric places half an hour either side of now: ΔRA × cos(Dec) and ΔDec per hour, recomputed every 10 minutes. Training: lock position moves turned back into sky arcseconds, summed, divided by the time |
| Predictive PEC | After Klenske, Zeilinger, Schölkopf and Hennig (2016), Gaussian process-based predictive control for periodic error correction, IEEE Transactions on Control Systems Technology 24(1). The gear error g = measured RA error + all RA corrections sent, binned on a 5 s grid over the last 2.2 periods; a least-squares line removed; a Gaussian process with kernel 0.3 × squared-exponential (length one period) + 0.7 × periodic (exp(−2 sin²(π Δt / P) / 0.6²)), scaled to the variance left after the line, noise variance from the frame-to-frame differences ÷ 2. Solved by Sidralune's own Cholesky factorisation (with jitter when nearly singular) and triangular solves. Control = reactive gain × error (zero under the minimum move) + predictive gain × (f(t + Δt) − f(t)). For the first two periods the output blends from Hysteresis by the share of two periods recorded |
| Worm period | Every 5 minutes once two periods are recorded: a Lomb–Scargle periodogram of the detrended gear error (6 periods or 30 minutes kept) between 0.5 P and 2 P; a peak with normalised power ≥ 0.2 moves P a quarter of the way to it |
| Adaptive optics | AO calibration: X and Y legs of N-step moves, each direction and px/step from a least-squares line. Each frame: the offset split into AO axes, Hysteresis per axis, steps opposite the error within the travel. Bump: when the position on an axis passes the bump % of the travel either way, the AO offset turned into mount axes through both calibrations; a mount pulse of the bump step (× 1.5 each frame while it stays out, at most × 10), sent without waiting, axes alternating |
| Rotator | The calibration notes the rotator's angle; both calibrated directions turn by the rotator's change since (sign reversed for a mirrored train). Corrections wait while the rotator moves; afterwards the star is chosen again and the lock set to it |
| Subframes | The read box covers the guide star, the lock position and the other stars, padded by the search radius × 2 + 4 px; whole frames when it would cover more than half the frame or the star is lost. The box is pasted into a copy of the last whole frame |
| Noise reduction | 2×2 mean: each pixel the mean of itself and its right, lower and lower-right neighbours (a half-pixel shift, the same in every frame); 3×3 median: the median of the 3 × 3 neighbourhood |
| PHD2-compatible server | PHD2's event-server protocol: JSON lines over TCP (CRLF), JSON-RPC 2.0 requests, events to every client; Version (PHDSubver "Sidralune"), the state events and AppState on connecting; unknown methods answer error −32601 |
| Drift alignment | A polar axis offset by m towards the meridian above the pole and e towards the east makes the telescope drift north against the stars at ω (m sin H + e cos H), with ω the sky's turn (2π a sidereal day) and H the hour angle, whatever the declination. A least-squares line through the north part of the star's offset (turned to the sky through the calibration) gives the rate; near the meridian it gives e, low in the east or west m, each with the other's last value taken out. Errors against the geometric pole |
| Static polar alignment | Three plate-solved frames (imaging or guide camera): five points fixed to the camera (centre and corners) turned into the Earth-fixed frame (longitude = −hour angle) at each frame's time; the normal of the plane through each point's three places is the axis, from the point with the widest circle. The axis and the refracted pole placed in the last frame through its plate solution; the reference star's target = star + (axis − pole) |
| Polar drift alignment | In the camera's frame the sky turns about the pole and the camera about the mount's axis, so every star drifts at ω × the error, at right angles to it: error (px) = the drift velocity (px/s, least-squares lines through x and y) turned 90° and divided by ω. The star is followed by its centroid within the search radius of its last place. The turn is one way for a plain image, the other for a mirrored one (from the guider calibration's handedness for the guide camera, else as ticked), and the correction moves stars by +error near the north pole, −error near the south |

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
- the Sidralune guider on the simulators: calibration recovers the guide camera's simulated angle within 3° and its rates within 10 %, guiding holds a mount with periodic error, drift and backlash to well under an arcsecond, and dithers, lost stars and meridian flips are handled,
- every sequencer instruction, loops, triggers, autofocus and device behaviour with the simulators.

See [CONTRIBUTING.md](https://github.com/pzyber/Sidralune/blob/main/CONTRIBUTING.md#scientific-correctness) for the rules new algorithms must follow.
