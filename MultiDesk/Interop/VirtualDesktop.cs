using System;
using System.Runtime.InteropServices;
using MultiDesk.Services;

namespace MultiDesk.Interop
{
    /// <summary>
    /// Best-effort pinning of a window to every virtual desktop, so the bar stays visible after a
    /// desktop switch. This relies on undocumented shell COM whose interface IDs differ between Windows
    /// builds, so everything is wrapped and any failure is a silent no-op: the bar simply stays on the
    /// desktop it was created on. This is the same private interface Task View's "show on all desktops"
    /// uses, and it is the one fragile piece, isolated here on purpose.
    /// </summary>
    internal static class VirtualDesktop
    {
        public static bool PinWindow(IntPtr hwnd)
        {
            try
            {
                var shellType = Type.GetTypeFromCLSID(new Guid("C2F03A33-21F5-47FA-B4BB-156362A2F239")); // ImmersiveShell
                if (shellType == null) return false;
                var shell = (IServiceProvider10)Activator.CreateInstance(shellType);

                var viewCollGuid = typeof(IApplicationViewCollection).GUID;
                object viewCollObj;
                shell.QueryService(ref viewCollGuid, ref viewCollGuid, out viewCollObj);
                var viewColl = (IApplicationViewCollection)viewCollObj;

                IApplicationView view;
                viewColl.GetViewForHwnd(hwnd, out view);
                if (view == null) return false;

                var pinnedGuid = typeof(IVirtualDesktopPinnedApps).GUID;
                object pinnedObj;
                shell.QueryService(ref pinnedGuid, ref pinnedGuid, out pinnedObj);
                var pinned = (IVirtualDesktopPinnedApps)pinnedObj;

                if (pinned.IsViewPinned(view)) return true;
                pinned.PinView(view);
                return true;
            }
            catch (Exception ex)
            {
                Log.Info("PinWindow (all desktops) not supported on this build: " + ex.Message);
                return false;
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
