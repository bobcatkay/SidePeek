using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SidePeek.App.Views;

internal sealed class DragSortController<TItem>
    where TItem : class
{
    private readonly FrameworkElement _root;
    private readonly Canvas _overlay;
    private readonly ItemsControl _itemsControl;
    private readonly Action<TItem, TItem> _moveItem;
    private const double DragGhostOpacity = 1;

    private Point _startPoint;
    private Point _ghostOffset;
    private TItem? _dragItem;
    private FrameworkElement? _sourceElement;
    private FrameworkElement? _dropTargetElement;
    private FrameworkElement? _ghost;
    private Dictionary<object, Point>? _positions;
    private Visibility _sourceVisibility;
    private bool _isDragging;

    public bool IsDragging => _isDragging;

    public DragSortController(
        FrameworkElement root,
        Canvas overlay,
        ItemsControl itemsControl,
        Action<TItem, TItem> moveItem)
    {
        _root = root;
        _overlay = overlay;
        _itemsControl = itemsControl;
        _moveItem = moveItem;
    }

    public void BeginCandidate(TItem item, FrameworkElement sourceElement, Point startPoint)
    {
        if (_isDragging)
            Cancel();

        _dragItem = item;
        _sourceElement = sourceElement;
        _startPoint = startPoint;
    }

    public bool TryStart(Point currentPoint)
    {
        if (_dragItem is null || _sourceElement is null)
            return false;

        if (Math.Abs(currentPoint.X - _startPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(currentPoint.Y - _startPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return false;
        }

        Start(currentPoint);
        return true;
    }

    public void Cancel()
    {
        if (!_isDragging)
        {
            ResetCandidate();
            return;
        }

        Cleanup(commit: false);
    }

    private void Start(Point currentPoint)
    {
        if (_isDragging || _dragItem is null || _sourceElement is null)
            return;

        _root.UpdateLayout();
        _sourceElement.UpdateLayout();
        _positions = DragSortAnimations.CapturePositions(_itemsControl, _root);
        ReleaseCurrentMouseCapture();

        Point sourcePoint = _sourceElement.TransformToAncestor(_root).Transform(new Point(0, 0));
        _ghostOffset = new Point(currentPoint.X - sourcePoint.X, currentPoint.Y - sourcePoint.Y);
        _ghost = DragSortAnimations.CreateSnapshot(_sourceElement, opacity: DragGhostOpacity, showDragTint: true);
        _ghost.RenderTransform = new ScaleTransform(1, 1);
        _ghost.RenderTransformOrigin = new Point(0.5, 0.5);
        Canvas.SetLeft(_ghost, sourcePoint.X);
        Canvas.SetTop(_ghost, sourcePoint.Y);
        _overlay.Children.Add(_ghost);
        AnimateGhostScale(_ghost, 1.06, DragGhostOpacity, DragSortAnimations.GrabDuration);

        _sourceVisibility = _sourceElement.Visibility;
        _sourceElement.Visibility = Visibility.Hidden;
        _isDragging = true;
        Mouse.OverrideCursor = Cursors.SizeAll;

        _root.PreviewMouseMove += OnRootMouseMove;
        _root.PreviewMouseLeftButtonUp += OnRootMouseLeftButtonUp;
        _root.LostMouseCapture += OnRootLostMouseCapture;
        if (!_root.CaptureMouse())
        {
            Cleanup(commit: false);
            return;
        }

        Update(currentPoint);
    }

    private void OnRootMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging)
            return;

        Update(e.GetPosition(_root));
        e.Handled = true;
    }

    private void OnRootMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDragging)
            return;

        Update(e.GetPosition(_root));
        Cleanup(commit: true);
        e.Handled = true;
    }

    private void OnRootLostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_isDragging)
            Cleanup(commit: false);
    }

    private void Update(Point point)
    {
        if (_ghost is not null)
        {
            Canvas.SetLeft(_ghost, point.X - _ghostOffset.X);
            Canvas.SetTop(_ghost, point.Y - _ghostOffset.Y);
        }

        FrameworkElement? target = FindItemElement(_root.InputHitTest(point) as DependencyObject);
        if (target?.DataContext is not TItem targetItem ||
            _dragItem is null ||
            ReferenceEquals(targetItem, _dragItem))
        {
            SetDropTarget(null);
            return;
        }

        SetDropTarget(target);
    }

    private void Cleanup(bool commit)
    {
        if (!_isDragging)
            return;

        _isDragging = false;
        _root.PreviewMouseMove -= OnRootMouseMove;
        _root.PreviewMouseLeftButtonUp -= OnRootMouseLeftButtonUp;
        _root.LostMouseCapture -= OnRootLostMouseCapture;

        if (ReferenceEquals(Mouse.Captured, _root))
            _root.ReleaseMouseCapture();

        TItem? source = _dragItem;
        TItem? target = _dropTargetElement?.DataContext as TItem;
        Dictionary<object, Point>? positions = _positions;
        FrameworkElement? sourceElement = _sourceElement;
        FrameworkElement? ghost = _ghost;
        Visibility sourceVisibility = _sourceVisibility;

        Mouse.OverrideCursor = null;

        if (commit &&
            source is not null &&
            target is not null &&
            !ReferenceEquals(source, target))
        {
            _moveItem(source, target);
            _itemsControl.UpdateLayout();
            if (positions is not null)
                DragSortAnimations.AnimateReorder(_itemsControl, _root, positions, excludedItem: source);
            SetDropTarget(null);

            Point finalPoint = GetElementPosition(sourceElement) ?? GetItemPosition(source) ?? GetGhostPosition(ghost);
            AnimateGhostHome(ghost, finalPoint, () => RestoreSource(sourceElement, sourceVisibility));
        }
        else
        {
            SetDropTarget(null);
            Point finalPoint = GetElementPosition(sourceElement) ?? GetGhostPosition(ghost);
            AnimateGhostHome(ghost, finalPoint, () => RestoreSource(sourceElement, sourceVisibility));
        }

        ResetCandidate();
    }

    private void SetDropTarget(FrameworkElement? element)
    {
        if (ReferenceEquals(_dropTargetElement, element))
            return;

        DragSortAnimations.SetDropTarget(_dropTargetElement, false);
        _dropTargetElement = element;
        DragSortAnimations.SetDropTarget(_dropTargetElement, true);
    }

    private void ResetCandidate()
    {
        _dragItem = null;
        _sourceElement = null;
        _dropTargetElement = null;
        _ghost = null;
        _positions = null;
        _isDragging = false;
    }

    private static FrameworkElement? FindItemElement(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is FrameworkElement { AllowDrop: true, DataContext: TItem } element)
                return element;

            source = VisualTreeHelper.GetParent(source);
        }

        return null;
    }

    private Point? GetElementPosition(FrameworkElement? element)
    {
        if (element is null)
            return null;

        try
        {
            return element.TransformToAncestor(_root).Transform(new Point(0, 0));
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private Point? GetItemPosition(TItem item)
    {
        if (_itemsControl.ItemContainerGenerator.ContainerFromItem(item) is not FrameworkElement container)
            return null;

        try
        {
            return container.TransformToAncestor(_root).Transform(new Point(0, 0));
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static Point GetGhostPosition(FrameworkElement? ghost)
    {
        if (ghost is null)
            return new Point(0, 0);

        return new Point(Canvas.GetLeft(ghost), Canvas.GetTop(ghost));
    }

    private void AnimateGhostHome(FrameworkElement? ghost, Point finalPoint, Action completed)
    {
        if (ghost is null)
        {
            completed();
            return;
        }

        var storyboard = new Storyboard { FillBehavior = FillBehavior.Stop };
        TimeSpan duration = DragSortAnimations.ReorderDuration;
        double currentLeft = Canvas.GetLeft(ghost);
        double currentTop = Canvas.GetTop(ghost);

        AddGhostAnimation(storyboard, ghost, Canvas.LeftProperty, currentLeft, finalPoint.X, duration);
        AddGhostAnimation(storyboard, ghost, Canvas.TopProperty, currentTop, finalPoint.Y, duration);
        AddGhostAnimation(storyboard, ghost, UIElement.OpacityProperty, ghost.Opacity, 1, duration);

        if (ghost.RenderTransform is ScaleTransform scale)
        {
            AddGhostAnimation(storyboard, scale, ScaleTransform.ScaleXProperty, scale.ScaleX, 1, duration);
            AddGhostAnimation(storyboard, scale, ScaleTransform.ScaleYProperty, scale.ScaleY, 1, duration);
        }

        storyboard.Completed += (_, _) =>
        {
            _overlay.Children.Remove(ghost);
            completed();
        };
        storyboard.Begin();
    }

    private static void AnimateGhostScale(FrameworkElement ghost, double scale, double opacity, TimeSpan duration)
    {
        if (ghost.RenderTransform is not ScaleTransform transform)
            return;

        var easing = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        transform.BeginAnimation(
            ScaleTransform.ScaleXProperty,
            new DoubleAnimation(scale, duration) { EasingFunction = easing });
        transform.BeginAnimation(
            ScaleTransform.ScaleYProperty,
            new DoubleAnimation(scale, duration) { EasingFunction = easing });
        ghost.BeginAnimation(
            UIElement.OpacityProperty,
            new DoubleAnimation(opacity, duration) { EasingFunction = easing });
    }

    private static void AddGhostAnimation(
        Storyboard storyboard,
        DependencyObject target,
        DependencyProperty property,
        double from,
        double to,
        TimeSpan duration)
    {
        var animation = new DoubleAnimation(from, to, duration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, new PropertyPath(property));
        storyboard.Children.Add(animation);
    }

    private static void RestoreSource(FrameworkElement? sourceElement, Visibility sourceVisibility)
    {
        if (sourceElement is not null)
            sourceElement.Visibility = sourceVisibility;
    }

    private static void ReleaseCurrentMouseCapture()
    {
        if (Mouse.Captured is UIElement capturedElement)
            capturedElement.ReleaseMouseCapture();
        else if (Mouse.Captured is ContentElement capturedContent)
            capturedContent.ReleaseMouseCapture();
    }
}
