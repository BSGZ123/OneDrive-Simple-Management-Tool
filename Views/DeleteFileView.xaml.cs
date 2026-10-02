using Microsoft.UI.Xaml.Controls;
using OneDrive_Simple_Management_Tool.ViewModels;

namespace OneDrive_Simple_Management_Tool.Views
{
    public sealed partial class DeleteFileView : ContentDialog
    {
        public DeleteFileView()
        {
            InitializeComponent();
            FileOperationDialog.Configure(this, () => ((DeleteFileViewModel)DataContext).DeleteFileCommand.ExecuteAsync(null));
        }
    }
}