using Microsoft.UI.Xaml.Controls;
using OneDrive_Simple_Management_Tool.Models;
using OneDrive_Simple_Management_Tool.ViewModels;
using System.Windows;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace OneDrive_Simple_Management_Tool.Pages.Tools
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class ShareCommunity : Page
    {
        public ShareCommunity()
        {
            this.InitializeComponent();
            DataContext = new ShareCommunityViewModel();
        }

        private async void Page_Loaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            await (DataContext as ShareCommunityViewModel).Refresh();
        }

        private  void ShowLinkDetailsDialogAsync(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            MessageBox.Show("还没准备好呢~");
        }

        private  void ShowCreateLinkDialogAsync(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            MessageBox.Show("还没准备好呢~");
        }
    }
}
