# 下载任务界面验证

此入口展示生产任务页、命令、ViewModel、OneDrive 元数据解析与 Downloader。Graph 使用内存响应，下载字节来自本机 HTTP 服务，不登录或访问真实云端文件。任务仅写入系统临时目录 `CloudFlow.DownloadUi/<随机标识>`。

```powershell
msbuild "OneDrive Simple Management Tool.csproj" /restore /p:Configuration=Debug /p:Platform=x64 /p:CustomAfterMicrosoftCommonTargets="$PWD\Tests\DownloadUi\Probe.targets" /p:OutputPath=bin\DownloadUi\ /p:IntermediateOutputPath=obj\DownloadUi\
& ".\bin\DownloadUi\OneDrive Simple Management Tool.exe"
```

窗口标题包含 `LOCAL`。预置完成、暂停、权限失败和等待四种任务；`interactive.bin` 可手动继续并暂停。权限失败项的服务固定拒绝访问，重试后仍应显示权限错误并允许再次重试。

## 2026-10-03 验证记录

| 操作 | 预期 | 实际 |
| --- | --- | --- |
| 打开任务页 | 完成、暂停、失败、等待文案及对应按钮正确 | 通过 |
| 暂停任务点击继续 | 保留已有进度，显示下载速度和暂停按钮 | 通过 |
| 下载中点击暂停 | 先显示暂停中，停止写入后显示已暂停和继续按钮 | 通过 |
| 查看权限失败任务 | 中文错误、重试按钮、辅助功能名称和提示正常 | 通过 |

界面截图见 [states.jpg](Screenshots/states.jpg)。取消、重试成功、完成竞争及文件保护的结果由 DownloadRegression 验证。

尚未以真实 OneDrive 账户验证网络/CDN 行为；常规应用的登录、云端导航和预览没有在本轮重新进行人工验收。

用户随后确认本轮检查通过，并授权提交、推送到远端主分支；此确认不补充未记录的具体场景覆盖。

## 2026-10-07 任务卡片样式

任务页改为卡片样式后，在中文界面检查：完成、暂停、失败、等待四种任务的文案、进度条和对应按钮可见；520 宽窗口下状态文字完整、字节数截断；切到“上传”显示空状态提示。继续、暂停、重试、移除和右键菜单本轮未操作，[states.jpg](Screenshots/states.jpg) 仍是旧界面。
