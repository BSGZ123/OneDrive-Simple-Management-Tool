using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using OneDrive_Simple_Management_Tool.Models;
using OneDrive_Simple_Management_Tool.Services;
using OneDrive_Simple_Management_Tool.Helpers;
using System;
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
        private int _uploadAttempt;
        private bool _removalRequested;
        private readonly UploadConflictResolver _conflicts;

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
        public string Name => _item.Name;
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
        public bool CanRemove => !_removalRequested;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Completed), nameof(HasFailed), nameof(IsPreparing), nameof(IsCancelling), nameof(IsUploading),
            nameof(IsWaitingForConflict), nameof(IsSkipped), nameof(IsCancelled), nameof(IsTerminal))]
        private UploadTaskState _state = UploadTaskState.Pending;

        [ObservableProperty] private int _progress;
        [ObservableProperty] private string _errorMessage = string.Empty;
        [ObservableProperty] private string _notice = string.Empty;

        // Like the task collection, lifecycle commands run on the UI thread.
        public Task StartUpload()
        {
            if (_manager.IsShuttingDown || _removalRequested || !_activeTask.IsCompleted || State is not (UploadTaskState.Pending or UploadTaskState.Failed))
            {
                return _activeTask;
            }

            _cancellation = new CancellationTokenSource();
            _activeTask = RunUploadAsync(_cancellation.Token, ++_uploadAttempt);
            ErrorMessage = string.Empty;
            Notice = string.Empty;
            Progress = 0;
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
                ulong totalBytes = _item is StorageFolder folder
                    ? await Utils.GetFolderSize(folder, cancellationToken)
                    : (await _item.GetBasicPropertiesAsync().AsTask(cancellationToken)).Size;
                cancellationToken.ThrowIfCancellationRequested();

                IProgress<long> progress = new Progress<long>(value =>
                {
                    _dispatcher.TryEnqueue(() =>
                    {
                        // Queued reports must not overwrite cancellation, a terminal state or a newer attempt.
                        if (State == UploadTaskState.Uploading && attempt == _uploadAttempt)
                        {
                            int percentage = totalBytes == 0 ? 0 : (int)Math.Clamp(value * 100.0 / totalBytes, 0, 99);
                            Progress = Math.Max(Progress, percentage);
                        }
                    });
                });

                State = UploadTaskState.Uploading;
                UploadOutcome outcome;
                if (_item is StorageFile file)
                {
                    outcome = await Drive.Provider.UploadFileAsync(file, _itemId, progress, cancellationToken, _conflicts);
                }
                else if (_item is StorageFolder uploadFolder)
                {
                    outcome = await Drive.Provider.UploadFolderAsync(uploadFolder, _itemId, progress, cancellationToken, _conflicts);
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
                State = UploadTaskState.Cancelled;
            }
            catch (Exception ex)
            {
                ErrorMessage = Helpers.FileOperationErrors.GetMessage(ex);
                State = UploadTaskState.Failed;
            }
            finally
            {
                _cancellation.Dispose();
                _cancellation = null;
            }
        }

        [RelayCommand(CanExecute = nameof(CanRemove))]
        public Task CancelTaskAsync()
        {
            if (_removalRequested) return _cancelTask;
            _removalRequested = true;
            _cancelTask = CancelCoreAsync();
            if (State is UploadTaskState.Pending or UploadTaskState.Preparing or UploadTaskState.Uploading or UploadTaskState.WaitingForConflict)
            {
                State = UploadTaskState.Cancelling;
                _cancellation?.Cancel();
            }
            OnPropertyChanged(nameof(CanRemove));
            CancelTaskCommand.NotifyCanExecuteChanged();
            return _cancelTask;
        }

        private async Task CancelCoreAsync()
        {
            // Publish the shared cancellation task before notifications can reenter the command.
            await Task.Yield();
            await _activeTask;
            if (State == UploadTaskState.Cancelling) State = UploadTaskState.Cancelled;
            _manager.RemoveSelectedUploadTasks(this);
        }
    }
}
