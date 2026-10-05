# EPUB 第三阶段集成记录

日期：2026-10-05，Asia/Shanghai；开发基线 `49bba75`。本记录随第三阶段实现提交。

## 实现与使用

普通 EPUB 文件的列表/网格右键菜单新增“用阅读器打开”，进入独立 ReaderPage；双击及其他预览保持原行为。Graph 请求固定所选账户、网盘与 ItemId，拒绝远程快捷方式、已删除/非文件项目、外国网盘返回值和非 HTTPS 下载 URL。内容版本使用带来源前缀的 cTag / eTag。

缓存放在当前用户应用数据 `Reader/Cache`，原始 EPUB 不加密，文件名为随机内部 ID；索引单独使用 DPAPI。每次打开先在线获取当前元数据；权限失败、文件缺失和断网均不自动使用旧副本。缓存命中核对大小与本地 SHA-256，损坏时重新下载。下载复用生产 DownloadSession，先写临时文件，再验证大小、可用哈希及前后版本；不把 Graph URL 或本机路径交给 WebView。

缓存预算 500,000,000 字节，单书 100,000,000 字节、最多 1000 条索引；按最近使用时间回收未租用文件，保留正在打开书籍的旧完整版本直到新下载成功或未来正常清理。传输临时副本可能另占最多一份单书空间。目录租约串行化跨进程下载/清理，书籍只读文件租约阻止使用中的文件被删除。清理前验证路径与重解析点，限定已知内部文件名，不递归删除任意目录。设置页清理缓存保留阅读进度；索引损坏必须显式清理后重建。

下载内容现增加流读取字节上限，避免无 Content-Length 的超长响应先写满磁盘再失败。正常用户下载的 Range/重试/暂停/最终校验沿用原行为。

## 环境与执行结果

Windows 10.0.26200 x64；.NET SDK 9.0.318；目标 .NET 8 / Windows SDK 10.0.22621.0；Windows App SDK 1.8.260921001；实测 WebView2 Runtime **154.0.4258.53**。引擎 commit 仍为 `78914aef4466eb960965702401634c2cb348e9b1`。

| 检查 | 实际结果 |
| --- | --- |
| ReaderRegression | 29/29：协议、存储、缓存、身份隔离、版本变化、取消、容量、租约和损坏恢复 |
| FileManagementRegression | 49/49，包含 3 项 Graph 阅读来源检查 |
| DownloadRegression | 18/18，包含超长无长度响应在字节预算内停止 |
| Share / Upload / Preview | 10/10、37/37、23/23 |
| FolderSync / AccountConfiguration | 20/20、46/46 |
| Settings / Home / Bookmark | 12/12、20/20、20/20 |
| Chromium 默认集 | 31/31；增加栅格头、SVG、CSS 和 Data 图片限制断言，未运行长循环性能模式 |
| 用户授权的本地 EPUB | 11/11，覆盖全部 56 个章节、10 个目录、5 张图片、设置/布局及新进程恢复；此次 `--cycles 0`，正常 5 次开关由原生 full 另测 |
| 原生 cloud 探针 | 列表/网格菜单范围；生产下载到缓存再打开；新页面缓存命中与进度恢复；版本变化后按比例恢复；权限拒绝与重试；清理保留进度：通过 |
| 原生 full 探针 | 更新后的 CSP、frame 消息隔离、外部导航、所属渲染进程崩溃恢复、窄窗/深色/滚动、5 次开关与初始化中关闭：通过 |
| 构建及发布 | 正式 Debug/x64、Release/win-x64 发布通过；22 个阅读器资源/许可文件逐一比对 SHA-256 一致 |

控制台共 11 个项目、284 项检查通过。网络与 Graph 异常使用模拟边界，原生 cloud 由回环 HTTP 供应自建 EPUB，仍使用真实下载、缓存、DPAPI 与 WebView2。**这些结果不是实际 OneDrive 账户验收。**

用户样书 SHA-256 保持 `784fef7b57199e58717cee6cfb4f0bcb7fb69dff4260d7ce5b12c006c749caf7`；书籍、私有标题、正文截图、测试配置和日志未加入提交。本轮保留正常 5 次开关检查，不延伸高频循环内存分析。

构建使用本机已有 NuGet 包缓存作为显式 RestoreSources，并设置本次 NuGetAudit=false；不更改依赖版本和全局配置，不将离线还原视作在线漏洞审计通过。保留原有 ShareCommunityViewModel CS1998，以及 net8.0 控制台编译 Windows DPAPI 的 CA1416 提示。

## 复现

控制台：从仓库根目录逐项执行 `dotnet run --project Tests/<名称>Regression/<名称>Regression.csproj`。名称为 Reader、FileManagement、Download、Share、Upload、Preview、FolderSync、AccountConfiguration、Settings、Home、Bookmark。

浏览器：在 `Tests/ReaderWeb` 执行 `npm test`；本地样书执行 `npm run verify-book -- --book "<本地 EPUB 绝对路径>" --cycles 0`。

原生：按 [README](README.md) 构建探针，分别使用 `CLOUDFLOW_READER_AUTORUN=cloud` 与 `full`，每次等待进程退出并检查日志结尾。cloud 使用独立 `CLOUDFLOW_READER_ROOT`；回环服务器不连接 OneDrive。正式发布命令见根 README。

## 支持边界与人工验收

本轮将正式入口的图片范围明确限定为 JPEG、PNG、单帧 GIF 和受限 SVG；在解码前检查图片头。拒绝动画、其他栅格格式及复杂 SVG/CSS，CSP 禁止 Data 图片。普通字体限制 16 MiB，WOFF/WOFF2 声明展开长度受限；混淆字体暂不通过严格头检查。完整阈值见 [ReaderWeb 说明](../ReaderWeb/README.md)。这些有限预算不等于任意 EPUB、任意排版和浏览器渲染内存均已验收。

待用户/独立机器验收：

1. 真实账户中从列表和网格右键打开 EPUB，阅读后返回、重开及重启恢复；同盘改名/移动、内容替换、多账户同名文件、权限撤销和重新登录。
2. 清理缓存后重开仍恢复阅读进度；断网提示需联网，连接恢复后重试。
3. 两种语言的完整视觉检查、物理键盘/输入法、选区复制及外链确认到系统浏览器的完整流程。
4. 完整 Release 目录在无开发工具机器启动；WebView2 Runtime 存在/缺失两种情况。当前机器已有运行时，没有卸载共享运行时来模拟缺失。
5. x86/ARM64 的本轮构建和实际运行、更多字体/RTL/竖排/固定版式授权样书。

交付结论：第三阶段代码与本机自动化完成，可进入真实账户试读和发布机验收；不把未执行的人工场景标为通过。
