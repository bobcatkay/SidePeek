using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using SidePeek.App.Models;
using SidePeek.App.Services;

namespace SidePeek.App.ViewModels;

public partial class WidgetsViewModel : ObservableObject
{
    private const string FileName = "tools.json";

    public ObservableCollection<ToolItem> Tools { get; }

    public WidgetsViewModel()
    {
        Tools = new ObservableCollection<ToolItem>(JsonStore.Load(FileName, Defaults));
    }

    public void Add(ToolItem item)
    {
        Tools.Add(item);
        Persist();
    }

    public void Update(ToolItem target, ToolItem source)
    {
        target.Title = source.Title;
        target.Description = source.Description;
        target.Glyph = source.Glyph;
        target.AccentHex = source.AccentHex;
        target.ExePath = source.ExePath;
        target.Arguments = source.Arguments;
        Persist();
    }

    public void Remove(ToolItem item)
    {
        Tools.Remove(item);
        Persist();
    }

    public void MoveToTop(ToolItem item)
    {
        int index = Tools.IndexOf(item);
        if (index > 0)
        {
            Tools.Move(index, 0);
            Persist();
        }
    }

    public void Move(ToolItem source, ToolItem target)
    {
        if (ReferenceEquals(source, target))
            return;

        int oldIndex = Tools.IndexOf(source);
        int newIndex = Tools.IndexOf(target);
        if (oldIndex < 0 || newIndex < 0 || oldIndex == newIndex)
            return;

        Tools.Move(oldIndex, newIndex);
        Persist();
    }

    public void Persist() => JsonStore.Save(FileName, Tools.ToList());

    public void Launch(ToolItem item)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = item.ExePath,
                Arguments = item.Arguments,
                UseShellExecute = true
            };
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"无法启动「{item.Title}」：\n{ex.Message}", "SidePeek",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static List<ToolItem> Defaults() => new()
    {
        new ToolItem { Title = "记事本", Description = "notepad", Glyph = "\uE70B", AccentHex = "#4C8DFF", ExePath = "notepad.exe" },
        new ToolItem { Title = "计算器", Description = "calc", Glyph = "\uE1D0", AccentHex = "#3FD27F", ExePath = "calc.exe" },
        new ToolItem { Title = "画图", Description = "mspaint", Glyph = "\uE790", AccentHex = "#FFB454", ExePath = "mspaint.exe" },
        new ToolItem { Title = "任务管理器", Description = "taskmgr", Glyph = "\uE9D9", AccentHex = "#B888FF", ExePath = "taskmgr.exe" },
    };

}
