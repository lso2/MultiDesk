// RETIRED. This file has been renamed to Services\DesktopManager.Foreground.cs. It used to hold both
// the custom Alt+Tab gesture session (BeginAltTab/AdvanceAltTab/EndAltTab/CancelAltTab, driving
// MultiDesk's own switcher) and the foreground-follow bookkeeping that keeps focus-follow honest. The
// gesture machinery has been removed entirely: cross-desktop switching now goes through the real,
// unmodified Windows Alt+Tab directly, which off-desktop windows stay enumerable to by being parked
// off screen rather than hidden or cloaked (see Services\DesktopManager.Visibility.cs). The
// foreground-follow logic that is still needed lives on in Services\DesktopManager.Foreground.cs.
//
// This file is excluded from the build (removed from MultiDesk.csproj's <Compile> list, replaced by
// an entry for DesktopManager.Foreground.cs) and is no longer part of the app. It is left on disk only
// because this tool has no way to delete files on your machine; it is safe to delete
// Services\DesktopManager.AltTab.cs by hand.
