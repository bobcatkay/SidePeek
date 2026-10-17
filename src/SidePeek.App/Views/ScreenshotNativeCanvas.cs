using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SidePeek.App.Services;
using Forms = System.Windows.Forms;

namespace SidePeek.App.Views;

/// <summary>独立不透明子 HWND：WPF 负责承载位置，截图像素和输入直接走原生窗口。</summary>
internal sealed class ScreenshotNativeCanvas : HwndHost
{
    private const string LogTag = nameof(ScreenshotNativeCanvas);
    private const int WindowChild = 0x40000000;
    private const int WindowVisible = 0x10000000;
    private const int WindowClipSiblings = 0x04000000;
    private const int WindowClipChildren = 0x02000000;
    private const int MessageKeyDown = 0x0100;
    private const int MessageCharacter = 0x0102;
    private const int MessageSystemKeyDown = 0x0104;
    private const int MessageSystemCharacter = 0x0106;
    private const int MessageMouseMove = 0x0200;
    private const int MessageLeftButtonDown = 0x0201;
    private const int MessageLeftButtonUp = 0x0202;
    private const int MessageLeftButtonDoubleClick = 0x0203;
    private const int MessageMiddleButtonDoubleClick = 0x0209;
    private const int MessageXButtonDown = 0x020B;
    private const int MessageXButtonDoubleClick = 0x020D;
    private const int CoordinateWordBits = 16;

    private readonly ScreenshotOverlayWindow _session;
    private readonly ScreenshotSurface _surface;
    private readonly Rect _viewport;
    private readonly ScreenshotGdiRenderer _renderer;
    private CanvasControl? _control;
    private Forms.Cursor _nativeCursor = Forms.Cursors.Cross;
    private ScreenshotPresentationProbe? _probe;
    private long _marker;
    private bool _ownsPointerCapture;
    private bool _failed;
    private bool _disposed;

    internal ScreenshotNativeCanvas(ScreenshotOverlayWindow session, ScreenshotSurface surface,
        Rect viewport, BitmapSource background)
    {
        _session = session;
        _surface = surface;
        _viewport = viewport;
        _renderer = new ScreenshotGdiRenderer(background, viewport);
        Focusable = true;
        IsEnabledChanged += (_, _) =>
        {
            if (_control is not null) _control.Enabled = IsEnabled;
        };
    }

    internal bool IsPointerCaptured => _control is { IsDisposed: false, IsHandleCreated: true, Capture: true };

    internal void UpdateVisuals(ScreenshotPresentationProbe? probe, long marker)
    {
        SetMarker(probe, marker);
        UpdateVisuals();
    }

    internal void UpdateVisuals()
    {
        if (_disposed || _failed || _control is not { IsHandleCreated: true }) return;
        try
        {
            _renderer.Update(_session, VisualTreeHelper.GetDpi(this).DpiScaleX, _probe, _marker);
            _renderer.PaintTo(_control.Handle);
        }
        catch (Exception error)
        {
            Fail(error);
        }
    }

    internal void SetMarker(ScreenshotPresentationProbe? probe, long marker)
    {
        if (_disposed) return;
        _probe = probe;
        _marker = marker;
    }

    internal bool FocusInput()
        => !_disposed && !_failed && _control is { IsHandleCreated: true, Enabled: true } && _control.Focus();

    internal bool CapturePointer()
    {
        if (_disposed || _failed || _control is not { IsHandleCreated: true, Enabled: true }) return false;
        // 捕获仅由手势状态机请求；越过屏缝后消息仍送到原来的控件，不在屏幕间转移捕获。
        _control.Capture = true;
        _ownsPointerCapture = _control.Capture;
        return _ownsPointerCapture;
    }

    internal void ReleasePointer()
    {
        // 主动松手已经完成或取消手势，不能再由 CaptureChanged 回滚刚提交的选区。
        _ownsPointerCapture = false;
        if (_control is { IsDisposed: false, IsHandleCreated: true, Capture: true }) _control.Capture = false;
    }

    internal void SetCursor(System.Windows.Input.Cursor cursor)
    {
        if (_disposed) return;
        _nativeCursor = cursor == Cursors.SizeWE ? Forms.Cursors.SizeWE
            : cursor == Cursors.SizeNS ? Forms.Cursors.SizeNS
            : cursor == Cursors.SizeNWSE ? Forms.Cursors.SizeNWSE
            : cursor == Cursors.SizeNESW ? Forms.Cursors.SizeNESW
            : cursor == Cursors.SizeAll ? Forms.Cursors.SizeAll
            : cursor == Cursors.Arrow ? Forms.Cursors.Arrow
            : Forms.Cursors.Cross;
        if (_control is not null && _control.Cursor != _nativeCursor) _control.Cursor = _nativeCursor;
    }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        _control = new CanvasControl(this, hwndParent.Handle)
        {
            Enabled = IsEnabled,
            Cursor = _nativeCursor,
            Size = new System.Drawing.Size(checked((int)_viewport.Width), checked((int)_viewport.Height))
        };
        try
        {
            // 创建 HWND 前准备首帧，首次 WM_PAINT 不依赖 WPF 的 Rendering 调度。
            _renderer.Update(_session, VisualTreeHelper.GetDpi(this).DpiScaleX, _probe, _marker);
            var handle = _control.Handle;
            AppLogger.Info($"{LogTag}: native canvas created; viewport={_viewport.Width}x{_viewport.Height}.");
            return new HandleRef(this, handle);
        }
        catch
        {
            _control.Dispose();
            _control = null;
            throw;
        }
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        ReleasePointer();
        var control = _control;
        _control = null;
        control?.Dispose();
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            base.Dispose(disposing);
        }
        finally
        {
            if (disposing)
            {
                _control?.Dispose();
                _control = null;
                _renderer.Dispose();
                _probe = null;
            }
        }
    }

    protected override bool TabIntoCore(TraversalRequest request) => FocusInput();

    protected override bool TranslateAcceleratorCore(ref MSG message, ModifierKeys modifiers)
    {
        if (message.message is MessageKeyDown or MessageSystemKeyDown
            && HandleKey(unchecked((int)message.wParam.ToInt64()),
                (modifiers & ModifierKeys.Control) != 0, (modifiers & ModifierKeys.Shift) != 0,
                (modifiers & ModifierKeys.Alt) != 0))
            return true;
        return base.TranslateAcceleratorCore(ref message, modifiers);
    }

    private bool HandleKey(int virtualKey, bool control, bool shift, bool alt)
    {
        if (_disposed || _failed) return true;
        try
        {
            return _session.HandleNativeKey(virtualKey, control, shift, alt);
        }
        catch (Exception error)
        {
            Fail(error);
            return true;
        }
    }

    private Point DesktopPoint(CanvasControl control, IntPtr coordinates)
    {
        long packed = coordinates.ToInt64();
        var local = new System.Drawing.Point(unchecked((short)packed), unchecked((short)(packed >> CoordinateWordBits)));
        var screen = control.PointToScreen(local);
        // 捕获期间客户区坐标可为负，先转换到物理屏幕坐标，再限制在完整桌面内。
        return new Point(Math.Clamp(screen.X - _session.Capture.Bounds.X, 0, _session.Capture.Image.PixelWidth),
            Math.Clamp(screen.Y - _session.Capture.Bounds.Y, 0, _session.Capture.Image.PixelHeight));
    }

    private void PointerCaptureChanged()
    {
        if (!_ownsPointerCapture || IsPointerCaptured) return;
        _ownsPointerCapture = false;
        if (!_disposed && !_failed) _surface.CancelGesture();
    }

    private void Fail(Exception error)
    {
        if (_failed || _disposed) return;
        _failed = true;
        ReleasePointer();
        AppLogger.Error($"{LogTag}: native canvas failed; closing screenshot session.", error);
        _session.CloseFromMonitor();
    }

    private sealed class CanvasControl : Forms.Control
    {
        private readonly ScreenshotNativeCanvas _owner;
        private readonly IntPtr _parent;

        internal CanvasControl(ScreenshotNativeCanvas owner, IntPtr parent)
        {
            _owner = owner;
            _parent = parent;
            SetStyle(Forms.ControlStyles.UserPaint | Forms.ControlStyles.AllPaintingInWmPaint
                | Forms.ControlStyles.Opaque | Forms.ControlStyles.Selectable, true);
            SetStyle(Forms.ControlStyles.OptimizedDoubleBuffer | Forms.ControlStyles.DoubleBuffer
                | Forms.ControlStyles.StandardClick | Forms.ControlStyles.StandardDoubleClick, false);
            BackColor = System.Drawing.Color.Black;
            // 画布不接收文本，避免中文输入法吞掉 A/R/P/V 等工具快捷键。
            ImeMode = Forms.ImeMode.Disable;
            TabStop = true;
        }

        protected override Forms.CreateParams CreateParams
        {
            get
            {
                var parameters = base.CreateParams;
                parameters.Parent = _parent;
                parameters.Style |= WindowChild | WindowVisible | WindowClipSiblings | WindowClipChildren;
                return parameters;
            }
        }

        protected override void OnPaintBackground(Forms.PaintEventArgs e) { }

        protected override void OnPaint(Forms.PaintEventArgs e)
        {
            if (_owner._disposed || _owner._failed) return;
            IntPtr dc = e.Graphics.GetHdc();
            try { _owner._renderer.PaintToHdc(dc); }
            finally { e.Graphics.ReleaseHdc(dc); }
        }

        protected override bool IsInputKey(Forms.Keys keyData) => true;

        protected override void OnKeyDown(Forms.KeyEventArgs e)
        {
            if (_owner.HandleKey(e.KeyValue, e.Control, e.Shift, e.Alt))
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
            else base.OnKeyDown(e);
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            _owner.PointerCaptureChanged();
        }

        protected override void WndProc(ref Forms.Message message)
        {
            try
            {
                bool mouseMessage = message.Msg >= MessageMouseMove && message.Msg <= MessageMiddleButtonDoubleClick
                    || message.Msg >= MessageXButtonDown && message.Msg <= MessageXButtonDoubleClick;
                if (mouseMessage)
                {
                    // 不进入 Control 的自动 Capture 路径；只让现有手势逻辑决定捕获和提交。
                    if (Enabled && _owner.IsEnabled && !_owner._disposed && !_owner._failed)
                    {
                        int timestamp = GetMessageTime();
                        Point point = _owner.DesktopPoint(this, message.LParam);
                        if (message.Msg is MessageLeftButtonDown or MessageLeftButtonDoubleClick)
                        {
                            Focus();
                            _owner._surface.NativePointerDown(point, timestamp);
                        }
                        else if (message.Msg == MessageMouseMove) _owner._surface.NativePointerMove(point, timestamp);
                        else if (message.Msg == MessageLeftButtonUp) _owner._surface.NativePointerUp(point, timestamp);
                    }
                    message.Result = IntPtr.Zero;
                    return;
                }
                // 画布没有文字输入，快捷键在 KeyDown 处理，余下字符不交给默认窗口产生提示音。
                if (message.Msg is MessageCharacter or MessageSystemCharacter)
                {
                    message.Result = IntPtr.Zero;
                    return;
                }
                base.WndProc(ref message);
            }
            catch (Exception error)
            {
                // 不能让托管绘制或手势异常穿越 Win32 回调边界。
                _owner.Fail(error);
                message.Result = IntPtr.Zero;
            }
        }
    }

    [DllImport("user32.dll")] private static extern int GetMessageTime();
}
