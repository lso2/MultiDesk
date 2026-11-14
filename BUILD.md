# MultiDesk Build and Use

MultiDesk is a .NET Framework 4.8 WPF app (x64) that docks a sidebar of desktops and their windows to a screen edge. Build steps, installer steps and the parts of the bar follow.

## Build

The solution builds with Visual Studio 2022 or later and the Desktop development with C# workload.

- Open `MultiDesk.sln`.
- Set the configuration to **Release** and the platform to **x64**.
- Build. The executable is written to `MultiDesk\bin\x64\Release\MultiDesk.exe`.

The project references framework assemblies only, and no NuGet restore step exists.

## Install

`Installer.iss` packages the build into a single setup executable.

- Build `MultiDesk.sln` for x64 first. Release is packaged when it is present and Debug otherwise.
- Open `Installer.iss` in Inno Setup and press Compile. The setup is written to `MultiDeskSetup-<version>-release.exe` in the `Output` folder beside `app`.
- The version in the file name, in Add or remove programs and in the setup executable's properties is read from the built executable. The compiler prints the build and version it packaged, and it stops with a message when neither build exists.
- The setup installs to the per-user app folder without administrator rights, adds a startup entry, and launches MultiDesk.

`MultiDesk.exe` also runs directly without installing.

## Use

MultiDesk runs from the tray and shows a bar of desktop sections.

- **Switch desktop:** click a section or its title.
- **Activate a window:** click its icon. The desktop switches and the window gets focus in one action.
- **Preview a window:** hover its icon for a preview beside the bar with the window title.
- **Window actions:** right-click an icon for restore, minimize, maximize, move to desktop, always on top, transparency, send to bottom, and close.
- **Manage desktops:** right-click a section to add, rename, or remove a desktop, and to toggle the titles. Drag a window icon onto another section to move the window there.
- **Dock and snap:** drag the bar by its header near any edge to dock it there, or away from the edge to float it. The header right-click menu and Settings also set the edge, auto-hide, and more.
- **Alt+Tab across desktops:** with **Show every desktop's windows in Windows' own Alt+Tab** on, every desktop's windows are listed in the Windows Alt+Tab, and selecting one switches to its desktop.
- **Remember window placement:** with **Remember window placement across restarts** on, each window's desktop is restored at the next launch, matched by executable, browser profile and title.

## Single-press activation

An activation switches to the target window's desktop, restores the window when it is minimized, and sets focus with MultiDesk's input queue attached to the foreground and target windows. Focus lands on the first click or Alt+Tab selection.

## Known limitations

- The hover preview of a window on another desktop shows its remembered snapshot, or its icon when no snapshot exists. The live thumbnail is shown for windows on the current desktop.
- A non-elevated MultiDesk cannot park, hide, or trim windows owned by elevated (administrator) processes. Running MultiDesk elevated removes that limit.
- Docking reserves a strip on the primary monitor.

## Project layout

The code under `MultiDesk\` is grouped by role.

- **Interop:** P/Invoke declarations and the window-event and DWM-thumbnail wrappers.
- **Models:** the settings type and the desktop and window models.
- **Services:** the desktop manager, window tracker, window actions, shell route classification, icon cache, dock manager, settings store, theme, update check, logging, and the navigation trace in `Services\Diag`.
- **UI:** the bar, the desktop section, the window tile, the preview, and the settings window.
- **Themes:** the dark and light dictionaries and the shared control styles.
