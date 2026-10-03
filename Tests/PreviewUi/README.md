# 预览界面回归

此测试入口使用生产预览弹窗、ViewModel、加载服务和渲染器。本地生成 TXT、Markdown、PNG、SVG、BMP、PDF 和无声 WAV；PDF/媒体由本机 TCP 服务返回，不登录、不读取令牌、不访问真实 OneDrive。

```powershell
msbuild "OneDrive Simple Management Tool.csproj" /restore /p:Configuration=Debug /p:Platform=x64 /p:CustomAfterMicrosoftCommonTargets="$PWD\Tests\PreviewUi\Probe.targets" /p:OutputPath=bin\PreviewUi\ /p:IntermediateOutputPath=obj\PreviewUi\
& ".\bin\PreviewUi\OneDrive Simple Management Tool.exe"
```

点击 **Run preview regression** 执行可靠性控件回归，**Run reading regression** 执行基础阅读控件回归。结果显示于窗口和输出目录的 preview-ui-results.log，每次启动覆盖日志。常规构建不会编译 Probe.cs、ReadingProbe.cs。环境变量 CLOUDFLOW_PREVIEW_AUTORUN=1 用于自动执行可靠性回归；设为 reading 自动执行阅读回归。

```powershell
$env:CLOUDFLOW_PREVIEW_AUTORUN = "reading"
& ".\bin\PreviewUi\OneDrive Simple Management Tool.exe"
Remove-Item Env:CLOUDFLOW_PREVIEW_AUTORUN
```

手动使用 **Reading Markdown**、**Reading TXT**、**GBK TXT**、**Large image** 打开阅读样本。选择 **Narrow / wide**、**Dark / light** 后，再打开样本检查对应窗口和主题；这些开关只影响测试窗口。

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

## 2026-10-03 可靠性阶段本地验证记录

- 自动控件回归通过：正常/空 Markdown、PNG、SVG、PDF、WAV，损坏图片/音频、伪 PDF、403、附件响应、正文/像素超限、Markdown 内嵌图片、关闭加载中的请求、同弹窗重试。
- 无声 WAV 实际开始播放后关闭：播放器已解除绑定，内容容器清空，检查通过。
- 图片、PDF、音频各重复打开关闭 20 次。第 5/10/15/20 轮私有内存约 155.5/163.2/160.5/145.0 MiB，句柄 1926/1943/1942/1945；结束清理后约 145.3 MiB、1852 个句柄。未观察到随每轮持续累积的趋势；这是本机短时回归，不是长期内存泄漏证明。
- 通过 computer-use 实际查看 PDF 的 “Local PDF preview” 页面文字；损坏图片的重试保留原弹窗。
- 通过生产 FileActions 验证失败后下载：预览先关闭，保存选择器显示 Notes-download.md，按 Escape 取消后无错误返回。没有写入下载文件。
- 窄窗口、深色主题下，错误文字换行及重试/下载/关闭按钮均可见。见 [验收截图](Screenshots/error-dark-narrow.png)。

## 2026-10-03 基础阅读阶段本地验证记录

| 操作 / 检查 | 预期 | 实际结果 |
| --- | --- | --- |
| 生产 FileViewModel 的中文.TXT、重命名为 .bin 再改回 .txt | 入口能力随扩展名更新 | 通过 |
| GBK TXT 自动解码失败，选择 GBK，再切换自动和 GB18030 | 同弹窗恢复，两行中文完整，仅获取一次正文 | 通过；元数据及正文缓存行为另有控制台检查 |
| TXT 全选、24 号字、关闭换行 | 只读文本可完整选择，字号和换行属性生效 | 通过；原生 TextBox 规范化换行符，文本比较按行校验 |
| TXT 弹窗打开时窗口从 1100 改为 540，再恢复 | 弹窗不越界、会话保持 Ready，阅读状态保留 | 通过；尺寸为物理窗口像素 |
| Markdown 7.1.2 标题、粗斜体、引用、嵌套列表、代码和表格 | 生产解析器识别各块；长代码与宽表格独立横向滚动 | 通过；代码可视/内容宽约 400/970，表格约 420/840 |
| Markdown 字号改为 22，切换源码并全选 | 渲染字号更新，源码全文可选择 | 通过 |
| 21 万字符 Markdown | 以完整可选纯文本打开并提示 | 通过；不截断内容 |
| 空 TXT | Empty，编码信息仍可见 | 通过 |
| SVG 240×120、2in×1in | 保持原始比例，绝对单位正确换算 | 通过；2in×1in 为 192×96 DIP |
| 3000×2000 PNG 的 100% 与快速连续缩放 | 原图像素对应屏幕像素，按需提升解码分辨率 | 通过；检查 DecodePixelWidth 达到 3000 |
| 100×10000 SVG 的适应窗口与恢复 100% | 支持低于原生 ScrollViewer 10% 下限的实际比例 | 通过；适应时无需纵向滚动，恢复后尺寸正确 |
| PDF 实际内容与放大按钮 | 页面显示文字，缩放生效，保存使用应用下载入口 | 通过实际查看 “Local PDF preview”，放大后文字变大，工具栏无内置保存按钮 |
| PDF 原生查找 | 打开查找框，输入后有匹配结果 | 查找框已打开；后续界面自动化失去可靠窗口绑定，未确认输入和匹配结果 |

阅读控件日志结果为 `ALL READING UI CHECKS PASSED`。原有可靠性控件回归也通过，包括正常与错误状态、取消、重试、媒体解绑，以及 PNG/PDF/WAV 各 20 次打开关闭。第 5/10/15/20 轮私有内存约 191.9/202.6/202.9/195.7 MiB，句柄 2074/2089/2117/2126；全部清理后约 195.6 MiB、2021 个句柄，起始约 175.2 MiB、1952 个句柄。这是本机短时记录，不能据此证明长期没有泄漏。

业务层 103 项检查及 Debug/x64、Release/x64 构建通过；无依赖升级，保留原有分享社区 CS1998 警告。Markdown 仍使用 7.1.2，通过其渲染器扩展点为表格添加横向滚动；复杂长文采用完整纯文本保护路径。

本轮窄窗口恢复、文本选择与缩放通过生产控件自动断言。新增阅读界面的深色主题、工具栏溢出菜单、剪贴板实际粘贴和触控手势还需人工验收；上方深色错误截图属于前一轮可靠性验收。DPI 比例计算覆盖 100%/150%/200%，本轮未切换系统 DPI，也未逐一验证多显示器迁移、EXIF 方向和各类 SVG 尺寸写法。

2026-10-03 用户确认本轮基础阅读体验验收通过，并授权提交、推送远端。具体文件、编码及操作场景未逐项记录；以上本地验证记录与未逐项确认的专项场景继续保留。

## 可靠性阶段用户验收与后续专项回归

2026-10-03 用户确认本轮验收通过，并授权提交、推送到远端分支。具体账户、文件类型和异常场景未逐项记录，上述自动化与本地界面覆盖情况保留。

后续专项回归使用可丢弃的文件，逐项记录个人/组织账户、真实 OneDrive CDN、不同视频编码、播放拖动、实际断网、文件删除或权限变化，以及加密 PDF 的结果。本地模拟通过不代表所有真实服务场景均已覆盖。
