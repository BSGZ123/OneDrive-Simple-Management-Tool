using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;
using Windows.Storage;
using System.Threading.Tasks;
using System.Linq;
using System;
using System.Collections.Generic;
using OneDrive_Simple_Management_Tool.Models;
using OneDrive_Simple_Management_Tool.Services;

namespace OneDrive_Simple_Management_Tool.ViewModels
{
    public class TaskManagerViewModel : ObservableObject
    {
        public ObservableCollection<DownloadTaskViewModel> DownloadTasks { get; } = new();
        public ObservableCollection<UploadTaskViewModel> UploadTasks { get; } = new();
        private Task<bool> _closeTask = Task.FromResult(false);
        public bool IsShuttingDown { get; private set; }
        private readonly List<(string Account, string Drive)> _blocked = new();
        public bool CanUseAccount(string account, string drive) => !_blocked.Any(x => x.Account == account && (x.Drive == null || x.Drive == drive));

        public async Task StopAccountAsync(string account, string drive = null)
        {
            _blocked.Add((account, drive));
            try
            {
                await Task.WhenAll(UploadTasks.Where(x => x.Drive.Provider.HomeAccountId == account &&
                        (drive == null || x.Drive.Provider.DriveId == drive)).ToArray().Select(x => x.CancelTaskAsync())
                    .Concat(DownloadTasks.Where(x => x.AccountId == account && (drive == null || x.DriveId == drive))
                        .ToArray().Select(x => x.StopForExitAsync())));
            }
            finally { _blocked.Remove((account, drive)); }
        }

        // All admission and lifecycle calls are on the UI thread. Freeze admission even while asking.
        public Task<bool> RequestCloseAsync(Func<int, int, Task<bool>> confirm, Func<Task> finish)
        {
            if (!_closeTask.IsCompleted) return _closeTask;
            IsShuttingDown = true;
            _closeTask = CloseCoreAsync(confirm, finish);
            return _closeTask;
        }

        private async Task<bool> CloseCoreAsync(Func<int, int, Task<bool>> confirm, Func<Task> finish)
        {
            await Task.Yield();
            bool ready = false;
            try
            {
                var uploads = UploadTasks.Where(task => task.NeedsExitProtection).ToArray();
                var downloads = DownloadTasks.Where(task => task.NeedsExitProtection).ToArray();
                if ((uploads.Length > 0 || downloads.Length > 0) && !await confirm(uploads.Length, downloads.Length))
                    return false;
                // Start cancellation for every task before awaiting any one of them.
                await Task.WhenAll(uploads.Select(task => task.CancelTaskAsync())
                    .Concat(downloads.Select(task => task.StopForExitAsync())));
                await finish();
                ready = true;
                return true;
            }
            finally { if (!ready) IsShuttingDown = false; }
        }

        public async Task AddDownloadTask(DriveViewModel drive, string itemId, StorageFile file)
        {
            if (IsShuttingDown) return;
            if (!CanUseAccount(drive.Provider.HomeAccountId, drive.Provider.DriveId)) return;
            drive.Provider.CheckAccess();
            DownloadTaskViewModel downloadTask = new(drive, itemId, file);
            DownloadTasks.Add(downloadTask);
            await downloadTask.StartDownload();
        }

        public DownloadTaskViewModel QueueDownloadBatch(IDownloadTreeSource source, IEnumerable<string> ids, string destination,
            string name, IReadOnlyDictionary<string, string> names = null)
        {
            if (IsShuttingDown) return null;
            if (!CanUseAccount(source.AccountId, source.DriveId)) return null;
            source.CheckAccess();
            var session = new DownloadBatchSession(source, ids, destination, names: names);
            var task = new DownloadTaskViewModel(name, session, this, source.AccountId, source.DriveId);
            DownloadTasks.Add(task);
            return task;
        }

        //重新开始所有下载任务
        public async Task StartAllDownloadTasks()
        {
            if (IsShuttingDown) return;
            await Task.WhenAll(DownloadTasks.ToList()
                .Where(task => task.CanResume || task.CanRetry)
                .Select(task => task.StartDownload()));
        }


        public void RemoveSelectedDownloadTasks(DownloadTaskViewModel task)
        {
            DownloadTasks.Remove(task);
        }

        //移除完成下载项
        public void RemoveCompletedDownloadTasks() 
        {
            foreach(var completedTask in DownloadTasks.Where(x => x.Completed).ToList())
            {
                DownloadTasks.Remove(completedTask);
            }
        }

        public async Task AddUploadTask(DriveViewModel drive, string itemId,IStorageItem item, UploadConflictResolver conflicts = null)
        {
            if (IsShuttingDown) return;
            if (!CanUseAccount(drive.Provider.HomeAccountId, drive.Provider.DriveId)) return;
            drive.Provider.CheckAccess();
            UploadTaskViewModel uploadTasks = new(drive, itemId, item, conflicts);
            UploadTasks.Add(uploadTasks);
            await uploadTasks.StartUpload();
        }

        public void RemoveSelectedUploadTasks(UploadTaskViewModel task)
        {
            UploadTasks.Remove(task);
        }

    }
}
