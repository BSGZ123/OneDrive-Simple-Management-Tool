using Microsoft.UI.Xaml.Controls;
using OneDrive_Simple_Management_Tool.Models;
using OneDrive_Simple_Management_Tool.ViewModels;
using OneDrive_Simple_Management_Tool.ViewModels.Tools;
using OneDrive_Simple_Management_Tool.Views.Tools;
using System;
using System.Threading.Tasks;
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

        private async void ShowLinkDetailsDialogAsync(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            LinkDetails dialog = new LinkDetails()
            {
                XamlRoot = XamlRoot,
                DataContext = new LinkDetailsViewModel((sender as Button).DataContext as Link)
            };

            await dialog.ShowAsync();
        }

        private async void ShowCreateLinkDialogAsync(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            CreateLink dialog = new()
            {
                XamlRoot = XamlRoot,
                DataContext = new CreateLinkViewModel(DataContext as ShareCommunityViewModel)
            };
            await dialog.ShowAsync();
        }
    }
}
