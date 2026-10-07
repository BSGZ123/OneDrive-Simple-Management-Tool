using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using OneDrive_Simple_Management_Tool.Helpers;
using OneDrive_Simple_Management_Tool.Models;
using OneDrive_Simple_Management_Tool.Services;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OneDrive_Simple_Management_Tool.ViewModels
{
    public partial class FolderSyncViewModel : ObservableObject
    {
        private readonly FolderSyncService _service;
        private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();
        public ObservableCollection<FolderSyncItemViewModel> Bindings { get; } = new();
        [ObservableProperty] private string _errorMessage = "";
        [ObservableProperty] private bool _isEmpty = true;
        public bool HasError => !string.IsNullOrEmpty(ErrorMessage);
        partial void OnErrorMessageChanged(string value) => OnPropertyChanged(nameof(HasError));

        public FolderSyncViewModel(FolderSyncService service)
        {
            _service = service;
            _service.Changed += () => _dispatcher.TryEnqueue(Refresh);
        }

        public async Task InitializeAsync()
        {
            await _service.InitializeAsync();
            Refresh();
        }

        private void Refresh()
        {
            var jobs = _service.Jobs;
            foreach (var item in Bindings.Where(i => !jobs.Contains(i.Job)).ToList())
            {
                item.Detach();
                Bindings.Remove(item);
            }
            foreach (var job in jobs.Where(j => Bindings.All(i => i.Job != j)))
                Bindings.Add(new FolderSyncItemViewModel(job));
            IsEmpty = Bindings.Count == 0;
            ErrorMessage = _service.ErrorKey?.GetLocalized() ?? "";
        }
    }

    public partial class FolderSyncItemViewModel : ObservableObject
    {
        private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();
        private int _queued;
        public FolderSyncJob Job { get; }
        public string Name => Job.Binding.FolderName;
        public string LocalPath => Job.Binding.LocalPath;
        public string RemotePath => Job.Binding.DriveName + "  ·  " + Job.Binding.RemotePath;
        [ObservableProperty] private string _state;
        [ObservableProperty] private string _detail;
        [ObservableProperty] private string _lastSuccess;
        [ObservableProperty] private string _issues;
        [ObservableProperty] private string _toggleLabel;
        [ObservableProperty] private double _percent;
        [ObservableProperty] private bool _isActive;
        // With IsActive, exactly one of these is true; the page colors the state by it.
        [ObservableProperty] private bool _needsAttention;
        [ObservableProperty] private bool _isPaused;
        [ObservableProperty] private bool _isIdle;
        [ObservableProperty] private bool _enabled;
        [ObservableProperty] private bool _hasIssues;
        [ObservableProperty] private string _commandError;

        public FolderSyncItemViewModel(FolderSyncJob job)
        {
            Job = job;
            Job.Updated += OnUpdated;
            Refresh();
        }

        public void Detach() => Job.Updated -= OnUpdated;

        private void OnUpdated()
        {
            if (Interlocked.Exchange(ref _queued, 1) != 0) return;
            _dispatcher.TryEnqueue(() => { Interlocked.Exchange(ref _queued, 0); Refresh(); });
        }

        private void Refresh()
        {
            var progress = Job.Progress;
            State = progress.StateKey.GetLocalized();
            Detail = string.Format("Sync_Progress".GetLocalized(), progress.Completed, progress.Total, progress.CurrentPath);
            LastSuccess = Job.Binding.LastSuccess is DateTimeOffset time
                ? string.Format("Sync_LastSuccess".GetLocalized(), time.ToLocalTime().ToString("g")) : "Sync_Never".GetLocalized();
            Issues = string.Join(Environment.NewLine, Job.Issues.Select(i => (i.Path.Length == 0 ? "" : i.Path + ": ") + i.MessageKey.GetLocalized()));
            HasIssues = Job.Issues.Count > 0;
            Enabled = Job.Binding.Enabled;
            ToggleLabel = (Enabled ? "Sync_Pause" : "Sync_Resume").GetLocalized();
            IsActive = progress.StateKey is "Sync_Scanning" or "Sync_Uploading";
            NeedsAttention = progress.StateKey == "Sync_Attention";
            IsPaused = progress.StateKey == "Sync_Paused";
            IsIdle = !IsActive && !NeedsAttention && !IsPaused;
            Percent = progress.Total == 0 ? (progress.StateKey == "Sync_Monitoring" ? 100 : 0) :
                Math.Clamp((progress.Completed + progress.Percent / 100d) * 100 / progress.Total, 0, 100);
        }

        [RelayCommand]
        private async Task ToggleAsync()
        {
            try { CommandError = ""; await Job.SetEnabledAsync(!Job.Binding.Enabled); }
            catch (Exception exception) { CommandError = FolderSyncJob.GetErrorKey(exception).GetLocalized(); }
        }

        [RelayCommand]
        private void CheckNow() => Job.RequestScan();
    }
}
