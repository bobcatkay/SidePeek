using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SidePeek.App.Interop;
using SidePeek.App.Models;
using SidePeek.App.Services;

namespace SidePeek.App.ViewModels;

public partial class NotesViewModel : ObservableObject
{
    private const string FileName = "notes.json";
    private const string CompletedFileName = "completed-notes.json";

    private static readonly string[] Palette =
    {
        "#FFB454", "#4C8DFF", "#3FD27F", "#FF6B6B", "#B888FF", "#36C5D6"
    };

    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _metricsTimer;

    public ObservableCollection<NoteItem> Notes { get; }
    public ObservableCollection<NoteItem> CompletedNotes { get; }
    public ObservableCollection<NoteItem> VisibleCompletedNotes { get; } = new();
    public IReadOnlyList<NoteHistoryRange> HistoryRanges { get; }

    [ObservableProperty]
    private NoteItem? _selected;

    [ObservableProperty]
    private bool _showCompleted;

    [ObservableProperty]
    private Visibility _activeVisibility = Visibility.Visible;

    [ObservableProperty]
    private Visibility _completedVisibility = Visibility.Collapsed;

    [ObservableProperty]
    private NoteHistoryRange _selectedHistoryRange;

    [ObservableProperty]
    private string _time = string.Empty;

    [ObservableProperty]
    private string _date = string.Empty;

    [ObservableProperty]
    private double _memoryUsedPercent;

    [ObservableProperty]
    private string _memoryText = string.Empty;

    public NotesViewModel()
    {
        Notes = new ObservableCollection<NoteItem>(JsonStore.Load(FileName, Defaults));
        CompletedNotes = new ObservableCollection<NoteItem>(JsonStore.Load(CompletedFileName, () => new List<NoteItem>()));
        HistoryRanges = new[]
        {
            new NoteHistoryRange("7 天", 7),
            new NoteHistoryRange("30 天", 30),
            new NoteHistoryRange("90 天", 90),
            new NoteHistoryRange("1 年", 365),
            new NoteHistoryRange("全部", null),
        };
        _selectedHistoryRange = HistoryRanges[3];
        Selected = Notes.FirstOrDefault();

        _saveTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(600)
        };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); Persist(); };

        _metricsTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _metricsTimer.Tick += (_, _) => RefreshMetrics();

        Notes.CollectionChanged += OnCollectionChanged;
        CompletedNotes.CollectionChanged += OnCollectionChanged;
        foreach (NoteItem note in Notes)
            WireNote(note);
        foreach (NoteItem note in CompletedNotes)
            WireNote(note);

        SettingsService.Changed += OnSettingsChanged;
        PruneCompletedHistory();
        RefreshCompletedNotes();
    }

    public void Resume()
    {
        RefreshMetrics();
        _metricsTimer.Start();
    }

    public void Pause() => _metricsTimer.Stop();

    public void Persist()
    {
        JsonStore.Save(FileName, Notes.ToList());
        JsonStore.Save(CompletedFileName, CompletedNotes.ToList());
    }

    private void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
            foreach (NoteItem note in e.NewItems)
                WireNote(note);

        if (e.OldItems != null)
            foreach (NoteItem note in e.OldItems)
                note.PropertyChanged -= OnNotePropertyChanged;

        RefreshCompletedNotes();
        Persist();
    }

    private void WireNote(NoteItem note) => note.PropertyChanged += OnNotePropertyChanged;

    private void OnNotePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is NoteItem note)
            note.UpdatedAt = DateTime.Now;
        ScheduleSave();
    }

    [RelayCommand]
    private void AddNote()
    {
        var note = new NoteItem
        {
            Title = "新便签",
            Content = string.Empty,
            ColorHex = Palette[Notes.Count % Palette.Length]
        };
        int insertIndex = Notes.TakeWhile(item => item.IsPinned).Count();
        Notes.Insert(insertIndex, note);
        Selected = note;
        ShowCompleted = false;
    }

    [RelayCommand]
    private void CompleteNote(NoteItem? note) => Complete(note);

    public void Complete(NoteItem? note)
    {
        if (note is null)
            return;

        Notes.Remove(note);
        note.IsPinned = false;
        note.CompletedAt = DateTime.Now;
        CompletedNotes.Insert(0, note);
        PruneCompletedHistory();
        Selected = Notes.FirstOrDefault();
    }

    [RelayCommand]
    private void DeleteNote(NoteItem? note) => Delete(note);

    public void Delete(NoteItem? note)
    {
        if (note is null)
            return;

        Notes.Remove(note);
        if (ReferenceEquals(Selected, note))
            Selected = Notes.FirstOrDefault();
    }

    [RelayCommand]
    private void TogglePin(NoteItem? note)
    {
        if (note is null)
            return;
        note.IsPinned = !note.IsPinned;
        if (note.IsPinned)
            MoveToTop(note);
    }

    [RelayCommand]
    private void ToggleCompletedView() => ShowCompleted = !ShowCompleted;

    [RelayCommand]
    private void RestoreNote(NoteItem? note)
    {
        if (note is null)
            return;

        CompletedNotes.Remove(note);
        note.CompletedAt = null;
        Notes.Insert(0, note);
        ShowCompleted = false;
        Selected = note;
    }

    public void MoveToTop(NoteItem item)
    {
        int index = Notes.IndexOf(item);
        if (index > 0)
        {
            Notes.Move(index, 0);
            Persist();
        }
    }

    public void Move(NoteItem source, NoteItem target)
    {
        if (ReferenceEquals(source, target))
            return;

        int oldIndex = Notes.IndexOf(source);
        int newIndex = Notes.IndexOf(target);
        if (oldIndex < 0 || newIndex < 0 || oldIndex == newIndex)
            return;

        Notes.Move(oldIndex, newIndex);
        Persist();
    }

    partial void OnShowCompletedChanged(bool value)
    {
        ActiveVisibility = value ? Visibility.Collapsed : Visibility.Visible;
        CompletedVisibility = value ? Visibility.Visible : Visibility.Collapsed;
        if (value)
            RefreshCompletedNotes();
    }

    partial void OnSelectedHistoryRangeChanged(NoteHistoryRange value) => RefreshCompletedNotes();

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        PruneCompletedHistory();
        RefreshCompletedNotes();
    }

    private void PruneCompletedHistory()
    {
        DateTime cutoff = DateTime.Now.AddMonths(-SettingsService.Current.NoteHistoryMonths);
        for (int i = CompletedNotes.Count - 1; i >= 0; i--)
        {
            DateTime completedAt = CompletedNotes[i].CompletedAt ?? CompletedNotes[i].UpdatedAt;
            if (completedAt < cutoff)
                CompletedNotes.RemoveAt(i);
        }
    }

    private void RefreshCompletedNotes()
    {
        if (SelectedHistoryRange is null)
            return;

        DateTime? cutoff = SelectedHistoryRange.Days is null
            ? null
            : DateTime.Now.AddDays(-SelectedHistoryRange.Days.Value);

        var items = CompletedNotes
            .Where(note => cutoff is null || (note.CompletedAt ?? note.UpdatedAt) >= cutoff.Value)
            .OrderByDescending(note => note.CompletedAt ?? note.UpdatedAt)
            .ToList();

        VisibleCompletedNotes.Clear();
        foreach (NoteItem note in items)
            VisibleCompletedNotes.Add(note);
    }

    private void RefreshMetrics()
    {
        DateTime now = DateTime.Now;
        Time = now.ToString("HH:mm:ss");
        Date = now.ToString("yyyy年M月d日 dddd");

        var mem = new NativeMethods.MEMORYSTATUSEX
        {
            dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>()
        };

        if (!NativeMethods.GlobalMemoryStatusEx(ref mem))
            return;

        MemoryUsedPercent = mem.dwMemoryLoad;
        double totalGb = mem.ullTotalPhys / 1024d / 1024d / 1024d;
        double usedGb = (mem.ullTotalPhys - mem.ullAvailPhys) / 1024d / 1024d / 1024d;
        MemoryText = $"{usedGb:0.0} / {totalGb:0.0} GB";
    }

    private static List<NoteItem> Defaults() => new()
    {
        new NoteItem
        {
            Title = "欢迎使用 SidePeek",
            Content = "把鼠标移到屏幕边缘即可展开，移开自动收起。\n这里是便签，支持新增、编辑、置顶，数据会自动保存。",
            ColorHex = "#4C8DFF"
        },
        new NoteItem
        {
            Title = "待办",
            Content = "• 完成停靠窗口动画\n• 接入数据持久化\n• 打磨视觉细节",
            ColorHex = "#3FD27F",
            IsPinned = true
        },
    };
}

public sealed class NoteHistoryRange
{
    public NoteHistoryRange(string label, int? days)
    {
        Label = label;
        Days = days;
    }

    public string Label { get; }
    public int? Days { get; }
}
