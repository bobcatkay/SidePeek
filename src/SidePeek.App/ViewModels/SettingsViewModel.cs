using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using SidePeek.App.Models;
using SidePeek.App.Services;
using Forms = System.Windows.Forms;

namespace SidePeek.App.ViewModels;

public sealed class SettingsOption<T>
{
    public SettingsOption(string label, T value)
    {
        Label = label;
        Value = value;
    }

    public string Label { get; }
    public T Value { get; }
}

public sealed class SettingsViewModel : ObservableObject
{
    private bool _loading;
    private SettingsOption<string> _selectedDockDisplay = null!;
    private SettingsOption<DockEdge> _selectedDockEdge = null!;
    private SettingsOption<AppThemeMode> _selectedTheme = null!;
    private double _expandDelayMs;
    private double _collapseDelayMs;
    private double _noteHistoryMonths;
    private bool _startWithWindows;
    private bool _hotkeyControl;
    private bool _hotkeyAlt;
    private bool _hotkeyShift;
    private string _hotkeyKey = "S";
    private string _hotkeyPreview = string.Empty;
    private string _hotkeyError = string.Empty;
    private bool _screenshotHotkeyControl;
    private bool _screenshotHotkeyAlt;
    private bool _screenshotHotkeyShift;
    private string _screenshotHotkeyKey = "A";
    private string _screenshotHotkeyPreview = string.Empty;
    private string _screenshotHotkeyError = string.Empty;

    public SettingsViewModel()
    {
        DockDisplays = BuildDockDisplays();

        DockEdges = new[]
        {
            new SettingsOption<DockEdge>("左侧", DockEdge.Left),
            new SettingsOption<DockEdge>("右侧", DockEdge.Right),
        };

        Themes = new[]
        {
            new SettingsOption<AppThemeMode>("浅色", AppThemeMode.Light),
            new SettingsOption<AppThemeMode>("深色", AppThemeMode.Dark),
            new SettingsOption<AppThemeMode>("跟随系统", AppThemeMode.System),
        };

        HotkeyKeys = Enumerable.Range('A', 26)
            .Select(value => ((char)value).ToString())
            .Concat(Enumerable.Range(1, 12).Select(value => $"F{value}"))
            .ToArray();

        LoadFromSettings();
        SettingsService.Changed += OnSettingsChanged;
    }

    public IReadOnlyList<SettingsOption<string>> DockDisplays { get; }
    public IReadOnlyList<SettingsOption<DockEdge>> DockEdges { get; }
    public IReadOnlyList<SettingsOption<AppThemeMode>> Themes { get; }
    public IReadOnlyList<string> HotkeyKeys { get; }
    public string VersionText { get; } = $"v{GetDisplayVersion()}";
    public static double ExpandDelayMinimumMs => SettingsService.MinExpandDelayMs;
    public static double ExpandDelayMaximumMs => SettingsService.MaxExpandDelayMs;
    public static double ExpandDelayTickMs => SettingsService.ExpandDelayStepMs;
    public static double CollapseDelayMinimumMs => SettingsService.MinCollapseDelayMs;
    public static double CollapseDelayMaximumMs => SettingsService.MaxCollapseDelayMs;
    public static double CollapseDelayTickMs => SettingsService.CollapseDelayStepMs;

    public SettingsOption<string> SelectedDockDisplay
    {
        get => _selectedDockDisplay;
        set
        {
            if (value is null || !SetProperty(ref _selectedDockDisplay, value) || _loading)
                return;
            SettingsService.Update(settings => settings.DockDisplayDeviceName = value.Value);
        }
    }

    public SettingsOption<DockEdge> SelectedDockEdge
    {
        get => _selectedDockEdge;
        set
        {
            if (value is null || !SetProperty(ref _selectedDockEdge, value) || _loading)
                return;
            SettingsService.Update(settings => settings.DockEdge = value.Value);
        }
    }

    public SettingsOption<AppThemeMode> SelectedTheme
    {
        get => _selectedTheme;
        set
        {
            if (value is null || !SetProperty(ref _selectedTheme, value) || _loading)
                return;
            SettingsService.Update(settings => settings.Theme = value.Value);
        }
    }

    public double ExpandDelayMs
    {
        get => _expandDelayMs;
        set
        {
            double normalized = Math.Round(value / SettingsService.ExpandDelayStepMs)
                * SettingsService.ExpandDelayStepMs;
            if (!SetProperty(ref _expandDelayMs, normalized) || _loading)
                return;
            SettingsService.Update(settings => settings.ExpandDelayMs = (int)normalized);
        }
    }

    public double CollapseDelayMs
    {
        get => _collapseDelayMs;
        set
        {
            double normalized = Math.Round(value / SettingsService.CollapseDelayStepMs)
                * SettingsService.CollapseDelayStepMs;
            if (!SetProperty(ref _collapseDelayMs, normalized) || _loading)
                return;
            SettingsService.Update(settings => settings.CollapseDelayMs = (int)normalized);
        }
    }

    public double NoteHistoryMonths
    {
        get => _noteHistoryMonths;
        set
        {
            double normalized = Math.Round(value);
            if (!SetProperty(ref _noteHistoryMonths, normalized) || _loading)
                return;
            SettingsService.Update(settings => settings.NoteHistoryMonths = (int)normalized);
        }
    }

    public bool StartWithWindows
    {
        get => _startWithWindows;
        set
        {
            if (!SetProperty(ref _startWithWindows, value) || _loading)
                return;
            SettingsService.Update(settings => settings.StartWithWindows = value);
        }
    }

    public bool HotkeyControl
    {
        get => _hotkeyControl;
        set
        {
            if (!SetProperty(ref _hotkeyControl, value) || _loading)
                return;
            UpdateHotkey();
        }
    }

    public bool HotkeyAlt
    {
        get => _hotkeyAlt;
        set
        {
            if (!SetProperty(ref _hotkeyAlt, value) || _loading)
                return;
            UpdateHotkey();
        }
    }

    public bool HotkeyShift
    {
        get => _hotkeyShift;
        set
        {
            if (!SetProperty(ref _hotkeyShift, value) || _loading)
                return;
            UpdateHotkey();
        }
    }

    public string HotkeyKey
    {
        get => _hotkeyKey;
        set
        {
            if (string.IsNullOrWhiteSpace(value) || !SetProperty(ref _hotkeyKey, value) || _loading)
                return;
            UpdateHotkey();
        }
    }

    public string HotkeyPreview
    {
        get => _hotkeyPreview;
        private set => SetProperty(ref _hotkeyPreview, value);
    }

    public string HotkeyError
    {
        get => _hotkeyError;
        private set => SetProperty(ref _hotkeyError, value);
    }

    public bool ScreenshotHotkeyControl
    {
        get => _screenshotHotkeyControl;
        set { if (SetProperty(ref _screenshotHotkeyControl, value) && !_loading) UpdateScreenshotHotkey(); }
    }
    public bool ScreenshotHotkeyAlt
    {
        get => _screenshotHotkeyAlt;
        set { if (SetProperty(ref _screenshotHotkeyAlt, value) && !_loading) UpdateScreenshotHotkey(); }
    }
    public bool ScreenshotHotkeyShift
    {
        get => _screenshotHotkeyShift;
        set { if (SetProperty(ref _screenshotHotkeyShift, value) && !_loading) UpdateScreenshotHotkey(); }
    }
    public string ScreenshotHotkeyKey
    {
        get => _screenshotHotkeyKey;
        set
        {
            if (!string.IsNullOrWhiteSpace(value) && SetProperty(ref _screenshotHotkeyKey, value) && !_loading)
                UpdateScreenshotHotkey();
        }
    }
    public string ScreenshotHotkeyPreview
    {
        get => _screenshotHotkeyPreview;
        private set => SetProperty(ref _screenshotHotkeyPreview, value);
    }
    public string ScreenshotHotkeyError
    {
        get => _screenshotHotkeyError;
        private set => SetProperty(ref _screenshotHotkeyError, value);
    }

    private void OnSettingsChanged(object? sender, EventArgs e) => LoadFromSettings();

    private void LoadFromSettings()
    {
        _loading = true;
        try
        {
            AppSettings settings = SettingsService.Current;
            SelectedDockDisplay = DockDisplays.FirstOrDefault(option =>
                    string.Equals(option.Value, settings.DockDisplayDeviceName, StringComparison.OrdinalIgnoreCase))
                ?? DockDisplays[0];
            SelectedDockEdge = DockEdges.First(option => option.Value == settings.DockEdge);
            SelectedTheme = Themes.First(option => option.Value == settings.Theme);
            ExpandDelayMs = settings.ExpandDelayMs;
            CollapseDelayMs = settings.CollapseDelayMs;
            NoteHistoryMonths = settings.NoteHistoryMonths;
            StartWithWindows = settings.StartWithWindows;
            HotkeyControl = settings.Hotkey.Control;
            HotkeyAlt = settings.Hotkey.Alt;
            HotkeyShift = settings.Hotkey.Shift;
            HotkeyKey = settings.Hotkey.Key;
            RefreshHotkeyPreview();
            ScreenshotHotkeyControl = settings.ScreenshotHotkey.Control;
            ScreenshotHotkeyAlt = settings.ScreenshotHotkey.Alt;
            ScreenshotHotkeyShift = settings.ScreenshotHotkey.Shift;
            ScreenshotHotkeyKey = settings.ScreenshotHotkey.Key;
            ScreenshotHotkeyPreview = ScreenshotService.DescribeHotkey(settings.ScreenshotHotkey);
            ScreenshotHotkeyError = (System.Windows.Application.Current as App)?.Screenshots?.HotkeyError ?? string.Empty;
        }
        finally
        {
            _loading = false;
        }
    }

    private void UpdateHotkey()
    {
        var requested = new HotkeySettings
        {
            Control = HotkeyControl, Alt = HotkeyAlt, Shift = HotkeyShift, Key = HotkeyKey
        };
        if (ScreenshotService.DescribeHotkey(requested) == ScreenshotService.DescribeHotkey(SettingsService.Current.ScreenshotHotkey))
        {
            LoadFromSettings();
            HotkeyError = "侧边栏热键和截图热键不能相同。";
            return;
        }
        HotkeyError = string.Empty;
        SettingsService.Update(settings =>
        {
            settings.Hotkey.Control = HotkeyControl;
            settings.Hotkey.Alt = HotkeyAlt;
            settings.Hotkey.Shift = HotkeyShift;
            settings.Hotkey.Key = HotkeyKey;
        });
        RefreshHotkeyPreview();
    }

    private void UpdateScreenshotHotkey()
    {
        var next = new HotkeySettings
        {
            Control = ScreenshotHotkeyControl, Alt = ScreenshotHotkeyAlt,
            Shift = ScreenshotHotkeyShift, Key = ScreenshotHotkeyKey
        };
        string error = string.Empty;
        if (!next.Control && !next.Alt && !next.Shift)
            error = "请至少选择一个修饰键。";
        else if (ScreenshotService.DescribeHotkey(next) == ScreenshotService.DescribeHotkey(SettingsService.Current.Hotkey))
            error = "侧边栏热键和截图热键不能相同。";
        else if (System.Windows.Application.Current is App { Screenshots: { } service })
            service.TryRegisterHotkey(next, out error);
        if (!string.IsNullOrEmpty(error))
        {
            LoadFromSettings();
            ScreenshotHotkeyError = error;
            return;
        }
        SettingsService.Update(settings => settings.ScreenshotHotkey = next);
        ScreenshotHotkeyPreview = ScreenshotService.DescribeHotkey(next);
        ScreenshotHotkeyError = string.Empty;
    }

    private void RefreshHotkeyPreview()
    {
        var parts = new List<string>();
        if (HotkeyControl)
            parts.Add("Ctrl");
        if (HotkeyAlt)
            parts.Add("Alt");
        if (HotkeyShift)
            parts.Add("Shift");
        parts.Add(HotkeyKey);

        HotkeyPreview = string.Join(" + ", parts);
    }

    private static IReadOnlyList<SettingsOption<string>> BuildDockDisplays()
    {
        Forms.Screen[] screens = Forms.Screen.AllScreens;
        if (screens.Length == 0)
            return new[] { new SettingsOption<string>("默认显示器", string.Empty) };

        return screens
            .Select((screen, index) =>
            {
                string primarySuffix = screen.Primary ? "（主屏）" : string.Empty;
                string label = $"显示器 {index + 1}{primarySuffix} · {screen.Bounds.Width}x{screen.Bounds.Height}";
                return new SettingsOption<string>(label, screen.DeviceName);
            })
            .ToArray();
    }

    private static string GetDisplayVersion()
    {
        string version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
            ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)
            ?? "0.0.0";

        int metadataIndex = version.IndexOf('+');
        return metadataIndex > 0 ? version[..metadataIndex] : version;
    }
}
