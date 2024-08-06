using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using OneDrive_Simple_Management_Tool.ViewModels;

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
