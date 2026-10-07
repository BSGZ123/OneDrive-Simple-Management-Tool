# 设置页 WinUI 检查

此入口使用生产设置页、主窗口、外观应用逻辑和偏好存储。配置及截图仅保存在独立测试输出的 `probe-results/<语言>/` 下，不启动账号认证或同步，不读写用户正式配置。

```powershell
msbuild "OneDrive Simple Management Tool.csproj" /p:Configuration=Debug /p:Platform=x64 /p:CustomAfterMicrosoftCommonTargets="$PWD\Tests\SettingsUi\Probe.targets" /p:OutputPath=bin\SettingsUi\ /p:IntermediateOutputPath=obj\SettingsUi\
& ".\bin\SettingsUi\OneDrive Simple Management Tool.exe"
& ".\bin\SettingsUi\OneDrive Simple Management Tool.exe" --english
```

测试自动操作下拉框，检查应用到窗口的主题、材质类型及普通背景回退；模拟保存失败并重试；往返首页验证选择保留；创建新存储和 ViewModel 验证重新加载；检查窄窗口及展开“关于”后的滚动布局。完成后自动关闭，结果写入 `result.txt`。

截图来自 XAML 的 RenderTargetBitmap，只包含应用内容，不包含系统标题栏按钮和原生合成背景。因此材质的实际视觉效果与系统策略需要人工检查。测试中的“重新加载”不是完整生产进程重启。

## 2026-10-04 本地验证

- 设置业务回归 12 组通过。
- 中英文生产控件检查通过，浅色、深色、保存失败、窄窗口及“关于”截图已检查。
- 既有分享 10、上传 37、文件管理 41、预览 23、同步 20、账户配置 46 项及下载回归全部通过。
- Debug/x64、Release/x64 和独立 WinUI 构建通过。使用现有 NuGet 缓存，未升级依赖；保留原有分享社区 CS1998 及 NuGet 源不可达警告。

截图：[中文浅色](Screenshots/light-zh.png)、[中文深色](Screenshots/dark-zh.png)、[英文窄窗口](Screenshots/narrow-en.png)、[保存失败](Screenshots/save-error-zh.png)。

## 第一轮人工验收

1. 在正式应用进入设置，分别选择浅色、深色、跟随系统；检查侧栏、标题栏按钮、文件页与文件操作弹窗的文字对比度。
2. 选择跟随系统后，在 Windows 设置切换系统应用明暗，观察本应用随之更新。
3. 切换无特殊材质、Mica、Mica Alt、Acrylic；检查效果及普通背景恢复。效果受系统透明设置、节电和远程桌面等环境影响。
4. 保存后退出并重启正式应用，确认主窗口及两个下拉框恢复选项；快速切换后立即关闭也应保留最后选择。
5. 缩窄窗口并展开“关于”，确认按钮、版本、源码及发布链接可以通过滚动访问。
6. 需要模拟损坏或保存失败时使用上述隔离入口和业务测试，不修改正式用户配置。

2026-10-04：用户确认本轮设置页验收通过，并授权提交、推送远端分支。真实系统主题切换、材质外观、高对比度和正式应用完整重启等场景未逐项记录；保留上述验收步骤及自动化覆盖边界，供后续专项回归使用。

## 2026-10-07 阅读缓存与诊断改为设置卡片

“阅读缓存”和“安全诊断”由文字加按钮改为设置卡片，归入新的“存储与诊断”分组；关闭的保存错误条不再占用间距，标题样式与其他页面一致。中英文自动检查均输出 PASS（未新增断言），四张截图已用本次运行结果替换，检查了中文浅色、中文深色和英文窄窗口。三页联合入口重新运行，中英文均通过，其中包含短窗口下三个诊断按钮文字完整的检查。清理缓存和诊断按钮本轮未实际点击。
