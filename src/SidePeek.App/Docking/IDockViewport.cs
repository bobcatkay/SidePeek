using System.Windows;

namespace SidePeek.App.Docking;

public interface IDockViewport
{
    void SetDockViewport(Rect expandedRect, Rect viewportRect);
}
