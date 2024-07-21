using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Foundation.Collections;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace OneDrive_Simple_Management_Tool
{
    /// <summary>
    /// An empty window that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class MainWindow : Window
    {
        public MainWindow()
        {
            this.InitializeComponent();
            //将窗口内容扩展到界面标题栏区域，以自定义标题栏
            //此属性在 WinUI 3 中允许你自定义标题栏，例如添加按钮、菜单或其他控件
            //但需要使用 SetTitleBar 方法来指定你想要作为标题栏使用的UI元素
            this.ExtendsContentIntoTitleBar = true;
            //将自定义的AppTitleBar设置为窗口标题栏
            this.SetTitleBar(AppTitleBar);
        }

        private void nvSample_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {

        }

        //private void myButton_Click(object sender, RoutedEventArgs e)
        //{
        //    myButton.Content = "Clicked";
        //}
    }
}
