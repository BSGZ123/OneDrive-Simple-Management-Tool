using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using OneDrive_Simple_Management_Tool.ViewModels.Tools;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Foundation.Collections;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace OneDrive_Simple_Management_Tool.Views.Preview
{
    public sealed partial class MarkdownPreviewView : ContentDialog
    {
        public MarkdownPreviewView()
        {
            this.InitializeComponent();
        }

        //预览页面载入数据，不关心返回数据
        private void LoadTextContentAsync(object sender, RoutedEventArgs e)
        {
            PreviewViewModel vm = DataContext as PreviewViewModel;
            _ = vm.LoadTextContent();
        }
    }
}
