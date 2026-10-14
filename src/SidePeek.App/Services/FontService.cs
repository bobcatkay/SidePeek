using System.Globalization;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;

namespace SidePeek.App.Services;

public static class FontService
{
    private const string LogTag = nameof(FontService);
    private const string AppFontResourceKey = "AppFontFamily";
    private const string AppDisplayFontResourceKey = "AppDisplayFontFamily";
    private const string DefaultFontResourceKey = "DefaultAppFontFamily";
    private const string DefaultDisplayFontResourceKey = "DefaultAppDisplayFontFamily";
    private const string ControlFontResourceKey = "ContentControlThemeFontFamily";

    private static readonly Lazy<IReadOnlyList<FontFamily>> InstalledFamilies = new(LoadInstalledFamilies);
    private static string? _appliedFamilyName;

    public static IReadOnlyList<FontFamily> AvailableFamilies => InstalledFamilies.Value;

    public static string NormalizeFamilyName(string? familyName)
    {
        string requested = (familyName ?? string.Empty).Trim();
        if (requested.Length == 0)
            return string.Empty;

        FontFamily? family = FindFamily(requested);
        if (family is not null)
            return family.Source;

        // 配置可能来自另一台电脑，或字体已卸载；回到默认项，避免显示与选择不一致。
        AppLogger.Info($"{LogTag}: configured font is unavailable; using the default font.");
        return string.Empty;
    }

    public static string GetDisplayName(FontFamily family)
    {
        for (CultureInfo culture = CultureInfo.CurrentUICulture;
             !culture.Equals(CultureInfo.InvariantCulture);
             culture = culture.Parent)
        {
            if (family.FamilyNames.TryGetValue(XmlLanguage.GetLanguage(culture.IetfLanguageTag), out string name))
                return name;
        }

        return family.Source;
    }

    public static void Apply(string? familyName)
    {
        string normalized = NormalizeFamilyName(familyName);
        if (string.Equals(_appliedFamilyName, normalized, StringComparison.Ordinal))
            return;

        ResourceDictionary resources = Application.Current.Resources;
        FontFamily? selected = normalized.Length == 0 ? null : FindFamily(normalized);
        FontFamily textFont = selected ?? (FontFamily)resources[DefaultFontResourceKey];
        FontFamily displayFont = selected ?? (FontFamily)resources[DefaultDisplayFontResourceKey];

        // 顶层动态资源会更新已打开的窗口，并在 WPF-UI 替换主题字典时保留。
        // 图标与命令输出继续使用各自的专用字体，不参与正文资源替换。
        resources[AppFontResourceKey] = textFont;
        resources[AppDisplayFontResourceKey] = displayFont;
        resources[ControlFontResourceKey] = textFont;
        _appliedFamilyName = normalized;
        AppLogger.Info($"{LogTag}: applied {(selected is null ? "default" : "installed")} UI font.");
    }

    private static FontFamily? FindFamily(string familyName) => AvailableFamilies.FirstOrDefault(family =>
        string.Equals(family.Source, familyName, StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<FontFamily> LoadInstalledFamilies()
    {
        try
        {
            // 缓存 WPF 可用的系统字体，避免每次设置变更都重新扫描系统字体集合。
            FontFamily[] families = Fonts.SystemFontFamilies
                .Where(family => !string.IsNullOrWhiteSpace(family.Source))
                .DistinctBy(family => family.Source, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            AppLogger.Info($"{LogTag}: loaded {families.Length} installed font families.");
            return families;
        }
        catch (Exception exception)
        {
            AppLogger.Error($"{LogTag}: could not enumerate installed fonts; keeping the default option.", exception);
            return Array.Empty<FontFamily>();
        }
    }
}
