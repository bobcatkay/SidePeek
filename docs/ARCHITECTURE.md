# SidePeek .NET/WPF 架构

当前 main 使用 `SidePeek.slnx` 和 `src/SidePeek.App/SidePeek.App.csproj`，目标为 `net9.0-windows`。界面使用 WPF/WPF-UI，视图模型使用 CommunityToolkit.Mvvm；截图采集使用 Vortice.Direct3D11 3.8.3 及其 DXGI 依赖。

## 模块

| 模块 | 职责 |
|---|---|
| App.xaml.cs | 单实例、设置/主题初始化、主窗口、托盘和截图服务生命周期 |
| Docking/DockManager.cs | 边缘停靠、悬停轮询、动画、显示器和 DPI 变化 |
| Views/DockWindow.xaml | 侧边栏、四个功能页、设置和截图入口 |
| ViewModels | 便签、命令、工具、剪贴板与设置状态 |
| Services/SettingsService.cs | 设置读取、规范化、持久化和变更通知 |
| Services/JsonStore.cs | AppData 下的 JSON 持久化 |
| Services/CommandExecutor.cs | 命令执行、输出、取消及进程树终止 |
| Services/ScreenshotService.cs | 独立截图热键、隐藏/恢复侧边栏、采集取消和编辑会话 |
| Services/ScreenshotCapture.cs | 每个适配器的 Direct3D 设备、输出采集、旋转和 HDR 转换 |
| Interop/ScreenshotNative.cs | DWM 窗口吸附候选、截图窗口排除、SDR 白电平及物理坐标 |
| Views/ScreenshotOverlayWindow.cs | 虚拟桌面遮罩、选区、矢量标注、撤销/重做、导出 |
| Views/ScreenshotToolbarWindow.xaml | 当前显示器顶部的工具栏、颜色、粗细和操作状态 |

## 截图流程

截图服务在 UI 线程接收热键或按钮操作，暂停停靠并隐藏侧边栏，等候合成器完成隐藏后在后台采集。重复触发会激活现有工具栏，不同时创建多个会话。退出程序会取消采集并关闭编辑窗口。

按原生显示器物理坐标建立虚拟桌面，按适配器创建 D3D11 设备。`IDXGIOutput5.DuplicateOutput1` 声明支持 FP16/BGRA8，逐输出复制到 CPU staging 纹理，按行跨度和旋转方向写入统一图像。每个已获取的帧和映射都在 finally 中释放，所有 COM 对象按使用范围 Dispose。

HDR 输出必须返回 FP16 scRGB；不满足时显示错误，避免用失真的 8 位画面替代。读取 Windows `DISPLAYCONFIG_SDR_WHITE_LEVEL`，按屏幕归一化亮度，再用共同高光曲线压缩 RGB 峰值并转换为 sRGB。查询白电平不可用时使用 80 nit 参考值。SDR BGRA8 直接复制。导出仅为带 sRGB 标记的 SDR PNG/剪贴板图像。

冻结画面使用可跨线程的 BitmapSource。覆盖窗口由 Win32 定位到整个虚拟桌面，绘制时抵消 WPF DPI 缩放；鼠标通过 PointToScreen 转成物理像素，选区、笔画和导出尺寸共用同一坐标系。窗口吸附使用按 Z 序枚举的可见窗口 DWM 边界，并排除本进程、最小化和 cloaked 窗口。

选区及标注存储独立于视图。标注颜色和粗细在创建笔画时固定，撤销/重做以完整笔画为单位。导出从冻结原图裁剪，在 96 DPI 的 DrawingVisual 中合成标注，确保一个单位等于一个输出像素；遮罩、手柄及工具栏不进入图片。

保存先写入目标目录的临时文件，编码和刷新成功后替换最终路径。取消文件对话框或导出失败保留编辑会话。复制同时提供 Bitmap 和 PNG 数据，遇到剪贴板占用时短暂重试，成功后关闭会话。

截图热键替换先注册另一个 ID，成功后注销旧 ID；注册失败保留旧热键，并在设置中显示原因。窗口关闭、取消或导出成功后恢复侧边栏及此前前台窗口。显示器拓扑发生变化时结束冻结会话，重新截图。

## 验证边界

本次按 AGENTS.md 仅阅读代码和差异，未执行构建、测试或静态检查。实际 HDR 显示效果、多屏 DPI、显卡驱动及系统剪贴板交互尚需运行验收。
