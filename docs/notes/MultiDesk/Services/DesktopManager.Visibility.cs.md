# Visibility engine

`MultiDesk\Services\DesktopManager.Visibility.cs`. This is the prose that used to sit at the top of
that file, moved here when the file passed 27 KB.

## The invariant

Every window not on the active desktop must never be seen on screen, under any circumstance. Nothing
in the engine relaxes it. How it is enforced depends on the **AltTabAllDesktops** setting.

**Off** is a real hide, `SW_HIDE`, the mechanism from before 2.9.2. Nothing can draw a hidden window,
and the system Alt+Tab shows only the active desktop, like plain Windows.

**On** parks windows instead of hiding them. A parked window stays `WS_VISIBLE`, relocated far below
its own monitor's column, which is what lets the real, unmodified Windows Alt+Tab list every
desktop's windows with live thumbnails and nothing drawn by MultiDesk.

Parking below the window's **own** column matters. The nearest monitor to the park spot is then the
window's own, so per-monitor-DPI windows receive no DPI change while parked. A park spot beside a
different-DPI monitor used to return portrait windows stretched to the wrong proportions.

## The ladder

Some windows defeat a plain position move. A window can clamp the move to nothing inside its own
message handling while the call reports success, and maximized or fullscreen browser windows, which
re-derive their geometry from their monitor, can also revert an off-screen placement later. So every
park is verified against the real on-screen rectangle, and each window earns its own mechanism,
escalating only by defeating the level below.

| Level | Mechanism |
| --- | --- |
| 0 | A pure position move, size and state untouched |
| 1 | One atomic placement change that restores the window to normal state directly into the off-screen spot, never visible un-maximized on screen, and re-maximizes it onto its own monitor in one call on return. The window stays `WS_VISIBLE` and live in Alt+Tab throughout |
| 2 | A minimize with that window's DWM animation suppressed: invisible like everything minimized, still listed in the real Alt+Tab, and restored natively on return, where Windows itself rebuilds maximized or fullscreen geometry from the monitor, exactly |

No level removes a window from the switcher. Every escalation is written to the trace and to
`bugs.log` as `park-defeated`, with the window's title and both readings.

## Edge cases the engine handles

- A window found minimized is left alone, already off screen and correct in Alt+Tab.
- A window minimized while parked gets its remembered restore rectangle repaired in place, staying
  minimized, so un-minimizing later lands at its true position.
- Hung windows are skipped. The heal pass retries once they answer.
- A parked window's way back is held only in this process's memory. A window found standing at the
  park spot with no capture to its name, left by a session that was killed, is given a centered
  position at its own size instead of being left stranded.
- A park Windows refuses outright is an elevated window no non-elevated process can manage, under
  this or any other mechanism. It is recorded once as `park-refused`.

## Restore fidelity

`Services\Diag\DiagPark.cs` keeps the reading taken at park time and compares it against a reading
taken 450 ms after the restore. A window that comes back covering a different amount of its monitor,
or the same amount by a different means, is reported. See `docs/notes/MultiDesk/Services/Diag.md`.
