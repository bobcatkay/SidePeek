using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using SidePeek.App.Interop;

namespace SidePeek.App.Views;

public partial class ScreenshotToolbarWindow : Window
{
    private readonly ScreenshotOverlayWindow _session;
    private readonly List<Button> _colors = [];
    private readonly Rect _workArea;
    private bool _busy;
    private bool _closed;
    private string? _error;

    internal ScreenshotToolbarWindow(ScreenshotOverlayWindow session)
    {
        _session = session;
        InitializeComponent();
        NativeMethods.GetCursorPos(out var cursor);
        _workArea = session.Capture.Monitors.FirstOrDefault(monitor => monitor.Bounds.Contains(new Point(cursor.X, cursor.Y)))
            ?.WorkArea ?? session.Capture.Monitors[0].WorkArea;
        foreach (string hex in new[] { "#FF4B55", "#FFB340", "#FFE55B", "#54D88B", "#53B6FF", "#B692FF", "#FFFFFF", "#171B24" })
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            var button = new Button
            {
                Background = new SolidColorBrush(color), Tag = color,
                Width = 25, Height = 25, Padding = new Thickness(0), ToolTip = $"画笔颜色 {hex}"
            };
            button.Click += (_, _) =>
            {
                _session.InkColor = color;
                _error = null;
                Refresh();
            };
            _colors.Add(button);
            ColorPanel.Children.Add(button);
        }
        WidthPicker.ItemsSource = new[] { 2, 4, 8, 12, 20 }.Select(width => new WidthOption(width)).ToArray();
        WidthPicker.DisplayMemberPath = nameof(WidthOption.Label);
        WidthPicker.SelectedIndex = 1;
        PreviewKeyDown += session.OnScreenshotKey;
        Loaded += (_, _) => PositionToolbar();
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(new Action(() => { if (!_closed) PositionToolbar(); }));
        SizeChanged += (_, _) => { if (IsLoaded && !_closed) PositionToolbar(); };
        Closed += (_, _) => { _closed = true; if (_session.IsVisible) _session.Close(); };
        MaxWidth = Math.Max(200, _workArea.Width - 32);
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
        MaxWidth = Math.Max(200, _workArea.Width / dpi.DpiScaleX - 32);
        int width = (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX);
        int x = (int)(_workArea.Left + (_workArea.Width - width) / 2);
        int y = (int)(_workArea.Top + 16 * dpi.DpiScaleY);
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
        ColorPanel.IsEnabled = WidthPicker.IsEnabled = !_busy;
        foreach (var button in _colors)
            button.BorderBrush = Equals(button.Tag, _session.InkColor) ? Brushes.White : new SolidColorBrush(Color.FromRgb(89, 103, 123));
        string colorInfo = _session.Capture.Hdr ? "HDR → sRGB" : "sRGB";
        if (_session.Capture.ProtectedContent) colorInfo += " · 受保护内容已被系统遮挡";
        string hint = _session.Selection is { IsEmpty: false } rect
            ? $"{rect.Width:0} × {rect.Height:0} px · {colorInfo} · 拖动边框调整选区，选择矩形或画笔标注；Tab 隐藏工具栏"
            : $"{colorInfo} · 点击吸附窗口，或拖动框选；Tab 隐藏工具栏，Esc 取消";
        StatusText.Text = _error ?? (_busy ? "正在导出截图…" : hint);
        StatusText.Foreground = _error is null ? new SolidColorBrush(Color.FromRgb(184, 200, 218)) : Brushes.Salmon;
    }

    internal void SetBusy(bool busy) { _busy = busy; Refresh(); }
    internal void ShowError(string message) { _error = message; Refresh(); }
    private void OnTool(object sender, RoutedEventArgs e)
    {
        _error = null;
        if (sender is ToggleButton { Tag: string name } && Enum.TryParse(name, out ScreenshotTool tool))
            _session.SetTool(tool);
    }
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
