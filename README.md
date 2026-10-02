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
...........................................


********


# 点点滴滴

## 当前
1. 常见格式转换
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
- [ ] 共享
- [ ] 自动同步
- [X] 重命名
- [X] 删除
- [X] 属性
- [ ] 转换常见格式文件(PDF)
- [ ] 新标签打开
- [ ] 自选主题
- [X] 多账户
- [ ] 语言国际化
- [ ] 工具页 
