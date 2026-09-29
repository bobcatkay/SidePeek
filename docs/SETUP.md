# SidePeek GPUI 开发环境

目标平台为 Windows 11 x64。当前实现使用 Rust 2024 + GPUI Kit 0.6.0；WPF 仅作为历史源码保留。

## 工具

- Rust MSVC 工具链，版本由根目录 rust-toolchain.toml 固定。通过 [Rust 官方安装程序](https://rustup.rs/) 安装。
- Visual Studio 2022/2026 或 Build Tools，勾选“使用 C++ 的桌面开发”、MSVC x64 和 Windows 10/11 SDK。
- PowerShell 7（打包脚本使用 ProcessStartInfo.ArgumentList）。
- 支持 DirectX 的显卡驱动。

不再需要 .NET SDK、WPF-UI 或 Node.js。Rust 编译器会自动发现 MSVC 和 Windows SDK。

## 常用命令

在已配置 Rust PATH 的终端执行：

~~~powershell
cargo run --locked
cargo test --locked
cargo fmt --all -- --check
cargo clippy --locked --all-targets --all-features -- -D warnings
.\build.ps1
.\build.ps1 -Configuration Debug -Offline
.\build-release.ps1 -Part Minor
~~~

本次迁移的本机工具链放在 .tools/cargo 和 .tools/rustup，未修改系统 PATH。构建脚本会自动识别；手动运行 Cargo 前可在当前 PowerShell 进程中设置：

~~~powershell
$env:CARGO_HOME = Join-Path $PWD '.tools\cargo'
$env:RUSTUP_HOME = Join-Path $PWD '.tools\rustup'
$env:PATH = "$env:CARGO_HOME\bin;$env:PATH"
~~~

## 网络与打包

build.ps1 的默认代理为 http://127.0.0.1:10808，仅传给 Cargo 子进程；脚本退出后不会改变终端或系统代理。传入 -Proxy '' 可直连，-Offline 使用本机缓存。旧参数 -NuGetProxy 是 -Proxy 的兼容别名；-SelfContained 可继续传入，GPUI 本身不依赖 .NET。

普通构建不会修改版本。build-release.ps1 递增 Cargo.toml 的版本，同步 Cargo.lock，再生成 ZIP；失败时同时回滚两份文件。

输出：
- dist/SidePeek-<版本>-gpui-win-x64/SidePeek.exe
- dist/SidePeek-<版本>-gpui-win-x64.zip

部署到其他电脑需要支持的 Windows/显卡驱动和 Microsoft Visual C++ x64 运行库。若系统报告缺少 VCRUNTIME DLL，请安装微软官方 Visual C++ Redistributable。

## 原生渲染与集成验证

测试使用独立目录，不读取真实便签或采集系统剪贴板，不修改开机自启。使用 GPUI 官方 render_to_image 验证真实 DirectX 渲染，覆盖各页面、深色主题和收起状态。

~~~powershell
$env:SIDEPEEK_DATA_DIR = Join-Path $PWD '.tools\smoke-data'
$env:SIDEPEEK_SMOKE_TEST = '1'
cargo run --locked --features smoke-test
Get-Content "$env:SIDEPEEK_DATA_DIR\smoke-result.txt"
~~~

每次测试请使用空的独立目录。结果为 PASS 时，13 张截图位于该目录 screenshots 下；同时检查原生表面、逻辑视口与停靠矩形的尺寸一致性，以及收起后再次展开。该测试功能不会进入普通发行包。

自动测试覆盖旧 JSON/日期兼容、未知字段保留、原子写入与归档恢复、连续悬停/DPI/固定窗口、参数替换、剪贴板去重/上限、stdout/stderr/退出码与进程树中断。键鼠操作、中文输入法候选窗、多屏热插拔和实际开机自启仍建议在目标机器上做人工验收。

## 数据

默认位置为 %AppData%\SidePeek。支持 SIDEPEEK_DATA_DIR 覆盖，便于开发隔离。首次写入已有文件前保留 backup-before-gpui 备份；日志位于 logs/。退出旧版后再运行新版，二者使用相同单实例锁。
