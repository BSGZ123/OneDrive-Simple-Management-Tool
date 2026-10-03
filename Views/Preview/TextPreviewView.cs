using Microsoft.UI.Xaml.Controls;

namespace OneDrive_Simple_Management_Tool.Views.Preview
{
    public sealed class TextPreviewView : ContentDialog
    {
        public TextPreviewView()
        {
            _ = new PreviewDialogController(this, new Grid());
        }
    }
}
