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

        //��ҳ�浼����ʱ,���ø�ҳ�������������
        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            //��鵼������ e.Parameter �Ƿ�Ϊ DriveViewModel���ͣ��Ǿ͸�ֵ��drive
            if (e.Parameter is DriveViewModel drive)
            {
                //��drive����Ϊҳ������������ģ������Ϳ��Ե��ĳ�����̵������������ļ��б�
                DataContext= drive;
            }
        }

        private void CopyIcon_DragOver(object sender, DragEventArgs e)
        {
            //����϶����������Ƿ���� StorageItems ��ʽ������
            if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                //����ִ���ϷŲ���
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

        //�����ϼ�Ŀ¼����
        private async void BackToLastFolder(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
        {

            var items = BreadcrumbBar.ItemsSource as ObservableCollection<BreadcrumbItem>;
            //����Ƿ����ϼ�Ŀ¼��û�о��޶���
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

        //���������ĳһ��·����������������ļ�·��
        private async void BreadcrumbBar_ItemClicked(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs args)
        {
            var items = BreadcrumbBar.ItemsSource as ObservableCollection<BreadcrumbItem>;
            //ѭ��ɾ���������б������֮���������
            for(int i = items.Count - 1; i >= args.Index + 1; i--)
            {
                items.RemoveAt(i);
            }

            string itemId = (args.Item as BreadcrumbItem).ItemId;
            await (DataContext as DriveViewModel).GetFiles(itemId);
        }
    }
}
