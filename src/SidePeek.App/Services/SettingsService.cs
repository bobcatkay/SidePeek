using System;
using System.Collections.Generic;
using SidePeek.App.Models;

namespace SidePeek.App.Services;

public static class SettingsService
{
    private const string FileName = "settings.json";
    public const int MinCollapseDelayMs = 150;
    public const int MaxCollapseDelayMs = 2000;
    public const int MinNoteHistoryMonths = 1;
    public const int MaxNoteHistoryMonths = 60;

    private static readonly HashSet<string> SupportedHotkeyKeys = BuildSupportedHotkeyKeys();
    private static AppSettings _current = new();
    private static bool _initialized;

    public static event EventHandler? Changed;

    public static AppSettings Current
    {
        get
        {
            EnsureInitialized();
            return _current;
        }
    }

    public static void Initialize()
    {
        _current = JsonStore.Load(FileName, () => new AppSettings());
        Normalize(_current);

        // The registry is the source of truth for this machine-level switch.
        _current.StartWithWindows = StartupService.IsEnabled;
        _initialized = true;
        Persist(raiseChanged: false);
    }

    public static void Update(Action<AppSettings> update)
    {
        EnsureInitialized();
        bool previousStartWithWindows = _current.StartWithWindows;
        update(_current);
        Normalize(_current);
        if (previousStartWithWindows != _current.StartWithWindows)
            StartupService.IsEnabled = _current.StartWithWindows;
        Persist(raiseChanged: true);
    }

    public static void SetDockEdge(DockEdge edge)
    {
        if (Current.DockEdge == edge)
            return;
        Update(settings => settings.DockEdge = edge);
    }

    private static void EnsureInitialized()
    {
        if (!_initialized)
            Initialize();
    }

    private static void Persist(bool raiseChanged)
    {
        JsonStore.Save(FileName, _current);
        if (raiseChanged)
            Changed?.Invoke(null, EventArgs.Empty);
    }

    private static void Normalize(AppSettings settings)
    {
        if (settings.DockEdge is not DockEdge.Left and not DockEdge.Right)
            settings.DockEdge = DockEdge.Right;

        settings.CollapseDelayMs = Math.Clamp(
            settings.CollapseDelayMs,
            MinCollapseDelayMs,
            MaxCollapseDelayMs);

        settings.NoteHistoryMonths = Math.Clamp(
            settings.NoteHistoryMonths,
            MinNoteHistoryMonths,
            MaxNoteHistoryMonths);

        settings.Hotkey ??= new HotkeySettings();

        settings.Hotkey.Key = settings.Hotkey.Key.Trim().ToUpperInvariant();
        if (!SupportedHotkeyKeys.Contains(settings.Hotkey.Key))
            settings.Hotkey.Key = "S";

        if (!settings.Hotkey.Control && !settings.Hotkey.Alt && !settings.Hotkey.Shift)
            settings.Hotkey.Control = true;
    }

    private static HashSet<string> BuildSupportedHotkeyKeys()
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (char c = 'A'; c <= 'Z'; c++)
            keys.Add(c.ToString());

        for (int i = 1; i <= 12; i++)
            keys.Add($"F{i}");

        return keys;
    }
}
