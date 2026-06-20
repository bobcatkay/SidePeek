using System.Windows;
using Microsoft.Win32;
using SidePeek.App.Models;

namespace SidePeek.App.Views;

public partial class ToolEditWindow : Window
{
    private readonly ToolItem _working;

    public ToolItem Result { get; private set; } = new();

    public ToolEditWindow(ToolItem? source = null)
    {
        InitializeComponent();

        _working = new ToolItem
        {
            Title = source?.Title ?? string.Empty,
            Description = source?.Description ?? string.Empty,
            Glyph = source?.Glyph ?? Services.IconCatalog.Glyphs[6],
            AccentHex = source?.AccentHex ?? Services.IconCatalog.Accents[1],
            ExePath = source?.ExePath ?? string.Empty,
            Arguments = source?.Arguments ?? string.Empty
        };

        HeaderText.Text = source is null ? "添加工具" : "编辑工具";
        DataContext = _working;
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择可执行文件",
            Filter = "程序 (*.exe)|*.exe|所有文件 (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == true)
        {
            _working.ExePath = dialog.FileName;
            if (string.IsNullOrWhiteSpace(_working.Title))
                _working.Title = System.IO.Path.GetFileNameWithoutExtension(dialog.FileName);
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_working.Title))
        {
            ShowError("请填写标题");
            return;
        }
        if (string.IsNullOrWhiteSpace(_working.ExePath))
        {
            ShowError("请选择或填写 exe 路径");
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
