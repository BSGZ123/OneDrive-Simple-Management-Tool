using Microsoft.Graph.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using OneDrive_Simple_Management_Tool.Pages;
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
            //������������չ������������������Զ��������
            //�������� WinUI 3 ���������Զ����������������Ӱ�ť���˵��������ؼ�
            //����Ҫʹ�� SetTitleBar ������ָ������Ҫ��Ϊ������ʹ�õ�UIԪ��
            this.ExtendsContentIntoTitleBar = true;
            //���Զ����AppTitleBar����Ϊ���ڱ�����
            this.SetTitleBar(AppTitleBar);
        }

        //NavigationView �ؼ��ĵ�������
        private void nvSample_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            if(args.IsSettingsSelected)
            {
                contentFrame.Navigate(typeof(SettingPage));
            }
            else
            {
                var selectedItem=(NavigationViewItem)args.SelectedItem;
                string selectedItemTag = (string)selectedItem.Tag;
                //·������������.
                string pageName = "OneDrive_Simple_Management_Tool.Pages." + selectedItemTag;
                Type pageNameType = Type.GetType(pageName);//����ҳ������·����ȡ��Ӧ�� Type ����
                contentFrame.Navigate(pageNameType);

            }
        }

        //�����������ֱ����Ŀ��ҳ������͡����ݸ�Ŀ��ҳ��Ĳ����͵���������Ϣ
        public void Navigate(Type pageType,object targetPageArguments = null, NavigationTransitionInfo navigationTransitionInfo = null)
        {
            Rootframe.Navigate(pageType, targetPageArguments, navigationTransitionInfo);
        }

        public Frame Rootframe => contentFrame;

        //private void myButton_Click(object sender, RoutedEventArgs e)
        //{
        //    myButton.Content = "Clicked";
        //}
    }
}
