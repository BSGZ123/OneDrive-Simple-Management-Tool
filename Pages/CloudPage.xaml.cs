using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Diagnostics.CodeAnalysis;
using OneDrive_Simple_Management_Tool.Views;
using OneDrive_Simple_Management_Tool.ViewModels;
using Microsoft.UI.Xaml.Input;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace OneDrive_Simple_Management_Tool.Pages
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class CloudPage : Page
    {
        public CloudPage()
        {
            InitializeComponent();
        }

        private async void ShowCreateDriveDialogAsync(object sender, RoutedEventArgs e)
        {
            //异步加载
            CreateDrive createDriveDialog = new CreateDrive() 
            {
                //将对话框的 XamlRoot 属性设置为当前的 XamlRoot，用于确定 XAML 页面的上下文
                XamlRoot = XamlRoot,
                DataContext = new CreateDriveViewModel(DataContext as CloudViewModel)
            };
            await createDriveDialog.ShowAsync();
        }

        private void NavigateToDrive(object sender,DoubleTappedRoutedEventArgs e)
        {

        }
    }
}
