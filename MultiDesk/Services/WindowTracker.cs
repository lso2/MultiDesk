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
        private WinEventHook _minHook;
        private DispatcherTimer _debounce;
        private DispatcherTimer _heartbeat;
        private IntPtr _shell;

        public WindowTracker(DesktopManager desktops)
        {
            _desktops = desktops;
            _enumProc = OnEnumWindow;
            _ownPid = Process.GetCurrentProcess().Id;
        }

        public void Start()
        {
            DiagChannels.Arm(DiagChannels.Track);
            _shell = NM.GetShellWindow();

            _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
            _debounce.Tick += (s, e) => { _debounce.Stop(); Refresh(); };

            _foreHook = new WinEventHook(NM.EVENT_SYSTEM_FOREGROUND, NM.EVENT_SYSTEM_FOREGROUND);
            _foreHook.Event += OnForeground;
            _foreHook.Install();

            _objHook = new WinEventHook(NM.EVENT_OBJECT_CREATE, NM.EVENT_OBJECT_NAMECHANGE);
            _objHook.Event += OnObject;
            _objHook.Install();

            _minHook = new WinEventHook(NM.EVENT_SYSTEM_MINIMIZESTART, NM.EVENT_SYSTEM_MINIMIZEEND);
            _minHook.Event += OnMinimize;
            _minHook.Install();

            // The event hooks above cover creation, destruction, showing, renaming, reordering,
            // minimizing, and foreground changes, but NOT a window moving itself: an app that snaps
            // its own window back on screen after being parked fires none of the hooked events, so a
            // purely event-driven repair pass would simply never run on a quiet desktop and the
            // violation would stand indefinitely. The heartbeat guarantees the repair pass runs
            // regardless of whether anything eventful happens.
            _heartbeat = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _heartbeat.Tick += (s, e) => Refresh();
            _heartbeat.Start();

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
            // A tracked active-desktop window being destroyed is about to make Windows hand the
            // foreground to a successor of its own choosing, which must never read as the user
            // choosing a desktop; note it before the refresh so the suppression is armed by the time
            // the successor's foreground event arrives.
            if (evt == NM.EVENT_OBJECT_DESTROY) _desktops.NoteWindowLeaving(hwnd);
            // Show is what catches an off-desktop window that un-hides itself, so the next refresh's
            // heal pass re-hides it immediately instead of leaving it visible until some other event
            // happens to trigger a refresh. Reorder is a cheap extra trigger for the same case.
            if (evt == NM.EVENT_OBJECT_CREATE || evt == NM.EVENT_OBJECT_DESTROY ||
                evt == NM.EVENT_OBJECT_SHOW || evt == NM.EVENT_OBJECT_NAMECHANGE ||
                evt == NM.EVENT_OBJECT_REORDER)
                ScheduleRefresh();
        }

        // Minimizes arrive here whoever performed them, MultiDesk's own sidebar or the window's
        // title-bar button, which no MultiDesk code path sees. Either way Windows is about to pick a
        // foreground successor on its own, so focus-follow is suppressed the same way for both, which
        // is what keeps minimizing from ever switching desktops.
        private void OnMinimize(uint evt, IntPtr hwnd, int idObject, int idChild)
        {
            if (evt == NM.EVENT_SYSTEM_MINIMIZESTART) _desktops.NoteWindowLeaving(hwnd);
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
        /// hid or parked for another desktop still exist, so they are kept rather than dropped. A
        /// window not yet tracked that is off screen, whether hidden, parked, or (migrating from an
        /// older, cloaking-era build) still cloaked, is only picked up here if it matches a remembered
        /// placement exactly, which is what lets a window MultiDesk hid, parked, or cloaked before an
        /// unexpected restart be found again on the next launch instead of staying invisible
        /// forever.</summary>
        public void Refresh()
        {
            try
            {
                _scan.Clear();
                NM.EnumWindows(_enumProc, IntPtr.Zero);

                foreach (var h in _scan)
                {
                    if (_desktops.IsTracked(h)) { _desktops.UpdateTitle(h, NM.TextOf(h)); continue; }
                    bool onScreen = _desktops.OnScreen(h);
                    string title = NM.TextOf(h);
                    uint pid;
                    NM.GetWindowThreadProcessId(h, out pid);
                    string exe = NM.ExePathForPid(pid);
                    // A window MultiDesk itself hid, parked, or (migrating from an older, cloaking-era
                    // build) left cloaked before a restart is off screen in exactly the way a window
                    // that should never be tracked at all is, with nothing in its current state to tell
                    // the two apart. The remembered placement is what tells them apart, matched on the
                    // exact title as well as the executable: only a window that was actually on a
                    // desktop before this launch is let through here, so a lost session is found again
                    // on the next launch without also starting to grab every window in the system that
                    // is off screen for a reason of its own, an ordinary app minimized to the tray or a
                    // shell helper window among them.
                    if (!onScreen && !_desktops.HasPendingPlacementFor(exe, title)) continue;
                    if (!IsAppWindow(h, !onScreen)) continue;
                    _desktops.EnsureWindow(h, title, pid, exe);
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

                _desktops.HealVisibility();

                // The heartbeat is also what gives the icon order and the active desktop a regular
                // reading, so a change neither the user nor this code asked for is caught within half
                // a second of happening rather than whenever something else happens to look.
                DiagOrder.Sample(_desktops.Desktops);
                DiagNav.SampleActive(_desktops.ActiveIndex);
            }
            catch (Exception ex) { Log.Error("tracker refresh", ex); }
        }

        private bool OnEnumWindow(IntPtr hwnd, IntPtr lparam) { _scan.Add(hwnd); return true; }

        private bool IsAppWindow(IntPtr h, bool allowHidden = false)
        {
            if (h == IntPtr.Zero || h == _shell) return false;
            if (!allowHidden && !NM.IsWindowVisible(h)) return false;
            if (NM.GetWindowTextLength(h) == 0) return false;

            long ex = NM.GetWindowLongPtr(h, NM.GWL_EXSTYLE).ToInt64();
            bool appWindow = (ex & NM.WS_EX_APPWINDOW) != 0;
            bool toolWindow = (ex & NM.WS_EX_TOOLWINDOW) != 0;
            if (toolWindow && !appWindow) return false;

            // A cloaked window is never a real, presentable app window on its own account, so this
            // always excludes it, with one repair: a window being recovered through a remembered
            // placement (allowHidden true) can legitimately be one an earlier, cloaking-era MultiDesk
            // build cloaked before it was killed without ever uncloaking it itself. That one case is
            // uncloaked here instead of excluded, or it would stay cloaked forever with nothing left
            // running that still knew to undo it. Parking never cloaks anything, so this repair only
            // ever has real work to do once, migrating away from a build that did.
            if (NM.IsCloaked(h) && (!allowHidden || !NM.TryUncloak(h))) return false;

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
            "XamlExplorerHostIslandWindow", "Windows.Internal.Shell.TabProxyWindow", "EdgeUiInputTopWndClass",
            "GDI+ Window", "BluetoothNotificationAreaIconWindowClass"
        };

        private static bool IsBlacklisted(string cls) { return cls != null && Blacklist.Contains(cls); }

        public void Dispose()
        {
            try { if (_debounce != null) _debounce.Stop(); } catch { }
            try { if (_heartbeat != null) _heartbeat.Stop(); } catch { }
            try { if (_foreHook != null) _foreHook.Dispose(); } catch { }
            try { if (_objHook != null) _objHook.Dispose(); } catch { }
            try { if (_minHook != null) _minHook.Dispose(); } catch { }
        }
    }
}
