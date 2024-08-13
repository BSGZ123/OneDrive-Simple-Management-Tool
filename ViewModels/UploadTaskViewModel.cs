using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using System;
using System.Threading.Tasks;
using Windows.Storage;

namespace OneDrive_Simple_Management_Tool.ViewModels
{
    public partial class UploadTaskViewModel : ObservableObject
    {
        public UploadTaskViewModel(DriveViewModel drive,string itemId,IStorageItem item) 
        { 

            Drive = drive;
            _item = item;
            _itemId = itemId;
        }


        public async Task StartUpload()
        {
            IsUploading = true;

            //进度报告机制
            IProgress<long> progress = new Progress<long>( value =>
            {
                //调度任务到合适线程
               _dispatcher.TryEnqueue(async () =>
              {
                  //如果 _item 是 StorageFile，计算进度百分比为 (ulong)value * 100 / 文件大小，否则的话直接返回value
                  Progress = _item is StorageFile ? (int)((ulong)value * 100 / (await _item.GetBasicPropertiesAsync()).Size) : (int)value;
              });
            });

            if (_item is StorageFile file)
            {

                await Drive.Provider.UploadFileAsync(file, _itemId, progress);
            }
            else if (_item is StorageFolder folder)
            {
                await Drive.Provider.UploadFolderAsync(folder, _itemId, progress);
            }
            Completed = true;
            IsUploading = false;

            //任务完成
            await Task.CompletedTask;
        }

        [RelayCommand]
        public static void PauseUpload()
        {
            // Microsoft Graph API 暂时不支持暂停上传。
        }

        [RelayCommand]
        public void CancelTask()
        {
            // https://github.com/microsoftgraph/msgraph-sdk-dotnet/issues/1678
            // Microsoft Graph v4 不支持取消上传，而 CommunityToolkit.Graph 仍在使用此版本。
            // 所以在工具库更新到 v5 之前，只能从列表中删除该任务。
            _manager.RemoveSelectedUploadTasks(this);
        }



        private readonly string _itemId;
        private readonly IStorageItem _item;
        private readonly TaskManagerViewModel _manager = Ioc.Default.GetService<TaskManagerViewModel>();
        private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();


        [ObservableProperty] private int _progress;
        [ObservableProperty] private bool _completed = false;
        [ObservableProperty] private bool _isUploading = true;

        public DriveViewModel Drive;
        public string Name => _item.Name;

    }

}
