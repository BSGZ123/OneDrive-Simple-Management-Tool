using OneDrive_Simple_Management_Tool.Models;
using OneDrive_Simple_Management_Tool.Services;
using OneDrive_Simple_Management_Tool.ViewModels;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

int passed = 0;
await Check("local book identity is separate from cloud identities and source is leased read-only", async () =>
{
    using var f = new Fixture();
    using var book = await ReaderLocalBook.OpenAsync(f.Book, default);
    Assert(book.Identity.Kind == "local" && book.Identity.AccountId == null && ReaderProtocol.IsHash(book.ContentVersion));
    await Throws<IOException>(() => Task.Run(() => { using var _ = File.Open(f.Book, FileMode.Open, FileAccess.Write, FileShare.ReadWrite); }));
    using var stream = book.OpenRead(); Assert(stream.Length == book.Length);
});
await Check("invalid signature, wrong extension and file over 100 MB fail before WebView creation", async () =>
{
    using var f = new Fixture();
    await File.WriteAllTextAsync(f.Book, "invalid");
    await Throws<ReaderException>(() => ReaderLocalBook.OpenAsync(f.Book, default));
    await Throws<ReaderException>(() => ReaderLocalBook.OpenAsync(f.Book + ".txt", default));
    using (var stream = File.Create(f.Book)) stream.SetLength(100_000_001);
    await Throws<ReaderException>(() => ReaderLocalBook.OpenAsync(f.Book, default));
});
await Check("bridge rejects foreign source, old session, duplicate fields, large and invalid JSON", () =>
{
    string id = new('a', 32), url = ReaderProtocol.PageUrl(id);
    string json = JsonSerializer.Serialize(new { version = 1, sessionId = id, requestId = (string)null, type = "ready", payload = new { } });
    Assert(ReaderProtocol.Parse(url, json, id) != null);
    foreach (var source in new[] { url + "?x", "blob:" + url, "https://reader.invalid.evil/reader/index.html", url.Replace(id, new string('b', 32)) })
        Assert(ReaderProtocol.Parse(source, json, id) == null);
    foreach (var bad in new[] { "{", json.Replace("\"version\":1", "\"version\":1,\"version\":1"), json.Replace(id, new string('b', 32)), new string('x', 32769) })
        Assert(ReaderProtocol.Parse(url, bad, id) == null);
    return Task.CompletedTask;
});
await Check("location, settings, frame URLs and external links have bounded schemas", () =>
{
    Assert(ReaderProtocol.ValidLocation(new("epubcfi(/6/2!/4/2)", 0.2)));
    Assert(!ReaderProtocol.ValidLocation(new("invalid", 0.2)) && !ReaderProtocol.ValidLocation(new(null, double.NaN)));
    Assert(!ReaderProtocol.ValidSettings(new() { FontSize = 100 }) && !ReaderProtocol.ValidSettings(new() { Theme = "url(evil)" }));
    Assert(ReaderProtocol.FrameUri("blob:https://reader.invalid/" + Guid.NewGuid()));
    Assert(!ReaderProtocol.FrameUri("blob:https://reader.invalid.evil/" + Guid.NewGuid()));
    foreach (string url in new[] { "file:///secret", "javascript:alert(1)", "https://user:password@example.com" }) Assert(!ReaderProtocol.ExternalUri(url, out _));
    Assert(ReaderProtocol.ExternalUri("https://example.com/", out _));
    return Task.CompletedTask;
});
await Check("native resource allowlist and CSP match tested browser policy", async () =>
{
    using var f = new Fixture(); using var book = await ReaderLocalBook.OpenAsync(f.Book, default);
    string session = Guid.NewGuid().ToString("N");
    using var provider = new ReaderResourceProvider(book, session);
    await provider.PrepareAsync(Environment.CurrentDirectory, default);
    var asset = await provider.ResolveAsync(ReaderProtocol.Origin + "/reader/index.html", "GET", default);
    Assert(asset.Status == 200 && asset.Mime == "text/html" && ReaderResourceProvider.Headers(asset).Contains("nosniff"));
    var response = await provider.ResolveAsync(ReaderProtocol.Origin + "/book/" + session + ".epub", "GET", default);
    Assert(response.Status == 200 && response.Length == book.Length && response.Content is FileStream);
    foreach (string suffix in new[] { "/reader/../appsettings.json", "/reader/%2e%2e/appsettings.json", "/reader/index.html?x", "/book/stale.epub" })
        Assert((await provider.ResolveAsync(ReaderProtocol.Origin + suffix, "GET", default)).Status == 403);
    Assert((await provider.ResolveAsync(ReaderProtocol.Origin + "/reader/index.html", "POST", default)).Status == 403);
    Assert((await provider.ResolveAsync("https://example.com/", "GET", default)).Status == 403);
    string policy = await File.ReadAllTextAsync("Tests/ReaderWeb/policy.mjs");
    foreach (string directive in ReaderResourceProvider.ContentSecurityPolicy.Split("; ")) Assert(policy.Contains('"' + directive + '"'));
    foreach (string resource in ReaderResourceProvider.Assets) Assert(policy.Contains("'" + resource + "'"));
    provider.Dispose(); Assert(!response.Content.CanRead);
});
await Check("real DPAPI progress and settings survive new store instances without plaintext", async () =>
{
    using var f = new Fixture(); var progress = f.Progress();
    await f.Store.SaveProgressAsync(progress, default);
    await f.Store.SaveSettingsAsync(new(new() { FontSize = 28 }, DateTimeOffset.UtcNow), default);
    var reopened = new ReaderStore(f.Paths);
    Assert(await reopened.LoadProgressAsync(progress.Identity, default) == progress);
    Assert((await reopened.LoadSettingsAsync(default)).FontSize == 28);
    string disk = await File.ReadAllTextAsync(f.Paths.ReaderProgress);
    Assert(!disk.Contains(progress.Identity.ItemId) && !disk.Contains("epubcfi"));
});
await Check("late progress cannot replace newer position or another identity", async () =>
{
    using var f = new Fixture(); var first = f.Progress();
    var newer = first with { Location = new(null, 0.8), UpdatedAtUtc = first.UpdatedAtUtc.AddSeconds(1) };
    await f.Store.SaveProgressAsync(newer, default); await f.Store.SaveProgressAsync(first, default);
    var other = first with { Identity = new("onedrive", "account", "drive", "item") };
    await f.Store.SaveProgressAsync(other, default);
    Assert(await f.Store.LoadProgressAsync(first.Identity, default) == newer);
    Assert(await f.Store.LoadProgressAsync(other.Identity, default) == other);
});
await Check("corrupt files remain untouched until explicit backup recovery or rebuild", async () =>
{
    using var f = new Fixture(); var first = f.Progress();
    await f.Store.SaveProgressAsync(first, default);
    await f.Store.SaveProgressAsync(first with { UpdatedAtUtc = first.UpdatedAtUtc.AddSeconds(1) }, default);
    await File.WriteAllTextAsync(f.Paths.ReaderProgress, "damaged");
    await Throws<ConfigurationException>(() => f.Store.SaveProgressAsync(first, default));
    Assert(await File.ReadAllTextAsync(f.Paths.ReaderProgress) == "damaged");
    await f.Store.RecoverAsync(true, default);
    Assert(await f.Store.LoadProgressAsync(first.Identity, default) == first);
    Assert(Directory.GetFiles(Path.GetDirectoryName(f.Paths.ReaderProgress), "*.recovery-*").Length == 1);
    await File.WriteAllTextAsync(f.Paths.ReaderProgress, "damaged-again");
    await f.Store.RecoverAsync(false, default);
    Assert(await f.Store.LoadProgressAsync(first.Identity, default) == null);
});
await Check("session requires ready, correlated opened and complete bounded TOC", async () =>
{
    using var f = new Fixture(); var host = new FakeHost(); var session = new ReaderSession(f.Store, () => host);
    await session.OpenAsync(new(f.Book));
    Assert(session.State == ReaderState.Ready && session.Toc.Count == 1 && session.Location.Fraction == 0.2);
    await session.CloseAsync(); await session.CloseAsync(); Assert(host.Disposals == 1 && session.State == ReaderState.Closed);
});
await Check("wrong request IDs, hostile links and post-close messages cannot change native state", async () =>
{
    using var f = new Fixture(); var host = new FakeHost(); var session = new ReaderSession(f.Store, () => host);
    int links = 0, back = 0; session.ExternalLinkRequested += _ => links++; session.BackRequested += () => back++;
    await session.OpenAsync(new(f.Book));
    host.Emit("locationChanged", new { cfi = "epubcfi(/6/2!/4/2)", fraction = 0.9 }, "wrong-request-id-0000");
    host.Emit("externalLinkRequested", new { url = "javascript:evil()" });
    Assert(session.Location.Fraction == 0.2 && links == 0);
    host.Emit("externalLinkRequested", new { url = "https://example.com" }); Assert(links == 1);
    await session.CloseAsync();
    host.Emit("locationChanged", new { fraction = 0.9 }); host.Emit("hostCommand", new { command = "back" });
    Assert(session.Location.Fraction == 0.2 && back == 0);
});
await Check("same version restores CFI; changed bytes restore fraction only", async () =>
{
    using var f = new Fixture();
    ReaderIdentity identity; string version;
    using (var book = await ReaderLocalBook.OpenAsync(f.Book, default)) { identity = book.Identity; version = book.ContentVersion; }
    await f.Store.SaveProgressAsync(new(identity, version, new("epubcfi(/6/2!/4/2)", 0.6), DateTimeOffset.UtcNow), default);
    var host = new FakeHost(); var session = new ReaderSession(f.Store, () => host);
    await session.OpenAsync(new(f.Book)); Assert(host.Restore?.Cfi != null); await session.CloseAsync();
    await File.AppendAllTextAsync(f.Book, "updated");
    host = new(); session = new(f.Store, () => host);
    await session.OpenAsync(new(f.Book)); Assert(host.Restore.Cfi == null && host.Restore.Fraction == 0.6);
    Assert(session.NoticeKey == "Reader_Approximate"); await session.CloseAsync();
});
await Check("close during native initialization cancels attempt and releases source lease", async () =>
{
    using var f = new Fixture(); var host = new FakeHost { Pause = true }; var session = new ReaderSession(f.Store, () => host);
    Task open = session.OpenAsync(new(f.Book)); await host.Initializing.Task.WaitAsync(TimeSpan.FromSeconds(3));
    await session.CloseAsync().WaitAsync(TimeSpan.FromSeconds(3)); await open;
    Assert(session.State == ReaderState.Closed && host.Disposals == 1);
    using var exclusive = File.Open(f.Book, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
});
await Check("native crash stops attempt; explicit retry creates a new session and host", async () =>
{
    using var f = new Fixture(); var hosts = new List<FakeHost>(); var session = new ReaderSession(f.Store, () => { var h = new FakeHost(); hosts.Add(h); return h; });
    await session.OpenAsync(new(f.Book)); string id = session.SessionId;
    hosts[0].Crash(); await Until(() => session.State == ReaderState.Failed && hosts[0].Disposals == 1);
    await session.RetryAsync(); Assert(session.State == ReaderState.Ready && hosts.Count == 2 && id != session.SessionId);
    await session.CloseAsync();
});
await Check("invalid TOC and malformed opened payload fail instead of publishing Ready", async () =>
{
    foreach (string invalid in new[] { "toc", "opened" })
    {
        using var f = new Fixture(); var host = new FakeHost { Invalid = invalid }; var session = new ReaderSession(f.Store, () => host);
        await session.OpenAsync(new(f.Book)); Assert(session.State == ReaderState.Failed && session.ErrorCode == "InvalidMessage"); await session.CloseAsync();
    }
});
await Check("save failure preserves reading and retries last accepted position", async () =>
{
    using var f = new Fixture(); var store = new FailingStore(f.Store); var host = new FakeHost(); var session = new ReaderSession(store, () => host);
    await session.OpenAsync(new(f.Book)); await session.FlushAsync(); Assert(session.SaveFailed && session.State == ReaderState.Ready);
    store.Fail = false; await session.FlushAsync(); Assert(!session.SaveFailed);
    await session.CloseAsync();
});
await Check("reader localization keys match in both languages and ViewModel resolves status", async () =>
{
    var languages = new[] { "en-US", "zh-CN" }.Select(lang => XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Strings", lang, "Resources.resw"))
        .Root.Elements("data").Where(x => ((string)x.Attribute("name")).StartsWith("Reader_")).ToDictionary(x => (string)x.Attribute("name"), x => x.Element("value").Value)).ToArray();
    Assert(languages[0].Keys.Order().SequenceEqual(languages[1].Keys.Order()) && languages.All(x => x.Values.All(v => !string.IsNullOrWhiteSpace(v))));
    using var f = new Fixture(); var vm = new ReaderViewModel(new(f.Store, () => new FakeHost()));
    Assert(!string.IsNullOrWhiteSpace(vm.Status)); await vm.Session.OpenAsync(new(f.Book)); Assert(vm.IsReady); await vm.Session.CloseAsync();
});
await Check("closing has a budget even when a storage boundary ignores cancellation", async () =>
{
    using var f = new Fixture(); var store = new DelayedStore(f.Store); var session = new ReaderSession(store, () => new FakeHost());
    await session.OpenAsync(new(f.Book));
    await session.CloseAsync().WaitAsync(TimeSpan.FromSeconds(3));
    Assert(session.State == ReaderState.Closed && session.SaveFailed);
    store.Release.TrySetResult(); await store.Finished.Task.WaitAsync(TimeSpan.FromSeconds(3));
});
await Check("workspace tracks a reopened page and closes it when the app exits", async () =>
{
    using var f = new Fixture(); var workspace = new ReaderWorkspace(f.Paths);
    var session = workspace.CreateSession(() => new FakeHost());
    await workspace.OpenAsync(session, new(f.Book)); await session.CloseAsync();
    Assert(!workspace.HasOpenSessions);
    await workspace.OpenAsync(session, new(f.Book)); Assert(workspace.HasOpenSessions);
    await workspace.CloseAllAsync(); Assert(session.State == ReaderState.Closed && !workspace.HasOpenSessions);
});
await CacheChecks.RunAsync(Check);
Console.WriteLine($"Passed {passed} reader regression checks.");

async Task Check(string name, Func<Task> action) { await action(); passed++; Console.WriteLine("PASS " + name); }
static void Assert(bool value) { if (!value) throw new InvalidOperationException("Assertion failed"); }
static async Task Throws<T>(Func<Task> operation) where T : Exception { try { await operation(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
static async Task Until(Func<bool> predicate) { using var timeout = new CancellationTokenSource(3000); while (!predicate()) await Task.Delay(10, timeout.Token); }

sealed class Fixture : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ReaderRegression-" + Guid.NewGuid().ToString("N"));
    public ApplicationDataPaths Paths { get; }
    public ReaderStore Store { get; }
    public string Book => Path.Combine(_root, "sample.epub");
    public Fixture() { Directory.CreateDirectory(_root); File.WriteAllBytes(Book, [80, 75, 3, 4, 0, 1, 2]); Paths = new(_root); Store = new(Paths); }
    public ReaderProgress Progress() => new(new("local", null, null, new string('A', 64)), "content-v1", new("epubcfi(/6/2!/4/2)", 0.4), DateTimeOffset.UtcNow);
    public void Dispose()
    {
        string root = Path.GetFullPath(_root), temp = Path.GetFullPath(Path.GetTempPath());
        if (!root.StartsWith(Path.Combine(temp, "ReaderRegression-"), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
        Directory.Delete(root, true);
    }
}
sealed class FakeHost : IReaderHost
{
    public event Action<string, string> MessageReceived;
    public event Action<string> Failed;
    private string _session;
    public bool Pause;
    public string Invalid;
    public int Disposals;
    public ReaderLocation Restore;
    public TaskCompletionSource Initializing = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public async Task InitializeAsync(ReaderLocalBook book, string session, CancellationToken token)
    {
        _session = session; Initializing.TrySetResult();
        if (Pause) await Task.Delay(Timeout.Infinite, token);
        Emit("ready", new { });
    }
    public void Emit(string type, object payload, string request = null) => MessageReceived?.Invoke(ReaderProtocol.PageUrl(_session),
        JsonSerializer.Serialize(new { version = 1, sessionId = _session, requestId = request, type, payload }));
    public void Send(string json)
    {
        using var doc = JsonDocument.Parse(json); var root = doc.RootElement;
        string type = root.GetProperty("type").GetString(), id = root.GetProperty("requestId").GetString();
        if (type == "openBook")
        {
            Restore = root.GetProperty("payload").GetProperty("location").Deserialize<ReaderLocation>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Emit("tocChunk", new { offset = 0, total = 1, nodes = new[] { new { id = Invalid == "toc" ? "toc-9" : "toc-0", parentId = (string)null, label = "Chapter" } } }, id);
            Emit("opened", new { title = Invalid == "opened" ? new string('x', 513) : "Sample", fixedLayout = false, direction = "ltr", restoredBy = "cfi",
                elapsedMs = 1, entries = 1, declaredTotal = 100, compressedBytes = 100,
                location = new { cfi = Restore?.Cfi ?? "epubcfi(/6/2!/4/2)", fraction = Restore?.Fraction ?? 0.2 } }, id);
        }
        Emit("commandCompleted", new { command = type }, id);
    }
    public void Crash() => Failed?.Invoke("ProcessFailed");
    public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
}
sealed class FailingStore(IReaderStore inner) : IReaderStore
{
    public bool Fail = true;
    public Task<ReaderProgress> LoadProgressAsync(ReaderIdentity identity, CancellationToken token) => inner.LoadProgressAsync(identity, token);
    public Task<ReaderSettings> LoadSettingsAsync(CancellationToken token) => inner.LoadSettingsAsync(token);
    public Task SaveProgressAsync(ReaderProgress progress, CancellationToken token) => Fail ? Task.FromException(new IOException()) : inner.SaveProgressAsync(progress, token);
    public Task SaveSettingsAsync(ReaderPreferences preferences, CancellationToken token) => inner.SaveSettingsAsync(preferences, token);
    public Task RecoverAsync(bool backup, CancellationToken token) => inner.RecoverAsync(backup, token);
}
sealed class DelayedStore(IReaderStore inner) : IReaderStore
{
    public TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<ReaderProgress> LoadProgressAsync(ReaderIdentity identity, CancellationToken token) => inner.LoadProgressAsync(identity, token);
    public Task<ReaderSettings> LoadSettingsAsync(CancellationToken token) => inner.LoadSettingsAsync(token);
    public async Task SaveProgressAsync(ReaderProgress progress, CancellationToken token)
    {
        await Release.Task;
        try { await inner.SaveProgressAsync(progress, default); }
        finally { Finished.TrySetResult(); }
    }
    public Task SaveSettingsAsync(ReaderPreferences preferences, CancellationToken token) => inner.SaveSettingsAsync(preferences, token);
    public Task RecoverAsync(bool backup, CancellationToken token) => inner.RecoverAsync(backup, token);
}
