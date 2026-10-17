# 批量下载界面验证

此可选入口运行生产 `DrivePage`、多选/右键操作、文件夹选择器、Graph 元数据适配器和任务页。Graph 返回内存数据，文件字节来自本机 HTTP 服务，不使用真实账户。预置“演示资料/子目录/内容.bin”、空文件夹、空文件以及会失败一次的 `Report.txt`。

```powershell
msbuild "OneDrive Simple Management Tool.csproj" /restore /p:Configuration=Debug /p:Platform=x64 /p:CustomAfterMicrosoftCommonTargets="$PWD\Tests\BatchDownloadUi\Probe.targets" /p:OutputPath=bin\BatchDownloadUi\ /p:IntermediateOutputPath=obj\BatchDownloadUi\
& ".\bin\BatchDownloadUi\OneDrive Simple Management Tool.exe"
```

窗口标题为 `Batch download UI regression — LOCAL`。顶部显示本次可丢弃的输出目录 `bin/BatchDownloadUi/LocalDownloads/<随机标识>`。

- `Files` / `Tasks` 在两个生产页面间切换。
- 从生产“下载选中项”按钮或右键菜单可打开保存目录选择器，选择顶部显示的测试目录。
- `Queue selected locally` 将当前选中项直接加入生产任务管理器，使用顶部测试目录，便于选择器无法被自动化工具定位时检查后续界面。
- `Queue paused folder` 创建整个“演示资料”的下载并暂停，便于验证继续、取消和同名目录自动另存。
- 选中根目录两项下载，`Report.txt` 会在第一次传输获取元数据时返回权限失败；重试应补完该文件，不重新下载其他成功内容。

## 2026-10-17 验证记录

已确认列表可多选，下载按钮启用，生产文件夹选择器正常弹出，取消选择器后返回文件页。自动化工具无法为独立的 Windows `PickerHost.exe` 返回可操作窗口，因此尚未自动完成目录选择的确认步骤。任务页后续检查使用 `Queue selected locally` 和 `Queue paused folder`。

| 操作 | 预期 | 实际 |
| --- | --- | --- |
| 下载文件夹与单文件的混合批次 | 显示字节、速度、完成数量和失败数量 | 通过 |
| 传输中暂停并展开失败明细 | 暂停后进度停止，显示 Report.txt 及中文权限错误 | 通过，见 [paused.png](Screenshots/paused.png) |
| 继续暂停批次 | 使用已有进度，补完未完成项，最终显示 3/3 | 通过 |
| 另建相同文件夹批次并取消 | 自动另存为“演示资料 (2)”，保留已完成空文件，清理未完成文件的临时数据，不影响另一批次 | 通过，见 [completed-cancelled.png](Screenshots/completed-cancelled.png) |
| 检查本地目录 | 保留“子目录/内容.bin”和空文件夹，完成文件大小为 8 MiB；两个批次结束后无临时文件 | 通过 |
| 独立批次失败后点击重试 | 显示 2/3 和 Report.txt 权限错误，重试后显示 3/3，已完成大文件不重写 | 通过；重试前后大文件修改时间一致，见 [failed.png](Screenshots/failed.png) 与 [retried.png](Screenshots/retried.png) |

上述记录为本地模拟验证，当时未进行真实账户验收。随后用户于 2026-10-17 使用最新 Release/x64 正式构建测试真实 OneDrive 账号，确认三批文件管理功能通过并授权提交、推送。账号类型、数据规模及断网等异常场景覆盖未逐项记录。自动化目录、分页、失败重试、取消清理检查见 [DownloadRegression](../DownloadRegression/README.md)，三批最终验收结论见 [FileManagementUi](../FileManagementUi/README.md#2026-10-17-三批文件管理功能验收)。
