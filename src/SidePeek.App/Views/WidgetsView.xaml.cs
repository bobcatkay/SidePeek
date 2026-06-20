using System.Windows;
using System.Windows.Controls;
using SidePeek.App.Models;
using SidePeek.App.ViewModels;

namespace SidePeek.App.Views;

public partial class WidgetsView : UserControl
{
    private readonly WidgetsViewModel _viewModel = new();

    public WidgetsView()
    {
        InitializeComponent();
        DataContext = _viewModel;
    }

    private DockWindow? Host => Window.GetWindow(this) as DockWindow;

    private void OnLoaded(object sender, RoutedEventArgs e) => _viewModel.Resume();

    private void OnUnloaded(object sender, RoutedEventArgs e) => _viewModel.Pause();

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

    private void OnDeleteTool(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not ToolItem item)
            return;

        Host?.SuspendDock();
        try
        {
            var result = MessageBox.Show(
                $"确定要删除工具「{item.Title}」吗？", "删除确认",
                MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result == MessageBoxResult.Yes)
                _viewModel.Remove(item);
        }
        finally
        {
            Host?.ResumeDock();
        }
    }
}
