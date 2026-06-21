using System;
using System.Linq;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using SidePeek.App.Docking;
using SidePeek.App.Interop;
using SidePeek.App.Models;
using SidePeek.App.Services;

namespace SidePeek.App.Views;

public partial class DockWindow : Window
{
    private const int HotkeyId = 0xB001;

    private DockManager? _dock;
    private IntPtr _hwnd;

    private NotesView? _notesView;
    private CommandsView? _commandsView;
    private WidgetsView? _widgetsView;
    private ClipboardView? _clipboardView;
    private SettingsView? _settingsView;
    private bool _hotkeyRegistered;
    private string? _registeredHotkeySignature;

    public DockWindow()
    {
        InitializeComponent();
    }

    public void SuspendDock() => _dock?.Suspend();

    public void ResumeDock() => _dock?.Resume();

    /// <summary>展开/收起切换（供托盘双击 / 菜单调用）。</summary>
    public void ToggleVisibility() => _dock?.Toggle();

    public void OpenSettings()
    {
        ContentHost.Content = _settingsView ??= new SettingsView();
        _dock?.Reveal();
        Activate();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        ApplyAcrylicBackdrop();
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        RegisterHotkey();
    }

    private void RegisterHotkey()
    {
        if (_hwnd == IntPtr.Zero)
            return;

        HotkeySettings hotkey = SettingsService.Current.Hotkey;
        string signature = DescribeHotkey(hotkey);
        if (_hotkeyRegistered && string.Equals(_registeredHotkeySignature, signature, StringComparison.Ordinal))
            return;

        UnregisterHotkey();

        uint modifiers = 0;
        if (hotkey.Control)
            modifiers |= NativeMethods.MOD_CONTROL;
        if (hotkey.Alt)
            modifiers |= NativeMethods.MOD_ALT;
        if (hotkey.Shift)
            modifiers |= NativeMethods.MOD_SHIFT;

        if (modifiers == 0 || !Enum.TryParse(hotkey.Key, ignoreCase: true, out Key key))
            return;

        uint virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey == 0)
            return;

        _hotkeyRegistered = NativeMethods.RegisterHotKey(_hwnd, HotkeyId, modifiers, virtualKey);
        if (!_hotkeyRegistered)
            AppLogger.Error($"Unable to register global hotkey: {DescribeHotkey(hotkey)}.");
        else
            _registeredHotkeySignature = signature;
    }

    private void UnregisterHotkey()
    {
        if (!_hotkeyRegistered || _hwnd == IntPtr.Zero)
            return;

        NativeMethods.UnregisterHotKey(_hwnd, HotkeyId);
        _hotkeyRegistered = false;
        _registeredHotkeySignature = null;
    }

    private static string DescribeHotkey(HotkeySettings hotkey)
    {
        string modifiers = string.Join("+", new[]
        {
            hotkey.Control ? "Ctrl" : string.Empty,
            hotkey.Alt ? "Alt" : string.Empty,
            hotkey.Shift ? "Shift" : string.Empty
        }.Where(part => !string.IsNullOrEmpty(part)));

        return string.IsNullOrEmpty(modifiers) ? hotkey.Key : $"{modifiers}+{hotkey.Key}";
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            _dock?.Toggle();
            handled = true;
        }
        return IntPtr.Zero;
    }

    protected override void OnClosed(EventArgs e)
    {
        SettingsService.Changed -= OnSettingsChanged;
        UnregisterHotkey();
        if (_hwnd != IntPtr.Zero)
            HwndSource.FromHwnd(_hwnd)?.RemoveHook(WndProc);
        base.OnClosed(e);
    }

    private void ApplyAcrylicBackdrop()
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;

        HwndSource? source = HwndSource.FromHwnd(hwnd);
        if (source?.CompositionTarget != null)
            source.CompositionTarget.BackgroundColor = Colors.Transparent;

        var margins = new NativeMethods.MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        NativeMethods.DwmExtendFrameIntoClientArea(hwnd, ref margins);

        int backdrop = NativeMethods.DWMSBT_TRANSIENTWINDOW;
        NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));

        int corner = NativeMethods.DWMWCP_ROUND;
        NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ShowTab(TabNotes);
        _clipboardView ??= new ClipboardView();
        AppSettings settings = SettingsService.Current;
        _dock = new DockManager(this)
        {
            CollapseDelayMs = settings.CollapseDelayMs
        };
        _dock.EdgeChanged += OnDockEdgeChanged;
        SettingsService.Changed += OnSettingsChanged;
        _dock.Start(settings.DockEdge, startExpanded: true);
    }

    private void OnTabChecked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
            return;
        ShowTab(sender);
    }

    private void ShowTab(object sender)
    {
        if (ReferenceEquals(sender, TabNotes))
            ContentHost.Content = _notesView ??= new NotesView();
        else if (ReferenceEquals(sender, TabCommands))
            ContentHost.Content = _commandsView ??= new CommandsView();
        else if (ReferenceEquals(sender, TabWidgets))
            ContentHost.Content = _widgetsView ??= new WidgetsView();
        else if (ReferenceEquals(sender, TabClipboard))
            ContentHost.Content = _clipboardView ??= new ClipboardView();
    }

    private void OnOpenSettings(object sender, RoutedEventArgs e) => OpenSettings();

    private void OnDockEdgeChanged(object? sender, DockEdge edge) => SettingsService.SetDockEdge(edge);

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        AppSettings settings = SettingsService.Current;
        ThemeService.Apply(settings.Theme);
        RegisterHotkey();

        if (_dock is null)
            return;

        _dock.CollapseDelayMs = settings.CollapseDelayMs;
        if (_dock.Edge != settings.DockEdge)
            _dock.SetEdge(settings.DockEdge, _dock.State == DockState.Expanded);
    }
}
