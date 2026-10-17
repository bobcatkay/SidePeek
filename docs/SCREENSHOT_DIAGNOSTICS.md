# 截图性能诊断

正常截图会在日志中记录启动阶段耗时，以及每次拖动结束后的性能汇总。日志位于 `%APPDATA%\SidePeek\logs\sidepeek-yyyyMMdd.log`。

默认使用每屏独立的原生画布 `ScreenshotNativeCanvas`，通过 GDI 更新冻结桌面、选区和标注；输入、撤销重做及导出共用截图会话的状态。诊断模式保留 `SIDEPEEK_SCREENSHOT_WPF_RENDERER=1`，可切回旧的 WPF 绘制路径进行同条件对照。

## 日志指标

- `ScreenshotService`：隐藏侧边栏、等待 DWM 合成、桌面捕获、创建及显示覆盖窗口的耗时。`nextStage` 用于定位未完成的阶段。
- `ScreenshotSurface`：实际绘制后端、调度模式、虚拟桌面及窗口大小、DPI，以及宿主的 WPF 渲染等级、纹理上限与渲染模式。宿主的 WPF 参数不代表原生画布的绘制后端。
- `ScreenshotPerformance`：输入事件年龄、事件处理间隔、绘制指令更新、工具栏更新和整个输入处理的耗时。只在手势结束时写日志，不逐帧写盘，不记录画面或坐标。

均值及最大值覆盖整个手势；p95 使用最近 2048 个样本。原生鼠标事件年龄使用毫秒消息时间戳，不能据 `0` 推断没有输入延迟：Windows 可以在取消息时才生成并合并 `WM_MOUSEMOVE`，此前的等待不会完整反映在该值中；WPF 也会生成同步鼠标事件。应结合事件处理间隔和独立跟踪判断。`visualUpdateMs` 只衡量调用阶段，不包含随后发生的桌面合成和屏幕呈现；在 WPF 路径中也不包含异步渲染线程的工作。

## 可重复回放

先退出已有 SidePeek 进程，再启动同一构建的诊断入口：

```powershell
.\SidePeek.exe --screenshot-diagnostics --screenshot-replay
```

该入口自动截图并进行一次 4 秒的选区往返回放。回放通过高精度等待计时器约每 1 ms 产生一个输入任务；实际频率、派发次数及忙时丢弃次数以日志为准。它复用选区更新和绘制路径，但不经过系统鼠标输入、消息路由和鼠标捕获，不能单独证明真实拖动卡顿已修复。

诊断模式中也可按 F8 再次回放；Esc 关闭截图，真实鼠标按下会取消回放。正常模式不使用 F8。

F9 会将回放路径扩展到整个虚拟桌面；自动启动时设置 `SIDEPEEK_SCREENSHOT_ACROSS_SCREENS=1` 可执行同样的跨屏回放。回放不会移动系统鼠标或修改系统计时器分辨率。高精度计时器不可用时会明确记录失败并回退到 `Task.Delay`，这种结果不能与高精度回放直接比较。

设置 `SIDEPEEK_SCREENSHOT_INTERACTION_TEST=1` 会在首次回放前同步执行交互回归，包括正反向框选、窗口吸附、平移、四角缩放、松手最终坐标、取消回滚、画笔和矩形、撤销重做、工具快捷键、全桌面选区及内存导出校验。它调用共享手势入口并执行捕获和释放，不注入系统鼠标事件，不写图片或剪贴板。每个场景及汇总记录在 `ScreenshotInteractionDiagnostics`；没有可吸附窗口时跳过对应场景。该检查验证状态和导出结果，真实鼠标跨屏、不同 DPI 下的视觉效果仍需实际操作确认。

以下选项仅影响当前诊断进程，不写入设置：

- 额外参数 `--screenshot-capture-visible`：允许桌面自动化工具捕获截图窗口，并使其出现在任务栏。默认诊断保留正常捕获排除设置；此选项会改变测量条件。
- 环境变量 `SIDEPEEK_SCREENSHOT_SINGLE_MONITOR=1`：仅显示回放目标屏幕，供跨屏与单屏对照；默认选包含桌面原点的屏幕，也可通过 `SIDEPEEK_SCREENSHOT_REPLAY_MONITOR` 指定，冻结图仍为完整桌面。
- 环境变量 `SIDEPEEK_SCREENSHOT_DIAGNOSTICS=1`：仅在手势期间观察 `CompositionTarget.Rendering`。该回调不代表 GPU 已完成或显示器已呈现；订阅本身可能影响调度，应单独对照。

## 桌面像素延迟对照

配合 `--screenshot-capture-visible` 和 `SIDEPEEK_SCREENSHOT_PRESENTATION_PROBE=1`，回放会显示一个黑白编号条。后台仅将这一小条中的一行读回，核验随机会话码、序号和校验码，不保存截图内容。

`ScreenshotPresentationProbe` 使用 UI 收到回放输入的 QPC 时间及首次观察到相应编号的 DXGI `LastPresentTime`，衡量输入到桌面图像更新的间隔。探针根据实际捕获纹理支持 BGRA8 和 HDR FP16 黑白编号，旋转显示器或不支持的格式会明确跳过。它不包含系统鼠标设备延迟，也不测量显示器物理扫描输出。

`markerDesktopIntervalMs` 和观测到的编号数量用于判断桌面图像更新是否连续。这些是桌面更新指标，不能换算为显示器物理 FPS：桌面图像更新频率可能高于显示器刷新率。仅有少量编号且延迟很低也不能说明跟手，因为长时间不更新后显示的可能恰好是最新输入。必须同时看更新间隔、样本量和延迟。采集本身可能影响调度，对照必须使用同一采样条件。

`AccumulatedFrames` 大于 1 表示采样错过了中间桌面更新；这部分样本可能高估延迟，应结合单独列出的 `inputToUnaccumulatedDesktopFrameMs` 判断。编号跳跃表示中间输入被合并或未被采到，不能直接当成丢帧。未成功初始化或没有有效编号时，该次结果不支持性能结论。

以下环境变量只用于对照实验：

| 变量 | 作用 |
| --- | --- |
| `SIDEPEEK_SCREENSHOT_REPLAY_MONITOR=0` 或 `1` | 选择显示器枚举中的回放目标；以探针日志的设备名确认 |
| `SIDEPEEK_SCREENSHOT_WPF_RENDERER=1` | 从默认原生画布切换到旧的 WPF 绘制路径 |
| `SIDEPEEK_SCREENSHOT_FRAME_SYNC=1` | 仅 WPF 路径：手势期间持续订阅 Rendering，输入更新模型，按帧合并绘图 |
| `SIDEPEEK_SCREENSHOT_SOFTWARE_RENDERING=1` | 对照 WPF 软件绘制；原生画布仍使用 GDI |
| `SIDEPEEK_SCREENSHOT_HIDE_TOOLBAR=1` | 对照隐藏工具栏 |
| `SIDEPEEK_SCREENSHOT_SPANNING_WINDOW=1` | 对照单个跨屏宿主窗口，可与 WPF 路径组合 |
| `SIDEPEEK_SCREENSHOT_ACROSS_SCREENS=1` | 将自动回放路径扩展到整个虚拟桌面 |
| `SIDEPEEK_SCREENSHOT_INTERACTION_TEST=1` | 在首次回放前执行一次同步交互回归 |
| `SIDEPEEK_SCREENSHOT_EXIT_AFTER_REPLAY=1` | 回放及采样结束后退出诊断进程 |

原生画布使用预先暗化的位图和选区差集更新，绘制工作量与 WPF 不完全相同。结果只能比较这两条实现路径，不能单凭差异断言 WPF 内部某一个阶段是根因。

## 原生渲染测量

需要进一步区分 WPF 对照路径的 UI 更新和渲染线程时，可用 ETW 记录 WPF provider `{E13B77A8-14B6-11DE-8069-001B212B5009}`，按日志的进程 ID 筛选 `11032/11033`（原生渲染）及 `11034/11035`（Present）的配对耗时。默认原生画布的更新不由这些 WPF 事件完整描述。使用独立命名的采集会话，结束后及时停止。

PresentMon 可以辅助观察提交间隔，但不要把多个 swapchain 的帧数相加。WPF 跨屏窗口可能为同一个 HWND 创建多个渲染目标；PresentMon 的 GPU GDI 路径按 HWND 关联显示事件，某个 swapchain 的缺失显示记录不等于该屏幕实际丢帧。疑似此问题时应保留原始 ETW，并通过 HWND、显示设备及事件时间归因。
