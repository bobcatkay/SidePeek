using System;
using System.Threading.Tasks;
using System.Windows;
using SidePeek.App.Models;
using SidePeek.App.Services;

namespace SidePeek.App.Views;

public partial class CommandRunnerWindow : Window
{
    private readonly CommandItem _item;
    private readonly string[]? _commandLines;

    public CommandRunnerWindow(CommandItem item, string[]? commandLines = null)
    {
        InitializeComponent();
        _item = item;
        _commandLines = commandLines;
        HeaderText.Text = $"正在执行：{item.Title}";
        Loaded += async (_, _) => await RunAllAsync();
    }

    private async Task RunAllAsync()
    {
        string[] lines = _commandLines ?? _item.CommandLines;
        int index = 0;

        foreach (string line in lines)
        {
            index++;
            Append($"┌─ [{index}/{lines.Length}] > {line}{Environment.NewLine}");
            try
            {
                int code = await CommandExecutor.RunOneAsync(line, Append);
                Append($"└─ 退出码: {code}{Environment.NewLine}{Environment.NewLine}");
            }
            catch (Exception ex)
            {
                Append($"└─ 执行失败: {ex.Message}{Environment.NewLine}{Environment.NewLine}");
            }
        }

        Spinner.Visibility = Visibility.Collapsed;
        HeaderText.Text = $"执行完成：{_item.Title}";
    }

    private void Append(string text)
    {
        Dispatcher.Invoke(() =>
        {
            Output.AppendText(text);
            Scroller.ScrollToEnd();
        });
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
