using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using OneDrive_Simple_Management_Tool.Helpers;
using OneDrive_Simple_Management_Tool.Models;
using OneDrive_Simple_Management_Tool.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.Storage;

namespace OneDrive_Simple_Management_Tool.ViewModels
{
    public partial class DownloadTaskViewModel : ObservableObject
    {
        private readonly IDownloadSession _session;
        private readonly TaskManagerViewModel _manager;
        private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();
        private long _revision;
        private long _lastProgressTick;
        private DownloadTaskState _lastReportedState;
        public TransferRecord Record { get; private set; }
        public TransferTaskControls Controls { get; }

        public DownloadTaskViewModel(DriveViewModel drive, string itemId, StorageFile file)
            : this(file.Name, new DownloadSession(file.Path, token => drive.Provider.GetDownloadSourceAsync(itemId, token)),
                accountId: drive.Provider.HomeAccountId, driveId: drive.Provider.DriveId) { }

        public DownloadTaskViewModel(string name, IDownloadSession session, TaskManagerViewModel manager = null, string accountId = null, string driveId = null)
        {
            AccountId = accountId;
            DriveId = driveId;
            Name = name;
            _manager = manager ?? Ioc.Default.GetRequiredService<TaskManagerViewModel>();
            _session = session;
            Controls = new(session is DownloadSession single ? single.Policy : session is DownloadBatchSession batch ? batch.Policy : new TransferPolicy());
            _session.Changed += OnSessionChanged;
            ApplySnapshot(_session.Snapshot);
        }

        public void AttachRecord(TransferRecord record, Action<TransferRecord> save, bool restore = false)
        {
            Record = record;
            Controls.AttachRecord(record, save);
            if (_session is DownloadBatchSession batch)
            {
                batch.AttachRecord(record, save, restore);
                batch.RequireCheckpoint = () => _manager.SaveRequired(record);
            }
            else if (_session is DownloadSession single)
            {
                single.AttachRecord(record, save, restore);
                single.RequireCheckpoint = () => _manager.SaveRequired(record);
            }
            ApplySnapshot(_session.Snapshot);
        }

        public void ReleaseRestoredResources()
        {
            Controls.Dispose();
            _session.Changed -= OnSessionChanged;
            if (_session is DownloadBatchSession batch) batch.ReleaseReservations();
        }

        public string Name { get; }
        public string AccountId { get; }
        public string DriveId { get; }
        public string DestinationPath => _session.DestinationPath;
        // Read the session directly: a queued UI snapshot can lag behind a finalization or pause.
        public bool NeedsExitProtection => _session.Snapshot.State is not (DownloadTaskState.Completed or DownloadTaskState.Cancelled);
        public DateTime StartTime { get; private set; }
        public DateTime? FinishTime { get; private set; }

        public bool Completed => State == DownloadTaskState.Completed;
        public bool HasFailed => State == DownloadTaskState.Failed;
        public bool IsPaused => State == DownloadTaskState.Paused;
        public bool CanPause => State is DownloadTaskState.Pending or DownloadTaskState.Preparing or DownloadTaskState.Downloading or DownloadTaskState.Retrying;
        public bool CanResume => State is DownloadTaskState.Pending or DownloadTaskState.Paused or DownloadTaskState.Interrupted;
        public bool CanRetry => HasFailed;
        public bool CanRemove => State is not (DownloadTaskState.Cancelling or DownloadTaskState.Finalizing);
        public bool IsIndeterminate => State is DownloadTaskState.Preparing or DownloadTaskState.Retrying or DownloadTaskState.Finalizing;
        public bool IsBatch => _session is DownloadBatchSession;
        public bool CanCancelBatch => IsBatch && State is not (DownloadTaskState.Completed or DownloadTaskState.Cancelled or DownloadTaskState.Cancelling);
        public bool ShowRemove => !IsBatch || State is DownloadTaskState.Completed or DownloadTaskState.Cancelled;
        public string StatusText => (IsBatch && State == DownloadTaskState.Preparing ? "DownloadBatch_Scanning" : $"DownloadState_{State}").GetLocalized();
        public IReadOnlyList<TransferFailureRow> Failures { get; private set; } = Array.Empty<TransferFailureRow>();
        public bool HasFailures => Failures.Count > 0;
        [ObservableProperty] private string _batchSummary = string.Empty;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Completed), nameof(HasFailed), nameof(IsPaused), nameof(CanPause),
            nameof(CanResume), nameof(CanRetry), nameof(CanRemove), nameof(IsIndeterminate), nameof(StatusText), nameof(CanCancelBatch), nameof(ShowRemove))]
        [NotifyCanExecuteChangedFor(nameof(PauseDownloadCommand), nameof(ResumeDownloadCommand),
            nameof(RetryDownloadCommand), nameof(CancelTaskCommand), nameof(OpenFolderCommand), nameof(CancelDownloadCommand))]
        private DownloadTaskState _state = DownloadTaskState.Pending;

        [ObservableProperty] private int _progress;
        [ObservableProperty] private long _downloadedBytes;
        [ObservableProperty] private long _totalBytes;
        [ObservableProperty] private long _downloadSpeed;
        [ObservableProperty] private string _errorMessage = string.Empty;
        [ObservableProperty] private string _notice = string.Empty;

        public async Task StartDownload()
        {
            if (_manager.IsShuttingDown) return;
            if (StartTime == default) StartTime = DateTime.Now;
            await _session.StartAsync();
            ApplySnapshot(_session.Snapshot);
        }

        [RelayCommand(CanExecute = nameof(CanPause))]
        public async Task PauseDownload()
        {
            await _session.PauseAsync();
            ApplySnapshot(_session.Snapshot);
        }

        [RelayCommand(CanExecute = nameof(CanResume))]
        public Task ResumeDownload() => StartDownload();

        [RelayCommand(CanExecute = nameof(CanRetry))]
        public Task RetryDownload() => StartDownload();

        [RelayCommand(CanExecute = nameof(CanCancelBatch))]
        public async Task CancelDownload()
        {
            await _session.CancelAsync();
            ApplySnapshot(_session.Snapshot);
        }

        [RelayCommand(CanExecute = nameof(CanRemove))]
        public async Task CancelTaskAsync()
        {
            await _session.CancelAsync();
            ApplySnapshot(_session.Snapshot);
            if (State is DownloadTaskState.Cancelled or DownloadTaskState.Completed)
            {
                if (_manager.RemoveSelectedDownloadTasks(this)) { _session.Changed -= OnSessionChanged; Controls.Dispose(); }
            }
        }

        public async Task StopForExitAsync()
        {
            if (Record != null)
            {
                if (_session is DownloadBatchSession batch) await batch.StopForExitAsync();
                else if (_session is DownloadSession single) await single.StopForExitAsync();
                ApplySnapshot(_session.Snapshot);
                return;
            }
            await CancelTaskAsync();
            if (_session.Snapshot.State is not (DownloadTaskState.Cancelled or DownloadTaskState.Completed))
                throw new InvalidOperationException("Download cleanup did not finish.");
        }

        [RelayCommand(CanExecute = nameof(Completed))]
        public void OpenFolder() => System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{DestinationPath}\"");

        private void OnSessionChanged(DownloadSnapshot snapshot)
        {
            // Throttle progress only. Every transition and final byte count reaches the UI.
            long now = Environment.TickCount64;
            if (_lastReportedState == snapshot.State && snapshot.State == DownloadTaskState.Downloading &&
                snapshot.ReceivedBytes < snapshot.TotalBytes &&
                now - System.Threading.Interlocked.Read(ref _lastProgressTick) < 200) return;
            _lastReportedState = snapshot.State;
            System.Threading.Interlocked.Exchange(ref _lastProgressTick, now);
            _dispatcher.TryEnqueue(() => ApplySnapshot(snapshot));
        }

        private void ApplySnapshot(DownloadSnapshot snapshot)
        {
            if (snapshot.Revision <= _revision) return;
            _revision = snapshot.Revision;
            State = snapshot.State;
            DownloadedBytes = snapshot.ReceivedBytes;
            TotalBytes = snapshot.TotalBytes;
            DownloadSpeed = snapshot.BytesPerSecond;
            Progress = Completed ? 100 : TotalBytes == 0 ? 0 : (int)Math.Clamp(DownloadedBytes * 100.0 / TotalBytes, 0, 99);
            Controls.Update(DownloadedBytes, TotalBytes, State == DownloadTaskState.Downloading,
                snapshot.Batch == null || (snapshot.Batch.DiscoveryComplete && snapshot.Batch.Failures.Count == 0), Completed);
            ErrorMessage = snapshot.Failure == DownloadFailure.None ? string.Empty :
                ($"DownloadError_{snapshot.Failure}").GetLocalized();
            Notice = IsBatch ? "DownloadBatch_KeepBoth".GetLocalized() : snapshot.Restarted ? "Download_Restarted".GetLocalized() : string.Empty;
            if (State == DownloadTaskState.Interrupted) Notice = "Transfer_RestartNotice".GetLocalized();
            if (snapshot.Batch is { } batch)
            {
                BatchSummary = string.Format("DownloadBatch_Summary".GetLocalized(), batch.CompletedFiles, batch.Files, batch.Folders, batch.Failures.Count);
                if (!batch.DiscoveryComplete) BatchSummary += " " + "DownloadBatch_IncompleteScan".GetLocalized();
                Failures = batch.Failures.Select(failure => new TransferFailureRow(failure.Path, ($"DownloadError_{failure.Failure}").GetLocalized(),
                    async () =>
                    {
                        if (_manager.IsShuttingDown || State is not (DownloadTaskState.Failed or DownloadTaskState.Interrupted or DownloadTaskState.Paused)) return;
                        if (failure.ItemId != null && _session is DownloadBatchSession session) await session.RetryItemAsync(failure.ItemId);
                        else await _session.StartAsync();
                        ApplySnapshot(_session.Snapshot);
                    }, () => State is DownloadTaskState.Failed or DownloadTaskState.Interrupted or DownloadTaskState.Paused)).ToArray();
                OnPropertyChanged(nameof(Failures));
                OnPropertyChanged(nameof(HasFailures));
            }
            if (Completed) FinishTime = DateTime.Now;
        }
    }

}
