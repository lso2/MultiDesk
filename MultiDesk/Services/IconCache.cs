using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MultiDesk.Interop;

namespace MultiDesk.Services
{
    /// <summary>
    /// Resolves a window's icon as a frozen WPF image. Exe-based icons are cached by path so the many
    /// windows of one app share a single bitmap, which keeps memory low at high window counts. The
    /// window's own icon is preferred when it exposes one, queried with a timeout so a hung app cannot
    /// stall the tracker.
    /// </summary>
    internal static class IconCache
    {
        private static readonly Dictionary<string, ImageSource> ByExe =
            new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);

        public static ImageSource ForWindow(IntPtr hwnd, string exePath)
        {
            var own = FromWindowIcon(hwnd);
            if (own != null) return own;

            if (!string.IsNullOrEmpty(exePath))
            {
                ImageSource cached;
                if (ByExe.TryGetValue(exePath, out cached)) return cached;
                var img = FromExe(exePath);
                if (img != null) { ByExe[exePath] = img; return img; }
            }
            return null;
        }

        private static ImageSource FromWindowIcon(IntPtr hwnd)
        {
            try
            {
                IntPtr res;
                IntPtr h = QueryIcon(hwnd, NativeMethods.ICON_BIG, out res);
                if (h == IntPtr.Zero) h = QueryIcon(hwnd, NativeMethods.ICON_SMALL2, out res);
                if (h == IntPtr.Zero) h = NativeMethods.GetClassLongPtr(hwnd, NativeMethods.GCLP_HICON);
                if (h == IntPtr.Zero) h = NativeMethods.GetClassLongPtr(hwnd, NativeMethods.GCLP_HICONSM);
                if (h == IntPtr.Zero) return null;
                // The window or class owns this handle, so it is converted but not destroyed.
                return FromHIcon(h, false);
            }
            catch (Exception ex) { Log.Error("FromWindowIcon", ex); return null; }
        }

        private static IntPtr QueryIcon(IntPtr hwnd, int which, out IntPtr result)
        {
            result = IntPtr.Zero;
            try
            {
                NativeMethods.SendMessageTimeout(hwnd, NativeMethods.WM_GETICON, new IntPtr(which), IntPtr.Zero,
                    NativeMethods.SMTO_ABORTIFHUNG, 200, out result);
            }
            catch { result = IntPtr.Zero; }
            return result;
        }

        private static ImageSource FromExe(string exePath)
        {
            var shfi = new NativeMethods.SHFILEINFO();
            NativeMethods.SHGetFileInfo(exePath, 0, ref shfi,
                (uint)Marshal.SizeOf(typeof(NativeMethods.SHFILEINFO)),
                NativeMethods.SHGFI_ICON | NativeMethods.SHGFI_LARGEICON);
            if (shfi.hIcon == IntPtr.Zero) return null;
            // SHGetFileInfo gives us an owned handle, so destroy it after converting.
            return FromHIcon(shfi.hIcon, true);
        }

        private static ImageSource FromHIcon(IntPtr hIcon, bool destroy)
        {
            try
            {
                var src = Imaging.CreateBitmapSourceFromHIcon(hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
            catch (Exception ex) { Log.Error("CreateBitmapSourceFromHIcon", ex); return null; }
            finally { if (destroy) { try { NativeMethods.DestroyIcon(hIcon); } catch { } } }
        }
    }
}
