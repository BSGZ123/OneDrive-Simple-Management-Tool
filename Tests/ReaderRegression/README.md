# EPUB 宿主逻辑回归

从仓库根目录运行（Windows、.NET 8 SDK）：

```powershell
dotnet run --project Tests/ReaderRegression/ReaderRegression.csproj
```

当前 18 项检查直接编译生产 Reader 会话、协议、资源提供器、存储及 ViewModel；WebView 边界使用模拟宿主，DPAPI 使用当前 Windows 用户的真实加解密。检查包括本地源文件租约、100 MB 限制、消息来源/会话/请求关联、TOC 与资源白名单、版本变化恢复、关闭取消、崩溃重试、保存失败与备份恢复、过期写入、保存超时和重新打开页面后的应用退出清理。

数据仅写入随机命名的临时目录。框架目标为 net8.0，Windows DPAPI 调用会产生 CA1416 平台提示；实际运行要求 Windows。这些检查不能替代 [真实 WinUI/WebView2 验证](../ReaderUi/README.md)。

受限网络下，如果依赖已存在于本机 NuGet 缓存，可显式使用该缓存还原后加 `--no-restore` 运行；不要为测试修改全局 NuGet 配置。
