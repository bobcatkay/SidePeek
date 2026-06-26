using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using SidePeek.App.Models;
using SidePeek.App.Services;
using SidePeek.App.ViewModels;

namespace SidePeek.App.Views;

public partial class CommandsView : UserControl
{
    private readonly CommandsViewModel _viewModel = new();
    private readonly ObservableCollection<RuntimeCommandParameter> _runtimeParameters = new();
    private readonly DispatcherTimer _toastTimer;
    private Point _dragStart;
    private DragSortController<CommandItem> _dragSort = null!;
    private bool _suppressItemClick;
    private CommandItem? _pendingRunItem;
    private CommandItem? _pendingDeleteItem;

    public CommandsView()
    {
        InitializeComponent();
        DataContext = _viewModel;
        ParameterPromptList.ItemsSource = _runtimeParameters;
        _dragSort = new DragSortController<CommandItem>(
            CommandRoot,
            DragOverlay,
            CommandsItemsControl,
            (source, target) => _viewModel.Move(source, target));

        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1400) };
        _toastTimer.Tick += (_, _) => HideCommandResultToast();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _toastTimer.Stop();
        CommandResultToast.Visibility = Visibility.Collapsed;
        HideDeleteConfirm();
        _dragSort.Cancel();
        Host?.ResumeDock();
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

        RunOrPrompt(item, (FrameworkElement)sender);
    }

    private void RunOrPrompt(CommandItem item, FrameworkElement target)
    {
        if (item.IsRunning)
            return;

        if (item.Parameters.Count > 0)
        {
            ShowParameterPrompt(item, target);
            return;
        }

        RunCommand(item);
    }

    private async void RunCommand(CommandItem item, string[]? commandLines = null)
    {
        if (item.SilentExecution)
        {
            await RunCommandSilentlyAsync(item, commandLines);
            return;
        }

        ShowCommandRunner(item, commandLines);
    }

    private void ShowCommandRunner(CommandItem item, string[]? commandLines = null)
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

        if (item.IsRunning)
        {
            e.Handled = true;
            return;
        }

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
        if (item.IsRunning)
            return;

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

    private async Task RunCommandSilentlyAsync(CommandItem item, string[]? commandLines = null)
    {
        if (item.IsRunning)
            return;

        Host?.SuspendDock();

        bool succeeded = true;
        string[] lines = commandLines ?? item.CommandLines;

        item.IsRunning = true;
        try
        {
            foreach (string line in lines)
            {
                try
                {
                    int code = await CommandExecutor.RunOneAsync(line);
                    if (code != 0)
                        succeeded = false;
                }
                catch
                {
                    succeeded = false;
                }
            }
        }
        finally
        {
            item.IsRunning = false;
        }

        if (IsLoaded)
            ShowCommandResultToast(succeeded);
        else
            Host?.ResumeDock();
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

    private async void OnConfirmParameterPrompt(object sender, RoutedEventArgs e)
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

        if (item.SilentExecution)
            await RunCommandSilentlyAsync(item, commandLines);
        else
            ShowCommandRunner(item, commandLines);
    }

    private void ShowCommandResultToast(bool succeeded)
    {
        _toastTimer.Stop();

        CommandResultToastText.Text = succeeded ? "执行成功" : "执行失败";
        CommandSuccessToastIcon.Visibility = succeeded ? Visibility.Visible : Visibility.Collapsed;
        CommandFailureToastIcon.Visibility = succeeded ? Visibility.Collapsed : Visibility.Visible;
        CommandResultToast.Visibility = Visibility.Visible;

        var opacityAnimation = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        CommandResultToast.BeginAnimation(OpacityProperty, opacityAnimation);

        if (CommandResultToast.RenderTransform is TranslateTransform transform)
        {
            var yAnimation = new DoubleAnimation(-8, 0, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            transform.BeginAnimation(TranslateTransform.YProperty, yAnimation);
        }

        _toastTimer.Start();
    }

    private void HideCommandResultToast()
    {
        _toastTimer.Stop();

        var opacityAnimation = new DoubleAnimation(CommandResultToast.Opacity, 0, TimeSpan.FromMilliseconds(160))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
        };
        opacityAnimation.Completed += (_, _) =>
        {
            CommandResultToast.Visibility = Visibility.Collapsed;
            Host?.ResumeDock();
        };
        CommandResultToast.BeginAnimation(OpacityProperty, opacityAnimation);

        if (CommandResultToast.RenderTransform is TranslateTransform transform)
        {
            var yAnimation = new DoubleAnimation(0, -6, TimeSpan.FromMilliseconds(160))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
            };
            transform.BeginAnimation(TranslateTransform.YProperty, yAnimation);
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
        if (item.IsRunning)
            return;

        _pendingDeleteItem = item;
        string title = string.IsNullOrWhiteSpace(item.Title) ? "这条命令" : $"命令「{item.Title}」";
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
        if (((FrameworkElement)sender).DataContext is CommandItem { IsRunning: true })
        {
            _dragSort.Cancel();
            e.Handled = true;
            return;
        }

        if (IsButtonElement(e.OriginalSource as DependencyObject))
        {
            _dragSort.Cancel();
            return;
        }

        _suppressItemClick = false;
        _dragStart = e.GetPosition(this);
        if (((FrameworkElement)sender).DataContext is CommandItem item)
            _dragSort.BeginCandidate(item, (FrameworkElement)sender, e.GetPosition(CommandRoot));
    }

    private void OnItemMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
            return;

        Point current = e.GetPosition(this);
        if (Math.Abs(current.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        if (_dragSort.TryStart(e.GetPosition(CommandRoot)))
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

        if (((FrameworkElement)sender).DataContext is CommandItem item)
        {
            if (item.IsRunning)
            {
                e.Handled = true;
                return;
            }

            RunOrPrompt(item, (FrameworkElement)sender);
            e.Handled = true;
        }
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
