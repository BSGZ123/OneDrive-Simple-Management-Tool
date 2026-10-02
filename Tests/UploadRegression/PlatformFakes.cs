using System.Collections.Concurrent;
using OneDrive_Simple_Management_Tool.Services;
using Windows.Storage.FileProperties;

// Only platform boundaries are faked. Upload service, progress aggregation and task
// state transitions are compiled directly from the application's source files.
namespace Windows.Storage.FileProperties
{
    public class BasicProperties
    {
        public ulong Size { get; init; }
    }
}

namespace Windows.Storage
{
    public interface IStorageItem
    {
        string Name { get; }
        Task<BasicProperties> GetBasicPropertiesAsync();
    }

    public sealed class StorageFile(string name, int size) : IStorageItem
    {
        public string Name { get; } = name;
        public string Path => Name;
        private MemoryStream _writtenContent = new();
        public byte[] WrittenContent => _writtenContent.ToArray();
        public Exception OpenError { get; init; }
        public Exception PropertiesError { get; init; }

        public Task<Stream> OpenStreamForReadAsync() => OpenError == null
            ? Task.FromResult<Stream>(new MemoryStream(new byte[size]))
            : Task.FromException<Stream>(OpenError);

        public Task<Stream> OpenStreamForWriteAsync()
        {
            _writtenContent = new MemoryStream();
            return Task.FromResult<Stream>(_writtenContent);
        }

        public Task<BasicProperties> GetBasicPropertiesAsync() => PropertiesError == null
            ? Task.FromResult(new BasicProperties { Size = (ulong)size })
            : Task.FromException<BasicProperties>(PropertiesError);
    }

    public sealed class StorageFolder(string name) : IStorageItem
    {
        public string Name { get; } = name;
        public List<StorageFile> Files { get; } = [];
        public List<StorageFolder> Folders { get; } = [];
        public Exception EnumerationError { get; init; }

        public Task<IReadOnlyList<StorageFile>> GetFilesAsync() => EnumerationError == null
            ? Task.FromResult<IReadOnlyList<StorageFile>>(Files)
            : Task.FromException<IReadOnlyList<StorageFile>>(EnumerationError);

        public Task<IReadOnlyList<StorageFolder>> GetFoldersAsync() => Task.FromResult<IReadOnlyList<StorageFolder>>(Folders);
        public Task<BasicProperties> GetBasicPropertiesAsync() => Task.FromResult(new BasicProperties());
    }
}

namespace Microsoft.UI.Dispatching
{
    public sealed class DispatcherQueue
    {
        private static readonly DispatcherQueue Instance = new();
        public static DispatcherQueue GetForCurrentThread() => Instance;

        public bool TryEnqueue(Action callback)
        {
            TestUiContext.Work.Enqueue(callback);
            return true;
        }
    }

    internal sealed class TestUiContext : SynchronizationContext
    {
        internal static readonly ConcurrentQueue<Action> Work = new();

        public override void Post(SendOrPostCallback callback, object state) => Work.Enqueue(() => callback(state));

        public static async Task Run(Func<Task> action)
        {
            var previous = Current;
            var context = new TestUiContext();
            Task task;
            SetSynchronizationContext(context);
            try
            {
                task = action();
            }
            finally
            {
                SetSynchronizationContext(previous);
            }

            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (!task.IsCompleted || !Work.IsEmpty)
            {
                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException("The simulated UI operation did not finish.");
                }

                if (Work.TryDequeue(out var callback))
                {
                    SetSynchronizationContext(context);
                    try
                    {
                        callback();
                    }
                    finally
                    {
                        SetSynchronizationContext(previous);
                    }
                }
                else
                {
                    await Task.Delay(1);
                }
            }

            await task;
        }
    }
}

namespace OneDrive_Simple_Management_Tool.ViewModels
{
#if !FILE_MANAGEMENT_REGRESSION
    public sealed class DriveViewModel(OneDrive provider)
    {
        public OneDrive Provider { get; } = provider;
    }
#endif

    public sealed class TaskManagerViewModel
    {
        public void RemoveSelectedUploadTasks(UploadTaskViewModel task) { }
    }
}
