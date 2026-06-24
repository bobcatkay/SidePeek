using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using SidePeek.App.Models;
using SidePeek.App.Services;
using SidePeek.App.ViewModels;

namespace SidePeek.App.Views;

public partial class NotesView : UserControl
{
    private NotesViewModel ViewModel => (NotesViewModel)DataContext;
    private Point _dragStart;
    private DragSortController<NoteItem> _dragSort = null!;
    private NoteItem? _pendingDeleteNote;
    private DockWindow? _suspendedDock;
    private readonly HashSet<NoteItem> _completingNotes = new();

    public NotesView()
    {
        InitializeComponent();
        DataContext = new NotesViewModel();
        _dragSort = new DragSortController<NoteItem>(
            NotesRoot,
            DragOverlay,
            NotesItemsControl,
            (source, target) => ViewModel.Move(source, target));
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => ViewModel.Resume();

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        HideDeleteConfirm();
        _dragSort.Cancel();
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

    private void OnCompleteNote(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not NoteItem note)
            return;

        if (!_completingNotes.Add(note))
            return;

        if (sender is not DependencyObject source)
        {
            CompleteAndClear(note);
            return;
        }

        FrameworkElement? card = FindNoteCard(source, note);
        if (card is null || card.ActualWidth <= 0 || card.ActualHeight <= 0)
        {
            CompleteAndClear(note);
            return;
        }

        try
        {
            AnimateNoteToHistory(note, card);
        }
        catch (Exception ex)
        {
            AppLogger.Error("Unable to animate completed note.", ex);
            CompleteAndClear(note);
        }
    }

    private void AnimateNoteToHistory(NoteItem note, FrameworkElement card)
    {
        NotesRoot.UpdateLayout();
        card.UpdateLayout();
        HistoryToggle.UpdateLayout();

        if (HistoryToggle.ActualWidth <= 0 || HistoryToggle.ActualHeight <= 0)
        {
            CompleteAndClear(note);
            return;
        }

        FrameworkElement? ghost = CreateNoteSnapshot(card);
        if (ghost is null)
        {
            CompleteAndClear(note);
            return;
        }

        Point start = card.TransformToAncestor(NotesRoot).Transform(new Point(0, 0));
        Point target = HistoryToggle.TransformToAncestor(NotesRoot).Transform(
            new Point(HistoryToggle.ActualWidth / 2, HistoryToggle.ActualHeight / 2));

        Canvas.SetLeft(ghost, start.X);
        Canvas.SetTop(ghost, start.Y);
        card.Opacity = 0;
        CompletionAnimationLayer.Children.Add(ghost);

        const double shrinkScale = 0.18;
        double endWidth = card.ActualWidth * shrinkScale;
        double endHeight = card.ActualHeight * shrinkScale;
        double endLeft = target.X - (endWidth / 2);
        double endTop = target.Y - (endHeight / 2);
        var storyboard = new Storyboard { FillBehavior = FillBehavior.Stop };
        TimeSpan duration = TimeSpan.FromMilliseconds(430);

        AddAnimation(storyboard, ghost, Canvas.LeftProperty, start.X, endLeft, duration, EasingMode.EaseInOut);
        AddAnimation(storyboard, ghost, Canvas.TopProperty, start.Y, endTop, duration, EasingMode.EaseInOut);
        AddAnimation(storyboard, ghost, WidthProperty, card.ActualWidth, endWidth, duration, EasingMode.EaseIn);
        AddAnimation(storyboard, ghost, HeightProperty, card.ActualHeight, endHeight, duration, EasingMode.EaseIn);
        AddAnimation(storyboard, ghost, OpacityProperty, 0.95, 0.32, duration, EasingMode.EaseIn);

        storyboard.Completed += (_, _) =>
        {
            CompletionAnimationLayer.Children.Remove(ghost);
            ViewModel.Complete(note);
            _completingNotes.Remove(note);
            PulseHistoryToggle();
        };
        storyboard.Begin();
    }

    private FrameworkElement? CreateNoteSnapshot(FrameworkElement source)
    {
        int pixelWidth = Math.Max(1, (int)Math.Ceiling(source.ActualWidth));
        int pixelHeight = Math.Max(1, (int)Math.Ceiling(source.ActualHeight));
        DpiScale dpi = VisualTreeHelper.GetDpi(source);

        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(pixelWidth * dpi.DpiScaleX),
            (int)Math.Ceiling(pixelHeight * dpi.DpiScaleY),
            96 * dpi.DpiScaleX,
            96 * dpi.DpiScaleY,
            PixelFormats.Pbgra32);
        bitmap.Render(source);

        return new Border
        {
            Width = source.ActualWidth,
            Height = source.ActualHeight,
            CornerRadius = new CornerRadius(10),
            Background = new ImageBrush(bitmap) { Stretch = Stretch.Fill },
            IsHitTestVisible = false,
            Opacity = 0.95
        };
    }

    private void CompleteAndClear(NoteItem note)
    {
        try
        {
            ViewModel.Complete(note);
        }
        finally
        {
            _completingNotes.Remove(note);
        }
    }

    private void PulseHistoryToggle()
    {
        AddPulseAnimation(ScaleTransform.ScaleXProperty);
        AddPulseAnimation(ScaleTransform.ScaleYProperty);
    }

    private void AddPulseAnimation(DependencyProperty property)
    {
        var animation = new DoubleAnimation(1, 1.16, TimeSpan.FromMilliseconds(140))
        {
            AutoReverse = true,
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        HistoryToggleScale.BeginAnimation(property, animation);
    }

    private static void AddAnimation(
        Storyboard storyboard,
        DependencyObject target,
        DependencyProperty property,
        double from,
        double to,
        TimeSpan duration,
        EasingMode easingMode)
    {
        var animation = new DoubleAnimation(from, to, duration)
        {
            EasingFunction = new CubicEase { EasingMode = easingMode }
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, new PropertyPath(property));
        storyboard.Children.Add(animation);
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
            _dragSort.Cancel();
            return;
        }

        _dragStart = e.GetPosition(this);
        if (((FrameworkElement)sender).DataContext is NoteItem item)
            _dragSort.BeginCandidate(item, (FrameworkElement)sender, e.GetPosition(NotesRoot));
    }

    private void OnItemMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
            return;

        Point current = e.GetPosition(this);
        if (Math.Abs(current.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        if (_dragSort.TryStart(e.GetPosition(NotesRoot)))
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

    private static FrameworkElement? FindNoteCard(DependencyObject? source, NoteItem note)
    {
        while (source is not null)
        {
            if (source is Border { AllowDrop: true, DataContext: NoteItem item } border &&
                ReferenceEquals(item, note))
            {
                return border;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return null;
    }
}
