The sequencer, in **Observatory → Sequencer**, runs a whole imaging night automatically: cooling the camera, waiting for darkness, slewing and centring on targets, focusing, guiding, taking exposures through different filters, flipping at the meridian, reacting to bad weather, and parking at dawn.

A sequence is a tree of instructions. Containers group instructions and can repeat them; triggers watch for conditions between exposures and step in when needed.

> Test every new sequence with the [simulated observatory](Observatory-and-devices#the-simulated-observatory) first, and stay with your equipment the first time you run it for real.

## Contents

- [The sequencer tab](#the-sequencer-tab)
- [Building a sequence](#building-a-sequence)
- [The template](#the-template)
- [Containers](#containers)
- [Instructions](#instructions)
- [Triggers](#triggers)
- [Running a sequence](#running-a-sequence)
- [Saving and loading](#saving-and-loading)
- [Example: LRGB night with two targets](#example-lrgb-night-with-two-targets)

## The sequencer tab

| Area | Contents |
|---|---|
| Toolbar | **Add**, **Remove**, **Up**, **Down**, **Template…**, **Open…**, **Save…**, **Run sequence**, **Stop** |
| Left | The sequence tree |
| **Properties** (right) | The settings of the selected instruction |
| **Triggers (checked between exposures)** | The triggers, each with a tick box to enable or disable it |
| Bottom | The log of the running sequence, with time stamps |

Each instruction in the tree shows its status: ▶ running, ✓ done, ✗ failed, – skipped, and "(disabled)" if you switched it off.

## Building a sequence

1. Select the container where the new instruction should go (or an instruction after which it should go).
2. Click **Add** and choose the instruction type from the menu.
3. Edit its settings in **Properties**.
4. Use **Up** and **Down** to reorder, **Remove** to delete.

Every instruction has an **Enabled** property. Disabling an instruction keeps it in the sequence but skips it, which is handy for testing. **Stop on error** decides what happens if the instruction fails (see below).

## The template

**Template…** builds a typical deep-sky night for the selected object (or M 31 if nothing is selected):

```
Night of 2026-10-18
├── Cool camera              −10 °C over 5 min
├── Unpark telescope
├── Wait                     until astronomical dusk
├── Target: M 31
│   ├── Wait                 until target altitude > 25°
│   ├── Slew, plate solve & center
│   ├── Autofocus
│   ├── Start guiding
│   ├── Loop (UntilAltitudeBelow)   alt < 20°
│   │   └── Expose light frames     10 × 180 s, filter L
│   └── Stop guiding
├── Park telescope
└── Warm camera              over 5 min
```

Adjust it to your needs instead of starting from scratch.

## Containers

| Container | Behaviour | Properties |
|---|---|---|
| **Sequence container** | Runs its children in order | Name |
| **Target container** | Runs its children with a target. Slew, centre, meridian flip and altitude conditions inside use this target | Target name, RA and Dec (J2000), optional rotation |
| **Loop container** | Repeats its children until its condition ends | **Condition**: Count (a number of **Iterations**), UntilTime (**Until utc**), UntilAltitudeBelow (target below **Altitude deg**), UntilAstronomicalDawn, or Forever (until stopped) |

Containers can be nested, for example a loop of filters inside a target inside a loop that repeats every target.

## Instructions

| Instruction | What it does | Main properties |
|---|---|---|
| **Slew to target** | Slews to the current target's apparent position | |
| **Center target** ("Slew, plate solve & center") | Slews, plate-solves, syncs and re-slews until the target is centred | **Tolerance arcmin** (1), **Exposure** (5 s), **Bin** (2) |
| **Start guiding** | Starts PHD2 guiding | **Force calibration** |
| **Stop guiding** | Stops guiding | |
| **Take exposures** ("Expose … frames") | Takes a series of frames and saves them | **Count**, **Exposure**, **Filter**, **Bin**, **Gain** and **Offset** (−1 keeps the camera's value), **Frame type** (chosen from a list: LIGHT, DARK, FLAT, BIAS), **Dither every** N frames, **Dither pixels** |
| **Record video** | Records a [planetary SER video](Observatory-and-devices#planetary-video), named after the target and saved under `Planetary\<target>\` | **Exposure ms**, **Region** (centred on the sensor, the same choices as in the Imaging tab: **Full sensor** or 1920 × 1080 down to 320 × 240), **Bin**, **Gain** and **Offset** (−1 keeps the camera's value), **Filter**, **Max frames**, **Max seconds** (stops at whichever comes first) |
| **Run autofocus** | Runs the V-curve autofocus routine | **Exposure**, **Step size**, **Steps** |
| **Switch filter** | Moves the filter wheel | **Filter** name, chosen from the wheel's filters |
| **Cool camera** / **Warm camera** | Ramps the sensor temperature gradually | **Temperature**, **Minutes**, **Warm** |
| **Wait** | Pauses | **Kind**: Seconds, UntilTime, UntilAltitude (target above **Altitude deg**), UntilAstronomicalDusk, UntilNauticalDusk, UntilSafe (safety monitor reports safe) |
| **Park telescope** / **Unpark telescope** | Parks or unparks the mount | **Unpark** |
| **Dome shutter** ("Open dome" / "Close dome") | Opens or closes the dome shutter or roof | **Open** |
| **Run script** ("Run external program") | Runs a program or batch file, for example to switch dew heaters or send a notification | **Command** (click **…** to choose the file), **Arguments**, **Timeout sec** |
| **Annotation** ("Note") | Writes a note to the log; does nothing else | **Text** |

The **Filter** of an instruction is chosen from the connected filter wheel's filters (the simulator's L, R, G, B, Ha, OIII and SII when no wheel is connected), and another name can still be typed for a wheel that is not connected while you edit. In **Take exposures** and **Record video** the empty choice keeps the current filter.

For a planetary rotation series, put **Record video** in a **Loop** with a **Wait** after it, for example a 90-second video every 5 minutes until the planet sinks below 25°. With a filter wheel, use one **Record video** per filter (R, G, B) in the loop. The videos are named in the WinJUPOS style, so the [Derotation](Planetary,-lunar-and-solar-imaging#derotation) tab can combine them later.

If an instruction fails, it is marked ✗, the error is shown in its properties and written to the log, and the sequence continues with the next instruction. Turn on **Stop on error** for instructions that must succeed (for example centring before a long series): when one fails, the rest of the container it is in is skipped (the rest of that target, say), and the sequence continues after that container, so a closing **Park** or **Close dome** still runs. An instruction directly in the sequence's top level ends the whole sequence.

A loop whose instructions all fail in one pass (for example because the camera stopped responding) stops instead of repeating the failure: it is marked ✗ with "Loop stopped: no instruction in it succeeded", and the sequence continues after the loop, so a closing **Park** or **Close dome** still runs.

## Triggers

Triggers are checked between exposures. Tick the ones you want in the **Triggers** card:

| Trigger | When it fires | What it does |
|---|---|---|
| **Park and close when unsafe** | The safety monitor reports unsafe (rain, clouds, wind) | Stops guiding, parks the mount, closes the dome, waits until conditions are safe again (checking every 30 s), then reopens, unparks and continues |
| **Meridian flip 5 min after transit** | A German equatorial mount on the east side of the pier is tracking a target that passed the meridian 5 minutes ago | Stops guiding, re-slews to the target so the mount flips to the west side, re-centres by plate solving and resumes guiding |
| **Autofocus when HFR rises 15%** | The median half-flux radius of the last 5 frames is 15% above the value measured at the last autofocus | Runs autofocus |

The sequencing engine also provides **Autofocus after 1.5 °C change** (temperature-compensating refocus) and **Autofocus every 30 exposures**. They are available to [scripts](Scripting-and-data-analysis) through `Sidralune.Devices.Automation`.

## Running a sequence

Click **Run sequence**. The sequence runs in the background: you can keep working in other workspaces, and the task pill in the status bar shows its progress. The tree marks each instruction's status and the log records every step with its time.

Before it starts, Sidralune checks that the devices the enabled instructions need are connected: a camera for exposures, videos, centring and autofocus, a telescope for slewing, centring and parking, a focuser for autofocus, and a filter wheel for **Switch filter**. If one is missing, the sequence does not start and a message names what to connect. Guiding, cooling and dome instructions are skipped when their device is absent, so they do not block a run.

Each frame the sequence takes is shown in the [Imaging tab](Observatory-and-devices#camera) as it arrives, with its star count, HFR, FWHM and file name. Videos are shown there too while they record, with the frame count and frame rate, and **Open in Planetary** opens the last one. Stopping the sequence during a video ends the recording and keeps the frames so far. While the sequence runs, everything else that uses the camera is disabled so that nothing can fight over it: **Start** and **Loop** in the Imaging tab, **Run autofocus**, **Center**, and **Start polar alignment**. The sequence's own autofocus and centring instructions are not affected. If a capture, preview loop, autofocus run, centring or polar alignment is already running, **Run sequence** is disabled until it has finished or you stop it.

Click **Stop**, or run the `Observatory: abort all` command, to stop. The current exposure is aborted and the mount stops slewing; it does not park automatically, so park it yourself if you are ending the night.

Frames are saved with the [file name pattern](Observatory-and-devices#file-names) from the Imaging tab, under the image folder from **Settings → Observer & accounts**.

## Saving and loading

**Save…** writes the sequence tree to a `.json` file, and **Open…** loads one. The file is plain JSON, so you can also generate sequences with a script or edit them in a text editor. Trigger choices are not stored in the file; set them before you run.

## Example: LRGB night with two targets

```
Night
├── Cool camera              −10 °C, 10 min
├── Unpark telescope
├── Wait                     until astronomical dusk
├── Target: NGC 891
│   ├── Wait                 until altitude > 30°
│   ├── Center target
│   ├── Switch filter        L
│   ├── Run autofocus
│   ├── Start guiding
│   ├── Loop                 Count, 3 iterations
│   │   ├── Switch filter    L
│   │   ├── Take exposures   12 × 120 s, L, dither every 2
│   │   ├── Switch filter    R
│   │   ├── Take exposures   4 × 120 s, R
│   │   ├── Switch filter    G
│   │   ├── Take exposures   4 × 120 s, G
│   │   ├── Switch filter    B
│   │   └── Take exposures   4 × 120 s, B
│   └── Stop guiding
├── Target: M 33
│   ├── Center target
│   ├── Start guiding
│   ├── Loop                 UntilAstronomicalDawn
│   │   └── Take exposures   10 × 300 s, Ha
│   └── Stop guiding
├── Park telescope
├── Close dome
└── Warm camera              10 min
```

With the safety, meridian flip and HFR autofocus triggers enabled, this runs unattended from dusk to dawn.
