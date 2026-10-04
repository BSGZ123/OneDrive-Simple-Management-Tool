using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.WinUI.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using OneDrive_Simple_Management_Tool;
using OneDrive_Simple_Management_Tool.Helpers;
using OneDrive_Simple_Management_Tool.Models;
using OneDrive_Simple_Management_Tool.Pages;
using OneDrive_Simple_Management_Tool.Services;
using OneDrive_Simple_Management_Tool.ViewModels;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using Windows.Storage;

internal static class SettingsUiProbe
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
                var paths = new ApplicationDataPaths(Path.Combine(_output, "data", Guid.NewGuid().ToString("N")));
                var store = new SwitchableStore(new AppearanceSettingsStore(paths));
                var vm = new SettingViewModel(store);
                await vm.InitializeAsync();
                Ioc.Default.ConfigureServices(new ServiceCollection().AddSingleton(vm).AddOfflineHome(paths).BuildServiceProvider());
                SafeDiagnostics.Configure(new SafeDiagnostics(paths.Diagnostics));
                window = new MainWindow { Title = "Settings UI regression - LOCAL ONLY" };
                typeof(App).GetField("m_window", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, window);
                vm.AttachAppearance(p => ThemeHelper.Apply(window, p));
                window.AppWindow.Resize(new SizeInt32(1100, 850));
                window.Activate();
                var navigation = (NavigationView)((FrameworkElement)window.Content).FindName("nvSample");
                await WaitForAsync(() => navigation.IsLoaded && navigation.SettingsItem != null &&
                    window.Rootframe.Content is HomePage);
                navigation.SelectedItem = navigation.SettingsItem;
                await Task.Delay(500);
                var page = (SettingPage)window.Rootframe.Content;
                var theme = (ComboBox)page.FindName("ThemeMode");
                var material = (ComboBox)page.FindName("MaterialMode");
                Assert(theme.SelectedIndex == 0 && material.SelectedIndex == 0, "Initial combo selection");
                theme.SelectedIndex = 2;
                await vm.FlushAsync();
                await Task.Delay(150);
                Assert(((FrameworkElement)window.Content).ActualTheme == ElementTheme.Dark, "Dark theme applied");
                await Capture(window, "dark.png");

                // Check the actual two-way UI bindings and all supported/fallback backdrop branches.
                foreach (int index in new[] { 1, 2, 3, 0 })
                {
                    material.SelectedIndex = index;
                    await vm.FlushAsync();
                    await Task.Delay(100);
                    Assert(vm.MaterialIndex == index, "Material binding");
                    bool available = index switch
                    {
                        1 or 2 => Microsoft.UI.Composition.SystemBackdrops.MicaController.IsSupported(),
                        3 => Microsoft.UI.Composition.SystemBackdrops.DesktopAcrylicController.IsSupported(),
                        _ => false
                    };
                    Assert((window.SystemBackdrop != null) == available, "Backdrop support");
                    Assert(((FrameworkElement)window.Content).FindName("FallbackBackground") is Border fallback &&
                        (fallback.Visibility == Visibility.Visible) == !available, "Plain background restored");
                }

                theme.SelectedIndex = 1;
                await vm.FlushAsync();
                await Task.Delay(150);
                Assert(((FrameworkElement)window.Content).ActualTheme == ElementTheme.Light, "Light theme applied");
                await Capture(window, "light.png");

                store.Fail = true;
                material.SelectedIndex = 2;
                await vm.FlushAsync();
                await Task.Delay(100);
                Assert(vm.HasError && vm.RetrySaveCommand.CanExecute(null), "Save error and retry");
                await Capture(window, "save-error.png");
                store.Fail = false;
                await vm.RetrySaveCommand.ExecuteAsync(null);
                Assert(!vm.HasError, "Save retry completed");

                navigation.SelectedItem = navigation.MenuItems[0];
                navigation.SelectedItem = navigation.SettingsItem;
                await Task.Delay(200);
                page = (SettingPage)window.Rootframe.Content;
                Assert(((ComboBox)page.FindName("ThemeMode")).SelectedIndex == 1 &&
                    ((ComboBox)page.FindName("MaterialMode")).SelectedIndex == 2, "Navigation preserves choices");

                // A fresh model/store simulates restoration, without touching the user's settings.
                var restored = new SettingViewModel(new AppearanceSettingsStore(paths));
                await restored.InitializeAsync();
                Assert(restored.ThemeIndex == 1 && restored.MaterialIndex == 2, "Restart restores preferences");
                restored.AttachAppearance(p => ThemeHelper.Apply(window, p));

                // None makes PNG output show the same plain background as the actual window.
                vm.MaterialIndex = 0;
                await vm.FlushAsync();
                window.AppWindow.Resize(new SizeInt32(620, 800));
                ((NavigationView)((FrameworkElement)window.Content).FindName("nvSample")).IsPaneOpen = false;
                await Task.Delay(200);
                await Capture(window, "narrow.png");
                foreach (var expander in Descendants(page).OfType<SettingsExpander>()) expander.IsExpanded = true;
                var scroller = Descendants(page).OfType<ScrollViewer>().First();
                await Task.Delay(100);
                scroller.ChangeView(null, scroller.ScrollableHeight, null, true);
                await Task.Delay(100);
                await Capture(window, "narrow-about.png");
                theme = (ComboBox)page.FindName("ThemeMode");
                theme.SelectedIndex = 0;
                await vm.FlushAsync();
                Assert(((FrameworkElement)window.Content).RequestedTheme == ElementTheme.Default, "Follow system selected");
                await File.WriteAllTextAsync(Path.Combine(_output, "result.txt"),
                    "PASS: initial selections; light/dark; 4 materials and fallback; save failure/retry; navigation; reload; narrow layout; system theme selection.");
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
            var bitmap = new RenderTargetBitmap();
            await bitmap.RenderAsync((UIElement)window.Content);
            var pixels = await bitmap.GetPixelsAsync();
            var file = await StorageFile.GetFileFromPathAsync(CreateImage(name));
            using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, pixels.ToArray());
            await encoder.FlushAsync();
        }

        private string CreateImage(string name)
        {
            string path = Path.Combine(_output, name);
            File.WriteAllBytes(path, Array.Empty<byte>());
            return path;
        }
    }

    private static System.Collections.Generic.IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void Assert(bool condition, string step)
    {
        if (!condition) throw new InvalidOperationException(step);
    }

    private static async Task WaitForAsync(Func<bool> ready)
    {
        for (int attempt = 0; attempt < 100 && !ready(); attempt++) await Task.Delay(50);
        Assert(ready(), "Window initialization timed out");
    }

    private sealed class SwitchableStore(IAppearanceSettingsStore inner) : IAppearanceSettingsStore
    {
        public bool Fail;
        public Task<AppearancePreferences> LoadAsync() => inner.LoadAsync();
        public Task SaveAsync(AppearancePreferences preferences) => Fail ? throw new IOException("Simulated save failure") : inner.SaveAsync(preferences);
    }
}
