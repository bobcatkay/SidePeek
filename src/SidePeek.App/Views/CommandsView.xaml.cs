using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SidePeek.App.Models;
using SidePeek.App.ViewModels;

namespace SidePeek.App.Views;

public partial class CommandsView : UserControl
{
    private readonly CommandsViewModel _viewModel = new();
    private Point _dragStart;
    private CommandItem? _dragItem;

    public CommandsView()
    {
        InitializeComponent();
        DataContext = _viewModel;
    }

    private DockWindow? Host => Window.GetWindow(this) as DockWindow;

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        Host?.SuspendDock();
        try
        {
            var dialog = new CommandEditWindow { Owner = Host };
            if (dialog.ShowDialog() == true)
                _viewModel.Add(dialog.Result);
        }
        finally
        {
            Host?.ResumeDock();
        }
    }

    private void OnRun(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not CommandItem item)
            return;

        Host?.SuspendDock();
        try
        {
            var runner = new CommandRunnerWindow(item) { Owner = Host };
            runner.ShowDialog();
        }
        finally
        {
            Host?.ResumeDock();
        }
    }

    private void OnItemMenu(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not CommandItem item)
            return;

        var menu = new ContextMenu();

        var edit = new MenuItem { Header = "编辑" };
        edit.Click += (_, _) => Edit(item);

        var pin = new MenuItem { Header = "置顶" };
        pin.Click += (_, _) => _viewModel.MoveToTop(item);

        var delete = new MenuItem { Header = "删除" };
        delete.Click += (_, _) => Delete(item);

        menu.Items.Add(edit);
        menu.Items.Add(pin);
        menu.Items.Add(new Separator());
        menu.Items.Add(delete);
        menu.PlacementTarget = (UIElement)sender;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void Edit(CommandItem item)
    {
        Host?.SuspendDock();
        try
        {
            var dialog = new CommandEditWindow(item) { Owner = Host };
            if (dialog.ShowDialog() == true)
                _viewModel.Update(item, dialog.Result);
        }
        finally
        {
            Host?.ResumeDock();
        }
    }

    private void Delete(CommandItem item)
    {
        Host?.SuspendDock();
        try
        {
            var result = MessageBox.Show(
                $"确定要删除命令「{item.Title}」吗？", "删除确认",
                MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result == MessageBoxResult.Yes)
                _viewModel.Remove(item);
        }
        finally
        {
            Host?.ResumeDock();
        }
    }

    private void OnItemMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(this);
        _dragItem = ((FrameworkElement)sender).DataContext as CommandItem;
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
        e.Effects = e.Data.GetDataPresent(typeof(CommandItem)) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnItemDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(CommandItem)) is CommandItem source &&
            ((FrameworkElement)sender).DataContext is CommandItem target)
        {
            _viewModel.Move(source, target);
        }

        e.Handled = true;
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not CommandItem item)
            return;

        Delete(item);
    }
}
