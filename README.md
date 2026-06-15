# MultiDesk

![Windows](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D6.svg?logo=windows&logoColor=white)
![.NET Framework](https://img.shields.io/badge/.NET%20Framework-4.8-512BD4.svg?logo=dotnet&logoColor=white)
![C#](https://img.shields.io/badge/C%23-7.3-239120.svg?logo=csharp&logoColor=white)
![UI](https://img.shields.io/badge/UI-WPF-1f6feb.svg)
![Version](https://img.shields.io/badge/Version-2.4.2-success.svg)
![License](https://img.shields.io/badge/License-MIT-orange.svg)

A lightweight Windows virtual-desktop sidebar and window manager. MultiDesk groups your open windows onto desktops shown as a slim docked bar, so you can switch contexts, preview windows on hover, and move or pin apps between desktops without the friction of Windows' built-in virtual desktops. Everything runs locally as a single tray app, and all settings stay in a plain JSON file on your computer.

## Summary

MultiDesk turns one cluttered taskbar into a set of focused desktops shown down the edge of your screen, with:

- **Managed virtual desktops** - Group windows into desktops; switching shows that desktop's windows and hides the rest
- **📌 Dockable sidebar** - Dock to any screen edge, float it, or auto-hide it to a thin trigger strip
- **🖼️ Live hover previews** - Hover an icon for a live thumbnail (or a remembered snapshot) with the window title
- **🖱️ One-click control** - Click an icon to bring a window forward, click again to minimize, right-click for the full menu
- **🔀 Alt+Tab across desktops** - Reveal every desktop's windows in the system Alt+Tab, or restrict it to the current one
- **📍 Pin apps to desktops** - Force an app to always open on a chosen desktop, with independent rules per browser profile
- **⚡ Reliable one-press focus** - Fixes the "press Alt+Tab three times to activate" problem found in other managers
- **🎨 Native feel** - Dark, light, or system theme that blends into the Windows shell
- **💾 Local storage** - Settings live in a JSON file you can back up, edit, and restore

## Features

### Managed virtual desktops

MultiDesk does not use Windows' native virtual desktops. Instead it manages a set of its own desktops by showing and hiding top-level windows. Each desktop is a group of windows; switching to a desktop reveals its windows and hides the others, so your screen and taskbar only show what belongs to the desktop you are on. This keeps switching instant and lets MultiDesk add previews, pinning, and per-desktop layout that the native desktops do not offer.

- Start with any number of desktops (six by default), add or remove them at will
- Name each desktop and toggle the titles on or off
- Reorder desktops by dragging a desktop's title, which carries its windows with it
- The active desktop is marked with a full lighter border, and the active window's icon is highlighted

### The sidebar

The bar is a thin, always-available strip of desktop sections.

- Dock it to the **left, right, top, or bottom** edge, or set it **floating**
- **Auto-hide** collapses it to a slim trigger strip; it expands on hover and tucks away when you leave, and it reveals itself briefly on launch so you can always find it
- Drag the bar's header near an edge to snap and dock it there
- Drag the inner edge to resize the bar's thickness (width when vertical, height when horizontal), snapping to whole icon columns
- **Reserve space** registers the bar as a Windows AppBar so other windows never cover it, or switch to an overlay that floats above content
- **Always on top** keeps the bar above other windows
- The corners are left free in auto-hide mode so moving to the top-right window buttons or the bottom taskbar corner never pops the bar open

### Window tiles

Every window appears as an icon tile in its desktop's section.

- **Hover** for a preview popup: a live DWM thumbnail for a window on the current desktop, or its icon and remembered snapshot for one on another desktop, with the title shown in the header
- **Click** to bring the window forward; **click again** to minimize it
- **Right-click** for the full action menu: Activate, Restore, Minimize, Maximize, Move to desktop, Pin app here, Always on top, Transparency, Send to bottom, and Close
- **Drag** a tile onto another tile to reorder it, or onto another desktop section to move that window there
- Icons stay grabbable instantly; a small jitter during a click never turns into an accidental drag

### Desktops and layout

- Desktops **auto-fill** the bar height evenly, so the space is always used
- Drag the divider under any desktop to resize just that desktop, growing it and shrinking the one directly below it like a splitter, down to a single row
- A desktop scrolls within its own area when it holds more windows than fit, with a slim, grabbable scroll indicator
- In a top or bottom dock the icons wrap into columns and each desktop's width is resizable instead

### Pin apps to desktops

Pinning makes an app always open on a chosen desktop.

- Right-click a window and choose **Pin [app] here** to bind that executable to the current desktop
- Pins are **per browser profile**: pin one Chrome or Brave profile to a desktop without affecting the others
- Pins persist across restarts and apply for the whole session, so a pinned app always lands on its desktop

### Alt+Tab across desktops

By default MultiDesk reveals every desktop's windows in the system Alt+Tab, so you can tab to any window anywhere and MultiDesk switches you to its desktop when you select it. A single Settings toggle restricts Alt+Tab to the current desktop only. The reveal is driven by a non-blocking keyboard watcher that can never swallow or break the normal Alt+Tab.

### Reliable one-press focus

Some window managers require pressing Alt+Tab two or three times to actually activate a window on another desktop. MultiDesk switches to the target window's desktop first and then forces focus through the documented foreground-lock workaround, so a single click or selection always lands focus on the window.

### Cross-desktop switcher

An optional global hotkey (`Ctrl+Alt+D`) opens a switcher listing every window across all desktops, so you can jump straight to any window without opening the bar.

### Remember window placement

With this on, MultiDesk records which desktop each window sat on and restores them on the next launch, matched by executable, browser profile, and title. Restoration only applies during a short boot window, so a window you open by hand later in the session lands on the current desktop rather than its old one.

### Persisted previews

When enabled (on by default), MultiDesk caches a downscaled snapshot of each window as you leave its desktop, so hovering a window that now lives on a hidden desktop shows its last look instead of just an icon. Captures are throttled so flipping between desktops never re-snapshots the same window repeatedly. Each snapshot is roughly 200 KB; turn the option off to use only icons and free the cache.

### Memory trimming

Optionally, MultiDesk trims the working set of windows that are hidden on other desktops after a short delay, returning physical memory to the system while they are not visible.

### Appearance and themes

- Dark, light, or system theme, applied live with no restart
- Adjustable bar thickness and icon size
- Toggle desktop titles and choose left, center, or right title alignment

### Settings backup and restore

Back up every setting, desktop name, height, and pin to a single JSON file from the Settings window, and restore it on the same or another machine.

## Requirements

- **Windows 10 (1809+) or Windows 11**, 64-bit
- **.NET Framework 4.8** runtime (preinstalled on current Windows)
- To build from source: **Visual Studio 2019/2022** (or the .NET Framework 4.8 build tools)
- To build the installer: **[Inno Setup](https://jrsoftware.org/isinfo.php)** 6+

## Installation

### Option A: Run a release build

1. Download the latest `MultiDeskSetup.exe` from the Releases page (or build it yourself below)
2. Run the installer. It installs per-user, adds a startup entry, and launches MultiDesk
3. The MultiDesk tray icon appears; the bar docks to the right edge by default

### Option B: Build from source

```bash
git clone https://github.com/lso2/MultiDesk.git
```

**Build the app**

1. Open `MultiDesk.sln` in Visual Studio
2. Select the **Debug | x64** or **Release | x64** configuration
3. Build the solution. The executable is produced at `MultiDesk\bin\x64\Debug\MultiDesk.exe` (or `...\Release\...`)
4. Run `MultiDesk.exe`

**Build the installer (optional)**

1. Build the app first so `MultiDesk.exe` exists at the path referenced in `[Files]`
2. Open `Installer.iss` in Inno Setup and press **Compile**
3. The single-file `MultiDeskSetup.exe` is produced in the output folder

> The installer's `[Files]` source points at the `Debug` output by default. If you build `Release`, change `Debug` to `Release` in `Installer.iss`.

## Usage

### Switching desktops

- Click a desktop section (its empty area or its title) to switch to it
- Click a window's icon to switch to its desktop and bring the window forward

### Managing windows

- Click an icon to activate, click it again to minimize
- Right-click an icon for Activate, Restore, Minimize, Maximize, Move to desktop, Pin, Always on top, Transparency, Send to bottom, and Close

### Moving and pinning

- Drag an icon onto another desktop section to move that window there
- Drag an icon onto another icon to reorder it
- Right-click and choose **Pin [app] here** to make that app always open on the current desktop

### Resizing

- Drag the bar's inner edge to resize its thickness
- Drag the divider beneath a desktop to resize that desktop and the one directly below it

### Docking

- Drag the header to an edge to snap and dock there, or set the edge in the header's right-click menu or in Settings
- Choose floating to position the bar anywhere

### Settings

- Right-click the header and choose **Settings...**, or use the tray menu
- Adjust theme, dock edge, thickness, icon size, titles, auto-hide, always on top, reserve space, Alt+Tab behavior, previews, memory trimming, startup, and the cross-desktop hotkey
- Back up and restore all settings from the same window

## Settings and data

All data is stored locally under your user profile:

```
%APPDATA%\MultiDesk\
├── settings.json        # All settings, desktop names, heights, pins, and remembered placements
└── debug.log            # Diagnostic log
```

### Privacy

- **No cloud sync** - everything stays on your computer
- **No accounts** - no login or registration
- **No telemetry** - MultiDesk makes no external network requests
- **Readable format** - a JSON file you can open, edit, and back up anywhere

### settings.json format

```json
{
  "dockEdge": "right",
  "barThicknessPx": 110,
  "iconSizePx": 24,
  "showTitles": true,
  "titleAlignment": "center",
  "theme": "system",
  "autoHide": true,
  "alwaysOnTop": true,
  "reserveSpace": true,
  "trimHiddenMemory": true,
  "trimDelayMs": 5000,
  "desktopCount": 6,
  "desktopNames": ["Desktop 1", "Desktop 2", "Desktop 3", "Desktop 4", "Desktop 5", "Desktop 6"],
  "rowsPerDesktop": 3,
  "desktopRows": [0, 0, 0, 0, 0, 0],
  "startWithWindows": true,
  "switcherHotkeyEnabled": false,
  "autoSwitchOnForeground": true,
  "altTabAllDesktops": true,
  "persistPreviews": true,
  "rememberPlacement": true,
  "showCoffee": true,
  "appPins": [
    { "exe": "explorer.exe", "desktop": 2 },
    { "exe": "chrome.exe", "profile": "Default", "desktop": 1 }
  ],
  "placements": [
    { "exe": "notepad.exe", "title": "Untitled - Notepad", "desktop": 0, "order": 0 }
  ],
  "schemaVersion": 6
}
```

## Compatibility

| OS | Supported | Notes |
|----|-----------|-------|
| Windows 11 | ✅ | Full support |
| Windows 10 (1809+) | ✅ | Full support |
| Windows 8.1 | ⚠️ | Untested; full-content previews need 8.1+ |
| Windows 7 | ⚠️ | Untested; some shell integration unavailable |
| macOS / Linux | ❌ | Windows-only (Win32 + WPF) |

## Troubleshooting

### The bar is not visible after installing

The installer relaunches MultiDesk, and with auto-hide on the bar reveals itself for a couple of seconds on launch, then collapses to a thin strip at its edge. Move your cursor to that edge to expand it, or use the tray icon to show or hide the bar.

### A window from another desktop is missing from Alt+Tab

Make sure **Show all desktops' windows in Alt+Tab** is on in Settings. Hold Alt and tab through; every desktop's windows appear, and selecting one switches you to its desktop.

### A window stays stuck in the bar after closing it

If an app minimizes to the tray (for example f.lux) or a transient dialog finishes, MultiDesk drops it from the bar once it is no longer visible on the active desktop. If a window seems stuck, switch to its desktop so MultiDesk can re-check it.

### Switching desktops does not switch

Try turning off **Follow focus across desktops** in Settings. MultiDesk already guards against focus bouncing during a switch, and on exit it reveals every managed window so none is left hidden.

### Build errors about C# syntax

The project targets C# 7.3 (`<LangVersion>7.3</LangVersion>`). Make sure you build with the .NET Framework 4.8 toolchain rather than retargeting to a newer framework.

## How it works

MultiDesk is a tray application built on WPF and documented Win32 interop only; it never injects code into other processes. The core ideas:

- **EnumWindows + SetWinEventHook** discover top-level application windows and keep the desktop model in sync as windows open, close, and gain focus
- **ShowWindow / ShowWindowAsync** (`SW_HIDE` / `SW_SHOWNA`) are the managed-desktop engine: switching a desktop shows its windows and hides the others
- **SHAppBarMessage** reserves the screen edge as an AppBar so the docked bar is never covered
- **DWM thumbnails** (`DwmRegisterThumbnail`) render the live hover preview; **PrintWindow** captures the persisted snapshots
- **AttachThreadInput + SetForegroundWindow** with the foreground-lock timeout workaround give reliable one-press focus
- A **low-level keyboard hook** (`WH_KEYBOARD_LL`) observes Alt+Tab to reveal all desktops, without ever swallowing keys
- **WMI** reads a browser's `--profile-directory` so profiles pin independently
- **ImmersiveShell `IVirtualDesktopPinnedApps`** pins the bar to every native virtual desktop so it stays visible
- **EmptyWorkingSet** trims memory of off-desktop windows
- Settings are serialized to JSON with `DataContractJsonSerializer`, with schema migration so older configs upgrade in place

## Project structure

```
MultiDesk/
├── MultiDesk.sln              # Visual Studio solution
├── Installer.iss              # Inno Setup installer script
├── PLAN.md                    # High-level design notes
├── BUILD.md                   # Build instructions
└── MultiDesk/
    ├── MultiDesk.csproj       # Project (.NET Framework 4.8, x64, C# 7.3)
    ├── app.manifest           # Per-monitor DPI, runs without elevation
    ├── App.xaml(.cs)          # Tray host and service startup
    ├── Interop/
    │   ├── NativeMethods.cs   # All Win32 P/Invoke
    │   ├── WinEventHook.cs    # Foreground and window-event hooks
    │   ├── DwmThumbnail.cs    # Live preview thumbnails
    │   └── VirtualDesktop.cs  # Pin the bar to native virtual desktops
    ├── Models/
    │   └── Models.cs          # Settings and runtime models
    ├── Services/
    │   ├── DesktopManager.cs  # Engine: show/hide, switch, layout
    │   ├── WindowTracker.cs   # Discovers and tracks app windows
    │   ├── WindowActions.cs   # Activate, minimize, close, transparency
    │   ├── DockManager.cs     # Edge docking, AppBar, auto-hide
    │   ├── AltTabService.cs   # Alt+Tab across desktops
    │   ├── PreviewCache.cs    # Persisted window snapshots
    │   ├── HotkeyService.cs   # Global hotkeys
    │   ├── ProcessInfo.cs     # Browser profile detection (WMI)
    │   ├── ThemeService.cs    # Dark / light / system theming
    │   ├── IconCache.cs       # Window icon extraction
    │   ├── SettingsStore.cs   # JSON load, save, migrate
    │   └── Log.cs
    ├── Themes/
    │   ├── Controls.xaml
    │   ├── Theme.Dark.xaml
    │   └── Theme.Light.xaml
    └── UI/
        ├── MainWindow.xaml(.cs)      # The bar shell
        ├── DesktopSection.xaml(.cs)  # One desktop section
        ├── WindowTile.xaml(.cs)      # One window icon
        ├── PreviewPopup.xaml(.cs)    # Hover preview
        ├── SwitcherWindow.xaml(.cs)  # Cross-desktop switcher
        └── SettingsWindow.xaml(.cs)  # Settings
```

## Contributing

1. Fork the repository
2. Create a feature branch (`git checkout -b feature/amazing-feature`)
3. Make your changes (keep to C# 7.3 and documented Win32 only)
4. Build and test on Windows 10 or 11
5. Commit your changes (`git commit -m 'Add amazing feature'`)
6. Push to the branch (`git push origin feature/amazing-feature`)
7. Open a Pull Request

## Author

PlexPixel - [plexpixel.com](https://plexpixel.com)

## License

MIT - see the [LICENSE](LICENSE) file for details.

---

**⭐ If MultiDesk made your desktop calmer, please star the repository!**

## 💖 Support this project

If MultiDesk saves you time every day, consider supporting its development.

[![Buy me a coffee](https://img.shields.io/badge/Buy%20me%20a%20coffee-FFDD00?style=for-the-badge&logo=buymeacoffee&logoColor=black)](https://plexpixel.com/donate)

**Why support?**

- ☕ Fuel continued development
- 🚀 New features like tagging, profiles, and richer layouts
- 🐛 Faster fixes and updates
- 📚 Better documentation

---

*Made with ❤️ for people with too many windows open.*
