using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using SidePeek.App.Docking;
using SidePeek.App.Interop;
using SidePeek.App.Models;

namespace SidePeek.App.Views;

public partial class DockWindow : Window
{
    private const int HotkeyId = 0xB001;

    private DockManager? _dock;
    private IntPtr _hwnd;

    private NotesView? _notesView;
    private CommandsView? _commandsView;
    private WidgetsView? _widgetsView;

    public DockWindow()
    {
        InitializeComponent();
    }

    public void SuspendDock() => _dock?.Suspend();

    public void ResumeDock() => _dock?.Resume();

    /// <summary>展开/收起切换（供托盘双击 / 菜单调用）。</summary>
    public void ToggleVisibility() => _dock?.Toggle();

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        ApplyAcrylicBackdrop();
        RegisterHotkey();
    }

    private void RegisterHotkey()
    {
        // 全局热键：Ctrl + Alt + S 切换展开/收起
        NativeMethods.RegisterHotKey(_hwnd, HotkeyId,
            NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT, 0x53 /* VK_S */);

        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            _dock?.Toggle();
            handled = true;
        }
        return IntPtr.Zero;
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_hwnd != IntPtr.Zero)
            NativeMethods.UnregisterHotKey(_hwnd, HotkeyId);
        base.OnClosed(e);
    }

    private void ApplyAcrylicBackdrop()
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;

        HwndSource? source = HwndSource.FromHwnd(hwnd);
        if (source?.CompositionTarget != null)
            source.CompositionTarget.BackgroundColor = Colors.Transparent;

        var margins = new NativeMethods.MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        NativeMethods.DwmExtendFrameIntoClientArea(hwnd, ref margins);

        int backdrop = NativeMethods.DWMSBT_TRANSIENTWINDOW;
        NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));

        int corner = NativeMethods.DWMWCP_ROUND;
        NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ShowTab(TabNotes);
        _dock = new DockManager(this);
        _dock.Start(DockEdge.Right, startExpanded: true);
    }

    private void OnTabChecked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
            return;
        ShowTab(sender);
    }

    private void ShowTab(object sender)
    {
        if (ReferenceEquals(sender, TabNotes))
            ContentHost.Content = _notesView ??= new NotesView();
        else if (ReferenceEquals(sender, TabCommands))
            ContentHost.Content = _commandsView ??= new CommandsView();
        else if (ReferenceEquals(sender, TabWidgets))
            ContentHost.Content = _widgetsView ??= new WidgetsView();
    }

    private void OnDockLeft(object sender, RoutedEventArgs e) => _dock?.MoveToEdge(DockEdge.Left);
    private void OnDockTop(object sender, RoutedEventArgs e) => _dock?.MoveToEdge(DockEdge.Top);
    private void OnDockRight(object sender, RoutedEventArgs e) => _dock?.MoveToEdge(DockEdge.Right);
    private void OnDockBottom(object sender, RoutedEventArgs e) => _dock?.MoveToEdge(DockEdge.Bottom);
}
