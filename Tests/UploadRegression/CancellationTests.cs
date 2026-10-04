using System.Net;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Dispatching;
using OneDrive_Simple_Management_Tool.Models;
using OneDrive_Simple_Management_Tool.ViewModels;
using Windows.Storage;

namespace OneDrive_Simple_Management_Tool.Tests.UploadRegression;

internal static partial class Program
{
    private static (string, Func<Task>)[] CancellationCases() =>
    [
        ("A pending task can be removed without starting upload", CancelPending),
        ("Immediate cancellation stops preparation and prevents restart", CancelImmediately),
        ("Cancellation reaches file properties", CancelProperties),
        ("Cancellation reaches recursive folder scanning", CancelFolderScan),
        ("Removal waits for an uncancellable file open and disposes its stream", CancelOpening),
        ("Cancellation reaches upload-session creation", () => CancelMetadata("session")),
        ("Cancellation reaches zero-byte uploads", () => CancelMetadata("empty")),
        ("Cancellation reaches remote folder creation", () => CancelMetadata("folder")),
        ("Cancellation stops SDK slices and awaits anonymous session cleanup", CancelSlices),
        ("Cancellation waits for all concurrent descendants and leaves other tasks alone", CancelDescendants),
        ("Cancelled folder scheduling does not open another file", CancelScheduling),
        ("Session cleanup rejection preserves cancellation", () => CleanupFailure(false)),
        ("Session cleanup has a bounded timeout", () => CleanupFailure(true)),
        ("A confirmed upload wins concurrent cancellation", CompletionWins),
        ("SDK final confirmation is retained when progress requests cancellation", ConfirmedServiceResult),
        ("A failed descendant still waits for other uploads and their cancellation", FailedSiblingWaits),
        ("An unrelated cancellation exception is a failure", UnrelatedCancellation),
        ("Completed and failed tasks can be removed exactly once", RemoveTerminal),
        ("Precancelled service calls do not perform storage or HTTP work", PrecancelledServices)
    ];

    private static TaskManagerViewModel Manager => Ioc.Default.GetService<TaskManagerViewModel>();
    private static UploadTaskViewModel NewTask(UploadFixture fixture, IStorageItem item) =>
        new(new DriveViewModel(fixture.Provider), "root", item);

    private static void AssertCancelled(UploadTaskViewModel task)
    {
        Assert(task.State == UploadTaskState.Cancelled && !task.HasFailed && !task.Completed && !task.IsUploading,
            "Cancellation was not a distinct terminal state.");
        Assert(task.Progress < 100 && task.ErrorMessage == string.Empty, "Cancellation looked like success or failure.");
        Assert(!task.CanRemove && !task.CancelTaskCommand.CanExecute(null), "Removed task still allows removal.");
        Assert(Manager.Removed.Count(item => item == task) == 1, "Removal must happen exactly once.");
    }

    private static async Task CancelPending()
    {
        await using var fixture = new UploadFixture();
        var task = NewTask(fixture, new StorageFile("pending.bin", 100));
        await TestUiContext.Run(async () =>
        {
            await task.CancelTaskAsync();
            await task.StartUpload();
            await task.CancelTaskAsync();
        });
        AssertCancelled(task);
        Assert(fixture.Metadata.Sessions == 0, "A removed pending task started uploading.");
    }

    private static async Task CancelImmediately()
    {
        await using var fixture = new UploadFixture();
        var task = NewTask(fixture, new StorageFile("immediate.bin", 100));
        await TestUiContext.Run(async () =>
        {
            Task running = task.StartUpload();
            Assert(ReferenceEquals(running, task.StartUpload()), "Repeated start created a new operation.");
            Task cancelling = task.CancelTaskAsync();
            Assert(task.IsCancelling && !task.CanRemove, "Cancellation feedback was not immediate.");
            Assert(ReferenceEquals(cancelling, task.CancelTaskAsync()), "Repeated cancel created a new operation.");
            Assert(ReferenceEquals(running, task.StartUpload()), "Start raced with cancellation.");
            await cancelling;
            Assert(running.IsCompleted, "Task was removed before the runner exited.");
        });
        AssertCancelled(task);
        Assert(fixture.Metadata.Sessions == 0, "Immediate cancellation still created a session.");
    }

    private static async Task CancelProperties()
    {
        await using var fixture = new UploadFixture();
        var gate = new OperationGate();
        var task = NewTask(fixture, new StorageFile("properties.bin", 100) { BeforeProperties = gate.WaitAsync });
        await TestUiContext.Run(async () =>
        {
            Task running = task.StartUpload();
            await gate.Entered.Task;
            await task.CancelTaskAsync();
            Assert(running.IsCompleted && gate.Exited.Task.IsCompleted, "Storage operation outlived cancellation.");
        });
        AssertCancelled(task);
        Assert(gate.Cancelled.Task.IsCompleted && fixture.Metadata.Sessions == 0, "Storage cancellation did not propagate.");
    }

    private static async Task CancelFolderScan()
    {
        await using var fixture = new UploadFixture();
        var gate = new OperationGate();
        var folder = new StorageFolder("root");
        folder.Folders.Add(new StorageFolder("child") { BeforeGetFiles = gate.WaitAsync });
        var task = NewTask(fixture, folder);
        await TestUiContext.Run(async () =>
        {
            Task running = task.StartUpload();
            await gate.Entered.Task;
            await task.CancelTaskAsync();
            Assert(running.IsCompleted && gate.Cancelled.Task.IsCompleted, "Descendant scan did not observe cancellation.");
        });
        AssertCancelled(task);
        Assert(fixture.Metadata.Folders == 0, "A cancelled scan created remote folders.");
    }

    private static async Task CancelOpening()
    {
        await using var fixture = new UploadFixture();
        var gate = new OperationGate();
        var file = new StorageFile("opening.bin", 100) { BeforeOpen = () => gate.WaitAsync(default) };
        var task = NewTask(fixture, file);
        await TestUiContext.Run(async () =>
        {
            Task running = task.StartUpload();
            await gate.Entered.Task;
            Task cancelling = task.CancelTaskAsync();
            await Task.Yield();
            Assert(!cancelling.IsCompleted && !Manager.Removed.Contains(task), "Removal abandoned the file-open operation.");
            Manager.OnRemove = removed =>
            {
                if (removed == task) Assert(running.IsCompleted && !file.ReadStream.CanRead, "Removal preceded resource disposal.");
            };
            try
            {
                gate.Release.TrySetResult();
                await cancelling;
            }
            finally
            {
                Manager.OnRemove = null;
            }
        });
        AssertCancelled(task);
        Assert(fixture.Metadata.Sessions == 0, "Cancelled open proceeded to create a session.");
    }

    private static async Task CancelMetadata(string kind)
    {
        await using var fixture = new UploadFixture();
        var gate = new OperationGate(deferCancellation: true);
        fixture.Metadata.BeforeResponse = (_, token) => gate.WaitAsync(token);
        IStorageItem item = kind == "folder" ? new StorageFolder("folder") : new StorageFile("file.bin", kind == "empty" ? 0 : 100);
        var task = NewTask(fixture, item);
        await TestUiContext.Run(async () =>
        {
            Task running = task.StartUpload();
            await gate.Entered.Task;
            Task cancelling = task.CancelTaskAsync();
            await gate.Cancelled.Task;
            Assert(!cancelling.IsCompleted && !Manager.Removed.Contains(task), "Removal did not await the cancelled request.");
            gate.Release.TrySetResult();
            await cancelling;
            Assert(running.IsCompleted && fixture.Metadata.ActiveRequests == 0, "A metadata request survived removal.");
        });
        AssertCancelled(task);
        Assert(fixture.Server.Headers.IsEmpty, "Metadata cancellation started a slice.");
        if (item is StorageFile file) Assert(!file.ReadStream.CanRead, "Cancelled upload leaked its stream.");
    }

    private static async Task CancelSlices()
    {
        await using var fixture = new UploadFixture();
        var slice = new OperationGate();
        var cleanup = new OperationGate();
        fixture.Server.AllowDisconnects = true;
        int slices = 0;
        fixture.Server.BeforeSliceResponse = token => Interlocked.Increment(ref slices) == 1 ? Task.CompletedTask : slice.WaitAsync(token);
        fixture.Server.BeforeDeleteResponse = cleanup.WaitAsync;
        var file = new StorageFile("slices.bin", 3 * 320 * 1024);
        var task = NewTask(fixture, file);
        int progressAtRemoval = -1;
        await TestUiContext.Run(async () =>
        {
            Task running = task.StartUpload();
            await slice.Entered.Task;
            Task cancelling = task.CancelTaskAsync();
            await cleanup.Entered.Task;
            Assert(task.IsCancelling && !cancelling.IsCompleted && !Manager.Removed.Contains(task), "Cleanup was not awaited.");
            Assert(ReferenceEquals(cancelling, task.CancelTaskAsync()), "Cleanup was duplicated.");
            await Task.Yield();
            cleanup.Release.TrySetResult();
            await cancelling;
            Assert(running.IsCompleted && !file.ReadStream.CanRead, "Upload resources survived removal.");
            progressAtRemoval = task.Progress;
        });
        AssertCancelled(task);
        Assert(task.Progress == progressAtRemoval, "A late progress callback changed the cancelled task.");
        Assert(fixture.Server.Headers.Count == 2 && fixture.Server.Deletes.Count == 1, "Cancellation started another slice or duplicated cleanup.");
        Assert(fixture.Server.Deletes.All(headers => !headers.ContainsKey("Authorization")), "Session cleanup leaked authentication.");
        Assert(fixture.Server.Paths.All(path => path == "/upload"), "Cleanup targeted an actual drive item.");
    }

    private static async Task CancelDescendants()
    {
        await using var fixture = new UploadFixture();
        await using var other = new UploadFixture();
        var slices = new OperationGate();
        var cleanup = new OperationGate();
        var bothSlices = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bothDeletes = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Server.AllowDisconnects = true;
        int enteredSlices = 0, enteredDeletes = 0;
        fixture.Server.BeforeSliceResponse = token =>
        {
            if (Interlocked.Increment(ref enteredSlices) == 2) bothSlices.TrySetResult();
            return slices.WaitAsync(token);
        };
        fixture.Server.BeforeDeleteResponse = token =>
        {
            if (Interlocked.Increment(ref enteredDeletes) == 2) bothDeletes.TrySetResult();
            return cleanup.WaitAsync(token);
        };
        var root = new StorageFolder("tree");
        var files = new[] { new StorageFile("a.bin", 640 * 1024), new StorageFile("b.bin", 640 * 1024) };
        foreach (var file in files)
        {
            var child = new StorageFolder(file.Name);
            child.Files.Add(file);
            child.Folders.Add(new StorageFolder("not-started"));
            root.Folders.Add(child);
        }
        var task = NewTask(fixture, root);
        var independent = NewTask(other, new StorageFile("independent.bin", 1));
        await TestUiContext.Run(async () =>
        {
            Task running = task.StartUpload();
            Task otherRunning = independent.StartUpload();
            await bothSlices.Task;
            Task cancelling = task.CancelTaskAsync();
            await bothDeletes.Task;
            Assert(!cancelling.IsCompleted && !Manager.Removed.Contains(task), "Parent was removed with descendants running.");
            cleanup.Release.TrySetResult();
            await cancelling;
            await otherRunning;
            Assert(running.IsCompleted && files.All(file => !file.ReadStream.CanRead), "A descendant survived removal.");
        });
        AssertCancelled(task);
        Assert(independent.Completed && !Manager.Removed.Contains(independent), "Cancellation affected an independent upload.");
        Assert(fixture.Metadata.Folders == 3 && fixture.Metadata.Sessions == 2 && fixture.Server.Headers.Count == 2,
            "Cancelled folder upload scheduled more descendants or slices.");
        Assert(fixture.Server.Deletes.Count == 2, "Not every unfinished session was cleaned up.");
    }

    private static async Task CancelScheduling()
    {
        await using var fixture = new UploadFixture();
        using var cancellation = new CancellationTokenSource();
        int secondOpened = 0;
        var first = new StorageFile("first.bin", 100) { BeforeOpen = () => { cancellation.Cancel(); return Task.CompletedTask; } };
        var root = new StorageFolder("root");
        root.Files.Add(first);
        root.Files.Add(new StorageFile("second.bin", 100) { BeforeOpen = () => { secondOpened++; return Task.CompletedTask; } });
        await ExpectCancelled(() => fixture.Provider.UploadFolderAsync(root, "root", cancellationToken: cancellation.Token));
        Assert(secondOpened == 0 && !first.ReadStream.CanRead && fixture.Metadata.Sessions == 0, "Cancellation did not stop scheduling.");
    }

    private static async Task CleanupFailure(bool timeout)
    {
        await using var fixture = new UploadFixture();
        var slice = new OperationGate();
        var cleanup = new OperationGate();
        fixture.Server.AllowDisconnects = true;
        fixture.Server.BeforeSliceResponse = slice.WaitAsync;
        if (timeout) fixture.Server.BeforeDeleteResponse = cleanup.WaitAsync;
        else fixture.Server.DeleteStatusCode = 403;
        var task = NewTask(fixture, new StorageFile("cleanup.bin", 640 * 1024));
        await TestUiContext.Run(async () =>
        {
            Task running = task.StartUpload();
            await slice.Entered.Task;
            await task.CancelTaskAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert(running.IsCompleted, "Cleanup failure left the upload running.");
        });
        AssertCancelled(task);
        Assert(fixture.Server.Deletes.Count == 1, "Cleanup did not reach the server with an independent token.");
    }

    private static async Task CompletionWins()
    {
        await using var fixture = new UploadFixture();
        var task = NewTask(fixture, new StorageFile("completed.bin", 100));
        Task cancelling = Task.CompletedTask;
        task.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(task.Progress) && task.Progress == 100) cancelling = task.CancelTaskAsync();
        };
        await TestUiContext.Run(async () =>
        {
            await task.StartUpload();
            await cancelling;
        });
        Assert(task.Completed && task.Progress == 100 && !task.HasFailed, "Cancellation overwrote confirmed success.");
        Assert(Manager.Removed.Count(item => item == task) == 1 && fixture.Server.Deletes.IsEmpty, "Completed upload was cleaned up or not removed.");
    }

    private static async Task UnrelatedCancellation()
    {
        await using var fixture = new UploadFixture();
        AssertFailed(await RunTask(fixture, new StorageFile("timeout.bin", 100) { PropertiesError = new TaskCanceledException("Unrelated timeout.") }));
    }

    private static async Task ConfirmedServiceResult()
    {
        await using var fixture = new UploadFixture();
        using var cancellation = new CancellationTokenSource();
        // The SDK reports the final slice before returning its confirmed result to the service.
        await fixture.Provider.UploadFileAsync(new StorageFile("confirmed.bin", 100), "root",
            new CancellingProgress(cancellation), cancellation.Token);
        Assert(cancellation.IsCancellationRequested && fixture.Server.Headers.Count == 1 && fixture.Server.Deletes.IsEmpty,
            "Cancellation discarded a confirmed service result or deleted its session.");
    }

    private static async Task FailedSiblingWaits()
    {
        await using var fixture = new UploadFixture();
        var slice = new OperationGate();
        var cleanup = new OperationGate();
        fixture.Metadata.RejectFileName = "denied.bin";
        fixture.Server.AllowDisconnects = true;
        fixture.Server.BeforeSliceResponse = slice.WaitAsync;
        fixture.Server.BeforeDeleteResponse = cleanup.WaitAsync;
        var folder = new StorageFolder("partial");
        folder.Files.Add(new StorageFile("denied.bin", 100));
        var sibling = new StorageFile("running.bin", 640 * 1024);
        folder.Files.Add(sibling);
        var task = NewTask(fixture, folder);
        await TestUiContext.Run(async () =>
        {
            Task running = task.StartUpload();
            await slice.Entered.Task;
            Assert(!running.IsCompleted, "A faulted file abandoned its running sibling.");
            Task cancelling = task.CancelTaskAsync();
            await cleanup.Entered.Task;
            Assert(!cancelling.IsCompleted && !Manager.Removed.Contains(task), "A fault skipped sibling cleanup.");
            cleanup.Release.TrySetResult();
            await cancelling;
            Assert(running.IsCompleted && !sibling.ReadStream.CanRead, "A sibling survived removal after failure.");
        });
        AssertFailed(task);
        Assert(Manager.Removed.Count(item => item == task) == 1 && fixture.Server.Deletes.Count == 1,
            "Failure/cancellation race duplicated removal or skipped cleanup.");
    }

    private sealed class CancellingProgress(CancellationTokenSource cancellation) : IProgress<long>
    {
        public void Report(long value) => cancellation.Cancel();
    }

    private static async Task RemoveTerminal()
    {
        await using var fixture = new UploadFixture();
        var completed = await RunTask(fixture, new StorageFile("completed.bin", 100));
        var failed = await RunTask(fixture, new StorageFile("failed.bin", 100) { OpenError = new IOException("Open failed.") });
        await TestUiContext.Run(async () =>
        {
            foreach (var task in new[] { completed, failed })
            {
                await task.CancelTaskAsync();
                await task.CancelTaskAsync();
                await task.StartUpload();
                Assert(Manager.Removed.Count(item => item == task) == 1, "Terminal task was removed twice.");
            }
        });
        Assert(completed.Completed && failed.HasFailed && fixture.Metadata.Sessions == 1 && fixture.Server.Deletes.IsEmpty,
            "Removing a terminal task changed its outcome or restarted upload.");
    }

    private static async Task PrecancelledServices()
    {
        await using var fixture = new UploadFixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var file = new StorageFile("not-opened.bin", 100);
        await ExpectCancelled(() => fixture.Provider.UploadFileAsync(file, "root", cancellationToken: cancellation.Token));
        await ExpectCancelled(() => fixture.Provider.UploadFolderAsync(new StorageFolder("not-created"), "root", cancellationToken: cancellation.Token));
        Assert(file.ReadStream == null && fixture.Metadata.Sessions == 0 && fixture.Metadata.Folders == 0, "Precancelled call performed work.");
    }

    private static async Task ExpectCancelled(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("Expected cancellation to propagate from the service.");
    }

    private sealed class OperationGate(bool deferCancellation = false)
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Exited { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task WaitAsync(CancellationToken token)
        {
            Entered.TrySetResult();
            try
            {
                await Release.Task.WaitAsync(token);
            }
            catch (OperationCanceledException)
            {
                Cancelled.TrySetResult();
                if (deferCancellation) await Release.Task;
                throw;
            }
            finally
            {
                Exited.TrySetResult();
            }
        }
    }
}
