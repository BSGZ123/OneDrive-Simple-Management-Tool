using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using OneDrive_Simple_Management_Tool.Helpers;
using OneDrive_Simple_Management_Tool.Models;
using OneDrive_Simple_Management_Tool.Services;
using System;
using System.Threading.Tasks;
using Windows.Storage;

namespace OneDrive_Simple_Management_Tool.ViewModels
{
    public partial class DownloadTaskViewModel : ObservableObject
    {
        private readonly DownloadSession _session;
        private readonly TaskManagerViewModel _manager;
        private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();
        private long _revision;
        private long _lastProgressTick;
        private DownloadTaskState _lastReportedState;

        public DownloadTaskViewModel(DriveViewModel drive, string itemId, StorageFile file)
        {
            Name = file.Name;
            _manager = Ioc.Default.GetService<TaskManagerViewModel>();
            _session = new DownloadSession(file.Path, token => drive.Provider.GetDownloadSourceAsync(itemId, token));
            _session.Changed += OnSessionChanged;
        }

        public string Name { get; }
        public string DestinationPath => _session.DestinationPath;
        public DateTime StartTime { get; private set; }
        public DateTime? FinishTime { get; private set; }

        public bool Completed => State == DownloadTaskState.Completed;
        public bool HasFailed => State == DownloadTaskState.Failed;
        public bool IsPaused => State == DownloadTaskState.Paused;
        public bool CanPause => State is DownloadTaskState.Preparing or DownloadTaskState.Downloading or DownloadTaskState.Retrying;
        public bool CanResume => State is DownloadTaskState.Pending or DownloadTaskState.Paused;
        public bool CanRetry => HasFailed;
        public bool CanRemove => State is not (DownloadTaskState.Cancelling or DownloadTaskState.Finalizing);
        public bool IsIndeterminate => State is DownloadTaskState.Preparing or DownloadTaskState.Retrying or DownloadTaskState.Finalizing;
        public string StatusText => ($"DownloadState_{State}").GetLocalized();

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Completed), nameof(HasFailed), nameof(IsPaused), nameof(CanPause),
            nameof(CanResume), nameof(CanRetry), nameof(CanRemove), nameof(IsIndeterminate), nameof(StatusText))]
        [NotifyCanExecuteChangedFor(nameof(PauseDownloadCommand), nameof(ResumeDownloadCommand),
            nameof(RetryDownloadCommand), nameof(CancelTaskCommand), nameof(OpenFolderCommand))]
        private DownloadTaskState _state = DownloadTaskState.Pending;

        [ObservableProperty] private int _progress;
        [ObservableProperty] private long _downloadedBytes;
        [ObservableProperty] private long _totalBytes;
        [ObservableProperty] private long _downloadSpeed;
        [ObservableProperty] private string _errorMessage = string.Empty;
        [ObservableProperty] private string _notice = string.Empty;

        public async Task StartDownload()
        {
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

        [RelayCommand(CanExecute = nameof(CanRemove))]
        public async Task CancelTaskAsync()
        {
            await _session.CancelAsync();
            ApplySnapshot(_session.Snapshot);
            if (State is DownloadTaskState.Cancelled or DownloadTaskState.Completed)
            {
                _session.Changed -= OnSessionChanged;
                _manager.RemoveSelectedDownloadTasks(this);
            }
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
            ErrorMessage = snapshot.Failure == DownloadFailure.None ? string.Empty :
                ($"DownloadError_{snapshot.Failure}").GetLocalized();
            Notice = snapshot.Restarted ? "Download_Restarted".GetLocalized() : string.Empty;
            if (Completed) FinishTime = DateTime.Now;
        }
    }
}
