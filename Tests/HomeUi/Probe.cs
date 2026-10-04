using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using OneDrive_Simple_Management_Tool;
using OneDrive_Simple_Management_Tool.Models;
using OneDrive_Simple_Management_Tool.Pages;
using OneDrive_Simple_Management_Tool.Services;
using OneDrive_Simple_Management_Tool.ViewModels;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using Windows.Storage;

internal static class HomeUiProbe
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

    private sealed class ProbeApp : App
    {
        private string _output;
        protected override async void OnLaunched(LaunchActivatedEventArgs args)
        {
            _output = Path.Combine(AppContext.BaseDirectory, "probe-results",
                Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride);
            Directory.CreateDirectory(_output);
            MainWindow window = null;
            try
            {
                var root = Path.Combine(_output, "data", Guid.NewGuid().ToString("N"));
                var paths = new ApplicationDataPaths(root, Path.Combine(root, "application"));
                Directory.CreateDirectory(root);
                SafeDiagnostics.Configure(new SafeDiagnostics(paths.Diagnostics));
                var drives = new Drives();
                var tasks = new TaskManagerViewModel();
                var service = new FolderSyncService(new FolderSyncStore(paths.FolderSync), _ => throw new Exception("No live sync in this probe"));
                await service.InitializeAsync();
                var sync = new FolderSyncViewModel(service);
                var settings = new SettingViewModel(new AppearanceSettingsStore(paths));
                await settings.InitializeAsync();
                var authentication = new NoNetworkAuthentication();
                Ioc.Default.ConfigureServices(new ServiceCollection().AddSingleton<IHomeDriveService>(drives)
                    .AddSingleton(tasks).AddSingleton(service).AddSingleton(sync).AddSingleton(settings)
                    .AddSingleton(new DriveConfigurationStore(paths)).AddSingleton<IAccountAuthenticationService>(authentication)
                    .AddTransient<HomeViewModel>().BuildServiceProvider());
                window = new MainWindow { Title = "Home UI regression - LOCAL ONLY" };
                typeof(App).GetField("m_window", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, window);
                ((FrameworkElement)window.Content).RequestedTheme = ElementTheme.Light;
                window.AppWindow.Resize(new SizeInt32(1280, 1060));
                // Startup can route to Files for recovery before the first selection event.
                window.Navigate(typeof(CloudPage));
                window.Activate();
                var navigation = (NavigationView)((FrameworkElement)window.Content).FindName("nvSample");
                await Until(() => navigation.IsLoaded && window.Rootframe.Content is CloudPage cloud && cloud.IsLoaded);
                await Task.Delay(100);
                Assert(window.Rootframe.Content is CloudPage, "Startup recovery route preserved");
                navigation.SelectedItem = navigation.MenuItems[0];
                await Until(() => window.Rootframe.Content is HomePage home && home.IsLoaded && !home.Model.IsLoading);
                var page = (HomePage)window.Rootframe.Content;
                Assert(page.Model.IsEmpty && page.Model.HasNoTransfers && page.Model.HasNoSyncs, "Empty states");
                await Capture(window, "empty.png");

                // Real view models, with state set directly; no worker or network operation starts.
                await File.WriteAllTextAsync(Path.Combine(root, "sample.txt"), "Local UI fixture");
                var file = await StorageFile.GetFileFromPathAsync(Path.Combine(root, "sample.txt"));
                var provider = new OneDrive("fictional-drive", "fictional-account", authentication);
                var drive = new DriveViewModel(provider, "Example drive");
                var upload = new UploadTaskViewModel(drive, "root", file) { State = UploadTaskState.Uploading };
                tasks.UploadTasks.Add(upload);
                tasks.UploadTasks.Add(new UploadTaskViewModel(drive, "root", file) { State = UploadTaskState.Failed });
                tasks.DownloadTasks.Add(new DownloadTaskViewModel(drive, "file", file) { State = DownloadTaskState.Paused });
                tasks.DownloadTasks.Add(new DownloadTaskViewModel(drive, "file", file) { State = DownloadTaskState.Completed });
                Assert(page.Model.ActiveTransfers == 1 && page.Model.FailedTransfers == 1 && page.Model.WaitingTransfers == 1, "Live task collection updates");
                upload.State = UploadTaskState.Completed;
                Assert(page.Model.ActiveTransfers == 0 && page.Model.CompletedTransfers == 2, "Live task state updates");
                upload.State = UploadTaskState.Uploading;
                var job = new FolderSyncJob(new FolderSyncBinding { FolderName = "Local test", Enabled = false },
                    new FolderSyncStore(paths.FolderSync), _ => throw new Exception("No target"));
                ((List<FolderSyncJob>)typeof(FolderSyncService).GetField("_jobs", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(service)).Add(job);
                await sync.InitializeAsync();
                Assert(page.Model.SyncCount == 1 && page.Model.PausedSyncs == 1, "Real sync model observed");

                drives.Populated = true;
                await page.Model.RefreshCommand.ExecuteAsync(null);
                Assert(page.Model.Drives.Count == 2 && page.Model.Drives[0].UsagePercent == 37.5 && page.Model.Drives[1].HasError, "Mixed quota outcomes");
                await Task.Delay(250);
                Assert(((TextBlock)page.FindName("ActiveTransferCount")).Text == "1", "Count reaches XAML binding");
                Assert(Grid.GetRow((FrameworkElement)page.FindName("SyncCard")) == 0, "Wide cards sit side by side");
                await Capture(window, "light.png");
                ((FrameworkElement)window.Content).RequestedTheme = ElementTheme.Dark;
                await Task.Delay(150);
                await Capture(window, "dark.png");

                foreach (var destination in new[]
                {
                    ("FilesShortcut", typeof(CloudPage), "CloudPage"),
                    ("TasksShortcut", typeof(TaskManagerPage), "TaskManagerPage"),
                    ("SyncShortcut", typeof(FolderSyncPage), "FolderSyncPage"),
                    ("SettingsShortcut", typeof(SettingPage), "Settings")
                })
                {
                    page = (HomePage)window.Rootframe.Content;
                    int oldCount = page.Model.ActiveTransfers;
                    Invoke((Button)page.FindName(destination.Item1));
                    await Until(() => window.Rootframe.Content?.GetType() == destination.Item2);
                    Assert(destination.Item3 == "Settings" ? navigation.SelectedItem == navigation.SettingsItem :
                        (navigation.SelectedItem as NavigationViewItem)?.Tag as string == destination.Item3, "Shortcut matches sidebar");
                    await Task.Delay(100);
                    upload.State = UploadTaskState.Completed;
                    Assert(page.Model.ActiveTransfers == oldCount, "Old page unsubscribed");
                    upload.State = UploadTaskState.Uploading;
                    window.Navigate(typeof(HomePage));
                    await Until(() => window.Rootframe.Content is HomePage next && next.IsLoaded && !next.Model.IsLoading);
                    Assert((navigation.SelectedItem as NavigationViewItem)?.Tag as string == "HomePage", "Return selection");
                }

                page = (HomePage)window.Rootframe.Content;
                // The real DrivePage may only request silent auth; the fake always rejects before networking.
                var cards = (ItemsControl)page.FindName("DriveCards");
                var open = Descendants(cards).OfType<Button>().First(b => b.DataContext is HomeDriveViewModel);
                Invoke(open);
                await Until(() => window.Rootframe.Content is DrivePage);
                Assert((navigation.SelectedItem as NavigationViewItem)?.Tag as string == "CloudPage", "Drive details select Files");
                var opened = ((DrivePage)window.Rootframe.Content).DataContext as DriveViewModel;
                Assert(opened.Provider.HomeAccountId == "account-A" && opened.Provider.DriveId == "drive-A", "Correct drive identity");
                window.Navigate(typeof(HomePage));
                await Until(() => window.Rootframe.Content is HomePage next && next.IsLoaded && !next.Model.IsLoading);
                page = (HomePage)window.Rootframe.Content;
                ((FrameworkElement)window.Content).RequestedTheme = ElementTheme.Light;
                window.AppWindow.Resize(new SizeInt32(620, 820));
                navigation.IsPaneOpen = false;
                await Task.Delay(250);
                Assert(Grid.GetRow((FrameworkElement)page.FindName("SyncCard")) == 1, "Narrow cards stack");
                await Capture(window, "narrow.png");
                var scroll = (ScrollViewer)page.FindName("HomeScroll");
                scroll.ChangeView(null, scroll.ScrollableHeight, null, true);
                await Task.Delay(200);
                await Capture(window, "narrow-drives.png");

                drives.Broken = true;
                await page.Model.RefreshCommand.ExecuteAsync(null);
                Assert(page.Model.HasError && !page.Model.IsEmpty, "Config error is not empty state");
                await Task.Delay(100);
                await Capture(window, "configuration-error.png");
                drives.Broken = false;
                await page.Model.RefreshCommand.ExecuteAsync(null);
                Assert(!page.Model.HasError && !page.Model.IsLoading, "Retry recovery");
                await File.WriteAllTextAsync(Path.Combine(_output, "result.txt"),
                    "PASS: startup recovery route; empty states; real transfer and sync model updates; mixed quotas; light/dark; four shortcut destinations and sidebar; old page unsubscribes; account-specific drive opening; narrow layout; config failure/retry. No real account/network used.");
            }
            catch (Exception exception)
            {
                await File.WriteAllTextAsync(Path.Combine(_output, "result.txt"), "FAIL: " + exception);
                Environment.ExitCode = 1;
            }
            finally { window?.Close(); }
        }

        private async Task Capture(MainWindow window, string name)
        {
            // Navigated/Loaded can precede completion of the outgoing page's transition.
            await Task.Delay(400);
            var bitmap = new RenderTargetBitmap();
            await bitmap.RenderAsync((UIElement)window.Content);
            var pixels = await bitmap.GetPixelsAsync();
            string path = Path.Combine(_output, name);
            File.WriteAllBytes(path, Array.Empty<byte>());
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, pixels.ToArray());
            await encoder.FlushAsync();
        }
    }

    private static void Invoke(Button button) =>
        ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
    private static void Assert(bool condition, string step) { if (!condition) throw new InvalidOperationException(step); }
    private static async Task Until(Func<bool> ready)
    {
        for (int attempt = 0; attempt < 200 && !ready(); attempt++) await Task.Delay(25);
        Assert(ready(), "UI initialization timed out");
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    private sealed class Drives : IHomeDriveService
    {
        public bool Populated, Broken;
        public Task<IReadOnlyList<HomeDrive>> LoadDrivesAsync(CancellationToken token)
        {
            if (Broken) throw new HomeOverviewException("Home_ConfigurationFailed");
            return Task.FromResult<IReadOnlyList<HomeDrive>>(Populated ? new[]
            {
                new HomeDrive("account-A", "drive-A", "Design archive · 项目资料"),
                new HomeDrive("account-B", "drive-B", "Personal · 日常文件")
            } : Array.Empty<HomeDrive>());
        }
        public async Task<HomeQuota> GetQuotaAsync(HomeDrive drive, CancellationToken token)
        {
            await Task.Delay(100, token);
            if (drive.DriveId == "drive-B") throw new HomeOverviewException("Home_SignInRequired");
            return new HomeQuota(100L << 30, 75L << 29, 125L << 29);
        }
    }
    private sealed class NoNetworkAuthentication : IAccountAuthenticationService
    {
        public Task<AccountToken> AcquireSilentAsync(string accountId, CancellationToken token) =>
            throw new AccountAuthenticationException(AuthenticationFailure.RequiresSignIn);
        public Task<AccountToken> AcquireInteractiveAsync(string expectedAccountId, CancellationToken token) =>
            throw new Exception("Interactive auth must not run in a Home probe");
    }
}
