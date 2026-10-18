# 传输持久化界面验收

此独立入口加载生产 `TaskManagerPage`、传输 ViewModel、加密记录存储及退出保护。Graph 元数据由内存返回，文件字节只传到本机回环服务器。无需登录，不访问真实云端。

```powershell
msbuild "OneDrive Simple Management Tool.csproj" /restore /p:Configuration=Debug /p:Platform=x64 /p:CustomAfterMicrosoftCommonTargets="$PWD\Tests\TransferPersistenceUi\Probe.targets" /p:OutputPath=bin\TransferResumeUi\ /p:IntermediateOutputPath=obj\TransferResumeUi\
& ".\bin\TransferResumeUi\OneDrive Simple Management Tool.exe"
```

首次运行自动建立上传和下载示例，每个示例有一个可正常传输的文件和一个权限失败文件。下载文件各为 8 MiB，便于在完成前暂停。点击“解除模拟故障”后，可以继续或在失败明细中单项重试。数据只保存在此构建输出目录的 `TransferResumeProbeData` 中；关闭并重新运行会读取这些记录。

## 2026-10-18 上传暂停与批量控制验证

使用独立输出 `bin\TransferBatchUi\` 和中间目录 `obj\TransferBatchUi\`，其余构建参数与下方第三阶段一致。先不建立标记文件，检查带模拟故障的上传/下载；保存退出后建立 `controls-mode` 和 `english-mode`，检查限速上传、英文按钮和暂停恢复。两种模式使用不同的测试数据目录。

| 操作 | 预期 | 实际 |
| --- | --- | --- |
| 中文下载页点击“全部暂停” | 写入停止、保留进度、启用全部继续 | 通过，保留 4.1 MB / 16 MB，速度与 ETA 显示“—” |
| 上传含成功文件及失败文件 | 仅重试失败可用，继续及清理已完成禁用 | 通过，1/2 完成、1 个失败 |
| 解除模拟故障，点击上传“重试失败” | 成功项目保留，失败项目完成 | 通过，2/2 完成、0 个失败，清理已完成启用 |
| 英文上传页点击 Continue all | 启动恢复任务，显示速度及 ETA | 通过，约 248–252 KiB/s |
| 单任务 Pause upload，随后 Continue all | 暂停停止网络工作，继续恢复传输 | 通过，暂停时清空速度/ETA，继续后恢复进度 |
| 上传 Pause all | 暂停任务，启用 Continue all | 通过 |
| 保存退出，重新运行后切换上传页 | 显式暂停状态保留，不自动上传 | 通过，仍显示 Upload paused，Continue upload/Continue all 可用 |

截图：[下载全部暂停](Screenshots/Phase4/download-paused-zh.png)、[上传失败](Screenshots/Phase4/upload-failed-zh.png)、[批量重试完成](Screenshots/Phase4/upload-retried-zh.png)、[上传全部暂停](Screenshots/Phase4/upload-paused-en.png)、[重启保留暂停状态](Screenshots/Phase4/upload-restored-en.png)。清理操作的数据安全、失败保留卡片、方向隔离及多任务筛选由 `DownloadRegression/BatchControlChecks.cs` 验证；上传真实分片暂停与继续由 `UploadRegression/PauseChecks.cs` 验证。

本次 UploadRegression 60 项、ShareRegression 10 项、FileManagementRegression 87 项、HomeRegression 22 项、完整 DownloadRegression、ResumeRegression 的 11 组检查及主应用 Debug/Release x64 构建通过。保留原有 CS1998 警告；DPAPI 控制台项目有 Windows 平台提示。所有网络测试均使用本机模拟服务器，没有验证真实 OneDrive 账号。

## 2026-10-18 第三阶段验证

用独立输出目录构建，建立 `controls-mode` 标记后启动，可进入限速和优先级验收模式。首次建立的任务等待手动继续，默认限速 256 KiB/s；下载总量 16 MiB，上传总量 8 MiB，均没有预设失败项。设置和任务数据保存在输出目录下的 `TransferControlProbeData`。

```powershell
msbuild "OneDrive Simple Management Tool.csproj" /restore /p:Configuration=Debug /p:Platform=x64 /p:CustomAfterMicrosoftCommonTargets="$PWD\Tests\TransferPersistenceUi\Probe.targets" /p:OutputPath=bin\TransferControlsUi\ /p:IntermediateOutputPath=obj\TransferControlsUi\
New-Item -ItemType File -Path bin\TransferControlsUi\controls-mode -Force
& ".\bin\TransferControlsUi\OneDrive Simple Management Tool.exe"
```

关闭后在同一输出目录建立 `english-mode` 文件，再运行可检查英文资源及同一份记录的恢复。测试入口的顶栏说明和文件名刻意保留中文，产品控件使用对应语言资源。

| 操作 | 预期 | 实际 |
| --- | --- | --- |
| 下载设为高优先级、128 KiB/s，继续 | 两个文件共用限速，并显示速度、ETA | 通过，约 116.5 KiB/s，ETA 约 2 分钟 |
| 下载中改为 256 KiB/s 并按 Enter | 上限立即变化，近期速度与 ETA 随之更新 | 通过，约 252.1 KiB/s，ETA 约 30 秒 |
| 暂停下载 | 保留字节，清空速度与 ETA | 通过，保留 10.1 MiB / 16 MiB，显示“—” |
| 上传设为低优先级、64 KiB/s，继续 | 分片未完成时也显示进度、速度和 ETA | 通过，进度 11%，约 58.3 KiB/s，ETA 约 2 分钟 |
| 保存退出，英文界面重新启动 | 上传和下载设置分别保留，等待用户继续 | 通过：下载 High / 256，上传 Low / 64 |

截图：[下载中](Screenshots/Phase3/download-running-zh.png)、[实时调速](Screenshots/Phase3/download-limit-changed-zh.png)、[暂停](Screenshots/Phase3/download-paused-zh.png)、[上传中](Screenshots/Phase3/upload-running-zh.png)、[下载设置恢复](Screenshots/Phase3/download-restored-en.png)、[上传设置恢复](Screenshots/Phase3/upload-restored-en.png)。优先级调度次序、设置日志恢复、低速传输超时和限速预算由 [TransferControlRegression](../TransferControlRegression/README.md) 验证。

上传的实时进度包括正在发送的分片；恢复记录只显示服务端已确认的字节。此界面模拟器的上传服务器在退出后重新建立，完整跨进程上传续传由 `ResumeRegression` 验证。

## 2026-10-18 第二阶段验证

| 操作 | 预期 | 实际 |
| --- | --- | --- |
| 下载中暂停 | 保留已写入的字节进度 | 通过，3.8 MiB / 16 MiB |
| 关闭并保存任务 | 对话框说明保留进度，正常退出 | 通过 |
| 重新启动 | 仍显示已保存字节，等待用户继续 | 通过，仍为 3.8 MiB / 16 MiB |
| 解除模拟错误并继续 | 从已保存位置继续，最后全部完成 | 通过，16 MiB / 16 MiB、2/2 文件 |
| 切换上传页并继续 | 显示会话续传说明，保留成功文件，完成剩余失败项 | 通过，1/2 → 2/2 |

截图：[暂停](Screenshots/Phase2/paused.png)、[保存退出](Screenshots/Phase2/save-exit.png)、[重启恢复](Screenshots/Phase2/restored.png)、[下载完成](Screenshots/Phase2/completed.png)、[上传恢复](Screenshots/Phase2/upload-restored.png)、[上传完成](Screenshots/Phase2/upload-completed.png)。实际 Range 偏移、上传服务端领先于本地记录及会话失效由 [ResumeRegression](../ResumeRegression/README.md) 的跨进程测试断言。

## 2026-10-18 第一阶段历史验证

| 操作 | 预期 | 实际 |
| --- | --- | --- |
| 重新运行已有任务的程序 | 显示中断状态和整文件重新传输说明 | 通过 |
| 继续下载，保留一个模拟权限错误 | 已完成 1/2，失败列表含文件名和错误 | 通过 |
| 解除故障，点击下载明细“重试此项目” | 完成剩余文件，计数变为 2/2 | 通过 |
| 上传仍有失败文件时关闭窗口 | 提示保存任务并退出，确认后退出 | 通过 |
| 重新启动并切换上传页 | 显示已中断，保留 1/2 完成计数与失败文件明细，等待手动继续 | 通过 |
| 解除故障，点击上传明细“重试此项目” | 完成剩余文件，计数变为 2/2，失败数为 0 | 通过 |

截图：[下载单项重试](Screenshots/download-retry.png)、[下载完成](Screenshots/download-completed.png)、[保存并退出](Screenshots/save-exit.png)、[上传恢复](Screenshots/upload-restored.png)、[上传完成](Screenshots/upload-completed.png)。

测试入口与生产构建分离；常规构建不包含 `Probe.cs`。单独的控制台回归验证已完成文件不被再次请求及真正的跨进程持久化。界面中的模拟服务不代表真实 OneDrive 网络和账号验收。
