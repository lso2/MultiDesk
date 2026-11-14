using System;
using System.Runtime.InteropServices;

namespace MultiDesk.Interop
{
    /// <summary>
    /// Windows virtual desktop pinning. Defines VirtualDesktop, which pins a window to every Windows
    /// virtual desktop through the undocumented shell COM behind Task View's "Show this window on all
    /// desktops". The bar is pinned and stays on screen after a Windows desktop switch. Interface IDs
    /// of that COM differ between Windows builds, and every call is wrapped.
    /// Doc: docs/notes/MultiDesk/Interop/VirtualDesktop.cs.md
    /// </summary>
    internal static class VirtualDesktop
    {
        // The service ID the immersive shell answers IVirtualDesktopPinnedApps under. Asking with the
        // interface ID as the service ID returns E_NOTIMPL.
        private static readonly Guid PinnedAppsService = new Guid("B5A399E7-1C87-46B8-88E9-FC5747B171BD");

        /// <summary>Pin a window to every Windows virtual desktop. Returns null on success, or the
        /// error that stopped it. A window the shell has no view for yet reports TYPE_E_ELEMENTNOTFOUND,
        /// and the caller retries.</summary>
        public static string PinWindow(IntPtr hwnd)
        {
            try
            {
                var shellType = Type.GetTypeFromCLSID(new Guid("C2F03A33-21F5-47FA-B4BB-156362A2F239")); // ImmersiveShell
                if (shellType == null) return "ImmersiveShell class not registered";
                var shell = (IServiceProvider10)Activator.CreateInstance(shellType);

                var viewCollGuid = typeof(IApplicationViewCollection).GUID;
                object viewCollObj;
                shell.QueryService(ref viewCollGuid, ref viewCollGuid, out viewCollObj);
                var viewColl = (IApplicationViewCollection)viewCollObj;

                IApplicationView view;
                viewColl.GetViewForHwnd(hwnd, out view);
                if (view == null) return "no application view for the window";

                var pinnedService = PinnedAppsService;
                var pinnedGuid = typeof(IVirtualDesktopPinnedApps).GUID;
                object pinnedObj;
                shell.QueryService(ref pinnedService, ref pinnedGuid, out pinnedObj);
                var pinned = (IVirtualDesktopPinnedApps)pinnedObj;

                if (!pinned.IsViewPinned(view)) pinned.PinView(view);
                return null;
            }
            catch (Exception ex)
            {
                return ex.GetType().Name + " 0x" + Marshal.GetHRForException(ex).ToString("X8") + ": " + ex.Message;
            }
        }

        [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IServiceProvider10
        {
            void QueryService(ref Guid service, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppvObject);
        }

        [ComImport, Guid("372E1D3B-38D3-42E4-A15B-8AB2B178F513"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IApplicationView
        {
            // Members are not needed; we only pass the pointer through.
        }

        [ComImport, Guid("1841C6D7-4F9D-42C0-AF41-8747538F10E5"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IApplicationViewCollection
        {
            void GetViews(out object array);
            void GetViewsByZOrder(out object array);
            void GetViewsByAppUserModelId(string id, out object array);
            void GetViewForHwnd(IntPtr hwnd, out IApplicationView view);
        }

        [ComImport, Guid("4CE81583-1E4C-4632-A621-07A53543148F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IVirtualDesktopPinnedApps
        {
            bool IsAppIdPinned(string appId);
            void PinAppID(string appId);
            void UnpinAppID(string appId);
            bool IsViewPinned(IApplicationView view);
            void PinView(IApplicationView view);
            void UnpinView(IApplicationView view);
        }
    }
}
