using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using SidePeek.App.Interop;
using SidePeek.App.Services;

namespace SidePeek.App.Views;

/// <summary>单个副显示器的截图宿主，与主窗口共享选区和编辑会话。</summary>
internal sealed class ScreenshotMonitorWindow : Window
{
    private const string LogTag = nameof(ScreenshotMonitorWindow);
    private const double InitialOffscreenPosition = -32000;
    private readonly ScreenshotOverlayWindow _session;
    private readonly Rect _screenBounds;
    private readonly ScreenshotSurface _surface;
    private HwndSource? _source;
    private bool _closed;

    internal ScreenshotMonitorWindow(ScreenshotOverlayWindow session, Rect screenBounds,
        ScreenshotSurface surface)
    {
        _session = session;
        _screenBounds = screenBounds;
        _surface = surface;
        Title = session.Title;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = App.ScreenshotDiagnosticsCaptureVisible;
        Topmost = true;
        Background = Brushes.Black;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = Top = InitialOffscreenPosition;
        Width = Height = 1;
        Content = surface;
        PreviewKeyDown += session.OnScreenshotKey;
        Loaded += (_, _) => PositionOverlay();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _source?.AddHook(WindowMessage);
        if (ScreenshotDiagnostics.SoftwareRendering && _source?.CompositionTarget is { } target)
            target.RenderMode = RenderMode.SoftwareOnly;
        ScreenshotNative.ExcludeFromCapture(this);
        PositionOverlay();
        AppLogger.Info($"{LogTag}: secondary screenshot host initialized.");
    }

    private IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == NativeMethods.WM_DISPLAYCHANGE)
        {
            // 显示器拓扑变化后冻结画面已失效，由主会话统一关闭所有宿主。
            _session.CloseFromMonitor();
        }
        else if (message == NativeMethods.WM_DPICHANGED)
        {
            // 冻结截图按物理像素定位；先让 WPF 更新 DPI，再恢复原屏幕边界，避免采用建议范围后偏移。
            Dispatcher.BeginInvoke(new Action(() => { if (!_closed) PositionOverlay(); }));
        }
        return IntPtr.Zero;
    }

    private void PositionOverlay()
    {
        if (_closed)
            return;

        if (!NativeMethods.SetWindowPos(new WindowInteropHelper(this).Handle, NativeMethods.HWND_TOPMOST,
                (int)_screenBounds.X, (int)_screenBounds.Y, (int)_screenBounds.Width, (int)_screenBounds.Height,
                NativeMethods.SWP_NOACTIVATE))
        {
            int error = Marshal.GetLastWin32Error();
            AppLogger.Error($"{LogTag}: unable to position secondary screenshot host; Win32 error={error}.");
            throw new Win32Exception(error);
        }
        _surface.UpdateVisuals();
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        if (_source is { IsDisposed: false })
            _source.RemoveHook(WindowMessage);
        _source = null;
        PreviewKeyDown -= _session.OnScreenshotKey;
        base.OnClosed(e);
        // Alt+F4 关闭任一屏时结束整次截图；异步且由主会话检查状态，避免 Owner 关闭引发重入。
        _session.CloseFromMonitor();
    }
}
