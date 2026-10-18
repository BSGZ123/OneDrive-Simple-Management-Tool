using OneDrive_Simple_Management_Tool.Models;
using OneDrive_Simple_Management_Tool.Services;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

if (args.FirstOrDefault() == "restore")
{
    var records = new TransferStore(args[1]).Load();
    Check(records.Count == 2 && records.All(x => x.Priority == 2 && x.SpeedLimitKiB == 128), "Restart lost task settings or journal deltas.");
    return;
}

await Scheduling();
await Limits();
Estimates();
string directory = Path.Combine(Path.GetTempPath(), "cloudflow-controls-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    await Persistence(directory);
    await Downloads(directory);
    await Uploads();
}
finally { Directory.Delete(directory, true); }
Console.WriteLine("All transfer control regression checks passed.");

static void Check(bool value, string message) { if (!value) throw new Exception(message); }

static async Task Scheduling()
{
    long clock = 0;
    var scheduler = new TransferScheduler(1, () => clock);
    var held = await scheduler.EnterAsync(default);
    var lowPolicy = new TransferPolicy { Priority = 0 };
    var low = scheduler.EnterAsync(default, lowPolicy);
    var normal = scheduler.EnterAsync(default);
    var high = scheduler.EnterAsync(default, new() { Priority = 2 });
    using var cancelled = new CancellationTokenSource();
    var removed = scheduler.EnterAsync(cancelled.Token, new() { Priority = 2 });
    cancelled.Cancel();
    try { await removed; throw new Exception("Queued cancellation was ignored."); } catch (OperationCanceledException) { }
    Check(!high.IsCompleted && !normal.IsCompleted, "Priority preempted an active file.");
    held.Dispose();
    var first = await high.WaitAsync(TimeSpan.FromSeconds(2));
    Check(!normal.IsCompleted && !low.IsCompleted, "Highest priority was not admitted alone.");
    lowPolicy.Priority = 2;
    first.Dispose();
    (await low.WaitAsync(TimeSpan.FromSeconds(2))).Dispose();
    (await normal.WaitAsync(TimeSpan.FromSeconds(2))).Dispose();
    var blocker = await scheduler.EnterAsync(default);
    var old = scheduler.EnterAsync(default, new() { Priority = 0 });
    clock = 61000;
    var fresh = scheduler.EnterAsync(default, new() { Priority = 2 });
    blocker.Dispose();
    var aged = await old.WaitAsync(TimeSpan.FromSeconds(2));
    Check(!fresh.IsCompleted, "An older low-priority task starved.");
    aged.Dispose();
    (await fresh).Dispose();
    var both = new TransferScheduler(2);
    var a = await both.EnterAsync(default);
    var b = await both.EnterAsync(default);
    var c = both.EnterAsync(default);
    Check(!c.IsCompleted, "Concurrency bound was exceeded.");
    a.Dispose(); a.Dispose();
    using var third = await c;
    var d = both.EnterAsync(default);
    Check(!d.IsCompleted, "Double disposal leaked a slot.");
    b.Dispose(); (await d).Dispose();
    Console.WriteLine("PASS priority order, live changes, FIFO aging, cancellation and shared concurrency");
}

static async Task Limits()
{
    var policy = new TransferPolicy { BytesPerSecond = 128 * 1024 };
    var watch = Stopwatch.StartNew();
    await Task.WhenAll(Enumerable.Range(0, 3).Select(async _ =>
    {
        for (int i = 0; i < 8; i++) await policy.WaitAsync(4096, default);
    }));
    Check(watch.Elapsed.TotalSeconds >= 0.62 && watch.Elapsed.TotalSeconds < 5, "Concurrent files each received a separate budget.");
    policy.BytesPerSecond = 1024;
    using var stop = new CancellationTokenSource(80);
    try { await policy.WaitAsync(16384, stop.Token); throw new Exception("Throttle ignored cancellation."); } catch (OperationCanceledException) { }
    var waiting = policy.WaitAsync(16384, default);
    await Task.Delay(80);
    policy.BytesPerSecond = 0;
    await waiting.WaitAsync(TimeSpan.FromSeconds(1));
    policy.BytesPerSecond = 1;
    waiting = policy.WaitAsync(1000, default);
    await Task.Delay(80);
    policy.BytesPerSecond = 1024 * 1024;
    await waiting.WaitAsync(TimeSpan.FromSeconds(1));
    Console.WriteLine("PASS aggregate rate budget, cancellation and live limit changes/unlimited");
}

static void Estimates()
{
    var estimator = new TransferProgressEstimator();
    Check(estimator.Sample(0, 0, 10000, true).Seconds == null, "ETA appeared without samples.");
    var result = estimator.Sample(1, 1000, 9000, true);
    Check(result.Speed == 1000 && result.Seconds == 9, "ETA used restored bytes instead of transferred bytes.");
    Check(estimator.Sample(2, 2000, 8000, false).Seconds == null, "Incomplete discovery produced an ETA.");
    for (int i = 3; i <= 8; i++) result = estimator.Sample(i, 2000, 8000, true);
    Check(result.Speed == 0 && result.Seconds == null, "A stalled transfer retained a stale speed/ETA.");
    estimator.Reset();
    Check(estimator.Sample(100, 2000, 8000, true).Seconds == null, "Pause/resume retained an old rate.");
    Check(estimator.Sample(101, 3000, 0, true).Seconds == null, "Final server confirmation was presented as completed.");
    Console.WriteLine("PASS rolling ETA, unknown size, stalled transfers and resumed baselines");
}

static async Task Persistence(string directory)
{
    string path = Path.Combine(directory, "records");
    var store = new TransferStore(path);
    foreach (var direction in Enum.GetValues<TransferDirection>())
    {
        var record = new TransferRecord { AccountId = "local-test", DriveId = "drive", Name = "data", Path = directory, Direction = direction };
        store.Save(record);
        record.Priority = 2; record.SpeedLimitKiB = 128;
        store.Save(record);
    }
    var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
    start.ArgumentList.Add("restore"); start.ArgumentList.Add(path);
    using var child = Process.Start(start)!;
    await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
    Check(child.ExitCode == 0, "Settings did not survive a fresh process.");
    string legacy = Path.Combine(directory, "legacy");
    Directory.CreateDirectory(legacy);
    var original = new TransferRecord { AccountId = "legacy", DriveId = "drive", Name = "old", Path = directory };
    var json = JsonSerializer.SerializeToNode(original)!.AsObject();
    json.Remove("Priority"); json.Remove("SpeedLimitKiB");
    File.WriteAllBytes(Path.Combine(legacy, original.Id.ToString("N") + ".dat"),
        new WindowsConfigurationProtector().Protect(JsonSerializer.SerializeToUtf8Bytes(json), "OneDriveSimpleManagementTool/transfers/v1"));
    var restored = new TransferStore(legacy).Load().Single();
    Check(restored.Priority == 1 && restored.SpeedLimitKiB == 0, "Old records did not default to normal/unlimited.");
    restored.SpeedLimitKiB = -1;
    try { new TransferStore(Path.Combine(directory, "invalid")).Save(restored); throw new Exception("Invalid limit was accepted."); }
    catch (InvalidDataException) { }
    Console.WriteLine("PASS upload/download settings survive process restart; old records and invalid limits handled");
}

static async Task Downloads(string directory)
{
    await using var server = new LoopbackServer { Bytes = new byte[128 * 1024], DelayMilliseconds = 0 };
    var policy = new TransferPolicy { BytesPerSecond = 128 * 1024 };
    DownloadSession Create(string name, bool durable)
    {
        var session = new DownloadSession(Path.Combine(directory, name), _ => Task.FromResult(new DownloadSource(server.Url, server.Bytes.Length, "v1")),
            new() { Policy = policy, BlockTimeoutMilliseconds = 150, MaxAttempts = 1 });
        if (durable) session.AttachResume(null, _ => { });
        return session;
    }
    var first = Create("durable.bin", true);
    var second = Create("runtime.bin", false);
    var timer = Stopwatch.StartNew();
    await Task.WhenAll(first.StartAsync(), second.StartAsync()).WaitAsync(TimeSpan.FromSeconds(15));
    Check(first.Snapshot.State == DownloadTaskState.Completed && second.Snapshot.State == DownloadTaskState.Completed,
        "Throttling was mistaken for a network timeout.");
    Check(timer.Elapsed.TotalSeconds >= 1.75 && policy.TransferredBytes == 2 * server.Bytes.Length, "Downloads bypassed the shared limit.");
    Check(File.ReadAllBytes(first.DestinationPath).SequenceEqual(server.Bytes) && File.ReadAllBytes(second.DestinationPath).SequenceEqual(server.Bytes), "Rate limiting corrupted downloads.");
    policy.BytesPerSecond = 1024;
    var paused = Create("paused.bin", true);
    var running = paused.StartAsync();
    await Task.Delay(250);
    await paused.PauseAsync().WaitAsync(TimeSpan.FromSeconds(2));
    await running;
    Check(paused.Snapshot.State == DownloadTaskState.Paused, "Pause did not drain a throttled download.");
    policy.BytesPerSecond = 0;
    await paused.StartAsync().WaitAsync(TimeSpan.FromSeconds(5));
    Check(paused.Snapshot.State == DownloadTaskState.Completed, "Throttled download could not resume.");
    Console.WriteLine("PASS durable/runtime downloads share a byte limit, preserve data and pause/resume promptly");
}

static async Task Uploads()
{
    await using var server = new UploadServer();
    using var http = new HttpClient();
    Task<UploadSessionInfo> Create(string _, CancellationToken token) => http.GetFromJsonAsync<UploadSessionInfo>(server.Url + "create", token)!;
    var policy = new TransferPolicy { BytesPerSecond = 64 * 1024 };
    var entry = new UploadEntryRecord { Name = "data.bin" };
    byte[] bytes = Enumerable.Range(0, 128 * 1024).Select(x => (byte)(x % 251)).ToArray();
    bool inFlight = false;
    var progress = new InlineProgress(value =>
    {
        if (value > 0 && value < bytes.Length && entry.Resume?.Bytes == 0) inFlight = true;
    });
    var watch = Stopwatch.StartNew();
    using var stream = new MemoryStream(bytes);
    var outcome = await new ResumableUploadSession(entry, () => { }, Create, policy).RunAsync(stream, "fail", progress, default);
    Check(outcome.Uploaded == 1 && server.CompletedBytes.SequenceEqual(bytes), "Throttled upload corrupted content.");
    Check(watch.Elapsed.TotalSeconds >= 1.75 && policy.TransferredBytes == bytes.Length, "Upload bypassed its byte limit.");
    Check(inFlight && !server.SawAuthorization && entry.Resume == null, "Live bytes changed durable offsets or leaked authorization.");
    Console.WriteLine("PASS upload streams are throttled with live progress and separate acknowledged checkpoints");

    // This slice intentionally takes longer than the old whole-request 60-second timeout.
    policy.BytesPerSecond = 1024;
    entry = new() { Name = "slow.bin" };
    using var slow = new MemoryStream(new byte[65 * 1024]);
    watch.Restart();
    outcome = await new ResumableUploadSession(entry, () => { }, Create, policy).RunAsync(slow, "fail", null, default)
        .WaitAsync(TimeSpan.FromSeconds(100));
    Check(outcome.Uploaded == 1 && watch.Elapsed.TotalSeconds > 60, "Low-speed uploads still hit a whole-request timeout.");
    Console.WriteLine("PASS a rate-limited upload exceeding 60 seconds completes without a false timeout");
}

sealed class InlineProgress(Action<long> report) : IProgress<long>
{
    public void Report(long value) => report(value);
}
