using System.Windows;
using SidePeek.App.Models;

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
            Glyph = source?.Glyph ?? Services.IconCatalog.Glyphs[0],
            AccentHex = source?.AccentHex ?? Services.IconCatalog.Accents[0],
            CommandText = source?.CommandText ?? string.Empty
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

        Result = _working;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
