using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OneDrive_Simple_Management_Tool.ViewModels;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace OneDrive_Simple_Management_Tool.Views.Layout
{
    public sealed partial class ImageCloudView : UserControl
    {
        public ImageCloudView()
        {
            this.InitializeComponent();
        }

        private async void LoadIamgeAsync(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (!args.InRecycleQueue)
            {
                FileViewModel file = (FileViewModel)args.Item;
                await file.LoadImage();
            }
        }

        private async void LoadAllImages(object sender, RoutedEventArgs e)
        {
            foreach (FileViewModel image in (DataContext as DriveViewModel).Images.ToList())
            {
                await image.LoadImage();
            }
        }
    }
}
