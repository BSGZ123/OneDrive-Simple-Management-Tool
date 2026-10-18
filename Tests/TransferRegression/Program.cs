using OneDrive_Simple_Management_Tool.Models;
using OneDrive_Simple_Management_Tool.Services;
using System.Collections.Concurrent;
using System.Diagnostics;

// A second OS process reads the same DPAPI-protected records, with no runtime state from its parent.
if (args.Length > 0 && args[0] == "restore")
{
    var reopened = new TransferStore(args[1]);
    var task = reopened.Load().Single();
    var source = new UploadSource();
    var session = new UploadRecordSession(task, source, () => reopened.Save(task), () => { }, _ => "error");
    await session.RunAsync(null, default, "file-0");
    Check(source.Uploaded.Keys.SequenceEqual(new[] { "file-0" }), "Individual retry touched another item.");
    Check(task.Uploads.Count(x => x.State == TransferEntryState.Failed) == 9, "Individual retry changed other failures.");
    await session.RunAsync(null, default);
    Check(source.Uploaded.Count == 10 && source.Uploaded.Values.All(x => x == 1), "Restart reuploaded completed files.");
    Check(source.Folders == 0 && source.Scans == 0, "Restart recreated or rescanned the completed directory.");
    Check(task.Uploads.All(x => x.State == TransferEntryState.Completed), "Remaining files were not completed.");
    return;
}

string directory = Path.Combine(Path.GetTempPath(), "cloudflow-transfer-checks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    var store = new TransferStore(directory);
    var record = NewRecord("account-a");
    var source = new UploadSource { Reject = key => int.Parse(key.Split('-')[1]) % 10 == 0 };
    var upload = new UploadRecordSession(record, source, () => store.Save(record), () => { }, _ => "FileOperation_Failed");
    await upload.RunAsync(null, default);
    Check(record.Uploads.Count(x => !x.IsFolder && x.State == TransferEntryState.Completed) == 90, "A failed file stopped its siblings.");
    Check(record.Uploads.Count(x => x.State == TransferEntryState.Failed) == 10, "Failures were not individually recorded.");
    Check(source.Folders == 1, "Wrong directory count.");
    Check(!System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(Directory.GetFiles(directory, "*.dat").Single())).Contains("account-a"), "Account identity was stored as plaintext.");
    var child = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
    child.ArgumentList.Add("restore"); child.ArgumentList.Add(directory);
    using (var process = Process.Start(child)!)
    {
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Check(process.ExitCode == 0, "Cross-process restoration failed.");
    }
    Console.WriteLine("PASS 100-file upload: 90 successes retained across process restart, single-item and remaining-failure retry");

    var other = NewRecord("account-b");
    store.Save(other);
    Check(new TransferStore(directory).Load().Select(x => x.AccountId).Distinct().Count() == 2, "Accounts were merged.");
    store.Remove(record.Id);
    Check(store.Load().Single().Id == other.Id, "Removing one task deleted another account's task.");
    Console.WriteLine("PASS task identity, account isolation and durable removal");

    string file = Path.Combine(directory, other.Id.ToString("N") + ".dat");
    byte[] original = File.ReadAllBytes(file);
    using (var held = new FileStream(file + ".journal", FileMode.Create, FileAccess.Write, FileShare.None))
    {
        other.Name = "changed";
        try { store.Save(other); throw new Exception("Save unexpectedly succeeded."); }
        catch (IOException) { }
    }
    Check(File.ReadAllBytes(file).SequenceEqual(original), "Failed atomic write destroyed the previous checkpoint.");
    Check(new TransferStore(directory).Load().Single().Name == "source", "Failed journal write changed the checkpoint.");
    store.Save(other);
    using (var torn = new FileStream(file + ".journal", FileMode.Append, FileAccess.Write)) torn.Write(new byte[] { 20, 0, 0, 0, 1, 2 });
    Check(new TransferStore(directory).Load().Single().Name == "changed", "Torn final journal frame hid committed records.");
    other.Name = "after-torn-write";
    store.Save(other);
    Check(new TransferStore(directory).Load().Single().Name == "after-torn-write", "Could not append after a torn write.");
    other.Version = 99;
    try { store.Save(other); throw new Exception("Unsupported version saved."); } catch (InvalidDataException) { }
    File.WriteAllBytes(file, [1, 2, 3]);
    try { store.Load(); throw new Exception("Corrupted checkpoint silently reset."); } catch (System.Security.Cryptography.CryptographicException) { }
    Check(File.ReadAllBytes(file).SequenceEqual(new byte[] { 1, 2, 3 }), "Corrupt data was overwritten during loading.");
    Console.WriteLine("PASS failed writes preserve previous records; corrupt and future-version records are preserved");

    var interrupted = NewRecord("account-c");
    interrupted.Uploads.Add(new() { Key = ".", Name = "single", State = TransferEntryState.Working });
    var resumed = new UploadRecordSession(interrupted, new UploadSource(), () => { }, () => { }, _ => "error");
    Check(interrupted.Uploads.Single().State == TransferEntryState.Pending, "In-flight file wasn't reset for full-file retry.");
    Console.WriteLine("PASS legacy file-level records without byte checkpoints remain resumable as whole files");

    var confirmed = NewRecord("account-confirmed");
    confirmed.IsFolder = false;
    var confirmationSource = new ConfirmThenFail();
    var confirmationSession = new UploadRecordSession(confirmed, confirmationSource, () => { }, () => { }, _ => "error");
    try { await confirmationSession.RunAsync(null, default); throw new Exception("Checkpoint error was hidden."); }
    catch (IOException) { }
    Check(confirmed.Uploads.Single().State == TransferEntryState.Completed, "Checkpoint failure erased confirmed completion.");
    await confirmationSession.RunAsync(null, default);
    Check(confirmationSource.Calls == 1, "Retry duplicated a confirmed upload after a checkpoint failure.");
    Console.WriteLine("PASS confirmed uploads survive checkpoint failure without being uploaded twice");

    var scheduler = new TransferScheduler(3);
    var first = await scheduler.EnterAsync(default);
    var second = await scheduler.EnterAsync(default);
    var third = await scheduler.EnterAsync(default);
    using var stop = new CancellationTokenSource();
    var cancelled = scheduler.EnterAsync(stop.Token);
    var waiting = scheduler.EnterAsync(default);
    Check(!waiting.IsCompleted, "Fourth transfer bypassed the limit.");
    stop.Cancel();
    try { await cancelled; throw new Exception("Queued cancellation was lost."); } catch (OperationCanceledException) { }
    first.Dispose(); first.Dispose();
    using var admitted = await waiting.WaitAsync(TimeSpan.FromSeconds(2));
    var stillWaiting = scheduler.EnterAsync(default);
    Check(!stillWaiting.IsCompleted, "Cancelled waiter or double-disposal leaked a slot.");
    second.Dispose();
    using var admittedNext = await stillWaiting;
    third.Dispose();
    Console.WriteLine("PASS shared concurrency limit, cancellation and idempotent slot release");
}
finally { Directory.Delete(directory, true); }

static TransferRecord NewRecord(string account) => new()
{
    AccountId = account, DriveId = "drive", Name = "source", ItemId = "remote-parent", Path = Path.GetTempPath(),
    Direction = TransferDirection.Upload, IsFolder = true
};
static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

sealed class UploadSource : IUploadRecordSource
{
    public Func<string, bool> Reject = _ => false;
    public int Folders;
    public int Scans;
    public ConcurrentDictionary<string, int> Uploaded = new();
    public Task<IReadOnlyList<UploadSourceItem>> GetChildrenAsync(string key, CancellationToken token)
    {
        Scans++;
        return Task.FromResult<IReadOnlyList<UploadSourceItem>>(Enumerable.Range(0, 100).Select(i => new UploadSourceItem("file-" + i, "file-" + i, false, 123)).ToArray());
    }
    public Task<UploadOutcome> CreateFolderAsync(UploadEntryRecord item, string parentId, CancellationToken token)
    {
        Folders++;
        return Task.FromResult(new UploadOutcome(1, 0, "created-folder", "source (2)"));
    }
    public async Task<UploadOutcome> UploadAsync(UploadEntryRecord item, string parentId, IProgress<long> progress, CancellationToken token)
    {
        if (parentId != "created-folder") throw new Exception("Lost the persisted remote folder mapping.");
        await Task.Yield();
        Uploaded.AddOrUpdate(item.Key, 1, (_, count) => count + 1);
        if (Reject(item.Key)) throw new IOException();
        return new(1, 0, "remote-" + item.Key, item.Name);
    }
}

sealed class ConfirmThenFail : IUploadRecordSource
{
    public int Calls;
    public Task<IReadOnlyList<UploadSourceItem>> GetChildrenAsync(string key, CancellationToken token) => throw new NotSupportedException();
    public Task<UploadOutcome> CreateFolderAsync(UploadEntryRecord item, string parentId, CancellationToken token) => throw new NotSupportedException();
    public Task<UploadOutcome> UploadAsync(UploadEntryRecord item, string parentId, IProgress<long> progress, CancellationToken token)
    {
        Calls++;
        item.TargetId = "confirmed-item";
        item.State = TransferEntryState.Completed;
        throw new IOException("Local checkpoint failed after server confirmation.");
    }
}
