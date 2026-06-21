using System;

namespace SidePeek.App.Models;

public sealed class ClipboardItem
{
    public string Text { get; set; } = string.Empty;
    public DateTime CapturedAt { get; set; } = DateTime.Now;
}
