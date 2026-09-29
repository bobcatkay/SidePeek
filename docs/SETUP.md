# SidePeek 开发环境

- Windows 11 x64。
- .NET 9 SDK；Visual Studio 可选，安装“.NET 桌面开发”工作负载。
- 支持 DirectX 11/Desktop Duplication 的显卡驱动。
- NuGet 依赖：CommunityToolkit.Mvvm、WPF-UI、Vortice.Direct3D11 3.8.3。

打开 `SidePeek.slnx`，或显式指定 WPF 项目运行：

```powershell
dotnet run --project src/SidePeek.App/SidePeek.App.csproj
```

## 发布

```powershell
.\build.ps1
.\build.ps1 -NuGetProxy ''
.\build.ps1 -NuGetProxy 'http://127.0.0.1:10809'
.\build.ps1 -SelfContained
.\build-release.ps1 -Part Minor
.\build-release.ps1 -NuGetProxy ''
```

脚本默认沿用当前网络配置，不再强制指定本地代理端口。`-NuGetProxy <URL>` 可为本次发布指定代理；`-NuGetProxy ''` 清除本次发布的 `HTTP_PROXY`、`HTTPS_PROXY` 和 `ALL_PROXY` 环境变量。发布结束后恢复原环境；系统代理或 NuGet 配置中的代理仍需在对应配置中调整。两个脚本均支持 `-Proxy` 兼容别名。发布文件位于 `dist/win-x64/`，ZIP 位于 `dist/`；发行脚本修改 `.csproj` 的版本，失败会还原。

默认包依赖 .NET 9 Desktop Runtime；自包含包不要求安装运行时。源码及构建入口已经恢复为 .NET，保留现有 WPF 多屏/DPI 修复，不再需要 Rust 工具链。

## 数据及截图

数据仍位于 `%AppData%\SidePeek`，日志位于该目录下的 `logs/`。新旧实现使用相同单实例锁；运行 .NET 版本前先退出旧程序。

截图默认 `Ctrl+Alt+A`，侧边栏默认 `Ctrl+Alt+S`。HDR 自动映射为 sRGB；PNG 和剪贴板输出均为 SDR。受系统保护的画面可能被系统遮挡；DirectX 采集失败会显示原因并恢复侧边栏。

按 AGENTS.md，除非用户明确要求，不执行编译、构建、测试、Lint、静态检查或主动触发 CI。此次功能的硬件运行验收尚未执行，具体操作见 [SCREENSHOTS.md](SCREENSHOTS.md)。
