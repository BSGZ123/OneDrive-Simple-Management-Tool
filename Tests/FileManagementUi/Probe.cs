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
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

// Explicit opt-in build via Probe.targets. No real authentication or network calls.
internal static class FileManagementUiProbe
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
        protected override async void OnLaunched(LaunchActivatedEventArgs args)
        {
            Ioc.Default.ConfigureServices(new TestServices());
            var handler = new Handler();
            var provider = new OneDrive("local-test-drive", "local-test-account") { IsAuthenticated = true };
            var client = new GraphServiceClient(new HttpClient(handler), new AnonymousAuthenticationProvider());
            typeof(OneDrive).GetField("graphClient", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(provider, client);
            var drive = new DriveViewModel(provider, "Local test");
            var panel = new Grid { Padding = new Thickness(16), RowSpacing = 12 };
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var toggles = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
            toggles.Children.Add(new TextBlock { Text = "Local UI test — in-memory data only", VerticalAlignment = VerticalAlignment.Center });
            var failure = new CheckBox { Content = "Reject mutations" };
            failure.Checked += (_, _) => handler.FailMutation = true;
            failure.Unchecked += (_, _) => handler.FailMutation = false;
            toggles.Children.Add(failure);
            var refreshFailure = new CheckBox { Content = "Reject refresh" };
            refreshFailure.Checked += (_, _) => handler.FailRefresh = true;
            refreshFailure.Unchecked += (_, _) => handler.FailRefresh = false;
            toggles.Children.Add(refreshFailure);
            panel.Children.Add(toggles);
            var frame = new Frame();
            Grid.SetRow(frame, 1);
            panel.Children.Add(frame);
            frame.Navigate(typeof(DrivePage), drive);
            _window = new Window { Title = "File management UI regression — LOCAL", Content = panel };
            typeof(App).GetField("m_window", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, _window);
            _window.Activate();
            await drive.GetFiles();
        }
    }

    private sealed class Handler : HttpMessageHandler
    {
        public bool FailMutation;
        public bool FailRefresh;
        private readonly Dictionary<string, string> _names = new()
        {
            ["folder"] = "Demo folder", ["document"] = "Report.docx", ["notes"] = "Notes.md", ["plain"] = "LICENSE"
        };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(600, cancellationToken);
            string path = request.RequestUri.AbsolutePath;
            if ((request.Method == HttpMethod.Get && FailRefresh) || (request.Method != HttpMethod.Get && FailMutation))
                return Json("{\"error\":{\"code\":\"accessDenied\",\"message\":\"Local test denial\"}}", HttpStatusCode.Forbidden);
            if (path.EndsWith("/content"))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("# Local preview\n\nThis content comes from an in-memory test response.") };
            if (path.EndsWith("/createLink"))
                return Json("{\"id\":\"test-permission\",\"link\":{\"webUrl\":\"https://example.com/local-test\"}}");
            if (request.Method == HttpMethod.Get)
            {
                var rows = path.Contains("/items/folder/") ? Array.Empty<object>() : _names.Select(pair => Item(pair.Key, pair.Value)).ToArray();
                return Json(JsonSerializer.Serialize(new { value = rows }));
            }
            string id = path.Split('/').Last();
            if (request.Method == HttpMethod.Delete || id == "permanentDelete")
            {
                if (id == "permanentDelete") id = path.Split('/')[^2];
                _names.Remove(id);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            var body = JsonDocument.Parse(await request.Content.ReadAsStringAsync(cancellationToken));
            string name = body.RootElement.GetProperty("name").GetString();
            if (id == "children") id = "new-" + Guid.NewGuid();
            _names[id] = name;
            return Json(JsonSerializer.Serialize(new { id, name }));
        }

        private static object Item(string id, string name) => new
        {
            id, name, size = 1234, lastModifiedDateTime = "2026-10-03T00:00:00Z",
            folder = id == "folder" || id.StartsWith("new-") ? new { childCount = 0 } : null
        };

        private static HttpResponseMessage Json(string value, HttpStatusCode status = HttpStatusCode.OK) => new(status)
        {
            Content = new StringContent(value, Encoding.UTF8, "application/json")
        };
    }

    private sealed class TestServices : IServiceProvider
    {
        public object GetService(Type serviceType) => null;
    }
}
