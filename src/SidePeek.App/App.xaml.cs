using System.Threading;
using System.Windows;
using System.Windows.Threading;
using SidePeek.App.Services;
using SidePeek.App.Views;

namespace SidePeek.App;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private TrayService? _tray;
    private DockWindow? _window;

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

        SettingsService.Initialize();
        ThemeService.Apply(SettingsService.Current.Theme);
        AppLogger.Info("SidePeek started.");
        DispatcherUnhandledException += OnUnhandledException;

        _window = new DockWindow();
        _window.Show();

        _tray = new TrayService(_window);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

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
