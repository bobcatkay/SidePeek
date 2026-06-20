using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using SidePeek.App.Interop;
using SidePeek.App.Models;
using SidePeek.App.Services;

namespace SidePeek.App.ViewModels;

public partial class WidgetsViewModel : ObservableObject, IDisposable
{
    private const string FileName = "tools.json";
    private readonly DispatcherTimer _timer;

    [ObservableProperty] private string _time = string.Empty;
    [ObservableProperty] private string _date = string.Empty;
    [ObservableProperty] private double _memoryUsedPercent;
    [ObservableProperty] private string _memoryText = string.Empty;

    public ObservableCollection<ToolItem> Tools { get; }

    public WidgetsViewModel()
    {
        Tools = new ObservableCollection<ToolItem>(JsonStore.Load(FileName, Defaults));

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Refresh();
    }

    public void Resume()
    {
        Refresh();
        _timer.Start();
    }

    public void Pause() => _timer.Stop();

    public void Add(ToolItem item)
    {
        Tools.Add(item);
        Persist();
    }

    public void Remove(ToolItem item)
    {
        Tools.Remove(item);
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

    private void Refresh()
    {
        DateTime now = DateTime.Now;
        Time = now.ToString("HH:mm:ss");
        Date = now.ToString("yyyy年M月d日 dddd");

        var mem = new NativeMethods.MEMORYSTATUSEX
        {
            dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>()
        };
        if (NativeMethods.GlobalMemoryStatusEx(ref mem))
        {
            MemoryUsedPercent = mem.dwMemoryLoad;
            double totalGb = mem.ullTotalPhys / 1024d / 1024d / 1024d;
            double usedGb = (mem.ullTotalPhys - mem.ullAvailPhys) / 1024d / 1024d / 1024d;
            MemoryText = $"{usedGb:0.0} / {totalGb:0.0} GB";
        }
    }

    private static List<ToolItem> Defaults() => new()
    {
        new ToolItem { Title = "记事本", Description = "notepad", Glyph = "\uE70B", AccentHex = "#4C8DFF", ExePath = "notepad.exe" },
        new ToolItem { Title = "计算器", Description = "calc", Glyph = "\uE1D0", AccentHex = "#3FD27F", ExePath = "calc.exe" },
        new ToolItem { Title = "画图", Description = "mspaint", Glyph = "\uE790", AccentHex = "#FFB454", ExePath = "mspaint.exe" },
        new ToolItem { Title = "任务管理器", Description = "taskmgr", Glyph = "\uE9D9", AccentHex = "#B888FF", ExePath = "taskmgr.exe" },
    };

    public void Dispose() => _timer.Stop();
}
