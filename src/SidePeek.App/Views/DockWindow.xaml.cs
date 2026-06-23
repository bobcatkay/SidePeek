using System;
using System.Linq;
using System.Runtime.InteropServices;
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
    private const int AcrylicAccentFlags = 2;

    private DockManager? _dock;
    private IntPtr _hwnd;

    private NotesView? _notesView;
    private CommandsView? _commandsView;
    private WidgetsView? _widgetsView;
    private ClipboardView? _clipboardView;
    private SettingsView? _settingsView;
    private bool _hotkeyRegistered;
    private string? _registeredHotkeySignature;
    private bool _startupShown;

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
        ClearSelectedTab();
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
        InitializeDocking();
    }

    private void InitializeDocking()
    {
        if (_dock is not null)
            return;

        AppSettings settings = SettingsService.Current;
        _dock = new DockManager(this)
        {
            CollapseDelayMs = settings.CollapseDelayMs,
            IsPinned = PinToggle.IsChecked == true
        };
        _dock.EdgeChanged += OnDockEdgeChanged;
        SettingsService.Changed += OnSettingsChanged;
        _dock.Start(settings.DockEdge, settings.DockDisplayDeviceName, startExpanded: true);
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
        IntPtr hwnd = _hwnd != IntPtr.Zero
            ? _hwnd
            : new WindowInteropHelper(this).Handle;

        if (hwnd == IntPtr.Zero)
            return;

        UpdateBackdropTint();

        // 透明合成表面，让系统亚克力背景透上来形成毛玻璃。
        // 注意：透明表面会让 WPF 文字退化为灰阶抗锯齿（略糊），这是毛玻璃的固有代价。
        HwndSource? source = HwndSource.FromHwnd(hwnd);
        if (source?.CompositionTarget != null)
            source.CompositionTarget.BackgroundColor = Colors.Transparent;

        var margins = new NativeMethods.MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        NativeMethods.DwmExtendFrameIntoClientArea(hwnd, ref margins);

        int backdrop = NativeMethods.DWMSBT_TRANSIENTWINDOW;
        NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));

        // DWMWA_SYSTEMBACKDROP_TYPE 在部分 Windows 版本/窗口组合下会静默退化；
        // legacy Acrylic 组合属性可作为更可靠的兜底。
        EnableAcrylicBlurBehind(hwnd);

        int corner = NativeMethods.DWMWCP_ROUND;
        NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
    }

    private void EnableAcrylicBlurBehind(IntPtr hwnd)
    {
        var accent = new NativeMethods.ACCENT_POLICY
        {
            AccentState = NativeMethods.ACCENT_ENABLE_ACRYLICBLURBEHIND,
            AccentFlags = AcrylicAccentFlags,
            GradientColor = GetAcrylicGradientColor()
        };

        int accentSize = Marshal.SizeOf<NativeMethods.ACCENT_POLICY>();
        IntPtr accentPtr = Marshal.AllocHGlobal(accentSize);

        try
        {
            Marshal.StructureToPtr(accent, accentPtr, false);
            var data = new NativeMethods.WINDOWCOMPOSITIONATTRIBDATA
            {
                Attribute = NativeMethods.WCA_ACCENT_POLICY,
                Data = accentPtr,
                SizeOfData = accentSize
            };

            NativeMethods.SetWindowCompositionAttribute(hwnd, ref data);
        }
        catch (EntryPointNotFoundException)
        {
            // Older systems can still use the DWM path above.
        }
        finally
        {
            Marshal.FreeHGlobal(accentPtr);
        }
    }

    private void UpdateBackdropTint()
    {
        bool isLightTheme = ThemeService.IsLightTheme(SettingsService.Current.Theme);
        BackdropTintLayer.Background = new SolidColorBrush(isLightTheme
            ? Color.FromArgb(0x3D, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0x52, 0x18, 0x1B, 0x20));
    }

    private int GetAcrylicGradientColor()
    {
        bool isLightTheme = ThemeService.IsLightTheme(SettingsService.Current.Theme);
        Color tint = isLightTheme
            ? Color.FromArgb(0xB8, 0xF8, 0xFA, 0xFC)
            : Color.FromArgb(0xCC, 0x18, 0x1B, 0x20);

        return ToAbgr(tint);
    }

    private static int ToAbgr(Color color)
    {
        uint value = ((uint)color.A << 24)
            | ((uint)color.B << 16)
            | ((uint)color.G << 8)
            | color.R;

        return unchecked((int)value);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ShowTab(TabNotes);
        _clipboardView ??= new ClipboardView();

        if (!_startupShown)
        {
            _startupShown = true;
            ApplyAcrylicBackdrop();
            Opacity = 1;
        }
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

    private void ClearSelectedTab()
    {
        TabNotes.IsChecked = false;
        TabCommands.IsChecked = false;
        TabWidgets.IsChecked = false;
        TabClipboard.IsChecked = false;
    }

    private void OnOpenSettings(object sender, RoutedEventArgs e) => OpenSettings();

    private void OnPinToggled(object sender, RoutedEventArgs e)
    {
        if (_dock is null)
            return;

        _dock.IsPinned = PinToggle.IsChecked == true;
        if (_dock.IsPinned)
            _dock.Reveal();
    }

    private void OnDockEdgeChanged(object? sender, DockEdge edge) => SettingsService.SetDockEdge(edge);

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        AppSettings settings = SettingsService.Current;
        ThemeService.Apply(settings.Theme);
        ApplyAcrylicBackdrop();
        RegisterHotkey();

        if (_dock is null)
            return;

        _dock.CollapseDelayMs = settings.CollapseDelayMs;
        if (_dock.Edge != settings.DockEdge ||
            !string.Equals(_dock.DisplayDeviceName, settings.DockDisplayDeviceName, StringComparison.OrdinalIgnoreCase))
        {
            _dock.SetPlacement(settings.DockEdge, settings.DockDisplayDeviceName, _dock.State == DockState.Expanded);
        }
    }
}
