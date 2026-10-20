using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using Windows.Storage;
using System.Threading.Tasks;
using System.Linq;
using System;
using System.Collections.Generic;
using OneDrive_Simple_Management_Tool.Models;
using OneDrive_Simple_Management_Tool.Services;
using OneDrive_Simple_Management_Tool.Helpers;
using Microsoft.UI.Dispatching;
using System.IO;
using System.ComponentModel;

namespace OneDrive_Simple_Management_Tool.ViewModels
{
    public partial class TaskManagerViewModel : ObservableObject
    {
        public ObservableCollection<DownloadTaskViewModel> DownloadTasks { get; } = new();
        public ObservableCollection<UploadTaskViewModel> UploadTasks { get; } = new();
        private Task<bool> _closeTask = Task.FromResult(false);
        public bool IsShuttingDown { get; private set; }
        private readonly List<(string Account, string Drive)> _blocked = new();
        private TransferStore _store;
        private TransferStore _pendingStore;
        private Action<TransferRecord> _restore;
        private bool _loadFailed;
        private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();
        private readonly HashSet<Guid> _unsaved = new();
        private readonly object _saveGate = new();
        private string _persistenceError = "";
        public string PersistenceError { get => _persistenceError; private set => SetProperty(ref _persistenceError, value); }
        private readonly HashSet<INotifyPropertyChanged> _observedTasks = new();

        public TaskManagerViewModel()
        {
            DownloadTasks.CollectionChanged += (_, _) => ObserveTasks();
            UploadTasks.CollectionChanged += (_, _) => ObserveTasks();
        }

        private void ObserveTasks()
        {
            var current = DownloadTasks.Cast<INotifyPropertyChanged>().Concat(UploadTasks).ToHashSet();
            foreach (var task in _observedTasks.Except(current)) task.PropertyChanged -= TaskChanged;
            foreach (var task in current.Except(_observedTasks)) task.PropertyChanged += TaskChanged;
            _observedTasks.Clear();
            _observedTasks.UnionWith(current);
            RefreshBatchCommands();
        }

        private void TaskChanged(object sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName is "State" or "CanRemove" or "CanPause") RefreshBatchCommands();
        }

        private void RefreshBatchCommands()
        {
            PauseAllCommand.NotifyCanExecuteChanged();
            ResumeAllCommand.NotifyCanExecuteChanged();
            RetryFailedCommand.NotifyCanExecuteChanged();
            ClearCompletedCommand.NotifyCanExecuteChanged();
        }

        private bool CanPauseAll(string direction) => !IsShuttingDown && (direction == "Upload"
            ? UploadTasks.Any(x => x.CanPause) : direction == "Download" && DownloadTasks.Any(x => x.CanPause));
        private bool CanResumeAll(string direction) => !IsShuttingDown && (direction == "Upload"
            ? UploadTasks.Any(x => x.CanResume) : direction == "Download" && DownloadTasks.Any(x => x.CanResume));
        private bool CanRetryFailed(string direction) => !IsShuttingDown && (direction == "Upload"
            ? UploadTasks.Any(x => x.CanRetry) : direction == "Download" && DownloadTasks.Any(x => x.CanRetry));
        private bool CanClearCompleted(string direction) => !IsShuttingDown && (direction == "Upload"
            ? UploadTasks.Any(x => x.Completed && x.CanRemove) : direction == "Download" && DownloadTasks.Any(x => x.Completed && x.CanRemove));

        [RelayCommand(CanExecute = nameof(CanPauseAll), AllowConcurrentExecutions = true)]
        public Task PauseAll(string direction)
        {
            if (!CanPauseAll(direction)) return Task.CompletedTask;
            return Task.WhenAll(direction == "Upload"
                ? UploadTasks.Where(x => x.CanPause).ToArray().Select(x => x.PauseUpload())
                : DownloadTasks.Where(x => x.CanPause).ToArray().Select(x => x.PauseDownload()));
        }

        [RelayCommand(CanExecute = nameof(CanResumeAll), AllowConcurrentExecutions = true)]
        public Task ResumeAll(string direction)
        {
            if (!CanResumeAll(direction)) return Task.CompletedTask;
            return Task.WhenAll(direction == "Upload"
                ? UploadTasks.Where(x => x.CanResume).ToArray().Select(x => x.ResumeUpload())
                : DownloadTasks.Where(x => x.CanResume).ToArray().Select(x => x.ResumeDownload()));
        }

        [RelayCommand(CanExecute = nameof(CanRetryFailed), AllowConcurrentExecutions = true)]
        public Task RetryFailed(string direction)
        {
            if (!CanRetryFailed(direction)) return Task.CompletedTask;
            return Task.WhenAll(direction == "Upload"
                ? UploadTasks.Where(x => x.CanRetry).ToArray().Select(x => x.RetryUpload())
                : DownloadTasks.Where(x => x.CanRetry).ToArray().Select(x => x.RetryDownload()));
        }

        [RelayCommand(CanExecute = nameof(CanClearCompleted), AllowConcurrentExecutions = true)]
        public Task ClearCompleted(string direction)
        {
            if (!CanClearCompleted(direction)) return Task.CompletedTask;
            return Task.WhenAll(direction == "Upload"
                ? UploadTasks.Where(x => x.Completed && x.CanRemove).ToArray().Select(x => x.CancelTaskAsync())
                : DownloadTasks.Where(x => x.Completed && x.CanRemove).ToArray().Select(x => x.CancelTaskAsync()));
        }

        public void Initialize(TransferStore store, Action<TransferRecord> restore)
        {
            if (_store != null) return;
            _pendingStore = store; _restore = restore;
            try
            {
                var records = store.Load();
                _store = store;
                foreach (var record in records) restore(record);
                _loadFailed = false;
                PersistenceError = "";
            }
            catch
            {
                foreach (var task in DownloadTasks) task.ReleaseRestoredResources();
                DownloadTasks.Clear(); UploadTasks.Clear();
                _store = null;
                _loadFailed = true;
                PersistenceError = "Transfer_LoadFailed".GetLocalized();
            }
        }

        [RelayCommand]
        public void RetryPersistence()
        {
            if (_loadFailed) Initialize(_pendingStore, _restore);
            else foreach (var record in UploadTasks.Select(x => x.Record).Concat(DownloadTasks.Select(x => x.Record)).Where(x => x != null)) SaveRecord(record);
        }

        public void SaveRecord(TransferRecord record)
        {
            if (_store == null) return;
            lock (_saveGate)
            {
                try { _store.Save(record); _unsaved.Remove(record.Id); }
                catch { _unsaved.Add(record.Id); }
                string error = _unsaved.Count == 0 ? "" : "Transfer_SaveFailed".GetLocalized();
                _dispatcher.TryEnqueue(() => PersistenceError = error);
            }
        }

        public void SaveRequired(TransferRecord record)
        {
            SaveRecord(record);
            lock (_saveGate) if (_unsaved.Contains(record.Id)) throw new IOException("Transfer checkpoint could not be saved.");
        }

        private bool RemoveRecord(TransferRecord record)
        {
            if (_store == null || record == null) return true;
            try
            {
                _store.Remove(record.Id);
                lock (_saveGate) _unsaved.Remove(record.Id);
                PersistenceError = _unsaved.Count == 0 ? "" : "Transfer_SaveFailed".GetLocalized();
                return true;
            }
            catch { PersistenceError = "Transfer_SaveFailed".GetLocalized(); return false; }
        }
        public bool CanUseAccount(string account, string drive) => !_loadFailed && !_blocked.Any(x => x.Account == account && (x.Drive == null || x.Drive == drive));

        public async Task StopAccountAsync(string account, string drive = null)
        {
            _blocked.Add((account, drive));
            try
            {
                // Stop only running, paused or waiting tasks; completed and failed cards stay as history without upload sessions.
                var uploads = UploadTasks.Where(x => x.Drive.Provider.HomeAccountId == account &&
                    (drive == null || x.Drive.Provider.DriveId == drive)).ToArray();
                await Task.WhenAll(uploads.Select(x => x.NeedsAccountStop ? x.CancelTaskAsync() : x.DiscardResumeAsync())
                    .Concat(DownloadTasks.Where(x => x.NeedsAccountStop && x.AccountId == account && (drive == null || x.DriveId == drive))
                        .ToArray().Select(x => x.CancelTaskAsync())));
            }
            finally { _blocked.Remove((account, drive)); }
        }

        // All admission and lifecycle calls are on the UI thread. Freeze admission even while asking.
        public Task<bool> RequestCloseAsync(Func<int, int, Task<bool>> confirm, Func<Task> finish)
        {
            if (!_closeTask.IsCompleted) return _closeTask;
            IsShuttingDown = true;
            RefreshBatchCommands();
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
                await Task.WhenAll(uploads.Select(task => task.StopForExitAsync())
                    .Concat(downloads.Select(task => task.StopForExitAsync())));
                foreach (var record in UploadTasks.Select(x => x.Record).Concat(DownloadTasks.Select(x => x.Record)).Where(x => x != null))
                    SaveRequired(record);
                await finish();
                ready = true;
                return true;
            }
            finally { if (!ready) { IsShuttingDown = false; RefreshBatchCommands(); } }
        }

        public async Task AddDownloadTask(DriveViewModel drive, string itemId, StorageFile file)
        {
            if (IsShuttingDown) return;
            if (!CanUseAccount(drive.Provider.HomeAccountId, drive.Provider.DriveId)) return;
            drive.Provider.CheckAccess();
            DownloadTaskViewModel downloadTask;
            if (_store == null) downloadTask = new(drive, itemId, file);
            else
            {
                var record = new TransferRecord { Direction = TransferDirection.Download, AccountId = drive.Provider.HomeAccountId,
                    DriveId = drive.Provider.DriveId, ItemId = itemId, Name = file.Name, Path = file.Path };
                SaveRequired(record);
                var session = new DownloadSession(file.Path, token => drive.Provider.GetDownloadSourceAsync(itemId, token),
                    new DownloadSessionOptions { StagingId = record.Id });
                downloadTask = new(file.Name, session, this, record.AccountId, record.DriveId);
                downloadTask.AttachRecord(record, SaveRecord);
            }
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
            if (_store != null)
            {
                var record = new TransferRecord { Direction = TransferDirection.Download, AccountId = source.AccountId,
                    DriveId = source.DriveId, Name = name, Path = destination, IsFolder = true };
                task.AttachRecord(record, SaveRecord);
                SaveRequired(record);
            }
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


        public bool RemoveSelectedDownloadTasks(DownloadTaskViewModel task)
        {
            if (!RemoveRecord(task.Record)) return false;
            DownloadTasks.Remove(task);
            return true;
        }

        //移除完成下载项
        public void RemoveCompletedDownloadTasks() 
        {
            foreach(var completedTask in DownloadTasks.Where(x => x.Completed).ToList())
            {
                if (RemoveSelectedDownloadTasks(completedTask)) completedTask.ReleaseRestoredResources();
            }
        }

        public async Task AddUploadTask(DriveViewModel drive, string itemId,IStorageItem item, UploadConflictResolver conflicts = null)
        {
            if (IsShuttingDown) return;
            if (!CanUseAccount(drive.Provider.HomeAccountId, drive.Provider.DriveId)) return;
            drive.Provider.CheckAccess();
            UploadTaskViewModel uploadTasks = new(drive, itemId, item, conflicts);
            var record = new TransferRecord { Direction = TransferDirection.Upload, AccountId = drive.Provider.HomeAccountId,
                DriveId = drive.Provider.DriveId, ItemId = itemId, Name = item.Name, Path = item.Path, IsFolder = item is StorageFolder };
            SaveRequired(record);
            uploadTasks.AttachRecord(record, SaveRecord);
            UploadTasks.Add(uploadTasks);
            await uploadTasks.StartUpload();
        }

        public bool RemoveSelectedUploadTasks(UploadTaskViewModel task)
        {
            if (!RemoveRecord(task.Record)) return false;
            UploadTasks.Remove(task);
            return true;
        }

    }
}
