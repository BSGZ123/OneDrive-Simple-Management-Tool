using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using OneDrive_Simple_Management_Tool;
using OneDrive_Simple_Management_Tool.Helpers;
using OneDrive_Simple_Management_Tool.Pages;
using OneDrive_Simple_Management_Tool.Services;
using OneDrive_Simple_Management_Tool.ViewModels;
using OneDrive_Simple_Management_Tool.Views.Layout;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.Graphics;
using Windows.System;

internal static partial class BookmarkUiProbe
{
    private sealed partial class ProbeApp
    {
        private async Task VerifyIntegration(MainWindow window, IBookmarkStore store, SettingViewModel settings, ApplicationDataPaths paths)
        {
            foreach (var entry in await store.LoadAsync()) await store.RemoveAsync(entry);
            window.AppWindow.Resize(new SizeInt32(1280, 1060));
            var navigation = (NavigationView)((FrameworkElement)window.Content).FindName("nvSample");
            window.Navigate(typeof(HomePage));
            var home = await ReadyHome(window);
            Assert(home.Model.Bookmarks.IsEmpty && home.Model.Drives.Count == 2, "Home local empty state alongside real quota bindings");
            Assert(home.KeyboardAccelerators.Any(a => a.Key == VirtualKey.F5), "Home refresh keyboard accelerator");

            // Full loop through the real Files page and real context menus.
            Invoke((Button)home.FindName("FilesShortcut"));
            await Until(() => window.Rootframe.Content is CloudPage cloud && cloud.IsLoaded &&
                cloud.DataContext is CloudViewModel vm && vm.Drives.Count == 2);
            var cloud = (CloudPage)window.Rootframe.Content;
            var cloudList = (ListView)cloud.FindName("DriveList");
            var drive = ((CloudViewModel)cloud.DataContext).Drives.First(d => d.Provider.HomeAccountId == "account-A");
            cloudList.ScrollIntoView(drive);
            await Until(() => Descendants(cloudList).OfType<Grid>().Any(g => g.DataContext == drive && g.ContextFlyout is MenuFlyout));
            await ClickBookmarkMenu(Descendants(cloudList).OfType<Grid>().First(g => g.DataContext == drive && g.ContextFlyout is MenuFlyout), "CloudPage_Open/Text");
            await Until(() => window.Rootframe.Content is DrivePage p && p.IsLoaded && p.DataContext is DriveViewModel vm && vm.IsComplete);
            await ClickBookmarkMenu(await Owner<ColumnFileView>(window, "file-A"), "Bookmarks_Add");
            await UntilAsync(async () => (await store.LoadAsync()).Count == 1);
            window.Navigate(typeof(HomePage));
            home = await ReadyHome(window);
            Assert(home.Model.Bookmarks.RecentItems.Single().Bookmark.ItemId == "file-A", "Home sees a bookmark saved from Files");
            await Until(() => Descendants(home).OfType<Button>().Any(b => b.DataContext is BookmarkItemViewModel));
            Invoke(Descendants(home).OfType<Button>().First(b => b.DataContext is BookmarkItemViewModel));
            await Until(() => window.Rootframe.Content is DrivePage p && p.DataContext is DriveViewModel vm && vm.IsComplete);
            var opened = (DriveViewModel)((DrivePage)window.Rootframe.Content).DataContext;
            Assert(opened.SelectedItem?.Id == "file-A" && opened.ParentItemId == "moved-parent" && opened.Provider.HomeAccountId == "account-A", "Recent bookmark resolves and selects moved file");
            window.Navigate(typeof(HomePage));
            home = await ReadyHome(window);
            Invoke((Button)home.FindName("BookmarksShortcut"));
            await ReadyBookmarks(window);
            var bookmarks = (BookmarkPage)window.Rootframe.Content;
            await ClickRow(bookmarks, bookmarks.Model.Items[0], "Bookmarks_RemoveButton");
            await Until(() => bookmarks.Model.IsEmpty);
            window.Navigate(typeof(HomePage));
            home = await ReadyHome(window);
            Assert(home.Model.Bookmarks.IsEmpty, "Home reflects removal on return");

            string longName = string.Concat(Enumerable.Repeat("Quarterly design review · 长名称检查 ", 6)) + ".md";
            await store.AddAsync(Entry("file-A", longName));
            await store.AddAsync(Entry("folder-A", "Reference folder · 参考资料"));
            await store.AddAsync(Entry("missing", "Unavailable item · 失效项目"));
            await home.Model.RefreshCommand.ExecuteAsync(null);
            Assert(home.Model.Bookmarks.RecentItems.Count == 3, "Home refresh rereads local bookmarks");
            await home.Model.Bookmarks.OpenAsync(home.Model.Bookmarks.RecentItems.Single(b => b.Bookmark.ItemId == "missing"));
            Assert(home.Model.Bookmarks.RecentItems.Single(b => b.Bookmark.ItemId == "missing").HasError && !home.Model.HasError, "Recent item error stays isolated");
            await Capture(window, "integration-home-light.png");

            // Change theme through the production Settings control, then visit the other two pages.
            Invoke((Button)home.FindName("SettingsShortcut"));
            await Until(() => window.Rootframe.Content is SettingPage p && p.IsLoaded);
            var settingsPage = (SettingPage)window.Rootframe.Content;
            ((ComboBox)settingsPage.FindName("ThemeMode")).SelectedIndex = 2;
            await settings.FlushAsync();
            window.Navigate(typeof(HomePage));
            home = await ReadyHome(window);
            Assert(home.ActualTheme == ElementTheme.Dark, "Settings theme reaches Home");
            await Capture(window, "integration-home-dark.png");
            window.AppWindow.Resize(new SizeInt32(620, 760));
            await Until(() => navigation.DisplayMode != NavigationViewDisplayMode.Expanded && !navigation.IsPaneOpen);
            await Capture(window, "integration-home-narrow.png");
            foreach (string name in new[] { "FilesShortcut", "TasksShortcut", "SyncShortcut", "BookmarksShortcut", "SettingsShortcut" })
                AssertFullLabel((Button)home.FindName(name));
            window.AppWindow.Resize(new SizeInt32(1280, 1060));
            Invoke((Button)home.FindName("BookmarksShortcut"));
            await ReadyBookmarks(window);
            bookmarks = (BookmarkPage)window.Rootframe.Content;
            Assert(bookmarks.ActualTheme == ElementTheme.Dark, "Settings theme reaches Bookmarks");
            Assert(bookmarks.KeyboardAccelerators.Any(a => a.Key == VirtualKey.F && a.Modifiers == VirtualKeyModifiers.Control) &&
                bookmarks.KeyboardAccelerators.Any(a => a.Key == VirtualKey.F5), "Bookmark keyboard accelerators");
            await Capture(window, "integration-bookmarks-dark.png");

            // A small viewport exercises automatic pane collapse and the scrolling list header.
            window.AppWindow.Resize(new SizeInt32(520, 520));
            await Until(() => navigation.DisplayMode != NavigationViewDisplayMode.Expanded && !navigation.IsPaneOpen);
            var search = (TextBox)bookmarks.FindName("BookmarkSearch");
            search.Text = "Quarterly";
            await Until(() => bookmarks.Model.Items.Count == 1);
            Assert(search.Focus(FocusState.Keyboard), "Search accepts keyboard focus");
            Assert(ReferenceEquals(FocusManager.GetFocusedElement(bookmarks.XamlRoot), search), "Search holds keyboard focus");
            var list = (ListView)bookmarks.FindName("BookmarkList");
            list.ScrollIntoView(bookmarks.Model.Items.Single());
            await Task.Delay(250);
            var scroller = Descendants(list).OfType<ScrollViewer>().First();
            scroller.ChangeView(null, scroller.ScrollableHeight, null, true);
            await Task.Delay(200);
            Assert(scroller.ScrollableHeight > 0 && scroller.ScrollableWidth < 1, "Long bookmark scrolls vertically without horizontal overflow");
            var rowButtons = BookmarkButtons(list).ToArray();
            Assert(rowButtons.Length == 2 && rowButtons.All(b => b.IsEnabled && b.ActualWidth > 0), "Narrow bookmark actions remain enabled");
            foreach (var button in rowButtons) AssertFullLabel(button);
            Assert(rowButtons[0].Focus(FocusState.Keyboard), "Bookmark action accepts keyboard focus");
            await Capture(window, "integration-bookmarks-short.png");

            search.Text = "";
            await File.WriteAllTextAsync(paths.Bookmarks, "fictional damaged integration data");
            await bookmarks.Model.ReloadCommand.ExecuteAsync(null);
            await Task.Delay(150);
            var rebuild = Descendants(list).OfType<Button>().Single(b => b.Content?.ToString() == "Bookmarks_Rebuild/Content".GetLocalized());
            rebuild.StartBringIntoView();
            await Task.Delay(200);
            Invoke(rebuild);
            await Until(() => Dialog(window) != null);
            var dialog = Dialog(window);
            Assert(dialog.ActualTheme == ElementTheme.Dark, "Recovery dialog follows the selected theme");
            await Capture(window, "integration-recovery-dialog.png", dialog);
            Invoke(Descendants(dialog).OfType<Button>().Single(b => b.Name == "CloseButton"));
            await Until(() => Dialog(window) == null);
            // Popup removal precedes completion of ShowAsync and the page's close guard.
            await Task.Delay(300);
            Assert(bookmarks.Model.NeedsRecovery && await File.ReadAllTextAsync(paths.Bookmarks) == "fictional damaged integration data", "Cancel leaves damaged data untouched");
            Invoke(rebuild);
            await Until(() => Dialog(window) != null);
            await Task.Delay(300);
            Invoke(Descendants(Dialog(window)).OfType<Button>().Single(b => b.Name == "PrimaryButton"));
            await Until(() => !bookmarks.Model.IsBusy && bookmarks.Model.IsEmpty && !bookmarks.Model.NeedsRecovery);
            Assert((await new BookmarkStore(paths).LoadAsync()).Count == 0 && Directory.GetFiles(Path.GetDirectoryName(paths.Bookmarks), "bookmarks.dat.recovery-*").Length >= 2, "Confirmed rebuild preserves recovery copies");

            window.Navigate(typeof(SettingPage));
            await Until(() => window.Rootframe.Content is SettingPage p && p.IsLoaded);
            settingsPage = (SettingPage)window.Rootframe.Content;
            Assert(((ComboBox)settingsPage.FindName("ThemeMode")).SelectedIndex == 2, "Settings selection survives navigation");
            ((ComboBox)settingsPage.FindName("ThemeMode")).SelectedIndex = 1;
            await settings.FlushAsync();
            var restored = new SettingViewModel(new AppearanceSettingsStore(paths));
            await restored.InitializeAsync();
            Assert(restored.ThemeIndex == 1, "Fresh preferences instance sees saved theme");
            await Capture(window, "integration-settings-short.png");
            var settingsScroll = Descendants(settingsPage).OfType<ScrollViewer>().First();
            settingsScroll.ChangeView(null, settingsScroll.ScrollableHeight, null, true);
            await Task.Delay(200);
            foreach (var button in Descendants(settingsPage).OfType<Button>().Where(b => b.Content is string text &&
                new[] { "Diagnostics_Start/Content", "Diagnostics_Stop/Content", "Diagnostics_Copy/Content" }.Any(k => k.GetLocalized() == text)))
                AssertFullLabel(button);
            await Capture(window, "integration-settings-actions.png");
        }
    }

    private static async Task<HomePage> ReadyHome(MainWindow window)
    {
        await Until(() => window.Rootframe.Content is HomePage p && p.IsLoaded && !p.Model.IsLoading && !p.Model.Bookmarks.IsBusy);
        return (HomePage)window.Rootframe.Content;
    }
    private static void Invoke(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
    private static void AssertFullLabel(Button button)
    {
        var label = Descendants(button).OfType<TextBlock>().First(t => t.Text == button.Content?.ToString());
        var measured = new TextBlock { Text = label.Text, FontFamily = label.FontFamily, FontSize = label.FontSize, FontWeight = label.FontWeight };
        measured.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        Assert(label.ActualWidth + 1 >= measured.DesiredSize.Width, "Complete button label: " + label.Text);
    }
    private static IEnumerable<Button> BookmarkButtons(DependencyObject root) => Descendants(root).OfType<Button>()
        .Where(b => b.DataContext is BookmarkItemViewModel && (b.Content?.ToString() == "Bookmarks_Open/Content".GetLocalized() ||
            b.Content?.ToString() == "Bookmarks_RemoveButton/Content".GetLocalized()));
    private static ContentDialog Dialog(MainWindow window) => VisualTreeHelper.GetOpenPopupsForXamlRoot(((FrameworkElement)window.Content).XamlRoot)
        .SelectMany(p => Descendants(p.Child).Prepend(p.Child)).OfType<ContentDialog>().FirstOrDefault();
}
