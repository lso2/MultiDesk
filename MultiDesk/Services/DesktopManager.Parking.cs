// RETIRED. The "parked" visibility engine that used to live in this file (keep off-desktop windows
// WS_VISIBLE and hide them from view only by relocating them in z order or off-screen) has been
// removed entirely and replaced with a real, unconditional hide/show engine in DesktopManager.cs.
// That approach could always be defeated by something else in the z order or by a window fighting to
// reassert its own topmost/fullscreen state, which is exactly what caused other desktops' windows to
// show through and fullscreen windows to get stuck visible across every switch. A real SW_HIDE has no
// such failure mode.
//
// This file is excluded from the build (removed from MultiDesk.csproj's <Compile> list) and is no
// longer part of the app. It is left on disk only because this tool has no way to delete files on
// your machine; it is safe to delete Services\DesktopManager.Parking.cs by hand.
