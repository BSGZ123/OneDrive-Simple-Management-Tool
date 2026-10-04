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
    // Mimics WinRT's awaitable operation and AsTask(token), with cancellable storage boundaries.
    public sealed class StorageOperation<T>(Func<CancellationToken, Task<T>> action)
    {
        public Task<T> AsTask(CancellationToken token) => action(token);
        public System.Runtime.CompilerServices.TaskAwaiter<T> GetAwaiter() => action(default).GetAwaiter();
    }

    public interface IStorageItem
    {
        string Name { get; }
        StorageOperation<BasicProperties> GetBasicPropertiesAsync();
    }

    public sealed class StorageFile(string name, int size) : IStorageItem
    {
        public string Name { get; } = name;
        public string Path => Name;
        private MemoryStream _writtenContent = new();
        public byte[] WrittenContent => _writtenContent.ToArray();
        public Exception OpenError { get; init; }
        public Exception PropertiesError { get; init; }
        public Func<CancellationToken, Task> BeforeProperties { get; init; }
        public Func<Task> BeforeOpen { get; init; }
        public Stream ReadStream { get; private set; }

        public async Task<Stream> OpenStreamForReadAsync()
        {
            if (BeforeOpen != null) await BeforeOpen();
            if (OpenError != null) throw OpenError;
            return ReadStream = new MemoryStream(new byte[size]);
        }

        public Task<Stream> OpenStreamForWriteAsync()
        {
            _writtenContent = new MemoryStream();
            return Task.FromResult<Stream>(_writtenContent);
        }

        public StorageOperation<BasicProperties> GetBasicPropertiesAsync() => new(async token =>
        {
            token.ThrowIfCancellationRequested();
            if (BeforeProperties != null) await BeforeProperties(token);
            if (PropertiesError != null) throw PropertiesError;
            return new BasicProperties { Size = (ulong)size };
        });
    }

    public sealed class StorageFolder(string name) : IStorageItem
    {
        public string Name { get; } = name;
        public List<StorageFile> Files { get; } = [];
        public List<StorageFolder> Folders { get; } = [];
        public Exception EnumerationError { get; init; }
        public Func<CancellationToken, Task> BeforeGetFiles { get; init; }
        public Func<CancellationToken, Task> BeforeGetFolders { get; init; }

        public StorageOperation<IReadOnlyList<StorageFile>> GetFilesAsync() => new(async token =>
        {
            token.ThrowIfCancellationRequested();
            if (BeforeGetFiles != null) await BeforeGetFiles(token);
            if (EnumerationError != null) throw EnumerationError;
            return Files;
        });

        public StorageOperation<IReadOnlyList<StorageFolder>> GetFoldersAsync() => new(async token =>
        {
            token.ThrowIfCancellationRequested();
            if (BeforeGetFolders != null) await BeforeGetFolders(token);
            return Folders;
        });
        public StorageOperation<BasicProperties> GetBasicPropertiesAsync() => new(_ => Task.FromResult(new BasicProperties()));
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
        public List<UploadTaskViewModel> Removed { get; } = [];
        public Action<UploadTaskViewModel> OnRemove { get; set; }
        public void RemoveSelectedUploadTasks(UploadTaskViewModel task)
        {
            OnRemove?.Invoke(task);
            Removed.Add(task);
        }
    }
}
