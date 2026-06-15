using System;
using System.Windows.Interop;
using NM = MultiDesk.Interop.NativeMethods;

namespace MultiDesk.Services
{
    /// <summary>
    /// Optional global hotkey (Ctrl+Alt+D) that opens the cross-desktop switcher. Off by default to
    /// keep the first build's surface small. Registers on the bar's window so WM_HOTKEY is delivered
    /// to a message hook there.
    /// </summary>
    internal sealed class HotkeyService : IDisposable
    {
        private const int HotkeyId = 0xB001;
        private const uint VK_D = 0x44;

        private readonly IntPtr _h;
        private readonly SettingsStore _s;
        private readonly Action _onHotkey;

        private HwndSource _src;
        private bool _registered;
        private bool _hooked;

        public HotkeyService(IntPtr hwnd, SettingsStore settings, Action onHotkey)
        {
            _h = hwnd;
            _s = settings;
            _onHotkey = onHotkey;
        }

        public void Apply()
        {
            bool want = _s.Current.SwitcherHotkeyEnabled;
            if (want && !_registered) Register();
            else if (!want && _registered) Unregister();
        }

        private void Register()
        {
            if (!_hooked)
            {
                _src = HwndSource.FromHwnd(_h);
                if (_src != null) { _src.AddHook(WndProc); _hooked = true; }
            }
            _registered = NM.RegisterHotKey(_h, HotkeyId, NM.MOD_CONTROL | NM.MOD_ALT | NM.MOD_NOREPEAT, VK_D);
            if (!_registered) Log.Info("RegisterHotKey Ctrl+Alt+D failed (already in use).");
        }

        private void Unregister()
        {
            if (_registered) { NM.UnregisterHotKey(_h, HotkeyId); _registered = false; }
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == NM.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
            {
                try { if (_onHotkey != null) _onHotkey(); }
                catch (Exception ex) { Log.Error("hotkey invoke", ex); }
                handled = true;
            }
            return IntPtr.Zero;
        }

        public void Dispose()
        {
            try { Unregister(); } catch { }
            try { if (_src != null && _hooked) _src.RemoveHook(WndProc); } catch { }
        }
    }
}
