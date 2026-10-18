# Command-line options

Sidralune is normally started without arguments. For documentation, automated visual checks and demonstrations it has a **screenshot mode**, which opens a workspace in a chosen state, renders the window to a PNG file and exits.

## Contents

- [Synopsis](#synopsis)
- [Options](#options)
- [Workspace keys](#workspace-keys)
- [Examples](#examples)
- [Driving the interface with a script](#driving-the-interface-with-a-script)
- [Notes](#notes)

## Synopsis

```
Sidralune.exe --screenshot <file.png> [--view <key>] [--time <ISO 8601>] [--sim] [--night]
              [--size <W>x<H>] [--wait <seconds>] [--script <file.cs>]
```

## Options

| Option | Meaning | Default |
|---|---|---|
| `--screenshot <file>` | Turns on screenshot mode and names the output PNG file | |
| `--view <key>` | The workspace to show (see below) | `home` |
| `--time <ISO 8601>` | Sets the simulated time, in UTC, for example `2026-12-14T22:00:00` | Live time |
| `--sim` | Connects the simulated observatory before showing the view | Off |
| `--night` | Uses the red night-vision palette | Off |
| `--size <W>x<H>` | Window size in pixels | `1500x950` |
| `--wait <seconds>` | How long to let the view load and settle before capturing | `4` |
| `--script <file.cs>` | Runs a C# script in the Scripts workspace before capturing (see below) | |

In screenshot mode the window is created off-screen and not shown in the taskbar, catalogues are loaded synchronously, and your normal settings (sites, equipment) are used. The window is rendered and saved after the wait time, then Sidralune exits.

`--script` only takes effect together with `--screenshot`.

## Workspace keys

| Key | Workspace |
|---|---|
| `home` | Tonight |
| `sky` | Sky |
| `planner` | Planner |
| `observatory` | Observatory |
| `stacking` | Deep sky |
| `processing` | Process |
| `planetary` | Planetary |
| `astrometry` | Astrometry |
| `photometry` | Photometry |
| `spectro` | Spectra |
| `solarsystem` | Solar sys. |
| `radio` | Radio |
| `databases` | Databases |
| `scripting` | Scripts |
| `settings` | Settings |

## Examples

The Tonight page for the Geminids peak, in night-vision mode:

```
Sidralune.exe --screenshot geminids.png --view home --time 2026-12-14T02:00:00 --night
```

The sky chart at a larger size:

```
Sidralune.exe --screenshot sky.png --view sky --size 1920x1080 --wait 6
```

The observatory with the simulated equipment connected:

```
Sidralune.exe --screenshot observatory.png --view observatory --sim
```

## Driving the interface with a script

`--script` runs a C# script (the same kind as in the [Scripts workspace](Scripting-and-Data-Analysis.md)) before the screenshot is taken. The script can call into the views through `MainForm.Instance` to put them in a particular state. Interface changes must run on the UI thread:

```csharp
var mf = MainForm.Instance;
mf.Invoke((Action)(() =>
{
    mf.Navigate("solarsystem");
    // select a tab, fill fields, start a calculation...
}));
```

Combined with `--wait`, this makes repeatable screenshots of any workspace, and can serve as an end-to-end test: the screenshot shows the script's output and the state of the interface. Contributors use this to check interface changes; see [CONTRIBUTING.md](../CONTRIBUTING.md#checking-the-interface).

## Notes

- Paths with spaces must be quoted.
- The exit code is 0 on success. Errors are written to the log in `%LOCALAPPDATA%\Sidralune\Logs`.
- Screenshot mode uses and updates your normal settings: like a normal exit, closing the window remembers the last workspace and disconnects devices.
