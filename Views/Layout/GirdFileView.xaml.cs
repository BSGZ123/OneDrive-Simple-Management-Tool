using Microsoft.UI.Xaml.Controls;

namespace OneDrive_Simple_Management_Tool.Views.Layout
{
    public sealed partial class GirdFileView : UserControl
    {
        public GirdFileView()
        {
            InitializeComponent();
            FileActions.Attach(this);
        }
    }
}