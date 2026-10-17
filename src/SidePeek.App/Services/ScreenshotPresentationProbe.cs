using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Box = Vortice.Mathematics.Box;
using static Vortice.Direct3D11.D3D11;
using static Vortice.DXGI.DXGI;

namespace SidePeek.App.Services;

/// <summary>显式诊断模式下，关联输入 QPC 与 DXGI 桌面帧里的像素标记；不测物理扫描输出。</summary>
internal sealed class ScreenshotPresentationProbe : IDisposable, IAsyncDisposable
{
    private const string LogTag = nameof(ScreenshotPresentationProbe);
    private const string EnvironmentVariable = "SIDEPEEK_SCREENSHOT_PRESENTATION_PROBE";
    private const string EnabledValue = "1";
    private const int DxgiNotFound = unchecked((int)0x887A0002);
    private const int DxgiWaitTimeout = unchecked((int)0x887A0027);
    private const uint AcquireTimeoutMilliseconds = 50;
    private const int PendingInputCapacity = 8192;
    private const int MaximumTimingSamples = 2048;
    private const double TailPercentile = 0.95;
    private const int SequenceBitCount = 24;
    private const int NonceBitCount = 16;
    private const int ChecksumBitCount = 16;
    private const int HeaderBitCount = MarkerBitCount - ChecksumBitCount;
    private const uint SequenceMask = (1U << SequenceBitCount) - 1;
    private const ulong MarkerMagic = 0xD3;
    private const ushort ChecksumPolynomial = 0x1021;
    private const ushort ChecksumHighBit = 0x8000;
    private const int SdrBytesPerPixel = 4;
    private const int ScRgbBytesPerPixel = 8;
    private const int HalfBytesPerChannel = 2;
    private const byte MaximumBlackChannel = 64;
    private const byte MinimumWhiteChannel = 192;
    private const float MaximumScRgbBlackMagnitude = 0.125f;
    private const float MinimumScRgbWhiteChannel = 0.75f;

    // 协议也供原生 GDI 诊断使用：每屏左上局部物理像素，MSB 优先，白色为 1、黑色为 0。
    internal const int MarkerLeft = 16;
    internal const int MarkerTop = 16;
    internal const int MarkerBitCount = 64;
    internal const int MarkerCellWidth = 4;
    internal const int MarkerHeight = 12;
    internal const int MarkerWidth = MarkerBitCount * MarkerCellWidth;

    private static readonly Format[] CaptureFormats = [Format.R16G16B16A16_Float, Format.B8G8R8A8_UNorm];
    private static int _nextProbeId;
    private readonly int _probeId = Interlocked.Increment(ref _nextProbeId);
    private readonly CaptureMonitor _monitor;
    private readonly Rect _drawingViewport;
    private readonly ushort _nonce = (ushort)RandomNumberGenerator.GetInt32(1, ushort.MaxValue + 1);
    private readonly object _inputGate = new();
    private readonly PendingInput[] _inputs = new PendingInput[PendingInputCapacity];
    private readonly CancellationTokenSource _cancellation = new();
    private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _worker;
    private readonly TimingSamples _inputToDesktop = new();
    private readonly TimingSamples _inputToUnaccumulatedDesktop = new();
    private readonly TimingSamples _inputToRead = new();
    private readonly TimingSamples _desktopToAcquire = new();
    private readonly TimingSamples _readback = new();
    private readonly TimingSamples _markerDesktopInterval = new();
    private readonly TimingSamples _markerObservationInterval = new();
    private uint _sequence;
    private long _recordedInputs;
    private long _observedInputs;
    private long _overwrittenInputs;
    private long _desktopFrames;
    private long _accumulatedFrames;
    private uint _maximumAccumulatedFrames;
    private long _invalidMarkers;
    private long _unknownSequences;
    private long _repeatedMarkers;
    private long _invalidTimings;
    private long _protectedFrames;
    private long _markersFromAccumulatedFrames;
    private long? _previousMarkerDesktopTime;
    private long? _previousMarkerObservedTime;
    private int _acceptingInputs;
    private bool _disposeRequested;
    private string _adapterDescription = "unresolved";
    private string _captureFormat = "unresolved";

    private ScreenshotPresentationProbe(CaptureMonitor monitor, Rect drawingViewport)
    {
        _monitor = monitor;
        _drawingViewport = drawingViewport;
        _worker = Task.Run(ObserveDesktop);
    }

    /// <param name="drawingViewport">该显示器在截图全桌面中的局部物理像素范围，和 DrawingContext 坐标一致。</param>
    internal static ScreenshotPresentationProbe? TryCreate(CaptureMonitor monitor, Rect drawingViewport)
    {
        if (!App.ScreenshotDiagnosticsEnabled || !App.ScreenshotDiagnosticsCaptureVisible
            || Environment.GetEnvironmentVariable(EnvironmentVariable) != EnabledValue)
        {
            return null;
        }
        return new ScreenshotPresentationProbe(monitor, drawingViewport);
    }

    internal Task<bool> Ready => _ready.Task;

    /// <summary>条码在整张截图内的局部物理像素范围；宿主须再减去自身 viewport 原点。</summary>
    internal Rect MarkerDrawingBounds => new(_drawingViewport.Left + MarkerLeft,
        _drawingViewport.Top + MarkerTop, MarkerWidth, MarkerHeight);

    /// <summary>输入到达时调用；返回不透明编码，后续绘制不能重置这里的输入时间。</summary>
    internal long RecordInput(long inputQpc)
    {
        if (Volatile.Read(ref _acceptingInputs) == 0)
            return 0;

        lock (_inputGate)
        {
            if (_disposeRequested || Volatile.Read(ref _acceptingInputs) == 0)
                return 0;
            _sequence = (_sequence + 1) & SequenceMask;
            if (_sequence == 0)
                _sequence = 1;
            int index = (int)(_sequence % PendingInputCapacity);
            if (_inputs[index].Sequence != 0 && !_inputs[index].Consumed)
                _overwrittenInputs++;
            _inputs[index] = new PendingInput(_sequence, inputQpc, false);
            _recordedInputs++;
            ulong header = (MarkerMagic << (NonceBitCount + SequenceBitCount))
                | ((ulong)_nonce << SequenceBitCount) | _sequence;
            return unchecked((long)((header << ChecksumBitCount) | ComputeChecksum(header)));
        }
    }

    /// <summary>在交互画面同一次 UI 更新批次的顶层绘制，使用 RecordInput 返回的编码；不保存屏幕画面。</summary>
    internal void DrawMarker(DrawingContext drawing, long marker)
    {
        if (marker == 0)
            return;
        ulong code = unchecked((ulong)marker);
        Rect bounds = MarkerDrawingBounds;
        double left = bounds.Left;
        double top = bounds.Top;
        drawing.DrawRectangle(Brushes.Black, null, bounds);
        for (int bit = 0; bit < MarkerBitCount; bit++)
        {
            if (((code >> (MarkerBitCount - bit - 1)) & 1) != 0)
                drawing.DrawRectangle(Brushes.White, null,
                    new Rect(left + bit * MarkerCellWidth, top, MarkerCellWidth, MarkerHeight));
        }
    }

    private void ObserveDesktop()
    {
        long started = Stopwatch.GetTimestamp();
        string outcome = "cancelled";
        try
        {
            if (_monitor.Bounds.Width < MarkerLeft + MarkerWidth || _monitor.Bounds.Height < MarkerTop + MarkerHeight)
                throw new ProbeUnavailableException("monitor-too-small");
            using var factory = CreateDXGIFactory1<IDXGIFactory1>();
            for (uint adapterIndex = 0; ; adapterIndex++)
            {
                _cancellation.Token.ThrowIfCancellationRequested();
                var adapterResult = factory.EnumAdapters1(adapterIndex, out var adapter);
                if (adapterResult.Code == DxgiNotFound)
                    break;
                adapterResult.CheckError();
                using (adapter)
                {
                    for (uint outputIndex = 0; ; outputIndex++)
                    {
                        var outputResult = adapter.EnumOutputs(outputIndex, out var output);
                        if (outputResult.Code == DxgiNotFound)
                            break;
                        outputResult.CheckError();
                        using (output)
                        {
                            var description = output.Description;
                            if (!description.AttachedToDesktop
                                || !string.Equals(description.DeviceName, _monitor.Name, StringComparison.OrdinalIgnoreCase))
                                continue;
                            _adapterDescription = adapter.Description1.Description;
                            using var output6 = output.QueryInterfaceOrNull<IDXGIOutput6>();
                            string outputColorSpace = output6?.Description1.ColorSpace.ToString() ?? "unavailable";
                            D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
                                [FeatureLevel.Level_11_0, FeatureLevel.Level_10_1],
                                out ID3D11Device device, out ID3D11DeviceContext context).CheckError();
                            using (device)
                            using (context)
                            using (var output5 = output.QueryInterface<IDXGIOutput5>())
                            using (var duplication = output5.DuplicateOutput1(device, CaptureFormats))
                            {
                                if ((int)duplication.Description.Rotation is not (0 or 1))
                                    throw new ProbeUnavailableException("rotated-output-not-measured");
                                _cancellation.Token.ThrowIfCancellationRequested();
                                Volatile.Write(ref _acceptingInputs, 1);
                                var mode = duplication.Description.ModeDescription;
                                AppLogger.Info($"{LogTag}: probe={_probeId} ready; monitor={_monitor.Name}; adapter={_adapterDescription}; outputColorSpace={outputColorSpace}; dimensions={mode.Width}x{mode.Height}; refreshRate={mode.RefreshRate.Numerator}/{mode.RefreshRate.Denominator}; rotation={duplication.Description.Rotation}; format=awaiting-first-frame; readback=marker-row-only; semantics=DXGI-desktop-update-not-physical-scanout.");
                                _ready.TrySetResult(true);
                                ReadFrames(device, context, duplication);
                                return;
                            }
                        }
                    }
                }
            }
            throw new ProbeUnavailableException("output-not-found");
        }
        catch (OperationCanceledException)
        {
            outcome = "cancelled";
        }
        catch (ProbeUnavailableException error)
        {
            outcome = "unavailable";
            AppLogger.Info($"{LogTag}: probe={_probeId} unavailable; reason={error.Message}.");
        }
        catch (Exception error)
        {
            outcome = "failed";
            AppLogger.Error($"{LogTag}: probe={_probeId} observation failed; errorType={error.GetType().Name}, hresult=0x{error.HResult:X8}.");
        }
        finally
        {
            Volatile.Write(ref _acceptingInputs, 0);
            _ready.TrySetResult(false);
            WriteSummary(outcome, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    }

    private void ReadFrames(ID3D11Device device, ID3D11DeviceContext context, IDXGIOutputDuplication duplication)
    {
        ID3D11Texture2D? staging = null;
        byte[] pixels = new byte[MarkerWidth * ScRgbBytesPerPixel];
        Format readbackFormat = Format.Unknown;
        int row = MarkerTop + MarkerHeight / 2;
        var region = new Box(MarkerLeft, row, 0, MarkerLeft + MarkerWidth, row + 1, 1);
        try
        {
            while (!_cancellation.IsCancellationRequested)
            {
                var result = duplication.AcquireNextFrame(AcquireTimeoutMilliseconds, out var info, out var resource);
                if (result.Code == DxgiWaitTimeout)
                {
                    resource?.Dispose();
                    continue;
                }
                if (result.Code < 0)
                {
                    resource?.Dispose();
                    result.CheckError();
                }
                long acquired = Stopwatch.GetTimestamp();
                try
                {
                    using (resource)
                    {
                        if (resource is null)
                            throw new ProbeUnavailableException("desktop-frame-resource-missing");
                        if (info.LastPresentTime == 0)
                            continue;
                        _desktopFrames++;
                        _accumulatedFrames += info.AccumulatedFrames;
                        _maximumAccumulatedFrames = Math.Max(_maximumAccumulatedFrames, info.AccumulatedFrames);
                        if (info.ProtectedContentMaskedOut)
                            _protectedFrames++;
                        using var texture = resource.QueryInterface<ID3D11Texture2D>();
                        var description = texture.Description;
                        int bytesPerPixel = description.Format switch
                        {
                            Format.B8G8R8A8_UNorm => SdrBytesPerPixel,
                            Format.R16G16B16A16_Float => ScRgbBytesPerPixel,
                            _ => throw new ProbeUnavailableException("unsupported-capture-format")
                        };
                        if (readbackFormat != Format.Unknown && readbackFormat != description.Format)
                            throw new ProbeUnavailableException("capture-format-changed");
                        if (description.Width < MarkerLeft + MarkerWidth || description.Height <= row)
                            throw new ProbeUnavailableException("output-size-changed");
                        if (staging is null)
                        {
                            readbackFormat = description.Format;
                            _captureFormat = readbackFormat.ToString();
                            AppLogger.Info($"{LogTag}: probe={_probeId} capture format resolved; monitor={_monitor.Name}; format={_captureFormat}; bytesPerPixel={bytesPerPixel}; decoding=black-white-marker-only; toneMapping=false.");
                            description.Width = MarkerWidth;
                            description.Height = 1;
                            description.Usage = ResourceUsage.Staging;
                            description.BindFlags = BindFlags.None;
                            description.CPUAccessFlags = CpuAccessFlags.Read;
                            description.MiscFlags = ResourceOptionFlags.None;
                            staging = device.CreateTexture2D(description);
                        }
                        // 只将标记中间的一行复制到 CPU，整屏纹理始终留在 GPU，不编码或持久化图像。
                        context.CopySubresourceRegion(staging, 0, 0, 0, 0, texture, 0, region);
                        var mapped = context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                        try
                        {
                            Marshal.Copy(mapped.DataPointer, pixels, 0, MarkerWidth * bytesPerPixel);
                        }
                        finally
                        {
                            context.Unmap(staging, 0);
                        }
                        long read = Stopwatch.GetTimestamp();
                        // 采样器成本覆盖全部读回帧，避免只选中成功的新标记而漏掉慢帧。
                        _readback.Add(Stopwatch.GetElapsedTime(acquired, read).TotalMilliseconds);
                        if (acquired >= info.LastPresentTime)
                            _desktopToAcquire.Add(Stopwatch.GetElapsedTime(info.LastPresentTime, acquired).TotalMilliseconds);
                        if (TryDecodeMarker(pixels, readbackFormat, bytesPerPixel, out uint sequence))
                            RecordObservedMarker(sequence, info.LastPresentTime, acquired, read, info.AccumulatedFrames);
                        else
                            _invalidMarkers++;
                    }
                }
                finally
                {
                    duplication.ReleaseFrame().CheckError();
                }
            }
        }
        finally
        {
            staging?.Dispose();
            Array.Clear(pixels);
        }
    }

    private bool TryDecodeMarker(byte[] pixels, Format format, int bytesPerPixel, out uint sequence)
    {
        sequence = 0;
        ulong code = 0;
        for (int bit = 0; bit < MarkerBitCount; bit++)
        {
            int offset = (bit * MarkerCellWidth + MarkerCellWidth / 2) * bytesPerPixel;
            int value = DecodePixel(pixels, offset, format);
            // 每个色块核对相邻两个内侧像素，拒绝抗锯齿、遮挡或与条码偶然相似的背景。
            if (value < 0 || value != DecodePixel(pixels, offset - bytesPerPixel, format))
                return false;
            code = (code << 1) | (uint)value;
        }
        ulong header = code >> ChecksumBitCount;
        if (header >> (NonceBitCount + SequenceBitCount) != MarkerMagic
            || ((header >> SequenceBitCount) & ushort.MaxValue) != _nonce
            || (ushort)code != ComputeChecksum(header))
            return false;
        sequence = (uint)(header & SequenceMask);
        return sequence != 0;
    }

    private static int DecodePixel(byte[] pixels, int offset, Format format)
    {
        if (format == Format.R16G16B16A16_Float)
        {
            float scR = (float)BitConverter.ToHalf(pixels, offset);
            float scG = (float)BitConverter.ToHalf(pixels, offset + HalfBytesPerChannel);
            float scB = (float)BitConverter.ToHalf(pixels, offset + HalfBytesPerChannel * 2);
            if (!float.IsFinite(scR) || !float.IsFinite(scG) || !float.IsFinite(scB))
                return -1;
            // scRGB 的 SDR 白至少为 1；只识别黑白，不做全屏色调映射或推断 HDR 峰值。
            if (Math.Abs(scR) <= MaximumScRgbBlackMagnitude && Math.Abs(scG) <= MaximumScRgbBlackMagnitude
                && Math.Abs(scB) <= MaximumScRgbBlackMagnitude)
                return 0;
            if (scR >= MinimumScRgbWhiteChannel && scG >= MinimumScRgbWhiteChannel && scB >= MinimumScRgbWhiteChannel)
                return 1;
            return -1;
        }
        byte b = pixels[offset], g = pixels[offset + 1], r = pixels[offset + 2];
        if (b <= MaximumBlackChannel && g <= MaximumBlackChannel && r <= MaximumBlackChannel)
            return 0;
        if (b >= MinimumWhiteChannel && g >= MinimumWhiteChannel && r >= MinimumWhiteChannel)
            return 1;
        return -1;
    }

    private void RecordObservedMarker(uint sequence, long desktopQpc, long acquiredQpc, long readQpc,
        uint accumulatedFrames)
    {
        long inputQpc;
        lock (_inputGate)
        {
            int index = (int)(sequence % PendingInputCapacity);
            ref PendingInput input = ref _inputs[index];
            if (input.Sequence != sequence)
            {
                _unknownSequences++;
                return;
            }
            if (input.Consumed)
            {
                _repeatedMarkers++;
                return;
            }
            inputQpc = input.Timestamp;
            // 首次识别就消耗该序号；时序不一致的样本不能借后续无关桌面更新重新变成有效延迟。
            input.Consumed = true;
            if (inputQpc <= 0 || desktopQpc < inputQpc || acquiredQpc < desktopQpc || readQpc < acquiredQpc)
            {
                _invalidTimings++;
                return;
            }
            _observedInputs++;
        }
        double desktopLatencyMs = Stopwatch.GetElapsedTime(inputQpc, desktopQpc).TotalMilliseconds;
        _inputToDesktop.Add(desktopLatencyMs);
        if (accumulatedFrames == 1)
            _inputToUnaccumulatedDesktop.Add(desktopLatencyMs);
        else if (accumulatedFrames > 1)
            _markersFromAccumulatedFrames++;
        _inputToRead.Add(Stopwatch.GetElapsedTime(inputQpc, readQpc).TotalMilliseconds);
        if (_previousMarkerDesktopTime is { } previousDesktop && desktopQpc >= previousDesktop)
            _markerDesktopInterval.Add(Stopwatch.GetElapsedTime(previousDesktop, desktopQpc).TotalMilliseconds);
        if (_previousMarkerObservedTime is { } previousObserved)
            _markerObservationInterval.Add(Stopwatch.GetElapsedTime(previousObserved, acquiredQpc).TotalMilliseconds);
        _previousMarkerDesktopTime = desktopQpc;
        _previousMarkerObservedTime = acquiredQpc;
    }

    private static ushort ComputeChecksum(ulong header)
    {
        ushort checksum = ushort.MaxValue;
        for (int bit = HeaderBitCount - 1; bit >= 0; bit--)
        {
            bool feedback = ((checksum & ChecksumHighBit) != 0) ^ (((header >> bit) & 1) != 0);
            checksum = (ushort)(checksum << 1);
            if (feedback)
                checksum ^= ChecksumPolynomial;
        }
        return checksum;
    }

    private void WriteSummary(string outcome, double elapsedMs)
    {
        long recorded, observed, overwritten;
        lock (_inputGate)
        {
            recorded = _recordedInputs;
            observed = _observedInputs;
            overwritten = _overwrittenInputs;
        }
        string summary = FormattableString.Invariant(
                $"{LogTag}: probe={_probeId}; monitor={_monitor.Name}; adapter={_adapterDescription}; format={_captureFormat}; outcome={outcome}; elapsedMs={elapsedMs:F3}; inputs={recorded}; observedMarkers={observed}; unobservedInputs={recorded - observed}; overwrittenUnobserved={overwritten}; ");
        // LastPresentTime 是桌面更新 QPC；积压帧可能合并，因此这里只报告首次观测到标记的帧。
        AppLogger.Info(summary
            + $"desktopFrames={_desktopFrames}; accumulatedFrames={_accumulatedFrames}; maxAccumulatedFrames={_maximumAccumulatedFrames}; markersFromAccumulatedFrames={_markersFromAccumulatedFrames}; protectedFrames={_protectedFrames}; "
            + $"invalidMarkerFrames={_invalidMarkers}; unknownSequenceFrames={_unknownSequences}; repeatedMarkerFrames={_repeatedMarkers}; invalidTimingFrames={_invalidTimings}; "
            + $"p95Window=last-{MaximumTimingSamples}; inputToDesktopFrameMs={_inputToDesktop.Describe()}; inputToReadMs={_inputToRead.Describe()}; "
            + $"inputToUnaccumulatedDesktopFrameMs={_inputToUnaccumulatedDesktop.Describe()}; "
            + $"desktopToAcquireMs={_desktopToAcquire.Describe()}; readbackMs={_readback.Describe()}; "
            + $"markerDesktopIntervalMs={_markerDesktopInterval.Describe()}; markerObservationIntervalMs={_markerObservationInterval.Describe()}; "
            + "semantics=first-observed-marker-in-DXGI-desktop-update; collectorTimingSamples=all-read-desktop-frames; accumulatedFrameLatencyMayOverestimate=true; physicalScanoutMeasured=false; captureMayAffectTiming=true; framesPersisted=false.");
    }

    public void Dispose()
    {
        lock (_inputGate)
        {
            if (_disposeRequested)
                return;
            _disposeRequested = true;
        }
        _cancellation.Cancel();
        // 不在 UI 线程等待 AcquireNextFrame 或 GPU 读回；资源只由观察线程释放。
        _ = _worker.ContinueWith(_ => _cancellation.Dispose(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        await _worker.ConfigureAwait(false);
    }

    private struct PendingInput(uint sequence, long timestamp, bool consumed)
    {
        internal uint Sequence = sequence;
        internal long Timestamp = timestamp;
        internal bool Consumed = consumed;
    }

    private sealed class ProbeUnavailableException(string reason) : Exception(reason) { }

    private sealed class TimingSamples
    {
        private readonly double[] _samples = new double[MaximumTimingSamples];
        private long _count;
        private int _sampleCount;
        private int _nextSample;
        private double _sum;
        private double _maximum;

        internal void Add(double milliseconds)
        {
            _count++;
            _sum += milliseconds;
            _maximum = Math.Max(_maximum, milliseconds);
            _samples[_nextSample] = milliseconds;
            _nextSample = (_nextSample + 1) % MaximumTimingSamples;
            _sampleCount = Math.Min(_sampleCount + 1, MaximumTimingSamples);
        }

        internal string Describe()
        {
            if (_count == 0)
                return "{n=0,avg=0,p95=0,max=0,p95Samples=0}";
            var sorted = new double[_sampleCount];
            Array.Copy(_samples, sorted, _sampleCount);
            Array.Sort(sorted);
            int percentileIndex = (int)Math.Ceiling(_sampleCount * TailPercentile) - 1;
            return string.Create(CultureInfo.InvariantCulture,
                $"{{n={_count},avg={_sum / _count:F3},p95={sorted[percentileIndex]:F3},max={_maximum:F3},p95Samples={_sampleCount}}}");
        }
    }
}
