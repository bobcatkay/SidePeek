using System.Windows;

namespace SidePeek.App.Docking;

public interface IDockViewport
{
    void SetDockViewport(Rect expandedRect, Rect viewportRect);

    /// <summary>展开前恢复面板效果，收起完成后切换为无外扩背景的触发条。</summary>
    void SetCollapsedPresentation(bool isCollapsed);
}
