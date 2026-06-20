using System;
using SidePeek.App.Views;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace SidePeek.App.Services;

/// <summary>系统托盘图标 + 右键菜单（显示/隐藏、开机自启、退出）。</summary>
public sealed class TrayService : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly DockWindow _window;
    private readonly Forms.ToolStripMenuItem _startupItem;

    public TrayService(DockWindow window)
    {
        _window = window;

        _icon = new Forms.NotifyIcon
        {
            Icon = CreateIcon(),
            Text = "SidePeek",
            Visible = true
        };

        var menu = new Forms.ContextMenuStrip();

        var toggleItem = new Forms.ToolStripMenuItem("显示 / 收起");
        toggleItem.Click += (_, _) => _window.ToggleVisibility();

        _startupItem = new Forms.ToolStripMenuItem("开机自启")
        {
            CheckOnClick = true,
            Checked = StartupService.IsEnabled
        };
        _startupItem.CheckedChanged += (_, _) => StartupService.IsEnabled = _startupItem.Checked;

        var exitItem = new Forms.ToolStripMenuItem("退出 SidePeek");
        exitItem.Click += (_, _) => System.Windows.Application.Current.Shutdown();

        menu.Items.Add(toggleItem);
        menu.Items.Add(_startupItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(exitItem);

        _icon.ContextMenuStrip = menu;
        _icon.DoubleClick += (_, _) => _window.ToggleVisibility();
    }

    private static Drawing.Icon CreateIcon()
    {
        using var bmp = new Drawing.Bitmap(32, 32);
        using (Drawing.Graphics g = Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Drawing.Color.Transparent);
            using var bg = new Drawing.SolidBrush(Drawing.ColorTranslator.FromHtml("#3A6BD6"));
            g.FillEllipse(bg, 1, 1, 30, 30);
            using var font = new Drawing.Font("Segoe UI", 15, Drawing.FontStyle.Bold, Drawing.GraphicsUnit.Pixel);
            using var fg = new Drawing.SolidBrush(Drawing.Color.White);
            var format = new Drawing.StringFormat
            {
                Alignment = Drawing.StringAlignment.Center,
                LineAlignment = Drawing.StringAlignment.Center
            };
            g.DrawString("S", font, fg, new Drawing.RectangleF(0, 0, 32, 32), format);
        }

        IntPtr handle = bmp.GetHicon();
        using var temp = Drawing.Icon.FromHandle(handle);
        return (Drawing.Icon)temp.Clone();
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
