using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SidePeek.App.Models;
using SidePeek.App.Services;

namespace SidePeek.App.ViewModels;

public partial class NotesViewModel : ObservableObject
{
    private const string FileName = "notes.json";

    private static readonly string[] Palette =
    {
        "#FFB454", "#4C8DFF", "#3FD27F", "#FF6B6B", "#B888FF", "#36C5D6"
    };

    private readonly DispatcherTimer _saveTimer;

    public ObservableCollection<NoteItem> Notes { get; }

    [ObservableProperty]
    private NoteItem? _selected;

    public NotesViewModel()
    {
        Notes = new ObservableCollection<NoteItem>(JsonStore.Load(FileName, Defaults));
        Selected = Notes.FirstOrDefault();

        _saveTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(600)
        };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); Persist(); };

        Notes.CollectionChanged += OnCollectionChanged;
        foreach (NoteItem note in Notes)
            note.PropertyChanged += OnNotePropertyChanged;
    }

    public void Persist() => JsonStore.Save(FileName, Notes.ToList());

    private void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
            foreach (NoteItem note in e.NewItems)
                note.PropertyChanged += OnNotePropertyChanged;

        if (e.OldItems != null)
            foreach (NoteItem note in e.OldItems)
                note.PropertyChanged -= OnNotePropertyChanged;

        Persist();
    }

    private void OnNotePropertyChanged(object? sender, PropertyChangedEventArgs e) => ScheduleSave();

    [RelayCommand]
    private void AddNote()
    {
        var note = new NoteItem
        {
            Title = "新便签",
            Content = string.Empty,
            ColorHex = Palette[Notes.Count % Palette.Length]
        };
        Notes.Insert(0, note);
        Selected = note;
    }

    [RelayCommand]
    private void DeleteNote(NoteItem? note)
    {
        if (note is null)
            return;
        Notes.Remove(note);
        Selected = Notes.FirstOrDefault();
    }

    [RelayCommand]
    private void TogglePin(NoteItem? note)
    {
        if (note is null)
            return;
        note.IsPinned = !note.IsPinned;
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
