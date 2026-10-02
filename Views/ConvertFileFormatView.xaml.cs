using Microsoft.UI.Xaml.Controls;
using OneDrive_Simple_Management_Tool.ViewModels;

namespace OneDrive_Simple_Management_Tool.Views
{
    public sealed partial class ConvertFileFormatView : ContentDialog
    {
        public ConvertFileFormatView()
        {
            InitializeComponent();
            Closing += (_, args) =>
            {
                if (DataContext is ConvertFileFormatViewModel { IsBusy: true }) args.Cancel = true;
            };
        }
    }
}