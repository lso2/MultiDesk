# Changelog
All notable changes to MultiDesk are recorded here. The major number marks a structural shift, the minor number marks a feature set, and the patch number marks fixes. The newest release is listed first.

## [2.9.2] - 2026-07-07
### Fixed
- Sidebar no longer snaps back to one column after toggling a settings checkbox. The bar is resized natively, so WPF's cached window size went stale at the startup width and re-asserted itself; the cache now tracks the real size.
- All-desktops Alt+Tab: a media window (YouTube was the reproducer) could steal focus after a switch or hijack the switch entirely. Foreground grabs that happen while the switcher is still up are never accepted as the selection, and one unrequested grab right after a deliberate focus placement is reverted.
- All-desktops Alt+Tab: a quick flick could land back on the starting window while the real selection activated invisibly. The commit now waits for the foreground to genuinely move, and a selection event that outruns the Alt release is recognized by its timing.
- Rapid alt-tab toggling: a new gesture now resolves the previous pending commit first instead of racing its backstop timer.
- Rapid alt-tab could lock onto the window being left instead of the selection. Dismissing the switcher makes foreground hop back to the starting window for a moment before landing on the selection; the commit fired on that hop and then defended it against the real choice. Foreground events for the starting window are no longer accepted as the selection.
- Switching to another desktop could leave a different window covering the selection: the unpark pass raised each of the target desktop's windows to the top after the OS had already raised the chosen one, so the last icon's window buried it while focus stayed on the buried window. The batch now ends by raising whichever window holds focus.
- Cross-desktop activation in single mode: activating a single-instance app whose window sits on another desktop now switches there and raises the window instead of appearing to do nothing.
- Dragging a bar icon forward (right or down) reordered it one slot early or not at all; icons now land exactly in the slot they are dropped on, in both directions and across desktops.
- Fast icon grabs: the tile captures the mouse on press, so a quick pull starts the drag instead of falling through to the section as a desktop switch.
- Missed Alt releases (UAC prompt, lock screen) no longer leave a stuck session that revealed every window on a plain Tab press.
- The low-level keyboard hook is re-based every minute while idle, so the OS silently dropping it no longer kills the all-desktops mode until restart.

### Changed
- All-desktops mode no longer hides windows at all. Off-desktop windows stay permanently visible, parked underneath the wallpaper (or off-screen where the OS refuses sub-shell placement), so the system Alt+Tab list is always warm: the switcher opens complete with no reveal step, no tile pop-in, and rapid cross-desktop tabbing works against a stable list, the way native virtual desktops behave. Single-desktop mode keeps the classic hide engine.
- A desktop switch in all-desktops mode is one atomic z-order shuffle with no show or hide; in single mode every show and hide applies in one deferred batch that paints in a single update, replacing the one-window-at-a-time cascade.
- Off-desktop windows that raise themselves are pushed back under the wallpaper automatically.
- Preview snapshots are skipped on the Alt+Tab commit path, whose synchronous captures froze the UI thread while windows vanished one by one.
- Settings window scrolls a fixed 24px per wheel notch instead of multiplying the OS wheel-lines setting.
