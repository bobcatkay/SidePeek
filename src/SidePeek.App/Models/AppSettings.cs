namespace SidePeek.App.Models;

public enum AppThemeMode
{
    Light,
    Dark,
    System
}

public sealed class HotkeySettings
{
    public bool Control { get; set; } = true;
    public bool Alt { get; set; } = true;
    public bool Shift { get; set; }
    public string Key { get; set; } = "S";
}

public sealed class AppSettings
{
    public const int DefaultExpandDelayMs = 1000;
    public const int DefaultCollapseDelayMs = 450;

    public DockEdge DockEdge { get; set; } = DockEdge.Right;
    public string DockDisplayDeviceName { get; set; } = string.Empty;
    public int ExpandDelayMs { get; set; } = DefaultExpandDelayMs;
    public int CollapseDelayMs { get; set; } = DefaultCollapseDelayMs;
    public int NoteHistoryMonths { get; set; } = 12;
    public AppThemeMode Theme { get; set; } = AppThemeMode.Light;
    public bool StartWithWindows { get; set; }
    public HotkeySettings Hotkey { get; set; } = new();
    public HotkeySettings ScreenshotHotkey { get; set; } = new() { Key = "A" };
}
