# MultiDesk Build and Use

MultiDesk is a .NET Framework 4.8 WPF app (x64) that docks a sidebar of virtual desktops and the windows on them. This file covers building it, installing it, and what each part does.

## Build

Open the solution and produce the executable.

- Open `MultiDesk.sln` in Visual Studio 2022 (the Desktop development with C# workload is enough, because .NET Framework 4.8 ships with it).
- Set the configuration to **Release** and the platform to **x64**.
- Build, which writes `MultiDesk\bin\x64\Release\MultiDesk.exe`.

There are no NuGet packages to restore, because the project references only framework assemblies.

## Install

Package the build into a single setup executable.

- Build `MultiDesk.sln` in Release x64 first so `MultiDesk.exe` exists.
- Open `Installer.iss` in Inno Setup and press Compile, which produces `Output\MultiDeskSetup.exe`.
- The setup installs to the per-user app folder without administrator rights, adds a startup entry, and launches MultiDesk.

You can also just run `MultiDesk.exe` directly without installing.

## Use

MultiDesk lives in the tray and shows a bar of desktop sections.

- **Switch desktop:** click a section, or click its title.
- **Activate a window:** click its icon, which switches to its desktop and focuses it in one action.
- **Preview a window:** hover its icon to open a live preview beside the bar, with the title as a tooltip.
- **Window actions:** right-click an icon for restore, minimize, maximize, move to desktop, always on top, transparency, send to bottom, and close.
- **Manage desktops:** right-click a section to add, rename, or remove a desktop, and to toggle the titles. Drag a window icon onto another section to move it there.
- **Dock and snap:** drag the bar by its header near any edge to dock and reserve that strip, or drag it away to float. The header right-click menu and Settings also set the edge, auto-hide, and more.
- **Cross-desktop switcher:** enable the Ctrl+Alt+D hotkey in Settings to open a list of every window on every desktop and activate one in a single press.

## The single-press activation fix

Every activation runs one synchronous routine that brings the target desktop into view first, restores the window if minimized, then forces focus through the documented foreground-lock workaround, so focus lands on the first action rather than the third.

## Known limitations

These are deliberate trade-offs of the managed engine in this first build.

- A window hidden on another desktop has no live surface, so its hover preview shows the large icon and title rather than a moving thumbnail.
- Window-to-desktop assignments reset on restart, because window handles are not stable between sessions. Desktop count, names, and settings persist.
- A non-elevated MultiDesk cannot show, hide, or trim windows owned by elevated (administrator) processes. Run MultiDesk elevated only if you need that.
- Docking reserves a strip on the primary monitor.

## Project layout

The code is grouped by role under `MultiDesk\`.

- **Interop** holds the P/Invoke surface and the window-event and DWM-thumbnail wrappers.
- **Models** holds the settings type and the desktop and window models.
- **Services** holds the desktop manager, window tracker, window actions, icon cache, dock manager, hotkeys, settings store, theme, and logging.
- **UI** holds the bar, the desktop section, the window tile, the preview, the switcher, and the settings window.
- **Themes** holds the dark and light dictionaries and the shared control styles.
