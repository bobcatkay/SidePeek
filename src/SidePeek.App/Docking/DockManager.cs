using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using SidePeek.App.Interop;
using SidePeek.App.Models;
using Forms = System.Windows.Forms;

namespace SidePeek.App.Docking;

/// <summary>
/// 负责把窗口吸附到屏幕某一边，并实现「悬停展开 / 移开自动收起」。
/// 收起态：仅在停靠边中部露出一小段（屏幕长边的 10%、垂直/水平居中）的触发块。
/// 展开态：沿停靠边铺满工作区。展开/收起同时对位置与尺寸做缓动动画。
/// </summary>
public sealed class DockManager
{
    private const double PanelThickness = 420;   // 左右停靠=宽度；上下停靠=高度
    private const double TriggerStrip = 6;        // 收起时露出的厚度（像素）
    private const double CollapseRatio = 0.10;    // 收起时沿边长度占比
    private const double AnimationMs = 220;

    private readonly Window _window;
    private readonly IDockViewport? _viewport;
    private readonly DispatcherTimer _pollTimer;
    private readonly DispatcherTimer _animTimer;
    private readonly Stopwatch _animClock = new();

    private DockEdge _edge = DockEdge.Right;
    private string _displayDeviceName = string.Empty;
    private DockState _state = DockState.Hidden;
    private DateTime? _outsideSince;
    private bool _suspended;

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
    public int CollapseDelayMs { get; set; } = 450;
    public bool IsPinned { get; set; }
    public event EventHandler<DockEdge>? EdgeChanged;

    public void Start(DockEdge edge, string displayDeviceName, bool startExpanded = false)
    {
        SetPlacement(edge, displayDeviceName, startExpanded);
        _pollTimer.Start();
    }

    /// <summary>对话框打开期间暂停轮询，避免面板在用户离开时收起。</summary>
    public void Suspend() => _suspended = true;

    public void Resume() => _suspended = false;

    public void SetEdge(DockEdge edge, bool expanded = false)
        => SetPlacement(edge, _displayDeviceName, expanded);

    public void SetPlacement(DockEdge edge, string displayDeviceName, bool expanded = false)
    {
        bool edgeChanged = _edge != edge;
        _edge = edge;
        _displayDeviceName = displayDeviceName ?? string.Empty;
        Layout();
        _outsideSince = null;
        _state = expanded ? DockState.Expanded : DockState.Hidden;
        ApplyRect(expanded ? _expandedRect : _collapsedRect, animate: false);
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
        if (_state == DockState.Expanded)
            Collapse();
        else
            Expand();
    }

    public void Reveal() => Expand();

    private void Layout()
    {
        _workArea = GetWorkArea();
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
        if (_suspended || !NativeMethods.GetCursorPos(out var p))
            return;

        Point cursor = ToDip(p);

        if (_state == DockState.Hidden)
        {
            if (_triggerRect.Contains(cursor))
                Expand();
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
        if (_state == DockState.Expanded)
            return;
        _state = DockState.Expanded;
        _outsideSince = null;
        ApplyRect(_expandedRect, animate: true);
    }

    private void Collapse()
    {
        if (_state == DockState.Hidden)
            return;
        _state = DockState.Hidden;
        _outsideSince = null;
        ApplyRect(_collapsedRect, animate: true);
    }

    private void ApplyRect(Rect target, bool animate)
    {
        _animTimer.Stop();

        if (!animate)
        {
            SetBounds(target);
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
        Forms.Screen? screen = Forms.Screen.AllScreens.FirstOrDefault(item =>
            string.Equals(item.DeviceName, _displayDeviceName, StringComparison.OrdinalIgnoreCase));

        screen ??= Forms.Screen.PrimaryScreen ?? Forms.Screen.AllScreens.FirstOrDefault();
        if (screen is null)
            return SystemParameters.WorkArea;

        return ToDip(screen.WorkingArea);
    }

    private Point ToDip(NativeMethods.POINT p)
    {
        var source = PresentationSource.FromVisual(_window);
        double sx = 1, sy = 1;
        if (source?.CompositionTarget != null)
        {
            Matrix m = source.CompositionTarget.TransformFromDevice;
            sx = m.M11;
            sy = m.M22;
        }
        return new Point(p.X * sx, p.Y * sy);
    }

    private Rect ToDip(System.Drawing.Rectangle r)
    {
        var source = PresentationSource.FromVisual(_window);
        Matrix transform = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        Point topLeft = transform.Transform(new Point(r.Left, r.Top));
        Point bottomRight = transform.Transform(new Point(r.Right, r.Bottom));
        return new Rect(topLeft, bottomRight);
    }
}
