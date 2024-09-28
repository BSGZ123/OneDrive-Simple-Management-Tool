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
using System.Windows;
using OneDrive_Simple_Management_Tool.Models;
using System.Runtime.CompilerServices;
using System.ComponentModel;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace OneDrive_Simple_Management_Tool.Pages
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public  partial class ToolPage : Page,INotifyPropertyChanged
    {
        public ToolPage()
        {
            this.InitializeComponent();
        }

        //�����������Բ�֪ͨ UI ����
        protected bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string propertyName = null)//�������Զ���ȡ�������Ե�����
        {
            if (Equals(storage, value)) return false;

            storage = value;
            NotifyPropertyChanged(propertyName);
            return true;
        }

        protected void NotifyPropertyChanged([CallerMemberName] string propertyName = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        //����������ֻ���ֶ���
        private IEnumerable<ToolItem> _items =new List<ToolItem> {
        
            new() {
                Name = "Share Community",
                Description="Share and browse OneDrive files",
                ImagePath="/Assets/link-share.png",
                FileName="ShareCommunity"
            },
        };

        public IEnumerable<ToolItem> Items
        {
            get => _items;
            set => SetProperty(ref _items, value);
        }


        public event PropertyChangedEventHandler PropertyChanged;

        //������߿�Ƭ�󵼺���Ŀ��ҳ��
        private void GridView_ItemClick(object sender, ItemClickEventArgs e)
        {
            MessageBox.Show("666666666");
        }
    }
}
