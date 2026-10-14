using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using SidePeek.App.Interop;
using SidePeek.App.Services;

namespace SidePeek.App.Views;

public partial class ScreenshotToolbarWindow : Window
{
    private const string LogTag = nameof(ScreenshotToolbarWindow);
    private const double MinimumToolbarWidth = 200;
    private const double WorkAreaHorizontalMargin = 16;
    private const double WorkAreaTopMargin = 16;
    private static readonly string[] InkPalette =
        ["#FF4B55", "#FFB340", "#FFE55B", "#54D88B", "#53B6FF", "#B692FF", "#FFFFFF", "#171B24"];
    private static readonly int[] InkWidths = [2, 4, 8, 12, 20];

    private readonly ScreenshotOverlayWindow _session;
    private readonly List<ToggleButton> _colors = [];
    private readonly Rect _workArea;
    private bool _busy;
    private bool _closed;
    private bool _statusIsError;
    private string? _error;

    internal ScreenshotToolbarWindow(ScreenshotOverlayWindow session)
    {
        _session = session;
        InitializeComponent();
        NativeMethods.GetCursorPos(out var cursor);
        _workArea = session.Capture.Monitors.FirstOrDefault(monitor => monitor.Bounds.Contains(new Point(cursor.X, cursor.Y)))
            ?.WorkArea ?? session.Capture.Monitors[0].WorkArea;
        var swatchStyle = (Style)FindResource("ColorSwatch");
        foreach (string hex in InkPalette)
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            var button = new ToggleButton
            {
                Background = brush, Tag = color, Style = swatchStyle, ToolTip = $"标注颜色 {hex}"
            };
            AutomationProperties.SetName(button, $"标注颜色 {hex}");
            button.Click += (_, _) =>
            {
                _session.InkColor = color;
                _error = null;
                Refresh();
            };
            _colors.Add(button);
            ColorPanel.Children.Add(button);
        }
        var widths = InkWidths.Select(width => new WidthOption(width)).ToArray();
        WidthPicker.ItemsSource = widths;
        WidthPicker.DisplayMemberPath = nameof(WidthOption.Label);
        WidthPicker.SelectedItem = widths.First(width => width.Value == _session.InkWidth);
        PreviewKeyDown += session.OnScreenshotKey;
        Loaded += (_, _) =>
        {
            PositionToolbar();
            AppLogger.Info($"{LogTag}: opened with theme {SettingsService.Current.Theme}.");
        };
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(new Action(() => { if (!_closed) PositionToolbar(); }));
        SizeChanged += (_, _) => { if (IsLoaded && !_closed) PositionToolbar(); };
        Closed += (_, _) => { _closed = true; if (_session.IsVisible) _session.Close(); };
        MaxWidth = Math.Max(MinimumToolbarWidth, _workArea.Width - WorkAreaHorizontalMargin * 2);
        Refresh();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ScreenshotNative.ExcludeFromCapture(this);
    }

    private void PositionToolbar()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        MaxWidth = Math.Max(MinimumToolbarWidth, _workArea.Width / dpi.DpiScaleX - WorkAreaHorizontalMargin * 2);
        int width = (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX);
        int x = (int)(_workArea.Left + (_workArea.Width - width) / 2);
        int y = (int)(_workArea.Top + WorkAreaTopMargin * dpi.DpiScaleY);
        NativeMethods.SetWindowPos(new WindowInteropHelper(this).Handle, NativeMethods.HWND_TOPMOST,
            x, y, 0, 0, NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }

    internal void Refresh()
    {
        if (_closed) return;
        bool selected = _session.Selection is { IsEmpty: false };
        WindowTool.IsChecked = _session.Tool == ScreenshotTool.Window;
        AreaTool.IsChecked = _session.Tool == ScreenshotTool.Area;
        AdjustTool.IsChecked = _session.Tool == ScreenshotTool.Adjust;
        RectangleTool.IsChecked = _session.Tool == ScreenshotTool.Rectangle;
        PenTool.IsChecked = _session.Tool == ScreenshotTool.Pen;
        ToolsRow.IsEnabled = !_busy;
        AdjustTool.IsEnabled = RectangleTool.IsEnabled = PenTool.IsEnabled = selected && !_busy;
        UndoButton.IsEnabled = _session.CanUndo && !_busy;
        RedoButton.IsEnabled = _session.CanRedo && !_busy;
        CopyButton.IsEnabled = SaveButton.IsEnabled = _session.CanExport && !_busy;
        AnnotationOptions.IsEnabled = !_busy;
        AnnotationOptions.Visibility = selected && (_session.Tool is ScreenshotTool.Rectangle or ScreenshotTool.Pen)
            ? Visibility.Visible : Visibility.Collapsed;
        // 拖动选区会频繁刷新；选中色环由主题样式处理，避免在此重复分配画刷。
        foreach (var button in _colors)
            button.IsChecked = Equals(button.Tag, _session.InkColor);
        string colorInfo = _session.Capture.Hdr ? "HDR → sRGB" : "sRGB";
        if (_session.Capture.ProtectedContent) colorInfo += " · 受保护内容已被系统遮挡";
        string hint = _session.Selection is { IsEmpty: false } rect
            ? $"{rect.Width:0} × {rect.Height:0} px · {colorInfo} · 拖动边框调整选区，选择矩形或画笔标注；Tab 隐藏工具栏，Esc 取消"
            : $"{colorInfo} · 点击吸附窗口，或拖动框选；Tab 隐藏工具栏，Esc 取消";
        StatusText.Text = _error ?? (_busy ? "正在导出截图…" : hint);
        // 常规提示按需展开，但导出进度和错误必须始终可见。
        StatusText.Visibility = _error is not null || _busy || InfoToggle.IsChecked == true
            ? Visibility.Visible : Visibility.Collapsed;
        bool isError = _error is not null;
        if (_statusIsError != isError)
        {
            _statusIsError = isError;
            StatusText.SetResourceReference(TextBlock.ForegroundProperty,
                isError ? "SystemFillColorCriticalBrush" : "TextFillColorSecondaryBrush");
        }
    }

    internal void SetBusy(bool busy) { _busy = busy; Refresh(); }
    internal void ShowError(string message) { _error = message; Refresh(); }
    private void OnTool(object sender, RoutedEventArgs e)
    {
        _error = null;
        if (sender is ToggleButton { Tag: string name } && Enum.TryParse(name, out ScreenshotTool tool))
        {
            AppLogger.Info($"{LogTag}: selected tool {tool}.");
            _session.SetTool(tool);
        }
    }
    private void OnInfo(object sender, RoutedEventArgs e) => Refresh();
    private void OnUndo(object sender, RoutedEventArgs e) => _session.Undo();
    private void OnRedo(object sender, RoutedEventArgs e) => _session.Redo();
    private void OnSave(object sender, RoutedEventArgs e) { _error = null; _session.SaveImage(); }
    private void OnCopy(object sender, RoutedEventArgs e) { _error = null; _session.CopyImage(); }
    private void OnCancel(object sender, RoutedEventArgs e) => _session.Close();
    private void OnWidthChanged(object sender, SelectionChangedEventArgs e)
    {
        if (WidthPicker.SelectedItem is WidthOption width) _session.InkWidth = width.Value;
    }
    private sealed record WidthOption(int Value) { public string Label => $"{Value} px"; }
}
