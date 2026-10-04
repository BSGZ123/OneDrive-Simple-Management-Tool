using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using OneDrive_Simple_Management_Tool.Helpers;
using OneDrive_Simple_Management_Tool.Models;
using OneDrive_Simple_Management_Tool.Services;
using OneDrive_Simple_Management_Tool.ViewModels;
using System;
using Windows.System;

namespace OneDrive_Simple_Management_Tool.Pages
{
    public sealed partial class BookmarkPage : Page
    {
        private bool _dialogOpen;
        public BookmarkViewModel Model { get; } = Ioc.Default.GetRequiredService<BookmarkViewModel>();

        public BookmarkPage()
        {
            InitializeComponent();
            DataContext = Model;
            Model.NavigationRequested += location => (App.StartupWindow as MainWindow)?.Navigate(typeof(DrivePage),
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

        private async void OpenBookmark(object sender, RoutedEventArgs args)
        {
            if ((sender as FrameworkElement)?.DataContext is BookmarkItemViewModel item) await Model.OpenAsync(item);
        }

        private async void RemoveBookmark(object sender, RoutedEventArgs args)
        {
            if ((sender as FrameworkElement)?.DataContext is BookmarkItemViewModel item) await Model.RemoveAsync(item);
        }

        private async void OnListKeyDown(object sender, KeyRoutedEventArgs args)
        {
            if (args.Key != VirtualKey.Enter || FocusManager.GetFocusedElement(XamlRoot) is ButtonBase) return;
            if (BookmarkList.SelectedItem is not BookmarkItemViewModel item) return;
            args.Handled = true;
            await Model.OpenAsync(item);
        }

        private void OpenFiles(object sender, RoutedEventArgs args) => (App.StartupWindow as MainWindow)?.Navigate(typeof(CloudPage));

        private async void Rebuild(object sender, RoutedEventArgs args)
        {
            if (_dialogOpen || Model.IsBusy || !Model.NeedsRecovery) return;
            _dialogOpen = true;
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot, Title = "Bookmarks_RebuildTitle".GetLocalized(), Content = "Bookmarks_RebuildDescription".GetLocalized(),
                PrimaryButtonText = "Bookmarks_RebuildAction".GetLocalized(), CloseButtonText = "Account_Cancel".GetLocalized(),
                DefaultButton = ContentDialogButton.Close
            };
            try
            {
                if (await dialog.ShowAsync() == ContentDialogResult.Primary) await Model.RebuildAsync();
            }
            catch (Exception exception) { Model.ErrorMessage = BookmarkErrors.Message(exception, "Bookmarks_RecoveryFailed"); }
            finally { _dialogOpen = false; }
        }
    }
}
