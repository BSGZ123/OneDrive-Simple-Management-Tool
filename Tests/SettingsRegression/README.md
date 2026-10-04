# 设置页业务回归

运行：

```powershell
dotnet run --project Tests/SettingsRegression/SettingsRegression.csproj
```

直接编译生产偏好模型、存储及 ViewModel，使用独立临时目录和模拟保存边界，不读取用户偏好、账号或令牌。

12 组检查覆盖：首次默认值；全部主题/材质组合落盘；损坏、过大、未知版本与非法枚举回退且不改原文件；初始化不保存；即时应用与重新加载；保存失败重试；连续切换串行保存与等待完成；旧失败不覆盖新结果；材质回退保留选择；非法 UI 索引；Windows 文件占用时保留旧文件并清理临时文件；中英文资源。

文件占用检查依赖 Windows 文件共享语义。实际主题、材质和布局由 [WinUI 检查](../SettingsUi/README.md) 验证。
