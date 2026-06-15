using System;
using System.Runtime.InteropServices;
using MultiDesk.Services;

namespace MultiDesk.Interop
{
    /// <summary>
    /// Live window preview via the DWM thumbnail API. A thumbnail registers a source window onto a
    /// destination window we own and DWM keeps it updating on its own. One instance is alive at a time
    /// (the hover preview), so this is close to free in memory. Disposing unregisters the thumbnail.
    /// </summary>
    internal sealed class DwmThumbnail : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct DWM_THUMBNAIL_PROPERTIES
        {
            public int dwFlags;
            public NativeMethods.RECT rcDestination;
            public NativeMethods.RECT rcSource;
            public byte opacity;
            public bool fVisible;
            public bool fSourceClientAreaOnly;
        }

        private const int DWM_TNP_RECTDESTINATION = 0x00000001;
        private const int DWM_TNP_VISIBLE = 0x00000008;
        private const int DWM_TNP_OPACITY = 0x00000004;
        private const int DWM_TNP_SOURCECLIENTAREAONLY = 0x00000010;

        [DllImport("dwmapi.dll")]
        private static extern int DwmRegisterThumbnail(IntPtr dest, IntPtr src, out IntPtr thumbId);

        [DllImport("dwmapi.dll")]
        private static extern int DwmUnregisterThumbnail(IntPtr thumbId);

        [DllImport("dwmapi.dll")]
        private static extern int DwmUpdateThumbnailProperties(IntPtr hThumbId, ref DWM_THUMBNAIL_PROPERTIES props);

        [DllImport("dwmapi.dll")]
        private static extern int DwmQueryThumbnailSourceSize(IntPtr hThumbId, out NativeMethods.SIZE size);

        private IntPtr _id;
        private readonly IntPtr _source;

        private DwmThumbnail(IntPtr id, IntPtr source) { _id = id; _source = source; }

        /// <summary>Register source onto destination. Returns null if the source has no live surface
        /// (for example a window hidden on another desktop), so callers can fall back to an icon.</summary>
        public static DwmThumbnail Register(IntPtr destination, IntPtr source)
        {
            try
            {
                IntPtr id;
                if (DwmRegisterThumbnail(destination, source, out id) != 0 || id == IntPtr.Zero)
                    return null;
                return new DwmThumbnail(id, source);
            }
            catch (Exception ex) { Log.Error("DwmRegisterThumbnail", ex); return null; }
        }

        /// <summary>Native pixel size of the source window content, or zero if unknown.</summary>
        public NativeMethods.SIZE SourceSize()
        {
            NativeMethods.SIZE size;
            try { if (DwmQueryThumbnailSourceSize(_id, out size) == 0) return size; }
            catch (Exception ex) { Log.Error("DwmQueryThumbnailSourceSize", ex); }
            size.cx = 0; size.cy = 0;
            return size;
        }

        /// <summary>Place the live preview into a destination rectangle in physical pixels.</summary>
        public void Place(int left, int top, int right, int bottom)
        {
            try
            {
                var props = new DWM_THUMBNAIL_PROPERTIES
                {
                    dwFlags = DWM_TNP_RECTDESTINATION | DWM_TNP_VISIBLE | DWM_TNP_OPACITY | DWM_TNP_SOURCECLIENTAREAONLY,
                    rcDestination = new NativeMethods.RECT { Left = left, Top = top, Right = right, Bottom = bottom },
                    opacity = 255,
                    fVisible = true,
                    fSourceClientAreaOnly = false
                };
                DwmUpdateThumbnailProperties(_id, ref props);
            }
            catch (Exception ex) { Log.Error("DwmUpdateThumbnailProperties", ex); }
        }

        public void Dispose()
        {
            if (_id != IntPtr.Zero)
            {
                try { DwmUnregisterThumbnail(_id); } catch { }
                _id = IntPtr.Zero;
            }
        }
    }
}
