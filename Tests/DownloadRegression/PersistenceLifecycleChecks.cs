using Microsoft.UI.Dispatching;
using OneDrive_Simple_Management_Tool.Models;
using OneDrive_Simple_Management_Tool.Services;
using OneDrive_Simple_Management_Tool.ViewModels;
using Windows.Storage;

internal static class PersistenceLifecycleChecks
{
    public static async Task RunAsync(TestServices services)
    {
        await using var fixture = new Fixture();
        string directory = Path.Combine(fixture.DirectoryPath, "records");
        Directory.CreateDirectory(directory);
        string corrupt = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".dat");
        File.WriteAllBytes(corrupt, [1, 2, 3]);
        var manager = new TaskManagerViewModel();
        manager.Initialize(new TransferStore(directory), _ => throw new Exception("Corrupt data reached restoration."));
        Check(!manager.CanUseAccount("account", "drive") && manager.PersistenceError.Length > 0 && File.Exists(corrupt), "Corruption didn't preserve records and block admission.");
        File.Delete(corrupt);
        manager.RetryPersistence();
        Check(manager.CanUseAccount("account", "drive") && manager.PersistenceError == "", "Retry didn't restore admission.");
        Console.WriteLine("PASS durable task load failure preserves data and supports retry without blocking the application");

        services.Manager = manager;
        var provider = new SourceProvider { HomeAccountId = "account", DriveId = "drive", Resolve = _ => Task.FromResult(new DownloadSource(fixture.Server.Url, fixture.Server.Bytes.Length, "v1")) };
        var running = manager.AddDownloadTask(new DriveViewModel(provider), "file", new StorageFile(Path.Combine(fixture.DirectoryPath, "file.bin")));
        while (manager.DownloadTasks.Single().DownloadedBytes < 32768)
        {
            DispatcherQueue.Drain();
            await Task.Delay(10);
        }
        var task = manager.DownloadTasks.Single();
        Check(await manager.RequestCloseAsync((_, _) => Task.FromResult(true), () => Task.CompletedTask), "Save-and-exit failed.");
        await running;
        var restored = new TransferStore(directory).Load().Single();
        Check(manager.DownloadTasks.Contains(task) && restored.DownloadState == DownloadTaskState.Interrupted &&
            restored.DownloadResume?.Bytes > 0 && Directory.EnumerateFiles(fixture.DirectoryPath, ".cloudflow-*").Any(), "Exit lost the task or its durable byte checkpoint.");
        Console.WriteLine("PASS save-and-exit retains file records and durable partial bytes after draining writers");

        var completedManager = new TaskManagerViewModel();
        var store = new TransferStore(directory);
        completedManager.Initialize(store, record =>
        {
            record.DownloadState = DownloadTaskState.Completed;
            var session = new DownloadSession(record.Path, _ => throw new Exception("Completed task started a download."));
            var vm = new DownloadTaskViewModel(record.Name, session, completedManager, record.AccountId, record.DriveId);
            vm.AttachRecord(record, completedManager.SaveRecord, true);
            completedManager.DownloadTasks.Add(vm);
        });
        string journal = Path.Combine(directory, restored.Id.ToString("N") + ".dat.journal");
        using (var held = new FileStream(journal, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            try
            {
                await completedManager.RequestCloseAsync((_, _) => throw new Exception("Completed task prompted."), () => throw new Exception("Exit skipped a failed save."));
                throw new Exception("Save failure allowed exit.");
            }
            catch (IOException) { }
        }
        Check(!completedManager.IsShuttingDown && completedManager.DownloadTasks.Count == 1, "Save failure lost the card or froze admission.");
        Check(await completedManager.RequestCloseAsync((_, _) => Task.FromResult(true), () => Task.CompletedTask), "Exit retry couldn't save.");
        Check(new TransferStore(directory).Load().Single().DownloadState == DownloadTaskState.Completed, "Completion wasn't durable after retry.");
        Console.WriteLine("PASS failed final checkpoint blocks exit and a later retry saves it");
        services.Manager = new();
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
