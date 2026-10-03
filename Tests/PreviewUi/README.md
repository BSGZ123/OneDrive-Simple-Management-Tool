# 预览界面回归

此测试入口使用生产预览弹窗、ViewModel、加载服务和渲染器。本地生成 Markdown、PNG、SVG、BMP、PDF 和无声 WAV；PDF/媒体由本机 TCP 服务返回，不登录、不读取令牌、不访问真实 OneDrive。

```powershell
msbuild "OneDrive Simple Management Tool.csproj" /restore /p:Configuration=Debug /p:Platform=x64 /p:CustomAfterMicrosoftCommonTargets="$PWD\Tests\PreviewUi\Probe.targets" /p:OutputPath=bin\PreviewUi\ /p:IntermediateOutputPath=obj\PreviewUi\
& ".\bin\PreviewUi\OneDrive Simple Management Tool.exe"
```

点击 **Run preview regression** 执行控件回归。结果显示于窗口和输出目录的 preview-ui-results.log。常规构建不会编译 Probe.cs。环境变量 CLOUDFLOW_PREVIEW_AUTORUN=1 可用于测试启动后自动执行。

## 验收内容

| 操作 | 预期 |
| --- | --- |
| Markdown、空文档、PNG、SVG、PDF、WAV | Ready 或 Empty，文件名与内容一致 |
| 损坏图片、伪 PDF、损坏音频 | 失败有提示，可重试/下载/关闭 |
| 大 Markdown、48 MP 但仅约 6 MB 的 BMP | 触发字节或像素限制 |
| PDF 返回 403、附件下载响应 | 有限恢复或提示不支持内嵌；不自动保存文件 |
| Markdown 内嵌本地 HTTP 图片 | 内容渲染完成，图片读取纳入同一生命周期 |
| 加载中关闭 | 迅速返回，旧结果不会写回 |
| 首次权限失败，恢复后同弹窗重试 | 成功显示正文 |
| 无声 WAV 开始播放后关闭 | 播放器解除绑定，内容容器清空 |
| 图片、PDF、媒体各连续打开关闭 20 次 | 无未处理异常，记录各阶段私有内存与句柄 |
| Failed preview -> download picker | 走生产 FileActions；关闭预览后显示保存选择器，取消正常返回 |
| Narrow / wide、Dark / light | 检查窗口较窄及不同主题下的状态、错误和按钮布局 |

PDF 正常样本还需检查实际页面文字，不能仅凭 NavigationCompleted 判断验收通过。测试日志中的 Ready 只验证应用可观测的状态。

## 2026-10-03 本地验证记录

- 自动控件回归通过：正常/空 Markdown、PNG、SVG、PDF、WAV，损坏图片/音频、伪 PDF、403、附件响应、正文/像素超限、Markdown 内嵌图片、关闭加载中的请求、同弹窗重试。
- 无声 WAV 实际开始播放后关闭：播放器已解除绑定，内容容器清空，检查通过。
- 图片、PDF、音频各重复打开关闭 20 次。第 5/10/15/20 轮私有内存约 155.5/163.2/160.5/145.0 MiB，句柄 1926/1943/1942/1945；结束清理后约 145.3 MiB、1852 个句柄。未观察到随每轮持续累积的趋势；这是本机短时回归，不是长期内存泄漏证明。
- 通过 computer-use 实际查看 PDF 的 “Local PDF preview” 页面文字；损坏图片的重试保留原弹窗。
- 通过生产 FileActions 验证失败后下载：预览先关闭，保存选择器显示 Notes-download.md，按 Escape 取消后无错误返回。没有写入下载文件。
- 窄窗口、深色主题下，错误文字换行及重试/下载/关闭按钮均可见。见 [验收截图](Screenshots/error-dark-narrow.png)。

## 用户验收与后续专项回归

2026-10-03 用户确认本轮验收通过，并授权提交、推送到远端分支。具体账户、文件类型和异常场景未逐项记录，上述自动化与本地界面覆盖情况保留。

后续专项回归使用可丢弃的文件，逐项记录个人/组织账户、真实 OneDrive CDN、不同视频编码、播放拖动、实际断网、文件删除或权限变化，以及加密 PDF 的结果。本地模拟通过不代表所有真实服务场景均已覆盖。
