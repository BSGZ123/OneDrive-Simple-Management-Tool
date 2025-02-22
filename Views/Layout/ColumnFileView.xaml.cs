using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using OneDrive_Simple_Management_Tool.Models;
using OneDrive_Simple_Management_Tool.Services;
using OneDrive_Simple_Management_Tool.ViewModels;
using OneDrive_Simple_Management_Tool.ViewModels.Tools;
using OneDrive_Simple_Management_Tool.Views.Preview;
using System;
using System.IO;
using System.Threading.Tasks;

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

        private async void ShowConverFiletDialogAsync(object sender, RoutedEventArgs e)
        {
            FileViewModel converFile = DataContext as FileViewModel;
            if (converFile.IsFile)
            {
                ConvertFileFormatView view = new()
                {
                    XamlRoot = XamlRoot,
                    DataContext= new ConvertFileFormatViewModel(converFile)
                };
                await view.ShowAsync();
            }
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

        //����Ԥ��ҳ��(����ѡ���ļ�viewmodel)
        private async void ShowPreviewDialogAsync(object sender, RoutedEventArgs e)
        {
            FileViewModel viewModel = DataContext as FileViewModel;
            await ShowPreviewDialogFromViewModel(viewModel);
        }

        private async Task ShowPreviewDialogFromViewModel(FileViewModel viewModel)
        {
            //���ѡ���ļ�·���ַ�������չ��(תΪСд)���ж��ļ����� ���ʵĽ���Ԥ��
            switch (Utils.GetFileType(Path.GetExtension(viewModel.Name).ToLower()))
            {
                case FileType.Markdown:
                    {
                        MarkdownPreviewView dialog = new()
                        {
                            XamlRoot = XamlRoot,
                            DataContext = new PreviewViewModel(viewModel)
                        };
                        await dialog.ShowAsync();
                        break;
                    }
                case FileType.Image: 
                    {
                        ImagePreviewView dialog = new()
                        {
                            XamlRoot = XamlRoot,
                            DataContext = new PreviewViewModel(viewModel)
                        };
                        await dialog.ShowAsync();
                        break;
                    }
                case FileType.Media:
                    {
                        MediaPreviewView dialog = new()
                        {
                            XamlRoot = XamlRoot,
                            DataContext = new PreviewViewModel(viewModel)
                        };
                        await dialog.ShowAsync();
                        break;
                    }
                case FileType.Pdf:
                    {

                        break;
                    }
                 
            }
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
                //��������Ǵ��ļ��󳣼���ʽ�ļ�ֱ��Ԥ��
                await ShowPreviewDialogFromViewModel(fileView);
            }
        }
    }
}
