using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using OneDrive_Simple_Management_Tool.ViewModels;
using System;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace OneDrive_Simple_Management_Tool.Views.Layout
{
    public sealed partial class ColumnFileView : UserControl
    {
        public ColumnFileView()
        {
            this.InitializeComponent();
        }

        private async void ShowDeleteFileDialogAsync(object sender, RoutedEventArgs e)
        {
            FileViewModel file = DataContext as FileViewModel;
            DeleteFileView dialog = new DeleteFileView() 
            {
                XamlRoot=XamlRoot,
                DataContext = new DeleteFileViewModel(file)
            };
            await dialog.ShowAsync();
        }

        private void ShowConverFiletDialogAsync(object sender, RoutedEventArgs e)
        {

        }

        private void ShowShareFileDialogAsync(object sender, RoutedEventArgs e)
        {

        }

        private async void ShowRenameFileDialogAsync(object sender, RoutedEventArgs e)
        {
            FileViewModel file=DataContext as FileViewModel;
            RenameFileView dialog = new()
            {
                XamlRoot = XamlRoot,
                DataContext = new RenameFileViewModel(file.Drive, file)
            };
            await dialog.ShowAsync();
        }

        private async void ShowPropertyDialogAsync(object sender, RoutedEventArgs e)
        {
            FileViewModel file = DataContext as FileViewModel;
            PropertyView dialog = new()
            {
                XamlRoot = XamlRoot,
                DataContext = new PropertyViewModel(file)
            };

            await dialog.ShowAsync();

        }

        private void ShowPreviewDialogAsync(object sender, RoutedEventArgs e)
        {

        }

        private async void OpenFile(object sender, DoubleTappedRoutedEventArgs e)
        {
            FileViewModel fileView = DataContext as FileViewModel;
            if (fileView.IsFolder)
            {
                await fileView.Drive.OpenFolder(fileView);
            }
            else 
            {
                //这里后续是打开文件，常见格式文件预览
                
            }
        }
    }
}
