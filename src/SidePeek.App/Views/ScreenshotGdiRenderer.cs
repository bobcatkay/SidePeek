using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SidePeek.App.Interop;
using SidePeek.App.Services;
using Drawing = System.Drawing;
using Drawing2D = System.Drawing.Drawing2D;

namespace SidePeek.App.Views;

/// <summary>每屏保留原图、暗图和最终帧，只重建发生变化的物理像素区域。</summary>
internal sealed class ScreenshotGdiRenderer : IDisposable
{
    private const string LogTag = nameof(ScreenshotGdiRenderer);
    private const byte DimmingAlpha = 120;
    private const int BytesPerPixel = 4;
    private const int BitsPerByte = 8;
    private const int BitmapBitCount = BytesPerPixel * BitsPerByte;
    private const int MaximumDirtyRectangles = 64;
    private const double SelectionBorderWidth = 2;
    private const double SelectionHandleRadius = 3;
    private const double AntialiasPadding = 2;
    private const int InitialAnnotationVersion = -1;
    private const uint SourceCopy = 0x00CC0020;
    private const uint WhiteColor = 0x00FFFFFF;
    private static readonly Drawing.Color BorderColor = Drawing.Color.FromArgb(83, 182, 255);
    private readonly Rect _viewport;
    private readonly PixelRect _canvas;
    private readonly List<PixelRect> _changes = new(MaximumDirtyRectangles);
    private readonly List<PixelRect> _pendingPaint = new(MaximumDirtyRectangles);
    private Dib? _original;
    private Dib? _dimmed;
    private Dib? _frame;
    private IntPtr _blackBrush;
    private IntPtr _whiteBrush;
    private Rect? _lastHighlight;
    private Rect? _lastSelection;
    private ScreenshotTool _lastTool;
    private double _lastDpi = 1;
    private int _annotationVersion = InitialAnnotationVersion;
    private ScreenshotAnnotation? _lastDraft;
    private int _lastDraftPointCount;
    private Point _lastDraftEnd;
    private PixelRect _committedInkBounds;
    private PixelRect _draftInkBounds;
    private PixelRect _visibleInkBounds;
    private long _marker;
    private PixelRect _markerBounds;
    private bool _hasState;
    private bool _disposed;

    /// <param name="background">已裁剪为当前显示器的冻结图，像素尺寸须与 viewport 一致。</param>
    /// <param name="viewport">当前显示器在整张截图内的局部物理像素范围。</param>
    internal ScreenshotGdiRenderer(BitmapSource background, Rect viewport)
    {
        _viewport = viewport;
        _canvas = new PixelRect(0, 0, background.PixelWidth, background.PixelHeight);
        if (viewport.Width != background.PixelWidth || viewport.Height != background.PixelHeight)
            throw new ArgumentException("Screenshot viewport and background dimensions must match.", nameof(viewport));

        byte[]? pixels = null;
        try
        {
            int stride = checked(background.PixelWidth * BytesPerPixel);
            pixels = new byte[checked(stride * background.PixelHeight)];
            BitmapSource source = background.Format == PixelFormats.Bgra32
                ? background : new FormatConvertedBitmap(background, PixelFormats.Bgra32, null, 0);
            source.CopyPixels(pixels, stride, 0);
            _original = new Dib(_canvas.Width, _canvas.Height, pixels);
            // 冻结图只在创建时暗化；普通选区拖动不会扫描或重新处理整屏像素。
            for (int index = 0; index < pixels.Length; index += BytesPerPixel)
            {
                pixels[index] = Dim(pixels[index]);
                pixels[index + 1] = Dim(pixels[index + 1]);
                pixels[index + 2] = Dim(pixels[index + 2]);
            }
            _dimmed = new Dib(_canvas.Width, _canvas.Height, pixels);
            _frame = new Dib(_canvas.Width, _canvas.Height);
            Copy(_frame.Dc, _dimmed.Dc, _canvas);
            _blackBrush = RequireHandle(CreateSolidBrush(0));
            _whiteBrush = RequireHandle(CreateSolidBrush(WhiteColor));
            AddPending(_canvas);
            AppLogger.Info($"{LogTag}: initialized; size={_canvas.Width}x{_canvas.Height}; buffers=original,dimmed,frame; backend=GDI.");
        }
        catch
        {
            Dispose();
            throw;
        }
        finally
        {
            if (pixels is not null) Array.Clear(pixels);
        }
    }

    internal void Update(ScreenshotOverlayWindow session, double dpi,
        ScreenshotPresentationProbe? probe = null, long marker = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        session.Dispatcher.VerifyAccess();
        _changes.Clear();
        Rect? highlight = session.Selection ?? session.Hover;
        PixelRect bright = VisibleRect(highlight);
        bool highlightChanged = _lastHighlight != highlight;
        bool selectionChanged = _lastSelection != session.Selection;
        bool decorationChanged = !_hasState || highlightChanged || _lastTool != session.Tool || _lastDpi != dpi;
        bool annotationsChanged = _annotationVersion != session.AnnotationVersion;
        ScreenshotAnnotation? draft = session.Draft;
        int draftPointCount = draft?.Points.Count ?? 0;
        Point draftEnd = draftPointCount > 0 ? draft!.Points[^1] : default;
        bool draftChanged = !ReferenceEquals(_lastDraft, draft) || _lastDraftPointCount != draftPointCount
            || _lastDraftEnd != draftEnd;

        if (!_hasState)
            AddChange(_canvas);
        else if (highlightChanged)
        {
            // 高亮内保持原图，外部保持暗图；只恢复新旧高亮的差集和装饰边缘。
            AddDifference(VisibleRect(_lastHighlight), bright);
            AddDifference(bright, VisibleRect(_lastHighlight));
        }
        if (decorationChanged)
        {
            AddDecorationBounds(_lastHighlight, _lastSelection is not null && _lastTool == ScreenshotTool.Adjust, _lastDpi);
            AddDecorationBounds(highlight, session.Selection is not null && session.Tool == ScreenshotTool.Adjust, dpi);
        }
        if (annotationsChanged)
        {
            _committedInkBounds = default;
            foreach (var annotation in session.Annotations)
                _committedInkBounds = PixelRect.Union(_committedInkBounds, AnnotationBounds(annotation));
        }
        if (draftChanged)
            _draftInkBounds = draft is null ? default : AnnotationBounds(draft);
        PixelRect visibleInk = PixelRect.Intersect(
            PixelRect.Union(_committedInkBounds, _draftInkBounds), VisibleRect(session.Selection));
        if (annotationsChanged || draftChanged || selectionChanged)
        {
            // 标注保持全桌面坐标；选区移动或缩放改变裁剪时同时恢复旧、新可见笔迹。
            AddChange(_visibleInkBounds);
            AddChange(visibleInk);
        }

        long nextMarker = probe is null ? 0 : marker;
        PixelRect nextMarkerBounds = default;
        if (nextMarker != 0)
        {
            // 跨屏诊断的宿主可覆盖整个桌面，条码仍必须落在 probe 所采集的那个显示器。
            Rect bounds = probe!.MarkerDrawingBounds;
            nextMarkerBounds = PixelRect.FromEdges(bounds.Left - _viewport.Left, bounds.Top - _viewport.Top,
                bounds.Right - _viewport.Left, bounds.Bottom - _viewport.Top);
        }
        if (_marker != nextMarker || _markerBounds != nextMarkerBounds)
        {
            AddChange(_markerBounds);
            AddChange(nextMarkerBounds);
        }
        bool redrawMarker = nextMarker != 0 && _changes.Any(rect => !PixelRect.Intersect(rect, nextMarkerBounds).Empty);
        if (redrawMarker)
            AddChange(nextMarkerBounds);

        if (_changes.Count > 0)
        {
            // 先恢复所有脏区，再用合并的 clip 只绘制一次，避免重叠脏区造成半透明笔迹叠加。
            foreach (var rect in _changes)
            {
                Copy(_frame!.Dc, _dimmed!.Dc, rect);
                Copy(_frame.Dc, _original!.Dc, PixelRect.Intersect(rect, bright));
            }
            FlushGdi();
            DrawOverlays(session, highlight, dpi);
            if (redrawMarker)
                DrawMarker(nextMarker, nextMarkerBounds);
            foreach (var rect in _changes)
                AddPending(rect);
        }

        _lastHighlight = highlight;
        _lastSelection = session.Selection;
        _lastTool = session.Tool;
        _lastDpi = dpi;
        _annotationVersion = session.AnnotationVersion;
        _lastDraft = draft;
        _lastDraftPointCount = draftPointCount;
        _lastDraftEnd = draftEnd;
        _visibleInkBounds = visibleInk;
        _marker = nextMarker;
        _markerBounds = nextMarkerBounds;
        _hasState = true;
    }

    /// <summary>输入更新后直接提交变化区域；不等待 DWM，也不将返回耗时解释为呈现延迟。</summary>
    internal void PaintTo(IntPtr hwnd)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (hwnd == IntPtr.Zero || _pendingPaint.Count == 0)
            return;
        IntPtr dc = RequireHandle(GetDC(hwnd));
        try
        {
            bool markerPending = false;
            foreach (var rect in _pendingPaint)
            {
                if (_marker == 0)
                    Copy(dc, _frame!.Dc, rect);
                else
                {
                    // 新序号最后提交，避免条码先于其对应的选区变化被桌面采集观察到。
                    foreach (var part in Difference(rect, MarkerBounds)) Copy(dc, _frame!.Dc, part);
                    markerPending |= !PixelRect.Intersect(rect, MarkerBounds).Empty;
                }
            }
            if (markerPending) Copy(dc, _frame!.Dc, MarkerBounds);
            FlushGdi();
            _pendingPaint.Clear();
        }
        finally
        {
            ReleaseDC(hwnd, dc);
        }
    }

    /// <summary>WM_PAINT 恢复保留帧；目标 HDC 自带更新区域裁剪，不消费其他尚未提交的脏区。</summary>
    internal void PaintToHdc(IntPtr targetHdc)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (targetHdc == IntPtr.Zero) return;
        Copy(targetHdc, _frame!.Dc, _canvas);
        FlushGdi();
    }

    private void DrawOverlays(ScreenshotOverlayWindow session, Rect? highlight, double dpi)
    {
        using var dirtyClip = new Drawing.Region();
        dirtyClip.MakeEmpty();
        foreach (var rect in _changes) dirtyClip.Union(rect.ToDrawing());
        using var graphics = Drawing.Graphics.FromHdc(_frame!.Dc);
        graphics.PageUnit = Drawing.GraphicsUnit.Pixel;
        graphics.SmoothingMode = Drawing2D.SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = Drawing2D.PixelOffsetMode.Half;
        graphics.SetClip(dirtyClip, Drawing2D.CombineMode.Replace);
        if (session.Selection is { IsEmpty: false } selection)
        {
            var saved = graphics.Save();
            try
            {
                graphics.SetClip(LocalRect(selection), Drawing2D.CombineMode.Intersect);
                foreach (var annotation in session.Annotations) DrawAnnotation(graphics, annotation);
                if (session.Draft is { } draft) DrawAnnotation(graphics, draft);
            }
            finally
            {
                graphics.Restore(saved);
            }
        }
        if (highlight is { IsEmpty: false } bounds)
        {
            using var border = new Drawing.Pen(BorderColor, (float)(SelectionBorderWidth * dpi));
            var rectangle = LocalRect(bounds);
            graphics.DrawRectangle(border, rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height);
            if (session.Selection is not null && session.Tool == ScreenshotTool.Adjust)
            {
                float radius = (float)(SelectionHandleRadius * dpi);
                DrawHandle(graphics, border, rectangle.Left, rectangle.Top, radius);
                DrawHandle(graphics, border, rectangle.Right, rectangle.Top, radius);
                DrawHandle(graphics, border, rectangle.Left, rectangle.Bottom, radius);
                DrawHandle(graphics, border, rectangle.Right, rectangle.Bottom, radius);
                DrawHandle(graphics, border, rectangle.Left + rectangle.Width / 2, rectangle.Top, radius);
                DrawHandle(graphics, border, rectangle.Left + rectangle.Width / 2, rectangle.Bottom, radius);
                DrawHandle(graphics, border, rectangle.Left, rectangle.Top + rectangle.Height / 2, radius);
                DrawHandle(graphics, border, rectangle.Right, rectangle.Top + rectangle.Height / 2, radius);
            }
        }
        graphics.Flush(Drawing2D.FlushIntention.Sync);
    }

    private void DrawAnnotation(Drawing.Graphics graphics, ScreenshotAnnotation annotation)
    {
        if (annotation.Points.Count == 0) return;
        var color = annotation.Color;
        using var brush = new Drawing.SolidBrush(Drawing.Color.FromArgb(color.A, color.R, color.G, color.B));
        using var pen = new Drawing.Pen(brush, (float)annotation.Width)
        {
            StartCap = Drawing2D.LineCap.Round,
            EndCap = Drawing2D.LineCap.Round,
            LineJoin = Drawing2D.LineJoin.Round
        };
        if (annotation.Rectangle && annotation.Points.Count > 1)
        {
            var rect = LocalRect(new Rect(annotation.Points[0], annotation.Points[^1]));
            graphics.DrawRectangle(pen, rect.X, rect.Y, rect.Width, rect.Height);
        }
        else if (annotation.Points.Count == 1)
        {
            var point = LocalPoint(annotation.Points[0]);
            float radius = (float)(annotation.Width / 2);
            graphics.FillEllipse(brush, point.X - radius, point.Y - radius, radius * 2, radius * 2);
        }
        else
        {
            var points = new Drawing.PointF[annotation.Points.Count];
            for (int index = 0; index < points.Length; index++) points[index] = LocalPoint(annotation.Points[index]);
            graphics.DrawLines(pen, points);
        }
    }

    private static void DrawHandle(Drawing.Graphics graphics, Drawing.Pen border, float x, float y, float radius)
    {
        graphics.FillRectangle(Drawing.Brushes.White, x - radius, y - radius, radius * 2, radius * 2);
        graphics.DrawRectangle(border, x - radius, y - radius, radius * 2, radius * 2);
    }

    private void DrawMarker(long marker, PixelRect bounds)
    {
        Fill(bounds, _blackBrush);
        ulong code = unchecked((ulong)marker);
        for (int bit = 0; bit < ScreenshotPresentationProbe.MarkerBitCount; bit++)
        {
            if (((code >> (ScreenshotPresentationProbe.MarkerBitCount - bit - 1)) & 1) == 0) continue;
            int left = bounds.Left + bit * ScreenshotPresentationProbe.MarkerCellWidth;
            Fill(new PixelRect(left, bounds.Top,
                left + ScreenshotPresentationProbe.MarkerCellWidth,
                bounds.Top + ScreenshotPresentationProbe.MarkerHeight), _whiteBrush);
        }
    }

    private PixelRect MarkerBounds => PixelRect.Intersect(_canvas, _markerBounds);

    private void AddDecorationBounds(Rect? bounds, bool handles, double dpi)
    {
        if (bounds is not { IsEmpty: false } rect) return;
        double extent = (SelectionBorderWidth / 2 + (handles ? SelectionHandleRadius : 0)) * dpi + AntialiasPadding;
        rect.Offset(-_viewport.X, -_viewport.Y);
        AddChange(PixelRect.FromEdges(rect.Left - extent, rect.Top - extent, rect.Right + extent, rect.Top + extent));
        AddChange(PixelRect.FromEdges(rect.Left - extent, rect.Bottom - extent, rect.Right + extent, rect.Bottom + extent));
        AddChange(PixelRect.FromEdges(rect.Left - extent, rect.Top - extent, rect.Left + extent, rect.Bottom + extent));
        AddChange(PixelRect.FromEdges(rect.Right - extent, rect.Top - extent, rect.Right + extent, rect.Bottom + extent));
    }

    private PixelRect AnnotationBounds(ScreenshotAnnotation annotation)
    {
        if (annotation.Points.Count == 0) return default;
        double left = double.PositiveInfinity, top = double.PositiveInfinity;
        double right = double.NegativeInfinity, bottom = double.NegativeInfinity;
        foreach (Point point in annotation.Points)
        {
            left = Math.Min(left, point.X); top = Math.Min(top, point.Y);
            right = Math.Max(right, point.X); bottom = Math.Max(bottom, point.Y);
        }
        double extent = annotation.Width / 2 + AntialiasPadding;
        return PixelRect.Intersect(_canvas, PixelRect.FromEdges(left - _viewport.X - extent, top - _viewport.Y - extent,
            right - _viewport.X + extent, bottom - _viewport.Y + extent));
    }

    private PixelRect VisibleRect(Rect? bounds)
    {
        if (bounds is not { IsEmpty: false } rect || rect.Width <= 0 || rect.Height <= 0) return default;
        return PixelRect.Intersect(_canvas, PixelRect.FromEdges(rect.Left - _viewport.X, rect.Top - _viewport.Y,
            rect.Right - _viewport.X, rect.Bottom - _viewport.Y));
    }

    private Drawing.RectangleF LocalRect(Rect rect) => new((float)(rect.X - _viewport.X), (float)(rect.Y - _viewport.Y),
        (float)rect.Width, (float)rect.Height);
    private Drawing.PointF LocalPoint(Point point) => new((float)(point.X - _viewport.X), (float)(point.Y - _viewport.Y));

    private void AddDifference(PixelRect source, PixelRect cut)
    {
        foreach (var rect in Difference(source, cut)) AddChange(rect);
    }

    private static IEnumerable<PixelRect> Difference(PixelRect source, PixelRect cut)
    {
        if (source.Empty) yield break;
        var overlap = PixelRect.Intersect(source, cut);
        if (overlap.Empty) { yield return source; yield break; }
        yield return new PixelRect(source.Left, source.Top, source.Right, overlap.Top);
        yield return new PixelRect(source.Left, overlap.Bottom, source.Right, source.Bottom);
        yield return new PixelRect(source.Left, overlap.Top, overlap.Left, overlap.Bottom);
        yield return new PixelRect(overlap.Right, overlap.Top, source.Right, overlap.Bottom);
    }

    private void AddChange(PixelRect rect) => AddBounded(_changes, PixelRect.Intersect(_canvas, rect));
    private void AddPending(PixelRect rect) => AddBounded(_pendingPaint, PixelRect.Intersect(_canvas, rect));

    private static void AddBounded(List<PixelRect> rectangles, PixelRect rect)
    {
        if (rect.Empty) return;
        for (int index = rectangles.Count - 1; index >= 0; index--)
        {
            if (rectangles[index].Contains(rect)) return;
            if (rect.Contains(rectangles[index])) rectangles.RemoveAt(index);
        }
        if (rectangles.Count >= MaximumDirtyRectangles)
        {
            // 窗口未建好或暂时没有提交时脏区可能累积；有限合并避免队列随输入无限增长。
            foreach (var pending in rectangles) rect = PixelRect.Union(rect, pending);
            rectangles.Clear();
        }
        rectangles.Add(rect);
    }

    private void Fill(PixelRect rect, IntPtr brush)
    {
        rect = PixelRect.Intersect(_canvas, rect);
        if (rect.Empty) return;
        var native = rect.ToNative();
        if (FillRect(_frame!.Dc, ref native, brush) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _frame?.Dispose();
        _dimmed?.Dispose();
        _original?.Dispose();
        if (_blackBrush != IntPtr.Zero) NativeMethods.DeleteObject(_blackBrush);
        if (_whiteBrush != IntPtr.Zero) NativeMethods.DeleteObject(_whiteBrush);
        _blackBrush = _whiteBrush = IntPtr.Zero;
    }

    private sealed class Dib : IDisposable
    {
        private IntPtr _bitmap;
        private IntPtr _previous;
        internal IntPtr Dc { get; private set; }

        internal Dib(int width, int height, byte[]? pixels = null)
        {
            try
            {
                Dc = RequireHandle(CreateCompatibleDC(IntPtr.Zero));
                var header = new BitmapHeader
                {
                    Size = (uint)Marshal.SizeOf<BitmapHeader>(), Width = width, Height = -height,
                    Planes = 1, BitCount = BitmapBitCount
                };
                _bitmap = RequireHandle(CreateDIBSection(Dc, ref header, 0, out var bits, IntPtr.Zero, 0));
                _previous = RequireHandle(SelectObject(Dc, _bitmap));
                if (pixels is not null) Marshal.Copy(pixels, 0, bits, pixels.Length);
            }
            catch { Dispose(); throw; }
        }

        public void Dispose()
        {
            if (Dc != IntPtr.Zero && _previous != IntPtr.Zero) SelectObject(Dc, _previous);
            if (_bitmap != IntPtr.Zero) NativeMethods.DeleteObject(_bitmap);
            if (Dc != IntPtr.Zero) DeleteDC(Dc);
            Dc = _bitmap = _previous = IntPtr.Zero;
        }
    }

    private readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
    {
        internal int Width => Right - Left;
        internal int Height => Bottom - Top;
        internal bool Empty => Width <= 0 || Height <= 0;
        internal bool Contains(PixelRect other) => Left <= other.Left && Top <= other.Top && Right >= other.Right && Bottom >= other.Bottom;
        internal Drawing.Rectangle ToDrawing() => new(Left, Top, Width, Height);
        internal NativeMethods.RECT ToNative() => new() { Left = Left, Top = Top, Right = Right, Bottom = Bottom };
        internal static PixelRect FromEdges(double left, double top, double right, double bottom)
            => new((int)Math.Floor(left), (int)Math.Floor(top), (int)Math.Ceiling(right), (int)Math.Ceiling(bottom));
        internal static PixelRect Intersect(PixelRect first, PixelRect second) => new(
            Math.Max(first.Left, second.Left), Math.Max(first.Top, second.Top),
            Math.Min(first.Right, second.Right), Math.Min(first.Bottom, second.Bottom));
        internal static PixelRect Union(PixelRect first, PixelRect second) => first.Empty ? second : second.Empty ? first : new(
            Math.Min(first.Left, second.Left), Math.Min(first.Top, second.Top),
            Math.Max(first.Right, second.Right), Math.Max(first.Bottom, second.Bottom));
    }

    private static byte Dim(byte value) => (byte)(value * (byte.MaxValue - DimmingAlpha) / byte.MaxValue);
    private static IntPtr RequireHandle(IntPtr handle)
        => handle == IntPtr.Zero || handle == new IntPtr(-1) ? throw new Win32Exception(Marshal.GetLastWin32Error()) : handle;
    private static void Copy(IntPtr destination, IntPtr source, PixelRect rect)
    {
        if (!rect.Empty && !BitBlt(destination, rect.Left, rect.Top, rect.Width, rect.Height,
                source, rect.Left, rect.Top, SourceCopy))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    private static void FlushGdi()
    {
        if (!GdiFlush()) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapHeader
    {
        internal uint Size;
        internal int Width, Height;
        internal ushort Planes, BitCount;
        internal uint Compression, ImageSize;
        internal int XPixelsPerMeter, YPixelsPerMeter;
        internal uint ColorsUsed, ColorsImportant;
    }

    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapHeader header, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateSolidBrush(uint color);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool BitBlt(IntPtr destination, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, uint operation);
    [DllImport("gdi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GdiFlush();
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("user32.dll", SetLastError = true)] private static extern int FillRect(IntPtr dc, ref NativeMethods.RECT rect, IntPtr brush);
}
