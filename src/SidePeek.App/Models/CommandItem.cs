using System.Text.Json.Serialization;
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

    [JsonIgnore]
    public string[] CommandLines =>
        CommandText.Replace("\r\n", "\n").Split('\n', System.StringSplitOptions.RemoveEmptyEntries);
}
