using System.Windows;

namespace SidePeek.App.Services;

/// <summary>本次进程的截图对照选项，不写入用户设置。</summary>
internal static class ScreenshotDiagnostics
{
    private const string EnabledValue = "1";
    private const string MonitorVariable = "SIDEPEEK_SCREENSHOT_REPLAY_MONITOR";
    internal static bool ExitAfterReplay => IsEnabled("SIDEPEEK_SCREENSHOT_EXIT_AFTER_REPLAY");
    internal static bool SoftwareRendering => IsEnabled("SIDEPEEK_SCREENSHOT_SOFTWARE_RENDERING");
    internal static bool HideToolbar => IsEnabled("SIDEPEEK_SCREENSHOT_HIDE_TOOLBAR");
    internal static bool WpfRenderer => IsEnabled("SIDEPEEK_SCREENSHOT_WPF_RENDERER");
    internal static bool InteractionRegression => IsEnabled("SIDEPEEK_SCREENSHOT_INTERACTION_TEST");
    internal static bool AcrossScreensReplay => IsEnabled("SIDEPEEK_SCREENSHOT_ACROSS_SCREENS");

    private static bool IsEnabled(string name) => App.ScreenshotDiagnosticsEnabled
        && Environment.GetEnvironmentVariable(name) == EnabledValue;

    internal static CaptureMonitor ReplayMonitor(DesktopCapture capture)
    {
        if (App.ScreenshotDiagnosticsEnabled
            && int.TryParse(Environment.GetEnvironmentVariable(MonitorVariable), out int index)
            && index >= 0 && index < capture.Monitors.Count)
            return capture.Monitors[index];
        return capture.Monitors.FirstOrDefault(monitor => monitor.Bounds.Contains(new Point(0, 0)))
            ?? capture.Monitors[0];
    }
}
