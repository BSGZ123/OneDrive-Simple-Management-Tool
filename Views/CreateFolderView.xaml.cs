using Microsoft.UI.Xaml.Controls;
using OneDrive_Simple_Management_Tool.ViewModels;

namespace OneDrive_Simple_Management_Tool.Views
{
    public sealed partial class CreateFolderView : ContentDialog
    {
        public CreateFolderView()
        {
            InitializeComponent();
            FileOperationDialog.Configure(this, () => ((CreateFolderViewModel)DataContext).CreateFolderCommand.ExecuteAsync(null));
        }
    }
}