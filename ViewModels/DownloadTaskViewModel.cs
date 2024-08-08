using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using Downloader;
using System;
using Microsoft.UI.Dispatching;
using System.Threading.Tasks;
using Windows.Storage;
using Microsoft.Graph.Models;

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
