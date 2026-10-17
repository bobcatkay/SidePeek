using System.Diagnostics;
using System.Globalization;
using System.Windows.Media;

namespace SidePeek.App.Services;

/// <summary>在截图 UI 线程汇总单次手势的耗时，只在结束时写一条日志。</summary>
internal sealed class ScreenshotPerformanceProbe : IDisposable
{
    private const string LogTag = "[ScreenshotPerformance]";
    private const string DiagnosticsEnvironmentVariable = "SIDEPEEK_SCREENSHOT_DIAGNOSTICS";
    private const string DiagnosticsEnabledValue = "1";
    private const int MaximumTimingSamples = 2048;
    private const double TailPercentile = 0.95;

    private readonly bool _observeRendering = string.Equals(
        Environment.GetEnvironmentVariable(DiagnosticsEnvironmentVariable), DiagnosticsEnabledValue,
        StringComparison.Ordinal);
    private readonly TimingSamples _inputAge = new();
    private readonly TimingSamples _inputDispatchInterval = new();
    private readonly TimingSamples _visualUpdate = new();
    private readonly TimingSamples _toolbarUpdate = new();
    private readonly TimingSamples _totalUpdate = new();
    private readonly TimingSamples _renderCallbackInterval = new();
    private readonly TimingSamples _updateToRenderCallback = new();
    private string _tool = string.Empty;
    private long _gestureStarted;
    private long? _previousInput;
    private long? _previousRenderCallback;
    private long? _pendingUpdate;
    private long _changedUpdates;
    private long _renderCallbacks;
    private bool _renderingSubscribed;
    private bool _disposed;

    internal bool IsActive { get; private set; }

    internal void Begin(string tool)
    {
        if (_disposed)
            return;
        if (IsActive)
            End("restarted");

        _inputAge.Reset();
        _inputDispatchInterval.Reset();
        _visualUpdate.Reset();
        _toolbarUpdate.Reset();
        _totalUpdate.Reset();
        _renderCallbackInterval.Reset();
        _updateToRenderCallback.Reset();
        _tool = tool;
        _previousInput = null;
        _previousRenderCallback = null;
        _pendingUpdate = null;
        _changedUpdates = 0;
        _renderCallbacks = 0;
        _gestureStarted = Stopwatch.GetTimestamp();
        IsActive = true;

        // Rendering 订阅本身可能改变 WPF 调度，仅显式诊断时、活跃手势内启用。
        if (_observeRendering)
        {
            CompositionTarget.Rendering += OnRendering;
            _renderingSubscribed = true;
        }
    }

    internal void RecordInput(int eventTimestamp)
    {
        if (!IsActive)
            return;

        long now = Stopwatch.GetTimestamp();
        // WPF 输入时间戳和 TickCount 都是 32 位毫秒计数，无符号差值可跨计数回绕。
        _inputAge.Add(unchecked((uint)(Environment.TickCount - eventTimestamp)));
        if (_previousInput is { } previous)
            _inputDispatchInterval.Add(Stopwatch.GetElapsedTime(previous, now).TotalMilliseconds);
        _previousInput = now;
    }

    internal void RecordUpdate(double visualMs, double toolbarMs, double totalMs, bool changed)
    {
        if (!IsActive)
            return;

        _visualUpdate.Add(visualMs);
        _toolbarUpdate.Add(toolbarMs);
        _totalUpdate.Add(totalMs);
        if (!changed)
            return;

        _changedUpdates++;
        // 同一回调前可合并多次更新；保留最早等待时间，避免持续输入掩盖调度停顿。
        if (_observeRendering)
            _pendingUpdate ??= Stopwatch.GetTimestamp();
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (!IsActive)
            return;

        long now = Stopwatch.GetTimestamp();
        _renderCallbacks++;
        if (_previousRenderCallback is { } previous)
            _renderCallbackInterval.Add(Stopwatch.GetElapsedTime(previous, now).TotalMilliseconds);
        _previousRenderCallback = now;
        if (_pendingUpdate is { } update)
        {
            _updateToRenderCallback.Add(Stopwatch.GetElapsedTime(update, now).TotalMilliseconds);
            _pendingUpdate = null;
        }
    }

    internal void End(string reason)
    {
        if (!IsActive)
            return;

        double durationMs = Stopwatch.GetElapsedTime(_gestureStarted).TotalMilliseconds;
        IsActive = false;
        UnsubscribeRendering();

        // 回调仅表示 WPF 即将处理一轮渲染，不代表 GPU 已提交或显示器已呈现画面。
        string rendering = _observeRendering
            ? $"renderObserver=enabled; renderSemantics=WPF-callback-not-GPU-present; renderCallbacks={_renderCallbacks}; "
                + $"pendingUpdate={_pendingUpdate.HasValue}; renderCallbackIntervalMs={_renderCallbackInterval.Describe()}; "
                + $"updateToRenderCallbackMs={_updateToRenderCallback.Describe()}"
            : "renderObserver=disabled";
        string summary = FormattableString.Invariant(
            $"{LogTag} processId={Environment.ProcessId}; tool={_tool}; reason={reason}; durationMs={durationMs:F3}; changedUpdates={_changedUpdates}; ");
        AppLogger.Info(summary
            + $"p95Window=last-{MaximumTimingSamples}; inputAgeMs={_inputAge.Describe()}; "
            + $"inputDispatchIntervalMs={_inputDispatchInterval.Describe()}; visualUpdateMs={_visualUpdate.Describe()}; "
            + $"toolbarUpdateMs={_toolbarUpdate.Describe()}; totalUpdateMs={_totalUpdate.Describe()}; {rendering}");
        _pendingUpdate = null;
    }

    private void UnsubscribeRendering()
    {
        if (!_renderingSubscribed)
            return;
        CompositionTarget.Rendering -= OnRendering;
        _renderingSubscribed = false;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        End("disposed");
        UnsubscribeRendering();
        _disposed = true;
    }

    private sealed class TimingSamples
    {
        private readonly double[] _samples = new double[MaximumTimingSamples];
        private int _sampleCount;
        private int _nextSample;
        private long _count;
        private double _sum;
        private double _maximum;

        internal void Reset()
        {
            _sampleCount = 0;
            _nextSample = 0;
            _count = 0;
            _sum = 0;
            _maximum = 0;
        }

        internal void Add(double milliseconds)
        {
            if (!double.IsFinite(milliseconds) || milliseconds < 0)
                return;

            _count++;
            _sum += milliseconds;
            _maximum = Math.Max(_maximum, milliseconds);
            // 长时间拖动也保持固定内存；均值/最大值覆盖全程，p95 只使用最近的有界样本。
            _samples[_nextSample] = milliseconds;
            _nextSample = (_nextSample + 1) % MaximumTimingSamples;
            _sampleCount = Math.Min(_sampleCount + 1, MaximumTimingSamples);
        }

        internal string Describe()
        {
            if (_count == 0)
                return "{n=0,avg=0,p95=0,max=0,p95Samples=0}";

            // 排序和字符串分配仅发生在手势结束后，不进入鼠标移动热路径。
            var sorted = new double[_sampleCount];
            Array.Copy(_samples, sorted, _sampleCount);
            Array.Sort(sorted);
            int percentileIndex = (int)Math.Ceiling(_sampleCount * TailPercentile) - 1;
            return string.Create(CultureInfo.InvariantCulture,
                $"{{n={_count},avg={_sum / _count:F3},p95={sorted[percentileIndex]:F3},max={_maximum:F3},p95Samples={_sampleCount}}}");
        }
    }
}
