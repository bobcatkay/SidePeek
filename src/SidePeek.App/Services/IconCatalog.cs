using System.Collections.Generic;

namespace SidePeek.App.Services;

/// <summary>可供用户选择的 Segoe Fluent 字形图标与强调色。</summary>
public static class IconCatalog
{
    public static IReadOnlyList<string> Glyphs { get; } = new[]
    {
        "\uE756", // 终端
        "\uE70B", // 记事
        "\uE1D0", // 计算
        "\uEC50", // 此电脑
        "\uE774", // 网页
        "\uE896", // 下载
        "\uE7AC", // 应用
        "\uE713", // 设置
        "\uE722", // 邮件
        "\uE787", // 日历
        "\uE8A5", // 文档
        "\uE8B7", // 文件夹
        "\uE909", // 世界
        "\uE945", // 闪电
        "\uE90F", // 播放
        "\uE72C", // 刷新
    };

    public static IReadOnlyList<string> Accents { get; } = new[]
    {
        "#4C8DFF",
        "#3FD27F",
        "#FFB454",
        "#FF6B6B",
        "#B888FF",
        "#36C5D6",
        "#F2784B",
        "#7E8CE0",
    };
}
