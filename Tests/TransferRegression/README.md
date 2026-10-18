# 传输任务持久化

跨进程恢复后，任务等待用户继续；已完成/已跳过文件不重复执行。第二阶段加入上传会话恢复及下载字节检查点，详见 [ResumeRegression](../ResumeRegression/README.md)。旧的第一阶段记录仍可读取，没有字节检查点的中断文件从头传输。

第三阶段加入每任务限速、优先级和预计剩余时间，行为及验证见 [TransferControlRegression](../TransferControlRegression/README.md)。

## 行为

- 任务记录按任务 ID 分开保存到 `%LOCALAPPDATA%\OneDriveSimpleManagementTool\Transfers`，包含账号、网盘、文件身份、目标路径、上传目录映射、成功和失败结果。
- 使用当前 Windows 用户的 DPAPI 加密，包括上传会话地址。原子快照和追加增量日志在状态/字节检查点刷新到磁盘；界面进度事件不会逐次写盘。只忽略最后一条未写完整的日志，完整密文校验失败会报错并保留。
- 上传、下载、同步上传共用三个文件传输名额。文件夹父任务不持有名额；持久化上传批次使用三个工作任务，不为每个文件创建等待任务。
- 失败列表的“重试此项目”只处理该项目；重试目录时包括该目录中未完成的子项。任务级继续/重试处理剩余项目。已创建的远端目录 ID 和实际名称会保留。
- 退出先停止并等待写入结束，再保存任务；保留下载临时字节和上传会话。取消/移除任务清理所属临时文件，并尝试取消未完成的上传会话；移除记录不删除已完成文件。
- 写入失败显示可重试错误，阻止后续文件开始和正常退出。读取失败保留原记录、阻止新增传输，任务页面提供重试读取入口，应用其余页面仍可使用。

## 自动化验证

```powershell
dotnet run --project Tests/TransferRegression/TransferRegression.csproj
dotnet run --project Tests/ResumeRegression/ResumeRegression.csproj
dotnet run --project Tests/DownloadRegression/DownloadRegression.csproj
dotnet run --project Tests/UploadRegression/UploadRegression.csproj
dotnet run --project Tests/ShareRegression/ShareRegression.csproj
dotnet run --project Tests/FileManagementRegression/FileManagementRegression.csproj
dotnet run --project Tests/FolderSyncRegression/FolderSyncRegression.csproj
dotnet run --project Tests/AccountLifecycleRegression/AccountLifecycleRegression.csproj
dotnet run --project Tests/ReaderRegression/ReaderRegression.csproj
dotnet run --project Tests/PreviewRegression/PreviewRegression.csproj
```

`TransferRegression` 会启动第二个本地进程读取同一份 DPAPI 记录：100 个模拟文件中 90 个成功、10 个失败，重启后先单独重试一个，再补齐其他失败项；断言成功文件不再次上传、目录不重新创建。另覆盖账号隔离、持久化移除、写入失败、日志尾部撕裂、损坏/未来版本记录、排队取消和名额释放。

下载回归覆盖磁盘记录恢复、稳定的同名另存路径、单文件重试、目录内字节续传、损坏临时字节重启、退出保留记录、保存失败阻止退出及读取失败重试。

2026-10-18：上述回归全部通过，主应用 Debug/x64 和 Release/x64 构建通过。主应用仍有原有 `ShareCommunityViewModel` 的 CS1998 警告；控制台测试的 DPAPI 调用有 Windows 平台限定提示。

测试使用模拟存储/Graph 边界或本机 HTTP 服务器，不接触真实 OneDrive。Windows 沙箱若禁止回环连接或临时文件原子替换，需要在允许这些本地操作的环境运行。真实账号登录、远端权限变化和实际网络中断仍需使用可丢弃文件人工验收。

## 界面验证

独立 WinUI 入口、操作步骤及截图见 [TransferPersistenceUi](../TransferPersistenceUi/README.md)。
