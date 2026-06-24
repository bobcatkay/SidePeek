using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using SidePeek.App.Models;
using SidePeek.App.ViewModels;

namespace SidePeek.App.Views;

public partial class ClipboardView : UserControl
{
    private readonly ClipboardViewModel _viewModel = new();
    private readonly DispatcherTimer _toastTimer;
    private DragSortController<ClipboardItem> _dragSort = null!;
    private Point _dragStart;

    public ClipboardView()
    {
        InitializeComponent();
        DataContext = _viewModel;
        _dragSort = new DragSortController<ClipboardItem>(
            ClipboardRoot,
            DragOverlay,
            ClipboardItemsControl,
            (source, target) => _viewModel.Move(source, target));
        _viewModel.CopySucceeded += OnCopySucceeded;

        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1400) };
        _toastTimer.Tick += (_, _) => HideCopySuccessToast();
    }

    private void OnCopySucceeded(object? sender, EventArgs e) => ShowCopySuccessToast();

    private void ShowCopySuccessToast()
    {
        _toastTimer.Stop();
        CopySuccessToast.Visibility = Visibility.Visible;

        var opacityAnimation = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        CopySuccessToast.BeginAnimation(OpacityProperty, opacityAnimation);

        if (CopySuccessToast.RenderTransform is TranslateTransform transform)
        {
            var yAnimation = new DoubleAnimation(-8, 0, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            transform.BeginAnimation(TranslateTransform.YProperty, yAnimation);
        }

        _toastTimer.Start();
    }

    private void HideCopySuccessToast()
    {
        _toastTimer.Stop();

        var opacityAnimation = new DoubleAnimation(CopySuccessToast.Opacity, 0, TimeSpan.FromMilliseconds(160))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
        };
        opacityAnimation.Completed += (_, _) => CopySuccessToast.Visibility = Visibility.Collapsed;
        CopySuccessToast.BeginAnimation(OpacityProperty, opacityAnimation);

        if (CopySuccessToast.RenderTransform is TranslateTransform transform)
        {
            var yAnimation = new DoubleAnimation(0, -6, TimeSpan.FromMilliseconds(160))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
            };
            transform.BeginAnimation(TranslateTransform.YProperty, yAnimation);
        }
    }

    private void OnItemMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (IsButtonElement(e.OriginalSource as DependencyObject))
        {
            _dragSort.Cancel();
            return;
        }

        _dragStart = e.GetPosition(this);
        if (((FrameworkElement)sender).DataContext is ClipboardItem item)
            _dragSort.BeginCandidate(item, (FrameworkElement)sender, e.GetPosition(ClipboardRoot));
    }

    private void OnItemMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
            return;

        Point current = e.GetPosition(this);
        if (Math.Abs(current.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        if (_dragSort.TryStart(e.GetPosition(ClipboardRoot)))
            e.Handled = true;
    }

    private static bool IsButtonElement(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is ButtonBase)
                return true;
            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }
}
