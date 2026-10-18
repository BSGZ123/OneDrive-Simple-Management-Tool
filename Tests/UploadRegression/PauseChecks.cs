using OneDrive_Simple_Management_Tool.Models;
using OneDrive_Simple_Management_Tool.Services;
using OneDrive_Simple_Management_Tool.ViewModels;
using System.Text.Json;
using Microsoft.UI.Dispatching;

namespace OneDrive_Simple_Management_Tool.Tests.UploadRegression;

internal static partial class Program
{
    private static (string, Func<Task>)[] PauseCases() =>
    [
        ("Pending and queued uploads pause without network work and resume once", PauseQueued),
        ("Upload pause retains confirmed bytes, completed siblings and restores paused state", PauseAndRestore),
        ("Remove during upload pause drains the request and wins over pause", RemoveDuringPause),
        ("Pausing a conflict decision closes the wait and allows continuation", PauseConflict),
        ("Failed and cancelled uploads keep their states on exit", PreserveExitState)
    ];

    private static UploadTaskViewModel DurableTask(UploadFixture fixture, string path, Action<TransferRecord> save = null,
        UploadConflictResolver conflicts = null)
    {
        fixture.Provider.IsAuthenticated = true;
        var task = new UploadTaskViewModel(new DriveViewModel(fixture.Provider), "root", null, conflicts);
        task.AttachRecord(new TransferRecord { Direction = TransferDirection.Upload, Name = Path.GetFileName(path), Path = path,
            ItemId = "root", AccountId = "test-account", DriveId = "test-drive" }, save ?? (_ => { }));
        return task;
    }

    private static async Task PauseQueued()
    {
        await using var fixture = new UploadFixture();
        using var first = await TransferScheduler.Shared.EnterAsync(default);
        using var second = await TransferScheduler.Shared.EnterAsync(default);
        using var third = await TransferScheduler.Shared.EnterAsync(default);
        var task = DurableTask(fixture, "empty.bin");
        await TestUiContext.Run(async () =>
        {
            await task.PauseUpload();
            Assert(task.IsPaused && task.CanResume && !task.CanRetry && fixture.Metadata.EmptyFiles == 0, "Pending pause started work.");
            var running = task.ResumeUpload();
            await Task.Yield();
            var pause = task.PauseUpload();
            Assert(ReferenceEquals(pause, task.PauseUpload()), "Repeated pause created another operation.");
            await pause;
            Assert(running.IsCompleted && task.IsPaused && fixture.Metadata.EmptyFiles == 0, "Queued pause did not drain the scheduler wait.");
            third.Dispose();
            await task.ResumeUpload();
            Assert(task.Completed && fixture.Metadata.EmptyFiles == 1, "Queued upload did not resume exactly once.");
            await task.PauseUpload();
            Assert(task.Completed, "Pause changed a completed upload.");
            await task.CancelTaskAsync();
        });
    }

    private static async Task PauseAndRestore()
    {
        await using var fixture = new UploadFixture();
        string path = Path.Combine(Path.GetTempPath(), "cloudflow-pause-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllBytesAsync(path, new byte[2 * ResumableUploadSession.SliceSize + 1234]);
        var gate = new OperationGate();
        fixture.Server.AllowDisconnects = true;
        int slices = 0;
        fixture.Server.BeforeSliceResponse = token => Interlocked.Increment(ref slices) == 2 ? gate.WaitAsync(token) : Task.CompletedTask;
        try
        {
            var task = DurableTask(fixture, path);
            // Model an already confirmed sibling; restarting must never upload it again.
            task.Record.Uploads.Add(new() { Key = "done", Name = "done", Size = 10, State = TransferEntryState.Completed, TargetId = "confirmed" });
            task.Record.Uploads.Add(new() { Key = ".", Name = task.Name, Size = new FileInfo(path).Length });
            await TestUiContext.Run(async () =>
            {
                var running = task.StartUpload();
                await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await task.PauseUpload();
                Assert(running.IsCompleted && task.IsPaused && !task.HasFailed && task.Progress > 0, "Pause lost progress or became failure.");
                var entry = task.Record.Uploads.Single(x => x.Key == ".");
                Assert(entry.Resume?.Bytes == ResumableUploadSession.SliceSize && fixture.Server.Deletes.IsEmpty, "Pause deleted its session or checkpoint.");
                await task.StopForExitAsync();
                var restoredRecord = JsonSerializer.Deserialize<TransferRecord>(JsonSerializer.Serialize(task.Record));
                task.Controls.Dispose();
                var restored = new UploadTaskViewModel(task.Drive, "root", null);
                restored.AttachRecord(restoredRecord, _ => { }, true);
                Assert(restored.IsPaused && restored.CanResume && !restored.CanRetry, "Restore forgot explicit pause.");
                fixture.Server.ResumeOffset = entry.Resume.Bytes;
                gate.Release.TrySetResult();
                await restored.ResumeUpload();
                Assert(restored.Completed && fixture.Metadata.Sessions == 1 && restoredRecord.Uploads[0].TargetId == "confirmed",
                    "Continue recreated the session or uploaded a completed sibling.");
                Assert(fixture.Server.Headers.Skip(2).First()["Content-Range"].StartsWith($"bytes {ResumableUploadSession.SliceSize}-"),
                    "Continue restarted the file from zero.");
                await restored.CancelTaskAsync();
            });
        }
        finally { gate.Release.TrySetResult(); File.Delete(path); }
    }

    private static async Task RemoveDuringPause()
    {
        await using var fixture = new UploadFixture();
        var gate = new OperationGate(deferCancellation: true);
        fixture.Metadata.BeforeResponse = (_, token) => gate.WaitAsync(token);
        var task = DurableTask(fixture, "empty.bin");
        await TestUiContext.Run(async () =>
        {
            var running = task.StartUpload();
            await gate.Entered.Task;
            var pause = task.PauseUpload();
            await gate.Cancelled.Task;
            var remove = task.CancelTaskAsync();
            Assert(!remove.IsCompleted && !pause.IsCompleted && task.IsCancelling, "Pause/remove abandoned an active request.");
            gate.Release.TrySetResult();
            await Task.WhenAll(running, pause, remove);
            AssertCancelled(task);
            Assert(!task.CanResume && !task.CanPause, "Removed upload remains resumable.");
        });
    }

    private static async Task PauseConflict()
    {
        await using var fixture = new UploadFixture();
        fixture.Metadata.Conflicts = _ => true;
        var gate = new OperationGate();
        var resolver = new UploadConflictResolver(async (_, token) => { await gate.WaitAsync(token); return new(UploadConflictChoice.Replace); });
        var task = DurableTask(fixture, "empty.bin", conflicts: resolver);
        await TestUiContext.Run(async () =>
        {
            var running = task.StartUpload();
            await gate.Entered.Task;
            Assert(task.IsWaitingForConflict && task.CanPause, "Conflict wait cannot pause.");
            await task.PauseUpload();
            Assert(running.IsCompleted && gate.Cancelled.Task.IsCompleted && task.IsPaused, "Pause left the conflict wait running.");
            fixture.Metadata.Conflicts = _ => false;
            await task.ResumeUpload();
            Assert(task.Completed, "Conflict pause could not continue.");
            await task.CancelTaskAsync();
        });
    }

    private static async Task PreserveExitState()
    {
        await using var fixture = new UploadFixture();
        foreach (var state in new[] { UploadTaskState.Failed, UploadTaskState.Cancelled, UploadTaskState.Paused })
        {
            var task = DurableTask(fixture, "empty.bin");
            task.State = state;
            await TestUiContext.Run(task.StopForExitAsync);
            Assert(task.State == state, "Exit changed a failed, cancelled or paused task to interrupted.");
            task.Controls.Dispose();
        }
    }
}
