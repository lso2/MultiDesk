using System;
using System.Globalization;
using NM = MultiDesk.Interop.NativeMethods;

namespace MultiDesk.Services
{
    /// <summary>
    /// Value formatting for trace lines. Defines DiagFormat, which renders handles, rectangles,
    /// placement states, live window flags and monitor work areas in one compact spelling, so two
    /// lines written months apart compare by eye and by grep. Reads window state only; changes
    /// nothing.
    /// Doc: docs/notes/MultiDesk/Services/Diag.md
    /// </summary>
    internal static class DiagFormat
    {
        public static string H(IntPtr h)
        {
            return "0x" + h.ToInt64().ToString("X", CultureInfo.InvariantCulture);
        }

        /// <summary>A rectangle as position and size, the two things a restore has to get right.</summary>
        public static string R(NM.RECT r)
        {
            return r.Left.ToString(CultureInfo.InvariantCulture) + "," + r.Top.ToString(CultureInfo.InvariantCulture)
                + " " + r.Width.ToString(CultureInfo.InvariantCulture) + "x" + r.Height.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>The WINDOWPLACEMENT show state by name. A window restored with the wrong one is
        /// the difference between genuinely maximized and merely the size of the screen.</summary>
        public static string Show(int showCmd)
        {
            switch (showCmd)
            {
                case NM.SW_HIDE: return "hidden";
                case NM.SW_SHOWNORMAL: return "normal";
                case NM.SW_SHOWMAXIMIZED: return "maximized";
                case NM.SW_SHOWNOACTIVATE: return "normal-noactivate";
                case NM.SW_SHOW: return "shown";
                case NM.SW_MINIMIZE: return "minimized";
                case NM.SW_SHOWMINNOACTIVE: return "minimized-noactivate";
                case NM.SW_SHOWNA: return "shown-noactivate";
                case NM.SW_RESTORE: return "restored";
                default: return "showcmd" + showCmd.ToString(CultureInfo.InvariantCulture);
            }
        }

        /// <summary>The live state bits, each present only when set, so a line stays short.</summary>
        public static string Flags(IntPtr h)
        {
            if (h == IntPtr.Zero || !NM.IsWindow(h)) return "gone";
            var s = "";
            try
            {
                if (NM.IsWindowVisible(h)) s += "visible ";
                if (NM.IsIconic(h)) s += "minimized ";
                if (NM.IsZoomed(h)) s += "maximized ";
                if (NM.IsCloaked(h)) s += "cloaked ";
                if (NM.IsHungAppWindow(h)) s += "hung ";
            }
            catch { }
            return s.Length > 0 ? s.TrimEnd() : "plain";
        }

        /// <summary>The monitor a window sits on, as both rectangles. A maximized window derives its
        /// size from the work area and a fullscreen one from the whole monitor, so telling those two
        /// apart from a plain window that merely got resized to the same numbers needs both.</summary>
        public static bool MonitorOf(IntPtr h, out NM.RECT work, out NM.RECT full, out IntPtr monitor)
        {
            work = new NM.RECT();
            full = new NM.RECT();
            monitor = IntPtr.Zero;
            try
            {
                monitor = NM.MonitorFromWindow(h, NM.MONITOR_DEFAULTTONEAREST);
                if (monitor == IntPtr.Zero) return false;
                var mi = new NM.MONITORINFO();
                mi.cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(NM.MONITORINFO));
                if (!NM.GetMonitorInfo(monitor, ref mi)) return false;
                work = mi.rcWork;
                full = mi.rcMonitor;
                return true;
            }
            catch { return false; }
        }

        /// <summary>A title cut to a readable length and quoted, so a line never runs away.</summary>
        public static string Q(string title)
        {
            if (title == null) return "\"\"";
            var t = title.Replace('\r', ' ').Replace('\n', ' ');
            if (t.Length > 60) t = t.Substring(0, 57) + "...";
            return "\"" + t + "\"";
        }

        /// <summary>True when two rectangles agree within the given pixel tolerance on all four sides,
        /// which absorbs the one-pixel differences DPI rounding produces on a legitimate restore.</summary>
        public static bool Near(NM.RECT a, NM.RECT b, int tolerance)
        {
            return Math.Abs(a.Left - b.Left) <= tolerance && Math.Abs(a.Top - b.Top) <= tolerance
                && Math.Abs(a.Right - b.Right) <= tolerance && Math.Abs(a.Bottom - b.Bottom) <= tolerance;
        }

        public static string Ms(int since)
        {
            return (Environment.TickCount - since).ToString(CultureInfo.InvariantCulture) + "ms";
        }
    }
}
