using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows.Threading;
using MultiDesk.Interop;
using NM = MultiDesk.Interop.NativeMethods;

namespace MultiDesk.Services
{
    /// <summary>
    /// Discovers top-level application windows and keeps the desktop manager in sync. Window event
    /// hooks signal changes, which are debounced into a single reconcile pass so a busy moment with
    /// many windows costs one refresh. The foreground event updates the active-window highlight at once.
    /// </summary>
    public sealed class WindowTracker : IDisposable
    {
        private readonly DesktopManager _desktops;
        private readonly NM.EnumWindowsProc _enumProc;
        private readonly List<IntPtr> _scan = new List<IntPtr>();
        private readonly int _ownPid;

        private WinEventHook _foreHook;
        private WinEventHook _objHook;
        private DispatcherTimer _debounce;
        private IntPtr _shell;

        public WindowTracker(DesktopManager desktops)
        {
            _desktops = desktops;
            _enumProc = OnEnumWindow;
            _ownPid = Process.GetCurrentProcess().Id;
        }

        public void Start()
        {
            _shell = NM.GetShellWindow();

            _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
            _debounce.Tick += (s, e) => { _debounce.Stop(); Refresh(); };

            _foreHook = new WinEventHook(NM.EVENT_SYSTEM_FOREGROUND, NM.EVENT_SYSTEM_FOREGROUND);
            _foreHook.Event += OnForeground;
            _foreHook.Install();

            _objHook = new WinEventHook(NM.EVENT_OBJECT_CREATE, NM.EVENT_OBJECT_NAMECHANGE);
            _objHook.Event += OnObject;
            _objHook.Install();

            Refresh();
        }

        private void OnForeground(uint evt, IntPtr hwnd, int idObject, int idChild)
        {
            if (hwnd == IntPtr.Zero) return;
            // Ignore our own bar gaining focus (for example when a tile is clicked). Otherwise the active
            // highlight would clear on every click, making the click-to-minimize toggle a coin flip.
            uint pid;
            NM.GetWindowThreadProcessId(hwnd, out pid);
            if (pid == (uint)_ownPid) return;
            _desktops.SetForeground(hwnd);
            ScheduleRefresh();
        }

        private void OnObject(uint evt, IntPtr hwnd, int idObject, int idChild)
        {
            if (idObject != NM.OBJID_WINDOW) return;
            if (evt == NM.EVENT_OBJECT_CREATE || evt == NM.EVENT_OBJECT_DESTROY ||
                evt == NM.EVENT_OBJECT_SHOW || evt == NM.EVENT_OBJECT_NAMECHANGE)
                ScheduleRefresh();
        }

        private void ScheduleRefresh()
        {
            if (_debounce == null) return;
            _debounce.Stop();
            _debounce.Start();
        }

        /// <summary>Reconcile the tracked set with what currently exists. New qualifying windows are
        /// added to the active desktop; tracked windows whose handle is gone are removed. Windows we
        /// hid for another desktop still exist, so they are kept rather than dropped.</summary>
        public void Refresh()
        {
            try
            {
                _scan.Clear();
                NM.EnumWindows(_enumProc, IntPtr.Zero);

                foreach (var h in _scan)
                {
                    if (_desktops.IsTracked(h)) { _desktops.UpdateTitle(h, NM.TextOf(h)); continue; }
                    if (!IsAppWindow(h)) continue;
                    uint pid;
                    NM.GetWindowThreadProcessId(h, out pid);
                    string exe = NM.ExePathForPid(pid);
                    _desktops.EnsureWindow(h, NM.TextOf(h), pid, exe);
                }

                int active = _desktops.ActiveIndex;
                bool settling = _desktops.InSwitchSettle;
                var dead = new List<IntPtr>();
                foreach (var w in _desktops.AllWindows)
                {
                    if (!NM.IsWindow(w.Hwnd)) { dead.Add(w.Hwnd); continue; }
                    // A window on the visible desktop that is no longer visible was hidden by its own app
                    // (for example f.lux minimizing to the tray) or is a finished transient (an Explorer
                    // progress dialog). We never hide active-desktop windows, so drop it. Windows on other
                    // desktops are the ones we hid, so leave them. Skip right after a switch while the async
                    // show/hide settles, so a window being revealed is not mistaken for one going away.
                    if (!settling && w.DesktopIndex == active && !NM.IsWindowVisible(w.Hwnd) && !NM.IsIconic(w.Hwnd))
                        dead.Add(w.Hwnd);
                }
                foreach (var h in dead) _desktops.RemoveWindow(h);
            }
            catch (Exception ex) { Log.Error("tracker refresh", ex); }
        }

        private bool OnEnumWindow(IntPtr hwnd, IntPtr lparam) { _scan.Add(hwnd); return true; }

        private bool IsAppWindow(IntPtr h)
        {
            if (h == IntPtr.Zero || h == _shell) return false;
            if (!NM.IsWindowVisible(h)) return false;
            if (NM.GetWindowTextLength(h) == 0) return false;

            long ex = NM.GetWindowLongPtr(h, NM.GWL_EXSTYLE).ToInt64();
            bool appWindow = (ex & NM.WS_EX_APPWINDOW) != 0;
            bool toolWindow = (ex & NM.WS_EX_TOOLWINDOW) != 0;
            if (toolWindow && !appWindow) return false;

            if (NM.IsCloaked(h)) return false;

            IntPtr owner = NM.GetWindow(h, NM.GW_OWNER);
            if (owner != IntPtr.Zero && !appWindow) return false;

            uint pid;
            NM.GetWindowThreadProcessId(h, out pid);
            if (pid == (uint)_ownPid) return false;

            return !IsBlacklisted(NM.ClassOf(h));
        }

        private static readonly HashSet<string> Blacklist = new HashSet<string>(StringComparer.Ordinal)
        {
            "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Button",
            "DV2ControlHost", "MsgrIMEWindowClass", "SysShadow", "Windows.UI.Core.CoreWindow",
            "ApplicationManager_DesktopShellWindow", "MultitaskingViewFrame", "ForegroundStaging",
            "XamlExplorerHostIslandWindow", "Windows.Internal.Shell.TabProxyWindow", "EdgeUiInputTopWndClass"
        };

        private static bool IsBlacklisted(string cls) { return cls != null && Blacklist.Contains(cls); }

        public void Dispose()
        {
            try { if (_debounce != null) _debounce.Stop(); } catch { }
            try { if (_foreHook != null) _foreHook.Dispose(); } catch { }
            try { if (_objHook != null) _objHook.Dispose(); } catch { }
        }
    }
}
