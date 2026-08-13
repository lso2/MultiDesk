// RETIRED. The Alt+Tab key-watcher service that used to live in this file, which intercepted Tab the
// moment Alt was held to drive MultiDesk's own cross-desktop switcher instead of the real one, has
// been removed entirely along with that switcher (UI\SwitcherWindow.xaml.cs). Cross-desktop switching
// now goes through the real, unmodified Windows Alt+Tab directly: off-desktop windows are kept
// genuinely WS_VISIBLE, parked off screen rather than hidden (see Services\DesktopManager.Visibility.cs),
// so the OS's own switcher already enumerates and thumbnails them on its own, with no key watcher and
// no interception needed at all.
//
// This file is excluded from the build (removed from MultiDesk.csproj's <Compile> list) and is no
// longer part of the app. It is left on disk only because this tool has no way to delete files on
// your machine; it is safe to delete Services\AltTabService.cs by hand.
