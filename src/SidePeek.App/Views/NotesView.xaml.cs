using System;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SidePeek.App.Models;
using SidePeek.App.Services;
using SidePeek.App.ViewModels;

namespace SidePeek.App.Views;

public partial class NotesView : UserControl
{
    private NotesViewModel ViewModel => (NotesViewModel)DataContext;
    private Point _dragStart;
    private NoteItem? _dragItem;
    private NoteItem? _pendingDeleteNote;
    private DockWindow? _suspendedDock;

    public NotesView()
    {
        InitializeComponent();
        DataContext = new NotesViewModel();
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => ViewModel.Resume();

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        HideDeleteConfirm();
        ViewModel.Pause();
    }

    private void OnDeleteNote(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not NoteItem note)
            return;

        _pendingDeleteNote = note;
        string title = string.IsNullOrWhiteSpace(note.Title) ? "这条便签" : $"「{note.Title}」";
        DeleteConfirmMessage.Text = $"确定要删除{title}吗？删除后无法恢复。";
        ApplyDeleteConfirmColors();
        DeleteConfirmOverlay.Visibility = Visibility.Visible;
        _suspendedDock = Window.GetWindow(this) as DockWindow;
        _suspendedDock?.SuspendDock();
    }

    private void OnCancelDelete(object sender, RoutedEventArgs e) => HideDeleteConfirm();

    private void OnConfirmDelete(object sender, RoutedEventArgs e)
    {
        if (_pendingDeleteNote is not null)
            ViewModel.Delete(_pendingDeleteNote);

        HideDeleteConfirm();
    }

    private void HideDeleteConfirm()
    {
        if (DeleteConfirmOverlay.Visibility != Visibility.Visible)
            return;

        _pendingDeleteNote = null;
        DeleteConfirmOverlay.Visibility = Visibility.Collapsed;
        _suspendedDock?.ResumeDock();
        _suspendedDock = null;
    }

    private void ApplyDeleteConfirmColors()
    {
        bool isLightTheme = ThemeService.IsLightTheme(SettingsService.Current.Theme);
        DeleteConfirmPanel.Background = new SolidColorBrush(isLightTheme
            ? Colors.White
            : Color.FromRgb(0x24, 0x28, 0x30));
    }

    private void OnItemMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (IsInputElement(e.OriginalSource as DependencyObject))
        {
            _dragItem = null;
            return;
        }

        _dragStart = e.GetPosition(this);
        _dragItem = ((FrameworkElement)sender).DataContext as NoteItem;
    }

    private void OnItemMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragItem is null)
            return;

        Point current = e.GetPosition(this);
        if (Math.Abs(current.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        DragDrop.DoDragDrop((DependencyObject)sender, _dragItem, DragDropEffects.Move);
        _dragItem = null;
    }

    private void OnItemDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(NoteItem)) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnItemDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(NoteItem)) is NoteItem source &&
            ((FrameworkElement)sender).DataContext is NoteItem target)
        {
            ViewModel.Move(source, target);
        }

        e.Handled = true;
    }

    private static bool IsInputElement(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is TextBox or ButtonBase)
                return true;
            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }
}
