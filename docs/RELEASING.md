# SidePeek 自动发布

把修改提交并 push 到 `main` 后，[发布工作流](../.github/workflows/release.yml) 自动完成整个发行过程。其他分支和单独推送 tag 不触发发行。

提交格式、分支同步和发布操作约定统一见 [项目规则](../AGENTS.md)。本文说明流水线的行为、产物及故障恢复方式。

工作流将提交标题和正文整理为更新说明，覆盖上次成功发布以来的提交。每笔提交使用独立小标题，正文编号保留为顶层有序列表，使 `1.`、`2.`、`3.` 在 GitHub 中按阿拉伯数字显示。历史提交保持原文。

## 版本与构建

1. 获取最新 main、完整提交历史和 tag，取 `.csproj` 与所有稳定版 tag 中的最大版本，递增补丁号。现有 `0.3.16` 的下一版为 `0.3.17`；也兼容历史 `v.0.3.2` 标签。
2. 更新 `.csproj` 与 `CHANGELOG.md`，按中文约定创建机器人提交。原子推送版本提交和附注 tag，使远端两者同时成功；不会强推分支或移动已有 tag。
3. 使用 .NET 9 SDK 和同一版本提交，调用现有 `build.ps1` 分别编译普通版和自包含版。两次构建均不再递增版本。
4. 创建或恢复 Release 草稿，上传全部附件，最后公开发布并设置 Latest。较旧版本的失败任务补发成功时，不会把更高版本的 Latest 降回去。

版本提交与 tag 在编译之前写入 GitHub，因此构建失败会保留该版本用于重试。失败版本的更新也会包含在下次成功版本的说明中。

产物如下（`版本号` 为自动生成的版本）：

| 文件 | 用途 |
| --- | --- |
| `SidePeek-版本号-win-x64.zip` | 普通版，需要 .NET 9 Desktop Runtime（x64） |
| `SidePeek-版本号-win-x64-selfcontained.zip` | 自包含版，无需另外安装运行时 |
| `SHA256SUMS.txt` | 两个 ZIP 的 SHA-256 校验值 |
| `release-notes.md` | 与 Release 正文一致的更新说明 |

解压 ZIP 后运行 `SidePeek.exe`。用户数据继续保存在 `%AppData%\SidePeek`。

## 连续推送与失败恢复

- 同时只执行一条发布流水线；正在发布的版本不会因新 push 被取消。GitHub 默认仅保留一个待执行任务，连续 push 的等待任务可能合并；每次准备版本都会读取最新 main，包含此前尚未发行的修改。
- 版本准备期间遇到新的 push，自动重新获取 main 并重试，最多三次。权限问题会直接报错，保留现有保护规则。
- 在 Actions 中选择失败运行并点击 **Re-run failed jobs** 或 **Re-run all jobs**，同一运行会找到原来的版本提交和 tag，不会重复递增版本。
- 也可在 main 上使用 **Run workflow**：若 main 顶部是未完成的自动版本提交，则继续发布该版本；已发布则跳过；有新提交则生成下一版。
- 两个包都编译成功后才创建 Release 草稿；附件全部上传成功后才公开。上传中断留下的草稿可在重跑时继续使用；已公开的 Release 不覆盖附件。
- 新版本已经发布后，重跑旧失败任务会补发旧版本，并保留更高版本为 Latest。
- 自动版本提交会使远端 main 前进；后续 push 提示非快进时，按 [版本与发布约定](../AGENTS.md#版本与发布约定) 同步分支。

## 仓库要求

工作流使用 GitHub 提供的 `GITHUB_TOKEN`，声明 `contents: write`，不需要配置个人访问令牌或额外 Secret。仓库需要启用 Actions，并允许工作流向 main 和版本 tag 写入。若未来增加分支保护或规则集，应为发布流程配置合适权限；工作流不会自行关闭保护。

版本准备、构建和发布放在同一条工作流，因为通过默认 `GITHUB_TOKEN` 推送提交或 tag [不会再触发其他工作流](https://docs.github.com/en/actions/how-tos/write-workflows/choose-when-workflows-run/trigger-a-workflow)。这也避免了版本提交引起递归发版。

所有构建发生在 GitHub 托管的 Windows runner 上；不修改开发机的永久代理配置。本地 `build.ps1` 和 `build-release.ps1` 保留原有用途，自动发版直接使用前者。
