using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.Graph;
using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.UI.Xaml;
using OneDrive_Simple_Management_Tool;
using OneDrive_Simple_Management_Tool.Pages;
using OneDrive_Simple_Management_Tool.Services;
using OneDrive_Simple_Management_Tool.ViewModels;
using System;
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

// Opt-in production UI with synthetic Graph metadata and a loopback byte server.
internal static class DownloadUiProbe
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
        protected override async void OnLaunched(LaunchActivatedEventArgs args)
        {
            var services = new TestServices();
            Ioc.Default.ConfigureServices(services);
            _server = new LoopbackServer
            {
                Bytes = Enumerable.Range(0, 8 * 1024 * 1024).Select(i => (byte)(i * 13)).ToArray(),
                DelayMilliseconds = 20
            };
            var provider = new OneDrive("local-drive", "local-account") { IsAuthenticated = true };
            var client = new GraphServiceClient(new HttpClient(new MetadataHandler(_server)), new AnonymousAuthenticationProvider());
            typeof(OneDrive).GetField("graphClient", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(provider, client);
            var drive = new DriveViewModel(provider, "Local downloads");
            string directory = Path.Combine(Path.GetTempPath(), "CloudFlow.DownloadUi", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            _window = new Window { Title = "Download UI regression — LOCAL", Content = new TaskManagerPage() };
            typeof(App).GetField("m_window", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, _window);
            _window.Activate();
            _window.Closed += async (_, _) =>
            {
                foreach (var task in services.Manager.DownloadTasks.ToArray()) await task.CancelTaskAsync();
                await _server.DisposeAsync();
            };
            foreach (string id in new[] { "completed", "paused", "failed", "interactive" })
            {
                string path = Path.Combine(directory, id + ".bin");
                await File.WriteAllBytesAsync(path, Array.Empty<byte>());
                var task = new DownloadTaskViewModel(drive, id, await StorageFile.GetFileFromPathAsync(path));
                services.Manager.DownloadTasks.Add(task);
                if (id == "interactive") continue;
                var running = task.StartDownload();
                if (id == "paused")
                {
                    await Task.Delay(700);
                    await task.PauseDownload();
                }
                await running;
            }
        }
    }

    private sealed class MetadataHandler(LoopbackServer server) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            string id = request.RequestUri.Segments.Last();
            if (id == "failed") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("{\"error\":{\"code\":\"accessDenied\",\"message\":\"Local fixture\"}}", Encoding.UTF8, "application/json")
            });
            string json = JsonSerializer.Serialize(new System.Collections.Generic.Dictionary<string, object>
            {
                ["id"] = id, ["size"] = id == "completed" ? 0 : server.Bytes.Length,
                ["cTag"] = "version-1", ["file"] = new { mimeType = "application/octet-stream" },
                ["@microsoft.graph.downloadUrl"] = server.Url
            });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class TestServices : IServiceProvider
    {
        public TaskManagerViewModel Manager { get; } = new();
        public object GetService(Type type) => type == typeof(TaskManagerViewModel) ? Manager : null;
    }
}
