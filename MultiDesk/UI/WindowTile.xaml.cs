using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using MultiDesk.Models;
using MultiDesk.Services;

namespace MultiDesk.UI
{
    /// <summary>
    /// One window in a desktop section. Left click activates it through the hardened routine, right
    /// click opens the core action menu, hovering shows the live preview, and dragging it onto another
    /// section moves the window to that desktop.
    /// </summary>
    public partial class WindowTile : UserControl
    {
        private Point _press;
        private bool _pressed;
        private bool _dragging;
        private readonly DispatcherTimer _hoverTimer;

        public WindowTile()
        {
            InitializeComponent();
            _hoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
            _hoverTimer.Tick += OnHoverTick;

            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
            MouseEnter += OnEnter;
            MouseLeave += OnLeave;
            PreviewMouseLeftButtonDown += OnDown;
            MouseMove += OnMove;
            MouseLeftButtonUp += OnUp;
            MouseRightButtonUp += OnRightClick;
            AllowDrop = true;
            DragOver += OnTileDragOver;
            Drop += OnTileDrop;
            // If something steals the capture (a context menu, a system popup), forget the press so a
            // stale flag cannot trigger a drag or click later. The drag path releases deliberately
            // after setting _dragging, so it is unaffected.
            LostMouseCapture += (s, e) => { if (!_dragging) _pressed = false; };
        }

        private void OnTileDragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent("MdWindow") ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        }

        private void OnTileDrop(object sender, DragEventArgs e)
        {
            var m = Model;
            if (m == null) return;
            if (e.Data.GetDataPresent("MdWindow"))
            {
                var dragged = e.Data.GetData("MdWindow") as WindowModel;
                // Drop a window onto this one to move it into this tile's slot (across desktops too).
                if (dragged != null && dragged != m) App.Desktops.MoveWindowToSpotOf(dragged.Hwnd, m.Hwnd);
                e.Handled = true;
            }
        }

        private WindowModel Model { get { return DataContext as WindowModel; } }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            ApplyIconSize();
            if (App.Settings != null) App.Settings.Changed += OnSettingsChanged;
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            _hoverTimer.Stop();
            if (App.Settings != null) App.Settings.Changed -= OnSettingsChanged;
        }

        private void OnSettingsChanged(object sender, EventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(ApplyIconSize));
        }

        private void ApplyIconSize()
        {
            int sz = (App.Settings != null && App.Settings.Current != null) ? App.Settings.Current.IconSizePx : 32;
            IconImage.Width = sz;
            IconImage.Height = sz;
        }

        private void OnEnter(object sender, MouseEventArgs e) { _hoverTimer.Stop(); _hoverTimer.Start(); }

        private void OnLeave(object sender, MouseEventArgs e) { _hoverTimer.Stop(); PreviewPopup.HideCurrent(); }

        private void OnHoverTick(object sender, EventArgs e)
        {
            _hoverTimer.Stop();
            if (IsMouseOver && Model != null) PreviewPopup.ShowFor(Model, this);
        }

        private void OnDown(object sender, MouseButtonEventArgs e)
        {
            _press = e.GetPosition(this);
            _pressed = true;
            _dragging = false;
            // Capture so a fast pull keeps routing mouse moves here after the cursor leaves the tile.
            // Without it, a quick grab often produced its first MouseMove when the cursor was already
            // outside, so the drag never started and the release landed on the section behind as a
            // plain desktop-switch click.
            CaptureMouse();
        }

        private void OnMove(object sender, MouseEventArgs e)
        {
            if (!_pressed || _dragging || e.LeftButton != MouseButtonState.Pressed) return;
            var p = e.GetPosition(this);
            if (Math.Abs(p.X - _press.X) > SystemParameters.MinimumHorizontalDragDistance ||
                Math.Abs(p.Y - _press.Y) > SystemParameters.MinimumVerticalDragDistance)
            {
                _dragging = true;
                _hoverTimer.Stop();
                PreviewPopup.HideCurrent();
                if (IsMouseCaptured) ReleaseMouseCapture(); // the OLE drag takes over the input from here
                var m = Model;
                if (m != null)
                {
                    var result = DragDropEffects.None;
                    try { result = DragDrop.DoDragDrop(this, new DataObject("MdWindow", m), DragDropEffects.Move); }
                    catch (Exception ex) { Log.Error("tile drag", ex); }
                    // Nothing accepted the drag, so the user really just clicked: act on it. This keeps
                    // the drag threshold low (grabs at once) without ever losing a click to a small jitter.
                    if (result == DragDropEffects.None) WindowActions.Toggle(m);
                }
                _pressed = false;
                _dragging = false;
            }
        }

        private void OnUp(object sender, MouseButtonEventArgs e)
        {
            // Decide the click before releasing capture, whose LostMouseCapture handler clears _pressed.
            bool click = _pressed && !_dragging;
            _pressed = false;
            if (IsMouseCaptured) ReleaseMouseCapture();
            if (click)
            {
                // Only a release still over the tile is a click; a release elsewhere is an abandoned
                // grab and must not fall through to the section as a desktop switch.
                if (IsMouseOver)
                {
                    var m = Model;
                    if (m != null) WindowActions.Toggle(m);
                }
                e.Handled = true;
            }
        }

        private void OnRightClick(object sender, MouseButtonEventArgs e)
        {
            var m = Model;
            if (m == null) return;
            var menu = BuildMenu(m);
            menu.PlacementTarget = this;
            DockManager.TrackMenu(menu);
            menu.IsOpen = true;
            e.Handled = true;
        }

        private ContextMenu BuildMenu(WindowModel m)
        {
            var cm = new ContextMenu();
            cm.Items.Add(Item("Activate", () => WindowActions.Activate(m)));
            cm.Items.Add(new Separator());
            cm.Items.Add(Item("Restore", () => WindowActions.Restore(m.Hwnd)));
            cm.Items.Add(Item("Minimize", () => WindowActions.Minimize(m.Hwnd)));
            cm.Items.Add(Item("Maximize", () => WindowActions.Maximize(m.Hwnd)));
            cm.Items.Add(new Separator());

            var move = new MenuItem { Header = "Move to desktop" };
            var desks = App.Desktops.Desktops;
            for (int i = 0; i < desks.Count; i++)
            {
                int idx = i;
                var mi = new MenuItem { Header = desks[i].Name, IsChecked = (i == m.DesktopIndex) };
                mi.Click += (s, a) => App.Desktops.MoveWindowToDesktop(m.Hwnd, idx);
                move.Items.Add(mi);
            }
            move.Items.Add(new Separator());
            var nd = new MenuItem { Header = "New desktop" };
            nd.Click += (s, a) =>
            {
                App.Desktops.AddDesktop();
                App.Desktops.MoveWindowToDesktop(m.Hwnd, App.Desktops.Desktops.Count - 1);
            };
            move.Items.Add(nd);
            cm.Items.Add(move);

            if (!string.IsNullOrEmpty(m.ExePath))
            {
                var pin = new MenuItem
                {
                    Header = "Pin " + App.Desktops.AppDisplayName(m) + " here",
                    IsChecked = App.Desktops.IsPinnedTo(m, m.DesktopIndex)
                };
                pin.Click += (s, a) =>
                {
                    if (App.Desktops.IsPinnedTo(m, m.DesktopIndex)) App.Desktops.UnpinApp(m);
                    else App.Desktops.PinApp(m, m.DesktopIndex);
                };
                cm.Items.Add(pin);
            }
            cm.Items.Add(new Separator());

            var aot = new MenuItem { Header = "Always on top", IsChecked = m.IsAlwaysOnTop };
            aot.Click += (s, a) =>
            {
                m.IsAlwaysOnTop = !m.IsAlwaysOnTop;
                WindowActions.SetAlwaysOnTop(m.Hwnd, m.IsAlwaysOnTop);
            };
            cm.Items.Add(aot);

            var tr = new MenuItem { Header = "Transparency" };
            foreach (int pct in new[] { 100, 90, 75, 50 })
            {
                int p = pct;
                var ti = new MenuItem { Header = pct + "%" };
                ti.Click += (s, a) => WindowActions.SetOpacity(m.Hwnd, p);
                tr.Items.Add(ti);
            }
            cm.Items.Add(tr);

            cm.Items.Add(Item("Send to bottom", () => WindowActions.SendToBottom(m.Hwnd)));
            cm.Items.Add(new Separator());
            cm.Items.Add(Item("Close", () => WindowActions.Close(m.Hwnd)));
            return cm;
        }

        private static MenuItem Item(string header, Action action)
        {
            var mi = new MenuItem { Header = header };
            mi.Click += (s, a) => { try { action(); } catch (Exception ex) { Log.Error("menu action", ex); } };
            return mi;
        }
    }
}
