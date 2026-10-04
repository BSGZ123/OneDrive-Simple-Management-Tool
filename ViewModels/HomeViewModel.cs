using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OneDrive_Simple_Management_Tool.Helpers;
using OneDrive_Simple_Management_Tool.Models;
using OneDrive_Simple_Management_Tool.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OneDrive_Simple_Management_Tool.ViewModels
{
    public partial class HomeViewModel(IHomeDriveService drives, TaskManagerViewModel tasks, FolderSyncViewModel sync,
        BookmarkViewModel bookmarks) : ObservableObject
    {
        private readonly HashSet<INotifyPropertyChanged> _observed = new();
        private CancellationTokenSource _request;
        private bool _active;
        private int _activationVersion;
        public ObservableCollection<HomeDriveViewModel> Drives { get; } = new();
        public BookmarkViewModel Bookmarks { get; } = bookmarks;
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
        private bool _isLoading;
        [ObservableProperty] private bool _isEmpty;
        [ObservableProperty] private string _errorMessage = "";
        [ObservableProperty] private string _lastRefreshed = "";
        [ObservableProperty] private int _activeTransfers;
        [ObservableProperty] private int _waitingTransfers;
        [ObservableProperty] private int _failedTransfers;
        [ObservableProperty] private int _completedTransfers;
        [ObservableProperty] private int _cancelledTransfers;
        [ObservableProperty] private int _totalTransfers;
        [ObservableProperty] private int _syncCount;
        [ObservableProperty] private int _activeSyncs;
        [ObservableProperty] private int _pausedSyncs;
        [ObservableProperty] private int _syncIssues;
        [ObservableProperty] private string _syncErrorMessage = "";
        public bool HasError => ErrorMessage.Length > 0;
        public bool HasSyncError => SyncErrorMessage.Length > 0;
        public bool HasNoTransfers => TotalTransfers == 0;
        public bool HasNoSyncs => SyncCount == 0 && !HasSyncError;
        public string TransferSummary => string.Format("Home_TransferSummary".GetLocalized(),
            ActiveTransfers, WaitingTransfers, FailedTransfers, CompletedTransfers, CancelledTransfers);
        public string SyncSummary => string.Format("Home_SyncSummary".GetLocalized(), SyncCount, ActiveSyncs, PausedSyncs, SyncIssues);
        partial void OnErrorMessageChanged(string value) => OnPropertyChanged(nameof(HasError));

        public async Task ActivateAsync()
        {
            if (_active) return;
            _active = true;
            int activation = ++_activationVersion;
            tasks.DownloadTasks.CollectionChanged += CollectionChanged;
            tasks.UploadTasks.CollectionChanged += CollectionChanged;
            sync.Bindings.CollectionChanged += CollectionChanged;
            sync.PropertyChanged += SyncChanged;
            ObserveItems();
            UpdateSummaries();
            // Local bookmarks stay available while quota and sync initialization are pending.
            var bookmarkLoad = Bookmarks.ActivateAsync();
            await sync.InitializeAsync();
            if (_active && activation == _activationVersion) await RefreshOverviewAsync(false);
            await bookmarkLoad;
        }

        public void Deactivate()
        {
            _active = false;
            _activationVersion++;
            _request?.Cancel();
            _request = null;
            Bookmarks.Deactivate();
            IsLoading = false;
            tasks.DownloadTasks.CollectionChanged -= CollectionChanged;
            tasks.UploadTasks.CollectionChanged -= CollectionChanged;
            sync.Bindings.CollectionChanged -= CollectionChanged;
            sync.PropertyChanged -= SyncChanged;
            foreach (var item in _observed) item.PropertyChanged -= ItemChanged;
            _observed.Clear();
        }

        private bool CanRefresh() => !IsLoading;

        [RelayCommand(CanExecute = nameof(CanRefresh))]
        private Task RefreshAsync() => RefreshOverviewAsync(true);

        private async Task RefreshOverviewAsync(bool reloadBookmarks)
        {
            if (!_active) return;
            _request?.Cancel();
            using var request = new CancellationTokenSource();
            _request = request;
            IsLoading = true;
            IsEmpty = false;
            ErrorMessage = LastRefreshed = "";
            Drives.Clear();
            var bookmarkLoad = reloadBookmarks && Bookmarks.ReloadCommand.CanExecute(null)
                ? Bookmarks.ReloadCommand.ExecuteAsync(null) : Task.CompletedTask;
            try
            {
                var configured = await drives.LoadDrivesAsync(request.Token).WaitAsync(request.Token);
                if (_request != request) return;
                foreach (var drive in configured) Drives.Add(new HomeDriveViewModel(drive));
                IsEmpty = Drives.Count == 0;
                // Limit concurrent Graph requests; each card completes or fails independently.
                using var slots = new SemaphoreSlim(3);
                await Task.WhenAll(Drives.Select(card => LoadQuotaAsync(card, slots, request)).ToList());
                if (_request == request)
                    LastRefreshed = string.Format("Home_LastRefreshed".GetLocalized(), DateTime.Now.ToString("t"));
            }
            catch (OperationCanceledException) when (request.IsCancellationRequested) { }
            catch (Exception exception)
            {
                if (_request == request) ErrorMessage = Message(exception, "Home_ConfigurationFailed");
            }
            finally
            {
                await bookmarkLoad;
                if (_request == request) { _request = null; IsLoading = false; }
            }
        }

        private async Task LoadQuotaAsync(HomeDriveViewModel card, SemaphoreSlim slots, CancellationTokenSource request)
        {
            await slots.WaitAsync(request.Token);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(request.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                var quota = await drives.GetQuotaAsync(card.Drive, timeout.Token).WaitAsync(timeout.Token);
                if (_request == request) card.Apply(quota);
            }
            catch (OperationCanceledException) when (request.IsCancellationRequested) { }
            catch (Exception exception)
            {
                if (_request == request) card.Fail(Message(exception,
                    exception is OperationCanceledException ? "Home_QuotaTimeout" : "Home_QuotaFailed"));
            }
            finally { slots.Release(); }
        }

        private static string Message(Exception exception, string fallback) =>
            (exception is HomeOverviewException home ? home.ResourceKey : fallback).GetLocalized();

        private void CollectionChanged(object sender, NotifyCollectionChangedEventArgs args)
        {
            ObserveItems();
            UpdateSummaries();
        }

        private void ObserveItems()
        {
            var current = tasks.DownloadTasks.Cast<INotifyPropertyChanged>().Concat(tasks.UploadTasks)
                .Concat(sync.Bindings).ToHashSet();
            foreach (var item in _observed.Except(current).ToList())
            {
                item.PropertyChanged -= ItemChanged;
                _observed.Remove(item);
            }
            foreach (var item in current.Except(_observed))
            {
                item.PropertyChanged += ItemChanged;
                _observed.Add(item);
            }
        }

        private void ItemChanged(object sender, PropertyChangedEventArgs args)
        {
            if (string.IsNullOrEmpty(args.PropertyName) || args.PropertyName is "State" or "IsActive" or "Enabled" or "HasIssues" or "CommandError")
                UpdateSummaries();
        }

        private void SyncChanged(object sender, PropertyChangedEventArgs args)
        {
            if (string.IsNullOrEmpty(args.PropertyName) || args.PropertyName == nameof(FolderSyncViewModel.ErrorMessage)) UpdateSummaries();
        }

        private void UpdateSummaries()
        {
            var downloads = tasks.DownloadTasks;
            var uploads = tasks.UploadTasks;
            TotalTransfers = downloads.Count + uploads.Count;
            ActiveTransfers = downloads.Count(t => t.State is DownloadTaskState.Preparing or DownloadTaskState.Downloading or
                DownloadTaskState.Retrying or DownloadTaskState.Pausing or DownloadTaskState.Finalizing or DownloadTaskState.Cancelling) +
                uploads.Count(t => t.State is UploadTaskState.Preparing or UploadTaskState.Uploading or UploadTaskState.Cancelling);
            WaitingTransfers = downloads.Count(t => t.State is DownloadTaskState.Pending or DownloadTaskState.Paused) +
                uploads.Count(t => t.State == UploadTaskState.Pending);
            FailedTransfers = downloads.Count(t => t.State == DownloadTaskState.Failed) + uploads.Count(t => t.State == UploadTaskState.Failed);
            CompletedTransfers = downloads.Count(t => t.State == DownloadTaskState.Completed) + uploads.Count(t => t.State == UploadTaskState.Completed);
            CancelledTransfers = downloads.Count(t => t.State == DownloadTaskState.Cancelled) + uploads.Count(t => t.State == UploadTaskState.Cancelled);
            SyncCount = sync.Bindings.Count;
            ActiveSyncs = sync.Bindings.Count(t => t.IsActive);
            PausedSyncs = sync.Bindings.Count(t => !t.Enabled);
            SyncIssues = sync.Bindings.Count(t => t.HasIssues || !string.IsNullOrEmpty(t.CommandError));
            SyncErrorMessage = sync.ErrorMessage;
            OnPropertyChanged(nameof(TransferSummary));
            OnPropertyChanged(nameof(SyncSummary));
            OnPropertyChanged(nameof(HasNoTransfers));
            OnPropertyChanged(nameof(HasNoSyncs));
            OnPropertyChanged(nameof(HasSyncError));
        }
    }
}
