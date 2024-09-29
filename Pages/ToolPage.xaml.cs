using Microsoft.UI.Xaml.Controls;
using OneDrive_Simple_Management_Tool.Models;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

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

        //用于设置属性并通知 UI 更改
        protected bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string propertyName = null)//此属性自动获取调用属性的名称
        {
            if (Equals(storage, value)) return false;

            storage = value;
            NotifyPropertyChanged(propertyName);
            return true;
        }

        protected void NotifyPropertyChanged([CallerMemberName] string propertyName = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        //这个后续添加只能手动了
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

        //点击工具卡片后导航至目标页面
        private void GridView_ItemClick(object sender, ItemClickEventArgs e)
        {
            //MessageBox.Show("666666666");
            ToolItem toolItem = e.ClickedItem as ToolItem;
            Type pageType = Type.GetType("OneDrive_Simple_Management_Tool.Pages.Tools." + toolItem.FileName);
            (App.StartupWindow as MainWindow).Navigate(pageType);
        }
    }
}
