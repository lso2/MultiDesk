using System;
using System.Globalization;
using NM = MultiDesk.Interop.NativeMethods;

namespace MultiDesk.Services
{
    /// <summary>
    /// The landing verdict. Defines DiagNavLanding, which reads where the foreground actually ended
    /// up once a decision has settled and names what that says. A window the user cannot see holding
    /// the keyboard is read from the window itself, never from a desktop index. A landing on a shell
    /// route or on another window of the same app is the person's or the app's own doing. A fault
    /// is reported when MultiDesk sent focus away from a hand-picked window, and when another
    /// process holds the foreground after a switch.
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

                // Alt+Tab, Start, the taskbar or the tray overflow holding the foreground is the person
                // opening a shell route again while this settled.
                if (ShellRoutes.RouteOf(fg) != null)
                {
                    Diag.Detail(DiagChannels.Fg, "landed-on-shell-route", where);
                    return;
                }

                // Focus resting where MultiDesk itself sent it means MultiDesk decided the outcome.
                if (steered != IntPtr.Zero && fg == steered)
                {
                    if (viaTaskChooser)
                    {
                        // A window the user picked by hand in Alt+Tab, with focus sent somewhere else
                        // by MultiDesk. The outcome names which decision produced it.
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

                Diag.Detail(DiagChannels.Fg, "focus-moved-on-by-something-else", where
                    + " steered=" + (steered == IntPtr.Zero ? "none" : DiagFormat.H(steered)));
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
