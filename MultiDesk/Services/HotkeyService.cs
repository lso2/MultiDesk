// RETIRED. The low-level keyboard hook that used to live in this file, watching for Alt+Tab and the
// Ctrl+Alt+D cross-desktop switcher hotkey to drive MultiDesk's own custom switcher
// (UI\SwitcherWindow.xaml.cs) and Services\AltTabService.cs, has been removed entirely along with both
// of those. Cross-desktop switching now goes through the real, unmodified Windows Alt+Tab directly,
// which needs no hook of its own to intercept anything; see Services\DesktopManager.Visibility.cs for
// how off-desktop windows stay enumerable to it without MultiDesk drawing anything itself.
//
// This file is excluded from the build (removed from MultiDesk.csproj's <Compile> list) and is no
// longer part of the app. It is left on disk only because this tool has no way to delete files on
// your machine; it is safe to delete Services\HotkeyService.cs by hand.
