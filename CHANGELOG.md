# 更新日志

## v0.3.17

### 更新内容

- 完善停靠交互、构建流程与命令中断 ([c55e33b](https://github.com/bobcatkay/SidePeek/commit/c55e33b00b987754f72f1e40279a1776e7cf67d6))

- 迁移 Rust/GPUI 并修复 WPF 缩放定位，清理构建产物跟踪 ([c30120c](https://github.com/bobcatkay/SidePeek/commit/c30120c7e288f7605f01e9f3f9ff7b422882d923))

- Restore .NET app with HDR screenshot tools and fix NuGet proxy defaults ([d109b4c](https://github.com/bobcatkay/SidePeek/commit/d109b4c268910fe210abf7ef36ed7bf2011fea25))

- 新增字体支持，优化截图与侧边栏，实现自动发布 ([f0466f7](https://github.com/bobcatkay/SidePeek/commit/f0466f793ebdf46c204d5c09bbd5df191544b181))
  1. 新增系统字体选择、即时预览和自动保存，统一应用字体资源。
  2. 调整截图工具栏的图标分组、主题样式、标注选项和操作提示。
  3. 修复侧边栏胶囊背景与裁剪，支持同屏全屏隐藏及截图结束后的无焦点恢复。
  4. 新增 main 推送后的自动版本递增、更新日志、标签、双版本编译打包和 GitHub Release 发布流程。
  5. 支持失败重跑复用版本、连续推送串行处理和发布包 SHA-256 校验，补充发布文档与中文提交约定。

### 下载说明

- 普通版：需要安装 .NET 9 Desktop Runtime（Windows x64）。
- 自包含版：文件名带 selfcontained，无需另外安装 .NET 运行时。
- 解压后运行 SidePeek.exe；SHA256SUMS.txt 用于核对下载文件。

[完整变更](https://github.com/bobcatkay/SidePeek/compare/v0.3.6...v0.3.17)
