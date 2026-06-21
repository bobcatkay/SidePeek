# SidePeek 架构方案

> 一个停靠在 Win11 屏幕边缘、悬停展开的轻量侧边栏工具，集成 **便签 / 快捷命令 / 小工具 / 剪切板** 四个模块。
> 技术栈：.NET 10 + WPF + WPF-UI（Fluent），MVVM + 依赖注入 + 模块化插件。

---

## 0. 实现现状（v0.2，当前代码）

> 第 2~10 节是**目标架构**（多项目分层）。当前为快速验证，先以**单工程**实现，结构清晰、后续可平滑拆分。

已落地：
- **亮色毛玻璃**：DWM 系统背景（`DWMSBT_TRANSIENTWINDOW` 亚克力）+ `DWMWA_WINDOW_CORNER_PREFERENCE` 圆角；`AllowsTransparency=False` + 透明合成背景，叠加半透明白色覆盖层。
- **左右吸附 + 悬停展开**：`DockManager` 轮询光标命中触发区，位置+尺寸同时缓动（cubic ease-out）。
- **收起态**：仅在停靠边中部露出「屏幕长边 12.5%、居中」的圆角触发块；展开铺满工作区。
- **便签**：卡片增改、完成归档、置顶、拖拽排序、颜色条；持久化 `notes.json` / `completed-notes.json`（编辑防抖 600ms 自动保存）。
- **快捷命令**：2 列卡片网格；添加/编辑对话框（图标/标题/描述/多条命令）；三点菜单删除/置顶；拖拽排序；点击执行，输出实时可见（`CommandRunnerWindow`）；持久化 `commands.json`。
- **小工具**：时钟、内存两张系统卡片 + exe 启动器网格（图标/标题/描述/exe 路径含浏览/启动参数）；三点菜单删除/置顶/编辑；拖拽排序；持久化 `tools.json`。
- **剪切板**：记录文本剪贴板历史，支持一键复制；持久化 `clipboard.json`。
- **系统托盘**：常驻图标，菜单含「显示/收起」「开机自启（勾选）」「退出」，双击切换展开/收起。
- **开机自启**：写 HKCU `...\Run`（`StartupService`），托盘菜单开关。
- **全局热键**：`Ctrl + Alt + S` 切换展开/收起（`RegisterHotKey` + `WndProc` 钩子）。
- **单实例**：`Mutex` 防止重复启动；`ShutdownMode=OnExplicitShutdown`（关窗不退，仅托盘退出）。
- **设置页与设置持久化**：右上角设置入口 + 托盘设置入口；显示当前应用版本；左右停靠边、收起延时、主题、开机自启、全局热键、便签历史时长写入 `settings.json`，并即时生效。
- **基础日志**：`AppLogger` 将启动与未处理异常写入 `%AppData%\SidePeek\logs\sidepeek-*.log`。
- **打包**：根目录 `build.ps1` 一键 `dotnet publish` 单文件 + zip 到 `dist/`，不修改版本；`build-release.ps1` 先自增 `<Version>` 再调用 `build.ps1`。

当前单工程文件结构：

```
src/SidePeek.App/
├─ App.xaml(.cs)              # Light 主题 + 单实例 + 托盘 + 全局异常落盘
├─ app.manifest              # PerMonitorV2 DPI
├─ Docking/DockManager.cs    # 吸附/悬停/收起(12.5%居中)/缓动 + Toggle + Suspend/Resume
├─ Interop/NativeMethods.cs  # GetCursorPos / GlobalMemoryStatusEx / DWM 背景与圆角 / RegisterHotKey
├─ Models/                   # DockEnums / NoteItem / CommandItem / ToolItem
├─ Services/                 # JsonStore / IconCatalog / StartupService / TrayService
├─ ViewModels/               # Notes / Commands / Widgets(=工具)
├─ Converters/               # StringToBrush
└─ Views/
   ├─ DockWindow            # 停靠主窗 + 分段 Tab + 亚克力 + 全局热键
   ├─ NotesView
   ├─ CommandsView + CommandEditWindow + CommandRunnerWindow
   └─ WidgetsView + ToolEditWindow
build.ps1                    # 普通编译打包脚本（不改版本）
build-release.ps1            # 发布打包脚本（自增版本后打包）
```

待办（见 `TASKS.md`）：Serilog 替换、多项目拆分、单元测试、多显示器。

---

## 1. 设计目标与约束

| 维度 | 目标 |
|---|---|
| 平台 | 仅 Windows 11（充分利用 Mica/Acrylic、Fluent） |
| 内存 | 空闲常驻 < 80MB；收起时暂停渲染/定时器 |
| 性能 | 悬停展开动画 60fps；冷启动 < 1.5s |
| 交互 | 四边吸附（上/下/左/右），鼠标悬停展开、移开收起 |
| 美观 | Fluent 设计、圆角、半透明材质、深浅色跟随系统 |
| 扩展 | 三个 Tab 模块化，未来可加新模块/小工具 |

---

## 2. 解决方案结构（Solution Layout）

```
SidePeek/
├── SidePeek.sln
├── Directory.Build.props          # 统一 TargetFramework / Nullable / LangVersion
├── src/
│   ├── SidePeek.App/              # WPF 启动入口、停靠主窗口、DI 引导、托盘
│   │   ├── App.xaml(.cs)
│   │   ├── Views/                 # DockWindow、ShellView、SettingsView
│   │   ├── ViewModels/            # ShellViewModel、DockViewModel
│   │   ├── Controls/              # 自定义控件（TriggerStrip 等）
│   │   ├── Resources/             # 主题、样式、图标
│   │   └── app.manifest           # PerMonitorV2 DPI 感知
│   │
│   ├── SidePeek.Core/             # 纯抽象层，无 UI 依赖
│   │   ├── Abstractions/          # IModule、IDockWindowService、ISettingsService...
│   │   ├── Models/                # DockEdge、DockState、AppSettings...
│   │   └── Events/                # 消息/事件定义
│   │
│   ├── SidePeek.Infrastructure/   # 平台实现
│   │   ├── Interop/               # Win32 P/Invoke（窗口定位、鼠标钩子、多屏）
│   │   ├── Docking/               # DockController 状态机 + 动画
│   │   ├── Persistence/           # JSON 存储、AppData 路径
│   │   ├── Hotkeys/               # 全局热键注册
│   │   └── Startup/               # 开机自启（注册表 Run）
│   │
│   └── SidePeek.Modules/          # 三个功能模块
│       ├── Notes/                 # 便签
│       ├── Commands/              # 快捷命令
│       └── Widgets/               # 小工具（含 widget 子插件）
│
├── tests/
│   └── SidePeek.Tests/            # 单元测试（xUnit）
├── docs/
│   ├── SETUP.md
│   ├── ARCHITECTURE.md
│   └── TASKS.md
└── assets/                        # 图标、截图
```

> 起步阶段三个模块作为 `SidePeek.Modules` 内的子文件夹/类即可，不必拆成独立程序集。待接口稳定后，可将 `Widgets` 抽成可热插拔的外部插件（`IModule` + 程序集动态加载）。

---

## 3. 分层与依赖方向

```
App ──> Modules ──┐
 │                ├──> Core (抽象)
 └──> Infrastructure ──┘
```

- **Core**：只有接口和模型，不引用任何其它项目，保证可测试、可替换。
- **Infrastructure**：实现 Core 接口（Win32、存储、热键）。
- **Modules**：依赖 Core 抽象，提供各 Tab 的 View + ViewModel + 数据服务。
- **App**：组合根（Composition Root），用 DI 把以上装配起来。

---

## 4. 核心抽象（SidePeek.Core）

```csharp
// 每个 Tab 模块统一实现此接口
public interface IModule
{
    string Id { get; }            // "notes" / "commands" / "widgets"
    string Title { get; }         // 显示名
    SymbolRegular Icon { get; }   // WPF-UI 图标
    int Order { get; }            // 排序
    object CreateView();          // 懒加载：首次激活时创建 UserControl
}

// 停靠窗口服务：管理吸附边、显示/隐藏、动画
public interface IDockWindowService
{
    DockEdge Edge { get; set; }       // Left / Right / Top / Bottom
    DockState State { get; }          // Hidden / Peeking / Expanded
    void Expand();
    void Collapse();
    void MoveToEdge(DockEdge edge);
}

public interface ISettingsService
{
    AppSettings Current { get; }
    void Save();
    event EventHandler? Changed;
}

// 通用键值/集合持久化
public interface IStorageService
{
    T? Load<T>(string key);
    void Save<T>(string key, T value);
}
```

```csharp
public enum DockEdge { Left, Right, Top, Bottom }
public enum DockState { Hidden, Peeking, Expanded }
```

---

## 5. 关键技术点：边缘吸附 + 悬停展开

### 5.1 窗口形态
- `WindowStyle=None`、`ResizeMode=NoResize`、`ShowInTaskbar=False`、`Topmost=True`、`AllowsTransparency=False`。
- **亮色毛玻璃**：`SourceInitialized` 时把合成背景设为透明（`HwndTarget.BackgroundColor=Transparent`）、`DwmExtendFrameIntoClientArea(-1)`、`DWMWA_SYSTEMBACKDROP_TYPE=DWMSBT_TRANSIENTWINDOW`（亚克力）、`DWMWA_WINDOW_CORNER_PREFERENCE=ROUND`；面板内再叠加 40% 白色覆盖层提升可读性。
- **不使用 AppBar（`SHAppBarMessage`）**——AppBar 会像任务栏一样永久占用桌面工作区。本项目要的是「悬浮覆盖 + 自动隐藏」，所以用普通置顶窗口自行管理位置。

### 5.2 状态机 + 收起尺寸（DockManager）
```
        光标进入触发块                 展开动画完成
Hidden ───────────────────────────────────────> Expanded
  ^                                                  │
  └────────────── 光标离开 + 延时(450ms) ────────────┘
```
- **Hidden（收起）**：窗口主体移出屏幕外，仅在停靠边**中部**露出「沿边长度 = 屏幕长边 12.5%、居中」的圆角触发块（厚度 ~6px）。
- **Expanded（展开）**：沿停靠边铺满工作区。
- 展开/收起对 **位置(Left/Top) 与 尺寸(Width/Height) 同时做缓动**（手动 15ms 定时器 + cubic ease-out，约 220ms），因此收起时是「从中部小块展开/收拢」的效果。
- 离开窗口区域后启动延时计时器，超时则收起；用延时避免误触。对话框打开期间 `Suspend()` 暂停轮询，避免误收起。

### 5.3 悬停检测方案
- 首选 **DispatcherTimer 轮询光标位置**（`GetCursorPos`）+ 命中触发条矩形判断，简单、稳定、无需全局钩子权限。
- 进阶可用低级鼠标钩子（`SetWindowsHookEx WH_MOUSE_LL`），但轮询（~30ms）已足够且更省心。

### 5.4 Win32 互操作（Interop/NativeMethods）
| API | 用途 | 现状 |
|---|---|---|
| `GetCursorPos` | 悬停检测 | ✅ 已用 |
| `GlobalMemoryStatusEx` | 内存小工具 | ✅ 已用 |
| `DwmSetWindowAttribute` | 亚克力背景 + 圆角 | ✅ 已用 |
| `DwmExtendFrameIntoClientArea` | 让背景铺满客户区 | ✅ 已用 |
| `RegisterHotKey` / `UnregisterHotKey` | 全局热键 Ctrl+Alt+S | ✅ 已用 |
| `MonitorFromWindow` / `GetMonitorInfo` | 多显示器边界 | ⏳ 待做（当前用主屏 `WorkArea`）|
| DPI：`app.manifest` 声明 `PerMonitorV2` | 多屏缩放正确 | ✅ 已用 |

### 5.5 多显示器与 DPI
- 停靠到「当前光标所在屏幕」或用户指定屏幕。
- 用每屏 DPI 换算逻辑像素，避免高分屏错位。

---

## 6. 三个功能模块设计

### 6.1 便签 Notes
- 现状：卡片列表，标题/正文可直接编辑、置顶、删除、颜色条。
- 待办：持久化（`notes.json`）、防抖自动保存、搜索。

### 6.2 快捷命令 Commands
- 布局：2 列卡片网格（`UniformGrid Columns=2`，`VerticalAlignment=Top` 避免行被拉伸），每卡 = 圆形图标 + 标题 + 描述 + 右上角删除「×」。
- 添加/编辑：`CommandEditWindow`——可选图标（`IconCatalog` 字形）、强调色、**标题(必填)**、描述、**命令(必填，多行=多条按顺序执行)**。
- 删除：`MessageBox` 弹窗确认。
- 执行可见：`CommandRunnerWindow` 用 `cmd /c` 逐条执行，重定向 stdout/stderr 实时滚动显示，并显示每条退出码。
- 存储：`commands.json`。

### 6.3 小工具 / 工具 Widgets
- 顶部系统卡片：**时钟**（时间+日期）、**内存占用**（进度条）。（已按需求去掉「开机时长」）
- 下半部「应用启动器」：与命令一致的 2 列网格，每项 = exe 启动器。
- 添加/编辑：`ToolEditWindow`——图标、强调色、**标题(必填)**、描述、**exe 路径(必填，可粘贴或「浏览…」经 `OpenFileDialog` 选择)**、启动参数；删除需确认。
- 存储：`tools.json`。
- 性能：仅当该 Tab 可见时刷新时钟/内存定时器（`Unloaded` 暂停）。

---

## 7. 性能与内存策略

- **单实例**：`Mutex` 防止重复启动（✅ 已实现）。
- **懒加载**：Tab 内容首次激活才创建 View（✅）。
- **收起即省电**：收起后小工具定时器暂停（`Unloaded`）（✅，进一步可在 `DockState.Hidden` 时统一暂停）。
- **托盘常驻**：`ShutdownMode=OnExplicitShutdown`，仅托盘「退出」结束进程（✅）。
- **资源冻结**：`Freeze()` 共享 Brush/Geometry。
- **AOT/裁剪（可选）**：发布时用 `dotnet publish` 启用 trimming 减小体积。

---

## 8. 配置与持久化

- 路径：`%AppData%\SidePeek\`
  - `commands.json` / `tools.json` / `notes.json`：模块数据（✅ 已实现，`Services/JsonStore`）。
  - 开机自启写入注册表 HKCU `...\Run`（非文件，`StartupService`）。
  - `settings.json`：停靠边、触发延时、主题、热键自定义（✅ 已实现，`Services/SettingsService`）。
  - `logs/`：基础文件日志（✅ 已实现，`Services/AppLogger`）；Serilog 滚动日志（⏳ 可后续替换）。
- 序列化：`System.Text.Json`（`WriteIndented`；读失败回退默认值）。

---

## 9. 主题与视觉

- 当前固定 **Light 主题**（`ApplicationThemeManager.Apply(Light)`）+ DWM 亚克力 = 亮色毛玻璃；后续可加「跟随系统」。
- 圆角 + Fluent 控件（`ui:Button`、`ui:TextBox`、`ui:ProgressRing` 等）。
- Tab 切换用**自定义分段控件**（三个 `RadioButton` + 模板，选中态 Accent 高亮），比 `NavigationView` 更轻、更易适配窄面板；后续如需可换回 `NavigationView`。

---

## 10. 后续可演进点

- Widgets 外部插件热加载（`AssemblyLoadContext`）。
- 云同步便签/命令。
- 多套停靠预设（不同屏幕不同布局）。
- 触摸/手势支持。
