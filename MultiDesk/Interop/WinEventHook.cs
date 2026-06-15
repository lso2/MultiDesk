using System;
using MultiDesk.Services;

namespace MultiDesk.Interop
{
    /// <summary>
    /// Managed wrapper around SetWinEventHook. Installed WINEVENT_OUTOFCONTEXT so callbacks marshal
    /// to the message loop of the creating thread (the WPF UI thread). The delegate is held in a field
    /// so the GC cannot collect it while the unmanaged hook still references it.
    /// </summary>
    internal sealed class WinEventHook : IDisposable
    {
        public event Action<uint, IntPtr, int, int> Event;

        private readonly NativeMethods.WinEventDelegate _proc;
        private IntPtr _handle;
        private readonly uint _min;
        private readonly uint _max;

        public WinEventHook(uint eventMin, uint eventMax)
        {
            _min = eventMin;
            _max = eventMax;
            _proc = OnWinEvent;
        }

        public bool Install()
        {
            _handle = NativeMethods.SetWinEventHook(
                _min, _max, IntPtr.Zero, _proc, 0, 0,
                NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);

            if (_handle == IntPtr.Zero)
                Log.Info("SetWinEventHook failed for range " + _min.ToString("X") + "-" + _max.ToString("X"));
            return _handle != IntPtr.Zero;
        }

        private void OnWinEvent(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
            int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
        {
            var handler = Event;
            if (handler == null) return;
            try { handler(eventType, hwnd, idObject, idChild); }
            catch (Exception ex)
            {
                // A throw here would unwind into unmanaged code, so swallow and record.
                Log.Error("WinEvent handler threw", ex);
            }
        }

        public void Dispose()
        {
            if (_handle != IntPtr.Zero)
            {
                NativeMethods.UnhookWinEvent(_handle);
                _handle = IntPtr.Zero;
            }
        }
    }
}
