using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using SidePeek.App.Models;
using SidePeek.App.Services;
using SidePeek.App.ViewModels;

namespace SidePeek.App.Views;

public partial class WidgetsView : UserControl
{
    private readonly WidgetsViewModel _viewModel = new();
    private Point _dragStart;
    private DragSortController<ToolItem> _dragSort = null!;
    private bool _suppressItemClick;
    private ToolItem? _pendingDeleteItem;

    public WidgetsView()
    {
        InitializeComponent();
        DataContext = _viewModel;
        _dragSort = new DragSortController<ToolItem>(
            WidgetsRoot,
            DragOverlay,
            ToolsItemsControl,
            (source, target) => _viewModel.Move(source, target));
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        HideDeleteConfirm();
        _dragSort.Cancel();
    }

    private DockWindow? Host => Window.GetWindow(this) as DockWindow;

    private void OnAddTool(object sender, RoutedEventArgs e)
    {
        Host?.SuspendDock();
        try
        {
            var dialog = new ToolEditWindow { Owner = Host };
            if (dialog.ShowDialog() == true)
                _viewModel.Add(dialog.Result);
        }
        finally
        {
            Host?.ResumeDock();
        }
    }

    private void OnLaunchTool(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is ToolItem item)
            _viewModel.Launch(item);
    }

    private void OnItemMenu(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not ToolItem item)
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

    private void Edit(ToolItem item)
    {
        Host?.SuspendDock();
        try
        {
            var dialog = new ToolEditWindow(item) { Owner = Host };
            if (dialog.ShowDialog() == true)
                _viewModel.Update(item, dialog.Result);
        }
        finally
        {
            Host?.ResumeDock();
        }
    }

    private void Delete(ToolItem item)
    {
        _pendingDeleteItem = item;
        string title = string.IsNullOrWhiteSpace(item.Title) ? "这个工具" : $"工具「{item.Title}」";
        DeleteConfirmMessage.Text = $"确定要删除{title}吗？删除后无法恢复。";
        ApplyDeleteConfirmColors();
        DeleteConfirmOverlay.Visibility = Visibility.Visible;
        Host?.SuspendDock();
    }

    private void OnCancelDelete(object sender, RoutedEventArgs e) => HideDeleteConfirm();

    private void OnConfirmDelete(object sender, RoutedEventArgs e)
    {
        if (_pendingDeleteItem is not null)
            _viewModel.Remove(_pendingDeleteItem);

        HideDeleteConfirm();
    }

    private void HideDeleteConfirm()
    {
        if (DeleteConfirmOverlay.Visibility != Visibility.Visible)
            return;

        _pendingDeleteItem = null;
        DeleteConfirmOverlay.Visibility = Visibility.Collapsed;
        Host?.ResumeDock();
    }

    private void ApplyDeleteConfirmColors()
    {
        bool isLightTheme = ThemeService.IsLightTheme(SettingsService.Current.Theme);
        DeleteConfirmPanel.Background = new SolidColorBrush(isLightTheme
            ? Colors.White
            : Color.FromRgb(0x24, 0x28, 0x30));
    }

    private void OnItemMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (IsButtonElement(e.OriginalSource as DependencyObject))
        {
            _dragSort.Cancel();
            return;
        }

        _suppressItemClick = false;
        _dragStart = e.GetPosition(this);
        if (((FrameworkElement)sender).DataContext is ToolItem item)
            _dragSort.BeginCandidate(item, (FrameworkElement)sender, e.GetPosition(WidgetsRoot));
    }

    private void OnItemMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
            return;

        Point current = e.GetPosition(this);
        if (Math.Abs(current.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        if (_dragSort.TryStart(e.GetPosition(WidgetsRoot)))
        {
            _suppressItemClick = true;
            e.Handled = true;
        }
    }

    private void OnItemMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_suppressItemClick || _dragSort.IsDragging)
        {
            _suppressItemClick = false;
            e.Handled = true;
            return;
        }

        if (IsButtonElement(e.OriginalSource as DependencyObject))
            return;

        if (((FrameworkElement)sender).DataContext is ToolItem item)
        {
            _viewModel.Launch(item);
            e.Handled = true;
        }
    }

    private void OnDeleteTool(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not ToolItem item)
            return;

        Delete(item);
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
