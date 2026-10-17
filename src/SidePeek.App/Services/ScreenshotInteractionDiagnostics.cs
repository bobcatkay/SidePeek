using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using SidePeek.App.Views;

namespace SidePeek.App.Services;

/// <summary>显式诊断入口：同步走真实手势状态机，仅在内存中验证结果，不注入系统鼠标或导出文件。</summary>
internal static class ScreenshotInteractionDiagnostics
{
    private const string LogTag = nameof(ScreenshotInteractionDiagnostics);
    private const double MinimumViewportExtent = 256;
    private const double SelectionStartFraction = 0.2;
    private const double SelectionEndFraction = 0.7;
    private const double IntermediateFraction = 0.5;
    private const double MoveFraction = 0.08;
    private const double ResizeFraction = 0.05;
    private const double DesktopStartFraction = 0.1;
    private const double DesktopEndFraction = 0.9;
    private const double StrokeStartFraction = 0.25;
    private const double StrokeMiddleFraction = 0.5;
    private const double StrokeEndFraction = 0.75;
    private const double RectangleBottomFraction = 0.4;
    private const double DiagnosticInkWidth = 8;
    private const int PenPointCount = 3;
    private const int PixelByteCount = 4;
    private const int AlphaChannelIndex = 3;
    private const int ColorChannelTolerance = 1;
    private static readonly Color DiagnosticInkColor = Colors.Magenta;
    private static readonly ConditionalWeakTable<ScreenshotOverlayWindow, RunState> Runs = new();

    internal static void Run(ScreenshotOverlayWindow session, ScreenshotSurface surface, Rect replayViewport)
    {
        if (!App.ScreenshotDiagnosticsEnabled || !ScreenshotDiagnostics.InteractionRegression)
            return;
        session.Dispatcher.VerifyAccess();
        surface.Dispatcher.VerifyAccess();
        var state = Runs.GetValue(session, static _ => new RunState());
        if (state.Started)
            return;
        state.Started = true;

        Require(!surface.IsGestureActive && session.Annotations.Count == 0 && session.Selection is null,
            "requires-fresh-session");
        Require(replayViewport.Width >= MinimumViewportExtent && replayViewport.Height >= MinimumViewportExtent,
            "viewport-too-small");

        int passed = 0, skipped = 0;
        Color previousColor = session.InkColor;
        double previousWidth = session.InkWidth;
        var selection = new Rect(At(replayViewport, SelectionStartFraction, SelectionStartFraction),
            At(replayViewport, SelectionEndFraction, SelectionEndFraction));
        var movement = new Vector(Math.Floor(replayViewport.Width * MoveFraction),
            Math.Floor(replayViewport.Height * MoveFraction));
        var expansion = new Vector(Math.Floor(replayViewport.Width * ResizeFraction),
            Math.Floor(replayViewport.Height * ResizeFraction));

        // 不 await、不泵消息；原生 capture/松手仍由生产路径执行，外部输入只能等本组场景结束后派发。
        using (session.Dispatcher.DisableProcessing())
        {
            try
            {
                Check("area-forward", () =>
                {
                    SelectArea(session, surface, selection.TopLeft, selection.BottomRight);
                    ExpectSelection(session, selection);
                });
                Check("area-reverse", () =>
                {
                    SelectArea(session, surface, selection.BottomRight, selection.TopLeft);
                    ExpectSelection(session, selection);
                });
                Check("area-click-empty", () =>
                {
                    session.SetTool(ScreenshotTool.Area);
                    Drag(session, surface, selection.TopLeft, selection.TopLeft, selection.TopLeft);
                    Require(session.Selection is null && !session.CanExport, "empty-click-created-selection");
                });
                Check("mouseup-final-position", () =>
                {
                    session.SetTool(ScreenshotTool.Area);
                    Drag(session, surface, selection.TopLeft, Center(selection), selection.BottomRight);
                    ExpectSelection(session, selection);
                });

                if (TryWindowPoint(session, replayViewport, out Point windowPoint, out Rect windowSelection))
                {
                    Check("window-click-snap", () =>
                    {
                        session.SetTool(ScreenshotTool.Window);
                        surface.NativePointerMove(windowPoint, Environment.TickCount);
                        Require(session.Hover == windowSelection, "window-hover-mismatch");
                        Drag(session, surface, windowPoint, windowPoint, windowPoint);
                        ExpectSelection(session, windowSelection);
                    });
                }
                else
                {
                    skipped++;
                    AppLogger.Info($"{LogTag}: scenario=window-click-snap; result=skipped; reason=no-captured-window.");
                }

                Check("adjust-move", () =>
                {
                    SelectArea(session, surface, selection.TopLeft, selection.BottomRight);
                    Point center = Center(selection);
                    Drag(session, surface, center, center + movement, center + movement);
                    Rect moved = selection;
                    moved.Offset(movement);
                    ExpectSelection(session, moved);
                });
                Check("adjust-top-left", () => Resize(selection.TopLeft, selection.TopLeft - expansion,
                    new Rect(selection.TopLeft - expansion, selection.BottomRight)));
                Check("adjust-bottom-right", () => Resize(selection.BottomRight, selection.BottomRight + expansion,
                    new Rect(selection.TopLeft, selection.BottomRight + expansion)));
                Check("adjust-top-right", () =>
                {
                    Point end = new(selection.Right + expansion.X, selection.Top - expansion.Y);
                    Resize(selection.TopRight, end,
                        new Rect(new Point(selection.Left, end.Y), new Point(end.X, selection.Bottom)));
                });
                Check("adjust-bottom-left", () =>
                {
                    Point end = new(selection.Left - expansion.X, selection.Bottom + expansion.Y);
                    Resize(selection.BottomLeft, end,
                        new Rect(new Point(end.X, selection.Top), new Point(selection.Right, end.Y)));
                });
                Check("cancel-adjust-rollback", () =>
                {
                    SelectArea(session, surface, selection.TopLeft, selection.BottomRight);
                    Point center = Center(selection);
                    Begin(session, surface, center);
                    surface.NativePointerMove(center + movement, Environment.TickCount);
                    Require(session.Selection != selection, "adjust-did-not-change");
                    surface.CancelGesture();
                    ExpectReleased(surface);
                    ExpectSelection(session, selection);
                });
                Check("cancel-area-rollback", () =>
                {
                    session.SetTool(ScreenshotTool.Area);
                    Begin(session, surface, selection.TopLeft);
                    surface.NativePointerMove(selection.BottomRight, Environment.TickCount);
                    Require(session.Selection is { IsEmpty: false }, "area-did-not-change");
                    surface.CancelGesture();
                    ExpectReleased(surface);
                    Require(session.Selection is null && !session.CanExport, "cancelled-area-survived");
                });

                session.InkColor = DiagnosticInkColor;
                session.InkWidth = DiagnosticInkWidth;
                Point penStart = At(selection, StrokeStartFraction, StrokeMiddleFraction);
                Point penMiddle = At(selection, StrokeMiddleFraction, StrokeMiddleFraction);
                Point penEnd = At(selection, StrokeEndFraction, StrokeMiddleFraction);
                ScreenshotAnnotation? pen = null;
                Check("pen-commit", () =>
                {
                    SelectArea(session, surface, selection.TopLeft, selection.BottomRight);
                    PressKey(session, Key.P);
                    Require(session.Tool == ScreenshotTool.Pen, "pen-shortcut-failed");
                    Drag(session, surface, penStart, penMiddle, penEnd);
                    Require(session.Draft is null && session.Annotations.Count == 1 && session.CanUndo,
                        "pen-commit-count");
                    pen = session.Annotations[0];
                    Require(!pen.Rectangle && pen.Points.Count == PenPointCount
                        && pen.Points[0] == penStart && pen.Points[1] == penMiddle && pen.Points[^1] == penEnd,
                        "pen-points-mismatch");
                    Require(pen.Color == DiagnosticInkColor && pen.Width == DiagnosticInkWidth,
                        "pen-style-mismatch");
                });
                Check("annotation-undo-redo", () =>
                {
                    PressKey(session, Key.Z, control: true);
                    Require(session.Annotations.Count == 0 && !session.CanUndo && session.CanRedo,
                        "undo-failed");
                    session.Redo();
                    Require(session.Annotations.Count == 1 && ReferenceEquals(session.Annotations[0], pen)
                        && session.CanUndo && !session.CanRedo, "redo-failed");
                });
                Check("rectangle-commit", () =>
                {
                    PressKey(session, Key.R);
                    Require(session.Tool == ScreenshotTool.Rectangle, "rectangle-shortcut-failed");
                    Point start = At(selection, StrokeStartFraction, StrokeStartFraction);
                    Point end = At(selection, StrokeEndFraction, RectangleBottomFraction);
                    Drag(session, surface, start, Center(new Rect(start, end)), end);
                    Require(session.Draft is null && session.Annotations.Count == 2, "rectangle-commit-count");
                    var rectangle = session.Annotations[^1];
                    Require(rectangle.Rectangle && rectangle.Points.Count == 2
                        && rectangle.Points[0] == start && rectangle.Points[^1] == end,
                        "rectangle-points-mismatch");
                });
                Check("cancel-annotation", () =>
                {
                    PressKey(session, Key.P);
                    Begin(session, surface, penStart);
                    surface.NativePointerMove(penMiddle, Environment.TickCount);
                    Require(session.Draft is not null, "annotation-draft-missing");
                    surface.CancelGesture();
                    ExpectReleased(surface);
                    Require(session.Draft is null && session.Annotations.Count == 2, "cancelled-annotation-committed");
                });
                Check("export-size-and-ink", () =>
                {
                    PressKey(session, Key.V);
                    Require(session.Tool == ScreenshotTool.Adjust, "adjust-shortcut-failed");
                    ExpectSelection(session, selection);
                    var image = session.RenderSelection();
                    Require(image.PixelWidth == (int)selection.Width && image.PixelHeight == (int)selection.Height,
                        "export-size-mismatch");
                    Require(image.Format == PixelFormats.Pbgra32, "export-format-mismatch");
                    var pixel = new byte[PixelByteCount];
                    try
                    {
                        image.CopyPixels(new Int32Rect((int)(penMiddle.X - selection.X),
                            (int)(penMiddle.Y - selection.Y), 1, 1), pixel, PixelByteCount, 0);
                        Require(Math.Abs(pixel[0] - DiagnosticInkColor.B) <= ColorChannelTolerance
                            && Math.Abs(pixel[1] - DiagnosticInkColor.G) <= ColorChannelTolerance
                            && Math.Abs(pixel[2] - DiagnosticInkColor.R) <= ColorChannelTolerance
                            && pixel[AlphaChannelIndex] == byte.MaxValue, "export-ink-pixel-mismatch");
                    }
                    finally { Array.Clear(pixel); }
                });
                Check("area-shortcut-clears-annotations", () =>
                {
                    PressKey(session, Key.A);
                    Require(session.Tool == ScreenshotTool.Area && session.Selection is null
                        && session.Annotations.Count == 0 && !session.CanRedo, "area-shortcut-failed");
                });

                // 虚拟桌面坐标包含负屏幕起点的平移，也允许显示器之间有空隙，不假设左右排列。
                var desktop = new Rect(0, 0, session.Capture.Image.PixelWidth, session.Capture.Image.PixelHeight);
                Point desktopStart = At(desktop, DesktopStartFraction, DesktopStartFraction);
                Point desktopEnd = At(desktop, DesktopEndFraction, DesktopEndFraction);
                Check("virtual-desktop-forward", () =>
                {
                    SelectArea(session, surface, desktopStart, desktopEnd);
                    ExpectSelection(session, new Rect(desktopStart, desktopEnd));
                });
                Check("virtual-desktop-reverse", () =>
                {
                    SelectArea(session, surface, desktopEnd, desktopStart);
                    ExpectSelection(session, new Rect(desktopStart, desktopEnd));
                });
                AppLogger.Info($"{LogTag}: completed; passed={passed}; skipped={skipped}; systemMouseInjected=false; filesWritten=false; clipboardWritten=false.");
            }
            finally
            {
                surface.CancelGesture();
                session.InkColor = previousColor;
                session.InkWidth = previousWidth;
                session.SetTool(ScreenshotTool.Window);
            }
        }

        void Check(string name, Action scenario)
        {
            try { scenario(); }
            catch (Exception error)
            {
                throw new InvalidOperationException($"{LogTag}: scenario={name} failed.", error);
            }
            passed++;
            AppLogger.Info($"{LogTag}: scenario={name}; result=passed.");
        }

        void Resize(Point start, Point end, Rect expected)
        {
            SelectArea(session, surface, selection.TopLeft, selection.BottomRight);
            Drag(session, surface, start, end, end);
            ExpectSelection(session, expected);
        }
    }

    private static void SelectArea(ScreenshotOverlayWindow session, ScreenshotSurface surface, Point start, Point end)
    {
        session.SetTool(ScreenshotTool.Area);
        Drag(session, surface, start, end, end);
    }

    private static void Drag(ScreenshotOverlayWindow session, ScreenshotSurface surface, Point start, Point move, Point end)
    {
        Begin(session, surface, start);
        surface.NativePointerMove(move, Environment.TickCount);
        surface.NativePointerUp(end, Environment.TickCount);
        ExpectReleased(surface);
    }

    private static void Begin(ScreenshotOverlayWindow session, ScreenshotSurface surface, Point point)
    {
        surface.NativePointerDown(point, Environment.TickCount);
        Require(surface.IsGestureActive && !session.CanExport, "gesture-capture-or-export-guard-failed");
    }

    private static void ExpectReleased(ScreenshotSurface surface)
        => Require(!surface.IsGestureActive, "gesture-capture-not-released");

    private static void ExpectSelection(ScreenshotOverlayWindow session, Rect expected)
        => Require(session.Selection == expected && session.CanExport, "selection-or-export-state-mismatch");

    private static void PressKey(ScreenshotOverlayWindow session, Key key, bool control = false)
        => Require(session.HandleNativeKey(KeyInterop.VirtualKeyFromKey(key), control, shift: false, alt: false),
            "shortcut-not-handled");

    private static Point At(Rect rect, double xFraction, double yFraction)
        => new(Math.Floor(rect.Left + rect.Width * xFraction), Math.Floor(rect.Top + rect.Height * yFraction));

    private static Point Center(Rect rect) => At(rect, IntermediateFraction, IntermediateFraction);

    private static bool TryWindowPoint(ScreenshotOverlayWindow session, Rect viewport, out Point point, out Rect expected)
    {
        Rect screenViewport = viewport;
        screenViewport.Offset(session.Capture.Bounds.X, session.Capture.Bounds.Y);
        foreach (Rect window in session.Capture.Windows)
        {
            Rect visible = Rect.Intersect(window, screenViewport);
            if (visible.IsEmpty || visible.Width < 1 || visible.Height < 1)
                continue;
            Point screenPoint = Center(visible);
            // 捕获列表按 Z 序保存；有重叠时采用该点最上层窗口，避免把遮挡关系误报成吸附错误。
            Rect topWindow = session.Capture.Windows.First(candidate => candidate.Contains(screenPoint));
            expected = Rect.Intersect(topWindow, session.Capture.Bounds);
            expected.Offset(-session.Capture.Bounds.X, -session.Capture.Bounds.Y);
            point = new Point(screenPoint.X - session.Capture.Bounds.X, screenPoint.Y - session.Capture.Bounds.Y);
            return true;
        }
        point = default;
        expected = Rect.Empty;
        return false;
    }

    private static void Require(bool condition, string reason)
    {
        if (!condition)
            throw new InvalidOperationException($"{LogTag}: {reason}.");
    }

    private sealed class RunState { internal bool Started; }
}
