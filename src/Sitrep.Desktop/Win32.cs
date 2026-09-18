using System.Runtime.InteropServices;

namespace Sitrep.Desktop;

internal static class Win32
{
    public const int VK_F7 = 0x76;
    public const int VK_F8 = 0x77;
    public const int VK_F9 = 0x78;
    public const int VK_MBUTTON = 0x04;
    public const int VK_SHIFT = 0x10;
    public const int VK_CONTROL = 0x11;

    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int WS_EX_LAYERED = 0x00080000;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int WS_EX_TOPMOST = 0x00000008;

    public const uint NIM_ADD = 0x00000000;
    public const uint NIM_DELETE = 0x00000002;
    public const uint NIF_MESSAGE = 0x00000001;
    public const uint NIF_ICON = 0x00000002;
    public const uint NIF_TIP = 0x00000004;
    public const int WM_USER = 0x0400;
    public const int WM_TRAYICON = WM_USER + 101;
    public const int WM_LBUTTONUP = 0x0202;
    public const int WM_LBUTTONDBLCLK = 0x0203;
    public const int WM_RBUTTONUP = 0x0205;

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    public static bool IsOwnWindow(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out uint processId);
        return processId == Environment.ProcessId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeProcessHandle OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(Microsoft.Win32.SafeHandles.SafeProcessHandle handle, uint milliseconds);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(Microsoft.Win32.SafeHandles.SafeProcessHandle process, uint flags, System.Text.StringBuilder exeName, ref uint size);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern uint RegisterWindowMessage(string message);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hwnd);

    private static readonly ProcessIdentityCache ProcessNames = new(OpenIdentity, () => Environment.TickCount64);

    /// <summary>Executable basename; cache hits validate the retained handle without reopening/enumerating.</summary>
    public static string GetProcessName(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) { return string.Empty; }
        GetWindowThreadProcessId(hwnd, out uint pid);
        string name = ProcessNames.GetName(pid);
        GetWindowThreadProcessId(hwnd, out uint currentPid);
        return currentPid == pid ? name : string.Empty;
    }

    public static void ClearProcessNameCache() => ProcessNames.Dispose();

    private sealed class ProcessIdentity(Microsoft.Win32.SafeHandles.SafeProcessHandle handle, string name) : IProcessIdentity
    {
        public string Name => name;
        // SYNCHRONIZE + a zero-time wait identifies exit even when the PID is subsequently reused.
        public bool IsAlive => WaitForSingleObject(handle, 0) == 0x00000102; // WAIT_TIMEOUT
        public void Dispose() => handle.Dispose();
    }

    private static IProcessIdentity? OpenIdentity(uint pid)
    {
        const uint synchronize = 0x00100000;
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | synchronize, false, pid);
        bool transferred = false;
        try
        {
            if (handle.IsInvalid) { return null; }
            var buffer = new System.Text.StringBuilder(32768);
            uint size = (uint)buffer.Capacity;
            if (!QueryFullProcessImageName(handle, 0, buffer, ref size)) { return null; }
            var identity = new ProcessIdentity(handle, System.IO.Path.GetFileNameWithoutExtension(buffer.ToString(0, (int)size)));
            transferred = true;
            return identity;
        }
        finally
        {
            if (!transferred) { handle.Dispose(); }
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr hwnd, ref POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT point, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public RECT Monitor;
        public RECT Work;
        public uint Flags;
    }

    public static long GetExtendedStyle(IntPtr hwnd)
    {
        Marshal.SetLastPInvokeError(0);
        long style = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        int error = Marshal.GetLastPInvokeError();
        if (style == 0 && error != 0)
        {
            throw new System.ComponentModel.Win32Exception(error);
        }
        return style;
    }

    public static void MakeClickThrough(IntPtr hwnd)
    {
        long style = GetExtendedStyle(hwnd) | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_NOACTIVATE;
        Marshal.SetLastPInvokeError(0);
        var previous = SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(style));
        int error = Marshal.GetLastPInvokeError();
        if (previous == IntPtr.Zero && error != 0)
        {
            throw new System.ComponentModel.Win32Exception(error);
        }
    }

    /// <summary>Makes the overlay hit-testable again for repositioning; layered/non-activating styles are kept.</summary>
    public static void RemoveClickThrough(IntPtr hwnd)
    {
        long style = GetExtendedStyle(hwnd) & ~(long)WS_EX_TRANSPARENT;
        Marshal.SetLastPInvokeError(0);
        var previous = SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(style));
        int error = Marshal.GetLastPInvokeError();
        if (previous == IntPtr.Zero && error != 0)
        {
            throw new System.ComponentModel.Win32Exception(error);
        }
    }

    /// <summary>Client area of <paramref name="hwnd"/> in physical screen pixels, or null when unavailable.</summary>
    public static Core.CaptureRegion? GetClientRegion(IntPtr hwnd)
    {
        var origin = new POINT();
        if (!GetClientRect(hwnd, out var client) || !ClientToScreen(hwnd, ref origin))
        {
            return null;
        }
        var region = new Core.CaptureRegion(origin.X, origin.Y, client.Right - client.Left, client.Bottom - client.Top);
        return region.IsEmpty ? null : region;
    }

    public static Core.CaptureRegion GetScreenRegion(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out var rect))
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
        }
        return new Core.CaptureRegion(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
    }

    public static Core.CaptureRegion? GetCaptureBounds(IntPtr hwnd, int cursorX, int cursorY)
    {
        if (GetClientRegion(hwnd) is not { } client)
        {
            return null;
        }
        var monitor = MonitorFromPoint(new POINT { X = cursorX, Y = cursorY }, 0);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
        {
            return null;
        }
        return Core.RoiBuilder.Intersect(client,
            new Core.CaptureRegion(info.Monitor.Left, info.Monitor.Top,
                info.Monitor.Right - info.Monitor.Left, info.Monitor.Bottom - info.Monitor.Top));
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    public static string GetWindowTitle(IntPtr hwnd)
    {
        var sb = new System.Text.StringBuilder(512);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }
}
