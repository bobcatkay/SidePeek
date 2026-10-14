using System;
using System.Collections.Generic;
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

public partial class DockWindow : Window, IDockViewport
{
    private const int HotkeyId = 0xB001;
    private const int AcrylicAccentFlags = 2;
    private const int FullClientGlassMargin = -1;
    private static readonly Color LightAcrylicTint = Color.FromArgb(0xB8, 0xF8, 0xFA, 0xFC);
    private static readonly Color DarkAcrylicTint = Color.FromArgb(0xCC, 0x18, 0x1B, 0x20);

    private DockManager? _dock;
    private IntPtr _hwnd;
    private readonly TranslateTransform _dockViewportTransform = new();
    private readonly HashSet<int> _failedDwmAttributes = new();
    private Size _dockViewportSize;
    private Size? _collapsedRegionSize;
    private bool _isCollapsedPresentation;

    private NotesView? _notesView;
    private CommandsView? _commandsView;
    private WidgetsView? _widgetsView;
    private ClipboardView? _clipboardView;
    private SettingsView? _settingsView;
    private bool _hotkeyRegistered;
    private string? _registeredHotkeySignature;
    private bool _startupShown;
    private bool _frameExtensionFailureLogged;
    private bool _accentFailureLogged;
    private bool _regionFailureLogged;

    public DockWindow()
    {
        InitializeComponent();
    }

    public void SuspendDock() => _dock?.Suspend();

    public void ResumeDock() => _dock?.Resume();

    public void StartScreenshot() => ((App)Application.Current).Screenshots?.Start();

    internal void SuspendDockForScreenshot() => _dock?.SuspendForScreenshot();

    internal void ResumeDockAfterScreenshot() => _dock?.ResumeAfterScreenshot();

    /// <summary>展开/收起切换（供托盘双击 / 菜单调用）。</summary>
    public void ToggleVisibility() => _dock?.Toggle();

    public void OpenSettings()
    {
        ClearSelectedTab();
        ContentHost.Content = _settingsView ??= new SettingsView();
        _dock?.Reveal();
        if (IsVisible)
            Activate();
    }

    public void SetDockViewport(Rect expandedRect, Rect viewportRect)
    {
        _dockViewportSize = viewportRect.Size;
        BackdropTintLayer.Width = Math.Max(1, expandedRect.Width);
        BackdropTintLayer.Height = Math.Max(1, expandedRect.Height);
        BackdropTintLayer.HorizontalAlignment = HorizontalAlignment.Left;
        BackdropTintLayer.VerticalAlignment = VerticalAlignment.Top;
        BackdropTintLayer.RenderTransform = _dockViewportTransform;

        _dockViewportTransform.X = expandedRect.Left - viewportRect.Left;
        _dockViewportTransform.Y = expandedRect.Top - viewportRect.Top;
    }

    public void SetCollapsedPresentation(bool isCollapsed)
    {
        bool changed = _isCollapsedPresentation != isCollapsed;
        _isCollapsedPresentation = isCollapsed;

        if (!isCollapsed)
            ClearCollapsedRegion();

        if (changed)
        {
            ApplyWindowBackdrop();
            AppLogger.Info($"[DockWindow] Dock presentation changed: collapsed={isCollapsed}.");
        }

        // 无动画定位也会走这里，确保收起时切换显示器、DPI 或停靠方向能更新区域。
        if (isCollapsed)
            ApplyCollapsedRegion();
    }

    private void ApplyCollapsedRegion()
    {
        if (_hwnd == IntPtr.Zero)
            return;

        Matrix transform = HwndSource.FromHwnd(_hwnd)?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
        Vector pixels = transform.Transform(new Vector(_dockViewportSize.Width, _dockViewportSize.Height));
        int width = Math.Max(1, (int)Math.Round(pixels.X));
        int height = Math.Max(1, (int)Math.Round(pixels.Y));
        var pixelSize = new Size(width, height);
        if (_collapsedRegionSize == pixelSize)
            return;

        // 原生区域以物理像素限制整个 HWND，WPF 的 Clip 只能限制内容，不能裁掉系统背景。
        int diameter = Math.Min(width, height);
        IntPtr region = NativeMethods.CreateRoundRectRgn(0, 0, width, height, diameter, diameter);
        if (region == IntPtr.Zero)
        {
            LogRegionFailure("create");
            return;
        }

        if (NativeMethods.SetWindowRgn(_hwnd, region, true) == 0)
        {
            NativeMethods.DeleteObject(region);
            LogRegionFailure("apply");
            return;
        }

        // SetWindowRgn 成功后区域归系统所有，只能在失败时自行释放。
        _collapsedRegionSize = pixelSize;
    }

    private void ClearCollapsedRegion()
    {
        if (_hwnd == IntPtr.Zero || _collapsedRegionSize is null)
            return;

        if (NativeMethods.SetWindowRgn(_hwnd, IntPtr.Zero, true) == 0)
        {
            LogRegionFailure("clear");
            return;
        }

        _collapsedRegionSize = null;
    }

    private void LogRegionFailure(string operation)
    {
        if (_regionFailureLogged)
            return;

        _regionFailureLogged = true;
        AppLogger.Error($"[DockWindow] Unable to {operation} the collapsed window region.");
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        ApplyWindowBackdrop();
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
            ExpandDelayMs = settings.ExpandDelayMs,
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

        uint modifiers = NativeMethods.MOD_NOREPEAT;
        if (hotkey.Control)
            modifiers |= NativeMethods.MOD_CONTROL;
        if (hotkey.Alt)
            modifiers |= NativeMethods.MOD_ALT;
        if (hotkey.Shift)
            modifiers |= NativeMethods.MOD_SHIFT;

        if (modifiers == NativeMethods.MOD_NOREPEAT || !Enum.TryParse(hotkey.Key, ignoreCase: true, out Key key))
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
        else if (msg == NativeMethods.WM_DPICHANGED ||
                 msg == NativeMethods.WM_DISPLAYCHANGE ||
                 msg == NativeMethods.WM_SETTINGCHANGE)
        {
            // Let WPF process the DPI message, then restore the configured screen edge.
            _dock?.QueuePlacementRefresh();
        }
        return IntPtr.Zero;
    }

    protected override void OnClosed(EventArgs e)
    {
        _dock?.Stop();
        SettingsService.Changed -= OnSettingsChanged;
        UnregisterHotkey();
        if (_hwnd != IntPtr.Zero)
            HwndSource.FromHwnd(_hwnd)?.RemoveHook(WndProc);
        base.OnClosed(e);
    }

    private void ApplyWindowBackdrop()
    {
        IntPtr hwnd = _hwnd != IntPtr.Zero
            ? _hwnd
            : new WindowInteropHelper(this).Handle;

        if (hwnd == IntPtr.Zero)
            return;

        UpdateBackdropTint();

        // 收起条不能继续使用全窗口玻璃：系统圆角、背景和阴影会超出窄条形成第二层胶囊。
        // 关闭玻璃时使用不透明主题色，避免非 layered 窗口的透明合成表面变黑。
        Color surfaceColor = Colors.Transparent;
        if (_isCollapsedPresentation)
        {
            surfaceColor = GetAcrylicTintColor();
            surfaceColor.A = byte.MaxValue;
        }
        Background = new SolidColorBrush(surfaceColor);
        BackdropTintLayer.Visibility = _isCollapsedPresentation ? Visibility.Hidden : Visibility.Visible;
        HwndSource? source = HwndSource.FromHwnd(hwnd);
        if (source?.CompositionTarget != null)
            source.CompositionTarget.BackgroundColor = surfaceColor;

        SetDwmAttribute(hwnd, NativeMethods.DWMWA_NCRENDERING_POLICY,
            _isCollapsedPresentation ? NativeMethods.DWMNCRP_DISABLED : NativeMethods.DWMNCRP_USEWINDOWSTYLE);

        int margin = _isCollapsedPresentation ? 0 : FullClientGlassMargin;
        var margins = new NativeMethods.MARGINS { Left = margin, Right = margin, Top = margin, Bottom = margin };
        int frameResult = NativeMethods.DwmExtendFrameIntoClientArea(hwnd, ref margins);
        if (frameResult < 0 && !_frameExtensionFailureLogged)
        {
            _frameExtensionFailureLogged = true;
            AppLogger.Error($"[DockWindow] Unable to update the glass frame. HRESULT: 0x{frameResult:X8}.");
        }

        SetDwmAttribute(hwnd, NativeMethods.DWMWA_SYSTEMBACKDROP_TYPE,
            _isCollapsedPresentation ? NativeMethods.DWMSBT_NONE : NativeMethods.DWMSBT_TRANSIENTWINDOW);

        // legacy Acrylic 是系统背景的兼容兜底；收起时两条路径必须一起禁用。
        SetAcrylicBlurBehind(hwnd, enabled: !_isCollapsedPresentation);
        SetDwmAttribute(hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE,
            _isCollapsedPresentation ? NativeMethods.DWMWCP_DONOTROUND : NativeMethods.DWMWCP_ROUND);

        // 描边与毛玻璃背景独立，主题变化重设背景后也要保持无描边。
        SetDwmAttribute(hwnd, NativeMethods.DWMWA_BORDER_COLOR, NativeMethods.DWMWA_COLOR_NONE);
    }

    private void SetDwmAttribute(IntPtr hwnd, int attribute, int value)
    {
        int result = NativeMethods.DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int));
        if (result < 0 && _failedDwmAttributes.Add(attribute))
            AppLogger.Error($"[DockWindow] Unable to set DWM attribute {attribute}; collapsed={_isCollapsedPresentation}, HRESULT: 0x{result:X8}.");
    }

    private void SetAcrylicBlurBehind(IntPtr hwnd, bool enabled)
    {
        var accent = new NativeMethods.ACCENT_POLICY
        {
            AccentState = enabled ? NativeMethods.ACCENT_ENABLE_ACRYLICBLURBEHIND : NativeMethods.ACCENT_DISABLED,
            AccentFlags = enabled ? AcrylicAccentFlags : 0,
            GradientColor = enabled ? ToAbgr(GetAcrylicTintColor()) : 0
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

            if (!NativeMethods.SetWindowCompositionAttribute(hwnd, ref data) && !_accentFailureLogged)
            {
                _accentFailureLogged = true;
                AppLogger.Error($"[DockWindow] Unable to update legacy acrylic: enabled={enabled}.");
            }
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

    private static Color GetAcrylicTintColor() => ThemeService.IsLightTheme(SettingsService.Current.Theme)
        ? LightAcrylicTint
        : DarkAcrylicTint;

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
        if (!_startupShown)
        {
            ShowTab(TabNotes);
            _clipboardView ??= new ClipboardView();
            _startupShown = true;
            ApplyWindowBackdrop();
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

    private void OnScreenshot(object sender, RoutedEventArgs e) => StartScreenshot();

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
        ApplyWindowBackdrop();
        RegisterHotkey();

        if (_dock is null)
            return;

        _dock.ExpandDelayMs = settings.ExpandDelayMs;
        _dock.CollapseDelayMs = settings.CollapseDelayMs;
        if (_dock.Edge != settings.DockEdge ||
            !string.Equals(_dock.DisplayDeviceName, settings.DockDisplayDeviceName, StringComparison.OrdinalIgnoreCase))
        {
            _dock.SetPlacement(settings.DockEdge, settings.DockDisplayDeviceName, _dock.State == DockState.Expanded);
        }
    }
}
