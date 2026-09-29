using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using SidePeek.App.Interop;
using SidePeek.App.Models;
using SidePeek.App.Views;

namespace SidePeek.App.Services;

/// <summary>全局截图入口及生命周期，采集和编辑期间暂停、隐藏侧边栏。</summary>
internal sealed class ScreenshotService : IDisposable
{
    private const int FirstHotkeyId = 0xB002, SecondHotkeyId = 0xB003;
    private readonly DockWindow _owner;
    private readonly IntPtr _hwnd;
    private readonly HwndSource? _source;
    private ScreenshotOverlayWindow? _session;
    private CancellationTokenSource? _captureCancellation;
    private int _hotkeyId;
    private string? _hotkeySignature;
    private bool _disposed;

    internal bool IsBusy { get; private set; }
    internal string HotkeyError { get; private set; } = string.Empty;

    internal ScreenshotService(DockWindow owner)
    {
        _owner = owner;
        _hwnd = new WindowInteropHelper(owner).Handle;
        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(WindowMessage);
        _owner.Closed += OnOwnerClosed;
        SettingsService.Changed += OnSettingsChanged;
        TryRegisterHotkey(SettingsService.Current.ScreenshotHotkey, out _);
    }

    internal static string DescribeHotkey(HotkeySettings key)
        => string.Join(" + ", new[] { key.Control ? "Ctrl" : null, key.Alt ? "Alt" : null,
            key.Shift ? "Shift" : null, key.Key }.Where(part => part is not null));

    internal bool TryRegisterHotkey(HotkeySettings hotkey, out string error)
    {
        error = string.Empty;
        string signature = DescribeHotkey(hotkey);
        if (_hotkeyId != 0 && signature == _hotkeySignature)
        {
            HotkeyError = string.Empty;
            return true;
        }
        uint modifiers = NativeMethods.MOD_NOREPEAT;
        if (hotkey.Control) modifiers |= NativeMethods.MOD_CONTROL;
        if (hotkey.Alt) modifiers |= NativeMethods.MOD_ALT;
        if (hotkey.Shift) modifiers |= NativeMethods.MOD_SHIFT;
        if (_disposed || modifiers == NativeMethods.MOD_NOREPEAT ||
            !Enum.TryParse(hotkey.Key, ignoreCase: true, out Key key) || KeyInterop.VirtualKeyFromKey(key) == 0)
        {
            error = HotkeyError = "截图快捷键需要修饰键以及 A–Z 或 F1–F12。";
            return false;
        }
        // Register the replacement first. If it is occupied, the original stays usable.
        int nextId = _hotkeyId == FirstHotkeyId ? SecondHotkeyId : FirstHotkeyId;
        if (!NativeMethods.RegisterHotKey(_hwnd, nextId, modifiers, (uint)KeyInterop.VirtualKeyFromKey(key)))
        {
            string fallback = _hotkeyId == 0 ? "请修改设置或从托盘截图。" : "原快捷键仍有效，也可从托盘截图。";
            error = HotkeyError = $"截图快捷键 {signature} 注册失败，可能已被占用。{fallback}";
            AppLogger.Error($"Unable to register screenshot hotkey; Win32 error: {Marshal.GetLastWin32Error()}.");
            return false;
        }
        if (_hotkeyId != 0) NativeMethods.UnregisterHotKey(_hwnd, _hotkeyId);
        _hotkeyId = nextId;
        _hotkeySignature = signature;
        HotkeyError = string.Empty;
        return true;
    }

    private IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == NativeMethods.WM_HOTKEY && _hotkeyId != 0 && wParam.ToInt32() == _hotkeyId)
        {
            handled = true;
            _owner.Dispatcher.BeginInvoke(new Action(Start));
        }
        return IntPtr.Zero;
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
        => TryRegisterHotkey(SettingsService.Current.ScreenshotHotkey, out _);

    private void OnOwnerClosed(object? sender, EventArgs e) => Dispose();

    internal async void Start()
    {
        if (_disposed) return;
        if (IsBusy)
        {
            _session?.ActivateTools();
            return;
        }
        IsBusy = true;
        bool wasVisible = _owner.IsVisible;
        IntPtr foreground = ScreenshotNative.GetForegroundWindow();
        using var cancellation = new CancellationTokenSource();
        _captureCancellation = cancellation;
        Exception? failure = null;
        try
        {
            _owner.SuspendDockForScreenshot();
            _owner.Hide();
            await Task.Delay(120, cancellation.Token);
            await Task.Run(() => ScreenshotNative.DwmFlush(), cancellation.Token);
            var capture = await ScreenshotCapture.CaptureAsync(cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            var closed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _session = new ScreenshotOverlayWindow(capture);
            _session.Closed += (_, _) => closed.TrySetResult(true);
            _session.Show();
            await closed.Task;
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            failure = error;
            AppLogger.Error("Screenshot session failed.", error);
        }
        finally
        {
            _session?.Close();
            _session = null;
            _captureCancellation = null;
            IsBusy = false;
            if (!_disposed)
            {
                if (wasVisible)
                {
                    bool activated = _owner.ShowActivated;
                    _owner.ShowActivated = false;
                    _owner.Show();
                    _owner.ShowActivated = activated;
                }
                _owner.ResumeDockAfterScreenshot();
                if (foreground != IntPtr.Zero) ScreenshotNative.SetForegroundWindow(foreground);
            }
        }
        if (failure is not null && !_disposed)
            MessageBox.Show(_owner, $"无法截取屏幕：{failure.Message}", "SidePeek 截图", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _owner.Closed -= OnOwnerClosed;
        SettingsService.Changed -= OnSettingsChanged;
        if (_source is { IsDisposed: false }) _source.RemoveHook(WindowMessage);
        if (_hotkeyId != 0) NativeMethods.UnregisterHotKey(_hwnd, _hotkeyId);
        _captureCancellation?.Cancel();
        _session?.Close();
    }
}
