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
    private readonly string _captureColorInfo;
    private bool _busy;
    private bool _closed;
    private bool _statusIsError;
    private bool? _selectionAvailable;
    private bool? _statusBusy;
    private Size? _statusSelectionSize;
    private string? _statusError;
    private string? _error;

    internal ScreenshotToolbarWindow(ScreenshotOverlayWindow session)
    {
        _session = session;
        _captureColorInfo = session.Capture.Hdr ? "HDR → sRGB" : "sRGB";
        if (session.Capture.ProtectedContent) _captureColorInfo += " · 受保护内容已被系统遮挡";
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
        UndoButton.IsEnabled = _session.CanUndo && !_busy;
        RedoButton.IsEnabled = _session.CanRedo && !_busy;
        AnnotationOptions.IsEnabled = !_busy;
        RefreshSelectionControls(selected);
        // 工具与颜色只在状态边界同步，拖动选区走轻量刷新。
        foreach (var button in _colors)
            button.IsChecked = Equals(button.Tag, _session.InkColor);
        RefreshStatus();
    }

    internal void RefreshSelection()
    {
        if (_closed) return;
        bool selected = _session.Selection is { IsEmpty: false };
        if (_selectionAvailable != selected)
            RefreshSelectionControls(selected);
        // 此入口只用于拖动；折叠提示在松手后的完整刷新中更新，避免不可见文字随每次输入重新格式化。
        if (StatusText.Visibility == Visibility.Visible)
            RefreshStatus();
    }

    private void RefreshSelectionControls(bool selected)
    {
        _selectionAvailable = selected;
        AdjustTool.IsEnabled = RectangleTool.IsEnabled = PenTool.IsEnabled = selected && !_busy;
        // 导出还依赖鼠标捕获状态，按下、抬起与取消手势时必须执行完整刷新。
        CopyButton.IsEnabled = SaveButton.IsEnabled = _session.CanExport && !_busy;
        AnnotationOptions.Visibility = selected && (_session.Tool is ScreenshotTool.Rectangle or ScreenshotTool.Pen)
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshStatus()
    {
        Size? selectionSize = _session.Selection is { IsEmpty: false } rect ? rect.Size : null;
        // 提示不包含选区位置；平移或画笔移动时跳过格式化，避免重复布局和辅助功能通知。
        if (_statusSelectionSize != selectionSize || _statusBusy != _busy || _statusError != _error)
        {
            _statusSelectionSize = selectionSize;
            _statusBusy = _busy;
            _statusError = _error;
            string text = _error ?? (_busy ? "正在导出截图…" : selectionSize is { } size
                ? $"{size.Width:0} × {size.Height:0} px · {_captureColorInfo} · 拖动边框调整选区，选择矩形或画笔标注；Tab 隐藏工具栏，Esc 取消"
                : $"{_captureColorInfo} · 点击吸附窗口，或拖动框选；Tab 隐藏工具栏，Esc 取消");
            // 完整刷新及展开信息时同步文本，信息按钮的 tooltip 也使用这里的最新内容。
            if (StatusText.Text != text) StatusText.Text = text;
        }
        // 常规提示按需展开，但导出进度和错误必须始终可见。
        var visibility = _error is not null || _busy || InfoToggle.IsChecked == true
            ? Visibility.Visible : Visibility.Collapsed;
        if (StatusText.Visibility != visibility) StatusText.Visibility = visibility;
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
