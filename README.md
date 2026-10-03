# OneDrive Simple Management Tool

![OneDrive-Simple-Management-Tool](https://socialify.git.ci/BSGZ123/OneDrive-Simple-Management-Tool/image?language=1&name=1&owner=1&theme=Light)

OneDrive简单管理工具，使用WinUI3开发

## 构建与运行

项目使用 .NET 8、Windows App SDK **1.8.12**（NuGet `1.8.260921001`）和 Windows SDK BuildTools `10.0.26100.4654`，目标框架仍为 `net8.0-windows10.0.22621.0`。

在 Visual Studio 2022 开发者 PowerShell 中执行：

```powershell
msbuild "OneDrive Simple Management Tool.sln" /restore /p:Configuration=Debug /p:Platform=x64
msbuild "OneDrive Simple Management Tool.sln" /restore /p:Configuration=Release /p:Platform=x64
```

调试时选择 `OneDrive Simple Management Tool (Unpackaged)` 启动配置。应用采用依赖框架的非打包部署，运行机器需要安装对应架构的 .NET 8 Desktop Runtime 和 Windows App Runtime 1.8.12（或同一 1.8 系列中满足最低版本要求的运行时）。Windows App Runtime 1.5 不能替代 1.8。Windows App Runtime 可从 [Microsoft 官方下载页面](https://learn.microsoft.com/windows/apps/windows-app-sdk/downloads-archive)获取。

发布 x64 版本：

```powershell
msbuild "OneDrive Simple Management Tool.csproj" /restore /t:Publish /p:Configuration=Release /p:Platform=x64 /p:RuntimeIdentifier=win-x64 /p:PublishProfile= /p:PublishDir=bin\publish\win-x64\
```

发布时保留完整输出目录及 `appsettings.json`，不要只复制 EXE。令牌缓存和网盘列表统一位于应用目录下的 `cache` 文件夹，请将非打包应用放在当前用户可写的位置。若旧版将缓存存放在其他工作目录，升级前请备份原 `cache` 文件夹，并在新应用首次启动前将其放到 EXE 所在目录。

升级 SDK 后应验证：首次启动、工具页及宽窄窗口布局、设置页、文件与传输页面、登录、上传下载、各类预览，以及未安装开发工具的机器上的发布版本启动。涉及文件修改或删除时使用可丢弃的测试文件。

# 操作说明

## 基础文件共享

在网盘的列表或网格视图中，右键单个文件或文件夹，选择“分享”，然后点击“生成链接”。生成成功后，点击“复制链接”，也可以在链接框内手动选中复制。

- 当前界面提供只读匿名链接：任何持有链接的人都可访问，无需登录。组织禁止匿名链接或当前账户无共享权限时会显示错误。
- 打开弹窗不会自动创建链接；生成期间禁止重复提交并保持弹窗打开。失败后可在同一弹窗重试。
- 使用文件所属网盘发起请求，并保留已有权限；重新打开弹窗生成链接时，服务可能返回已有链接。
- 关闭弹窗不会撤销链接。编辑权限、组织内共享、密码、有效期、撤销共享及分享社区暂未接入此次基础功能。

共享回归检查（在仓库根目录执行）：

```powershell
dotnet run --project Tests/ShareRegression/ShareRegression.csproj
dotnet run --project Tests/UploadRegression/UploadRegression.csproj
```

共享检查使用真实 Graph SDK 与本地 HTTP 模拟响应，编译生产服务及 ViewModel，不读取真实令牌或修改云端文件。覆盖文件/文件夹目标、所属网盘、只读匿名参数、权限保留、已有链接、无效响应、重复提交、错误重试、复制失败及中英文提示。

2026-10-03 验证：Debug/x64、Release/x64 构建通过；10 项共享回归检查与 18 项上传回归检查通过。构建保留原有分享社区 `CS1998` 警告；受网络限制出现 NuGet 源不可达警告，使用已缓存依赖完成构建。

本地 WinUI 界面验证通过：使用生产列表/网格控件和共享弹窗，配合内存中的 Graph 模拟响应，验证文件与文件夹入口、初始复制禁用、生成进度、权限错误提示、重试成功、链接显示及剪贴板复制成功反馈；没有发送真实云端请求。

2026-10-03 用户确认基础共享功能验证通过，没有发现问题。具体账户、布局及异常场景未逐项记录；以下保留各项覆盖情况，后续专项回归使用专门的测试文件/文件夹。

| 步骤 | 预期结果 | 实际结果 |
| --- | --- | --- |
| 分别从列表、网格的右键“分享”打开弹窗并生成链接 | 目标名称正确，生成成功，复制内容与显示一致 | 用户确认基础功能通过，布局覆盖未逐项记录 |
| 将链接粘贴到未登录的浏览器窗口 | 可查看目标文件或文件夹，权限为只读 | 用户确认基础功能通过，浏览器状态未单独记录 |
| 在不同账户的网盘执行共享 | 请求与生成链接均属于所选文件的网盘 | 真实多账户场景未单独确认 |
| 测试断网、文件删除、账户禁止匿名共享 | 显示对应错误；恢复条件后可以重试 | 本地模拟响应通过，真实异常场景未单独确认 |
| 分享已有权限的测试文件 | 原有权限仍保留 | 请求参数回归通过，真实账户权限保留未单独确认 |


## 列表与网格的基础文件操作

右上角“布局”按钮和空白处右键菜单可切换列表/网格。两种视图共用文件集合、单选状态、操作菜单和弹窗；切换保留当前目录、筛选结果和选中项，不额外请求云端。图片模式暂未开放，多选、批量操作、移动和复制不在本次范围内。

- 文件夹可双击或按 Enter 打开；Markdown、图片、媒体和 PDF 使用已有预览。未支持的预览类型禁用“打开”。
- 下载、重命名、删除、分享、属性和符合条件的 PDF 转换在两种布局中使用相同入口。文件夹下载保持禁用。
- 快捷键：Enter 打开、F2 重命名、Delete 删除、Alt+Enter 属性、F5 刷新、Ctrl+Shift+N 新建文件夹、Backspace/Alt+Left 返回上级。文本编辑和弹窗期间不会触发页面文件操作。
- 新建与重命名校验名称；请求期间禁用重复提交。失败保留输入并显示中英文错误，成功后刷新列表。若操作已成功但刷新失败，会提示只需刷新，不重复提交已完成的操作。普通删除默认不勾选永久删除。
- PDF 转换使用所选文件所属网盘，并通过 `format=pdf` 查询参数请求；取消保存不再发起转换。支持列表收敛到原有格式中仍受 [Microsoft Graph PDF 转换接口](https://learn.microsoft.com/en-us/graph/api/driveitem-get-content-format?view=graph-rest-1.0)支持的扩展名，不新增转换格式。
- 修复返回上级、网格图标打包，以及测试输出资源被主应用重复收集的问题。布局选择在当前应用会话内保留，不新增设置持久化。

2026-10-03 PDF 转换验收：用户已实际测试并确认可以成功转换，基础功能已完成。

运行文件管理回归：

```powershell
dotnet run --project Tests/FileManagementRegression/FileManagementRegression.csproj
```

2026-10-03 本次验证：Debug/x64、Release/x64 构建通过；17 项文件管理、10 项分享、18 项上传检查通过。保留原有 `ShareCommunityViewModel.Refresh` 的 `CS1998` 警告。环境中的 NuGet 在线还原不可用，使用已缓存依赖完成构建：还原时可按本机情况指定 `RestorePackagesPath`、`RestoreIgnoreFailedSources=true` 与 `NuGetAudit=false`，未修改项目的常规还原配置。

WinUI 使用生产页面与内存 Graph 响应验证了列表重命名、切换网格后保留选中项、F2、网格文件夹导航及返回、Ctrl+Shift+N、名称校验、权限错误反馈、网格图标与 Markdown 预览，以及无扩展名文件的保存选择器打开/取消。具体步骤、可重现的界面测试入口和截图见 [界面回归记录](Tests/FileManagementUi/README.md)。

2026-10-03 用户完成测试并确认本轮验证通过，同意提交至远端主分支。具体账户、文件类型和异常场景未逐项记录；上述自动化与本地模拟覆盖情况保留，供后续专项回归参考。

********


## 预览可靠性

Markdown、图片、PDF 和音视频预览统一显示文件名、加载状态和错误提示。失败可以在当前弹窗重试，也可以下载后查看；下载先关闭并清理预览，再进入已有保存选择器和下载任务。TXT 和 EPUB 尚未接入。

- 每次 PDF/媒体预览重新获取下载地址；可恢复错误最多自动恢复一次，之后由用户重试。关闭会取消请求、阻止迟到结果回写，并释放播放器和 WebView2。
- Markdown 限制为 2 MiB；图片限制为 20 MiB、4000 万像素，限制解码尺寸并使用专门的 SVG 加载方式。超限、空内容和解码失败都有提示。Markdown 内嵌图片也受读取限制和取消控制。
- PDF 先探测文件头，再由 WebView2 展示。HTTP 错误页、附件下载响应、初始化/导航/进程失败可回到应用错误状态；密码和 PDF 深层结构错误仍由内嵌查看器反馈。
- 音视频保留系统播放器控件。播放中失败或持续缓冲超时后允许手动重试；关闭时停止播放并释放资源。

2026-10-03：20 组预览、10 项共享、18 项上传、17 组下载、35 项文件管理回归通过；Debug/x64 与 Release/x64 构建通过，保留原有分享社区 CS1998 警告。使用本机缓存依赖完成构建，未升级包版本。

专项检查与边界见 [预览业务回归](Tests/PreviewRegression/README.md)，生产 WinUI 控件的本地验证入口见 [预览界面回归](Tests/PreviewUi/README.md)。

2026-10-03 用户确认本轮预览可靠性验收通过，并授权提交、推送。具体账户、文件类型、视频编码及断网场景未逐项记录；保留上述本地验证边界，供后续专项回归参考。

# 点点滴滴

## 当前
1. 常见格式转换（PDF 基础功能已完成）
2. EPUB书籍阅读
3. 修复Bug



## 出现的问题

### 2024-12-10
- [X] 导航至ToolPage页面时出现崩溃，报错COMException异常，已经确定问题代码位置

问题已经解决，原因是WinAppSDK1.6为最新版本，不支持或不允许这种实现方法，回退到1.5版本即可。
后续阅读下1.6版本的更新日志，应该是有破坏性更新。

2026-10-02 迁移至 1.8 时补充定位：工具页崩溃明确报错找不到 `Breakpoint640Plus` 资源，已将工具卡片的自适应断点改为 `MinWindowWidth="640"`，不再依赖该未定义资源。

本次迁移验证：

- Debug/x64、Release/x64、Release/x86、Release/ARM64 构建及 Release/x64 发布通过；保留原有 `ShareCommunityViewModel.Refresh` 的 `CS1998` 警告。
- 本机运行通过：主窗口启动、工具卡片显示与页面跳转、设置页、未登录的网盘列表、传输任务页，以及发布目录中的 EXE 启动。
- 发布测试发现图片资源未复制，已显式设置 `Assets` 内容复制到构建和发布目录。
- 2026-10-02 用户人工验证通过：真实账户登录、下载及已测试的预览流程均正常。
- 尚待人工验证：上传、窄窗口布局、无开发环境的机器启动，以及 x86/ARM64 的实际运行。
- 2026-10-02 用户确认本次迁移验收通过；未覆盖的专项测试保留为后续回归项。

### 2024-8-13（12-31）
- [X] 文件下载线程堵塞，文件虽然下载成功且程序正常关闭，但仍驻留后台

原因：下载组件缓存释放较慢，稍稍等等就行了

- [ ] 文件上传执行异步上传线程时，抛出 Microsoft.Graph.ServiceException


### 2024-08-04
- [X] 文件列表导航工具栏未居右侧

应为RelativePanel.AlignRightWithPanel

- [X] 需要管理员权限启动应用

已确定问题，首先程序启动需获取(创建)身份令牌缓存文件，package打包部署时，文件的读写位置在常规权限无法覆盖的区域(疑似)。Unpackaged方式部署时，一切操作都在程序所在目录，即不再需要管理员权限读写特权目录了。

## 预期
- [X] 登录(2024-08-04)
- [ ] UI
- [X] 容量(2024-08-04)
- [X] 下载
- [X] 上传
- [X] 下载进度
- [ ] 下载(多来源)
- [X] 预览
- [X] 基础共享（只读匿名链接生成与复制；2026-10-03 用户验收通过）
- [ ] 自动同步
- [X] 重命名
- [X] 删除
- [X] 属性
- [X] 转换常见格式文件（PDF，基础功能已完成；2026-10-03 用户实测通过）
- [ ] 新标签打开
- [ ] 自选主题
- [X] 多账户
- [ ] 语言国际化
- [ ] 工具页 
