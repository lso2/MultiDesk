# Navigation trace

The trace records what MultiDesk decided at every point where a navigation fault can start, and then
reads back what actually happened. The value is attribution: a line says which guard answered an
Alt+Tab, what the window looked like when it was parked and what it looked like when it came back,
and which icon moved to which slot with nothing having asked for it.

## Files

Both files are written under `%APPDATA%\MultiDesk\logs`.

- **`diag-YYYYMMDD.log`** is the full chronological trace, one file per day, rolled aside at 4 MB and
  deleted after 14 days.
- **`bugs.log`** holds only the anomaly lines. It carries no day in its name and is rolled by size
  alone at 1 MB, keeping one previous generation as `bugs.1.log`. This is the file to open first.

Settings has a **Diagnostics** section with the recording toggle, a verbose toggle and a button that
opens the folder. Recording is on by default. The faults worth catching are intermittent, and an
intermittent fault is only on record if recording was already running when it happened.

## Reading a line

```
2026-09-15 08:14:22.413 [FG   ] handoff-previous-vanished  "Chrome - docs" hwnd=0x50A32 desktop=4 active=2 via=alt-tab state=visible | onscreen=0 parked-with=1 ...
```

| Column | Meaning |
| --- | --- |
| timestamp | Local time to the millisecond |
| `[FG   ]` | Channel |
| `handoff-...` | Event, or for a `BUG` line the anomaly kind |
| rest | Subject and measurements, `key=value` throughout |

`hwnd` values are hexadecimal. `desktop` is the desktop a window belongs to and `active` is the
desktop showing at the time. Rectangles print as `left,top widthxheight`.

## Channels

| Channel | Records |
| --- | --- |
| `NAV` | Desktop switches with their cause, visibility passes, the boot check |
| `FG` | Every foreground change, the decision it was given, and where focus settled afterward |
| `PARK` | Parks, restores, escalations and refusals |
| `GEOM` | A window's geometry when it left its desktop against its geometry when it came back |
| `ORDER` | Icon slots in the pane, and every change to them |
| `PANE` | Bar rectangle, thickness, grid columns and unused width |
| `TRACK` | Window discovery and removal, and repairs the heal pass made |
| `UPD` | Version readings and update checks |
| `BUG` | Anomalies, mirrored into `bugs.log` |

## Foreground outcomes

`SetForeground` reaches exactly one of these for every foreground change, and the name in the trace
is the name of the branch that decided it. The first two are recorded only when verbose tracing is
on, since they cover ordinary clicking.

| Outcome | The decision |
| --- | --- |
| `untracked` | The window is not one MultiDesk manages |
| `same-desktop` | The window is already on the desktop showing |
| `guard-revert` | A different window grabbed focus moments after focus was placed deliberately, and the placement was reasserted once |
| `self-parked-foreground` | MultiDesk parked this window under 500 ms ago while it held focus, so the event is that park's own echo |
| `handoff-after-park` | A park happened under 250 ms ago, so Windows picked this successor rather than a person |
| `handoff-previous-vanished` | The previous foreground window closed, hid itself or minimized, so this is Windows filling a dead spot |
| `breaker-cooldown` | The loop breaker is holding, and stuck events are answered by steering focus back |
| `breaker-tripped` | A fourth stuck-driven switch arrived inside one second, which starts the cooldown |
| `suppressed` | Focus-follow is suppressed after MultiDesk minimized, closed or parked something |
| `suppressed-steered-back` | The same, for a window that is off screen, whose focus is handed back to the active desktop |
| `follow-focus-off` | The window is on screen on another desktop and the follow-focus setting is off |
| `reunited-sibling` | Hide mode only: a second window of an app already on this desktop was brought here |
| `switch-guard` | A switch finished under 600 ms ago and is still settling |
| `switched` | The desktop was switched to this window's |

`via=alt-tab` on a line means the system task switcher held the foreground immediately before the
event. That is a window the person picked by hand, and it is what separates a real Alt+Tab commit
from a hand-off Windows performed on its own.

The classes that earn the label are `XamlExplorerHostIslandWindow`, `MultitaskingViewFrame`,
`TaskSwitcherWnd` and `TaskListThumbnailWnd`. `IsTaskChooser` in the same file is deliberately wider
and also lists `ForegroundStaging` and `Windows.UI.Core.CoreWindow`, since it decides an exemption
where a false positive is harmless. Ordinary UWP windows use `Windows.UI.Core.CoreWindow`, so the
trace does not take it as evidence of an Alt+Tab.

## Anomaly kinds

Every report reaches the trace. The `bugs.log` mirror is throttled instead, by kind and subject and
never by the measurements: the first 5 reports of a kind mirror, then at most one a minute. Throttling
on the measurements would have meant no throttling at all, since most detectors carry rectangles and
tick counts that move on every reading. Held-back counts are stated in the next mirrored line and
again at shutdown, and a `counter-reset` line is written if 512 distinct subjects are ever counted.

| Kind | What was measured |
| --- | --- |
| `alt-tab-commit-not-honored` | A commit arrived from the task switcher and, 550 ms later, the foreground was a different window, with the target still alive. The `outcome=` field names the decision that produced it |
| `focus-on-window-no-monitor-shows` | The foreground window is hidden, minimized, cloaked, or standing at a position that intersects no monitor. Keystrokes are going somewhere the user cannot see. Read from the window, never from its desktop index |
| `switch-focus-taken-by-another-process` | The desktop was switched to the window and focus was placed on it, and half a second later a different process held it |
| `switch-breaker-blocked-foreground` | The loop breaker answered a foreground change instead of honoring it |
| `desktop-switch-ping-pong` | 4 desktop switches inside 1.5 seconds |
| `desktop-changed-with-no-cause` | The active desktop differs from the last reading with no switch recorded |
| `maximized-returned-screen-sized` | A window left maximized came back filling the work area with the maximized state gone |
| `fullscreen-returned-screen-sized` | A window left covering the whole monitor came back at work-area size |
| `maximized-returned-normal` | A window left maximized came back at an ordinary size |
| `fullscreen-returned-<state>` | A window left fullscreen came back in some other state |
| `coverage-changed` | The amount of its monitor a window covers differs from what it left with |
| `rect-changed` | An ordinary window came back at a different rectangle |
| `monitor-changed` | A window came back on a different monitor |
| `restored-minimized` | A window that was not minimized came back minimized |
| `icon-order-changed-with-no-cause` | Icon slots differ from the previous reading with nothing having declared a reorder |
| `pane-resized-with-no-cause` | The bar's rectangle or thickness differs with nothing having declared a resize |
| `icon-grid-leaves-unused-width` | The icon grid has more than a quarter of a cell of width beyond its last whole column. Measured once the bar has settled, and not written again while the number is unchanged |
| `window-on-screen-off-its-desktop` | The heal pass found a window standing on screen that belongs to another desktop. Reported at most once per window per 10 seconds, since a window this process cannot move stays in that state permanently |
| `park-defeated` | A park mechanism reported success and the window did not move, or reverted it afterward |
| `park-refused` | Windows refused the move outright, which is what an elevated window does to a non-elevated process |

## Finding a specific fault

**An Alt+Tab that landed on the wrong window.** Open `bugs.log` and look for
`alt-tab-commit-not-honored`. The `outcome=` field on that line names which of the 14 branches above
decided it, and the full trace at the same timestamp carries the readings that branch saw. The kind
is reported only when focus ended up where MultiDesk itself sent it, so a notification or a click
taking focus during the settle window is recorded in the trace as
`focus-moved-on-by-something-else` and does not reach `bugs.log`.

**Alt+Tab to another desktop that did not switch.** Search `bugs.log` for
`focus-on-window-no-monitor-shows`. The line carries the window's state and its rectangle. If the
switch was refused instead, the matching `FG` line in the trace names the guard.

**A maximized window that came back almost full screen.** Search `bugs.log` for
`maximized-returned-screen-sized` or `fullscreen-returned-screen-sized`. The line carries both
readings side by side: `left` is the window as it was parked, `back` is the window once it had
settled for 450 ms after the restore, each with its rectangle, its coverage, its `WINDOWPLACEMENT`
show state and its monitor work area. `level=` is the park mechanism that held it, 0 for a position
move, 1 for a placement change, 2 for a minimize.

**An icon that moved on its own.** Search for `icon-order-changed-with-no-cause`. The line names every
icon that moved, as `"Title" d0#3->d1#0`, where `d` is the desktop and `#` is the slot. Compare
against the `ORDER reordered` lines nearby, which carry a `cause=` for every move that was asked
for.

**The pane resizing on its own.** Search for `pane-resized-with-no-cause`. The line carries the
rectangle and thickness before and after, and the most recent declared cause with its age, so a
resize that arrived just outside the attribution window is distinguishable from one that had no
cause at all.

**Empty space to the right of the icons.** Search for `icon-grid-leaves-unused-width`. The line
carries the column count, the measured cell width, the viewport width, the unused width in both
device-independent and device pixels, the current thickness, and the thickness that would leave no
gap.

## Cause attribution

Deliberate changes announce themselves before they happen, and a watcher compares the live state
against the previous reading on the tracker's half-second heartbeat. A difference with a declared
cause is recorded as a normal event. A difference with nothing declared is an anomaly.

Declared causes last 2.5 seconds. Calls arriving inside that window extend the cause into a chain
joined with `>`, so a resize that passed through several layers reads as
`settings-changed>dock-apply>resize-live`. The chain keeps the 4 newest causes and drops the oldest,
which is what keeps it from reaching a length at which further causes stop being recorded.

Pane readings arrive on every frame of a drag. They are held and evaluated 400 ms after the last one,
so one drag produces one line instead of several hundred.

Order causes: `window-discovered`, `window-removed`, `dragged-onto-tile`, `moved-to-desktop-N`,
`sibling-reunite`, `desktop-added`, `desktop-removed-N`, `desktop-reordered`, `settings-reload`.

Pane causes: `whole-column-snap`, `startup-column-snap`, `thickness-slider`, `edge-grip-drag`,
`section-grip-drag`, `settings-changed`, `dock-apply`, `resize-live`, `resize-committed`,
`auto-hide-expand`, `auto-hide-collapse`, `appbar-reserved-position`, `float-position`, `float-drag`.

## Coverage

`DiagWindow.Coverage` answers how much of its monitor a window occupies and by what means. The four
answers are states a person can tell apart on sight.

- **`maximized`** is `IsZoomed`, the state Windows itself maintains.
- **`fullscreen`** covers the whole monitor rectangle within 8 pixels without being zoomed.
- **`screen-sized`** covers the work area within 8 pixels without being zoomed. This is the state
  nobody asks for: the window fills the screen while Windows still treats it as an ordinary
  resizable window.
- **`normal`** is everything else.

The 8 pixel tolerance absorbs shadow borders and DPI rounding, which routinely account for a few
pixels on a genuinely maximized window.

## Channel manifest and the boot check

`DiagChannels.All` is the full channel list and `DiagChannels.Watched` is the subset a watcher owns.
Each watcher calls `Arm` once it is running, and `BootCheck` writes one `NAV boot-check` line naming
the armed and unarmed channels. A channel that never armed writes nothing and would otherwise look
exactly like a channel with nothing to report, so the unarmed list also goes to `debug.log`.

`BUG` is in `All` and not in `Watched`. Every detector writes to it, so it is live exactly when one of
the watched channels is.

## Files in the set

| File | Job |
| --- | --- |
| `Diag.cs` | Writes lines, opens the day's file, rolls it at 4 MB, prunes past 14 days |
| `DiagChannels.cs` | Channel names, the manifest, the arming record and the boot check |
| `DiagFormat.cs` | Handles, rectangles, show states, live flags, monitor rectangles, quoted titles |
| `DiagSession.cs` | Session banner and close, with the version, the screen layout and the settings that change navigation |
| `DiagBug.cs` | The one anomaly path, with repeat counting and the `bugs.log` mirror |
| `DiagWindow.cs` | One reading of a window, the coverage answer, and the differences between two readings |
| `DiagNav.cs` | Foreground decisions, desktop switches, the ping-pong detector, the outcome vocabulary |
| `DiagNavLanding.cs` | The verdict, read 550 ms after a decision, on where focus actually went |
| `DiagPark.cs` | Park and restore events, and the fidelity check queued 450 ms after a restore |
| `DiagOrder.cs` | Icon slots, cause attribution, and the named diff between two readings |
| `DiagPane.cs` | Bar geometry, cause attribution, and the unused width beyond the last whole column |

## Settle times

Both read-back checks wait, because an app rebuilds its own geometry and focus over several frames.

| Check | Wait |
| --- | --- |
| Where focus landed | 550 ms after the decision |
| Whether a restore was faithful | 450 ms after the restore |
| Icon order and active desktop | The tracker's 500 ms heartbeat |
| Pane geometry | 400 ms after the bar stops changing size or position |

## What a landing check will not blame on MultiDesk

550 ms is long enough for other things to move focus, so three readings are taken before an anomaly
is raised.

- The window that ended up focused belonging to the **same process** as the one the user picked is an
  app opening its own dialog, picker or tool panel. Recorded as `landed-on-sibling-window`.
- Focus resting anywhere **other than where MultiDesk sent it** means something else took it.
  Recorded as `focus-moved-on-by-something-else`, with the handle MultiDesk steered to on the line.
- The window still being **visible on a monitor** means only that MultiDesk's bookkeeping puts it on
  another desktop, which the heal pass settles. That is not a fault of its own.

## Verification performed

The writer, the anomaly counter and its throttle, the order diff, the cause chain, the pane settle
and slack math, the coverage and restore-fault comparisons, the no-monitor reading, the channel
manifest and the version parser were compiled from these same source files into a console harness and
run on 2026-09-14. All 51 checks passed. The update check and its schedule were run the same way
against the live GitHub releases API, 18 checks, which is what found that this project's release tags
are written as `MultiDeskSetup-2.9.11-release` and that the first version of the parser rejected
them.

The park, unpark, heal and foreground code paths were compiled and inspected, not executed, since
running them means restarting the app.

A review of the finished change set found 12 defects, all fixed before this note was written. The
three that mattered: the update check fired on every settings save, and placements save every 45
seconds, which would have exceeded the GitHub rate limit within the first hour; one bar resize drag
wrote a line per mouse-move frame; and a window this process cannot move logged about 6 lines a
second for as long as it existed, building those lines even with the trace switched off.
