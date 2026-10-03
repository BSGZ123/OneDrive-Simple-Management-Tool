using Microsoft.UI.Xaml.Controls;

namespace OneDrive_Simple_Management_Tool.Views.Preview
{
    public sealed partial class MediaPreviewView : ContentDialog
    {
        public MediaPreviewView()
        {
            InitializeComponent();
            _ = new PreviewDialogController(this, ContentHost);
        }
    }
}