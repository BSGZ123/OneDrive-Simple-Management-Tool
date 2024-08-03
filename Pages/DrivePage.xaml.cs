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
using System.Collections.ObjectModel;
using OneDrive_Simple_Management_Tool.Models;

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

        //在页面导航到时,设置该页面的数据上下文
        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            //检查导航参数 e.Parameter 是否为 DriveViewModel类型，是就赋值给drive
            if (e.Parameter is DriveViewModel drive)
            {
                //将drive设置为页面的数据上下文，这样就可以点击某个网盘导航到该网盘文件列表
                DataContext= drive;
            }
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

        //返回上级目录功能
        private async void BackToLastFolder(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
        {

            var items = BreadcrumbBar.ItemsSource as ObservableCollection<BreadcrumbItem>;
            //检查是否有上级目录，没有就无动作
            if (items.Count <= 1) 
            {
                return;
            }

            await (DataContext as DriveViewModel).GetFiles(items.Last().ItemId);

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

        //点击导航栏某一条路径，进入所点击的文件路径
        private async void BreadcrumbBar_ItemClicked(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs args)
        {
            var items = BreadcrumbBar.ItemsSource as ObservableCollection<BreadcrumbItem>;
            //循环删除导航栏中被点击项之后的所有项
            for(int i = items.Count - 1; i >= args.Index + 1; i--)
            {
                items.RemoveAt(i);
            }

            string itemId = (args.Item as BreadcrumbItem).ItemId;
            await (DataContext as DriveViewModel).GetFiles(itemId);
        }
    }
}
