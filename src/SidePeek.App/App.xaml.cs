using System.Threading;
using System.Windows;
using System.Windows.Threading;
using SidePeek.App.Services;
using SidePeek.App.Views;

namespace SidePeek.App;

public partial class App : Application
{
    private const string ScreenshotDiagnosticsArgument = "--screenshot-diagnostics";
    private const string ScreenshotCaptureVisibleArgument = "--screenshot-capture-visible";
    private const string ScreenshotReplayArgument = "--screenshot-replay";
    private Mutex? _singleInstance;
    private TrayService? _tray;
    private DockWindow? _window;
    internal ScreenshotService? Screenshots { get; private set; }
    internal static bool ScreenshotDiagnosticsEnabled { get; private set; }
    internal static bool ScreenshotDiagnosticsCaptureVisible { get; private set; }
    internal static bool ScreenshotDiagnosticsReplayEnabled { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new Mutex(initiallyOwned: true, "SidePeek_SingleInstance_Mutex", out bool created);
        if (!created)
        {
            Shutdown();
            return;
        }

        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        // 诊断及其子选项只对本次进程生效；默认保持原捕获排除设置，避免改变性能测量条件。
        ScreenshotDiagnosticsEnabled = e.Args.Any(argument => string.Equals(
            argument, ScreenshotDiagnosticsArgument, StringComparison.OrdinalIgnoreCase));
        ScreenshotDiagnosticsCaptureVisible = ScreenshotDiagnosticsEnabled && e.Args.Any(argument => string.Equals(
            argument, ScreenshotCaptureVisibleArgument, StringComparison.OrdinalIgnoreCase));
        ScreenshotDiagnosticsReplayEnabled = ScreenshotDiagnosticsEnabled && e.Args.Any(argument => string.Equals(
            argument, ScreenshotReplayArgument, StringComparison.OrdinalIgnoreCase));

        SettingsService.Initialize();
        ThemeService.Apply(SettingsService.Current.Theme);
        FontService.Apply(SettingsService.Current.FontFamilyName);
        SettingsService.Changed += OnSettingsChanged;
        AppLogger.Info("SidePeek started.");
        DispatcherUnhandledException += OnUnhandledException;

        _window = new DockWindow();
        _window.Show();

        Screenshots = new ScreenshotService(_window);
        _tray = new TrayService(_window);

        if (ScreenshotDiagnosticsEnabled)
        {
            AppLogger.Info($"[ScreenshotDiagnostics] Process-only diagnostics enabled; captureVisible={ScreenshotDiagnosticsCaptureVisible}; automaticSyntheticReplay={ScreenshotDiagnosticsReplayEnabled}. Rendering callback observation requires SIDEPEEK_SCREENSHOT_DIAGNOSTICS=1 separately.");
            // 等初始窗口完成加载后自动进入一次截图，便于本地诊断工具观察，无需发送全局热键。
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => Screenshots?.Start()));
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SettingsService.Changed -= OnSettingsChanged;
        Screenshots?.Dispose();
        _tray?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
        => FontService.Apply(SettingsService.Current.FontFamilyName);

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            AppLogger.Error("Unhandled dispatcher exception.", e.Exception);
        }
        catch { /* ignore */ }

        MessageBox.Show(e.Exception.ToString(), "SidePeek - 未处理异常",
            MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
