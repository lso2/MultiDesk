# Changelog
All notable changes to MultiDesk are recorded here. The major number marks a structural shift, the minor number marks a feature set, and the patch number marks fixes. The newest release is listed first.

## [2.11.2] - 2026-11-14
### Fixed
- Opening Settings leaves the bar thickness, icon size and memory trim delay at their saved values.

## [2.11.1] - 2026-11-14
### Fixed
- Clicking a window's taskbar button switches to its desktop.

## [2.11.0] - 2026-11-14
### Changed
- Picking a window through Alt+Tab, the taskbar, the tray or Start always switches to its desktop.
- Follow focus across desktops is off by default and applies only to windows that take focus on their own.
- Removed the automatic bar thickness adjustment at launch.
- Removed the reserved right margin beside each desktop's icons.
- Removed the bar layout refresh that ran with every remembered placement save.
- Removed the retired switcher, hotkey and keyboard hook sources.
- The installer is named `MultiDeskSetup-<version>-release.exe` and written to the `Output` folder beside `app`.
- The trace records the desktop chosen at launch and the cause of every desktop switch.

### Fixed
- Icons fill every column the saved bar thickness holds.
- Windows on other desktops taking focus on their own stay on their desktop, and focus returns to the window in use.
- MultiDesk starts on the desktop you are using.
- Keyboard focus moves to the Windows desktop when the current desktop shows no window.
- Settings saves blocked by another program holding settings.json are retried.
- The bar stays on screen across Windows' own virtual desktops.
- Double-clicking a window's icon activates the window once.
- Switching desktops leaves minimized windows minimized.
- The bar returns to its edge after a display scale or resolution change.
- bugs.log drops false reports for Alt+Tab reopened during a switch, windows parked again before their check, windows minimized after a restore, and elevated windows.
- Focus placement leaves Windows' foreground lock timeout at the value you set.

## [2.10.0] - 2026-09-14
### Added
- A navigation trace of desktop switches, foreground decisions, parks, restores, icon moves and pane resizes under `%APPDATA%\MultiDesk\logs`.
- `bugs.log`, a separate log of the faults the trace measured.
- Focus landing and window restore checks in the trace.
- Named foreground decisions in the trace, including the guard behind a refused Alt+Tab.
- A Diagnostics section in Settings.
- An update check against GitHub releases, with an Update button on the bar.
- An Updates section in Settings.
- The running version in the Settings window.

### Changed
- The version number is set once, in `AssemblyInfo.cs`.
- The installer output is named `MultiDesk-<version>-release.exe`.
- The Release build is packaged when one exists.
- Version, product name, company and description in the setup executable's file properties.
- Installer architecture identifiers changed to `x64compatible`.

## [2.9.17] - 2026-08-06
### Fixed
- Portrait windows were stretched in the hover preview: DWM scales a live thumbnail into whatever destination rectangle it is given, and the preview handed it the popup's whole content area regardless of the window's own shape, so anything taller than wide was squashed to fit. The preview now fits the source's true aspect ratio into the available area and centers it, the same letterboxing the cached-snapshot fallback always had. No parking change was involved; the earlier DPI diagnosis was wrong about where the stretching lived.
- The fullscreen video browser could still vanish from Alt+Tab, just later: a fullscreen window re-derives its geometry from the monitor exactly like a maximized one, so it eventually defeated the placement park too, and the ladder's last step was a hide, which removes a window from the switcher. The last step is now a MINIMIZE with that window's DWM animation suppressed: invisible, like everything minimized, yet still listed in the real Alt+Tab, and restored natively on return, where Windows itself rebuilds maximized or fullscreen geometry from the monitor. No level of the ladder removes a window from the switcher anymore.
- Choosing a parked window through Alt+Tab now switches desktops through one unified path regardless of the window's park level, because a level-2 commit un-minimizes the window before the foreground event arrives, and the old check would have read it as an ordinary on-screen window and left the decision to the focus-follow setting.

## [2.9.16] - 2026-08-06
### Fixed
- The window last switched away from could vanish from Alt+Tab. 2.9.15 hid every window that defeated a position move, and maximized browser windows always defeat one, so the windows most alt-tabbed to were exactly the ones being hidden. Escalation is now a three-step ladder, each step verified against the window's real rectangle and escalated only on defeat: a plain position move; then one atomic placement change that restores the window to normal state directly INTO the off-screen spot (never visibly un-maximized on screen) and re-maximizes it onto its own monitor in one call on return, deterministic because maximized geometry is monitor-derived, with the window staying visible and live in Alt+Tab throughout; then, only for a window that defeats both, a genuine hide, logged with its title. Maximized browsers land on the placement step and stay in Alt+Tab.
- Portrait windows (the Android emulator mirror among them) could return from parking stretched to wrong proportions. The park spot sat far LEFT, whose nearest monitor can carry a different DPI than the window's own, so parking triggered a DPI resize and the position-only restore left the new size in place. Windows now park far BELOW their own monitor's column, so no DPI change ever occurs while parked, and the restore now reapplies the full captured rectangle, size included, so nothing a window did to itself while away survives the return.
- Closing or minimizing the last window on a desktop could jump to another desktop, because the suppression that should catch Windows' automatic foreground hand-off is timing-based and loses whenever an app hides its window before destroying it. Hand-offs are now detected structurally: a stuck foreground arriving after the PREVIOUS foreground window vanished (closed, hid itself, or minimized) is steered back instead of followed, regardless of event timing, with the system Alt+Tab chooser recognized and exempted since it hides itself on every commit. Closing the last window on a desktop now leaves you on that empty desktop.
- New windows opened in the background could land on a seemingly random desktop, because a process's desktop was inherited from whichever sibling window happened to enumerate first. A sibling on the active desktop now wins outright.

## [2.9.15] - 2026-08-06
### Fixed
- The last-active window of each desktop, maximized browser windows above all, stayed visible on every desktop. Two causes stacked. First, a window can clamp a move to nothing inside its own message handling, so the park call reported success while the window never left the screen; maximized Chromium windows re-derive their position from the monitor and do exactly this, which is also the same reassert that broke this feature's 2.9.2 incarnation. Every park is now verified by re-reading the window's real rectangle immediately, so a clamped no-op is caught at switch time. Second, the repair pass only ever ran after a window event (create, destroy, show, rename, reorder, minimize, foreground), and a window moving itself fires none of those, so on a quiet desktop the violation stood forever. The tracker now runs on a half-second heartbeat as well, so repair no longer depends on anything eventful happening.
- A window that defeats parking, whether caught synchronously by the verification or found back on screen after a believed-successful park, is flagged for the session and genuinely hidden (`SW_HIDE`) while off its desktop from then on, because nothing may ever stay visible off-desktop and Windows offers no way to hold a self-reasserting window both visible and off screen. Stated plainly: such a window is absent from Alt+Tab while away, exactly as every window was in 2.9.5; windows that accept parking, which is most, remain in the real Alt+Tab with live thumbnails. A hide touches neither position nor state, so flagged windows return exactly as they were left. Each flagging is written to the debug log with the window's title.
- Hiding the focused window makes Windows hand the foreground to a successor within milliseconds, and with parked windows staying visible that successor is often one of them. Any stuck foreground event arriving within a quarter second of a focused-window park is now steered back to the active desktop instead of followed, since no person re-Alt+Tabs that fast, which prevents the fighter-hide path from ever causing a spurious desktop switch.

## [2.9.14] - 2026-08-06
### Fixed
The parking mechanism is again untouched; both remaining problems came from one chain the repair pass could not see.
- A window could stand visible on a desktop it does not belong to, indefinitely, including on an empty desktop. Root cause: the repair pass exempted the focused window unconditionally, on the theory that the foreground correction was about to handle it, but corrections only ever start from a foreground event and a window that already holds focus fires no new ones. So the window focused at the moment of a switch, parked with its focus intact, whose app then snapped itself back on screen, held that exemption forever with no event ever coming. The exemption is now time-boxed: 600ms after the last foreground event, long enough for any correction genuinely in flight, the invariant wins and the window is parked like any other.
- Windows kept migrating to the current desktop when such a stray window was clicked, because the sibling-reunite rule read "an on-screen window of an app with a sibling here took focus" and moved it over. Reunite is now fully disabled while parking is on: every case it once served is handled without moving any window between desktops (a new panel opening focused stays on the active desktop by assignment, a parked window taking focus is a hand-off or an Alt+Tab commit, and an on-screen off-desktop window is a violation the repair pass parks). Windows change desktops when the user moves them, never as a side effect of focus.
- A park refused by Windows is now logged once per window. No non-elevated process can move, hide, or otherwise manage an elevated app's window, so an elevated window showing through on every desktop is a Windows security boundary, not a parking failure, and the log now says which is which.

## [2.9.13] - 2026-08-06
### Fixed
The parking mechanism itself is untouched in this release. All three fixes are to desktop-assignment policies around it, written in the hide-engine era, that under parking fired constantly where they used to fire almost never, and physically moved windows between desktops when they did.
- Windows kept migrating onto whichever desktop was active until every desktop's windows were piled behind the current one. Root cause: parked windows stay `WS_VISIBLE`, so Windows hands one of them the foreground after nearly every close, minimize, and popup dismissal, and the sibling-reunite rule read each of those hand-offs as "a second window of this app took focus" and moved that window onto the active desktop and unparked it. Reunite now only ever applies to a window genuinely on screen; a parked window is never moved between desktops by focus alone.
- A new window discovered while it holds the foreground (a form's dialog, a save prompt, a picker) stays on the desktop the user is on, instead of inheriting an arbitrary sibling window's desktop and being parked off screen mid-interaction, which deactivated it, and a dialog that loses activation is pushed behind its app or closes itself before it can be clicked.
- The heal pass no longer parks the owner window of the currently-focused popup. Parking the owner mid-popup rips the popup's anchor off screen, and Windows closes an owned popup whose owner deactivates or moves, which is what made browser save-bookmark bubbles vanish instantly.

Note: desktop assignments saved to settings.json while the relocation bug was active may be scrambled; after this build, arranging windows once persists normally again.

## [2.9.12] - 2026-08-06
### Fixed
- Switching could ignite a permanent feedback loop that snapped back and forth between two desktops with no user input, flooding the system with window moves and focus forcing until Windows Explorer itself locked up. Root cause: every switch parks the window the user was focused on while it still holds the foreground for a few more milliseconds, so its own stale foreground event arrived looking exactly like a window stuck off screen, and the stuck-window correction answered it by switching back, which parked the other side's focused window in turn and manufactured the next stale event, forever. The window MultiDesk itself just parked while focused is now recognized and its stale event ignored, since the switch that parked it already has a deliberate focus placement in flight.
- A circuit breaker backs that up: a fourth stuck-driven switch inside one second is a feedback loop, never a person, so stuck events then stop being honored for a moment and focus is steered back to the active desktop, which starves any loop this diagnosis did not foresee instead of feeding it. The trip is written to the debug log.
- Forcing focus toward a hung window could freeze the bar, and every desktop switch with it, because the focus dance attaches this thread's input queue to the target's thread and an attach to a hung thread hangs with it. A hung window now gets one async show and one plain foreground request, nothing more, and the previous foreground window's thread is likewise never attached while hung.

## [2.9.11] - 2026-08-02
### Fixed
- Clicking the active window's tile restored it right back instead of leaving it minimized. The minimize itself worked, but Windows then handed the foreground to a parked window (parked windows stay visible, so they are eligible successors), and the hand-off re-route from 2.9.9 answered by focusing the top window of the active desktop without skipping minimized ones, which was the very window just minimized, and forcing focus onto a minimized window restores it as a first step. The re-route now only ever picks a window actually standing on screen, and when the active desktop has nothing restorable to focus it leaves the foreground alone, so a minimize sticks and the tile click toggles minimize and restore the way it should.

## [2.9.10] - 2026-08-02
### Fixed
- Switching desktops un-maximized windows and could return them at a wrong default size and position. Root cause: parking moved windows through `SetWindowPlacement`, whose `showCmd` field is a show-STATE command, so every park coerced the window's normal/maximized state as a side effect of moving it.
- Windows from every desktop could remain visibly on screen at once, including on an empty desktop, while the engine believed they were all parked. Root cause, same API: `rcNormalPosition` is only a window's restore rectangle, which a maximized window's on-screen rectangle ignores entirely, so writing the park point there left maximized windows physically in place while the placement-based parked check read that bookkeeping value, swore they were away, and told the heal pass to skip them forever.
- Parking is rebuilt as a pure move of the window's real rectangle: capture `GetWindowRect`, `SetWindowPos` to the off-screen park point with no size change, no z-order change, and no activation, and restore by moving back to the exact captured rectangle. No show-state verb exists anywhere in the movement path anymore, so a maximized window travels and returns as a maximized window, sizes are never touched, and the parked check now reads the same real rectangle the screen shows, which makes "believed parked but visibly on screen" impossible as a state rather than merely unlikely.
- The park coordinate moved off -32000, which is the placeholder position Windows itself moves minimized windows to, so a parked rectangle can never be confused with a minimized one.
- `SetWindowPlacement` survives in exactly one narrow repair that moves and shows nothing: a normal-state window minimized while parked has the park point recorded by Windows as its restore target, so that bookkeeping entry is rewritten to the true captured rectangle while the window stays minimized, and un-minimizing it later lands at its real position.

## [2.9.9] - 2026-08-02
### Fixed
- Parking a window activated it. `Park` moved windows with `SW_SHOWNORMAL`, whose documented behavior includes activating the window, so every park briefly stole the foreground for a window on another desktop, the stuck-off-screen correction answered each theft with a desktop switch, and that switch parked more windows and activated those in turn. That self-sustaining loop was the window flashing on and off screen around once a second, it was several desktops' windows ending up stacked behind the active window at once (each bounce left desktops half-restored), and it was windows appearing to relocate themselves when dragged to another desktop, since the drag's park kicked off the same storm. Every placement change in the visibility engine is now non-activating (`SW_SHOWNOACTIVATE`), on the park side and the restore side both, with maximized restore as the one documented exception because Win32 offers no non-activating maximize; the desktop switch sets the final foreground itself afterward, exactly as before.
- Minimizing or closing a window could switch desktops when "show every desktop's windows in Alt+Tab" was on. Parked windows stay `WS_VISIBLE`, so Windows could pick one as the automatic foreground successor after a minimize or close, and the stuck-off-screen correction bypassed the suppression guard and followed it there. The tracker now watches the system minimize event itself, so a minimize is suppressed no matter who performed it, the sidebar or the window's own title-bar button, and a suppressed hand-off to a parked window now hands the foreground straight back to the active desktop instead of switching, so keystrokes never land on a window no monitor shows.
- A window minimized while parked kept the park point as its restore position, so un-minimizing it later would have sent it off screen. It now stays minimized on restore with only its remembered restore rectangle repaired, including restoring to maximized when that was its true state.
- A minimized window that was not yet tracked was invisible to discovery while parking was on, because the on-screen check read minimized as off screen. Minimized now counts as an ordinary, discoverable state, matching how the hide engine always treated it.
- Windows moved between desktops or revealed by a switch are repaired if they are found standing at the park point with parking off (a parked session killed, then the setting changed before relaunch), instead of being shown on a coordinate no monitor covers.
- Hung windows are skipped by park and restore instead of stalled on, since `SetWindowPlacement` sends synchronous messages a hung window never answers; the heal pass retries once the window responds again.

## [2.9.8] - 2026-08-02
### Removed
- MultiDesk's own cross-desktop Alt+Tab switcher (`SwitcherWindow`, plus the `AltTabService` key watcher and the `HotkeyService` hook that drove it) is gone entirely, along with the "Cross-desktop switcher hotkey (Ctrl+Alt+D)" setting. Drawing any replacement for the real Windows Alt+Tab, no matter how it looked or what it showed, was the wrong approach from the start; the fix is to make the OS's own switcher already correct on its own, not to compete with it.

### Changed
- Off-desktop windows are now parked instead of hidden or cloaked whenever "show every desktop's windows in Alt+Tab" is on: kept genuinely `WS_VISIBLE` and relocated to a coordinate outside every monitor's combined bounds, rather than hidden (`SW_HIDE`) or DWM-cloaked (2.9.7). Staying `WS_VISIBLE` is what lets the real, unmodified Windows Alt+Tab, live thumbnails included, already list every desktop's windows on its own, with nothing drawn by MultiDesk. This mechanism was tried before, in 2.9.2 through 2.9.4, using z-order and topmost tricks to keep a visible window out of sight, and was abandoned in 2.9.5 because that approach could always be defeated by something else in the z order or by a window reasserting its own topmost or fullscreen state. Off-screen coordinates share neither failure mode: nothing can bring a window positioned outside every monitor back into view by changing its z order or topmost state, because there is no monitor pixel there for it to draw on. A healing pass corrects any window an app repositions out of its parked spot on every tracker refresh, and a window found already minimized is left alone rather than parked, since it is already off screen and already lists correctly in Alt+Tab that way.
- The setting formerly described as "Use MultiDesk's switcher for Alt+Tab across desktops" is now "Show every desktop's windows in Windows' own Alt+Tab," describing what it actually does.

### Fixed
- A window parked off screen has its true original position remembered only in the running process's own memory, never on disk, so a MultiDesk process that parks a window and is then killed before it ever restores it, a crash, a forced update, a Task Manager end, could leave that window sitting at the off-screen park point with nothing left running that still knew where it came from, potentially forever. The next launch now recognizes a window already sitting at the exact park point with no remembered position for what it is and gives it a reasonable on-screen fallback position instead, both at the moment such a window is rediscovered and on every tracker refresh afterward, so it is merely repositioned rather than stranded off screen.
- Turning off "show every desktop's windows in Alt+Tab" while a window was still parked from when it was on could leave that window hidden at its off-screen park coordinates, because a real hide never touches position and so simply inherited wherever the window last was. The switch from parking back to hiding now restores every parked window's true position first.
- A window still DWM-cloaked from the previous, now-removed cloaking mechanism (2.9.7) is recognized and uncloaked on rediscovery instead of staying permanently unrecoverable, a one-time migration repair rather than an ongoing mechanism.

## [2.9.7] - 2026-08-02
### Fixed
- The tracker recovery added in 2.9.6 for a window hidden across an unexpected restart matched on executable alone, so any other hidden top-level window belonging to a process that had even one legitimate remembered placement was pulled into the switcher along with it, a shell helper window explorer.exe keeps open for its own use among them. The match is now exact on title as well as executable, and two of the specific helper window classes this let through are also blocked outright, the same way every other known shell helper window already was.

### Added
- Off-desktop windows are now DWM-cloaked instead of genuinely hidden whenever the cloak verifiably takes effect: the window stays composited (so a real, live, playing thumbnail can still be read from it) while DWM itself draws nothing to the physical screen for it, so it is exactly as absent from the screen as a real hide, never merely moved or dimmed. This is attempted for every off-desktop window and always verified immediately after by reading the window's actual cloak state back; whatever does not verify falls straight through to the proven real hide from 2.9.5 in the same pass, so the worst case if cloaking does not work at all on a given machine or a given window is the identical, already-shipped behavior. Microsoft does not document this specific attribute for use outside the shell itself, which is why it is never trusted without checking, on every window, every time.
- The cross-desktop switcher has a dedicated preview panel next to the list, showing a real live DWM thumbnail of the selected window whenever one has a live surface to read, which now includes any window this release managed to cloak rather than hide, on top of every window still on its own active desktop. Only a window that fell back to a real hide, or one that is minimized, has no live surface left, and only those fall back to the last still snapshot captured of it, or its plain icon if none was ever captured. The sidebar's own hover preview gained the same widened reach, for the same reason.

## [2.9.6] - 2026-08-02
### Fixed
- A window MultiDesk had genuinely hidden for another desktop could never be found again after an unexpected restart, so it stayed invisible with nothing left running that still knew where it belonged, indistinguishable from a window that had simply closed. Root cause: the startup scan that rediscovers windows only ever considered visible ones, because a window MultiDesk itself hid looks, from the outside, identical to one that should never be tracked at all. It now also considers a hidden window when its executable was recorded on a desktop by a previous save, which is enough to tell the two apart without opening the door to every ordinary window in the system that happens to be minimized to the tray.
- Window placement is now saved every 45 seconds while MultiDesk runs, not only on a clean exit, so an unexpected restart loses at most a short moment of window movement instead of everything recorded since the last clean exit.
- An unhandled exception anywhere in the app previously ended the whole process, which for a window manager permanently strands every window it had hidden for another desktop, since nothing is left running to show it again. The desktop switch, the visibility engine, and the two drag-and-drop moves between desktops are now guarded at their own source and logged instead of left to bring the process down, and the application itself now catches any other unhandled exception on the interface thread and keeps running instead of exiting.

### Added
- The cross-desktop switcher shows each window's last cached snapshot instead of a bare icon, when one has been captured, closer to what a window still on screen looks like.

### Changed
- DesktopManager is split into a fourth partial file (`DesktopManager.Placements.cs`) for remembered window placement, kept separate from the visibility engine and the Alt+Tab session.

## [2.9.5] - 2026-08-01
### Changed
- All-desktops mode no longer keeps any window visible anywhere off its own desktop, under any circumstance. Every previous version of this feature (2.9.2 through 2.9.4) kept off-desktop windows technically visible (`WS_VISIBLE`) and relied on z-order or off-screen coordinates to keep them out of sight, so that the system Alt+Tab list would still include them. That approach is removed entirely. Off-desktop windows are now always genuinely hidden (`SW_HIDE`), exactly like single-desktop mode always did, with no exceptions and no separate engine.
- Because the system Alt+Tab list can only ever contain visible windows, it can no longer list other desktops' windows now that they are genuinely hidden. When "Alt+Tab across desktops" is enabled, pressing Tab while Alt is held now opens MultiDesk's own cross-desktop switcher in place of the system one, listing every window on every desktop from MultiDesk's own tracked list regardless of its hidden state. Further Tabs (or Shift+Tab) move its selection; releasing Alt activates whatever is highlighted and closes it; Escape cancels.

### Fixed
- Other desktops' windows could show up behind or around the current desktop's window, including on a desktop with only one window open. Root cause: no version of "leave it visible but move it out of sight" is actually reliable, because it can always be defeated by something else in the z order or by a window that fights to reassert its own state. A genuine hide has no such failure mode.
- A window playing fullscreen video (or any window put into real OS fullscreen) could get permanently stuck showing in the background of every desktop until it was minimized, because fullscreen browsers and players commonly reassert their own topmost state to keep the OS from covering them, faster than any z-order placement could hold them back. A hidden window cannot be drawn regardless of topmost state, so this is no longer possible.
- Minimizing or closing a window from the sidebar (or its context menu) could switch to a different desktop as a side effect, cycling through every desktop that had an open window in a fixed order on repeated presses. Root cause: minimizing or closing the active window makes Windows itself hand foreground to whatever it judges is next in line, and MultiDesk's focus-follow was reading that automatic hand-off as the user choosing to visit that window's desktop. Focus-follow is now suppressed for a moment after MultiDesk itself minimizes or closes a window, and, independently, a window that is not on the active desktop is now always genuinely hidden, so there is nothing left for Windows to hand foreground to except another window already on the active desktop.
- Alt+Tab could activate a window while leaving it off-screen at the parking spot, recoverable only through Alt+Space. This entire class of bug is eliminated along with the parking engine: a window is either shown at its real position or genuinely hidden, never shown at a fake position.
- The Alt+Tab commit no longer depends on inferring the user's choice from watching which window the OS happens to report as foreground. MultiDesk's own switcher knows the selection directly the instant Alt is released, which removes the suspect-window tracking, the foreground race window, and the backstop timer that the old design needed to guess correctly.

### Removed
- The parked visibility engine (`DesktopManager.Parking.cs`, added in 2.9.2) and the `Parked`, `ParkOffscreen`, `HasParkPos`, `ParkLeft`, `ParkTop`, `SavedLeft`, and `SavedTop` fields it depended on. Nothing needs a restore position anymore, because nothing is ever moved off-screen anymore.

## [2.9.4] - 2026-08-01
### Fixed
- All-desktops mode still showed other desktops' windows behind the current one even after 2.9.3's park verification. That check only confirmed a window sat below the shell window specifically, but the shell is not always the thing actually painting the wallpaper: Explorer renders the desktop background in a companion window it can tear down and recreate independently, and the two are not guaranteed to stay in a fixed order relative to each other. A park that landed below the shell but above that companion window passed verification while still showing through in any screen area the active desktop's own windows did not cover. Parking now targets the literal bottom of the z order instead of "one step below the shell," which is below both of them regardless of which one is actually lower, and verification checks for anything foreign below the window at all rather than checking its position against one specific handle.
- The fallback to off-screen parking was an all-or-nothing switch for the whole app, decided once from whichever window happened to be parked first. If that window accepted bottom placement, every other window trusted the same in-place parking for the rest of the run, including ones that would have been individually refused (this varies per window, not per system). Refusal is now checked per window, so one window falling back to off-screen coordinates no longer forces or hides the need for the same fallback on any other window.
- Alt+Tab could still activate a window while it sat at the off-screen park spot, recoverable only through Alt+Space. The commit path unparked a window's z order and visibility in one deferred batch but never re-checked whether the position restore inside that batch actually landed, so a restore that had nothing to restore (or lost the race) left the window shown, focused, and invisible until the next tracker pass caught up, if it caught up at all. Every unpark is now followed by an immediate, synchronous check of the window's real on-screen position, so a window still found stranded is moved back before control returns to the caller, not on the next tick.

## [2.9.3] - 2026-08-01
### Fixed
- All-desktops mode: every off-desktop window could sit visibly stacked behind the current desktop's windows. The check that decides whether below-wallpaper parking works asked whether ANY window sat below the shell in z-order, and hidden system windows nearly always do, so it approved placements the OS had actually clamped into the visible band. Parking is now verified against our own windows' real z position, and the moment the OS clamps one it falls back to off-screen parking, which cannot be refused.
- The stray healer could churn forever re-parking the same windows below a shell that kept refusing them, because each failed attempt raised a reorder event that scheduled the next pass. A stray that returns right after being re-parked now demotes parking to off-screen mode for good.
- Alt+Tab could focus a window while leaving it parked off-screen at the top-left, recoverable only through Alt+Space. Focus-follow treated parked windows as visible, so the post-switch settle window silently dropped the activation. Parked windows now count as hidden for focus-follow, a foreground found sitting on a parked window is committed to its desktop by the heal pass even inside the settle window, and any window found stranded at the park spot is restored to its last good position, including windows left there by a crashed earlier run.
- The saved restore position survives its first use, and a minimized window's off-screen minimized coordinates are never captured as a restore target.
- An Alt+Tab session whose Alt release was never seen (the OS dropped the keyboard hook mid-gesture) blocked cross-desktop switching until the next key press; it now closes automatically once Alt is provably up.
- The keyboard hook watchdog also retries when the initial install failed, instead of leaving all-desktops mode dead until restart.
- The backstop commit timer dropped from 400ms to 250ms, so cancelled or mouse-selected gestures settle faster; an early commit can no longer strand the real selection invisibly because the parked failsafes catch the late activation.
- Single-desktop mode: a show that fell back from the batch API skipped restoring the window's position, which could reveal a previously parked window at the off-screen spot.
- Off-screen parking gained the same self-raise healing as below-shell parking: an app that moves its own parked window back on-screen is parked again and keeps the position it chose as its new restore target.

### Changed
- DesktopManager is split into three partial-class files (core, Alt+Tab session, parking engine).

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
