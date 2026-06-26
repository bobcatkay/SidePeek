using System.Windows;
using SidePeek.App.Models;
using SidePeek.App.ViewModels;

namespace SidePeek.App.Views;

public partial class CommandEditWindow : Window
{
    private readonly CommandItem _working;

    public CommandItem Result { get; private set; } = new();

    public CommandEditWindow(CommandItem? source = null)
    {
        InitializeComponent();

        _working = new CommandItem
        {
            Title = source?.Title ?? string.Empty,
            Description = source?.Description ?? string.Empty,
            SilentExecution = source?.SilentExecution ?? false,
            Glyph = source?.Glyph ?? Services.IconCatalog.Glyphs[0],
            AccentHex = source?.AccentHex ?? Services.IconCatalog.Accents[0],
            CommandText = source?.CommandText ?? string.Empty,
            Parameters = CommandsViewModel.CloneParameters(source?.Parameters)
        };

        HeaderText.Text = source is null ? "添加命令" : "编辑命令";
        DataContext = _working;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_working.Title))
        {
            ShowError("请填写标题");
            return;
        }
        if (string.IsNullOrWhiteSpace(_working.CommandText))
        {
            ShowError("请至少填写一条命令");
            return;
        }
        if (!ValidateParameters())
            return;

        Result = _working;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnAddParameter(object sender, RoutedEventArgs e)
    {
        _working.Parameters.Add(new CommandParameterDefinition
        {
            Label = $"参数 {_working.Parameters.Count + 1}"
        });
        HideError();
    }

    private void OnRemoveParameter(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is CommandParameterDefinition parameter)
            _working.Parameters.Remove(parameter);
    }

    private void OnAddChoice(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is CommandParameterDefinition parameter)
        {
            parameter.Choices.Add(new CommandParameterChoice
            {
                Label = $"选项 {parameter.Choices.Count + 1}",
                IsDefault = parameter.Choices.Count == 0
            });
            HideError();
        }
    }

    private void OnRemoveChoice(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not CommandParameterChoice choice)
            return;

        if ((sender as FrameworkElement)?.Tag is CommandParameterDefinition parameter)
            parameter.Choices.Remove(choice);
    }

    private void OnDefaultChoiceChecked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not CommandParameterChoice selected ||
            (sender as FrameworkElement)?.Tag is not CommandParameterDefinition parameter)
        {
            return;
        }

        foreach (CommandParameterChoice choice in parameter.Choices)
        {
            if (!ReferenceEquals(choice, selected))
                choice.IsDefault = false;
        }
    }

    private bool ValidateParameters()
    {
        for (int i = 0; i < _working.Parameters.Count; i++)
        {
            CommandParameterDefinition parameter = _working.Parameters[i];
            if (string.IsNullOrWhiteSpace(parameter.Label))
                parameter.Label = $"参数 {i + 1}";

            if (parameter.Mode != CommandParameterMode.Choices)
                continue;

            if (parameter.Choices.Count == 0)
            {
                ShowError($"参数「{parameter.Label}」需要至少一个候选值");
                return false;
            }

            foreach (CommandParameterChoice choice in parameter.Choices)
            {
                if (string.IsNullOrWhiteSpace(choice.Label) || string.IsNullOrWhiteSpace(choice.Value))
                {
                    ShowError($"参数「{parameter.Label}」的候选值需要填写标签和值");
                    return false;
                }
            }

            NormalizeDefaultChoice(parameter);
        }

        return true;
    }

    private static void NormalizeDefaultChoice(CommandParameterDefinition parameter)
    {
        bool foundDefault = false;
        foreach (CommandParameterChoice choice in parameter.Choices)
        {
            if (!choice.IsDefault)
                continue;

            if (!foundDefault)
            {
                foundDefault = true;
                continue;
            }

            choice.IsDefault = false;
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void HideError() => ErrorText.Visibility = Visibility.Collapsed;
}
