# 账户退出与网盘移除回归

使用生产认证适配器、账户访问状态、操作协调服务、网盘/书签/同步存储、同步任务和阅读缓存；微软账户、HTTP 和同步远端使用虚构边界。配置使用真实 Windows DPAPI，所有文件写入随机隔离目录。不访问实际 OneDrive，不读取用户登录缓存。

```powershell
dotnet run --project Tests/AccountLifecycleRegression/AccountLifecycleRegression.csproj
dotnet run --project Tests/AccountConfigurationRegression/AccountConfigurationRegression.csproj
dotnet run --project Tests/FileManagementRegression/FileManagementRegression.csproj
dotnet run --project Tests/DownloadRegression/DownloadRegression.csproj
```

其他关联检查：ShareRegression、UploadRegression、FolderSyncRegression、BookmarkRegression、ReaderRegression、HomeRegression、SettingsRegression、PreviewRegression。上传、下载及部分阅读/同步用例需要本机回环 HTTP。受限环境可把当前进程的 `TEMP`、`TMP` 指向仓库内的 `TestResults` 子目录。

## 行为约定

| 操作 | 退出此账户 | 移除网盘 |
| --- | --- | --- |
| 范围 | 相同 HomeAccountId 的全部网盘 | 精确 HomeAccountId + DriveId |
| 配置 | 保留网盘和同步绑定 | 删除所选网盘及其同步绑定 |
| 同步 | 持久化暂停，登录后手动恢复 | 停止后解除绑定 |
| 书签 | 保留，显示需要登录 | 默认保留并显示不可用，可明确选择删除 |
| 凭据 | 只移除此账户的 MSAL 缓存账户 | 仅最后一个网盘移除时清除该账户凭据 |
| 内容缓存 | 清除此账户的阅读缓存和列表状态 | 仅清除此网盘的阅读缓存和列表状态 |

云端内容、本地同步目录、已完成的下载文件及阅读进度均保留。停止传输时仅删除传输自身创建的临时文件。已完成和失败的传输卡片及错误信息保留，但已结束上传的续传会话会被删除并清出传输记录，因为会话地址无需账户即可写入；重新登录后重试将从头上传。已提交的云端操作不能撤回；退出只针对本应用，不清理浏览器或其他应用的会话。

PDF 预览改用私密 WebView2 会话，与 EPUB 阅读器一致，避免新预览把免登录下载链接和内容保存在所有账户共用的持久缓存中。旧版本留下的共享 WebView2 数据没有账户归属索引，不能安全按账户删除，因此不会通过清空整个浏览器目录影响其他账户。

## 失败和恢复

`account-access.dat` 是加密的访问禁用记录及待清理操作日志。先持久化禁用范围，再取消并等待相关请求/任务，清除凭据，处理同步、书签和缓存，最后提交网盘移除与完成状态。

失败保留禁用状态和操作记录；页面提供重试，启动时重试未完成操作。操作按范围执行且可重复。清理未完成时不允许重新登录该账户；同账户的其他网盘在单网盘移除期间仍可静默访问。缓存索引损坏时不猜测文件归属，不做全量清理。占用文件保留在索引中供重试。配置保存失败不会报告移除成功。

已退出账户的静默获取令牌被阻止；只有新的显式登录可以恢复。退出前启动的登录结果不能恢复访问。网盘移除记录只有显式重新添加成功后才清除。重新登录不会自动恢复同步。

`account-access.dat` 无法读取时启动停在修复入口，所有账户保持禁用。可检查备份（只读）、从备份恢复或重建，均需确认；记录可读时拒绝修复，避免覆盖其他进程的修复结果。损坏原件及未采用的备份加密保留为 `.recovery-*` 副本；超过读取上限的文件不读取内容，原样改名保留，不阻止修复。外层格式正确但载荷为空同样视为损坏。恢复仅保留备份中的网盘级记录；MSAL 缓存、网盘配置、同步绑定及备份中出现的每个账户都写入待完成的整账户退出，启动时随即清除凭据、持久暂停同步并清理缓存。之后每个账户都需显式重新登录，同步需手动恢复。网盘配置无法读取时，其账户凭据由 MSAL 列表覆盖；同步配置无法读取时，同步清理失败使退出保持待完成，账户继续禁用。

## 验证范围

新增套件覆盖退出重启恢复、账户/网盘隔离、最后网盘凭据处理、书签可选删除、请求取消与等待、迟到登录、待清理重启重试、各阶段失败、缓存占用/损坏、重新添加、MSAL 精确账户移除，以及访问记录损坏（含空载荷、超限文件）后的重建、备份恢复与拒绝覆盖可读记录。

DownloadRegression 补充真实回环下载期间按网盘/账户停止、其他任务保留及本地文件保留；FileManagementRegression 补充列表、选择、容量状态失效与其他账户隔离。

原生界面入口和复现步骤见 [AccountConfigurationUi](../AccountConfigurationUi/README.md)。测试使用虚构认证，真实微软账户交互登录、组织策略和浏览器会话仍需使用一次性测试账户验收。

## 2026-10-17 验证结果

| 回归套件 | 结果 |
| --- | --- |
| AccountLifecycleRegression | 17 项通过 |
| AccountConfigurationRegression | 46 项通过 |
| ShareRegression | 10 项通过 |
| UploadRegression | 52 项通过 |
| FileManagementRegression | 87 项通过 |
| DownloadRegression | 全部通过（40 条 PASS） |
| FolderSyncRegression | 20 项通过 |
| BookmarkRegression | 20 项通过 |
| ReaderRegression | 29 项通过 |
| HomeRegression | 22 项通过 |
| SettingsRegression | 12 项通过 |
| PreviewRegression | 23 项通过 |

Windows x64 Debug、Release 均完成还原和构建，无错误。仅有现存 `ShareCommunityViewModel.cs:17` 的 CS1998 警告。控制台及构建日志保存于本地忽略目录 `TestResults/`；账户原生界面的具体预期、实际结果和未覆盖项记录在上述 UI 验收文档。

PDF 私密会话调整后的原生预览回归也全部通过，包括 20 轮图片、PDF、音频重复打开与关闭，详见 [PreviewUi](../PreviewUi/README.md)。
