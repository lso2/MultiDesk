using System;
using NM = MultiDesk.Interop.NativeMethods;

namespace MultiDesk.Services
{
    /// <summary>
    /// The launch desktop. Defines StartupDesktop, the desktop MultiDesk starts on, and RecordActive,
    /// which copies the active desktop into settings.json for the next launch.
    /// Doc: docs/notes/MultiDesk/Services/DesktopManager.Startup.cs.md
    /// </summary>
    public sealed partial class DesktopManager
    {
        // Order: the foreground window's desktop, then the last session's active desktop, then
        // desktop 0. reason is the name of the rule that applied.
        private int StartupDesktop(out string reason, out IntPtr foreground)
        {
            foreground = NM.GetForegroundWindow();
            if (foreground != IntPtr.Zero)
            {
                // A dialog in the foreground counts as the window that owns it.
                IntPtr root = NM.GetAncestor(foreground, NM.GA_ROOTOWNER);
                if (root != IntPtr.Zero) foreground = root;
            }
            int index = LaunchDesktopOf(foreground);
            if (index >= 0) { reason = "foreground-window"; return index; }
            index = _settings.Current.ActiveDesktop;
            if (index >= 0 && index < Desktops.Count) { reason = "last-session"; return index; }
            reason = "first-desktop";
            return 0;
        }

        // The desktop assigned to this window at discovery by its app pin or remembered placement.
        // The placement stays in the list for discovery. -1 when neither applies.
        private int LaunchDesktopOf(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return -1;
            uint pid;
            NM.GetWindowThreadProcessId(hwnd, out pid);
            if (pid == 0) return -1;
            string exe = NM.ExePathForPid(pid);
            string profile = ProcessInfo.ProfileArg(pid, exe);
            int pin = PinIndexFor(exe, profile);
            return pin >= 0 ? pin : PeekPlacement(exe, profile, NM.TextOf(hwnd));
        }

        // Copies the active desktop into settings for the next launch. write saves settings.json when
        // the value changed. Without write, the value is saved with the next settings write.
        private void RecordActive(bool write)
        {
            bool changed = _settings.Current.ActiveDesktop != ActiveIndex;
            _settings.Current.ActiveDesktop = ActiveIndex;
            if (changed && write) _settings.Persist();
        }
    }
}
