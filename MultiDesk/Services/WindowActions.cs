using System;
using MultiDesk.Interop;
using MultiDesk.Models;
using NM = MultiDesk.Interop.NativeMethods;

namespace MultiDesk.Services
{
    /// <summary>
    /// Per-window operations. Defines WindowActions: activate, minimize, close, raise and the rest
    /// of the actions the bar and the desktop manager run on a window. Activate switches to
    /// the window's desktop first and then takes the foreground past the system's foreground lock,
    /// so one click lands focus on the target.
    /// Doc: docs/notes/MultiDesk/Services/WindowActions.cs.md
    /// </summary>
    internal static class WindowActions
    {
        /// <summary>Bring a tracked window forward reliably in one action.</summary>
        public static void Activate(WindowModel w)
        {
            if (w == null) return;
            try
            {
                // Desktop switch first and synchronously, so focus is set after the target is visible.
                MultiDesk.App.Desktops?.SwitchTo(w.DesktopIndex, "window-activate", false);
                ForceForeground(w.Hwnd);
            }
            catch (Exception ex) { Log.Error("activate", ex); }
        }

        /// <summary>Tile click behavior, decided from live window state so it never misfires on a stale
        /// flag: restore a minimized window, minimize the one that is already active, otherwise bring it
        /// forward.</summary>
        public static void Toggle(WindowModel w)
        {
            if (w == null || w.Hwnd == IntPtr.Zero || !NM.IsWindow(w.Hwnd)) return;
            try
            {
                if (NM.IsIconic(w.Hwnd)) { Activate(w); return; }
                if (w.IsActive) { Minimize(w.Hwnd); return; }
                Activate(w);
            }
            catch (Exception ex) { Log.Error("toggle", ex); }
        }

        /// <summary>Force a window to the foreground, defeating the foreground lock. Shows or restores
        /// the window first. Used by Activate, the desktop switch and every focus hand-back.</summary>
        public static void ForceForeground(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || !NM.IsWindow(hwnd)) return;
            try
            {
                // A hung window cannot run the focus dance in TakeForeground, and attaching this
                // thread's input queue to a hung thread hangs this thread with it, which reads as the
                // whole bar, and every desktop switch with it, freezing. One async show and one plain
                // request are safe; whatever they miss is retried by the next real interaction once
                // the window answers again.
                if (NM.IsHungAppWindow(hwnd))
                {
                    NM.ShowWindowAsync(hwnd, NM.IsIconic(hwnd) ? NM.SW_RESTORE : NM.SW_SHOW);
                    NM.SetForegroundWindow(hwnd);
                    return;
                }

                if (NM.IsIconic(hwnd)) NM.ShowWindow(hwnd, NM.SW_RESTORE);
                else NM.ShowWindow(hwnd, NM.SW_SHOW);

                if (NM.GetForegroundWindow() == hwnd) return;
                TakeForeground(hwnd, true);
            }
            catch (Exception ex) { Log.Error("ForceForeground", ex); }
        }

        /// <summary>Focus the Windows desktop, for an active desktop that shows no window. The desktop
        /// is activated only, never raised above other windows or shown.</summary>
        public static void FocusDesktop(IntPtr shell)
        {
            if (shell == IntPtr.Zero || !NM.IsWindow(shell)) return;
            try
            {
                if (NM.GetForegroundWindow() == shell) return;
                // A hung Explorer gets one plain request, for the reason ForceForeground gives.
                if (NM.IsHungAppWindow(shell)) { NM.SetForegroundWindow(shell); return; }
                TakeForeground(shell, false);
            }
            catch (Exception ex) { Log.Error("FocusDesktop", ex); }
        }

        // Takes the foreground for hwnd past the foreground lock, with this thread's input queue
        // attached to the foreground and target threads and a synthetic Alt tap. raise also brings
        // the window to the top of the z-order and gives it the keyboard focus directly. Windows'
        // foreground lock timeout is a system-wide setting and is left as the user set it.
        private static void TakeForeground(IntPtr hwnd, bool raise)
        {
            IntPtr fore = NM.GetForegroundWindow();
            uint thisThread = NM.GetCurrentThreadId();
            uint dummy;
            uint foreThread = fore != IntPtr.Zero && !NM.IsHungAppWindow(fore)
                ? NM.GetWindowThreadProcessId(fore, out dummy) : 0;
            uint targetThread = NM.GetWindowThreadProcessId(hwnd, out dummy);

            bool attFore = false, attTarget = false;
            if (foreThread != 0 && foreThread != thisThread)
                attFore = NM.AttachThreadInput(thisThread, foreThread, true);
            if (targetThread != 0 && targetThread != thisThread && targetThread != foreThread)
                attTarget = NM.AttachThreadInput(thisThread, targetThread, true);

            // A synthetic Alt tap marks our thread as having received input, which the lock rules
            // require before SetForegroundWindow on another thread's window is allowed.
            NM.keybd_event(NM.VK_MENU, 0, 0, UIntPtr.Zero);
            NM.keybd_event(NM.VK_MENU, 0, NM.KEYEVENTF_KEYUP, UIntPtr.Zero);

            if (raise) NM.BringWindowToTop(hwnd);
            NM.SetForegroundWindow(hwnd);
            if (raise)
            {
                NM.SetActiveWindow(hwnd);
                NM.SetFocus(hwnd);
            }

            if (attTarget) NM.AttachThreadInput(thisThread, targetThread, false);
            if (attFore) NM.AttachThreadInput(thisThread, foreThread, false);
        }

        // Minimizing or closing the active window hands Windows' own foreground to whatever it judges
        // is next in line, which is not a desktop the user chose to visit. Focus-follow is suppressed
        // for a moment so that automatic hand-off can never read as "switch desktops."
        public static void Minimize(IntPtr h)
        {
            MultiDesk.App.Desktops?.SuppressFocusFollow();
            DiagPark.Minimized(h, "bar");
            Safe(h, () => NM.ShowWindow(h, NM.SW_MINIMIZE));
        }
        public static void Maximize(IntPtr h) { Safe(h, () => NM.ShowWindow(h, NM.SW_MAXIMIZE)); }
        public static void Restore(IntPtr h) { Safe(h, () => NM.ShowWindow(h, NM.SW_RESTORE)); }
        public static void Close(IntPtr h)
        {
            MultiDesk.App.Desktops?.SuppressFocusFollow();
            Safe(h, () => NM.PostMessage(h, NM.WM_CLOSE, IntPtr.Zero, IntPtr.Zero));
        }

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
