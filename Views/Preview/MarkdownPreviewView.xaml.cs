using Microsoft.UI.Xaml.Controls;

namespace OneDrive_Simple_Management_Tool.Views.Preview
{
    public sealed partial class MarkdownPreviewView : ContentDialog
    {
        public MarkdownPreviewView()
        {
            InitializeComponent();
            _ = new PreviewDialogController(this, ContentHost);
        }
    }
}