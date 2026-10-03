using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OneDrive_Simple_Management_Tool.Helpers;
using OneDrive_Simple_Management_Tool.Models;
using OneDrive_Simple_Management_Tool.Services;
using OneDrive_Simple_Management_Tool.ViewModels;
using System;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace OneDrive_Simple_Management_Tool.Views
{
    public sealed partial class CreateFolderSyncView : ContentDialog
    {
        private readonly CreateFolderSyncViewModel _model;
        private bool _binding;
        public CreateFolderSyncView(CreateFolderSyncViewModel model)
        {
            InitializeComponent();
            DataContext = _model = model;
            Loaded += async (_, _) => await model.LoadDrivesAsync();
        }

        private async void PickLocal(object sender, RoutedEventArgs args)
        {
            if (_binding) return;
            try
            {
                var picker = new FolderPicker();
                picker.FileTypeFilter.Add("*");
                InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.StartupWindow));
                var folder = await picker.PickSingleFolderAsync();
                if (folder != null) _model.LocalPath = folder.Path;
            }
            catch (Exception exception) { _model.ErrorMessage = FolderSyncJob.GetErrorKey(exception).GetLocalized(); }
        }

        private async void SelectDrive(object sender, SelectionChangedEventArgs args)
        {
            if (!_binding) await _model.SelectDriveAsync();
        }

        private async void OpenFolder(object sender, ItemClickEventArgs args)
        {
            if (!_model.IsBusy && args.ClickedItem is FolderSyncRemoteFolder folder) await _model.OpenFolderAsync(folder);
        }

        private async void Bind(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            var deferral = args.GetDeferral();
            _binding = true;
            try { args.Cancel = !await _model.BindAsync(); }
            finally { _binding = false; deferral.Complete(); }
        }

        private void OnClosing(ContentDialog sender, ContentDialogClosingEventArgs args)
        {
            if (_binding) args.Cancel = true;
        }
    }
}
