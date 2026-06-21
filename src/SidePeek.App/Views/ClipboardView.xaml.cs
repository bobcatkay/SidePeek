using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using SidePeek.App.Models;
using SidePeek.App.ViewModels;

namespace SidePeek.App.Views;

public partial class ClipboardView : UserControl
{
    private readonly ClipboardViewModel _viewModel = new();
    private Point _dragStart;
    private ClipboardItem? _dragItem;

    public ClipboardView()
    {
        InitializeComponent();
        DataContext = _viewModel;
    }

    private void OnItemMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (IsButtonElement(e.OriginalSource as DependencyObject))
        {
            _dragItem = null;
            return;
        }

        _dragStart = e.GetPosition(this);
        _dragItem = ((FrameworkElement)sender).DataContext as ClipboardItem;
    }

    private void OnItemMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragItem is null)
            return;

        Point current = e.GetPosition(this);
        if (Math.Abs(current.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        DragDrop.DoDragDrop((DependencyObject)sender, _dragItem, DragDropEffects.Move);
        _dragItem = null;
    }

    private void OnItemDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(ClipboardItem)) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnItemDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(ClipboardItem)) is ClipboardItem source &&
            ((FrameworkElement)sender).DataContext is ClipboardItem target)
        {
            _viewModel.Move(source, target);
        }

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
