using System.Security.Cryptography;
using OneDrive_Simple_Management_Tool.Models;
using OneDrive_Simple_Management_Tool.Services;
using OneDrive_Simple_Management_Tool.ViewModels;
using OneDrive_Simple_Management_Tool.Helpers;
using Microsoft.UI.Dispatching;
using CommunityToolkit.Mvvm.DependencyInjection;

var services = new TestServices();
Ioc.Default.ConfigureServices(services);

await Test("ordinary, empty, one-byte and unknown-length downloads", async fixture =>
{
    foreach (int size in new[] { 0, 1, 16387, 131072 })
    {
        fixture.Server.Bytes = Enumerable.Range(0, size).Select(i => (byte)(i * 13 + 7)).ToArray();
        fixture.Server.OmitLength = size == 131072;
        var session = fixture.Create(size.ToString());
        await session.StartAsync();
        fixture.AssertCompleted(session);
        await session.CancelAsync();
        Check(File.Exists(session.DestinationPath), "Removing a completed task deleted the file.");
    }
});

await Test("pause drains writer, refreshes URL and resumes with Range", async fixture =>
{
    var session = fixture.Create();
    var running = session.StartAsync();
    Check(ReferenceEquals(running, session.StartAsync()), "Duplicate start created another operation.");
    await Until(() => session.Snapshot.ReceivedBytes >= 32768);
    await session.PauseAsync();
    await running;
    Check(session.Snapshot.State == DownloadTaskState.Paused, "Pause did not settle.");
    long paused = session.Snapshot.ReceivedBytes;
    await Task.Delay(50);
    Check(session.Snapshot.ReceivedBytes == paused, "Progress changed after pause completed.");
    fixture.UrlSuffix = "?fresh-link=1";
    await session.StartAsync();
    fixture.AssertCompleted(session);
    Check(fixture.Server.Offsets.Any(offset => offset > 0), "Resume did not use Range.");
    Check(fixture.Server.Paths.Any(path => path.Contains("fresh-link")), "Resume reused the old URL.");
});

await Test("partial failure retains progress for manual retry", async fixture =>
{
    fixture.Server.DropsRemaining = 1;
    var session = fixture.Create(maxAttempts: 1);
    await session.StartAsync();
    Check(session.Snapshot.State == DownloadTaskState.Failed, "Truncated response was accepted.");
    Check(!File.Exists(session.DestinationPath), "Failure published a partial file.");
    await session.StartAsync();
    fixture.AssertCompleted(session);
    Check(fixture.Server.Offsets.Any(offset => offset > 0), "Retry lost partial progress.");
});

await Test("automatic retries are bounded and recover transient failures", async fixture =>
{
    fixture.Server.DropsRemaining = 1;
    var recover = fixture.Create("recover");
    await recover.StartAsync();
    fixture.AssertCompleted(recover);
    fixture.Server.StatusCode = 503;
    int before = fixture.Resolutions;
    var fail = fixture.Create("fail");
    await fail.StartAsync();
    Check(fail.Snapshot.State == DownloadTaskState.Failed, "Permanent outage did not finish.");
    Check(fixture.Resolutions - before == 3, $"Retry budget was not exactly three attempts: {fixture.Resolutions - before}, {fail.Snapshot}");
    await fail.CancelAsync();
});

await Test("expired URL is refreshed on automatic retry", async fixture =>
{
    fixture.Server.StatusCode = 403;
    fixture.OnResolve = count => { if (count == 2) fixture.Server.StatusCode = 200; };
    var session = fixture.Create();
    await session.StartAsync();
    fixture.AssertCompleted(session);
    Check(session.Snapshot.Attempt == 2, "Expired link was not retried.");
});

await Test("changed source restarts safely", async fixture =>
{
    var session = fixture.Create();
    var running = session.StartAsync();
    await Until(() => session.Snapshot.ReceivedBytes > 32768);
    await session.PauseAsync();
    await running;
    fixture.Version = "v2";
    fixture.Server.Bytes = fixture.Server.Bytes.Select(value => (byte)(value ^ 0x5a)).ToArray();
    await session.StartAsync();
    fixture.AssertCompleted(session);
    Check(session.Snapshot.Restarted, "Changed source was not reported.");
});

foreach (string mode in new[] { "no-range", "ignore-range", "bad-range", "416" })
    await Test($"safe recovery: {mode}", async fixture =>
    {
        var session = fixture.Create();
        var running = session.StartAsync();
        await Until(() => session.Snapshot.ReceivedBytes > 32768);
        await session.PauseAsync();
        await running;
        fixture.Server.SupportsRange = mode != "no-range";
        fixture.Server.IgnoreRange = mode == "ignore-range";
        fixture.Server.BadContentRange = mode == "bad-range";
        fixture.Server.RejectRange = mode == "416";
        await session.StartAsync();
        fixture.AssertCompleted(session);
    });

await Test("cancellation preserves destination and cleans only owned files", async fixture =>
{
    var session = fixture.Create();
    await File.WriteAllTextAsync(session.DestinationPath, "existing file");
    string unrelated = Path.Combine(fixture.DirectoryPath, "unrelated.download");
    await File.WriteAllTextAsync(unrelated, "keep");
    var running = session.StartAsync();
    await Until(() => session.Snapshot.ReceivedBytes > 32768);
    await Task.WhenAll(session.CancelAsync(), session.CancelAsync(), running);
    Check(session.Snapshot.State == DownloadTaskState.Cancelled, "Cancellation did not finish.");
    Check(await File.ReadAllTextAsync(session.DestinationPath) == "existing file", "Cancellation changed destination.");
    Check(File.Exists(unrelated), "Cancellation deleted another task's file.");
    Check(!Directory.EnumerateFiles(fixture.DirectoryPath, ".cloudflow-*").Any(), "Cancellation leaked staging files.");
});

await Test("cancel during metadata lookup and retry delay", async fixture =>
{
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var session = new DownloadSession(Path.Combine(fixture.DirectoryPath, "metadata"), async token =>
    {
        entered.TrySetResult();
        await Task.Delay(Timeout.Infinite, token);
        return null;
    });
    var running = session.StartAsync();
    await entered.Task;
    await session.CancelAsync();
    await running;
    Check(session.Snapshot.State == DownloadTaskState.Cancelled, "Metadata cancellation failed.");
    fixture.Server.StatusCode = 503;
    session = fixture.Create("retry-delay", retryDelay: TimeSpan.FromSeconds(5));
    running = session.StartAsync();
    await Until(() => session.Snapshot.State == DownloadTaskState.Retrying);
    await session.CancelAsync();
    await running;
    Check(session.Snapshot.State == DownloadTaskState.Cancelled, "Retry delay cancellation failed.");
});

await Test("destination exclusion and lock failure retry without redownload", async fixture =>
{
    var session = fixture.Create();
    var running = session.StartAsync();
    await Until(() => session.Snapshot.ReceivedBytes > 32768);
    var duplicate = fixture.Create();
    await duplicate.StartAsync();
    Check(duplicate.Snapshot.Failure == DownloadFailure.DestinationBusy, "Same destination was not excluded.");
    await duplicate.CancelAsync();
    await session.CancelAsync();
    await running;

    session = fixture.Create();
    await File.WriteAllTextAsync(session.DestinationPath, "original");
    using (var locked = new FileStream(session.DestinationPath, FileMode.Open, FileAccess.Read, FileShare.None))
    {
        await session.StartAsync();
        Check(session.Snapshot.State == DownloadTaskState.Failed && session.Snapshot.Failure == DownloadFailure.LocalStorage,
            "Save failure was reported as completed.");
    }
    Check(await File.ReadAllTextAsync(session.DestinationPath) == "original", "Save failure damaged original.");
    int downloads = fixture.Server.DownloadRequests;
    await session.StartAsync();
    fixture.AssertCompleted(session);
    Check(downloads == fixture.Server.DownloadRequests, "Saving retry downloaded the file again.");
});

await Test("checksum mismatch and source change during download cannot complete", async fixture =>
{
    fixture.BadHash = true;
    var session = fixture.Create("hash");
    await session.StartAsync();
    Check(session.Snapshot.Failure == DownloadFailure.InvalidResponse, "Checksum mismatch was accepted.");
    await session.CancelAsync();
    fixture.BadHash = false;
    fixture.OnResolve = _ => fixture.Version = Guid.NewGuid().ToString();
    session = fixture.Create("version");
    await session.StartAsync();
    Check(session.Snapshot.Failure == DownloadFailure.SourceChanged, "Changed source was accepted.");
    Check(!File.Exists(session.DestinationPath), "Changed source was published.");
    await session.CancelAsync();
});

await Test("view model terminal state ignores queued events; both languages resolve", async fixture =>
{
    var provider = new SourceProvider { Resolve = _ => Task.FromResult(new DownloadSource(
        fixture.Server.Url, fixture.Server.Bytes.Length, "v1")) };
    var viewModel = new DownloadTaskViewModel(new DriveViewModel(provider), "item",
        new Windows.Storage.StorageFile(Path.Combine(fixture.DirectoryPath, "vm")));
    services.Manager.DownloadTasks.Add(viewModel);
    await viewModel.StartDownload();
    Check(viewModel.Completed && viewModel.Progress == 100, "View model did not complete.");
    DispatcherQueue.Drain();
    Check(viewModel.Completed && viewModel.Progress == 100 && viewModel.DownloadSpeed == 0,
        "Queued progress overwrote completion.");
    Check(!viewModel.PauseDownloadCommand.CanExecute(null) && !viewModel.ResumeDownloadCommand.CanExecute(null) &&
        !viewModel.RetryDownloadCommand.CanExecute(null), "Completed task commands remain enabled.");
    await viewModel.CancelTaskAsync();
    Check(!services.Manager.DownloadTasks.Contains(viewModel) && File.Exists(viewModel.DestinationPath),
        "Removing completed record damaged file or retained task.");

    provider.Resolve = _ => throw new DownloadFailureException(DownloadFailure.AccessDenied);
    viewModel = new DownloadTaskViewModel(new DriveViewModel(provider), "item",
        new Windows.Storage.StorageFile(Path.Combine(fixture.DirectoryPath, "failed-vm")));
    services.Manager.DownloadTasks.Add(viewModel);
    ResourceHelper.Language = "zh-CN";
    await viewModel.StartDownload();
    DispatcherQueue.Drain();
    Check(viewModel.HasFailed && viewModel.RetryDownloadCommand.CanExecute(null) &&
        viewModel.ErrorMessage.Contains("权限"), "Failed task has no localized retry action.");
    provider.Resolve = _ => Task.FromResult(new DownloadSource(fixture.Server.Url, fixture.Server.Bytes.Length, "v1"));
    await services.Manager.StartAllDownloadTasks();
    DispatcherQueue.Drain();
    Check(viewModel.Completed, "Batch start did not retry failed task.");
    await viewModel.CancelTaskAsync();

    foreach (string language in new[] { "en-US", "zh-CN" })
    {
        ResourceHelper.Language = language;
        foreach (var state in Enum.GetValues<DownloadTaskState>()) Check(!string.IsNullOrWhiteSpace($"DownloadState_{state}".GetLocalized()), "Missing state text.");
        foreach (var error in Enum.GetValues<DownloadFailure>().Where(error => error != DownloadFailure.None))
            Check(!string.IsNullOrWhiteSpace($"DownloadError_{error}".GetLocalized()), "Missing error text.");
    }
});

await Test("pause during metadata and cancellation at completion boundary", async fixture =>
{
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    bool wait = true;
    var session = new DownloadSession(Path.Combine(fixture.DirectoryPath, "metadata-pause"), async token =>
    {
        if (wait)
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        }
        return new DownloadSource(fixture.Server.Url, fixture.Server.Bytes.Length, "v1");
    });
    var running = session.StartAsync();
    await entered.Task;
    await session.PauseAsync();
    await running;
    Check(session.Snapshot.State == DownloadTaskState.Paused, "Metadata preparation could not pause.");
    wait = false;
    Task cancellation = null;
    session.Changed += snapshot =>
    {
        if (snapshot.State == DownloadTaskState.Finalizing) cancellation = session.CancelAsync();
    };
    await session.StartAsync();
    if (cancellation != null) await cancellation;
    fixture.AssertCompleted(session);
});

await Test("stalled body times out with finite retries and can be retried", async fixture =>
{
    fixture.Server.StallBody = true;
    var session = fixture.Create(maxAttempts: 2);
    await session.StartAsync();
    Check(session.Snapshot.State == DownloadTaskState.Failed && session.Snapshot.Failure == DownloadFailure.Network &&
        session.Snapshot.Attempt == 2, "Body timeout did not respect the retry budget.");
    fixture.Server.StallBody = false;
    await session.StartAsync();
    fixture.AssertCompleted(session);
});

await Test("overlong chunked bodies stop at the metadata byte budget", async fixture =>
{
    fixture.Server.OmitLength = true;
    using var client = new HttpClient(new DownloadResponseHandler(1024));
    using var stream = await client.GetStreamAsync(fixture.Server.Url);
    using var destination = new MemoryStream();
    try { await stream.CopyToAsync(destination); throw new Exception("Overlong response was accepted"); }
    catch (InvalidDataException) { Check(destination.Length <= 1024, "Excess response bytes reached the destination"); }
});
Console.WriteLine("All download regression checks passed.");

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

static async Task Until(Func<bool> predicate)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    while (!predicate()) await Task.Delay(5, timeout.Token);
}

static async Task Test(string name, Func<Fixture, Task> test)
{
    await using var fixture = new Fixture();
    await test(fixture).WaitAsync(TimeSpan.FromSeconds(30));
    Console.WriteLine($"PASS {name}");
}

internal sealed class Fixture : IAsyncDisposable
{
    public LoopbackServer Server { get; } = new();
    public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "CloudFlow.DownloadRegression", Guid.NewGuid().ToString("N"));
    public string Version = "v1";
    public string UrlSuffix = "";
    public int Resolutions;
    public bool BadHash;
    public Action<int> OnResolve;

    public Fixture() => Directory.CreateDirectory(DirectoryPath);

    public DownloadSession Create(string name = "target", int maxAttempts = 3, TimeSpan? retryDelay = null) =>
        new(Path.Combine(DirectoryPath, name), token =>
        {
            token.ThrowIfCancellationRequested();
            int count = Interlocked.Increment(ref Resolutions);
            OnResolve?.Invoke(count);
            return Task.FromResult(new DownloadSource(Server.Url + UrlSuffix, Server.Bytes.Length, Version,
                BadHash ? new string('0', 64) : Convert.ToHexString(SHA256.HashData(Server.Bytes))));
        }, new DownloadSessionOptions
        {
            MaxAttempts = maxAttempts,
            BlockTimeoutMilliseconds = 1000,
            RetryDelay = retryDelay ?? TimeSpan.FromMilliseconds(20)
        });

    public void AssertCompleted(DownloadSession session)
    {
        if (session.Snapshot.State != DownloadTaskState.Completed)
            throw new Exception($"Expected completion: {session.Snapshot}");
        if (!File.ReadAllBytes(session.DestinationPath).SequenceEqual(Server.Bytes))
            throw new Exception("Downloaded bytes differ from the source.");
    }

    public async ValueTask DisposeAsync()
    {
        await Server.DisposeAsync();
        Directory.Delete(DirectoryPath, true);
    }
}
