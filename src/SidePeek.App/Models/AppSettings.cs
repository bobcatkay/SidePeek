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
    public DockEdge DockEdge { get; set; } = DockEdge.Right;
    public string DockDisplayDeviceName { get; set; } = string.Empty;
    public int CollapseDelayMs { get; set; } = 450;
    public int NoteHistoryMonths { get; set; } = 12;
    public AppThemeMode Theme { get; set; } = AppThemeMode.Light;
    public bool StartWithWindows { get; set; }
    public HotkeySettings Hotkey { get; set; } = new();
}
