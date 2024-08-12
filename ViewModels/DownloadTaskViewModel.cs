using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using Downloader;
using System;
using Microsoft.UI.Dispatching;
using System.Threading.Tasks;
using Windows.Storage;
using Microsoft.Graph.Models;
using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace OneDrive_Simple_Management_Tool.ViewModels
{
   public partial class DownloadTaskViewModel: ObservableObject
    {
        public DownloadTaskViewModel(DriveViewModel drive,  string itemId , StorageFile file)
        {
            _itemId = itemId;
            _file = file;
            Drive = drive;
        }

        public async Task StartDownload()
        {
            DriveItem item = await Drive.Provider.GetItem(_itemId);
            //从获取的 DriveItem 对象中提取下载 URL
            string downloadUrl = item.AdditionalData["@microsoft.graph.downloadUrl"].ToString();

            StartTime = DateTime.Now;
            _downloader = new();
            _downloader.DownloadFileCompleted += DownloadFileCompleted;
            _downloader.DownloadProgressChanged += DownloadProgressChanged;
            await _downloader.DownloadFileTaskAsync(downloadUrl,_file.Path);
        }

        private void DownloadFileCompleted(object sender, AsyncCompletedEventArgs e)
        {
            _dispatcher.TryEnqueue(() =>
            {
                // 更新状态变量
                if (_downloader.Status == DownloadStatus.Completed)
                {
                    Completed = true;
                    IsDownloading = false;
                }
            });
        }

        private void DownloadProgressChanged(object sender, DownloadProgressChangedEventArgs e)
        {
            // 根据 _updateInterval 控制更新频率，防止频繁更新导致界面卡顿
            if (DateTime.Now - _lastUpdate >= _updateInterval)
            {
                _lastUpdate = DateTime.Now;
                // 如果这里使用了double数据类型，会导致进度控件需要处理额外的数据，从而导致页面卡住。
                _dispatcher.TryEnqueue(() =>
                {
                    Progress = (int)e.ProgressPercentage;
                    DownloadedBytes = e.ReceivedBytesSize;
                    TotalBytes = e.TotalBytesToReceive;
                    DownloadSpeed = (long)e.BytesPerSecondSpeed;
                });
            }
        }

        [RelayCommand]
        public void PauseDownload()
        {
            _downloader.Pause();
            _pack=_downloader.Package;
            IsPaused = true;
            IsDownloading= false;
        }

        [RelayCommand]
        public async Task ResumeDownload()
        {
            IsPaused = false;
            IsDownloading = true;

            //如果检测到暂停时间大于等于1个小时，就重新获取下载链接
            if ((DateTime.Now - StartTime).TotalHours >= 1)
            {
                //刷新下载链接
                DriveItem item = await Drive.Provider.GetItem(_itemId);
                string downloadUrl = item.AdditionalData["@microsoft.graph.downloadUrl"].ToString();
                _pack.Address = downloadUrl;
            }
            if(_pack != null)
            {
                await _downloader.DownloadFileTaskAsync(_pack);
            }
            else
            {
                _downloader.Resume();
            }
        }

        [RelayCommand]
        public async Task CancelTaskAsync()
        {
            if (!Completed)
            {
                _downloader.CancelAsync();
                await _file.DeleteAsync();
            }
            //这里还要对传输任务管理器删除对应下载任务
            _manager.RemoveSelectedDownloadTasks(this);
        }

        [RelayCommand]
        public void OpenFolder()
        {
            //下载完成后查看文件所在目录
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{_file.Path}\"");
        }


        //分片大小为1MB
        public static readonly int chunkSize = 1024 * 1024;
        private DateTime _lastUpdate;
        //每秒更新当前下载速度
        private readonly TimeSpan _updateInterval = TimeSpan.FromMilliseconds(1000);
        private readonly string _itemId;
        private DriveViewModel Drive { get; }
        private readonly StorageFile _file;
        //确保整个程序只有一个taskmanager实例
        private readonly TaskManagerViewModel _manager = Ioc.Default.GetService<TaskManagerViewModel>();
        private DownloadService _downloader;
        private DownloadPackage _pack;
        //管理 UI 线程上的操作,保证UI线程持续保持响应
        private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();


        [ObservableProperty] private int _progress;
        [ObservableProperty] private bool _completed = false;
        [ObservableProperty] private bool _isDownloading = true;
        [ObservableProperty] private bool _isPaused = false;
        [ObservableProperty] private long _downloadedBytes = 0;
        [ObservableProperty] private long _totalBytes = 0;
        [ObservableProperty] private long _downloadSpeed = 0;

        public DateTime StartTime { get; private set; }
        public DateTime FinishTime { get; private set; }
        public string Name { get => _file.Name; }
    }
}
