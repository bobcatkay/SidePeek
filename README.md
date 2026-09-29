# SidePeek

使用 Rust + [GPUI Kit](https://github.com/longbridge/gpui-kit) 实现的 Windows 11 屏幕边缘侧边栏。

- 便签：编辑与自动保存、颜色、置顶、拖动排序、完成归档与恢复。
- 命令：多行顺序执行、`%1` 参数、输入或候选值、静默执行、实时输出、进程树中断。
- 工具：时钟、内存状态、应用启动器与文件选择。
- 剪贴板：文本历史、去重、搜索、复制、排序和清空。
- 系统集成：左右停靠、显示器选择、DPI 缩放、悬停动画、托盘、全局热键、开机自启、浅色/深色/系统主题。

默认快捷键为 **Ctrl + Alt + S**。鼠标停留在边缘触发条上约 1 秒展开，移开后自动收起；点击星标可固定窗口。

## 构建

安装 Rust MSVC 工具链、Visual Studio C++ Build Tools 和 Windows SDK。仓库使用 `rust-toolchain.toml` 固定工具链，使用 `Cargo.lock` 固定依赖。

```powershell
cargo run --locked
cargo test --locked
cargo clippy --locked --all-targets -- -D warnings
cargo fmt --all -- --check

# PowerShell 7：构建并打包，无需 .NET
.\build.ps1
.\build.ps1 -Proxy ''  # 直连
.\build-release.ps1   # 递增版本号并打包
```

脚本也会自动识别项目 `.tools` 中的本地 Rust 工具链。发布文件位于 `dist/SidePeek-<版本>-gpui-win-x64/SidePeek.exe`。

## 数据兼容

继续使用 `%AppData%\SidePeek` 下的原有六个 JSON 文件，保留 WPF 的字段名、枚举编号和日期格式。首次修改前，原文件会备份到 `backup-before-gpui/`；损坏的文件会报错并保留，不会被默认数据覆盖。便签归档使用可恢复事务，避免两份文件保存中断导致丢失。

开发时可设置 `SIDEPEEK_DATA_DIR` 使用独立数据目录。新旧程序共用单实例锁，切换前请从托盘退出旧版。

如果旧版已开启开机自启，在新版设置中保存一次，将启动路径更新为新版可执行文件的位置。

原 WPF 源码保留在 `src/SidePeek.App` 供迁移对照；根目录构建脚本已切换到 GPUI，不再编译 WPF。

更多环境与验证说明见 [docs/SETUP.md](docs/SETUP.md)，实现说明见 [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)。
