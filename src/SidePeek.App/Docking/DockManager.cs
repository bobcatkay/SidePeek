using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using SidePeek.App.Interop;
using SidePeek.App.Models;

namespace SidePeek.App.Docking;

/// <summary>
/// 负责把窗口吸附到屏幕某一边，并实现「悬停展开 / 移开自动收起」。
/// 收起态：仅在停靠边中部露出一小段（屏幕长边的 12.5%、垂直/水平居中）的触发块。
/// 展开态：沿停靠边铺满工作区。展开/收起同时对位置与尺寸做缓动动画。
/// </summary>
public sealed class DockManager
{
    private const double PanelThickness = 380;   // 左右停靠=宽度；上下停靠=高度
    private const double TriggerStrip = 6;        // 收起时露出的厚度（像素）
    private const double CollapseRatio = 0.125;   // 收起时沿边长度占比
    private const double AnimationMs = 220;
    private static readonly TimeSpan CollapseDelay = TimeSpan.FromMilliseconds(450);

    private readonly Window _window;
    private readonly DispatcherTimer _pollTimer;
    private readonly DispatcherTimer _animTimer;
    private readonly Stopwatch _animClock = new();

    private DockEdge _edge = DockEdge.Right;
    private DockState _state = DockState.Hidden;
    private DateTime? _outsideSince;
    private bool _suspended;

    private Rect _expandedRect;
    private Rect _collapsedRect;
    private Rect _triggerRect;

    private Rect _fromRect;
    private Rect _toRect;

    public DockManager(Window window)
    {
        _window = window;
        _pollTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(25) };
        _pollTimer.Tick += Poll;

        _animTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(15) };
        _animTimer.Tick += AnimTick;
    }

    public DockEdge Edge => _edge;
    public event EventHandler<DockEdge>? EdgeChanged;

    public void Start(DockEdge edge, bool startExpanded = false)
    {
        SetEdge(edge, startExpanded);
        _pollTimer.Start();
    }

    /// <summary>对话框打开期间暂停轮询，避免面板在用户离开时收起。</summary>
    public void Suspend() => _suspended = true;

    public void Resume() => _suspended = false;

    public void SetEdge(DockEdge edge, bool expanded = false)
    {
        _edge = edge;
        Layout();
        _outsideSince = null;
        _state = expanded ? DockState.Expanded : DockState.Hidden;
        ApplyRect(expanded ? _expandedRect : _collapsedRect, animate: false);
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
        Rect wa = SystemParameters.WorkArea;

        switch (_edge)
        {
            case DockEdge.Left:
            {
                double ch = wa.Height * CollapseRatio;
                double cy = wa.Top + (wa.Height - ch) / 2;
                _expandedRect = new Rect(wa.Left, wa.Top, PanelThickness, wa.Height);
                _collapsedRect = new Rect(wa.Left - PanelThickness + TriggerStrip, cy, PanelThickness, ch);
                _triggerRect = new Rect(wa.Left, cy, TriggerStrip, ch);
                break;
            }
            case DockEdge.Right:
            {
                double ch = wa.Height * CollapseRatio;
                double cy = wa.Top + (wa.Height - ch) / 2;
                _expandedRect = new Rect(wa.Right - PanelThickness, wa.Top, PanelThickness, wa.Height);
                _collapsedRect = new Rect(wa.Right - TriggerStrip, cy, PanelThickness, ch);
                _triggerRect = new Rect(wa.Right - TriggerStrip, cy, TriggerStrip, ch);
                break;
            }
            case DockEdge.Top:
            {
                double cw = wa.Width * CollapseRatio;
                double cx = wa.Left + (wa.Width - cw) / 2;
                _expandedRect = new Rect(wa.Left, wa.Top, wa.Width, PanelThickness);
                _collapsedRect = new Rect(cx, wa.Top - PanelThickness + TriggerStrip, cw, PanelThickness);
                _triggerRect = new Rect(cx, wa.Top, cw, TriggerStrip);
                break;
            }
            case DockEdge.Bottom:
            {
                double cw = wa.Width * CollapseRatio;
                double cx = wa.Left + (wa.Width - cw) / 2;
                _expandedRect = new Rect(wa.Left, wa.Bottom - PanelThickness, wa.Width, PanelThickness);
                _collapsedRect = new Rect(cx, wa.Bottom - TriggerStrip, cw, PanelThickness);
                _triggerRect = new Rect(cx, wa.Bottom - TriggerStrip, cw, TriggerStrip);
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
        else
        {
            if (_expandedRect.Contains(cursor))
            {
                _outsideSince = null;
            }
            else
            {
                _outsideSince ??= DateTime.Now;
                if (DateTime.Now - _outsideSince >= CollapseDelay)
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
        _window.Left = r.X;
        _window.Top = r.Y;
        _window.Width = Math.Max(1, r.Width);
        _window.Height = Math.Max(1, r.Height);
    }

    private static double Lerp(double a, double b, double k) => a + (b - a) * k;

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
}
