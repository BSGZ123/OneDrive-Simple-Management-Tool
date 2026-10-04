# 书签业务回归

在 Windows 上从仓库根目录运行：

```powershell
dotnet run --project Tests/BookmarkRegression/BookmarkRegression.csproj
dotnet run --project Tests/FileManagementRegression/FileManagementRegression.csproj
```

书签回归直接编译生产存储、解析器、ViewModel、账户认证提供器和配置保护代码。Graph SDK 使用内存 HTTP 响应，账户令牌由替身提供；数据位于独立临时目录，不访问正式配置或真实 OneDrive。Windows DPAPI 和目标文件占用检查使用实际系统能力，需要在 Windows 上运行。

2026-10-04：20 项书签检查通过，覆盖：

- 首次使用不创建数据文件；真实 DPAPI 加密保存并由新存储实例读取；多实例并发添加、幂等操作和账户/云盘/项目隔离。
- 元数据更新保留收藏时间，迟到更新不会恢复已移除书签；加密或原子替换失败保留原文件，重试可成功。
- 损坏、未知版本、缺失必填字段、重复记录和无效输入被拒绝；显式恢复备份与重建保留原文件副本，正常配置不能直接重建。
- 离线列表、排序、筛选、移除及失败反馈；加载异常不显示为空列表；删除失效条目不访问云端。
- 重命名或移动后的定位；请求期间重复操作被拦截；切页取消并忽略迟到结果；名称更新保存失败仍可打开目标，并显示提醒。
- 实际 Graph 请求绑定原账户，缺失账户、登录失效和令牌账户不匹配时不回退；校验远端快捷方式、跨盘响应、删除标记和缺失父目录。
- 401/403/404/410/429、网络异常和取消；中英文资源及页面 UID 完整性。

文件管理回归共 46 项，其中新增的 5 项在 `BookmarkNavigationChecks.cs`，编译实际 `DriveViewModel` 和 `OneDrive`：后续分页目标选中和面包屑、文件夹内容打开、首次定位失败后刷新保留目标目录、目标再次移动时提示、迟到导航结果不覆盖新目录。

生产控件、实际页面导航及布局见 [书签 WinUI 验证](../BookmarkUi/README.md)。模拟网络测试不能替代真实服务的移动语义、租户权限和交互登录验证。
