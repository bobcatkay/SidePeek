using System.Threading;
using System.Windows;
using System.Windows.Threading;
using SidePeek.App.Services;
using SidePeek.App.Views;
using Wpf.Ui.Appearance;

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

        ApplicationThemeManager.Apply(ApplicationTheme.Light);
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
            string log = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sidepeek_error.log");
            System.IO.File.WriteAllText(log, e.Exception.ToString());
        }
        catch { /* ignore */ }

        MessageBox.Show(e.Exception.ToString(), "SidePeek - 未处理异常",
            MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
