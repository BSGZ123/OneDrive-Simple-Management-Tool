using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.Graph;
using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OneDrive_Simple_Management_Tool;
using OneDrive_Simple_Management_Tool.Pages;
using OneDrive_Simple_Management_Tool.Services;
using OneDrive_Simple_Management_Tool.ViewModels;
using System;
using System.Collections.Concurrent;
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

// Production file selection, FolderPicker, Graph adapter and task page with local fixtures.
internal static class BatchDownloadUiProbe
{
    [STAThread]
    private static void Main()
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(_ =>
        {
            SynchronizationContext.SetSynchronizationContext(new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()));
            new ProbeApp();
        });
    }

    private sealed class ProbeApp : App
    {
        private Window _window;
        private LoopbackServer _server;
        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            var services = new TestServices();
            Ioc.Default.ConfigureServices(services);
            _server = new LoopbackServer
            {
                Bytes = Enumerable.Range(0, 8 * 1024 * 1024).Select(i => (byte)(i * 13)).ToArray(),
                DelayMilliseconds = 30
            };
            var handler = new MetadataHandler(_server);
            var provider = new OneDrive("local-drive", "local-account") { IsAuthenticated = true };
            var client = new GraphServiceClient(new HttpClient(handler), new AnonymousAuthenticationProvider());
            typeof(OneDrive).GetField("graphClient", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(provider, client);
            var drive = new DriveViewModel(provider, "Local batch downloads");
            string directory = Path.Combine(AppContext.BaseDirectory, "LocalDownloads", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var panel = new Grid { Padding = new Thickness(16), RowSpacing = 12 };
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var controls = new StackPanel { Spacing = 8 };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            var frame = new Frame();
            var files = new Button { Content = "Files" };
            files.Click += (_, _) => frame.Navigate(typeof(DrivePage), drive);
            var tasks = new Button { Content = "Tasks" };
            tasks.Click += (_, _) => frame.Navigate(typeof(TaskManagerPage));
            var selected = new Button { Content = "Queue selected locally" };
            selected.Click += (_, _) =>
            {
                if (!drive.CanDownloadSelection) return;
                var task = services.Manager.QueueDownloadBatch(provider, drive.SelectedItems.Select(item => item.Id), directory,
                    "批量下载（本地测试）");
                _ = task.StartDownload();
                frame.Navigate(typeof(TaskManagerPage));
            };
            var paused = new Button { Content = "Queue paused folder" };
            paused.Click += async (_, _) =>
            {
                var task = services.Manager.QueueDownloadBatch(provider, ["folder"], directory, "暂停 / 取消验证");
                Task running = task.StartDownload();
                await Task.Delay(900);
                await task.PauseDownload();
                await running;
                frame.Navigate(typeof(TaskManagerPage));
            };
            buttons.Children.Add(new TextBlock { Text = "LOCAL fixtures only", VerticalAlignment = VerticalAlignment.Center });
            buttons.Children.Add(files); buttons.Children.Add(tasks); buttons.Children.Add(selected); buttons.Children.Add(paused);
            controls.Children.Add(buttons);
            controls.Children.Add(new TextBlock { Text = directory, IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(controls);
            Grid.SetRow(frame, 1); panel.Children.Add(frame);
            frame.Navigate(typeof(DrivePage), drive);
            _window = new Window { Title = "Batch download UI regression — LOCAL", Content = panel };
            typeof(App).GetField("m_window", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, _window);
            _window.Activate();
            _window.Closed += async (_, _) =>
            {
                foreach (var task in services.Manager.DownloadTasks.ToArray()) await task.CancelTaskAsync();
                await _server.DisposeAsync();
                client.Dispose();
            };
        }
    }

    private sealed class MetadataHandler(LoopbackServer server) : HttpMessageHandler
    {
        private readonly ConcurrentDictionary<string, int> _requests = new();
        private readonly Dictionary<string, (string Name, string Parent, bool Folder, bool Empty)> _items = new()
        {
            ["root"] = ("Root", null, true, true),
            ["folder"] = ("演示资料", "root", true, false),
            ["sub"] = ("子目录", "folder", true, false),
            ["empty"] = ("空文件夹", "folder", true, true),
            ["large"] = ("内容.bin", "sub", false, false),
            ["zero"] = ("空文件.txt", "folder", false, true),
            ["report"] = ("Report.txt", "root", false, true)
        };
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            string[] segments = request.RequestUri.AbsolutePath.Split('/');
            bool children = segments.Last() == "children";
            string id = children ? segments[^2] : segments.Last();
            if (children)
            {
                if (id.Equals("Root", StringComparison.OrdinalIgnoreCase)) id = "root";
                return Task.FromResult(Reply(new { value = _items.Where(pair => pair.Value.Parent == id).Select(pair => Item(pair.Key)).ToArray() }));
            }
            if (id == "report" && _requests.AddOrUpdate(id, 1, (_, n) => n + 1) == 2)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    Content = new StringContent("{\"error\":{\"code\":\"accessDenied\",\"message\":\"One simulated failure\"}}", Encoding.UTF8, "application/json")
                });
            return Task.FromResult(Reply(Item(id)));
        }
        private object Item(string id)
        {
            var item = _items[id];
            return new Dictionary<string, object>
            {
                ["id"] = id, ["name"] = item.Name, ["size"] = item.Empty ? 0 : server.Bytes.Length,
                ["folder"] = item.Folder ? new { } : null, ["file"] = item.Folder ? null : new { mimeType = "application/octet-stream" },
                ["root"] = id == "root" ? new { } : null,
                ["parentReference"] = new { id = item.Parent, driveId = "local-drive" },
                ["cTag"] = "version-1", ["@microsoft.graph.downloadUrl"] = server.Url,
                ["lastModifiedDateTime"] = "2026-10-17T00:00:00Z"
            };
        }
        private static HttpResponseMessage Reply(object body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
    }

    private sealed class TestServices : IServiceProvider
    {
        public TaskManagerViewModel Manager { get; } = new();
        public object GetService(Type type) => type == typeof(TaskManagerViewModel) ? Manager : null;
    }
}
