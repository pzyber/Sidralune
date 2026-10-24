Sidralune reads and writes the standard formats of amateur and professional astronomy, all implemented in its own code without external libraries.

## Contents

- [Images](#images)
- [Videos](#videos)
- [Spectra](#spectra)
- [Catalogues, orbits and ephemerides](#catalogues-orbits-and-ephemerides)
- [Reports and exports](#reports-and-exports)
- [Sidralune's own files](#sidralunes-own-files)
- [FITS keywords Sidralune reads](#fits-keywords-sidralune-reads)

## Images

| Format | Extensions | Read | Write | Notes |
|---|---|---|---|---|
| FITS | `.fit`, `.fits`, `.fts` | Yes | Yes | 8-, 16-, 32- and 64-bit integer and 32/64-bit float data (`BITPIX` 8, 16, 32, 64, −32, −64), `BZERO`/`BSCALE`, primary HDU and IMAGE extensions, mono and 3-plane colour cubes, top-down row order (`ROWORDER`), Bayer keywords. Written as 16-bit integer or 32-bit float with the full header |
| XISF | `.xisf` | Yes | Yes | Extensible Image Serialization Format, including zlib and LZ4 (and LZ4-HC) compressed data blocks and byte shuffling. Written as 16-bit or 32-bit float, compressed |
| TIFF | `.tif`, `.tiff` | Yes | Yes | 8-, 16- and 32-bit integer and 32/64-bit float, mono and RGB, chunky or planar, strips or tiles, uncompressed, LZW, Deflate and PackBits. Written as 16-bit or 32-bit float |
| DNG | `.dng` | Yes | | Raw CFA data, including lossless-JPEG compressed tiles |
| Canon CR2 | `.cr2` | Yes | | Raw sensor data (lossless JPEG with slices) |
| Canon CR3 | `.cr3` | Yes | | Full-size RAW (tested with the EOS Ra), read with Sidralune's own decoder. The masked border is cropped off and its level subtracted as the black level; exposure, ISO, date and camera come from the file. Compressed C-RAW files, and files written with the newer revision of Canon's codec, are not read yet: convert those to DNG with Adobe DNG Converter |
| PNG | `.png` | Yes | Yes | 8-bit; written with the display stretch applied |
| JPEG | `.jpg`, `.jpeg` | Yes | Yes | 8-bit; written with the display stretch applied |
| BMP | `.bmp` | Yes | | |

Images are held in memory as 32-bit floating point, normalised so that the full range of an integer format is 0–1. Processing can produce values outside that range; 32-bit float formats keep them.

## Videos

| Format | Read | Write | Notes |
|---|---|---|---|
| SER | Yes | Yes | Mono, Bayer and RGB/BGR, 8 and 16 bits, with per-frame time stamps from the trailer |
| AVI | Yes | Yes | Read: uncompressed RGB24/RGB32, 8-bit grey (Y800, GREY, indexed), YUY2 and MJPEG. Write: uncompressed |
| Image sequences | Yes | Yes | A folder or a selection of FITS, TIFF, PNG or other image files, read in name order |

## Spectra

| Format | Read | Write | Notes |
|---|---|---|---|
| FITS 1D | Yes | Yes | Linear wavelength axis from `CRVAL1`, `CDELT1` (or `CD1_1`) and `CRPIX1`. Written in BeSS format |
| Text / CSV | Yes | Yes | Two columns: wavelength (Å) and flux; comments starting with `#` are ignored |

## Catalogues, orbits and ephemerides

| Format | Used for |
|---|---|
| HYG CSV (v4.x) | Star catalogue |
| OpenNGC CSV (semicolon separated) | Deep-sky catalogue |
| Stellarium `constellationship.fab` | Constellation lines |
| Stellarium `constellation_boundaries.dat` | IAU constellation boundaries |
| MPCORB / NEA (MPC 1-line orbit format, packed designations and epochs) | Asteroid orbits |
| MPC `CometEls.txt` | Comet orbits |
| JPL SBDB API (JSON) | Single-object orbits |
| CelesTrak TLE (two-line elements, with name lines) | Satellite orbits |
| JPL SPK (`.bsp`, DAF binary, Chebyshev segment types 2 and 3) | DE440s planetary ephemeris |

## Reports and exports

| Format | Produced by |
|---|---|
| AAVSO Extended File Format | Photometry → Measure → **AAVSO report…** |
| MPC 1992 80-column format | Solar sys. → MPC astrometry |
| ADES PSV (pipe-separated values) | Solar sys. → MPC astrometry |
| BeSS FITS | Spectra → Analyse → **Save** |
| WCS in FITS header (TAN or TAN-SIP) | Astrometry → **Write WCS to FITS…** |
| CSV | Light curves, ephemerides, catalogue searches, query results, observing lists, the logbook, radio spectra, every plot (right-click → **Export data as CSV…**) |
| PNG | Sky charts, plots, images |

CSV files always use a full stop as the decimal separator and a comma as the field separator, whatever the Windows language settings, so they can be exchanged between computers.

## Sidralune's own files

| File | Contents | Location |
|---|---|---|
| `settings.json` | Sites, equipment, observer details, preferences | `%LOCALAPPDATA%\Sidralune\Settings` |
| `planner.json` | Observing lists and logbook | `%LOCALAPPDATA%\Sidralune\Settings` |
| `.sdstack` | Deep-sky stacking file lists | Wherever you save them |
| `.json` sequence | Sequencer instruction trees | Wherever you save them |
| `.cs` | C# scripts | `%LOCALAPPDATA%\Sidralune\Data\Scripts` by default |
| Log files | One file per day, plus `crash-<date>-<time>.txt` reports | `%LOCALAPPDATA%\Sidralune\Logs` |

All of these are plain text (JSON, CSV or C#) and can be backed up, versioned or edited by hand.

## FITS keywords Sidralune reads

| Keyword | Used for |
|---|---|
| `DATE-OBS` (or `DATE_OBS`) | Observation start time (UTC) |
| `EXPTIME`, `EXPOSURE`, `EXP_TIME` | Exposure time |
| `RA`, `DEC`, `OBJCTRA`, `OBJCTDEC` | Plate-solving hints |
| `CTYPE`, `CRVAL`, `CRPIX`, `CD`/`CDELT`/`CROTA`, `A_ORDER`/`B_ORDER` and SIP coefficients | Existing WCS |
| `FOCALLEN`, `XPIXSZ`, `PIXSIZE1` | Image scale for plate solving |
| `BAYERPAT`, `XBAYROFF`, `YBAYROFF` | Bayer pattern for debayering |
| `FILTER`, `OBJECT`, `IMAGETYP` / `FRAME` | Frame classification |
| `CCD-TEMP`, `CCDTEMP`, `SENSOR-TEMP` | Sensor temperature |
| `GAIN`, `EGAIN` | Gain |
| `ROWORDER` | Row order (`TOP-DOWN` images are flipped on reading) |
