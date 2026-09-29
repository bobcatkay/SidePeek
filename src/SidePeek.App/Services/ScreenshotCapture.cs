using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SidePeek.App.Interop;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using static Vortice.Direct3D11.D3D11;
using static Vortice.DXGI.DXGI;

namespace SidePeek.App.Services;

internal sealed record CaptureMonitor(string Name, Rect Bounds, Rect WorkArea);
internal sealed record DesktopCapture(BitmapSource Image, Rect Bounds,
    IReadOnlyList<CaptureMonitor> Monitors, IReadOnlyList<Rect> Windows, bool Hdr, bool ProtectedContent);

/// <summary>按显卡/输出采集，HDR 保持 FP16 scRGB，最后统一转换为 sRGB。</summary>
internal static class ScreenshotCapture
{
    private const int WaitTimeout = unchecked((int)0x887A0027);
    private const long MaximumImageBytes = 256L * 1024 * 1024;
    private static readonly Format[] HdrFormats = [Format.R16G16B16A16_Float, Format.B8G8R8A8_UNorm];
    private static readonly Format[] SdrFormats = [Format.B8G8R8A8_UNorm];

    internal static Task<DesktopCapture> CaptureAsync(CancellationToken cancellation)
        => Task.Run(() => Capture(cancellation), cancellation);

    private static DesktopCapture Capture(CancellationToken cancellation)
    {
        var monitors = new List<CaptureMonitor>();
        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
            (IntPtr monitor, IntPtr hdc, ref NativeMethods.RECT rect, IntPtr data) =>
            {
                var info = new NativeMethods.MONITORINFOEX { Size = Marshal.SizeOf<NativeMethods.MONITORINFOEX>() };
                if (NativeMethods.GetMonitorInfo(monitor, ref info))
                    monitors.Add(new CaptureMonitor(info.DeviceName, ToRect(info.Monitor), ToRect(info.Work)));
                return true;
            }, IntPtr.Zero);
        if (monitors.Count == 0)
            throw new InvalidOperationException("没有可截图的显示器。");
        var windows = ScreenshotNative.WindowBounds();

        Rect desktop = monitors[0].Bounds;
        foreach (var monitor in monitors.Skip(1))
            desktop.Union(monitor.Bounds);
        int width = checked((int)desktop.Width), height = checked((int)desktop.Height);
        long imageBytes = checked((long)width * height * 4);
        if (imageBytes > MaximumImageBytes)
            throw new InvalidOperationException("屏幕总尺寸过大，请减少显示器数量或分辨率后重试。");
        byte[] pixels = new byte[(int)imageBytes];
        // Gaps between monitors remain opaque black in a spanning selection.
        for (int index = 3; index < pixels.Length; index += 4)
            pixels[index] = 255;

        bool hdr = false, protectedContent = false;
        var captured = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var factory = CreateDXGIFactory1<IDXGIFactory1>();
        for (uint adapterIndex = 0; ; adapterIndex++)
        {
            var adapterResult = factory.EnumAdapters1(adapterIndex, out var adapter);
            if (adapterResult.Code == unchecked((int)0x887A0002)) // DXGI_ERROR_NOT_FOUND
                break;
            adapterResult.CheckError();
            using (adapter)
            {
                ID3D11Device? device = null;
                ID3D11DeviceContext? context = null;
                try
                {
                    for (uint outputIndex = 0; ; outputIndex++)
                    {
                        var outputResult = adapter.EnumOutputs(outputIndex, out var output);
                        if (outputResult.Code == unchecked((int)0x887A0002))
                            break;
                        outputResult.CheckError();
                        using (output)
                        {
                            var description = output.Description;
                            if (!description.AttachedToDesktop || captured.Contains(description.DeviceName))
                                continue;
                            var monitor = monitors.FirstOrDefault(item => string.Equals(item.Name,
                                description.DeviceName, StringComparison.OrdinalIgnoreCase));
                            if (monitor is null)
                                continue;
                            cancellation.ThrowIfCancellationRequested();
                            if (device is null)
                            {
                                D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
                                    [FeatureLevel.Level_11_0, FeatureLevel.Level_10_1],
                                    out device, out context).CheckError();
                            }
                            using var output6 = output.QueryInterfaceOrNull<IDXGIOutput6>();
                            bool outputHdr = output6 is not null && (int)output6.Description1.ColorSpace == 12;
                            using var output5 = output.QueryInterface<IDXGIOutput5>();
                            using var duplication = output5.DuplicateOutput1(device, outputHdr ? HdrFormats : SdrFormats);
                            protectedContent |= ReadOutput(device, context!, duplication, monitor.Bounds,
                                desktop, pixels, outputHdr, description.DeviceName, cancellation);
                            hdr |= outputHdr;
                            captured.Add(description.DeviceName);
                        }
                    }
                }
                finally
                {
                    context?.Dispose();
                    device?.Dispose();
                }
            }
        }
        if (monitors.Any(monitor => !captured.Contains(monitor.Name)))
            throw new InvalidOperationException("部分显示器无法通过 DirectX 采集。请检查显卡驱动、远程会话或正在运行的录屏工具。");
        cancellation.ThrowIfCancellationRequested();
        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32,
            null, pixels, checked(width * 4));
        image.Freeze();
        return new DesktopCapture(image, desktop, monitors, windows, hdr, protectedContent);
    }

    private static bool ReadOutput(ID3D11Device device, ID3D11DeviceContext context,
        IDXGIOutputDuplication duplication, Rect monitor, Rect desktop, byte[] pixels,
        bool hdr, string deviceName, CancellationToken cancellation)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(3))
        {
            cancellation.ThrowIfCancellationRequested();
            var result = duplication.AcquireNextFrame(150, out var info, out var resource);
            if (result.Code == WaitTimeout)
                continue;
            result.CheckError();
            try
            {
                using (resource)
                {
                    // Pointer-only updates have no new desktop image.
                    if (info.LastPresentTime == 0)
                        continue;
                    using var texture = resource.QueryInterface<ID3D11Texture2D>();
                    var description = texture.Description;
                    if (hdr && description.Format != Format.R16G16B16A16_Float)
                        throw new InvalidOperationException("HDR 显示器未返回浮点画面，无法保证截图颜色。请更新显卡驱动后重试。");
                    if (description.Format != Format.R16G16B16A16_Float && description.Format != Format.B8G8R8A8_UNorm)
                        throw new InvalidOperationException($"不支持的截图像素格式：{description.Format}。");
                    description.Usage = ResourceUsage.Staging;
                    description.BindFlags = BindFlags.None;
                    description.CPUAccessFlags = CpuAccessFlags.Read;
                    description.MiscFlags = ResourceOptionFlags.None;
                    using var staging = device.CreateTexture2D(description);
                    context.CopyResource(staging, texture);
                    var mapped = context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                    try
                    {
                        CopyPixels(mapped, description, (int)duplication.Description.Rotation, monitor,
                            desktop, pixels, hdr, ScreenshotNative.SdrWhiteScale(deviceName), cancellation);
                    }
                    finally
                    {
                        context.Unmap(staging, 0);
                    }
                    return info.ProtectedContentMaskedOut;
                }
            }
            finally
            {
                duplication.ReleaseFrame().CheckError();
            }
        }
        throw new TimeoutException("等待桌面画面超时，请重新截图。");
    }

    private static void CopyPixels(MappedSubresource mapped, Texture2DDescription description,
        int rotation, Rect monitor, Rect desktop, byte[] target, bool hdr, float white,
        CancellationToken cancellation)
    {
        int sourceWidth = checked((int)description.Width), sourceHeight = checked((int)description.Height);
        int width = (int)monitor.Width, height = (int)monitor.Height;
        bool rotated = rotation is 2 or 4;
        if ((rotated ? sourceHeight : sourceWidth) != width || (rotated ? sourceWidth : sourceHeight) != height)
            throw new InvalidOperationException("截图期间显示器尺寸发生变化，请重试。");
        int pixelSize = description.Format == Format.R16G16B16A16_Float ? 8 : 4;
        byte[] row = new byte[checked(sourceWidth * pixelSize)];
        int targetWidth = (int)desktop.Width;
        int left = (int)(monitor.Left - desktop.Left), top = (int)(monitor.Top - desktop.Top);
        for (int sy = 0; sy < sourceHeight; sy++)
        {
            cancellation.ThrowIfCancellationRequested();
            Marshal.Copy(IntPtr.Add(mapped.DataPointer, checked(sy * (int)mapped.RowPitch)), row, 0, row.Length);
            for (int sx = 0; sx < sourceWidth; sx++)
            {
                (int dx, int dy) = rotation switch
                {
                    2 => (sourceHeight - 1 - sy, sx),
                    3 => (sourceWidth - 1 - sx, sourceHeight - 1 - sy),
                    4 => (sy, sourceWidth - 1 - sx),
                    _ => (sx, sy)
                };
                int destination = checked(((top + dy) * targetWidth + left + dx) * 4);
                int source = sx * pixelSize;
                if (pixelSize == 4)
                {
                    target[destination] = row[source];
                    target[destination + 1] = row[source + 1];
                    target[destination + 2] = row[source + 2];
                }
                else
                {
                    float r = HalfValue(row, source) / white;
                    float g = HalfValue(row, source + 2) / white;
                    float b = HalfValue(row, source + 4) / white;
                    // A shared shoulder preserves hue while compressing HDR highlights.
                    float peak = Math.Max(r, Math.Max(g, b));
                    float scale = hdr && peak > 0.75f
                        ? (0.75f + 0.25f * (1 - MathF.Exp(-(peak - 0.75f) / 0.25f))) / peak
                        : 1f;
                    target[destination] = Srgb(b * scale);
                    target[destination + 1] = Srgb(g * scale);
                    target[destination + 2] = Srgb(r * scale);
                }
                target[destination + 3] = 255;
            }
        }
    }

    private static float HalfValue(byte[] row, int offset)
    {
        float value = (float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(row, offset));
        return float.IsFinite(value) ? Math.Max(0, value) : 0;
    }

    private static byte Srgb(float linear)
    {
        linear = Math.Clamp(linear, 0, 1);
        float encoded = linear <= 0.0031308f ? linear * 12.92f : 1.055f * MathF.Pow(linear, 1 / 2.4f) - 0.055f;
        return (byte)Math.Clamp((int)MathF.Round(encoded * 255), 0, 255);
    }

    private static Rect ToRect(NativeMethods.RECT value)
        => new(value.Left, value.Top, value.Right - value.Left, value.Bottom - value.Top);
}
