using System;
using MultiDesk.Interop;
using MultiDesk.Models;
using NM = MultiDesk.Interop.NativeMethods;

namespace MultiDesk.Services
{
    /// <summary>
    /// Per-window operations. The headline is Activate, which fixes the multi-press focus problem by
    /// switching to the window's desktop first and then forcing focus through the documented
    /// foreground-lock workaround, so a single action always lands focus on the target.
    /// </summary>
    internal static class WindowActions
    {
        /// <summary>Bring a tracked window forward reliably in one action.</summary>
        public static void Activate(WindowModel w)
        {
            if (w == null) return;
            // Desktop switch first and synchronously, so focus is set after the target is visible.
            MultiDesk.App.Desktops?.SwitchTo(w.DesktopIndex, false);
            ForceForeground(w.Hwnd);
        }

        /// <summary>Tile click behavior, decided from live window state so it never misfires on a stale
        /// flag: restore a minimized window, minimize the one that is already active, otherwise bring it
        /// forward.</summary>
        public static void Toggle(WindowModel w)
        {
            if (w == null || w.Hwnd == IntPtr.Zero || !NM.IsWindow(w.Hwnd)) return;
            if (NM.IsIconic(w.Hwnd)) { Activate(w); return; }
            if (w.IsActive) { Minimize(w.Hwnd); return; }
            Activate(w);
        }

        /// <summary>Force a window to the foreground, defeating the foreground lock. This is the core of
        /// the single-press fix and is reused by the desktop switch and the cross-desktop switcher.</summary>
        public static void ForceForeground(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || !NM.IsWindow(hwnd)) return;
            try
            {
                if (NM.IsIconic(hwnd)) NM.ShowWindow(hwnd, NM.SW_RESTORE);
                else NM.ShowWindow(hwnd, NM.SW_SHOW);

                IntPtr fore = NM.GetForegroundWindow();
                if (fore == hwnd) return;

                uint thisThread = NM.GetCurrentThreadId();
                uint dummy;
                uint foreThread = fore != IntPtr.Zero ? NM.GetWindowThreadProcessId(fore, out dummy) : 0;
                uint targetThread = NM.GetWindowThreadProcessId(hwnd, out dummy);

                // Drop the foreground lock timeout so the request is honored, restored afterward.
                uint oldTimeout = 0;
                bool gotTimeout = NM.SystemParametersInfo(NM.SPI_GETFOREGROUNDLOCKTIMEOUT, 0, ref oldTimeout, 0);
                uint zero = 0;
                NM.SystemParametersInfo(NM.SPI_SETFOREGROUNDLOCKTIMEOUT, 0, ref zero, NM.SPIF_SENDCHANGE);

                bool attFore = false, attTarget = false;
                if (foreThread != 0 && foreThread != thisThread)
                    attFore = NM.AttachThreadInput(thisThread, foreThread, true);
                if (targetThread != 0 && targetThread != thisThread && targetThread != foreThread)
                    attTarget = NM.AttachThreadInput(thisThread, targetThread, true);

                // A synthetic Alt tap marks our thread as having received input, which the lock rules
                // require before SetForegroundWindow on another thread's window is allowed.
                NM.keybd_event(NM.VK_MENU, 0, 0, UIntPtr.Zero);
                NM.keybd_event(NM.VK_MENU, 0, NM.KEYEVENTF_KEYUP, UIntPtr.Zero);

                NM.BringWindowToTop(hwnd);
                NM.SetForegroundWindow(hwnd);
                NM.SetActiveWindow(hwnd);
                NM.SetFocus(hwnd);

                if (attTarget) NM.AttachThreadInput(thisThread, targetThread, false);
                if (attFore) NM.AttachThreadInput(thisThread, foreThread, false);

                if (gotTimeout)
                {
                    uint restore = oldTimeout;
                    NM.SystemParametersInfo(NM.SPI_SETFOREGROUNDLOCKTIMEOUT, 0, ref restore, NM.SPIF_SENDCHANGE);
                }
            }
            catch (Exception ex) { Log.Error("ForceForeground", ex); }
        }

        public static void Minimize(IntPtr h) { Safe(h, () => NM.ShowWindow(h, NM.SW_MINIMIZE)); }
        public static void Maximize(IntPtr h) { Safe(h, () => NM.ShowWindow(h, NM.SW_MAXIMIZE)); }
        public static void Restore(IntPtr h) { Safe(h, () => NM.ShowWindow(h, NM.SW_RESTORE)); }
        public static void Close(IntPtr h) { Safe(h, () => NM.PostMessage(h, NM.WM_CLOSE, IntPtr.Zero, IntPtr.Zero)); }

        public static void SendToBottom(IntPtr h)
        {
            Safe(h, () => NM.SetWindowPos(h, NM.HWND_BOTTOM, 0, 0, 0, 0,
                NM.SWP_NOMOVE | NM.SWP_NOSIZE | NM.SWP_NOACTIVATE));
        }

        public static void SetAlwaysOnTop(IntPtr h, bool on)
        {
            Safe(h, () => NM.SetWindowPos(h, on ? NM.HWND_TOPMOST : NM.HWND_NOTOPMOST, 0, 0, 0, 0,
                NM.SWP_NOMOVE | NM.SWP_NOSIZE | NM.SWP_NOACTIVATE));
        }

        /// <summary>Set window opacity as a percentage from 40 to 100. The layered style is added once
        /// and left in place, with alpha 255 used to restore a fully opaque look.</summary>
        public static void SetOpacity(IntPtr h, int percent)
        {
            Safe(h, () =>
            {
                long ex = NM.GetWindowLongPtr(h, NM.GWL_EXSTYLE).ToInt64();
                if ((ex & NM.WS_EX_LAYERED) == 0)
                    NM.SetWindowLongPtr(h, NM.GWL_EXSTYLE, new IntPtr(ex | NM.WS_EX_LAYERED));
                if (percent < 40) percent = 40;
                if (percent > 100) percent = 100;
                byte a = (byte)(percent * 255 / 100);
                NM.SetLayeredWindowAttributes(h, 0, a, NM.LWA_ALPHA);
            });
        }

        private static void Safe(IntPtr h, Action a)
        {
            if (h == IntPtr.Zero || !NM.IsWindow(h)) return;
            try { a(); } catch (Exception ex) { Log.Error("window action", ex); }
        }
    }
}
