using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using OneDrive_Simple_Management_Tool.Services;
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
            if (IsUploading || Completed)
            {
                return;
            }

            IsUploading = true;
            HasFailed = false;
            ErrorMessage = string.Empty;
            Progress = 0;
            int attempt = ++_uploadAttempt;

            try
            {
                ulong totalBytes = _item is StorageFolder folder
                    ? await Utils.GetFolderSize(folder)
                    : (await _item.GetBasicPropertiesAsync()).Size;

                IProgress<long> progress = new Progress<long>(value =>
                {
                    _dispatcher.TryEnqueue(() =>
                    {
                        // Queued reports must not overwrite a terminal state or a newer attempt.
                        if (IsUploading && attempt == _uploadAttempt)
                        {
                            int percentage = totalBytes == 0 ? 0 : (int)Math.Clamp(value * 100.0 / totalBytes, 0, 99);
                            Progress = Math.Max(Progress, percentage);
                        }
                    });
                });

                if (_item is StorageFile file)
                {
                    await Drive.Provider.UploadFileAsync(file, _itemId, progress);
                }
                else if (_item is StorageFolder uploadFolder)
                {
                    await Drive.Provider.UploadFolderAsync(uploadFolder, _itemId, progress);
                }
                else
                {
                    throw new NotSupportedException("Only files and folders can be uploaded.");
                }

                Progress = 100;
                Completed = true;
            }
            catch (Exception ex)
            {
                ErrorMessage = ex.Message;
                HasFailed = true;
            }
            finally
            {
                IsUploading = false;
            }
        }

        [RelayCommand]
        public static void PauseUpload()
        {
            // 暂停和恢复上传尚未接入任务状态。
        }

        [RelayCommand]
        public void CancelTask()
        {
            // 当前仅移除任务记录；真正取消上传还需接入 CancellationToken。
            _manager.RemoveSelectedUploadTasks(this);
        }



        private readonly string _itemId;
        private readonly IStorageItem _item;
        private readonly TaskManagerViewModel _manager = Ioc.Default.GetService<TaskManagerViewModel>();
        private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();
        private int _uploadAttempt;


        [ObservableProperty] private int _progress;
        [ObservableProperty] private bool _completed = false;
        [ObservableProperty] private bool _isUploading;
        [ObservableProperty] private bool _hasFailed;
        [ObservableProperty] private string _errorMessage = string.Empty;

        public DriveViewModel Drive;
        public string Name => _item.Name;

    }

}
