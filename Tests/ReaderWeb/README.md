# EPUB 浏览器验证（阶段一）

此目录使用真实 Chromium 测试 `Assets/Reader` 中的 EPUB 适配层。当前为第一阶段基础实现，尚未完成计划的全部阶段一验收；WinUI/WebView2 宿主、账户、缓存、加密进度与正式文件入口仍待后续实现。

## 运行

需要 Node.js 20+ 和本机 Microsoft Edge。测试仅绑定回环地址，使用新建的无头浏览器实例，不读取真实浏览器用户资料或 OneDrive 账户。

```powershell
cd Tests/ReaderWeb
npm ci --ignore-scripts
npm test
node run.mjs --performance
```

`npm test` 运行基础检查；`--performance` 额外生成接近容量上限的书籍，并在同一页面反复创建/关闭 30 个会话。可通过 `$env:READER_BROWSER_CHANNEL='chrome'` 改用本机 Chrome，测试记录会保存实际浏览器版本。依赖只需安装一次，运行不需要 CDN 或外部书库。

输出位于忽略的 `artifacts/`：

- `results.json`：每项结果、耗时、环境、引擎 SHA、测试/适配器源码散列、样书大小与散列、CSP 控制台记录及网络探针结果。
- `fixtures/manifest.json` 和 `.epub`：本项目生成的中文、图文、RTL、竖排、固定版式、EPUB 2、长章节、多章节和恶意样书。
- `reader-*.png`：图文、固定版式、RTL、竖排及深色窄窗口截图。

样书全部由 [fixtures.mjs](fixtures.mjs) 生成。文字、美术和攻击代码均为本项目测试内容，不含真实书库，也没有第三方书籍再分发。攻击样书包含运行时探针端口，因此每次运行记录实际 SHA；复现时以生成器及源码散列、实际样书清单共同标识版本。近上限样书主要使用未压缩填充条目，不能代表大量高分辨率图片的解码峰值。

交互检查可执行 `npm run serve`，打开打印的回环 URL，在浏览器控制台执行 `host.open()`。`host.command('turn', { direction: 'next' })` 翻页，`host.command('applySettings', { theme: 'dark', fontSize: 26 })` 调整设置。测试宿主的 `host` 全局对象和模拟桥只存在于本目录，应用输出不包含它们。

## 验证用户提供的 EPUB

```powershell
cd Tests/ReaderWeb
npm run verify-book -- --book "本地 EPUB 的绝对路径"
# 开发时只跑功能检查，跳过默认的 5 次循环：
npm run verify-book -- --book "本地 EPUB 的绝对路径" --cycles 0
```

该入口读取指定原文件，完成所有章节解析/显示、全部目录跳转、图片、设置、方向键、跨浏览器进程 CFI 恢复、取消和默认 5 次同页开关。每次关闭须清空阅读页、撤销全部书籍 Blob、解除全部尺寸监听；进程内存仅记录观察值，不调用强制 GC。按需可用 `--cycles N` 调整循环数。文件 SHA-256 前后核对；样书不复制到公共 fixtures，也不加入版本控制。报告只记录样本编号、散列、结构数量、受控错误及性能数值，书名、正文、CFI、原始异常和路径不写入报告。截图留在忽略的 `artifacts/local-book/`，包含书籍画面，不作为公共测试资源分发。

测试使用新浏览器配置与本地回环服务，外部网络路由额外拦截。CSP 和章节 sandbox 仍按生产适配层执行。书内 frame 禁止脚本，因此异步图片/字体检查在测试宿主脚本环境运行，通过 `load` 事件取得弱引用；不打开书内脚本权限。

本轮真实样书记录见 [LOCAL-BOOK-RESULTS.md](LOCAL-BOOK-RESULTS.md)。跨进程恢复时 CFI 由测试宿主内存保留，这项验证不代表 C# 进度已持久化。

## 固定依赖与分发

foliate-js 固定为 `78914aef4466eb960965702401634c2cb348e9b1`，zip.js 固定为上游该提交自带的 bundle（上游 lockfile 为 2.8.22）。来源、逐文件原始/修改后 SHA-256 及补丁说明在 [dependencies.json](../../Assets/Reader/dependencies.json)，许可证在 [THIRD-PARTY-NOTICES.md](../../Assets/Reader/THIRD-PARTY-NOTICES.md)。

```powershell
npm run verify-vendor
```

如需重建 vendor，将官方固定提交的归档解压到 `artifacts/upstream/foliate-js-78914aef4466eb960965702401634c2cb348e9b1`，执行 `node vendor.mjs`。脚本仅复制 EPUB 所需模块并应用已记录补丁，不自动下载浮动分支。`Assets/Reader` 在 Git 中固定 LF，避免 Windows 换行转换使散列失效。

项目文件显式将全部 `Assets/Reader` 内容及许可证复制到构建和发布目录；浏览器测试文件与 npm 依赖不进入应用。`verify-vendor.mjs` 核验分发依赖，所有相对静态/动态导入均由固定资源表供应，不分发 PDF.js、MOBI、fflate、FB2 或上游演示页面。

## 协议与边界

每个原生宿主尝试应创建新的 WebView2 与新的不可猜测 `sessionId`，导航到 `https://reader.invalid/reader/index.html#<sessionId>`。阅读页发送版本 1 的 `ready` 后，宿主发送 `openBook`。宿主须在注册所有拦截器后才导航，并且核验实际 `WebMessageReceived.Source`、请求关联和状态；这些平台侧规则还没有实现。

宿主用固定路由 `/book/<sessionId>.epub` 提供当前 EPUB，不向 JS 发送本机路径、账户信息或 Graph 下载地址。阅读页只绑定顶层 WebView2 的消息事件，不监听/转发普通 `window.postMessage`。所有输入经过 `protocol.js` 校验，参数使用有限枚举/数值；目录目标由适配层的会话内 ID 映射。目录同时按节点数和 UTF-8 消息字节数分块。

命令为 `openBook`、`applySettings`、`navigateToToc`、`restoreLocation`、`turn`、`close`。响应为 `ready`、`tocChunk`、`opened`、`locationChanged`、`restored`、`externalLinkRequested`、`hostCommand`、`readerError`、`commandCompleted`。成功确认附原 `requestId`，自发事件的请求 ID 为 null。外链只产生 HTTP/HTTPS 请求事件，后续 C# 仍必须校验、确认后才能调用系统浏览器。

状态为 idle → opening → restoring → ready，失败进入 failed。close 可打断等待中的工作，进入 closed，后续命令拒绝；重试应创建新会话。恢复期间不发临时起点进度。同内容 CFI 需解析到真实章节/锚点，且实际显示与定位事件满足条件；失效时依次尝试比例和起点。固定版式当前页不产生新的 relocate，故独立核对 renderer 的可见章节。

以下为原型的可测试预算，尚未按完整样书矩阵批准为正式产品限值：

| 项目 | 当前值 |
| --- | --- |
| EPUB 压缩大小 | 100,000,000 字节（十进制 100 MB） |
| ZIP 条目数 | 10,000 |
| 单条目 / 文本条目 | 32 MiB / 8 MiB |
| 累计不同资源解压量 | 256 MiB，读取中检查实际字节；重复读取同条目不重复计费 |
| 章节 / 目录节点数 | 1,024 / 1,024 |
| 文档元素 / 深度 | 50,000 / 64 |
| 目录深度 / 标签长度 / 单批节点数 | 16 / 256 UTF-16 字符 / 至多 64，另按消息字节拆批 |
| 消息 / CFI | 32 KiB UTF-8 / 4,096 UTF-16 字符 |
| 等待命令数 | 8；close 不受此队列限制 |
| 打开 / 定位命令 | 20 秒 / 5 秒 |

超时会拒绝发布结果并中止加载；不能中断已开始的同步解析或声称超时等于底层任务已结束。解压流检查取消，迟到的资源创建另有上游补丁阻止。错误只返回受控代码，不将正文或原始异常作为桥接 payload。

当前供应完整 Blob，随后按 ZIP 条目解析。没有实现 Range，也不声称零拷贝。CSP 字符串和固定资源表在 [policy.mjs](policy.mjs)，所有测试响应携带策略头；两个正文 renderer 的 sandbox 均移除了 `allow-scripts`。专门用例临时允许测试 frame 脚本，单独验证 Blob CSP 继承仍能拦截内联/Blob/Data/外部脚本及外部图片/CSS。独立 HTTP 探针必须收到零次请求；仅控制台报错不算通过。

## 第一阶段尚待验收

- 已补充一本文字型真实 EPUB 的全部章节、目录和图片验证；书内字体及混淆字体、复杂 RTL/竖排方向键、复杂固定版式与图片密集样本仍需补齐。单本真实样书通过不等同于 EPUB 全规范兼容。
- 图片解码像素/动画帧预算、内嵌 Data 图片及复杂 SVG/CSS 的资源限制。当前 ZIP 字节预算不能替代解码规模限制，故暂不开放正式账户入口。
- 接近上限样书的加载中进程峰值采样与更多参考机器测量。按本轮范围决定，正常使用的 5 次开关、关闭竞态和资源释放作为提交检查；高频长循环的进程内存观察不阻塞本次提交，也不代表长时间内存稳定性已验收。

真实 WebView2 来源/frame 隔离、资源流与 deferral、导航拦截、故障重建、DPAPI 存储、WinUI 焦点/输入法与宿主进程测试属于**阶段二工作及验收**，不是开始阶段二之前必须先实现的项目。OneDrive、缓存租约、文件菜单、离线策略和发布机验证属于阶段三。

本轮结果见 [RESULTS.md](RESULTS.md)。阶段一尚未整体签收，不能据此把阶段二/三或真实账户流程标为完成。
