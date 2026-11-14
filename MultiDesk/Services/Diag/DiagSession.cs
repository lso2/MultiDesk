using System;
using System.Globalization;
using MultiDesk.Models;
using NM = MultiDesk.Interop.NativeMethods;

namespace MultiDesk.Services
{
    /// <summary>
    /// The session banner. Defines DiagSession, which opens the trace with the version, the screen
    /// layout and every setting that changes how navigation behaves, and closes it with the counts
    /// that were held back. A trace read weeks later is only worth reading if it says what it was
    /// recorded on.
    /// Doc: docs/notes/MultiDesk/Services/Diag.md
    /// </summary>
    internal static class DiagSession
    {
        public static void Open(MultiDeskSettings s)
        {
            if (!Diag.Enabled) return;
            try
            {
                Diag.Raw("");
                Diag.Raw("=== MultiDesk " + AppVersion.Display + " session " + Diag.SessionId
                    + " started " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " ===");
                Diag.Raw("    windows=" + Environment.OSVersion.Version
                    + " 64bit=" + (Environment.Is64BitOperatingSystem ? "yes" : "no")
                    + " screens=" + NM.GetSystemMetrics(NM.SM_CXSCREEN) + "x" + NM.GetSystemMetrics(NM.SM_CYSCREEN)
                    + " virtual=" + NM.GetSystemMetrics(NM.SM_CXVIRTUALSCREEN) + "x" + NM.GetSystemMetrics(NM.SM_CYVIRTUALSCREEN)
                    + " at " + NM.GetSystemMetrics(NM.SM_XVIRTUALSCREEN) + "," + NM.GetSystemMetrics(NM.SM_YVIRTUALSCREEN));
                if (s == null) return;
                Diag.Raw("    alt-tab-all-desktops=" + (s.AltTabAllDesktops ? "on" : "off")
                    + " follow-focus=" + (s.AutoSwitchOnForeground ? "on" : "off")
                    + " remember-placement=" + (s.RememberPlacement ? "on" : "off")
                    + " trim-hidden=" + (s.TrimHiddenMemory ? "on" : "off")
                    + " desktops=" + s.DesktopCount);
                Diag.Raw("    dock=" + s.DockEdge + " thickness=" + s.BarThicknessPx + "px"
                    + " icon=" + s.IconSizePx + "px rows=" + s.RowsPerDesktop
                    + " auto-hide=" + (s.AutoHide ? "on" : "off")
                    + " reserve-space=" + (s.ReserveSpace ? "on" : "off")
                    + " always-on-top=" + (s.AlwaysOnTop ? "on" : "off")
                    + " verbose=" + (Diag.Verbose ? "on" : "off"));
            }
            catch (Exception ex) { Log.Error("diagnostic session banner", ex); }
        }

        public static void Close()
        {
            if (!Diag.Enabled) return;
            try
            {
                DiagBug.Flush();
                Diag.Raw("=== session " + Diag.SessionId + " ended "
                    + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " ===");
            }
            catch { }
        }
    }
}
