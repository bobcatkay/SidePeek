using CommunityToolkit.Mvvm.ComponentModel;

namespace SidePeek.App.Models;

public partial class ToolItem : ObservableObject
{
    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _description = string.Empty;

    [ObservableProperty]
    private string _glyph = "\uE7AC";

    [ObservableProperty]
    private string _accentHex = "#3FD27F";

    [ObservableProperty]
    private string _exePath = string.Empty;

    [ObservableProperty]
    private string _arguments = string.Empty;
}
