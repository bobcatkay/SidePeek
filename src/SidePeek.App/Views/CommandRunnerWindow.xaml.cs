using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using SidePeek.App.Models;
using SidePeek.App.Services;

namespace SidePeek.App.Views;

public partial class CommandRunnerWindow : Window
{
    private readonly CommandItem _item;
    private readonly string[]? _commandLines;
    private CancellationTokenSource? _commandCancellation;
    private bool _isRunning;
    private bool _closeWhenStopped;
    private bool _allowClose;

    public CommandRunnerWindow(CommandItem item, string[]? commandLines = null)
    {
        InitializeComponent();
        _item = item;
        _commandLines = commandLines;
        HeaderText.Text = $"正在执行：{item.Title}";
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_isRunning)
            return;

        var cancellation = new CancellationTokenSource();
        _commandCancellation = cancellation;
        _isRunning = true;
        InterruptButton.IsEnabled = true;

        bool interrupted = false;
        try
        {
            interrupted = await RunAllAsync(cancellation.Token);
        }
        finally
        {
            _isRunning = false;
            InterruptButton.IsEnabled = false;
            Spinner.Visibility = Visibility.Collapsed;
            HeaderText.Text = interrupted
                ? $"已中断：{_item.Title}"
                : $"执行完成：{_item.Title}";

            if (ReferenceEquals(_commandCancellation, cancellation))
                _commandCancellation = null;

            cancellation.Dispose();

            if (_closeWhenStopped)
            {
                _allowClose = true;
                Close();
            }
        }
    }

    private async Task<bool> RunAllAsync(CancellationToken cancellationToken)
    {
        string[] lines = _commandLines ?? _item.CommandLines;
        int index = 0;

        foreach (string line in lines)
        {
            if (cancellationToken.IsCancellationRequested)
                return true;

            index++;
            Append($"┌─ [{index}/{lines.Length}] > {line}{Environment.NewLine}");
            try
            {
                int code = await CommandExecutor.RunOneAsync(line, Append, cancellationToken);
                Append($"└─ 退出码: {code}{Environment.NewLine}{Environment.NewLine}");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Append($"└─ 已中断{Environment.NewLine}{Environment.NewLine}");
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Error("Command execution failed.", ex);
                Append($"└─ 执行失败: {ex.Message}{Environment.NewLine}{Environment.NewLine}");
            }
        }

        return cancellationToken.IsCancellationRequested;
    }

    private void Append(string text)
    {
        Dispatcher.Invoke(() =>
        {
            Output.AppendText(text);
            Scroller.ScrollToEnd();
        });
    }

    private void OnInterrupt(object sender, RoutedEventArgs e) => RequestInterruption();

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose || !_isRunning)
            return;

        // Keep the window alive until process-tree termination has completed so no output
        // callback can target disposed UI and no command is left running in the background.
        e.Cancel = true;
        _closeWhenStopped = true;
        CloseButton.IsEnabled = false;
        RequestInterruption();
    }

    private void RequestInterruption()
    {
        if (!_isRunning || _commandCancellation is null || _commandCancellation.IsCancellationRequested)
            return;

        AppLogger.Info("Command interruption requested.");
        InterruptButton.IsEnabled = false;
        HeaderText.Text = $"正在中断：{_item.Title}";
        _commandCancellation.Cancel();
    }
}
