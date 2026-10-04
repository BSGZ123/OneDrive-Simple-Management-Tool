using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Graph;
using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using OneDrive_Simple_Management_Tool;
using OneDrive_Simple_Management_Tool.Helpers;
using OneDrive_Simple_Management_Tool.Models;
using OneDrive_Simple_Management_Tool.Models.DTO;
using OneDrive_Simple_Management_Tool.Pages;
using OneDrive_Simple_Management_Tool.Services;
using OneDrive_Simple_Management_Tool.ViewModels;
using OneDrive_Simple_Management_Tool.Views;
using OneDrive_Simple_Management_Tool.Views.Layout;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using Windows.Storage;

internal static partial class BookmarkUiProbe
{
    [STAThread]
    private static void Main()
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride =
            Environment.GetCommandLineArgs().Contains("--english") ? "en-US" : "zh-CN";
        Application.Start(_ =>
        {
            SynchronizationContext.SetSynchronizationContext(new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()));
            new ProbeApp();
        });
    }

    private sealed partial class ProbeApp : App
    {
        private string _output;
        protected override async void OnLaunched(LaunchActivatedEventArgs args)
        {
            _output = Path.Combine(AppContext.BaseDirectory, "probe-results", Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride);
            Directory.CreateDirectory(_output);
            MainWindow window = null;
            try
            {
                string root = Path.Combine(_output, "data", Guid.NewGuid().ToString("N"));
                var paths = new ApplicationDataPaths(root, Path.Combine(root, "application"));
                SafeDiagnostics.Configure(new SafeDiagnostics(paths.Diagnostics));
                var driveStore = new DriveConfigurationStore(paths);
                await driveStore.AddAsync(new DriveDTO { DisplayName = "Design archive · 项目资料", Provider = new() { HomeAccountId = "account-A", DriveId = "drive-A" } }, 0);
                await driveStore.AddAsync(new DriveDTO { DisplayName = "Work · 工作资料", Provider = new() { HomeAccountId = "account-B", DriveId = "drive-A" } }, 1);
                var store = new SwitchableStore(new BookmarkStore(paths));
                var settings = new SettingViewModel(new AppearanceSettingsStore(paths));
                await settings.InitializeAsync();
                var authentication = new Authentication();
                var resolver = new BookmarkResolver(driveStore, authentication, (auth, account) =>
                    new GraphServiceClient(new HttpClient(new Handler(account)), new BaseBearerTokenAuthenticationProvider(new AccountTokenProvider(auth, account))));
                Ioc.Default.ConfigureServices(new ServiceCollection().AddSingleton<IBookmarkStore>(store).AddSingleton<IBookmarkResolver>(resolver)
                    .AddSingleton(driveStore).AddSingleton<IAccountAuthenticationService>(authentication).AddTransient<BookmarkViewModel>()
                    .AddOfflineHome(paths).AddSingleton(settings)
                    .AddSingleton<IHomeDriveService>(new HomeDriveService(driveStore, authentication, (auth, account) =>
                        new GraphServiceClient(new HttpClient(new Handler(account)), new BaseBearerTokenAuthenticationProvider(new AccountTokenProvider(auth, account)))))
                    .BuildServiceProvider());
                window = new MainWindow { Title = "Bookmarks UI regression - LOCAL ONLY" };
                typeof(App).GetField("m_window", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, window);
                settings.AttachAppearance(p => ThemeHelper.Apply(window, p));
                // Install an in-memory Graph transport before DrivePage starts its real navigation code.
                window.Rootframe.Navigating += (_, e) =>
                {
                    var drive = e.Parameter is DriveNavigationRequest route ? route.Drive : e.Parameter as DriveViewModel;
                    if (drive == null) return;
                    drive.Provider.IsAuthenticated = true;
                    typeof(OneDrive).GetField("graphClient", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(drive.Provider,
                        new GraphServiceClient(new HttpClient(new Handler(drive.Provider.HomeAccountId)), new AnonymousAuthenticationProvider()));
                };
                ((FrameworkElement)window.Content).RequestedTheme = ElementTheme.Light;
                window.AppWindow.Resize(new SizeInt32(1280, 1060));
                window.Navigate(typeof(BookmarkPage));
                window.Activate();
                await ReadyBookmarks(window);
                var page = (BookmarkPage)window.Rootframe.Content;
                Assert(page.Model.IsEmpty, "Empty state");
                await Capture(window, "empty.png");

                var driveModel = new DriveViewModel(new OneDrive("drive-A", "account-A"), "Design archive · 项目资料");
                window.Navigate(typeof(DrivePage), driveModel);
                await Until(() => driveModel.IsComplete && window.Rootframe.Content is DrivePage drivePage && drivePage.IsLoaded);
                var owner = await Owner<ColumnFileView>(window, "file-A");
                await ClickBookmarkMenu(owner, "Bookmarks_Add");
                await UntilAsync(async () => (await store.LoadAsync()).Count == 1);
                Assert(driveModel.HasBookmarkMessage, "Save feedback");
                await ClickBookmarkMenu(owner, "Bookmarks_Remove");
                await UntilAsync(async () => (await store.LoadAsync()).Count == 0);
                await ClickBookmarkMenu(owner, "Bookmarks_Add");
                await UntilAsync(async () => (await store.LoadAsync()).Count == 1);

                driveModel.Layout = FileLayout.Grid;
                var gridOwner = await Owner<GirdFileView>(window, "folder-A");
                await ClickBookmarkMenu(gridOwner, "Bookmarks_Add");
                await UntilAsync(async () => (await store.LoadAsync()).Count == 2);
                await ClickBookmarkMenu(gridOwner, "Bookmarks_Remove");
                await UntilAsync(async () => (await store.LoadAsync()).Count == 1);
                await ClickBookmarkMenu(gridOwner, "Bookmarks_Add");
                await UntilAsync(async () => (await store.LoadAsync()).Count == 2);
                store.Fail = true;
                await FileActions.ExecuteAsync(FileAction.AddBookmark, gridOwner, driveModel.Files.Single(f => f.Id == "missing"));
                Assert(driveModel.HasError && !driveModel.HasBookmarkMessage && (await store.LoadAsync()).Count == 2, "Save failure never reports success");
                store.Fail = false;
                await store.AddAsync(Entry("missing", "Old reference · 失效示例"));
                await store.AddAsync(Entry("file-A", "Other account · 同名项目") with { AccountId = "account-B", DriveName = "Work · 工作资料" });

                window.Navigate(typeof(BookmarkPage));
                await ReadyBookmarks(window);
                page = (BookmarkPage)window.Rootframe.Content;
                var search = (TextBox)page.FindName("BookmarkSearch");
                search.Text = "Other account";
                await Until(() => page.Model.Items.Count == 1);
                await ClickRow(page, page.Model.Items[0], "Bookmarks_Open");
                await Until(() => window.Rootframe.Content is DrivePage drivePage && drivePage.DataContext is DriveViewModel vm && vm.IsComplete);
                var opened = (DriveViewModel)((DrivePage)window.Rootframe.Content).DataContext;
                Assert(opened.Provider.HomeAccountId == "account-B" && opened.SelectedItem?.Id == "file-A", "Account-specific file opening");
                Assert(opened.ParentItemId == "moved-parent" && opened.BreadcrumbItems.Last().Name == "Moved folder · 新位置", "Moved item breadcrumbs");
                await Task.Delay(250);
                var fileList = Descendants((DependencyObject)window.Rootframe.Content).OfType<ListView>().First();
                await Capture(window, "located-file.png");
                Assert(fileList.SelectedItem == opened.SelectedItem && fileList.ContainerFromItem(opened.SelectedItem) != null,
                    "Later page file selected and realized: selected=" + (fileList.SelectedItem as FileViewModel)?.Id + "; loaded=" + fileList.IsLoaded);
                var navigation = (NavigationView)((FrameworkElement)window.Content).FindName("nvSample");
                Assert((navigation.SelectedItem as NavigationViewItem)?.Tag as string == "CloudPage", "Files navigation selected");
                await store.RemoveAsync(Entry("file-A", "Other account") with { AccountId = "account-B" });

                window.Navigate(typeof(BookmarkPage));
                await ReadyBookmarks(window);
                page = (BookmarkPage)window.Rootframe.Content;
                await page.Model.OpenAsync(page.Model.Items.Single(i => i.Bookmark.ItemId == "folder-A"));
                await Until(() => window.Rootframe.Content is DrivePage drivePage && drivePage.DataContext is DriveViewModel vm && vm.IsComplete);
                opened = (DriveViewModel)((DrivePage)window.Rootframe.Content).DataContext;
                Assert(opened.ParentItemId == "folder-A" && opened.SelectedItem == null && opened.Files.Count == 1, "Folder opens its contents");

                window.Navigate(typeof(BookmarkPage));
                await ReadyBookmarks(window);
                page = (BookmarkPage)window.Rootframe.Content;
                await page.Model.OpenAsync(page.Model.Items.Single(i => i.Bookmark.ItemId == "missing"));
                Assert(ReferenceEquals(window.Rootframe.Content, page) && page.Model.Items.Single(i => i.Bookmark.ItemId == "missing").HasError, "Deleted item stays with actionable error");
                await Capture(window, "light.png");
                ((FrameworkElement)window.Content).RequestedTheme = ElementTheme.Dark;
                await Capture(window, "dark.png");
                ((FrameworkElement)window.Content).RequestedTheme = ElementTheme.Light;
                window.AppWindow.Resize(new SizeInt32(620, 820));
                navigation.IsPaneOpen = false;
                search = (TextBox)page.FindName("BookmarkSearch");
                search.Text = "失效";
                await Until(() => page.Model.Items.Count == 1);
                await Capture(window, "narrow-error.png");
                await ClickRow(page, page.Model.Items[0], "Bookmarks_RemoveButton");
                await Until(() => !page.Model.IsBusy && page.Model.HasNoMatches);
                Assert((await store.LoadAsync()).Count == 2, "Remove affects only local bookmark");
                search.Text = "";
                Assert(page.Model.Items.Count == 2, "Filter clears");
                // A fresh store reads the same encrypted fictional data after page recreation.
                Assert((await new BookmarkStore(paths).LoadAsync()).Count == 2, "Persistence across store instances");

                await File.WriteAllTextAsync(paths.Bookmarks, "fictional damage");
                await page.Model.ReloadCommand.ExecuteAsync(null);
                Assert(page.Model.NeedsRecovery && !page.Model.CanUseItems && !page.Model.IsEmpty, "Corruption is not empty");
                await Capture(window, "configuration-error.png");
                var disabledActions = BookmarkButtons(page).ToArray();
                Assert(disabledActions.Length > 0 && disabledActions.All(b => !b.IsEnabled), "Corrupt-data row buttons disabled");
                await page.Model.RestoreBackupCommand.ExecuteAsync(null);
                Assert(!page.Model.HasError && !page.Model.NeedsRecovery && page.Model.Items.Count > 0, "Backup recovery");
                await VerifyIntegration(window, store, settings, paths);
                await File.WriteAllTextAsync(Path.Combine(_output, "result.txt"),
                    "PASS: empty; list/grid add/remove menus; save failure; account isolation; file rename/move and later-page selection; folder opening; missing item; filtering and local removal; fresh store; encrypted data recovery; Home/Files/bookmark loop; theme across three pages; long names; automatic pane; short viewport; recovery dialog cancel/rebuild; keyboard focus. No real cloud requests.");
            }
            catch (Exception exception)
            {
                await File.WriteAllTextAsync(Path.Combine(_output, "result.txt"), "FAIL: " + exception);
                Environment.ExitCode = 1;
            }
            finally { window?.Close(); }
        }

        private async Task Capture(MainWindow window, string name, UIElement surface = null)
        {
            await Task.Delay(400);
            var bitmap = new RenderTargetBitmap();
            await bitmap.RenderAsync(surface ?? (UIElement)window.Content);
            var pixels = await bitmap.GetPixelsAsync();
            string path = Path.Combine(_output, name);
            File.WriteAllBytes(path, Array.Empty<byte>());
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, pixels.ToArray());
            await encoder.FlushAsync();
        }
    }

    private static Bookmark Entry(string item, string name) => new()
    {
        AccountId = "account-A", DriveId = "drive-A", ItemId = item, Name = name, DriveName = "Design archive · 项目资料",
        AddedAt = DateTimeOffset.UtcNow, IsFolder = item == "folder-A"
    };
    private static Task ReadyBookmarks(MainWindow window) => Until(() => window.Rootframe.Content is BookmarkPage page && page.IsLoaded && !page.Model.IsBusy);
    private static async Task<T> Owner<T>(MainWindow window, string id) where T : UserControl
    {
        await Until(() => Descendants((DependencyObject)window.Rootframe.Content).OfType<T>().Any(c => c.IsLoaded && c.DataContext is FileViewModel f && f.Id == id));
        return Descendants((DependencyObject)window.Rootframe.Content).OfType<T>().First(c => c.DataContext is FileViewModel f && f.Id == id);
    }
    private static async Task ClickBookmarkMenu(FrameworkElement owner, string key)
    {
        var menu = (MenuFlyout)owner.ContextFlyout;
        var opened = new TaskCompletionSource();
        var closed = new TaskCompletionSource();
        menu.Opened += (_, _) => opened.TrySetResult();
        menu.Closed += (_, _) => closed.TrySetResult();
        menu.ShowAt(owner);
        await opened.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Until(() => menu.Items.OfType<MenuFlyoutItem>().Any(i => i.Text == key.GetLocalized() && i.IsEnabled));
        var item = menu.Items.OfType<MenuFlyoutItem>().Single(i => i.Text == key.GetLocalized());
        ((IInvokeProvider)new MenuFlyoutItemAutomationPeer(item).GetPattern(PatternInterface.Invoke)).Invoke();
        menu.Hide();
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }
    private static async Task ClickRow(BookmarkPage page, BookmarkItemViewModel item, string uid)
    {
        var list = (ListView)page.FindName("BookmarkList");
        list.ScrollIntoView(item);
        await Until(() => Descendants(list).OfType<Button>().Any(b => b.DataContext == item && b.Content?.ToString() == (uid + "/Content").GetLocalized()));
        var button = Descendants(list).OfType<Button>().First(b => b.DataContext == item && b.Content?.ToString() == (uid + "/Content").GetLocalized());
        ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
    }
    private static async Task Until(Func<bool> ready)
    {
        for (int attempt = 0; attempt < 200 && !ready(); attempt++) await Task.Delay(25);
        Assert(ready(), "UI condition timed out");
    }
    private static async Task UntilAsync(Func<Task<bool>> ready)
    {
        for (int attempt = 0; attempt < 200; attempt++) { if (await ready()) return; await Task.Delay(25); }
        throw new InvalidOperationException("Async condition timed out");
    }
    private static void Assert(bool condition, string step) { if (!condition) throw new InvalidOperationException(step); }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    private sealed class SwitchableStore(IBookmarkStore inner) : IBookmarkStore
    {
        public bool Fail;
        public Task<IReadOnlyList<Bookmark>> LoadAsync(CancellationToken token = default) => inner.LoadAsync(token);
        public Task AddAsync(Bookmark b, CancellationToken token = default) => Fail ? throw new IOException() : inner.AddAsync(b, token);
        public Task RemoveAsync(Bookmark b, CancellationToken token = default) => Fail ? throw new IOException() : inner.RemoveAsync(b, token);
        public Task UpdateMetadataAsync(Bookmark b, CancellationToken token = default) => inner.UpdateMetadataAsync(b, token);
        public Task RestoreBackupAsync(CancellationToken token = default) => inner.RestoreBackupAsync(token);
        public Task RebuildAsync(CancellationToken token = default) => inner.RebuildAsync(token);
    }
    private sealed class Authentication : IAccountAuthenticationService
    {
        public Task<AccountToken> AcquireSilentAsync(string account, CancellationToken token) => Task.FromResult(new AccountToken(account, "fictional-token"));
        public Task<AccountToken> AcquireInteractiveAsync(string expectedAccountId, CancellationToken token) => throw new Exception("No interactive login in this probe");
    }
    private sealed class Handler(string account) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (request.Method != HttpMethod.Get) throw new InvalidOperationException("Bookmarks must not mutate cloud data");
            string path = request.RequestUri.AbsolutePath;
            object result;
            if (path.EndsWith("/drives/drive-A")) result = new { id = "drive-A", quota = new { total = 107374182400L, used = 40265318400L, remaining = 67108864000L } };
            else if (path.EndsWith("/root")) result = new { id = "root", folder = new { }, root = new { } };
            else if (path.EndsWith("/items/missing")) return Task.FromResult(Json(new { error = new { code = "itemNotFound", message = "fictional missing item" } }, HttpStatusCode.NotFound));
            else if (path.EndsWith("/items/file-A")) result = new { id = "file-A", name = "Design notes · 已重命名.md", file = new { }, parentReference = new { id = "moved-parent", driveId = "drive-A" } };
            else if (path.EndsWith("/items/folder-A") || path.EndsWith("/items/moved-parent"))
            {
                string id = path.Split('/').Last();
                result = new { id, name = id == "folder-A" ? "Reference · 参考资料" : "Moved folder · 新位置", folder = new { }, parentReference = new { id = "root", driveId = "drive-A" } };
            }
            else if (path.Contains("/items/moved-parent/children"))
            {
                bool later = request.RequestUri.Query.Contains("page=2");
                var rows = Enumerable.Range(later ? 30 : 0, 30).Select(i => Row("item-" + i, "Document " + i + ".txt")).ToList();
                if (later) rows.Add(Row("file-A", "Design notes · 已重命名.md"));
                result = new Dictionary<string, object> { ["value"] = rows, ["@odata.nextLink"] = later ? null : "https://graph.microsoft.com/v1.0/drives/drive-A/items/moved-parent/children?page=2" };
            }
            else if (path.Contains("/items/folder-A/children")) result = new { value = new[] { Row("child", "Inside folder.txt") } };
            else if (path.EndsWith("/children")) result = new { value = new[] { Row("file-A", "Design notes · 设计说明.md"), Row("folder-A", "Reference · 参考资料", true), Row("missing", "Old reference · 失效示例") } };
            else throw new InvalidOperationException("Unexpected test URL: " + path + " (" + account + ")");
            return Task.FromResult(Json(result));
        }
        private static object Row(string id, string name, bool folder = false) => new { id, name, file = folder ? null : new { mimeType = "text/plain" }, folder = folder ? new { childCount = 1 } : null };
        private static HttpResponseMessage Json(object value, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    }
}
