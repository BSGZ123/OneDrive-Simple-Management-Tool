using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using CommunityToolkit.Mvvm.Input;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using OneDrive_Simple_Management_Tool.Views;
using OneDrive_Simple_Management_Tool.ViewModels;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace OneDrive_Simple_Management_Tool.Pages
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class DrivePage : Page
    {
        public DrivePage()
        {
            this.InitializeComponent();
        }

        private void CopyIcon_DragOver(object sender, DragEventArgs e)
        {
            //检查拖动的数据中是否包含 StorageItems 格式的数据
            if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                //允许执行拖放操作
                e.AcceptedOperation = DataPackageOperation.Copy;
            }
        }

        private void ToUpload_Drop(object sender, DragEventArgs e)
        {

        }

        private void CreateFolderDialogAsync(object sender, RoutedEventArgs e)
        {

        }

        private void ChangeLayout(object sender, RoutedEventArgs e)
        {

        }

        private async void BackToLastFolder(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
        { 
        
        }

        private async void ShowSearchDialogAsync(object sender, RoutedEventArgs e)
        {
            SearchView searchDiglog = new SearchView()
            {
                XamlRoot = XamlRoot,
                DataContext=new SearchViewModel(DataContext as DriveViewModel)
            };

            await searchDiglog.ShowAsync();

        }

        private void BreadcrumbBar_ItemClicked(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs args)
        {

        }
    }
}
