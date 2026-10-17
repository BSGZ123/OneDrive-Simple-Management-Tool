# 同盘移动与复制验证

列表和网格共用右键菜单中的“移动…”、“复制…”；多选后也可使用页面的“整理”按钮。目标选择器从当前网盘的真实根目录开始，逐层打开文件夹并支持返回上一级。选中的文件夹不能作为目标，也不能移动或复制进自身子目录。搜索结果同时包含父子项时，只处理最上层的选中目录。

同名策略针对整个批次，默认“保留两者”，自动生成 `名称 (2).扩展名`；也可选择“跳过”。同名文件夹不合并，不覆盖或删除既有目标。策略、目标和选中项在提交后固定。成功项不会因后续失败或刷新失败而重复执行，逐项显示完成、跳过、失败及待确认状态。

复制使用 Graph 的异步任务。HTTP 202 仅代表请求已接受，必须查询到完成并重新核对目标项目后才报告成功。状态查询失败或本轮等待超时保留原查询地址，重试继续查询，不重新 POST。关闭弹窗后，从该网盘的整理菜单进入会恢复待确认批次；查询地址失效时可以明确结束跟踪，再检查目标位置。记录仅保留在当前应用会话中，重启后应先检查目标，避免重复复制。

“停止等待”阻止后续项目并取消目录扫描/状态查询；已经提交的云端复制可能继续。发送中的移动/复制请求最多等待 30 秒获取确认，避免因立即取消而丢失复制回执。结果不明确且没有查询地址的复制不会允许直接重试。应用窗口在当前弹窗执行期间不会直接关闭。

协议依据：[移动项目](https://learn.microsoft.com/en-us/graph/api/driveitem-move?view=graph-rest-1.0)、[复制项目](https://learn.microsoft.com/en-us/graph/api/driveitem-copy?view=graph-rest-1.0)、[异步任务](https://learn.microsoft.com/en-us/graph/long-running-actions-overview)。移动携带源 eTag；复制使用服务默认的冲突失败行为，客户端重新列目录后决定改名或跳过，兼容个人 OneDrive 不支持 conflictBehavior 查询参数的限制。复制保留目录内容，使用最新版本，不承诺保留版本历史或元数据。批次不提供事务回滚，已完成项目保留；服务器复制失败时也可能留下部分结果，重试前应核对目标。

## 自动回归

`Tests/FileManagementRegression/FileTransferChecks.cs` 编译生产 ViewModel、Graph 服务和领域类型，模拟 HTTP 与平台边界。22 项新增检查覆盖：

- 目标浏览失败回退、空目录、选择快照、混合文件/目录、同一父目录的移动与复制。
- 大小写同名、扩展名、最长名称、文件夹改名、跳过、冲突竞态及重试上限。
- 父子重复选择、部分失败、成功后刷新失败、重复提交、账号切换、无效目标和层级循环。
- 复制进度、停止后保留回执、查询失败、完成结果不匹配、丢失回执、移动结果核对和结束跟踪。
- 固定 drive/item/root、If-Match、分页边界、空中间页、监控 URL 和响应格式、匿名查询和双语资源。

运行：

```powershell
dotnet run --project Tests/FileManagementRegression/FileManagementRegression.csproj
dotnet run --project Tests/ShareRegression/ShareRegression.csproj
dotnet run --project Tests/UploadRegression/UploadRegression.csproj
dotnet run --project Tests/DownloadRegression/DownloadRegression.csproj
```

2026-10-17：文件管理 86 项、分享 10 项、上传 52 项、下载 39 项通过，共 187 项。测试使用模拟服务和本机回环网络，未登录真实 OneDrive 账号。

最终 Debug/x64、Release/x64 和本地 UI 入口构建通过。仅有既有的 `ShareCommunityViewModel` CS1998 警告；`git diff --check` 通过。依赖来自本机 NuGet 缓存。

## 原生界面测试入口

```powershell
msbuild "OneDrive Simple Management Tool.csproj" /restore /p:Configuration=Debug /p:Platform=x64 /p:CustomAfterMicrosoftCommonTargets="$PWD\Tests\FileTransferUi\Probe.targets" /p:OutputPath=bin\FileTransferUi\ /p:IntermediateOutputPath=obj\FileTransferUi\
& ".\bin\FileTransferUi\OneDrive Simple Management Tool.exe"
```

这是显式启用的入口，常规构建不包含测试代码。全部网盘请求都经过内存 HTTP handler，不登录、不访问网络、不接触真实文件。生产页面、弹窗、ViewModel 和 Graph 适配器保持原样；顶部复制测试按钮仅向适配器注入模拟异步监控 HTTP 客户端。移动可直接从生产“整理”菜单操作。

“Select sources”选中 `Report.txt` 和 `资料` 文件夹；“归档”已有同名 Report.txt，且包含“2026”子目录。“Reject Report once”使 Report 的下一次修改请求返回一次权限错误。“Copy / resume (local monitor)”打开或恢复复制弹窗。重启入口恢复初始内存数据。

## 2026-10-17 原生界面验证记录

| 操作 | 预期 | 实际 |
| --- | --- | --- |
| 选中两个源项目，打开复制并进入“归档” | 路径变为 /归档，显示子目录；冲突选项为保留两者、跳过 | 通过 |
| 默认保留两者，启动复制 | 收起目录选择区，显示逐项状态和百分比 | 通过，观察到 25%、50%、75% |
| 文件夹完成附近点击停止等待 | 保留完成项，后续 Report 保持待处理 | 通过 |
| 继续处理，触发一次权限失败 | 文件夹保持成功，Report 显示失败和中文原因 | 通过 |
| 重试 Report，复制期间停止等待 | 显示等待确认和结束跟踪按钮，可关闭弹窗 | 通过 |
| 关闭后重新打开复制测试入口并查询 | 恢复原批次，确认完成，不再生成另一个副本 | 通过，完成 2 项，提交按钮禁用 |
| 使用生产“整理 → 移动…”批量移动到归档 | 同名自动改名，两个源项目从根列表移除 | 通过，资料 (2)、Report (3).txt；根目录只剩归档 |
| 打开目标目录 | 原件、复制结果和移动结果各保留一份 | 通过，显示 2026、资料、资料 (2)、Report.txt、Report (2).txt、Report (3).txt 共 6 项 |

截图：[目标和策略](Screenshots/target-picker.png)、[复制进度](Screenshots/copy-progress.png)、[部分失败](Screenshots/copy-partial.png)、[停止后待确认](Screenshots/copy-waiting.png)、[移动完成](Screenshots/move-complete.png)、[目标目录结果](Screenshots/destination-results.png)。

用户于 2026-10-17 使用最新 Release/x64 正式构建测试真实 OneDrive 账号，确认三批文件管理功能通过并授权提交、推送；具体账号类型、数据规模和异常场景覆盖未逐项记录。最终验收结论见 [FileManagementUi](../FileManagementUi/README.md#2026-10-17-三批文件管理功能验收)。

后续专项验收建议使用可丢弃文件，在个人 OneDrive 和企业网盘分别核对同盘跨目录移动、含空子目录的复制、两种同名策略、权限失败及断网后查询恢复。跨盘移动、跨盘复制、覆盖替换及重启恢复不在本批范围。
