using Microsoft.UI.Dispatching;
using OneDrive_Simple_Management_Tool.Models;
using OneDrive_Simple_Management_Tool.ViewModels;
using Windows.Storage;

internal static class ShutdownChecks
{
    public static async Task RunAsync(TestServices services)
    {
        await Case("Cancel exit keeps paused downloads and restores admission", async fixture =>
        {
            var manager = services.Manager;
            var download = AddDownload(fixture, manager);
            Task running = download.StartDownload();
            await Until(() => download.DownloadedBytes >= 32768);
            await download.PauseDownload();
            await running;
            long retained = download.DownloadedBytes;
            bool finished = false;
            bool allowed = await manager.RequestCloseAsync((uploads, downloads) =>
            {
                Check(uploads == 0 && downloads == 1 && manager.IsShuttingDown, "Paused task was omitted or admission stayed open.");
                return Task.FromResult(false);
            }, () => { finished = true; return Task.CompletedTask; });
            Check(!allowed && !finished && !manager.IsShuttingDown && download.IsPaused && download.DownloadedBytes == retained,
                "Cancel exit stopped or discarded a transfer.");
            await download.StartDownload();
            Check(download.Completed, "Cancel exit did not restore start admission.");
            await download.CancelTaskAsync();
        });

        await Case("Repeated close shares one confirmation; all writers drain before finishing", async fixture =>
        {
            var manager = services.Manager;
            var download = AddDownload(fixture, manager);
            var running = download.StartDownload();
            await Until(() => download.DownloadedBytes >= 32768);
            var releaseUpload = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var upload = new UploadTaskViewModel(null, "id", null) { NeedsExitProtection = true, OnCancel = () => releaseUpload.Task };
            manager.UploadTasks.Add(upload);
            var confirmed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            int prompts = 0;
            bool finished = false;
            Task<bool> closing = manager.RequestCloseAsync((up, down) =>
            {
                prompts++;
                Check(up == 1 && down == 1, "Confirmation counts are incorrect.");
                return confirmed.Task;
            }, () =>
            {
                Check(running.IsCompleted && upload.WasCancelled && releaseUpload.Task.IsCompleted,
                    "Shutdown finished before a writer drained.");
                finished = true;
                return Task.CompletedTask;
            });
            Check(ReferenceEquals(closing, manager.RequestCloseAsync((_, _) => throw new Exception("Duplicate prompt"), () => Task.CompletedTask)),
                "Repeated close launched a second coordinator.");
            await Until(() => prompts == 1);
            await manager.AddUploadTask(null, "blocked", null);
            await manager.AddDownloadTask(null, "blocked", null);
            await manager.StartAllDownloadTasks();
            Check(manager.UploadTasks.Count == 1 && manager.DownloadTasks.Count == 1, "New task admitted during exit confirmation.");
            confirmed.SetResult(true);
            await Until(() => upload.WasCancelled && running.IsCompleted);
            Check(!finished && !closing.IsCompleted, "Exit ignored a still-running upload.");
            releaseUpload.SetResult();
            Check(await closing && finished && manager.IsShuttingDown, "Exit did not finish after all tasks stopped.");
            Check(!Directory.EnumerateFiles(fixture.DirectoryPath, ".cloudflow-*").Any(), "Exit leaked download partial files.");
            manager.UploadTasks.Clear();
        });

        // A new application instance resets the admission gate after an accepted exit.
        services.Manager = new();
        await Case("Cleanup failure blocks exit and permits a subsequent successful close", async fixture =>
        {
            var manager = services.Manager;
            fixture.Server.Bytes = [1, 2, 3];
            var download = AddDownload(fixture, manager);
            await File.WriteAllTextAsync(download.DestinationPath, "existing");
            using (var targetLock = new FileStream(download.DestinationPath, FileMode.Open, FileAccess.Read, FileShare.None))
                await download.StartDownload();
            Check(download.HasFailed, "Destination lock did not retain the completed staging file.");
            string staging = Directory.EnumerateFiles(fixture.DirectoryPath, ".cloudflow-*.tmp").Single();
            bool finished = false;
            using (var locked = new FileStream(staging, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                try
                {
                    await manager.RequestCloseAsync((_, _) => Task.FromResult(true), () => { finished = true; return Task.CompletedTask; });
                    throw new Exception("Exit allowed failed cleanup.");
                }
                catch (InvalidOperationException) { }
            }
            Check(!finished && !manager.IsShuttingDown && manager.DownloadTasks.Contains(download) && download.HasFailed,
                "Cleanup failure lost the task or left admission frozen.");
            Check(await manager.RequestCloseAsync((_, _) => Task.FromResult(true), () => Task.CompletedTask), "Retry close did not clean retained data.");
            Check(!Directory.EnumerateFiles(fixture.DirectoryPath, ".cloudflow-*").Any(), "Retry close leaked partial files.");
        });

        services.Manager = new();
        await Case("Final save failure retains the window and completed files need no prompt", async fixture =>
        {
            var manager = services.Manager;
            fixture.Server.Bytes = [1, 2, 3];
            var download = AddDownload(fixture, manager);
            await download.StartDownload();
            try
            {
                await manager.RequestCloseAsync((_, _) => throw new Exception("Completed transfer prompted"), () => throw new IOException("save"));
                throw new Exception("Save failure permitted exit.");
            }
            catch (IOException) { }
            Check(!manager.IsShuttingDown && File.Exists(download.DestinationPath), "Failed save damaged completed file or froze admission.");
            Check(await manager.RequestCloseAsync((_, _) => throw new Exception("Completed transfer prompted"), () => Task.CompletedTask), "Close retry failed.");
            Check(File.Exists(download.DestinationPath), "Successful close removed a completed file.");
        });
    }

    private static DownloadTaskViewModel AddDownload(Fixture fixture, TaskManagerViewModel manager)
    {
        var provider = new SourceProvider { Resolve = token =>
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(new DownloadSource(fixture.Server.Url, fixture.Server.Bytes.Length, "v1"));
        } };
        var task = new DownloadTaskViewModel(new DriveViewModel(provider), "item", new StorageFile(Path.Combine(fixture.DirectoryPath, "download")));
        manager.DownloadTasks.Add(task);
        return task;
    }

    private static async Task Until(Func<bool> condition)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) { DispatcherQueue.Drain(); await Task.Delay(5, stop.Token); }
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task Case(string name, Func<Fixture, Task> run)
    {
        await using var fixture = new Fixture();
        await run(fixture).WaitAsync(TimeSpan.FromSeconds(25));
        DispatcherQueue.Drain();
        Console.WriteLine($"PASS {name}");
    }
}
