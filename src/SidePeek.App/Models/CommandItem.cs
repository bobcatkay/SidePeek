using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SidePeek.App.Models;

public partial class CommandItem : ObservableObject
{
    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _description = string.Empty;

    [ObservableProperty]
    private string _glyph = "\uE756";

    [ObservableProperty]
    private string _accentHex = "#4C8DFF";

    /// <summary>多条命令，每行一条，按顺序执行。</summary>
    [ObservableProperty]
    private string _commandText = string.Empty;

    [ObservableProperty]
    private ObservableCollection<CommandParameterDefinition> _parameters = new();

    [JsonIgnore]
    public string[] CommandLines =>
        CommandText.Replace("\r\n", "\n").Split('\n', System.StringSplitOptions.RemoveEmptyEntries);

    public string[] BuildCommandLines(IReadOnlyList<string> parameterValues) =>
        CommandLines.Select(line => ReplaceParameters(line, parameterValues)).ToArray();

    private static string ReplaceParameters(string commandLine, IReadOnlyList<string> parameterValues) =>
        Regex.Replace(commandLine, "%(\\d+)", match =>
        {
            if (!int.TryParse(match.Groups[1].Value, out int index))
                return match.Value;

            return index >= 1 && index <= parameterValues.Count
                ? parameterValues[index - 1]
                : match.Value;
        });
}

public enum CommandParameterMode
{
    Prompt,
    Choices
}

public partial class CommandParameterDefinition : ObservableObject
{
    [ObservableProperty]
    private string _label = string.Empty;

    [ObservableProperty]
    private string _promptDefaultValue = string.Empty;

    [ObservableProperty]
    private CommandParameterMode _mode = CommandParameterMode.Prompt;

    [ObservableProperty]
    private ObservableCollection<CommandParameterChoice> _choices = new();

    [JsonIgnore]
    public bool IsPrompt
    {
        get => Mode == CommandParameterMode.Prompt;
        set
        {
            if (value)
                Mode = CommandParameterMode.Prompt;
        }
    }

    [JsonIgnore]
    public bool UsesChoices
    {
        get => Mode == CommandParameterMode.Choices;
        set
        {
            if (value)
                Mode = CommandParameterMode.Choices;
        }
    }

    partial void OnModeChanged(CommandParameterMode value)
    {
        if (value == CommandParameterMode.Choices && Choices.Count == 0)
        {
            Choices.Add(new CommandParameterChoice { Label = "选项 1", IsDefault = true });
            Choices.Add(new CommandParameterChoice { Label = "选项 2" });
        }

        OnPropertyChanged(nameof(IsPrompt));
        OnPropertyChanged(nameof(UsesChoices));
    }
}

public partial class CommandParameterChoice : ObservableObject
{
    [ObservableProperty]
    private string _label = string.Empty;

    [ObservableProperty]
    private string _value = string.Empty;

    [ObservableProperty]
    private bool _isDefault;
}
