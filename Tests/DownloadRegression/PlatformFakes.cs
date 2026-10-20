using System.Collections.Concurrent;
using System.Xml.Linq;
using OneDrive_Simple_Management_Tool.Models;
using OneDrive_Simple_Management_Tool.ViewModels;

namespace Windows.Storage
{
    public interface IStorageItem { string Name { get; } string Path { get; } }
    public sealed class StorageFolder : IStorageItem { public string Name => "folder"; public string Path => "folder"; }
    public sealed class StorageFile(string path) : IStorageItem
    {
        public string Name => System.IO.Path.GetFileName(path);
        public string Path => path;
    }
}

namespace Microsoft.UI.Dispatching
{
    public sealed class DispatcherQueue
    {
        public static ConcurrentQueue<Action> Pending { get; } = new();
        public static DispatcherQueue GetForCurrentThread() => new();
        public bool TryEnqueue(Action callback) { Pending.Enqueue(callback); return true; }
        public static void Drain()
        {
            while (Pending.TryDequeue(out var callback)) callback();
        }
    }
}

namespace OneDrive_Simple_Management_Tool.Helpers
{
    public static class ResourceHelper
    {
        public static string Language { get; set; } = "en-US";
        public static string GetLocalized(this string key) => XDocument.Load(
            Path.Combine(AppContext.BaseDirectory, "Strings", Language, "Resources.resw"))
            .Root.Elements("data").Single(element => (string)element.Attribute("name") == key).Element("value").Value;
    }
}

namespace OneDrive_Simple_Management_Tool.ViewModels
{
    public sealed class SourceProvider
    {
        public string HomeAccountId { get; set; }
        public string DriveId { get; set; }
        public void CheckAccess() { }
        public Func<CancellationToken, Task<DownloadSource>> Resolve { get; set; }
        public Task<DownloadSource> GetDownloadSourceAsync(string id, CancellationToken token) => Resolve(token);
    }
    public sealed class DriveViewModel(SourceProvider provider)
    {
        public SourceProvider Provider { get; } = provider;
    }
    // Upload behavior has its own regression project; only download paths run here.
    public sealed class UploadTaskViewModel : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
    {
        private UploadTaskState _state;
        public UploadTaskState State { get => _state; set => SetProperty(ref _state, value); }
        public bool Completed => State == UploadTaskState.Completed;
        public bool CanRemove => true;
        public bool CanPause => State is UploadTaskState.Pending or UploadTaskState.Uploading;
        public bool CanResume => State is UploadTaskState.Pending or UploadTaskState.Paused or UploadTaskState.Interrupted;
        public bool CanRetry => State == UploadTaskState.Failed;
        public int Starts { get; private set; }
        public Task PauseUpload() { State = UploadTaskState.Paused; return Task.CompletedTask; }
        public Task ResumeUpload() => StartUpload();
        public Task RetryUpload() => StartUpload();
        public TransferRecord Record { get; private set; }
        public void AttachRecord(TransferRecord record, Action<TransferRecord> save, bool restore = false) => Record = record;
        public Task StopForExitAsync() => CancelTaskAsync();
        public UploadTaskViewModel(DriveViewModel drive, string id, Windows.Storage.IStorageItem item, UploadConflictResolver conflicts = null) { Drive = drive; }
        public DriveViewModel Drive { get; }
        public bool NeedsExitProtection { get; set; }
        public bool NeedsAccountStop => NeedsExitProtection;
        public Task DiscardResumeAsync() => Task.CompletedTask;
        public bool WasCancelled { get; private set; }
        public Func<Task> OnCancel { get; set; } = () => Task.CompletedTask;
        public Task StartUpload() { Starts++; State = UploadTaskState.Uploading; NeedsExitProtection = true; return Task.CompletedTask; }
        public async Task CancelTaskAsync() { WasCancelled = true; await OnCancel(); NeedsExitProtection = false; }
    }
}

internal sealed class TestServices : IServiceProvider
{
    public TaskManagerViewModel Manager { get; set; } = new();
    public object GetService(Type type) => type == typeof(TaskManagerViewModel) ? Manager : null;
}
