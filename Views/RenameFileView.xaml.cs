using Microsoft.UI.Xaml.Controls;
using OneDrive_Simple_Management_Tool.ViewModels;

namespace OneDrive_Simple_Management_Tool.Views
{
    public sealed partial class RenameFileView : ContentDialog
    {
        public RenameFileView()
        {
            InitializeComponent();
            FileOperationDialog.Configure(this, () => ((RenameFileViewModel)DataContext).RenameFileCommand.ExecuteAsync(null));
        }
    }
}