using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OneDrive_Simple_Management_Tool;
using OneDrive_Simple_Management_Tool.Pages;
using OneDrive_Simple_Management_Tool.Tests.UploadRegression;
using OneDrive_Simple_Management_Tool.ViewModels;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;

// Opt-in only. Real WinUI/storage/SDK, in-memory metadata and a loopback upload server.
internal static class UploadUiProbe
{
    [STAThread]
    private static void Main()
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(_ =>
        {
            SynchronizationContext.SetSynchronizationContext(new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()));
            // Unpackaged language overrides apply only to this test process.
            string languagePath = Path.Combine(AppContext.BaseDirectory, "UploadProbeLanguage.txt");
            Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride =
                File.Exists(languagePath) ? File.ReadAllText(languagePath).Trim() : "zh-CN";
            new ProbeApp();
        });
    }

    private sealed class ProbeApp : App
    {
        private Window _window;
        private readonly TestServices _services = new();
        private readonly List<UploadFixture> _fixtures = new();
        private readonly List<Task> _runs = new();
        private TextBlock _summary;

        protected override async void OnLaunched(LaunchActivatedEventArgs args)
        {
            Ioc.Default.ConfigureServices(_services);
            var panel = new Grid { Padding = new Thickness(20), RowSpacing = 12 };
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            _summary = new TextBlock { Text = "LOCAL TEST — loopback only. Cancel each upload; cleanup takes 4 seconds.", TextWrapping = TextWrapping.Wrap };
            panel.Children.Add(_summary);
            var page = new TaskManagerPage();
            ((Pivot)((Grid)page.Content).Children[0]).SelectedIndex = 1;
            Grid.SetRow(page, 1);
            panel.Children.Add(page);
            _window = new Window { Title = "Upload cancellation UI regression — LOCAL", Content = panel };
            _window.Closed += async (_, _) =>
            {
                foreach (var task in _services.Manager.UploadTasks.ToArray()) await task.CancelTaskAsync();
                await Task.WhenAll(_runs);
                foreach (var fixture in _fixtures) await fixture.DisposeAsync();
            };
            _window.Activate();

            string root = Path.Combine(AppContext.BaseDirectory, "UploadProbeData");
            Directory.CreateDirectory(Path.Combine(root, "Nested folder", "Child"));
            CreateFile(Path.Combine(root, "Large file.bin"), 16 * 1024 * 1024);
            CreateFile(Path.Combine(root, "Nested folder", "Root file.bin"), 1024 * 1024);
            CreateFile(Path.Combine(root, "Nested folder", "Child", "Child file.bin"), 1024 * 1024);
            AddTask(await StorageFile.GetFileFromPathAsync(Path.Combine(root, "Large file.bin")));
            AddTask(await StorageFolder.GetFolderFromPathAsync(Path.Combine(root, "Nested folder")));
            _services.Manager.UploadTasks.CollectionChanged += (_, _) =>
            {
                _summary.Text = $"LOCAL TEST — remaining: {_services.Manager.UploadTasks.Count}; session cleanup requests: {_fixtures.Sum(f => f.Server.Deletes.Count)}";
            };
        }

        private void AddTask(IStorageItem item)
        {
            var fixture = new UploadFixture();
            _fixtures.Add(fixture);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Server.AllowDisconnects = true;
            fixture.Server.BeforeSliceResponse = token => release.Task.WaitAsync(token);
            fixture.Server.BeforeDeleteResponse = async token =>
            {
                await Task.Delay(TimeSpan.FromSeconds(4), token);
                release.TrySetResult();
            };
            _runs.Add(_services.Manager.AddUploadTask(new DriveViewModel(fixture.Provider, "Local upload test"), "root", item));
        }

        private static void CreateFile(string path, long length)
        {
            using var stream = File.Create(path);
            stream.SetLength(length);
        }
    }

    private sealed class TestServices : IServiceProvider
    {
        public TaskManagerViewModel Manager { get; } = new();
        public object GetService(Type serviceType) => serviceType == typeof(TaskManagerViewModel) ? Manager : null;
    }
}
