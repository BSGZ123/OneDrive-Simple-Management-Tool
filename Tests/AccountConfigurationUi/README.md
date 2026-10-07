# 账户与配置 WinUI 检查

这个显式启用的构建使用生产页面、对话框与 ViewModel，注入虚构网盘、内存 Graph 响应和独立的 DPAPI 配置目录。不会登录微软账户或访问真实网盘。模拟文件位于独立构建输出的 `probe-data/<随机号>` 中。

```powershell
msbuild "OneDrive Simple Management Tool.csproj" /p:Configuration=Debug /p:Platform=x64 /p:CustomAfterMicrosoftCommonTargets="$PWD\Tests\AccountConfigurationUi\Probe.targets" /p:OutputPath=bin\AccountConfigurationUi\ /p:IntermediateOutputPath=obj\AccountConfigurationUi\
& ".\bin\AccountConfigurationUi\OneDrive Simple Management Tool.exe"
```

窗口标题为 `Account configuration UI test - LOCAL ONLY`，底部按钮仅存在于测试构建。

1. 初始页面有两张“同名网盘”卡片，容量显示“容量信息不可用”（模拟响应不含容量）。单击第二张、聚焦第二张按回车，或右键第二张选择“打开”，均应看到 `Selected fake-drive-B.txt`。
2. 勾选 `Reject saves`，点击 `Test add dialog`，输入显示名称并点“添加”。模拟登录完成后，保存应失败；对话框及输入保留，错误提示不包含异常原文。
3. 取消对话框、关闭 `Reject saves`，重新点击 `Test add dialog` 并添加。成功落盘后对话框关闭，网盘 C 进入列表。重复添加 C 应定位已有记录并提示，不能覆盖原名称。
4. 登录模拟期间或提交前取消，确认不新增网盘。忙碌时不能重复提交。
5. 点击 `Simulate damaged config`，返回文件列表并重新加载，应显示恢复说明及“重新加载 / 重试”“恢复备份”“重建列表”按钮并禁用新增；配置正常时这些按钮不出现；损坏的模拟文件保持原样，直到明确执行恢复或重建。
6. 在中英文界面及较窄窗口下检查文案、按钮和输入是否可见。测试构建中其他需要真实服务的导航不属于此测试入口。

2026-10-04 已通过原生界面检查：启动进入网盘列表；第二个同名项的双击、回车、菜单打开；保存失败保留名称和对话框；成功保存关闭对话框。存储恢复、并发、取消和日志策略另由控制台回归验证。未执行的界面步骤不计作已通过。

2026-10-07 网盘页改为卡片网格后，在中文界面用真实鼠标检查：宽窗口两列、窄窗口单列、单击第二张卡片进入 `Selected fake-drive-B.txt`、配置损坏时出现提示与三个恢复按钮且“添加网盘”禁用。右键菜单、回车打开、空状态、英文与深色界面未检查。
