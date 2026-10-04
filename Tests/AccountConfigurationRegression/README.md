# 账户与本地配置加固回归

本项目直接编译生产认证、DPAPI 存储、启动协调和安全日志代码。测试只使用虚构身份和独立临时目录，不加载正式账户、令牌或用户配置。需要 Windows 和 .NET 8；DPAPI 验证使用真实 Windows 实现。

```powershell
dotnet run --project Tests/AccountConfigurationRegression/AccountConfigurationRegression.csproj
dotnet run --project Tests/AccountConfigurationRegression/AccountConfigurationRegression.csproj --configuration Release
dotnet run --project Tests/FileManagementRegression/FileManagementRegression.csproj
```

专项检查覆盖账户取消与不匹配、逆序完成、绑定网盘验证、Graph 主机与取消令牌限制、同名身份、重复记录显式修复、真实 DPAPI 往返和篡改检测、版本与容量限制、备份恢复、迁移重入、旧明文占用、加密/写入失败、跨进程版本冲突、同步配置迁移和删除标记、启动顺序、诊断轮转及生产会话期限、MSAL 损坏缓存保留。

`FileManagementRegression/AccountViewModelChecks.cs` 另外直接测试生产 ViewModel：幂等加载、保存失败保留输入、重试不重复登录、禁止重复提交、取消不新增、重复身份定位、已有网盘重认证不改身份，以及损坏配置禁用新增。

## 存储与兼容性

- 业务配置使用当前 Windows 用户的 DPAPI。加密封装和内部 schema 均为版本 1，账户及网盘 ID 按精确二元组比较。
- 网盘清单位于 `%LOCALAPPDATA%/OneDriveSimpleManagementTool/Configuration/drives.dat`，同步配置为同一应用根目录下 `FolderSync/<binding-id>.dat`，各自保留上一份有效 `.bak`。
- 网盘清单上限为 4 MiB / 4096 条；同步配置独立使用 128 MiB / 100 万检查点上限。这些是资源保护上限，不是产品推荐规模。
- `.migrated` 只记录格式状态；旧明文仍存在时会继续提示清理。`.removed` 防止解除同步绑定后因中断残留文件而重新启动任务。
- 显式恢复/修复会生成 `.recovery-<随机号>` 加密归档。归档保护用途为原用途加 `/recovery`，保留完整原始字节，不能当作普通主文件直接替换。
- 迁移前应关闭所有旧版本。启动时同名旧进程检查及用户目录锁避免并行运行；存储服务另有跨进程锁和版本检查。
- 令牌仍由 MSAL 管理。迁移先验证加密临时副本，检测 MSAL Extensions 的错误信号但不记录其文本，避免损坏缓存被当作空账户列表接受。
- 新格式不能交给旧程序读取。回退应使用兼容新格式的修复版本或同一用户环境下的加密备份，不自动生成明文副本。

## 2026-10-04 验证记录

环境：Windows x64、.NET SDK 8.0.416 / 9.0.318、Visual Studio 2022 MSBuild，现有项目依赖版本保持不变。

| 检查 | 实际结果 |
| --- | --- |
| AccountConfigurationRegression | Debug / Release 各 46 项通过 |
| FileManagementRegression | 41 项通过，其中新增账户 ViewModel 检查 6 项 |
| ShareRegression | 10 项通过 |
| UploadRegression | 37 项通过 |
| FolderSyncRegression | 20 项通过 |
| PreviewRegression | 23 项通过 |
| DownloadRegression | 全部通过 |
| Debug/x64 与 Release/x64 | 构建通过，仅有原有 `ShareCommunityViewModel` 的 CS1998 警告 |
| 独立 WinUI 构建 | 已验证同名第二项的双击、回车及菜单导航；保存失败保留输入和对话框；成功保存后关闭对话框 |

代码测试不替代真实交互认证。发布前仍需用可丢弃账户人工检查：A/B 登录与选错账户、浏览器登录取消、过期令牌续期、真实旧缓存迁移、另一 Windows 用户无法解密、实际断电/磁盘写满和完整启动错误界面。尚未执行这些人工项目，不应据此声明全部发布验收完成。

模拟界面入口与复现步骤见 `../AccountConfigurationUi/README.md`。

## 启动配置编码回归（2026-10-04）

仓库附带的 `appsettings.json` 使用带 BOM 的 UTF-8。之前的启动校验直接向 `JsonDocument` 传递原始字节，遇到开头的 `EF BB BF` 时抛出异常，界面因此提示设置缺失或无效，即使配置文件已经复制到程序目录。新增直接读取随应用配置文件的检查后，在修复前稳定复现了该错误。

启动现在通过应用原有的 JSON 配置提供程序读取有大小限制的内存流，并将验证后的同一份配置交给应用使用。新增四项检查覆盖随应用配置、带或不带 BOM 的 UTF-8、注释/尾逗号/键名大小写、缺失或无效配置、读取上限及取消；原有路径检查补充了文件替换后配置快照保持一致的断言。测试不会修改正式账户或配置文件。
