# EPUB 宿主逻辑回归

从仓库根目录运行（Windows、.NET 8 SDK）：

```powershell
dotnet run --project Tests/ReaderRegression/ReaderRegression.csproj
```

当前 29 项检查直接编译生产 Reader 会话、协议、资源提供器、存储及 ViewModel；WebView 边界使用模拟宿主，DPAPI 使用当前 Windows 用户的真实加解密。检查包括本地源文件租约、100 MB 限制、消息校验、版本变化恢复、关闭取消、崩溃重试、备份恢复、过期写入和保存超时。缓存检查使用回环 HTTP 和真实 DownloadSession，覆盖身份隔离、缓存命中仍检查权限、版本变化、取消清理、哈希篡改、LRU 容量、文件租约和索引损坏恢复。

数据仅写入随机命名的临时目录。框架目标为 net8.0，Windows DPAPI 调用会产生 CA1416 平台提示；实际运行要求 Windows。这些检查不能替代 [真实 WinUI/WebView2 验证](../ReaderUi/README.md)。

受限网络下，如果依赖已存在于本机 NuGet 缓存，可显式使用该缓存还原后加 `--no-restore` 运行；不要为测试修改全局 NuGet 配置。
