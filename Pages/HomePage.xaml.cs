using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using OneDrive_Simple_Management_Tool.Services;
using OneDrive_Simple_Management_Tool.Models;
using OneDrive_Simple_Management_Tool.ViewModels;

namespace OneDrive_Simple_Management_Tool.Pages
{
    public sealed partial class HomePage : Page
    {
        public HomeViewModel Model { get; } = Ioc.Default.GetRequiredService<HomeViewModel>();

        public HomePage()
        {
            InitializeComponent();
            DataContext = Model;
            Model.Bookmarks.NavigationRequested += location => (App.StartupWindow as MainWindow)?.Navigate(typeof(DrivePage),
                new DriveNavigationRequest(new DriveViewModel(new OneDrive(location.Bookmark.DriveId, location.Bookmark.AccountId),
                    location.Bookmark.DriveName), location));
            Loaded += async (_, _) => await Model.ActivateAsync();
            Unloaded += (_, _) => Model.Deactivate();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs args)
        {
            Model.Deactivate();
            base.OnNavigatedFrom(args);
        }

        private void OpenFiles(object sender, RoutedEventArgs args) => (App.StartupWindow as MainWindow)?.Navigate(typeof(CloudPage));
        private void OpenTasks(object sender, RoutedEventArgs args) => (App.StartupWindow as MainWindow)?.Navigate(typeof(TaskManagerPage));
        private void OpenSync(object sender, RoutedEventArgs args) => (App.StartupWindow as MainWindow)?.Navigate(typeof(FolderSyncPage));
        private void OpenSettings(object sender, RoutedEventArgs args) => (App.StartupWindow as MainWindow)?.Navigate(typeof(SettingPage));
        private void OpenBookmarks(object sender, RoutedEventArgs args) => (App.StartupWindow as MainWindow)?.Navigate(typeof(BookmarkPage));

        private async void OpenRecentBookmark(object sender, RoutedEventArgs args)
        {
            if ((sender as FrameworkElement)?.DataContext is BookmarkItemViewModel item) await Model.Bookmarks.OpenAsync(item);
        }

        private async void RefreshOverview(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
        {
            args.Handled = true;
            if (Model.RefreshCommand.CanExecute(null)) await Model.RefreshCommand.ExecuteAsync(null);
        }

        private void OpenDrive(object sender, RoutedEventArgs args)
        {
            if ((sender as FrameworkElement)?.DataContext is not HomeDriveViewModel card) return;
            var drive = card.Drive;
            (App.StartupWindow as MainWindow)?.Navigate(typeof(DrivePage),
                new DriveViewModel(new OneDrive(drive.DriveId, drive.AccountId), drive.DisplayName));
        }
    }
}
