# 跨重启字节续传（第二阶段）

任务中心的普通文件/文件夹上传和下载支持字节续传，沿用第一阶段的手动继续、逐项失败重试和全局三个传输名额。旧记录没有字节检查点时仍从文件开头传输。同步上传保留原有同步流程和共享并发限制。

## 上传

- 通过 Graph 创建上传会话，使用 10 × 320 KiB 的顺序分片。会话 URL、到期时间、已确认字节数及源文件 SHA-256 随任务一起由 DPAPI 加密保存，日志不记录 URL。
- 恢复时锁定本地源文件并重新计算 SHA-256，然后匿名查询会话的 `nextExpectedRanges`。以服务端实际接收情况为准，本地记录落后也不会重发已确认分片。
- 源文件内容改变或会话返回 404/410 时，重新创建会话。有效会话在退出时保留；显式取消/移除时尝试删除，无法连接的会话交由服务端过期清理。
- 最终分片发送前记录“提交结果待确认”。若响应丢失且会话随后不可用，显示需要核对云端文件的错误，避免盲目重传产生重复内容。用户核对后可移除记录并重新上传。
- 已收到最终成功结果但本地保存失败时，保留内存中的成功结果；重试保存或继续任务不会重复上传该文件。

## 下载

- 保存稳定的临时文件身份、远端版本、大小、已刷新到磁盘的字节数及本地前缀 SHA-256。每约 1 秒或 4 MiB，以及暂停/退出/失败边界执行检查点；先刷新文件，再提交加密记录。
- 恢复时刷新 Graph 下载地址，验证远端版本和本地前缀，截去检查点之后未经确认的尾部，再请求 `Range`。下载地址不落盘。
- 源文件变化、本地前缀缺失/损坏、服务器不接受范围请求时安全重启该文件。严格校验 `Content-Range`、内容长度及可用的远端哈希，不拼接不同版本的数据。
- 下载完成使用原有目标写入策略。若进程在文件移动成功后、完成状态落盘前退出，重启会核对目标文件的完整哈希，确认后直接完成。

## 回归

```powershell
dotnet run --project Tests/ResumeRegression/ResumeRegression.csproj
dotnet run --project Tests/DownloadRegression/DownloadRegression.csproj
dotnet run --project Tests/UploadRegression/UploadRegression.csproj
dotnet run --project Tests/TransferRegression/TransferRegression.csproj
```

`ResumeRegression` 使用本机 HTTP 服务器和两个独立 OS 进程。第一个进程在检查点直接退出，第二个进程只依赖加密记录和磁盘文件恢复。断言请求偏移和最终文件字节，覆盖：

- 下载正常恢复、未确认尾部、前缀损坏、远端版本变化、不支持 Range、恢复后取消、完成移动后的崩溃。
- 上传服务端领先于本地记录、会话失效、同大小源文件变化、416 后状态查询、最终结果未知的重复上传防护。
- 会话 URL 在任务存储中不可作为明文读取，分片和状态查询不发送 bearer token。

上传回归还经过生产 OneDrive Graph 适配器验证创建会话与中断后复用；下载回归验证目录任务中每个文件的检查点映射。其他仓库回归用于确认分享、文件管理、阅读器和账号清理的兼容性。

2026-10-18：上述四套回归，以及 Share、FileManagement、Reader、AccountLifecycle 回归通过；主应用 Debug/x64、Release/x64 构建通过，仅有原有的 CS1998 警告。WinUI 恢复、继续与退出的操作记录及截图见 [界面验收](../TransferPersistenceUi/README.md)。

协议依据：[Graph 上传会话](https://learn.microsoft.com/en-us/graph/api/driveitem-createuploadsession?view=graph-rest-1.0)、[Graph 范围下载](https://learn.microsoft.com/en-us/graph/api/driveitem-get-content?view=graph-rest-1.0)。本目录的回归测试不接触真实账户。

2026-10-18 使用真实 OneDrive 和 256 MiB 测试文件完成上传/下载断网、强制退出续传及完整 SHA-256 校验；同时修复 HTTPS 下载网络异常被误报为磁盘错误的问题。步骤、检查点和覆盖边界见 [实际环境验收](../LiveTransferAcceptance/ACCEPTANCE-2026-10-18.md)。账号权限变化等场景仍需单独验收。
