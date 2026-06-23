using System;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using SidePeek.App.Models;

namespace SidePeek.App.Views;

public partial class CommandRunnerWindow : Window
{
    private readonly CommandItem _item;
    private static readonly Encoding ConsoleOutputEncoding = GetConsoleOutputEncoding();

    public CommandRunnerWindow(CommandItem item)
    {
        InitializeComponent();
        _item = item;
        HeaderText.Text = $"正在执行：{item.Title}";
        Loaded += async (_, _) => await RunAllAsync();
    }

    private async Task RunAllAsync()
    {
        string[] lines = _item.CommandLines;
        int index = 0;

        foreach (string line in lines)
        {
            index++;
            Append($"┌─ [{index}/{lines.Length}] > {line}{Environment.NewLine}");
            try
            {
                int code = await RunOneAsync(line);
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

    private Task<int> RunOneAsync(string commandLine)
    {
        var tcs = new TaskCompletionSource<int>();

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c " + commandLine,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = ConsoleOutputEncoding,
                StandardErrorEncoding = ConsoleOutputEncoding
            },
            EnableRaisingEvents = true
        };

        process.OutputDataReceived += (_, e) => { if (e.Data != null) Append(e.Data + Environment.NewLine); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) Append(e.Data + Environment.NewLine); };
        process.Exited += (_, _) =>
        {
            int code = process.ExitCode;
            process.Dispose();
            tcs.TrySetResult(code);
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return tcs.Task;
    }

    private void Append(string text)
    {
        Dispatcher.Invoke(() =>
        {
            Output.AppendText(text);
            Scroller.ScrollToEnd();
        });
    }

    private static Encoding GetConsoleOutputEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        uint codePage = SidePeek.App.Interop.NativeMethods.GetOEMCP();
        if (codePage == 0)
            return Encoding.UTF8;

        try
        {
            return Encoding.GetEncoding((int)codePage);
        }
        catch (ArgumentException)
        {
            return Encoding.UTF8;
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
