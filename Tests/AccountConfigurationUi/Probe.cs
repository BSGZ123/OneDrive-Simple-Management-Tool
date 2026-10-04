using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Graph;
using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OneDrive_Simple_Management_Tool;
using OneDrive_Simple_Management_Tool.Models.DTO;
using OneDrive_Simple_Management_Tool.Pages;
using OneDrive_Simple_Management_Tool.Services;
using OneDrive_Simple_Management_Tool.ViewModels;
using OneDrive_Simple_Management_Tool.Views;
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

internal static class AccountConfigurationUiProbe
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
        protected override async void OnLaunched(LaunchActivatedEventArgs args)
        {
            var root = Path.Combine(AppContext.BaseDirectory, "probe-data", Guid.NewGuid().ToString("N"));
            var paths = new ApplicationDataPaths(root, Path.Combine(root, "application"));
            var protector = new Protector();
            var store = new DriveConfigurationStore(paths, protector);
            SafeDiagnostics.Configure(new SafeDiagnostics(paths.Diagnostics));
            await store.AddAsync(Record("A"), 0);
            await store.AddAsync(Record("B"), 1);
            Ioc.Default.ConfigureServices(new ServiceCollection().AddSingleton(store).AddSingleton<TaskManagerViewModel>().AddOfflineHome(paths).BuildServiceProvider());
            var model = new CloudViewModel(store, CreateDrive);
            await model.LoadDrivesFromDisk();
            var window = new MainWindow { Title = "Account configuration UI test - LOCAL ONLY" };
            typeof(App).GetField("m_window", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, window);
            window.Rootframe.Navigated += (_, e) => { if (e.Content is CloudPage page) page.DataContext = model; };
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Padding = new Thickness(12) };
            panel.Children.Add(new TextBlock { Text = "LOCAL fictional data", VerticalAlignment = VerticalAlignment.Center });
            var fail = new CheckBox { Content = "Reject saves" };
            fail.Checked += (_, _) => protector.Fail = true;
            fail.Unchecked += (_, _) => protector.Fail = false;
            panel.Children.Add(fail);
            var add = new Button { Content = "Test add dialog" };
            add.Click += async (_, _) =>
            {
                var vm = new CreateDriveViewModel(model, async token =>
                {
                    await Task.Delay(1200, token);
                    return CreateDrive(Record("C")).Provider;
                });
                await new CreateDrive { XamlRoot = panel.XamlRoot, DataContext = vm }.ShowAsync();
            };
            panel.Children.Add(add);
            var corrupt = new Button { Content = "Simulate damaged config" };
            corrupt.Click += async (_, _) => { await File.WriteAllTextAsync(paths.Drives, "fictional corruption"); await model.LoadDrivesFromDisk(); };
            panel.Children.Add(corrupt);
            var grid = (Grid)window.Content;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(panel, 2);
            grid.Children.Add(panel);
            window.Activate();
            window.Navigate(typeof(CloudPage));
        }
    }

    private static DriveDTO Record(string id) => new() { DisplayName = "同名网盘", Provider = new() { HomeAccountId = "fake-account-" + id, DriveId = "fake-drive-" + id } };
    private static DriveViewModel CreateDrive(DriveDTO record)
    {
        var provider = new OneDrive(record.Provider.DriveId, record.Provider.HomeAccountId, null) { IsAuthenticated = true };
        var client = new GraphServiceClient(new HttpClient(new Handler(record.Provider.DriveId)), new AnonymousAuthenticationProvider());
        typeof(OneDrive).GetField("graphClient", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(provider, client);
        return new DriveViewModel(provider, record.DisplayName);
    }

    private sealed class Handler(string drive) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"value\":[{\"id\":\"local-file\",\"name\":\"Selected " + drive + ".txt\",\"file\":{},\"size\":42}]}",
                    System.Text.Encoding.UTF8, "application/json")
            });
    }

    private sealed class Protector : IConfigurationProtector
    {
        public bool Fail;
        public byte[] Protect(byte[] bytes, string purpose) => Fail ? throw new CryptographicException() : new WindowsConfigurationProtector().Protect(bytes, purpose);
        public byte[] Unprotect(byte[] bytes, string purpose) => new WindowsConfigurationProtector().Unprotect(bytes, purpose);
    }
}
