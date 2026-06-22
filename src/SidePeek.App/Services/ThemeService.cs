using Microsoft.Win32;
using SidePeek.App.Models;
using Wpf.Ui.Appearance;

namespace SidePeek.App.Services;

public static class ThemeService
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public static void Apply(AppThemeMode mode)
    {
        ApplicationTheme theme = IsLightTheme(mode) ? ApplicationTheme.Light : ApplicationTheme.Dark;

        ApplicationThemeManager.Apply(theme);
    }

    public static bool IsLightTheme(AppThemeMode mode) => mode switch
    {
        AppThemeMode.Dark => false,
        AppThemeMode.System => SystemPrefersLight(),
        _ => true
    };

    private static bool SystemPrefersLight()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            object? value = key?.GetValue("AppsUseLightTheme");
            return value is not int appsUseLightTheme || appsUseLightTheme != 0;
        }
        catch
        {
            return true;
        }
    }
}
