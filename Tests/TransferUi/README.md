# 上传重名与退出保护界面验收

此可选入口使用生产 `MainWindow`、`TaskManagerPage`、`TransferDialogs`、`TransferWindowCloseGuard` 和上传/下载 ViewModel。Graph 元数据由内存处理器返回，文件字节仅经过本机回环服务器；不会登录、读取令牌或访问真实云端。

## 启动

在仓库根目录的 Visual Studio Developer PowerShell 中运行：

```powershell
msbuild "OneDrive Simple Management Tool.csproj" /restore /p:Configuration=Debug /p:Platform=x64 /p:CustomAfterMicrosoftCommonTargets="$PWD\Tests\TransferUi\Probe.targets" /p:OutputPath=bin\TransferUi\ /p:IntermediateOutputPath=obj\TransferUi\
& ".\bin\TransferUi\OneDrive Simple Management Tool.exe"
```

入口仅在指定 `Probe.targets` 时编入应用。普通构建仍使用正常应用入口。验收生成文件及逐事件 `audit.jsonl` 位于该可执行文件目录下的 `TransferProbeData/<时间>/`，各次启动相互隔离。

## 操作与断言

- **单个重名**：分别选择覆盖、跳过、保留两份。未选择时“继续”禁用。请求先使用 `fail`，明确选择后才允许 `replace` 或 `rename`；跳过不发送文件切片，任务显示已跳过。
- **零字节重名**：检查空文件直接上传路径也遵循所选策略。
- **批量重名（3 项）**：勾选“应用到本批上传的其余重名项目”，分别验证三种选择；每批只显示一次弹窗，三个任务使用相同策略。新批次必须重新询问。不勾选时逐个询问。
- **弹窗期间关闭**：在重名弹窗出现时按 Alt+F4，退出提示应接管；取消退出后恢复重名询问。
- **启动上传和下载**：上传等待本地服务器响应，下载以限速方式持续进行。关闭窗口时提示 1 个上传、1 个下载；取消退出后窗口保持，下载字节继续增长。
- **停止传输并退出**：上传会话清理故意延迟 4 秒。窗口应等待清理及全部运行任务结束后关闭。审计的 `upload-cleanup-finished` 必须先于 `finish` 和 `window-closed`；后两者必须报告无剩余活动任务。原下载目标与模拟已完成文件必须保留，下载临时文件必须清除。

审计记录选择、批量作用范围、实际请求策略、上传切片数、任务结果、下载字节和关闭顺序。它验证客户端行为，不模拟 Microsoft Graph 服务端自动重命名生成的具体文件名。

## 2026-10-17 验收记录

使用中文 WinUI 窗口、Debug/x64 构建，通过真实鼠标点击、窗口关闭按钮和 Alt+F4 完成以下操作。七个重名场景共处理 15 个模拟文件，无场景异常或未处理异常。

| 操作 | 预期 | 实际 |
| --- | --- | --- |
| 首次打开重名询问 | 没有默认覆盖选择，“继续”禁用 | 通过；取消此上传为默认按钮 |
| 单文件选择覆盖 | 明确选择后发送 `replace`，上传完成 | 通过；请求 `fail → replace`，1 个切片；[截图](Screenshots/replace.png) |
| 零字节文件选择跳过 | 不覆盖，显示已跳过 | 通过；只有 `fail` 请求，0 个切片；[截图](Screenshots/skipped.png) |
| 单文件选择保留两份 | 使用 `rename`，上传完成 | 通过；请求 `fail → rename`，1 个切片；[截图](Screenshots/keep-both.png) |
| 3 项批量覆盖 | 只询问一次，三项覆盖 | 通过；3 个 `fail` 后分别 `replace`，三项 Completed |
| 新批次开始 | 不继承上一批的选择或批量勾选 | 通过；重新询问，未选方式且未勾选 |
| 3 项批量跳过 | 只询问一次，三项跳过 | 通过；仅 3 个 `fail`，0 个切片，三项 Skipped；[截图](Screenshots/batch-skip.png) |
| 3 项批量保留两份 | 只询问一次，三项自动重命名上传 | 通过；3 个 `fail` 后分别 `rename`，三项 Completed；[截图](Screenshots/batch-keep-both.png) |
| 不勾选批量应用，逐项选择跳过、覆盖、保留两份 | 询问三次，各项执行自己的选择 | 通过；结果 Skipped、Completed、Completed，2 个切片 |
| 重名询问期间 Alt+F4 | 退出提示接管，正确统计等待选择的任务 | 通过；提示 3 个上传、0 个下载 |
| 上述退出提示选择取消 | 恢复原文件的重名询问，三个任务保留 | 通过；恢复 `重名文件-7-1.txt` 的询问；[截图](Screenshots/conflict-restored.png) |
| 活动上传和下载时点击窗口关闭 | 明确提示活动任务数量，默认取消退出 | 通过；提示 1 个上传、1 个下载；[截图](Screenshots/active-exit.png) |
| 选择取消退出 | 窗口与两个任务保留，传输继续 | 通过；上传仍 Uploading，下载字节继续从 1,590,272 增至 1,901,568，关闭门禁解除；[截图](Screenshots/exit-cancelled.png) |
| 再次关闭并选择停止传输后退出 | 先停止、清理，再关闭窗口 | 通过；会话清理耗时 4.015 秒，关闭时所有运行任务已结束，无活动上传/下载 |
| 退出后检查本地文件 | 保留原目标和已完成文件，清除下载暂存数据 | 通过；两个文件内容保持，`.cloudflow-*` 残留为 0 |

退出审计时间（本机 UTC+08:00）：清理开始 `20:20:44.9989506`，清理完成 `20:20:49.0134886`，最终保存回调 `20:20:49.0232575`，窗口关闭 `20:20:49.0629470`。完整本地审计保存在 `bin/TransferUi/TransferProbeData/20261017-201612/audit.jsonl`，该路径属于忽略的构建目录。

本轮重跑上传回归 52 项、下载全部检查（包含退出生命周期）、分享回归 10 项、文件管理回归 49 项，均通过。WinUI 验收构建和普通应用 Debug/x64 构建均通过，保留既有 `ShareCommunityViewModel.cs` 的 CS1998 警告。为了使验收调用实际关闭事件处理，将原 `App` 的关闭事件逻辑原样提取到 `TransferWindowCloseGuard`，正式入口和验收入口共用。

本机离线构建使用 .NET SDK `8.0.416`（`MSBuildSDKsPath` 和 `DOTNET_MSBUILD_SDK_RESOLVER_SDKS_DIR` 指向该版本的 `Sdks` 目录）、`WindowsSdkPackageVersion=10.0.22621.57` 和现有 NuGet 缓存。没有进行真实账户或云端上传验收。
