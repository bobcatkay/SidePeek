using System;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;

namespace SidePeek.App.Services;

public static class CommandExecutor
{
    private static readonly Encoding ConsoleOutputEncoding = GetConsoleOutputEncoding();

    public static async Task<int> RunOneAsync(string commandLine, Action<string>? output = null)
    {
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
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync();
        await Task.Run(process.WaitForExit);
        return process.ExitCode;
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
