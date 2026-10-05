using OneDrive_Simple_Management_Tool.Models;
using OneDrive_Simple_Management_Tool.Services;
using System.Net;
using System.Net.Sockets;
using System.Text;

internal static class CacheChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        await check("cloud cache rechecks metadata and never persists download URLs", async () =>
        {
            using var f = new Fixture(); await using var server = new BookServer(); var cache = new ReaderCacheService(f.Paths);
            int resolves = 0;
            var request = Request(server, resolve: () => resolves++);
            using (var first = await cache.OpenAsync(request, null, default)) Assert(first.Identity == request.Identity && first.ContentVersion == "ctag:v1");
            int downloads = server.Gets, metadata = resolves;
            using var second = await new ReaderCacheService(f.Paths).OpenAsync(request, null, default);
            Assert(server.Gets == downloads && resolves > metadata);
            string disk = await File.ReadAllTextAsync(f.Paths.ReaderCacheIndex);
            Assert(!disk.Contains(server.Url) && !disk.Contains("account") && !disk.Contains("ctag:"));
        });
        await check("denied, missing and offline metadata never return a cached book", async () =>
        {
            using var f = new Fixture(); await using var server = new BookServer(); var cache = new ReaderCacheService(f.Paths);
            var request = Request(server);
            (await cache.OpenAsync(request, null, default)).Dispose(); int downloads = server.Gets;
            foreach (string error in new[] { "AccessDenied", "NotFound", "Network" })
                await Refused(() => cache.OpenAsync(request with { ResolveSource = _ => throw new ReaderException(error) }, null, default), error);
            Assert(server.Gets == downloads && Directory.GetFiles(f.Paths.ReaderCache, "*.epub").Length == 1);
        });
        await check("cloud identities separate account, drive and item even with identical bytes", async () =>
        {
            using var f = new Fixture(); await using var server = new BookServer(); var cache = new ReaderCacheService(f.Paths);
            var request = Request(server);
            foreach (var identity in new[] { request.Identity, request.Identity with { AccountId = "other" }, request.Identity with { DriveId = "other" }, request.Identity with { ItemId = "other" } })
            {
                using var book = await cache.OpenAsync(request with { Identity = identity }, null, default);
                Assert(book.Identity == identity);
            }
            Assert(Directory.GetFiles(f.Paths.ReaderCache, "*.epub").Length == 4);
        });
        await check("version changes during transfer preserve the complete old cache", async () =>
        {
            using var f = new Fixture(); await using var server = new BookServer(); var cache = new ReaderCacheService(f.Paths);
            var request = Request(server); (await cache.OpenAsync(request, null, default)).Dispose();
            string old = Directory.GetFiles(f.Paths.ReaderCache, "*.epub").Single(); byte[] bytes = await File.ReadAllBytesAsync(old);
            int calls = 0;
            var changing = request with { ResolveSource = _ => Task.FromResult(new DownloadSource(server.Url, server.Bytes.Length, ++calls >= 3 ? "ctag:v3" : "ctag:v2")) };
            await Refused(() => cache.OpenAsync(changing, null, default), "SourceChanged");
            Assert((await File.ReadAllBytesAsync(old)).SequenceEqual(bytes) && Directory.GetFiles(f.Paths.ReaderCache, "*.epub").Length == 1);
            Assert(!Directory.GetFiles(f.Paths.ReaderCache).Any(path => path.Contains(".tmp")));
        });
        await check("cache tampering triggers a verified new download", async () =>
        {
            using var f = new Fixture(); await using var server = new BookServer(); var cache = new ReaderCacheService(f.Paths);
            var request = Request(server); (await cache.OpenAsync(request, null, default)).Dispose();
            string old = Directory.GetFiles(f.Paths.ReaderCache, "*.epub").Single();
            var damaged = server.Bytes.ToArray(); damaged[^1] ^= 1; await File.WriteAllBytesAsync(old, damaged);
            int downloads = server.Gets; using var book = await cache.OpenAsync(request, null, default);
            Assert(server.Gets > downloads);
            using var stream = book.OpenRead(); using var memory = new MemoryStream(); await stream.CopyToAsync(memory);
            Assert(memory.ToArray().SequenceEqual(server.Bytes));
        });
        await check("cache clear respects active file leases and preserves progress and unrelated files", async () =>
        {
            using var f = new Fixture(); await using var server = new BookServer(); var cache = new ReaderCacheService(f.Paths);
            var progress = f.Progress(); await f.Store.SaveProgressAsync(progress, default);
            var book = await cache.OpenAsync(Request(server), null, default);
            string unrelated = Path.Combine(f.Paths.ReaderCache, "user-note.txt"); await File.WriteAllTextAsync(unrelated, "keep");
            Assert(await cache.ClearAsync() == 1); book.Dispose(); Assert(await cache.ClearAsync() == 0);
            Assert(await f.Store.LoadProgressAsync(progress.Identity, default) == progress && File.Exists(unrelated));
            Assert(Directory.GetFiles(f.Paths.ReaderCache, "*.epub").Length == 0);
        });
        await check("LRU eviction stays within budget and pinned files cause a controlled failure", async () =>
        {
            using var f = new Fixture(); await using var server = new BookServer();
            var cache = new ReaderCacheService(f.Paths, server.Bytes.Length * 2);
            var request = Request(server); using var first = await cache.OpenAsync(request, null, default);
            var secondRequest = request with { Identity = request.Identity with { ItemId = "second" } };
            var second = await cache.OpenAsync(secondRequest, null, default);
            var third = request with { Identity = request.Identity with { ItemId = "third" } };
            await Refused(() => cache.OpenAsync(third, null, default), "CacheStorage"); second.Dispose();
            using var last = await cache.OpenAsync(third, null, default);
            Assert(Directory.GetFiles(f.Paths.ReaderCache, "*.epub").Sum(path => new FileInfo(path).Length) <= server.Bytes.Length * 2);
        });
        await check("cancelled cache transfer cleans partial files before another operation", async () =>
        {
            using var f = new Fixture(); await using var server = new BookServer { HoldBody = true }; var cache = new ReaderCacheService(f.Paths);
            using var cancellation = new CancellationTokenSource();
            Task opening = cache.OpenAsync(Request(server), null, cancellation.Token);
            await server.Requested.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel(); server.Release.TrySetResult();
            try { await opening.WaitAsync(TimeSpan.FromSeconds(5)); throw new InvalidOperationException("Cancellation was ignored"); } catch (OperationCanceledException) { }
            Assert(Directory.GetFiles(f.Paths.ReaderCache).Length == 0);
        });
        await check("damaged cache index requires explicit clear, without modifying reader progress", async () =>
        {
            using var f = new Fixture(); await using var server = new BookServer(); var cache = new ReaderCacheService(f.Paths);
            var request = Request(server); (await cache.OpenAsync(request, null, default)).Dispose();
            await File.WriteAllTextAsync(f.Paths.ReaderCacheIndex, "damaged");
            try { await cache.OpenAsync(request, null, default); throw new InvalidOperationException("Corruption accepted"); } catch (ConfigurationException) { }
            Assert(await File.ReadAllTextAsync(f.Paths.ReaderCacheIndex) == "damaged");
            await cache.ClearAsync(); using var restored = await cache.OpenAsync(request, null, default);
            Assert(restored.Identity == request.Identity);
        });
        await check("insufficient capacity for a new version keeps the previous complete copy", async () =>
        {
            using var f = new Fixture(); await using var server = new BookServer(); var cache = new ReaderCacheService(f.Paths, server.Bytes.Length);
            var request = Request(server); (await cache.OpenAsync(request, null, default)).Dispose();
            string original = Directory.GetFiles(f.Paths.ReaderCache, "*.epub").Single();
            var changed = request with { ResolveSource = _ => Task.FromResult(new DownloadSource(server.Url, server.Bytes.Length, "ctag:v2")) };
            await Refused(() => cache.OpenAsync(changed, null, default), "CacheStorage");
            Assert(File.Exists(original) && (await File.ReadAllBytesAsync(original)).SequenceEqual(server.Bytes));
        });
        await check("closing the reader during a cloud download returns promptly and suppresses a late host", async () =>
        {
            using var f = new Fixture(); await using var server = new BookServer { HoldBody = true };
            var cache = new ReaderCacheService(f.Paths); int hosts = 0;
            var session = new ReaderSession(f.Store, () => { hosts++; return new FakeHost(); }, cache.OpenAsync);
            var opening = session.OpenAsync(Request(server)); await server.Requested.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await session.CloseAsync().WaitAsync(TimeSpan.FromSeconds(3)); server.Release.TrySetResult(); await opening.WaitAsync(TimeSpan.FromSeconds(5));
            Assert(session.State == ReaderState.Closed && hosts == 0 && Directory.GetFiles(f.Paths.ReaderCache).Length == 0);
        });
    }
    private static ReaderOpenRequest Request(BookServer server, Action resolve = null) => new(Identity: new("onedrive", "account", "drive", "item"),
        ResolveSource: _ => { resolve?.Invoke(); return Task.FromResult(new DownloadSource(server.Url, server.Bytes.Length, "ctag:v1")); });
    private static void Assert(bool value) { if (!value) throw new InvalidOperationException("Cache assertion failed"); }
    private static async Task Refused(Func<Task<ReaderLocalBook>> call, string code)
    {
        try { (await call()).Dispose(); throw new InvalidOperationException("Unexpected cache success"); }
        catch (ReaderException error) { Assert(error.Code == code); }
    }
}

internal sealed class BookServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private readonly List<Task> _connections = new();
    public byte[] Bytes { get; } = [80, 75, 3, 4, 0, 1, 2];
    public int Gets;
    public bool HoldBody;
    public TaskCompletionSource Requested = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public string Url { get; }
    public BookServer()
    {
        _listener.Start(); Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/book";
        _loop = AcceptAsync();
    }
    private async Task AcceptAsync()
    {
        try { while (!_stop.IsCancellationRequested) _connections.Add(ServeAsync(await _listener.AcceptTcpClientAsync(_stop.Token))); }
        catch (OperationCanceledException) { }
    }
    private async Task ServeAsync(TcpClient client)
    {
        using (client) try
        {
            using var stream = client.GetStream(); using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
            string request = await reader.ReadLineAsync(_stop.Token); string header;
            do { header = await reader.ReadLineAsync(_stop.Token); } while (!string.IsNullOrEmpty(header));
            bool head = request.StartsWith("HEAD ", StringComparison.Ordinal);
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {Bytes.Length}\r\nContent-Type: application/epub+zip\r\nConnection: close\r\n\r\n"), _stop.Token);
            if (!head)
            {
                Interlocked.Increment(ref Gets); Requested.TrySetResult();
                if (HoldBody) await Release.Task.WaitAsync(_stop.Token);
                await stream.WriteAsync(Bytes, _stop.Token);
            }
        }
        catch (Exception error) when (error is IOException or OperationCanceledException) { }
    }
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel(); _listener.Stop(); await _loop; await Task.WhenAll(_connections); _stop.Dispose();
    }
}
