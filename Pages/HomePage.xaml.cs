using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using OneDrive_Simple_Management_Tool.Services;
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

        private void OpenDrive(object sender, RoutedEventArgs args)
        {
            if ((sender as FrameworkElement)?.DataContext is not HomeDriveViewModel card) return;
            var drive = card.Drive;
            (App.StartupWindow as MainWindow)?.Navigate(typeof(DrivePage),
                new DriveViewModel(new OneDrive(drive.DriveId, drive.AccountId), drive.DisplayName));
        }
    }
}
