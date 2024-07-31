using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using OneDrive_Simple_Management_Tool.ViewModels;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Foundation.Collections;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace OneDrive_Simple_Management_Tool.Views.Layout
{
    public sealed partial class GirdFileView : UserControl
    {
        public GirdFileView()
        {
            this.InitializeComponent();
        }

        private void ShowDeleteFileDialogAsync(object sender, RoutedEventArgs e)
        {

        }

        private void ShowConverFiletDialogAsync(object sender, RoutedEventArgs e)
        {

        }

        private void ShowShareFileDialogAsync(object sender, RoutedEventArgs e)
        {

        }

        private void ShowRenameFileDialogAsync(object sender, RoutedEventArgs e)
        {

        }

        private void ShowPropertyDialogAsync(object sender, RoutedEventArgs e)
        {

        }

        private async void OpenFolder(object sender, DoubleTappedRoutedEventArgs e)
        {
            FileViewModel viewModel = DataContext as FileViewModel;
            //if (viewModel.IsFolder)
            //{
            //    await viewModel.Drive.OpenFolder(viewModel);
            //}
        }
    }
}
