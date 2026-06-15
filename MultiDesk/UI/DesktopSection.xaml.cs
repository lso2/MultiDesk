using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MultiDesk.Models;
using MultiDesk.Services;
using NM = MultiDesk.Interop.NativeMethods;

namespace MultiDesk.UI
{
    /// <summary>
    /// One desktop in the bar: an optional title, a grid of its window tiles, and a full lighter
    /// border when it is active. Clicking switches to it, double-clicking the title renames it,
    /// dropping a window tile moves that window here, and right click opens the desktop menu.
    /// </summary>
    public partial class DesktopSection : UserControl
    {
        private bool _resizingSection;
        private int _startCursorX, _startCursorY;
        private int _startRows;
        private DesktopModel _belowModel;
        private int _belowStartRows;
        private double _dpiX = 1.0, _dpiY = 1.0;
        private bool _titlePressed;
        private bool _titleDragging;
        private Point _titlePress;

        public DesktopSection()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
            Root.MouseRightButtonUp += OnSectionRightClick;
            Root.MouseLeftButtonUp += OnRootClick;
            TitleText.MouseLeftButtonDown += OnTitleDown;
            TitleText.MouseMove += OnTitleMove;
            TitleText.MouseLeftButtonUp += OnTitleUp;
            TitleEdit.KeyDown += OnEditKey;
            TitleEdit.LostFocus += (s, e) => CommitRename();
            DragOver += OnDragOver;
            Drop += OnDrop;
            TilesScroller.PreviewMouseWheel += OnTilesWheel;
        }

        private DesktopModel Model { get { return DataContext as DesktopModel; } }

        // Actual width available to the icon grid, used to snap the bar to whole columns accurately.
        public double IconAreaViewportWidth { get { return TilesScroller != null ? TilesScroller.ViewportWidth : 0; } }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            ApplyLayout();
            if (App.Settings != null) App.Settings.Changed += OnSettingsChanged;
            if (App.Desktops != null) App.Desktops.LayoutChanged += OnLayoutChanged;
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            if (App.Settings != null) App.Settings.Changed -= OnSettingsChanged;
            if (App.Desktops != null) App.Desktops.LayoutChanged -= OnLayoutChanged;
        }

        private void OnSettingsChanged(object sender, EventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(ApplyLayout));
        }

        private void OnLayoutChanged()
        {
            Dispatcher.BeginInvoke(new Action(ApplyGrid));
        }

        private void ApplyLayout()
        {
            ApplyTitleVisibility();
            ApplyOrientation();
            ApplyGrid();
        }

        public static readonly DependencyProperty TilesOrientationProperty =
            DependencyProperty.Register("TilesOrientation", typeof(Orientation), typeof(DesktopSection),
                new PropertyMetadata(Orientation.Horizontal));

        public Orientation TilesOrientation
        {
            get { return (Orientation)GetValue(TilesOrientationProperty); }
            set { SetValue(TilesOrientationProperty, value); }
        }

        // Vertical bar: title on top, icons wrap into rows. Horizontal bar: title becomes a slim rotated
        // strip on the left and icons wrap into columns, so each desktop stays compact.
        private void ApplyOrientation()
        {
            var s = (App.Settings != null) ? App.Settings.Current : null;
            bool horizontal = s != null && (s.DockEdge == "top" || s.DockEdge == "bottom");
            // Section titles always read normally on top; only the icon wrap direction changes.
            DockPanel.SetDock(TitleRow, Dock.Top);
            TitleRotate.Angle = 0;
            if (horizontal)
            {
                TilesOrientation = Orientation.Vertical;   // icons fill columns within the bar height
                TilesScroller.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
                TilesScroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden;
                ScrollIndicator.Visibility = Visibility.Collapsed;
                // Grip on the right edge: drag to set this desktop's column count.
                SectionGrip.Visibility = Visibility.Visible;
                SectionGrip.Width = 6; SectionGrip.Height = double.NaN;
                SectionGrip.HorizontalAlignment = HorizontalAlignment.Right;
                SectionGrip.VerticalAlignment = VerticalAlignment.Stretch;
                SectionGrip.Cursor = System.Windows.Input.Cursors.SizeWE;
            }
            else
            {
                TilesOrientation = Orientation.Horizontal;
                TilesScroller.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
                TilesScroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
                // Grip on the bottom edge: drag to set this desktop's row count.
                SectionGrip.Visibility = Visibility.Visible;
                SectionGrip.Height = 6; SectionGrip.Width = double.NaN;
                SectionGrip.VerticalAlignment = VerticalAlignment.Bottom;
                SectionGrip.HorizontalAlignment = HorizontalAlignment.Stretch;
                SectionGrip.Cursor = System.Windows.Input.Cursors.SizeNS;
            }
        }

        private void ApplyTitleVisibility()
        {
            var s = (App.Settings != null) ? App.Settings.Current : null;
            bool show = s != null ? s.ShowTitles : true;
            TitleRow.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            var align = ParseAlign(s != null ? s.TitleAlignment : "center");
            TitleText.TextAlignment = align;
            TitleEdit.TextAlignment = align;
        }

        private static TextAlignment ParseAlign(string a)
        {
            if (a == "left") return TextAlignment.Left;
            if (a == "right") return TextAlignment.Right;
            return TextAlignment.Center;
        }

        // Desktops auto-fill the pane height equally. A desktop the user manually shortened keeps that
        // height (a lock), and the rest divide the remaining height so the full pane is always used.
        private void ApplyGrid()
        {
            var s = (App.Settings != null) ? App.Settings.Current : null;
            var dm = App.Desktops;
            var m = Model;
            if (s == null || dm == null || m == null) return;
            int cell = s.IconSizePx + 6;

            // Horizontal bar: the icon area fills the bar height; no row-based height needed.
            if (s.DockEdge == "top" || s.DockEdge == "bottom")
            {
                TilesScroller.Height = double.NaN; // fill the bar height
                if (m.Rows > 0) { TilesScroller.Width = m.Rows * cell; m.DisplayRows = m.Rows; } // locked columns
                else
                {
                    TilesScroller.Width = double.NaN; // size to content
                    double vpH = TilesScroller.ViewportHeight;
                    int perCol = vpH > cell ? (int)(vpH / cell) : 1;
                    int cnt = m.Windows != null ? m.Windows.Count : 0;
                    m.DisplayRows = Math.Max(1, perCol > 0 ? (int)Math.Ceiling((double)cnt / perCol) : 1);
                }
                return;
            }

            if (m.Rows > 0) { TilesScroller.Height = m.Rows * cell; m.DisplayRows = m.Rows; return; } // locked

            double avail = dm.ContentHeight;
            if (avail <= 0) { TilesScroller.Height = s.RowsPerDesktop * cell; m.DisplayRows = s.RowsPerDesktop; return; }

            const double chromeV = 28; // title + padding + margin + border per section
            double lockedScroller = 0;
            int autoCount = 0;
            foreach (var d in dm.Desktops)
            {
                if (d.Rows > 0) lockedScroller += d.Rows * cell;
                else autoCount++;
            }
            if (autoCount < 1) autoCount = 1;
            int n = dm.Desktops.Count;
            double autoEach = (avail - n * chromeV - lockedScroller) / autoCount;
            if (autoEach < cell) autoEach = cell;
            TilesScroller.Height = autoEach;
            m.DisplayRows = Math.Max(1, (int)Math.Round(autoEach / cell));
        }

        // The rows a desktop currently shows: its locked value, or the auto-filled height it rendered.
        private static int CurrentRows(DesktopModel m)
        {
            if (m == null) return 3;
            if (m.Rows > 0) return m.Rows;
            return m.DisplayRows > 0 ? m.DisplayRows : 3;
        }

        private int EffectiveRows()
        {
            var m = Model;
            int global = (App.Settings != null && App.Settings.Current != null) ? App.Settings.Current.RowsPerDesktop : 3;
            return (m != null && m.Rows > 0) ? m.Rows : global;
        }

        // Thin overlay that shows scroll position in place of a scrollbar, and can still be grabbed
        // and dragged to scroll if you aim for it.
        private bool _draggingIndicator;

        private void OnTilesScroll(object sender, ScrollChangedEventArgs e) { UpdateIndicator(); }

        private void OnIndicatorDown(object sender, MouseButtonEventArgs e)
        {
            _draggingIndicator = true;
            ScrollIndicator.CaptureMouse();
            ScrollToCursor(e);
            e.Handled = true;
        }

        private void OnIndicatorMove(object sender, MouseEventArgs e)
        {
            if (_draggingIndicator) ScrollToCursor(e);
        }

        private void OnIndicatorUp(object sender, MouseButtonEventArgs e)
        {
            if (!_draggingIndicator) return;
            _draggingIndicator = false;
            if (ScrollIndicator.IsMouseCaptured) ScrollIndicator.ReleaseMouseCapture();
            e.Handled = true;
        }

        private void ScrollToCursor(MouseEventArgs e)
        {
            var sv = TilesScroller;
            double ext = sv.ExtentHeight, vp = sv.ViewportHeight;
            if (ext <= vp) return;
            double thumb = Math.Max(12, vp * (vp / ext));
            double y = e.GetPosition(sv).Y;
            double denom = vp - thumb;
            double frac = denom > 0 ? (y - thumb / 2) / denom : 0;
            if (frac < 0) frac = 0;
            if (frac > 1) frac = 1;
            sv.ScrollToVerticalOffset(frac * (ext - vp));
        }

        private void UpdateIndicator()
        {
            var sv = TilesScroller;
            if (sv == null) return;
            double ext = sv.ExtentHeight, vp = sv.ViewportHeight;
            // Only show the indicator on real overflow, not a few stray pixels from padding.
            if (vp <= 0 || ext <= vp + 6) { ScrollIndicator.Visibility = Visibility.Collapsed; return; }
            double thumb = Math.Max(12, vp * (vp / ext));
            double maxOff = ext - vp;
            double pos = (maxOff > 0) ? (sv.VerticalOffset / maxOff) * (vp - thumb) : 0;
            ScrollIndicator.Height = thumb;
            ScrollIndicator.Margin = new Thickness(0, pos, 0, 0);
            ScrollIndicator.Visibility = Visibility.Visible;
        }

        // Drag the divider under a desktop to set just that desktop's height, down to a single row.
        private void OnSectionGripDown(object sender, MouseButtonEventArgs e)
        {
            NM.POINT p;
            if (!NM.GetCursorPos(out p)) return;
            var m = Model;
            var dm = App.Desktops;
            _startCursorX = p.X;
            _startCursorY = p.Y;
            _startRows = CurrentRows(m);
            _belowModel = null;
            _belowStartRows = 0;
            var s = (App.Settings != null) ? App.Settings.Current : null;
            bool horizontal = s != null && (s.DockEdge == "top" || s.DockEdge == "bottom");
            // Side docks resize height and hand the inverse to the desktop directly below (a splitter).
            // Horizontal docks resize this desktop's width (columns) on its own, with no neighbour transfer.
            if (!horizontal && dm != null && m != null && m.Index + 1 < dm.Desktops.Count)
            {
                _belowModel = dm.Desktops[m.Index + 1];
                _belowStartRows = CurrentRows(_belowModel);
            }
            try { var dpi = VisualTreeHelper.GetDpi(this); _dpiX = dpi.DpiScaleX; _dpiY = dpi.DpiScaleY; }
            catch { _dpiX = 1.0; _dpiY = 1.0; }
            if (_dpiX <= 0) _dpiX = 1.0;
            if (_dpiY <= 0) _dpiY = 1.0;
            _resizingSection = true;
            SectionGrip.CaptureMouse();
            e.Handled = true;
        }

        private void OnSectionGripMove(object sender, MouseEventArgs e)
        {
            if (!_resizingSection) return;
            var m = Model;
            var s = (App.Settings != null) ? App.Settings.Current : null;
            if (m == null || s == null) return;
            NM.POINT p;
            if (!NM.GetCursorPos(out p)) return;

            // Horizontal dock: drag sets this desktop's column count from the horizontal movement.
            if (s.DockEdge == "top" || s.DockEdge == "bottom")
            {
                double cellPxX = (s.IconSizePx + 6) * _dpiX;
                if (cellPxX <= 0) return;
                int nc = _startRows + (int)System.Math.Round((p.X - _startCursorX) / cellPxX);
                if (nc < 1) nc = 1;
                if (nc > 12) nc = 12;
                if (m.Rows != nc) { m.Rows = nc; ApplyGrid(); }
                return;
            }

            double cellPx = (s.IconSizePx + 6) * _dpiY;
            if (cellPx <= 0) return;
            int delta = (int)System.Math.Round((p.Y - _startCursorY) / cellPx);

            if (_belowModel != null)
            {
                // True splitter: this grows by delta, the section directly below shrinks by the same, so
                // their sum is preserved and no other desktop changes height.
                if (_startRows + delta < 1) delta = 1 - _startRows;
                if (_belowStartRows - delta < 1) delta = _belowStartRows - 1;
                if (_startRows + delta > 12) delta = 12 - _startRows;
                if (_belowStartRows - delta > 12) delta = -(12 - _belowStartRows);
                int newThis = _startRows + delta;
                int newBelow = _belowStartRows - delta;
                if (m.Rows != newThis || _belowModel.Rows != newBelow)
                {
                    m.Rows = newThis;
                    _belowModel.Rows = newBelow;
                    ApplyGrid();
                    if (App.Desktops != null) App.Desktops.RaiseLayout(); // updates the section below
                }
            }
            else
            {
                int nr = _startRows + delta;
                if (nr < 1) nr = 1;
                if (nr > 12) nr = 12;
                if (m.Rows != nr) { m.Rows = nr; ApplyGrid(); if (App.Desktops != null) App.Desktops.RaiseLayout(); }
            }
        }

        private void OnSectionGripUp(object sender, MouseButtonEventArgs e)
        {
            if (!_resizingSection) return;
            _resizingSection = false;
            _belowModel = null;
            if (SectionGrip.IsMouseCaptured) SectionGrip.ReleaseMouseCapture();
            if (App.Desktops != null) App.Desktops.SaveDesktops();
        }

        // In a horizontal dock the wheel scrolls the icon columns sideways, since there is no vertical
        // overflow to scroll there.
        private void OnTilesWheel(object sender, MouseWheelEventArgs e)
        {
            var s = (App.Settings != null) ? App.Settings.Current : null;
            bool horizontal = s != null && (s.DockEdge == "top" || s.DockEdge == "bottom");
            if (!horizontal) return;
            double step = (s.IconSizePx + 6) * 2;
            TilesScroller.ScrollToHorizontalOffset(TilesScroller.HorizontalOffset - (e.Delta > 0 ? step : -step));
            e.Handled = true;
        }

        private void OnRootClick(object sender, MouseButtonEventArgs e)
        {
            // Empty-area click switches to this desktop. Tile clicks mark themselves handled.
            if (e.Handled) return;
            var m = Model;
            if (m != null) App.Desktops.SwitchTo(m.Index);
        }

        // Title: single click switches, double click renames, drag reorders the desktop.
        private void OnTitleDown(object sender, MouseButtonEventArgs e)
        {
            var m = Model;
            if (m == null) return;
            if (e.ClickCount >= 2) { BeginRename(); e.Handled = true; _titlePressed = false; return; }
            _titlePressed = true;
            _titleDragging = false;
            _titlePress = e.GetPosition(this);
        }

        private void OnTitleMove(object sender, MouseEventArgs e)
        {
            if (!_titlePressed || _titleDragging || e.LeftButton != MouseButtonState.Pressed) return;
            var p = e.GetPosition(this);
            if (Math.Abs(p.X - _titlePress.X) > SystemParameters.MinimumHorizontalDragDistance ||
                Math.Abs(p.Y - _titlePress.Y) > SystemParameters.MinimumVerticalDragDistance)
            {
                _titleDragging = true;
                var m = Model;
                if (m != null)
                {
                    try { DragDrop.DoDragDrop(this, new DataObject("MdDesktop", m), DragDropEffects.Move); }
                    catch (Exception ex) { Log.Error("desktop drag", ex); }
                }
                _titlePressed = false;
                _titleDragging = false;
            }
        }

        private void OnTitleUp(object sender, MouseButtonEventArgs e)
        {
            if (_titlePressed && !_titleDragging)
            {
                var m = Model;
                if (m != null) App.Desktops.SwitchTo(m.Index);
            }
            _titlePressed = false;
            e.Handled = true;
        }

        private void BeginRename()
        {
            var m = Model;
            if (m == null) return;
            TitleEdit.Text = m.Name;
            TitleEdit.Visibility = Visibility.Visible;
            TitleText.Visibility = Visibility.Collapsed;
            TitleEdit.Focus();
            TitleEdit.SelectAll();
        }

        private void OnEditKey(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { CommitRename(); e.Handled = true; }
            else if (e.Key == Key.Escape) { EndRename(); e.Handled = true; }
        }

        private void CommitRename()
        {
            if (TitleEdit.Visibility != Visibility.Visible) return;
            var m = Model;
            if (m != null && !string.IsNullOrWhiteSpace(TitleEdit.Text))
                App.Desktops.RenameDesktop(m.Index, TitleEdit.Text.Trim());
            EndRename();
        }

        private void EndRename()
        {
            TitleEdit.Visibility = Visibility.Collapsed;
            TitleText.Visibility = Visibility.Visible;
        }

        private void OnSectionRightClick(object sender, MouseButtonEventArgs e)
        {
            var m = Model;
            if (m == null) return;
            var cm = BuildMenu(m);
            cm.PlacementTarget = this;
            DockManager.TrackMenu(cm);
            cm.IsOpen = true;
            e.Handled = true;
        }

        private ContextMenu BuildMenu(DesktopModel m)
        {
            var cm = new ContextMenu();
            cm.Items.Add(Item("Switch to this desktop", () => App.Desktops.SwitchTo(m.Index)));
            cm.Items.Add(new Separator());
            cm.Items.Add(Item("Add desktop", () => App.Desktops.AddDesktop()));
            cm.Items.Add(Item("Rename...", BeginRename));
            var rem = Item("Remove this desktop", () => App.Desktops.RemoveDesktop(m.Index));
            rem.IsEnabled = App.Desktops.Desktops.Count > 1;
            cm.Items.Add(rem);
            if (m.Rows > 0)
                cm.Items.Add(Item("Auto-fit height", () => { m.Rows = 0; App.Desktops.SaveDesktops(); App.Desktops.RaiseLayout(); }));
            cm.Items.Add(new Separator());
            var st = new MenuItem { Header = "Show desktop titles", IsChecked = App.Settings.Current.ShowTitles };
            st.Click += (s, a) => { App.Settings.Current.ShowTitles = !App.Settings.Current.ShowTitles; App.Settings.Save(); };
            cm.Items.Add(st);
            return cm;
        }

        private void OnDragOver(object sender, DragEventArgs e)
        {
            bool ok = e.Data.GetDataPresent("MdWindow") || e.Data.GetDataPresent("MdDesktop");
            e.Effects = ok ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        }

        private void OnDrop(object sender, DragEventArgs e)
        {
            var m = Model;
            if (m == null) return;
            if (e.Data.GetDataPresent("MdDesktop"))
            {
                var moved = e.Data.GetData("MdDesktop") as DesktopModel;
                if (moved != null && moved != m) App.Desktops.ReorderDesktop(moved.Index, m.Index);
                e.Handled = true;
                return;
            }
            if (e.Data.GetDataPresent("MdWindow"))
            {
                var wm = e.Data.GetData("MdWindow") as WindowModel;
                if (wm != null) App.Desktops.MoveWindowToDesktop(wm.Hwnd, m.Index);
                e.Handled = true;
            }
        }

        private static MenuItem Item(string header, Action action)
        {
            var mi = new MenuItem { Header = header };
            mi.Click += (s, a) => { try { action(); } catch (Exception ex) { Log.Error("section menu", ex); } };
            return mi;
        }
    }
}
