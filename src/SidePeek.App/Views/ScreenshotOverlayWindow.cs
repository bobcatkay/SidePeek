using System.Diagnostics;
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
    private const int DiagnosticReplayStartupDelayMilliseconds = 500;
    private const string SingleMonitorDiagnosticsVariable = "SIDEPEEK_SCREENSHOT_SINGLE_MONITOR";
    private const string SpanningDiagnosticsVariable = "SIDEPEEK_SCREENSHOT_SPANNING_WINDOW";
    private const string DiagnosticsEnabledValue = "1";
    private readonly DesktopCapture _capture;
    private readonly Rect _windowBounds;
    private readonly ScreenshotSurface _surface;
    private readonly List<ScreenshotSurface> _surfaces = [];
    private readonly List<ScreenshotMonitorWindow> _monitorWindows = [];
    private ScreenshotSurface _inputSurface;
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
    internal bool CanExport => Selection is { IsEmpty: false } && !_surfaces.Any(surface => surface.IsGestureActive);
    internal int AnnotationVersion { get; private set; }
    internal IReadOnlyList<ScreenshotAnnotation> Annotations => _annotations;

    internal ScreenshotOverlayWindow(DesktopCapture capture)
    {
        _capture = capture;
        bool singleMonitor = App.ScreenshotDiagnosticsEnabled
            && Environment.GetEnvironmentVariable(SingleMonitorDiagnosticsVariable) == DiagnosticsEnabledValue;
        bool spanningWindow = App.ScreenshotDiagnosticsEnabled
            && Environment.GetEnvironmentVariable(SpanningDiagnosticsVariable) == DiagnosticsEnabledValue;
        NativeMethods.GetCursorPos(out var cursor);
        // 自动对照使用诊断指定的显示器，避免窗口模式改变同时改变测试区域。
        Point initialScreen = singleMonitor || App.ScreenshotDiagnosticsReplayEnabled
            ? ScreenshotDiagnostics.ReplayMonitor(capture).Bounds.TopLeft : new Point(cursor.X, cursor.Y);
        var initialMonitor = capture.Monitors.FirstOrDefault(monitor => monitor.Bounds.Contains(initialScreen))
            ?? capture.Monitors[0];
        _windowBounds = spanningWindow ? capture.Bounds : initialMonitor.Bounds;
        Title = "SidePeek 截图";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        // 诊断入口暴露一个可定位的顶层窗口，正常截图仍不占用任务栏。
        ShowInTaskbar = App.ScreenshotDiagnosticsCaptureVisible;
        Topmost = true;
        Background = Brushes.Black;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -32000;
        Top = -32000;
        Width = Height = 1;
        try
        {
            _surface = new ScreenshotSurface(this, _windowBounds, initialMonitor.Bounds);
            _inputSurface = _surface;
            _surfaces.Add(_surface);
            Content = _surface;
            // 每屏保留独立原生画布和物理边界；手势及导出仍共用全桌面坐标。
            if (!spanningWindow && !singleMonitor)
            {
                foreach (var monitor in capture.Monitors.Where(monitor => monitor != initialMonitor))
                {
                    var surface = new ScreenshotSurface(this, monitor.Bounds);
                    _surfaces.Add(surface);
                    _monitorWindows.Add(new ScreenshotMonitorWindow(this, monitor.Bounds, surface));
                }
            }
            _toolbar = new ScreenshotToolbarWindow(this);
        }
        catch
        {
            // 后续屏幕或工具栏创建失败时，先前画布尚未 Loaded，不会收到 Unloaded 释放 DIB。
            _closed = true;
            foreach (var surface in _surfaces) surface.DisposeResources();
            foreach (var monitorWindow in _monitorWindows) monitorWindow.Close();
            Close();
            throw;
        }
        PreviewKeyDown += OnScreenshotKey;
        Loaded += async (_, _) =>
        {
            PositionOverlay();
            foreach (var monitorWindow in _monitorWindows)
            {
                monitorWindow.Owner = this;
                monitorWindow.Show();
            }
            _toolbar.Owner = this;
            if (!ScreenshotDiagnostics.HideToolbar) _toolbar.Show();
            Activate();
            _surface.FocusInput();
            AppLogger.Info($"ScreenshotOverlayWindow: overlay opened; windows={_surfaces.Count}, spanningDiagnostics={spanningWindow}.");
            if (App.ScreenshotDiagnosticsReplayEnabled)
            {
                await Task.Delay(DiagnosticReplayStartupDelayMilliseconds);
                if (!_closed) _surface.ReplaySelection(ScreenshotDiagnostics.AcrossScreensReplay);
            }
        };
        Closed += (_, _) =>
        {
            _closed = true;
            // Show 失败或尚未显示就关闭时也须释放原生缓冲，不能只依赖 Unloaded。
            foreach (var surface in _surfaces) surface.DisposeResources();
            foreach (var monitorWindow in _monitorWindows)
                monitorWindow.Close();
            _toolbar.Close();
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        source?.AddHook(WindowMessage);
        if (ScreenshotDiagnostics.SoftwareRendering && source?.CompositionTarget is { } target)
            target.RenderMode = RenderMode.SoftwareOnly;
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
            // 冻结画布保持当前宿主的物理屏幕边界，不采用 DPI 变化给出的建议范围。
            Dispatcher.BeginInvoke(new Action(() => { if (!_closed) PositionOverlay(); }));
        }
        return IntPtr.Zero;
    }

    private void PositionOverlay()
    {
        var bounds = _windowBounds;
        if (!NativeMethods.SetWindowPos(new WindowInteropHelper(this).Handle, NativeMethods.HWND_TOPMOST,
                (int)bounds.X, (int)bounds.Y, (int)bounds.Width, (int)bounds.Height, NativeMethods.SWP_NOACTIVATE))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        _surface.UpdateVisuals();
    }

    internal void SetTool(ScreenshotTool tool)
    {
        if (_exporting)
            return;
        CancelGestures();
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
        foreach (var surface in _surfaces) surface.Cursor = Cursors.Cross;
        Hover = null;
        Refresh();
        FocusInputSurface();
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
        CancelGestures();
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
        CancelGestures();
        if (_redo.TryPop(out var annotation))
        {
            _annotations.Add(annotation);
            AnnotationVersion++;
        }
        Refresh();
    }

    internal void Refresh()
    {
        UpdateVisuals();
        _toolbar.Refresh();
    }

    internal void UpdateVisuals()
    {
        foreach (var surface in _surfaces) surface.UpdateVisuals();
    }

    internal void UseInputSurface(ScreenshotSurface surface) => _inputSurface = surface;

    internal bool CancelReplay()
    {
        if (!_surfaces.Any(surface => surface.IsReplaying)) return false;
        CancelGestures();
        return true;
    }

    internal void CloseFromMonitor()
        => Dispatcher.BeginInvoke(new Action(() => { if (!_closed) Close(); }));

    private void FocusInputSurface()
    {
        Window.GetWindow(_inputSurface)?.Activate();
        _inputSurface.FocusInput();
    }

    private void CancelGestures()
    {
        foreach (var surface in _surfaces.Where(surface => surface.IsGestureActive))
            surface.CancelGesture();
    }

    private void SetSurfaceInputEnabled(bool enabled)
    {
        foreach (var surface in _surfaces) surface.IsEnabled = enabled;
    }

    internal void RefreshSelectionInfo() => _toolbar.RefreshSelection();

    internal void ActivateTools()
    {
        if (_closed) return;
        if (!_toolbar.IsVisible) _toolbar.Show();
        _toolbar.Activate();
    }

    internal void OnScreenshotKey(object sender, KeyEventArgs e)
        => e.Handled = HandleKey(e.Key, Keyboard.Modifiers);

    internal bool HandleNativeKey(int virtualKey, bool control, bool shift, bool alt)
        => HandleKey(KeyInterop.KeyFromVirtualKey(virtualKey),
            (control ? ModifierKeys.Control : ModifierKeys.None)
            | (shift ? ModifierKeys.Shift : ModifierKeys.None)
            | (alt ? ModifierKeys.Alt : ModifierKeys.None));

    private bool HandleKey(Key key, ModifierKeys modifiers)
    {
        if (_exporting)
        {
            if (key == Key.Escape)
                Close();
            return true;
        }
        if (key == Key.Escape)
            Close();
        else if ((modifiers == ModifierKeys.Control && key == Key.C)
                 || (modifiers == ModifierKeys.None && key == Key.Enter))
            CopyImage();
        else if (modifiers == ModifierKeys.Control && key == Key.S)
            SaveImage();
        else if (modifiers == ModifierKeys.Control && key == Key.Z)
            Undo();
        else if ((modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && key == Key.Z)
                 || (modifiers == ModifierKeys.Control && key == Key.Y))
            Redo();
        else if (modifiers == ModifierKeys.None && key == Key.Tab)
        {
            if (_toolbar.IsVisible) _toolbar.Hide(); else _toolbar.Show();
        }
        else if (modifiers == ModifierKeys.None && key == Key.W)
            SetTool(ScreenshotTool.Window);
        else if (modifiers == ModifierKeys.None && key == Key.A)
            SetTool(ScreenshotTool.Area);
        else if (modifiers == ModifierKeys.None && key == Key.V)
            SetTool(ScreenshotTool.Adjust);
        else if (modifiers == ModifierKeys.None && key == Key.R)
            SetTool(ScreenshotTool.Rectangle);
        else if (modifiers == ModifierKeys.None && key == Key.P)
            SetTool(ScreenshotTool.Pen);
        else if (App.ScreenshotDiagnosticsEnabled && modifiers == ModifierKeys.None && key == Key.F8)
            _inputSurface.ReplaySelection();
        else if (App.ScreenshotDiagnosticsEnabled && modifiers == ModifierKeys.None && key == Key.F9)
            _inputSurface.ReplaySelection(acrossScreens: true);
        else
            return false;
        return true;
    }

    internal BitmapSource RenderSelection()
    {
        CancelGestures();
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
        SetSurfaceInputEnabled(false);
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
            SetSurfaceInputEnabled(true);
            if (!_closed) _toolbar.SetBusy(false);
        }
    }

    internal void SaveImage()
    {
        if (_exporting || !CanExport)
            return;
        _exporting = true;
        SetSurfaceInputEnabled(false);
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
            SetSurfaceInputEnabled(true);
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
    private const string FrameSyncDiagnosticsVariable = "SIDEPEEK_SCREENSHOT_FRAME_SYNC";
    private const string DiagnosticsEnabledValue = "1";
    private const string FrameSyncSchedulingMode = "frame-sync";
    private const string InputSchedulingMode = "input-driven";
    private const byte DimmingAlpha = 120;
    private const double SelectionBorderWidth = 2;
    private const double SelectionHandleRadius = 3;
    private const double SelectionDragThreshold = 3;
    private const double SelectionEdgeTolerance = 8;
    private const int UnrenderedAnnotationVersion = -1;
    private const int RenderingTierShift = 16;
    private const double ReplayStartFraction = 0.15;
    private const double ReplayEndFraction = 0.85;
    private const int ReplaySweepCount = 4;
    private const int PresentationDrainMilliseconds = 200;
    private static readonly Color SelectionBorderColor = Color.FromRgb(83, 182, 255);
    private readonly ScreenshotOverlayWindow _session;
    private readonly ScreenshotPerformanceProbe _performance = new();
    private ScreenshotPresentationProbe? _presentationProbe;
    private long _presentationMarker;
    private long _drawnPresentationMarker;
    private readonly bool _frameSyncEnabled = ScreenshotDiagnostics.WpfRenderer
        && Environment.GetEnvironmentVariable(FrameSyncDiagnosticsVariable) == DiagnosticsEnabledValue;
    private readonly DrawingVisual _background = new();
    private readonly ShadeVisual _topShade = new();
    private readonly ShadeVisual _bottomShade = new();
    private readonly ShadeVisual _leftShade = new();
    private readonly ShadeVisual _rightShade = new();
    private readonly DrawingVisual _annotations = new();
    private readonly DrawingVisual _interaction = new();
    private readonly DrawingVisual _diagnosticMarkerVisual = new();
    private readonly VisualCollection _visuals;
    private readonly ScreenshotNativeCanvas? _nativeCanvas;
    private readonly Rect _desktop;
    private readonly Rect _viewport;
    private readonly Rect _replayViewport;
    private readonly BitmapSource _backgroundImage;
    private readonly SolidColorBrush _dimBrush;
    private System.Windows.Media.Pen? _border;
    private DpiScale _dpi;
    private bool _viewportReady;
    private bool _clipReady;
    private bool _interactionReady;
    private Rect? _drawnHighlight;
    private ScreenshotTool _drawnTool;
    private bool _hadDraft;
    private Rect? _renderedSelection;
    private int _annotationVersion = UnrenderedAnnotationVersion;
    private Point _start;
    private bool _selecting;
    private Rect? _originalSelection;
    private int _edges;
    private CancellationTokenSource? _replayCancellation;
    private bool _gestureRenderingSubscribed;
    private bool _gestureVisualsDirty;
    private bool _resourcesDisposed;
    private TimeSpan? _lastGestureRenderingTime;
    private string VisualSchedulingMode => _frameSyncEnabled ? FrameSyncSchedulingMode : InputSchedulingMode;
    private bool PointerCaptured => _nativeCanvas?.IsPointerCaptured ?? IsMouseCaptured;
    internal bool IsGestureActive => PointerCaptured || _replayCancellation is not null;
    internal bool IsReplaying => _replayCancellation is not null;
    private const int LeftEdge = 1, TopEdge = 2, RightEdge = 4, BottomEdge = 8;
    private const int TopLeftCorner = LeftEdge | TopEdge, TopRightCorner = RightEdge | TopEdge;
    private const int BottomLeftCorner = LeftEdge | BottomEdge, BottomRightCorner = RightEdge | BottomEdge;

    internal ScreenshotSurface(ScreenshotOverlayWindow session, Rect screenBounds, Rect? replayScreenBounds = null)
    {
        _session = session;
        _visuals = new VisualCollection(this)
        {
            _background, _topShade, _bottomShade, _leftShade, _rightShade, _annotations, _interaction, _diagnosticMarkerVisual
        };
        _desktop = new Rect(0, 0, session.Capture.Image.PixelWidth, session.Capture.Image.PixelHeight);
        _viewport = new Rect(screenBounds.X - session.Capture.Bounds.X, screenBounds.Y - session.Capture.Bounds.Y,
            screenBounds.Width, screenBounds.Height);
        var replayBounds = replayScreenBounds ?? screenBounds;
        _replayViewport = new Rect(replayBounds.X - session.Capture.Bounds.X, replayBounds.Y - session.Capture.Bounds.Y,
            replayBounds.Width, replayBounds.Height);
        // 各窗口只上传本屏位图；最终导出仍使用会话内未经裁剪的原始图。
        _backgroundImage = new CroppedBitmap(session.Capture.Image,
            new Int32Rect((int)_viewport.X, (int)_viewport.Y, (int)_viewport.Width, (int)_viewport.Height));
        _backgroundImage.Freeze();
        var viewportClip = new RectangleGeometry(_viewport);
        viewportClip.Freeze();
        _interaction.Clip = viewportClip;
        _dimBrush = new SolidColorBrush(Color.FromArgb(DimmingAlpha, 0, 0, 0));
        _dimBrush.Freeze();
        Focusable = true;
        Cursor = Cursors.Cross;
        if (!ScreenshotDiagnostics.WpfRenderer)
        {
            // 输入与选框像素在同一原生子窗口更新，避开 WPF 高频提交后桌面长时间不更新的路径。
            _nativeCanvas = new ScreenshotNativeCanvas(session, this, _viewport, _backgroundImage);
            _visuals.Clear();
            _visuals.Add(_nativeCanvas);
            AddLogicalChild(_nativeCanvas);
        }
        Loaded += (_, _) =>
        {
            UpdateVisuals();
            var target = (PresentationSource.FromVisual(this) as HwndSource)?.CompositionTarget;
            AppLogger.Info($"{LogTag}: screenshot visuals ready; backend={(_nativeCanvas is null ? "WPF" : "GDI")}, visualScheduling={VisualSchedulingMode}, frameSyncDiagnostics={_frameSyncEnabled}, "
                + $"desktop={_desktop.Width}x{_desktop.Height}, viewport={_viewport.Width}x{_viewport.Height}, "
                + $"dpi={_dpi.DpiScaleX:F2}x{_dpi.DpiScaleY:F2}, renderingTier={RenderCapability.Tier >> RenderingTierShift}, "
                + $"maxTexture={RenderCapability.MaxHardwareTextureSize}, renderMode={target?.RenderMode}, monitors={session.Capture.Monitors.Count}.");
        };
        Unloaded += (_, _) => DisposeResources();
    }

    internal void DisposeResources()
    {
        if (_resourcesDisposed) return;
        _resourcesDisposed = true;
        _replayCancellation?.Cancel();
        StopGestureRendering(flush: false);
        _presentationProbe?.Dispose();
        _nativeCanvas?.Dispose();
        _performance.Dispose();
    }

    protected override int VisualChildrenCount => _visuals.Count;
    protected override Visual GetVisualChild(int index) => _visuals[index];

    protected override Size MeasureOverride(Size availableSize)
    {
        _nativeCanvas?.Measure(availableSize);
        return _nativeCanvas?.DesiredSize ?? base.MeasureOverride(availableSize);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _nativeCanvas?.Arrange(new Rect(finalSize));
        return finalSize;
    }

    internal void FocusInput()
    {
        Focus();
        _nativeCanvas?.FocusInput();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        UpdateVisuals();
    }

    private void UpdateViewport()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        if (_viewportReady && dpi.DpiScaleX == _dpi.DpiScaleX && dpi.DpiScaleY == _dpi.DpiScaleY)
            return;
        _dpi = dpi;
        _viewportReady = true;
        _interactionReady = false;
        var transform = new MatrixTransform(1 / dpi.DpiScaleX, 0, 0, 1 / dpi.DpiScaleY,
            -_viewport.X / dpi.DpiScaleX, -_viewport.Y / dpi.DpiScaleY);
        transform.Freeze();
        foreach (DrawingVisual visual in _visuals)
            visual.Transform = transform;
        _border = new System.Windows.Media.Pen(new SolidColorBrush(SelectionBorderColor), SelectionBorderWidth * dpi.DpiScaleX);
        _border.Freeze();
        // DrawingVisual 缓存的是绘制指令；背景只保留一张原图，避免选区内重复采样和透明混合。
        using var drawing = _background.RenderOpen();
        drawing.DrawImage(_backgroundImage, _viewport);
    }

    internal void UpdateVisuals()
    {
        if (!IsLoaded)
            return;
        if (_nativeCanvas is not null)
        {
            _dpi = VisualTreeHelper.GetDpi(this);
            _nativeCanvas.SetCursor(Cursor);
            _nativeCanvas.UpdateVisuals(_presentationProbe, _presentationMarker);
            return;
        }
        // 只更新保留的绘制内容；诊断帧合并可从 Rendering 调用，工具栏布局仍在输入阶段更新。
        UpdateViewport();
        var highlight = _session.Selection ?? _session.Hover;
        UpdateShading(highlight);
        // 已提交标注不会随鼠标改变；移动/缩放选区只更新裁剪，撤销或新增时才重建矢量内容。
        if (_annotationVersion != _session.AnnotationVersion)
        {
            using var annotations = _annotations.RenderOpen();
            _session.DrawAnnotations(annotations, includeDraft: false);
            _annotationVersion = _session.AnnotationVersion;
        }
        Rect selectionClip = _session.Selection is { } selection ? Rect.Intersect(selection, _viewport) : Rect.Empty;
        if (!_clipReady || _renderedSelection != selectionClip)
        {
            var clip = new RectangleGeometry(selectionClip);
            clip.Freeze();
            _annotations.Clip = clip;
            _renderedSelection = selectionClip;
            _clipReady = true;
        }
        Rect? visibleHighlight = null;
        if (highlight is { } bounds)
        {
            // 边框和手柄会伸出选区；屏缝两侧都要绘制各自可见部分，不能只用选区本体剔除。
            Rect inkBounds = bounds;
            double outsideInk = (SelectionBorderWidth / 2
                + (_session.Tool == ScreenshotTool.Adjust ? SelectionHandleRadius : 0)) * _dpi.DpiScaleX;
            inkBounds.Inflate(outsideInk, outsideInk);
            if (inkBounds.IntersectsWith(_viewport)) visibleHighlight = bounds;
        }
        bool hasDraft = _session.Draft is not null;
        // 选区完全位于另一屏时，不使本屏的交互图层失效；边框仍画原始选区并裁剪，避免屏缝出现假边。
        if (!_interactionReady || _drawnHighlight != visibleHighlight || _drawnTool != _session.Tool || hasDraft || _hadDraft)
        {
            using var drawing = _interaction.RenderOpen();
            DrawInteraction(drawing, visibleHighlight);
            _drawnHighlight = visibleHighlight;
            _drawnTool = _session.Tool;
            _hadDraft = hasDraft;
            _interactionReady = true;
        }
        if (_drawnPresentationMarker != _presentationMarker)
        {
            // 同一 UI 调用内更新，随选框一起提交；独立小图层避免条码扩大选框的重绘包围盒。
            using var markerDrawing = _diagnosticMarkerVisual.RenderOpen();
            _presentationProbe?.DrawMarker(markerDrawing, _presentationMarker);
            _drawnPresentationMarker = _presentationMarker;
        }
    }

    private void UpdateShading(Rect? highlight)
    {
        // 四块蒙版分别保留，只有边界改变的块才更新；选区内直接露出背景，不再叠画原图。
        Rect rect = highlight is { } bounds ? Rect.Intersect(bounds, _viewport) : Rect.Empty;
        if (!rect.IsEmpty)
        {
            _topShade.SetBounds(new Rect(_viewport.Left, _viewport.Top, _viewport.Width, rect.Top - _viewport.Top), _dimBrush);
            _bottomShade.SetBounds(new Rect(_viewport.Left, rect.Bottom, _viewport.Width, _viewport.Bottom - rect.Bottom), _dimBrush);
            _leftShade.SetBounds(new Rect(_viewport.Left, rect.Top, rect.Left - _viewport.Left, rect.Height), _dimBrush);
            _rightShade.SetBounds(new Rect(rect.Right, rect.Top, _viewport.Right - rect.Right, rect.Height), _dimBrush);
        }
        else
        {
            _topShade.SetBounds(_viewport, _dimBrush);
            _bottomShade.SetBounds(Rect.Empty, _dimBrush);
            _leftShade.SetBounds(Rect.Empty, _dimBrush);
            _rightShade.SetBounds(Rect.Empty, _dimBrush);
        }
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
        if (_nativeCanvas is null) e.Handled = PointerDown(ScreenPoint(e));
    }

    internal void NativePointerDown(Point point, int timestamp) => PointerDown(point);

    private bool PointerDown(Point point)
    {
        if (_session.CancelReplay())
            return true;
        _session.UseInputSurface(this);
        FocusInput();
        _start = point;
        if (_session.Selection is not { } selection)
        {
            _selecting = true;
            UpdateHover(_start);
        }
        else if (_session.Tool == ScreenshotTool.Adjust)
        {
            _edges = EdgesAt(_start, selection);
            if (_edges == 0 && !selection.Contains(_start))
                return false;
            _originalSelection = selection;
        }
        else if (selection.Contains(_start) && _session.Tool is ScreenshotTool.Rectangle or ScreenshotTool.Pen)
            _session.Draft = new ScreenshotAnnotation(_session.Tool == ScreenshotTool.Rectangle,
                _session.InkColor, _session.InkWidth, [_start]);
        else
            return false;
        if (!(_nativeCanvas?.CapturePointer() ?? CaptureMouse()))
        {
            // 捕获失败不会收到预期的松手事件，不能留下持续 Rendering 订阅。
            CancelGesture();
            return true;
        }
        BeginGestureRendering(_selecting ? $"{_session.Tool}:Select" : _session.Tool.ToString());
        _session.Refresh();
        return true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_nativeCanvas is null) PointerMove(ScreenPoint(e), e.Timestamp);
    }

    internal void NativePointerMove(Point point, int timestamp) => PointerMove(point, timestamp);

    private void PointerMove(Point point, int timestamp)
    {
        long inputStarted = Stopwatch.GetTimestamp();
        if (PointerCaptured)
            _performance.RecordInput(timestamp);
        if (!PointerCaptured)
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
                _nativeCanvas?.SetCursor(Cursor);
            }
            return;
        }
        UpdateGestureVisuals(point, inputStarted);
    }

    private void UpdateGestureVisuals(Point point, long inputStarted)
    {
        bool changed = UpdateGesture(point);
        double visualMs = 0, toolbarMs = 0;
        if (changed)
        {
            _presentationMarker = _presentationProbe?.RecordInput(inputStarted) ?? 0;
            if (_gestureRenderingSubscribed)
            {
                // 模型和笔迹点已逐次更新，只合并绘制，避免丢失画笔的中间采样。
                _gestureVisualsDirty = true;
            }
            else
            {
                long visualStarted = Stopwatch.GetTimestamp();
                _session.UpdateVisuals();
                visualMs = Stopwatch.GetElapsedTime(visualStarted).TotalMilliseconds;
            }
            long toolbarStarted = Stopwatch.GetTimestamp();
            _session.RefreshSelectionInfo();
            toolbarMs = Stopwatch.GetElapsedTime(toolbarStarted).TotalMilliseconds;
        }
        _performance.RecordUpdate(visualMs, toolbarMs, Stopwatch.GetElapsedTime(inputStarted).TotalMilliseconds, changed);
    }

    private void BeginGestureRendering(string tool)
    {
        // summary 中区分两条路径；frame-sync 的 visualUpdateMs 仅统计输入阶段，不包含延后的绘制。
        _performance.Begin($"{tool}:{VisualSchedulingMode}");
        if (!_frameSyncEnabled || _gestureRenderingSubscribed)
            return;
        _gestureVisualsDirty = false;
        _lastGestureRenderingTime = null;
        // 整个手势保持订阅，让 WPF 使用持续呈现调度；逐次订阅并立即退订不能提供这一边界。
        CompositionTarget.Rendering += OnGestureRendering;
        _gestureRenderingSubscribed = true;
    }

    private void OnGestureRendering(object? sender, EventArgs e)
    {
        if (!_gestureRenderingSubscribed || !_gestureVisualsDirty || !IsLoaded)
            return;
        if (e is RenderingEventArgs rendering)
        {
            // 同一预计呈现时刻可能出现额外回调，只提交最新模型的一次绘制。
            if (_lastGestureRenderingTime == rendering.RenderingTime)
                return;
            _lastGestureRenderingTime = rendering.RenderingTime;
        }
        _gestureVisualsDirty = false;
        _session.UpdateVisuals();
    }

    private void StopGestureRendering(bool flush)
    {
        if (!_gestureRenderingSubscribed)
            return;
        CompositionTarget.Rendering -= OnGestureRendering;
        _gestureRenderingSubscribed = false;
        _gestureVisualsDirty = false;
        _lastGestureRenderingTime = null;
        // 松手及回滚不等待下一帧；卸载时直接释放订阅，不再绘制已销毁的宿主。
        if (flush && IsLoaded)
            _session.UpdateVisuals();
    }

    internal async void ReplaySelection(bool acrossScreens = false)
    {
        if (!App.ScreenshotDiagnosticsEnabled || _replayCancellation is not null)
            return;
        if (ScreenshotDiagnostics.InteractionRegression)
        {
            try
            {
                ScreenshotInteractionDiagnostics.Run(_session, this, _replayViewport);
            }
            catch (Exception error)
            {
                AppLogger.Error($"{LogTag}: interaction regression failed.", error);
                _session.Close();
                if (ScreenshotDiagnostics.ExitAfterReplay) Application.Current.Shutdown(1);
                return;
            }
        }
        _session.SetTool(ScreenshotTool.Area);
        using var cancellation = new CancellationTokenSource();
        _replayCancellation = cancellation;
        var viewport = acrossScreens ? _desktop : _replayViewport;
        _start = new Point(viewport.X + viewport.Width * ReplayStartFraction, viewport.Y + viewport.Height * ReplayStartFraction);
        _selecting = true;
        BeginGestureRendering(acrossScreens ? "Replay:AcrossScreens" : "Replay:Select");
        try
        {
            var monitorBounds = _replayViewport;
            monitorBounds.Offset(_session.Capture.Bounds.X, _session.Capture.Bounds.Y);
            var monitor = _session.Capture.Monitors.First(item => item.Bounds == monitorBounds);
            _presentationProbe = ScreenshotPresentationProbe.TryCreate(monitor, _replayViewport);
            if (_presentationProbe is not null && !await _presentationProbe.Ready)
                AppLogger.Info($"{LogTag}: presentation readback unavailable; replay continues without display latency evidence.");
            // 复用真实拖动的更新路径，持续改变大选区；合成输入只能测 UI 排队和更新，不能代替系统鼠标延迟。
            await ScreenshotInputReplay.RunAsync(Dispatcher, (timestamp, progress) =>
            {
                long started = Stopwatch.GetTimestamp();
                _performance.RecordInput(timestamp);
                double sweep = (1 - Math.Cos(progress * (ReplaySweepCount * Math.Tau + Math.PI))) / 2;
                double fraction = ReplayStartFraction + (ReplayEndFraction - ReplayStartFraction) * sweep;
                UpdateGestureVisuals(new Point(viewport.X + viewport.Width * fraction, viewport.Y + viewport.Height * fraction), started);
            }, cancellation.Token);
            StopGestureRendering(flush: true);
            _performance.End("replay-completed");
            _selecting = false;
            if (_session.Selection is { IsEmpty: false } selection)
                _session.Selected(selection);
            await Task.Delay(PresentationDrainMilliseconds, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            _performance.End("replay-cancelled");
        }
        catch (Exception error)
        {
            _performance.End("replay-failed");
            AppLogger.Error($"{LogTag}: selection replay failed.", error);
        }
        finally
        {
            StopGestureRendering(flush: true);
            if (_presentationProbe is not null)
            {
                await _presentationProbe.DisposeAsync();
                _presentationProbe = null;
                _presentationMarker = 0;
            }
            _replayCancellation = null;
            _selecting = false;
            if (IsLoaded) _session.Refresh();
            if (ScreenshotDiagnostics.ExitAfterReplay)
            {
                _session.Close();
                Application.Current.Shutdown();
            }
        }
    }

    private bool UpdateGesture(Point point)
    {
        if (_selecting)
        {
            if ((_start - point).Length >= SelectionDragThreshold)
                return UpdateSelection(_session.PixelRect(new Rect(_start, point)));
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
            return UpdateSelection(_session.PixelRect(new Rect(new Point(left, top), new Point(right, bottom))));
        }
        else if (_session.Draft is { } draft && _session.Selection is { } selected)
        {
            point = new Point(Math.Clamp(point.X, selected.Left, selected.Right), Math.Clamp(point.Y, selected.Top, selected.Bottom));
            if (draft.Rectangle)
            {
                if (draft.Points[^1] == point)
                    return false;
                if (draft.Points.Count == 1) draft.Points.Add(point); else draft.Points[1] = point;
                return true;
            }
            else if ((draft.Points[^1] - point).Length >= 1)
            {
                draft.Points.Add(point);
                return true;
            }
        }
        return false;
    }

    private bool UpdateSelection(Rect selection)
    {
        // 像素取整、边界钳制后可能仍是相同选区，无变化的鼠标事件不应产生新的绘制工作。
        if (_session.Selection == selection)
            return false;
        _session.Selection = selection;
        return true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_nativeCanvas is null) e.Handled = PointerUp(ScreenPoint(e));
    }

    internal void NativePointerUp(Point point, int timestamp) => PointerUp(point);

    private bool PointerUp(Point point)
    {
        if (!PointerCaptured)
            return false;
        // 松手事件可能没有对应的 MouseMove，提交前补上最终坐标。
        UpdateGesture(point);
        StopGestureRendering(flush: true);
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
        _performance.End("completed");
        ReleasePointer();
        _session.Refresh();
        return true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_nativeCanvas is null) CancelGesture();
    }

    private void ReleasePointer()
    {
        if (_nativeCanvas is not null) _nativeCanvas.ReleasePointer();
        else ReleaseMouseCapture();
    }

    internal void CancelGesture()
    {
        _replayCancellation?.Cancel();
        _performance.End("cancelled");
        bool selecting = _selecting;
        _selecting = false;
        if (_originalSelection is { } original) _session.Selection = original;
        else if (selecting) _session.Selection = null;
        _originalSelection = null;
        _session.Draft = null;
        StopGestureRendering(flush: true);
        if (PointerCaptured) ReleasePointer();
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
            _session.UpdateVisuals();
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

    private sealed class ShadeVisual : DrawingVisual
    {
        private Rect _bounds = Rect.Empty;

        internal void SetBounds(Rect bounds, Brush brush)
        {
            if (_bounds == bounds)
                return;
            _bounds = bounds;
            using var drawing = RenderOpen();
            if (!bounds.IsEmpty && bounds.Width > 0 && bounds.Height > 0)
                drawing.DrawRectangle(brush, null, bounds);
        }
    }
}
