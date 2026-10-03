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
            var controls = new StackPanel { Spacing = 8 };
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
            controls.Children.Add(toggles);
            var browsing = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
            var count = new ComboBox { Header = "Directory size", ItemsSource = new[] { 4, 1000, 10000 }, SelectedIndex = 0 };
            count.SelectionChanged += async (_, _) =>
            {
                handler.SetCount((int)count.SelectedItem);
                await drive.GetFiles();
            };
            browsing.Children.Add(count);
            var pageFailure = new CheckBox { Content = "Reject later pages" };
            pageFailure.Checked += (_, _) => handler.FailLaterPage = true;
            pageFailure.Unchecked += (_, _) => handler.FailLaterPage = false;
            browsing.Children.Add(pageFailure);
            controls.Children.Add(browsing);
            panel.Children.Add(controls);
            var frame = new Frame();
            Grid.SetRow(frame, 1);
            panel.Children.Add(frame);
            frame.Navigate(typeof(DrivePage), drive);
            _window = new Window { Title = "File management UI regression — LOCAL", Content = panel };
            typeof(App).GetField("m_window", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, _window);
            _window.Activate();
            // Exercise the page's navigation load; explicitly loading here would mask
            // a regression where entering a drive requires a manual refresh.
            for (int attempt = 0; attempt < 100 && drive.IsLoading == Visibility.Visible; attempt++)
                await Task.Delay(50);
            if (drive.IsLoading == Visibility.Visible || drive.HasError || drive.Files.Count != 4)
                throw new InvalidOperationException("Navigating to DrivePage did not automatically load its files.");
            _window.Title = "File management UI regression — LOCAL — initial load passed";
        }
    }

    private sealed class Handler : HttpMessageHandler
    {
        public bool FailMutation;
        public bool FailRefresh;
        public bool FailLaterPage;
        private readonly Dictionary<string, string> _names = new()
        {
            ["folder"] = "Demo folder", ["document"] = "Report.docx", ["notes"] = "Notes.md", ["plain"] = "LICENSE"
        };

        public void SetCount(int count)
        {
            foreach (string id in _names.Keys.Where(id => id.StartsWith("bulk-")).ToArray()) _names.Remove(id);
            for (int i = 4; i < count; i++)
                _names["bulk-" + i] = i == count - 1 ? "Last-page-match.txt" : $"File-{i:D5}.txt";
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(_names.Count > 4 ? 60 : 600, cancellationToken);
            string path = request.RequestUri.AbsolutePath;
            if ((request.Method == HttpMethod.Get && FailRefresh) || (request.Method != HttpMethod.Get && FailMutation))
                return Json("{\"error\":{\"code\":\"accessDenied\",\"message\":\"Local test denial\"}}", HttpStatusCode.Forbidden);
            if (path.EndsWith("/content"))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("# Local preview\n\nThis content comes from an in-memory test response.") };
            if (path.EndsWith("/createLink"))
                return Json("{\"id\":\"test-permission\",\"link\":{\"webUrl\":\"https://example.com/local-test\"}}");
            if (request.Method == HttpMethod.Get)
            {
                if (path.EndsWith("/root"))
                    return Json("{\"id\":\"root-id\",\"name\":\"Root\",\"folder\":{},\"root\":{}}");
                if (path.EndsWith("/items/folder"))
                    return Json("{\"id\":\"folder\",\"name\":\"Demo folder\",\"folder\":{},\"parentReference\":{\"id\":\"root-id\",\"driveId\":\"local-test-drive\"}}");
                string itemId = path.Split('/').Last();
                if (_names.TryGetValue(itemId, out string itemName))
                    return Json(JsonSerializer.Serialize(Item(itemId, itemName)));
                int offset = request.RequestUri.Query.StartsWith("?cursor=") ? int.Parse(request.RequestUri.Query.Substring(8)) : 0;
                if (FailLaterPage && offset > 0)
                    return Json("{\"error\":{\"code\":\"accessDenied\",\"message\":\"Later page denied\"}}", HttpStatusCode.Forbidden);
                var matches = _names.AsEnumerable();
                string decoded = Uri.UnescapeDataString(path);
                int start = decoded.IndexOf("search(q='", StringComparison.Ordinal);
                if (start >= 0)
                {
                    string keyword = decoded.Substring(start + 10, decoded.Length - start - 12).Replace("''", "'");
                    matches = matches.Where(pair => pair.Value.Contains(keyword, StringComparison.OrdinalIgnoreCase));
                }
                var all = path.Contains("/items/folder/") ? Array.Empty<object>() : matches.Select(pair => Item(pair.Key, pair.Value)).ToArray();
                var rows = all.Skip(offset).Take(200).ToArray();
                string next = offset + rows.Length < all.Length ? request.RequestUri.GetLeftPart(UriPartial.Path) + "?cursor=" + (offset + rows.Length) : null;
                return Json(JsonSerializer.Serialize(new Dictionary<string, object> { ["value"] = rows, ["@odata.nextLink"] = next }));
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
            id, name, size = id == "notes" ? Encoding.UTF8.GetByteCount("# Local preview\n\nThis content comes from an in-memory test response.") : 1234,
            file = id == "folder" || id.StartsWith("new-") ? null : new { mimeType = "text/plain" },
            lastModifiedDateTime = "2026-10-03T00:00:00Z",
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
