using System;
using System.IO;
using System.Text.Json;

namespace SidePeek.App.Services;

/// <summary>极简 JSON 持久化：数据存放在 %AppData%\SidePeek\。</summary>
public static class JsonStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true
    };

    private static string Dir
    {
        get
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "SidePeek");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static T Load<T>(string fileName, Func<T> fallback)
    {
        try
        {
            string path = Path.Combine(Dir, fileName);
            if (!File.Exists(path))
                return fallback();
            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<T>(json, Options) ?? fallback();
        }
        catch
        {
            return fallback();
        }
    }

    public static void Save<T>(string fileName, T value)
    {
        try
        {
            string path = Path.Combine(Dir, fileName);
            File.WriteAllText(path, JsonSerializer.Serialize(value, Options));
        }
        catch
        {
            // 忽略持久化失败，不影响使用
        }
    }
}
