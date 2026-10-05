# EPUB 阶段一实施记录

日期：2026-10-05，Asia/Shanghai。本文保留阶段一实施时的结果；完整阶段一兼容性验收尚未完成，后续阶段二进展见 [WinUI 验证记录](../ReaderUi/RESULTS.md)，阶段三未开始。

后续补充：用户提供的真实 EPUB 已完成专项验证并触发目录、异步排版、监听释放及初始化取消修复；最新自建回归为 **32/32**。真实样本 **12/12**，默认按用户确认的正常使用范围运行 **5 次开关**。以下保留首轮 29 项历史记录，当前结果、历史压力数据及本轮提交范围见 [LOCAL-BOOK-RESULTS.md](LOCAL-BOOK-RESULTS.md)。

## 环境与复现

- 项目基线：`e1ef6d0`。
- foliate-js：`78914aef4466eb960965702401634c2cb348e9b1`；zip.js 上游锁定版本：2.8.22。
- Windows `10.0.26200`，x64；Node `v20.20.0`，Playwright `1.63.0`。
- 实际浏览器：Microsoft Edge `154.0.4258.53`，无头 Chromium；此处不是 WebView2 验收。
- 最终浏览器运行开始：2026-10-05 03:11:31 +08:00。
- 命令：在 `Tests/ReaderWeb` 执行 `npm ci --ignore-scripts`，随后 `node run.mjs --performance`。基础集可执行 `npm test`。

所有样书由项目源码生成，版本、条目数、压缩/展开大小和 SHA-256 记录在本机 `artifacts/results.json` / `artifacts/fixtures/manifest.json`。这两个文件及截图为测试生成物，未加入 Git。读者可按 [README](README.md) 重建。没有使用真实 OneDrive 账户或私有书籍。

## 自动检查结果

最终 **29/29 通过**，其中基础集 27 项、性能/循环集 2 项。

| 检查 | 步骤与预期 | 实际 |
| --- | --- | --- |
| 消息边界（1） | 错误 JSON、旧 session、错误版本、未知命令、超长 CFI、无效数值、目录超量均拒绝；长中文目录按 UTF-8 字节分块 | 通过 |
| 基础样书（9） | text、EPUB 2、PNG 图文、固定版式、RTL、竖排、空目录、80 章节、长章节：显示正文、输出有界目录、翻页改变位置，关闭后 Blob 全释放 | 9/9 通过 |
| 目录/链接/键盘（1） | 目录跳第二章，内部链接跳第三章；输入框方向键不翻页，正文 PageDown 翻页 | 通过 |
| 位置恢复（1） | 捕获 CFI 后重建页面；改字号/行距/宽度/深色/滚动，窗口缩至 540×650 后重定位；坏 CFI 降级到比例/起点 | 通过；比例偏差断言小于 0.1 |
| 固定版式（1） | 下一页后适应宽度及 1.5 倍缩放；无字号排版 API 调用 | 通过，位置保留 |
| 恶意 EPUB（1） | 内联、书内、外部脚本、事件属性、javascript 链接、嵌套 frame、父桥调用及 window.postMessage；外部图片/CSS/iframe 请求 | 执行标记为空，独立 HTTP 探针请求为零；主动点击正常外链只产生一次宿主事件 |
| CSP 单独验证（1） | 测试 frame 临时保留 allow-scripts，验证 Blob 文档 CSP 继承 | 内联、Blob、Data、外部脚本、事件属性、外部图片/CSS 均阻止；执行标记和探针请求为零 |
| 异常输入（8） | 非 ZIP、声明 33 MiB 条目、路径穿越、伪造解压长度、未知 DRM、压缩文件声明超限、错误 MIME、302 重定向 | 全部拒绝；没有 opened 消息 |
| 流式实际大小（1） | 模拟无 Content-Length 的响应，持续供给超过 100 MB 的数据 | 按实际读取量报 BookLimit，并取消响应流 |
| 下载期间关闭（1） | 启动慢响应，立即关闭两次 | 无迟到 opened/location/error，无残留 Blob |
| 初始化期间关闭（1） | renderer 插入时关闭，观察后续结果 | 无迟到状态，无残留 Blob，不重新显示控件 |
| 路由（1） | 未知路径、编码路径、旧会话、带查询路由、非 GET 方法 | 拒绝 |
| 近上限书（1） | 96.3 MB 文件首屏和慢下载取消 | 通过；数值见下 |
| 同页循环（1） | 同一浏览器页面创建、读取并关闭 30 个会话，交替图文/固定版式 | 每轮关闭后宿主内容清空、全部书籍 Blob URL 撤销 |

最初固定版式失败是因为其 `getContents()` 不提供章节 index，且导航到同一页不产生 relocate。适配层现分别验证其可见章节和既有位置。新增伪造 ZIP 测试也暴露了上游吞掉读取错误的行为：加载器保留受控错误，定位后复核，避免误报定位失败。

自动截图已查看：固定版式插图、RTL 文字、日中竖排和深色窄窗口均有正文。此记录不代表人工阅读体验、真实字体库、WinUI 焦点或输入法专项验收。

## 性能观察

普通自建样书首次确认正文耗时 84–336 ms，包含本地回环供应、ZIP 解析、排版和位置确认；不包含 OneDrive 网络下载。

近上限样书：压缩 **96,299,437 字节**，声明展开 **96,415,075 字节**，93 条目、80 章节；实际首屏 **528 ms**，慢响应取消并关闭页面 **40 ms**。文件主要通过六个不参与显示的未压缩条目增加体积，适合观察完整 Blob 供应的成本，不能替代图像密集场景。

CDP 枚举本测试浏览器进程，读取 7 个相关进程的合计值；以下 MB 均为十进制 10^6 字节：

| 采样点 | Working Set MB | Private Bytes MB | 句柄 |
| --- | ---: | ---: | ---: |
| 近上限书打开前 | 504.5 | 278.0 | 3452 |
| 首屏就绪后 | 727.9 | 486.1 | 3493 |
| 循环 5 次关闭后 | 579.6 | 337.5 | 3524 |
| 循环 10 次关闭后 | 644.9 | 403.1 | 3548 |
| 循环 15 次关闭后 | 647.7 | 402.9 | 3530 |
| 循环 20 次关闭后 | 652.5 | 406.1 | 3525 |
| 循环 25 次关闭后 | 657.9 | 412.1 | 3549 |
| 循环 30 次关闭后 | 667.1 | 421.5 | 3582 |

近上限书就绪后的 JS heap 约 4.0 MB，明显小于进程内存增量，不能用 JS heap 代替浏览器总内存。循环过程中 DOM/监听器计数受 GC 影响上下波动，且后段进程内存仍有缓慢上升；本轮仅通过资源撤销断言，**尚未证明稳定平台期或不存在泄漏**。还需要更长的稳定阶段、加载中峰值采样及真实宿主生命周期测试。当前没有为运行内存设置已验收的产品阈值。

当前保持完整 Blob 加载作为阶段一候选；本轮数据不足以认定需要 Range，也不足以批准所有接近上限书籍的正式性能承诺。

## 构建和既有回归

使用本机已恢复的 NuGet 依赖：

```powershell
dotnet run --project Tests/ShareRegression/ShareRegression.csproj --no-restore
dotnet run --project Tests/UploadRegression/UploadRegression.csproj --no-restore
dotnet run --project Tests/FileManagementRegression/FileManagementRegression.csproj --no-restore
msbuild "OneDrive Simple Management Tool.sln" /p:Configuration=Debug /p:Platform=x64
msbuild "OneDrive Simple Management Tool.sln" /p:Configuration=Release /p:Platform=x64
```

分享 **10/10**、上传 **37/37**、文件管理 **46/46** 通过，合计 **93 项**。Debug/x64、Release/x64 构建通过；完整编译时保留已有 `ShareCommunityViewModel.cs` 的 CS1998 警告。最终增量构建通过。

逐文件 SHA-256 对比确认两种构建输出的全部 **21 个阅读器资源/许可文件**与源码一致。依赖校验通过（13 个 vendor 文件）。`git diff --check` 通过。尚未执行最终发布机、x86/ARM64、完整其他功能回归或真实账户验收，这些属于后续集成范围。

## 进入下一阶段前的验收缺口

图片解码像素/动画帧限制、Data 图片及复杂 SVG/CSS 预算、书内字体/混淆字体和复杂授权样书仍需补齐；本轮 ZIP 字节限制不能替代这些边界。本文记录阶段一当时的浏览器结果；随后完成的真实 WebView2 来源/frame 隔离、故障恢复、资源流/deferral 与 DPAPI 验证见 [阶段二记录](../ReaderUi/RESULTS.md)。OneDrive 权限/cache 路径仍属于第三阶段。

因此本次交付为阶段一可重复运行的基础实现，未把正式阅读页面或账户入口开放给用户，也未将计划的阶段门槛标记为全部通过。
