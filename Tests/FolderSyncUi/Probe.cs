using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using OneDrive_Simple_Management_Tool;
using OneDrive_Simple_Management_Tool.Models;
using OneDrive_Simple_Management_Tool.Services;
using OneDrive_Simple_Management_Tool.ViewModels;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

internal static class FolderSyncUiProbe
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
            string directory = Path.Combine(AppContext.BaseDirectory, "probe-data");
            Directory.CreateDirectory(Path.Combine(directory, "项目资料", "空目录"));
            Directory.CreateDirectory(Path.Combine(directory, "照片归档"));
            await File.WriteAllTextAsync(Path.Combine(directory, "项目资料", "说明.txt"), "Local sync UI test");
            var service = new FolderSyncService(new FolderSyncStore(Path.Combine(directory, "state", Guid.NewGuid().ToString("N"))),
                _ => new Target());
            var paths = new ApplicationDataPaths(Path.Combine(directory, "configuration", Guid.NewGuid().ToString("N")), directory);
            var drives = new DriveConfigurationStore(paths);
            await drives.AddAsync(new OneDrive_Simple_Management_Tool.Models.DTO.DriveDTO
            {
                DisplayName = "本地模拟网盘", Provider = new() { HomeAccountId = "fake-account", DriveId = "fake-drive" }
            }, 0);
            Ioc.Default.ConfigureServices(new ServiceCollection().AddSingleton(service).AddSingleton<FolderSyncViewModel>()
                .AddSingleton(drives).AddSingleton<TaskManagerViewModel>().AddOfflineHome(paths).BuildServiceProvider());
            await service.AddAsync(Binding("项目资料", "folder", true));
            await service.AddAsync(Binding("照片归档", "photos", false));
            var window = new MainWindow { Title = "Folder sync UI test — LOCAL ONLY" };
            typeof(App).GetField("m_window", BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, window);
            window.Activate();
            window.Closed += (_, _) => service.Stop();

            FolderSyncBinding Binding(string name, string id, bool enabled) => new()
            {
                LocalPath = Path.Combine(directory, name), FolderName = name,
                DriveId = "fake-drive", AccountId = "fake-account", DriveName = "本地模拟网盘",
                RemoteFolderId = id, RemotePath = "/" + name, RemoteAncestorIds = new() { "root" }, Enabled = enabled
            };
        }
    }

    private sealed class Target : IFolderSyncTarget, IFolderSyncBrowser
    {
        public Task ValidateRootAsync(CancellationToken token) => Task.CompletedTask;
        public Task EnsureFolderAsync(string path, CancellationToken token) => Task.CompletedTask;
        public async Task UploadAsync(string path, Stream content, IProgress<long> progress, CancellationToken token)
        {
            await content.CopyToAsync(Stream.Null, token);
            progress?.Report(content.Length);
        }
        public Task<FolderSyncRemoteFolder> GetRootAsync(CancellationToken token) => Task.FromResult(new FolderSyncRemoteFolder("root", "root"));
        public Task<IReadOnlyList<FolderSyncRemoteFolder>> BrowseAsync(string id, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<FolderSyncRemoteFolder>>(id == "root"
                ? new[] { new FolderSyncRemoteFolder("folder", "项目资料"), new FolderSyncRemoteFolder("photos", "照片归档"), new FolderSyncRemoteFolder("new", "同步测试") }
                : Array.Empty<FolderSyncRemoteFolder>());
        public void Dispose() { }
    }
}
