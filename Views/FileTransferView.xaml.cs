using Microsoft.UI.Xaml.Controls;
using OneDrive_Simple_Management_Tool.Models;
using OneDrive_Simple_Management_Tool.ViewModels;

namespace OneDrive_Simple_Management_Tool.Views
{
    public sealed partial class FileTransferView : ContentDialog
    {
        public FileTransferView()
        {
            InitializeComponent();
            Opened += async (_, _) => await ((FileTransferViewModel)DataContext).InitializeAsync();
            FileOperationDialog.Configure(this, () => ((FileTransferViewModel)DataContext).SubmitAsync(), closeOnSuccess: false);
        }

        private async void OpenFolder(object sender, ItemClickEventArgs args)
        {
            if (args.ClickedItem is TransferItem folder) await ((FileTransferViewModel)DataContext).OpenFolderAsync(folder);
        }
    }
}
