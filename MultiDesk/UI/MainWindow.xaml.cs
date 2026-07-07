using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using MultiDesk.Services;
using NM = MultiDesk.Interop.NativeMethods;

namespace MultiDesk.UI
{
    /// <summary>
    /// The bar shell. Hosts one section per desktop, docks to any edge through DockManager, snaps to an
    /// edge when the header is dragged near one, and resizes its thickness with the inner-edge gripper.
    /// </summary>
    public partial class MainWindow : Window
    {
        public static readonly DependencyProperty SectionsOrientationProperty =
            DependencyProperty.Register("SectionsOrientation", typeof(Orientation), typeof(MainWindow),
                new PropertyMetadata(Orientation.Vertical));

        public Orientation SectionsOrientation
        {
            get { return (Orientation)GetValue(SectionsOrientationProperty); }
            set { SetValue(SectionsOrientationProperty, value); }
        }

        private IntPtr _handle;
        private DockManager _dock;
        private HotkeyService _hotkeys;

        private bool _dragging;
        private int _dragOffX, _dragOffY;
        private System.Windows.Threading.DispatcherTimer _snapDwell;
        private string _pendingEdge;

        private bool _resizing;
        private int _resizeStartX, _resizeStartY, _resizeStartThickness;
        private double _resizeScale = 1.0;
        private double _resizeChromeDip = 15;
        private double _resizeCellDip = 30;

        private System.Windows.Threading.DispatcherTimer _pinTimer;
        private int _pinAttempts;

        public MainWindow()
        {
            InitializeComponent();
            SectionsHost.ItemsSource = App.Desktops.Desktops;
            Scroller.SizeChanged += OnScrollerSizeChanged;
            SourceInitialized += OnSourceInitialized;
            // The dock resizes this window natively, so WPF's cached Width/Height would otherwise stay
            // at the XAML startup values. Whenever WPF later re-asserts its cached size (some property
            // changes do), the bar snapped back to that stale startup width, which showed up as the
            // sidebar shrinking to one column after toggling a settings checkbox. Keeping the cache in
            // step with the real size makes any re-assert a no-op.
            SizeChanged += (s, e) => { Width = e.NewSize.Width; Height = e.NewSize.Height; };
        }

        private bool _widthSnapped;
        private int _snapAttempts;

        private void OnScrollerSizeChanged(object sender, SizeChangedEventArgs e)
        {
            // Tell the desktops how much height they have, so sections auto-fill it.
            App.Desktops.NotifyLayout(e.NewSize.Height - 4);
            if (!_widthSnapped) TrySnapWidth();
        }

        // Re-measure the scroll area and re-fill the sections to its real height. Used after the coffee
        // row is shown or hidden, so the sections neither overflow (clipping the first title) nor leave a
        // gap at the bottom.
        private void RefillLayout()
        {
            if (Scroller != null) App.Desktops.NotifyLayout(Scroller.ActualHeight - 4);
        }

        // Once laid out, snap the current width to a whole number of columns so the grid never shows a
        // near-column gap on first run, using the measured chrome and the display DPI.
        private void TrySnapWidth()
        {
            var edge = App.Settings.Current.DockEdge;
            if (edge == "top" || edge == "bottom") { _widthSnapped = true; return; }
            var sec = FirstSection(SectionsHost);
            double vpDip = (sec != null) ? sec.IconAreaViewportWidth : 0;
            if (vpDip <= 4) return; // sections not laid out yet; a later size event retries

            var tile = FirstTile(SectionsHost);
            double cellDip = (tile != null && tile.ActualWidth > 4) ? tile.ActualWidth : 0;
            if (cellDip <= 0)
            {
                // Wait for a real rendered tile so the snap uses the true DPI-rounded width. After a few
                // tries (for example the active desktop is empty), fall back to the estimate.
                if (_snapAttempts++ < 12) return;
                cellDip = App.Settings.Current.IconSizePx + 6;
            }

            double scale = 1.0;
            try { scale = VisualTreeHelper.GetDpi(this).DpiScaleX; } catch { }
            if (scale <= 0) scale = 1.0;
            double barDip = App.Settings.Current.BarThicknessPx / scale;
            double chromeDip = barDip - vpDip;
            int cols = (int)Math.Max(1, Math.Round(vpDip / cellDip));
            int nt = (int)Math.Round((chromeDip + cols * cellDip) * scale);
            _widthSnapped = true;
            if (Math.Abs(nt - App.Settings.Current.BarThicknessPx) >= 1)
            {
                App.Settings.Current.BarThicknessPx = nt;
                if (_dock != null) _dock.Apply();
            }
        }

        private void OnSourceInitialized(object sender, EventArgs e)
        {
            _handle = new WindowInteropHelper(this).Handle;

            // The bar is UI chrome, not a switchable app: keep it out of Alt+Tab (and the taskbar).
            try
            {
                long ex = NM.GetWindowLongPtr(_handle, NM.GWL_EXSTYLE).ToInt64();
                NM.SetWindowLongPtr(_handle, NM.GWL_EXSTYLE, new IntPtr(ex | NM.WS_EX_TOOLWINDOW));
            }
            catch (Exception ex) { Log.Error("toolwindow style", ex); }

            _dock = new DockManager(this, App.Settings);
            _dock.AttachHandle(_handle);

            _hotkeys = new HotkeyService(_handle, App.Settings, ShowSwitcher);
            _hotkeys.Apply();

            App.Settings.Changed += OnSettingsChanged;
            App.Settings.ThicknessChangedLive += OnThicknessLive;

            _snapDwell = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
            _snapDwell.Tick += OnSnapDwell;

            StartPinAllDesktops();
            UpdateOrientationAndGripper();
            ScheduleStartupSnap();
        }

        // After launch the active desktop's tiles need a moment to render. Re-run the column snap once a
        // real tile exists, so the grid lands on whole columns without the user nudging the bar.
        private void ScheduleStartupSnap()
        {
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            int tries = 0;
            t.Tick += (s, e) =>
            {
                tries++;
                var tile = FirstTile(SectionsHost);
                bool haveTile = tile != null && tile.ActualWidth > 4;
                if (haveTile || tries >= 12)
                {
                    _widthSnapped = false;
                    _snapAttempts = 12; // permit the estimate fallback if no tile ever rendered
                    TrySnapWidth();
                    RefillLayout();
                    t.Stop();
                }
            };
            t.Start();
        }

        // Pin the bar to every virtual desktop so it stays visible after a desktop switch. The shell
        // view only exists once the window has rendered, so retry a few times until it takes.
        private void StartPinAllDesktops()
        {
            _pinAttempts = 0;
            _pinTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _pinTimer.Tick += (s, e) =>
            {
                _pinAttempts++;
                bool ok = _handle != IntPtr.Zero && MultiDesk.Interop.VirtualDesktop.PinWindow(_handle);
                if (ok || _pinAttempts >= 8) { _pinTimer.Stop(); _pinTimer = null; }
            };
            _pinTimer.Start();
        }

        private void OnThicknessLive(object sender, EventArgs e)
        {
            if (_dock != null) _dock.ResizeLive(App.Settings.Current.BarThicknessPx);
        }

        private void OnSettingsChanged(object sender, EventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    if (_dock != null) _dock.Apply();
                    if (_hotkeys != null) _hotkeys.Apply();
                    UpdateOrientationAndGripper();
                    Dispatcher.BeginInvoke(new Action(RefillLayout), System.Windows.Threading.DispatcherPriority.Loaded);
                }
                catch (Exception ex) { Log.Error("bar settings changed", ex); }
            }));
        }

        private void UpdateOrientationAndGripper()
        {
            string edge = App.Settings.Current.DockEdge;
            bool vertical = edge == "left" || edge == "right" || edge == "float";
            SectionsOrientation = vertical ? Orientation.Vertical : Orientation.Horizontal;
            // The outer list never reserves a scrollbar (it would steal a column); the wheel still scrolls.
            Scroller.VerticalScrollBarVisibility = vertical ? ScrollBarVisibility.Hidden : ScrollBarVisibility.Disabled;
            Scroller.HorizontalScrollBarVisibility = vertical ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Hidden;

            // Header: top bar for vertical docks, slim rotated strip on the left for horizontal docks.
            if (vertical) { DockPanel.SetDock(Header, Dock.Top); HeaderRotate.Angle = 0; }
            else { DockPanel.SetDock(Header, Dock.Left); HeaderRotate.Angle = -90; }

            // The coffee button shows at the bottom of a vertical bar, hideable in settings.
            CoffeeBtn.Visibility = (vertical && App.Settings.Current.ShowCoffee) ? Visibility.Visible : Visibility.Collapsed;

            // Inner-edge resize handle: a vertical bar for side docks, a horizontal bar for top/bottom.
            Gripper.Visibility = Visibility.Visible;
            if (edge == "right")
            {
                Gripper.Width = 6; Gripper.Height = double.NaN;
                Gripper.HorizontalAlignment = HorizontalAlignment.Left;
                Gripper.VerticalAlignment = VerticalAlignment.Stretch;
                Gripper.Cursor = Cursors.SizeWE;
            }
            else if (edge == "top")
            {
                Gripper.Height = 6; Gripper.Width = double.NaN;
                Gripper.VerticalAlignment = VerticalAlignment.Bottom;
                Gripper.HorizontalAlignment = HorizontalAlignment.Stretch;
                Gripper.Cursor = Cursors.SizeNS;
            }
            else if (edge == "bottom")
            {
                Gripper.Height = 6; Gripper.Width = double.NaN;
                Gripper.VerticalAlignment = VerticalAlignment.Top;
                Gripper.HorizontalAlignment = HorizontalAlignment.Stretch;
                Gripper.Cursor = Cursors.SizeNS;
            }
            else // left, float
            {
                Gripper.Width = 6; Gripper.Height = double.NaN;
                Gripper.HorizontalAlignment = HorizontalAlignment.Right;
                Gripper.VerticalAlignment = VerticalAlignment.Stretch;
                Gripper.Cursor = Cursors.SizeWE;
            }
        }

        // ---- header drag to snap -------------------------------------------
        private void OnHeaderDown(object sender, MouseButtonEventArgs e)
        {
            NM.POINT cur;
            NM.RECT r;
            if (!NM.GetCursorPos(out cur) || !NM.GetWindowRect(_handle, out r)) return;
            _dragOffX = cur.X - r.Left;
            _dragOffY = cur.Y - r.Top;
            _dragging = true;
            _pendingEdge = null;
            Header.CaptureMouse();
            if (_dock != null) _dock.BeginFloatForDrag();
        }

        private void OnHeaderMove(object sender, MouseEventArgs e)
        {
            if (!_dragging) return;
            NM.POINT cur;
            if (!NM.GetCursorPos(out cur)) return;
            if (_dock != null) _dock.MoveFloat(cur.X - _dragOffX, cur.Y - _dragOffY);

            // Snap only on a deliberate dwell against an edge, not on a casual pass.
            string edge = EdgeUnderCursor(cur);
            if (edge != null)
            {
                if (edge != _pendingEdge) { _pendingEdge = edge; _snapDwell.Stop(); _snapDwell.Start(); }
            }
            else { _pendingEdge = null; _snapDwell.Stop(); }
        }

        private void OnHeaderUp(object sender, MouseButtonEventArgs e)
        {
            if (!_dragging) return;
            _snapDwell.Stop();
            EndDragInternal();
            if (_dock != null) _dock.SaveFloatPosition(); // dropped without a dwell, so stay floating
        }

        private void OnSnapDwell(object sender, EventArgs e)
        {
            _snapDwell.Stop();
            if (!_dragging || _pendingEdge == null) return;
            string edge = _pendingEdge;
            EndDragInternal();
            if (_dock != null) _dock.SnapToEdge(edge);
        }

        private void EndDragInternal()
        {
            _dragging = false;
            _pendingEdge = null;
            if (Header.IsMouseCaptured) Header.ReleaseMouseCapture();
        }

        private string EdgeUnderCursor(NM.POINT p)
        {
            int sw = NM.GetSystemMetrics(NM.SM_CXSCREEN);
            int sh = NM.GetSystemMetrics(NM.SM_CYSCREEN);
            const int z = 10;
            if (p.X <= z) return "left";
            if (p.X >= sw - z) return "right";
            if (p.Y <= z) return "top";
            if (p.Y >= sh - z) return "bottom";
            return null;
        }

        // ---- inner-edge resize ---------------------------------------------
        private void OnGripDown(object sender, MouseButtonEventArgs e)
        {
            NM.POINT cur;
            if (!NM.GetCursorPos(out cur)) return;
            _resizeStartX = cur.X;
            _resizeStartY = cur.Y;
            _resizeStartThickness = App.Settings.Current.BarThicknessPx;

            // Measure the real chrome once (it does not change with width) and the DPI, so the column
            // snap is exact regardless of display scaling.
            _resizeScale = 1.0;
            try { _resizeScale = VisualTreeHelper.GetDpi(this).DpiScaleX; } catch { }
            if (_resizeScale <= 0) _resizeScale = 1.0;
            var sec = FirstSection(SectionsHost);
            double vpDip = (sec != null) ? sec.IconAreaViewportWidth : 0;
            double barDip = App.Settings.Current.BarThicknessPx / _resizeScale;
            _resizeChromeDip = (vpDip > 4) ? (barDip - vpDip) : 15;
            if (_resizeChromeDip < 0) _resizeChromeDip = 15;
            _resizeCellDip = MeasuredCellDip();

            _resizing = true;
            Gripper.CaptureMouse();
            e.Handled = true;
        }

        private DesktopSection FirstSection(DependencyObject root)
        {
            var ds = root as DesktopSection;
            if (ds != null) return ds;
            int n = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                var r = FirstSection(VisualTreeHelper.GetChild(root, i));
                if (r != null) return r;
            }
            return null;
        }

        private WindowTile FirstTile(DependencyObject root)
        {
            var t = root as WindowTile;
            if (t != null) return t;
            int n = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                var r = FirstTile(VisualTreeHelper.GetChild(root, i));
                if (r != null) return r;
            }
            return null;
        }

        // The real rendered tile width in DIP, which already includes the device-pixel rounding that an
        // estimate (icon + chrome) misses, so snapping to it lands exactly on whole columns.
        private double MeasuredCellDip()
        {
            var t = FirstTile(SectionsHost);
            double w = (t != null) ? t.ActualWidth : 0;
            return w > 4 ? w : (App.Settings.Current.IconSizePx + 6);
        }

        private void OnGripMove(object sender, MouseEventArgs e)
        {
            if (!_resizing) return;
            NM.POINT cur;
            if (!NM.GetCursorPos(out cur)) return;
            string edge = App.Settings.Current.DockEdge;

            // Horizontal docks resize the bar height from the vertical drag; no column snap applies there.
            if (edge == "top" || edge == "bottom")
            {
                int dyh = cur.Y - _resizeStartY;
                int rawh = edge == "bottom" ? _resizeStartThickness - dyh : _resizeStartThickness + dyh;
                if (rawh < 56) rawh = 56;
                if (rawh > 400) rawh = 400;
                App.Settings.Current.BarThicknessPx = rawh;
                if (_dock != null) _dock.ResizeLive(rawh);
                return;
            }

            int dx = cur.X - _resizeStartX;
            int raw = edge == "right" ? _resizeStartThickness - dx : _resizeStartThickness + dx;
            // Snap to whole columns using the measured chrome, cell, and DPI, so the grid fills with no gap.
            double cellDip = _resizeCellDip;
            double rawDip = raw / _resizeScale;
            int cols = (int)Math.Round((rawDip - _resizeChromeDip) / cellDip);
            if (cols < 1) cols = 1;
            int nt = (int)Math.Round((_resizeChromeDip + cols * cellDip) * _resizeScale);
            if (nt < 56) nt = 56;
            if (nt > 400) nt = 400;
            App.Settings.Current.BarThicknessPx = nt;
            if (_dock != null) _dock.ResizeLive(nt); // single cheap move, no AppBar round-trip
        }

        private void OnGripUp(object sender, MouseButtonEventArgs e)
        {
            if (!_resizing) return;
            _resizing = false;
            if (Gripper.IsMouseCaptured) Gripper.ReleaseMouseCapture();
            if (_dock != null) _dock.CommitResize();
            App.Settings.Save();
        }

        // ---- menus ---------------------------------------------------------
        private void ShowSwitcher() { SwitcherWindow.ShowSingleton(); }

        private void OnCoffee(object sender, RoutedEventArgs e) { App.OpenSupportLink(); }

        // The header is the bar's window-control menu, the way a title bar's system menu works.
        private void OnHeaderRightClick(object sender, MouseButtonEventArgs e)
        {
            var cm = new ContextMenu();
            cm.Items.Add(Item("Settings...", () => SettingsWindow.ShowSingleton()));
            cm.Items.Add(new Separator());
            var ah = new MenuItem { Header = "Auto-hide", IsChecked = App.Settings.Current.AutoHide };
            ah.Click += (s, a) => { App.Settings.Current.AutoHide = !App.Settings.Current.AutoHide; App.Settings.Save(); };
            cm.Items.Add(ah);
            var aot = new MenuItem { Header = "Always on top", IsChecked = App.Settings.Current.AlwaysOnTop };
            aot.Click += (s, a) => { App.Settings.Current.AlwaysOnTop = !App.Settings.Current.AlwaysOnTop; App.Settings.Save(); };
            cm.Items.Add(aot);
            var rs = new MenuItem { Header = "Reserve space (when docked)", IsChecked = App.Settings.Current.ReserveSpace };
            rs.Click += (s, a) => { App.Settings.Current.ReserveSpace = !App.Settings.Current.ReserveSpace; App.Settings.Save(); };
            cm.Items.Add(rs);
            var dock = new MenuItem { Header = "Dock" };
            dock.Items.Add(EdgeItem("Left", "left"));
            dock.Items.Add(EdgeItem("Right", "right"));
            dock.Items.Add(EdgeItem("Top", "top"));
            dock.Items.Add(EdgeItem("Bottom", "bottom"));
            dock.Items.Add(EdgeItem("Floating", "float"));
            cm.Items.Add(dock);
            cm.Items.Add(new Separator());
            cm.Items.Add(Item("Add desktop", () => App.Desktops.AddDesktop()));
            cm.Items.Add(new Separator());
            cm.Items.Add(Item("Hide pane", () => CloseBar()));
            cm.Items.Add(Item("Exit MultiDesk", () => Application.Current.Shutdown()));
            cm.PlacementTarget = this;
            DockManager.TrackMenu(cm);
            cm.IsOpen = true;
            e.Handled = true;
        }

        private MenuItem EdgeItem(string header, string edge)
        {
            var mi = new MenuItem { Header = header, IsChecked = App.Settings.Current.DockEdge == edge };
            mi.Click += (s, a) => { App.Settings.Current.DockEdge = edge; App.Settings.Save(); };
            return mi;
        }

        private static MenuItem Item(string header, Action action)
        {
            var mi = new MenuItem { Header = header };
            mi.Click += (s, a) => { try { action(); } catch (Exception ex) { Log.Error("bar menu", ex); } };
            return mi;
        }

        /// <summary>Called on shutdown to undock cleanly and release hooks before the window closes.</summary>
        public void CloseBar()
        {
            try { App.Settings.Changed -= OnSettingsChanged; } catch { }
            try { App.Settings.ThicknessChangedLive -= OnThicknessLive; } catch { }
            try { if (_hotkeys != null) _hotkeys.Dispose(); } catch { }
            try { if (_dock != null) _dock.Dispose(); } catch { }
            try { Close(); } catch (Exception ex) { Log.Error("bar close", ex); }
        }
    }
}
