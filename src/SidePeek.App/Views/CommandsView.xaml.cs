using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SidePeek.App.Models;
using SidePeek.App.Services;
using SidePeek.App.ViewModels;

namespace SidePeek.App.Views;

public partial class CommandsView : UserControl
{
    private readonly CommandsViewModel _viewModel = new();
    private readonly ObservableCollection<RuntimeCommandParameter> _runtimeParameters = new();
    private Point _dragStart;
    private CommandItem? _dragItem;
    private CommandItem? _pendingRunItem;

    public CommandsView()
    {
        InitializeComponent();
        DataContext = _viewModel;
        ParameterPromptList.ItemsSource = _runtimeParameters;
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
        if ((sender as FrameworkElement)?.DataContext is not CommandItem item)
            return;

        if (item.Parameters.Count > 0)
        {
            ShowParameterPrompt(item, (FrameworkElement)sender);
            return;
        }

        RunCommand(item);
    }

    private void RunCommand(CommandItem item, string[]? commandLines = null)
    {
        Host?.SuspendDock();
        try
        {
            var runner = new CommandRunnerWindow(item, commandLines) { Owner = Host };
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

    private void ShowParameterPrompt(CommandItem item, FrameworkElement target)
    {
        if (ParameterPromptLayer.Visibility == Visibility.Visible)
            HideParameterPrompt();

        _pendingRunItem = item;
        _runtimeParameters.Clear();
        for (int i = 0; i < item.Parameters.Count; i++)
            _runtimeParameters.Add(new RuntimeCommandParameter(item.Parameters[i], i + 1));

        ParameterPromptTitle.Text = "参数设置";
        ParameterPromptError.Visibility = Visibility.Collapsed;
        ApplyParameterPromptColors();
        ParameterPromptLayer.Visibility = Visibility.Visible;
        Host?.SuspendDock();
        PositionParameterPrompt(target);
    }

    private void PositionParameterPrompt(FrameworkElement target)
    {
        CommandRoot.UpdateLayout();
        ParameterPromptPanel.Margin = new Thickness(0);
        ParameterPromptPanel.Measure(new Size(ParameterPromptPanel.Width, double.PositiveInfinity));

        double panelHeight = ParameterPromptPanel.DesiredSize.Height;
        Point targetPoint = target.TransformToAncestor(CommandRoot).Transform(new Point(0, 0));
        const double gap = 8;

        double belowTop = targetPoint.Y + target.ActualHeight + gap;
        double aboveTop = targetPoint.Y - panelHeight - gap;
        double top = belowTop + panelHeight <= CommandRoot.ActualHeight - gap
            ? belowTop
            : aboveTop;

        double maxTop = Math.Max(gap, CommandRoot.ActualHeight - panelHeight - gap);
        top = Math.Clamp(top, gap, maxTop);

        ParameterPromptPanel.Margin = new Thickness(0, top, 0, 0);
    }

    private void OnCancelParameterPrompt(object sender, RoutedEventArgs e) => HideParameterPrompt();

    private void OnConfirmParameterPrompt(object sender, RoutedEventArgs e)
    {
        if (_pendingRunItem is null)
        {
            HideParameterPrompt();
            return;
        }

        var values = new string[_runtimeParameters.Count];
        for (int i = 0; i < _runtimeParameters.Count; i++)
        {
            RuntimeCommandParameter parameter = _runtimeParameters[i];
            if (parameter.RequiresChoice && parameter.SelectedChoice is null)
            {
                ShowParameterPromptError($"请选择 {parameter.Label}");
                return;
            }

            values[i] = parameter.GetValue();
        }

        CommandItem item = _pendingRunItem;
        string[] commandLines = item.BuildCommandLines(values);
        HideParameterPrompt(resumeDock: false);

        try
        {
            var runner = new CommandRunnerWindow(item, commandLines) { Owner = Host };
            runner.ShowDialog();
        }
        finally
        {
            Host?.ResumeDock();
        }
    }

    private void HideParameterPrompt(bool resumeDock = true)
    {
        ParameterPromptLayer.Visibility = Visibility.Collapsed;
        _pendingRunItem = null;
        _runtimeParameters.Clear();
        if (resumeDock)
            Host?.ResumeDock();
    }

    private void ShowParameterPromptError(string message)
    {
        ParameterPromptError.Text = message;
        ParameterPromptError.Visibility = Visibility.Visible;
    }

    private void ApplyParameterPromptColors()
    {
        bool isLightTheme = ThemeService.IsLightTheme(SettingsService.Current.Theme);
        ParameterPromptPanel.Background = new SolidColorBrush(isLightTheme
            ? Colors.White
            : Color.FromRgb(0x24, 0x28, 0x30));
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

    private sealed class RuntimeCommandParameter
    {
        private readonly CommandParameterDefinition _definition;

        public RuntimeCommandParameter(CommandParameterDefinition definition, int index)
        {
            _definition = definition;
            string label = string.IsNullOrWhiteSpace(definition.Label)
                ? $"参数 {index}"
                : definition.Label;
            Label = $"%{index}  {label}";
            Value = definition.Mode == CommandParameterMode.Prompt
                ? definition.PromptDefaultValue
                : string.Empty;
            SelectedChoice = definition.Mode == CommandParameterMode.Choices
                ? definition.Choices.FirstOrDefault(choice => choice.IsDefault) ?? definition.Choices.FirstOrDefault()
                : null;
        }

        public string Label { get; }
        public string Value { get; set; }
        public CommandParameterChoice? SelectedChoice { get; set; }
        public ObservableCollection<CommandParameterChoice> Choices => _definition.Choices;
        public bool RequiresChoice => _definition.Mode == CommandParameterMode.Choices;
        public Visibility InputVisibility => _definition.Mode == CommandParameterMode.Prompt
            ? Visibility.Visible
            : Visibility.Collapsed;
        public Visibility ChoiceVisibility => _definition.Mode == CommandParameterMode.Choices
            ? Visibility.Visible
            : Visibility.Collapsed;

        public string GetValue() => _definition.Mode == CommandParameterMode.Choices
            ? SelectedChoice?.Value ?? string.Empty
            : Value;
    }
}
