# 上传取消验证

上传取消现在先停止传输并等待全部子任务退出，再移除记录。取消期间显示“正在取消”，禁用重复操作；已完成、失败的任务只移除记录。已上传的文件和已创建的目录保留，不回滚同名文件替换。

取消信号覆盖目录扫描、文件属性、创建目录、创建上传会话、零字节文件和分片上传。文件流打开没有取消重载，因此等待打开结束后立即释放，不在后台遗留操作。已获得地址的未完成上传会话使用独立的 5 秒令牌尝试清理；清理失败不覆盖取消结果。若服务端已提交但响应因断线丢失，取消不保证云端文件不存在。

## 控制台回归

```powershell
dotnet run --project Tests/UploadRegression/UploadRegression.csproj
dotnet run --project Tests/ShareRegression/ShareRegression.csproj
dotnet run --project Tests/FileManagementRegression/FileManagementRegression.csproj
dotnet run --project Tests/PreviewRegression/PreviewRegression.csproj
dotnet run --project Tests/DownloadRegression/DownloadRegression.csproj
```

上传回归编译生产服务和 ViewModel，使用模拟存储边界、UI 调度队列以及接收真实 SDK HTTP 请求的回环服务器。新增测试以可控等待点验证取消时序，不依赖碰巧命中的延时。

覆盖开始前取消、属性读取和递归扫描取消、等待文件打开、元数据请求取消、分片中断、并发后代任务退出、独立任务隔离、停止调度、重复命令、迟到进度、会话清理拒绝/超时、完成与取消竞争，以及文件失败后仍等待其他文件退出。

## WinUI 测试入口

此入口仅在显式启用时参与编译。使用生产任务页、任务管理器、ViewModel、WinRT 存储和 Graph SDK；元数据在内存中返回，上传和清理仅发送至 `127.0.0.1`，无需账户或真实网盘。

```powershell
msbuild "OneDrive Simple Management Tool.csproj" /restore /p:Configuration=Debug /p:Platform=x64 /p:CustomAfterMicrosoftCommonTargets="$PWD\Tests\UploadUi\Probe.targets" /p:OutputPath=bin\UploadUi\ /p:IntermediateOutputPath=obj\UploadUi\
& ".\bin\UploadUi\OneDrive Simple Management Tool.exe"
```

窗口标题包含 `LOCAL`。启动后在构建输出的 `UploadProbeData` 目录生成一个 16 MiB 文件和一个嵌套目录，并自动创建两个上传任务。服务器暂停分片响应以方便点击取消，清理响应延迟 4 秒以观察“正在取消”。顶部显示剩余任务和收到的会话清理请求数。

默认使用中文。关闭测试窗口后，可切换为英文再次启动：

```powershell
Set-Content -LiteralPath bin/UploadUi/UploadProbeLanguage.txt -Value en-US -Encoding utf8
& ".\bin\UploadUi\OneDrive Simple Management Tool.exe"
```

将文件内容改为 `zh-CN` 可切回中文。语言覆盖只作用于这个未打包的测试进程，不持久修改正式应用设置。

## 2026-10-04 验证记录

| 操作 | 预期 | 实际 |
| --- | --- | --- |
| 打开上传页 | 不显示尚未实现的暂停、恢复入口 | 中英文均通过 |
| 点击大文件取消按钮 | 立即显示正在取消，按钮禁用；清理后移除 | 中英文均通过 |
| 大文件取消期间观察另一个任务 | 目录任务保留，可独立操作 | 中英文均通过 |
| 中文界面右键目录 → 取消上传 / 移除任务 | 执行同一取消命令，等待清理再移除 | 通过 |
| 英文界面点击目录取消按钮 | 英文提示完整显示，任务最终移除 | 通过 |
| 两项取消完成 | 剩余任务为 0，收到 2 次会话清理请求，无上传失败提示 | 中英文均通过 |

截图：[中文取消中](Screenshots/cancelling-zh.png)、[英文取消中](Screenshots/cancelling-en.png)、[取消后列表](Screenshots/removed-zh.png)。

自动化结果：上传 37 项（原有 18 项、新增 19 项）、分享 10 项、文件管理 35 项、预览 23 项及下载回归全部通过。Debug/x64、Release/x64 和独立 WinUI 测试构建通过；仅有原有 `ShareCommunityViewModel` CS1998 警告。

本机构建使用已安装的 NuGet 缓存还原（`/p:RestoreSources=C:/Users/asus/.nuget/packages /p:NuGetAudit=false`），未修改仓库 NuGet 源。以上为模拟网络及本地 WinUI 验证，尚未使用真实 OneDrive 账户验证云端最终状态。

2026-10-04：用户确认本轮验收通过，并授权提交、推送远端分支。
