using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OneDrive_Simple_Management_Tool.ViewModels.Tools;
using System;

namespace OneDrive_Simple_Management_Tool.Views.Preview
{
    internal sealed class PreviewDialogController
    {
        private readonly ContentDialog _dialog;
        private readonly Grid _host;
        private PreviewViewModel _viewModel;
        private bool _started;

        public PreviewDialogController(ContentDialog dialog, Grid host)
        {
            _dialog = dialog;
            _host = host;
            dialog.Loaded += Loaded;
            dialog.Closing += (_, _) => { if (_viewModel != null) _ = _viewModel.CloseAsync(); };
            dialog.Closed += Closed;
            dialog.Unloaded += Unloaded;
            dialog.PrimaryButtonClick += (_, args) => args.Cancel = true;
            dialog.SecondaryButtonClick += (_, _) =>
            {
                if (_viewModel != null) _viewModel.DownloadRequested = true;
            };
        }

        private async void Loaded(object sender, RoutedEventArgs args)
        {
            if (_started) return;
            _started = true;
            _viewModel = (PreviewViewModel)_dialog.DataContext;
            var renderer = new PreviewRenderer(_host, _viewModel);
            _viewModel.Configure(renderer.RenderAsync, renderer.Reset);
            await _viewModel.StartAsync();
        }

        private async void Closed(ContentDialog sender, ContentDialogClosedEventArgs args)
        {
            if (_viewModel != null) await _viewModel.CloseAsync();
        }

        private async void Unloaded(object sender, RoutedEventArgs args)
        {
            if (_viewModel != null) await _viewModel.CloseAsync();
        }
    }
}
