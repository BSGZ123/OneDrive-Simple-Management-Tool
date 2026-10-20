using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using OneDrive_Simple_Management_Tool.Models;
using OneDrive_Simple_Management_Tool.Services;
using OneDrive_Simple_Management_Tool.Helpers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;

namespace OneDrive_Simple_Management_Tool.ViewModels
{
    public partial class UploadTaskViewModel : ObservableObject
    {
        private readonly string _itemId;
        private readonly IStorageItem _item;
        private readonly TaskManagerViewModel _manager = Ioc.Default.GetService<TaskManagerViewModel>();
        private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();
        private CancellationTokenSource _cancellation;
        private Task _activeTask = Task.CompletedTask;
        private Task _cancelTask = Task.CompletedTask;
        private Task _pauseTask = Task.CompletedTask;
        private int _uploadAttempt;
        private bool _removalRequested;
        private readonly UploadConflictResolver _conflicts;
        public TransferRecord Record { get; private set; }
        private Action<TransferRecord> _save;
        private string _retryKey;
        private bool _pauseRequested;
        private long _uploadedBytes;
        private long _totalBytes;
        private bool _totalKnown;
        public TransferTaskControls Controls { get; } = new(new TransferPolicy());

        public UploadTaskViewModel(DriveViewModel drive, string itemId, IStorageItem item, UploadConflictResolver conflicts = null)
        {
            Drive = drive;
            _item = item;
            _itemId = itemId;
            if (conflicts != null)
            {
                _conflicts = new UploadConflictResolver(async (conflict, token) =>
                {
                    State = UploadTaskState.WaitingForConflict;
                    try
                    {
                        var choice = await conflicts.ResolveAsync(conflict, token);
                        if (choice == UploadConflictChoice.Cancel) _cancellation?.Cancel();
                        return new(choice);
                    }
                    finally
                    {
                        if (!token.IsCancellationRequested) State = UploadTaskState.Uploading;
                    }
                });
            }
        }

        public DriveViewModel Drive { get; }
        public string Name => Record?.Name ?? _item.Name;
        public bool IsInterrupted => State == UploadTaskState.Interrupted;
        public bool CanRetry => !_removalRequested && State == UploadTaskState.Failed;
        public bool CanResume => !_removalRequested && State is UploadTaskState.Pending or UploadTaskState.Paused or UploadTaskState.Interrupted;
        public bool CanPause => !_removalRequested && Record != null && State is UploadTaskState.Pending or UploadTaskState.Preparing or UploadTaskState.Uploading or UploadTaskState.WaitingForConflict;
        public bool IsPaused => State == UploadTaskState.Paused;
        public bool IsPausing => State == UploadTaskState.Pausing;
        public IReadOnlyList<TransferFailureRow> Failures { get; private set; } = Array.Empty<TransferFailureRow>();
        public bool HasFailures => Failures.Count > 0;

        public void AttachRecord(TransferRecord record, Action<TransferRecord> save, bool restore = false)
        {
            Record = record; _save = save;
            Controls.AttachRecord(record, save);
            if (restore) State = record.UploadState is UploadTaskState.Completed or UploadTaskState.Skipped or UploadTaskState.Cancelled or UploadTaskState.Failed or UploadTaskState.Paused
                ? record.UploadState : UploadTaskState.Interrupted;
            OnPropertyChanged(nameof(CanPause));
            PauseUploadCommand.NotifyCanExecuteChanged();
            UpdateRecordedProgress();
        }

        partial void OnStateChanged(UploadTaskState value)
        {
            Controls.Update(_uploadedBytes, _totalBytes, value == UploadTaskState.Uploading, _totalKnown, Completed);
            if (Record == null) return;
            Record.UploadState = value;
            _save(Record);
            foreach (var row in Failures) row.RetryCommand.NotifyCanExecuteChanged();
        }
        public bool Completed => State == UploadTaskState.Completed;
        public bool HasFailed => State == UploadTaskState.Failed;
        public bool IsPreparing => State == UploadTaskState.Preparing;
        public bool IsCancelling => State == UploadTaskState.Cancelling;
        public bool IsUploading => State is UploadTaskState.Preparing or UploadTaskState.Uploading or UploadTaskState.WaitingForConflict;
        public bool IsWaitingForConflict => State == UploadTaskState.WaitingForConflict;
        public bool IsSkipped => State == UploadTaskState.Skipped;
        public bool IsCancelled => State == UploadTaskState.Cancelled;
        public bool IsTerminal => State is UploadTaskState.Completed or UploadTaskState.Skipped or UploadTaskState.Cancelled;
        public bool NeedsExitProtection => !_activeTask.IsCompleted || !_cancelTask.IsCompleted || !IsTerminal;
        // Account stop keeps ended cards and their errors; DiscardResumeAsync drops their upload sessions.
        public bool NeedsAccountStop => !_activeTask.IsCompleted || !_cancelTask.IsCompleted || !(IsTerminal || HasFailed);
        public bool CanRemove => !_removalRequested;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Completed), nameof(HasFailed), nameof(IsPreparing), nameof(IsCancelling), nameof(IsUploading),
            nameof(IsWaitingForConflict), nameof(IsSkipped), nameof(IsCancelled), nameof(IsTerminal), nameof(IsInterrupted), nameof(CanRetry),
            nameof(CanResume), nameof(CanPause), nameof(IsPaused), nameof(IsPausing))]
        [NotifyCanExecuteChangedFor(nameof(RetryUploadCommand), nameof(ResumeUploadCommand), nameof(PauseUploadCommand))]
        private UploadTaskState _state = UploadTaskState.Pending;

        [ObservableProperty] private int _progress;
        [ObservableProperty] private string _errorMessage = string.Empty;
        [ObservableProperty] private string _notice = string.Empty;

        // Like the task collection, lifecycle commands run on the UI thread.
        public Task StartUpload()
        {
            if (_manager.IsShuttingDown || _removalRequested || !_activeTask.IsCompleted || !_pauseTask.IsCompleted ||
                State is not (UploadTaskState.Pending or UploadTaskState.Failed or UploadTaskState.Interrupted or UploadTaskState.Paused))
            {
                return _activeTask;
            }

            _cancellation = new CancellationTokenSource();
            _activeTask = RunUploadAsync(_cancellation.Token, ++_uploadAttempt);
            ErrorMessage = string.Empty;
            Notice = string.Empty;
            if (Record == null) Progress = 0;
            else UpdateRecordedProgress();
            State = UploadTaskState.Preparing;
            return _activeTask;
        }

        private async Task RunUploadAsync(CancellationToken cancellationToken, int attempt)
        {
            // Install _activeTask before any callbacks can request cancellation or another start.
            await Task.Yield();
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Record != null)
                {
                    Drive.Provider.CheckAccess();
                    var session = new UploadRecordSession(Record, new StorageUploadRecordSource(Drive.Provider, Record, _conflicts,
                        () => _manager.SaveRequired(Record), Controls.Policy),
                        () => _manager.SaveRequired(Record), UpdateRecordedProgress, FileOperationErrors.GetKey);
                    State = UploadTaskState.Uploading;
                    var recordProgress = new Progress<long>(bytes =>
                    {
                        if (State != UploadTaskState.Uploading || attempt != _uploadAttempt) return;
                        long total = Record.Uploads.Where(x => !x.IsFolder).Sum(x => x.Size);
                        Progress = total == 0 ? 0 : (int)Math.Clamp(bytes * 100.0 / total, 0, 99);
                        UpdateTransferProgress(bytes, total, Record.Uploads.All(x => (!x.IsFolder || x.Enumerated) && x.State != TransferEntryState.Failed));
                    });
                    await session.RunAsync(recordProgress, cancellationToken, _retryKey);
                    State = Record.Uploads.Any(x => x.State is not (TransferEntryState.Completed or TransferEntryState.Skipped)) ? UploadTaskState.Failed :
                        Record.Uploads.All(x => x.State == TransferEntryState.Skipped) ? UploadTaskState.Skipped : UploadTaskState.Completed;
                    UpdateRecordedProgress();
                    return;
                }
                ulong totalBytes = _item is StorageFolder folder
                    ? await Utils.GetFolderSize(folder, cancellationToken)
                    : (await _item.GetBasicPropertiesAsync().AsTask(cancellationToken)).Size;
                cancellationToken.ThrowIfCancellationRequested();

                UpdateTransferProgress(0, checked((long)totalBytes), true);
                IProgress<long> progress = new Progress<long>(value =>
                {
                    _dispatcher.TryEnqueue(() =>
                    {
                        // Queued reports must not overwrite cancellation, a terminal state or a newer attempt.
                        if (State == UploadTaskState.Uploading && attempt == _uploadAttempt)
                        {
                            int percentage = totalBytes == 0 ? 0 : (int)Math.Clamp(value * 100.0 / totalBytes, 0, 99);
                            Progress = Math.Max(Progress, percentage);
                            UpdateTransferProgress(value, checked((long)totalBytes), true);
                        }
                    });
                });

                State = UploadTaskState.Uploading;
                UploadOutcome outcome;
                if (_item is StorageFile file)
                {
                    outcome = await Drive.Provider.UploadFileAsync(file, _itemId, progress, cancellationToken, _conflicts, Controls.Policy);
                }
                else if (_item is StorageFolder uploadFolder)
                {
                    outcome = await Drive.Provider.UploadFolderAsync(uploadFolder, _itemId, progress, cancellationToken, _conflicts, Controls.Policy);
                }
                else
                {
                    throw new NotSupportedException("Only files and folders can be uploaded.");
                }

                if (outcome.Skipped > 0) Notice = string.Format("Upload_SkippedCount".GetLocalized(), outcome.Skipped);
                State = outcome.Uploaded == 0 ? UploadTaskState.Skipped : UploadTaskState.Completed;
                Progress = Completed ? 100 : 0;
            }
            catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested || exception is UploadAbortedException)
            {
                if (Record != null)
                {
                    foreach (var entry in Record.Uploads.Where(x => x.State == TransferEntryState.Working)) entry.State = TransferEntryState.Pending;
                }
                bool allConfirmed = Record?.Uploads.Count > 0 && Record.Uploads.All(x => x.State is TransferEntryState.Completed or TransferEntryState.Skipped);
                State = allConfirmed ? (Record.Uploads.All(x => x.State == TransferEntryState.Skipped) ? UploadTaskState.Skipped : UploadTaskState.Completed) :
                    _pauseRequested && !_removalRequested ? UploadTaskState.Paused : UploadTaskState.Cancelled;
                UpdateRecordedProgress();
            }
            catch (Exception ex)
            {
                ErrorMessage = Helpers.FileOperationErrors.GetMessage(ex);
                State = UploadTaskState.Failed;
            }
            finally
            {
                _retryKey = null;
                _cancellation.Dispose();
                _cancellation = null;
            }
        }

        [RelayCommand(CanExecute = nameof(CanRetry))]
        public Task RetryUpload() => StartUpload();

        [RelayCommand(CanExecute = nameof(CanResume))]
        public Task ResumeUpload() => StartUpload();

        [RelayCommand(CanExecute = nameof(CanPause))]
        public Task PauseUpload()
        {
            if (!_pauseTask.IsCompleted) return _pauseTask;
            if (!CanPause) return Task.CompletedTask;
            _pauseRequested = true;
            _pauseTask = PauseCoreAsync();
            State = UploadTaskState.Pausing;
            _cancellation?.Cancel();
            return _pauseTask;
        }

        private async Task PauseCoreAsync()
        {
            await Task.Yield();
            try
            {
                await _activeTask;
                if (!_removalRequested && State == UploadTaskState.Pausing) State = UploadTaskState.Paused;
                UpdateRecordedProgress();
            }
            finally { _pauseRequested = false; }
        }

        private Task RetryItem(string key)
        {
            if (!(CanRetry || IsPaused || IsInterrupted) || !_activeTask.IsCompleted || _removalRequested) return _activeTask;
            _retryKey = key;
            return StartUpload();
        }

        private void UpdateRecordedProgress()
        {
            if (Record == null) return;
            var files = Record.Uploads.Where(x => !x.IsFolder).ToArray();
            int completed = files.Count(x => x.State is TransferEntryState.Completed or TransferEntryState.Skipped);
            long total = files.Sum(x => x.Size);
            long received = files.Sum(x => x.State is TransferEntryState.Completed or TransferEntryState.Skipped ? x.Size : x.Resume?.Bytes ?? 0);
            Progress = Completed ? 100 : total == 0 ? 0 : (int)Math.Min(99, received * 100.0 / total);
            UpdateTransferProgress(received, total, Record.Uploads.Count > 0 &&
                Record.Uploads.All(x => (!x.IsFolder || x.Enumerated) && x.State != TransferEntryState.Failed));
            Failures = Record.Uploads.Where(x => x.State == TransferEntryState.Failed).Select(x => new TransferFailureRow(
                x.Key == "." ? Name : x.Key, (x.ErrorKey ?? "FileOperation_Failed").GetLocalized(), () => RetryItem(x.Key),
                () => !_removalRequested && (CanRetry || IsPaused || IsInterrupted))).ToArray();
            Notice = string.Format("Transfer_FileSummary".GetLocalized(), completed, files.Length, Failures.Count);
            OnPropertyChanged(nameof(Failures)); OnPropertyChanged(nameof(HasFailures));
        }

        private void UpdateTransferProgress(long bytes, long total, bool known)
        {
            _uploadedBytes = bytes; _totalBytes = total; _totalKnown = known;
            Controls.Update(bytes, total, State == UploadTaskState.Uploading, known, Completed);
        }

        public async Task StopForExitAsync()
        {
            if (Record == null) { await CancelTaskAsync(); return; }
            bool preservePause = IsPaused || IsPausing;
            await PauseUpload();
            await _activeTask;
            await _cancelTask;
            if (!IsTerminal && !HasFailed && !preservePause && !_removalRequested)
            {
                foreach (var entry in Record.Uploads.Where(x => x.State == TransferEntryState.Working)) entry.State = TransferEntryState.Pending;
                State = UploadTaskState.Interrupted;
                _save(Record);
                UpdateRecordedProgress();
            }
        }

        // Session URLs authorize writes without the account, so an ended card kept by account stop must not keep them.
        // A retry after a new sign-in starts fresh sessions. Deletion and persistence failures remain retryable.
        public async Task DiscardResumeAsync()
        {
            if (Record == null || NeedsAccountStop) return;
            var resumable = Record.Uploads.Where(x => x.Resume != null).ToArray();
            await Task.WhenAll(resumable.Select(x => ResumableUploadSession.DeleteRequiredAsync(x.Resume)));
            foreach (var item in resumable) item.Resume = null;
            // A previous save may have failed after clearing the in-memory sessions.
            // Retry the checkpoint even when there is no remote deletion left to do.
            _manager.SaveRequired(Record);
        }

        [RelayCommand(CanExecute = nameof(CanRemove))]
        public Task CancelTaskAsync()
        {
            if (_removalRequested) return _cancelTask;
            _removalRequested = true;
            _cancelTask = CancelCoreAsync();
            if (!IsTerminal && !HasFailed)
            {
                State = UploadTaskState.Cancelling;
                _cancellation?.Cancel();
            }
            OnPropertyChanged(nameof(CanRemove));
            OnPropertyChanged(nameof(CanPause));
            OnPropertyChanged(nameof(CanResume));
            OnPropertyChanged(nameof(CanRetry));
            CancelTaskCommand.NotifyCanExecuteChanged();
            PauseUploadCommand.NotifyCanExecuteChanged();
            ResumeUploadCommand.NotifyCanExecuteChanged();
            RetryUploadCommand.NotifyCanExecuteChanged();
            return _cancelTask;
        }

        private async Task CancelCoreAsync()
        {
            // Publish the shared cancellation task before notifications can reenter the command.
            await Task.Yield();
            await _activeTask;
            await _pauseTask;
            if (Record != null)
            {
                await Task.WhenAll(Record.Uploads.Select(x => ResumableUploadSession.DeleteAsync(x.Resume)));
                foreach (var item in Record.Uploads) item.Resume = null;
            }
            if (State == UploadTaskState.Cancelling) State = UploadTaskState.Cancelled;
            if (!_manager.RemoveSelectedUploadTasks(this))
            {
                _removalRequested = false;
                OnPropertyChanged(nameof(CanRemove));
                OnPropertyChanged(nameof(CanPause));
                OnPropertyChanged(nameof(CanResume));
                OnPropertyChanged(nameof(CanRetry));
                CancelTaskCommand.NotifyCanExecuteChanged();
                PauseUploadCommand.NotifyCanExecuteChanged();
                ResumeUploadCommand.NotifyCanExecuteChanged();
                RetryUploadCommand.NotifyCanExecuteChanged();
            }
            else Controls.Dispose();
        }
    }
}
