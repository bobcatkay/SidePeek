using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using SidePeek.App.Models;
using SidePeek.App.Services;

namespace SidePeek.App.ViewModels;

public class CommandsViewModel
{
    private const string FileName = "commands.json";

    public ObservableCollection<CommandItem> Commands { get; }

    public CommandsViewModel()
    {
        var loaded = JsonStore.Load(FileName, Defaults);
        Commands = new ObservableCollection<CommandItem>(loaded);
    }

    public void Add(CommandItem item)
    {
        Commands.Add(item);
        Persist();
    }

    public void Remove(CommandItem item)
    {
        Commands.Remove(item);
        Persist();
    }

    public void Persist() => JsonStore.Save(FileName, Commands.ToList());

    private static List<CommandItem> Defaults() => new()
    {
        new CommandItem
        {
            Title = "查看 IP 配置", Description = "ipconfig", Glyph = "\uE968", AccentHex = "#4C8DFF",
            CommandText = "ipconfig /all"
        },
        new CommandItem
        {
            Title = "清理临时文件", Description = "删除 %temp%", Glyph = "\uE74D", AccentHex = "#FF6B6B",
            CommandText = "del /q /s %temp%\\*"
        },
        new CommandItem
        {
            Title = "测试网络", Description = "ping", Glyph = "\uE945", AccentHex = "#3FD27F",
            CommandText = "ping 127.0.0.1 -n 2"
        },
    };
}
