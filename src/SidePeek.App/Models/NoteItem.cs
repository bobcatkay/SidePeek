using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SidePeek.App.Models;

public partial class NoteItem : ObservableObject
{
    [ObservableProperty]
    private string _title = "新便签";

    [ObservableProperty]
    private string _content = string.Empty;

    [ObservableProperty]
    private string _colorHex = "#FFB454";

    [ObservableProperty]
    private bool _isPinned;

    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}
