# 文件管理界面回归

此入口使用生产 `DrivePage`、列表/网格控件、菜单、弹窗和 ViewModel；Graph 请求全部由内存中的 `HttpMessageHandler` 返回。不会登录、读取令牌或访问真实云端文件。修改和删除只影响进程内的模拟数据，重启恢复初始状态。

在仓库根目录的 Visual Studio Developer PowerShell 中执行：

```powershell
msbuild "OneDrive Simple Management Tool.csproj" /restore /p:Configuration=Debug /p:Platform=x64 /p:CustomAfterMicrosoftCommonTargets="$PWD\Tests\FileManagementUi\Probe.targets" /p:OutputPath=bin\FileManagementUi\ /p:IntermediateOutputPath=obj\FileManagementUi\
& ".\bin\FileManagementUi\OneDrive Simple Management Tool.exe"
```

这是显式启用的独立构建入口，常规构建不会包含 `Probe.cs`。关闭测试应用后可重新构建。标题中的 `LOCAL` 和顶部说明用于区分正式应用。

顶部 `Reject mutations` 模拟修改操作的权限错误；`Reject refresh` 模拟列表加载错误。请求延迟 600 毫秒，便于观察忙碌状态。保存选择器是真的系统对话框；本测试中下载仅验证打开和取消，完整下载仍需正式应用的可丢弃测试文件。转换返回模拟内容，不用于检验真实 PDF 格式。

## 2026-10-03 验证记录

| 步骤 | 预期 | 实际 |
| --- | --- | --- |
| 列表右键 Report.docx → 重命名 | 菜单为中文，未修改名称时禁止保存 | 通过 |
| 输入新名称并保存 | 异步完成后关闭弹窗，列表显示新名称 | 通过 |
| 切换网格并按 F2 | 保持选中项，打开同一文件的重命名弹窗 | 通过 |
| 网格双击 Demo folder，然后 Alt+Left | 进入空目录并更新面包屑，返回根目录恢复文件列表 | 通过 |
| Ctrl+Shift+N | 打开新建弹窗，空名称禁止创建 | 通过 |
| 启用 Reject mutations，填写名称后按 Enter | 保留输入、显示中文权限错误、恢复创建按钮 | 通过 |
| LICENSE → 下载 → 取消 | 无扩展名文件可打开主窗口所属的系统保存选择器，取消无错误 | 通过 |
| 最终构建切换网格 | 文件夹及文件图标正常显示，保持选择 | 通过 |
| 网格双击 Notes.md | 打开共用预览弹窗并渲染内存返回的 Markdown | 通过 |

列表截图：[list.png](Screenshots/list.png)。网格截图：[grid.png](Screenshots/grid.png)。

自动化业务回归另见 `Tests/FileManagementRegression`，覆盖普通/永久删除端点、失败重试、重复提交、操作成功但刷新失败、转换取消和请求参数等。本地模拟通过不代表所有真实服务场景均已覆盖。

2026-10-03 用户完成测试并确认本轮验证通过，同意提交至远端主分支。具体账户、文件类型、异常场景及永久删除等专项覆盖未逐项记录，上表仅表示已记录的本地界面验证。
