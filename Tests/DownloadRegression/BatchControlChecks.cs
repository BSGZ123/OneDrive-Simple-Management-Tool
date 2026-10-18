using Microsoft.UI.Dispatching;
using OneDrive_Simple_Management_Tool.Models;
using OneDrive_Simple_Management_Tool.Services;
using OneDrive_Simple_Management_Tool.ViewModels;

internal static class BatchControlChecks
{
    public static async Task RunAsync()
    {
        await using var fixture = new Fixture();
        var manager = new TaskManagerViewModel();
        string records = Path.Combine(fixture.DirectoryPath, "records");
        var store = new TransferStore(records);
        manager.Initialize(store, _ => throw new Exception("Unexpected saved task."));
        Check(!manager.PauseAllCommand.CanExecute("Download") && !manager.ResumeAllCommand.CanExecute("Upload") &&
            !manager.ClearCompletedCommand.CanExecute("Download"), "Empty commands must be disabled.");
        int notifications = 0;
        manager.PauseAllCommand.CanExecuteChanged += (_, _) => notifications++;

        DownloadTaskViewModel Add(string name, DownloadTaskState state = DownloadTaskState.Pending, bool fails = false)
        {
            string path = Path.Combine(fixture.DirectoryPath, name);
            var record = new TransferRecord { Name = name, Path = path, Direction = TransferDirection.Download, DownloadState = state,
                AccountId = "account", DriveId = "drive", ItemId = name };
            var session = fails ? new DownloadSession(path, _ => throw new DownloadFailureException(DownloadFailure.AccessDenied)) : fixture.Create(name);
            var task = new DownloadTaskViewModel(name, session, manager);
            task.AttachRecord(record, manager.SaveRecord, state != DownloadTaskState.Pending);
            store.Save(record);
            manager.DownloadTasks.Add(task);
            return task;
        }

        var active = Add("active.bin");
        var pending = Add("pending.bin");
        var failed = Add("failed.bin", DownloadTaskState.Failed, true);
        var interrupted = Add("interrupted.bin", DownloadTaskState.Interrupted);
        var complete = Add("complete.bin", DownloadTaskState.Completed);
        await File.WriteAllTextAsync(complete.DestinationPath, "keep completed file");
        var otherDirection = new UploadTaskViewModel(null, "root", null) { State = UploadTaskState.Uploading };
        manager.UploadTasks.Add(otherDirection);
        var running = active.StartDownload();
        await Until(() => active.DownloadedBytes > 32768);
        await manager.PauseAll("Download");
        await running;
        Check(active.IsPaused && pending.IsPaused && failed.HasFailed && interrupted.State == DownloadTaskState.Interrupted &&
            complete.Completed && otherDirection.State == UploadTaskState.Uploading, "Pause all affected the wrong states/direction.");
        Check(notifications > 0 && !manager.PauseAllCommand.CanExecute("Download") && manager.ResumeAllCommand.CanExecute("Download"),
            "Batch commands did not react to state/collection changes.");
        await active.StopForExitAsync();
        Check(store.Load().Single(x => x.Id == active.Record.Id).DownloadState == DownloadTaskState.Paused,
            "Save-and-exit discarded explicit pause.");
        fixture.Server.DelayMilliseconds = 0;
        await manager.ResumeAll("Download");
        DispatcherQueue.Drain();
        Check(active.Completed && pending.Completed && interrupted.Completed && failed.HasFailed,
            "Continue all retried failures or skipped resumable tasks.");
        foreach (var task in new[] { active, pending, interrupted })
            Check(File.ReadAllBytes(task.DestinationPath).SequenceEqual(fixture.Server.Bytes), "Resume corrupted file content.");
        await manager.RetryFailed("Download");
        Check(failed.HasFailed && active.Completed, "Retry failed changed successful tasks.");

        // A failed deletion must keep its card; successful siblings still clear.
        string marker = Path.Combine(records, complete.Record.Id.ToString("N") + ".dat.removed");
        using (var held = new FileStream(marker, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None))
        {
            await manager.ClearCompleted("Download");
            Check(manager.DownloadTasks.Count == 2 && manager.DownloadTasks.Contains(complete) && manager.DownloadTasks.Contains(failed),
                "Clear completed lost a failed removal or removed unfinished work.");
        }
        File.Delete(marker);
        await manager.ClearCompleted("Download");
        Check(manager.DownloadTasks.SequenceEqual(new[] { failed }) && store.Load().Single().Id == failed.Record.Id,
            "Clear completed left durable records or removed failures.");
        Check(await File.ReadAllTextAsync(complete.DestinationPath) == "keep completed file" && File.Exists(active.DestinationPath),
            "Clear completed deleted downloaded files.");
        await failed.CancelTaskAsync();
        Check(!manager.ClearCompletedCommand.CanExecute("Download"), "Clear remains enabled after collection removal.");
        Console.WriteLine("PASS bulk downloads: scoped pause/continue/retry, state notifications, durable cleanup failures and file retention");

        var pausedUpload = new UploadTaskViewModel(null, "root", null) { State = UploadTaskState.Paused };
        var failedUpload = new UploadTaskViewModel(null, "root", null) { State = UploadTaskState.Failed };
        var doneUpload = new UploadTaskViewModel(null, "root", null) { State = UploadTaskState.Completed };
        manager.UploadTasks.Add(pausedUpload); manager.UploadTasks.Add(failedUpload); manager.UploadTasks.Add(doneUpload);
        doneUpload.OnCancel = () => { manager.RemoveSelectedUploadTasks(doneUpload); return Task.CompletedTask; };
        await manager.ResumeAll("Upload");
        Check(pausedUpload.Starts == 1 && failedUpload.Starts == 0 && doneUpload.Starts == 0, "Upload continue selection is wrong.");
        await manager.PauseAll("Upload");
        Check(pausedUpload.State == UploadTaskState.Paused && otherDirection.State == UploadTaskState.Paused, "Upload pause missed active tasks.");
        await manager.RetryFailed("Upload");
        Check(failedUpload.Starts == 1 && pausedUpload.Starts == 1, "Upload retry included paused tasks.");
        await manager.ClearCompleted("Upload");
        Check(!manager.UploadTasks.Contains(doneUpload) && !failedUpload.WasCancelled && !pausedUpload.WasCancelled,
            "Upload clear removed unfinished tasks.");
        Check(!manager.PauseAllCommand.CanExecute("invalid"), "Invalid direction admitted work.");
        await manager.RequestCloseAsync((_, _) =>
        {
            Check(!manager.ResumeAllCommand.CanExecute("Upload") && !manager.RetryFailedCommand.CanExecute("Upload"),
                "Batch commands remain enabled during close confirmation.");
            return Task.FromResult(false);
        }, () => Task.CompletedTask);
        Check(manager.ResumeAllCommand.CanExecute("Upload"), "Declining exit did not restore batch commands.");
        manager.UploadTasks.Clear();
        Console.WriteLine("PASS bulk upload selection and close admission (upload lifecycle covered by UploadRegression)");
    }

    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) { DispatcherQueue.Drain(); await Task.Delay(5, timeout.Token); }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
