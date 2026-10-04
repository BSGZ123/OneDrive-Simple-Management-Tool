using System.Collections.Concurrent;
using System.Net;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Dispatching;
using OneDrive_Simple_Management_Tool.Helpers;
using OneDrive_Simple_Management_Tool.Services;
using OneDrive_Simple_Management_Tool.ViewModels;
using Windows.Storage;

namespace OneDrive_Simple_Management_Tool.Tests.UploadRegression;

internal static partial class Program
{
    private static async Task Main()
    {
        Ioc.Default.ConfigureServices(new TestServices());
        (string Name, Func<Task> Run)[] cases =
        [
            ("Slices are anonymous and final byte count is exact", AnonymousSlices),
            ("An empty file uses a zero-length PUT", EmptyFile),
            ("A completely empty folder succeeds", () => EmptyFolder(false)),
            ("Nested folders containing only empty files succeed", () => EmptyFolder(true)),
            ("Folder progress includes all descendants exactly once", FolderProgress),
            ("Concurrent and duplicate progress reports are monotonic", ConcurrentProgress),
            ("A successful task remains at 100 after queued reports", CompletedState),
            ("File-open errors reach the task failure state", FileOpenFailure),
            ("File-property errors reach the task failure state", FilePropertiesFailure),
            ("Folder enumeration errors reach the task failure state", FolderEnumerationFailure),
            ("Upload-session errors propagate and never mark completion", SessionFailure),
            ("Slice rejection propagates and never marks completion", SliceFailure),
            ("Empty-file rejection never marks completion", EmptyFileFailure),
            ("An empty-file response without an item is rejected", EmptyFileMissingResult),
            ("A session without an upload URL is rejected", InvalidSession),
            ("A completed slice response without an item ID is rejected", InvalidCompletedItem),
            ("Folder creation errors never mark completion", FolderCreationFailure),
            ("A failed descendant prevents folder completion", DescendantFailure),
            .. CancellationCases()
        ];

        foreach (var test in cases)
        {
            await test.Run();
            Console.WriteLine($"PASS {test.Name}");
        }
        Console.WriteLine($"Passed {cases.Length} upload regression checks.");
    }

    private static async Task AnonymousSlices()
    {
        await using var fixture = new UploadFixture();
        var progress = new RecordingProgress();
        const int size = 320 * 1024 + 17;
        await fixture.Provider.UploadFileAsync(new StorageFile("large.bin", size), "root", progress);
        Assert(fixture.Metadata.Sessions == 1, "Expected one upload session.");
        Assert(fixture.Server.Headers.Count == 2, "Expected two real SDK slice requests.");
        Assert(fixture.Server.Headers.All(headers => !headers.ContainsKey("Authorization")), "A slice received an Authorization header.");
        Assert(progress.Values.First() == 320 * 1024, "Slice progress must count bytes, not report an offset.");
        Assert(progress.Values.Last() == size, "The final byte count must equal the stream length.");
    }

    private static async Task EmptyFile()
    {
        await using var fixture = new UploadFixture();
        var progress = new RecordingProgress();
        await fixture.Provider.UploadFileAsync(new StorageFile("empty.txt", 0), "root", progress);
        Assert(fixture.Metadata.EmptyFiles == 1 && fixture.Metadata.Sessions == 0, "Empty files must bypass upload sessions.");
        Assert(progress.Values.SequenceEqual(new long[] { 0 }), "An empty file must report zero bytes.");
    }

    private static async Task EmptyFolder(bool includeFiles)
    {
        await using var fixture = new UploadFixture();
        var folder = new StorageFolder("empty-tree");
        if (includeFiles)
        {
            folder.Files.Add(new StorageFile("empty.txt", 0));
            var child = new StorageFolder("child");
            child.Files.Add(new StorageFile("also-empty.txt", 0));
            folder.Folders.Add(child);
            folder.Folders.Add(new StorageFolder("empty-child"));
        }
        var task = await RunTask(fixture, folder);
        Assert(task.Completed && !task.HasFailed && !task.IsUploading && task.Progress == 100, "The zero-byte task must complete normally.");
        Assert(fixture.Metadata.Folders == (includeFiles ? 3 : 1), "Empty folder structure must be preserved.");
        Assert(fixture.Metadata.EmptyFiles == (includeFiles ? 2 : 0), "All empty files must be created.");
    }

    private static async Task FolderProgress()
    {
        await using var fixture = new UploadFixture();
        var folder = new StorageFolder("root");
        folder.Files.AddRange([new StorageFile("a.bin", 1100), new StorageFile("b.bin", 2300)]);
        var child = new StorageFolder("child");
        child.Files.Add(new StorageFile("c.bin", 320 * 1024 + 23));
        var sibling = new StorageFolder("sibling");
        sibling.Files.Add(new StorageFile("d.bin", 1700));
        child.Folders.Add(new StorageFolder("empty"));
        folder.Folders.AddRange([child, sibling]);
        long total = (long)await Utils.GetFolderSize(folder);
        var progress = new RecordingProgress();
        await fixture.Provider.UploadFolderAsync(folder, "root", progress);
        long[] reports = progress.Values.ToArray();
        Assert(reports.Last() == total, "Descendant bytes are missing or counted twice.");
        Assert(reports.All(value => value >= 0 && value <= total), "Folder progress is outside the byte range.");
        Assert(reports.SequenceEqual(reports.Order()), "Folder progress went backwards.");
    }

    private static Task ConcurrentProgress()
    {
        var reports = new RecordingProgress();
        var tracker = new UploadProgressTracker(reports);
        Parallel.For(0, 100, _ =>
        {
            var file = tracker.CreateFileProgress();
            file.Report(500);
            file.Report(500);
            file.Report(200);
            file.Report(1000);
        });
        long[] values = reports.Values.ToArray();
        Assert(values.Last() == 100_000, "Concurrent files or retries were double-counted.");
        Assert(values.SequenceEqual(values.Order()), "Concurrent reports went backwards.");
        return Task.CompletedTask;
    }

    private static async Task CompletedState()
    {
        await using var fixture = new UploadFixture();
        var task = await RunTask(fixture, new StorageFile("success.bin", 640 * 1024 + 1));
        Assert(task.Completed && !task.HasFailed && !task.IsUploading && task.Progress == 100, "Queued callbacks overwrote the completed task.");
        Assert(task.ErrorMessage == string.Empty, "A successful task retained an error.");
    }

    private static async Task FileOpenFailure()
    {
        await using var fixture = new UploadFixture();
        var file = new StorageFile("locked.bin", 100) { OpenError = new IOException("Simulated locked file.") };
        await ExpectFailure(() => fixture.Provider.UploadFileAsync(file, "root"));
        AssertFailed(await RunTask(fixture, file));
        Assert(fixture.Metadata.Sessions == 0, "A locked file must not start an upload session.");
    }

    private static async Task FilePropertiesFailure()
    {
        await using var fixture = new UploadFixture();
        var file = new StorageFile("missing.bin", 100) { PropertiesError = new IOException("Simulated missing file.") };
        AssertFailed(await RunTask(fixture, file));
        Assert(fixture.Metadata.Sessions == 0, "A failed size lookup must not start an upload session.");
    }

    private static async Task FolderEnumerationFailure()
    {
        await using var fixture = new UploadFixture();
        var folder = new StorageFolder("unreadable") { EnumerationError = new UnauthorizedAccessException("Simulated unreadable folder.") };
        AssertFailed(await RunTask(fixture, folder));
    }

    private static async Task SessionFailure()
    {
        await using var fixture = new UploadFixture();
        fixture.Metadata.SessionStatus = HttpStatusCode.Forbidden;
        var file = new StorageFile("denied.bin", 100);
        await ExpectFailure(() => fixture.Provider.UploadFileAsync(file, "root"));
        AssertFailed(await RunTask(fixture, file));
    }

    private static async Task SliceFailure()
    {
        await using var fixture = new UploadFixture();
        fixture.Server.StatusCode = 401;
        var file = new StorageFile("rejected.bin", 100);
        await ExpectFailure(() => fixture.Provider.UploadFileAsync(file, "root"));
        AssertFailed(await RunTask(fixture, file));
        Assert(fixture.Metadata.Sessions == 2 && fixture.Server.Headers.Count >= 2, "The service and task must both reach the rejecting slice endpoint; SDK retries are allowed.");
    }

    private static async Task EmptyFileFailure()
    {
        await using var fixture = new UploadFixture();
        fixture.Metadata.EmptyFileStatus = HttpStatusCode.Forbidden;
        AssertFailed(await RunTask(fixture, new StorageFile("empty.txt", 0)));
    }

    private static async Task EmptyFileMissingResult()
    {
        await using var fixture = new UploadFixture();
        fixture.Metadata.EmptyFileStatus = HttpStatusCode.NoContent;
        AssertFailed(await RunTask(fixture, new StorageFile("empty.txt", 0)));
    }

    private static async Task InvalidSession()
    {
        await using var fixture = new UploadFixture();
        fixture.Metadata.InvalidSession = true;
        AssertFailed(await RunTask(fixture, new StorageFile("file.bin", 100)));
    }

    private static async Task InvalidCompletedItem()
    {
        await using var fixture = new UploadFixture();
        fixture.Server.OmitItemId = true;
        AssertFailed(await RunTask(fixture, new StorageFile("file.bin", 100)));
    }

    private static async Task FolderCreationFailure()
    {
        await using var fixture = new UploadFixture();
        fixture.Metadata.FolderStatus = HttpStatusCode.Forbidden;
        AssertFailed(await RunTask(fixture, new StorageFolder("denied")));
    }

    private static async Task DescendantFailure()
    {
        await using var fixture = new UploadFixture();
        fixture.Metadata.RejectFileName = "denied.bin";
        var folder = new StorageFolder("partial");
        folder.Files.Add(new StorageFile("ok.bin", 100));
        var child = new StorageFolder("child");
        child.Files.Add(new StorageFile("denied.bin", 100));
        folder.Folders.Add(child);
        AssertFailed(await RunTask(fixture, folder));
        Assert(fixture.Server.Headers.Count == 1, "The successful file should upload before the descendant fails.");
    }

    private static async Task<UploadTaskViewModel> RunTask(UploadFixture fixture, IStorageItem item)
    {
        var task = new UploadTaskViewModel(new DriveViewModel(fixture.Provider), "root", item);
        await TestUiContext.Run(task.StartUpload);
        return task;
    }

    private static void AssertFailed(UploadTaskViewModel task)
    {
        Assert(task.HasFailed && !task.Completed && !task.IsUploading, "A failed task has an incorrect terminal state.");
        Assert(task.Progress < 100, "A failed task must not show 100 percent.");
        Assert(!string.IsNullOrWhiteSpace(task.ErrorMessage), "A failed task must expose its error.");
    }

    private static async Task ExpectFailure(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception)
        {
            return;
        }
        throw new InvalidOperationException("The service swallowed an upload failure.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class RecordingProgress : IProgress<long>
    {
        public ConcurrentQueue<long> Values { get; } = new();
        public void Report(long value) => Values.Enqueue(value);
    }

    private sealed class TestServices : IServiceProvider
    {
        private readonly TaskManagerViewModel _manager = new();
        public object GetService(Type serviceType) => serviceType == typeof(TaskManagerViewModel) ? _manager : null;
    }
}
