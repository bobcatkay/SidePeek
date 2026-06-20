using System.Windows;
using System.Windows.Controls;
using SidePeek.App.Models;
using SidePeek.App.ViewModels;

namespace SidePeek.App.Views;

public partial class CommandsView : UserControl
{
    private readonly CommandsViewModel _viewModel = new();

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

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not CommandItem item)
            return;

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
}
