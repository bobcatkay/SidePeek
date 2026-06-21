using System;
using System.IO;

namespace SidePeek.App.Services;

public static class AppLogger
{
    private static string LogDir
    {
        get
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "SidePeek",
                "logs");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message, Exception? exception = null)
    {
        string details = exception is null ? message : $"{message}{Environment.NewLine}{exception}";
        Write("ERROR", details);
    }

    private static void Write(string level, string message)
    {
        try
        {
            string path = Path.Combine(LogDir, $"sidepeek-{DateTime.Now:yyyyMMdd}.log");
            File.AppendAllText(path, $"[{DateTime.Now:HH:mm:ss}] {level} {message}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never break the desktop tool itself.
        }
    }
}
