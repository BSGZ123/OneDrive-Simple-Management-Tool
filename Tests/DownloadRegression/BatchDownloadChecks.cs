using Microsoft.UI.Dispatching;
using OneDrive_Simple_Management_Tool.Helpers;
using OneDrive_Simple_Management_Tool.Models;
using OneDrive_Simple_Management_Tool.Services;
using OneDrive_Simple_Management_Tool.ViewModels;

internal static class BatchDownloadChecks
{
    public static async Task RunAsync(TestServices services)
    {
        await Test("nested folders, empty directories and mixed root files preserve structure", async (f, source) =>
        {
            source.Folder("folder", "Documents");
            source.Folder("empty", "Empty", "folder");
            source.Folder("sub", "子目录", "folder");
            source.File("nested", "内容.bin", "sub");
            source.File("zero", "zero.txt", "folder", 0);
            source.File("loose", "loose.bin");
            var batch = Batch(f, source, "folder", "loose");
            await batch.StartAsync();
            Complete(batch, 3, 3);
            Check(Directory.Exists(Path.Combine(f.DirectoryPath, "Documents", "Empty")), "Empty directory missing.");
            Check(File.ReadAllBytes(Path.Combine(f.DirectoryPath, "Documents", "子目录", "内容.bin")).SequenceEqual(f.Server.Bytes), "Nested bytes or path differ.");
            Check(new FileInfo(Path.Combine(f.DirectoryPath, "Documents", "zero.txt")).Length == 0, "Empty file missing.");
        });
        await Test("overlapping selections download the ancestor tree once", async (f, source) =>
        {
            source.Folder("folder", "Folder");
            source.Folder("sub", "Sub", "folder");
            source.File("file", "child.bin", "sub");
            var batch = Batch(f, source, "file", "sub", "folder", "file");
            await batch.StartAsync();
            Complete(batch, 1, 2);
            Check(Directory.GetFiles(f.DirectoryPath, "child.bin", SearchOption.AllDirectories).Length == 1 &&
                !Directory.Exists(Path.Combine(f.DirectoryPath, "Sub")), "Selection overlap duplicated a subtree.");
        });
        await Test("failed later pages retry their continuation without repeating completed files", async (f, source) =>
        {
            source.Folder("folder", "Folder");
            source.File("a", "a.bin", "folder");
            source.File("b", "b.bin", "folder");
            bool fail = true;
            source.List = (id, link, _) => link == null ? Task.FromResult(new DownloadTreePage([source.Items["a"]], "page2"))
                : fail ? throw new DownloadFailureException(DownloadFailure.AccessDenied) : Task.FromResult(new DownloadTreePage([source.Items["b"]], null));
            var batch = Batch(f, source, "folder");
            await batch.StartAsync();
            Check(batch.Snapshot.State == DownloadTaskState.Failed && batch.Snapshot.Batch.CompletedFiles == 1 &&
                !batch.Snapshot.Batch.DiscoveryComplete && batch.Snapshot.Batch.Failures.Single().Path == "Folder", "Partial traversal looked complete.");
            int aCalls = source.ResolveCounts["a"];
            fail = false;
            await batch.StartAsync();
            Complete(batch, 2, 1);
            Check(source.ListCalls.Count(call => call.Link == null) == 1 && source.ResolveCounts["a"] == aCalls, "Retry rescanned or downloaded completed work.");
        });
        await Test("file failures are listed and only failed files retry", async (f, source) =>
        {
            source.File("good", "good.bin");
            source.File("bad", "bad.bin");
            bool fail = true;
            source.BeforeResolve = (id, _) => id == "bad" && fail ? throw new DownloadFailureException(DownloadFailure.AccessDenied) : Task.CompletedTask;
            var batch = Batch(f, source, "good", "bad");
            await batch.StartAsync();
            Check(batch.Snapshot.State == DownloadTaskState.Failed && batch.Snapshot.Batch.Failures.Single().Path == "bad.bin", "Failure details missing.");
            int goodCalls = source.ResolveCounts["good"];
            fail = false;
            await batch.StartAsync();
            Complete(batch, 2, 0);
            Check(source.ResolveCounts["good"] == goodCalls, "Successful file was downloaded again.");
        });
        await Test("existing files and folders are preserved with stable keep-both names", async (f, source) =>
        {
            Directory.CreateDirectory(Path.Combine(f.DirectoryPath, "Folder"));
            await File.WriteAllTextAsync(Path.Combine(f.DirectoryPath, "same.txt"), "original");
            source.File("file", "same.txt");
            source.Folder("folder", "Folder");
            source.File("child", "child.bin", "folder");
            var batch = Batch(f, source, "file", "folder");
            await batch.StartAsync();
            Complete(batch, 2, 1);
            Check(File.ReadAllText(Path.Combine(f.DirectoryPath, "same.txt")) == "original" &&
                File.Exists(Path.Combine(f.DirectoryPath, "same (2).txt")) &&
                File.Exists(Path.Combine(f.DirectoryPath, "Folder (2)", "child.bin")), "Existing content was overwritten or directory merged.");
        });
        await Test("unsafe, reserved and case-colliding cloud names remain distinct and contained", async (f, source) =>
        {
            string[] names = ["../escape.txt", "CON.txt", "a:b.txt", "A_B.txt", "trailing. ", new string('长', 220) + ".txt"];
            for (int i = 0; i < names.Length; i++) source.File(i.ToString(), names[i], size: 0);
            var batch = Batch(f, source, Enumerable.Range(0, names.Length).Select(i => i.ToString()).ToArray());
            await batch.StartAsync();
            Complete(batch, names.Length, 0);
            var files = Directory.GetFiles(f.DirectoryPath);
            Check(files.Length == names.Length && files.All(path => Path.GetFileName(path).Length <= 180) &&
                files.Any(path => Path.GetFileName(path) == "_CON.txt") && files.Any(path => Path.GetFileName(path) == "A_B (2).txt"), "Unsafe names collided or escaped.");
        });
        await Test("a file appearing after planning cannot be overwritten, including on retry", async (f, source) =>
        {
            source.File("race", "race.bin", size: 0);
            string path = Path.Combine(f.DirectoryPath, "race.bin");
            source.BeforeResolve = async (_, _) => { if (!File.Exists(path)) await File.WriteAllTextAsync(path, "external content"); };
            var batch = Batch(f, source, "race");
            await batch.StartAsync();
            await batch.StartAsync();
            Check(batch.Snapshot.State == DownloadTaskState.Failed && File.ReadAllText(path) == "external content", "Concurrent destination was overwritten.");
            await batch.CancelAsync();
            Check(!Directory.GetFiles(f.DirectoryPath, ".cloudflow-*").Any(), "Staging survived cancellation.");
        });
        await Test("concurrent batches reserve different destinations", async (f, source) =>
        {
            source.File("same", "same.bin");
            var first = Batch(f, source, "same");
            var second = Batch(f, source, "same");
            await Task.WhenAll(first.StartAsync(), second.StartAsync());
            Complete(first, 1, 0);
            Complete(second, 1, 0);
            Check(File.Exists(Path.Combine(f.DirectoryPath, "same.bin")) && File.Exists(Path.Combine(f.DirectoryPath, "same (2).bin")), "Concurrent batches used one destination.");
        });
        await Test("a global three-file bound applies across batches", async (f, source) =>
        {
            for (int i = 0; i < 8; i++) source.File(i.ToString(), i + ".bin", size: 0);
            int active = 0, peak = 0;
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            source.BeforeResolve = async (_, token) =>
            {
                int count = Interlocked.Increment(ref active);
                int old;
                do { old = peak; } while (count > old && Interlocked.CompareExchange(ref peak, count, old) != old);
                try { await gate.Task.WaitAsync(token); }
                finally { Interlocked.Decrement(ref active); }
            };
            var first = Batch(f, source, "0", "1", "2", "3");
            var second = Batch(f, source, "4", "5", "6", "7");
            Task all = Task.WhenAll(first.StartAsync(), second.StartAsync());
            await Until(() => Volatile.Read(ref active) == 3);
            await Task.Delay(100);
            Check(peak == 3, "Concurrency bound exceeded.");
            gate.SetResult();
            await all;
            Complete(first, 4, 0);
            Complete(second, 4, 0);
        });
        await Test("pausing directory discovery resumes from its pending page", async (f, source) =>
        {
            source.Folder("folder", "Folder");
            source.File("a", "a.bin", "folder");
            source.File("b", "b.bin", "folder");
            bool wait = true;
            var entered = new TaskCompletionSource();
            source.List = async (_, link, token) =>
            {
                if (link == null) return new DownloadTreePage([source.Items["a"]], "page2");
                entered.TrySetResult();
                if (wait) await Task.Delay(Timeout.Infinite, token);
                return new DownloadTreePage([source.Items["b"]], null);
            };
            var batch = Batch(f, source, "folder");
            Task running = batch.StartAsync();
            await entered.Task;
            await batch.PauseAsync();
            await running;
            Check(batch.Snapshot.State == DownloadTaskState.Paused && source.ResolveCounts.Count == 0, "Discovery pause started transfers.");
            wait = false;
            await batch.StartAsync();
            Complete(batch, 2, 1);
            Check(source.ListCalls.Count(call => call.Link == null) == 1, "Resume repeated the first page.");
        });
        await Test("pausing file writes drains them and resumes retained byte ranges", async (f, source) =>
        {
            source.File("large", "large.bin");
            var batch = Batch(f, source, "large");
            Task running = batch.StartAsync();
            await Until(() => batch.Snapshot.ReceivedBytes >= 32768);
            await batch.PauseAsync();
            await running;
            Check(batch.Snapshot.State == DownloadTaskState.Paused, "Batch pause did not settle.");
            long bytes = batch.Snapshot.ReceivedBytes;
            await Task.Delay(50);
            Check(bytes == batch.Snapshot.ReceivedBytes, "Writer continued after pause.");
            await batch.StartAsync();
            Complete(batch, 1, 0);
            Check(f.Server.Offsets.Any(offset => offset > 0), "Batch retry discarded partial bytes.");
        });
        await Test("cancelling active and queued files drains writers and keeps completed files", async (f, source) =>
        {
            source.File("done", "done.bin", size: 0);
            for (int i = 0; i < 8; i++) source.File(i.ToString(), i + ".bin");
            var batch = Batch(f, source, new[] { "done" }.Concat(Enumerable.Range(0, 8).Select(i => i.ToString())).ToArray());
            Task running = batch.StartAsync();
            await Until(() => batch.Snapshot.Batch.CompletedFiles > 0 && batch.Snapshot.ReceivedBytes >= 32768);
            await batch.CancelAsync();
            await running;
            Check(batch.Snapshot.State == DownloadTaskState.Cancelled && File.Exists(Path.Combine(f.DirectoryPath, "done.bin")), "Cancel removed completed content.");
            Check(!Directory.GetFiles(f.DirectoryPath, ".cloudflow-*", SearchOption.AllDirectories).Any(), "Cancel left partial writers/files.");
            int calls = source.ResolveCounts.Values.Sum();
            await batch.StartAsync();
            await Task.Delay(50);
            Check(calls == source.ResolveCounts.Values.Sum(), "Cancelled batch restarted or scheduled more files.");
        });
        await Test("cancelling directory discovery stops new work", async (f, source) =>
        {
            source.Folder("folder", "Folder");
            var entered = new TaskCompletionSource();
            source.List = async (_, _, token) => { entered.SetResult(); await Task.Delay(Timeout.Infinite, token); return null; };
            var batch = Batch(f, source, "folder");
            Task running = batch.StartAsync();
            await entered.Task;
            await batch.CancelAsync();
            await running;
            Check(batch.Snapshot.State == DownloadTaskState.Cancelled && source.ResolveCounts.Count == 0, "Cancelled scan transferred data.");
        });
        await Test("duplicate pages, empty intermediate pages and cycles cannot lose or duplicate files", async (f, source) =>
        {
            source.Folder("folder", "Folder");
            source.File("a", "a.bin", "folder", 0);
            source.List = (_, link, _) => Task.FromResult(link switch
            {
                null => new DownloadTreePage([source.Items["a"]], "empty"),
                "empty" => new DownloadTreePage([], "last"),
                _ => new DownloadTreePage([source.Items["a"]], null)
            });
            var batch = Batch(f, source, "folder");
            await batch.StartAsync();
            Complete(batch, 1, 1);
            source.List = (_, _, _) => Task.FromResult(new DownloadTreePage([], "cycle"));
            var cyclic = Batch(f, source, "folder");
            await cyclic.StartAsync();
            Check(cyclic.Snapshot.State == DownloadTaskState.Failed && !cyclic.Snapshot.Batch.DiscoveryComplete, "Cyclic continuation completed.");
            await cyclic.CancelAsync();
        });
        await Test("ancestor cycles and malformed folder children remain failures", async (f, source) =>
        {
            source.Folder("a", "A", "b");
            source.Folder("b", "B", "a");
            var batch = Batch(f, source, "a", "b");
            await batch.StartAsync();
            Check(batch.Snapshot.State == DownloadTaskState.Failed && batch.Snapshot.Batch.Failures.Count == 2, "Overlapping cycle looked empty/successful.");
            await batch.CancelAsync();
            source.Items["a"] = source.Items["a"] with { ParentId = "root" };
            source.List = (_, _, _) => Task.FromResult(new DownloadTreePage([source.Items["a"]], null));
            batch = Batch(f, source, "a");
            await batch.StartAsync();
            Check(batch.Snapshot.State == DownloadTaskState.Failed, "Self child was accepted.");
            await batch.CancelAsync();
        });
        await Test("batch task UI state, cancellation and shutdown use the shared task manager", async (f, source) =>
        {
            source.File("large", "large.bin");
            services.Manager = new TaskManagerViewModel();
            var manager = services.Manager;
            var task = manager.QueueDownloadBatch(source, ["large"], f.DirectoryPath, "Batch");
            Task running = task.StartDownload();
            await Until(() => { DispatcherQueue.Drain(); return task.DownloadedBytes >= 32768; });
            Check(task.IsBatch && task.CanCancelBatch && !task.ShowRemove && task.BatchSummary.Length > 0, "Missing batch controls/progress.");
            bool finished = false;
            Check(await manager.RequestCloseAsync((_, downloads) =>
            {
                Check(downloads == 1 && manager.QueueDownloadBatch(source, ["large"], f.DirectoryPath, "blocked") == null, "Shutdown did not freeze batch admission.");
                return Task.FromResult(true);
            }, () => { finished = true; return Task.CompletedTask; }), "Shutdown was not accepted.");
            await running;
            Check(finished && !task.NeedsExitProtection && !Directory.GetFiles(f.DirectoryPath, ".cloudflow-*").Any(), "Shutdown returned before cleanup.");
        });
        await Test("batch localization and explicit cancel retain a cancelled card", async (f, source) =>
        {
            source.File("file", "file.bin", size: 0);
            services.Manager = new TaskManagerViewModel();
            var task = services.Manager.QueueDownloadBatch(source, ["file"], f.DirectoryPath, "Batch");
            await task.CancelDownload();
            Check(task.State == DownloadTaskState.Cancelled && task.ShowRemove && !task.CanRetry && services.Manager.DownloadTasks.Contains(task), "Cancel removed the status card or permits restart.");
            await task.CancelTaskAsync();
            foreach (string language in new[] { "en-US", "zh-CN" })
            {
                ResourceHelper.Language = language;
                foreach (string key in new[] { "DownloadBatch_Title", "DownloadBatch_Queued", "DownloadBatch_Scanning", "DownloadBatch_Summary", "DownloadBatch_IncompleteScan", "DownloadBatch_KeepBoth", "DownloadError_UnsupportedItem" })
                    Check(!string.IsNullOrWhiteSpace(key.GetLocalized()), "Missing batch resource.");
            }
            ResourceHelper.Language = "en-US";
        });
    }

    private static DownloadBatchSession Batch(Fixture f, TreeSource source, params string[] ids) => new(source, ids, f.DirectoryPath,
        new DownloadSessionOptions { MaxAttempts = 1, BlockTimeoutMilliseconds = 1000, RetryDelay = TimeSpan.FromMilliseconds(10) });

    private static void Complete(DownloadBatchSession batch, int files, int folders) => Check(batch.Snapshot.State == DownloadTaskState.Completed &&
        batch.Snapshot.Batch.CompletedFiles == files && batch.Snapshot.Batch.Files == files && batch.Snapshot.Batch.Folders == folders &&
        batch.Snapshot.Batch.DiscoveryComplete && batch.Snapshot.Batch.Failures.Count == 0, "Wrong completion: " + batch.Snapshot);

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

    private static async Task Until(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!predicate()) await Task.Delay(10, timeout.Token);
    }

    private static async Task Test(string name, Func<Fixture, TreeSource, Task> action)
    {
        await using var fixture = new Fixture();
        await action(fixture, new TreeSource(fixture)).WaitAsync(TimeSpan.FromSeconds(40));
        Console.WriteLine("PASS batch: " + name);
    }

    private sealed class TreeSource(Fixture fixture) : IDownloadTreeSource
    {
        public Dictionary<string, DownloadTreeItem> Items { get; } = new()
        {
            ["root"] = new("root", "Root", true, 0, IsRoot: true)
        };
        public System.Collections.Concurrent.ConcurrentDictionary<string, int> ResolveCounts { get; } = new();
        public List<(string Id, string Link)> ListCalls { get; } = new();
        public Func<string, string, CancellationToken, Task<DownloadTreePage>> List;
        public Func<string, CancellationToken, Task> BeforeResolve;
        public void File(string id, string name, string parent = "root", long? size = null) => Items[id] = new(id, name, false, size ?? fixture.Server.Bytes.Length, parent);
        public void Folder(string id, string name, string parent = "root") => Items[id] = new(id, name, true, 0, parent);
        public Task<DownloadTreeItem> GetDownloadItemAsync(string id, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(Items[id]);
        }
        public Task<DownloadTreePage> GetDownloadChildrenAsync(string id, string link, CancellationToken token)
        {
            lock (ListCalls) ListCalls.Add((id, link));
            return List != null ? List(id, link, token) : Task.FromResult(new DownloadTreePage(Items.Values.Where(item => item.ParentId == id).ToArray(), null));
        }
        public async Task<DownloadSource> GetDownloadSourceAsync(string id, CancellationToken token)
        {
            ResolveCounts.AddOrUpdate(id, 1, (_, count) => count + 1);
            if (BeforeResolve != null) await BeforeResolve(id, token);
            return new DownloadSource(fixture.Server.Url, Items[id].Size, "v1");
        }
    }
}
