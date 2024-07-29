using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using OneDrive_Simple_Management_Tool.Helpers;
using OneDrive_Simple_Management_Tool.Models;
using OneDrive_Simple_Management_Tool.Services;
using System.Collections.ObjectModel;
using System.Threading.Tasks;

namespace OneDrive_Simple_Management_Tool.ViewModels
{
    public partial class DriveViewModel : ObservableObject
    {
        //为在 UI 中显示位置导航，本地化获取根目录字符串，为导航功能做准备
        public DriveViewModel(OneDrive provider, string displayName = null)
        {
            Provider = provider;
            DisplayName = displayName ?? provider.DriveId;
            BreadcrumbItems.Add(new BreadcrumbItem { Name = "RootFileName".GetLocalized(), ItemId = "Root" });
        }


        [RelayCommand]
        public async Task Refresh()
        {
            
        }

        [ObservableProperty] private Visibility _isLoading = Visibility.Collapsed;

        public ObservableCollection<BreadcrumbItem> BreadcrumbItems { get; } = new();
        public OneDrive Provider { get; }
        public string DisplayName { get; }
    }
}