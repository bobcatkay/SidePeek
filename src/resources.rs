//! Shared copy and dimensions keep the narrow sidebar consistent across views.
pub mod text {
    pub const APP: &str = "SidePeek";
    pub const NOTES: &str = "便签";
    pub const COMMANDS: &str = "命令";
    pub const TOOLS: &str = "工具";
    pub const CLIPBOARD: &str = "剪贴板";
    pub const SETTINGS: &str = "设置";
    pub const ADD: &str = "新建";
    pub const EDIT: &str = "编辑";
    pub const SAVE: &str = "保存";
    pub const CANCEL: &str = "取消";
    pub const DELETE: &str = "删除";
    pub const CONFIRM_DELETE: &str = "确认删除此项？";
    pub const DELETE_DETAIL: &str = "此操作会从本地记录中移除此项。";
    pub const CLOSE: &str = "关闭";
    pub const BACK: &str = "返回";
    pub const PIN: &str = "置顶";
    pub const UNPIN: &str = "取消置顶";
    pub const PIN_WINDOW: &str = "固定窗口";
    pub const COLLAPSE: &str = "收起";
    pub const TOGGLE: &str = "显示 / 收起";
    pub const EXIT: &str = "退出 SidePeek";
    pub const COPY: &str = "复制";
    pub const COPIED: &str = "已复制";
    pub const COMPLETE: &str = "完成";
    pub const RESTORE: &str = "恢复";
    pub const HISTORY: &str = "已完成";
    pub const ACTIVE: &str = "进行中";
    pub const TITLE: &str = "标题";
    pub const CONTENT: &str = "内容";
    pub const DESCRIPTION: &str = "描述（可选）";
    pub const COLOR: &str = "颜色";
    pub const ICON: &str = "图标";
    pub const NEW_NOTE: &str = "新便签";
    pub const EMPTY_NOTES: &str = "记下此刻的想法";
    pub const EMPTY_NOTES_DETAIL: &str = "新建一条便签，内容会自动保存。";
    pub const EMPTY_HISTORY: &str = "还没有已完成的便签";
    pub const EMPTY_COMMANDS: &str = "把常用命令放在手边";
    pub const EMPTY_COMMANDS_DETAIL: &str = "添加命令后，点按卡片即可执行。";
    pub const EMPTY_TOOLS: &str = "添加常用应用";
    pub const EMPTY_TOOLS_DETAIL: &str = "选择程序，随时从侧边栏启动。";
    pub const EMPTY_CLIPBOARD: &str = "复制的文字会出现在这里";
    pub const CLIPBOARD_DETAIL: &str = "仅保存在本机，最多保留 200 条。";
    pub const SEARCH: &str = "搜索内容…";
    pub const NO_RESULTS: &str = "没有匹配的内容";
    pub const NOTE_SAVED: &str = "已自动保存";
    pub const SAVE_FAILED: &str = "保存失败，内容仍在内存中；请检查数据目录权限后重试。";
    pub const RETRY: &str = "重试保存";
    pub const COMMAND_TEXT: &str = "命令（每行一条，按顺序执行）";
    pub const COMMAND_HINT: &str = "用 %1、%2 引用下面定义的参数。";
    pub const SILENT: &str = "静默执行";
    pub const PARAMETERS: &str = "参数";
    pub const ADD_PARAMETER: &str = "添加参数";
    pub const PARAMETER_LABEL: &str = "参数名称";
    pub const DEFAULT_VALUE: &str = "默认值";
    pub const PROMPT: &str = "输入值";
    pub const CHOICES: &str = "候选值";
    pub const CHOICE_LABEL: &str = "选项名称";
    pub const CHOICE_VALUE: &str = "选项值";
    pub const ADD_CHOICE: &str = "添加选项";
    pub const DEFAULT_CHOICE: &str = "设为默认";
    pub const RUN: &str = "执行";
    pub const RUNNING: &str = "正在执行…";
    pub const STOP: &str = "中断";
    pub const STOPPING: &str = "正在中断…";
    pub const STOPPED: &str = "已中断";
    pub const FINISHED: &str = "执行完成";
    pub const RUN_FAILED: &str = "执行失败";
    pub const OUTPUT: &str = "执行输出";
    pub const OUTPUT_LIMIT: &str = "较早的输出已截断。\n";
    pub const VALIDATE_TITLE: &str = "请填写标题。";
    pub const VALIDATE_COMMAND: &str = "请至少填写一条命令。";
    pub const VALIDATE_PARAMETER: &str = "请填写参数名称，以及候选项的名称和值。";
    pub const VALIDATE_PATH: &str = "请选择或填写应用路径。";
    pub const EXE_PATH: &str = "应用路径";
    pub const BROWSE: &str = "浏览…";
    pub const ARGUMENTS: &str = "启动参数（可选）";
    pub const MEMORY: &str = "内存";
    pub const LAUNCHERS: &str = "应用启动器";
    pub const DOCK_EDGE: &str = "停靠位置";
    pub const LEFT: &str = "左侧";
    pub const RIGHT: &str = "右侧";
    pub const DISPLAY: &str = "显示器";
    pub const PRIMARY_DISPLAY: &str = "主显示器";
    pub const THEME: &str = "外观";
    pub const LIGHT: &str = "浅色";
    pub const DARK: &str = "深色";
    pub const SYSTEM: &str = "跟随系统";
    pub const EXPAND_DELAY: &str = "悬停展开延时（毫秒）";
    pub const COLLAPSE_DELAY: &str = "移开收起延时（毫秒）";
    pub const HISTORY_MONTHS: &str = "已完成便签保留月数";
    pub const STARTUP: &str = "开机自启";
    pub const HOTKEY: &str = "全局快捷键";
    pub const HOTKEY_HINT: &str = "至少选择一个修饰键；主键支持 A–Z 和 F1–F12。";
    pub const CTRL: &str = "Ctrl";
    pub const ALT: &str = "Alt";
    pub const SHIFT: &str = "Shift";
    pub const VALIDATE_SETTINGS: &str = "请检查延时、保留月数及快捷键格式。";
    pub const SETTINGS_SAVED: &str = "设置已保存";
    pub const NATIVE_FAILED: &str = "系统集成操作失败，请检查日志；快捷键可能已被占用。";
    pub const OPEN_DATA: &str = "打开数据目录";
    pub const CLEAR: &str = "清空";
    pub const CLEAR_CLIPBOARD: &str = "清空剪贴板历史？";
    pub const CLEAR_CLIPBOARD_DETAIL: &str = "仅清除 SidePeek 保存的历史，不改变系统剪贴板。";
    pub const WELCOME_TITLE: &str = "欢迎使用 SidePeek";
    pub const WELCOME_CONTENT: &str =
        "把鼠标移到屏幕边缘即可展开，移开自动收起。\n便签支持编辑、置顶、拖拽排序和完成归档。";
    pub const WEEK: &str = "7 天";
    pub const MONTH: &str = "30 天";
    pub const QUARTER: &str = "90 天";
    pub const YEAR: &str = "1 年";
    pub const ALL: &str = "全部";
    pub const DRAG: &str = "拖动排序";
    pub const VERSION: &str = concat!("v", env!("CARGO_PKG_VERSION"), " · GPUI");
}

pub mod layout {
    pub const PANEL_WIDTH: f32 = 420.0;
    pub const TRIGGER_WIDTH: f32 = 6.0;
    pub const TRIGGER_RATIO: f32 = 0.10;
    pub const SPACE_XS: f32 = 4.0;
    pub const SPACE_SM: f32 = 8.0;
    pub const SPACE_MD: f32 = 12.0;
    pub const SPACE_LG: f32 = 16.0;
    pub const SPACE_XL: f32 = 24.0;
    pub const CARD_RADIUS: f32 = 12.0;
    pub const BODY_SIZE: f32 = 14.0;
    pub const SMALL_SIZE: f32 = 12.0;
    pub const TITLE_SIZE: f32 = 18.0;
    pub const CLOCK_SIZE: f32 = 26.0;
    pub const CONTENT_HEIGHT: f32 = 110.0;
    pub const EDITOR_HEIGHT: f32 = 160.0;
    pub const CARD_MIN_HEIGHT: f32 = 116.0;
    pub const OUTPUT_HEIGHT: f32 = 320.0;
    pub const ICON_SIZE: f32 = 20.0;
    pub const NOTE_STRIPE_WIDTH: f32 = 3.0;
    pub const GRID_COLUMNS: usize = 2;
    pub const SURFACE_OPACITY: f32 = 0.94;
}

pub const PALETTE: [&str; 6] = [
    "#FFB454", "#4C8DFF", "#3FD27F", "#FF6B6B", "#B888FF", "#36C5D6",
];
pub const ICONS: [&str; 8] = [
    "terminal",
    "globe",
    "folder",
    "file",
    "calculator",
    "rocket",
    "settings",
    "app",
];
pub const DEFAULT_COMMAND_ACCENT: &str = "#4C8DFF";
pub const DEFAULT_TOOL_ACCENT: &str = "#3FD27F";
pub const LOG_TAG: &str = "sidepeek";
