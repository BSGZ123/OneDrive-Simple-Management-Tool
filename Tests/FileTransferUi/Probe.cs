using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.Graph;
using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OneDrive_Simple_Management_Tool;
using OneDrive_Simple_Management_Tool.Models;
using OneDrive_Simple_Management_Tool.Pages;
using OneDrive_Simple_Management_Tool.Services;
using OneDrive_Simple_Management_Tool.ViewModels;
using OneDrive_Simple_Management_Tool.Views;
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

// Opt-in native UI with the production Graph adapter and an in-memory HTTP boundary.
internal static class FileTransferUiProbe
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
        private FileTransferViewModel _copy;
        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            Ioc.Default.ConfigureServices(new TestServices());
            var handler = new Handler();
            var client = new GraphServiceClient(new HttpClient(handler), new AnonymousAuthenticationProvider());
            var service = new GraphFileTransferService(client, "local-drive", new HttpClient(handler));
            var provider = new OneDrive("local-drive", "local-account") { IsAuthenticated = true };
            typeof(OneDrive).GetField("graphClient", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(provider, client);
            var drive = new DriveViewModel(provider, "本地模拟网盘");
            var panel = new Grid { Padding = new Thickness(16), RowSpacing = 12 };
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            controls.Children.Add(new TextBlock { Text = "LOCAL · 内存数据", VerticalAlignment = VerticalAlignment.Center });
            var select = new Button { Content = "Select sources" };
            select.Click += (_, _) => drive.SetSelection(drive.Files.Where(f => f.Id is "report" or "folder"));
            controls.Children.Add(select);
            var copy = new Button { Content = "Copy / resume (local monitor)" };
            copy.Click += async (_, _) =>
            {
                if (drive.SelectedCount == 0) return;
                if (_copy?.HasPendingCopy != true) _copy = new FileTransferViewModel(drive.SelectedItems, FileTransferKind.Copy, service);
                await FileActions.ShowDialogAsync(panel, new FileTransferView { DataContext = _copy });
            };
            controls.Children.Add(copy);
            var fail = new CheckBox { Content = "Reject Report once" };
            fail.Checked += (_, _) => handler.RejectOnce = true;
            fail.Unchecked += (_, _) => handler.RejectOnce = false;
            controls.Children.Add(fail);
            panel.Children.Add(controls);
            var frame = new Frame();
            Grid.SetRow(frame, 1); panel.Children.Add(frame);
            frame.Navigate(typeof(DrivePage), drive);
            _window = new Window { Title = "File transfer UI regression — LOCAL", Content = panel };
            typeof(App).GetField("m_window", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, _window);
            _window.Activate();
            _window.Closed += (_, _) => client.Dispose();
        }
    }
    private sealed record Item(string Name, string Parent, bool Folder);
    private sealed class Handler : HttpMessageHandler
    {
        public bool RejectOnce;
        private int _serial;
        private readonly Dictionary<string, int> _polls = new();
        private readonly Dictionary<string, Item> _items = new()
        {
            ["root"] = new("Root", null, true),
            ["dest"] = new("归档", "root", true),
            ["sub"] = new("2026", "dest", true),
            ["report"] = new("Report.txt", "root", false),
            ["folder"] = new("资料", "root", true),
            ["child"] = new("目录说明.txt", "folder", false),
            ["existing"] = new("Report.txt", "dest", false)
        };
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            await Task.Delay(350, token);
            string[] parts = request.RequestUri.AbsolutePath.Split('/');
            string last = parts.Last();
            if (request.RequestUri.Host == "api.onedrive.com")
            {
                int count = _polls.TryGetValue(last, out int previous) ? previous + 1 : 1;
                _polls[last] = count;
                return count < 4 ? Json(new { status = "inProgress", percentageComplete = count * 25 })
                    : Json(new { status = "completed", resourceId = last });
            }
            if (request.Method == HttpMethod.Get)
            {
                string id = last == "children" ? parts[^2] : last;
                if (id.Equals("Root", StringComparison.OrdinalIgnoreCase)) id = "root";
                return last == "children" ? Json(new { value = _items.Where(p => p.Value.Parent == id).Select(p => Raw(p.Key)).ToArray() }) : Json(Raw(id));
            }
            string source = last == "copy" ? parts[^2] : last;
            if (RejectOnce && source == "report")
            {
                RejectOnce = false;
                return Json(new { error = new { code = "accessDenied" } }, HttpStatusCode.Forbidden);
            }
            using var body = JsonDocument.Parse(await request.Content.ReadAsStringAsync(token));
            string name = body.RootElement.GetProperty("name").GetString();
            string parent = body.RootElement.GetProperty("parentReference").GetProperty("id").GetString();
            if (_items.Values.Any(i => i.Parent == parent && i.Name == name))
                return Json(new { error = new { code = "nameAlreadyExists" } }, HttpStatusCode.Conflict);
            if (request.Method == HttpMethod.Patch)
            {
                _items[source] = _items[source] with { Name = name, Parent = parent };
                return Json(Raw(source));
            }
            string copied = Clone(source, parent, name);
            return new(HttpStatusCode.Accepted) { Headers = { Location = new Uri("https://api.onedrive.com/monitor/" + copied) } };
        }
        private string Clone(string source, string parent, string name)
        {
            string id = "copy-" + ++_serial;
            _items[id] = _items[source] with { Name = name, Parent = parent };
            foreach (var child in _items.Where(p => p.Value.Parent == source).ToArray()) Clone(child.Key, id, child.Value.Name);
            return id;
        }
        private object Raw(string id) => new
        {
            id, name = _items[id].Name, eTag = "\"version-1\"", size = 24,
            parentReference = new { id = _items[id].Parent, driveId = "local-drive" },
            root = id == "root" ? new { } : null,
            folder = _items[id].Folder ? new { } : null, file = _items[id].Folder ? null : new { },
            lastModifiedDateTime = "2026-10-17T00:00:00Z"
        };
        private static HttpResponseMessage Json(object body, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
    }
    private sealed class TestServices : IServiceProvider
    {
        private readonly TaskManagerViewModel _manager = new();
        public object GetService(Type type) => type == typeof(TaskManagerViewModel) ? _manager : null;
    }
}
