using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using CommunityToolkit.Mvvm.DependencyInjection;
using OneDrive_Simple_Management_Tool.Helpers;
using OneDrive_Simple_Management_Tool.Models;
using OneDrive_Simple_Management_Tool.ViewModels;
using OneDrive_Simple_Management_Tool.Views;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.System;

namespace OneDrive_Simple_Management_Tool.Pages
{
    public sealed partial class DrivePage : Page
    {
        public DrivePage() => InitializeComponent();

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            if (e.Parameter is DriveViewModel drive)
            {
                DataContext = drive;
                await drive.TryRefresh();
            }
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            if (DataContext is DriveViewModel drive) drive.CancelLoading();
            base.OnNavigatedFrom(e);
        }

        private void CopyIcon_DragOver(object sender, DragEventArgs e)
        {
            if (DataContext is DriveViewModel { CanCreateHere: true } && !FileActions.IsDialogOpen(XamlRoot) && e.DataView.Contains(StandardDataFormats.StorageItems))
                e.AcceptedOperation = DataPackageOperation.Copy;
        }

        private async void ToUpload_Drop(object sender, DragEventArgs e)
        {
            try
            {
                if (DataContext is not DriveViewModel { CanCreateHere: true } || FileActions.IsDialogOpen(XamlRoot) || !e.DataView.Contains(StandardDataFormats.StorageItems)) return;
                UploadErrorInfoBar.IsOpen = false;
                DriveViewModel drive = (DriveViewModel)DataContext;
                string parentItemId = drive.ParentItemId;
                IReadOnlyList<IStorageItem> items;
                var deferral = e.GetDeferral();
                try
                {
                    items = await e.DataView.GetStorageItemsAsync();
                }
                finally
                {
                    deferral.Complete();
                }

                TaskManagerViewModel manager = Ioc.Default.GetService<TaskManagerViewModel>();
                await Task.WhenAll(items.Select(item => manager.AddUploadTask(drive, parentItemId, item)));
                if (!await drive.RefreshAfterMutation())
                {
                    UploadErrorInfoBar.Title = "UploadRefreshFailedTitle".GetLocalized();
                    UploadErrorInfoBar.Message = drive.ErrorMessage;
                    UploadErrorInfoBar.IsOpen = true;
                }
            }
            catch (Exception exception)
            {
                UploadErrorInfoBar.Title = "UploadRequestFailedTitle".GetLocalized();
                UploadErrorInfoBar.Message = FileOperationErrors.GetMessage(exception);
                UploadErrorInfoBar.IsOpen = true;
            }
        }

        private async Task ShowCreateFolder()
        {
            if (DataContext is not DriveViewModel drive || !drive.CanCreateHere) return;
            try
            {
                await FileActions.ShowDialogAsync(this, new CreateFolderView { DataContext = new CreateFolderViewModel(drive) });
            }
            catch (Exception exception) { drive.ErrorMessage = FileOperationErrors.GetMessage(exception); }
        }

        private async void CreateFolderDialogAsync(object sender, RoutedEventArgs e) => await ShowCreateFolder();

        private void ChangeLayout(object sender, RoutedEventArgs e)
        {
            if (DataContext is DriveViewModel drive && sender is MenuFlyoutItem item &&
                Enum.TryParse<FileLayout>(item.Tag?.ToString(), out var layout))
                drive.Layout = layout;
        }

        private async void OnFileAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
        {
            if (!FileActions.CanUseShortcuts(this) || DataContext is not DriveViewModel drive) return;
            // Enter must still activate a focused toolbar or breadcrumb control.
            if (sender.Key == VirtualKey.Enter && sender.Modifiers == VirtualKeyModifiers.None &&
                FocusManager.GetFocusedElement(XamlRoot) is ButtonBase or BreadcrumbBarItem) return;
            args.Handled = true;
            switch (sender.Key)
            {
                case VirtualKey.F5: await drive.Refresh(); break;
                case VirtualKey.N: await ShowCreateFolder(); break;
                case VirtualKey.Back:
                case VirtualKey.Left: await drive.GoUp(); break;
                case VirtualKey.F2: await FileActions.ExecuteAsync(FileAction.Rename, this, drive.SelectedItem); break;
                case VirtualKey.Delete: await FileActions.ExecuteAsync(FileAction.Delete, this, drive.SelectedItem); break;
                case VirtualKey.Enter:
                    await FileActions.ExecuteAsync(sender.Modifiers == VirtualKeyModifiers.Menu ? FileAction.Property : FileAction.Open, this, drive.SelectedItem);
                    break;
            }
        }

        private async void ShowSearchDialogAsync(object sender, RoutedEventArgs e)
        {
            if (DataContext is not DriveViewModel drive) return;
            try
            {
                await FileActions.ShowDialogAsync(this, new SearchView { DataContext = new SearchViewModel(drive) });
            }
            catch (Exception exception) { drive.ErrorMessage = FileOperationErrors.GetMessage(exception); }
        }

        private async void BreadcrumbBar_ItemClicked(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs args)
        {
            if (DataContext is DriveViewModel drive) await drive.NavigateToBreadcrumb(args.Index);
        }
    }
}
