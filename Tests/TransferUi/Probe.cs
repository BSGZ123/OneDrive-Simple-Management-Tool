using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.Graph;
using Microsoft.Kiota.Abstractions.Authentication;
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
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;

// Opt-in: production dialogs, task UI and native close handler. No sign-in or cloud access.
internal static class TransferUiProbe
{
    [STAThread]
    private static void Main()
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(_ =>
        {
            SynchronizationContext.SetSynchronizationContext(new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()));
            Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = "zh-CN";
            new ProbeApp();
        });
    }

    private sealed class ProbeApp : App
    {
        private readonly TestServices _services = new();
        private readonly List<UploadFixture> _fixtures = new();
        private readonly List<Task> _runs = new();
        private readonly List<DownloadTaskViewModel> _downloads = new();
        private readonly object _logGate = new();
        private readonly string _root = Path.Combine(AppContext.BaseDirectory, "TransferProbeData", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        private MainWindow _window;
        private Pivot _pivot;
        private TextBlock _summary;
        private LoopbackServer _downloadServer;
        private GraphServiceClient _downloadClient;
        private DispatcherTimer _timer;
        private int _scenario;
        private bool _finishCalled;

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            Directory.CreateDirectory(_root);
            Ioc.Default.ConfigureServices(_services);
            UnhandledException += (_, error) => Log("unhandled", new { error.Exception.Message, error.Exception.StackTrace });
            _window = new MainWindow { Title = "传输界面验收 — LOCAL ONLY" };
            typeof(App).GetField("m_window", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, _window);
            _window.Navigate(typeof(TaskManagerPage));
            var tasks = (TaskManagerPage)_window.Rootframe.Content;
            _pivot = (Pivot)((Grid)tasks.Content).Children[0];
            _pivot.SelectedIndex = 1;
            var panel = new Grid { Padding = new Thickness(16), RowSpacing = 10 };
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            panel.Children.Add(new TextBlock { Text = "本地模拟验收：使用真实传输弹窗和关闭处理；不连接云端。", TextWrapping = TextWrapping.Wrap });
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            AddButton(buttons, "单个重名", () => ConflictAsync(1, false));
            AddButton(buttons, "零字节重名", () => ConflictAsync(1, true));
            AddButton(buttons, "批量重名（3 项）", () => ConflictAsync(3, false));
            AddButton(buttons, "启动上传和下载", ActiveAsync);
            Grid.SetRow(buttons, 1);
            panel.Children.Add(buttons);
            _summary = new TextBlock { Text = "就绪。请用右上角关闭或 Alt+F4 验证退出。", TextWrapping = TextWrapping.Wrap };
            Grid.SetRow(_summary, 2);
            panel.Children.Add(_summary);
            Grid.SetRow(tasks, 3);
            _window.Rootframe.Content = panel;
            panel.Children.Add(tasks);
            TransferWindowCloseGuard.Attach(_window, _services.Manager, FinishAsync);
            _window.AppWindow.Closing += (_, close) => Log("native-close", new
            {
                close.Cancel, _services.Manager.IsShuttingDown,
                uploads = _services.Manager.UploadTasks.Select(t => new { t.Name, state = t.State.ToString() }),
                downloads = _downloads.Select(t => new { t.Name, state = t.State.ToString(), t.DownloadedBytes })
            });
            _window.Closed += (_, _) =>
            {
                _timer.Stop();
                Log("window-closed", new { _finishCalled, allRunsCompleted = _runs.All(t => t.IsCompleted), remainingUploads = _services.Manager.UploadTasks.Count(t => t.NeedsExitProtection), remainingDownloads = _services.Manager.DownloadTasks.Count(t => t.NeedsExitProtection) });
            };
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _timer.Tick += (_, _) =>
            {
                if (_downloads.Count > 0)
                    Log("heartbeat", new { _services.Manager.IsShuttingDown, uploads = _services.Manager.UploadTasks.Select(t => t.State.ToString()), downloads = _downloads.Select(t => new { state = t.State.ToString(), t.DownloadedBytes }) });
            };
            _timer.Start();
            _window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1200, 850));
            _window.Activate();
            Log("ready", new { root = _root });
        }

        private void AddButton(StackPanel panel, string title, Func<Task> action)
        {
            var button = new Button { Content = title };
            button.Click += async (_, _) =>
            {
                button.IsEnabled = false;
                try { await action(); }
                catch (Exception error) { _summary.Text = error.Message; Log("scenario-error", new { error.Message, error.StackTrace }); }
                finally { button.IsEnabled = true; }
            };
            panel.Children.Add(button);
        }

        private async Task ConflictAsync(int count, bool empty)
        {
            if (_services.Manager.IsShuttingDown) return;
            foreach (var old in _services.Manager.UploadTasks.Where(t => t.IsTerminal).ToArray()) _services.Manager.UploadTasks.Remove(old);
            int scenario = ++_scenario;
            int prompts = 0;
            var resolver = new UploadConflictResolver(async (conflict, token) =>
            {
                prompts++;
                Log("conflict-shown", new { scenario, prompts, conflict.Name });
                var decision = await TransferDialogs.ResolveAsync(conflict, token);
                Log("decision", new { scenario, choice = decision.Choice.ToString(), decision.ApplyToBatch });
                return decision;
            });
            var fixture = new UploadFixture();
            _fixtures.Add(fixture);
            fixture.Metadata.Conflicts = _ => true;
            var drive = new DriveViewModel(fixture.Provider, "本地模拟网盘");
            var tasks = new List<Task>();
            var viewModels = new List<UploadTaskViewModel>();
            _pivot.SelectedIndex = 1;
            Log("scenario-start", new { scenario, count, empty });
            for (int i = 1; i <= count; i++)
            {
                string path = Path.Combine(_root, $"重名文件-{scenario}-{i}.txt");
                await File.WriteAllTextAsync(path, empty ? "" : "Local replacement data");
                var vm = new UploadTaskViewModel(drive, "root", await StorageFile.GetFileFromPathAsync(path), resolver);
                viewModels.Add(vm);
                _services.Manager.UploadTasks.Add(vm);
                tasks.Add(vm.StartUpload());
            }
            _runs.AddRange(tasks);
            await Task.WhenAll(tasks);
            Log("scenario-complete", new
            {
                scenario, prompts, states = viewModels.Select(t => t.State.ToString()),
                requests = fixture.Metadata.Requests.Select(r => new { r.Method, r.Path, r.Behavior }),
                slices = fixture.Server.Paths.Count
            });
            _summary.Text = $"场景 {scenario} 完成：{count} 项 / 弹窗 {prompts} 次 / {string.Join(", ", viewModels.Select(t => t.State))}";
        }

        private async Task ActiveAsync()
        {
            if (_services.Manager.IsShuttingDown || _downloads.Count > 0) return;
            var fixture = new UploadFixture();
            _fixtures.Add(fixture);
            fixture.Server.AllowDisconnects = true;
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Server.BeforeSliceResponse = token => release.Task.WaitAsync(token);
            fixture.Server.BeforeDeleteResponse = async token =>
            {
                Log("upload-cleanup-start", new { });
                await Task.Delay(4000, token);
                release.TrySetResult();
                Log("upload-cleanup-finished", new { });
            };
            string uploadPath = Path.Combine(_root, "正在上传.bin");
            using (var file = File.Create(uploadPath)) file.SetLength(4 * 1024 * 1024);
            _runs.Add(_services.Manager.AddUploadTask(new DriveViewModel(fixture.Provider, "本地模拟网盘"), "root", await StorageFile.GetFileFromPathAsync(uploadPath)));
            _downloadServer = new LoopbackServer { Bytes = new byte[32 * 1024 * 1024], DelayMilliseconds = 100 };
            _downloadClient = new GraphServiceClient(new HttpClient(new DownloadMetadata(_downloadServer)), new AnonymousAuthenticationProvider());
            var provider = new OneDrive("local-drive", "local-account");
            typeof(OneDrive).GetField("graphClient", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(provider, _downloadClient);
            string destination = Path.Combine(_root, "正在下载.bin");
            await File.WriteAllTextAsync(destination, "original destination");
            await File.WriteAllTextAsync(Path.Combine(_root, "已完成.txt"), "completed data");
            var download = new DownloadTaskViewModel(new DriveViewModel(provider, "本地模拟网盘"), "active", await StorageFile.GetFileFromPathAsync(destination));
            _downloads.Add(download);
            _services.Manager.DownloadTasks.Add(download);
            _runs.Add(download.StartDownload());
            _pivot.SelectedIndex = 0;
            _summary.Text = "上传和下载已启动。取消退出应保留任务；停止后退出应等待约 4 秒的上传清理。";
            Log("active-started", new { });
        }

        private async Task FinishAsync()
        {
            await Task.WhenAll(_runs);
            _finishCalled = true;
            Log("finish", new
            {
                allRunsCompleted = _runs.All(t => t.IsCompleted),
                remainingUploads = _services.Manager.UploadTasks.Count(t => t.NeedsExitProtection),
                remainingDownloads = _services.Manager.DownloadTasks.Count(t => t.NeedsExitProtection),
                originalDestinationPreserved = _downloads.Count == 0 || File.ReadAllText(_downloads[0].DestinationPath) == "original destination",
                completedFilePreserved = _downloads.Count == 0 || File.ReadAllText(Path.Combine(_root, "已完成.txt")) == "completed data",
                remainingFiles = Directory.GetFiles(_root).Select(Path.GetFileName)
            });
            foreach (var fixture in _fixtures) await fixture.DisposeAsync();
            if (_downloadServer != null) await _downloadServer.DisposeAsync();
            _downloadClient?.Dispose();
        }

        private void Log(string name, object data)
        {
            lock (_logGate) File.AppendAllText(Path.Combine(_root, "audit.jsonl"), JsonSerializer.Serialize(new { time = DateTimeOffset.Now, name, data }) + Environment.NewLine);
        }
    }

    private sealed class TestServices : IServiceProvider
    {
        public TaskManagerViewModel Manager { get; } = new();
        public object GetService(Type type) => type == typeof(TaskManagerViewModel) ? Manager : null;
    }

    private sealed class DownloadMetadata(LoopbackServer server) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            string json = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["id"] = "active", ["size"] = server.Bytes.Length, ["cTag"] = "local-version",
                ["file"] = new { mimeType = "application/octet-stream" }, ["@microsoft.graph.downloadUrl"] = server.Url
            });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }
}
