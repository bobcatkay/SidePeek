using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using SidePeek.App.Interop;
using SidePeek.App.Services;

namespace SidePeek.App.Docking;

/// <summary>检测前台窗口是否覆盖指定显示器；在应用的 PerMonitorV2 UI 线程上调用。</summary>
internal sealed class FullscreenWindowDetector
{
    private const string LogTag = "[FullscreenWindowDetector]";
    private const int DwmExtendedFrameBounds = 9;
    private const int DwmCloaked = 14;
    private const int WindowStyleIndex = -16;
    private const int WindowCaptionStyle = 0x00C00000;
    private const int WindowClassNameCapacity = 256;
    private static readonly TimeSpan FailureLogInterval = TimeSpan.FromSeconds(30);

    private readonly Stopwatch _failureLogClock = new();

    public bool IsFullscreenOnMonitor(IntPtr monitor)
    {
        if (monitor == IntPtr.Zero)
            return false;

        IntPtr window = GetForegroundWindow();
        if (window == IntPtr.Zero || !IsWindowVisible(window) || IsIconic(window))
            return false;

        if (GetWindowThreadProcessId(window, out uint processId) == 0 ||
            processId == Environment.ProcessId || IsShellWindow(window))
        {
            return false;
        }

        int cloakResult = DwmGetWindowAttribute(window, DwmCloaked, out int cloaked, sizeof(int));
        if (cloakResult < 0)
        {
            LogFailure($"Unable to read foreground window visibility. HRESULT: 0x{cloakResult:X8}.");
            return false;
        }
        if (cloaked != 0)
            return false;

        // Without a reserved taskbar area, a normal maximized window can also fill rcMonitor.
        // Fullscreen applications normally remove their caption even if they keep WS_MAXIMIZE.
        if (IsZoomed(window) &&
            (GetWindowLong(window, WindowStyleIndex) & WindowCaptionStyle) == WindowCaptionStyle)
        {
            return false;
        }

        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info))
        {
            LogFailure($"Unable to read dock monitor bounds. Win32 error: {Marshal.GetLastWin32Error()}.");
            return false;
        }

        int boundsResult = DwmGetWindowAttribute(window, DwmExtendedFrameBounds,
            out NativeMethods.RECT bounds, Marshal.SizeOf<NativeMethods.RECT>());
        if (boundsResult < 0 && !GetWindowRect(window, out bounds))
        {
            LogFailure($"Unable to read foreground window bounds. Win32 error: {Marshal.GetLastWin32Error()}.");
            return false;
        }

        // DWM bounds exclude invisible resize borders and use physical pixels. Our PerMonitorV2
        // manifest keeps the GetWindowRect fallback and monitor coordinates in that same space.
        // Require full coverage without inward tolerance: even the small work-area inset of an
        // auto-hidden taskbar must not turn ordinary maximized windows into fullscreen windows.
        NativeMethods.RECT screen = info.Monitor;
        return screen.Right > screen.Left && screen.Bottom > screen.Top &&
            bounds.Right > bounds.Left && bounds.Bottom > bounds.Top &&
            bounds.Left <= screen.Left && bounds.Top <= screen.Top &&
            bounds.Right >= screen.Right && bounds.Bottom >= screen.Bottom;
    }

    private static bool IsShellWindow(IntPtr window)
    {
        if (window == GetDesktopWindow() || window == GetShellWindow())
            return true;

        var className = new StringBuilder(WindowClassNameCapacity);
        if (GetClassName(window, className, className.Capacity) == 0)
            return true;

        // WorkerW hosts desktop wallpaper; secondary monitors use a separate taskbar class.
        return className.ToString() is "Progman" or "WorkerW" or
            "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";
    }

    private void LogFailure(string message)
    {
        if (_failureLogClock.IsRunning && _failureLogClock.Elapsed < FailureLogInterval)
            return;

        _failureLogClock.Restart();
        AppLogger.Error($"{LogTag} {message}");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeMethods.RECT Monitor;
        public NativeMethods.RECT WorkArea;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(IntPtr window);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder className, int capacity);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeMethods.RECT bounds);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr window, int attribute,
        out NativeMethods.RECT value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr window, int attribute,
        out int value, int size);
}
