using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace SidePeek.App.Views;

internal static class DragSortAnimations
{
    public static readonly TimeSpan ReorderDuration = TimeSpan.FromMilliseconds(260);
    public static readonly TimeSpan GrabDuration = TimeSpan.FromMilliseconds(120);

    private static readonly TimeSpan FeedbackDuration = TimeSpan.FromMilliseconds(260);

    public static Dictionary<object, Point> CapturePositions(ItemsControl itemsControl, Visual relativeTo)
    {
        itemsControl.UpdateLayout();

        var positions = new Dictionary<object, Point>();
        foreach (object item in itemsControl.Items)
        {
            if (itemsControl.ItemContainerGenerator.ContainerFromItem(item) is not FrameworkElement container ||
                container.ActualWidth <= 0 ||
                container.ActualHeight <= 0)
            {
                continue;
            }

            positions[item] = container.TransformToAncestor(relativeTo).Transform(new Point(0, 0));
        }

        return positions;
    }

    public static void AnimateReorder(
        ItemsControl itemsControl,
        Visual relativeTo,
        IReadOnlyDictionary<object, Point> previousPositions,
        object? excludedItem = null)
    {
        itemsControl.UpdateLayout();

        foreach (object item in itemsControl.Items)
        {
            if (ReferenceEquals(item, excludedItem))
                continue;

            if (!previousPositions.TryGetValue(item, out Point previous) ||
                itemsControl.ItemContainerGenerator.ContainerFromItem(item) is not FrameworkElement container)
            {
                continue;
            }

            Point current = container.TransformToAncestor(relativeTo).Transform(new Point(0, 0));
            double offsetX = previous.X - current.X;
            double offsetY = previous.Y - current.Y;
            if (Math.Abs(offsetX) < 0.5 && Math.Abs(offsetY) < 0.5)
                continue;

            (_, TranslateTransform transform) = EnsureSortTransform(container);
            transform.X = offsetX;
            transform.Y = offsetY;

            var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
            transform.BeginAnimation(
                TranslateTransform.XProperty,
                new DoubleAnimation(offsetX, 0, ReorderDuration) { EasingFunction = easing });
            transform.BeginAnimation(
                TranslateTransform.YProperty,
                new DoubleAnimation(offsetY, 0, ReorderDuration) { EasingFunction = easing });
        }
    }

    public static FrameworkElement CreateSnapshot(FrameworkElement source, double opacity = 0.97, bool showDragTint = false)
    {
        source.UpdateLayout();

        int pixelWidth = Math.Max(1, (int)Math.Ceiling(source.ActualWidth));
        int pixelHeight = Math.Max(1, (int)Math.Ceiling(source.ActualHeight));
        DpiScale dpi = VisualTreeHelper.GetDpi(source);

        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(pixelWidth * dpi.DpiScaleX),
            (int)Math.Ceiling(pixelHeight * dpi.DpiScaleY),
            96 * dpi.DpiScaleX,
            96 * dpi.DpiScaleY,
            PixelFormats.Pbgra32);
        bitmap.Render(source);
        if (bitmap.CanFreeze)
            bitmap.Freeze();

        var brush = new ImageBrush(bitmap) { Stretch = Stretch.Fill };
        if (brush.CanFreeze)
            brush.Freeze();

        var snapshot = new Grid
        {
            Width = source.ActualWidth,
            Height = source.ActualHeight,
            IsHitTestVisible = false,
            Opacity = opacity,
            CacheMode = new BitmapCache(),
            Clip = new RectangleGeometry(
                new Rect(0, 0, source.ActualWidth, source.ActualHeight),
                10,
                10)
        };

        snapshot.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = brush
        });

        if (showDragTint)
        {
            var tintMask = new ImageBrush(bitmap) { Stretch = Stretch.Fill };
            if (tintMask.CanFreeze)
                tintMask.Freeze();

            snapshot.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(10),
                Background = new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)),
                OpacityMask = tintMask
            });
        }

        return snapshot;
    }

    public static void SetDragSource(FrameworkElement? element, bool active)
    {
        AnimateFeedback(element, active ? 0.985 : 1, active ? 0.62 : 1);
    }

    public static void SetDropTarget(FrameworkElement? element, bool active)
    {
        AnimateFeedback(element, active ? 0.88 : 1, 1);
    }

    private static void AnimateFeedback(FrameworkElement? element, double scale, double opacity)
    {
        if (element is null)
            return;

        (ScaleTransform transform, _) = EnsureSortTransform(element);

        var easing = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        transform.BeginAnimation(
            ScaleTransform.ScaleXProperty,
            new DoubleAnimation(scale, FeedbackDuration) { EasingFunction = easing });
        transform.BeginAnimation(
            ScaleTransform.ScaleYProperty,
            new DoubleAnimation(scale, FeedbackDuration) { EasingFunction = easing });
        element.BeginAnimation(
            UIElement.OpacityProperty,
            new DoubleAnimation(opacity, FeedbackDuration) { EasingFunction = easing });
    }

    private static (ScaleTransform Scale, TranslateTransform Translate) EnsureSortTransform(FrameworkElement element)
    {
        if (element.RenderTransform is TransformGroup group &&
            group.Children.Count == 2 &&
            group.Children[0] is ScaleTransform scale &&
            group.Children[1] is TranslateTransform translate)
        {
            return (scale, translate);
        }

        double scaleX = 1;
        double scaleY = 1;
        double translateX = 0;
        double translateY = 0;

        if (element.RenderTransform is ScaleTransform existingScale)
        {
            scaleX = existingScale.ScaleX;
            scaleY = existingScale.ScaleY;
        }
        else if (element.RenderTransform is TranslateTransform existingTranslate)
        {
            translateX = existingTranslate.X;
            translateY = existingTranslate.Y;
        }

        scale = new ScaleTransform(scaleX, scaleY);
        translate = new TranslateTransform(translateX, translateY);
        element.RenderTransform = new TransformGroup
        {
            Children = new TransformCollection { scale, translate }
        };
        element.RenderTransformOrigin = new Point(0.5, 0.5);

        return (scale, translate);
    }
}
