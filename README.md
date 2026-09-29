# SidePeek

使用 .NET 9 + WPF 实现的 Windows 11 屏幕边缘侧边栏。main 分支已恢复 .NET 入口，解决方案为 `SidePeek.slnx`，项目为 `src/SidePeek.App/SidePeek.App.csproj`。

- 便签：编辑、自动保存、颜色、置顶、拖动排序、完成历史与恢复。
- 命令：多行执行、参数与候选值、静默执行、实时输出、进程树中断。
- 工具与剪贴板：时钟、内存、应用启动器和文本历史。
- 系统集成：左右停靠、显示器选择、DPI 缩放、托盘、全局热键、开机自启及主题。
- 截图：窗口吸附、跨屏矩形框选、选区移动与缩放、矩形标注、画笔、颜色与粗细、撤销/重做、PNG 保存、复制及取消。

侧边栏默认快捷键为 **Ctrl + Alt + S**，截图默认快捷键为 **Ctrl + Alt + A**。两者都可在设置中修改；也可从侧边栏顶部按钮或托盘菜单截图。

截图时隐藏侧边栏，冻结整个桌面，在鼠标所在显示器顶部显示工具栏。点击窗口完成吸附，或拖动框选；选区确认后可拖动边框调整，选择矩形或画笔标注。点击保存或复制完成截图，Esc 取消，Tab 临时隐藏工具栏。

HDR 屏幕通过 DirectX Desktop Duplication 采集 FP16 scRGB 画面，结合该屏幕的 Windows SDR 白电平进行高光压缩，输出 sRGB PNG/剪贴板图像。HDR/SDR 混合显示器分别处理；导出的 PNG 为 SDR 图像，未保留 HDR 原始动态范围。工具栏、遮罩与选区边框不进入导出图片。

## 开发与打包

安装 .NET 9 SDK；使用 Visual Studio 时安装“.NET 桌面开发”工作负载。

```powershell
dotnet run --project src/SidePeek.App/SidePeek.App.csproj
.\build.ps1
.\build.ps1 -NuGetProxy ''
.\build.ps1 -SelfContained
.\build-release.ps1
```

默认发布需要目标电脑安装 .NET 9 Desktop Runtime；`-SelfContained` 包含运行时。发布目录为 `dist/win-x64/`，ZIP 位于 `dist/`。普通构建不修改版本，发布脚本递增 `.csproj` 版本，失败时回滚。

两个脚本默认沿用当前网络配置；`-NuGetProxy <URL>` 指定本次发布的代理，`-NuGetProxy ''` 清除本次发布的代理环境变量，结束后恢复原环境。

数据继续保存在 `%AppData%\SidePeek`。旧 Rust/GPUI 源码与 Cargo 入口已从当前工作树移除，历史仍在 Git 中；现有 WPF 多屏/DPI 定位修复保留。

按 [AGENTS.md](AGENTS.md) 的项目约定，本次修改仅阅读代码和核对差异，未执行编译、构建、测试、Lint、静态检查或 CI。更多说明见 [开发环境](docs/SETUP.md)、[架构](docs/ARCHITECTURE.md) 和 [截图使用](docs/SCREENSHOTS.md)。
