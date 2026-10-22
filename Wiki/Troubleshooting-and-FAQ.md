## Contents

- [Installation and start-up](#installation-and-start-up)
- [Data and catalogues](#data-and-catalogues)
- [Positions and times](#positions-and-times)
- [Sky chart and planning](#sky-chart-and-planning)
- [Equipment](#equipment)
- [Plate solving](#plate-solving)
- [Stacking and processing](#stacking-and-processing)
- [Planetary](#planetary)
- [Photometry, spectroscopy and radio](#photometry-spectroscopy-and-radio)
- [Online features](#online-features)
- [Scripts](#scripts)
- [Getting help](#getting-help)

## Installation and start-up

**Sidralune does not start, or Windows says a .NET Framework is missing.**
Install .NET Framework 4.8.1 from Microsoft. It is a free runtime included with recent Windows 11 builds.

**Windows SmartScreen warns about the program.**
Self-built and unsigned executables trigger this warning. Click **More info → Run anyway** if you built or downloaded it from the official repository.

**The window looks blurry or too small on a high-resolution screen.**
Sidralune is per-monitor DPI aware. If it still looks wrong, check that Windows' compatibility settings for `Sidralune.exe` do not override high-DPI scaling.

**Something went wrong. Where are the details?**
Click **Log** in the bottom-right corner, or open `%LOCALAPPDATA%\Sidralune\Logs`. Crashes are saved as `crash-<date>-<time>.txt`.

**How do I reset everything?**
Close Sidralune and delete or rename `%LOCALAPPDATA%\Sidralune\Settings\settings.json` (settings) and `planner.json` (observing lists and logbook). Downloaded data in `Data` can stay.

## Data and catalogues

**The sky chart only shows a few stars and the Messier objects.**
You are using the built-in catalogue. Download the recommended datasets in **Settings → Data**. The status bar's data pill shows what is loaded.

**A download failed.**
Check your internet connection and try again; the servers (JPL, MPC, GitHub, CelesTrak) are occasionally slow. Behind a proxy, set it in Windows' internet options, which Sidralune uses. You can also download the files elsewhere and copy them in; see [Data and catalogues](Data-and-catalogues#adding-files-by-hand).

**I cannot remove the DE440 ephemeris.**
Sidralune releases the file before removing it. If removal still fails, another program (an antivirus scanner, a backup tool) may have it open; try again later.

**A new comet is not found.**
Update the MPC comet elements (**Settings → Data → Update installed**), or add it directly with **Solar sys. → Minor bodies → Fetch from JPL SBDB**.

## Positions and times

**Positions differ from another program.**
Check that you compare like with like:

- J2000 (astrometric) or apparent (of date) coordinates. Sidralune shows both in the object panel.
- Topocentric (for your site) or geocentric. For the Moon the difference can reach 1°.
- With or without refraction (**Settings → Appearance**). Near the horizon refraction lifts objects by up to 0.5°.
- Whether DE440s is installed. Without it, planet positions can be off by a few arcminutes.
- The same site coordinates, elevation and time zone.

**Times are off by an hour.**
Check the site's **Time zone** in **Settings → Location**. Sidralune uses the Windows time zone rules, including daylight saving time, so choose the zone by its name rather than a fixed UTC offset.

**The clock is amber.**
You are looking at a time other than now. Click **Live** in the top bar.

## Sky chart and planning

**Objects are listed as Not visible even though they are up.**
The visibility model accounts for your sky brightness, the Moon, your telescope and your eyes. Check **Settings → Location** (SQM, extinction) and **Settings → Equipment** (the active telescope and eyepiece), and **Settings → Observer & accounts** (age, experience). A faint galaxy near a bright Moon really is invisible.

**The best-targets list on Tonight is empty.**
At high latitudes in summer the Sun may not get far enough below the horizon. The list also needs the deep-sky catalogue; the built-in one has only bright objects.

**The printed chart is too dark or wastes ink.**
Printing always uses a black-on-white palette, whatever the screen theme. Use the print preview to check before printing.

**The chart is mirrored compared with my eyepiece.**
Use **Mirror** for a star diagonal and **Invert** for a Newtonian. See [Sky chart](Sky-chart#mirror-and-invert).

## Equipment

**A button says "No telescope connected" (or camera, focuser, guider, dome).**
That action needs the named device and none of that kind is connected. Connect it in **Observatory → Equipment** (or click **Connect PHD2** for the guider), or click **Connect simulated observatory** to try the feature without hardware.

**My ASCOM device is not in the list.**
Install the ASCOM Platform and the driver, then click **ASCOM…** in the device's row to pick it in the ASCOM Chooser. If Sidralune says the platform is not installed, reinstall it from ascom-standards.org.

**Connecting fails.**
Close other programs that use the device (most drivers allow only one connection unless they are hubs), check the COM port in **Setup…**, and look in the log for the driver's error message. Try the simulator to confirm that Sidralune itself works.

**Alpaca discovery finds nothing.**
Make sure the Alpaca server is running, the computer is on the same network, and Windows Firewall allows UDP port 32227 for Sidralune. Some routers block broadcasts on Wi-Fi.

**PHD2 does not connect.**
In PHD2, choose **Tools → Enable Server**. If you run several PHD2 instances, the first uses port 4400.

**Autofocus fails or gives a poor V-curve.**
Use an exposure that shows at least 20 stars, start reasonably near focus, choose a step size that changes the HFR noticeably, and avoid nights with fast-changing seeing or thin cloud. Check the backlash setting of your focuser driver.

**Polar alignment reports a huge error.**
Each of the three frames must plate solve. Point at a part of the sky away from the pole with plenty of stars, and make sure the RA axis has room to rotate by the step three times without hitting anything.

## Plate solving

**Solving fails.**
See [When solving fails](Astrometry-and-framing#when-solving-fails). The most common causes are a wrong scale (focal length or pixel size), too few stars, and using the local catalogue for a narrow field.

**A blind solve takes minutes.**
Without hints the solver searches the whole sky. Enter a rough **Hint RA** / **Hint Dec** (a few degrees off is fine) or the **Scale**, and it usually solves in about a second.

**ASTAP is not found.**
Install ASTAP in its default folder (`C:\Program Files\astap`) together with one of its star databases.

**astrometry.net solving fails immediately.**
Enter your API key in **Settings → Observer & accounts**. Large files take a while to upload; the web service can also be busy.

## Stacking and processing

**Registration fails for many frames.**
Lower **Detection sigma** for sparse fields, or raise it for noisy frames. Check the frames themselves: clouds, trailing and bad focus make matching impossible. Calibrated frames register better, so add darks before registering.

**The stacked image has a strong colour cast or gradient.**
That is normal for linear data. Use **Process → Background extraction**, then **Background neutralization** and colour calibration.

**Hot pixels or "walking noise" remain.**
Use darks, enable **Hot pixel removal**, and dither between frames (the sequencer's **Dither every** setting). Use a rejection method such as WinsorizedSigma.

**Stacking runs out of memory.**
Use a 64-bit Windows, close other programs, and avoid drizzle on large sensors. Stacking streams frames from temporary files, so the frame count matters less than the frame size.

**Saved PNG looks different from the screen.**
PNG and JPEG are saved with the display stretch applied so that they match the screen. FITS, XISF and TIFF keep the actual data and look dark in programs that do not stretch.

## Planetary

**Pre-processing rejects almost every frame.**
Relax **Min object size fraction** / **Max object size fraction** and **Brightness tolerance**, check **Object mode** (use Surface for lunar and solar close-ups), or turn off **Reject edge frames** if the planet touches the frame edge.

**Lucky stacking produces a blurry or doubled image.**
Use smaller percentages (10–25%), a smaller **Ap size** for detailed targets or larger for poor seeing, and make sure **Align** matches the target (Planet for discs, Surface for close-ups).

**Derotation produces smeared edges.**
Check that the images' times are correct (FITS `DATE-OBS` or WinJUPOS file names in UTC), that **Image north angle** and **Mirrored** match your images, and that the planet is set correctly.

## Photometry, spectroscopy and radio

**Photometry skips frames.**
Frames need a `DATE-OBS` time and must plate solve (or already have a WCS). Check that the stars' coordinates are J2000 and that the comparison stars are in the field and not saturated.

**The light curve is very noisy.**
Use several comparison stars of similar brightness and colour, adjust the aperture (try **Fwhm multiple** around 1.5–2), calibrate with flats, and defocus slightly so bright stars are not saturated.

**Wavelength calibration fails.**
The approximate start and dispersion must be within about 10% of the truth for the lamp lines to be identified. Check that you chose the right **Lamp** and that blue is on the left.

**The radio spectrum is dominated by a spike in the middle.**
That is the receiver's DC offset; Sidralune suppresses the central channel. Offset your recording's centre frequency slightly (for example 1420.0 MHz instead of 1420.4) so the hydrogen line is away from the centre.

**rtl_tcp live capture fails.**
Start `rtl_tcp` first (it listens on 127.0.0.1:1234 by default), and make sure no other program uses the dongle.

## Online features

**Queries time out or fail.**
The online services (CDS, NASA, JPL, AAVSO, MPC) are run by research institutions and are occasionally down for maintenance. Try again later. All offline features keep working meanwhile.

**Does Sidralune send my data anywhere?**
Only when you use an online feature, and only what that query needs (a name, coordinates, an ADQL query, or, for astrometry.net, the image being solved). Nothing is collected in the background.

## Scripts

**My script does not compile although it is valid C#.**
Scripts are compiled as C# 5, so newer syntax such as string interpolation (`$"..."`), `?.` and `nameof` is not available. See [Language level](Scripting-and-data-analysis#language-level).

**Changing a control from a script throws an exception.**
Interface changes must run on the UI thread: wrap them in `MainForm.Instance.Invoke((Action)(() => { ... }))`.

## Getting help

If your problem is not listed, open an issue on [GitHub](https://github.com/pzyber/Sidralune/issues) with the details described in [CONTRIBUTING.md](https://github.com/pzyber/Sidralune/blob/main/CONTRIBUTING.md#reporting-a-bug).
