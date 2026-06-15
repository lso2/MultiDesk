using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using MultiDesk.Interop;
using MultiDesk.Models;
using MultiDesk.Services;
using NM = MultiDesk.Interop.NativeMethods;

namespace MultiDesk.UI
{
    /// <summary>
    /// Floating live preview shown on hover. For a window on the active desktop it composites a live
    /// DWM thumbnail; for a window hidden on another desktop, which has no live surface, it shows the
    /// large icon and title instead. One instance exists at a time, so the feature is light in memory.
    /// The window is not layered, because DWM thumbnails do not render on layered windows.
    /// </summary>
    public partial class PreviewPopup : Window
    {
        private static PreviewPopup _current;
        private DwmThumbnail _thumb;

        public PreviewPopup()
        {
            InitializeComponent();
            Closed += (s, e) => { if (_thumb != null) { _thumb.Dispose(); _thumb = null; } };
        }

        public static void ShowFor(WindowModel model, FrameworkElement anchor)
        {
            HideCurrent();
            if (model == null || anchor == null) return;
            try
            {
                var p = new PreviewPopup();
                _current = p;
                p.Setup(model, anchor);
            }
            catch (Exception ex) { Log.Error("preview show", ex); }
        }

        public static void HideCurrent()
        {
            if (_current != null)
            {
                try { _current.Close(); } catch { }
                _current = null;
            }
        }

        private void Setup(WindowModel model, FrameworkElement anchor)
        {
            TitleText.Text = model.Title;

            double scale = 1.0;
            try { scale = VisualTreeHelper.GetDpi(anchor).DpiScaleX; } catch { }
            if (scale <= 0) scale = 1.0;

            Point tl = anchor.PointToScreen(new Point(0, 0));
            Point br = anchor.PointToScreen(new Point(anchor.RenderSize.Width, anchor.RenderSize.Height));

            int popupW = (int)(Width * scale);
            int popupH = (int)(Height * scale);
            int sw = NM.GetSystemMetrics(NM.SM_CXSCREEN);
            int sh = NM.GetSystemMetrics(NM.SM_CYSCREEN);
            string edge = (App.Settings != null && App.Settings.Current != null) ? App.Settings.Current.DockEdge : "left";

            int x, y;
            if (edge == "right") { x = (int)tl.X - 8 - popupW; y = (int)tl.Y; }
            else if (edge == "top") { x = (int)tl.X; y = (int)br.Y + 8; }
            else if (edge == "bottom") { x = (int)tl.X; y = (int)tl.Y - 8 - popupH; }
            else { x = (int)br.X + 8; y = (int)tl.Y; if (x + popupW > sw) x = (int)tl.X - 8 - popupW; }

            if (x + popupW > sw - 4) x = sw - popupW - 4;
            if (x < 4) x = 4;
            if (y + popupH > sh - 4) y = sh - popupH - 4;
            if (y < 4) y = 4;

            Show();

            var handle = new WindowInteropHelper(this).Handle;
            if (handle != IntPtr.Zero)
                NM.SetWindowPos(handle, NM.HWND_TOPMOST, x, y, popupW, popupH,
                    NM.SWP_NOACTIVATE | NM.SWP_SHOWWINDOW);

            // A live DWM thumbnail only composites for a window that is actually on screen: the active
            // desktop, visible, not minimized. For a hidden, other-desktop, or minimized window it renders
            // an empty white rectangle, so for those show the icon and title instead.
            bool onActive = App.Desktops != null && model.DesktopIndex == App.Desktops.ActiveIndex
                            && NM.IsWindowVisible(model.Hwnd) && !NM.IsIconic(model.Hwnd);
            bool live = false;
            if (onActive)
            {
                try
                {
                    if (handle != IntPtr.Zero)
                    {
                        var t = DwmThumbnail.Register(handle, model.Hwnd);
                        if (t != null)
                        {
                            var size = t.SourceSize();
                            if (size.cx > 0 && size.cy > 0)
                            {
                                int marg = (int)(8 * scale);
                                int header = (int)(34 * scale);
                                t.Place(marg, header, popupW - marg, popupH - marg);
                                _thumb = t;
                                live = true;
                            }
                            else t.Dispose();
                        }
                    }
                }
                catch (Exception ex) { Log.Error("preview thumbnail", ex); }
            }

            if (!live)
            {
                // With the persist-previews setting on, show the last cached snapshot of this window if we
                // have one; otherwise fall back to the icon.
                ImageSource cached = (App.Settings != null && App.Settings.Current.PersistPreviews)
                    ? PreviewCache.Get(model.Hwnd) : null;
                if (cached != null)
                {
                    CachedPreview.Source = cached;
                    CachedPreview.Visibility = Visibility.Visible;
                }
                else
                {
                    IconFallback.Source = model.Icon;
                    IconFallback.Visibility = Visibility.Visible;
                }
            }
        }
    }
}
