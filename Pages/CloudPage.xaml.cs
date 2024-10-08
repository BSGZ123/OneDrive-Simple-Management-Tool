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
        //每当页面初始化时，应当加载本地缓存的云盘信息，利用Loaded
        [RequiresUnreferencedCode("Calls System.Text.Json.JsonSerializer.Serialize<TValue>(TValue, JsonSerializerOptions)")]
        public CloudPage()
        {
            InitializeComponent();
            DataContext =new CloudViewModel();
            Loaded += async (sender, args) =>
            {
                await (DataContext as CloudViewModel).LoadDrivesFromDisk();
            };
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

        //双击网盘列表某一行后，将会根据网盘名获取网盘信息，跳转到网盘文件页面
        private void NavigateToDrive(object sender,DoubleTappedRoutedEventArgs e)
        {
            string displayName=(sender as Grid).Tag.ToString();
            DriveViewModel drive = (DataContext as CloudViewModel).GetDrive(displayName);
            Type pageType = Type.GetType("OneDrive_Simple_Management_Tool.Pages.DrivePage");
            //实现路由追踪跳转。。。
            (App.StartupWindow as MainWindow).Navigate(pageType,drive);
        }
    }
}
