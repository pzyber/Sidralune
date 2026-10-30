Sidralune uses one main window for everything. This page explains its parts and the conventions shared by every workspace.

## Contents

- [Window layout](#window-layout)
- [Navigation rail](#navigation-rail)
- [Top bar](#top-bar)
- [Command palette](#command-palette)
- [Time](#time)
- [The selected object](#the-selected-object)
- [Status bar](#status-bar)
- [Notifications](#notifications)
- [Background tasks](#background-tasks)
- [Night vision mode](#night-vision-mode)
- [Common controls](#common-controls)
- [Design principles](#design-principles)

## Window layout

```
┌──────────────────────────────────────────────────────────────────────────┐
│ Sidralune   Workspace name     [ Search objects and commands… Ctrl+K ]    │  top bar
│                                         Site  date time  LST   Live  ☾   │
├────┬─────────────────────────────────────────────────────────────────────┤
│ ⌂  │                                                                     │
│ ◎  │                                                                     │
│ ▦  │                    the current workspace                            │
│    │          (tabs across the top, side panels, charts, grids)          │
│ …  │                                                                     │
│ ⚙  │                                                                     │
├────┴─────────────────────────────────────────────────────────────────────┤
│ ● Telescope ● Camera ● Focuser ● FilterWheel ● Guider ● Dome  ● data  task │  status bar
└──────────────────────────────────────────────────────────────────────────┘
```

Sidralune remembers the workspace you were using and opens it again next time. Window contents scale with Windows' display scaling, including per-monitor scaling when you move the window between screens.

## Navigation rail

The rail on the left lists the workspaces, grouped by task:

| Group | Workspaces |
|---|---|
| Plan | Tonight, Sky, Planner |
| Acquire | Observatory |
| Process | Deep sky, Process, Planetary |
| Science | Astrometry, Photometry, Spectra, Solar sys., Radio |
| Tools | Databases, Scripts, Settings |

Ctrl+1 to Ctrl+9 open the first nine workspaces (Tonight to Photometry) in that order. When the window is short, the rail shrinks its buttons to fit and shows icons only; hover over an icon to see the workspace name. Every workspace stays reachable at the smallest window size.

Each workspace is created the first time you open it, so start-up stays fast.

## Top bar

From left to right:

- The Sidralune logo and the name of the current workspace.
- The search box. Clicking it opens the [command palette](#command-palette). In a narrow window it moves left and gets narrower so it never covers the clock.
- The clock: site name, local date and time, and local sidereal time (LST). It is grey when it shows the present and amber when you are looking at another time.
- **Live**, shown only when you are away from the present. It returns to the current time.
- The moon button, which switches [night vision mode](#night-vision-mode).

## Command palette

Press **Ctrl+K** (or Ctrl+F, or Ctrl+Shift+P) anywhere, or click the search box. Type a few letters:

- **Objects**: star names (`Vega`), catalogue designations (`M 31`, `NGC 7000`, `IC 434`, `HIP 91262`, and with the PGC galaxies installed `PGC 2557` or another galaxy name such as `UGC 454`), planets (`Saturn`), comets and asteroids (`Ceres`, `12P`). Choosing one selects it and centres it in the Sky chart.
- **Coordinates** are not searched in the palette, but the find box on the **Sky** toolbar accepts `hh mm ss +dd mm ss` and centres the chart on that position.
- **Commands**: every important action of every workspace, for example `stack`, `print finder chart`, `polar alignment`, `new observing list`, `night`, `+1 day`. Each entry shows its category and keyboard shortcut.

Use the arrow keys to move through the results, **Enter** (or a double-click) to run and **Esc** to close. Matching is forgiving: names that start with what you typed rank first, then names that contain it, then names that contain its letters in order, so `plal` finds "polar alignment".

## Time

Every calculation uses a single, shared time: the clock in the top bar. By default it follows the real time.

To look at another moment:

- Type a date and time (your site's local time) in the date field on the **Sky** toolbar.
- Use the step buttons on the Sky toolbar: back one day, back one hour, **Now**, forward one hour, forward one day.
- Press the play button on the Sky toolbar to animate time at 300 times normal speed.
- Use the command palette: `Time: +1 hour`, `Time: -1 hour`, `Time: +1 day`, `Return to live time`.

All workspaces update when the time changes: the Tonight page, the planner, ephemerides, the observatory's target altitudes and so on.

## The selected object

Clicking an object in a chart, a grid row, or a search result makes it the selected object. Many workspaces use it as a starting point: the Planner adds it to a list, Astrometry frames it, Photometry adds it as a target, Radio uses it as the antenna pointing, and the Scripts API exposes it as `Selection`.

## Status bar

- **Device pills** for the telescope, camera, focuser, filter wheel, guider and dome. A faint pill means no device of that kind is configured, green means connected and red means configured but disconnected. The pill text also shows extra state: slewing, parked or tracking for the mount, the sensor temperature for the camera, and the guiding state for the guider. Click a pill to open the Observatory.
- **Data pill**: which ephemeris and catalogues are loaded, for example "DE440 · HYG (119627 stars) · OpenNGC (13380 objects)". Click it to open **Settings → Data**.
- **Task pill**: the background task that is running, with its progress and status. "(+2)" means two more tasks are queued or running.
- The latest warning or status message, on the right.
- **Log**: shows or hides the log panel, which lists informational messages, warnings and errors with their source. The same messages are written to `%LOCALAPPDATA%\Sidralune\Logs`.

When the window is too narrow for everything, the data pill and the task pill are shortened first, then the message; shortened texts end in "…". Hover over a shortened item to see its full text.

## Notifications

Results and problems are shown as short "toasts" in the corner of the window instead of dialog boxes, so they never interrupt your work. Successes are green, warnings amber and errors red. Dialog boxes are only used to confirm something that cannot be undone, such as deleting an observing list.

If Sidralune hits an unexpected error, it keeps running, shows a toast and writes a crash report to the Logs folder. Please include that report when you [report a bug](https://github.com/pzyber/Sidralune/blob/main/CONTRIBUTING.md#reporting-a-bug).

## Background tasks

Anything that takes more than a moment (opening images and videos, loading catalogues, stacking, plate solving, image processing, period searches, online queries, downloads, scripts) runs in the background. The window stays responsive, the task pill shows progress, and a toast appears when the task finishes. You can keep working in other workspaces meanwhile. Tasks that control equipment can be stopped with their **Stop** buttons or with the `Observatory: abort all` command.

A task that works on something you could change, or that you start with a button, runs one at a time: while a plate solve, a processing step, a stacking run, a pre-processing or lucky-stacking run, a photometry measurement, a data download, an online database query or a Solar system calculation (an ephemeris, minor body positions, Galilean moon events, occultations, satellite passes) is busy, the buttons that would start another one or change its input (for example **Open image…** and **Solve** during a plate solve) are greyed out, and a message says what is still running. They come back when the task finishes or is stopped (a photometry measurement has its own **Stop** button). Recalculations that follow what you change (Tonight, the planner, previews) never wait: a new one replaces the one still running, so you always see the result of the latest one. The replaced calculation is stopped first, so changing things quickly never leaves several running at once.

## Night vision mode

Night vision mode replaces every colour with deep reds on black, so the screen does not ruin your dark adaptation at the telescope. Switch it with the moon button, **Ctrl+Shift+N**, **Settings → Appearance**, or the `Toggle night vision mode` command. The moon button and the setting always show the same state, and the choice is remembered.

Everything follows the switch straight away, including scroll bars, drop-down lists, selected text, plots, chart overlays and plate-solve labels. Images (camera frames, stacks, plate-solving images) are shown as their brightness in red, at the same brightness as in the dark theme.

For the best result, also turn down your screen's brightness: a red screen at full brightness still affects dark adaptation.

## Common controls

| Control | Behaviour |
|---|---|
| Tabs | Most workspaces are split into tabs across the top, in the order you would normally use them |
| Toolbars | Buttons above a workspace. In a narrow window they wrap onto a second row instead of being cut off |
| Side panels | Panels beside charts and images scroll when the window is too short for their contents |
| Cards | Panels with a title, grouping related settings. Long cards scroll |
| Collapsible sections | In the Process workspace, click a section heading to expand or collapse it |
| Number boxes | Type a value, use the arrows, or scroll with the mouse wheel |
| Property grids | Option lists (stacking settings, pre-processing, sequence instructions) are edited in a two-column grid of names and values. Option names are split into words, and nested option groups can be expanded |
| Data grids | Click a column heading to sort (numbers such as magnitudes, angles such as RA and Dec, and times sort by value; empty values come last), double-click a row to open or select it, Ctrl+click or Shift+click to select several rows |
| Image viewers | Mouse wheel zooms around the cursor, drag pans, **0** fits the image, **1** shows actual pixels. The readout under the image shows pixel coordinates, value and, for plate-solved images, RA and Dec |
| Plots | Drag a box to zoom in and double-click to reset. In plots that select ranges (light curves, spectra) dragging selects the range instead. Right-click for **Reset zoom**, **Copy image**, **Save as PNG…** and **Export data as CSV…** |
| Drag and drop | Image viewers accept files dropped from Explorer |

## Design principles

The interface follows established usability research:

- **One workspace per task**, ordered like an observing night: plan, acquire, process, analyse.
- **Progressive disclosure**: common actions are visible, advanced options sit in collapsible sections and property grids.
- **Recognition over recall**: the command palette finds any object or action by name.
- **Non-blocking feedback**: background tasks and toasts instead of modal dialogs and frozen windows.
- **Safe defaults**: nothing is downloaded or moved without you asking, and destructive actions ask for confirmation.
- **Dark first**: a dark theme by default and a true red night-vision theme for use at the telescope.
