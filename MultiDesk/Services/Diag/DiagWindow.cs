using System;
using NM = MultiDesk.Interop.NativeMethods;

namespace MultiDesk.Services
{
    /// <summary>
    /// Window state capture. Defines DiagWinState, one reading of everything a restore has to get
    /// right, and DiagWindow, which takes those readings and names the differences between two of
    /// them. Coverage is the reading that matters most: a window filling its monitor because Windows
    /// maximized it and a window merely resized to the same numbers look identical on screen and
    /// behave differently in every other way.
    /// Doc: docs/notes/MultiDesk/Services/Diag.md
    /// </summary>
    internal struct DiagWinState
    {
        public IntPtr Hwnd;
        public bool Alive, Visible, Iconic, Zoomed, Cloaked;
        public NM.RECT Rect;
        public bool HasPlacement;
        public int ShowCmd, PlacementFlags;
        public NM.RECT Normal;
        public bool HasMonitor;
        public IntPtr Monitor;
        public NM.RECT Work, Full;
    }

    internal static class DiagWindow
    {
        // How far a rectangle may sit from a monitor edge and still count as covering it. Shadow
        // borders and DPI rounding routinely account for a few pixels on a genuinely maximized window.
        private const int CoverTolerance = 8;

        public static DiagWinState Capture(IntPtr h)
        {
            var s = new DiagWinState();
            s.Hwnd = h;
            try
            {
                s.Alive = h != IntPtr.Zero && NM.IsWindow(h);
                if (!s.Alive) return s;
                s.Visible = NM.IsWindowVisible(h);
                s.Iconic = NM.IsIconic(h);
                s.Zoomed = NM.IsZoomed(h);
                s.Cloaked = NM.IsCloaked(h);
                NM.RECT r;
                if (NM.GetWindowRect(h, out r)) s.Rect = r;
                var p = NM.NewPlacement();
                if (NM.GetWindowPlacement(h, ref p))
                {
                    s.HasPlacement = true;
                    s.ShowCmd = p.showCmd;
                    s.PlacementFlags = p.flags;
                    s.Normal = p.rcNormalPosition;
                }
                NM.RECT work, full;
                IntPtr mon;
                if (DiagFormat.MonitorOf(h, out work, out full, out mon))
                {
                    s.HasMonitor = true;
                    s.Monitor = mon;
                    s.Work = work;
                    s.Full = full;
                }
            }
            catch { }
            return s;
        }

        /// <summary>How much of its monitor this window occupies, and by what means. The four answers
        /// are distinct states a user can tell apart on sight, and "screen-sized" is the one nobody
        /// asks for: the window fills the work area while Windows still considers it an ordinary
        /// resizable window, so it has lost the maximized state it was left in.</summary>
        public static string Coverage(DiagWinState s)
        {
            if (!s.Alive) return "gone";
            if (s.Iconic) return "minimized";
            if (s.Zoomed) return "maximized";
            if (!s.HasMonitor) return "normal";
            if (DiagFormat.Near(s.Rect, s.Full, CoverTolerance)) return "fullscreen";
            if (DiagFormat.Near(s.Rect, s.Work, CoverTolerance)) return "screen-sized";
            return "normal";
        }

        public static string Describe(DiagWinState s)
        {
            if (!s.Alive) return "gone";
            var text = "rect=" + DiagFormat.R(s.Rect) + " cover=" + Coverage(s) + " state=" + StateOf(s);
            if (s.HasPlacement)
                text += " show=" + DiagFormat.Show(s.ShowCmd) + " normal=" + DiagFormat.R(s.Normal)
                    + ((s.PlacementFlags & NM.WPF_RESTORETOMAXIMIZED) != 0 ? " restore-to-maximized" : "");
            if (s.HasMonitor)
                text += " mon=" + DiagFormat.H(s.Monitor) + " work=" + DiagFormat.R(s.Work);
            return text;
        }

        // The state flags as the reading captured them, in the order DiagFormat.Flags writes live ones.
        private static string StateOf(DiagWinState s)
        {
            var text = "";
            if (s.Visible) text += "visible ";
            if (s.Iconic) text += "minimized ";
            if (s.Zoomed) text += "maximized ";
            if (s.Cloaked) text += "cloaked ";
            return text.Length > 0 ? text.TrimEnd() : "plain";
        }

        /// <summary>Everything that changed between two readings of one window, named. An empty
        /// result means the second reading matches the first on every count that was compared.</summary>
        public static string Differences(DiagWinState before, DiagWinState after)
        {
            var text = "";
            if (!after.Alive) return "gone";
            string cb = Coverage(before), ca = Coverage(after);
            if (cb != ca) text += " cover=" + cb + "->" + ca;
            if (before.HasPlacement && after.HasPlacement && before.ShowCmd != after.ShowCmd)
                text += " show=" + DiagFormat.Show(before.ShowCmd) + "->" + DiagFormat.Show(after.ShowCmd);
            if (!DiagFormat.Near(before.Rect, after.Rect, 2))
                text += " rect=" + DiagFormat.R(before.Rect) + "->" + DiagFormat.R(after.Rect);
            if (before.HasMonitor && after.HasMonitor && before.Monitor != after.Monitor)
                text += " mon=" + DiagFormat.H(before.Monitor) + "->" + DiagFormat.H(after.Monitor);
            if (before.Iconic != after.Iconic)
                text += " minimized=" + (before.Iconic ? "1" : "0") + "->" + (after.Iconic ? "1" : "0");
            if (before.Visible != after.Visible)
                text += " visible=" + (before.Visible ? "1" : "0") + "->" + (after.Visible ? "1" : "0");
            return text.Length > 0 ? text.Substring(1) : "";
        }

        /// <summary>True when no monitor is showing this window right now, read from the window
        /// itself rather than from any bookkeeping: gone, hidden, minimized, or standing at a
        /// position that intersects none of the monitors. This is the reading that says whether
        /// keystrokes are landing somewhere the user cannot see.</summary>
        public static bool NoMonitorShows(IntPtr h)
        {
            try
            {
                if (h == IntPtr.Zero || !NM.IsWindow(h)) return true;
                if (!NM.IsWindowVisible(h) || NM.IsIconic(h) || NM.IsCloaked(h)) return true;
                NM.RECT r;
                if (!NM.GetWindowRect(h, out r)) return false;
                int vx = NM.GetSystemMetrics(NM.SM_XVIRTUALSCREEN);
                int vy = NM.GetSystemMetrics(NM.SM_YVIRTUALSCREEN);
                int vw = NM.GetSystemMetrics(NM.SM_CXVIRTUALSCREEN);
                int vh = NM.GetSystemMetrics(NM.SM_CYVIRTUALSCREEN);
                return r.Right <= vx || r.Left >= vx + vw || r.Bottom <= vy || r.Top >= vy + vh;
            }
            catch { return false; }
        }

        /// <summary>The process behind a window, or 0 when it cannot be read. Two windows of one
        /// process passing focus between themselves is an app opening its own dialog, never a
        /// navigation fault.</summary>
        public static uint ProcessOf(IntPtr h)
        {
            try
            {
                if (h == IntPtr.Zero || !NM.IsWindow(h)) return 0;
                uint pid;
                NM.GetWindowThreadProcessId(h, out pid);
                return pid;
            }
            catch { return 0; }
        }

        /// <summary>The anomaly kind a park-and-return pair earns, or null when the return was
        /// faithful. Judged on what the user can see: the window covers a different amount of its
        /// monitor than it did, or it covers the same amount by a different means.</summary>
        public static string RestoreFault(DiagWinState before, DiagWinState after)
        {
            if (!before.Alive || !after.Alive) return null;
            if (after.Iconic && !before.Iconic) return "restored-minimized";
            if (after.Iconic || before.Iconic) return null; // a minimize the user performed is theirs
            string cb = Coverage(before), ca = Coverage(after);
            if (cb == "maximized" && ca == "screen-sized") return "maximized-returned-screen-sized";
            if (cb == "fullscreen" && ca == "screen-sized") return "fullscreen-returned-screen-sized";
            if (cb == "maximized" && ca == "normal") return "maximized-returned-normal";
            if (cb == "fullscreen" && ca != "fullscreen") return "fullscreen-returned-" + ca;
            if (cb != ca) return "coverage-changed";
            if (cb == "normal" && !DiagFormat.Near(before.Rect, after.Rect, 2)) return "rect-changed";
            if (before.HasMonitor && after.HasMonitor && before.Monitor != after.Monitor) return "monitor-changed";
            return null;
        }
    }
}
