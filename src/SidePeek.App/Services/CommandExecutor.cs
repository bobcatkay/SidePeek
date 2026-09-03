using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SidePeek.App.Services;

public static class CommandExecutor
{
    private static readonly Encoding ConsoleOutputEncoding = GetConsoleOutputEncoding();

    public static async Task<int> RunOneAsync(
        string commandLine,
        Action<string>? output = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var process = new Process
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

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                output?.Invoke(e.Data + Environment.NewLine);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                output?.Invoke(e.Data + Environment.NewLine);
        };
        process.Start();
        AppLogger.Info($"Command process started. ProcessId={process.Id}.");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // Cancelling the wait alone leaves cmd.exe and any program it launched running.
        // Terminating the entire tree keeps interrupt and window-close semantics consistent.
        using CancellationTokenRegistration cancellationRegistration = cancellationToken.Register(
            static state => TerminateProcessTree((Process)state!),
            process);

        await process.WaitForExitAsync();
        await Task.Run(process.WaitForExit);
        cancellationToken.ThrowIfCancellationRequested();
        AppLogger.Info($"Command process exited. ProcessId={process.Id}, ExitCode={process.ExitCode}.");
        return process.ExitCode;
    }

    private static void TerminateProcessTree(Process process)
    {
        try
        {
            if (process.HasExited)
                return;

            AppLogger.Info($"Terminating command process tree. ProcessId={process.Id}.");
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // The process exited between the HasExited check and Kill.
        }
        catch (Exception ex)
        {
            AppLogger.Error("Unable to terminate command process tree.", ex);
        }
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
}
