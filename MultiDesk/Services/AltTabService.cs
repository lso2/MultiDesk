using System;
using System.Runtime.InteropServices;
using System.Windows;
using MultiDesk.Interop;
using NM = MultiDesk.Interop.NativeMethods;

namespace MultiDesk.Services
{
    /// <summary>
    /// Optional "Alt+Tab across all desktops". While the user holds Alt and presses Tab, every desktop's
    /// windows are revealed so the system switcher lists them all; releasing Alt switches to whichever
    /// window was chosen and re-hides the rest. The hook only observes keys and always calls the next
    /// hook, so it can never swallow or break the normal Alt+Tab. Driven by settings.AltTabAllDesktops.
    /// </summary>
    public sealed class AltTabService : IDisposable
    {
        private readonly SettingsStore _settings;
        private readonly NM.LowLevelKeyboardProc _proc; // held so the GC cannot collect the callback
        private IntPtr _hook;
        private bool _altDown;
        private bool _session;

        public AltTabService(SettingsStore settings)
        {
            _settings = settings;
            _proc = HookProc;
        }

        /// <summary>Install or remove the hook to match the current setting.</summary>
        public void Apply()
        {
            bool want = _settings != null && _settings.Current != null && _settings.Current.AltTabAllDesktops;
            if (want && _hook == IntPtr.Zero) Install();
            else if (!want && _hook != IntPtr.Zero) Uninstall();
        }

        private void Install()
        {
            try
            {
                _hook = NM.SetWindowsHookEx(NM.WH_KEYBOARD_LL, _proc, NM.GetModuleHandle(null), 0);
                if (_hook == IntPtr.Zero) Log.Info("Alt+Tab hook did not install.");
                else Log.Info("Alt+Tab across desktops enabled.");
            }
            catch (Exception ex) { Log.Error("alt-tab hook install", ex); }
        }

        private void Uninstall()
        {
            try { if (_hook != IntPtr.Zero) NM.UnhookWindowsHookEx(_hook); }
            catch (Exception ex) { Log.Error("alt-tab hook remove", ex); }
            _hook = IntPtr.Zero;
            _altDown = false;
            _session = false;
        }

        private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            // Observe only. Any exception is swallowed so the keyboard is never affected.
            try
            {
                if (nCode >= 0)
                {
                    int msg = wParam.ToInt32();
                    int vk = Marshal.ReadInt32(lParam); // KBDLLHOOKSTRUCT.vkCode is the first field
                    bool down = msg == NM.WM_KEYDOWN || msg == NM.WM_SYSKEYDOWN;
                    bool up = msg == NM.WM_KEYUP || msg == NM.WM_SYSKEYUP;
                    bool isAlt = vk == NM.VK_MENU || vk == NM.VK_LMENU || vk == NM.VK_RMENU;

                    if (isAlt)
                    {
                        if (down) _altDown = true;
                        else if (up && _altDown)
                        {
                            _altDown = false;
                            if (_session) { _session = false; Post(EndSession); }
                        }
                    }
                    else if (vk == NM.VK_TAB && down && _altDown && !_session)
                    {
                        _session = true;
                        // Reveal synchronously, on this hook call, before Windows builds the Alt+Tab list,
                        // so every desktop's windows are in it. Posting it would run too late to be included.
                        try { if (App.Desktops != null) App.Desktops.BeginAltTab(); }
                        catch (Exception ex) { Log.Error("alt-tab begin", ex); }
                    }
                }
            }
            catch (Exception ex) { Log.Error("alt-tab hook proc", ex); }
            return NM.CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        private static void Post(Action a)
        {
            var app = Application.Current;
            if (app != null) app.Dispatcher.BeginInvoke(a);
        }

        private static void EndSession()
        {
            try { if (App.Desktops != null) App.Desktops.EndAltTab(); }
            catch (Exception ex) { Log.Error("alt-tab end", ex); }
        }

        public void Dispose() { Uninstall(); }
    }
}
