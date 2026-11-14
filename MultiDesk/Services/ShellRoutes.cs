using System;
using System.IO;
using System.Runtime.InteropServices;
using NM = MultiDesk.Interop.NativeMethods;

namespace MultiDesk.Services
{
    /// <summary>
    /// Shell routes. Defines ShellRoutes, which names the part of the Windows shell a window belongs
    /// to when a person picks windows through it: Alt+Tab, the taskbar, the tray overflow, or Start
    /// with its search, notification and jump list panes. A window on another desktop that takes the
    /// foreground straight after one of these held it, with keyboard or mouse input that recent, was
    /// picked by that person.
    /// Doc: docs/notes/MultiDesk/Services/ShellRoutes.cs.md
    /// </summary>
    internal static class ShellRoutes
    {
        public const string AltTab = "alt-tab";
        public const string Taskbar = "taskbar";
        public const string Tray = "tray";
        public const string Start = "start";

        // A pick reaches the foreground right after the click or key release that made it. Older input
        // means the route window was only holding the foreground when another window took it.
        private const uint PickInputMs = 5000;

        // Processes whose top-level Windows.UI.Core.CoreWindow is a shell pane. Store apps draw their
        // own windows with the same class.
        private static readonly string[] ShellHosts =
        {
            "explorer.exe", "StartMenuExperienceHost.exe", "SearchHost.exe", "SearchApp.exe",
            "ShellExperienceHost.exe"
        };

        /// <summary>The route a window belongs to, or null for a window that is not part of one.</summary>
        public static string RouteOf(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return null;
            string c;
            try { c = NM.ClassOf(hwnd); } catch { return null; }
            switch (c)
            {
                case "MultitaskingViewFrame":
                case "TaskSwitcherWnd":
                case "XamlExplorerHostIslandWindow":
                case "ForegroundStaging":
                    return AltTab;
                case "Shell_TrayWnd":
                case "Shell_SecondaryTrayWnd":
                case "TaskListThumbnailWnd":
                    return Taskbar;
                case "NotifyIconOverflowWindow":
                    return Tray;
                case "Windows.UI.Core.CoreWindow":
                    return IsShellHost(hwnd) ? Start : null;
                default:
                    return null;
            }
        }

        /// <summary>True when route names a shell route and keyboard or mouse input arrived within
        /// PickInputMs: a pick through that route.</summary>
        public static bool IsPick(string route)
        {
            if (route == null) return false;
            var info = new NM.LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf(typeof(NM.LASTINPUTINFO)) };
            if (!NM.GetLastInputInfo(ref info)) return false;
            return unchecked((uint)Environment.TickCount - info.dwTime) < PickInputMs;
        }

        private static bool IsShellHost(IntPtr hwnd)
        {
            try
            {
                uint pid;
                NM.GetWindowThreadProcessId(hwnd, out pid);
                var path = NM.ExePathForPid(pid);
                if (string.IsNullOrEmpty(path)) return false;
                var exe = Path.GetFileName(path);
                foreach (var host in ShellHosts)
                    if (string.Equals(exe, host, StringComparison.OrdinalIgnoreCase)) return true;
                return false;
            }
            catch { return false; }
        }
    }
}
