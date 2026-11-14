using System;
using System.Globalization;
using NM = MultiDesk.Interop.NativeMethods;

namespace MultiDesk.Services
{
    /// <summary>
    /// The landing verdict. Defines DiagNavLanding, which reads where the foreground actually ended
    /// up once a decision has settled and names what that says. A window the user cannot see holding
    /// the keyboard is read from the window itself, never from a desktop index. A landing elsewhere
    /// counts as a fault only when MultiDesk put focus there: another process taking it, or the same
    /// app opening a window of its own, is somebody else's doing.
    /// Doc: docs/notes/MultiDesk/Services/Diag.md
    /// </summary>
    internal static class DiagNavLanding
    {
        public static void Check(IntPtr hwnd, string title, int windowDesktop, int activeAtDecision,
            string outcome, bool viaTaskChooser, IntPtr steered)
        {
            if (!Diag.Enabled) return;
            try
            {
                var fg = NM.GetForegroundWindow();
                int activeNow = DiagNav.LastActive;
                bool alive = hwnd != IntPtr.Zero && NM.IsWindow(hwnd);
                var where = "wanted=" + DiagFormat.H(hwnd) + " " + DiagFormat.Q(title)
                    + " desktop=" + windowDesktop.ToString(CultureInfo.InvariantCulture)
                    + " active=" + activeAtDecision.ToString(CultureInfo.InvariantCulture)
                    + "->" + activeNow.ToString(CultureInfo.InvariantCulture)
                    + " outcome=" + outcome
                    + (viaTaskChooser ? " via=alt-tab" : "")
                    + " got=" + Describe(fg);

                if (fg == hwnd)
                {
                    // Judged on what the screen shows, never on the desktop index: a window can belong
                    // to another desktop and still be standing in plain sight, which is a bookkeeping
                    // difference the heal pass settles rather than a fault of its own.
                    if (!DiagWindow.NoMonitorShows(hwnd))
                    {
                        Diag.Detail(DiagChannels.Fg, "landed", where);
                        return;
                    }
                    Diag.Write(DiagChannels.Fg, "landed-out-of-sight", where + " " + DiagFormat.Flags(hwnd));
                    DiagBug.Report("focus-on-window-no-monitor-shows", DiagFormat.Q(title),
                        where + " " + DiagWindow.Describe(DiagWindow.Capture(hwnd)));
                    return;
                }

                if (!alive)
                {
                    Diag.Detail(DiagChannels.Fg, "target-gone", where);
                    return;
                }

                // The app the user picked opening a window of its own is the app's business. Its
                // dialogs, pickers and tool panels all arrive this way within the settle window.
                uint wanted = DiagWindow.ProcessOf(hwnd);
                if (wanted != 0 && wanted == DiagWindow.ProcessOf(fg))
                {
                    Diag.Detail(DiagChannels.Fg, "landed-on-sibling-window", where);
                    return;
                }

                if (outcome == DiagNav.OutSwitched)
                {
                    // The desktop was switched to this window and focus was placed on it. Another
                    // process holding it half a second later took it from a window the user chose.
                    Diag.Write(DiagChannels.Fg, "switched-then-lost", where);
                    DiagBug.Report("switch-focus-taken-by-another-process", DiagFormat.Q(title), where);
                    return;
                }

                // Focus resting where MultiDesk itself sent it is the only remaining case that says
                // MultiDesk decided the outcome. Anything else took focus while this was settling.
                bool mdPlaced = steered != IntPtr.Zero && fg == steered;
                if (!mdPlaced)
                {
                    Diag.Detail(DiagChannels.Fg, "focus-moved-on-by-something-else", where
                        + " steered=" + (steered == IntPtr.Zero ? "none" : DiagFormat.H(steered)));
                    return;
                }

                if (viaTaskChooser)
                {
                    // The previous foreground was the system task switcher, so this was a window the
                    // user picked by hand in Alt+Tab, and MultiDesk sent focus somewhere else. The
                    // outcome names which decision produced it.
                    Diag.Write(DiagChannels.Fg, "alt-tab-not-honored", where + " " + DiagFormat.Flags(hwnd));
                    DiagBug.Report("alt-tab-commit-not-honored", DiagFormat.Q(title), where);
                    return;
                }

                if (outcome == DiagNav.OutBreakerCooldown || outcome == DiagNav.OutBreakerTrip)
                {
                    Diag.Write(DiagChannels.Fg, "breaker-blocked", where);
                    DiagBug.Report("switch-breaker-blocked-foreground", DiagFormat.Q(title), where);
                    return;
                }

                Diag.Detail(DiagChannels.Fg, "steered-away", where);
            }
            catch { }
        }

        private static string Describe(IntPtr h)
        {
            if (h == IntPtr.Zero) return "none";
            if (!NM.IsWindow(h)) return DiagFormat.H(h) + " gone";
            try
            {
                return DiagFormat.H(h) + " " + DiagFormat.Q(NM.TextOf(h)) + " class=" + (NM.ClassOf(h) ?? "?");
            }
            catch { return DiagFormat.H(h); }
        }
    }
}
