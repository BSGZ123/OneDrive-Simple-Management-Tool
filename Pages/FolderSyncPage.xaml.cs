using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OneDrive_Simple_Management_Tool.Helpers;
using OneDrive_Simple_Management_Tool.Services;
using OneDrive_Simple_Management_Tool.ViewModels;
using OneDrive_Simple_Management_Tool.Views;
using System;

namespace OneDrive_Simple_Management_Tool.Pages
{
    public sealed partial class FolderSyncPage : Page
    {
        private readonly FolderSyncService _service = Ioc.Default.GetService<FolderSyncService>();
        private readonly FolderSyncViewModel _model = Ioc.Default.GetService<FolderSyncViewModel>();
        private bool _dialogOpen;

        public FolderSyncPage()
        {
            InitializeComponent();
            DataContext = _model;
            Loaded += async (_, _) => await _model.InitializeAsync();
        }

        private async void CreateBinding(object sender, RoutedEventArgs args)
        {
            if (_dialogOpen) return;
            _dialogOpen = true;
            try
            {
                using var model = new CreateFolderSyncViewModel(_service);
                var dialog = new CreateFolderSyncView(model) { XamlRoot = XamlRoot };
                await dialog.ShowAsync();
            }
            catch (Exception exception) { _model.ErrorMessage = FolderSyncJob.GetErrorKey(exception).GetLocalized(); }
            finally { _dialogOpen = false; }
        }

        private async void RemoveBinding(object sender, RoutedEventArgs args)
        {
            if (_dialogOpen || (sender as Button)?.Tag is not FolderSyncItemViewModel item) return;
            _dialogOpen = true;
            try
            {
                var dialog = new ContentDialog
                {
                    XamlRoot = XamlRoot, Title = "Sync_RemoveTitle".GetLocalized(),
                    Content = "Sync_RemoveDescription".GetLocalized(), PrimaryButtonText = "Sync_RemoveLabel".GetLocalized(),
                    CloseButtonText = "Sync_Cancel".GetLocalized()
                };
                if (await dialog.ShowAsync() == ContentDialogResult.Primary) await _service.RemoveAsync(item.Job);
            }
            catch (Exception exception) { _model.ErrorMessage = FolderSyncJob.GetErrorKey(exception).GetLocalized(); }
            finally { _dialogOpen = false; }
        }
    }
}
