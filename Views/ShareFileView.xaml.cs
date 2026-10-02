using Microsoft.UI.Xaml.Controls;
using OneDrive_Simple_Management_Tool.ViewModels;
using Windows.ApplicationModel.DataTransfer;

namespace OneDrive_Simple_Management_Tool.Views
{
    public sealed partial class ShareFileView : ContentDialog
    {
        private readonly ShareFileViewModel _viewModel;

        public ShareFileView(FileViewModel file)
        {
            InitializeComponent();
            _viewModel = new ShareFileViewModel(file, CopyToClipboard);
            DataContext = _viewModel;
        }

        private static void CopyToClipboard(string link)
        {
            DataPackage package = new();
            package.SetText(link);
            Clipboard.SetContent(package);
            Clipboard.Flush();
        }

        private void OnClosing(ContentDialog sender, ContentDialogClosingEventArgs args)
        {
            // Keep the result visible even when Escape is pressed during a request.
            args.Cancel = _viewModel.IsGenerating;
        }
    }
}
