using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace SidePeek.App.Interop;

/// <summary>截图使用物理像素坐标，避免混合 DPI 屏幕上的选区偏移。</summary>
internal static class ScreenshotNative
{
    private const int DwmExtendedFrameBounds = 9;
    private const int DwmCloaked = 14;
    private const uint QueryOnlyActivePaths = 2;
    private const int PathInfoSize = 72;
    private const int ModeInfoSize = 64;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowProc callback, IntPtr data);
    private delegate bool EnumWindowProc(IntPtr hwnd, IntPtr data);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeMethods.RECT rect);
    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out NativeMethods.RECT value, int size);
    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
    [DllImport("dwmapi.dll")]
    internal static extern int DwmFlush();
    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint paths, IntPtr pathArray,
        ref uint modes, IntPtr modeArray, IntPtr topologyId);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    private static extern int GetSourceName(ref SourceDeviceName request);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    private static extern int GetWhiteLevel(ref SdrWhiteLevel request);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct DeviceInfoHeader
    {
        public uint Type, Size;
        public long AdapterId;
        public uint Id;
    }

    // DISPLAYCONFIG_DEVICE_INFO_HEADER has 4-byte alignment, including its LUID.
    [StructLayout(LayoutKind.Sequential, Pack = 4, CharSet = CharSet.Unicode)]
    private struct SourceDeviceName
    {
        public DeviceInfoHeader Header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string Name;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct SdrWhiteLevel
    {
        public DeviceInfoHeader Header;
        public uint Level;
    }

    internal static IReadOnlyList<Rect> WindowBounds()
    {
        var result = new List<Rect>();
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out uint processId);
            if (processId == Environment.ProcessId || !IsWindowVisible(hwnd) || IsIconic(hwnd))
                return true;
            if (DwmGetWindowAttribute(hwnd, DwmCloaked, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
                return true;
            if (DwmGetWindowAttribute(hwnd, DwmExtendedFrameBounds, out NativeMethods.RECT rect,
                    Marshal.SizeOf<NativeMethods.RECT>()) != 0 && !GetWindowRect(hwnd, out rect))
                return true;
            if (rect.Right > rect.Left && rect.Bottom > rect.Top)
                result.Add(new Rect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top));
            return true;
        }, IntPtr.Zero);
        return result;
    }

    internal static void ExcludeFromCapture(Window window)
        => SetWindowDisplayAffinity(new WindowInteropHelper(window).Handle, 0x11);

    /// <summary>Windows HDR 的 SDR 白电平：1000 = scRGB 1.0 = 80 nit。</summary>
    internal static float SdrWhiteScale(string deviceName)
    {
        // Display topology may change between querying the sizes and fetching the paths.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (GetDisplayConfigBufferSizes(QueryOnlyActivePaths, out uint pathCount, out uint modeCount) != 0)
                break;
            IntPtr paths = Marshal.AllocHGlobal(checked((int)pathCount * PathInfoSize));
            IntPtr modes = Marshal.AllocHGlobal(checked((int)modeCount * ModeInfoSize));
            try
            {
                int error = QueryDisplayConfig(QueryOnlyActivePaths, ref pathCount, paths,
                    ref modeCount, modes, IntPtr.Zero);
                if (error == 122) // ERROR_INSUFFICIENT_BUFFER
                    continue;
                if (error != 0)
                    break;
                for (int index = 0; index < pathCount; index++)
                {
                    IntPtr path = IntPtr.Add(paths, index * PathInfoSize);
                    var name = new SourceDeviceName
                    {
                        Header = new DeviceInfoHeader
                        {
                            Type = 1, Size = (uint)Marshal.SizeOf<SourceDeviceName>(),
                            AdapterId = Marshal.ReadInt64(path), Id = (uint)Marshal.ReadInt32(path, 8)
                        },
                        Name = string.Empty
                    };
                    if (GetSourceName(ref name) != 0 || !string.Equals(name.Name, deviceName, StringComparison.OrdinalIgnoreCase))
                        continue;
                    var white = new SdrWhiteLevel
                    {
                        Header = new DeviceInfoHeader
                        {
                            Type = 11, Size = (uint)Marshal.SizeOf<SdrWhiteLevel>(),
                            AdapterId = Marshal.ReadInt64(path, 20), Id = (uint)Marshal.ReadInt32(path, 28)
                        }
                    };
                    if (GetWhiteLevel(ref white) == 0 && white.Level > 0)
                        return Math.Max(1f, white.Level / 1000f);
                }
                break;
            }
            finally
            {
                Marshal.FreeHGlobal(modes);
                Marshal.FreeHGlobal(paths);
            }
        }
        // Default Windows SDR reference white is 80 nit when the query is unavailable.
        return 1f;
    }
}
