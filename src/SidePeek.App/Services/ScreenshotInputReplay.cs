using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Microsoft.Win32.SafeHandles;

namespace SidePeek.App.Services;

/// <summary>仅供本地诊断：通过真实 Dispatcher 队列回放合成输入，不移动系统鼠标。</summary>
internal static class ScreenshotInputReplay
{
    private const string LogTag = "[ScreenshotInputReplay]";
    private const int ReplayDurationMilliseconds = 4000;
    private const int InputIntervalMilliseconds = 1;
    private const int MaximumPendingCallbacks = 1;
    private const uint CreateTimerHighResolution = 0x00000002;
    private const uint TimerModifyStateAccess = 0x00000002;
    private const uint SynchronizeAccess = 0x00100000;
    private const uint TimerWaitTimeoutMilliseconds = 100;
    private const uint WaitSignaled = 0;
    private const uint WaitFailed = uint.MaxValue;

    [DllImport("kernel32.dll", EntryPoint = "CreateWaitableTimerExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeWaitHandle CreateWaitableTimerEx(IntPtr attributes, string? name,
        uint flags, uint desiredAccess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimer(SafeWaitHandle timer, ref long dueTime, int period,
        IntPtr completionRoutine, IntPtr argument, [MarshalAs(UnmanagedType.Bool)] bool resume);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeWaitHandle handle, uint timeoutMilliseconds);

    internal static Task RunAsync(Dispatcher dispatcher, Action<int, double> onInput,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(onInput);
        if (!App.ScreenshotDiagnosticsEnabled)
            throw new InvalidOperationException("Screenshot input replay requires --screenshot-diagnostics.");

        // 生产端独立于 UI 线程；UI 卡住时仍继续计时和生产，以保留真实排队时间。
        return Task.Run(async () =>
        {
            using var delay = new ReplayDelay();
            var clock = Stopwatch.StartNew();
            DispatcherOperation? pending = null;
            long generated = 0, scheduled = 0, delivered = 0, dropped = 0;
            string outcome = "completed";
            string cleanupFailure = "none";

            DispatcherOperation Schedule(double progress)
            {
                int generatedTimestamp = Environment.TickCount;
                var operation = dispatcher.InvokeAsync(() =>
                {
                    onInput(generatedTimestamp, progress);
                    Interlocked.Increment(ref delivered);
                }, DispatcherPriority.Input, cancellation);
                scheduled++;
                return operation;
            }

            try
            {
                while (clock.Elapsed.TotalMilliseconds < ReplayDurationMilliseconds)
                {
                    cancellation.ThrowIfCancellationRequested();
                    double progress = generated == 0 ? 0
                        : clock.Elapsed.TotalMilliseconds / ReplayDurationMilliseconds;
                    generated++;
                    if (pending is not null && !pending.Task.IsCompleted)
                    {
                        // 保留已排队样本的原时间戳，丢弃新样本以限制积压；不把排队延迟清零。
                        dropped++;
                    }
                    else
                    {
                        if (pending is not null)
                            await pending.Task.ConfigureAwait(false);
                        pending = Schedule(Math.Min(1, progress));
                    }
                    await delay.WaitAsync(cancellation).ConfigureAwait(false);
                }

                // 结束时确保进度到达 1，且所有回调完成后调用方才能结束手势或销毁覆盖层。
                if (pending is not null)
                    await pending.Task.ConfigureAwait(false);
                cancellation.ThrowIfCancellationRequested();
                generated++;
                pending = Schedule(1);
                await pending.Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                outcome = "canceled";
                throw;
            }
            catch
            {
                outcome = "failed";
                throw;
            }
            finally
            {
                if (pending is not null)
                {
                    // 取消尚未派发的回调；已执行的回调必须等结束，避免返回后继续改动 UI。
                    pending.Abort();
                    try
                    {
                        await pending.Task.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception error)
                    {
                        // 主路径负责传播原始异常；清理只记录类型，避免覆盖取消或先前失败。
                        cleanupFailure = error.GetType().Name;
                    }
                }
                AppLogger.Info(FormattableString.Invariant(
                    $"{LogTag} source=synthetic-input; outcome={outcome}; targetDurationMs={ReplayDurationMilliseconds}; actualDurationMs={clock.Elapsed.TotalMilliseconds:F3}; producerIntervalMs={InputIntervalMilliseconds}; maxPendingCallbacks={MaximumPendingCallbacks}; generated={generated}; scheduled={scheduled}; delivered={delivered}; droppedWhilePending={dropped}; cleanupFailure={cleanupFailure}; timing={delay.Mode}; nativeTimerWaits={delay.NativeTimerWaits}; taskDelayWaits={delay.TaskDelayWaits}; includesNativeMousePath=false."));
            }
        });
    }

    private sealed class ReplayDelay : IDisposable
    {
        private SafeWaitHandle? _timer;

        internal string Mode => _timer is null ? "Task.Delay-fallback" : "high-resolution-waitable-timer";
        internal long NativeTimerWaits { get; private set; }
        internal long TaskDelayWaits { get; private set; }

        internal ReplayDelay()
        {
            try
            {
                _timer = CreateWaitableTimerEx(IntPtr.Zero, null, CreateTimerHighResolution,
                    TimerModifyStateAccess | SynchronizeAccess);
                if (_timer.IsInvalid)
                {
                    int error = Marshal.GetLastWin32Error();
                    FallBack($"operation=CreateWaitableTimerEx; win32Error={error}");
                }
            }
            catch (EntryPointNotFoundException error)
            {
                FallBack($"operation=CreateWaitableTimerEx; errorType={error.GetType().Name}");
            }
        }

        internal Task WaitAsync(CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            if (_timer is not null)
            {
                // 负数表示相对时间，单位为 100 ns；单次高精度等待不修改全局计时器分辨率。
                long dueTime = -InputIntervalMilliseconds * TimeSpan.TicksPerMillisecond;
                if (!SetWaitableTimer(_timer, ref dueTime, 0, IntPtr.Zero, IntPtr.Zero, resume: false))
                {
                    int error = Marshal.GetLastWin32Error();
                    FallBack($"operation=SetWaitableTimer; win32Error={error}");
                }
                else
                {
                    // 只阻塞后台生产线程，不忙等；有限超时也避免异常计时器阻止取消或退出。
                    uint result = WaitForSingleObject(_timer, TimerWaitTimeoutMilliseconds);
                    if (result == WaitSignaled)
                    {
                        NativeTimerWaits++;
                        cancellation.ThrowIfCancellationRequested();
                        return Task.CompletedTask;
                    }
                    int error = result == WaitFailed ? Marshal.GetLastWin32Error() : 0;
                    FallBack($"operation=WaitForSingleObject; waitResult=0x{result:X}; win32Error={error}");
                }
            }

            TaskDelayWaits++;
            return Task.Delay(InputIntervalMilliseconds, cancellation);
        }

        private void FallBack(string reason)
        {
            _timer?.Dispose();
            _timer = null;
            // 一旦失败便整段回放使用兜底，不在每次输入上重复尝试或写日志。
            AppLogger.Error($"{LogTag} High-resolution timer unavailable; timing=Task.Delay-fallback; {reason}.");
        }

        public void Dispose()
        {
            _timer?.Dispose();
            _timer = null;
        }
    }
}
