using System.Net;
using Microsoft.UI.Dispatching;
using OneDrive_Simple_Management_Tool.Models;
using OneDrive_Simple_Management_Tool.ViewModels;
using Windows.Storage;

namespace OneDrive_Simple_Management_Tool.Tests.UploadRegression;

internal static partial class Program
{
    private static (string, Func<Task>)[] ConflictCases() =>
    [
        ("Every file upload starts with server-enforced fail semantics", DefaultConflictPolicy),
        ("Empty and nonempty conflicts honor replace, skip and keep both", FileConflictChoices),
        ("No resolver or a permission failure never enables replacement", NoImplicitReplacement),
        ("A conflict on the final slice is cleaned up before a new decision", LateConflict),
        ("Concurrent batch conflicts serialize and only remember explicit choices", BatchChoices),
        ("Folder conflicts skip, rename or merge without deleting cloud items", FolderChoices),
        ("A file cannot be replaced by merging a folder", FolderFileCollision),
        ("Cancelling a folder conflict drains waiting descendants", CancelFolderConflict),
        ("Skipped tasks have a distinct terminal result and cannot restart", SkippedTask),
        ("Cancelling a decision cancels the upload without reporting failure", CancelDecision),
        ("Shutdown freezes upload starts", ShutdownPreventsStart)
    ];

    private static UploadConflictResolver Resolve(UploadConflictChoice choice) =>
        new((_, _) => Task.FromResult(new UploadConflictDecision(choice)));

    private static async Task DefaultConflictPolicy()
    {
        await using var fixture = new UploadFixture();
        await fixture.Provider.UploadFileAsync(new StorageFile("empty", 0), "root");
        await fixture.Provider.UploadFileAsync(new StorageFile("file", 100), "root");
        await fixture.Provider.UploadFolderAsync(new StorageFolder("folder"), "root");
        Assert(fixture.Metadata.Requests.All(request => request.Behavior == "fail"), "An unapproved request could overwrite an item.");
    }

    private static async Task FileConflictChoices()
    {
        foreach (int size in new[] { 0, 100 })
        foreach (var choice in new[] { UploadConflictChoice.Replace, UploadConflictChoice.Skip, UploadConflictChoice.KeepBoth })
        {
            await using var fixture = new UploadFixture();
            fixture.Metadata.Conflicts = _ => true;
            int prompts = 0;
            var resolver = new UploadConflictResolver((conflict, _) =>
            {
                Assert(conflict.Name == "same.txt" && !conflict.IsFolder, "Wrong conflict identity.");
                Assert(fixture.Server.Headers.IsEmpty, "Bytes were sent before the name conflict was resolved.");
                prompts++;
                return Task.FromResult(new UploadConflictDecision(choice));
            });
            var outcome = await fixture.Provider.UploadFileAsync(new StorageFile("same.txt", size), "root", conflicts: resolver);
            bool skip = choice == UploadConflictChoice.Skip;
            Assert(prompts == 1 && outcome.Skipped == (skip ? 1 : 0) && outcome.Uploaded == (skip ? 0 : 1), "Wrong conflict outcome.");
            var requests = fixture.Metadata.Requests.ToArray();
            Assert(requests[0].Behavior == "fail" && requests.Length == (skip ? 1 : 2), "Skip sent another mutation or choice was ignored.");
            if (!skip) Assert(requests[1].Behavior == (choice == UploadConflictChoice.Replace ? "replace" : "rename"), "Wrong server behavior.");
        }
    }

    private static async Task NoImplicitReplacement()
    {
        await using var fixture = new UploadFixture();
        fixture.Metadata.Conflicts = _ => true;
        await ExpectFailure(() => fixture.Provider.UploadFileAsync(new StorageFile("same", 100), "root"));
        fixture.Metadata.Conflicts = _ => false;
        fixture.Metadata.SessionStatus = HttpStatusCode.Forbidden;
        int prompts = 0;
        await ExpectFailure(() => fixture.Provider.UploadFileAsync(new StorageFile("denied", 100), "root", conflicts:
            new((_, _) => { prompts++; return Task.FromResult(new UploadConflictDecision(UploadConflictChoice.Replace)); })));
        Assert(prompts == 0 && fixture.Metadata.Requests.All(request => request.Behavior == "fail"), "Non-conflict failure enabled replacement.");
    }

    private static async Task LateConflict()
    {
        await using var fixture = new UploadFixture();
        fixture.Server.StatusCode = 409;
        fixture.Server.ErrorCode = "nameAlreadyExists";
        int prompts = 0;
        var resolver = new UploadConflictResolver((_, _) =>
        {
            Assert(fixture.Server.Deletes.Count == 1, "The old session was not cleaned before prompting.");
            fixture.Server.StatusCode = 200;
            prompts++;
            return Task.FromResult(new UploadConflictDecision(UploadConflictChoice.KeepBoth));
        });
        var outcome = await fixture.Provider.UploadFileAsync(new StorageFile("late", 100), "root", conflicts: resolver);
        Assert(prompts == 1 && outcome.Uploaded == 1 && fixture.Metadata.Sessions == 2, "Late conflict was not resolved.");
        Assert(fixture.Metadata.Requests.Last().Behavior == "rename", "Late conflict silently replaced the file.");
    }

    private static async Task BatchChoices()
    {
        int prompts = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resolver = new UploadConflictResolver(async (_, _) =>
        {
            Interlocked.Increment(ref prompts);
            entered.TrySetResult();
            await release.Task;
            return new(UploadConflictChoice.Skip, true);
        });
        Task<UploadConflictChoice> first = resolver.ResolveAsync(new("a", false), default);
        await entered.Task;
        using var cancellation = new CancellationTokenSource();
        var cancelled = resolver.ResolveAsync(new("cancelled", false), cancellation.Token);
        cancellation.Cancel();
        try { await cancelled; throw new Exception("Queued prompt ignored cancellation."); }
        catch (OperationCanceledException) { }
        var rest = Enumerable.Range(0, 5).Select(i => resolver.ResolveAsync(new(i.ToString(), false), default)).ToArray();
        release.SetResult();
        Assert((await Task.WhenAll(rest.Append(first))).All(choice => choice == UploadConflictChoice.Skip) && prompts == 1, "Batch choice was not serialized or remembered.");
        var nextBatch = new UploadConflictResolver((_, _) => { prompts++; return Task.FromResult(new UploadConflictDecision(UploadConflictChoice.KeepBoth)); });
        await nextBatch.ResolveAsync(new("a", false), default);
        await nextBatch.ResolveAsync(new("b", false), default);
        Assert(prompts == 3, "Choice leaked into a different batch or was remembered without opt-in.");
    }

    private static async Task FolderChoices()
    {
        foreach (var choice in new[] { UploadConflictChoice.Replace, UploadConflictChoice.Skip, UploadConflictChoice.KeepBoth })
        {
            await using var fixture = new UploadFixture();
            fixture.Metadata.Conflicts = path => path.EndsWith("children");
            var folder = new StorageFolder("folder");
            folder.Files.Add(new StorageFile("child", 100));
            var outcome = await fixture.Provider.UploadFolderAsync(folder, "root", conflicts: Resolve(choice));
            Assert(fixture.Metadata.Requests.All(request => request.Method != "DELETE"), "Folder merge deleted a cloud item.");
            if (choice == UploadConflictChoice.Skip)
                Assert(outcome.Skipped == 1 && outcome.Uploaded == 0 && fixture.Metadata.Sessions == 0, "Skipped folder uploaded descendants.");
            else if (choice == UploadConflictChoice.Replace)
                Assert(fixture.Metadata.Requests.Any(request => request.Path.Contains("existing-folder") && request.Behavior == "fail"), "Merge did not target the existing folder safely.");
            else
                Assert(fixture.Metadata.Requests.Any(request => request.Behavior == "rename") && fixture.Metadata.Sessions == 1, "Keep both failed to create a new folder.");
        }
    }

    private static async Task FolderFileCollision()
    {
        await using var fixture = new UploadFixture();
        fixture.Metadata.Conflicts = _ => true;
        fixture.Metadata.ExistingFolderIsFile = true;
        await ExpectFailure(() => fixture.Provider.UploadFolderAsync(new StorageFolder("folder"), "root", conflicts: Resolve(UploadConflictChoice.Replace)));
        Assert(fixture.Metadata.Requests.All(request => request.Method != "DELETE"), "Type collision deleted the existing file.");
    }

    private static async Task CancelFolderConflict()
    {
        await using var fixture = new UploadFixture();
        fixture.Metadata.Conflicts = path => path.EndsWith("createUploadSession");
        var folder = new StorageFolder("folder");
        folder.Files.AddRange([new StorageFile("a", 100), new StorageFile("b", 100)]);
        try
        {
            await fixture.Provider.UploadFolderAsync(folder, "root", conflicts: Resolve(UploadConflictChoice.Cancel)).WaitAsync(TimeSpan.FromSeconds(5));
            throw new Exception("Cancellation was swallowed.");
        }
        catch (UploadAbortedException) { }
        Assert(fixture.Metadata.ActiveRequests == 0 && fixture.Server.Headers.IsEmpty, "Cancelled descendants are still active.");
    }

    private static async Task SkippedTask()
    {
        await using var fixture = new UploadFixture();
        fixture.Metadata.Conflicts = _ => true;
        var task = new UploadTaskViewModel(new DriveViewModel(fixture.Provider), "root", new StorageFile("skip", 0), Resolve(UploadConflictChoice.Skip));
        await TestUiContext.Run(task.StartUpload);
        Assert(task.IsSkipped && task.IsTerminal && !task.Completed && !task.HasFailed && !task.NeedsExitProtection && task.Notice.Contains('1'), "Skip was reported as success or failure.");
        await TestUiContext.Run(task.StartUpload);
        Assert(fixture.Metadata.EmptyFiles == 1, "Skipped task restarted.");
    }

    private static async Task CancelDecision()
    {
        await using var fixture = new UploadFixture();
        fixture.Metadata.Conflicts = _ => true;
        var task = new UploadTaskViewModel(new DriveViewModel(fixture.Provider), "root", new StorageFile("cancel", 100), Resolve(UploadConflictChoice.Cancel));
        await TestUiContext.Run(task.StartUpload);
        Assert(task.IsCancelled && !task.HasFailed && !task.NeedsExitProtection, "Cancelled prompt reported an error or remained active.");
    }

    private static async Task ShutdownPreventsStart()
    {
        await using var fixture = new UploadFixture();
        var task = NewTask(fixture, new StorageFile("blocked", 100));
        Manager.IsShuttingDown = true;
        try { await TestUiContext.Run(task.StartUpload); }
        finally { Manager.IsShuttingDown = false; }
        Assert(fixture.Metadata.Sessions == 0 && task.State == UploadTaskState.Pending, "Upload started during shutdown.");
    }
}
