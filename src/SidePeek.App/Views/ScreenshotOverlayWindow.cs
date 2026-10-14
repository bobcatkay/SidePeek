using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SidePeek.App.Interop;
using SidePeek.App.Services;
using Microsoft.Win32;

namespace SidePeek.App.Views;

internal enum ScreenshotTool { Window, Area, Adjust, Rectangle, Pen }
internal sealed record ScreenshotAnnotation(bool Rectangle, Color Color, double Width, List<Point> Points);

/// <summary>覆盖虚拟桌面的冻结画面，所有选区及标注均保存为物理像素坐标。</summary>
internal sealed class ScreenshotOverlayWindow : Window
{
    private readonly DesktopCapture _capture;
    private readonly ScreenshotSurface _surface;
    private readonly ScreenshotToolbarWindow _toolbar;
    private readonly List<ScreenshotAnnotation> _annotations = [];
    private readonly Stack<ScreenshotAnnotation> _redo = [];
    private bool _exporting;
    private bool _closed;

    internal ScreenshotTool Tool { get; private set; } = ScreenshotTool.Window;
    internal Rect? Selection { get; set; }
    internal Rect? Hover { get; set; }
    internal ScreenshotAnnotation? Draft { get; set; }
    internal Color InkColor { get; set; } = Color.FromRgb(255, 75, 85);
    internal double InkWidth { get; set; } = 4;
    internal DesktopCapture Capture => _capture;
    internal bool CanUndo => _annotations.Count > 0;
    internal bool CanRedo => _redo.Count > 0;
    internal bool CanExport => Selection is { IsEmpty: false } && !_surface.IsMouseCaptured;
    internal int AnnotationVersion { get; private set; }

    internal ScreenshotOverlayWindow(DesktopCapture capture)
    {
        _capture = capture;
        Title = "SidePeek 截图";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Background = Brushes.Black;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -32000;
        Top = -32000;
        Width = Height = 1;
        _surface = new ScreenshotSurface(this);
        Content = _surface;
        _toolbar = new ScreenshotToolbarWindow(this);
        PreviewKeyDown += OnScreenshotKey;
        Loaded += (_, _) =>
        {
            PositionOverlay();
            _toolbar.Owner = this;
            _toolbar.Show();
            Activate();
            _surface.Focus();
        };
        Closed += (_, _) =>
        {
            _closed = true;
            _toolbar.Close();
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WindowMessage);
        ScreenshotNative.ExcludeFromCapture(this);
        PositionOverlay();
    }

    private IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == NativeMethods.WM_DISPLAYCHANGE)
        {
            // A frozen frame no longer describes the desktop after a topology change.
            Dispatcher.BeginInvoke(new Action(Close));
        }
        else if (message == NativeMethods.WM_DPICHANGED)
        {
            // Preserve the virtual desktop bounds instead of accepting a one-monitor suggested rect.
            Dispatcher.BeginInvoke(new Action(() => { if (!_closed) PositionOverlay(); }));
        }
        return IntPtr.Zero;
    }

    private void PositionOverlay()
    {
        var bounds = _capture.Bounds;
        if (!NativeMethods.SetWindowPos(new WindowInteropHelper(this).Handle, NativeMethods.HWND_TOPMOST,
                (int)bounds.X, (int)bounds.Y, (int)bounds.Width, (int)bounds.Height, NativeMethods.SWP_NOACTIVATE))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        _surface.RequestRender();
    }

    internal void SetTool(ScreenshotTool tool)
    {
        if (_exporting)
            return;
        _surface.CancelGesture();
        if (tool is ScreenshotTool.Window or ScreenshotTool.Area)
        {
            Selection = null;
            _annotations.Clear();
            _redo.Clear();
            AnnotationVersion++;
        }
        else if (Selection is null)
            return;
        Tool = tool;
        _surface.Cursor = Cursors.Cross;
        Hover = null;
        Refresh();
        _surface.Focus();
    }

    internal void Selected(Rect rect)
    {
        Selection = PixelRect(rect);
        Hover = null;
        Tool = ScreenshotTool.Adjust;
        Refresh();
    }

    internal Rect PixelRect(Rect rect)
    {
        rect.Intersect(new Rect(0, 0, _capture.Image.PixelWidth, _capture.Image.PixelHeight));
        if (rect.IsEmpty || rect.Width < 1 || rect.Height < 1)
            return Rect.Empty;
        double left = Math.Floor(rect.Left), top = Math.Floor(rect.Top);
        return new Rect(left, top, Math.Ceiling(rect.Right) - left, Math.Ceiling(rect.Bottom) - top);
    }

    internal void CommitDraft()
    {
        if (Draft is { } draft && draft.Points.Count > 0)
        {
            _annotations.Add(draft);
            _redo.Clear();
            AnnotationVersion++;
        }
        Draft = null;
        Refresh();
    }

    internal void Undo()
    {
        _surface.CancelGesture();
        if (_annotations.Count > 0)
        {
            _redo.Push(_annotations[^1]);
            _annotations.RemoveAt(_annotations.Count - 1);
            AnnotationVersion++;
        }
        Refresh();
    }

    internal void Redo()
    {
        _surface.CancelGesture();
        if (_redo.TryPop(out var annotation))
        {
            _annotations.Add(annotation);
            AnnotationVersion++;
        }
        Refresh();
    }

    internal void Refresh()
    {
        _surface.RequestRender();
        _toolbar.Refresh();
    }

    internal void RefreshSelectionInfo() => _toolbar.RefreshSelection();

    internal void ActivateTools()
    {
        if (_closed) return;
        if (!_toolbar.IsVisible) _toolbar.Show();
        _toolbar.Activate();
    }

    internal void OnScreenshotKey(object sender, KeyEventArgs e)
    {
        if (_exporting)
        {
            if (e.Key == Key.Escape)
                Close();
            e.Handled = true;
            return;
        }
        var modifiers = Keyboard.Modifiers;
        if (e.Key == Key.Escape)
            Close();
        else if ((modifiers == ModifierKeys.Control && e.Key == Key.C)
                 || (modifiers == ModifierKeys.None && e.Key == Key.Enter))
            CopyImage();
        else if (modifiers == ModifierKeys.Control && e.Key == Key.S)
            SaveImage();
        else if (modifiers == ModifierKeys.Control && e.Key == Key.Z)
            Undo();
        else if ((modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.Z)
                 || (modifiers == ModifierKeys.Control && e.Key == Key.Y))
            Redo();
        else if (modifiers == ModifierKeys.None && e.Key == Key.Tab)
        {
            if (_toolbar.IsVisible) _toolbar.Hide(); else _toolbar.Show();
        }
        else if (modifiers == ModifierKeys.None && e.Key == Key.W)
            SetTool(ScreenshotTool.Window);
        else if (modifiers == ModifierKeys.None && e.Key == Key.A)
            SetTool(ScreenshotTool.Area);
        else if (modifiers == ModifierKeys.None && e.Key == Key.V)
            SetTool(ScreenshotTool.Adjust);
        else if (modifiers == ModifierKeys.None && e.Key == Key.R)
            SetTool(ScreenshotTool.Rectangle);
        else if (modifiers == ModifierKeys.None && e.Key == Key.P)
            SetTool(ScreenshotTool.Pen);
        else
            return;
        e.Handled = true;
    }

    private BitmapSource RenderSelection()
    {
        _surface.CancelGesture();
        if (Selection is not { IsEmpty: false } rect || rect.Width < 1 || rect.Height < 1)
            throw new InvalidOperationException("请先选择截图区域。");
        var crop = new CroppedBitmap(_capture.Image,
            new Int32Rect((int)rect.X, (int)rect.Y, (int)rect.Width, (int)rect.Height));
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawImage(crop, new Rect(0, 0, rect.Width, rect.Height));
            drawing.PushClip(new RectangleGeometry(new Rect(0, 0, rect.Width, rect.Height)));
            drawing.PushTransform(new TranslateTransform(-rect.X, -rect.Y));
            DrawAnnotations(drawing, includeDraft: false);
            drawing.Pop();
            drawing.Pop();
        }
        var bitmap = new RenderTargetBitmap((int)rect.Width, (int)rect.Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    internal async void CopyImage()
    {
        if (_exporting || !CanExport)
            return;
        _exporting = true;
        _surface.IsEnabled = false;
        _toolbar.SetBusy(true);
        try
        {
            var image = RenderSelection();
            using var png = new MemoryStream();
            EncodePng(image, png);
            using var clipboardPng = new MemoryStream(png.ToArray());
            var data = new DataObject();
            data.SetImage(image);
            data.SetData("PNG", clipboardPng);
            for (int attempt = 0; ; attempt++)
            {
                if (_closed)
                    return;
                try
                {
                    Clipboard.SetDataObject(data, copy: true);
                    break;
                }
                catch (ExternalException) when (attempt < 4)
                {
                    await Task.Delay(100);
                }
            }
            Close();
        }
        catch (Exception error)
        {
            AppLogger.Error("Screenshot clipboard export failed.", error);
            if (!_closed) _toolbar.ShowError("复制失败，剪贴板可能被占用；可以重试或保存文件。");
        }
        finally
        {
            _exporting = false;
            _surface.IsEnabled = true;
            if (!_closed) _toolbar.SetBusy(false);
        }
    }

    internal void SaveImage()
    {
        if (_exporting || !CanExport)
            return;
        _exporting = true;
        _surface.IsEnabled = false;
        _toolbar.SetBusy(true);
        string? temporary = null;
        try
        {
            var dialog = new SaveFileDialog
            {
                Title = "保存截图", Filter = "PNG 图像 (*.png)|*.png", DefaultExt = ".png",
                AddExtension = true, OverwritePrompt = true,
                FileName = $"SidePeek-{DateTime.Now:yyyyMMdd-HHmmss}.png",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
            };
            if (dialog.ShowDialog(_toolbar) != true || _closed)
                return;
            var image = RenderSelection();
            temporary = Path.Combine(Path.GetDirectoryName(dialog.FileName)!, $".sidepeek-{Guid.NewGuid():N}.tmp");
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
            {
                EncodePng(image, stream);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, dialog.FileName, overwrite: true);
            temporary = null;
            Close();
        }
        catch (Exception error)
        {
            AppLogger.Error("Screenshot file export failed.", error);
            if (!_closed) _toolbar.ShowError("保存失败，请检查文件夹权限或选择其他位置。");
        }
        finally
        {
            if (temporary is not null)
            {
                try { File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            _exporting = false;
            _surface.IsEnabled = true;
            if (!_closed) _toolbar.SetBusy(false);
        }
    }

    private static void EncodePng(BitmapSource image, Stream destination)
    {
        var metadata = new BitmapMetadata("png");
        metadata.SetQuery("/sRGB/RenderingIntent", (byte)0);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image, null, metadata, null));
        encoder.Save(destination);
    }

    internal void DrawAnnotations(DrawingContext drawing, bool includeDraft)
    {
        foreach (var annotation in _annotations)
            DrawAnnotation(drawing, annotation);
        if (includeDraft && Draft is { } draft)
            DrawAnnotation(drawing, draft);
    }

    internal static void DrawAnnotation(DrawingContext drawing, ScreenshotAnnotation annotation)
    {
        if (annotation.Points.Count == 0)
            return;
        var brush = new SolidColorBrush(annotation.Color);
        var pen = new System.Windows.Media.Pen(brush, annotation.Width)
        {
            StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round
        };
        pen.Freeze();
        if (annotation.Rectangle && annotation.Points.Count > 1)
            drawing.DrawRectangle(null, pen, new Rect(annotation.Points[0], annotation.Points[^1]));
        else if (annotation.Points.Count == 1)
            drawing.DrawEllipse(brush, null, annotation.Points[0], annotation.Width / 2, annotation.Width / 2);
        else
        {
            var geometry = new StreamGeometry();
            using (var path = geometry.Open())
            {
                path.BeginFigure(annotation.Points[0], isFilled: false, isClosed: false);
                path.PolyLineTo(annotation.Points.Skip(1).ToArray(), isStroked: true, isSmoothJoin: true);
            }
            geometry.Freeze();
            drawing.DrawGeometry(null, pen, geometry);
        }
    }
}

internal sealed class ScreenshotSurface : FrameworkElement
{
    private const string LogTag = nameof(ScreenshotSurface);
    private const byte DimmingAlpha = 120;
    private const double SelectionBorderWidth = 2;
    private const double SelectionHandleRadius = 3;
    private const double SelectionDragThreshold = 3;
    private const double SelectionEdgeTolerance = 8;
    private const int UnrenderedAnnotationVersion = -1;
    private const int RenderingTierShift = 16;
    private static readonly Color SelectionBorderColor = Color.FromRgb(83, 182, 255);
    private readonly ScreenshotOverlayWindow _session;
    private readonly DrawingVisual _background = new();
    private readonly DrawingVisual _selection = new();
    private readonly DrawingVisual _annotations = new();
    private readonly DrawingVisual _interaction = new();
    private readonly VisualCollection _visuals;
    private readonly Rect _desktop;
    private readonly ImageBrush _desktopBrush;
    private readonly SolidColorBrush _dimBrush;
    private System.Windows.Media.Pen? _border;
    private DpiScale _dpi;
    private bool _viewportReady;
    private bool _renderPending;
    private bool _selectionDirty = true;
    private bool _clipReady;
    private Rect? _renderedHighlight;
    private Rect? _renderedSelection;
    private int _annotationVersion = UnrenderedAnnotationVersion;
    private Point _start;
    private bool _selecting;
    private Rect? _originalSelection;
    private int _edges;
    private const int LeftEdge = 1, TopEdge = 2, RightEdge = 4, BottomEdge = 8;
    private const int TopLeftCorner = LeftEdge | TopEdge, TopRightCorner = RightEdge | TopEdge;
    private const int BottomLeftCorner = LeftEdge | BottomEdge, BottomRightCorner = RightEdge | BottomEdge;

    internal ScreenshotSurface(ScreenshotOverlayWindow session)
    {
        _session = session;
        _visuals = new VisualCollection(this) { _background, _selection, _annotations, _interaction };
        _desktop = new Rect(0, 0, session.Capture.Image.PixelWidth, session.Capture.Image.PixelHeight);
        // 固定原图映射，只绘制选区矩形；整图加 Clip 会让动态层仍携带整桌面的绘制内容。
        _desktopBrush = new ImageBrush(session.Capture.Image)
        {
            ViewportUnits = BrushMappingMode.Absolute,
            Viewport = _desktop,
            Stretch = Stretch.Fill,
            TileMode = TileMode.None
        };
        _desktopBrush.Freeze();
        _dimBrush = new SolidColorBrush(Color.FromArgb(DimmingAlpha, 0, 0, 0));
        _dimBrush.Freeze();
        Focusable = true;
        Cursor = Cursors.Cross;
        Loaded += (_, _) =>
        {
            RequestRender();
            AppLogger.Info($"{LogTag}: screenshot layers ready; desktop={_desktop.Width}x{_desktop.Height}, renderingTier={RenderCapability.Tier >> RenderingTierShift}.");
        };
        Unloaded += (_, _) => CancelPendingRender();
    }

    protected override int VisualChildrenCount => _visuals.Count;
    protected override Visual GetVisualChild(int index) => _visuals[index];

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        RequestRender();
    }

    internal void RequestRender()
    {
        if (_renderPending || !IsLoaded)
            return;
        // 高频鼠标事件只更新状态，同一帧只提交一次绘制，也不触发 FrameworkElement 的布局失效。
        _renderPending = true;
        CompositionTarget.Rendering += RenderFrame;
    }

    private void CancelPendingRender()
    {
        if (!_renderPending)
            return;
        CompositionTarget.Rendering -= RenderFrame;
        _renderPending = false;
    }

    private void UpdateViewport()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        if (_viewportReady && dpi.DpiScaleX == _dpi.DpiScaleX && dpi.DpiScaleY == _dpi.DpiScaleY)
            return;
        _dpi = dpi;
        _viewportReady = true;
        var transform = new ScaleTransform(1 / dpi.DpiScaleX, 1 / dpi.DpiScaleY);
        transform.Freeze();
        foreach (DrawingVisual visual in _visuals)
            visual.Transform = transform;
        _border = new System.Windows.Media.Pen(new SolidColorBrush(SelectionBorderColor), SelectionBorderWidth * dpi.DpiScaleX);
        _border.Freeze();
        // 暗化桌面是静态层，拖动只更新选区覆盖的原图和边框，避免每帧重建整个桌面。
        using var drawing = _background.RenderOpen();
        drawing.DrawImage(_session.Capture.Image, _desktop);
        drawing.DrawRectangle(_dimBrush, null, _desktop);
        _selectionDirty = true;
    }

    private void RenderFrame(object? sender, EventArgs e)
    {
        CancelPendingRender();
        if (!IsLoaded)
            return;
        UpdateViewport();
        var highlight = _session.Selection ?? _session.Hover;
        if (_selectionDirty || _renderedHighlight != highlight)
        {
            using var image = _selection.RenderOpen();
            if (highlight is { IsEmpty: false } selected)
                image.DrawRectangle(_desktopBrush, null, selected);
            _renderedHighlight = highlight;
            _selectionDirty = false;
        }
        // 已提交标注不会随鼠标改变；移动/缩放选区只更新裁剪，撤销或新增时才重建矢量内容。
        if (_annotationVersion != _session.AnnotationVersion)
        {
            using var annotations = _annotations.RenderOpen();
            _session.DrawAnnotations(annotations, includeDraft: false);
            _annotationVersion = _session.AnnotationVersion;
        }
        if (!_clipReady || _renderedSelection != _session.Selection)
        {
            var clip = new RectangleGeometry(_session.Selection ?? Rect.Empty);
            clip.Freeze();
            _annotations.Clip = clip;
            _renderedSelection = _session.Selection;
            _clipReady = true;
        }
        using (var drawing = _interaction.RenderOpen())
            DrawInteraction(drawing, highlight);
        _session.RefreshSelectionInfo();
    }

    private void DrawInteraction(DrawingContext drawing, Rect? highlight)
    {
        if (highlight is { IsEmpty: false } rect)
        {
            if (_session.Selection is not null && _session.Draft is { } draft)
            {
                drawing.PushClip(_annotations.Clip);
                ScreenshotOverlayWindow.DrawAnnotation(drawing, draft);
                drawing.Pop();
            }
            drawing.DrawRectangle(null, _border, rect);
            if (_session.Selection is not null && _session.Tool == ScreenshotTool.Adjust)
            {
                double radius = SelectionHandleRadius * _dpi.DpiScaleX;
                DrawHandle(drawing, rect.TopLeft, radius);
                DrawHandle(drawing, rect.TopRight, radius);
                DrawHandle(drawing, rect.BottomLeft, radius);
                DrawHandle(drawing, rect.BottomRight, radius);
                DrawHandle(drawing, new Point(rect.Left + rect.Width / 2, rect.Top), radius);
                DrawHandle(drawing, new Point(rect.Left + rect.Width / 2, rect.Bottom), radius);
                DrawHandle(drawing, new Point(rect.Left, rect.Top + rect.Height / 2), radius);
                DrawHandle(drawing, new Point(rect.Right, rect.Top + rect.Height / 2), radius);
            }
        }
    }

    private void DrawHandle(DrawingContext drawing, Point point, double radius)
        => drawing.DrawRectangle(Brushes.White, _border, new Rect(point.X - radius, point.Y - radius, radius * 2, radius * 2));

    private Point ScreenPoint(MouseEventArgs e)
    {
        Point screen = PointToScreen(e.GetPosition(this));
        return new Point(Math.Clamp(screen.X - _session.Capture.Bounds.X, 0, _session.Capture.Image.PixelWidth),
            Math.Clamp(screen.Y - _session.Capture.Bounds.Y, 0, _session.Capture.Image.PixelHeight));
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        _start = ScreenPoint(e);
        if (_session.Selection is not { } selection)
        {
            _selecting = true;
            UpdateHover(_start);
        }
        else if (_session.Tool == ScreenshotTool.Adjust)
        {
            _edges = EdgesAt(_start, selection);
            if (_edges == 0 && !selection.Contains(_start))
                return;
            _originalSelection = selection;
        }
        else if (selection.Contains(_start) && _session.Tool is ScreenshotTool.Rectangle or ScreenshotTool.Pen)
            _session.Draft = new ScreenshotAnnotation(_session.Tool == ScreenshotTool.Rectangle,
                _session.InkColor, _session.InkWidth, [_start]);
        else
            return;
        CaptureMouse();
        _session.Refresh();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Point point = ScreenPoint(e);
        if (!IsMouseCaptured)
        {
            if (_session.Selection is null)
                UpdateHover(point);
            else if (_session.Tool == ScreenshotTool.Adjust && _session.Selection is { } selection)
            {
                int edges = EdgesAt(point, selection);
                Cursor = edges switch
                {
                    LeftEdge or RightEdge => Cursors.SizeWE,
                    TopEdge or BottomEdge => Cursors.SizeNS,
                    TopLeftCorner or BottomRightCorner => Cursors.SizeNWSE,
                    TopRightCorner or BottomLeftCorner => Cursors.SizeNESW,
                    _ => selection.Contains(point) ? Cursors.SizeAll : Cursors.Cross
                };
            }
            return;
        }
        UpdateGesture(point);
        RequestRender();
    }

    private void UpdateGesture(Point point)
    {
        if (_selecting)
        {
            if ((_start - point).Length >= SelectionDragThreshold)
                _session.Selection = _session.PixelRect(new Rect(_start, point));
        }
        else if (_originalSelection is { } original)
        {
            Vector delta = point - _start;
            double left = original.Left, top = original.Top, right = original.Right, bottom = original.Bottom;
            if (_edges == 0)
            {
                left = Math.Clamp(original.Left + delta.X, 0, _session.Capture.Image.PixelWidth - original.Width);
                top = Math.Clamp(original.Top + delta.Y, 0, _session.Capture.Image.PixelHeight - original.Height);
                right = left + original.Width;
                bottom = top + original.Height;
            }
            else
            {
                if ((_edges & LeftEdge) != 0) left = Math.Min(point.X, right - 1);
                if ((_edges & RightEdge) != 0) right = Math.Max(point.X, left + 1);
                if ((_edges & TopEdge) != 0) top = Math.Min(point.Y, bottom - 1);
                if ((_edges & BottomEdge) != 0) bottom = Math.Max(point.Y, top + 1);
            }
            _session.Selection = _session.PixelRect(new Rect(new Point(left, top), new Point(right, bottom)));
        }
        else if (_session.Draft is { } draft && _session.Selection is { } selected)
        {
            point = new Point(Math.Clamp(point.X, selected.Left, selected.Right), Math.Clamp(point.Y, selected.Top, selected.Bottom));
            if (draft.Rectangle)
            {
                if (draft.Points.Count == 1) draft.Points.Add(point); else draft.Points[1] = point;
            }
            else if ((draft.Points[^1] - point).Length >= 1)
                draft.Points.Add(point);
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!IsMouseCaptured)
            return;
        // 帧合并不能丢失松手位置；提交和导出始终使用最后一个输入坐标。
        UpdateGesture(ScreenPoint(e));
        if (_selecting)
        {
            var selected = _session.Selection ?? _session.Hover;
            _selecting = false;
            if (selected is { IsEmpty: false } rect)
                _session.Selected(rect);
            else
                _session.Selection = null;
        }
        else if (_session.Draft is { } draft)
        {
            if (draft.Rectangle && (draft.Points.Count < 2 || new Rect(draft.Points[0], draft.Points[^1]).Width < 1
                    || new Rect(draft.Points[0], draft.Points[^1]).Height < 1))
                _session.Draft = null;
            _session.CommitDraft();
        }
        _originalSelection = null;
        ReleaseMouseCapture();
        _session.Refresh();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        CancelGesture();
    }

    internal void CancelGesture()
    {
        bool selecting = _selecting;
        _selecting = false;
        if (_originalSelection is { } original) _session.Selection = original;
        else if (selecting) _session.Selection = null;
        _originalSelection = null;
        _session.Draft = null;
        if (IsMouseCaptured) ReleaseMouseCapture();
        _session.Refresh();
    }

    private void UpdateHover(Point point)
    {
        Rect? hover = null;
        if (_session.Tool == ScreenshotTool.Window)
        {
            Point screen = new(point.X + _session.Capture.Bounds.X, point.Y + _session.Capture.Bounds.Y);
            foreach (var window in _session.Capture.Windows)
            {
                if (!window.Contains(screen)) continue;
                Rect local = window;
                local.Offset(-_session.Capture.Bounds.X, -_session.Capture.Bounds.Y);
                hover = _session.PixelRect(local);
                break;
            }
        }
        if (_session.Hover != hover)
        {
            _session.Hover = hover;
            RequestRender();
        }
    }

    private int EdgesAt(Point point, Rect rect)
    {
        double tolerance = SelectionEdgeTolerance * VisualTreeHelper.GetDpi(this).DpiScaleX;
        Rect expanded = rect;
        expanded.Inflate(tolerance, tolerance);
        if (!expanded.Contains(point)) return 0;
        int edges = 0;
        if (Math.Abs(point.X - rect.Left) <= tolerance) edges |= LeftEdge;
        else if (Math.Abs(point.X - rect.Right) <= tolerance) edges |= RightEdge;
        if (Math.Abs(point.Y - rect.Top) <= tolerance) edges |= TopEdge;
        else if (Math.Abs(point.Y - rect.Bottom) <= tolerance) edges |= BottomEdge;
        return edges;
    }
}
