# WinUI EPUB 本地验证

这是显式启用的隔离测试入口，使用生产 ReaderPage、ReaderSession、WebView2 控制器和 DPAPI 存储。启动时跳过应用登录、OneDrive 和同步初始化；数据写入独立根目录。普通应用构建不会包含测试入口。第三阶段已新增正式文件右键入口；真实账户与发布机验收单独记录。

## 构建及手工试读

在仓库根目录的 Visual Studio Developer PowerShell 中运行：

```powershell
msbuild "OneDrive Simple Management Tool.csproj" /p:Configuration=Debug /p:Platform=x64 "/p:CustomAfterMicrosoftCommonTargets=$PWD/Tests/ReaderUi/Probe.targets" /p:OutputPath=bin/ReaderUi/ /p:IntermediateOutputPath=obj/ReaderUi/
$env:CLOUDFLOW_READER_ROOT = Join-Path $PWD 'Tests/ReaderUi/artifacts/manual-profile'
Remove-Item Env:CLOUDFLOW_READER_AUTORUN -ErrorAction SilentlyContinue
Start-Process -FilePath '.\bin\ReaderUi\OneDrive Simple Management Tool.exe'
```

点击 **Open local EPUB** 选择书籍，可使用目录、前后翻页、设置；测试工具栏提供宽窄窗口和宿主深浅色切换。关闭测试程序后再重建，避免文件被占用。需要本机已安装的 Windows App Runtime 1.8 与 WebView2 Runtime；应从正常用户会话运行。

## 可重复自动检查

先按 [ReaderWeb](../ReaderWeb/README.md) 安装依赖并生成 `artifacts/fixtures`。设置环境变量后启动同一个测试程序；结果写入 `bin/ReaderUi/reader-ui-results.log`，结尾必须为 `ALL READER UI CHECKS PASSED`，程序退出码本身不代表测试通过。

```powershell
$env:CLOUDFLOW_READER_ROOT = Join-Path $PWD 'Tests/ReaderUi/artifacts/profile'
$env:CLOUDFLOW_READER_BOOK = Join-Path $PWD 'Tests/ReaderWeb/artifacts/fixtures/text.epub'
$env:CLOUDFLOW_READER_AUTORUN = 'full'
Start-Process -FilePath '.\bin\ReaderUi\OneDrive Simple Management Tool.exe' -Wait
Get-Content bin/ReaderUi/reader-ui-results.log
```

模式：

| 值 | 检查 |
| --- | --- |
| `smoke` | 打开、目录、翻页、设置、加密保存及关闭；固定版式另验证缩放/禁用重排控件 |
| `full` | smoke 加 CSP、书内脚本禁用、frame 消息隔离、外部导航、所属渲染进程故障恢复、窄窗口/深色/滚动、5 次正常开关和初始化中关闭；使用文字样书 |
| `restore` | 新进程复用 smoke 的根目录和书，验证进度及字号 26/深色设置恢复；先运行 smoke，再运行此模式 |
| `bookscan` | smoke 加逐一导航所有目录项；适用于用户授权的本地 EPUB |
| `cloud` | 原生列表/网格菜单范围、回环 HTTP 下载、生产缓存、云身份进度、版本变化、权限拒绝/重试及清理；使用自建文字样书，与真实 Graph 账户验收分开 |

`full` 通过当前测试 WebView 的 CDP `Page.crash` 触发其渲染进程故障，随后验证手动重试路径；不查杀其他应用进程。特殊测试 frame 临时允许脚本，确认 frame 确实发送了消息，但宿主返回操作未触发；生产正文 frame 仍必须只有 `allow-same-origin`。

测试截图分别为 `reader-body.png`（WebView 内容）与 `reader-layout.png`（WinUI 控件；RenderTargetBitmap 不包含 WebView 合成表面）。这些文件、隔离配置、日志及样书均不提交。截图可能含书名和正文，私有样书测试结果只能在本地查看。

## 实现边界

- 本地测试用路径散列和内容 SHA-256；正式入口固定账户/网盘/项目身份并使用 cTag（缺失时 eTag），标记版本来源。不把路径或 Graph 下载 URL 传入 JS。
- 原始 EPUB 以只读租约和受控流提供给本次会话。路由、CSP、MIME、导航及消息来源由宿主校验；无 host object，禁止下载、权限请求和新窗口。HTTP/HTTPS 外链必须经原生确认对话框。
- 进度与设置分别使用当前用户 DPAPI、版本封套、原子替换及备份，损坏时提示；恢复/重建必须由用户主动确认。关闭保存等待预算 2 秒，超时显示未保存；底层任务未结束前仍持有写入序列锁。
- 正文缓存和 Graph 下载已接入；首版每次打开要求联网，缓存 500 MB，超限按最近使用时间清理未被租用的文件。图片资源范围与阶段三待验收项见 [STAGE3-RESULTS.md](STAGE3-RESULTS.md)。阶段二历史结果保留在 [RESULTS.md](RESULTS.md)。
