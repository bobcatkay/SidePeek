# SidePeek 可执行任务清单

> 按阶段拆分，每个任务都可独立完成并验证。建议按顺序推进；带 `[CLI]` 的给出了可直接运行的命令。
> 勾选规则：完成并通过「验收标准」后再打 `[x]`。

---

## 进度快照（v0.3）

> 注意：当前先以**单工程**快速实现（非阶段 0 规划的多项目结构），后续再拆分。

- ✅ 解决方案/工程脚手架、NuGet 依赖、可运行
- ✅ 停靠窗口：左右吸附、悬停展开、缓动动画
- ✅ 收起态：屏幕长边 12.5%、垂直/水平居中的触发块
- ✅ 亮色毛玻璃（DWM 亚克力 + 圆角）
- ✅ 自定义分段 Tab + 懒加载
- ✅ 便签：增改/置顶/拖拽排序/完成历史 + 持久化（`notes.json` + `completed-notes.json`，防抖自动保存）
- ✅ 快捷命令：2 列网格 + 添加/编辑 + 三点菜单（删除/置顶）+ 拖拽排序 + 执行输出可见 + 持久化
- ✅ 工具：时钟/内存卡片 + exe 启动器网格（添加/编辑/浏览/参数）+ 三点菜单 + 拖拽排序 + 持久化
- ✅ 剪切板：历史文本记录 + 一键复制 + 持久化（`clipboard.json`）
- ✅ 系统托盘（显示/收起、开机自启开关、退出）
- ✅ 开机自启（HKCU Run，`StartupService`）
- ✅ 全局热键 Ctrl+Alt+S 切换展开/收起
- ✅ 单实例 Mutex + 关窗不退（仅托盘退出）
- ✅ 编译打包脚本：`build.ps1` 普通打包不改版本，`build-release.ps1` 自增版本后打包
- ✅ 设置页：显示版本号；左右停靠、收起延时、主题、开机自启、全局热键、便签历史时长 + `settings.json`
- ✅ 基础日志落盘：`%AppData%\SidePeek\logs\sidepeek-*.log`
- ⏳ Serilog 替换 / 多项目拆分 / 单元测试 / 多显示器

---

## 阶段 0：环境与脚手架

- [ ] **T0.1 安装工具** `[CLI]`
  ```powershell
  winget install Microsoft.DotNet.SDK.9
  winget install Git.Git
  ```
  验收：重启终端后 `dotnet --version` 输出 9.x。

- [ ] **T0.2 创建解决方案与项目** `[CLI]`（在 `D:\MyProjects\SidePeek` 下执行）
  ```powershell
  dotnet new sln -n SidePeek
  dotnet new wpf      -n SidePeek.App            -o src/SidePeek.App
  dotnet new classlib -n SidePeek.Core           -o src/SidePeek.Core
  dotnet new classlib -n SidePeek.Infrastructure -o src/SidePeek.Infrastructure
  dotnet new classlib -n SidePeek.Modules        -o src/SidePeek.Modules
  dotnet new xunit    -n SidePeek.Tests          -o tests/SidePeek.Tests

  dotnet sln add (Get-ChildItem -r *.csproj)
  ```
  验收：`dotnet build` 成功。

- [ ] **T0.3 配置项目引用与目标框架**
  - `App` → 引用 `Core`、`Infrastructure`、`Modules`
  - `Infrastructure`、`Modules` → 引用 `Core`
  - 所有 `csproj` 设 `<TargetFramework>net9.0-windows</TargetFramework>`，类库需要 WPF 类型的设 `<UseWPF>true</UseWPF>`。
  - 新建 `Directory.Build.props` 统一 `Nullable`、`LangVersion`、`ImplicitUsings`。
  验收：`dotnet build` 成功，引用关系正确。

- [ ] **T0.4 安装 NuGet 依赖** `[CLI]`
  ```powershell
  dotnet add src/SidePeek.App package WPF-UI
  dotnet add src/SidePeek.App package CommunityToolkit.Mvvm
  dotnet add src/SidePeek.App package Microsoft.Extensions.Hosting
  dotnet add src/SidePeek.App package Serilog
  dotnet add src/SidePeek.App package Serilog.Sinks.File
  ```
  验收：`dotnet restore` 成功。

- [ ] **T0.5 .gitignore 与初始提交**
  ```powershell
  dotnet new gitignore
  git add . ; git commit -m "chore: scaffold SidePeek solution"
  ```

---

## 阶段 1：应用骨架与 DI

- [ ] **T1.1 DI / Host 引导**：在 `App.xaml.cs` 用 `Microsoft.Extensions.Hosting` 构建容器，注册服务与 ViewModel；接管 `OnStartup`/`OnExit`。
- [ ] **T1.2 单实例**：用 `Mutex` 防重复启动，第二次启动唤起已有实例。
- [ ] **T1.3 日志**：接入 Serilog，输出到 `%AppData%\SidePeek\logs\`。（当前已有 `AppLogger` 基础文件日志）
- [x] **T1.4 应用设置**：实现 `SettingsService` + `AppSettings`，读写 `settings.json`。
- [x] **T1.5 托盘图标**：托盘菜单（显示/设置/退出），关闭窗口不退出进程。
- 验收：运行后无主窗口可见，托盘有图标，菜单可退出；日志/设置文件生成。

---

## 阶段 2：停靠窗口核心（项目重点）

- [ ] **T2.1 DockWindow 外观**：无边框、置顶、不进任务栏、Mica/圆角、`PerMonitorV2`（改 `app.manifest`）。
- [ ] **T2.2 Win32 Interop**：封装 `SetWindowPos` / `GetCursorPos` / `MonitorFromWindow` / `GetMonitorInfo` 的 P/Invoke。
- [ ] **T2.3 边缘定位**：实现 `MoveToEdge(DockEdge)`，按停靠边把窗口贴到当前屏幕边缘（含 DPI 换算）。
- [ ] **T2.4 触发条 TriggerStrip**：Hidden 态下只露 ~4px 触发条。
- [ ] **T2.5 悬停检测**：`DispatcherTimer`(~30ms) 轮询光标，命中触发条 → `Expand()`；离开窗口 → 延时 `Collapse()`。
- [ ] **T2.6 滑入/滑出动画**：`DoubleAnimation` 平移，按停靠边切换动画轴；状态机 Hidden/Peeking/Expanded。
- [ ] **T2.7 多显示器**：选「光标所在屏」或设置指定屏，边界正确。
- 验收：四个边都能吸附；鼠标移到边缘平滑展开、移开自动收起；多屏/高分屏不错位。

---

## 阶段 3：Shell 与 Tab 框架

- [ ] **T3.1 IModule 抽象**：定义 `IModule`，App 通过 DI 收集所有模块。
- [ ] **T3.2 ShellView**：用 WPF-UI `NavigationView` 承载三个 Tab，按停靠边自适应（左右停靠用左侧导航，上下停靠用顶部导航）。
- [ ] **T3.3 懒加载**：Tab 首次激活才 `CreateView()`，缓存实例。
- 验收：三个空 Tab 可切换；首次点击才初始化对应视图。

---

## 阶段 4：便签模块 Notes

- [ ] **T4.1 模型与存储**：`NoteItem` + `notes.json` 持久化（`IStorageService`）。
- [ ] **T4.2 列表与编辑**：便签列表、新增/删除/编辑、颜色标签、置顶。
- [ ] **T4.3 自动保存**：编辑防抖（~500ms）自动落盘。
- [ ] **T4.4 搜索**：按标题/内容过滤。
- 验收：增删改即时保存，重启后数据保留，搜索可用。

---

## 阶段 5：快捷命令模块 Commands

- [ ] **T5.1 模型与存储**：`CommandItem`（类型：exe/url/脚本/文件夹）+ `commands.json`。
- [ ] **T5.2 执行**：`Process.Start` 启动；URL 用默认浏览器；记录最近使用。
- [ ] **T5.3 管理 UI**：增删改、分组、图标、搜索。
- [ ] **T5.4 安全确认（可选）**：执行脚本类命令前确认。
- 验收：能正确启动各类命令；配置持久化。

---

## 阶段 6：小工具模块 Widgets

- [ ] **T6.1 IWidget 抽象**：标题/图标/CreateView/刷新间隔。
- [ ] **T6.2 内置 widget**：时钟日历、系统监控(CPU/内存)、计算器、取色器、倒计时（先做 2-3 个）。
- [ ] **T6.3 刷新调度**：仅窗口展开且该 Tab 可见时刷新；收起即暂停定时器。
- 验收：widget 正常显示与刷新；收起后 CPU 占用回落。

---

## 阶段 7：设置页与体验打磨

- [x] **T7.1 设置页**：左右停靠边、触发延时、主题(深/浅/跟随系统)、开机自启、全局热键、便签历史保留时长。
- [x] **T7.2 开机自启**：写注册表 `Run` 项，可开关。
- [x] **T7.3 全局热键**：`RegisterHotKey` 一键唤起/隐藏。
- [ ] **T7.4 性能核对**：空闲内存 < 80MB；收起时定时器全部暂停。
- 验收：设置项即时生效并持久化；性能达标。

---

## 阶段 8：测试与发布

- [ ] **T8.1 单元测试**：覆盖 Core/Infrastructure 的纯逻辑（DPI 换算、存储、状态机）。
- [ ] **T8.2 发布** `[CLI]`
  ```powershell
  .\build.ps1           # 不修改版本号
  .\build-release.ps1   # 自增 Patch 版本后发布
  ```
- [ ] **T8.3（可选）裁剪/单文件优化**，减小体积。
- 验收：产出可双击运行的发布包；首启正常。

---

## 里程碑

| 里程碑 | 包含阶段 | 可演示成果 |
|---|---|---|
| M1 骨架可跑 | 0-1 | 托盘常驻、设置/日志生成 |
| M2 核心交互 | 2-3 | 边缘吸附 + 悬停展开 + 三个空 Tab |
| M3 功能完整 | 4-6 | 便签/命令/小工具均可用 |
| M4 可发布 | 7-8 | 设置完善、性能达标、发布包 |
