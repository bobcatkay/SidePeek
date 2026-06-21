using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using SidePeek.App.Models;
using SidePeek.App.Services;

namespace SidePeek.App.ViewModels;

public partial class ClipboardViewModel : IDisposable
{
    private const string FileName = "clipboard.json";
    private const int MaxItems = 200;

    private readonly DispatcherTimer _timer;
    private string? _lastText;

    public ObservableCollection<ClipboardItem> Items { get; }

    public ClipboardViewModel()
    {
        Items = new ObservableCollection<ClipboardItem>(
            JsonStore.Load(FileName, () => new List<ClipboardItem>())
                .OrderByDescending(item => item.CapturedAt)
                .Take(MaxItems));

        _lastText = Items.FirstOrDefault()?.Text;

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(900)
        };
        _timer.Tick += (_, _) => CaptureClipboardText();
        _timer.Start();
    }

    [RelayCommand]
    private void Copy(ClipboardItem? item)
    {
        if (item is null || string.IsNullOrEmpty(item.Text))
            return;

        try
        {
            Clipboard.SetText(item.Text);
            _lastText = item.Text;
        }
        catch (Exception ex)
        {
            AppLogger.Error("Unable to copy clipboard history item.", ex);
        }
    }

    public void Move(ClipboardItem source, ClipboardItem target)
    {
        if (ReferenceEquals(source, target))
            return;

        int oldIndex = Items.IndexOf(source);
        int newIndex = Items.IndexOf(target);
        if (oldIndex < 0 || newIndex < 0 || oldIndex == newIndex)
            return;

        Items.Move(oldIndex, newIndex);
        Persist();
    }

    private void CaptureClipboardText()
    {
        try
        {
            if (!Clipboard.ContainsText())
                return;

            string text = Clipboard.GetText();
            if (string.IsNullOrWhiteSpace(text) || text == _lastText)
                return;

            ClipboardItem? existing = Items.FirstOrDefault(item => item.Text == text);
            if (existing is not null)
                Items.Remove(existing);

            Items.Insert(0, new ClipboardItem
            {
                Text = text,
                CapturedAt = DateTime.Now
            });

            while (Items.Count > MaxItems)
                Items.RemoveAt(Items.Count - 1);

            _lastText = text;
            Persist();
        }
        catch (Exception ex)
        {
            AppLogger.Error("Unable to read clipboard text.", ex);
        }
    }

    private void Persist() => JsonStore.Save(FileName, Items.ToList());

    public void Dispose() => _timer.Stop();
}
