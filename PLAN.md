# MultiDesk Project Plan

MultiDesk is a Windows sidebar that manages virtual desktops and the windows on them, built to replace the desktop sidebar in Actual Window Manager with something lighter and more reliable. This plan is the working specification. It supersedes the earlier AI-generated draft, and where the two disagree, this document wins because it reflects the priorities you set directly.

## Goal

Give a heavy multi-desktop user a fast, low-memory bar that shows every desktop as its own section, shows the windows on each desktop as icons, previews a window live on hover, and exposes per-window actions on right click. The bar docks to any screen edge the way QuickPane's pinned dock does, and switching to a window always lands focus in a single action.

## Decisions locked

These were settled before the build and shape everything below.

- **Desktop engine:** MultiDesk-managed. The app owns desktops 1..N and switches by showing and hiding windows through documented Win32 only, which keeps it working on every build, keeps it testable on your machine, and lets it trim the memory of off-desktop windows.
- **Framework:** .NET Framework 4.8, x64, WPF. It ships in-box on Windows 10 LTSC so users install nothing, and it reuses QuickPane's proven AppBar and interop code through the same Visual Studio and Inno Setup pipeline.
- **Menu scope:** Core actions first. The right-click menu ships a solid, reliable subset now, with the wider Actual Window Manager set listed as deferred.

## How it looks

The bar is a narrow dark strip of stacked desktop sections, matching your mockups. Each section can show a title at its top, holds a small grid of app icons for the windows on that desktop, and carries a full lighter border when it is the active desktop. The focused window's icon carries its own highlight so the active item reads at a glance. Hovering an icon opens a floating preview beside the bar with a live image of that window and its title.

## Panel and docking

The panel is a floating, resizable window by default, and dragging it to a screen edge snaps it there and reserves that strip as an OS AppBar so other windows do not overlap it. This is the same docking behavior QuickPane uses for its pinned left dock, generalized to all four edges.

- **Edges:** left and right dock vertically, top and bottom dock horizontally, and the desktop sections lay out along the long axis in each case.
- **Snap:** dragging the bar within a threshold of an edge docks it to that edge on release, and dragging it away from the edge floats it again.
- **Auto-hide:** an optional mode collapses the docked bar to a thin trigger strip that slides out on hover, for users who want the screen space back.
- **Resize:** a gripper on the inner edge changes the bar's thickness, and the size persists.

## Desktops (the managed engine)

MultiDesk keeps its own list of desktops and assigns every tracked window to one of them. Switching to a desktop shows that desktop's windows and hides the rest with the documented show and hide calls, so there is no dependence on undocumented COM that could fail on your exact LTSC edition.

- **Manage:** right-click a section to add a desktop, rename it, or remove it, where removing a desktop moves its windows onto a neighbor rather than stranding them.
- **Move windows:** drag a window icon onto another section, or use the move-to-desktop submenu, to reassign a window.
- **Memory:** an optional pass trims the working set of windows on desktops you are not viewing after a short delay, which returns physical memory to the system under heavy load, and a quick switch back faults the pages in again.
- **Safety:** on exit MultiDesk shows every window it was managing so nothing is ever left hidden, and a tray command and the switcher both recover windows on demand.

## Windows on each desktop

A tracker enumerates top-level application windows, filters out tool windows, cloaked shells, and MultiDesk's own windows, and groups what remains by assigned desktop. It keeps the grid live through window event hooks for create, destroy, show, and title change, debounced so a busy moment with many windows costs one refresh rather than many.

## Hover preview and tooltip

Resting the cursor on an icon past a short delay opens the preview beside the bar. For a window on the active desktop the preview is a live DWM thumbnail that updates on its own, and for a window on another desktop, which has no live surface while hidden, the preview falls back to a large icon with the title. Only one preview exists at a time, which keeps the feature close to free in memory.

## Right-click menu (core-first)

Right-clicking a window icon opens a themed menu of the actions that apply to any window.

- **Activate** brings the window forward through the hardened single-action routine described below.
- **Restore, Minimize, Maximize** drive the window's show state.
- **Move to desktop** lists the desktops and a new-desktop entry, and reassigns the window.
- **Always on top** toggles topmost.
- **Transparency** sets the window to 100, 90, 75, or 50 percent.
- **Send to bottom** drops the window behind the others.
- **Close** asks the window to close.

App-specific entries from the mockup such as new tab, vertical tabs, and bookmark all tabs are not generically possible across arbitrary windows, so they are left out by design.

## The Alt+Tab activation fix

The triple-press problem in Actual Window Manager comes from two things stacking, where a window hidden on another desktop drops out of the system Alt+Tab list so early presses only surface it, and the desktop switch races the focus call so Windows' foreground lock rejects the activation. MultiDesk runs one synchronous routine on every activation that brings the target desktop into view first, restores the window if minimized, then forces focus through the documented foreground-lock workaround so a single action lands focus every time. An optional cross-desktop switcher on a hotkey lists every window on every desktop and activates the selection the same reliable way, as a correct replacement for the OS switcher.

## Settings

A settings window binds to the stored configuration and applies changes live.

- **Show desktop titles** toggles the per-section title row.
- **Theme** follows the system or forces dark or light.
- **Dock edge** chooses left, right, top, bottom, or floating.
- **Auto-hide** collapses the docked bar to a trigger strip.
- **Trim off-desktop memory** and its delay control the memory pass.
- **Icon size** and **bar thickness** size the grid.
- **Start with Windows** writes the per-user startup entry.
- **Cross-desktop switcher** enables the optional hotkey.

## Persistence

Settings live in a JSON file under the per-user app data folder, written atomically so a crash never truncates it. Desktop count, names, and all preferences persist across runs. Per-window desktop assignments do not persist, because window handles are not stable between sessions, so windows open on the active desktop at startup and you distribute them from there.

## Build and install

The solution file sits in the repository root for Visual Studio, and the Inno Setup script sits beside it.

- **Build:** open MultiDesk.sln, select Release and x64, and build, which produces MultiDesk.exe.
- **Install:** open Installer.iss in Inno Setup and compile, which produces a single setup executable that installs without administrator rights, adds a startup entry, and launches the app.

## Deferred (future)

These are out of scope for the first build and tracked for later.

- The wider Actual Window Manager menu: roll up to title bar, ghost click-through, priority, minimize to tray, and richer transparency control.
- Per-desktop wallpaper, where the only approach that ships cleanly is swapping the wallpaper on switch.
- An optional native virtual-desktop bridge for users who prefer the OS desktops, kept behind the same engine interface.
- A Windows 11 port and an open-source release, both of which the managed engine already supports without per-build changes.

## Project structure

The code is organized so a later port stays contained.

- **Interop** holds all P/Invoke and the window event and DWM thumbnail wrappers.
- **Models** holds the settings and the desktop and window data types.
- **Services** holds the desktop manager, window tracker, window actions, icon cache, dock manager, settings store, theme, and logging.
- **UI** holds the main bar, the desktop section, the window tile, the preview, the switcher, and the settings window.
- **Themes** holds the dark and light dictionaries and the shared control styles.
