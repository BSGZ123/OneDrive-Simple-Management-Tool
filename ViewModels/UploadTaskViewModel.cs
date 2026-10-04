using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using OneDrive_Simple_Management_Tool.Models;
using OneDrive_Simple_Management_Tool.Services;
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

        public UploadTaskViewModel(DriveViewModel drive, string itemId, IStorageItem item)
        {
            Drive = drive;
            _item = item;
            _itemId = itemId;
        }

        public DriveViewModel Drive { get; }
        public string Name => _item.Name;
        public bool Completed => State == UploadTaskState.Completed;
        public bool HasFailed => State == UploadTaskState.Failed;
        public bool IsPreparing => State == UploadTaskState.Preparing;
        public bool IsCancelling => State == UploadTaskState.Cancelling;
        public bool IsUploading => State is UploadTaskState.Preparing or UploadTaskState.Uploading;
        public bool CanRemove => !_removalRequested;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Completed), nameof(HasFailed), nameof(IsPreparing), nameof(IsCancelling), nameof(IsUploading))]
        private UploadTaskState _state = UploadTaskState.Pending;

        [ObservableProperty] private int _progress;
        [ObservableProperty] private string _errorMessage = string.Empty;

        // Like the task collection, lifecycle commands run on the UI thread.
        public Task StartUpload()
        {
            if (_removalRequested || !_activeTask.IsCompleted || State is not (UploadTaskState.Pending or UploadTaskState.Failed))
            {
                return _activeTask;
            }

            _cancellation = new CancellationTokenSource();
            _activeTask = RunUploadAsync(_cancellation.Token, ++_uploadAttempt);
            ErrorMessage = string.Empty;
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
                if (_item is StorageFile file)
                {
                    await Drive.Provider.UploadFileAsync(file, _itemId, progress, cancellationToken);
                }
                else if (_item is StorageFolder uploadFolder)
                {
                    await Drive.Provider.UploadFolderAsync(uploadFolder, _itemId, progress, cancellationToken);
                }
                else
                {
                    throw new NotSupportedException("Only files and folders can be uploaded.");
                }

                Progress = 100;
                State = UploadTaskState.Completed;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                State = UploadTaskState.Cancelled;
            }
            catch (Exception ex)
            {
                ErrorMessage = ex.Message;
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
            if (State is UploadTaskState.Pending or UploadTaskState.Preparing or UploadTaskState.Uploading)
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
