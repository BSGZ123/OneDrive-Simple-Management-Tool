using Microsoft.UI.Xaml.Controls;

namespace OneDrive_Simple_Management_Tool.Views.Layout
{
    public sealed partial class ColumnFileView : UserControl
    {
        public ColumnFileView()
        {
            InitializeComponent();
            FileActions.Attach(this);
        }
    }
}