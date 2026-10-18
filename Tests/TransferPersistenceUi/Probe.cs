using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OneDrive_Simple_Management_Tool;
using OneDrive_Simple_Management_Tool.Models;
using OneDrive_Simple_Management_Tool.Pages;
using OneDrive_Simple_Management_Tool.Services;
using OneDrive_Simple_Management_Tool.Tests.UploadRegression;
using OneDrive_Simple_Management_Tool.ViewModels;
using OneDrive_Simple_Management_Tool.Views;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;

internal static class TransferPersistenceProbe
{
    private static bool ControlsMode => File.Exists(Path.Combine(AppContext.BaseDirectory, "controls-mode"));
    [STAThread]
    private static void Main()
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(_ =>
        {
            SynchronizationContext.SetSynchronizationContext(new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()));
            Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride =
                File.Exists(Path.Combine(AppContext.BaseDirectory, "english-mode")) ? "en-US" : "zh-CN";
            new ProbeApp();
        });
    }

    private sealed class ProbeApp : App
    {
        private readonly ProbeServices _services = new();
        private UploadFixture _upload;
        private readonly LoopbackServer _server = new() { Bytes = new byte[8 * 1024 * 1024], DelayMilliseconds = 8 };
        private readonly string _directory = Path.Combine(AppContext.BaseDirectory, ControlsMode ? "TransferControlProbeData" : "TransferResumeProbeData");
        protected override async void OnLaunched(LaunchActivatedEventArgs args)
        {
            Directory.CreateDirectory(_directory);
            UnhandledException += (_, error) => File.AppendAllText(Path.Combine(_directory, "errors.txt"), error.Exception.ToString() + Environment.NewLine);
            Ioc.Default.ConfigureServices(_services);
            _upload = new();
            _upload.Provider.IsAuthenticated = true;
            _upload.Metadata.RejectFileName = ControlsMode ? null : "retry-me";
            var source = new Source(_server) { Fail = !ControlsMode };
            if (ControlsMode) _server.DelayMilliseconds = 0;
            var manager = _services.Manager;
            manager.Initialize(new TransferStore(Path.Combine(_directory, "records")), record =>
            {
                if (record.Direction == TransferDirection.Upload)
                {
                    var task = new UploadTaskViewModel(new DriveViewModel(_upload.Provider), record.ItemId, null);
                    task.AttachRecord(record, manager.SaveRecord, true);
                    manager.UploadTasks.Add(task);
                }
                else
                {
                    var session = new DownloadBatchSession(source, record.Roots.Select(x => x.Id), record.Path);
                    var task = new DownloadTaskViewModel(record.Name, session, manager, record.AccountId, record.DriveId);
                    task.AttachRecord(record, manager.SaveRecord, true);
                    manager.DownloadTasks.Add(task);
                }
            });
            var window = new MainWindow { Title = ControlsMode ? "传输控制验收 — LOCAL ONLY" : "传输持久化验收 — LOCAL ONLY" };
            typeof(App).GetField("m_window", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, window);
            window.Navigate(typeof(TaskManagerPage));
            var page = (TaskManagerPage)window.Rootframe.Content;
            var panel = new Grid { Padding = new Thickness(16), RowSpacing = 12 };
            panel.RowDefinitions.Add(new() { Height = GridLength.Auto });
            panel.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
            var header = new StackPanel { Spacing = 8 };
            header.Children.Add(new TextBlock { Text = "本机模拟服务器；关闭并重新运行可恢复任务，不登录或连接云端。", TextWrapping = TextWrapping.Wrap });
            var release = new Button { Content = "解除模拟故障" };
            release.Click += (_, _) => { source.Fail = false; _upload.Metadata.RejectFileName = null; release.Content = "故障已解除，可单项重试"; };
            header.Children.Add(release);
            panel.Children.Add(header);
            Grid.SetRow(page, 1); panel.Children.Add(page);
            window.Rootframe.Content = panel;
            TransferWindowCloseGuard.Attach(window, manager, () => Task.CompletedTask);
            window.Activate();
            if (manager.DownloadTasks.Count == 0)
            {
                var batch = manager.QueueDownloadBatch(source, new[] { "good", "bad" }, Path.Combine(_directory, "downloads"), "演示下载任务");
                if (ControlsMode) batch.Controls.SpeedLimitKiB = 256;
                else _ = batch.StartDownload();
            }
            if (manager.UploadTasks.Count == 0)
            {
                string local = Path.Combine(_directory, "演示上传目录");
                Directory.CreateDirectory(local);
                int size = ControlsMode ? 4 * 1024 * 1024 : 4096;
                await File.WriteAllBytesAsync(Path.Combine(local, "completed.bin"), new byte[size]);
                await File.WriteAllBytesAsync(Path.Combine(local, "retry-me.bin"), new byte[size]);
                if (ControlsMode)
                {
                    var record = new TransferRecord { Direction = TransferDirection.Upload, AccountId = "test-account", DriveId = "test-drive",
                        Name = "演示上传目录", Path = local, ItemId = "root", IsFolder = true, SpeedLimitKiB = 256 };
                    var task = new UploadTaskViewModel(new DriveViewModel(_upload.Provider), "root", null);
                    task.AttachRecord(record, manager.SaveRecord, true);
                    manager.SaveRequired(record);
                    manager.UploadTasks.Add(task);
                }
                else await manager.AddUploadTask(new DriveViewModel(_upload.Provider), "root", await StorageFolder.GetFolderFromPathAsync(local));
            }
        }
    }

    private sealed class ProbeServices : IServiceProvider
    {
        public TaskManagerViewModel Manager { get; } = new();
        public object GetService(Type type) => type == typeof(TaskManagerViewModel) ? Manager : null;
    }

    private sealed class Source(LoopbackServer server) : IDownloadTreeSource
    {
        public bool Fail = true;
        public string AccountId => "test-account";
        public string DriveId => "test-drive";
        public Task<DownloadTreeItem> GetDownloadItemAsync(string id, CancellationToken token) => Task.FromResult(id == "root"
            ? new DownloadTreeItem("root", "Root", true, 0, IsRoot: true)
            : new DownloadTreeItem(id, id == "good" ? "已完成.bin" : "待重试.bin", false, server.Bytes.Length, "root"));
        public Task<DownloadTreePage> GetDownloadChildrenAsync(string parentId, string nextLink, CancellationToken token) => throw new NotSupportedException();
        public Task<DownloadSource> GetDownloadSourceAsync(string itemId, CancellationToken token) =>
            itemId == "bad" && Fail ? throw new DownloadFailureException(DownloadFailure.AccessDenied)
                : Task.FromResult(new DownloadSource(server.Url, server.Bytes.Length, "v1"));
    }
}
