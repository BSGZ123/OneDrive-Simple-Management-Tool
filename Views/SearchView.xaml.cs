using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;


// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace OneDrive_Simple_Management_Tool.Views
{
    public sealed partial class SearchView : ContentDialog
    {
        public SearchView()
        {
            this.InitializeComponent();
        }

        private void LoadDefaultValue(object sender, RoutedEventArgs e)
        {
            (sender as ComboBox).SelectedIndex = 0;
        }
    }
}
