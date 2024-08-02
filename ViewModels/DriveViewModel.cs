using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Graph.Models;
using Microsoft.UI.Xaml;
using OneDrive_Simple_Management_Tool.Helpers;
using OneDrive_Simple_Management_Tool.Models;
using OneDrive_Simple_Management_Tool.Services;
using System.Collections.ObjectModel;
using System.Linq;
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

        //默认第一次调用时从网盘根目录调取，后续接受特定路径参数,获取路径内的文件列表
        [RelayCommand]
        public async Task GetFiles(string itemId = "Root")
        {
            IsLoading = Visibility.Visible;
            _parentItemId = itemId;
            DriveItemCollectionResponse files = await Provider.GetFiles(_parentItemId);
            Files.Clear();
            Images.Clear();
            files.Value.ForEach(file =>
            {
                FileViewModel newFile = new(this, file);
                Files.Add(newFile);
                if (file.Image != null)
                    Images.Add(newFile);
            });
            IsLoading = Visibility.Collapsed;
        }


        [RelayCommand]
        public async Task Refresh()
        {
            await GetFiles(_parentItemId);
        }

        public void FilterByName(string name)
        {
            var filesToRemove = Files.Where(file => !file.Name.Contains(name)).ToList();
            foreach (var file in filesToRemove)
            {
                Files.Remove(file);
                Images.Remove(file);
            }
        }

        [RelayCommand]
        public async Task OpenFolder(FileViewModel file) 
        {
            BreadcrumbItems.Add(new BreadcrumbItem { Name = file.Name, ItemId = file.Id });
            await GetFiles(file.Id);
        }

        //获取用户网盘的容量信息(字节表示)，本地处理展示
        [RelayCommand]
        private async Task GetCapacity()
        {
            Quota quota = await Provider.GetStorageInfo();
            StorageInfo = Utils.ReadableFileSize(quota.Used) + " / " + Utils.ReadableFileSize(quota.Total);

        }


        private string _parentItemId = "Root";
        [ObservableProperty] private Visibility _isLoading = Visibility.Collapsed;
        [ObservableProperty] private string _storageInfo;


        public string ParentItemId => _parentItemId;
        public ObservableCollection<FileViewModel> Files { get; } = new();
        public ObservableCollection<FileViewModel> Images { get; } = new();
        public ObservableCollection<BreadcrumbItem> BreadcrumbItems { get; } = new();
        public FileViewModel SelectedItem { get; set; }
        public OneDrive Provider { get; }
        public string DisplayName { get; }
    }
}