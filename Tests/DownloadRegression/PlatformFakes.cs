using System.Collections.Concurrent;
using System.Xml.Linq;
using OneDrive_Simple_Management_Tool.Models;
using OneDrive_Simple_Management_Tool.ViewModels;

namespace Windows.Storage
{
    public interface IStorageItem { }
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
        public Func<CancellationToken, Task<DownloadSource>> Resolve { get; set; }
        public Task<DownloadSource> GetDownloadSourceAsync(string id, CancellationToken token) => Resolve(token);
    }
    public sealed class DriveViewModel(SourceProvider provider)
    {
        public SourceProvider Provider { get; } = provider;
    }
    // Upload behavior has its own regression project; only download paths run here.
    public sealed class UploadTaskViewModel
    {
        public UploadTaskViewModel(DriveViewModel drive, string id, Windows.Storage.IStorageItem item) { }
        public Task StartUpload() => Task.CompletedTask;
    }
}

internal sealed class TestServices : IServiceProvider
{
    public TaskManagerViewModel Manager { get; } = new();
    public object GetService(Type type) => type == typeof(TaskManagerViewModel) ? Manager : null;
}
