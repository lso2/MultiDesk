using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NM = MultiDesk.Interop.NativeMethods;

namespace MultiDesk.Services
{
    /// <summary>
    /// Optional cache of a still snapshot per window, captured while the window is on screen, so the hover
    /// preview can show a window that now lives on a hidden desktop instead of just its icon. Off by
    /// default to save memory. Snapshots are downscaled to a standalone frozen bitmap so the large source
    /// is not retained.
    /// </summary>
    public static class PreviewCache
    {
        private static readonly Dictionary<IntPtr, ImageSource> _cache = new Dictionary<IntPtr, ImageSource>();
        private static readonly Dictionary<IntPtr, int> _stamp = new Dictionary<IntPtr, int>();
        private const int MaxWidth = 300;
        private const int RefreshMs = 8000; // do not re-snapshot the same window more often than this

        public static ImageSource Get(IntPtr hwnd)
        {
            ImageSource img;
            return _cache.TryGetValue(hwnd, out img) ? img : null;
        }

        public static void Remove(IntPtr hwnd) { _cache.Remove(hwnd); _stamp.Remove(hwnd); }

        public static void Clear() { _cache.Clear(); _stamp.Clear(); }

        /// <summary>Snapshot a window that is currently visible. Quietly does nothing on failure. Throttled
        /// so flipping between desktops does not re-capture the same window on every switch.</summary>
        public static void Capture(IntPtr hwnd)
        {
            int last;
            if (_stamp.TryGetValue(hwnd, out last) && Math.Abs(Environment.TickCount - last) < RefreshMs) return;
            _stamp[hwnd] = Environment.TickCount; // throttle attempts whether or not the capture succeeds
            try
            {
                NM.RECT r;
                if (!NM.GetWindowRect(hwnd, out r)) return;
                int w = r.Right - r.Left, h = r.Bottom - r.Top;
                if (w < 16 || h < 16 || w > 8000 || h > 8000) return;

                using (var bmp = new System.Drawing.Bitmap(w, h))
                {
                    using (var g = System.Drawing.Graphics.FromImage(bmp))
                    {
                        IntPtr hdc = g.GetHdc();
                        bool ok;
                        try { ok = NM.PrintWindow(hwnd, hdc, NM.PW_RENDERFULLCONTENT); }
                        finally { g.ReleaseHdc(hdc); }
                        if (!ok) return;
                    }
                    IntPtr hbmp = bmp.GetHbitmap();
                    try
                    {
                        var src = Imaging.CreateBitmapSourceFromHBitmap(
                            hbmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                        _cache[hwnd] = Downscale(src, w, h);
                    }
                    finally { NM.DeleteObject(hbmp); }
                }
            }
            catch (Exception ex) { Log.Error("preview capture", ex); }
        }

        // Render a smaller standalone copy so the full-size source bitmap is not held in memory.
        private static ImageSource Downscale(BitmapSource src, int w, int h)
        {
            double scale = (w > MaxWidth) ? (double)MaxWidth / w : 1.0;
            int tw = Math.Max(1, (int)(w * scale));
            int th = Math.Max(1, (int)(h * scale));
            var rtb = new RenderTargetBitmap(tw, th, 96, 96, PixelFormats.Pbgra32);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen()) dc.DrawImage(src, new Rect(0, 0, tw, th));
            rtb.Render(dv);
            rtb.Freeze();
            return rtb;
        }
    }
}
