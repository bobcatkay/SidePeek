using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using SidePeek.App.Interop;
using SidePeek.App.Models;
using SidePeek.App.Services;

namespace SidePeek.App.Docking;

/// <summary>
/// 负责把窗口吸附到屏幕某一边，并实现「悬停展开 / 移开自动收起」。
/// 收起态：仅在停靠边中部露出一小段（屏幕长边的 10%、垂直/水平居中）的触发块。
/// 展开态：沿停靠边铺满工作区。展开/收起同时对位置与尺寸做缓动动画。
/// 同屏前台全屏或截图期间隐藏整个窗口，退出后恢复此前的显示状态。
/// </summary>
public sealed class DockManager
{
    private const double PanelThickness = 420;   // 左右停靠=宽度；上下停靠=高度
    private const double TriggerStrip = 6;        // 收起时露出的厚度（像素）
    private const double CollapseRatio = 0.10;    // 收起时沿边长度占比
    private const double AnimationMs = 220;
    private static readonly TimeSpan DisplayRefreshInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan FullscreenCheckInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan FullscreenRestoreDelay = TimeSpan.FromMilliseconds(500);

    private readonly Window _window;
    private readonly IDockViewport? _viewport;
    private readonly DispatcherTimer _pollTimer;
    private readonly DispatcherTimer _animTimer;
    private readonly Stopwatch _animClock = new();
    private readonly Stopwatch _displayRefreshClock = Stopwatch.StartNew();
    private readonly Stopwatch _fullscreenCheckClock = new();
    private readonly Stopwatch _fullscreenExitClock = new();
    private readonly FullscreenWindowDetector _fullscreenDetector = new();
    private DispatcherOperation? _placementRefresh;
    private Matrix _layoutTransform = Matrix.Identity;
    private IntPtr _displayMonitor;

    private DockEdge _edge = DockEdge.Right;
    private string _displayDeviceName = string.Empty;
    private DockState _state = DockState.Hidden;
    private DateTime? _insideTriggerSince;
    private DateTime? _outsideSince;
    private bool _suspended;
    private bool _screenshotSuspended;
    private bool _fullscreenHidden;
    private bool _restoreVisibility;
    private bool _topmostPromotionFailureLogged;
    private bool _displayReadFailureLogged;

    private Rect _expandedRect;
    private Rect _collapsedRect;
    private Rect _triggerRect;
    private Rect _workArea;

    private Rect _fromRect;
    private Rect _toRect;

    public DockManager(Window window)
    {
        _window = window;
        _viewport = window as IDockViewport;
        _pollTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(25) };
        _pollTimer.Tick += Poll;

        _animTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(15) };
        _animTimer.Tick += AnimTick;
    }

    public DockEdge Edge => _edge;
    public string DisplayDeviceName => _displayDeviceName;
    public DockState State => _state;
    public int ExpandDelayMs { get; set; } = AppSettings.DefaultExpandDelayMs;
    public int CollapseDelayMs { get; set; } = AppSettings.DefaultCollapseDelayMs;
    public bool IsPinned { get; set; }
    public event EventHandler<DockEdge>? EdgeChanged;

    public void Start(DockEdge edge, string displayDeviceName, bool startExpanded = false)
    {
        SetPlacement(edge, displayDeviceName, startExpanded);
        _pollTimer.Start();
    }

    public void Stop()
    {
        _pollTimer.Stop();
        _animTimer.Stop();
        _placementRefresh?.Abort();
        _placementRefresh = null;
    }

    public void QueuePlacementRefresh()
    {
        if (!_pollTimer.IsEnabled || _placementRefresh is not null)
            return;

        // WPF must finish handling WM_DPICHANGED before we read its new DIP transform.
        // Stop the old animation now so it cannot reapply pre-change coordinates.
        _animTimer.Stop();
        _placementRefresh = _window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _placementRefresh = null;
            RefreshPlacement(force: true);
        }));
    }

    private void RefreshPlacement(bool force = false)
    {
        _displayRefreshClock.Restart();
        IntPtr previousMonitor = _displayMonitor;
        Rect workArea = GetWorkArea();
        Matrix transform = DpiTransform;
        if (!force && workArea == _workArea && transform == _layoutTransform && previousMonitor == _displayMonitor)
            return;

        Layout(workArea);
        _insideTriggerSince = null;
        _outsideSince = null;
        ApplyRect(_state == DockState.Expanded ? _expandedRect : _collapsedRect, animate: false);
        RefreshFullscreenVisibility(force: true);
        AppLogger.Info($"[DockManager] Display geometry refreshed: edge={_edge}, state={_state}, workArea={_workArea}, deviceToDip={_layoutTransform}.");
    }

    /// <summary>交互或命令执行期间暂停悬停展开/收起，显示器和全屏检测继续运行。</summary>
    public void Suspend()
    {
        _suspended = true;
        _insideTriggerSince = null;
    }

    public void Resume() => _suspended = false;

    public void SuspendForScreenshot()
    {
        _screenshotSuspended = true;
        _fullscreenExitClock.Reset();
        _insideTriggerSince = null;
        _outsideSince = null;
        UpdateWindowVisibility();
    }

    public void ResumeAfterScreenshot()
    {
        _screenshotSuspended = false;
        _insideTriggerSince = null;
        _outsideSince = null;
        RefreshPlacement();
        RefreshFullscreenVisibility(force: true);
        UpdateWindowVisibility();
    }

    public void SetEdge(DockEdge edge, bool expanded = false)
        => SetPlacement(edge, _displayDeviceName, expanded);

    public void SetPlacement(DockEdge edge, string displayDeviceName, bool expanded = false)
    {
        bool edgeChanged = _edge != edge;
        _edge = edge;
        _displayDeviceName = displayDeviceName ?? string.Empty;
        Layout(GetWorkArea());
        _insideTriggerSince = null;
        _outsideSince = null;
        _state = expanded ? DockState.Expanded : DockState.Hidden;
        ApplyRect(expanded ? _expandedRect : _collapsedRect, animate: false);
        // SourceInitialized also sets placement; wait until the initial Show has completed before hiding.
        if (_window.IsLoaded)
            RefreshFullscreenVisibility(force: true);
        if (expanded && !_fullscreenHidden && !_screenshotSuspended)
            PromoteToTopmost();
        if (edgeChanged)
            EdgeChanged?.Invoke(this, edge);
    }

    public void MoveToEdge(DockEdge edge)
    {
        SetEdge(edge);
        Expand();
    }

    /// <summary>展开/收起切换（供托盘、全局热键调用）。</summary>
    public void Toggle()
    {
        RefreshFullscreenVisibility(force: true);
        if (_fullscreenHidden || _screenshotSuspended)
            return;

        if (_state == DockState.Expanded)
            Collapse();
        else
            Expand();
    }

    public void Reveal() => Expand();

    private void Layout(Rect workArea)
    {
        _workArea = workArea;
        _layoutTransform = DpiTransform;
        Rect wa = _workArea;

        switch (_edge)
        {
            case DockEdge.Left:
            {
                double ch = wa.Height * CollapseRatio;
                double cy = wa.Top + (wa.Height - ch) / 2;
                _expandedRect = new Rect(wa.Left, wa.Top, PanelThickness, wa.Height);
                _triggerRect = new Rect(wa.Left, cy, TriggerStrip, ch);
                _collapsedRect = _triggerRect;
                break;
            }
            case DockEdge.Right:
            {
                double ch = wa.Height * CollapseRatio;
                double cy = wa.Top + (wa.Height - ch) / 2;
                _expandedRect = new Rect(wa.Right - PanelThickness, wa.Top, PanelThickness, wa.Height);
                _triggerRect = new Rect(wa.Right - TriggerStrip, cy, TriggerStrip, ch);
                _collapsedRect = _triggerRect;
                break;
            }
            case DockEdge.Top:
            {
                double cw = wa.Width * CollapseRatio;
                double cx = wa.Left + (wa.Width - cw) / 2;
                _expandedRect = new Rect(wa.Left, wa.Top, wa.Width, PanelThickness);
                _triggerRect = new Rect(cx, wa.Top, cw, TriggerStrip);
                _collapsedRect = _triggerRect;
                break;
            }
            case DockEdge.Bottom:
            {
                double cw = wa.Width * CollapseRatio;
                double cx = wa.Left + (wa.Width - cw) / 2;
                _expandedRect = new Rect(wa.Left, wa.Bottom - PanelThickness, wa.Width, PanelThickness);
                _triggerRect = new Rect(cx, wa.Bottom - TriggerStrip, cw, TriggerStrip);
                _collapsedRect = _triggerRect;
                break;
            }
        }
    }

    private void Poll(object? sender, EventArgs e)
    {
        // 截图共用 UI 线程，暂停期间跳过显示器查询；恢复时会刷新布局及全屏状态。
        if (_screenshotSuspended || _placementRefresh is not null)
            return;

        // RDP reconnects can deliver display notifications before the topology settles.
        // Recheck infrequently, including while dialogs suspend hover handling.
        if (_displayRefreshClock.Elapsed >= DisplayRefreshInterval)
            RefreshPlacement();

        RefreshFullscreenVisibility();
        if (_suspended || _fullscreenHidden)
            return;

        if (!NativeMethods.GetCursorPos(out var p))
        {
            _insideTriggerSince = null;
            return;
        }

        Point cursor = ToDip(p);

        if (_state == DockState.Hidden)
        {
            if (_triggerRect.Contains(cursor))
            {
                DateTime now = DateTime.UtcNow;
                _insideTriggerSince ??= now;

                // 必须连续停留满设定时长；快速划过或中途离开都会重新计时。
                if (now - _insideTriggerSince >= TimeSpan.FromMilliseconds(ExpandDelayMs))
                {
                    AppLogger.Info($"Dock expansion triggered after {ExpandDelayMs} ms hover delay.");
                    Expand();
                }
            }
            else
            {
                _insideTriggerSince = null;
            }
        }
        else if (IsPinned)
        {
            _outsideSince = null;
        }
        else
        {
            if (_expandedRect.Contains(cursor))
            {
                _outsideSince = null;
            }
            else
            {
                _outsideSince ??= DateTime.Now;
                if (DateTime.Now - _outsideSince >= TimeSpan.FromMilliseconds(CollapseDelayMs))
                    Collapse();
            }
        }
    }

    private void Expand()
    {
        RefreshFullscreenVisibility(force: true);
        if (_fullscreenHidden || _screenshotSuspended)
            return;

        PromoteToTopmost();
        if (_state == DockState.Expanded)
            return;
        _state = DockState.Expanded;
        _insideTriggerSince = null;
        _outsideSince = null;
        ApplyRect(_expandedRect, animate: true);
    }

    private void RefreshFullscreenVisibility(bool force = false)
    {
        // The screenshot overlay is our own foreground window. Keep the pre-capture fullscreen
        // state until the session closes and restores the previous foreground window.
        if (_screenshotSuspended ||
            (!force && _fullscreenCheckClock.IsRunning && _fullscreenCheckClock.Elapsed < FullscreenCheckInterval))
        {
            return;
        }

        _fullscreenCheckClock.Restart();
        bool fullscreen = _fullscreenDetector.IsFullscreenOnMonitor(_displayMonitor);
        if (fullscreen)
        {
            _fullscreenExitClock.Reset();
        }
        else if (_fullscreenHidden)
        {
            // Switching display modes or Alt-Tab can temporarily leave no foreground window.
            // Require a stable exit before showing a topmost window over the application again.
            if (!_fullscreenExitClock.IsRunning)
                _fullscreenExitClock.Start();
            if (_fullscreenExitClock.Elapsed < FullscreenRestoreDelay)
                return;
        }

        if (_fullscreenHidden == fullscreen)
            return;

        _fullscreenHidden = fullscreen;
        _insideTriggerSince = null;
        _outsideSince = null;
        UpdateWindowVisibility();
        AppLogger.Info($"[DockManager] Fullscreen visibility changed: hidden={fullscreen}, state={_state}.");
    }

    private void UpdateWindowVisibility()
    {
        if (_fullscreenHidden || _screenshotSuspended)
        {
            _animTimer.Stop();
            if (_window.IsVisible)
            {
                // Share visibility ownership between fullscreen and capture so ending either
                // one cannot reveal the dock while the other still requires it to be hidden.
                _restoreVisibility = true;
                _window.Hide();
            }
            return;
        }

        if (!_restoreVisibility)
            return;

        _restoreVisibility = false;
        _insideTriggerSince = null;
        _outsideSince = null;
        ApplyRect(_state == DockState.Expanded ? _expandedRect : _collapsedRect, animate: false);
        bool showActivated = _window.ShowActivated;
        try
        {
            // Restoring a capsule must not steal focus, including when leaving an exclusive game.
            _window.ShowActivated = false;
            _window.Show();
        }
        finally
        {
            _window.ShowActivated = showActivated;
        }
    }

    private void PromoteToTopmost()
    {
        IntPtr hwnd = new WindowInteropHelper(_window).Handle;
        if (hwnd == IntPtr.Zero)
            return;

        const uint flags = NativeMethods.SWP_NOMOVE
            | NativeMethods.SWP_NOSIZE
            | NativeMethods.SWP_NOACTIVATE;

        // Topmost windows still have their own Z-order. Reassert only when revealing so the
        // sidebar moves ahead of later topmost windows without stealing focus or fighting them per frame.
        if (NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0, flags) ||
            _topmostPromotionFailureLogged)
        {
            return;
        }

        _topmostPromotionFailureLogged = true;
        AppLogger.Error($"Unable to promote dock window to topmost. Win32 error: {Marshal.GetLastWin32Error()}.");
    }

    private void Collapse()
    {
        if (_state == DockState.Hidden)
            return;
        _state = DockState.Hidden;
        _insideTriggerSince = null;
        _outsideSince = null;
        ApplyRect(_collapsedRect, animate: true);
    }

    private void ApplyRect(Rect target, bool animate)
    {
        _animTimer.Stop();

        // 必须在展开/中断动画前解除收起条的原生裁剪，避免面板仍被限制在窄条内。
        if (animate || _state == DockState.Expanded)
            _viewport?.SetCollapsedPresentation(false);

        if (!animate)
        {
            SetBounds(target);
            _viewport?.SetCollapsedPresentation(_state == DockState.Hidden);
            return;
        }

        _fromRect = new Rect(_window.Left, _window.Top, _window.Width, _window.Height);
        _toRect = target;
        _animClock.Restart();
        _animTimer.Start();
    }

    private void AnimTick(object? sender, EventArgs e)
    {
        double t = _animClock.Elapsed.TotalMilliseconds / AnimationMs;
        if (t >= 1)
        {
            _animTimer.Stop();
            SetBounds(_toRect);
            // 在最终尺寸落定后切换背景和裁剪，动画期间保持完整的面板呈现。
            _viewport?.SetCollapsedPresentation(_state == DockState.Hidden);
            return;
        }

        double k = 1 - Math.Pow(1 - t, 3); // cubic ease-out
        SetBounds(new Rect(
            Lerp(_fromRect.X, _toRect.X, k),
            Lerp(_fromRect.Y, _toRect.Y, k),
            Lerp(_fromRect.Width, _toRect.Width, k),
            Lerp(_fromRect.Height, _toRect.Height, k)));
    }

    private void SetBounds(Rect r)
    {
        Rect bounds = ClampToWorkArea(r);
        _viewport?.SetDockViewport(_expandedRect, bounds);
        SetHorizontalBounds(bounds.X, bounds.Width);
        SetVerticalBounds(bounds.Y, bounds.Height);
    }

    private Rect ClampToWorkArea(Rect r)
    {
        if (_workArea.IsEmpty)
            return r;

        double width = Math.Min(Math.Max(1, r.Width), Math.Max(1, _workArea.Width));
        double height = Math.Min(Math.Max(1, r.Height), Math.Max(1, _workArea.Height));
        double left = Math.Clamp(r.X, _workArea.Left, _workArea.Right - width);
        double top = Math.Clamp(r.Y, _workArea.Top, _workArea.Bottom - height);

        return new Rect(left, top, width, height);
    }

    private void SetHorizontalBounds(double left, double width)
    {
        if (left > _window.Left && width < _window.Width)
        {
            _window.Width = width;
            _window.Left = left;
            return;
        }

        _window.Left = left;
        _window.Width = width;
    }

    private void SetVerticalBounds(double top, double height)
    {
        if (top > _window.Top && height < _window.Height)
        {
            _window.Height = height;
            _window.Top = top;
            return;
        }

        _window.Top = top;
        _window.Height = height;
    }

    private static double Lerp(double a, double b, double k) => a + (b - a) * k;

    private Rect GetWorkArea()
    {
        (IntPtr Monitor, Rect WorkArea)? selected = null;
        (IntPtr Monitor, Rect WorkArea)? primary = null;
        (IntPtr Monitor, Rect WorkArea)? first = null;
        // Enumerate native monitors afresh; cached Screen objects can retain the old
        // work area during remote-session resolution and taskbar changes.
        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
            (IntPtr monitor, IntPtr hdc, ref NativeMethods.RECT bounds, IntPtr data) =>
            {
                var info = new NativeMethods.MONITORINFOEX
                {
                    Size = Marshal.SizeOf<NativeMethods.MONITORINFOEX>()
                };
                if (!NativeMethods.GetMonitorInfo(monitor, ref info))
                    return true;

                NativeMethods.RECT area = info.Work;
                if (area.Right <= area.Left || area.Bottom <= area.Top)
                    return true;

                var placement = (Monitor: monitor, WorkArea: ToDip(area));
                first ??= placement;
                if ((info.Flags & NativeMethods.MONITORINFOF_PRIMARY) != 0)
                    primary = placement;
                if (string.Equals(info.DeviceName, _displayDeviceName, StringComparison.OrdinalIgnoreCase))
                    selected = placement;
                return true;
            }, IntPtr.Zero);

        var available = selected ?? primary ?? first;
        if (available is { } availablePlacement)
        {
            // Use the same fallback monitor for fullscreen detection and dock placement.
            _displayMonitor = availablePlacement.Monitor;
            _displayReadFailureLogged = false;
            return availablePlacement.WorkArea;
        }

        if (!_displayReadFailureLogged)
        {
            AppLogger.Error("[DockManager] No usable monitor work area; retaining the previous placement until display information becomes available.");
            _displayReadFailureLogged = true;
        }

        // Keep the last usable geometry through a transient display disconnect.
        return _workArea.IsEmpty || _workArea.Width <= 0 ? SystemParameters.WorkArea : _workArea;
    }

    private Matrix DpiTransform => PresentationSource.FromVisual(_window)
        ?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;

    private Point ToDip(NativeMethods.POINT p) => DpiTransform.Transform(new Point(p.X, p.Y));

    private Rect ToDip(NativeMethods.RECT r)
    {
        Matrix transform = DpiTransform;
        Point topLeft = transform.Transform(new Point(r.Left, r.Top));
        Point bottomRight = transform.Transform(new Point(r.Right, r.Bottom));
        return new Rect(topLeft, bottomRight);
    }
}
