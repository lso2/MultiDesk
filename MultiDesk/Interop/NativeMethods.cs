using System;
using System.Runtime.InteropServices;
using System.Text;

namespace MultiDesk.Interop
{
    /// <summary>
    /// Win32 declarations. Defines NativeMethods, the P/Invoke declarations MultiDesk calls user32,
    /// kernel32, gdi32, psapi, dwmapi and shell32 through, grouped by purpose. Calls on other
    /// processes' windows read, move, show or focus them, and no code is injected into another
    /// process.
    /// Doc: docs/notes/MultiDesk/Interop/NativeMethods.cs.md
    /// </summary>
    internal static class NativeMethods
    {
        // ---- structs --------------------------------------------------------
        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left, Top, Right, Bottom;
            public int Width { get { return Right - Left; } }
            public int Height { get { return Bottom - Top; } }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct SIZE { public int cx, cy; }

        [StructLayout(LayoutKind.Sequential)]
        public struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        // ---- enumeration / identity ----------------------------------------
        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        public static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        public static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool IsZoomed(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

        [DllImport("user32.dll")]
        public static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

        [DllImport("user32.dll")]
        public static extern IntPtr GetShellWindow();

        // The top-level window at the top of the z-order. GetWindow with GW_HWNDNEXT walks down from it.
        [DllImport("user32.dll")]
        public static extern IntPtr GetTopWindow(IntPtr hWnd);

        public const uint GW_OWNER = 4;
        public const uint GW_HWNDNEXT = 2;
        public const uint GA_ROOT = 2;
        public const uint GA_ROOTOWNER = 3;

        public static string ClassOf(IntPtr hWnd)
        {
            var sb = new StringBuilder(256);
            GetClassName(hWnd, sb, sb.Capacity);
            return sb.ToString();
        }

        public static string TextOf(IntPtr hWnd)
        {
            int len = GetWindowTextLength(hWnd);
            if (len <= 0) return string.Empty;
            var sb = new StringBuilder(len + 1);
            GetWindowText(hWnd, sb, sb.Capacity);
            return sb.ToString();
        }

        // ---- focus / activation --------------------------------------------
        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern IntPtr SetActiveWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern IntPtr SetFocus(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        public const byte VK_MENU = 0x12;
        public const uint KEYEVENTF_KEYUP = 0x0002;

        // True when a window's thread has stopped pumping messages. Attaching input to a hung thread
        // hangs the caller, and focus calls skip the input-queue attach for such a window.
        [DllImport("user32.dll")]
        public static extern bool IsHungAppWindow(IntPtr hwnd);

        // Time of the last keyboard or mouse input in the session, in GetTickCount milliseconds.
        [StructLayout(LayoutKind.Sequential)]
        public struct LASTINPUTINFO
        {
            public uint cbSize;
            public uint dwTime;
        }

        [DllImport("user32.dll")]
        public static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

        // ---- show state / positioning --------------------------------------
        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        public static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        // Deferred batches apply many show/hide/z changes in one visual update, so a desktop switch
        // paints at once instead of windows disappearing one at a time.
        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr BeginDeferWindowPos(int nNumWindows);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr DeferWindowPos(IntPtr hWinPosInfo, IntPtr hWnd, IntPtr hWndInsertAfter,
            int x, int y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool EndDeferWindowPos(IntPtr hWinPosInfo);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool MoveWindow(IntPtr hWnd, int X, int Y, int nWidth, int nHeight, bool bRepaint);

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        public static extern bool GetCursorPos(out POINT lpPoint);

        public const int SW_HIDE = 0;
        public const int SW_SHOWNORMAL = 1;
        public const int SW_MAXIMIZE = 3;
        public const int SW_SHOWNOACTIVATE = 4;
        public const int SW_SHOW = 5;
        public const int SW_MINIMIZE = 6;
        public const int SW_SHOWMINNOACTIVE = 7;
        public const int SW_SHOWNA = 8;
        public const int SW_RESTORE = 9;

        public static readonly IntPtr HWND_TOP = IntPtr.Zero;
        public static readonly IntPtr HWND_BOTTOM = new IntPtr(1);
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);

        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOZORDER = 0x0004;
        public const uint SWP_NOACTIVATE = 0x0010;
        public const uint SWP_NOOWNERZORDER = 0x0200;
        public const uint SWP_SHOWWINDOW = 0x0040;
        public const uint SWP_HIDEWINDOW = 0x0080;
        public const uint SWP_FRAMECHANGED = 0x0020;
        public const uint SWP_ASYNCWINDOWPOS = 0x4000;

        // ---- styles (64-bit safe) ------------------------------------------
        public static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex)
        {
            return IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : new IntPtr(GetWindowLong32(hWnd, nIndex));
        }

        public static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
        {
            return IntPtr.Size == 8
                ? SetWindowLongPtr64(hWnd, nIndex, dwNewLong)
                : new IntPtr(SetWindowLong32(hWnd, nIndex, dwNewLong.ToInt32()));
        }

        [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
        private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
        private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
        private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        public static IntPtr GetClassLongPtr(IntPtr hWnd, int nIndex)
        {
            return IntPtr.Size == 8 ? GetClassLongPtr64(hWnd, nIndex) : new IntPtr(unchecked((int)GetClassLong32(hWnd, nIndex)));
        }

        [DllImport("user32.dll", EntryPoint = "GetClassLong")]
        private static extern uint GetClassLong32(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll", EntryPoint = "GetClassLongPtr")]
        private static extern IntPtr GetClassLongPtr64(IntPtr hWnd, int nIndex);

        public const int GWL_STYLE = -16;
        public const int GWL_EXSTYLE = -20;

        public const long WS_EX_TOOLWINDOW = 0x00000080L;
        public const long WS_EX_APPWINDOW = 0x00040000L;
        public const long WS_EX_LAYERED = 0x00080000L;
        public const long WS_EX_NOACTIVATE = 0x08000000L;

        public const int GCLP_HICON = -14;
        public const int GCLP_HICONSM = -34;

        // ---- transparency ---------------------------------------------------
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

        public const uint LWA_ALPHA = 0x2;

        // ---- messages -------------------------------------------------------
        [DllImport("user32.dll")]
        public static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam,
            uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

        public const uint WM_CLOSE = 0x0010;
        public const uint WM_GETICON = 0x007F;
        public const int ICON_SMALL = 0;
        public const int ICON_BIG = 1;
        public const int ICON_SMALL2 = 2;
        public const uint SMTO_ABORTIFHUNG = 0x0002;

        // ---- DWM cloak query, uncloak repair, and per-window transition suppression ----------------
        [DllImport("dwmapi.dll")]
        public static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

        public const int DWMWA_CLOAKED = 14;
        private const int DWMWA_CLOAK = 13;
        private const int DWMWA_TRANSITIONS_FORCEDISABLED = 3;

        /// <summary>Suppress (or restore) DWM's minimize/restore animations for one window, so a
        /// minimize performed as off-desktop parking is not accompanied by a visible shrink flourish.
        /// Documented attribute; best effort, harmless where unsupported.</summary>
        public static void SetTransitionsDisabled(IntPtr hwnd, bool disabled)
        {
            try
            {
                int v = disabled ? 1 : 0;
                DwmSetWindowAttribute(hwnd, DWMWA_TRANSITIONS_FORCEDISABLED, ref v, sizeof(int));
            }
            catch { }
        }

        // ---- monitors -------------------------------------------------------
        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

        [DllImport("user32.dll")]
        public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        public const uint MONITOR_DEFAULTTONEAREST = 2;

        // ---- WinEvent hooks -------------------------------------------------
        public delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
            int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
            WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

        [DllImport("user32.dll")]
        public static extern bool UnhookWinEvent(IntPtr hWinEventHook);

        public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
        public const uint EVENT_SYSTEM_MINIMIZESTART = 0x0016;
        public const uint EVENT_SYSTEM_MINIMIZEEND = 0x0017;
        public const uint EVENT_OBJECT_CREATE = 0x8000;
        public const uint EVENT_OBJECT_DESTROY = 0x8001;
        public const uint EVENT_OBJECT_SHOW = 0x8002;
        public const uint EVENT_OBJECT_HIDE = 0x8003;
        public const uint EVENT_OBJECT_REORDER = 0x8004;
        public const uint EVENT_OBJECT_NAMECHANGE = 0x800C;
        public const uint WINEVENT_OUTOFCONTEXT = 0x0000;
        public const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
        public const int OBJID_WINDOW = 0;

        // ---- AppBar / docking ----------------------------------------------
        [StructLayout(LayoutKind.Sequential)]
        public struct APPBARDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public uint uCallbackMessage;
            public uint uEdge;
            public RECT rc;
            public IntPtr lParam;
        }

        [DllImport("shell32.dll")]
        public static extern UIntPtr SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

        [DllImport("user32.dll")]
        public static extern uint RegisterWindowMessage(string lpString);

        [DllImport("user32.dll")]
        public static extern int GetSystemMetrics(int nIndex);

        public const int SM_CXSCREEN = 0;
        public const int SM_CYSCREEN = 1;

        public const uint ABM_NEW = 0x0;
        public const uint ABM_REMOVE = 0x1;
        public const uint ABM_QUERYPOS = 0x2;
        public const uint ABM_SETPOS = 0x3;
        public const uint ABM_WINDOWPOSCHANGED = 0x9;

        public const uint ABE_LEFT = 0;
        public const uint ABE_TOP = 1;
        public const uint ABE_RIGHT = 2;
        public const uint ABE_BOTTOM = 3;

        public const int ABN_POSCHANGED = 0x1;

        // Display scale and resolution changes, after which a docked bar is put back on its edge.
        public const int WM_DPICHANGED = 0x02E0;
        public const int WM_DISPLAYCHANGE = 0x007E;

        // ---- icon extraction (exe fallback) --------------------------------
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes,
            ref SHFILEINFO psfi, uint cbSizeFileInfo, uint uFlags);

        [DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr hIcon);

        // Capture a window's pixels for the persisted preview cache.
        [DllImport("user32.dll")]
        public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

        public const uint PW_RENDERFULLCONTENT = 0x00000002;

        [DllImport("gdi32.dll")]
        public static extern bool DeleteObject(IntPtr hObject);

        public const uint SHGFI_ICON = 0x000000100;
        public const uint SHGFI_LARGEICON = 0x000000000;
        public const uint SHGFI_SMALLICON = 0x000000001;
        public const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;

        // ---- process info ---------------------------------------------------
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr h);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool QueryFullProcessImageName(IntPtr hProcess, int flags, StringBuilder name, ref int size);

        [DllImport("psapi.dll", SetLastError = true)]
        public static extern bool EmptyWorkingSet(IntPtr hProcess);

        public const uint PROCESS_QUERY_INFORMATION = 0x0400;
        public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        public const uint PROCESS_SET_QUOTA = 0x0100;

        /// <summary>Full image path for a process id, or null if it cannot be read.</summary>
        public static string ExePathForPid(uint pid)
        {
            if (pid == 0) return null;
            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return null;
            try
            {
                int cap = 1024;
                var sb = new StringBuilder(cap);
                return QueryFullProcessImageName(h, 0, sb, ref cap) ? sb.ToString() : null;
            }
            finally { CloseHandle(h); }
        }

        /// <summary>Trim a process working set so off-desktop windows return physical memory.
        /// Best effort: fails quietly for elevated or protected processes.</summary>
        public static void TrimProcessMemory(uint pid)
        {
            if (pid == 0) return;
            IntPtr h = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_SET_QUOTA, false, pid);
            if (h == IntPtr.Zero) return;
            try { EmptyWorkingSet(h); }
            finally { CloseHandle(h); }
        }

        public static bool IsCloaked(IntPtr hwnd)
        {
            try
            {
                int cloaked;
                if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out cloaked, sizeof(int)) == 0)
                    return cloaked != 0;
            }
            catch { }
            return false;
        }

        // One-directional migration repair only, never a hiding mechanism: clears a stray DWM cloak flag
        // a window can be left with if an earlier MultiDesk build that cloaked windows itself (since
        // retired in favor of parking) was killed before it ever ran its own un-cloak or exit cleanup.
        // Safe to call on a window that was never cloaked at all; DWM simply reports no change needed.
        public static bool TryUncloak(IntPtr hwnd)
        {
            try
            {
                int off = 0;
                return DwmSetWindowAttribute(hwnd, DWMWA_CLOAK, ref off, sizeof(int)) == 0;
            }
            catch { return false; }
        }

        // ---- window placement -----------------------------------------------------------------------
        // Used ONLY to repair a minimized window's remembered restore rectangle in place (see
        // DesktopManager.Visibility.cs). Never used to move or show a window: the showCmd field is a
        // show-STATE command, so driving movement through it coerces a window's normal/maximized state,
        // and rcNormalPosition is only the restore rectangle, which a maximized window's on-screen
        // rectangle ignores entirely.
        [StructLayout(LayoutKind.Sequential)]
        public struct WINDOWPLACEMENT
        {
            public int length;
            public int flags;
            public int showCmd;
            public POINT ptMinPosition;
            public POINT ptMaxPosition;
            public RECT rcNormalPosition;
        }

        [DllImport("user32.dll")]
        public static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

        [DllImport("user32.dll")]
        public static extern bool SetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

        // showCmd STATE value as reported by GetWindowPlacement for a maximized window (numerically
        // the same as the SW_MAXIMIZE command verb, named separately because it is read as a state).
        public const int SW_SHOWMAXIMIZED = 3;

        // WINDOWPLACEMENT.flags bit: a minimized window carrying it restores to maximized, not
        // normal, when next un-minimized.
        public const int WPF_RESTORETOMAXIMIZED = 2;

        /// <summary>A ready-to-fill WINDOWPLACEMENT with length set, which GetWindowPlacement requires.</summary>
        public static WINDOWPLACEMENT NewPlacement()
        {
            WINDOWPLACEMENT p = new WINDOWPLACEMENT();
            p.length = Marshal.SizeOf(typeof(WINDOWPLACEMENT));
            return p;
        }

        // ---- virtual screen (the bounding rectangle of every monitor combined) ----------------------
        public const int SM_XVIRTUALSCREEN = 76;
        public const int SM_YVIRTUALSCREEN = 77;
        public const int SM_CXVIRTUALSCREEN = 78;
        public const int SM_CYVIRTUALSCREEN = 79;
    }
}
