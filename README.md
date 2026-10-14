# SidePeek

使用 .NET 9 + WPF 实现的 Windows 11 屏幕边缘侧边栏，集成便签、命令、工具、剪贴板与截图功能。

- 便签：编辑、自动保存、颜色、置顶、拖动排序、完成历史与恢复。
- 命令：多行执行、参数与候选值、静默执行、实时输出、进程树中断。
- 工具与剪贴板：时钟、内存、应用启动器和文本历史。
- 系统集成：左右停靠、显示器选择、DPI 缩放、托盘、全局热键、开机自启及主题。
- 字体：在“设置 → 外观”中选择 Windows 已安装字体，支持名称键盘定位、即时预览和自动保存，也可恢复默认字体；图标和命令输出保留专用字体。
- 截图：窗口吸附、跨屏矩形框选、选区移动与缩放、矩形标注、画笔、颜色与粗细、撤销/重做、PNG 保存、复制及取消。

侧边栏默认快捷键为 **Ctrl + Alt + S**，截图默认快捷键为 **Ctrl + Alt + A**。两者都可在设置中修改；也可从侧边栏顶部按钮或托盘菜单截图。

胶囊所在屏幕的前台窗口进入全屏（如游戏、视频或浏览器全屏）时，侧边栏自动隐藏并暂停边缘悬停触发；退出全屏或切换到非全屏窗口后自动恢复原来的收起/展开状态，不抢占焦点。普通最大化窗口、其他屏幕上的全屏窗口不受影响；截图结束时仍处于全屏则继续隐藏。

截图时隐藏侧边栏，冻结整个桌面，在鼠标所在显示器顶部显示工具栏。点击窗口完成吸附，或拖动框选；选区确认后可拖动边框调整，选择矩形或画笔标注。点击保存或复制完成截图，Esc 取消，Tab 临时隐藏工具栏。

HDR 屏幕通过 DirectX Desktop Duplication 采集 FP16 scRGB 画面，结合该屏幕的 Windows SDR 白电平进行高光压缩，输出 sRGB PNG/剪贴板图像。HDR/SDR 混合显示器分别处理；导出的 PNG 为 SDR 图像，未保留 HDR 原始动态范围。工具栏、遮罩与选区边框不进入导出图片。

## 下载与更新

从 [GitHub Releases](https://github.com/bobcatkay/SidePeek/releases/latest) 下载发布包，解压后运行 `SidePeek.exe`。更新时退出程序，再解压新版使用；用户数据保存在 `%AppData%\SidePeek`。

自动发布提供以下文件：

- 普通版 ZIP：需要 .NET 9 Desktop Runtime（Windows x64）。
- 自包含版 ZIP：文件名带 `selfcontained`，包含运行时。
- `SHA256SUMS.txt`：发布包的 SHA-256 校验值。
- `release-notes.md`：本次更新说明，Release 页面也可查看。

构建进度可在 [GitHub Actions](https://github.com/bobcatkay/SidePeek/actions/workflows/release.yml) 查看，流程说明见 [自动发布文档](docs/RELEASING.md)。

## 开发与打包

安装 .NET 9 SDK；使用 Visual Studio 时安装“.NET 桌面开发”工作负载并打开 `SidePeek.slnx`。应用项目位于 `src/SidePeek.App/SidePeek.App.csproj`。

```powershell
dotnet run --project src/SidePeek.App/SidePeek.App.csproj
.\build.ps1
.\build.ps1 -NuGetProxy ''
.\build.ps1 -SelfContained
.\build-release.ps1
```

默认发布需要目标电脑安装 .NET 9 Desktop Runtime；`-SelfContained` 包含运行时。发布目录为 `dist/win-x64/`，ZIP 位于 `dist/`。普通构建不修改版本；`build-release.ps1` 用于手动递增 `.csproj` 版本并本地打包，失败时回滚。

两个脚本默认沿用当前网络配置；`-NuGetProxy <URL>` 指定本次发布的代理，`-NuGetProxy ''` 清除本次发布的代理环境变量，结束后恢复原环境。

更多说明见 [开发环境](docs/SETUP.md)、[架构](docs/ARCHITECTURE.md)、[截图使用](docs/SCREENSHOTS.md) 和 [项目规则](AGENTS.md)。
