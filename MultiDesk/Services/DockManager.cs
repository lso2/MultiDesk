using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using NM = MultiDesk.Interop.NativeMethods;

namespace MultiDesk.Services
{
    /// <summary>
    /// Docks the bar to any screen edge and reserves that strip as an OS AppBar, the same behavior
    /// QuickPane's pinned dock uses, generalized to all four edges. Supports a floating mode and an
    /// auto-hide mode that collapses to a thin trigger strip. Dragging the bar near an edge snaps it
    /// there on release.
    /// </summary>
    internal sealed class DockManager
    {
        private readonly Window _w;
        private readonly SettingsStore _s;

        private IntPtr _h;
        private uint _callbackMsg;
        private bool _appbarRegistered;

        private string _edge = "";
        private bool _autoHide;
        private bool _reserveSpace = true;
        private bool _applied;
        private bool _expanded;

        // Set while a context menu is open so an auto-hiding bar does not collapse out from under it.
        public static bool SuppressAutoHide;

        public static void TrackMenu(System.Windows.Controls.ContextMenu cm)
        {
            if (cm == null) return;
            cm.Opened += (s, e) => SuppressAutoHide = true;
            cm.Closed += (s, e) => SuppressAutoHide = false;
        }

        private DispatcherTimer _collapseTimer;

        public DockManager(Window window, SettingsStore settings)
        {
            _w = window;
            _s = settings;
        }

        public void AttachHandle(IntPtr handle)
        {
            _h = handle;
            var src = HwndSource.FromHwnd(_h);
            if (src != null) src.AddHook(WndProc);

            _w.MouseEnter += (s, e) => { if (_autoHide) { StopCollapseTimer(); Expand(); } };
            _w.MouseLeave += (s, e) => { if (_autoHide) StartCollapseTimer(); };

            Apply();
        }

        /// <summary>Apply the dock state from settings. Idempotent: re-applying the same edge only
        /// repositions, so saving unrelated settings does not cause a full re-dock or flicker.</summary>
        public void Apply()
        {
            if (_h == IntPtr.Zero) return;
            string edge = _s.Current.DockEdge;
            bool autoHide = _s.Current.AutoHide && edge != "float";
            bool reserve = _s.Current.ReserveSpace;

            if (_applied && edge == _edge && autoHide == _autoHide && reserve == _reserveSpace)
            {
                Reposition();
                return;
            }

            RemoveAppBar();
            StopCollapseTimer();

            _edge = edge;
            _autoHide = autoHide;
            _reserveSpace = reserve;
            _applied = true;
            _expanded = !autoHide;

            if (edge == "float") { ApplyFloat(); return; }
            if (autoHide) { ApplyAutoHide(); }
            else if (reserve) { ApplyReserved(); }
            else { ApplyOverlay(); }
        }

        private void Reposition()
        {
            if (_edge == "float") ApplyFloat();
            else if (_autoHide) SetStrip(_expanded);
            else if (_reserveSpace) SetReservedPosition();
            else ResizeLive(_s.Current.BarThicknessPx);
        }

        // ---- reserved (AppBar) ---------------------------------------------
        // Docked but not reserving space: sit at the edge over other windows.
        private void ApplyOverlay()
        {
            _w.Topmost = true;
            ResizeLive(_s.Current.BarThicknessPx);
        }

        private void ApplyReserved()
        {
            _w.Topmost = _s.Current.AlwaysOnTop;
            if (!_appbarRegistered)
            {
                _callbackMsg = NM.RegisterWindowMessage("MultiDeskAppBarMessage");
                var abd = new NM.APPBARDATA
                {
                    cbSize = Marshal.SizeOf(typeof(NM.APPBARDATA)),
                    hWnd = _h,
                    uCallbackMessage = _callbackMsg
                };
                NM.SHAppBarMessage(NM.ABM_NEW, ref abd);
                _appbarRegistered = true;
            }
            SetReservedPosition();
        }

        private void SetReservedPosition()
        {
            if (_h == IntPtr.Zero || !_appbarRegistered) return;
            int t = _s.Current.BarThicknessPx;
            int sw = NM.GetSystemMetrics(NM.SM_CXSCREEN);
            int sh = NM.GetSystemMetrics(NM.SM_CYSCREEN);

            var abd = new NM.APPBARDATA { cbSize = Marshal.SizeOf(typeof(NM.APPBARDATA)), hWnd = _h };
            switch (_edge)
            {
                case "right": abd.uEdge = NM.ABE_RIGHT; abd.rc = Rect(sw - t, 0, sw, sh); break;
                case "top": abd.uEdge = NM.ABE_TOP; abd.rc = Rect(0, 0, sw, t); break;
                case "bottom": abd.uEdge = NM.ABE_BOTTOM; abd.rc = Rect(0, sh - t, sw, sh); break;
                default: abd.uEdge = NM.ABE_LEFT; abd.rc = Rect(0, 0, t, sh); break;
            }

            NM.SHAppBarMessage(NM.ABM_QUERYPOS, ref abd);
            switch (_edge)
            {
                case "right": abd.rc.Left = abd.rc.Right - t; break;
                case "top": abd.rc.Bottom = abd.rc.Top + t; break;
                case "bottom": abd.rc.Top = abd.rc.Bottom - t; break;
                default: abd.rc.Right = abd.rc.Left + t; break;
            }
            NM.SHAppBarMessage(NM.ABM_SETPOS, ref abd);
            NM.MoveWindow(_h, abd.rc.Left, abd.rc.Top, abd.rc.Right - abd.rc.Left, abd.rc.Bottom - abd.rc.Top, true);
        }

        // ---- auto-hide (trigger strip) -------------------------------------
        private void ApplyAutoHide()
        {
            _w.Topmost = true;
            // Reveal the bar so it is clearly visible when it docks (notably right after install), then
            // tuck it away after a readable delay. Without this the bar starts as a 4px strip and looks
            // like it never opened.
            _expanded = true;
            SetStrip(true);
            StartIntroCollapse();
        }

        private void StartIntroCollapse()
        {
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.0) };
            t.Tick += (s, e) =>
            {
                t.Stop();
                if (SuppressAutoHide || CursorOverWindow()) { StartCollapseTimer(); return; }
                Collapse();
            };
            t.Start();
        }

        private void SetStrip(bool expanded)
        {
            if (_h == IntPtr.Zero) return;
            int t = _s.Current.BarThicknessPx;
            int sw = NM.GetSystemMetrics(NM.SM_CXSCREEN);
            int sh = NM.GetSystemMetrics(NM.SM_CYSCREEN);
            const int strip = 4;
            // The expanded bar spans the whole edge and may cover content such as window buttons. Only
            // the collapsed trigger leaves the corners free, so moving to the top-right buttons or the
            // bottom-right taskbar corner does not expand the bar.
            const int dead = 46;
            int x, y, w, h;
            switch (_edge)
            {
                case "right":
                    w = expanded ? t : strip; x = sw - w;
                    if (expanded) { y = 0; h = sh; } else { y = dead; h = sh - 2 * dead; }
                    break;
                case "left":
                    w = expanded ? t : strip; x = 0;
                    if (expanded) { y = 0; h = sh; } else { y = dead; h = sh - 2 * dead; }
                    break;
                case "top":
                    h = expanded ? t : strip; x = 0; y = 0; w = expanded ? sw : sw - dead;
                    break;
                case "bottom":
                    h = expanded ? t : strip; x = 0; y = sh - h; w = expanded ? sw : sw - dead;
                    break;
                default:
                    w = expanded ? t : strip; x = 0;
                    if (expanded) { y = 0; h = sh; } else { y = dead; h = sh - 2 * dead; }
                    break;
            }
            NM.SetWindowPos(_h, NM.HWND_TOPMOST, x, y, w, h, NM.SWP_NOACTIVATE | NM.SWP_SHOWWINDOW);
        }

        private void Expand() { if (!_expanded) { _expanded = true; SetStrip(true); } }
        private void Collapse() { if (_expanded) { _expanded = false; SetStrip(false); } }

        private void StartCollapseTimer()
        {
            if (_collapseTimer == null)
            {
                _collapseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
                _collapseTimer.Tick += (s, e) =>
                {
                    _collapseTimer.Stop();
                    if (SuppressAutoHide || CursorOverWindow()) { _collapseTimer.Start(); return; }
                    Collapse();
                };
            }
            _collapseTimer.Stop();
            _collapseTimer.Start();
        }

        private void StopCollapseTimer() { if (_collapseTimer != null) _collapseTimer.Stop(); }

        // ---- floating ------------------------------------------------------
        private void ApplyFloat()
        {
            _w.Topmost = _s.Current.AlwaysOnTop;
            int t = _s.Current.BarThicknessPx;
            int sh = NM.GetSystemMetrics(NM.SM_CYSCREEN);
            int h = (int)(sh * 0.6);
            int x = (int)_s.Current.FloatLeft;
            int y = (int)_s.Current.FloatTop;
            NM.SetWindowPos(_h, NM.HWND_TOP, x, y, t, h, NM.SWP_NOACTIVATE | NM.SWP_SHOWWINDOW);
        }

        // ---- drag to snap --------------------------------------------------
        public void BeginFloatForDrag()
        {
            RemoveAppBar();
            StopCollapseTimer();
            _edge = "float";
            _autoHide = false;
            _applied = true;
            _expanded = true;
            _w.Topmost = true; // keep visible above others while dragging
        }

        public void MoveFloat(int x, int y)
        {
            if (_h == IntPtr.Zero) return;
            NM.SetWindowPos(_h, IntPtr.Zero, x, y, 0, 0, NM.SWP_NOSIZE | NM.SWP_NOZORDER | NM.SWP_NOACTIVATE);
        }

        /// <summary>Snap to an edge after a deliberate dwell. Saving raises Changed, which drives the
        /// main window to dock there.</summary>
        public void SnapToEdge(string edge)
        {
            _s.Current.DockEdge = edge;
            _s.Save();
        }

        /// <summary>Keep the bar floating where it was dropped, with no snapping.</summary>
        public void SaveFloatPosition()
        {
            NM.RECT r;
            if (NM.GetWindowRect(_h, out r))
            {
                _s.Current.FloatLeft = r.Left;
                _s.Current.FloatTop = r.Top;
            }
            _s.Current.DockEdge = "float";
            _s.Save();
        }

        /// <summary>Resize smoothly during a drag with a single window move and no AppBar round-trip,
        /// which is what kept resizing laggy. The reservation is reasserted once on release.</summary>
        public void ResizeLive(int thickness)
        {
            if (_h == IntPtr.Zero) return;
            if (thickness < 56) thickness = 56;
            if (thickness > 400) thickness = 400;
            int sw = NM.GetSystemMetrics(NM.SM_CXSCREEN);
            int sh = NM.GetSystemMetrics(NM.SM_CYSCREEN);

            if (_edge == "float")
            {
                NM.RECT r;
                if (!NM.GetWindowRect(_h, out r)) return;
                NM.SetWindowPos(_h, IntPtr.Zero, r.Left, r.Top, thickness, r.Bottom - r.Top,
                    NM.SWP_NOZORDER | NM.SWP_NOACTIVATE);
                return;
            }

            int x, y, w, h;
            switch (_edge)
            {
                case "right": w = thickness; x = sw - w; y = 0; h = sh; break;
                case "top": x = 0; y = 0; w = sw; h = thickness; break;
                case "bottom": h = thickness; x = 0; y = sh - h; w = sw; break;
                default: x = 0; y = 0; w = thickness; h = sh; break;
            }
            NM.SetWindowPos(_h, IntPtr.Zero, x, y, w, h, NM.SWP_NOZORDER | NM.SWP_NOACTIVATE);
        }

        /// <summary>Finalize a resize by reasserting the AppBar reservation once.</summary>
        public void CommitResize()
        {
            if (_edge == "float") return;
            if (_autoHide) SetStrip(_expanded);
            else SetReservedPosition();
        }

        // ---- plumbing ------------------------------------------------------
        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (!_autoHide && _edge != "float" && _callbackMsg != 0 && (uint)msg == _callbackMsg &&
                wParam.ToInt32() == NM.ABN_POSCHANGED)
            {
                SetReservedPosition();
                handled = true;
            }
            return IntPtr.Zero;
        }

        private bool CursorOverWindow()
        {
            NM.POINT pt;
            if (!NM.GetCursorPos(out pt)) return false;
            NM.RECT r;
            if (!NM.GetWindowRect(_h, out r)) return false;
            return pt.X >= r.Left && pt.X < r.Right && pt.Y >= r.Top && pt.Y < r.Bottom;
        }

        public void RemoveAppBar()
        {
            if (!_appbarRegistered || _h == IntPtr.Zero) return;
            try
            {
                var abd = new NM.APPBARDATA { cbSize = Marshal.SizeOf(typeof(NM.APPBARDATA)), hWnd = _h };
                NM.SHAppBarMessage(NM.ABM_REMOVE, ref abd);
            }
            catch (Exception ex) { Log.Error("appbar remove", ex); }
            _appbarRegistered = false;
        }

        private static NM.RECT Rect(int l, int t, int r, int b)
        {
            return new NM.RECT { Left = l, Top = t, Right = r, Bottom = b };
        }

        public void Dispose()
        {
            StopCollapseTimer();
            RemoveAppBar();
        }
    }
}
