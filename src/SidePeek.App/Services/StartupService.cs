using System;
using Microsoft.Win32;

namespace SidePeek.App.Services;

/// <summary>通过 HKCU Run 项管理开机自启。</summary>
public static class StartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "SidePeek";

    private static string ExePath => Environment.ProcessPath ?? string.Empty;

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey);
                string? value = key?.GetValue(ValueName) as string;
                return !string.IsNullOrEmpty(value);
            }
            catch
            {
                return false;
            }
        }
        set
        {
            try
            {
                using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey);
                if (value)
                    key.SetValue(ValueName, $"\"{ExePath}\"");
                else
                    key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            catch
            {
                // 忽略权限/IO 失败
            }
        }
    }
}
