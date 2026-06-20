# SidePeek 开发环境与工具

> 目标平台：Windows 11；技术栈：.NET 10 + WPF + WPF-UI。

## 1. 必装工具

| 工具 | 版本 | 用途 | 安装命令 |
|---|---|---|---|
| .NET SDK | 10.x (LTS) | WPF 编译/运行核心 | `winget install Microsoft.DotNet.SDK.10` |
| Git | 最新 | 版本管理 | `winget install Git.Git` |

> 装完后**重启终端**（或重启 Cursor），让 PATH 生效。

## 2. 不需要安装的工具

- **Visual Studio**：非必须。Cursor + C# 扩展即可开发 WPF。VS 唯一的优势是 XAML 可视化设计器，但本项目界面基本手写 XAML（WPF-UI Fluent），不依赖设计器。
- **cmake / MSVC / C++ 工具链**：完全不需要。cmake 是 C/C++ 的构建系统，.NET 用 `dotnet` CLI + MSBuild，二者无关。
- **Node.js / npm**：不需要（非 Web 方案）。

## 3. 推荐的 Cursor / VS Code 扩展

- **C# Dev Kit**（`ms-dotnettools.csdevkit`）——C# 语言支持、调试、解决方案管理。
- **C#**（`ms-dotnettools.csharp`，会随 Dev Kit 一起装）。
- 可选：**XAML Styler**、**.NET Install Tool**。

## 4. 验证环境

安装并重启终端后执行：

```powershell
dotnet --version          # 应输出 10.x.x
dotnet --list-sdks        # 应能看到 10.x 的 SDK
git --version             # 应输出 git version ...
```

## 5. NuGet 依赖（无需手动安装，dotnet restore 自动还原）

| 包 | 用途 |
|---|---|
| `WPF-UI` | Fluent 设计控件 / Mica 材质 / 主题 |
| `CommunityToolkit.Mvvm` | MVVM（ObservableObject / RelayCommand） |
| `Microsoft.Extensions.Hosting` | 依赖注入 + 应用生命周期 |
| `Microsoft.Extensions.DependencyInjection` | DI 容器 |
| `Serilog` + `Serilog.Sinks.File` | 日志 |
| `Hardcodet.NotifyIcon.Wpf` | 系统托盘图标（WPF-UI 自带 TrayIcon 也可） |
| `System.Text.Json` | 配置/数据持久化（.NET 内置） |

> 数据存储默认用 JSON 文件，存放在 `%AppData%\SidePeek\`。若便签数据量增大，可后续切换到 SQLite（`Microsoft.Data.Sqlite` 或 `LiteDB`）。

## 6. 常用命令速查

```powershell
dotnet restore                       # 还原 NuGet 依赖
dotnet build                         # 编译
dotnet run --project src/SidePeek.App # 运行
dotnet test                          # 运行单元测试

# 打包为单文件 exe（输出到 dist\）
.\build.ps1                          # framework-dependent（需目标机装 .NET 10 桌面运行时，~6MB）
.\build.ps1 -SelfContained           # self-contained（自带运行时，免安装，~150MB）
```

## 8. 运行时快捷键与托盘

- 全局热键 **Ctrl + Alt + S**：展开 / 收起面板。
- 鼠标移到屏幕停靠边中部的小触发块：悬停展开；移开自动收起。
- 系统托盘图标：双击切换显示；右键菜单含「开机自启」开关与「退出」。
- 数据保存在 `%AppData%\SidePeek\`（`notes.json` / `commands.json` / `tools.json`）。

## 7. 网络代理（本机环境）

访问 NuGet/GitHub 若需要代理：

```powershell
$env:HTTP_PROXY='http://127.0.0.1:10809'; $env:HTTPS_PROXY='http://127.0.0.1:10809'
```
