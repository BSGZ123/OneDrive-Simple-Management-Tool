# 文件夹同步界面测试

使用生产 MainWindow、FolderSyncPage、绑定对话框和 ViewModel，注入本地模拟的云端接口。所有测试输入位于独立构建输出的 `probe-data` 下；上传只读到 `Stream.Null`，不会访问账号或真实网盘。

```powershell
msbuild "OneDrive Simple Management Tool.csproj" /p:Configuration=Debug /p:Platform=x64 /p:CustomAfterMicrosoftCommonTargets="$PWD\Tests\FolderSyncUi\Probe.targets" /p:OutputPath=bin\FolderSyncUi\ /p:IntermediateOutputPath=obj\FolderSyncUi\
& ".\bin\FolderSyncUi\OneDrive Simple Management Tool.exe"
```

窗口标题为 `Folder sync UI test — LOCAL ONLY`。界面包含“项目资料”和“照片归档”两个模拟绑定。测试模式下不要使用其他需要实际账号的功能。

## 人工检查步骤

1. 点击左侧“文件夹同步”，检查两条绑定的路径、状态、最近成功时间及按钮。
2. 暂停“项目资料”，在其测试目录新增文件，再恢复，确认状态和检查数量更新。
3. 点击“新建同步”，检查说明、选择器、云端浏览和错误提示。
4. 选择一个名为“同步测试”的本地可丢弃目录，在模拟网盘中进入同名目录；名称不一致时“绑定并同步”应禁用。
5. 绑定成功后列表应新增记录并自动首次同步；关闭后重开对话框不应保留旧的忙碌状态。
6. 到“任务 → 同步上传”检查当前进度；调整窗口宽度，确认文字和按钮可访问。
7. 解除绑定应先说明本地和云端文件均保留；取消应保持绑定不变。

具体已完成的界面验证记录在本文件后续的验证记录中；未记录的步骤不表示已通过。

## 2026-10-03 验证记录

| 检查 | 实际结果 |
| --- | --- |
| 使用 Computer Use 启动独立测试构建 | 成功，窗口标题为 LOCAL ONLY |
| 左侧导航进入文件夹同步 | 成功，展示两条模拟绑定和中文说明 |
| 路径、进度、最近成功时间 | 正常显示，活动绑定完成首次检查 |
| 点击暂停 | 状态变为已暂停，立即检查按钮禁用 |
| 暂停期间新增测试文件，再点击恢复 | 成功补扫，已检查项从 2/2 更新为 3/3 |
| 打开新建同步对话框 | 成功，未选目录时绑定按钮禁用 |
| 打开本地文件夹选择器 | 成功；PickerHost 未返回可操作窗口，已请求用户完成目录选择 |

助手执行的界面验证未覆盖通过对话框新建绑定、传输页和窄窗口；控制台业务检查不能替代这些界面步骤。

同步专项 20 项、分享 10 项、上传 18 项、文件管理 35 项回归通过。Debug/x64、Release/x64 构建成功，仅保留已有的 `ShareCommunityViewModel` CS1998 警告。上述测试未访问真实 OneDrive。

2026-10-03，用户确认本轮功能验收通过，并授权提交及推送远端分支。用户未逐项说明验收账号和场景，以上记录保留助手实际完成的验证范围。
